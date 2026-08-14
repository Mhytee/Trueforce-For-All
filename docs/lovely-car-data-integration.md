# Lovely car data integration (rev lights)

Status: DESIGN (2026-08-14). Not implemented. Owner has agreed on direction: consume for rev lights, contribute back later.

## Goal

Use the community-maintained [lovely-car-data](https://github.com/Lovely-Sim-Racing/lovely-car-data) repository as a per-car rev-light data source. In order of value:

1. Per-gear redlines and per-LED lighting thresholds, replacing our one-size linear ramp with each car's real, usually nonlinear light buildup.
2. A car-authentic light profile, layout plus colors, composed per car and written into a plugin-owned Lightsync custom slot at car change.

Plus a contribute-back path: our CarFacts consensus data (and Forza coverage, which they lack entirely) flows upstream as curated PRs.

## The source

Joint project of Lovely Sim Racing, ATSR and Gomez Sim Industries ("open and unified sim racing car data"). Plain JSON over raw GitHub, no API:

- `data/manifest.json` lists all cars grouped by game.
- `data/{simId}/{carId}.json` per car, fetched from the `main` branch.
- `carId` is the SimHub identifier; file names are normalized (lowercase, hyphens for spaces, special characters stripped).

Fields per car: `carName`, `carId`, `carClass`, `ledNumber`, `redlineBlinkInterval` (ms), `ledColor` (per-LED HTML/hex colors), `ledRpm` (per-gear arrays), optional `carSettings` (unused by us for now).

### ledRpm semantics (from the BMW M4 GT3 iRacing entry)

Each gear key (`R`, `N`, `1`..`n`) maps to an array where:

- index 0 is that gear's redline;
- the remaining entries are per-LED illumination thresholds in dash order;
- a 0 threshold is a dead slot (a physical gap in the dash bar), matching a fully transparent entry at the same index in `ledColor`.

Example: their M4 GT3 redlines at 7150 in 1st but 7250 in 6th, with the outer LEDs starting at 4800 in 1st vs 6000 in 6th. That per-gear spread is not derivable from telemetry and is the core value of the dataset.

Implementation note: the sample entry has 13 rpm entries and 13 colors against `ledNumber` 12. The index-0 redline accounts for the rpm arrays; whether `ledColor` index 0 is a blink color or the arrays are simply aligned 1:1 must be verified against their schema/pre-commit rules before coding.

### Coverage and data quality (repo clone measured 2026-08-14, 717 cars)

| Sim | Cars | Distinct datasets | Gear-varying redlines | Real colors |
| --- | --- | --- | --- | --- |
| Le Mans Ultimate | 339 | 32 | 23 | 339 |
| Automobilista 2 | 137 | 83 | 0 | 131 |
| iRacing | 80 | 69 | 29 | 68 |
| ACC | 53 | 43 | 0 | 52 |
| F1 2025 | 50 | 10 | 0 | 50 |
| F1 2024 | 35 | 11 | 0 | 35 |
| AC Evo | 20 | 18 | 0 | 20 |
| AC / RaceRoom / PMR | 3 | 3 | 0 | 3 |

Measured facts that shape the design:

- Every entry has a full per-gear ledRpm structure, but only 52 cars actually VARY redline by gear (54 vary thresholds), concentrated in iRacing and LMU. Everywhere else the same array is copied per gear. Per-gear handling still costs nothing and captures exactly the cars where it matters (iRacing GT3s and LMU hypercars), but it is a minority feature, not the bulk of the dataset.
- Threshold quality is real: only 17 of 717 entries are degenerate (2 or fewer distinct thresholds); the median buildup band spans ~15% of redline, a genuine nonlinear ramp our flat mapping cannot express.
- Colors are the broadest asset: 698 of 717 entries carry real multi-color data, so the dynamic-slot feature has near-total coverage.
- Duplication is livery-shaped, not rot: LMU's 339 files collapse to 32 distinct datasets (team variants of the same class car) and F1's ~85 to ~21. Correct and harmless for per-carId lookup; just do not quote file counts as car counts.
- 247 entries set a nonzero redlineBlinkInterval.

No Forza, effectively no AC. AMS2 and LMU are prime Trueforce For All titles (no native Trueforce), so the coverage fits our audience well. iRacing entries complement the session YAML: the YAML has single shift-light values, Lovely has per-gear tables where they exist.

### License

CC BY-NC-SA 4.0. Rules we hold ourselves to:

- Runtime consumption on the client only. NEVER import Lovely rows into our Supabase (ShareAlike would make our community dataset a derivative, and it would bypass the CarFacts trust model).
- Attribution: "Car data by Lovely Sim Racing" (with link) near the feature in the settings UI, plus a credit in the README.
- NonCommercial: every feature that consumes this data stays in the free tier, never behind supporter entitlements.

## Identity mapping

Our key is (game, carId); theirs is (simId, carId) with carId already being SimHub's. Work needed:

- A small static map from our game identifiers to their simIds (e.g. their `iracing`, `f12024`; exact strings from the manifest).
- Apply their file-name normalization to our carId for the URL; the manifest is the authority when normalization is ambiguous.
- No match means silent fallback to current behavior. Never guess across games.

## Consumption layer

A `LovelyCarDataStore` in the plugin, modeled on the community fact cache (offline-first, fetch-fail never wipes cache):

- On car change: serve from cache immediately if present; refresh in the background when older than the TTL (7 days, matching the fact cache).
- Cache location: `<SimHub>\PluginsData\Common\TrueforceForAll-Library\lovely-cache\{simId}\{carId}.json` plus a fetch-timestamp sidecar/index.
- Negative caching: a 404 (car not in the dataset) is cached too, with the same TTL, so absent cars do not refetch on every session.
- Network gating: its own global toggle (proposal: "Per-car light data from Lovely Sim Racing"). It is a third-party GitHub fetch, not our community backend, so it does not ride CommunityEnabled; it is still a networked feature and the toggle copy says so. Default: owner decides (default-off is the safe baseline).

New settings fields are global (Settings tab), machine-independent, so each gets a BackupProjection line. Any collection defaults empty (the settings loader appends).

## Feature 1: per-gear thresholds and redlines

The current RPM-to-level mapping is a linear ramp from the engage percent to redline, identical for every car. With Lovely data, for RPM r in gear g:

- lit = count of active (nonzero) thresholds in gear g's array that are <= r
- level = round(10 * lit / activeLedCount)

This reproduces the car's real buildup on our 0..10 level interface (GT3 cars crawl through the first LEDs and sprint through the last few). Details:

- Dead slots (0 thresholds) are excluded from both counts.
- Symmetric dashes light LEDs in pairs; the fraction handles that naturally, no dedup needed.
- Blink at r >= redline[g], using `redlineBlinkInterval` when nonzero. Whether 0 means "no blink" or "firmware default" needs checking against their docs.
- Gear source is existing telemetry. Unknown or missing gear key falls back to the highest defined forward gear's table.
- The user's engage-percent setting keeps governing the fallback ramp only; Lovely-covered cars ignore it.

Precedence, highest first:

1. User's explicit per-car values (existing overrides, e.g. UserRedlineRpm).
2. CarFacts consensus (redline and redline-per-gear facts).
3. Lovely data.
4. The current engage-percent ramp.

CarFacts interplay: Lovely values appear in the CarFacts UI as pre-filled suggestions. A user confirming one submits it as a normal human submission, so the consensus pipeline stays sybil-hardened and purely human. No automated submission of Lovely data, ever (also a ShareAlike matter, see License).

## Feature 2: car-authentic light profile in a dynamic Lightsync slot

One pipeline, not two (owner decision 2026-08-14: match layouts, not the 9 firmware patterns). At car change we COMPOSE a custom Lightsync profile from the Lovely entry (layout + colors) and write it into a plugin-owned custom slot, then select that slot. The 9 built-in firmware patterns stay exactly what they are today: the user's manual pick path, untouched.

### Layout classification

The lights on a real dash run in one of four canonical layouts: left to right, right to left, outside in, inside out. The Lovely per-LED threshold arrays encode which one directly:

- ascending thresholds across positions = left to right; descending = right to left;
- symmetric with the lowest thresholds at the edges = outside in (the M4 GT3 sample);
- symmetric with the lowest at the center = inside out.

Classify by best fit (rank correlation across active slots, dead slots excluded); ties or weird asymmetric dashes get the closest of the four. No hand-authored signature table needed; this is pure data.

### Color mapping

- Car has usable `ledColor` data: resample the active-slot colors onto the wheel's strip length (fn0 read, 10 on the G PRO) and write them into the profile. The wheel follows the car's real colors.
- No usable color data: standard green / yellow / red default, assigned in PROGRESSION ORDER (first-lit to last-lit) so it works on every layout (owner decisions 2026-08-14: red is 2 LEDs, and symmetric layouts must not split a color across a pair):
  - Linear layouts (left to right, right to left): per-LED steps, 5 green, 3 yellow, 2 red.
  - Symmetric layouts (outside in, inside out): the strip advances in mirrored pairs, so bands are pair-quantized: green, green, yellow, yellow, red (4/4/2 in LED count). Red is exactly the final pair: the center pair for outside in, the outermost for inside out.
  - Other strip lengths keep the shape: red is the last step or pair (~2 LEDs, never more), roughly half the strip green, the rest yellow.
- Resampling collapses dead slots and adapts LED-count mismatches (12-LED dash onto a 10-LED strip).

### The dynamic slot

Owner-provided fact: the G PRO rev-light LEDs are RGB; custom colors are set through G HUB Lightsync profiles, and mescon's spec has effects 5-9 as the five custom slots. mescon's [logitech-trueforce-linux-driver](https://github.com/mescon/logitech-trueforce-linux-driver) ships a live LIGHTSYNC editor for these wheels, which proves a writable slot-programming path outside G HUB and gives us an implementation to read.

- The user designates one custom slot as the plugin's dynamic slot (explicit opt-in with clear "the plugin will overwrite this slot" copy; we are writing over something G HUB considers its own).
- On car change: compose the profile (layout + colors), write the slot, select it, riding the same gated car-load apply path as pattern selection today.
- No Lovely entry for the car: leave the slot and selection alone; current behavior (user pick or the wheel's own selection) applies.
- The user's explicit per-car pick always wins over the dynamic slot. Auto-selection state lives in its own map, never in `CarRevLightEffect` (that dict stays user-intent only).

### Discovery before any code

1. Read mescon's LIGHTSYNC implementation for the HID++ feature and report format, including whether a custom slot encodes a direction/animation natively or only per-LED colors (this decides how the layout classification is expressed in the profile).
2. Confirm on Windows with a USBPcap capture of G HUB while editing a Lightsync profile (existing capture workflow).
3. Establish per-slot format limits (LED count, color depth), whether slot writes persist across power cycles, and whether a running G HUB fights us for slot ownership (it may rewrite profiles on its own profile switches; if so, document "close G HUB" or detect and back off).

Risks: protocol unknowns until captured; the LED/FFB contention rule applies to slot writes (mitigated by writing only at car change through the existing safety gate); G HUB coexistence. Feature-flagged, default off. Note the slot-write protocol is now on the critical path for auto-match, so discovery moves ahead of it in phasing.

## Wheel scope

- Feature 1 (thresholds/redlines to level): every wheel on the 0x807A level path. The strip length comes from fn0 at resolve (10 on the direct-drive wheels, 5 addressable pair-states on a G923), so the lit-fraction mapping adapts automatically.
- Feature 2 (dynamic Lightsync slot): G PRO first (custom slots = effects 5-9). RS50 later via mescon's separate RGB-zone model (0x807B, per-LED RGB, different protocol; do not assume the G PRO slot format transfers). G923 excluded (no Lightsync custom slots; PS-mode C266 has no HID++ at all).

## Contribute-back

Direction agreed. The flows are complementary: they are strong where SimHub sims are, we are strong where they are empty (Forza, AC mods, and whatever our consensus accumulates).

- What: human-vetted consensus-tier facts only (per-gear redlines, car names), plus our Forza data if they want Forza simIds at all (their call; propose upstream first).
- How: curated, owner-reviewed PR batches in their format (their repo enforces pre-commit formatting hooks). No automation.
- Prerequisite: our community DB has no declared license yet. Decide one that permits donating derived batches under their CC BY-NC-SA before the first PR.

## Phasing

- M1: LovelyCarDataStore (fetch, cache, mapping) + Feature 1 thresholds/redlines behind the new toggle.
- M2: slot-write protocol discovery (mescon's code, USBPcap of G HUB's Lightsync editor).
- M3: Feature 2 dynamic slot: layout classification + color mapping + slot write at car change.
- M4: CarFacts suggestion pre-fill from Lovely values.
- M5: contribute-back tooling (consensus export in their format) + first curated PR.

## Open questions

- `ledColor` index-0 meaning (blink color vs 1:1 alignment with the rpm arrays).
- Exact simId strings for our supported games; whether SimHub's AMS2/LMU carIds match theirs byte-for-byte before normalization.
- `redlineBlinkInterval` 0 semantics.
- Blink mechanics on the level path: whether the firmware blinks at full level on its own (per profile) or we emulate by toggling the fn6 level at the interval ourselves. Emulation adds LED write traffic at redline, which touches the LED/FFB contention rule; resolve during M2 discovery.
- Default state of the master toggle (owner decision).
- Custom-slot profile format: does a slot natively encode direction/animation or only per-LED colors; LED count and color depth per slot; persistence across power cycles; G HUB coexistence (all part of M2 discovery).
- Track data ([lovely-track-data](https://github.com/Lovely-Sim-Racing/lovely-track-data)): no consumer in our effect set today; parked unless corner-anticipation haptics ever becomes a thing.
