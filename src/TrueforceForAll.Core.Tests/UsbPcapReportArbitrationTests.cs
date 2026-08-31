using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // End-to-end through ParseFrom: HID++ 0x8123 fn2 force writes on ep0 as
    // SET_REPORT, on the long report 0x11 and the very-long report 0x12, at
    // the G PRO's seed feature index 0x0E. Pins the 2026-08-28 change: 0x12 is
    // decoded without any opt-in, and the report carrying the stream wins.
    public class UsbPcapReportArbitrationTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;
        private const byte FfbIdx = 0x0e;

        // One ep0 control SET_REPORT record (setup stage) carrying a HID++
        // report whose bytes 10-11 hold the force, big-endian.
        private static byte[] Ep0(byte reportId, short force)
        {
            const int HeaderLen = 28;               // stage byte lives at [27]
            int len = reportId == 0x12 ? 64 : 20;
            var p = new byte[HeaderLen + 8 + len];
            p[0]  = HeaderLen;
            p[19] = (byte)(Dev & 0xff);
            p[20] = (byte)(Dev >> 8);
            p[21] = 0x00;                           // ep0 OUT
            p[22] = 0x02;                           // control transfer
            p[27] = 0x00;                           // setup stage
            p[HeaderLen + 0] = 0x21;                // bmRequestType: class, interface, host-to-device
            p[HeaderLen + 1] = 0x09;                // SET_REPORT
            int d = HeaderLen + 8;
            p[d + 0] = reportId;
            p[d + 1] = 0xff;
            p[d + 2] = FfbIdx;
            p[d + 3] = 0x2d;                        // fn2, sw-id 0xd
            p[d + 10] = (byte)(force >> 8);
            p[d + 11] = (byte)(force & 0xff);
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

        // A change-driven force stream: each packet differs a little from the
        // last, the way the Logitech stack really emits it.
        private static byte[][] Repeat(byte reportId, short force, int n)
        {
            var arr = new byte[n][];
            for (int i = 0; i < n; i++) arr[i] = Ep0(reportId, (short)(force + (i % 7) - 3));
            return arr;
        }

        private static byte[][] Constant(byte reportId, short value, int n)
        {
            var arr = new byte[n][];
            for (int i = 0; i < n; i++) arr[i] = Ep0(reportId, value);
            return arr;
        }

        [Fact]
        public void VeryLongOnlyStream_IsDecoded_WithoutAnyOptIn()
        {
            // The owner's G PRO on 2026-08-28: every force write on 0x12.
            var tap = Run(Repeat(0x12, 2000, 25));
            short? got = tap.TryGetFreshFfbTarget(1000);
            Assert.True(got.HasValue && Math.Abs(got.Value - 2000) <= 3);
            Assert.Equal((byte)0x12, tap.LiveForceReport);
            Assert.True(tap.FfbSamplesCaptured >= 20);
        }

        [Fact]
        public void Rs50InAssettoCorsa_ConstantVeryLongHeartbeat_IsNeverTheTarget()
        {
            // Tester trace 2026-08-29: 0x12 = 32767 on every packet at 100+/s,
            // real force on 0x11. The shipped path used to mirror the 32767
            // until 0x11 proved itself, pinning the wheel at full lock.
            var records = new System.Collections.Generic.List<byte[]>();
            records.AddRange(Constant(0x12, 32767, 60));
            for (int i = 0; i < 20; i++)
            {
                records.AddRange(Constant(0x12, 32767, 4));
                records.Add(Ep0(0x11, (short)(-127 - i * 200)));
            }
            var tap = Run(records.ToArray());
            short? got = tap.TryGetFreshFfbTarget(1000);
            Assert.True(got.HasValue && got.Value < 0 && got.Value != 32767);
            Assert.Equal((byte)0x11, tap.LiveForceReport);
        }

        [Fact]
        public void LongStream_IsDecoded_AndVeryLongManagementWritesAreIgnored()
        {
            // 0x11 carries the force; a same-shaped 0x12 write with a garbage
            // "hard left" value must not reach the target.
            var records = new System.Collections.Generic.List<byte[]>();
            records.AddRange(Repeat(0x11, 1500, 10));
            records.Add(Ep0(0x12, -30000));
            records.AddRange(Repeat(0x11, 1500, 5));
            records.Add(Ep0(0x12, -30000));
            var tap = Run(records.ToArray());
            short? got = tap.TryGetFreshFfbTarget(1000);
            Assert.True(got.HasValue && Math.Abs(got.Value - 1500) <= 3);
            Assert.Equal((byte)0x11, tap.LiveForceReport);
        }

        [Fact]
        public void LoneVeryLongWrites_NeverBecomeTheTarget()
        {
            var tap = Run(Ep0(0x12, -30000), Ep0(0x12, -30000));
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.Equal((byte)0, tap.LiveForceReport);
        }

        [Fact]
        public void DecodedStream_LeavesNoUndecodedTraffic()
        {
            var tap = Run(Repeat(0x11, 1500, 10));
            Assert.False(tap.ForceTrafficSinceLastSample);
        }

        [Fact]
        public void SimulatedNoCapture_ReportsUndecodedForceTraffic()
        {
            // NOFFB: the wheel is being written to, the tap decodes nothing,
            // so the plugin's quiet-spell hold must stand down.
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev) { SimulateNoFfbCapture = true };
            using (var s = new MemoryStream(Capture(Repeat(0x11, 1500, 10))))
            {
                try { tap.ParseFrom(s); } catch (Exception) { }
            }
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.True(tap.ForceTrafficSinceLastSample);
        }
    }
}
