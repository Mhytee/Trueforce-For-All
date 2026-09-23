// Monotone piecewise-linear curve: the calibration primitive that maps a
// game's raw combined slip onto canonical grip utilization (phase 5 of
// docs/haptic-engine-plan.md). Linear, not cubic, on purpose: monotonicity is
// a SAFETY property here — a non-monotone fit would invert the "more slip =
// less grip margin" cue mid-corner — and anchors are few (4-6), with
// smoothness provided by the EMA downstream, not the curve.

using System;

namespace TrueforceForAll.Core
{
    public sealed class PiecewiseCurve
    {
        /// <summary>Anchor inputs, strictly increasing.</summary>
        public double[] X { get; set; }

        /// <summary>Anchor outputs, non-decreasing (monotone).</summary>
        public double[] Y { get; set; }

        public PiecewiseCurve() { }
        public PiecewiseCurve(double[] x, double[] y) { X = x; Y = y; }

        /// <summary>Structural validity: parallel arrays, len ≥ 2, all finite,
        /// X strictly increasing, Y non-decreasing.</summary>
        public bool IsValid()
        {
            if (X == null || Y == null || X.Length != Y.Length || X.Length < 2) return false;
            for (int i = 0; i < X.Length; i++)
                if (double.IsNaN(X[i]) || double.IsInfinity(X[i]) ||
                    double.IsNaN(Y[i]) || double.IsInfinity(Y[i])) return false;
            for (int i = 1; i < X.Length; i++)
            {
                if (X[i] <= X[i - 1]) return false;
                if (Y[i] <  Y[i - 1]) return false;
            }
            return true;
        }

        /// <summary>Interpolate. Inputs outside the anchor range clamp to the
        /// end values (calibration curves own their full input range by
        /// construction; clamping is the safe extrapolation). O(log n),
        /// allocation-free.</summary>
        public double Eval(double x)
        {
            var xs = X; var ys = Y;
            if (x <= xs[0]) return ys[0];
            int last = xs.Length - 1;
            if (x >= xs[last]) return ys[last];

            // Binary search for the segment: largest i with xs[i] <= x.
            int lo = 0, hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (xs[mid] <= x) lo = mid; else hi = mid;
            }
            double t = (x - xs[lo]) / (xs[hi] - xs[lo]);
            return ys[lo] + t * (ys[hi] - ys[lo]);
        }
    }
}
