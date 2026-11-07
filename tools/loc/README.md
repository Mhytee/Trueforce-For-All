# Localization tooling

PowerShell 5.1 scripts that move the panel's English out of XAML and into
`src\TrueforceForAll.Plugin\Languages\en.json`, one reviewed slice at a time.
Design, phases and the identifier couplings they must respect are in
`docs/localization-plan.md`. Every script starts with `Set-StrictMode -Version 2`
and stops on the first error; `_common.ps1` holds the shared rules and is
dot-sourced, never run.

## The six scripts

| Script | What it does |
| --- | --- |
| `inventory.ps1 -Xaml <path>[,...] -Out <csv>` | One row per candidate string (attribute value, Setter value or text node) with a `skipReason`: empty for a real string, else `binding`, `numeric`, `glyph`, `identifier` or `empty`. Denylist approach: every attribute is a candidate except the identifier and layout attributes listed in `_common.ps1`. Prints per-file and grand totals. `-ResolveWith <en.json> -Baseline <csv>` re-inventories converted files with the English resolved and fails on the first value that differs from the baseline, in order. |
| `keys.ps1 -Inventory <csv> -Scope <spec>[,...] -Out <csv>` | Proposes `<Area>_<Meaning>[<Suffix>]` for every real string in scope, plus the translator-context columns `siblingNames`, `helpText` and `ownToolTip`. Scopes use the grammar below plus `file:<name>` for a whole file; several go as `@('a','b')` or one comma-joined string, and a spec that matches no real string is an error. The inventory must be a fresh `inventory.ps1` run, not the dated baseline. Values listed in `common-keys.txt` share `Common_<Slug>`. Collisions get `_2`, `_3` and a `reviewFlag`; so do values the code-behind overwrites (`code-assigned`) and unit-like values. TextBox and ComboBox `Text` and the values in `xaml-keep-literal.txt` are excluded and listed on the console. |
| `convert-xaml.ps1 -Keys <csv> [-DryRun]` | Replaces each listed value with `{loc:T Key}` and merges the English into `en.json`. Each row must match exactly one occurrence on its recorded line, on the element with its recorded `x:Name` or path, or nothing is written. Files are read and written as bytes: BOM and line endings survive, nothing outside the replaced spans changes, and the result must parse as XML. Adds `xmlns:loc` to the root when missing and records the scopes it converted on the file's line in `converted-files.txt`. Element text moves to an attribute only on TextBlock, Label, Run, Button, CheckBox, RadioButton, ComboBoxItem and ListBoxItem (Hyperlink text is wrapped in a Run); a property element such as `Button.Content` or any other element is refused with "convert by hand". `-DryRun` prints the plan, including the scopes each file would get, and writes nothing. |
| `pseudo.ps1 -In en.json -Out qps-ploc.json` | Accent-swaps every value, pads it 30 percent with `~`, wraps it in brackets and keeps `{n}` placeholders intact. |
| `validate.ps1 [-Root <repo>]` | The test suite's checks, printed: no duplicate key in any `Languages\*.json`, every `{loc:T Key}` (positional or `Key=` form) and `Loc.T/F/N("Key")` exists in `en.json`, every `new Binding("[Key]")` bound straight to the store names a key that exists and holds no placeholders, no unreferenced key (`Effect_*_Name` and `EngineLayout_*` exempt), `Loc.F` arity against the placeholders, `Loc.N` arity against the values after the count (the count itself is not a format argument), placeholder parity per translation, the same leading and trailing whitespace as English per translated key (a value that ends in a space butts against its neighbor in one sentence, and a translator cannot see it), and no literal left in the converted scopes of each file `converted-files.txt` lists. C# call sites use string literal keys, `Loc.T("Key")`; there is no generated constants class (decision of 2026-09-24). A label a helper sets once at wire time binds instead of assigning, `new Binding("[Key]") { Source = Loc.Instance, Mode = BindingMode.OneWay }`, the shape `{loc:T}` itself produces, so it follows a language change with no relabel pass; the checks count that as a reference too. A key built by concatenation is dynamic: the checks never see it, and the `en.json` keys it reaches must belong to an exempt family. Exit code 1 on any failure. |
| `sweep-cs.ps1 [-Detail] [-Indirect] [-Prose] [-WriteBudget] [-NoBudget] [-Only <file>]` | The C# half: every literal that reaches a UI sink and is not routed through `Loc.T/F/N`, counted per file and held against `cs-literal-budget.txt`. `-Prose` is the wider report, for finding what the sink rules do not reach. Its own section is below. |

## Scopes

A scope names part of a XAML file. `keys.ps1 -Scope` takes scopes, and a
`converted-files.txt` line carries them when only part of a file is converted.
`<name>` is the file name or its repo-relative path.

| Spec | Covers |
| --- | --- |
| `resources:<name>` | elements inside an element whose local name ends with `.Resources` (`UserControl.Resources`, `Window.Resources`) |
| `header:<name>` | elements before the first `sh:SHTabItem` in document order and not inside Resources; in a file without tabs, everything outside Resources |
| `tab:<x:Name>` | descendants of the `sh:SHTabItem` with that `x:Name`, for example `tab:SupportTab` |
| `trailer:<name>` | elements after the last `sh:SHTabItem` |
| `file:<name>` | the whole file (`keys.ps1` only; `converted-files.txt` lists a whole file as a bare path) |

Area names follow the scopes in `SettingsControl.xaml`: `Resources`, `Header`,
the tab's stem (`SupportTab` becomes `Support`) and `Trailer`.

## The list files and the baseline

- `common-keys.txt`: values that share one key wherever they appear verbatim.
  Opt-in per value after a sense check ("Save…" means one thing 19 times;
  "Strength:" has four meanings and is not listed). A listed value counts as a
  real string whatever its length, which is how "NEW" and "OK" get keys.
- `xaml-keep-literal.txt`: values allowed to stay literal in converted files.
  The ten FxTestEffectBox protocol names, the repository address used as a
  tooltip, and the lovely-car-data license tag.
- `converted-files.txt`: what has been converted, one XAML file per line. A
  bare repo-relative path means the whole file. `<path>|<spec>[,<spec>...]`
  means only those scopes (`resources:<file>`, `header:<file>`, `tab:<xName>`,
  `trailer:<file>`), for example
  `src/TrueforceForAll.Plugin/SettingsControl.xaml|resources:SettingsControl.xaml,tab:SupportTab`.
  `convert-xaml.ps1` derives the specs from the rows it converted and merges
  them into the file's line: a new line for a first slice, a union for the
  next ones; a bare path stays bare, and a file without `sh:SHTabItem`
  (`MotdStrip.xaml`, `PresetManagerControl.xaml`, `CustomEngineEditor.xaml`)
  is always recorded whole. Once every scope of a file is converted, collapse
  its line to the bare path by hand. `validate.ps1` and the C# no-literals and
  round-trip tests check only the rows inside a line's scopes; the
  walker-agreement test compares the rows outside them against the baseline.
  It starts empty; the converter adds a line per converted file, so after the
  rehearsal it names SettingsControl.xaml (scoped), PresetManagerControl.xaml
  and MotdStrip.xaml.
- `baseline\xaml-2026-09-28.csv`: the inventory of the four XAML files before
  any conversion. The round-trip test re-inventories the converted files with
  keys resolved and expects the same real strings, byte-identical, in order.
  It is the round-trip reference only: after a conversion its rows no longer
  match the files, so it is never `keys.ps1` input again. When UI text changes
  outside the converted scopes, test 3 reports rows the baseline lacks or rows
  the walk did not produce; if the change is intended, regenerate the baseline
  in place with
  `.\inventory.ps1 -Xaml ..\..\src\TrueforceForAll.Plugin\SettingsControl.xaml, ..\..\src\TrueforceForAll.Plugin\PresetManagerControl.xaml, ..\..\src\TrueforceForAll.Plugin\MotdStrip.xaml, ..\..\src\TrueforceForAll.Plugin\CustomEngineEditor.xaml -ResolveWith ..\..\src\TrueforceForAll.Plugin\Languages\en.json -Out baseline\xaml-2026-09-28.csv`
  (overwrite it; a second CSV owning the same file makes the tests throw).
  Run it from PowerShell itself: `powershell -File` splits the comma list
  into separate arguments and the script refuses them.
  Keep the four files in that order: the CSV's file blocks follow the
  argument order, and the baseline's hash depends on it.
  Converted rows come back as their English text, so the round trip still
  holds, and the refreshed CSV is still never `keys.ps1` input.

## Converting a slice

1. `inventory.ps1` over the file, into a scratch CSV. Always a fresh run: the
   dated baseline is the round-trip reference and is never reused as
   `keys.ps1` input once anything has been converted.
2. `keys.ps1` for the slice's scope, from that CSV. Owner reviews the output:
   renames keys, moves shared values into `common-keys.txt`, resolves every
   `reviewFlag`.
3. `convert-xaml.ps1 -DryRun`, read the plan, then run it without `-DryRun`.
   To keep the plan: `.\convert-xaml.ps1 -Keys x.csv -DryRun *>&1 | ForEach-Object { $_.ToString() }`.
4. Build, then `validate.ps1` and the Core.Tests suite. Deploy the three DLLs
   and compare the panel against pre-change screenshots.

Anchoring rule: a replacement matches on `x:Name` (or path), attribute and
exact value; line numbers are audit columns. Never edit a converted file with a
scripted whole-file rewrite; the BOM and line-ending mix in this repo is why
the converter splices bytes.

To rehearse without touching the repo, copy the XAML files into another folder
with the same `src\TrueforceForAll.Plugin\` layout and pass `-Root <folder>` to
`keys.ps1`, `convert-xaml.ps1`, `inventory.ps1` and `validate.ps1`. The tree
should also carry the plugin's `.cs` files (at least the code-behind of the
rehearsed XAML), because `keys.ps1 -Root` scans them for the code-assigned
review flags, and a XAML-only tree silently drops those flags. `-Root`
also relocates `en.json`: without `-EnJson` the scripts use
`<Root>\src\TrueforceForAll.Plugin\Languages\en.json`, and the converter
refuses to write an `en.json` under the repo from another root unless
`-EnJson` names it explicitly. Under another root the converter records the
scopes in `<Root>\tools\loc\converted-files.txt` when that folder exists, the
same file `validate.ps1 -Root` reads; the repo's `converted-files.txt` is never
touched from another root.

## The C# sweep and the literal budget

`validate.ps1` asks whether every key a call names exists. `sweep-cs.ps1` asks
the opposite question, the one Phase 2 of `docs/localization-plan.md` is measured
by: is any string a user reads still a bare English literal in code? It finds
every literal that reaches a UI sink and is not routed through `Loc.T/F/N`,
prints the count per file and a total, and holds the per-file numbers against
`cs-literal-budget.txt`. `LocCsLiteralBudget` in
`src/TrueforceForAll.Core.Tests/LocalizationTests.cs` recomputes the same
numbers in C# from the same three data files, so the budget is enforced by
`dotnet test` as well as by hand.

| Command | What it does |
| --- | --- |
| `.\sweep-cs.ps1` | Counts, then checks the budget. Exit 1 on any problem. |
| `.\sweep-cs.ps1 -Detail` | Every remaining literal as `file:line sink [text]`. `-Only <filename>` narrows the listing. |
| `.\sweep-cs.ps1 -Indirect` | The sinks whose value arrives as a bare identifier, grouped by the member they sit in. This is the tool's own blind-spot report: the literal, if any, is at the caller. |
| `.\sweep-cs.ps1 -WriteBudget` | Rewrites `cs-literal-budget.txt` from today's counts. |
| `.\sweep-cs.ps1 -NoBudget` | Counts only, no verdict. |
| `.\sweep-cs.ps1 -Prose` | Every literal in the plugin that reads like a sentence and is not routed through `Loc`, whether or not it reaches a known sink, grouped by the member it sits in. A report, not a check: nothing fails on its output and the C# test does not mirror it. This is how the next blind spot gets found. |

The check fails when a file holds **more** than its budget (a label was written
in English, or moved into a file whose number was lower), when it holds
**fewer** (a slice converted labels without lowering its line, so the number
stops being true), when a file with no budget line holds any at all, when the
budget names a file that is gone, and when a method on a watched receiver is
neither a call sink nor a nonsink. Lowering a number is part of the slice's own
diff; `-WriteBudget` does it. Phase 2 is provably done the day
`cs-literal-budget.txt` holds no entries.

### The three data files

- `cs-ui-sinks.txt`: what a UI sink is, and the only place the rule lives. Seven
  display properties (`Text`, `Content`, `Header`, `ToolTip`, `Title`, plus
  `Watermark` and `PlaceholderText` for the first one written), both as
  `label.Text = ...` and as a `Text = ...` entry in an object initializer, `+=`
  included; `new Run(...)`; the `TrueforceDialog` entry points with their title,
  body and button labels, selected both by position and by parameter name
  because the same overload is called both ways; `SetValue(X.YProperty, ...)`;
  the status-label helpers by shape (`callre Set[A-Za-z]*Status[A-Za-z]*`); and
  the indirect sinks, the plugin's own helpers that take text and write it to a
  label, which is where the literal actually lives. `skipcall` names a call whose
  literals never reach a user (a `Loc` key, a `ToString` format specifier) and
  `textcall` one that composes the text it is handed (`string.Format`). `watch
  TrueforceDialog` makes every method on that type either a listed sink or a
  listed nonsink, so a new overload cannot quietly become a blind spot. Three
  directives name a region rather than an assignment, because not every label is
  written to a property: `labels <Identifier>` counts the entries of a declared
  collection of labels, `textmember <Name>` counts every literal a member
  returns, and `recordprop <Type>.<Prop>` counts an initializer entry on one of
  our own records, which is what tells `GuideEntry.ActionLabel` from a field
  called `ActionLabel` on something that is not a record. A parallel key or value
  array (`IdleStyleKeys`, `FieldKeys`, `IdleColorHex`) is deliberately absent
  from the labels list, and so are the OLED wheel-screen short names, which
  cannot carry a translation at all.
- `cs-keep-literal.txt`: what stays English, with a comment per entry naming what
  the plan says about it. `file:` for developer tooling (DevCodes.cs,
  TestCodesWindow.cs), `type:` for a data record whose property is spelled like
  a UI sink (`ChangelogVersion.Title`, `BackupFile.Text`), `value:` for a single
  string (the OLED greeting's factory text, and the six typeface names the font
  picker lists beside "Default", which is translated). Scope only: a string that is merely
  awkward to translate still gets a key.
- `cs-literal-budget.txt`: `<repo-relative path> <count>` per line, generated.

### What the sweep sees, and what it does not

Covered: a dot-form or object-initializer assignment to a display property,
including `+=` and a value built by concatenation or by a ternary; a window
`Title` written as a bare statement; `new Run(...)`; the dialog call sites; the
status helpers; the indirect helpers listed in `cs-ui-sinks.txt`; a literal
inside `string.Format`. Comments are blanked before anything is matched, so a
sink quoted in one is never counted, and a sink matched inside a string literal
is dropped by the frame pass.

Deliberately not counted: a literal being compared rather than shown
(`kind == "car" ? A : B`), a `ToString` format specifier, a lookup key or any
other argument to a call the rules do not name, and an interpolated string whose
only letters come from inside its holes (an interpolated string of two holes
and a separator has nothing to translate). Placeholders come out of the value before its letters are counted,
so `"{0} items"` is a real string and `"{0:X2}{1:X2}{2:X2}"` is not.

Not visible at all, and the reason `-Indirect` exists:

- a sentence assembled into a local and written to a label later in the method
  (`string outcome = "..."; ... Status.Text = outcome;`)
- a sentence built by a helper of the plugin's own that the rules do not name,
  or three methods away through an `AppendLine` chain
- a helper called through a receiver the one-part call rules cannot reach
- the roughly 1,000 `[TF4ALL]` log lines, the access-code replies and the car
  data tables, which are not UI sinks and so never enter the count in the first
  place (this is why they need no allowlist entry)

Adding a rule to `cs-ui-sinks.txt` is the one legitimate reason a budget number
goes up: the sweep starts seeing writes it was blind to. Re-run with
`-WriteBudget` in that commit and say so in the message.

### Known blind

The budget counts literals written AT a sink, where a sink is what
`cs-ui-sinks.txt` names. An empty budget file is a necessary condition for Phase 2
rather than a sufficient one, so the second question has its own report.

**What the rules cover now.** The three directives added on 2026-09-26 started with
a named subset of their kind and were extended the same day from a scan of every
prose literal in the plugin, which is what `-Prose` runs:

| Directive | Seeded with | Now |
| --- | --- | --- |
| `labels <Identifier>` | 15 collections | 14, and all fourteen resolve on every read rather than at type load, because a static initializer freezes the language and can run before `Loc.Initialize` |
| `textmember <Name>` | 2 members | 47, including the status and advice layer, the three native-Trueforce notices, the install and uninstall results and the link flows' error tuples |
| `recordprop <Type>.<Prop>` | 4 pairs | 11 over 9 property names |
| `prop <Name>` | 7 WPF properties | 34, the extra ones being locals and controller fields that hold a sentence on its way to a control |

Four shapes that were wholly blind are not any more:

- A `StringBuilder` line. `AppendLine` and `Append` are sinks, which found the FFB
  self-test checklist and the preset details panel. Three builders that write a log
  line, a translator report or a CSV are named in `cs-keep-literal.txt` instead.
- One hop through a local. The locals that hold display text are named in the
  `prop` list, and the two that hold Markdown for a GitHub issue are called
  `issueTitle` and `issueBody` so the name says which is which.
- A member written as an expression body. `textmember` was narrowed to return
  position, which left out `Name => expr;`, where the member IS its return. An
  arrow region is one return span now.
- `return (a, "text")`. The literal collector read that as a call to something
  named return and stepped over it, which hid both link clients' error messages. A
  keyword before a bracket means a grouping parenthesis in both walkers.

**What is still not covered**, from the `-Prose` report (434 literals in 177
members on 2026-09-26, and the number in a fresh run is the one to trust):

| Shape | Roughly | Why it stays |
| --- | --- | --- |
| Readme and export text | 60 | The folder readmes, the pack readme and the log export write files, not labels |
| GitHub issue and discussion bodies | 25 | Written for the project to read, in English, and escaped into a URL |
| Access-code and Mode B surfaces | 60 | Developer tooling, per the plan |
| Exception messages | 50 | `TrueforceDialog.ShowError` logs `ex.Message` and shows the caller's own sentence, so these never reach a user |
| Core and Engine | 4 sentences | Not walked at all. `UsbPcapFfbTap.Status` is composed into `FfbTapStatus`, which the panel polls into a label |
| The rest | 1 to 3 per member, across about 150 members | Each needs a judgement about that member, which is what the report is for |

Naming more instances of a covered shape, or teaching the sweep a new one, is the
one legitimate reason a budget number goes up. Re-run with `-WriteBudget` in that
commit and say so in the message.

Two things the number is not. It counts literals, not translation units, so one
sentence split across four concatenated fragments counts four; the chain joiner
turns those into one key each time it can, which is why the count fell faster than
the key count rose. It also counts only what reaches a sink, so on its own it is at
once an overcount of strings a translator will type and an undercount of English a
user can read. Read it with the `-Prose` report beside it.

## Rules worth knowing

- Keys match `^[A-Za-z][A-Za-z0-9_]*$` and freeze at the first shipped
  release: after that a key is never renamed or reused for other text, so
  translations and root overrides keep matching.
- On a collision, an unnamed element first retries with six words of its text;
  when that still collides (or the elements are named) the later rows get
  `_2`, `_3`. Every row in the group carries the `collision` flag so the
  reviewer sees the whole set.
- Area is the enclosing `SHTabItem` name without `Tab`, `Resources` inside
  `UserControl.Resources`, `Header` for the rest of `SettingsControl.xaml`
  before the first tab, `Trailer` for what follows the last tab, and
  `PresetManager`, `EngineEditor`, `Motd` for the other files. Meaning is the
  element's `x:Name` with a control suffix stripped, or the nearest named
  ancestor's stem plus the first words of the text.
- A value is a real string when it has a letter and any of: it contains a
  space; it has four or more letters; it is listed verbatim in
  `common-keys.txt`; or (the short-token rule) it has two or more letters on a
  `Content`, `Header`, `Text`, `Title` or text-node attribute of a
  ComboBoxItem, RadioButton, Button, CheckBox, TextBlock, Label, Run,
  Hyperlink, MenuItem, TabItem, SHTabItem, GroupBox or Expander. That is how
  "Off" on a ComboBoxItem, "RPM" on a TextBlock and "Set" on a Button get
  keys while the "R", "G", "B" channel labels stay `glyph`. A Setter's
  `Setter:Text` never qualifies; list the value in `common-keys.txt` when it
  should. The C# walker in `LocalizationTests.cs` carries the same two lists.
- `Text` on a TextBox or editable ComboBox is never extracted: the code parses
  it back ("250 ms", "2.5 s").
- The denylist in `_common.ps1` has the contract's attributes plus a short
  list of additions found on the real files (more event handlers, enum-valued
  layout attributes, `ActionName`). `FriendlyName` on `shui:ControlsEditor`
  stays a candidate on purpose: SimHub shows it to the user.
- The scripts are LF, no BOM, and pure ASCII. PowerShell 5.1 reads a BOM-less
  script in the ANSI code page, so a non-ASCII literal in a script is a parse
  error waiting to happen; spell such characters as code points.
