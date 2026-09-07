// Cache policy for somebody else's server.
//
// The failure behaviour is the part worth pinning: a week-old leaderboard beats an empty one, and
// the player is offline either way. These tests exist because that rule is easy to regress into
// "clear the cache on error", which is the exact wrong move.

using System;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class TeknoParrotCacheTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;
        private DateTime _now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        public TeknoParrotCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tp-cache-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "boards.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>Markup copied from the live page, trimmed to one row. Faithful on purpose:
        /// a fixture invented from memory tests the fixture, not the parser.</summary>
        private static string Page(string player, string time) =>
            "<table><tbody id=\"track-records-Akina_Downhill\">" +
            "<tr>" +
            "<td class=\"rank-col\">1</td>" +
            "<td class=\"name-col\"><a class=\"btn highscore-player-link\" href=\"/en/ProfileViewer/Index/" + player + "\">" + player + "</a></td>" +
            "<td class=\"detail-col\">MAZDA RX-7 III (FC3S)</td>" +
            "<td class=\"time-col\">" + time + "</td>" +
            "<td class=\"date-col\"><span class=\"date-single-line d-none d-md-inline\">8/6/2024 5:47:17 PM</span></td>" +
            "<td class=\"action-col\"><a href=\"/en/Highscore/EntrySpecific?gameId=ID8&amp;entryId=2746\">x</a></td>" +
            "</tr>" +
            "</tbody></table>";

        private TeknoParrotLeaderboardCache Make(Func<string, string> fetch) =>
            new TeknoParrotLeaderboardCache(_path, fetch, () => _now);

        [Fact]
        public void FetchesWhenThereIsNoCacheAndStoresIt()
        {
            int calls = 0;
            var c = Make(_ => { calls++; return Page("TPRacer1", "2:11:053"); });

            var boards = c.GetBoards("ID8", out var result, out _);
            Assert.Equal(TeknoParrotCacheResult.Fetched, result);
            Assert.Single(boards);
            Assert.Equal(1, calls);
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void SecondCallInsideTheTtlDoesNotHitTheNetwork()
        {
            int calls = 0;
            Func<string, string> fetch = _ => { calls++; return Page("TPRacer1", "2:11:053"); };

            Make(fetch).GetBoards("ID8", out _, out _);
            var boards = Make(fetch).GetBoards("ID8", out var result, out _);

            Assert.Equal(TeknoParrotCacheResult.Fresh, result);
            Assert.Equal(1, calls);
            Assert.Single(boards);
        }

        [Fact]
        public void RefetchesOnceTheCacheIsStale()
        {
            int calls = 0;
            Func<string, string> fetch = _ => { calls++; return Page("TPRacer1", "2:11:053"); };

            Make(fetch).GetBoards("ID8", out _, out _);
            _now = _now.AddHours(25);          // Ttl defaults to 24 h
            Make(fetch).GetBoards("ID8", out var result, out _);

            Assert.Equal(TeknoParrotCacheResult.Fetched, result);
            Assert.Equal(2, calls);
        }

        /// <summary>The rule that matters: a stale board beats no board.</summary>
        [Fact]
        public void UsesAStaleCacheWhenTheFetchFails()
        {
            Make(_ => Page("TPRacer1", "2:11:053")).GetBoards("ID8", out _, out _);
            _now = _now.AddDays(30);

            var boards = Make(_ => throw new InvalidOperationException("no network"))
                .GetBoards("ID8", out var result, out string detail);

            Assert.Equal(TeknoParrotCacheResult.StaleFallback, result);
            Assert.Single(boards);
            Assert.Contains("no network", detail);
        }

        [Fact]
        public void ReturnsEmptyRatherThanThrowingWhenThereIsNothingAtAll()
        {
            var boards = Make(_ => throw new InvalidOperationException("no network"))
                .GetBoards("ID8", out var result, out _);

            Assert.Equal(TeknoParrotCacheResult.Unavailable, result);
            Assert.Empty(boards);
        }

        /// <summary>A 200 that parses to nothing means their markup moved. Overwriting a good cache
        /// with an empty one would turn a site change into a silent loss of every board.</summary>
        [Fact]
        public void AnEmptyParseDoesNotDestroyAGoodCache()
        {
            Make(_ => Page("TPRacer1", "2:11:053")).GetBoards("ID8", out _, out _);
            _now = _now.AddHours(25);

            var boards = Make(_ => "<html><body>redesigned</body></html>")
                .GetBoards("ID8", out var result, out _);

            Assert.Equal(TeknoParrotCacheResult.StaleFallback, result);
            Assert.Single(boards);
        }

        [Fact]
        public void ACorruptCacheIsTreatedAsNoCache()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{ this is not json");

            var boards = Make(_ => Page("TPRacer1", "2:11:053")).GetBoards("ID8", out var result, out _);
            Assert.Equal(TeknoParrotCacheResult.Fetched, result);
            Assert.Single(boards);
        }
    }
}
