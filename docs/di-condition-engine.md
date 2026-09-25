# DirectInput condition effects under the Trueforce stream

Status: SHIPPED and on by default. Phase 1 was built 2026-09-01 and the
damper sign was validated on the rig the same day (see "First rig session"
below); the half-scale authority cap came off 2026-09-05.

Owner mandate: the force that reaches the wheel with the plugin running
must match the native force, so the game's DirectInput condition effects
(damper, spring, friction, inertia) have to be captured and rendered into
`cur`, in every game, not just AC.

Build deltas from the design below, decided during implementation:

- The CONSTANT force stayed entirely on the shipped scalar path (arbiter,
  `_packed`, freshness windows), byte-identical: it already mirrors 1:1 and
  every existing test still pins it. The engine owns everything else, so
  constant envelopes / durations / multi-slot sums / global gain on
  constants remain phase 2 refinements (gain changes are logged when a game
  sets less than 100 %).
- Periodics and ramp ARE rendered in phase 1 (owner: "everything").
- The wheel-reply slot assignment is implemented (interrupt IN, fn2 echo,
  params[0]); the fallback is provisional lowest-free-slot placement,
  corrected by the reply or by the driver's own re-download.
- Rendering ships ON by default behind the `DICOND` A/B kill switch. The
  engine term was capped at half scale until the rig check; that cap was
  lifted 2026-09-05 (5f6cb3a) and decoded conditions now reach full scale,
  with only the wheel's own range clamping the sum. `CSPFFB DAMPSIGN` flips
  the sign, `CSPFFB DAMPK` / `DAMPCAL` own the damper scale. The knobs were
  session-only at first; the tuned gains persist now (see "Persistence"
  below).
- Position source at the pump, best first: shared DirectInputWheel (AC
  sessions), WheelSteeringReader, fresh game steering; one
  WheelMotionEstimator (alpha-beta) derives velocity/acceleration so the
  units stay DAMPCAL's regardless of source.
- Tests: HidppEffectEngineTests + UsbPcapHidppEffectTests (trace bytes,
  sign contract, gain, state functions, reply assignment, NOFFB, the
  quiet-hold traffic guard); suite 757 green.

Rig checklist, in order: (1) AC parked, damper 75 %: rim should be heavy
like native; if it flutters or speeds up on a flick, `CSPFFB DAMPSIGN`.
(2) `DICOND` off/on A/B for the felt difference. (3) `DAMPCAL` for the
measured damper gain (persists nothing yet; note the number). (4) A
periodic-effects game (Forza/rally class) for the rumble path.

## Why this exists

While an ep3 type-0x01 session is open the wheel firmware stops rendering
its whole 0x8123 slot engine, conditions included, and steers by `cur`
alone (parked flick, G PRO, 2026-08-31; mescon documents the constant-force
half of this). The shipped tap mirrors only the constant force, so every
tap-driven game loses its damper and spring: AC's parked damper, iRacing's
damping slider, Forza's centering. This has been true since v0.1.12; the AC
CSP bridge work made it visible.

## What is on the wire (decoded from the 2026-08-31 usb-trace.pcap)

The Windows runtime drives the wheel with the mainline Linux `hidpp_ff`
slot dialect on feature 0x8123, byte for byte
(`drivers/hid/hid-logitech-hidpp.c`, `hidpp_ff_upload_effect`):

    [reportId][0xff][featIdx][fn<<4 | swid][slot][type | 0x80 autostart][len:2][delay:2][params...]

| fn | name             | params (offsets from the HID++ payload start)                     |
|----|------------------|-------------------------------------------------------------------|
| 1  | RESET_ALL        | none                                                              |
| 2  | DOWNLOAD_EFFECT  | slot @4 (0 = new, wheel assigns), type @5, len @6-7 ms (0 = infinite), delay @8-9, effect block @10.. |
| 3  | SET_EFFECT_STATE | slot, state (1 stop, 2 play, 3 pause)                             |
| 4  | DESTROY_EFFECT   | slot                                                              |
| 8  | SET_GLOBAL_GAINS | gain u16 (0xFFFF = 100 %), boost u16                              |

Effect types: 0x00 CONSTANT, 0x01..0x05 PERIODIC (sine, square, triangle,
saw up, saw down), 0x06 SPRING, 0x07 DAMPER, 0x08 FRICTION, 0x09 INERTIA,
0x0A RAMP. Bit 7 = autostart.

Effect blocks (all big-endian):

- CONSTANT: level s16 @10-11; envelope attack_level u8 @12 (of 255),
  attack_length u16 @13-14 ms, fade_level u8 @15, fade_length u16 @16-17.
  Rides report 0x11 (14 param bytes fit the long report).
- CONDITIONS: left_sat u16 @10-11 (wire = sat >> 1), left_coeff s16 @12-13,
  deadband u16 @14-15 (wire = deadband >> 1), center s16 @16-17,
  right_coeff s16 @18-19, right_sat u16 @20-21. Rides report 0x12 (18 param
  bytes do not fit 0x11). AC parked: `12 ff 0e 2f 02 87 00 00 00 00 7f ff
  5f b0 00 00 00 00 5f b0 7f ff 00 00` = slot 2 DAMPER, coeff 0.748 both
  sides, saturation full, no deadband, center 0.
- PERIODIC: magnitude s16 @10-11, offset s16 @12-13, period u16 @14-15 ms,
  phase u16 @16-17, envelope @18-23. RAMP: start s16 @10-11, end s16
  @12-13, envelope @14-19.

New-effect downloads carry slot 0; the wheel answers on the HID++
interrupt IN endpoint with the same report shape, function echoed, and the
assigned slot in params[0] (`12 ff 0e 2a 01 ...` in gpro_ffb_withleds.pcap).
Updates to a live effect carry its slot. AC re-downloads the damper on
every coefficient change (659 times in 333 s parked) and the constant on
every force change; it sent no fn3/fn4 in that trace, other games will.

Consequences for existing code: the effect type byte at offset 5
classifies a packet deterministically. FfbReportArbiter's rate/change
heuristic (which report carries force) is superseded; the RS50 "constant
32767 on 0x12" of issue #8 was a condition download's saturation field.

## Architecture

### Core: `HidppEffectEngine` (new, pure, unit-tested)

A slot table (16 slots) fed by decoded commands from the tap's parser
thread, evaluated on the pump thread:

- `Download(slotOrZero, type, autostart, lengthMs, delayMs, block, nowTicks)`,
  `AssignSlotFromReply(slot)`, `SetState(slot, state, nowTicks)`,
  `Destroy(slot)`, `ResetAll()`, `SetGlobalGain(u16)`.
- `ConstantSum(nowTicks)`: sum of playing CONSTANT slots with duration,
  delay and envelope applied, times global gain. Replaces "latest write
  wins" (a game with two constant slots is summed, like the firmware).
- `EvaluateConditions(pos, vel, acc)`: sum of playing SPRING / DAMPER /
  FRICTION / INERTIA slots, times global gain, in the DirectInput model:
  spring = coeff_side x (dev beyond the half deadband), damper = coeff x
  velocity, friction = coeff x sign(velocity) (with a small velocity dead
  zone), inertia = coeff x acceleration; each side clamped to its
  saturation. Positions and velocities are the wheel's normalized range
  (-1..1 across the configured rotation, per second), the same units
  DirectInputWheel and WheelSteeringReader produce.
- Cross-thread: parser thread mutates the table and publishes an immutable
  snapshot (reference swap, the `_playingSprings` pattern); the pump reads
  the snapshot. Allocation-free evaluation.
- Slot assignment: reply-driven when the tap sees it, else lowest free
  slot >= 1 after 50 ms (mainline semantics; matches the trace).
- Unsupported types (periodic, ramp) are decoded, counted and logged once
  per session ("game uses periodic effects; not rendered yet"), so we learn
  which titles need phase 2 instead of guessing.

### Sign contract (must hold before anything reaches the wheel)

`cur` positive pulls toward LOWER steer (left). That is the convention the
stationary spring is hardware-validated on (AC + G PRO: `dir = steer > 0 ?
+1 : -1` recenters) and the classic spring emulation shares. So in engine
units: spring `F = +k * dev`, damper `F = +c * vel`, friction
`F = +c * sign(vel)`, inertia `F = +c * acc`. The tap's constant passes
through unchanged, as today. FfbInvertSign still applies downstream to the
whole sum, so a user inversion stays consistent. One on-wheel check
confirms the damper sign (a wrong sign is an anti-damper: a flicked wheel
speeds up instead of settling); the access codes below make that a
ten-second test.

### Position and velocity source

Recommendation: WheelSteeringReader (HID interface 0, already deployed,
self-healing, event-driven at the wheel's report rate, no DirectInput
acquisition in the game's way), through a small Core `WheelMotionEstimator`
(alpha-beta on position, yielding velocity and acceleration; a 16-bit
encoder differenced at 1 kHz needs the smoothing). DirectInputWheel stays
for DAMPCAL's exclusive native-damper reference and as the fallback reader
if the HID stream is unavailable. Both report the same normalized units, so
a DAMPCAL gain transfers.

### Plugin wiring (provider lambda in AttachDevice)

1. Tap: the ep0 and interrupt-OUT HID++ decoders route every fn1/2/3/4/8
   packet on the FFB feature index into the engine; the interrupt-IN
   decoder (new; the tap ignores IN today) feeds download replies. The
   scalar `_packed` path publishes `ConstantSum` instead of the last
   constant level, so every freshness window, quiet-spell hold and
   capture-health rule in the provider keeps working unchanged.
2. Provider: after `chosen` is settled and before the stationary spring,
   add `tap.TryEvaluateHidppConditions(pos, vel, acc)` when the tap has a
   live capture. Skip under Mode B (MaybeReshapeFfb replaces the target)
   and skip AddSynthesizedDamper's CSP term while the engine is rendering a
   decoded damper (the CSP synthesis stays for the tap-free AC path, which
   sees no downloads to decode).
3. Gates: `AnyHidppConditionPlaying` joins `AnyClassicSpringPlaying`
   wherever "game FFB quiet" is decided (LED / OLED write windows, the
   FS25 misdecode drop, the no-FFB escalation). Condition re-downloads are
   ep0 force writes for the contention model too.
4. Access codes, session-only: `DICOND` on/off A/B, `DICONDK <0..2>`
   damper gain, `DICONDSIGN` flip. Shipped default ON after the owner's
   on-wheel check (fidelity is the mandate, and the CSP damper already
   showed the missing damper is felt as a regression).
5. Caps: the engine sum was clamped to +-0.5 FS on top of the per-effect
   saturations while unvalidated. Lifted 2026-09-05 once DICOND was
   validated; the wheel's own range is the only clamp left.

### Calibration

DAMPCAL (built, unrun) measures the ratio of the native damper to ours by
release decay rates and outputs a gain in exactly the engine's units:
force fraction per (coefficient x normalized range/s). The engine takes
that number as its damper scale (and friction until FRICCAL exists).
Spring needs no scale under DirectInput semantics (coefficient x
normalized deviation = force fraction) but the firmware may differ: phase
2 adds SPRINGCAL by natural frequency (a released wheel under a spring
oscillates at f = sqrt(k/J)/2pi; native vs synthesized at a probe gain
gives k by (f_native/f_synth)^2, friction-tolerant). Inertia is rare and
waits for a game that uses it.

## Roadmap (revised 2026-09-01 after the audit + prior-art study)

Phase 1 (BUILT, see the status block above): engine + decoder + IN reply
parse + tests + provider wiring + access codes; all ten parametric types
rendered; constants untouched on the scalar path.

Phase 1.5, PRE-RIG POLISH: BUILT 2026-09-01 (same day; suite 764 green,
Release 0 warnings, deployed). All seven items below landed; deltas: the
unknown-type drop applies only when the autostart bit is set (an
ambiguous byte without it stays on the scalar path so a foreign dialect
cannot be deafened; the arbiter's change gate guards it), the
driver-intercept fix is the minimum type-byte guard (conditions are
dropped there, not rendered; full engine feed only if the mode is kept),
and pause still resets the table (parameter retention stays phase 2).
The mescon report was canceled by the owner the same day; nothing is
sent externally. Original item list:

Engine-local, unit-testable without the wheel,
done BEFORE the rig session so the calibration validates the final
shapes instead of being redone after them. Informed by the three open
implementations, which independently converged on the same stability
answers:

1. Friction rewrite: Simucube's stick-slip lock point (static friction,
   no zero-crossing chatter) composed with OpenFFBoard's deadband/offset
   handling and sinusoidal easing (concepts only from Simucube: EULA).
2. Per-condition output low-pass, tunable: every prior implementation
   filters condition OUTPUTS, not just the velocity input; this is the
   proven anti-ring mechanism and de-risks the first stiff-spring title.
3. Expiry pruning (audit gap: expired effects pin AnyDamperPlaying and
   suppress the CSP fallback damper).
4. Envelope invalid on infinite-duration effects (match OpenFFBoard /
   PID semantics; ours applies attack at len=0).
5. Audit quick wins: unknown fn2 types to a counted drop; deferred
   parser-thread reset for ClearLastFfbTarget; DAMPSIGN scoped to
   velocity-derived terms only; NOFFB ingest gate; counter atomicity.
6. Driver-intercept mode: minimum fix is the type-byte check in
   TFFADriverChannel (stops the +32767 misdecode); full engine feed only
   if that mode is kept.
7. Parametric-only game credit: a handled download counts as extraction
   (no false no-FFB escalation) and satisfies the active-shape hold.

RIG GATE (owner): sign check, DICOND A/B, DAMPCAL. The sign was validated
2026-09-01, the half-scale cap came off 2026-09-05 (5f6cb3a), and the gains
persist (TrueforceSettings fields, classified in BackupProjection). The
shipped gains came from the FXTEST effect bench rather than DAMPCAL, which
still exists for the measured damper number.

Phase 2, EVIDENCE-DRIVEN (each item needs a capture or a measurement,
not more code study; prior art cannot answer these):
- Periodic phase units: Logitech-dialect u16 vs DI 35999 centidegrees
  (factor ~1.82); needs one pcap from a periodic-heavy title.
- Deadband wire scaling and damper deadband/center velocity units.
- SPRINGCAL by natural frequency; FRICCAL if a friction title appears.
- Slew-limiter / smoothing exemption for the engine term; band-split
  interaction.
- fn8 gain onto the scalar constant; constant envelope/duration/
  multi-slot via the engine if a game is found that needs them.

Phase 3, BREADTH: classic C266 per-type parsers feeding the SAME
renderer and output filters (13 missing types + default-spring family +
the on-wheel sign A/B); CSP bridge export of non-damper effects for
tap-free rigs; LED/OLED quiet-gate awareness of parametric traffic;
R3EFFB contention guard.

Closed questions (do not revisit): the rendering model is settled
(validated by three independent implementations); no further prior art
exists to study (Simucube 2 is closed; the capture side has no
precedent); absolute scale is measured per wheel (DAMPCAL), never
derived; the constant path stays untouched.

## Tests

- Decoder: the trace bytes above for every function and type, both report
  ids, the interrupt-OUT form (G923 Xbox), reply slot assignment and the
  50 ms fallback.
- Engine: constant sum and gain; duration expiry; stop / pause / destroy /
  reset; envelope shape; each condition's deadband, per-side coefficient,
  saturation and sign against the ClassicSpringEmulationTests contract.
- End to end through `ParseFrom` with a synthetic pcap including IN
  records, the existing harness.

## Open decisions (owner)

1. Velocity source: WheelSteeringReader + estimator (recommended) or
   DirectInputWheel shared-mode polling.
2. Keep FfbReportArbiter as a fallback for a wheel whose downloads we fail
   to classify, or retire it outright once the type-byte path ships.
3. Default state at release: ON after the rig check (recommended), or a
   Settings checkbox.

## Fidelity audit, 2026-09-01 (55-agent adversarial sweep over every force path)

Method: five mappers (tap HID++ routing, engine math, classic C266 path,
non-tap sources, device chain + freshness), then two adversarial refuters
per load-bearing claim. Every routing claim on the new code CONFIRMED:
constants byte-identical to pre-engine (verbatim block, arbiter untouched),
parametric types can never be misread as force, both transports and report
ids share one router, quiet-hold stamping correct, pause reset and NOFFB
correct at the packet level, engine wire decode and condition math match
DI semantics in the validated sign space. The gaps below are what stands
between the current build and 1:1; ranked.

### Bugs the audit found (fix first)

1. DRIVER-INTERCEPT MODE (experimental, default off): TFFADriverChannel
   .RecvLoop decodes bytes 10-11 of EVERY fn2 with no type-byte routing, so
   a condition download's 0x7FFF saturation publishes as a +32767 constant
   on the highest-priority source: the issue-#8 misdecode reintroduced,
   full-scale spikes on every damper re-parameterization. Also: intercepted
   writes are absorbed, not echoed, so under driver mode conditions are
   neither rendered nor firmware-handled. Small fix (IsParametricType check
   + feed a shared engine) or gate the mode off until done.
2. Unknown fn2 types (0x0b..0x7f after masking autostart) still fall
   through to the scalar extractor; on a live report the arbiter accepts
   them. Route type > TypeRamp to a counted drop.
3. ClearLastFfbTarget calls _hidppEffects.ResetAll() from plugin threads
   while the parser may be mid-download: mirror the classic path's
   deferred-reset flag (_playing=null synchronously is safe; table clear
   belongs on the parser thread).
4. CSPFFB DAMPSIGN multiplies the WHOLE engine term (spring/periodics
   included), so fixing an anti-damper would create an anti-spring. Scope
   the sign to velocity-derived terms inside the engine.
5. Expired finite-duration effects are never pruned from the snapshot:
   force is correctly 0 but AnyPlaying/AnyDamperPlaying stay true, which
   keeps the CSP synthesized damper suppressed indefinitely.
6. NOFFB asymmetry: parametric ingest still stamps _lastSampleTicks and
   counters under SimulateNoFfbCapture, self-defeating the simulation in
   any game with condition traffic.
7. Parametric counters are non-volatile longs on a 32-bit host (torn
   diagnostic reads); make them ints/Interlocked.

### Parametric-only games (conditions with no constant stream)

- Never confirm the feature index or count as captured FFB: the no-FFB
  escalation (whole-bus restart + user warning) fires while we are
  decoding and rendering their effects. Credit a handled download as
  extraction.
- Never enter the ACTIVE shape: AddHidppDiEffects needs chosen.HasValue
  and the quiet-hold needs a constants-decided arbiter, so the engine term
  and all Trueforce haptics stay dead (firmware renders natively in
  keepalive, so game feel is right; our haptics are not). Let
  AnyHidppParametricPlaying satisfy the hold's session condition.

### Known fidelity deltas (deliberate or pending calibration)

- Authority cap +-16384: closed. Lifted 2026-09-05 (5f6cb3a) once the rig
  had validated the sign and the scale.
- Damper/inertia gains are bench-tuned (G PRO effect bench, 2026-09-19 and
  2026-09-20) rather than DAMPCAL-derived, and they persist: they are
  TrueforceSettings fields, classified in BackupProjection. Spring slope
  1.0 assumed; SPRINGCAL pending.
- fn8 global gain applies to the engine term but not the scalar constant.
- Pause wipes the effect table and assumes re-download; native slots
  persist. Retain parameters, suspend playback, resume on PLAY. fn3 PAUSE
  is treated as STOP with a clock restart (re-attack on resume).
- The engine term rides the full pump chain: FfbScale (default 0.80),
  slew limiter (preset-scoped; builtin presets set these), smoothing.
  A damper lagged by an LPF damps less; bypass is phase 2. Whether 0.80
  equals the real ep3-vs-ep0 gain ratio has never been measured.
- Deadband wire scaling (>>1, half-width) and damper/inertia
  deadband/center velocity units are self-consistent inferences; no trace
  has exercised them. Periodic phase units and waveform origins likewise
  (need one pcap from a periodic-heavy title).
- Friction ignores its deadband/center; the 0.05 norm/s ramp window is an
  anti-chatter invention.
- Provisional slot tracking is single-entry; interleaved new-effect
  downloads before the wheel's reply can mis-address a later fn3/fn4
  (self-heals on re-download).
- Position-source dropout (no DI reader, HID reader dead, game steer
  stale) silently disables condition rendering; wants a log line and
  EnsureWheelMotion on first parametric.
- 500 ms replay window + active-zero hold vs the firmware's indefinite
  force hold: the deliberate issue-#13 safety trade. Resume ramp (300 ms)
  and shape-switch feel edges likewise plugin-only. Stationary spring is a
  default-on authored force (product choice).
- Residual: _slewLimitedFfb not reset on keepalive handback (ms-scale
  onset transient).

### Classic C266 path (separate work item, pre-dates this project phase)

Variable force (0x08) and hi-res spring (0x0b) render unconditionally. All
fifteen types decode as of 2026-09-17, everything except the two scalars
through the condition engine behind the CLASSICCOND gate: damper
(0x02/0x0c) and friction (0x0e) since 2026-09-16, and the low-res spring
(0x01), the auto-centers (0x03/0x0d), the periodics (0x04-0x07/0x09/0x0a)
and the ramp since 2026-09-17. Since 2026-09-18 the hi-res spring lands in
the engine as well, additively, while keeping its own ClassicSpring record
for spring mode, which closes the FH5-class mixed-traffic gap where road
force arrived with no autocenter because the spring had only the exclusive
spring-mode renderer (spring-ONLY bus + 2 s + 1 s dwell, 80% strength,
ramps). See the three dated entries at the end of this file. The
default-spring command family (cmds 0x4/0x5/0xe/0xf, present in the FH5
capture) is still ignored. Audit also flagged a sign-contradiction between
the variable decode and the spring evaluator that needs one on-wheel C266
A/B, the 2^K spring slope guess vs lg4ff's table (the gate reads it linear
instead), and byte3 for the second variable slot.

### Tap-free CSP mode (no USBPcap)

Only the damper is synthesized; the bridge exports no spring/friction/
periodic parameters. AC's own output is constant+damper so the practical
gap is narrow; closing it means extending the Lua bridge.

### R3EFFB

No guard against the game still driving the wheel (GameFfbOn hardcoded);
wants the Forza-style contention watch.

## The effect test bench (FXTEST), built 2026-09-01

The rig-validation and calibration surface, in two layers:

- UI: Settings tab, "Effect test bench" expander (below the access-code
  box). Effect picker (all ten types), strength and period sliders, Play
  native / Play engine / Stop, live tuning (a PER-EFFECT gain slider,
  Flip direction = DAMPSIGN, velocity terms only; condition filter Hz
  slider on both renderers), and Save tuning.
- Access codes for the same thing: `FXTEST NATIVE|ENGINE <effect>
  [strength%] [periodMs]`, `FXTEST OFF` (auto-off 120 s).

NATIVE plays the wheel's own DirectInput effect via exclusive
DirectInput with the Trueforce stream fully STOPPED (the master-toggle
Stop-then-Pause sequence, resumed on exit), so the FIRMWARE renders it
on a wheel exactly as it is without the plugin: the reference feel.
Owner's correction, 2026-09-01: keepalive was NOT good enough for the
reference, because whether the keepalive shape truly restores ep0
condition rendering is the still-open protocol question; DAMPCAL's
native phase and the DIDAMP spike were moved to the same real stop for
the same reason (their references would otherwise ride the unproven
assumption). A deliberate keepalive-vs-stopped native A/B on this bench
is now also the experiment that would settle that open question.
ENGINE builds the identical parameters as a
synthetic 0x8123 download (Core/FxTestPayloads.cs, round-trip tested)
and runs it through the real decode-and-render path into cur, base 0,
so no game is needed. Alternate on one effect, tune until they match:
that is the calibration, per effect, on the rig. DAMPCAL still exists
for the measured damper number; run it, then Save.

Per-effect gains (owner, 2026-09-01: "there doesn't seem to be a way to
tune one effect, save its values, change effects, tune, save; the values
all show the same as I switch effects"). One slider used to edit the
damper number no matter which effect was picked, and inertia had no
number of its own (it borrowed the damper's), while spring and friction
were reachable only through auto-tune. Now every family the renderer
covers carries its own gain, and the slider follows the effect picker:

| Picked effect | Gain edited |
| --- | --- |
| DAMPER | `FfbConditionDamperGain` (the DAMPK/DAMPCAL number) |
| SPRING | `FfbConditionSpringGain` |
| FRICTION | `FfbConditionFrictionGain` |
| INERTIA | `FfbConditionInertiaGain` |
| SINE / SQUARE / TRIANGLE / SAWUP / SAWDOWN | `FfbConditionPeriodicGain` (one waveform scale) |
| RAMP | `FfbConditionRampGain` |

`HidppEffectEngine.Evaluate` takes periodicGain and rampGain alongside
the existing four; the plugin passes all six at every call site. Hand
edits on the slider touch ONLY the picked family. The two measurement
paths that cannot measure inertia (auto-tune's Done branch and
SetCspDamperGain / DAMPCAL) carry the damper result across to inertia
while the two are still equal, and leave inertia alone once the bench
has tuned it apart: linked until unlinked.

Persistence (closes the "calibration is session-only" audit gap): the
tuned gains, direction and filter live in TrueforceSettings, which is the
source of truth for the numbers (generation 1, from the G PRO effect bench
on 2026-09-19 and 2026-09-20: FfbConditionDamperGain 1.0 /
FfbConditionSignInverted false / FfbConditionLpfHz 200 /
FfbConditionSpringGain 4.5 / FfbConditionFrictionGain 0.15 /
FfbConditionInertiaGain 0.10 / FfbConditionPeriodicGain 0.9 /
FfbConditionRampGain 0.5, with FfbConditionDefaultsGeneration re-seeding the
gains and filter cutoffs in every stored file once; the direction flip is a
wheel fact, not tuning, and is held), classified
Portable in BackupProjection (wheel feel, travels like FfbScale),
applied at device attach. One Save writes all of them, so the loop is
tune -> switch -> tune -> Save once. Session knobs (CSPFFB
DAMPK/DAMPSIGN, the sliders) persist only via Save.

Reset (owner, 2026-09-01): "Reset" on the gain row puts the PICKED
effect back to its shipped default and leaves the others alone; "Reset
all" next to Save puts every gain, the direction and the filter back.
Both read their defaults from a fresh `TrueforceSettings` instance, so
the settings model stays the single source of truth. Both are live-only
in keeping with the rest of the bench: Save persists a reset, and
reopening SimHub without saving brings the stored tuning back.

### AUTO-TUNE (hands-free calibration), built 2026-09-01

The bench's "Auto-tune" button (owner's idea: the plugin can excite the
wheel itself). Core/WheelAutoTuner.cs runs bounded self-pulses (0.22 FS,
escalating to at most 0.4 on weak trials, always toward center) and
identifies each effect by its physical signature, native group first
(stream truly stopped) then engine group, three trials each, medians:

- Force scale: peak coast speed per equal pulse, native vs engine. The
  ratio IS the measured native-vs-stream force scale (what FfbScale has
  guessed at since v0.1.12) and equalizes all later engine probes.
- Damper: exponential decay rate; friction/inertia cancel via the
  engine baseline phase (DAMPCAL's math, pulses instead of hand flicks).
- Spring: post-pulse oscillation frequency; gain = (fN/fE)^2, inertia
  cancels. (The planned SPRINGCAL, automated.)
- Friction: linear deceleration, baseline-canceled.

Results land in the live gains for the owner to VERIFY BY FEEL on the
bench (auto-tune proposes, hands confirm), then Save persists. Safety:
|pos| > 0.9 or |vel| > 10 aborts with everything zeroed; Stop cancels;
phase trial caps abort rather than loop. The whole math is pinned by a
physics-simulator test (WheelAutoTunerTests: a virtual wheel with known
firmware constants; the tuner must recover them through the full run).
Inertia and periodic-phase remain manual/capture questions.

PRE-HARDWARE AUDIT, 2026-09-01 (60-agent adversarial fleet, run before
the first real-wheel run at the owner's request): 26 confirmed findings
collapsing to ~10 defects, ALL FIXED same day (suite 773 green):

- Single-effect-slot bug (would have failed EVERY run): the probe pulse
  destroyed the condition effect under test. DirectInputWheel now has a
  DEDICATED pulse effect slot, created once and driven by parameter
  updates (also fixing pulse-onset latency biasing the force ratio, and
  the pulse clock now stamps AFTER the drive call).
- No watchdog (safety): a dead poll thread left a latched pulse torque
  streaming with the runaway guard dead. Now: 180 s hard cap, the poll
  loop retries transient errors and notifies its owner on ANY abnormal
  exit, and a pump-side deadman zeroes output and cancels the run if
  wheel samples stop for 750 ms.
- Stranded-state family: game-change edges, FXTEST/DAMPCAL/DIDAMP
  started mid-run, plugin End(), and the StopStreamOnPause gate could
  each kill the sample loop or fight the run; all now guarded or torn
  down (End() cancels every bench tool; the pause gate stands down
  during runs; sibling tools refuse or cancel a running auto-tune).
- Accuracy: force-phase peaks are amplitude-normalized (the escalation
  ladder no longer skews the ratio); the nonlinear chain stages (spike
  soft-knee, smoothing) are neutralized for the run and restored after;
  the engine-vs-DirectInput direction frame is DETECTED from the force
  phases' signed responses and compensated (FfbInvertSign rigs), with a
  note in the result.

## First rig session, 2026-09-01 (owner) and the fixes it drove

Field results from the first hardware contact:

- DAMPER SIGN VALIDATED: the rendered damper in AC "feels like a proper
  damper" at speed with no direction flip needed. The big unknown is
  closed; DAMPSIGN stays as a per-rig escape hatch only.
- Slow-turn SHAKE in AC: velocity was being re-derived from quantized
  DirectInput position through the alpha-beta estimator; near zero speed
  the noise dominated and the damper amplified it. FIXED: when the DI
  reader is the source, its own differenced+smoothed velocity is used
  (one derivation), and the estimator publishes velocity through a
  ~60 Hz one-pole either way (WheelMotionEstimator.UpdateWithVelocity).
- FXTEST ENGINE effects read as silent: (a) the renderer bailed
  entirely when no position source was live, which also killed
  waveforms that need no position; now zeros are used instead
  (conditions output zero, sine and friends always play) with a
  one-shot log; (b) a damper/spring at a still, centered wheel
  legitimately outputs nothing; (c) at the default 0.25 damper gain the
  effect is genuinely weak until tuned; the start log now prints the
  live gains.
- AUTO-TUNE runaway abort near the lock during native phases: coast
  drift accumulated across trials with nothing recentering (the guard
  worked and aborted; the run was lost). FIXED per the owner's rule
  ("center, play effect, center, play effect"): every trial begins with
  an active re-centering sub-state (gentle proportional drive, capped
  0.20 FS, floor 0.08 to beat stiction, quantized updates,
  5 s timeout-abort), then settle, pulse, measure.

- SECOND rig attempt: centering itself drove the wheel into the lock,
  both pulses "to the left". Root cause: the tuner ASSUMED the
  DirectInput direction frame (+1 = rightward force); on the owner's
  G PRO it is INVERTED, so "toward center" (pulses AND the new centering
  drive) pushed away from center. FIXED by measuring instead of
  assuming: a DirProbe phase now opens every run (hand-center prompt,
  one small raw pulse, read which way the wheel moved -> _nativeDir
  applied to every native drive); the ENGINE frame is decided on the
  first EngineForce trial (not after the phase) so engine-side centering
  cannot repeat the mistake; and a belt-guard aborts any centering that
  increases distance from center by 0.18 before the lock is ever near.
  Simulator-proven: an inverted-native-frame full run must detect the
  flip and still recover the firmware scales
  (InvertedNativeFrame_IsDetected_AndTheRunStillRecovers).

Second-round audit note: the re-audit fleet was killed by the session
usage limit before producing findings; it should be re-run (it now also
covers these field fixes) before the ship gate.

## First complete auto-tune run, 2026-09-01 23:22 (owner, G PRO)

The run finished end to end for the first time (~2 min, every phase 3/3,
three over-travel discards handled by the soft cut). Measured:

| Quantity | Value | Reading |
| --- | --- | --- |
| Stream vs native torque | engine pulses = 105% of native | the pass-through is within 5% of what DirectInput delivers; FfbScale 0.95 would match exactly (owner runs 1.0) |
| Damper gain | 0.25 -> **0.73** | the rendered damper was ~2.9x too weak |
| Spring gain | 1.0 -> **3.61** | the rendered spring was ~3.6x too weak (frequency ratio 1.9x, squared): the biggest mismatch found |
| Friction gain | 1.0 -> **0.59** | the rendered friction was ~1.7x too strong |
| Inertia | tracked the damper to 0.73 | no measurement phase; linked-until-unlinked rule |

Two defects the run exposed, both fixed the same night:

**1. The suspect flag was inverted (the important one).** The run
printed "the engine force direction is inverted on this rig: fix
direction and re-run before trusting these gains" and marked the results
suspect. That advice was backwards and following it would have created
an anti-damper. What EngineDirProbe actually measures is the sign of the
loop the renderer closes:

    cur = +k x velocity            (the damper, velTermSign +1)
    velocity_response = G x cur    (G is what the probe measures)

so the loop decays only when `G < 0`: a positive `cur` command must drive
the measured velocity NEGATIVE. That is exactly what the probe reported
("inverted"), and it is what the sign contract was authored for (positive
cur pulls toward lower steer; positive velocity is the other way). It
also matches the AC rig result from earlier the same day ("proper
damper", velTermSign +1). The old reading assumed the renderer had been
written for a non-inverting chain. Note the measurement needs no physical
direction convention at all: the probe and the renderer read velocity
from the SAME source, so the loop is self-consistent whatever "rightward"
means on a given wheel.

Corroborating evidence from the run itself: all three engine phases
produced physically sensible numbers. A wrongly-signed engine damper
would have pumped energy in (`rDE - rB > 1e-3` would have failed), and a
wrongly-signed spring would have diverged instead of oscillating
(`fSE > 0.05` would have failed). Both passed.

Fix: `RenderSignMeasured` / `RenderSignCorrect` on the tuner; suspect
now fires on `!RenderSignCorrect` (positive feedback), which is the real
fault and one `velTermSign` cannot fully rescue since springs do not
flip with it. The plugin now APPLIES the measured sign to `_damperSign`
instead of printing advice, and the done line says "render direction
CHECKS OUT" when the loop is negative feedback.

The simulator had been hiding this: it drove the engine command through
`+SChain` while applying engine effects as `-deff x vel` (always
opposing) - the two paths disagreed about which way the chain pointed.
`RunFullSimulation` now takes `engineSign`, defaulting to -1 (the rig's
case, and the only value consistent with its own effect model), and a
second test pins the `+1` case as flagged positive feedback.

**2. A measured gain the UI could not show.** Spring 3.61 was written
straight to settings while the bench slider stopped at 2, so the stored
value and the displayed value diverged and one slider touch would have
silently cut the spring to 2. One shared range now (`FxGainMax = 5`,
`ClampFxGain`) across the slider, `SetFxKindGain`, `SetCspDamperGain`
and the auto-tune result application.

Still open from this run: the sign conclusion is proven for the
DirectInput position source (AC sessions and the bench). The HID
steering-reader fallback in `TryUpdateWheelMotion` feeds the same
renderer from a different source whose sign has never been checked
against it; a game that lands on that path could render an anti-damper.
Worth a bench A/B before ship.

## Inertia, 2026-09-01 (rig: "engine inertia works, feels a little grainy"; native "just feels like damper")

The engine behavior the owner described - push the wheel and it holds
the speed it was pushed to - is CORRECT inertia. Added virtual mass
resists a change of speed in both directions: it fights the push, and
then it fights the wheel's own friction trying to slow it down, so it
coasts on. A damper stops dead on release; inertia does not. That is the
discriminating test, and it is the test the native side still needs.

Two fixes the report drove:

**Grain: acceleration was differenced per tick.** `Publish` computed
`(v - v_prev)/dt` at 1 kHz and low-passed the result. Velocity off a
quantized encoder moves in steps, so that derivative is a train of
impulses one step tall divided by 0.001 s. A one-pole averages them to
the right number but leaves ripple, and inertia is the one effect that
amplifies loop noise rather than suppressing it (it cancels damping
instead of adding it), so ripple the damper never notices arrives as
grain. Replaced with a washout differentiator:

    v_slow += (v - v_slow) x dt/(tau+dt);   a = (v - v_slow)/tau

Same derivative, no division by dt anywhere, band-limited to ~3 Hz
(`AccelTauSec` 0.05). Exact for steady acceleration, zero at constant
speed. `WheelMotionEstimatorTests` pins all three, including a coarse
0.05-quantum velocity where naive differencing would swing 0..50 against
a true value of 2.

**Inertia must never inherit the damper's gain.** The damper's gain is
force per unit VELOCITY; inertia's is force per unit ACCELERATION, and a
hand-turned wheel reaches an order of magnitude more range/s^2 than
range/s. The auto-tuner's damper result (0.73) carried across to inertia
saturates the term on every push, which is itself a grain source
(bang-bang between saturation and zero). The linked-until-unlinked rule
is removed from both the auto-tune Done branch and `SetCspDamperGain`;
inertia keeps its own value, default 0.05.

**Rig A/B result (owner, same night): the wheel does not coast.** Native
inertia turned up "just acts as damping"; ours keeps moving, but only at
gains high enough to fully cancel the wheel's own friction. At matched,
sane gains the two felt the same. So the divergence lives at the top of
the gain range, not in normal use.

Both behaviors now exist behind one switch, because which is right is
an open question, not a settled one:

- `InertiaCoasts = false` (default, `FfbConditionInertiaCoasts`): the
  half of the term that would push the wheel ALONG its travel is faded
  out, so inertia only ever resists. Matches the wheel.
- `InertiaCoasts = true`: the DirectInput reading, a lossless flywheel.

The fade is ramped over +-0.05 range/s rather than gated on
`sign(velocity)`, because a hard gate chatters exactly where the wheel
dithers around zero: the start of a push.

Owner's counter-point, recorded: if the wheel is out of spec, matching
its defect may be the wrong call, and the behavior could equally point
to a bug on our side. Checked: our native DirectInput inertia effect is
built the standard way (condition set, single axis, direction 0, full
saturation, no deadband) and the wheel enumerates the effect; and our
rendered inertia has the correct sign (positive acceleration gives a
positive term, which pulls toward lower steer and resists the
acceleration; the coast-on during deceleration is real stored energy,
not an inverted term). So the difference is genuine, not a defect in
either path.

Two out-of-spec explanations remain, and they are separable BY
MEASUREMENT rather than by feel:

1. the driver aliases the inertia condition onto its damper;
2. it renders true inertia but from a heavily lagged acceleration
   estimate, and a lagged acceleration term IS a damping term.

The discriminator: added mass drops the peak velocity a FIXED impulse
produces (`v_peak` proportional to `impulse/J_eff`) and stretches the
decay in time; added damping barely moves the peak and raises the decay
rate. Both metrics already exist in the auto-tuner's force and damper
phases, so a native-inertia probe is a small addition and would settle
it. Until then the default matches the wheel, since a lossless inertia
term is negative damping and costs loop stability margin.

## Measurement audit and rework, 2026-09-02

A 13-agent adversarial audit of auto-tune (31 findings raised, 20 refuted by
verifiers told to refute by default, 8 distinct defects confirmed). Its
headline: the damper was never noisy, it was BIASED, by three independent
paths, two opposite in sign and all three vanishing only where the two sides
already match, which is the one point at which no correction is needed.

All eight are fixed. What each one was:

| | Defect | Fix |
| --- | --- | --- |
| D1 | The wheel's own drag was left in both sides of the damper ratio, "because it cancels". An additive term common to a ratio's numerator and denominator does not cancel, it pulls the ratio toward 1: measured -25 % to -57 % in simulation | Subtract a measured wheel baseline per side |
| D2 | `peak / pulseAmp` assumed the response is proportional to the pulse. Coulomb friction subtracts a constant before anything accelerates, so it is affine, and the intercept is bigger on the weaker side: the force ratio was pushed away from 1 and the error landed 1:1 on the damper | `(peak/T + mu)/A`, exact |
| D3 | Every gain is valid only at the FfbScale it was measured at, and the result line advised changing FfbScale in the same sentence. FfbScale is preset-carried while the gains are global | Stamp the scale into the calibration, warn on mismatch, and say what changing it costs |
| D4 | The two damper plateaus pushed opposite ways, so Coulomb reversed sign between them and canceled only by luck. Starting direction was set by which side of center the wheel settled on: a coin flip, 25 % out when the two phases disagreed | Both levels one way, launched from the far side, direction alternating per trial |
| D5 | Friction differenced mean decelerations against a baseline measured with the stream UP while the native leg ran with it STOPPED, over per-trial speed bands | Friction in the parameter domain, same-side baseline |
| D6 | Rendered friction carries a velocity term (`FrictionDampK`) that a constant-deceleration fit charges to Coulomb | The integral fit puts it in `c`, where it belongs |
| D7 | The `_forceEq` clamp railed silently, leaving the engine phases un-equalized | Rails set `ResultsSuspect` and say so |
| D8 | `InertiaTerm` got a negated term beside an un-negated velocity, inverting which half it faded: on a flipped frame it kept the sustaining half and killed the resisting one | Pass the frame sign to both |

### What the rework found that the audit did not

**The wheel's inertia IS recoverable**, which D1's fix needed and the audit
judged impossible ("not recoverable from anything else the tuner measures").
The coast fit works in acceleration and the plateaus in force; they differ by
exactly `J`. A pulse of amplitude `A` held `T` seconds from rest reaches
`v_peak = (A - mu*J/K)*T/(J/K)`, so `J/K = A*T/(v_peak + mu*T)` from numbers
the force phase already produces. Multiplying a coast-fit rate by it lands it
in the plateaus' units. Verified in simulation: `J/K` recovers 0.0200 against
a true 0.0200.

**An unloaded baseline plateau cannot work.** It was the audit's suggested fix
for D1 and it is unimplementable: a wheel with no effect loaded has no
terminal speed to average. It accelerates until it hits the lock, which is
what it did. The derived baseline above replaces it and needs no extra phase.

**Friction cannot be measured by plateau either**, for the same reason in a
different guise: a constant friction torque provides nothing
velocity-proportional to balance a force against, so above the threshold the
wheel accelerates and below it does not move. Every simulated friction trial
read "0.00 then 1.57". Friction is measured in the parameter domain instead.

**Plateaus need a stiction kick.** Every plateau starts from rest, and a wheel
at rest must be broken loose before it slides. Stiction is not what
`F = mu + c*v` describes. Left in, it forced the low level up until the wheel
would start, which sent the high level out of the travel window, and the two
requirements could never be satisfied at once.

**Two force levels, not two multiples of one force.** The base has to clear
the friction threshold; the step sets how far apart the two speeds land. As
two multiples they are one knob, and raising it until the wheel moves at the
low level sends the high level's speed through the roof.

**The flatness tolerance was doing real damage.** At 10 % it passed a plateau
still 11 % short of terminal, and since the engine side settles four times
slower than the native side, that alone put the damper gain 20 % low. At 4 %
the simulator recovers 1.000 against a true 1.000.

### Trustworthiness, and the rule behind it

Ranked by class of estimator rather than by effect:

> counting / dimensionless / amplitude-free (spring)  >  integrative
> steady-state, dimensioned, needs an equalizer (damper)  >  windowed
> difference of means with a cross-condition baseline (the retired friction)

The spring holds 1-2 % on the rig because it is baseline-free,
amplitude-independent, and counts zero crossings rather than fitting a curve:
it inherits none of D1, D2 or D4, and a filter's phase lag shifts every
crossing equally and cancels. Any future metric is worth checking against the
same three questions: is it a ratio of like quantities measured within one
side, is it amplitude-independent, is it baseline-free.

## Feel parity reached, 2026-09-02 (owner, G PRO)

Saved: **damper 0.85, spring 4.00, friction 0.13, inertia 0.84**, filter 200 Hz,
direction normal. Owner: "that feels much better... graininess is basically
gone", without touching the condition filter. Two changes did it, and both came
from taking a feel report literally instead of explaining it away.

**Inertia renders as damping.** The owner reported twice that
native inertia "feels like a whole different effect, like it's just damping, no
inertia". The G PRO's firmware evidently renders the DirectInput inertia
condition against velocity. Matching the wheel is the entire mandate, so a
textbook flywheel is the wrong answer here however correct it is on paper:
`InertiaAsDamping` (settings `FfbConditionInertiaAsDamping`, default true).

It also removed the grain, and the two facts are the same fact. Acceleration is
a SECOND derivative of a quantized encoder, and inertia was the only effect
built on one, which is exactly why it was the grainiest of the four. Velocity is
one derivative cleaner. Note the gain changes meaning with the mode (per
velocity, not per acceleration): the owner retuned to 0.84, almost exactly the
damper's 0.85, which is what the shared shape predicts.

**The bench now neutralizes the output chain, as auto-tune already did.** The
owner noticed the engine spring "feels like it has a damper built in", and it
did. A delayed spring is not a late spring, it is a spring PLUS a damper:

    F = -k*x(t - tau) ~= -k*x + k*tau*x'

and that second term is viscous damping of size `k*tau`, growing with the
spring's own stiffness. The NATIVE side of a bench comparison is rendered inside
the wheel and touches none of our processing; the ENGINE side went through the
slew limiter, the smoothing pole and the transient ceiling, every one of which
delays force against position. So the A/B was judging the renderer for what the
chain did. `NeutralizeChainForBench` saves and restores those for the duration,
and teardown restores them too.

**Open, and it generalizes past the bench.** In a GAME our conditions still pass
through that chain while a firmware-rendered condition would not. At default
settings (smoothing 0 ms, spike taming off) the chain is transparent and there
is no difference; switch either on and conditions get damped in game while the
bench says otherwise, so the bench stops predicting in-game feel. Deciding
whether rendered conditions should bypass processing that exists to tame the
PASS-THROUGH force is a real design question and is not yet decided.

## Reference implementations (open DirectInput effect stacks)

Prior art that renders a downloaded DI/PID effect table against live axis
state, found 2026-09-01. None captures effects off another driver's USB
traffic (that part stays ours); all four are worth diffing the engine
against, the first two especially.

- Simucube 1 open firmware (loryx636/SimuCUBE-OpenSource-Firmware,
  cFFBDevice.cpp + FfbEffects.cpp): firmware-side stack on a 2500 Hz
  loop. At the audited commit: constant, sine/square/triangle/saws,
  spring, friction, damper implemented; ramp/inertia/custom stubs. Their
  DI coverage is narrower than ours: spring/damper ignore deadband, the
  negative coefficient and (for the damper) both saturations, and the
  damper reads only the positive coefficient (magnitude/127) times
  axisSpeedPerMs times a hand-picked 256. Heavy 1st-order biquad
  filtering on BOTH the velocity input (axisSpeedMsLPF 100 Hz) and the
  damping/friction force outputs (200 Hz), plus a separate endstop
  effect. THE idea worth taking: friction is not sign(velocity) but a
  STICK-SLIP LOCK POINT: torque proportional to the encoder's distance
  from a lock position that drags along once the distance exceeds
  magnitude/256 turns, then output-LPF'd. Bounded, no zero-crossing
  chatter, and it renders static friction, which our sign(vel) ramp
  cannot. Developer commentary (community.granitedevices.com/t/
  directinput-effects/9651): friction "goes unstable too easily",
  condition units depend on the configured rotation range (the exact
  problem DAMPCAL answers by measurement), "condition effects seem to be
  a bit of black magic".
- OpenFFBoard (Ultrawipf/OpenFFBoard, EffectsCalculator.cpp + Axis.cpp +
  HidFFB.cpp; MIT): the most complete open stack. calcConditionEffectForce
  is structurally OUR ConditionTerm (side selection by metric vs offset,
  deadband subtracted from the metric, coefficient /0x7fff, per-side
  saturation clamp), independently converged. Deltas worth borrowing:
  - Per-effect configurable biquad on each condition's OUTPUT (their
    anti-ring answer; tunable damper_f/damper_q, friction_f/friction_q),
    plus tunable biquad PROFILES on the speed/accel metrics themselves.
  - Friction: honors deadband/offset (ours ignores them) and ramps
    through zero with a SINUSOIDAL easing over a configurable
    percent-of-max-speed window, per-side coefficient by speed sign,
    saturation clip. Refines our linear 0.05 norm/s ramp; Simucube's
    lock-point model is still the stronger idea (static friction).
  - Duration-expired effects are PRUNED to inactive in the main loop:
    exactly the fix for our audit gap where expired effects pin
    AnyDamperPlaying true.
  - Envelope: computed per tick incl. the fade leg, follows the
    magnitude's sign, and is explicitly INVALID on infinite-duration
    effects (ours applies attack even when len=0; check vs firmware).
  - PID device-control semantics: Enable/Disable actuators, Stop All,
    Reset, PAUSE/CONTINUE as GLOBAL device state (the PID model). Our
    dialect's fn3 per-effect pause remains a separate question.
  - Phase-units caveat: their periodic phase is the raw DI/PID unit
    (0..35999 hundredths of degrees) because games speak PID to them;
    OUR wire is the Logitech/mainline dialect where phase is u16 and the
    Linux convention is fraction-of-cycle /65536 (our assumption). The
    two differ by a factor ~1.82; do not copy their phase math blindly.
  - Global gain applied /255 at the sum. They too multiply by arbitrary
    INTERNAL_SCALER_* constants plus user gains: even device makers pick
    the absolute scale by hand.
- mescon's Linux DD driver: full host-side pipeline at 1 kHz, spring
  ring fixed with proportional synthetic damping (wheel_spring_damping
  25% default).
- njz3/vJoyIOFeederWithFFB (BackForceFeeder): Windows host-side renderer
  for arcade cabinets, fed by a vJoy virtual wheel rather than a tap.

## Verification round, 2026-09-01 (post-phase-1.5 adversarial workflow)

A second fleet (4 skeptics + refuters; the plugin-wiring refuters died on
the session usage limit and were triaged by hand) confirmed and got fixed
same-day (suite 765 green, redeployed):

- CloneWith no longer copies pump-owned render state (torn
  LockPos/LockInit could deliver a full-saturation friction kick); a
  state flip re-seeds instead.
- Parametric ARMING GATE: rendering, sample stamps and index
  confirmation now require the index to be constant-confirmed OR the
  same (slot, type) downloaded 4x in a row. One parametric-shaped
  packet on a wrong index guess could previously lock the resolver,
  disarm the no-FFB watchdog and render garbage as torque (the FM8
  LED-feature class). Unarmed downloads still populate the table.
- Pause handling is now SUSPEND-AND-RETAIN: the snapshot drops
  synchronously, the table survives, and the first post-pause HID++
  command republishes it, so a game that downloads conditions once and
  then only sends SET_EFFECT_STATE keeps them across pauses (this was a
  confirmed permanent-loss bug). fn1 RESET_ALL still wipes. Residual: a
  game SWITCH with the tap kept alive could republish the old game's
  table until the new game's reset/downloads land; games reset effects
  on device acquisition, so exposure is small; noted, unfixed.
- The wheel-ack path ignores acks while a pause suspend is pending (a
  pre-pause ack must not resurrect effects mid-pause), and a constant
  download cancels a pending provisional ack (a constant's ack could
  relocate a parametric effect to the wrong slot).
- OutputLpf holds its state on same-timestamp evaluates instead of
  stomping it; fn8 gain logging is change-deduped.
- AddSynthesizedDamper's base sign flipped from -coef to +coef: it was
  sign-OPPOSED to the decoded HID++ damper while sharing the same
  velocity source and DAMPSIGN, so one of the two was an anti-damper.
  Both now share one convention; the rig flick test settles the frame.
- DAMPCAL is now authoritative: its active-zero short-circuits the
  provider tail (reshape/stationary spring repainted the calibration
  zero), and the no-game pause-release branch defers to a running
  wizard (it fired first and starved the friction/synth phases).

Deliberately not fixed: inertia sharing the damper gain (documented
placeholder until a calibration exists); the capture fingerprint still
confirming on constants only (diagnostic-only); the provisional slot
being a single cell (mitigated by ack-cancellation, self-heals on
re-download).

Licensing for anything beyond ideas: TF4ALL is GPL-2.0. OpenFFBoard and
BackForceFeeder are MIT (code may be adapted with attribution; the
formulas are trivial to reimplement anyway). The Simucube 1 "open"
firmware is NOT open-source: it is governed by the Granite Devices EULA
plus a CLA, so take facts and algorithms (the friction lock-point model
as a concept), never code. mescon is GPL-2.0; the standing practice
there stays facts-only with attribution. Simucube 2 / True Drive is
closed (their releases repo says source "will be published soon", it has
not been); nothing to read there today.

## Inertia rendered accurately, 2026-09-09 (owner call)

Owner: "I think we should make our inertia effect accurate. No more
inertia as damping." Both switches now default to the DirectInput
reading:

| setting | was | now |
| --- | --- | --- |
| `FfbConditionInertiaAsDamping` | true (velocity) | **false** (acceleration) |
| `FfbConditionInertiaCoasts` | false (resist only) | **true** (lossless flywheel) |

Neither switch is deleted. The finding that produced the old defaults is
real and still unexplained: the wheel's OWN native inertia reads as
damping by feel, reported twice, and nothing has yet
measured whether the firmware aliases the condition onto its damper or
renders true inertia off a heavily lagged acceleration estimate. The
bench keeps both as A/B controls, and "Inertia as damping" is now a
checkbox beside "Inertia coasts" instead of a settings-file-only field.

**The saved gain had to be discarded, not migrated.** The inertia gain
means force per unit VELOCITY under the old default and force per unit
ACCELERATION under the new one, and a hand-turned wheel reaches about an
order of magnitude more range/s^2 than range/s. The owner's saved 0.84
was tuned in the velocity domain; carried across it would saturate the
term on every push, which is itself a grain source. `FfbConditionInertia
SpecMigrated` flips both switches once and resets the gain to the
acceleration default (0.05), but ONLY when the stored mode was the
damping one: anyone already rendering acceleration has a gain that means
what it says. Latch is in BackupProjection's Excluded set with the other
migration markers, so it never travels to a second PC.

**What to watch on the rig.** `InertiaAsDamping` was what removed the
last of the graininess at feel parity, and acceleration is a second
derivative of a quantized encoder, so some grain may return. The washout
differentiator in `WheelMotionEstimator` (AccelTauSec 0.05) is the real
defense; `FfbConditionInertiaLpfHz` is the second lever and currently
follows the 200 Hz global. Coasting is also negative damping, so it
spends loop stability margin: if the wheel starts to ring or self-excite
with a game's inertia effect loaded, that is the first suspect and
turning coasting off is the test.

**Cross-check against mescon (read 2026-09-09,
`mainline/hidpp_dd_effect_math.h` + `hid-logitech-hidpp.c`, GPL-2.0).**
His `FF_INERTIA` feeds the shared condition formula a clamped
`wheel_accel * 4096`, with no coast/resist split and no per-class user
level, and his comment records the 4096 as a hand-picked default because
"INERTIA is rare in games". So our new default is his implementation,
and the condition formula itself now has a four-way independent
convergence (ours, his, OpenFFBoard, Simucube). Two deltas worth
recording:

- His acceleration is `new_vel - wheel_vel`, a raw per-tick difference of
  a velocity derived from a quantized encoder. That is exactly the
  impulse train we measured as the grain source on 2026-09-01 and
  replaced with the washout differentiator, so our inertia is the
  cleaner of the two whichever metric turns out to be right.
- His spring adds synthetic damping at 25% of the SPRING'S OWN
  coefficient, clamped to the spring's saturation. Damping proportional
  to stiffness holds the damping RATIO constant, which is the right
  invariant for ring, and we have no equivalent. With our measured
  spring gain at 4.00 this is worth taking.

**Settled for free by reading his source:** his periodic phase is a u16
where 0xFFFF is one full cycle, which is our wire assumption and not
OpenFFBoard's 0..35999. The phase-units question in the roadmap is
closed in our favor. Still open: our decode treats the wire deadband as
already halved while he halves the evdev value himself, and he cannot
settle it because on Linux he never downloads conditions to the wheel.
FXDUMP can, but the bench hardcodes `DeadBand = 0`, so testing it needs
a one-line change there first.

## Classic-protocol damper and friction (G923 PS/PC), 2026-09-16 (built, gated OFF)

**The C266 gets its DirectInput conditions from the classic slot protocol,
and they now reach the same engine.** The G923 PS/PC speaks no HID++, so
its games' damper, friction and inertia effects arrive as classic slot
downloads (types 0x0c hi-res damper, 0x02 low-res damper, 0x0e friction),
which the firmware ignores while the ep3 stream is live. The tap decodes
them into the engine's normalized condition form and feeds a new external
slot region of `HidppEffectEngine` (`ExternalSlotCount = 4`, above the
HID++ pool): `UpsertExternalCondition` on download / download-and-play /
refresh, `SetExternalPlaying` on PLAY / STOP, `RemoveExternal` when any
other type overwrites the slot, `ResetExternal` from `ResetClassicState`
(capture start and the deferred pause reset). Everything downstream
(`AnyHidppParametricPlaying`, `AnyHidppDamperPlayingNow`,
`TryEvaluateHidppEffects`, the active-shape gate, the motion source, the
CSP damper dedup, DICOND, DAMPSIGN, DAMPCAL, the output LPFs) is shared and
unchanged. Springs stay on the classic-spring path.

**K is linear, not 2^K.** Logitech's Force Feedback Protocol V1.6, Table 47:
"K1, K2 0...15 Linear slope with 0 being the weakest force and 15 being
the strongest. The actual coefficient factor is device dependent." The
Windows driver on a c266 encodes a 50 % DirectInput damper as K = 8
(new-lg4ff issue #86 capture), new-lg4ff encodes `(2 * abs(coeff)) >> 12`,
GIMX decodes `K / 15`. The decode is therefore `K / 15` (4-bit), `K / 255`
(8-bit friction) and Logitech's Table 27 for the 3-bit low-res form
(1/4 .. 4 times offset, normalized to K = 7). `K / 15` reads a K = 0
damper as zero where new-lg4ff's bucket midpoint `(K + 0.5) / 16` would
read 1/32; the two differ by at most 1/32 and an FFBInspector sweep on the
rig settles which the Windows driver means. The absolute factor is the
one unknown and it is exactly what DAMPCAL fits.

**The classic spring's slope goes linear under the same gate.** The hi-res
spring (0x0b) stays on `TryEvaluateClassicSprings`, not in the engine, but
while CLASSICCOND is on its per-side slope is `2 x K / 15` per unit of the
0..1 lock-to-lock axis (the factor 2 converts a DirectInput coefficient
defined per unit of the -1..1 axis into that domain), per the same
document ("scaled linearly from 0 to 15") and the Windows driver's damper
data point (WINCAP, new-lg4ff issue #86: a DirectInput 0.5 damper arrives
as K = 8), carried over to the spring by the document's statement that
0x0c uses the same 4-bit K scale as 0x0b; no spring data point with a
known DirectInput coefficient exists. With the gate off the shipped `1 << K`
guess is untouched, so nothing changes for FS25 users who never type the
code. Under the linear model a K = 0 spring renders zero and the FS25
corpus (mostly K = 0) renders very differently; that is one reason the
gate stays off until a spring calibration exists. The absolute factor is
unvalidated either way.

**The hi-res damper's CLIP byte is ignored.** The document scopes byte 6
to the Driving Force Pro ("only for PID_C298"), GIMX honors it only there,
the Windows driver writes 0x01 in it for a full-saturation damper, and
the wheel's owner-reported behavior is that it ignores the byte. Honoring
it would render every Windows damper at 1/255. Saturation is 1.0 and the
raw byte is in the log line. Friction's CLIP (byte 4) is real and honored.

**What the Windows driver sends on a C266.** DirectInput Damper, Friction
and Inertia all arrive as 0x0c dampers in slots 2, 3 and 4 (the document
lists friction for DFP/G25/DFGT/G27 only; new-lg4ff casts friction and
inertia to a damper on the G923 "as the Windows driver does"). Three
dampers can therefore play at once and must sum, so the external region
is exempt from the one-copy-per-type rule that guards missed HID++
destroys. The 0x0e branch exists and is tested but no Logitech Windows
title is expected to exercise it.

**Decisions recorded.** Pause wipes classic-fed conditions (the classic
spring precedent; the HID++ pool retains); a refresh into a stopped slot
stays ignored; the first placed condition arms the parametric gates with
no streak (the classic firewall already excludes the wrong-feature-index
class the streak guards); `ClassicConditionUpdatesCaptured` stands the
no-FFB watchdog down; `_lastSampleTicks` is not stamped. `ResetExternal`
republishes only when it removed something: it runs at every capture
start, and on a HID++ wheel an unconditional republish there would
render the retained pool during a pause instead of when the game drives
again. The setting (`ClassicConditionEmulationEnabled`, CLASSICCOND) is
default OFF and sits in BackupProjection's Excluded set as a testing gate
until a C266 rig session validates it. Render scope is the HID++
conditions' scope: pass-through, the pause release and the Mode B
slip-starved pass-through; the reshape paths replace the base as before.

**What to watch on the rig.** The sign frame from the DirectInput axis to
ep3 torque has never been checked on a C266: if the wheel speeds up when
turned, it is DAMPSIGN. The first-decode line shows the raw K next to the
derived coefficient; a coefficient sweep in FFBInspector with FXDUMP on
(the classic trace prints every slot command) confirms the driver's map.
DAMPCAL then fits the absolute factor. If the wheel ever renders a classic
damper natively under the stream, the game's damper doubles; DICOND is
the A/B that shows it.

## Inertia fixed at the source, 2026-09-17 (built, always on)

**Owner call: inertia renders to spec on every wheel, and the fix is not a
guard.** Logitech's own rendering of the effect is a damper on every wheel
measured or captured so far (the PS G923 by driver cast, the Xbox G923 by
mescon's fixed-push test, the G PRO by feel twice over). The owner reads
that as a defect in the wheel, not a model to copy. A stability guard was
built on 2026-09-16 (gain ceiling, own output pole, rest fade, behind a
bench checkbox) and REMOVED the next day, owner: "I don't see why a guard
is needed, what we need is a fixed inertia effect." He was right: a gain
cap limits how far the effect can be turned up, it does not make the
effect correct.

**The defect was never in the inertia effect.** The engine computes
coefficient times acceleration with deadband and saturation, which is what
DirectInput inertia is. What was wrong is the acceleration handed to it.
`WheelMotionEstimator` produced it with a single-pole filtered derivative,
`a = (v - v_slow)/tau`. Below the corner at `1/(2 pi tau)` = 3.2 Hz that is
a true derivative. Above it the response stops rising and holds FLAT at
`1/tau` = 20 for every frequency to the packet rate, so at 50 Hz it
reported velocity times twenty and called it acceleration. Rendered as
inertia that is a twenty times damper, and through the 8 to 11 ms loop
delay to the motor a damper that far out is negative damping, which
self-excites on a geared wheel whose rotor sits in the lash. The G923
buzzed at rest and louder with gain; the G PRO, a heavier lash-free plant,
held at the same numbers.

**The fix is the second pole.** A real differentiator's response keeps
rising, which no sampled system can do (unbounded noise gain on a
quantized encoder), so every practical one adds a pole and rolls off after
the corner. Ours had only one. Now:

```
a_raw = (v - v_slow)/tau ;  a += (a_raw - a) x dt/(tau+dt)
```

which is `s/(1+s tau)^2`, magnitude `w/(1+(w tau)^2)`. Measured on the
discrete implementation at 1 kHz, tau 0.05, against the single pole:

| Hz | one pole | two poles | cut | true derivative |
|---|---|---|---|---|
| 1 | 5.99 | 5.71 | none | 6.28 |
| 10 | 18.89 | 5.68 | 3.3x | 62.83 |
| 50 | 19.72 | 1.25 | 15.8x | 314.16 |
| 250 | 19.80 | 0.20 | 99x | 1570.80 |

Unchanged where hands live, sixteen times less in the buzz band, and still
falling above it where one pole never did. The cost is phase lag between
about 3 and 15 Hz, so a fast flick reads its peak a few milliseconds late.
No cap, no gate, no per-chassis fallback, nothing to turn on: the loop gain
that self-excited is gone at its source.

**Also unlocked, untried.** With one pole, shortening tau to give inertia
more bandwidth RAISED the flat gain (`1/tau`) and made the buzz worse, so
the corner could never be moved. With rolloff after the corner that trade
is gone, and a shorter tau is now a safe thing to try on the rig if inertia
wants to feel more alive in the 5 to 15 Hz band. Default stays 0.05, so the
G PRO tuning the owner signed off on is preserved exactly.

**Tests** (`WheelMotionEstimatorTests`): `AccelGainAt` drives a sinusoidal
velocity and reads back the peak acceleration, which measures the response
directly rather than by feel. It asserts the true derivative below the
corner, about 1.27 at 50 Hz with a hard ceiling of 3 (the single pole would
read 20), a still-falling response at 100 Hz, and a one-count encoder
dither at rest staying under 0.01 range/s^2.

**What still needs the rig.** That the G923 buzz is actually gone, from the
tester who reported it. That the G PRO feels unchanged at the owner's saved
inertia of 0.84, which is the regression check, since nothing below 3 Hz
moved. Both are feel tests; the frequency response itself is now pinned by
the suite.

## All fifteen classic force types, 2026-09-17 (built, behind CLASSICCOND)

**Owner call: coverage follows capability, not observed demand.** "If we
have proof that these wheels can accept an effect we should support it. The
PS wheel needs to have equal coverage to the other wheels. We need to be
able to replicate any game's force." The captures we had were all from
troubleshooting sessions, so they say nothing about what games do in
general, and two of the three were not even tests (Assetto Corsa was
driving Trueforce natively, Farming Simulator uses a spring model).

**All fifteen types in Table 23 now render.** Previously five did. Added:
constant (0x00, one level per slot, Table 24), low-res spring (0x01),
auto-centering spring (0x03) and its hi-res form (0x0d), sawtooth up and
down (0x04/0x05), trapezoid (0x06), rectangle (0x07), ramp (0x09) and
square wave (0x0a). Levels are offset binary per Table 22
(`ClassicLevel`), and every duration counts 2 ms main loops per Table 18
(`ClassicLoopMs`).

**One new engine waveform covers three shapes.** `TypeTrapezoid` (0x20,
above the wire range so a download can never collide) carries independent
rise, hold-high, fall and hold-low fractions. A rectangle is that shape
with zero ramps and a triangle is one with zero holds, so the classic
trapezoid and rectangle both render through it. `Wave` now takes the effect
rather than its type byte so the shape can read its own fractions.

**Two new external ingest methods**, `UpsertExternalPeriodic` and
`UpsertExternalRamp`, beside the condition one, sharing a `PlaceExternal`
tail that carries the slot's play state and start tick. A periodic with a
zero period is refused rather than divided by.

**Known inconsistency, deliberate.** (Superseded 2026-09-18, see the entry
below: the hi-res spring now goes through the engine too.) The hi-res
spring (0x0b) still renders
on the older classic-spring path with its own slope model, while the three
springs added here go through the engine. Routing 0x0b into the engine
would be the tidier end state but it is the path Farming Simulator depends
on and it is validated on hardware, so it was left alone rather than
risked. The two should be unified once a rig session can check the spring
slope.

**Rig plan unchanged**: FXDUMP alone first, which traces every classic slot
command without rendering anything, to learn what games actually send; then
CLASSICCOND on for feel; then the bench gain, which absorbs the absolute
scale (the document says the coefficient factor is device dependent, so
there is nothing to measure, only something to set).

## The hi-res spring reaches the engine, 2026-09-18 (built, behind CLASSICCOND)

**The bug this closes: the commonest classic condition on a PS wheel was
silent in mixed traffic.** The hi-res spring (0x0b) had exactly one
renderer, the classic-spring path, and that path only ever runs under
SPRING MODE, which is a replacement mode: it takes over the whole force and
arms only when the toggle is on, a spring is playing, and the tap has seen
NO constant force for two seconds, plus a one second dwell. So in any game
that streams road force and a centering spring together the spring was
dropped. Forza is exactly that case: 5,955 variable-force commands beside
276 hi-res springs in the owner's capture, which means its autocenter has
never been rendered during normal driving. Only Farming Simulator, which
sends nothing but springs, satisfies the gate.

The 2026-09-17 work made that worse rather than better: the three springs
added then (low-res 0x01, auto-center 0x03 and 0x0d) go through the
engine's external slots and sum on top of pass-through force like the
dampers, so the rare spring types rendered during normal driving and the
one games actually send did not. That entry called it a deliberate
inconsistency in the slope model. It was a coverage hole.

**0x0b now decodes into an engine external slot as well**, as a
`TypeSpring` condition, additively, beside its existing ClassicSpring
record. One decode of the wire bytes feeds both: `TryParseClassicCondition`
calls `ParseHiResSpring` and converts, so there is no second reading of the
layout to drift. Everything the Farming Simulator path depends on is
byte-identical: `ParseHiResSpring`, `TryEvaluateClassicSprings`,
`PublishSpringSnapshot`, `AnyClassicSpringPlaying` and the arming logic are
untouched. Same CLASSICCOND gate as the rest; with the gate off nothing
reaches the engine and the shipped path behaves exactly as before.

**The domain conversion.** The classic path works in `p = (steerNorm+1)/2`,
the 0..1 lock-to-lock axis the 11-bit D1/D2 fields are expressed in. The
engine works in `steerNorm`, -1..1. With `s = 2p - 1`:

```
band edges    s1 = 2*D1 - 1,  s2 = 2*D2 - 1
center        (s1 + s2)/2 = D1 + D2 - 1
half-width    (s2 - s1)/2 = D2 - D1
```

A p-distance `d` is a steerNorm distance `2d`, which is why the classic
path carries a factor 2 in its slope (`2 * K/15` per unit of p) and the
engine does not (`K/15` per unit of steerNorm): the two render the same
force. Slope is LINEAR, `K/15` per side, matching the hi-res auto-center
already in that switch; saturation is `CLIP/255`; each side's S bit is a
plain sign on that side's coefficient. Sign checked against the engine's
contract (positive force pulls toward lower steer; `ConditionTerm` applies
`RightCoeff` when `dev > deadband`) and against what the classic path does
today: right of the band with S2 clear it adds `+f`, so `RightCoeff` is
positive, and left of it with S1 clear it adds `-f`, which `ConditionTerm`
produces from a POSITIVE `LeftCoeff` times a negative deviation. Both sides
therefore carry the S bit unchanged. `HiResSpring_EngineCopy_MatchesTheClassicPath`
asserts the two agree within two counts at nine positions on an off-center
band, which is the real proof of the conversion.

**Also fixed on the way past**: the external condition upsert was passing a
hardcoded deadband and center of zero, so the low-res spring's decoded band
was discarded. It now passes `cond.Deadband, cond.Center`. Damper and
friction carry neither on the wire and are unaffected.

**The stand-down, and where it acts.** Under spring mode the plugin renders
the captured spring itself and returns it as the base force, so the engine
copy must go silent or the wheel gets one spring twice. The flag is
`UsbPcapFfbTap.ClassicSpringModeActive`, asserted from the plugin tick in
`UpdateSpringModeArming` on the same line as `SyntheticFfbActive`; it
proxies `HidppEffectEngine.SpringModeActive`, and `Evaluate` skips any
effect flagged `StandsDownForSpringMode` while it is set.

It acts at EVALUATION time, not at ingest, and that choice is the whole
design:

- Refusing the upsert in the parse loop would leave whatever the engine
  already held when the mode armed later, and spring mode can arm with no
  further download in sight (Forza sends its menu spring once and leaves it
  playing). The spring would be stuck ON.
- Removing the slot on the transition would need the parser to notice the
  transition, and the parser only runs when a packet arrives. On that same
  quiet bus the spring would be stuck on until the next download, and stuck
  OFF after a disarm.
- Reading the flag per evaluation costs one volatile read per effect per
  tick and cannot go stale in either direction: the first 1 kHz tick after
  an arm renders without it, the first tick after a disarm renders with it
  again. The slot table is never rewritten, so nothing about the game's
  effect is lost across the round trip.

A stood-down spring still counts as playing, because it IS on the wire: the
quiet probes, the LED gating and the no-FFB watchdog must keep seeing it.
The stand-down is scoped to the effect, not the slot, so a damper that
replaces the spring in the same slot renders immediately even while the
mode is armed.

**Which callers stand down, and which must not.** `MaybeReshapeFfb` already
REPLACES the target with `ComputeSpringModeForce` while spring mode is
armed, and `AddHidppDiEffects` runs before it, so on the main pump path a
double spring would be discarded by the substitution anyway. The stand-down
is there so that ordering does not have to hold by accident, which is the
kind of thing a later edit breaks.

The other caller is the opposite case. `ReleasedWheelForce` (the pause and
focus release) calls `AddHidppDiEffects` itself and RETURNS THAT VALUE, so
it never reaches the substitution and never renders a spring of its own:
standing the captured spring down there subtracts the only copy, and a
spring-mode game that pauses gets a limp wheel instead of the game's menu
centering, which is the failure that release was written to prevent. So the
stand-down is a per-call argument, not engine state alone:
`Evaluate` and `TryEvaluateHidppEffects` take
`allowSpringModeStandDown`, and `AddHidppDiEffects` passes `!conditionsOnly`
so the release path renders the captured spring while the main path stands
it down. (`SpringModeActive` earned a first draft where the skip was
unconditional; the release path made it wrong in exactly the place the copy
is most useful.)

**The disarm must not wipe the table.** `UpdateSpringModeArming`'s disarm
used to call `tap.ClearLastFfbTarget()`, which arms a full classic reset:
`ResetClassicState` then calls `HidppEffectEngine.ResetExternal` and the
engine's copy of the spring is gone. The HID++ path has a deferred
republish for exactly this (`_hidppResetRequested` -> `RepublishFromTable`),
but it is honored only in `HandleHidppFfbFunction` and a C266 never sends
one, so there was no recovery on the classic side. The sequence is the
target scenario itself: a Forza menu arms spring mode, driving resumes, the
first scalar disarms it, and the autocenter the change exists to render is
silent until Forza re-downloads the spring, which it does not (it sends the
menu spring once and leaves it playing).

What the disarm actually needs is the stale SCALAR gone, which is what its
own comment says: spring mode ignored everything the tap captured while it
was armed, and on Farming Simulator a misdecoded heartbeat stayed "fresh"
for ten seconds and drove the wheel to lock. So it now calls
`UsbPcapFfbTap.ClearCapturedForceKeepingEffects()`, which clears `_packed`
from the plugin thread and arms `_classicScalarResetRequested`, a deferred
reset that clears `_classicSlotForce`, `_classicSlotDecoded` and the
published-target latch and touches nothing else. A bare PLAY after it still
finds an undecoded slot and publishes nothing (the issue #13 guard), while
the springs, the conditions and the play flags the game genuinely has
loaded stay where they are. `ClearLastFfbTarget` keeps its full reset for
the callers that want a wipe: the pause gate, the game change and capture
start.

**The flag follows `_forceMode`, not the tick.** `ClassicSpringModeActive`
was asserted once at the top of `UpdateSpringModeArming`, before the same
call decides to arm or disarm, so it lagged a full DataUpdate pass on both
edges: about 16 ms rendering the spring beside our own copy of it after an
arm, and 16 ms of no spring at all after a disarm. It is now asserted from
`AssertSpringModeStandDown()`, called from the per-pass line (the re-attach
backstop), from both `_forceMode` writes inside that method, and from
`ApplyModeBFromSettings`, where the neighboring `FfbBypassTapCorrections`
line was hardened against the same window for the same reason.

**Tests** (`ClassicConditionEmulationTests`, twenty new): the gate on
lands an engine spring with the right center, deadband, coefficients and
saturation; the engine copy matches the classic path count for count on an
off-center band; the band is silent between its edges and off-center where
the game put it; CLIP saturates; an S bit inverts that side; the gate off
changes nothing; a bare download waits for its PLAY; a STOP silences both
renderers and a PLAY restores them; a variable force and a damper each
evict the spring; the stand-down silences the engine copy with no packet in
between and a disarm restores it; it leaves a damper in another slot alone;
it applies to a spring downloaded while already armed; and it does not
follow the slot onto a damper that replaces the spring.
`Spring_ReplacesDamper_DamperGone` was updated: the slot is no longer empty
after a spring lands, so what it now asserts is that the DAMPER is gone
(velocity renders nothing) rather than that nothing is playing.

Six more cover the review's findings: the stand-down survives a classic
STOP/PLAY round trip (the flag rides on the effect, and `CloneWith` is a
`MemberwiseClone`, so a future explicit field copy would drop it silently);
it does not reach the release path, which still renders the captured spring
while the mode is armed; a disarm keeps the engine copy across a bare PLAY
and the road force that follows, on the live-capture harness; the low-res
spring's band lands where the wire bytes put it, probed at two positions an
uncentered band would get backwards; the hi-res auto-center centers on zero
with its two-count band; and the first-decode log line carries the band for
the spring family and not for the damper.
`SpringMode_StandDown_LeavesTheOtherConditionsAlone` was strengthened: it
probed at position zero, where the centered spring is worth eight counts
and a leaked term could not move the result out of a 1000-count band, so it
now probes where the spring is worth 3495 and compares the armed and
disarmed sums.

**What still needs a rig.** That a Forza autocenter now renders at a
sensible strength beside road force on a C266, which is the whole point of
the change, and that it does not fight the wheel at the lock. That the
linear `K/15` reading is right for the spring specifically: it is the
document's reading and it matches the damper's Windows data point, but no
spring capture with a known DirectInput coefficient exists, so the absolute
scale is still set rather than measured (the bench spring gain absorbs it).
That Farming Simulator is unchanged, which is the regression check: the
stand-down should make the engine copy invisible there, and the suite
covers the mechanism but not the feel. That a pause in a spring-mode game
now centers on the game's own spring rather than going limp, which is the
release-path scoping above and has only ever been reasoned about. And that
the low-res spring's band, live on the wire only since the upsert started
carrying the decoded pair, lands where a game actually puts it: the tests
prove the arithmetic against the wire bytes, not against a wheel.
