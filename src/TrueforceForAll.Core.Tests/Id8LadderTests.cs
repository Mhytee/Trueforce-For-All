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
