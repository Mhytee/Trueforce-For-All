using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>
    /// The session: one recorded drive that every field is answered from, and
    /// the marks the operator taps during it.
    ///
    /// Two things here go wrong silently, which is why they are pinned rather
    /// than left to the screen:
    ///
    ///   A session that turns into some other run. A request that carries a
    ///   known value alongside the session flag would be a known-value search
    ///   that returns before the recorder starts, and the operator who pressed
    ///   Record would drive ten minutes into a file that does not exist.
    ///
    ///   A mark that looks AT the tap rather than before it. The owner: "taps
    ///   would always be late except shifts." A window of zero asks the analysis
    ///   for a change at the instant of the tap, where the change has already
    ///   happened, and every mark would read as a clean negative.
    /// </summary>
    public class MemorySessionTests
    {
        // ---- the command line ------------------------------------------------

        [Fact]
        public void ASessionRunAsksForTheWideRecordingAndNothingElse()
        {
            var req = new MemoryScanRequest { Pid = 1234, Session = true };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
            Assert.Null(error);
            Assert.Contains("--session", args);
            Assert.Contains("--script none", args);
            Assert.Contains("--seconds 1800", args);
            Assert.Contains("--hz 30", args);
            Assert.Contains("--store-budget-mbps 20", args);
            Assert.Contains("--pid 1234", args);
            Assert.DoesNotContain("--survey", args);
            Assert.DoesNotContain("--watch-only", args);
            Assert.DoesNotContain("--paths-to", args);
            Assert.DoesNotContain("--known-", args);
        }

        [Fact]
        public void ASessionCarriesNoKnownValueEvenWhenTheMapHasOne()
        {
            // The map's rows keep their values between runs. A session must not
            // pick them up: a needle on its command line makes it a search that
            // returns before the recorder starts.
            var req = new MemoryScanRequest
            {
                Pid = 1,
                Session = true,
                KnownString = "TRUENO GT-APEX",
                KnownStrings = new List<string> { "MHYTEE" },
                KnownRedline = 11000,
                KnownGearLive = true,
            };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 3, out string error);
            Assert.Null(error);
            Assert.Contains("--session", args);
            Assert.DoesNotContain("--known-string", args);
            Assert.DoesNotContain("--known-redline", args);
            Assert.DoesNotContain("--known-gear", args);
            Assert.DoesNotContain("--watch-hold", args);
        }

        [Fact]
        public void TheSessionCapAndBudgetAreTheOnesAskedFor()
        {
            var req = new MemoryScanRequest { Pid = 1, Session = true, SessionSeconds = 600, SessionBudgetMbps = 35 };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out _);
            Assert.Contains("--seconds 600", args);
            Assert.Contains("--store-budget-mbps 35", args);
        }

        [Fact]
        public void ANonsenseCapFallsBackToTheDefaultsRatherThanZero()
        {
            // A zero-second recording and a zero budget are both refusals the
            // scanner would report as a usage error, which reads on the tab as
            // "your memscan is too old".
            var req = new MemoryScanRequest { Pid = 1, Session = true, SessionSeconds = 0, SessionBudgetMbps = -1 };
            string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out _);
            Assert.Contains("--seconds 1800", args);
            Assert.Contains("--store-budget-mbps 20", args);
        }

        [Fact]
        public void EveryOtherRunStillBuildsWithoutTheSessionFlag()
        {
            foreach (var req in new[]
            {
                new MemoryScanRequest { Pid = 1, Survey = true },
                new MemoryScanRequest { Pid = 1, WatchHost = true },
                new MemoryScanRequest { Pid = 1, KnownString = "TRUENO" },
                new MemoryScanRequest { Pid = 1, Script = "rpm-v1" },
            })
            {
                string args = MemoryScanArgs.Build(req, @"C:\out", 5, 0, out string error);
                Assert.Null(error);
                Assert.DoesNotContain("--session", args);
            }
        }

        // ---- marks on the wire -------------------------------------------------

        [Fact]
        public void AMarkCarriesItsLookbackWindowOnTheWire()
        {
            // The scanner reads the window as "lookbackMs" in whole
            // milliseconds (Commands.cs, session.ps1, PLAN-v4). The presets and
            // the tab talk in seconds; CommandFor converts at the wire, so three
            // seconds go out as 3000. Sending "lookbackSecs" instead was
            // silently ignored, and every mark fell back to the scanner's own
            // default window, which for a "stop" is narrower than the stop.
            JObject o = MemoryMarks.CommandFor("crash", 3);
            Assert.Equal("mark", (string)o["cmd"]);
            Assert.Equal("crash", (string)o["name"]);
            Assert.Equal(3000L, (long)o["lookbackMs"]);
            Assert.Null(o["lookbackSecs"]);
        }

        [Fact]
        public void AMarkWithNoWindowLooksExactlyAsItAlwaysDid()
        {
            // A scanner that reads "name" alone must see the line it always saw:
            // no extra field, nothing to trip on.
            JObject o = MemoryMarks.CommandFor("note", 0);
            Assert.Null(o["lookbackMs"]);
            Assert.Equal("{\"cmd\":\"mark\",\"name\":\"note\"}", o.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void MarkNamesAreNormalisedSoAButtonAndATypedNameAgree()
        {
            Assert.Equal("car-change", MemoryMarks.Normalize(" Car Change "));
            Assert.Equal("race-start", MemoryMarks.Normalize("RACE_START"));
            Assert.Equal("note", MemoryMarks.Normalize(""));
            Assert.Equal("note", MemoryMarks.Normalize(null));
            Assert.Equal("note", MemoryMarks.Normalize("---"));
            Assert.Equal("lap", MemoryMarks.Normalize("lap"));
        }

        // ---- the windows -------------------------------------------------------

        [Fact]
        public void NoMarkEverLooksBackZeroSeconds()
        {
            // The worst failure available: a window of zero eliminates the real
            // value for "not responding" and looks like a clean negative.
            foreach (var p in MemoryMarks.Presets)
                Assert.True(p.LookbackSecs > 0, p.Name + " has a window of " + p.LookbackSecs);
            Assert.True(MemoryMarks.LookbackSecsFor("something-nobody-listed") > 0);
        }

        [Fact]
        public void TheWindowDiffersByEventBecauseReactionLagDoes()
        {
            // A crash is a second or two; a car change is a menu action and can
            // be fifteen. Both are generous on purpose.
            Assert.True(MemoryMarks.LookbackSecsFor("crash") >= 2);
            Assert.True(MemoryMarks.LookbackSecsFor("car-change") >= 15);
            Assert.True(MemoryMarks.LookbackSecsFor("car-change") > MemoryMarks.LookbackSecsFor("crash"));
            // A stop is held for ten seconds, and the tap comes at the end of it.
            Assert.True(MemoryMarks.LookbackSecsFor("stop") >= 10);
            // Spelling does not change the window.
            Assert.Equal(MemoryMarks.LookbackSecsFor("car-change"), MemoryMarks.LookbackSecsFor("Car Change"));
        }

        [Fact]
        public void ThePresetListStartsWithNoteAndHasNoRepeats()
        {
            var presets = MemoryMarks.Presets;
            Assert.True(presets.Count >= 5);
            Assert.Equal(MemoryMarks.DefaultName, presets[0].Name);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in presets)
            {
                Assert.True(seen.Add(p.Name), "repeated preset " + p.Name);
                Assert.Equal(p.Name, MemoryMarks.Normalize(p.Name));
                Assert.False(string.IsNullOrWhiteSpace(p.Separates));
            }
            // The super run's events all have a name to tap.
            foreach (string need in new[] { "race-start", "lap", "stop", "car-change", "crash", "drift" })
                Assert.NotNull(MemoryMarks.Find(need));
        }

        // ---- what the recording has written ------------------------------------

        [Fact]
        public void RecordingProgressIsReadOffWhateverEventCarriesIt()
        {
            var onStage = ScanHost.Parse("{\"ev\":\"stage\",\"name\":\"record\",\"bytesWritten\":3145728}");
            Assert.NotNull(onStage);
            Assert.Equal(3145728L, onStage.BytesWritten);
            Assert.Equal(0.0, onStage.MbWritten);

            var ownLine = ScanHost.Parse("{\"ev\":\"progress\",\"tick\":900,\"mbWritten\":12.5}");
            Assert.NotNull(ownLine);
            Assert.Equal(12.5, ownLine.MbWritten, 6);

            var neither = ScanHost.Parse("{\"ev\":\"stage\",\"name\":\"seed\"}");
            Assert.Equal(0L, neither.BytesWritten);
            Assert.Equal(0.0, neither.MbWritten);

            // The session's final size arrives as fileBytes on the session
            // result line: no live per-tick size, so the tab shows the real
            // total after the run rather than a zero that reads as a dead
            // recording.
            var onResult = ScanHost.Parse("{\"ev\":\"session\",\"path\":\"x.msr\",\"fileBytes\":7340032}");
            Assert.NotNull(onResult);
            Assert.Equal(7340032L, onResult.BytesWritten);
        }

        // ---- the map's own words ----------------------------------------------

        [Fact]
        public void TheRevsFieldSaysHowItIsFoundOnThisCabinet()
        {
            // The car cannot rev in neutral, so the stationary scripts cannot
            // work here and the row has to say where the answer comes from
            // instead: the drive, its shifts and its limiter.
            var def = MemoryFields.ByKey(MemoryFields.Rpm);
            Assert.NotNull(def);
            Assert.Equal(MemoryValueKind.None, def.ValueKind);
            Assert.Contains("shift", def.What, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("limiter", def.What, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session", def.What, StringComparison.OrdinalIgnoreCase);
        }
    }
}
