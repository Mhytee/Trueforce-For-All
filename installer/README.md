# Installer

Inno Setup script and supporting files that produce `TrueforceForAll-Setup.exe`,
the user-facing installer.

## What it produces

A single setup `.exe` that:

1. Detects SimHub via its Inno-Setup uninstall registry key (`{019253FE-...}_is1`),
   with fallbacks (registry `DisplayName` scan, then default install path).
   No SimHub on this PC → dialog with link to `https://www.simhubdash.com/`,
   abort install.
2. Copies the plugin DLL, helper exe, and shared deps into SimHub's install dir.
3. If USBPcap isn't already installed, runs the bundled `USBPcapSetup.exe`
   silently, then registers the `NonStandardHWIDs` key (`USBPcapCMD -I`) so
   USB 3.0 ports are captured too.

The user never picks an install path — it's locked to wherever SimHub lives.

## How releases get built

The GitHub Actions workflow at [.github/workflows/release.yml](../.github/workflows/release.yml)
runs on `v*` tag pushes and:

1. Builds `TrueforceForAll.Plugin` (`net48`, AnyCPU, Release).
2. Publishes `TrueforceForAll.LoopbackHelper` (self-contained, single-file,
   `win-x64`, Release).
3. Downloads the bundled USBPcap installer to `installer/vendor/USBPcapSetup.exe`.
4. Compiles `TrueforceForAll.iss` with the GitHub Actions runner's preinstalled
   Inno Setup 6.
5. Uploads the resulting `TrueforceForAll-Setup.exe` to a draft GitHub release.

## Building locally

You'd need Inno Setup 6+ (`iscc.exe`) and the bundled USBPcap installer
in place. From the repo root:

```powershell
dotnet build src\TrueforceForAll.Plugin\TrueforceForAll.Plugin.csproj -c Release
dotnet publish src\TrueforceForAll.LoopbackHelper\TrueforceForAll.LoopbackHelper.csproj -c Release -r win-x64
# Drop USBPcapSetup-1.5.4.0.exe (or current) into installer\vendor\USBPcapSetup.exe
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\TrueforceForAll.iss
```

Output goes to `installer\output\TrueforceForAll-Setup.exe`.

## What's bundled

| Component | Source | License |
|---|---|---|
| Plugin DLL + helper exe | This repo | GPL-2.0 (see [../LICENSE](../LICENSE)) |
| HidSharp, NAudio | NuGet, copied from plugin build output | (Apache 2.0 / MIT) |
| USBPcap setup | `installer/vendor/USBPcapSetup.exe`, built by Tomasz Moń | BSD 2-Clause (see [USBPcap-LICENSE.txt](USBPcap-LICENSE.txt)) |

The bundled USBPcap version is pinned per release; we don't track upstream
USBPcap releases. USBPcap is a low-churn project and the user-mode CLI we
depend on hasn't changed materially in years.
