using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Passive capture of the wheel's configured rotation range.
    //
    // Whoever configures the wheel (G HUB, or a game's soft lock going through
    // the Logitech driver) sets it with HID++ 0x8123 fn6 SET_APERTURE, BE16
    // degrees in params[0..1]. The tap already sees every host->device packet
    // on the FFB feature, so the range costs nothing to observe. We never send
    // fn6, and we do not send the fn5 GET either: this is read-only.
    //
    // Function code from the mainline hidpp_ff table, which our decode matches
    // on all five codes we already handle (fn1 RESET_ALL, fn2 DOWNLOAD_EFFECT,
    // fn3 SET_EFFECT_STATE, fn4 DESTROY_EFFECT, fn8 SET_GLOBAL_GAINS).
    public class ApertureCaptureTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;
        private const byte FfbFeature = 0x0e;   // UsbPcapFfbTap's default seed

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
            var tap = new UsbPcapFfbTap(@"\.\USBPcap1", deviceAddress: Dev);
            using (var s = new MemoryStream(Capture(records)))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
            return tap;
        }

        // A 20-byte HID++ long report: [rid][devIdx][featIdx][fn|sw][params..].
        private static byte[] SetAperture(int degrees, byte feature = FfbFeature, byte rid = 0x11)
        {
            var p = new byte[20];
            p[0] = rid; p[1] = 0xff; p[2] = feature; p[3] = 0x6f;
            p[4] = (byte)(degrees >> 8);
            p[5] = (byte)(degrees & 0xff);
            return p;
        }

        [Fact]
        public void NothingObserved_RangeIsUnknown()
        {
            var tap = Run();
            Assert.Null(tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void SetAperture_PublishesTheRange()
        {
            var tap = Run(Record(SetAperture(900)));
            Assert.Equal(900, tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void SetAperture_1080_IsAccepted()
        {
            // The G PRO's maximum, and the case that started this: a user on
            // 1080 must not be reported as anything else.
            var tap = Run(Record(SetAperture(1080)));
            Assert.Equal(1080, tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void LaterWrite_ReplacesTheRange()
        {
            var tap = Run(Record(SetAperture(900)), Record(SetAperture(540)));
            Assert.Equal(540, tap.ObservedRotationRangeDeg);
        }

        [Theory]
        [InlineData(0)]        // the all-zero params of a GET, or padding
        [InlineData(39)]       // narrower than any wheel accepts
        [InlineData(1081)]     // wider than any supported wheel offers
        [InlineData(0xffff)]   // a foreign dialect reusing fn6
        public void ImplausibleValue_IsRejected(int degrees)
        {
            var tap = Run(Record(SetAperture(degrees)));
            Assert.Null(tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void ImplausibleValue_DoesNotClobberAGoodOne()
        {
            var tap = Run(Record(SetAperture(900)), Record(SetAperture(0)));
            Assert.Equal(900, tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void Fn6_OnAnotherFeature_IsNotARange()
        {
            // fn6 on the rev-light feature is a LEVEL write, and byte 4-5 there
            // is not an aperture. Only the FFB feature index reaches the router.
            var tap = Run(Record(SetAperture(900, feature: 0x0a)));
            Assert.Null(tap.ObservedRotationRangeDeg);
        }

        [Fact]
        public void VeryLongReport_IsAlsoDecoded()
        {
            // Conditions ride 0x12 on an RS50 because the block does not fit
            // 0x11; the aperture can arrive on either form.
            var tap = Run(Record(SetAperture(720, rid: 0x12)));
            Assert.Equal(720, tap.ObservedRotationRangeDeg);
        }
    }
}
