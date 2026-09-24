# tools/loc/pseudo.ps1
#
# Purpose: build the pseudo-locale file from en.json. Every value gets its
#          letters accent-swapped, is padded by 30 percent with "~" and is
#          wrapped in brackets, so clipped or hard-coded text shows up in the
#          panel; {n} placeholders stay intact. _meta becomes culture
#          "qps-ploc", name "Pseudo (layout test)".
# Usage:   .\pseudo.ps1 -In <en.json> -Out <qps-ploc.json>
# Runs under Windows PowerShell 5.1. This file must stay pure ASCII: 5.1 reads
# a script without a BOM in the ANSI code page, so the accented letters are
# spelled as code points below.

param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

# ASCII letter -> accented look-alike (Unicode code point), a..z then A..Z.
# Parallel arrays rather than a hash literal: PowerShell hash keys are
# case-insensitive, so 'a' and 'A' would collide.
$letters = 'abcdefghijklmnopqrstuvwxyz'
$lowerCodes = @(0x00E1, 0x0180, 0x00E7, 0x00F0, 0x00E9, 0x0192, 0x011D, 0x0125, 0x00ED, 0x0135, 0x0137, 0x013C, 0x0271,
    0x00F1, 0x00F3, 0x00FE, 0x01EB, 0x0155, 0x0161, 0x0167, 0x00FA, 0x1E7D, 0x0175, 0x1E8B, 0x00FD, 0x017E)
$upperCodes = @(0x00C1, 0x0181, 0x00C7, 0x00D0, 0x00C9, 0x0191, 0x011C, 0x0124, 0x00CD, 0x0134, 0x0136, 0x013B, 0x1E3E,
    0x00D1, 0x00D3, 0x00DE, 0x01EA, 0x0154, 0x0160, 0x0166, 0x00DA, 0x1E7C, 0x0174, 0x1E8C, 0x00DD, 0x017D)
$accentMap = New-Object 'System.Collections.Generic.Dictionary[char,char]'
for ($i = 0; $i -lt $letters.Length; $i++) {
    $accentMap[$letters[$i]] = [char][int]$lowerCodes[$i]
    $accentMap[[char]::ToUpperInvariant($letters[$i])] = [char][int]$upperCodes[$i]
}

function ConvertTo-LocPseudo([string]$Value) {
    $parts = [regex]::Split($Value, '(\{[^{}]*\})')
    $sb = New-Object System.Text.StringBuilder
    $visible = 0
    foreach ($part in $parts) {
        if ($part.Length -eq 0) { continue }
        if ($part.StartsWith('{') -and $part.EndsWith('}')) { [void]$sb.Append($part); continue }
        $visible += $part.Length
        foreach ($ch in $part.ToCharArray()) {
            if ($accentMap.ContainsKey($ch)) { [void]$sb.Append($accentMap[$ch]) } else { [void]$sb.Append($ch) }
        }
    }
    $pad = [int][Math]::Ceiling($visible * 0.3)
    return ('[' + $sb.ToString() + ('~' * $pad) + ']')
}

$en = Read-LocJson (Resolve-LocInputPath $In)
# Not $out: PowerShell variables are case-insensitive and the [string]$Out
# parameter would turn the dictionary into its type name.
$result = New-Object System.Collections.Specialized.OrderedDictionary
$meta = New-Object System.Collections.Specialized.OrderedDictionary
$meta['culture'] = 'qps-ploc'
$meta['name'] = 'Pseudo (layout test)'
if ($en.Contains('_meta') -and $en['_meta'] -is [System.Collections.Specialized.OrderedDictionary]) {
    foreach ($k in $en['_meta'].Keys) { if ($k -ne 'culture' -and $k -ne 'name') { $meta[$k] = $en['_meta'][$k] } }
}
$result['_meta'] = $meta
$count = 0
foreach ($k in $en.Keys) {
    if ($k -eq '_meta') { continue }
    if (-not ($en[$k] -is [string])) { throw "en.json key '$k' is not a string." }
    $result[$k] = ConvertTo-LocPseudo ([string]$en[$k])
    $count++
}
Write-LocJson (Resolve-LocOutputPath $Out) $result
Write-Host ('{0} strings pseudo-localized -> {1}' -f $count, $Out)
