#Requires -RunAsAdministrator
# 1-check-rig.ps1 - verify the spare rig is ready to load a test-signed kernel driver.
# Read-only. Changes nothing. Run in an ELEVATED PowerShell.
$ErrorActionPreference = 'Continue'
Write-Host "== TFFA rig readiness ==" -ForegroundColor Cyan

# BitLocker: if ON, changing Secure Boot can demand a recovery key. Want Off/None.
try { $bl = (Get-BitLockerVolume -MountPoint C: -ErrorAction Stop).ProtectionStatus } catch { $bl = '? (need admin)' }
Write-Host ("BitLocker C:            : {0,-8} (want: Off)" -f $bl)

# Secure Boot: must be OFF for test-signing to take effect.
try { $sb = Confirm-SecureBootUEFI } catch { $sb = '?' }
Write-Host ("Secure Boot            : {0,-8} (want: False)" -f $sb)

# Test-signing: lets Windows load our self-signed driver.
$ts = [bool]((bcdedit /enum "{current}") -match 'testsigning\s+Yes')
Write-Host ("Test-signing           : {0,-8} (want: True)" -f $ts)

# Memory Integrity (HVCI): blocks test-signed drivers even when everything else is right.
$p = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"
$hvci = try { (Get-ItemProperty -Path $p -Name Enabled -ErrorAction Stop).Enabled } catch { 0 }
Write-Host ("Memory Integrity (HVCI): {0,-8} (want: 0)" -f $hvci)

Write-Host ""
if ($sb -eq $false -and $ts -and $hvci -eq 0) {
    Write-Host "READY. Run 2-install.ps1 next." -ForegroundColor Green
} else {
    Write-Host "NOT READY - fix the mismatches above:" -ForegroundColor Yellow
    if ($sb -ne $false)  { Write-Host "  * Secure Boot: reboot into UEFI/BIOS -> disable Secure Boot -> save." }
    if ($hvci -ne 0)     { Write-Host "  * Memory Integrity: Windows Security > Device security > Core isolation > Memory integrity -> Off, reboot." }
    if (-not $ts)        { Write-Host "  * Test-signing: (after the two above) run  bcdedit /set testsigning on  then reboot." }
    Write-Host "  Then re-run this script."
}
