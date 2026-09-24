# tools/loc/_common.ps1
#
# Purpose: shared helpers for the localization tooling. Byte-preserving file IO
#          (BOM and line endings survive), RFC 4180 CSV read/write, XAML walking
#          with line info, the attribute denylist, the real-string rule (with
#          the short-token rule), the scope specs shared by keys.ps1 -Scope and
#          converted-files.txt, the key proposal rules, and a flat JSON
#          reader/writer that rejects duplicate keys. Dot-source it from the
#          scripts in this folder; it does nothing on its own.
# Usage:   . "$PSScriptRoot\_common.ps1"
# Runs under Windows PowerShell 5.1: no &&, ||, ternary, ??, or ?. anywhere.

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
Add-Type -AssemblyName Microsoft.VisualBasic

$LocToolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LocRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $LocToolsDir '..\..'))
$LocXamlNs = [System.Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/winfx/2006/xaml')
$LocXNameName = $LocXamlNs + 'Name'
$LocXNameKey = $LocXamlNs + 'Key'
$LocLoadOptions = [System.Xml.Linq.LoadOptions]::SetLineInfo -bor [System.Xml.Linq.LoadOptions]::PreserveWhitespace
# en.json relative to a root: keys.ps1 and convert-xaml.ps1 build their -EnJson
# default as (Join-Path $Root $LocEnJsonRel), so the two cannot drift.
$LocEnJsonRel = 'src\TrueforceForAll.Plugin\Languages\en.json'
$LocXmlnsLoc = 'clr-namespace:TrueforceForAll.Plugin.Localization'
$LocKeyRegex = '^[A-Za-z][A-Za-z0-9_]*$'

# ---------------------------------------------------------------- paths

function Resolve-LocInputPath([string]$Path) {
    # Accepts an absolute path, a path relative to the current directory, or a
    # path relative to the repo root. Fails if none of those exists.
    if ([System.IO.Path]::IsPathRooted($Path)) {
        if (Test-Path -LiteralPath $Path) { return [System.IO.Path]::GetFullPath($Path) }
        throw "File not found: $Path"
    }
    $cwd = Join-Path (Get-Location).Path $Path
    if (Test-Path -LiteralPath $cwd) { return [System.IO.Path]::GetFullPath($cwd) }
    $repo = Join-Path $LocRepoRoot $Path
    if (Test-Path -LiteralPath $repo) { return [System.IO.Path]::GetFullPath($repo) }
    throw "File not found (tried current directory and repo root): $Path"
}

function Resolve-LocOutputPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Get-LocRelativePath([string]$Path, [string]$Root) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if ($full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($rootFull.Length).Replace('\', '/')
    }
    return $full.Replace('\', '/')
}

# ---------------------------------------------------------------- byte-level file IO

function Test-LocBytesEqual([byte[]]$A, [int]$AOffset, [byte[]]$B) {
    if (($A.Length - $AOffset) -ne $B.Length) { return $false }
    $sub = New-Object byte[] $B.Length
    [System.Array]::Copy($A, $AOffset, $sub, 0, $B.Length)
    return [System.Linq.Enumerable]::SequenceEqual([byte[]]$sub, [byte[]]$B)
}

function Read-LocTextFile([string]$Path) {
    # Returns Text (without the BOM), HasBom, Eol ("`r`n" or "`n") and the raw
    # Bytes. Decoding is strict UTF-8 and the decode/encode round trip is
    # asserted byte for byte, so a later splice can only change what it means to.
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $start = 0
    if ($hasBom) { $start = 3 }
    $enc = New-Object System.Text.UTF8Encoding($false, $true)
    $text = $enc.GetString($bytes, $start, $bytes.Length - $start)
    $back = $enc.GetBytes($text)
    if (-not (Test-LocBytesEqual $bytes $start $back)) {
        throw "UTF-8 round trip is not byte-identical for $Path; refusing to touch it."
    }
    $crlf = [regex]::Matches($text, "`r`n").Count
    $lf = [regex]::Matches($text, "(?<!`r)`n").Count
    $eol = "`n"
    if ($crlf -gt $lf) { $eol = "`r`n" }
    return [pscustomobject]@{ Path = $Path; Text = $text; HasBom = $hasBom; Eol = $eol; Bytes = $bytes; CrLf = $crlf; BareLf = $lf }
}

function Write-LocTextFile([string]$Path, [string]$Text, [bool]$Bom) {
    $enc = New-Object System.Text.UTF8Encoding($false, $true)
    $body = $enc.GetBytes($Text)
    $out = $body
    if ($Bom) {
        $out = New-Object byte[] ($body.Length + 3)
        $out[0] = 0xEF; $out[1] = 0xBB; $out[2] = 0xBF
        [System.Array]::Copy($body, 0, $out, 3, $body.Length)
    }
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir) }
    [System.IO.File]::WriteAllBytes($Path, $out)
}

function Write-LocNewFile([string]$Path, [string]$Text) {
    # New files this tooling creates are LF, UTF-8, no BOM.
    Write-LocTextFile $Path ($Text.Replace("`r`n", "`n")) $false
}

# ---------------------------------------------------------------- CSV

function ConvertTo-LocCsvField($Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + ([string]$Value).Replace('"', '""') + '"'
}

function Write-LocCsv([string]$Path, [string[]]$Columns, $Rows) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append((@($Columns | ForEach-Object { ConvertTo-LocCsvField $_ }) -join ',')).Append("`n")
    foreach ($r in $Rows) {
        $fields = New-Object System.Collections.Generic.List[string]
        foreach ($c in $Columns) {
            $p = $r.PSObject.Properties[$c]
            if ($null -eq $p) { $fields.Add('""') } else { $fields.Add((ConvertTo-LocCsvField $p.Value)) }
        }
        [void]$sb.Append(($fields -join ',')).Append("`n")
    }
    Write-LocNewFile $Path $sb.ToString()
}

function Read-LocCsv([string]$Path) {
    # RFC 4180 (quoted fields may hold commas, quotes and line breaks).
    $f = Read-LocTextFile $Path
    $reader = New-Object System.IO.StringReader($f.Text)
    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($reader)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    $rows = New-Object System.Collections.Generic.List[object]
    if ($parser.EndOfData) { return ,$rows }
    $header = $parser.ReadFields()
    while (-not $parser.EndOfData) {
        $fields = $parser.ReadFields()
        if ($fields.Count -ne $header.Count) {
            throw ("CSV row {0} in {1} has {2} fields, header has {3}." -f $rows.Count + 2, $Path, $fields.Count, $header.Count)
        }
        $o = New-Object psobject
        for ($i = 0; $i -lt $header.Count; $i++) {
            $o | Add-Member -NotePropertyName $header[$i] -NotePropertyValue $fields[$i]
        }
        $rows.Add($o)
    }
    $parser.Close()
    # The comma keeps PowerShell from unrolling the list into the pipeline.
    return ,$rows
}

# ---------------------------------------------------------------- list files

function Read-LocListFile([string]$Path) {
    # One value per line, verbatim. Blank lines and lines starting with '#' are
    # ignored. Returns a case-sensitive HashSet.
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Path)) { return ,$set }
    $f = Read-LocTextFile $Path
    foreach ($line in $f.Text.Split("`n")) {
        $t = $line.TrimEnd("`r")
        if ($t.Trim().Length -eq 0) { continue }
        if ($t.StartsWith('#')) { continue }
        [void]$set.Add($t)
    }
    return ,$set
}

# ---------------------------------------------------------------- XML text helpers

function ConvertFrom-LocXmlEscapes([string]$Raw) {
    if ($Raw.IndexOf('&') -lt 0) { return $Raw }
    $ev = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $e = $m.Groups[1].Value
        if ($e -eq 'amp') { return '&' }
        if ($e -eq 'lt') { return '<' }
        if ($e -eq 'gt') { return '>' }
        if ($e -eq 'quot') { return '"' }
        if ($e -eq 'apos') { return "'" }
        if ($e.StartsWith('#x')) { return [char]::ConvertFromUtf32([Convert]::ToInt32($e.Substring(2), 16)) }
        if ($e.StartsWith('#')) { return [char]::ConvertFromUtf32([int]$e.Substring(1)) }
        return $m.Value
    }
    return [regex]::Replace($Raw, '&(#x[0-9A-Fa-f]+|#[0-9]+|[A-Za-z]+);', $ev)
}

function ConvertTo-LocCollapsedText([string]$Text) {
    # What WPF shows for element text: line ends normalized, runs of whitespace
    # collapsed to one space, ends trimmed.
    return [regex]::Replace($Text.Replace("`r`n", "`n"), '\s+', ' ').Trim()
}

# ---------------------------------------------------------------- XAML walking

function Get-LocXamlDoc([string]$Text) {
    return [System.Xml.Linq.XDocument]::Parse($Text, $LocLoadOptions)
}

function Get-LocLine($Node) {
    return ([System.Xml.IXmlLineInfo]$Node).LineNumber
}

function Get-LocElementTypeName($El) {
    $prefix = $El.GetPrefixOfNamespace($El.Name.Namespace)
    if ([string]::IsNullOrEmpty($prefix)) { return $El.Name.LocalName }
    return ($prefix + ':' + $El.Name.LocalName)
}

function Get-LocAttributeName($El, $Attr) {
    if ($Attr.Name.Namespace -eq [System.Xml.Linq.XNamespace]::None) { return $Attr.Name.LocalName }
    $prefix = $El.GetPrefixOfNamespace($Attr.Name.Namespace)
    if ([string]::IsNullOrEmpty($prefix)) { return $Attr.Name.LocalName }
    return ($prefix + ':' + $Attr.Name.LocalName)
}

function Get-LocOwnName($El) {
    # x:Name, else x:Key (Styles), else $null.
    $a = $El.Attribute($LocXNameName)
    if ($null -ne $a) { return $a.Value }
    $k = $El.Attribute($LocXNameKey)
    if ($null -ne $k) { return $k.Value }
    return $null
}

function Get-LocOrdinalStep($El) {
    $i = 1
    foreach ($s in $El.ElementsBeforeSelf()) { if ($s.Name -eq $El.Name) { $i++ } }
    return ((Get-LocElementTypeName $El) + '[' + $i + ']')
}

function Get-LocElementPath($El) {
    # The element's own x:Name, else the nearest named ancestor's name plus an
    # ordinal path down to the element, e.g. "AudioExpander/StackPanel[1]/Grid[2]/TextBlock[1]".
    $own = Get-LocOwnName $El
    if ($null -ne $own) { return $own }
    $steps = New-Object System.Collections.Generic.List[string]
    $cur = $El
    while ($null -ne $cur) {
        if (-not [object]::ReferenceEquals($cur, $El)) {
            $n = Get-LocOwnName $cur
            if ($null -ne $n) { $steps.Insert(0, $n); break }
        }
        $steps.Insert(0, (Get-LocOrdinalStep $cur))
        $cur = $cur.Parent
    }
    return ($steps -join '/')
}

function Get-LocNamedAncestor($El) {
    foreach ($a in $El.Ancestors()) {
        $n = Get-LocOwnName $a
        if ($null -ne $n) { return $a }
    }
    return $null
}

function Get-LocTabItem($El) {
    foreach ($a in $El.AncestorsAndSelf()) { if ($a.Name.LocalName -eq 'SHTabItem') { return $a } }
    return $null
}

function Test-LocInResources($El) {
    foreach ($a in $El.Ancestors()) { if ($a.Name.LocalName.EndsWith('.Resources')) { return $true } }
    return $false
}

function Get-LocElementText($El) {
    # Text attribute if present, else the visible text of its inline content
    # (text nodes plus Run Text attributes), collapsed the way WPF shows it.
    $t = $El.Attribute('Text')
    if ($null -ne $t) { return $t.Value }
    $sb = New-Object System.Text.StringBuilder
    foreach ($n in $El.DescendantNodes()) {
        if ($n.NodeType -eq [System.Xml.XmlNodeType]::Text) { [void]$sb.Append($n.Value) }
        elseif ($n.NodeType -eq [System.Xml.XmlNodeType]::Element) {
            $rt = $n.Attribute('Text')
            if ($null -ne $rt -and -not $rt.Value.StartsWith('{')) { [void]$sb.Append($rt.Value) }
        }
    }
    return (ConvertTo-LocCollapsedText $sb.ToString())
}

# ---------------------------------------------------------------- denylist and the real-string rule

# Identifier and layout attributes named in the tooling contract.
$LocDenyContract = @(
    'x:Name', 'x:Key', 'x:Class', 'x:Uid', 'Tag', 'GroupName', 'Name', 'Property', 'TargetType', 'TargetName',
    'Style', 'BasedOn', 'Key', 'SharedSizeGroup',
    'Click', 'Checked', 'Unchecked', 'SelectionChanged', 'TextChanged', 'KeyDown', 'LostFocus', 'Loaded',
    'MouseDown', 'PreviewMouseDown', 'ValueChanged', 'Expanded', 'Collapsed', 'Closed', 'RequestNavigate',
    'Grid.Row', 'Grid.Column', 'Grid.RowSpan', 'Grid.ColumnSpan', 'Margin', 'Padding', 'Width', 'Height',
    'MinWidth', 'MaxWidth', 'MinHeight', 'MaxHeight', 'FontSize', 'FontWeight', 'FontFamily', 'Opacity',
    'Foreground', 'Background', 'BorderBrush', 'BorderThickness', 'CornerRadius', 'Fill', 'Stroke', 'Stretch',
    'Orientation', 'HorizontalAlignment', 'VerticalAlignment', 'Visibility', 'IsEnabled', 'IsChecked',
    'IsExpanded', 'SelectedIndex', 'Minimum', 'Maximum', 'Value', 'TickFrequency', 'Interval', 'Cursor',
    'Focusable', 'IsTabStop', 'TextWrapping', 'TextTrimming', 'TextAlignment', 'Source', 'Data', 'Points',
    'StrokeThickness', 'RenderTransformOrigin', 'Panel.ZIndex', 'Command', 'CommandParameter', 'ItemsSource',
    'DisplayMemberPath', 'SelectedValuePath', 'Binding', 'Path', 'ElementName', 'UpdateSourceTrigger', 'Mode',
    'Converter'
)
# Additions found while running the inventory on the real files: more event
# handlers, and enum-valued layout attributes whose values are English words
# ("CharacterEllipsis", "CenterOwner") but never display text. ActionName is a
# SimHub action identifier ("TrueforcePlugin.MasterGainUp"); FriendlyName beside
# it is display text and stays a candidate.
$LocDenyAdded = @(
    'Unloaded', 'MouseMove', 'MouseLeave', 'MouseEnter', 'MouseLeftButtonUp', 'MouseLeftButtonDown',
    'MouseRightButtonUp', 'MouseDoubleClick', 'LostKeyboardFocus', 'GotFocus', 'GotKeyboardFocus',
    'DropDownClosed', 'DropDownOpened', 'LoadingRow', 'KeyUp', 'PreviewKeyDown', 'PreviewKeyUp', 'Initialized',
    'ActionName', 'FontStyle', 'FontStretch', 'DockPanel.Dock', 'SelectionMode', 'SelectionUnit',
    'HeadersVisibility', 'GridLinesVisibility', 'WindowStartupLocation', 'ResizeMode', 'SizeToContent',
    'Placement', 'Color', 'TextDecorations', 'TextElement.Foreground', 'FocusVisualStyle', 'CaretBrush', 'mc:Ignorable',
    'd:DesignHeight', 'd:DesignWidth', 'SmallChange', 'LargeChange', 'Angle', 'MaxLength', 'MaxDropDownHeight',
    'RowHeaderWidth', 'BlurRadius', 'ShadowDepth', 'Columns', 'Rows'
)
$LocDenySet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($n in $LocDenyContract) { [void]$LocDenySet.Add($n) }
foreach ($n in $LocDenyAdded) { [void]$LocDenySet.Add($n) }

function Test-LocDenied([string]$AttributeName) {
    if ($LocDenySet.Contains($AttributeName)) { return $true }
    if ($AttributeName.StartsWith('xmlns')) { return $true }
    if ($AttributeName.EndsWith('Changed')) { return $true }
    if ($AttributeName.EndsWith('Alignment')) { return $true }
    if ($AttributeName.EndsWith('Visibility')) { return $true }
    if ($AttributeName.EndsWith('Brush')) { return $true }
    if ($AttributeName.EndsWith('Style')) { return $true }
    if ($AttributeName.EndsWith('Thickness')) { return $true }
    if ($AttributeName.StartsWith('ToolTipService.')) { return $true }
    if ($AttributeName.StartsWith('ScrollViewer.')) { return $true }
    return $false
}

# The short-token rule: a value of two or three letters ("Off", "RPM", "Set")
# is a real string only on a display attribute of a display element. The two
# lists are the rule; the C# walker in Core.Tests (LocalizationTests.cs)
# carries the same two lists.
$LocShortTokenAttributes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($n in @('Content', 'Header', 'Text', 'Title', '#text')) { [void]$LocShortTokenAttributes.Add($n) }
$LocShortTokenElements = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($n in @('ComboBoxItem', 'RadioButton', 'Button', 'CheckBox', 'TextBlock', 'Label', 'Run', 'Hyperlink',
        'MenuItem', 'TabItem', 'SHTabItem', 'GroupBox', 'Expander')) { [void]$LocShortTokenElements.Add($n) }

function Get-LocSkipReason([string]$Value, $CommonSet, [string]$ElementName = '', [string]$Attribute = '') {
    # '' means a real string. Otherwise one of: binding, numeric, glyph,
    # identifier, empty. A value with at least one letter is real when any of
    # these holds: it contains whitespace; it has four or more letters; it is
    # listed verbatim in common-keys.txt ("NEW", "OK"); or it has two or more
    # letters on a display attribute ($LocShortTokenAttributes) of a display
    # element ($LocShortTokenElements). ElementName is the element's local
    # name and Attribute the inventory column, so "Setter:Text" on a Setter
    # never qualifies and a single-letter value ("R") stays glyph.
    if ($null -eq $Value) { return 'empty' }
    $t = $Value.Trim()
    if ($t.Length -eq 0) { return 'empty' }
    if ($t.StartsWith('{')) { return 'binding' }
    if ($t -eq 'True' -or $t -eq 'False') { return 'identifier' }
    $letters = 0
    $digits = 0
    foreach ($ch in $t.ToCharArray()) {
        if ([char]::IsLetter($ch)) { $letters++ }
        elseif ([char]::IsDigit($ch)) { $digits++ }
    }
    if ($letters -eq 0) {
        if ($digits -gt 0) { return 'numeric' }
        return 'glyph'
    }
    if ($t -match '\s') { return '' }
    if ($letters -ge 4) { return '' }
    if ($null -ne $CommonSet -and $CommonSet.Contains($Value)) { return '' }
    if ($letters -ge 2 -and $LocShortTokenAttributes.Contains($Attribute) -and $LocShortTokenElements.Contains($ElementName)) { return '' }
    return 'glyph'
}

# ---------------------------------------------------------------- inventory

$LocInventoryColumns = @('file', 'line', 'elementType', 'xName', 'attribute', 'value', 'skipReason')

function Get-LocInventory([string]$Path, [string]$RelPath, $CommonSet) {
    # One row per candidate string. Denylisted attributes are not candidates
    # and are counted in .Denied instead. Whitespace-only text nodes are
    # formatting, not candidates. Rows carry Element and Node for the callers
    # that need the live XML (keys.ps1, convert-xaml.ps1); inventory.ps1 writes
    # only the CSV columns.
    $f = Read-LocTextFile $Path
    $doc = Get-LocXamlDoc $f.Text
    $rows = New-Object System.Collections.Generic.List[object]
    $denied = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
    foreach ($el in $doc.Root.DescendantsAndSelf()) {
        $type = Get-LocElementTypeName $el
        $xname = Get-LocElementPath $el
        $isSetter = ($el.Name.LocalName -eq 'Setter')
        $setterProp = $null
        if ($isSetter) {
            $pa = $el.Attribute('Property')
            if ($null -ne $pa) { $setterProp = $pa.Value }
        }
        foreach ($a in $el.Attributes()) {
            $aname = Get-LocAttributeName $el $a
            if ($a.IsNamespaceDeclaration) { $aname = 'xmlns' }
            $col = $aname
            $deny = $false
            if ($isSetter -and $a.Name.LocalName -eq 'Value' -and $a.Name.Namespace -eq [System.Xml.Linq.XNamespace]::None -and $null -ne $setterProp) {
                $col = 'Setter:' + $setterProp
                $deny = Test-LocDenied $setterProp
            } else {
                $deny = Test-LocDenied $aname
            }
            if ($deny) {
                if ($denied.ContainsKey($col)) { $denied[$col] = $denied[$col] + 1 } else { $denied[$col] = 1 }
                continue
            }
            $rows.Add([pscustomobject]@{
                file = $RelPath; line = (Get-LocLine $a); elementType = $type; xName = $xname
                attribute = $col; value = $a.Value; skipReason = (Get-LocSkipReason $a.Value $CommonSet $el.Name.LocalName $col)
                Element = $el; Node = $a
            })
        }
        foreach ($n in $el.Nodes()) {
            if ($n.NodeType -ne [System.Xml.XmlNodeType]::Text) { continue }
            $raw = $n.Value
            if ([string]::IsNullOrWhiteSpace($raw)) { continue }
            $collapsed = ConvertTo-LocCollapsedText $raw
            # Line of the first visible character, not of the node start.
            $lead = $raw.Length - $raw.TrimStart().Length
            $line = (Get-LocLine $n) + ([regex]::Matches($raw.Substring(0, $lead), "`n").Count)
            $rows.Add([pscustomobject]@{
                file = $RelPath; line = $line; elementType = $type; xName = $xname
                attribute = '#text'; value = $collapsed; skipReason = (Get-LocSkipReason $collapsed $CommonSet $el.Name.LocalName '#text')
                Element = $el; Node = $n
            })
        }
    }
    return [pscustomobject]@{ File = $f; Doc = $doc; Rows = $rows; Denied = $denied }
}

function Get-LocRowKey($Row) {
    return ('{0}|{1}|{2}|{3}' -f $Row.line, $Row.attribute, $Row.xName, $Row.value)
}

function Get-LocInventoryIndex($Inventory) {
    $idx = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    foreach ($r in $Inventory.Rows) {
        $k = Get-LocRowKey $r
        if ($idx.ContainsKey($k)) { throw "Inventory rows are not unique: $k" }
        $idx[$k] = $r
    }
    return $idx
}

# ---------------------------------------------------------------- scopes and key proposal rules

# Scope specs name a part of a XAML file. keys.ps1 -Scope takes them, and a
# converted-files.txt line carries them after a '|' when only part of a file
# is converted (ConvertFrom-LocConvertedEntry). <name> is the XAML file name
# or its repo-relative path.
#   file:<name>        the whole file (keys.ps1 only; converted-files.txt
#                      writes a whole file as a bare path)
#   resources:<name>   elements inside an element whose local name ends with
#                      ".Resources" (UserControl.Resources, Window.Resources)
#   header:<name>      elements before the first sh:SHTabItem and not inside
#                      Resources; in a file without tabs, everything outside
#                      Resources
#   tab:<x:Name>       descendants of the sh:SHTabItem with that x:Name
#   trailer:<name>     elements after the last sh:SHTabItem
$LocScopeKinds = @('file', 'resources', 'header', 'tab', 'trailer')

$LocScopeContextCache = New-Object 'System.Collections.Generic.Dictionary[object,object]'

function Get-LocScopeContext($Doc) {
    # The first and last SHTabItem of a document ($null when it has none),
    # cached per document because Get-LocArea asks once per row.
    if ($LocScopeContextCache.ContainsKey($Doc)) { return $LocScopeContextCache[$Doc] }
    $first = $null
    $last = $null
    foreach ($t in $Doc.Root.Descendants()) {
        if ($t.Name.LocalName -ne 'SHTabItem') { continue }
        if ($null -eq $first) { $first = $t }
        $last = $t
    }
    $ctx = [pscustomobject]@{ FirstTab = $first; LastTab = $last }
    $LocScopeContextCache[$Doc] = $ctx
    return $ctx
}

function Get-LocSection($El, $Ctx) {
    # Where an element sits in its document, one kind per element: resources
    # (inside an X.Resources element; checked first), tab (inside an
    # SHTabItem; Arg is its x:Name, '' when unnamed), header (before the first
    # SHTabItem), trailer (after the last), file (the document has no
    # SHTabItem) or between (outside every tab yet between two of them; no
    # spec covers such an element).
    if (Test-LocInResources $El) { return [pscustomobject]@{ Kind = 'resources'; Arg = '' } }
    $tab = Get-LocTabItem $El
    if ($null -ne $tab) {
        $tn = Get-LocOwnName $tab
        if ($null -eq $tn) { $tn = '' }
        return [pscustomobject]@{ Kind = 'tab'; Arg = $tn }
    }
    if ($null -eq $Ctx.FirstTab) { return [pscustomobject]@{ Kind = 'file'; Arg = '' } }
    if ($El.IsBefore($Ctx.FirstTab)) { return [pscustomobject]@{ Kind = 'header'; Arg = '' } }
    if ($El.IsAfter($Ctx.LastTab)) { return [pscustomobject]@{ Kind = 'trailer'; Arg = '' } }
    return [pscustomobject]@{ Kind = 'between'; Arg = '' }
}

function Split-LocScopeSpec([string]$Spec) {
    # kind:value with both parts present, as ReadConvertedFiles in
    # LocalizationTests.cs reads it: 'tab:' with nothing after the colon is an error.
    $i = $Spec.IndexOf(':')
    if ($i -lt 1 -or $i -eq $Spec.Length - 1) { throw "Bad scope spec '$Spec' (expected kind:value)." }
    $kind = $Spec.Substring(0, $i).ToLowerInvariant()
    if ($LocScopeKinds -notcontains $kind) { throw "Unknown scope kind '$kind' in '$Spec' (one of: $($LocScopeKinds -join ', '))." }
    return [pscustomobject]@{ Kind = $kind; Arg = $Spec.Substring($i + 1) }
}

function Test-LocSpecMatch($Row, [string]$Spec, $Ctx) {
    # Whether one scope spec covers an inventory row.
    $s = Split-LocScopeSpec $Spec
    if ($s.Kind -eq 'tab') {
        $tab = Get-LocTabItem $Row.Element
        if ($null -eq $tab) { return $false }
        return ((Get-LocOwnName $tab) -ceq $s.Arg)
    }
    $fileName = [System.IO.Path]::GetFileName($Row.file)
    if (-not ($fileName -eq $s.Arg -or $Row.file -eq $s.Arg.Replace('\', '/'))) { return $false }
    if ($s.Kind -eq 'file') { return $true }
    if ($s.Kind -eq 'resources') { return (Test-LocInResources $Row.Element) }
    $sec = Get-LocSection $Row.Element $Ctx
    if ($s.Kind -eq 'header') { return ($sec.Kind -eq 'header' -or $sec.Kind -eq 'file') }
    return ($sec.Kind -eq 'trailer')
}

function Test-LocInScope($Row, [string[]]$Specs, $Ctx) {
    foreach ($spec in $Specs) { if (Test-LocSpecMatch $Row $spec $Ctx) { return $true } }
    return $false
}

function Get-LocRowScopeSpec($Row, $Ctx) {
    # The converted-files.txt spec that covers a row once it is converted: ''
    # for the whole file (a document without SHTabItems is one unit, its
    # Resources included), else resources:<file>, header:<file>, tab:<x:Name>
    # or trailer:<file>. $null when no spec can name the element.
    if ($null -eq $Ctx.FirstTab) { return '' }
    $name = [System.IO.Path]::GetFileName($Row.file)
    $sec = Get-LocSection $Row.Element $Ctx
    if ($sec.Kind -eq 'resources') { return ('resources:' + $name) }
    if ($sec.Kind -eq 'header') { return ('header:' + $name) }
    if ($sec.Kind -eq 'trailer') { return ('trailer:' + $name) }
    if ($sec.Kind -eq 'tab' -and $sec.Arg.Length -gt 0) { return ('tab:' + $sec.Arg) }
    return $null
}

function ConvertFrom-LocConvertedEntry([string]$Line) {
    # One converted-files.txt line: 'path' (the whole file is converted) or
    # 'path|spec,spec' (only those scopes are). Specs is $null for a whole
    # file. Paths come back repo-relative with forward slashes.
    $t = $Line.Trim()
    $bar = $t.IndexOf('|')
    if ($bar -lt 0) { return [pscustomobject]@{ Path = $t.Replace('\', '/'); Specs = $null } }
    $path = $t.Substring(0, $bar).Trim().Replace('\', '/')
    $specs = @($t.Substring($bar + 1).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
    if ($path.Length -eq 0 -or $specs.Count -eq 0) { throw "converted-files.txt: '$Line' needs a path and at least one scope spec after '|'." }
    foreach ($s in $specs) {
        if ((Split-LocScopeSpec $s).Kind -eq 'file') { throw "converted-files.txt: '$Line' uses file:; a whole file is listed as a bare path." }
    }
    return [pscustomobject]@{ Path = $path; Specs = $specs }
}

function ConvertTo-LocConvertedEntry([string]$Path, $Specs) {
    # The line for a path: bare for a whole file, else 'path|spec,spec' with
    # the specs deduplicated and in ordinal order.
    if ($null -eq $Specs) { return $Path }
    [string[]]$arr = @($Specs | Select-Object -Unique)
    if ($arr.Count -gt 1) { [System.Array]::Sort($arr, [System.StringComparer]::Ordinal) }
    return ($Path + '|' + ($arr -join ','))
}

function Read-LocConvertedList([string]$Path) {
    # Every entry of converted-files.txt in file order; blank and '#' lines
    # are skipped. Throws when a path is listed twice.
    $entries = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Path)) { return ,$entries }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($line in (Read-LocTextFile $Path).Text.Split("`n")) {
        $t = $line.TrimEnd("`r")
        if ($t.Trim().Length -eq 0) { continue }
        if ($t.StartsWith('#')) { continue }
        $e = ConvertFrom-LocConvertedEntry $t
        if (-not $seen.Add($e.Path)) { throw "converted-files.txt lists $($e.Path) twice." }
        $entries.Add($e)
    }
    return ,$entries
}

$LocFileAreas = @{ 'PresetManagerControl' = 'PresetManager'; 'CustomEngineEditor' = 'EngineEditor'; 'MotdStrip' = 'Motd' }

function Get-LocArea($Row) {
    # In a file with SHTabItems: Resources, Header, Trailer or the tab's stem
    # ("SupportTab" -> "Support"; an element between tabs falls back to
    # Header). A file without tabs is one area, named in $LocFileAreas or
    # after the file ("FooControl" -> "Foo").
    $ctx = Get-LocScopeContext $Row.Element.Document
    if ($null -ne $ctx.FirstTab) {
        $sec = Get-LocSection $Row.Element $ctx
        if ($sec.Kind -eq 'resources') { return 'Resources' }
        if ($sec.Kind -eq 'trailer') { return 'Trailer' }
        if ($sec.Kind -eq 'tab' -and $sec.Arg.Length -gt 0) {
            if ($sec.Arg.EndsWith('Tab') -and $sec.Arg.Length -gt 3) { return $sec.Arg.Substring(0, $sec.Arg.Length - 3) }
            return $sec.Arg
        }
        return 'Header'
    }
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Row.file)
    if ($LocFileAreas.ContainsKey($stem)) { return $LocFileAreas[$stem] }
    return ($stem -replace 'Control$', '')
}

# Longest first so CheckBox wins over Box and TextBlock over Text.
$LocNameSuffixes = @('TextBlock', 'CheckBox', 'ComboBox', 'Expander', 'Button', 'Slider', 'Status', 'Header', 'Panel', 'Label',
    'Check', 'Combo', 'Text', 'Hint', 'Help', 'Link', 'Btn', 'Box')

function Get-LocStrippedStem([string]$Name) {
    foreach ($s in $LocNameSuffixes) {
        if ($Name.Length -gt $s.Length -and $Name.EndsWith($s)) { return $Name.Substring(0, $Name.Length - $s.Length) }
    }
    return $Name
}

function ConvertTo-LocPascalWords([string]$Value, [int]$MinWords, [int]$MaxWords, [int]$MaxChars) {
    # Letters and digits only. Takes MinWords words, keeps going to MaxWords
    # while the result is still short, and cuts at MaxChars.
    $words = [regex]::Split($Value, '[^\p{L}\p{Nd}]+') | Where-Object { $_.Length -gt 0 }
    $sb = New-Object System.Text.StringBuilder
    $n = 0
    foreach ($w in $words) {
        if ($n -ge $MaxWords) { break }
        if ($n -ge $MinWords -and $sb.Length -ge 16) { break }
        $piece = $w
        if ($w.Length -gt 1 -and $w -ceq $w.ToUpperInvariant()) { $piece = $w.Substring(0, 1) + $w.Substring(1).ToLowerInvariant() }
        else { $piece = $w.Substring(0, 1).ToUpperInvariant() + $w.Substring(1) }
        [void]$sb.Append($piece)
        $n++
    }
    $out = $sb.ToString()
    $out = [regex]::Replace($out, '[^A-Za-z0-9]', '')
    if ($out.Length -gt $MaxChars) { $out = $out.Substring(0, $MaxChars) }
    return $out
}

function Get-LocCommonSlug([string]$Value) {
    $slug = ConvertTo-LocPascalWords $Value 6 6 40
    if ($Value.Contains([string][char]0x2026) -or $Value.Contains('...')) { $slug = $slug + 'Ellipsis' }
    return $slug
}

function Get-LocKeySuffix([string]$Attribute) {
    $a = $Attribute
    if ($a.StartsWith('Setter:')) { $a = $a.Substring(7) }
    switch ($a) {
        'ToolTip' { return '_Tip' }
        'Header' { return '_Header' }
        'Title' { return '_Title' }
        'Watermark' { return '_Watermark' }
        'Text' { return '' }
        'Content' { return '' }
        '#text' { return '' }
        default {
            $local = $a
            $dot = $a.LastIndexOf('.')
            if ($dot -ge 0) { $local = $a.Substring($dot + 1) }
            return ('_' + [regex]::Replace($local, '[^A-Za-z0-9]', ''))
        }
    }
}

function Get-LocProposedKey($Row, $CommonSet, [switch]$Long) {
    # Returns the key before uniqueness handling, plus whether it is shared.
    # -Long takes six words of the text instead of four; keys.ps1 tries it on
    # a collision between unnamed elements before falling back to _2.
    $minWords = 4
    if ($Long) { $minWords = 6 }
    if ($null -ne $CommonSet -and $CommonSet.Contains($Row.value)) {
        return [pscustomobject]@{ Key = ('Common_' + (Get-LocCommonSlug $Row.value)); Shared = $true }
    }
    $area = Get-LocArea $Row
    $el = $Row.Element
    $stem = ''
    $own = $null
    if ($el.Name.LocalName -ne 'Setter') { $own = Get-LocOwnName $el }
    if ($null -ne $own) {
        $stem = Get-LocStrippedStem $own
    } else {
        $anc = Get-LocNamedAncestor $el
        $ancStem = ''
        if ($null -ne $anc -and $anc.Name.LocalName -ne 'SHTabItem' -and $null -ne $anc.Parent) {
            $ancStem = Get-LocStrippedStem (Get-LocOwnName $anc)
        }
        # An unnamed element's ToolTip (or Header, Title, ...) is named after
        # the element's own Content or Text when it has one, so "Export..." and
        # its tooltip become X_Export and X_Export_Tip.
        $source = $Row.value
        if ($Row.attribute -ne 'Text' -and $Row.attribute -ne 'Content' -and $Row.attribute -ne '#text' -and $el.Name.LocalName -ne 'Setter') {
            foreach ($cand in @('Content', 'Text')) {
                $ca = $el.Attribute($cand)
                if ($null -ne $ca -and (Get-LocSkipReason $ca.Value $CommonSet $el.Name.LocalName $cand) -eq '') { $source = $ca.Value; break }
            }
        }
        $words = ConvertTo-LocPascalWords $source $minWords 6 40
        # Do not repeat the ancestor's tail: CarFacts + "Car Facts" is CarFacts,
        # CarFactsPerGear + "Per-Gear Redlines" is CarFactsPerGearRedlines.
        if ($ancStem.Length -gt 0 -and $words.Length -gt 0) {
            $pieces = @([regex]::Matches($words, '[A-Z][a-z0-9]*|[0-9]+|[a-z]+') | ForEach-Object { $_.Value })
            for ($k = [Math]::Min($pieces.Count, 4); $k -ge 1; $k--) {
                $head = ($pieces[0..($k - 1)] -join '')
                if ($head.Length -gt 0 -and $ancStem.EndsWith($head)) {
                    $words = $words.Substring($head.Length)
                    break
                }
            }
        }
        $stem = $ancStem + $words
    }
    $stem = [regex]::Replace($stem, '[^A-Za-z0-9]', '')
    if ($stem.Length -eq 0) { $stem = ConvertTo-LocPascalWords $Row.value 4 6 40 }
    if ($stem.Length -eq 0) { $stem = 'Text' }
    return [pscustomobject]@{ Key = ($area + '_' + $stem + (Get-LocKeySuffix $Row.attribute)); Shared = $false }
}

# ---------------------------------------------------------------- flat JSON (duplicate keys rejected)

function ConvertFrom-LocJsonString([string]$Quoted) {
    $inner = $Quoted.Substring(1, $Quoted.Length - 2)
    if ($inner.IndexOf('\') -lt 0) { return $inner }
    $ev = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $e = $m.Groups[1].Value
        switch ($e.Substring(0, 1)) {
            'u' { return [string][char][Convert]::ToInt32($e.Substring(1), 16) }
            'n' { return "`n" }
            'r' { return "`r" }
            't' { return "`t" }
            'b' { return [string][char]8 }
            'f' { return [string][char]12 }
            default { return $e }
        }
    }
    return [regex]::Replace($inner, '\\(u[0-9a-fA-F]{4}|.)', $ev)
}

function ConvertTo-LocJsonString([string]$Value) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $Value.ToCharArray()) {
        $c = [int]$ch
        if ($ch -eq '"') { [void]$sb.Append('\"') }
        elseif ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($c -eq 10) { [void]$sb.Append('\n') }
        elseif ($c -eq 13) { [void]$sb.Append('\r') }
        elseif ($c -eq 9) { [void]$sb.Append('\t') }
        elseif ($c -lt 32) { [void]$sb.Append('\u' + $c.ToString('x4')) }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-LocJsonTokens([string]$Text) {
    $re = [regex]'\G\s*(?:("(?:[^"\\]|\\.)*")|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(true|false|null)|([{}\[\],:]))'
    $tokens = New-Object System.Collections.Generic.List[object]
    $pos = 0
    while ($pos -lt $Text.Length) {
        if ($Text.Substring($pos).Trim().Length -eq 0) { break }
        $m = $re.Match($Text, $pos)
        if (-not $m.Success) { throw ("JSON syntax error at offset {0}: '{1}'" -f $pos, $Text.Substring($pos, [Math]::Min(20, $Text.Length - $pos))) }
        $type = 'punct'
        if ($m.Groups[1].Success) { $type = 'str' }
        elseif ($m.Groups[2].Success) { $type = 'num' }
        elseif ($m.Groups[3].Success) { $type = 'lit' }
        $tokens.Add([pscustomobject]@{ Type = $type; Text = $m.Value.TrimStart() })
        $pos = $m.Index + $m.Length
    }
    return ,$tokens
}

function Read-LocJsonValue($Tokens, [ref]$I) {
    if ($I.Value -ge $Tokens.Count) { throw 'JSON ended early.' }
    $t = $Tokens[$I.Value]
    $I.Value++
    if ($t.Type -eq 'str') { return (ConvertFrom-LocJsonString $t.Text) }
    if ($t.Type -eq 'num') { return [double]$t.Text }
    if ($t.Type -eq 'lit') {
        if ($t.Text -eq 'true') { return $true }
        if ($t.Text -eq 'false') { return $false }
        return $null
    }
    if ($t.Text -eq '{') {
        $obj = New-Object System.Collections.Specialized.OrderedDictionary
        if ($Tokens[$I.Value].Type -eq 'punct' -and $Tokens[$I.Value].Text -eq '}') { $I.Value++; return $obj }
        while ($true) {
            $k = $Tokens[$I.Value]
            if ($k.Type -ne 'str') { throw "JSON object key expected, got '$($k.Text)'." }
            $I.Value++
            $key = ConvertFrom-LocJsonString $k.Text
            $colon = $Tokens[$I.Value]
            if ($colon.Type -ne 'punct' -or $colon.Text -ne ':') { throw "JSON ':' expected after key '$key'." }
            $I.Value++
            $v = Read-LocJsonValue $Tokens $I
            if ($obj.Contains($key)) { throw "Duplicate JSON key '$key'." }
            $obj[$key] = $v
            $sep = $Tokens[$I.Value]
            $I.Value++
            if ($sep.Type -eq 'punct' -and $sep.Text -eq ',') { continue }
            if ($sep.Type -eq 'punct' -and $sep.Text -eq '}') { return $obj }
            throw "JSON ',' or '}' expected after key '$key'."
        }
    }
    if ($t.Text -eq '[') {
        $arr = New-Object System.Collections.Generic.List[object]
        if ($Tokens[$I.Value].Type -eq 'punct' -and $Tokens[$I.Value].Text -eq ']') { $I.Value++; return ,$arr }
        while ($true) {
            $arr.Add((Read-LocJsonValue $Tokens $I))
            $sep = $Tokens[$I.Value]
            $I.Value++
            if ($sep.Type -eq 'punct' -and $sep.Text -eq ',') { continue }
            if ($sep.Type -eq 'punct' -and $sep.Text -eq ']') { return ,$arr }
            throw "JSON ',' or ']' expected in array."
        }
    }
    throw "Unexpected JSON token '$($t.Text)'."
}

function Read-LocJson([string]$Path) {
    # Parses a language file into an OrderedDictionary (case-sensitive keys).
    # Throws on the first duplicate key, like Newtonsoft with
    # DuplicatePropertyNameHandling.Error.
    $f = Read-LocTextFile $Path
    $tokens = Get-LocJsonTokens $f.Text
    $i = 0
    $v = Read-LocJsonValue $tokens ([ref]$i)
    if ($i -ne $tokens.Count) { throw "Trailing JSON content in $Path." }
    if (-not ($v -is [System.Collections.Specialized.OrderedDictionary])) { throw "$Path is not a JSON object." }
    return $v
}

function ConvertTo-LocJsonText($Value, [int]$Indent) {
    $pad = ' ' * (2 * $Indent)
    $padIn = ' ' * (2 * ($Indent + 1))
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    if ($Value -is [string]) { return (ConvertTo-LocJsonString $Value) }
    if ($Value -is [System.Collections.Specialized.OrderedDictionary]) {
        if ($Value.Count -eq 0) { return '{}' }
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($k in $Value.Keys) {
            $parts.Add($padIn + (ConvertTo-LocJsonString ([string]$k)) + ': ' + (ConvertTo-LocJsonText $Value[$k] ($Indent + 1)))
        }
        return ("{`n" + ($parts -join ",`n") + "`n" + $pad + '}')
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($item in $Value) { $parts.Add($padIn + (ConvertTo-LocJsonText $item ($Indent + 1))) }
        if ($parts.Count -eq 0) { return '[]' }
        return ("[`n" + ($parts -join ",`n") + "`n" + $pad + ']')
    }
    return ([string]$Value)
}

function Write-LocJson([string]$Path, $Dict) {
    # "_meta" first, then every other key in ordinal order, 2-space indent,
    # LF, no BOM.
    $ordered = New-Object System.Collections.Specialized.OrderedDictionary
    if ($Dict.Contains('_meta')) { $ordered['_meta'] = $Dict['_meta'] }
    # Typed as string[]: an object[] of PSObjects makes Array.Sort fall back to
    # a culture-aware compare and the order stops being ordinal.
    [string[]]$keys = @($Dict.Keys | Where-Object { $_ -ne '_meta' })
    if ($keys.Count -gt 0) { [System.Array]::Sort($keys, [System.StringComparer]::Ordinal) }
    foreach ($k in $keys) { $ordered[$k] = $Dict[$k] }
    Write-LocNewFile $Path ((ConvertTo-LocJsonText $ordered 0) + "`n")
}

function Get-LocPlaceholders([string]$Value) {
    # The {n} and {n:format} tokens of a string, sorted, joined by '|'.
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($Value, '\{\d+(?::[^{}]*)?\}')) { $list.Add($m.Value) }
    $arr = $list.ToArray()
    if ($arr.Length -gt 0) { [System.Array]::Sort($arr, [System.StringComparer]::Ordinal) }
    return ($arr -join '|')
}

function Get-LocMaxPlaceholderIndex([string]$Value) {
    $max = -1
    foreach ($m in [regex]::Matches($Value, '\{(\d+)(?::[^{}]*)?\}')) {
        $n = [int]$m.Groups[1].Value
        if ($n -gt $max) { $max = $n }
    }
    return $max
}
