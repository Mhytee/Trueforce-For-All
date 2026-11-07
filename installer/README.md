# Installer

Inno Setup script and supporting files that produce `TrueforceForAll-Setup.exe`,
the user-facing installer.

## What it produces

A single setup `.exe` that:

1. Detects SimHub via its Inno-Setup uninstall registry key (`{019253FE-...}_is1`),
   with fallbacks (registry `DisplayName` scan, then default install path).
   No SimHub on this PC → dialog with link to `https://www.simhubdash.com/`,
   abort install.
2. Copies the three plugin DLLs (`User.TrueforceForAll.dll`,
   `TrueforceForAll.Core.dll`, `TrueforceForAll.Engine.dll`), the
   LoopbackHelper exe, the LICENSE / EULA / privacy policy, the factory
   presets and the TF4ALL Dash into SimHub's install dir. No third-party
   dependency DLLs are copied, the conditional HidSharp repair copy aside
   (see What's bundled).
3. If USBPcap isn't already installed, runs the bundled `USBPcapSetup.exe`
   silently, then registers the `NonStandardHWIDs` key (`USBPcapCMD -I`) so
   USB 3.0 ports are captured too.
4. Runs two PowerShell helpers: `RegisterPlugin.ps1` registers the plugin in
   SimHub's `PluginsActivation.json` so a fresh install lands enabled and
   pinned to the sidebar (it respects an existing entry, so a disable or a
   hide survives an upgrade), and `PrepareVisibleLaunch.ps1` turns SimHub's
   "Start minimized" off for the post-install launch so SimHub opens visibly.

The user never picks an install path. It's locked to wherever SimHub lives.

## How releases get built

Releases are built **locally** by the maintainer. The full checklist
(version bumps, changelog entries, tagging, draft-release upload) lives in
[../docs/RELEASING.md](../docs/RELEASING.md). There is no CI build. The SimHub
plugin csproj references SimHub's redistributable DLLs by hint path, so
a runner without SimHub installed can't compile the plugin.

## Building locally

You'd need Inno Setup 6+ (`iscc.exe`) and the bundled USBPcap installer
in place. From the repo root:

```powershell
dotnet build src\TrueforceForAll.Plugin\TrueforceForAll.Plugin.csproj -c Release
dotnet publish src\TrueforceForAll.LoopbackHelper\TrueforceForAll.LoopbackHelper.csproj -c Release -r win-x64
# Drop USBPcapSetup-1.5.4.0.exe (or current) into installer\vendor\USBPcapSetup.exe
$env:TRUEFORCEFORALL_VERSION = 'X.Y.Z'  # match the csproj <Version>; iss falls back to 0.1.0-dev when empty
# ISCC location varies by Inno Setup install mode: system-wide installs sit
# under "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"; per-user installs
# under "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe". Both work.
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\TrueforceForAll.iss
```

Output goes to `installer\output\TrueforceForAll-Setup.exe`.

## What's bundled

| Component | Source | License |
|---|---|---|
| Plugin DLLs, helper exe, factory presets, TF4ALL Dash | This repo | GPL-2.0 (see [../LICENSE](../LICENSE)) |
| HidSharp 2.6.4 (repair copy only) | `installer/repair/HidSharp.dll`, written only when the broken 2.1.0 an old installer shipped is still on disk (issue #11) | Apache-2.0 (see [HidSharp-LICENSE.txt](HidSharp-LICENSE.txt)) |
| USBPcap setup | `installer/vendor/USBPcapSetup.exe`, built by Tomasz Moń | BSD 2-Clause (see [USBPcap-LICENSE.txt](USBPcap-LICENSE.txt)) |

Neither HidSharp nor NAudio is bundled as a runtime dependency. Both are
compile-only references: at runtime the net48 plugin binds to SimHub's own
copies in its install root. The one exception is the conditional HidSharp
repair copy in the table above. The net8 LoopbackHelper needs neither, because
it captures through the Windows API and emits raw buffers itself; it publishes
as a single self-contained exe.

The bundled USBPcap version is pinned per release; we don't track upstream
USBPcap releases. USBPcap is a low-churn project and the user-mode CLI we
depend on hasn't changed materially in years.
