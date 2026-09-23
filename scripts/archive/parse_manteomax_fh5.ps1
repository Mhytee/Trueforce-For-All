# Parse ManteoMax's Forza Horizon 5 spreadsheet → CSV (ordinal, year, make, model, cyl, isElectric, isRotary)
#
# Source: ManteoMax's Forza HORIZON 5 Spreadsheets.xlsx (https://www.manteomax.com/)
# Credit in README required.
#
# Schema in the sheet's "Cars" tab (sheet1.xml):
#   D = Ordinal       (the int the Forza UDP packet carries as CarOrdinal)
#   E = Year
#   F = Make
#   G = Model
#   AF = Engine        (V / Inline / Flat / E / Rotary / W / Single)
#   AG = Cylinders     (numeric N.0, "E" for electric, "2R"/"3R"/"4R" for rotaries)
#
# Output rules:
#   electric → IsElectric=true, Cylinders=0 (sentinel; resolver falls through to Cylinders setting)
#   rotary "NR" → Cylinders = N × 2 (effective cyl for our firing-freq math)
#   regular N.0 → Cylinders = (int)N
# Rows without an Ordinal are skipped (unlisted / DLC-only without UDP exposure).

param(
    [string]$Xlsx = "C:\Users\mhyte\Downloads\ManteoMax's Forza HORIZON 5 Spreadsheets.xlsx",
    [string]$WorkDir = "C:\Users\mhyte\AppData\Local\Temp\fh5_xlsx",
    [string]$OutCsv = "$PSScriptRoot\fh5_catalog.csv"
)

# Extract xlsx as zip
Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Copy-Item -LiteralPath $Xlsx -Destination "$WorkDir\book.zip"
Expand-Archive -Path "$WorkDir\book.zip" -DestinationPath $WorkDir -Force

$sstXml = [xml](Get-Content -Raw "$WorkDir\xl\sharedStrings.xml")
$ns = New-Object System.Xml.XmlNamespaceManager $sstXml.NameTable
$ns.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
$strings = @($sstXml.SelectNodes('//x:si', $ns) | ForEach-Object { $_.InnerText })

$sheetXml = [xml](Get-Content -Raw "$WorkDir\xl\worksheets\sheet1.xml")
$nsm = New-Object System.Xml.XmlNamespaceManager $sheetXml.NameTable
$nsm.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')

function Get-Cell($row, $colLetter) {
    $cellRef = "$colLetter$($row.r)"
    $c = $row.SelectSingleNode("x:c[@r='$cellRef']", $nsm)
    if (-not $c) { return $null }
    $vNode = $c.SelectSingleNode('x:v', $nsm)
    if (-not $vNode) { return $null }
    if ($c.t -eq 's') { return $strings[[int]$vNode.InnerText] }
    return $vNode.InnerText
}

$rows = $sheetXml.SelectNodes("//x:sheetData/x:row", $nsm)
$entries = New-Object System.Collections.ArrayList
$stats = @{ Total=0; KeptCombustion=0; KeptElectric=0; KeptRotary=0; SkippedNoOrdinal=0; SkippedNoCyl=0; SkippedDuplicate=0 }
$seenOrds = @{}

foreach ($row in $rows) {
    if ($row.r -eq '1') { continue }   # header
    $stats.Total++

    $ordStr = Get-Cell $row 'D'
    if ([string]::IsNullOrWhiteSpace($ordStr)) { $stats.SkippedNoOrdinal++; continue }
    $ord = [int][double]$ordStr  # value is stored as float "2740.0"
    if ($seenOrds.ContainsKey($ord)) { $stats.SkippedDuplicate++; continue }
    $seenOrds[$ord] = $true

    $year = Get-Cell $row 'E'
    $make = (Get-Cell $row 'F') -as [string]
    $model = (Get-Cell $row 'G') -as [string]
    $eng  = (Get-Cell $row 'AF') -as [string]
    $cyl  = (Get-Cell $row 'AG') -as [string]

    if ([string]::IsNullOrWhiteSpace($cyl)) { $stats.SkippedNoCyl++; continue }
    $cyl = $cyl.Trim()

    $isElectric = $false
    $isRotary = $false
    $cylInt = 0

    if ($cyl -eq 'E') {
        $isElectric = $true
        $cylInt = 0   # sentinel — resolver leaves AutoCylinders null for EVs
    }
    elseif ($cyl -match '^(\d+)R$') {
        $isRotary = $true
        $rotors = [int]$matches[1]
        $cylInt = $rotors * 2   # effective cyl for firing-freq math
    }
    elseif ($cyl -match '^(\d+(?:\.\d+)?)$') {
        $cylInt = [int][double]$matches[1]
    }
    else {
        # Unknown format — skip
        continue
    }

    if (-not $isElectric -and ($cylInt -lt 1 -or $cylInt -gt 16)) { continue }

    if ($isElectric) { $stats.KeptElectric++ }
    elseif ($isRotary) { $stats.KeptRotary++ }
    else { $stats.KeptCombustion++ }

    # Build a display name: "<Year> <Make> <Model>" trimmed
    $yearInt = if ($year) { [int][double]$year } else { 0 }
    $displayName = if ($yearInt -gt 0) { "$yearInt $make $model" } else { "$make $model" }
    $displayName = ($displayName -replace '\s+', ' ').Trim()

    [void]$entries.Add([pscustomobject]@{
        Ordinal     = $ord
        Year        = $yearInt
        Make        = $make
        Model       = $model
        Cylinders   = $cylInt
        IsElectric  = $isElectric
        IsRotary    = $isRotary
        EngineLayout = $eng
        DisplayName = $displayName
    })
}

Write-Host "Parsed FH5 catalog:" -ForegroundColor Cyan
$stats.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-22} {1}" -f $_.Key, $_.Value) }
Write-Host ("Final entries: {0}" -f $entries.Count)

$entries | Sort-Object Ordinal | Export-Csv -LiteralPath $OutCsv -NoTypeInformation -Encoding utf8
Write-Host "Wrote: $OutCsv"
