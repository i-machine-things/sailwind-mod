# Sailwind Real Earth

A BepInEx/Harmony mod for the sailing game [Sailwind](https://store.steampowered.com/app/1764530).
**Goal**: replace its fictional archipelago with real-world coastlines/bathymetry and real
wind/weather (historical climatology or a live API, toggleable), in the spirit of *Sailaway*.
Coastlines/bathymetry and real weather are not built yet — see Status below for what's actually
working today.

Full milestone roadmap (M0 harness through M6 navigation/polish) lives in
[`docs/ROADMAP.md`](docs/ROADMAP.md) — that's the source of truth for scope and sequencing. This
file is a snapshot of what currently works.

## Status

- **M0 — mod harness**: done. BepInEx 5 + Harmony loads a custom plugin into the live Proton
  install; `Assembly-CSharp.dll` decompiled to confirm real patch targets.
- **M1 — real coordinates, same small world**: done. The vanilla archipelago is relabeled onto
  shifted real-world-style coordinates (lon/lat) with the world otherwise untouched — no objects
  physically moved, no crashes, normal save/load, normal mission maps. Depends on the community
  mod [`BorderExpander`](https://github.com/NANDbrew/BorderExpander) so sailing near the shifted
  coordinates doesn't trip the vanilla world-border safety net. **Install it and enable
  Settings → Free Sailing in its config** — without that, you'll get bounced back to the dock by
  vanilla's "out of game area" recovery the moment you sail near the shifted coordinates (the mod
  logs a warning on startup if this is missing). See `.claude/CODING_NOTES.md` for the exact
  install steps.
- **M2+ (real wind, real terrain, navigation)**: not started.

## How it works

- `FloatingOriginManager.instance.GetGlobeCoords(transform)` is the game's own built-in lat/lon
  API (`.x` = longitude, `.z` = latitude, 9000 Unity units per degree). The mod doesn't replace
  this — it offsets `FloatingOriginManager.outCurrentOffset` once at boot, which relabels every
  object's reported coordinates uniformly without moving any `Transform`. See
  `.claude/CODING_NOTES.md`'s "Floating origin" section for why a more direct approach (physically
  repositioning islands/player) was tried first and abandoned after it caused a crash.
- An in-game debug overlay (press **F9** to toggle) shows the current lat/lon.

## Building

```
cd mod
dotnet build
```

Requires the .NET SDK (works with no actual .NET Framework or Mono installed — see
`.claude/CODING_NOTES.md`'s "BepInEx plugin build" section). Building auto-deploys the plugin DLL
straight into the live Sailwind install's `BepInEx/plugins/SailwindRealEarth/` folder; there is no
separate install step.

## Layout

- `mod/` — the BepInEx plugin (`Plugin.cs`) and its project file.
- `pipeline/` — (not yet populated) offline tooling that will preprocess geographic/weather data
  into runtime tiles for the terrain-streaming milestones.
- `data/` — (not yet populated) generated tiles the mod ships or downloads on demand; currently
  holds `vanilla_island_survey.json`, a reference dump of all 41 vanilla islands' names and
  real-lat/lon-style coordinates.
- `docs/` — supplementary docs (not yet populated).
- `.claude/CODING_NOTES.md` — confirmed API details, gotchas, and the full root-cause writeups for
  bugs hit along the way. Read this before making further changes.
