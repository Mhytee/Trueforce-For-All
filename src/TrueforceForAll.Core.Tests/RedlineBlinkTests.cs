using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>Whether the redline flashes is a THIRD state, not a rate of zero.
    /// The published data distinguishes "blinks every N ms" from "does not
    /// blink", and 470 of its 717 cars are the second. Folding those together
    /// made a Formula Vee, whose real dash holds steady, flash on the rim.
    ///
    /// LastLevel is what the strip WOULD show, computed whether or not a wheel
    /// is present, which is what makes this testable with no hardware.</summary>
    public class RedlineBlinkTests
    {
        private static RpmLedController Make() => new RpmLedController(_ => { });

        [Fact]
        public void ACarThatDoesNotBlinkHoldsTheBarSolidAtRedline()
        {
            var c = Make();
            c.RedlineBlinks = false;

            // Sampled repeatedly: a blink would put a zero in here sooner or
            // later, and every frame being full is the whole assertion.
            for (int i = 0; i < 50; i++)
            {
                c.OnFrame(1.0, 8000, 8000, redline: true, gateOpen: true);
                Assert.True(c.LastLevel > 0, "frame " + i + " went dark; the bar blinked");
            }
        }

        [Fact]
        public void TheFlashStartsDarkSoItLinesUpWithTheSimsOwnDash()
        {
            var c = Make();

            // The very first frame of a redline burst must be DARK. Approaching
            // redline the bar is already fully lit by the rev ramp, so the first
            // thing that CHANGES on the sim's dash is it going out; opening our
            // own burst lit repeats a state the driver already sees and leaves us
            // half a period behind for the rest of it.
            c.OnFrame(1.0, 8000, 8000, redline: true, gateOpen: true);
            Assert.Equal(0, c.LastLevel);

            // And again after dropping out of redline: each burst gets its own
            // phase rather than inheriting whatever the clock was doing.
            c.OnFrame(0.5, 4000, 8000, redline: false, gateOpen: true);
            c.OnFrame(1.0, 8000, 8000, redline: true, gateOpen: true);
            Assert.Equal(0, c.LastLevel);
        }

        [Fact]
        public void BlinkingIsTheDefaultSoAnUncoveredCarIsUnchanged()
        {
            Assert.True(Make().RedlineBlinks);
        }

        [Fact]
        public void TheHalfPeriodStillDefaultsToIRacingsRate()
        {
            Assert.Equal(185, Make().RedlineBlinkHalfMs);
        }
    }
}
