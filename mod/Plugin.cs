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

        private static void Postfix(FloatingOriginManager __instance, Transform __0, ref Vector3 __result)
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
            string shifterName = __instance.shifterObject != null ? __instance.shifterObject.name : "null";
            Plugin.Log.LogInfo(
                $"[RealEarth] GetGlobeCoords({Plugin.LastObserved}) -> lon={__result.x:F3} lat={__result.z:F3} | "
                + $"outCurrentOffset={__instance.outCurrentOffset} shiftDistance={__instance.shiftDistance} "
                + $"shifterObject={shifterName} shifterPos={(__instance.shifterObject != null ? __instance.shifterObject.position : Vector3.zero)}");
        }
    }

    // Diagnostic: counts/logs every real call to the engine's own private Shift(x,z) method -
    // the thing FloatingOriginManager.Update() calls automatically, in a tight while loop, any
    // time shifterObject's raw position exceeds shiftDistance. This is what caught the actual
    // root cause of the M1 reposition freeze/crash: teleporting the player's raw position
    // ~290000 units past shiftDistance (confirmed 512 units) in one shot sent this auto-shift
    // loop into a runaway - 28000+ calls in a single burst, outCurrentOffset ballooning to 10.8
    // million (~37x past the intended offset) before reversing direction, matching the in-game
    // view rendering the world as a shrinking planet seen from outside its own skybox, ending in
    // an actual process crash (confirmed live via screenshots + the "EARLY ACCESS" splash
    // reappearing, meaning Unity restarted, not just froze). Left in place as an ongoing safety
    // net - in normal play (and with FloatingOriginOffsetPatch below, which never moves any raw
    // position) this should stay near zero.
    [HarmonyPatch(typeof(FloatingOriginManager), "Shift")]
    internal static class ShiftCallDiagnosticPatch
    {
        private static int callCount;

        private static void Prefix(int __0, int __1, FloatingOriginManager __instance)
        {
            callCount++;
            if (callCount <= 20 || callCount % 200 == 0)
            {
                Plugin.Log.LogInfo(
                    $"[RealEarth][ShiftCall] #{callCount} x={__0} z={__1} "
                    + $"outCurrentOffset(before)={__instance.outCurrentOffset}");
            }
        }
    }

    // The actual M1 "reposition" mechanism. Earlier attempts physically moved islands, the
    // player's start position, and the starter set's transform by Plugin.ReposOffset - all
    // reverted (see git history / CODING_NOTES.md "Floating origin") after live-testing showed
    // moving the player's raw position triggers the runaway described on ShiftCallDiagnosticPatch
    // above. This replaces all of that: GetGlobeCoords is purely
    // (pos - outCurrentOffset - globeOffset) / 9000, so offsetting outCurrentOffset once, right
    // after FloatingOriginManager.Start() resets it to (0,0,0), relabels every object's reported
    // lon/lat uniformly WITHOUT moving a single Transform. The player, islands, and starter set
    // all stay exactly where vanilla puts them relative to each other (so no more "phantom dock"
    // mismatch either - that was a symptom of physically moving islands away from an unmoved
    // boat/player, which can no longer happen since nothing physically moves), and the player's
    // raw position never leaves the safe zone near origin, so the engine's auto-shift logic never
    // has a reason to fire.
    //
    // Note: the "confirmed-safe" raw-distance ceiling documented in CODING_NOTES.md was about
    // physically moving objects, which this approach no longer does - but larger offsets (e.g.
    // the original -64.4/-16.7 Caribbean target) haven't been empirically retested under this new
    // approach yet, so the config default stays conservative for now.
    [HarmonyPatch(typeof(FloatingOriginManager), "Start")]
    internal static class FloatingOriginOffsetPatch
    {
        private static void Postfix(FloatingOriginManager __instance)
        {
            if (Plugin.RepositionEnabled == null || !Plugin.RepositionEnabled.Value)
            {
                return;
            }

            __instance.outCurrentOffset = -Plugin.ReposOffset;
            Plugin.Log.LogInfo(
                $"[RealEarth] Set outCurrentOffset={__instance.outCurrentOffset} to relabel the "
                + "world's coordinates - no objects moved.");
        }
    }

    // M1 step 1 (survey): logs every vanilla island's name, index, and real lat/lon at load
    // time, same capture point scrambled-seas used (IslandHorizon.Start). Verified in-game -
    // see data/vanilla_island_survey.json. Pure logging now - FloatingOriginOffsetPatch above
    // is what actually makes these report shifted coordinates.
    [HarmonyPatch(typeof(IslandHorizon), "Start")]
    internal static class IslandSurveyLogPatch
    {
        private static void Postfix(IslandHorizon __instance)
        {
            Vector3 localPos = __instance.transform.localPosition;
            Vector3 globe = FloatingOriginManager.instance != null
                ? FloatingOriginManager.instance.GetGlobeCoords(__instance.transform)
                : new Vector3(float.NaN, float.NaN, float.NaN);

            Plugin.Log.LogInfo(
                $"[RealEarth][Survey] '{__instance.gameObject.name}' idx={__instance.islandIndex} " +
                $"localPos={localPos} lon={globe.x:F3} lat={globe.z:F3}");
        }
    }
}
