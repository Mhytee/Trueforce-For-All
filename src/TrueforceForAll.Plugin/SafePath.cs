using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrueforceForAll.Plugin
{
    // ZIP Slip + filename sanitization gate. Canonicalizes baseDir + entryFullName
    // via Path.GetFullPath, ensures the result stays within baseDir, and rejects
    // absolute paths plus any '..' segment. Used by archive import paths to keep
    // a malicious ZIP from writing outside the user library or update temp dir.
    internal static class SafePath
    {
        // True iff baseDir + entryFullName resolves to a path strictly under
        // baseDir. Sets safeAbsolute to the canonicalized destination on success.
        public static bool IsSafeArchivePath(string baseDir, string entryFullName,
                                             out string safeAbsolute)
        {
            safeAbsolute = null;
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(entryFullName))
                return false;

            string entryNorm = entryFullName.Replace('\\', '/');
            if (entryNorm.StartsWith("/", StringComparison.Ordinal))
                return false;

            // Reject any '..' segment. net48 has no Contains(string, StringComparison)
            // overload, so use IndexOf with the comparison instead.
            if (entryNorm.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;

            try
            {
                string baseDirFull = Path.GetFullPath(baseDir);
                baseDirFull = baseDirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string entryPath = Path.Combine(baseDirFull, entryNorm.Replace('/', Path.DirectorySeparatorChar));
                string entryFull = Path.GetFullPath(entryPath);

                if (!entryFull.StartsWith(baseDirFull, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Guard against the "baseDir = C:\foo, entry resolves to C:\foobar" case
                // where StartsWith matches but the trailing char is not a separator.
                if (entryFull.Length > baseDirFull.Length)
                {
                    char nextChar = entryFull[baseDirFull.Length];
                    if (nextChar != Path.DirectorySeparatorChar && nextChar != Path.AltDirectorySeparatorChar)
                        return false;
                }

                safeAbsolute = entryFull;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Replace any character that's unsafe in a Windows filename with '_'.
        // Also prefixes reserved DOS device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9)
        // with an underscore and trims trailing dots/spaces.
        public static string SanitizeForFilename(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";

            var invalid = Path.GetInvalidFileNameChars();
            var hashSet = new HashSet<char>(invalid);
            hashSet.Add('/');
            hashSet.Add('\\');
            hashSet.Add(':');
            hashSet.Add('*');
            hashSet.Add('?');
            hashSet.Add('"');
            hashSet.Add('<');
            hashSet.Add('>');
            hashSet.Add('|');
            for (char c = (char)0; c < ' '; c++)
                hashSet.Add(c);

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (hashSet.Contains(c))
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            string result = sb.ToString();

            string[] reserved = { "CON", "PRN", "AUX", "NUL",
                                  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            foreach (var res in reserved)
            {
                if (result.Equals(res, StringComparison.OrdinalIgnoreCase))
                    return "_" + result;
            }

            result = result.TrimEnd(' ', '.');
            if (string.IsNullOrEmpty(result)) return "_";

            return result;
        }
    }
}
