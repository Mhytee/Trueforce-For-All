# tools/loc/keys.ps1
#
# Purpose: propose one key per real string in scope, from an inventory CSV,
#          with translator-context columns for the owner review. Keys follow
#          <Area>_<Meaning>[<Suffix>]; values listed in common-keys.txt share a
#          Common_<Slug> key; collisions get _2, _3 and a review flag. TextBox
#          and editable ComboBox Text values and the values in
#          xaml-keep-literal.txt are excluded (identifier couplings) and listed
#          on the console instead.
# Usage:   .\keys.ps1 -Inventory <csv> -Scope <spec>[,<spec>...] -Out <csv> [-EnJson <en.json>] [-Root <repo>]
#          -Inventory is a fresh inventory.ps1 run over the current files, never
#          a CSV taken before a conversion (its rows no longer match).
#          Scope specs (the grammar shared with converted-files.txt, _common.ps1):
#                       file:<name>          whole XAML file
#                       resources:<name>     inside an X.Resources element
#                       header:<name>        before the first sh:SHTabItem, outside Resources
#                       tab:<x:Name>         one SHTabItem subtree, e.g. tab:SupportTab
#                       trailer:<name>       after the last sh:SHTabItem
#          Several specs: -Scope @('a','b') or one comma-joined string. A spec
#          that matches no real string is an error.
#          -EnJson defaults to <Root>\src\TrueforceForAll.Plugin\Languages\en.json
#          (-Root relocates it); when it exists its keys are respected (same
#          value: reused; other value: renamed).
# Runs under Windows PowerShell 5.1.

param(
    [Parameter(Mandatory = $true)][string]$Inventory,
    [Parameter(Mandatory = $true)][string[]]$Scope,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$EnJson = '',
    [string]$Root = ''
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

if ($Root -eq '') { $Root = $LocRepoRoot }
$Root = [System.IO.Path]::GetFullPath($Root)
# -Root relocates en.json too, so a rehearsal never reads the repo's keys.
if ($EnJson -eq '') { $EnJson = Join-Path $Root $LocEnJsonRel }
# A single comma-joined string (as cmd.exe or -File passes it) is one spec per item.
if ($Scope.Count -eq 1 -and $Scope[0].Contains(',')) {
    $Scope = @($Scope[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
}
if ($Scope.Count -eq 0) { throw '-Scope has no spec.' }
foreach ($s in $Scope) { [void](Split-LocScopeSpec $s) }   # fail on a bad spec before any work
$commonSet = Read-LocListFile (Join-Path $LocToolsDir 'common-keys.txt')
$keepLiteral = Read-LocListFile (Join-Path $LocToolsDir 'xaml-keep-literal.txt')

# common-keys.txt must slug to distinct keys.
$slugOwner = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
foreach ($v in $commonSet) {
    $slug = Get-LocCommonSlug $v
    if ($slugOwner.ContainsKey($slug)) { throw ("common-keys.txt: '{0}' and '{1}' both become Common_{2}." -f $v, $slugOwner[$slug], $slug) }
    $slugOwner[$slug] = $v
}

function Get-LocCodeAssignedMap([string]$Dir) {
    # x:Name -> set of properties the code-behind assigns (.Text, .Content,
    # .Header, .ToolTip). A XAML value on such a pair is often a placeholder.
    $map = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Dir)) { return $map }
    $re = [regex]'\b([A-Za-z_][A-Za-z0-9_]*)\.(Text|Content|Header|ToolTip)\s*=(?!=)'
    $files = Get-ChildItem -Path $Dir -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    foreach ($cs in $files) {
        $text = [System.IO.File]::ReadAllText($cs.FullName)
        foreach ($m in $re.Matches($text)) {
            $name = $m.Groups[1].Value
            if (-not $map.ContainsKey($name)) { $map[$name] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal) }
            [void]$map[$name].Add($m.Groups[2].Value)
        }
    }
    return $map
}

function Get-LocSiblingNames($El) {
    if ($null -eq $El.Parent) { return '' }
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($s in $El.Parent.Elements()) {
        if ([object]::ReferenceEquals($s, $El)) { continue }
        $n = Get-LocOwnName $s
        if ($null -ne $n) { $names.Add($n) }
    }
    return ($names -join ' ')
}

function Get-LocHelpTextAfter($El) {
    # The next TextBlock styled HelpText after the element, its parent, and so
    # on up to and including its containing Grid.
    $cur = $El
    $levels = 0
    while ($null -ne $cur -and $levels -lt 6) {
        $next = $null
        foreach ($s in $cur.ElementsAfterSelf()) { $next = $s; break }
        if ($null -ne $next -and $next.Name.LocalName -eq 'TextBlock') {
            $st = $next.Attribute('Style')
            if ($null -ne $st -and $st.Value -like '*HelpText*') { return (Get-LocElementText $next) }
        }
        if ($cur.Name.LocalName -eq 'Grid' -or $cur.Name.LocalName -eq 'SHTabItem') { break }
        $cur = $cur.Parent
        $levels++
    }
    return ''
}

# ---- load the inventory and re-parse the files it names

$csvRows = Read-LocCsv (Resolve-LocInputPath $Inventory)
$files = @($csvRows | ForEach-Object { $_.file } | Select-Object -Unique)
$fileData = @{}
foreach ($f in $files) {
    $abs = Join-Path $Root ($f.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $abs)) { throw "Inventory names $f but it is not under $Root" }
    $inv = Get-LocInventory $abs $f $commonSet
    $fileData[$f] = @{ Inv = $inv; Index = (Get-LocInventoryIndex $inv); Ctx = (Get-LocScopeContext $inv.Doc) }
}
$codeAssigned = Get-LocCodeAssignedMap (Join-Path $Root 'src\TrueforceForAll.Plugin')
$existing = $null
if (Test-Path -LiteralPath $EnJson) { $existing = Read-LocJson $EnJson }

# ---- select the rows in scope

$inScope = New-Object System.Collections.Generic.List[object]
$excluded = New-Object System.Collections.Generic.List[string]
$specHits = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
foreach ($s in $Scope) { $specHits[$s] = 0 }
$stale = 0
$realTotal = 0
foreach ($r in $csvRows) {
    if ($r.skipReason -ne '') { continue }
    $realTotal++
    $fd = $fileData[$r.file]
    $k = Get-LocRowKey $r
    if (-not $fd.Index.ContainsKey($k)) {
        $stale++
        Write-Warning ("Inventory row not found in the current file (stale CSV?): {0}:{1} {2}=[{3}]" -f $r.file, $r.line, $r.attribute, $r.value)
        continue
    }
    $live = $fd.Index[$k]
    # Every spec is tested so a spec that matches nothing can be reported.
    $matched = $false
    foreach ($s in $Scope) {
        if (Test-LocSpecMatch $live $s $fd.Ctx) { $specHits[$s] = $specHits[$s] + 1; $matched = $true }
    }
    if (-not $matched) { continue }
    $ln = $live.Element.Name.LocalName
    if ($live.attribute -eq 'Text' -and ($ln -eq 'TextBox' -or $ln -eq 'PasswordBox' -or $ln -eq 'RichTextBox' -or $ln -eq 'ComboBox')) {
        $excluded.Add(('{0}:{1} {2} {3}.Text=[{4}]  (typed input; code parses it back)' -f $live.file, $live.line, $live.xName, $ln, $live.value))
        continue
    }
    if ($keepLiteral.Contains($live.value)) {
        $excluded.Add(('{0}:{1} {2} {3}=[{4}]  (xaml-keep-literal.txt)' -f $live.file, $live.line, $live.xName, $live.attribute, $live.value))
        continue
    }
    $inScope.Add($live)
}
if ($stale -gt 0) { throw "$stale inventory rows no longer match the files. Re-run inventory.ps1 first." }
$emptySpecs = @($Scope | Where-Object { $specHits[$_] -eq 0 })
if ($emptySpecs.Count -gt 0) {
    throw ("Scope spec(s) matched no real string in the inventory: {0}. Check the file name or x:Name, and that the inventory covers that file." -f ($emptySpecs -join ', '))
}

# ---- propose keys

$proposals = New-Object System.Collections.Generic.List[object]
$baseCount = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
$baseValues = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
foreach ($row in $inScope) {
    $p = Get-LocProposedKey $row $commonSet
    if (-not $p.Shared) {
        if ($baseCount.ContainsKey($p.Key)) { $baseCount[$p.Key] = $baseCount[$p.Key] + 1 } else { $baseCount[$p.Key] = 1 }
        if (-not $baseValues.ContainsKey($p.Key)) { $baseValues[$p.Key] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal) }
        [void]$baseValues[$p.Key].Add($row.value)
    }
    $proposals.Add([pscustomobject]@{ Row = $row; Base = $p.Key; Shared = $p.Shared; Key = ''; Flags = (New-Object System.Collections.Generic.List[string]) })
}

$taken = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($p in $proposals) {
    $value = $p.Row.value
    if ($p.Shared) {
        if ($null -ne $existing -and $existing.Contains($p.Base) -and -not ([string]$existing[$p.Base] -ceq $value)) {
            throw ("Shared key {0} already exists in en.json with a different value: [{1}] vs [{2}]" -f $p.Base, $existing[$p.Base], $value)
        }
        $p.Key = $p.Base
        [void]$taken.Add($p.Key)
    } else {
        $key = $p.Base
        # Unnamed elements whose keys collide first try a longer word run
        # ("...TelemetryFfbStrengthUp" beside "...TelemetryFfbStrengthDown").
        if ($baseCount[$p.Base] -gt 1) {
            $alt = (Get-LocProposedKey $p.Row $commonSet -Long).Key
            $altFree = ($alt -ne $p.Base -and -not $taken.Contains($alt))
            if ($altFree -and $null -ne $existing -and $existing.Contains($alt) -and -not ([string]$existing[$alt] -ceq $value)) { $altFree = $false }
            if ($altFree) {
                $others = @($proposals | Where-Object { $_.Base -eq $p.Base -and -not [object]::ReferenceEquals($_, $p) } | ForEach-Object { (Get-LocProposedKey $_.Row $commonSet -Long).Key })
                if ($others -notcontains $alt) { $key = $alt }
            }
        }
        $n = 1
        while ($true) {
            $clash = $taken.Contains($key)
            if (-not $clash -and $null -ne $existing -and $existing.Contains($key) -and -not ([string]$existing[$key] -ceq $value)) { $clash = $true }
            if (-not $clash) { break }
            $n++
            $key = $p.Base + '_' + $n
        }
        $p.Key = $key
        [void]$taken.Add($key)
        if ($baseCount[$p.Base] -gt 1) {
            $note = 'collision: {0} rows propose {1}' -f $baseCount[$p.Base], $p.Base
            if ($key -ne $p.Base -and -not $key.StartsWith($p.Base + '_')) { $note += ' (longer word run used)' }
            if ($baseValues[$p.Base].Count -eq 1) { $note += ' (same value; consider common-keys.txt)' }
            $p.Flags.Add($note)
        } elseif ($key -ne $p.Base) {
            $p.Flags.Add('collision: en.json already has ' + $p.Base + ' with another value')
        }
        if ($null -ne $existing -and $existing.Contains($key)) { $p.Flags.Add('already in en.json') }
    }
    if (-not ($p.Key -match $LocKeyRegex)) { throw "Proposed key '$($p.Key)' is not a valid identifier." }
    # Placeholder text: the code-behind writes this property on this x:Name.
    $own = $null
    if ($p.Row.Element.Name.LocalName -ne 'Setter') { $own = Get-LocOwnName $p.Row.Element }
    if ($null -ne $own -and $codeAssigned.ContainsKey($own)) {
        $prop = $p.Row.attribute
        if ($prop -eq '#text') { $prop = 'Text' }
        if ($prop.StartsWith('Setter:')) { $prop = $prop.Substring(7) }
        if ($codeAssigned[$own].Contains($prop)) { $p.Flags.Add('code-assigned: ' + $own + '.' + $prop) }
    }
    if ($value -match '^\s*[\d.,]+\s*[^\d\s]{1,8}\s*$') { $p.Flags.Add('unit-like') }
}

# ---- output

$outRows = New-Object System.Collections.Generic.List[object]
foreach ($p in $proposals) {
    $el = $p.Row.Element
    $ownTip = ''
    if ($el.Name.LocalName -ne 'Setter' -and $p.Row.attribute -ne 'ToolTip') {
        $ta = $el.Attribute('ToolTip')
        if ($null -ne $ta -and -not $ta.Value.StartsWith('{')) { $ownTip = $ta.Value }
    }
    $help = ''
    if ($el.Name.LocalName -ne 'Setter') { $help = Get-LocHelpTextAfter $el }
    if ($help -ceq $p.Row.value) { $help = '' }
    $outRows.Add([pscustomobject]@{
        key = $p.Key; file = $p.Row.file; line = $p.Row.line; xName = $p.Row.xName; attribute = $p.Row.attribute
        value = $p.Row.value; reviewFlag = ($p.Flags -join ' | '); siblingNames = (Get-LocSiblingNames $el)
        helpText = $help; ownToolTip = $ownTip
    })
}
$columns = @('key', 'file', 'line', 'xName', 'attribute', 'value', 'reviewFlag', 'siblingNames', 'helpText', 'ownToolTip')
Write-LocCsv (Resolve-LocOutputPath $Out) $columns $outRows

Write-Host ('Scope: {0}' -f ($Scope -join ', '))
Write-Host ('{0} real strings in the inventory, {1} in scope, {2} excluded, {3} keys proposed ({4} distinct)' -f $realTotal, ($inScope.Count + $excluded.Count), $excluded.Count, $outRows.Count, $taken.Count)
$areaCounts = $outRows | Group-Object { ($_.key -split '_')[0] } | Sort-Object Name
foreach ($g in $areaCounts) { Write-Host ('  {0,-14} {1,5} keys' -f $g.Name, $g.Count) }
$flagged = @($outRows | Where-Object { $_.reviewFlag -ne '' })
Write-Host ('{0} rows carry a review flag' -f $flagged.Count)
$flagKinds = $flagged | ForEach-Object { $_.reviewFlag -split ' \| ' } | ForEach-Object { ($_ -split ':')[0] } | Group-Object | Sort-Object Name
foreach ($g in $flagKinds) { Write-Host ('  {0,-16} {1,5}' -f $g.Name, $g.Count) }
if ($excluded.Count -gt 0) {
    Write-Host ('{0} excluded (no key proposed):' -f $excluded.Count)
    foreach ($e in $excluded) { Write-Host ('  ' + $e) }
}
Write-Host ('-> {0}' -f $Out)
