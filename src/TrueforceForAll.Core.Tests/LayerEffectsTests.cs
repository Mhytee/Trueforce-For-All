using System;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Test-layer effects: AxleSlip voices, kerb thump, lockup judder.
    public class LayerEffectsTests
    {
        private static TelemetryFrame Rollups(double front, double rear, double? balance = null) =>
            new TelemetryFrame
            {
                FrontGrip01 = front,
                RearGrip01  = rear,
                GripBalance = balance ?? (rear - front),
                SpeedKmh    = 100,
            };

        private static float RenderPeak(TelemetryEffect fx, int batches = 200)
        {
            var buf = new float[4];
            float peak = 0;
            for (int i = 0; i < batches; i++)
            {
                Array.Clear(buf, 0, buf.Length);
                fx.RenderAdd(buf, buf.Length);
                foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v));
            }
            return peak;
        }

        // ---------------- AxleSlip ----------------

        [Fact]
        public void AxleSlip_SilentBelowOnset_AndWithoutRollups()
        {
            var fx = new AxleSlipEffect();
            fx.OnTelemetry(Rollups(0.5, 0.5));
            Assert.False(fx.IsActive);
            Assert.Equal(0f, RenderPeak(fx), 3);

            fx.OnTelemetry(new TelemetryFrame());   // null rollups
            Assert.False(fx.IsActive);
        }

        [Fact]
        public void AxleSlip_FrontVoice_FiresPastOnset()
        {
            var fx = new AxleSlipEffect();
            fx.OnTelemetry(Rollups(1.2, 0.5));
            Assert.True(fx.IsActive);
            Assert.True(RenderPeak(fx) > 0.05f);
        }

        [Fact]
        public void AxleSlip_Salience_DucksTheOtherAxle()
        {
            // Same front utilization; deep oversteer (rear >> front) must
            // shrink the FRONT voice vs the balanced case. RearAmp = 0
            // isolates the front voice in the rendered output.
            var balanced = new AxleSlipEffect { RearAmp = 0f };
            balanced.OnTelemetry(Rollups(1.2, 1.2, balance: 0));

            var oversteer = new AxleSlipEffect { RearAmp = 0f };
            oversteer.OnTelemetry(Rollups(1.2, 2.0, balance: 0.8));

            float fBal  = RenderPeak(balanced, 800);
            float fOver = RenderPeak(oversteer, 800);
            Assert.True(fOver < fBal * 0.5f,
                $"front not ducked under oversteer: balanced {fBal}, oversteer {fOver}");
        }

        [Fact]
        public void AxleSlip_PredictiveLead_FiresEarlyOnFastRise_OnlyWhenEnabled()
        {
            // Utilization ramping hard toward (but still below) the onset.
            var off = new AxleSlipEffect();                          // lead off
            var on  = new AxleSlipEffect { PredictiveLeadMs = 80f };

            for (int i = 0; i <= 20; i++)
            {
                double u = 0.40 + i * 0.02;                          // +10/s rise, ends 0.80
                off.OnTelemetry(Rollups(u, 0.3));
                on.OnTelemetry(Rollups(u, 0.3));
            }
            Assert.False(off.IsActive, "no-lead effect fired below onset");
            Assert.True(on.IsActive, "predictive lead failed to pre-fire on a fast rise");
        }

        [Fact]
        public void AxleSlip_RevLockedRearPulse_TracksWheelRevRate()
        {
            var fx = new AxleSlipEffect { RevLockedRearPulse = true };
            var f = Rollups(0.3, 1.4);
            f.HasTireQuads = true;
            f.WheelRotRadS = TireQuad.Of(0, 0, 314.159, 314.159);   // 50 rev/s
            fx.OnTelemetry(f);
            Assert.True(fx.IsActive);
            // 50 Hz is inside the clamp [RearFreqMinHz, 90]; verify indirectly:
            // the effect renders (frequency plumbing didn't throw / zero out).
            Assert.True(RenderPeak(fx) > 0.05f);
        }

        [Fact]
        public void AxleSlip_LockupGate_MutesLockedAxle_KeepsWheelspin()
        {
            // Same front utilization; locking under braking (signed slip
            // ratio -0.9) must be silent, wheelspin (+0.9) keeps the voice.
            var locked = new AxleSlipEffect { RearAmp = 0f };
            var fL = Rollups(1.3, 0.3);
            fL.HasTireQuads = true;
            fL.TireSlipRatio = TireQuad.Of(-0.9, -0.9, 0, 0);
            locked.OnTelemetry(fL);

            var spinning = new AxleSlipEffect { RearAmp = 0f };
            var fS = Rollups(1.3, 0.3);
            fS.HasTireQuads = true;
            fS.TireSlipRatio = TireQuad.Of(0.9, 0.9, 0, 0);
            spinning.OnTelemetry(fS);

            Assert.False(locked.IsActive, "front voice fired on brake lockup");
            Assert.True(spinning.IsActive, "front voice muted on wheelspin");
        }

        [Fact]
        public void AxleSlip_LockupGate_SilencesRevLockedPulse_OnRearLock()
        {
            // The old violent-braking signature: a locked rear (near-zero
            // wheel revs, slip ratio -1) pinned the rev-locked pulse at its
            // 40 Hz floor at full amplitude. The gate must silence it.
            var fx = new AxleSlipEffect { RevLockedRearPulse = true, FrontAmp = 0f };
            var f = Rollups(0.3, 1.5);
            f.HasTireQuads = true;
            f.WheelRotRadS = TireQuad.Of(0, 0, 2.0, 2.0);
            f.TireSlipRatio = TireQuad.Of(0, 0, -1.0, -1.0);
            fx.OnTelemetry(f);
            Assert.False(fx.IsActive, "rear pulse fired on rear lockup");
        }

        [Fact]
        public void AxleSlip_StallAndReset_GoSilent()
        {
            var fx = new AxleSlipEffect();
            fx.OnTelemetry(Rollups(1.3, 1.3));
            Assert.True(fx.IsActive);
            fx.OnTelemetryStall();
            RenderPeak(fx, 3000);            // let amps decay
            Assert.True((float)fx.ActivityLevel < 0.01f);
            fx.Reset();
            Assert.False(fx.IsActive);
        }

        // ---------------- KerbThump ----------------

        [Fact]
        public void KerbThump_FiresOnStartEvent_NotOnSustain()
        {
            var fx = new KerbThumpEffect();
            var enter = new TelemetryFrame { Events = FrameEvents.RumbleStripStart, SpeedKmh = 100 };
            var stay  = new TelemetryFrame { Events = FrameEvents.None, SpeedKmh = 100 };

            fx.OnTelemetry(stay);
            Assert.False(fx.IsActive);
            fx.OnTelemetry(enter);
            Assert.True(fx.IsActive);
            Assert.True(RenderPeak(fx) > 0.05f);

            // Envelope decays to done; staying on the kerb retriggers nothing.
            RenderPeak(fx, 500);
            fx.OnTelemetry(stay);
            Assert.False(fx.IsActive);
        }

        [Fact]
        public void KerbThump_SpeedGate_IgnoresParkingLotKerbs()
        {
            var fx = new KerbThumpEffect();
            fx.OnTelemetry(new TelemetryFrame { Events = FrameEvents.RumbleStripStart, SpeedKmh = 8 });
            Assert.False(fx.IsActive);
        }

        // ---------------- LockupJudder ----------------

        private static TelemetryFrame Locked(double worstRatio, double kmh, FrameEvents ev) =>
            new TelemetryFrame
            {
                HasTireQuads  = true,
                TireSlipRatio = TireQuad.Of(worstRatio, -0.1, 0, 0),
                SpeedKmh      = kmh,
                Events        = ev,
            };

        [Fact]
        public void LockupJudder_LivesBetweenStartAndEnd()
        {
            var fx = new LockupJudderEffect();
            fx.OnTelemetry(Locked(-0.05, 120, FrameEvents.None));
            Assert.False(fx.IsActive);

            fx.OnTelemetry(Locked(-0.9, 120, FrameEvents.LockupStart));
            Assert.True(fx.IsActive);
            Assert.True(RenderPeak(fx) > 0.05f);

            fx.OnTelemetry(Locked(-0.1, 100, FrameEvents.LockupEnd));
            RenderPeak(fx, 3000);            // decay out
            Assert.True((float)fx.ActivityLevel < 0.01f);
        }

        [Fact]
        public void LockupJudder_SeverityScalesAmplitude()
        {
            var soft = new LockupJudderEffect();
            soft.OnTelemetry(Locked(-0.3, 120, FrameEvents.LockupStart));
            var hard = new LockupJudderEffect();
            hard.OnTelemetry(Locked(-1.0, 120, FrameEvents.LockupStart));
            Assert.True(RenderPeak(hard, 800) > RenderPeak(soft, 800));
        }
    }
}
