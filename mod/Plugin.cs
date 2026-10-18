using System.Reflection;
using BepInEx;
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

        private Harmony harmony;
        private bool overlayVisible = true;

        private void Awake()
        {
            Log = Logger;
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

    // M1 step 1: survey every vanilla island's name, index, and real lat/lon at load time,
    // same capture point scrambled-seas used (IslandHorizon.Start) to later reposition them.
    [HarmonyPatch(typeof(IslandHorizon), "Start")]
    internal static class IslandSurveyPatch
    {
        private static void Postfix(IslandHorizon __instance)
        {
            Vector3 localPos = __instance.transform.localPosition;
            string globeStr = "n/a";
            if (FloatingOriginManager.instance != null)
            {
                Vector3 globe = FloatingOriginManager.instance.GetGlobeCoords(__instance.transform);
                globeStr = $"lon={globe.x:F3} lat={globe.z:F3}";
            }

            Plugin.Log.LogInfo(
                $"[RealEarth][IslandSurvey] '{__instance.gameObject.name}' idx={__instance.islandIndex} " +
                $"localPos={localPos} {globeStr}");
        }
    }
}
