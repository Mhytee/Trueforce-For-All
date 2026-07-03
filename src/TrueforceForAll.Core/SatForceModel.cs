// Self-aligning-torque force model — the heart of Mode B full synthesis
// (phase 4 of docs/haptic-engine-plan.md). Works in grip-utilization space
// (u: 0 = coasting, 1 = at the limit, >1 = past it) so the model is
// game-agnostic once calibration owns the raw-slip → u mapping, and the peak
// sits at u = 1.0 BY CONSTRUCTION.
//
// Shape: rise below the peak (gamma-curved), then the on-wheel-validated
// smoothstep drop from the peak to DropFloor over (1.0 .. FullU) — the same
// geometry as SlipSaturationShaper (peak-and-drop is what Forza's own FFB
// lacks: it's k·slip·load clamped, monotone, so the wheel stays heavy exactly
// when a real front tire goes light). Load scales torque around its static
// value; the speed "trail ramp" builds pneumatic trail from zero so parked
// and creeping speeds don't fight the driver — and 40 mph genuinely feels
// lighter than 80.
//
// Pure math, no state: input smoothing (EMA on u/load) and the sign-flip
// deadband live at the integration layer (the FFB provider), same "smooth the
// inputs, not the force" principle the reshaper validated. Returns a signed
// normalized force in [-1, 1]; the compressor downstream owns loudness.

using System;

namespace TrueforceForAll.Core
{
    public sealed class SatForceModel
    {
        // --- primary tunables (SAT / SATFULL / FLOOR / SATRISE codes) ---

        /// <summary>Peak aligning torque as a fraction of full scale, at
        /// nominal load and full trail.</summary>
        public double SatGain { get; set; } = 0.55;

        /// <summary>Utilization at which the drop floor is fully reached.
        /// The peak is fixed at u = 1.0 by calibration.</summary>
        public double FullU { get; set; } = 1.6;

        /// <summary>Torque fraction remaining past FullU (0.20 = the
        /// "pronounced" on-wheel-validated drop; 0.35 = subtle).</summary>
        public double DropFloor { get; set; } = 0.35;

        /// <summary>Rise curvature below the peak. 1 = linear buildup,
        /// &lt; 1 = eager (more force earlier), &gt; 1 = lazy.</summary>
        public double RiseGamma { get; set; } = 1.0;

        // --- secondary tunables (LOADFX / trail) ---

        /// <summary>How much derived load modulates torque. 0 = ignore load,
        /// 1 = fully proportional. Default 0.5 hedges the Tier-2 reality that
        /// FM8 load is derived from accel, not measured.</summary>
        public double LoadEffect { get; set; } = 0.5;

        /// <summary>Speed (km/h) at which pneumatic trail (and thus SAT) is
        /// fully built. Below it, torque scales down linearly toward zero at
        /// standstill.</summary>
        public double SpeedFullKmh { get; set; } = 60.0;

        /// <summary>Signed normalized aligning torque in [-1, 1].
        /// <paramref name="uFront"/>: front-axle grip utilization (≥ 0).
        /// <paramref name="signedFrontSlipRad"/>: supplies the SIGN only.
        /// <paramref name="load01"/>: front axle load, 1.0 = static.
        /// <paramref name="speedKmh"/>: vehicle speed for the trail ramp.</summary>
        public double Force01(double uFront, double signedFrontSlipRad, double load01, double speedKmh)
        {
            if (uFront <= 0 || signedFrontSlipRad == 0) return 0;
            double u = uFront;

            double rise = Math.Pow(Math.Min(u, 1.0), Math.Max(0.05, RiseGamma));

            double drop = 1.0;
            if (u > 1.0)
            {
                double span = Math.Max(1e-6, FullU - 1.0);
                double t = Math.Min((u - 1.0) / span, 1.0);
                double e = t * t * (3.0 - 2.0 * t);           // validated smoothstep shoulder
                drop = 1.0 - e * (1.0 - DropFloor);
            }

            double loadClamped = Math.Min(Math.Max(load01, 0.0), 2.0);
            double load = 1.0 + LoadEffect * (loadClamped - 1.0);

            double trail = Math.Min(Math.Max(speedKmh, 0.0) / Math.Max(1.0, SpeedFullKmh), 1.0);

            double f = Math.Sign(signedFrontSlipRad) * SatGain * rise * drop * load * trail;
            return Math.Min(1.0, Math.Max(-1.0, f));
        }
    }
}
