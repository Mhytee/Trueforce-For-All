#Requires -RunAsAdministrator
# 2b-arm-safety.ps1 - dead-man's switch so a bad driver can't lock you out.
#
# It (1) enables auto-login so a desktop session reaches the guard with NO
# input, and (2) schedules tffa-fakegame --guard at logon. After you reboot
# into the driver:
#   * USB works  -> the guard window appears; press any key to KEEP the driver.
#   * USB is DEAD -> you can't press anything; after the countdown the guard
#                    strips the filter and reboots, restoring USB automatically.
#
# Auto-login needs the login password (or a passwordless account). On a
# throwaway test rig the simplest is to remove the account password entirely.
#
#   .\2b-arm-safety.ps1                       # passwordless account
#   .\2b-arm-safety.ps1 -Password 'hunter2'   # account with a password
#   .\2b-arm-safety.ps1 -Timeout 90

param(
    [int]$Timeout = 60,
    [string]$User = $env:USERNAME,
    [string]$Password
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'tffa-fakegame.exe'
if (-not (Test-Path $exe)) { throw "tffa-fakegame.exe not found in $PSScriptRoot - copy the whole kit folder." }

# 1) Auto-login (so the guard runs without needing a keyboard at the lock screen).
$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty $winlogon -Name AutoAdminLogon -Value "1"
Set-ItemProperty $winlogon -Name DefaultUserName -Value $User
Set-ItemProperty $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
if ($PSBoundParameters.ContainsKey('Password')) {
    Set-ItemProperty $winlogon -Name DefaultPassword -Value $Password
} else {
    Remove-ItemProperty $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue
    Write-Host "No -Password given: assuming '$User' has NO password. If it does, auto-login" -ForegroundColor Yellow
    Write-Host "will fail and the guard won't run - re-run with -Password, or clear the password." -ForegroundColor Yellow
}

# 2) Logon task that runs the guard elevated on the interactive desktop.
$action    = New-ScheduledTaskAction -Execute $exe -Argument "--guard --timeout $Timeout"
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId $User -RunLevel Highest -LogonType Interactive
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(10))
Register-ScheduledTask -TaskName 'TFFA-Guard' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host ""
Write-Host "Safety guard ARMED (dead-man timer ${Timeout}s)." -ForegroundColor Green
Write-Host "Now reboot into the driver. If USB dies, DO NOTHING - it self-heals and reboots."
Write-Host "To disarm later: .\2c-disarm-safety.ps1"
