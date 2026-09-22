// Sets one key in an ini file, changing nothing else.
//
// This edits SOMEBODY ELSE'S configuration file. Their comments, their ordering,
// their spacing and their line endings are theirs, and a user who has spent an
// evening tuning MinForce and AlternativeFFB should find every one of those
// values and every one of their notes exactly where they left them. So this does
// not parse-and-rewrite: it finds the one line and replaces it, or inserts a
// single line, and leaves every other byte untouched.
//
// It works on raw text rather than File.ReadAllLines / WriteAllLines because
// that pair silently normalises line endings: an ini written with bare newlines
// would come back rewritten with carriage returns throughout, which is a diff
// across the whole file for a one-key change, and looks to the owner like we
// mangled it.

using System;
using System.Collections.Generic;
using System.Text;

namespace TrueforceForAll.Core
{
    public static class IniKeyWriter
    {
        /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> inside
        /// <paramref name="section"/>. Replaces the key in place when it is already
        /// there, otherwise inserts it at the end of that section.
        ///
        /// Returns the new text, or null with a reason in <paramref name="error"/>
        /// when the section does not exist. A missing section is deliberately NOT
        /// created: an ini without the section we expect is not the file we think
        /// it is, and writing into it would be guessing.</summary>
        public static string SetKey(string text, string section, string key, string value, out string error)
        {
            error = null;
            if (text == null) { error = "the file was empty"; return null; }
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
            { error = "no section or key given"; return null; }

            var lines = SplitKeepingEndings(text);

            string header = "[" + section + "]";
            int sectionAt = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (Content(lines[i]).Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                {
                    sectionAt = i;
                    break;
                }
            }
            if (sectionAt < 0) { error = "there is no [" + section + "] section"; return null; }

            // The section runs until the next header, or the end of the file.
            int sectionEnd = lines.Count;
            for (int i = sectionAt + 1; i < lines.Count; i++)
            {
                string c = Content(lines[i]).Trim();
                if (c.StartsWith("[", StringComparison.Ordinal) && c.EndsWith("]", StringComparison.Ordinal))
                {
                    sectionEnd = i;
                    break;
                }
            }

            for (int i = sectionAt + 1; i < sectionEnd; i++)
            {
                string c = Content(lines[i]);
                string trimmed = c.TrimStart();
                // A commented-out key is left alone: it is the user's note to
                // themselves about a value they turned off, not a setting.
                if (trimmed.StartsWith(";", StringComparison.Ordinal)
                    || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                if (!trimmed.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                lines[i] = key + "=" + value + Ending(lines[i]);
                return Join(lines);
            }

            // Not present: insert at the end of the section, carrying the ending
            // style the file already uses.
            string nl = DominantEnding(lines);

            // Whether the file ends with a newline is part of its shape too. A
            // file that ended cleanly must still end cleanly, and one that did not
            // must not gain a trailing blank line.
            bool endedWithNewline = lines.Count == 0 || Ending(lines[lines.Count - 1]).Length > 0;

            int insertAt = sectionEnd;

            // Keep it inside the section rather than after the blank line that
            // usually separates sections.
            while (insertAt > sectionAt + 1 && Content(lines[insertAt - 1]).Trim().Length == 0) insertAt--;

            // The line before the insert may be the last in the file with no
            // terminator of its own; give it one or the new key lands on its end.
            if (insertAt > 0 && Ending(lines[insertAt - 1]).Length == 0)
                lines[insertAt - 1] = Content(lines[insertAt - 1]) + nl;

            bool atEnd = insertAt >= lines.Count;
            lines.Insert(insertAt, key + "=" + value + (atEnd && !endedWithNewline ? "" : nl));
            return Join(lines);
        }

        /// <summary>Reads a key's value, or null when the section or key is absent.
        /// Commented-out lines are skipped, matching SetKey: a disabled setting is
        /// not a value.</summary>
        public static string GetKey(string text, string section, string key)
        {
            if (text == null || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return null;

            var lines = SplitKeepingEndings(text);
            string header = "[" + section + "]";
            int at = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (Content(lines[i]).Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            }
            if (at < 0) return null;

            for (int i = at + 1; i < lines.Count; i++)
            {
                string c = Content(lines[i]).Trim();
                if (c.StartsWith("[", StringComparison.Ordinal) && c.EndsWith("]", StringComparison.Ordinal)) break;
                if (c.StartsWith(";", StringComparison.Ordinal) || c.StartsWith("#", StringComparison.Ordinal)) continue;
                int eq = c.IndexOf('=');
                if (eq <= 0) continue;
                if (!c.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return c.Substring(eq + 1).Trim();
            }
            return null;
        }

        // Each entry is one line INCLUDING its terminator, so rejoining is exact.
        private static List<string> SplitKeepingEndings(string text)
        {
            var outp = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                outp.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
            if (start < text.Length) outp.Add(text.Substring(start));
            return outp;
        }

        private static string Content(string line)
        {
            int n = line.Length;
            while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) n--;
            return line.Substring(0, n);
        }

        private static string Ending(string line) => line.Substring(Content(line).Length);

        private static string DominantEnding(List<string> lines)
        {
            int crlf = 0, lf = 0;
            foreach (var l in lines)
            {
                string e = Ending(l);
                if (e == "\r\n") crlf++;
                else if (e == "\n") lf++;
            }
            return crlf >= lf && crlf > 0 ? "\r\n" : lf > 0 ? "\n" : Environment.NewLine;
        }

        private static string Join(List<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l);
            return sb.ToString();
        }
    }
}
