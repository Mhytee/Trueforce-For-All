// The rules a translation has to satisfy before it reaches a label, on the client
// side of the same contract the server checks.
//
// Both sides check because both sides can be the one that is out of date: the
// server refuses a bad submission, and the plugin re-checks every fetched row
// against its own English, because the row was written for whatever English that
// server held and this build may display another. A row that fails here is
// dropped, not shown: the key falls through to the shipped translation or to
// English, which is always readable, where a mismatched placeholder would throw
// inside string.Format and cost the whole string.
//
// Pure, so Core.Tests compiles this file directly on net8.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocPlaceholderRules
    {
        // {0} and {0:F1}: the same pattern Get-LocPlaceholders uses, so the
        // signatures the tools compute and the ones the plugin computes agree.
        private static readonly Regex Placeholder = new Regex(@"\{\d+(?::[^{}]*)?\}", RegexOptions.Compiled);

        /// <summary>Every placeholder in the value, sorted, joined by '|'. A
        /// multiset, not a set: an English with {0} twice needs a translation with
        /// {0} twice, or string.Format hands the same argument to one slot and the
        /// sentence loses the other.</summary>
        public static string Signature(string value)
        {
            var found = new List<string>();
            foreach (Match m in Placeholder.Matches(value ?? "")) found.Add(m.Value);
            found.Sort(StringComparer.Ordinal);
            return string.Join("|", found.ToArray());
        }

        /// <summary>Whether a translation may stand in for this English.</summary>
        public static bool Check(string english, string translation)
            => Signature(english) == Signature(translation);

        // The characters no value may carry, the same list as _loc_banned_chars() in
        // migration 0140. Narrower than 0136's, which refused text that other writing
        // systems need in order to be written at all: Persian cannot be spelled without
        // the zero-width non-joiner, Indic conjuncts need the joiner, and mixed
        // Arabic-and-Latin needs a directional mark to put its punctuation in the right
        // place. What stays refused is what can make a string render as something other
        // than itself.
        //
        // Both sides hold the list because either can be the one out of date. The count
        // is asserted in Core.Tests against the number this file and the migration agree
        // on, which is what stops them drifting.
        private static readonly HashSet<char> Banned = BuildBanned();

        private static HashSet<char> BuildBanned()
        {
            var set = new HashSet<char>();
            for (int c = 1; c <= 8; c++) set.Add((char)c);        // C0 below TAB
            set.Add((char)11); set.Add((char)12);                 // VT, FF
            for (int c = 14; c <= 31; c++) set.Add((char)c);      // C0 above CR
            set.Add((char)127);                                  // DEL
            for (int c = 128; c <= 159; c++) set.Add((char)c);    // C1
            set.Add((char)0x200B);                               // ZWSP: padding, invisible text
            set.Add((char)0x2028); set.Add((char)0x2029);         // LS, PS: break a one-line label
            for (int c = 0x202A; c <= 0x202E; c++) set.Add((char)c);  // LRE..RLO: disguise text
            set.Add((char)0xFEFF);                               // ZWNBSP, a stray BOM
            return set;
        }

        /// <summary>Whether the value carries a character no translation may. NUL is
        /// included here although Postgres cannot store one, because a file on disk
        /// can.</summary>
        public static bool HasUnsafeChars(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (c == '\0' || Banned.Contains(c)) return true;
            return false;
        }

        /// <summary>The whole client-side gate for one fetched row, in the order the
        /// design states so a drop is attributed to the first thing wrong with it.
        /// Returns null when the row may serve, else a short reason for the log.</summary>
        public static string Reject(string english, string translation)
        {
            if (string.IsNullOrEmpty(translation)) return "empty";
            if (HasUnsafeChars(translation)) return "control or invisible character";
            if (translation.IndexOf('\n') >= 0 && (english ?? "").IndexOf('\n') < 0)
                return "a line break its English does not have";
            int max = Math.Max(64, 4 * (english ?? "").Length);
            if (translation.Length > max) return "longer than four times the English";
            if (!Check(english, translation)) return "placeholders differ from the English";
            return null;
        }
    }
}
