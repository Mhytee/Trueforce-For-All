# Real-world steering feel vs. the Mode B model

What a real steering wheel actually transmits, how our synthesis stacks up
term by term, and the gaps ranked by how much feel they're worth. Written to
guide the next test layers; read alongside `haptic-engine-plan.md`.

## Where real steering torque comes from

Rim torque is (almost entirely) front lateral force × total trail:

```
T_steer ≈ F_lat_front × (t_pneumatic + t_mechanical) / steering_ratio
```

- **Pneumatic trail** (`t_p`): the tire's contact patch builds lateral force
  toward its REAR half, so the force acts behind the geometric center. Key
  property: `t_p` SHRINKS as slip angle grows — roughly linearly, hitting
  ~zero right around peak grip, slightly negative past it.
- **Mechanical (caster) trail** (`t_m`): fixed by suspension geometry.
  Constant with slip; its torque contribution simply follows `F_lat`.

The product produces the signature everyone's hands know:

1. **Torque peaks BEFORE grip peaks** — at roughly 60–80% of the slip angle
   of maximum lateral force, because `t_p` is already collapsing while
   `F_lat` is still climbing. The "the wheel stopped getting heavier" moment
   IS the approach warning, and it happens while the tire still has margin.
2. **Past the limit, torque falls to a caster floor, not zero** — `t_p` is
   gone but `t_m · F_lat` remains (the tire still makes force in a slide,
   just less). Street/race cars land at roughly 30–60% of peak torque in a
   full understeer plow.
3. **In oversteer the wheel counter-steers itself.** As the car yaws, the
   front slip angle swings toward the outside of the slide; the still-
   gripping front axle's SAT torques the rim toward opposite lock, hard.
   A driver "catching it with the wheel loose" is literally the front tire
   doing the catch. The rear never touches the rim directly — the cue comes
   through front slip direction + magnitude, plus chassis yaw.

## Scorecard: our model against that

| Real behavior | Our term | Verdict |
|---|---|---|
| Torque rises with front lateral force | SatForceModel rise × lat-g cornering weight (BLAT) | Good — the lat-g multiplier is a direct `F_lat` proxy |
| Torque peak before grip peak | Peak pinned AT u = 1.0 | **GAP #1** — our wheel is still gaining weight where a real one has already plateaued; the pre-limit warning window is compressed |
| Caster floor in a slide | DropFloor (0.20 default, slider) | Good — physically the `t_m` remainder; Andrew's 0.20 sits at the sporty end of the real 0.3–0.6 range, which suits a feel-forward setup |
| Counter-steer torque from front slip direction | dir = sign of the front slip signal, saturating at 3% of peak slip | **GAP #2 STILL OPEN, and now understood.** Both attempts at it (the BCS counter term and the trail spring) were retired 2026-08-02. The blocker is a units bug, not a tuning one. See below |
| Load sensitivity (sub-proportional) | LoadEffect 0.5 | Good hedge — real tires gain grip sub-linearly with load, ~0.5 effective is the right ballpark |
| Speed-proportional trail buildup | SpeedFullKmh ramp | Good |
| Understeer stick-slip chatter (~10–20 Hz) | Layer 1 judder at 14 Hz past the limit | Good, matches the real mechanism |
| One-wheel bump kick through the rack | — nothing — | **GAP #3**, see below |
| Yaw-rate damping (tire relaxation resists fast rotation) | Wheel-velocity damper (BDAMP) approximates it | Acceptable |

## The gaps, as future test layers

**Layer 8 — early torque peak (GAP #1).** Reshape the rise so torque
plateaus around u ≈ 0.7–0.8 and only *drops* after u = 1.0. Cheap version:
piecewise rise with zero slope from PeakTorqueU to 1.0. This widens the
"wheel stopped loading = you're approaching the edge" window, the single
most information-dense cue a real wheel gives. Interacts with BPEAK/BRISE,
so it must be its own layer — it will change his dialed feel.

**Layer 9 — road kick (GAP #3).** Real racks kick the rim when ONE front
wheel hits a bump: asymmetric vertical load × trail = signed torque blip.
We already carry per-corner suspension travel at 60 Hz → derive
`d/dt(suspFL − suspFR)`, band-limit it, inject as a signed transient into
the force channel (not the texture channel). This is the "alive road"
feeling sim wheels famously lack; FM8's suspension channel is clean enough.

**Layer 10 (GAP #2): both attempts retired 2026-08-02, gap still open.**

Two different mechanisms were built to make the steering settle into a
countersteer during a slide. Neither survived, and the reasons are different
and both worth keeping.

**Attempt 1, the BCS counter term** (shipped beta 0.2.0-0.2.4). An additive
torque gated on the rear's utilization excess over the front. Four shapings
were driven back to back: slide-depth growth; `CounterHandoff`, an inverse fade
over front slip; `CounterCrossfade`, the same fade timed on the rear-breakaway
gate so the two were complementary by construction; and a `|dir|` center gate
added after the term was caught ringing the wheel at center under power (it is
LINEAR in dir, so unlike the lateral-demand SAT path its loop gain at dead
center is nonzero, and wheelspin opens the gate while the wheel is straight).
Every session gave the same verdict: the wheel is smoother the less it gets. It
survived only near 0.1 of gain, barely perceptible, while carrying a slider,
three toggles, four access codes and four helper curves.

**Attempt 2, the trail spring** (`AdaptiveDirWindow`, new in 0.2.5, never
shipped). It widened the direction window during a slide so `dir` would stay
proportional rather than saturating. Logging proved it **never once acted**: in
50 of 50 samples across two sessions the front slip was outside the window, so
`dir` stayed pinned at 1 through every slide, at 6 deg, at 15, and at 45.

**The root cause, which is the real finding.** `TelemetryFrame.FrontSlipAngleRad`
is misnamed and its doc is wrong. It carries Forza's `TireSlipAngle` straight
through with no conversion, and that value is NOT radians: it is normalised slip
where about 1.0 means the tire is at its peak slip angle, exactly like its
sibling `TireCombinedSlip` which this codebase already reads that way (see
`ModeBPeakUtil`). Measured range was 0.15 to 1.05 in ordinary drifting and 7.3
in a spin, which is impossible as radians (420 deg) and exactly right as a
fraction of peak.

So the shipped 0.03 direction window is not 1.7 deg, it is **3% of peak slip**,
and `dir` has been effectively `sign(slip)` in anything but a straight line
since the model was written. Any feature that tries to use the slip MAGNITUDE
is dead on arrival until the units are fixed, which is precisely why both
attempts at GAP #2 failed while everything else works: force SIZE comes from
`u` (combined slip), whose units are read correctly.

**To reopen this gap**, in order: rename the field and fix its doc; express the
direction window in slip units instead of the fictional radians/degrees;
re-derive the base window (0.03 of peak slip is arbitrary, it just happens to
give sign-like behaviour); only then rebuild a proportional-in-slide term and
validate it. The rear-excess gate (`SlideGate01`) is sound and stays, since it
reads combined slip; it still drives `SlideDuck`.
