# Generates the "TF4ALL Dash" DashStudio dashboard (.djson + .metadata).
# Item schemas mirror shipped dashes (RSC - Toggle Switch / MobileDash):
# TextItem / RectangleItem for visuals, transparent ButtonItem tap zones with
# TriggerAction = "TrueforcePlugin.<DashAction>". All formulas use the JS
# interpreter (Interpreter=1) with $prop(). JS string literals use double
# quotes so these PS single-quoted strings stay readable.

$ErrorActionPreference = 'Stop'
$OutDir = Join-Path $PSScriptRoot 'TF4ALL Dash'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---- theme ----
# Colours are not baked. The dashboard binds its structural colours to
# Dash.Theme.*, which the plugin serves from whichever palette is selected,
# so picking a theme in Settings repaints every screen live with no reload.
#
# Only STRUCTURE is themed. Green for good and red for trouble stay put: a
# theme that can turn a warning green is a theme that can lie.
#
# The constants below are the static fallbacks. They are what a preview
# renders and what shows for the instant before the first binding runs, so
# they match the shipped default palette (Midnight).
$TH = 'TrueforcePlugin.Dash.Theme.'
function ThemeBind([string]$target, [string]$key) {
    BindJS $target ('return ""+$prop("' + $script:TH + $key + '")')
}
# Stamps the theme onto an already-built rect, for the lightened areas
# inside a card and the slice of background that breaks a card's top
# border: both have to track the palette or the seams show.
function ThemePaint($rect, [string]$key) {
    $rect.Bindings['BackgroundColor'] = ThemeBind 'BackgroundColor' $key
    $rect
}

# ---- palette (static fallbacks) ----
$BG       = '#FF000000'   # dashboard background
$PANEL    = '#00FFFFFF'   # cards are an outline; the edge does the work
$CARD_EDGE = '#FF4E5668'
# $PANEL had been doing three jobs: the big cards, the lightened areas
# inside them, and the background of small buttons. Unfilling it for the
# outlined look made every button vanish, which is why these are separate.
#   New-Card  the outlined container
#   SUBPANEL  a lightened area inside a card, barely there
#   New-Btn   something you press, always solid
# How far the card title sits BELOW the border it breaks. Centred exactly
# on the line made the tops of the cards read unevenly against each other;
# dropping it a few pixels keeps the break while giving the title room to
# breathe. One number, so it stays easy to re-tune by eye.
$HEAD_DROP = 6
$SUBPANEL = '#FF0E0E10'
$BTN      = '#FF1C1C20'
$BTN_EDGE = '#FF3A4150'
# Hairlines: ring outlines, rev sockets, tick marks. These live HERE with
# the rest of the palette, not beside the scope colours further down: the
# screens are built as the file runs, so a colour defined halfway through
# is null for everything above it. That shipped 72 items with an empty
# BackgroundColor, which SimHub cannot parse, and the dashboard stopped
# opening at all.
$LINE     = '#FF39404C'   # themed as Dim
$REVBG    = '#FF15181E'   # themed as Sub

# Every big container goes through here, so a new box cannot quietly opt
# out of the theme. The border is always ONE pixel and only its COLOUR
# changes: a palette that wants no outline sets it transparent, because
# thickness is geometry and geometry does not switch as cleanly as colour.
function New-Card([string]$name, $x, $y, $w, $h, [int]$radius = 10) {
    $r = New-Rect $name $x $y $w $h $script:PANEL $null $radius
    $r.BorderStyle.BorderColor = $script:CARD_EDGE
    $r.BorderColor = $script:CARD_EDGE
    foreach ($sd in 'Top', 'Bottom', 'Left', 'Right') {
        $r.BorderStyle."Border$sd" = 1
        $r."Border$sd" = 1
    }
    $r.Bindings['BackgroundColor'] = ThemeBind 'BackgroundColor' 'Card'
    # Into BorderStyle.Bindings, NOT the item's own. The viewer takes the
    # outline from BorderStyle and looks for its binding in the same place;
    # one put on the item is accepted, saved, and never read. That is why
    # themes appeared to leave every box edge alone.
    $r.BorderStyle.Bindings['BorderColor'] = ThemeBind 'BorderColor' 'CardEdge'
    $r
}

function New-Btn([string]$name, $x, $y, $w, $h, [int]$radius = 4) {
    $r = New-Rect $name $x $y $w $h $script:BTN $null $radius
    $r.BorderStyle.BorderColor = $script:BTN_EDGE
    $r.BorderColor = $script:BTN_EDGE
    foreach ($sd in 'Top', 'Bottom', 'Left', 'Right') {
        $r.BorderStyle."Border$sd" = 1
        $r."Border$sd" = 1
    }
    $r.Bindings['BackgroundColor'] = ThemeBind 'BackgroundColor' 'Btn'
    $r.BorderStyle.Bindings['BorderColor'] = ThemeBind 'BorderColor' 'BtnEdge'
    $r
}

$TILE    = '#FF141414'   # buttons / tiles (off state)
$TILEON  = '#FF23503A'   # toggle tile on state
$GREEN   = '#FF37D67A'
$RED     = '#FFE5484D'
$YELLOW  = '#FFE8C547'   # spike-reduction badge lit state
# Text is white or grey, never coloured, and these three match the tones
# the plugin serves for Text/Muted/Dim. They used to be a blue-tinted white
# and a blue-grey, which is subtle in isolation and obvious once a theme
# put a warm or green outline next to it. They are also baked into ~120
# computed colour expressions where no theme pass can reach them, so the
# constants themselves have to be right.
$WHITE   = '#FFF4F4F4'
$MUTED   = '#FFA0A0A0'
$GRAY    = '#FF6E6E6E'
$CLEAR   = '#00FFFFFF'

# Tyre temperature ramp: blue, green, yellow, orange, red, interpolated
# rather than stepped, because a tyre does not change state at a threshold
# and the drift toward the next colour IS the reading. Breakpoints match
# the old stepped scale, so a tyre that read amber still does. The blue
# lead-in keeps cold readable as cold: without it a tyre with no heat in
# it shows the same green as one in its window. One table: the dash
# formula and the preview renderer are both generated from it, so a
# thumbnail can never show a colour the dash would not.
# Built with the unary comma per row: a bare @(@(..),@(..)) flattens in
# PowerShell and the rows stop being rows.
$TEMP_STOPS = @()
$TEMP_STOPS += , @(10,   31,  63, 122)   # deep blue, frozen
$TEMP_STOPS += , @(40,   61, 111, 181)   # blue, stone cold
$TEMP_STOPS += , @(60,   55, 214, 122)   # green, in its window
$TEMP_STOPS += , @(85,  232, 212,  77)   # yellow
$TEMP_STOPS += , @(100, 232, 163,  61)   # orange
$TEMP_STOPS += , @(115, 229,  72,  77)   # red
$TEMP_STOPS += , @(140, 138,  14,  18)   # deep red, cooked

# Emits the ramp up to "c holds the colour", so the block fill and the
# label on top are computed from one piece of arithmetic.
function TempRampJs([string]$emptyReturn) {
    $js = 'if(isNaN(v)||v<=0){' + $emptyReturn + '}var s=['
    $js += (($TEMP_STOPS | ForEach-Object { '[' + ($_ -join ',') + ']' }) -join ',')
    # Driven by s.length, so adding or moving a stop needs no edit here.
    $js += '];var L=s.length-1;var c=s[0];if(v>=s[L][0])c=s[L];' +
           'else if(v>s[0][0]){for(var i=0;i<L;i++){if(v<=s[i+1][0]){' +
           'var t=(v-s[i][0])/(s[i+1][0]-s[i][0]);c=[0,' +
           's[i][1]+(s[i+1][1]-s[i][1])*t,s[i][2]+(s[i+1][2]-s[i][2])*t,' +
           's[i][3]+(s[i+1][3]-s[i][3])*t];break;}}}'
    $js
}

function TempColorJs([string]$tileColor) {
    (TempRampJs ('return "' + $tileColor + '";')) +
    'var o="#FF";for(var k=1;k<4;k++){var n=Math.round(c[k]);' +
    'if(n<0)n=0;if(n>255)n=255;var x=n.toString(16);' +
    'if(x.length<2)x="0"+x;o+=x;}return o'
}

# The ends of the ramp are dark enough to swallow dark text, so the label
# picks its own contrast from the fill it is sitting on rather than being
# a fixed colour that only works across the middle of the range.
function TempTextColorJs([string]$emptyColor) {
    (TempRampJs ('return "' + $emptyColor + '";')) +
    'var lum=(c[1]*299+c[2]*587+c[3]*114)/1000;' +
    ('return lum>140?"#FF101216":"' + $script:WHITE + '"')
}

# Same ramp in PowerShell, for the preview renderer (it draws static
# colors and never evaluates the formula above).
function TempColor([double]$v) {
    if ($v -le 0) { return $script:TILE }
    $st = $script:TEMP_STOPS
    $last = $st.Count - 1
    $c = $st[0]
    if ($v -ge $st[$last][0]) { $c = $st[$last] }
    elseif ($v -gt $st[0][0]) {
        for ($i = 0; $i -lt $last; $i++) {
            if ($v -le $st[$i + 1][0]) {
                $t = ($v - $st[$i][0]) / ($st[$i + 1][0] - $st[$i][0])
                # Every element parenthesised: PowerShell's comma binds
                # tighter than + and *, so a bare expression here becomes
                # (0, x) * $t and dies on op_Multiply.
                $c = @(0,
                    ($st[$i][1] + ($st[$i + 1][1] - $st[$i][1]) * $t),
                    ($st[$i][2] + ($st[$i + 1][2] - $st[$i][2]) * $t),
                    ($st[$i][3] + ($st[$i + 1][3] - $st[$i][3]) * $t))
                break
            }
        }
    }
    '#FF' + ('{0:X2}{1:X2}{2:X2}' -f [int][math]::Round($c[1]), [int][math]::Round($c[2]), [int][math]::Round($c[3]))
}

function TempTextColor([double]$v) {
    $c = [System.Drawing.ColorTranslator]::FromHtml((TempColor $v))
    $lum = ($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000
    if ($lum -gt 140) { '#FF101216' } else { $script:WHITE }
}
$BACKDROP= '#F60D0F13'   # overlay backdrop (near-opaque)

function BindJS([string]$target, [string]$expr) {
    [ordered]@{
        Formula = [ordered]@{ Interpreter = 1; Expression = $expr }
        Mode = 2
        TargetPropertyName = $target
    }
}

function New-Text([string]$name, $x, $y, $w, $h, $size, [string]$text, [string]$color,
                  [int]$halign = 1, [hashtable]$bindings = $null, [string]$weight = 'Normal',
                  [switch]$Fontable) {
    $b = [ordered]@{}
    if ($bindings) { foreach ($k in $bindings.Keys) { $b[$k] = $bindings[$k] } }
    $it = [ordered]@{
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
    # Font is omitted unless the item is meant to carry one. An empty Font
    # is NOT "use the dashboard's default": it is a family nobody has, and
    # putting it on every text item made the entire dash fall back.
    if ($Fontable) { $it.Insert(3, 'Font', '') }
    $it
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
    $items.Add((OnOverlay (New-Card 'kp-entry-bg' 250 48 300 64 6) 'keypad'))
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
function RevStrip([string]$P, [bool]$driveTab = $false) {
    $items = [System.Collections.Generic.List[object]]::new()
    # Narrowing belongs to the Drive tab and nowhere else. There it is the
    # space above the gear between the two box columns, which is a real
    # place; on every other screen the middle of the header row is where
    # the title and the car name live, so there is nothing to narrow to and
    # the strip simply spans the full width. Only the Drive copy carries
    # the bindings at all, so the setting cannot reach the others.
    $cenX = 300; $cenW = 200; $cenY = 16
    $rc = '$prop("' + $P + '.RevCentered")'
    # Whole-pixel pitch on purpose: these numbers land in JS source, and a
    # fractional one would be written with the machine's decimal separator.
    $bg = New-Rect 'rev-bg' 0 0 800 12 $script:REVBG $null 0
    if ($driveTab) {
        $bg.Left = [double]$cenX; $bg.Width = [double]$cenW; $bg.Top = [double]$cenY
        $bg.Bindings['Left']  = BindJS 'Left'  ('return ' + $rc + '?' + $cenX + ':0')
        $bg.Bindings['Width'] = BindJS 'Width' ('return ' + $rc + '?' + $cenW + ':800')
        $bg.Bindings['Top']   = BindJS 'Top'   ('return ' + $rc + '?' + $cenY + ':0')
    }
    # Hidden behind the idle card: the card is the screen when it is up, and
    # a rev strip over it is chrome from a dashboard nobody is looking at.
    $notIdle = '!$prop("' + $P + '.Idle.On")'
    $bg.Bindings['Visible'] = BindJS 'Visible' ('return ' + $notIdle)
    $items.Add($bg)

    # Unlit sockets: a faint 1px outline per LED position, always visible,
    # so the strip is discoverable before the first rev (an all-dark strip
    # read as empty chrome). Lit segments draw over them.
    for ($i = 0; $i -lt 16; $i++) {
        $x = 2 + $i * 50
        $cx2 = 305 + $i * 12
        $sock = New-Rect "rev-sock$i" $x 1 46 10 $script:CLEAR $null 2
        if ($driveTab) {
            $sock.Left = [double]$cx2; $sock.Width = 10.0; $sock.Top = [double]($cenY + 1)
            $sock.Bindings['Left']  = BindJS 'Left'  ('return ' + $rc + '?' + $cx2 + ':' + $x)
            $sock.Bindings['Width'] = BindJS 'Width' ('return ' + $rc + '?10:46')
            $sock.Bindings['Top']   = BindJS 'Top'   ('return ' + $rc + '?' + ($cenY + 1) + ':1')
        }
        $sock.Bindings['Visible'] = BindJS 'Visible' ('return ' + $notIdle)
        $sock.BorderStyle.BorderColor = $script:LINE
        $sock.BorderStyle.BorderTop = 1; $sock.BorderStyle.BorderBottom = 1
        $sock.BorderStyle.BorderLeft = 1; $sock.BorderStyle.BorderRight = 1
        $sock.BorderColor = $script:LINE
        $sock.BorderTop = 1; $sock.BorderBottom = 1; $sock.BorderLeft = 1; $sock.BorderRight = 1
        $items.Add($sock)
    }
    for ($i = 0; $i -lt 16; $i++) {
        $x = 2 + $i * 50
        $cx2 = 305 + $i * 12
        # Two threshold schemes, chosen live by Dash.RevOutsideIn:
        # left-to-right walks 50..96.9 across the strip; outside-in pairs
        # mirror segments (0+15 first, converging on 7+8) over 50..93.75.
        $tLtr = [math]::Round(50 + $i * 3.125, 2)
        $pair = [math]::Min($i, 15 - $i)
        $tOut = [math]::Round(50 + $pair * 6.25, 2)
        $amber = '#FFE8A33D'
        $cLtr = if ($i -lt 8) { $script:GREEN } elseif ($i -lt 12) { $amber } else { $script:RED }
        $cOut = if ($pair -lt 4) { $script:GREEN } elseif ($pair -lt 6) { $amber } else { $script:RED }
        $seg = New-Rect "rev-seg$i" $x 1 46 10 $cLtr @{
            BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.RevOutsideIn")?"' + $cOut + '":"' + $cLtr + '"')
        } 2
        if ($driveTab) {
            $seg.Left = [double]$cx2; $seg.Width = 10.0; $seg.Top = [double]($cenY + 1)
            $seg.Bindings['Left']  = BindJS 'Left'  ('return ' + $rc + '?' + $cx2 + ':' + $x)
            $seg.Bindings['Width'] = BindJS 'Width' ('return ' + $rc + '?10:46')
            $seg.Bindings['Top']   = BindJS 'Top'   ('return ' + $rc + '?' + ($cenY + 1) + ':1')
        }
        # RevFlash: steady true below redline, wheel-synced blink at/above.
        $seg.Bindings['Visible'] = BindJS 'Visible' ('var t=$prop("' + $P + '.RevOutsideIn")?' + $tOut + ':' + $tLtr + ';return ' + $notIdle + ' && (1*$prop("' + $P + '.RpmPct"))>=t && $prop("' + $P + '.RevFlash")')
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

# ---------------------------------------------------------------------
# Bundled images. SimHub keeps a dashboard's images in a sibling ZIP named
# <dash>.djson.ressources, one entry per image called <Name><Extension>,
# and describes each in the djson's Images array. Verified against a
# shipped dashboard: the MD5 is of the raw file bytes and Length is their
# uncompressed count, both reproduced exactly before anything was built on
# this. Generating them here rather than checking in binaries keeps the
# shapes editable, which is the same reason the djson itself is generated.
# ---------------------------------------------------------------------
$script:DASH_IMAGES = [System.Collections.Generic.List[object]]::new()

function Add-DashImage([string]$name, [byte[]]$png, [int]$w, [int]$h) {
    $md5 = [System.Security.Cryptography.MD5]::Create().ComputeHash($png)
    $hex = ($md5 | ForEach-Object { $_.ToString('x2') }) -join ''
    $script:DASH_IMAGES.Add([ordered]@{
        Bytes = $png
        Meta  = [ordered]@{
            Name = $name; Extension = '.png'
            Modified = $false; Optimized = $false
            Width = $w; Height = $h
            Length = $png.Length; MD5 = $hex
        }
    })
}

# A 90 degree wedge from the CENTRE of a square canvas, pointing in one of
# the four directions. Square and centred on purpose: an item centred on
# the radar covers it exactly, so no rotation is needed at all and the four
# orientations are four images. Rotation would be one image, but whether
# the viewer re-renders a BOUND rotation is unproven, and four tiny PNGs
# cost nothing next to finding that out the hard way.
function New-WedgePng([string]$hex, [int]$size, [double]$dirDeg) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $col = [System.Drawing.ColorTranslator]::FromHtml($hex)
    # GDI angles run clockwise from 3 o'clock; ours run clockwise from 12,
    # hence the -90. Half a quadrant either side of the centre line.
    $start = $dirDeg - 90 - 45
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPie(0, 0, $size, $size, $start, 90)
    $br = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $br.CenterPoint = (New-Object System.Drawing.PointF(($size / 2.0), ($size / 2.0)))
    # Strongest at the middle, fading to the rim: a warning that grows
    # toward you rather than a flat slab of colour.
    $br.CenterColor = [System.Drawing.Color]::FromArgb(150, $col.R, $col.G, $col.B)
    $br.SurroundColors = @([System.Drawing.Color]::FromArgb(18, $col.R, $col.G, $col.B))
    $g.FillPath($br, $path)
    $br.Dispose(); $path.Dispose(); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $ms.ToArray()
}

# GDI is needed here, not just by the preview at the end: the wedge
# images are drawn at generation time and bundled into the dashboard.
Add-Type -AssemblyName System.Drawing

# Proximity wedges: four directions, two warning colours. Front, right,
# rear and left, each centred on its axis so a car dead ahead lights the
# front rather than half of two corners.
$WEDGE_PX = 256
foreach ($wd in @(@('f', 0), @('r', 90), @('b', 180), @('l', 270))) {
    Add-DashImage ('tf4all-wedge-y-' + $wd[0]) (New-WedgePng '#FFE8C547' $WEDGE_PX $wd[1]) $WEDGE_PX $WEDGE_PX
    Add-DashImage ('tf4all-wedge-r-' + $wd[0]) (New-WedgePng '#FFE5484D' $WEDGE_PX $wd[1]) $WEDGE_PX $WEDGE_PX
}

# A soft radial glow: one GradientItem carrying a WPF RadialGradientBrush,
# opaque at the centre and fading to fully transparent at the rim. Stacked
# translucent discs approximate this, but each disc has an edge and the
# steps show; a real gradient has none. The brush is serialised the way
# SimHub stores it, an XML brush expressed as JSON attributes.
function New-Glow([string]$name, $cx, $cy, $r, [string]$rgb, [int]$centerAlpha) {
    $a = ('{0:X2}' -f $centerAlpha)
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.GradientItem, SimHub.Plugins'
        Color = [ordered]@{
            RadialGradientBrush = [ordered]@{
                '@Center'       = '0.5,0.5'
                '@GradientOrigin' = '0.5,0.5'
                '@RadiusX'      = '0.5'
                '@RadiusY'      = '0.5'
                '@MappingMode'  = 'RelativeToBoundingBox'
                '@SpreadMethod' = 'Pad'
                '@Opacity'      = '1'
                '@xmlns'        = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                'RadialGradientBrush.GradientStops' = [ordered]@{
                    GradientStop = @(
                        [ordered]@{ '@Color' = "#$a$rgb"; '@Offset' = '0' },
                        [ordered]@{ '@Color' = "#00$rgb"; '@Offset' = '1' }
                    )
                }
            }
        }
        Rotation = 0.0; UseRotation = $false; CanResize = $true
        BackgroundColor = $script:CLEAR
        BlurRadius = 0.0; EnableBlur = $false
        BorderStyle = [ordered]@{
            AllBorders = 0; AllCornerRadius = 0
            BorderColor = $script:CLEAR
            BorderTop = 0; BorderBottom = 0; BorderLeft = 0; BorderRight = 0
            CornerRadius = '0,0,0,0'
            RadiusTopLeft = 0; RadiusTopRight = 0
            RadiusBottomLeft = 0; RadiusBottomRight = 0
            Bindings = [ordered]@{}
        }
        Height = [double]($r * 2); Left = [double]($cx - $r)
        Opacity = 100.0; Top = [double]($cy - $r)
        Visible = $true; Width = [double]($r * 2)
        BorderBottom = 0; BorderColor = $script:CLEAR
        BorderLeft = 0; BorderRight = 0; BorderTop = 0
        IsFreezed = $false; RenderingSkip = 0
        Name = $name
        Bindings = [ordered]@{}
    }
}

# A true ellipse. A RectangleItem can only be a circle when its corner
# radius is exactly half its size, so animating the size leaves the radius
# behind and the shape squares off. An EllipseItem takes width and height
# independently, which is what lets a contour breathe out of round.
function New-Ellipse([string]$name, $x, $y, $w, $h, [string]$stroke, [double]$thick = 1) {
    [ordered]@{
        '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.EllipseItem, SimHub.Plugins'
        FillColor = $script:CLEAR
        EllipseColor = $stroke
        EllipseThickness = [double]$thick
        EllipseBackgroundImage = 'None'
        BackgroundColor = $script:CLEAR
        Height = [double]$h; Left = [double]$x; Opacity = 100.0; Top = [double]$y
        Visible = $true; Width = [double]$w
        Rotation = 0.0; RenderingSkip = 0; IsFreezed = $false
        Name = $name; Bindings = [ordered]@{}
    }
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

# Driver input, OUR frame first and SimHub second. Ours carries whatever
# source is live, which for Forza is the game's own UDP at frame rate and
# needs no "Also forward to SimHub", and for everything else is the same
# data SimHub has anyway. Ours is 0..1, SimHub's is 0..100. Clutch and
# handbrake report -1 when the source has no such channel, which fails the
# >0 test and hands over to SimHub exactly like a missing value.
function PedalJs([string]$P, [string]$ours, [string]$sim) {
    'var v=100*(1*$prop("' + $P + '.' + $ours + '"));' +
    'if(!(v>0))v=1*$prop("' + $script:SIM + $sim + '");' +
    'if(isNaN(v))v=0;if(v>100)v=100;if(v<0)v=0;'
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
    # The notice claims something about the GAME, so it waits until there is
    # a car on a track to claim it. Paused or between sessions the value is
    # missing for a reason that has nothing to do with what the title
    # reports, and saying otherwise was simply wrong.
    function NoDataVis([string]$k, [string]$dataJs) {
        'return ' + $script:sel + '=="' + $k + '"' + $script:rowCond +
        ' && $prop("' + $script:PN + '.SessionLive") && !(' + $dataJs + ')'
    }
    $script:sel = $sel; $script:rowCond = $rowCond; $script:PN = $P
    $script:xN = $x; $script:yN = $y; $script:wN = $w

    $panel = New-Card "d$slot-panel" $x $y $w $h 10
    $panel.Bindings['Visible'] = BindJS 'Visible' ('return ' + $sel + '!="None"' + $rowCond)
    $items.Add($panel)

    # One tap zone per BOX, on the title itself. The title moved onto the
    # card's top border, so a zone still sitting inside the card is a zone
    # under the thing it belongs to. Fixed width and centred rather than
    # measured, because the button is built before the title exists and its
    # width depends on the title's length; 220 covers every label we use
    # and still leaves a gap to the next card.
    $hbW = [math]::Min(220, $w - 40)
    $headBtn = New-Button "d$slot-head" ($x + $w / 2 - $hbW / 2) ($y - 12 + $script:HEAD_DROP) $hbW 26 ("DashDriveBoxOpen$slot")
    $headBtn.Bindings['Visible'] = BindJS 'Visible' ('return 1' + $rowCond)
    $items.Add($headBtn)
    # An empty box would otherwise be an invisible panel with no way back.
    $t = New-Text "d$slot-empty" $ix ($iy + 40) $iw 24 14 'TAP TO CHOOSE' $script:GRAY 1
    $t.Bindings['Visible'] = BindJS 'Visible' ('return ' + $sel + '=="None"' + $rowCond)
    $items.Add($t)

    # Adds a section header that shows whenever the box holds this key,
    # data or not, so a "no data" box still says what it is.
    # The header doubles as the control for what the box shows. A caret
    # says so without a second widget, and the tap zone is the title's own
    # half of the row so it cannot swallow a badge or a value on the right.
    function AddHead([string]$id, [string]$title, [string]$k) {
        # Centred on the card's own top border, with a slice of background
        # painted over the line behind it: the border appears to break for
        # the title rather than the title floating inside a frame.
        $lbl = $title + '  ' + [char]0x25BE
        $wpx = 9 * $lbl.Length + 18
        $cx = $script:xN + $script:wN / 2 - $wpx / 2
        $gap = ThemePaint (New-Rect "d$script:slotN-$id-gap" $cx ($script:yN - 2) $wpx 5 $script:BG $null 0) 'Bg'
        $gap.Bindings['Visible'] = BindJS 'Visible' (KeyVis $k $null)
        $script:headGap = $gap
        $t = New-Text "d$script:slotN-$id-h" $cx ($script:yN - 9 + $script:HEAD_DROP) $wpx 18 12 $lbl $script:MUTED 1
        # The titles are the most repeated text on the dashboard, so they do
        # more than anything else to make one theme look unlike another.
        $t.Bindings['TextColor'] = ThemeBind 'TextColor' 'Muted'
        $t.Bindings['Visible'] = BindJS 'Visible' (KeyVis $k $null)
        $t
    }

    # The gap belongs with the title, but AddHead can only return one item,
    # so callers take it from here right after.
    function AddHeadGap {
        $g = $script:headGap
        $script:headGap = $null
        $g
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
    # Having a BEST lap means a lap has been COMPLETED, which is not the
    # same as the game reporting lap times: before the first flying lap this
    # claimed Assetto Corsa does not report them. A running current lap is
    # the honest test, and it is there from the moment you leave the pits.
    $dLap   = '((1*$prop("' + $P + '.Forza.BestLap"))>0)' +
              '||((1*$prop("' + $P + '.Forza.CurLap"))>0)' +
              '||((""+$prop("' + $SIM + 'CurrentLapTime")||"")!="")' +
              '||((""+$prop("' + $SIM + 'BestLapTime")||"")!=""&&(""+$prop("' + $SIM + 'BestLapTime")).indexOf("00:00:00")!=0)'
    $dTemp  = '((1*$prop("' + $P + '.Forza.TempFL"))>0)||((1*$prop("' + $SIM + 'TyreTemperatureFrontLeft"))>0)'
    $dWear  = '($prop("' + $P + '.Forza.HasWear")&&(1*$prop("' + $P + '.Forza.WearFL"))>0)||((1*$prop("' + $SIM + 'TyreWearFrontLeft"))>0)'
    $dFuel  = '((1*$prop("' + $P + '.Forza.FuelPct"))>0)||((1*$prop("' + $SIM + 'MaxFuel"))>0)'
    $dDelta = '(""+$prop("' + $TRK + 'EstimatedLapTime")||"")!=""'
    $dOpp   = '(1*$prop("' + $SIM + 'OpponentsCount"))>1'
    $dG     = '!isNaN(1*$prop("' + $P + '.Drive.GLat"))'
    # Telemetry FFB gives the better grip number, but it is no longer the
    # only one: with it off the box runs on measured accelerations, so it
    # needs whatever the g circle needs.
    # SimHub's CarDamage1-5. Forza's packet carries no damage at all, so
    # this is one of the few boxes with no telemetry of our own behind it.
    # A panel reporting a number at all counts as reported, zero included,
    # because a pristine car is a real reading.
    $dDmg   = '(""+$prop("' + $SIM + 'CarDamage1")||"")!=""'
    $dFric  = '($prop("' + $P + '.ModeB.On"))||(' + $dG + ')'

    # ---------------- CAR FACTS (ours, always available) -------------
    # Tappable exactly like the Car facts tab: the engine row opens the
    # layout picker, the redline row opens the keypad. Both overlays
    # live on this screen too, so the flow never leaves the Drive tab.
    $vis = KeyVis 'CarFacts' $null
    $hd = AddHead 'cf' 'CAR FACTS' 'CarFacts'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $t = New-Text "d$slot-cf-car" $ix ($iy + 20) $iw 26 18 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('return ""+($prop("' + $P + '.CarName")||"No car")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # Each fact sits on its own lightened sub-panel with a caption, the
    # value, and its action tile on the right, which is the Car facts
    # tab's layout at this size rather than a list of label/value rows.
    $cfP1 = ThemePaint (New-Rect "d$slot-cf-p1" $ix ($iy + 48) $iw 52 $script:SUBPANEL $null 5) 'Sub'
    $cfP1.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($cfP1)
    $cfP2 = ThemePaint (New-Rect "d$slot-cf-p2" $ix ($iy + 106) $iw 52 $script:SUBPANEL $null 5) 'Sub'
    $cfP2.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($cfP2)
    # Engine: caption, value, CHANGE tile on the right, exactly as the tab
    # arranges it.
    $chW = 62
    $t = New-Text "d$slot-cf-engl" ($ix + 10) ($iy + 52) ($iw - 20) 16 10 'ENGINE LAYOUT' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $t = New-Text "d$slot-cf-eng-v" ($ix + 10) ($iy + 68) ($iw - 20 - $chW - 8) 26 17 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('return ""+($prop("' + $P + '.EngineLayout")||"Auto")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $r = New-Btn "d$slot-cf-eng-ch-bg" ($ix + $iw - $chW - 8) ($iy + 58) $chW 34 4
    $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
    $t = New-Text "d$slot-cf-eng-ch-t" ($ix + $iw - $chW - 8) ($iy + 58) $chW 34 12 'CHANGE' $script:WHITE 1
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-cf-eng-tap" ($ix + $iw - $chW - 8) ($iy + 58) $chW 34 'DashEngineLayoutOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    # Redline: same shape, with the tab's minus / plus 50 in place of the
    # CHANGE tile and the value itself opening the keypad.
    $stepW = 30
    $t = New-Text "d$slot-cf-redl" ($ix + 10) ($iy + 110) ($iw - 20) 16 10 'REDLINE  (TAP TO TYPE)' $script:MUTED 0
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $rdValW = $iw - 20 - ($stepW * 2 + 6) - 12
    $t = New-Text "d$slot-cf-red-v" ($ix + 10) ($iy + 126) $rdValW 26 17 '' $script:WHITE 0 @{
        Text = BindJS 'Text' ('var r=1*$prop("' + $P + '.Redline");return r>0?(r+" rpm"):"--"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $b = New-Button "d$slot-cf-red-tap" ($ix + 10) ($iy + 126) $rdValW 26 'DashRedlineOpen'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    foreach ($st in @(@('dn', 'DashRedlineDown', '-', 0), @('up', 'DashRedlineUp', '+', ($stepW + 6)))) {
        $sx = $ix + $iw - 8 - ($stepW * 2 + 6) + $st[3]
        $r = New-Btn "d$slot-cf-r$($st[0])-bg" $sx ($iy + 116) $stepW 34 4
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $tt = New-Text "d$slot-cf-r$($st[0])-t" $sx ($iy + 116) $stepW 34 20 $st[2] $script:WHITE 1 $null 'Bold'
        $tt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($tt)
        $bb = New-Button "d$slot-cf-r$($st[0])" $sx ($iy + 116) $stepW 34 $st[1]
        $bb.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($bb)
    }
    # The tab's provenance line: where the redline came from and the
    # observed ceiling, so a wrong buzz point is diagnosable here too.
    $t = New-Text "d$slot-cf-info" $ix ($iy + 164) $iw 20 11 '' $script:GRAY 0 @{
        Text = BindJS 'Text' ('var m=1*$prop("' + $P + '.MaxRpm");var s=""+($prop("' + $P + '.RedlineSource")||"");' +
                              'var t=m>0?("MAX "+m):"";if(s!=""&&s!="none")t+=(t!=""?"   ":"")+s.toUpperCase();return t')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)

    # ---------------- GAINS (ours) -----------------------------------
    # Shaped like the Home tab rather than a list of rows: a caption, the
    # value large and centred with a tap zone hugging the digits, and a
    # wide minus / plus pair beneath. Both gains get that treatment, and
    # the two toggles are full-width tiles that colour with their state,
    # exactly as the tab's PLUGIN and AUDIO HAPTICS tiles do.
    $vis = KeyVis 'Home' $null
    $hd = AddHead 'hm' 'GAINS' 'Home'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $half = ($iw - 8) / 2
    # Wheel state on the header row, as on the tab. It is the one thing
    # here that is not a gain, and the reason to glance at this box when
    # the wheel goes quiet.
    $t = New-Text "d$slot-hm-wheel" $ix ($iy + 1) $iw 18 12 '' $script:GREEN 2 @{
        Text      = BindJS 'Text'      ('return $prop("' + $P + '.WheelOk")?"WHEEL OK":"WHEEL OFFLINE"')
        TextColor = BindJS 'TextColor' ('return $prop("' + $P + '.WheelOk")?"' + $script:GREEN + '":"' + $script:RED + '"')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # Both gains get the tab's treatment: caption, big centred value with
    # the tap zone hugging the digits, wide minus / plus underneath.
    # Dropping the on/off tiles freed the room for the second one.
    # Parenthesised on purpose: PowerShell's comma binds tighter than +,
    # so a bare concatenation here swallows the element after it.
    $gainRows = @(
        @('mg', 'MASTER GAIN', ($P + '.MasterGain'), 'DashMasterGainOpen', 'DashMasterGainDown', 'DashMasterGainUp', ''),
        @('ag', 'AUDIO GAIN',  ($P + '.AudioGain'),  'DashAudioGainOpen',  'DashAudioGainDown',  'DashAudioGainUp',  ($P + '.Fx.Audio.On'))
    )
    # One lightened sub-panel per gain, matching car facts and presets:
    # caption, then the value flanked by its steppers so the controls sit
    # with the number they change.
    $gy = $iy + 24
    $gStep = 44
    foreach ($gRow in $gainRows) {
        $gid = $gRow[0]; $glabel = $gRow[1]; $gprop = $gRow[2]
        $gopen = $gRow[3]; $gdn = $gRow[4]; $gup = $gRow[5]; $gonProp = $gRow[6]
        # 74 tall rather than 66: the extra 8 goes under the caption row so
        # the ON/OFF pill has a gap beneath it instead of resting on the
        # steppers, and the panel still clears the bottom of the smallest box.
        $pnl = ThemePaint (New-Rect "d$slot-hm-$gid-p" $ix $gy $iw 74 $script:SUBPANEL $null 5) 'Sub'
        $pnl.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($pnl)
        $t = New-Text "d$slot-hm-$gid-l" ($ix + 10) ($gy + 6) ($iw - 20) 14 10 $glabel $script:MUTED 0
        $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
        # Audio needs an explicit off: the steppers switch capture ON when
        # it is off (reaching for the gain means you want to hear it), so
        # without this there is no way back.
        if ($gonProp -ne '') {
            $pillW = 42
            $px = $ix + $iw - 10 - $pillW
            $r = New-Rect "d$slot-hm-$gid-pill-bg" $px ($gy + 4) $pillW 18 $script:BTN @{
                BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $gonProp + '")?"' + $script:TILEON + '":"' + $script:PANEL + '"')
            } 4
            $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
            $t = New-Text "d$slot-hm-$gid-pill-t" $px ($gy + 4) $pillW 18 10 '' $script:WHITE 1 @{
                Text      = BindJS 'Text'      ('return $prop("' + $gonProp + '")?"ON":"OFF"')
                TextColor = BindJS 'TextColor' ('return $prop("' + $gonProp + '")?"' + $script:WHITE + '":"' + $script:MUTED + '"')
            } 'Bold'
            $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
            $b = New-Button "d$slot-hm-$gid-pill" $px ($gy + 4) $pillW 18 'DashFxAudioToggle'
            $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
        }
        # An audio gain the capture is not using reads as off, not as a
        # number that is doing nothing.
        $valJs = if ($gonProp -ne '') {
            'return $prop("' + $gonProp + '")?(1*$prop("' + $gprop + '")).toFixed(2):"off"'
        } else {
            'return (1*$prop("' + $gprop + '")).toFixed(2)'
        }
        $vX = $ix + 10 + $gStep + 6
        $vW = $iw - 20 - ($gStep + 6) * 2
        $t = New-Text "d$slot-hm-$gid" $vX ($gy + 30) $vW 36 28 '' $script:WHITE 1 @{
            Text = BindJS 'Text' $valJs
        } 'Bold'
        $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
        # Tap the number to type it, as on the tab.
        $b = New-Button "d$slot-hm-$gid-tap" $vX ($gy + 30) $vW 36 $gopen
        $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
        foreach ($st in @(@(($gid + 'dn'), $gdn, '-', ($ix + 10)), @(($gid + 'up'), $gup, '+', ($ix + $iw - 10 - $gStep)))) {
            $sx = $st[3]
            $r = New-Btn "d$slot-hm-$($st[0])-bg" $sx ($gy + 30) $gStep 36 4
            $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
            $tt = New-Text "d$slot-hm-$($st[0])-t" $sx ($gy + 30) $gStep 36 24 $st[2] $script:WHITE 1 $null 'Bold'
            $tt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($tt)
            $bb = New-Button "d$slot-hm-$($st[0])" $sx ($gy + 30) $gStep 36 $st[1]
            $bb.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($bb)
        }
        $gy += 82
    }

    # ---------------- PRESETS (ours) ---------------------------------
    # Same shape as the Presets tab: a lightened panel per scope, with its
    # caption, the bound preset, and a CHANGE tile that opens the picker.
    $vis = KeyVis 'Presets' $null
    $hd = AddHead 'pr' 'PRESETS' 'Presets'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $prRows = @(
        @('g', 'GAME PRESET', ($P + '.PresetName'),    'DashPresetOpenGame', '(manual tune)', 26),
        @('c', 'CAR PRESET',  ($P + '.CarPresetName'), 'DashPresetOpenCar',  '(none saved)',  92)
    )
    $chW = 62
    foreach ($pr in $prRows) {
        $prid = $pr[0]; $prLabel = $pr[1]; $prProp = $pr[2]
        $prAct = $pr[3]; $prEmpty = $pr[4]; $prY = $iy + $pr[5]
        $r = ThemePaint (New-Rect "d$slot-pr-$prid-p" $ix $prY $iw 58 $script:SUBPANEL $null 5) 'Sub'
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $t = New-Text "d$slot-pr-$prid-l" ($ix + 10) ($prY + 6) ($iw - 20) 16 10 $prLabel $script:MUTED 0
        $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
        $t = New-Text "d$slot-pr-$prid" ($ix + 10) ($prY + 22) ($iw - 20 - $chW - 8) 30 16 '' $script:WHITE 0 @{
            Text = BindJS 'Text' ('var p=""+($prop("' + $prProp + '")||"");return p!=""?p:"' + $prEmpty + '"')
        } 'Bold'
        $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
        $r = New-Btn "d$slot-pr-$prid-ch-bg" ($ix + $iw - $chW - 8) ($prY + 12) $chW 34 4
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
        $t = New-Text "d$slot-pr-$prid-ch-t" ($ix + $iw - $chW - 8) ($prY + 12) $chW 34 12 'CHANGE' $script:WHITE 1
        $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
        $b = New-Button "d$slot-pr-$prid-tap" ($ix + $iw - $chW - 8) ($prY + 12) $chW 34 $prAct
        $b.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($b)
    }

    # ---------------- TYRE TEMPS (visual) ----------------------------
    # Four tyre blocks coloured by temperature, so the box reads at a
    # glance instead of needing four numbers parsed. Bands are broad on
    # purpose: games disagree on what they report (core vs surface, C vs
    # F), so this is a relative cue with the number kept for detail.
    $vis = KeyVis 'TyreTemps' $dTemp
    $bandOrder = @(@('Outer', 'Middle', 'Inner'), @('Inner', 'Middle', 'Outer'))
    # A tyre does not change state at a threshold, so the colour does not
    # step at one either: it is interpolated between four stops, and the
    # drift toward the next colour IS the reading. Stops keep the old
    # breakpoints so a tyre that read amber still does. Games disagree on
    # units and on core vs surface, so this stays a relative cue.
    $tempScale = TempColorJs $script:TILE
    $tempText  = TempTextColorJs $script:MUTED
    # Everything internal is Celsius (the Forza parse converts its
    # Fahrenheit on the way in), so the ramp keeps working in Celsius and
    # only the printed number follows SimHub's unit choice. Both property
    # paths are probed because the value has moved between them; unknown
    # falls through to Celsius, which is the unit the number is already in.
    # TemperatureUnit is the one the stock dashboards read; its values are
    # "Fahrenheit" and "Celcius" (SimHub's spelling), so testing for an F
    # covers both. LocalTemperatureUnit is probed after it as a fallback.
    $tempUnitJs = 'var uu=""+($prop("' + $SIM + 'TemperatureUnit")||"");' +
                  'if(uu=="")uu=""+($prop("' + $SIM + 'LocalTemperatureUnit")||"");' +
                  'var uF=uu.toUpperCase().indexOf("F")>=0;'
    # The unit lives in the header: the blocks are too narrow to carry a
    # degree suffix, and a bare number in the wrong unit is worse than none.
    $ttHead = AddHead 'tt' 'TYRE TEMPS' 'TyreTemps'
    $g = AddHeadGap; if ($g) { $items.Add($g) }
    # Binding the text replaces the static title, caret included, so this
    # has to put it back: without it this is the one box whose header does
    # not look tappable.
    $ttHead.Bindings['Text'] = BindJS 'Text' ($tempUnitJs +
        'return "TYRE TEMPS "+(uF?"°F":"°C")+"  ' + [char]0x25BE + '"')
    $items.Add($ttHead)
    $tyreProps = @('TyreTemperatureFrontLeft', 'TyreTemperatureFrontRight', 'TyreTemperatureRearLeft', 'TyreTemperatureRearRight')
    $wearProps = @('TyreWearFrontLeft', 'TyreWearFrontRight', 'TyreWearRearLeft', 'TyreWearRearRight')
    $tyW = 54; $tyH = [math]::Min(74, ($h - 70) / 2); $gapX = 26
    $cx0 = $ix + ($iw - ($tyW * 2 + $gapX)) / 2
    # The blocks are capped at 74 tall, so in the taller one-row box the
    # grid does not grow; centre it in the space below the header instead
    # of leaving it pinned to the top.
    $cy0 = ($iy + 30 + $y + $h - 12) / 2 - ($tyH + 5)
    $fzTemp = @('TempFL', 'TempFR', 'TempRL', 'TempRR')
    # Games that measure across the tread report it in three bands, and the
    # spread across a tyre is the useful part: an outer edge running away
    # from the middle is the camber/pressure story a single average hides.
    # Forza sends one temperature per tyre and nothing more, so the split
    # appears only where the game actually measures it and the single block
    # stands in otherwise. Bands run outer-to-inner on the left of the car
    # and inner-to-outer on the right, so the inner edges face each other
    # the way they do on the car.
    for ($q = 0; $q -lt 4; $q++) {
        $cx = $cx0 + ($q % 2) * ($tyW + $gapX)
        $cy = $cy0 + [math]::Floor($q / 2) * ($tyH + 10)
        # ours first, SimHub's as the fallback
        # Ours is Celsius (the Forza parse converts on the way in);
        # SimHub's is already in whatever unit the user picked, so it is
        # the one that may need converting. v ends up Celsius either way,
        # which is what the ramp is calibrated against.
        $vJs = 'var v=1*$prop("' + $P + '.Forza.' + $fzTemp[$q] + '");var fs=0;' +
               'if(!(v>0)){v=1*$prop("' + $SIM + $tyreProps[$q] + '");fs=1;}' +
               $tempUnitJs + 'if(fs&&uF)v=(v-32)*5/9;'
        # A real split needs all three bands AND a source that measures
        # them. SimHub fills Middle from a single per-tyre reading, so
        # testing Middle alone handed Forza a split it does not have and
        # left the outer and inner bands grey. Our own Forza temperature
        # being present is proof this is a title with one value per tyre.
        $hasBands = '!((1*$prop("' + $P + '.Forza.' + $fzTemp[$q] + '"))>0)' +
                    '&&(1*$prop("' + $SIM + $tyreProps[$q] + 'Inner"))>0' +
                    '&&(1*$prop("' + $SIM + $tyreProps[$q] + 'Middle"))>0' +
                    '&&(1*$prop("' + $SIM + $tyreProps[$q] + 'Outer"))>0'
        # cold -> blue, working -> green, hot -> amber, overheating -> red
        $colJs = $vJs + $tempScale
        $r = New-Rect "d$slot-tt$q" $cx $cy $tyW $tyH $script:TILE @{
            BackgroundColor = BindJS 'BackgroundColor' $colJs
        } 6
        $r.Bindings['Visible'] = BindJS 'Visible' ($vis -replace '^return ', ('return !(' + $hasBands + ') && '))
        $items.Add($r)
        $bandW = ($tyW - 4) / 3
        for ($bi = 0; $bi -lt 3; $bi++) {
            $band = $bandOrder[$q % 2][$bi]
            # Bands are SimHub's alone, so always its unit.
            $bJs = 'var v=1*$prop("' + $SIM + $tyreProps[$q] + $band + '");' +
                   $tempUnitJs + 'if(uF)v=(v-32)*5/9;'
            $b = New-Rect "d$slot-tt$q-$($band.ToLower())" ($cx + $bi * ($bandW + 2)) $cy $bandW $tyH $script:TILE @{
                BackgroundColor = BindJS 'BackgroundColor' ($bJs + $tempScale)
            } 3
            $b.Bindings['Visible'] = BindJS 'Visible' ($vis -replace '^return ', ('return (' + $hasBands + ') && '))
            $items.Add($b)
        }
        $tv = New-Text "d$slot-tt$q-v" $cx ($cy + $tyH / 2 - 15) $tyW 30 17 '' '#FF101216' 1 @{
            Text      = BindJS 'Text'      ($vJs +
                'return isNaN(v)||v<=0?"--":Math.round(uF?v*9/5+32:v)')
            TextColor = BindJS 'TextColor' ($vJs + $tempText)
        } 'Bold'
        $tv.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($tv)
    }
    $items.Add((AddNote 'tt' 'This game does not report tyre temperatures.' 'TyreTemps' $dTemp))

    # ---------------- TYRE WEAR (visual) -----------------------------
    # Same blocks; here the colour is how much tread is left.
    $vis = KeyVis 'TyreWear' $dWear
    $hd = AddHead 'tw' 'TYRE WEAR' 'TyreWear'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
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
    $hd = AddHead 'fu' 'FUEL' 'Fuel'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    # Forza reports a tank fraction rather than litres, so the big number
    # is a percentage there and a level everywhere else.
    # Tank fraction, ours first. Drives both the readout and the bar, so
    # they can never disagree about how much is left.
    $fuPct = 'var p=1*$prop("' + $P + '.Forza.FuelPct");' +
             'if(!(p>0))p=1*$prop("' + $SIM + 'FuelPercent");' +
             'if(isNaN(p))p=-1;if(p>100)p=100;'
    $t = New-Text "d$slot-fu-lvl" $ix ($iy + 20) $iw 52 42 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ($fuPct + 'return p<0?"--":Math.round(p)+"%"')
        TextColor = BindJS 'TextColor' ($fuPct +
            'return p<0?"' + $script:MUTED + '":(p<10?"' + $script:RED +
            '":(p<25?"#FFE8A33D":"' + $script:WHITE + '"))')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # A bar reads at a glance where a number has to be parsed, and it is the
    # cheapest way to fill a box that was two thirds empty.
    $fuBarY = $iy + 80
    $r = ThemePaint (New-Rect "d$slot-fu-bar-bg" $ix $fuBarY $iw 18 $script:SUBPANEL $null 5) 'Sub'
    $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
    $r = New-Rect "d$slot-fu-bar" $ix $fuBarY 2 18 $script:GREEN @{
        BackgroundColor = BindJS 'BackgroundColor' ($fuPct +
            'return p<10?"' + $script:RED + '":(p<25?"#FFE8A33D":"' + $script:GREEN + '")')
    } 5
    $r.Bindings['Width'] = BindJS 'Width' ($fuPct + 'if(p<0)p=0;return Math.max(2,' + $iw + '*p/100)')
    $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
    BoxLine "d$slot-fu-laps" $ix ($iy + 110) $iw 'Laps left' ('var v=1*$prop("DataCorePlugin.Computed.Fuel_RemainingLaps");return isNaN(v)||v<=0?"--":v.toFixed(1)') $vis 17 | ForEach-Object { $items.Add($_) }
    # Litres where the game reports them; Forza only ever gives a fraction.
    BoxLine "d$slot-fu-lit" $ix ($iy + 142) $iw 'In tank' ('var v=1*$prop("' + $SIM + 'Fuel");return isNaN(v)||v<=0?"--":v.toFixed(1)+" L"') $vis 17 | ForEach-Object { $items.Add($_) }
    $items.Add((AddNote 'fu' 'This game does not report fuel.' 'Fuel' $dFuel))

    # ---------------- LAP DELTA --------------------------------------
    # One box for the whole lap picture. A separate Lap times box repeated
    # two of these rows and only the delta ever needed a headline, so the
    # running lap joins it here: that number was the reason to keep both.
    $vis = KeyVis 'Delta' $dDelta
    $hd = AddHead 'dl' 'LAP DELTA' 'Delta'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $t = New-Text "d$slot-dl-v" $ix ($iy + 22) $iw 44 32 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('var v=1*$prop("' + $TRK + 'SessionBestLastLapDelta");return isNaN(v)?"--":(v>0?"+":"")+v.toFixed(2)')
        TextColor = BindJS 'TextColor' ('var v=1*$prop("' + $TRK + 'SessionBestLastLapDelta");return isNaN(v)?"' + $script:MUTED + '":(v>0?"' + $script:RED + '":"' + $script:GREEN + '")')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    # Four rows at 28 rather than three at 32: the extra one has to fit the
    # short box as well as the tall one.
    $dlRows = @(
        @('cur',  'Current',   (FmtLapDualJs ($P + '.Forza.CurLap')  ($SIM + 'CurrentLapTime'))),
        @('est',  'Estimated', (FmtLapJs ($TRK + 'EstimatedLapTime'))),
        @('last', 'Last',      (FmtLapDualJs ($P + '.Forza.LastLap') ($SIM + 'LastLapTime'))),
        @('best', 'Best',      (FmtLapDualJs ($P + '.Forza.BestLap') ($SIM + 'BestLapTime')))
    )
    for ($d = 0; $d -lt $dlRows.Count; $d++) {
        BoxLine "d$slot-dl-$($dlRows[$d][0])" $ix ($iy + 70 + $d * 28) $iw $dlRows[$d][1] $dlRows[$d][2] $vis 17 |
            ForEach-Object { $items.Add($_) }
    }
    $items.Add((AddNote 'dl' 'This game does not report lap deltas.' 'Delta' $dDelta))

    # ---------------- G CIRCLE (game accelerations) ------------------
    # Classic g-g diagram: the dot is where the car's acceleration
    # points, the rings are 0.75 g and 1.5 g. Reads the same
    # accelerations the crash duck uses, so it works on every telemetry
    # source we support rather than only games with raw g properties.
    $vis = KeyVis 'GCircle' $dG
    $hd = AddHead 'gc' 'G CIRCLE' 'GCircle'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $gr  = [math]::Min(($iw - 24) / 2, ($h - 66) / 2)
    $gcx = $ix + $iw / 2
    # Centre in the area below the header rather than hanging off the top
    # of it. In the tall one-row box the radius is capped by WIDTH, so the
    # circle cannot grow into the extra height and would otherwise sit
    # well above centre. The dots position off these constants, so this
    # has to be right here: a post-hoc shift would move the rings and
    # leave the bound dots behind.
    $gcy = ($iy + 30 + $y + $h - 12) / 2
    $items.Add((New-Ring "d$slot-gc-r1" $gcx $gcy $gr $script:LINE 1 $vis))
    $items.Add((New-Ring "d$slot-gc-r2" $gcx $gcy ($gr / 2) $script:SUBPANEL 1 $vis))
    # The dot shows the force you FEEL, not the car's acceleration vector:
    # squeeze the throttle and it sinks toward you, brake and it rises,
    # turn right and it swings left, which is the way a g meter on a dash
    # reads. Both signs are therefore inverted from the raw accelerations.
    $gLatJs = 'var g=1*$prop("' + $P + '.Drive.GLat");if(isNaN(g))g=0;if(g>1.5)g=1.5;if(g<-1.5)g=-1.5;'
    $gLonJs = 'var g=1*$prop("' + $P + '.Drive.GLong");if(isNaN(g))g=0;if(g>1.5)g=1.5;if(g<-1.5)g=-1.5;'
    $dot = New-Rect "d$slot-gc-dot" ($gcx - 7) ($gcy - 7) 14 14 $script:GREEN $null 7
    $dot.Bindings['Left'] = BindJS 'Left' ($gLatJs + 'return ' + ($gcx - 7) + '-g*' + ($gr / 1.5))
    $dot.Bindings['Top']  = BindJS 'Top'  ($gLonJs + 'return ' + ($gcy - 7) + '+g*' + ($gr / 1.5))
    $dot.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($dot)
    # Total g, one number. Split signed axes were tried and read worse:
    # the dot already says which way the load is going, so the number's
    # job is how MUCH, and two of them competed with it.
    $t = New-Text "d$slot-gc-v" $ix ($iy + 22) $iw 20 13 '' $script:MUTED 2 @{
        Text = BindJS 'Text' ('var a=1*$prop("' + $P + '.Drive.GLat");var b=1*$prop("' + $P + '.Drive.GLong");if(isNaN(a)||isNaN(b))return "";return Math.sqrt(a*a+b*b).toFixed(2)+" g"')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $items.Add((AddNote 'gc' 'This game does not report accelerations.' 'GCircle' $dG))

    # ---------------- FRICTION CIRCLE (ours) -------------------------
    # Not the g diagram: this is how much of the tyre's GRIP is in use.
    # The ring IS the limit, so a dot touching it means there is nothing
    # left. With Telemetry FFB running the number comes from its model,
    # which knows what the tyre is doing and not merely what the car ended
    # up doing. Without it, the measured load against the hardest this car
    # has taken, which every game reporting accelerations can feed.
    $vis = KeyVis 'Friction' $dFric
    $hd = AddHead 'fc' 'FRICTION CIRCLE' 'Friction'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $items.Add((New-Ring "d$slot-fc-lim" $gcx $gcy $gr $script:GRAY 2 $vis))
    $items.Add((New-Ring "d$slot-fc-in" $gcx $gcy ($gr * 0.75) $script:SUBPANEL 1 $vis))
    $uJs = 'var u=1*$prop("' + $P + '.Drive.Util");if(isNaN(u))u=0;if(u>1.3)u=1.3;'
    $dirJs = 'var a=1*$prop("' + $P + '.Drive.GLat");var b=1*$prop("' + $P + '.Drive.GLong");' +
             'var m=Math.sqrt(a*a+b*b);if(isNaN(m)||m<0.05){a=0;b=0;m=1;}'
    $fdot = New-Rect "d$slot-fc-dot" ($gcx - 8) ($gcy - 8) 16 16 $script:GREEN @{
        BackgroundColor = BindJS 'BackgroundColor' ($uJs + 'return u<0.75?"' + $script:GREEN + '":(u<1.0?"#FFE8A33D":"' + $script:RED + '")')
    } 8
    # Same felt-force convention as the g circle above, so the two boxes
    # never disagree about which way the load is pointing.
    $fdot.Bindings['Left'] = BindJS 'Left' ($uJs + $dirJs + 'return ' + ($gcx - 8) + '-(a/m)*u*' + $gr)
    $fdot.Bindings['Top']  = BindJS 'Top'  ($uJs + $dirJs + 'return ' + ($gcy - 8) + '+(b/m)*u*' + $gr)
    $fdot.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($fdot)
    # Counts DOWN: what the tyre has left, not what it is spending. "0%
    # grip" while cruising reads as no grip at all, which is backwards.
    # The word "left" carries the direction, because the number now falls
    # while the dot travels outward and the two would otherwise look like
    # they disagree.
    $t = New-Text "d$slot-fc-v" $ix ($iy + 22) $iw 20 13 '' $script:MUTED 2 @{
        Text = BindJS 'Text' ($uJs + 'var g=Math.round((1-u)*100);' +
            'if(g<0)g=0;if(g>100)g=100;return g+"% grip left"')
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $items.Add((AddNote 'fc' 'This game does not report accelerations.' 'Friction' $dFric))

    # ---------------- RELATIVE ---------------------------------------
    # Two cars ahead and two behind, from the tracker plugin rather than
    # the obsolete leaderboard item. Three columns: position, who it is,
    # and their last lap. A relative without names is a list of strangers,
    # and the name is the part you actually recognise mid stint.
    #
    # Rows fill the card rather than stopping short of it, and alternate
    # light and dark so the eye can follow one across three columns. Your
    # own row is tinted green instead: on a list where every row looks the
    # same, finding yourself is the one thing you do constantly.
    $vis = KeyVis 'Relative' $dOpp
    $hd = AddHead 'rel' 'RELATIVE' 'Relative'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $relRows = @(
        @('DriverAhead_01', $script:MUTED), @('DriverAhead_00', $script:WHITE),
        @('__ME__', $script:GREEN),
        @('DriverBehind_00', $script:WHITE), @('DriverBehind_01', $script:MUTED)
    )
    # Capped, so the one-row layout does not stretch five rows into bands;
    # whatever is left over becomes padding and the block sits centred.
    $relTop0 = $iy + 26
    $relBot  = $y + $h - 12
    $relRowH = [math]::Min(38, ($relBot - $relTop0) / $relRows.Count)
    $relTop  = ($relTop0 + $relBot) / 2 - ($relRowH * $relRows.Count) / 2
    $relFont = [math]::Max(14, [math]::Min(18, $relRowH - 16))
    for ($r = 0; $r -lt $relRows.Count; $r++) {
        $ry = $relTop + $r * $relRowH
        $src = $relRows[$r][0]; $col = $relRows[$r][1]
        $isMe = $src -eq '__ME__'
        $bandCol = if ($isMe) { '#2637D67A' } elseif ($r % 2 -eq 0) { $script:SUBPANEL } else { $script:CLEAR }
        $band = New-Rect "d$slot-rel$r-bg" $ix $ry $iw ($relRowH - 2) $bandCol $null 4
        $band.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($band)

        # Surnames are what people go by on a timing screen, so a
        # "First Last" is cut to the last word BEFORE any truncation, which
        # keeps the useful half rather than clipping it away.
        $lastName = 'if(n.indexOf(" ")>=0)n=n.substring(n.lastIndexOf(" ")+1);' +
                    'if(n.length>13)n=n.substring(0,12)+"\u2026";'
        if ($isMe) {
            $posJs = 'var p=1*$prop("' + $SIM + 'Position");return isNaN(p)||p<=0?"-":"P"+p'
            $nameJs = 'var n=""+($prop("' + $SIM + 'PlayerName")||"");if(n=="")return "You";' +
                      $lastName + 'return n'
            $valJs = 'var s=""+($prop("' + $SIM + 'LastLapTime")||"");if(s.indexOf(".")>=0)s=s.substring(0,s.indexOf(".")+3);if(s.indexOf("00:")==0)s=s.substring(3);return s==""||s.indexOf("00:00")==0?"--":s'
        } else {
            $posJs = 'var p=1*$prop("' + $TRK + $src + '_Position");return isNaN(p)||p<=0?"-":"P"+p'
            $nameJs = 'var n=""+($prop("' + $TRK + $src + '_Name")||"");if(n=="")return "";' +
                      $lastName + 'return n'
            $valJs = 'var s=""+($prop("' + $TRK + $src + '_LastLapTime")||"");if(s.indexOf(".")>=0)s=s.substring(0,s.indexOf(".")+3);if(s.indexOf("00:")==0)s=s.substring(3);return s==""?"--":s'
        }
        $timeW = 80
        $ty = $ry + ($relRowH - 2 - 22) / 2
        $pt = New-Text "d$slot-rel$r-p" ($ix + 8) $ty 40 22 $relFont '' $col 0 @{ Text = BindJS 'Text' $posJs } 'Bold'
        $pt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($pt)
        $nm = New-Text "d$slot-rel$r-n" ($ix + 50) $ty ($iw - 50 - $timeW - 10) 22 $relFont '' $col 0 @{ Text = BindJS 'Text' $nameJs }
        $nm.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($nm)
        $nt = New-Text "d$slot-rel$r-t" ($ix + $iw - $timeW - 8) $ty $timeW 22 $relFont '' $col 2 @{ Text = BindJS 'Text' $valJs }
        $nt.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($nt)
    }
    $items.Add((AddNote 'rel' 'No other cars in this session.' 'Relative' $dOpp))

    # ---------------- RADAR ------------------------------------------
    # Our own dots rather than SimHub's radar item, for two reasons it
    # cannot do: every opponent gets the same colour there, and its scale
    # is an undocumented multiplier, so a ring could not mean a distance.
    # Here one scale is shared by the dots, the rings and the warning.
    #   rim 40 m, mid ring 20 m, inner ring 8 m
    #   dots white far, yellow past the mid ring, red past the inner
    $vis = KeyVis 'Radar' $dOpp
    $hd = AddHead 'rd' 'RADAR' 'Radar'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $rdSize = [math]::Min($iw, $h - 52)
    $rdCx = $ix + $iw / 2
    $rdCy = ($iy + 30 + $y + $h - 12) / 2
    $rdR  = $rdSize / 2

    # Quadrant wedge, under everything. Full radius whatever the level: an
    # alarm that lights a SMALLER area as the car gets closer reads exactly
    # backwards, which is what a shorter cone for a nearer car did.
    for ($qi = 0; $qi -lt 4; $qi++) {
        $qdir = @('f', 'r', 'b', 'l')[$qi]
        $qlvl = 'var l=1*$prop("' + $P + '.Radar.Q' + $qi + '");'
        foreach ($tone in @(@('y', 1), @('r', 2))) {
            $tk = $tone[0]; $tl = $tone[1]
            $img = [ordered]@{
                '$type' = 'SimHub.Plugins.OutputPlugins.GraphicalDash.Models.ImageItem, SimHub.Plugins'
                Image = 'tf4all-wedge-' + $tk + '-' + $qdir
                AutoSize = $false; AutoSizeScale = 1.0
                BackgroundColor = $script:CLEAR
                Height = [double]($rdR * 2); Left = [double]($rdCx - $rdR)
                Opacity = 100.0; Top = [double]($rdCy - $rdR)
                Visible = $true; Width = [double]($rdR * 2)
                Rotation = 0.0; RenderingSkip = 0; IsFreezed = $false
                Name = "d$slot-rd-w$qi$tk"
                Bindings = [ordered]@{}
            }
            $img.Bindings['Visible'] = BindJS 'Visible' (
                $qlvl + 'return l==' + $tl + ' && (' + ($vis -replace '^return ', '') + ')')
            $items.Add($img)
        }
    }

    # Rings ARE the thresholds the colours change on.
    $items.Add((New-Ring "d$slot-rd-r1" $rdCx $rdCy $rdR $script:LINE 1 $vis))
    $items.Add((New-Ring "d$slot-rd-r2" $rdCx $rdCy ($rdR * 20 / 40) '#FF3A3A2A' 1 $vis))
    $items.Add((New-Ring "d$slot-rd-r3" $rdCx $rdCy ($rdR * 8 / 40) '#FF4A2226' 1 $vis))

    # Opponents: white while they are simply out there, yellow once inside
    # the middle ring, red inside the inner one.
    for ($i = 0; $i -lt 8; $i++) {
        $dxJs = '(1*$prop("' + $P + '.Radar.D' + $i + 'X"))'
        $dyJs = '(1*$prop("' + $P + '.Radar.D' + $i + 'Y"))'
        $dlJs = 'var l=1*$prop("' + $P + '.Radar.D' + $i + 'L");'
        # Identical to the player marker: same car, same size, one scale.
        # Only the colour separates them.
        $dot = New-Rect "d$slot-rd-d$i" ($rdCx - 4) ($rdCy - 7) 8 14 $script:WHITE @{
            BackgroundColor = BindJS 'BackgroundColor' ($dlJs +
                'return l>2?"' + $script:RED + '":(l>1?"#FFE8C547":"' + $script:WHITE + '")')
        } 3
        $dot.Bindings['Left'] = BindJS 'Left' ('return ' + ($rdCx - 4) + '+' + $dxJs + '*' + $rdR)
        $dot.Bindings['Top']  = BindJS 'Top'  ('return ' + ($rdCy - 7) + '+' + $dyJs + '*' + $rdR)
        $dot.Bindings['Visible'] = BindJS 'Visible' (
            $dlJs + 'return l>0 && (' + ($vis -replace '^return ', '') + ')')
        $items.Add($dot)
    }

    # You, in green, pointing up. Green because you are the one car on here
    # that is never the hazard.
    # Left at the size it always was: this marker was already right, and
    # only the opponents needed to stop being round.
    $r = New-Rect "d$slot-rd-me" ($rdCx - 4) ($rdCy - 7) 8 14 $script:GREEN $null 3
    $r.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($r)
    $items.Add((AddNote 'rd' 'No other cars in this session.' 'Radar' $dOpp))

    # ---------------- DAMAGE -----------------------------------------
    # The car from above, not a row of tiles: front and rear bumpers, the
    # two flanks, and the shell between them, so a glance lands on the
    # corner that took the hit without reading a single label. SimHub
    # numbers these 1 to 5 and the mapping is the game's, so the shape
    # carries the meaning rather than a caption claiming more precision
    # than the source has.
    $vis = KeyVis 'Damage' $dDmg
    $hd = AddHead 'dm' 'DAMAGE' 'Damage'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $cw = 104.0; $ch = 150.0
    $cx = $ix + $iw / 2
    $cy = ($iy + 40 + $y + $h - 14) / 2
    $cl = $cx - $cw / 2; $ct = $cy - $ch / 2
    # Undamaged is quiet: a pristine car should read as chrome at a glance,
    # not as a wall of green competing with the tyre box next to it.
    $dmScale = 'if(isNaN(v)||v<1)return "#FF2A303A";' +
               'return v<25?"' + $script:GREEN + '":(v<60?"#FFE8A33D":"' + $script:RED + '")'

    # Tyres first, so the panels sit over them and read as the body on top.
    # Parenthesised: comma binds tighter than minus too, so a bare
    # subtraction here splits the literal into two arrays and PowerShell
    # tries to subtract one from the other.
    foreach ($tp in @(@('a', -8, 18), @('b', ($cw - 8), 18),
                      @('c', -8, ($ch - 46)), @('d', ($cw - 8), ($ch - 46)))) {
        $r = New-Rect "d$slot-dm-w$($tp[0])" ($cl + $tp[1]) ($ct + $tp[2]) 16 28 $script:REVBG $null 5
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
    }
    # Shell underneath every panel, so the gaps between them read as body.
    $r = New-Rect "d$slot-dm-body" $cl $ct $cw $ch $script:SUBPANEL $null 22
    $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)

    # 1 front, 2 rear, 3 left, 4 right, 5 centre: SimHub's order, laid out
    # where those panels actually are.
    foreach ($dm in @(
        @('f',  1, 10,             4,             ($cw - 20), 34, 14),
        @('r',  2, 10,             ($ch - 38),    ($cw - 20), 34, 14),
        @('l',  3, 4,              44,            22,         ($ch - 88), 8),
        @('rt', 4, ($cw - 26),     44,            22,         ($ch - 88), 8),
        @('c',  5, 30,             44,            ($cw - 60), ($ch - 88), 10))) {
        $dk = $dm[0]; $dn = $dm[1]
        $px = $cl + $dm[2]; $py = $ct + $dm[3]; $pw = $dm[4]; $ph = $dm[5]; $pr = $dm[6]
        $dJs = 'var v=1*$prop("' + $SIM + 'CarDamage' + $dn + '");'
        $r = New-Rect "d$slot-dm-$dk" $px $py $pw $ph $script:SUBPANEL @{
            BackgroundColor = BindJS 'BackgroundColor' ($dJs + $dmScale)
        } $pr
        $r.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($r)
    }

    # One number, for the panel that is worst. Five numbers on a shape this
    # size is unreadable, and the shape already says WHICH panel it is.
    $worstJs = 'var w=0;for(var i=1;i<=5;i++){var v=1*$prop("' + $SIM + 'CarDamage"+i);' +
               'if(!isNaN(v)&&v>w)w=v;}'
    $t = New-Text "d$slot-dm-worst" $ix ($iy + 22) $iw 26 17 '' $script:MUTED 1 @{
        Text = BindJS 'Text' ($worstJs + 'return w<1?"NO DAMAGE":"WORST "+Math.round(w)+"%"')
        TextColor = BindJS 'TextColor' ($worstJs +
            'return w<1?"' + $script:MUTED + '":(w<25?"' + $script:GREEN +
            '":(w<60?"#FFE8A33D":"' + $script:RED + '"))')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis; $items.Add($t)
    $items.Add((AddNote 'dm' 'This game does not report damage.' 'Damage' $dDmg))

    # ---------------- INPUTS (pedals + steering) ---------------------
    # What the driver did, next to what the wheel did. All four controls
    # prefer SimHub's own properties (every game it reads reports them)
    # and fall back to our parse for the Forza-without-forwarding case.
    # Clutch and handbrake keep their bars when released rather than
    # disappearing, so an automatic reads as "clutch at rest" instead of
    # looking like the box lost a channel.
    # Steering has NO SimHub equivalent (it exposes no universal steering
    # field), so that bar is ours alone and simply hides when the active
    # source does not report it.
    # Parenthesised on purpose: PowerShell's comma binds tighter than +, so
    # a bare concatenation splits into extra elements and the JS body here
    # gets cut off at the first + (which is what silently froze these bars).
    $vis = KeyVis 'Inputs' $null
    $hd = AddHead 'in' 'INPUTS' 'Inputs'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
    $barW = 38; $barGap = 14
    $barH = [math]::Max(60, $h - 118)
    $barY = $iy + 30
    # Labels overhang their bar into the gaps, so "Handbrake" fits without
    # widening the bars themselves.
    $lblW = $barW + $barGap - 2
    $amber = '#FFE8A33D'
    $pedals = @(
        @('thr', 'Throttle',  $script:GREEN,         (PedalJs $P 'Throttle'  'Throttle')),
        @('brk', 'Brake',     $script:RED,           (PedalJs $P 'Brake'     'Brake')),
        @('clu', 'Clutch',    $script:SCOPE_PURPLE,  (PedalJs $P 'Clutch'    'Clutch')),
        @('hbr', 'Handbrake', $amber,                (PedalJs $P 'Handbrake' 'Handbrake'))
    )
    $barX0 = $ix + ($iw - ($barW * $pedals.Count + $barGap * ($pedals.Count - 1))) / 2
    for ($pi = 0; $pi -lt $pedals.Count; $pi++) {
        $pkey = $pedals[$pi][0]; $plabel = $pedals[$pi][1]
        $pcol = $pedals[$pi][2]; $pJs = $pedals[$pi][3]
        $px = $barX0 + $pi * ($barW + $barGap)
        $trough = ThemePaint (New-Rect "d$slot-in-$pkey-bg" $px $barY $barW $barH $script:SUBPANEL $null 5) 'Sub'
        $trough.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($trough)
        # Fill grows from the bottom, so Top moves as Height does.
        $fill = New-Rect "d$slot-in-$pkey" $px ($barY + $barH) $barW 2 $pcol $null 5
        $fill.Bindings['Height'] = BindJS 'Height' ($pJs + 'return Math.max(2,' + $barH + '*v/100)')
        $fill.Bindings['Top']    = BindJS 'Top'    ($pJs + 'return ' + ($barY + $barH) + '-Math.max(2,' + $barH + '*v/100)')
        $fill.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($fill)
        $t = New-Text "d$slot-in-$pkey-l" ($px - ($lblW - $barW) / 2) ($barY + $barH + 4) $lblW 18 11 $plabel $script:MUTED 1
        $t.Bindings['Visible'] = BindJS 'Visible' $vis
        $items.Add($t)
    }
    # Steering: centre-origin bar. Hidden entirely when the source has no
    # steering to report, rather than sitting convincingly at centre.
    $steerJs = 'var s=1*$prop("' + $P + '.Steer");'
    $steerHas = 'return (' + ($isKeyBase = $sel + '=="Inputs"' + $rowCond) + ') && (1*$prop("' + $P + '.Steer"))>-1.5'
    $stY = $barY + $barH + 26
    $stTrough = ThemePaint (New-Rect "d$slot-in-st-bg" $ix $stY $iw 10 $script:SUBPANEL $null 5) 'Sub'
    $stTrough.Bindings['Visible'] = BindJS 'Visible' $steerHas
    $items.Add($stTrough)
    $stTick = New-Rect "d$slot-in-st-tick" ($ix + $iw / 2 - 1) ($stY - 3) 2 16 $script:LINE $null 0
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
    $hd = AddHead 'sc' 'VISUALIZER' 'Scope'
    $g = AddHeadGap; if ($g) { $items.Add($g) }   # under the title, not over it
    $items.Add($hd)
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
    # Reserves the legend row at the foot of the card. Both lanes shrink
    # rather than the lower one alone, so they stay equal, and the envelope
    # lane clears the key instead of running under its text.
    $scTotal = $h - 64
    $scLane  = ($scTotal - 16) / 2      # two equal lanes with a gap between
    # --- upper lane: the game's FFB force, as a line ---
    $l1y = $scTop
    $l1mid = $l1y + $scLane / 2
    # Each lane sits on its own lightened panel over the darker card, the
    # same figure-on-ground the full screen uses. Without it the two
    # traces float on one flat background and read as a single lane.
    $p1 = ThemePaint (New-Rect "d$slot-sc-p1" $ix $l1y $iw $scLane $script:SUBPANEL $null 4) 'Sub'
    $p1.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($p1)
    $z1 = New-Rect "d$slot-sc-z1" $ix $l1mid $iw 2 $script:SCOPE_GRID $null 0
    $z1.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($z1)
    # Clip threshold rails, dotted like the full screen: the force line
    # reaching them IS the clip, which is what the badge then reports.
    # 0.98 of the lane is where the trace clips, but a 2px dash drawn
    # centred on that line half-hangs out of the panel. Pull it in by the
    # dash height so the rails stay inside the lightened area.
    # Centre each dash ON its line (hence the -1 for the 2px height),
    # otherwise both are drawn downward from it and the bottom one eats
    # its own gap while the top one keeps a full one.
    $railOff = [math]::Min(($scLane / 2) * 0.98, ($scLane / 2) - 5)
    foreach ($side in @(-1, 1)) {
        $ry = $l1mid - $side * $railOff - 1
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
    $p2 = ThemePaint (New-Rect "d$slot-sc-p2" $ix $l2y $iw $scLane $script:SUBPANEL $null 4) 'Sub'
    $p2.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($p2)
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
    # A ChartItem lays its plot area out from the values it was built with
    # and does not re-lay-out when a bound Height arrives, so binding its
    # geometry left the trace drawn to the two-row lane while the panel had
    # grown to the one-row one. Both variants are emitted instead, each with
    # static geometry and gated on the layout, which is what the full-screen
    # visualizer does and why that one has always been right.
    $extra = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $a.Count; $i++) {
        $ia = $a[$i]; $ib = $b[$i]
        if ([string]$ia.'$type' -like '*ChartItem*') {
            $same = $true
            foreach ($pn in 'Top', 'Height', 'Left', 'Width') {
                if ([string]$ia.$pn -ne [string]$ib.$pn) { $same = $false }
            }
            if (-not $same) {
                $vA = [string]$ia.Bindings['Visible'].Formula.Expression -replace '^return ', ''
                $vB = [string]$ib.Bindings['Visible'].Formula.Expression -replace '^return ', ''
                $ia.Bindings['Visible'] = BindJS 'Visible' ('return ' + $tr + ' && (' + $vA + ')')
                $ib.Bindings['Visible'] = BindJS 'Visible' ('return !' + $tr + ' && (' + $vB + ')')
                $ib.Name = [string]$ib.Name + '-1'
                $extra.Add($ib)
                continue
            }
        }
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
    @($a) + @($extra)
}

function TabBar([string]$P) {
    # Idle is in here too. The tab bar bakes its own overlay check into
    # Visible, which makes Hide-ButtonsUnderOverlay skip it, so without this
    # the tab buttons would keep taking taps through the opaque idle card.
    $overlayClosed = '(""+$prop("' + $P + '.Overlay"))=="" && !$prop("' + $P + '.Idle.On")'
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
        # Outlined like everything else in the outline skin, with the active tab
        # carrying the fill instead of being the only filled thing.
        $bg = New-Rect "tab$i-bg" $x 446 127 32 $script:PANEL @{
            BackgroundColor = BindJS 'BackgroundColor' (
                'return $prop("' + $slot + '.Active")?(""+$prop("' + $script:TH + 'TileOn")):(""+$prop("' + $script:TH + 'Card"))')
            Left  = BindJS 'Left'  $leftJs
            Width = BindJS 'Width' $widthJs
        } 4
        $bg.BorderStyle.BorderColor = $script:CARD_EDGE
        $bg.BorderColor = $script:CARD_EDGE
        foreach ($sd in 'Top', 'Bottom', 'Left', 'Right') {
            $bg.BorderStyle."Border$sd" = 1
            $bg."Border$sd" = 1
        }
        $bg.BorderStyle.Bindings['BorderColor'] = ThemeBind 'BorderColor' 'CardEdge'
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
# =====================================================================
# IDLE CARD. Drawn over whatever tab is open once the car has been
# standing still, rather than as a screen of its own: there is no
# navigation to get stuck in, and any sign of driving clears it.
# Everything animates off Dash.Idle.T, a 0..1 phase the plugin derives
# from its own clock, so every connected dash moves in step and every
# curve built on it closes seamlessly at the wrap.
# =====================================================================
function IdleCard([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $on  = '$prop("' + $P + '.Idle.On")'
    $vis = 'return ' + $on
    $T   = '(1*$prop("' + $P + '.Idle.T"))'
    $col = '(""+($prop("' + $P + '.Idle.Color")||"' + $script:WHITE + '"))'
    $style = '(""+($prop("' + $P + '.Idle.Style")||"Aurora"))'

    # Opaque backdrop: the tab underneath keeps updating, and a translucent
    # card over live telemetry reads as a rendering fault.
    $bg = ThemePaint (New-Rect 'idle-bg' 0 0 800 480 '#FF0B0D11' $null 0) 'Bg'
    $bg.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($bg)

    $styleVis = { param($k) 'return ' + $on + ' && ' + $style + '=="' + $k + '"' }
    # Every motion is a sum of sines on INTEGER multiples of the phase, so
    # each closes exactly at the wrap and the loop has no seam, while the
    # different multiples beat against each other for long enough that it
    # never looks like it is repeating. One term is a circle; three is
    # weather.
    $w = { param($mult, $ph) 'Math.sin(6.283185307*(' + $mult + '*' + $T + '+' + $ph + '))' }
    $wc = { param($mult, $ph) 'Math.cos(6.283185307*(' + $mult + '*' + $T + '+' + $ph + '))' }

    # --- One pool of ellipses, several patterns, chosen inside the formula.
    #
    # Each pattern used to be its own set of items, and the idle card exists
    # on all seven screens, and the active screen evaluates every binding
    # every frame whether or not that pattern is the one showing. So a style
    # nobody had selected still cost a full frame's worth of maths, and two
    # of them together were the flicker.
    #
    # Here the patterns SHARE the pool and the style switches the maths. A
    # new pattern is a branch in an expression, not another forty items on
    # seven cards, and it costs nothing while it is not selected.
    #
    # Forty eight is few enough that a pattern has to be legible at low
    # density. A sunflower spiral and an evenly sampled Lissajous were both
    # tried here and both read as scattered dots: they need hundreds of
    # points before the eye finds the figure. What works instead is
    # structure the eye can complete from a handful of points, which is why
    # these three are arms, a trail, and interference.
    $POOL = 48
    $TAU  = '6.283185307'
    # Three drifting centres for the contour pattern. Rings are dealt round
    # robin between them, so the three families slide through each other and
    # it is their CROSSINGS that draw the contour lines. Nothing computes a
    # contour: the interference is the picture.
    $ctrM = @(1, 2, 3)
    $ctrN = @(2, 3, 1)
    $ctrP = @(0.0, 0.37, 0.72)

    for ($i = 0; $i -lt $POOL; $i++) {
        $c = $i % 3
        $k = [int][math]::Floor($i / 3)

        # Spiral: three arms of sixteen, one per accent, turning once per
        # loop while the radius breathes on a different multiple.
        #
        # The angular STEP is what decides whether this reads as an arm or
        # as a scatter. At 24 degrees a point twelve long wraps almost the
        # whole way round and crosses the other arms, which is a field of
        # dots. At 9 degrees it spans about 130 degrees: long enough to be a
        # curve, short enough that the eye keeps hold of it.
        $arm = $i % 3
        $j   = [int][math]::Floor($i / 3)
        $spTh = [math]::Round($arm * 2.0943951 + $j * 0.16, 4)
        $spR  = 16 + $j * 15
        $spJp = [math]::Round($j / 16.0, 4)
        $spS  = [math]::Round(3 + $j * 0.3, 2)
        $spPre = 'var b=' + $spR + '*(1+0.08*Math.sin(' + $TAU + '*(2*T+' + $spJp +
            ')));var th=' + $spTh + '+' + $TAU + '*T;'

        # Ribbon: one head running a 3:2 Lissajous with the rest of the pool
        # strung out BEHIND it in time. Sampling the curve evenly put
        # neighbours on opposite sides of the screen; a trail keeps them
        # adjacent, so the dots draw the line they are travelling along.
        $rbOff = [math]::Round($i * 0.0055, 5)
        $rbS   = [math]::Round(9 - 6 * ($i / [double]$POOL), 2)

        # Contour: concentric rings about a drifting centre. Wider than tall,
        # because a ring 1.25 times wider than high reads as ground seen at
        # an angle rather than as a target.
        $tM = $ctrM[$c]; $tN = $ctrN[$c]; $tP = $ctrP[$c]
        $tBase = 14 + $k * 15
        $tKp = [math]::Round($k / 8.0, 4)
        $tCx = '400+150*Math.sin(' + $TAU + '*(' + $tM + '*T+' + $tP + '))'
        $tCy = '240+92*Math.cos(' + $TAU + '*(' + $tN + '*T+' + $tP + '))'
        # [string] first: with an int on the left, + is addition and
        # PowerShell tries to parse the JS as a number.
        $tRad = [string]$tBase + '+7*Math.sin(' + $TAU + '*(2*T+' + $tKp + '))'

        $head = 'var T=' + $T + ';var s=' + $style + ';'

        # Static starting geometry, which is also what the preview renders.
        $rad0 = $tBase + 7 * [math]::Sin(2 * [math]::PI * $tKp)
        $cx0 = 400 + 150 * [math]::Sin(2 * [math]::PI * $tP)
        $cy0 = 240 + 92 * [math]::Cos(2 * [math]::PI * $tP)

        $e = New-Ellipse "idle-fld$i" ($cx0 - $rad0 * 1.25) ($cy0 - $rad0) `
            ($rad0 * 2.5) ($rad0 * 2) $script:MUTED 1
        # Fades along the index, which is outward for the rings and the arms
        # and backward along the trail: near and far in all three. Static,
        # because it was a fifth animated formula for depth the motion
        # already gives.
        $e.Opacity = [double]([math]::Round(18 + 30 * [math]::Exp(-$i / 16.0), 1))
        $e.Bindings['EllipseColor'] = ThemeBind 'EllipseColor' ('Accent' + ($c + 1))
        $e.Bindings['Left'] = BindJS 'Left' ($head + 'var x,r;' +
            'if(s=="Spiral"){' + $spPre + 'x=400+b*Math.cos(th)*1.4;r=' + $spS + ';}' +
            'else if(s=="Ribbon"){var p=T-' + $rbOff + ';x=400+300*Math.sin(' + $TAU + '*3*p);r=' + $rbS + ';}' +
            'else{x=' + $tCx + ';r=(' + $tRad + ')*1.25;}' +
            'return x-r')
        $e.Bindings['Top'] = BindJS 'Top' ($head + 'var y,r;' +
            'if(s=="Spiral"){' + $spPre + 'y=240+b*Math.sin(th)*0.92;r=' + $spS + ';}' +
            'else if(s=="Ribbon"){var p=T-' + $rbOff + ';y=240+185*Math.sin(' + $TAU + '*(2*p+0.25));r=' + $rbS + ';}' +
            'else{y=' + $tCy + ';r=' + $tRad + ';}' +
            'return y-r')
        $e.Bindings['Width'] = BindJS 'Width' ($head + 'var r;' +
            'if(s=="Spiral"){r=' + $spS + ';}' +
            'else if(s=="Ribbon"){r=' + $rbS + ';}' +
            'else{r=(' + $tRad + ')*1.25;}' +
            'return r*2')
        $e.Bindings['Height'] = BindJS 'Height' ($head + 'var r;' +
            'if(s=="Spiral"){r=' + $spS + ';}' +
            'else if(s=="Ribbon"){r=' + $rbS + ';}' +
            'else{r=' + $tRad + ';}' +
            'return r*2')
        $e.Bindings['Visible'] = BindJS 'Visible' ('return ' + $on + ' && (' +
            $style + '=="Topo"||' + $style + '=="Spiral"||' + $style + '=="Ribbon")')
        $items.Add($e)
    }

    # --- Aurora: slow coloured weather, drifting and breathing ---
    $aur = @(
        @(0, 320, 'Accent1', 1, 2, 3, 0.00, 0.31, 0.67),
        @(1, 270, 'Accent2', 2, 1, 4, 0.37, 0.72, 0.11),
        @(2, 240, 'Accent3', 1, 3, 2, 0.71, 0.19, 0.43),
        @(3, 200, 'Accent1', 3, 2, 5, 0.14, 0.58, 0.86),
        @(4, 180, 'Accent2', 2, 4, 3, 0.53, 0.05, 0.29)
    )
    foreach ($a in $aur) {
        $ai = $a[0]; $ar = $a[1]; $ac = $a[2]
        $mx = $a[3]; $my = $a[4]; $ms = $a[5]
        $p1 = $a[6]; $p2 = $a[7]; $p3 = $a[8]
        # Size breathes, so Left and Top have to follow it to stay centred.
        $sz = 'var s=' + $ar + '*(1+0.28*' + (& $w $ms $p3) + ');'
        $cxJs = 'var cx=400+' + (& $w $mx $p1) + '*250+' + (& $w (2 * $mx) $p2) + '*90;'
        $cyJs = 'var cy=240+' + (& $wc $my $p2) + '*140+' + (& $wc (3 * $my) $p1) + '*50;'
        $r = New-Rect "idle-aur$ai" 100 100 $ar $ar $script:MUTED $null ([int]($ar / 2))
        $r.Bindings['BackgroundColor'] = ThemeBind 'BackgroundColor' $ac
        $r.Opacity = 14.0
        $r.Bindings['Left']   = BindJS 'Left'   ($sz + $cxJs + 'return cx-s/2')
        $r.Bindings['Top']    = BindJS 'Top'    ($sz + $cyJs + 'return cy-s/2')
        $r.Bindings['Width']  = BindJS 'Width'  ($sz + 'return s')
        $r.Bindings['Height'] = BindJS 'Height' ($sz + 'return s')
        # Fading in and out as well as moving, so no blob is ever simply
        # sliding across a fixed background.
        # High enough that the colour actually reads against the backdrop:
        # below about 14 these land as grey and the whole thing looks dead.
        $r.Bindings['Opacity'] = BindJS 'Opacity' ('return 17+13*(1+' + (& $w ($ms + 2) $p1) + ')/2')
        $r.Bindings['Visible'] = BindJS 'Visible' (& $styleVis 'Aurora')
        $items.Add($r)
    }

    # --- Pulse: rings leaving the centre, the centre itself wandering ---
    for ($i = 0; $i -lt 5; $i++) {
        $ph = $i / 5.0
        $g = 'var g=(' + $T + '*2+' + $ph + ')%1;'
        $drift = 'var dx=' + (& $w 1 ($ph)) + '*70;var dy=' + (& $wc 2 ($ph)) + '*50;'
        $r = New-Rect "idle-pul$i" 340 180 120 120 $script:CLEAR $null 60
        $r.BorderStyle.BorderColor = $script:MUTED; $r.BorderColor = $script:MUTED
        foreach ($sd in 'Top', 'Bottom', 'Left', 'Right') {
            $r.BorderStyle."Border$sd" = 2; $r."Border$sd" = 2
        }
        $r.Bindings['Width']  = BindJS 'Width'  ($g + 'return 40+g*640')
        $r.Bindings['Height'] = BindJS 'Height' ($g + 'return 40+g*640')
        $r.Bindings['Left']   = BindJS 'Left'   ($g + $drift + 'return 400+dx-(40+g*640)/2')
        $r.Bindings['Top']    = BindJS 'Top'    ($g + $drift + 'return 240+dy-(40+g*640)/2')
        $r.Bindings['Opacity'] = BindJS 'Opacity' ($g + 'return 60*(1-g)*(g<0.08?g/0.08:1)')
        $r.BorderStyle.Bindings['BorderColor'] = ThemeBind 'BorderColor' 'Accent1'
        $r.Bindings['Visible'] = BindJS 'Visible' (& $styleVis 'Pulse')
        $items.Add($r)
    }

    # --- Streaks: rain that also drifts sideways and varies in weight ---
    for ($i = 0; $i -lt 16; $i++) {
        $sx = 12 + $i * 50
        $sp = ($i * 0.13) % 1.0
        $sl = 70 + ($i % 5) * 34
        $rate = 1 + ($i % 4) * 0.5
        $g2 = 'var g=(' + $T + '*' + $rate + '+' + $sp + ')%1;'
        $sway = 'var sw=' + (& $w (1 + ($i % 3)) $sp) + '*26;'
        $r = New-Rect "idle-str$i" $sx 0 3 $sl $script:MUTED $null 2
        $r.Bindings['BackgroundColor'] = ThemeBind 'BackgroundColor' 'Accent2'
        $r.Opacity = 34.0
        $r.Bindings['Top']  = BindJS 'Top'  ($g2 + 'return -' + $sl + '+g*' + (480 + $sl))
        $r.Bindings['Left'] = BindJS 'Left' ($sway + 'return ' + $sx + '+sw')
        $r.Bindings['Opacity'] = BindJS 'Opacity' ($g2 + 'return 12+30*Math.sin(3.14159*g)')
        $r.Bindings['Visible'] = BindJS 'Visible' (& $styleVis 'Streaks')
        $items.Add($r)
    }

    # --- Driver identity: the number is the point, the name labels it ---
    # Name over or under the number, the user's call. The pair keeps the
    # same total height either way, so the block stays put on the card and
    # only the two swap places.
    $above = '$prop("' + $P + '.Idle.NameAbove")'
    # Font is bound, not baked, so the choice applies live. Only the two
    # big readouts carry it: a Font binding on every text item in the
    # dashboard would be several hundred more formulas evaluated per
    # update to change something nobody reads at speed.
    $fontJs = 'return ""+($prop("' + $P + '.Idle.Font")||"")'
    $t = New-Text 'idle-num' 0 90 800 220 190 '' $script:WHITE 1 @{
        Text      = BindJS 'Text'      ('return ""+($prop("' + $P + '.Idle.Number")||"")')
        TextColor = BindJS 'TextColor' ('return ' + $col)
        Top       = BindJS 'Top'       ('return ' + $above + '?124:90')
        Font      = BindJS 'Font'      $fontJs
    } 'Bold' -Fontable
    $t.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($t)
    $t = New-Text 'idle-name' 0 310 800 54 40 '' $script:WHITE 1 @{
        Text      = BindJS 'Text'      ('return ""+($prop("' + $P + '.Idle.Name")||"")')
        TextColor = BindJS 'TextColor' ('return ' + $col)
        Top       = BindJS 'Top'       ('return ' + $above + '?62:344')
        Font      = BindJS 'Font'      $fontJs
    } 'Bold' -Fontable
    $t.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($t)

    # --- Plugin status along the foot. This is the one moment anyone is
    # looking at the dash and not the road, so it is where a version, a
    # supporter badge and an update notice actually get read.
    $t = New-Text 'idle-ver' 24 430 300 26 15 '' $script:MUTED 0 @{
        Text = BindJS 'Text' ('var v=""+($prop("' + $P + '.Version")||"");return v==""?"":"TF4ALL v"+v')
        TextColor = ThemeBind 'TextColor' 'Muted'
    }
    $t.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($t)
    $supVis = 'return ' + $on + ' && $prop("' + $P + '.Supporter")'
    $r = New-Rect 'idle-sup-bg' 24 396 132 26 '#FF3A2E12' $null 5
    $r.Bindings['Visible'] = BindJS 'Visible' $supVis
    $items.Add($r)
    $t = New-Text 'idle-sup' 24 396 132 26 13 'SUPPORTER' '#FFE8C547' 1 $null 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $supVis
    $items.Add($t)
    $t = New-Text 'idle-upd' 400 430 376 26 15 '' $script:GREEN 2 @{
        Text = BindJS 'Text' ('var v=""+($prop("' + $P + '.UpdateVersion")||"");' +
                              'return v==""?"":"UPDATE AVAILABLE  "+v')
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' ('return ' + $on + ' && $prop("' + $P + '.UpdateReady")')
    $items.Add($t)

    # --- Leaving: the whole card is the button. A dedicated Exit tile asks
    # the driver to aim at a 120px target on a screen whose entire job is
    # to be looked at rather than used. Added LAST so it sits over
    # everything, and named idle-exit so the hide pass leaves it alone.
    $t = New-Text 'idle-hint' 0 442 800 22 12 'TAP ANYWHERE TO RETURN' $script:LINE 1
    $t.Bindings['TextColor'] = ThemeBind 'TextColor' 'Muted'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($t)
    $b = New-Button 'idle-exit' 0 0 800 480 'DashIdleExit'
    $b.Bindings['Visible'] = BindJS 'Visible' $vis
    $items.Add($b)

    $items
}

function FlagBar([string]$P) {
    $F = 'DataCorePlugin.GameData.NewData.Flag_'
    # Orange is the meatball and Green the restart; both were missing, so
    # two of the seven flags a game can raise went unreported entirely.
    $any = '$prop("' + $P + '.FlagsOn") && (' +
           '$prop("' + $F + 'Yellow")||$prop("' + $F + 'Blue")||$prop("' + $F + 'Black")' +
           '||$prop("' + $F + 'White")||$prop("' + $F + 'Checkered")' +
           '||$prop("' + $F + 'Orange")||$prop("' + $F + 'Green"))'
    $vis = 'return ' + $any
    # Colour follows the flag, checkered reads as white.
    # The meatball is a BLACK flag with an orange disc, so it takes the
    # black band and earns its discs below rather than a colour of its own.
    $colJs = 'if($prop("' + $F + 'Yellow"))return "#F2E8C33D";' +
             'if($prop("' + $F + 'Blue"))return "#F23D7FE8";' +
             'if($prop("' + $F + 'Black")||$prop("' + $F + 'Orange"))return "#F21A1A1A";' +
             'if($prop("' + $F + 'Green"))return "#F23DC77A";' +
             'if($prop("' + $F + 'White")||$prop("' + $F + 'Checkered"))return "#F2E8EAEE";' +
             'return "#00FFFFFF"'
    $txtJs = 'if($prop("' + $F + 'Black")||$prop("' + $F + 'Orange")||$prop("' + $F + 'Blue"))' +
             'return "' + $script:WHITE + '";return "#FF101216"'
    $nameJs = 'var n=""+($prop("' + $F + 'Name")||"");if(n!="")return n.toUpperCase();' +
              'if($prop("' + $F + 'Yellow"))return "YELLOW FLAG";' +
              'if($prop("' + $F + 'Blue"))return "BLUE FLAG";' +
              'if($prop("' + $F + 'Black"))return "BLACK FLAG";' +
              'if($prop("' + $F + 'Checkered"))return "CHEQUERED FLAG";' +
              'if($prop("' + $F + 'Orange"))return "MEATBALL FLAG";' +
              'if($prop("' + $F + 'Green"))return "GREEN FLAG";' +
              'if($prop("' + $F + 'White"))return "WHITE FLAG";return ""'
    $bg = New-Rect 'flag-bg' 0 14 800 48 $script:CLEAR @{
        BackgroundColor = BindJS 'BackgroundColor' $colJs
    } 0
    # Yellow breathes, because it is the one flag that means react NOW and a
    # steady band beside six other steady bands does not say that. Everything
    # else holds still: a dash where each warning moves is a dash where none
    # of them stand out.
    $bg.Bindings['Opacity'] = BindJS 'Opacity' (
        'if(!$prop("' + $F + 'Yellow"))return 100;' +
        'var t=1*$prop("' + $P + '.PulseT");' +
        'return 62+38*(0.5+0.5*Math.sin(6.283185307*t))')
    $bg.Bindings['Visible'] = BindJS 'Visible' $vis
    $t = New-Text 'flag-t' 0 14 800 48 24 '' $script:WHITE 1 @{
        Text      = BindJS 'Text'      $nameJs
        TextColor = BindJS 'TextColor' $txtJs
    } 'Bold'
    $t.Bindings['Visible'] = BindJS 'Visible' $vis

    # A chequered flag that is just a white band is a white flag with the
    # wrong caption. The band stays white and black squares go over it, so
    # only half the squares need drawing, and the run under the text is
    # skipped rather than covered: the caption sits on clean white instead
    # of fighting a chequer for legibility.
    $out = [System.Collections.Generic.List[object]]::new()
    $out.Add($bg)

    # A near black band on a near black dash is a band nobody sees. Two thin
    # light rules give it an edge, on the two flags that use it.
    $darkVis = 'return ' + $any + ' && ($prop("' + $F + 'Black")||$prop("' + $F + 'Orange"))'
    foreach ($ey in @(14, 60)) {
        $e = New-Rect "flag-edge$ey" 0 $ey 800 2 '#FFE8EAEE' $null 0
        $e.Bindings['Visible'] = BindJS 'Visible' $darkVis
        $out.Add($e)
    }
    # The meatball itself, one either side of the caption so it reads the
    # same whichever half of the band the eye lands on.
    $mbVis = 'return ' + $any + ' && $prop("' + $F + 'Orange")'
    foreach ($mx in @(186, 582)) {
        $m = New-Rect "flag-mb$mx" $mx 22 32 32 '#FFE87A1F' $null 16
        $m.Bindings['Visible'] = BindJS 'Visible' $mbVis
        $out.Add($m)
    }
    $chk = 'return ' + $any + ' && $prop("' + $F + 'Checkered")'
    $sq = 24; $bandY = 14; $rows = 2
    $clearL = 236; $clearR = 564
    for ($cx = 0; $cx -lt 800; $cx += $sq) {
        for ($ry = 0; $ry -lt $rows; $ry++) {
            if ((([math]::Floor($cx / $sq)) + $ry) % 2 -ne 0) { continue }
            if (($cx + $sq) -gt $clearL -and $cx -lt $clearR) { continue }
            $w2 = [math]::Min($sq, 800 - $cx)
            $b2 = New-Rect "flag-chk$cx-$ry" $cx ($bandY + $ry * $sq) $w2 $sq '#FF101216' $null 0
            $b2.Bindings['Visible'] = BindJS 'Visible' $chk
            $out.Add($b2)
        }
    }
    $out.Add($t)
    $out
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
# What a Drive box shows, picked on the dash. Sixteen options is too many
# for the eight-slot list the preset picker uses, so this is a grid: four
# across, four down, every choice on one screen with no paging. The labels
# are the same ones the Settings combo used to offer, in the same order,
# because the plugin indexes tiles straight into DashDriveContentKeys.
function DriveBoxOverlay([string]$P) {
    $items = [System.Collections.Generic.List[object]]::new()
    $items.Add((OnOverlay (New-Rect 'db-backdrop' 0 0 800 480 $script:BACKDROP $null 0) 'drivebox'))
    $items.Add((OnOverlay (New-Text 'db-title' 0 16 800 28 19 '' $script:WHITE 1 @{
        Text = BindJS 'Text' ('return "' + 'BOX: ' + '"+(""+$prop("' + $P + '.Drive.EditSlot"))')
    } 'Bold') 'drivebox'))
    # Index-matched with DashDriveContentKeys: the plugin indexes a tile
    # straight into that array, so these must stay in the same order.
    $labels = @('Car facts', 'Damage', 'Friction circle', 'Fuel', 'G circle', 'Gains',
                'Inputs', 'Lap delta + times', 'Presets', 'Radar', 'Relative',
                'Tyre temps', 'Tyre wear', 'Visualizer', 'Empty')
    $cols = 4; $tw = 176; $th = 62; $gx = 12; $gy = 12
    $x0 = (800 - ($cols * $tw + ($cols - 1) * $gx)) / 2
    $y0 = 58
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $cx = $x0 + ($i % $cols) * ($tw + $gx)
        $cy = $y0 + [math]::Floor($i / $cols) * ($th + $gy)
        $bg = New-Rect "db-t$i-bg" $cx $cy $tw $th $script:TILE $null 6
        $items.Add((OnOverlay $bg 'drivebox'))
        $t = New-Text "db-t$i-t" $cx $cy $tw $th 15 $labels[$i] $script:WHITE 1
        $items.Add((OnOverlay $t 'drivebox'))
        $items.Add((OnOverlay (New-Button "db-t$i" $cx $cy $tw $th "DashDriveBoxPick$i") 'drivebox'))
    }
    $cyc = $y0 + 4 * ($th + $gy) + 6
    # Red and bold, like every other cancel on the dash. This one was muted
    # text on a panel, so the one place you reach it mid-session was the one
    # place it did not look like the way out.
    $items.Add((OnOverlay (New-Rect 'db-cancel-bg' 300 $cyc 200 40 $script:TILE $null 6) 'drivebox'))
    $items.Add((OnOverlay (New-Text 'db-cancel-t' 300 $cyc 200 40 15 'CANCEL' $script:RED 1 $null 'Bold') 'drivebox'))
    $items.Add((OnOverlay (New-Button 'db-cancel' 300 $cyc 200 40 'DashDriveBoxCancel') 'drivebox'))
    $items
}

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
$s1.Add((New-Card 'mg-panel' 16 116 376 240))
$s1.Add((New-Text 'mg-label' 32 128 344 26 16 'MASTER GAIN  (tap value to type)' $MUTED 0))
$s1.Add((New-Text 'mg-value' 32 156 344 96 64 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return (1*$prop("' + $P + '.MasterGain")).toFixed(2)')
} 'Bold'))
# Tap zone hugs the centered digits, not the card's full width: dead
# space beside the number must not open the keypad.
$s1.Add((New-Button 'mg-value-tap' 114 156 180 96 'DashMasterGainOpen'))
StepperTiles 'mg' 32 264 344 76 'DashMasterGainDown' 'DashMasterGainUp' | ForEach-Object { $s1.Add($_) }

# Audio gain (right column); same tap-to-type value
$s1.Add((New-Card 'ag-panel' 408 116 376 240))
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
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P | ForEach-Object { $s1.Add($_) }
KeypadOverlay $P | ForEach-Object { $s1.Add($_) }
FlagBar $P | ForEach-Object { $s1.Add($_) }
IdleCard $P | ForEach-Object { $s1.Add($_) }
ToastBar $P | ForEach-Object { $s1.Add($_) }

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
$s2.Add((New-Card 'cf-eng-panel' 16 92 768 108))
$s2.Add((New-Text 'cf-eng-label' 32 102 400 24 15 'ENGINE LAYOUT' $MUTED 0))
$s2.Add((New-Text 'cf-eng-value' 32 128 600 60 34 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var s=""+($prop("' + $P + '.EngineLayoutSource")||"");var l=""+($prop("' + $P + '.EngineLayout")||"Auto");return s!=""?(l+"  ("+s+")"):l')
} 'Bold'))
$s2.Add((New-Rect 'cf-eng-hint' 648 116 120 60 $TILE))
$s2.Add((New-Text 'cf-eng-hint-t' 648 116 120 60 17 'CHANGE' $WHITE 1))
$s2.Add((New-Button 'cf-eng-btn' 648 116 120 60 'DashEngineLayoutOpen'))

# Redline row: tap value = keypad; +/- 50 steppers on the right
$s2.Add((New-Card 'cf-rl-panel' 16 212 768 120))
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
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P | ForEach-Object { $s2.Add($_) }
EngineLayoutOverlay $P | ForEach-Object { $s2.Add($_) }

TabBar $P | ForEach-Object { $s2.Add($_) }
# ---- overlay: shared keypad (redline entry opens it via DashRedlineOpen) ----
KeypadOverlay $P | ForEach-Object { $s2.Add($_) }
FlagBar $P | ForEach-Object { $s2.Add($_) }
IdleCard $P | ForEach-Object { $s2.Add($_) }
ToastBar $P | ForEach-Object { $s2.Add($_) }

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
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P | ForEach-Object { $s3.Add($_) }
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
IdleCard $P | ForEach-Object { $s3.Add($_) }
ToastBar $P | ForEach-Object { $s3.Add($_) }

# =====================================================================
# Screen 4: PRESETS (game preset + car preset, picker overlay)
# =====================================================================
$s4 = [System.Collections.Generic.List[object]]::new()
$s4.Add((New-Text 'pr-title' 16 16 300 36 24 'PRESETS' $WHITE 0 $null 'Bold'))
$s4.Add((New-Text 'pr-car' 320 16 464 36 16 '' $MUTED 2 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Game")||"No game");var c=""+($prop("' + $P + '.CarName")||"");return c!=""?(g+"  -  "+c):g')
}))

$s4.Add((New-Card 'pr-game-panel' 16 64 768 150))
$s4.Add((New-Text 'pr-game-label' 32 76 500 24 15 'GAME PRESET  (applies to the whole game)' $MUTED 0))
$s4.Add((New-Text 'pr-game-value' 32 104 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?p:"(manual tune)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-game-hint' 648 96 120 84 $TILE))
$s4.Add((New-Text 'pr-game-hint-t' 648 96 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-game-btn' 648 96 120 84 'DashPresetOpenGame'))

$s4.Add((New-Card 'pr-carp-panel' 16 228 768 150))
$s4.Add((New-Text 'pr-carp-label' 32 240 500 24 15 'CAR PRESET  (this car only)' $MUTED 0))
$s4.Add((New-Text 'pr-carp-value' 32 268 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.CarPresetName")||"");return p!=""?p:"(none saved for this car)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-carp-hint' 648 260 120 84 $TILE))
$s4.Add((New-Text 'pr-carp-hint-t' 648 260 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-carp-btn' 648 260 120 84 'DashPresetOpenCar'))


TabBar $P | ForEach-Object { $s4.Add($_) }
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P | ForEach-Object { $s4.Add($_) }
PresetOverlay $P | ForEach-Object { $s4.Add($_) }
FlagBar $P | ForEach-Object { $s4.Add($_) }
IdleCard $P | ForEach-Object { $s4.Add($_) }
ToastBar $P | ForEach-Object { $s4.Add($_) }

# =====================================================================
# Screen 5: VISUALIZER (scrolling signal waveforms, stacked lanes)
# Top lane = the game's FFB steering force as a smooth ChartItem line
# (amber); bottom lane = the Trueforce haptic signal actually streaming
# to the wheel, drawn as a mirrored envelope from the plugin's 78-column
# 32 ms ring (purple). Palette mirrors the FFB-architecture doc (base
# amber / tf purple).
# =====================================================================
$SCOPE_AMBER  = '#FFE3A445'   # NOT themed: this colour is the legend
$SCOPE_PURPLE = '#FFA08CFF'   # NOT themed: this colour is the legend
$SCOPE_GRID   = '#FF262F3A'   # themed as Sub
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
$s5.Add((New-Card 'sc-ffb-panel' 10 74 780 160))
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
$s5.Add((New-Card 'sc-tex-panel' 10 262 780 164))
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
IdleCard $P | ForEach-Object { $s5.Add($_) }
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
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P | ForEach-Object { $s6.Add($_) }
KeypadOverlay $P | ForEach-Object { $s6.Add($_) }
FlagBar $P | ForEach-Object { $s6.Add($_) }
IdleCard $P | ForEach-Object { $s6.Add($_) }
ToastBar $P | ForEach-Object { $s6.Add($_) }

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
$revCen  = '$prop("' + $P + '.RevCentered")'

# Gear: the one readout that is always meaningful, in every game.
# The gear keeps the middle column in both layouts (the bottom boxes
# grow into the top row rather than across it) and just recentres.
# Our own frame first, SimHub second: a Forza player usually leaves
# forwarding off, which leaves SimHub's copies empty all session.
$gear = New-Text 'dr-gear' 300 55 200 210 130 '' $WHITE 1 @{
    TextColor = ThemeBind 'TextColor' 'Text'
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Gear")||"");' +
                          'if(g=="")g=""+($prop("' + $SIM + 'Gear")||"");return g==""?"N":g')
    Top  = BindJS 'Top'  ('return ' + $twoRows + '?55:130')
} 'Bold'
$s7.Add($gear)

# Revs above the gear, in the band between the rev strip and the digits.
# Positioned per band: the strip drops to 16 when it is narrowed to this
# column, and the gear sits lower in the one-row layout, so all four
# combinations get their own resting place. Sits 6px below the centre of
# its band rather than on it, which reads as belonging to the gear
# instead of floating between it and the strip.
$rpmTop = 'return ' + $twoRows + '?(' + $revCen + '?34:26):(' + $revCen + '?71:63)'
$rpm = New-Text 'dr-rpm' 300 26 200 28 22 '' $MUTED 1 @{
    TextColor = ThemeBind 'TextColor' 'Muted'
    Text = BindJS 'Text' ('var r=1*$prop("' + $P + '.Rpm");' +
                          'if(!(r>0))r=1*$prop("' + $SIM + 'Rpms");' +
                          'if(isNaN(r)||r<=0)return "";return Math.round(r)+" rpm"')
    Top  = BindJS 'Top' $rpmTop
} 'Bold'
$s7.Add($rpm)
# Speed follows SimHub's unit setting when SimHub has the data; from our
# own frame it is km/h, converted here when the user is set to MPH.
$spd = New-Text 'dr-speed' 300 268 200 40 26 '' $MUTED 1 @{
    TextColor = ThemeBind 'TextColor' 'Muted'
    Text = BindJS 'Text' ('var u=""+($prop("' + $SIM + 'SpeedLocalUnit")||"");' +
                          'var v=1*$prop("' + $SIM + 'SpeedLocal");' +
                          'if(!(v>0)){var k=1*$prop("' + $P + '.SpeedKmh");' +
                          'if(!(k>0))return "--";v=(u=="MPH")?k*0.621371:k;if(u=="")u="KMH";}' +
                          'return Math.round(v)+" "+u')
    Top  = BindJS 'Top'  ('return ' + $twoRows + '?268:343')
} 'Bold'
$s7.Add($spd)

# Pedals around the gear (Dash.DrivePedals): thin throttle and brake
# bars either side of it with steering underneath, using space the gear
# column has spare. Brake left, throttle right, matching a pedal box.
# Independent of the Inputs box, which shows the same three in a card.
$pedOn = '$prop("' + $P + '.DrivePedals")'
# The bars run all the way down to the steering, so they use the whole
# height the gear column has spare. Only the top moves with the layout:
# the bottom is fixed at the content edge in both.
$pedTop2 = 60; $pedTop1 = 135; $pedBot = 418
$pedTopJs = 'return ' + $twoRows + '?' + $pedTop2 + ':' + $pedTop1
$pedHJs   = 'return ' + $twoRows + '?' + ($pedBot - $pedTop2) + ':' + ($pedBot - $pedTop1)
foreach ($pd in @(
    @('brk', 302, (PedalJs $P 'Brake'    'Brake'),    $RED),
    @('thr', 488, (PedalJs $P 'Throttle' 'Throttle'), $GREEN))) {
    $pk = $pd[0]; $px = $pd[1]; $pJs = $pd[2]; $pCol = $pd[3]
    $tr = New-Rect "dr-ped-$pk-bg" $px $pedTop2 10 ($pedBot - $pedTop2) $TILE @{
        Top    = BindJS 'Top'    $pedTopJs
        Height = BindJS 'Height' $pedHJs
    } 5
    $tr.Bindings['Visible'] = BindJS 'Visible' ('return ' + $pedOn)
    $s7.Add($tr)
    # Fills upward from the fixed bottom edge, so Top moves as Height does.
    $pedFillJs = $pJs + 'var H=' + $twoRows + '?' + ($pedBot - $pedTop2) + ':' + ($pedBot - $pedTop1) + ';var hh=Math.max(2,H*v/100);'
    $fl = New-Rect "dr-ped-$pk" $px ($pedBot - 2) 10 2 $pCol $null 5
    $fl.Bindings['Height'] = BindJS 'Height' ($pedFillJs + 'return hh')
    $fl.Bindings['Top']    = BindJS 'Top'    ($pedFillJs + 'return ' + $pedBot + '-hh')
    $fl.Bindings['Visible'] = BindJS 'Visible' ('return ' + $pedOn)
    $s7.Add($fl)
}
# Steering sits on the bottom edge, level with the foot of the boxes, so
# it reads as the base of the gear column in either layout. Centre-origin,
# hidden when the source reports no steering rather than sitting
# convincingly straight.
$steerVis = 'return ' + $pedOn + ' && (1*$prop("' + $P + '.Steer"))>-1.5'
$stBg = New-Rect 'dr-st-bg' 302 428 196 8 $TILE $null 4
$stBg.Bindings['Visible'] = BindJS 'Visible' $steerVis
$s7.Add($stBg)
$stTick = New-Rect 'dr-st-tick' 399 425 2 14 $script:LINE $null 0
$stTick.Bindings['Visible'] = BindJS 'Visible' $steerVis
$s7.Add($stTick)
$stDot = New-Rect 'dr-st' 393 426 12 12 $WHITE $null 6
$stDot.Bindings['Left'] = BindJS 'Left' ('var s=1*$prop("' + $P + '.Steer");if(s<-1)s=-1;if(s>1)s=1;return 394+s*92')
$stDot.Bindings['Visible'] = BindJS 'Visible' $steerVis
$s7.Add($stDot)

# The whole gear column is the rev strip's span control: tap it to switch
# between full width and just this column. Added LAST so it sits over the
# gear, revs, speed and pedals rather than under them, and kept clear of
# the strip itself at the top and the steering at the foot. Hide-Buttons-
# UnderOverlay gates it on no overlay being open, like every other button.
$s7.Add((New-Button 'dr-revspan' 300 40 200 380 'DashRevStripSpanToggle'))

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
# Under the overlays: a backdrop is meant to dim this, not sit beneath it.
RevStrip $P $true | ForEach-Object { $s7.Add($_) }
KeypadOverlay $P | ForEach-Object { $s7.Add($_) }
EngineLayoutOverlay $P | ForEach-Object { $s7.Add($_) }
PresetOverlay $P | ForEach-Object { $s7.Add($_) }
FlagBar $P | ForEach-Object { $s7.Add($_) }
IdleCard $P | ForEach-Object { $s7.Add($_) }
ToastBar $P | ForEach-Object { $s7.Add($_) }
DriveBoxOverlay $P | ForEach-Object { $s7.Add($_) }

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
    # Also gated on idle: the card is opaque and covers the whole screen, so
    # every button under it must stop taking taps. Its own EXIT is excluded
    # by name, being the one control that has to work while it is showing.
    $closed = '(""+$prop("TrueforcePlugin.Dash.Overlay"))=="" && !$prop("TrueforcePlugin.Dash.Idle.On")'
    foreach ($it in $items) {
        if ([string]$it.'$type' -notlike '*ButtonItem*') { continue }
        if ([string]$it.Name -eq 'idle-exit') { continue }
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

# Themes every static palette colour on a finished screen, in one pass.
#
# Doing this at each call site meant most of them were missed: colours
# were bound in a handful of places and hardcoded in hundreds, which is
# why early themes only appeared to change the background. A pass over the
# built items cannot miss one, and a new box is themed the day it is
# added without anyone remembering to do it.
#
# An item that already binds a colour is left alone. Those are the ones
# that MEAN something (a tyre at temperature, a delta against the best, a
# flag), and a theme must not be able to repaint meaning.
# Built imperatively rather than as a literal: the keys are COLOUR VALUES,
# and a hashtable literal throws on a duplicate key. Two palette entries
# sharing a colour is normal in a themed set (Ember's card edge and button
# edge are the same orange), so first-wins is the rule and the order below
# is the priority. As a literal this exploded the moment a palette reused
# a colour, which is a trap waiting for whoever adds the next theme.
$THEME_MAP = @{}
function Map-Theme([string]$prop, [string]$colour, [string]$key) {
    if ([string]::IsNullOrEmpty($colour)) { return }
    if (-not $script:THEME_MAP.ContainsKey($prop)) { $script:THEME_MAP[$prop] = @{} }
    if (-not $script:THEME_MAP[$prop].ContainsKey($colour)) { $script:THEME_MAP[$prop][$colour] = $key }
}
Map-Theme 'BackgroundColor' $SCOPE_GRID   'Sub'
Map-Theme 'BackgroundColor' $REVBG        'Sub'
# NOT $PANEL, and NOT $BG. $PANEL is transparent in the outlined look, so
# it is the same value as $CLEAR, and keying on it matched every text item,
# button, ellipse and image that simply has no background: 1533 of them,
# every one of which would have painted the card colour in any theme where
# a card is filled. New-Card and the screen backdrop bind themselves, so
# neither ever needed to be in this map.
Map-Theme 'BackgroundColor' $SUBPANEL     'Sub'
Map-Theme 'BackgroundColor' $BTN          'Btn'
Map-Theme 'BackgroundColor' $TILE         'Tile'
Map-Theme 'BackgroundColor' $TILEON       'TileOn'
Map-Theme 'TextColor'       $WHITE        'Text'
Map-Theme 'TextColor'       $MUTED        'Muted'
Map-Theme 'TextColor'       $GRAY         'Dim'
Map-Theme 'TextColor'       $LINE         'Dim'
Map-Theme 'BorderColor'     $CARD_EDGE    'CardEdge'
Map-Theme 'BorderColor'     $BTN_EDGE     'BtnEdge'
# Hairlines get their own tone rather than borrowing the faint-text one.
# They are not read, only sensed, so they sit far darker than the grey that
# keeps small text legible; sharing one key would have brightened every
# ring, tick and rev socket the moment text went neutral.
Map-Theme 'BorderColor'     $LINE         'Line'
Map-Theme 'BackgroundColor' $LINE         'Line'
Map-Theme 'EllipseColor'    $MUTED        'Muted'

function Apply-Theme($items) {
    foreach ($it in $items) {
        if (-not $it.Bindings) { continue }
        foreach ($prop in $THEME_MAP.Keys) {
            if ($prop -eq 'BorderColor') { continue }        # lives in BorderStyle, below
            if (-not $it.Contains($prop)) { continue }
            if ($it.Bindings.Contains($prop)) { continue }   # already means something
            $cur = [string]$it.$prop
            $key = $THEME_MAP[$prop][$cur]
            if ($key) { $it.Bindings[$prop] = ThemeBind $prop $key }
        }
        # A tile IS a button, so it shows an edge like one. Only the Drive
        # screen was built with New-Btn, which left its 36 buttons outlined
        # and the tiles on every other tab bare: the same control looked
        # like two different things depending on which tab it was on, and a
        # theme could only repaint the edge that existed.
        #
        # Done here rather than at the call sites for the usual reason: there
        # are about 150 of them across seven screens and any one that got
        # missed would be invisible until someone happened to compare two
        # tabs. Setting the static colour is enough, because the pass below
        # is what turns it into a binding.
        if ($it.Contains('BackgroundColor') -and $it.BorderStyle -and
            [int]$it.BorderStyle.BorderTop -eq 0 -and
            ([string]$it.BackgroundColor -eq $script:TILE -or
             [string]$it.BackgroundColor -eq $script:TILEON)) {
            $it.BorderStyle.BorderColor = $script:BTN_EDGE
            if ($it.Contains('BorderColor')) { $it.BorderColor = $script:BTN_EDGE }
            foreach ($sd in 'Top', 'Bottom', 'Left', 'Right') {
                $it.BorderStyle."Border$sd" = 1
                if ($it.Contains("Border$sd")) { $it."Border$sd" = 1 }
            }
        }

        # The outline is a special case in BOTH directions: the colour the
        # viewer draws comes from BorderStyle, not from the item's own
        # BorderColor, and so does the binding it reads. The old pass got
        # both wrong, so it compared against a value nothing renders and
        # wrote to a slot nothing reads. Rings, rev sockets and every card
        # edge went untouched by a theme as a result.
        if (-not $it.Contains('BorderStyle') -or -not $it.BorderStyle) { continue }
        if ($it.BorderStyle.Bindings.Contains('BorderColor')) { continue }
        $key = $THEME_MAP['BorderColor'][[string]$it.BorderStyle.BorderColor]
        if ($key) {
            $it.BorderStyle.Bindings['BorderColor'] = ThemeBind 'BorderColor' $key
            # Keep the legacy top-level copy agreeing with it, so the two
            # never disagree about what colour the box is meant to be.
            if ($it.Contains('BorderColor')) { $it.BorderColor = $it.BorderStyle.BorderColor }
        }
    }
    $items
}

function New-Screen([string]$name, $items, [int]$tabIndex) {
    # Inserted at the FRONT so it sits under everything on the screen.
    $items = @((ThemePaint (New-Rect ('bg-' + $tabIndex) 0 0 800 480 $script:BG $null 0) 'Bg')) + @($items)
    [ordered]@{
        Name = $name; InGameScreen = $true; IdleScreen = $true; PitScreen = $false
        ScreenId = [guid]::NewGuid().ToString()
        AllowOverlays = $true; IsForegroundLayer = $false; IsOverlayLayer = $false
        OverlayTriggerExpression = [ordered]@{ Expression = '' }
        ScreenEnabledExpression  = [ordered]@{ JSExt = 1; Interpreter = 1; Expression = 'return (1*$prop("TrueforcePlugin.Dash.Tab"))==' + $tabIndex }
        OverlayMaxDuration = 0; OverlayMinDuration = 0; IsBackgroundLayer = $false
        BackgroundColor = $CLEAR
        Items = @(Apply-Theme (Hide-ButtonsUnderOverlay $items))
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
        (New-Screen 'Gains' $s1 0),
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
    Images = @($(if ($script:DASH_IMAGES.Count) { ($script:DASH_IMAGES | ForEach-Object { $_.Meta }) }))
    Metadata = $meta
    ShowOnScreenControls = $true
    IsOverlay = $false
    EnableClickThroughOverlay = $true
    EnableOnDashboardMessaging = $false
}

# Every binding body is JS assembled by string concatenation, and a
# truncated one fails silently: the viewer just leaves the item frozen at
# its static value, which looks like a dead control rather than a broken
# formula (the Inputs pedal bars sat at zero for a release this way).
# Unbalanced quotes or brackets catch that class of mistake here instead.
# An empty colour is a constant that was read before it was assigned.
# SimHub rejects the whole dashboard for one of them, with an error that
# names a screen index and nothing else, so catch it here where the item
# name is still known.
$blank = @()
foreach ($scr in $doc.Screens) {
    foreach ($it in $scr.Items) {
        foreach ($ck in 'BackgroundColor', 'TextColor', 'BorderColor', 'EllipseColor', 'FillColor') {
            if ($it.Contains($ck) -and [string]::IsNullOrEmpty([string]$it.$ck)) {
                $blank += "$($it.Name).$ck"
            }
        }
    }
}
if ($blank.Count) {
    $blank | Select-Object -First 10 | ForEach-Object { Write-Host "EMPTY COLOUR  $_" -ForegroundColor Red }
    throw "$($blank.Count) item(s) with an empty colour; dashboard NOT written."
}

# A BorderColor binding on the ITEM is accepted by the viewer, saved, and
# then ignored: the outline is drawn from BorderStyle and the binding is
# read from BorderStyle.Bindings. Nothing warns, the edge simply never
# moves. 257 of them shipped that way and made themes look like they only
# touched backgrounds, so it is a hard error now rather than a thing to
# notice by eye.
$misplaced = @()
foreach ($scr in $doc.Screens) {
    foreach ($it in $scr.Items) {
        if ($it.Bindings -and $it.Bindings.Contains('BorderColor')) { $misplaced += $it.Name }
    }
}
if ($misplaced.Count) {
    $misplaced | Select-Object -First 10 | ForEach-Object {
        Write-Host "BORDER BIND ON ITEM  $_  (belongs in BorderStyle.Bindings)" -ForegroundColor Red
    }
    throw "$($misplaced.Count) item(s) bind BorderColor where it is never read; dashboard NOT written."
}

# Text is white or grey. The only colours allowed on it are the ones that
# MEAN something, and they are listed here by hand so that adding a fifth
# is a decision rather than an accident.
#
# This is checked rather than trusted because a text colour can arrive from
# three places: the item, the theme, or a literal baked into a computed
# expression. The last kind is invisible to the theme pass, and ~120 of
# them sat there holding a blue-grey through every palette.
$SEMANTIC_TEXT = @('#FF37D67A', '#FFE5484D', '#FFE8A33D', '#FFE8C547')
function ColorSpread([string]$hex) {
    $r = [Convert]::ToInt32($hex.Substring(3, 2), 16)
    $g = [Convert]::ToInt32($hex.Substring(5, 2), 16)
    $b = [Convert]::ToInt32($hex.Substring(7, 2), 16)
    [Math]::Max([Math]::Max($r, $g), $b) - [Math]::Min([Math]::Min($r, $g), $b)
}
$tinted = @()
foreach ($scr in $doc.Screens) {
    foreach ($it in $scr.Items) {
        if (-not $it.Contains('TextColor')) { continue }
        $seen = @()
        if ($it.Bindings -and $it.Bindings.Contains('TextColor')) {
            $ex = [string]$it.Bindings['TextColor'].Formula.Expression
            $seen = [regex]::Matches($ex, '#[0-9A-Fa-f]{8}') | ForEach-Object { $_.Value }
        } else {
            $seen = @([string]$it.TextColor)
        }
        foreach ($c in $seen) {
            if ($SEMANTIC_TEXT -contains $c.ToUpper()) { continue }
            if ((ColorSpread $c) -gt 12) { $tinted += "$($it.Name)  $c" }
        }
    }
}
if ($tinted.Count) {
    $tinted | Select-Object -First 10 | ForEach-Object { Write-Host "TINTED TEXT  $_" -ForegroundColor Red }
    throw "$($tinted.Count) text colour(s) are neither grey nor meaningful; dashboard NOT written."
}

$bad = @()
foreach ($scr in $doc.Screens) {
    foreach ($it in $scr.Items) {
        if (-not $it.Bindings) { continue }
        foreach ($bk in $it.Bindings.Keys) {
            $ex = [string]$it.Bindings[$bk].Formula.Expression
            $q = ([regex]::Matches($ex, '"')).Count
            $op = ([regex]::Matches($ex, '[\(\[\{]')).Count
            $cl = ([regex]::Matches($ex, '[\)\]\}]')).Count
            # Balanced but still dead: a statement spliced in after the
            # return keyword parses as nothing and the item never updates.
            $malformed = $ex -match 'return\s+(var|if|for)'
            if (($q % 2) -ne 0 -or $op -ne $cl -or $malformed) {
                $bad += "$($it.Name).$bk : $ex"
            }
        }
    }
}
if ($bad.Count) {
    $bad | ForEach-Object { Write-Host "MALFORMED BINDING  $_" -ForegroundColor Red }
    throw "$($bad.Count) malformed binding expression(s); dashboard NOT written."
}

$json = $doc | ConvertTo-Json -Depth 60
[IO.File]::WriteAllText((Join-Path $OutDir 'TF4ALL Dash.djson'), $json, [Text.UTF8Encoding]::new($false))
# The image bundle beside the djson, one entry per image. Rewritten every
# run so it can never drift from the Images array that describes it.
$resPath = Join-Path $OutDir 'TF4ALL Dash.djson.ressources'
if (Test-Path $resPath) { Remove-Item $resPath -Force }
if ($script:DASH_IMAGES.Count) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fs = [System.IO.File]::Open($resPath, [System.IO.FileMode]::CreateNew)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($im in $script:DASH_IMAGES) {
        $entry = $zip.CreateEntry($im.Meta.Name + $im.Meta.Extension)
        $es = $entry.Open()
        $es.Write($im.Bytes, 0, $im.Bytes.Length)
        $es.Close()
    }
    $zip.Dispose(); $fs.Close()
}

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

# Per corner, not one radius for all four. A quarter disc is a square with
# ONE corner rounded to the full side, and drawing it with a uniform radius
# renders a circle instead, which is a preview that cannot check the very
# shape it was opened to check.
function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r,
                         [float]$rtr = -1, [float]$rbr = -1, [float]$rbl = -1) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    # One argument keeps the old uniform behaviour.
    if ($rtr -lt 0) { $rtr = $r }
    if ($rbr -lt 0) { $rbr = $r }
    if ($rbl -lt 0) { $rbl = $r }
    $rtl = $r
    if ($rtl -le 0 -and $rtr -le 0 -and $rbr -le 0 -and $rbl -le 0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
        return $p
    }
    $cap = [math]::Min($w, $h)
    foreach ($n in 'rtl', 'rtr', 'rbr', 'rbl') {
        if ((Get-Variable $n -ValueOnly) -gt $cap) { Set-Variable $n -Value $cap }
    }
    # Straight segments where a corner has no radius, arcs where it does.
    if ($rtl -gt 0) { $p.AddArc($x, $y, 2 * $rtl, 2 * $rtl, 180, 90) }
    else { $p.AddLine($x, $y, $x, $y) }
    if ($rtr -gt 0) { $p.AddArc($x + $w - 2 * $rtr, $y, 2 * $rtr, 2 * $rtr, 270, 90) }
    else { $p.AddLine($x + $w, $y, $x + $w, $y) }
    if ($rbr -gt 0) { $p.AddArc($x + $w - 2 * $rbr, $y + $h - 2 * $rbr, 2 * $rbr, 2 * $rbr, 0, 90) }
    else { $p.AddLine($x + $w, $y + $h, $x + $w, $y + $h) }
    if ($rbl -gt 0) { $p.AddArc($x, $y + $h - 2 * $rbl, 2 * $rbl, 2 * $rbl, 90, 90) }
    else { $p.AddLine($x, $y + $h, $x, $y + $h) }
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
        # Items resting at Opacity 0 (badge glow layers, proximity sectors)
        # stay hidden unless the override raises them, which is the whole
        # point of an override: this test used to run first and silently
        # dropped every item whose live opacity is what makes it appear.
        $opOv = $null
        if ($o -and $o.ContainsKey('Opacity')) { $opOv = [double]$o.Opacity }
        if ($null -eq $opOv -and $it.Contains('Opacity') -and [double]$it.Opacity -le 0) { continue }
        if ($null -ne $opOv -and $opOv -le 0) { continue }
        $x = [float]$it.Left; $y = [float]$it.Top; $w = [float]$it.Width; $h = [float]$it.Height
        if ($o -and $o.ContainsKey('Left'))   { $x = [float]$o.Left }
        if ($o -and $o.ContainsKey('Top'))    { $y = [float]$o.Top }
        if ($o -and $o.ContainsKey('Width'))  { $w = [float]$o.Width }
        if ($o -and $o.ContainsKey('Height')) { $h = [float]$o.Height }
        $type = [string]$it.'$type'
        if ($type -like '*EllipseItem*') {
            $ec = [string]$it.EllipseColor
            if ($o -and $o.ContainsKey('EllipseColor')) { $ec = [string]$o.EllipseColor }
            $col = [System.Drawing.ColorTranslator]::FromHtml($ec)
            if ($op -lt 100) { $col = [System.Drawing.Color]::FromArgb([int](255 * $op / 100), $col.R, $col.G, $col.B) }
            $pen = New-Object System.Drawing.Pen $col, ([float]$it.EllipseThickness)
            $g.DrawEllipse($pen, $x, $y, $w, $h)
            $pen.Dispose()
        }
        elseif ($type -like '*ImageItem*') {
            # Same bytes the bundle carries, so the thumbnail shows exactly
            # what the dashboard will.
            $im = $script:DASH_IMAGES | Where-Object { $_.Meta.Name -eq [string]$it.Image } | Select-Object -First 1
            if ($im) {
                $ims = New-Object System.IO.MemoryStream (,$im.Bytes)
                $bm = [System.Drawing.Image]::FromStream($ims)
                $rot2 = 0.0
                if ($o -and $o.ContainsKey('Rotation')) { $rot2 = [double]$o.Rotation }
                elseif ($it.Contains('Rotation'))        { $rot2 = [double]$it.Rotation }
                if ($rot2 -ne 0) {
                    $g.TranslateTransform($x + $w / 2, $y + $h / 2)
                    $g.RotateTransform([float]$rot2)
                    $g.DrawImage($bm, (-$w / 2), (-$h / 2), $w, $h)
                    $g.ResetTransform()
                } else {
                    $g.DrawImage($bm, $x, $y, $w, $h)
                }
                $bm.Dispose(); $ims.Dispose()
            }
        }
        elseif ($type -like '*GradientItem*') {
            # PathGradientBrush is GDI's radial: centre colour out to the
            # surround colour at the ellipse edge, which is what the WPF
            # RadialGradientBrush does in the viewer.
            $stops = $it.Color.RadialGradientBrush.'RadialGradientBrush.GradientStops'.GradientStop
            if ($stops) {
                $ctr = [System.Drawing.ColorTranslator]::FromHtml([string]$stops[0].'@Color')
                $edge = [System.Drawing.ColorTranslator]::FromHtml([string]$stops[1].'@Color')
                $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
                $gp.AddEllipse($x, $y, $w, $h)
                $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
                $pgb.CenterColor = $ctr
                $pgb.SurroundColors = @($edge)
                $g.FillPath($pgb, $gp)
                $pgb.Dispose(); $gp.Dispose()
            }
        }
        elseif ($type -like '*RectangleItem*') {
            $fill = [string]$it.BackgroundColor
            if ($o -and $o.ContainsKey('BackgroundColor')) { $fill = [string]$o.BackgroundColor }
            # Rotation matches the live viewer: CSS transform, center pivot.
            $rot = 0.0
            if ($o -and $o.ContainsKey('Rotation')) { $rot = [double]$o.Rotation }
            if ($rot -ne 0) {
                $g.TranslateTransform($x + $w / 2, $y + $h / 2)
                $g.RotateTransform([float]$rot)
                $path = New-RoundedPath (-$w / 2) (-$h / 2) $w $h ([float]$it.BorderStyle.RadiusTopLeft) `
                    ([float]$it.BorderStyle.RadiusTopRight) ([float]$it.BorderStyle.RadiusBottomRight) `
                    ([float]$it.BorderStyle.RadiusBottomLeft)
            } else {
                $path = New-RoundedPath $x $y $w $h ([float]$it.BorderStyle.RadiusTopLeft) `
                    ([float]$it.BorderStyle.RadiusTopRight) ([float]$it.BorderStyle.RadiusBottomRight) `
                    ([float]$it.BorderStyle.RadiusBottomLeft)
            }
            $c = [System.Drawing.ColorTranslator]::FromHtml($fill)
            # Item Opacity is a multiplier on the fill in the viewer, so the
            # thumbnail has to apply it too: without this a 20%-opacity ambient
            # layer previews as a solid slab and every judgement about it is
            # made against something the user will never see.
            $op = 100.0
            if ($o -and $o.ContainsKey('Opacity')) { $op = [double]$o.Opacity }
            elseif ($it.Contains('Opacity'))       { $op = [double]$it.Opacity }
            if ($op -lt 100) {
                $c = [System.Drawing.Color]::FromArgb([int]($c.A * $op / 100.0), $c.R, $c.G, $c.B)
            }
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
    # Outside-in, matching the shipped DashRevStripOutsideIn default: the
    # thresholds and colours are the same pair-index scheme RevStrip binds,
    # so the thumbnail shows the strip a new user actually gets.
    for ($i = 0; $i -lt 16; $i++) {
        $pair = [math]::Min($i, 15 - $i)
        if ($pct -ge (50 + $pair * 6.25)) {
            $col = if ($pair -lt 4) { $GREEN } elseif ($pair -lt 6) { '#FFE8A33D' } else { $RED }
            $o["rev-seg$i"] = @{ Show = $true; BackgroundColor = $col }
        }
    }
    # The factory tab bar exactly as a fresh install shows it: order from
    # DashTabFactoryOrder, minus the tabs DashTabFactoryDisabled starts off
    # (Gains, whose box on the Drive tab covers it). Keep this in step with
    # both, or the thumbnails advertise a layout nobody gets.
    $slotNames = @('DRIVE', 'CAR FACTS', 'VISUALIZER', 'EFFECTS', 'TELE-FFB', 'PRESETS')
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

$ovDrive = PreviewChrome 78 -1   # Gains is off by default: no tab highlighted
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

$ovFacts = PreviewChrome 78 1
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
$ovScope = PreviewChrome 78 2
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
$ovDriveTab['dr-rpm']   = @{ Text = '6420 rpm' }
$ovDriveTab['dr-speed'] = @{ Text = '148 kph' }
foreach ($sl in 0, 1, 2, 3) { $ovDriveTab["d$sl-panel"] = @{ Show = $true } }
# slot 0: car facts
$ovDriveTab['d0-cf-h']      = @{ Show = $true }
$ovDriveTab['d0-cf-car']    = @{ Show = $true; Text = '1985 Sprinter Trueno' }
$ovDriveTab['d0-cf-p1']     = @{ Show = $true }
$ovDriveTab['d0-cf-p2']     = @{ Show = $true }
$ovDriveTab['d0-cf-engl']   = @{ Show = $true }
$ovDriveTab['d0-cf-eng-v']  = @{ Show = $true; Text = 'Inline 4' }
$ovDriveTab['d0-cf-eng-ch-bg'] = @{ Show = $true }
$ovDriveTab['d0-cf-eng-ch-t']  = @{ Show = $true }
$ovDriveTab['d0-cf-redl']   = @{ Show = $true }
$ovDriveTab['d0-cf-red-v']  = @{ Show = $true; Text = '7800 rpm' }
$ovDriveTab['d0-cf-rdn-bg'] = @{ Show = $true }
$ovDriveTab['d0-cf-rdn-t']  = @{ Show = $true }
$ovDriveTab['d0-cf-rup-bg'] = @{ Show = $true }
$ovDriveTab['d0-cf-rup-t']  = @{ Show = $true }
$ovDriveTab['d0-cf-info']   = @{ Show = $true; Text = 'MAX 8100   COMMUNITY' }
# slot 1: tyre temps
$ovDriveTab['d1-tt-h'] = @{ Show = $true }
$tq  = @(86, 84, 79, 108)
for ($i = 0; $i -lt 4; $i++) {
    $ovDriveTab["d1-tt$i"]   = @{ Show = $true; BackgroundColor = (TempColor $tq[$i]) }
    $ovDriveTab["d1-tt$i-v"] = @{ Show = $true; Text = ([string]$tq[$i]); TextColor = (TempTextColor $tq[$i]) }
}
# slot 2: the Visualizer in miniature, two lanes exactly like the tab.
# Geometry mirrors DriveBox: box y=228 h=212 -> inner y 238, lanes of 75
# with a 16 px gap, force line on top and haptic envelope below.
$ovDriveTab['d2-sc-h']  = @{ Show = $true }
$ovDriveTab['d2-sc-p1'] = @{ Show = $true }
$ovDriveTab['d2-sc-p2'] = @{ Show = $true }
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
$scLane = ((212 - 64) - 16) / 2
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
# the default set (lap times, wear, fuel, delta, gains, presets, friction,
# relative, radar, inputs) have no override here and correctly stay hidden
# in the thumbnail.
# slot 3: the g circle
$ovDriveTab['d3-gc-h']   = @{ Show = $true }
$ovDriveTab['d3-gc-r1']  = @{ Show = $true }
$ovDriveTab['d3-gc-r2']  = @{ Show = $true }
# Mid corner: loaded left and braking, so the dot sits up and right of
# centre under the felt-force convention the live dot uses.
$ovDriveTab['d3-gc-dot'] = @{ Show = $true; Left = 690; Top = 300 }
$ovDriveTab['d3-gc-v']   = @{ Show = $true; Text = '0.82 g' }
# Pedals and steering around the gear, on by default: throttle carrying
# the car through the corner, brake released, a touch of right lock.
$ovDriveTab['dr-ped-thr-bg'] = @{ Show = $true }
$ovDriveTab['dr-ped-thr']    = @{ Show = $true; Top = 114; Height = 304 }
$ovDriveTab['dr-ped-brk-bg'] = @{ Show = $true }
$ovDriveTab['dr-ped-brk']    = @{ Show = $true }
$ovDriveTab['dr-st-bg']      = @{ Show = $true }
$ovDriveTab['dr-st-tick']    = @{ Show = $true }
$ovDriveTab['dr-st']         = @{ Show = $true; Left = 408 }

Render-Preview $s1 $ovDrive   (Join-Path $OutDir 'TF4ALL Dash.djson.00.png')
Render-Preview $s2 $ovFacts   (Join-Path $OutDir 'TF4ALL Dash.djson.01.png')
Render-Preview $s3 $ovFx      (Join-Path $OutDir 'TF4ALL Dash.djson.02.png')
Render-Preview $s4 $ovPresets (Join-Path $OutDir 'TF4ALL Dash.djson.03.png')
Render-Preview $s5 $ovScope   (Join-Path $OutDir 'TF4ALL Dash.djson.04.png')
Render-Preview $s6 $ovModeB   (Join-Path $OutDir 'TF4ALL Dash.djson.05.png')
Render-Preview $s7 $ovDriveTab (Join-Path $OutDir 'TF4ALL Dash.djson.06.png')
# Cover art is the Drive tab: it leads the factory order and is what the
# dashboard is for. Screen 00 is Gains, which ships switched off.
Copy-Item (Join-Path $OutDir 'TF4ALL Dash.djson.06.png') (Join-Path $OutDir 'TF4ALL Dash.djson.png') -Force

$itemCount = $s1.Count + $s2.Count + $s3.Count + $s4.Count + $s5.Count + $s6.Count + $s7.Count
Write-Host "Wrote $OutDir  (items: $itemCount; drive=$($s1.Count) carfacts=$($s2.Count) effects=$($s3.Count) presets=$($s4.Count) visualizer=$($s5.Count) teleffb=$($s6.Count) drivetab=$($s7.Count); previews: main + 7 screens)"
