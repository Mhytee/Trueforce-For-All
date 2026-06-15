// Phase 2 backup/sync, milestone M1: bundle the on-disk USER preset library into a
// backup envelope, and restore it on a second PC.
//
// The user's presets are the source of truth (the runtime Settings.Presets /
// CarOverrides / *Defaults dicts are caches rebuilt from these files on Init, and
// are CLEARED post-migration, so they serialize empty; see UserPresets.cs). We copy
// each file as OPAQUE TEXT keyed by its root-relative path: no parsing, so a preset
// authored on a newer plugin survives a restore on an older one, and the exact
// folder layout (the cars/<GameName>/... grouping that the loader does not keep in
// memory) is preserved byte-for-byte.
//
// Factory presets live in a SIBLING folder (TrueforceForAll/factory), so walking the
// user folder excludes them automatically. The import drop-inbox and hidden working
// dirs (.cleanup-*, .tfren-*) are transient and skipped.

using System;
using System.Collections.Generic;
using System.IO;

namespace TrueforceForAll.Plugin
{
    /// <summary>Thrown by <see cref="BackupLibrary.Bundle"/> when the library exceeds the backup
    /// size ceiling. A distinct type so the auto-sync push can treat it as PERMANENT (don't
    /// retry-spin) rather than a transient build failure.</summary>
    public sealed class BackupTooLargeException : Exception
    {
        public BackupTooLargeException(string message) : base(message) { }
    }

    /// <summary>File-side of the backup: read/write the user preset library folder.
    /// Pairs with <see cref="BackupProjection"/> (the settings side) to form a full
    /// <see cref="BackupEnvelope"/>.</summary>
    public static class BackupLibrary
    {
        // Transient subfolder under the user root that must never be backed up.
        private const string ImportInbox = "import";

        // Safety ceiling so a pathological folder can't produce a huge payload.
        // A normal library is a few hundred KB; power users a couple MB.
        public const long MaxTotalChars = 25L * 1024 * 1024;

        /// <summary>Read every user preset file under <paramref name="userFolder"/>
        /// into a (root-relative, forward-slash path) -> file text map. Skips the
        /// import inbox and any dot-prefixed working dir. Empty map if the folder is
        /// absent. Throws if the library blows past <see cref="MaxTotalChars"/>.</summary>
        public static Dictionary<string, BackupFile> Bundle(string userFolder)
        {
            var map = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(userFolder) || !Directory.Exists(userFolder)) return map;

            string root = Path.GetFullPath(userFolder);
            long total = 0;
            foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            {
                string rel = RelPath(root, path);
                if (rel == null || IsSkipped(rel)) continue;

                string text;
                DateTime mtime;
                try { text = File.ReadAllText(path); mtime = File.GetLastWriteTimeUtc(path); }
                catch { continue; }   // unreadable file: skip, keep bundling the rest

                total += text.Length;
                if (total > MaxTotalChars)
                    throw new BackupTooLargeException(
                        $"Preset library exceeds the {MaxTotalChars / (1024 * 1024)} MB backup limit.");

                map[rel] = new BackupFile { Text = text, ModifiedUtc = mtime };
            }
            return map;
        }

        /// <summary>Stable content hash (FNV-1a 64-bit, hex) of a preset's text. Deterministic
        /// across machines, so the 3-way merge can detect edits by CONTENT (reliable) rather than
        /// by mtime (which isn't comparable across PCs).</summary>
        public static string Hash(string text)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
                ulong h = offset;
                if (text != null)
                    foreach (char ch in text) { h ^= ch; h *= prime; }
                return h.ToString("x16");
            }
        }

        /// <summary>Path -> content-hash map of the current library: the last-synced baseline for
        /// the 3-way merge + delete-propagation. Same enumeration + skip rules as Bundle.</summary>
        public static Dictionary<string, string> Manifest(string userFolder)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Bundle(userFolder))
                map[kv.Key] = Hash(kv.Value.Text);
            return map;
        }

        /// <summary>Cheap change-detection fingerprint of the library: a hash over each file's
        /// path + last-write-time + size, read from directory METADATA only (no file content).
        /// Lets the auto-sync backstop skip the full content re-hash when nothing changed, so the
        /// periodic check stays fast even for a large library. Same enumeration + skip rules as
        /// Bundle. Catches add / delete / rename / edit (any of which moves a file's name, mtime,
        /// or size).</summary>
        public static string Fingerprint(string userFolder)
        {
            if (string.IsNullOrEmpty(userFolder) || !Directory.Exists(userFolder)) return "empty";
            var root = new DirectoryInfo(Path.GetFullPath(userFolder));
            string rootFull = root.FullName;
            var entries = new List<string>();
            foreach (var fi in root.EnumerateFiles("*.json", SearchOption.AllDirectories))
            {
                string rel = RelPath(rootFull, fi.FullName);
                if (rel == null || IsSkipped(rel)) continue;
                entries.Add(rel + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length);
            }
            entries.Sort(StringComparer.Ordinal);
            return Hash(string.Join("\n", entries));
        }

        /// <summary>Write a bundled library back under <paramref name="userFolder"/>.
        /// Each relative path is validated to stay within the root (no traversal, no
        /// absolute paths). Additive-overwrite: files in the bundle are written, files
        /// already present but NOT in the bundle are left alone (restore never wipes
        /// the user's existing presets). Caller reloads UserPresets afterwards.</summary>
        /// <summary>Returns the number of entries that could NOT be written (rejected
        /// path or I/O failure) so the caller can surface a partial restore.</summary>
        public static int Restore(string userFolder, IDictionary<string, BackupFile> library,
            IReadOnlyCollection<string> deleteIfAbsent = null)
        {
            if (string.IsNullOrEmpty(userFolder) || library == null) return 0;
            Directory.CreateDirectory(userFolder);
            string root = Path.GetFullPath(userFolder);

            int skipped = 0;
            foreach (var kv in library)
            {
                string rel = kv.Key;
                var file = kv.Value;
                if (string.IsNullOrEmpty(rel) || IsSkipped(rel) || file == null) continue;

                string dest = SafeJoin(root, rel);
                if (dest == null) { skipped++; continue; }   // rejected: traversal or absolute path

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.WriteAllText(dest, file.Text ?? string.Empty);
                    // Preserve the original edit time so newest-wins stays meaningful:
                    // a restored-but-unedited file must not look newer than its origin.
                    if (file.ModifiedUtc.Year > 2000)
                        File.SetLastWriteTimeUtc(dest, file.ModifiedUtc);
                }
                catch { skipped++; /* keep restoring the rest */ }
            }

            // Delete-propagation (auto-sync only): remove files that were in the last-synced
            // baseline but are absent from this authoritative bundle (deleted on the other PC; the
            // merge already kept any edited survivors). Only baseline paths are eligible, so a file
            // that was never synced (local-only) is never touched. Manual restore passes null here.
            if (deleteIfAbsent != null)
            {
                foreach (var rel in deleteIfAbsent)
                {
                    if (string.IsNullOrEmpty(rel) || IsSkipped(rel) || library.ContainsKey(rel)) continue;
                    string dest = SafeJoin(root, rel);
                    if (dest == null) continue;
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best-effort */ }
                }
            }
            return skipped;
        }

        /// <summary>Merge two bundled libraries: union all paths, and on a path present
        /// in BOTH keep the file with the newer <see cref="BackupFile.ModifiedUtc"/>
        /// (the agreed newest-wins rule). Inputs are never mutated.</summary>
        public static Dictionary<string, BackupFile> MergeNewestWins(
            IDictionary<string, BackupFile> local, IDictionary<string, BackupFile> cloud)
        {
            var merged = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
            if (local != null)
                foreach (var kv in local)
                    if (kv.Value != null) merged[kv.Key] = kv.Value;
            if (cloud != null)
                foreach (var kv in cloud)
                {
                    if (kv.Value == null) continue;
                    if (!merged.TryGetValue(kv.Key, out var have) || have == null
                        || kv.Value.ModifiedUtc > have.ModifiedUtc)
                        merged[kv.Key] = kv.Value;   // cloud-only, or cloud is newer
                }
            return merged;
        }

        private static bool IsSkipped(string rel)
        {
            // rel is forward-slash, root-relative. Skip the import inbox and any
            // hidden/working segment (dot-prefixed: .cleanup-*, .tfren-*, ...).
            foreach (var seg in rel.Split('/'))
            {
                if (seg.Length == 0) continue;
                if (seg[0] == '.') return true;
                if (string.Equals(seg, ImportInbox, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string RelPath(string root, string fullPath)
        {
            string full = Path.GetFullPath(fullPath);
            string rootWithSep = WithSep(root);
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return null;
            return full.Substring(rootWithSep.Length).Replace('\\', '/');
        }

        // Join a root-relative path under root, rejecting absolute paths and any "../"
        // attempt to escape the root.
        private static string SafeJoin(string root, string rel)
        {
            string normalized = rel.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized)) return null;

            string combined = Path.GetFullPath(Path.Combine(root, normalized));
            if (!combined.StartsWith(WithSep(root), StringComparison.OrdinalIgnoreCase)) return null;
            return combined;
        }

        private static string WithSep(string dir)
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            return dir.EndsWith(sep, StringComparison.Ordinal) ? dir : dir + sep;
        }
    }
}
