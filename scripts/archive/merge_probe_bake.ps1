# Merge probe_ac_cylinders.ps1 output (ac_engine_bake.cs.txt) into
# BuiltinCarCylinders.cs, preserving the manually-curated Kunos section
# and any old heuristic-derived entries the new probe didn't redetect.
#
# Strategy:
#   1. Read existing bake. Split into:
#        - prelude (everything up to and including the last Kunos line)
#        - heuristic-derived block (sections 'Heuristic-derived: <source>')
#        - postlude (closing brackets)
#   2. Read probe fragment. Each line is one car entry with cyl + EngineConfig.
#   3. Build a dictionary of existing heuristic entries keyed by carId.
#   4. For each probe entry: write it (probe wins on overlap).
#      For each old-only entry (not in probe): write it (preserved).
#   5. Re-emit grouped by Source label (chassis, codename, etc.).

param(
    [string]$BakePath  = "$PSScriptRoot/../src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs",
    [string]$ProbePath = "$PSScriptRoot/ac_engine_bake.cs.txt",
    [string]$CsvPath   = "$PSScriptRoot/ac_cylinders_probe.csv"
)

if (-not (Test-Path $BakePath))  { Write-Host "Bake not found: $BakePath" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $ProbePath)) { Write-Host "Probe fragment not found: $ProbePath. Run probe with -EmitBakeFile first." -ForegroundColor Red; exit 1 }

$bakeLines = Get-Content -LiteralPath $BakePath -Encoding UTF8

# Find boundaries.
# - Heuristic block starts at the "Heuristic-derived (auto-baked..." comment.
# - Closes at the line that closes the AssettoCorsa dictionary (a single "};"
#   at the indentation of the inner dictionary).
$heurStart = -1; $heurEnd = -1
for ($i = 0; $i -lt $bakeLines.Count; $i++) {
    if ($heurStart -lt 0 -and $bakeLines[$i] -match '===== Heuristic-derived') { $heurStart = $i }
    if ($heurStart -ge 0 -and $bakeLines[$i] -match '^\s*\}\s*;\s*$' -and $heurEnd -lt 0) { $heurEnd = $i }
}
if ($heurStart -lt 0 -or $heurEnd -lt 0) {
    Write-Host "Couldn't locate heuristic-derived block boundaries." -ForegroundColor Red
    exit 1
}
Write-Host "Heuristic block: lines $($heurStart+1)..$($heurEnd) (inclusive of closing brace)" -ForegroundColor Cyan

$prelude  = $bakeLines[0..($heurStart-1)]
$postlude = $bakeLines[$heurEnd..($bakeLines.Count-1)]

# Parse each entry line: ["carId"] = new BuiltinCarSpec(N[, EngineConfig.X]),
$entryRegex = '^\s*\["(?<id>[^"]+)"\]\s*=\s*new BuiltinCarSpec\((?<cyl>\d+)(?:\s*,\s*EngineConfig\.(?<cfg>\w+))?(?:\s*,\s*true)?\s*\),\s*(?:\/\/.*)?$'

# Old heuristic entries.
$old = @{}
for ($i = $heurStart; $i -lt $heurEnd; $i++) {
    if ($bakeLines[$i] -match $entryRegex) {
        $old[$Matches['id']] = @{
            Cyl = [int]$Matches['cyl']
            Cfg = $Matches['cfg']  # may be empty
            Line = $bakeLines[$i]
        }
    }
}
Write-Host "Old heuristic entries: $($old.Count)" -ForegroundColor Cyan

# Probe entries grouped by source. We use the CSV (richer source info) instead
# of parsing the bake fragment so we can re-categorize.
$probeRows = Import-Csv -LiteralPath $CsvPath -Encoding utf8 |
    Where-Object { $_.Cylinders -and [int]$_.Cylinders -gt 0 }
$probe = @{}
foreach ($r in $probeRows) {
    $probe[$r.CarId] = @{
        Cyl    = [int]$r.Cylinders
        Cfg    = $r.EngineConfig
        Source = ($r.Source -split ':')[0]   # "tag", "chassis", etc.
    }
}
Write-Host "Probe entries: $($probe.Count)" -ForegroundColor Cyan

# Check Kunos overlap: Kunos cars in the prelude shouldn't be re-emitted
# from probe (manual bakes are preserved). Build a Kunos-name set so we
# can skip them if probe redetected.
$kunosIds = @{}
foreach ($line in $prelude) {
    if ($line -match $entryRegex) { $kunosIds[$Matches['id']] = $true }
}
Write-Host "Kunos / pre-heuristic entries (prelude): $($kunosIds.Count)" -ForegroundColor Cyan

# Build the merged set: probe entries (winning) + old-only entries.
$merged = @{}
foreach ($id in $probe.Keys) {
    if ($kunosIds.ContainsKey($id)) { continue }   # don't shadow manual bake
    $merged[$id] = $probe[$id]
}
$preservedFromOld = 0
foreach ($id in $old.Keys) {
    if ($merged.ContainsKey($id)) { continue }
    if ($kunosIds.ContainsKey($id)) { continue }
    # Old-only: keep as-is. Source falls back to "old-bake" so we can group
    # it visibly in the output.
    $merged[$id] = @{
        Cyl    = $old[$id].Cyl
        Cfg    = $old[$id].Cfg
        Source = 'old-bake'
    }
    $preservedFromOld++
}
Write-Host "Merged total: $($merged.Count)  ($preservedFromOld preserved from old-only)" -ForegroundColor Green

# Group merged entries by source for emit. Preserve canonical order.
$sourceOrder = @('cylword', 'tag', 'codename', 'desc', 'desc-word', 'chassis', 'old-bake')
$grouped = @{}
foreach ($id in ($merged.Keys | Sort-Object)) {
    $entry = $merged[$id]
    $src = $entry.Source
    if (-not $grouped.ContainsKey($src)) { $grouped[$src] = New-Object 'System.Collections.Generic.List[object]' }
    [void]$grouped[$src].Add(@{ Id = $id; Cyl = $entry.Cyl; Cfg = $entry.Cfg })
}

# Emit the new heuristic block.
$out = New-Object 'System.Collections.Generic.List[string]'
[void]$out.Add('            // ===== Heuristic-derived (auto-baked from probe + preserved old-only) =====')
[void]$out.Add('            // Cascade: cylword > tag > codename > desc > chassis. EngineConfig comes')
[void]$out.Add('            // from the same probe pass; "old-bake" preserves entries no longer detected')
[void]$out.Add('            // by the current heuristic so we do not lose coverage on bake refresh.')
[void]$out.Add('')

foreach ($src in $sourceOrder) {
    if (-not $grouped.ContainsKey($src)) { continue }
    $entries = $grouped[$src]
    [void]$out.Add(("            // ----- {0} ({1} entries) -----" -f $src, $entries.Count))
    foreach ($e in ($entries | Sort-Object { $_.Id })) {
        $padded = '"' + $e.Id + '"'
        $padding = ' ' * [math]::Max(1, 50 - $padded.Length)
        if ($e.Cfg) {
            [void]$out.Add(("            [{0}]{1}= new BuiltinCarSpec({2}, EngineConfig.{3})," -f $padded, $padding, $e.Cyl, $e.Cfg))
        } else {
            [void]$out.Add(("            [{0}]{1}= new BuiltinCarSpec({2})," -f $padded, $padding, $e.Cyl))
        }
    }
    [void]$out.Add('')
}

# Stitch back together.
$final = New-Object 'System.Collections.Generic.List[string]'
foreach ($l in $prelude)  { [void]$final.Add($l) }
foreach ($l in $out)      { [void]$final.Add($l) }
foreach ($l in $postlude) { [void]$final.Add($l) }

Set-Content -LiteralPath $BakePath -Value $final -Encoding UTF8
Write-Host "Wrote merged bake: $BakePath" -ForegroundColor Green

# Summary
$probeCount = $probe.Keys | Where-Object { -not $kunosIds.ContainsKey($_) } | Measure-Object | Select-Object -ExpandProperty Count
Write-Host ""
Write-Host "Heuristic-derived breakdown:" -ForegroundColor Cyan
foreach ($src in $sourceOrder) {
    if ($grouped.ContainsKey($src)) {
        Write-Host ("  {0,-12} {1}" -f $src, $grouped[$src].Count)
    }
}
$cfgCount = ($merged.Values | Where-Object { $_.Cfg } | Measure-Object).Count
Write-Host ""
Write-Host ("Total: {0} entries, {1} with explicit EngineConfig ({2:p1})" -f $merged.Count, $cfgCount, ($cfgCount / [math]::Max($merged.Count, 1)))
