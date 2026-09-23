// Writes built-in preset files into the folder (directory-scan layout; no
// manifest to keep in sync). The read side is BuiltinPresetStore. This is the
// dev-tooling / DEV-mode write side so normal runtime never touches the folder.
//
// After a batch of writes the caller calls BuiltinPresets.Reload() so the
// in-memory store reflects the new files.

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    internal static class BuiltinPresetWriter
    {
        // Strip characters Windows forbids in file names; keep the rest
        // (spaces / parens stay, so names remain readable). A dot-only result
        // ("." / "..") is a traversal token, not a name, so neutralize it.
        private static string SafeFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Trim();
            return SafePath.IsTraversalSegment(s) ? "preset" : s;
        }

        /// <summary>The name a GAME preset is loaded under (its file stem) for a
        /// given raw name. A game-default binding VALUE must use this so it
        /// resolves to the file the preset was written to; a raw name carrying
        /// characters SafeFile strips ("GT3: race") would otherwise never match
        /// the loaded "GT3_ race" and the game would fall back to the factory
        /// default. Idempotent for already-safe names. NOTE: car presets take
        /// their runtime name from the file's PresetName field, not the
        /// filename (BuiltinPresetStore.LoadCarPresets), so car-default bindings
        /// are NOT sanitized this way.</summary>
        public static string GamePresetLoadName(string name) => SafeFile(name);

        // Write through a temp file + rename so a crash mid-write doesn't leave
        // a truncated/corrupt file (game-defaults.json, car-defaults.json, and
        // preset files are all written here at runtime via Pack Manager). Same
        // pattern as CarPresetStore / InstalledPacksStore.
        private static void AtomicWriteAllText(string path, string content)
        {
            // Unique temp per write: with a fixed ".tmp" name, two concurrent
            // saves of the same file interleave into one temp and the corrupt
            // result can get promoted over the real file.
            string tmp = path + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                File.WriteAllText(tmp, content);
                if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
                else File.Move(tmp, path);
            }
            finally
            {
                // A failed write must not leave its temp behind.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            SweepStaleTemps(path);
        }

        // Delete crash orphans: temps for this file older than an hour (the age
        // gate keeps a concurrent writer's live temp safe), plus the legacy
        // fixed-name temp older builds used.
        private static void SweepStaleTemps(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var stale in Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp"))
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff)
                    {
                        try { File.Delete(stale); } catch { }
                    }
                }
                string legacy = path + ".tmp";
                if (File.Exists(legacy) && File.GetLastWriteTimeUtc(legacy) < cutoff)
                {
                    try { File.Delete(legacy); } catch { }
                }
            }
            catch { }
        }

        // Rename a file safely on a case-insensitive filesystem (Windows).
        // Naive "if exists delete then move" wipes the source when the rename
        // is case-only ("iRacing.json" -> "IRacing.json" is the SAME file),
        // because File.Exists(newPath) returns true and File.Delete erases
        // both. Two-step via a temp filename avoids that.
        private static void SafeRenameFile(string oldPath, string newPath)
        {
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(newPath) ?? "";
                string tmpPath = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(newPath)
                    + ".tfren-" + Guid.NewGuid().ToString("N")
                    + Path.GetExtension(newPath));
                File.Move(oldPath, tmpPath);
                File.Move(tmpPath, newPath);
                return;
            }
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(oldPath, newPath);
        }

        /// <summary>Resolve the absolute path WriteGame / DeleteGame use for a
        /// given preset name, without writing or reading anything. The Pack
        /// Manager calls this to hash the file's current content against the
        /// pack record's BaselineHash before destructive removal.</summary>
        public static string GetGamePresetPath(string folder, string name) =>
            Path.Combine(folder, "games", SafeFile(name) + ".json");

        /// <summary>Write a game preset (GameSettingsSnapshot JSON) to
        /// games/&lt;name&gt;.json. The preset name is taken from the filename
        /// on load, so it round-trips. Returns the relative path.</summary>
        public static string WriteGame(string folder, string name, string snapshotJson)
        {
            Directory.CreateDirectory(Path.Combine(folder, "games"));
            string rel = "games/" + SafeFile(name) + ".json";
            AtomicWriteAllText(Path.Combine(folder, rel), snapshotJson);
            return rel;
        }

        /// <summary>Resolve the absolute path WriteCar / DeleteCar use for a
        /// given car preset, without writing or reading anything. Mirrors
        /// WriteCar's path construction exactly. Used by the one-time legacy-car
        /// migration to avoid clobbering an already-migrated file on a re-run.</summary>
        public static string GetCarPresetPath(string folder, string gameName, string carId, string presetName)
        {
            string g = SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName);
            return Path.Combine(folder, "cars", g, SafeFile(carId), SafeFile(presetName) + ".json");
        }

        /// <summary>Write a car preset (CarPresetFile JSON) to
        /// cars/&lt;GameName&gt;/&lt;carId&gt;/&lt;PresetName&gt;.json. Multiple
        /// presets per car. Returns the relative path.</summary>
        public static string WriteCar(string folder, string gameName, string carId, string presetName, string carPresetJson)
        {
            string g = SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName);
            string dir = Path.Combine(folder, "cars", g, SafeFile(carId));
            Directory.CreateDirectory(dir);
            string rel = "cars/" + g + "/" + SafeFile(carId) + "/" + SafeFile(presetName) + ".json";
            AtomicWriteAllText(Path.Combine(folder, rel), carPresetJson);
            return rel;
        }

        /// <summary>Delete a game preset file (games/&lt;name&gt;.json).</summary>
        public static void DeleteGame(string folder, string name)
        {
            string path = Path.Combine(folder, "games", SafeFile(name) + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Delete a car preset file
        /// (cars/&lt;game&gt;/&lt;carId&gt;/&lt;PresetName&gt;.json) and prune the
        /// car's folder if it's now empty.</summary>
        public static void DeleteCar(string folder, string gameName, string carId, string presetName)
        {
            string g = SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName);
            string carDir = Path.Combine(folder, "cars", g, SafeFile(carId));
            string path = Path.Combine(carDir, SafeFile(presetName) + ".json");
            if (File.Exists(path)) File.Delete(path);
            try { if (Directory.Exists(carDir) && Directory.GetFileSystemEntries(carDir).Length == 0) Directory.Delete(carDir); }
            catch { /* leave the dir if it won't delete */ }
        }

        /// <summary>Rename a car preset file (cars/&lt;game&gt;/&lt;carId&gt;/&lt;old&gt;.json -&gt;
        /// &lt;new&gt;.json) and repoint car-defaults.json from old -&gt; new for
        /// that carId if it matched. The file's inner PresetName needs updating
        /// separately by the caller (this just moves the file).</summary>
        public static void RenameCar(string folder, string gameName, string carId, string oldName, string newName)
        {
            string g = SafeFile(string.IsNullOrEmpty(gameName) ? "Unknown" : gameName);
            string carDir = Path.Combine(folder, "cars", g, SafeFile(carId));
            string oldPath = Path.Combine(carDir, SafeFile(oldName) + ".json");
            string newPath = Path.Combine(carDir, SafeFile(newName) + ".json");
            if (File.Exists(oldPath))
            {
                Directory.CreateDirectory(carDir);
                SafeRenameFile(oldPath, newPath);
            }
            // Repoint car-defaults if it pointed at the old name.
            string dpath = Path.Combine(folder, BuiltinPresetStore.CarDefaultsFileName);
            if (!File.Exists(dpath)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(dpath));
                if (o[carId] != null && (string)o[carId] == oldName)
                {
                    o[carId] = newName;
                    AtomicWriteAllText(dpath, o.ToString(Formatting.Indented));
                }
            }
            catch { /* leave defaults as-is on parse trouble */ }
        }

        /// <summary>Rename a game preset file and repoint any game-defaults
        /// entries from the old name to the new one.</summary>
        public static void RenameGame(string folder, string oldName, string newName)
        {
            string oldPath = Path.Combine(folder, "games", SafeFile(oldName) + ".json");
            string newPath = Path.Combine(folder, "games", SafeFile(newName) + ".json");
            if (File.Exists(oldPath))
            {
                Directory.CreateDirectory(Path.Combine(folder, "games"));
                SafeRenameFile(oldPath, newPath);
            }
            // Repoint defaults old -> new.
            string dpath = Path.Combine(folder, BuiltinPresetStore.GameDefaultsFileName);
            if (!File.Exists(dpath)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(dpath));
                bool changed = false;
                foreach (var p in o.Properties())
                    if ((string)p.Value == oldName) { p.Value = newName; changed = true; }
                if (changed) AtomicWriteAllText(dpath, o.ToString(Formatting.Indented));
            }
            catch { /* leave defaults as-is on parse trouble */ }
        }

        /// <summary>Set a game-default binding (GameName -> built-in preset
        /// name) in game-defaults.json. No-op-safe.</summary>
        public static void SetGameDefault(string folder, string gameName, string presetName)
        {
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(presetName)) return;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, BuiltinPresetStore.GameDefaultsFileName);
            JObject o;
            try { o = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); }
            catch { o = new JObject(); }
            o[gameName] = presetName;
            AtomicWriteAllText(path, o.ToString(Formatting.Indented));
        }

        /// <summary>Remove a game's binding from game-defaults.json. No-op-safe.</summary>
        public static void RemoveGameDefault(string folder, string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return;
            string path = Path.Combine(folder, BuiltinPresetStore.GameDefaultsFileName);
            if (!File.Exists(path)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                if (o.Remove(gameName)) AtomicWriteAllText(path, o.ToString(Formatting.Indented));
            }
            catch { /* ignore */ }
        }

        /// <summary>Set a car's built-in default (carId -> preset name) in
        /// car-defaults.json. No-op-safe.</summary>
        public static void SetCarDefault(string folder, string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, BuiltinPresetStore.CarDefaultsFileName);
            JObject o;
            try { o = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject(); }
            catch { o = new JObject(); }
            o[carId] = presetName;
            AtomicWriteAllText(path, o.ToString(Formatting.Indented));
        }

        /// <summary>Remove a car's binding from car-defaults.json. No-op-safe.</summary>
        public static void RemoveCarDefault(string folder, string carId)
        {
            if (string.IsNullOrEmpty(carId)) return;
            string path = Path.Combine(folder, BuiltinPresetStore.CarDefaultsFileName);
            if (!File.Exists(path)) return;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path));
                if (o.Remove(carId)) AtomicWriteAllText(path, o.ToString(Formatting.Indented));
            }
            catch { /* ignore */ }
        }
    }
}
