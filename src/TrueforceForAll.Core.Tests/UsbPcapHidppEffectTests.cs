using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // End-to-end through ParseFrom: the game's DirectInput parametric
    // effects (conditions and friends) arrive as HID++ 0x8123 fn2 downloads
    // with the effect type at payload offset 5, and the tap routes them to
    // the effect engine instead of the scalar force path. Constants (type
    // 0x00) keep the legacy scalar pipeline: this split is also what keeps
    // a condition's saturation field (0x7FFF at offset 10-11) from ever
    // reading as a full-scale force (the issue #8 "constant 32767 on 0x12").
    public class UsbPcapHidppEffectTests
    {
        private const int DltUsbPcap = 249;
        private const ushort Dev = 7;
        private const byte FfbIdx = 0x0e;

        // One ep0 control SET_REPORT record (setup stage) around a raw HID++
        // report.
        private static byte[] Ep0(params byte[] hidpp)
        {
            const int HeaderLen = 28;
            var p = new byte[HeaderLen + 8 + hidpp.Length];
            p[0]  = HeaderLen;
            p[19] = (byte)(Dev & 0xff);
            p[20] = (byte)(Dev >> 8);
            p[21] = 0x00;                           // ep0 OUT
            p[22] = 0x02;                           // control transfer
            p[27] = 0x00;                           // setup stage
            p[HeaderLen + 0] = 0x21;                // HID class request
            p[HeaderLen + 1] = 0x09;                // SET_REPORT
            Array.Copy(hidpp, 0, p, HeaderLen + 8, hidpp.Length);
            return p;
        }

        // One interrupt-IN record (the wheel's HID++ reply path).
        private static byte[] InReply(params byte[] hidpp)
        {
            const int HeaderLen = 27;
            var p = new byte[HeaderLen + hidpp.Length];
            p[0]  = HeaderLen;
            p[19] = (byte)(Dev & 0xff);
            p[20] = (byte)(Dev >> 8);
            p[21] = 0x82;                           // ep2 IN
            p[22] = 0x01;                           // interrupt transfer
            Array.Copy(hidpp, 0, p, HeaderLen, hidpp.Length);
            return p;
        }

        private static byte[] Fn2(byte reportId, byte slot, byte type, params byte[] block)
        {
            var r = new byte[reportId == 0x12 ? 64 : 20];
            r[0] = reportId; r[1] = 0xff; r[2] = FfbIdx; r[3] = 0x2f;
            r[4] = slot; r[5] = type;
            Array.Copy(block, 0, r, 10, block.Length);
            return r;
        }

        private static byte[] Fn(byte reportId, byte fnHi, params byte[] prms)
        {
            var r = new byte[reportId == 0x12 ? 64 : 20];
            r[0] = reportId; r[1] = 0xff; r[2] = FfbIdx; r[3] = (byte)(fnHi | 0x0f);
            Array.Copy(prms, 0, r, 4, prms.Length);
            return r;
        }

        // The AC damper condition block from the owner's trace: sat 0x7FFF,
        // coeff 0x5FB0 (0.748), both sides, no deadband, center 0.
        private static readonly byte[] DamperBlock =
            { 0x7f, 0xff, 0x5f, 0xb0, 0x00, 0x00, 0x00, 0x00, 0x5f, 0xb0, 0x7f, 0xff };

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

        // Four changing constants: settles the arbiter and confirms the FFB
        // feature index, the way a real game's force stream does.
        private static byte[][] ConfirmingConstants() => new[]
        {
            Ep0(Fn2(0x11, 0x01, 0x80, 0x03, 0x20)),
            Ep0(Fn2(0x11, 0x01, 0x80, 0x04, 0x20)),
            Ep0(Fn2(0x11, 0x01, 0x80, 0x05, 0x20)),
            Ep0(Fn2(0x11, 0x01, 0x80, 0x06, 0x20)),
        };

        [Fact]
        public void DamperDownload_PlaysWithoutPublishingAScalar()
        {
            // Four identical (slot, type) downloads: the arming streak, the
            // way a real game's re-sent damper looks on the wire.
            var d = Fn2(0x12, 0x02, 0x87, DamperBlock);
            var tap = Run(Ep0(d), Ep0(d), Ep0(d), Ep0(d));
            Assert.True(tap.AnyHidppParametricPlaying);
            // The 0x7FFF saturation at offset 10-11 must NOT appear as force.
            Assert.Null(tap.TryGetFreshFfbTarget(10000));
            // Rightward motion pulls left (positive), about 0.748 FS.
            short? f = tap.TryEvaluateHidppEffects(0f, 1f, 0f, 1f, 1f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)23000, (short)26000);
        }

        [Fact]
        public void ConstantAndDamper_SplitBetweenScalarAndEngine()
        {
            // A constant force stream settles the arbiter and confirms the
            // index; the damper downloads that follow arm immediately.
            var tap = Run(
                Ep0(Fn2(0x11, 0x01, 0x80, 0x03, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x04, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x05, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x06, 0x20)),
                Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)),
                Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)));
            // Scalar path carries the last constant (0x0620 = 1568).
            Assert.Equal((short)0x0620, tap.TryGetFreshFfbTarget(10000));
            // Engine carries the damper alongside.
            Assert.True(tap.AnyHidppParametricPlaying);
        }

        [Fact]
        public void StopAndReset_EndTheRendering()
        {
            var d = Fn2(0x12, 0x02, 0x87, DamperBlock);
            var stop = Run(
                Ep0(d), Ep0(d), Ep0(d), Ep0(d),
                Ep0(Fn(0x11, 0x30, 0x02, HidppEffectEngine.StateStop)));
            Assert.False(stop.AnyHidppParametricPlaying);

            var reset = Run(
                Ep0(d), Ep0(d), Ep0(d), Ep0(d),
                Ep0(Fn(0x11, 0x10)));
            Assert.False(reset.AnyHidppParametricPlaying);
        }

        [Fact]
        public void GlobalGain_FromTheWire_ScalesTheOutput()
        {
            var d = Fn2(0x12, 0x02, 0x87, DamperBlock);
            var tap = Run(
                Ep0(Fn(0x11, 0x80, 0x7f, 0xff, 0x00, 0x00)),   // 50 %
                Ep0(d), Ep0(d), Ep0(d), Ep0(d));
            short? f = tap.TryEvaluateHidppEffects(0f, 1f, 0f, 1f, 1f);
            Assert.True(f.HasValue);
            Assert.InRange(f.Value, (short)11000, (short)13500);
        }

        [Fact]
        public void WheelReply_AssignsTheSlot()
        {
            // Confirmed index (constants), then a new effect (slot byte 0):
            // the wheel replies "slot 2" and a stop addressed to slot 2 must
            // land on it.
            var records = new System.Collections.Generic.List<byte[]>(ConfirmingConstants())
            {
                Ep0(Fn2(0x12, 0x00, 0x87, DamperBlock)),
                InReply(0x12, 0xff, FfbIdx, 0x2a, 0x02),
                Ep0(Fn(0x11, 0x30, 0x02, HidppEffectEngine.StateStop)),
            };
            var tap = Run(records.ToArray());
            Assert.False(tap.AnyHidppParametricPlaying);

            // Companion positive: without the stop the same session renders.
            var live = new System.Collections.Generic.List<byte[]>(ConfirmingConstants())
            {
                Ep0(Fn2(0x12, 0x00, 0x87, DamperBlock)),
                InReply(0x12, 0xff, FfbIdx, 0x2a, 0x02),
            };
            Assert.True(Run(live.ToArray()).AnyHidppParametricPlaying);
        }

        [Fact]
        public void ClearLastFfbTarget_SuspendsAndRetainsEffects()
        {
            var d = Fn2(0x12, 0x02, 0x87, DamperBlock);
            var tap = Run(Ep0(d), Ep0(d), Ep0(d), Ep0(d));
            Assert.True(tap.AnyHidppParametricPlaying);
            tap.ClearLastFfbTarget();
            // Snapshot drops synchronously: nothing renders through a pause.
            Assert.False(tap.HidppEffects.AnyPlaying);
            // The next command from the game (driving again) republishes the
            // RETAINED table: a game that downloads its conditions once and
            // then only sends state commands keeps them across every pause.
            Feed(tap, Ep0(Fn(0x11, 0x30, 0x02, HidppEffectEngine.StatePlay)));
            Assert.True(tap.AnyHidppParametricPlaying);
        }

        [Fact]
        public void Arming_RequiresAStreakOrAConfirmedIndex()
        {
            // Three identical downloads: table populated, nothing armed, the
            // resolver and watchdog stay live.
            var d = Fn2(0x12, 0x02, 0x87, DamperBlock);
            var three = Run(Ep0(d), Ep0(d), Ep0(d));
            Assert.False(three.AnyHidppParametricPlaying);
            Assert.Null(three.TryEvaluateHidppEffects(0f, 1f, 0f, 1f, 1f));
            Assert.True(three.HidppEffects.AnyPlaying);   // ingested, gated

            // Varied (slot, type) bytes, the shape of non-FFB traffic that
            // happens to parse (the FM8 LED-feature class), never arm.
            var varied = Run(
                Ep0(Fn2(0x12, 0x01, 0x86, DamperBlock)),
                Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)),
                Ep0(Fn2(0x12, 0x03, 0x89, DamperBlock)),
                Ep0(Fn2(0x12, 0x04, 0x86, DamperBlock)),
                Ep0(Fn2(0x12, 0x05, 0x87, DamperBlock)));
            Assert.False(varied.AnyHidppParametricPlaying);
            Assert.Null(varied.TryEvaluateHidppEffects(0f, 1f, 0f, 1f, 1f));
        }

        [Fact]
        public void Noffb_SimulationSilencesEvaluation()
        {
            var tap = Run(Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)));
            tap.SimulateNoFfbCapture = true;
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(tap.TryEvaluateHidppEffects(0f, 1f, 0f, 1f, 1f));
        }

        [Fact]
        public void UnknownType_WithAutostart_IsDroppedNotPublished()
        {
            // Type 0x0f | autostart: definitely an effect download of a type
            // this build does not know. Its parameter bytes (0x7fff at 10-11)
            // must never publish as force.
            var tap = Run(Ep0(Fn2(0x12, 0x01, 0x8f, 0x7f, 0xff, 0x5f, 0xb0)));
            Assert.Null(tap.TryGetFreshFfbTarget(10000));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Equal(1, tap.HidppEffects.UnknownTypeDownloads);
        }

        [Fact]
        public void UnknownType_WithoutAutostart_KeepsTheScalarFallback()
        {
            // Without the autostart bit the packet is ambiguous (a foreign
            // dialect's force write would look like this); dropping it could
            // deafen the tap, so it stays on the legacy scalar path where the
            // arbiter's change gate guards it.
            var tap = Run(
                Ep0(Fn2(0x11, 0x01, 0x0f, 0x03, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x0f, 0x04, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x0f, 0x05, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x0f, 0x06, 0x20)));
            Assert.Equal((short)0x0620, tap.TryGetFreshFfbTarget(10000));
        }

        [Fact]
        public void Noffb_ParametricIngest_DoesNotStampSamples()
        {
            // Under the NOFFB simulation a decoded damper must not clear the
            // undecoded-traffic guard (the stamp would self-defeat the
            // simulation of "reports visible, force not extracted").
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev);
            tap.SimulateNoFfbCapture = true;
            using (var s = new MemoryStream(Capture(Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)))))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF */ }
            }
            Assert.True(tap.ForceTrafficSinceLastSample);
            Assert.False(tap.AnyHidppParametricPlaying);
        }

        [Fact]
        public void ParametricTraffic_DoesNotReadAsUndecoded()
        {
            // Damper re-downloads are decoded traffic: the quiet-spell hold's
            // "force writes we are not decoding" guard must stay quiet.
            var tap = Run(
                Ep0(Fn2(0x11, 0x01, 0x80, 0x03, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x04, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x05, 0x20)),
                Ep0(Fn2(0x11, 0x01, 0x80, 0x06, 0x20)),
                Ep0(Fn2(0x12, 0x02, 0x87, DamperBlock)));
            Assert.False(tap.ForceTrafficSinceLastSample);
        }
    }
}
