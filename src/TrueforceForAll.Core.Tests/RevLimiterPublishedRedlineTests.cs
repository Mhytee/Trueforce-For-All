// Where a PUBLISHED per-car redline sits in the rev-limiter's cascade.
//
// This matters beyond the buzz: the rev-light flash and the bar's fill onset
// both read EffectiveRedlineRpm, so whatever wins here decides all three at
// once. That shared read IS the design, and it is why the dataset is fed in
// here rather than at the LED call site.
//
// The rule being pinned (owner, 2026-08-24) is that a published figure outranks
// everything except the user's own pin, because it records where the real car's
// DASH lights up rather than where its limiter sits, and never displaces
// today's behaviour for a car it knows nothing about.

using System.Collections.Generic;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class RevLimiterPublishedRedlineTests
    {
        private static RevLimiterEffect NewEffect()
        {
            var e = new RevLimiterEffect();
            e.Enabled = true;
            return e;
        }

        private static TelemetryFrame Frame(string gear, double rpms, double redline, double maxRpm)
            => new TelemetryFrame { Gear = gear, Rpms = rpms, RedlineRpm = redline, MaxRpm = maxRpm };

        [Fact]
        public void PublishedRedlineBeatsTheEstimateWhenNothingElseKnows()
        {
            // The case it exists for: no telemetry redline, no community value.
            // Without it the shift cue is 0.85 x MaxRpm and badged as a guess.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(7150, e.EffectiveRedlineRpm);
            Assert.False(e.IsRedlineGuessed);
        }

        [Fact]
        public void WithoutPublishedDataNothingChanges()
        {
            // The fallback the owner asked for: a car with no entry behaves
            // exactly as it does today, down to the guessed badge.
            var e = NewEffect();
            e.PublishedGearRedlines = null;

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(6800, e.EffectiveRedlineRpm);   // 0.85 x 8000
            Assert.True(e.IsRedlineGuessed);
        }

        [Fact]
        public void ThePublishedFigureBeatsTheGamesTelemetryRedline()
        {
            // The reversal that started this. Telemetry reports the LIMITER;
            // the dataset records where the dash lights up. Shifting at the
            // limiter is late by exactly the gap, and the wheel flashing at
            // 7000 while the screen flashed at 7150 was the visible symptom.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 7000, 8000));

            Assert.Equal(7150, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void ThePublishedFigureBeatsOurCommunityConsensus()
        {
            // The knock-on the owner should see stated plainly: raising the tier
            // above telemetry raises it above the community value too, because
            // that sits lower still. Defensible on the same grounds - a submitted
            // "redline" is a limiter reading like telemetry's - but it IS our own
            // users being overridden on a car the dataset covers, so it is pinned
            // rather than left to be discovered.
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(7150, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void ThePublishedFigureBeatsOurCommunityPerGearConsensus()
        {
            // Same knock-on, for the per-gear community tier.
            var e = NewEffect();
            e.CommunityGearRedlines = new Dictionary<int, int> { { 2, 6950 } };
            e.PublishedGearRedlines = new Dictionary<int, int> { { 2, 7150 } };

            e.OnTelemetry(Frame("2", 5000, 0, 8000));

            Assert.Equal(7150, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void TheUsersOwnValueStillWins()
        {
            var e = NewEffect();
            e.UserGearRedlines = new Dictionary<int, int> { { 0, 7400 } };
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(7400, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void FollowsTheGearSoTheCueMovesWithIt()
        {
            // The reason per-gear data is worth having at all: no sim reports a
            // redline that moves between gears, and on the cars that publish one
            // it genuinely does.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));
            Assert.Equal(7150, e.EffectiveRedlineRpm);

            e.OnTelemetry(Frame("6", 5000, 0, 8000));
            Assert.Equal(7250, e.EffectiveRedlineRpm);
        }

        [Theory]
        [InlineData("R")]
        [InlineData("N")]
        [InlineData("0")]
        public void ReverseAndNeutralFallThroughRatherThanBorrowingAGear(string gear)
        {
            // A published reverse ramp describes a stationary car and says
            // nothing about a shift point, so it must not supply one.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame(gear, 5000, 0, 8000));

            Assert.Equal(6800, e.EffectiveRedlineRpm);   // the 0.85 estimate
            Assert.True(e.IsRedlineGuessed);
        }

        [Fact]
        public void AGearWithNoPublishedEntryFallsThrough()
        {
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("4", 5000, 0, 8000));

            Assert.Equal(6800, e.EffectiveRedlineRpm);
            Assert.True(e.IsRedlineGuessed);
        }

        [Theory]
        [InlineData(20000)]   // absurdly above the rev ceiling
        [InlineData(50)]      // below any real engine speed
        public void ImplausiblePublishedValuesAreRejected(int published)
        {
            // Same sanity clamp the community per-gear tier gets. Bad outside
            // data must fall through to the estimate rather than move the cue
            // somewhere the engine never reaches.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, published } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(6800, e.EffectiveRedlineRpm);
            Assert.True(e.IsRedlineGuessed);
        }

        // ---- what happens when the dataset does NOT decide ----
        //
        // The published tier used to sit at the bottom and blend its per-gear
        // SHAPE onto a more trusted anchor. Raising it above telemetry made that
        // blend unreachable (it only ever acted when the current gear had an
        // entry, which is now exactly the case that returns outright), so it was
        // removed rather than left as dead code the comments still pointed at.
        // What remains worth pinning is that falling THROUGH the tier lands on
        // the next real source, whole and unmodified.

        [Fact]
        public void AGearTheDatasetDoesNotCoverFallsThroughWhole()
        {
            // The dataset knows gears 1 and 6; we are in 3. No shaping, no
            // interpolation, no borrowing a neighbouring gear: the game's own
            // figure stands exactly as reported.
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("3", 5000, 7000, 8000));
            Assert.Equal(7000, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void AnImplausiblePublishedGearFallsThroughToTheNextRealSource()
        {
            // The clamp must hand off to the tier below rather than to the 0.85
            // estimate: bad data about this car says nothing about the community
            // value we already hold for it.
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 2, 12000 } };

            e.OnTelemetry(Frame("2", 5000, 0, 8000));
            Assert.Equal(6900, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void ReverseFallsThroughWhole()
        {
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("R", 5000, 0, 8000));
            Assert.Equal(6900, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void AnEmptyMapIsTreatedAsNoData()
        {
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int>();

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(6800, e.EffectiveRedlineRpm);
            Assert.True(e.IsRedlineGuessed);
        }
    }
}
