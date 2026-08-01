using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The braking-grip learner finds the front slip ratio at the braking grip
    // KNEE (peak deceleration), not the top of the slip distribution, so a
    // locked wheel sitting at slip ~1.0 (past the limit) can't drag it up.
    public class BrakingGripLearnerTests
    {
        private const double Dt = 16.7;   // ~60 Hz

        // One hard-braking event: hold at the knee (peak decel, slip = knee),
        // optionally run a lockup tail (decel drops, slip runs away), then
        // release so the event ends and records.
        private static void RunBrakeEvent(BrakingGripLearner l, double peakDecel, double slipAtKnee,
                                          int holdTicks = 90, bool withLockupTail = true)
        {
            for (int i = 0; i < holdTicks; i++)
                l.Tick(slipAtKnee, peakDecel, speedKmh: 100, lateralMs2: 0, dtMs: Dt);
            if (withLockupTail)
                for (int i = 0; i < 20; i++)
                    l.Tick(1.5, peakDecel * 0.4, speedKmh: 100, lateralMs2: 0, dtMs: Dt);
            for (int i = 0; i < 15; i++)   // release: smoothed decel decays below the floor, event ends
                l.Tick(0.0, 0.0, speedKmh: 100, lateralMs2: 0, dtMs: Dt);
        }

        // A one-frame crash / kerb deceleration injected mid-braking.
        private static void RunBrakeEventWithImpact(BrakingGripLearner l, double peakDecel, double slipAtKnee)
        {
            for (int i = 0; i < 45; i++) l.Tick(slipAtKnee, peakDecel, 100, 0, Dt);
            l.Tick(1.7, 25.0, 100, 0, Dt);   // 2.6 g impact at high slip
            for (int i = 0; i < 45; i++) l.Tick(slipAtKnee, peakDecel, 100, 0, Dt);
            for (int i = 0; i < 15; i++) l.Tick(0.0, 0.0, 100, 0, Dt);
        }

        [Fact]
        public void Fresh_ReturnsDefaultUntilConfident()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            Assert.Equal(0.8, l.EffectiveLimitSlip, 6);
            Assert.Equal(0.0, l.Confidence, 6);
        }

        [Fact]
        public void LearnsSlipAtPeakDecel_NotLockupValue()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 12; e++)
                RunBrakeEvent(l, peakDecel: 12.0, slipAtKnee: 1.1);
            Assert.True(l.Confidence >= 0.99, $"confidence {l.Confidence}");
            Assert.InRange(l.LimitSlip, 1.05, 1.15);            // the knee, not 1.5
            Assert.InRange(l.EffectiveLimitSlip, 1.05, 1.15);
        }

        [Fact]
        public void LockupTail_DoesNotDragEstimateUp()
        {
            var lWith = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            var lNo   = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 12; e++)
            {
                RunBrakeEvent(lWith, 12.0, 0.9, withLockupTail: true);
                RunBrakeEvent(lNo,   12.0, 0.9, withLockupTail: false);
            }
            Assert.InRange(lWith.LimitSlip, 0.85, 0.95);
            Assert.Equal(lNo.LimitSlip, lWith.LimitSlip, 6);   // the tail changed nothing
        }

        [Fact]
        public void Cornering_TeachesNothing()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int i = 0; i < 300; i++)
                l.Tick(0.9, 12.0, speedKmh: 100, lateralMs2: 8.0, dtMs: Dt);   // high lateral g
            l.Tick(0.9, 0.0, 100, 8.0, Dt);
            Assert.Equal(0.0, l.Confidence, 6);
            Assert.Equal(0.8, l.EffectiveLimitSlip, 6);
        }

        [Fact]
        public void LightBrakingAndLowSpeed_TeachNothing()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int i = 0; i < 300; i++) l.Tick(0.5, 1.0, 100, 0, Dt);   // decel below MinDecel
            for (int i = 0; i < 300; i++) l.Tick(0.9, 12.0, 20, 0, Dt);   // speed below MinSpeed
            Assert.Equal(0.0, l.Confidence, 6);
        }

        [Fact]
        public void ImpactSpike_DoesNotPolluteTheEstimateOrPeakDecel()
        {
            // A one-frame 2.6 g impact (above the physical braking ceiling) must
            // change nothing: it is rejected, so the learned knee and peakDecel
            // match a clean run.
            var clean = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            var spiked = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 12; e++)
            {
                RunBrakeEvent(clean, 12.0, 0.9, withLockupTail: false);
                RunBrakeEventWithImpact(spiked, 12.0, 0.9);
            }
            Assert.Equal(clean.LimitSlip, spiked.LimitSlip, 6);
            Assert.True(spiked.PeakDecel <= 20.0 + 1e-9, $"peakDecel {spiked.PeakDecel} above the cap");
        }

        [Fact]
        public void ModerateBraking_DoesNotBiasTheLimitDown()
        {
            // Ordinary sub-limit stops (peak ~5 m/s^2, below the grip knee) must
            // teach NOTHING; otherwise they drag the limit toward gentle-braking
            // slip and limp the wheel under normal braking (the review's finding).
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 20; e++)
                RunBrakeEvent(l, peakDecel: 5.0, slipAtKnee: 0.2, withLockupTail: false);
            Assert.Equal(0.0, l.Confidence, 6);
            Assert.Equal(0.8, l.EffectiveLimitSlip, 6);
        }

        [Fact]
        public void MixedBraking_LearnsOnlyFromHardStops()
        {
            // Hard threshold stops (knee 1.1) interleaved with gentle stops (0.2):
            // the estimate must converge to the hard-stop knee, not a blend
            // dragged down by the gentle ones.
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 12; e++)
            {
                RunBrakeEvent(l, peakDecel: 12.0, slipAtKnee: 1.1);
                RunBrakeEvent(l, peakDecel: 5.0, slipAtKnee: 0.2, withLockupTail: false);
            }
            Assert.InRange(l.LimitSlip, 1.05, 1.15);
        }

        [Fact]
        public void FadesFromDefaultTowardLearned()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.5f, FullConfidenceSec = 12.0 };
            for (int e = 0; e < 3; e++) RunBrakeEvent(l, 12.0, 1.2, holdTicks: 30);
            Assert.True(l.Confidence > 0 && l.Confidence < 1, $"confidence {l.Confidence}");
            Assert.InRange(l.EffectiveLimitSlip, 0.5, 1.2);
        }

        [Fact]
        public void Reset_FadesFromNewDefault()
        {
            var l = new BrakingGripLearner { DefaultLimitSlip = 0.8f };
            for (int e = 0; e < 12; e++) RunBrakeEvent(l, 12.0, 1.3);
            l.DefaultLimitSlip = 1.0f;
            l.Reset();
            Assert.Equal(0.0, l.Confidence, 6);
            Assert.Equal(1.0, l.EffectiveLimitSlip, 6);
        }
    }
}
