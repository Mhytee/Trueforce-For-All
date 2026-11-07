// What Loc.N actually shows, per language, when a form is present, absent, or was never
// a form English had.
//
// The rules themselves are asserted in LocPluralTests. These are about the lookup around
// them, and the property that matters is that every path lands on something a reader can
// understand: an untranslated Russian 'few' must read as English, never as a key in
// brackets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using TrueforceForAll.Plugin.Localization;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LocPluralRuntimeTests
    {
        private static string Lang(string culture, params (string Key, string Value)[] pairs)
        {
            var o = new JObject
            {
                [LocStore.MetaMember] = new JObject { ["culture"] = culture, ["name"] = "Test " + culture },
            };
            foreach (var p in pairs) o[p.Key] = p.Value;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static LocStore Store(Dictionary<string, string> embedded, out List<string> log)
        {
            var captured = new List<string>();
            log = captured;
            Func<string, string> read = tag => embedded.TryGetValue(tag, out string json) ? json : null;
            // No languages root: these tests are about the table, not the folder.
            return new LocStore(read, null, captured.Add);
        }

        private static Dictionary<string, string> English() => new Dictionary<string, string>
        {
            ["en"] = Lang("en", ("Files.one", "{0} file"), ("Files.other", "{0} files")),
        };

        [Fact]
        public void EnglishUsesItsTwoForms()
        {
            var store = Store(English(), out _);
            store.Load("en");
            Assert.Equal("1 file", store.N("Files", 1, 1));
            Assert.Equal("2 files", store.N("Files", 2, 2));
            Assert.Equal("0 files", store.N("Files", 0, 0));
        }

        [Fact]
        public void RussianPicksTheFormForTheCount()
        {
            var embedded = English();
            embedded["ru"] = Lang("ru",
                ("Files.one", "{0} fayl"), ("Files.few", "{0} fayla"),
                ("Files.many", "{0} faylov"), ("Files.other", "{0} faylov"));
            var store = Store(embedded, out _);
            store.Load("ru");
            // The count picks the form and is also the argument: N does not fill {0} on
            // its own, so a caller passes the number twice, as Loc.N's callers all do.
            Assert.Equal("1 fayl", store.N("Files", 1, 1));      // one
            Assert.Equal("21 fayl", store.N("Files", 21, 21));   // one, by last digit
            Assert.Equal("2 fayla", store.N("Files", 2, 2));     // few
            Assert.Equal("23 fayla", store.N("Files", 23, 23));  // few
            Assert.Equal("5 faylov", store.N("Files", 5, 5));    // many
            Assert.Equal("11 faylov", store.N("Files", 11, 11)); // many: 11 is not 1
        }

        [Fact]
        public void AFormTheLanguageHasNotTranslatedFallsToItsOwnOther()
        {
            // Russian declares four forms and this file has two of them. A count that
            // wants 'few' should read as Russian, using the form Russian itself uses for
            // the counts it did not single out, rather than dropping to English.
            var embedded = English();
            embedded["ru"] = Lang("ru", ("Files.one", "{0} fayl"), ("Files.other", "{0} faylov"));
            var store = Store(embedded, out _);
            store.Load("ru");
            Assert.Equal("2 faylov", store.N("Files", 2, 2));
            Assert.Equal("1 fayl", store.N("Files", 1, 1));
        }

        [Fact]
        public void ALanguageWithNoPluralsAtAllReadsAsEnglish()
        {
            // The case that must never show a bracketed key: a translator has started the
            // language but not reached this string.
            var embedded = English();
            embedded["ru"] = Lang("ru", ("Other_Key", "something"));
            var store = Store(embedded, out _);
            store.Load("ru");
            Assert.Equal("1 file", store.N("Files", 1, 1));
            Assert.Equal("5 files", store.N("Files", 5, 5));
            Assert.DoesNotContain("[", store.N("Files", 5, 5));
        }

        [Fact]
        public void OneFormLanguagesUseOtherForEveryCount()
        {
            var embedded = English();
            embedded["ja"] = Lang("ja", ("Files.other", "{0} ファイル"));
            var store = Store(embedded, out _);
            store.Load("ja");
            Assert.Equal("1 ファイル", store.N("Files", 1, 1));
            Assert.Equal("2 ファイル", store.N("Files", 2, 2));
            Assert.Equal("100 ファイル", store.N("Files", 100, 100));
        }

        [Fact]
        public void ArabicReachesItsSixthForm()
        {
            var embedded = English();
            embedded["ar"] = Lang("ar",
                ("Files.zero", "no files ar"), ("Files.one", "one file ar"),
                ("Files.two", "two files ar"), ("Files.few", "few files ar"),
                ("Files.many", "many files ar"), ("Files.other", "other files ar"));
            var store = Store(embedded, out _);
            store.Load("ar");
            Assert.Equal("no files ar", store.N("Files", 0));
            Assert.Equal("one file ar", store.N("Files", 1));
            Assert.Equal("two files ar", store.N("Files", 2));
            Assert.Equal("few files ar", store.N("Files", 3));
            Assert.Equal("many files ar", store.N("Files", 11));
            Assert.Equal("other files ar", store.N("Files", 100));
        }

        [Fact]
        public void FrenchTreatsZeroAsSingular()
        {
            var embedded = English();
            embedded["fr"] = Lang("fr", ("Files.one", "{0} fichier"), ("Files.other", "{0} fichiers"));
            var store = Store(embedded, out _);
            store.Load("fr");
            Assert.Equal("0 fichier", store.N("Files", 0, 0));
            Assert.Equal("1 fichier", store.N("Files", 1, 1));
            Assert.Equal("2 fichiers", store.N("Files", 2, 2));
        }

        [Fact]
        public void AKeyWithNoPluralFormsAnywhereStillReadsAndWarnsOnce()
        {
            var embedded = new Dictionary<string, string>
            {
                ["en"] = Lang("en", ("Plain", "just text")),
            };
            var store = Store(embedded, out var log);
            store.Load("en");
            Assert.Equal("just text", store.N("Plain", 3));
            Assert.Contains(log, m => m.Contains("no plural form") && m.Contains("Plain.other"));
            int before = log.Count;
            store.N("Plain", 4);
            Assert.Equal(before, log.Count);   // said once, not per call
        }

        [Fact]
        public void EnglishForAPluralFormFollowsTheEnglishPair()
        {
            var store = Store(English(), out _);
            store.Load("ru");
            // The singular translates the singular; every other form translates the
            // plural, because that is all English has.
            Assert.Equal("{0} file", store.EnglishForPluralKey("Files.one"));
            Assert.Equal("{0} files", store.EnglishForPluralKey("Files.other"));
            Assert.Equal("{0} files", store.EnglishForPluralKey("Files.few"));
            Assert.Equal("{0} files", store.EnglishForPluralKey("Files.many"));
            Assert.Equal("{0} files", store.EnglishForPluralKey("Files.zero"));
            Assert.Null(store.EnglishForPluralKey("Nothing.few"));
        }

        [Fact]
        public void TheFormsAKeyNeedsFollowTheLanguageBeingEdited()
        {
            // The language being edited, not the one on screen: a translator starting
            // Russian is reading the plugin in English, and Russian has no file yet, so
            // asking the active language would have shown them English's two forms.
            var store = Store(English(), out _);
            store.Load("en");

            Assert.True(store.IsPluralFormFor("en", "Files.one"));
            Assert.True(store.IsPluralFormFor("en", "Files.other"));
            Assert.False(store.IsPluralFormFor("en", "Files.few"));   // English has no few

            Assert.True(store.IsPluralFormFor("ru", "Files.few"));    // Russian does
            Assert.True(store.IsPluralFormFor("ru", "Files.many"));
            Assert.False(store.IsPluralFormFor("ru", "Files.two"));   // and no two

            Assert.True(store.IsPluralFormFor("ar", "Files.two"));
            Assert.True(store.IsPluralFormFor("ja", "Files.other"));
            Assert.False(store.IsPluralFormFor("ja", "Files.one"));   // one form, and it is 'other'

            // Not a plural key, and a plural form whose base English never had.
            Assert.False(store.IsPluralFormFor("ru", "Files"));
            Assert.False(store.IsPluralFormFor("ru", "Missing.few"));
        }
    }
}
