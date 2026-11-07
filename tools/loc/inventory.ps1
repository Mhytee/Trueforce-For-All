# tools/loc/inventory.ps1
#
# Purpose: list every candidate string in one or more XAML files, one CSV row
#          each, with a skipReason column that is empty for a real string and
#          otherwise names why it is not one (binding, numeric, glyph,
#          identifier, empty). Denylisted identifier and layout attributes are
#          not candidates and are only counted. Prints per-file totals and the
#          grand total.
# Usage:   .\inventory.ps1 -Xaml <path>[,<path>...] -Out <csv>
#          .\inventory.ps1 -Xaml <converted.xaml> -Out <csv> -ResolveWith <en.json> [-Baseline <csv>]
#            -ResolveWith replaces every "{loc:T Key}" value with its English
#            text so the output can be diffed against a pre-conversion run;
#            -Baseline then compares the ordered real-string values per file
#            and fails on the first difference (the round-trip check).
# Runs under Windows PowerShell 5.1.

param(
    [Parameter(Mandatory = $true)][string[]]$Xaml,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ResolveWith = '',
    [string]$Baseline = '',
    [string]$Root = ''
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

# -Root is the base the file column is made relative to (default: the repo),
# so an inventory of copies under another root compares against the baseline.
if ($Root -eq '') { $Root = $LocRepoRoot }
# A single comma-joined string (as cmd.exe or -File passes it) is one path per item.
if ($Xaml.Count -eq 1 -and $Xaml[0].Contains(',')) {
    $Xaml = @($Xaml[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
}
if ($Xaml.Count -eq 0) { throw '-Xaml names no file.' }
$commonSet = Read-LocListFile (Join-Path $LocToolsDir 'common-keys.txt')
$en = $null
if ($ResolveWith -ne '') { $en = Read-LocJson (Resolve-LocInputPath $ResolveWith) }

$allRows = New-Object System.Collections.Generic.List[object]
$grandReal = 0
$grandRows = 0
$deniedTotal = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)

foreach ($x in $Xaml) {
    $path = Resolve-LocInputPath $x
    $rel = Get-LocRelativePath $path $Root
    $inv = Get-LocInventory $path $rel $commonSet
    $real = 0
    $byReason = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
    foreach ($r in $inv.Rows) {
        if ($null -ne $en) {
            # Positional or Key= form, as the C# test's LocRefWhole accepts.
            $m = [regex]::Match($r.value, '^\{loc:T\s+(?:Key\s*=\s*)?([A-Za-z][A-Za-z0-9_]*)\s*\}$')
            if ($m.Success) {
                $k = $m.Groups[1].Value
                if ($en.Contains($k)) {
                    $r.value = [string]$en[$k]
                    $r.skipReason = Get-LocSkipReason $r.value $commonSet $r.Element.Name.LocalName $r.attribute
                } else {
                    Write-Warning ("{0}:{1} references missing key {2}" -f $rel, $r.line, $k)
                }
            }
        }
        if ($r.skipReason -eq '') { $real++ }
        else {
            if ($byReason.ContainsKey($r.skipReason)) { $byReason[$r.skipReason] = $byReason[$r.skipReason] + 1 } else { $byReason[$r.skipReason] = 1 }
        }
        $allRows.Add($r)
    }
    foreach ($k in $inv.Denied.Keys) {
        if ($deniedTotal.ContainsKey($k)) { $deniedTotal[$k] = $deniedTotal[$k] + $inv.Denied[$k] } else { $deniedTotal[$k] = $inv.Denied[$k] }
    }
    $skipped = @($byReason.Keys | Sort-Object | ForEach-Object { '{0} {1}' -f $_, $byReason[$_] }) -join ', '
    Write-Host ('{0}: {1} real strings, {2} rows ({3})' -f $rel, $real, $inv.Rows.Count, $skipped)
    $grandReal += $real
    $grandRows += $inv.Rows.Count
}

Write-LocCsv (Resolve-LocOutputPath $Out) $LocInventoryColumns $allRows
Write-Host ('TOTAL: {0} real strings, {1} rows, {2} files -> {3}' -f $grandReal, $grandRows, $Xaml.Count, $Out)
$deniedSum = 0
foreach ($v in $deniedTotal.Values) { $deniedSum += $v }
Write-Host ('Denylisted attributes not listed: {0} across {1} names' -f $deniedSum, $deniedTotal.Count)

if ($Baseline -ne '') {
    $base = Read-LocCsv (Resolve-LocInputPath $Baseline)
    $files = @($allRows | ForEach-Object { $_.file } | Select-Object -Unique)
    $failed = $false
    foreach ($f in $files) {
        $now = @($allRows | Where-Object { $_.file -eq $f -and $_.skipReason -eq '' } | ForEach-Object { $_.value })
        $was = @($base | Where-Object { $_.file -eq $f -and $_.skipReason -eq '' } | ForEach-Object { $_.value })
        $n = [Math]::Min($now.Count, $was.Count)
        $firstDiff = -1
        for ($i = 0; $i -lt $n; $i++) { if (-not ($now[$i] -ceq $was[$i])) { $firstDiff = $i; break } }
        if ($firstDiff -lt 0 -and $now.Count -ne $was.Count) { $firstDiff = $n }
        if ($firstDiff -ge 0) {
            $failed = $true
            $a = '(end)'
            $b = '(end)'
            if ($firstDiff -lt $was.Count) { $a = $was[$firstDiff] }
            if ($firstDiff -lt $now.Count) { $b = $now[$firstDiff] }
            Write-Host ('ROUND-TRIP FAIL {0}: real string #{1} differs. baseline=[{2}] now=[{3}] (baseline {4}, now {5})' -f $f, ($firstDiff + 1), $a, $b, $was.Count, $now.Count)
        } else {
            Write-Host ('round-trip ok {0}: {1} real strings identical and in order' -f $f, $now.Count)
        }
    }
    if ($failed) { exit 1 }
}
