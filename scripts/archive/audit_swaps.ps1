# Audit candidate engine-swap mismatches in BuiltinCarCylinders.
#
# Strategy: re-run the heuristic detector against ui_car.json for every
# car in the library, separately recording (1) what the bake currently
# claims, and (2) what the description text strongly suggests via swap
# language or explicit engine-name mentions. Disagreements = swap-likely
# candidates the user should review.
#
# Output: scripts/swap_candidates.csv with columns
#   carId, baked_cyl, suggested_cyl, suggested_source, evidence

param(
    [string]$AcRoot   = "D:\SteamLibrary\steamapps\common\assettocorsa\content\cars",
    [string]$BakePath = "$PSScriptRoot/../src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs",
    [string]$OutPath  = "$PSScriptRoot/swap_candidates.csv"
)

$root = $AcRoot

# Re-import the baked map by parsing BuiltinCarCylinders.cs
$src = Get-Content -Raw -LiteralPath $BakePath
$baked = @{}
foreach ($m in [regex]::Matches($src, '\["([^"]+)"\]\s*=\s*new BuiltinCarSpec\((\d+)\)')) {
    $baked[$m.Groups[1].Value] = [int]$m.Groups[2].Value
}
Write-Host "Loaded $($baked.Count) baked entries"

# Engine codename → cylinder lookup (mirrors CarCylinderResolver). When a
# codename is explicitly named in description text, we trust it strongly —
# that's the engine the modder is calling out.
$codenameTable = @(
    @{ Pattern='\b2JZ(?:[-\s]?GTE|[-\s]?GE)?\b';      Cyl=6 },
    @{ Pattern='\b1JZ(?:[-\s]?GTE|[-\s]?GE)?\b';      Cyl=6 },
    @{ Pattern='\bRB2[567](?:DET{1,2})?\b';           Cyl=6 },
    @{ Pattern='\bRB30\b';                            Cyl=6 },
    @{ Pattern='\b7M[-\s]?GTE\b';                     Cyl=6 },
    @{ Pattern='\b4[AG][-\s]?GE\b';                   Cyl=4 },
    @{ Pattern='\bSR20(?:DET|VE|DE)?\b';              Cyl=4 },
    @{ Pattern='\bCA18(?:DET|DE)?\b';                 Cyl=4 },
    @{ Pattern='\bKA24(?:DE|E)?\b';                   Cyl=4 },
    @{ Pattern='\b4G6[34]\b';                         Cyl=4 },
    @{ Pattern='\bK20[ACZ]?\b';                       Cyl=4 },
    @{ Pattern='\bK24[AZ]?\b';                        Cyl=4 },
    @{ Pattern='\bF20C\b';                            Cyl=4 },
    @{ Pattern='\bH22A\b';                            Cyl=4 },
    @{ Pattern='\b3SGTE\b';                           Cyl=4 },
    @{ Pattern='\bEJ20[57]?\b';                       Cyl=4 },
    @{ Pattern='\bEJ25[57]?\b';                       Cyl=4 },
    @{ Pattern='\bFA20\b';                            Cyl=4 },
    @{ Pattern='\bLS[1-9]X?\b';                       Cyl=8 },
    @{ Pattern='\bLT[1-5]\b';                         Cyl=8 },
    @{ Pattern='\bCoyote\b';                          Cyl=8 },
    @{ Pattern='\bHEMI\b';                            Cyl=8 },
    @{ Pattern='\bHellcat\b';                         Cyl=8 },
    @{ Pattern='\b1UZ(?:[-\s]?FE)?\b';                Cyl=8 },
    @{ Pattern='\b2UZ(?:[-\s]?FE)?\b';                Cyl=8 },
    @{ Pattern='\bVQ3[57]\b';                         Cyl=6 },
    @{ Pattern='\bVR38\b';                            Cyl=6 },
    @{ Pattern='\b13B(?:[-\s]?(?:REW|MSP|T))?\b';     Cyl=4 },
    @{ Pattern='\bRenesis\b';                         Cyl=4 },
    @{ Pattern='\b20B(?:[-\s]?REW)?\b';               Cyl=6 },
    @{ Pattern='\b26B\b';                             Cyl=8 }
)

$candidates = @()
$scanned = 0
$swapWordHits = 0

foreach ($carId in $baked.Keys | Sort-Object) {
    $scanned++
    $bakedCyl = $baked[$carId]
    $uiPath = Join-Path $root "$carId\ui\ui_car.json"
    if (-not (Test-Path $uiPath)) { continue }

    try {
        $raw = Get-Content -Raw -LiteralPath $uiPath
        if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
        $ui = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch { continue }

    $desc = "$($ui.description)"
    if ([string]::IsNullOrEmpty($desc)) { continue }
    # Strip HTML entities for cleaner matching
    $desc = $desc -replace '&[a-z]+;', ' '

    $hasSwapWord = $desc -match '(?i)\b(swap|swapped|swapping|engine swap|motor swap)\b'
    if ($hasSwapWord) { $swapWordHits++ }

    # Collect every engine codename mentioned in the description
    $foundCodenames = @()
    foreach ($cn in $codenameTable) {
        $m = [regex]::Match($desc, $cn.Pattern)
        if ($m.Success) {
            $foundCodenames += [pscustomobject]@{
                Name = $m.Value
                Cyl  = $cn.Cyl
            }
        }
    }

    if ($foundCodenames.Count -eq 0) { continue }

    # If any mentioned codename disagrees with the bake, that's a candidate
    $disagreements = $foundCodenames | Where-Object { $_.Cyl -ne $bakedCyl }
    if ($disagreements.Count -gt 0) {
        # The "right" answer is whichever codename was named most explicitly
        # in the description. For simplicity take the first disagreement.
        $first = $disagreements[0]
        $names = ($foundCodenames | ForEach-Object { "$($_.Name)($($_.Cyl))" }) -join ", "
        $candidates += [pscustomobject]@{
            CarId         = $carId
            BakedCyl      = $bakedCyl
            SuggestedCyl  = $first.Cyl
            HasSwapWord   = $hasSwapWord
            Evidence      = $names
        }
    }
}

Write-Host ""
Write-Host "Scanned: $scanned baked entries"
Write-Host "Cars with 'swap' word in description: $swapWordHits"
Write-Host ("Candidate disagreements: {0}" -f $candidates.Count) -ForegroundColor Yellow
Write-Host ""

# Sort: those with explicit swap word first (highest confidence flag)
$candidates = $candidates | Sort-Object @{Expression='HasSwapWord';Descending=$true}, CarId

$candidates | Format-Table CarId, BakedCyl, SuggestedCyl, HasSwapWord, Evidence -AutoSize

$candidates | Export-Csv -LiteralPath $OutPath -NoTypeInformation -Encoding utf8
Write-Host ""
Write-Host "Wrote: scripts/swap_candidates.csv"
