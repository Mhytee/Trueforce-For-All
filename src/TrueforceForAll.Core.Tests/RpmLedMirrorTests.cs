// What the rev strip SHOULD read, as opposed to what reached the wheel.
//
// The dash draws the wheel's strip on a phone, and drawing sends nothing to
// the hardware, so it is legitimate in exactly the frames where writing an LED
// is not: a game that lights its own wheel, Mode B disarmed, a rig with no
// channel open at all. LastLevel used to be assigned inside the write, which
// meant every one of those cases froze the mirror at whatever had last reached
// the wheel. These pin the split.

using System.Threading;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class RpmLedMirrorTests
    {
        // No wheel is ever opened here: the controller builds its channel but
        // never resolves it, which is the point. StripLength answers the family
        // default of ten until a wheel says otherwise.
        private static RpmLedController Fresh() => new RpmLedController(_ => { });

        [Fact]
        public void TracksTheBarWithTheGateShut()
        {
            var c = Fresh();
            c.OnFrame(0.5, 3000, 6000, redline: false, gateOpen: false);
            Assert.Equal(5, c.LastLevel);
        }

        [Fact]
        public void FollowsTheRevsDownAgainWithTheGateShut()
        {
            var c = Fresh();
            c.OnFrame(0.9, 5400, 6000, redline: false, gateOpen: false);
            Assert.Equal(9, c.LastLevel);

            // The regression: this used to hold at 9 for as long as the gate
            // stayed shut, leaving the on-screen strip lit through a whole
            // straight because the last thing the wheel was told was 9.
            c.OnFrame(0.2, 1200, 6000, redline: false, gateOpen: false);
            Assert.Equal(2, c.LastLevel);
        }

        [Fact]
        public void CountsInTheWheelsOwnSteps()
        {
            var c = Fresh();
            Assert.Equal(WheelLedChannel.LedCount, c.MirrorSteps);
            c.OnFrame(1.0, 6000, 6000, redline: false, gateOpen: false);
            Assert.Equal(c.MirrorSteps, c.LastLevel);
        }

        [Fact]
        public void UsesTheCarsSwitchOnPointsWhenItHasThem()
        {
            var c = Fresh();
            // Stand-in for the published per-car curve: three LEDs from 4000,
            // nothing below it. A percentage ramp would have lit five here.
            c.LevelCurve = (rpm, steps) => rpm >= 4000 ? 3 : 0;

            c.OnFrame(0.5, 3000, 6000, redline: false, gateOpen: false);
            Assert.Equal(0, c.LastLevel);

            c.OnFrame(0.7, 4200, 6000, redline: false, gateOpen: false);
            Assert.Equal(3, c.LastLevel);
        }

        [Fact]
        public void HoldsSteadyThroughTelemetryJitter()
        {
            var c = Fresh();
            c.OnFrame(0.50, 3000, 6000, redline: false, gateOpen: false);
            Assert.Equal(5, c.LastLevel);

            // Just over the boundary is not enough to move the bar: the mirror
            // gets the same hysteresis the wheel does, so a phone on the rim
            // does not strobe at a steady throttle.
            c.OnFrame(0.53, 3180, 6000, redline: false, gateOpen: false);
            Assert.Equal(5, c.LastLevel);
        }

        [Fact]
        public void BlinksTheWholeBarAtTheRedline()
        {
            var c = Fresh();
            // Both phases of the flash are levels the strip really shows, so
            // pin the pair rather than the phase: full bar or dark, never a
            // partial fill.
            c.OnFrame(1.0, 6000, 6000, redline: true, gateOpen: false);
            Assert.Contains(c.LastLevel, new[] { 0, c.MirrorSteps });
        }

        [Fact]
        public void CarriesTheRedlineBlinkWithTheGateShut()
        {
            // The dash draws its redline flash from the level, so the level has
            // to carry it in the frames the wheel never hears about. Sampled
            // across more than one half-period, both phases must appear.
            var c = Fresh();
            bool sawLit = false, sawDark = false;
            for (int i = 0; i < 400 && !(sawLit && sawDark); i++)
            {
                c.OnFrame(1.0, 6000, 6000, redline: true, gateOpen: false);
                if (c.LastLevel > 0) sawLit = true; else sawDark = true;
                Thread.Sleep(2);
            }
            Assert.True(sawLit, "the bar never lit at the redline");
            Assert.True(sawDark, "the bar never went dark, so the flash is missing");
        }

        [Fact]
        public void HoldsTheBarSolidForACarThatDoesNotBlink()
        {
            var c = Fresh();
            c.RedlineBlinks = false;
            for (int i = 0; i < 400; i++)
            {
                c.OnFrame(1.0, 6000, 6000, redline: true, gateOpen: false);
                Assert.Equal(c.MirrorSteps, c.LastLevel);
                Thread.Sleep(2);
            }
        }
    }
}
