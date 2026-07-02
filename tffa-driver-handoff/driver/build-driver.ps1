# build-driver.ps1 - one-shot repeatable build for TFFAUsbFilter (the content-based USB FFB filter).
#
# Why this script exists: building a KMDF driver from a winget-installed WDK 26100 on VS 2022
# needs four non-obvious flags. Each is documented below so the recipe isn't tribal knowledge.
#
# Prereqs (install once, all via winget in an ELEVATED terminal):
#   winget install Microsoft.WindowsWDK.10.0.26100        # kernel toolset + km headers
#   winget install Microsoft.WindowsSDK.10.0.26100        # full SDK (shared\usbdi.h etc.)
#   VS Installer -> add component "Component.Microsoft.Windows.DriverKit"   # registers the VS toolset
#
# Output: x64\Release\TFFAUsbFilter\  ->  .sys  .inf  .cat (test-signed)  and  ..\TFFAUsbFilter.cer
#
# Usage:  powershell -ExecutionPolicy Bypass -File build-driver.ps1 [-Config Release]

param(
    [ValidateSet('Release','Debug')]
    [string]$Config = 'Release',
    [string]$Project = 'TFFAUsbFilter\TFFAUsbFilter.vcxproj'
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

# Locate the 64-bit MSBuild via vswhere.
# WHY 64-bit: the InfVerif build task P/Invokes a native InfVerif.dll from a host-arch subdir.
# WDK 26100 ships only x64/arm64 tools (Microsoft dropped x86 host tools in 24H2). The default
# 32-bit MSBuild.exe looks for the nonexistent x86\InfVerif.dll and fails; amd64\MSBuild.exe finds x64.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath  = & $vswhere -latest -property installationPath
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "64-bit MSBuild not found at $msbuild" }

Write-Host "Building $Project ($Config|x64) with $msbuild" -ForegroundColor Cyan

# -p:SpectreMitigation=false  -> dev/test build; KMDF forces Spectre on and we have no Spectre libs.
#                                Turn this ON (remove the override) for a shipping build.
# -p:WindowsTargetPlatformVersion=10.0.26100.0 -> pin the SDK/WDK version; unpinned it resolves to a
#                                bogus "10.0" and the km headers (ntddk.h) vanish from the include path.
& $msbuild $Project `
    -p:Configuration=$Config `
    -p:Platform=x64 `
    -t:Rebuild -nologo -m `
    -p:SpectreMitigation=false `
    -p:WindowsTargetPlatformVersion=10.0.26100.0

if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$out = Join-Path $PSScriptRoot "TFFAUsbFilter\x64\$Config\TFFAUsbFilter"
Write-Host "`nBuild OK. Package:" -ForegroundColor Green
Get-ChildItem $out -Include *.sys,*.inf,*.cat -Recurse | ForEach-Object { Write-Host "  $($_.FullName)" }
Write-Host "  $(Join-Path (Split-Path $out) 'TFFAUsbFilter.cer')  (test cert to import in the VM)"
