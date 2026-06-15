# Open SimHub visibly after an install/update, even when the user has SimHub's
# "Start minimized" preference enabled (which would otherwise drop it into the
# system tray). Run by the installer right before it launches SimHub.
#
# What it does: if GlobalSimhubSettings.json has StartMinimized = true, flip it
# to false for this one launch and drop a marker file telling the Trueforce
# plugin to restore the user's real preference in SimHub's in-memory model on
# startup (SimHub re-persists that value on exit, so the change is invisible to
# the user). If StartMinimized is already off, or the file is missing, do
# nothing and drop no marker, so the plugin stays out of it.
#
# Best-effort: any failure exits 0 so the install/launch is never blocked. Worst
# case SimHub opens into the tray exactly as it did before.

param(
    [Parameter(Mandatory = $true)][string]$SettingsPath,
    [Parameter(Mandatory = $true)][string]$MarkerDir
)

try {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { exit 0 }

    # Read raw bytes and decode UTF-8 so any non-ASCII content (e.g. a custom
    # path holding a non-Latin username) round-trips intact, and preserve the
    # file's original BOM state on write-back.
    $bytes = [System.IO.File]::ReadAllBytes($SettingsPath)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.TrimStart([char]0xFEFF) }

    # Only act when the user actually has start-minimized ON. Tolerate whitespace
    # variations in SimHub's JSON serialization.
    if ($text -notmatch '"StartMinimized"\s*:\s*true') { exit 0 }

    # Flip just this one value; leave the rest of the file byte-for-byte intact.
    $new = [regex]::Replace($text, '("StartMinimized"\s*:\s*)true', '${1}false', 1)
    if ($new -eq $text) { exit 0 }

    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($SettingsPath, $new, $enc)

    # Only now that we actually flipped it: tell the plugin to restore the user's
    # preference in memory (so SimHub persists the real value back on exit).
    if (-not (Test-Path -LiteralPath $MarkerDir)) {
        New-Item -ItemType Directory -Force -Path $MarkerDir | Out-Null
    }
    $markerPath = Join-Path $MarkerDir 'restore-startminimized.flag'
    [System.IO.File]::WriteAllText($markerPath, 'restore', (New-Object System.Text.UTF8Encoding($false)))
}
catch {
    # Never block the install/launch on this.
}

exit 0
