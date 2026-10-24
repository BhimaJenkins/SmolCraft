using System.Collections.Generic;

namespace SmolCraft
{
    /// <summary>
    /// Vanilla smoke rules: a station puffs smoke every ~0.5s, but only if no older puff is still
    /// sitting right on top of the emitter. If no puff has managed to spawn for ~4 seconds the
    /// SmokeSpawner reports "blocked" and smelters/kilns stop working.
    ///
    /// "Smokeless Stations" short-circuits both halves of that for SmolCraft stations only:
    /// no puffs are spawned, and IsBlocked() always answers "no".
    /// </summary>
    internal static class SmokePatches
    {
        // SmokeSpawner instance id -> does it belong to a SmolCraft station?
        private static readonly Dictionary<int, bool> OwnerCache = new Dictionary<int, bool>();

        internal static void ClearCache() => OwnerCache.Clear();

        private static bool Suppress(SmokeSpawner spawner)
        {
            if (spawner == null) return false;
            if (!ConfigSync.EffectiveEnabled || !ConfigSync.EffectiveSmokeless) return false;

            int id = spawner.GetInstanceID();
            if (OwnerCache.TryGetValue(id, out bool managed)) return managed;

            var nview = spawner.GetComponentInParent<ZNetView>();
            managed = nview != null && StationRegistry.IsManaged(StationRegistry.PrefabNameOf(nview.gameObject));
            OwnerCache[id] = managed;
            return managed;
        }

        // Return false = skip the vanilla method.
        internal static bool Spawn_Prefix(SmokeSpawner __instance)
        {
            return !Suppress(__instance);
        }

        internal static bool IsBlocked_Prefix(SmokeSpawner __instance, ref bool __result)
        {
            if (!Suppress(__instance)) return true;
            __result = false;
            return false;
        }
    }
}
