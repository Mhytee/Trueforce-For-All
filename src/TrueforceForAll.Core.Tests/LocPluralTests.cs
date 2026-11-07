// The plural rules, asserted count by count.
//
// These are worth testing hard because the failure is invisible: a wrong category shows
// a sentence that is grammatically wrong rather than missing, and nobody files that as a
// bug in a language the maintainer does not read. The counts below are the ones the rules
// actually turn on: 1, 2, 5, 11, 21, 101, and 0 where a language treats it specially.
using System.Collections.Generic;
using TrueforceForAll.Plugin.Localization;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LocPluralTests
    {
        [Theory]
        // English: 1 alone.
        [InlineData("en", 0, "other")]
        [InlineData("en", 1, "one")]
        [InlineData("en", 2, "other")]
        // Spanish and German follow it, and a region does not change a rule.
        [InlineData("es", 1, "one")]
        [InlineData("es", 21, "other")]
        [InlineData("de-DE", 1, "one")]
        [InlineData("de-DE", 5, "other")]
        // Hungarian, Georgian and Turkish read like one-form languages and are not.
        [InlineData("hu", 1, "one")]
        [InlineData("ka", 1, "one")]
        [InlineData("tr", 1, "one")]
        [InlineData("tr", 3, "other")]
        // French: zero takes the singular.
        [InlineData("fr", 0, "one")]
        [InlineData("fr", 1, "one")]
        [InlineData("fr", 2, "other")]
        [InlineData("pt-BR", 0, "one")]
        // One form for every count: the noun does not move.
        [InlineData("ja", 0, "other")]
        [InlineData("ja", 1, "other")]
        [InlineData("ja", 5, "other")]
        [InlineData("zh-Hans-CN", 1, "other")]
        [InlineData("ko-KR", 1, "other")]
        [InlineData("th", 1, "other")]
        // Russian: by the last digit, and the teens are not included.
        [InlineData("ru", 1, "one")]
        [InlineData("ru", 21, "one")]
        [InlineData("ru", 101, "one")]
        [InlineData("ru", 11, "many")]      // the carve-out: 11 is not 1
        [InlineData("ru", 2, "few")]
        [InlineData("ru", 4, "few")]
        [InlineData("ru", 23, "few")]
        [InlineData("ru", 12, "many")]      // and 12 is not 2
        [InlineData("ru", 5, "many")]
        [InlineData("ru", 0, "many")]
        [InlineData("ru", 100, "many")]
        [InlineData("uk", 2, "few")]
        [InlineData("hr", 21, "one")]
        // Polish: 1 alone, then 2 to 4 by last digit.
        [InlineData("pl", 1, "one")]
        [InlineData("pl", 21, "many")]      // unlike Russian, 21 is not 'one' here
        [InlineData("pl", 2, "few")]
        [InlineData("pl", 22, "few")]
        [InlineData("pl", 12, "many")]
        [InlineData("pl", 5, "many")]
        // Czech: 2 to 4 together, and nothing by last digit.
        [InlineData("cs", 1, "one")]
        [InlineData("cs", 2, "few")]
        [InlineData("cs", 4, "few")]
        [InlineData("cs", 5, "other")]
        [InlineData("cs", 22, "other")]     // not 'few': Czech does not look at the digit
        [InlineData("sk", 3, "few")]
        // Lithuanian: the whole teens range is carved out.
        [InlineData("lt", 1, "one")]
        [InlineData("lt", 21, "one")]
        [InlineData("lt", 11, "other")]
        [InlineData("lt", 19, "other")]
        [InlineData("lt", 2, "few")]
        [InlineData("lt", 9, "few")]
        [InlineData("lt", 29, "few")]
        // Latvian has a zero form, and the teens fall into it.
        [InlineData("lv", 0, "zero")]
        [InlineData("lv", 10, "zero")]
        [InlineData("lv", 11, "zero")]
        [InlineData("lv", 19, "zero")]
        [InlineData("lv", 1, "one")]
        [InlineData("lv", 21, "one")]
        [InlineData("lv", 2, "other")]
        // Romanian: zero shares a form with the teens.
        [InlineData("ro", 1, "one")]
        [InlineData("ro", 0, "few")]
        [InlineData("ro", 2, "few")]
        [InlineData("ro", 19, "few")]
        [InlineData("ro", 20, "other")]
        [InlineData("ro", 101, "few")]      // 101 % 100 is 1, inside the range
        // Arabic uses all six.
        [InlineData("ar", 0, "zero")]
        [InlineData("ar", 1, "one")]
        [InlineData("ar", 2, "two")]
        [InlineData("ar", 3, "few")]
        [InlineData("ar", 10, "few")]
        [InlineData("ar", 11, "many")]
        [InlineData("ar", 99, "many")]
        [InlineData("ar", 100, "other")]
        [InlineData("ar", 102, "other")]
        // Hebrew: 1, 2, the tens, the rest.
        [InlineData("he", 1, "one")]
        [InlineData("he", 2, "two")]
        [InlineData("he", 10, "many")]
        [InlineData("he", 20, "many")]
        [InlineData("he", 3, "other")]
        [InlineData("he", 0, "other")]
        // A language the table has never heard of behaves exactly as the runtime did
        // before any of this existed, which is what makes adding one safe.
        [InlineData("xx", 1, "one")]
        [InlineData("xx", 7, "other")]
        public void Category(string tag, int n, string expected)
            => Assert.Equal(expected, LocPlurals.Category(tag, n));

        [Fact]
        public void EveryCategoryAReturnsIsOneTheLanguageDeclares()
        {
            // The contract between the two halves: the Translate window builds rows from
            // FormsFor, and Loc.N looks up whatever Category returns. A category outside
            // the declared set would be a key no translator was ever shown.
            string[] tags = { "en", "es", "fr", "ja", "ru", "pl", "cs", "lt", "lv", "ro", "ar", "he", "xx" };
            foreach (string tag in tags)
            {
                var forms = new HashSet<string>(LocPlurals.FormsFor(tag));
                for (int n = 0; n <= 220; n++)
                    Assert.True(forms.Contains(LocPlurals.Category(tag, n)),
                        tag + " returns '" + LocPlurals.Category(tag, n) + "' for " + n
                        + ", which is not in the forms it declares");
            }
        }

        [Fact]
        public void FormsForIsOrderedAndAlwaysEndsInOther()
        {
            // 'other' is the fallback every lookup lands on, so a language that did not
            // declare it would have rows with nowhere to fall back to.
            string[] tags = { "en", "es", "fr", "ja", "ru", "pl", "cs", "lt", "lv", "ro", "ar", "he", "xx" };
            foreach (string tag in tags)
            {
                var forms = LocPlurals.FormsFor(tag);
                Assert.NotEmpty(forms);
                Assert.Equal("other", forms[forms.Count - 1]);
                Assert.Equal(forms.Count, new HashSet<string>(forms).Count);
            }
        }

        [Fact]
        public void FormCountsPerLanguage()
        {
            Assert.Single(LocPlurals.FormsFor("ja"));
            Assert.Equal(2, LocPlurals.FormsFor("en").Count);
            Assert.Equal(3, LocPlurals.FormsFor("lv").Count);
            Assert.Equal(3, LocPlurals.FormsFor("ro").Count);
            Assert.Equal(4, LocPlurals.FormsFor("ru").Count);
            Assert.Equal(4, LocPlurals.FormsFor("he").Count);
            Assert.Equal(6, LocPlurals.FormsFor("ar").Count);
        }

        [Fact]
        public void NegativeAndExtremeCountsBehave()
        {
            // A count reaching a label should never be negative, but a bound key or a
            // telemetry gap could make it one, and an unhandled case here would throw
            // inside a label rather than read oddly.
            Assert.Equal("one", LocPlurals.Category("en", -1));
            Assert.Equal("few", LocPlurals.Category("ru", -2));
            Assert.Equal("other", LocPlurals.Category("en", int.MaxValue));
            Assert.Equal("many", LocPlurals.Category("ru", int.MinValue));
        }

        [Theory]
        [InlineData("Settings_Files.one", "Settings_Files", "one")]
        [InlineData("Settings_Files.other", "Settings_Files", "other")]
        [InlineData("A_B.few", "A_B", "few")]
        [InlineData("A_B.zero", "A_B", "zero")]
        [InlineData("A_B.two", "A_B", "two")]
        [InlineData("A_B.many", "A_B", "many")]
        public void TrySplitFindsAPluralForm(string key, string expectedBase, string expectedForm)
        {
            string baseKey, form;
            Assert.True(LocPlurals.TrySplit(key, out baseKey, out form));
            Assert.Equal(expectedBase, baseKey);
            Assert.Equal(expectedForm, form);
        }

        [Theory]
        [InlineData("Settings_Close")]          // no dot at all
        [InlineData("Settings_Files.singular")] // a dot, but not a category
        [InlineData("Settings_Files.")]         // nothing after the dot
        [InlineData(".one")]                    // nothing before it
        [InlineData("")]
        [InlineData(null)]
        public void TrySplitLeavesEverythingElseAlone(string key)
        {
            string baseKey, form;
            Assert.False(LocPlurals.TrySplit(key, out baseKey, out form));
            Assert.Equal(key, baseKey);
            Assert.Null(form);
        }
    }
}
