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
//  * Slide counter-force: with front-only utilization, rear breakaway is
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
        /// <paramref name="counterGain"/>: slide counter-force gain (0 = off).
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
