# Re-generate FH5 catalog entries with brand-specific EngineConfig overrides
# for known-special engines.
#
# Default mapping (already in parse_manteomax_fh5.ps1):
#   Inline → EngineConfig.Inline       (LayoutFromLegacy picks Inline{3,4,5,6})
#   Flat   → EngineConfig.Boxer        (picks Boxer4/Boxer6)
#   V cyl=6  → EngineConfig.V60        (picks V6_60Even)
#   V cyl=12 → EngineConfig.V60        (picks V12_60)
#   V cyl=8/10 → Auto                  (picks V8CrossPlane / V10_72)
#   Rotary → EngineConfig.Rotary       (picks Rotary{1..4} by cyl)
#   E      → IsElectric=true
#   Single → EngineConfig.Single
#   W      → Auto                      (cyl=12 wrongly picks V12_60; cyl=16 picks W12_W16)
#
# Refinements applied here:
#   1. V8 flat-plane cars → EngineConfig.V8FlatPlane (was Auto = V8CrossPlane).
#      Makes: Ferrari, McLaren, Maserati. Models: Lotus Esprit V8, Ford Shelby
#      GT350/GT350R, Koenigsegg V8s.
#   2. W-engine cars → EngineConfig.V90Even (so cyl=12 routes to W12_W16
#      instead of V12_60). Bentley/VW/Bugatti.
#   3. Everything else keeps the default.

param(
    [string]$InCsv  = "$PSScriptRoot\fh5_catalog.csv",
    [string]$OutTxt = "$PSScriptRoot\fh5_entries.txt"
)

$cat = Import-Csv $InCsv

function Is-V8FlatPlane($make, $model, $cyl, $eng) {
    if ($cyl -ne '8') { return $false }
    if ($eng -ne 'V') { return $false }
    $m = $make.Trim().ToLowerInvariant()
    $md = $model.Trim().ToLowerInvariant()
    # All modern Ferrari V8s (308 onward — every Ferrari V8 since ~1973 is flat-plane).
    if ($m -eq 'ferrari') { return $true }
    # All production McLaren V8s (M838T / M840T family).
    if ($m -eq 'mclaren') { return $true }
    # All Maserati V8s — M139 (Ferrari-derived F136-related) and M156 are flat-plane.
    if ($m -eq 'maserati') { return $true }
    # Lotus Esprit V8 only (Type 918 flat-plane). Other Lotus V8s don't exist.
    if ($m -eq 'lotus' -and $md -like '*esprit*') { return $true }
    # Ford Mustang Shelby GT350 / GT350R — 5.2L Voodoo (flat-plane). Not the
    # GT500 (Predator V8, cross-plane). Catch by "shelby gt350" subsstring.
    if ($m -eq 'ford' -and $md -like '*shelby gt350*') { return $true }
    # Koenigsegg V8s (Agera, Regera, Jesko, CC8S, CCR, CCX/CCXR, One:1, CC850).
    if ($m -eq 'koenigsegg') { return $true }
    # TVR Cerbera Speed 8 used AJP8 flat-plane V8 if FH5 has TVR.
    if ($m -eq 'tvr' -and $md -like '*cerbera*') { return $true }
    return $false
}

function Is-WEngine($eng, $cyl) {
    return ($eng -eq 'W')
}

function Map-Config($make, $model, $eng, $cyl, $isRotary) {
    if ($isRotary -eq 'True') { return 'EngineConfig.Rotary' }
    if ($eng -eq 'E')      { return 'EngineConfig.Auto' }   # electric handled by IsElectric
    if ($eng -eq 'Single') { return 'EngineConfig.Single' }
    if ($eng -eq 'Inline') { return 'EngineConfig.Inline' }
    if ($eng -eq 'Flat')   { return 'EngineConfig.Boxer' }
    if ($eng -eq 'Rotary') { return 'EngineConfig.Rotary' }
    if (Is-WEngine $eng $cyl) {
        # W12 / W16 — route through V90Even so cyl=12 picks W12_W16 instead
        # of the V12_60 the V60 path would give. cyl=16 ends up at W12_W16
        # either way.
        return 'EngineConfig.V90Even'
    }
    if ($eng -eq 'V') {
        if (Is-V8FlatPlane $make $model $cyl $eng) { return 'EngineConfig.V8FlatPlane' }
        switch ([int]$cyl) {
            6  { return 'EngineConfig.V60' }       # V6_60Even
            12 { return 'EngineConfig.V60' }       # V12_60
            default { return 'EngineConfig.Auto' } # V8CrossPlane (8) / V10_72 (10) via Auto
        }
    }
    return 'EngineConfig.Auto'
}

function Escape-CSharp($s) {
    if ($null -eq $s) { return '' }
    return ($s -replace '\\','\\\\') -replace '"','\"'
}

$entries = New-Object System.Text.StringBuilder
$stats = @{
    V8FlatPlaneOverrides = 0
    WEngineOverrides     = 0
    Total                = 0
}

foreach ($r in $cat | Sort-Object { [int]$_.Ordinal }) {
    $stats.Total++
    $ord = $r.Ordinal
    $cyl = $r.Cylinders
    $isEv = ($r.IsElectric -eq 'True')
    $cfg = Map-Config $r.Make $r.Model $r.EngineLayout $cyl $r.IsRotary
    if ($cfg -eq 'EngineConfig.V8FlatPlane') { $stats.V8FlatPlaneOverrides++ }
    if ($cfg -eq 'EngineConfig.V90Even')     { $stats.WEngineOverrides++ }
    $name = Escape-CSharp $r.DisplayName
    # SimHub's FH5 reader formats CarId as "Car_<ordinal>" (verified in the
    # log — "Backfilled GameName='FH5' on 1 preset(s) for car 'Car_424'").
    # Keys must match that exact form or the catalog lookup misses.
    $key = "[`"Car_$ord`"]"
    $pad = [Math]::Max(1, 16 - $key.Length)
    $line = "            $key" + (' ' * $pad)
    if ($isEv) {
        $line += "= new BuiltinCarSpec(0, electric: true, config: $cfg, displayName: `"$name`"),"
    } else {
        $line += "= new BuiltinCarSpec($cyl, electric: false, config: $cfg, displayName: `"$name`"),"
    }
    [void]$entries.AppendLine($line)
}

$entries.ToString() | Out-File $OutTxt -Encoding utf8
Write-Host "Stats:"
$stats.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-25} {1}" -f $_.Key, $_.Value) }
Write-Host "Wrote: $OutTxt"

# Quick check: show all V8 flat-plane overrides for sanity
Write-Host ""
Write-Host "V8 flat-plane overrides:" -ForegroundColor Cyan
foreach ($r in $cat | Sort-Object Make, Model) {
    $cfg = Map-Config $r.Make $r.Model $r.EngineLayout $r.Cylinders $r.IsRotary
    if ($cfg -eq 'EngineConfig.V8FlatPlane') {
        Write-Host ("  ord={0,-6} {1,-12} {2}" -f $r.Ordinal, $r.Make, $r.Model)
    }
}
Write-Host ""
Write-Host "W-engine overrides:" -ForegroundColor Cyan
foreach ($r in $cat | Sort-Object Make, Model) {
    $cfg = Map-Config $r.Make $r.Model $r.EngineLayout $r.Cylinders $r.IsRotary
    if ($cfg -eq 'EngineConfig.V90Even') {
        Write-Host ("  ord={0,-6} {1,-12} {2} cyl={3}" -f $r.Ordinal, $r.Make, $r.Model, $r.Cylinders)
    }
}
