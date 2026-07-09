using System;
using System.IO;
using System.Linq;
using System.Threading;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Phase 0a coverage: FM8 (Motorsport 2023) 331-byte packet support with
    // runtime dash-base plausibility resolution, the per-tire TireQuad fields
    // on TelemetryFrame, and the GoldenCaptureLog v2 fixture format. Synthetic
    // packets straight into the internal ParsePacket, same pattern as
    // ForzaUdpTelemetrySourceTests.
    public class ForzaFm8AndQuadTests
    {
        private const int Fm8Length     = 331;   // FM2023: sled + dash@232 + tire wear + track ordinal
        private const int Fm7Length     = 311;   // FM7: sled + dash@232
        private const int HorizonLength = 324;   // FH4/5/6: sled + 12-byte insert + dash@244 + 1 trailing

        // Sled offsets (identical across every Forza title).
        private const int OFF_IS_RACE_ON    = 0;
        private const int OFF_ENGINE_MAX    = 8;
        private const int OFF_CURRENT_RPM   = 16;
        private const int OFF_NORM_SUSP_FL  = 68;
        private const int OFF_SLIP_RATIO_FL = 84;
        private const int OFF_WHEEL_ROT_FL  = 100;
        private const int OFF_SURFACE_FL    = 148;
        private const int OFF_SLIP_ANGLE_FL = 164;
        private const int OFF_COMBINED_FL   = 180;
        private const int OFF_SUSP_M_FL     = 196;
        private const int OFF_CAR_ORDINAL   = 212;
        private const int OFF_NUM_CYL       = 228;

        private static void PutInt32(byte[] b, int off, int v) =>
            BitConverter.GetBytes(v).CopyTo(b, off);
        private static void PutFloat(byte[] b, int off, float v) =>
            BitConverter.GetBytes(v).CopyTo(b, off);

        // Fill the sled with a plausible driving frame plus per-corner values
        // that are distinct (base + 0.01*corner) so the tests can prove each
        // corner landed in the right TireQuad slot.
        private static byte[] SledBase(int len, float maxRpm = 8000f, float rpm = 5000f)
        {
            var b = new byte[len];
            PutInt32(b, OFF_IS_RACE_ON, 1);
            PutFloat(b, OFF_ENGINE_MAX, maxRpm);
            PutFloat(b, OFF_CURRENT_RPM, rpm);
            for (int i = 0; i < 4; i++)
            {
                PutFloat(b, OFF_NORM_SUSP_FL  + i * 4, 0.5f);
                PutFloat(b, OFF_SLIP_RATIO_FL + i * 4, 0.10f + 0.01f * i);
                PutFloat(b, OFF_WHEEL_ROT_FL  + i * 4, 50f + i);
                PutFloat(b, OFF_SURFACE_FL    + i * 4, 0.20f + 0.01f * i);
                PutFloat(b, OFF_SLIP_ANGLE_FL + i * 4, 0.05f + 0.01f * i);
                PutFloat(b, OFF_COMBINED_FL   + i * 4, 0.60f + 0.01f * i);
                PutFloat(b, OFF_SUSP_M_FL     + i * 4, 0.02f + 0.01f * i);
            }
            PutInt32(b, OFF_CAR_ORDINAL, 1234);
            PutInt32(b, OFF_NUM_CYL, 6);
            return b;
        }

        // FM8/FM7 dash sits directly after the sled at base 232.
        private static void PutMotorsportDash(byte[] b, float speedMs = 30f,
            byte accel = 200, byte brake = 0, byte gear = 4, sbyte steer = 32)
        {
            const int Base = 232;
            PutFloat(b, Base + 12, speedMs);
            b[Base + 71] = accel;
            b[Base + 72] = brake;
            b[Base + 75] = gear;
            b[Base + 76] = unchecked((byte)steer);
        }

        private static ForzaUdpTelemetrySource NewSource() => new ForzaUdpTelemetrySource(5300);

        // Open + spend the spawn settle window so grip channels flow.
        private static ForzaUdpTelemetrySource SettledSource(byte[] packet, int len)
        {
            var src = NewSource();
            src.ParsePacket(packet, len);
            Thread.Sleep(450);
            return src;
        }

        // ---------------- FM8 packet dispatch ----------------

        [Fact]
        public void Fm8Packet_ExtractsDashAt232()
        {
            var b = SledBase(Fm8Length);
            PutMotorsportDash(b, speedMs: 30f, accel: 255, gear: 4, steer: 64);
            var src = SettledSource(b, Fm8Length);

            var f = src.ParsePacket(b, Fm8Length);

            Assert.Equal(30.0 * 3.6, f.SpeedKmh, 3);
            Assert.Equal(1.0, f.Throttle01, 3);
            Assert.Equal("3", f.Gear);
            Assert.Equal(64.0 / 127.0, f.SteeringAngle.GetValueOrDefault(), 4);
            Assert.Equal(5000.0, f.Rpms, 3);
        }

        [Fact]
        public void Fm7Packet_ExtractsDashAt232()
        {
            // FM7's 311-byte packet has the same dash at the same base; the old
            // parser ignored it entirely (dash was gated on len >= 324).
            var b = SledBase(Fm7Length);
            PutMotorsportDash(b, speedMs: 20f, gear: 3);
            var src = SettledSource(b, Fm7Length);

            var f = src.ParsePacket(b, Fm7Length);

            Assert.Equal(20.0 * 3.6, f.SpeedKmh, 3);
            Assert.Equal("2", f.Gear);
        }

        [Fact]
        public void Fm8Length_IsValidPacketCandidate()
        {
            Assert.True(ForzaUdpTelemetrySource.IsValidPacketCandidate(new byte[Fm8Length], Fm8Length));
        }

        // ---------------- Dash-base plausibility resolution ----------------

        [Fact]
        public void DashBase_FallsBackWhenDocumentedBaseImplausible()
        {
            // Hypothetical shifted layout: garbage where the documented FM8 base
            // (232) expects speed/gear, plausible values at the Horizon base
            // (244). The resolver must fall back rather than feed garbage speed.
            var b = SledBase(Fm8Length);
            PutFloat(b, 232 + 12, 1e9f);   // "speed" at base 232: absurd
            b[232 + 75] = 200;             // "gear" at base 232: absurd
            PutFloat(b, 244 + 12, 30f);    // speed at base 244: plausible
            b[244 + 71] = 128;             // throttle at 244
            b[244 + 75] = 4;               // gear at 244: plausible

            var src = SettledSource(b, Fm8Length);
            var f = src.ParsePacket(b, Fm8Length);

            Assert.Equal(30.0 * 3.6, f.SpeedKmh, 3);
            Assert.Equal("3", f.Gear);
        }

        [Fact]
        public void DashBase_NoPlausibleBase_SledStillFlows()
        {
            var b = SledBase(Fm8Length);
            PutFloat(b, 232 + 12, float.NaN);
            b[232 + 75] = 200;
            PutFloat(b, 244 + 12, -1e9f);
            b[244 + 75] = 250;

            var src = SettledSource(b, Fm8Length);
            var f = src.ParsePacket(b, Fm8Length);

            Assert.Equal(0.0, f.SpeedKmh, 6);        // no dash: idle defaults
            Assert.Null(f.SteeringAngle);
            Assert.Equal(5000.0, f.Rpms, 3);         // sled channels unaffected
            Assert.True(f.HasTireQuads);
        }

        [Fact]
        public void DashBase_KeepalivePacketDoesNotCacheResolution()
        {
            // An all-zero keepalive passes plausibility at ANY base (0 speed,
            // 0 gear). It must not lock in a base for the session; the first
            // LIVE packet decides. Feed a keepalive, then a live packet whose
            // documented base is implausible: the live packet must still fall
            // back to the alternate.
            var src = NewSource();
            var keepalive = new byte[Fm8Length];          // maxRpm == 0 -> keepalive
            src.ParsePacket(keepalive, Fm8Length);
            Thread.Sleep(450);

            var b = SledBase(Fm8Length);
            PutFloat(b, 232 + 12, 1e9f);
            b[232 + 75] = 200;
            PutFloat(b, 244 + 12, 25f);
            b[244 + 75] = 5;
            var f = src.ParsePacket(b, Fm8Length);

            Assert.Equal(25.0 * 3.6, f.SpeedKmh, 3);
        }

        // ---------------- Per-tire quads ----------------

        [Fact]
        public void LiveFrame_PopulatesAllQuadsPerCorner()
        {
            var b = SledBase(HorizonLength);
            var src = SettledSource(b, HorizonLength);

            var f = src.ParsePacket(b, HorizonLength);

            Assert.True(f.HasTireQuads);
            // Distinct per-corner values prove FL/FR/RL/RR landed in order.
            Assert.Equal(0.10, f.TireSlipRatio.FL, 4);
            Assert.Equal(0.11, f.TireSlipRatio.FR, 4);
            Assert.Equal(0.12, f.TireSlipRatio.RL, 4);
            Assert.Equal(0.13, f.TireSlipRatio.RR, 4);
            Assert.Equal(0.06, f.TireSlipAngleRad.FR, 4);
            Assert.Equal(0.63, f.TireCombinedSlip.RR, 4);
            Assert.Equal(0.04, f.SuspTravelM.RL, 4);
            Assert.Equal(0.21, f.SurfaceRumbleQ.FR, 4);
            Assert.Equal(52.0, f.WheelRotRadS.RL, 3);
            // Existing scalar fields stay consistent with the quads. WheelSlip is
            // now the LOAD-WEIGHTED whole-car slip (issue #30), not the max: with
            // all four suspensions equally loaded here (SledBase sets 0.5 each) it
            // is the plain mean of the combined-slip quad.
            double expectedWeighted = (f.TireCombinedSlip.FL + f.TireCombinedSlip.FR
                                     + f.TireCombinedSlip.RL + f.TireCombinedSlip.RR) / 4.0;
            Assert.Equal(expectedWeighted, f.WheelSlip.GetValueOrDefault(), 6);
            Assert.Equal(f.TireSlipAngleRad.FrontAvg, f.FrontSlipAngleRad.GetValueOrDefault(), 6);
        }

        [Fact]
        public void SpawnFrame_SuppressesQuadsButWheelRotFlows()
        {
            // During the settle window the grip quads are zeroed like the scalar
            // grip channels; wheel rotation is kinematics and keeps flowing.
            var src = NewSource();
            var f = src.ParsePacket(SledBase(HorizonLength), HorizonLength);

            Assert.True(f.HasTireQuads);
            Assert.Equal(0.0, f.TireCombinedSlip.MaxAbs, 6);
            Assert.Equal(0.0, f.TireSlipAngleRad.FrontAvg, 6);
            Assert.True(f.WheelRotRadS.FL > 0.0);
        }

        [Fact]
        public void KeepalivePacket_HasNoQuads()
        {
            var src = NewSource();
            var f = src.ParsePacket(new byte[HorizonLength], HorizonLength);
            Assert.False(f.HasTireQuads);
        }

        [Fact]
        public void TireQuad_Rollups()
        {
            var q = TireQuad.Of(0.1, 0.3, -0.5, 0.2);
            Assert.Equal(0.2, q.FrontAvg, 6);
            Assert.Equal(-0.15, q.RearAvg, 6);
            Assert.Equal(0.5, q.MaxAbs, 6);
            Assert.Equal(0.1, q[0], 6);
            Assert.Equal(0.3, q[1], 6);
            Assert.Equal(-0.5, q[2], 6);
            Assert.Equal(0.2, q[3], 6);
            Assert.Throws<ArgumentOutOfRangeException>(() => q[4]);
        }

        // ---------------- GoldenCaptureLog v2 ----------------

        private static TelemetryFrame QuadFrame() => new TelemetryFrame
        {
            Rpms = 5000, MaxRpm = 8000, Throttle01 = 0.75, SpeedKmh = 108,
            Gear = "3", AccelerationSway = -3.2, YawRateDegPerSec = 12.5,
            WheelSlip = 0.63, FrontSlipAngleRad = 0.055, FrontSuspTravelMeters = 0.035,
            SurfaceRumble = 0.23, OnRumbleStrip = false, Airborne = false,
            NumCylinders = 6, RedlineRpm = 7600,
            HasTireQuads = true,
            TireSlipRatio    = TireQuad.Of(0.10, 0.11, 0.12, 0.13),
            TireSlipAngleRad = TireQuad.Of(0.05, 0.06, 0.07, 0.08),
            TireCombinedSlip = TireQuad.Of(0.60, 0.61, 0.62, 0.63),
            SuspTravelM      = TireQuad.Of(0.02, 0.03, 0.04, 0.05),
            SurfaceRumbleQ   = TireQuad.Of(0.20, 0.21, 0.22, 0.23),
            WheelRotRadS     = TireQuad.Of(50, 51, 52, 53),
            CapturedAtTicks  = System.Diagnostics.Stopwatch.GetTimestamp(),
        };

        [Fact]
        public void GoldenRow_ColumnCountMatchesHeader_WithAndWithoutQuads()
        {
            int headerCols = GoldenCaptureLog.Header.Split(',').Length;

            var withQuads = GoldenCaptureLog.FormatRow(QuadFrame(), sessionActive: true, gameFfb: 1234);
            Assert.Equal(headerCols, withQuads.Split(',').Length);

            var bare = GoldenCaptureLog.FormatRow(new TelemetryFrame { Gear = "N" }, sessionActive: false, gameFfb: null);
            Assert.Equal(headerCols, bare.Split(',').Length);
        }

        [Fact]
        public void GoldenRow_QuadValuesLandInDeclaredColumns()
        {
            var cols = GoldenCaptureLog.Header.Split(',').ToList();
            var vals = GoldenCaptureLog.FormatRow(QuadFrame(), true, null).Split(',');

            Assert.Equal("0.1",  vals[cols.IndexOf("slip_ratio_fl")]);
            Assert.Equal("0.13", vals[cols.IndexOf("slip_ratio_rr")]);
            Assert.Equal("0.08", vals[cols.IndexOf("slip_angle_rr")]);
            Assert.Equal("53",   vals[cols.IndexOf("wheel_rot_rr")]);
            Assert.Equal("0.63", vals[cols.IndexOf("combined_rr")]);
            Assert.Equal("3",    vals[cols.IndexOf("gear")]);
            Assert.Equal("1",    vals[cols.IndexOf("session_active")]);
            Assert.Equal("",     vals[cols.IndexOf("game_ffb")]);
        }

        [Fact]
        public void GoldenRow_MissingQuads_AreEmptyCellsNotZeros()
        {
            var cols = GoldenCaptureLog.Header.Split(',').ToList();
            var vals = GoldenCaptureLog.FormatRow(new TelemetryFrame { Gear = "N" }, false, null).Split(',');

            Assert.Equal("", vals[cols.IndexOf("slip_ratio_fl")]);
            Assert.Equal("", vals[cols.IndexOf("wheel_rot_rr")]);
            Assert.Equal("", vals[cols.IndexOf("steer")]);       // null scalar too
            Assert.Equal("0", vals[cols.IndexOf("session_active")]);
        }

        [Fact]
        public void GoldenLog_FileRoundTrip_MetaHeaderAndRows()
        {
            string path = Path.Combine(Path.GetTempPath(), "tfcap_test_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (var log = new GoldenCaptureLog(path, "Forza (UDP)", "ForzaMotorsport"))
                {
                    log.LogRow(QuadFrame(), true, 4321);
                    log.LogRow(QuadFrame(), true, null);
                    Assert.Equal(2, log.RowsWritten);
                }

                var lines = File.ReadAllLines(path);
                Assert.StartsWith("#tfcap v=2 src=Forza (UDP) game=ForzaMotorsport recorded=", lines[0]);
                Assert.Equal(GoldenCaptureLog.Header, lines[1]);
                Assert.Equal(4, lines.Length);
                Assert.EndsWith(",4321", lines[2]);
                Assert.EndsWith(",", lines[3]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void GoldenLog_MetaLine_StripsCommasFromFreeText()
        {
            var meta = GoldenCaptureLog.MetaLine("a,b", "c,d", new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal("#tfcap v=2 src=a;b game=c;d recorded=2026-07-03T12:00:00Z", meta);
        }
    }
}
