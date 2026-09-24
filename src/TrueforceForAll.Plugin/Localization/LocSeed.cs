// Writes the reference copies of every embedded language into
// <languagesRoot>\shipped\<tag>.json at each start, when the SHA-256 of the
// embedded bytes differs from the file on disk, plus a _README.txt saying
// what the folder is for. Also the one place that knows how the language
// files are named inside the DLL (see the csproj EmbeddedResource item), so
// Init, the diagnostics and the store read them through the same door.
//
// Nothing here is fatal: a folder that cannot be written costs the user the
// reference copies, not the translations, which come from the DLL.
//
// Design and phases: docs/localization-plan.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TrueforceForAll.Plugin.Localization
{
    internal static class LocSeed
    {
        /// <summary>The LogicalName prefix the csproj gives Languages\*.json.</summary>
        internal const string ResourcePrefix = "TrueforceForAll.Plugin.Languages.";
        internal const string ResourceSuffix = ".json";
        internal const string ReadmeName = "_README.txt";

        /// <summary>Every culture tag this build embeds, "en" among them.</summary>
        internal static IReadOnlyList<string> EmbeddedTags(Assembly assembly)
        {
            var tags = new List<string>();
            try
            {
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                    if (!name.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;
                    string tag = name.Substring(ResourcePrefix.Length, name.Length - ResourcePrefix.Length - ResourceSuffix.Length);
                    if (tag.Length > 0) tags.Add(tag);
                }
            }
            catch { }
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            return tags;
        }

        /// <summary>The raw bytes of an embedded language file, or null.</summary>
        internal static byte[] ReadEmbeddedBytes(Assembly assembly, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            using (Stream s = assembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix))
            {
                if (s == null) return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>An embedded language file as text (UTF-8, BOM dropped),
        /// or null when the build has none for the tag. What Loc.Initialize
        /// is handed as readEmbedded.</summary>
        internal static string ReadEmbeddedText(Assembly assembly, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            using (Stream s = assembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix))
            {
                if (s == null) return null;
                using (var reader = new StreamReader(s, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>Seed the folder: shipped\ copies for every embedded
        /// language and the README, each written only when its content
        /// differs from what is on disk.</summary>
        internal static void Run(Assembly assembly, string languagesRoot, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(languagesRoot)) return;
            string shipped = Path.Combine(languagesRoot, LocStore.ShippedFolder);
            try
            {
                Directory.CreateDirectory(languagesRoot);
                Directory.CreateDirectory(shipped);
            }
            catch (Exception ex)
            {
                Warn(log, "[TF4ALL] Language folder " + languagesRoot + " could not be created: " + ex.Message
                    + ". Translations still load from the plugin; only the reference copies are missing.");
                return;
            }

            foreach (string tag in EmbeddedTags(assembly))
            {
                try
                {
                    byte[] bytes = ReadEmbeddedBytes(assembly, tag);
                    if (bytes == null) continue;
                    WriteIfDifferent(Path.Combine(shipped, tag + ResourceSuffix), bytes);
                }
                catch (Exception ex)
                {
                    Warn(log, "[TF4ALL] Language reference copy " + tag + ".json could not be written: " + ex.Message);
                }
            }

            try
            {
                WriteIfDifferent(Path.Combine(languagesRoot, ReadmeName), new UTF8Encoding(false).GetBytes(ReadmeText));
            }
            catch (Exception ex)
            {
                Warn(log, "[TF4ALL] Language folder README could not be written: " + ex.Message);
            }
        }

        private static void WriteIfDifferent(string path, byte[] bytes)
        {
            if (File.Exists(path) && Sha256Hex(File.ReadAllBytes(path)) == Sha256Hex(bytes)) return;
            File.WriteAllBytes(path, bytes);
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void Warn(Action<string> log, string message)
        {
            try { log?.Invoke(message); } catch { }
        }

        // English by design: this is the translator's briefing, and the plugin's
        // English is the text every translation is made from.
        private const string ReadmeText =
"Trueforce For All: language files\r\n" +
"\r\n" +
"This folder holds the text the plugin shows in its settings panel, one JSON\r\n" +
"file per language, so a translation can be fixed or added without rebuilding\r\n" +
"the plugin.\r\n" +
"\r\n" +
"shipped\\<language>.json\r\n" +
"    The text built into this version of the plugin, written here for\r\n" +
"    reference at every SimHub start. Do not edit these files: they are\r\n" +
"    rewritten whenever the plugin updates and your changes would be lost.\r\n" +
"\r\n" +
"<language>.json (in this folder, next to this file)\r\n" +
"    Your corrections. Copy only the keys you want to change out of the\r\n" +
"    shipped file into a file of the same name here, keep the \"_meta\" object,\r\n" +
"    and your text wins for those keys. Everything else keeps coming from the\r\n" +
"    built-in text. The plugin picks the file up within a second of saving;\r\n" +
"    no restart is needed.\r\n" +
"\r\n" +
"    Example, es.json:\r\n" +
"    {\r\n" +
"      \"_meta\": { \"culture\": \"es\", \"name\": \"Español\" },\r\n" +
"      \"Settings_SaveButton\": \"Guardar\"\r\n" +
"    }\r\n" +
"\r\n" +
"Language names follow SimHub's own Language setting, for example \"es\",\r\n" +
"\"de-DE\", \"fr-FR\" or \"zh-Hans-CN\". A regional file such as es-MX.json sits\r\n" +
"on top of es.json, and anything neither defines falls back to English.\r\n" +
"\r\n" +
"Placeholders such as {0} and {1} are filled in by the plugin; keep them in\r\n" +
"your text, in whichever order reads well. A key that ends in \".one\" is used\r\n" +
"for a count of exactly one and its \".other\" twin for every other count.\r\n" +
"\r\n" +
"Found a mistake, or finished a translation you would like shipped with the\r\n" +
"plugin so everyone gets it? Send the file to\r\n" +
"https://github.com/Mhytee/Trueforce-For-All/issues or to the Discord server,\r\n" +
"https://discord.gg/sfwsDqTsdn. The SimHub log (Logs\\SimHub.txt) reports each\r\n" +
"language at start, with the keys it lacks, on lines beginning\r\n" +
"\"[TF4ALL] Language\".\r\n";
    }
}
