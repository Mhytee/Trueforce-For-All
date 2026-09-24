# Localization tooling

PowerShell 5.1 scripts that move the panel's English out of XAML and into
`src\TrueforceForAll.Plugin\Languages\en.json`, one reviewed slice at a time.
Design, phases and the identifier couplings they must respect are in
`docs/localization-plan.md`. Every script starts with `Set-StrictMode -Version 2`
and stops on the first error; `_common.ps1` holds the shared rules and is
dot-sourced, never run.

## The five scripts

| Script | What it does |
| --- | --- |
| `inventory.ps1 -Xaml <path>[,...] -Out <csv>` | One row per candidate string (attribute value, Setter value or text node) with a `skipReason`: empty for a real string, else `binding`, `numeric`, `glyph`, `identifier` or `empty`. Denylist approach: every attribute is a candidate except the identifier and layout attributes listed in `_common.ps1`. Prints per-file and grand totals. `-ResolveWith <en.json> -Baseline <csv>` re-inventories converted files with the English resolved and fails on the first value that differs from the baseline, in order. |
| `keys.ps1 -Inventory <csv> -Scope <spec>[,...] -Out <csv>` | Proposes `<Area>_<Meaning>[<Suffix>]` for every real string in scope, plus the translator-context columns `siblingNames`, `helpText` and `ownToolTip`. Scopes use the grammar below plus `file:<name>` for a whole file; several go as `@('a','b')` or one comma-joined string, and a spec that matches no real string is an error. The inventory must be a fresh `inventory.ps1` run, not the dated baseline. Values listed in `common-keys.txt` share `Common_<Slug>`. Collisions get `_2`, `_3` and a `reviewFlag`; so do values the code-behind overwrites (`code-assigned`) and unit-like values. TextBox and ComboBox `Text` and the values in `xaml-keep-literal.txt` are excluded and listed on the console. |
| `convert-xaml.ps1 -Keys <csv> [-DryRun]` | Replaces each listed value with `{loc:T Key}` and merges the English into `en.json`. Each row must match exactly one occurrence on its recorded line, on the element with its recorded `x:Name` or path, or nothing is written. Files are read and written as bytes: BOM and line endings survive, nothing outside the replaced spans changes, and the result must parse as XML. Adds `xmlns:loc` to the root when missing and records the scopes it converted on the file's line in `converted-files.txt`. Element text moves to an attribute only on TextBlock, Label, Run, Button, CheckBox, RadioButton, ComboBoxItem and ListBoxItem (Hyperlink text is wrapped in a Run); a property element such as `Button.Content` or any other element is refused with "convert by hand". `-DryRun` prints the plan, including the scopes each file would get, and writes nothing. |
| `pseudo.ps1 -In en.json -Out qps-ploc.json` | Accent-swaps every value, pads it 30 percent with `~`, wraps it in brackets and keeps `{n}` placeholders intact. |
| `validate.ps1 [-Root <repo>]` | The test suite's checks, printed: no duplicate key in any `Languages\*.json`, every `{loc:T Key}` (positional or `Key=` form) and `Loc.T/F/N("Key")` exists in `en.json`, no unreferenced key (`Effect_*_Name` and `EngineLayout_*` exempt), `Loc.F` arity against the placeholders, `Loc.N` arity against the values after the count (the count itself is not a format argument), placeholder parity per translation, and no literal left in the converted scopes of each file `converted-files.txt` lists. C# call sites use string literal keys, `Loc.T("Key")`; there is no generated constants class (decision of 2026-09-24). A key built by concatenation is dynamic: the checks never see it, and the `en.json` keys it reaches must belong to an exempt family. Exit code 1 on any failure. |

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
- `baseline\xaml-2026-09-24.csv`: the inventory of the four XAML files before
  any conversion. The round-trip test re-inventories the converted files with
  keys resolved and expects the same real strings, byte-identical, in order.
  It is the round-trip reference only: after a conversion its rows no longer
  match the files, so it is never `keys.ps1` input again. When UI text changes
  outside the converted scopes, test 3 reports rows the baseline lacks or rows
  the walk did not produce; if the change is intended, regenerate the baseline
  in place with
  `.\inventory.ps1 -Xaml ..\..\src\TrueforceForAll.Plugin\SettingsControl.xaml, ..\..\src\TrueforceForAll.Plugin\PresetManagerControl.xaml, ..\..\src\TrueforceForAll.Plugin\CustomEngineEditor.xaml, ..\..\src\TrueforceForAll.Plugin\MotdStrip.xaml -ResolveWith ..\..\src\TrueforceForAll.Plugin\Languages\en.json -Out baseline\xaml-2026-09-24.csv`
  (overwrite it; a second CSV owning the same file makes the tests throw).
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
