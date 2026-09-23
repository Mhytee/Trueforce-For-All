// Centralized sanitization of user-supplied text before UI rendering.
// Strips control characters (except newlines in multiline), Unicode
// bidi-override characters, and zero-width joiners that could be used
// to mislead users about content identity.
namespace TrueforceForAll.Plugin
{
    internal static class UiContentSanitizer
    {
        // Unicode bidirectional overrides and zero-width formatters.
        // LRE U+202A, RLE U+202B, PDF U+202C, LRO U+202D, RLO U+202E,
        // ZWSP U+200B, ZWNJ U+200C, ZWJ U+200D, LRM U+200E, RLM U+200F,
        // BOM U+FEFF.
        private static readonly char[] BidiAndZeroWidthChars = new char[]
        {
            '‪', '‫', '‬', '‭', '‮',
            '​', '‌', '‍', '‎', '‏',
            '﻿',
        };

        private static bool IsControlChar(char ch)
        {
            // All chars below U+0020 are C0 control codes (includes \t \n \r).
            return ch < ' ';
        }

        private static bool IsBidiOrZeroWidth(char ch)
        {
            for (int i = 0; i < BidiAndZeroWidthChars.Length; i++)
                if (BidiAndZeroWidthChars[i] == ch) return true;
            return false;
        }

        // Sanitize a single-line text field for safe UI display.
        // Strips control characters (< 0x20), Unicode bidirectional overrides,
        // and zero-width characters. Truncates to maxLen. Returns null if input
        // is null or becomes empty after stripping.
        public static string SafeDisplayText(string input, int maxLen = 256)
        {
            if (string.IsNullOrEmpty(input)) return null;

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (IsControlChar(ch)) continue;
                if (IsBidiOrZeroWidth(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= maxLen) break;
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        // Sanitize a multi-line text field for safe UI display.
        // Strips control characters and bidirectional/zero-width chars.
        // Truncates to maxLen characters and maxLines lines.
        // Normalizes line endings to \n. Returns null if input is null or
        // becomes empty after stripping.
        public static string SafeMultiLineText(string input, int maxLen = 1024, int maxLines = 20)
        {
            if (string.IsNullOrEmpty(input)) return null;

            var sb = new System.Text.StringBuilder(input.Length);
            int lineCount = 0;
            foreach (var ch in input)
            {
                if (IsControlChar(ch))
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                        {
                            sb.Append('\n');
                            lineCount++;
                        }
                        if (lineCount >= maxLines) break;
                    }
                    continue;
                }
                if (IsBidiOrZeroWidth(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= maxLen) break;
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
    }
}
