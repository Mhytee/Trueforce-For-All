# Tester handoff — haptic-engine branch (G Pro wanted!)

This branch turns Trueforce-For-All into a full haptic engine: instead of
passing the game's force feedback through, it **synthesizes the steering
force from telemetry** (slip angles, per-tire grip, suspension travel) and
layers grip/road/event textures on top through the TrueForce audio channel.
The first target is **Forza Motorsport (FM8)**, where the game's own FFB
can't coexist with a TrueForce plugin anyway — so we build the whole feel
ourselves, and honestly it's better than what the game ships.

Everything below is validated on a **G923**. You have a **G Pro** — that's
exactly what we need: same TrueForce protocol, very different motor. This
doc is everything you need to get running and to tell us what's different.

## Where we left off (2026-07-07)

- Mode B (synthesized FFB) is the daily driver on FM8: tire-model steering
  force with peak-and-drop at the grip limit, cornering weight from lateral
  g, counter-steer pull on rear breakaway, suspension-load weight transfer.
- 12 "Feel layers" exist, each behind its own checkbox (settings page,
  "Feel layers" section). 1, 3–11 are validated and ship ON. **2 (kerb
  thump) and 12 (frequency-aware ducking) are still unvalidated** — they
  ship OFF and we'd love your verdict on both.
- Per-car grip auto-calibration is live: drive a car hard for about a lap
  and the plugin learns where that car's grip metric really tops out, then
  normalizes the limit feel. Persists per car. Watch the SimHub log for
  `Grip auto-cal converged: peak=...`.
- Crash protection (force goes soft on impact, breathes back in ~0.6 s) and
  a low-speed gate (no synthesized force below walking pace, where slip
  data is mathematically garbage) are always-on.
- 179/179 unit tests green. The full plan lives in
  `docs/haptic-engine-plan.md`; the frequency/feel research in
  `docs/steering-feel-physics.md`.

## Install

Prereqs: SimHub (recent), [USBPcap](https://desowin.org/usbpcap/) installed
(reboot after), FM8, and **G HUB fully quit** (right-click the tray icon →
Quit — it fights the plugin for the wheel; also kill `lghub_agent` in Task
Manager if it respawns).

1. Build the solution (VS 2022 or `dotnet build TrueforceForAll.sln -c Release`).
2. Close SimHub, then run `scripts/install-plugin.bat` **as administrator**
   (right-click → Run as administrator). It copies the THREE plugin DLLs
   into the SimHub folder — all three matter; a missing
   `TrueforceForAll.Engine.dll` means the plugin silently fails to load.
3. Start SimHub, enable the Trueforce-For-All plugin, open its settings page.

### FM8 setup

- FM8 → Settings → Gameplay & HUD → UDP Race Telemetry: **ON**, IP
  `127.0.0.1`, port `5300`. (MS Store build: you may need the
  `CheckNetIsolation` loopback exemption; Steam build: send to your LAN IP
  if loopback shows nothing.)
- FM8 wheel settings: **force feedback / vibration scale to 0**. Mode B
  must be the only thing driving the wheel. If the game is still sending,
  the log will shout `Mode B CONTENTION` at you — that's your cue.
- Mode B arms automatically when SimHub detects FM8. Drive.

### First-run sanity

- Log line `Startup re-attach: cycling the device once...` a few seconds
  after start is EXPECTED (it clears a Logitech firmware quirk).
- **First corner: check the force direction.** On the G923 the self-aligning
  torque direction was correct out of the box, but if your wheel pulls INTO
  corners instead of centering, type `BSIGN -1` in the access-code box at
  the bottom of the settings page. Tell us either way — this is per-wheel
  info we need.

## What we need from a G Pro

1. **Motor band map.** Type `SWEEP1` through `SWEEP6` in the access-code box
   (octave sweeps: 8-16, 16-32, 32-63, 63-125, 125-250, 250-400 Hz, 5 s
   each) and describe each band: violent / strong / buzzy / barely there /
   dead. The G923 peaks violently at 63-125 Hz and is dead above 250 — if
   the G Pro's map differs (it will), effect frequency placement becomes a
   per-wheel profile and your map is its first data point.
2. **Mode B first impressions** at defaults: does the wheel go light when
   the front washes out? Does the counter-steer pull on a power slide feel
   like the right direction and strength? The G Pro has ~3x the torque of a
   G923 — the "Strength" slider (Mode B section) may want to come down.
3. **The two unvalidated layers.** Check layer 2 (kerb thump) — drive over
   kerbs, is the entry WHACK distinct from the sustained rumble? Check
   layer 12 (frequency-aware ducking) — hold a slide with revs high; the
   scrub texture should stay crisp through the engine pulse instead of
   blending into it. On/off A/B both while driving; everything applies live.
4. **Center feel.** The "Center feel" slider (Mode B section) sets the calm
   zone at dead center. 0.12 is the G923-validated default. If your center
   buzzes, raise it; if it feels disconnected, lower it. Report your number.

## Reporting

- SimHub log: `C:\Program Files (x86)\SimHub\Logs\SimHub.txt` — grep for
  `[Trueforce]`. Warnings are written to be actionable; read them.
- High-rate trace: type `TRACE` in the access-code box, reproduce the
  problem for up to ~30 s, type `TRACE` again — a CSV lands in
  `Documents\TrueforceForAll\`. Attach it; a trace found and fixed five
  separate buzz sources for us in one day. **Only toggle TRACE off on a
  straight** (the dump can hiccup the stream for a moment).
- Per-band sweeps, layer verdicts, slider values: plain words are fine.
  "Layer 9 kicks the wrong way on left-wheel kerbs" is a perfect bug report.

## Access-code cheat sheet

Typed into the box at the bottom of the plugin settings page:

| Code | What |
|------|------|
| `MODEB 1` / `MODEB 0` | Force Mode B on/off (auto-arms on FM8 anyway) |
| `BSIGN -1` | Flip the synthesized force direction (per-wheel) |
| `BDIRK 0.2` | Center flat-spot width, live (same as the Center feel slider) |
| `SWEEP1`..`SWEEP6` | Octave motor sweeps for the band map |
| `SWEEP` | Full 8-300 Hz sweep, 15 s |
| `FAULT` | Force one device re-attach cycle (heals a silent audio channel) |
| `TRACE` | Toggle the high-rate FFB trace (CSV on stop) |

## Known quirks

- Any SimHub restart while FM8 holds the wheel can wedge the wheel's audio
  channel (force works, textures silent). The plugin self-heals ~3 s after
  startup; if textures ever die mid-session, `FAULT` fixes it by hand.
- If the wheel is dead after replugging, launch G HUB once to let it
  re-initialize the wheel, then QUIT G HUB fully and restart SimHub.
- The tap needs USBPcap's driver; if the log complains it can't find the
  capture device, rerun the USBPcap installer and reboot.
