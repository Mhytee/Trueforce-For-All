# tools/loc/validate.ps1
#
# Purpose: the localization checks the test suite runs, printed for a
#          translator or a reviewer without a build: no duplicate keys in any
#          Languages\*.json, every {loc:T Key} in XAML and every
#          Loc.T/F/N("Key") in C# exists in en.json, no unreferenced en.json
#          key (Effect_*_Name and EngineLayout_* are dynamic and exempt), Loc.F
#          and Loc.N arity match the {n} placeholders, placeholder parity
#          between each translation and English, and no letter-bearing literal
#          left in the converted scopes of the files converted-files.txt lists
#          (a bare path is the whole file, 'path|spec,spec' only those scopes)
#          unless xaml-keep-literal.txt allows it, and no XAML file carrying
#          {loc:T} references that converted-files.txt does not list. C# is
#          read with comments blanked, so a call quoted in one is not counted.
# Usage:   .\validate.ps1 [-Root <repo>]
#          -Root checks a rehearsal copy; its tools\loc\converted-files.txt is
#          read when it exists, the repo's otherwise.
#          Exit code 0 when everything passes, 1 otherwise.
# Runs under Windows PowerShell 5.1.

param([string]$Root = '')

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '_common.ps1')

if ($Root -eq '') { $Root = $LocRepoRoot }
$plugin = Join-Path $Root 'src\TrueforceForAll.Plugin'
$langDir = Join-Path $plugin 'Languages'
$fails = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

function Get-LocSourceFiles([string]$Dir, [string]$Filter) {
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -Path $Dir -Recurse -Filter $Filter | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
}

# ---- a lexer-lite for C#, mirroring LocalizationTests.cs (IsLiteralStart,
# ---- SkipLiteral, StripComments, CountArgsAfter): literals and comments,
# ---- enough to count top-level commas inside one argument list.

function Test-LocLiteralStart([string]$Text, [int]$I) {
    # A char literal, a regular string, or a verbatim or interpolated string
    # behind one or two of '@' and '$'.
    $c = $Text[$I]
    if ($c -eq '"' -or $c -eq "'") { return $true }
    if ($c -eq '@' -or $c -eq '$') {
        $j = $I
        while ($j -lt $Text.Length -and ($Text[$j] -eq '@' -or $Text[$j] -eq '$')) { $j++ }
        return (($j - $I) -le 2 -and $j -lt $Text.Length -and $Text[$j] -eq '"')
    }
    return $false
}

function Get-LocLiteralEnd([string]$Text, [int]$I) {
    # Index just past the literal starting at I: a char literal, a regular
    # string (backslash escapes), a verbatim string (doubled quotes) or an
    # interpolated string, whose holes may hold code with literals of their
    # own. The runs between the characters that matter are skipped with
    # IndexOfAny, so a long string costs one loop turn per quote or escape.
    $verbatim = $false
    $interpolated = $false
    while ($I -lt $Text.Length -and ($Text[$I] -eq '@' -or $Text[$I] -eq '$')) {
        if ($Text[$I] -eq '@') { $verbatim = $true } else { $interpolated = $true }
        $I++
    }
    if ($I -ge $Text.Length) { return $I }
    $quote = $Text[$I]
    $I++
    if ($quote -eq "'") {
        while ($I -lt $Text.Length -and $Text[$I] -ne "'") {
            if ($Text[$I] -eq '\') { $I++ }
            $I++
        }
        return [Math]::Min($I + 1, $Text.Length)
    }
    if ($quote -ne '"') { return $I }
    $stops = New-Object System.Collections.Generic.List[char]
    $stops.Add([char]'"')
    if (-not $verbatim) { $stops.Add([char]'\') }
    if ($interpolated) { $stops.Add([char]'{') }
    $stopChars = $stops.ToArray()
    while ($I -lt $Text.Length) {
        $n = $Text.IndexOfAny($stopChars, $I)
        if ($n -lt 0) { return $Text.Length }
        $I = $n
        $c = $Text[$I]
        if ($c -eq '"') {
            if ($verbatim -and $I + 1 -lt $Text.Length -and $Text[$I + 1] -eq '"') { $I += 2; continue }
            return $I + 1
        }
        if ($c -eq '\') { $I += 2; continue }
        # An interpolation hole: '{{' is a brace, anything else runs to the
        # matching '}' with its own nesting and literals.
        if ($I + 1 -lt $Text.Length -and $Text[$I + 1] -eq '{') { $I += 2; continue }
        $I++
        $depth = 0
        while ($I -lt $Text.Length) {
            if (Test-LocLiteralStart $Text $I) { $I = Get-LocLiteralEnd $Text $I; continue }
            $h = $Text[$I]
            if ($h -eq '{' -or $h -eq '(' -or $h -eq '[') { $depth++ }
            elseif ($h -eq ')' -or $h -eq ']') { $depth-- }
            elseif ($h -eq '}') {
                if ($depth -eq 0) { $I++; break }
                $depth--
            }
            $I++
        }
    }
    return $I
}

function Remove-LocComments([string]$Text) {
    # Comments become spaces (newlines kept, so line numbers hold); string and
    # char literals pass through untouched, so a "//" inside one is not a
    # comment. Only the characters that can start a literal or a comment are
    # visited; the runs between them are copied whole.
    $sb = New-Object System.Text.StringBuilder($Text.Length)
    $stops = [char[]]@([char]'"', [char]"'", [char]'/', [char]'@', [char]'$')
    $i = 0
    while ($i -lt $Text.Length) {
        $n = $Text.IndexOfAny($stops, $i)
        if ($n -lt 0) { [void]$sb.Append($Text, $i, $Text.Length - $i); break }
        if ($n -gt $i) { [void]$sb.Append($Text, $i, $n - $i); $i = $n }
        if (Test-LocLiteralStart $Text $i) {
            $end = Get-LocLiteralEnd $Text $i
            [void]$sb.Append($Text, $i, $end - $i)
            $i = $end
            continue
        }
        $c = $Text[$i]
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/') {
            $end = $Text.IndexOf("`n", $i)
            if ($end -lt 0) { $end = $Text.Length }
            [void]$sb.Append([char]' ', $end - $i)
            $i = $end
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*') {
            $end = $Text.IndexOf('*/', $i + 2, [System.StringComparison]::Ordinal)
            if ($end -lt 0) { $end = $Text.Length } else { $end += 2 }
            [void]$sb.Append(($Text.Substring($i, $end - $i) -replace '[^\n]', ' '))
            $i = $end
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

function Get-LocGenericListEnd([string]$Text, [int]$Open) {
    # Index just past the '>' that closes the type argument list opened at
    # Open, or -1 when the text up to it is not a type list (letters, digits,
    # '_', '.', ',', '?', nested angle brackets, square brackets, whitespace).
    $depth = 0
    for ($j = $Open; $j -lt $Text.Length; $j++) {
        $c = $Text[$j]
        if ($c -eq '<') { $depth++; continue }
        if ($c -eq '>') { $depth--; if ($depth -eq 0) { return $j + 1 }; continue }
        if (-not ($c -match '[A-Za-z0-9_.,?\[\]\s]')) { return -1 }
    }
    return -1
}

function Get-LocArgCountAfter([string]$Text, [int]$Pos) {
    # Number of arguments that follow position Pos (just past the key literal)
    # up to the call's closing parenthesis: one per top-level comma. Nested
    # parentheses, brackets, braces and literals are skipped whole, and so is
    # a generic type argument list ("Foo<Dictionary<string, int>>()"): a '<'
    # right after an identifier character opens one when everything up to the
    # matching '>' is type-list text. A comparison written without spaces
    # ("a<b, c>d") reads as a generic list; assign such an argument to a
    # local first.
    $depth = 0
    $count = 0
    $i = $Pos
    while ($i -lt $Text.Length) {
        if (Test-LocLiteralStart $Text $i) { $i = Get-LocLiteralEnd $Text $i; continue }
        $c = $Text[$i]
        if ($c -eq '<' -and $i -gt 0 -and ($Text[$i - 1] -match '[A-Za-z0-9_]')) {
            $end = Get-LocGenericListEnd $Text $i
            if ($end -gt 0) { $i = $end; continue }
        }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        elseif ($c -eq ')' -or $c -eq ']' -or $c -eq '}') {
            if ($depth -eq 0) { break }
            $depth--
        } elseif ($c -eq ',' -and $depth -eq 0) { $count++ }
        $i++
    }
    return $count
}

# ---- 1. language files
$langs = @{}
if (Test-Path -LiteralPath $langDir) {
    foreach ($j in Get-ChildItem -Path $langDir -Filter *.json) {
        try {
            $d = Read-LocJson $j.FullName
            $langs[[System.IO.Path]::GetFileNameWithoutExtension($j.Name)] = $d
            foreach ($k in $d.Keys) {
                if ($k -eq '_meta') { continue }
                if (-not ($k -match $LocKeyRegex) -and -not ($k -match '^[A-Za-z][A-Za-z0-9_]*\.(one|other|few|many|zero|two)$')) { $fails.Add("$($j.Name): key '$k' is not a valid identifier") }
                if (-not ($d[$k] -is [string])) { $fails.Add("$($j.Name): key '$k' is not a string") }
            }
        } catch {
            $fails.Add("$($j.Name): $($_.Exception.Message)")
        }
    }
    Write-Host ('Languages: {0}' -f (@($langs.Keys | Sort-Object) -join ', '))
} else {
    $notes.Add('No Languages folder yet ({0}); en.json treated as empty.' -f $langDir)
}
$en = New-Object System.Collections.Specialized.OrderedDictionary
if ($langs.ContainsKey('en')) { $en = $langs['en'] }
$enCount = 0
foreach ($k in $en.Keys) { if ($k -ne '_meta') { $enCount++ } }
Write-Host ('en.json: {0} keys' -f $enCount)

# ---- 2. XAML references
$referenced = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$xamlRefs = 0
# {loc:T} references per repo-relative file, for section 6: a file that
# carries some but has no converted-files.txt line is checked by nobody.
$refsByFile = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
# Positional or Key= form: {loc:T Key} and {loc:T Key=Key}.
$locRe = [regex]'\{loc:T\s+(?:Key\s*=\s*)?([^}]*)\}'
foreach ($x in Get-LocSourceFiles $plugin '*.xaml') {
    $text = (Read-LocTextFile $x.FullName).Text
    $xrel = Get-LocRelativePath $x.FullName $Root
    $refsByFile[$xrel] = 0
    foreach ($m in $locRe.Matches($text)) {
        $key = $m.Groups[1].Value.Trim()
        $line = [regex]::Matches($text.Substring(0, $m.Index), "`n").Count + 1
        $where = '{0}:{1}' -f $xrel, $line
        $xamlRefs++
        $refsByFile[$xrel] = $refsByFile[$xrel] + 1
        if (-not ($key -match $LocKeyRegex)) { $fails.Add("$where`: malformed {loc:T $key}"); continue }
        [void]$referenced.Add($key)
        if (-not $en.Contains($key)) { $fails.Add("$where`: {loc:T $key} is not in en.json") }
    }
}
Write-Host ('XAML: {0} {{loc:T}} references' -f $xamlRefs)

# ---- 3. C# references and arity
$csRefs = 0
# Literal keys only (decision C: no constants class). The literal must be the
# whole first argument (a ',' or ')' follows it): a key built by concatenation
# ("Effect_" + id + "_Name") is dynamic and never matches here.
$callRe = [regex]'\bLoc\.(T|F|N)\(\s*@?"((?:[^"\\]|\\.)*)"\s*(?=[,)])'
$pluralBases = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($cs in Get-LocSourceFiles $plugin '*.cs') {
    # Comments are blanked first (newlines kept), so a call quoted in one is
    # not counted and the line numbers below still hold.
    $text = Remove-LocComments ([System.IO.File]::ReadAllText($cs.FullName))
    foreach ($m in $callRe.Matches($text)) {
        $kind = $m.Groups[1].Value
        $key = $m.Groups[2].Value
        $line = [regex]::Matches($text.Substring(0, $m.Index), "`n").Count + 1
        $where = '{0}:{1}' -f (Get-LocRelativePath $cs.FullName $Root), $line
        $csRefs++
        $argsAfter = Get-LocArgCountAfter $text ($m.Index + $m.Length)
        if ($kind -eq 'N') {
            # LocStore.N(key, n, args): the count is the first argument after
            # the key and is NOT inserted into args, so only what follows it
            # reaches string.Format. Each plural form may use at most that
            # many placeholders and the widest form uses exactly that many
            # (".one" may leave the number out).
            [void]$pluralBases.Add($key)
            foreach ($v in @('one', 'other')) {
                $vk = $key + '.' + $v
                [void]$referenced.Add($vk)
                if (-not $en.Contains($vk)) { $fails.Add("$where`: Loc.N(`"$key`") needs $vk in en.json") }
            }
            if ($argsAfter -lt 1) { $fails.Add("$where`: Loc.N(`"$key`") has no count argument"); continue }
            $values = $argsAfter - 1
            $widest = -1
            $any = $false
            foreach ($v in @('one', 'other')) {
                $vk = $key + '.' + $v
                if (-not $en.Contains($vk)) { continue }
                $any = $true
                $need = (Get-LocMaxPlaceholderIndex ([string]$en[$vk])) + 1
                if ($need -gt $widest) { $widest = $need }
                if ($need -gt $values) { $fails.Add("$where`: Loc.N(`"$key`") passes $values value(s) after the count but $vk uses $need placeholder(s)") }
            }
            if ($any -and $widest -ne $values) { $fails.Add("$where`: Loc.N(`"$key`") passes $values value(s) after the count but its plural forms use at most $widest placeholder(s)") }
            continue
        }
        [void]$referenced.Add($key)
        if (-not $en.Contains($key)) { $fails.Add("$where`: Loc.$kind(`"$key`") is not in en.json"); continue }
        $need = (Get-LocMaxPlaceholderIndex ([string]$en[$key])) + 1
        if ($kind -eq 'F') {
            if ($need -ne $argsAfter) { $fails.Add("$where`: Loc.F(`"$key`") passes $argsAfter argument(s) but the English uses $need placeholder(s)") }
        } elseif ($need -gt 0) {
            $fails.Add("$where`: Loc.T(`"$key`") returns a string with $need placeholder(s) raw; use Loc.F")
        }
    }
}
Write-Host ('C#: {0} Loc.T/F/N references' -f $csRefs)

# ---- 4. unreferenced English keys
$unreferenced = 0
foreach ($k in $en.Keys) {
    if ($k -eq '_meta') { continue }
    if ($referenced.Contains($k)) { continue }
    if ($k -match '^Effect_.+_Name$' -or $k -match '^EngineLayout_') { continue }
    $unreferenced++
    $fails.Add("en.json: '$k' is referenced by no XAML or C#")
}
Write-Host ('en.json: {0} unreferenced key(s)' -f $unreferenced)

# ---- 5. placeholder parity per translation
foreach ($culture in @($langs.Keys | Sort-Object)) {
    if ($culture -eq 'en') { continue }
    $d = $langs[$culture]
    $missing = 0
    $unknown = 0
    $bad = 0
    foreach ($k in $d.Keys) {
        if ($k -eq '_meta') { continue }
        if (-not $en.Contains($k)) { $unknown++; $fails.Add("$culture.json: '$k' is not an English key"); continue }
        if ((Get-LocPlaceholders ([string]$d[$k])) -ne (Get-LocPlaceholders ([string]$en[$k]))) { $bad++; $fails.Add("$culture.json: '$k' placeholders differ from English") }
    }
    foreach ($k in $en.Keys) { if ($k -ne '_meta' -and -not $d.Contains($k)) { $missing++ } }
    Write-Host ('{0}.json: {1} missing (falls back to English), {2} unknown, {3} placeholder mismatch' -f $culture, $missing, $unknown, $bad)
}

# ---- 6. no literals left in the converted scopes of converted XAML files
# Under -Root the rehearsal's own list wins when it has one.
$convertedListPath = Join-Path $Root 'tools\loc\converted-files.txt'
if (-not (Test-Path -LiteralPath $convertedListPath)) { $convertedListPath = Join-Path $LocToolsDir 'converted-files.txt' }
$convertedList = Read-LocConvertedList $convertedListPath
$keepLiteral = Read-LocListFile (Join-Path $LocToolsDir 'xaml-keep-literal.txt')
$commonSet = Read-LocListFile (Join-Path $LocToolsDir 'common-keys.txt')
$literals = 0
$scoped = 0
foreach ($entry in $convertedList) {
    $rel = $entry.Path
    $abs = Join-Path $Root ($rel.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $abs)) { $fails.Add("converted-files.txt names $rel, which does not exist"); continue }
    if ($null -ne $entry.Specs) { $scoped++ }
    $inv = Get-LocInventory $abs $rel $commonSet
    $ctx = Get-LocScopeContext $inv.Doc
    foreach ($r in $inv.Rows) {
        if ($r.skipReason -ne '') { continue }
        # A scoped line checks only the rows inside its scopes.
        if ($null -ne $entry.Specs -and -not (Test-LocInScope $r $entry.Specs $ctx)) { continue }
        if ($keepLiteral.Contains($r.value)) { continue }
        $ln = $r.Element.Name.LocalName
        if ($r.attribute -eq 'Text' -and ($ln -eq 'TextBox' -or $ln -eq 'PasswordBox' -or $ln -eq 'RichTextBox' -or $ln -eq 'ComboBox')) { continue }
        $literals++
        $fails.Add(('{0}:{1} {2} {3}="{4}" is still a literal' -f $rel, $r.line, $r.xName, $r.attribute, $r.value))
    }
}
# A file that carries {loc:T} references but has no converted-files.txt line
# is checked by nobody: neither its scopes nor its literals.
$listed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($entry in $convertedList) { [void]$listed.Add($entry.Path) }
foreach ($f in @($refsByFile.Keys | Sort-Object)) {
    if ($refsByFile[$f] -eq 0 -or $listed.Contains($f)) { continue }
    $fails.Add(('{0} has {1} {{loc:T}} reference(s) but no converted-files.txt line, so none of it is checked for literals' -f $f, $refsByFile[$f]))
}
Write-Host ('Converted XAML files: {0} ({1} scoped), literals left: {2}' -f $convertedList.Count, $scoped, $literals)

# ---- verdict
foreach ($n in $notes) { Write-Host ('note: ' + $n) }
if ($fails.Count -gt 0) {
    Write-Host ''
    Write-Host ('FAILED: {0} problem(s)' -f $fails.Count)
    foreach ($f in $fails) { Write-Host ('  ' + $f) }
    exit 1
}
Write-Host 'OK'
exit 0
