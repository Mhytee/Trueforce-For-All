# Step 1 of 2 for driver test-signing: create the self-signed code-signing cert.
#
# This is the "Step 1 (cert creation)" that enable-test-signing.ps1 (Step 2)
# expects to have run first. It creates a code-signing certificate whose
# subject is exactly "CN=TrueforceForAll Dev" in the current user's personal
# store. Step 2 then trusts that cert machine-wide and turns on test-signing.
#
# Reconstructed from the requirements in enable-test-signing.ps1 (the original
# Step 1 script was not committed). Run from a normal (non-admin) PowerShell
# window; cert creation in CurrentUser\My does not need elevation. Step 2 does.

$ErrorActionPreference = 'Stop'

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=TrueforceForAll Dev' } |
    Select-Object -First 1
if ($existing) {
    Write-Host "Cert already exists. Thumbprint: $($existing.Thumbprint)  Expires: $($existing.NotAfter)" -ForegroundColor Yellow
    Write-Host "Nothing to do. Run enable-test-signing.ps1 (as admin) next." -ForegroundColor Yellow
    return
}

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject 'CN=TrueforceForAll Dev' `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5)

Write-Host "Created code-signing cert." -ForegroundColor Green
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Expires:    $($cert.NotAfter)"
Write-Host ""
Write-Host "Next: open an ADMIN PowerShell window and run enable-test-signing.ps1, then reboot." -ForegroundColor Cyan
