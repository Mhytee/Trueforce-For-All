// Parsing the number a user typed into a slider's readout box.
//
// Pulled out of SettingsControl.CommitReadout so it can be tested: it is the
// one piece of the click-to-type path that is pure logic, it runs for every one
// of the hundred-odd readouts on the settings screen, and it was silently wrong
// for a whole class of users before.
//
// The box is FILLED in invariant form when it takes focus, but the user types
// over that with their own keyboard. On a comma-decimal locale "1,25" matched
// only the "1" and committed 1, with no error and no visible sign, which is the
// worst way for a control to be wrong.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TrueforceForAll.Core
{
    public static class ReadoutNumber
    {
        // A sign, then digits followed by any number of mark-and-digits groups,
        // or a bare fractional part so ".5" is not rejected. The groups repeat so
        // a grouped number stays in one match: with a single optional group
        // "1.234,5" matched just "1.234" and came out as 1.234. Anything after
        // the number is ignored, which is what lets a readout carry its unit
        // ("45 dB", "2.5 s", "8 (2ms)") and still round-trip.
        private static readonly Regex Number =
            new Regex(@"[-+]?(\d+([.,]\d*)*|[.,]\d+)", RegexOptions.CultureInvariant);

        /// <summary>Read the first number out of a readout box's text, accepting
        /// either decimal mark. Returns false when there is no number in it,
        /// which the caller treats as "restore what was showing".</summary>
        public static bool TryParse(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var m = Number.Match(text);
            if (!m.Success) return false;
            return double.TryParse(Normalize(m.Value), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Rewrite a matched number in invariant form. Where both marks
        /// appear the LAST is the decimal one and any earlier ones are grouping,
        /// which reads "1.234,5" and "1,234.5" the same way round.</summary>
        private static string Normalize(string numeric)
        {
            int lastMark = Math.Max(numeric.LastIndexOf('.'), numeric.LastIndexOf(','));
            if (lastMark < 0) return numeric;
            string whole = numeric.Substring(0, lastMark).Replace(".", "").Replace(",", "");
            string frac  = numeric.Substring(lastMark + 1);
            // A trailing mark with no digits after it ("12." while still typing)
            // is a whole number, not a syntax error.
            if (frac.Length == 0) return whole.Length == 0 ? "0" : whole;
            if (whole.Length == 0 || whole == "-" || whole == "+") whole += "0";
            return whole + "." + frac;
        }
    }
}
