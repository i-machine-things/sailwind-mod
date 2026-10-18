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
