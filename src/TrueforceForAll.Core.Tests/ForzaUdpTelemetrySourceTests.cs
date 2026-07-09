using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tests the Forza Data Out packet parser (the part most recently changed:
    // Steer -> SteeringAngle for the stationary spring, plus CarOrdinal, the
    // spawn settle window, and Sled-vs-Dash tolerance). Feeds synthetic
    // packets straight into the internal ParsePacket, no socket involved.
    public class ForzaUdpTelemetrySourceTests
    {
        // Forza "Horizon dash" packet (FH4/FH5/FH6 shape). Offsets mirror the
        // private OFF_* constants in ForzaUdpTelemetrySource.
        private const int HorizonDashLength = 324;
        private const int OFF_IS_RACE_ON   = 0;
        private const int OFF_ENGINE_MAX   = 8;
        private const int OFF_CURRENT_RPM  = 16;
        private const int OFF_ACCEL_Y      = 24;   // heave (m/s^2, up)
        private const int OFF_NORM_SUSP_FL = 68;   // normalized susp travel[4], 0=droop..1=compressed
        private const int OFF_SLIP_ANGLE_FL = 164; // tyre slip angle[4], radians, signed
        private const int OFF_COMBINED_FL  = 180;  // tyre combined slip[4]
        private const int OFF_SUSP_TRAVEL_M_FL = 196; // suspension travel[4], meters
        private const int OFF_CAR_ORDINAL  = 212;
        private const int OFF_NUM_CYL      = 228;
        private const int OFF_SPEED        = 256;  // m/s
        private const int OFF_ACCEL_PEDAL  = 315;  // 0..255
        private const int OFF_GEAR         = 319;  // 0=R,1=N,2..=fwd
        private const int OFF_STEER        = 320;  // int8, -127..127

        private static void PutInt32(byte[] b, int off, int v) =>
            BitConverter.GetBytes(v).CopyTo(b, off);
        private static void PutFloat(byte[] b, int off, float v) =>
            BitConverter.GetBytes(v).CopyTo(b, off);

        // Build a Horizon-dash packet with the fields the parser reads. Defaults
        // are a plausible "driving" frame; override per test.
        private static byte[] DashPacket(
            int raceOn = 1, float maxRpm = 8000f, float rpm = 5000f, float heave = 9.0f,
            float combinedSlip = 0.7f, int carOrdinal = 2468, int cylinders = 8,
            float speedMs = 30f, byte accel = 200, byte gear = 4, sbyte steer = 0,
            float suspTravel = 0.5f, float slipAngleRad = 0f, float suspTravelM = 0.2f)
        {
            var b = new byte[HorizonDashLength];
            PutInt32(b, OFF_IS_RACE_ON, raceOn);
            PutFloat(b, OFF_ENGINE_MAX, maxRpm);
            PutFloat(b, OFF_CURRENT_RPM, rpm);
            PutFloat(b, OFF_ACCEL_Y, heave);
            for (int i = 0; i < 4; i++) PutFloat(b, OFF_NORM_SUSP_FL + i * 4, suspTravel);
            for (int i = 0; i < 4; i++) PutFloat(b, OFF_SLIP_ANGLE_FL + i * 4, slipAngleRad);
            for (int i = 0; i < 4; i++) PutFloat(b, OFF_COMBINED_FL + i * 4, combinedSlip);
            for (int i = 0; i < 4; i++) PutFloat(b, OFF_SUSP_TRAVEL_M_FL + i * 4, suspTravelM);
            PutInt32(b, OFF_CAR_ORDINAL, carOrdinal);
            PutInt32(b, OFF_NUM_CYL, cylinders);
            PutFloat(b, OFF_SPEED, speedMs);
            b[OFF_ACCEL_PEDAL] = accel;
            b[OFF_GEAR] = gear;
            b[OFF_STEER] = unchecked((byte)steer);
            return b;
        }

        private static ForzaUdpTelemetrySource NewSource() => new ForzaUdpTelemetrySource(5300);

        [Fact]
        public void DrivingFrame_ExtractsCoreFields()
        {
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(rpm: 5000f, maxRpm: 8000f, speedMs: 30f,
                                               accel: 255, gear: 4, cylinders: 8, carOrdinal: 2468), HorizonDashLength);

            Assert.Equal(5000.0, f.Rpms, 3);
            Assert.Equal(8000.0, f.MaxRpm, 3);
            Assert.Equal(30.0 * 3.6, f.SpeedKmh, 3);   // m/s -> km/h
            Assert.Equal(1.0, f.Throttle01, 3);         // 255/255
            Assert.Equal("3", f.Gear);                  // gear byte 4 -> "3"
            Assert.Equal(8, f.NumCylinders);
            Assert.Equal(2468, src.CurrentCarOrdinal);
        }

        [Theory]
        [InlineData((sbyte)127, 1.0)]
        [InlineData((sbyte)-127, -1.0)]
        [InlineData((sbyte)0, 0.0)]
        [InlineData((sbyte)64, 64.0 / 127.0)]
        public void Steer_NormalizedToPlusMinusOne(sbyte steerByte, double expected)
        {
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(steer: steerByte), HorizonDashLength);

            Assert.True(f.SteeringAngle.HasValue);
            Assert.Equal(expected, f.SteeringAngle.Value, 4);
        }

        [Fact]
        public void SpawnFrame_IsSettling_SuppressesImpactButFlowsMotion()
        {
            // First raceOn=1 frame on a fresh source is the IsRaceOn 0->1 edge,
            // so the settle window is open: impact/grip channels must be zeroed
            // (no spawn jolt) while engine/speed/steer flow normally.
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(heave: 50f, combinedSlip: 0.9f, steer: 64), HorizonDashLength);

            Assert.Equal(0.0, f.AccelerationHeave.GetValueOrDefault(), 6); // suppressed
            Assert.Equal(0.0, f.WheelSlip.GetValueOrDefault(), 6);         // suppressed
            Assert.True(f.Rpms > 0);                                       // engine flows
            Assert.True(f.SpeedKmh > 0);                                   // speed flows
            Assert.True(f.SteeringAngle.HasValue);                         // steering flows
        }

        [Fact]
        public void AfterSettleWindow_ImpactChannelsResume()
        {
            // The settle window is 400 ms wall-clock. Open it, wait it out, then
            // a subsequent raceOn=1 frame must let impact/grip through again.
            var src = NewSource();
            src.ParsePacket(DashPacket(heave: 50f, combinedSlip: 0.9f), HorizonDashLength);
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(heave: 50f, combinedSlip: 0.9f), HorizonDashLength);

            Assert.True(Math.Abs(f.AccelerationHeave.GetValueOrDefault()) > 0.0);
            Assert.True(f.WheelSlip.GetValueOrDefault() > 0.0);
        }

        [Fact]
        public void Airborne_FlagSetWhenAllWheelsDrooped()
        {
            // All four wheels drooped (suspension travel ~0) = car in the air.
            // Past the settle window, the Airborne flag is set so AirborneEffect
            // can duck the configured voices; raw channels still flow (the
            // effect, not the source, decides what to silence).
            var src = NewSource();
            src.ParsePacket(DashPacket(), HorizonDashLength);   // open + spend settle window
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(combinedSlip: 0.9f, suspTravel: 0.0f), HorizonDashLength);

            Assert.True(f.Airborne.GetValueOrDefault());
            Assert.True(f.Rpms > 0);        // engine flows
            Assert.True(f.SpeedKmh > 0);    // speed flows
        }

        [Fact]
        public void Grounded_LightlyLoadedSuspension_NotAirborne()
        {
            // A loaded wheel sits well off full droop even when light; the flag
            // stays clear (we only call it airborne when all four are drooped).
            var src = NewSource();
            src.ParsePacket(DashPacket(), HorizonDashLength);
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(combinedSlip: 0.9f, suspTravel: 0.1f), HorizonDashLength);

            Assert.False(f.Airborne.GetValueOrDefault());
            Assert.True(f.WheelSlip.GetValueOrDefault() > 0.0);
        }

        [Fact]
        public void Airborne_LoadWeightedSlip_GoesSilent()
        {
            // Issue #30: a car in the air spins its unloaded wheels, so combined
            // slip spikes, but no grip is being lost. Weighting each wheel by its
            // suspension load drops the airborne wheels out, so the direct slip
            // reads ~0 (silent) even with airborne ducking off.
            var src = NewSource();
            src.ParsePacket(DashPacket(suspTravel: 0.5f), HorizonDashLength);   // grounded: arm the load latch + open settle
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(combinedSlip: 0.9f, suspTravel: 0.0f), HorizonDashLength);

            Assert.True(f.Airborne.GetValueOrDefault());
            Assert.Equal(0.0, f.WheelSlip.GetValueOrDefault(), 6);
        }

        [Fact]
        public void OneWheelLift_LoadWeightedSlip_TracksGroundedWheels()
        {
            // Issue #30: one wheel lifted over a kerb spins up (high slip, ~0
            // load) while the other three stay planted with little slip. The load
            // weighting follows the grounded three, not the lone spinning wheel
            // (the old max-abs would have read the 0.90 spike at full strength).
            var src = NewSource();
            src.ParsePacket(DashPacket(suspTravel: 0.5f), HorizonDashLength);
            Thread.Sleep(450);

            var b = DashPacket(suspTravel: 0.5f);        // three grounded wheels loaded...
            PutFloat(b, OFF_COMBINED_FL + 0, 0.05f);     // FL grounded, low slip
            PutFloat(b, OFF_COMBINED_FL + 4, 0.05f);     // FR grounded, low slip
            PutFloat(b, OFF_COMBINED_FL + 8, 0.05f);     // RL grounded, low slip
            PutFloat(b, OFF_COMBINED_FL + 12, 0.90f);    // RR spun up...
            PutFloat(b, OFF_NORM_SUSP_FL + 12, 0.0f);    // ...and lifted (no load)
            var f = src.ParsePacket(b, HorizonDashLength);

            Assert.False(f.Airborne.GetValueOrDefault());   // not all four drooped
            // Grounded three dominate: ~0.05, nowhere near the lifted wheel's 0.90.
            Assert.InRange(f.WheelSlip.GetValueOrDefault(), 0.04, 0.10);
        }

        [Fact]
        public void DeadSuspChannel_LoadWeightedSlip_FallsBackToLegacyMax()
        {
            // A hypothetical Forza title that never populates normalized
            // suspension travel (all zeros) leaves the load channel unproven.
            // Rather than silencing traction loss forever, the weighting falls
            // back to the legacy max-abs so the effect still works (issue #30
            // fallback) -- the loud branch the airborne test can't reach.
            var src = NewSource();
            src.ParsePacket(DashPacket(combinedSlip: 0.7f, suspTravel: 0.0f), HorizonDashLength);  // settle; never arms the latch
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(combinedSlip: 0.7f, suspTravel: 0.0f), HorizonDashLength);

            Assert.Equal(0.7, f.WheelSlip.GetValueOrDefault(), 6);   // legacy max-abs, not silenced
        }

        [Fact]
        public void DrivingFrame_ExtractsFrontSlipAngleAndSuspTravel()
        {
            // Past the settle window, the front-axle slip angle (Forza offset
            // 164) and suspension travel in meters (offset 196) are extracted as
            // the front-pair average. These are the SAT-model inputs the parser
            // previously skipped entirely.
            var src = NewSource();
            src.ParsePacket(DashPacket(), HorizonDashLength);   // open + spend settle window
            Thread.Sleep(450);
            var f = src.ParsePacket(DashPacket(slipAngleRad: 0.12f, suspTravelM: 0.05f), HorizonDashLength);

            Assert.True(f.FrontSlipAngleRad.HasValue);
            Assert.Equal(0.12, f.FrontSlipAngleRad.Value, 4);
            Assert.True(f.FrontSuspTravelMeters.HasValue);
            Assert.Equal(0.05, f.FrontSuspTravelMeters.Value, 4);
        }

        [Fact]
        public void FrontSlipAngle_IsSignedFrontPairAverage()
        {
            // Slip angle is signed (slip direction) and averaged across the two
            // front tires, so opposite-sign fronts partially cancel and a
            // negative pair yields a negative average (used as the SAT sign).
            var src = NewSource();
            src.ParsePacket(DashPacket(), HorizonDashLength);
            Thread.Sleep(450);

            var b = DashPacket();
            PutFloat(b, OFF_SLIP_ANGLE_FL + 0, -0.20f);  // FL
            PutFloat(b, OFF_SLIP_ANGLE_FL + 4, -0.10f);  // FR
            var f = src.ParsePacket(b, HorizonDashLength);

            Assert.Equal(-0.15, f.FrontSlipAngleRad.Value, 4);   // (-0.20 + -0.10)/2
        }

        [Fact]
        public void SpawnFrame_SuppressesSatChannels()
        {
            // During the spawn settle window the SAT inputs are zeroed like the
            // other grip channels, so a placement transient can't drive a jolt
            // through a future SAT model.
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(slipAngleRad: 0.3f, suspTravelM: 0.2f), HorizonDashLength);

            Assert.Equal(0.0, f.FrontSlipAngleRad.GetValueOrDefault(), 6);
            Assert.Equal(0.0, f.FrontSuspTravelMeters.GetValueOrDefault(), 6);
        }

        [Fact]
        public void EmptyKeepalivePacket_ZeroesVolatileChannels()
        {
            // FH6 interleaves all-zero "keepalive" packets (EngineMaxRpm == 0)
            // between real frames. The silence gate keys on maxRpm == 0, not on
            // IsRaceOn, so a genuinely-empty packet zeroes the volatile channels
            // even though it still carries a stray current-rpm/speed value.
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(raceOn: 0, maxRpm: 0f, rpm: 5000f, speedMs: 30f), HorizonDashLength);

            Assert.Equal(0.0, f.Rpms, 6);
            Assert.Equal(0.0, f.SpeedKmh, 6);
            Assert.Equal("N", f.Gear);
            // Empty frame leaves the SAT inputs null (not provided), so a model
            // can tell "paused" from "zero force".
            Assert.Null(f.FrontSlipAngleRad);
            Assert.Null(f.FrontSuspTravelMeters);
        }

        [Fact]
        public void Keepalive_DoesNotStampRaceOn()
        {
            // An all-zero keepalive's IsRaceOn byte is zeroed payload, not
            // state. Stamping it flapped LastIsRaceOn several times a second
            // mid-race, which notched every consumer keyed on IsSessionActive
            // (the FFB provider's pause-release above all).
            var src = NewSource();
            src.ParsePacket(DashPacket(raceOn: 1), HorizonDashLength);
            Assert.True(src.LastIsRaceOn);

            src.ParsePacket(DashPacket(raceOn: 0, maxRpm: 0f, rpm: 0f, speedMs: 0f), HorizonDashLength);
            Assert.True(src.LastIsRaceOn);   // unchanged by the empty
        }

        [Fact]
        public void KeepaliveBetweenRealFrames_DoesNotReopenSettleWindow()
        {
            // A keepalive used to record a raceOn=0 level, so the next real
            // frame looked like a 0->1 edge and re-opened the 400 ms settle
            // window: with keepalives interleaving several times a second in
            // FH6 races, the grip / impact channels never escaped suppression.
            var src = NewSource();
            src.ParsePacket(DashPacket(), HorizonDashLength);   // open + spend settle window
            Thread.Sleep(450);
            src.ParsePacket(DashPacket(raceOn: 0, maxRpm: 0f, rpm: 0f, speedMs: 0f), HorizonDashLength);
            var f = src.ParsePacket(DashPacket(heave: 50f, combinedSlip: 0.9f), HorizonDashLength);

            Assert.True(Math.Abs(f.AccelerationHeave.GetValueOrDefault()) > 0.0);
            Assert.True(f.WheelSlip.GetValueOrDefault() > 0.0);
        }

        [Fact]
        public void ShouldEmit_SwallowsLoneKeepalives_PassesPersistentEmptiness()
        {
            // Lone keepalives adjacent to fresh real frames must not reach
            // consumers (their zeroed speed / rpm / gear wipe scalar caches
            // mid-race); only persistent emptiness (a real pause / menu) may
            // pass the silencing zero frame through.
            var src = NewSource();
            long hz = System.Diagnostics.Stopwatch.Frequency;
            var real  = new TelemetryFrame { MaxRpm = 8000 };
            var empty = new TelemetryFrame { MaxRpm = 0 };

            Assert.True(src.ShouldEmit(real, 10 * hz));
            Assert.False(src.ShouldEmit(empty, 10 * hz + hz / 100));   // 10 ms later: lone
            Assert.True(src.ShouldEmit(real, 10 * hz + hz / 50));
            Assert.False(src.ShouldEmit(empty, 10 * hz + hz / 25));    // 20 ms since real: lone
            Assert.True(src.ShouldEmit(empty, 11 * hz));               // ~1 s since real: pause
        }

        [Fact]
        public void ShouldEmit_KeepaliveBeforeAnyRealFrame_FlowsThrough()
        {
            // Fresh source straight into a menu: no real frame has been seen,
            // so the silencing zero frame must not be withheld.
            var src = NewSource();
            Assert.True(src.ShouldEmit(new TelemetryFrame { MaxRpm = 0 }, 123456));
        }

        [Fact]
        public void PersistentKeepalives_DropRaceOn_AfterSilenceWindow()
        {
            // One zeroed payload is noise, but persistent emptiness is a real
            // pause: the session flag must drop when the silencing frames
            // start flowing, or the FFB provider's pause-release (issue #13
            // full-lock protection) never engages on Horizon pauses.
            var src = NewSource();
            long hz = System.Diagnostics.Stopwatch.Frequency;
            src.ParsePacket(DashPacket(raceOn: 1), HorizonDashLength);
            Assert.True(src.LastIsRaceOn);

            var real  = new TelemetryFrame { MaxRpm = 8000 };
            var empty = new TelemetryFrame { MaxRpm = 0 };
            Assert.True(src.ShouldEmit(real, 10 * hz));
            Assert.False(src.ShouldEmit(empty, 10 * hz + hz / 100));
            Assert.True(src.LastIsRaceOn);                 // lone empty: still driving
            Assert.True(src.ShouldEmit(empty, 11 * hz));   // persistent: pause
            Assert.False(src.LastIsRaceOn);
        }

        [Fact]
        public void RaceOnZeroButLive_FlowsThrough()
        {
            // The counterpart to the keepalive gate: FH6 free-roam and replays
            // report IsRaceOn == 0 while still sending real physics. Gating on
            // maxRpm (not IsRaceOn) means these frames must flow through intact,
            // otherwise the wheel goes dead in free-roam. Regression guard for
            // the exact bug the maxRpm gate was introduced to fix.
            var src = NewSource();
            var f = src.ParsePacket(DashPacket(raceOn: 0, maxRpm: 8000f, rpm: 5000f, speedMs: 30f), HorizonDashLength);

            Assert.Equal(5000.0, f.Rpms, 3);
            Assert.Equal(30.0 * 3.6, f.SpeedKmh, 3);
        }

        [Fact]
        public void CarOrdinalZero_IsNull()
        {
            var src = NewSource();
            src.ParsePacket(DashPacket(carOrdinal: 0), HorizonDashLength);
            Assert.Null(src.CurrentCarOrdinal);
        }

        // Pick a currently-free UDP port. UDP has no TIME_WAIT, so the port is
        // reusable the instant the probe socket closes.
        private static int FreeUdpPort()
        {
            using (var probe = new UdpClient(0))
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        [Fact]
        public void Start_BindsToAnyAddress_ReceivesLoopbackPacket()
        {
            // Proves the 0.0.0.0 (IPAddress.Any) bind actually receives traffic
            // sent to 127.0.0.1 through OUR socket loop, not just in theory. This
            // is the default BindAddress and the config we tell users to keep, so
            // it gets real end-to-end coverage (bind -> receive -> ParsePacket ->
            // EmitFrame), guarding against a regression that would silently kill
            // telemetry for every default-configured Forza user.
            int port = FreeUdpPort();
            var src = new ForzaUdpTelemetrySource(port, IPAddress.Any);
            var got = new ManualResetEventSlim(false);
            TelemetryFrame frame = default;   // TelemetryFrame is a struct; got.IsSet signals receipt
            src.OnFrame = f => { frame = f; got.Set(); };

            try
            {
                src.Start();
                Assert.True(src.IsRunning);

                using (var sender = new UdpClient())
                {
                    var dest = new IPEndPoint(IPAddress.Loopback, port);
                    var packet = DashPacket(rpm: 4200f, maxRpm: 8000f);
                    // Resend a few times: the first datagram can land in the split
                    // second between bind and the receive loop entering ReceiveFrom.
                    // The OS buffers it either way, but resending makes the test
                    // robust on a slow CI box without a long single wait.
                    for (int i = 0; i < 10 && !got.IsSet; i++)
                    {
                        sender.Send(packet, packet.Length, dest);
                        got.Wait(200);
                    }
                }

                Assert.True(got.IsSet, "no frame emitted from a loopback packet on an Any-bound source");
                Assert.Equal(4200.0, frame.Rpms, 3);     // engine RPM flows even during the spawn settle window
                Assert.True(src.PacketsReceived >= 1);
            }
            finally
            {
                src.Stop();
            }
        }

        [Fact]
        public void SledOnlyPacket_HasNoDashFields()
        {
            // 232-byte Sled-only packet (FM7 shape): no speed/steer/gear in the
            // dash region, so SteeringAngle stays null and speed stays 0.
            var src = NewSource();
            var sled = new byte[232];
            PutInt32(sled, OFF_IS_RACE_ON, 1);
            PutFloat(sled, OFF_CURRENT_RPM, 5000f);

            var f = src.ParsePacket(sled, sled.Length);

            Assert.Null(f.SteeringAngle);
            Assert.Equal(0.0, f.SpeedKmh, 6);
        }
    }
}
