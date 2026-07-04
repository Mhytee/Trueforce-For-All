using System;
using System.IO;
using System.Linq;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Unit tests for InstalledPacksStore: the pure merge/dedup logic behind the
    // Library -> Packs registry. Covers the behavior this work introduced:
    //   - AddOrMergePack folds a repeat/partial community download into one row
    //     (keyed by CommunitySourceId) instead of duplicating it,
    //   - entry identity (game by name, car by carId+name, engine by id),
    //   - AddPack (disk import) always appends,
    //   - round-trip persistence.
    // The store is link-compiled into this test assembly (see csproj); it has no
    // SimHub/WPF dependencies, so it runs as a plain unit test.
    public sealed class InstalledPacksStoreTests : IDisposable
    {
        private readonly string _dir;

        public InstalledPacksStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tffa-packtests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }

        private InstalledPacksStore NewStore() => new InstalledPacksStore(() => _dir);

        private static InstalledPackEntry Game(string name, string hash = "h") =>
            new InstalledPackEntry { Kind = InstalledPackEntry.KindGame, Name = name, BaselineHash = hash };

        private static InstalledPackEntry Car(string carId, string preset, string hash = "h") =>
            new InstalledPackEntry { Kind = InstalledPackEntry.KindCar, CarId = carId, PresetName = preset, BaselineHash = hash };

        private static InstalledPackEntry Engine(string id, string name = "e", string hash = "h") =>
            new InstalledPackEntry { Kind = InstalledPackEntry.KindEngine, EngineId = id, Name = name, BaselineHash = hash };

        private static InstalledPack Pack(string name, string sourceId, params InstalledPackEntry[] entries) =>
            new InstalledPack
            {
                PackName = name,
                CommunitySourceId = sourceId,
                Entries = entries.ToList(),
            };

        [Fact]
        public void AddOrMergePack_NewPack_AddsOneRecord()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("A", "src-A", Game("GP1"), Engine("eng-1")));

            var packs = store.Load().Packs;
            Assert.Single(packs);
            Assert.Equal("A", packs[0].PackName);
            Assert.Equal(2, packs[0].Entries.Count);
        }

        [Fact]
        public void AddOrMergePack_EmptyEntries_IsNoOp()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("Empty", "src-empty")); // no entries
            store.AddOrMergePack(null);

            Assert.Empty(store.Load().Packs);
        }

        [Fact]
        public void AddOrMergePack_SameSourceId_MergesIntoOneRow()
        {
            var store = NewStore();
            // Partial download #1: two of the pack's entries.
            store.AddOrMergePack(Pack("Shared", "src-1", Game("GP1"), Engine("eng-1")));
            // Partial download #2: the rest, same source id.
            store.AddOrMergePack(Pack("Shared", "src-1", Car("car_x", "Setup"), Engine("eng-2")));

            var packs = store.Load().Packs;
            Assert.Single(packs);                      // one row, not two
            Assert.Equal(4, packs[0].Entries.Count);   // entries unioned
        }

        [Fact]
        public void AddOrMergePack_SameSourceId_DoesNotDuplicateIdenticalEntries()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("Shared", "src-1", Game("GP1"), Engine("eng-1")));
            // Full re-download: same entries again.
            store.AddOrMergePack(Pack("Shared", "src-1", Game("GP1"), Engine("eng-1")));

            var packs = store.Load().Packs;
            Assert.Single(packs);
            Assert.Equal(2, packs[0].Entries.Count);   // no growth
        }

        [Fact]
        public void AddOrMergePack_EntryIdentity_IsKindAware()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("P", "src-1",
                Game("Same"), Car("car", "Same"), Engine("id-same", name: "First")));
            // Re-add entries that collide on identity but differ on other fields:
            // game by Name, car by CarId+PresetName, engine by EngineId. None
            // should be added again.
            store.AddOrMergePack(Pack("P", "src-1",
                Game("Same"), Car("car", "Same"), Engine("id-same", name: "Renamed")));

            var entries = store.Load().Packs.Single().Entries;
            Assert.Equal(3, entries.Count);
            // A genuinely new engine id IS added.
            store.AddOrMergePack(Pack("P", "src-1", Engine("id-different")));
            Assert.Equal(4, store.Load().Packs.Single().Entries.Count);
        }

        [Fact]
        public void AddOrMergePack_DifferentSourceIds_StaySeparate()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("A", "src-A", Game("GP1")));
            store.AddOrMergePack(Pack("B", "src-B", Game("GP2")));

            Assert.Equal(2, store.Load().Packs.Count);
        }

        [Fact]
        public void AddOrMergePack_NullSourceId_AlwaysAdds()
        {
            var store = NewStore();
            // A null/blank source id has no merge key, so each call is its own row
            // (e.g. a manually-built pack with no community provenance).
            store.AddOrMergePack(Pack("X", null, Game("GP1")));
            store.AddOrMergePack(Pack("X", null, Game("GP1")));

            Assert.Equal(2, store.Load().Packs.Count);
        }

        [Fact]
        public void AddPack_AlwaysAppends_EvenSameContent()
        {
            var store = NewStore();
            // Disk import path: each .tfpack import is its own record.
            store.AddPack(new InstalledPack { PackName = "Disk", Entries = { Game("GP1") } });
            store.AddPack(new InstalledPack { PackName = "Disk", Entries = { Game("GP1") } });

            Assert.Equal(2, store.Load().Packs.Count);
        }

        [Fact]
        public void AddOrMergePack_RefreshesMetadataOnMerge()
        {
            var store = NewStore();
            store.AddOrMergePack(new InstalledPack
            {
                PackName = "Old name", Author = "A", CommunitySourceId = "src-1",
                Entries = { Game("GP1") },
            });
            store.AddOrMergePack(new InstalledPack
            {
                PackName = "New name", Author = "B", AuthorVersion = "2.0", CommunitySourceId = "src-1",
                Entries = { Game("GP2") },
            });

            var pack = store.Load().Packs.Single();
            Assert.Equal("New name", pack.PackName);
            Assert.Equal("B", pack.Author);
            Assert.Equal("2.0", pack.AuthorVersion);
        }

        [Fact]
        public void Find_ResolvesGameAndCarEntries()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("P", "src-1", Game("MyGame"), Car("car_x", "MyCar")));

            Assert.Equal("P", store.FindPackForGame("MyGame")?.PackName);
            Assert.Equal("P", store.FindPackForCar("car_x", "MyCar")?.PackName);
            Assert.Null(store.FindPackForGame("nope"));
            Assert.Null(store.FindPackForCar("car_x", "nope"));
        }

        [Fact]
        public void Persistence_SurvivesNewStoreInstance()
        {
            NewStore().AddOrMergePack(Pack("A", "src-A", Game("GP1"), Engine("eng-1")));

            // Fresh instance reads from disk (no shared in-memory cache).
            var reloaded = NewStore().Load().Packs;
            Assert.Single(reloaded);
            Assert.Equal("A", reloaded[0].PackName);
            Assert.Equal("src-A", reloaded[0].CommunitySourceId);
            Assert.Equal(2, reloaded[0].Entries.Count);
            Assert.Contains(reloaded[0].Entries, e => e.Kind == InstalledPackEntry.KindEngine && e.EngineId == "eng-1");
        }

        [Fact]
        public void RemovePack_DropsRecord()
        {
            var store = NewStore();
            store.AddOrMergePack(Pack("A", "src-A", Game("GP1")));
            var pack = store.Load().Packs.Single();

            store.RemovePack(pack);
            Assert.Empty(store.Load().Packs);
        }
    }
}
