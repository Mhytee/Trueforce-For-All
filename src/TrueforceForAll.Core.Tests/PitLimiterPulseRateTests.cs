// The pit-limiter pulse rate taken from a car's own published blink rate.
//
// This is how a per-car cadence reaches the driver in games where we never
// touch the lights: flashing needs a stream of level writes and that cuts the
// game's force on the shared pipe, but a RATE is just a number and the wheel
// can pulse it for nothing.

using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class PitLimiterPulseRateTests
    {
        // The rate actually rendered, read back through the same clamp the
        // renderer uses. Exposed via reflection because it is an implementation
        // detail rather than API, but it is the thing worth pinning.
        private static float Effective(PitLimiterEffect e)
        {
            var p = typeof(PitLimiterEffect).GetProperty("EffectivePulseFreq",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (float)p.GetValue(e);
        }

        [Fact]
        public void WithoutPublishedDataTheUsersOwnSettingIsUsed()
        {
            var e = new PitLimiterEffect { PulseFreq = 6.0f, PublishedPulseHz = null };
            Assert.Equal(6.0f, Effective(e));
        }

        [Fact]
        public void APublishedRateOverridesTheDefault()
        {
            // 250 ms between blinks is 4 Hz.
            var e = new PitLimiterEffect { PulseFreq = 6.0f, PublishedPulseHz = 4.0f };
            Assert.Equal(4.0f, Effective(e));
        }

        [Theory]
        // Real published intervals from the dataset, converted to Hz: 50 ms is
        // 20 Hz and 500 ms is 2 Hz, so both ends of the real range are covered.
        [InlineData(20.0f, 12.0f)]   // too fast to feel as separate pulses
        [InlineData(2.0f, 2.0f)]     // the slow end, unchanged
        [InlineData(0.5f, 2.0f)]     // slower than a pulse train reads as
        public void ExtremeRatesAreClampedToWhatAWheelCanRender(float published, float expected)
        {
            var e = new PitLimiterEffect { PulseFreq = 6.0f, PublishedPulseHz = published };
            Assert.Equal(expected, Effective(e));
        }

        [Theory]
        // The slider runs to 15 Hz, and the loader accepts values from 1.0. The
        // clamp is for PUBLISHED figures only: capping the user's own setting
        // would silently take the top of their slider away, on a preset-scoped
        // value, for someone who never enabled any of this.
        [InlineData(15.0f)]
        [InlineData(13.5f)]
        [InlineData(1.0f)]
        public void TheUsersOwnSettingIsNeverClamped(float userValue)
        {
            var e = new PitLimiterEffect { PulseFreq = userValue, PublishedPulseHz = null };
            Assert.Equal(userValue, Effective(e));
        }

        [Fact]
        public void ClearingThePublishedRateHandsControlBack()
        {
            // Leaving a car whose data set a rate must not strand the effect on
            // that car's cadence.
            var e = new PitLimiterEffect { PulseFreq = 7.5f, PublishedPulseHz = 3.0f };
            Assert.Equal(3.0f, Effective(e));
            e.PublishedPulseHz = null;
            Assert.Equal(7.5f, Effective(e));
        }
    }
}
