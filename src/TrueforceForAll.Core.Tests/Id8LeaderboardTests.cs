using System;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>The leaderboard record format, name encoding and merge.
    ///
    /// The byte fixtures here are real records lifted from the running game on 2026-09-06, not
    /// invented ones: one of the player's own rows and one of the SEGA filler rows. If the record
    /// layout is ever re-read differently, these fail rather than silently writing rows the game
    /// will not render.</summary>
    public class Id8LeaderboardTests
    {
        // Akina Lake downhill, the player's own record: name MHYTEE, goal 2m19.932.
        private static byte[] RealRecord() => new byte[]
        {
            0x82,0x6c,0x82,0x67,0x82,0x78,0x82,0x73,0x82,0x64,0x82,0x64,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x09,0x01,0x76,0xbb,0x54,0x00,0xe0,0xf4,0x47,0x68,
            0xfd,0x14,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x9c,0x22,0x02,0x00,
        };

        // A SEGA filler row: goal exactly six minutes.
        private static byte[] FillerRecord() => new byte[]
        {
            0x82,0x72,0x82,0x64,0x82,0x66,0x82,0x60,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x09,0x02,0x00,0x00,0x00,0x00,0xb0,0x84,0xc3,0x52,
            0xc1,0xd4,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x40,0x7e,0x05,0x00,
        };

        [Fact]
        public void ReadsARealRecordFromTheGame()
        {
            Id8LeaderboardRecord r = Id8LeaderboardRecord.Read(RealRecord(), 0);

            Assert.Equal("MHYTEE", Id8Name.Decode(r.RawName));
            Assert.Equal(139932, r.GoalMs);           // 2m19.932
            Assert.Equal(70909, r.Section1);          // 1m10.909
            Assert.Equal(Id8LeaderboardRecord.FlagReal, r.Flags);
            Assert.Equal(0x0054bb76u, r.PlayerId);
            Assert.Equal(0x6847f4e0u, r.UnixTime);
            Assert.False(r.IsFiller);
        }

        [Fact]
        public void ReadsAFillerRecordAndKnowsItIsFiller()
        {
            Id8LeaderboardRecord r = Id8LeaderboardRecord.Read(FillerRecord(), 0);

            Assert.Equal("SEGA", Id8Name.Decode(r.RawName));
            Assert.Equal(Id8LeaderboardRecord.DefaultGoalMs, r.GoalMs);
            Assert.Equal(Id8LeaderboardRecord.FlagDefault, r.Flags);
            Assert.Equal(0u, r.PlayerId);
            Assert.True(r.IsFiller);
        }

        [Fact]
        public void RecordRoundTripsByteForByte()
        {
            byte[] original = RealRecord();
            var buf = new byte[Id8LeaderboardRecord.Size];
            Id8LeaderboardRecord.Read(original, 0).Write(buf, 0);

            Assert.Equal(original, buf);
        }

        [Theory]
        [InlineData("A", 0x82, 0x60)]
        [InlineData("Z", 0x82, 0x79)]
        [InlineData("M", 0x82, 0x6c)]
        [InlineData("0", 0x82, 0x4f)]
        [InlineData("9", 0x82, 0x58)]
        [InlineData("-", 0x81, 0x7c)]
        [InlineData("_", 0x81, 0x51)]
        public void EncodesFullwidthShiftJis(string text, byte hi, byte lo)
        {
            byte[] b = Id8Name.Encode(text);
            Assert.Equal(new byte[] { hi, lo }, b);
        }

        [Fact]
        public void EncodeMatchesTheGamesOwnBytesForMhytee()
        {
            byte[] expected = RealRecord().Take(12).ToArray();
            Assert.Equal(expected, Id8Name.Encode("MHYTEE"));
        }

        [Fact]
        public void EncodeAndDecodeAreInverse()
        {
            const string s = "TF4ALL-1";
            Assert.Equal(s, Id8Name.Decode(Id8Name.Encode(s)));
        }

        [Theory]
        [InlineData("mhytee", "MHYTEE")]
        [InlineData("VeryLongUsername", "VERYLONG")]         // 8 characters, no more
        [InlineData("Ryosuke.Takahashi", "RYOSUKET")]        // the dot is dropped, then truncated
        [InlineData("naïve", "NAVE")]                        // unmappable dropped, not substituted
        [InlineData("...", Id8Name.Fallback)]                // nothing left, so the fallback
        [InlineData("", Id8Name.Fallback)]
        [InlineData(null, Id8Name.Fallback)]
        [InlineData("  spaced", "SPACED")]                   // no leading space
        // profiles.username is ^[a-zA-Z0-9_]{3,32}$, so underscores are legal and must survive.
        [InlineData("drift_king", "DRIFT_KI")]
        [InlineData("a_b", "A_B")]
        public void SanitizeTruncatesToWhatTheScreenHolds(string input, string expected)
        {
            Assert.Equal(expected, Id8Name.Sanitize(input));
        }

        /// <summary>Every name the account system can produce has to survive the round trip,
        /// because the board name is not a nickname the user can fix: it is their username.</summary>
        [Theory]
        [InlineData("abc")]                                  // the 3-character minimum
        [InlineData("Drift_King_99")]
        [InlineData("________")]
        [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]     // the 32-character maximum
        public void AnyLegalUsernameEncodesAfterSanitizing(string username)
        {
            string s = Id8Name.Sanitize(username);
            Assert.InRange(s.Length, 1, Id8Name.MaxChars);
            byte[] enc = Id8Name.Encode(s);
            Assert.NotNull(enc);
            Assert.Equal(s, Id8Name.Decode(enc));
        }

        /// <summary>The boundary itself, pinned because it moved once: nine characters
        /// fit the field but ran into the time column on screen, so eight is the limit and
        /// eight must still work.</summary>
        [Fact]
        public void ANameAtTheLimitStillEncodes()
        {
            Assert.Equal(8, Id8Name.MaxChars);
            Assert.NotNull(Id8Name.Encode("EIGHTCHR"));
        }

        [Fact]
        public void EverySanitizedNameEncodes()
        {
            foreach (string s in new[] { "mhytee", "VeryLongUsername", "naïve", "...", "a b c d e f" })
                Assert.NotNull(Id8Name.Encode(Id8Name.Sanitize(s)));
        }

        [Fact]
        public void EncodeRefusesANameTooLongForTheScreen()
        {
            // Nine still fits the FIELD but not the layout, and Encode enforces the
            // display limit, so this is refused a character earlier than the bytes require.
            Assert.Null(Id8Name.Encode("NINECHARS"));
        }

        [Fact]
        public void SlotIndexMatchesTheGamesArithmetic()
        {
            Assert.Equal(0, Id8Leaderboard.SlotIndex(0, 0));
            Assert.Equal(1, Id8Leaderboard.SlotIndex(0, 1));
            Assert.Equal(6, Id8Leaderboard.SlotIndex(3, 0));    // Akina downhill
            Assert.Equal(31, Id8Leaderboard.SlotIndex(15, 1));
        }

        [Fact]
        public void RecordOffsetPagesByTheCourseSlotCount()
        {
            Assert.Equal(0, Id8Leaderboard.RecordOffset(0, 0, 0));
            Assert.Equal(48 * 32, Id8Leaderboard.RecordOffset(0, 0, 1));
            Assert.Equal(48 * (6 + 32 * 2), Id8Leaderboard.RecordOffset(3, 0, 2));
        }

        private static Id8LeaderboardEntry Entry(string name, int ms) =>
            new Id8LeaderboardEntry { Username = name, GoalMs = ms };

        [Fact]
        public void MergeAlwaysProducesTenRows()
        {
            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                new[] { Entry("alpha", 100000) });

            Assert.Equal(Id8Leaderboard.Ranks, rows.Length);
        }

        [Fact]
        public void MergeSortsByTimeAndDropsFiller()
        {
            var existing = new[]
            {
                Id8LeaderboardRecord.Read(RealRecord(), 0),     // MHYTEE 139932
                Id8LeaderboardRecord.Read(FillerRecord(), 0),   // SEGA filler
            };
            var incoming = new[] { Entry("fast", 100000), Entry("slow", 200000) };

            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(existing, incoming);

            Assert.Equal("FAST", Id8Name.Decode(rows[0].RawName));
            Assert.Equal("MHYTEE", Id8Name.Decode(rows[1].RawName));
            Assert.Equal("SLOW", Id8Name.Decode(rows[2].RawName));
            Assert.True(rows[3].IsFiller);
        }

        [Fact]
        public void MergeKeepsTheLocalRecordOnATie()
        {
            var existing = new[] { Id8LeaderboardRecord.Read(RealRecord(), 0) };
            var incoming = new[] { Entry("rival", 139932) };   // exactly the same time

            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(existing, incoming);

            Assert.Equal("MHYTEE", Id8Name.Decode(rows[0].RawName));
            Assert.Equal("RIVAL", Id8Name.Decode(rows[1].RawName));
        }

        [Fact]
        public void MergeCanBeToldToDropTheLocalRows()
        {
            var existing = new[] { Id8LeaderboardRecord.Read(RealRecord(), 0) };
            var incoming = new[] { Entry("only", 150000) };

            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(existing, incoming, keepExisting: false);

            Assert.Equal("ONLY", Id8Name.Decode(rows[0].RawName));
            Assert.DoesNotContain(rows, r => Id8Name.Decode(r.RawName) == "MHYTEE");
        }

        [Fact]
        public void MergeKeepsOnlyTheFastestTenWhenOversubscribed()
        {
            var incoming = Enumerable.Range(1, 25).Select(i => Entry("R" + i, 100000 + i * 1000)).ToArray();

            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(Array.Empty<Id8LeaderboardRecord>(), incoming);

            Assert.Equal(Id8Leaderboard.Ranks, rows.Length);
            Assert.Equal(101000, rows[0].GoalMs);
            Assert.Equal(110000, rows[9].GoalMs);
            Assert.True(rows.Select(r => r.GoalMs).SequenceEqual(rows.Select(r => r.GoalMs).OrderBy(x => x)));
        }

        [Fact]
        public void MergeIgnoresEntriesWithNoTime()
        {
            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(
                Array.Empty<Id8LeaderboardRecord>(),
                new[] { Entry("nope", 0), Entry("also", -5), Entry("yes", 90000) });

            Assert.Equal("YES", Id8Name.Decode(rows[0].RawName));
            Assert.True(rows[1].IsFiller);
        }

        [Fact]
        public void MergedRowsSurviveAWriteReadRoundTrip()
        {
            Id8LeaderboardRecord[] rows = Id8Leaderboard.Merge(
                Array.Empty<Id8LeaderboardRecord>(),
                new[] { Entry("mhytee", 139932) });

            var buf = new byte[Id8LeaderboardRecord.Size * Id8Leaderboard.Ranks];
            for (int i = 0; i < rows.Length; i++) rows[i].Write(buf, i * Id8LeaderboardRecord.Size);

            Id8LeaderboardRecord back = Id8LeaderboardRecord.Read(buf, 0);
            Assert.Equal("MHYTEE", Id8Name.Decode(back.RawName));
            Assert.Equal(139932, back.GoalMs);
            Assert.Equal(Id8LeaderboardRecord.FlagReal, back.Flags);
        }

        [Fact]
        public void BoardOffsetsAreTheOnesTheGameUses()
        {
            Assert.Equal(0xac, (int)Id8Board.ShopPerCar);
            Assert.Equal(0xb0, (int)Id8Board.ShopTopTen);
            Assert.Equal(0xb4, (int)Id8Board.OnlinePerCar);
            Assert.Equal(0xb8, (int)Id8Board.OnlineTopTen);
        }

        [Fact]
        public void APlayersOwnLapSurvivesBeingWrittenBackToTheBoard()
        {
            // The game stamps a card id on a record it sets, and IsOurs reads a zero there as our
            // signature. We put their record back every time the board is rebuilt, and stamping our
            // own zero over it disowned it: the next rebuild read the row as one of ours and
            // dropped it, so a lap set mid-session lived for exactly one rewrite. Under climb mode
            // that is a rewrite every time they walk back to the stage select.
            Id8LeaderboardRecord theirs = Id8LeaderboardRecord.Read(RealRecord(), 0);
            Assert.Equal(5553014u, theirs.PlayerId);
            Assert.False(Id8Leaderboard.IsOurs(theirs));

            Id8LeaderboardRecord[] board = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Local,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                null, null,
                new[] { theirs });

            Assert.Equal("MHYTEE", Id8Name.Decode(board[0].RawName));
            Assert.Equal(5553014u, board[0].PlayerId);

            // The rebuild after that reads the live board back as its source of local records,
            // exactly as the service does, and has to still find them on it.
            var again = Id8Leaderboard.LocalRecordsFrom(board);
            Assert.Single(again);
            Assert.Equal(theirs.GoalMs, again[0].GoalMs);
        }

        [Fact]
        public void RowsWeInventedAreStillOursToSweep()
        {
            // The other half of the same rule: a community or TeknoParrot row carries no card id,
            // so it must keep coming back as ours or the sweep would leave them behind forever.
            Id8LeaderboardRecord[] board = Id8Leaderboard.BuildBoard(
                Id8BoardSource.TeknoParrot,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                null,
                new[] { Entry("rival", 130000) },
                null);

            Assert.Equal("RIVAL", Id8Name.Decode(board[0].RawName));
            Assert.True(Id8Leaderboard.IsOurs(board[0]));
            Assert.Empty(Id8Leaderboard.LocalRecordsFrom(board));
        }

        [Fact]
        public void TheOwnedCopyWinsWhenTheSameLapArrivesFromTwoPools()
        {
            // Their own row and the copy they submitted are the same lap at the same time. Keeping
            // the submitted one would put a zero id back on the board and lose the ownership again.
            Id8LeaderboardRecord theirs = Id8LeaderboardRecord.Read(RealRecord(), 0);
            var submitted = new Id8LeaderboardEntry
            {
                Username = "MHYTEE", CarId = theirs.CarId, GoalMs = theirs.GoalMs,
            };

            Id8LeaderboardRecord[] board = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Merged,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                new[] { submitted },
                null,
                new[] { theirs });

            Assert.Equal("MHYTEE", Id8Name.Decode(board[0].RawName));
            Assert.Equal(5553014u, board[0].PlayerId);
        }

        // ---- ladder climb: the two layouts of the same ten drivers ----

        /// <summary>A field deep enough that the top ten and the player's neighbourhood cannot
        /// overlap, so a test that passes by accident is not possible. Times ascend with the
        /// number, so P1 is fastest and P100 slowest.</summary>
        private static Id8LeaderboardEntry[] Field(int n = 100) =>
            Enumerable.Range(1, n).Select(i => Entry("P" + i, 100000 + i * 1000)).ToArray();

        [Fact]
        public void TheViewValuesAreThePlacesTheyName()
        {
            // Load bearing: the enum value is passed straight through as placeAt. Renumbering
            // these silently changes the layout, so the numbers are asserted rather than trusted.
            Assert.Equal(5, (int)Id8LadderView.Standings);
            Assert.Equal(2, (int)Id8LadderView.Target);
        }

        [Fact]
        public void StandingsPutsThePlayerFifthWithFourAboveAndFiveBelow()
        {
            var w = Id8Leaderboard.LadderWindow(Field(), "P20", 10, (int)Id8LadderView.Standings);

            Assert.Equal(10, w.Count);
            Assert.Equal("P20", w[4].Username);          // fifth
            Assert.Equal("P16", w[0].Username);          // four faster above
            Assert.Equal("P25", w[9].Username);          // five slower below
        }

        [Fact]
        public void TargetPutsTheNextDriverUpFirstAndThePlayerSecond()
        {
            var w = Id8Leaderboard.LadderWindow(Field(), "P20", 10, (int)Id8LadderView.Target);

            Assert.Equal(10, w.Count);
            Assert.Equal("P19", w[0].Username);          // the rung they are chasing, and the
            Assert.Equal("P20", w[1].Username);          // time the game copies into the HUD
        }

        [Fact]
        public void TheTargetIsTheOnlyThingBetweenThePlayerAndTheTopRow()
        {
            // The whole point of the layout: whatever the game reads off row one is one place
            // better than the player, never two and never a world record.
            // Including the very bottom, where keeping the board full would have slid the window
            // up and sent them after somebody nine places away.
            foreach (int place in new[] { 2, 3, 17, 64, 99, 100 })
            {
                var w = Id8Leaderboard.LadderWindow(Field(), "P" + place, 10, (int)Id8LadderView.Target,
                                                    keepBoardFull: false);
                Assert.Equal("P" + (place - 1), w[0].Username);
                Assert.Equal("P" + place, w[1].Username);
            }
        }

        [Fact]
        public void NeitherViewEverShowsAPlayerBelowTheirRealPlace()
        {
            // Genuinely third is shown third, not dropped to fifth to make the window symmetric.
            var standings = Id8Leaderboard.LadderWindow(Field(), "P3", 10, (int)Id8LadderView.Standings);
            Assert.Equal("P3", standings[2].Username);
            Assert.Equal("P1", standings[0].Username);

            // And first is first in both, because there is no rung above them to promote.
            foreach (Id8LadderView v in new[] { Id8LadderView.Standings, Id8LadderView.Target })
            {
                var w = Id8Leaderboard.LadderWindow(Field(), "P1", 10, (int)v);
                Assert.Equal("P1", w[0].Username);
            }
        }

        [Fact]
        public void StandingsStaysFullAtTheBottomOfTheField()
        {
            // Last of a hundred cannot have five below them, so the reading board slides up rather
            // than trailing off into filler. Still above their real place, which is the only rule.
            var w = Id8Leaderboard.LadderWindow(Field(), "P100", 10, (int)Id8LadderView.Standings);

            Assert.Equal(10, w.Count);
            Assert.Equal("P100", w[9].Username);
            Assert.Equal("P91", w[0].Username);
        }

        [Fact]
        public void TargetGivesUpAFullBoardRatherThanTheRightTargetRow()
        {
            // Two real rows and eight filler beats ten real rows aimed at the wrong driver: the
            // top row is the time the game puts in front of them.
            var w = Id8Leaderboard.LadderWindow(Field(), "P100", 10, (int)Id8LadderView.Target,
                                                keepBoardFull: false);

            Assert.Equal(2, w.Count);
            Assert.Equal("P99", w[0].Username);
            Assert.Equal("P100", w[1].Username);
        }

        [Fact]
        public void BuildBoardKeepsTheRightTargetForAPlayerAtTheBottom()
        {
            // End to end, because the per-view clamp is chosen inside BuildBoard, not by the caller.
            Id8LeaderboardRecord[] rows = Id8Leaderboard.BuildBoard(
                Id8BoardSource.TeknoParrot,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                null, Field(), null, "P100", Id8LadderView.Target);

            Assert.Equal(10, rows.Length);
            Assert.Equal("P99", Id8Name.Decode(rows[0].RawName));
            Assert.Equal("P100", Id8Name.Decode(rows[1].RawName));
            Assert.True(rows[2].IsFiller);
        }

        [Fact]
        public void ACourseThePlayerHasNeverDrivenShowsTheFastestTimes()
        {
            foreach (Id8LadderView v in new[] { Id8LadderView.Standings, Id8LadderView.Target })
            {
                var w = Id8Leaderboard.LadderWindow(Field(), "NOBODY", 10, (int)v);
                Assert.Equal("P1", w[0].Username);
                Assert.Equal("P10", w[9].Username);
            }
        }

        [Fact]
        public void BuildBoardWritesTheTargetLayoutStraightOntoTheBoard()
        {
            // End to end through the real build path, because the window being right is not the
            // same as the rows the game will read being right.
            Id8LeaderboardRecord[] rows = Id8Leaderboard.BuildBoard(
                Id8BoardSource.TeknoParrot,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                null,
                Field(),
                null,
                "P20",
                Id8LadderView.Target);

            Assert.Equal("P19", Id8Name.Decode(rows[0].RawName));
            Assert.Equal("P20", Id8Name.Decode(rows[1].RawName));
        }

        [Fact]
        public void BuildBoardDefaultsToStandingsWhenNoViewIsNamed()
        {
            Id8LeaderboardRecord[] rows = Id8Leaderboard.BuildBoard(
                Id8BoardSource.TeknoParrot,
                new[] { Id8LeaderboardRecord.Read(FillerRecord(), 0) },
                null,
                Field(),
                null,
                "P20");

            Assert.Equal("P20", Id8Name.Decode(rows[4].RawName));
        }
    }
}
