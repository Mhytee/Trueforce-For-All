# tools/loc/convert-xaml.ps1
#
# Purpose: replace the English attribute values and text nodes listed in a
#          keys CSV (from keys.ps1, after owner review) with {loc:T Key}
#          markup, byte for byte outside the replaced spans (BOM and line
#          endings kept), and merge the English text into en.json. Every row
#          must match exactly one occurrence on its recorded line, on the
#          element with its recorded x:Name or path, or nothing is written.
# Usage:   .\convert-xaml.ps1 -Keys <csv> [-DryRun] [-EnJson <path>] [-Root <repo>]
#          -DryRun prints every planned replacement and writes nothing.
#          -EnJson defaults to <Root>\src\TrueforceForAll.Plugin\Languages\en.json.
#          -Root resolves the CSV's repo-relative file paths against another
#          folder (used to rehearse a conversion on copies) and relocates
#          en.json with them; an en.json under the repo is written from a
#          rehearsal only when -EnJson names it explicitly.
# converted-files.txt: after a real conversion, the file's line in
#          <Root>\tools\loc\converted-files.txt gets the scopes the CSV rows
#          covered (resources:, header:, tab:, trailer:, see _common.ps1); a
#          file without SHTabItems is listed whole, as a bare path. A bare
#          path already listed stays bare. Under another -Root the scopes are
#          recorded there only when that tools\loc folder exists (it is the
#          same file validate.ps1 -Root reads); the repo's list is never
#          touched from another root.
# Text nodes: Hyperlink text is wrapped in <Run Text="{loc:T Key}"/>; the sole
#          text child of a TextBlock, Label, Run, Button, CheckBox,
#          RadioButton, ComboBoxItem or ListBoxItem moves to its Text or
#          Content attribute. Any other element (property elements such as
#          Button.Content included) is refused: convert by hand.
# Runs under Windows PowerShell 5.1.

param(
    [Parameter(Mandatory = $true)][string]$Keys,
    [switch]$DryRun,
    [string]$EnJson = '',
    [string]$Root = ''
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

$enJsonExplicit = ($EnJson -ne '')
if ($Root -eq '') { $Root = $LocRepoRoot }
$Root = [System.IO.Path]::GetFullPath($Root)
$isRepo = ($Root.TrimEnd('\') -ieq $LocRepoRoot.TrimEnd('\'))
# -Root relocates en.json too, so a rehearsal never merges into the repo's.
if ($EnJson -eq '') { $EnJson = Join-Path $Root $LocEnJsonRel }
$EnJson = [System.IO.Path]::GetFullPath($EnJson)
$commonSet = Read-LocListFile (Join-Path $LocToolsDir 'common-keys.txt')
$convertedListPath = Join-Path $Root 'tools\loc\converted-files.txt'

# Elements whose sole text child may move to an attribute (Text on Run and
# TextBlock, Content on the rest). Hyperlink text is wrapped in a Run instead.
$LocTextMoveElements = @('TextBlock', 'Label', 'Run', 'Button', 'CheckBox', 'RadioButton', 'ComboBoxItem', 'ListBoxItem', 'Hyperlink')

function Update-LocConvertedList([string]$ListPath, [string]$Rel, $Derived) {
    # Merges the scopes a conversion covered ($Derived: a set of specs, ''
    # meaning the whole file) into the file's converted-files.txt line: a new
    # line for a file not yet listed, the union for a scoped line, and a bare
    # path (whole file) stays bare or replaces a scoped line when the whole
    # file was covered. Every other line, comments included, is kept.
    $whole = $Derived.Contains('')
    $text = ''
    if (Test-Path -LiteralPath $ListPath) { $text = (Read-LocTextFile $ListPath).Text.Replace("`r`n", "`n") }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($l in $text.Split("`n")) { $lines.Add($l) }
    while ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Length -eq 0) { $lines.RemoveAt($lines.Count - 1) }
    $found = -1
    $existing = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i]
        if ($t.Trim().Length -eq 0 -or $t.StartsWith('#')) { continue }
        $e = ConvertFrom-LocConvertedEntry $t
        if ($e.Path -eq $Rel) { $found = $i; $existing = $e; break }
    }
    $specs = $null
    if ($null -eq $existing) {
        if (-not $whole) { $specs = @($Derived) }
        $line = ConvertTo-LocConvertedEntry $Rel $specs
        $lines.Add($line)
        Write-Host ('converted-files.txt: added ' + $line)
    } elseif ($null -eq $existing.Specs) {
        Write-Host ('converted-files.txt: {0} is already listed as a whole file' -f $Rel)
        return
    } else {
        if (-not $whole) {
            $union = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
            foreach ($s in $existing.Specs) { [void]$union.Add($s) }
            foreach ($s in $Derived) { [void]$union.Add($s) }
            $specs = @($union)
        }
        $line = ConvertTo-LocConvertedEntry $Rel $specs
        $lines[$found] = $line
        Write-Host ('converted-files.txt: now ' + $line)
    }
    Write-LocNewFile $ListPath (($lines -join "`n") + "`n")
}

$rows = Read-LocCsv (Resolve-LocInputPath $Keys)
if ($rows.Count -eq 0) { throw 'The keys CSV has no rows.' }

# ---- key sanity: valid identifiers, one value per key
$keyValues = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
foreach ($r in $rows) {
    if (-not ($r.key -match $LocKeyRegex)) { throw "Row $($r.file):$($r.line) has an invalid key '$($r.key)'." }
    if ($keyValues.ContainsKey($r.key)) {
        if (-not ($keyValues[$r.key] -ceq $r.value)) { throw ("Key {0} is used for two different values: [{1}] and [{2}]" -f $r.key, $keyValues[$r.key], $r.value) }
    } else { $keyValues[$r.key] = $r.value }
}

function Get-LocLineStarts([string]$Text) {
    $starts = New-Object System.Collections.Generic.List[int]
    $starts.Add(0)
    $i = $Text.IndexOf("`n")
    while ($i -ge 0) { $starts.Add($i + 1); $i = $Text.IndexOf("`n", $i + 1) }
    return ,$starts
}

function Get-LocLineSpan($Starts, [string]$Text, [int]$LineNo) {
    if ($LineNo -lt 1 -or $LineNo -gt $Starts.Count) { throw "Line $LineNo is outside the file." }
    $s = $Starts[$LineNo - 1]
    $e = $Text.Length
    if ($LineNo -lt $Starts.Count) { $e = $Starts[$LineNo] - 1 }
    if ($e -gt $s -and $Text[$e - 1] -eq "`r") { $e-- }
    return @($s, $e)
}

$plans = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$byFile = $rows | Group-Object file

foreach ($g in $byFile) {
    $rel = $g.Name
    $abs = Join-Path $Root ($rel.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $abs)) { $failures.Add("$rel is not under $Root"); continue }
    $inv = Get-LocInventory $abs $rel $commonSet
    $idx = Get-LocInventoryIndex $inv
    $ctx = Get-LocScopeContext $inv.Doc
    $text = $inv.File.Text
    $starts = Get-LocLineStarts $text
    $edits = New-Object System.Collections.Generic.List[object]
    # The converted-files.txt scopes these rows cover ('' = the whole file).
    $specs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

    foreach ($r in $g.Group) {
        $k = Get-LocRowKey $r
        if (-not $idx.ContainsKey($k)) {
            $failures.Add(('{0}:{1} {2}=[{3}] on {4}: no such string at that line on that element (stale CSV?)' -f $rel, $r.line, $r.attribute, $r.value, $r.xName))
            continue
        }
        $live = $idx[$k]
        $spec = Get-LocRowScopeSpec $live $ctx
        if ($null -eq $spec) {
            $failures.Add(('{0}:{1} {2}="{3}" on {4}: between two SHTabItems and inside neither (or in an unnamed tab); no converted-files.txt scope covers it, convert by hand' -f $rel, $r.line, $r.attribute, $r.value, $r.xName))
            continue
        }
        [void]$specs.Add($spec)
        $lineNo = [int]$r.line
        $span = Get-LocLineSpan $starts $text $lineNo
        $lineText = $text.Substring($span[0], $span[1] - $span[0])
        $locValue = '{loc:T ' + $r.key + '}'
        $desc = '{0}:{1}  {2}="{3}"  ->  {4}' -f $rel, $r.line, $r.attribute, $r.value, $locValue

        if ($r.attribute -ne '#text') {
            # Attribute (or Setter Value): exactly one Attr="value" on the line
            # whose decoded value is the recorded English.
            $attrName = $r.attribute
            if ($attrName.StartsWith('Setter:')) { $attrName = 'Value' }
            $re = [regex]('(?<=\s)' + [regex]::Escape($attrName) + '="([^"]*)"')
            $hits = New-Object System.Collections.Generic.List[object]
            foreach ($m in $re.Matches($lineText)) {
                if ((ConvertFrom-LocXmlEscapes $m.Groups[1].Value) -ceq $r.value) { $hits.Add($m) }
            }
            if ($hits.Count -ne 1) {
                $failures.Add(('{0}:{1} {2}="{3}": expected exactly one occurrence on the line, found {4}' -f $rel, $r.line, $r.attribute, $r.value, $hits.Count))
                continue
            }
            # The XML parser's own position for the attribute must agree.
            $li = [System.Xml.IXmlLineInfo]$live.Node
            $attrOffset = $starts[$li.LineNumber - 1] + $li.LinePosition - 1
            $hitOffset = $span[0] + $hits[0].Index
            if ($attrOffset -ne $hitOffset) {
                $failures.Add(('{0}:{1} {2}: text match at column {3} but the parser places the attribute at column {4}' -f $rel, $r.line, $r.attribute, ($hits[0].Index + 1), $li.LinePosition))
                continue
            }
            $edits.Add([pscustomobject]@{ Start = ($span[0] + $hits[0].Groups[1].Index); Length = $hits[0].Groups[1].Length; Replacement = $locValue; Desc = $desc })
            continue
        }

        # Text node. Its parser position gives the exact offset of the first
        # visible character; the raw span runs to the next '<'.
        $el = $live.Element
        $node = $live.Node
        $tli = [System.Xml.IXmlLineInfo]$node
        $nodeStart = $starts[$tli.LineNumber - 1] + $tli.LinePosition - 1
        $lt = $text.IndexOf('<', $nodeStart)
        if ($lt -lt 0) { $failures.Add("$rel`:$($r.line) #text: no closing tag after the text"); continue }
        $rawText = $text.Substring($nodeStart, $lt - $nodeStart)
        if (-not ((ConvertTo-LocCollapsedText (ConvertFrom-LocXmlEscapes $rawText)) -ceq $r.value)) {
            $failures.Add(('{0}:{1} #text: file text [{2}] does not match the recorded value [{3}]' -f $rel, $r.line, (ConvertTo-LocCollapsedText $rawText), $r.value))
            continue
        }
        if ($text[$nodeStart - 1] -ne '>') { $failures.Add("$rel`:$($r.line) #text: the text does not start right after a tag"); continue }
        $typeName = Get-LocElementTypeName $el
        $local = $el.Name.LocalName
        $closing = '</' + $typeName + '>'
        $onlyChild = (@($el.Nodes()).Count -eq 1)
        if ($local -eq 'Hyperlink') {
            $edits.Add([pscustomobject]@{ Start = $nodeStart; Length = $rawText.Length; Replacement = ('<Run Text="' + $locValue + '"/>'); Desc = $desc })
            continue
        }
        # Only a known control's text moves to an attribute. A property
        # element (Button.Content, TextBlock.Text) or any other element keeps
        # its text: the attribute to use is not knowable here.
        if ($local.Contains('.') -or $LocTextMoveElements -notcontains $local) {
            $failures.Add(('{0}:{1} #text on <{2}>: only the text of {3} moves to an attribute; convert by hand' -f $rel, $r.line, $typeName, ($LocTextMoveElements -join ', ')))
            continue
        }
        if (-not $onlyChild) {
            $failures.Add(('{0}:{1} #text on <{2}>: mixed content (text beside other nodes) cannot move to an attribute; convert by hand' -f $rel, $r.line, $typeName))
            continue
        }
        if ($text.Substring($lt, [Math]::Min($closing.Length, $text.Length - $lt)) -ne $closing) {
            $failures.Add(('{0}:{1} #text on <{2}>: expected {3} right after the text' -f $rel, $r.line, $typeName, $closing))
            continue
        }
        $attr = 'Content'
        if ($local -eq 'Run' -or $local -eq 'TextBlock') { $attr = 'Text' }
        if ($null -ne $el.Attribute($attr)) {
            $failures.Add(('{0}:{1} #text on <{2}>: the element already has a {3} attribute' -f $rel, $r.line, $typeName, $attr))
            continue
        }
        # From the '>' that ends the start tag through the closing tag.
        $edits.Add([pscustomobject]@{ Start = ($nodeStart - 1); Length = (($lt + $closing.Length) - ($nodeStart - 1)); Replacement = (' ' + $attr + '="' + $locValue + '"/>'); Desc = $desc })
    }

    $plans.Add([pscustomobject]@{ Rel = $rel; Abs = $abs; File = $inv.File; Edits = $edits; Text = $text; Specs = $specs })
}

# ---- report the plan
$totalEdits = 0
foreach ($p in $plans) {
    foreach ($e in ($p.Edits | Sort-Object Start)) { Write-Host $e.Desc }
    $totalEdits += $p.Edits.Count
    if ($p.Edits.Count -gt 0 -and $p.Text.IndexOf('xmlns:loc=') -lt 0) {
        Write-Host ('{0}:root  + xmlns:loc="{1}"' -f $p.Rel, $LocXmlnsLoc)
    }
    if ($p.Specs.Count -gt 0) {
        [string[]]$shown = @($p.Specs | ForEach-Object { if ($_ -eq '') { 'whole file' } else { $_ } })
        if ($shown.Count -gt 1) { [System.Array]::Sort($shown, [System.StringComparer]::Ordinal) }
        Write-Host ('{0}:scopes  {1}' -f $p.Rel, ($shown -join ', '))
    }
}

# ---- en.json merge (computed in both modes, written only for real)
$en = $null
if (Test-Path -LiteralPath $EnJson) { $en = Read-LocJson $EnJson }
else {
    $en = New-Object System.Collections.Specialized.OrderedDictionary
    $meta = New-Object System.Collections.Specialized.OrderedDictionary
    $meta['culture'] = 'en'
    $meta['name'] = 'English'
    $en['_meta'] = $meta
}
$newKeys = 0
$sameKeys = 0
foreach ($k in $keyValues.Keys) {
    if ($en.Contains($k)) {
        if (-not ([string]$en[$k] -ceq $keyValues[$k])) { $failures.Add(("en.json already has {0} = [{1}], the CSV wants [{2}]" -f $k, $en[$k], $keyValues[$k])) }
        else { $sameKeys++ }
    } else { $newKeys++ }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host ('{0} problem(s); nothing written:' -f $failures.Count)
    foreach ($f in $failures) { Write-Host ('  ' + $f) }
    exit 1
}

Write-Host ''
Write-Host ('{0} replacements in {1} file(s); en.json: +{2} new key(s), {3} already present -> {4}' -f $totalEdits, $plans.Count, $newKeys, $sameKeys, (Get-LocRelativePath $EnJson $LocRepoRoot))
if ($DryRun) {
    Write-Host 'Dry run: nothing written.'
    exit 0
}

# A rehearsal under another -Root must not merge into the repo's en.json by
# accident; only an explicit -EnJson may point there.
$repoPrefix = $LocRepoRoot.TrimEnd('\') + '\'
$rootPrefix = $Root.TrimEnd('\') + '\'
if (-not $isRepo -and -not $enJsonExplicit -and
    $EnJson.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
    -not $EnJson.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw ("Refusing to write {0}: it is under the repo while -Root is {1}. Pass -EnJson explicitly to merge a rehearsal into the repo's en.json." -f $EnJson, $Root)
}

# ---- apply
foreach ($p in $plans) {
    if ($p.Edits.Count -eq 0) { continue }
    $text = $p.Text
    $sorted = @($p.Edits | Sort-Object Start -Descending)
    $prevStart = [int]::MaxValue
    foreach ($e in $sorted) {
        if ($e.Start + $e.Length -gt $prevStart) { throw "Overlapping edits in $($p.Rel) at offset $($e.Start)." }
        $text = $text.Substring(0, $e.Start) + $e.Replacement + $text.Substring($e.Start + $e.Length)
        $prevStart = $e.Start
    }
    if ($text.IndexOf('xmlns:loc=') -lt 0) {
        $anchor = 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'
        $ai = $text.IndexOf($anchor)
        if ($ai -lt 0) { throw "Cannot find the xmlns:x declaration in $($p.Rel) to add xmlns:loc after." }
        $lineStart = $text.LastIndexOf("`n", $ai) + 1
        $indent = $text.Substring($lineStart, $ai - $lineStart)
        $indent = $indent.Substring(0, $indent.Length - $indent.TrimStart().Length)
        $text = $text.Insert($ai + $anchor.Length, $p.File.Eol + $indent + 'xmlns:loc="' + $LocXmlnsLoc + '"')
    }
    [void](Get-LocXamlDoc $text)   # throws if the result is not well-formed
    $before = [regex]::Matches($p.Text, '\{loc:T ').Count
    $after = [regex]::Matches($text, '\{loc:T ').Count
    if ($after - $before -ne $p.Edits.Count) { throw "Edit count mismatch in $($p.Rel): expected $($p.Edits.Count) new {loc:T}, got $($after - $before)." }
    Write-LocTextFile $p.Abs $text $p.File.HasBom
    Write-Host ('wrote {0} ({1} edits, BOM={2}, EOL={3})' -f $p.Rel, $p.Edits.Count, $p.File.HasBom, ($p.File.Eol.Replace("`r", 'CR').Replace("`n", 'LF')))

    # The list lives beside the tools of the tree being converted; a rehearsal without a tools\loc folder records nothing and the repo's list is never touched from another root.
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $convertedListPath))) { Write-Host ('converted-files.txt untouched ({0} has no tools\loc folder)' -f $Root); continue }
    Update-LocConvertedList $convertedListPath $p.Rel $p.Specs
}

foreach ($k in $keyValues.Keys) { if (-not $en.Contains($k)) { $en[$k] = $keyValues[$k] } }
Write-LocJson $EnJson $en
Write-Host ('wrote {0}' -f $EnJson)
