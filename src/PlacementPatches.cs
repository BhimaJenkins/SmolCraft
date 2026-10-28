using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SmolCraft
{
    /// <summary>
    /// "No Upgrade Spacing": station upgrades (Forge Bellows, Grinding Wheel, Tanning Rack, anvils…)
    /// can be placed right next to each other.
    ///
    /// Vanilla gives every upgrade a "space requirement" radius and refuses placement ("needs more
    /// space") if another upgrade is inside it. That check is StationExtension.OtherExtensionInRange.
    /// We make it always answer "no other upgrade nearby".
    ///
    /// Everything else stays vanilla: the ghost still has to sit on something solid (the game only
    /// shows it where your aim hits a surface), the upgrade still has to be in range of its station,
    /// and wards / no-build zones still apply.
    ///
    /// Backup: if a Valheim update renames that method, a second hook clears the "needs more space"
    /// status for upgrades directly.
    /// </summary>
    internal static class PlacementPatches
    {
        internal static bool MainHookActive;

        private static readonly FieldInfo GhostField = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static readonly FieldInfo StatusField = AccessTools.Field(typeof(Player), "m_placementStatus");
        private static readonly MethodInfo SetGhostValid = AccessTools.Method(typeof(Player), "SetPlacementGhostValid");
        private static object _moreSpace, _valid;
        private static bool _statusLooked;

        // Prefix on StationExtension.OtherExtensionInRange — "is another upgrade too close?"
        internal static bool OtherExtensionInRange_Prefix(ref bool __result)
        {
            if (!ConfigSync.EffectiveNoUpgradeSpacing) return true;
            __result = false;
            return false;
        }

        // Backup postfix on Player.UpdatePlacementGhost.
        internal static void UpdatePlacementGhost_Postfix(Player __instance)
        {
            if (MainHookActive || !ConfigSync.EffectiveNoUpgradeSpacing) return;
            if (GhostField == null || StatusField == null) return;

            try
            {
                if (!_statusLooked)
                {
                    _statusLooked = true;
                    var t = StatusField.FieldType;
                    if (Enum.IsDefined(t, "MoreSpace")) _moreSpace = Enum.Parse(t, "MoreSpace");
                    if (Enum.IsDefined(t, "Valid")) _valid = Enum.Parse(t, "Valid");
                }
                if (_moreSpace == null || _valid == null) return;

                var ghost = GhostField.GetValue(__instance) as GameObject;
                if (ghost == null || ghost.GetComponent<StationExtension>() == null) return;
                if (!Equals(StatusField.GetValue(__instance), _moreSpace)) return;

                StatusField.SetValue(__instance, _valid);
                SetGhostValid?.Invoke(__instance, new object[] { true });
            }
            catch (Exception ex)
            {
                SmolCraftPlugin.Log.LogDebug($"Upgrade spacing backup hook error: {ex.Message}");
            }
        }
    }
}
