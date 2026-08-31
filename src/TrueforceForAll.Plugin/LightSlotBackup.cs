// Safekeeping for a LIGHTSYNC slot we borrow.
//
// Writing a slot PERSISTS on the wheel: it overwrites a saved preset the user
// may have spent time on in G HUB or the base menu, and it survives our process
// exiting. So the rule is simple and absolute: read and store the original
// BEFORE the first write, and be able to put it back afterwards.
//
// The backup deliberately lives on disk rather than in memory. A crash, a
// SimHub kill, or a plugin disable all end the process without giving us a
// chance to restore, and an in-memory copy would take the user's colors with
// it. On disk, the next launch can still make them whole.
//
// MACHINE-LOCAL by nature: it mirrors the state of one physical wheel on one
// PC, so it must never travel in a settings backup to a second machine.
//
// Self-contained (Newtonsoft + BCL only) so it link-compiles into the tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>One saved slot, as it was before we touched it.</summary>
    public sealed class LightSlotBackupEntry
    {
        public byte Slot { get; set; }
        public byte DirectionWire { get; set; }
        /// <summary>Hex, 60 chars, physical LED order. A string so a person can
        /// read the file and see what is stored.</summary>
        public string RgbHex { get; set; }
        public DateTime TakenAt { get; set; }
        /// <summary>Set once we have written the original back. A backup still
        /// marked false at the next launch means a session died holding the
        /// slot, and the restore is owed.</summary>
        public bool Restored { get; set; }
        /// <summary>Which wheel this came from, so a backup taken on one base is
        /// never pushed onto a different one.</summary>
        public string WheelId { get; set; }

        /// <summary>The slot's own name before we borrowed it, so the wheel's
        /// menu reads as theirs again once we give it back. Null when the slot
        /// had no name or it could not be read.</summary>
        public string OriginalName { get; set; }

        /// <summary>DEV ONLY (SLOTBLANK): this slot has been deliberately emptied
        /// to rehearse the factory-wheel first run, and must STAY empty across a
        /// restart for that rehearsal to mean anything.
        ///
        /// A separate flag rather than reusing Restored, which would have been the
        /// obvious shortcut and is a data-loss bug. Restored means "the debt is
        /// paid", and it does two other things: it makes the automatic restore
        /// skip the slot (wanted here) but ALSO makes the next borrow treat the
        /// slot as un-backed-up, re-read it, and record the BLANK as the original
        /// (catastrophic here, because it discards the last copy of the user's own
        /// colors). Blanked keeps the debt open, so the backup is still protected
        /// and SLOTRESTORE still works, while the auto-restore paths leave it
        /// alone.</summary>
        public bool Blanked { get; set; }
    }

    /// <summary>Reads and writes the on-disk slot backups.</summary>
    public sealed class LightSlotBackupStore
    {
        private readonly string _path;
        public Action<string> Log;

        public LightSlotBackupStore(string filePath) { _path = filePath; }

        /// <summary>True when the last <see cref="Load"/> found a file it could
        /// not parse. Absent and unreadable are NOT the same thing: absent means
        /// nothing is borrowed, unreadable means we do not know, and the caller
        /// must refuse to overwrite a slot it cannot promise to put back.</summary>
        public bool LastLoadCorrupt { get; private set; }

        public Dictionary<string, LightSlotBackupEntry> Load()
        {
            LastLoadCorrupt = false;
            var map = new Dictionary<string, LightSlotBackupEntry>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(_path)) return map;

            // Try the live file, then the previous generation Save leaves behind.
            foreach (string candidate in new[] { _path, _path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, LightSlotBackupEntry>>(
                        File.ReadAllText(candidate), SafeJson.Settings);
                    if (loaded == null) { MarkCorrupt(candidate); continue; }
                    foreach (var kv in loaded)
                        if (kv.Value != null) map[kv.Key] = kv.Value;
                    LastLoadCorrupt = false;
                    return map;
                }
                catch (Exception ex)
                {
                    Warn("backup unreadable (" + Path.GetFileName(candidate) + "): " + ex.Message);
                    MarkCorrupt(candidate);
                }
            }
            return map;
        }

        /// <summary>Set aside a file we could not read rather than letting the
        /// next save overwrite it. It may be the only copy of the user's colors,
        /// and a human can still read the hex out of it by hand.</summary>
        private void MarkCorrupt(string path)
        {
            LastLoadCorrupt = true;
            try
            {
                string dead = path + ".corrupt";
                if (!File.Exists(dead)) File.Move(path, dead);
            }
            catch { }
        }

        /// <summary>Returns FALSE if nothing reached the disk. Callers must not
        /// go on to overwrite a wheel slot after a false: the on-disk copy is the
        /// only thing that can give the user their colors back.</summary>
        /// <summary>Serialises writers in this process. Two applies firing at once
        /// is not hypothetical: a car load and a settings change did exactly that.
        /// The unique temp name stops them corrupting each other; this stops them
        /// racing on the replace as well.</summary>
        private static readonly object SaveLock = new object();

        public bool Save(Dictionary<string, LightSlotBackupEntry> map)
        {
            lock (SaveLock)
            try
            {
                if (string.IsNullOrEmpty(_path)) return false;
                string dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(dir)) return false;
                Directory.CreateDirectory(dir);

                // A temp name unique to this write. A fixed ".tmp" meant two
                // saves in flight at once fought over one file and the loser
                // reported "cannot access the file", which surfaced to the user
                // as "could not save a backup of your slot" on a wheel that was
                // perfectly writable. Seen live 2026-08-23 when two car-color
                // applies ran 3 ms apart.
                string tmp = _path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(map, Formatting.Indented),
                                      new UTF8Encoding(false));

                    // Replace is atomic on NTFS and keeps the previous generation
                    // as .bak. The old delete-then-move left a window with NO
                    // backup on disk at all, on the very operation that is about
                    // to overwrite the user's slot.
                    if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", true);
                    else File.Move(tmp, _path);
                    return true;
                }
                finally
                {
                    // A failed write must not leave its scratch file behind, or
                    // the folder fills with them one per failure.
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex) { Warn("backup write FAILED, nothing saved: " + ex.Message); return false; }
        }

        /// <summary>Key for a slot on a given wheel. Keyed by wheel as well as
        /// slot so swapping bases cannot cross-contaminate.</summary>
        public static string KeyFor(string wheelId, int slot)
            => (wheelId ?? "wheel") + "#" + slot.ToString();

        /// <summary>Find the entry for a slot, preferring an exact wheel match but
        /// falling back to ANY unrestored entry for that slot number.
        ///
        /// The fallback matters because the wheel identity is a string that can
        /// legitimately differ between sessions (a discovery miss, an unreadable
        /// USB product name, a future edit to that text). Without it, a changed
        /// identity would orphan a backup that is still owed, the next borrow
        /// would record OUR colors as the original, and the user's real ones
        /// would be gone. An owed debt is worth paying even if we are less sure
        /// which wheel it came from; the alternative is silent loss.</summary>
        public static LightSlotBackupEntry Find(Dictionary<string, LightSlotBackupEntry> map,
                                                string wheelId, int slot, out string key)
        {
            key = KeyFor(wheelId, slot);
            LightSlotBackupEntry exact;
            if (map != null && map.TryGetValue(key, out exact) && exact != null) return exact;

            if (map != null)
            {
                foreach (var kv in map)
                {
                    if (kv.Value == null || kv.Value.Slot != slot || kv.Value.Restored) continue;
                    key = kv.Key;
                    return kv.Value;
                }
            }
            return null;
        }

        private void Warn(string m)
        {
            var l = Log;
            if (l != null) { try { l("[TF4ALL] slot backup: " + m); } catch { } }
        }

        // ---- conversion helpers ----

        public static string ToHex(byte[] rgb)
        {
            if (rgb == null) return string.Empty;
            var sb = new StringBuilder(rgb.Length * 2);
            foreach (byte b in rgb) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        /// <summary>Parse stored hex back to bytes. Returns null for anything
        /// malformed, so a corrupted backup is refused rather than written to the
        /// wheel as garbage colors.</summary>
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim();
            if (hex.Length % 2 != 0) return null;
            var outp = new byte[hex.Length / 2];
            for (int i = 0; i < outp.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out outp[i]))
                    return null;
            }
            return outp;
        }

        public static LightSlotBackupEntry FromSlot(WheelLedChannel.WheelLedSlot s, string wheelId, DateTime now)
            => new LightSlotBackupEntry
            {
                Slot = s.Slot,
                DirectionWire = s.DirectionWire,
                RgbHex = ToHex(s.Rgb),
                TakenAt = now,
                Restored = false,
                WheelId = wheelId,
            };

        /// <summary>Rebuild a writable slot from a backup entry, or null if the
        /// entry is unusable.</summary>
        public static WheelLedChannel.WheelLedSlot ToSlot(LightSlotBackupEntry e)
        {
            if (e == null) return null;
            var rgb = FromHex(e.RgbHex);
            if (rgb == null || rgb.Length < 30) return null;
            return new WheelLedChannel.WheelLedSlot
            {
                Slot = e.Slot,
                DirectionWire = e.DirectionWire,
                Rgb = rgb,
            };
        }
    }
}
