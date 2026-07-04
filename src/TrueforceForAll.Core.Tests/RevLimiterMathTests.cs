using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tests the community-redline plausibility band. This gate is the only
    // sanity check on the crowd-sourced redline that drives the rev-limiter
    // engage point on the Forza-style case (no telemetry redline), so it has
    // to reject typos / wrong-car mappings / modded outliers (caller then
    // falls through to the 0.85x default) while accepting every real value.
    public class RevLimiterMathTests
    {
        // Matches RevLimiterEffect.MinEngineRpm.
        private const double Min = 100.0;

        [Theory]
        [InlineData(7800.0, 8000.0, true)]   // typical Forza redline just under the ceiling
        [InlineData(8000.0, 8000.0, true)]   // right at the ceiling
        [InlineData(8400.0, 8000.0, true)]   // 1.05x ceiling, inclusive
        [InlineData(8500.0, 8000.0, false)]  // above the ceiling -> reject
        [InlineData(17000.0, 8000.0, false)] // typo / wrong-car mapping -> reject (buzz would never fire)
        [InlineData(4000.0, 8000.0, true)]   // 0.5x floor, inclusive
        [InlineData(3500.0, 8000.0, false)]  // below the floor -> reject
        [InlineData(2000.0, 8000.0, false)]  // garbage-low -> reject (would buzz constantly)
        [InlineData(50.0, 8000.0, false)]    // at/under min engine rpm -> reject
        public void Plausibility_AcceptsWithinBand_RejectsOutside(double redline, double maxRpm, bool expected)
        {
            Assert.Equal(expected, RevLimiterMath.IsCommunityRedlinePlausible(redline, maxRpm, Min));
        }

        [Theory]
        [InlineData(5000.0)]
        [InlineData(25000.0)] // no ceiling to band against, so even an absurd value is accepted here
        public void UnknownMaxRpm_AcceptsAnythingAboveMinEngine(double redline)
        {
            // maxRpm <= Min means the game gave us no rev ceiling; the band
            // can't be formed, so we don't reject (same escape the telemetry
            // path uses). The absolute 500..25000 bound is enforced upstream
            // at submit time, not here.
            Assert.True(RevLimiterMath.IsCommunityRedlinePlausible(redline, 0.0, Min));
        }

        [Fact]
        public void UnknownMaxRpm_StillRejectsAtOrBelowMinEngine()
        {
            Assert.False(RevLimiterMath.IsCommunityRedlinePlausible(50.0, 0.0, Min));
        }

        [Fact]
        public void BandScalesWithMaxRpm_LowRevvingEngine()
        {
            // A low-revving engine (e.g. a big diesel reported at 5000 max):
            // its real redline near 4800 is accepted, and a value plausible
            // for a high-revving engine (9000) is correctly rejected for it.
            Assert.True(RevLimiterMath.IsCommunityRedlinePlausible(4800.0, 5000.0, Min));
            Assert.False(RevLimiterMath.IsCommunityRedlinePlausible(9000.0, 5000.0, Min));
        }
    }
}
