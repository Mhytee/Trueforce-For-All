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
| Counter-steer torque from front slip direction | dir = front slip angle (sign-verified), shaped by the trail spring (MBTRAIL) | Good. **GAP #2 closed differently than planned**: the trail spring widens the dir window during a slide so the wheel settles into a countersteer. The additive BCS counter term that originally addressed this was RETIRED 2026-08-02. See below |
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

**Layer 10 (GAP #2), superseded 2026-08-02.** The original plan was
"slide-depth counter growth": scale the BCS term by min(|front slip|/0.15, 1)
so a shallow drift asks politely and a big one yanks toward opposite lock. It
shipped in beta 0.2.0-0.2.4 and was retired, because the trail spring closed
the same gap by a better route and the two work against each other.

Why they conflict. Both terms in `ModeBComposer.Compose` carry the same `dir`,
so the composed force factors as `dir x (sat + counter)` and the spring rate
the hands feel is that bracket divided by the direction-ramp window. The trail
spring stabilises a slide by WIDENING that window (`AdaptiveDirWindow`),
dividing the rate down. The counter term adds straight back into the numerator,
gated on the same rear-breakaway excess that opened the window. Worse, the SAT
term is designed to collapse toward `DropFloor` in a slide, so the counter's
SHARE of the rate peaks exactly where the trail spring wants to be softest: on
the beta recipe (SatGain 0.50, DropFloor 0.50, BCS 0.50) the counter term
carried more rate than the SAT term it was meant to supplement. Growth aimed
that stiffness at the deepest part of the slide. Owner's on-wheel read, which
started this: "with countersteer at 0 the wheel feels more stable, especially
in slides."

What was tried, and the verdict. Four shapings were built and driven back to
back: the original slide-depth growth; `CounterHandoff`, an inverse fade over
front slip angle; `CounterCrossfade`, the same fade timed on the rear-breakaway
gate instead so the two were complementary by construction; and a `|dir|` center
gate after the term was caught oscillating the wheel at center under power (it
is LINEAR in dir, so unlike the lateral-demand SAT path its loop gain at dead
center is nonzero, and wheelspin opens the gate while the wheel is straight).
Every session returned the same answer: the wheel is smoother the less
countersteer it gets. It survived only around 0.1 to 0.2 of gain, where it was
barely perceptible, while carrying a slider, three toggles, four access codes
and four helper curves. RETIRED 2026-08-02 by owner decision. The trail spring
covers the gap.

What survives, and is the real lesson: the rear-excess GATE those terms shared
with the trail spring and `SlideDuck`. Logging it (551 half-second buckets above
30 km/h) showed p50 0.39, p90 7.45, p99 29.7, max 62.1, two orders of magnitude,
against an `OverCap` of 1.0 that had been assumed for months. 38.7% of buckets
were fully pegged, so the trail spring and the centering ease were running as
switches rather than the progressive ramps they were designed as. That is now
`SlideGate01`, a soft saturation `x/(x+k)` with `k = SlideHalfPoint = 2.0` from
the measured distribution, tunable via BOVERCAP. Nothing clips. Still open: the
excess is not speed-normalised, and crawl-speed wheelspin drives it to 17+.

## Motor reality check (G PRO on the rig, G923 at the desk)

- Direct-drive G PRO: ~11 N·m ceiling, flat response through our whole
  texture band. The 2.2 N·m belt G923 low-passes hard above ~150 Hz —
  front-scrub content (150–250 Hz) will read muted there; fine on the PRO.
- Our force channel is effectively torque command at 1 kHz; the motor and
  rim inertia are the only filters. That's why smoothness bugs (steps,
  slams) are feelable at all — nothing mechanical hides them.
- **SWEEP protocol** (access code, see below): play the built-in frequency
  sweep and note where the rim feels strong / buzzy / dead. That maps the
  rig's real usable band so effect frequencies can sit where the hardware
  actually renders, instead of where the math says. SWEEP1..SWEEP6 play one
  octave each (~5 s) for band-by-band judging; plain SWEEP is the full run.

## Measured: Andrew's G923, 2026-07-06 (SWEEP1-6 at 0.6 amp / 0.5 master)

| Band | Range | Verdict |
|------|-------|---------|
| 1 | 8–16 Hz | aggressive, jerky — rim rocking, reads as FFB shoves |
| 2 | 16–32 Hz | violent but consistent — "off-road at high speed" |
| 3 | 32–63 Hz | very violent, starting to buzz — "fast kerb" |
| 4 | 63–125 Hz | PEAK — "thought the wheel was gonna explode" |
| 5 | 125–250 Hz | good high end, texture without aggression |
| 6 | 250–400 Hz | whiny, barely noticeable — above the motor's ceiling |

An electronic whine rides the low bands at this amplitude (motor-driver PWM
leaking through, not a fault). Placement rules that follow:
- The prior assumption above ("low-passes hard above ~150 Hz") was close:
  usable texture extends to ~250 Hz, dead past that. Keep all effect
  content under 250 Hz; nothing goes above.
- 63–125 Hz is the danger band: full-scale content there is genuinely
  violent. Big cues (collision, kerb strikes, lockup pulses) can afford to
  sit here at LOW amplitude; sustained textures should not.
- Kerb thump at 30 Hz lives in a strong band — its weak first test was
  amplitude/duration, not placement (already mitigated: default gain 1.6).
- Front-scrub 150–250 Hz (band 5) is exactly right for always-on texture:
  present but never overwhelming.
