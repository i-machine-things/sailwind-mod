using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SailwindRealEarth
{
    // Soft dependency on NANDbrew/BorderExpander (com.nandbrew.borderexpander) - expands
    // WorldBorder to +-70deg and handles east/west circumnavigation, which M1's shifted
    // coordinates immediately need (confirmed live: vanilla WorldBorder's "out of game area"
    // recovery fires once our shift puts the player outside its hardcoded authored bounds).
    // Soft, not hard: our own patches don't call any BorderExpander API (yet - AddRegion is
    // there for custom trade-wind regions if M2+ needs it), so the mod still loads and works,
    // just bounded by vanilla's border, if BorderExpander isn't installed. SoftDependency only
    // affects load order (BorderExpander first if both present), per the GUID confirmed by
    // inspecting its DLL - see CODING_NOTES.md.
    [BepInDependency("com.nandbrew.borderexpander", BepInDependency.DependencyFlags.SoftDependency)]
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

        // M2: real historical wind direction, baked from Open-Meteo's archive API - see
        // pipeline/bake_wind_climatology.py and CODING_NOTES.md.
        internal static ConfigEntry<bool> HistoricalWindEnabled;

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
            HistoricalWindEnabled = Config.Bind(
                "RealEarth", "HistoricalWindEnabled", true,
                "M2: replace Wind.GetCurrentTradeWind's fictional direction, and "
                + "Wind.SetNewWindTarget's base magnitude, with real historical-average values "
                + "for the player's current (shifted) lon/lat and the real-world current month, "
                + "from a climatology grid baked offline by pipeline/bake_wind_climatology.py. "
                + "Magnitude is clamped to vanilla's own [minimumMagnitude, maximumMagnitude] "
                + "range - chaos/gusts/storms still stay vanilla. No effect if "
                + "BepInEx/plugins/SailwindRealEarth/wind_climatology.csv is missing.");
            // BorderExpander is a soft, not hard, dependency (see the class-level comment above
            // for why) - but if reposition is on and it's missing, the player WILL get stuck at
            // the vanilla world-border "out of game area" recovery the moment they sail near the
            // shifted coordinates, with no indication why. Confirmed live before BorderExpander
            // was installed. A loud startup warning is cheap insurance against that silent trap -
            // flagged by CodeRabbit (PR #1) as a real gap in the soft-dependency design.
            // Originally only checked whether BorderExpander was loaded at all - missed the case
            // where it's installed but its own "Free Sailing" setting is still off (default
            // false), which hits the exact same stuck-at-border trap silently. Caught by
            // CodeRabbit (PR #2), fifth pass. Reads the live config value via reflection (no
            // direct assembly reference - don't want a hard build dependency on a third-party
            // mod) rather than just inferring it from "is the plugin loaded".
            if (RepositionEnabled.Value)
            {
                bool borderExpanderLoaded = Chainloader.PluginInfos.ContainsKey("com.nandbrew.borderexpander");
                if (!borderExpanderLoaded || !IsBorderExpanderFreeSailingEnabled())
                {
                    Log.LogWarning(
                        "[RealEarth] RepositionIslands is enabled but "
                        + (borderExpanderLoaded
                            ? "BorderExpander's \"Free Sailing\" setting is off"
                            : "BorderExpander (com.nandbrew.borderexpander) isn't loaded")
                        + " - sailing near the shifted coordinates will likely trigger vanilla "
                        + "WorldBorder's \"out of game area\" recovery and bounce you back to the "
                        + "dock. Install BorderExpander and enable its \"Free Sailing\" setting - "
                        + "see CODING_NOTES.md.");
                }
            }

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo($"{PluginName} {PluginVersion} loaded - F9 toggles the lat/lon overlay, "
                + "F6 forces an immediate wind recalculation, F7 forces an obvious test gust, "
                + "F8 logs BorderExpander's live border-limit state");
        }

        private static bool IsBorderExpanderFreeSailingEnabled()
        {
            Type beType = Type.GetType("BorderExpander.Plugin, BorderExpander");
            FieldInfo field = beType?.GetField("freeSail", BindingFlags.NonPublic | BindingFlags.Static);
            object configEntry = field?.GetValue(null);
            object value = configEntry?.GetType().GetProperty("Value")?.GetValue(configEntry);
            return value is bool enabled && enabled;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                overlayVisible = !overlayVisible;
            }

            // Wind.SetNewWindTarget only runs on its own internal timer (unknown exact
            // interval - Inspector-configured, not visible from decompiled code alone), which
            // made testing HistoricalWindEnabled's on/off difference impractical: toggling it
            // and immediately comparing just shows whatever wind value was already computed
            // before the toggle, since nothing forces a recalculation. Wind.debugChangeWind is
            // the game's own existing debug hook for this (confirmed in Wind.Update: setting it
            // zeroes the internal timer, forcing SetNewWindTarget to run again immediately) -
            // F6 exposes it so the toggle is actually testable on demand. Originally bound to
            // F10, but that's already wired to vanilla's own pause/escape menu - confirmed live
            // (pressing it opened the esc menu instead of just logging), moved off the F9-F12
            // range entirely to dodge any other undocumented vanilla bindings in that block.
            if (Input.GetKeyDown(KeyCode.F6) && Wind.instance != null)
            {
                Wind.instance.debugChangeWind = true;
                Log.LogInfo("[RealEarth] F6: forced an immediate Wind.SetNewWindTarget recalculation.");
            }

            // F6 proved the direction toggle works correctly via log lines, but a direction-only
            // change within vanilla's normal gameplay magnitude range (currentWind started at
            // ~9 units) is genuinely hard to feel/see at the dock over a short window - raised
            // directly by live testing ("not seeing any difference in-game"). This forces an
            // obvious, unmistakable gust using the game's own existing public debug hook
            // (Wind.ForceNewWind, confirmed in Wind.cs: sets currentWind/currentBaseWind directly
            // and re-triggers target/gust calculation) at several times normal magnitude, in the
            // SAME direction our climatology patch computes - so it's both a magnitude sanity
            // check and a visible demonstration of the real direction, not a separate fake test.
            if (Input.GetKeyDown(KeyCode.F7) && Wind.instance != null)
            {
                Vector3 direction = Wind.currentWind.sqrMagnitude > 0.0001f
                    ? Wind.currentWind.normalized
                    : Vector3.forward;

                if (FloatingOriginManager.instance != null && Refs.observerMirror != null)
                {
                    WindClimatology.EnsureLoaded();
                    Vector3 globe = FloatingOriginManager.instance.GetGlobeCoords(Refs.observerMirror.transform);
                    if (WindClimatology.TryGetWind(globe.x, globe.z, DateTime.Now.Month, out float bearing, out _))
                    {
                        direction = WindClimatology.BearingToDirection(bearing);
                    }
                }

                const float GaleMagnitude = 60f;
                Wind.instance.ForceNewWind(direction * GaleMagnitude);
                Log.LogInfo($"[RealEarth] F7: forced an obvious test gust, direction={direction} magnitude={GaleMagnitude} "
                    + "(several times normal - for visibility, not a realistic value).");
            }

            // Diagnostic for the "recovery cascade at extreme offsets" investigation - after
            // repositioning to 46N/123W, a large outCurrentOffset drift was observed alongside a
            // 2000+-call Shift() burst, matching Recovery.worldBorder (confirmed via decompiling
            // Recovery.cs: DoRecoverPlayer raw-teleports Refs.observerMirror.transform.position
            // to the last visited port, which - since we never move raw positions ourselves -
            // retriggers FloatingOriginManager's own auto-shift). BorderExpander's own
            // WorldBorderPatch should set eastLimit/westLimit to +-Infinity when Free Sailing is
            // on, which shouldn't flag any finite longitude - reading its actual live field
            // values via reflection (no direct assembly reference - don't want a hard build
            // dependency on a third-party mod) rather than guessing why it fired anyway.
            if (Input.GetKeyDown(KeyCode.F8))
            {
                LogBorderExpanderState();
            }
        }

        private static void LogBorderExpanderState()
        {
            Log.LogInfo($"[RealEarth] F8: {DescribeBorderExpanderState()}");
        }

        // Shared by the F8 manual dump and RecoveryReasonLogPatch (fires automatically whenever
        // Recovery.RecoverPlayer is called) - both want the same live field snapshot. Reflection,
        // not a direct assembly reference, since this is a soft dependency we don't want a hard
        // build reference to.
        internal static string DescribeBorderExpanderState()
        {
            Type beType = Type.GetType("BorderExpander.Plugin, BorderExpander");
            if (beType == null)
            {
                return "BorderExpander.Plugin type not found (not loaded?).";
            }

            object GetStatic(string name)
            {
                FieldInfo f = beType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return f?.GetValue(null);
            }

            object freeSailEntry = GetStatic("freeSail");
            object freeSailValue = freeSailEntry?.GetType().GetProperty("Value")?.GetValue(freeSailEntry);
            object initialized = GetStatic("initialized");
            object north = GetStatic("northLimit");
            object south = GetStatic("southLimit");
            object east = GetStatic("eastLimit");
            object west = GetStatic("westLimit");

            return $"BorderExpander.Plugin.freeSail={freeSailValue} initialized={initialized} "
                + $"limits N={north} S={south} E={east} W={west}";
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

    // M2: replaces the vanilla fictional trade-wind direction with a real historical-average
    // one. Confirmed by decompiling Wind.cs: GetCurrentTradeWind is the *only* geography-
    // dependent piece of the wind system - it returns a direction vector (not a full wind
    // vector) from hardcoded lat/lon thresholds, which SetNewWindTarget then blends with a
    // random direction, the previous wind's direction, and separately-computed magnitude/chaos/
    // storm/coastal effects. Overriding only this method's __result therefore swaps in a real
    // direction while leaving all of that existing variability/gameplay-feel logic untouched -
    // exactly the surgical patch point the plan called for, not a guess.
    [HarmonyPatch(typeof(Wind), "GetCurrentTradeWind")]
    internal static class HistoricalWindDirectionPatch
    {
        private static void Postfix(Wind __instance, ref Vector3 __result)
        {
            if (Plugin.HistoricalWindEnabled == null || !Plugin.HistoricalWindEnabled.Value)
            {
                return;
            }

            if (FloatingOriginManager.instance == null || Refs.observerMirror == null)
            {
                return;
            }

            WindClimatology.EnsureLoaded();
            if (!WindClimatology.IsLoaded)
            {
                return;
            }

            // Same reference object the vanilla method itself already queries (Refs.observerMirror)
            // - already shifted automatically by FloatingOriginOffsetPatch, no extra work needed.
            Vector3 globe = FloatingOriginManager.instance.GetGlobeCoords(Refs.observerMirror.transform);
            int month = DateTime.Now.Month;

            if (WindClimatology.TryGetWind(globe.x, globe.z, month, out float bearing, out float speedKn))
            {
                __result = WindClimatology.BearingToDirection(bearing);
                // Logging minimumMagnitude/maximumMagnitude here too - Inspector-configured
                // fields, not visible from decompiled code alone - to calibrate a real
                // knots-to-game-units conversion for the still-open "apply real magnitude" M2
                // item, rather than guessing a scale factor blind.
                Plugin.Log.LogInfo(
                    $"[RealEarth][HistoricalWind] lon={globe.x:F2} lat={globe.z:F2} month={month} -> "
                    + $"toward_bearing={bearing:F1}deg, mean speed {speedKn:F1}kn "
                    + $"| vanilla magnitude range [{__instance.minimumMagnitude:F2}, {__instance.maximumMagnitude:F2}]");
            }
        }
    }

    // M2 magnitude: applies the climatology's real mean wind speed, not just direction.
    // Confirmed live (F6-forced recalculation, logged alongside HistoricalWindDirectionPatch
    // above): vanilla's own minimumMagnitude/maximumMagnitude are [0, 18], and our baked grid's
    // mean_speed_kn ranges 2.79-21.85 (mean 11.9) across all 220 points/12 months - close enough
    // to a 1:1 correspondence that the devs' magnitude units are plausibly already "roughly
    // knots". Uses the climatology speed directly (clamped to vanilla's own range, so it can
    // never exceed what the game was tuned/tested for) rather than inventing an unjustified
    // scale factor.
    //
    // SetNewWindTarget is private, and the field we need to rescale (currentWindTarget) is
    // private too - Harmony's "___fieldName" Postfix parameter convention gives ref access to
    // it. Postfix (not Prefix) so this runs after the vanilla method (and
    // HistoricalWindDirectionPatch, which patches a method SetNewWindTarget calls internally)
    // have already set the target's direction - this only rescales magnitude, direction is
    // untouched.
    [HarmonyPatch(typeof(Wind), "SetNewWindTarget")]
    internal static class HistoricalWindMagnitudePatch
    {
        private static void Postfix(Wind __instance, ref Vector3 ___currentWindTarget)
        {
            if (Plugin.HistoricalWindEnabled == null || !Plugin.HistoricalWindEnabled.Value)
            {
                return;
            }

            if (FloatingOriginManager.instance == null || Refs.observerMirror == null)
            {
                return;
            }

            WindClimatology.EnsureLoaded();
            if (!WindClimatology.IsLoaded)
            {
                return;
            }

            if (___currentWindTarget.sqrMagnitude < 0.0001f)
            {
                // No direction to rescale (shouldn't normally happen - direction is set earlier
                // in the same method call via GetCurrentTradeWind/blending) - skip rather than
                // risk dividing by ~zero in .normalized.
                return;
            }

            Vector3 globe = FloatingOriginManager.instance.GetGlobeCoords(Refs.observerMirror.transform);
            int month = DateTime.Now.Month;

            if (WindClimatology.TryGetWind(globe.x, globe.z, month, out _, out float speedKn))
            {
                // Rescale the vanilla-computed magnitude rather than replacing it outright -
                // ___currentWindTarget already has the random walk plus chaos/storm/coastal
                // effects baked in by this point, and replacing it with a single fixed
                // climatology value per grid point/month would flatten all of that organic
                // variability (same gust/storm feel the ROADMAP's M2 section calls out as
                // something to preserve). Scaling by (climatology speed / vanilla midpoint)
                // keeps the vanilla variation shape while biasing its average toward the real
                // value. Caught by CodeRabbit (PR #3).
                float vanillaMagnitude = ___currentWindTarget.magnitude;
                float vanillaMid = 0.5f * (__instance.minimumMagnitude + __instance.maximumMagnitude);
                float scale = vanillaMid > 0.0001f ? speedKn / vanillaMid : 1f;
                float magnitude = Mathf.Clamp(vanillaMagnitude * scale,
                    __instance.minimumMagnitude, __instance.maximumMagnitude);
                ___currentWindTarget = ___currentWindTarget.normalized * magnitude;
                Plugin.Log.LogInfo($"[RealEarth][HistoricalWind] applied magnitude {magnitude:F2} "
                    + $"(climatology {speedKn:F1}kn scaling vanilla {vanillaMagnitude:F2}, "
                    + "clamped to vanilla range).");
            }
        }
    }

    // Direct visibility into why a recovery (the "out of game area"/thirst/hunger/etc. dock
    // teleport) fired, instead of inferring it after the fact - BepInEx's LogOutput.log doesn't
    // include Unity's own Debug.Log calls by default (WriteUnityLog=false in BepInEx.cfg unless
    // the user opts in), so neither Recovery.DoRecoverPlayer's nor BorderExpander's own logging
    // ("updating/defaulting world limits") were ever reaching the log file - this patch logs the
    // actual RecoveryReason plus live globe coords and BorderExpander's border-limit state at the
    // exact moment recovery is requested, through our own ManualLogSource instead.
    [HarmonyPatch(typeof(Recovery), "RecoverPlayer")]
    internal static class RecoveryReasonLogPatch
    {
        private static void Prefix(RecoveryReason reason)
        {
            Vector3 globe = (FloatingOriginManager.instance != null && Refs.observerMirror != null)
                ? FloatingOriginManager.instance.GetGlobeCoords(Refs.observerMirror.transform)
                : new Vector3(float.NaN, float.NaN, float.NaN);

            Plugin.Log.LogWarning($"[RealEarth][Recovery] RecoverPlayer reason={reason} "
                + $"lon={globe.x:F3} lat={globe.z:F3} | {Plugin.DescribeBorderExpanderState()}");
        }
    }
}
