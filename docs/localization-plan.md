# Localization plan

Status: planned, not started. Owner decisions are listed at the end.
Written 2026-09-24 from a two-workflow audit of the tree (five readers,
two competing designs, three adversarial reviews). Counts below were
measured on `dev` that day; re-measure rather than trusting them.

## What we are localizing

| Surface | Count |
|---|---|
| XAML user-visible strings, four files | 1,273 instances, 1,030 distinct, 12,233 words |
| of which ToolTips | 323 instances carrying 6,719 words (55 percent) |
| C# UI strings (code-behind, 29 window classes, dialogs, status helpers) | about 750 |
| Guides (`Guides\*.md`, embedded) | 28 files, 7,889 words |
| Effect display names | 15 Engine `Name` overrides, duplicated in five plugin-side tables |
| EffectChangelog entries | 94 (not translated, see scope) |
| Dash labels (`make-tf4all-dash.ps1`) | about 92 (not translated, see scope) |

XAML holds zero `{0}` placeholders. All composition happens in C#
interpolation (about 82 sites) plus roughly 14 hand-rolled English plurals
(`entr{y|ies}`, `item{s}`, `device(s)`).

## Three facts that shape the design

**1. SimHub pins the process to en-US.** SimHub's startup sets
`CurrentCulture`, `CurrentUICulture` and both `DefaultThread*` properties to
the literal "en-US" before any plugin loads, then hands the user's chosen
language only to its own resx provider (verified in the IL of
`SimHubWPF.exe`). Consequences: `CultureInfo.CurrentUICulture` reads "en"
for every user; `CommunityNameLocaleSig()` has always returned `lang=en`;
every culture-less `ToString("F2")` in the plugin renders en-US today. Never
read `CurrentUICulture` to learn about the user, and never set thread
culture (SimHub's own code relies on the pin).

**2. SimHub ships a plugin localization SDK, and it cannot do Spanish.**
`WoteverLocalization.dll` provides `{SLoc Key, 'Default'}` for XAML,
`Loc.GetValue(key, fallback)` for C#, loose `<Plugin>.<culture>.resx` files
in `<SimHub>\Languages`, listing in SimHub's translation editor, and
`.diff.resx` user overrides (see
`PluginSdk\User.PluginSdkDemo\GettingStarted.txt`). Verified: a plugin resx
merges into SimHub's Language objects and user diffs win. Also verified: a
Language exists only when `SimHub.<culture>.resx` exists. SimHub ships
de-DE, fr-FR, it, ko-KR, ru-RU, zh-Hans-CN and no Spanish, so Spanish is
unreachable through SimHub's system until SimHub ships one. The DLL is also
brand new (built 2026-09-17); a hard `{SLoc}` reference in XAML would fail
the whole settings panel on any earlier SimHub. We therefore own the
runtime and keep the data SimHub-shaped, so an export to
`<SimHub>\Languages` for the six SimHub cultures stays a script rather than
a redesign.

**3. Culture leaks: zero numeric ones.** Every float and double parse (28
sites) passes an explicit culture; every persisted or transmitted number
goes through Newtonsoft or `InvariantCulture`; no game-read file receives a
formatted number. The whole data-side fix list is three
`DateTime.TryParse(s, null, ...)` calls (`PresetSharingClient` `TryParseDate`
and `created_at`, `SettingsControl` account `created_at`), which should pass
`InvariantCulture` like their ten siblings. Localized number display (0,75)
is deferred; if wanted later, format explicitly per call through a helper.

## Design

Plugin-owned runtime, SimHub-shaped data.

**Packaging.** English and every shipped translation are embedded in
`User.TrueforceForAll.dll`, the same pattern as the Guides. The three-DLL
deploy contract does not change and a three-DLL copy with no language files
renders full translations. At Init the plugin writes reference copies to
`<SimHub>\PluginsData\Common\TrueforceForAll-Languages\shipped\<culture>.json`
(rewritten when the embedded hash differs) plus a `_README.txt`. A user or
translator drops `<culture>.json` in the folder root containing only the
keys they changed; it wins per key. Nothing ever goes under
`TrueforceForAll\factory`, which the installer wipes.

**Source of truth.** `src\TrueforceForAll.Plugin\Languages\en.json`: a flat
key-to-string object plus `_meta`. Translations as `es.json` and later
`de-DE.json`, `fr-FR.json`, `it.json`, `ko-KR.json`, `ru-RU.json`,
`zh-Hans-CN.json`, using SimHub's exact tags so Follow SimHub matches by
name. Spanish ships as neutral `es`; es-MX falls back to es.

**XAML.** `{loc:T Key}`, a plugin-owned markup extension returning a
`Binding` to a singleton indexer, so a language change re-renders live. The
English text leaves the attribute; only the key stays. Inside Setters and
DataTemplates the Binding object is returned directly (4 Setters, 4
templates today).

**C#.** `Loc.T(key)`; `Loc.F(key, args)` (string.Format with `{0}`
placeholders, catches `FormatException` and falls back to English with one
Warn); `Loc.N(key, n, args)` picking `.one`/`.other`, later `.few`/`.many`.
C# uses a generated `LocKeys` constants class so a typo is a compile error;
XAML keys are checked by the test suite. Every C# sink that assigns a Loc
result is re-applied by `RefreshFromPlugin()` on `LanguageChanged`, and
`PresetManagerControl` re-captures its DataGrid base headers.

**Language resolution**, once at Init and again on a setting or file change:

1. `TrueforceSettings.UiLanguage`, a new global setting with one
   `BackupProjection` line; "" means Follow SimHub. Settings tab picker
   listing Follow SimHub plus every discovered language by its `_meta.name`,
   with "Open language folder" and "Fix a translation" beside it.
2. SimHub's `"Culture"` key in `<SimHub>\PluginsData\GlobalSimhubSettings.json`,
   read with Newtonsoft, read only, never round-tripped (the installer
   already treats that file with a BOM-preserving regex). No reflection into
   `WoteverLocalization`.
3. When that key is "" (system default, the common case): the Windows
   display language via `GetUserPreferredUILanguages`, a Win32 call the pin
   cannot touch. This is what puts Spanish in front of Spanish users without
   them finding a setting; the picker is the opt-out. Owner decision 3.
4. Per key: root override, shipped, parent culture, embedded English, then
   `[key]` with one Warn.

**Effect names.** `TelemetryEffect.Name` stays English; it is log identity
for `[TF4ALL] TestEffect`. Add `Id` returning the `KnownEffectIds` token.
Display goes through `Effect_<Id>_Name` in the plugin, collapsing the five
duplicated label tables (`EffectTagLabels`, `CommunitySectionPickerWindow`,
`PresetManagerControl` `AppendEffectLine`/`AppendOverrideSection`,
`SettingsControl.EffectLabel`, the XAML expander headers) onto one lookup
with a lowercase community-tag to Id map. Wire identifiers never change:
`KnownEffectIds`/`SectionKind` (persisted), lowercase `effect_tags`
(uploads), dash row ids. `EngineLayout` display names move to
`EngineLayout_<EnumName>` keys; the Engine keeps its English version for
`HeadlessRig`.

**Guides.** `Guides\es\*.md` embedded with LogicalName
`TrueforceForAll.Plugin.Guides.es.%(Filename).md`; the existing wildcard is
single-level, so one csproj item per language. `GuideText.LoadRaw` tries
culture, parent, neutral. Translators keep `](guide:key)` targets and
`{{guide:...|panel:...}}` tokens verbatim; a parity test checks token
counts, link targets and heading counts.

**Diagnostics.** One startup Info line per language (keys covered, missing,
unknown override keys, active source), Warn above 5 percent missing. Access
codes: `LOCREPORT` writes a missing-keys file for translators; `LOCMARK`
brackets every string that fell back to English; `PSEUDO` walks the loaded
tree, lengthens each string 30 percent with accent swaps, measures every
TextBlock with `FormattedText` against `ActualWidth` and logs
`[TF4ALL] pseudo-clip` with x:Name; `PSEUDOCJK` fills with Hangul and Han
to exercise font fallback and line height.

## Identifier couplings to fix before any extraction

These are display strings the code reads back as state. Translating any of
them silently breaks a setting.

- `ImplementThudWaveformCombo`: Content is matched as a `Waveform` enum
  name (`(it.Content as string) == wname` and `Enum.TryParse<Waveform>`).
  Switch to the existing `SelectWaveform`/`WaveformOf` index helpers;
  `OscillatorSource` orders Sine, Square, Saw, Triangle, Noise exactly like
  the items.
- `FxTestEffectBox`: `FxSelectedKind()` returns Content (DAMPER, SPRING,
  ...), which reaches the DirectInput kind switch and is the persisted key in
  `FxKindGainNow`/`SetFxKindGain`. Give the items Tag values; read Tag.
- `PresetShareWindow`: a row is found by `label.Text == "Sharing as"`. Tag
  the Grid in `MakeFactLine`; match on Tag.
- `IRacingAutoMaxForceStatus.Text.StartsWith("Set to ")`: replace with a
  bool set where the confirmation text is written.
- Five input TextBoxes carry letter-bearing default Text that code parses
  back (`LedSweepText` "2.5 s", `FxTestPeriodText` "250 ms",
  `FxTuneLpfText` "200 Hz", `PerfTfRingText` "8 (2ms)", `PerfAudioRingText`
  "16 (4ms)"). Never extract Text from a TextBox or an editable ComboBox.
- All 53 `Tag` values are identifiers (effect ids, modes, intervals). Never
  touched.
- `TelemetryFfbTab.Header` is set in C# ("FFB" / "FFB & LED"), declared in
  XAML ("Telemetry FFB") and read back by `TelemetryFfbTabLabel()` in
  `SettingsControl.Guides.cs` for "Open the ... tab". All three move in one
  commit and the sentence becomes a format key.

Permanent test: every read (not write) of `.Text`, `.Content` or `.Header`
on an x:Name whose element is not a TextBox is a failure unless allowlisted.

## Phases

Anchoring rule for every phase: a replacement matches on x:Name plus
attribute plus exact value (XAML) or enclosing method plus sink plus value
(C#), asserts exactly one hit, and preserves each file's BOM and line
endings byte for byte (`SettingsControl.xaml` is LF with BOM,
`PresetManagerControl.xaml` LF without, `CustomEngineEditor.xaml` and
`MotdStrip.xaml` CRLF). Line numbers are audit columns only. The `.tmpfix`
leftovers from 2026-08-23, a scripted insert that dropped about 2,470 lines
from `SettingsControl.xaml.cs`, are the precedent this rule exists for.

### Phase 0: pre-work (hours; ships alone)

The identifier fixes above. The three `DateTime.TryParse` culture fixes. A
comma-decimal guard before `JObject.Parse` in
`FarmingSimulatorTelemetrySource` (the game's Lua runs outside SimHub's
pin; whether Giants' VM honors the C locale is unverified, so this is cheap
insurance). Move `ExecuteAccessCode` and the code catalog into
`SettingsControl.DevCodes.cs` so the later literal sweep can exclude
developer tooling by file (agent-audit S5). One Init log line printing the
thread UI culture and SimHub's Culture key.

Exit: build green; grep shows no Content-as-key read remains; Implement
thud waveform and FX kind gain round-trip through save and reload; the log
line shows en-US.

### Phase 1: runtime plus XAML, English only (2 to 4 days; ships alone)

The Loc runtime, about 350 lines. Rehearsal slice first, chosen to hit
every hazard class on about 15 percent of the surface: `MotdStrip.xaml`
(CRLF), `PresetManagerControl.xaml` and its code-behind (LF without BOM, 27
DataGrid headers with the sort-arrow capture, 4 DataTemplates, 13
interpolations, 6 plural sites, 30 dialog calls), the `SettingsControl.xaml`
resources block (the 4 Setters) and the Support tab. Then one commit per
`SHTabItem` (Controls, Account, Lightsync, Settings, Effects, TelemetryFfb,
then the pinned header region); the SHTabItem elements are the only
trustworthy banners in that file. The owner reviews the proposed key list
once before conversion; keys freeze at first shipped release. Shared keys
for repeated values are opt-in per value after a sense check: "Strength:"
appears four times with four meanings, "Save…" 19 times with one.

Tests in `Core.Tests` (net8, reads repo files): inventory round-trip against
a committed baseline CSV (same rows, same count, resolved values
byte-identical); a no-literal sweep as a denylist of identifier attributes
(x:Name, x:Key, Tag, GroupName, Property, TargetType, Style, event
handlers), flagging every other letter-bearing attribute value and text
node so `FriendlyName` and Hyperlink content become decisions instead of
blind spots; keys resolve with zero orphans; duplicate keys fail at test
time (runtime uses last-wins with a Warn); call-site format arity (`Loc.F`
argument count equals max `{n}` plus one in en.json, and a key with
placeholders is never assigned raw).

Exit: tests green; three DLLs deployed and the panel identical tab by tab
to pre-change screenshots; `[TF4ALL] Language en` line with no warnings;
panel construction delta under 100 ms; `PSEUDO` shows brackets on every
string and its clip log is captured as the Phase 3 worklist.

### Phase 2: C# strings, windows, picker (3 to 5 days; ships alone)

About 750 strings: dot-form and object-initializer assignments,
`new Run(...)`, the seven `Set*Status` helpers, 90 window titles, about 245
dialog call sites (title, body, okLabel, cancelLabel), the 29 window
classes, TrueforcePlugin's 53 diagnostic return strings and 11 status
fields, the 14 plural sites through `Loc.N`. The converter rewrites only
known UI sinks and never matches by value: 125 XAML literals also appear
verbatim in JSON keys and `case` labels. Cross-surface duplicates get one
key (the XAML `SlipEnabledCheck` Content and the C# refresh that reassigns
it). `LocKeys` constants. Effect display unification. The `UiLanguage`
setting, picker, `BackupProjection` line and `LanguageChanged` wiring.

Exit: C# literal sweep green with the allowlist confined to `DevCodes.cs`,
`TestCodesWindow.cs`, log lines and access-code output; the hot-swap check
run after `RefreshFromPlugin` has executed at least once, not on a cold
panel; `BackupSelfTest` passes with `UiLanguage` classified; the two
`TestEffect` log lines still print English.

### Phase 3: layout, pipeline, Spanish (3 to 4 days plus review time)

Layout from the `PSEUDO` log: `LabelText` gets
`TextTrimming=CharacterEllipsis` so clipping becomes visible instead of
silent; the 138 x 140 px and 21 x 150 px label columns and their 39 coupled
`Margin="140,..."` indents move to one shared width resource; inline
`Width` on the 41 buttons and the 10 airborne CheckBoxes becomes `MinWidth`
plus `Padding`; `EffectSaveButton` (60 px behind 18 "Save…" buttons) and
the 84 px header column for "Game preset"/"Car preset" widen. At 130
percent length, 19 of the 182 label rows are estimated to clip today.

The translation pipeline below, the first Spanish pass, the review gate, the
guides. `CommunityNameLocaleSig()` per owner decision 4.

Exit: `LocTranslationIntegrity` green for es; guide parity green; `PSEUDO`
and es walks log zero clips at SimHub's narrowest pane; a 30-minute
`LOCMARK` walk shows no fallbacks; the picker entry reads
"Español (beta, traducción automática)".

### Phase 4: per release, and more languages

`retranslate.ps1` computes new, stale (English text at translation time
differs from current) and removed keys per language; only new and stale go
to the model; human-fixed keys are re-sent with the previous human text and
flagged for a glance. `merge-user-fix.ps1` folds a user's root
`<culture>.json` into the shipped file and marks those keys human.
Languages are added in the order the telemetry language field suggests,
using SimHub's exact tags. CJK gets one glyph and line-height check on a
stock en-US Windows 11 VM before shipping.

## Translation pipeline

Where AI translation actually fails here, measured: an agent translated 28
real strings from `SettingsControl.xaml` into Spanish using only tab,
control type and nearest header as context. Long tooltips came out clean.
Of the short labels, about one in five was wrong or misleading on a visible
control: "Rate limiter" became a speed limiter, "Wheelspin-locked" read as
brake lock, "Implement thud" became the software verb, "Weight buildup"
read as body weight, "rumble strip" became a soundtrack. Four of those
would have been fixed by the HelpText that sits two lines below the label
in the XAML. So:

- **Context is mechanical, not hand-written.** The extractor emits per key
  the sibling x:Name stems (195 of 202 label TextBlocks have no x:Name; the
  named element is the slider beside them), the ToolTip on the label or
  slider, the HelpText block that follows the Grid, and the slider Min/Max.
  Translation is batched by container (Expander or named StackPanel), never
  by key name, so the model sees each label with its explanation in one
  request.
- **Cross-references are tracked.** Tooltips quote other labels ('Enable
  community features (online)', "your Strength setting") and guides bold
  them (**Lightsync only**). The extractor seeds a refs list by substring
  match; the translate script sends the already-translated target text with
  the referencing key; the integrity test asserts the target's translation
  appears as a substring.
- **Short keys report their sense.** For every key of three words or fewer
  the model returns the sense it assumed and a confidence. Low-confidence
  rows are the owner's review list, and the glossary grows from them rather
  than from intuition.
- **Glossary** in `docs/localization/glossary.md`. Never translate:
  Trueforce, Trueforce For All, TF4ALL, FFB, SimHub, G HUB, Logitech,
  Lightsync, USBPcap, CSP, HID and HID++, OLED, LED, DRS, ABS, Patreon,
  Discord, GitHub, game names, units, waveform names. Translate
  consistently, one term per concept, decided once: wheel is volante unless
  the note says tire; gain; the two senses of strength; damper; spring;
  slip; lockup; soft lock (tope de dirección); curb (piano); rev lights;
  redline; pit limiter; airborne ducking; spike reduction; preset; Mode B;
  per-car. SimHub's own FR and DE translations left redline, FFB, dash and
  plugin in English and are the reference for that judgment. Typography
  (¿ ¡, quote style, formal or informal address) decided once. No em-dash
  and no "--" in any language file.
- **English normalized first.** One term per concept in the source
  ("Traction loss (rear breaking loose / wheelspin)" and "Traction loss"
  name one effect). Runtime-overwritten placeholders ("Idle" on the status
  pill) are allowlisted rather than keyed.
- **Review gate before shipping, not after.** All 39 GitHub issues to date
  are functional; none concerns copy. The "users report oddities" loop does
  not exist yet, so the first pass is the final pass for months. Gate: 100
  percent of keys of three words or fewer, the status pill vocabulary
  (Ready, Active, Audio only, Waiting for telemetry, In menu / paused,
  Stream stopped, Wheel not detected), the eight tab headers, and a sample
  of long strings, read by one Spanish-speaking sim racer recruited from
  Discord beforehand. The picker entry is labeled as machine-translated
  beta. The report action is one click: `LOCMARK` plus a button that opens
  a prefilled issue or Discord message with key and current text.
- **One neutral Spanish, decided 2026-09-24.** Spain and Mexico differ, but
  the UI's divisive vocabulary is short: "car" appears in 167 UI strings,
  then spring (20), gear (20), curb (14), PC (12), tire (9), out of about
  1,425. Each resolves through one glossary entry, so the glossary picks the
  term understood on both sides of the Atlantic rather than a country:
  auto (not coche or carro), resorte (not muelle), neumático (not llanta,
  which means rim in Spain), marcha, piano (sim-racing jargon everywhere),
  PC. The reviewer may come from either region and settles what remains.
  The file stays `es`. If real users later want a regional variant, the
  retranslate script generates `es-MX` or `es-ES` by re-translating only
  the roughly 230 keys that contain a glossary-divisive term, and the
  parent-culture fallback covers the rest; nobody hand-edits 2,000 keys.

## Out of scope for the first pass

- OLED wheel-screen text. `WheelOledChannel.WriteField` maps every
  character outside 0x20 to 0x7F to a space; text is upper-cased; slots are
  10 characters. Non-Latin scripts cannot reach the wheel at all. Whether
  the firmware font holds any glyph above 0x7F is untested and would need a
  deliberate 0x80 to 0xFF write on the rig. The WPF-side `OledScreenModel`
  panel labels are in scope; FieldKeys and enum values never move.
- Dash labels baked into the generated djson. Labels already fed from
  plugin properties localize for free once those strings are keyed.
- `[TF4ALL]` log lines (about 1,000) and Engine effect `Name`.
- EffectChangelog entries. The what's-new modal prefers GitHub release
  notes, which stay English; only modal chrome and the five Group labels are
  keyed.
- Access-code responses and Mode B readouts (developer tooling).
- Server text: MOTD, release notes, community preset names and
  descriptions, car names.
- Localized number display (0,75). The 176 display sites in
  `SettingsControl.xaml.cs` would route through a formatting helper; every
  write path stays invariant. Decide separately.
- Export to `<SimHub>\Languages\TrueforceForAll.<culture>.resx` for SimHub's
  editor (six cultures, Spanish excluded, two sources of truth). A script
  later if users ask for it.
- Right-to-left scripts. No `FlowDirection` anywhere and 39 hardcoded left
  margins.

## Owner decisions

Decided 2026-09-24:

1. **Spanish variant: one neutral file.** Not es-ES or es-419; a neutral
   register with the glossary choosing the term understood in both Spain
   and Latin America (see the translation pipeline). A regional variant is
   generated later from the glossary diff only if users ask.
2. **Hot-swap.** Binding-based for XAML plus `RefreshFromPlugin` for C#
   sinks. SimHub's own restart semantics are the fallback if the per-string
   Binding cost surprises in the Phase 1 timing.
3. **Default when SimHub's Culture is "": follow the Windows display
   language.** A Spanish Windows gets Spanish TF4ALL inside an English
   SimHub; the picker is the opt-out, and the release notes say where it is.

4. **Per-language car names: retired.** The owner's view is that most
   people use cars' English names anyway, so one global consensus bucket
   is the intended behavior. That is also the only behavior that has ever
   existed: because of the en-US pin, `CommunityNameLocaleSig()` has always
   emitted `lang=en`. Retirement is therefore a no-op on the wire: the
   method returns the constant `"lang=en"` with an honest comment (a legacy
   bucket key, not a language), the three `CommunityClient.cs` comments that
   describe per-language keying are corrected, no backend row moves, and
   the fetch's exact-sig-else-top-by-support rule reduces to top-by-support.
   Never repoint it at the localized UI language. Privacy upside for the
   companion telemetry change: the tag is a constant, not user-derived, so
   `PRIVACY.md` needs no car-facts language bullet and the promise that
   usage reports "cannot be tied to your car-data submissions" stands
   without amendment.

## Companion change: telemetry language field

Implemented 2026-09-24 as `supabase/migrations/0134_telemetry_language.sql`
plus `UsageLanguage.cs` (commit 4e54879); the migration is applied to
production and smoke-tested; how to read it is `docs/telemetry-runbook.md`
section 2.6. Two nullable two-letter columns
on `public.telemetry` (`ui_lang` from `GetUserPreferredUILanguages`,
`fmt_lang` from the regional format's language subtag), an aggregate-only
reading view `v_install_language` with a minimum cohort of 5, a Wilson
interval and a rankable flag at 30 installs in the bucket and 150 covered. At today's
fleet (3,984 cumulative downloads across every release; 18 reporting
installs on 2026-09-24) it can say whether English dominates and which
single non-English language leads, nothing finer. Do not wait for it to
pick Spanish. One MOTD asking "would you use this in your language, and
would you translate it" answers the volunteer question faster, and SimHub's
own six translations exist because volunteers appeared. The rules in
`telemetry-runbook.md` apply to reading it.
