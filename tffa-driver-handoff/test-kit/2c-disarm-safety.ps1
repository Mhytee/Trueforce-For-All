#Requires -RunAsAdministrator
# 2c-disarm-safety.ps1 - undo 2b: remove the logon guard task and turn off
# auto-login. Run this once you're done testing (before/after 3-uninstall).
$ErrorActionPreference = 'Continue'

schtasks /delete /tn TFFA-Guard /f 2>$null | Out-Null

$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty $winlogon -Name AutoAdminLogon -Value "0"
Remove-ItemProperty $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue

Write-Host "Safety guard disarmed: TFFA-Guard task removed, auto-login off." -ForegroundColor Green
