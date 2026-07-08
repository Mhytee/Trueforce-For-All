using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 2 edge events: hysteresis gating, seed behavior, edge pairs.
    public class EventDeriverTests
    {
        private static TelemetryFrame Grip(double front, double rear) => new TelemetryFrame
        {
            FrontGrip01 = front,
            RearGrip01  = rear,
        };

        [Fact]
        public void Breakaway_StartAndEnd_WithHysteresis()
        {
            var d = new EventDeriver();
            Assert.Equal(FrameEvents.None, d.Derive(Grip(0.5, 0.4)));

            var ev = d.Derive(Grip(1.05, 0.4));
            Assert.True(ev.HasFlag(FrameEvents.FrontBreakawayStart));
            Assert.False(ev.HasFlag(FrameEvents.RearBreakawayStart));

            // Hovering between End (0.90) and Start (1.00): no events at all.
            Assert.Equal(FrameEvents.None, d.Derive(Grip(0.95, 0.4)));
            Assert.Equal(FrameEvents.None, d.Derive(Grip(1.02, 0.4)));   // still active, no re-Start

            ev = d.Derive(Grip(0.85, 0.4));
            Assert.True(ev.HasFlag(FrameEvents.FrontBreakawayEnd));

            // Re-crossing fires a fresh Start.
            ev = d.Derive(Grip(1.10, 0.4));
            Assert.True(ev.HasFlag(FrameEvents.FrontBreakawayStart));
        }

        [Fact]
        public void FrontAndRear_TrackIndependently()
        {
            var d = new EventDeriver();
            var ev = d.Derive(Grip(1.2, 1.2));
            Assert.True(ev.HasFlag(FrameEvents.FrontBreakawayStart));
            Assert.True(ev.HasFlag(FrameEvents.RearBreakawayStart));

            ev = d.Derive(Grip(0.5, 1.2));   // front recovers, rear still out
            Assert.True(ev.HasFlag(FrameEvents.FrontBreakawayEnd));
            Assert.False(ev.HasFlag(FrameEvents.RearBreakawayEnd));
        }

        [Fact]
        public void Lockup_And_Wheelspin_FromSlipRatios()
        {
            var d = new EventDeriver();
            var rolling = new TelemetryFrame { HasTireQuads = true, TireSlipRatio = TireQuad.Of(0, 0, 0, 0) };
            Assert.Equal(FrameEvents.None, d.Derive(rolling));

            // One front wheel locks under braking.
            var locked = new TelemetryFrame { HasTireQuads = true, TireSlipRatio = TireQuad.Of(-0.8, -0.1, 0, 0) };
            Assert.True(d.Derive(locked).HasFlag(FrameEvents.LockupStart));
            Assert.True(d.Derive(rolling).HasFlag(FrameEvents.LockupEnd));

            // Rears light up on power.
            var spinning = new TelemetryFrame { HasTireQuads = true, TireSlipRatio = TireQuad.Of(0, 0, 0.9, 0.9) };
            Assert.True(d.Derive(spinning).HasFlag(FrameEvents.WheelspinStart));
            Assert.True(d.Derive(rolling).HasFlag(FrameEvents.WheelspinEnd));
        }

        [Fact]
        public void Gear_SeedsSilently_ThenFiresOnChange()
        {
            var d = new EventDeriver();
            // Waking up mid-drive in 3rd: no phantom shift event.
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { Gear = "3" }));
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { Gear = "3" }));
            Assert.True(d.Derive(new TelemetryFrame { Gear = "4" }).HasFlag(FrameEvents.GearChanged));
            // Frames without a gear reading change nothing.
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { Gear = null }));
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { Gear = "4" }));
        }

        [Fact]
        public void RumbleAndAirborne_BoolEdges()
        {
            var d = new EventDeriver();
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { OnRumbleStrip = false }));
            Assert.True(d.Derive(new TelemetryFrame { OnRumbleStrip = true }).HasFlag(FrameEvents.RumbleStripStart));
            Assert.Equal(FrameEvents.None, d.Derive(new TelemetryFrame { OnRumbleStrip = true }));
            Assert.True(d.Derive(new TelemetryFrame { OnRumbleStrip = false }).HasFlag(FrameEvents.RumbleStripEnd));

            Assert.True(d.Derive(new TelemetryFrame { Airborne = true }).HasFlag(FrameEvents.AirborneStart));
            Assert.True(d.Derive(new TelemetryFrame { Airborne = false }).HasFlag(FrameEvents.AirborneEnd));
        }

        [Fact]
        public void NullChannels_NeverFire()
        {
            var d = new EventDeriver();
            var empty = new TelemetryFrame();   // no rollups, no quads, no gear, no bools
            for (int i = 0; i < 10; i++)
                Assert.Equal(FrameEvents.None, d.Derive(empty));
        }
    }
}
