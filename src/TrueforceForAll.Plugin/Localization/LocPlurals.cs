// Which plural form a language uses for a count.
//
// English has two, and the runtime used to assume everyone did: .one when the count was
// 1 and .other for everything else. That is wrong in most of Europe and it is wrong in a
// way nobody reports, because the sentence still appears, just ungrammatical. Russian
// needs three for integers, and "2 файла" against "5 файлов" is grammar rather than
// polish. Polish, Ukrainian, Czech and Croatian are the same shape. Arabic has six.
//
// The rules are CLDR's, for integer counts only, which is all Loc.N is ever given. A
// language this file does not know falls back to one and other, which is exactly what the
// runtime did before, so adding a language here can only improve it and never break it.
//
// Pure, so Core.Tests compiles this file directly on net8 and every rule below is
// asserted rather than believed.
using System;
using System.Collections.Generic;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocPlurals
    {
        public const string Zero = "zero";
        public const string One = "one";
        public const string Two = "two";
        public const string Few = "few";
        public const string Many = "many";
        public const string Other = "other";

        private static readonly string[] OtherOnly = { Other };
        private static readonly string[] OneOther = { One, Other };
        private static readonly string[] OneFewManyOther = { One, Few, Many, Other };
        private static readonly string[] OneFewOther = { One, Few, Other };
        private static readonly string[] ZeroOneOther = { Zero, One, Other };
        private static readonly string[] OneTwoManyOther = { One, Two, Many, Other };
        private static readonly string[] AllSix = { Zero, One, Two, Few, Many, Other };

        /// <summary>The rule a language follows. Named after a representative language
        /// rather than numbered, so a reader can tell what a tag was grouped with.</summary>
        private enum Rule
        {
            /// <summary>One form for every count: the count carries the number and the
            /// noun does not change. Japanese, Chinese, Korean, Thai, Vietnamese.</summary>
            None,
            /// <summary>English: 1 is special, everything else is not.</summary>
            English,
            /// <summary>French: 0 and 1 both take the singular.</summary>
            French,
            /// <summary>Russian, Ukrainian, Croatian: by the last digit, with the teens
            /// carved out.</summary>
            Russian,
            /// <summary>Polish: 1 alone, then 2 to 4 by last digit, then the rest.</summary>
            Polish,
            /// <summary>Czech and Slovak: 1 alone, 2 to 4 together, then the rest.</summary>
            Czech,
            /// <summary>Lithuanian: like Russian but the whole 11 to 19 range is
            /// carved out.</summary>
            Lithuanian,
            /// <summary>Latvian: a zero form, and 1 by last digit.</summary>
            Latvian,
            /// <summary>Romanian: 0 and anything ending 01 to 19 share a form.</summary>
            Romanian,
            /// <summary>Arabic: all six.</summary>
            Arabic,
            /// <summary>Hebrew: 1, 2, the tens, and the rest.</summary>
            Hebrew,
        }

        // By base language, because a plural rule belongs to the language and not to the
        // region: pt-BR and pt-PT count the same way. Anything absent is English's rule,
        // which is what the runtime already did for every language.
        private static readonly Dictionary<string, Rule> Rules =
            new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
            {
                // One form. The count is the number and the noun stays put.
                { "ja", Rule.None }, { "zh", Rule.None }, { "ko", Rule.None },
                { "th", Rule.None }, { "vi", Rule.None }, { "id", Rule.None },
                { "ms", Rule.None }, { "lo", Rule.None }, { "my", Rule.None },
                { "km", Rule.None }, { "yo", Rule.None },
                // Hungarian, Georgian and Turkish read as if they had one form, and do
                // not: all three mark the singular. They are absent on purpose, which
                // gives them English's rule.

                // 0 and 1 both singular.
                { "fr", Rule.French }, { "pt", Rule.French },

                // Slavic by last digit, teens excepted.
                { "ru", Rule.Russian }, { "uk", Rule.Russian }, { "be", Rule.Russian },
                { "sr", Rule.Russian }, { "hr", Rule.Russian }, { "bs", Rule.Russian },

                { "pl", Rule.Polish },
                { "cs", Rule.Czech }, { "sk", Rule.Czech },
                { "lt", Rule.Lithuanian },
                { "lv", Rule.Latvian },
                { "ro", Rule.Romanian }, { "mo", Rule.Romanian },
                { "ar", Rule.Arabic },
                { "he", Rule.Hebrew }, { "iw", Rule.Hebrew },
            };

        /// <summary>The base language of a tag: "pt-BR" to "pt". A plural rule belongs to
        /// the language, not the region.</summary>
        private static string Base(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            int dash = tag.IndexOf('-');
            return dash > 0 ? tag.Substring(0, dash) : tag;
        }

        private static Rule RuleFor(string tag)
        {
            Rule rule;
            return Rules.TryGetValue(Base(tag), out rule) ? rule : Rule.English;
        }

        /// <summary>Every form this language needs, in the order a translator should meet
        /// them. What the Translate window builds its rows from, so a Russian translator
        /// is shown the three forms Russian has rather than the two English does.</summary>
        public static IReadOnlyList<string> FormsFor(string tag)
        {
            switch (RuleFor(tag))
            {
                case Rule.None: return OtherOnly;
                case Rule.Russian:
                case Rule.Polish:
                case Rule.Czech:
                case Rule.Lithuanian: return OneFewManyOther;
                case Rule.Romanian: return OneFewOther;
                case Rule.Latvian: return ZeroOneOther;
                case Rule.Hebrew: return OneTwoManyOther;
                case Rule.Arabic: return AllSix;
                default: return OneOther;
            }
        }

        /// <summary>The form this count takes in this language. Integer counts only, which
        /// is all Loc.N is given.</summary>
        public static string Category(string tag, int n)
        {
            int abs = n == int.MinValue ? int.MaxValue : Math.Abs(n);
            int mod10 = abs % 10;
            int mod100 = abs % 100;
            switch (RuleFor(tag))
            {
                case Rule.None:
                    return Other;

                case Rule.French:
                    // 0 and 1: "0 fichier", singular.
                    return abs <= 1 ? One : Other;

                case Rule.Russian:
                    if (mod10 == 1 && mod100 != 11) return One;                 // 1, 21, 101
                    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return Few;  // 2, 23
                    return Many;                                                 // 5, 11, 100

                case Rule.Polish:
                    if (abs == 1) return One;
                    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return Few;
                    return Many;

                case Rule.Czech:
                    if (abs == 1) return One;
                    if (abs >= 2 && abs <= 4) return Few;
                    return Other;   // 'many' is for fractions, which a count never is

                case Rule.Lithuanian:
                    if (mod10 == 1 && (mod100 < 11 || mod100 > 19)) return One;
                    if (mod10 >= 2 && mod10 <= 9 && (mod100 < 11 || mod100 > 19)) return Few;
                    return Other;   // 'many' is for fractions

                case Rule.Latvian:
                    if (mod10 == 0 || (mod100 >= 11 && mod100 <= 19)) return Zero;
                    if (mod10 == 1 && mod100 != 11) return One;
                    return Other;

                case Rule.Romanian:
                    if (abs == 1) return One;
                    if (abs == 0 || (mod100 >= 1 && mod100 <= 19)) return Few;
                    return Other;

                case Rule.Arabic:
                    if (abs == 0) return Zero;
                    if (abs == 1) return One;
                    if (abs == 2) return Two;
                    if (mod100 >= 3 && mod100 <= 10) return Few;
                    if (mod100 >= 11 && mod100 <= 99) return Many;
                    return Other;   // 100, 101, 200

                case Rule.Hebrew:
                    if (abs == 1) return One;
                    if (abs == 2) return Two;
                    if (abs != 0 && mod10 == 0) return Many;   // 10, 20, 30
                    return Other;

                default:
                    return abs == 1 ? One : Other;
            }
        }

        /// <summary>Where a form sits in the order a translator should meet it, so the
        /// singular comes before the form for 2 to 4 and not after it alphabetically.
        /// Last for a form this language does not have.</summary>
        public static int FormRank(string tag, string form)
        {
            var forms = FormsFor(tag);
            for (int i = 0; i < forms.Count; i++) if (forms[i] == form) return i;
            return forms.Count;
        }

        /// <summary>The first few counts this form covers, for the note beside a row in
        /// the Translate window. Counts rather than the category name because "few" says
        /// nothing to someone who is not a translator by trade, while "2, 3, 4, 22" is the
        /// rule itself. Empty when the form is not one this language has.
        ///
        /// The search stops at 120, which is far enough to show a rule that turns on the
        /// last two digits.</summary>
        public static IReadOnlyList<int> ExampleCounts(string tag, string form, int want, out bool more)
        {
            var found = new List<int>();
            more = false;
            if (string.IsNullOrEmpty(form) || want < 1) return found;
            for (int n = 0; n <= 120; n++)
            {
                if (Category(tag, n) != form) continue;
                if (found.Count < want) { found.Add(n); continue; }
                more = true;
                break;
            }
            return found;
        }

        /// <summary>Whether a key names a plural form, and which base it belongs to. A key
        /// is a plural form when its last dotted part is one of the six categories, which
        /// is also the rule validate.ps1 and the server use.</summary>
        public static bool TrySplit(string key, out string baseKey, out string form)
        {
            baseKey = key;
            form = null;
            if (string.IsNullOrEmpty(key)) return false;
            int dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1) return false;
            string tail = key.Substring(dot + 1);
            if (tail != Zero && tail != One && tail != Two
                && tail != Few && tail != Many && tail != Other) return false;
            baseKey = key.Substring(0, dot);
            form = tail;
            return true;
        }
    }
}
