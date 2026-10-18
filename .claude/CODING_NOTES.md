# Coding Best Practices & Reminders

> **Style rule:** Notes must be clear and concise — 300 characters or less each. Group by topic, not by date. Whenever a PR review (CodeRabbit or human) catches a mistake, add or amend a note here right away so it isn't repeated.

## General Style Notes

- **Keep lines under 120 characters.** Long lines are hard to review side-by-side in a diff or split editor pane, and tend to signal a line doing too many things at once. Wrap or break up expressions rather than letting them run long.
- **Add docstrings/comments to explain code.** Focus on *why* a function/class exists or *why* it does something non-obvious — the code itself already shows *what* it does. A comment worth writing usually covers intent, assumptions, edge cases, or a gotcha a future reader would otherwise have to rediscover the hard way.

## Proton / Steam

- **BepInEx won't inject under Proton unless Wine is told to prefer the native `winhttp.dll`.** BepInEx's loader hijacks `winhttp.dll` next to the game exe, but Wine's own built-in stub for that name can win over the real file on disk. Fix: add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to Sailwind's Steam launch options. Without it, the game boots fine but `BepInEx/LogOutput.log` never appears.
- **Game install path**: `~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sailwind` (Steam AppID `1764530`, Flatpak Steam). Relaunch via `flatpak run com.valvesoftware.Steam steam://rungameid/1764530`.

## BepInEx plugin build (C#/.NET)

- **SDK-style `.csproj` auto-includes `*.cs` files.** Don't also add an explicit `<Compile Include="...">` item for a file already in the project directory — that's `NETSDK1022: Duplicate 'Compile' items`.
- **`net472` target + `Microsoft.NETFramework.ReferenceAssemblies` NuGet package lets `dotnet build` compile a BepInEx 5 plugin on Linux** with no actual .NET Framework (or Mono) installed — it's just reference metadata for compilation, not a runtime.
- **Reference the game's/BepInEx's actual installed DLLs directly (`<Reference><HintPath>`), not NuGet packages, for `Assembly-CSharp`, `BepInEx`, `0Harmony`, and `UnityEngine.*`.** Guarantees the compiled plugin always matches exactly what's loaded at runtime. This is also what the real Sailwind mods on GitHub (`BorderExpander`, `ChaoticWind`) do.
- **`MonoBehaviour`/`BaseUnityPlugin` needs *both* `UnityEngine.dll` (the legacy facade) and `UnityEngine.CoreModule.dll` referenced** — CoreModule alone throws `CS0012` on `MonoBehaviour`. `OnGUI`/`GUI.*` need a further separate reference, `UnityEngine.IMGUIModule.dll`.

## Sailwind modding ecosystem

- **`FloatingOriginManager.instance.GetGlobeCoords(transform)` is the game's real, built-in lat/lon API** — `.x` = longitude, `.z` = latitude, ~9000 Unity units per degree. Confirmed by reading real shipped mod source (`NANDbrew/BorderExpander`, `Rahakami/scrambled-seas`), not by guessing. Don't build a parallel coordinate system — extend this one.
- **`NANDbrew/BorderExpander` already expands `WorldBorder` to ±70° latitude and implements east/west circumnavigation (antimeridian wraparound).** Depend on it rather than reimplementing border/wraparound logic.

## Floating origin — don't raw-translate objects far from where they already are

- **A direct `Transform.Translate(offset, Space.World)` by real-world-scale distances (~580,000 Unity units) froze the game** (pinned CPU, log stopped progressing) when repositioning all 41 islands onto the Caribbean. `scrambled-seas`'s similar island-randomizer never hits this because it only shuffles islands *within* the existing vanilla lon/lat box (-12/32E, 26/46N) — it never pushes raw Unity coordinates beyond what the engine/physics/Crest-ocean are already tested at.
- **Decompiled `FloatingOriginManager` itself (via ilspycmd) to check whether `RealPosToShiftingPos`/`ShiftingPosToRealPos` would fix this — they don't, by themselves.** Both are just `pos ± outCurrentOffset`, and `outCurrentOffset` starts at `(0,0,0)` and only changes once the game's own `Shift()` has fired at least once (triggered by the *player* drifting past `shiftDistance` from origin). Our reposition runs at world-load time, before any shift has ever happened, so the round trip is mathematically a no-op right there. Applied it anyway (matches the pattern `BorderExpander`'s circumnavigation code uses for moves that *do* happen after a shift), but confirmed by testing that it does **not** raise the safe magnitude on its own.
- **Empirically bisected the actual safe range** (same technique/magnitude, with the round-trip applied, repositioning all 41 islands each time, confirmed live in-game each step):
  - `-32.2 / -8.35` deg (~290000/-75000 units) — fine, confirmed playable.
  - `-48.3 / -12.525` deg (~435000/-113000 units) — fine, confirmed playable.
  - `-56.35 / -14.6` deg (~507000/-131000 units) — noticeably degraded ("struggling"), not a hard freeze.
  - `-64.4 / -16.7` deg (~580000/-150000 units, the original Caribbean target) — froze outright.
  - **This is a progressive degradation with distance, not a single discrete bug** — consistent with general large-coordinate float-precision/physics-engine strain rather than one fixable code path. There is no simple code change that unlocks arbitrary-distance raw repositioning of static objects; reaching genuinely distant real-world targets needs the tile-streaming + floating-origin-driven re-centering architecture (M4/M5 in the plan), not a bigger version of this technique.
- **Mitigation in place:** `RealEarth/RepositionIslands` BepInEx config defaults to a shift (`-32.2/-8.35`) kept well inside the confirmed-safe range with margin, not the original Caribbean target.
