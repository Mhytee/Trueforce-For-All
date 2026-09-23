// Parser tests for the TeknoParrot leaderboard page.
//
// The markup here is copied verbatim from a real fetch of the Initial D 8 board, because a parser
// tested against markup invented by its own author only proves the author is consistent. Every
// structural quirk is theirs: the nested spans in the date cell, the entity-escaped ampersand in
// the details link, and the empty video cell that sits between the time and the date.
//
// The PLAYER NAMES are not theirs, deliberately. Real handles from the public board stood here
// until 2026-09-07. Two reasons they had to go, and the second is the one that settles it. A
// leaderboard is a third party's to publish and not ours to copy into a repository that is going
// to be public. And the fixture had already drifted: row two paired a real player's real entry id
// with a time they never set, so it read as a record of theirs while asserting something they did
// not do. Placeholders cannot drift like that. The times, dates and entry ids are untouched
// because they are what the parser is actually asserting on.

using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class TeknoParrotLeaderboardTests
    {
        private const string RealPage = @"
<table class=""records-table"">
  <thead><tr><th scope=""col"" class=""rank-col"">#</th></tr></thead>
  <tbody id=""track-records-Akagi_Downhill"">
    <tr>
      <td class=""rank-col"">1</td>
      <td class=""name-col"">
        <a class=""btn highscore-player-link"" target=""_blank"" href=""/en/ProfileViewer/Index/TPRacer1"">TPRacer1</a>
      </td>
      <td class=""detail-col"">MAZDA RX-7 III (FC3S)</td>
      <td class=""time-col"">2:11:053</td>
      <td class=""video-col"">
      </td>
      <td class=""date-col"">
        <span class=""date-single-line d-none d-md-inline"">27-04-2025 22:37:49</span>
        <span class=""date-single-line d-inline d-md-none"">27-04-2025 22:37:49</span>
      </td>
      <td class=""action-col"">
        <a class=""btn btn-info highscore-details-link"" target=""_blank"" title=""View details"" href=""/en/Highscore/EntrySpecific?gameId=ID8&amp;entryId=4600"">
          <i class='bi bi-search' aria-hidden=""true""></i>
        </a>
      </td>
    </tr>
    <tr>
      <td class=""rank-col"">2</td>
      <td class=""name-col"">
        <a class=""btn highscore-player-link"" target=""_blank"" href=""/en/ProfileViewer/Index/TPRacer2"">TPRacer2</a>
      </td>
      <td class=""detail-col"">TOYOTA SPRINTER TRUENO GT-APEX (AE86)</td>
      <td class=""time-col"">2:14:900</td>
      <td class=""video-col""></td>
      <td class=""date-col"">
        <span class=""date-single-line d-none d-md-inline"">01-05-2025 09:03:11</span>
      </td>
      <td class=""action-col"">
        <a class=""btn btn-info highscore-details-link"" href=""/en/Highscore/EntrySpecific?gameId=ID8&amp;entryId=10526""></a>
      </td>
    </tr>
  </tbody>
</table>
<table class=""records-table"">
  <tbody id=""track-records-Lake_Akina_Counterclockwise"">
    <tr>
      <td class=""rank-col"">1</td>
      <td class=""name-col""><a class=""btn highscore-player-link"" href=""/en/ProfileViewer/Index/someone"">someone</a></td>
      <td class=""detail-col"">MAZDA RX-7 (FD3S)</td>
      <td class=""time-col"">58:420</td>
      <td class=""video-col""></td>
      <td class=""date-col""><span class=""date-single-line d-none d-md-inline"">14-06-2025 12:00:00</span></td>
      <td class=""action-col""></td>
    </tr>
  </tbody>
</table>";

        [Fact]
        public void ParsesBoardsAndRows()
        {
            var boards = TeknoParrotLeaderboard.Parse(RealPage);

            Assert.Equal(2, boards.Count);
            Assert.True(boards.ContainsKey("Akagi_Downhill"));

            var akagi = boards["Akagi_Downhill"];
            Assert.Equal("Akagi", akagi.Course);
            Assert.Equal("Downhill", akagi.Direction);
            Assert.Equal(2, akagi.Entries.Count);

            var top = akagi.Entries[0];
            Assert.Equal(1, top.Rank);
            Assert.Equal("TPRacer1", top.Player);
            Assert.Equal("MAZDA RX-7 III (FC3S)", top.Car);
            Assert.Equal("2:11:053", top.TimeText);
            Assert.Equal(new TimeSpan(0, 0, 2, 11, 53), top.Time);
            Assert.Equal(new DateTime(2025, 4, 27, 22, 37, 49), top.Submitted);
            Assert.Equal(4600, top.EntryId);

            Assert.Equal("TPRacer2", akagi.Entries[1].Player);
            Assert.Equal(10526, akagi.Entries[1].EntryId);
        }

        [Fact]
        public void SplitsEveryDirectionTheSiteUses()
        {
            // The real thirty-two boards use four different direction vocabularies, and a course
            // name can itself contain a space, which is what makes "last underscore wins" wrong.
            Check("Akagi_Downhill", "Akagi", "Downhill");
            Check("Akina_Hill_Climb", "Akina", "Hill Climb");
            Check("Akina_Snow_Hill_Climb", "Akina Snow", "Hill Climb");
            Check("Lake_Akina_Counterclockwise", "Lake Akina", "Counterclockwise");
            Check("Lake_Akina_Clockwise", "Lake Akina", "Clockwise");
            Check("Happogahara_Outbound", "Happogahara", "Outbound");
            Check("Tsukuba_Inbound", "Tsukuba", "Inbound");
            Check("Irohazaka_Reverse", "Irohazaka", "Reverse");
            Check("Tsubaki_Line_Downhill", "Tsubaki Line", "Downhill");
        }

        private static void Check(string key, string course, string direction)
        {
            string c, d;
            TeknoParrotLeaderboard.SplitCourse(key, out c, out d);
            Assert.Equal(course, c);
            Assert.Equal(direction, d);
        }

        [Fact]
        public void AnUnknownDirectionKeepsTheWholeKeyAsTheCourse()
        {
            // A course type they add later must show up as a course rather than vanish.
            string c, d;
            TeknoParrotLeaderboard.SplitCourse("Somewhere_New_Sideways", out c, out d);
            Assert.Equal("Somewhere New Sideways", c);
            Assert.Equal("", d);
        }

        [Fact]
        public void TimesAreMinutesSecondsMilliseconds()
        {
            // The trap: TimeSpan.Parse reads "2:11:053" as 2 hours 11 minutes 53 seconds.
            Assert.Equal(new TimeSpan(0, 0, 2, 11, 53), TeknoParrotLeaderboard.ParseTime("2:11:053"));
            Assert.Equal(new TimeSpan(0, 0, 0, 58, 420), TeknoParrotLeaderboard.ParseTime("58:420"));
            Assert.Equal(new TimeSpan(0, 1, 2, 11, 53), TeknoParrotLeaderboard.ParseTime("1:2:11:053"));
        }

        [Fact]
        public void NonsenseTimesAreNullRatherThanWrong()
        {
            Assert.Null(TeknoParrotLeaderboard.ParseTime(null));
            Assert.Null(TeknoParrotLeaderboard.ParseTime(""));
            Assert.Null(TeknoParrotLeaderboard.ParseTime("--"));
            Assert.Null(TeknoParrotLeaderboard.ParseTime("2:99:053"));   // 99 seconds
            Assert.Null(TeknoParrotLeaderboard.ParseTime("1:2:3:4:5"));
        }

        [Fact]
        public void DatesAreDayFirstRegardlessOfTheMachine()
        {
            // 27-04 can only be April. A locale-driven parse would read this as a US month and
            // either throw or land eight months out.
            Assert.Equal(new DateTime(2025, 4, 27, 22, 37, 49),
                TeknoParrotLeaderboard.ParseDate("27-04-2025 22:37:49"));
            Assert.Null(TeknoParrotLeaderboard.ParseDate("not a date"));
        }

        [Fact]
        public void APageThatIsNotALeaderboardYieldsNoBoards()
        {
            // A login wall, an error page or a redirect must read as "no data", never as a throw.
            Assert.Empty(TeknoParrotLeaderboard.Parse(null));
            Assert.Empty(TeknoParrotLeaderboard.Parse(""));
            Assert.Empty(TeknoParrotLeaderboard.Parse("<html><body>Sign in to continue</body></html>"));
        }

        [Fact]
        public void HeaderRowsAreNotMistakenForResults()
        {
            var boards = TeknoParrotLeaderboard.Parse(@"
<tbody id=""track-records-Akagi_Downhill"">
  <tr><th class=""rank-col"">#</th><th class=""name-col"">Driver</th></tr>
</tbody>");
            Assert.Single(boards);
            Assert.Empty(boards["Akagi_Downhill"].Entries);
        }

        [Fact]
        public void CourseMatchingIgnoresSpellingDifferences()
        {
            var boards = TeknoParrotLeaderboard.Parse(RealPage);
            // The cabinet says AKAGI, the site says Akagi_Downhill.
            var found = TeknoParrotLeaderboard.BoardsForCourse(boards, "AKAGI");
            Assert.Single(found);
            Assert.Equal("Akagi_Downhill", found[0].Key);

            // And a multi-word course still matches with different spacing.
            Assert.Single(TeknoParrotLeaderboard.BoardsForCourse(boards, "lake akina"));
            Assert.Empty(TeknoParrotLeaderboard.BoardsForCourse(boards, "Myogi"));
        }

        [Fact]
        public void PageUrlUsesTheProfileName()
        {
            Assert.Equal("https://teknoparrot.com/en/Highscore/GameSpecific/ID8",
                TeknoParrotLeaderboard.PageUrl("ID8"));
            Assert.Null(TeknoParrotLeaderboard.PageUrl(null));
        }
    
        /// <summary>Direction 0 is Downhill. Confirmed on the cabinet 2026-09-06: a marker written
        /// with direction 0 appeared on "Akina DH". Pinned because getting it backwards puts every
        /// uphill time on the downhill board, where a wrong time still looks plausible and so the
        /// mistake would not announce itself.</summary>
        [Theory]
        [InlineData("Downhill", 0)]
        [InlineData("downhill", 0)]
        [InlineData("Hill_Climb", 1)]
        [InlineData("Hill Climb", 1)]
        [InlineData("HILLCLIMB", 1)]
        [InlineData("Outbound", 0)]
        [InlineData("Inbound", 1)]
        [InlineData("Clockwise", 0)]
        [InlineData("Counterclockwise", 1)]
        [InlineData("Reverse", 1)]
        public void DirectionIndexMatchesTheCabinet(string text, int expected)
        {
            Assert.Equal(expected, TeknoParrotLeaderboard.DirectionIndex(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Sideways")]
        public void UnknownDirectionIsRefusedRatherThanGuessed(string text)
        {
            Assert.Equal(-1, TeknoParrotLeaderboard.DirectionIndex(text));
        }
    
        /// <summary>Course ids are the game's own, from the exe table at 0x0121c9e0. Twelve of the
        /// sixteen match TeknoParrot's spelling once punctuation is dropped; these four do not and
        /// are aliased explicitly. Verified against the live page 2026-09-06, where all 32 site
        /// boards mapped onto all 32 game slots exactly once, with no gaps and no duplicates.</summary>
        [Theory]
        [InlineData("AkinaLake", 0)]
        [InlineData("Lake Akina", 0)]
        [InlineData("Happo", 6)]
        [InlineData("Happogahara", 6)]
        [InlineData("Tsubaki", 8)]
        [InlineData("Tsubaki Line", 8)]
        [InlineData("Momiji", 14)]
        [InlineData("Momiji Line", 14)]
        [InlineData("Akina", 3)]
        [InlineData("Akina-Snow", 12)]
        [InlineData("Akina Snow", 12)]
        [InlineData("Nanamagari", 15)]
        public void CourseIdAcceptsBothTheCabinetAndTheSiteSpelling(string name, int expected)
        {
            Assert.Equal(expected, TeknoParrotLeaderboard.CourseId(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Mount Akina Special Stage")]
        public void UnknownCourseIsRefusedRatherThanGuessed(string name)
        {
            Assert.Equal(-1, TeknoParrotLeaderboard.CourseId(name));
        }

        /// <summary>Akina and Akina-Snow are different courses that share a word. A prefix or
        /// contains match would collapse them, and the times would land on the wrong board.</summary>
        [Fact]
        public void AkinaAndAkinaSnowStayDistinct()
        {
            Assert.NotEqual(TeknoParrotLeaderboard.CourseId("Akina"),
                            TeknoParrotLeaderboard.CourseId("Akina-Snow"));
        }

        /// <summary>Every one of the game's 32 boards is reachable, and no two course names collide
        /// onto the same id.</summary>
        [Fact]
        public void AllSixteenCoursesAreDistinctAndCoverZeroToFifteen()
        {
            string[] cabinet = { "AkinaLake","Myogi","Akagi","Akina","Irohazaka","Tsukuba","Happo",
                                 "Nagao","Tsubaki","Usui","Sadamine","Tsuchisaka","AkinaSnow",
                                 "Hakone","Momiji","Nanamagari" };
            var ids = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < cabinet.Length; i++)
            {
                int id = TeknoParrotLeaderboard.CourseId(cabinet[i]);
                Assert.Equal(i, id);
                Assert.True(ids.Add(id), "duplicate id for " + cabinet[i]);
            }
            Assert.Equal(16, ids.Count);
        }
    }
}
