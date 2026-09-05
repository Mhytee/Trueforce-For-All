// The watch list: a label, an address, and a live value, one row at a time.
//
// This is the map. Everything else in the Memory Map tab ends in a VERDICT that
// somebody has to take on trust, and trust has already been misplaced once: a
// manifold-pressure decoy passed every commanded test honestly and was named
// CONFIRMED in a program with no tachometer, because those tests describe the
// pedal rather than the crankshaft. Watching a value while deliberately changing
// the thing it should track is stronger than any statistic, and it exposes that
// decoy in seconds.
//
// So a row is not a debug readout. It carries a name the operator can edit, and
// when its address is written as module+offset it is a map entry that survives a
// relaunch: this target's executable cannot be moved by Windows (RELOCS_STRIPPED,
// fixed ImageBase), so an offset confirmed today resolves tomorrow. That is why
// the list is saved to disk rather than kept for the session.
//
// Deliberately free of WPF, of SimHub and of the plugin's own path helpers: the
// caller passes the file path in. That is what lets this file be link-compiled
// into TrueforceForAll.Core.Tests, where the address parsing that decides whether
// a row ever resolves at all can be pinned.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>One watched address, exactly as it goes on the wire.</summary>
    public sealed class MemoryWatchRow
    {
        /// <summary>Either an absolute hex address ("0x01C02750") or a
        /// module-relative one ("game.exe+0x4A2C10"). The second form is the
        /// whole point of the map: it survives a relaunch. This string is the
        /// row's identity, and it is what an unwatch names, so it is stored in
        /// the exact form it was sent in.</summary>
        [JsonProperty("addr")]
        public string Addr { get; set; }

        /// <summary>One of <see cref="MemoryWatchList.Kinds"/>.</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        /// <summary>Characters for utf16, bytes otherwise. Meaningless, and left
        /// off the wire, for the fixed-width numeric kinds.</summary>
        [JsonProperty("len")]
        public int Len { get; set; }

        /// <summary>What the operator calls it. An unlabelled address is a
        /// number; a labelled one is a map entry.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        // ---- what this row has been reading, this session --------------------
        //
        // Not saved: a value read half an hour ago in a different launch is not
        // a map entry, it is a stale number that would light the row up the
        // first time it was read again and claim a change nobody made.

        /// <summary>The text last shown for this row, which is what a new read
        /// is compared against. Includes the "cannot read" line, so a row that
        /// goes from readable to unreadable counts as the change it is.</summary>
        [JsonIgnore]
        public string LastShown { get; set; }

        /// <summary>False until the first refresh. The first value a row ever
        /// shows is not a change: it is the row arriving.</summary>
        [JsonIgnore]
        public bool HasRead { get; set; }

        /// <summary>How many times this row's value has moved since it was
        /// added. This is the number the operator is looking at when they
        /// change the car.</summary>
        [JsonIgnore]
        public int Changes { get; set; }
    }

    /// <summary>One row of one refresh, after the list has compared it with
    /// what that row was showing before.</summary>
    public sealed class MemoryWatchUpdate
    {
        /// <summary>The list's own row, which carries the label and the running
        /// count.</summary>
        public MemoryWatchRow Row;

        /// <summary>What to put in the value column: the decoded value, or the
        /// reason it could not be read.</summary>
        public string Shown;

        /// <summary>True when this refresh differs from the one before it. The
        /// point of the whole feature: the operator changed the car and is
        /// looking for the row that followed.</summary>
        public bool Changed;

        /// <summary>False when the read failed. The row keeps its place.</summary>
        public bool Ok;

        /// <summary>Where a module-relative row landed this launch, or null.</summary>
        public string Resolved;
    }

    /// <summary>The rows, their validation, and their file. Not thread safe:
    /// every caller is the UI thread or the plugin's own start path.</summary>
    public sealed class MemoryWatchList
    {
        /// <summary>Default string length, in characters for utf16 and bytes
        /// otherwise. The protocol's own default.</summary>
        public const int DefaultLen = 32;

        /// <summary>The protocol's cap on a string or byte read.</summary>
        public const int MaxLen = 256;

        /// <summary>How many rows the list will hold. Every refresh carries
        /// EVERY row, and every row is a live control, so this is a real limit
        /// rather than a formality. It is stated when it bites; a map larger
        /// than this belongs in the results folder, not on a 5 Hz wire.</summary>
        public const int MaxRows = 64;

        /// <summary>Refreshes per second, the protocol's watch-rate.</summary>
        public const int DefaultHz = 5;
        public const int MinHz = 1;
        public const int MaxHz = 20;

        // The twelve the protocol names, commonest first: this order is what the
        // kind picker shows.
        private static readonly string[] KindList =
        {
            "int32", "uint32", "float32", "float64",
            "int16", "uint16", "int8", "uint8", "int64",
            "utf16", "utf8", "bytes",
        };

        public static IReadOnlyList<string> Kinds => KindList;

        /// <summary>True for the kinds that take a length: the two strings and
        /// the raw bytes. Everything else is fixed width and a length sent with
        /// it would be noise.</summary>
        public static bool IsSizedKind(string kind)
        {
            switch (kind)
            {
                case "utf8":
                case "utf16":
                case "bytes": return true;
                default: return false;
            }
        }

        /// <summary>The canonical kind name, or null when it is not one of the
        /// twelve. Generous about spelling because these names arrive from a
        /// finding's type column as well as from the picker.</summary>
        public static string NormalizeKind(string kind)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");
            switch (k)
            {
                case "i8": case "int8": case "sbyte": return "int8";
                case "u8": case "uint8": case "byte": return "uint8";
                case "i16": case "int16": case "short": return "int16";
                case "u16": case "uint16": case "ushort": case "word": return "uint16";
                case "i32": case "int32": case "int": case "long32": return "int32";
                case "u32": case "uint32": case "uint": case "dword": return "uint32";
                case "i64": case "int64": case "long": case "qword": return "int64";
                case "f32": case "float32": case "float": case "single": return "float32";
                case "f64": case "float64": case "double": return "float64";
                case "utf8": case "utf8string": case "ascii": case "ansi": return "utf8";
                case "utf16": case "utf16le": case "unicode": case "wide": case "wchar": return "utf16";
                case "bytes": case "byte[]": case "raw": case "hex": return "bytes";
                default: return null;
            }
        }

        /// <summary>The kind to watch a finding with, from the type column the
        /// scanner printed beside it. Falls back to int32 rather than refusing:
        /// a row watched with the wrong kind still moves when the value moves,
        /// which is most of what the operator is looking for, and the kind can
        /// be corrected by adding it again by hand.
        ///
        /// Shift-JIS is the one encoding the protocol has no kind for, and it is
        /// a real possibility on a Japanese arcade title, so it is watched as
        /// bytes: unreadable as text, but it visibly changes when the car does,
        /// which is the question being asked.</summary>
        public static string KindForFindingType(string type)
        {
            string t = (type ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return "int32";
            string exact = NormalizeKind(t);
            if (exact != null) return exact;
            if (t.IndexOf("sjis", StringComparison.Ordinal) >= 0
                || t.IndexOf("shift", StringComparison.Ordinal) >= 0) return "bytes";
            if (t.IndexOf("utf16", StringComparison.Ordinal) >= 0
                || t.IndexOf("utf-16", StringComparison.Ordinal) >= 0
                || t.IndexOf("unicode", StringComparison.Ordinal) >= 0
                || t.IndexOf("wide", StringComparison.Ordinal) >= 0) return "utf16";
            if (t.IndexOf("utf8", StringComparison.Ordinal) >= 0
                || t.IndexOf("utf-8", StringComparison.Ordinal) >= 0
                || t.IndexOf("ascii", StringComparison.Ordinal) >= 0) return "utf8";
            // A string finding whose encoding was not spelled out. UTF-16 is what
            // a Windows game usually keeps display text in, and it is the one
            // guess here with a reason behind it.
            if (t.IndexOf("string", StringComparison.Ordinal) >= 0
                || t.IndexOf("text", StringComparison.Ordinal) >= 0
                || t.IndexOf("name", StringComparison.Ordinal) >= 0) return "utf16";
            if (t.IndexOf("double", StringComparison.Ordinal) >= 0) return "float64";
            if (t.IndexOf("float", StringComparison.Ordinal) >= 0) return "float32";
            if (t.IndexOf("ptr", StringComparison.Ordinal) >= 0
                || t.IndexOf("pointer", StringComparison.Ordinal) >= 0) return "uint32";
            return "int32";
        }

        /// <summary>How much of a string to read for a row made from a finding.
        /// Long enough to show a DIFFERENT name as well as the one that was
        /// searched for: a window exactly the length of the search term would
        /// cut off the answer that proves the point, which is the whole reason
        /// the row exists.
        ///
        /// The scanner sends a length of its own and it is deliberately NOT used
        /// as written: it is the length of the text that was FOUND, so the
        /// longer name the operator is about to switch to would arrive cut in
        /// half. It is used as a FLOOR.</summary>
        public static int LenForFinding(string kind, int scannerLen, string searchedFor)
        {
            if (!IsSizedKind(kind)) return 0;
            int len = string.IsNullOrEmpty(searchedFor) ? DefaultLen : searchedFor.Length + 12;
            if (scannerLen > 0 && scannerLen + 12 > len) len = scannerLen + 12;
            if (len < 16) len = 16;
            if (len > MaxLen) len = MaxLen;
            return len;
        }

        /// <summary>The address, kind, length and name to watch one finding
        /// with, so pressing Watch this needs nothing retyped.
        ///
        /// Two rules, and both change the answer:
        ///
        /// The scanner's own watchKind wins over anything derived from the Type
        /// column, because it knows which encoding actually matched and this
        /// side is reading a description of it.
        ///
        /// A finding that carries a module+offset form is stored in THAT form.
        /// A heap address is worth one launch; module+offset is a map entry that
        /// resolves again tomorrow, and that difference is what makes this a map
        /// rather than a list of numbers.</summary>
        public static void SpecForFinding(string address, string moduleAddr, string type,
                                          string watchKind, int watchLen, string findingLabel,
                                          string searchedFor,
                                          out string addr, out string kind, out int len, out string label)
        {
            string exact = NormalizeKind(watchKind);
            kind = exact ?? KindForFindingType(type);
            addr = string.IsNullOrWhiteSpace(moduleAddr) ? address : moduleAddr;
            len = LenForFinding(kind, watchLen, searchedFor);
            label = CleanLabel(findingLabel);
            if (label.Length == 0)
            {
                // The name that was searched for is the obvious name for the
                // row: once the car is changed, a value that no longer matches
                // its own label is the finding, visible at a glance.
                label = string.IsNullOrEmpty(searchedFor) ? "unnamed" : CleanLabel(searchedFor);
            }
        }

        /// <summary>The address in the one form this list stores and sends, or
        /// null with a reason. Three shapes are accepted, and the last two are
        /// what make a row a map entry rather than a note:
        ///
        ///   0x01C02750                 an absolute address, this launch only
        ///   game.exe+0x4A2C10          module plus offset, good on every launch
        ///   [game.exe+0x4A2C10]+0x18   a pointer path, re-walked on every read
        ///
        /// The path is what a HEAP address needs before it is worth writing
        /// down: the values this target actually gives up are not in the image,
        /// so a bare address is good for one launch and a path from a root that
        /// cannot move is good for every launch. It is written in the notation
        /// every memory tool uses, so one can be pasted in from elsewhere.
        ///
        /// A bare hex number is read as an absolute address and a bare offset
        /// after a module name as hex, because everything either half of this
        /// prints is hex and a decimal reading would silently land somewhere
        /// else entirely.</summary>
        public static string NormalizeAddress(string raw, out string problem)
        {
            problem = null;
            string s = (raw ?? "").Trim();
            if (s.Length == 0)
            {
                problem = "Type an address first, like 0x01C02750 or game.exe+0x4A2C10.";
                return null;
            }

            if (s[0] == '[')
            {
                var path = MemoryPathSpec.TryParse(s, out string pathProblem);
                if (path == null)
                {
                    problem = "That is not a pointer path (" + pathProblem + "). One looks like "
                            + "[game.exe+0x4A2C10]+0x18.";
                    return null;
                }
                return path.Format();
            }

            int plus = s.IndexOf('+');
            if (plus >= 0)
            {
                string module = s.Substring(0, plus).Trim();
                string offset = s.Substring(plus + 1).Trim();
                if (module.Length == 0)
                {
                    problem = "There is nothing before the +. A module-relative address looks like game.exe+0x4A2C10.";
                    return null;
                }
                if (module.IndexOf('"') >= 0 || module.IndexOf('\\') >= 0 || module.IndexOf('/') >= 0)
                {
                    problem = "Use the module's name on its own, not a path: game.exe+0x4A2C10.";
                    return null;
                }
                if (!TryHex(offset, out ulong off, out string offProblem))
                {
                    problem = "The offset after the + is not a hex number (" + offProblem + "). "
                            + "It looks like game.exe+0x4A2C10.";
                    return null;
                }
                return module + "+0x" + off.ToString("X", CultureInfo.InvariantCulture);
            }

            if (!TryHex(s, out ulong abs, out string absProblem))
            {
                problem = "That is not an address (" + absProblem + "). It is either a hex address like "
                        + "0x01C02750, or a module and an offset like game.exe+0x4A2C10.";
                return null;
            }
            // Padded to eight digits, which is what the scanner prints, so the
            // same address typed by hand and pasted from a finding look the same
            // in the list rather than like two rows.
            return "0x" + abs.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static bool TryHex(string s, out ulong value, out string problem)
        {
            value = 0;
            problem = null;
            string h = (s ?? "").Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            if (h.Length == 0) { problem = "there are no digits in it"; return false; }
            foreach (char c in h)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) { problem = "\"" + c + "\" is not a hex digit"; return false; }
            }
            // Leading zeros are written by everything that prints an address, so
            // they are dropped before the length is judged rather than counted
            // against it.
            string digits = h.TrimStart('0');
            if (digits.Length == 0) { value = 0; return true; }
            if (digits.Length > 16) { problem = "it is too long to be an address"; return false; }
            return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The length to send with a kind, clamped. Zero for the fixed
        /// width kinds, which never carry one.</summary>
        public static int NormalizeLen(string kind, int len)
        {
            if (!IsSizedKind(kind)) return 0;
            if (len <= 0) return DefaultLen;
            return len > MaxLen ? MaxLen : len;
        }

        // ---- the list --------------------------------------------------------

        private readonly List<MemoryWatchRow> _rows = new List<MemoryWatchRow>();
        private int _hz = DefaultHz;

        public IReadOnlyList<MemoryWatchRow> Rows => _rows;
        public int Count => _rows.Count;

        /// <summary>Refreshes per second, clamped to what the protocol allows.</summary>
        public int Hz
        {
            get { return _hz; }
            set { _hz = value < MinHz ? MinHz : value > MaxHz ? MaxHz : value; }
        }

        /// <summary>The row for an address, matched the way the scanner echoes
        /// it back. Case-insensitive because a module name and a hex digit can
        /// both come back in either case, and losing a row over a capital letter
        /// would look exactly like a value that stopped updating.</summary>
        public MemoryWatchRow Find(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return null;
            string a = addr.Trim();
            foreach (var r in _rows)
                if (string.Equals(r.Addr, a, StringComparison.OrdinalIgnoreCase)) return r;
            // A last try through the normaliser, so a hand-typed 0x1c02750 finds
            // the row that was stored as 0x01C02750.
            string norm = NormalizeAddress(a, out _);
            if (norm == null) return null;
            foreach (var r in _rows)
                if (string.Equals(r.Addr, norm, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>Add a row, or update the one already on that address.
        /// Returns null when the row is in the list, or the reason it is not.
        /// "added" says which of the two happened, because re-watching an
        /// address is a correction rather than a mistake and should not read
        /// like one.</summary>
        public string Add(string addr, string kind, int len, string label,
                          out MemoryWatchRow row, out bool added)
        {
            row = null;
            added = false;

            string a = NormalizeAddress(addr, out string problem);
            if (a == null) return problem;

            string k = NormalizeKind(kind);
            if (k == null)
                return "\"" + (kind ?? "") + "\" is not one of the kinds this can read. "
                     + "Pick one from the list.";

            var existing = Find(a);
            if (existing != null)
            {
                existing.Kind = k;
                existing.Len = NormalizeLen(k, len);
                existing.Label = CleanLabel(label);
                row = existing;
                return null;
            }

            if (_rows.Count >= MaxRows)
                return "The watch list already holds " + MaxRows.ToString(CultureInfo.InvariantCulture)
                     + " rows, which is as many as one refresh can carry. Remove one first.";

            row = new MemoryWatchRow
            {
                Addr = a,
                Kind = k,
                Len = NormalizeLen(k, len),
                Label = CleanLabel(label),
            };
            _rows.Add(row);
            added = true;
            return null;
        }

        /// <summary>Fold one refresh into the list: match every row the scanner
        /// reported against the row that asked for it, work out what to show and
        /// whether it moved, and count anything the scanner is watching that
        /// this list does not hold.
        ///
        /// Here rather than in the tab because this is the judgement the feature
        /// rests on. "Did the value follow when I changed the car" is answered
        /// by one string comparison, and a tab that owns it is a tab where that
        /// comparison can only be tested by pointing a camera at a screen. The
        /// caller paints; nothing here knows what a colour is.</summary>
        public IReadOnlyList<MemoryWatchUpdate> Apply(IEnumerable<ScanWatchRow> reported, out int strangers)
        {
            strangers = 0;
            var outp = new List<MemoryWatchUpdate>();
            if (reported == null) return outp;
            foreach (var r in reported)
            {
                if (r == null || string.IsNullOrEmpty(r.Addr)) continue;
                var row = Find(r.Addr);
                if (row == null)
                {
                    // The scanner is watching something this list does not have.
                    // Counted rather than adopted: the list is the map, and a
                    // row that appeared from nowhere has no name and no reason
                    // to be trusted.
                    strangers++;
                    continue;
                }
                string shown = r.Ok
                    ? (r.Value ?? "")
                    : "cannot read: " + (string.IsNullOrEmpty(r.Why) ? "no reason given" : r.Why);
                bool changed = row.HasRead && !string.Equals(row.LastShown, shown, StringComparison.Ordinal);
                row.LastShown = shown;
                row.HasRead = true;
                if (changed) row.Changes++;
                outp.Add(new MemoryWatchUpdate
                {
                    Row = row,
                    Shown = shown,
                    Changed = changed,
                    Ok = r.Ok,
                    Resolved = r.Resolved,
                });
            }
            return outp;
        }

        /// <summary>Forget what every row was reading, without touching the map
        /// itself. Called when a new scanner starts: the first refresh of a new
        /// run is a row arriving, not a value that moved, and a count carried
        /// over from the last run would say the car changed while nobody was
        /// looking.</summary>
        public void ForgetReadings()
        {
            foreach (var r in _rows) { r.LastShown = null; r.HasRead = false; r.Changes = 0; }
        }

        public bool Remove(string addr)
        {
            var r = Find(addr);
            if (r == null) return false;
            _rows.Remove(r);
            return true;
        }

        public void Clear() => _rows.Clear();

        /// <summary>Rename a row. True when something actually changed, so a
        /// caller can skip the save and the re-send on a no-op keystroke.</summary>
        public bool Relabel(string addr, string label)
        {
            var r = Find(addr);
            if (r == null) return false;
            string clean = CleanLabel(label);
            if (string.Equals(r.Label ?? "", clean ?? "", StringComparison.Ordinal)) return false;
            r.Label = clean;
            return true;
        }

        /// <summary>One line, no control characters, and short enough to sit in
        /// a column. A label is a name, not a note.</summary>
        public static string CleanLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var sb = new StringBuilder(label.Length);
            foreach (char c in label)
                sb.Append(c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
            string s = sb.ToString().Trim();
            if (s.Length > 64) s = s.Substring(0, 64).TrimEnd();
            return s;
        }

        // ---- the file --------------------------------------------------------

        private sealed class FileShape
        {
            [JsonProperty("hz")]   public int Hz { get; set; }
            [JsonProperty("rows")] public List<MemoryWatchRow> Rows { get; set; }
        }

        /// <summary>Write the list. Returns null, or a reason that is worth
        /// showing: a map that silently failed to save is worse than no map.
        /// Temp file plus replace, the same shape every other store here uses,
        /// so a crash mid-write cannot leave half a list behind.</summary>
        public string Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return "no path";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(
                    new FileShape { Hz = _hz, Rows = new List<MemoryWatchRow>(_rows) },
                    Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Read the list, replacing whatever is held. A missing file is
        /// not an error: it is the ordinary state before the first row exists.
        /// A malformed one leaves the list empty rather than throwing, and says
        /// so, because this file is on disk between launches and a hand edit is
        /// exactly the kind of thing that happens to it.</summary>
        public string Load(string path)
        {
            _rows.Clear();
            _hz = DefaultHz;
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var shape = JsonConvert.DeserializeObject<FileShape>(json, SafeJson.Settings);
                if (shape == null) return "the watch list file could not be read";
                Hz = shape.Hz > 0 ? shape.Hz : DefaultHz;
                if (shape.Rows == null) return null;
                foreach (var r in shape.Rows)
                {
                    if (r == null) continue;
                    Add(r.Addr, r.Kind, r.Len, r.Label, out _, out _);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
