using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 4 Mode B per-axle composition: cornering-weight multiplier and
    // rear-breakaway counter torque on top of the pure SatForceModel term.
    // The composer owns the SHAPE (smoothstep onset/saturation, caps, clamp);
    // temporal smoothing of overExcess lives at the integration layer.
    public class ModeBComposerTests
    {
        private static double Smoothstep(double over)
        {
            double t = Math.Min(Math.Max(over, 0.0), ModeBComposer.OverCap) / ModeBComposer.OverCap;
            return t * t * (3.0 - 2.0 * t);
        }

        [Fact]
        public void ZeroGains_PassesSatThrough()
        {
            double f = ModeBComposer.Compose(
                satSigned: 0.4, dir: 1.0, overExcess: 0.9,
                latG: 1.0, latGain: 0.0, counterGain: 0.0, trail01: 1.0);
            Assert.Equal(0.4, f, 10);
        }

        [Fact]
        public void CorneringWeight_ScalesWithLatG_AndCaps()
        {
            double flat = ModeBComposer.Compose(0.3, 1, 0, 0.0, 0.8, 0, 1);
            double oneG = ModeBComposer.Compose(0.3, 1, 0, 1.0, 0.8, 0, 1);
            Assert.Equal(0.3, flat, 10);                 // no lat accel: unchanged
            Assert.Equal(0.3 * (1 + 0.8), oneG, 10);     // 1 g: full gain applied

            // Beyond the cap the multiplier stops growing (kerb-spike guard).
            double capped = ModeBComposer.Compose(0.3, 1, 0, ModeBComposer.LatGCap, 0.8, 0, 1);
            double insane = ModeBComposer.Compose(0.3, 1, 0, 5.0, 0.8, 0, 1);
            Assert.Equal(capped, insane, 10);
        }

        [Fact]
        public void CorneringWeight_PreservesSign()
        {
            double left  = ModeBComposer.Compose(-0.3, -1, 0, 1.0, 0.8, 0, 1);
            double right = ModeBComposer.Compose( 0.3,  1, 0, 1.0, 0.8, 0, 1);
            Assert.Equal(-right, left, 10);
        }

        [Fact]
        public void Counter_SilentWhenNoExcess()
        {
            // Understeer or neutral: zero (or negative) excess adds nothing.
            double f0 = ModeBComposer.Compose(0.2, 1, 0.0, 0, 0, 0.6, 1);
            double fn = ModeBComposer.Compose(0.2, 1, -0.4, 0, 0, 0.6, 1);
            Assert.Equal(0.2, f0, 10);
            Assert.Equal(0.2, fn, 10);
        }

        [Fact]
        public void Counter_SmoothstepShaped_OnRearBreakaway()
        {
            double catchF = ModeBComposer.Compose(0.15, 1, 0.8, 0, 0, 0.6, 1);
            Assert.Equal(0.15 + 0.6 * Smoothstep(0.8), catchF, 10);

            // Excess beyond OverCap saturates at full counter gain.
            double deep = ModeBComposer.Compose(0.15, 1, 3.0, 0, 0, 0.6, 1);
            Assert.Equal(0.15 + 0.6, deep, 10);
        }

        [Fact]
        public void Counter_OnsetIsGentle_NotLinear()
        {
            // The snap fix: a grazing slide (small excess) must produce far
            // less torque than a linear ramp would — zero slope at onset.
            double graze = ModeBComposer.Compose(0.0, 1, 0.1, 0, 0, 1.0, 1);
            Assert.True(graze < 0.1 * 0.5, $"onset too sharp: {graze}");

            // And the curve is monotone: more excess never means less pull.
            double prev = 0;
            for (double over = 0; over <= 1.2; over += 0.01)
            {
                double f = ModeBComposer.Compose(0.0, 1, over, 0, 0, 1.0, 1);
                Assert.True(f >= prev - 1e-12, $"non-monotone at over={over}");
                prev = f;
            }
        }

        [Fact]
        public void Counter_GatedByTrailRamp_QuietAtStandstill()
        {
            // Launch wheelspin at a stop: huge excess, but trail 0 → no torque.
            double f = ModeBComposer.Compose(0.0, 0.5, 1.9, 0, 0, 0.8, trail01: 0);
            Assert.Equal(0.0, f, 10);
        }

        [Fact]
        public void Counter_FollowsDirSign()
        {
            double pos = ModeBComposer.Compose(0, 1.0, 0.8, 0, 0, 0.5, 1);
            double neg = ModeBComposer.Compose(0, -1.0, 0.8, 0, 0, 0.5, 1);
            Assert.Equal(-pos, neg, 10);
        }

        [Fact]
        public void Output_ClampedToUnitRange()
        {
            double hi = ModeBComposer.Compose(0.9, 1, 1.5, 1.5, 2.0, 1.5, 1);
            double lo = ModeBComposer.Compose(-0.9, -1, 1.5, 1.5, 2.0, 1.5, 1);
            Assert.Equal(1.0, hi, 10);
            Assert.Equal(-1.0, lo, 10);
        }

        [Fact]
        public void NegativeInputs_TreatedAsZeroGains()
        {
            // Defensive: negative gains/latG never invert or explode the force.
            double f = ModeBComposer.Compose(0.3, 1, 0.6, -1.0, -0.8, -0.5, 1);
            Assert.Equal(0.3, f, 10);
        }
    }
}
