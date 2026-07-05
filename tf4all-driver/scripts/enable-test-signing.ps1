# TrueforceForAll driver dev: trust self-signed cert + enable test-signing mode.
# MUST be run from an admin PowerShell window.
#
# After this completes successfully, REBOOT, then look for the "Test Mode"
# watermark in the bottom-right corner of the desktop. If it does not appear,
# Secure Boot is blocking test-signing and must be disabled in BIOS.

$ErrorActionPreference = 'Stop'

# Sanity: must be admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run from an ADMIN PowerShell window." -ForegroundColor Red
    Write-Host "Right-click Windows PowerShell, choose 'Run as administrator', then re-run." -ForegroundColor Red
    exit 1
}

# Locate the self-signed cert created in step 1
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=TrueforceForAll Dev" } | Select-Object -First 1
if (-not $cert) {
    Write-Host "ERROR: Cert 'CN=TrueforceForAll Dev' not found in CurrentUser\My." -ForegroundColor Red
    Write-Host "Step 1 (cert creation) needs to run first. Run create-dev-cert.ps1, then re-run this script." -ForegroundColor Red
    exit 1
}
Write-Host "Found cert. Thumbprint: $($cert.Thumbprint)  Expires: $($cert.NotAfter)" -ForegroundColor Green

# Trust as Root + TrustedPublisher (admin)
$cerPath = Join-Path $env:TEMP "tffa-dev.cer"
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $cerPath -Force
Write-Host "Cert trusted as Root + TrustedPublisher." -ForegroundColor Green

# Enable test signing
$bcdOut = & bcdedit /set testsigning on 2>&1 | Out-String
Write-Host "bcdedit:" -ForegroundColor Cyan
Write-Host $bcdOut

# Report Secure Boot status
try {
    $sb = Confirm-SecureBootUEFI
    Write-Host "SecureBoot is: $sb" -ForegroundColor Yellow
    if ($sb) {
        Write-Host ""
        Write-Host "WARNING: Secure Boot is ENABLED. Test-signing will be silently ignored after reboot." -ForegroundColor Yellow
        Write-Host "After you reboot, if you do NOT see a 'Test Mode' watermark, you'll need to disable" -ForegroundColor Yellow
        Write-Host "Secure Boot in BIOS (usually F2/F10/Del at boot -> Security -> Secure Boot -> Disabled)." -ForegroundColor Yellow
        Write-Host "Heads-up: disabling Secure Boot may trigger BitLocker recovery on next boot - have" -ForegroundColor Yellow
        Write-Host "your BitLocker recovery key ready (Microsoft account -> Devices -> BitLocker keys)." -ForegroundColor Yellow
    }
} catch {
    Write-Host "Could not read SecureBoot state: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "DONE. Reboot now. After reboot, the 'Test Mode' watermark should appear in the" -ForegroundColor Green
Write-Host "bottom-right corner of the desktop. If it does not, Secure Boot is blocking it." -ForegroundColor Green
