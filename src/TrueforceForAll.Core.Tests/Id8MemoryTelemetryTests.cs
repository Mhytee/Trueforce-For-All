// The Initial D 8 car table and the tachometer conversion.
//
// The conversion earns its own tests because it has been wrong twice against a real cabinet, and
// both times the only thing that caught it was the wheel firing at the wrong moment. The first
// attempt published the internal rpm, which is about 10 percent above the dial. The second scaled
// by the face's printed maximum, which happened to work on one car and could never fire on another.

using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8CarTableTests
    {
        [Fact]
        public void HasEveryCar()
        {
            Assert.Equal(50, Id8CarTable.Count);
        }

        [Fact]
        public void TruenoMatchesItsTachoFaces()
        {
            Id8Car c = Id8CarTable.Find(0);
            Assert.NotNull(c);
            Assert.Equal("AE86T", c.Code);
            Assert.Equal(7300, c.Redline(false));
            Assert.Equal(8000, c.DialMax(false));
            Assert.Equal(11000, c.Redline(true));
            Assert.Equal(13000, c.DialMax(true));
            Assert.Equal(5, c.GearCount);
            Assert.Equal(4, c.Cylinders);
            Assert.False(c.IsRotary);
        }

        [Fact]
        public void NismoMatchesItsTachoFace()
        {
            Id8Car c = Id8CarTable.Find(263);
            Assert.NotNull(c);
            Assert.Equal("R35", c.Code);
            Assert.Equal(7000, c.Redline(false));
            Assert.Equal(9000, c.DialMax(false));
            Assert.Equal(6, c.GearCount);
            Assert.Equal(6, c.Cylinders);
        }

        [Fact]
        public void RotariesReportNoCylindersButPulseLikeAFour()
        {
            // A 13B twin rotor has no cylinders, but it fires twice per crankshaft revolution,
            // which is the same rate as a four cylinder four stroke. That rate is what an engine
            // pulse effect actually needs, so it must not fall back to a generic guess.
            Id8Car c = Id8CarTable.Find(768);
            Assert.NotNull(c);
            Assert.True(c.IsRotary);
            Assert.Equal(0, c.Cylinders);
            Assert.Equal(4, c.PulseCylinderEquivalent);
        }

        [Fact]
        public void UnknownCarIsNull()
        {
            Assert.Null(Id8CarTable.Find(99999));
        }

        [Fact]
        public void TunedFaceBitIsBitTwenty()
        {
            Assert.Equal(1u << 20, Id8CarTable.TunedTachoFaceBit);
        }
    }

    public class Id8DialConversionTests
    {
        // The limiter pins the normalised rpm here on every car, measured live at 0.813 on a GT-R
        // and 0.814 on a tuned Trueno. The exe builds it as 6500 + 1540 against a 10000 scale.
        private const double LimiterNorm = 0.804;

        [Fact]
        public void NeedleReadsTheRedlineWhenTheLimiterBites()
        {
            // The whole rule this conversion encodes: the limiter cuts at the redline.
            Assert.Equal(7000, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 7000), 0);
            Assert.Equal(11000, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 11000), 0);
            Assert.Equal(7300, Id8MemoryTelemetry.DialRpmFor(LimiterNorm, 7300), 0);
        }

        [Fact]
        public void EveryCarCanActuallyReachItsRedline()
        {
            // The regression that mattered. Scaling by the face maximum meant the tuned Trueno's
            // needle stopped at 10582 against a redline of 11000, so its buzz never fired at all.
            // The limiter overshoots its centre by about 0.01, which is what puts the needle into
            // the red rather than merely up to it.
            const double limiterPeak = 0.814;
            foreach (int carId in new[] { 0, 263, 768 })
            {
                Id8Car c = Id8CarTable.Find(carId);
                foreach (bool tuned in new[] { false, true })
                {
                    int redline = c.Redline(tuned);
                    double atPeak = Id8MemoryTelemetry.DialRpmFor(limiterPeak, redline);
                    Assert.True(atPeak >= redline,
                        $"CarID {carId} {(tuned ? "tuned" : "stock")}: the needle reaches {atPeak:0} " +
                        $"but the red starts at {redline}, so the redline buzz could never fire.");
                }
            }
        }

        [Fact]
        public void ScalesLinearlyFromIdle()
        {
            // Idle sits near 0.08 of the scale, so it must read well clear of the redline.
            double idle = Id8MemoryTelemetry.DialRpmFor(0.08, 7000);
            Assert.InRange(idle, 600, 800);
            Assert.Equal(2 * idle, Id8MemoryTelemetry.DialRpmFor(0.16, 7000), 3);
        }

        [Fact]
        public void RefusesNonsenseRatherThanPropagatingIt()
        {
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(double.NaN, 7000));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(double.PositiveInfinity, 7000));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(0.5, 0));
            Assert.Equal(0.0, Id8MemoryTelemetry.DialRpmFor(-1.0, 7000));
        }
    }
}

namespace TrueforceForAll.Core.Tests
{
    // The wheel's panel draws ASCII only, and this is a Japanese game whose default driver name is
    // katakana. Without romanisation the name row is simply blank, which reads as a fault rather
    // than as an unsupported character set.
    public class Id8NameRomanisationTests
    {
        [Fact]
        public void RomanisesTheGamesOwnDefaultName()
        {
            // What the cabinet actually reported for a driver with no card.
            Assert.Equal("PUREIYA", Id8MemoryTelemetry.ToAscii("プレイヤー"));
        }

        [Fact]
        public void KeepsPlainAsciiAsItIs()
        {
            Assert.Equal("MHYTEE", Id8MemoryTelemetry.ToAscii("MHYTEE"));
        }

        [Fact]
        public void ConvertsFullWidthLatin()
        {
            // Japanese input produces full width letters for latin text.
            Assert.Equal("AB1", Id8MemoryTelemetry.ToAscii("ＡＢ１"));
        }

        [Fact]
        public void PrefersTheLongerSyllable()
        {
            // A naive per-character pass turns SHA into SIYA, because the small kana modifies the
            // one before it rather than standing alone.
            Assert.Equal("SHA", Id8MemoryTelemetry.ToAscii("シャ"));
            Assert.Equal("TOKYO", Id8MemoryTelemetry.ToAscii("トウキョウ")
                .Replace("U", ""));
        }

        [Fact]
        public void ReturnsNullWhenNothingCanBeShown()
        {
            // Kanji cannot be romanised without a dictionary, so the caller says GUEST rather than
            // drawing an empty row.
            Assert.Null(Id8MemoryTelemetry.ToAscii("赤城"));
            Assert.Null(Id8MemoryTelemetry.ToAscii(""));
            Assert.Null(Id8MemoryTelemetry.ToAscii(null));
        }
    }
}

namespace TrueforceForAll.Core.Tests
{
    // The floor under an arcade cabinet's steering force. Tested because the reference plugin's
    // own version is what it replaces, and that one steps at centre: it lifts the magnitude and
    // gates on the command being above a hair of nothing, so a force crossing zero jumps from plus
    // the floor to zero to minus the floor. Ours has to be continuous there or it is no better.
    public class ArcadeMinForceTests
    {
        [Fact]
        public void ZeroStaysZero()
        {
            // The whole point. A wheel asked for nothing must do nothing.
            Assert.Equal(0.0, FfbArcadePluginSource.ApplyMinForce(0.0, 0.20));
        }

        [Fact]
        public void OffMeansUntouched()
        {
            for (double f = -1.0; f <= 1.0; f += 0.25)
                Assert.Equal(f, FfbArcadePluginSource.ApplyMinForce(f, 0.0), 6);
        }

        [Fact]
        public void IsContinuousThroughCentre()
        {
            // The failure being fixed: no step as the force changes sign. Walking across zero in
            // small increments, no single step may jump by anything like the floor.
            const double min = 0.20, step = 0.0005;
            double prev = FfbArcadePluginSource.ApplyMinForce(-0.05, min);
            for (double f = -0.05 + step; f <= 0.05; f += step)
            {
                double now = FfbArcadePluginSource.ApplyMinForce(f, min);
                Assert.True(System.Math.Abs(now - prev) < 0.02,
                    $"jumped from {prev:0.###} to {now:0.###} at f={f:0.####}");
                prev = now;
            }
        }

        [Fact]
        public void LiftsRealForcesToTheFloor()
        {
            // Past the ramp the floor is fully applied. The ramp is tied to the floor's own size,
            // so a force at or beyond it gets the whole lift; this is what the setting is for.
            double atBand = FfbArcadePluginSource.ApplyMinForce(0.15, 0.20);
            Assert.InRange(atBand, 0.30, 0.38);
            double wellPast = FfbArcadePluginSource.ApplyMinForce(0.30, 0.20);
            Assert.InRange(wellPast, 0.42, 0.50);
        }

        [Fact]
        public void GainNearZeroStaysBoundedAtEveryFloor()
        {
            // The ring came from gain near zero, and a fixed narrow ramp made it worse the higher
            // the floor went. Tying the ramp to the floor holds it near two whatever is chosen.
            foreach (double min in new[] { 0.10, 0.20, 0.40, 0.60, 0.80 })
            {
                const double probe = 0.005;
                double gain = FfbArcadePluginSource.ApplyMinForce(probe, min) / probe;
                Assert.True(gain < 3.5, $"floor {min:0.00} gives {gain:0.0}x near zero, which rings");
            }
        }

        [Fact]
        public void IsOddAboutZero()
        {
            // A floor that is not symmetric would pull the wheel to one side.
            foreach (double f in new[] { 0.001, 0.01, 0.1, 0.5, 1.0 })
                Assert.Equal(-FfbArcadePluginSource.ApplyMinForce(f, 0.25),
                              FfbArcadePluginSource.ApplyMinForce(-f, 0.25), 9);
        }

        [Fact]
        public void NeverExceedsFullScale()
        {
            foreach (double m in new[] { 0.1, 0.5, 0.9, 0.99 })
                Assert.True(System.Math.Abs(FfbArcadePluginSource.ApplyMinForce(1.0, m)) <= 1.0);
        }

        [Fact]
        public void RisesMonotonically()
        {
            // The wheel must never push less as the game asks for more.
            double prev = -1;
            for (double f = 0; f <= 1.0; f += 0.01)
            {
                double now = FfbArcadePluginSource.ApplyMinForce(f, 0.20);
                Assert.True(now >= prev - 1e-9, $"went backwards at f={f:0.##}");
                prev = now;
            }
        }
    }
}

namespace TrueforceForAll.Core.Tests
{
    // The lift has to reach full strength on a sustained force and average away on one that
    // reverses several times a second. That separation is the only thing that can stop the wheel
    // oscillating while still lifting weak forces: no static curve can do both, because lifting a
    // 5 percent force needs gain above one near zero and gain above one in a feedback loop rings.
    public class ArcadeLiftSmoothingTests
    {
        // Mirrors what the source does per poll, so the arithmetic is pinned even though the real
        // one carries its state across calls on the poll thread.
        /// <summary>Mirrors LiftFollowPerPoll in the source. Kept here as a named constant so a
        /// change there shows up as a failing expectation rather than silently passing.</summary>
        private const double LiftFollow = 0.015;

        private static double Smooth(double prev, double target, double follow)
            => prev + (target - prev) * follow;

        [Fact]
        public void SustainedForceReachesTheFullLift()
        {
            const double f = 0.05, min = 0.20, follow = LiftFollow;
            double target = FfbArcadePluginSource.ApplyMinForce(f, min) - f;
            double lift = 0;
            // Half a second at the 500 Hz poll.
            // A second at the 500 Hz poll, which is longer than a corner takes to develop.
            for (int i = 0; i < 500; i++) lift = Smooth(lift, target, follow);
            Assert.True(lift > target * 0.9,
                $"a sustained force only reached {lift:0.####} of {target:0.####}");
        }

        [Fact]
        public void AlternatingForceAveragesAwayToNearNothing()
        {
            // A force reversing about eight times a second, which is what an oscillation looks
            // like. The lift must not follow it, or it feeds the loop that created it.
            const double mag = 0.05, min = 0.20, follow = LiftFollow;
            double lift = 0, worst = 0;
            for (int i = 0; i < 1000; i++)
            {
                double f = ((i / 30) % 2 == 0) ? mag : -mag;
                double target = FfbArcadePluginSource.ApplyMinForce(f, min) - f;
                lift = Smooth(lift, target, follow);
                if (i > 200) worst = System.Math.Max(worst, System.Math.Abs(lift));
            }
            double full = FfbArcadePluginSource.ApplyMinForce(mag, min) - mag;
            Assert.True(worst < full * 0.45,
                $"the lift still tracked the reversal at {worst:0.####} against {full:0.####}");
        }

        [Fact]
        public void TheGameForceItselfIsNeverSmoothed()
        {
            // Only the lift is filtered. A real force has to arrive when the game sends it, so the
            // smoothing must never be able to delay or soften the cabinet's own output.
            const double min = 0.0;   // no lift at all
            foreach (double f in new[] { -1.0, -0.3, 0.0, 0.3, 1.0 })
                Assert.Equal(f, FfbArcadePluginSource.ApplyMinForce(f, min), 9);
        }
    }

    /// <summary>The per-effect hold overrides.
    ///
    /// These decide how long a force keeps acting after the cabinet stops asking
    /// for it, so a wrong answer is a force held on the wheel with nothing behind
    /// it. Worth pinning rather than reading.</summary>
    public class ArcadeHoldSplitTests
    {
        private const uint KConstant = 1, KSine = 2, KTriangle = 3, KSawUp = 4, KSawDown = 5;
        private const uint KSpring = 6, KDamper = 7, KFriction = 9;

        [Fact]
        public void ZeroOverridesLeaveEveryPublishedLengthAlone()
        {
            foreach (uint kind in new uint[] { KConstant, KSine, KSpring, KFriction })
                Assert.Equal(500u, FfbArcadePluginSource.HeldLength(500, kind, 0, 0));
        }

        [Fact]
        public void SteeringHoldReachesTheConstantAndNothingElse()
        {
            Assert.Equal(2000u, FfbArcadePluginSource.HeldLength(500, KConstant, 2000, 0));
            Assert.Equal(500u,  FfbArcadePluginSource.HeldLength(500, KSpring,   2000, 0));
            Assert.Equal(49u,   FfbArcadePluginSource.HeldLength(49,  KSine,     2000, 0));
        }

        [Fact]
        public void VibrationHoldReachesEveryWaveformAndNothingElse()
        {
            foreach (uint kind in new uint[] { KSine, KTriangle, KSawUp, KSawDown })
                Assert.Equal(120u, FfbArcadePluginSource.HeldLength(49, kind, 0, 120));
            Assert.Equal(500u, FfbArcadePluginSource.HeldLength(500, KConstant, 0, 120));
            Assert.Equal(500u, FfbArcadePluginSource.HeldLength(500, KSpring,   0, 120));
            Assert.Equal(500u, FfbArcadePluginSource.HeldLength(500, KDamper,   0, 120));
        }

        /// <summary>The two are independent, which is the whole point: the
        /// cabinet ships one number for both and that is what forced this split.</summary>
        [Fact]
        public void TheTwoHoldsDoNotInterfere()
        {
            Assert.Equal(2000u, FfbArcadePluginSource.HeldLength(500, KConstant, 2000, 120));
            Assert.Equal(120u,  FfbArcadePluginSource.HeldLength(49,  KSine,     2000, 120));
        }

        /// <summary>An override shortens as readily as it lengthens. A user who
        /// wants the steering to drop away FASTER than the game says has the same
        /// control, and nothing in the path treats that as a special case.</summary>
        [Fact]
        public void AnOverrideCanShortenToo()
        {
            Assert.Equal(100u, FfbArcadePluginSource.HeldLength(5000, KConstant, 100, 0));
        }
    }

    /// <summary>The release that replaced the cliff at the end of a steering hold.
    ///
    /// The measurement behind it: this game leaves 5 and 6 second gaps with no
    /// steering command, so the force at the end of any hold is still large and
    /// dropping it in one frame is felt as the wheel being switched off.</summary>
    public class ArcadeSteeringReleaseTests
    {
        [Fact]
        public void FullAtTheStartAndNothingAtTheEnd()
        {
            Assert.Equal(1.0, FfbArcadePluginSource.ReleaseScale(0, 350));
            Assert.Equal(0.0, FfbArcadePluginSource.ReleaseScale(350, 350));
            Assert.Equal(0.0, FfbArcadePluginSource.ReleaseScale(10_000, 350));
        }

        [Fact]
        public void FallsAwayEvenly()
        {
            Assert.Equal(0.5, FfbArcadePluginSource.ReleaseScale(175, 350), 6);
            Assert.Equal(0.75, FfbArcadePluginSource.ReleaseScale(87.5, 350), 6);
        }

        /// <summary>Never negative, so a late reading cannot push the wheel the
        /// other way. That would turn a force fading out into a force reversing,
        /// which is the one outcome worse than the cliff being fixed.</summary>
        [Fact]
        public void NeverReversesAndNeverExceedsTheForceItStartedFrom()
        {
            for (double t = -50; t <= 700; t += 7)
            {
                double k = FfbArcadePluginSource.ReleaseScale(t, 350);
                Assert.InRange(k, 0.0, 1.0);
            }
        }

        [Fact]
        public void AZeroLengthReleaseIsJustTheOldBehaviour()
        {
            Assert.Equal(0.0, FfbArcadePluginSource.ReleaseScale(0, 0));
        }
    }
}
