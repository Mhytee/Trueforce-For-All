# Generates the "TF4ALL Dash" DashStudio dashboard (.djson + .metadata).
# Item schemas mirror shipped dashes (RSC - Toggle Switch / MobileDash):
# TextItem / RectangleItem for visuals, transparent ButtonItem tap zones with
# TriggerAction = "TrueforcePlugin.<DashAction>". All formulas use the JS
# interpreter (Interpreter=1) with $prop(). JS string literals use double
# quotes so these PS single-quoted strings stay readable.

$ErrorActionPreference = 'Stop'
$OutDir = Join-Path $PSScriptRoot 'TF4ALL Dash'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---- palette ----
$BG      = '#FF101216'   # dashboard background
$PANEL   = '#FF1B1F27'   # info panels
$TILE    = '#FF232936'   # buttons / tiles (off state)
$TILEON  = '#FF23503A'   # toggle tile on state
$GREEN   = '#FF37D67A'
$RED     = '#FFE5484D'
$YELLOW  = '#FFE8C547'   # spike-reduction badge lit state
$WHITE   = '#FFF2F4F8'
$MUTED   = '#FF8B93A7'
$GRAY    = '#FF6B7280'
$CLEAR   = '#00FFFFFF'
$BACKDROP= '#F60D0F13'   # overlay backdrop (near-opaque)

function BindJS([string]$target, [string]$expr) {
    [ordered]@{
        Formula = [ordered]@{ Interpreter = 1; Expression = $expr }
        Mode = 2
        TargetPropertyName = $target
    }
}

function New-Text([string]$name, $x, $y, $w, $h, $size, [string]$text, [string]$color,
                  [int]$halign = 1, [hashtable]$bindings = $null, [string]$weight = 'Normal') {
    $b = [ordered]@{}
    if ($bindings) { foreach ($k in $bindings.Keys) { $b[$k] = $bindings[$k] } }
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.TextItem, SimHub.Plugins'
        FontWeight = $weight; TextWrapping = 1; FontStyle = 'Normal'
        FontSize = [double]$size; Text = $text; TextColor = $color
        HorizontalAlignment = $halign; VerticalAlignment = 1
        BackgroundColor = $CLEAR
        Height = [double]$h; Left = [double]$x; Opacity = 100.0; Top = [double]$y
        Visible = $true; Width = [double]$w
        Rotation = 0.0; RenderingSkip = 0; IsFreezed = $false
        Name = $name; Bindings = $b
    }
}

function New-Rect([string]$name, $x, $y, $w, $h, [string]$fill,
                  [hashtable]$bindings = $null, [int]$radius = 6) {
    $b = [ordered]@{}
    if ($bindings) { foreach ($k in $bindings.Keys) { $b[$k] = $bindings[$k] } }
    $cr = "$radius,$radius,$radius,$radius"
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.RectangleItem, SimHub.Plugins'
        BackgroundColor = $fill
        BorderStyle = [ordered]@{
            BorderColor = $CLEAR; BorderTop = 0; BorderBottom = 0; BorderLeft = 0; BorderRight = 0
            CornerRadius = $cr; RadiusTopLeft = $radius; RadiusTopRight = $radius
            RadiusBottomLeft = $radius; RadiusBottomRight = $radius; Bindings = [ordered]@{}
        }
        Height = [double]$h; Left = [double]$x; Opacity = 100.0; Top = [double]$y
        Visible = $true; Width = [double]$w
        BorderBottom = 0; BorderColor = $CLEAR; BorderLeft = 0; BorderRight = 0; BorderTop = 0
        Rotation = 0.0; RenderingSkip = 0; IsFreezed = $false
        Name = $name; Bindings = $b
    }
}

function New-Button([string]$name, $x, $y, $w, $h, [string]$action,
                    [hashtable]$bindings = $null) {
    $b = [ordered]@{}
    if ($bindings) { foreach ($k in $bindings.Keys) { $b[$k] = $bindings[$k] } }
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.ButtonItem, SimHub.Plugins'
        SimulatedKey = 0
        SimulatedKeyV2 = [ordered]@{ Win = $false; Ctrl = $false; Alt = $false; Shift = $false }
        SimulateKey = $false
        TriggerAction = "TrueforcePlugin.$action"
        TriggerSimHubInputName = ''
        IsEnabled = $true; PressedImage = 'None'; DisabledOpacity = 50; PressedOpacity = 60
        Image = 'None'; AutoSize = $false; BackgroundColor = $CLEAR
        Height = [double]$h; Left = [double]$x; Top = [double]$y
        Visible = $true; Width = [double]$w
        Name = $name; Bindings = $b
    }
}

# Built-in scrolling time chart (schema mirrored from RSC - iRacing - FFB).
# The server samples the CurrentValue binding every MinimumRefreshIntervalMS
# and pushes points; the viewer strokes the history as one smooth line.
# RENDERER FACTS (Web/controls.js): the canvas has a hardcoded 10 px inner
# margin on ALL sides, so Min/Max map to (x+10..x+w-10, y+10..y+h-10) --
# size items 10 px beyond the intended band. Values are NOT clamped to
# Min/Max; drawing is canvas-clipped to the margin rect +/- LineTickness,
# so an out-of-range value simply doesn't draw (usable to hide a resting
# level). One stroke color for the whole history line.
function New-Chart([string]$name, $x, $y, $w, $h, [string]$lineColor, [int]$thickness,
                   [double]$points, [string]$valueExpr) {
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.ChartItem, SimHub.Plugins'
        ChartSuspended = $false; ChartEnabled = $true
        CurrentValue = 0.0
        Minimum = -1.0; UseMinimum = $true; UseMaximum = $true; Maximum = 1.0
        LineColor = $lineColor; LineTickness = $thickness
        PointsCount = [double]$points
        BackgroundColor = $CLEAR
        Height = [double]$h; Left = [double]$x; Top = [double]$y
        Visible = $true; Width = [double]$w
        IsEffectiveDelayConstrainer = $true; IsFreezed = $false
        RenderingSkip = 0; MinimumRefreshIntervalMS = 20.0
        Name = $name
        Bindings = [ordered]@{ CurrentValue = BindJS 'CurrentValue' $valueExpr }
    }
}

# Visible-on-overlay helper: stamps the Visible binding onto an item.
function OnOverlay($item, [string]$mode) {
    $item.Bindings['Visible'] = BindJS 'Visible' ('return (""+$prop("TrueforcePlugin.Dash.Overlay"))=="' + $mode + '"')
    $item
}

# Shared numeric keypad overlay (Dash.Overlay == "keypad"). Serves master
# gain, audio gain, every effect gain and the redline; the plugin stamps
# the title with the target + current value on open.
function KeypadOverlay([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add((OnOverlay (New-Rect 'kp-backdrop' 0 0 800 480 $script:BACKDROP $null 0) 'keypad'))
    $items.Add((OnOverlay (New-Text 'kp-title' 0 16 800 30 20 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('return ""+$prop("' + $P + '.KeypadTitle")')
    } 'Bold') 'keypad'))
    $items.Add((OnOverlay (New-Rect 'kp-entry-bg' 250 48 300 64 $script:PANEL) 'keypad'))
    $items.Add((OnOverlay (New-Text 'kp-entry' 250 48 300 64 42 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('var e=""+$prop("' + $P + '.KeypadEntry");return e==""?"----":e')
    } 'Bold') 'keypad'))
    $keys = @(
        @('1','DashKeypadDigit1'), @('2','DashKeypadDigit2'), @('3','DashKeypadDigit3'),
        @('4','DashKeypadDigit4'), @('5','DashKeypadDigit5'), @('6','DashKeypadDigit6'),
        @('7','DashKeypadDigit7'), @('8','DashKeypadDigit8'), @('9','DashKeypadDigit9'),
        @('DEL','DashKeypadBack'), @('0','DashKeypadDigit0'), @('.','DashKeypadDot')
    )
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $label = $keys[$i][0]; $action = $keys[$i][1]
        $c = $i % 3; $r = [math]::Floor($i / 3)
        $x = 235 + $c * 115; $y = 124 + $r * 74
        $tcol = if ($label -eq 'DEL') { $script:MUTED } else { $script:WHITE }
        $safe = if ($label -eq '.') { 'dot' } else { $label }
        $items.Add((OnOverlay (New-Rect  "kp-$safe-bg" $x $y 105 64 $script:TILE) 'keypad'))
        $items.Add((OnOverlay (New-Text  "kp-$safe-t"  $x $y 105 64 24 $label $tcol 1 $null 'Bold') 'keypad'))
        $items.Add((OnOverlay (New-Button "kp-$safe"   $x $y 105 64 $action) 'keypad'))
    }
    $items.Add((OnOverlay (New-Rect 'kp-set-bg' 605 124 160 138 $script:TILEON) 'keypad'))
    $items.Add((OnOverlay (New-Text 'kp-set-t' 605 124 160 138 26 'SET' $script:WHITE 1 $null 'Bold') 'keypad'))
    $items.Add((OnOverlay (New-Button 'kp-set' 605 124 160 138 'DashKeypadSet') 'keypad'))
    $items.Add((OnOverlay (New-Rect 'kp-cancel-bg' 605 344 160 64 $script:TILE) 'keypad'))
    $items.Add((OnOverlay (New-Text 'kp-cancel-t' 605 344 160 64 18 'CANCEL' $script:RED 1 $null 'Bold') 'keypad'))
    $items.Add((OnOverlay (New-Button 'kp-cancel' 605 344 160 64 'DashKeypadCancel') 'keypad'))
    $items
}

# Rev LED strip (topmost, every screen): 16 thin segments across the top,
# lighting progressively from 50% to ~97% of the plugin's EFFECTIVE redline
# (Dash.RpmPct: user pin > community > telemetry > estimate). Green,
# amber, red zones; goes dark when telemetry stalls. Lets a wheel-mounted
# remote double as rev lights in race.
function RevStrip([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add((New-Rect 'rev-bg' 0 0 800 12 '#FF15181E' $null 0))
    # Unlit sockets: a faint 1px outline per LED position, always visible,
    # so the strip is discoverable before the first rev (an all-dark strip
    # read as empty chrome). Lit segments draw over them.
    for ($i = 0; $i -lt 16; $i++) {
        $x = 2 + $i * 50
        $sock = New-Rect "rev-sock$i" $x 1 46 10 $script:CLEAR $null 2
        $sock.BorderStyle.BorderColor = '#FF39404C'
        $sock.BorderStyle.BorderTop = 1; $sock.BorderStyle.BorderBottom = 1
        $sock.BorderStyle.BorderLeft = 1; $sock.BorderStyle.BorderRight = 1
        $sock.BorderColor = '#FF39404C'
        $sock.BorderTop = 1; $sock.BorderBottom = 1; $sock.BorderLeft = 1; $sock.BorderRight = 1
        $items.Add($sock)
    }
    for ($i = 0; $i -lt 16; $i++) {
        $x = 2 + $i * 50
        # Two threshold schemes, chosen live by Dash.RevOutsideIn:
        # left-to-right walks 50..96.9 across the strip; outside-in pairs
        # mirror segments (0+15 first, converging on 7+8) over 50..93.75.
        $tLtr = [math]::Round(50 + $i * 3.125, 2)
        $pair = [math]::Min($i, 15 - $i)
        $tOut = [math]::Round(50 + $pair * 6.25, 2)
        # Colors follow the direction: left-to-right zones run green ->
        # amber -> red across the strip; outside-in zones run green at the
        # edges converging to red in the CENTER (pair index, not position).
        $amber = '#FFE8A33D'
        $cLtr = if ($i -lt 8) { $script:GREEN } elseif ($i -lt 12) { $amber } else { $script:RED }
        $cOut = if ($pair -lt 4) { $script:GREEN } elseif ($pair -lt 6) { $amber } else { $script:RED }
        $seg = New-Rect "rev-seg$i" $x 1 46 10 $cLtr @{
            BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.RevOutsideIn")?"' + $cOut + '":"' + $cLtr + '"')
        } 2
        # RevFlash: steady true below redline, wheel-synced blink at/above.
        $seg.Bindings['Visible'] = BindJS 'Visible' ('var t=$prop("' + $P + '.RevOutsideIn")?' + $tOut + ':' + $tLtr + ';return (1*$prop("' + $P + '.RpmPct"))>=t && $prop("' + $P + '.RevFlash")')
        $items.Add($seg)
    }
    $items
}

# Bottom tab bar (every screen): direct navigation replacing screen
# swipes. SimHub's web viewer fires ButtonItems on touch-down AND swallows
# the touch, so a swipe that started on any tap zone triggered that
# control and never changed screen anyway. Each screen's
# ScreenEnabledExpression gates on Dash.Tab, so exactly one screen is
# enabled at a time (swipes are inert) and these buttons are the
# navigation.
# The bar is six position-fixed SLOTS driven entirely by plugin
# properties (Dash.TabSlot<i>.Label / .On / .Active), so users can hide
# and reorder tabs from the desktop Settings tab with no dash reload:
# enabled tabs pack left in the user's order, empty slots vanish, and
# the highlight follows whichever slot maps to the active screen. Every
# slot carries a button (DashTabSlotSelect<i>); the plugin maps slots to
# screens and treats a tap on the active slot as a no-op.
# Hidden while any overlay is up: the viewer gives item visuals
# pointer-events:none, so an overlay backdrop would NOT shield these
# buttons from taps; display:none (a Visible binding) does.
# ---- Drive screen content boxes -------------------------------------
# Each of the four boxes renders EVERY content option and shows the one
# whose key matches Dash.Drive.Slot<n>, so swapping a box in Settings
# applies on the next property poll with no dashboard reload. Top-row
# boxes additionally gate on Dash.Drive.TwoRows, which is the phone
# (bottom row only) vs tablet (both rows) choice.
# Our own boxes (car facts, gains, presets, FFB scope, friction circle)
# read TrueforcePlugin properties; the rest read SimHub's game data, so
# no telemetry is plumbed through our plugin for them. Property names
# are taken from shipped SimHub dashboards, never guessed.
# EVERY game-data box also carries a "no data" line: the games differ
# wildly in what they report (Forza gives tyres and gear but no
# opponents, a free-roam session has no lap times), and a silently empty
# box reads as a broken dash. One data test drives both states, so the
# values and the notice can never show at once.
$SIM = 'DataCorePlugin.GameData.NewData.'
$TRK = 'PersistantTrackerPlugin.'

function FmtLapJs([string]$prop) {
    'var s=""+($prop("' + $prop + '")||"");' +
    'if(s.indexOf(".")>=0)s=s.substring(0,s.indexOf(".")+4);' +
    'if(s.indexOf("00:")==0)s=s.substring(3);' +
    'return (s==""||s.indexOf("00:00.00")==0)?"--":s'
}

# Same readout, but preferring our own Forza parse (seconds as a float)
# and falling back to SimHub's TimeSpan string. Forza players commonly
# leave forwarding off, so SimHub's copy is empty for them.
function FmtLapDualJs([string]$fzProp, [string]$simProp) {
    'var f=1*$prop("' + $fzProp + '");' +
    'if(f>0){var m=Math.floor(f/60);var s=f-m*60;var ss=s.toFixed(3);if(s<10)ss="0"+ss;return (m>0?(m+":"):"")+ss;}' +
    'var t=""+($prop("' + $simProp + '")||"");' +
    'if(t.indexOf(".")>=0)t=t.substring(0,t.indexOf(".")+4);' +
    'if(t.indexOf("00:")==0)t=t.substring(3);' +
    'return (t==""||t.indexOf("00:00.00")==0)?"--":t'
}

# One labelled value line inside a box.
function BoxLine([string]$name, $x, $y, $w, [string]$label, [string]$valueJs, [string]$vis, [int]$size = 20) {
    $l = New-Text "$name-l" $x $y ($w * 0.46) 30 14 $label $script:MUTED 0
    $l.Bindings['Visible'] = BindJS 'Visible' $vis
    $v = New-Text "$name-v" ($x + $w * 0.46) $y ($w * 0.54) 30 $size '' $script:WHITE 2 @{
        Text = BindJS 'Text' $valueJs
    } 'Bold'
    $v.Bindings['Visible'] = BindJS 'Visible' $vis
    @($l, $v)
}

# A rounded outline ring (used by both circle boxes).
function New-Ring([string]$name, $cx, $cy, $r, [string]$color, [int]$thickness, [string]$vis) {
    $ring = New-Rect $name ($cx - $r) ($cy - $r) ($r * 2) ($r * 2) $script:CLEAR $null ([int]$r)
    $ring.BorderStyle.BorderColor = $color
    $ring.BorderStyle.BorderTop = $thickness; $ring.BorderStyle.BorderBottom = $thickness
    $ring.BorderStyle.BorderLeft = $thickness; $ring.BorderStyle.BorderRight = $thickness
    $ring.BorderColor = $color
    $ring.BorderTop = $thickness; $ring.BorderBottom = $thickness
    $ring.BorderLeft = $thickness; $ring.BorderRight = $thickness
    $ring.Bindings['Visible'] = BindJS 'Visible' $vis
    $ring
}

function DriveBox([string]$P, [int]$slot, $x, $y, $w, $h, [bool]$topRow) {
    $items = [System.Collections.Generic.List[object]]::new()
    $slotProp = $P + '.Drive.Slot' + $slot
    $rowCond = if ($topRow) { ' && $prop("' + $P + '.Drive.TwoRows")' } else { '' }
    $sel = '(""+$prop("' + $slotProp + '"))'

    $ix = $x + 14; $iw = $w - 28; $iy = $y + 10

    # Shown when this box holds the key (and, where given, the game
    # actually reports that data).
    function KeyVis([string]$k, [string]$dataJs) {
        $base = $script:sel + '=="' + $k + '"' + $script:rowCond
        if ($dataJs) { 'return ' + $base + ' && (' + $dataJs + ')' } else { 'return ' + $base }
    }
    function NoDataVis([string]$k, [string]$dataJs) {
        'return ' + $script:sel + '=="' + $k + '"' + $script:rowCond + ' && !(' + $dataJs + ')'
    }
    $script:sel = $sel; $script:rowCond = $rowCond

    $panel = New-Rect "d$slot-panel" $x $y $w $h $script:PANEL
    $panel.Bindings['Visible'] = BindJS 'Visible' ('return ' + $sel + '!="None"' + $rowCond)
    $items.Add($panel)

    # Adds a section header that shows whenever the box holds this key,
    # data or not, so a "no data" box still says what it is.
    function AddHead([string]$id, [string]$title, [string]$k) {
        $t = New-Text "d$script:slotN-$id-h" $script:ixN $script:iyN $script:iwN 22 13 $title $script:MUTED 0
        $t.Bindings['Visible'] = BindJS 'Visible' (KeyVis $k $null)
        $t
    }
    function AddNote([string]$id, [string]$text, [string]$k, [string]$dataJs) {
        $t = New-Text "d$script:slotN-$id-nd" $script:ixN ($script:iyN + 54) $script:iwN 60 14 $text $script:GRAY 1
        $t.Bindings['Visible'] = BindJS 'Visible' (NoDataVis $k $dataJs)
        $t
    }
    $script:slotN = $slot; $script:ixN = $ix; $script:iyN = $iy; $script:iwN = $iw

    # --- data tests, per content type ---
    # Forza first, SimHub second. A Forza player normally leaves "Also
    # forward to SimHub" off, so SimHub's own properties stay empty all
    # session while our UDP listener has the data; every one of these
    # boxes would otherwise show its "not reported" notice to exactly
    # the audience the Drive tab was built for. Zero from our parse
    # still means "this title does not report it" (Horizon leaves parts
    # of the dash block empty), so the notice still does its job.
    $fzTempJs = 'function(){var t=1*$prop("' + $P + '.Forza.TempFL");return t>0}'
    $dLap   = '((1*$prop("' + $P + '.Forza.BestLap"))>0)||((""+$prop("' + $SIM + 'BestLapTime")||"")!=""&&(""+$prop("' + $SIM + 'BestLapTime")).indexOf("00:00:00")!=0)'
    $dTemp  = '((1*$prop("' + $P + '.Forza.TempFL"))>0)||((1*$prop("' + $SIM + 'TyreTemperatureFrontLeft"))>0)'
    $dWear  = '($prop("' + $P + '.Forza.HasWear")&&(1*$prop("' + $P + '.Forza.WearFL"))>0)||((1*$prop("' + $SIM + 'TyreWearFrontLeft"))>0)'
    $dFuel  = '((1*$prop("' + $P + '.Forza.FuelPct"))>0)||((1*$prop("' + $SIM + 'MaxFuel"))>0)'
    $dDelta = '(""+$prop("' + $TRK + 'EstimatedLapTime")||"")!=""'
    $dOpp   = '(1*$prop("' + $SIM + 'OpponentsCount"))>1'
    $dG     = '!isNaN(1*$prop("' + $P + '.Drive.GLat"))'
    $dFric  = '$prop("' + $P + '.ModeB.On")'

    # ---------------- CAR FACTS (ours, always available) -------------
    # Tappable exactly like the Car facts tab: the engine row opens the
    # layout picker, the redline row opens the keypad. Both overlays
    # live on this screen too, so the flow never leaves the Drive tab.
    $vis = KeyVis 'CarFacts' $null
    $items.Add((AddHead 'cf' 'CAR FACTS' 'CarFacts'))
    $t = New-Text "d$slot-cf-car" $ix ($iy + 24) $iw 34 19 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('return ""+($prop("' + $P + '.CarName")||"No car")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    BoxLine "d$slot-cf-eng" $ix ($iy + 62) $iw 'Engine' ('return ""+($prop("' + $P + '.EngineLayout")||"Auto")') $vis 17 | ForEach-Object { $items.Add($_) }
    $b = New-Button "d$slot-cf-eng-tap" $ix ($iy + 60) $iw 32 'DashEngineLayoutOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    # Redline row with the tab's own controls: minus / plus 50 either
    # side and the value itself opening the keypad.
    $t = New-Text "d$slot-cf-redl" $ix ($iy + 96) 62 26 14 'Redline' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $stepW = 34
    $valX = $ix + 66; $valW = $iw - 66 - ($stepW * 2 + 8)
    $t = New-Text "d$slot-cf-red-v" $valX ($iy + 94) $valW 30 17 '' $script:WHITE 2 @{
        Text = BindJS 'Text' ('var r=1*$prop("' + $P + '.Redline");return r>0?(r+" rpm"):"--"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-cf-red-tap" $valX ($iy + 94) $valW 30 'DashRedlineOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    foreach ($st in @(@('dn', 'DashRedlineDown', '-', 0), @('up', 'DashRedlineUp', '+', $stepW + 8))) {
        $sx = $ix + $iw - ($stepW * 2 + 8) + $st[3]
        $r = New-Rect "d$slot-cf-r$($st[0])-bg" $sx ($iy + 94) $stepW 30 $script:TILE
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $tt = New-Text "d$slot-cf-r$($st[0])-t" $sx ($iy + 94) $stepW 30 20 $st[2] $script:WHITE 1 $null 'Bold'
        $tt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($tt)
        $bb = New-Button "d$slot-cf-r$($st[0])" $sx ($iy + 94) $stepW 30 $st[1]
        $bb.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($bb)
    }
    # The tab's provenance line: where the redline came from and the
    # observed ceiling, so a wrong buzz point is diagnosable here too.
    $t = New-Text "d$slot-cf-info" $ix ($iy + 128) $iw 22 12 '' $script:GRAY 0 @{
        Text = BindJS 'Text' ('var m=1*$prop("' + $P + '.MaxRpm");var s=""+($prop("' + $P + '.RedlineSource")||"");' +
                              'var t=m>0?("MAX "+m):"";if(s!=""&&s!="none")t+=(t!=""?"   ":"")+s.toUpperCase();return t')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)

    # ---------------- GAINS (ours) -----------------------------------
    # Live like the Home tab: steppers on master gain, the value opens
    # the keypad, and the audio and plugin rows toggle.
    $vis = KeyVis 'Home' $null
    $items.Add((AddHead 'hm' 'GAINS' 'Home'))
    $t = New-Text "d$slot-hm-mg" $ix ($iy + 22) ($iw - 108) 46 34 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('return (1*$prop("' + $P + '.MasterGain")).toFixed(2)')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-hm-mg-tap" $ix ($iy + 22) ($iw - 108) 46 'DashMasterGainOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    foreach ($st in @(@('dn', 'DashMasterGainDown', '-', 0), @('up', 'DashMasterGainUp', '+', 52))) {
        $sx = $ix + $iw - 100 + $st[3]
        $r = New-Rect "d$slot-hm-$($st[0])-bg" $sx ($iy + 22) 46 46 $script:TILE
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $tt = New-Text "d$slot-hm-$($st[0])-t" $sx ($iy + 22) 46 46 26 $st[2] $script:WHITE 1 $null 'Bold'
        $tt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($tt)
        $bb = New-Button "d$slot-hm-$($st[0])" $sx ($iy + 22) 46 46 $st[1]
        $bb.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($bb)
    }
    $t = New-Text "d$slot-hm-mgl" $ix ($iy + 68) $iw 18 12 'MASTER GAIN' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # Audio row with the Home tab's full set: the label toggles it, the
    # value opens the keypad, and it gets its own steppers.
    $t = New-Text "d$slot-hm-agl" $ix ($iy + 90) 58 26 14 'Audio' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-hm-ag-tgl" $ix ($iy + 88) 58 30 'DashFxAudioToggle'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    $agValX = $ix + 62; $agValW = $iw - 62 - 92
    $t = New-Text "d$slot-hm-ag-v" $agValX ($iy + 88) $agValW 30 17 '' $script:WHITE 2 @{
        Text = BindJS 'Text' ('return $prop("' + $P + '.Fx.Audio.On")?(1*$prop("' + $P + '.AudioGain")).toFixed(2):"off"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-hm-ag-tap" $agValX ($iy + 88) $agValW 30 'DashAudioGainOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    foreach ($st in @(@('adn', 'DashAudioGainDown', '-', 0), @('aup', 'DashAudioGainUp', '+', 46))) {
        $sx = $ix + $iw - 88 + $st[3]
        $r = New-Rect "d$slot-hm-$($st[0])-bg" $sx ($iy + 88) 40 30 $script:TILE
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $tt = New-Text "d$slot-hm-$($st[0])-t" $sx ($iy + 88) 40 30 20 $st[2] $script:WHITE 1 $null 'Bold'
        $tt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($tt)
        $bb = New-Button "d$slot-hm-$($st[0])" $sx ($iy + 88) 40 30 $st[1]
        $bb.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($bb)
    }
    BoxLine "d$slot-hm-on" $ix ($iy + 124) $iw 'Plugin' ('return $prop("' + $P + '.PluginOn")?"on":"off"') $vis 17 | ForEach-Object { $items.Add($_) }
    $b = New-Button "d$slot-hm-on-tap" $ix ($iy + 122) $iw 30 'DashPluginToggle'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)

    # ---------------- PRESETS (ours) ---------------------------------
    $vis = KeyVis 'Presets' $null
    $items.Add((AddHead 'pr' 'PRESETS' 'Presets'))
    $t = New-Text "d$slot-pr-gl" $ix ($iy + 26) $iw 18 12 'GAME' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $t = New-Text "d$slot-pr-g" $ix ($iy + 42) $iw 32 18 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?p:"(manual tune)"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $t = New-Text "d$slot-pr-cl" $ix ($iy + 80) $iw 18 12 'CAR' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $t = New-Text "d$slot-pr-c" $ix ($iy + 96) $iw 32 18 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.CarPresetName")||"");return p!=""?p:"(none)"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # Each row opens its own picker, same as the Presets tab.
    $b = New-Button "d$slot-pr-g-tap" $ix ($iy + 24) $iw 50 'DashPresetOpenGame'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    $b = New-Button "d$slot-pr-c-tap" $ix ($iy + 78) $iw 50 'DashPresetOpenCar'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)

    # ---------------- LAP TIMES --------------------------------------
    $vis = KeyVis 'LapTimes' $dLap
    $items.Add((AddHead 'lt' 'LAP TIMES' 'LapTimes'))
    $t = New-Text "d$slot-lt-cur" $ix ($iy + 22) $iw 40 30 '' $script:WHITE 0 @{
        Text = BindJS 'Text' (FmtLapDualJs ($P + '.Forza.CurLap') ($SIM + 'CurrentLapTime'))
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    BoxLine "d$slot-lt-last" $ix ($iy + 66) $iw 'Last' (FmtLapDualJs ($P + '.Forza.LastLap') ($SIM + 'LastLapTime')) $vis 17 | ForEach-Object { $items.Add($_) }
    BoxLine "d$slot-lt-best" $ix ($iy + 98) $iw 'Best' (FmtLapDualJs ($P + '.Forza.BestLap') ($SIM + 'BestLapTime')) $vis 17 | ForEach-Object { $items.Add($_) }
    $items.Add((AddNote 'lt' 'This game does not report lap times.' 'LapTimes' $dLap))

    # ---------------- TYRE TEMPS (visual) ----------------------------
    # Four tyre blocks coloured by temperature, so the box reads at a
    # glance instead of needing four numbers parsed. Bands are broad on
    # purpose: games disagree on what they report (core vs surface, C vs
    # F), so this is a relative cue with the number kept for detail.
    $vis = KeyVis 'TyreTemps' $dTemp
    $items.Add((AddHead 'tt' 'TYRE TEMPS' 'TyreTemps'))
    $tyreProps = @('TyreTemperatureFrontLeft', 'TyreTemperatureFrontRight', 'TyreTemperatureRearLeft', 'TyreTemperatureRearRight')
    $wearProps = @('TyreWearFrontLeft', 'TyreWearFrontRight', 'TyreWearRearLeft', 'TyreWearRearRight')
    $tyW = 54; $tyH = [math]::Min(74, ($h - 70) / 2); $gapX = 26
    $cx0 = $ix + ($iw - ($tyW * 2 + $gapX)) / 2
    # The blocks are capped at 74 tall, so in the taller one-row box the
    # grid does not grow; centre it in the space below the header instead
    # of leaving it pinned to the top.
    $cy0 = ($iy + 30 + $y + $h - 12) / 2 - ($tyH + 5)
    $fzTemp = @('TempFL', 'TempFR', 'TempRL', 'TempRR')
    for ($q = 0; $q -lt 4; $q++) {
        $cx = $cx0 + ($q % 2) * ($tyW + $gapX)
        $cy = $cy0 + [math]::Floor($q / 2) * ($tyH + 10)
        # ours first, SimHub's as the fallback
        $vJs = 'var v=1*$prop("' + $P + '.Forza.' + $fzTemp[$q] + '");' +
               'if(!(v>0))v=1*$prop("' + $SIM + $tyreProps[$q] + '");'
        # cold -> blue, working -> green, hot -> amber, overheating -> red
        $colJs = $vJs + 'if(isNaN(v)||v<=0)return "' + $script:TILE + '";' +
                 'return v<60?"#FF3D6FB5":(v<85?"' + $script:GREEN + '":(v<100?"#FFE8A33D":"' + $script:RED + '"))'
        $r = New-Rect "d$slot-tt$q" $cx $cy $tyW $tyH $script:TILE @{
            BackgroundColor = BindJS 'BackgroundColor' $colJs
        } 6
        $r.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($r)
        $tv = New-Text "d$slot-tt$q-v" $cx ($cy + $tyH / 2 - 15) $tyW 30 17 '' '#FF101216' 1 @{
            Text = BindJS 'Text' ($vJs + 'return isNaN(v)||v<=0?"--":Math.round(v)')
        } 'Bold'
        $tv.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($tv)
    }
    $items.Add((AddNote 'tt' 'This game does not report tyre temperatures.' 'TyreTemps' $dTemp))

    # ---------------- TYRE WEAR (visual) -----------------------------
    # Same blocks; here the colour is how much tread is left.
    $vis = KeyVis 'TyreWear' $dWear
    $items.Add((AddHead 'tw' 'TYRE WEAR' 'TyreWear'))
    $fzWear = @('WearFL', 'WearFR', 'WearRL', 'WearRR')
    for ($q = 0; $q -lt 4; $q++) {
        $cx = $cx0 + ($q % 2) * ($tyW + $gapX)
        $cy = $cy0 + [math]::Floor($q / 2) * ($tyH + 10)
        # Forza reports wear 0 = fresh, so invert it into tread-left to
        # match how SimHub reports it and how the colours below read.
        $vJs = 'var v=NaN;if($prop("' + $P + '.Forza.HasWear")){var fw=1*$prop("' + $P + '.Forza.' + $fzWear[$q] + '");if(fw>=0)v=100-fw*100;}' +
               'if(isNaN(v))v=1*$prop("' + $SIM + $wearProps[$q] + '");'
        $colJs = $vJs + 'if(isNaN(v))return "' + $script:TILE + '";' +
                 'return v>60?"' + $script:GREEN + '":(v>30?"#FFE8A33D":"' + $script:RED + '")'
        $r = New-Rect "d$slot-tw$q" $cx $cy $tyW $tyH $script:TILE @{
            BackgroundColor = BindJS 'BackgroundColor' $colJs
        } 6
        $r.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($r)
        $tv = New-Text "d$slot-tw$q-v" $cx ($cy + $tyH / 2 - 15) $tyW 30 17 '' '#FF101216' 1 @{
            Text = BindJS 'Text' ($vJs + 'return isNaN(v)?"--":Math.round(v)+"%"')
        } 'Bold'
        $tv.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($tv)
    }
    $items.Add((AddNote 'tw' 'This game does not report tyre wear.' 'TyreWear' $dWear))

    # ---------------- FUEL -------------------------------------------
    $vis = KeyVis 'Fuel' $dFuel
    $items.Add((AddHead 'fu' 'FUEL' 'Fuel'))
    # Forza reports a tank fraction rather than litres, so the big number
    # is a percentage there and a level everywhere else.
    $t = New-Text "d$slot-fu-lvl" $ix ($iy + 22) $iw 40 30 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('var p=1*$prop("' + $P + '.Forza.FuelPct");if(p>0)return Math.round(p)+"%";' +
                              'var v=1*$prop("' + $SIM + 'Fuel");return isNaN(v)?"--":v.toFixed(1)')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    BoxLine "d$slot-fu-laps" $ix ($iy + 66) $iw 'Laps left' ('var v=1*$prop("DataCorePlugin.Computed.Fuel_RemainingLaps");return isNaN(v)||v<=0?"--":v.toFixed(1)') $vis 17 | ForEach-Object { $items.Add($_) }
    BoxLine "d$slot-fu-pct" $ix ($iy + 98) $iw 'Tank' ('var p=1*$prop("' + $P + '.Forza.FuelPct");if(p>0)return Math.round(p)+"%";' +
                              'var v=1*$prop("' + $SIM + 'FuelPercent");return isNaN(v)?"--":Math.round(v)+"%"') $vis 17 | ForEach-Object { $items.Add($_) }
    $items.Add((AddNote 'fu' 'This game does not report fuel.' 'Fuel' $dFuel))

    # ---------------- LAP DELTA --------------------------------------
    $vis = KeyVis 'Delta' $dDelta
    $items.Add((AddHead 'dl' 'LAP DELTA' 'Delta'))
    $t = New-Text "d$slot-dl-v" $ix ($iy + 22) $iw 44 32 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('var v=1*$prop("' + $TRK + 'SessionBestLastLapDelta");return isNaN(v)?"--":(v>0?"+":"")+v.toFixed(2)')
        TextColor = BindJS 'TextColor' ('var v=1*$prop("' + $TRK + 'SessionBestLastLapDelta");return isNaN(v)?"' + $script:MUTED + '":(v>0?"' + $script:RED + '":"' + $script:GREEN + '")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    BoxLine "d$slot-dl-est" $ix ($iy + 70) $iw 'Estimated' (FmtLapJs ($TRK + 'EstimatedLapTime')) $vis 17 | ForEach-Object { $items.Add($_) }
    BoxLine "d$slot-dl-prev" $ix ($iy + 102) $iw 'Previous' (FmtLapJs ($TRK + 'PreviousLap_00')) $vis 17 | ForEach-Object { $items.Add($_) }
    $items.Add((AddNote 'dl' 'This game does not report lap deltas.' 'Delta' $dDelta))

    # ---------------- G CIRCLE (game accelerations) ------------------
    # Classic g-g diagram: the dot is where the car's acceleration
    # points, the rings are 0.75 g and 1.5 g. Reads the same
    # accelerations the crash duck uses, so it works on every telemetry
    # source we support rather than only games with raw g properties.
    $vis = KeyVis 'GCircle' $dG
    $items.Add((AddHead 'gc' 'G CIRCLE' 'GCircle'))
    $gr  = [math]::Min(($iw - 24) / 2, ($h - 66) / 2)
    $gcx = $ix + $iw / 2
    # Centre in the area below the header rather than hanging off the top
    # of it. In the tall one-row box the radius is capped by WIDTH, so the
    # circle cannot grow into the extra height and would otherwise sit
    # well above centre. The dots position off these constants, so this
    # has to be right here: a post-hoc shift would move the rings and
    # leave the bound dots behind.
    $gcy = ($iy + 30 + $y + $h - 12) / 2
    $items.Add((New-Ring "d$slot-gc-r1" $gcx $gcy $gr '#FF39404C' 1 $vis))
    $items.Add((New-Ring "d$slot-gc-r2" $gcx $gcy ($gr / 2) '#FF2A303A' 1 $vis))
    $gLatJs = 'var g=1*$prop("' + $P + '.Drive.GLat");if(isNaN(g))g=0;if(g>1.5)g=1.5;if(g<-1.5)g=-1.5;'
    $gLonJs = 'var g=1*$prop("' + $P + '.Drive.GLong");if(isNaN(g))g=0;if(g>1.5)g=1.5;if(g<-1.5)g=-1.5;'
    $dot = New-Rect "d$slot-gc-dot" ($gcx - 7) ($gcy - 7) 14 14 $script:GREEN $null 7
    $dot.Bindings['Left'] = BindJS 'Left' ($gLatJs + 'return ' + ($gcx - 7) + '+g*' + ($gr / 1.5))
    $dot.Bindings['Top']  = BindJS 'Top'  ($gLonJs + 'return ' + ($gcy - 7) + '-g*' + ($gr / 1.5))
    $dot.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($dot)
    $t = New-Text "d$slot-gc-v" $ix ($iy + 22) $iw 20 13 '' $script:MUTED 2 @{
        Text = BindJS 'Text' ('var a=1*$prop("' + $P + '.Drive.GLat");var b=1*$prop("' + $P + '.Drive.GLong");if(isNaN(a)||isNaN(b))return "";return Math.sqrt(a*a+b*b).toFixed(2)+" g"')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $items.Add((AddNote 'gc' 'This game does not report accelerations.' 'GCircle' $dG))

    # ---------------- FRICTION CIRCLE (ours, Mode B) -----------------
    # Not the g diagram: this is how much of the tyre's grip our own
    # model believes is in use, which is what shapes the braking feel.
    # The ring IS the limit, so a dot touching it means the model has
    # run out of grip. Needs Telemetry FFB running for this game.
    $vis = KeyVis 'Friction' $dFric
    $items.Add((AddHead 'fc' 'FRICTION CIRCLE' 'Friction'))
    $items.Add((New-Ring "d$slot-fc-lim" $gcx $gcy $gr '#FF6B7280' 2 $vis))
    $items.Add((New-Ring "d$slot-fc-in" $gcx $gcy ($gr * 0.75) '#FF2A303A' 1 $vis))
    $uJs = 'var u=1*$prop("' + $P + '.Drive.Util");if(isNaN(u))u=0;if(u>1.3)u=1.3;'
    $dirJs = 'var a=1*$prop("' + $P + '.Drive.GLat");var b=1*$prop("' + $P + '.Drive.GLong");' +
             'var m=Math.sqrt(a*a+b*b);if(isNaN(m)||m<0.05){a=0;b=0;m=1;}'
    $fdot = New-Rect "d$slot-fc-dot" ($gcx - 8) ($gcy - 8) 16 16 $script:GREEN @{
        BackgroundColor = BindJS 'BackgroundColor' ($uJs + 'return u<0.75?"' + $script:GREEN + '":(u<1.0?"#FFE8A33D":"' + $script:RED + '")')
    } 8
    $fdot.Bindings['Left'] = BindJS 'Left' ($uJs + $dirJs + 'return ' + ($gcx - 8) + '+(a/m)*u*' + $gr)
    $fdot.Bindings['Top']  = BindJS 'Top'  ($uJs + $dirJs + 'return ' + ($gcy - 8) + '-(b/m)*u*' + $gr)
    $fdot.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($fdot)
    $t = New-Text "d$slot-fc-v" $ix ($iy + 22) $iw 20 13 '' $script:MUTED 2 @{
        Text = BindJS 'Text' ($uJs + 'return Math.round(u*100)+"% grip"')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $items.Add((AddNote 'fc' 'Turn Telemetry FFB on for this game to see grip use.' 'Friction' $dFric))

    # ---------------- RELATIVE ---------------------------------------
    # Two cars ahead and two behind with their last lap, from the
    # tracker plugin rather than the obsolete leaderboard item.
    $vis = KeyVis 'Relative' $dOpp
    $items.Add((AddHead 'rel' 'RELATIVE' 'Relative'))
    $relRows = @(
        @('DriverAhead_01', $script:MUTED), @('DriverAhead_00', $script:WHITE),
        @('__ME__', $script:GREEN),
        @('DriverBehind_00', $script:WHITE), @('DriverBehind_01', $script:MUTED)
    )
    for ($r = 0; $r -lt $relRows.Count; $r++) {
        $ry = $iy + 28 + $r * 24
        $src = $relRows[$r][0]; $col = $relRows[$r][1]
        if ($src -eq '__ME__') {
            $posJs = 'var p=1*$prop("' + $SIM + 'Position");return isNaN(p)||p<=0?"-":"P"+p'
            $valJs = 'return "You"'
        } else {
            $posJs = 'var p=1*$prop("' + $TRK + $src + '_Position");return isNaN(p)||p<=0?"-":"P"+p'
            $valJs = 'var s=""+($prop("' + $TRK + $src + '_LastLapTime")||"");if(s.indexOf(".")>=0)s=s.substring(0,s.indexOf(".")+3);if(s.indexOf("00:")==0)s=s.substring(3);return s==""?"--":s'
        }
        $pt = New-Text "d$slot-rel$r-p" $ix $ry 48 22 15 '' $col 0 @{ Text = BindJS 'Text' $posJs } 'Bold'
        $pt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($pt)
        $nt = New-Text "d$slot-rel$r-n" ($ix + 52) $ry ($iw - 52) 22 15 '' $col 2 @{ Text = BindJS 'Text' $valJs }
        $nt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($nt)
    }
    $items.Add((AddNote 'rel' 'No other cars in this session.' 'Relative' $dOpp))

    # ---------------- RADAR (SimHub's own proximity item) ------------
    # One native item that draws itself from the session's opponents, so
    # it lights up in games reporting car positions and stays quiet in
    # the ones that do not.
    $vis = KeyVis 'Radar' $dOpp
    $items.Add((AddHead 'rd' 'RADAR' 'Radar'))
    # Square, so width caps it in the tall box: centre rather than pin.
    $rdSize = [math]::Min($iw, $h - 52)
    $radar = [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.RadarItem, SimHub.Plugins'
        BackgroundColor = $script:CLEAR
        Height = [double]$rdSize; Left = [double]($ix + ($iw - $rdSize) / 2)
        Top = [double](($iy + 30 + $y + $h - 12) / 2 - $rdSize / 2)
        Visible = $true; Width = [double]$rdSize
        Rotation = 0.0; RenderingSkip = 0; IsFreezed = $false
        Name = "d$slot-rd"
        Bindings = [ordered]@{ Visible = (BindJS 'Visible' $vis) }
    }
    $items.Add($radar)
    $items.Add((AddNote 'rd' 'No other cars in this session.' 'Radar' $dOpp))

    # ---------------- INPUTS (pedals + steering) ---------------------
    # What the driver did, next to what the wheel did. Throttle and brake
    # prefer SimHub's own properties (every game it reads reports them)
    # and fall back to our parse for the Forza-without-forwarding case.
    # Steering has NO SimHub equivalent (it exposes no universal steering
    # field), so that bar is ours alone and simply hides when the active
    # source does not report it.
    $vis = KeyVis 'Inputs' $null
    $items.Add((AddHead 'in' 'INPUTS' 'Inputs'))
    $barW = 46; $barGap = 20
    $barH = [math]::Max(60, $h - 118)
    $barY = $iy + 30
    $barX0 = $ix + ($iw - ($barW * 2 + $barGap)) / 2
    $pedals = @(
        @('thr', 'Throttle', $script:GREEN, 'var v=1*$prop("' + $SIM + 'Throttle");if(!(v>0))v=100*(1*$prop("' + $P + '.Throttle"));if(isNaN(v))v=0;if(v>100)v=100;if(v<0)v=0;'),
        @('brk', 'Brake',    $script:RED,   'var v=1*$prop("' + $SIM + 'Brake");if(!(v>0))v=100*(1*$prop("' + $P + '.Brake"));if(isNaN(v))v=0;if(v>100)v=100;if(v<0)v=0;')
    )
    for ($pi = 0; $pi -lt $pedals.Count; $pi++) {
        $pkey = $pedals[$pi][0]; $plabel = $pedals[$pi][1]
        $pcol = $pedals[$pi][2]; $pJs = $pedals[$pi][3]
        $px = $barX0 + $pi * ($barW + $barGap)
        $trough = New-Rect "d$slot-in-$pkey-bg" $px $barY $barW $barH $script:TILE $null 5
        $trough.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($trough)
        # Fill grows from the bottom, so Top moves as Height does.
        $fill = New-Rect "d$slot-in-$pkey" $px ($barY + $barH) $barW 2 $pcol $null 5
        $fill.Bindings['Height'] = BindJS 'Height' ($pJs + 'return Math.max(2,' + $barH + '*v/100)')
        $fill.Bindings['Top']    = BindJS 'Top'    ($pJs + 'return ' + ($barY + $barH) + '-Math.max(2,' + $barH + '*v/100)')
        $fill.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($fill)
        $t = New-Text "d$slot-in-$pkey-l" $px ($barY + $barH + 4) $barW 18 12 $plabel $script:MUTED 1
        $t.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($t)
    }
    # Steering: centre-origin bar. Hidden entirely when the source has no
    # steering to report, rather than sitting convincingly at centre.
    $steerJs = 'var s=1*$prop("' + $P + '.Steer");'
    $steerHas = 'return (' + ($isKeyBase = $sel + '=="Inputs"' + $rowCond) + ') && (1*$prop("' + $P + '.Steer"))>-1.5'
    $stY = $barY + $barH + 26
    $stTrough = New-Rect "d$slot-in-st-bg" $ix $stY $iw 10 $script:TILE $null 5
    $stTrough.Bindings['Visible'] = BindJS 'Visible' $steerHas
    $items.Add($stTrough)
    $stTick = New-Rect "d$slot-in-st-tick" ($ix + $iw / 2 - 1) ($stY - 3) 2 16 '#FF39404C' $null 0
    $stTick.Bindings['Visible'] = BindJS 'Visible' $steerHas
    $items.Add($stTick)
    $stDot = New-Rect "d$slot-in-st" ($ix + $iw / 2 - 7) ($stY - 2) 14 14 $script:WHITE $null 7
    $stDot.Bindings['Left'] = BindJS 'Left' ($steerJs + 'if(s<-1)s=-1;if(s>1)s=1;return ' + ($ix + $iw / 2 - 7) + '+s*' + (($iw - 14) / 2))
    $stDot.Bindings['Visible'] = BindJS 'Visible' $steerHas
    $items.Add($stDot)
    $t = New-Text "d$slot-in-st-l" $ix ($stY + 16) $iw 18 12 'Steering' $script:MUTED 1
    $t.Bindings['Visible'] = BindJS 'Visible' $steerHas
    $items.Add($t)

    # ---------------- VISUALIZER (ours) ------------------------------
    # The Visualizer tab in miniature, laid out the SAME way: the game's
    # steering force on its own lane up top, the Trueforce haptic
    # envelope on its own lane underneath, never stacked on each other.
    # The force line reddens on a clip exactly as the full screen does.
    # Sampled every other ring column so a box costs about half.
    $vis = KeyVis 'Scope' $null
    $items.Add((AddHead 'sc' 'VISUALIZER' 'Scope'))
    # CLIP and SPIKE badges, same contract as the full screen: grey at
    # rest, with a coloured layer and light text crossfading in on the
    # plugin-computed glow (1 at the event, decaying to 0).
    $badges = @(
        @('clip',  'CLIP',  $script:RED,    'Scope.FfbClipGlow', $script:WHITE),
        @('spike', 'SPIKE', '#FFE8C33D',    'Scope.SpikeGlow',   '#FF101216')
    )
    for ($bi = 0; $bi -lt $badges.Count; $bi++) {
        $bid = $badges[$bi][0]; $blabel = $badges[$bi][1]
        $bcol = $badges[$bi][2]; $bprop = $badges[$bi][3]; $btxt = $badges[$bi][4]
        $bw = 50; $bx = $ix + $iw - ($badges.Count - $bi) * ($bw + 6) + 6
        $glowJs = '(1*$prop("' + $P + '.' + $bprop + '"))'
        $r = New-Rect "d$slot-sc-$bid-bg" $bx ($iy + 2) $bw 18 $script:TILE $null 4
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $g = New-Rect "d$slot-sc-$bid-glow" $bx ($iy + 2) $bw 18 $bcol @{
            Opacity = BindJS 'Opacity' ('return 100*' + $glowJs)
        } 4
        $g.Opacity = 0.0
        $g.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($g)
        $t1 = New-Text "d$slot-sc-$bid-t" $bx ($iy + 2) $bw 18 11 $blabel $script:GRAY 1 $null 'Bold'
        $t1.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t1)
        $t2 = New-Text "d$slot-sc-$bid-t2" $bx ($iy + 2) $bw 18 11 $blabel $btxt 1 @{
            Opacity = BindJS 'Opacity' ('return 100*' + $glowJs)
        } 'Bold'
        $t2.Opacity = 0.0
        $t2.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t2)
    }
    $scTop   = $iy + 26
    $scTotal = $h - 46
    $scLane  = ($scTotal - 16) / 2      # two equal lanes with a gap between
    # --- upper lane: the game's FFB force, as a line ---
    $l1y = $scTop
    $l1mid = $l1y + $scLane / 2
    $z1 = New-Rect "d$slot-sc-z1" $ix $l1mid $iw 2 $script:SCOPE_GRID $null 0
    $z1.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($z1)
    # Clip threshold rails, dotted like the full screen: the force line
    # reaching them IS the clip, which is what the badge then reports.
    $railOff = ($scLane / 2) * 0.98
    foreach ($side in @(-1, 1)) {
        $ry = $l1mid + 1 - $side * $railOff
        for ($seg = 0; $seg -lt 12; $seg++) {
            $rx = $ix + $seg * ($iw / 12)
            $rail = New-Rect "d$slot-sc-rail$($side)_$seg" $rx $ry ($iw / 24) 2 '#66E5484D' $null 0
            $rail.Bindings['Visible'] = BindJS 'Visible' $vis
            $items.Add($rail)
        }
    }
    $clipGlowJs = '(1*$prop("' + $P + '.Scope.FfbClipGlow"))'
    $trace = New-Chart "d$slot-sc-tr" ($ix - 10) ($l1y - 10) ($iw + 20) ($scLane + 20) $script:SCOPE_AMBER 2 90 ('return 1*$prop("' + $P + '.Scope.Ffb77")')
    $trace.Bindings['LineColor'] = BindJS 'LineColor' ('var g=' + $clipGlowJs + ';if(g<0)g=0;if(g>1)g=1;var r=Math.round(227+g*2).toString(16);var q=Math.round(164-g*92).toString(16);var w=Math.round(69+g*8).toString(16);if(r.length<2)r="0"+r;if(q.length<2)q="0"+q;if(w.length<2)w="0"+w;return "#FF"+r+q+w')
    $trace.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($trace)
    # --- lower lane: the Trueforce haptic envelope, as columns ---
    $l2y = $scTop + $scLane + 16
    $l2mid = $l2y + $scLane / 2
    $z2 = New-Rect "d$slot-sc-z2" $ix $l2mid $iw 2 $script:SCOPE_GRID $null 0
    $z2.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($z2)
    $cols = 39
    $cw = $iw / $cols
    for ($c = 0; $c -lt $cols; $c++) {
        $src = $c * 2
        $colJs = 'var v=1*$prop("' + $P + '.Scope.Tex' + $src + '");if(v>1)v=1;if(v<0)v=0;'
        $col = New-Rect "d$slot-sc$c" ($ix + $c * $cw) $l2mid $cw 2 $script:SCOPE_PURPLE $null 0
        $col.Bindings['Height'] = BindJS 'Height' ($colJs + 'return 2+v*' + ($scLane - 4))
        $col.Bindings['Top']    = BindJS 'Top'    ($colJs + 'var hh=2+v*' + ($scLane - 4) + ';return ' + ($l2mid + 1) + '-hh/2')
        $col.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($col)
    }
    # Colour key, bottom left, the same pairing the full screen uses. It
    # sits over the quiet end of the envelope lane rather than taking a
    # row of its own, so it costs no height in a box this size.
    $lgY = $y + $h - 18
    $sw1 = New-Rect "d$slot-sc-lg1" $ix $lgY 9 9 $script:SCOPE_AMBER $null 2
    $sw1.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($sw1)
    $lt1 = New-Text "d$slot-sc-lg1t" ($ix + 13) ($lgY - 5) 64 18 10 'GAME FFB' $script:MUTED 0
    $lt1.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($lt1)
    $sw2 = New-Rect "d$slot-sc-lg2" ($ix + 78) $lgY 9 9 $script:SCOPE_PURPLE $null 2
    $sw2.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($sw2)
    $lt2 = New-Text "d$slot-sc-lg2t" ($ix + 91) ($lgY - 5) 70 18 10 'TRUEFORCE' $script:MUTED 0
    $lt2.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($lt2)

    # In the taller one-row layout a text stack would otherwise sit at the
    # top of a half-empty card, which reads as misaligned beside the boxes
    # that do fill their space. Nudge the text groups down so each card is
    # vertically centred. Visual content (tyres, both circles, radar, the
    # visualizer lanes) already sizes itself from the box height and is
    # deliberately left alone, as is the panel.
    # Measure each text group's real bounding box and centre THAT in the
    # panel, rather than assuming a nominal content height: the groups
    # differ (car facts is three rows, relative is five), so a fixed
    # offset leaves some of them sitting visibly high.
    if ($h -gt 212) {
        foreach ($g in 'cf', 'hm', 'pr', 'lt', 'fu', 'dl', 'rel') {
            $members = @($items | Where-Object { [string]$_.Name -match "^d$slot-$g(-|\d)" })
            if ($members.Count -eq 0) { continue }
            $minTop = ($members | ForEach-Object { [double]$_.Top } | Measure-Object -Minimum).Minimum
            $maxBot = ($members | ForEach-Object { [double]$_.Top + [double]$_.Height } | Measure-Object -Maximum).Maximum
            $dy = ($y + ($h - ($maxBot - $minTop)) / 2) - $minTop
            if ($dy -gt 0) { foreach ($m in $members) { $m.Top = [double]$m.Top + $dy } }
        }
    }
    $items
}

# A bottom box has two geometries: the two-row layout, and the taller
# one-row layout that reclaims the hidden top row. Rather than emit both
# sets of items, generate the box twice and merge, so any position or
# size that differs becomes one binding that picks by Dash.Drive.TwoRows.
# Items whose geometry is already a formula (the visualizer columns, the
# circle dots) keep both formulas, selected the same way.
function DriveBoxDual([string]$P, [int]$slot, $x, $w, $yTwo, $hTwo, $yOne, $hOne) {
    $a = @(DriveBox $P $slot $x $yTwo $w $hTwo $false)
    $b = @(DriveBox $P $slot $x $yOne $w $hOne $false)
    $tr = '$prop("' + $P + '.Drive.TwoRows")'
    for ($i = 0; $i -lt $a.Count; $i++) {
        $ia = $a[$i]; $ib = $b[$i]
        foreach ($pn in 'Top', 'Height', 'Left', 'Width') {
            if (-not $ia.Contains($pn)) { continue }
            $hasA = $ia.Bindings -and $ia.Bindings.Contains($pn)
            $hasB = $ib.Bindings -and $ib.Bindings.Contains($pn)
            if ($hasA -or $hasB) {
                # At least one side is a formula: wrap both bodies so the
                # multi-statement ones stay valid.
                $ea = if ($hasA) { '(function(){' + [string]$ia.Bindings[$pn].Formula.Expression + '})()' } else { [string]$ia.$pn }
                $eb = if ($hasB) { '(function(){' + [string]$ib.Bindings[$pn].Formula.Expression + '})()' } else { [string]$ib.$pn }
                if ($ea -ne $eb) { $ia.Bindings[$pn] = BindJS $pn ('return ' + $tr + '?' + $ea + ':' + $eb) }
            }
            elseif ([string]$ia.$pn -ne [string]$ib.$pn) {
                $ia.Bindings[$pn] = BindJS $pn ('return ' + $tr + '?' + $ia.$pn + ':' + $ib.$pn)
            }
        }
    }
    $a
}

function TabBar([string]$P) {
    $overlayClosed = '(""+$prop("' + $P + '.Overlay"))==""'
    # Enabled-slot count, summed from the seven .On props inside the formula
    # itself (no extra plugin property needed). Slots pack left, so the
    # visible slots are exactly 0..n-1 and each slot's Left/Width bind to
    # pitch = usable width / n: hiding a tab makes the rest stretch to
    # fill the bar. Plugin missing -> n falls back to 7 (bar is hidden
    # then anyway, .On reads null).
    $countJs = '($prop("' + $P + '.TabSlot0.On")?1:0)+($prop("' + $P + '.TabSlot1.On")?1:0)+($prop("' + $P + '.TabSlot2.On")?1:0)+($prop("' + $P + '.TabSlot3.On")?1:0)+($prop("' + $P + '.TabSlot4.On")?1:0)+($prop("' + $P + '.TabSlot5.On")?1:0)+($prop("' + $P + '.TabSlot6.On")?1:0)'
    $items = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt 7; $i++) {
        $x = 10 + $i * 112
        $slot = $P + '.TabSlot' + $i
        $vis = 'return ' + $overlayClosed + ' && $prop("' + $slot + '.On")'
        $leftJs  = 'var n=' + $countJs + ';if(n<1)n=7;return 10+' + $i + '*(784/n)'
        $widthJs = 'var n=' + $countJs + ';if(n<1)n=7;return (784/n)-4'
        $bg = New-Rect "tab$i-bg" $x 446 127 32 $TILE @{
            BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $slot + '.Active")?"' + $TILEON + '":"' + $TILE + '"')
            Left  = BindJS 'Left'  $leftJs
            Width = BindJS 'Width' $widthJs
        } 4
        $bg.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($bg)
        $t = New-Text "tab$i-t" $x 446 127 32 13 '' $MUTED 1 @{
            Text      = BindJS 'Text'      ('return ""+($prop("' + $slot + '.Label")||"")')
            TextColor = BindJS 'TextColor' ('return $prop("' + $slot + '.Active")?"' + $WHITE + '":"' + $MUTED + '"')
            Left      = BindJS 'Left'  $leftJs
            Width     = BindJS 'Width' $widthJs
        } 'Bold'
        $t.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($t)
        $b = New-Button "tab$i" $x 446 127 32 "DashTabSlotSelect$i" @{
            Left  = BindJS 'Left'  $leftJs
            Width = BindJS 'Width' $widthJs
        }
        $b.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($b)
    }
    $items
}

# Race flags (Dash.FlagsOn, off by default). A band across the top of
# whichever screen is showing, coloured like the flag being waved, so a
# glance at a wheel-mounted phone tells you what the marshals are doing.
# Reads SimHub's own flag properties, so it lights up in the games that
# report flags and stays silent in the ones that do not (Forza reports
# none, which is exactly why this is a toggle rather than always on).
# Drawn above the toast but below the rev strip.
function FlagBar([string]$P) {
    $F = 'DataCorePlugin.GameData.NewData.Flag_'
    $any = '$prop("' + $P + '.FlagsOn") && (' +
           '$prop("' + $F + 'Yellow")||$prop("' + $F + 'Blue")||$prop("' + $F + 'Black")' +
           '||$prop("' + $F + 'White")||$prop("' + $F + 'Checkered"))'
    $vis = 'return ' + $any
    # Colour follows the flag, checkered reads as white.
    $colJs = 'if($prop("' + $F + 'Yellow"))return "#F2E8C33D";' +
             'if($prop("' + $F + 'Blue"))return "#F23D7FE8";' +
             'if($prop("' + $F + 'Black"))return "#F21A1A1A";' +
             'if($prop("' + $F + 'White")||$prop("' + $F + 'Checkered"))return "#F2E8EAEE";' +
             'return "#00FFFFFF"'
    $txtJs = 'if($prop("' + $F + 'Black")||$prop("' + $F + 'Blue"))return "' + $script:WHITE + '";return "#FF101216"'
    $nameJs = 'var n=""+($prop("' + $F + 'Name")||"");if(n!="")return n.toUpperCase();' +
              'if($prop("' + $F + 'Yellow"))return "YELLOW FLAG";' +
              'if($prop("' + $F + 'Blue"))return "BLUE FLAG";' +
              'if($prop("' + $F + 'Black"))return "BLACK FLAG";' +
              'if($prop("' + $F + 'Checkered"))return "CHEQUERED FLAG";' +
              'if($prop("' + $F + 'White"))return "WHITE FLAG";return ""'
    $bg = New-Rect 'flag-bg' 0 14 800 48 $script:CLEAR @{
        BackgroundColor = BindJS 'BackgroundColor' $colJs
    } 0
    $bg.Bindings['Visible'] = BindJS 'Visible' $vis
    $t = New-Text 'flag-t' 0 14 800 48 24 '' $script:WHITE 1 @{
        Text      = BindJS 'Text'      $nameJs
        TextColor = BindJS 'TextColor' $txtJs
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis
    @($bg, $t)
}

# Transient feedback bar (Dash.Toast): the plugin stamps a message when an
# action cannot run (no game / no car / desktop edit open) and the property
# self-expires after ~3.5 s. Rendered topmost on every screen.
function ToastBar([string]$P) {
    $visExpr = 'return (""+$prop("' + $P + '.Toast"))!=""'
    $bg = New-Rect 'toast-bg' 100 204 600 72 '#F25A2626' $null 10
    $bg.Bindings['Visible'] = BindJS 'Visible' $visExpr
    $t = New-Text 'toast-t' 116 204 568 72 19 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('return ""+$prop("' + $P + '.Toast")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $visExpr
    @($bg, $t)
}

# Preset picker overlay (Dash.Overlay == "presets"): 8 name slots + paging.
# Slots with an empty name hide themselves; the active preset highlights.
function PresetOverlay([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add((OnOverlay (New-Rect 'pp-backdrop' 0 0 800 480 $script:BACKDROP $null 0) 'presets'))
    $items.Add((OnOverlay (New-Text 'pp-title' 0 14 800 30 20 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('return ""+$prop("' + $P + '.Preset.Title")')
    } 'Bold') 'presets'))
    for ($i = 1; $i -le 8; $i++) {
        $y = 46 + ($i - 1) * 46
        $slotProp = $P + '.Preset.Slot' + $i
        $visExpr = 'return (""+$prop("TrueforcePlugin.Dash.Overlay"))=="presets" && (""+$prop("' + $slotProp + '"))!=""'
        $bg = New-Rect "pp-slot$i-bg" 100 $y 520 42 $script:TILE @{
            BackgroundColor = BindJS 'BackgroundColor' ('return (""+$prop("' + $slotProp + '"))==(""+$prop("' + $P + '.Preset.Current"))?"' + $script:TILEON + '":"' + $script:TILE + '"')
        }
        $bg.Bindings['Visible'] = BindJS 'Visible' $visExpr
        $items.Add($bg)
        $t = New-Text "pp-slot$i-t" 116 $y 488 42 17 '' $script:WHITE 0 @{
            Text = BindJS 'Text' ('return ""+$prop("' + $slotProp + '")')
        }
        $t.Bindings['Visible'] = BindJS 'Visible' $visExpr
        $items.Add($t)
        $b = New-Button "pp-slot$i" 100 $y 520 42 "DashPresetSelect$i"
        $b.Bindings['Visible'] = BindJS 'Visible' $visExpr
        $items.Add($b)
    }
    # paging column on the right
    $items.Add((OnOverlay (New-Rect 'pp-prev-bg' 648 92 120 84 $script:TILE) 'presets'))
    $items.Add((OnOverlay (New-Text 'pp-prev-t' 648 92 120 84 26 'PREV' $script:WHITE 1 $null 'Bold') 'presets'))
    $items.Add((OnOverlay (New-Button 'pp-prev' 648 92 120 84 'DashPresetPrev') 'presets'))
    $items.Add((OnOverlay (New-Text 'pp-page' 648 186 120 32 17 '' $script:MUTED 1 @{
        Text = BindJS 'Text' ('return ""+$prop("' + $P + '.Preset.PageLabel")')
    }) 'presets'))
    $items.Add((OnOverlay (New-Rect 'pp-next-bg' 648 228 120 84 $script:TILE) 'presets'))
    $items.Add((OnOverlay (New-Text 'pp-next-t' 648 228 120 84 26 'NEXT' $script:WHITE 1 $null 'Bold') 'presets'))
    $items.Add((OnOverlay (New-Button 'pp-next' 648 228 120 84 'DashPresetNext') 'presets'))
    $items.Add((OnOverlay (New-Rect 'pp-cancel-bg' 100 428 520 42 $script:TILE) 'presets'))
    $items.Add((OnOverlay (New-Text 'pp-cancel-t' 100 428 520 42 17 'CANCEL' $script:RED 1 $null 'Bold') 'presets'))
    $items.Add((OnOverlay (New-Button 'pp-cancel' 100 428 520 42 'DashPresetClose') 'presets'))
    $items
}

# Simple stepper block: label text + minus/plus tiles. Returns items.
function StepperTiles([string]$prefix, $x, $y, $w, $h, [string]$downAction, [string]$upAction) {
    $half = [math]::Floor(($w - 10) / 2)
    @(
        (New-Rect  "$prefix-minus-bg" $x $y $half $h $TILE)
        (New-Text  "$prefix-minus-t"  $x $y $half $h 30 '-' $WHITE 1 $null 'Bold')
        (New-Button "$prefix-minus"   $x $y $half $h $downAction)
        (New-Rect  "$prefix-plus-bg" ($x + $half + 10) $y $half $h $TILE)
        (New-Text  "$prefix-plus-t"  ($x + $half + 10) $y $half $h 30 '+' $WHITE 1 $null 'Bold')
        (New-Button "$prefix-plus"   ($x + $half + 10) $y $half $h $upAction)
    )
}

$P = 'TrueforcePlugin.Dash'

# =====================================================================
# Screen 1: DRIVE
# =====================================================================
$s1 = [System.Collections.Generic.List[object]]::new()

$s1.Add((New-Text 'title' 16 16 240 36 24 'TF4ALL DASH' $WHITE 0 $null 'Bold'))
$s1.Add((New-Text 'wheel' 520 16 264 36 18 'WHEEL' $MUTED 2 @{
    Text      = BindJS 'Text'      ('return $prop("' + $P + '.WheelOk")?"WHEEL OK":"WHEEL OFFLINE"')
    TextColor = BindJS 'TextColor' ('return $prop("' + $P + '.WheelOk")?"' + $GREEN + '":"' + $RED + '"')
}))
$s1.Add((New-Text 'gamecar' 16 56 768 28 17 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Game")||"No game");var c=""+($prop("' + $P + '.CarName")||"");return c!=""?(g+"  -  "+c):g')
}))
$s1.Add((New-Text 'preset' 16 86 768 26 15 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?("PRESET  "+p):"PRESET  (manual tune)"')
}))

# Master gain (left column); the big value is a tap zone for exact entry
$s1.Add((New-Rect 'mg-panel' 16 116 376 240 $PANEL))
$s1.Add((New-Text 'mg-label' 32 128 344 26 16 'MASTER GAIN  (tap value to type)' $MUTED 0))
$s1.Add((New-Text 'mg-value' 32 156 344 96 64 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return (1*$prop("' + $P + '.MasterGain")).toFixed(2)')
} 'Bold'))
# Tap zone hugs the centered digits, not the card's full width: dead
# space beside the number must not open the keypad.
$s1.Add((New-Button 'mg-value-tap' 114 156 180 96 'DashMasterGainOpen'))
StepperTiles 'mg' 32 264 344 76 'DashMasterGainDown' 'DashMasterGainUp' | ForEach-Object { $s1.Add($_) }

# Audio gain (right column); same tap-to-type value
$s1.Add((New-Rect 'ag-panel' 408 116 376 240 $PANEL))
$s1.Add((New-Text 'ag-label' 424 128 344 26 16 '' $MUTED 0 @{
    Text = BindJS 'Text' ('return "AUDIO GAIN  "+($prop("' + $P + '.Fx.Audio.On")?"(ON)":"(OFF)")+"  (tap value to type)"')
}))
$s1.Add((New-Text 'ag-value' 424 156 344 96 64 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return (1*$prop("' + $P + '.AudioGain")).toFixed(2)')
} 'Bold'))
$s1.Add((New-Button 'ag-value-tap' 506 156 180 96 'DashAudioGainOpen'))
StepperTiles 'ag' 424 264 344 76 'DashAudioGainDown' 'DashAudioGainUp' | ForEach-Object { $s1.Add($_) }

# Bottom toggles: plugin on/off + audio on/off (above the tab bar)
$s1.Add((New-Rect 'plug-bg' 16 372 376 66 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.PluginOn")?"' + $TILEON + '":"' + $TILE + '"')
}))
$s1.Add((New-Text 'plug-t' 16 372 376 66 22 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.PluginOn")?"PLUGIN ON":"PLUGIN OFF"')
} 'Bold'))
$s1.Add((New-Button 'plug-btn' 16 372 376 66 'DashPluginToggle'))
$s1.Add((New-Rect 'aud-bg' 408 372 376 66 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.Fx.Audio.On")?"' + $TILEON + '":"' + $TILE + '"')
}))
$s1.Add((New-Text 'aud-t' 408 372 376 66 22 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.Fx.Audio.On")?"AUDIO HAPTICS ON":"AUDIO HAPTICS OFF"')
} 'Bold'))
$s1.Add((New-Button 'aud-btn' 408 372 376 66 'DashFxAudioToggle'))

TabBar $P | ForEach-Object { $s1.Add($_) }
KeypadOverlay $P | ForEach-Object { $s1.Add($_) }
FlagBar $P | ForEach-Object { $s1.Add($_) }
ToastBar $P | ForEach-Object { $s1.Add($_) }
RevStrip $P | ForEach-Object { $s1.Add($_) }

# =====================================================================
# Screen 2: CAR FACTS (+ layout picker overlay + redline keypad overlay)
# =====================================================================
$s2 = [System.Collections.Generic.List[object]]::new()

$s2.Add((New-Text 'cf-title' 16 16 200 36 24 'CAR FACTS' $WHITE 0 $null 'Bold'))
$s2.Add((New-Text 'cf-car' 224 16 560 36 18 '' $MUTED 2 @{
    Text = BindJS 'Text' ('return ""+($prop("' + $P + '.CarName")||"No car detected")')
}))

# Engine row: tap the value to open the layout picker. The two cards +
# info line sit 32px below the natural flow so the block reads centered
# between the title and the footer note.
$s2.Add((New-Rect 'cf-eng-panel' 16 92 768 108 $PANEL))
$s2.Add((New-Text 'cf-eng-label' 32 102 400 24 15 'ENGINE LAYOUT' $MUTED 0))
$s2.Add((New-Text 'cf-eng-value' 32 128 600 60 34 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var s=""+($prop("' + $P + '.EngineLayoutSource")||"");var l=""+($prop("' + $P + '.EngineLayout")||"Auto");return s!=""?(l+"  ("+s+")"):l')
} 'Bold'))
$s2.Add((New-Rect 'cf-eng-hint' 648 116 120 60 $TILE))
$s2.Add((New-Text 'cf-eng-hint-t' 648 116 120 60 17 'CHANGE' $WHITE 1))
$s2.Add((New-Button 'cf-eng-btn' 648 116 120 60 'DashEngineLayoutOpen'))

# Redline row: tap value = keypad; +/- 50 steppers on the right
$s2.Add((New-Rect 'cf-rl-panel' 16 212 768 120 $PANEL))
$s2.Add((New-Text 'cf-rl-label' 32 222 400 24 15 'REDLINE  (tap value to type it)' $MUTED 0))
$s2.Add((New-Text 'cf-rl-value' 32 250 360 68 44 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var r=1*$prop("' + $P + '.Redline");return r>0?(r+" rpm"):"not set"')
} 'Bold'))
$s2.Add((New-Button 'cf-rl-open' 16 212 400 120 'DashRedlineOpen'))
$s2.Add((New-Rect 'cf-rl-dn-bg' 432 240 160 72 $TILE))
$s2.Add((New-Text 'cf-rl-dn-t' 432 240 160 72 24 '-50' $WHITE 1 $null 'Bold'))
$s2.Add((New-Button 'cf-rl-dn' 432 240 160 72 'DashRedlineDown'))
$s2.Add((New-Rect 'cf-rl-up-bg' 608 240 160 72 $TILE))
$s2.Add((New-Text 'cf-rl-up-t' 608 240 160 72 24 '+50' $WHITE 1 $null 'Bold'))
$s2.Add((New-Button 'cf-rl-up' 608 240 160 72 'DashRedlineUp'))

$s2.Add((New-Text 'cf-info' 32 348 736 28 16 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var m=1*$prop("' + $P + '.MaxRpm");var s=""+($prop("' + $P + '.RedlineSource")||"");var t=m>0?("MAX RPM  "+m):"";if(s!=""&&s!="none"){t+=(t!=""?"      ":"")+"REDLINE SOURCE  "+s}return t')
}))
$s2.Add((New-Text 'cf-note' 32 414 736 26 14 'Edits save to this car and apply instantly. Sharing follows your community settings.' $GRAY 0))

# ---- overlay: engine layout picker ----
$layouts = @(
    @('Auto',             'Auto (detect)'),
    @('Electric',         'Electric'),
    @('Single',           'Single'),
    @('Twin',             'Twin'),
    @('Twin180',          'Twin 180'),
    @('VTwin45',          'V-twin 45'),
    @('VTwin60',          'V-twin 60'),
    @('VTwin90',          'V-twin 90'),
    @('Inline3',          'Inline 3'),
    @('Inline4',          'Inline 4'),
    @('Inline4CrossPlane','I4 crossplane'),
    @('Inline5',          'Inline 5'),
    @('Inline6',          'Inline 6'),
    @('Boxer4',           'Boxer 4'),
    @('Boxer6',           'Boxer 6'),
    @('V4',               'V4'),
    @('V4TwinPulse',      'V4 twin-pulse'),
    @('V6_60Even',        'V6 even-fire'),
    @('V6_OddFire',       'V6 odd-fire'),
    @('V8CrossPlane',     'V8 crossplane'),
    @('V8FlatPlane',      'V8 flatplane'),
    @('V10_72',           'V10'),
    @('V12_60',           'V12'),
    @('W12_W16',          'W12 / W16'),
    @('Rotary1',          'Rotary 1'),
    @('Rotary2',          'Rotary 2'),
    @('Rotary3',          'Rotary 3'),
    @('Rotary4',          'Rotary 4')
)
# Shared so the Drive tab's car-facts box can open the same picker.
function EngineLayoutOverlay([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add((OnOverlay (New-Rect 'lp-backdrop' 0 0 800 480 $script:BACKDROP $null 0) 'layout'))
    $items.Add((OnOverlay (New-Text 'lp-title' 0 12 800 32 20 'ENGINE LAYOUT' $script:WHITE 1 $null 'Bold') 'layout'))
    for ($i = 0; $i -lt $script:layouts.Count; $i++) {
        $enum = $script:layouts[$i][0]; $label = $script:layouts[$i][1]
        $c = $i % 4; $r = [math]::Floor($i / 4)
        $x = 8 + $c * 198; $y = 44 + $r * 56
        $bgBind = @{ BackgroundColor = BindJS 'BackgroundColor' ('return (""+$prop("' + $P + '.EnginePin"))=="' + $enum + '"?"' + $script:TILEON + '":"' + $script:TILE + '"') }
        $items.Add((OnOverlay (New-Rect  "lp-$enum-bg" $x $y 190 50 $script:TILE $bgBind) 'layout'))
        $items.Add((OnOverlay (New-Text  "lp-$enum-t"  $x $y 190 50 16 $label $script:WHITE 1) 'layout'))
        $items.Add((OnOverlay (New-Button "lp-$enum"   $x $y 190 50 "DashEngineLayoutSet_$enum") 'layout'))
    }
    # cancel occupies the last free grid slot (28 layouts fill 7 rows exactly, so add a bar)
    $items.Add((OnOverlay (New-Rect 'lp-cancel-bg' 8 438 782 36 $script:TILE) 'layout'))
    $items.Add((OnOverlay (New-Text 'lp-cancel-t' 8 438 782 36 16 'CANCEL' $script:RED 1 $null 'Bold') 'layout'))
    $items.Add((OnOverlay (New-Button 'lp-cancel' 8 438 782 36 'DashEngineLayoutClose') 'layout'))
    $items
}
EngineLayoutOverlay $P | ForEach-Object { $s2.Add($_) }

TabBar $P | ForEach-Object { $s2.Add($_) }
# ---- overlay: shared keypad (redline entry opens it via DashRedlineOpen) ----
KeypadOverlay $P | ForEach-Object { $s2.Add($_) }
FlagBar $P | ForEach-Object { $s2.Add($_) }
ToastBar $P | ForEach-Object { $s2.Add($_) }
RevStrip $P | ForEach-Object { $s2.Add($_) }

# =====================================================================
# Screen 3: EFFECTS (13 rows in 2 columns: toggle tile + gain readout + steppers)
# Airborne ducking is deliberately absent: it is a background modifier with
# no gain, not a feel effect to tune from a phone; toggle it on the desktop.
# =====================================================================
$s3 = [System.Collections.Generic.List[object]]::new()
$s3.Add((New-Text 'fx-title' 16 14 400 34 22 'EFFECTS' $WHITE 0 $null 'Bold'))

$effects = @(
    @('Engine',     'Engine pulse',  $true),
    @('Bumps',      'Road bumps',    $true),
    @('Traction',   'Traction loss', $true),
    @('AxleSlip',   'Axle slip',     $true),
    @('Kerb',       'Kerb thump',    $true),
    @('Lockup',     'Lockup judder', $true),
    @('Shift',      'Gear shift',    $true),
    @('Abs',        'ABS',           $true),
    @('Pit',        'Pit limiter',   $true),
    @('Drs',        'DRS',           $true),
    @('Collision',  'Collision',     $true),
    @('RevLimiter', 'Redline buzz',  $true),
    @('Audio',      'Audio haptics', $true)
)
# Row layout: name tile (tap = toggle), then a [-] value [+] cluster so the
# steppers visually flank the value they change (a trailing -/+ pair read as
# ambiguous between neighboring rows on a real screen). Tapping the value
# opens the keypad for exact entry. Wide gutter between the two columns.
for ($i = 0; $i -lt $effects.Count; $i++) {
    $key = $effects[$i][0]; $label = $effects[$i][1]; $hasGain = $effects[$i][2]
    $col = [math]::Floor($i / 7); $row = $i % 7
    $x = 10 + $col * 404; $y = 50 + $row * 56
    # audio routes to its dedicated actions (peer voice, not a TelemetryEffect)
    $tgl  = if ($key -eq 'Audio') { 'DashFxAudioToggle' } else { "DashFx${key}Toggle" }
    $up   = if ($key -eq 'Audio') { 'DashAudioGainUp' }   else { "DashFx${key}GainUp" }
    $dn   = if ($key -eq 'Audio') { 'DashAudioGainDown' } else { "DashFx${key}GainDown" }
    $open = if ($key -eq 'Audio') { 'DashAudioGainOpen' } else { "DashFx${key}GainOpen" }
    $onProp = $P + '.Fx.' + $key + '.On'
    $s3.Add((New-Rect "fx-$key-bg" $x $y 170 50 $TILE @{
        BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $onProp + '")?"' + $TILEON + '":"' + $TILE + '"')
    }))
    $s3.Add((New-Text "fx-$key-t" ($x + 8) $y 156 50 16 $label $WHITE 0 @{
        TextColor = BindJS 'TextColor' ('return $prop("' + $onProp + '")?"' + $WHITE + '":"' + $GRAY + '"')
    }))
    $s3.Add((New-Button "fx-$key-tgl" $x $y 170 50 $tgl))
    if (-not $hasGain) { continue }
    $s3.Add((New-Rect  "fx-$key-dn-bg" ($x + 176) $y 50 50 $TILE))
    $s3.Add((New-Text  "fx-$key-dn-t"  ($x + 176) $y 50 50 26 '-' $WHITE 1 $null 'Bold'))
    $s3.Add((New-Button "fx-$key-dn"   ($x + 176) $y 50 50 $dn))
    $s3.Add((New-Rect "fx-$key-gain-bg" ($x + 230) $y 82 50 $PANEL $null 0))
    $s3.Add((New-Text "fx-$key-gain" ($x + 230) $y 82 50 17 '' $WHITE 1 @{
        Text = BindJS 'Text' ('return (1*$prop("' + $P + '.Fx.' + $key + '.Gain")).toFixed(3)')
    }))
    $s3.Add((New-Button "fx-$key-gain-tap" ($x + 230) $y 82 50 $open))
    $s3.Add((New-Rect  "fx-$key-up-bg" ($x + 316) $y 50 50 $TILE))
    $s3.Add((New-Text  "fx-$key-up-t"  ($x + 316) $y 50 50 26 '+' $WHITE 1 $null 'Bold'))
    $s3.Add((New-Button "fx-$key-up"   ($x + 316) $y 50 50 $up))
}
# Save/Revert bar, top right, visible only while unsaved dash tuning
# exists (effect/audio edits are drafts; a car change or restart drops
# them, so the bar is the "keep this" affordance). REVERT additionally
# gates on CanRevert: an anchor-less edit (no active preset) lights the
# bar so SAVE can capture it into a new preset, but has no saved baseline
# to revert to, so the revert control must not appear with nothing to do.
$dirtyVis  = 'return $prop("' + $P + '.TuningDirty")'
$revertVis = 'return $prop("' + $P + '.TuningDirty") && $prop("' + $P + '.CanRevert")'
$saveBar = @(
    (New-Text 'fx-dirty-t' 380 16 144 30 14 'UNSAVED' '#FFE8A33D' 2 $null 'Bold'),
    (New-Rect 'fx-save-bg' 652 14 132 32 $TILEON),
    (New-Text 'fx-save-t' 652 14 132 32 14 'SAVE' $WHITE 1 $null 'Bold'),
    (New-Button 'fx-save' 652 14 132 32 'DashTuneSaveOpen')
)
$revertBar = @(
    (New-Rect 'fx-revert-bg' 536 14 110 32 $TILE),
    (New-Text 'fx-revert-t' 536 14 110 32 14 'REVERT' $RED 1 $null 'Bold'),
    (New-Button 'fx-revert' 536 14 110 32 'DashTuneRevert')
)
foreach ($b in $saveBar)   { $b.Bindings['Visible'] = BindJS 'Visible' $dirtyVis;  $s3.Add($b) }
foreach ($b in $revertBar) { $b.Bindings['Visible'] = BindJS 'Visible' $revertVis; $s3.Add($b) }

TabBar $P | ForEach-Object { $s3.Add($_) }
KeypadOverlay $P | ForEach-Object { $s3.Add($_) }

# ---- overlay: save scope chooser (Dash.Overlay == "savescope") ----
$s3.Add((OnOverlay (New-Rect 'ss-backdrop' 0 0 800 480 $BACKDROP $null 0) 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-title' 0 20 800 32 22 'SAVE TUNING TO' $WHITE 1 $null 'Bold') 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-context' 0 56 800 26 14 '' $MUTED 1 @{
    Text = BindJS 'Text' ('return ""+$prop("' + $P + '.SaveContext")')
}) 'savescope'))
$s3.Add((OnOverlay (New-Rect 'ss-car-bg' 220 100 360 64 $TILE) 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-car-t' 220 100 360 64 19 'THIS CAR ONLY' $WHITE 1 $null 'Bold') 'savescope'))
$s3.Add((OnOverlay (New-Button 'ss-car' 220 100 360 64 'DashTuneSaveCar') 'savescope'))
$s3.Add((OnOverlay (New-Rect 'ss-game-bg' 220 174 360 64 $TILE) 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-game-t' 220 174 360 64 19 'GAME PRESET' $WHITE 1 $null 'Bold') 'savescope'))
$s3.Add((OnOverlay (New-Button 'ss-game' 220 174 360 64 'DashTuneSaveGame') 'savescope'))
$s3.Add((OnOverlay (New-Rect 'ss-both-bg' 220 248 360 64 $TILEON) 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-both-t' 220 248 360 64 19 'BOTH' $WHITE 1 $null 'Bold') 'savescope'))
$s3.Add((OnOverlay (New-Button 'ss-both' 220 248 360 64 'DashTuneSaveBoth') 'savescope'))
$s3.Add((OnOverlay (New-Rect 'ss-cancel-bg' 220 372 360 44 $TILE) 'savescope'))
$s3.Add((OnOverlay (New-Text 'ss-cancel-t' 220 372 360 44 15 'CANCEL' $RED 1 $null 'Bold') 'savescope'))
$s3.Add((OnOverlay (New-Button 'ss-cancel' 220 372 360 44 'DashTuneSaveCancel') 'savescope'))
FlagBar $P | ForEach-Object { $s3.Add($_) }
ToastBar $P | ForEach-Object { $s3.Add($_) }
RevStrip $P | ForEach-Object { $s3.Add($_) }

# =====================================================================
# Screen 4: PRESETS (game preset + car preset, picker overlay)
# =====================================================================
$s4 = [System.Collections.Generic.List[object]]::new()
$s4.Add((New-Text 'pr-title' 16 16 300 36 24 'PRESETS' $WHITE 0 $null 'Bold'))
$s4.Add((New-Text 'pr-car' 320 16 464 36 16 '' $MUTED 2 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Game")||"No game");var c=""+($prop("' + $P + '.CarName")||"");return c!=""?(g+"  -  "+c):g')
}))

$s4.Add((New-Rect 'pr-game-panel' 16 64 768 150 $PANEL))
$s4.Add((New-Text 'pr-game-label' 32 76 500 24 15 'GAME PRESET  (applies to the whole game)' $MUTED 0))
$s4.Add((New-Text 'pr-game-value' 32 104 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?p:"(manual tune)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-game-hint' 648 96 120 84 $TILE))
$s4.Add((New-Text 'pr-game-hint-t' 648 96 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-game-btn' 648 96 120 84 'DashPresetOpenGame'))

$s4.Add((New-Rect 'pr-carp-panel' 16 228 768 150 $PANEL))
$s4.Add((New-Text 'pr-carp-label' 32 240 500 24 15 'CAR PRESET  (this car only)' $MUTED 0))
$s4.Add((New-Text 'pr-carp-value' 32 268 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.CarPresetName")||"");return p!=""?p:"(none saved for this car)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-carp-hint' 648 260 120 84 $TILE))
$s4.Add((New-Text 'pr-carp-hint-t' 648 260 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-carp-btn' 648 260 120 84 'DashPresetOpenCar'))


TabBar $P | ForEach-Object { $s4.Add($_) }
PresetOverlay $P | ForEach-Object { $s4.Add($_) }
FlagBar $P | ForEach-Object { $s4.Add($_) }
ToastBar $P | ForEach-Object { $s4.Add($_) }
RevStrip $P | ForEach-Object { $s4.Add($_) }

# =====================================================================
# Screen 5: VISUALIZER (scrolling signal waveforms, stacked lanes)
# Top lane = the game's FFB steering force as a smooth ChartItem line
# (amber); bottom lane = the Trueforce haptic signal actually streaming
# to the wheel, drawn as a mirrored envelope from the plugin's 78-column
# 32 ms ring (purple). Palette mirrors the FFB-architecture doc (base
# amber / tf purple).
# =====================================================================
$SCOPE_AMBER  = '#FFE3A445'
$SCOPE_PURPLE = '#FFA08CFF'
$SCOPE_GRID   = '#FF262F3A'
$s5 = [System.Collections.Generic.List[object]]::new()
$s5.Add((New-Text 'sc-title' 16 18 300 34 22 'VISUALIZER' $WHITE 0 $null 'Bold'))
# CLIP + SPIKE badges: gray at rest; a colored layer + text crossfade in
# while active and fade back out over ~1.5 s, driven by the plugin-computed
# glow properties (1 while active, linear decay to 0). CLIP = red, the FFB
# line hit the rails; SPIKE = yellow, spike reduction actively softened the
# force (dark text: white on yellow is unreadable).
$clipGlow = '(1*$prop("' + $P + '.Scope.FfbClipGlow"))'
$s5.Add((New-Rect 'sc-clip-bg' 446 25 58 20 $TILE $null 4))
$clipGlowBg = New-Rect 'sc-clip-glow' 446 25 58 20 $RED @{
    Opacity = BindJS 'Opacity' ('return 100*' + $clipGlow)
} 4
$clipGlowBg.Opacity = 0.0
$s5.Add($clipGlowBg)
$s5.Add((New-Text 'sc-clip-t' 446 25 58 20 12 'CLIP' $GRAY 1 $null 'Bold'))
$clipGlowT = New-Text 'sc-clip-t2' 446 25 58 20 12 'CLIP' $WHITE 1 @{
    Opacity = BindJS 'Opacity' ('return 100*' + $clipGlow)
} 'Bold'
$clipGlowT.Opacity = 0.0
$s5.Add($clipGlowT)
$spikeGlow = '(1*$prop("' + $P + '.Scope.SpikeGlow"))'
$s5.Add((New-Rect 'sc-spike-bg' 384 25 58 20 $TILE $null 4))
$spikeGlowBg = New-Rect 'sc-spike-glow' 384 25 58 20 $YELLOW @{
    Opacity = BindJS 'Opacity' ('return 100*' + $spikeGlow)
} 4
$spikeGlowBg.Opacity = 0.0
$s5.Add($spikeGlowBg)
$s5.Add((New-Text 'sc-spike-t' 384 25 58 20 12 'SPIKE' $GRAY 1 $null 'Bold'))
$spikeGlowT = New-Text 'sc-spike-t2' 384 25 58 20 12 'SPIKE' '#FF1A1A1A' 1 @{
    Opacity = BindJS 'Opacity' ('return 100*' + $spikeGlow)
} 'Bold'
$spikeGlowT.Opacity = 0.0
$s5.Add($spikeGlowT)
$s5.Add((New-Rect 'sc-leg1-sw' 520 28 14 14 $SCOPE_AMBER $null 2))
$s5.Add((New-Text 'sc-leg1-t' 540 18 106 34 13 'GAME FFB' $MUTED 0))
$s5.Add((New-Rect 'sc-leg2-sw' 650 28 14 14 $SCOPE_PURPLE $null 2))
$s5.Add((New-Text 'sc-leg2-t' 670 18 120 34 13 'TRUEFORCE' $MUTED 0))

$s5.Add((New-Text 'sc-ffb-label' 16 50 400 20 13 'GAME FFB (as sent to the wheel)' $MUTED 0))
$s5.Add((New-Rect 'sc-ffb-panel' 10 74 780 160 $PANEL))
$s5.Add((New-Rect 'sc-ffb-zero' 12 153 776 2 $SCOPE_GRID $null 0))
# Thin dotted red lines at the clip DETECTION threshold (0.98 of full
# scale, matching the plugin's latch on the drawn value): y = 154 -/+
# 0.98*76 = 79.5 / 228.5. The line can push a hair past them to the
# absolute rails at 78/230. Faint alpha keeps them subordinate.
for ($i = 0; $i -lt 49; $i++) {
    $x = 12 + $i * 16
    $s5.Add((New-Rect "sc-railt$i" $x 78.5 8 2 '#66E5484D' $null 0))
    $s5.Add((New-Rect "sc-railb$i" $x 227.5 8 2 '#66E5484D' $null 0))
}
# Smooth connected line: one ChartItem sampling the NEWEST ring slot
# (index 77; plugin-smoothed). Item bounds run 10 px beyond the intended
# band on every side (see New-Chart), so the inner drawing area is
# 12..788 x 78..230: the +/-1 rails land exactly on the dotted lines and
# the zero on the grid line at 154. The line color lerps amber -> red by
# FfbClipGlow: full red while clipping, crossfading back over 1.5 s.
$clipVal = '(1*$prop("' + $P + '.Scope.FfbClip"))'
$ffbTrace = New-Chart 'sc-ffb-trace' 2 68 796 172 $SCOPE_AMBER 2 120 ('return 1*$prop("' + $P + '.Scope.Ffb77")')
$ffbTrace.Bindings['LineColor'] = BindJS 'LineColor' ('var g=' + $clipGlow + ';if(g<0)g=0;if(g>1)g=1;var r=Math.round(227+g*2).toString(16);var q=Math.round(164-g*92).toString(16);var w=Math.round(69+g*8).toString(16);if(r.length<2)r="0"+r;if(q.length<2)q="0"+q;if(w.length<2)w="0"+w;return "#FF"+r+q+w')
$s5.Add($ffbTrace)
# Clip rail markers: thin ChartItems on the same server-push timeline as
# the trace, drawn OVER it. While clipping, the marker level sits ON the
# dotted threshold line (covering the pinned line section); at rest the
# level is OUT of the chart's Min/Max range, which the viewer
# canvas-clips away entirely (no masks needed). Top strip inner band
# 71.5..79.5 (clip level = value 0 = 79.5); bottom strip inner band
# 228.5..236.5 (clip level = value 1 = 228.5); resting values 2 / -1
# fall outside the clip rect.
$clipPos = New-Chart 'sc-clip-pos' 2 61.5 796 28 $RED 3 120 ('return ' + $clipVal + '>0?0:2')
$clipPos.Minimum = 0.0
$s5.Add($clipPos)
$clipNeg = New-Chart 'sc-clip-neg' 2 218.5 796 28 $RED 3 120 ('return ' + $clipVal + '<0?1:-1')
$clipNeg.Minimum = 0.0
$s5.Add($clipNeg)

$s5.Add((New-Text 'sc-tex-label' 16 240 400 20 13 'TRUEFORCE HAPTIC SIGNAL' $MUTED 0))
$s5.Add((New-Rect 'sc-tex-panel' 10 262 780 164 $PANEL))
$s5.Add((New-Rect 'sc-tex-zero' 12 343 776 2 $SCOPE_GRID $null 0))
# Full-width columns (no gaps) render the envelope as one solid filled
# waveform silhouette rather than separated bars.
for ($i = 0; $i -lt 78; $i++) {
    $x = 10 + $i * 10
    $col = New-Rect "sc-tex$i" $x 343 10 2 $SCOPE_PURPLE $null 0
    $col.Bindings['Height'] = BindJS 'Height' ('var v=1*$prop("' + $P + '.Scope.Tex' + $i + '");if(v>1)v=1;if(v<0)v=0;return 2+v*160')
    $col.Bindings['Top'] = BindJS 'Top' ('var v=1*$prop("' + $P + '.Scope.Tex' + $i + '");if(v>1)v=1;if(v<0)v=0;var h=2+v*160;return 344-h/2')
    $s5.Add($col)
}
$s5.Add((New-Text 'sc-hint' 16 428 768 16 12 'Scrolls left, about 2.5 seconds of history. Red = FFB clipping. Yellow = spike reduction.' $GRAY 0))

TabBar $P | ForEach-Object { $s5.Add($_) }
FlagBar $P | ForEach-Object { $s5.Add($_) }
ToastBar $P | ForEach-Object { $s5.Add($_) }
RevStrip $P | ForEach-Object { $s5.Add($_) }

# =====================================================================
# Screen 6: TELE-FFB (Telemetry FFB / Mode B tuning)
# Mode B settings are GLOBAL (no preset/car scope): edits apply live and
# persist immediately, so this screen has no save/revert bar. Everything
# below the title gates on Dash.ModeB.Supported (the Forza titles);
# unsupported games get an explainer instead of dead controls.
# =====================================================================
$s6 = [System.Collections.Generic.List[object]]::new()
$s6.Add((New-Text 'mb-title' 16 14 300 34 22 'TELEMETRY FFB' $WHITE 0 $null 'Bold'))
$s6.Add((New-Text 'mb-game' 320 14 464 34 16 '' $MUTED 2 @{
    Text = BindJS 'Text' ('return ""+($prop("' + $P + '.Game")||"No game")')
}))

$mbSupported = '$prop("' + $P + '.ModeB.Supported")'
# Gated content collects here; every item gets a Supported Visible
# binding stamped below (Hide-ButtonsUnderOverlay then ANDs the
# overlay-closed gate onto the buttons).
$mbGated = [System.Collections.Generic.List[object]]::new()

# Toggle tiles: per-game enable + rev lights
$mbGated.Add((New-Rect 'mb-en-bg' 16 54 376 54 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.ModeB.On")?"' + $TILEON + '":"' + $TILE + '"')
}))
$mbGated.Add((New-Text 'mb-en-t' 16 54 376 54 19 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.ModeB.On")?"TELEMETRY FFB ON":"TELEMETRY FFB OFF"')
} 'Bold'))
$mbGated.Add((New-Button 'mb-en-btn' 16 54 376 54 'DashModeBToggle'))
$mbGated.Add((New-Rect 'mb-rl-bg' 408 54 376 54 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.ModeB.RevLightsOn")?"' + $TILEON + '":"' + $TILE + '"')
}))
$mbGated.Add((New-Text 'mb-rl-t' 408 54 376 54 19 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.ModeB.RevLightsOn")?"REV LIGHTS ON":"REV LIGHTS OFF"')
} 'Bold'))
$mbGated.Add((New-Button 'mb-rl-btn' 408 54 376 54 'DashModeBRevLightsToggle'))

# Knob rows, Effects-screen geometry: label + [-] value [+]; the value is
# a tap zone for the shared keypad. Keys/steps/ranges live plugin-side in
# the _dashModeB table; toFixed decimals mirror the desktop readouts.
$mbKnobs = @(
    @('Strength', 'Strength',           2),
    @('MinForce', 'Min force',          2),
    @('Damper',   'Damping',            2),
    @('Center',   'Centering',          2),
    @('Lat',      'Cornering weight',   2),
    @('Rise',     'Weight buildup',     2),
    @('Reversal', 'Reversal damping',   2),
    @('Smooth',   'Smoothing ms',       0)
)
for ($i = 0; $i -lt $mbKnobs.Count; $i++) {
    $key = $mbKnobs[$i][0]; $label = $mbKnobs[$i][1]; $dec = $mbKnobs[$i][2]
    $col = [math]::Floor($i / 4); $row = $i % 4
    $x = 10 + $col * 404; $y = 126 + $row * 56
    $mbGated.Add((New-Text "mb-$key-t" ($x + 8) $y 160 50 16 $label $WHITE 0))
    $mbGated.Add((New-Rect  "mb-$key-dn-bg" ($x + 176) $y 50 50 $TILE))
    $mbGated.Add((New-Text  "mb-$key-dn-t"  ($x + 176) $y 50 50 26 '-' $WHITE 1 $null 'Bold'))
    $mbGated.Add((New-Button "mb-$key-dn"   ($x + 176) $y 50 50 "DashModeB${key}Down"))
    $mbGated.Add((New-Rect "mb-$key-val-bg" ($x + 230) $y 82 50 $PANEL $null 0))
    $mbGated.Add((New-Text "mb-$key-val" ($x + 230) $y 82 50 17 '' $WHITE 1 @{
        Text = BindJS 'Text' ('return (1*$prop("' + $P + '.ModeB.' + $key + '")).toFixed(' + $dec + ')')
    }))
    $mbGated.Add((New-Button "mb-$key-val-tap" ($x + 230) $y 82 50 "DashModeB${key}Open"))
    $mbGated.Add((New-Rect  "mb-$key-up-bg" ($x + 316) $y 50 50 $TILE))
    $mbGated.Add((New-Text  "mb-$key-up-t"  ($x + 316) $y 50 50 26 '+' $WHITE 1 $null 'Bold'))
    $mbGated.Add((New-Button "mb-$key-up"   ($x + 316) $y 50 50 "DashModeB${key}Up"))
}
$mbGated.Add((New-Text 'mb-hint' 16 394 768 44 14 'Tap a value to type an exact number. Changes apply instantly and are shared across games. More options live on the desktop Telemetry FFB tab.' $GRAY 0))
foreach ($it in $mbGated) {
    $it.Bindings['Visible'] = BindJS 'Visible' ('return ' + $mbSupported + '?true:false')
    $s6.Add($it)
}

# Unsupported-game explainer (inverse gate; also what shows in menus)
$mbNote1 = New-Text 'mb-na-t1' 0 180 800 34 22 'Telemetry FFB is not available for this game' $WHITE 1 $null 'Bold'
$mbNote1.Bindings['Visible'] = BindJS 'Visible' ('return !' + $mbSupported)
$s6.Add($mbNote1)
$mbNote2 = New-Text 'mb-na-t2' 60 224 680 60 16 'It works in Forza Motorsport and Forza Horizon 4, 5 and 6. Start one of those games to tune it here.' $MUTED 1
$mbNote2.Bindings['Visible'] = BindJS 'Visible' ('return !' + $mbSupported)
$s6.Add($mbNote2)

TabBar $P | ForEach-Object { $s6.Add($_) }
KeypadOverlay $P | ForEach-Object { $s6.Add($_) }
FlagBar $P | ForEach-Object { $s6.Add($_) }
ToastBar $P | ForEach-Object { $s6.Add($_) }
RevStrip $P | ForEach-Object { $s6.Add($_) }

# =====================================================================
# Screen 7: DRIVE (gear + four swappable info boxes)
# The while-driving screen: gear big in the middle, four boxes whose
# contents the user picks in Settings. Two-row is the tablet layout;
# turning it off hides the top pair and grows the gear into that space,
# which is the phone layout. The gear moves via Left/Top/Width/Height
# bindings rather than a second set of items, and the boxes never move,
# so nothing inside them needs repositioning.
# =====================================================================
$s7 = [System.Collections.Generic.List[object]]::new()
$twoRows = '$prop("' + $P + '.Drive.TwoRows")'

# Gear: the one readout that is always meaningful, in every game.
# The gear keeps the middle column in both layouts (the bottom boxes
# grow into the top row rather than across it) and just recentres.
# Our own frame first, SimHub second: a Forza player usually leaves
# forwarding off, which leaves SimHub's copies empty all session.
$gear = New-Text 'dr-gear' 300 55 200 210 130 '' $WHITE 1 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Gear")||"");' +
                          'if(g=="")g=""+($prop("' + $SIM + 'Gear")||"");return g==""?"N":g')
    Top  = BindJS 'Top'  ('return ' + $twoRows + '?55:130')
} 'Bold'
$s7.Add($gear)
# Speed follows SimHub's unit setting when SimHub has the data; from our
# own frame it is km/h, converted here when the user is set to MPH.
$spd = New-Text 'dr-speed' 300 268 200 40 26 '' $MUTED 1 @{
    Text = BindJS 'Text' ('var u=""+($prop("' + $SIM + 'SpeedLocalUnit")||"");' +
                          'var v=1*$prop("' + $SIM + 'SpeedLocal");' +
                          'if(!(v>0)){var k=1*$prop("' + $P + '.SpeedKmh");' +
                          'if(!(k>0))return "--";v=(u=="MPH")?k*0.621371:k;if(u=="")u="KMH";}' +
                          'return Math.round(v)+" "+u')
    Top  = BindJS 'Top'  ('return ' + $twoRows + '?268:343')
} 'Bold'
$s7.Add($spd)

# Four content boxes. Slot order matches the plugin: TL, TR, BL, BR.
# The top pair simply hides in the one-row layout; the bottom pair grows
# up into the space it leaves, so nothing is wasted on a phone.
DriveBox $P 0 10  16  282 206 $true  | ForEach-Object { $s7.Add($_) }
DriveBox $P 1 508 16  282 206 $true  | ForEach-Object { $s7.Add($_) }
DriveBoxDual $P 2 10  282 228 212 16 424 | ForEach-Object { $s7.Add($_) }
DriveBoxDual $P 3 508 282 228 212 16 424 | ForEach-Object { $s7.Add($_) }

TabBar $P | ForEach-Object { $s7.Add($_) }
# The interactive boxes reuse the same overlays their full tabs use, so
# a tap on Drive never bounces the user to another screen.
KeypadOverlay $P | ForEach-Object { $s7.Add($_) }
EngineLayoutOverlay $P | ForEach-Object { $s7.Add($_) }
PresetOverlay $P | ForEach-Object { $s7.Add($_) }
FlagBar $P | ForEach-Object { $s7.Add($_) }
ToastBar $P | ForEach-Object { $s7.Add($_) }
RevStrip $P | ForEach-Object { $s7.Add($_) }

# =====================================================================
# Assemble document
# =====================================================================
# Each screen is enabled ONLY while Dash.Tab holds its index, so the
# plugin owns navigation and the tab bar is the way around (screen swipes
# walk enabled screens only, so they are inert). With the plugin missing,
# $prop returns null and 1*null==0, which keeps Drive visible instead of
# a blank dash.
# The viewer gives item VISUALS pointer-events:none, so an overlay
# backdrop cannot shield the buttons beneath it: any ungated ButtonItem
# stays tappable while an overlay is up (same quirk the tab bar works
# around). Gate every screen button on "no overlay open". Buttons whose
# visibility already tests Dash.Overlay (overlay members, tab bar) keep
# their own gate; any other existing condition (SAVE/REVERT dirty bar)
# is ANDed with it.
function Hide-ButtonsUnderOverlay($items) {
    $closed = '(""+$prop("TrueforcePlugin.Dash.Overlay"))==""'
    foreach ($it in $items) {
        if ([string]$it.'$type' -notlike '*ButtonItem*') { continue }
        $vis = $null
        if ($it.Bindings -and $it.Bindings.Contains('Visible')) { $vis = $it.Bindings['Visible'] }
        if ($vis -and ([string]$vis.Formula.Expression) -like '*Dash.Overlay*') { continue }
        if ($vis) {
            $inner = ([string]$vis.Formula.Expression) -replace '^return ', ''
            $it.Bindings['Visible'] = BindJS 'Visible' ('return ' + $closed + ' && (' + $inner + ')')
        } else {
            $it.Bindings['Visible'] = BindJS 'Visible' ('return ' + $closed)
        }
    }
    $items
}

function New-Screen([string]$name, $items, [int]$tabIndex) {
    [ordered]@{
        Name = $name; InGameScreen = $true; IdleScreen = $true; PitScreen = $false
        ScreenId = [guid]::NewGuid().ToString()
        AllowOverlays = $true; IsForegroundLayer = $false; IsOverlayLayer = $false
        OverlayTriggerExpression = [ordered]@{ Expression = '' }
        ScreenEnabledExpression  = [ordered]@{ JSExt = 1; Interpreter = 1; Expression = 'return (1*$prop("TrueforcePlugin.Dash.Tab"))==' + $tabIndex }
        OverlayMaxDuration = 0; OverlayMinDuration = 0; IsBackgroundLayer = $false
        BackgroundColor = $CLEAR
        Items = @(Hide-ButtonsUnderOverlay $items)
    }
}

$meta = [ordered]@{
    Category = 'TF4ALL'; Title = 'TF4ALL Dash'
    Description = 'Control panel and rev lights for Trueforce For All: gains, effects, car facts and presets from a phone or tablet'
    Author = 'Mhytee'
    ScreenCount = 7.0
    InGameScreensIndexs = @(0, 1, 2, 3, 4, 5, 6)
    IdleScreensIndexs = @(0, 1, 2, 3, 4, 5, 6)
    MainPreviewIndex = 0
    IsOverlay = $false
    Width = 800.0; Height = 480.0
    OverlaySizeWarning = $false; MetadataVersion = 2.0
    EnableOnDashboardMessaging = $false
    PitScreensIndexs = @()
}

$doc = [ordered]@{
    DashboardDebugManager = [ordered]@{ Maximized = $false }
    Version = 2
    Id = [guid]::NewGuid().ToString()
    BaseHeight = 480; BaseWidth = 800
    BackgroundColor = $BG
    Screens = @(
        (New-Screen 'Home' $s1 0),
        (New-Screen 'Car facts' $s2 1),
        (New-Screen 'Effects' $s3 2),
        (New-Screen 'Presets' $s4 3),
        (New-Screen 'Visualizer' $s5 4),
        (New-Screen 'Tele-FFB' $s6 5),
        (New-Screen 'Drive' $s7 6)
    )
    SnapToGrid = $false; HideLabels = $false
    ShowForeground = $true; ForegroundOpacity = 50.0
    ShowBackground = $true; BackgroundOpacity = 50.0
    ShowBoundingRectangles = $false; GridSize = 10
    Images = @()
    Metadata = $meta
    ShowOnScreenControls = $true
    IsOverlay = $false
    EnableClickThroughOverlay = $true
    EnableOnDashboardMessaging = $false
}

$json = $doc | ConvertTo-Json -Depth 60
[IO.File]::WriteAllText((Join-Path $OutDir 'TF4ALL Dash.djson'), $json, [Text.UTF8Encoding]::new($false))
$metaJson = $meta | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $OutDir 'TF4ALL Dash.djson.metadata'), $metaJson, [Text.UTF8Encoding]::new($false))

# =====================================================================
# Preview images. SimHub shows "<name>.djson.png" as the dash thumbnail
# and "<name>.djson.NN.png" per screen (normally written by Dash Studio
# on save; this dash never round-trips through the editor, so we render
# our own). GDI+ walk of the same item lists: rects + text only, items
# with a Visible binding (overlays, toast) stay hidden unless a preview
# override shows them. Overrides also stand in for bound text/waveforms.
# =====================================================================
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
        return $p
    }
    $d = 2 * $r
    if ($d -gt $w) { $d = $w }
    if ($d -gt $h) { $d = $h }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Render-Preview($items, [hashtable]$ov, [string]$outPath) {
    $bmp = New-Object System.Drawing.Bitmap 800, 480
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml($script:BG))
    foreach ($it in $items) {
        $name = [string]$it.Name
        $o = $null
        if ($ov -and $ov.ContainsKey($name)) { $o = $ov[$name] }
        $show = $o -and $o.ContainsKey('Show') -and $o.Show
        if ($it.Bindings -and $it.Bindings.Contains('Visible') -and -not $show) { continue }
        # Items resting at Opacity 0 (badge glow layers) stay hidden.
        if ($it.Contains('Opacity') -and [double]$it.Opacity -le 0) { continue }
        $x = [float]$it.Left; $y = [float]$it.Top; $w = [float]$it.Width; $h = [float]$it.Height
        if ($o -and $o.ContainsKey('Left'))   { $x = [float]$o.Left }
        if ($o -and $o.ContainsKey('Top'))    { $y = [float]$o.Top }
        if ($o -and $o.ContainsKey('Width'))  { $w = [float]$o.Width }
        if ($o -and $o.ContainsKey('Height')) { $h = [float]$o.Height }
        $type = [string]$it.'$type'
        if ($type -like '*RectangleItem*') {
            $fill = [string]$it.BackgroundColor
            if ($o -and $o.ContainsKey('BackgroundColor')) { $fill = [string]$o.BackgroundColor }
            # Rotation matches the live viewer: CSS transform, center pivot.
            $rot = 0.0
            if ($o -and $o.ContainsKey('Rotation')) { $rot = [double]$o.Rotation }
            if ($rot -ne 0) {
                $g.TranslateTransform($x + $w / 2, $y + $h / 2)
                $g.RotateTransform([float]$rot)
                $path = New-RoundedPath (-$w / 2) (-$h / 2) $w $h ([float]$it.BorderStyle.RadiusTopLeft)
            } else {
                $path = New-RoundedPath $x $y $w $h ([float]$it.BorderStyle.RadiusTopLeft)
            }
            $c = [System.Drawing.ColorTranslator]::FromHtml($fill)
            if ($c.A -gt 0) {
                $br = New-Object System.Drawing.SolidBrush $c
                $g.FillPath($br, $path); $br.Dispose()
            }
            if ([int]$it.BorderTop -gt 0) {
                $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml([string]$it.BorderColor)), 1
                $g.DrawPath($pen, $path); $pen.Dispose()
            }
            $path.Dispose()
            if ($rot -ne 0) { $g.ResetTransform() }
        } elseif ($type -like '*TextItem*') {
            $txt = [string]$it.Text
            $tc  = [string]$it.TextColor
            if ($o -and $o.ContainsKey('Text'))      { $txt = [string]$o.Text }
            if ($o -and $o.ContainsKey('TextColor')) { $tc  = [string]$o.TextColor }
            if ($txt -eq '') { continue }
            $style = if ([string]$it.FontWeight -eq 'Bold') { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
            $font = New-Object System.Drawing.Font 'Segoe UI', ([float]$it.FontSize), $style, ([System.Drawing.GraphicsUnit]::Pixel)
            $fmt = New-Object System.Drawing.StringFormat
            $fmt.Alignment = @([System.Drawing.StringAlignment]::Near, [System.Drawing.StringAlignment]::Center, [System.Drawing.StringAlignment]::Far)[[int]$it.HorizontalAlignment]
            $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
            $br = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($tc))
            $g.DrawString($txt, $font, $br, (New-Object System.Drawing.RectangleF($x, $y, $w, $h)), $fmt)
            $br.Dispose(); $font.Dispose(); $fmt.Dispose()
        }
        elseif ($type -like '*ChartItem*') {
            # Charts sample live data, so previews must supply the trace via
            # a 'Points' override. Mirrors the live web renderer: 10 px inner
            # margins, values NOT clamped to Min/Max, drawing canvas-clipped
            # to the margin rect +/- LineTickness (out-of-range hides).
            if ($o -and $o.ContainsKey('Points')) {
                $pts = @($o.Points)
                if ($pts.Count -ge 2) {
                    $min = [double]$it.Minimum; $max = [double]$it.Maximum
                    $thick = [float][int]$it.LineTickness
                    $innerW = $w - 20; $innerH = $h - 20
                    $clipY = $y + [math]::Max(0, 10 - $thick)
                    $clipH = [math]::Min($h, $h - 20 + 2 * $thick)
                    $g.SetClip((New-Object System.Drawing.RectangleF(($x + 10), $clipY, $innerW, $clipH)))
                    $arr = New-Object 'System.Drawing.PointF[]' $pts.Count
                    for ($k = 0; $k -lt $pts.Count; $k++) {
                        $v = [double]$pts[$k]
                        $px = $x + 10 + $innerW * $k / [double]$it.PointsCount
                        $py = $y + 10 + $innerH * (1 - (($v - $min) / ($max - $min)))
                        $arr[$k] = New-Object System.Drawing.PointF ([float]$px), ([float]$py)
                    }
                    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml([string]$it.LineColor)), $thick
                    $g.DrawLines($pen, $arr); $pen.Dispose()
                    $g.ResetClip()
                }
            }
        }
        # ButtonItems are transparent tap zones: nothing to draw.
    }
    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Per-screen chrome: rev strip at ~78% of redline (segments 0-8 lit,
# left-to-right thresholds) + the tab bar. The bar's labels and highlight
# are property-bound at runtime (slot indirection), so previews supply
# the factory layout and the screen's own slot as active.
function PreviewChrome([double]$pct, [int]$activeSlot) {
    $o = @{}
    for ($i = 0; $i -lt 16; $i++) {
        if ($pct -ge (50 + $i * 3.125)) { $o["rev-seg$i"] = @{ Show = $true } }
    }
    # Factory tab order (Drive leads), matching DashTabFactoryOrder.
    $slotNames = @('DRIVE', 'HOME', 'CAR FACTS', 'EFFECTS', 'TELE-FFB', 'PRESETS', 'VISUALIZER')
    $pitch = 784 / $slotNames.Count
    for ($i = 0; $i -lt $slotNames.Count; $i++) {
        $bgc = if ($i -eq $activeSlot) { $TILEON } else { $TILE }
        $txc = if ($i -eq $activeSlot) { $WHITE } else { $MUTED }
        # Slot geometry is Left/Width-bound at runtime; the preview renderer
        # reads static values, so restate the packed positions here.
        $left = 10 + $i * $pitch
        $o["tab$i-bg"] = @{ Show = $true; BackgroundColor = $bgc; Left = $left; Width = ($pitch - 4) }
        $o["tab$i-t"]  = @{ Show = $true; Text = $slotNames[$i]; TextColor = $txc; Left = $left; Width = ($pitch - 4) }
    }
    $o
}

$pvGame   = 'Assetto Corsa'
$pvCar    = 'Mazda MX-5 Cup'
$pvPreset = 'Assetto Corsa (default)'

$ovDrive = PreviewChrome 78 1
$ovDrive['wheel']    = @{ Text = 'WHEEL OK'; TextColor = $GREEN }
$ovDrive['gamecar']  = @{ Text = "$pvGame  -  $pvCar" }
$ovDrive['preset']   = @{ Text = "PRESET  $pvPreset" }
$ovDrive['mg-value'] = @{ Text = '1.00' }
$ovDrive['ag-label'] = @{ Text = 'AUDIO GAIN  (ON)  (tap value to type)' }
$ovDrive['ag-value'] = @{ Text = '0.55' }
$ovDrive['plug-bg']  = @{ BackgroundColor = $TILEON }
$ovDrive['plug-t']   = @{ Text = 'PLUGIN ON' }
$ovDrive['aud-bg']   = @{ BackgroundColor = $TILEON }
$ovDrive['aud-t']    = @{ Text = 'AUDIO HAPTICS ON' }

$ovFacts = PreviewChrome 78 2
$ovFacts['cf-car']       = @{ Text = $pvCar }
$ovFacts['cf-eng-value'] = @{ Text = 'Inline 4  (community)' }
$ovFacts['cf-rl-value']  = @{ Text = '7200 rpm' }
$ovFacts['cf-info']      = @{ Text = 'MAX RPM  7500      REDLINE SOURCE  community' }

$ovFx = PreviewChrome 78 3
$pvGains = @{
    Engine = '0.850'; Bumps = '0.600'; Traction = '0.550'; AxleSlip = '0.450'
    Kerb = '0.700'; Lockup = '0.500'; Shift = '0.400'; Abs = '0.350'
    Pit = '0.300'; Drs = '0.250'; Collision = '0.800'; RevLimiter = '0.650'; Audio = '0.550'
}
foreach ($e in $effects) {
    $key = $e[0]
    $ovFx["fx-$key-bg"] = @{ BackgroundColor = $TILEON }
    if ($e[2]) { $ovFx["fx-$key-gain"] = @{ Text = $pvGains[$key] } }
}

$ovPresets = PreviewChrome 78 5
$ovPresets['pr-car']        = @{ Text = "$pvGame  -  $pvCar" }
$ovPresets['pr-game-value'] = @{ Text = $pvPreset }
$ovPresets['pr-carp-value'] = @{ Text = '(none saved for this car)' }

# Visualizer: synthesize a plausible trace (signed FFB wave for the
# chart, bursty haptic envelope with the same geometry math the live
# column bindings use). Wave amplitude deliberately exceeds 1 so the
# preview shows the clip feature: the pinned sections read at the rails
# with the marker strips lit over them.
$ovScope = PreviewChrome 78 6
$ffbPts = @(); $clipPosPts = @(); $clipNegPts = @()
for ($i = 0; $i -lt 120; $i++) {
    $v = 0.88 * [math]::Sin($i / 8.5) + 0.36 * [math]::Sin($i / 2.9 + 1.3)
    $ffbPts += [math]::Max(-1.0, [math]::Min(1.0, $v))   # plugin clamps before publishing
    $clipPosPts += $(if ($v -ge 0.995) { 0 } else { 2 })
    $clipNegPts += $(if ($v -le -0.995) { 1 } else { -1 })
}
$ovScope['sc-ffb-trace'] = @{ Points = $ffbPts }
$ovScope['sc-clip-pos']  = @{ Points = $clipPosPts }
$ovScope['sc-clip-neg']  = @{ Points = $clipNegPts }
for ($i = 0; $i -lt 78; $i++) {
    $t = (0.2 + 0.75 * [math]::Abs([math]::Sin($i / 9))) * [math]::Abs([math]::Sin($i / 2.1))
    $th = 2 + $t * 160
    $ovScope["sc-tex$i"] = @{ Top = 344 - $th / 2; Height = $th }
}

# Tele-FFB: gated content hides behind Visible bindings, so the preview
# shows the supported-game state via Show overrides + the owner recipe.
$ovModeB = PreviewChrome 78 4
$ovModeB['mb-game']  = @{ Text = 'Forza Horizon 6' }
$ovModeB['mb-en-bg'] = @{ Show = $true; BackgroundColor = $TILEON }
$ovModeB['mb-en-t']  = @{ Show = $true; Text = 'TELEMETRY FFB ON' }
$ovModeB['mb-rl-bg'] = @{ Show = $true; BackgroundColor = $TILEON }
$ovModeB['mb-rl-t']  = @{ Show = $true; Text = 'REV LIGHTS ON' }
$ovModeB['mb-hint']  = @{ Show = $true }
$pvModeB = @{
    Strength = '0.50'; MinForce = '0.05'; Damper = '0.07'; Center = '0.25'
    Lat = '0.60'; Rise = '0.80'; Reversal = '0.50'; Smooth = '40'
}
foreach ($k in $pvModeB.Keys) {
    $ovModeB["mb-$k-t"]      = @{ Show = $true }
    $ovModeB["mb-$k-dn-bg"]  = @{ Show = $true }
    $ovModeB["mb-$k-dn-t"]   = @{ Show = $true }
    $ovModeB["mb-$k-val-bg"] = @{ Show = $true }
    $ovModeB["mb-$k-val"]    = @{ Show = $true; Text = $pvModeB[$k] }
    $ovModeB["mb-$k-up-bg"]  = @{ Show = $true }
    $ovModeB["mb-$k-up-t"]   = @{ Show = $true }
}

# Drive tab: show the factory box assignment (car facts / lap times /
# scope / tyre temps) in the two-row layout, since every box item is
# slot-gated and therefore hidden to the preview renderer by default.
$ovDriveTab = PreviewChrome 78 0
$ovDriveTab['dr-gear']  = @{ Text = '4' }
$ovDriveTab['dr-speed'] = @{ Text = '148 kph' }
foreach ($sl in 0, 1, 2, 3) { $ovDriveTab["d$sl-panel"] = @{ Show = $true } }
# slot 0: car facts
$ovDriveTab['d0-cf-h']      = @{ Show = $true }
$ovDriveTab['d0-cf-car']    = @{ Show = $true; Text = '1985 Sprinter Trueno' }
$ovDriveTab['d0-cf-eng-l']  = @{ Show = $true }
$ovDriveTab['d0-cf-eng-v']  = @{ Show = $true; Text = 'Inline 4' }
$ovDriveTab['d0-cf-redl']   = @{ Show = $true }
$ovDriveTab['d0-cf-red-v']  = @{ Show = $true; Text = '7800 rpm' }
$ovDriveTab['d0-cf-rdn-bg'] = @{ Show = $true }
$ovDriveTab['d0-cf-rdn-t']  = @{ Show = $true }
$ovDriveTab['d0-cf-rup-bg'] = @{ Show = $true }
$ovDriveTab['d0-cf-rup-t']  = @{ Show = $true }
$ovDriveTab['d0-cf-info']   = @{ Show = $true; Text = 'MAX 8100   COMMUNITY' }
# slot 1: lap times
$ovDriveTab['d1-lt-h']      = @{ Show = $true }
$ovDriveTab['d1-lt-cur']    = @{ Show = $true; Text = '01:24.318' }
$ovDriveTab['d1-lt-last-l'] = @{ Show = $true }
$ovDriveTab['d1-lt-last-v'] = @{ Show = $true; Text = '01:25.902' }
$ovDriveTab['d1-lt-best-l'] = @{ Show = $true }
$ovDriveTab['d1-lt-best-v'] = @{ Show = $true; Text = '01:23.771' }
# slot 2: the Visualizer in miniature, two lanes exactly like the tab.
# Geometry mirrors DriveBox: box y=228 h=212 -> inner y 238, lanes of 75
# with a 16 px gap, force line on top and haptic envelope below.
$ovDriveTab['d2-sc-h']  = @{ Show = $true }
$ovDriveTab['d2-sc-z1'] = @{ Show = $true }
$ovDriveTab['d2-sc-z2'] = @{ Show = $true }
# Badges at rest: grey chrome, glow layers stay hidden at Opacity 0.
$ovDriveTab['d2-sc-clip-bg']  = @{ Show = $true }
$ovDriveTab['d2-sc-clip-t']   = @{ Show = $true }
$ovDriveTab['d2-sc-spike-bg'] = @{ Show = $true }
$ovDriveTab['d2-sc-spike-t']  = @{ Show = $true }
$ovDriveTab['d2-sc-lg1']  = @{ Show = $true }
$ovDriveTab['d2-sc-lg1t'] = @{ Show = $true }
$ovDriveTab['d2-sc-lg2']  = @{ Show = $true }
$ovDriveTab['d2-sc-lg2t'] = @{ Show = $true }
foreach ($side in @(-1, 1)) { for ($seg = 0; $seg -lt 12; $seg++) { $ovDriveTab["d2-sc-rail$($side)_$seg"] = @{ Show = $true } } }
$scLane = ((212 - 46) - 16) / 2
$l2mid  = 228 + 10 + 26 + $scLane + 16 + $scLane / 2
for ($i = 0; $i -lt 39; $i++) {
    $v = (0.25 + 0.7 * [math]::Abs([math]::Sin($i / 5.1))) * [math]::Abs([math]::Sin($i / 2.3))
    $hh = 2 + $v * ($scLane - 4)
    $ovDriveTab["d2-sc$i"] = @{ Show = $true; Top = ($l2mid + 1 - $hh / 2); Height = $hh }
}
$scPts = @()
for ($i = 0; $i -lt 90; $i++) { $scPts += (0.72 * [math]::Sin($i / 7.5) + 0.24 * [math]::Sin($i / 2.6 + 0.8)) }
$ovDriveTab['d2-sc-tr'] = @{ Show = $true; Points = $scPts }
# The preview shows the SHIPPED defaults, so boxes that are not part of
# the default set (inputs, circles, relative, radar) have no override
# here and correctly stay hidden in the thumbnail.
# slot 3: tyre temps as coloured blocks
$ovDriveTab['d3-tt-h'] = @{ Show = $true }
$tq  = @(86, 84, 79, 108)
$tqc = @($GREEN, $GREEN, $GREEN, $RED)
for ($i = 0; $i -lt 4; $i++) {
    $ovDriveTab["d3-tt$i"]    = @{ Show = $true; BackgroundColor = $tqc[$i] }
    $ovDriveTab["d3-tt$i-v"]  = @{ Show = $true; Text = ([string]$tq[$i]) }
}

Render-Preview $s1 $ovDrive   (Join-Path $OutDir 'TF4ALL Dash.djson.00.png')
Render-Preview $s2 $ovFacts   (Join-Path $OutDir 'TF4ALL Dash.djson.01.png')
Render-Preview $s3 $ovFx      (Join-Path $OutDir 'TF4ALL Dash.djson.02.png')
Render-Preview $s4 $ovPresets (Join-Path $OutDir 'TF4ALL Dash.djson.03.png')
Render-Preview $s5 $ovScope   (Join-Path $OutDir 'TF4ALL Dash.djson.04.png')
Render-Preview $s6 $ovModeB   (Join-Path $OutDir 'TF4ALL Dash.djson.05.png')
Render-Preview $s7 $ovDriveTab (Join-Path $OutDir 'TF4ALL Dash.djson.06.png')
Copy-Item (Join-Path $OutDir 'TF4ALL Dash.djson.00.png') (Join-Path $OutDir 'TF4ALL Dash.djson.png') -Force

$itemCount = $s1.Count + $s2.Count + $s3.Count + $s4.Count + $s5.Count + $s6.Count + $s7.Count
Write-Host "Wrote $OutDir  (items: $itemCount; drive=$($s1.Count) carfacts=$($s2.Count) effects=$($s3.Count) presets=$($s4.Count) visualizer=$($s5.Count) teleffb=$($s6.Count) drivetab=$($s7.Count); previews: main + 7 screens)"
