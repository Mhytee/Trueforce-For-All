// Test layers 8-10 (the three gaps from docs/steering-feel-physics.md):
// early torque peak (SatForceModel.PlateauStartU), road kick
// (RoadKickModel), and layer 10's slide gate (ModeBComposer.SlideGate01).
// Layer 10 was originally an additive countersteer term, retired 2026-08-02;
// its gate is what survived. See the section comment below.

using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Layer8To10Tests
    {
        // ---------------- Layer 8: early torque peak ----------------

        [Fact]
        public void EarlyPeak_DefaultIsLegacyShape()
        {
            var legacy = new SatForceModel();
            var val = legacy.Force01(0.9, 0.05, 1.0, 120);
            var expected = new SatForceModel { PlateauStartU = 1.0 }.Force01(0.9, 0.05, 1.0, 120);
            Assert.Equal(expected, val, 12);
        }

        [Fact]
        public void EarlyPeak_TorquePlateausFromPlateauToLimit()
        {
            var m = new SatForceModel { PlateauStartU = 0.75, SatGain = 1.0 };
            double at75 = m.Force01(0.75, 0.05, 1.0, 120);
            double at90 = m.Force01(0.90, 0.05, 1.0, 120);
            double at100 = m.Force01(1.00, 0.05, 1.0, 120);
            // Zero slope across the plateau: full torque held from 0.75 to 1.0.
            Assert.Equal(at75, at90, 12);
            Assert.Equal(at75, at100, 12);
        }

        [Fact]
        public void EarlyPeak_RiseReachesFullTorqueAtPlateau_AndDropStillApplies()
        {
            var m = new SatForceModel { PlateauStartU = 0.75, SatGain = 1.0, DropFloor = 0.4, FullU = 1.6 };
            double below = m.Force01(0.5, 0.05, 1.0, 120);
            double atPlateau = m.Force01(0.75, 0.05, 1.0, 120);
            double pastLimit = m.Force01(1.6, 0.05, 1.0, 120);
            Assert.True(below < atPlateau, "still rising below the plateau");
            // Past FullU the smoothstep drop lands on DropFloor of the peak.
            Assert.Equal(atPlateau * 0.4, pastLimit, 6);
        }

        // ---------------- Layer 9: road kick ----------------

        [Fact]
        public void RoadKick_OneWheelCompressionKicks_SymmetricDoesNot()
        {
            var oneWheel = new RoadKickModel();
            var bothWheels = new RoadKickModel();
            oneWheel.Tick(0.05, 0.05, 16);   // seed
            bothWheels.Tick(0.05, 0.05, 16);

            // FL compresses 3 cm in one 16 ms frame, FR untouched → kick.
            double kick = oneWheel.Tick(0.08, 0.05, 16);
            Assert.True(kick > 0.3, $"one-wheel hit should kick, got {kick}");

            // Both compress identically (crest) → cancels to zero.
            double flat = bothWheels.Tick(0.08, 0.08, 16);
            Assert.Equal(0.0, flat, 9);
        }

        [Fact]
        public void RoadKick_SteadyAsymmetryDecaysToZero()
        {
            var m = new RoadKickModel();
            m.Tick(0.05, 0.05, 16);
            m.Tick(0.09, 0.05, 16);          // the hit
            // Parked on the kerb: delta constant, rate zero → kick decays out.
            double last = 1.0;
            for (int i = 0; i < 60; i++) last = m.Tick(0.09, 0.05, 16);
            Assert.True(Math.Abs(last) < 0.02, $"steady asymmetry must decay, got {last}");
        }

        [Fact]
        public void RoadKick_FrameRateAlternationIsSilenced()
        {
            // Tenth wheel test (trace-confirmed): a hard launch rocks the
            // chassis with driveline torque reaction, so FL−FR flips sign
            // every telemetry frame — a ~50 Hz alternating "kick" that
            // buzzed the rim off the line. Alternation is noise by
            // definition (a real bump is one-sided); it must read as zero.
            var m = new RoadKickModel();
            m.Tick(0.05, 0.05, 16);   // seed
            double worst = 0.0;
            for (int i = 0; i < 120; i++)
            {
                double fl = 0.05 + ((i % 2 == 0) ? 0.004 : -0.004);
                worst = Math.Max(worst, Math.Abs(m.Tick(fl, 0.05, 16)));
            }
            Assert.Equal(0.0, worst, 9);
        }

        [Fact]
        public void RoadKick_KerbPulseSurvivesTheDeadband()
        {
            // A real kerb strike: one wheel compresses one-sided over ~4
            // frames at ~1.25 m/s. Must still land as a distinct kick.
            var m = new RoadKickModel();
            m.Tick(0.05, 0.05, 16);   // seed
            double peak = 0.0;
            for (int i = 1; i <= 4; i++)
                peak = Math.Max(peak, m.Tick(0.05 + 0.02 * i, 0.05, 16));
            Assert.True(peak > 0.25, $"kerb pulse should survive, got {peak}");
        }

        [Fact]
        public void RoadKick_ResetForgetsTravelState()
        {
            var m = new RoadKickModel();
            m.Tick(0.02, 0.02, 16);
            m.Reset();
            // First tick after reset seeds — a huge apparent jump (car swap)
            // must NOT produce a phantom kick.
            Assert.Equal(0.0, m.Tick(0.15, 0.02, 16), 9);
        }

        // -------- Slide gate (GAP #2's replacement) --------
        // Layer 10 originally shaped an additive countersteer term here, first as
        // "slide-depth growth", later as a handoff and a crossfade. All of it was
        // retired 2026-08-02 (see the ModeBComposer header): the trail spring
        // closed the gap by a better route and four on-wheel sessions all said the
        // wheel is smoother the less countersteer it gets. What survives is the
        // GATE those terms shared with the trail spring and SlideDuck, which turned
        // out to be the part that actually mattered.

        [Fact]
        public void SlideExcess_SpeedRamp_KillsCrawlSpeedGarbage_KeepsRealDrifts()
        {
            // The excess is built from COMBINED slip, a ratio against ground speed,
            // so it inflates like ~1/speed with no slide behind it: measured at 17
            // during 4-10 km/h wheelspin and past 20 through 25-49 km/h, against
            // ~4 in real drifts at 54-124 km/h. The force path ramps it in over
            // exactly that contamination band. Mirrors the caller's thresholds.
            double Ramp(double kmh) => ModeBComposer.LowSpeedGate(kmh, 10.0, 50.0);

            Assert.Equal(0.0, Ramp(0), 12);
            Assert.Equal(0.0, Ramp(10), 12);      // crawl-speed wheelspin: silent
            Assert.True(Ramp(30) < 0.6, $"mid-band must still be discounted: {Ramp(30)}");
            Assert.Equal(1.0, Ramp(50), 12);      // real drift speeds: untouched
            Assert.Equal(1.0, Ramp(124), 12);

            // The 17.4 crawl reading collapses below anything a real drift makes,
            // while the drift reading itself is unchanged.
            Assert.True(17.4 * Ramp(7) < 4.3 * Ramp(60),
                "crawl garbage must no longer outrank a genuine slide");
            Assert.Equal(4.3, 4.3 * Ramp(60), 12);
        }

        [Fact]
        public void SlideGate01_SoftSaturates_NeverClips()
        {
            // Half at the half point, by definition.
            Assert.Equal(0.5, ModeBComposer.SlideGate01(ModeBComposer.SlideHalfPoint), 12);
            Assert.Equal(0.0, ModeBComposer.SlideGate01(0.0), 12);
            Assert.Equal(0.0, ModeBComposer.SlideGate01(-5.0), 12);
            Assert.Equal(0.0, ModeBComposer.SlideGate01(double.NaN), 12);

            // The property the hard cap lacked: the measured range spans 0.4 to 62
            // and every point in it stays distinguishable, approaching 1 without
            // ever reaching it. A clamp flattened everything past the cap.
            double p50 = ModeBComposer.SlideGate01(0.39);
            double p90 = ModeBComposer.SlideGate01(7.45);
            double p99 = ModeBComposer.SlideGate01(29.7);
            double max = ModeBComposer.SlideGate01(62.1);
            Assert.True(p50 < p90 && p90 < p99 && p99 < max, "must stay strictly monotone across the measured range");
            Assert.True(max < 1.0, "must never actually reach 1");
            Assert.True(p50 < 0.25, $"normal driving should sit low: {p50}");
            Assert.True(p90 > 0.7, $"a real slide should read high: {p90}");
        }

    }
}
