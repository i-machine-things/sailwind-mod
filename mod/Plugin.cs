using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SailwindRealEarth
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.allan.sailwind.realearth";
        public const string PluginName = "Sailwind Real Earth";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        // Updated by GlobeCoordsLogPatch every time the game itself calls
        // FloatingOriginManager.GetGlobeCoords on the player, so the overlay always
        // shows a fresh value without us having to guess which object to query.
        internal static bool HasCoords;
        internal static float LastLon;
        internal static float LastLat;
        internal static string LastObserved;

        // M1 rehearsal only: an arbitrary fixed shift, not the final real-world mapping.
        // Moves the vanilla archipelago's main cluster (centered roughly on lon -0.6, lat
        // 34.7, excluding the Chronos outlier) onto the Caribbean (~18 deg N, 65 deg W),
        // purely to prove the reposition-by-real-degrees technique works end to end.
        internal const float ReposLonDeltaDeg = -64.4f;
        internal const float ReposLatDeltaDeg = -16.7f;
        internal const float UnitsPerDegree = 9000f;
        internal static readonly Vector3 ReposOffset =
            new Vector3(ReposLonDeltaDeg * UnitsPerDegree, 0f, ReposLatDeltaDeg * UnitsPerDegree);

        internal static ConfigEntry<bool> RepositionEnabled;

        private Harmony harmony;
        private bool overlayVisible = true;

        private void Awake()
        {
            Log = Logger;
            RepositionEnabled = Config.Bind(
                "RealEarth",
                "RepositionIslands",
                true,
                "M1 rehearsal: shift the vanilla archipelago onto a real-world region " +
                "(Caribbean) to validate the lat/lon repositioning math. Not the final mapping.");
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo($"{PluginName} {PluginVersion} loaded - F9 toggles the lat/lon overlay");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                overlayVisible = !overlayVisible;
            }
        }

        private void OnGUI()
        {
            if (!overlayVisible || !HasCoords)
            {
                return;
            }

            const int w = 260;
            const int h = 60;
            GUI.Box(new Rect(10, 10, w, h), "");
            GUI.Label(new Rect(20, 15, w - 20, 20), "Sailwind Real Earth (F9 to hide)");
            GUI.Label(new Rect(20, 35, w - 20, 20), $"lon {LastLon:F3}  lat {LastLat:F3}  ({LastObserved})");
        }
    }

    // M0 smoke test: confirms BepInEx/Harmony can patch real game code, and that the
    // game's existing lat/lon API (FloatingOriginManager.GetGlobeCoords) behaves as the
    // BorderExpander/scrambled-seas source led us to expect: x = longitude, z = latitude.
    [HarmonyPatch(typeof(FloatingOriginManager), "GetGlobeCoords")]
    internal static class GlobeCoordsLogPatch
    {
        private static float lastLogTime = -999f;

        private static void Postfix(Transform __0, ref Vector3 __result)
        {
            Plugin.HasCoords = true;
            Plugin.LastLon = __result.x;
            Plugin.LastLat = __result.z;
            Plugin.LastObserved = __0 != null ? __0.name : "null";

            if (Time.time - lastLogTime < 2f)
            {
                return;
            }

            lastLogTime = Time.time;
            Plugin.Log.LogInfo($"[RealEarth] GetGlobeCoords({Plugin.LastObserved}) -> lon={__result.x:F3} lat={__result.z:F3}");
        }
    }

    // M1 step 1 (survey): logs every vanilla island's name, index, and real lat/lon at load
    // time, same capture point scrambled-seas used (IslandHorizon.Start). Verified in-game -
    // see data/vanilla_island_survey.json.
    //
    // M1 step 2 (reposition): translates each island by Plugin.ReposOffset (a fixed, uniform
    // world-space shift, same technique as scrambled-seas' WorldScrambler) so the whole
    // archipelago keeps its relative shape but lands on real-world coordinates. This only
    // moves IslandHorizon objects - region anchors, ocean-floor planes, ports, and boats
    // (which scrambled-seas' WorldScrambler also moves) are deliberately out of scope for
    // this rehearsal step.
    [HarmonyPatch(typeof(IslandHorizon), "Start")]
    internal static class IslandRepositionPatch
    {
        private static void Postfix(IslandHorizon __instance)
        {
            Vector3 localPos = __instance.transform.localPosition;
            Vector3 beforeGlobe = GetGlobe(__instance.transform);

            if (Plugin.RepositionEnabled != null && Plugin.RepositionEnabled.Value)
            {
                __instance.transform.Translate(Plugin.ReposOffset, Space.World);
            }

            Vector3 afterGlobe = GetGlobe(__instance.transform);

            Plugin.Log.LogInfo(
                $"[RealEarth][Reposition] '{__instance.gameObject.name}' idx={__instance.islandIndex} " +
                $"localPos={localPos} lon {beforeGlobe.x:F3}->{afterGlobe.x:F3} " +
                $"lat {beforeGlobe.z:F3}->{afterGlobe.z:F3}");
        }

        private static Vector3 GetGlobe(Transform t)
        {
            return FloatingOriginManager.instance != null
                ? FloatingOriginManager.instance.GetGlobeCoords(t)
                : new Vector3(float.NaN, float.NaN, float.NaN);
        }
    }
}
