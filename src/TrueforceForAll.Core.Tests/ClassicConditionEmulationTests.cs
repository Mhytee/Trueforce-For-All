using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Classic-condition emulation (CLASSICCOND): the G923 PS/PC (C266) gets
    // its DirectInput damper, friction and inertia as classic Logitech slot
    // downloads (types 0x0c hi-res damper, 0x02 low-res damper, 0x0e
    // friction), which the wheel firmware ignores while our ep3 stream is
    // live. Behind the gate the tap decodes them into the condition engine's
    // normalized form and feeds the engine's external slot region; the same
    // Evaluate the HID++ wheels use renders them.
    //
    // Wire bytes: WINCAP (new-lg4ff issue #86, poisotf, 2024-08-19), the
    // Logitech Windows driver on a G923 c266 playing FFBInspector's Damper,
    // Friction and Inertia at coefficient 5000/10000:
    //
    //   21 0c 08 00 08 00 01   slot 2, dl+play, hi-res damper K=8 both sides
    //   41 0c 08 00 08 00 01   slot 3, the DirectInput friction, cast to a damper
    //   81 0c 08 00 08 00 01   slot 4, the DirectInput inertia, cast to a damper
    //
    // K is linear (Logitech's protocol document: "Linear slope with 0 being
    // the weakest force and 15 being the strongest"), so K=8 is 8/15 =
    // 0.533 and vel +1 renders 0.533 x 32767 = 17475 at damperGain 1.
    //
    // Sign contract (the engine's, hardware-validated on the HID++ path):
    // positive force pulls toward LOWER steer; positive velocity = rightward.
    // A damper under rightward motion comes out POSITIVE. K1/S1 is the
    // left (push) side, K2/S2 the right (pull) side; S = 1 inverts.
    public class ClassicConditionEmulationTests
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

        private static void Feed(UsbPcapFfbTap tap, params byte[][] records)
        {
            using (var s = new MemoryStream(Capture(records)))
            {
                try { tap.ParseFrom(s); }
                catch (Exception) { /* EOF = end of synthetic capture */ }
            }
        }

        // A tap with the CLASSICCOND gate on and the engine's output filter off
        // (raw math, like HidppEffectEngineTests), before any packet.
        private static UsbPcapFfbTap NewTap(bool gate = true)
        {
            var tap = new UsbPcapFfbTap(@"\\.\USBPcap1", deviceAddress: Dev)
            {
                ClassicConditionsEnabled = gate,
            };
            tap.HidppEffects.ConditionOutputCutoffHz = 0f;
            return tap;
        }

        private static UsbPcapFfbTap Run(params byte[][] records)
        {
            var tap = NewTap();
            Feed(tap, records);
            return tap;
        }

        private static byte[] B(params byte[] b) => b;   // explicit 7-byte payloads
        private static readonly byte[] WinDamper = { 0x21, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01 };   // WINCAP #19
        private static readonly byte[] Play2     = { 0x22, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };   // slot 2 PLAY
        private static readonly byte[] Stop2     = { 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };   // slot 2 STOP
        private static short? Vel(UsbPcapFfbTap tap, float vel) => tap.TryEvaluateHidppEffects(0f, vel, 0f, 1f, 1f);

        // ---------------- hi-res damper 0x0c ----------------

        [Fact]
        public void WindowsDamper_DownloadAndPlay_ArmsAndRenders()
        {
            var tap = Run(Record(WinDamper));
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.True(tap.AnyHidppDamperPlayingNow);
            // A condition never publishes a scalar force target.
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
            Assert.InRange(Vel(tap, -1f).Value, (short)-18500, (short)-16500);
            Assert.Equal(1, tap.ClassicConditionUpdatesCaptured);
            Assert.Equal(1, tap.HidppEffects.ExternalConditionUpdates);
            Assert.Equal(0, tap.HidppEffects.ParametricDownloads);
        }

        [Fact]
        public void DamperClipByte_IsIgnored()
        {
            // Byte 6 is a Driving Force Pro extension; the Windows driver
            // writes 0x01 there for a full-saturation damper. Honoring it
            // would render every Windows damper at 1/255.
            var tap = Run(Record(B(0x21, 0x0c, 0x0f, 0x00, 0x0f, 0x00, 0x00)));
            Assert.Equal((short)32767, Vel(tap, +1f).Value);
            var win = Run(Record(WinDamper));
            Assert.InRange(Vel(win, +1f).Value, (short)16500, (short)18500);
        }

        [Fact]
        public void SignBits_MakeAnAntiDamper()
        {
            // S1 = S2 = 1: the force accentuates motion ("ice"); K=4 -> 0.267.
            var tap = Run(Record(B(0x21, 0x0c, 0x04, 0x01, 0x04, 0x01, 0x80)));
            Assert.InRange(Vel(tap, +1f).Value, (short)-9300, (short)-8200);
            Assert.InRange(Vel(tap, -1f).Value, (short)8200, (short)9300);
        }

        [Fact]
        public void K1IsLeft_K2IsRight()
        {
            // K1=2 (0.133) on the left/push side, K2=12 (0.8) on the right.
            var tap = Run(Record(B(0x21, 0x0c, 0x02, 0x00, 0x0c, 0x00, 0xff)));
            Assert.InRange(Vel(tap, -1f).Value, (short)-4900, (short)-3900);
            Assert.InRange(Vel(tap, +1f).Value, (short)25700, (short)26700);
        }

        [Fact]
        public void S1Only_FlipsOnlyTheLeftSide()
        {
            // Left side inverted: moving left is accentuated (pulls left =
            // positive); moving right is still opposed (positive too).
            var tap = Run(Record(B(0x21, 0x0c, 0x08, 0x01, 0x08, 0x00, 0xff)));
            Assert.InRange(Vel(tap, -1f).Value, (short)16500, (short)18500);
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
        }

        // ---------------- low-res damper 0x02 ----------------

        [Fact]
        public void LowResDamper_UsesTable27()
        {
            // K1=3 -> 0.25; K2=5 -> 0.5 with S2 set -> -0.5.
            var tap = Run(Record(B(0x21, 0x02, 0x03, 0x00, 0x05, 0x01, 0x00)));
            Assert.InRange(Vel(tap, +1f).Value, (short)-16900, (short)-15900);
            Assert.InRange(Vel(tap, -1f).Value, (short)-8700, (short)-7700);
        }

        [Fact]
        public void LowResDamper_KZero_IsNotZero()
        {
            // Table 27's K=0 is 1/4 of the offset, 1/16 normalized: a slope.
            var tap = Run(Record(B(0x21, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00)));
            Assert.InRange(Vel(tap, +1f).Value, (short)1900, (short)2200);
        }

        // ---------------- friction 0x0e ----------------

        [Fact]
        public void Friction_Decodes_WithRealClip()
        {
            // LG4FF encoding of left -8192, right +32767, saturation 32768:
            // K1=0x40 with S1, K2=0xff, clip 0x80 -> sats 0.502 both sides.
            var tap = Run(Record(B(0x21, 0x0e, 0x40, 0xff, 0x80, 0x01, 0x00)));
            Assert.True(tap.AnyHidppDamperPlayingNow);
            // First evaluate seeds the lock point: no force out of nowhere.
            Assert.Equal((short)0, tap.TryEvaluateHidppEffects(0f, 0f, 0f, 1f, 1f).Value);
            // Far right of the lock: RightCoeff 1.0, clamped to the 0.502 clip.
            Assert.InRange(tap.TryEvaluateHidppEffects(0.1f, 0f, 0f, 1f, 1f).Value, (short)15900, (short)17000);
        }

        [Fact]
        public void Friction_ClipZero_PlaysSilently()
        {
            var tap = Run(Record(B(0x21, 0x0e, 0x80, 0x80, 0x00, 0x00, 0x00)));
            Assert.True(tap.AnyHidppParametricPlaying);
            tap.TryEvaluateHidppEffects(0f, 0f, 0f, 1f, 1f);
            short? f = tap.TryEvaluateHidppEffects(0.1f, 0f, 0f, 1f, 1f);
            Assert.True(f.HasValue);       // playing, not "nothing playing"
            Assert.Equal((short)0, f.Value);
        }

        // Friction's S bits live in byte 5 (bit 0 = S1 -> left, bit 4 = S2 ->
        // right), unlike the dampers (S1 in byte 3, S2 in byte 5 bit 0). The
        // vectors below are the brief's F1 / F3 / F5: K1 = K2 = 0x80 (0.502)
        // with clip 0xff, so the lock-point force is +/-0.502 = 16449 counts.
        // Seed at 0, then probe 0.1 away: right of the lock reads RightCoeff
        // at frac +1, left of it reads LeftCoeff at frac -1.
        private static short FrictionAt(byte[] payload, float pos)
        {
            var tap = Run(Record(payload));
            Assert.Equal((short)0, tap.TryEvaluateHidppEffects(0f, 0f, 0f, 1f, 1f).Value);
            return tap.TryEvaluateHidppEffects(pos, 0f, 0f, 1f, 1f).Value;
        }

        [Fact]
        public void Friction_NoSignBits_OpposesBothSides()
        {
            // F1: right of the lock pulls left (positive), left of it pulls
            // right (negative).
            var f1 = B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x00, 0x00);
            Assert.InRange(FrictionAt(f1, +0.1f), (short)15900, (short)17000);
            Assert.InRange(FrictionAt(f1, -0.1f), (short)-17000, (short)-15900);
        }

        [Fact]
        public void Friction_S2_NegatesTheRightSideOnly()
        {
            // F5: byte 5 bit 4 = S2 -> RightCoeff = -0.502; the left side is
            // untouched.
            var f5 = B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x10, 0x00);
            Assert.InRange(FrictionAt(f5, +0.1f), (short)-17000, (short)-15900);
            Assert.InRange(FrictionAt(f5, -0.1f), (short)-17000, (short)-15900);
        }

        [Fact]
        public void Friction_S1_NegatesTheLeftSideOnly()
        {
            // Byte 5 bit 0 = S1 -> LeftCoeff = -0.502: left of the lock the
            // force flips to +0.502 (frac -1 times -0.502); the right side
            // still reads +0.502.
            var s1 = B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x01, 0x00);
            Assert.InRange(FrictionAt(s1, +0.1f), (short)15900, (short)17000);
            Assert.InRange(FrictionAt(s1, -0.1f), (short)15900, (short)17000);
        }

        [Fact]
        public void Friction_BothSignBits_InvertBothSides()
        {
            // F3: 0x11 = S1 and S2, an anti-friction on both sides.
            var f3 = B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x11, 0x00);
            Assert.InRange(FrictionAt(f3, +0.1f), (short)-17000, (short)-15900);
            Assert.InRange(FrictionAt(f3, -0.1f), (short)15900, (short)17000);
        }

        [Fact]
        public void HiResDamper_KZero_PlaysAtZero()
        {
            // V5: K = 0 both sides under K/15 is a playing damper with no
            // force (a value of 0, not "nothing playing"). The clip byte
            // 0xff changes nothing on this type.
            var tap = Run(Record(B(0x21, 0x0c, 0x00, 0x00, 0x00, 0x00, 0xff)));
            Assert.True(tap.AnyHidppParametricPlaying);
            short? f = Vel(tap, +1f);
            Assert.True(f.HasValue);
            Assert.Equal((short)0, f.Value);
        }

        // ---------------- slot state machine ----------------

        [Fact]
        public void BareDownload_DoesNotPlay_PlayStartsIt()
        {
            var tap = Run(Record(B(0x20, 0x0c, 0x08, 0x00, 0x08, 0x00, 0xff)),
                          Record(Play2));
            // Both records are in one capture; check the end state through a
            // second tap that stops after the download alone.
            var loaded = Run(Record(B(0x20, 0x0c, 0x08, 0x00, 0x08, 0x00, 0xff)));
            Assert.False(loaded.AnyHidppParametricPlaying);
            Assert.Null(Vel(loaded, +1f));
            Assert.Equal(0, loaded.ClassicConditionUpdatesCaptured);
            // After PLAY: renders; PLAY itself never counts (spring precedent).
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
            Assert.Equal(0, tap.ClassicConditionUpdatesCaptured);
        }

        [Fact]
        public void Stop_SilencesButRetains_PlayResumes()
        {
            var stopped = Run(Record(WinDamper), Record(Stop2));
            Assert.False(stopped.AnyHidppParametricPlaying);
            Assert.Null(Vel(stopped, +1f));
            var resumed = Run(Record(WinDamper), Record(Stop2), Record(Play2));
            Assert.InRange(Vel(resumed, +1f).Value, (short)16500, (short)18500);
        }

        [Fact]
        public void Refresh_RetunesThePlayingDamper()
        {
            var tap = Run(Record(WinDamper),
                          Record(B(0x2c, 0x0c, 0x04, 0x00, 0x04, 0x00, 0xff)));
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.InRange(Vel(tap, +1f).Value, (short)8200, (short)9300);
        }

        [Fact]
        public void RefreshIntoStoppedSlot_IsIgnored()
        {
            // The protocol defines refresh on a playing force only; a later
            // PLAY renders the pre-stop parameters, not the refresh's.
            var tap = Run(Record(WinDamper), Record(Stop2),
                          Record(B(0x2c, 0x0c, 0x04, 0x00, 0x04, 0x00, 0xff)),
                          Record(Play2));
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
        }

        [Fact]
        public void Spring_ReplacesDamper_DamperGone()
        {
            // Since 2026-09-18 the hi-res spring is an engine effect too, so
            // the slot is not empty afterwards: what must be gone is the
            // DAMPER, which is the only term velocity moves. The spring at
            // the centered position contributes its own near-zero.
            var tap = Run(Record(WinDamper),
                          Record(B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff)));
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.False(tap.AnyHidppDamperPlayingNow);
            Assert.InRange(Vel(tap, +1f).Value, (short)-50, (short)50);
            Assert.InRange(Vel(tap, -1f).Value, (short)-50, (short)50);
        }

        [Fact]
        public void Damper_ReplacesSpring_SpringGone()
        {
            var tap = Run(Record(B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff)),
                          Record(B(0x21, 0x0c, 0x08, 0x00, 0x08, 0x00, 0xff)));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
        }

        [Fact]
        public void VariableForce_ReplacesDamper_ScalarPublished()
        {
            var tap = Run(Record(WinDamper),
                          Record(B(0x21, 0x08, 0xc0, 0x00, 0x00, 0x00, 0x00)));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void MultiSlotMask_LoadsBothSlots()
        {
            // Mask 0x6 = slots 2 and 3 in one command: two dampers sum past
            // full scale.
            var tap = Run(Record(B(0x61, 0x0c, 0x08, 0x00, 0x08, 0x00, 0xff)));
            Assert.Equal((short)32767, Vel(tap, +1f).Value);
        }

        [Fact]
        public void ThreeWindowsDampers_Sum()
        {
            // WINCAP #19/#21/#23: DirectInput damper, friction and inertia all
            // cast to 0x0c dampers in slots 2, 3, 4. They must sum (no
            // one-copy-per-type dedupe on the classic slots).
            var tap = Run(Record(B(0x21, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)),
                          Record(B(0x41, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)),
                          Record(B(0x81, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)));
            Assert.Equal((short)32767, Vel(tap, +1f).Value);
            Assert.Equal(0, tap.HidppEffects.ReplacedStaleConditions);
            Assert.Equal(3, tap.ClassicConditionUpdatesCaptured);
        }

        [Fact]
        public void SpringSwapInMiddleSlot_LeavesTheOtherDampersAlone()
        {
            // Three K=4 dampers (0.267 each, 0.8 summed = 26214) in slots 1,
            // 2, 3; a hi-res spring then overwrites slot 2 (the two other
            // dampers must survive: the eviction is per slot, never
            // region-wide), and a damper overwrites the spring again.
            var d1 = B(0x11, 0x0c, 0x04, 0x00, 0x04, 0x00, 0x01);
            var d2 = B(0x21, 0x0c, 0x04, 0x00, 0x04, 0x00, 0x01);
            var d3 = B(0x41, 0x0c, 0x04, 0x00, 0x04, 0x00, 0x01);
            var spring2 = B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff);

            var three = Run(Record(d1), Record(d2), Record(d3));
            Assert.InRange(Vel(three, +1f).Value, (short)25700, (short)26700);

            var swapped = Run(Record(d1), Record(d2), Record(d3), Record(spring2));
            Assert.InRange(Vel(swapped, +1f).Value, (short)17000, (short)18000);
            Assert.True(swapped.AnyClassicSpringPlaying);
            Assert.Equal(1, swapped.SpringUpdatesCaptured);

            var back = Run(Record(d1), Record(d2), Record(d3), Record(spring2), Record(d2));
            Assert.InRange(Vel(back, +1f).Value, (short)25700, (short)26700);
            Assert.False(back.AnyClassicSpringPlaying);
            Assert.Equal(0, back.HidppEffects.ReplacedStaleConditions);
            Assert.Equal(4, back.ClassicConditionUpdatesCaptured);
        }

        [Fact]
        public void StopAll_f3_StopsEveryClassicSlot()
        {
            // f3 00 = mask 0xf + STOP: what ACC sends.
            var tap = Run(Record(B(0x21, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)),
                          Record(B(0x41, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)),
                          Record(B(0x81, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)),
                          Record(B(0xf3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)));
            Assert.False(tap.AnyHidppParametricPlaying);
        }

        [Fact]
        public void ClearLastFfbTarget_DropsImmediately_AndABarePlayDoesNotResurrect()
        {
            var tap = Run(Record(WinDamper));
            Assert.True(tap.AnyHidppParametricPlaying);
            // The pause primitive (plugin thread): the snapshot drops NOW.
            tap.ClearLastFfbTarget();
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Vel(tap, +1f));
            // A bare PLAY afterwards finds the slot wiped (the deferred reset
            // on the parser thread; a capture start wipes it the same way),
            // so nothing comes back.
            Feed(tap, Record(Play2));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Vel(tap, +1f));
        }

        // ---------------- live capture: the deferred pause reset ----------------
        //
        // Feed() delivers each command through its own ParseFrom, and every
        // capture start wipes the slot state, so the tests above cannot tell
        // the deferred reset (ClearLastFfbTarget's _classicResetRequested,
        // honored at the next classic command) from the capture-start wipe.
        // A blocking stream keeps ONE capture open on a worker thread while
        // the test thread plays the plugin: pause, then a bare PLAY, then the
        // game driving again.

        private sealed class BlockingStream : Stream
        {
            private readonly BlockingCollection<byte[]> _chunks = new BlockingCollection<byte[]>();
            private byte[] _cur;
            private int _pos;

            public void Push(params byte[][] chunks) { foreach (var c in chunks) _chunks.Add(c); }
            public void Complete() => _chunks.CompleteAdding();

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_cur == null || _pos >= _cur.Length)
                {
                    // Completed, or a hung test: EOF ends the parser either way.
                    if (!_chunks.TryTake(out _cur, 10000)) return 0;
                    _pos = 0;
                }
                int n = Math.Min(count, _cur.Length - _pos);
                Array.Copy(_cur, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static byte[] PcapHeader() => Capture();
        private static byte[] PcapRecord(byte[] record) => Capture(record).Skip(24).ToArray();

        // A record addressed to another USB device: the parser counts it and
        // drops it before the classic state machine, so it is a barrier. Once
        // PacketsParsed covers it, every record pushed before it has been
        // fully handled (one parser thread, in order).
        private static byte[] Barrier()
        {
            var r = Record(WinDamper);
            r[19] = (byte)((Dev + 1) & 0xff);
            return PcapRecord(r);
        }

        private static void WaitUntil(Func<bool> cond, string what)
            => Assert.True(SpinWait.SpinUntil(cond, 5000), "timed out waiting for " + what);

        private static Thread StartParser(UsbPcapFfbTap tap, BlockingStream stream)
        {
            var t = new Thread(() => { try { tap.ParseFrom(stream); } catch (Exception) { /* EOF */ } })
            {
                IsBackground = true,
                Name = "test-parser",
            };
            t.Start();
            return t;
        }

        [Fact]
        public void LiveCapture_PauseThenBarePlay_LeavesTheEngineSlotEmpty()
        {
            var tap = NewTap();
            var stream = new BlockingStream();
            var parser = StartParser(tap, stream);
            try
            {
                stream.Push(PcapHeader(), PcapRecord(Record(WinDamper)));
                WaitUntil(() => tap.AnyHidppParametricPlaying, "the damper to play");
                Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);

                // The plugin pauses: the snapshot drops from this thread.
                tap.ClearLastFfbTarget();
                Assert.False(tap.AnyHidppParametricPlaying);
                Assert.Null(Vel(tap, +1f));

                // The game sends a bare PLAY into the same capture. The
                // parser honors the deferred reset first, so the slot is
                // gone and the PLAY finds nothing.
                long seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(Play2)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the PLAY and its barrier");
                Assert.False(tap.AnyHidppParametricPlaying);
                Assert.False(tap.HidppEffects.AnyPlaying);
                Assert.Null(Vel(tap, +1f));

                // A repeated PLAY on a retained playing slot is a no-op by
                // design, so the probe that tells a wiped table from a
                // retained one is the game's own pause/resume pair: STOP then
                // PLAY republishes a retained slot on the edge. The table
                // itself must be empty, not just the snapshot.
                seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(Stop2)), PcapRecord(Record(Play2)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 3, "the STOP, the PLAY and their barrier");
                Assert.False(tap.AnyHidppParametricPlaying);
                Assert.False(tap.HidppEffects.AnyPlaying);
                Assert.Null(Vel(tap, +1f));

                // The game drives again: a fresh download renders, proving
                // the parser is alive and the reset was consumed once.
                seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(WinDamper)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the re-download and its barrier");
                Assert.True(tap.AnyHidppParametricPlaying);
                Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
            }
            finally
            {
                stream.Complete();
                parser.Join(5000);
            }
        }

        [Fact]
        public void LiveCapture_PauseThenBarePlay_DoesNotReplayTheSpring()
        {
            // The spring precedent on the same harness, gate off (the shipped
            // path): a bare PLAY after the pause must not republish the
            // pre-pause spring from a slot we still believed loaded.
            var tap = NewTap(gate: false);
            var stream = new BlockingStream();
            var parser = StartParser(tap, stream);
            try
            {
                stream.Push(PcapHeader(), PcapRecord(Record(CenteredK8Spring)));
                WaitUntil(() => tap.AnyClassicSpringPlaying, "the spring to play");
                Assert.NotNull(tap.TryEvaluateClassicSprings(0.8f));

                tap.ClearLastFfbTarget();
                Assert.False(tap.AnyClassicSpringPlaying);
                Assert.Null(tap.TryEvaluateClassicSprings(0.8f));

                long seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(Play2)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the PLAY and its barrier");
                Assert.False(tap.AnyClassicSpringPlaying);
                Assert.Null(tap.TryEvaluateClassicSprings(0.8f));

                seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(CenteredK8Spring)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the re-download and its barrier");
                Assert.True(tap.AnyClassicSpringPlaying);
            }
            finally
            {
                stream.Complete();
                parser.Join(5000);
            }
        }

        // ---------------- gate ----------------

        [Fact]
        public void GateOff_WindowsDamper_RendersNothing()
        {
            var tap = NewTap(gate: false);
            Feed(tap, Record(WinDamper));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Vel(tap, +1f));
            Assert.Equal(0, tap.ClassicConditionUpdatesCaptured);
            Assert.Equal(0, tap.HidppEffects.ExternalConditionUpdates);
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void GateOff_CorpusFriction_RendersNothing()
        {
            // The payload the old spring test used for "some other type".
            var tap = NewTap(gate: false);
            Feed(tap, Record(B(0x21, 0x0e, 0x80, 0x80, 0x88, 0x00, 0xff)));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.Null(Vel(tap, +1f));
            Assert.Equal(0, tap.ClassicConditionUpdatesCaptured);
            Assert.Equal(0, tap.HidppEffects.ExternalConditionUpdates);
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void SimulateNoFfbCapture_SuppressesConditions()
        {
            var lines = new List<string>();
            var tap = NewTap();
            tap.SimulateNoFfbCapture = true;
            tap.Logger = lines.Add;
            Feed(tap, Record(WinDamper));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Vel(tap, +1f));
            Assert.Equal(0, tap.ClassicConditionUpdatesCaptured);
            // The first-decode line must not claim a render that never happens.
            var damper = Assert.Single(lines.Where(l => l.Contains("classic damper 0x0C")));
            Assert.Contains("NOFFB simulation, not rendered", damper);
            Assert.DoesNotContain("rendering into the stream", damper);
        }

        // ---------------- logging ----------------

        [Fact]
        public void FirstDecode_LogsOncePerType()
        {
            var lines = new List<string>();
            var tap = NewTap();
            tap.Logger = lines.Add;
            Feed(tap, Record(WinDamper), Record(WinDamper),
                      Record(B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x00, 0x00)),
                      Record(B(0x21, 0x0e, 0x80, 0x80, 0xff, 0x00, 0x00)));
            var damper = lines.Where(l => l.Contains("classic damper 0x0C")).ToList();
            Assert.Single(damper);
            Assert.Contains("slot=2", damper[0]);
            Assert.Contains("K1=8", damper[0]);
            Assert.Contains("clip=0x01 (ignored on this type)", damper[0]);
            Assert.Contains("leftCoeff=+0.533", damper[0]);
            Assert.Single(lines.Where(l => l.Contains("classic friction 0x0E")));
        }

        [Fact]
        public void Fxdump_TracesClassicCommands()
        {
            var lines = new List<string>();
            var tap = NewTap();
            tap.LogEffectDownloads = true;
            tap.Logger = lines.Add;
            Feed(tap, Record(WinDamper), Record(B(0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)));
            var trace = lines.Where(l => l.StartsWith("FFB tap: CLASSIC")).ToList();
            Assert.Equal(2, trace.Count);
            Assert.Contains("slots=0x2 cmd=0x1 (download-and-play) type=0x0C", trace[0]);
            Assert.Contains("raw=21 0c 08 00 08 00 01", trace[0]);
            Assert.Contains("cmd=0x3 (stop)", trace[1]);
        }

        [Fact]
        public void HidppReport_StillRejected()
        {
            // A HID++ long report (0x11 ff ...) shares byte 0 with a slot-1
            // download-and-play; byte 1 = 0xff is above every classic type
            // and the firewall drops it before the slot machine.
            var hidpp = new byte[20];
            hidpp[0] = 0x11; hidpp[1] = 0xff; hidpp[2] = 0x0b; hidpp[3] = 0x2d;
            hidpp[4] = 0x00; hidpp[5] = 0x86;
            var tap = Run(Record(hidpp));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Equal(0, tap.HidppEffects.ExternalConditionUpdates);
        }

        // ---------------- classic spring slope under the gate ----------------
        //
        // Under the gate the hi-res spring's slope nibble reads LINEAR (2 x
        // K/15 per unit of the 0..1 axis) instead of the shipped 2^K guess,
        // on BOTH of its renderers: the classic path asserted here, and since
        // 2026-09-18 the engine copy as well (see "hi-res spring 0x0b in the
        // engine" below). With the gate off nothing changes.

        // FH5's centered spring with K=8 both sides: d1 = d2 = 1024/2047.
        private static readonly byte[] CenteredK8Spring = { 0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff };
        // Displacement 0.1 of the axis beyond the band edge (p = 0.6002).
        private const float RightOfBandBy0p1 = 0.2005f;

        [Fact]
        public void SpringSlope_GateOff_KeepsTwoToTheK()
        {
            // K=8 -> slope 256: 0.1 beyond the band saturates at the clip.
            var tap = NewTap(gate: false);
            Feed(tap, Record(CenteredK8Spring));
            Assert.Equal((short)32767, tap.TryEvaluateClassicSprings(RightOfBandBy0p1).Value);
            // The corpus K=1 packet from the spring tests: slope 2, about +30700.
            var k1 = NewTap(gate: false);
            Feed(k1, Record(B(0x21, 0x0b, 0x00, 0x08, 0x11, 0x20, 0xff)));
            Assert.InRange(k1.TryEvaluateClassicSprings(0f).Value, (short)29000, (short)32500);
        }

        [Fact]
        public void SpringSlope_GateOn_IsLinear()
        {
            // K=8: 0.1 x 2 x 8/15 = 0.10667 -> 3495 counts, positive right of
            // the band (pulls left), negative left of it.
            var tap = Run(Record(CenteredK8Spring));
            Assert.InRange(tap.TryEvaluateClassicSprings(RightOfBandBy0p1).Value, (short)3400, (short)3600);
            Assert.InRange(tap.TryEvaluateClassicSprings(-RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
            // K=1 corpus packet: dev 0.468 x 2/15 = 0.0624 -> about 2045.
            var k1 = Run(Record(B(0x21, 0x0b, 0x00, 0x08, 0x11, 0x20, 0xff)));
            Assert.InRange(k1.TryEvaluateClassicSprings(0f).Value, (short)1900, (short)2200);
        }

        [Fact]
        public void SpringSlope_GateOn_SBitsStillInvert()
        {
            // S2 set (byte 5 bit 4): right of the band pushes AWAY (negative).
            var tap = Run(Record(B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x10, 0xff)));
            Assert.InRange(tap.TryEvaluateClassicSprings(RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
        }

        // ---------------- hi-res spring 0x0b in the engine ----------------
        //
        // Since 2026-09-18 the hi-res spring lands in an external engine slot
        // as well, additively, so it renders alongside a streamed road force.
        // Its own ClassicSpring path is untouched: that one only ever renders
        // under spring mode, a replacement mode that arms on a bus with no
        // constant force for two seconds, so before this the commonest
        // classic condition on a PS wheel was silent in mixed traffic.
        //
        // Domain: the classic path works in p = (steerNorm + 1) / 2 and the
        // engine in steerNorm, so the 11-bit band edges D1/D2 map to
        // center = D1 + D2 - 1 and deadband half-width = D2 - D1, and the
        // slope loses the classic path's factor 2 (K/15 per unit of steerNorm
        // is 2 * K/15 per unit of p). The two renderers must therefore agree
        // count for count, which is what MatchesTheClassicPath asserts.

        private static short? Pos(UsbPcapFfbTap tap, float pos)
            => tap.TryEvaluateHidppEffects(pos, 0f, 0f, 1f, 1f);

        // Position and velocity together, for telling a spring term apart
        // from a damper term in the same sum.
        private static short? PosVel(UsbPcapFfbTap tap, float pos, float vel)
            => tap.TryEvaluateHidppEffects(pos, vel, 0f, 1f, 1f);

        // What the pause and focus releases ask for: conditions only (no
        // periodic or ramp gain) and NO spring-mode stand-down, because that
        // path renders the captured spring itself rather than substituting
        // its own.
        private static short? PosReleased(UsbPcapFfbTap tap, float pos)
            => tap.TryEvaluateHidppEffects(pos, 0f, 0f, 1f, 1f, 1, 1f, 1f, 0f, 0f,
                                           allowSpringModeStandDown: false);

        // A band that is NOT centered: edges at p 0.500 and 0.875, so in
        // steerNorm it runs from +0.000 to +0.751. A center bug reads as a
        // force on the wrong side of the wheel here, where a centered band
        // would hide it.
        private static readonly byte[] OffCenterBandSpring = { 0x21, 0x0b, 0x80, 0xe0, 0x88, 0x00, 0xff };

        [Fact]
        public void HiResSpring_GateOn_LandsAnAdditiveEngineSpring()
        {
            var tap = Run(Record(CenteredK8Spring));
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.Equal(1, tap.HidppEffects.ExternalConditionUpdates);
            // K=8 linear: 0.2 of steerNorm beyond the band is 0.2 x 8/15 =
            // 0.1067, so 3495 counts, positive right of center (pulls left).
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
            Assert.InRange(Pos(tap, -RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
            // The spring's own path is unchanged and still publishes no scalar.
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            // A spring download is counted once, as a spring; it is not also
            // a classic-condition capture.
            Assert.Equal(1, tap.SpringUpdatesCaptured);
            Assert.Equal(0, tap.ClassicConditionUpdatesCaptured);
        }

        [Fact]
        public void HiResSpring_EngineCopy_MatchesTheClassicPath()
        {
            // The domain conversion, end to end: for an off-center band the
            // engine copy and TryEvaluateClassicSprings must agree to within
            // rounding at every position, inside the band and beyond both
            // edges. Disagreement here is a center, deadband or slope error.
            var tap = Run(Record(OffCenterBandSpring));
            foreach (float s in new[] { -1f, -0.9f, -0.2f, -0.001f, 0.2f, 0.4f, 0.7f, 0.9f, 1f })
            {
                int classic = tap.TryEvaluateClassicSprings(s).Value;
                int engine  = Pos(tap, s).Value;
                Assert.InRange(engine, classic - 2, classic + 2);
            }
        }

        [Fact]
        public void HiResSpring_GateOn_BandIsSilentAndOffCenter()
        {
            // Band edges +0.000 and +0.751 of steerNorm: nothing between
            // them, a leftward pull above and a rightward push below.
            var tap = Run(Record(OffCenterBandSpring));
            Assert.Equal((short)0, Pos(tap, 0.4f).Value);
            Assert.Equal((short)0, Pos(tap, 0.7f).Value);
            // 0.149 past the upper edge x 8/15 = 0.0795 -> 2606.
            Assert.InRange(Pos(tap, 0.9f).Value, (short)2500, (short)2700);
            // 0.200 below the lower edge -> -3504.
            Assert.InRange(Pos(tap, -0.2f).Value, (short)-3600, (short)-3400);
        }

        [Fact]
        public void HiResSpring_GateOn_ClipSaturates()
        {
            // Same K=8 centered spring with CLIP 0x20 = 0.1255, which caps
            // the term at 4112 counts well before the slope would get there.
            var tap = Run(Record(B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x00, 0x20)));
            Assert.InRange(Pos(tap, 0.5f).Value, (short)4000, (short)4200);
            Assert.InRange(Pos(tap, 1f).Value, (short)4000, (short)4200);
            Assert.InRange(Pos(tap, -1f).Value, (short)-4200, (short)-4000);
        }

        [Fact]
        public void HiResSpring_GateOn_SBitInvertsThatSide()
        {
            // S2 set: right of the band the engine copy pushes AWAY, the same
            // inversion the classic path applies.
            var tap = Run(Record(B(0x21, 0x0b, 0x80, 0x80, 0x88, 0x10, 0xff)));
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
            // S1 clear, so the left side still pulls back toward center.
            Assert.InRange(Pos(tap, -RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
        }

        [Fact]
        public void HiResSpring_GateOff_NothingReachesTheEngine()
        {
            var tap = NewTap(gate: false);
            Feed(tap, Record(CenteredK8Spring));
            Assert.Equal(0, tap.HidppEffects.ExternalConditionUpdates);
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Pos(tap, 0.5f));
            // The shipped spring path is untouched by the gate's absence.
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.Equal((short)32767, tap.TryEvaluateClassicSprings(RightOfBandBy0p1).Value);
        }

        [Fact]
        public void HiResSpring_BareDownload_DoesNotPlayUntilPlay()
        {
            // cmd 0 into a stopped slot loads without playing, as for every
            // other type; the later PLAY starts it.
            var loaded = B(0x20, 0x0b, 0x80, 0x80, 0x88, 0x00, 0xff);
            var tap = Run(Record(loaded));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Pos(tap, +RightOfBandBy0p1));
            // Same capture, plus the PLAY (a second ParseFrom is a new
            // capture and resets the slot table).
            var played = Run(Record(loaded), Record(Play2));
            Assert.InRange(Pos(played, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
        }

        [Fact]
        public void HiResSpring_Stop_SilencesTheEngineCopy()
        {
            var tap = Run(Record(CenteredK8Spring), Record(Stop2));
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Pos(tap, +RightOfBandBy0p1));
            Assert.False(tap.AnyClassicSpringPlaying);
            // A later PLAY in the same capture brings both renderers back.
            var again = Run(Record(CenteredK8Spring), Record(Stop2), Record(Play2));
            Assert.InRange(Pos(again, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
            Assert.True(again.AnyClassicSpringPlaying);
        }

        [Fact]
        public void HiResSpring_ReplacedByVariableForce_EngineCopyGone()
        {
            var tap = Run(Record(CenteredK8Spring),
                          Record(B(0x21, 0x08, 0xc0, 0x00, 0x00, 0x00, 0x00)));
            Assert.False(tap.AnyClassicSpringPlaying);
            Assert.False(tap.AnyHidppParametricPlaying);
            Assert.Null(Pos(tap, +RightOfBandBy0p1));
            Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
        }

        [Fact]
        public void HiResSpring_ReplacedByDamper_LeavesOnlyTheDamper()
        {
            var tap = Run(Record(CenteredK8Spring), Record(WinDamper));
            Assert.False(tap.AnyClassicSpringPlaying);
            // Position alone renders nothing now; velocity renders the damper.
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
        }

        // ---------------- the spring-mode stand-down ----------------

        [Fact]
        public void SpringMode_StandsDownTheEngineCopy_AndDisarmRestoresIt()
        {
            // Spring mode renders the captured spring itself, as the base
            // force, so the additive copy must go silent while it is armed or
            // the wheel gets the same spring twice. No packet arrives between
            // the arm and the disarm: the whole point of reading the flag at
            // evaluation time is that a quiet bus cannot leave it stuck.
            var tap = Run(Record(CenteredK8Spring));
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);

            tap.ClassicSpringModeActive = true;
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            Assert.Equal((short)0, Pos(tap, -RightOfBandBy0p1).Value);
            // Still captured game FFB: the quiet probes and the no-FFB
            // watchdog must keep seeing it, and the spring path still renders.
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.True(tap.AnyClassicSpringPlaying);
            Assert.InRange(tap.TryEvaluateClassicSprings(RightOfBandBy0p1).Value, (short)3400, (short)3600);

            tap.ClassicSpringModeActive = false;
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
        }

        [Fact]
        public void SpringMode_StandDown_LeavesTheOtherConditionsAlone()
        {
            // Only the spring has a second renderer. A damper in another slot
            // keeps rendering while spring mode is armed.
            //
            // Probed AT the spring's position, not at zero: the centered
            // spring is worth about eight counts at zero, so a leaked spring
            // term there hides inside any tolerance the damper needs. Out
            // where the spring is worth 3495, the two sums differ by that
            // much and the assertion can actually fail.
            var tap = Run(Record(CenteredK8Spring), Record(B(0x41, 0x0c, 0x08, 0x00, 0x08, 0x00, 0x01)));
            int both = PosVel(tap, +RightOfBandBy0p1, +1f).Value;
            tap.ClassicSpringModeActive = true;
            int damperOnly = PosVel(tap, +RightOfBandBy0p1, +1f).Value;
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            // The damper is untouched, and the spring's 3495 counts are gone.
            Assert.InRange(damperOnly, 16500, 18500);
            Assert.InRange(both - damperOnly, 3400, 3600);
        }

        [Fact]
        public void SpringMode_ArmedBeforeTheDownload_StandsDownTheNewSpring()
        {
            // The other order: the mode is already armed when the game sends
            // its spring. The download still lands (so a disarm has something
            // to render) and still renders nothing while armed.
            var tap = NewTap();
            tap.ClassicSpringModeActive = true;
            Feed(tap, Record(CenteredK8Spring));
            Assert.Equal(1, tap.HidppEffects.ExternalConditionUpdates);
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            tap.ClassicSpringModeActive = false;
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
        }

        [Fact]
        public void SpringMode_StandDown_DoesNotOutliveTheSpring()
        {
            // Armed the whole time, and the game replaces the spring with a
            // damper in the same slot: the stand-down must not follow the
            // slot onto the new effect.
            var tap = NewTap();
            tap.ClassicSpringModeActive = true;
            Feed(tap, Record(CenteredK8Spring), Record(WinDamper));
            Assert.InRange(Vel(tap, +1f).Value, (short)16500, (short)18500);
            Assert.False(tap.AnyClassicSpringPlaying);
        }

        [Fact]
        public void SpringMode_StandDown_SurvivesAStopPlayRoundTrip()
        {
            // The flag lives on the effect, and a classic STOP/PLAY rebuilds
            // that effect (CloneWith). If the rebuild ever dropped it, the
            // wheel would get the spring twice while the mode is armed and
            // nothing else in the suite would notice: the stand-down tests
            // send no STOP, and the STOP test does not arm the mode. 109 slot
            // STOPs in the FH5 corpus, so this is a real wire sequence.
            var tap = Run(Record(CenteredK8Spring), Record(Stop2), Record(Play2));
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);

            tap.ClassicSpringModeActive = true;
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            Assert.True(tap.AnyHidppParametricPlaying);

            tap.ClassicSpringModeActive = false;
            Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
        }

        [Fact]
        public void SpringMode_StandDown_DoesNotReachThePauseRelease()
        {
            // The pause and focus releases evaluate the game's conditions on
            // their own and return that value directly: they never reach the
            // substitution that makes the stand-down necessary. Standing the
            // captured spring down there would hand a menu a limp wheel,
            // which is the failure those releases exist to prevent.
            var tap = Run(Record(CenteredK8Spring));
            tap.ClassicSpringModeActive = true;
            Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
            Assert.InRange(PosReleased(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
            Assert.InRange(PosReleased(tap, -RightOfBandBy0p1).Value, (short)-3600, (short)-3400);
        }

        [Fact]
        public void SpringModeDisarm_KeepsTheEngineCopy()
        {
            // The disarm's own reset used to be the full ClearLastFfbTarget,
            // which armed a classic reset and wiped the engine's external
            // slots at the next packet. The game is mid-session there, not
            // paused, and Forza sends its menu spring once and leaves it
            // playing, so the autocenter this whole route exists to render
            // went silent for the rest of the session the moment the mode
            // handed back. Only the stale scalar goes now.
            var tap = NewTap();
            var stream = new BlockingStream();
            var parser = StartParser(tap, stream);
            try
            {
                // A road force in slot 3 beside the menu spring in slot 2:
                // the mixed traffic this change is about.
                stream.Push(PcapHeader(),
                            PcapRecord(Record(B(0x41, 0x08, 0xc0, 0x00, 0x00, 0x00, 0x00))),
                            PcapRecord(Record(CenteredK8Spring)));
                WaitUntil(() => tap.AnyHidppParametricPlaying, "the spring to play");
                Assert.NotNull(tap.TryGetFreshFfbTarget(1000));

                // Spring mode arms on the quiet bus, then hands back.
                tap.ClassicSpringModeActive = true;
                Assert.Equal((short)0, Pos(tap, +RightOfBandBy0p1).Value);
                tap.ClearCapturedForceKeepingEffects();
                tap.ClassicSpringModeActive = false;

                // The stale scalar is gone from this thread, the spring is not.
                Assert.Null(tap.TryGetFreshFfbTarget(1000));
                Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);

                // A bare PLAY: the deferred scalar reset is honored, so the
                // slot's stale force cannot republish, and the engine spring
                // is still there.
                long seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(Play2)), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the PLAY and its barrier");
                Assert.Null(tap.TryGetFreshFfbTarget(1000));
                Assert.True(tap.AnyHidppParametricPlaying);
                Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);

                // Driving again: the game's road force publishes normally and
                // the spring keeps summing on top of it.
                seen = tap.PacketsParsed;
                stream.Push(PcapRecord(Record(B(0x41, 0x08, 0xc0, 0x00, 0x00, 0x00, 0x00))), Barrier());
                WaitUntil(() => tap.PacketsParsed >= seen + 2, "the road force and its barrier");
                Assert.Equal((short)(64 << 8), tap.TryGetFreshFfbTarget(1000));
                Assert.InRange(Pos(tap, +RightOfBandBy0p1).Value, (short)3400, (short)3600);
            }
            finally
            {
                stream.Complete();
                parser.Join(5000);
            }
        }

        // ---------------- low-res spring 0x01 and auto-center 0x0d ----------
        //
        // The other spring types, routed into the engine on 2026-09-17. Their
        // decoded band only started reaching the engine on 2026-09-18 (the
        // upsert used to pass a hardcoded 0, 0 for deadband and center), and
        // nothing covered them until now, so an off-center band could have
        // landed on the wrong side of the wheel unnoticed.

        // Band edges at axis counts 0x60 and 0xe0, K = 3 both sides, no
        // inversion, full clip. In steerNorm that is a band from -0.247 to
        // +0.757, centered on +0.255: deliberately NOT symmetric about zero,
        // so a center dropped on the floor renders force where this asserts
        // silence and vice versa.
        private static readonly byte[] OffCenterLoResSpring = { 0x21, 0x01, 0x60, 0xe0, 0x33, 0x00, 0xff };

        [Fact]
        public void LoResSpring_BandLandsWhereTheBytesPutIt()
        {
            var tap = Run(Record(OffCenterLoResSpring));
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.Equal(1, tap.HidppEffects.ExternalConditionUpdates);
            // Inside the band, both sides of the wheel's own center: silent.
            Assert.Equal((short)0, Pos(tap, 0f).Value);
            Assert.Equal((short)0, Pos(tap, -0.2f).Value);
            // Still inside at +0.7, which a center of zero would have put
            // 0.2 beyond the edge and rendered at about 1638 counts.
            Assert.Equal((short)0, Pos(tap, 0.7f).Value);
            // Past the lower edge by 0.053: Table 27 K=3 is 1/4, so -434.
            // A center of zero renders nothing at all here.
            Assert.InRange(Pos(tap, -0.3f).Value, (short)-500, (short)-380);
            // Past the upper edge by 0.143: +1173.
            Assert.InRange(Pos(tap, 0.9f).Value, (short)1100, (short)1250);
            // A spring publishes no scalar, and this type has no second
            // renderer: the ClassicSpring path is for 0x0b alone.
            Assert.Null(tap.TryGetFreshFfbTarget(1000));
            Assert.False(tap.AnyClassicSpringPlaying);
        }

        [Fact]
        public void HiResAutoCenter_CentersOnZeroWithATwoCountBand()
        {
            // Table 50: K1, K2 and CLIP at bytes 2, 3 and 4. K = 8 linear,
            // full clip, so 8/15 per unit of steerNorm beyond a band one
            // axis count wide either side of dead center.
            var tap = Run(Record(B(0x21, 0x0d, 0x08, 0x08, 0xff, 0x00, 0x00)));
            Assert.True(tap.AnyHidppParametricPlaying);
            Assert.Equal((short)0, Pos(tap, 0f).Value);
            // Inside the band (1/127.5 = 0.00784), then just outside it.
            Assert.Equal((short)0, Pos(tap, 0.004f).Value);
            Assert.InRange(Pos(tap, 0.02f).Value, (short)180, (short)250);
            // Symmetric about zero, 0.5 x 8/15 less the band = 8601.
            Assert.InRange(Pos(tap, 0.5f).Value, (short)8450, (short)8750);
            Assert.InRange(Pos(tap, -0.5f).Value, (short)-8750, (short)-8450);
        }

        [Fact]
        public void SpringTypes_LogTheirBand()
        {
            // The first-decode line is what a rig session reads back, and for
            // the spring family the band is the decode worth checking: a
            // slope beside no band cannot show that the center landed where
            // the game put it.
            var lines = new List<string>();
            var tap = NewTap();
            tap.Logger = lines.Add;
            Feed(tap, Record(OffCenterLoResSpring), Record(WinDamper));
            string spring = Assert.Single(lines, l => l.Contains("spring 0x01"));
            Assert.Contains("band center=+0.255 halfWidth=0.502", spring);
            // The damper carries no band, so its line is unchanged.
            string damper = Assert.Single(lines, l => l.Contains("classic damper 0x0C"));
            Assert.DoesNotContain("band center", damper);
        }
    }
}
