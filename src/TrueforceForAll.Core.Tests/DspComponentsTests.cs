using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Standalone DSP components for later phases, unit-tested before they're
    // wired into anything: SoftKneeCompressor (phase 3), AlphaBetaFilter
    // (phase 1 resampler), PiecewiseCurve (phase 5 calibration), SatForceModel
    // (phase 4 Mode B).
    public class DspComponentsTests
    {
        // ---------------- SoftKneeCompressor ----------------

        [Fact]
        public void Compressor_ContinuousAndC1_AtKnee()
        {
            var c = new SoftKneeCompressor { Gain = 1.6, Threshold01 = 0.6, Ceiling01 = 0.96 };
            double kneeIn = c.Threshold01 / c.Gain;   // input magnitude that lands on T post-gain
            const double eps = 1e-7;

            double below = c.Apply(kneeIn - eps);
            double above = c.Apply(kneeIn + eps);
            Assert.True(Math.Abs(above - below) < 1e-5, "discontinuous at knee");

            // Numeric slope both sides of the knee must match to ~0.1%.
            double slopeBelow = (c.Apply(kneeIn - eps) - c.Apply(kneeIn - 2 * eps)) / eps;
            double slopeAbove = (c.Apply(kneeIn + 2 * eps) - c.Apply(kneeIn + eps)) / eps;
            Assert.True(Math.Abs(slopeAbove - slopeBelow) / slopeBelow < 0.001,
                $"not C1 at knee: below {slopeBelow:F6}, above {slopeAbove:F6}");
        }

        [Fact]
        public void Compressor_Monotone_Bounded_OddSymmetric()
        {
            foreach (var c in new[] { SoftKneeCompressor.Linear(), SoftKneeCompressor.Heavy(), SoftKneeCompressor.MaxInfo() })
            {
                double prev = double.NegativeInfinity;
                for (double x = 0; x <= 3.0; x += 0.001)
                {
                    double y = c.Apply(x);
                    Assert.True(y >= prev, $"non-monotone at {x} ({c.Gain}/{c.Threshold01})");
                    Assert.True(y <= Math.Max(c.Ceiling01, c.Threshold01) + 1e-9, $"exceeded ceiling at {x}: {y}");
                    Assert.Equal(-y, c.Apply(-x), 12);   // odd symmetry
                    prev = y;
                }
                // Deep overdrive stays strictly below 1.0 for the knee'd presets.
                if (c.Threshold01 < 1.0)
                    Assert.True(c.Apply(10.0) < c.Ceiling01, "asymptote violated");
            }
        }

        [Fact]
        public void Compressor_LinearPreset_IsIdentityBelowFullScale()
        {
            var c = SoftKneeCompressor.Linear();
            Assert.Equal(0.5, c.Apply(0.5), 12);
            Assert.Equal(-0.25, c.Apply(-0.25), 12);
        }

        // ---------------- AlphaBetaFilter ----------------

        [Fact]
        public void AlphaBeta_ConvergesToConstant()
        {
            var f = new AlphaBetaFilter();
            for (int i = 0; i < 50; i++) f.Update(5.0, 1.0 / 60);
            Assert.Equal(5.0, f.Value, 3);
            Assert.Equal(0.0, f.Velocity, 2);
        }

        [Fact]
        public void AlphaBeta_TracksRamp_AndPredictsAhead()
        {
            // 60 Hz samples of a unit-slope ramp; after convergence the
            // 16.7 ms prediction should land near the true future value.
            var f = new AlphaBetaFilter();
            double dt = 1.0 / 60;
            double t = 0;
            for (int i = 0; i < 120; i++) { t = i * dt; f.Update(t, i == 0 ? 0 : dt); }

            double horizon = dt;
            double predicted = f.PredictAt(horizon);
            Assert.Equal(t + horizon, predicted, 2);
            Assert.Equal(1.0, f.Velocity, 1);
        }

        [Fact]
        public void AlphaBeta_PredictionHorizon_IsClamped()
        {
            var f = new AlphaBetaFilter { MaxHorizonSec = 0.025 };
            double dt = 1.0 / 60;
            for (int i = 0; i < 120; i++) f.Update(i * dt, i == 0 ? 0 : dt);   // slope 1 ramp

            // A 1-second horizon must extrapolate no further than 25 ms worth.
            double capped = f.PredictAt(1.0);
            Assert.True(capped <= f.Value + 0.026, $"horizon not clamped: {capped} vs value {f.Value}");
        }

        [Fact]
        public void AlphaBeta_FirstSampleAfterReset_SeedsWithoutSlew()
        {
            var f = new AlphaBetaFilter();
            f.Update(100.0, 1.0 / 60);
            Assert.Equal(100.0, f.Value, 9);
            Assert.Equal(0.0, f.Velocity, 9);
            Assert.Equal(100.0, f.PredictAt(0.016), 9);   // no ghost velocity
        }

        // ---------------- PiecewiseCurve ----------------

        [Fact]
        public void Curve_Interpolates_And_ClampsEnds()
        {
            var c = new PiecewiseCurve(new[] { 0.0, 0.5, 1.0, 1.8, 4.0 },
                                       new[] { 0.0, 0.55, 1.0, 1.6, 2.0 });
            Assert.True(c.IsValid());
            Assert.Equal(0.0, c.Eval(-1), 9);          // clamp low
            Assert.Equal(2.0, c.Eval(99), 9);          // clamp high
            Assert.Equal(0.55, c.Eval(0.5), 9);        // anchor exact
            Assert.Equal(0.775, c.Eval(0.75), 9);      // midpoint of (0.5,0.55)-(1,1.0)
        }

        [Fact]
        public void Curve_Validity_RejectsBadShapes()
        {
            Assert.False(new PiecewiseCurve(new[] { 0.0, 0.0 }, new[] { 0.0, 1.0 }).IsValid());   // non-increasing X
            Assert.False(new PiecewiseCurve(new[] { 0.0, 1.0 }, new[] { 1.0, 0.5 }).IsValid());   // decreasing Y
            Assert.False(new PiecewiseCurve(new[] { 0.0 }, new[] { 0.0 }).IsValid());              // too short
            Assert.False(new PiecewiseCurve(new[] { 0.0, double.NaN }, new[] { 0.0, 1.0 }).IsValid());
        }

        // ---------------- SatForceModel ----------------

        [Fact]
        public void Sat_PeaksAtLimit_DropsPastIt()
        {
            var m = new SatForceModel { DropFloor = 0.2, FullU = 1.6 };
            double atPeak = m.Force01(1.0, +0.1, 1.0, 120);
            double past   = m.Force01(1.6, +0.1, 1.0, 120);
            double way    = m.Force01(3.0, +0.1, 1.0, 120);

            Assert.True(atPeak > 0);
            Assert.Equal(m.SatGain, atPeak, 6);                       // full trail, static load
            Assert.Equal(atPeak * m.DropFloor, past, 6);              // floor reached at FullU
            Assert.Equal(past, way, 6);                               // held past FullU
        }

        [Fact]
        public void Sat_RiseIsMonotone_BelowPeak()
        {
            var m = new SatForceModel();
            double prev = 0;
            for (double u = 0.05; u <= 1.0; u += 0.05)
            {
                double f = m.Force01(u, +0.1, 1.0, 120);
                Assert.True(f >= prev, $"rise not monotone at u={u}");
                prev = f;
            }
        }

        [Fact]
        public void Sat_SignFollowsSlip_AndSpeedBuildsTrail()
        {
            var m = new SatForceModel { SpeedFullKmh = 60 };
            Assert.True(m.Force01(0.8, -0.1, 1.0, 120) < 0);          // sign flips with slip
            double slow = m.Force01(0.8, +0.1, 1.0, 30);              // half trail
            double fast = m.Force01(0.8, +0.1, 1.0, 90);              // full trail
            Assert.True(slow < fast, "40 must feel lighter than 80");
            Assert.Equal(fast * 0.5, slow, 6);
            Assert.Equal(0.0, m.Force01(0.8, +0.1, 1.0, 0), 9);       // parked: no SAT fight
        }

        [Fact]
        public void Sat_LoadEffect_BlendsAroundStatic()
        {
            var m = new SatForceModel { LoadEffect = 0.5 };
            double baseline = m.Force01(0.9, +0.1, 1.0, 120);
            double loaded   = m.Force01(0.9, +0.1, 1.5, 120);
            double light    = m.Force01(0.9, +0.1, 0.5, 120);
            Assert.Equal(baseline * 1.25, loaded, 6);                  // 1 + 0.5*(1.5-1)
            Assert.Equal(baseline * 0.75, light, 6);
            // Extreme derived load is clamped so a bad weight model can't
            // double the force.
            Assert.Equal(baseline * 1.5, m.Force01(0.9, +0.1, 99, 120), 6);
        }
    }
}
