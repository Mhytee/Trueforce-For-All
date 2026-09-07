// Which pool fills which board, and what happens when both do.
//
// The interesting case is Merged: a player who has submitted to tf4all AND to TeknoParrot must
// appear once, at their better time, or the board shows the same person twice and the ranking
// below them is wrong by one place.

using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class Id8BoardSourceTests
    {
        private static Id8LeaderboardEntry E(string name, int ms) =>
            new Id8LeaderboardEntry { Username = name, GoalMs = ms };

        private static readonly List<Id8LeaderboardEntry> Community = new List<Id8LeaderboardEntry>
        {
            E("Mhytee", 131000), E("Rival1", 128500),
        };

        private static readonly List<Id8LeaderboardEntry> Tekno = new List<Id8LeaderboardEntry>
        {
            E("Rival2", 121000), E("Mhytee", 135000), E("Rival3", 140000),
        };

        [Fact]
        public void CommunityOnlyIgnoresTheOtherPool()
        {
            var r = Id8Leaderboard.Combine(Id8BoardSource.Community, Community, Tekno);
            Assert.Equal(new[] { "Rival1", "Mhytee" }, r.Select(x => x.Username));
        }

        [Fact]
        public void TeknoParrotOnlyIgnoresTheOtherPool()
        {
            var r = Id8Leaderboard.Combine(Id8BoardSource.TeknoParrot, Community, Tekno);
            Assert.Equal(new[] { "Rival2", "Mhytee", "Rival3" }, r.Select(x => x.Username));
            Assert.Equal(135000, r.First(x => x.Username == "Mhytee").GoalMs);
        }

        /// <summary>The point of the whole feature: both pools ranked together.</summary>
        [Fact]
        public void MergedRanksBothPoolsTogether()
        {
            var r = Id8Leaderboard.Combine(Id8BoardSource.Merged, Community, Tekno);
            Assert.Equal(new[] { "Rival2", "Rival1", "Mhytee", "Rival3" }, r.Select(x => x.Username));
        }

        /// <summary>Mhytee is in both pools, 131000 in the community and 135000 on the site. One
        /// row, at the faster time, or everyone below is ranked one place too low.</summary>
        [Fact]
        public void SomeoneInBothPoolsAppearsOnceAtTheirBest()
        {
            var r = Id8Leaderboard.Combine(Id8BoardSource.Merged, Community, Tekno);
            Assert.Single(r.Where(x => x.Username == "Mhytee"));
            Assert.Equal(131000, r.First(x => x.Username == "Mhytee").GoalMs);
        }

        /// <summary>Dedupe is on the name as DISPLAYED. Two entries that sanitize the same would
        /// render identically on a nine-character board, so they must not both be shown.</summary>
        [Fact]
        public void DeduplicationUsesTheDisplayedName()
        {
            var a = new List<Id8LeaderboardEntry> { E("drift_king", 130000) };
            var b = new List<Id8LeaderboardEntry> { E("Drift_King", 125000) };
            var r = Id8Leaderboard.Combine(Id8BoardSource.Merged, a, b);
            Assert.Single(r);
            Assert.Equal(125000, r[0].GoalMs);
        }

        /// <summary>Local pulls in no external rows: it is expressed entirely by keeping what
        /// was already on the board.</summary>
        [Fact]
        public void LocalContributesNoExternalRows()
        {
            Assert.Empty(Id8Leaderboard.Combine(Id8BoardSource.Local, Community, Tekno));
        }

        [Fact]
        public void NeverReturnsMoreThanTheBoardHolds()
        {
            var many = Enumerable.Range(1, 40).Select(i => E("P" + i, 100000 + i)).ToList();
            Assert.Equal(Id8Leaderboard.Ranks, Id8Leaderboard.Combine(Id8BoardSource.Merged, many, many).Count);
        }

        [Fact]
        public void MissingPoolIsNotAnError()
        {
            var r = Id8Leaderboard.Combine(Id8BoardSource.Merged, null, Tekno);
            Assert.Equal(3, r.Count);
            Assert.Empty(Id8Leaderboard.Combine(Id8BoardSource.Merged, null, null));
        }

        /// <summary>A row with no time is not a row. Writing a zero would sort it to the top.</summary>
        [Fact]
        public void EntriesWithNoTimeAreDropped()
        {
            var junk = new List<Id8LeaderboardEntry> { E("Ghost", 0), E("Real", 130000) };
            var r = Id8Leaderboard.Combine(Id8BoardSource.Community, junk, null);
            Assert.Equal(new[] { "Real" }, r.Select(x => x.Username));
        }
    
        // ---- Local, and what each source does with rows already on the board ----

        private static Id8LeaderboardRecord Real(string name, int ms) => new Id8LeaderboardRecord
        {
            RawName = Id8Name.Encode(Id8Name.Sanitize(name)),
            Reserved = new byte[] { 0, 0, Id8LeaderboardRecord.ConstantAt16 },
            Flags = Id8LeaderboardRecord.FlagReal,
            GoalMs = ms,
        };

        private static List<Id8LeaderboardRecord> ExistingBoard() => new List<Id8LeaderboardRecord>
        {
            Real("LocalGuy", 133000),
            Id8Leaderboard.DefaultRow(),
            Id8Leaderboard.DefaultRow(),
        };

        [Theory]
        [InlineData(Id8BoardSource.Local, true)]
        [InlineData(Id8BoardSource.Merged, true)]
        [InlineData(Id8BoardSource.Community, false)]
        [InlineData(Id8BoardSource.TeknoParrot, false)]
        public void OnlyLocalAndMergedKeepWhatWasAlreadyThere(Id8BoardSource source, bool keeps)
        {
            Assert.Equal(keeps, Id8Leaderboard.KeepsLocalRecords(source));
        }

        /// <summary>Local writes nothing new. It clears SEGA's filler out from under the times
        /// somebody actually set, which needs neither an account nor a network.</summary>
        [Fact]
        public void LocalKeepsTheExistingRecordAndAddsNothing()
        {
            var rows = Id8Leaderboard.BuildBoard(Id8BoardSource.Local, ExistingBoard(), Community, Tekno);
            Assert.Equal(Id8Leaderboard.Ranks, rows.Length);
            Assert.Equal("LOCALGUY", Id8Name.Decode(rows[0].RawName));
            Assert.Equal(133000, rows[0].GoalMs);
            Assert.All(rows.Skip(1), r => Assert.True(r.IsFiller));
        }

        /// <summary>Community has to mean strictly tf4all. Mixing the player's own local record in
        /// would make the board answer a different question from the one they asked.</summary>
        [Fact]
        public void CommunityDoesNotSmuggleInTheLocalRecord()
        {
            var rows = Id8Leaderboard.BuildBoard(Id8BoardSource.Community, ExistingBoard(), Community, Tekno);
            var names = rows.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)).ToList();
            Assert.DoesNotContain("LOCALGUY", names);
            Assert.Equal(new[] { "RIVAL1", "MHYTEE" }, names);
        }

        /// <summary>Merged is everything: both pools and the local record, ranked together.</summary>
        [Fact]
        public void MergedIncludesTheLocalRecordAlongsideBothPools()
        {
            var rows = Id8Leaderboard.BuildBoard(Id8BoardSource.Merged, ExistingBoard(), Community, Tekno);
            var names = rows.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)).ToList();
            Assert.Equal(new[] { "RIVAL2", "RIVAL1", "MHYTEE", "LOCALGUY", "RIVAL3" }, names);
        }

        /// <summary>A board is always exactly ten rows, topped up with filler, whatever the source.
        /// Writing fewer would leave whatever was in the tail of the old board showing.</summary>
        [Theory]
        [InlineData(Id8BoardSource.Local)]
        [InlineData(Id8BoardSource.Community)]
        [InlineData(Id8BoardSource.TeknoParrot)]
        [InlineData(Id8BoardSource.Merged)]
        public void EveryBoardIsAlwaysExactlyTenRows(Id8BoardSource source)
        {
            Assert.Equal(Id8Leaderboard.Ranks,
                Id8Leaderboard.BuildBoard(source, ExistingBoard(), Community, Tekno).Length);
        }
    
        // ---- local records fed into the ONLINE board ----
        //
        // The online board is SEGA filler on every row, so it has no local records of its own.
        // Merged there has to be given the player's rows from the SHOP snapshot or it silently
        // omits them, which is not what "merged" means.

        private static List<Id8LeaderboardRecord> OnlineBoardAsShipped() =>
            Enumerable.Range(0, 10).Select(_ => Id8Leaderboard.DefaultRow()).ToList();

        [Fact]
        public void LocalRecordsFromDropsFillerAndKeepsRealRows()
        {
            var snap = new List<Id8LeaderboardRecord>
            {
                Real("LocalGuy", 133000), Id8Leaderboard.DefaultRow(), Real("Someone", 145000),
            };
            var local = Id8Leaderboard.LocalRecordsFrom(snap);
            Assert.Equal(2, local.Count);
            Assert.All(local, r => Assert.False(r.IsFiller));
        }

        [Fact]
        public void LocalRecordsFromToleratesNothing()
        {
            Assert.Empty(Id8Leaderboard.LocalRecordsFrom(null));
            Assert.Empty(Id8Leaderboard.LocalRecordsFrom(new List<Id8LeaderboardRecord>()));
        }

        /// <summary>The bug this parameter exists to prevent: without the shop snapshot, Merged on
        /// the online board leaves the player off their own leaderboard.</summary>
        [Fact]
        public void MergedOnTheOnlineBoardIncludesTheShopSnapshotsLocalRecords()
        {
            var local = Id8Leaderboard.LocalRecordsFrom(new List<Id8LeaderboardRecord> { Real("LocalGuy", 133000) });

            var without = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Merged, OnlineBoardAsShipped(), Community, Tekno);
            Assert.DoesNotContain("LOCALGUY",
                without.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)));

            var with = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Merged, OnlineBoardAsShipped(), Community, Tekno, local);
            Assert.Equal(new[] { "RIVAL2", "RIVAL1", "MHYTEE", "LOCALGUY", "RIVAL3" },
                with.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)));
        }

        /// <summary>A local record must not create a second row for someone already in a pool.</summary>
        [Fact]
        public void ALocalRecordForSomeoneAlreadyInAPoolCollapsesToTheirBest()
        {
            var local = Id8Leaderboard.LocalRecordsFrom(new List<Id8LeaderboardRecord> { Real("Mhytee", 129000) });
            var rows = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Merged, OnlineBoardAsShipped(), Community, Tekno, local);

            var names = rows.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)).ToList();
            Assert.Single(names.Where(n => n == "MHYTEE"));
            // 129000 local beats the 131000 community row.
            Assert.Equal(129000, rows.First(r => Id8Name.Decode(r.RawName) == "MHYTEE").GoalMs);
        }

        /// <summary>Community means tf4all. Handing it local records must not change that.</summary>
        [Fact]
        public void CommunityIgnoresLocalRecordsEvenWhenGivenThem()
        {
            var local = Id8Leaderboard.LocalRecordsFrom(new List<Id8LeaderboardRecord> { Real("LocalGuy", 100000) });
            var rows = Id8Leaderboard.BuildBoard(
                Id8BoardSource.Community, OnlineBoardAsShipped(), Community, Tekno, local);
            Assert.DoesNotContain("LOCALGUY",
                rows.Where(r => !r.IsFiller).Select(r => Id8Name.Decode(r.RawName)));
        }
    
        // ---- filler rows carry our name, and are still filler ----

        /// <summary>The whole risk of naming the filler rows: if one stopped counting as filler it
        /// would be treated as somebody's real record. It would survive as a "local record", it
        /// would occupy a rank on a merged board, and the per-car guard that refuses to replace a
        /// real time with a slower one would refuse to overwrite it. A six minute placeholder would
        /// then sit on the board permanently, above nothing and below everyone.</summary>
        [Fact]
        public void ARenamedFillerRowIsStillFiller()
        {
            Id8LeaderboardRecord filler = Id8Leaderboard.DefaultRow();
            Assert.True(filler.IsFiller);
            Assert.Equal(Id8LeaderboardRecord.FlagDefault, filler.Flags);
            Assert.Equal(Id8LeaderboardRecord.DefaultGoalMs, filler.GoalMs);
        }

        [Fact]
        public void FillerRowsCarryOurName()
        {
            Assert.Equal("TF4ALL", Id8Name.Decode(Id8Leaderboard.DefaultRow().RawName));
            Assert.Equal(Id8Leaderboard.FillerName, Id8Name.Decode(Id8Leaderboard.DefaultRow().RawName));
        }

        /// <summary>A board we filled and then read back must not treat our own filler as the
        /// player's records. This is the path a second fill takes.</summary>
        [Fact]
        public void OurFillerIsNotMistakenForALocalRecordOnARefill()
        {
            var boardWeWrote = new List<Id8LeaderboardRecord> { Real("LocalGuy", 133000) };
            for (int i = 0; i < 9; i++) boardWeWrote.Add(Id8Leaderboard.DefaultRow());

            var local = Id8Leaderboard.LocalRecordsFrom(boardWeWrote);
            Assert.Single(local);
            Assert.Equal("LOCALGUY", Id8Name.Decode(local[0].RawName));
        }

        /// <summary>The name has to fit the field, or it would be truncated on screen.</summary>
        [Fact]
        public void TheFillerNameFitsTheRecord()
        {
            byte[] enc = Id8Name.Encode(Id8Leaderboard.FillerName);
            Assert.NotNull(enc);
            Assert.True(enc.Length < Id8LeaderboardRecord.NameBytes);
        }
    }
}
