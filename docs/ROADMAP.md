# Sailwind "Real Earth" Mod — Plan & Feature Roadmap

## Context

Sailwind (Raw Lion Workshop) is installed at
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sailwind`
and runs via Proton (Steam AppID `1764530`). The goal is a mod that replaces
Sailwind's fictional archipelago with **the real Earth** — real coastlines/
bathymetry and real wind/weather, in the spirit of *Sailaway* — with weather
sourced from either historical climatology or a live API (toggleable).

This is a large, multi-month-scale modding project. The plan below sequences
it so there's a working, testable milestone every step, rather than one big
unverifiable rewrite.

### What the game already gives us (confirmed by inspecting the install)

- **Moddable runtime**: Unity 2019.1.10f1, **Mono** scripting backend (not
  IL2CPP), unobfuscated `Assembly-CSharp.dll` in `Sailwind_Data/Managed/`.
  This means **BepInEx 5.x (Mono/x64) + Harmony** works directly — no IL2CPP
  interop headaches.
- **Floating origin already exists**: a `FloatingOriginManager` class is
  present, which is exactly the pattern needed to support a world far larger
  than Unity's single-precision float space can represent natively. We
  extend this rather than invent it.
- **Islands are discrete streamed scenes**, not one baked landmass:
  `IslandLoaded`, `islandLoadDistance`, `IslandSceneryScene`,
  `IslandDistanceTracker` — islands load/unload based on boat proximity.
  Real-world terrain tiles can use the same streaming seam.
- **Ocean rendering is the Crest Ocean System** (`OceanRenderer`,
  `OceanWavesUpdater`, `OceanWaveSpectrum`) — a known, documented Unity asset
  with a public API for wave spectra driven by wind, which we can feed real
  wind data into instead of patching shader internals blind.
- **Wind/weather already has a single logical system to hook**, evidenced by
  symbols like `BoatWind`, `GetApparentWind`, `currentWind`/
  `currentWindTarget`, `UpdateWind`, `ApplyWeather`/`ChangeWeather`,
  `GetCurrentTradeWind`, `WeatherSnapshot`, plus **existing debug hooks**
  (`debugWind`, `debugConstantWind`, `ForceNewWind`, `DebugWeather`) — strong
  signal the devs already isolated wind/weather behind a patchable surface.
- **Navigation is celestial** (`latitude`, `latitudeDial`, `MapChart`,
  `MapChartTextureRenderer`, `chartData`) — a sextant/latitude mechanic that,
  once the world is real, can be upgraded to use true astronomical formulas.
- **World is currently bounded** (`WorldBorder`) and relatively small/fixed —
  this is the main thing a "whole Earth" mod has to outgrow.

### Ecosystem check (done after M0 harness install — confirms we're on the standard path)

There's an active Sailwind modding community on Thunderstore + GitHub
(org `AppSailwindMods`, modder `NANDbrew`, modder `DogEggz`, modder `RadDude`),
all built on **BepInEx 5.4.23.5 + Harmony** — exactly what we installed. Real
shipped mods gave us ground truth that replaces several guesses above:

- **The game already has a global lat/lon API** —
  `FloatingOriginManager.instance.GetGlobeCoords(transform)` returns a
  `Vector3` where `.x` = longitude and `.z` = latitude, and
  `ShiftingPosToRealPos(pos)` / `RealPosToShiftingPos(pos)` convert between
  floating-origin-relative and stable world position. **This removes most of
  the custom coordinate-layer work originally planned for M1** — we drive
  real-world lat/lon through the existing API instead of building one.
- **Confirmed scale**: 9000 Unity units = 1 degree of longitude/latitude.
  Vanilla world bounds are lon -12°E to 32°E, lat 26°N to 46°N (a
  Mediterranean-shaped box) — consistent with the `Climate` mod's named
  regions (Al'Ankh, Emerald, Aestrin, Fire Fish Lagoon, Chronos).
- **The world is a flat equirectangular-style projection, not a true
  sphere**: longitude wraps (circumnavigation), latitude is clamped, no
  pole handling needed. This is a simplification Sailaway-style mods can
  live with for v1.
- **`NANDbrew/BorderExpander` (open source, MIT-style modder info in its
  README) already does most of M5's hard parts**: expands `WorldBorder` to
  ±70° latitude, implements east/west circumnavigation (antimeridian
  wraparound, including safely teleporting nearby moored boats), adjusts
  sunrise/sunset and the sun compass for latitude, and exposes a public
  `BorderExpander.AddRegion(TradeWindRegion)` API for custom trade-wind
  regions. **We should depend on this mod instead of reimplementing border
  expansion/circumnavigation.**
- **Confirmed real classes/methods** (via decompiled strings and by reading
  `ChaoticWind`, `BorderExpander`, and `scrambled-seas` source directly):
  `Wind.Awake`, `Wind.SetNewWindTarget`, `Wind.SetNewGustTarget`,
  `Wind.GetCurrentTradeWind`; `RegionBlender.Update`/`.Start` with private
  field `currentTargetRegion`; `Region` components placed in the world that
  `RegionBlender` picks the nearest of; `WorldBorder.Update` with private
  fields `currentGlobePos`/`outTimer`; `GameState` (statics like `playing`,
  `recovering`, `justWokeUp`, `distanceToLand`, `modData`); `SaveLoadManager`
  with `SaveModData`/`LoadModData` — **the community's established extension
  point for mod save data**, not a custom save format; `IslandHorizon` (per-
  island component with `islandIndex`).
- **Some vanilla wind-scaling thresholds are IL constants**, not fields
  (`ChaoticWind`'s `WindPatches.cs` uses a Harmony *transpiler* to rewrite
  `Mathf.InverseLerp(1500f, 4000f, ...)` inside `Wind.SetNewWindTarget`) —
  worth knowing going in so we're not surprised when a prefix/postfix isn't
  enough for a given value.
- **Plugin conventions to match**: GUID style `com.<author>.<modname>`
  (ours: `com.allan.sailwind.realearth`), `[BepInDependency(otherGuid,
  SoftDependency)]` + `Chainloader.PluginInfos.ContainsKey(...)` checks for
  cross-mod compatibility (as `ChaoticWind`/`BorderExpander` do for each
  other and for `Climate`), config via `BepInEx.Configuration.ConfigFile.Bind`.

This means M0's "ship a trivial Harmony patch" smoke test should log
`FloatingOriginManager.instance.GetGlobeCoords(...)` for the player's boat —
proving both injection *and* the real geo API in one step — rather than a
throwaway patch we'd discard.

### Map/chart UI — two separate systems, confirmed by decompiling (ilspycmd)

Moving or adding land raises an obvious question: does any in-game map need to
be kept in sync? Decompiling `MapChart.cs`, `MapChartTextureRenderer.cs`, and
`MissionDetailsUI.cs` directly (not guessing from strings) shows there are
**two unrelated systems**, only one of which cares:

- **The player's own navigation chart (`MapChart`/`ChartData`/
  `MapChartTextureRenderer`) is blank graph paper.** It only renders
  player-drawn `ChartLine`s (hand-plotted dead-reckoning/celestial-fix lines)
  onto a texture via a tiny top-down camera. It never references island or
  port positions at all. **Repositioning or adding islands needs zero changes
  here** — this is exactly the intended Sailwind gameplay (you navigate by
  your own plotting, not a GPS blip), and real-world coordinates make this
  *more* legitimate, not less.
- **The mission-briefing map (`MissionDetailsUI.UpdateMap`) is a static,
  hand-painted texture per region** (`oceanMap`, `alankhMap`, `emeraldMap`,
  `mediMap`, `lagoonMap`) with a route line/destination pin positioned from
  each port's **separately-authored** `oceanMapLocation`/`localMapLocation`
  marker `Transform` — not computed from the port's actual world position.
  Moving a port's real `IslandHorizon` transform does **not** move this
  marker, so the pin/route line goes stale (wrong) while the art still looks
  fine — silently misleading, which is worse than visibly broken. The vanilla
  game already has a graceful fallback for this exact shape of problem
  (`flag2` branch in `UpdateMap`: renderer hidden, replaced with
  `"(map unavailable)\napproximate location: {lat}° {N/S}, {lon}° {E/W}"` via
  `FloatingOriginManager.GetGlobeCoords`) — but that fallback only triggers on
  null/mismatched markers, not on "the marker is just wrong now", so it won't
  fire automatically just because we moved an island. `Rahakami/scrambled-seas`
  hit this exact problem and worked around it by force-disabling the renderer
  entirely (Harmony postfix on `MissionDetailsUI.UpdateMap`, same technique).

**Decision for this mod:** in M1 (repositioning existing islands, no new art
yet), do the same as `scrambled-seas` — force the vanilla "(map unavailable)"
fallback for any mission whose ports we've moved, rather than show a
misleadingly-stale pin. This is a stopgap, not the real fix. The real fix
belongs with the terrain milestones (M4/M5): once we're generating real
coastline art for a region anyway, generate the mission-briefing map texture
from the same real data and compute `oceanMapLocation`/`localMapLocation`
marker positions directly from each port's real lat/lon projected into that
texture's space — at that point this map stops being hand-painted-and-stale
and becomes another generated artifact of the same real-geography pipeline.

## Architecture

1. **Mod harness**: BepInEx 5 (Mono x64) dropped into the Sailwind install,
   loading a single C# plugin DLL built with Harmony patches, with a soft/
   hard dependency on `BorderExpander` (for world-border expansion and
   circumnavigation — no need to reimplement). New project at
   `~/code/sailwind-mod/` (its own git repo), containing:
   - `mod/` — the C# BepInEx plugin (patches, runtime logic)
   - `pipeline/` — offline Python/GDAL tooling that preprocesses public
     geographic & weather datasets into compact runtime tiles
   - `data/` — generated tiles the mod ships or downloads on demand
2. **Coordinate system**: use the game's existing `FloatingOriginManager`
   lat/lon API directly (`GetGlobeCoords`, `ShiftingPosToRealPos`/
   `RealPosToShiftingPos`, 9000 units/degree) instead of building a parallel
   geographic layer. Our mod extends the *range* this already covers (via
   `BorderExpander`, pushed further for whole-Earth coverage) rather than
   replacing the coordinate math itself.
3. **Terrain/coastline streaming**: real elevation+bathymetry
   (GEBCO global bathymetric grid, free) and coastline vectors (OpenStreetMap
   coastlines / Natural Earth, free) are preprocessed offline into a tiled
   heightmap format, then streamed at runtime using the same
   load/unload-by-distance pattern the game already uses for islands.
4. **Wind/weather data layer**: a small abstraction with two
   implementations selected by a mod config toggle:
   - **Historical/climatological**: precomputed monthly wind climatology
     (e.g. ERA5 reanalysis or a public wind atlas) baked into compact grids
     at pipeline time — fully offline.
   - **Live**: a free marine weather API (e.g. Open-Meteo Marine, no key
     required, global coverage) queried periodically for the boat's real
     coordinates.
   Both feed the same internal "current wind/weather at (lat,lon,time)"
   interface, which Harmony-patches into the game's existing
   `BoatWind`/`ApplyWeather`/`UpdateWind` methods, replacing their fictional
   trade-wind-belt logic.
5. **Celestial navigation**: once positions are real, replace the sextant's
   underlying latitude solution with a real solar/stellar ephemeris
   computation (standard astronomical formulas), so navigation becomes
   genuinely accurate rather than thematically fictional.

## Feature Roadmap

**M0 — Mod harness & decompile (foundation, no visible change in-game)**
- Install BepInEx 5 into the Sailwind Proton prefix; confirm it loads
  (log line / on-screen console) under Proton.
- Decompile `Assembly-CSharp.dll` with ILSpy; pin the exact classes/methods
  behind the symbols found above (wind, weather, floating origin, island
  streaming, save/load).
- Ship a trivial Harmony patch (e.g. log current wind each second) as an
  end-to-end pipeline smoke test.

**M1 — Real coordinates, same small world (prove the coordinate system) — DONE**
- Debug overlay/log reading `FloatingOriginManager.instance.GetGlobeCoords`
  for the player's boat each second, confirmed against known reference
  points (the existing API already gives true lat/lon — no new math needed).
  ✅ Done (F9 overlay).
- Reposition the *existing* Sailwind islands onto real-world analogue
  coordinates, as a rehearsal for swapping in real terrain later. ✅ Done —
  **not** via physically moving objects (that approach was tried, caused a
  severe crash, and was abandoned — see `CODING_NOTES.md`'s "Floating
  origin" section for the full story). Final approach: offset
  `FloatingOriginManager.outCurrentOffset` once at boot, which relabels
  every object's reported lon/lat uniformly without moving a single
  `Transform`. Confirmed live, repeatedly: normal spawn, normal sailing,
  normal save/load, no crashes.
- Depend on `BorderExpander` with free-sailing enabled so the border doesn't
  get in the way while testing. ✅ Done — installed `BorderExpander` v0.5.0,
  confirmed GUID `com.nandbrew.borderexpander`, soft-dependency declared in
  our plugin, `Free Sailing` config enabled. Confirmed live: the vanilla
  "out of game area" border-recovery timer (which fired correctly once our
  shift put the player outside `WorldBorder`'s hardcoded authored box) no
  longer triggers.
- Force the vanilla "(map unavailable)" fallback in `MissionDetailsUI.UpdateMap`
  for any mission involving a port we've moved — **turned out to be
  unnecessary**, confirmed live. That staleness only happens when a port's
  *raw position* moves away from its hand-authored map marker; since the
  final M1 approach never moves raw positions at all, the markers stay
  correctly aligned automatically. Accepted a real mission in-game and
  confirmed the map/route pin render normally, no fallback needed.

**M2 — Real wind, historical mode**
- Decompiled the real wind architecture to confirm the actual patch target
  (not guessed): `Wind.GetCurrentTradeWind()` (private, returns a direction
  vector purely from `FloatingOriginManager.GetGlobeCoords` latitude/
  longitude thresholds - already the exact API M1 uses) is the *one* real
  geography-dependent hook. `BoatWind` and `Weather`/`ApplyWeather` don't
  touch geography at all - `BoatWind.FixedUpdate` just reads the global
  `Wind.currentWind` and computes apparent wind from boat velocity, and
  `Weather.ApplyWeather` is purely visual (sky/cloud/rain blending). `Region`
  objects (trigger volumes) only control chaos/variability parameters
  (`windChaos`/`windDirChaos`), orthogonal to direction - keep as-is.
- **Data source: Open-Meteo's historical/archive API** (not ERA5/Copernicus
  CDS) - free, no account/API key needed, and the same provider M3 already
  planned to use for live data, so one integration covers both milestones.
- **Time mapping: real wall-clock date.** Vanilla Sailwind has no calendar/
  season system at all (`GameState.day` is just a day-counter since the save
  started, confirmed by decompiling `Sun.cs` - no month/season concept
  anywhere). Rather than invent an in-game calendar, whatever month it
  actually is in real life when you play picks the climatology month.
  Simplest option that needs no new systems; revisit if an in-game calendar
  ever becomes worth building for its own sake.
- Build the pipeline step that bakes a monthly wind climatology grid from
  Open-Meteo's historical API.
- Patch `Wind.GetCurrentTradeWind()` (Postfix, override `__result`) to return
  a direction derived from the climatology grid at the player's current
  (shifted) lat/lon and real-world current month, when historical mode is
  enabled - leaving `SetNewWindTarget`'s existing magnitude/chaos/gust
  blending intact so the organic variability gameplay feel is preserved.
- Validate: sailing north/south through a known trade-wind latitude band
  produces the expected real prevailing direction/strength.

**M3 — Real wind, live mode + toggle**
- Add the live Open-Meteo-style backend and the config toggle between
  historical/live.
- Handle no-connectivity gracefully (fall back to historical).

**M4 — First real terrain tile (coastline streaming proof)**
- Pipeline: GEBCO bathymetry + OSM coastline → one streamable tile format,
  for one small pilot region.
- Runtime: stream that tile in/out using the existing island
  load/unload-by-distance pattern; render as Unity Terrain.
- **Tile streaming and floating-origin re-centering must be designed
  together, not separately.** M1's reposition rehearsal first tried moving
  all 41 islands (and the player/starter set) via direct raw-position
  teleports. That approach is now understood and abandoned — see
  `CODING_NOTES.md`'s "Floating origin" section for the full story — the
  real root cause (confirmed by instrumenting the engine, not guessed) was
  that `FloatingOriginManager.Update()` auto-triggers the engine's own
  `Shift()` in a tight loop whenever the player's **raw** position exceeds
  `shiftDistance` (confirmed: 512 units), and a one-shot ~290,000-unit
  teleport sent that loop into a runaway (28,000+ calls, `outCurrentOffset`
  ballooning to 10.8 million) ending in an actual crash. **M1's reposition
  is now done by offsetting `FloatingOriginManager.outCurrentOffset` once at
  boot instead — zero objects physically moved, zero risk of that cascade.**
  This also means the earlier "empirically bisected safe range for raw
  moves" no longer bounds M1 itself, but the underlying lesson for M4/M5
  stands and matters more there: **tile streaming will need to physically
  place real terrain geometry at real raw coordinates** (unlike M1's
  label-only trick), so it still must keep each loaded tile's position
  expressed relative to the *current* floating-origin shift as the origin
  re-centers around the player, not as a growing raw offset from a fixed
  world zero, and should expect the same auto-shift-cascade risk if a tile
  is ever placed/moved in one big raw jump rather than through the engine's
  own small-step `Shift()` mechanism.
- This is the riskiest milestone technically — validate thoroughly before
  scaling tile coverage outward.

**M5 — Scale terrain streaming toward whole-Earth coverage**
- Generalize the pipeline to tile arbitrary bounding boxes; start expanding
  coverage region by region (this is where "all of it, like Sailaway"
  becomes a coverage/compute/storage problem rather than an architecture
  problem — tackled incrementally, region by region, after M4 is solid).
- Ocean (Crest) tiling/scale validated at the larger extents.

**M6 — Navigation & polish**
- Real astronomical ephemeris for celestial navigation tied to true lat/lon
  + time.
- Save-game schema for real-world position (old saves are incompatible by
  necessity — positions mean something fundamentally different).
- Ports/economy placement at real coastal towns (from OSM points) — content
  scope to be decided once the terrain pipeline is stable.

## Verification approach (per milestone)

- Each milestone ships as a buildable BepInEx plugin DLL that can be dropped
  into the live install and played, not just unit-tested in isolation.
- M1: debug HUD comparing in-game lat/lon to known real-world reference
  coordinates for existing landmarks you sail to.
- M2/M3: log wind samples over a sailing session and compare against the
  source climatology/API for the same coordinates/time.
- M4/M5: visually confirm streamed coastline shape against a real map for
  the pilot region before expanding coverage.

## Next step

M0 and M1 are both done (see their sections above). M2 (real wind,
historical mode) is next.
