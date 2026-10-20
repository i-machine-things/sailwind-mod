#!/usr/bin/env python3
"""
Bakes a monthly wind climatology grid for M2 (historical wind mode) from Open-Meteo's
free historical/archive API (https://archive-api.open-meteo.com/v1/archive) - no API
key needed, confirmed live against a 10-year single-location query and a multi-location
batch query before writing this script (see CODING_NOTES.md "Wind climatology pipeline").

For each grid point and calendar month, fetches YEARS of daily wind data and computes:
  - direction: the resultant (vector-averaged) prevailing "blowing toward" bearing, in
    the game's coordinate convention (0deg = toward +z/north, 90deg = toward +x/east) -
    NOT the same as the raw meteorological "blowing from" bearing Open-Meteo reports,
    which is why every sample gets a 180deg flip before averaging. Vector-averaging
    (not plain degree-averaging) is what correctly handles circular data - directions
    near 359deg and 1deg should average to ~0deg, not ~180deg.
  - speed_kn: the arithmetic mean wind speed for that location/month (NOT the magnitude
    of the averaged vector, which would be artificially low wherever direction varies a
    lot - we want "how strong does it typically blow", not "how much do the directions
    cancel out").

Output: a flat CSV grid (data/wind_climatology.csv) the C# plugin loads at runtime for a
simple nearest-grid-point lookup, keyed by real lon/lat (matching
FloatingOriginManager.GetGlobeCoords, 9000 units/degree) and the current real-world month.
CSV, not JSON: the game's managed DLLs only have Unity's JsonUtility available (no
Newtonsoft.Json or similar), which can't deserialize our dictionary-keyed-by-month schema
without extra machinery - a flat "lat,lon,month,bearing,speed" file needs nothing but
string.Split on the C# side.
"""

import csv
import math
import sys
import time
from pathlib import Path

import requests

ARCHIVE_URL = "https://archive-api.open-meteo.com/v1/archive"

# Covers the vanilla archipelago's shifted bounding box (lon -44.2 to -0.2, lat 17.65 to
# 37.65 - vanilla's -12/32E, 26/46N shifted by the M1 default -32.2/-8.35 deg) plus a few
# degrees of margin for sailing beyond it. Re-run with a wider box once M4/M5 need more
# coverage - this isn't meant to be the final whole-Earth grid.
LON_MIN, LON_MAX = -47.0, 2.0
LAT_MIN, LAT_MAX = 15.0, 40.0
GRID_SPACING_DEG = 2.5

# Started at 10 years / 20-location batches - hit HTTP 429 on the 2nd request. Open-Meteo's
# free-tier billing counts any single-location request spanning >2 weeks as MULTIPLE calls
# (fractional, scaling with duration - see their pricing page), so a 10-year x 20-location
# request was costing far more than 20 "calls" against the 600/min limit. Backed off to 3
# years and 5-location batches with pacing + retry/backoff below - trades some statistical
# robustness (3 years of samples per month instead of 10) for actually completing without
# getting rate-limited. Revisit if a richer climatology is worth a slower/paid-tier bake.
YEAR_START, YEAR_END = 2022, 2024

LOCATIONS_PER_REQUEST = 5
REQUEST_PACING_SECONDS = 3.0
MAX_RETRIES = 5
RETRY_BACKOFF_BASE_SECONDS = 10.0

OUTPUT_PATH = Path(__file__).resolve().parent.parent / "data" / "wind_climatology.csv"


def build_grid_points():
    points = []
    lon = LON_MIN
    while lon <= LON_MAX + 1e-9:
        lat = LAT_MIN
        while lat <= LAT_MAX + 1e-9:
            points.append((round(lat, 4), round(lon, 4)))
            lat += GRID_SPACING_DEG
        lon += GRID_SPACING_DEG
    return points


def fetch_batch(points):
    """Fetches YEAR_START..YEAR_END daily wind data for a batch of (lat, lon) points.
    Returns a dict keyed by (lat, lon) -> {"dates": [...], "speed_kn": [...], "dir_from_deg": [...]}.
    """
    lats = ",".join(str(p[0]) for p in points)
    lons = ",".join(str(p[1]) for p in points)
    params = {
        "latitude": lats,
        "longitude": lons,
        "start_date": f"{YEAR_START}-01-01",
        "end_date": f"{YEAR_END}-12-31",
        "daily": "wind_speed_10m_mean,wind_direction_10m_dominant",
        "wind_speed_unit": "kn",
        "timezone": "GMT",
    }
    for attempt in range(MAX_RETRIES):
        resp = requests.get(ARCHIVE_URL, params=params, timeout=60)
        if resp.status_code != 429:
            resp.raise_for_status()
            break
        wait = RETRY_BACKOFF_BASE_SECONDS * (2 ** attempt)
        print(f"  429 rate limited, backing off {wait:.0f}s (attempt {attempt + 1}/{MAX_RETRIES})", file=sys.stderr)
        time.sleep(wait)
    else:
        resp.raise_for_status()  # re-raise the final 429 if all retries were exhausted
    data = resp.json()
    # Single-location requests return one object, not a list - normalize to a list so the
    # caller doesn't need two code paths.
    if isinstance(data, dict):
        data = [data]

    if len(data) != len(points):
        raise RuntimeError(
            f"Expected {len(points)} results, got {len(data)} - API response shape "
            "assumption (one object per requested point, same order) may be wrong."
        )

    out = {}
    for point, entry in zip(points, data):
        daily = entry["daily"]
        out[point] = {
            "dates": daily["time"],
            "speed_kn": daily["wind_speed_10m_mean"],
            "dir_from_deg": daily["wind_direction_10m_dominant"],
        }
    return out


def vector_average_direction_and_mean_speed(dir_from_deg_list, speed_kn_list):
    """Returns (toward_bearing_deg, mean_speed_kn) for one (grid point, month) bucket,
    skipping any day with a null reading (the API returns null for missing/future dates).
    """
    sum_x = 0.0
    sum_z = 0.0
    speeds = []
    for dir_from, speed in zip(dir_from_deg_list, speed_kn_list):
        if dir_from is None or speed is None:
            continue
        # Open-Meteo reports the bearing wind blows FROM (meteorological convention);
        # Wind.currentWind in-game is the "blowing toward" vector, so flip 180deg first.
        toward_deg = (dir_from + 180.0) % 360.0
        toward_rad = math.radians(toward_deg)
        # Game convention: .x = longitude/east, .z = latitude/north (confirmed against
        # FloatingOriginManager.GetGlobeCoords and sanity-checked against the vanilla
        # fictional trade-wind vectors in Wind.cs - see CODING_NOTES.md).
        sum_x += math.sin(toward_rad)
        sum_z += math.cos(toward_rad)
        speeds.append(speed)

    if not speeds:
        return None, None

    resultant_bearing = math.degrees(math.atan2(sum_x, sum_z)) % 360.0
    mean_speed = sum(speeds) / len(speeds)
    return resultant_bearing, mean_speed


def bake():
    points = build_grid_points()
    print(f"Grid: {len(points)} points, lon [{LON_MIN},{LON_MAX}] lat [{LAT_MIN},{LAT_MAX}] "
          f"spacing {GRID_SPACING_DEG}deg, years {YEAR_START}-{YEAR_END}", file=sys.stderr)

    all_series = {}
    batches = [points[i:i + LOCATIONS_PER_REQUEST] for i in range(0, len(points), LOCATIONS_PER_REQUEST)]
    for i, batch in enumerate(batches):
        print(f"Fetching batch {i + 1}/{len(batches)} ({len(batch)} points)...", file=sys.stderr)
        all_series.update(fetch_batch(batch))
        if i < len(batches) - 1:
            time.sleep(REQUEST_PACING_SECONDS)

    rows = []
    for (lat, lon), series in sorted(all_series.items()):
        for month in range(1, 13):
            idxs = [i for i, d in enumerate(series["dates"]) if int(d[5:7]) == month]
            dirs = [series["dir_from_deg"][i] for i in idxs]
            speeds = [series["speed_kn"][i] for i in idxs]
            bearing, mean_speed = vector_average_direction_and_mean_speed(dirs, speeds)
            if bearing is None:
                print(f"WARNING: no data for lat={lat} lon={lon} month={month}", file=sys.stderr)
                continue
            rows.append((lat, lon, month, round(bearing, 1), round(mean_speed, 2)))

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT_PATH.open("w", newline="") as f:
        # toward_bearing_deg: compass bearing the wind blows TOWARD (0=toward +z/north,
        # 90=toward +x/east), matching Wind.currentWind's direction in-game - NOT the raw
        # meteorological "blowing from" bearing. Source: Open-Meteo archive API
        # (archive-api.open-meteo.com/v1/archive), years {YEAR_START}-{YEAR_END}, grid
        # spacing {GRID_SPACING_DEG}deg, bbox lon [{LON_MIN},{LON_MAX}] lat [{LAT_MIN},{LAT_MAX}].
        writer = csv.writer(f)
        writer.writerow(["lat", "lon", "month", "toward_bearing_deg", "mean_speed_kn"])
        writer.writerows(rows)
    print(f"Wrote {len(rows)} rows ({len(all_series)} grid points) to {OUTPUT_PATH}", file=sys.stderr)


if __name__ == "__main__":
    bake()
