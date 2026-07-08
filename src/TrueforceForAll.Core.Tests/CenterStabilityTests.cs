// Center stability (sixth wheel test: buzz at center + hands-off ring).
// CenterSoftDir must have zero slope at dead center — that's the whole
// fix — while preserving saturation and symmetry. UtilizationFloor must
// zero the noise fuzz and keep u = 1 meaning the limit.

using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class CenterStabilityTests
    {
        [Fact]
        public void CenterSoftDir_AttenuatesNoiseNearCenter()
        {
            // The linear ramp gave 0.05 → 0.05. The rational curve must cut
            // small-signal output several-fold: noise lands on the flat spot.
            double nearCenter = ModeBComposer.CenterSoftDir(0.05, 0.12);
            Assert.True(Math.Abs(nearCenter) < 0.02,
                $"expected strong attenuation at raw=0.05, got {nearCenter}");
        }

        [Fact]
        public void CenterSoftDir_NoSteepWallInTheBody()
        {
            // The smoothstep's 1.5× mid-band slope made ±3° rocking jumpy
            // (seventh wheel test). The rational curve's slope must stay
            // near-linear across the body of the band.
            for (double r = 0.2; r < 1.0; r += 0.1)
            {
                double slope = (ModeBComposer.CenterSoftDir(r + 0.05, 0.12)
                              - ModeBComposer.CenterSoftDir(r - 0.05, 0.12)) / 0.1;
                Assert.InRange(slope, 0.5, 1.25);
            }
        }

        [Fact]
        public void CenterSoftDir_ZeroSoftnessIsExactlyLinear()
        {
            for (double r = -1.0; r <= 1.0001; r += 0.1)
            {
                double clamped = Math.Min(Math.Max(r, -1.0), 1.0);
                Assert.Equal(clamped, ModeBComposer.CenterSoftDir(r, 0.0), 9);
            }
        }

        [Fact]
        public void CenterSoftDir_SaturatesAndIsOdd()
        {
            Assert.Equal(1.0, ModeBComposer.CenterSoftDir(1.0), 12);
            Assert.Equal(-1.0, ModeBComposer.CenterSoftDir(-1.0), 12);
            Assert.Equal(1.0, ModeBComposer.CenterSoftDir(3.0), 12);    // clamped
            Assert.Equal(
                -ModeBComposer.CenterSoftDir(0.4),
                ModeBComposer.CenterSoftDir(-0.4), 12);
            Assert.Equal(0.0, ModeBComposer.CenterSoftDir(0.0), 12);
        }

        [Fact]
        public void CenterSoftDir_MonotoneThroughTheBlendBand()
        {
            double prev = -1.0;
            for (double r = -1.0; r <= 1.0001; r += 0.05)
            {
                double v = ModeBComposer.CenterSoftDir(r);
                Assert.True(v >= prev - 1e-12, $"non-monotone at raw={r}");
                prev = v;
            }
        }

        [Fact]
        public void LowSpeedGate_SilentAtCrawl_UntouchedAtSpeed()
        {
            // Twelfth wheel test: stuck off-road at 0-14 km/h, slip signals
            // undefined/thrashing, force buzzed the rim. Below walking pace
            // the gate must be exactly zero; from 20 km/h up exactly one so
            // every validated behavior is untouched.
            Assert.Equal(0.0, ModeBComposer.LowSpeedGate(0.0), 12);
            Assert.Equal(0.0, ModeBComposer.LowSpeedGate(6.0), 12);
            Assert.Equal(1.0, ModeBComposer.LowSpeedGate(20.0), 12);
            Assert.Equal(1.0, ModeBComposer.LowSpeedGate(180.0), 12);
            // The stuck-in-the-dirt band is strongly suppressed.
            Assert.True(ModeBComposer.LowSpeedGate(10.0) < 0.25,
                $"10 km/h should be mostly gated, got {ModeBComposer.LowSpeedGate(10.0)}");
            // Monotone and smooth through the band.
            double prev = -1e-9;
            for (double s = 0.0; s <= 25.0; s += 0.5)
            {
                double g = ModeBComposer.LowSpeedGate(s);
                Assert.True(g >= prev, $"non-monotone at {s} km/h");
                prev = g;
            }
        }

        [Fact]
        public void UtilizationFloor_ZeroesNoiseAndKeepsTheLimit()
        {
            Assert.Equal(0.0, ModeBComposer.UtilizationFloor(0.0), 12);
            Assert.Equal(0.0, ModeBComposer.UtilizationFloor(0.04), 12);  // below floor
            Assert.Equal(1.0, ModeBComposer.UtilizationFloor(1.0), 12);   // limit preserved
            // Above the floor the curve is continuous from zero.
            double justOver = ModeBComposer.UtilizationFloor(0.06);
            Assert.InRange(justOver, 0.0, 0.02);
        }
    }
}
