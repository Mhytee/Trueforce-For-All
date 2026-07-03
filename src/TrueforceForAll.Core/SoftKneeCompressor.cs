// Soft-knee force compressor — the "all gain lives here" stage of the shared
// FFB post-chain (phase 3 of docs/haptic-engine-plan.md). Linear gain below
// the knee, hyperbolic compression above it, asymptote strictly below full
// scale. On a 2.2 Nm wheel the philosophy is: sacrifice the unrenderable top
// ~10% of force fidelity to bring the middle 80% into the motor's expressive
// range, while preserving RELATIVE differences near the top instead of
// hard-clip flattening.
//
// Stability property (why this replaces FfbScale as the gain stage): the
// transfer slope only ever DECREASES past the knee, and the output is bounded
// by Ceiling01 < 1. A memoryless compressor adds no phase lag, so it can
// shrink a limit-cycle's amplitude but never create one — the 3.3 Hz
// oscillation of 2026-06-30 came from gain applied AFTER all shaping with
// only a hard clamp above it; this is the structural fix.
//
// Measuring the knee excess post-gain makes the curve C1 at the threshold
// automatically (both branches have slope G at the knee) — same math as the
// device's transient soft-cap, generalized and normalized.

using System;

namespace TrueforceForAll.Core
{
    public sealed class SoftKneeCompressor
    {
        /// <summary>Linear gain below the knee. The GAIN access code maps here
        /// (FfbScale becomes pure hardware calibration at 1.0).</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>Knee onset as a fraction of full scale, measured on the
        /// post-gain magnitude. 1.0 = knee never engages (pure linear).</summary>
        public double Threshold01 { get; set; } = 0.7;

        /// <summary>Output asymptote, strictly &lt; 1.0 so the hard clamp
        /// downstream never engages in nominal operation.</summary>
        public double Ceiling01 { get; set; } = 0.98;

        /// <summary>Compress one normalized force sample. Pure, odd-symmetric,
        /// monotone, C1 at the knee, bounded below Ceiling01.</summary>
        public double Apply(double f01)
        {
            double s = f01 < 0 ? -1.0 : 1.0;
            double m = Gain * Math.Abs(f01);
            double t = Threshold01;
            if (m <= t) return s * m;
            double c = Ceiling01 - t;              // available headroom
            if (c <= 0) return s * Math.Min(m, Threshold01);   // degenerate config: hard knee at T
            double e = m - t;
            return s * (t + c * e / (c + e));      // hyperbolic knee → asymptote Ceiling01
        }

        // Shipped presets (the COMPLIN/COMPHVY/COMPMAX access codes).
        public static SoftKneeCompressor Linear()  => new SoftKneeCompressor { Gain = 1.0, Threshold01 = 1.0, Ceiling01 = 1.0 };
        public static SoftKneeCompressor Heavy()   => new SoftKneeCompressor { Gain = 1.6, Threshold01 = 0.60, Ceiling01 = 0.96 };
        public static SoftKneeCompressor MaxInfo() => new SoftKneeCompressor { Gain = 2.2, Threshold01 = 0.50, Ceiling01 = 0.98 };
    }
}
