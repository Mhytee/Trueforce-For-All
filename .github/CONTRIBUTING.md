# Contributing

Contributions are welcome. The project is small and pre-1.0 so most of
the conventions below are guidance, not rules.

## Reporting bugs

Hardware-dependent bugs are nearly impossible to debug without context. The
easiest way to provide it: in the plugin, open the **Settings** tab and press
**Report an issue...** at the bottom. It offers to zip your logs to your
Desktop, then opens a pre-filled issue carrying the plugin and SimHub
versions, your wheel, the active game and car, and the USB / telemetry
status; drag the zip in afterwards. **Export logs...** sits beside it if you
only want the zip.

If you'd rather file manually, include:

- SimHub version and plugin version
- Wheel model + USB ID (the plugin's Status section, or G HUB / USBPcap)
- Game and version
- Whether G HUB was running
- USBPcap version
- Relevant lines from `Logs\SimHub.txt`

## Submitting a change

- Open an issue first if you're planning a non-trivial change, so we
  can agree on scope before you sink time into it. Small fixes and
  obvious improvements are fine to PR directly.
- Match existing style; no formal style guide.
- The plugin targets `net48` (SimHub is 32-bit) and the audio helper
  targets `net8.0`. Don't introduce dependencies that pull in heavy
  transitive libs.
- If you have a supported wheel, please test on real hardware before
  opening the PR.
- Default presets are tuned values exported from real driving sessions.
  If you're proposing changes to a default preset, mention which car /
  track / driving conditions you tuned it under.

## Building locally

You'll need .NET 8 SDK.

```powershell
dotnet build src\TrueforceForAll.Plugin\TrueforceForAll.Plugin.csproj -c Release
dotnet publish src\TrueforceForAll.LoopbackHelper\TrueforceForAll.LoopbackHelper.csproj -c Release -r win-x64
```

The plugin csproj resolves SimHub assemblies via `$(SimHubPath)`, defaulting to `C:\Program Files (x86)\SimHub`. Override with `-p:SimHubPath="..."` if SimHub lives elsewhere. Deploying by hand means copying THREE DLLs into the SimHub install folder: `User.TrueforceForAll.dll`, `TrueforceForAll.Core.dll`, and `TrueforceForAll.Engine.dll`. All three matter: a missing `TrueforceForAll.Engine.dll` means the plugin silently fails to load, and a stale `TrueforceForAll.Core.dll` means a silently dead wheel. The full release build (with Inno Setup installer) is documented in [RELEASING.md](../docs/RELEASING.md), which is maintainer-facing.

Unit tests for the Core and Engine libraries (telemetry parsing, synthesis math, the effect and resampler suites, the golden-fixture replay parity gate) live in `src/TrueforceForAll.Core.Tests`. The test project is intentionally not in `TrueforceForAll.sln` (it targets net8.0 and would perturb the net48 plugin build); run it directly with `dotnet test src/TrueforceForAll.Core.Tests`. Please run it before submitting changes that touch `TrueforceForAll.Core` or `TrueforceForAll.Engine`.

## Adding a new effect

Effects are small synth voices mixed into the haptic stream. Adding one
touches several files; the quickest path is to copy an existing effect end
to end. `RevLimiterEffect` is a good template (telemetry-driven, with its own
settings section). Grep the codebase for `RevLimiter` to find every spot you
need to mirror; it spans Core, Engine and Plugin, though some of those hits
are its own math and car-data files rather than wiring you have to repeat. A
new effect touches about a dozen files: `ImplementThud`, the most recent one,
touches fourteen (eleven under `src/`, plus a built-in preset, the dash
generator and the djson it regenerates). Note the dash calls it `ImplThud`, so
grepping the effect's full name alone will miss those. The steps:

1. **Effect class** in `src/TrueforceForAll.Engine/Effects/` (effects live in
   the Engine assembly now, not the Plugin). Extend `TelemetryEffect`,
   implement `OnTelemetry(TelemetryFrame)` to update state and
   `RenderAdd(float[], int)` to synth into the buffer. Expose `Name`,
   `IsActive`, and (if it ducks other voices or should be duckable)
   `ActivityLevel`. See `src/TrueforceForAll.Engine/Effects/RevLimiterEffect.cs`.
2. **Settings class** in `TrueforceSettings.cs`: a `XxxSettings` class with the
   tunables (`Enabled`, `Gain`, plus effect-specific fields). Add a slot for it
   in three places: the global `TrueforceSettings.Xxx`, the preset snapshot
   class, and the `CarOverride` class (the override slot stays nullable, "use
   global", so existing presets need no migration). Choose the `Enabled`
   default per effect (default-off is the safe baseline since it won't change
   wheel feel on upgrade until the user sees the badge; broadly-wanted effects
   can ship on).
3. **Classify it for backup** in `BackupProjection.cs`: add the settings
   block's name to the per-effect portable list. The projection is an
   allowlist, so a top-level settings field nobody classifies is quietly left
   out of cloud backup and sync, and the startup audit that would catch it
   only runs with dev mode unlocked.
4. **Wire it into the plugin** (`TrueforcePlugin.cs`): construct the effect and
   add it to the `_effects` array (it is then fanned out for `OnTelemetry` and
   mixed automatically); add an `ActiveXxx` accessor
   (`GetActiveCarOverride()?.Xxx ?? Settings.Xxx`); copy settings into the live
   effect where the other effects are applied; and add it to the clone and
   equality/dirty helpers so Save / Revert track it.
5. **UI section** in `SettingsControl.xaml` plus handlers in the `.cs`: a
   collapsible section bound in `RefreshFromPlugin` and written back on change.
   Copy the RevLimiter section.
6. **Register the three enums** (keep them in sync, this is the easy step to
   miss): `EffectKind` in `SettingsControl.xaml.cs`, and `EffectField` plus
   `SectionKind` in `TrueforcePlugin.cs`.
7. **Badge and changelog**: add the effect ID to
   `EffectChangelog.KnownEffectIds`, that alone fires the per-section NEW badge
   on upgrade. Optionally mirror the release notes into a `ChangelogVersion`
   for the offline changelog, setting `EffectId` on the new-effect entry (see
   [RELEASING.md](../docs/RELEASING.md) step 3).
8. **Dash and preset preview**: add a `DashFx` row in
   `TrueforcePlugin.DashRemote.cs`, plus its key in `DashFxDisplayOrder` and
   the per-game `DashFxSupported` switch, so the effect reaches the TF4ALL
   dash (the dash's own row list lives in `dashboards/make-tf4all-dash.ps1`,
   which generates the djson; edit the script, never the djson). Then add an
   `AddSection` line in `PresetPreviewWindow.cs` so the effect shows when
   someone previews a preset.
9. **Ducking (optional)**: if the airborne coordinator or sidechain should
   affect it, add a `DuckXxx` flag to `AirborneSettings` and honor it where the
   other voices are ducked.

## License

By submitting a change, you agree that your contribution is licensed
under the same GPL-2.0-only terms as the rest of the project.
