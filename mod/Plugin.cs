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

        // M1 rehearsal only: not the final real-world mapping. The full-size shift
        // (-64.4 lon / -16.7 lat, ~580000 units) froze the game - see CODING_NOTES.md's
        // "Floating origin" section. These are now config-bound (not const) specifically so
        // we can empirically find the actual safe threshold by editing the saved .cfg and
        // relaunching, without a rebuild each step, starting small and working up.
        internal const float UnitsPerDegree = 9000f;
        internal static ConfigEntry<float> ReposLonDeltaDeg;
        internal static ConfigEntry<float> ReposLatDeltaDeg;
        internal static ConfigEntry<bool> RepositionEnabled;

        internal static Vector3 ReposOffset =>
            new Vector3(ReposLonDeltaDeg.Value * UnitsPerDegree, 0f, ReposLatDeltaDeg.Value * UnitsPerDegree);

        private Harmony harmony;
        private bool overlayVisible = true;

        private void Awake()
        {
            Log = Logger;
            RepositionEnabled = Config.Bind(
                "RealEarth",
                "RepositionIslands",
                true,
                "M1 rehearsal: shift the vanilla archipelago by RepositionLonDeltaDeg/"
                + "RepositionLatDeltaDeg to validate the lat/lon repositioning math. Confirmed "
                + "by empirical testing (see CODING_NOTES.md 'Floating origin'): safe/comfortable "
                + "up to at least -48.3/-12.5 deg, visibly degraded by -56.35/-14.6, froze outright "
                + "at the original -64.4/-16.7 Caribbean target. Default below kept well inside the "
                + "confirmed-safe range with margin - do not push toward the degraded/frozen range "
                + "without reading that section first.");
            ReposLonDeltaDeg = Config.Bind(
                "RealEarth", "RepositionLonDeltaDeg", -32.2f,
                "Longitude shift in degrees applied to every island. See RepositionIslands for the confirmed-safe range.");
            ReposLatDeltaDeg = Config.Bind(
                "RealEarth", "RepositionLatDeltaDeg", -8.35f,
                "Latitude shift in degrees applied to every island. See RepositionIslands for the confirmed-safe range.");
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
    // M1 step 2 (reposition): moves each island by Plugin.ReposOffset so the whole
    // archipelago keeps its relative shape but lands on different real-world coordinates.
    // Goes through FloatingOriginManager's ShiftingPosToRealPos/RealPosToShiftingPos round
    // trip - the same conversion BorderExpander's circumnavigation code uses for large moves
    // - rather than a raw Transform.Translate. NOTE: per decompiling FloatingOriginManager
    // itself, outCurrentOffset is still (0,0,0) at world-load time (it only becomes nonzero
    // after the game's own Shift() has fired at least once), so this round trip is
    // mathematically a no-op *right now* and is NOT expected to be what fixes the freeze by
    // itself - it's here because it's the technically correct pattern (matches how the rest
    // of the ecosystem does large moves, and stays correct if this ever runs after a shift
    // has occurred). The actual fix is finding a safe magnitude empirically via
    // RepositionLonDeltaDeg/RepositionLatDeltaDeg - see CODING_NOTES.md.
    //
    // Only moves IslandHorizon objects - region anchors, ocean-floor planes, ports, and boats
    // (which scrambled-seas' WorldScrambler also moves) are deliberately out of scope for
    // this rehearsal step.
    [HarmonyPatch(typeof(IslandHorizon), "Start")]
    internal static class IslandRepositionPatch
    {
        private static void Postfix(IslandHorizon __instance)
        {
            Vector3 localPos = __instance.transform.localPosition;
            Vector3 beforeGlobe = GetGlobe(__instance.transform);

            if (Plugin.RepositionEnabled != null && Plugin.RepositionEnabled.Value
                && FloatingOriginManager.instance != null)
            {
                FloatingOriginManager fom = FloatingOriginManager.instance;
                Vector3 realPos = fom.ShiftingPosToRealPos(__instance.transform.position);
                Vector3 targetReal = realPos + Plugin.ReposOffset;
                __instance.transform.position = fom.RealPosToShiftingPos(targetReal);
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
