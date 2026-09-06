using System;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>
    /// The map's rows carry the value to search for, and one action searches
    /// every row that has one.
    ///
    /// Three things here go wrong silently, which is why they are pinned rather
    /// than left to the screen:
    ///
    ///   A value that reaches no search. The whole promise of a box on a row is
    ///   that filling it in makes that field part of the next search. A kind the
    ///   scanner cannot search must therefore offer no box at all and say why,
    ///   or the operator types a lap time, gets nothing, and concludes the field
    ///   is absent when nobody ever looked for it.
    ///
    ///   A finding on the wrong field. One sweep now carries several needles, so
    ///   attribution decides which field a hit belongs to. Put them all on one
    ///   field and searching several at once is worse than useless: it looks
    ///   like the answer and is not.
    ///
    ///   A needle that never reaches the command line. Blank, repeated or past
    ///   the scanner's ceiling, all of which read afterwards as "that value is
    ///   not in memory".
    /// </summary>
    public class MemoryFieldValueTests
    {
        // ---- what kind of value a field carries -------------------------------

        [Fact]
        public void TheCatalogSaysWhichFieldsCanBeSearchedByTypingAValue()
        {
            // The three the scanner actually has a search for today, and the
            // ones it does not. This test is the statement of that contract: a
            // field moves out of the last group only when a search exists.
            Assert.Equal(MemoryValueKind.Text, MemoryFields.ByKey(MemoryFields.CarName).ValueKind);
            Assert.Equal(MemoryValueKind.Text, MemoryFields.ByKey(MemoryFields.PlayerName).ValueKind);
            Assert.Equal(MemoryValueKind.Static, MemoryFields.ByKey(MemoryFields.Redline).ValueKind);
            Assert.Equal(MemoryValueKind.GearOrder, MemoryFields.ByKey(MemoryFields.Gear).ValueKind);

            Assert.Equal(MemoryValueKind.Displayed, MemoryFields.ByKey(MemoryFields.LapTime).ValueKind);
            Assert.Equal(MemoryValueKind.Displayed, MemoryFields.ByKey(MemoryFields.TimeLeft).ValueKind);
            Assert.Equal(MemoryValueKind.Displayed, MemoryFields.ByKey(MemoryFields.Speed).ValueKind);
            Assert.Equal(MemoryValueKind.None, MemoryFields.ByKey(MemoryFields.Rpm).ValueKind);

            Assert.True(MemoryValueKinds.Searchable(MemoryValueKind.Text));
            Assert.True(MemoryValueKinds.Searchable(MemoryValueKind.Static));
            Assert.True(MemoryValueKinds.Searchable(MemoryValueKind.GearOrder));
            Assert.False(MemoryValueKinds.Searchable(MemoryValueKind.Displayed));
            Assert.False(MemoryValueKinds.Searchable(MemoryValueKind.None));
        }

        [Fact]
        public void AClockLockedFieldRefusesATypedValueAndSaysThereIsNoSearchForIt()
        {
            // The lap time is the case this rule exists for. There is no
            // clock-locked search yet, so a box that accepted one would be a box
            // that does nothing, and the run that followed would teach the
            // operator the wrong thing.
            var store = new MemoryFieldStore();
            string problem = store.SetKnownValue(MemoryFields.LapTime, "1:23.456");
            Assert.NotNull(problem);
            Assert.Contains("no search", problem, StringComparison.OrdinalIgnoreCase);
            Assert.False(store.Find(MemoryFields.LapTime).HasKnownValue);
            Assert.False(store.PlanSearch().Any);
        }

        [Fact]
        public void ThePlayerNameIsAFieldOfItsOwnAndIsSearchedAsText()
        {
            // The owner asked for this by name, and it is the stronger probe: a
            // name the player typed has to be stored as characters, where a car
            // name can be baked into a texture.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            var plan = store.PlanSearch();
            Assert.Single(plan.Needles);
            Assert.Equal(MemoryFields.PlayerName, plan.Needles[0].FieldKey);
            Assert.Equal("MHYTEE", plan.Needles[0].Text);
        }

        [Fact]
        public void AOneCharacterNeedleIsRefusedBecauseItMatchesEverywhere()
        {
            var store = new MemoryFieldStore();
            Assert.NotNull(store.SetKnownValue(MemoryFields.CarName, "A"));
            Assert.False(store.Find(MemoryFields.CarName).HasKnownValue);
        }

        [Fact]
        public void AFixedNumberOutsideWhatTheFieldHoldsIsRefusedRatherThanSearchedFor()
        {
            // A redline of 74 would be searched for everywhere and would come
            // back with thousands of hits, which reads as a working search.
            var store = new MemoryFieldStore();
            Assert.NotNull(store.SetKnownValue(MemoryFields.Redline, "7"));
            Assert.Null(store.SetKnownValue(MemoryFields.Redline, "7400"));
            Assert.Equal("7400", store.Find(MemoryFields.Redline).KnownValue);
        }

        [Fact]
        public void AGearOrderIsNormalisedAndOneGearIsNotASequence()
        {
            var store = new MemoryFieldStore();
            Assert.NotNull(store.SetKnownValue(MemoryFields.Gear, "3"));
            Assert.NotNull(store.SetKnownValue(MemoryFields.Gear, "1,2,three"));
            Assert.Null(store.SetKnownValue(MemoryFields.Gear, " 1 2  3,4 ,3,2 "));
            Assert.Equal("1,2,3,4,3,2", store.Find(MemoryFields.Gear).KnownValue);
        }

        [Fact]
        public void ClearingAValueIsAlwaysAllowedAndIsNeverAnError()
        {
            // An empty field is not a mistake, it is a field that is not part of
            // this search. Refusing the empty string would make a row impossible
            // to take back out of a search.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "   "));
            Assert.False(store.Find(MemoryFields.CarName).HasKnownValue);
            Assert.Null(store.FirstValueProblem());
        }

        // ---- one action, every field ------------------------------------------

        [Fact]
        public void OneSearchCarriesEveryTextValueThatIsFilledIn()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            Assert.Null(store.SetKnownValue(MemoryFields.Redline, "7400"));

            var plan = store.PlanSearch();
            Assert.Equal(2, plan.Needles.Count);
            var keys = new List<string>();
            foreach (var n in plan.Needles) keys.Add(n.FieldKey);
            Assert.Contains(MemoryFields.CarName, keys);
            Assert.Contains(MemoryFields.PlayerName, keys);
            Assert.True(plan.HasStatic);
            Assert.Equal(MemoryFields.Redline, plan.StaticFieldKey);
            Assert.Equal(7400, plan.StaticValue);
            Assert.Empty(plan.Problems);
        }

        [Fact]
        public void OnlyOneFixedNumberRidesOnARunAndTheOtherIsNamedRatherThanDropped()
        {
            // The scanner takes one --known-redline. A second one silently left
            // out is a value the operator typed and then waited for.
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCustomField("Torque peak", MemoryValueKind.Static, "4200", out _));
            Assert.Null(store.SetKnownValue(MemoryFields.Redline, "7400"));

            // Catalog order first, then whatever was added, so the built-in
            // redline is the one that rides and the added field is the one
            // named as left out.
            var plan = store.PlanSearch();
            Assert.True(plan.HasStatic);
            Assert.Equal(MemoryFields.Redline, plan.StaticFieldKey);
            Assert.Single(plan.StaticNotThisRun);
            Assert.Equal("Torque peak", plan.StaticNotThisRun[0]);
            Assert.Contains("one fixed number", plan.Describe(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AConfirmedFieldTakesNoPartInASearchEvenWithItsValueStillOnTheRow()
        {
            // Searching for something already in the map would bury the row that
            // was right under a hundred fresh candidates.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.AddCandidate(MemoryFields.CarName, "0x00001000", "utf16", 32, null, "found", out _));
            Assert.Null(store.Promote(MemoryFields.CarName, "0x00001000", "Car name", null, "run-1", out _));

            var plan = store.PlanSearch();
            Assert.Empty(plan.Needles);
            Assert.False(plan.Any);
            Assert.Equal("TRUENO GT-APEX", store.Find(MemoryFields.CarName).KnownValue);
        }

        [Fact]
        public void AGearOrderAloneIsNotSomethingTheNoDrivingSearchCanDo()
        {
            // The scanner asks for declared gears ONE AT A TIME and waits for
            // the operator to select each. A no-driving run carrying nothing but
            // a gear order would sit there waiting for somebody who thought they
            // had pressed a search button, and a request carrying nothing the
            // scanner counts as a known value falls through to the driving
            // script instead.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.Gear, "1,2,3,4"));
            var plan = store.PlanSearch();
            Assert.True(plan.Any);
            Assert.False(plan.AnyWithoutDriving);
            Assert.Contains("driving run", plan.Describe(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OneTextValueIsEnoughForASearchThatAsksForNothingAtTheWheel()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            Assert.True(store.PlanSearch().AnyWithoutDriving);
            store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.Redline, "7400"));
            Assert.True(store.PlanSearch().AnyWithoutDriving);
        }

        [Fact]
        public void NeedlesPastTheScannersCeilingAreNamedRatherThanDropped()
        {
            var store = new MemoryFieldStore();
            for (int i = 0; i < MemoryValueKinds.MaxTextNeedles + 3; i++)
                Assert.Null(store.AddCustomField("Text " + i.ToString(), MemoryValueKind.Text,
                                                 "needle" + i.ToString(), out _));
            var plan = store.PlanSearch();
            Assert.Equal(MemoryValueKinds.MaxTextNeedles, plan.Needles.Count);
            Assert.Equal(3, plan.NeedlesNotThisRun.Count);
            Assert.Contains("NOT this run", plan.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public void AFieldAddedByHandCarriesItsKindAndItsValueInOneAction()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.AddCustomField("Course name", MemoryValueKind.Text, "AKINA DOWNHILL", out var field));
            Assert.True(field.Custom);
            Assert.Equal(MemoryValueKind.Text, field.ValueKind);
            Assert.Equal("AKINA DOWNHILL", field.KnownValue);
            Assert.Single(store.PlanSearch().Needles);
        }

        [Fact]
        public void AFieldStillLandsWhenTheValueTypedWithItCannotBeUsed()
        {
            // Refusing the row as well would make the operator type the name
            // again to keep the field they asked for.
            var store = new MemoryFieldStore();
            string problem = store.AddCustomField("Course name", MemoryValueKind.Text, "A", out var field);
            Assert.NotNull(problem);
            Assert.NotNull(field);
            Assert.NotNull(store.Find("course-name"));
            Assert.False(field.HasKnownValue);
        }

        [Fact]
        public void ABadValueIsReportedByNameSoARunCanBeStoppedBeforeAnybodyDrives()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.FirstValueProblem());
            // Written past the checker, the way a map file edited by hand would
            // arrive.
            store.Find(MemoryFields.Redline).KnownValue = "12";
            string problem = store.FirstValueProblem();
            Assert.NotNull(problem);
            Assert.Contains("Redline", problem, StringComparison.Ordinal);
        }

        // ---- which field a finding belongs to ---------------------------------

        [Fact]
        public void EachFindingGoesToTheFieldWhoseValueFoundIt()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            Assert.Null(store.SetKnownValue(MemoryFields.Redline, "7400"));
            var router = MemoryFieldStore.RouterFor(store.PlanSearch(), MemoryFields.Rpm);

            Assert.Equal(MemoryFields.CarName,
                router.Route("TRUENO GT-APEX", "car-name", "string"));
            Assert.Equal(MemoryFields.PlayerName,
                router.Route("MHYTEE", "car-name", "string"));
            // What POINTS at the name belongs to the same field: it is the whole
            // reason the name was worth finding.
            Assert.Equal(MemoryFields.PlayerName,
                router.Route("MHYTEE", "car-table-pointer", "ptr32"));
            // A search with no needle of its own is placed by its role.
            Assert.Equal(MemoryFields.Redline, router.Route(null, "redline", "int32"));
            Assert.Equal(0, router.Unattributed);
        }

        [Fact]
        public void TheNeedleBeatsTheLabelBecauseBothNamesComeBackLabelledTheSame()
        {
            // A player name and a car name arrive through the same code path
            // with the same label. Reading the label first would put every hit
            // on one field and look exactly like a working search.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            var router = MemoryFieldStore.RouterFor(store.PlanSearch(), MemoryFields.CarName);
            Assert.Equal(MemoryFields.PlayerName, router.Route("MHYTEE", "car-name", "string"));
        }

        [Fact]
        public void AScannerTooOldToAttributeStillLandsASingleNeedlesFindings()
        {
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            var router = MemoryFieldStore.RouterFor(store.PlanSearch(), MemoryFields.Rpm);
            Assert.Equal(MemoryFields.PlayerName, router.Route(null, "car-name", "string"));
            Assert.Equal(0, router.Unattributed);
        }

        [Fact]
        public void AFindingThatCannotBePlacedGoesToTheSelectedFieldAndIsCounted()
        {
            // Never dropped: a run that found something must not lose it. But it
            // is counted, because a pile of candidates on the wrong field looks
            // exactly like a field that really did match.
            var store = new MemoryFieldStore();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
            var router = MemoryFieldStore.RouterFor(store.PlanSearch(), MemoryFields.Speed);
            Assert.Equal(MemoryFields.Speed, router.Route(null, "car-name", "string"));
            Assert.Equal(1, router.Unattributed);
        }

        [Fact]
        public void APressDrivenGearRunPlacesItsFindingsWithNoNeedleAtAll()
        {
            // Nothing is declared on that path, so there is no plan entry and no
            // needle: the role is registered by hand or every gear finding falls
            // through to whatever was selected.
            var router = MemoryFieldStore.RouterFor(new MemoryFieldSearch(), MemoryFields.CarName);
            router.ForRole("gear", MemoryFields.Gear);
            Assert.Equal(MemoryFields.Gear, router.Route(null, "gear", "int32"));
        }

        // ---- the wire ----------------------------------------------------------

        [Fact]
        public void EveryNeedleReachesTheCommandLineAsItsOwnKnownStringFlag()
        {
            var req = new MemoryScanRequest
            {
                Pid = 4321,
                KnownString = "TRUENO GT-APEX",
                KnownStrings = new List<string> { "MHYTEE", "AKINA DOWNHILL" },
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Contains("--known-string \"TRUENO GT-APEX\"", args);
            Assert.Contains("--known-string MHYTEE", args);
            Assert.Contains("--known-string \"AKINA DOWNHILL\"", args);
            // A known-value run performs no script: the scanner's known search
            // returns before the recorder starts.
            Assert.Contains("--script none", args);
        }

        [Fact]
        public void TheSingleNeedleFormStillBuildsExactlyWhatItAlwaysDid()
        {
            // The scanner's own gates drive the single form, so the commonest
            // run on this tab has to stay the shape that is tested end to end
            // over there.
            var req = new MemoryScanRequest { Pid = 1, KnownString = "TRUENO GT-APEX" };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Equal(1, CountOf(args, "--known-string"));
            Assert.True(req.AnyKnownValue);
            Assert.True(req.AnyKnownString);
        }

        [Fact]
        public void BlanksAndRepeatsNeverReachTheCommandLine()
        {
            // A repeated needle would double the pointer-follow work for
            // nothing, and a blank one is a usage error over there, which reads
            // on the tab as "your memscan is too old".
            var req = new MemoryScanRequest
            {
                Pid = 1,
                KnownString = "MHYTEE",
                KnownStrings = new List<string> { "  ", "MHYTEE", null, "AKINA" },
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Equal(2, CountOf(args, "--known-string"));
            Assert.Contains("--known-string AKINA", args);
        }

        [Fact]
        public void DifferentCaseIsADifferentSearchAndBothGoOut()
        {
            // A REPEAT IS BYTE FOR BYTE. The scanner's text search is case
            // sensitive on purpose, so "MHYTEE" and "mhytee" are two different
            // searches over there and both are worth running: a game that
            // upper-cases a name for the leaderboard and keeps the typed form in
            // the save file has both in memory, at different addresses, meaning
            // different things.
            //
            // This was measured rather than reasoned about. Folding case here
            // sent ONE needle for two rows, and the router folded case too, so
            // the row that typed the lowercase form was handed the uppercase
            // row's answer. The wire gate reproduces it against the harness's
            // lowercase decoy plant.
            var req = new MemoryScanRequest
            {
                Pid = 1,
                KnownString = "MHYTEE",
                KnownStrings = new List<string> { "mhytee" },
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Equal(2, CountOf(args, "--known-string"));
            Assert.Contains("--known-string MHYTEE", args);
            Assert.Contains("--known-string mhytee", args);
        }

        [Fact]
        public void TheNeedleCountIsHeldToTheScannersOwnCeiling()
        {
            // A refusal on the command line takes the whole run down rather than
            // costing one needle.
            var req = new MemoryScanRequest { Pid = 1, KnownStrings = new List<string>() };
            for (int i = 0; i < MemoryScanArgs.MaxKnownStrings + 5; i++)
                req.KnownStrings.Add("needle" + i.ToString());
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Equal(MemoryScanArgs.MaxKnownStrings, CountOf(args, "--known-string"));
        }

        [Fact]
        public void APathsRunStillCarriesNoNeedlesAtAll()
        {
            // The scanner refuses the pair outright: a search is how you GET the
            // address a paths run starts from.
            var req = new MemoryScanRequest
            {
                Pid = 1,
                PathsTo = new List<string> { "0x0A001000" },
                KnownString = "TRUENO GT-APEX",
                KnownStrings = new List<string> { "MHYTEE" },
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.DoesNotContain("--known-string", args);
        }

        [Fact]
        public void AFindingCarriesTheNeedleThatProducedItOffTheWire()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"finding\",\"address\":\"0x01C02750\",\"type\":\"string\",\"label\":\"car-name\"," +
                "\"confidence\":\"UNPROVEN\",\"evidence\":\"encoding utf16\",\"needle\":\"MHYTEE\"}");
            Assert.NotNull(ev);
            Assert.Equal("MHYTEE", ev.Needle);
        }

        [Fact]
        public void AFindingWithNoNeedleIsNotAParseFailure()
        {
            // A redline finding has no needle, and neither does anything from a
            // scanner built before the flag was repeatable.
            var ev = ScanHost.Parse(
                "{\"ev\":\"finding\",\"address\":\"0x01C02750\",\"type\":\"int32\",\"label\":\"redline\"}");
            Assert.NotNull(ev);
            Assert.Null(ev.Needle);
        }

        // ---- the file ----------------------------------------------------------

        [Fact]
        public void TheValuesAndACustomFieldsKindSurviveASaveAndALoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-values-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                var store = new MemoryFieldStore();
                Assert.Null(store.SetKnownValue(MemoryFields.PlayerName, "MHYTEE"));
                Assert.Null(store.AddCustomField("Course name", MemoryValueKind.Text, "AKINA", out _));
                Assert.Null(store.Save(path));

                var back = new MemoryFieldStore();
                Assert.Null(back.Load(path));
                Assert.Equal("MHYTEE", back.Find(MemoryFields.PlayerName).KnownValue);
                var custom = back.Find("course-name");
                Assert.NotNull(custom);
                Assert.Equal(MemoryValueKind.Text, custom.ValueKind);
                Assert.Equal("AKINA", custom.KnownValue);
                Assert.Equal(2, back.PlanSearch().Needles.Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void AMapWrittenBeforeThisRoundLoadsWithNoValuesAndSearchesForNothing()
        {
            // The older file has no "known" and no "valueKind" on any field, and
            // it must come back as a map with nothing typed rather than as a map
            // that refuses to load.
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-values-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "game.json");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                    "{\"version\":1,\"process\":\"InitialD8\",\"fields\":[" +
                    "{\"key\":\"car-name\",\"name\":\"Car name\",\"custom\":false,\"candidates\":[]}," +
                    "{\"key\":\"lap-count\",\"name\":\"Lap count\",\"custom\":true,\"candidates\":[]}]}");
                var store = new MemoryFieldStore();
                Assert.Null(store.Load(path));
                Assert.False(store.Find(MemoryFields.CarName).HasKnownValue);
                var custom = store.Find("lap-count");
                Assert.NotNull(custom);
                Assert.Equal(MemoryValueKind.None, custom.ValueKind);
                Assert.False(store.PlanSearch().Any);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void AValueWithAwkwardCharactersSurvivesTheCommandLine()
        {
            // The values on this wire are TYPED BY THE OPERATOR now: a needle is
            // whatever is spelled on the screen in front of them, and games do
            // put quotes and punctuation in names. A character lost between the
            // box and the child's parser is lost SILENTLY: the search finds
            // nothing, or finds something else, and comes back attributed to a
            // needle this side never sent.
            //
            // These are the Windows rules CommandLineToArgvW implements.
            Assert.Equal("plain", MemoryScanArgs.Quote("plain"));
            Assert.Equal("\"two words\"", MemoryScanArgs.Quote("two words"));

            // A quote with no space beside it. "quote only when there is a
            // space" left this one bare and the child ate the quote.
            Assert.Equal("\"AE86\\\"GT\"", MemoryScanArgs.Quote("AE86\"GT"));

            // A trailing backslash. Trimming it searched for one character less
            // than was typed; the rule is to double the run of backslashes that
            // meets the closing quote.
            Assert.Equal("ends\\", MemoryScanArgs.Quote("ends\\"));
            Assert.Equal("\"two words\\\\\"", MemoryScanArgs.Quote("two words\\"));

            Assert.Equal("\"\"", MemoryScanArgs.Quote(""));
        }

        [Fact]
        public void AMultiValueRunsExitCodeIsNotReadAsTheWholeRunFailing()
        {
            // The scanner's exit code is the WORST needle's outcome. A run that
            // found the car and did not find the driver exits 11, and 11 on its
            // own means "not found: nothing did what it was told to do". True of
            // one needle, a lie about three, and the kind of lie that makes an
            // operator throw away a good answer.
            var filled = new List<string> { "Car name", "Player name" };

            string eleven = MemoryFieldSearch.OutcomeLine(11, 3, filled);
            Assert.NotNull(eleven);
            Assert.Contains("WORST", eleven);
            Assert.Contains("not a verdict on the run", eleven);
            Assert.Contains("Car name", eleven);
            Assert.Contains("Player name", eleven);

            string ten = MemoryFieldSearch.OutcomeLine(10, 2, filled);
            Assert.NotNull(ten);
            Assert.Contains("more than one place", ten);

            // Nothing found at all is a different sentence, because "what was
            // found landed on" with an empty list would say nothing.
            string none = MemoryFieldSearch.OutcomeLine(11, 3, new List<string>());
            Assert.NotNull(none);
            Assert.Contains("Check the spelling", none);

            // One needle keeps the scanner's own plain meaning: for one needle,
            // "not found" is exactly what 11 says.
            Assert.Null(MemoryFieldSearch.OutcomeLine(11, 1, filled));
            // And a code that is not an outcome word is left alone.
            Assert.Null(MemoryFieldSearch.OutcomeLine(0, 3, filled));
            Assert.Null(MemoryFieldSearch.OutcomeLine(2, 3, filled));
        }

        [Fact]
        public void AMapFileValueThatNoRowCanClearIsDroppedOnLoadAndNamed()
        {
            // The one action refuses to run while any row's value is unusable,
            // which is right. But a clock-locked row offers NO BOX, so a value
            // that arrived on one of those could never be cleared from the tab
            // and the button would stay dead with nothing the operator could do
            // about it. Reachable from a hand-edited file, or from a catalog
            // that moves a field between kinds across versions.
            string dir = Path.Combine(Path.GetTempPath(), "tf-memmap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "map.json");
                File.WriteAllText(path,
                    "{\"version\":1,\"process\":\"game.exe\",\"fields\":[" +
                    "{\"key\":\"lap-time\",\"name\":\"Lap time\",\"known\":\"1:23.456\",\"candidates\":[]}," +
                    "{\"key\":\"car-name\",\"name\":\"Car name\",\"known\":\"TRUENO GT-APEX\",\"candidates\":[]}]}");
                var store = new MemoryFieldStore();
                string why = store.Load(path);
                Assert.NotNull(why);
                Assert.Contains("Lap time", why);
                Assert.False(store.Find(MemoryFields.LapTime).HasKnownValue);

                // And nothing else in the file was touched: the row that CAN
                // carry a value still has it, and the search still runs.
                Assert.Equal("TRUENO GT-APEX", store.Find(MemoryFields.CarName).KnownValue);
                var plan = store.PlanSearch();
                Assert.Single(plan.Needles);
                Assert.Empty(plan.Problems);
                Assert.True(plan.AnyWithoutDriving);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void TwoRowsWithTheSameTextAreOneSearchAndTheEarlierRowKeepsIt()
        {
            // The other half of the case above, with the opposite answer. Byte
            // for byte identical IS one search, and one search's hits cannot be
            // split between two rows. The earlier row keeps them and the later
            // one is NAMED: left silent it would sit empty, looking exactly like
            // a value that was searched for and not found.
            var store = new MemoryFieldStore();
            store.EnsureBuiltIns();
            Assert.Null(store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX"));
            Assert.Null(store.AddCustomField("Body", MemoryValueKind.Text, "TRUENO GT-APEX", out _));

            var plan = store.PlanSearch();
            Assert.Single(plan.Needles);
            Assert.Equal(MemoryFields.CarName, plan.Needles[0].FieldKey);
            Assert.Single(plan.SameTextAsAnotherField);
            Assert.Equal("Body", plan.SameTextAsAnotherField[0]);
            Assert.Contains("Searched once, not twice", plan.Describe());

            // And the routing agrees with the plan rather than with whichever
            // row was registered last.
            var router = MemoryFieldStore.RouterFor(plan, MemoryFields.Rpm);
            Assert.Equal(MemoryFields.CarName, router.Route("TRUENO GT-APEX", "TRUENO GT-APEX", "string"));
            Assert.Equal(0, router.Unattributed);
        }

        [Fact]
        public void TheRouterDoesNotFoldCaseBetweenTwoRows()
        {
            var store = new MemoryFieldStore();
            store.EnsureBuiltIns();
            store.SetKnownValue(MemoryFields.CarName, "TRUENO GT-APEX");
            store.AddCustomField("Decoy", MemoryValueKind.Text, "trueno gt-apex", out _);
            var plan = store.PlanSearch();
            Assert.Equal(2, plan.Needles.Count);
            Assert.Empty(plan.SameTextAsAnotherField);

            var router = MemoryFieldStore.RouterFor(plan, MemoryFields.Rpm);
            Assert.Equal(MemoryFields.CarName, router.Route("TRUENO GT-APEX", "TRUENO GT-APEX", "string"));
            Assert.Equal("decoy", router.Route("trueno gt-apex", "trueno gt-apex", "string"));
            Assert.Equal(0, router.Unattributed);
        }

        [Fact]
        public void TheArmBudgetIsSharedAcrossFieldsRatherThanSpentOnTheFirst()
        {
            // The scanner's watch list is ONE list with one ceiling, and one
            // press now fills SEVERAL fields at once. Spent in field order, a
            // name search that returned two hundred candidates would take the
            // whole budget and the player name typed on the row below it would
            // be read at zero rows: on the map, indistinguishable from any other
            // candidate, and impossible to rule in or out because nothing is
            // reading it.
            var store = new MemoryFieldStore();
            store.EnsureBuiltIns();
            for (int i = 0; i < 300; i++)
                store.AddCandidate(MemoryFields.CarName, "0x" + (0x10000000 + i * 4).ToString("X"),
                                   "utf16", 16, null, "car", out _);
            for (int i = 0; i < 300; i++)
                store.AddCandidate(MemoryFields.PlayerName, "0x" + (0x20000000 + i * 4).ToString("X"),
                                   "utf16", 16, null, "player", out _);

            // Nothing selected: the budget is shared round robin, so BOTH rows
            // are being read. Before this, the first row took all hundred and
            // the second was on the map and read by nothing, which no round can
            // rule in or out either way.
            store.SelectedKey = null;
            var armed = store.ArmList(100, out int dropped);
            Assert.Equal(100, armed.Count);
            int car = 0, player = 0;
            foreach (var c in armed)
            {
                if ((c.Note ?? "") == "car") car++;
                else if ((c.Note ?? "") == "player") player++;
            }
            Assert.True(car > 0, "the first field is being read");
            Assert.True(player > 0, "and so is the second, which is the whole point");
            Assert.True(Math.Abs(car - player) <= 1,
                        "the budget is shared, not spent: " + car + " car, " + player + " player");
            Assert.True(dropped > 0, "and what did not fit is counted rather than dropped silently");

            // And the field being WORKED ON is served in full first, because an
            // elimination round runs against the selected field and one that can
            // judge half its candidates takes twice as many rounds.
            store.SelectedKey = MemoryFields.PlayerName;
            var mine = store.ArmList(100, out _);
            int player2 = 0;
            foreach (var c in mine) if ((c.Note ?? "") == "player") player2++;
            Assert.Equal(100, player2);

            // Moving to the other row moves the budget with it.
            store.SelectedKey = MemoryFields.CarName;
            var theirs = store.ArmList(100, out _);
            int car2 = 0;
            foreach (var c in theirs) if ((c.Note ?? "") == "car") car2++;
            Assert.Equal(100, car2);
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
