# Contributing

Contributions are welcome. The project is small and pre-1.0 so most of
the conventions below are guidance, not rules.

## Reporting bugs

Hardware-dependent bugs are nearly impossible to debug without context. The
easiest way to provide it: in the plugin, open the **Feedback** section, click
**Report an issue**, then **Export Logs**, and attach the zip to your GitHub
issue. It bundles your logs, the plugin's settings, your wheel / game / SimHub
versions, and the USB / telemetry status automatically.

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

The plugin csproj resolves SimHub assemblies via `$(SimHubPath)`, defaulting to `C:\Program Files (x86)\SimHub`. Override with `-p:SimHubPath="..."` if SimHub lives elsewhere. Drop the build outputs into your SimHub install folder and reload the plugin in SimHub. The full release build (with Inno Setup installer) is documented in [RELEASING.md](RELEASING.md), which is maintainer-facing.

## Adding a new effect

Effects are small synth voices mixed into the haptic stream. Adding one
touches several files; the quickest path is to copy an existing effect end
to end. `RevLimiterEffect` is a good template (telemetry-driven, with its own
settings section). Grep the codebase for `RevLimiter` to find every spot you
need to mirror, it currently appears in roughly eight files. The steps:

1. **Effect class** in `src/TrueforceForAll.Plugin/Effects/`. Extend
   `TelemetryEffect`, implement `OnTelemetry(TelemetryFrame)` to update state
   and `RenderAdd(float[], int)` to synth into the buffer. Expose `Name`,
   `IsActive`, and (if it ducks other voices or should be duckable)
   `ActivityLevel`. See `Effects/RevLimiterEffect.cs`.
2. **Settings class** in `TrueforceSettings.cs`: a `XxxSettings` class with the
   tunables (`Enabled`, `Gain`, plus effect-specific fields). Add a slot for it
   in three places: the global `TrueforceSettings.Xxx`, the preset snapshot
   class, and the `CarOverride` class (the override slot stays nullable, "use
   global", so existing presets need no migration). Choose the `Enabled`
   default per effect (default-off is the safe baseline since it won't change
   wheel feel on upgrade until the user sees the badge; broadly-wanted effects
   can ship on).
3. **Wire it into the plugin** (`TrueforcePlugin.cs`): construct the effect and
   add it to the `_effects` array (it is then fanned out for `OnTelemetry` and
   mixed automatically); add an `ActiveXxx` accessor
   (`GetActiveCarOverride()?.Xxx ?? Settings.Xxx`); copy settings into the live
   effect where the other effects are applied; and add it to the clone and
   equality/dirty helpers so Save / Revert track it.
4. **UI section** in `SettingsControl.xaml` plus handlers in the `.cs`: a
   collapsible section bound in `RefreshFromPlugin` and written back on change.
   Copy the RevLimiter section.
5. **Register the three enums** (keep them in sync, this is the easy step to
   miss): `EffectKind` in `SettingsControl.xaml.cs`, and `EffectField` plus
   `SectionKind` in `TrueforcePlugin.cs`.
6. **Badge and changelog**: add the effect ID to
   `EffectChangelog.KnownEffectIds`, that alone fires the per-section NEW badge
   on upgrade. Optionally mirror the release notes into a `ChangelogVersion`
   for the offline changelog, setting `EffectId` on the new-effect entry (see
   [RELEASING.md](RELEASING.md) step 3).
7. **Ducking (optional)**: if the airborne coordinator or sidechain should
   affect it, add a `DuckXxx` flag to `AirborneSettings` and honor it where the
   other voices are ducked.

## License

By submitting a change, you agree that your contribution is licensed
under the same GPL-2.0-only terms as the rest of the project.
