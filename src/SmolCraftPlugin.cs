using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SmolCraft
{
    /// <summary>
    /// SmolCraft — shrinks smelters, kilns, refineries and other crafting stations.
    ///
    /// How it works, in one paragraph:
    /// When a world loads, ZNetScene builds its list of every spawnable prefab. We walk that list,
    /// find every buildable station, create a config slider for it, and scale the PREFAB itself.
    /// Because every station in the world (and every placement ghost) is cloned from that prefab,
    /// they all come out small automatically. When a slider changes we also rescale the stations
    /// already standing in the world. In multiplayer the server sends its values to every client
    /// so everyone sees (and collides with) the same sizes.
    /// </summary>
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class SmolCraftPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "BhimaJenkins.SmolCraft";
        public const string ModName = "SmolCraft";
        public const string ModVersion = "0.2.0";

        internal static SmolCraftPlugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<bool> Smokeless;
        internal static ConfigEntry<bool> RaiseSmokeExit;
        internal static ConfigEntry<bool> EnforceServerConfig;
        internal static ConfigEntry<bool> NoUpgradeSpacing;
        internal static ConfigEntry<string> CustomStations;
        internal static ConfigEntry<bool> LogPieceNames;

        internal const string CustomSection = "6 - Custom Stations";

        internal static ConfigFile Cfg => Instance.Config;

        private Harmony _harmony;
        private FileSystemWatcher _watcher;
        private volatile bool _reloadRequested;
        private bool _applyRequested;
        private bool _customListDirty;
        private float _applyTimer;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            const string general = "1 - General";

            ModEnabled = Config.Bind(general, "Enabled", true,
                new ConfigDescription(
                    "Master switch. Turn off to put every station back to vanilla size.",
                    null,
                    new ConfigurationManagerAttributes { Order = 40 }));

            Smokeless = Config.Bind(general, "Smokeless Stations", false,
                new ConfigDescription(
                    "Stations managed by SmolCraft stop producing smoke and can never be 'blocked' by smoke. " +
                    "Use this if a shrunk station keeps going out indoors. Only affects SmolCraft stations " +
                    "(campfires, hearths, braziers etc. are untouched).",
                    null,
                    new ConfigurationManagerAttributes { Order = 30 }));

            RaiseSmokeExit = Config.Bind(general, "Keep Smoke Exit At Vanilla Height", true,
                new ConfigDescription(
                    "Shrinking a smelter also shrinks its chimney, and smoke puffs are a fixed size, so they " +
                    "jam inside a small flue and the station stops ('needs ventilation'). With this on, the " +
                    "point where smoke comes out stays at its vanilla height so puffs spawn above the shrunk " +
                    "chimney and float away normally. Turn off for a purist look (smoke from the tiny chimney).",
                    null,
                    new ConfigurationManagerAttributes { Order = 20 }));

            NoUpgradeSpacing = Config.Bind(general, "No Upgrade Spacing", true,
                new ConfigDescription(
                    "Station upgrades (Forge Bellows, Grinding Wheel, Tanning Rack, anvils, etc.) can be placed " +
                    "right next to each other — no more 'needs more space'. They still have to be on a solid " +
                    "surface and within range of their station. Works independently of the size settings.",
                    null,
                    new ConfigurationManagerAttributes { Order = 15 }));

            EnforceServerConfig = Config.Bind(general, "Enforce Server Settings", true,
                new ConfigDescription(
                    "SERVER ONLY. When on, the server sends its SmolCraft settings to every player that joins " +
                    "and overrides theirs, so everyone sees the same sizes. Ignored on clients.",
                    null,
                    new ConfigurationManagerAttributes { Order = 10 }));

            CustomStations = Config.Bind(CustomSection, "Extra Station Prefabs", "",
                new ConfigDescription(
                    "Add any piece SmolCraft didn't pick up on its own (modded or vanilla). Comma-separated " +
                    "prefab names; each one gets its own size slider in this section. Optionally add a " +
                    "starting size after a colon, e.g.  piece_bigbench:0.6, MyModForge:65%  . " +
                    "Names are case-sensitive — use 'Log Buildable Piece Names' to find them. " +
                    "Removing a name puts that piece back to vanilla size.",
                    null,
                    new ConfigurationManagerAttributes { Order = 100 }));

            LogPieceNames = Config.Bind(CustomSection, "Log Buildable Piece Names", false,
                new ConfigDescription(
                    "When on (in a world), writes every buildable piece's prefab name, in-game name and " +
                    "whether SmolCraft manages it to the BepInEx log (LogOutput.log / console). " +
                    "Turn on, check the log, turn off.",
                    null,
                    new ConfigurationManagerAttributes { Order = 90 }));

            CustomStations.SettingChanged += (_, __) =>
            {
                _customListDirty = true;
                RequestApply(1.0f); // wait for the player to finish typing
            };
            LogPieceNames.SettingChanged += (_, __) =>
            {
                if (LogPieceNames.Value) StationRegistry.LogBuildablePieces();
            };

            ModEnabled.SettingChanged += OnAnySettingChanged;
            Smokeless.SettingChanged += OnAnySettingChanged;
            RaiseSmokeExit.SettingChanged += OnAnySettingChanged;
            EnforceServerConfig.SettingChanged += OnAnySettingChanged;
            NoUpgradeSpacing.SettingChanged += (_, __) => ConfigSync.BroadcastIfServer();

            _harmony = new Harmony(ModGuid);
            Patches.ApplyAll(_harmony);

            SetupConfigWatcher();

            Log.LogInfo($"{ModName} {ModVersion} loaded.");
        }

        internal void OnAnySettingChanged(object sender, EventArgs e)
        {
            SmokePatches.ClearCache();
            RequestApply();
        }

        /// <summary>Debounced so dragging a slider doesn't rescale the world 60 times a second.</summary>
        internal void RequestApply(float delay = 0.25f)
        {
            _applyRequested = true;
            _applyTimer = Math.Max(_applyTimer, delay);
        }

        private void Update()
        {
            if (_reloadRequested)
            {
                _reloadRequested = false;
                try
                {
                    Config.Reload();
                    Log.LogInfo("Config file changed on disk, reloaded.");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Could not reload config: {ex.Message}");
                }
                RequestApply();
            }

            if (_applyRequested)
            {
                _applyTimer -= Time.unscaledDeltaTime;
                if (_applyTimer <= 0f)
                {
                    _applyRequested = false;
                    _applyTimer = 0f;
                    if (_customListDirty)
                    {
                        _customListDirty = false;
                        StationRegistry.SyncCustomStations();
                    }
                    StationRegistry.ApplyAll();
                    ConfigSync.BroadcastIfServer();
                }
            }
        }

        private void SetupConfigWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(Paths.ConfigPath, Path.GetFileName(Config.ConfigFilePath));
                _watcher.Changed += (_, __) => _reloadRequested = true;
                _watcher.Created += (_, __) => _reloadRequested = true;
                _watcher.Renamed += (_, __) => _reloadRequested = true;
                _watcher.IncludeSubdirectories = false;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Config file watcher unavailable ({ex.Message}); edits will apply on restart.");
            }
        }

        private void OnDestroy()
        {
            _watcher?.Dispose();
            _harmony?.UnpatchSelf();
        }
    }
}
