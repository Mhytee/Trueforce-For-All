using System;
using System.Collections.Generic;
using System.IO;

namespace TrueforceForAll.Core
{
    /// <summary>Assetto Corsa's own rev-light writer, as a setting we can hand
    /// ourselves.
    ///
    /// AC drives the wheel's rev lights through CSP's g27_lights module, over
    /// the same HID++ pipe our wheel-base screen uses. Measured on a G PRO
    /// 2026-09-03: with the screen streaming, 60% of two-second windows saw
    /// AC's light writes stall and 23% saw them stop dead, gaps of five seconds
    /// with the bar frozen where it last landed. One writer at a time is the
    /// only arrangement that works.
    ///
    /// CSP offers no runtime way to silence the module (the Lua API can read
    /// its state and watch for changes, nothing more), so the lever is its
    /// config: the module ACTIVE and its MODE set to DISABLED, which CSP itself
    /// documents as how you hand the lights to an external tool. Both halves
    /// matter and the pairing is a trap: an INACTIVE module leaves AC's own
    /// behaviour in place no matter what its mode says, so "MODE=DISABLED" on
    /// its own looks configured and changes nothing.
    ///
    /// Every edit is surgical (the two keys, nothing else) and the file we
    /// found is copied beside itself first, so Revert puts the user's game back
    /// exactly as it was. Taking a stock feature away from someone's game is
    /// only defensible if giving it back is reliable.</summary>
    public static class AcRevLightModuleConfig
    {
        public const string ModuleId = "g27_lights";

        /// <summary>Marker written in place of a backup when the user had no
        /// override file at all, so Revert knows to delete ours rather than
        /// restore a file that never existed.</summary>
        private const string NoOriginalMarker = "; TF4ALL: no g27_lights override existed before we wrote one.";

        /// <summary>`<Documents>\Assetto Corsa\cfg\extension\g27_lights.ini`,
        /// the per-user override CSP reads on top of its shipped defaults.
        /// Null if the Documents folder cannot be resolved.</summary>
        public static string ConfigPath()
        {
            string docs;
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            catch { return null; }
            if (string.IsNullOrEmpty(docs)) return null;
            return Path.Combine(docs, "Assetto Corsa", "cfg", "extension", ModuleId + ".ini");
        }

        private static string BackupPath(string cfg) => cfg + ".tf4all-original";

        /// <summary>True once we have taken the module over and not yet given
        /// it back, i.e. a backup of the user's own file is standing.</summary>
        public static bool TakenOver(string cfgPath = null)
        {
            string cfg = cfgPath ?? ConfigPath();
            return cfg != null && File.Exists(BackupPath(cfg));
        }

        /// <summary>Set the module ACTIVE with its output DISABLED, so AC stops
        /// writing the wheel's rev lights and we can. Backs the user's file up
        /// the first time. Returns true when the file ends up in the wanted
        /// state (including when it already was).</summary>
        public static bool Apply(Action<string> log, string cfgPath = null)
        {
            string cfg = cfgPath ?? ConfigPath();
            if (cfg == null) { log?.Invoke("[REVLIGHT] cannot resolve the Documents folder; leaving AC's lights alone."); return false; }
            try
            {
                string dir = Path.GetDirectoryName(cfg);
                if (dir != null && !Directory.Exists(dir))
                {
                    // No CSP config folder means no CSP; writing one would be
                    // us inventing configuration for a patch that is not there.
                    log?.Invoke("[REVLIGHT] no CSP config folder; leaving AC's lights alone.");
                    return false;
                }

                string bak = BackupPath(cfg);
                if (!File.Exists(bak))
                {
                    if (File.Exists(cfg)) File.Copy(cfg, bak);
                    else File.WriteAllText(bak, NoOriginalMarker + Environment.NewLine);
                }

                var lines = File.Exists(cfg)
                          ? new List<string>(File.ReadAllLines(cfg))
                          : new List<string>();
                bool changed = SetKey(lines, "BASIC", "ENABLED", "1");
                changed |= SetKey(lines, "BASIC", "MODE", "DISABLED");
                if (!changed) return true;                 // already right

                File.WriteAllLines(cfg, lines);
                log?.Invoke("[REVLIGHT] Assetto Corsa's rev-light module set to ENABLED=1 MODE=DISABLED "
                          + "so it stops writing the wheel's lights and we can drive them. "
                          + $"Your original file is saved at {Path.GetFileName(bak)}; "
                          + "turning our rev lights off puts it back.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[REVLIGHT] could not update AC's rev-light config: {ex.Message}");
                return false;
            }
        }

        /// <summary>Give AC its rev lights back: restore the file we found, or
        /// remove ours if there was none. No-op when we never took it over.</summary>
        public static bool Revert(Action<string> log, string cfgPath = null)
        {
            string cfg = cfgPath ?? ConfigPath();
            if (cfg == null) return false;
            string bak = BackupPath(cfg);
            try
            {
                if (!File.Exists(bak)) return false;
                string first = "";
                try
                {
                    using (var r = new StreamReader(bak)) first = r.ReadLine() ?? "";
                }
                catch { }
                if (first.StartsWith("; TF4ALL: no g27_lights override", StringComparison.Ordinal))
                {
                    if (File.Exists(cfg)) File.Delete(cfg);
                }
                else
                {
                    File.Copy(bak, cfg, true);
                }
                File.Delete(bak);
                log?.Invoke("[REVLIGHT] Assetto Corsa's own rev-light setting restored; the game drives the bar again.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[REVLIGHT] could not restore AC's rev-light config: {ex.Message}");
                return false;
            }
        }

        /// <summary>Set one key inside one section, preserving every other line
        /// (comments and unrelated sections included: this is the user's file,
        /// and the module carries settings we have no business rewriting).
        /// Returns true when the file actually needed changing.</summary>
        private static bool SetKey(List<string> lines, string section, string key, string value)
        {
            int sectionStart = -1, sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length < 2 || t[0] != '[') continue;
                bool isOurs = t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
                if (sectionStart < 0 && isOurs) { sectionStart = i; continue; }
                if (sectionStart >= 0) { sectionEnd = i; break; }
            }

            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return true;
            }

            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                string t = lines[i].TrimStart();
                if (t.Length == 0 || t[0] == ';') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (!t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                // Keep any trailing "; comment" CSP wrote, so the file still
                // reads the way its owner (and Content Manager) expects.
                string rest = t.Substring(eq + 1);
                int semi = rest.IndexOf(';');
                string comment = semi >= 0 ? " " + rest.Substring(semi) : "";
                string current = (semi >= 0 ? rest.Substring(0, semi) : rest).Trim();
                if (current.Equals(value, StringComparison.OrdinalIgnoreCase)) return false;
                lines[i] = key + "=" + value + comment;
                return true;
            }

            lines.Insert(sectionEnd, key + "=" + value);
            return true;
        }
    }
}
