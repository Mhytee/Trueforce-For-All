# tools/loc/validate.ps1
#
# Purpose: the localization checks the test suite runs, printed for a
#          translator or a reviewer without a build: no duplicate keys in any
#          Languages\*.json, every {loc:T Key} in XAML and every
#          Loc.T/F/N("Key") or new Binding("[Key]") in C# exists in en.json,
#          no unreferenced en.json
#          key (Effect_*_Name and EngineLayout_* are dynamic and exempt), Loc.F
#          and Loc.N arity match the {n} placeholders, placeholder parity
#          between each translation and English, the same leading and
#          trailing whitespace as English per key, and no letter-bearing literal
#          left in the converted scopes of the files converted-files.txt lists
#          (a bare path is the whole file, 'path|spec,spec' only those scopes)
#          unless xaml-keep-literal.txt allows it, no XAML file carrying
#          {loc:T} references that converted-files.txt does not list, no value
#          holding a literal escape (\n or \" as text, from a C# literal keyed
#          verbatim), and no translated string wider than the fixed-width
#          control it is drawn in, measured with WPF's own text engine. C# is
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

# The C# lexer-lite this file used to carry (Test-LocLiteralStart,
# Get-LocLiteralEnd, Remove-LocComments, Get-LocGenericListEnd,
# Get-LocArgCountAfter) moved into _common.ps1 when sweep-cs.ps1 needed the same
# reader, so there is one copy rather than one per script. Its C# mirror is still
# LocalizationTests.cs (IsLiteralStart, SkipLiteral, StripComments,
# CountArgsAfter).

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
$csBinds = 0
# Literal keys only (decision C: no constants class). The literal must be the
# whole first argument (a ',' or ')' follows it): a key built by concatenation
# ("Effect_" + id + "_Name") is dynamic and never matches here.
$callRe = [regex]'\bLoc\.(T|F|N)\(\s*@?"((?:[^"\\]|\\.)*)"\s*(?=[,)])'
# The second C# reference form: a Binding straight to the store's indexer,
# new Binding("[Key]") { Source = Loc.Instance, ... }, which is how a label set
# once at wire time follows a language change without a relabel pass. Same
# shape TExtension.ProvideValue builds for {loc:T}, so the key is spelled
# inside the path brackets and a concatenated path stays dynamic.
$bindRe = [regex]'\bnew\s+Binding\(\s*"\[([A-Za-z][A-Za-z0-9_.]*)\]"\s*\)'
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
    foreach ($m in $bindRe.Matches($text)) {
        $key = $m.Groups[1].Value
        $line = [regex]::Matches($text.Substring(0, $m.Index), "`n").Count + 1
        $where = '{0}:{1}' -f (Get-LocRelativePath $cs.FullName $Root), $line
        $csBinds++
        [void]$referenced.Add($key)
        if (-not $en.Contains($key)) { $fails.Add("$where`: new Binding(`"[$key]`") is not in en.json"); continue }
        # A bound path cannot carry arguments, so a value with placeholders
        # would reach the label as "{0}".
        $need = (Get-LocMaxPlaceholderIndex ([string]$en[$key])) + 1
        if ($need -gt 0) { $fails.Add("$where`: new Binding(`"[$key]`") binds a string with $need placeholder(s); bind a key without placeholders") }
    }
}
Write-Host ('C#: {0} Loc.T/F/N references, {1} store binding(s)' -f $csRefs, $csBinds)

# ---- 4. unreferenced English keys
$unreferenced = 0
foreach ($k in $en.Keys) {
    if ($k -eq '_meta') { continue }
    if ($referenced.Contains($k)) { continue }
    # Looked up by a runtime id, never by a literal: the effect names and engine
    # layouts, plus three tables that pair an id with a key name because the sweep
    # cannot tell an id from a label inside one initializer.
    if ($k -match '^(Effect_.+_Name$|EngineLayout_|EffectTag_|Report_Category|PatternEditor_Dir)') { continue }
    $unreferenced++
    $fails.Add("en.json: '$k' is referenced by no XAML, no Loc.T/F/N call and no store binding")
}
Write-Host ('en.json: {0} unreferenced key(s)' -f $unreferenced)

# ---- 5. placeholder and edge-whitespace parity per translation
foreach ($culture in @($langs.Keys | Sort-Object)) {
    if ($culture -eq 'en') { continue }
    $d = $langs[$culture]
    $missing = 0
    $unknown = 0
    $bad = 0
    $space = 0
    foreach ($k in $d.Keys) {
        if ($k -eq '_meta') { continue }
        if (-not $en.Contains($k)) { $unknown++; $fails.Add("$culture.json: '$k' is not an English key"); continue }
        if ((Get-LocPlaceholders ([string]$d[$k])) -ne (Get-LocPlaceholders ([string]$en[$k]))) { $bad++; $fails.Add("$culture.json: '$k' placeholders differ from English") }
        $want = Get-LocEdgeSpace ([string]$en[$k])
        $got = Get-LocEdgeSpace ([string]$d[$k])
        if ($want -ne $got) { $space++; $fails.Add("$culture.json: '$k' leading and trailing whitespace differs from English (English $want, translation $got)") }
    }
    foreach ($k in $en.Keys) { if ($k -ne '_meta' -and -not $d.Contains($k)) { $missing++ } }
    Write-Host ('{0}.json: {1} missing (falls back to English), {2} unknown, {3} placeholder mismatch, {4} whitespace mismatch' -f $culture, $missing, $unknown, $bad, $space)
}

# ---- 5b. an escape captured verbatim instead of the character it stands for
# A C# literal keyed by hand can arrive with its own escape still in the text, so
# the dialog shows \n or \" where it should show a blank line or a quote. Paths
# keep their backslashes, so only the sequences C# would have escaped are refused.
$escapes = 0
foreach ($culture in @($langs.Keys | Sort-Object)) {
    $d = $langs[$culture]
    foreach ($k in $d.Keys) {
        if ($k -eq '_meta') { continue }
        $v = [string]$d[$k]
        foreach ($seq in @('\n', '\t', '\"', '\\')) {
            if ($v.Contains($seq)) {
                $escapes++
                $fails.Add("$culture.json: '$k' holds the literal text $seq; use the character itself")
                break
            }
        }
    }
}
Write-Host ('Languages: {0} value(s) hold a literal escape sequence' -f $escapes)
# ---- 5c. localized text that cannot fit the control it sits in
# Validate-only, unlike the checks above: it measures with WPF's own text engine, which
# the net8 test suite cannot load. The scan is Get-LocFitSites in _common.ps1, shared
# with fit-budget.ps1, so this check and the table the Translate window warns from can
# only disagree by being out of date, which the staleness check below catches.
#
# The chrome allowance is an estimate of border, padding and a checkbox's own box, so a
# few pixels over is a note and only a real overshoot fails the run.
$fitSlack = 4.0
$fitChecked = 0
$fitBad = 0
$fitSites = @()
try {
    $fitSites = @(Get-LocFitSites $plugin $Root)
    $null = Get-LocTextWidth 'x' $LocFitFontSize
} catch {
    $fitSites = @()
    $notes.Add('The fixed-width fit check was skipped: ' + $_.Exception.Message)
}
foreach ($culture in @($langs.Keys | Sort-Object)) {
    $d = $langs[$culture]
    foreach ($site in $fitSites) {
        if (-not $d.Contains($site.Key)) { continue }
        $s = [string]$d[$site.Key]
        if ($s -eq '') { continue }
        $fitChecked++
        $over = [math]::Round((Get-LocTextWidth $s $LocFitFontSize) - $site.Room, 1)
        if ($over -le 0) { continue }
        $msg = ("{0}.json: '{1}' needs about {2}px more than the {3}px it has at {4} (`"{5}`")" -f `
                $culture, $site.Key, $over, $site.Room, $site.Where, $s)
        if ($over -gt $fitSlack) { $fitBad++; $fails.Add($msg) } else { $notes.Add($msg) }
    }
}
Write-Host ('Fixed-width fit: {0} string(s) measured in {1} tight spot(s), {2} too wide' -f `
    $fitChecked, $fitSites.Count, $fitBad)

# The Translate window warns a translator from LocFitBudget.cs, so a stale table leaves
# it quiet about a control that has since become tight. Skipped under -Root, where the
# generator would regenerate from the real repo rather than the copy being checked.
$fitBudgetScript = Join-Path $LocToolsDir 'fit-budget.ps1'
if ($Root -eq $LocRepoRoot -and $fitSites.Count -gt 0 -and (Test-Path -LiteralPath $fitBudgetScript)) {
    $fitOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $fitBudgetScript -Check 2>&1
    if ($LASTEXITCODE -ne 0) { $fails.Add(('LocFitBudget.cs: ' + (($fitOut | Out-String).Trim() -replace "`r?`n", ' '))) }
    else { Write-Host ('  ' + (($fitOut | Out-String).Trim())) }
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
