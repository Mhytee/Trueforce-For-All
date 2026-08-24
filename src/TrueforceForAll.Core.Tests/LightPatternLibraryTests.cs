// The layout library: ordering, naming, cycling and importing what is already
// on the wheel.
//
// Order is cycle order, so reordering is a real edit. Cycling wraps because it
// is driven from a wheel button where hitting an invisible end feels broken.
// Import must be idempotent or a user who deletes an imported layout would find
// it back at the next launch.

using System;
using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LightPatternLibraryTests
    {
        private static byte[] Rgb(byte r, byte g, byte b)
        {
            var a = new byte[30];
            for (int i = 0; i < 10; i++) { a[i * 3] = r; a[i * 3 + 1] = g; a[i * 3 + 2] = b; }
            return a;
        }

        // ---------------- trim exemption ----------------
        //
        // A pattern the user tuned by eye on the rim, or one imported out of the
        // wheel's slots, is already in the wheel's own space. Trimming it would
        // correct the same error twice.

        [Fact]
        public void APatternIsTrimmedUnlessItSaysOtherwise()
        {
            var lib = new LightPatternLibrary();
            var made = LightPatternStore.Add(lib, "mine", 3, Rgb(0xFF, 0xFF, 0), "user");

            Assert.False(made.TrimExempt);
        }

        [Fact]
        public void AnExemptPatternKeepsTheFlagThroughAddAndClone()
        {
            var lib = new LightPatternLibrary();
            var made = LightPatternStore.Add(lib, "mine", 3, Rgb(0xFF, 0xFF, 0), "user", trimExempt: true);

            Assert.True(made.TrimExempt);
            Assert.True(made.Clone().TrimExempt);
        }

        [Fact]
        public void AdoptingOffTheWheelMarksThePatternExempt()
        {
            var lib = new LightPatternLibrary();
            LightPatternStore.AdoptWheelOrder(lib, Slots(Rgb(1, 2, 3)), 5, allowAdd: true);

            Assert.Single(lib.Patterns);
            Assert.True(lib.Patterns[0].TrimExempt);   // came out of the wheel
            Assert.Equal("wheel", lib.Patterns[0].Origin);
        }

        [Fact]
        public void AnExemptPatternIsMatchedOnItsRawBytesNotItsTrimmedOnes()
        {
            // The failure this guards: a trimmed comparison against an exempt
            // pattern never matches, so the library gains a duplicate and the
            // slot is re-uploaded on every tab open.
            var lib = new LightPatternLibrary();
            LightPatternStore.Add(lib, "mine", 2, Rgb(10, 200, 100), "user", trimExempt: true);

            Func<LightPattern, byte[]> wire = p => p.TrimExempt
                ? p.Rgb()
                : LedColorGain.Apply(p.Rgb(), 1f, 0.5f, 0.5f);

            int added = LightPatternStore.AdoptWheelOrder(
                lib, Slots(Rgb(10, 200, 100)), 5, allowAdd: true, wireBytes: wire);

            Assert.Equal(0, added);
            Assert.Single(lib.Patterns);
        }

        private static LightPatternLibrary WithPatterns(params string[] names)
        {
            var lib = new LightPatternLibrary();
            foreach (var n in names) LightPatternStore.Add(lib, n, 3, Rgb(0xFF, 0, 0), "user");
            return lib;
        }

        [Fact]
        public void AddGivesEveryLayoutItsOwnIdentity()
        {
            var lib = WithPatterns("A", "B");
            Assert.Equal(2, lib.Patterns.Count);
            Assert.NotEqual(lib.Patterns[0].Id, lib.Patterns[1].Id);
            Assert.All(lib.Patterns, l => Assert.False(string.IsNullOrEmpty(l.Id)));
        }

        [Fact]
        public void DuplicateNamesAreDisambiguatedRatherThanCollided()
        {
            var lib = WithPatterns("GT3", "GT3", "GT3");
            Assert.Equal(new[] { "GT3", "GT3 (2)", "GT3 (3)" }, lib.Patterns.Select(l => l.Name));
        }

        [Fact]
        public void ColoursSurviveTheRoundTripToStorage()
        {
            var lib = new LightPatternLibrary();
            var rgb = new byte[30];
            for (int i = 0; i < 30; i++) rgb[i] = (byte)(i * 7);
            var layout = LightPatternStore.Add(lib, "Test", 2, rgb, "user");
            Assert.Equal(rgb, layout.Rgb());
            Assert.Equal(2, layout.DirectionWire);
        }

        [Fact]
        public void AnInvalidDirectionFallsBackToLeftToRight()
        {
            var lib = new LightPatternLibrary();
            // The firmware rejects anything outside 1..4, so it must never be stored.
            Assert.Equal(3, LightPatternStore.Add(lib, "Bad", 9, Rgb(1, 2, 3), "user").DirectionWire);
            Assert.Equal(3, LightPatternStore.Add(lib, "Zero", 0, Rgb(1, 2, 3), "user").DirectionWire);
        }

        [Fact]
        public void RenamingAvoidsCollidingWithOthersButNotWithItself()
        {
            var lib = WithPatterns("A", "B");
            var b = lib.Patterns[1];

            LightPatternStore.Rename(lib, b.Id, "A");
            Assert.Equal("A (2)", b.Name);          // collides with the other

            LightPatternStore.Rename(lib, b.Id, "A (2)");
            Assert.Equal("A (2)", b.Name);          // its own name is not a collision
        }

        [Fact]
        public void ReorderingChangesCyclePosition()
        {
            var lib = WithPatterns("A", "B", "C");
            var c = lib.Patterns[2];

            Assert.True(LightPatternStore.Move(lib, c.Id, 0));
            Assert.Equal(new[] { "C", "A", "B" }, lib.Patterns.Select(l => l.Name));
        }

        [Fact]
        public void MovingBeyondTheEndsClampsInsteadOfThrowing()
        {
            var lib = WithPatterns("A", "B", "C");
            LightPatternStore.Move(lib, lib.Patterns[0].Id, 99);
            Assert.Equal("A", lib.Patterns[2].Name);
            LightPatternStore.Move(lib, lib.Patterns[2].Id, -5);
            Assert.Equal("A", lib.Patterns[0].Name);
        }

        [Fact]
        public void RemovingTheShowingLayoutClearsWhatIsShowing()
        {
            var lib = WithPatterns("A", "B");
            lib.CurrentId = lib.Patterns[0].Id;

            LightPatternStore.Remove(lib, lib.Patterns[0].Id);
            Assert.Null(lib.CurrentId);
            Assert.Single(lib.Patterns);
        }

        [Fact]
        public void CyclingWalksForwardAndWrapsAtTheEnd()
        {
            var lib = WithPatterns("A", "B", "C");
            lib.CurrentId = lib.Patterns[2].Id;      // on the last one
            Assert.Equal("A", LightPatternStore.Step(lib, 1).Name);
        }

        [Fact]
        public void CyclingWalksBackwardAndWrapsAtTheStart()
        {
            var lib = WithPatterns("A", "B", "C");
            lib.CurrentId = lib.Patterns[0].Id;
            Assert.Equal("C", LightPatternStore.Step(lib, -1).Name);
        }

        [Fact]
        public void TheFirstPressLandsOnTheFirstLayout()
        {
            // Nothing showing yet, so a forward press should start at the top of
            // the library rather than skipping an entry.
            var lib = WithPatterns("A", "B", "C");
            lib.CurrentId = null;
            Assert.Equal("A", LightPatternStore.Step(lib, 1).Name);
        }

        [Fact]
        public void CyclingAnEmptyLibraryDoesNothing()
            => Assert.Null(LightPatternStore.Step(new LightPatternLibrary(), 1));

        [Fact]
        public void CyclingASinglePatternstaysOnIt()
        {
            var lib = WithPatterns("Only");
            lib.CurrentId = lib.Patterns[0].Id;
            Assert.Equal("Only", LightPatternStore.Step(lib, 1).Name);
        }

        // ---- shipped patterns ----

        [Fact]
        public void EveryShippedPatternIsWellFormed()
        {
            // These go straight to the wheel, and the firmware rejects a
            // direction outside 1..4 outright.
            foreach (var b in LightPatternStore.Builtins)
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Name));
                Assert.InRange(b.Direction, (byte)1, (byte)4);
                var rgb = LightSlotBackupStore.FromHex(b.Hex);
                Assert.NotNull(rgb);
                Assert.Equal(30, rgb.Length);      // exactly ten LEDs
            }
        }

        [Fact]
        public void ShippedPatternNamesAreDistinct()
        {
            var names = LightPatternStore.Builtins.Select(b => b.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void SamePatternToleratesRoundingButNotADifferentPattern()
        {
            var a = new byte[] { 100, 150, 200, 10, 20, 30 };

            // A trim nudged by a fraction of a count rounds some bytes the other
            // way. That is the same pattern and must still match: this exact case
            // re-adopted four of five wheel slots as "CUSTOM n" and pushed the
            // named originals off the wheel.
            Assert.True(LightPatternStore.SamePattern(a, new byte[] { 101, 149, 200, 10, 21, 29 }));
            Assert.True(LightPatternStore.SamePattern(a, new byte[] { 102, 148, 202, 12, 22, 32 }));

            // Three counts is past the tolerance, and a genuinely different
            // pattern is nowhere near it.
            Assert.False(LightPatternStore.SamePattern(a, new byte[] { 103, 150, 200, 10, 20, 30 }));
            Assert.False(LightPatternStore.SamePattern(a, new byte[] { 0, 255, 0, 255, 0, 255 }));

            // Nulls and length mismatches are never a match rather than throwing.
            Assert.False(LightPatternStore.SamePattern(a, null));
            Assert.False(LightPatternStore.SamePattern(null, a));
            Assert.False(LightPatternStore.SamePattern(a, new byte[] { 100, 150, 200 }));
        }

        [Fact]
        public void BuiltinsFillAnEmptyLibrary()
        {
            var lib = new LightPatternLibrary();
            int n = LightPatternStore.AddBuiltins(lib);
            Assert.Equal(LightPatternStore.Builtins.Length, n);
            Assert.All(lib.Patterns, p => Assert.Equal("builtin", p.Origin));
            Assert.Contains(lib.Patterns, p => p.Name == "Rainbow");
        }

        [Fact]
        public void BuiltinsAreNotReAddedToALibraryThatAlreadyHasSomething()
        {
            // Otherwise a user who deleted one would find it back next launch.
            var lib = WithPatterns("Mine");
            Assert.Equal(0, LightPatternStore.AddBuiltins(lib));
            Assert.Single(lib.Patterns);
        }

        [Fact]
        public void TheMirroredBuiltinsAreActuallySymmetric()
        {
            // A mirrored direction drives the strip in pairs, so a pattern
            // declared outside-in or inside-out must read the same from both
            // ends or the two halves would disagree.
            foreach (var b in LightPatternStore.Builtins.Where(x => x.Direction == 1 || x.Direction == 2))
            {
                var rgb = LightSlotBackupStore.FromHex(b.Hex);
                for (int i = 0; i < 5; i++)
                {
                    int j = 9 - i;
                    Assert.True(rgb[i * 3] == rgb[j * 3]
                             && rgb[i * 3 + 1] == rgb[j * 3 + 1]
                             && rgb[i * 3 + 2] == rgb[j * 3 + 2],
                             b.Name + " is not symmetric at LED " + (i + 1));
                }
            }
        }

        // ---- where a pattern lives is where it sits ----

        private static Func<int, WheelLedChannel.WheelLedSlot> Slots(params byte[][] colours)
            => slot => slot < colours.Length && colours[slot] != null
                ? new WheelLedChannel.WheelLedSlot
                  { Slot = (byte)slot, DirectionWire = 2, Rgb = colours[slot] }
                : null;

        private static LightPatternLibrary LibOf(params string[] names)
        {
            var lib = new LightPatternLibrary();
            for (int i = 0; i < names.Length; i++)
                LightPatternStore.Add(lib, names[i], 3, Rgb((byte)(i + 1), 0, 0), "user");
            return lib;
        }

        [Fact]
        public void TheFirstFiveAreOnTheWheelAndTheRestAreNot()
        {
            var lib = LibOf("a", "b", "c", "d", "e", "f", "g");
            LightPatternStore.NormalizeSlots(lib);

            Assert.Equal(new[] { 0, 1, 2, 3, 4, -1, -1 }, lib.Patterns.Select(p => p.Slot));
            Assert.Equal(5, lib.Patterns.Count(p => p.OnWheel));
        }

        [Fact]
        public void MovingUpIntoTheTopFivePutsAPatternOnTheWheel()
        {
            var lib = LibOf("a", "b", "c", "d", "e", "f");
            var sixth = lib.Patterns[5];
            Assert.False(sixth.OnWheel);

            LightPatternStore.Move(lib, sixth.Id, 4);

            Assert.True(sixth.OnWheel);
            Assert.Equal(4, sixth.Slot);
        }

        [Fact]
        public void MovingDownOutOfTheTopFiveTakesAPatternOffTheWheel()
        {
            var lib = LibOf("a", "b", "c", "d", "e", "f");
            var fifth = lib.Patterns[4];
            Assert.True(fifth.OnWheel);

            LightPatternStore.Move(lib, fifth.Id, 5);

            Assert.False(fifth.OnWheel);
            Assert.Equal(-1, fifth.Slot);
            // And the one it swapped with has come the other way.
            Assert.True(lib.Patterns[4].OnWheel);
        }

        [Fact]
        public void DeletingSomethingAboveTheLinePullsThePatternBelowItOn()
        {
            var lib = LibOf("a", "b", "c", "d", "e", "f");
            var sixth = lib.Patterns[5];

            LightPatternStore.Remove(lib, lib.Patterns[0].Id);

            Assert.True(sixth.OnWheel);
            Assert.Equal(4, sixth.Slot);
        }

        [Fact]
        public void AdoptingPutsTheListInTheOrderTheWheelIsIn()
        {
            var lib = new LightPatternLibrary();
            // Deliberately added in the wrong order: adopting must reorder them.
            var second = LightPatternStore.Add(lib, "second", 2, Rgb(4, 5, 6), "user");
            var first  = LightPatternStore.Add(lib, "first",  2, Rgb(1, 2, 3), "user");

            int added = LightPatternStore.AdoptWheelOrder(
                lib, Slots(Rgb(1, 2, 3), Rgb(4, 5, 6)), 5, allowAdd: true);

            Assert.Equal(0, added);                 // both already existed
            Assert.Same(first, lib.Patterns[0]);
            Assert.Same(second, lib.Patterns[1]);
            Assert.Equal(0, first.Slot);
            Assert.Equal(1, second.Slot);
        }

        [Fact]
        public void AdoptingAssignsAnExistingPatternRatherThanCopyingIt()
        {
            // The shipped built-ins ARE copies of real slots, so without this
            // every user would start with a duplicate of each.
            var lib = new LightPatternLibrary();
            var mine = LightPatternStore.Add(lib, "Converge", 2, Rgb(1, 2, 3), "builtin");

            int added = LightPatternStore.AdoptWheelOrder(lib, Slots(Rgb(1, 2, 3)), 5, allowAdd: true);

            Assert.Equal(0, added);
            Assert.Single(lib.Patterns);
            Assert.Equal(0, mine.Slot);
        }

        [Fact]
        public void AdoptingBringsInWhatTheWheelHoldsAndWeDoNot()
        {
            var lib = new LightPatternLibrary();
            int added = LightPatternStore.AdoptWheelOrder(
                lib, Slots(Rgb(1, 2, 3), Rgb(4, 5, 6)), 5, allowAdd: true);

            Assert.Equal(2, added);
            Assert.Equal(new[] { "CUSTOM 1", "CUSTOM 2" }, lib.Patterns.Select(l => l.Name));
            Assert.All(lib.Patterns, l => Assert.Equal("wheel", l.Origin));
        }

        [Fact]
        public void AdoptingIgnoresSlotsThatWereNeverProgrammed()
        {
            var lib = new LightPatternLibrary();
            int added = LightPatternStore.AdoptWheelOrder(
                lib, Slots(Rgb(0, 0, 0), Rgb(9, 9, 9), null), 5, allowAdd: true);

            Assert.Equal(1, added);
            Assert.Equal("CUSTOM 2", lib.Patterns[0].Name);
        }

        [Fact]
        public void AdoptingAddsNothingNewWhenAddingIsOff()
        {
            var lib = new LightPatternLibrary();
            int added = LightPatternStore.AdoptWheelOrder(
                lib, Slots(Rgb(1, 2, 3)), 5, allowAdd: false);

            Assert.Equal(0, added);
            Assert.Empty(lib.Patterns);
        }

        [Fact]
        public void AdoptingSurvivesASlotThatCannotBeRead()
        {
            var lib = new LightPatternLibrary();
            Func<int, WheelLedChannel.WheelLedSlot> throwing = slot =>
            {
                if (slot == 1) throw new System.IO.IOException("wheel busy");
                // Well apart per slot: adoption matches with a tolerance now, so
                // a fixture whose "different" slots sit two counts apart would
                // have them collapse into one pattern and test nothing.
                return new WheelLedChannel.WheelLedSlot
                    { Slot = (byte)slot, DirectionWire = 3, Rgb = Rgb((byte)(20 + slot * 60), 7, 7) };
            };

            int added = LightPatternStore.AdoptWheelOrder(lib, throwing, 3, allowAdd: true);
            Assert.Equal(2, added);                    // the other two still land
        }
    }

    public class LightSlotBackupHexTests
    {
        [Fact]
        public void HexRoundTripsExactly()
        {
            var rgb = new byte[30];
            for (int i = 0; i < 30; i++) rgb[i] = (byte)(255 - i * 3);
            Assert.Equal(rgb, LightSlotBackupStore.FromHex(LightSlotBackupStore.ToHex(rgb)));
        }

        [Theory]
        [InlineData("ABC")]        // odd length
        [InlineData("ZZ00")]       // not hex
        [InlineData("")]
        [InlineData(null)]
        public void MalformedHexIsRefusedRatherThanPartiallyParsed(string hex)
        {
            // A half-parsed backup written back to the wheel would be garbage
            // colours, so anything malformed must fail cleanly.
            Assert.Null(LightSlotBackupStore.FromHex(hex));
        }

        [Fact]
        public void AShortBackupIsNotAcceptedAsASlot()
        {
            var entry = new LightSlotBackupEntry { RgbHex = "FFFFFF", Slot = 0, DirectionWire = 3 };
            Assert.Null(LightSlotBackupStore.ToSlot(entry));   // needs a full 30 bytes
        }

        [Fact]
        public void BackupsAreKeyedByWheelAndSlotSoTwoBasesCannotCrossOver()
        {
            Assert.NotEqual(LightSlotBackupStore.KeyFor("G PRO", 4),
                            LightSlotBackupStore.KeyFor("RS50", 4));
            Assert.NotEqual(LightSlotBackupStore.KeyFor("G PRO", 3),
                            LightSlotBackupStore.KeyFor("G PRO", 4));
        }
    }
}
