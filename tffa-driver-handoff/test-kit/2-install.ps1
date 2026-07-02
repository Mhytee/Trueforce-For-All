#Requires -RunAsAdministrator
# 2-install.ps1 - trust the test cert, unmask the driver's logs, install the filter.
# Run ELEVATED, from the folder that holds TFFAUsbFilter.inf/.sys/.cat/.cer.
param([string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'

$inf = Join-Path $Dir 'TFFAUsbFilter.inf'
$cer = Join-Path $Dir 'TFFAUsbFilter.cer'
if (-not (Test-Path $inf)) { throw "TFFAUsbFilter.inf not found in $Dir - copy the whole kit folder over, don't just copy the scripts." }

# Guard: test-signing must be on or the driver silently won't load.
if (-not ((bcdedit /enum "{current}") -match 'testsigning\s+Yes')) {
    throw "Test-signing is not ON. Run 1-check-rig.ps1 and fix the toggles first."
}

# Make our DbgPrintEx (IHVDRIVER) output visible in DebugView. Applies after reboot.
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Debug Print Filter" `
    /v IHVDRIVER /t REG_DWORD /d 0xFFFFFFFF /f | Out-Null

# Trust the self-signed test cert (both stores) so the catalog validates.
if (Test-Path $cer) {
    certutil -addstore -f Root $cer | Out-Null
    certutil -addstore -f TrustedPublisher $cer | Out-Null
    Write-Host "Test cert trusted (Root + TrustedPublisher)." -ForegroundColor Green
} else {
    Write-Warning "TFFAUsbFilter.cer not found - install may warn about an untrusted publisher."
}

# Stage + install the driver package.
Write-Host "Installing driver..." -ForegroundColor Cyan
pnputil /add-driver $inf /install

Write-Host ""
Write-Host "Installed. Now REBOOT so the USB class filter attaches." -ForegroundColor Green
Write-Host "After reboot:" -ForegroundColor Cyan
Write-Host "  1. Run DebugView.exe as admin."
Write-Host "  2. Capture menu -> tick 'Capture Kernel' AND 'Enable Verbose Kernel Output'."
Write-Host "  3. Plug in the G923. Watch for lines starting 'TFFAUsbFilter:'."
Write-Host "     - 'EvtDeviceAdd ... attached' on the wheel = filter is live."
Write-Host "     - Wheel still enumerates in Device Manager = safe. (Stage A pass.)"
