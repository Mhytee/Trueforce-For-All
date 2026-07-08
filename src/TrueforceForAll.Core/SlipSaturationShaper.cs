// Slip-angle saturation reshaping for force feedback.
//
// Why: captured data (Forza Horizon, 12k aligned telemetry+FFB pairs) shows the
// game's FFB has NO self-aligning-torque saturation, holding load fixed, force
// rises monotonically with slip angle and rails at full scale during slides. A
// real front tire's aligning torque peaks near the optimal slip angle (~6-10 deg)
// and then FALLS as the contact patch saturates, that fall is the "the front is
// washing out, the wheel goes light" cue the game never gives.
//
// This reshaper restores that cue WITHOUT discarding the game's force (which has
// good load/road/kerb content). It multiplies the game's force by a DropFactor
// that is 1.0 below the grip peak (leave the game alone where it's fine) and
// eases down to DropFloor past it (lighten the wheel as the front lets go):
//
//   factor
//   1.0 |--------\
//       |         \___
//   floor|             \________
//       +----|--------|---------------> |slip angle|
//          peak      full
//
// It runs on the same int16 force the rest of the pipeline uses, so it sits in
// the FFB-target path (FfbTargetProvider -> TrueforceDevice cur) with no driver
// and no cert. Pure/stateless: one input slip angle + force in, shaped force out,
// trivially unit-testable and safe to A/B against the raw force live.
//
// NOTE: because the game's force climbs steeply with slip, a multiplicative drop
// FLATTENS the climb at moderate floors and only produces an actual force
// DECREASE past the peak at low floors. The default here is the "pronounced"
// setting; DropFloor is exposed so feel can be tuned on the wheel. If even the
// low floor can't deliver enough lightening, an absolute-cap variant (cap that
// declines past the peak) is the planned v2.

using System;

namespace TrueforceForAll.Core
{
    public sealed class SlipSaturationShaper
    {
        // Below this |slip angle| the game's force is untouched (factor 1.0).
        // ~0.12 rad ≈ 7°, a typical street-tire peak-grip slip angle. Forza's
        // saturation-free data can't reveal the true peak, so this is a model
        // parameter anchored to real tire behavior and tuned by feel.
        public double PeakSlipAngleRad { get; set; } = 0.12;

        // |slip angle| at which the drop reaches DropFloor and stays there.
        // ~0.35 rad ≈ 20°, comfortably into "the front has let go" territory.
        public double FullSlipAngleRad { get; set; } = 0.35;

        // Force multiplier once fully past the peak (at/after FullSlipAngleRad).
        // 1.0 = no reshaping. The captured "pronounced" default: 0.35 lightens
        // the wheel hard during a slide. Lower = more lightening (closer to a
        // true force decrease); 1.0 disables the effect.
        public double DropFloor { get; set; } = 0.35;

        /// <summary>Force multiplier in (0, 1] for a given slip angle. 1.0 below
        /// the peak; eases via a smoothstep shoulder down to DropFloor by
        /// FullSlipAngleRad; held at DropFloor beyond. Symmetric in sign.</summary>
        public double DropFactor(double slipAngleRad)
        {
            double a = Math.Abs(slipAngleRad);
            double peak = PeakSlipAngleRad;
            double full = FullSlipAngleRad;
            double floor = DropFloor;

            if (a <= peak) return 1.0;

            double span = full - peak;
            if (span <= 0) return floor;          // degenerate config: snap to floor past peak

            double t = (a - peak) / span;
            if (t > 1.0) t = 1.0;
            // Smoothstep easing (3t^2 - 2t^3): a natural shoulder at the peak and
            // a soft landing at the floor, rather than a hard corner that would
            // itself feel like a notch.
            double e = t * t * (3.0 - 2.0 * t);
            return 1.0 - e * (1.0 - floor);
        }

        /// <summary>Apply the slip-saturation reshape to a game FFB target,
        /// returning the shaped int16 force (clamped). Sign preserved.</summary>
        public short Apply(short gameFfb, double slipAngleRad)
        {
            double v = gameFfb * DropFactor(slipAngleRad);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            return (short)Math.Round(v);
        }
    }
}
