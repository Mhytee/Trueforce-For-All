// Which language tags reach the picker.
//
// The pairs below are real rows from CultureInfo.GetCultures(NeutralCultures) on net48,
// written out rather than enumerated: the culture table differs between net48 and net8,
// so enumerating here would test the test host. The failure this guards against is
// silent in both directions, a language listed twice or a language missing from the
// picker altogether, and neither is something a maintainer who does not read the
// language would catch.
using System;
using System.Collections.Generic;
using TrueforceForAll.Plugin.Localization;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class LocLanguageListTests
    {
        // Real tags and the names they call themselves, as net48 reports them.
        private static Dictionary<string, string> Table() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pa", "ਪੰਜਾਬੀ" },        { "pa-Guru", "ਪੰਜਾਬੀ" },
            { "uz", "o‘zbek" },        { "uz-Latn", "o‘zbek" },   { "uz-Cyrl", "Ўзбек" },
            { "bs", "bosanski" },      { "bs-Latn", "bosanski" },
            { "mn", "монгол" },        { "mn-Cyrl", "монгол" },
            { "sr", "srpski" },        { "sr-Latn", "srpski" },   { "sr-Cyrl", "српски" },
            { "zh", "中文" },           { "zh-Hans", "中文(简体)" }, { "zh-Hant", "中文(繁體)" },
            { "en", "English" },       { "es", "español" },
            { "nd", "isiNdebele" },    { "nr", "isiNdebele" },
            { "tzm", "Tamaziɣt n laṭlaṣ" }, { "tzm-Tfng", "ⵜⴰⵎⴰⵣⵉⵖⵜ" },
            { "zgh", "ⵜⴰⵎⴰⵣⵉⵖⵜ" },     { "zgh-Tfng", "ⵜⴰⵎⴰⵣⵉⵖⵜ" },
        };

        private static bool Restates(string tag)
        {
            var table = Table();
            return LocLanguageList.RestatesItsLanguage(tag, table[tag], table);
        }

        [Theory]
        // The script only restates what the language is already written in, so the two
        // rows read as the same language twice and the qualified one goes.
        [InlineData("pa-Guru")]
        [InlineData("uz-Latn")]
        [InlineData("bs-Latn")]
        [InlineData("mn-Cyrl")]
        [InlineData("sr-Latn")]
        [InlineData("zgh-Tfng")]
        public void AScriptThatRestatesItsLanguageIsDropped(string tag)
            => Assert.True(Restates(tag));

        [Theory]
        // A bare language is never dropped: it is the tag to keep, because a file
        // written for it serves every region and script of that language.
        [InlineData("pa")]
        [InlineData("uz")]
        [InlineData("en")]
        [InlineData("zh")]
        [InlineData("nd")]
        [InlineData("nr")]
        // A script that reads differently is its own entry. Dropping these would take
        // a language out of the picker with nothing to say it had gone.
        [InlineData("zh-Hans")]
        [InlineData("zh-Hant")]
        [InlineData("sr-Cyrl")]
        [InlineData("uz-Cyrl")]
        [InlineData("tzm-Tfng")]
        public void EverythingElseStays(string tag)
            => Assert.False(Restates(tag));

        [Fact]
        public void TwoLanguagesSharingANameAreBothKept()
        {
            // North and South Ndebele are both isiNdebele and are not the same
            // language. The picker tells them apart by adding the English name; what
            // this rule must not do is collapse one into the other, and it cannot,
            // because neither is the other's base language.
            Assert.False(Restates("nd"));
            Assert.False(Restates("nr"));
        }

        [Fact]
        public void ATagWhoseBaseIsNotListedStays()
        {
            // A regional file someone wrote, or a test locale, with no base language
            // beside it in the table.
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "qps-ploc", "qps-ploc" },
                { "es-419", "español de Latinoamérica" },
            };
            Assert.False(LocLanguageList.RestatesItsLanguage("qps-ploc", "qps-ploc", table));
            Assert.False(LocLanguageList.RestatesItsLanguage(
                "es-419", "español de Latinoamérica", table));
        }

        [Fact]
        public void MissingOrEmptyNamesAreNotEvidence()
        {
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "xx", "" }, { "xx-Yyyy", "" }, { "zz", "Zed" },
            };
            // Two empty names are not a match: collapsing on them would drop a tag on
            // no grounds at all.
            Assert.False(LocLanguageList.RestatesItsLanguage("xx-Yyyy", "", table));
            Assert.False(LocLanguageList.RestatesItsLanguage("zz-Wwww", "Zed", null));
            Assert.False(LocLanguageList.RestatesItsLanguage(null, "Zed", table));
            Assert.False(LocLanguageList.RestatesItsLanguage("", "Zed", table));
            // A leading dash is not a base language either.
            Assert.False(LocLanguageList.RestatesItsLanguage("-Latn", "Zed", table));
        }
    }
}
