using System;
using System.Collections.Generic;

namespace SmolCraft
{
    /// <summary>
    /// Minimal server → client settings sync (no ServerSync/Jotunn dependency).
    ///
    /// 1. Every connection (both sides) registers our RPC as soon as it opens.
    /// 2. When the server receives a joining player's peer info, it sends its settings.
    /// 3. Whenever the server's settings change (Config Manager on a host, or editing the .cfg on a
    ///    dedicated server), it re-sends to everyone.
    /// 4. A client that receives settings uses them instead of its own until it disconnects.
    ///
    /// If the server doesn't run SmolCraft, nothing is ever received and clients use their own config.
    /// </summary>
    internal static class ConfigSync
    {
        private const string RpcName = "SmolCraft_SyncConfig";
        private const int PackageVersion = 2;

        internal static bool HasServerConfig { get; private set; }

        private static bool _srvEnabled;
        private static bool _srvSmokeless;
        private static bool _srvRaiseSmoke;
        private static bool _srvNoUpgradeSpacing;
        private static readonly Dictionary<string, float> SrvScales = new Dictionary<string, float>();

        internal static bool EffectiveEnabled =>
            HasServerConfig ? _srvEnabled : SmolCraftPlugin.ModEnabled.Value;

        internal static bool EffectiveSmokeless =>
            HasServerConfig ? _srvSmokeless : SmolCraftPlugin.Smokeless.Value;

        internal static bool EffectiveRaiseSmokeExit =>
            HasServerConfig ? _srvRaiseSmoke : SmolCraftPlugin.RaiseSmokeExit.Value;

        internal static bool EffectiveNoUpgradeSpacing =>
            HasServerConfig ? _srvNoUpgradeSpacing : SmolCraftPlugin.NoUpgradeSpacing.Value;

        internal static IEnumerable<string> ServerStationNames => SrvScales.Keys;

        internal static bool TryGetServerScale(string prefab, out float scale)
        {
            scale = 1f;
            return HasServerConfig && SrvScales.TryGetValue(prefab, out scale);
        }

        // ------------------------------------------------------------------ Harmony hooks

        internal static void OnNewConnection_Postfix(ZNetPeer peer)
        {
            try
            {
                peer?.m_rpc?.Register<ZPackage>(RpcName, RPC_ReceiveConfig);
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogWarning($"Could not register sync RPC: {ex.Message}");
            }
        }

        internal static void RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc)
        {
            if (__instance == null || rpc == null) return;
            if (!__instance.IsServer() || !SmolCraftPlugin.EnforceServerConfig.Value) return;

            try
            {
                rpc.Invoke(RpcName, BuildPackage());
                SmolCraftPlugin.Log.LogInfo("Sent SmolCraft settings to joining player.");
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogWarning($"Could not send settings to joining player: {ex.Message}");
            }
        }

        internal static void ZNet_OnDestroy_Postfix()
        {
            if (!HasServerConfig) return;
            HasServerConfig = false;
            SrvScales.Clear();
            StationRegistry.ForgetServerOnly();
            SmokePatches.ClearCache();
            StationRegistry.ApplyAll(); // back to this player's own settings for the next world
            SmolCraftPlugin.Log.LogInfo("Left server; using local SmolCraft settings again.");
        }

        // ------------------------------------------------------------------ send / receive

        internal static void BroadcastIfServer()
        {
            var znet = ZNet.instance;
            if (znet == null || !znet.IsServer() || !SmolCraftPlugin.EnforceServerConfig.Value) return;

            int sent = 0;
            foreach (var peer in znet.GetPeers())
            {
                if (peer?.m_rpc == null) continue;
                try
                {
                    peer.m_rpc.Invoke(RpcName, BuildPackage()); // fresh package per peer
                    sent++;
                }
                catch (Exception ex)
                {
                    SmolCraftPlugin.Log.LogWarning($"Sync to a peer failed: {ex.Message}");
                }
            }

            if (sent > 0) SmolCraftPlugin.Log.LogInfo($"Pushed updated SmolCraft settings to {sent} player(s).");
        }

        private static ZPackage BuildPackage()
        {
            var pkg = new ZPackage();
            pkg.Write(PackageVersion);
            pkg.Write(SmolCraftPlugin.ModEnabled.Value);
            pkg.Write(SmolCraftPlugin.Smokeless.Value);
            pkg.Write(SmolCraftPlugin.RaiseSmokeExit.Value);
            pkg.Write(SmolCraftPlugin.NoUpgradeSpacing.Value);

            var list = new List<StationInfo>(StationRegistry.Local);
            pkg.Write(list.Count);
            foreach (var info in list)
            {
                pkg.Write(info.PrefabName);
                pkg.Write(info.Scale.Value);
            }
            return pkg;
        }

        private static void RPC_ReceiveConfig(ZRpc rpc, ZPackage pkg)
        {
            // The server never takes settings from anyone.
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            try
            {
                int version = pkg.ReadInt();
                if (version != PackageVersion)
                {
                    SmolCraftPlugin.Log.LogWarning(
                        $"Server SmolCraft settings format v{version} (expected v{PackageVersion}). " +
                        "Update SmolCraft so it matches the server. Using local settings.");
                    return;
                }

                _srvEnabled = pkg.ReadBool();
                _srvSmokeless = pkg.ReadBool();
                _srvRaiseSmoke = pkg.ReadBool();
                _srvNoUpgradeSpacing = pkg.ReadBool();

                SrvScales.Clear();
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    string name = pkg.ReadString();
                    float scale = pkg.ReadSingle();
                    SrvScales[name] = scale;
                }

                HasServerConfig = true;
                StationRegistry.EnsureServerStations();
                SmokePatches.ClearCache();
                StationRegistry.ApplyAll();
                SmolCraftPlugin.Log.LogInfo(
                    $"Using server SmolCraft settings ({count} stations). Local slider changes are ignored while connected.");
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogWarning($"Bad SmolCraft settings package from server: {ex.Message}");
            }
        }
    }
}
