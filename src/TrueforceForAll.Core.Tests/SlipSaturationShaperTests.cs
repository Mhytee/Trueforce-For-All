using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tests the slip-angle saturation reshaper: the DropFactor curve (1.0 below
    // the grip peak, easing to DropFloor past it) and Apply's scaling/clamping.
    public class SlipSaturationShaperTests
    {
        private static SlipSaturationShaper Default() =>
            new SlipSaturationShaper { PeakSlipAngleRad = 0.12, FullSlipAngleRad = 0.35, DropFloor = 0.35 };

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.05)]
        [InlineData(0.12)]   // exactly at the peak: still untouched
        public void BelowOrAtPeak_FactorIsOne(double slip)
        {
            Assert.Equal(1.0, Default().DropFactor(slip), 9);
        }

        [Theory]
        [InlineData(0.35)]   // exactly at full
        [InlineData(0.50)]
        [InlineData(2.68)]   // deep off-road slide (max seen in capture)
        public void AtOrBeyondFull_FactorIsFloor(double slip)
        {
            Assert.Equal(0.35, Default().DropFactor(slip), 9);
        }

        [Fact]
        public void Factor_IsMonotonicNonIncreasing_AsSlipGrows()
        {
            var s = Default();
            double prev = 1.0;
            for (double a = 0.0; a <= 0.6; a += 0.01)
            {
                double f = s.DropFactor(a);
                Assert.True(f <= prev + 1e-12, $"factor rose at slip={a}: {f} > {prev}");
                Assert.InRange(f, 0.35 - 1e-9, 1.0 + 1e-9);
                prev = f;
            }
        }

        [Fact]
        public void Factor_IsSymmetricInSign()
        {
            var s = Default();
            foreach (double a in new[] { 0.05, 0.2, 0.4 })
                Assert.Equal(s.DropFactor(a), s.DropFactor(-a), 12);
        }

        [Fact]
        public void Factor_MidwayBetweenPeakAndFull_IsHalfwayViaSmoothstep()
        {
            // Smoothstep at t=0.5 gives e=0.5, so factor = 1 - 0.5*(1-floor).
            var s = Default();
            double mid = (s.PeakSlipAngleRad + s.FullSlipAngleRad) / 2.0;
            double expected = 1.0 - 0.5 * (1.0 - s.DropFloor);   // = 0.675
            Assert.Equal(expected, s.DropFactor(mid), 9);
        }

        [Fact]
        public void Apply_BelowPeak_ReturnsForceUnchanged()
        {
            Assert.Equal((short)12000, Default().Apply(12000, 0.03));
            Assert.Equal((short)-12000, Default().Apply(-12000, 0.03));
        }

        [Fact]
        public void Apply_DeepSlide_ScalesByFloor_PreservingSign()
        {
            var s = Default();
            Assert.Equal((short)Math.Round(30000 * 0.35), s.Apply(30000, 0.5));    // 10500
            Assert.Equal((short)Math.Round(-30000 * 0.35), s.Apply(-30000, 0.5));  // -10500
        }

        [Fact]
        public void Apply_ClampsToInt16()
        {
            // Floor never amplifies, but guard the clamp anyway (factor <= 1).
            var s = new SlipSaturationShaper { DropFloor = 1.0 };   // identity
            Assert.Equal(short.MaxValue, s.Apply(short.MaxValue, 1.0));
            Assert.Equal(short.MinValue, s.Apply(short.MinValue, 1.0));
        }

        [Fact]
        public void DegenerateConfig_FullNotPastPeak_SnapsToFloorAbovePeak()
        {
            var s = new SlipSaturationShaper { PeakSlipAngleRad = 0.2, FullSlipAngleRad = 0.2, DropFloor = 0.4 };
            Assert.Equal(1.0, s.DropFactor(0.1), 9);    // below peak still untouched
            Assert.Equal(0.4, s.DropFactor(0.25), 9);   // past peak snaps to floor
        }
    }
}
