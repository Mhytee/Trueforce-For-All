using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Load-weighted whole-car slip (issue #30): the formula the direct sources
    // (AC wheelSlip[] + wheelLoad[], Forza TireCombinedSlip[] + suspension
    // travel) fold their four tyres through before handing TractionLossEffect a
    // scalar. An unloaded wheel must contribute nothing; a four-wheel slide must
    // read full strength; and total-load-near-zero must take the caller's
    // fallback, not divide by ~0.
    public class SlipWeightingTests
    {
        private const double Min = 0.08;   // representative min-total-load

        [Fact]
        public void EqualLoads_ReturnsPlainMean()
        {
            // Equal weights collapse to the arithmetic mean of the slips.
            double r = SlipWeighting.Weighted(
                0.60, 0.61, 0.62, 0.63,
                0.5, 0.5, 0.5, 0.5, Min, fallback: -1);
            Assert.Equal(0.615, r, 6);
        }

        [Fact]
        public void FourWheelSlide_ReadsFullStrength()
        {
            // All tyres sliding equally: the weighting equals that common value,
            // so the effect's "full at 0.50" calibration is preserved.
            double r = SlipWeighting.Weighted(
                0.50, 0.50, 0.50, 0.50,
                0.4, 0.6, 0.3, 0.7, Min, fallback: -1);
            Assert.Equal(0.50, r, 6);
        }

        [Fact]
        public void UnloadedWheel_ContributesNothing()
        {
            // One wheel spun up (0.9) but carrying no load, three planted with
            // low slip: the reading follows the loaded three, not the spike.
            double r = SlipWeighting.Weighted(
                0.05, 0.05, 0.05, 0.90,
                0.5, 0.5, 0.5, 0.0, Min, fallback: -1);
            Assert.Equal(0.05, r, 6);   // (0.05*1.5) / 1.5
        }

        [Fact]
        public void AllUnloaded_ReturnsFallback()
        {
            // Summed load below the floor (airborne / unpopulated channel): the
            // caller's fallback wins instead of a divide-by-~0.
            double r = SlipWeighting.Weighted(
                0.9, 0.9, 0.9, 0.9,
                0.0, 0.0, 0.0, 0.0, Min, fallback: 0.42);
            Assert.Equal(0.42, r, 6);
        }

        [Fact]
        public void LoadUnitsCancel_NewtonsOrNormalized()
        {
            // Loads only appear as a ratio to their sum, so scaling every load by
            // a constant (e.g. normalized 0..1 vs newtons) leaves the result
            // unchanged. This is why one helper serves both AC and Forza.
            double normalized = SlipWeighting.Weighted(
                0.2, 0.4, 0.6, 0.8,
                0.1, 0.2, 0.3, 0.4, Min, fallback: -1);
            double newtons = SlipWeighting.Weighted(
                0.2, 0.4, 0.6, 0.8,
                1000, 2000, 3000, 4000, minTotalLoad: 40, fallback: -1);
            Assert.Equal(normalized, newtons, 6);
        }

        [Fact]
        public void NegativeLoad_ClampedNotSubtracted()
        {
            // A bad negative load sample must not cancel a real wheel's weight;
            // it clamps to 0 and simply drops out.
            double clamped = SlipWeighting.Weighted(
                0.5, 0.5, 0.5, 0.5,
                0.5, 0.5, 0.5, -0.5, Min, fallback: -1);
            double dropped = SlipWeighting.Weighted(
                0.5, 0.5, 0.5, 0.5,
                0.5, 0.5, 0.5, 0.0, Min, fallback: -1);
            Assert.Equal(dropped, clamped, 6);
        }

        [Fact]
        public void SlipMagnitudeUsed_SignIgnored()
        {
            // Slip is used by magnitude (lockup is negative, wheelspin positive):
            // a locked wheel contributes just like a spinning one.
            double r = SlipWeighting.Weighted(
                -0.40, 0.40, -0.40, 0.40,
                0.5, 0.5, 0.5, 0.5, Min, fallback: -1);
            Assert.Equal(0.40, r, 6);
        }
    }
}
