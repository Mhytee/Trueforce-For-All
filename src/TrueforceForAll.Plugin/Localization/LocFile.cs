// Language files on disk, and the hash that names an English.
//
// Three jobs, all pure so Core.Tests compiles this file directly on net8:
//
//   Serialize     the exact shape Write-LocJson produces, so a file this writes
//                 and a file the tools write are byte-identical. A translator's
//                 own file and the plugin's community cache read the same way.
//   WriteAtomic   temp then File.Replace, the path CommunityBrowseCacheStore
//                 already uses, so a killed process cannot leave a half file
//                 where a language used to be.
//   EnglishSha256 SHA-256 over the UTF-8 bytes of the value, lowercase hex. This
//                 is what lets a mixed-version fleet share one server: a row
//                 names the English it translated, and an install applies it only
//                 when that hash matches the English the install itself shows.
//                 No trimming, no newline or Unicode normalization, no BOM, and
//                 the key is not part of the input. See section 4 of
//                 docs/localization-translation-service.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocFile
    {
        /// <summary>Write-LocJson's shape: "_meta" first, every other key in
        /// ordinal order, two-space indent, LF, no BOM, one trailing newline.
        /// Hand-rolled rather than through a serializer because the shape is the
        /// contract: a serializer's escaping or ordering could change under us and
        /// the diff against a tools-written file is what proves it has not.</summary>
        public static string Serialize(IDictionary<string, string> values, string meta)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("{\n");
            bool first = true;
            if (!string.IsNullOrEmpty(meta))
            {
                sb.Append("  \"_meta\": ").Append(meta);
                first = false;
            }
            foreach (string key in keys)
            {
                if (key == LocStore.MetaMember) continue;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(Quote(key)).Append(": ").Append(Quote(values[key] ?? ""));
            }
            sb.Append("\n}\n");
            return sb.ToString();
        }

        /// <summary>JSON string escaping, matching what the tools emit: the two
        /// mandatory escapes, the five short forms, and \u for the rest of C0.
        /// Everything above stays as its own character, which is why the files are
        /// readable in any editor and why a translator can grep them.</summary>
        public static string Quote(string value)
        {
            var sb = new StringBuilder(value == null ? 2 : value.Length + 2);
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Write text where a reader may already be looking. The temp file
        /// lands beside the target so Replace stays on one volume, and a failure
        /// leaves the previous file untouched rather than truncated.</summary>
        public static void WriteAtomic(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(temp, text, utf8NoBom);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>SHA-256 over the UTF-8 bytes, lowercase hex, 64 characters.
        /// The one place the plugin computes it.</summary>
        public static string EnglishSha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
