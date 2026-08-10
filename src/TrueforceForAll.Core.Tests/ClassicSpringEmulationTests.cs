using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Classic-spring emulation: FS25-class games command force as a parametric
    // HIGH-RESOLUTION SPRING (type 0x0b) instead of streaming a value, and the
    // wheel firmware renders it from its own position. In Trueforce mode the
    // firmware doesn't, so the tap captures the parameters and the plugin
    // evaluates them against the physical wheel position.
    //
    // Every corpus byte shape here is from the FS25/G923-PS reporter capture
    // (usb-trace.pcap, PID C266, 2026-08-06), where the spring's dead band
    // visibly tracks the game's steering target:
    //
    //   21 0b 87 94 00 ae ff   slot 2, dl+play, band right of center, k=0
    //   21 0b 00 08 11 20 ff   band at full left, k=1 both sides
    //   21 0b 80 80 88 00 ff   FH5's centered auto-center spring (276x there)
    //
    // Position space: steerNorm -1..1 maps to 0..1, dead band edges are 11-bit
    // over the same range. Sign contract (matches the stationary spring's
    // hardware-validated convention): wheel RIGHT of the band pulls LEFT =
    // POSITIVE force.
    public class ClassicSpringEmulationTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;

        private static byte[] Record(params byte[] report)
        {
            const int HeaderLen = 27;
            var p = new byte[HeaderLen + report.Length];
            p[0]  = HeaderLen;
            p[19] = (byte)(Dev & 0xff);
            p[20] = (byte)(Dev >> 8);
            p[21] = 0x01;                      // ep1 OUT
            p[22] = 0x01;                      // interrupt transfer
            Array.Copy(report, 0, p, HeaderLen, report.Length);
            return p;
        }

        private static byte[] Capture(params byte[][] records)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0xa1b2c3d4u);
                w.Write(new byte[12]);
                w.Write(65535);
                w.Write(DltUsbPcap);
                foreach (var r in records)
                {
                    w.Write(0); w.Write(0); w.Write(r.Length); w.Write(r.Length);
                    w.Write(r);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static UsbPcapFfbTap Run(params byte[][] records)
        {
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev);
            Feed(tap, records);
            return tap;
        }

        private static void Feed(UsbPcapFfbTap tap, params byte[][] records)
        {
            using (var s = new MemoryStream(Capture(records)))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
        }

        // The FS25 corpus packet: dead band d1=1087 d2=1189 (0.531..0.581 of
        // range, just right of center), k1=k2=0 (gentlest slope), S bits 0,
        // clip 0xff (no cap).
        private static readonly byte[] Fs25Spring = { 0x21, 0x0b, 0x87, 0x94, 0x00, 0xae, 0xff };

        [Fact]
        public void Fs25Spring_DownloadAndPlay_IsCapturedAndPlaying()
        {
            var tap = Run(Record(Fs25Spring));
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.Equal(1, tap.SpringUpdatesCaptured);
            // A spring never publishes a scalar force target.
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void RightOfBand_PullsLeft_Positive()
        {
            var tap = Run(Record(Fs25Spring));
            // steerNorm 0.8 -> p=0.9, dev over d2(0.581) = 0.319, k2=0 ->
            // slope 1 -> force fraction 0.319 -> about +10460.
            short? f = tap.TryEvaluateClassicSprings(0.8f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)9500, (short)11500);
        }

        [Fact]
        public void LeftOfBand_PullsRight_Negative()
        {
            var tap = Run(Record(Fs25Spring));
            // steerNorm -0.8 -> p=0.1, dev under d1(0.531) = 0.431 -> about -14130.
            short? f = tap.TryEvaluateClassicSprings(-0.8f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)-15200, (short)-13000);
        }

        [Fact]
        public void InsideDeadBand_IsZeroForce_NotNull()
        {
            var tap = Run(Record(Fs25Spring));
            // p=0.556 (steerNorm 0.112) sits inside 0.531..0.581. A playing
            // spring commanding no force is a real zero, distinct from "no
            // spring": downstream must treat the game as owning the wheel.
            short? f = tap.TryEvaluateClassicSprings(0.112f);
            Assert.True(f.HasValue);
            Assert.Equal((short)0, f.Value);
        }

        [Fact]
        public void SlopeNibble_DoublesForcePerStep()
        {
            // Corpus packet: band 0..65 (full left), k1=k2=1 -> slope 2.
            var tap = Run(Record(new byte[] { 0x21, 0x0b, 0x00, 0x08, 0x11, 0x20, 0xff }));
            // p=0.5, dev = 0.5 - 0.0318 = 0.468 -> x2 = 0.937 -> about +30700.
            short? f = tap.TryEvaluateClassicSprings(0f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)29000, (short)32500);
        }

        [Fact]
        public void Clip_CapsTheForce()
        {
            // Band at full left, k=4 both sides (slope 16), clip 0x40 (0.251).
            var tap = Run(Record(new byte[] { 0x21, 0x0b, 0x00, 0x00, 0x44, 0x00, 0x40 }));
            // p=0.9: dev*16 is way past the clip; force must cap at 0.251.
            short? f = tap.TryEvaluateClassicSprings(0.8f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)7900, (short)8600);
        }

        [Fact]
        public void Stop_ClearsTheSpring()
        {
            var tap = Run(Record(Fs25Spring),
                          Record(new byte[] { 0x23, 0x00, 0, 0, 0, 0, 0 }));   // slot 2 STOP
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
        }

        [Fact]
        public void BareDownload_DoesNotPlay()
        {
            // Command 0 loads the spring without playing it: no force, and it
            // must not count as captured FFB (a game that never plays anything
            // must not disarm the no-FFB watchdog).
            var tap = Run(Record(new byte[] { 0x20, 0x0b, 0x87, 0x94, 0x00, 0xae, 0xff }));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
            Assert.Equal(0, tap.SpringUpdatesCaptured);
        }

        [Fact]
        public void BareDownload_ThenPlay_ArmsTheSpring()
        {
            // Same capture, download then PLAY: the spring engages.
            var tap = Run(Record(new byte[] { 0x20, 0x0b, 0x87, 0x94, 0x00, 0xae, 0xff }),
                          Record(new byte[] { 0x22, 0x00, 0, 0, 0, 0, 0 }));   // slot 2 PLAY
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.NotNull(tap.TryEvaluateClassicSprings(0.8f));
        }

        [Fact]
        public void RefreshForce_RetunesThePlayingSpring()
        {
            // FS25's servo: dl+play once, then the band moves. After a refresh
            // that shifts the band to full left, a centered wheel must feel a
            // leftward (positive) pull instead of sitting inside the old band.
            var tap = Run(Record(Fs25Spring),
                          Record(new byte[] { 0x2c, 0x0b, 0x00, 0x08, 0x11, 0x20, 0xff }));
            short? f = tap.TryEvaluateClassicSprings(0.112f);   // inside the OLD band
            Assert.True(f.HasValue);
            Assert.True(f.Value > 20000, $"expected a strong left pull, got {f.Value}");
        }

        [Fact]
        public void VariableForce_OverwritingTheSlot_EndsTheSpring()
        {
            // A later variable-force download into the same slot replaces the
            // spring; keeping both would double-command the wheel.
            var tap = Run(Record(Fs25Spring),
                          Record(new byte[] { 0x21, 0x08, 0xC0, 0, 0, 0, 0 }));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void SpringAndVariable_InDifferentSlots_Coexist()
        {
            // Slot 1 variable force, slot 2 spring: the scalar path and the
            // spring path answer independently.
            var tap = Run(Record(new byte[] { 0x11, 0x08, 0xC0, 0, 0, 0, 0 }),
                          Record(Fs25Spring));
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.NotNull(tap.TryEvaluateClassicSprings(0.8f));
        }

        [Fact]
        public void ClearLastFfbTarget_DropsTheSpringImmediately()
        {
            // The plugin clears the target while it holds the stream stopped
            // for a pause, and a paused game may send nothing at all. The
            // spring must die on the clear itself, not at the next parsed
            // command, or emulated force keeps flowing through the pause.
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev);
            Feed(tap, Record(Fs25Spring));
            Assert.True(tap.AnyClassicSpringPlaying);

            tap.ClearLastFfbTarget();
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));

            // And a bare PLAY after the pause must not resurrect it: the
            // parser-side reset runs at the next command.
            Feed(tap, Record(new byte[] { 0x22, 0x00, 0, 0, 0, 0, 0 }));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
        }

        [Fact]
        public void SimulateNoFfbCapture_SuppressesSpringOutput()
        {
            var tap = Run(Record(Fs25Spring));
            tap.SimulateNoFfbCapture = true;
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
        }

        [Fact]
        public void OtherForceTypes_AreStillNotGuessedAt()
        {
            // Type 0x0e (high-res auto-center) stays undecoded: no scalar, no
            // spring, no invented force.
            var tap = Run(Record(new byte[] { 0x21, 0x0e, 0x80, 0x80, 0x88, 0x00, 0xff }));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryEvaluateClassicSprings(0.8f));
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.Equal(0, tap.SpringUpdatesCaptured);
        }

        [Fact]
        public void SideInvertBits_FlipTheirSide()
        {
            // Same as the corpus spring but with S2 set (bit 4 of byte 5):
            // right of band now pushes AWAY (negative) instead of toward.
            var tap = Run(Record(new byte[] { 0x21, 0x0b, 0x87, 0x94, 0x00, 0xbe, 0xff }));
            short? f = tap.TryEvaluateClassicSprings(0.8f);
            Assert.True(f.HasValue);
            Assert.True(f.Value < 0, $"expected an inverted (negative) force, got {f.Value}");
        }
    }
}
