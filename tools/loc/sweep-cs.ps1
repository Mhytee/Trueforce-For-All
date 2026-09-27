# tools/loc/sweep-cs.ps1
#
# Purpose: the C# half of the no-literal check, which validate.ps1 does not do.
#          validate.ps1 asks whether every key a call names exists; this asks the
#          opposite question, the one Phase 2 is measured by: is any string a
#          user reads still a bare English literal in code? It finds every
#          literal that reaches a UI sink (tools/loc/cs-ui-sinks.txt says what a
#          sink is) and is not routed through Loc.T/F/N, prints the count per
#          file and a total, and holds the per-file numbers against
#          tools/loc/cs-literal-budget.txt so the remainder can only shrink.
#          Comments are blanked before anything is matched, so a sink quoted in
#          one is never counted.
# Usage:   .\sweep-cs.ps1                 counts, then checks the budget
#          .\sweep-cs.ps1 -Detail         every remaining literal, file by file
#          .\sweep-cs.ps1 -Detail -Only SettingsControl.xaml.cs
#          .\sweep-cs.ps1 -Indirect       the sinks fed from somewhere else, the
#                                         shapes this tool cannot see
#          .\sweep-cs.ps1 -WriteBudget    rewrite the budget from today's counts
#          .\sweep-cs.ps1 -NoBudget       counts only, no verdict
#          Exit code 0 when the budget holds, 1 otherwise.
# Runs under Windows PowerShell 5.1: no &&, ||, ternary, ??, or ?. anywhere.
#
# The same sweep runs in C# as LocCsLiteralBudget in
# src/TrueforceForAll.Core.Tests/LocalizationTests.cs, the way Get-LocInventory
# and LocXamlWalker mirror each other for XAML. The rules both read are the data
# files (cs-ui-sinks.txt, cs-keep-literal.txt, cs-literal-budget.txt); the
# walking code is written twice on purpose, so the test needs no PowerShell. A
# rule added to one belongs in the other.

param(
    [string]$Root = '',
    [switch]$Detail,
    [switch]$Machine,
    [switch]$Indirect,
    [switch]$WriteBudget,
    [switch]$NoBudget,
    [string]$Only = ''
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

if ($Root -eq '') { $Root = $LocRepoRoot }
$plugin = Join-Path $Root 'src\TrueforceForAll.Plugin'

# ---------------------------------------------------------------- the rules file

function Read-LocCsSinkRules([string]$Path) {
    # cs-ui-sinks.txt: prop / ctor / call / callre / skipcall / watch / nonsink.
    # Fields are whitespace separated; the third field, where a directive has
    # one, is the argument selector and never contains a space.
    $rules = [pscustomobject]@{
        Props     = New-Object 'System.Collections.Generic.List[string]'
        Ctors     = New-Object 'System.Collections.Generic.Dictionary[string,string[]]' ([System.StringComparer]::Ordinal)
        Calls     = New-Object 'System.Collections.Generic.Dictionary[string,string[]]' ([System.StringComparer]::Ordinal)
        CallRes   = New-Object 'System.Collections.Generic.List[object]'
        SkipCalls = New-Object 'System.Collections.Generic.List[string]'
        TextCalls = New-Object 'System.Collections.Generic.List[string]'
        Watches   = New-Object 'System.Collections.Generic.List[string]'
        NonSinks  = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
        TextMembers = New-Object 'System.Collections.Generic.List[string]'
        Labels    = New-Object 'System.Collections.Generic.List[string]'
        RecordProps = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    }
    if (-not (Test-Path -LiteralPath $Path)) { throw "Missing sink rules: $Path" }
    $f = Read-LocTextFile $Path
    $n = 0
    foreach ($raw in $f.Text.Split("`n")) {
        $n++
        $line = $raw.TrimEnd("`r").Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $parts = @($line -split '\s+')
        $kind = $parts[0]
        if ($parts.Count -lt 2) { throw "cs-ui-sinks.txt:$n has a directive with no name: [$line]" }
        $name = $parts[1]
        $sel = @()
        if ($parts.Count -ge 3) { $sel = @($parts[2] -split ',' | Where-Object { $_.Length -gt 0 }) }
        switch ($kind) {
            'prop'     { $rules.Props.Add($name) }
            'ctor'     {
                if ($rules.Ctors.ContainsKey($name)) { throw "cs-ui-sinks.txt:$n has a second ctor rule for [$name]; merge the argument lists, because each reader keeps a different one of the two" }
                $rules.Ctors[$name] = $sel
            }
            'call'     {
                # A second rule for the same name is how the two walkers came to
                # disagree about BuildModCard: both key by name, and they kept
                # opposite ends of the pair.
                if ($rules.Calls.ContainsKey($name)) { throw "cs-ui-sinks.txt:$n has a second call rule for [$name]; merge the argument lists, because each reader keeps a different one of the two" }
                $rules.Calls[$name] = $sel
            }
            'callre'   { $rules.CallRes.Add([pscustomobject]@{ Body = $name; Args = $sel }) }
            'skipcall' { $rules.SkipCalls.Add($name) }
            'textcall' { $rules.TextCalls.Add($name) }
            'watch'    { $rules.Watches.Add($name) }
            'nonsink'  { [void]$rules.NonSinks.Add($name) }
            'textmember' { $rules.TextMembers.Add($name) }
            'labels'   { $rules.Labels.Add($name) }
            'recordprop' {
                # <Type>.<Prop>: the property counts only inside an object
                # initializer of that type, which is what tells GuideEntry.Label
                # from a field called Label on something that is not a record.
                $dot = $name.LastIndexOf('.')
                if ($dot -lt 1 -or $dot -eq $name.Length - 1) { throw "cs-ui-sinks.txt:$n recordprop wants <Type>.<Prop>: [$line]" }
                $type = $name.Substring(0, $dot)
                $prop = $name.Substring($dot + 1)
                if (-not $rules.RecordProps.ContainsKey($prop)) {
                    $rules.RecordProps[$prop] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
                }
                [void]$rules.RecordProps[$prop].Add($type)
            }
            default    { throw "cs-ui-sinks.txt:$n has an unknown directive [$kind]" }
        }
    }
    if ($rules.Props.Count -eq 0) { throw 'cs-ui-sinks.txt names no property sink.' }
    return $rules
}

function Read-LocCsAllowlist([string]$Path) {
    # cs-keep-literal.txt: 'file:<repo-relative path>' drops a whole file,
    # 'value:<exact text>' drops that literal wherever it appears, and
    # 'type:<Name>' drops the entries of an object initializer of that type (a
    # data record whose property happens to share a UI sink's name).
    $allow = [pscustomobject]@{
        Files  = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
        Values = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
        Types  = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    }
    foreach ($entry in (Read-LocListFile $Path)) {
        $colon = $entry.IndexOf(':')
        if ($colon -lt 1) { throw "cs-keep-literal.txt: [$entry] is not kind:value" }
        $kind = $entry.Substring(0, $colon)
        $val = $entry.Substring($colon + 1)
        switch ($kind) {
            'file'  { [void]$allow.Files.Add($val.Replace('\', '/')) }
            'value' { [void]$allow.Values.Add($val) }
            'type'  { [void]$allow.Types.Add($val) }
            default { throw "cs-keep-literal.txt: unknown kind [$kind] in [$entry]" }
        }
    }
    return $allow
}

function Read-LocCsBudget([string]$Path) {
    # cs-literal-budget.txt: '<repo-relative path> <count>' per line.
    $budget = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
    $order = New-Object 'System.Collections.Generic.List[string]'
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Counts = $budget; Order = $order; Exists = $false } }
    $f = Read-LocTextFile $Path
    $n = 0
    foreach ($raw in $f.Text.Split("`n")) {
        $n++
        $line = $raw.TrimEnd("`r").Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $parts = @($line -split '\s+')
        if ($parts.Count -ne 2) { throw "cs-literal-budget.txt:$n is not '<path> <count>': [$line]" }
        $path = $parts[0].Replace('\', '/')
        if ($budget.ContainsKey($path)) { throw "cs-literal-budget.txt lists $path twice." }
        $budget[$path] = [int]$parts[1]
        $order.Add($path)
    }
    return [pscustomobject]@{ Counts = $budget; Order = $order; Exists = $true }
}

# ---------------------------------------------------------------- C# reading

function Get-LocCsBalancedEnd([string]$Text, [int]$Open) {
    # Open is the index of '(', '[' or '{'. Returns the index just past the
    # matching close, or the text length. Literals are stepped over whole.
    $depth = 0
    $i = $Open
    while ($i -lt $Text.Length) {
        if (Test-LocLiteralStart $Text $i) { $i = Get-LocLiteralEnd $Text $i; continue }
        $c = $Text[$i]
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        elseif ($c -eq ')' -or $c -eq ']' -or $c -eq '}') {
            $depth--
            if ($depth -le 0) { return $i + 1 }
        }
        $i++
    }
    return $Text.Length
}

function Get-LocCsExprEnd([string]$Text, [int]$Start) {
    # The end of the expression that starts at Start: the first ';' or ',' at
    # depth zero, or the close of the brace, bracket or parenthesis that
    # encloses it. That covers a statement ("Title = "x";"), an object
    # initializer entry ("Content = "Close"," and the last one before '}') and
    # an initializer written inside an argument list.
    $depth = 0
    $i = $Start
    while ($i -lt $Text.Length) {
        if (Test-LocLiteralStart $Text $i) { $i = Get-LocLiteralEnd $Text $i; continue }
        $c = $Text[$i]
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        elseif ($c -eq ')' -or $c -eq ']' -or $c -eq '}') {
            if ($depth -eq 0) { return $i }
            $depth--
        } elseif ($depth -eq 0 -and ($c -eq ';' -or $c -eq ',')) { return $i }
        $i++
    }
    return $Text.Length
}

function Split-LocCsArgs([string]$Text, [int]$Open) {
    # The arguments of the call whose '(' is at Open, as spans with the
    # parameter name a named argument gives them ("okLabel: "Got it""). An empty
    # argument list returns no spans.
    $spans = New-Object 'System.Collections.Generic.List[object]'
    $depth = 0
    $i = $Open
    $argStart = $Open + 1
    while ($i -lt $Text.Length) {
        if ($i -gt $Open -and (Test-LocLiteralStart $Text $i)) { $i = Get-LocLiteralEnd $Text $i; continue }
        $c = $Text[$i]
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++; $i++; continue }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') {
            $depth--
            if ($depth -eq 0) {
                if ($i -gt $argStart -or $Text.Substring($argStart, $i - $argStart).Trim().Length -gt 0) {
                    $spans.Add([pscustomobject]@{ Start = $argStart; End = $i })
                }
                break
            }
            $i++
            continue
        }
        if ($c -eq ',' -and $depth -eq 1) {
            $spans.Add([pscustomobject]@{ Start = $argStart; End = $i })
            $argStart = $i + 1
            $i++
            continue
        }
        $i++
    }
    $out = New-Object 'System.Collections.Generic.List[object]'
    $idx = 0
    foreach ($s in $spans) {
        $body = $Text.Substring($s.Start, $s.End - $s.Start)
        if ($body.Trim().Length -eq 0) { $idx++; continue }
        $name = ''
        $m = [regex]::Match($body, '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)')
        if ($m.Success) { $name = $m.Groups[1].Value }
        $out.Add([pscustomobject]@{ Start = $s.Start; End = $s.End; Index = $idx; Name = $name })
        $idx++
    }
    return ,$out
}

function ConvertFrom-LocCsLiteral([string]$Text, [int]$Start, [int]$End) {
    # The value of the C# literal at [Start,End): escapes resolved in a regular
    # string, doubled quotes folded in a verbatim one, and an interpolation hole
    # rendered as '{}' so the letters outside it are what the real-string rule
    # weighs. A char literal returns its one character.
    $i = $Start
    $verbatim = $false
    $interpolated = $false
    while ($i -lt $End -and ($Text[$i] -eq '@' -or $Text[$i] -eq '$')) {
        if ($Text[$i] -eq '@') { $verbatim = $true } else { $interpolated = $true }
        $i++
    }
    if ($i -ge $End) { return '' }
    $quote = $Text[$i]
    $i++
    $stop = $End
    if ($stop -gt $Start -and $Text[$stop - 1] -eq $quote) { $stop = $End - 1 }
    $sb = New-Object System.Text.StringBuilder
    while ($i -lt $stop) {
        $c = $Text[$i]
        if ($verbatim -and $c -eq $quote -and $i + 1 -lt $stop -and $Text[$i + 1] -eq $quote) {
            [void]$sb.Append($quote); $i += 2; continue
        }
        if (-not $verbatim -and $c -eq '\' -and $i + 1 -lt $stop) {
            # Case-sensitive: '\U' takes eight hex digits and '\u' four, and
            # PowerShell's -eq would treat them as the same escape.
            $e = $Text[$i + 1]
            $i += 2
            if ($e -ceq 'n') { [void]$sb.Append("`n") }
            elseif ($e -ceq 'r') { [void]$sb.Append("`r") }
            elseif ($e -ceq 't') { [void]$sb.Append("`t") }
            elseif ($e -ceq '0') { [void]$sb.Append([char]0) }
            elseif ($e -ceq 'a' -or $e -ceq 'b' -or $e -ceq 'f' -or $e -ceq 'v') { [void]$sb.Append(' ') }
            elseif ($e -ceq 'u' -and $i + 4 -le $stop) {
                [void]$sb.Append([char][Convert]::ToInt32($Text.Substring($i, 4), 16)); $i += 4
            } elseif ($e -ceq 'U' -and $i + 8 -le $stop) {
                [void]$sb.Append([char]::ConvertFromUtf32([Convert]::ToInt32($Text.Substring($i, 8), 16))); $i += 8
            } elseif ($e -ceq 'x') {
                $h = ''
                while ($h.Length -lt 4 -and $i -lt $stop -and $Text[$i] -match '[0-9A-Fa-f]') { $h += $Text[$i]; $i++ }
                if ($h.Length -gt 0) { [void]$sb.Append([char][Convert]::ToInt32($h, 16)) }
            } else { [void]$sb.Append($e) }
            continue
        }
        if ($interpolated -and $c -eq '{') {
            if ($i + 1 -lt $stop -and $Text[$i + 1] -eq '{') { [void]$sb.Append('{'); $i += 2; continue }
            # Not $end: that name is the $End parameter (PowerShell ignores case).
            $holeEnd = Get-LocCsBalancedEnd $Text $i
            [void]$sb.Append('{}')
            $i = $holeEnd
            continue
        }
        if ($interpolated -and $c -eq '}' -and $i + 1 -lt $stop -and $Text[$i + 1] -eq '}') {
            [void]$sb.Append('}'); $i += 2; continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

function Get-LocCsSkipReason([string]$Value, $CommonSet) {
    # '' means a real UI string, otherwise numeric, glyph or empty. The XAML
    # rule (Get-LocSkipReason) asks whether the attribute and element are
    # display before letting a two-letter value through; a C# sink write is
    # display by definition, so two letters is the threshold here and the rest
    # of the rule is the same: whitespace or four letters is always real, a
    # value in common-keys.txt is real, one letter alone is a glyph.
    #
    # One rule the XAML side does not need: placeholders are removed before the
    # letters are counted, because XAML holds none. "{0} items" keeps " items"
    # and stays real, while "{0:X2}{1:X2}{2:X2}" is left with nothing and is a
    # number format rather than a sentence.
    if ($null -eq $Value) { return 'empty' }
    $t = ($Value -replace '\{[^{}]*\}', '').Trim()
    if ($t.Length -eq 0) { return 'empty' }
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
    if ($letters -ge 2) { return '' }
    if ($null -ne $CommonSet -and $CommonSet.Contains($Value)) { return '' }
    return 'glyph'
}

# ---------------------------------------------------------------- the regexes

function New-LocCsCallRegex([string]$Name) {
    # "Loc.T" matches a call by that dotted name; ".ToString" matches it on any
    # receiver. The match ends at the '(' so its last character is the paren.
    $lead = '(?<![A-Za-z0-9_.])'
    $body = $Name
    if ($Name.StartsWith('.')) { $lead = '\.\s*'; $body = $Name.Substring(1) }
    $parts = @($body -split '\.' | ForEach-Object { [regex]::Escape($_) })
    return [regex]($lead + ($parts -join '\s*\.\s*') + '(?![A-Za-z0-9_])\s*\(')
}

function New-LocCsRegexSet($Rules) {
    $props = @($Rules.Props | Sort-Object -Property Length -Descending)
    $propPattern = '(?:(?<dot>\.)|(?<![A-Za-z0-9_.]))(?<name>' + (($props | ForEach-Object { [regex]::Escape($_) }) -join '|') +
        ')(?![A-Za-z0-9_])\s*(?<plus>\+)?=(?![=>])'
    $calls = New-Object 'System.Collections.Generic.List[object]'
    foreach ($k in $Rules.Calls.Keys) {
        $parts = @($k -split '\.' | ForEach-Object { [regex]::Escape($_) })
        # A dotted name is matched as written, so nothing else called Show is
        # taken for TrueforceDialog.Show. A one-part name matches on any receiver
        # ("btn.SetValue(...)"), which is why the lookbehind lets a dot through.
        $lead = '(?<![A-Za-z0-9_.])'
        if ($parts.Count -eq 1) { $lead = '(?<![A-Za-z0-9_])' }
        $pat = $lead + ($parts -join '\s*\.\s*') + '(?![A-Za-z0-9_])\s*\('
        $calls.Add([pscustomobject]@{ Name = $k; Args = $Rules.Calls[$k]; Re = [regex]$pat })
    }
    foreach ($cr in $Rules.CallRes) {
        # The method name is captured so a finding names the helper it landed on
        # rather than the pattern that matched it. Any receiver, as above.
        $pat = '(?<![A-Za-z0-9_])(?<mname>' + $cr.Body + ')(?![A-Za-z0-9_])\s*\('
        $calls.Add([pscustomobject]@{ Name = $cr.Body; Args = $cr.Args; Re = [regex]$pat })
    }
    $ctors = New-Object 'System.Collections.Generic.List[object]'
    foreach ($k in $Rules.Ctors.Keys) {
        $pat = '\bnew\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*' + [regex]::Escape($k) + '(?![A-Za-z0-9_])\s*\('
        $ctors.Add([pscustomobject]@{ Name = $k; Args = $Rules.Ctors[$k]; Re = [regex]$pat })
    }
    $skips = New-Object 'System.Collections.Generic.List[object]'
    foreach ($s in $Rules.SkipCalls) { $skips.Add([pscustomobject]@{ Name = $s; Re = (New-LocCsCallRegex $s) }) }
    $texts = New-Object 'System.Collections.Generic.List[object]'
    foreach ($s in $Rules.TextCalls) { $texts.Add([pscustomobject]@{ Name = $s; Re = (New-LocCsCallRegex $s) }) }
    $watches = New-Object 'System.Collections.Generic.List[object]'
    foreach ($w in $Rules.Watches) {
        $watches.Add([pscustomobject]@{
            Name = $w
            Re = [regex]('(?<![A-Za-z0-9_.])' + [regex]::Escape($w) + '\s*\.\s*(?<m>[A-Za-z_][A-Za-z0-9_]*)\s*\(')
        })
    }
    # The three Loc calls, so a sink whose value comes through the store can be
    # told from one whose value is computed somewhere else.
    $locRe = [regex]'(?<![A-Za-z0-9_.])Loc\s*\.\s*(?:T|F|N)(?![A-Za-z0-9_])\s*\('
    # A record property counts only inside its own type's initializer, so the
    # pattern is the same shape as the plain property one and the frame decides.
    $recRe = $null
    if ($Rules.RecordProps.Count -gt 0) {
        $alt = ($Rules.RecordProps.Keys | Sort-Object { $_.Length } -Descending | ForEach-Object { [regex]::Escape($_) }) -join '|'
        $recRe = [regex]('(?:(?<dot>\.)|(?<![A-Za-z0-9_.]))(?<name>' + $alt + ')(?![A-Za-z0-9_])\s*(?<plus>\+)?=(?![=>])')
    }
    return [pscustomobject]@{
        Prop = [regex]$propPattern; Calls = $calls; Ctors = $ctors; Skips = $skips
        Texts = $texts; Watches = $watches; Loc = $locRe; RecordProp = $recRe
    }
}

# ---------------------------------------------------------------- per-file walk

function Get-LocCsRegionSpans([string]$Text, $Names, [string]$Kind) {
    # The span of each named region, as [Start,End) over the comment-blanked
    # text. A textmember is a method or property whose literals are all display
    # text: its body is the balanced braces after the declaration, or everything
    # up to the ';' when it is an expression body. A labels region is a declared
    # collection of labels: the balanced braces of its initializer. Both are
    # matched by name at a declaration, not at a use, so a call to the member
    # and a read of the collection are untouched.
    $out = New-Object 'System.Collections.Generic.List[object]'
    foreach ($nm in $Names) {
        $esc = [regex]::Escape($nm)
        if ($Kind -eq 'labels') {
            # Ident = { ... } or Ident = new <anything> { ... }
            # "Name = { ... }", "Name = new X { ... }" and the property form a
            # label table has to use to resolve live, "Name => new[] { ... }".
            $re = [regex]('(?<![A-Za-z0-9_.])' + $esc + '\s*=>?\s*(?:new\b[^={;]*)?\{')
        } else {
            # Name(...) { ... }  |  Name(...) => ...;  |  Name { get ... }  |  Name => ...;
            $re = [regex]('(?<![A-Za-z0-9_.])' + $esc + '\s*(?:\([^()]*\))?\s*(?:=>|\{)')
        }
        foreach ($m in $re.Matches($Text)) {
            $openAt = $Text.IndexOf('{', $m.Index + $m.Length - 1)
            $isArrow = $Text.Substring($m.Index, $m.Length).EndsWith('=>')
            if ($isArrow) {
                $start = $m.Index + $m.Length
                $end = Get-LocCsExprEnd $Text $start
            } else {
                $start = $m.Index + $m.Length - 1
                if ($Text[$start] -ne '{') { continue }
                $end = Get-LocCsBalancedEnd $Text $start
            }
            if ($end -gt $start) {
                # Arrow: the region IS the returned expression, so a textmember has
                # no inner "return" to look for and the whole region is the span.
                $out.Add([pscustomobject]@{ Start = $start; End = $end; Name = $nm; Arrow = $isArrow })
            }
        }
    }
    return ,$out
}

function Get-LocCsReturnSpans([string]$Text, [int]$Start, [int]$End) {
    # The spans inside [Start,End) that a member actually returns: what follows
    # "return" or "=>" up to the end of that expression. A textmember names a
    # member whose RETURNED text is display text; the rest of its body is code,
    # and a member that returns display text may still switch on string case
    # labels, which are identifiers and must never be keyed.
    $out = New-Object 'System.Collections.Generic.List[object]'
    $re = [regex]'(?<![A-Za-z0-9_])return(?![A-Za-z0-9_])|=>'
    foreach ($m in $re.Matches($Text)) {
        if ($m.Index -lt $Start -or $m.Index -ge $End) { continue }
        $from = $m.Index + $m.Length
        $to = Get-LocCsExprEnd $Text $from
        if ($to -gt $End) { $to = $End }
        if ($to -gt $from) { $out.Add([pscustomobject]@{ Start = $from; End = $to }) }
    }
    return ,$out
}

function Get-LocCsNewSpans([string]$Text) {
    # Two maps over every "new" in the file.
    #   Types: brace index -> the type of the object or collection initializer
    #          that brace opens, '' for an implicit array. Everything else is a
    #          statement block, which is how a bare "Title = ..." on a window is
    #          told from an entry in a data record's initializer.
    #   Ends:  the index of "new" -> the index just past the whole construction.
    #          A sink expression steps over a construction whole, because any UI
    #          text inside it sits on a sink of its own and is counted there.
    $types = New-Object 'System.Collections.Generic.Dictionary[int,string]'
    $ends = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    foreach ($m in [regex]::Matches($Text, '\bnew\b')) {
        $j = $m.Index + 3
        while ($j -lt $Text.Length -and [char]::IsWhiteSpace($Text[$j])) { $j++ }
        $type = ''
        if ($j -lt $Text.Length -and $Text[$j] -ne '[' -and $Text[$j] -ne '{') {
            $start = $j
            while ($j -lt $Text.Length -and ($Text[$j] -match '[A-Za-z0-9_.]')) { $j++ }
            if ($j -eq $start) { continue }
            $type = $Text.Substring($start, $j - $start)
        }
        while ($j -lt $Text.Length) {
            while ($j -lt $Text.Length -and [char]::IsWhiteSpace($Text[$j])) { $j++ }
            if ($j -ge $Text.Length) { break }
            $c = $Text[$j]
            if ($c -eq '<') {
                $e = Get-LocGenericListEnd $Text $j
                if ($e -lt 0) { break }
                $j = $e
                continue
            }
            if ($c -eq '(' -or $c -eq '[') { $j = Get-LocCsBalancedEnd $Text $j; continue }
            break
        }
        $end = $j
        while ($end -lt $Text.Length -and [char]::IsWhiteSpace($Text[$end])) { $end++ }
        if ($end -lt $Text.Length -and $Text[$end] -eq '{') {
            $types[$end] = $type
            $ends[$m.Index] = Get-LocCsBalancedEnd $Text $end
        } else {
            $ends[$m.Index] = $j
        }
    }
    return [pscustomobject]@{ Types = $types; Ends = $ends }
}

function Get-LocCsSiteFrames([string]$Text, $Sites, $InitTypes) {
    # One pass over the file, jumping between the characters that matter, to
    # learn for each site whether the code reaches it at all (a site matched
    # inside a string literal does not count) and which object initializer, if
    # any, encloses it. Returns index -> type, with '' for a statement block and
    # no entry for a dead site.
    $frames = New-Object 'System.Collections.Generic.Dictionary[int,string]'
    if ($Sites.Count -eq 0) { return $frames }
    $stack = New-Object 'System.Collections.Generic.List[string]'
    $stops = [char[]]@([char]'"', [char]"'", [char]'@', [char]'$', [char]'{', [char]'}')
    $i = 0
    $s = 0
    while ($i -lt $Text.Length) {
        $n = $Text.IndexOfAny($stops, $i)
        if ($n -lt 0) { $n = $Text.Length }
        while ($s -lt $Sites.Count -and $Sites[$s].Index -lt $n) {
            $top = ''
            if ($stack.Count -gt 0) { $top = $stack[$stack.Count - 1] }
            $frames[$Sites[$s].Index] = $top
            $s++
        }
        if ($n -ge $Text.Length) { break }
        $i = $n
        if (Test-LocLiteralStart $Text $i) {
            $end = Get-LocLiteralEnd $Text $i
            while ($s -lt $Sites.Count -and $Sites[$s].Index -lt $end) { $s++ }   # matched inside a literal
            $i = $end
            continue
        }
        $c = $Text[$i]
        if ($c -eq '{') {
            $t = ''
            if ($InitTypes.ContainsKey($i)) { $t = $InitTypes[$i] }
            $stack.Add($t)
        } elseif ($c -eq '}') {
            if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
        }
        $i++
    }
    return $frames
}

function Test-LocCsTestOperand([string]$Text, [int]$Start, [int]$End) {
    # True when the literal at [Start,End) is being compared rather than shown:
    # 'mode == "mine"', '"mine" == mode', 'is "x"', 'case "x":', or the pattern
    # side of a switch arm ('"x" => ...'). Such a literal sits at the top level
    # of a sink expression ('Label.Text = kind == "car" ? A : B') and would
    # otherwise read as the value. The value in that example is A or B, and both
    # are counted on their own.
    $k = $Start - 1
    while ($k -ge 0 -and [char]::IsWhiteSpace($Text[$k])) { $k-- }
    if ($k -ge 1) {
        $two = $Text.Substring($k - 1, 2)
        if ($two -ceq '==' -or $two -ceq '!=') { return $true }
    }
    if ($k -ge 0 -and ($Text[$k] -match '[A-Za-z0-9_]')) {
        $w = $k
        while ($w -ge 0 -and ($Text[$w] -match '[A-Za-z0-9_]')) { $w-- }
        $word = $Text.Substring($w + 1, $k - $w)
        if ($word -ceq 'is' -or $word -ceq 'case') { return $true }
    }
    $j = $End
    while ($j -lt $Text.Length -and [char]::IsWhiteSpace($Text[$j])) { $j++ }
    if ($j + 1 -lt $Text.Length) {
        $two = $Text.Substring($j, 2)
        if ($two -ceq '==' -or $two -ceq '!=' -or $two -ceq '=>') { return $true }
    }
    return $false
}

function Get-LocCsHoleSpans([string]$Text, [int]$Start, [int]$End) {
    # The interior of each interpolation hole in the literal at [Start,End).
    # '{{' is an escaped brace and never opens a hole; a nested brace (an object
    # initializer or a nested interpolation) is tracked by depth; a literal
    # inside a hole is stepped over so a '}' in its text cannot close the hole.
    $out = New-Object 'System.Collections.Generic.List[object]'
    $i = $Start
    # Past the opening quote, so a '{' cannot be read out of the '$@"' prefix.
    while ($i -lt $End -and $Text[$i] -ne '"') { $i++ }
    $i++
    while ($i -lt $End) {
        $c = $Text[$i]
        if ($c -eq '{') {
            if ($i + 1 -lt $End -and $Text[$i + 1] -eq '{') { $i += 2; continue }
            $depth = 1
            $j = $i + 1
            $holeStart = $j
            while ($j -lt $End -and $depth -gt 0) {
                if (Test-LocLiteralStart $Text $j) { $j = Get-LocLiteralEnd $Text $j; continue }
                $d = $Text[$j]
                if ($d -eq '{') { $depth++ }
                elseif ($d -eq '}') { $depth-- ; if ($depth -eq 0) { break } }
                $j++
            }
            if ($j -gt $holeStart) { $out.Add([pscustomobject]@{ Start = $holeStart; End = $j }) }
            $i = $j + 1
            continue
        }
        $i++
    }
    return ,$out
}

# A word before a bracket makes it a call, unless the word is one of these: then
# the bracket groups an expression and the walk has to go into it. Without this,
# "return (a, \"text\")" reads as a call to something named return.
$LocCsParenKeywords = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($w in @('return', 'throw', 'if', 'while', 'switch', 'case', 'else', 'do',
                 'for', 'foreach', 'lock', 'using', 'catch', 'when', 'yield', 'in',
                 'is', 'as', 'await', 'and', 'or', 'not')) { [void]$LocCsParenKeywords.Add($w) }

function Test-LocCsWordBeforeIsKeyword([string]$Text, [int]$At) {
    # $At is the index of the last non-space character before the bracket.
    if ($At -lt 0) { return $false }
    $e = $At
    while ($e -ge 0 -and $Text[$e] -match '[A-Za-z0-9_]') { $e-- }
    $word = $Text.Substring($e + 1, $At - $e)
    return $LocCsParenKeywords.Contains($word)
}

function Get-LocCsLiteralsIn([string]$Text, [int]$Start, [int]$End, $Maps) {
    # Every string literal in [Start,End) that a user actually reads. Stepped
    # over whole: a call the rules skip (a key, a format specifier), any "new
    # ..." construction (its own text has its own sink inside it), and any other
    # call or indexer, because a literal handed to a method or used as a lookup
    # key is not display text. Descended into: a grouping parenthesis, a ternary,
    # a concatenation, and the calls the rules mark as text composers.
    $out = New-Object 'System.Collections.Generic.List[object]'
    $i = $Start
    while ($i -lt $End) {
        if ($Maps.SkipAt.ContainsKey($i)) { $i = Get-LocCsBalancedEnd $Text $Maps.SkipAt[$i]; continue }
        if ($Maps.NewEnd.ContainsKey($i)) { $i = $Maps.NewEnd[$i]; continue }
        if ($Maps.TextAt.ContainsKey($i)) { $i = $Maps.TextAt[$i] + 1; continue }
        if (Test-LocLiteralStart $Text $i) {
            # Not $end: PowerShell variable names are case-insensitive, so that
            # would be the $End parameter and the scan would stop at the first
            # literal it found.
            $litEnd = Get-LocLiteralEnd $Text $i
            # A char literal is never display text, and neither is one being
            # compared rather than shown.
            if ($Text[$i] -ne "'" -and -not (Test-LocCsTestOperand $Text $i $litEnd)) {
                $out.Add([pscustomobject]@{ Start = $i; End = $litEnd })
            }
            # An interpolated string's holes hold code, and that code can hold
            # display text of its own. Everything the outer walk steps over is
            # stepped over in here too, since it is the same walk.
            $isInterp = $false
            $p = $i
            while ($p -lt $litEnd -and ($Text[$p] -eq '@' -or $Text[$p] -eq '$')) {
                if ($Text[$p] -eq '$') { $isInterp = $true }
                $p++
            }
            if ($isInterp) {
                foreach ($h in (Get-LocCsHoleSpans $Text $i $litEnd)) {
                    foreach ($inner in (Get-LocCsLiteralsIn $Text $h.Start $h.End $Maps)) { $out.Add($inner) }
                }
            }
            $i = $litEnd
            continue
        }
        $c = $Text[$i]
        if ($c -eq '(' -or $c -eq '[') {
            $k = $i - 1
            while ($k -ge 0 -and [char]::IsWhiteSpace($Text[$k])) { $k-- }
            $isCall = $false
            if ($k -ge 0) {
                $p = $Text[$k]
                if ($p -match '[A-Za-z0-9_]' -or $p -eq ')' -or $p -eq ']') { $isCall = $true }
                # ...unless the word is a keyword, in which case the bracket groups
                # an expression: "return (a, "text")" is not a call.
                if ($isCall -and ($p -match '[A-Za-z0-9_]') -and (Test-LocCsWordBeforeIsKeyword $Text $k)) { $isCall = $false }
            }
            if ($isCall) { $i = Get-LocCsBalancedEnd $Text $i; continue }
        }
        $i++
    }
    return ,$out
}

function Get-LocCsLineStarts([string]$Text) {
    # The index of every line's first character, so a line number is a binary
    # search rather than a substring of everything above it.
    $list = New-Object 'System.Collections.Generic.List[int]'
    $list.Add(0)
    $i = $Text.IndexOf("`n")
    while ($i -ge 0) {
        $list.Add($i + 1)
        $i = $Text.IndexOf("`n", $i + 1)
    }
    return $list
}

function Get-LocCsLine($LineStarts, [int]$Index) {
    $lo = 0
    $hi = $LineStarts.Count - 1
    while ($lo -lt $hi) {
        $mid = [int](($lo + $hi + 1) / 2)
        if ($LineStarts[$mid] -le $Index) { $lo = $mid } else { $hi = $mid - 1 }
    }
    return $lo + 1
}

function Test-LocCsAnyIn($Positions, [int]$Start, [int]$End) {
    foreach ($p in $Positions) { if ($p -ge $Start -and $p -lt $End) { return $true } }
    return $false
}

function Get-LocCsMemberStarts([string]$Text) {
    # Every method or property declaration in the file, in order, for the
    # indirect report only: a best effort that names the member a computed sink
    # write sits in so a reader can go find who feeds it.
    $re = [regex]'(?m)^[ \t]*(?:(?:public|private|internal|protected|static|sealed|override|async|virtual|partial|new|extern|unsafe)[ \t]+)*[A-Za-z_][A-Za-z0-9_<>,\[\]\.\?]*[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*[\(\{]'
    # A statement that opens a block reads like a declaration to that regex, so
    # the keywords are dropped by name.
    $keywords = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($k in @('if', 'else', 'for', 'foreach', 'while', 'do', 'switch', 'case', 'try', 'catch',
            'finally', 'lock', 'using', 'fixed', 'return', 'new', 'get', 'set', 'checked', 'unchecked')) {
        [void]$keywords.Add($k)
    }
    $list = New-Object 'System.Collections.Generic.List[object]'
    foreach ($m in $re.Matches($Text)) {
        $name = $m.Groups['name'].Value
        if ($keywords.Contains($name)) { continue }
        $list.Add([pscustomobject]@{ Index = $m.Index; Name = $name })
    }
    return $list
}

function Get-LocCsEnclosingMember($MemberStarts, [int]$Index) {
    $best = '(file scope)'
    foreach ($m in $MemberStarts) {
        if ($m.Index -gt $Index) { break }
        $best = $m.Name
    }
    return $best
}

function Get-LocCsFileResult([string]$Path, [string]$Rel, $Rx, $Rules, $Allow, $CommonSet, [bool]$WantIndirect) {
    $raw = [System.IO.File]::ReadAllText($Path)
    $text = Remove-LocComments $raw
    $sites = New-Object 'System.Collections.Generic.List[object]'
    foreach ($m in $Rx.Prop.Matches($text)) {
        $sites.Add([pscustomobject]@{
            Index = $m.Index; Kind = 'prop'; Sink = $m.Groups['name'].Value
            Dot = $m.Groups['dot'].Success; Body = $m.Index + $m.Length; Args = $null
        })
    }
    foreach ($c in $Rx.Calls) {
        foreach ($m in $c.Re.Matches($text)) {
            $sink = $c.Name
            if ($m.Groups['mname'].Success) { $sink = $m.Groups['mname'].Value }
            $sites.Add([pscustomobject]@{
                Index = $m.Index; Kind = 'call'; Sink = $sink
                Dot = $false; Body = $m.Index + $m.Length - 1; Args = $c.Args
            })
        }
    }
    foreach ($c in $Rx.Ctors) {
        foreach ($m in $c.Re.Matches($text)) {
            $sites.Add([pscustomobject]@{
                Index = $m.Index; Kind = 'ctor'; Sink = 'new ' + $c.Name
                Dot = $false; Body = $m.Index + $m.Length - 1; Args = $c.Args
            })
        }
    }
    if ($Rx.RecordProp -ne $null) {
        foreach ($m in $Rx.RecordProp.Matches($text)) {
            $sites.Add([pscustomobject]@{
                Index = $m.Index; Kind = 'recordprop'; Sink = $m.Groups['name'].Value
                Dot = $m.Groups['dot'].Success; Body = $m.Index + $m.Length; Args = $null
            })
        }
    }
    foreach ($r in (Get-LocCsRegionSpans $text $Rules.TextMembers 'textmember')) {
        $sites.Add([pscustomobject]@{
            Index = $r.Start; Kind = 'textmember'; Sink = $r.Name
            Dot = $false; Body = $r.Start; Args = $null; RegionEnd = $r.End; Arrow = $r.Arrow
        })
    }
    foreach ($r in (Get-LocCsRegionSpans $text $Rules.Labels 'labels')) {
        $sites.Add([pscustomobject]@{
            Index = $r.Start; Kind = 'labels'; Sink = $r.Name
            Dot = $false; Body = $r.Start; Args = $null; RegionEnd = $r.End
        })
    }
    $sites = @($sites | Sort-Object -Property Index)

    # Calls whose literals never reach a user, the two text composers, and the
    # Loc calls on their own.
    $skipAt = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    foreach ($s in $Rx.Skips) {
        foreach ($m in $s.Re.Matches($text)) { $skipAt[$m.Index] = $m.Index + $m.Length - 1 }
    }
    $textAt = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    foreach ($s in $Rx.Texts) {
        foreach ($m in $s.Re.Matches($text)) { $textAt[$m.Index] = $m.Index + $m.Length - 1 }
    }
    $locAt = New-Object 'System.Collections.Generic.List[int]'
    foreach ($m in $Rx.Loc.Matches($text)) { $locAt.Add($m.Index) }

    $news = Get-LocCsNewSpans $text
    $maps = [pscustomobject]@{ SkipAt = $skipAt; TextAt = $textAt; NewEnd = $news.Ends }
    $frames = Get-LocCsSiteFrames $text $sites $news.Types
    $lineStarts = Get-LocCsLineStarts $text
    $members = New-Object 'System.Collections.Generic.List[object]'
    if ($WantIndirect) { $members = Get-LocCsMemberStarts $text }

    $findings = New-Object 'System.Collections.Generic.List[object]'
    $allowed = 0
    $viaLoc = 0
    $computed = 0
    $indirectCounts = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
    foreach ($site in $sites) {
        if (-not $frames.ContainsKey($site.Index)) { continue }   # inside a literal
        $frame = $frames[$site.Index]
        $spans = New-Object 'System.Collections.Generic.List[object]'
        if ($site.Kind -eq 'textmember') {
            if ($site.Arrow) {
                $spans.Add([pscustomobject]@{ Start = $site.Body; End = $site.RegionEnd })
            } else {
                foreach ($rs in (Get-LocCsReturnSpans $text $site.Body $site.RegionEnd)) { $spans.Add($rs) }
            }
        } elseif ($site.Kind -eq 'labels') {
            $spans.Add([pscustomobject]@{ Start = $site.Body; End = $site.RegionEnd })
        } elseif ($site.Kind -eq 'recordprop') {
            $types = $Rules.RecordProps[$site.Sink]
            if ($frame -eq '' -or -not $types.Contains($frame)) { continue }
            $spans.Add([pscustomobject]@{ Start = $site.Body; End = (Get-LocCsExprEnd $text $site.Body) })
        } elseif ($site.Kind -eq 'prop') {
            $spans.Add([pscustomobject]@{ Start = $site.Body; End = (Get-LocCsExprEnd $text $site.Body) })
        } else {
            foreach ($a in (Split-LocCsArgs $text $site.Body)) {
                $take = $false
                foreach ($sel in $site.Args) {
                    if ($sel -match '^\d+$') { if ([int]$sel -eq $a.Index) { $take = $true } }
                    elseif ($a.Name -ne '' -and $a.Name -eq $sel) { $take = $true }
                }
                if ($take) { $spans.Add([pscustomobject]@{ Start = $a.Start; End = $a.End }) }
            }
        }
        $lits = New-Object 'System.Collections.Generic.List[object]'
        $hasLoc = $false
        foreach ($sp in $spans) {
            foreach ($l in (Get-LocCsLiteralsIn $text $sp.Start $sp.End $maps)) { $lits.Add($l) }
            if (Test-LocCsAnyIn $locAt $sp.Start $sp.End) { $hasLoc = $true }
        }
        $real = New-Object 'System.Collections.Generic.List[object]'
        foreach ($l in $lits) {
            $value = ConvertFrom-LocCsLiteral $text $l.Start $l.End
            if ((Get-LocCsSkipReason $value $CommonSet) -ne '') { continue }
            if ($Allow.Values.Contains($value)) { $allowed++; continue }
            if ($frame -ne '' -and $Allow.Types.Contains($frame)) { $allowed++; continue }
            $real.Add([pscustomobject]@{ Start = $l.Start; End = $l.End; Value = $value })
        }
        if ($hasLoc) { $viaLoc++ }
        if ($real.Count -eq 0) {
            if (-not $hasLoc -and $spans.Count -gt 0) {
                $computed++
                if ($WantIndirect -and $site.Kind -eq 'prop') {
                    $body = $text.Substring($spans[0].Start, $spans[0].End - $spans[0].Start).Trim()
                    if ($body -match '^[A-Za-z_][A-Za-z0-9_]*$') {
                        $who = Get-LocCsEnclosingMember $members $site.Index
                        if ($indirectCounts.ContainsKey($who)) { $indirectCounts[$who] = $indirectCounts[$who] + 1 } else { $indirectCounts[$who] = 1 }
                    }
                }
            }
            continue
        }
        foreach ($r in $real) {
            $findings.Add([pscustomobject]@{
                File = $Rel
                Line = (Get-LocCsLine $lineStarts $r.Start)
                Start = $r.Start
                End = $r.End
                Sink = $site.Sink
                Kind = $site.Kind
                Frame = $frame
                Value = $r.Value
            })
        }
    }

    # Watched receivers: a method nobody has classified is a blind spot, not a pass.
    $unknown = New-Object 'System.Collections.Generic.List[string]'
    foreach ($w in $Rx.Watches) {
        foreach ($m in $w.Re.Matches($text)) {
            $full = $w.Name + '.' + $m.Groups['m'].Value
            if ($Rules.Calls.ContainsKey($full) -or $Rules.NonSinks.Contains($full)) { continue }
            $line = Get-LocCsLine $lineStarts $m.Index
            $unknown.Add(('{0}:{1} {2}(...) is neither a call sink nor a nonsink in cs-ui-sinks.txt' -f $Rel, $line, $full))
        }
    }

    return [pscustomobject]@{
        Rel = $Rel; Findings = $findings; Allowed = $allowed; ViaLoc = $viaLoc
        Computed = $computed; Sites = $sites.Count; Unknown = $unknown; Indirect = $indirectCounts
    }
}

# ---------------------------------------------------------------- run

# -Only scans one file, so a budget written from it would drop every other
# file's line and read as a finished phase.
if ($WriteBudget -and $Only -ne '') {
    throw '-WriteBudget writes the whole budget, so it cannot be combined with -Only. Run it without -Only.'
}

$rules = Read-LocCsSinkRules (Join-Path $LocToolsDir 'cs-ui-sinks.txt')
$allow = Read-LocCsAllowlist (Join-Path $LocToolsDir 'cs-keep-literal.txt')
$commonSet = Read-LocListFile (Join-Path $LocToolsDir 'common-keys.txt')
$rx = New-LocCsRegexSet $rules

$files = @(Get-ChildItem -Path $plugin -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object -Property FullName)

$results = New-Object 'System.Collections.Generic.List[object]'
$skippedFiles = New-Object 'System.Collections.Generic.List[string]'
$unknownAll = New-Object 'System.Collections.Generic.List[string]'
$indirectAll = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
foreach ($f in $files) {
    $rel = Get-LocRelativePath $f.FullName $Root
    # -Only is an inspection switch, so it skips the other files outright rather
    # than scanning them and filtering the output. A full run is the enforcing
    # one; this one cannot check the budget, because every skipped file would
    # read as zero and trip the under-budget rule.
    if ($Only -ne '' -and -not $rel.EndsWith($Only)) { continue }
    if ($allow.Files.Contains($rel)) { $skippedFiles.Add($rel); continue }
    $r = Get-LocCsFileResult $f.FullName $rel $rx $rules $allow $commonSet ([bool]$Indirect)
    foreach ($u in $r.Unknown) { $unknownAll.Add($u) }
    foreach ($k in $r.Indirect.Keys) {
        if ($indirectAll.ContainsKey($k)) { $indirectAll[$k] = $indirectAll[$k] + $r.Indirect[$k] }
        else { $indirectAll[$k] = $r.Indirect[$k] }
    }
    $results.Add($r)
}

$remaining = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
$totalRemaining = 0
$totalAllowed = 0
$totalViaLoc = 0
$totalComputed = 0
$totalSites = 0
foreach ($r in $results) {
    if ($r.Findings.Count -gt 0) { $remaining[$r.Rel] = $r.Findings.Count }
    $totalRemaining += $r.Findings.Count
    $totalAllowed += $r.Allowed
    $totalViaLoc += $r.ViaLoc
    $totalComputed += $r.Computed
    $totalSites += $r.Sites
}

Write-Host ('Sink rules: {0} propert(y/ies), {1} constructor(s), {2} call(s), {3} call pattern(s), {4} skipped call(s)' -f
    $rules.Props.Count, $rules.Ctors.Count, $rules.Calls.Count, $rules.CallRes.Count, $rules.SkipCalls.Count)
Write-Host ('Files: {0} scanned, {1} allowlisted whole' -f $results.Count, $skippedFiles.Count)
if ($Only -ne '') {
    Write-Host ('Only: ' + $Only + ' (an inspection run: the other files were not scanned, so the budget is not checked)')
}
Write-Host ''
Write-Host 'Remaining bare UI literals, by file:'
$rows = @($results | Where-Object { $_.Findings.Count -gt 0 } | Sort-Object -Property @{ Expression = { $_.Findings.Count }; Descending = $true }, Rel)
foreach ($r in $rows) {
    Write-Host ('  {0,5}  {1}' -f $r.Findings.Count, $r.Rel)
}
if ($rows.Count -eq 0) { Write-Host '  (none)' }
Write-Host ''
Write-Host ('TOTAL remaining: {0} literal(s) in {1} file(s)' -f $totalRemaining, $rows.Count)
Write-Host ('Sink writes seen: {0}; through Loc: {1}; value computed elsewhere: {2}' -f $totalSites, $totalViaLoc, $totalComputed)
Write-Host ('Allowlisted: {0} literal(s) by value or type, plus {1} file(s) whole' -f $totalAllowed, $skippedFiles.Count)

if ($Machine) {
    # One record per finding: path, 1-based line, character offset of the
    # literal, its end, the sink, and the file's length so a consumer can prove
    # it is reading the same bytes. Tab separated, no prose, nothing localized.
    Write-Host ''
    Write-Host 'Machine:'
    foreach ($r in $rows) {
        $len = ([System.IO.File]::ReadAllText((Join-Path $Root ($r.Rel.Replace('/', '\\'))))).Length
        foreach ($fd in $r.Findings) {
            Write-Host ("{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $fd.File, $fd.Line, $fd.Start, $fd.End, $fd.Sink, $len)
        }
    }
}

if ($Detail) {
    Write-Host ''
    Write-Host 'Detail:'
    foreach ($r in $rows) {
        foreach ($fd in $r.Findings) {
            $where = '{0}:{1}' -f $fd.File, $fd.Line
            $frame = ''
            if ($fd.Frame -ne '') { $frame = ' in new ' + $fd.Frame + ' {}' }
            Write-Host ('  {0} {1}{2} [{3}]' -f $where, $fd.Sink, $frame, ($fd.Value -replace "`n", '\n'))
        }
    }
}

if ($Indirect) {
    Write-Host ''
    Write-Host 'Sinks fed from elsewhere (a bare identifier reaches the sink), by enclosing member.'
    Write-Host 'These are the call sites this tool cannot see: the literal, if any, is at the caller.'
    foreach ($k in @($indirectAll.Keys | Sort-Object -Property @{ Expression = { $indirectAll[$_] }; Descending = $true }, @{ Expression = { $_ } })) {
        Write-Host ('  {0,4}  {1}' -f $indirectAll[$k], $k)
    }
}

$fails = New-Object 'System.Collections.Generic.List[string]'
foreach ($u in $unknownAll) { $fails.Add($u) }

$budgetPath = Join-Path $LocToolsDir 'cs-literal-budget.txt'
if ($WriteBudget) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("# tools/loc/cs-literal-budget.txt`n")
    [void]$sb.Append("#`n")
    [void]$sb.Append("# The ratchet for Phase 2 of docs/localization-plan.md: how many bare English`n")
    [void]$sb.Append("# UI literals each C# file is still allowed to hold. One '<repo-relative path>`n")
    [void]$sb.Append("# <count>' line per file, sorted by count. Generated by`n")
    [void]$sb.Append("# tools/loc/sweep-cs.ps1 -WriteBudget and enforced by that script and by`n")
    [void]$sb.Append("# LocCsLiteralBudget in src/TrueforceForAll.Core.Tests/LocalizationTests.cs.`n")
    [void]$sb.Append("#`n")
    [void]$sb.Append("# A number only ever goes down, and a file that is not listed must hold none.`n")
    [void]$sb.Append("# Converting a label lowers its file's line in the same commit, and the check`n")
    [void]$sb.Append("# fails on slack as well as on excess, so the numbers stay true. Phase 2 is`n")
    [void]$sb.Append("# provably done the day this file holds no entries.`n")
    [void]$sb.Append("#`n")
    [void]$sb.Append("# One legitimate reason a number goes up: a new rule in cs-ui-sinks.txt makes the`n")
    [void]$sb.Append("# sweep see writes it was blind to. Re-run with -WriteBudget in that commit and`n")
    [void]$sb.Append("# say so in the message.`n")
    [void]$sb.Append("#`n")
    [void]$sb.Append(('# Written {0} from {1} plugin file(s): {2} literal(s) left.' -f
        (Get-Date -Format 'yyyy-MM-dd'), $results.Count, $totalRemaining))
    [void]$sb.Append("`n`n")
    foreach ($r in $rows) { [void]$sb.Append(('{0} {1}' -f $r.Rel, $r.Findings.Count)).Append("`n") }
    Write-LocNewFile $budgetPath $sb.ToString()
    Write-Host ''
    Write-Host ('Wrote {0} ({1} file(s), {2} literal(s)).' -f (Get-LocRelativePath $budgetPath $Root), $rows.Count, $totalRemaining)
} elseif (-not $NoBudget -and $Only -eq '') {
    $budget = Read-LocCsBudget $budgetPath
    if (-not $budget.Exists) {
        $fails.Add('tools/loc/cs-literal-budget.txt does not exist; run sweep-cs.ps1 -WriteBudget to create it.')
    } else {
        foreach ($rel in $budget.Order) {
            $want = $budget.Counts[$rel]
            $got = 0
            if ($remaining.ContainsKey($rel)) { $got = $remaining[$rel] }
            $abs = Join-Path $Root ($rel.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $abs)) {
                $fails.Add(('{0}: in the budget but not on disk; drop the line' -f $rel))
                continue
            }
            if ($got -gt $want) {
                $fails.Add(('{0}: {1} bare UI literal(s), budget {2}. Route the new one through Loc.T/F/N; the budget only goes down.' -f $rel, $got, $want))
            } elseif ($got -lt $want) {
                $fails.Add(('{0}: {1} bare UI literal(s), budget {2}. Lower the budget line to {1} in this commit.' -f $rel, $got, $want))
            }
        }
        foreach ($rel in @($remaining.Keys | Sort-Object)) {
            if ($budget.Counts.ContainsKey($rel)) { continue }
            $fails.Add(('{0}: {1} bare UI literal(s) and no budget line. Route them through Loc.T/F/N, or add a line with -WriteBudget.' -f $rel, $remaining[$rel]))
        }
    }
}

Write-Host ''
if ($fails.Count -gt 0) {
    Write-Host ('FAILED: {0} problem(s)' -f $fails.Count)
    foreach ($f in $fails) { Write-Host ('  ' + $f) }
    exit 1
}
Write-Host 'OK'
exit 0
