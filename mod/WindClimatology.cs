using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SailwindRealEarth
{
    // Loads data/wind_climatology.csv (baked offline by pipeline/bake_wind_climatology.py from
    // Open-Meteo's free historical/archive API - see CODING_NOTES.md "Wind climatology pipeline")
    // and answers "what's the real prevailing wind direction/speed at this lon/lat, this month?"
    // via nearest-grid-point lookup. Plain CSV, not JSON: the game's managed DLLs only have
    // Unity's JsonUtility available, which can't deserialize the month-keyed dictionary shape
    // the data naturally has.
    internal static class WindClimatology
    {
        private readonly struct GridPoint
        {
            public readonly float Lat;
            public readonly float Lon;
            public readonly int Month;
            public readonly float TowardBearingDeg;
            public readonly float MeanSpeedKn;

            public GridPoint(float lat, float lon, int month, float bearing, float speedKn)
            {
                Lat = lat;
                Lon = lon;
                Month = month;
                TowardBearingDeg = bearing;
                MeanSpeedKn = speedKn;
            }
        }

        private static List<GridPoint> points;
        private static bool loadAttempted;

        internal static bool IsLoaded => points != null && points.Count > 0;

        internal static void EnsureLoaded()
        {
            if (loadAttempted)
            {
                return;
            }

            loadAttempted = true;
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string csvPath = Path.Combine(dir ?? ".", "wind_climatology.csv");

            if (!File.Exists(csvPath))
            {
                Plugin.Log.LogWarning(
                    $"[RealEarth] wind_climatology.csv not found at {csvPath} - historical wind "
                    + "mode will have no effect. Run pipeline/bake_wind_climatology.py and rebuild.");
                return;
            }

            List<GridPoint> loaded = new List<GridPoint>();
            string[] lines = File.ReadAllLines(csvPath);
            // lines[0] is the header (lat,lon,month,toward_bearing_deg,mean_speed_kn) - skip it.
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split(',');
                if (parts.Length != 5)
                {
                    continue;
                }

                // float.Parse/int.Parse throw on malformed data, with nothing upstream to catch
                // it - EnsureLoaded runs from inside Harmony postfixes on the wind patches, so an
                // unhandled exception here could disrupt a live wind recalculation instead of
                // just cleanly disabling historical wind. Caught by CodeRabbit (PR #3). Treats
                // ANY malformed row as reason to abandon the whole load (not just skip that row)
                // - partial/inconsistent climatology data is arguably worse than none.
                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float lat)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float lon)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int month)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float bearing)
                    || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float speedKn))
                {
                    Plugin.Log.LogWarning(
                        $"[RealEarth] Invalid wind climatology row {i} in {csvPath} - historical "
                        + "wind disabled (vanilla behavior preserved).");
                    return;
                }

                loaded.Add(new GridPoint(lat, lon, month, bearing, speedKn));
            }

            points = loaded;
            Plugin.Log.LogInfo($"[RealEarth] Loaded {points.Count} wind climatology rows from {csvPath}");
        }

        // Nearest-grid-point lookup (not bilinear interpolation) - the 2.5deg grid spacing is
        // coarse enough that interpolation would be a meaningful accuracy improvement later, but
        // nearest-neighbor is enough to validate the concept for M2 and keeps this simple. Linear
        // scan over ~2640 rows is fine since this only runs when Wind.SetNewWindTarget's own
        // timer fires (every few seconds), not every frame.
        internal static bool TryGetWind(float lon, float lat, int month, out float towardBearingDeg, out float meanSpeedKn)
        {
            towardBearingDeg = 0f;
            meanSpeedKn = 0f;

            if (!IsLoaded)
            {
                return false;
            }

            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < points.Count; i++)
            {
                GridPoint p = points[i];
                if (p.Month != month)
                {
                    continue;
                }

                float dLat = p.Lat - lat;
                float dLon = p.Lon - lon;
                float dist = dLat * dLat + dLon * dLon;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                return false;
            }

            GridPoint best = points[bestIdx];
            towardBearingDeg = best.TowardBearingDeg;
            meanSpeedKn = best.MeanSpeedKn;
            return true;
        }

        // Converts a "blowing toward" compass bearing (0=toward +z/north, 90=toward +x/east -
        // the pipeline's convention, matching Wind.currentWind's own direction in-game, NOT the
        // raw meteorological "blowing from" bearing) into a unit direction vector.
        internal static Vector3 BearingToDirection(float towardBearingDeg)
        {
            float rad = towardBearingDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        }
    }
}
