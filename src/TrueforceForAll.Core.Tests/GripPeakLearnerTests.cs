// Phase 5 / layer 11: per-car grip-limit auto-calibration. The contract
// that matters on the wheel: no behavior change until the learner has
// earned trust, convergence within ~a lap of pushing, and a spin must not
// poison the peak.

using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class GripPeakLearnerTests
    {
        private const double DtMs = 16.7;   // ~60 Hz telemetry

        private static void Drive(GripPeakLearner l, double u, double seconds,
                                  double speedKmh = 120.0)
        {
            for (double t = 0.0; t < seconds; t += DtMs / 1000.0)
                l.Tick(u, speedKmh, DtMs);
        }

        [Fact]
        public void FreshLearner_UsesNominalPeak()
        {
            // A car with no learned data normalizes against the nominal ceiling
            // (1.2, the fleet-average real peak), not 1.0, so fresh cars do not
            // over-read utilization and inflate the braking-loop gain (issue #38).
            var l = new GripPeakLearner();
            Assert.Equal(1.2, l.EffectivePeak, 9);
            Assert.Equal(0.0, l.Confidence, 9);
        }

        [Fact]
        public void HotMetricCar_ConvergesNearItsRealCeiling()
        {
            // A car whose combined-slip metric sustains ~1.4 at the limit.
            var l = new GripPeakLearner();
            Drive(l, 1.4, 90.0);
            Assert.InRange(l.Peak, 1.30, 1.45);
            Assert.Equal(1.0, l.Confidence, 6);
            Assert.InRange(l.EffectivePeak, 1.30, 1.45);
        }

        [Fact]
        public void ColdMetricCar_LearnsTheLowerCeiling()
        {
            // A car that tops out at ~0.8: without calibration it never
            // reaches the drop cue. Peak must fall toward the real ceiling.
            var l = new GripPeakLearner();
            Drive(l, 0.8, 180.0);
            Assert.InRange(l.Peak, 0.75, 0.90);
        }

        [Fact]
        public void SpinSpike_BarelyMovesThePeak()
        {
            // Learner settled at ~1.0, then a 2-second spin reads u = 3.0.
            var l = new GripPeakLearner();
            Drive(l, 1.0, 90.0);
            double before = l.Peak;
            Drive(l, 3.0, 2.0);
            Assert.True(l.Peak < before * 1.10,
                $"spin moved peak {before:0.000} -> {l.Peak:0.000} (>10%)");
        }

        [Fact]
        public void ParkedAndSlow_TeachNothing()
        {
            var l = new GripPeakLearner();
            Drive(l, 1.6, 300.0, speedKmh: 5.0);
            Assert.Equal(1.2, l.Peak, 9);   // untouched: sits at the nominal
            Assert.Equal(0.0, l.Confidence, 9);
        }

        [Fact]
        public void Straights_NeitherRaiseNorErode()
        {
            var l = new GripPeakLearner();
            Drive(l, 1.3, 90.0);
            double settled = l.Peak;
            Drive(l, 0.05, 600.0);   // ten minutes of straight-line cruise
            // The pre-filter's decay tail crosses the cornering band once on
            // corner exit — a sub-1% one-time cost, not ongoing erosion.
            Assert.InRange(l.Peak, settled * 0.99, settled * 1.001);
        }

        [Fact]
        public void ConfidenceFadesTheCorrectionIn()
        {
            // Partial seat time = partial trust: EffectivePeak sits between
            // the nominal (1.2) and the learned peak, monotonically toward it.
            var l = new GripPeakLearner();
            Drive(l, 1.4, 20.0);
            Assert.True(l.Confidence > 0.0 && l.Confidence < 1.0);
            Assert.True(l.EffectivePeak > l.NominalPeak && l.EffectivePeak < l.Peak,
                $"expected {l.NominalPeak} < {l.EffectivePeak:0.000} < {l.Peak:0.000}");
        }

        [Fact]
        public void RestoreRoundTrip_PreservesStateAndRejectsGarbage()
        {
            var l = new GripPeakLearner();
            Drive(l, 1.4, 90.0);
            double peak = l.Peak, sec = l.QualifyingSec;

            var back = new GripPeakLearner();
            back.Restore(peak, sec);
            Assert.Equal(peak, back.Peak, 9);
            Assert.Equal(sec, back.QualifyingSec, 9);
            Assert.Equal(l.EffectivePeak, back.EffectivePeak, 9);

            var garbage = new GripPeakLearner();
            garbage.Restore(double.NaN, -5.0);
            Assert.Equal(1.2, garbage.Peak, 9);   // garbage -> nominal fallback
            Assert.Equal(0.0, garbage.QualifyingSec, 9);
        }

        [Fact]
        public void EffectivePeak_StaysInsideSanityRange()
        {
            var l = new GripPeakLearner();
            Drive(l, 50.0, 600.0);   // absurd metric forever
            Assert.InRange(l.EffectivePeak, l.PeakMin, l.PeakMax);
        }

        [Fact]
        public void ConfiguringNominalPeak_MovesTheFreshStartingEstimate()
        {
            // A second instance pointed at another domain (auto strength watches
            // intrinsic FORCE, nominal 0.90, not grip) must start there. Keeping
            // the grip domain's 1.2 made the first minute on every car read a
            // ceiling 33 percent too high, and only the 45 s fall constant
            // brought it back, so the applied scale was biased light throughout.
            var force = new GripPeakLearner
            {
                NominalPeak    = 0.90,
                CorneringFloor = 0.15,
                PeakMin        = 0.30,
                PeakMax        = 1.50,
            };
            Assert.Equal(0.90, force.Peak, 9);
            Assert.Equal(0.90, force.EffectivePeak, 9);

            // The grip learner's own default is untouched.
            Assert.Equal(1.2, new GripPeakLearner().Peak, 9);
        }

        [Fact]
        public void ConfiguringNominalPeak_DoesNotClobberLearnedState()
        {
            // Guard on the fix above: the seeding only applies while nothing has
            // been learned, so a restored car keeps its saved peak.
            var l = new GripPeakLearner { NominalPeak = 0.90, PeakMin = 0.30, PeakMax = 1.50 };
            l.Restore(1.35, 60.0);
            l.NominalPeak = 0.80;
            Assert.Equal(1.35, l.Peak, 9);
        }
    }
}
