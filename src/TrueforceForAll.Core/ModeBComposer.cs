// Mode B force composition — the per-axle layer on top of SatForceModel
// (phase 4 of docs/haptic-engine-plan.md). SatForceModel stays a pure
// front-axle SAT curve; this composes the two feel terms the first wheel
// tests showed it lacks:
//
//  * Cornering weight: real steering torque is dominated by front lateral
//    force, so weight must build with lateral g — a 0.9 g sweeper is heavy,
//    a 0.3 g hairpin is light, even at identical grip utilization. Applied
//    as a multiplier on the SAT term so the peak-and-drop shape (lightness
//    past the limit) is preserved, just scaled.
//
//  * Countersteer force: with front-only utilization, rear breakaway is
//    invisible — the front keeps grip, u stays low, the wheel stays limp
//    exactly when it should pull hard toward the counter-steer. The rear's
//    utilization EXCESS over the front gates an additive torque in the
//    front-slip direction (the sign-verified counter-steer direction).
//    Gating on the excess (not raw rear slip) keeps straight-line
//    wheelspin quiet: a burnout spikes rear slip but dir ~ 0 and the
//    trail ramp is low.
//
// Pure math, no state — smoothing of every input lives at the integration
// layer ("smooth the inputs, not the force"). Returns a signed normalized
// force in [-1, 1].

using System;

namespace TrueforceForAll.Core
{
    public static class ModeBComposer
    {
        /// <summary>Lateral g beyond which the cornering-weight multiplier
        /// stops growing (road cars rarely exceed ~1.5 g; caps runaway on
        /// kerb spikes).</summary>
        public const double LatGCap = 1.5;

        /// <summary>Rear-over-front utilization excess treated as a full
        /// slide; excess beyond this adds no more counter torque.</summary>
        public const double OverCap = 1.0;

        /// <summary>Center-stable direction blend (sixth wheel test: "at
        /// wheel center sometimes the motor buzzes out... let go at center
        /// and it starts to oscillate"). The linear dir ramp gave the model
        /// a stiff spring constant at dead center — force/slip slope was
        /// maximal exactly where the slip signal is pure noise, so noise
        /// buzzed the motor and the hands-off loop (force moves wheel →
        /// wheel makes slip → opposing force) had a spring to ring against.
        ///
        /// Shape: rational softening f(a) = (1+k)·a²/(a+k), sign-symmetric,
        /// f(1) = 1. Slope is exactly zero at center (noise lands on a flat
        /// spot) and stays near 1 through the body of the band — unlike a
        /// smoothstep, whose 1.5× mid-band wall made small deliberate
        /// crossings (±3° rocking, seventh wheel test) feel jumpy.
        /// <paramref name="softness"/> = k: 0 is EXACTLY the legacy linear
        /// ramp; ~0.1 gives a subtle flat spot; larger = wider dead feel.</summary>
        public static double CenterSoftDir(double raw, double softness = 0.12)
        {
            double r = Math.Min(Math.Max(raw, -1.0), 1.0);
            double a = Math.Abs(r);
            double k = Math.Min(Math.Max(softness, 0.0), 0.5);
            if (a <= 0.0) return 0.0;
            return Math.Sign(r) * (1.0 + k) * a * a / (a + k);
        }

        /// <summary>Low-speed validity gate (twelfth wheel test: stuck
        /// off-road at 0-14 km/h, force flipping sign 8-24x/s at up to a
        /// third of full scale). Slip angle is mathematically undefined as
        /// speed approaches zero and wheelspin-at-crawl pegs the grip metric
        /// while its sign thrashes — ALL slip-derived synthesis is garbage
        /// down there. The SAT model's linear trail ramp still passes ~17%
        /// of full force at 10 km/h, which is plenty to buzz the rim.
        ///
        /// Smoothstep: exactly 0 at/below <paramref name="deadBelowKmh"/>
        /// (slip undefined → force silent), exactly 1 at/above
        /// <paramref name="fullAtKmh"/> (everything validated on the wheel
        /// is untouched), C1-continuous between.</summary>
        public static double LowSpeedGate(double speedKmh,
                                          double deadBelowKmh = 6.0,
                                          double fullAtKmh = 20.0)
        {
            double lo = Math.Max(0.0, deadBelowKmh);
            double hi = Math.Max(lo + 0.1, fullAtKmh);
            double t = (speedKmh - lo) / (hi - lo);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>Utilization noise floor: readings below
        /// <paramref name="floor"/> are the grip signal's noise fuzz (parked,
        /// dead straight) and must produce exactly zero force instead of
        /// buzzing the motor at center. Rescaled so u = 1 still means the
        /// limit — the curve above the floor keeps its shape.</summary>
        public static double UtilizationFloor(double u, double floor = 0.05)
        {
            double f = Math.Min(Math.Max(floor, 0.0), 0.5);
            return Math.Max(0.0, (u - f) / (1.0 - f));
        }

        /// <summary>Braking-lockup gate for the synthesized aligning force, the
        /// FORCE-path twin of AxleSlipEffect.LockupGate. Grip utilization is built
        /// from COMBINED slip (longitudinal included), so a front tire locking
        /// under braking pumps utilization to its peak and slams the aligning
        /// force to full scale, while a real locked or hard-sliding front tire
        /// makes almost no aligning torque. Under heavy brake/slide that near-peak
        /// force, driven by a laggy direction relay with little velocity damping,
        /// swings the wheel left and right (issue #38). Fade the composed force out
        /// as the worst front tire's SIGNED slip ratio falls from
        /// <paramref name="start"/> (fade begins) to <paramref name="full"/>
        /// (silent). Positive slip (wheelspin) and lateral scrub pass untouched
        /// (returns 1 for ratio &gt;= start); sources without tire quads pass a
        /// ratio of 0 and are unaffected. The caller passes the start/full
        /// thresholds, scaled to the manual or learned braking-grip point (they
        /// default to the shipped texture gate's -0.2 / -0.5).</summary>
        public static double LockupGate(double worstFrontSlipRatio,
                                        double start = -0.2, double full = -0.5)
        {
            if (worstFrontSlipRatio >= start) return 1.0;
            double span = Math.Max(0.05, start - full);
            double g = 1.0 - (start - worstFrontSlipRatio) / span;
            return g < 0.0 ? 0.0 : g;
        }

        /// <summary>Friction-circle lateral share, the A/B alternative to
        /// <see cref="LockupGate"/> (ModeBFrictionCircle toggle). A tire has one
        /// grip budget; longitudinal use (braking OR wheelspin, measured by
        /// |slip ratio|) spends part of it, and the aligning force scales by the
        /// lateral share that remains: sqrt(1 - (r/full)^2), the circle's
        /// vertical chord. Differences from the gate: continuous from zero (no
        /// threshold; even light braking sheds a few percent, which is real),
        /// circle-shaped rather than a linear ramp, and sign-blind so front
        /// wheelspin (AWD corner exit, launches) also lightens the wheel the way
        /// a spinning front tire really does. <paramref name="fullRatio"/> is
        /// where the budget is fully spent (aligning force zero), aligned with
        /// the gate's full-lock point.</summary>
        public static double FrictionCircleLateralShare(double frontSlipRatioAbs,
                                                        double fullRatio = 0.5)
        {
            double r = Math.Abs(frontSlipRatioAbs) / Math.Max(0.05, fullRatio);
            if (r >= 1.0) return 0.0;
            return Math.Sqrt(1.0 - r * r);
        }

        /// <summary>Reversal softening: take the violence out of a slide being
        /// CAUGHT. The synthesized aligning force keeps near-full magnitude right
        /// up to the slip-angle zero crossing (the drop floor and the combined-slip
        /// utilization both stay high while sliding), so a fast catch lands as a
        /// near-instant full-scale sign flip and the wheel→slip→force loop rings.
        /// It is the drift-reversal twin of the braking limit cycle (issue #38);
        /// the fix mirrors real self-aligning torque, which genuinely goes light
        /// through the reversal as pneumatic trail and lateral force pass through
        /// zero. Fade the composed force down only while the slide is COLLAPSING
        /// toward center, scaled by how fast it collapses; a slide being dug DEEPER
        /// (|slip| growing) passes untouched, so loaded drift weight is preserved.
        ///
        /// The "returning flux" -slipAngle·slipRate (rad²/s) is positive only while
        /// |slip| shrinks and is largest for a big angle caught fast — the exact
        /// violent case. It vanishes at exact center (where the force is already ~0
        /// via <see cref="CenterSoftDir"/>) and when the wheel is barely moving, so
        /// steady cornering and gentle transitions are untouched. In a limit cycle
        /// it strips energy on each approach-to-center quarter, damping the ring.
        /// Sign-invariant: multiplying both inputs by the direction sign leaves the
        /// product unchanged, so the caller need not de-sign them.
        /// <paramref name="slipAngleRad"/>: smoothed signed front slip angle.
        /// <paramref name="slipRateRadPerSec"/>: its smoothed time derivative.
        /// <paramref name="strength"/>: 0 = off; up to 1 fades to silent at a hard
        /// fast catch. <paramref name="refFlux"/>: returning flux (rad²/s) at which
        /// the cut reaches HALF of <paramref name="strength"/>; smaller softens
        /// gentler catches too.</summary>
        public static double ReversalSoften(double slipAngleRad, double slipRateRadPerSec,
                                            double strength, double refFlux = 0.3)
        {
            double s = Math.Min(Math.Max(strength, 0.0), 1.0);
            if (s <= 0.0) return 1.0;
            double returning = -slipAngleRad * slipRateRadPerSec;   // > 0 while |slip| shrinking
            if (double.IsNaN(returning) || returning <= 0.0) return 1.0;  // digging in / steady: untouched
            double x = returning / Math.Max(1e-4, refFlux);
            double t = x / (1.0 + x);                              // 0..1, smooth, half at x = 1
            return 1.0 - s * t;
        }

        /// <summary>Slide-adaptive direction-ramp window for the "trail spring".
        /// The direction blend (<see cref="CenterSoftDir"/>) saturates to ±1 at
        /// ±<paramref name="baseWindowRad"/> of slip angle (~0.03 rad / 1.7°
        /// shipped), so past that the aligning force is a CONSTANT-direction shove
        /// with no proportional restoring: the wheel is driven to the steering lock
        /// instead of settling into a countersteer like real pneumatic trail. Real
        /// trail is a SPRING (torque ∝ slip angle) that eases off as the front
        /// realigns, so the wheel finds a stable countersteer angle.
        ///
        /// Widening the window makes dir = slipAngle / window proportional over a
        /// wider slip-angle span, restoring that spring: as the driver catches the
        /// slide and the front comes back into line, the force ramps DOWN smoothly
        /// to a stable equilibrium at alignment. Done only DURING A SLIDE (gated on
        /// the rear-breakaway signal, which is decoupled from the front slip angle
        /// so it holds the window open through the whole catch) and smoothstepped
        /// for a C1 onset, so grip driving (gate ~0) keeps the shipped narrow,
        /// strong-on-center feel bit-for-bit.
        /// <paramref name="baseWindowRad"/>: gripping window (the shipped 0.03).
        /// <paramref name="maxWindowRad"/>: full-slide window (the "trail range").
        /// <paramref name="slideGate01"/>: 0 = gripping .. 1 = full slide.</summary>
        public static double AdaptiveDirWindow(double baseWindowRad, double maxWindowRad, double slideGate01)
        {
            double lo = Math.Max(1e-3, baseWindowRad);
            double hi = Math.Max(lo, maxWindowRad);
            double g = Math.Min(Math.Max(slideGate01, 0.0), 1.0);
            double shaped = g * g * (3.0 - 2.0 * g);      // C1 onset: grip stays at the base window
            return lo + (hi - lo) * shaped;
        }

        /// <summary>Slide duck: a multiplier that eases a stability force OUT as the
        /// rear breaks away. The centering spring pulls toward STRAIGHT, but during a
        /// slide the wheel's stable home is the COUNTERSTEER angle, so centering
        /// fights the trail spring (different equilibria). Fade centering down with
        /// the same rear-breakaway gate the trail spring uses, so it stays full for
        /// grip driving and stops fighting the countersteer in a slide.
        /// <paramref name="amount"/>: 0 = never duck (untouched) .. 1 = fully gone at
        /// full slide. <paramref name="slideGate01"/>: 0 = gripping .. 1 = full slide.
        /// Returns the multiplier: 1 gripping, (1 - amount) at full slide,
        /// smoothstepped so grip driving keeps its centering bit-for-bit.</summary>
        public static double SlideDuck(double amount, double slideGate01)
        {
            double a = Math.Min(Math.Max(amount, 0.0), 1.0);
            double g = Math.Min(Math.Max(slideGate01, 0.0), 1.0);
            double shaped = g * g * (3.0 - 2.0 * g);
            return 1.0 - a * shaped;
        }

        /// <summary>Phase-lead the slip angle that drives the direction blend, to
        /// cancel the telemetry-loop lag that makes the synthesized aligning force
        /// RING near its target. A real, physics-computed SAT spring settles because
        /// it has no lag; Mode B closes a lagged loop (wheel → 60 Hz telemetry →
        /// force → wheel, ~50-80 ms round trip), so a restoring spring oscillates
        /// instead of settling — worst right at the equilibrium, where the spring
        /// gain peaks. Extrapolating the slip angle forward by <paramref
        /// name="leadSec"/> using its rate makes the force ANTICIPATE where the
        /// wheel is heading instead of reacting to where it was, recovering the
        /// phase the loop lost. Sign-safe: it leads a signal by its own rate, so
        /// there is no direction to invert. The lead contribution is capped so a
        /// rate spike can't fling the prediction across center.
        /// <paramref name="slipRad"/>: smoothed signed slip angle.
        /// <paramref name="slipRateRadPerSec"/>: its smoothed rate.
        /// <paramref name="leadSec"/>: prediction horizon; 0 = identity.</summary>
        public static double PhaseLeadSlip(double slipRad, double slipRateRadPerSec, double leadSec)
        {
            if (leadSec <= 0.0 || double.IsNaN(slipRateRadPerSec)) return slipRad;
            double lead = leadSec * slipRateRadPerSec;
            const double cap = 0.15;   // rad (~8.6°): allows real anticipation, bounds a noise spike
            if (lead > cap) lead = cap; else if (lead < -cap) lead = -cap;
            return slipRad + lead;
        }

        /// <summary>Compose the final Mode B force from the front-axle SAT
        /// term plus the per-axle feel terms.
        /// <paramref name="satSigned"/>: SatForceModel output × direction, [-1, 1].
        /// <paramref name="dir"/>: smooth signed direction blend, [-1, 1].
        /// <paramref name="overExcess"/>: rear utilization excess over the
        /// front (≥ 0), temporally smoothed by the caller — the composer owns
        /// only the SHAPE. Smoothstepped over (0 .. OverCap): zero slope at
        /// onset, so a grazing rear slide barely registers and the pull
        /// builds progressively instead of snapping in (third wheel test:
        /// "no traction then TRACTION").
        /// <paramref name="latG"/>: |lateral acceleration| in g (≥ 0).
        /// <paramref name="latGain"/>: cornering-weight gain (0 = off).
        /// <paramref name="counterGain"/>: countersteer force gain (0 = off).
        /// <paramref name="trail01"/>: speed trail ramp, 0 parked → 1 at speed.
        /// <paramref name="slideDepth01"/>: test layer 10 (GAP #2) — scales
        /// the counter term by slide depth so a shallow drift asks politely
        /// and a deep one yanks toward opposite lock like real SAT, instead
        /// of full counter arriving the moment dir saturates at ±0.03 rad.
        /// Caller derives it as min(|front slip|/0.15, 1); pass 1.0 for the
        /// legacy layer-off behavior.</summary>
        public static double Compose(
            double satSigned, double dir,
            double overExcess,
            double latG, double latGain,
            double counterGain, double trail01,
            double slideDepth01 = 1.0)
        {
            double latMult = 1.0 + Math.Max(0.0, latGain) * Math.Min(Math.Max(latG, 0.0), LatGCap);

            double t = Math.Min(Math.Max(overExcess, 0.0), OverCap) / OverCap;
            double shaped = t * t * (3.0 - 2.0 * t);      // C1 onset and saturation
            double counter = Math.Max(0.0, counterGain)
                           * shaped
                           * dir
                           * Math.Min(Math.Max(trail01, 0.0), 1.0)
                           * Math.Min(Math.Max(slideDepth01, 0.0), 1.0);

            double f = satSigned * latMult + counter;
            return Math.Min(1.0, Math.Max(-1.0, f));
        }
    }
}
