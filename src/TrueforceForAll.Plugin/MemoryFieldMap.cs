// The field frame and the map file: what the Memory Map tab is FOR, rather
// than what a single scan happened to return.
//
// The problem this replaces. A search hands back a flat list of findings, and
// two real name searches on the target returned 126 and 154 of them. That is
// not something to read, it is something to filter, and clicking through a
// hundred rows one at a time is not filtering. Worse, ranking a list like that
// teaches the operator to trust the order: this tool has already put an audio
// filter cutoff above a tachometer with high confidence, so a dropdown sorted
// "most likely first" is an invitation to believe the wrong row.
//
// So the main view is a list of FIELDS, which is the map: car name, redline,
// gear, revs, speed, lap time, time remaining, plus anything the operator adds.
// Each field is in one of three states: nothing yet, N candidates, or confirmed
// at an address with a name. Findings are candidates FOR a field.
//
// Elimination is BULK. The operator declares what they are about to do ("I am
// about to change car"), does it, and every candidate that did not respond the
// way that field must respond is dropped in one go. One action, one round.
// Twice gets a hundred candidates down to a handful, which a human then
// confirms by eye.
//
// Two rules the code below never breaks, because both of them decide whether
// this tool can be trusted at all:
//
//   A candidate is eliminated on EVIDENCE, never on silence. A candidate the
//   scanner did not report during a round is kept, and counted separately, so a
//   watch list that quietly dropped half the addresses cannot delete the answer.
//
//   A map entry is never assumed good. On load every entry is re-resolved,
//   read and sanity checked, and one that fails is marked STALE rather than fed
//   onward. A map that quietly reads garbage after a game update is worse than
//   no map.
//
// Deliberately free of WPF, of SimHub and of the plugin's own path helpers, for
// the same reason MemoryWatchList is: the caller passes the file path in, and
// this file is link-compiled into TrueforceForAll.Core.Tests where the
// elimination arithmetic and the sanity checks can be pinned without a game, a
// scanner or a screen.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    // ---- what an elimination round asks of a candidate ----------------------

    /// <summary>What a field's value MUST do while the declared action is being
    /// performed. This is the whole of the elimination logic: everything else is
    /// bookkeeping around one of these two words.</summary>
    public enum MemoryFieldExpect
    {
        /// <summary>It has to move. A per-car value that sits still through a
        /// car change is not that value.</summary>
        Change,
        /// <summary>It has to sit still. A value that wanders while the car is
        /// parked and untouched is noise, whatever else it did.</summary>
        Hold,
    }

    /// <summary>One thing the operator can declare they are about to do, and
    /// what it proves. The prompt is written in the first person and in the
    /// future tense on purpose: it is pressed BEFORE the action, and a label
    /// reading "changed car" would be pressed after it, with nothing recorded
    /// for the change itself.</summary>
    public sealed class MemoryEliminationAction
    {
        public readonly string Id;
        public readonly string Prompt;
        public readonly MemoryFieldExpect Expect;
        /// <summary>Why this eliminates anything, in one line, shown beside the
        /// button. An operator who does not know what a round proves cannot tell
        /// a good round from a wasted one.</summary>
        public readonly string Proves;

        public MemoryEliminationAction(string id, string prompt, MemoryFieldExpect expect, string proves)
        {
            Id = id; Prompt = prompt; Expect = expect; Proves = proves;
        }
    }

    /// <summary>The declared actions, by id. Shared across fields: changing car
    /// proves something about the car name, the redline and the gear ratios
    /// alike, and writing it three times would let the three drift apart.</summary>
    public static class MemoryEliminationActions
    {
        public static readonly MemoryEliminationAction ChangeCar = new MemoryEliminationAction(
            "change-car", "I am about to change car", MemoryFieldExpect.Change,
            "Everything the game stores per car has to follow the car. Anything that sits still through a car change is not it.");

        public static readonly MemoryEliminationAction SitStill = new MemoryEliminationAction(
            "sit-still", "I am about to sit still and touch nothing", MemoryFieldExpect.Hold,
            "A parked car with nothing touching it has nothing to report. Anything still moving is noise.");

        public static readonly MemoryEliminationAction Shift = new MemoryEliminationAction(
            "shift", "I am about to change gear", MemoryFieldExpect.Change,
            "The gear number changes on a shift. So do the revs, which is what separates the crankshaft from everything that merely follows the pedal.");

        public static readonly MemoryEliminationAction HoldGear = new MemoryEliminationAction(
            "hold-gear", "I am about to drive without changing gear", MemoryFieldExpect.Hold,
            "The gear number holds while the car is moving, which almost nothing else does.");

        public static readonly MemoryEliminationAction RevHard = new MemoryEliminationAction(
            "rev-hard", "I am about to rev hard", MemoryFieldExpect.Change,
            "Engine speed moves a long way when the throttle is pinned. So do several pedal followers, so this narrows rather than answers.");

        public static readonly MemoryEliminationAction DriveOff = new MemoryEliminationAction(
            "drive-off", "I am about to drive off from a standstill", MemoryFieldExpect.Change,
            "Road speed leaves zero and stays away from it.");

        public static readonly MemoryEliminationAction ClockRuns = new MemoryEliminationAction(
            "clock-runs", "I am about to let the clock run", MemoryFieldExpect.Change,
            "A timer moves on its own with nobody touching anything. Most of memory does not.");

        public static readonly MemoryEliminationAction Paused = new MemoryEliminationAction(
            "paused", "I am about to pause, or sit at a menu", MemoryFieldExpect.Hold,
            "A timer stops when the game is paused. Anything still counting is a different clock.");

        public static readonly MemoryEliminationAction ItChanges = new MemoryEliminationAction(
            "it-changes", "I am about to change it", MemoryFieldExpect.Change,
            "Whatever this field is, it has to follow the thing you are about to change.");

        public static readonly MemoryEliminationAction ItHolds = new MemoryEliminationAction(
            "it-holds", "I am about to leave it alone", MemoryFieldExpect.Hold,
            "Whatever this field is, it has to sit still while the thing it tracks is sitting still.");

        private static readonly MemoryEliminationAction[] AllList =
        {
            ChangeCar, SitStill, Shift, HoldGear, RevHard, DriveOff, ClockRuns, Paused, ItChanges, ItHolds,
        };

        public static IReadOnlyList<MemoryEliminationAction> All => AllList;

        public static MemoryEliminationAction ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var a in AllList)
                if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }

    // ---- is this value even plausible ---------------------------------------

    /// <summary>How to tell a plausible reading of a field from garbage. The
    /// point is not to grade the value, it is to catch the two ways a map goes
    /// wrong after a game update: the address now holds something that is not
    /// this field at all, or it resolves into a region that reads as noise.
    ///
    /// The numeric bands are deliberately WIDE. We do not know the units the
    /// target stores a field in (revs could be rpm or radians per second, a lap
    /// time could be seconds, milliseconds, hundredths or frames), and a band
    /// tight enough to police the units would reject the right answer on a
    /// technicality. Wide still catches NaN, a pointer read as a float, and a
    /// freed page read as a number, which is what actually happens.</summary>
    public sealed class MemorySanity
    {
        public enum Shape { None, Text, Number }

        public readonly Shape Kind;
        public readonly double Min;
        public readonly double Max;
        /// <summary>Printable characters a text field needs before it counts as
        /// a name rather than as a page of nulls.</summary>
        public readonly int MinChars;

        private MemorySanity(Shape kind, double min, double max, int minChars)
        { Kind = kind; Min = min; Max = max; MinChars = minChars; }

        public static MemorySanity None() => new MemorySanity(Shape.None, 0, 0, 0);
        public static MemorySanity Text(int minChars) => new MemorySanity(Shape.Text, 0, 0, minChars);
        public static MemorySanity Number(double min, double max) => new MemorySanity(Shape.Number, min, max, 0);

        /// <summary>Judge one decoded value, as the scanner printed it. Returns
        /// null when it is plausible, or the reason it is not, in words worth
        /// showing.</summary>
        public string Check(string shown)
        {
            if (shown == null) return "there was nothing to read";
            string s = shown.Trim();
            if (s.Length == 0) return "it read back empty";
            // The scanner prints a failed read as a sentence rather than a
            // value, and the caller passes what it was given, so this is where
            // that has to be caught rather than parsed as text.
            if (s.StartsWith("cannot read", StringComparison.OrdinalIgnoreCase))
                return "it could not be read";

            switch (Kind)
            {
                case Shape.Text:
                {
                    string t = Unquote(s);
                    int printable = 0;
                    foreach (char c in t)
                    {
                        if (char.IsControl(c)) continue;
                        if (c == ' ') continue;
                        if (c == '�') continue;   // a decode that gave up
                        printable++;
                    }
                    if (printable < MinChars)
                        return "it does not read as text any more (" + printable.ToString(CultureInfo.InvariantCulture)
                             + " printable character(s))";
                    return null;
                }
                case Shape.Number:
                {
                    if (!TryNumber(s, out double v))
                        return "\"" + Shorten(s) + "\" is not a number";
                    if (double.IsNaN(v) || double.IsInfinity(v))
                        return "it reads as " + s.Trim();
                    if (v < Min || v > Max)
                        return v.ToString("0.###", CultureInfo.InvariantCulture)
                             + " is outside the range this field can hold ("
                             + Min.ToString("0.###", CultureInfo.InvariantCulture) + " to "
                             + Max.ToString("0.###", CultureInfo.InvariantCulture) + ")";
                    return null;
                }
                default:
                    return null;
            }
        }

        /// <summary>Read a number the way the scanner prints one. Generous about
        /// the wrappers a decoded value picks up on the way here, and strict
        /// about the number itself: invariant culture, because a machine that
        /// writes 7,400 for seven thousand four hundred would otherwise read a
        /// redline as 7.4 and call the map stale.</summary>
        public static bool TryNumber(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            // "nan" and "inf" parse under NumberStyles.Float on some runtimes
            // and not others, so they are named here rather than left to luck.
            string low = t.ToLowerInvariant();
            if (low == "nan" || low.EndsWith("nan", StringComparison.Ordinal))
            { value = double.NaN; return true; }
            if (low.IndexOf("infinity", StringComparison.Ordinal) >= 0 || low == "inf" || low == "-inf")
            { value = low.StartsWith("-", StringComparison.Ordinal) ? double.NegativeInfinity : double.PositiveInfinity; return true; }
            // Thousands separators are accepted because several printers add
            // them; a bare comma decimal point is not, and would fail the parse
            // rather than land somewhere a thousand times off.
            return double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands,
                                   CultureInfo.InvariantCulture, out value);
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static string Shorten(string s)
            => s.Length <= 24 ? s : s.Substring(0, 24) + "...";
    }

    // ---- the catalog --------------------------------------------------------

    /// <summary>One field of the map: what it is called, what it should read
    /// like, and which declared actions can eliminate candidates for it.</summary>
    public sealed class MemoryFieldDef
    {
        public readonly string Key;
        public readonly string Name;
        /// <summary>The watch kind to try first for a candidate that arrived
        /// without one. A hint, not a rule: a finding's own kind always wins.</summary>
        public readonly string Kind;
        public readonly MemorySanity Sanity;
        public readonly string What;
        private readonly MemoryEliminationAction[] _actions;

        public MemoryFieldDef(string key, string name, string kind, MemorySanity sanity, string what,
                              params MemoryEliminationAction[] actions)
        {
            Key = key; Name = name; Kind = kind; Sanity = sanity; What = what;
            _actions = actions ?? new MemoryEliminationAction[0];
        }

        /// <summary>The actions offered for this field, its own first and the
        /// two generic ones last, so the specific one is what the operator
        /// reaches for.</summary>
        public IReadOnlyList<MemoryEliminationAction> Actions
        {
            get
            {
                var list = new List<MemoryEliminationAction>(_actions);
                foreach (var g in new[] { MemoryEliminationActions.ItChanges, MemoryEliminationActions.ItHolds })
                {
                    bool have = false;
                    foreach (var a in list) if (ReferenceEquals(a, g)) { have = true; break; }
                    if (!have) list.Add(g);
                }
                return list;
            }
        }
    }

    /// <summary>The fields this map is for, in the owner's own priority order:
    /// the things that unlock effects and the things that go on the wheel's
    /// screen come first, and they happen to be the cheapest to find as well.</summary>
    public static class MemoryFields
    {
        public const string CarName  = "car-name";
        public const string Redline  = "redline";
        public const string Gear     = "gear";
        public const string Rpm      = "rpm";
        public const string Speed    = "speed";
        public const string LapTime  = "lap-time";
        public const string TimeLeft = "time-left";

        private static readonly MemoryFieldDef[] BuiltInList =
        {
            new MemoryFieldDef(Rpm, "Revs", "float32", MemorySanity.Number(0, 30000),
                "Engine speed. Drives the rev lights, the engine effects and the rev limiter.",
                MemoryEliminationActions.Shift, MemoryEliminationActions.RevHard, MemoryEliminationActions.SitStill),

            new MemoryFieldDef(LapTime, "Lap time", "float32", MemorySanity.Number(0, 10000000),
                "The running lap or session time. Goes on the wheel's screen, and it costs no driving to find: it moves on its own.",
                MemoryEliminationActions.ClockRuns, MemoryEliminationActions.Paused),

            new MemoryFieldDef(TimeLeft, "Time remaining", "float32", MemorySanity.Number(0, 10000000),
                "The countdown an arcade cabinet runs on. Goes on the wheel's screen.",
                MemoryEliminationActions.ClockRuns, MemoryEliminationActions.Paused),

            new MemoryFieldDef(CarName, "Car name", "utf16", MemorySanity.Text(2),
                "The car as the game displays it. Finding it is the doorway to everything else the game stores per car.",
                MemoryEliminationActions.ChangeCar, MemoryEliminationActions.SitStill),

            new MemoryFieldDef(Redline, "Redline", "int32", MemorySanity.Number(50, 40000),
                "Where the rev lights should go red. Per car, so changing car is what confirms it.",
                MemoryEliminationActions.ChangeCar, MemoryEliminationActions.SitStill),

            new MemoryFieldDef(Gear, "Gear", "int32", MemorySanity.Number(-2, 12),
                "The gear the car is in. Shift effects, and the ratio relation that helps confirm revs and speed.",
                MemoryEliminationActions.Shift, MemoryEliminationActions.HoldGear, MemoryEliminationActions.SitStill),

            new MemoryFieldDef(Speed, "Speed", "float32", MemorySanity.Number(0, 2000),
                "Road speed. Speed-scaled effects, and it falls out of the revs and the gear once those are known.",
                MemoryEliminationActions.DriveOff, MemoryEliminationActions.SitStill),
        };

        public static IReadOnlyList<MemoryFieldDef> BuiltIn => BuiltInList;

        public static MemoryFieldDef ByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var f in BuiltInList)
                if (string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>The definition for a field the operator added, which has no
        /// entry in the catalog. It gets no sanity band, because we do not know
        /// what it holds, and both generic actions.</summary>
        public static MemoryFieldDef Custom(string key, string name)
            => new MemoryFieldDef(key, string.IsNullOrEmpty(name) ? key : name, "int32",
                                  MemorySanity.None(), "A field you added.");

        /// <summary>A key from a typed name. Lower case, spaces to hyphens, and
        /// nothing that would make a bad file name, because a custom key ends up
        /// in the map file beside the built-in ones.</summary>
        public static string KeyFromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') { if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-'); }
            }
            string k = sb.ToString().Trim('-');
            if (k.Length == 0) return null;
            if (k.Length > 32) k = k.Substring(0, 32).Trim('-');
            return k;
        }
    }

    // ---- pointer paths ------------------------------------------------------

    /// <summary>A route to an address through pointers, which is what makes a
    /// HEAP address a map entry instead of a number good for one launch.
    ///
    /// Written and read in the notation every memory tool uses, so an operator
    /// can paste one in from elsewhere and read one out to somewhere else:
    ///
    ///     [game.exe+0x4A2C10]+0x18                 one dereference
    ///     [[game.exe+0x4A2C10]+0x18]+0x4           two
    ///
    /// Resolution is left to right, starting at the root:
    ///
    ///     addr = root
    ///     for each offset:  addr = read4(addr) + offset
    ///
    /// so the offset count IS the number of dereferences, and the last offset is
    /// added without a read. The scanner does the resolving; this side only has
    /// to write the path down in a form that survives a save and a paste.</summary>
    public sealed class MemoryPathSpec
    {
        /// <summary>Where the walk starts. A root inside the game's own
        /// executable is the strong case (that image cannot be moved by
        /// Windows), a root in a DLL still has to be resolved at read time, and
        /// an absolute root is worth one launch.</summary>
        public string Root;

        /// <summary>The offsets, in walk order.</summary>
        public List<long> Offsets = new List<long>();

        public int Depth => Offsets == null ? 0 : Offsets.Count;

        /// <summary>The path as one string, which is the form everything else
        /// here stores, sends and compares.</summary>
        public string Format()
        {
            if (string.IsNullOrWhiteSpace(Root)) return null;
            if (Offsets == null || Offsets.Count == 0) return Root;
            var sb = new StringBuilder();
            for (int i = 0; i < Offsets.Count; i++) sb.Append('[');
            sb.Append(Root);
            for (int i = 0; i < Offsets.Count; i++)
            {
                sb.Append(']');
                long o = Offsets[i];
                sb.Append(o < 0 ? "-0x" : "+0x");
                sb.Append(Math.Abs(o).ToString("X", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Read a path back. Returns null with a reason when the text
        /// is not one, so a hand-typed path fails with an explanation rather
        /// than becoming a row that reads nothing forever.</summary>
        public static MemoryPathSpec TryParse(string text, out string problem)
        {
            problem = null;
            string s = (text ?? "").Trim();
            if (s.Length == 0) { problem = "there is nothing there"; return null; }
            if (s[0] != '[')
            {
                problem = "a pointer path starts with a [, like [game.exe+0x4A2C10]+0x18";
                return null;
            }

            int opens = 0;
            int i = 0;
            while (i < s.Length && s[i] == '[') { opens++; i++; }
            if (opens > 12) { problem = "that is more levels than a pointer path is ever worth"; return null; }

            int rootStart = i;
            int close = s.IndexOf(']', i);
            if (close < 0) { problem = "the brackets are not closed"; return null; }
            string root = s.Substring(rootStart, close - rootStart).Trim();
            if (root.Length == 0) { problem = "there is nothing inside the brackets"; return null; }

            var offsets = new List<long>();
            i = close;
            while (i < s.Length)
            {
                if (s[i] != ']') { problem = "\"" + s.Substring(i) + "\" is not part of a pointer path"; return null; }
                i++;
                // Everything up to the next ] is one signed hex offset.
                int next = s.IndexOf(']', i);
                string chunk = (next < 0 ? s.Substring(i) : s.Substring(i, next - i)).Trim();
                if (chunk.Length == 0)
                {
                    problem = "one of the levels has no offset after it, so the path stops in the middle";
                    return null;
                }
                if (!TryOffset(chunk, out long off, out string why)) { problem = why; return null; }
                offsets.Add(off);
                if (next < 0) { i = s.Length; break; }
                i = next;
            }

            if (offsets.Count != opens)
            {
                problem = "the path has " + opens.ToString(CultureInfo.InvariantCulture)
                        + " level(s) but " + offsets.Count.ToString(CultureInfo.InvariantCulture)
                        + " offset(s); every level needs one";
                return null;
            }

            // The root is an address in its own right, so it goes through the
            // same parser every other address here does. That is what stops
            // "[gameexe0x4A2C10]+0x18" from becoming a row nothing can resolve.
            string normRoot = MemoryWatchList.NormalizeAddress(root, out string rootProblem);
            if (normRoot == null) { problem = "the root of the path is not an address (" + rootProblem + ")"; return null; }

            return new MemoryPathSpec { Root = normRoot, Offsets = offsets };
        }

        private static bool TryOffset(string chunk, out long value, out string problem)
        {
            value = 0; problem = null;
            string c = chunk.Trim();
            bool neg = false;
            if (c.StartsWith("+", StringComparison.Ordinal)) c = c.Substring(1).Trim();
            else if (c.StartsWith("-", StringComparison.Ordinal)) { neg = true; c = c.Substring(1).Trim(); }
            else { problem = "\"" + chunk + "\" needs a + or a - in front of it"; return false; }
            if (c.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) c = c.Substring(2);
            if (c.Length == 0) { problem = "an offset in the path has no digits"; return false; }
            if (c.Length > 8) { problem = "\"" + chunk + "\" is too large to be an offset"; return false; }
            foreach (char ch in c)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!hex) { problem = "\"" + ch + "\" is not a hex digit, in \"" + chunk + "\""; return false; }
            }
            if (!long.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long raw))
            { problem = "\"" + chunk + "\" is not a hex number"; return false; }
            value = neg ? -raw : raw;
            return true;
        }
    }

    /// <summary>Which of the three ways an entry is reached. The order is the
    /// order of preference, and it is a statement about durability rather than
    /// about confidence: a path re-resolves from a fixed root every read, a
    /// module offset re-resolves once per launch, and an absolute address is
    /// worth exactly one launch of one process.</summary>
    public enum MemoryRoute { Absolute = 0, Module = 1, Path = 2 }

    public static class MemoryRoutes
    {
        /// <summary>Work out which route an address string is, from its shape.
        /// The three forms cannot be confused: a path starts with a bracket, a
        /// module offset has a name before the +, and everything else is a bare
        /// address.</summary>
        public static MemoryRoute Of(string addr)
        {
            string s = (addr ?? "").Trim();
            if (s.Length == 0) return MemoryRoute.Absolute;
            if (s[0] == '[') return MemoryRoute.Path;
            int plus = s.IndexOf('+');
            if (plus > 0) return MemoryRoute.Module;
            return MemoryRoute.Absolute;
        }

        /// <summary>How durable that route is, said in one line, which is what
        /// the map has to show beside every entry.</summary>
        public static string Durability(MemoryRoute route)
        {
            switch (route)
            {
                case MemoryRoute.Path:
                    return "resolved from a fixed root on every read, so it survives a relaunch";
                case MemoryRoute.Module:
                    return "module plus offset, resolved once per launch, so it survives a relaunch";
                default:
                    return "an absolute address, good for this launch of this process only";
            }
        }

        public static string Word(MemoryRoute route)
        {
            switch (route)
            {
                case MemoryRoute.Path:   return "path";
                case MemoryRoute.Module: return "module";
                default:                 return "absolute";
            }
        }

        public static MemoryRoute FromWord(string word)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "path":   return MemoryRoute.Path;
                case "module": return MemoryRoute.Module;
                default:       return MemoryRoute.Absolute;
            }
        }

        /// <summary>One address, in the one spelling both halves agree on.
        ///
        /// THE TWO HALVES DO NOT WRITE AN ADDRESS THE SAME WAY, and comparing
        /// them as text is how a working feature reads as a broken one. This
        /// side pads a bare address to eight hex digits, so a hand-typed
        /// 0x1c02750 and a pasted 0x01C02750 are one row rather than two. The
        /// scanner prints the shortest form it can, with no padding. On a 32 bit
        /// target every heap address is below 0x10000000, so it has a leading
        /// zero here and none there, and the two strings are never equal: the
        /// address we asked for a pointer path to comes back named in a spelling
        /// this side does not recognise as its own.
        ///
        /// So every comparison between an address WE hold and an address the
        /// scanner MINTED goes through here. Rows the scanner merely echoes back
        /// are unaffected, because it echoes exactly what it was sent.
        ///
        /// Returns the input, trimmed, when it is not an address at all: an
        /// unparseable string still has to compare equal to itself.</summary>
        public static string AddressKey(string addr)
        {
            string s = (addr ?? "").Trim();
            if (s.Length == 0) return "";
            string norm = MemoryWatchList.NormalizeAddress(s, out _);
            return (norm ?? s).ToLowerInvariant();
        }

        /// <summary>Whether two addresses name the same place, whichever half
        /// wrote each of them. Works for all three route shapes, including a
        /// pointer path whose root is an absolute address and therefore carries
        /// the same padding difference inside its brackets.</summary>
        public static bool SameAddress(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(AddressKey(a), AddressKey(b), StringComparison.Ordinal);
        }
    }

    // ---- candidates ---------------------------------------------------------

    /// <summary>One address that might be a field, with what it has been reading
    /// and what it did during the last elimination round.</summary>
    public sealed class MemoryFieldCandidate
    {
        /// <summary>The address exactly as it goes on the wire and comes back,
        /// which is this candidate's identity.</summary>
        [JsonProperty("addr")] public string Addr { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("len")]  public int Len { get; set; }

        /// <summary>The same address written as module+offset, when the scanner
        /// said it was inside a loaded image. Its presence is the difference
        /// between a candidate that is already durable and one that will need a
        /// pointer path before it can be promoted.</summary>
        [JsonProperty("module")] public string ModuleAddr { get; set; }

        /// <summary>What the scanner said about it when it turned up. Kept
        /// verbatim: it is the only record of how this address was arrived at.</summary>
        [JsonProperty("note")] public string Note { get; set; }

        /// <summary>How many rounds this candidate has survived. Not a ranking
        /// and not shown as one: it is a count of the evidence behind it.</summary>
        [JsonProperty("survived")] public int RoundsSurvived { get; set; }

        // ---- this session's readings, not saved ----------------------------

        [JsonIgnore] public string Shown { get; set; }
        [JsonIgnore] public bool HasRead { get; set; }
        [JsonIgnore] public bool LastOk { get; set; }
        [JsonIgnore] public int Changes { get; set; }

        // ---- the round in progress -----------------------------------------

        /// <summary>The first value this candidate was successfully read at
        /// during the round. Everything since is compared against it, rather
        /// than against the reading before it, so a value that moves and comes
        /// back still counts as having moved.</summary>
        [JsonIgnore] public string RoundFirstOk { get; set; }
        [JsonIgnore] public int RoundOkReads { get; set; }
        [JsonIgnore] public bool RoundChanged { get; set; }
        [JsonIgnore] public bool RoundFailedRead { get; set; }

        /// <summary>True when the address is already durable on its own. A
        /// candidate that is not can still be promoted, but it needs a pointer
        /// path first, because a bare heap address is not a map entry.</summary>
        [JsonIgnore] public bool IsAnchored
            => !string.IsNullOrWhiteSpace(ModuleAddr) || MemoryRoutes.Of(Addr) != MemoryRoute.Absolute;

        /// <summary>The address to watch and to promote with: the durable form
        /// when there is one.</summary>
        [JsonIgnore] public string BestAddr
            => string.IsNullOrWhiteSpace(ModuleAddr) ? Addr : ModuleAddr;
    }

    /// <summary>What one round of elimination did, in the numbers the tab
    /// shows: the count falling is the whole product.</summary>
    public sealed class MemoryEliminationResult
    {
        public string FieldKey;
        public MemoryEliminationAction Action;
        public int Before;
        public int After;
        public int Eliminated;
        /// <summary>Candidates that could not be judged because the scanner did
        /// not report them enough times during the round. KEPT, never dropped: a
        /// candidate is eliminated on evidence, never on silence, or a watch
        /// list that quietly lost half its rows would delete the answer.</summary>
        public int Unread;
        public string Line;
    }

    // ---- a field, as the map holds it ---------------------------------------

    public enum MemoryFieldState { Empty, Candidates, Confirmed }

    /// <summary>An entry that has been promoted: this field, at this address,
    /// confirmed this way, on this day.</summary>
    public sealed class MemoryMapEntry
    {
        [JsonProperty("field")] public string FieldKey { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("kind")]  public string Kind { get; set; }
        [JsonProperty("len")]   public int Len { get; set; }

        /// <summary>The preferred route's address, in the form it is sent in.</summary>
        [JsonProperty("addr")] public string Addr { get; set; }

        /// <summary>The second way in, kept so an entry whose preferred route
        /// stops resolving has somewhere to fall back to rather than simply
        /// going dark. Usually the absolute address a path was found from, which
        /// is honest only within the launch it was found in, and is marked as
        /// such below.</summary>
        [JsonProperty("fallback")] public string Fallback { get; set; }

        /// <summary>True when the fallback is only good for the launch it was
        /// recorded in, which is the case for every bare heap address. Written
        /// down so a later read through the fallback is not mistaken for the map
        /// still working.</summary>
        [JsonProperty("fallbackVolatile")] public bool FallbackVolatile { get; set; }

        /// <summary>What made this an entry, in the operator's own terms: which
        /// declared actions it survived, and how many.</summary>
        [JsonProperty("how")] public string ConfirmedHow { get; set; }

        [JsonProperty("confirmedUtc")] public DateTime ConfirmedUtc { get; set; }

        /// <summary>The results folder of the run that found it. Which run found
        /// a row is the difference between being able to go back to the evidence
        /// and having to find it again.</summary>
        [JsonProperty("run")] public string Run { get; set; }

        /// <summary>How many separate launches this entry has resolved and read
        /// plausibly in, counting the one it was confirmed in. This is the
        /// number that separates a route that worked once from one that is
        /// actually durable, and it is the reason a path validated across a
        /// relaunch is worth more than one that resolved.</summary>
        [JsonProperty("launches")] public int Launches { get; set; }

        /// <summary>The last plausible value, so an entry that has gone stale
        /// can be shown next to what it used to read.</summary>
        [JsonProperty("lastGood")] public string LastGood { get; set; }

        // ---- this session ---------------------------------------------------

        [JsonIgnore] public string Shown { get; set; }
        [JsonIgnore] public bool HasRead { get; set; }
        /// <summary>Null until this launch has read it. Then null means live and
        /// anything else is the reason it is stale.</summary>
        [JsonIgnore] public string StaleWhy { get; set; }
        [JsonIgnore] public bool CheckedThisLaunch { get; set; }
        /// <summary>This launch has already been counted towards
        /// <see cref="Launches"/>. Separate from CheckedThisLaunch because a
        /// single game launch can be read by several scanner runs, and counting
        /// each of those as a relaunch would turn one afternoon into a durability
        /// claim nothing earned.</summary>
        [JsonIgnore] public bool CountedThisLaunch { get; set; }
        /// <summary>The read came back through the fallback rather than the
        /// preferred route, or could not be resolved at all.</summary>
        [JsonIgnore] public bool ResolveFailed { get; set; }

        [JsonIgnore] public MemoryRoute Route => MemoryRoutes.Of(Addr);

        /// <summary>Live, stale or not checked yet. Three states rather than
        /// two because a game that is not running cannot be read, and calling
        /// that stale would cry wolf every time SimHub starts first.</summary>
        [JsonIgnore] public string StateWord
            => !CheckedThisLaunch ? "not checked" : StaleWhy == null ? "live" : "STALE";
    }

    /// <summary>One field's whole state: its candidates, and the entry it was
    /// promoted to.</summary>
    public sealed class MemoryField
    {
        [JsonProperty("key")]  public string Key { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>True for a field the operator added, so the catalog does not
        /// have to be edited to map something nobody thought of.</summary>
        [JsonProperty("custom")] public bool Custom { get; set; }

        [JsonProperty("entry")] public MemoryMapEntry Entry { get; set; }

        [JsonProperty("candidates")] public List<MemoryFieldCandidate> Candidates { get; set; }
            = new List<MemoryFieldCandidate>();

        /// <summary>The declared actions this field's candidates have already
        /// been through, in order. Carried into the entry when one is promoted,
        /// because "it survived a car change and a sit-still" is the confirmation
        /// record and losing it would leave a row nobody can audit.</summary>
        [JsonProperty("rounds")] public List<string> RoundsRun { get; set; } = new List<string>();

        [JsonIgnore]
        public MemoryFieldState State
            => Entry != null ? MemoryFieldState.Confirmed
             : (Candidates != null && Candidates.Count > 0) ? MemoryFieldState.Candidates
             : MemoryFieldState.Empty;

        [JsonIgnore] private MemoryFieldDef _def;

        /// <summary>The catalog entry behind this field. Cached because it is
        /// asked for on every read of every entry, five times a second: for a
        /// built-in it is the shared static instance, and for a custom field it
        /// would otherwise build a definition and a sanity band per read.</summary>
        [JsonIgnore]
        public MemoryFieldDef Def
        {
            get
            {
                if (_def == null)
                    _def = (Custom ? null : MemoryFields.ByKey(Key)) ?? MemoryFields.Custom(Key, Name);
                return _def;
            }
        }
    }

    // ---- the store ----------------------------------------------------------

    /// <summary>The map for one game: its fields, their candidates, their
    /// entries, and the file all of that lives in. Not thread safe: every caller
    /// is the UI thread or the plugin's own start path, exactly as the watch
    /// list is.</summary>
    public sealed class MemoryFieldStore
    {
        /// <summary>How many candidates one field will hold. A name search
        /// legitimately returns a hundred and fifty, which is the case this
        /// whole feature exists for, so the cap is well above that and exists
        /// only to stop a runaway search filling memory.</summary>
        public const int MaxCandidatesPerField = 512;

        /// <summary>THE SCANNER'S OWN CEILING on its watch list, which is the
        /// wire this map rides on. It refuses row 257 with "REFUSED, the watch
        /// list is full", so anything armed past this is a row nobody is reading
        /// and no round can ever judge, which looks from here exactly like a row
        /// that failed honestly. Measured against the real memscan, not assumed:
        /// an earlier build of this file armed 400 and 144 of them went nowhere.
        ///
        /// If a future scanner raises its cap this number follows it, and the
        /// only cost of being behind is fewer candidates per round.</summary>
        public const int ScannerWatchRows = 256;

        /// <summary>How many addresses the MAP will hand to the scanner at once.
        /// Every one of them rides on every refresh, so this is a real wire limit
        /// rather than a formality. It is stated when it bites, and it never
        /// causes an elimination: an address that is not being read is not
        /// evidence either way.
        ///
        /// Below the scanner's ceiling on purpose, because the hand-curated watch
        /// list shares that ceiling and the operator's own rows are the ones they
        /// are looking at. The caller passes what is actually left; this is only
        /// the ceiling on that.</summary>
        public const int MaxArmed = ScannerWatchRows;

        /// <summary>Successful reads a candidate needs during a round before it
        /// can be judged. Two, because one reading proves nothing about whether
        /// a value moved.</summary>
        public const int MinReadsToJudge = 2;

        private readonly List<MemoryField> _fields = new List<MemoryField>();
        private readonly Dictionary<string, List<MemoryFieldCandidate>> _byAddr =
            new Dictionary<string, List<MemoryFieldCandidate>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Which game this map is for, as a person would say it.</summary>
        public string Game { get; set; }
        /// <summary>The process whose memory it describes, without .exe. This is
        /// what the map is really about: a map belongs to an executable's layout,
        /// not to the name SimHub happens to be showing.</summary>
        public string Process { get; set; }

        public IReadOnlyList<MemoryField> Fields => _fields;

        /// <summary>The field the tab is pointed at. Findings from a run land
        /// here, and elimination acts on it.</summary>
        public string SelectedKey { get; set; }

        // ---- the round in progress ------------------------------------------

        public MemoryEliminationAction RoundAction { get; private set; }
        public string RoundFieldKey { get; private set; }
        public DateTime RoundStartedUtc { get; private set; }
        public bool RoundLive => RoundAction != null;

        /// <summary>When a refresh last arrived. A round's baseline is only
        /// seeded from what the candidates were already showing when that
        /// reading is FRESH: a value read ten minutes ago is not what the field
        /// held when the operator pressed start, and a hold round judged against
        /// it would rule out the right answer for moving while nobody was
        /// looking.</summary>
        /// Written by Observe. The setter is internal only so the freshness rule
        /// can be exercised without a test that sleeps for it.
        public DateTime LastObservedUtc { get; internal set; } = DateTime.MinValue;

        /// <summary>How old a reading may be and still seed a round's baseline.
        /// Two seconds is ten refreshes at the default rate, and comfortably
        /// longer than any gap a live watch leaves.</summary>
        public static readonly TimeSpan BaselineFreshness = TimeSpan.FromSeconds(2);

        /// <summary>What the last finished round threw out, kept so it can be
        /// put back. A declared action pressed by mistake would otherwise delete
        /// a whole session's candidates with nothing to undo it.</summary>
        private readonly List<MemoryFieldCandidate> _undoDropped = new List<MemoryFieldCandidate>();
        /// <summary>The survivors whose round count the last round put up, so an
        /// undo can put it back down. Without this a round run and undone would
        /// leave every survivor claiming one more piece of evidence behind it
        /// than actually exists, and that claim ends up written into the map as
        /// the confirmation record.</summary>
        private readonly List<MemoryFieldCandidate> _undoCredited = new List<MemoryFieldCandidate>();
        private string _undoFieldKey;
        private string _undoLine;

        public bool CanUndo => _undoDropped.Count > 0;
        public string UndoLine => _undoLine;

        // ---- fields ----------------------------------------------------------

        public MemoryFieldStore()
        {
            EnsureBuiltIns();
        }

        /// <summary>Every catalog field exists in every map, whether or not it
        /// has been looked for. The tab's main view is the map, and a map that
        /// only listed the fields somebody had already found would not tell
        /// anyone what is left to do.</summary>
        public void EnsureBuiltIns()
        {
            foreach (var def in MemoryFields.BuiltIn)
            {
                if (Find(def.Key) != null) continue;
                _fields.Add(new MemoryField { Key = def.Key, Name = def.Name, Custom = false });
            }
            // Catalog order first, then whatever the operator added, so the
            // priority order the owner asked for is what the list shows.
            _fields.Sort((a, b) =>
            {
                int ia = IndexInCatalog(a.Key), ib = IndexInCatalog(b.Key);
                if (ia != ib) return ia.CompareTo(ib);
                return string.Compare(a.Name ?? a.Key, b.Name ?? b.Key, StringComparison.OrdinalIgnoreCase);
            });
            if (SelectedKey == null || Find(SelectedKey) == null)
                SelectedKey = _fields.Count > 0 ? _fields[0].Key : null;
            RebuildIndex();
        }

        private static int IndexInCatalog(string key)
        {
            var list = MemoryFields.BuiltIn;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        public MemoryField Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var f in _fields)
                if (string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        public MemoryField Selected => Find(SelectedKey);

        /// <summary>Add a field the catalog does not have. Returns null when it
        /// is there, or the reason it is not.</summary>
        public string AddCustomField(string name, out MemoryField field)
        {
            field = null;
            string key = MemoryFields.KeyFromName(name);
            if (key == null) return "Give the field a name first.";
            var existing = Find(key);
            if (existing != null) { field = existing; return null; }
            field = new MemoryField
            {
                Key = key,
                Name = MemoryWatchList.CleanLabel(name),
                Custom = true,
            };
            _fields.Add(field);
            return null;
        }

        public bool RemoveCustomField(string key)
        {
            var f = Find(key);
            if (f == null || !f.Custom) return false;
            _fields.Remove(f);
            if (string.Equals(SelectedKey, key, StringComparison.OrdinalIgnoreCase))
                SelectedKey = _fields.Count > 0 ? _fields[0].Key : null;
            RebuildIndex();
            return true;
        }

        // ---- candidates ------------------------------------------------------

        /// <summary>Put one finding on a field as a candidate. Returns null when
        /// it landed, or the reason it did not. Re-adding an address already
        /// there is a correction, not a mistake: the kind and the note are
        /// updated and everything it has read is kept.</summary>
        public string AddCandidate(string fieldKey, string addr, string kind, int len,
                                   string moduleAddr, string note, out MemoryFieldCandidate candidate)
        {
            candidate = null;
            var field = Find(fieldKey);
            if (field == null) return "There is no field called \"" + (fieldKey ?? "") + "\".";

            string a = MemoryWatchList.NormalizeAddress(addr, out string problem);
            if (a == null) return problem;
            string k = MemoryWatchList.NormalizeKind(kind) ?? field.Def.Kind;

            string m = null;
            if (!string.IsNullOrWhiteSpace(moduleAddr))
            {
                m = MemoryWatchList.NormalizeAddress(moduleAddr, out _);
                // A module form that does not parse is dropped rather than
                // refused: the absolute address is still worth a candidate, and
                // the anchoring is what the promote step is for.
            }

            if (field.Candidates == null) field.Candidates = new List<MemoryFieldCandidate>();
            foreach (var c in field.Candidates)
            {
                if (!string.Equals(c.Addr, a, StringComparison.OrdinalIgnoreCase)) continue;
                c.Kind = k;
                c.Len = MemoryWatchList.NormalizeLen(k, len);
                if (m != null) c.ModuleAddr = m;
                if (!string.IsNullOrWhiteSpace(note)) c.Note = note;
                candidate = c;
                return null;
            }

            if (field.Candidates.Count >= MaxCandidatesPerField)
                return "That field already holds " + MaxCandidatesPerField.ToString(CultureInfo.InvariantCulture)
                     + " candidates, which is more than a search should ever need. Eliminate a round first.";

            candidate = new MemoryFieldCandidate
            {
                Addr = a,
                Kind = k,
                Len = MemoryWatchList.NormalizeLen(k, len),
                ModuleAddr = m,
                Note = note,
            };
            field.Candidates.Add(candidate);
            Index(candidate);
            return null;
        }

        public bool RemoveCandidate(string fieldKey, string addr)
        {
            var field = Find(fieldKey);
            if (field?.Candidates == null) return false;
            for (int i = 0; i < field.Candidates.Count; i++)
            {
                if (!string.Equals(field.Candidates[i].Addr, addr, StringComparison.OrdinalIgnoreCase)) continue;
                field.Candidates.RemoveAt(i);
                RebuildIndex();
                return true;
            }
            return false;
        }

        public void ClearCandidates(string fieldKey)
        {
            var field = Find(fieldKey);
            if (field == null) return;
            field.Candidates = new List<MemoryFieldCandidate>();
            field.RoundsRun = new List<string>();
            _undoDropped.Clear();
            _undoCredited.Clear();
            _undoFieldKey = null;
            _undoLine = null;
            RebuildIndex();
        }

        /// <summary>Candidates in the order the tab draws them: the ones that are
        /// already durable first, then by address.
        ///
        /// Deliberately NOT by a score. This tool has already ranked an audio
        /// filter above a tachometer with high confidence, and an operator shown
        /// a "most likely first" list learns to trust the order rather than the
        /// evidence. Anchoring is a fact about durability, not a guess about
        /// which one is right, so it is the only thing allowed to reorder.</summary>
        public IReadOnlyList<MemoryFieldCandidate> CandidatesInOrder(string fieldKey)
        {
            var field = Find(fieldKey);
            var list = new List<MemoryFieldCandidate>();
            if (field?.Candidates == null) return list;
            list.AddRange(field.Candidates);
            list.Sort((a, b) =>
            {
                if (a.IsAnchored != b.IsAnchored) return a.IsAnchored ? -1 : 1;
                return string.Compare(a.Addr, b.Addr, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        // ---- what the scanner is reading -------------------------------------

        /// <summary>True when the map asked for this address, as a candidate or
        /// as a confirmed entry. The tab uses it to tell a row the map put on
        /// the wire apart from one nobody asked for, and the entries have to
        /// count: they are on the wire for every launch, so leaving them out
        /// would report the confirmed half of the map as strangers forever.</summary>
        public bool Knows(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return false;
            string a = addr.Trim();
            if (_byAddr.ContainsKey(a)) return true;
            foreach (var f in _fields)
                if (f.Entry != null && string.Equals(f.Entry.Addr, a, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>How many addresses the map wants read. Counted without
        /// building the list, because the button that starts a watch asks this
        /// on every resync: a map with entries and no watch-list rows still has
        /// everything to check, and a watch button disabled in that state would
        /// leave a saved map permanently unverifiable.</summary>
        public int ReadableCount
        {
            get
            {
                int n = 0;
                foreach (var f in _fields)
                {
                    if (f.Entry != null && !string.IsNullOrWhiteSpace(f.Entry.Addr)) n++;
                    if (f.Candidates != null) n += f.Candidates.Count;
                }
                return n;
            }
        }

        /// <summary>Every address the scanner should be reading for the map:
        /// each field's entry first, because a stale entry is the thing that has
        /// to be caught, then the candidates. Capped, and the caller is told
        /// when the cap bit.</summary>
        public IReadOnlyList<MemoryFieldCandidate> ArmList(out int dropped)
            => ArmList(MaxArmed, out dropped);

        /// <summary>The same list, held to a budget the caller worked out.
        ///
        /// The budget exists because the scanner's watch list is ONE list with
        /// one ceiling, shared with the rows the operator curated by hand, and
        /// the map does not get to have those. Passing the real number in is the
        /// difference between a candidate that is not being read and being told
        /// so, and a candidate that is not being read and looking exactly like
        /// one that failed a round honestly.
        ///
        /// Entries always go first and are never dropped: a confirmed row that
        /// stops being checked is a map that has quietly stopped saying whether
        /// it still works.</summary>
        public IReadOnlyList<MemoryFieldCandidate> ArmList(int budget, out int dropped)
        {
            dropped = 0;
            if (budget < 0) budget = 0;
            if (budget > MaxArmed) budget = MaxArmed;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<MemoryFieldCandidate>();

            foreach (var f in _fields)
            {
                var e = f.Entry;
                if (e == null || string.IsNullOrWhiteSpace(e.Addr)) continue;
                if (!seen.Add(e.Addr)) continue;
                list.Add(new MemoryFieldCandidate { Addr = e.Addr, Kind = e.Kind, Len = e.Len, Note = e.Label });
            }
            foreach (var f in _fields)
            {
                if (f.Candidates == null) continue;
                foreach (var c in f.Candidates)
                {
                    if (string.IsNullOrWhiteSpace(c.Addr)) continue;
                    if (!seen.Add(c.Addr)) continue;
                    if (list.Count >= budget) { dropped++; continue; }
                    list.Add(c);
                }
            }
            return list;
        }

        /// <summary>Fold one watch refresh into the map: every candidate and
        /// every entry that this refresh carried. Returns how many of the
        /// reported rows belonged here, so the caller can tell a map row from a
        /// watch-list row from a stranger.
        ///
        /// This is where a round accumulates its evidence, and where an entry is
        /// sanity checked. Both happen on the same refresh because both ask the
        /// same question of the same reading.</summary>
        public int Observe(IEnumerable<ScanWatchRow> reported)
        {
            if (reported == null) return 0;
            LastObservedUtc = DateTime.UtcNow;
            int mine = 0;
            foreach (var r in reported)
            {
                if (r == null || string.IsNullOrEmpty(r.Addr)) continue;
                string shown = r.Ok
                    ? (r.Value ?? "")
                    : "cannot read: " + (string.IsNullOrEmpty(r.Why) ? "no reason given" : r.Why);

                bool touched = false;

                if (_byAddr.TryGetValue(r.Addr.Trim(), out var cands))
                {
                    touched = true;
                    foreach (var c in cands) FoldCandidate(c, shown, r.Ok);
                }

                foreach (var f in _fields)
                {
                    var e = f.Entry;
                    if (e == null || !string.Equals(e.Addr, r.Addr, StringComparison.OrdinalIgnoreCase)) continue;
                    touched = true;
                    FoldEntry(f, e, r, shown);
                }

                if (touched) mine++;
            }
            return mine;
        }

        private void FoldCandidate(MemoryFieldCandidate c, string shown, bool ok)
        {
            bool changed = c.HasRead && !string.Equals(c.Shown, shown, StringComparison.Ordinal);
            c.Shown = shown;
            c.HasRead = true;
            c.LastOk = ok;
            if (changed) c.Changes++;

            if (!RoundLive) return;
            // Only successful reads are evidence. A read that failed and came
            // back changes the shown text, and counting that as "it moved" would
            // let a region being freed and remapped survive a round it should
            // have died in.
            if (!ok) { c.RoundFailedRead = true; return; }
            c.RoundOkReads++;
            if (c.RoundFirstOk == null) { c.RoundFirstOk = shown; return; }
            if (!string.Equals(c.RoundFirstOk, shown, StringComparison.Ordinal)) c.RoundChanged = true;
        }

        private void FoldEntry(MemoryField field, MemoryMapEntry e, ScanWatchRow r, string shown)
        {
            e.Shown = shown;
            e.HasRead = true;
            e.CheckedThisLaunch = true;
            // The scanner draws the distinction the map needs: a row it could
            // not RESOLVE comes back with nothing to resolve to, while a row it
            // resolved and could not READ names where it landed. The two have
            // different fixes, so they get different words.
            e.ResolveFailed = !r.Ok && string.IsNullOrEmpty(r.Resolved);
            if (!r.Ok)
            {
                e.StaleWhy = e.ResolveFailed
                    ? "it no longer resolves: " + (string.IsNullOrEmpty(r.Why) ? "the root is not there" : r.Why)
                    : "it resolves but cannot be read: " + (string.IsNullOrEmpty(r.Why) ? "no reason given" : r.Why);
                return;
            }
            string why = field.Def.Sanity.Check(shown);
            e.StaleWhy = why == null ? null : "it reads back wrong: " + why;
            if (why == null)
            {
                e.LastGood = shown;
                // Counted once per launch, on the first plausible read. This is
                // the number that separates a route that resolved once from one
                // that has survived a relaunch, which is the whole difference
                // between a map entry and a lucky address.
                if (!e.CountedThisLaunch) { e.Launches++; e.CountedThisLaunch = true; }
            }
        }

        /// <summary>Forget every reading, without touching the map. Called when
        /// a scanner starts: the first value a row shows in a new run is the row
        /// arriving, not a value that moved, and a count carried across would say
        /// the car changed while nobody was looking.</summary>
        public void ForgetReadings()
        {
            // A round in flight is void, not carried over. Its evidence was the
            // readings that have just been thrown away, and finishing it against
            // an empty slate would judge every candidate on nothing.
            if (RoundLive) CancelRound();
            LastObservedUtc = DateTime.MinValue;
            foreach (var f in _fields)
            {
                if (f.Candidates != null)
                    foreach (var c in f.Candidates)
                    {
                        c.Shown = null; c.HasRead = false; c.LastOk = false; c.Changes = 0;
                        c.RoundFirstOk = null; c.RoundOkReads = 0; c.RoundChanged = false; c.RoundFailedRead = false;
                    }
                if (f.Entry != null)
                {
                    f.Entry.Shown = null;
                    f.Entry.HasRead = false;
                    // NOT CheckedThisLaunch: a new scanner inside the same game
                    // launch is still the same launch, and re-counting it would
                    // turn one afternoon of scanning into a dozen "relaunches"
                    // this entry has supposedly survived.
                }
            }
        }

        /// <summary>A new launch of the game: every entry is unchecked again and
        /// the launch counter is armed to count the next plausible read. Called
        /// when the map is loaded and whenever the target's pid changes.</summary>
        public void BeginLaunch()
        {
            foreach (var f in _fields)
            {
                if (f.Entry == null) continue;
                f.Entry.CheckedThisLaunch = false;
                f.Entry.CountedThisLaunch = false;
                f.Entry.StaleWhy = null;
                f.Entry.ResolveFailed = false;
                f.Entry.Shown = null;
                f.Entry.HasRead = false;
            }
            ForgetReadings();
        }

        // ---- elimination -----------------------------------------------------

        /// <summary>Start a round: the operator has declared what they are about
        /// to do and has not done it yet. Returns null, or why it could not
        /// start.</summary>
        public string BeginRound(string fieldKey, string actionId)
        {
            var field = Find(fieldKey);
            if (field == null) return "Pick a field first.";
            if (field.Candidates == null || field.Candidates.Count == 0)
                return "That field has no candidates yet, so there is nothing to eliminate. Run a search first.";
            var action = MemoryEliminationActions.ById(actionId);
            if (action == null) return "Pick what you are about to do.";
            if (RoundLive) return "A round is already running. Finish or cancel it first.";

            bool fresh = LastObservedUtc != DateTime.MinValue
                      && DateTime.UtcNow - LastObservedUtc <= BaselineFreshness;
            foreach (var c in field.Candidates)
            {
                bool seed = fresh && c.HasRead && c.LastOk;
                c.RoundFirstOk = seed ? c.Shown : null;
                c.RoundOkReads = seed ? 1 : 0;
                c.RoundChanged = false;
                c.RoundFailedRead = false;
            }
            RoundAction = action;
            RoundFieldKey = field.Key;
            RoundStartedUtc = DateTime.UtcNow;
            return null;
        }

        /// <summary>Throw away the baselines this round started with and take the
        /// next reading as the baseline instead. Returns false when no round is
        /// open.
        ///
        /// Called when the scanner acknowledges the round-start, so the two
        /// halves measure the SAME WINDOW. Without it this side begins the moment
        /// the button is pressed and the scanner begins when its next refresh
        /// picks the command up, and anything that moves in that gap is counted
        /// by one half and not the other. On a value that ticks once a second
        /// that is a routine one-row disagreement, and the cross-check then tells
        /// the operator to throw away a round that was fine.</summary>
        public bool RebaseRound()
        {
            if (!RoundLive) return false;
            var field = Find(RoundFieldKey);
            if (field?.Candidates == null) return false;
            foreach (var c in field.Candidates)
            {
                c.RoundFirstOk = null;
                c.RoundOkReads = 0;
                c.RoundChanged = false;
                c.RoundFailedRead = false;
            }
            RoundStartedUtc = DateTime.UtcNow;
            return true;
        }

        /// <summary>Abandon the round without eliminating anything.</summary>
        public void CancelRound()
        {
            RoundAction = null;
            RoundFieldKey = null;
        }

        /// <summary>Finish the round and filter. Everything that did not respond
        /// the way the field must respond is dropped in one go; anything the
        /// scanner did not read enough times is kept and counted, because a
        /// candidate is eliminated on evidence and never on silence.</summary>
        public MemoryEliminationResult FinishRound()
        {
            var action = RoundAction;
            var field = Find(RoundFieldKey);
            RoundAction = null;
            string key = RoundFieldKey;
            RoundFieldKey = null;
            if (action == null || field?.Candidates == null) return null;

            var kept = new List<MemoryFieldCandidate>();
            var dropped = new List<MemoryFieldCandidate>();
            var credited = new List<MemoryFieldCandidate>();
            int unread = 0;

            foreach (var c in field.Candidates)
            {
                if (c.RoundOkReads < MinReadsToJudge)
                {
                    unread++;
                    kept.Add(c);
                    continue;
                }
                bool responded = action.Expect == MemoryFieldExpect.Change ? c.RoundChanged : !c.RoundChanged;
                if (responded) { c.RoundsSurvived++; credited.Add(c); kept.Add(c); }
                else dropped.Add(c);
            }

            int before = field.Candidates.Count;
            field.Candidates = kept;
            if (field.RoundsRun == null) field.RoundsRun = new List<string>();
            field.RoundsRun.Add(action.Id);
            RebuildIndex();

            _undoDropped.Clear();
            _undoDropped.AddRange(dropped);
            _undoCredited.Clear();
            _undoCredited.AddRange(credited);
            _undoFieldKey = key;

            var result = new MemoryEliminationResult
            {
                FieldKey = key,
                Action = action,
                Before = before,
                After = kept.Count,
                Eliminated = dropped.Count,
                Unread = unread,
            };
            result.Line = DescribeRound(field, result);
            _undoLine = result.Line;
            return result;
        }

        private static string DescribeRound(MemoryField field, MemoryEliminationResult r)
        {
            var sb = new StringBuilder();
            sb.Append(r.Action.Expect == MemoryFieldExpect.Change
                ? "Kept the ones that moved. " : "Kept the ones that sat still. ");
            sb.Append(r.Before.ToString(CultureInfo.InvariantCulture));
            sb.Append(r.Before == 1 ? " candidate, " : " candidates, ");
            sb.Append(r.Eliminated.ToString(CultureInfo.InvariantCulture));
            sb.Append(" ruled out, ");
            sb.Append(r.After.ToString(CultureInfo.InvariantCulture));
            sb.Append(" left.");
            if (r.Unread > 0)
            {
                sb.Append("  ");
                sb.Append(r.Unread.ToString(CultureInfo.InvariantCulture));
                sb.Append(" of them were not read often enough to judge, so they were KEPT rather than ruled out. ");
                sb.Append("Nothing is eliminated on silence. Start the watch, check the rows are live, and run the round again.");
            }
            if (r.After == 0)
            {
                sb.Append("  Nothing survived, which usually means the action was declared the wrong way round, ");
                sb.Append("or the thing it was supposed to change did not change. Undo puts them back.");
            }
            else if (r.After <= 3 && r.Eliminated > 0)
            {
                sb.Append("  Close enough to read by eye now: watch what is left and check it says what it should.");
            }
            else if (r.Eliminated == 0 && r.Unread == 0)
            {
                sb.Append("  Nothing was ruled out, so this round proved nothing about ");
                sb.Append(field.Name ?? field.Key);
                sb.Append(". Try the opposite declaration, or a bigger change.");
            }
            return sb.ToString();
        }

        /// <summary>Put back what the last round threw out. One level, which is
        /// what a wrongly declared action needs.</summary>
        public bool UndoLastRound()
        {
            if (_undoDropped.Count == 0) return false;
            var field = Find(_undoFieldKey);
            if (field == null) { _undoDropped.Clear(); _undoCredited.Clear(); return false; }
            if (field.Candidates == null) field.Candidates = new List<MemoryFieldCandidate>();
            foreach (var c in _undoDropped)
            {
                bool have = false;
                foreach (var existing in field.Candidates)
                    if (string.Equals(existing.Addr, c.Addr, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
                if (!have) field.Candidates.Add(c);
            }
            // The survivors gave up the credit that round handed them, or an
            // entry promoted afterwards would claim a round of evidence that was
            // taken back.
            foreach (var c in _undoCredited) if (c.RoundsSurvived > 0) c.RoundsSurvived--;
            if (field.RoundsRun != null && field.RoundsRun.Count > 0)
                field.RoundsRun.RemoveAt(field.RoundsRun.Count - 1);
            _undoDropped.Clear();
            _undoCredited.Clear();
            _undoFieldKey = null;
            _undoLine = null;
            RebuildIndex();
            return true;
        }

        // ---- promotion --------------------------------------------------------

        /// <summary>Turn one candidate into a map entry. Returns null when it is
        /// in the map, or the reason it is not.
        ///
        /// "pathAddr" is a pointer path the scanner found for this candidate, if
        /// one has been asked for and answered. It is the preferred route when
        /// it is there, because it re-resolves from a fixed root on every read.
        /// Without it, a candidate inside a loaded image is anchored on
        /// module+offset, and a bare heap address is stored as what it is: an
        /// entry good for one launch, said so in the file, with the path still to
        /// come.</summary>
        public string Promote(string fieldKey, string addr, string label, string pathAddr,
                              string runFolder, out MemoryMapEntry entry)
        {
            entry = null;
            var field = Find(fieldKey);
            if (field == null) return "There is no field called \"" + (fieldKey ?? "") + "\".";

            MemoryFieldCandidate c = null;
            if (field.Candidates != null)
                foreach (var x in field.Candidates)
                    if (string.Equals(x.Addr, addr, StringComparison.OrdinalIgnoreCase)) { c = x; break; }
            if (c == null) return "That address is not one of this field's candidates any more.";

            string preferred;
            string fallback = null;
            bool volatileFallback = false;

            if (!string.IsNullOrWhiteSpace(pathAddr))
            {
                string p = MemoryWatchList.NormalizeAddress(pathAddr, out string pathProblem);
                if (p == null) return "The pointer path is not readable (" + pathProblem + ").";
                preferred = p;
                // The address the path was found FROM is the fallback, and it is
                // honest only inside the launch it was found in unless it is
                // itself anchored. Written down rather than left to be assumed.
                fallback = c.BestAddr;
                volatileFallback = MemoryRoutes.Of(fallback) == MemoryRoute.Absolute;
            }
            else if (!string.IsNullOrWhiteSpace(c.ModuleAddr))
            {
                preferred = c.ModuleAddr;
                fallback = c.Addr;
                volatileFallback = MemoryRoutes.Of(c.Addr) == MemoryRoute.Absolute;
            }
            else
            {
                preferred = c.Addr;
                fallback = null;
                volatileFallback = MemoryRoutes.Of(c.Addr) == MemoryRoute.Absolute;
            }

            string name = MemoryWatchList.CleanLabel(label);
            if (name.Length == 0) name = field.Name ?? field.Key;

            entry = new MemoryMapEntry
            {
                FieldKey = field.Key,
                Label = name,
                Kind = c.Kind,
                Len = c.Len,
                Addr = preferred,
                Fallback = fallback,
                FallbackVolatile = volatileFallback,
                ConfirmedHow = DescribeConfirmation(field, c),
                ConfirmedUtc = DateTime.UtcNow,
                Run = runFolder,
                Launches = 0,
                LastGood = c.HasRead && c.LastOk ? c.Shown : null,
            };
            field.Entry = entry;
            // The candidate list has done its job for this field. Kept rather
            // than cleared would leave a hundred rows under a confirmed answer,
            // which is exactly the wall of rows the field frame replaced.
            field.Candidates = new List<MemoryFieldCandidate>();
            RebuildIndex();
            return null;
        }

        /// <summary>How this entry was confirmed, written down at the moment it
        /// is promoted. Not decoration: an entry nobody can audit is an entry
        /// nobody should trust, and "it survived a car change and a sit-still"
        /// is the difference between evidence and a number somebody liked.</summary>
        private static string DescribeConfirmation(MemoryField field, MemoryFieldCandidate c)
        {
            var parts = new List<string>();
            if (field.RoundsRun != null && field.RoundsRun.Count > 0)
            {
                var words = new List<string>();
                foreach (string id in field.RoundsRun)
                {
                    var a = MemoryEliminationActions.ById(id);
                    if (a != null) words.Add(a.Prompt.Replace("I am about to ", ""));
                }
                if (words.Count > 0)
                    parts.Add("survived " + words.Count.ToString(CultureInfo.InvariantCulture)
                            + " elimination round(s): " + string.Join(", then ", words));
            }
            if (c.RoundsSurvived > 0)
                parts.Add("responded correctly in " + c.RoundsSurvived.ToString(CultureInfo.InvariantCulture)
                        + " of them");
            if (c.Changes > 0)
                parts.Add("watched live and seen to move " + c.Changes.ToString(CultureInfo.InvariantCulture) + " time(s)");
            if (!string.IsNullOrWhiteSpace(c.Note)) parts.Add("found as: " + c.Note.Trim());
            if (parts.Count == 0) parts.Add("promoted by hand, with no elimination round behind it");
            return string.Join("; ", parts);
        }

        /// <summary>Take an entry back out of the map, leaving the field empty.
        /// Its address comes back as a candidate so a re-find does not start
        /// from nothing.</summary>
        public bool Demote(string fieldKey)
        {
            var field = Find(fieldKey);
            if (field?.Entry == null) return false;
            var e = field.Entry;
            field.Entry = null;
            if (field.Candidates == null) field.Candidates = new List<MemoryFieldCandidate>();
            field.Candidates.Add(new MemoryFieldCandidate
            {
                Addr = e.Addr,
                Kind = e.Kind,
                Len = e.Len,
                ModuleAddr = MemoryRoutes.Of(e.Addr) == MemoryRoute.Module ? e.Addr : null,
                Note = "was a confirmed entry: " + (e.ConfirmedHow ?? ""),
            });
            RebuildIndex();
            return true;
        }

        /// <summary>How the map is doing, in one line: what is live, what is
        /// stale, and what has not been looked at. This is the sentence the tab
        /// puts at the top, and it is the reason a stale row cannot hide.</summary>
        public string Summary()
        {
            int confirmed = 0, live = 0, stale = 0, unchecked_ = 0, withCands = 0, empty = 0;
            foreach (var f in _fields)
            {
                switch (f.State)
                {
                    case MemoryFieldState.Confirmed:
                        confirmed++;
                        if (!f.Entry.CheckedThisLaunch) unchecked_++;
                        else if (f.Entry.StaleWhy == null) live++;
                        else stale++;
                        break;
                    case MemoryFieldState.Candidates: withCands++; break;
                    default: empty++; break;
                }
            }
            var parts = new List<string>();
            parts.Add(confirmed.ToString(CultureInfo.InvariantCulture) + " of "
                    + _fields.Count.ToString(CultureInfo.InvariantCulture) + " field(s) confirmed");
            if (confirmed > 0)
            {
                var bits = new List<string>();
                if (live > 0) bits.Add(live.ToString(CultureInfo.InvariantCulture) + " live");
                if (stale > 0) bits.Add(stale.ToString(CultureInfo.InvariantCulture) + " STALE");
                if (unchecked_ > 0) bits.Add(unchecked_.ToString(CultureInfo.InvariantCulture) + " not checked yet");
                if (bits.Count > 0) parts.Add(string.Join(", ", bits));
            }
            if (withCands > 0) parts.Add(withCands.ToString(CultureInfo.InvariantCulture) + " with candidates");
            if (empty > 0) parts.Add(empty.ToString(CultureInfo.InvariantCulture) + " not started");
            return string.Join(". ", parts) + ".";
        }

        // ---- the address index ------------------------------------------------

        private void Index(MemoryFieldCandidate c)
        {
            if (c == null || string.IsNullOrEmpty(c.Addr)) return;
            if (!_byAddr.TryGetValue(c.Addr, out var list))
            {
                list = new List<MemoryFieldCandidate>();
                _byAddr[c.Addr] = list;
            }
            if (!list.Contains(c)) list.Add(c);
        }

        /// <summary>The address to candidate index, rebuilt. One address can be
        /// a candidate for more than one field (a value could be the revs or the
        /// speed until a round says otherwise), so it maps to a list.</summary>
        private void RebuildIndex()
        {
            _byAddr.Clear();
            foreach (var f in _fields)
            {
                if (f.Candidates == null) continue;
                foreach (var c in f.Candidates) Index(c);
            }
        }

        // ---- the file ----------------------------------------------------------

        private sealed class FileShape
        {
            [JsonProperty("version")] public int Version { get; set; }
            [JsonProperty("game")]    public string Game { get; set; }
            [JsonProperty("process")] public string Process { get; set; }
            [JsonProperty("savedUtc")] public DateTime SavedUtc { get; set; }
            /// <summary>The field that was being worked on. Saved because it is
            /// where the operator left off, and coming back to a tab pointed at
            /// something else is how a run's findings end up on the wrong
            /// field.</summary>
            [JsonProperty("selected")] public string Selected { get; set; }
            [JsonProperty("fields")]  public List<MemoryField> Fields { get; set; }
        }

        /// <summary>A file name for one game's map. The process is what the map
        /// really describes, so it is the key; the game name is written inside
        /// the file where it can be read without being a file name.</summary>
        public static string FileNameFor(string key)
        {
            string k = (key ?? "").Trim();
            if (k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) k = k.Substring(0, k.Length - 4);
            var sb = new StringBuilder();
            foreach (char c in k)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.') sb.Append(c);
                else sb.Append('_');
            }
            string name = sb.ToString().Trim('_', '.');
            if (name.Length == 0) name = "unknown";
            if (name.Length > 80) name = name.Substring(0, 80);
            return name + ".json";
        }

        /// <summary>Write the map. Returns null, or a reason worth showing: a
        /// map that silently failed to save is worse than no map. Temp file plus
        /// replace, the same shape every other store here uses.</summary>
        public string Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return "no path";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(new FileShape
                {
                    Version = 1,
                    Game = Game,
                    Process = Process,
                    SavedUtc = DateTime.UtcNow,
                    Selected = SelectedKey,
                    Fields = new List<MemoryField>(_fields),
                }, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Read the map, replacing whatever is held, and arm every entry
        /// to be re-resolved, re-read and sanity checked. A missing file is not
        /// an error: it is the ordinary state before the first field is found.
        ///
        /// Nothing loaded here is believed. Every entry comes back as "not
        /// checked", and it only becomes live once this launch has actually read
        /// it and the value made sense for its field.</summary>
        public string Load(string path)
        {
            _fields.Clear();
            _byAddr.Clear();
            _undoDropped.Clear();
            _undoCredited.Clear();
            _undoFieldKey = null;
            _undoLine = null;
            RoundAction = null;
            RoundFieldKey = null;
            SelectedKey = null;
            string problem = null;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var shape = JsonConvert.DeserializeObject<FileShape>(json, SafeJson.Settings);
                    if (shape == null) problem = "the map file could not be read";
                    else
                    {
                        Game = shape.Game;
                        Process = shape.Process;
                        SelectedKey = shape.Selected;
                        if (shape.Fields != null)
                            foreach (var f in shape.Fields)
                            {
                                if (f == null || string.IsNullOrWhiteSpace(f.Key)) continue;
                                if (Find(f.Key) != null) continue;
                                if (f.Candidates == null) f.Candidates = new List<MemoryFieldCandidate>();
                                if (f.RoundsRun == null) f.RoundsRun = new List<string>();
                                if (f.Entry != null && string.IsNullOrWhiteSpace(f.Entry.Addr)) f.Entry = null;
                                _fields.Add(f);
                            }
                    }
                }
            }
            catch (Exception ex) { problem = ex.Message; }
            // EnsureBuiltIns rebuilds the address index on its way out, which is
            // what puts the candidates that came straight off the JSON into it.
            // They never went through AddCandidate, so nothing else would, and an
            // unindexed candidate is one every reading misses: a round after a
            // restart would judge nothing and report that it kept everything
            // because it could not read it. Pinned by a test rather than left to
            // a call two methods away.
            EnsureBuiltIns();
            BeginLaunch();
            return problem;
        }
    }
}
