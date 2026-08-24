// Where a PUBLISHED per-car redline sits in the rev-limiter's cascade.
//
// This matters beyond the buzz: the rev-light flash and the bar's fill onset
// both read EffectiveRedlineRpm, so whatever wins here decides all three at
// once. The rule being pinned is that an outside dataset never outranks the
// game's own telemetry or our community's agreed value, and never displaces
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
        public void TheGamesOwnTelemetryRedlineStillWins()
        {
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 7000, 8000));

            Assert.Equal(7000, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void OurOwnCommunityConsensusStillWins()
        {
            // The precedence that matters politically as well as technically:
            // our users' agreed value for this car outranks an outside dataset.
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));

            Assert.Equal(6900, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void OurCommunityPerGearConsensusStillWins()
        {
            var e = NewEffect();
            e.CommunityGearRedlines = new Dictionary<int, int> { { 2, 6950 } };
            e.PublishedGearRedlines = new Dictionary<int, int> { { 2, 7150 } };

            e.OnTelemetry(Frame("2", 5000, 0, 8000));

            Assert.Equal(6950, e.EffectiveRedlineRpm);
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

        // ---- anchor and shape ----
        //
        // When a more trusted source gives ONE figure for the whole car and the
        // published set has a complete per-gear set, our figure stays the anchor
        // and only the dataset's deviations around its own mean are adopted.

        [Fact]
        public void OurCommunityValueAnchorsWhileTheDatasetSuppliesThePerGearShape()
        {
            // Published gears 7150 and 7250, mean 7200, so deviations are -50 and
            // +50. Our community says 6900, which must remain the anchor.
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));
            Assert.Equal(6850, e.EffectiveRedlineRpm);   // 6900 - 50

            e.OnTelemetry(Frame("6", 5000, 0, 8000));
            Assert.Equal(6950, e.EffectiveRedlineRpm);   // 6900 + 50
        }

        [Fact]
        public void TheGamesTelemetryFigureIsShapedTheSameWay()
        {
            var e = NewEffect();
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("1", 5000, 7000, 8000));
            Assert.Equal(6950, e.EffectiveRedlineRpm);   // 7000 - 50
        }

        [Fact]
        public void ACarWhosePublishedGearsAllMatchIsLeftCompletelyAlone()
        {
            // 665 of 717 cars are like this, so the blend must be a no-op for
            // them rather than nudging a number for no reason.
            var e = NewEffect();
            e.CarFactsRedline = 6900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7000 }, { 2, 7000 }, { 3, 7000 } };

            e.OnTelemetry(Frame("2", 5000, 0, 8000));
            Assert.Equal(6900, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void OurOwnPerGearConsensusIsNeverReshaped()
        {
            // Already gear-specific and more trusted, so an outside source must
            // not bend it.
            var e = NewEffect();
            e.CommunityGearRedlines = new Dictionary<int, int> { { 1, 6950 } };
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));
            Assert.Equal(6950, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void TheUsersOwnValueIsNeverReshaped()
        {
            var e = NewEffect();
            e.UserGearRedlines = new Dictionary<int, int> { { 0, 7400 } };
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 7150 }, { 6, 7250 } };

            e.OnTelemetry(Frame("1", 5000, 0, 8000));
            Assert.Equal(7400, e.EffectiveRedlineRpm);
        }

        [Fact]
        public void AShapeThatWouldPushThePointPastTheRevCeilingIsRefused()
        {
            // A deviation that large means the sources disagree about more than
            // shape, so ours is kept whole.
            var e = NewEffect();
            e.CarFactsRedline = 7900;
            e.PublishedGearRedlines = new Dictionary<int, int> { { 1, 4000 }, { 2, 12000 } };

            e.OnTelemetry(Frame("2", 5000, 0, 8000));
            Assert.Equal(7900, e.EffectiveRedlineRpm);   // 7900 + 4000 would exceed the ceiling
        }

        [Fact]
        public void ReverseIsNotReshaped()
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
