using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // AC per-wheel derivations (phase D): the signed slip ratio built from
    // wheel rotation vs car speed, and the per-wheel load guard that zeroes
    // frozen/free-spinning entries on unloaded wheels.
    public class AcQuadDerivationTests
    {
        private const float R = AcSharedMemoryTelemetrySource.RollingRadiusM;

        [Fact]
        public void SlipRatio_LockedWheel_ReadsMinusOne_RegardlessOfRadiusError()
        {
            // omega = 0 at speed: ratio = (0 - v)/v = -1 exactly; the fixed
            // rolling radius cannot perturb it.
            var rot = TireQuad.Of(0, 0, 0, 0);
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 100.0, reverseGear: false);
            Assert.Equal(-1.0, q.FL, 3);
            Assert.Equal(-1.0, q.RR, 3);
        }

        [Fact]
        public void SlipRatio_RollingWheel_ReadsZero_AndWheelspinPositive()
        {
            double v = 100.0 / 3.6;                       // m/s
            double omegaRolling = v / R;
            var rot = TireQuad.Of(omegaRolling, omegaRolling,
                                  2 * omegaRolling, 2 * omegaRolling);   // rears spinning 2x
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 100.0, reverseGear: false);
            Assert.Equal(0.0, q.FL, 3);
            Assert.Equal(1.0, q.RL, 3);                   // (2v - v)/v = +1
        }

        [Fact]
        public void SlipRatio_SilentAtStandstill_AndCrawl()
        {
            // A parked car has omega ~0 on every LOADED wheel, which a naive
            // derivation reads as full lockup on all four; the consumers have
            // no speed gate, so the source must return silence instead. This
            // was a live bug: full-severity lockup judder at every stop.
            var rot = TireQuad.Of(0, 0, 0, 0);
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 0.0, reverseGear: false);
            Assert.Equal(0.0, q.FL, 6);
            Assert.Equal(0.0, q.RR, 6);

            var crawl = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 9.0, reverseGear: false);
            Assert.Equal(0.0, crawl.FL, 6);               // 9 km/h = 2.5 m/s < the 3 m/s gate
        }

        [Fact]
        public void SlipRatio_SilentInReverse()
        {
            // Reverse: signed wheel rotation runs opposite the unsigned
            // speed, pinning a naive ratio at the clamp (full-lockup buzz
            // while backing out of the pits). Reverse returns silence.
            double v = 15.0 / 3.6;
            var rot = TireQuad.Of(-v / R, -v / R, -v / R, -v / R);
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 15.0, reverseGear: true);
            Assert.Equal(0.0, q.FL, 6);
        }

        [Fact]
        public void SlipRatio_IsClamped_AgainstGarbage()
        {
            var qNeg = AcSharedMemoryTelemetrySource.DeriveSlipRatio(
                TireQuad.Of(-500, 0, 0, 0), 50.0, reverseGear: false);
            Assert.Equal(-2.0, qNeg.FL, 3);

            var qPos = AcSharedMemoryTelemetrySource.DeriveSlipRatio(
                TireQuad.Of(500, 0, 0, 0), 50.0, reverseGear: false);
            Assert.Equal(2.0, qPos.FL, 3);
        }

        [Fact]
        public void GuardByLoad_ZeroesOnlyUnloadedWheels_WhenArmed()
        {
            var q    = TireQuad.Of(1.2, 1.3, 1.4, 1.5);
            var load = TireQuad.Of(3000, 0.5, 2800, 0.0);   // FR + RR in the air
            var g = AcSharedMemoryTelemetrySource.GuardByLoad(q, load, armed: true);
            Assert.Equal(1.2, g.FL, 6);
            Assert.Equal(0.0, g.FR, 6);
            Assert.Equal(1.4, g.RL, 6);
            Assert.Equal(0.0, g.RR, 6);
        }

        [Fact]
        public void GuardByLoad_PassesThrough_WhenUnarmed()
        {
            // Builds that leave wheelLoad at 0 must not zero everything.
            var q = TireQuad.Of(1.2, 1.3, 1.4, 1.5);
            var g = AcSharedMemoryTelemetrySource.GuardByLoad(
                q, TireQuad.Of(0, 0, 0, 0), armed: false);
            Assert.Equal(1.3, g.FR, 6);
        }

        // ---- Scalar #30 load-weighting (frozen-entry guard + fallback) ----

        [Fact]
        public void ZeroFrozenLoads_FirstFrame_PassesThrough()
        {
            // No previous frame to compare against: never zero anything, or the
            // first frame of a session would drop wheels on a stale comparison.
            var slip = TireQuad.Of(0.5, 0.5, 0.5, 0.5);
            var load = TireQuad.Of(3000, 3000, 3000, 3000);
            var e = AcSharedMemoryTelemetrySource.ZeroFrozenLoads(
                slip, prevSlip: default, prevValid: false, speedKmh: 100, loadN: load);
            Assert.Equal(3000, e.FL, 6);
            Assert.Equal(3000, e.RR, 6);
        }

        [Fact]
        public void ZeroFrozenLoads_BelowSpeedGate_PassesThrough()
        {
            // At a crawl a grounded wheel's slip legitimately repeats, so the
            // guard is disarmed below FrozenGuardSpeedKmh even for identical slip.
            var slip = TireQuad.Of(0.5, 0.5, 0.5, 0.5);
            var load = TireQuad.Of(3000, 3000, 3000, 3000);
            var e = AcSharedMemoryTelemetrySource.ZeroFrozenLoads(
                slip, prevSlip: slip, prevValid: true, speedKmh: 5.0, loadN: load);
            Assert.Equal(3000, e.FL, 6);
            Assert.Equal(3000, e.RR, 6);
        }

        [Fact]
        public void ZeroFrozenLoads_ZeroesOnlyBitIdenticalWheels_AtSpeed()
        {
            // Above the gate: a wheel whose slip is bit-identical to the previous
            // physics frame has left the ground (AC stopped solving it) and its
            // load is zeroed; a wheel whose slip moved at all keeps its load.
            var prev = TireQuad.Of(0.50, 0.50, 0.50, 0.50);
            var slip = TireQuad.Of(0.50, 0.70, 0.50, 0.80);   // FL + RL frozen
            var load = TireQuad.Of(3000, 3000, 3000, 3000);
            var e = AcSharedMemoryTelemetrySource.ZeroFrozenLoads(
                slip, prev, prevValid: true, speedKmh: 60.0, loadN: load);
            Assert.Equal(0.0,  e.FL, 6);
            Assert.Equal(3000, e.FR, 6);
            Assert.Equal(0.0,  e.RL, 6);
            Assert.Equal(3000, e.RR, 6);
        }

        [Fact]
        public void DirectScalarSlip_FourWheelSlide_ReadsFullStrength()
        {
            // Every tyre loaded and sliding equally: the weighting equals that
            // common value, so the effect's 0.50-is-full calibration is kept.
            double r = AcSharedMemoryTelemetrySource.DirectScalarSlip(
                TireQuad.Of(0.50, 0.50, 0.50, 0.50),
                TireQuad.Of(2500, 3500, 2000, 4000), seenLoad: true);
            Assert.Equal(0.50, r, 6);
        }

        [Fact]
        public void DirectScalarSlip_UnloadedWheel_DropsOut()
        {
            // A lifted wheel spun up to 0.9 but carrying no load contributes
            // nothing; the reading follows the three planted wheels.
            double r = AcSharedMemoryTelemetrySource.DirectScalarSlip(
                TireQuad.Of(0.05, 0.05, 0.05, 0.90),
                TireQuad.Of(3000, 3000, 3000, 0.0), seenLoad: true);
            Assert.Equal(0.05, r, 6);
        }

        [Fact]
        public void DirectScalarSlip_Airborne_WhenChannelLive_GoesSilent()
        {
            // Every wheel unloaded and the load channel has proven live: this is
            // a genuine jump, so the direct slip reads silent (issue #30).
            double r = AcSharedMemoryTelemetrySource.DirectScalarSlip(
                TireQuad.Of(0.9, 0.9, 0.9, 0.9),
                TireQuad.Of(0.0, 0.0, 0.0, 0.0), seenLoad: true);
            Assert.Equal(0.0, r, 6);
        }

        [Fact]
        public void DirectScalarSlip_DeadLoadChannel_FallsBackToMaxAbs()
        {
            // wheelLoad never populated on this build (seenLoad false): weighting
            // can't work, so fall back to the legacy max-abs rather than going
            // silent. This is the loud-fallback branch the airborne test can't
            // reach.
            double r = AcSharedMemoryTelemetrySource.DirectScalarSlip(
                TireQuad.Of(0.9, 0.8, 0.7, 0.6),
                TireQuad.Of(0.0, 0.0, 0.0, 0.0), seenLoad: false);
            Assert.Equal(0.9, r, 6);   // max-abs of the slip quad
        }

        // ---- Airborne detection via frozen slip (#31) ----

        [Fact]
        public void AllSlipFrozen_FirstFrame_NotAirborne()
        {
            // No previous frame to compare: the detector must not fire on the
            // first frame of a session (nothing to be identical to).
            var slip = TireQuad.Of(0.3, 0.3, 0.3, 0.3);
            Assert.False(AcSharedMemoryTelemetrySource.AllSlipFrozen(
                slip, prevSlip: default, prevValid: false, speedKmh: 120.0));
        }

        [Fact]
        public void AllSlipFrozen_BelowSpeedGate_NotAirborne()
        {
            // A parked car's slip is frozen on every wheel too; the speed gate
            // keeps that from reading as a jump.
            var slip = TireQuad.Of(0.2, 0.2, 0.2, 0.2);
            Assert.False(AcSharedMemoryTelemetrySource.AllSlipFrozen(
                slip, prevSlip: slip, prevValid: true, speedKmh: 5.0));
        }

        [Fact]
        public void AllSlipFrozen_AllFourFrozenAtSpeed_Airborne()
        {
            // Above the gate with every entry bit-identical to the previous
            // physics frame: AC has stopped solving all four contact patches,
            // the car is off the ground.
            var prev = TireQuad.Of(0.41, 0.52, 0.63, 0.74);
            var slip = TireQuad.Of(0.41, 0.52, 0.63, 0.74);
            Assert.True(AcSharedMemoryTelemetrySource.AllSlipFrozen(
                slip, prev, prevValid: true, speedKmh: 90.0));
        }

        [Fact]
        public void AllSlipFrozen_OneWheelStillSolving_NotAirborne()
        {
            // A wheelie / one-corner lift freezes some entries but not all; only
            // a full four-way freeze is airborne (a single moving entry means a
            // wheel is still on the ground being solved).
            var prev = TireQuad.Of(0.41, 0.52, 0.63, 0.74);
            var slip = TireQuad.Of(0.41, 0.52, 0.63, 0.75);   // RR still moving
            Assert.False(AcSharedMemoryTelemetrySource.AllSlipFrozen(
                slip, prev, prevValid: true, speedKmh: 90.0));
        }

        [Fact]
        public void AcShapedFrame_LightsTheCtmRollups()
        {
            // An AC-style frame (combined quads + suspension travel, no
            // rumble, no slip angle) must produce the axle rollups AxleSlip
            // and the breakaway events feed on.
            var frame = new TelemetryFrame
            {
                SpeedKmh = 120,
                HasTireQuads = true,
                TireCombinedSlip = TireQuad.Of(0.9, 1.1, 0.5, 0.6),
                SuspTravelM      = TireQuad.Of(0.04, 0.06, 0.05, 0.05),
            };
            CtmComposer.Compose(ref frame);
            Assert.True(frame.FrontGrip01.HasValue && frame.FrontGrip01.Value > 0.8);
            Assert.True(frame.RearGrip01.HasValue);
            Assert.True(frame.GripBalance.HasValue && frame.GripBalance.Value < 0);
        }
    }
}
