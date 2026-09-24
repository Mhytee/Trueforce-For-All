# Telemetry runbook

How to read `public.telemetry` after a release without fooling yourself.

Every query below is runnable as written from the repo root:

```
supabase db query --linked "SELECT ..."
```

Read only. Several maintenance functions in this schema delete rows; never
call one from here.

All numbers quoted as "measured" were taken on 2026-09-24, when the whole
dataset was 21 rows over 18 installs, every one of them on plugin_version
0.4.0. Re-measure rather than trusting the figures.

---

## 1. Before you read anything after a release: the defaults merge wave

**The first release after 0.4.0 will make nearly every install emit a settings
row on the same day, and none of it is user behavior.** Read as tuning
activity it is a total fabrication. This is the single biggest way to misread
this data, so it goes first.

### Why it happens

`ModeBRecipeFields` (TrueforcePlugin.cs, around line 11345) lists 44 fields:
30 `ModeB*`, 12 `SpringMode*`, plus `FfbConditionInertiaGain` and
`FfbConditionPeriodicGain`. `MergeWheelDefaults` rewrites every one of those
still holding a shipped constant, and it runs on version upgrade as well as on
wheel swap. `ModeBDefaultsGeneration` is currently 8; commit 9569982 says of
the two condition gains that they "join the defaults merge at generation 8 and
move an install that never deliberately tuned them".

`MaybeSendUsagePing` only sends the settings blob when its SHA-256 has changed
since the last accepted send. So on an ordinary day almost nobody sends a blob.
On upgrade day the merge moves fields on most installs at once, every hash
changes at once, and the settings column lights up across the whole fleet in
a few hours.

Every live row today is plugin_version 0.4.0, which means the merge has never
yet fired as an upgrade wave inside this dataset. The first time you see it,
it will look exactly like a fleet-wide burst of tuning.

### The attribution method

Four facts make the wave separable:

1. `public.telemetry` is a per-install **change log**, not a daily snapshot.
   `wheel` and `plugin_version` are real columns, but `settings` is NULL on a
   day the blob hash did not move. Confirm before every analysis session:

   ```sql
   select count(*) as rows_total,
          count(settings) as rows_with_settings,
          count(distinct anon_id) as installs,
          min(day) as first_day, max(day) as last_day
     from public.telemetry;
   ```

   Measured: 21 rows, 19 with settings, 18 installs. That ratio is high only
   because the dataset is days old; once installs settle it drops, and a row
   with a NULL `settings` means "this install was active and nothing changed",
   not "this install reported nothing".

2. `ModeBDefaultsGeneration` is a `private const` on TrueforcePlugin
   (TrueforcePlugin.cs:11237), not a settings property at all, so
   `BuildUsageSettingsSnapshot`, which walks `TrueforceSettings`, cannot see
   it and it is **not** in the blob. The latch that does carry the generation,
   `Settings.WheelDefaultsApplied` (`"G923#8"`, written at
   TrueforcePlugin.cs:5171), is a string outside `UsageSnapshotStringAllow`
   and is dropped too. Verify both:

   ```sql
   select bool_or(settings ? 'ModeBDefaultsGeneration') as has_generation,
          bool_or(settings ? 'WheelDefaultsApplied')    as has_wheel_latch
     from public.telemetry where settings is not null;
   ```

   Both return false. So `plugin_version` is the only way to know which generation
   a row's build shipped. Keep a note of which version carried which
   generation; nothing in the data will tell you later.

3. The merge is **deterministic** given (wheel, old value, generation). Two
   installs on the same wheel holding the same shipped constant get the same
   new value at the same moment. So an identical field-to-value transition
   arriving on many installs of one wheel on one day is the merge signature.

4. A transition **no other install shares** is a candidate user edit. Not a
   certainty: two people can coincidentally land on the same value, and a
   single remaining user of a rare wheel produces a singleton that is still a
   merge. Treat count as evidence, not proof, and sanity check the value
   against `ApplyWheelDefaults`.

### The version crossover diff

For each install, compare the last settings blob it sent on the old version
against the first one it sent on the new version, then group the field
transitions. Substitute the two version strings.

```sql
with old_ver as (
  select distinct on (anon_id) anon_id, wheel, settings
    from public.telemetry
   where plugin_version = '0.4.0' and settings is not null
   order by anon_id, day desc, updated_at desc
), new_ver as (
  select distinct on (anon_id) anon_id, wheel, settings
    from public.telemetry
   where plugin_version = '0.5.0' and settings is not null
   order by anon_id, day asc, updated_at asc
)
select coalesce(n.wheel, '(unreported)') as wheel,
       ov.key   as field,
       ov.value as old_val,
       nv.value as new_val,
       count(*) as installs
  from old_ver o
  join new_ver n using (anon_id)
  cross join lateral jsonb_each(o.settings) ov
  join lateral jsonb_each(n.settings) nv on nv.key = ov.key
 where ov.value is distinct from nv.value
 group by 1, 2, 3, 4
 order by installs desc, field;
```

Read it like this:

- **High `installs` on one wheel, and the `new_val` matches what that wheel's
  entry in `ApplyWheelDefaults` now ships**: that is the merge. Discard it.
- **`installs` = 1**: candidate user edit. Keep it.
- **Same field, many different `new_val` rows**: real tuning spread, because
  the merge only ever produces one target value per (wheel, field).

To limit the diff to the 44 fields the merge actually owns, join against this
list. It is the current contents of `ModeBRecipeFields`; re-read the array in
TrueforcePlugin.cs before relying on it, because fields join at each
generation bump.

```sql
with recipe(field) as (values
 ('ModeBSatGain'),('ModeBRiseGamma'),('ModeBPeakUtil'),('ModeBDropFloor'),
 ('ModeBEmaMs'),('ModeBSign'),('ModeBDamper'),('ModeBCenter'),
 ('ModeBLatGain'),('ModeBDirSoft'),('ModeBLockupRecoverMs'),('ModeBLockupPoint'),
 ('ModeBMinForce'),('ModeBCompressor'),('ModeBSuspensionLoad'),('ModeBEarlyTorquePeak'),
 ('ModeBRoadKick'),('ModeBRoadKickGain'),('ModeBReversalDamp'),('ModeBReversalDampGain'),
 ('ModeBPhaseLead'),('ModeBPhaseLeadMs'),('ModeBCenterPd'),('ModeBCenterLeadMs'),
 ('ModeBGripAutoCal'),('ModeBFrictionCircle'),('ModeBLongitudinalGripLearn'),('ModeBGripTrim'),
 ('ModeBLateralDemand'),('ModeBAutoStrength'),
 ('SpringModeStrength'),('SpringModeMinForce'),('SpringModeCenterGain'),('SpringModeCenterFirmness'),
 ('SpringModeSpeedEffect'),('SpringModeTerrainEnabled'),('SpringModeTerrainGain'),
 ('SpringModeDragEnabled'),('SpringModeDragGain'),('SpringModeDragStrainFraction'),
 ('SpringModeChassisWeightEnabled'),('SpringModeChassisWeightGain'),
 ('FfbConditionInertiaGain'),('FfbConditionPeriodicGain'))
select count(*) as recipe_fields,
       count(*) filter (where exists (
         select 1 from public.telemetry t where t.settings ? r.field)) as present_in_blob
  from recipe r;
```

Measured: 44 and 44. All 44 recipe fields ride the blob, so all 44 are
exposed to the wave.

### Float comparison: use a tolerance, and a tight one

`MergeWheelDefaults` compares **bit-exact** (`Equals(cur, tgt)`, documented in
the source as deliberate). A value that drifted off a shipped constant by any
amount is therefore never eligible for the merge, however close it looks.

That gives two different comparisons for two different questions:

- **"Will the merge move this install?"** Exact equality. Anything else
  misrepresents the code.
- **"Is this install sitting on the factory value?"** Tolerance, because the
  blob carries `float` (single precision) values printed through a JSON round
  trip.

Use a **relative** tolerance, `abs(v - seed) <= 1e-6 * greatest(1.0, abs(seed))`,
not a flat 1e-6. The recipe is not all small numbers: it spans 0.05 up to 40,
because four fields are milliseconds (`ModeBEmaMs`, `ModeBLockupRecoverMs`,
`ModeBPhaseLeadMs` and `ModeBCenterLeadMs` all ship at 40). Single precision
carries about 7 decimal digits, so a single ulp at 40 is already about 3.8e-6,
larger than a flat 1e-6 window; an at-seed 40 could fall outside it. Scaling
by the seed keeps the window at roughly a decade of margin over round-trip
noise everywhere in the list. Every deliberate control in the recipe moves in
far larger steps: the `ModeBDamper` slider nudges by 0.02.

(`MasterGain` is not a recipe field. TrueforcePlugin.cs:11340 says so
explicitly: the wheel table seeds it on a fresh install only and the merge
never touches it, so do not reason about the wave from it.)

Do **not** reach for something loose like 1e-3. Live data on 2026-09-24 held
`MasterGain` = 0.9995428 on an RS50. A 1e-3 tolerance reports that install as
"at the factory 1.0" while the merge, comparing bit-exact, treats it as tuned.
You would get the census and the merge prediction wrong in opposite
directions at the same time.

### The precaution

Write the crossover query, with both version strings filled in, **before** you
publish the release. Once the wave has landed you will be staring at a chart
that looks like a tuning trend, and the temptation to read it that way is
exactly what this section exists to defeat.

---

## 2. Traps

### 2.1 Per-wheel seeded defaults: never average across wheels

`ApplyWheelDefaults` (TrueforcePlugin.cs, around line 11153) gives each wheel
a different starting point. As of 2026-09-24:

| Field | G PRO | RS50 | G923 |
| --- | --- | --- | --- |
| `MasterGain` | 1.0 | 1.0 | 1.5 |
| `ModeBSatGain` | 0.50 | 0.60 | 1.00 |
| `ModeBDamper` | 0.07 | 0.07 | 0.09 |

**Read the current seeds out of `ApplyWheelDefaults` rather than trusting this
table.** It is a snapshot of a function that changes every generation, and a
G PRO value here is really the class initializer in TrueforceSettings.cs
(the `switch` has no G PRO case, so unrecognized wheels fall through to the
coded defaults).

A fleet mean of `ModeBSatGain` measures nothing but your wheel mix. Report
at-seed / raised / lowered per wheel instead, and count installs rather than
rows: `telemetry` is a change log, so an install that sent the same value on
two days is counted twice unless you take its latest blob first.

```sql
with latest as (
  select distinct on (anon_id) anon_id, wheel, settings
    from public.telemetry
   where settings is not null
   order by anon_id, day desc, updated_at desc
)
select t.wheel,
       count(*) filter (where abs((t.settings->>'ModeBSatGain')::numeric - s.seed)
                              <= 1e-6 * greatest(1.0, abs(s.seed)))      as at_seed,
       count(*) filter (where (t.settings->>'ModeBSatGain')::numeric
                              > s.seed + 1e-6 * greatest(1.0, abs(s.seed))) as raised,
       count(*) filter (where (t.settings->>'ModeBSatGain')::numeric
                              < s.seed - 1e-6 * greatest(1.0, abs(s.seed))) as lowered,
       count(distinct t.anon_id) as installs
  from latest t
  join (values ('G PRO', 0.50), ('RS50', 0.60), ('G923', 1.00)) as s(wheel, seed)
    on s.wheel = t.wheel
 where t.settings ? 'ModeBSatGain'
 group by 1 order by 1;
```

Measured: G PRO 5 at seed / 1 raised, G923 6 at seed, RS50 3 at seed /
2 lowered. That reads as "strength is close to right on all three wheels",
which is a claim worth making. "Mean strength is 0.70" is not.

Counted as rows rather than installs, the same data says RS50 4 at seed,
because one install sent `ModeBSatGain` 0.6 on two different days. That is the
change-log trap in miniature.

`wheel` can be NULL (one install on 2026-09-24). The join above silently drops
those rows; `coalesce(wheel, '(unreported)')` them back in whenever a total
matters.

### 2.2 `PluginEnabled` is effective state, not intent

`PluginEnabled` is written as `effectiveMode == TrueforceMasterMode.Normal`,
and the effective mode is the **stored** choice demoted twice: by the per-game
memory (to Off) and by this session's native-stream watch (to Lightsync only).

So a row with `PluginEnabled` false and `MasterMode` Normal is not a user who
switched the plugin off. It is an install that stepped aside on a title
streaming its own Trueforce, which is the plugin working correctly.

Both keys ride the blob. Always read them together:

```sql
with latest as (
  select distinct on (anon_id) anon_id, settings
    from public.telemetry
   where settings is not null
   order by anon_id, day desc, updated_at desc
)
select settings->>'MasterMode' as master_mode,
       settings->>'PluginEnabled' as plugin_enabled,
       count(distinct anon_id) as installs
  from latest
 group by 1, 2 order by 3 desc;
```

The `latest` wrapper is the point of the query, not decoration. Straight off
`public.telemetry` the same buckets sum to more than the install count, because
an install that flipped `PluginEnabled` between two days appears in two of
them.

`MasterMode` in the blob is the **stored** global stance, not the effective
mode. `ApplyEffectiveMode` (TrueforcePlugin.cs:1585) keeps the effective value
in `_effectiveMode` and mirrors only `PluginEnabled`; it never writes
`Settings.MasterMode`. A mode chosen while a game is running goes to
`GameModes` and leaves the global alone (TrueforcePlugin.cs:1967). So the
stored global choice is in the blob, and the per-game choice is the part that
is missing: `GameModes`, `ModeBGameEnabled` and `GameEnabled` are dictionaries,
and `BuildUsageSettingsSnapshot` drops every non-scalar. Confirm with:

```sql
select bool_or(settings ? 'GameModes')       as has_gamemodes,
       bool_or(settings ? 'ModeBGameEnabled') as has_modeb_game,
       bool_or(settings ? 'GameEnabled')      as has_game_enabled
  from public.telemetry where settings is not null;
```

All three return false. Per-game intent is the one genuine blind spot in this
dataset today; do not infer it from `MasterMode`.

(If you go to the source to check this, note that the plugin also exposes a
`MasterMode` property, at TrueforcePlugin.cs:1476, which returns the effective
mode. The blob's key is the settings one, because
`BuildUsageSettingsSnapshot` walks `typeof(TrueforceSettings)`. The plugin
property for the stored value is `StoredMasterMode`.)

**Closing from the next release.** `BuildChangedGamePresetsJson` now injects
three keys into each per-game preset body: `_modeB` (bool), `_mode` (integer
`TrueforceMasterMode`) and `_enabled` (bool). Each is present only when the
install actually made that choice for that game, so absence still reads as
"never chose". Two consequences worth holding on to:

- They are the only keys in `telemetry_game_settings.preset` that are user
  intent rather than preset content, so section 2.3 does not apply to them:
  there is no preset value to attribute them against.
- `v_game_tuning` will emit them as the paths `_mode`, `_modeB` and
  `_enabled`, and `v_game_disagreement` will list them among the settings
  installs disagree on. They are not settings.

### 2.3 Preset-scoped fields are not user intent

The 16 scalars on `GameSettingsSnapshot` (`FfbScale`, `FfbInvertSign`,
`FfbSmoothTimeConstantMs`, the four `FfbSpike*`, `FfbPeakSoftLimitLsb`, the
five `Duck*`, and the three `StationarySpring*`) are **replaced wholesale**
when a game's preset is applied. Their top-level mirrors in the global blob
move with them. A change in one of those keys is just as likely a preset
switch as a slider drag.

Attribute by joining the global blob to the per-game preset body:

```sql
with latest as (
  select distinct on (anon_id) anon_id, settings
    from public.telemetry
   where settings is not null
   order by anon_id, day desc, updated_at desc
)
select gs.game,
       t.settings->>'FfbScale' as global_value,
       gs.preset->>'FfbScale'  as preset_value,
       (t.settings->>'FfbScale') is distinct from (gs.preset->>'FfbScale') as user_edited
  from latest t
  join public.telemetry_game_settings gs on gs.anon_id = t.anon_id
 order by gs.game;
```

Join on `anon_id` alone, and take the game from `telemetry_game_settings`.
Adding `and gs.game = t.game` looks tighter and is wrong: as section 3 notes,
`telemetry.game` is only the title live at ping time, so that condition throws
away every other game body the install has sent while still looking like it
answered for all of them. The `latest` wrapper is there for the other half of
the same problem: without it each game body is multiplied by every settings row
the install ever sent.

If the two agree, the global value is the preset talking. If they differ, that
install has moved the control away from what its preset says, which is a real
edit. Measured on 2026-09-24: eight of ten rows agreed exactly; two installs
sat at 0.8009 against 0.8008723 in the preset, which is a live edit on each.

`telemetry_game_settings` carries at most 10 game bodies per ping and at most
50 games per install, and the server only accepts a new game once the install
is under that cap, so absence of a row is not absence of play.

### 2.4 The rollups never contain today

`telemetry_rollup()` runs as cron job 22 on `25 4 * * *`, so **04:25 UTC
daily**. Its `telemetry_daily` insert is filtered `where day < current_date`,
which means **today is never in the rollup by design**, and yesterday is only
there after the batch has run.

Reading rollups alone on a release day undercounts badly. It once showed 1
install when there were 17. Measured on 2026-09-24: `telemetry_daily` held
2026-09-21 through 2026-09-23 with 1, 1 and 9 active; raw `telemetry` for
2026-09-24 held 10 distinct installs that the rollup knew nothing about.

For a live number, union the rollup with raw telemetry past its high-water
mark:

```sql
select day, active, source from (
  select d.day, d.active, 'rollup'::text as source
    from public.telemetry_daily d
   where d.day >= current_date - 14
  union all
  select t.day, count(distinct t.anon_id)::int, 'raw (live)'
    from public.telemetry t
   where t.day >= greatest(
           current_date - 14,
           coalesce((select max(day) from public.telemetry_daily), current_date - 14) + 1)
   group by t.day
) u order by day;
```

Also worth knowing: the rollup deletes `telemetry` and `telemetry_game_days`
rows older than 90 days, so the raw change log is a rolling window. Anything
you want to compare across a longer span has to be pulled out before it ages
off.

### 2.5 The 8192 byte settings blob cap, and how to measure it wrongly

`telemetry_ping` tests `pg_column_size(p_settings) > 8192` and, over the line,
sets `p_settings := null`. It then inserts the row and **returns ok anyway**,
with `settings` false in the receipt. Nothing fails and nothing retries; the
settings half of usage statistics simply stops arriving. Because the blob is
every scalar in `BackupProjection.Portable`, this is a **global** cliff, not a
per-install one: when it is crossed, it is crossed by every install on that
build at once. The plugin now logs a Warn when it sees `settings` false after
having sent a blob, which is the only alarm that exists.

Measuring it from stored rows has its own trap. Three different numbers:

```sql
select max(pg_column_size(settings))                       as stored_compressed,
       max(pg_column_size(settings || '{}'::jsonb))        as uncompressed_datum,
       max(octet_length(settings::text))                   as json_text
  from public.telemetry where settings is not null;
```

`settings || '{}'::jsonb` is the detoast trick, and the concatenation is
deliberate. `jsonb_strip_nulls` detoasts too, but it also drops null-valued
keys, which the server's `pg_column_size(p_settings)` does not; there are zero
of those today, so the two agree, and the day one appears the strip version
would quietly undercount the exact thing this measurement exists to catch.

Measured 2026-09-24: 2490, 4932, 4705.

**`pg_column_size(settings)` on the stored column is wrong.** It reports 2490
because TOAST compressed the value on disk, which would tell you that you have
70 percent of the cap free when you actually have 40 percent. The server's
check runs on the uncompressed inbound jsonb, so the middle number is the one
that matters: **4932 bytes over 161 keys, about 60 percent of the limit.**

Headroom query, to run every release:

```sql
select max(pg_column_size(settings || '{}'::jsonb)) as max_bytes,
       round(100.0 * max(pg_column_size(settings || '{}'::jsonb)) / 8192, 1) as pct_of_cap,
       max((select count(*) from jsonb_object_keys(settings))) as max_keys,
       count(*) filter (where settings is null) as rows_without_settings,
       count(*) as rows_total
  from public.telemetry where day >= current_date - 7;
```

Treat 75 percent as the line at which the blob needs trimming rather than
another field. It grows every time a setting is classified Portable for
backup, which happens for reasons that have nothing to do with telemetry.

---

## 3. Useful queries

### Fleet shape right now

```sql
select coalesce(wheel, '(unreported)') as wheel,
       plugin_version,
       count(distinct anon_id) as installs
  from public.telemetry
 group by 1, 2 order by 3 desc;
```

### Which games are actually played

```sql
select game, count(distinct anon_id) as installs, count(*) as game_days
  from public.telemetry_game_days
 where day >= current_date - 30
 group by 1 order by 2 desc, 3 desc;
```

`telemetry_game_days` is the per-game activity log the ping drains; the `game`
column on `public.telemetry` is only the title live at ping time, so it is a
much weaker signal. User-added titles arrive as the literal string `Custom`,
never with their label.

### Distribution of any single blob field, per wheel

Substitute the field name in both places.

```sql
select coalesce(wheel, '(unreported)') as wheel,
       settings->>'ModeBDamper' as value,
       count(distinct anon_id) as installs
  from public.telemetry
 where settings ? 'ModeBDamper'
 group by 1, 2 order by 1, 3 desc;
```

Grouping by the raw text is deliberate: it keeps 0.07 and 0.0867722 apart,
which is what you want when hunting for the merge signature. Cast to numeric
only when you are doing the at-seed census in 2.1.

### How each game gets tuned

Migration 0132 adds two owner-only views over `telemetry_game_settings`.
`v_game_tuning` flattens each preset body into one row per settings leaf, with
a dotted path (`FfbScale`, `GearShift.Waveform`). `v_game_disagreement` rolls
that up into the paths installs do not agree on.

Migration 0132 was applied on 2026-09-24, so this query runs as written. Both
views are `security_invoker` and granted to `service_role` only, so they are
invisible to the anon key that ships in the plugin.

```sql
select * from public.v_game_disagreement
 where game = 'FH6'
 order by installs desc, distinct_vals desc;
```

`distinct_vals` of 2 across 40 installs and 2 across 2 installs mean opposite
things, which is why `installs` is in the view. Both views are
`security_invoker` over a deny-all table; never grant either to `anon` or
`authenticated`.

Everything in section 2.3 still applies: a value here can be the preset
talking rather than the user. The exceptions are the three `_mode`, `_modeB`
and `_enabled` paths described at the end of 2.2, which are pure user intent
and are not settings at all.

### Latest blob per install

`telemetry` only holds a blob on days it changed, so "the current state of
install X" needs the most recent non-null:

```sql
select distinct on (anon_id) anon_id, day, plugin_version, wheel, settings
  from public.telemetry
 where settings is not null
 order by anon_id, day desc, updated_at desc;
```

`telemetry_rollup()` maintains exactly this in
`telemetry_devices.last_settings` / `last_settings_day`, which is cheaper to
query and up to one batch stale. Use the rollup for trends and the query above
whenever today matters.

### How many installs have opted out

```sql
select settings->>'ShareUsageStats' as sharing, count(distinct anon_id)
  from public.telemetry where settings is not null group by 1;
```

This can only ever count people who are still sending, so it measures opt-outs
that happened after at least one ping. It is not an opt-out rate.

---

## 4. Standing reminders

- `anon_id` is minted per install and is classified MachineLocal, so it never
  travels in a backup. A second PC counts as a second install. There is no
  "users" number in this dataset, only installs.
- `AnalyticsAnonId` and `CarFactsAnonId` are deliberately separate ids so the
  two anonymous datasets cannot be cross-linked. Do not build anything that
  joins them.
- Nothing in the blob is free text. `BuildUsageSettingsSnapshot` emits only
  Portable scalars, enums by name, and three allow-listed selector strings
  (`DashTheme`, `DashIdleStyle`, `CspBridgeFfbField`). If you find a string
  value that looks like something a person typed, that is a bug worth chasing
  immediately.
- A ping is once per UTC day per install, gated on `ShareUsageStats` and
  skipped entirely when the stored master mode is Off. "Active installs" means
  "installs that ran SimHub with the plugin not switched off", which is close
  to but not the same as "people who drove".
