using System;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // TractionLoss regressions from the 500 Hz fixed-tick move (phase 1):
    // the neutral / low-speed decay alpha must be rescaled like its EMA
    // siblings, and the RPM-derivative wheelspin heuristic must survive
    // being ticked every 2 ms instead of once per source frame.
    public class TractionLossEffectTests
    {
        private static TelemetryFrame DirectSlip(double slip, string gear = "3", double speedKmh = 100) =>
            new TelemetryFrame
            {
                WheelSlip = slip,
                SpeedKmh  = speedKmh,
                Gear      = gear,
            };

        // Drive the EMA up via the direct-slip path (rawTraction = 1.0), then
        // measure one decay step. 0.4/frame at the AC reference rate rescaled
        // to the 500 Hz tick is 0.4^(333/500) = 0.54; the unrescaled 0.4
        // decays ~5x too fast in wall-clock terms and fails the bound.
        [Fact]
        public void NeutralGearDecay_UsesRescaledAlpha()
        {
            var fx = new TractionLossEffect();
            for (int i = 0; i < 3; i++) fx.OnTelemetry(DirectSlip(5.0));
            double before = fx.ActivityLevel;
            Assert.InRange(before, 0.5, 0.999);

            fx.OnTelemetry(DirectSlip(5.0, gear: "N"));
            Assert.Equal(0.54, fx.ActivityLevel / before, 2);
        }

        [Fact]
        public void LowSpeedDecay_UsesRescaledAlpha()
        {
            var fx = new TractionLossEffect();
            for (int i = 0; i < 3; i++) fx.OnTelemetry(DirectSlip(5.0));
            double before = fx.ActivityLevel;
            Assert.InRange(before, 0.5, 0.999);

            fx.OnTelemetry(DirectSlip(5.0, speedKmh: 1));
            Assert.Equal(0.54, fx.ActivityLevel / before, 2);
        }

        // Heuristic path (no WheelSlip channel): RPM climbing at 3000 RPM/s
        // at constant speed under full throttle is unambiguous wheelspin.
        // 6 RPM per call = 3000 RPM/s at the 500 Hz effect tick the window
        // constants assume. Fully deterministic: the window counts ticks,
        // not wall time. (Before the windowed baseline the detector rebased
        // its history on every 2 ms tick and never saw a usable derivative.)
        [Fact]
        public void Wheelspin_DetectedAtEngineTickRate()
        {
            var fx = new TractionLossEffect();
            var frame = new TelemetryFrame
            {
                SpeedKmh   = 80,
                Gear       = "2",
                Throttle01 = 1.0,
                MaxRpm     = 8000,
                Rpms       = 3000,
            };
            for (int i = 0; i < 500; i++)   // one simulated second
            {
                frame.Rpms += 6.0;
                fx.OnTelemetry(frame);
            }
            Assert.True(fx.ActivityLevel > 0.3,
                $"wheelspin never detected (activity={fx.ActivityLevel:F3})");
        }

        // Same cadence, steady RPM: the windowing must not manufacture a
        // derivative out of rebasing artifacts.
        [Fact]
        public void Wheelspin_SteadyRpm_StaysSilent()
        {
            var fx = new TractionLossEffect();
            var frame = new TelemetryFrame
            {
                SpeedKmh   = 80,
                Gear       = "2",
                Throttle01 = 1.0,
                MaxRpm     = 8000,
                Rpms       = 4000,
            };
            for (int i = 0; i < 500; i++) fx.OnTelemetry(frame);
            Assert.Equal(0.0, fx.ActivityLevel, 3);
        }

        // Free-revving in neutral and then re-engaging must not read the
        // across-the-gap RPM delta as wheelspin: neutral ticks invalidate the
        // derivative baseline, so the first window after re-engagement only
        // measures post-engagement rise.
        [Fact]
        public void Wheelspin_NeutralRevGap_DoesNotFalseTrigger()
        {
            var fx = new TractionLossEffect();
            var frame = new TelemetryFrame
            {
                SpeedKmh   = 80,
                Gear       = "2",
                Throttle01 = 1.0,
                MaxRpm     = 8000,
                Rpms       = 3000,
            };
            for (int i = 0; i < 50; i++) fx.OnTelemetry(frame);   // steady, in gear

            frame.Gear = "N";
            frame.Rpms = 6000;                                    // free-rev in neutral
            for (int i = 0; i < 50; i++) fx.OnTelemetry(frame);

            // Track the PEAK across re-engagement: without invalidation the
            // phantom fires as a ~10-tick transient right after re-engaging
            // and has fully decayed again by the end of the loop, so a
            // final-value assert would miss it.
            frame.Gear = "2";                                     // re-engage, steady at the higher rpm
            double peak = 0;
            for (int i = 0; i < 50; i++)
            {
                fx.OnTelemetry(frame);
                peak = Math.Max(peak, fx.ActivityLevel);
            }
            Assert.True(peak < 0.01, $"phantom wheelspin after neutral rev (peak={peak:F3})");
        }

        // Same shape for the stall path: OnTelemetryStall must also
        // invalidate the derivative baseline, or telemetry resuming at a
        // different RPM reads as wheelspin.
        [Fact]
        public void Wheelspin_StallGap_DoesNotFalseTrigger()
        {
            var fx = new TractionLossEffect();
            var frame = new TelemetryFrame
            {
                SpeedKmh   = 80,
                Gear       = "2",
                Throttle01 = 1.0,
                MaxRpm     = 8000,
                Rpms       = 3000,
            };
            for (int i = 0; i < 50; i++) fx.OnTelemetry(frame);

            fx.OnTelemetryStall();
            frame.Rpms = 6000;                                    // resume at a different rpm
            double peak = 0;
            for (int i = 0; i < 50; i++)
            {
                fx.OnTelemetry(frame);
                peak = Math.Max(peak, fx.ActivityLevel);
            }
            Assert.True(peak < 0.01, $"phantom wheelspin after stall (peak={peak:F3})");
        }
    }
}
