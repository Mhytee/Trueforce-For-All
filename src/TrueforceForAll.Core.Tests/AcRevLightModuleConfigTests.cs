using System;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Handing AC's rev lights to us means editing a file inside somebody's
    // game. These pin the two things that makes defensible: the edit touches
    // only the two keys it must, and Revert gives the original back exactly.
    public class AcRevLightModuleConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _cfg;

        public AcRevLightModuleConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tf4all-g27-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _cfg = Path.Combine(_dir, "g27_lights.ini");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Cfg() => File.ReadAllText(_cfg);

        [Fact]
        public void Apply_SetsBothKeys_AndKeepsEverythingElse()
        {
            File.WriteAllLines(_cfg, new[]
            {
                "; my own notes",
                "[BASIC]",
                "ENABLED=0",
                "CUSTOM_DELAY=12 ; Delay; from 0 frame(s) to 24, round",
                "",
                "[PERCENTAGE]",
                "MIN=0.5",
            });

            Assert.True(AcRevLightModuleConfig.Apply(null, _cfg));

            string outp = Cfg();
            Assert.Contains("ENABLED=1", outp);
            Assert.Contains("MODE=DISABLED", outp);
            // Untouched: the comment, the unrelated key, the other section.
            Assert.Contains("; my own notes", outp);
            Assert.Contains("CUSTOM_DELAY=12 ; Delay; from 0 frame(s) to 24, round", outp);
            Assert.Contains("[PERCENTAGE]", outp);
            Assert.Contains("MIN=0.5", outp);
        }

        [Fact]
        public void Apply_PreservesTheTrailingCommentOnAKeyItRewrites()
        {
            File.WriteAllLines(_cfg, new[] { "[BASIC]", "MODE=DI_BASED ; Mode (algorithm for LEDs)" });
            AcRevLightModuleConfig.Apply(null, _cfg);
            Assert.Contains("MODE=DISABLED ; Mode (algorithm for LEDs)", Cfg());
        }

        [Fact]
        public void Apply_AddsTheSectionWhenTheFileHasNone()
        {
            File.WriteAllLines(_cfg, new[] { "[SOMETHING_ELSE]", "X=1" });
            AcRevLightModuleConfig.Apply(null, _cfg);
            string outp = Cfg();
            Assert.Contains("[BASIC]", outp);
            Assert.Contains("ENABLED=1", outp);
            Assert.Contains("MODE=DISABLED", outp);
            Assert.Contains("[SOMETHING_ELSE]", outp);
        }

        [Fact]
        public void Apply_IsIdempotent_AndDoesNotRewriteAnAlreadyCorrectFile()
        {
            File.WriteAllLines(_cfg, new[] { "[BASIC]", "ENABLED=1", "MODE=DISABLED" });
            AcRevLightModuleConfig.Apply(null, _cfg);
            string once = Cfg();
            AcRevLightModuleConfig.Apply(null, _cfg);
            Assert.Equal(once, Cfg());
        }

        [Fact]
        public void Revert_RestoresTheUsersFileByteForByte()
        {
            var original = new[] { "; keep me", "[BASIC]", "ENABLED=0", "CUSTOM_DELAY=12" };
            File.WriteAllLines(_cfg, original);
            string before = Cfg();

            AcRevLightModuleConfig.Apply(null, _cfg);
            Assert.True(AcRevLightModuleConfig.TakenOver(_cfg));
            Assert.NotEqual(before, Cfg());

            Assert.True(AcRevLightModuleConfig.Revert(null, _cfg));
            Assert.Equal(before, Cfg());
            Assert.False(AcRevLightModuleConfig.TakenOver(_cfg));
        }

        [Fact]
        public void Revert_DeletesOurFileWhenTheUserNeverHadOne()
        {
            Assert.False(File.Exists(_cfg));
            AcRevLightModuleConfig.Apply(null, _cfg);
            Assert.True(File.Exists(_cfg));

            AcRevLightModuleConfig.Revert(null, _cfg);
            // Restoring a file that never existed means removing ours, not
            // leaving CSP an override the user never wrote.
            Assert.False(File.Exists(_cfg));
            Assert.False(AcRevLightModuleConfig.TakenOver(_cfg));
        }

        [Fact]
        public void Revert_WithoutAnApply_DoesNothing()
        {
            File.WriteAllLines(_cfg, new[] { "[BASIC]", "ENABLED=0" });
            string before = Cfg();
            Assert.False(AcRevLightModuleConfig.Revert(null, _cfg));
            Assert.Equal(before, Cfg());
        }

        [Fact]
        public void Apply_TakenTwice_KeepsTheFIRSTBackup()
        {
            // A second Apply must not overwrite the backup with our own edited
            // file, or Revert would "restore" the state we imposed.
            var original = new[] { "[BASIC]", "ENABLED=0" };
            File.WriteAllLines(_cfg, original);
            string before = Cfg();

            AcRevLightModuleConfig.Apply(null, _cfg);
            File.WriteAllLines(_cfg, new[] { "[BASIC]", "ENABLED=0" });  // as if CM rewrote it
            AcRevLightModuleConfig.Apply(null, _cfg);

            AcRevLightModuleConfig.Revert(null, _cfg);
            Assert.Equal(before, Cfg());
        }

        [Fact]
        public void Apply_RefusesWhenTheCspConfigFolderIsAbsent()
        {
            string missing = Path.Combine(_dir, "no-such-folder", "g27_lights.ini");
            Assert.False(AcRevLightModuleConfig.Apply(null, missing));
            Assert.False(File.Exists(missing));
        }
    }
}
