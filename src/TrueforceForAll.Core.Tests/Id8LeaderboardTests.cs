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
    }
}
