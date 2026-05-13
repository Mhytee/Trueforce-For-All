# Releasing

Maintainer notes for cutting a new version of Trueforce For All.

Releases are built **locally**. The plugin csproj references SimHub's
redistributable DLLs by hint path under `$(SimHubPath)`, so a CI runner
without SimHub installed can't compile it. There is no GitHub Actions
workflow for releases — the build + tag + draft-release flow below is
the whole process.

The csproj `<Version>` is the single source of truth for the release. It
populates the assembly version, which is what the in-panel header readout,
the diagnostics block, the changelog dialog, and the auto-updater all read
at runtime. The installer build picks it up via the `TRUEFORCEFORALL_VERSION`
environment variable (step 6 below) — set that to the same value.

For each release:

1. Bump `<Version>X.Y.Z</Version>` in
   `src/TrueforceForAll.Plugin/TrueforceForAll.Plugin.csproj`. This drives
   the assembly version, the in-panel header readout, the auto-updater's
   "current version," and the User-Agent it sends to GitHub.
2. Update `README.md` if any user-visible feature changed (especially the
   supported-games or wheels tables, install steps, known limitations).
3. If this release adds or changes a user-visible effect:
   - Add any new effect IDs to `EffectChangelog.KnownEffectIds` in
     `src/TrueforceForAll.Plugin/EffectChangelog.cs`. IDs are append-only
     and match `TrueforcePlugin.SectionKind` names.
   - Append a new `ChangelogVersion` to `EffectChangelog.Versions` with
     one `ChangelogEntry` per user-visible change. Entries that should
     trigger a per-section "NEW" badge must set `EffectId` to the
     matching ID. Entries without an `EffectId` surface in the banner
     modal only.
   - New effects must default `Enabled = false` on their settings class.
     Their `CarOverride` slot stays nullable (= use global), so existing
     presets and per-car overrides inherit the disabled-by-default
     behavior with no migration. The badge surfaces the new effect; the
     default-off keeps wheel feel stable across the upgrade.
4. Hardware-validate any new telemetry source or game-detection change on
   the rig before tagging.
5. Commit. Tag `vX.Y.Z` matching the csproj version exactly. Push the
   branch and the tag.
6. Build the installer locally. `TRUEFORCEFORALL_VERSION` must be set to
   the release version before invoking `iscc` — the Inno Setup script
   reads it at compile time and falls back to `0.1.0-dev` (which ends up
   in Add/Remove Programs and the installer filename) when it's empty:

   ```powershell
   dotnet build src\TrueforceForAll.Plugin\TrueforceForAll.Plugin.csproj -c Release
   dotnet publish src\TrueforceForAll.LoopbackHelper\TrueforceForAll.LoopbackHelper.csproj -c Release -r win-x64
   # confirm installer\vendor\USBPcapSetup.exe is present
   $env:TRUEFORCEFORALL_VERSION = 'X.Y.Z'  # same value as the csproj <Version>
   # ISCC location varies by install mode: system-wide installs sit under
   # "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"; per-user installs (the
   # default if you opted into "Install for me only") sit under
   # "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe".
   & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\TrueforceForAll.iss
   ```

   The artifact lands at `installer\output\TrueforceForAll-Setup.exe`.
7. Upload to a draft GitHub release:

   ```powershell
   gh release create vX.Y.Z installer\output\TrueforceForAll-Setup.exe `
       --draft --title "Trueforce For All vX.Y.Z" --generate-notes
   ```
8. On GitHub, open the draft release. Edit the auto-generated notes if
   needed. Tick "Set as the latest release." Click Publish. Until you do
   this, the auto-updater will not see it because `/releases/latest`
   skips drafts and pre-releases.
9. After publishing, reload the plugin in SimHub on a test machine to
   confirm the update banner appears and the installer downloads, and
   that any new-effect badges + "What's new" banner surface as expected.
