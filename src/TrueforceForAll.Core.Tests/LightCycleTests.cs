// The wheel button's running order, and where in it we stand.
//
// Every test in the "bugs that actually happened" section below is a defect the
// owner found on the rim in a single week, because this logic lived as a private
// method inside an 18,000-line file where nothing could reach it. They are
// written as regressions, in the user's words, so the reason each rule exists
// survives the next person who thinks it looks redundant.

using System;
using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LightCycleTests
    {
        private const int Slots = 5;

        private static LightPattern Pat(string id, int slot = -1)
            => new LightPattern { Id = id, Name = id, Slot = slot };

        private static bool[] Programmed(params int[] filled)
        {
            var a = new bool[Slots];
            foreach (int i in filled) a[i] = true;
            return a;
        }

        private static List<LightCycleStop> Build(
            bool auto = false, int stage = 4, bool[] programmed = null,
            params LightPattern[] patterns)
            => LightCycle.Build(auto, stage, programmed ?? new bool[Slots],
                                patterns, Slots, e => "Effect " + e);

        private static string[] Labels(IEnumerable<LightCycleStop> stops)
            => stops.Select(s => s.Label).ToArray();

        // ---------------- bugs that actually happened ----------------

        [Fact]
        public void EachPatternAppearsExactlyOnce()
        {
            // "each pattern appears once in the cycle". A pattern living in a
            // programmed slot of its own was being added twice: once as that slot,
            // where the base shows the slot's stored name, and once through the
            // stage under its real name. Cycling visited all five top patterns
            // twice per lap.
            var own = Pat("deep-water", slot: 1);
            var lib = Pat("sunset");
            var stops = Build(stage: 4, programmed: Programmed(1), patterns: new[] { own, lib });

            Assert.Single(stops.Where(s => s.Pattern != null && s.Pattern.Id == "deep-water")
                               .Concat(stops.Where(s => s.Pattern == null && s.Effect == 5 + 1)));
        }

        [Fact]
        public void ThePatternInTheLentSlotStillAppearsUnderItsOwnName()
        {
            // The stage is skipped in the slot section precisely so its pattern
            // shows up in the library section instead, under its real name rather
            // than the slot's stored one.
            var staged = Pat("ember", slot: 4);
            var stops = Build(stage: 4, programmed: Programmed(4), patterns: new[] { staged });

            Assert.DoesNotContain(stops, s => s.Pattern == null && s.Effect == 5 + 4);
            Assert.Contains(stops, s => s.Pattern != null && s.Pattern.Id == "ember");
        }

        [Fact]
        public void AutoIsReachableByCycling()
        {
            // "auto never gets cycled to". Without a stop of its own, a user who
            // cycles away from the car's colours can never get back: every other
            // stop pins something, and pinning is what turns Auto off.
            Assert.Contains(Build(auto: true), s => s.Auto);
        }

        [Fact]
        public void AutoIsTheFirstStopWhereThePickersPutIt()
        {
            Assert.True(Build(auto: true)[0].Auto);
        }

        [Fact]
        public void TheCycleStepsPastTheFirstSweepInsteadOfToggling()
        {
            // "cycling when in a car now only switches between auto and inside
            // out". The Auto stop has a null Pattern and Effect 0, so a lookup
            // that did not exclude it matched Auto whenever the wheel's known
            // selection read 0. Position reset to 0 on every press, and the cycle
            // could only ever step one place.
            var stops = Build(auto: true);

            int at = LightCycle.PositionOf(stops, LightShowing.WheelEffect,
                                           currentPatternId: null, knownSelection: 0, lastKey: null);

            Assert.NotEqual(0, at);   // must NOT have matched the Auto stop
        }

        [Fact]
        public void CyclingInACarStepsOnFromWhereWeLastLanded()
        {
            // In a car the wheel cannot say what is selected: the game's FFB owns
            // the pipe, so a pick is staged rather than written and the known
            // selection never moves. The search failed, position fell back to an
            // end, and the next press wrapped to 0. That is the "toggles between
            // one stop and Auto" report.
            var stops = Build(auto: true);
            string third = LightCycle.KeyFor(stops[2]);

            int at = LightCycle.PositionOf(stops, LightShowing.WheelEffect,
                                           currentPatternId: null, knownSelection: -1, lastKey: third);

            Assert.Equal(2, at);
            Assert.Equal(stops[3].Label, LightCycle.Step(stops, at, 1).Label);
        }

        [Fact]
        public void AutoIsAskedBeforeTheLibrary()
        {
            // While the car's own colours are up, a stage slot may well be lent
            // out too, so both flags can read true at once. The library test would
            // otherwise claim the position and the cycle would step off from the
            // wrong place.
            var stops = Build(auto: true, patterns: new[] { Pat("sunset") });

            int at = LightCycle.PositionOf(stops, LightShowing.CarAutoColors,
                                           currentPatternId: "sunset", knownSelection: 0, lastKey: null);

            Assert.True(stops[at].Auto);
        }

        // ---------------- what is showing ----------------
        //
        // One answer, derived in one place. Three separate flags used to answer
        // this between them and nothing owned the result, which is how a car with
        // no published data kept the PREVIOUS car's colours.

        [Fact]
        public void CarColoursWinOverALentSlot()
        {
            // Both are true at once whenever auto colours are painted into a
            // borrowed slot, and only one of them is what the user sees.
            Assert.Equal(LightShowing.CarAutoColors,
                LightCycle.Showing(autoColorsApplied: true, carChoicePinned: false,
                                   libraryPatternShowing: true));
        }

        [Fact]
        public void APatternPinnedToThisCarIsNotAuto()
        {
            // Pinning is precisely what turns Auto off, so the pinned pattern is
            // what is showing even though our colours are still on the slot.
            Assert.Equal(LightShowing.LibraryPattern,
                LightCycle.Showing(autoColorsApplied: true, carChoicePinned: true,
                                   libraryPatternShowing: true));
        }

        [Fact]
        public void NothingOfOursMeansTheWheelsOwnEffect()
        {
            Assert.Equal(LightShowing.WheelEffect,
                LightCycle.Showing(false, false, false));
        }

        [Fact]
        public void AStalePatternIdStillLandsOnAPatternStop()
        {
            // The bug this guards: the auto path used to leave CurrentId naming a
            // pattern it had painted over. An id that matches nothing must fall
            // back to some pattern stop rather than reporting "no idea", which
            // would send the cycle to an end.
            var stops = Build(patterns: new[] { Pat("sunset"), Pat("ember") });

            int at = LightCycle.PositionOf(stops, LightShowing.LibraryPattern,
                                           currentPatternId: "deleted-pattern",
                                           knownSelection: -1, lastKey: null);

            Assert.True(at >= 0);
            Assert.NotNull(stops[at].Pattern);
        }

        [Fact]
        public void AMissingPatternIdIsTreatedTheSameWay()
        {
            var stops = Build(patterns: new[] { Pat("sunset") });

            int at = LightCycle.PositionOf(stops, LightShowing.LibraryPattern,
                                           currentPatternId: null, knownSelection: -1, lastKey: null);

            Assert.NotNull(stops[at].Pattern);
        }

        // ---------------- the running order ----------------

        [Fact]
        public void TheWheelsOwnEffectsComeFirst()
        {
            Assert.Equal(new[] { "Effect 1", "Effect 2", "Effect 3", "Effect 4" },
                         Labels(Build()).Take(4));
        }

        [Fact]
        public void ABlankSlotIsNotAStop()
        {
            // A blank factory slot as a stop is a dark strip and a press that
            // reads as broken.
            var stops = Build(stage: 4, programmed: Programmed(0, 2));

            Assert.Contains(stops, s => s.Pattern == null && s.Effect == 5 + 0);
            Assert.Contains(stops, s => s.Pattern == null && s.Effect == 5 + 2);
            Assert.DoesNotContain(stops, s => s.Pattern == null && s.Effect == 5 + 1);
        }

        [Fact]
        public void WithNoSlotToBorrowTheLibraryIsNotOffered()
        {
            // Nothing to show them in, so offering them would be a press that
            // changes nothing.
            var stops = Build(stage: -1, patterns: new[] { Pat("sunset") });

            Assert.DoesNotContain(stops, s => s.Pattern != null);
        }

        [Fact]
        public void LibraryPatternsAllRideTheOneLentSlot()
        {
            var stops = Build(stage: 3, patterns: new[] { Pat("a"), Pat("b") });

            Assert.All(stops.Where(s => s.Pattern != null), s => Assert.Equal(5 + 3, s.Effect));
        }

        [Fact]
        public void ANullPatternInTheLibraryIsSkippedRatherThanCrashing()
        {
            var stops = Build(stage: 4, patterns: new LightPattern[] { null, Pat("sunset") });

            Assert.Single(stops.Where(s => s.Pattern != null));
        }

        [Fact]
        public void AShortProgrammedMapDoesNotThrow()
        {
            // Slot reads can fail; a truncated map must not take the cycle with it.
            var stops = LightCycle.Build(false, 4, new bool[2], new[] { Pat("sunset") },
                                         Slots, e => "Effect " + e);

            Assert.NotEmpty(stops);
        }

        // ---------------- stepping ----------------

        [Fact]
        public void SteppingWrapsAtBothEnds()
        {
            // Driven from a wheel button, where hitting an invisible end feels
            // broken.
            var stops = Build();

            Assert.Equal(stops[0].Label, LightCycle.Step(stops, stops.Count - 1, 1).Label);
            Assert.Equal(stops[stops.Count - 1].Label, LightCycle.Step(stops, 0, -1).Label);
        }

        [Fact]
        public void AFirstPressWithNothingKnownStillLandsSomewhere()
        {
            var stops = Build();

            // Forwards lands on the first stop, backwards on the last: each
            // direction starts from the end it is coming from, so a first press
            // moves INTO the list rather than off it.
            Assert.Equal(stops[0].Label, LightCycle.Step(stops, -1, 1).Label);
            Assert.Equal(stops[stops.Count - 1].Label, LightCycle.Step(stops, -1, -1).Label);
        }

        [Fact]
        public void SteppingAnEmptyCycleIsNotACrash()
        {
            Assert.Null(LightCycle.Step(new List<LightCycleStop>(), 0, 1));
            Assert.Equal(-1, LightCycle.PositionOf(new List<LightCycleStop>(),
                                                   LightShowing.WheelEffect, null, 0, null));
        }

        // ---------------- keys ----------------
        //
        // The running order changes as slots fill and cars come and go, so a bare
        // index is not a position we can remember between presses.

        [Fact]
        public void EachKindOfStopHasItsOwnKind_OfKey()
        {
            Assert.Equal("auto",     LightCycle.KeyFor(new LightCycleStop { Auto = true }));
            Assert.Equal("p:sunset", LightCycle.KeyFor(new LightCycleStop { Pattern = Pat("sunset") }));
            Assert.Equal("e:7",      LightCycle.KeyFor(new LightCycleStop { Effect = 7 }));
        }

        [Fact]
        public void AKeySurvivesTheListBeingRebuilt()
        {
            // The point of keys: a slot filling up between two presses must not
            // lose our place.
            var sunset = Pat("sunset");
            var before = Build(stage: 4, programmed: Programmed(), patterns: new[] { sunset });
            string key = LightCycle.KeyFor(before.First(s => s.Pattern != null));

            var after = Build(stage: 4, programmed: Programmed(0, 1), patterns: new[] { sunset });
            int at = LightCycle.PositionOf(after, LightShowing.WheelEffect, null, -1, key);

            Assert.True(at >= 0);
            Assert.Equal("sunset", after[at].Pattern.Id);
        }

        [Fact]
        public void NoStopHasNoKey()
        {
            Assert.Null(LightCycle.KeyFor(null));
        }
    }
}
