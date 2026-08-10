// Test layers 8 and 9 from docs/steering-feel-physics.md: early torque peak
// (SatForceModel.PlateauStartU) and road kick (RoadKickModel). Layer 10 was
// GAP #2, the countersteer/slide-feel term; every attempt at it was retired
// 2026-08-02 along with the rear-axle gate they shared, so nothing of it
// remains to test. See the ModeBComposer header for the post-mortem.

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
            Assert.True(peak > 0.25, $"curb pulse should survive, got {peak}");
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

    }
}
