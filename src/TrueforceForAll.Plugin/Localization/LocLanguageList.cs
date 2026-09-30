// Which of the framework's language tags belong in the picker.
//
// CultureInfo.GetCultures(NeutralCultures) lists 309 entries on net48, and 75 of them
// are 37 pairs that read as the same language twice: the framework lists a language and
// then lists it again with the script it is already written in spelled out. pa beside
// pa-Guru, uz beside uz-Latn, bs beside bs-Latn, mn beside mn-Cyrl. On screen only the
// language's own name shows, so the picker showed what looked like 37 duplicates.
//
// The bare tag is the one to keep. A file written for it serves every region and script
// of that language, and it is the tag the resolution chain walks to. A script that reads
// differently is a separate entry and stays: zh-Hans and zh-Hant are not one list item,
// and neither are sr and sr-Cyrl.
//
// Pure, so Core.Tests compiles this file directly on net8 and the rule is asserted
// against fixed inputs rather than against whichever culture table the test host
// happens to carry. That matters here: the table differs between net48 and net8, and a
// rule that collapsed too much would drop a language out of the picker entirely, which
// is not something anyone would notice.
using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocLanguageList
    {
        /// <summary>Whether a tag only restates the script its language is already
        /// written in, so it reads as that language a second time. True only when the
        /// base language is in the same list AND calls itself the same thing, which is
        /// exactly what makes the two indistinguishable on screen.</summary>
        public static bool RestatesItsLanguage(string tag, string native,
                                               IDictionary<string, string> nativeByTag)
        {
            if (string.IsNullOrEmpty(tag) || nativeByTag == null) return false;
            int dash = tag.IndexOf('-');
            if (dash <= 0) return false;
            string baseNative;
            if (!nativeByTag.TryGetValue(tag.Substring(0, dash), out baseNative)) return false;
            // A name the framework does not know is not evidence of anything, and
            // collapsing on two empties would drop a tag on no grounds at all.
            if (string.IsNullOrEmpty(baseNative) || string.IsNullOrEmpty(native)) return false;
            return string.Equals(baseNative, native, StringComparison.OrdinalIgnoreCase);
        }
    }
}
