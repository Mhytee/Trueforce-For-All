// Safekeeping for a borrowed LIGHTSYNC slot.
//
// This file guards the least reversible thing the plugin does. Writing a slot
// OVERWRITES a preset the user may have spent an evening on in G HUB, it
// persists on the wheel, and it outlives our process. The on-disk backup is the
// only route back. Every test here is really the same assertion: after any
// plausible mishap, the user's own colours are still recoverable.
//
// So the bias throughout is REFUSE RATHER THAN GUESS. A malformed backup must
// come back null, never as plausible-looking bytes, because the caller writes
// what it is given straight onto the wheel.

using System;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LightSlotBackupTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public LightSlotBackupTests()
        {
            _dir  = Path.Combine(Path.GetTempPath(), "tf4all-slotbackup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "lightsync-slot-backup.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private LightSlotBackupStore Store() => new LightSlotBackupStore(_path);

        private static byte[] Rgb(byte seed)
        {
            var a = new byte[30];
            for (int i = 0; i < 30; i++) a[i] = (byte)(seed + i);
            return a;
        }

        private static WheelLedChannel.WheelLedSlot Slot(byte n, byte seed = 0x10, byte dir = 1)
            => new WheelLedChannel.WheelLedSlot { Slot = n, DirectionWire = dir, Rgb = Rgb(seed) };

        private static Dictionary<string, LightSlotBackupEntry> Map(
            params KeyValuePair<string, LightSlotBackupEntry>[] entries)
        {
            var m = new Dictionary<string, LightSlotBackupEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) m[e.Key] = e.Value;
            return m;
        }

        private static KeyValuePair<string, LightSlotBackupEntry> Entry(
            string wheel, byte slot, byte seed = 0x10, bool restored = false)
        {
            var e = LightSlotBackupStore.FromSlot(Slot(slot, seed), wheel, new DateTime(2026, 8, 24));
            e.Restored = restored;
            return new KeyValuePair<string, LightSlotBackupEntry>(
                LightSlotBackupStore.KeyFor(wheel, slot), e);
        }

        // ---------------- the round trip ----------------
        //
        // The whole feature in one assertion: what we read off the wheel is what
        // goes back onto it, byte for byte. RestoreSlot verifies its write by
        // comparing bytes, so a single count of drift anywhere in here would make
        // every restore report failure on a wheel that restored perfectly.

        [Fact]
        public void ASlotSurvivesTheTripToDiskAndBackUnchanged()
        {
            var original = Slot(3, seed: 0x21, dir: 1);
            var store = Store();

            Assert.True(store.Save(Map(new KeyValuePair<string, LightSlotBackupEntry>(
                LightSlotBackupStore.KeyFor("G PRO", 3),
                LightSlotBackupStore.FromSlot(original, "G PRO", DateTime.UtcNow)))));

            string key;
            var found = LightSlotBackupStore.Find(Store().Load(), "G PRO", 3, out key);
            var back = LightSlotBackupStore.ToSlot(found);

            Assert.Equal(original.Slot, back.Slot);
            Assert.Equal(original.DirectionWire, back.DirectionWire);
            Assert.Equal(original.Rgb, back.Rgb);
        }

        [Fact]
        public void TheHexIsTheColoursInOrderSoAPersonCanReadTheFile()
        {
            // The format is a deliberate choice: the file is the last copy of
            // someone's colours, and a human must be able to pull them out of it
            // by hand if everything else fails.
            var rgb = new byte[] { 0x00, 0xFF, 0x80 };
            Assert.Equal("00FF80", LightSlotBackupStore.ToHex(rgb));
            Assert.Equal(rgb, LightSlotBackupStore.FromHex("00FF80"));
        }

        [Fact]
        public void HexParsingIsCaseInsensitive()
        {
            Assert.Equal(LightSlotBackupStore.FromHex("00ff80"), LightSlotBackupStore.FromHex("00FF80"));
        }

        [Fact]
        public void FromSlotDoesNotInventAName()
        {
            // The slot's own name is read separately and may legitimately be
            // absent. Inventing one here would rename a user's slot on restore.
            var e = LightSlotBackupStore.FromSlot(Slot(2), "G PRO", DateTime.UtcNow);
            Assert.Null(e.OriginalName);
        }

        [Fact]
        public void AFreshBackupIsNotYetRestored()
        {
            // Restored is the debt flag: false at the next launch means a session
            // died holding the slot and we still owe the user their colours.
            Assert.False(LightSlotBackupStore.FromSlot(Slot(2), "G PRO", DateTime.UtcNow).Restored);
        }

        // ---------------- refusing malformed data ----------------
        //
        // Everything here returns null rather than a best guess. The caller
        // writes what it gets straight to the wheel, so a lenient parse would
        // paint garbage over the very colours this file exists to protect.

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ABC")]          // odd length: one nibble is missing
        [InlineData("ZZ")]           // not hex at all
        [InlineData("00FF8G")]       // one bad character late in the string
        public void MalformedHexIsRefusedRatherThanGuessed(string hex)
        {
            Assert.Null(LightSlotBackupStore.FromHex(hex));
        }

        [Fact]
        public void ToHexOfNothingIsEmptyRatherThanACrash()
        {
            Assert.Equal(string.Empty, LightSlotBackupStore.ToHex(null));
        }

        [Fact]
        public void AnEntryShorterThanTheStripIsRefused()
        {
            // 10 LEDs x 3 bytes. A short buffer means a truncated file, and
            // writing it would light part of the strip and leave the rest dark
            // while reporting success.
            var e = LightSlotBackupStore.FromSlot(Slot(1), "G PRO", DateTime.UtcNow);
            e.RgbHex = LightSlotBackupStore.ToHex(new byte[29]);

            Assert.Null(LightSlotBackupStore.ToSlot(e));
        }

        [Fact]
        public void AnEntryWithUnreadableHexIsRefused()
        {
            var e = LightSlotBackupStore.FromSlot(Slot(1), "G PRO", DateTime.UtcNow);
            e.RgbHex = "not hex";

            Assert.Null(LightSlotBackupStore.ToSlot(e));
        }

        [Fact]
        public void ThereIsNothingToRestoreFromNothing()
        {
            Assert.Null(LightSlotBackupStore.ToSlot(null));
        }

        // ---------------- keys ----------------

        [Fact]
        public void TwoWheelsDoNotShareASlotsBackup()
        {
            // Keyed by wheel as well as slot so swapping bases cannot push one
            // wheel's colours onto another's slot 3.
            Assert.NotEqual(LightSlotBackupStore.KeyFor("G PRO", 3),
                            LightSlotBackupStore.KeyFor("G923", 3));
        }

        [Fact]
        public void AnUnidentifiedWheelStillGetsAStableKey()
        {
            // Discovery can fail to name a wheel. That must still produce a key
            // we can find again, not a null one that loses the backup.
            Assert.Equal(LightSlotBackupStore.KeyFor(null, 3), LightSlotBackupStore.KeyFor(null, 3));
            Assert.NotEmpty(LightSlotBackupStore.KeyFor(null, 3));
        }

        // ---------------- finding what is owed ----------------

        [Fact]
        public void TheRightWheelsBackupWins()
        {
            var map = Map(Entry("G PRO", 3, seed: 0x10), Entry("G923", 3, seed: 0x90));

            string key;
            var found = LightSlotBackupStore.Find(map, "G923", 3, out key);

            Assert.Equal(LightSlotBackupStore.KeyFor("G923", 3), key);
            Assert.Equal(LightSlotBackupStore.ToHex(Rgb(0x90)), found.RgbHex);
        }

        [Fact]
        public void AnOwedBackupIsPaidEvenIfTheWheelNameChanged()
        {
            // The wheel identity is a string, and it can legitimately differ
            // between sessions: a discovery miss, an unreadable USB product name,
            // a future edit to that text. Orphaning the backup would let the next
            // borrow record OUR colours as the original, and the user's real ones
            // would be gone for good. An owed debt is worth paying even when we
            // are less sure which wheel it came from.
            var map = Map(Entry("G PRO (old name)", 3, seed: 0x40));

            string key;
            var found = LightSlotBackupStore.Find(map, "G PRO (new name)", 3, out key);

            Assert.NotNull(found);
            Assert.Equal(LightSlotBackupStore.KeyFor("G PRO (old name)", 3), key);
        }

        [Fact]
        public void TheFallbackReportsTheKeyItActuallyFound()
        {
            // The caller marks the debt paid by writing back at this key. Handing
            // it the key we LOOKED for rather than the one we found would leave
            // the real entry unrestored forever, and every launch would try again.
            var map = Map(Entry("old", 3));

            string key;
            LightSlotBackupStore.Find(map, "new", 3, out key);

            Assert.True(map.ContainsKey(key));
        }

        [Fact]
        public void ASettledLoanIsNotReopenedByTheFallback()
        {
            // Already restored means the user has their colours. Handing this back
            // would overwrite whatever they have chosen since.
            var map = Map(Entry("old name", 3, restored: true));

            string key;
            Assert.Null(LightSlotBackupStore.Find(map, "different name", 3, out key));
        }

        [Fact]
        public void TheFallbackDoesNotBorrowADifferentSlotsColours()
        {
            var map = Map(Entry("old name", 4));

            string key;
            Assert.Null(LightSlotBackupStore.Find(map, "new name", 3, out key));
        }

        [Fact]
        public void AnExactMatchIsHonouredEvenOnceSettled()
        {
            // Asymmetric with the fallback above, deliberately. An exact hit is
            // authoritative: the caller needs to see the settled entry to know the
            // slot is ours to keep rather than a debt still owed.
            var map = Map(Entry("G PRO", 3, restored: true));

            string key;
            Assert.NotNull(LightSlotBackupStore.Find(map, "G PRO", 3, out key));
        }

        [Fact]
        public void NothingBorrowedMeansNothingFound()
        {
            string key;
            Assert.Null(LightSlotBackupStore.Find(
                new Dictionary<string, LightSlotBackupEntry>(), "G PRO", 3, out key));
        }

        [Fact]
        public void AMissingMapIsNotACrash()
        {
            string key;
            Assert.Null(LightSlotBackupStore.Find(null, "G PRO", 3, out key));
            Assert.NotEmpty(key);
        }

        // ---------------- the file itself ----------------

        [Fact]
        public void NoFileMeansNothingIsBorrowed()
        {
            // Absent is a definite answer, and it must NOT read as corrupt: the
            // caller refuses to borrow a slot while it cannot promise a restore.
            var store = Store();

            Assert.Empty(store.Load());
            Assert.False(store.LastLoadCorrupt);
        }

        [Fact]
        public void AnUnreadableFileIsNotMistakenForAnEmptyOne()
        {
            // The distinction the caller acts on. Empty means nothing is owed;
            // unreadable means we do not KNOW, and it must refuse to overwrite a
            // slot it cannot promise to put back.
            File.WriteAllText(_path, "{ this is not json");
            var store = Store();

            store.Load();

            Assert.True(store.LastLoadCorrupt);
        }

        [Fact]
        public void AnUnreadableFileIsSetAsideRatherThanOverwritten()
        {
            // It may be the only copy of the user's colours, and a person can
            // still read the hex out of it by hand.
            File.WriteAllText(_path, "{ this is not json");

            Store().Load();

            Assert.True(File.Exists(_path + ".corrupt"));
        }

        [Fact]
        public void ThePreviousGenerationIsReadWhenTheLiveFileIsUnreadable()
        {
            // Save leaves a .bak behind precisely so a half-written live file is
            // survivable. This is the path that makes that worth doing.
            Store().Save(Map(Entry("G PRO", 3, seed: 0x55)));   // creates the file
            Store().Save(Map(Entry("G PRO", 3, seed: 0x55)));   // rotates it into .bak
            File.WriteAllText(_path, "{ truncated");

            string key;
            var found = LightSlotBackupStore.Find(Store().Load(), "G PRO", 3, out key);

            Assert.NotNull(found);
            Assert.Equal(LightSlotBackupStore.ToHex(Rgb(0x55)), found.RgbHex);
        }

        [Fact]
        public void SavingKeepsThePreviousGeneration()
        {
            var store = Store();
            store.Save(Map(Entry("G PRO", 3, seed: 0x11)));
            store.Save(Map(Entry("G PRO", 3, seed: 0x22)));

            Assert.True(File.Exists(_path + ".bak"));
        }

        [Fact]
        public void ASaveLeavesNoScratchFilesBehind()
        {
            // A fixed temp name once made two concurrent saves fight over one
            // file. The fix was a unique name per write, which only works if each
            // one cleans up after itself.
            var store = Store();
            store.Save(Map(Entry("G PRO", 3)));
            store.Save(Map(Entry("G PRO", 3)));

            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void ASaveWithNowhereToWriteReportsFailure()
        {
            // Callers must not go on to overwrite a wheel slot after a false. The
            // return value is the only thing standing between a failed write and
            // a slot that can never be put back.
            Assert.False(new LightSlotBackupStore(null).Save(Map(Entry("G PRO", 3))));
        }

        [Fact]
        public void KeysSurviveTheFileWithTheirCaseIgnored()
        {
            // Keys embed a wheel identity string we do not control the casing of.
            Store().Save(Map(Entry("G PRO", 3)));

            var loaded = Store().Load();

            Assert.True(loaded.ContainsKey(LightSlotBackupStore.KeyFor("g pro", 3)));
        }

        [Fact]
        public void ARestoredFlagSurvivesTheFile()
        {
            // The debt flag is the one field that MUST persist: losing it means
            // either restoring twice over the user's newer choice, or never.
            Store().Save(Map(Entry("G PRO", 3, restored: true)));

            string key;
            var found = LightSlotBackupStore.Find(Store().Load(), "G PRO", 3, out key);

            Assert.True(found.Restored);
        }

        [Fact]
        public void TheOriginalNameSurvivesTheFile()
        {
            // So the wheel's own menu reads as theirs again once we give it back.
            var e = LightSlotBackupStore.FromSlot(Slot(3), "G PRO", DateTime.UtcNow);
            e.OriginalName = "Race Day";
            Store().Save(Map(new KeyValuePair<string, LightSlotBackupEntry>(
                LightSlotBackupStore.KeyFor("G PRO", 3), e)));

            string key;
            var found = LightSlotBackupStore.Find(Store().Load(), "G PRO", 3, out key);

            Assert.Equal("Race Day", found.OriginalName);
        }
    }
}
