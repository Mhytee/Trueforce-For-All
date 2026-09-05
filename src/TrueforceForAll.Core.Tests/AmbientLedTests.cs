// The rim LEDs driven by something that is not the revs.
//
// The level write has to land on the wheel's OWN strip and leave the rev
// path's latches alone, so handing the bar back to the revs starts from the
// revs.
//
// The two signals' own shapes live in LedSweepTests and
// AudioLevelEnvelopeTests; this file is the write path they share.

using TrueforceForAll.Core;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class AmbientLedTests
    {
        // The meter half lives in AudioLevelEnvelopeTests; the level maths
        // there is where the two failed designs are pinned. What is left here is
        // the write path both ambient modes share.

        // No wheel is opened here: the controller builds its channel and never
        // resolves it, so StripLength answers the family default of ten.
        private static RpmLedController Fresh() => new RpmLedController(_ => { });

        [Fact]
        public void ScalesTheMeterOntoTheWheelsOwnStrip()
        {
            var c = Fresh();
            c.OnAmbientFrame(0.0, gateOpen: false);
            Assert.Equal(0, c.LastLevel);

            c.OnAmbientFrame(0.5, gateOpen: false);
            Assert.Equal(c.MirrorSteps / 2, c.LastLevel);

            c.OnAmbientFrame(1.0, gateOpen: false);
            Assert.Equal(c.MirrorSteps, c.LastLevel);
        }

        [Fact]
        public void ClampsWhateverItIsHanded()
        {
            var c = Fresh();
            c.OnAmbientFrame(-3.0, gateOpen: false);
            Assert.Equal(0, c.LastLevel);
            c.OnAmbientFrame(9.0, gateOpen: false);
            Assert.Equal(c.MirrorSteps, c.LastLevel);
            c.OnAmbientFrame(double.NaN, gateOpen: false);
            Assert.Equal(0, c.LastLevel);
        }

        [Fact]
        public void MovesEveryFrameWhereTheRevBarWouldHoldSteady()
        {
            // The rev ramp needs about half an LED past a boundary before it
            // changes, so telemetry jitter does not strobe the strip. A meter
            // is SUPPOSED to move, and the same latch applied to it would make
            // it sticky.
            var c = Fresh();
            c.OnAmbientFrame(0.50, gateOpen: false);
            Assert.Equal(5, c.LastLevel);
            c.OnAmbientFrame(0.53, gateOpen: false);
            Assert.Equal(5, c.LastLevel);   // still rounds to five, no latch involved
            c.OnAmbientFrame(0.56, gateOpen: false);
            Assert.Equal(6, c.LastLevel);   // the ramp's hysteresis would have held five
        }

        [Fact]
        public void HandingTheBarBackToTheRevsStartsFromTheRevs()
        {
            // The regression this guards: the meter leaving the rev ramp's
            // hysteresis latch parked at its own last level, so the first rev
            // frame after an arcade game closed drew the music rather than the
            // engine.
            var c = Fresh();
            c.OnAmbientFrame(0.9, gateOpen: false);
            Assert.Equal(9, c.LastLevel);

            c.OnFrame(0.2, 1200, 6000, redline: false, gateOpen: false);
            Assert.Equal(2, c.LastLevel);
        }

        [Fact]
        public void NeverFlashesARedline()
        {
            // A meter has no redline. Held at the top across more than one
            // blink period, the bar must stay full rather than strobing.
            var c = Fresh();
            for (int i = 0; i < 200; i++)
            {
                c.OnAmbientFrame(1.0, gateOpen: false);
                Assert.Equal(c.MirrorSteps, c.LastLevel);
                System.Threading.Thread.Sleep(2);
            }
        }

        [Fact]
        public void CountsInTheSameStepsTheMirrorDraws()
        {
            var c = Fresh();
            Assert.Equal(WheelLedChannel.LedCount, c.MirrorSteps);
        }
    }
}
