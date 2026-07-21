# Generates the "TF4ALL Remote" DashStudio dashboard (.djson + .metadata).
# Item schemas mirror shipped dashes (RSC - Toggle Switch / MobileDash):
# TextItem / RectangleItem for visuals, transparent ButtonItem tap zones with
# TriggerAction = "TrueforcePlugin.<DashAction>". All formulas use the JS
# interpreter (Interpreter=1) with $prop(). JS string literals use double
# quotes so these PS single-quoted strings stay readable.

$ErrorActionPreference = 'Stop'
$OutDir = Join-Path $PSScriptRoot 'TF4ALL Remote'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---- palette ----
$BG      = '#FF101216'   # dashboard background
$PANEL   = '#FF1B1F27'   # info panels
$TILE    = '#FF232936'   # buttons / tiles (off state)
$TILEON  = '#FF23503A'   # toggle tile on state
$GREEN   = '#FF37D67A'
$RED     = '#FFE5484D'
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
    $items.Add((OnOverlay (New-Text 'kp-title' 0 10 800 30 20 '' $script:WHITE 1 @{
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
    for ($i = 0; $i -lt 16; $i++) {
        $x = 2 + $i * 50
        $thresh = [math]::Round(50 + $i * 3.125, 2)
        $color = if ($i -lt 8) { $script:GREEN } elseif ($i -lt 12) { '#FFE8A33D' } else { $script:RED }
        $seg = New-Rect "rev-seg$i" $x 1 46 10 $color $null 2
        # RevFlash: steady true below redline, ~7 Hz square at/above it, so
        # the whole lit strip flashes in unison as the shift cue.
        $seg.Bindings['Visible'] = BindJS 'Visible' ('return (1*$prop("' + $P + '.RpmPct"))>=' + $thresh + ' && $prop("' + $P + '.RevFlash")')
        $items.Add($seg)
    }
    $items
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
    $items.Add((OnOverlay (New-Text 'pp-title' 0 8 800 30 20 '' $script:WHITE 1 @{
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

$s1.Add((New-Text 'title' 16 8 240 36 24 'TF4ALL REMOTE' $WHITE 0 $null 'Bold'))
$s1.Add((New-Text 'wheel' 520 8 264 36 18 'WHEEL' $MUTED 2 @{
    Text      = BindJS 'Text'      ('return $prop("' + $P + '.WheelOk")?"WHEEL OK":"WHEEL OFFLINE"')
    TextColor = BindJS 'TextColor' ('return $prop("' + $P + '.WheelOk")?"' + $GREEN + '":"' + $RED + '"')
}))
$s1.Add((New-Text 'gamecar' 16 48 768 28 17 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Game")||"No game");var c=""+($prop("' + $P + '.CarName")||"");return c!=""?(g+"  -  "+c):g')
}))
$s1.Add((New-Text 'preset' 16 78 768 26 15 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?("PRESET  "+p):"PRESET  (manual tune)"')
}))

# Master gain (left column); the big value is a tap zone for exact entry
$s1.Add((New-Rect 'mg-panel' 16 116 376 240 $PANEL))
$s1.Add((New-Text 'mg-label' 32 128 344 26 16 'MASTER GAIN  (tap value to type)' $MUTED 0))
$s1.Add((New-Text 'mg-value' 32 156 344 96 64 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return (1*$prop("' + $P + '.MasterGain")).toFixed(2)')
} 'Bold'))
$s1.Add((New-Button 'mg-value-tap' 32 156 344 96 'DashMasterGainOpen'))
StepperTiles 'mg' 32 264 344 76 'DashMasterGainDown' 'DashMasterGainUp' | ForEach-Object { $s1.Add($_) }

# Audio gain (right column); same tap-to-type value
$s1.Add((New-Rect 'ag-panel' 408 116 376 240 $PANEL))
$s1.Add((New-Text 'ag-label' 424 128 344 26 16 '' $MUTED 0 @{
    Text = BindJS 'Text' ('return "AUDIO GAIN  "+($prop("' + $P + '.Fx.Audio.On")?"(ON)":"(OFF)")+"  (tap value to type)"')
}))
$s1.Add((New-Text 'ag-value' 424 156 344 96 64 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return (1*$prop("' + $P + '.AudioGain")).toFixed(2)')
} 'Bold'))
$s1.Add((New-Button 'ag-value-tap' 424 156 344 96 'DashAudioGainOpen'))
StepperTiles 'ag' 424 264 344 76 'DashAudioGainDown' 'DashAudioGainUp' | ForEach-Object { $s1.Add($_) }

# Bottom toggles: plugin on/off + audio on/off
$s1.Add((New-Rect 'plug-bg' 16 388 376 72 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.PluginOn")?"' + $TILEON + '":"' + $TILE + '"')
}))
$s1.Add((New-Text 'plug-t' 16 388 376 72 22 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.PluginOn")?"PLUGIN ON":"PLUGIN OFF"')
} 'Bold'))
$s1.Add((New-Button 'plug-btn' 16 388 376 72 'DashPluginToggle'))
$s1.Add((New-Rect 'aud-bg' 408 388 376 72 $TILE @{
    BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $P + '.Fx.Audio.On")?"' + $TILEON + '":"' + $TILE + '"')
}))
$s1.Add((New-Text 'aud-t' 408 388 376 72 22 '' $WHITE 1 @{
    Text = BindJS 'Text' ('return $prop("' + $P + '.Fx.Audio.On")?"AUDIO HAPTICS ON":"AUDIO HAPTICS OFF"')
} 'Bold'))
$s1.Add((New-Button 'aud-btn' 408 388 376 72 'DashFxAudioToggle'))

KeypadOverlay $P | ForEach-Object { $s1.Add($_) }
ToastBar $P | ForEach-Object { $s1.Add($_) }
RevStrip $P | ForEach-Object { $s1.Add($_) }

# =====================================================================
# Screen 2: CAR FACTS (+ layout picker overlay + redline keypad overlay)
# =====================================================================
$s2 = [System.Collections.Generic.List[object]]::new()

$s2.Add((New-Text 'cf-title' 16 8 200 36 24 'CAR FACTS' $WHITE 0 $null 'Bold'))
$s2.Add((New-Text 'cf-car' 224 8 560 36 18 '' $MUTED 2 @{
    Text = BindJS 'Text' ('return ""+($prop("' + $P + '.CarName")||"No car detected")')
}))

# Engine row: tap the value to open the layout picker
$s2.Add((New-Rect 'cf-eng-panel' 16 60 768 108 $PANEL))
$s2.Add((New-Text 'cf-eng-label' 32 70 400 24 15 'ENGINE LAYOUT  (tap to change)' $MUTED 0))
$s2.Add((New-Text 'cf-eng-value' 32 96 600 60 34 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var s=""+($prop("' + $P + '.EngineLayoutSource")||"");var l=""+($prop("' + $P + '.EngineLayout")||"Auto");return s!=""?(l+"  ("+s+")"):l')
} 'Bold'))
$s2.Add((New-Rect 'cf-eng-hint' 648 84 120 60 $TILE))
$s2.Add((New-Text 'cf-eng-hint-t' 648 84 120 60 17 'CHANGE' $WHITE 1))
$s2.Add((New-Button 'cf-eng-btn' 16 60 768 108 'DashEngineLayoutOpen'))

# Redline row: tap value = keypad; +/- 50 steppers on the right
$s2.Add((New-Rect 'cf-rl-panel' 16 180 768 120 $PANEL))
$s2.Add((New-Text 'cf-rl-label' 32 190 400 24 15 'REDLINE  (tap value to type it)' $MUTED 0))
$s2.Add((New-Text 'cf-rl-value' 32 218 360 68 44 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var r=1*$prop("' + $P + '.Redline");return r>0?(r+" rpm"):"not set"')
} 'Bold'))
$s2.Add((New-Button 'cf-rl-open' 16 180 400 120 'DashRedlineOpen'))
$s2.Add((New-Rect 'cf-rl-dn-bg' 432 208 160 72 $TILE))
$s2.Add((New-Text 'cf-rl-dn-t' 432 208 160 72 24 '-50' $WHITE 1 $null 'Bold'))
$s2.Add((New-Button 'cf-rl-dn' 432 208 160 72 'DashRedlineDown'))
$s2.Add((New-Rect 'cf-rl-up-bg' 608 208 160 72 $TILE))
$s2.Add((New-Text 'cf-rl-up-t' 608 208 160 72 24 '+50' $WHITE 1 $null 'Bold'))
$s2.Add((New-Button 'cf-rl-up' 608 208 160 72 'DashRedlineUp'))

$s2.Add((New-Text 'cf-info' 32 316 736 28 16 '' $MUTED 0 @{
    Text = BindJS 'Text' ('var m=1*$prop("' + $P + '.MaxRpm");var s=""+($prop("' + $P + '.RedlineSource")||"");var t=m>0?("MAX RPM  "+m):"";if(s!=""&&s!="none"){t+=(t!=""?"      ":"")+"REDLINE SOURCE  "+s}return t')
}))
$s2.Add((New-Text 'cf-note' 32 430 736 30 14 'Edits save to this car and apply instantly. Sharing follows your community settings.' $GRAY 0))

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
$s2.Add((OnOverlay (New-Rect 'lp-backdrop' 0 0 800 480 $BACKDROP $null 0) 'layout'))
$s2.Add((OnOverlay (New-Text 'lp-title' 0 6 800 32 20 'ENGINE LAYOUT' $WHITE 1 $null 'Bold') 'layout'))
for ($i = 0; $i -lt $layouts.Count; $i++) {
    $enum = $layouts[$i][0]; $label = $layouts[$i][1]
    $c = $i % 4; $r = [math]::Floor($i / 4)
    $x = 8 + $c * 198; $y = 44 + $r * 56
    $bgBind = @{ BackgroundColor = BindJS 'BackgroundColor' ('return (""+$prop("' + $P + '.EnginePin"))=="' + $enum + '"?"' + $TILEON + '":"' + $TILE + '"') }
    $s2.Add((OnOverlay (New-Rect  "lp-$enum-bg" $x $y 190 50 $TILE $bgBind) 'layout'))
    $s2.Add((OnOverlay (New-Text  "lp-$enum-t"  $x $y 190 50 16 $label $WHITE 1) 'layout'))
    $s2.Add((OnOverlay (New-Button "lp-$enum"   $x $y 190 50 "DashEngineLayoutSet_$enum") 'layout'))
}
# cancel occupies the last free grid slot (28 layouts fill 7 rows exactly, so add a bar)
$s2.Add((OnOverlay (New-Rect 'lp-cancel-bg' 8 438 782 36 $TILE) 'layout'))
$s2.Add((OnOverlay (New-Text 'lp-cancel-t' 8 438 782 36 16 'CANCEL' $RED 1 $null 'Bold') 'layout'))
$s2.Add((OnOverlay (New-Button 'lp-cancel' 8 438 782 36 'DashEngineLayoutClose') 'layout'))

# ---- overlay: shared keypad (redline entry opens it via DashRedlineOpen) ----
KeypadOverlay $P | ForEach-Object { $s2.Add($_) }
ToastBar $P | ForEach-Object { $s2.Add($_) }
RevStrip $P | ForEach-Object { $s2.Add($_) }

# =====================================================================
# Screen 3: EFFECTS (14 rows in 2 columns: toggle tile + gain readout + steppers)
# =====================================================================
$s3 = [System.Collections.Generic.List[object]]::new()
$s3.Add((New-Text 'fx-title' 16 4 400 34 22 'EFFECTS' $WHITE 0 $null 'Bold'))

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
    @('Airborne',   'Airborne duck', $false),
    @('Audio',      'Audio haptics', $true)
)
# Row layout: name tile (tap = toggle), then a [-] value [+] cluster so the
# steppers visually flank the value they change (a trailing -/+ pair read as
# ambiguous between neighboring rows on a real screen). Tapping the value
# opens the keypad for exact entry. Wide gutter between the two columns.
for ($i = 0; $i -lt $effects.Count; $i++) {
    $key = $effects[$i][0]; $label = $effects[$i][1]; $hasGain = $effects[$i][2]
    $col = [math]::Floor($i / 7); $row = $i % 7
    $x = 10 + $col * 404; $y = 44 + $row * 62
    # audio routes to its dedicated actions (peer voice, not a TelemetryEffect)
    $tgl  = if ($key -eq 'Audio') { 'DashFxAudioToggle' } else { "DashFx${key}Toggle" }
    $up   = if ($key -eq 'Audio') { 'DashAudioGainUp' }   else { "DashFx${key}GainUp" }
    $dn   = if ($key -eq 'Audio') { 'DashAudioGainDown' } else { "DashFx${key}GainDown" }
    $open = if ($key -eq 'Audio') { 'DashAudioGainOpen' } else { "DashFx${key}GainOpen" }
    $onProp = $P + '.Fx.' + $key + '.On'
    $s3.Add((New-Rect "fx-$key-bg" $x $y 170 56 $TILE @{
        BackgroundColor = BindJS 'BackgroundColor' ('return $prop("' + $onProp + '")?"' + $TILEON + '":"' + $TILE + '"')
    }))
    $s3.Add((New-Text "fx-$key-t" ($x + 8) $y 156 56 16 $label $WHITE 0 @{
        TextColor = BindJS 'TextColor' ('return $prop("' + $onProp + '")?"' + $WHITE + '":"' + $GRAY + '"')
    }))
    $s3.Add((New-Button "fx-$key-tgl" $x $y 170 56 $tgl))
    if (-not $hasGain) { continue }
    $s3.Add((New-Rect  "fx-$key-dn-bg" ($x + 176) $y 50 56 $TILE))
    $s3.Add((New-Text  "fx-$key-dn-t"  ($x + 176) $y 50 56 26 '-' $WHITE 1 $null 'Bold'))
    $s3.Add((New-Button "fx-$key-dn"   ($x + 176) $y 50 56 $dn))
    $s3.Add((New-Rect "fx-$key-gain-bg" ($x + 230) $y 82 56 $PANEL $null 0))
    $s3.Add((New-Text "fx-$key-gain" ($x + 230) $y 82 56 17 '' $WHITE 1 @{
        Text = BindJS 'Text' ('return (1*$prop("' + $P + '.Fx.' + $key + '.Gain")).toFixed(3)')
    }))
    $s3.Add((New-Button "fx-$key-gain-tap" ($x + 230) $y 82 56 $open))
    $s3.Add((New-Rect  "fx-$key-up-bg" ($x + 316) $y 50 56 $TILE))
    $s3.Add((New-Text  "fx-$key-up-t"  ($x + 316) $y 50 56 26 '+' $WHITE 1 $null 'Bold'))
    $s3.Add((New-Button "fx-$key-up"   ($x + 316) $y 50 56 $up))
}
$s3.Add((New-Text 'fx-hint' 420 8 370 30 13 'Tap name = on/off. Tap value = type exact.' $GRAY 2))

KeypadOverlay $P | ForEach-Object { $s3.Add($_) }
ToastBar $P | ForEach-Object { $s3.Add($_) }
RevStrip $P | ForEach-Object { $s3.Add($_) }

# =====================================================================
# Screen 4: PRESETS (game preset + car preset, picker overlay)
# =====================================================================
$s4 = [System.Collections.Generic.List[object]]::new()
$s4.Add((New-Text 'pr-title' 16 8 300 36 24 'PRESETS' $WHITE 0 $null 'Bold'))
$s4.Add((New-Text 'pr-car' 320 8 464 36 16 '' $MUTED 2 @{
    Text = BindJS 'Text' ('var g=""+($prop("' + $P + '.Game")||"No game");var c=""+($prop("' + $P + '.CarName")||"");return c!=""?(g+"  -  "+c):g')
}))

$s4.Add((New-Rect 'pr-game-panel' 16 64 768 150 $PANEL))
$s4.Add((New-Text 'pr-game-label' 32 76 500 24 15 'GAME PRESET  (applies to the whole game, tap to change)' $MUTED 0))
$s4.Add((New-Text 'pr-game-value' 32 104 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.PresetName")||"");return p!=""?p:"(manual tune)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-game-hint' 648 96 120 84 $TILE))
$s4.Add((New-Text 'pr-game-hint-t' 648 96 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-game-btn' 16 64 768 150 'DashPresetOpenGame'))

$s4.Add((New-Rect 'pr-carp-panel' 16 228 768 150 $PANEL))
$s4.Add((New-Text 'pr-carp-label' 32 240 500 24 15 'CAR PRESET  (this car only, tap to change)' $MUTED 0))
$s4.Add((New-Text 'pr-carp-value' 32 268 600 80 30 '' $WHITE 0 @{
    Text = BindJS 'Text' ('var p=""+($prop("' + $P + '.CarPresetName")||"");return p!=""?p:"(none saved for this car)"')
} 'Bold'))
$s4.Add((New-Rect 'pr-carp-hint' 648 260 120 84 $TILE))
$s4.Add((New-Text 'pr-carp-hint-t' 648 260 120 84 17 'CHANGE' $WHITE 1))
$s4.Add((New-Button 'pr-carp-btn' 16 228 768 150 'DashPresetOpenCar'))

$s4.Add((New-Text 'pr-note' 32 420 736 40 14 'Applying a preset replaces your current unsaved tuning, same as applying it in the desktop UI.' $GRAY 0))

PresetOverlay $P | ForEach-Object { $s4.Add($_) }
ToastBar $P | ForEach-Object { $s4.Add($_) }
RevStrip $P | ForEach-Object { $s4.Add($_) }

# =====================================================================
# Assemble document
# =====================================================================
function New-Screen([string]$name, $items) {
    [ordered]@{
        Name = $name; InGameScreen = $true; IdleScreen = $true; PitScreen = $false
        ScreenId = [guid]::NewGuid().ToString()
        AllowOverlays = $true; IsForegroundLayer = $false; IsOverlayLayer = $false
        OverlayTriggerExpression = [ordered]@{ Expression = '' }
        ScreenEnabledExpression  = [ordered]@{ Expression = '' }
        OverlayMaxDuration = 0; OverlayMinDuration = 0; IsBackgroundLayer = $false
        BackgroundColor = $CLEAR
        Items = @($items)
    }
}

$meta = [ordered]@{
    Category = 'TF4ALL'; Title = 'TF4ALL Remote'
    Description = 'Remote control for Trueforce For All: gains, effect toggles and car facts from a phone or tablet'
    Author = 'Mhytee'
    ScreenCount = 4.0
    InGameScreensIndexs = @(0, 1, 2, 3)
    IdleScreensIndexs = @(0, 1, 2, 3)
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
        (New-Screen 'Drive' $s1),
        (New-Screen 'Car facts' $s2),
        (New-Screen 'Effects' $s3),
        (New-Screen 'Presets' $s4)
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
[IO.File]::WriteAllText((Join-Path $OutDir 'TF4ALL Remote.djson'), $json, [Text.UTF8Encoding]::new($false))
$metaJson = $meta | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $OutDir 'TF4ALL Remote.djson.metadata'), $metaJson, [Text.UTF8Encoding]::new($false))

$itemCount = $s1.Count + $s2.Count + $s3.Count + $s4.Count
Write-Host "Wrote $OutDir  (items: $itemCount; drive=$($s1.Count) carfacts=$($s2.Count) effects=$($s3.Count) presets=$($s4.Count))"
