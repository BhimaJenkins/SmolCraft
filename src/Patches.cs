using System;
using HarmonyLib;

namespace SmolCraft
{
    /// <summary>
    /// All Harmony hooks are applied by name and checked one by one, so if a Valheim update renames
    /// a method, SmolCraft logs a clear warning and keeps the rest working instead of crashing.
    /// </summary>
    internal static class Patches
    {
        internal static void ApplyAll(Harmony harmony)
        {
            // Station discovery + prefab scaling. Priority.Last so stations added by other mods
            // (Jotunn etc.) are already in the list when we look.
            TryPatch(harmony, typeof(ZNetScene), "Awake",
                postfix: Hook(typeof(Patches), nameof(ZNetScene_Awake_Postfix), Priority.Last));

            // Multiplayer config sync.
            TryPatch(harmony, typeof(ZNet), "OnNewConnection",
                postfix: Hook(typeof(ConfigSync), nameof(ConfigSync.OnNewConnection_Postfix)));
            TryPatch(harmony, typeof(ZNet), "RPC_PeerInfo",
                postfix: Hook(typeof(ConfigSync), nameof(ConfigSync.RPC_PeerInfo_Postfix)));
            TryPatch(harmony, typeof(ZNet), "OnDestroy",
                postfix: Hook(typeof(ConfigSync), nameof(ConfigSync.ZNet_OnDestroy_Postfix)));

            // No spacing rule between station upgrades.
            PlacementPatches.MainHookActive = TryPatch(harmony, typeof(StationExtension), "OtherExtensionInRange",
                prefix: Hook(typeof(PlacementPatches), nameof(PlacementPatches.OtherExtensionInRange_Prefix)));
            TryPatch(harmony, typeof(Player), "UpdatePlacementGhost",
                postfix: Hook(typeof(PlacementPatches), nameof(PlacementPatches.UpdatePlacementGhost_Postfix)));

            // Smoke handling.
            TryPatch(harmony, typeof(SmokeSpawner), "Spawn",
                prefix: Hook(typeof(SmokePatches), nameof(SmokePatches.Spawn_Prefix)));
            TryPatch(harmony, typeof(SmokeSpawner), "IsBlocked",
                prefix: Hook(typeof(SmokePatches), nameof(SmokePatches.IsBlocked_Prefix)));
        }

        private static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            try
            {
                StationRegistry.OnZNetSceneAwake(__instance);
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogError($"Station discovery failed: {ex}");
            }
        }

        private static HarmonyMethod Hook(Type host, string name, int priority = -1)
        {
            var method = AccessTools.Method(host, name);
            var hm = new HarmonyMethod(method);
            if (priority >= 0) hm.priority = priority;
            return hm;
        }

        private static bool TryPatch(Harmony harmony, Type type, string methodName,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            var target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                SmolCraftPlugin.Log.LogWarning(
                    $"Could not find {type.Name}.{methodName} — this part of SmolCraft is disabled " +
                    "(a Valheim update probably renamed it).");
                return false;
            }

            try
            {
                harmony.Patch(target, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogError($"Failed to patch {type.Name}.{methodName}: {ex.Message}");
                return false;
            }
        }
    }
}
