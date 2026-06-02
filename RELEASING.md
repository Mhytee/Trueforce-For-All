# Releasing

Maintainer notes for cutting a new version of Trueforce For All.

Releases are built **locally**. The plugin csproj references SimHub's
redistributable DLLs by hint path under `$(SimHubPath)`, so a CI runner
without SimHub installed can't compile it. There is no GitHub Actions
workflow for releases. The build, tag, and draft-release flow below is the
whole process.

The csproj `<Version>` is the single source of truth for the release. It
populates the assembly version, which is what the in-panel header readout,
the diagnostics block, the changelog dialog, and the auto-updater all read
at runtime. The installer build picks it up via the `TRUEFORCEFORALL_VERSION`
environment variable (step 6 below); set that to the same value.

For each release:

1. Bump `<Version>X.Y.Z</Version>` in
   `src/TrueforceForAll.Plugin/TrueforceForAll.Plugin.csproj`. This drives
   the assembly version, the in-panel header readout, the auto-updater's
   "current version," and the User-Agent it sends to GitHub.
2. Update `README.md` if any user-visible feature changed (especially the
   supported-games or wheels tables, install steps, known limitations).
3. Changelog / What's new:
   - The **GitHub release notes are the canonical "What's new" source.** The
     in-app What's-new modal fetches and renders the published release body
     (RenderReleaseNotes), so notes can be fixed post-release without a plugin
     update. A normal fix/hotfix release needs **no** `EffectChangelog` entry.
   - `EffectChangelog.cs` has two separate jobs:
     - **Badge registry** (`KnownEffectIds`): when the release adds a new
       effect, append its ID here (append-only, match
       `TrueforcePlugin.SectionKind` names). This is what fires the per-section
       "NEW" badge on upgrade. Required for a new effect, irrelevant otherwise.
     - **Offline changelog** (`Versions`): the structured fallback rendered when
       the GitHub notes can't be fetched. Optional. To populate it, just mirror
       the release's GitHub notes into a `ChangelogVersion` (one `ChangelogEntry`
       per note); set `EffectId` on any entry that is a new effect so it also
       fires the badge.
   - A new effect's `Enabled` default is a case-by-case call. Default-off is
     the safe baseline (the NEW badge surfaces it without changing how the
     wheel feels on upgrade); an effect most users will clearly want can ship
     on instead (the rev limiter does). Either way, keep its `CarOverride` slot
     nullable (= use global) so existing presets and per-car overrides inherit
     the chosen default with no migration.
4. Hardware-validate any new telemetry source or game-detection change on the
   rig before tagging.
5. Commit the version bump (plus any README / changelog changes) to `main`
   and push it.
6. Build the installer locally. `TRUEFORCEFORALL_VERSION` must be set to the
   release version before invoking `iscc`; the Inno Setup script reads it at
   compile time and falls back to `0.1.0-dev` (which ends up in Add/Remove
   Programs and the installer filename) when it's empty:

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
7. Create a **draft** GitHub release targeting the version-bump commit on
   `main`, and upload the installer:

   ```powershell
   gh release create vX.Y.Z installer\output\TrueforceForAll-Setup.exe `
       --draft --target main --title "vX.Y.Z: <description>" --notes-file notes.md
   ```

   Title and notes conventions (consistent since v0.1.19):
   - **Title:** `vX.Y.Z: <description>`. Description is sentence case (first
     word plus proper nouns / acronyms capitalized, the rest lowercase, e.g.
     "Diagnostics improvements", "Xbox G923 FFB resolver Hotfix"). Capitalize
     "Hotfix". Keep the `vX.Y.Z:` prefix even though GitHub also shows the tag.
   - **Body:** group notes under markdown section headers (`### Bug fixes`,
     `## Diagnostics`, and so on), never a bare bullet list; the in-app modal
     renders `###` / `##` as gold section headers. Lead each bullet with a
     bold one-line summary, then the detail.
   - Don't use `--generate-notes`; write the notes by hand to these conventions.
8. On GitHub, open the draft, give the notes a final read, tick "Set as the
   latest release," and Publish. Until you publish, the auto-updater and the
   in-app What's-new won't see it (`/releases/latest` and the modal both skip
   drafts).
9. After publishing, reload the plugin in SimHub on a test machine to confirm
   the update banner appears and the installer downloads, and that any
   new-effect badges plus the "What's new" banner surface as expected.
