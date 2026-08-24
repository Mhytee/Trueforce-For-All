// The user's own library of wheel light Patterns.
//
// The wheel stores five custom slots. We store as many Patterns as the user
// likes and swap what is currently written into ONE designated slot, so that
// slot behaves like a folder rather than a preset: cycling walks the library
// and writes the next entry into it. The wheel is unchanged, the user gets
// unlimited Patterns.
//
// Order is meaningful: it IS the cycle order, so reordering is a real edit
// rather than cosmetic sorting.
//
// Self-contained (Newtonsoft + BCL only) so it link-compiles into the tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    /// <summary>One named layout: how the strip fills and what each LED is.</summary>
    public sealed class LightPattern
    {
        /// <summary>Stable identity, so renaming or reordering never breaks a
        /// reference to a layout (a per-car assignment, the cycle position).</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Device direction value 1..4: 1 inside-out, 2 outside-in,
        /// 3 left-to-right, 4 right-to-left. Hardware-verified on a G PRO: the
        /// stored value genuinely drives the animation.</summary>
        public byte DirectionWire { get; set; } = 3;

        /// <summary>Hex, 3 bytes per LED, PHYSICAL order with LED 1 (the LEFTMOST,
        /// hardware-confirmed) first. Stored as text so the file stays readable
        /// and hand-editable.</summary>
        public string RgbHex { get; set; }

        /// <summary>Send these colours to the wheel exactly as stored, skipping
        /// the LED colour trim.
        ///
        /// Patterns normally hold sRGB INTENT: what the colour means on a
        /// screen, which the trim converts into what this particular wheel has
        /// to be sent to render it. That is right for anything picked on
        /// screen, and it is what lets a pattern travel to someone else's wheel
        /// and still look like itself.
        ///
        /// It is wrong for a pattern the user tuned BY EYE on the rim, or one
        /// imported out of the wheel's own slots. Those bytes already account
        /// for the wheel, so trimming them corrects the same error twice.
        /// Adopted-from-wheel patterns therefore default to exempt.
        ///
        /// Absent in the file means false, so everything already on disk keeps
        /// being trimmed.</summary>
        public bool TrimExempt { get; set; }

        /// <summary>Where this came from, for the UI to label it: "wheel" for a
        /// slot we imported, "builtin" for a shipped template, "user" for one they
        /// made. Never used for logic, only for showing provenance.</summary>
        public string Origin { get; set; }

        /// <summary>Which of the wheel's five custom slots this pattern lives in,
        /// or -1 for one that exists only in the plugin.
        ///
        /// DERIVED FROM POSITION, never set by hand: the first five patterns in
        /// the list are the five the wheel is holding, and everything below is in
        /// the plugin. Moving a pattern up into the top five puts it on the wheel
        /// and moving it down takes it off, so where it sits IS where it lives.
        /// Kept as a field only so it survives a round trip through the file and
        /// can be read without the list to hand.</summary>
        public int Slot { get; set; } = -1;

        /// <summary>Stored on the wheel, so it still works with SimHub closed.
        /// </summary>
        public bool OnWheel => Slot >= 0;

        public byte[] Rgb() => LightSlotBackupStore.FromHex(RgbHex);

        public LightPattern Clone() => new LightPattern
        {
            Id = Id, Name = Name, DirectionWire = DirectionWire,
            RgbHex = RgbHex, Origin = Origin, Slot = Slot,
            TrimExempt = TrimExempt,
        };
    }

    /// <summary>The ordered library, persisted as one file.</summary>
    public sealed class LightPatternLibrary
    {
        public int Version { get; set; } = 1;

        /// <summary>Ordered. Index is cycle position.</summary>
        public List<LightPattern> Patterns { get; set; } = new List<LightPattern>();

        /// <summary>Id of the pattern currently written into the borrowed slot,
        /// so cycling resumes where it left off across restarts.</summary>
        public string CurrentId { get; set; }

        /// <summary>The pattern the USER last chose, as opposed to one a car's
        /// data put there. Their choice is a standing one, so it is restored when
        /// a car has nothing of its own to say. Automatic applies deliberately do
        /// NOT touch this, or the first car with published data would quietly
        /// redefine what the user had picked.</summary>
        public string StickyId { get; set; }

        /// <summary>True once we have imported what was already on the wheel, so
        /// a user who deletes an imported layout does not get it back next
        /// launch. Slot ASSIGNMENT still reconciles every time; this only governs
        /// whether unrecognised slot contents are ADDED as new patterns.</summary>
        public bool ImportedFromWheel { get; set; }
    }

    /// <summary>Loads, saves and edits the layout library.</summary>
    public sealed class LightPatternStore
    {
        private readonly string _path;
        public Action<string> Log;

        public LightPatternStore(string filePath) { _path = filePath; }

        public LightPatternLibrary Load()
        {
            try
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path))
                {
                    var lib = JsonConvert.DeserializeObject<LightPatternLibrary>(
                        File.ReadAllText(_path), SafeJson.Settings);
                    if (lib != null)
                    {
                        // Collections must never come back null: the rest of the
                        // code treats an empty library as "nothing yet", and a
                        // null would be an exception instead.
                        if (lib.Patterns == null) lib.Patterns = new List<LightPattern>();
                        lib.Patterns.RemoveAll(l => l == null);
                        foreach (var l in lib.Patterns)
                            if (string.IsNullOrEmpty(l.Id)) l.Id = NewId();

                        // A pattern imported out of the wheel's own slots holds
                        // bytes the user already tuned by eye, so it must never
                        // be trimmed. Libraries written before TrimExempt
                        // existed have no flag, and on a calibrated wheel those
                        // patterns would mismatch every comparison and re-upload
                        // their slots on every tab open.
                        if (lib.Version < 2)
                        {
                            foreach (var l in lib.Patterns)
                                if (string.Equals(l.Origin, "wheel", StringComparison.OrdinalIgnoreCase))
                                    l.TrimExempt = true;
                            lib.Version = 2;
                        }
                        return lib;
                    }
                }
            }
            catch (Exception ex) { Warn("library unreadable, starting empty: " + ex.Message); }
            return new LightPatternLibrary();
        }

        public void Save(LightPatternLibrary lib)
        {
            if (lib == null) return;
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(lib, Formatting.Indented),
                                  new UTF8Encoding(false));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex) { Warn("library write failed: " + ex.Message); }
        }

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        private void Warn(string m)
        {
            var l = Log;
            if (l != null) { try { l("[TF4ALL] light library: " + m); } catch { } }
        }

        // ---------------- editing ----------------

        /// <summary>Append a layout, giving it an id and a name that does not
        /// collide with an existing one.</summary>
        public static LightPattern Add(LightPatternLibrary lib, string name, byte direction, byte[] rgb, string origin,
                                       bool trimExempt = false)
        {
            if (lib == null) return null;
            var layout = new LightPattern
            {
                Id = NewId(),
                Name = UniqueName(lib, string.IsNullOrWhiteSpace(name) ? "Layout" : name.Trim()),
                DirectionWire = (direction >= 1 && direction <= 4) ? direction : (byte)3,
                RgbHex = LightSlotBackupStore.ToHex(rgb),
                Origin = origin,
                TrimExempt = trimExempt,
            };
            lib.Patterns.Add(layout);
            NormalizeSlots(lib);
            return layout;
        }

        /// <summary>"GT3" twice becomes "GT3" and "GT3 (2)". Names are the handle
        /// the user reaches for, so two identical ones would be a trap.</summary>
        public static string UniqueName(LightPatternLibrary lib, string wanted)
        {
            if (lib?.Patterns == null || lib.Patterns.Count == 0) return wanted;
            var taken = new HashSet<string>(lib.Patterns.Where(l => l?.Name != null).Select(l => l.Name),
                                            StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(wanted)) return wanted;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = wanted + " (" + n + ")";
                if (!taken.Contains(candidate)) return candidate;
            }
            return wanted + " " + NewId();
        }

        public static bool Remove(LightPatternLibrary lib, string id)
        {
            if (lib?.Patterns == null || string.IsNullOrEmpty(id)) return false;
            int i = lib.Patterns.FindIndex(l => l.Id == id);
            if (i < 0) return false;
            lib.Patterns.RemoveAt(i);
            if (lib.CurrentId == id) lib.CurrentId = null;
            // Everything below has shifted up, so one of them may have just moved
            // onto the wheel.
            NormalizeSlots(lib);
            return true;
        }

        /// <summary>Move a layout to a new position. Order is cycle order, so this
        /// is what lets a user put their favourites next to each other.</summary>
        public static bool Move(LightPatternLibrary lib, string id, int newIndex)
        {
            if (lib?.Patterns == null || string.IsNullOrEmpty(id)) return false;
            int i = lib.Patterns.FindIndex(l => l.Id == id);
            if (i < 0) return false;
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= lib.Patterns.Count) newIndex = lib.Patterns.Count - 1;
            if (newIndex == i) return false;
            var item = lib.Patterns[i];
            lib.Patterns.RemoveAt(i);
            lib.Patterns.Insert(newIndex, item);
            // Position IS residency: this move may have put something on the
            // wheel or taken it off.
            NormalizeSlots(lib);
            return true;
        }

        public static bool Rename(LightPatternLibrary lib, string id, string name)
        {
            if (lib?.Patterns == null || string.IsNullOrWhiteSpace(name)) return false;
            var l = lib.Patterns.FirstOrDefault(x => x.Id == id);
            if (l == null) return false;
            if (string.Equals(l.Name, name.Trim(), StringComparison.Ordinal)) return false;
            // Exclude itself when checking, or renaming to its own name would
            // append a "(2)".
            var others = new LightPatternLibrary { Patterns = lib.Patterns.Where(x => x.Id != id).ToList() };
            l.Name = UniqueName(others, name.Trim());
            return true;
        }

        // ---------------- cycling ----------------

        /// <summary>The layout <paramref name="step"/> places along from the one
        /// showing now, wrapping at both ends. Returns null on an empty library.
        /// Wrapping rather than stopping because this is driven from a wheel
        /// button, where hitting an invisible end feels broken.</summary>
        public static LightPattern Step(LightPatternLibrary lib, int step)
        {
            if (lib?.Patterns == null || lib.Patterns.Count == 0) return null;
            int current = lib.Patterns.FindIndex(l => l.Id == lib.CurrentId);
            if (current < 0) current = step >= 0 ? -1 : 0;   // nothing showing: first press lands on entry 0
            int n = lib.Patterns.Count;
            int next = ((current + step) % n + n) % n;
            return lib.Patterns[next];
        }

        // ---------------- shipped patterns ----------------

        /// <summary>Patterns everyone gets, so the library is never empty and a
        /// new user has something to cycle on day one.
        ///
        /// Stored in PHYSICAL order (LED 1, the leftmost, first). Every one of
        /// these has been looked at on a G PRO and kept because it earned a
        /// place; three earlier entries were dropped rather than shipped. So
        /// treat the list as curated: adding one means putting it on a wheel
        /// first, not just liking the hex.
        ///
        /// Most of them were tuned BY EYE at the shipped colour trim, so the
        /// stored bytes are not meant to read correctly as sRGB. What matters is
        /// what comes out the far side of LedColorGain, and moving the shipped
        /// gains means retuning these rather than just recompiling.
        ///
        /// Direction values are the device's own: 1 inside-out, 2 outside-in,
        /// 3 left-to-right, 4 right-to-left.</summary>
        public static readonly (string Name, byte Direction, string Hex)[] Builtins =
        {
            // Teal deepening across the bar into blue, ending on magenta.
            ("Deep Water",   3, "001414002828003C3C005050006464007878008C8C2F157D02034FF907D7"),
            // Magenta into red, finishing green.
            ("Magenta Rush", 3, "FF00FFFF00FFFF00FFFF00FFFF0000FF0000FF0000FF000000FF0000FF00"),
            // Yellow through orange into red: a heat ramp.
            ("Heat",         3, "FFB400FFA000FF8C00FF7800FF6400FF5000FF3C00FF2800FF1400FF0000"),
            // Warm at the top, cooling into purple as it runs back.
            ("Sunset",       3, "FFD080FFA850FF8030FF5820F03828D02040A018607010704B0C7A4B0875"),
            // Blue and green at the ends, magenta meeting in the middle.
            ("Neon Mirror",  2, "0000FF00FFAD00FF6C3916FBFF0037FF00373916FB00FF6C00FFAD0000FF"),
            // Ten distinct colours. Unmistakable, so it shows instantly which end
            // a strip starts from and which way it fills.
            ("Rainbow",      3, "FF0000FF6000FFFF0000FF0000FF8000FFFF0080FF0000FF8000FFFF00FF"),

            // The rest are ours rather than anyone's slots. Five slots means five
            // patterns on the wheel, so without a decent number below the line
            // there is nothing to move up and nothing to cycle into.

            // The canonical rev bar: five green, three amber, two red. Only two
            // red, because a ten-LED strip that turns red early reads as angry
            // long before the shift actually matters.
            ("Classic",      3, "00FF0000FF0000FF0000FF0000FF00FFA000FFA000FFA000FF0000FF0000"),
            // The same idea folded in half. A mirrored fill drives the strip in
            // pairs, so the bands have to be even: four green, four amber, two
            // red, rather than the five/three/two that only works in a line.
            ("Classic Mirror", 2, "00C80000C800FFA000FFA000FF0000FF0000FFA000FFA00000C80000C800"),
            // Pale blue deepening to navy.
            //
            // Read as sRGB this looks like a set of saturated blues rather than
            // a pale ramp, and it is meant to: it was tuned BY EYE on the rim at
            // the shipped colour trim, so what matters is what comes out the far
            // side of that trim, not how the stored bytes read on a screen. Move
            // the shipped gains and this pattern needs retuning.
            ("Ice",          3, "559DFF4995FF3D8EFF3186FF247FFF186FFF125BF00C47E00633E1001EFF"),
            // Barely-lit at the ends, white hot where the two halves meet.
            ("Ember",        1, "FF0200DC0600AF1000FF7000FFD070FFD070FF7000AF1000DC0600FF0200"),
            // Three hard bands with a dark LED between them. The gaps make the
            // moment it crosses from one band to the next unmissable.
            ("Traffic",      3, "00FF0000FF0000FF00000000FFB000FFB000FFB000000000FF0000FF0000"),
            // Dim through to full white. The one to reach for when checking the
            // brightness control, since nothing else is competing for the eye.
            ("Mono",         3, "101010282828404040585858707070888888A0A0A0C0C0C0E0E0E0FFFFFF"),
            // Ice's blues split at the ends and meeting in the middle.
            ("Ice Split",    2, "0040FF0080FF00C0FF80E0FFFFFFFFFFFFFF80E0FF00C0FF0080FF0040FF"),
        };

        /// <summary>Put the shipped patterns into an empty library. Only ever
        /// fills a library that has none, so a user who deletes one does not get
        /// it back.</summary>
        public static int AddBuiltins(LightPatternLibrary lib)
        {
            if (lib == null || lib.Patterns.Count > 0) return 0;
            foreach (var b in Builtins)
                Add(lib, b.Name, b.Direction, LightSlotBackupStore.FromHex(b.Hex), "builtin");
            return Builtins.Length;
        }

        // ---------------- slots ----------------

        /// <summary>Make every pattern's Slot agree with where it sits. The first
        /// five ARE the wheel's five slots, in order; everything after them lives
        /// in the plugin. Called after anything that changes the order.</summary>
        public static void NormalizeSlots(LightPatternLibrary lib)
        {
            if (lib?.Patterns == null) return;
            for (int i = 0; i < lib.Patterns.Count; i++)
            {
                var p = lib.Patterns[i];
                if (p == null) continue;
                p.Slot = i < WheelLedChannel.CustomSlotCount ? i : -1;
            }
        }

        /// <summary>Put the list in the order the WHEEL is already in, so the
        /// first time a user opens the tab the top five are the five they can
        /// actually see on the rim. Only meaningful once: after this, position is
        /// what drives the wheel rather than the other way round.
        ///
        /// A slot whose colours we already hold ADOPTS that pattern rather than
        /// adding a copy, which is what keeps one pattern to one entry. The
        /// shipped built-ins are copies of real slots, so without that every user
        /// would start with a duplicate of each.</summary>
        /// <summary>Do these two strips hold the same pattern? Compared with a
        /// tolerance, NOT byte for byte.
        ///
        /// The question being asked is "is this the same pattern", and the
        /// colour trim stands between a stored pattern and the bytes that
        /// reached the wheel. Nudge the trim by a fraction of one count and
        /// every byte can round differently, so an exact test answers "no" for a
        /// pattern that is obviously the same one. That really happened: moving
        /// green from 0.60557 to 0.606 made four of five slots fail to match,
        /// and they were re-adopted as CUSTOM 2 to CUSTOM 5 while the named
        /// originals were pushed off the wheel.
        ///
        /// Two counts is safe. Two genuinely different patterns do not sit
        /// within two counts on all thirty bytes; two roundings of the same
        /// pattern always do.
        ///
        /// Note this is the opposite of what SyncSlotsToWheel wants, and
        /// deliberately so: that asks "is the wheel already holding exactly what
        /// we would send", where any difference is a real reason to write.</summary>
        public static bool SamePattern(byte[] a, byte[] b, int tolerance = 2)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > tolerance) return false;
            return true;
        }

        /// <param name="wireBytes">Turns a PATTERN into the bytes it becomes on
        /// the wire. Patterns normally hold sRGB intent, so on a calibrated
        /// wheel the bytes in a slot are the trimmed form and matching on raw
        /// values would adopt nothing and add a duplicate of every pattern. It
        /// takes the pattern rather than its bytes because the decision is per
        /// pattern: an exempt one goes out untrimmed, so its wire form IS its
        /// stored form. Defaults to the stored bytes, which is exactly right for
        /// an uncalibrated wheel and keeps this class free of any dependency on
        /// settings so it still link-compiles into the tests.</param>
        public static int AdoptWheelOrder(LightPatternLibrary lib,
                                          Func<int, WheelLedChannel.WheelLedSlot> readSlot,
                                          int slotCount, bool allowAdd,
                                          Func<LightPattern, byte[]> wireBytes = null)
        {
            if (lib == null || readSlot == null) return 0;
            if (wireBytes == null) wireBytes = p => p?.Rgb();
            int added = 0;

            for (int slot = 0; slot < slotCount; slot++)
            {
                WheelLedChannel.WheelLedSlot s = null;
                try { s = readSlot(slot); } catch { }
                if (s?.Rgb == null || s.Rgb.Length < 3) continue;   // unreadable: leave the order alone
                if (s.Rgb.All(b => b == 0)) continue;               // never programmed: holds no pattern

                var match = lib.Patterns.FirstOrDefault(p => p != null
                    && SamePattern(wireBytes(p), s.Rgb)
                    && p.DirectionWire == s.DirectionWire);

                if (match == null)
                {
                    if (!allowAdd) continue;
                    // Straight out of the wheel, so already in the wheel's own
                    // space. Never trim these.
                    match = Add(lib, "CUSTOM " + (slot + 1), s.DirectionWire, s.Rgb, "wheel", trimExempt: true);
                    added++;
                }

                // Move it to this slot's position. Slots are walked in order, so
                // the ones already placed stay where they were put.
                lib.Patterns.Remove(match);
                lib.Patterns.Insert(Math.Min(slot, lib.Patterns.Count), match);
            }

            NormalizeSlots(lib);
            return added;
        }


    }
}
