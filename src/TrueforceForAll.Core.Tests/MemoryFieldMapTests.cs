using System;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>
    /// The field frame is where a candidate lives or dies and where a saved map
    /// entry is called live or stale. Both decisions are invisible on a screen
    /// until they have already gone wrong, and both have a failure mode that is
    /// worse than not having the feature at all:
    ///
    ///   Eliminating on silence deletes the right answer. A candidate the
    ///   scanner did not report looks exactly like one that did not move, and
    ///   the watch wire has a cap, so this WILL happen.
    ///
    ///   Calling a garbage read live feeds a wrong number onward. A map that
    ///   quietly reads rubbish after a game update is worse than no map.
    ///
    /// So the elimination arithmetic, the sanity checks and the pointer path
    /// notation are pinned here, where they can be exercised with no game, no
    /// scanner and no screen.
    /// </summary>
    public class MemoryFieldMapTests
    {
        private static ScanWatchRow Read(string addr, string value)
            => new ScanWatchRow { Addr = addr, Value = value, Ok = true };

        private static ScanWatchRow Failed(string addr, string why)
            => new ScanWatchRow { Addr = addr, Ok = false, Why = why };

        private static MemoryFieldStore StoreWith(params string[] addresses)
        {
            var store = new MemoryFieldStore();
            foreach (string a in addresses)
                Assert.Null(store.AddCandidate(MemoryFields.CarName, a, "utf16", 32, null, "found", out _));
            return store;
        }

        // ---- the catalog ------------------------------------------------------

        [Fact]
        public void EveryFieldExistsInAFreshMapEvenBeforeAnythingIsFound()
        {
            // The map is the tab's main view, so a map that only listed what had
            // already been found would never tell anyone what is left to do.
            var store = new MemoryFieldStore();
            Assert.Equal(MemoryFields.BuiltIn.Count, store.Fields.Count);
            foreach (var f in store.Fields) Assert.Equal(MemoryFieldState.Empty, f.State);
            Assert.NotNull(store.Find(MemoryFields.Rpm));
            Assert.NotNull(store.Find(MemoryFields.LapTime));
            Assert.NotNull(store.Find(MemoryFields.TimeLeft));
        }

        [Fact]
        public void EveryFieldOffersBothGenericActionsAsWellAsItsOwn()
        {
            foreach (var def in MemoryFields.BuiltIn)
            {
                var ids = new List<string>();
                foreach (var a in def.Actions) ids.Add(a.Id);
                Assert.Contains("it-changes", ids);
                Assert.Contains("it-holds", ids);
                Assert.True(ids.Count >= 3, def.Key + " offers nothing of its own");
            }
        }

        // ---- elimination: the arithmetic the whole feature rests on -----------

        [Fact]
        public void AChangeRoundKeepsWhatMovedAndDropsWhatDidNot()
        {
            var store = StoreWith("0x00001000", "0x00002000", "0x00003000");
            store.Observe(new[] { Read("0x00001000", "TRUENO"), Read("0x00002000", "still"), Read("0x00003000", "also") });

            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "LEVIN"), Read("0x00002000", "still"), Read("0x00003000", "also") });

            var result = store.FinishRound();
            Assert.Equal(3, result.Before);
            Assert.Equal(1, result.After);
            Assert.Equal(2, result.Eliminated);
            Assert.Equal(0, result.Unread);
            Assert.Single(store.Find(MemoryFields.CarName).Candidates);
            Assert.Equal("0x00001000", store.Find(MemoryFields.CarName).Candidates[0].Addr);
        }

        [Fact]
        public void AHoldRoundKeepsWhatSatStillAndDropsWhatWandered()
        {
            var store = StoreWith("0x00001000", "0x00002000");
            store.Observe(new[] { Read("0x00001000", "7400"), Read("0x00002000", "12") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "sit-still"));
            store.Observe(new[] { Read("0x00001000", "7400"), Read("0x00002000", "13") });

            var result = store.FinishRound();
            Assert.Equal(1, result.After);
            Assert.Equal("0x00001000", store.Find(MemoryFields.CarName).Candidates[0].Addr);
        }

        [Fact]
        public void ACandidateTheScannerNeverReportedIsKeptAndCounted()
        {
            // The rule that stops this tool deleting its own answer. The watch
            // wire has a cap and a scanner can drop rows; a candidate nobody
            // read is not evidence either way, and dropping it would look
            // identical to dropping one that failed the test honestly.
            var store = StoreWith("0x00001000", "0x00002000");
            store.Observe(new[] { Read("0x00001000", "TRUENO") });

            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "LEVIN") });

            var result = store.FinishRound();
            Assert.Equal(1, result.Unread);
            Assert.Equal(2, result.After);        // both kept: one passed, one was never read
            Assert.Equal(0, result.Eliminated);
            Assert.Contains("KEPT", result.Line);
        }

        [Fact]
        public void OneReadingInsideARoundIsNotEnoughToJudgeACandidate()
        {
            // A single reading says nothing about whether a value moved, so a
            // candidate seen once during the round is unread rather than
            // unchanged.
            var store = StoreWith("0x00001000");
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "TRUENO") });

            var result = store.FinishRound();
            Assert.Equal(1, result.Unread);
            Assert.Equal(0, result.Eliminated);
        }

        [Fact]
        public void AReadThatFailedIsNotCountedAsTheValueMoving()
        {
            // A failed read changes the text shown, and counting that as "it
            // moved" would let a region that was freed and remapped survive a
            // round it should have died in.
            var store = StoreWith("0x00001000");
            store.Observe(new[] { Read("0x00001000", "TRUENO") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Failed("0x00001000", "the page is gone") });
            store.Observe(new[] { Failed("0x00001000", "the page is gone") });

            var result = store.FinishRound();
            // One good read at the start, none since: not enough to judge.
            Assert.Equal(1, result.Unread);
            Assert.Equal(0, result.Eliminated);
        }

        [Fact]
        public void AValueThatMovedAndCameBackStillCountsAsHavingMoved()
        {
            // Compared against the FIRST reading of the round rather than the
            // one before it, so a transient response is not thrown away by the
            // value settling back.
            var store = StoreWith("0x00001000");
            store.Observe(new[] { Read("0x00001000", "3") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "it-changes"));
            store.Observe(new[] { Read("0x00001000", "4") });
            store.Observe(new[] { Read("0x00001000", "3") });

            var result = store.FinishRound();
            Assert.Equal(0, result.Eliminated);
            Assert.Equal(1, result.After);
        }

        [Fact]
        public void TwoRoundsTakeAWallOfCandidatesDownToAHandful()
        {
            // The claim the feature is sold on, exercised end to end: a hundred
            // and fifty candidates, one car change, one sit-still, and what is
            // left is readable by eye.
            var store = new MemoryFieldStore();
            var addresses = new List<string>();
            for (int i = 0; i < 150; i++)
            {
                string a = "0x" + (0x10000 + i * 0x10).ToString("X8");
                addresses.Add(a);
                Assert.Null(store.AddCandidate(MemoryFields.CarName, a, "utf16", 32, null, "found", out _));
            }

            // Round one: change car. Only every tenth address follows.
            var before = new List<ScanWatchRow>();
            var after = new List<ScanWatchRow>();
            for (int i = 0; i < addresses.Count; i++)
            {
                before.Add(Read(addresses[i], "TRUENO"));
                after.Add(Read(addresses[i], i % 10 == 0 ? "LEVIN" : "TRUENO"));
            }
            store.Observe(before);
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(after);
            var r1 = store.FinishRound();
            Assert.Equal(15, r1.After);

            // Round two: sit still. Three of the fifteen keep flickering.
            var idle1 = new List<ScanWatchRow>();
            var idle2 = new List<ScanWatchRow>();
            var left = store.Find(MemoryFields.CarName).Candidates;
            for (int i = 0; i < left.Count; i++)
            {
                idle1.Add(Read(left[i].Addr, "LEVIN"));
                idle2.Add(Read(left[i].Addr, i < 3 ? "NOISE" + i.ToString() : "LEVIN"));
            }
            store.Observe(idle1);
            Assert.Null(store.BeginRound(MemoryFields.CarName, "sit-still"));
            store.Observe(idle2);
            var r2 = store.FinishRound();
            Assert.Equal(12, r2.After);
            Assert.Equal(3, r2.Eliminated);
        }

        [Fact]
        public void UndoPutsBackEverythingTheLastRoundThrewOut()
        {
            // A declared action pressed the wrong way round would otherwise
            // delete a whole session's work with nothing to reverse it.
            var store = StoreWith("0x00001000", "0x00002000");
            store.Observe(new[] { Read("0x00001000", "a"), Read("0x00002000", "b") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "z"), Read("0x00002000", "b") });
            store.FinishRound();
            Assert.Single(store.Find(MemoryFields.CarName).Candidates);

            Assert.True(store.CanUndo);
            Assert.True(store.UndoLastRound());
            Assert.Equal(2, store.Find(MemoryFields.CarName).Candidates.Count);
            Assert.Empty(store.Find(MemoryFields.CarName).RoundsRun);
            Assert.False(store.CanUndo);
        }

        [Fact]
        public void UndoTakesBackTheCreditTheRoundHandedTheSurvivors()
        {
            // Otherwise a round run and undone leaves every survivor claiming a
            // piece of evidence that was taken back, and that claim is written
            // into the map as the confirmation record.
            var store = StoreWith("0x00001000", "0x00002000");
            store.Observe(new[] { Read("0x00001000", "a"), Read("0x00002000", "b") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "z"), Read("0x00002000", "b") });
            store.FinishRound();
            Assert.Equal(1, store.Find(MemoryFields.CarName).Candidates[0].RoundsSurvived);

            Assert.True(store.UndoLastRound());
            foreach (var c in store.Find(MemoryFields.CarName).Candidates)
                Assert.Equal(0, c.RoundsSurvived);
        }

        [Fact]
        public void AStaleReadingDoesNotSeedARoundsBaseline()
        {
            // A value read minutes ago is not what the field held when the
            // operator pressed start. Judging a hold round against it would rule
            // out the right answer for moving while nobody was watching, so a
            // baseline that old is not used and the candidate comes back unread
            // instead.
            var store = StoreWith("0x00001000");
            store.Observe(new[] { Read("0x00001000", "old") });
            store.LastObservedUtc = DateTime.UtcNow - TimeSpan.FromMinutes(5);

            Assert.Null(store.BeginRound(MemoryFields.CarName, "sit-still"));
            store.Observe(new[] { Read("0x00001000", "new") });
            var result = store.FinishRound();
            Assert.Equal(1, result.Unread);
            Assert.Equal(0, result.Eliminated);
        }

        [Fact]
        public void ARoundCannotStartOnAFieldWithNothingToEliminate()
        {
            var store = new MemoryFieldStore();
            Assert.NotNull(store.BeginRound(MemoryFields.Rpm, "sit-still"));
            Assert.False(store.RoundLive);
        }

        [Fact]
        public void ASecondRoundCannotStartWhileOneIsRunning()
        {
            var store = StoreWith("0x00001000");
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            Assert.NotNull(store.BeginRound(MemoryFields.CarName, "sit-still"));
        }

        // ---- sanity checks: what stops a stale map feeding a wrong number -----

        [Theory]
        [InlineData("7400")]
        [InlineData("7400.0")]
        [InlineData("740")]        // stored in tens, which is a real encoding
        [InlineData("775.0")]      // radians per second, which is another
        public void APlausibleRedlineReadsAsLive(string value)
            => Assert.Null(MemoryFields.ByKey(MemoryFields.Redline).Sanity.Check(value));

        [Theory]
        [InlineData("NaN")]
        [InlineData("-Infinity")]
        [InlineData("2147483647")]
        [InlineData("0")]          // no car has a redline of nothing
        [InlineData("hello")]
        [InlineData("")]
        [InlineData("cannot read: the page is gone")]
        public void AnImplausibleRedlineIsCalledOutRatherThanTrusted(string value)
            => Assert.NotNull(MemoryFields.ByKey(MemoryFields.Redline).Sanity.Check(value));

        [Fact]
        public void ATextFieldFullOfNullsIsNotAName()
        {
            var text = MemoryFields.ByKey(MemoryFields.CarName).Sanity;
            Assert.Null(text.Check("TRUENO GT-APEX"));
            Assert.NotNull(text.Check("\0\0\0\0"));
            Assert.NotNull(text.Check("   "));
            // A decode that gave up reads as replacement characters, which is a
            // failed read wearing a value's clothes.
            Assert.NotNull(text.Check("��"));
        }

        [Fact]
        public void NumbersAreReadInvariantlySoAThousandsCommaCannotShiftThem()
        {
            Assert.True(MemorySanity.TryNumber("7,400", out double v));
            Assert.Equal(7400d, v);
            Assert.True(MemorySanity.TryNumber("7400.5", out double d));
            Assert.Equal(7400.5d, d);
        }

        [Fact]
        public void AnEntryIsNotLiveUntilThisLaunchHasReadIt()
        {
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-1", out var entry));
            Assert.False(entry.CheckedThisLaunch);
            Assert.Equal("not checked", entry.StateWord);

            store.Observe(new[] { Read("0x00001000", "TRUENO GT-APEX") });
            Assert.Equal("live", entry.StateWord);
            Assert.Equal(1, entry.Launches);
        }

        [Fact]
        public void AnEntryThatReadsBackGarbageIsMarkedStaleRatherThanUsed()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCandidate(MemoryFields.Redline, "0x00001000", "int32", 0, null, "found", out _));
            Assert.Null(store.Promote(MemoryFields.Redline, "0x00001000", "Redline", null, "run-1", out var entry));

            store.Observe(new[] { Read("0x00001000", "7400") });
            Assert.Equal("live", entry.StateWord);
            Assert.Equal("7400", entry.LastGood);

            store.Observe(new[] { Read("0x00001000", "1908874240") });
            Assert.Equal("STALE", entry.StateWord);
            // The last value that made sense is kept, so a stale row can be
            // shown next to what it used to read.
            Assert.Equal("7400", entry.LastGood);
        }

        [Fact]
        public void AnEntryThatWillNotResolveIsToldApartFromOneThatWillNotRead()
        {
            // Two different fixes: a root that is not there needs a new anchor,
            // and a page that cannot be read usually means the game moved on.
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-1", out var entry));

            store.Observe(new[] { new ScanWatchRow { Addr = "0x00001000", Ok = false, Why = "no such module" } });
            Assert.True(entry.ResolveFailed);
            Assert.Contains("no longer resolves", entry.StaleWhy);

            store.Observe(new[] { new ScanWatchRow { Addr = "0x00001000", Ok = false, Resolved = "0x0A001000",
                                                     Why = "the page is not committed" } });
            Assert.False(entry.ResolveFailed);
            Assert.Contains("resolves but cannot be read", entry.StaleWhy);
        }

        [Fact]
        public void ANewLaunchMakesEveryEntryEarnLiveAgain()
        {
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-1", out var entry));
            store.Observe(new[] { Read("0x00001000", "TRUENO") });
            Assert.Equal(1, entry.Launches);

            store.BeginLaunch();
            Assert.Equal("not checked", entry.StateWord);
            store.Observe(new[] { Read("0x00001000", "TRUENO") });
            Assert.Equal(2, entry.Launches);
        }

        [Fact]
        public void ASecondScannerInsideOneLaunchDoesNotCountAsARelaunch()
        {
            // The launch count is what separates a route that resolved once from
            // one that is actually durable, so an afternoon of scanning must not
            // inflate it.
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-1", out var entry));
            store.Observe(new[] { Read("0x00001000", "TRUENO") });
            store.ForgetReadings();
            store.Observe(new[] { Read("0x00001000", "TRUENO") });
            Assert.Equal(1, entry.Launches);
        }

        // ---- promotion and anchoring -------------------------------------------

        [Fact]
        public void AModuleAnchoredCandidateIsPromotedOnItsModuleForm()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCandidate(MemoryFields.Redline, "0x004A2C10", "int32", 0,
                                           "game.exe+0xA2C10", "found", out var c));
            Assert.True(c.IsAnchored);
            Assert.Null(store.Promote(MemoryFields.Redline, "0x004A2C10", "Redline", null, "run-1", out var entry));
            Assert.Equal("game.exe+0xA2C10", entry.Addr);
            Assert.Equal(MemoryRoute.Module, entry.Route);
            // The absolute address is the fallback, and it is good for one
            // launch, which the file says rather than leaves to be assumed.
            Assert.Equal("0x004A2C10", entry.Fallback);
            Assert.True(entry.FallbackVolatile);
        }

        [Fact]
        public void APointerPathBeatsEverythingElseAsTheRoute()
        {
            var store = StoreWith("0x0A001000");
            Assert.False(store.Find(MemoryFields.CarName).Candidates[0].IsAnchored);
            Assert.Null(store.Promote(MemoryFields.CarName, "0x0A001000", "Car name",
                                      "[game.exe+0x4A2C10]+0x18", "run-1", out var entry));
            Assert.Equal(MemoryRoute.Path, entry.Route);
            Assert.Equal("[game.exe+0x4A2C10]+0x18", entry.Addr);
            Assert.Equal("0x0A001000", entry.Fallback);
            Assert.True(entry.FallbackVolatile);
        }

        [Fact]
        public void PromotingWritesDownHowItWasConfirmed()
        {
            // An entry nobody can audit is an entry nobody should trust.
            var store = StoreWith("0x00001000", "0x00002000");
            store.Observe(new[] { Read("0x00001000", "a"), Read("0x00002000", "b") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
            store.Observe(new[] { Read("0x00001000", "z"), Read("0x00002000", "b") });
            store.FinishRound();

            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-7", out var entry));
            Assert.Contains("survived", entry.ConfirmedHow);
            Assert.Contains("change car", entry.ConfirmedHow);
            Assert.Equal("run-7", entry.Run);
        }

        [Fact]
        public void PromotingClearsTheOtherCandidatesRatherThanLeavingAWallOfThem()
        {
            var store = StoreWith("0x00001000", "0x00002000", "0x00003000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, null, out _));
            Assert.Empty(store.Find(MemoryFields.CarName).Candidates);
            Assert.Equal(MemoryFieldState.Confirmed, store.Find(MemoryFields.CarName).State);
        }

        [Fact]
        public void DemotingPutsTheAddressBackAsACandidateSoARefindDoesNotStartFromNothing()
        {
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, null, out _));
            Assert.True(store.Demote(MemoryFields.CarName));
            Assert.Single(store.Find(MemoryFields.CarName).Candidates);
            Assert.Equal(MemoryFieldState.Candidates, store.Find(MemoryFields.CarName).State);
        }

        [Fact]
        public void CandidatesAreOrderedByAnchoringAndAddressRatherThanByAnyScore()
        {
            // Ranking has already put an audio filter cutoff above a tachometer
            // here. Anchoring is a fact about durability; a score is a guess, and
            // a list sorted by one teaches whoever reads it to trust the order.
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x0000F000", "utf16", 32, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x00001000", "utf16", 32, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x0000E000", "utf16", 32,
                                           "game.exe+0xE000", "", out _));
            var order = store.CandidatesInOrder(MemoryFields.CarName);
            Assert.Equal("0x0000E000", order[0].Addr);   // anchored first
            Assert.Equal("0x00001000", order[1].Addr);   // then by address
            Assert.Equal("0x0000F000", order[2].Addr);
        }

        [Fact]
        public void OneAddressCanBeACandidateForTwoFieldsAtOnce()
        {
            // Until a round says otherwise, a number could be the revs or the
            // speed, and both have to see it move.
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCandidate(MemoryFields.Rpm, "0x00001000", "float32", 0, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.Speed, "0x00001000", "float32", 0, null, "", out _));
            store.Observe(new[] { Read("0x00001000", "3000") });
            Assert.Equal("3000", store.Find(MemoryFields.Rpm).Candidates[0].Shown);
            Assert.Equal("3000", store.Find(MemoryFields.Speed).Candidates[0].Shown);
        }

        // ---- pointer path notation ----------------------------------------------

        [Theory]
        [InlineData("[game.exe+0x4A2C10]+0x18", "game.exe+0x4A2C10", 1)]
        [InlineData("[[game.exe+0x4A2C10]+0x18]+0x4", "game.exe+0x4A2C10", 2)]
        [InlineData("[[[game.exe+0x100]+0x8]+0x10]+0x24", "game.exe+0x100", 3)]
        public void APointerPathRoundTripsThroughItsOwnNotation(string text, string root, int depth)
        {
            var spec = MemoryPathSpec.TryParse(text, out string problem);
            Assert.Null(problem);
            Assert.Equal(root, spec.Root);
            Assert.Equal(depth, spec.Depth);
            Assert.Equal(text, spec.Format());
        }

        [Fact]
        public void APathOffsetIsReadAsHexEvenWithoutThePrefix()
        {
            // Everything either half of this prints is hex. A decimal reading
            // would land somewhere else entirely and still look like a valid row.
            var spec = MemoryPathSpec.TryParse("[game.exe+0x100]+18", out string problem);
            Assert.Null(problem);
            Assert.Equal("[game.exe+0x100]+0x18", spec.Format());
        }

        [Theory]
        [InlineData("game.exe+0x100")]          // not a path at all
        [InlineData("[game.exe+0x100")]          // unclosed
        [InlineData("[game.exe+0x100]")]         // a level with no offset
        [InlineData("[[game.exe+0x100]+0x8")]    // one level short of its offsets
        [InlineData("[not an address]+0x8")]     // the root is not one
        [InlineData("[game.exe+0x100]+0xZZ")]    // not hex
        public void AMalformedPathIsRefusedWithAReason(string text)
        {
            var spec = MemoryPathSpec.TryParse(text, out string problem);
            Assert.Null(spec);
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        [Fact]
        public void TheWatchListAcceptsAPointerPathAsAnAddress()
        {
            // Without this a path could be found and never watched, which would
            // leave the one thing that makes a heap address durable unusable.
            string got = MemoryWatchList.NormalizeAddress("[game.exe+0x4a2c10]+0x18", out string problem);
            Assert.Null(problem);
            Assert.Equal("[game.exe+0x4A2C10]+0x18", got);
            Assert.Equal(MemoryRoute.Path, MemoryRoutes.Of(got));
        }

        [Fact]
        public void TheThreeRoutesAreToldApartByShapeAlone()
        {
            Assert.Equal(MemoryRoute.Absolute, MemoryRoutes.Of("0x01C02750"));
            Assert.Equal(MemoryRoute.Module,   MemoryRoutes.Of("game.exe+0x4A2C10"));
            Assert.Equal(MemoryRoute.Path,     MemoryRoutes.Of("[game.exe+0x4A2C10]+0x18"));
        }

        // ---- the wire ------------------------------------------------------------

        [Fact]
        public void APathEventDecodesTheScannersOwnFieldNames()
        {
            // The exact line the scanner emits. Pinned because a rename on
            // either side leaves a promotion waiting forever on an answer that
            // arrived and was not recognised, which on screen is a search that
            // never finishes.
            var ev = ScanHost.Parse(
                "{\"ev\":\"path\",\"spec\":\"[[game.exe+0x4A2C10]+0x18]+0x4\",\"target\":\"0x0A001000\","
              + "\"depth\":2,\"window\":64,\"root\":\"game.exe+0x4A2C10\",\"rootModule\":\"game.exe\","
              + "\"rootKind\":\"main-exe\",\"rank\":1,\"rootFixed\":true}");
            Assert.NotNull(ev);
            Assert.Equal("path", ev.Ev);
            Assert.Equal("[[game.exe+0x4A2C10]+0x18]+0x4", ev.PathText);
            Assert.Equal("0x0A001000", ev.PathTarget);
            Assert.Equal("game.exe+0x4A2C10", ev.PathRoot);
            Assert.Equal(2, ev.PathDepth);
            Assert.Equal(64, ev.PathWindow);
            Assert.Equal("main-exe", ev.PathRootKind);
            Assert.Equal(1, ev.PathRank);
            Assert.True(ev.PathStatic);
        }

        [Fact]
        public void APathEventWithNoSpecIsTheAnswerThatThereIsNone()
        {
            // A real answer, not a failure: it means the value is reached some
            // other way. It must not decode as a path with an empty text, which
            // would be promoted as an entry pointing at nothing.
            var ev = ScanHost.Parse(
                "{\"ev\":\"path\",\"spec\":null,\"target\":\"0x0A001000\",\"rootKind\":\"none\"}");
            Assert.NotNull(ev);
            Assert.Null(ev.PathText);
            Assert.Equal("0x0A001000", ev.PathTarget);
        }

        [Fact]
        public void APathSpecMatchesWhatThisSideWouldHaveWrittenItself()
        {
            // Both halves render a path in the same notation, and it has to stay
            // that way: the spec goes straight onto the watch list as written and
            // into the map file as an entry's address, so a rendering difference
            // would make the same path two different rows.
            var spec = new MemoryPathSpec
            {
                Root = "game.exe+0x4A2C10",
                Offsets = new List<long> { 0x18, 0x4 },
            };
            Assert.Equal("[[game.exe+0x4A2C10]+0x18]+0x4", spec.Format());
        }

        [Fact]
        public void APathCheckDecodesItsOutcomeAndWhatItRead()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"path-check\",\"spec\":\"[game.exe+0x100]+0x18\",\"label\":\"Revs\","
              + "\"outcome\":\"IMPLAUSIBLE\",\"resolved\":\"0x0A001000\",\"value\":\"1908874240\","
              + "\"why\":\"outside 0,30000\"}");
            Assert.Equal("IMPLAUSIBLE", ev.Outcome);
            Assert.Equal("[game.exe+0x100]+0x18", ev.PathText);
            Assert.Equal("0x0A001000", ev.Resolved);
            Assert.Equal("1908874240", ev.PathValue);
        }

        [Fact]
        public void ARoundEventDecodesWhichRowsMovedAndWhichDidNot()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"round\",\"name\":\"car-name\",\"must\":\"change\",\"ended\":\"host\","
              + "\"changedCount\":1,\"unchangedCount\":2,\"changed\":[\"0x1000\"],"
              + "\"unchanged\":[\"0x2000\",\"0x3000\"],\"rows\":[]}");
            Assert.Equal("round", ev.Ev);
            Assert.Equal("car-name", ev.RoundName);
            Assert.Equal("change", ev.RoundMust);
            Assert.Equal(1, ev.ChangedCount);
            Assert.Equal(2, ev.UnchangedCount);
            Assert.Equal(new List<string> { "0x1000" }, ev.Changed);
        }

        [Fact]
        public void PathOffsetsSentAsHexStringsAreNotReadAsDecimal()
        {
            // Tolerance for a scanner that sends the parts rather than the text.
            // Everything either half prints is hex, and a decimal reading would
            // land somewhere else entirely and still look like a valid row.
            var ev = ScanHost.Parse(
                "{\"ev\":\"path\",\"target\":\"0x0A001000\",\"root\":\"game.exe+0x100\","
              + "\"offsets\":[\"0x18\",\"0x4\"]}");
            Assert.Equal(new List<long> { 24, 4 }, ev.PathOffsets);
        }

        [Fact]
        public void APathsRunAsksForTheOneModeTheScannerAllowsAlongsideAWatch()
        {
            // A paths run excludes a search, a survey, a script and a watch-only
            // host, and the scanner refuses the combination rather than picking
            // one: getting this wrong is exit code 2, which reads on the tab as
            // "your memscan is too old" and sends the operator after the wrong
            // thing entirely.
            var req = new MemoryScanRequest
            {
                Pid = 4321,
                PathsTo = new List<string> { "0x0A001000" },
                PathKind = "float32",
                PathLabel = "Revs",
                PathExpectMin = 0,
                PathExpectMax = 30000,
                PathWatchHold = true,
                KnownString = "TRUENO GT-APEX",   // must NOT reach the command line
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 3, out string error);
            Assert.Null(error);
            Assert.Contains("--paths-to 0x0A001000", args);
            Assert.Contains("--path-depth 3", args);
            Assert.Contains("--path-window 512", args);
            Assert.Contains("--paths-kind float32", args);
            Assert.Contains("--paths-expect 0,30000", args);
            Assert.Contains("--watch-hold", args);
            Assert.DoesNotContain("--known-string", args);
            Assert.DoesNotContain("--script", args);
            Assert.DoesNotContain("--watch-only", args);
            Assert.DoesNotContain("--survey", args);
        }

        [Fact]
        public void APathsRunRefusesAnAddressThatIsAlreadyDurable()
        {
            // A module-relative address or a path resolves itself already, so a
            // pointer path would buy it nothing, and the scanner takes absolute
            // addresses only.
            var req = new MemoryScanRequest
            {
                Pid = 1,
                PathsTo = new List<string> { "game.exe+0x4A2C10" },
            };
            Assert.Null(MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        // ---- the file -------------------------------------------------------------

        [Fact]
        public void AMapSurvivesASaveAndALoadWithItsConfirmationRecordIntact()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-map-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                var store = StoreWith("0x00001000");
                store.Game = "Initial D Arcade Stage 8";
                store.Process = "InitialD8";
                store.Observe(new[] { Read("0x00001000", "TRUENO") });
                Assert.Null(store.BeginRound(MemoryFields.CarName, "change-car"));
                store.Observe(new[] { Read("0x00001000", "LEVIN") });
                store.FinishRound();
                Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name",
                                          "[game.exe+0x100]+0x18", "run-9", out var entry));
                string how = entry.ConfirmedHow;
                Assert.Null(store.Save(path));

                var back = new MemoryFieldStore();
                Assert.Null(back.Load(path));
                var field = back.Find(MemoryFields.CarName);
                Assert.NotNull(field.Entry);
                Assert.Equal("[game.exe+0x100]+0x18", field.Entry.Addr);
                Assert.Equal("Car name", field.Entry.Label);
                Assert.Equal(how, field.Entry.ConfirmedHow);
                Assert.Equal("run-9", field.Entry.Run);
                Assert.Equal("Initial D Arcade Stage 8", back.Game);
                // Nothing loaded is believed: it has to be read again first.
                Assert.Equal("not checked", field.Entry.StateWord);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void AMissingMapFileIsTheOrdinaryStateAndNotAnError()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.Load(Path.Combine(Path.GetTempPath(),
                "tf4all-no-such-" + Guid.NewGuid().ToString("N") + ".json")));
            Assert.Equal(MemoryFields.BuiltIn.Count, store.Fields.Count);
        }

        [Fact]
        public void AMangledMapFileLeavesTheCatalogStandingAndSaysWhatWentWrong()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-map-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, "{ this is not json");
                var store = new MemoryFieldStore();
                Assert.NotNull(store.Load(path));
                Assert.Equal(MemoryFields.BuiltIn.Count, store.Fields.Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void AMapFileIsNamedAfterTheProcessAndNeverAfterSomethingUnsafe()
        {
            Assert.Equal("InitialD8.json", MemoryFieldStore.FileNameFor("InitialD8.exe"));
            Assert.Equal("a_b.json", MemoryFieldStore.FileNameFor("a\\b"));
            Assert.Equal("unknown.json", MemoryFieldStore.FileNameFor(""));
        }

        [Fact]
        public void ACustomFieldGetsAKeyThatIsSafeToPutInAFile()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCustomField("Checkpoint time!", out var field));
            Assert.Equal("checkpoint-time", field.Key);
            Assert.True(field.Custom);
            // No sanity band, because nobody knows what it holds: an unknown
            // field must not be called stale for reading something surprising.
            Assert.Equal(MemorySanity.Shape.None, field.Def.Sanity.Kind);
        }

        // ---- arming ----------------------------------------------------------------

        [Fact]
        public void TheArmListCarriesEveryEntryAndEveryCandidateExactlyOnce()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCandidate(MemoryFields.Rpm, "0x00001000", "float32", 0, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.Speed, "0x00001000", "float32", 0, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.Speed, "0x00002000", "float32", 0, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x00003000", "utf16", 32, null, "", out _));
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00003000", "Car name", null, null, out _));

            var arm = store.ArmList(out int dropped);
            Assert.Equal(0, dropped);
            var addrs = new List<string>();
            foreach (var c in arm) addrs.Add(c.Addr);
            Assert.Equal(3, addrs.Count);
            Assert.Contains("0x00003000", addrs);
        }

        [Fact]
        public void TheArmListPutsTheEntriesFirstSoAStaleRowIsAlwaysRead()
        {
            // Entries are the ones that must be caught going stale, so they never
            // lose their place on the wire to a pile of candidates.
            var store = new MemoryFieldStore();
            for (int i = 0; i < MemoryFieldStore.MaxArmed + 20; i++)
                Assert.Null(store.AddCandidate(MemoryFields.Rpm, "0x" + (0x10000 + i * 0x10).ToString("X8"),
                                               "float32", 0, null, "", out _));
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x0BAD0000", "utf16", 32, null, "", out _));
            Assert.Null(store.Promote(MemoryFields.CarName, "0x0BAD0000", "Car name", null, null, out _));

            var arm = store.ArmList(out int dropped);
            Assert.Equal("0x0BAD0000", arm[0].Addr);
            Assert.True(dropped > 0);
            Assert.Equal(MemoryFieldStore.MaxArmed, arm.Count);
        }

        [Fact]
        public void AnAddressTheMapCaresAboutIsRecognisedSoItIsNotCountedAsAStranger()
        {
            var store = StoreWith("0x00001000");
            Assert.True(store.Knows("0x00001000"));
            Assert.False(store.Knows("0x00009999"));
        }

        [Fact]
        public void AConfirmedEntrysAddressCountsAsOneTheMapAskedFor()
        {
            // Entries are on the wire for every launch. Leaving them out of this
            // would report the confirmed half of the map as rows nobody asked
            // for, on every refresh, forever.
            var store = StoreWith("0x00001000");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name",
                                      "[game.exe+0x100]+0x18", null, out var entry));
            Assert.Empty(store.Find(MemoryFields.CarName).Candidates);
            Assert.True(store.Knows(entry.Addr));
        }

        [Fact]
        public void TheFieldBeingWorkedOnSurvivesASaveAndALoad()
        {
            // Coming back to a tab pointed somewhere else is how a run's
            // findings end up on the wrong field.
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-map-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                var store = StoreWith("0x00001000");
                store.SelectedKey = MemoryFields.Gear;
                Assert.Null(store.Save(path));
                var back = new MemoryFieldStore();
                Assert.Null(back.Load(path));
                Assert.Equal(MemoryFields.Gear, back.SelectedKey);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- the two halves spell an address differently ----------------------
        //
        // Measured against the real memscan, not supposed. This side pads a bare
        // address to eight hex digits so a hand-typed 0x1c02750 and a pasted
        // 0x01C02750 are one row; the scanner prints the shortest form it can. On
        // a 32-bit target every heap address is below 0x10000000, so it has a
        // leading zero here and none there and the two strings are NEVER equal.
        // Everything the scanner mints comes back in its spelling, and the flow
        // that turns a heap address into a durable map entry compares one against
        // the other.

        [Fact]
        public void TheSameHeapAddressWrittenBothWaysIsOneAddress()
        {
            // The exact pair the wire gate saw off the real scanner: the plugin
            // asked for a path to 0x0B4C0050 and was told about 0xB4C0050.
            const string plugin = "0x0B4C0050";
            const string scanner = "0xB4C0050";
            Assert.NotEqual(plugin, scanner);
            Assert.True(MemoryRoutes.SameAddress(plugin, scanner));
            Assert.Equal(MemoryRoutes.AddressKey(plugin), MemoryRoutes.AddressKey(scanner));
        }

        [Fact]
        public void APathWhoseRootIsAnAbsoluteAddressComparesTheSameWay()
        {
            // The padding difference is inside the brackets, so a path spec with
            // an absolute root carries it too.
            Assert.True(MemoryRoutes.SameAddress("[[0xB4C0050]+0x18]+0x2C", "[[0x0B4C0050]+0x18]+0x2C"));
        }

        [Fact]
        public void TwoDifferentAddressesStillDoNotCompareEqual()
        {
            // The looser comparison must not become no comparison.
            Assert.False(MemoryRoutes.SameAddress("0x0B4C0050", "0x0B4C0054"));
            Assert.False(MemoryRoutes.SameAddress("game.exe+0x400", "other.dll+0x400"));
            Assert.False(MemoryRoutes.SameAddress("[game.exe+0x400]+0x18", "[game.exe+0x400]+0x1C"));
            Assert.False(MemoryRoutes.SameAddress("0x0B4C0050", null));
            Assert.False(MemoryRoutes.SameAddress("", "0x0B4C0050"));
        }

        [Fact]
        public void AModuleRelativeAddressComparesEqualToItselfEitherCase()
        {
            // The scanner lower-cases a module name in one place and keeps its
            // case in another, and a row lost over a capital letter looks exactly
            // like a value that stopped updating.
            Assert.True(MemoryRoutes.SameAddress("Game.exe+0x4A2C10", "game.exe+0x4a2c10"));
        }

        [Fact]
        public void SomethingThatIsNotAnAddressStillEqualsItself()
        {
            // AddressKey has to be total: it is used as a dictionary key, and a
            // key that throws or returns null on junk would lose the row.
            Assert.True(MemoryRoutes.SameAddress("not an address", "not an address"));
            Assert.Equal("", MemoryRoutes.AddressKey(null));
        }

        // ---- the wire has a ceiling and the map has to respect it -------------

        [Fact]
        public void TheMapNeverArmsMoreThanTheScannersWatchListHolds()
        {
            // The scanner refuses row 257 outright. A row past that is never read,
            // and from here that is indistinguishable from a row that was read and
            // did not move, so a round would keep it forever.
            var store = new MemoryFieldStore();
            for (int i = 0; i < 400; i++)
                store.AddCandidate(MemoryFields.CarName,
                                   "0x" + (0x01000000 + i * 4).ToString("X8"), "int32", 0, null, "c", out _);
            var armed = store.ArmList(out int dropped);
            Assert.Equal(MemoryFieldStore.ScannerWatchRows, armed.Count);
            Assert.Equal(400 - MemoryFieldStore.ScannerWatchRows, dropped);
        }

        [Fact]
        public void ABudgetLeavesRoomForTheWatchListsOwnRows()
        {
            // One list, one ceiling, shared with the rows the operator curated by
            // hand. The map gets what is left rather than a number nobody checked.
            var store = new MemoryFieldStore();
            for (int i = 0; i < 60; i++)
                store.AddCandidate(MemoryFields.CarName,
                                   "0x" + (0x01000000 + i * 4).ToString("X8"), "int32", 0, null, "c", out _);
            var armed = store.ArmList(40, out int dropped);
            Assert.Equal(40, armed.Count);
            Assert.Equal(20, dropped);
        }

        [Fact]
        public void AConfirmedEntryIsArmedEvenWhenTheBudgetIsSpent()
        {
            // An entry that stops being checked is a map that has quietly stopped
            // saying whether it still works, which is the one thing this file
            // exists to prevent.
            var store = StoreWith("0x00001000", "0x00001004", "0x00001008");
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car", null, "run", out _));
            store.AddCandidate(MemoryFields.Gear, "0x00002000", "int32", 0, null, "g", out _);
            store.AddCandidate(MemoryFields.Gear, "0x00002004", "int32", 0, null, "g", out _);
            var armed = store.ArmList(0, out int dropped);
            Assert.Single(armed);
            Assert.Equal("0x00001000", armed[0].Addr);
            Assert.Equal(2, dropped);
        }

        // ---- the two halves have to measure the same window -------------------

        [Fact]
        public void RebasingARoundThrowsAwayWhatItHadAndStartsAgain()
        {
            // Called when the scanner acknowledges the round, so both halves
            // baseline on the same refresh. Without it anything that moves between
            // the button and the scanner's next refresh is counted by one half and
            // not the other, and the cross-check calls an honest round unproved.
            var store = StoreWith("0x00001000");
            store.Observe(new[] { Read("0x00001000", "AE86") });
            Assert.Null(store.BeginRound(MemoryFields.CarName, MemoryEliminationActions.ChangeCar.Id));
            // Something moved before the scanner had even started measuring.
            store.Observe(new[] { Read("0x00001000", "RX-7") });
            Assert.True(store.RebaseRound());
            // From here on it holds still, so the round must say it did not move.
            store.Observe(new[] { Read("0x00001000", "RX-7") });
            store.Observe(new[] { Read("0x00001000", "RX-7") });
            var result = store.FinishRound();
            Assert.Equal(0, result.Unread);
            Assert.Equal(0, result.After);
            Assert.Equal(1, result.Eliminated);
        }

        [Fact]
        public void RebasingWithNoRoundOpenIsRefusedRatherThanIgnored()
        {
            // The ack for a round-start can arrive after the round was cancelled.
            var store = StoreWith("0x00001000");
            Assert.False(store.RebaseRound());
        }

        // ---- a map that came off disk has to work like one that did not -------

        [Fact]
        public void CandidatesLoadedFromTheFileAreStillReadWhenTheWatchReportsThem()
        {
            // Candidates come straight off the JSON rather than through
            // AddCandidate, so nothing indexes them on the way in. Miss that and
            // the address index is empty after a restart: every reading is filed
            // as belonging to nobody, and a round judges nothing while reporting
            // that it kept everything because it could not read it. The count
            // never falls, and nothing on the tab can say why.
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-map-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                var store = StoreWith("0x00001000", "0x00001004");
                Assert.Null(store.Save(path));

                var back = new MemoryFieldStore();
                Assert.Null(back.Load(path));
                Assert.True(back.Knows("0x00001000"), "a loaded candidate is an address the map asked for");

                int mine = back.Observe(new[] { Read("0x00001000", "AE86"), Read("0x00001004", "steady") });
                Assert.Equal(2, mine);

                Assert.Null(back.BeginRound(MemoryFields.CarName, MemoryEliminationActions.ChangeCar.Id));
                back.Observe(new[] { Read("0x00001000", "AE86"), Read("0x00001004", "steady") });
                back.Observe(new[] { Read("0x00001000", "RX-7"), Read("0x00001004", "steady") });
                var result = back.FinishRound();
                Assert.Equal(0, result.Unread);
                Assert.Equal(1, result.Eliminated);
                Assert.Equal(1, result.After);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
