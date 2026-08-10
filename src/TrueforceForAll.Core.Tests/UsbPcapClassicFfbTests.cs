using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The classic Logitech slot protocol is the G923 PS/PC (C266) wheel's ONLY
    // force path: its captures contain zero HID++ writes, so none of the
    // HID++-side hardening applies to it. Every byte shape asserted here was
    // taken from the owner's own captures (FH5-G923-PS.pcapng and
    // ACC-G923-PS-CLEAN.pcapng, PID C266, 2026-05-17):
    //
    //   11 08 <force> ...   5955x  slot 1, download-and-play, variable force
    //   13 00 00 ...         109x  slot 1, STOP force
    //   21 0b 80 80 ...      276x  slot 2, download-and-play, double constant
    //   14 00 / f5 00          5x  default spring on / off
    //   f8 12 <mask> ...   12984x  extended command (rev LEDs), ACC capture
    //   01 00 ...         207378x  our own ep3 Trueforce stream, ACC capture
    //
    // The regression this pins: STOP used to be invisible, so the tap replayed
    // the last downloaded force for up to FfbTargetMaxAgeMs (10 s) after the
    // game had cancelled it.
    public class UsbPcapClassicFfbTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;

        // One interrupt-OUT record on ep1 carrying the given classic report.
        private static byte[] Record(params byte[] report)
        {
            const int HeaderLen = 27;
            var p = new byte[HeaderLen + report.Length];
            p[0]  = HeaderLen;                 // headerLen (u16 LE)
            p[19] = (byte)(Dev & 0xff);        // device address (u16 LE)
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
            using (var s = new MemoryStream(Capture(records)))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
            return tap;
        }

        // A 7-byte classic report, the length the wheel actually uses.
        private static byte[] Cmd(byte b0, byte b1, byte b2 = 0)
            => new byte[] { b0, b1, b2, 0, 0, 0, 0 };

        [Fact]
        public void DownloadAndPlay_LatchesForce()
        {
            // 0xC0 - 0x80 = +64, normalized to the int16 scale the HID++ path uses.
            var tap = Run(Record(Cmd(0x11, 0x08, 0xC0)));
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void Stop_CommandsZero_InsteadOfReplayingTheStaleForce()
        {
            // The bug: a strong left force downloaded, then stopped by the game.
            // The tap used to keep handing back 0x20's force for up to 10 s.
            var tap = Run(Record(Cmd(0x11, 0x08, 0x20)),    // -96 (hard left)
                          Record(Cmd(0x13, 0x00)));         // slot 1 STOP
            Assert.Equal((short)0, tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void Stop_OnlyClearsTheSlotItNames()
        {
            // Stopping slot 2 must not cancel the force playing in slot 1.
            var tap = Run(Record(Cmd(0x11, 0x08, 0xC0)),    // slot 1: +64
                          Record(Cmd(0x23, 0x00)));         // slot 2 STOP
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void Play_AfterStop_RestoresTheDownloadedForce()
        {
            var tap = Run(Record(Cmd(0x11, 0x08, 0xC0)),
                          Record(Cmd(0x13, 0x00)),          // stop
                          Record(Cmd(0x12, 0x00)));         // slot 1 PLAY
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void BareDownload_DoesNotCommandForce()
        {
            // Command 0 loads the slot without playing it. Treating it as live
            // would command a force the wheel is not rendering.
            var tap = Run(Record(Cmd(0x10, 0x08, 0xC0)));
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void UndecodedForceType_ContributesNothing_AndIsNotGuessedAt()
        {
            // 21 0b: slot 2, double constant. We cannot decode it, so it must
            // neither invent a force nor disturb the slot we can decode.
            var tap = Run(Record(Cmd(0x11, 0x08, 0xC0)),
                          Record(new byte[] { 0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff }));
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void DefaultSpringAndExtendedCommands_AreNotForceWrites()
        {
            // 14 00 / f5 00 = default spring on / off, f8 12 = rev LEDs.
            // None of them may latch or clear a force.
            var tap = Run(Record(Cmd(0x14, 0x00)),
                          Record(Cmd(0xf5, 0x00)),
                          Record(new byte[] { 0xf8, 0x12, 0x1f, 0, 0, 0, 0 }));
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.Equal(0, tap.FfbSamplesCaptured);
        }

        [Fact]
        public void OurOwnTrueforceStream_IsNeverParsedAsAClassicCommand()
        {
            // Our ep3 report starts 0x01, which is slot mask 0. Feeding it into
            // the slot machine would make the plugin tap its own output.
            var ep3 = new byte[64];
            ep3[0] = 0x01; ep3[4] = 0x01; ep3[6] = 0x34; ep3[7] = 0x92;
            ep3[10] = 0x04; ep3[11] = 0x0d;
            var tap = Run(Record(ep3));
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.Equal(0, tap.FfbSamplesCaptured);
        }

        [Fact]
        public void HidppInterruptReports_DoNotDriveTheSlotMachine()
        {
            // Report IDs 0x10 / 0x11 / 0x12 collide with slot-1 download /
            // download-and-play / play. Byte 1 is the HID++ device index (0xff),
            // never a classic force type, and that is what separates them. If
            // this leaks, the Xbox G923's real FFB gets clobbered by a
            // phantom slot-1 state.
            var hidpp = new byte[20];
            hidpp[0] = 0x11; hidpp[1] = 0xff; hidpp[2] = 0x0b; hidpp[3] = 0x2d;
            hidpp[10] = 0x40; hidpp[11] = 0x00;        // real force, int16 BE
            var tap = Run(Record(hidpp));

            // The HID++ path (feature index 0x0b is not the 0x0e seed) does not
            // extract here either, so the point is simply that the classic
            // machine stayed out of it and left _packed alone.
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void ClearLastFfbTarget_StopsABarePlayFromReplayingThePrePauseForce()
        {
            // The plugin clears the target while it holds the stream stopped for
            // a pause (issue #13). A PLAY arriving after that must not resurrect
            // the force from a slot we still believed was loaded.
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev);
            var first = Capture(Record(Cmd(0x11, 0x08, 0x20)));
            using (var s = new MemoryStream(first))
            {
                try { tap.ParseFrom(s); } catch (Exception) { }
            }
            Assert.NotNull(tap.TryGetFreshFfbTarget(1000));

            tap.ClearLastFfbTarget();
            Assert.Null(tap.TryGetFreshFfbTarget(1000));

            // Same tap, next capture: a bare PLAY with nothing downloaded since.
            var second = Capture(Record(Cmd(0x12, 0x00)));
            using (var s = new MemoryStream(second))
            {
                try { tap.ParseFrom(s); } catch (Exception) { }
            }
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void UndecodableTraffic_DoesNotKeepAStaleForceLookingFresh()
        {
            // A slot-2 shape we cannot decode must not refresh the timestamp of
            // the slot-1 value, or staleness stops meaning anything.
            var tap = Run(Record(Cmd(0x11, 0x08, 0xC0)),
                          Record(new byte[] { 0x21, 0x0b, 0x80, 0x80, 0x00, 0x00, 0x4d }));
            Assert.Equal(1, tap.FfbSamplesCaptured);
        }
    }
}
