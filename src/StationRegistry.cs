using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmolCraft
{
    internal enum StationCategory
    {
        None,
        SmeltersAndKilns,
        CraftingStations,
        StationUpgrades,
        Production,
        Custom
    }

    internal class StationInfo
    {
        public string PrefabName;
        public string DisplayName;
        public StationCategory Category;
        public GameObject Prefab;
        public Vector3 OriginalScale;

        /// <summary>Where each smoke emitter sits relative to the station root, at vanilla size.</summary>
        public Vector3[] SmokeOffsets;

        /// <summary>Null for server-only stations (sized by the server, no local slider).</summary>
        public ConfigEntry<float> Scale;
        public float MinScale;

        /// <summary>Added by a player through the "Extra Station Prefabs" list.</summary>
        public bool IsCustom;

        /// <summary>Only known because the server sent a size for it (the server's custom list).</summary>
        public bool IsServerOnly;

        public bool HasSmoke => SmokeOffsets != null && SmokeOffsets.Length > 0;
    }

    internal static class StationRegistry
    {
        // Lowest size each section allows. Smoke stations get a higher floor because below ~40% the
        // ore/fuel/output openings get fiddly to aim at even with the smoke fix on.
        private const float SmelterMinScale = 0.4f;
        private const float OtherMinScale = 0.3f;

        private const float DefaultShrunk = 0.7f;
        private const float DefaultVanilla = 1.0f;

        /// <summary>
        /// Anything whose prefab name or piece token contains one of these defaults to 70%.
        /// Matching by keyword (instead of exact prefab names) means the Deep North stations are
        /// caught even though we can't see their internal names from here.
        /// Covers: Smelter ("Furnace"), Blast Furnace, Charcoal Kiln, Eitr Refinery,
        /// Frost Foundry, Frigid Kiln.
        /// </summary>
        private static readonly string[] DefaultShrinkKeywords =
        {
            "smelter", "blastfurnace", "blast_furnace", "kiln", "eitrrefinery", "eitr_refinery",
            "refinery", "foundry", "frigid"
        };

        private static readonly Dictionary<string, StationInfo> Stations =
            new Dictionary<string, StationInfo>(StringComparer.Ordinal);

        private static readonly HashSet<string> UsedDisplayNames = new HashSet<string>();

        internal static bool IsManaged(string prefabName) =>
            prefabName != null && Stations.ContainsKey(prefabName);

        /// <summary>Stations whose size this machine decides (i.e. not server-only ones).</summary>
        internal static IEnumerable<StationInfo> Local => Stations.Values.Where(s => s.Scale != null);

        // ------------------------------------------------------------------ discovery

        internal static void OnZNetSceneAwake(ZNetScene scene)
        {
            if (scene == null || scene.m_prefabs == null) return;

            int added = 0;
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;

                if (Stations.TryGetValue(prefab.name, out var existing))
                {
                    existing.Prefab = prefab; // same prefab on a later world load
                    continue;
                }

                var piece = prefab.GetComponent<Piece>();
                if (piece == null) continue;

                var category = Classify(prefab, piece);
                if (category == StationCategory.None) continue;

                bool shrink = MatchesShrinkKeyword(IdOf(prefab, piece));
                Register(prefab, piece, category, shrink ? DefaultShrunk : DefaultVanilla, custom: false, serverOnly: false);
                added++;
            }

            if (added > 0)
                SmolCraftPlugin.Log.LogInfo($"Found {added} new station(s) automatically.");

            SyncCustomStations();
            EnsureServerStations();

            if (SmolCraftPlugin.LogPieceNames.Value) LogBuildablePieces();

            SmokePatches.ClearCache();
            ApplyAll();
        }

        private static string IdOf(GameObject prefab, Piece piece) =>
            (prefab.name + " " + (piece != null ? piece.m_name ?? "" : "")).ToLowerInvariant();

        private static bool MatchesShrinkKeyword(string text)
        {
            foreach (var k in DefaultShrinkKeywords)
                if (text.Contains(k)) return true;
            return false;
        }

        private static StationCategory Classify(GameObject prefab, Piece piece)
        {
            if (MatchesShrinkKeyword(IdOf(prefab, piece)) || prefab.GetComponent<Smelter>() != null)
                return StationCategory.SmeltersAndKilns;
            if (prefab.GetComponent<CraftingStation>() != null)
                return StationCategory.CraftingStations;
            if (prefab.GetComponent<StationExtension>() != null)
                return StationCategory.StationUpgrades;
            if (prefab.GetComponent<CookingStation>() != null || prefab.GetComponent<Fermenter>() != null)
                return StationCategory.Production;

            return StationCategory.None;
        }

        private static string SectionName(StationCategory c)
        {
            switch (c)
            {
                case StationCategory.SmeltersAndKilns: return "2 - Smelters, Kilns & Refineries";
                case StationCategory.CraftingStations: return "3 - Crafting Stations";
                case StationCategory.StationUpgrades: return "4 - Station Upgrades";
                case StationCategory.Production: return "5 - Cooking & Fermenting";
                case StationCategory.Custom: return SmolCraftPlugin.CustomSection;
                default: return "9 - Other";
            }
        }

        private static System.Reflection.PropertyInfo _locInstance;
        private static System.Reflection.MethodInfo _localize;

        private static string Localized(Piece piece, string fallback)
        {
            if (piece == null) return fallback;
            string token = piece.m_name;
            if (string.IsNullOrEmpty(token)) return fallback;
            try
            {
                // Localization lives in assembly_guiutils.dll; looked up by name so we don't need
                // another DLL reference (and it can't break the build if Iron Gate moves it again).
                if (_localize == null)
                {
                    var locType = AccessTools.TypeByName("Localization");
                    _locInstance = locType == null ? null : AccessTools.Property(locType, "instance");
                    _localize = locType == null ? null : AccessTools.Method(locType, "Localize", new[] { typeof(string) });
                }

                object loc = _locInstance?.GetValue(null, null);
                if (loc != null && _localize != null)
                {
                    string text = _localize.Invoke(loc, new object[] { token }) as string;
                    if (!string.IsNullOrEmpty(text) && !text.StartsWith("$") && !text.StartsWith("["))
                        return text;
                }
            }
            catch
            {
                // dedicated servers may not have localization ready; fall through
            }
            return fallback;
        }

        private static string SafeKey(string s)
        {
            foreach (char bad in new[] { '=', '\n', '\t', '\\', '"', '\'', '[', ']' })
                s = s.Replace(bad, '_');
            return s;
        }

        private static StationInfo Register(GameObject prefab, Piece piece, StationCategory category,
            float defaultScale, bool custom, bool serverOnly)
        {
            var root = prefab.transform;
            var spawners = prefab.GetComponentsInChildren<SmokeSpawner>(true);

            var info = new StationInfo
            {
                PrefabName = prefab.name,
                Category = category,
                Prefab = prefab,
                OriginalScale = root.localScale,
                SmokeOffsets = spawners.Select(s => root.InverseTransformPoint(s.transform.position)).ToArray(),
                IsCustom = custom,
                IsServerOnly = serverOnly
            };

            bool smokeFloor = category == StationCategory.SmeltersAndKilns ||
                              (category == StationCategory.Custom && info.HasSmoke);
            info.MinScale = smokeFloor ? SmelterMinScale : OtherMinScale;

            string display = Localized(piece, prefab.name);
            if (!UsedDisplayNames.Add(display)) display = $"{display} ({prefab.name})";
            info.DisplayName = display;

            if (!serverOnly)
            {
                string desc = $"{display} [{prefab.name}] — size multiplier. 1.0 = vanilla, " +
                              $"{info.MinScale:0.0} = smallest allowed.";
                if (info.HasSmoke)
                    desc += " This station makes smoke: if it keeps stopping indoors, leave " +
                            "'Keep Smoke Exit At Vanilla Height' on or enable 'Smokeless Stations'.";

                info.Scale = SmolCraftPlugin.Cfg.Bind(
                    SectionName(category),
                    SafeKey(prefab.name),
                    Mathf.Clamp(defaultScale, info.MinScale, 1f),
                    new ConfigDescription(desc,
                        new AcceptableValueRange<float>(info.MinScale, 1f),
                        new ConfigurationManagerAttributes { DispName = display, ShowRangeAsPercent = true }));

                info.Scale.SettingChanged += SmolCraftPlugin.Instance.OnAnySettingChanged;
            }

            Stations[prefab.name] = info;
            return info;
        }

        // ------------------------------------------------------------------ custom list

        /// <summary>
        /// Parses "Extra Station Prefabs". Entries are separated by commas, semicolons or new lines.
        /// Each is a prefab name, optionally followed by ":size" as the slider's starting value,
        /// e.g. "piece_bigbench:0.6, MyModForge".
        /// </summary>
        private static Dictionary<string, float> ParseCustomList()
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            string raw = SmolCraftPlugin.CustomStations.Value ?? "";

            foreach (string part in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = part.Trim();
                if (entry.Length == 0) continue;

                float start = DefaultVanilla;
                int colon = entry.IndexOf(':');
                if (colon >= 0)
                {
                    string num = entry.Substring(colon + 1).Trim().TrimEnd('%');
                    entry = entry.Substring(0, colon).Trim();
                    if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                        start = parsed > 1.5f ? parsed / 100f : parsed; // accept "60" or "60%" too
                }

                if (entry.Length > 0) result[entry] = start;
            }
            return result;
        }

        private static GameObject FindPrefab(ZNetScene scene, string name)
        {
            if (scene?.m_prefabs == null) return null;
            foreach (var p in scene.m_prefabs)
                if (p != null && p.name == name) return p;
            return null;
        }

        /// <summary>
        /// Adds sliders for newly listed prefabs and removes sliders (restoring vanilla size)
        /// for prefabs taken off the list.
        /// </summary>
        internal static void SyncCustomStations()
        {
            var wanted = ParseCustomList();

            foreach (var info in Stations.Values.Where(i => i.IsCustom && !wanted.ContainsKey(i.PrefabName)).ToList())
            {
                Forget(info, removeConfig: true);
                SmolCraftPlugin.Log.LogInfo($"Removed custom station '{info.PrefabName}' (back to vanilla size).");
            }

            var scene = ZNetScene.instance;
            if (scene == null) return; // will run again when a world loads

            foreach (var kv in wanted)
            {
                if (Stations.TryGetValue(kv.Key, out var existing))
                {
                    if (existing.IsServerOnly)
                        Forget(existing, removeConfig: false); // take it over with a local slider
                    else
                    {
                        if (!existing.IsCustom)
                            SmolCraftPlugin.Log.LogInfo(
                                $"'{kv.Key}' is already handled automatically — its slider is in \"{SectionName(existing.Category)}\".");
                        continue;
                    }
                }

                var prefab = FindPrefab(scene, kv.Key);
                if (prefab == null)
                {
                    SmolCraftPlugin.Log.LogWarning(
                        $"Custom station '{kv.Key}' not found. Prefab names are case-sensitive — turn on " +
                        "'Log Buildable Piece Names' to see the exact names.");
                    continue;
                }

                Register(prefab, prefab.GetComponent<Piece>(), StationCategory.Custom, kv.Value, custom: true, serverOnly: false);
                SmolCraftPlugin.Log.LogInfo($"Added custom station '{kv.Key}'.");
            }

            SmokePatches.ClearCache();
        }

        /// <summary>
        /// When the server sends sizes for stations this player has no slider for (e.g. the server's
        /// own custom list), size them anyway so everyone matches.
        /// </summary>
        internal static void EnsureServerStations()
        {
            var scene = ZNetScene.instance;
            if (scene == null || !ConfigSync.HasServerConfig) return;

            foreach (string name in ConfigSync.ServerStationNames)
            {
                if (Stations.ContainsKey(name)) continue;
                var prefab = FindPrefab(scene, name);
                if (prefab == null) continue; // player doesn't have the mod that adds it
                Register(prefab, prefab.GetComponent<Piece>(), StationCategory.Custom, DefaultVanilla, custom: false, serverOnly: true);
            }
            SmokePatches.ClearCache();
        }

        internal static void ForgetServerOnly()
        {
            foreach (var info in Stations.Values.Where(i => i.IsServerOnly).ToList())
                Forget(info, removeConfig: false);
        }

        private static void Forget(StationInfo info, bool removeConfig)
        {
            ResetToVanilla(info);
            Stations.Remove(info.PrefabName);
            UsedDisplayNames.Remove(info.DisplayName);

            if (removeConfig && info.Scale != null)
            {
                info.Scale.SettingChanged -= SmolCraftPlugin.Instance.OnAnySettingChanged;
                SmolCraftPlugin.Cfg.Remove(info.Scale.Definition);
                SmolCraftPlugin.Cfg.Save();
            }
            SmokePatches.ClearCache();
        }

        private static void ResetToVanilla(StationInfo info)
        {
            if (info.Prefab != null) ApplyToRoot(info.Prefab.transform, info, 1f);
            ForEachPlaced((nview, i) => { if (i == info) ApplyToRoot(nview.transform, info, 1f); });
        }

        // ------------------------------------------------------------------ helper: find names

        internal static void LogBuildablePieces()
        {
            var scene = ZNetScene.instance;
            if (scene?.m_prefabs == null)
            {
                SmolCraftPlugin.Log.LogInfo("Load into a world to list piece names.");
                return;
            }

            var lines = new List<string>();
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                var piece = prefab.GetComponent<Piece>();
                if (piece == null) continue;

                string status = Stations.TryGetValue(prefab.name, out var info)
                    ? $"managed → {SectionName(info.Category)}"
                    : "not managed";
                lines.Add($"  {prefab.name,-40} {Localized(piece, "?"),-30} {status}");
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            SmolCraftPlugin.Log.LogInfo(
                $"Buildable pieces ({lines.Count}). Copy the first column into 'Extra Station Prefabs':\n" +
                string.Join("\n", lines));
        }

        // ------------------------------------------------------------------ applying sizes

        internal static float EffectiveScale(StationInfo info)
        {
            if (!ConfigSync.EffectiveEnabled) return 1f;

            float v;
            if (ConfigSync.TryGetServerScale(info.PrefabName, out float server)) v = server;
            else if (info.Scale != null) v = info.Scale.Value;
            else v = 1f;

            return Mathf.Clamp(v, info.MinScale, 1f);
        }

        /// <summary>Rescale every station prefab, then every station already built in the loaded area.</summary>
        internal static void ApplyAll()
        {
            if (Stations.Count == 0) return;

            var scales = new Dictionary<StationInfo, float>(Stations.Count);
            foreach (var info in Stations.Values)
            {
                float s = EffectiveScale(info);
                scales[info] = s;
                if (info.Prefab != null) ApplyToRoot(info.Prefab.transform, info, s);
            }

            int count = 0;
            ForEachPlaced((nview, info) =>
            {
                ApplyToRoot(nview.transform, info, scales[info]);
                count++;
            });

            if (count > 0) SmolCraftPlugin.Log.LogDebug($"Rescaled {count} placed station(s).");
        }

        private static void ForEachPlaced(Action<ZNetView, StationInfo> action)
        {
            if (ZNetScene.instance == null) return;

            foreach (var nview in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
            {
                if (nview == null) continue;
                string name = PrefabNameOf(nview.gameObject);
                if (name != null && Stations.TryGetValue(name, out var info))
                    action(nview, info);
            }
        }

        private static void ApplyToRoot(Transform root, StationInfo info, float s)
        {
            root.localScale = info.OriginalScale * s;

            if (!info.HasSmoke) return;

            // Move smoke emitters. With the fix on, the emitter keeps its sideways position (so it is
            // still over the chimney) but its height is divided by the scale, which lands it back at
            // the vanilla height in world space — i.e. just above the top of the shrunk chimney.
            bool raise = ConfigSync.EffectiveRaiseSmokeExit;
            var spawners = root.GetComponentsInChildren<SmokeSpawner>(true);
            int n = Math.Min(spawners.Length, info.SmokeOffsets.Length);
            for (int i = 0; i < n; i++)
            {
                var t = spawners[i].transform;
                if (t == root) continue; // never move the whole station

                Vector3 p0 = info.SmokeOffsets[i];
                Vector3 local = raise && s > 0.01f ? new Vector3(p0.x, p0.y / s, p0.z) : p0;
                t.position = root.TransformPoint(local);
            }
        }

        internal static string PrefabNameOf(GameObject go)
        {
            if (go == null) return null;
            string n = go.name;
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i >= 0 ? n.Substring(0, i).Trim() : n;
        }
    }
}
