# Haptic Engine Transformation Plan

Turning TF4ALL into a game-agnostic dual-channel haptic engine: canonical telemetry in,
three-way channel separation out (main FFB torque / TrueForce texture / shakers-later).
This is the working plan for the `haptic-engine` branch. Local build first; upstream later
if it earns it.

Decisions locked up front: **FM8 is the first-class adapter target**, **replay harness comes
first** (every phase proves parity before it ships to the wheel), **Mode B full-synthesis FFB
is prioritized early** — "40mph feels like 40" is the point.

## The shape of the thing

```
game UDP/MMF → adapter (dumb translator) → frame queue
    → EngineLoop thread (1kHz, paced by device ring back-pressure)
        every 2nd tick (500Hz): jitter buffer → resampler (interp / alpha-beta) →
            CtmComposer (calibration → grip utilization, axle rollups, caps flags) →
            EventDeriver (frame diff → edges) → effects Tick()
        every tick (1kHz): FFB synth hook → duck envelopes → mixer.Render(4) → device
    → TrueforceDevice StreamTick (1kHz, unchanged): cur = FFB latch, window = 4kHz texture
```

Key architectural calls (and why):

1. **`TelemetryFrame` grows in place — it IS the CTM.** No parallel frame type, no
   converters. New: `TireQuad` per-tire fields, `SignalGroups` Native/Derived caps bitset,
   axle rollups (`FrontGrip01`/`RearGrip01`/`GripBalance`), `FrameEvents`. Legacy scalar
   fields stay through the migration, populated FROM the quads, then become computed
   properties in the final cleanup.
2. **No third clock.** The existing `ProducerLoop` is already a de-facto 1kHz engine thread
   (paced by `PushFloats` ring back-pressure). It gets promoted into the Engine assembly and
   picks up telemetry processing at 500Hz decimation. Effects tick and render on the SAME
   thread — kills the whole eventual-consistency threading hazard class.
3. **Bands enforced by frequency-aware ducking, not filter buses.** Effects are oscillators;
   we know their instantaneous frequency exactly. `duck = f(priority class, activity, band
   overlap)`. GripState > Transient > Ambience, with GripState→Ambience depth 0.85 (slip
   ducks engine pulse HARD). SmoothDuck attack/release infra kept (10ms/150ms defaults).
4. **Mode B synthesizes in utilization space** via the on-wheel-validated smoothstep
   peak-and-drop geometry (SlipSaturationShaper's shape, promoted to a full SAT model:
   SatGain/FullU/DropFloor/RiseGamma + LoadEffect + speed trail ramp). Road/kerb texture
   stays in the TrueForce window — the Horizon data proved texture in the force channel is
   exactly what makes Forza FFB jittery.
5. **All gain lives in the SoftKneeCompressor** (out = G·f below T, hyperbolic knee above,
   ceiling strictly < 1). This structurally bounds high-amplitude loop gain — the 3.3Hz limit
   cycle can't come back through the front door. FfbScale → 1.0, GAIN access code remaps.
6. **Calibration maps raw slip → utilization in `CtmComposer`** (not in adapters — central,
   uniform, replay-testable; deviation from spec §5.1, deliberate). `PiecewiseCurve` monotone
   linear anchors, per-game default + per-car `.tfgrip.json` overrides, CAL/CALMARK/CALFIT
   capture-and-fit flow.

## FM8 packet facts (verified against geeooff/forza-data-web, 2026-07-03)

- Sled block (0–231) is **byte-identical across FM8/FH5/FH6** and carries all the per-wheel
  physics: slipRatio@84, wheelRotSpeed@100, rumbleStrip@116, puddle@132, surfaceRumble@148,
  slipAngle@164, combinedSlip@180, suspTravelM@196, carOrdinal@212, cylinders@228.
- Length dispatch: 232=Sled, 311=FM7 dash, **324=Horizon (12-byte extras THEN dash@244)**,
  **331=FM8 (dash@232 directly, tire wear + track ordinal @311–330)**.
- FM8 dash: speed@244, throttle@303, brake@304, gear@307, steer@308 (sbyte).
- FM8 is fixed 60Hz → the resampler's alpha-beta predictor is what makes it feel 1kHz.
- Runtime plausibility checker validates offsets on the first packets of any new length
  before the force path consumes anything (rpm/speed/slip/gear range asserts, ±12-shift
  candidate fallback, loud logging).

## Phases

| # | What | Proof |
|---|------|-------|
| 0a | Golden capture v2 (per-tire CSV) + TireQuads on frame + minimal FM8 331-byte parse | Unit tests; additive, wheel untouched |
| 0b | Extract `TrueforceForAll.Engine` asm (move-only; one logger injection) | Build green, drive parity |
| 0c | Extract FrameEnricher + EngineLoop homes behind forwarders; ITimeSource injection | Build green, drive parity |
| 0d | Replay harness: CsvFrameReader, ReplayFrameSource (virtual + real-time-to-wheel), HeadlessRig, Goertzel band asserts, RMS parity | Harness runs synthetic fixtures |
| — | **Rig session: record golden fixtures** (FM8 skidpad/lockup/launch/hot-lap/crash/pause + FH + AC + fallback) | Goldens frozen |
| 1 | EngineLoop becomes the clock: frame queue, jitter buffer, resampler, 500Hz ticking, EMA→time-constant conversion | Parity goldens in tolerance; latency ≤20ms; replay-to-wheel A/B |
| 2 | CTM completion: caps stamping, CtmComposer rollups, EventDeriver, effects consume Events; FM8 load01 + settle/forward-through finish | CTM invariants; event-count parity |
| 3 | Chain refactor: ConditionGameForce (A-only) + ApplySharedPost; SoftKneeCompressor; consolidation (FfbScale→1, transient cap off w/ comp); MODEA | FfbTrace byte-diff parity at COMPLIN; compressor unit suite |
| 4 | **Mode B v1**: SatForceModel @1kHz w/ 25ms input EMAs, MODEB/AB/BLEND, 50ms crossfades, stall ramp, oscillation guardrails | Model tests; no-step replay assert; on-wheel A/B protocol |
| 5 | Calibration: PiecewiseCurve/mapper/store, FM8 default anchors, CAL flow; Mode B → utilization space | Fitter recovers synthetic breakaway within 5% |
| 6 | Effect contract (descriptors/ladders/CurrentBands) + BandArbiter replaces UpdateDucking + predictive slip per-axle voices (front scrub 150–250Hz w/ stick-slip judder past limit; rear pulse train 40–70Hz; balance-gated salience) | Band occupancy + duck envelope asserts; on-wheel provocation test |
| 7 | FM8 shipped defaults, legacy field cleanup, docs | Full game-matrix drive |

## Continuity guarantees (the daily-driver clause)

- `FORZAFFB` + all existing access codes keep working every phase (Phase 4 aliases FORZAFFB
  → profile+MODEA, same felt behavior).
- Every phase ends buildable AND wheel-drivable; parity phases (0b/0c/1/3) gate on golden
  tolerance or byte-diff before deploy.
- `AB` is the one-code A/B toggle while driving; mode switches crossfade 50ms, never step.
- Deploy stays `deploy_ffb.bat` (solution build → x64 path — remember the stale-DLL gotcha).

## Stability guardrails (3.3Hz limit cycle, never again)

Bounded-slope compressor holds the gain; loop-gain budget logged on every param change;
CENTER-without-DAMP warns (PD pairing); the damper stays the ONLY velocity-coupled term;
opt-in oscillation watchdog (2–6Hz zero-crossing detector auto-ducks G 20%); gates have
hysteresis; stalls ramp over 100ms, never cut.

## Deferred

Bass shakers (own thing later — forward-through + SimHub props keep existing setups alive),
FH6 adapter polish (works via shared sled; dash offsets already handled), Tier-3
yaw-divergence derivation, native shaker WASAPI output, standalone daemon.
