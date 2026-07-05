#Requires -RunAsAdministrator
# 3-uninstall.ps1 - remove the USB class filter and delete the driver package.
# The FIRST step (removing the UpperFilter) is the important one - it returns USB to normal.
param([string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'Continue'

$inf = Join-Path $Dir 'TFFAUsbFilter.inf'

# 1) Remove the class UpperFilter + service (INF DefaultUninstall). This is the safety step.
if (Test-Path $inf) {
    Write-Host "Removing UpperFilter + service..." -ForegroundColor Cyan
    rundll32.exe SetupApi.dll,InstallHinfSection DefaultUninstall.NTamd64 132 $inf
}

# 2) Belt-and-suspenders: strip our name from the USB class UpperFilters list directly.
$key = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36FC9E60-C465-11CF-8056-444553540000}"
try {
    $uf = (Get-ItemProperty -Path $key -Name UpperFilters -ErrorAction Stop).UpperFilters
    $new = @($uf | Where-Object { $_ -ne 'TFFAUsbFilter' })
    Set-ItemProperty -Path $key -Name UpperFilters -Value $new
    Write-Host "UpperFilters cleaned: $($new -join ',')"
} catch { Write-Host "UpperFilters already clean." }

# 3) Delete the published driver package (find our oemN.inf by original name).
$published = $null; $cur = $null
foreach ($line in (pnputil /enum-drivers)) {
    if     ($line -match 'Published Name\s*:\s*(oem\d+\.inf)')      { $cur = $matches[1] }
    elseif ($line -match 'Original Name\s*:\s*TFFAUsbFilter\.inf')  { $published = $cur }
}
if ($published) {
    Write-Host "Deleting package $published ..." -ForegroundColor Cyan
    pnputil /delete-driver $published /uninstall /force
} else {
    Write-Host "No published TFFAUsbFilter package found (already removed)."
}

Write-Host ""
Write-Host "Done. REBOOT to fully detach the filter." -ForegroundColor Green
Write-Host "When finished testing, also re-enable Secure Boot + Memory Integrity, and:  bcdedit /set testsigning off"
