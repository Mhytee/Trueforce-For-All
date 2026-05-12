# Launch SimHub and force its main window to the foreground regardless of
# the user's "Start minimized" / "Minimize in tray" preferences. Invoked
# by the installer's Finished-page "Launch SimHub now" action so users
# immediately see the app instead of nothing (window hidden to tray) or
# a taskbar button (window minimized).
#
# Why .NET Process.MainWindowHandle isn't enough:
# With StartMinimized=True and MinimizeInTray=True, SimHub creates its
# main window in a hidden state from the start (Visibility=Hidden ->
# ShowWindow(SW_HIDE) underneath). Process.MainWindowHandle returns
# IntPtr.Zero for hidden windows, so a polling loop that waits for it
# never finds anything. EnumWindows does return hidden top-level
# windows, so we enumerate everything owned by the SimHubWPF pid and
# pick the one whose title looks like the main window.
#
# After we have the handle, the AttachThreadInput dance + a layered
# restore sequence (SC_RESTORE, ShowWindow SW_SHOW, SW_RESTORE,
# ShowWindowAsync, SetWindowPos TOPMOST/NOTOPMOST, BringWindowToTop,
# SetForegroundWindow) walks through every documented foreground-steal
# workaround. A retry pass 800ms later catches SimHub re-applying
# StartMinimized late in its WPF init.
#
# Logs each step to %TEMP%\TfaLaunchSimHub.log for post-mortem
# debugging.
#
# Args:
#   -SimHubExe <path>   Absolute path to SimHubWPF.exe.

param(
    [Parameter(Mandatory = $true)][string]$SimHubExe
)

$ErrorActionPreference = 'Continue'
$logPath = Join-Path $env:TEMP 'TfaLaunchSimHub.log'

function Write-Log([string]$msg) {
    try {
        $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
        Add-Content -Path $logPath -Value "[$stamp] $msg" -ErrorAction SilentlyContinue
    } catch {}
}

Write-Log "Begin. SimHubExe=$SimHubExe"

if (-not (Test-Path $SimHubExe)) {
    Write-Log "SimHubExe not found; exiting."
    exit 0
}

$workingDir = Split-Path -Parent $SimHubExe

try {
    Start-Process -FilePath $SimHubExe -WorkingDirectory $workingDir -WindowStyle Normal | Out-Null
    Write-Log "Start-Process issued for SimHubWPF."
} catch {
    Write-Log "Start-Process threw: $($_.Exception.Message)"
    exit 0
}

# Visible "Launching SimHub..." progress window. SimHub's cold start takes
# several seconds on most rigs (more on first-run icon-cache scans), and
# without this users see no visible feedback at all between clicking
# Finish and SimHub's window appearing. Window is topmost + fixed-dialog
# so it can't be lost behind other apps and reads as a system status
# rather than a regular window the user might dismiss. Closed once the
# foreground dance below finishes.
$splash = $null
try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    $splash = New-Object System.Windows.Forms.Form
    $splash.Text            = 'Launching SimHub...'
    $splash.Size            = New-Object System.Drawing.Size(420, 130)
    $splash.StartPosition   = 'CenterScreen'
    $splash.FormBorderStyle = 'FixedDialog'
    $splash.MaximizeBox     = $false
    $splash.MinimizeBox     = $false
    $splash.ControlBox      = $false
    $splash.TopMost         = $true
    $splash.ShowInTaskbar   = $false
    $splash.BackColor       = [System.Drawing.Color]::FromArgb(0x2A, 0x2A, 0x2A)
    $splash.ForeColor       = [System.Drawing.Color]::FromArgb(0xEA, 0xEA, 0xEA)

    $msg = New-Object System.Windows.Forms.Label
    $msg.Text     = 'Starting SimHub. This usually takes a few seconds.'
    $msg.AutoSize = $false
    $msg.Location = New-Object System.Drawing.Point(20, 18)
    $msg.Size     = New-Object System.Drawing.Size(380, 22)
    $msg.ForeColor = $splash.ForeColor
    $splash.Controls.Add($msg)

    $bar = New-Object System.Windows.Forms.ProgressBar
    $bar.Style                 = 'Marquee'
    $bar.MarqueeAnimationSpeed = 30
    $bar.Location              = New-Object System.Drawing.Point(20, 50)
    $bar.Size                  = New-Object System.Drawing.Size(380, 18)
    $splash.Controls.Add($bar)

    $splash.Show()
    [System.Windows.Forms.Application]::DoEvents()
    Write-Log "Splash shown."
} catch {
    Write-Log "Splash creation threw: $($_.Exception.Message)"
    $splash = $null
}

function Pump {
    if ($null -ne $splash -and -not $splash.IsDisposed) {
        try { [System.Windows.Forms.Application]::DoEvents() } catch {}
    }
}

function Close-Splash {
    if ($null -ne $splash -and -not $splash.IsDisposed) {
        try { $splash.Close(); $splash.Dispose() } catch {}
    }
}

# Compile inline P/Invoke. Includes EnumWindows so we can find hidden
# top-level windows that Process.MainWindowHandle misses.
Add-Type -ErrorAction SilentlyContinue @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class TfaLaunchInterop {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public const int  SW_HIDE         = 0;
    public const int  SW_SHOWNORMAL   = 1;
    public const int  SW_SHOW         = 5;
    public const int  SW_RESTORE      = 9;
    public const uint WM_SYSCOMMAND   = 0x0112;
    public const int  SC_RESTORE      = 0xF120;
    public const uint GW_OWNER        = 4;
    public static readonly IntPtr HWND_TOPMOST    = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST  = new IntPtr(-2);
    public const uint SWP_NOMOVE      = 0x0002;
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_SHOWWINDOW  = 0x0040;

    public struct WindowInfo {
        public IntPtr Handle;
        public string Title;
        public string ClassName;
        public bool   Visible;
        public bool   HasOwner;
    }

    public static List<WindowInfo> FindTopLevelWindowsForPid(uint targetPid) {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, lParam) => {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != targetPid) return true;
            // Top-level: no owner window.
            IntPtr owner = GetWindow(hWnd, GW_OWNER);
            int tLen = GetWindowTextLength(hWnd);
            var tb = new StringBuilder(tLen + 1);
            if (tLen > 0) GetWindowText(hWnd, tb, tb.Capacity);
            var cb = new StringBuilder(256);
            GetClassName(hWnd, cb, cb.Capacity);
            result.Add(new WindowInfo {
                Handle    = hWnd,
                Title     = tb.ToString(),
                ClassName = cb.ToString(),
                Visible   = IsWindowVisible(hWnd),
                HasOwner  = (owner != IntPtr.Zero),
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

# Poll until a SimHubWPF process exposes at least one top-level window
# via EnumWindows. Pick the most "main-window-looking" one: prefer a
# window with "SimHub" in the title and no owner (top-level), break
# ties by visible-first.
$timeoutSeconds = 20
$start = Get-Date
$target = $null
$targetPid = 0
do {
    Pump
    Start-Sleep -Milliseconds 300
    Pump
    $procs = Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue
    if (-not $procs) { continue }
    foreach ($p in $procs) {
        $windows = [TfaLaunchInterop]::FindTopLevelWindowsForPid([uint32]$p.Id)
        if ($windows.Count -eq 0) { continue }
        # Score: 3 for has-SimHub-title + no owner, 2 for has-SimHub-title,
        # 1 for no-owner. Then prefer visible.
        $best = $null
        $bestScore = -1
        foreach ($w in $windows) {
            $score = 0
            if ($w.Title -and $w.Title.IndexOf('SimHub', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $score += 2 }
            if (-not $w.HasOwner) { $score += 1 }
            if ($w.Visible) { $score += 0.1 }
            if ($score -gt $bestScore) { $bestScore = $score; $best = $w }
        }
        if ($best -ne $null -and $bestScore -gt 0) {
            $target = $best
            $targetPid = $p.Id
            break
        }
    }
    if ($target) { break }
} until (((Get-Date) - $start).TotalSeconds -ge $timeoutSeconds)

if ($null -eq $target) {
    Write-Log "Timed out: no SimHubWPF top-level window found via EnumWindows."
    # Dump what we did see for debugging.
    $procs = Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue
    if ($procs) {
        foreach ($p in $procs) {
            $windows = [TfaLaunchInterop]::FindTopLevelWindowsForPid([uint32]$p.Id)
            Write-Log "  pid=$($p.Id) windows=$($windows.Count)"
            foreach ($w in $windows) {
                Write-Log "    hWnd=$($w.Handle) cls='$($w.ClassName)' title='$($w.Title)' visible=$($w.Visible) ownerSet=$($w.HasOwner)"
            }
        }
    } else {
        Write-Log "  SimHubWPF process not running."
    }
    Close-Splash
    exit 0
}

$handle = $target.Handle
Write-Log "Picked hWnd=$handle pid=$targetPid title='$($target.Title)' cls='$($target.ClassName)' visible=$($target.Visible) iconic=$([TfaLaunchInterop]::IsIconic($handle))"

# Attach thread inputs so SetForegroundWindow / ShowWindow aren't
# swallowed by Windows' foreground-steal lock.
$ourTid = [TfaLaunchInterop]::GetCurrentThreadId()
$fgHwnd = [TfaLaunchInterop]::GetForegroundWindow()
$fgPid  = 0
$fgTid  = [TfaLaunchInterop]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPid)
$tgtPid = 0
$tgtTid = [TfaLaunchInterop]::GetWindowThreadProcessId($handle, [ref]$tgtPid)
Write-Log "Threads: our=$ourTid fg=$fgTid (pid $fgPid) target=$tgtTid (pid $tgtPid)"

$attachedFg = $false
$attachedTgt = $false
try {
    if ($fgTid -ne 0 -and $fgTid -ne $ourTid) {
        $attachedFg = [TfaLaunchInterop]::AttachThreadInput($ourTid, $fgTid, $true)
    }
    if ($tgtTid -ne 0 -and $tgtTid -ne $ourTid) {
        $attachedTgt = [TfaLaunchInterop]::AttachThreadInput($ourTid, $tgtTid, $true)
    }
    Write-Log "Attach results: fg=$attachedFg target=$attachedTgt"

    [TfaLaunchInterop]::SendMessage($handle, [TfaLaunchInterop]::WM_SYSCOMMAND, [IntPtr][TfaLaunchInterop]::SC_RESTORE, [IntPtr]::Zero) | Out-Null
    [TfaLaunchInterop]::ShowWindow($handle, [TfaLaunchInterop]::SW_SHOW) | Out-Null
    [TfaLaunchInterop]::ShowWindow($handle, [TfaLaunchInterop]::SW_RESTORE) | Out-Null
    [TfaLaunchInterop]::ShowWindowAsync($handle, [TfaLaunchInterop]::SW_RESTORE) | Out-Null
    [TfaLaunchInterop]::SetWindowPos($handle, [TfaLaunchInterop]::HWND_TOPMOST,   0, 0, 0, 0, ([TfaLaunchInterop]::SWP_NOMOVE -bor [TfaLaunchInterop]::SWP_NOSIZE -bor [TfaLaunchInterop]::SWP_SHOWWINDOW)) | Out-Null
    [TfaLaunchInterop]::SetWindowPos($handle, [TfaLaunchInterop]::HWND_NOTOPMOST, 0, 0, 0, 0, ([TfaLaunchInterop]::SWP_NOMOVE -bor [TfaLaunchInterop]::SWP_NOSIZE -bor [TfaLaunchInterop]::SWP_SHOWWINDOW)) | Out-Null
    [TfaLaunchInterop]::BringWindowToTop($handle) | Out-Null
    [TfaLaunchInterop]::SetForegroundWindow($handle) | Out-Null
    Write-Log "First restore + foreground pass issued."
} catch {
    Write-Log "Restore block threw: $($_.Exception.Message)"
} finally {
    if ($attachedFg)  { [TfaLaunchInterop]::AttachThreadInput($ourTid, $fgTid,  $false) | Out-Null }
    if ($attachedTgt) { [TfaLaunchInterop]::AttachThreadInput($ourTid, $tgtTid, $false) | Out-Null }
}

# WPF startup applies StartMinimized late; retry after a beat if the
# window snapped back.
Start-Sleep -Milliseconds 1000
$iconic = [TfaLaunchInterop]::IsIconic($handle)
$visible = [TfaLaunchInterop]::IsWindowVisible($handle)
Write-Log "Post-wait: iconic=$iconic visible=$visible"
if ($iconic -or -not $visible) {
    Write-Log "Window re-minimized or still hidden after restore; retrying."
    try {
        if ($fgTid -ne 0 -and $fgTid -ne $ourTid) { [TfaLaunchInterop]::AttachThreadInput($ourTid, $fgTid, $true) | Out-Null }
        if ($tgtTid -ne 0 -and $tgtTid -ne $ourTid) { [TfaLaunchInterop]::AttachThreadInput($ourTid, $tgtTid, $true) | Out-Null }
        [TfaLaunchInterop]::SendMessage($handle, [TfaLaunchInterop]::WM_SYSCOMMAND, [IntPtr][TfaLaunchInterop]::SC_RESTORE, [IntPtr]::Zero) | Out-Null
        [TfaLaunchInterop]::ShowWindow($handle, [TfaLaunchInterop]::SW_SHOW) | Out-Null
        [TfaLaunchInterop]::ShowWindow($handle, [TfaLaunchInterop]::SW_RESTORE) | Out-Null
        [TfaLaunchInterop]::ShowWindowAsync($handle, [TfaLaunchInterop]::SW_RESTORE) | Out-Null
        [TfaLaunchInterop]::BringWindowToTop($handle) | Out-Null
        [TfaLaunchInterop]::SetForegroundWindow($handle) | Out-Null
    } finally {
        if ($fgTid -ne 0 -and $fgTid -ne $ourTid) { [TfaLaunchInterop]::AttachThreadInput($ourTid, $fgTid, $false) | Out-Null }
        if ($tgtTid -ne 0 -and $tgtTid -ne $ourTid) { [TfaLaunchInterop]::AttachThreadInput($ourTid, $tgtTid, $false) | Out-Null }
    }
}

Close-Splash
Write-Log "Done."
exit 0
