// Ladder climb: a window of the field centred on the player rather than its top.
//
// The board holds ten rows and TeknoParrot's field is about 1765 deep, so the top ten is ten world
// records: a target nobody reaches and a board you never appear on. These pin the two things that
// make the window useful rather than merely different: where the player is placed, because the
// in-race time to beat is copied from the TOP row, and that nobody is ever shown below the place
// they actually hold.

using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8LadderTests
    {
        private static List<Id8LeaderboardEntry> Field(int n, string mineAt)
        {
            var l = new List<Id8LeaderboardEntry>();
            for (int i = 1; i <= n; i++)
                l.Add(new Id8LeaderboardEntry { Username = i.ToString() == mineAt ? "Mhytee" : "D" + i,
                                                CarId = 0, GoalMs = 120000 + i * 100 });
            return l;
        }

        private static List<string> Names(IReadOnlyList<Id8LeaderboardEntry> w) =>
            w.Select(e => e.Username).ToList();

        [Fact]
        public void TheDefaultBoardPutsThePlayerFifth()
        {
            var w = Id8Leaderboard.LadderWindow(Field(1000, "500"), "Mhytee");
            Assert.Equal(10, w.Count);
            Assert.Equal("Mhytee", w[4].Username);          // fifth row, 1 based
            Assert.Equal("D496", w[0].Username);            // four above
            Assert.Equal("D505", w[9].Username);            // five below
        }

        /// <summary>The in-race variant. Second place means the TOP row is the next time to beat,
        /// which is the row the game copies into the HUD.</summary>
        [Fact]
        public void SecondPlaceMakesTheTopRowTheNextTimeToBeat()
        {
            var w = Id8Leaderboard.LadderWindow(Field(1000, "500"), "Mhytee", placeAt: 2);
            Assert.Equal("D499", w[0].Username);            // exactly one place ahead
            Assert.Equal("Mhytee", w[1].Username);
        }

        /// <summary>Nobody is shown below the place they hold. Third really means third, with the
        /// window simply running from the top of the field.</summary>
        [Theory]
        [InlineData("1", 0)]
        [InlineData("2", 1)]
        [InlineData("3", 2)]
        public void APlayerNearTheTopKeepsTheirRealPlace(string at, int expectedIndex)
        {
            var w = Id8Leaderboard.LadderWindow(Field(1000, at), "Mhytee");
            Assert.Equal(expectedIndex, Names(w).IndexOf("Mhytee"));
            // Starts at the top of the FIELD, whoever holds it. Asserting a name here was wrong:
            // when the player is genuinely first, the first row is them.
            Assert.Equal(120100, w[0].GoalMs);
        }

        /// <summary>At the bottom the window slides up so the board is still full, rather than
        /// trailing off into filler rows.</summary>
        [Fact]
        public void TheBottomOfTheFieldStillFillsTheBoard()
        {
            var w = Id8Leaderboard.LadderWindow(Field(1000, "1000"), "Mhytee");
            Assert.Equal(10, w.Count);
            Assert.Equal("Mhytee", w[9].Username);
            Assert.Equal("D991", w[0].Username);
        }

        /// <summary>No time on this course means nothing to climb from, so the fastest times are
        /// the right first thing to show. This is the "top ten until you set a time" rule.</summary>
        [Fact]
        public void APlayerWithNoTimeSeesTheTopOfTheBoard()
        {
            var w = Id8Leaderboard.LadderWindow(Field(1000, "-"), "Mhytee");
            Assert.Equal("D1", w[0].Username);
            Assert.Equal("D10", w[9].Username);
        }

        /// <summary>A field smaller than the board is returned whole, not padded or clipped.</summary>
        [Fact]
        public void ASmallFieldComesBackWhole()
        {
            var w = Id8Leaderboard.LadderWindow(Field(4, "3"), "Mhytee");
            Assert.Equal(4, w.Count);
            Assert.Equal("Mhytee", w[2].Username);
        }

        /// <summary>"All" is one ranked field, not three lists shown in turn.
        ///
        /// This is the test that would have caught the two bugs that made ladder climb a no-op:
        /// Combine ignored its own `take` and always returned ten, and the community fetch asked for
        /// ten rows. The window was therefore cut from a list already truncated to the top ten, so
        /// it WAS the top ten, and the feature looked like it worked.</summary>
        [Fact]
        public void AllBlendsCommunityTeknoParrotAndYourOwnIntoOneLadder()
        {
            // A deep TeknoParrot field, a few tf4all names scattered through it by time, and the
            // player's own record sitting between them.
            var tekno = new List<Id8LeaderboardEntry>();
            for (int i = 1; i <= 200; i++)
                tekno.Add(new Id8LeaderboardEntry { Username = "TP" + i, CarId = 0, GoalMs = 120000 + i * 100 });

            var community = new List<Id8LeaderboardEntry>
            {
                new Id8LeaderboardEntry { Username = "Tf4a", CarId = 0, GoalMs = 124950 },   // ~50th
                new Id8LeaderboardEntry { Username = "Tf4b", CarId = 0, GoalMs = 125150 },   // ~52nd
            };
            var mine = new List<Id8LeaderboardRecord> { Real("Mhytee", 125050) };            // ~51st

            var rows = Id8Leaderboard.BuildBoard(Id8BoardSource.Merged, ExistingBoard(),
                                                 community, tekno,
                                                 Id8Leaderboard.LocalRecordsFrom(mine), "Mhytee");
            var names = rows.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)).ToList();

            // The player is on the board at all, which the top ten could never have shown.
            Assert.Contains("MHYTEE", names);
            // And their immediate neighbours from the OTHER two pools are there with them.
            Assert.Contains("TF4A", names);
            Assert.Contains("TF4B", names);
            Assert.Contains(names, n => n.StartsWith("TP"));
            // Ranked as one field: strictly ascending by time, whichever pool a row came from.
            var times = rows.Where(r => !r.IsFiller).Select(r => r.GoalMs).ToList();
            Assert.Equal(times.OrderBy(t => t).ToList(), times);
            // Nowhere near the front, which is the whole point.
            Assert.DoesNotContain("TP1", names);
        }

        private static List<Id8LeaderboardRecord> ExistingBoard() => new List<Id8LeaderboardRecord>
        {
            Id8Leaderboard.DefaultRow(), Id8Leaderboard.DefaultRow(),
        };

        private static Id8LeaderboardRecord Real(string name, int ms) => new Id8LeaderboardRecord
        {
            RawName = Id8Name.Encode(Id8Name.Sanitize(name)),
            Reserved = new byte[] { 0, 0, Id8LeaderboardRecord.ConstantAt16 },
            Flags = Id8LeaderboardRecord.FlagReal,
            PlayerId = 5553014,
            GoalMs = ms,
        };

        /// <summary>A ladder needs rungs. tf4all alone does not have them yet, and Local is a board
        /// of one person by definition.</summary>
        [Theory]
        [InlineData(Id8BoardSource.Merged, true)]
        [InlineData(Id8BoardSource.TeknoParrot, true)]
        [InlineData(Id8BoardSource.Community, false)]
        [InlineData(Id8BoardSource.Local, false)]
        public void ClimbIsOfferedOnlyWhereThereIsAFieldToClimb(Id8BoardSource source, bool ok)
        {
            Assert.Equal(ok, Id8Leaderboard.SupportsLadder(source));
        }
    }
}
