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
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 100.0);
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
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 100.0);
            Assert.Equal(0.0, q.FL, 3);
            Assert.Equal(1.0, q.RL, 3);                   // (2v - v)/v = +1
        }

        [Fact]
        public void SlipRatio_IsClamped_AndBoundedAtCrawl()
        {
            // Free-spinning wheel on a stationary car: the 2 m/s floor plus
            // the clamp keep the ratio finite and within +-2.
            var rot = TireQuad.Of(500, 500, 500, 500);
            var q = AcSharedMemoryTelemetrySource.DeriveSlipRatio(rot, 0.0);
            Assert.Equal(2.0, q.FL, 3);

            var qNeg = AcSharedMemoryTelemetrySource.DeriveSlipRatio(
                TireQuad.Of(-500, 0, 0, 0), 50.0);
            Assert.Equal(-2.0, qNeg.FL, 3);
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
