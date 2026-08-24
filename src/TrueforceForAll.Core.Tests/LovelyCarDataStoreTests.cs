// Cache behaviour for the lovely-car-data store. The fetcher is injected, so
// none of this touches the network; what is under test is the offline-first
// contract, and above all that a FAILED fetch never destroys a good cached copy.

using System;
using System.IO;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LovelyCarDataStoreTests : IDisposable
    {
        private readonly string _dir;

        public LovelyCarDataStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tf4all-lovely-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private const string Car = @"{""carName"":""Test Car"",""ledNumber"":4,
            ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFF0000""],
            ""ledRpm"":[{""1"":[7000,5000,5500,6000,6500]}]}";

        private const string OtherCar = @"{""carName"":""Replacement"",""ledNumber"":4,
            ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFF0000""],
            ""ledRpm"":[{""1"":[8000,6000,6500,7000,7500]}]}";

        private LovelyCarDataStore NewStore(Func<string, string> fetch)
            => new LovelyCarDataStore(_dir) { Fetch = fetch };

        [Fact]
        public void FetchesAndCachesOnFirstAsk()
        {
            int calls = 0;
            var store = NewStore(_ => { calls++; return Car; });

            var p = store.Refresh("iRacing", "testcar");
            Assert.NotNull(p);
            Assert.Equal("Test Car", p.CarName);
            Assert.Equal(1, calls);
            Assert.True(File.Exists(Path.Combine(_dir, "iracing", "testcar.json")));
        }

        [Fact]
        public void DerivesThePathFromSimHubIdsWithoutAMappingTable()
        {
            string asked = null;
            var store = NewStore(rel => { asked = rel; return Car; });
            store.Refresh("Assetto Corsa", "ks_audi_r8");
            Assert.Equal("assetto-corsa/ks-audi-r8.json", asked);
        }

        [Fact]
        public void ServesTheCacheWithoutRefetchingWhileFresh()
        {
            int calls = 0;
            var store = NewStore(_ => { calls++; return Car; });
            store.Refresh("iRacing", "testcar");
            store.ForgetMemory();
            store.Refresh("iRacing", "testcar");
            Assert.Equal(1, calls);     // second ask served from disk
        }

        [Fact]
        public void RefetchesOnceTheCacheGoesStale()
        {
            int calls = 0;
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = NewStore(_ => { calls++; return Car; });
            store.UtcNow = () => now;

            store.Refresh("iRacing", "testcar");
            Assert.Equal(1, calls);

            now = now.AddDays(8);       // past the 7-day TTL
            store.ForgetMemory();
            store.Refresh("iRacing", "testcar");
            Assert.Equal(2, calls);
        }

        [Fact]
        public void AFailedFetchNeverDestroysAGoodCachedCopy()
        {
            // The whole reason the store exists in this shape.
            var store = NewStore(_ => Car);
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            store.UtcNow = () => now;
            store.Refresh("iRacing", "testcar");

            now = now.AddDays(30);                 // stale, so it will try
            store.Fetch = _ => null;               // and the network is down
            store.ForgetMemory();

            var p = store.Refresh("iRacing", "testcar");
            Assert.NotNull(p);
            Assert.Equal("Test Car", p.CarName);   // still the cached car
            Assert.True(File.Exists(Path.Combine(_dir, "iracing", "testcar.json")));
        }

        [Fact]
        public void ReplacesTheCachedCopyWhenAFetchSucceeds()
        {
            var store = NewStore(_ => Car);
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            store.UtcNow = () => now;
            store.Refresh("iRacing", "testcar");

            now = now.AddDays(30);
            store.Fetch = _ => OtherCar;
            store.ForgetMemory();

            Assert.Equal("Replacement", store.Refresh("iRacing", "testcar").CarName);
        }

        [Fact]
        public void RemembersThatACarIsNotInTheDataset()
        {
            int calls = 0;
            var store = NewStore(_ => { calls++; return string.Empty; });   // 404

            Assert.Null(store.Refresh("Forza", "car_123"));
            store.ForgetMemory();
            Assert.Null(store.Refresh("Forza", "car_123"));

            // Asked once, not every session: the negative answer is cached too.
            Assert.Equal(1, calls);
            Assert.Contains("car-123.json", File.ReadAllText(Path.Combine(_dir, "cache-index.json")));
        }

        [Fact]
        public void ANegativeAnswerClearsAStaleCachedCar()
        {
            var store = NewStore(_ => Car);
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            store.UtcNow = () => now;
            store.Refresh("iRacing", "testcar");

            now = now.AddDays(30);
            store.Fetch = _ => string.Empty;        // upstream removed it
            store.ForgetMemory();

            Assert.Null(store.Refresh("iRacing", "testcar"));
            Assert.False(File.Exists(Path.Combine(_dir, "iracing", "testcar.json")));
        }

        [Fact]
        public void AClockThatMovesBackwardsCostsOneRefetchRatherThanFreezingTheEntry()
        {
            // Regression guard. Freshness once compared our clock against the
            // filesystem's modified time, so any backwards clock move (NTP
            // correction, timezone repair, dual boot) left the entry looking
            // fresh forever and the car never updated again.
            int calls = 0;
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = NewStore(_ => { calls++; return Car; });
            store.UtcNow = () => now;

            store.Refresh("iRacing", "testcar");
            Assert.Equal(1, calls);

            now = now.AddYears(-1);          // clock jumps into the past
            store.ForgetMemory();
            store.Refresh("iRacing", "testcar");
            Assert.Equal(2, calls);          // refetched instead of freezing
        }

        [Fact]
        public void RefetchesWhenTheCachedFileWasDeletedBehindOurBack()
        {
            int calls = 0;
            var store = NewStore(_ => { calls++; return Car; });
            store.Refresh("iRacing", "testcar");

            File.Delete(Path.Combine(_dir, "iracing", "testcar.json"));
            store.ForgetMemory();

            Assert.NotNull(store.Refresh("iRacing", "testcar"));
            Assert.Equal(2, calls);
        }

        [Fact]
        public void SurvivesACorruptCacheIndex()
        {
            File.WriteAllText(Path.Combine(_dir, "cache-index.json"), "{ not json");
            var store = NewStore(_ => Car);
            Assert.NotNull(store.Refresh("iRacing", "testcar"));
        }

        [Fact]
        public void GetCachedNeverTouchesTheNetwork()
        {
            var store = NewStore(_ => throw new InvalidOperationException("must not fetch"));
            Assert.Null(store.GetCached("iRacing", "testcar"));   // nothing cached, no fetch
        }

        [Fact]
        public void GetCachedServesWhatRefreshStored()
        {
            var store = NewStore(_ => Car);
            store.Refresh("iRacing", "testcar");
            store.ForgetMemory();
            Assert.Equal("Test Car", store.GetCached("iRacing", "testcar").CarName);
        }

        [Fact]
        public void SurvivesAFetcherThatThrows()
        {
            var store = NewStore(_ => throw new TimeoutException("network"));
            Assert.Null(store.Refresh("iRacing", "testcar"));
        }

        [Fact]
        public void SurvivesACorruptCacheFile()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "iracing"));
            File.WriteAllText(Path.Combine(_dir, "iracing", "testcar.json"), "{ truncated");
            var store = NewStore(_ => null);
            Assert.Null(store.GetCached("iRacing", "testcar"));
        }

        [Theory]
        [InlineData(null, "car")]
        [InlineData("game", null)]
        [InlineData("", "")]
        [InlineData("!!!", "!!!")]
        public void IgnoresIdsThatCannotAddressAFile(string game, string car)
        {
            var store = NewStore(_ => Car);
            Assert.Null(store.Refresh(game, car));
            Assert.Null(store.GetCached(game, car));
        }

        [Fact]
        public void CannotBeTalkedIntoWritingOutsideItsCacheFolder()
        {
            // The cleaner already strips separators and dots, so this is a
            // regression guard on that guarantee rather than a live hole.
            var store = NewStore(_ => Car);
            store.Refresh("../../etc", "../../passwd");
            Assert.False(File.Exists(Path.Combine(_dir, "..", "..", "passwd.json")));
        }
    }
}
