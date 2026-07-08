using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Forward-path gap bridge: masks short raceOn=0 windows (replay loops,
    // rewinds) so SimHub never sees a disconnect, while preserving car
    // identity so no phantom car-change events fire.
    public class ForzaGapBridgeTests
    {
        private const int Len = 331;             // FM8 packet
        private const double Tps = 1000.0;       // 1 tick = 1 ms in these tests

        private static byte[] LivePacket()
        {
            var b = new byte[Len];
            b[0] = 1;                                            // raceOn
            BitConverter.GetBytes(8200f).CopyTo(b, 8);           // maxRpm
            BitConverter.GetBytes(900f).CopyTo(b, 12);           // idleRpm
            BitConverter.GetBytes(2471).CopyTo(b, 212);          // carOrdinal
            BitConverter.GetBytes(6).CopyTo(b, 216);             // carClass
            BitConverter.GetBytes(870).CopyTo(b, 220);           // carPI
            BitConverter.GetBytes(1).CopyTo(b, 224);             // drivetrain
            BitConverter.GetBytes(8).CopyTo(b, 228);             // cylinders
            BitConverter.GetBytes(161.4f).CopyTo(b, 256);        // some dynamics
            return b;
        }

        private static byte[] GapPacket()
        {
            var b = new byte[Len];                               // all zero, like the game sends
            BitConverter.GetBytes(12345u).CopyTo(b, 4);          // timestamp keeps ticking
            return b;
        }

        [Fact]
        public void LivePackets_PassThroughUntouched()
        {
            var bridge = new ForzaGapBridge();
            var p = LivePacket();
            var orig = (byte[])p.Clone();
            Assert.False(bridge.Process(p, Len, 0, Tps));
            Assert.Equal(orig, p);
        }

        [Fact]
        public void ShortGap_PatchedToConnectedIdle_WithCarIdentity()
        {
            var bridge = new ForzaGapBridge();
            bridge.Process(LivePacket(), Len, 0, Tps);

            var gap = GapPacket();
            Assert.True(bridge.Process(gap, Len, 3000, Tps));    // 3 s into the gap

            Assert.Equal(1, gap[0]);                                          // raceOn restored
            Assert.Equal(8200f, BitConverter.ToSingle(gap, 8));               // rpm range back
            Assert.Equal(900f, BitConverter.ToSingle(gap, 12));
            Assert.Equal(2471, BitConverter.ToInt32(gap, 212));               // same car — no
            Assert.Equal(8, BitConverter.ToInt32(gap, 228));                  // phantom car change
            Assert.Equal(12345u, BitConverter.ToUInt32(gap, 4));              // its own timestamp
            Assert.Equal(0f, BitConverter.ToSingle(gap, 256));                // dynamics stay zero
        }

        [Fact]
        public void LongGap_PassesHonestDisconnect()
        {
            var bridge = new ForzaGapBridge();                   // MaxGapSeconds = 15
            bridge.Process(LivePacket(), Len, 0, Tps);
            var gap = GapPacket();
            Assert.False(bridge.Process(gap, Len, 16_000, Tps));
            Assert.Equal(0, gap[0]);
        }

        [Fact]
        public void GapClock_ResetsOnEveryLivePacket()
        {
            var bridge = new ForzaGapBridge();
            bridge.Process(LivePacket(), Len, 0, Tps);
            bridge.Process(LivePacket(), Len, 60_000, Tps);      // still racing a minute later
            Assert.True(bridge.Process(GapPacket(), Len, 62_000, Tps));
        }

        [Fact]
        public void NoLivePacketSeen_NeverBridges()
        {
            var bridge = new ForzaGapBridge();
            var gap = GapPacket();
            Assert.False(bridge.Process(gap, Len, 100, Tps));
            Assert.Equal(0, gap[0]);
        }

        [Fact]
        public void LengthChange_MeansNewLayout_NoBridge()
        {
            // FM8 (331) snapshot must never patch a Horizon (324) packet.
            var bridge = new ForzaGapBridge();
            bridge.Process(LivePacket(), Len, 0, Tps);
            var gap = new byte[324];
            Assert.False(bridge.Process(gap, 324, 1000, Tps));
        }

        [Fact]
        public void Disabled_PassesEverythingThrough()
        {
            var bridge = new ForzaGapBridge { Enabled = false };
            bridge.Process(LivePacket(), Len, 0, Tps);
            var gap = GapPacket();
            Assert.False(bridge.Process(gap, Len, 1000, Tps));
            Assert.Equal(0, gap[0]);
        }
    }
}
