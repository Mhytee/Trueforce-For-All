@echo off
REM ============================================================
REM  Trueforce-For-All plugin install (haptic-engine branch)
REM  Build the solution in Release first, then RIGHT-CLICK this
REM  file -> "Run as administrator".
REM
REM  Copies the THREE plugin DLLs into SimHub. All three matter:
REM  a missing TrueforceForAll.Engine.dll = plugin fails to load.
REM ============================================================
setlocal
set REPO=%~dp0..
set SIMHUB=C:\Program Files (x86)\SimHub
if not exist "%SIMHUB%\SimHubWPF.exe" (
    echo SimHub not found at "%SIMHUB%" - edit SIMHUB= in this script.
    pause
    exit /b 1
)

echo.
echo Stopping SimHub (gracefully first)...
REM Graceful close FIRST: a clean exit sends the wheel a proper Trueforce
REM stop command instead of cutting the 1 kHz stream mid-packet (hard kills
REM mid-stream can wedge the wheel's audio path until a re-init).
taskkill /IM SimHubWPF.exe >nul 2>&1
timeout /t 6 /nobreak >nul
taskkill /IM SimHubWPF.exe /F >nul 2>&1
taskkill /IM TrueforceForAll.LoopbackHelper.exe /F >nul 2>&1
REM Kill any orphaned capture child: an orphan holds the USBPcap device AND
REM SimHub's UDP sockets under a dead PID.
taskkill /IM USBPcapCMD.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

REM All three DLLs are staged into the plugin's own output dir, so copy the
REM matched set from there. The solution build (Release^|x64) lands in
REM bin\x64\Release\net48; a direct `dotnet build` of the plugin project
REM (AnyCPU) lands in bin\Release\net48. Prefer the latter, fall back to the
REM former, so this works after either build.
set PLUGINOUT=%REPO%\src\TrueforceForAll.Plugin\bin\Release\net48
if not exist "%PLUGINOUT%\User.TrueforceForAll.dll" set PLUGINOUT=%REPO%\src\TrueforceForAll.Plugin\bin\x64\Release\net48
if not exist "%PLUGINOUT%\User.TrueforceForAll.dll" (
    echo Build output not found. Build the solution or the plugin project in Release first.
    pause
    exit /b 1
)

echo Copying plugin DLLs from "%PLUGINOUT%"...
copy /Y "%PLUGINOUT%\TrueforceForAll.Core.dll" "%SIMHUB%\TrueforceForAll.Core.dll"
copy /Y "%PLUGINOUT%\TrueforceForAll.Engine.dll" "%SIMHUB%\TrueforceForAll.Engine.dll"
copy /Y "%PLUGINOUT%\User.TrueforceForAll.dll" "%SIMHUB%\User.TrueforceForAll.dll"

echo.
echo Starting SimHub...
start "" "%SIMHUB%\SimHubWPF.exe"

echo.
echo ============================================================
echo  DONE. Look for "1 file(s) copied" THREE times above.
echo  If you see "being used by another process", SimHub didn't
echo  fully close -- close this window and re-run as admin.
echo  Next: docs\TESTER-HANDOFF.md has the full setup + test plan.
echo ============================================================
pause
