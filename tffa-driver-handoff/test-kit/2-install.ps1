#Requires -RunAsAdministrator
# 2-install.ps1 - trust cert, ARM self-heal, install the filter, then a LIVE
# dead-man's-switch that covers the install MOMENT itself (USB died on install
# last time, before any reboot). PowerShell keeps running even when USB dies,
# so this script watches for a keypress right after install and auto-removes
# the filter + reboots if none comes.
#
#   .\2-install.ps1                    # passwordless account
#   .\2-install.ps1 -Password 'x'      # account with a password (for reboot guard)
#   .\2-install.ps1 -GuardTimeout 90

param([string]$Password, [int]$GuardTimeout = 60, [string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'

$inf = Join-Path $Dir 'TFFAUsbFilter.inf'
$cer = Join-Path $Dir 'TFFAUsbFilter.cer'
$usbClass = "HKLM\SYSTEM\CurrentControlSet\Control\Class\{36FC9E60-C465-11CF-8056-444553540000}"
if (-not (Test-Path $inf)) { throw "TFFAUsbFilter.inf not found in $Dir - copy the whole kit folder." }
if (-not ((bcdedit /enum "{current}") -match 'testsigning\s+Yes')) { throw "Test-signing not ON. Run 1-check-rig.ps1 first." }

function Restore-Usb {
    Write-Host "`n  Stripping the filter from the USB class and rebooting to restore USB..." -ForegroundColor Yellow
    reg delete "$usbClass" /v UpperFilters /f | Out-Null
    shutdown /r /t 5 /c "TFFA: filter removed, restoring USB" | Out-Null
}

# Make our DbgPrintEx logs visible in DebugView (after reboot).
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Debug Print Filter" `
    /v IHVDRIVER /t REG_DWORD /d 0xFFFFFFFF /f | Out-Null

# Trust the self-signed test cert.
if (Test-Path $cer) {
    certutil -addstore -f Root $cer | Out-Null
    certutil -addstore -f TrustedPublisher $cer | Out-Null
    Write-Host "Test cert trusted (Root + TrustedPublisher)." -ForegroundColor Green
}

# 1) Arm the REBOOT-time guard FIRST. Covers the worst case: if pnputil hangs
#    and you have to hard-power-off, the next boot self-heals before you're stuck.
Write-Host "Arming reboot-time self-heal..." -ForegroundColor Cyan
$armArgs = @{ Timeout = $GuardTimeout }
if ($PSBoundParameters.ContainsKey('Password')) { $armArgs['Password'] = $Password }
& (Join-Path $Dir '2b-arm-safety.ps1') @armArgs

# 2) Install. If USB dies at this instant, PowerShell keeps running -> the live
#    guard below catches it without any reboot needed.
Write-Host "`nInstalling driver - WATCH YOUR KEYBOARD/MOUSE..." -ForegroundColor Cyan
pnputil /add-driver $inf /install

# 3) LIVE dead-man's-switch for the install moment.
Write-Host "`n==================================================================" -ForegroundColor Yellow
Write-Host "  Driver installed. IS YOUR KEYBOARD STILL WORKING?" -ForegroundColor Yellow
Write-Host "  Press ANY KEY within $GuardTimeout s to KEEP the driver." -ForegroundColor Yellow
Write-Host "  If USB just died, do NOTHING - it auto-removes and reboots." -ForegroundColor Yellow
Write-Host "==================================================================" -ForegroundColor Yellow

$deadline = (Get-Date).AddSeconds($GuardTimeout)
$kept = $false
while ((Get-Date) -lt $deadline) {
    try { if ([Console]::KeyAvailable) { [Console]::ReadKey($true) | Out-Null; $kept = $true; break } } catch { }
    $left = [math]::Ceiling(($deadline - (Get-Date)).TotalSeconds)
    Write-Host -NoNewline ("`r  Auto-removing in {0,3} s ... press any key to keep.   " -f $left)
    Start-Sleep -Milliseconds 200
}

if ($kept) {
    Write-Host "`n`nKEY DETECTED - USB works, keeping the driver." -ForegroundColor Green
    Write-Host "The filter fully engages after a REBOOT. Reboot when ready; the reboot-time"
    Write-Host "guard will ask you to press a key again (that's your USB check for the boot path)."
    Write-Host "Then: DebugView (admin, Capture Kernel + verbose), plug in the G923, watch 'TFFAUsbFilter:'."
} else {
    Restore-Usb
}
