using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // We edit a file the user owns and may have spent an evening tuning, so the
    // bar is not "the key is set", it is "nothing else moved". These tests are
    // written against the real shape of a shipped FFBPlugin.ini: a comment banner,
    // a single [Settings] section, CRLF line endings.
    public class IniKeyWriterTests
    {
        // Faithful to a real Initial D 8 FFBPlugin.ini, including the CRLF endings
        // and the banner. Values are the ones actually shipped.
        private const string RealIni =
            "; ************************************\r\n" +
            "; *** FFB Settings for Initial D 8 ***\r\n" +
            "; ************************************\r\n" +
            "[Settings]\r\n" +
            "GameId=18\r\n" +
            "StartDelay=1\r\n" +
            "MinForce=0\r\n" +
            "MaxForce=100\r\n" +
            "DeviceGUID=0300e03b6d04000072c2000000000000\r\n" +
            "EnableRumble=1\r\n" +
            "FeedbackLength=5000\r\n" +
            "EnableDamper=0\r\n";

        private static string Set(string text, string key, string value)
        {
            string err;
            string outp = IniKeyWriter.SetKey(text, "Settings", key, value, out err);
            Assert.Null(err);
            Assert.NotNull(outp);
            return outp;
        }

        // The whole point. Everything the user tuned has to survive verbatim.
        [Fact]
        public void AddingOurKey_ChangesNothingElse()
        {
            string outp = Set(RealIni, "SharedMemoryOutput", "2");

            Assert.Contains("SharedMemoryOutput=2", outp);
            // Every original line still present, byte for byte.
            foreach (var line in RealIni.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(line, outp);
            // And nothing was lost: the output is the original plus our one line.
            Assert.Equal(RealIni.Length + "SharedMemoryOutput=2\r\n".Length, outp.Length);
        }

        [Fact]
        public void CrlfEndingsSurvive_AndNoBareNewlinesAppear()
        {
            string outp = Set(RealIni, "SharedMemoryOutput", "2");
            Assert.DoesNotContain("\n", outp.Replace("\r\n", ""));
        }

        // An LF-only file must stay LF. File.ReadAllLines + WriteAllLines would
        // silently convert the whole file to CRLF, which is a diff on every line
        // for a one-key change.
        [Fact]
        public void LfOnlyFile_StaysLfOnly()
        {
            string lf = "[Settings]\nGameId=18\nMaxForce=100\n";
            string outp = Set(lf, "SharedMemoryOutput", "2");
            Assert.DoesNotContain("\r", outp);
            Assert.Contains("SharedMemoryOutput=2", outp);
        }

        [Fact]
        public void ExistingKey_IsReplacedInPlace_NotDuplicated()
        {
            string withKey = RealIni + "SharedMemoryOutput=0\r\n";
            string outp = Set(withKey, "SharedMemoryOutput", "2");

            Assert.Contains("SharedMemoryOutput=2", outp);
            Assert.DoesNotContain("SharedMemoryOutput=0", outp);
            // Exactly one occurrence: a second install must not stack keys.
            Assert.Equal(1, CountOf(outp, "SharedMemoryOutput="));
            Assert.Equal(withKey.Length, outp.Length + "0".Length - "2".Length);
        }

        [Fact]
        public void KeyMatchIsCaseInsensitive_AsIniKeysAre()
        {
            string withKey = RealIni + "sharedmemoryoutput=0\r\n";
            string outp = Set(withKey, "SharedMemoryOutput", "2");
            Assert.Equal(1, CountOf(outp, "=2"));
            Assert.DoesNotContain("=0\r\nsharedmemoryoutput", outp);
        }

        // A key the user commented out is a note about a value they turned off.
        // Editing it would resurrect a setting they deliberately disabled.
        [Fact]
        public void CommentedOutKey_IsLeftAlone_AndARealKeyIsAdded()
        {
            string withComment = RealIni + ";SharedMemoryOutput=1\r\n";
            string outp = Set(withComment, "SharedMemoryOutput", "2");

            Assert.Contains(";SharedMemoryOutput=1", outp);
            Assert.Contains("SharedMemoryOutput=2", outp);
        }

        // Their tuning lives in [Settings]; a per-game section further down must
        // not absorb our key or lose its own.
        [Fact]
        public void OnlyTheNamedSectionIsTouched()
        {
            string multi =
                "[Settings]\r\nGameId=18\r\n\r\n[Advanced]\r\nMinForce=10\r\nSharedMemoryOutput=9\r\n";
            string outp = Set(multi, "SharedMemoryOutput", "2");

            // The one in [Advanced] is a different key in a different section.
            Assert.Contains("[Advanced]\r\nMinForce=10\r\nSharedMemoryOutput=9", outp);
            // Ours went into [Settings], before the blank line and the next header.
            Assert.Contains("[Settings]\r\nGameId=18\r\nSharedMemoryOutput=2", outp);
        }

        [Fact]
        public void MissingSection_IsRefused_RatherThanCreated()
        {
            string err;
            string outp = IniKeyWriter.SetKey("[Other]\r\nx=1\r\n", "Settings", "K", "V", out err);
            Assert.Null(outp);
            Assert.NotNull(err);
        }

        // A file whose last line has no terminator must not end up with our key
        // glued onto it.
        [Fact]
        public void FileWithNoTrailingNewline_GetsOneBeforeOurKey()
        {
            string noTrail = "[Settings]\r\nGameId=18";
            string outp = Set(noTrail, "SharedMemoryOutput", "2");
            Assert.Contains("GameId=18\r\nSharedMemoryOutput=2", outp);
        }

        [Fact]
        public void RoundTrippingTwice_IsStable()
        {
            string once = Set(RealIni, "SharedMemoryOutput", "2");
            string twice = Set(once, "SharedMemoryOutput", "2");
            Assert.Equal(once, twice);
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
