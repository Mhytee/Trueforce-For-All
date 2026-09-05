using System;
using System.Collections.Generic;
using System.IO;
using TrueforceForAll.Plugin;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>
    /// The watch list is the memory map: a label, an address and a live value.
    /// Two things in it are worth pinning, and both fail silently if they break.
    ///
    /// The address parser decides whether a row is anchored to a module (good on
    /// every launch) or to a bare address (good until the game restarts), and a
    /// row that normalises to a different string than the one the scanner echoes
    /// back never matches, which on screen looks exactly like a value that
    /// stopped changing. That is the reading this whole feature exists to make
    /// trustworthy, so the parser gets the tests.
    ///
    /// The wire decode has the same property: a watch line that fails to parse
    /// freezes every row at once with nothing to say why.
    /// </summary>
    public class MemoryWatchListTests
    {
        // ---- addresses -------------------------------------------------------

        [Theory]
        [InlineData("0x01C02750", "0x01C02750")]
        [InlineData("0x1c02750", "0x01C02750")]      // short, lower case, same row
        [InlineData("1C02750", "0x01C02750")]        // no prefix at all
        [InlineData("  0X01c02750  ", "0x01C02750")] // whitespace and case
        public void AbsoluteAddressesNormaliseToOneForm(string typed, string expected)
        {
            string got = MemoryWatchList.NormalizeAddress(typed, out string problem);
            Assert.Null(problem);
            Assert.Equal(expected, got);
        }

        [Fact]
        public void ModuleRelativeAddressKeepsItsModuleAndItsOffset()
        {
            string got = MemoryWatchList.NormalizeAddress("game.exe+0x4A2C10", out string problem);
            Assert.Null(problem);
            Assert.Equal("game.exe+0x4A2C10", got);
        }

        [Fact]
        public void ModuleRelativeOffsetIsReadAsHexEvenWithoutThePrefix()
        {
            // Everything either half prints is hex. Reading a bare offset as
            // decimal would land somewhere else entirely and still look like a
            // valid row.
            string got = MemoryWatchList.NormalizeAddress("game.exe+4a2c10", out string problem);
            Assert.Null(problem);
            Assert.Equal("game.exe+0x4A2C10", got);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nonsense")]
        [InlineData("0x")]
        [InlineData("+0x10")]                       // no module before the +
        [InlineData("game.exe+")]                   // no offset after it
        [InlineData("game.exe+0xZZ")]
        [InlineData("C:\\games\\game.exe+0x10")]    // a path, not a module name
        [InlineData("0x11111111111111111")]         // 17 digits, wider than any address
        public void BadAddressesAreRefusedWithAReason(string typed)
        {
            string got = MemoryWatchList.NormalizeAddress(typed, out string problem);
            Assert.Null(got);
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        // ---- kinds -----------------------------------------------------------

        [Theory]
        [InlineData("f32", "float32")]
        [InlineData("F32", "float32")]
        [InlineData("i32", "int32")]
        [InlineData("dword", "uint32")]
        [InlineData("double", "float64")]
        [InlineData("utf-16", "utf16")]
        [InlineData("unicode", "utf16")]
        public void KindSpellingsFoldOntoTheProtocolNames(string typed, string expected)
            => Assert.Equal(expected, MemoryWatchList.NormalizeKind(typed));

        [Fact]
        public void AnUnknownKindIsRefusedRatherThanGuessed()
            => Assert.Null(MemoryWatchList.NormalizeKind("bcd"));

        [Theory]
        [InlineData("utf16", "utf16")]
        [InlineData("string:utf-16le", "utf16")]
        [InlineData("shift-jis", "bytes")]   // no kind for it; bytes still moves when the car does
        [InlineData("sjis", "bytes")]
        [InlineData("f64", "float64")]
        [InlineData("", "int32")]
        [InlineData("something we have never seen", "int32")]
        public void AFindingsTypeAlwaysYieldsAWatchableKind(string type, string expected)
            => Assert.Equal(expected, MemoryWatchList.KindForFindingType(type));

        [Fact]
        public void OnlyTheStringAndByteKindsCarryALength()
        {
            Assert.Equal(0, MemoryWatchList.NormalizeLen("int32", 64));
            Assert.Equal(32, MemoryWatchList.NormalizeLen("utf16", 0));
            Assert.Equal(64, MemoryWatchList.NormalizeLen("utf16", 64));
            Assert.Equal(MemoryWatchList.MaxLen, MemoryWatchList.NormalizeLen("bytes", 99999));
        }

        // ---- the list --------------------------------------------------------

        [Fact]
        public void ARowIsFoundHoweverItsAddressIsWrittenBack()
        {
            var list = new MemoryWatchList();
            Assert.Null(list.Add("0x1c02750", "int32", 0, "redline?", out var row, out bool added));
            Assert.True(added);

            // The scanner echoes the address back exactly as it was sent, and
            // that is the case the UI depends on.
            Assert.Same(row, list.Find("0x01C02750"));
            // A hand-typed variant still lands on the same row rather than
            // looking like a row that went quiet.
            Assert.Same(row, list.Find("0X1C02750"));
            Assert.Null(list.Find("0x01C02754"));
        }

        [Fact]
        public void AddingAnAddressTwiceCorrectsTheRowRatherThanDuplicatingIt()
        {
            var list = new MemoryWatchList();
            list.Add("0x01C02750", "int32", 0, "first guess", out _, out _);
            Assert.Null(list.Add("0x01C02750", "float32", 0, "second guess", out var row, out bool added));

            Assert.False(added);
            Assert.Equal(1, list.Count);
            Assert.Equal("float32", row.Kind);
            Assert.Equal("second guess", row.Label);
        }

        [Fact]
        public void TheListRefusesToGrowPastWhatOneRefreshCanCarry()
        {
            var list = new MemoryWatchList();
            for (int i = 0; i < MemoryWatchList.MaxRows; i++)
                Assert.Null(list.Add("0x" + (0x1000 + i * 4).ToString("X8"), "int32", 0, "r", out _, out _));

            string problem = list.Add("0x00009000", "int32", 0, "one too many", out _, out _);
            Assert.False(string.IsNullOrWhiteSpace(problem));
            Assert.Equal(MemoryWatchList.MaxRows, list.Count);
        }

        [Fact]
        public void ABadAddressNeverReachesTheList()
        {
            var list = new MemoryWatchList();
            Assert.NotNull(list.Add("not an address", "int32", 0, "x", out var row, out _));
            Assert.Null(row);
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void RelabellingSaysWhetherAnythingActuallyChanged()
        {
            var list = new MemoryWatchList();
            list.Add("0x01C02750", "int32", 0, "car name", out _, out _);

            Assert.False(list.Relabel("0x01C02750", "car name"));   // the same name, no work to do
            Assert.True(list.Relabel("0x01C02750", "redline"));
            Assert.Equal("redline", list.Find("0x01C02750").Label);
            Assert.False(list.Relabel("0x0BADF00D", "nothing there"));
        }

        [Fact]
        public void ALabelIsANameNotANote()
        {
            Assert.Equal("car name", MemoryWatchList.CleanLabel("  car\nname  "));
            Assert.Equal(64, MemoryWatchList.CleanLabel(new string('x', 400)).Length);
        }

        [Fact]
        public void TheRateIsClampedToWhatTheProtocolAllows()
        {
            var list = new MemoryWatchList { Hz = 0 };
            Assert.Equal(MemoryWatchList.MinHz, list.Hz);
            list.Hz = 9999;
            Assert.Equal(MemoryWatchList.MaxHz, list.Hz);
            list.Hz = 5;
            Assert.Equal(5, list.Hz);
        }

        // ---- the file --------------------------------------------------------

        [Fact]
        public void TheListSurvivesARelaunch()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tf4all-watch-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var saved = new MemoryWatchList { Hz = 10 };
                saved.Add("game.exe+0x4A2C10", "utf16", 48, "car name", out _, out _);
                saved.Add("0x01C02750", "int32", 0, "redline?", out _, out _);
                Assert.Null(saved.Save(path));

                var loaded = new MemoryWatchList();
                Assert.Null(loaded.Load(path));
                Assert.Equal(10, loaded.Hz);
                Assert.Equal(2, loaded.Count);

                var row = loaded.Find("game.exe+0x4A2C10");
                Assert.NotNull(row);
                Assert.Equal("utf16", row.Kind);
                Assert.Equal(48, row.Len);
                Assert.Equal("car name", row.Label);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void AMissingFileIsNotAnError()
        {
            var list = new MemoryWatchList();
            list.Add("0x01C02750", "int32", 0, "x", out _, out _);
            Assert.Null(list.Load(Path.Combine(Path.GetTempPath(),
                "tf4all-watch-absent-" + Guid.NewGuid().ToString("N") + ".json")));
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void AMangledFileLeavesAnEmptyListRatherThanThrowing()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tf4all-watch-bad-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ this is not json");
                var list = new MemoryWatchList();
                Assert.NotNull(list.Load(path));     // it says why
                Assert.Equal(0, list.Count);         // and it is empty, not half read
            }
            finally { try { File.Delete(path); } catch { } }
        }

        // ---- the wire --------------------------------------------------------

        [Fact]
        public void AWatchRefreshDecodesEveryRowInOneLine()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"watch\",\"tick\":412,\"atMs\":13750,\"rows\":[" +
                "{\"addr\":\"0x01C02750\",\"resolved\":\"0x01C02750\",\"kind\":\"int32\",\"value\":\"7400\",\"ok\":true}," +
                "{\"addr\":\"game.exe+0x4A2C10\",\"resolved\":\"0x0084A2C10\",\"kind\":\"utf16\",\"value\":\"AE86 Trueno\",\"ok\":true}," +
                "{\"addr\":\"0x0BADF00D\",\"resolved\":null,\"kind\":\"int32\",\"value\":null,\"ok\":false,\"why\":\"unreadable\"}]}");

            Assert.NotNull(ev);
            Assert.Equal("watch", ev.Ev);
            Assert.Equal(412, ev.Tick);
            Assert.Equal(13750, ev.AtMs);
            Assert.Equal(3, ev.Rows.Count);

            Assert.Equal("7400", ev.Rows[0].Value);
            Assert.True(ev.Rows[0].Ok);

            Assert.Equal("AE86 Trueno", ev.Rows[1].Value);
            Assert.Equal("0x0084A2C10", ev.Rows[1].Resolved);

            // A row that could not be read keeps its place and its reason: a
            // value that becomes unreadable is itself information.
            Assert.False(ev.Rows[2].Ok);
            Assert.Null(ev.Rows[2].Value);
            Assert.Equal("unreadable", ev.Rows[2].Why);
        }

        [Fact]
        public void OneUnreadableRowDoesNotTakeTheWholeRefreshDown()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"watch\",\"tick\":7,\"rows\":[" +
                "\"not an object\"," +
                "{\"kind\":\"int32\",\"value\":\"1\"}," +          // no address, so no row to update
                "{\"addr\":\"0x00001000\",\"kind\":\"int32\",\"value\":\"42\"}]}");

            Assert.NotNull(ev);
            Assert.Single(ev.Rows);
            Assert.Equal("0x00001000", ev.Rows[0].Addr);
            // No "ok" field, but a value came with it, so the read worked.
            Assert.True(ev.Rows[0].Ok);
        }

        [Fact]
        public void ANumericValueOnTheWireIsStillReadable()
        {
            var ev = ScanHost.Parse(
                "{\"ev\":\"watch\",\"rows\":[{\"addr\":\"0x1000\",\"kind\":\"int32\",\"value\":7400}]}");
            Assert.Equal("7400", ev.Rows[0].Value);
            Assert.True(ev.Rows[0].Ok);
        }

        [Fact]
        public void ALineWithNoRowsIsNotAnEmptyWatch()
        {
            // Null, not empty: "this was not a watch line" and "the scanner is
            // watching nothing" are different answers and the UI treats them
            // differently.
            var ev = ScanHost.Parse("{\"ev\":\"stage\",\"name\":\"narrow\"}");
            Assert.Null(ev.Rows);

            var empty = ScanHost.Parse("{\"ev\":\"watch\",\"rows\":[]}");
            Assert.NotNull(empty.Rows);
            Assert.Empty(empty.Rows);
        }

        // ---- Apply: the comparison the whole feature rests on -------------

        private static MemoryWatchList ListWith(params string[] addrs)
        {
            var l = new MemoryWatchList();
            foreach (var a in addrs) l.Add(a, "utf16", 16, a, out _, out _);
            return l;
        }

        private static ScanEvent Refresh(params string[] json)
            => ScanHost.Parse("{\"ev\":\"watch\",\"tick\":1,\"rows\":[" + string.Join(",", json) + "]}");

        [Fact]
        public void TheFirstValueARowEverShowsIsNotAChange()
        {
            // A row arriving is not the car changing. Counting it would light up
            // the whole map the moment a watch starts and mean nothing.
            var l = ListWith("0x1000");
            var u = l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"AE86\",\"ok\":true}").Rows, out int s);
            Assert.Equal(0, s);
            Assert.Single(u);
            Assert.False(u[0].Changed);
            Assert.Equal("AE86", u[0].Shown);
            Assert.Equal(0, u[0].Row.Changes);
        }

        [Fact]
        public void AValueThatFollowsTheCarIsCountedAsAChange()
        {
            var l = ListWith("0x1000");
            l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"AE86\",\"ok\":true}").Rows, out _);
            var u = l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"RX-7\",\"ok\":true}").Rows, out _);
            Assert.True(u[0].Changed);
            Assert.Equal(1, u[0].Row.Changes);
            // And a refresh that says the same thing again is not a second one.
            var again = l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"RX-7\",\"ok\":true}").Rows, out _);
            Assert.False(again[0].Changed);
            Assert.Equal(1, again[0].Row.Changes);
        }

        [Fact]
        public void BecomingUnreadableIsItselfAChangeAndKeepsThePlace()
        {
            // The region was freed, or the car was unloaded. That is information
            // about the target, so the row stays and the change is counted.
            var l = ListWith("0x1000");
            l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"AE86\",\"ok\":true}").Rows, out _);
            var u = l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":null,\"ok\":false,\"why\":\"unreadable\"}").Rows, out _);
            Assert.True(u[0].Changed);
            Assert.False(u[0].Ok);
            Assert.Equal("cannot read: unreadable", u[0].Shown);
        }

        [Fact]
        public void ARowTheListDoesNotHoldIsCountedRatherThanAdopted()
        {
            var l = ListWith("0x1000");
            var u = l.Apply(Refresh(
                "{\"addr\":\"0x00001000\",\"value\":\"a\",\"ok\":true}",
                "{\"addr\":\"0x00009999\",\"value\":\"b\",\"ok\":true}").Rows, out int strangers);
            Assert.Single(u);
            Assert.Equal(1, strangers);
            Assert.Equal(1, l.Count);
        }

        [Fact]
        public void ForgettingReadingsKeepsTheMapAndDropsTheHistory()
        {
            // A new run has read nothing. Carrying the last run's value over
            // would count a change nobody made.
            var l = ListWith("0x1000");
            l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"AE86\",\"ok\":true}").Rows, out _);
            l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"RX-7\",\"ok\":true}").Rows, out _);
            Assert.Equal(1, l.Rows[0].Changes);
            l.ForgetReadings();
            Assert.Equal(1, l.Count);
            Assert.False(l.Rows[0].HasRead);
            Assert.Equal(0, l.Rows[0].Changes);
            var u = l.Apply(Refresh("{\"addr\":\"0x00001000\",\"value\":\"AE86\",\"ok\":true}").Rows, out _);
            Assert.False(u[0].Changed);
        }

        // ---- a finding becomes a row with nothing retyped ------------------

        [Fact]
        public void AFindingInsideAnImageBecomesAModuleRelativeRow()
        {
            // The difference between a map and a list of numbers: module+offset
            // resolves again after a relaunch and a heap address does not.
            MemoryWatchList.SpecForFinding("0x004A2C10", "game.exe+0x4A2C10", "string", "utf16", 11,
                                           "car-name", "AE86 Trueno",
                                           out string addr, out string kind, out int len, out string label);
            Assert.Equal("game.exe+0x4A2C10", addr);
            Assert.Equal("utf16", kind);
            Assert.Equal("car-name", label);
            Assert.True(len > 11);
        }

        [Fact]
        public void TheScannersOwnKindWinsOverTheTypeColumn()
        {
            // The scanner knows which encoding matched. The Type column only
            // describes it, and a Shift-JIS hit has no kind of its own.
            MemoryWatchList.SpecForFinding("0x1000", null, "shift-jis", "bytes", 8, "", "ハチロク",
                                           out _, out string kind, out int len, out string label);
            Assert.Equal("bytes", kind);
            Assert.Equal("ハチロク", label);
            Assert.True(len >= 20);

            // And with no watchKind at all, the type column is still read.
            MemoryWatchList.SpecForFinding("0x1000", null, "ptr32", null, 0, "", "",
                                           out _, out string k2, out int l2, out _);
            Assert.Equal("uint32", k2);
            Assert.Equal(0, l2);
        }

        [Fact]
        public void AStringRowAsksForMoreThanTheNameThatMatched()
        {
            // The whole point is to see a DIFFERENT name arrive. A window the
            // exact length of the search term would cut the answer off.
            Assert.True(MemoryWatchList.LenForFinding("utf16", 11, "AE86 Trueno") > 11);
            Assert.True(MemoryWatchList.LenForFinding("utf16", 40, "AE") >= 52);
            Assert.Equal(MemoryWatchList.MaxLen, MemoryWatchList.LenForFinding("utf8", 400, "x"));
            // Fixed-width kinds never carry one.
            Assert.Equal(0, MemoryWatchList.LenForFinding("int32", 11, "AE86 Trueno"));
        }

        [Fact]
        public void AnEventKindThisBuildDoesNotKnowIsIgnoredRatherThanFatal()
        {
            // The forward-compatibility rule both halves are built on: the
            // scanner may add kinds without waiting for a plugin release.
            Assert.NotNull(ScanHost.Parse("{\"ev\":\"something-new\",\"n\":1}"));
            Assert.Null(ScanHost.Parse("not json at all"));
            Assert.Null(ScanHost.Parse(""));
        }
    }
}
