using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tests the per-variant signature RPM banding. The headline behaviour the
    // current design depends on: MaxRpm bands tight (50, near-exact) vs redline
    // (500) so a small Forza upgrade that nudges the rev ceiling lands in its
    // own community bucket instead of being merged with the stock build, while
    // float noise on an identical build still pools.
    public class VariantSignatureMathTests
    {
        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(100.0, 0)]
        [InlineData(499.9, 0)]      // below the floor: component is dropped
        [InlineData(500.0, 500)]
        [InlineData(7400.0, 7400)]
        [InlineData(7430.0, 7450)]  // 148.6 -> 149
        [InlineData(7700.0, 7700)]
        [InlineData(8000.0, 8000)]
        [InlineData(8100.0, 8100)]
        [InlineData(8140.0, 8150)]  // 162.8 -> 163
        public void BandMaxRpm_BandsToNearest50(double rpm, int expected)
        {
            Assert.Equal(expected, VariantSignatureMath.BandMaxRpm(rpm));
        }

        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(499.9, 0)]
        [InlineData(500.0, 500)]
        [InlineData(7400.0, 7500)]
        [InlineData(7700.0, 7500)]  // 15.40 -> 15, still 7500 at the looser band
        [InlineData(8000.0, 8000)]
        public void BandRedline_BandsToNearest500(double rpm, int expected)
        {
            Assert.Equal(expected, VariantSignatureMath.BandRedline(rpm));
        }

        [Fact]
        public void MaxRpmBand_SeparatesSmallUpgrade_ThatRedlineBandWouldMerge()
        {
            // ~150 RPM apart (a Forza race-piston / minor engine upgrade). The
            // 50 MaxRpm band gives the two builds distinct community buckets;
            // the 500 redline band merges them, and the old 250 MaxRpm band
            // would have too. This finer separation is the point of the 50 band.
            const double stock = 7400.0;
            const double upgraded = 7550.0;
            Assert.NotEqual(
                VariantSignatureMath.BandMaxRpm(stock),
                VariantSignatureMath.BandMaxRpm(upgraded));
            Assert.Equal(
                VariantSignatureMath.BandRedline(stock),
                VariantSignatureMath.BandRedline(upgraded));
        }

        [Fact]
        public void IdenticalBuild_WithFloatNoise_PoolsToSameBand()
        {
            // MaxRpm is a deterministic engine spec; two users on the same build
            // report the same value (modulo float representation). The band must
            // not split them apart.
            Assert.Equal(
                VariantSignatureMath.BandMaxRpm(7500.0),
                VariantSignatureMath.BandMaxRpm(7500.49));
        }

        [Theory]
        [InlineData(500.0)]
        [InlineData(8000.0)]
        [InlineData(8100.0)]
        [InlineData(9333.0)]
        public void BandMaxRpm_OutputIsAlwaysAMultipleOfTheBand(double rpm)
        {
            Assert.Equal(0, VariantSignatureMath.BandMaxRpm(rpm) % VariantSignatureMath.MaxRpmBandRpm);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(250.0)]
        [InlineData(499.99)]
        public void BelowFloor_BothAxesDropTheComponent(double rpm)
        {
            Assert.Equal(0, VariantSignatureMath.BandMaxRpm(rpm));
            Assert.Equal(0, VariantSignatureMath.BandRedline(rpm));
        }
    }
}
