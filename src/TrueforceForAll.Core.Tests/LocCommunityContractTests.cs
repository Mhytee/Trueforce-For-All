// The client half of the translation service's contract, asserted rather than
// trusted. Three things live here:
//
//   the hash vectors, because a wrong encoding on this path would make every row
//   name an English the server does not hold, and the failure would look like
//   "the service does nothing" rather than like a bug;
//
//   the file shape, because Write-LocJson writes the same files and a translator
//   edits them by hand, so a difference in ordering or escaping shows up as a diff
//   nobody asked for;
//
//   the row gate, because it is the only thing between a server row and a label.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TrueforceForAll.Plugin.Localization;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LocCommunityContractTests
    {
        // Recomputed from real en.json values. The middle one carries U+2026: if
        // anything on this path encodes as Windows-1252 it is the one that moves.
        [Theory]
        [InlineData("Sign in", "bfd402b2f6f3812529b55596136d3a11c51616317e3b1cd999928e2d4eae7d3f")]
        [InlineData("Change…", "a6b4705c688314f0a6e0de5886c082aab286c8fc763490d724218e7f5f53c54e")]
        [InlineData("Signed in as {0}.", "57d8ac41163a34ec577c56848983580e14ffacf51935889a9591bc9537bd698a")]
        public void LocEnglishHashVectors(string value, string expected)
            => Assert.Equal(expected, LocFile.EnglishSha256(value));

        [Fact]
        public void EnglishSha256IsLowercaseHexOf64()
        {
            string h = LocFile.EnglishSha256("anything");
            Assert.Equal(64, h.Length);
            Assert.Equal(h.ToLowerInvariant(), h);
        }

        [Fact]
        public void EnglishSha256DoesNotTrimOrNormalize()
        {
            // The convention is explicit that none of these are the same value, which
            // is what lets a trailing space stay load-bearing in a label.
            Assert.NotEqual(LocFile.EnglishSha256("Save"), LocFile.EnglishSha256("Save "));
            Assert.NotEqual(LocFile.EnglishSha256("a\nb"), LocFile.EnglishSha256("a\r\nb"));
        }

        [Fact]
        public void SerializeWritesMetaFirstThenOrdinalKeys()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Zebra_Key", "z" }, { "Alpha_Key", "a" }, { "_meta", "ignored" },
            };
            string text = LocFile.Serialize(values, "{\"culture\":\"es\"}");
            Assert.StartsWith("{\n  \"_meta\": {\"culture\":\"es\"},\n", text);
            Assert.True(text.IndexOf("Alpha_Key", StringComparison.Ordinal)
                        < text.IndexOf("Zebra_Key", StringComparison.Ordinal),
                        "keys must be ordinal, so a file diff stays readable");
            // The _meta member in the dictionary is not written twice.
            Assert.DoesNotContain("ignored", text);
            Assert.EndsWith("\n}\n", text);
            Assert.DoesNotContain("\r", text);
        }

        [Fact]
        public void SerializeEscapesWhatJsonRequiresAndNothingElse()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "K_Quote", "say \"hi\"" },
                { "K_Break", "one\ntwo" },
                { "K_Accent", "Español" },
                { "K_Control", "bell\u0007" },
            };
            string text = LocFile.Serialize(values, null);
            Assert.Contains("say \\\"hi\\\"", text);
            Assert.Contains("one\\ntwo", text);
            // Accents stay themselves: the files are meant to be readable and greppable.
            Assert.Contains("Español", text);
            Assert.Contains("bell\\u0007", text);
        }

        [Fact]
        public void SerializeRoundTripsThroughTheStoreSParser()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "A_One", "uno" }, { "B_Two", "dos\ntres" }, { "C_Three", "\"cuatro\"" },
            };
            string json = LocFile.Serialize(values, "{\"culture\":\"es\",\"name\":\"Español\"}");
            string metaName;
            var back = LocStore.ParseLanguageJson(json, out metaName, _ => { });
            Assert.Equal("Español", metaName);
            Assert.Equal(3, back.Count);
            foreach (var kv in values) Assert.Equal(kv.Value, back[kv.Key]);
        }

        [Fact]
        public void WriteAtomicReplacesWithoutLosingTheOldFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tf4all-locfile-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "community", "es.json");
            try
            {
                LocFile.WriteAtomic(path, "first");
                Assert.Equal("first", File.ReadAllText(path));
                LocFile.WriteAtomic(path, "second");
                Assert.Equal("second", File.ReadAllText(path));
                // No temp file left behind for the next reader to trip over.
                Assert.False(File.Exists(path + ".tmp"));
                // Written without a BOM, like every other language file.
                byte[] head = File.ReadAllBytes(path);
                Assert.False(head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("no slots here", "")]
        [InlineData("{0} and {1}", "{0}|{1}")]
        [InlineData("{1} before {0}", "{0}|{1}")]      // order cannot matter
        [InlineData("{0} then {0}", "{0}|{0}")]        // a multiset, not a set
        [InlineData("{0:F1} formatted", "{0:F1}")]
        public void PlaceholderSignature(string value, string expected)
            => Assert.Equal(expected, LocPlaceholderRules.Signature(value));

        [Fact]
        public void PlaceholderCheckRefusesADroppedOrInventedSlot()
        {
            Assert.True(LocPlaceholderRules.Check("Saved as {0}.", "Guardado como {0}."));
            Assert.False(LocPlaceholderRules.Check("Saved as {0}.", "Guardado."));
            Assert.False(LocPlaceholderRules.Check("Saved.", "Guardado como {0}."));
            // Twice in the English needs twice in the translation.
            Assert.False(LocPlaceholderRules.Check("{0} of {0}", "{0} de uno"));
        }

        [Theory]
        [InlineData("plain text", false)]
        [InlineData("tabs\tand\nbreaks are fine", false)]
        [InlineData("Español", false)]
        [InlineData("zero​width", true)]        // ZWSP
        [InlineData("bidi‮override", true)]     // RLO, can disguise a string
        [InlineData("Persian‌spelling", false)] // ZWNJ, required to spell Persian
        [InlineData("Arabic‏ mark", false)]     // RLM, punctuation in mixed text
        [InlineData("⁦isolated⁩", false)] // the directional isolates
        [InlineData("bell\u0007", true)]
        [InlineData("bom﻿", true)]
        public void UnsafeCharacters(string value, bool unsafeText)
            => Assert.Equal(unsafeText, LocPlaceholderRules.HasUnsafeChars(value));

        [Fact]
        public void RejectNamesTheFirstThingWrong()
        {
            Assert.Null(LocPlaceholderRules.Reject("Save", "Guardar"));
            Assert.Equal("empty", LocPlaceholderRules.Reject("Save", ""));
            Assert.Contains("control", LocPlaceholderRules.Reject("Save", "Guardar​"));
            Assert.Contains("line break", LocPlaceholderRules.Reject("Save", "Guar\ndar"));
            Assert.Null(LocPlaceholderRules.Reject("one\ntwo", "uno\ndos"));
            Assert.Contains("placeholders", LocPlaceholderRules.Reject("Saved as {0}.", "Guardado."));
        }

        [Fact]
        public void RejectAllowsFourTimesTheEnglishAndNoMore()
        {
            // Short English gets a 64-character floor, so a two-letter label is not
            // held to eight characters.
            Assert.Null(LocPlaceholderRules.Reject("OK", new string('a', 64)));
            Assert.Contains("four times", LocPlaceholderRules.Reject("OK", new string('a', 65)));
            string english = new string('e', 100);
            Assert.Null(LocPlaceholderRules.Reject(english, new string('a', 400)));
            Assert.Contains("four times", LocPlaceholderRules.Reject(english, new string('a', 401)));
        }

        [Fact]
        public void TheBannedListMatchesTheServerSCount()
        {
            // _loc_banned_chars() in migration 0140 returns 70 code points, eight fewer
            // than 0136's: the joiners and the directional marks and isolates came off it
            // because a script that needs them cannot be written without them. Counting
            // here is what keeps the two lists from drifting apart: the server would
            // refuse a row this side had already let through, or the reverse.
            int banned = 0;
            for (int c = 1; c <= 0xFFFF; c++)
                if (LocPlaceholderRules.HasUnsafeChars(((char)c).ToString())) banned++;
            Assert.Equal(70, banned);
        }
    }
}
