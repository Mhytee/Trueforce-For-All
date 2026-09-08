// The Initial D 8 leaderboard tables: record format, name encoding, and the merge.
//
// UNLIKE Id8MemoryTelemetry.cs, which is strictly read only, this file exists to WRITE the game's
// leaderboard tables. Everything here is pure: it turns bytes into records and back, encodes names
// the way the game does, and decides what the ten rows of a board should contain. Nothing in this
// file touches a process. The attach-and-write half lives separately, so the logic that decides
// what to write can be unit tested with no game running.
//
// WHERE THE TABLES ARE. The address calculator at 0x00a01640 (any car) and its per-car sibling at
// 0x00a01910 both resolve a record as
//
//     record = tableBase + 48 * (courseIdx + count * page)
//     courseIdx = (course_word >> 28) + 2 * (course_word & 0xff)     direction + 2 * course
//
// with count at [obj + 0xa0] (measured: 32, being 16 courses x 2 directions) and four table
// pointers on obj = system + 0x28, where system = [0x013bb860]:
//
//     [obj + 0xac]  per car,  shop        50 pages x 32   (one best per car, no rank axis)
//     [obj + 0xb0]  any car,  shop        10 pages x 32   (rank 0..9)
//     [obj + 0xb4]  per car,  online      50 pages x 32
//     [obj + 0xb8]  any car,  online      10 pages x 32
//
// All four are contiguous, 184320 bytes in total, and those sizes were confirmed live from the
// gaps between the four base addresses rather than assumed.
//
// SHOP vs ONLINE. Measured on the running game: the shop tables carry the local player's real
// times, and the online tables are untouched SEGA defaults on every row, because the server that
// filled them has been dead for years. The shop tables are loaded from the player's save, so
// writing them can persist; the online tables are initialised from the exe, so writes there are
// transient and cleared by a restart.

using System;
using System.Collections.Generic;
using System.Linq;

namespace TrueforceForAll.Core
{
    /// <summary>Where one in-game board gets its rows from.
    ///
    /// A source per board rather than a pair of on/off switches, because the interesting
    /// configurations are about WHICH data lands WHERE. With two boards and four sources the
    /// player can put the two pools side by side and read their standing in each: community on one
    /// board and merged on the other answers "where am I among tf4all users" and "where am I
    /// against everyone" at the same time, without either board being a lie.
    ///
    /// There is no "off" member on purpose: Arcade.Id8LeaderboardsEnabled is the off switch, and
    /// having two ways to mean off invites them to disagree.</summary>
    public enum Id8BoardSource
    {
        /// <summary>Only the real records already on the board, which on the shop board are the
        /// player's own from their save. Needs no account and no network: it just clears SEGA's
        /// filler out from under the times somebody actually set.</summary>
        Local = 0,

        /// <summary>Trueforce For All community times, and nothing else. Strictly tf4all, so a
        /// player can read where they stand among tf4all users alone.</summary>
        Community = 1,

        /// <summary>TeknoParrot's public leaderboard, and nothing else.</summary>
        TeknoParrot = 2,

        /// <summary>Everything available, ranked together: tf4all, TeknoParrot, and the player's
        /// own local records.</summary>
        Merged = 3,

    }

    /// <summary>Which of the four leaderboard tables a write is aimed at.</summary>
    public enum Id8Board
    {
        /// <summary>Per-car best, shop scope. [obj + 0xac], 50 car pages.</summary>
        ShopPerCar = 0xac,

        /// <summary>Top ten, shop scope. [obj + 0xb0], 10 rank pages. Carries the player's own
        /// real times and is loaded from their save, so writes here can persist.</summary>
        ShopTopTen = 0xb0,

        /// <summary>Per-car best, online scope. [obj + 0xb4], 50 car pages.</summary>
        OnlinePerCar = 0xb4,

        /// <summary>Top ten, online scope. [obj + 0xb8], 10 rank pages. Nothing but SEGA defaults
        /// on a real machine, and the game never writes it, so it is ours to own.</summary>
        OnlineTopTen = 0xb8,
    }

    /// <summary>One 48-byte leaderboard record, as the game stores it.</summary>
    public struct Id8LeaderboardRecord
    {
        public const int Size = 48;

        /// <summary>Offset of the name, which runs to <see cref="NameBytes"/> and is NUL
        /// terminated. Fullwidth Shift-JIS, so two bytes per character.</summary>
        public const int NameOffset = 0x00;
        public const int NameBytes = 0x14;

        public const int FlagsOffset = 0x17;
        public const int PlayerIdOffset = 0x18;
        public const int TimestampOffset = 0x1c;
        public const int SectionsOffset = 0x20;   // int32[3]
        public const int GoalOffset = 0x2c;

        /// <summary>The byte at +0x16, which reads 0x09 on every record seen so far.</summary>
        public const byte ConstantAt16 = 0x09;

        /// <summary>Flag value on records set by a real player.</summary>
        public const byte FlagReal = 0x01;

        /// <summary>Flag value on the SEGA rows the game ships as filler.</summary>
        public const byte FlagDefault = 0x02;

        /// <summary>Goal time on a SEGA filler row: exactly six minutes.</summary>
        public const int DefaultGoalMs = 360000;

        /// <summary>Raw name bytes as stored, without the terminator.</summary>
        public byte[] RawName;

        public byte Flags;
        public uint PlayerId;
        public uint UnixTime;
        public int Section1, Section2, Section3;
        public int GoalMs;

        /// <summary>Bytes +0x14, +0x15 and +0x16. The first two are the CAR, the third is the
        /// constant 0x09. Kept as a blob because it is written back verbatim; read it through
        /// <see cref="CarId"/> rather than by index.</summary>
        public byte[] Reserved;   // 3 bytes: +0x14, +0x15, +0x16

        /// <summary>The car this time was set in, as the game stores it: a little-endian u16 at
        /// +0x14, and the same CarID the rest of the plugin uses, (maker &lt;&lt; 8) | member.
        ///
        /// These two bytes were read as opaque "reserved" until a real save settled it. Of one
        /// player's eighteen records, seventeen held [0, 0] and one held [0, 4]. Zero is the AE86
        /// Trueno and 0x0400 is 1024, the GC8 Impreza, which is exactly the one course that player
        /// had driven in something else. Nothing else in the 48 bytes can hold a car.
        ///
        /// It matters because the any-car board shows a car beside every row. Writing zero here,
        /// which is what building the blob as { 0, 0, 0x09 } did, made every row we wrote display
        /// as a Trueno regardless of what was actually driven.</summary>
        public int CarId
        {
            get { return Reserved != null && Reserved.Length >= 2 ? (Reserved[0] | (Reserved[1] << 8)) : 0; }
        }

        /// <summary>Build the +0x14..+0x16 blob for a car, so no caller has to know the layout.</summary>
        public static byte[] ReservedFor(int carId)
        {
            return new byte[] { (byte)(carId & 0xff), (byte)((carId >> 8) & 0xff), ConstantAt16 };
        }

        /// <summary>True for a row the game shipped as filler rather than one somebody set.
        /// Both tests matter: the flag is the game's own marker, and the six minute goal catches
        /// a filler row whose flag we have misread.</summary>
        public bool IsFiller => Flags == FlagDefault || GoalMs <= 0 || GoalMs >= DefaultGoalMs;

        public static Id8LeaderboardRecord Read(byte[] buf, int at)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (at < 0 || at + Size > buf.Length) throw new ArgumentOutOfRangeException(nameof(at));

            var r = new Id8LeaderboardRecord();

            int nameLen = 0;
            while (nameLen < NameBytes && buf[at + NameOffset + nameLen] != 0) nameLen++;
            r.RawName = new byte[nameLen];
            Array.Copy(buf, at + NameOffset, r.RawName, 0, nameLen);

            r.Reserved = new byte[3];
            Array.Copy(buf, at + 0x14, r.Reserved, 0, 3);

            r.Flags = buf[at + FlagsOffset];
            r.PlayerId = BitConverter.ToUInt32(buf, at + PlayerIdOffset);
            r.UnixTime = BitConverter.ToUInt32(buf, at + TimestampOffset);
            r.Section1 = BitConverter.ToInt32(buf, at + SectionsOffset);
            r.Section2 = BitConverter.ToInt32(buf, at + SectionsOffset + 4);
            r.Section3 = BitConverter.ToInt32(buf, at + SectionsOffset + 8);
            r.GoalMs = BitConverter.ToInt32(buf, at + GoalOffset);
            return r;
        }

        public void Write(byte[] buf, int at)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (at < 0 || at + Size > buf.Length) throw new ArgumentOutOfRangeException(nameof(at));

            for (int i = 0; i < Size; i++) buf[at + i] = 0;

            byte[] name = RawName ?? new byte[0];
            int n = Math.Min(name.Length, NameBytes - 1);   // always leave room for the terminator
            Array.Copy(name, 0, buf, at + NameOffset, n);

            byte[] res = Reserved ?? new byte[] { 0, 0, ConstantAt16 };
            Array.Copy(res, 0, buf, at + 0x14, Math.Min(3, res.Length));

            buf[at + FlagsOffset] = Flags;
            BitConverter.GetBytes(PlayerId).CopyTo(buf, at + PlayerIdOffset);
            BitConverter.GetBytes(UnixTime).CopyTo(buf, at + TimestampOffset);
            BitConverter.GetBytes(Section1).CopyTo(buf, at + SectionsOffset);
            BitConverter.GetBytes(Section2).CopyTo(buf, at + SectionsOffset + 4);
            BitConverter.GetBytes(Section3).CopyTo(buf, at + SectionsOffset + 8);
            BitConverter.GetBytes(GoalMs).CopyTo(buf, at + GoalOffset);
        }
    }

    /// <summary>Names as the game stores them: fullwidth Shift-JIS, two bytes per character.
    /// Proven from the live tables, where "MHYTEE" is 82 6c 82 67 82 78 82 73 82 64 82 64 and the
    /// filler rows read 82 72 82 64 82 66 82 60 = "SEGA". Writing plain ASCII would be halfwidth
    /// and unlike every real record in the table.</summary>
    public static class Id8Name
    {
        /// <summary>What the FIELD holds: two bytes per character, with one character
        /// of it spent on the terminator.</summary>
        public const int FieldChars = (Id8LeaderboardRecord.NameBytes / 2) - 1;   // 9

        /// <summary>What actually fits on SCREEN, which is one character less than the
        /// field allows. At the full nine the name runs into the time column and covers
        /// it (rig, 2026-09-07). The field is not the constraint here, the layout is,
        /// so this is kept as its own number rather than folded into the maths above.</summary>
        public const int MaxChars = FieldChars - 1;   // 8

        /// <summary>Used when a name has nothing left after sanitising.</summary>
        public const string Fallback = "RACER";

        /// <summary>Cut a tf4all username down to what the field can hold. Uppercases, drops
        /// anything with no fullwidth mapping, then truncates. Deliberately drops rather than
        /// substitutes: a name peppered with hyphens where the accents were reads worse than a
        /// short one.</summary>
        public static string Sanitize(string username)
        {
            if (string.IsNullOrEmpty(username)) return Fallback;

            var kept = new System.Text.StringBuilder(MaxChars);
            foreach (char raw in username)
            {
                char c = char.ToUpperInvariant(raw);
                // Underscore is legal in a tf4all username (profiles.username is
                // ^[a-zA-Z0-9_]{3,32}$), so it has to survive rather than be dropped.
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '-' || c == '_' || c == ' ';
                if (!ok) continue;
                if (c == ' ' && kept.Length == 0) continue;   // no leading space
                kept.Append(c);
                if (kept.Length == MaxChars) break;
            }

            string s = kept.ToString().TrimEnd();
            return s.Length == 0 ? Fallback : s;
        }

        /// <summary>Encode to fullwidth Shift-JIS. Returns null if any character has no mapping,
        /// so a caller that skipped <see cref="Sanitize"/> fails loudly rather than writing
        /// something the game renders as garbage.</summary>
        public static byte[] Encode(string sanitized)
        {
            if (sanitized == null) return null;
            if (sanitized.Length > MaxChars) return null;

            var outBytes = new byte[sanitized.Length * 2];
            for (int i = 0; i < sanitized.Length; i++)
            {
                char c = sanitized[i];
                int w;
                if (c >= 'A' && c <= 'Z') w = 0x8260 + (c - 'A');
                else if (c >= '0' && c <= '9') w = 0x824f + (c - '0');
                else if (c == '-') w = 0x817c;
                else if (c == '_') w = 0x8151;
                else if (c == ' ') w = 0x8140;
                else return null;
                outBytes[i * 2] = (byte)(w >> 8);
                outBytes[i * 2 + 1] = (byte)(w & 0xff);
            }
            return outBytes;
        }

        /// <summary>Decode for logging and for reading back what is already there.</summary>
        public static string Decode(byte[] raw)
        {
            if (raw == null) return string.Empty;
            var sb = new System.Text.StringBuilder(raw.Length / 2);
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                int w = (raw[i] << 8) | raw[i + 1];
                if (w >= 0x8260 && w <= 0x8279) sb.Append((char)('A' + w - 0x8260));
                else if (w >= 0x824f && w <= 0x8258) sb.Append((char)('0' + w - 0x824f));
                else if (w == 0x817c) sb.Append('-');
                else if (w == 0x8151) sb.Append('_');
                else if (w == 0x8140) sb.Append(' ');
                else sb.Append('?');
            }
            return sb.ToString();
        }
    }

    /// <summary>One time we want on a board.</summary>
    public sealed class Id8LeaderboardEntry
    {
        public string Username;

        /// <summary>CarID, (maker &lt;&lt; 8) | member. The any-car board displays a car beside
        /// every row, so a pool entry has to carry one or the row shows the wrong car.</summary>
        public int CarId;
        public int GoalMs;
        public int Section1, Section2, Section3;

        /// <summary>Seconds since the unix epoch, for the record's date field.</summary>
        public uint UnixTime;

        /// <summary>The cabinet card id of whoever set this, or 0.
        ///
        /// Non-zero only on an entry built FROM a record already on the board, and it exists so that
        /// record can be written back as what it is. PlayerId 0 is our own write signature, so
        /// dropping the id here re-stamps somebody's genuine record as ours, and the next sweep
        /// deletes it as if we had put it there, without even logging a removal. The top-ten path
        /// never had this problem because its merge carries the whole record through; the per-car
        /// path rebuilds the board out of entries and so has to carry the id itself.</summary>
        public uint PlayerId;
    }

    public static class Id8Leaderboard
    {
        /// <summary>Rows on a top-ten board.</summary>
        public const int Ranks = 10;

        /// <summary>Courses times directions, and the multiplier between pages.</summary>
        public const int CourseSlots = 32;

        /// <summary>Record index for a course and direction, matching the game's own arithmetic
        /// at 0x00a01640: idx = direction + 2 * course.</summary>
        public static int SlotIndex(int courseId, int direction)
        {
            if (courseId < 0 || courseId > 15) throw new ArgumentOutOfRangeException(nameof(courseId));
            if (direction < 0 || direction > 1) throw new ArgumentOutOfRangeException(nameof(direction));
            return 2 * courseId + direction;
        }

        /// <summary>Byte offset of a record within its table.</summary>
        public static int RecordOffset(int courseId, int direction, int page)
        {
            if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
            return Id8LeaderboardRecord.Size * (SlotIndex(courseId, direction) + CourseSlots * page);
        }

        /// <summary>Decide the ten rows of a board.
        ///
        /// Existing rows that somebody actually set are kept: on the shop board those are the
        /// player's own records and they belong there as much as any community time. SEGA filler
        /// is dropped, incoming times are added, the lot is sorted by time, and the board is
        /// topped back up with filler if there are fewer than ten real rows. Ties keep the
        /// existing row first, so a local record is never demoted by an equal community time.
        /// </summary>
        public static Id8LeaderboardRecord[] Merge(
            IReadOnlyList<Id8LeaderboardRecord> existing,
            IReadOnlyList<Id8LeaderboardEntry> incoming,
            bool keepExisting = true)
        {
            var filler = new List<Id8LeaderboardRecord>();
            var real = new List<Id8LeaderboardRecord>();

            if (existing != null)
                foreach (Id8LeaderboardRecord r in existing)
                {
                    if (r.IsFiller) filler.Add(r);
                    else if (keepExisting && IsPlausibleLap(r.GoalMs)) real.Add(r);
                }

            if (incoming != null)
                foreach (Id8LeaderboardEntry e in incoming)
                {
                    if (e == null || !IsPlausibleLap(e.GoalMs)) continue;
                    string name = Id8Name.Sanitize(e.Username);
                    byte[] enc = Id8Name.Encode(name);
                    if (enc == null) continue;

                    real.Add(new Id8LeaderboardRecord
                    {
                        RawName = enc,
                        Reserved = Id8LeaderboardRecord.ReservedFor(e.CarId),
                        Flags = Id8LeaderboardRecord.FlagReal,
                        PlayerId = 0,
                        UnixTime = e.UnixTime,
                        Section1 = e.Section1,
                        Section2 = e.Section2,
                        Section3 = e.Section3,
                        GoalMs = e.GoalMs,
                    });
                }

            // OrderBy is a stable sort, so equal times keep the order they went in, which puts
            // existing rows ahead of incoming ones.
            var ordered = real.OrderBy(r => r.GoalMs).Take(Ranks).ToList();

            // Always OUR filler, never the game's. Reusing the existing rows first meant a board
            // with ten SEGA placeholders stayed entirely SEGA, and a board with one real row
            // showed SEGA at ranks one to nine with TF4ALL appearing only at rank ten. The point
            // of naming the filler was that a player can tell which boards we are feeding.
            while (ordered.Count < Ranks)
                ordered.Add(DefaultRow());
            return ordered.ToArray();
        }

        /// <summary>Pick the rows for a board from the two pools, according to its source.
        ///
        /// Deduplication is on the SANITIZED name, not the raw one, because that is what ends up on
        /// screen: two rows that display identically read as a bug whether or not they are really
        /// two different people. The faster time wins, so somebody who has submitted to both pools
        /// appears once, at their best.
        ///
        /// That does mean two genuinely different people whose names sanitize to the same nine
        /// characters collapse into one row. On a ten-row board showing a nine-character name,
        /// there is no representation in which they are distinguishable, so collapsing them is the
        /// honest outcome rather than a loss.</summary>
        public static IReadOnlyList<Id8LeaderboardEntry> Combine(
            Id8BoardSource source,
            IReadOnlyList<Id8LeaderboardEntry> community,
            IReadOnlyList<Id8LeaderboardEntry> teknoParrot)
        {
            var pool = new List<Id8LeaderboardEntry>();
            if (UsesCommunity(source) && community != null) pool.AddRange(community);
            if (UsesTeknoParrot(source) && teknoParrot != null) pool.AddRange(teknoParrot);
            // Local contributes no external rows. It is expressed entirely by KeepsLocalRecords,
            // which tells the merge to preserve what was already on the board.

            // THE TEN FASTEST TIMES. Not the ten fastest people.
            //
            // One row per person is the usual leaderboard shape and it was what this did, keeping
            // each driver's single best. On a server with one driver that left nine of the ten
            // ranks showing TF4ALL filler while real times sat unshown, so the board read as broken
            // rather than as new.
            //
            // The fix is not to rank people and then backfill, which is what I tried first: putting
            // somebody above a time that actually beat them, because they happen to be a different
            // person, is a board that states something untrue. This is a time-attack board. It
            // ranks times.
            //
            // Deduped per person per CAR, which is not a tie-break dodge: the table's own unique key
            // is (game, course, direction, car_id, user_id), so a second slower run in the same car
            // is the same record, not a second one. Different cars are different records and each
            // earns its own place.
            var best = new Dictionary<string, Id8LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (Id8LeaderboardEntry e in pool)
            {
                if (e == null || e.GoalMs <= 0) continue;
                string name = Id8Name.Sanitize(e.Username);
                if (name.Length == 0) continue;

                string key = name + " " + e.CarId;
                if (!best.TryGetValue(key, out Id8LeaderboardEntry held)) { best[key] = e; order.Add(key); }
                else if (e.GoalMs < held.GoalMs) best[key] = e;
            }

            // Sort by time. Ties keep insertion order, which puts the community pool first when
            // both are present, because it was added first.
            return order.Select(k => best[k]).OrderBy(e => e.GoalMs).Take(Ranks).ToList();
        }

        /// <summary>Whether this source keeps the real rows already on the board.
        ///
        /// Local and Merged do; the two single-pool sources do not, and that is the point of them.
        /// "Community" has to mean strictly tf4all or a player cannot use it to read their standing
        /// among tf4all users: quietly mixing their own local records in would make the board a
        /// different thing from the one they asked for.</summary>
        /// <summary>Whether this source draws on the tf4all pool.
        ///
        /// Here rather than at each call site because there were five of them testing
        /// `source == Community || source == Merged` by hand, and adding a fifth source meant
        /// finding every one. One that got missed would not fail loudly: the board would simply
        /// come up short of rows and look like a quiet backend.</summary>
        public static bool UsesCommunity(Id8BoardSource source)
        {
            return source == Id8BoardSource.Community || source == Id8BoardSource.Merged;
        }

        /// <summary>Whether this source draws on TeknoParrot's public board.</summary>
        public static bool UsesTeknoParrot(Id8BoardSource source)
        {
            return source == Id8BoardSource.TeknoParrot || source == Id8BoardSource.Merged;
        }

        /// <summary>Whether the player's own records are ranked in alongside the chosen pool.
        ///
        /// ALWAYS. The source picks who you are measured against. It was never a question about
        /// whether you appear on your own leaderboard, and treating it as one cost the owner 17
        /// saved records: the shop board IS the save, so a row we decline to show is a row we
        /// delete.
        ///
        /// It also leaves a shape simple enough to say in one line. Local is your records alone,
        /// Trueforce For All is yours and the community's, Everyone adds TeknoParrot. Your own
        /// times are in all three, which is why no label needs to mention them.
        ///
        /// Kept as a method rather than inlined as true, because the fact that this USED to vary,
        /// and what it cost, is worth having where somebody will read it. The two guards that made
        /// keeping them safe are unchanged: LocalRecordsFrom drops rows carrying our own zero
        /// player id, so this cannot feed last session's community rows back as the player's own,
        /// and they are ranked on time like everything else rather than pinned to the top.</summary>
        public static bool KeepsLocalRecords(Id8BoardSource source)
        {
            return true;
        }

        /// <summary>The player's own records out of a board snapshot: the rows somebody actually
        /// set, with SEGA's filler dropped.
        ///
        /// This MUST come from a snapshot taken BEFORE we wrote anything. The shop board is the
        /// only one that holds real local records, and once we have written community or
        /// TeknoParrot rows onto it, reading it back would feed our own writes in again as though
        /// they were the player's. Take it once, at attach, alongside the backup.
        ///
        /// These rows are for DISPLAY only and are never submitted anywhere. A row in a save file
        /// carries no proof of who set it, the save is trivially editable, and we write to that
        /// board ourselves, so nothing read from it can be treated as a claim about a person.
        /// </summary>
        public static IReadOnlyList<Id8LeaderboardRecord> LocalRecordsFrom(
            IReadOnlyList<Id8LeaderboardRecord> snapshot)
        {
            var real = new List<Id8LeaderboardRecord>();
            if (snapshot != null)
                foreach (Id8LeaderboardRecord r in snapshot)
                    if (!r.IsFiller && !IsOurs(r) && IsPlausibleLap(r.GoalMs)) real.Add(r);
            return real;
        }

        /// <summary>Whether this row is one WE wrote, rather than one the game recorded.
        ///
        /// The game stamps a player id on a genuine record. Every row this class builds leaves it
        /// zero, because we have no cabinet id to put there, so a zero is our signature.
        ///
        /// This matters because our writes PERSIST: the game saves the board, so a row we put on
        /// screen one session is still there the next. Without this test the merge reads its own
        /// output back as "the player's local records" and folds it in again, so community and
        /// TeknoParrot names accumulate as if the player had set them, and stale times can never be
        /// displaced. Measured on a real save: all 18 genuine records carried player id 5553014
        /// while every row we had written carried 0.
        ///
        /// The failure direction is deliberate. If a cabinet ever records a genuine best with a
        /// zero player id, we would decline to merge it as a local record, which costs that player
        /// a display nicety. Getting it the other way round corrupts the boards permanently.</summary>
        public static bool IsOurs(Id8LeaderboardRecord r)
        {
            return r.PlayerId == 0;
        }

        /// <summary>The floor below which a stored value cannot be a completed course.
        ///
        /// Not every non-filler row in the game's table is a lap time. A real save carried an Akina
        /// downhill entry of exactly 41.000, three times faster than anything ever driven, and the
        /// only one of that player's eighteen records with no milliseconds on it. Merged onto the
        /// board it put them first by over a minute, ahead of a genuine 2:0x.
        ///
        /// 60 seconds is deliberately far below anything reachable rather than tuned close to it.
        /// Across all 1765 entries TeknoParrot publishes, the fastest lap ever recorded on any
        /// course in either direction is 2:06.845, so this leaves more than a minute of headroom
        /// for a future record while still rejecting a value three times faster than the sport.
        ///
        /// This is a sanity test on data we READ, not an anti-cheat measure. A submission is
        /// checked server side; this exists so a partial run, a sentinel, or whatever else the
        /// game leaves in that table cannot be displayed as somebody's record.</summary>
        public const int MinPlausibleMs = 60000;

        public static bool IsPlausibleLap(int goalMs)
        {
            return goalMs >= MinPlausibleMs && goalMs < Id8LeaderboardRecord.DefaultGoalMs;
        }

        /// <summary>Everything a board write needs: pick the pool, then decide what happens to the
        /// player's own rows. One call so the two decisions cannot drift apart.
        /// </summary>
        /// <param name="existing">Rows currently on the board being written. Used to harvest
        /// filler rows for topping the board back up to ten.</param>
        /// <param name="localRecords">The player's own records, from the pre-write shop snapshot.
        /// Passed separately from <paramref name="existing"/> because the ONLINE board holds none
        /// of its own: it is SEGA filler on every row. Without this, Merged on the online board
        /// would silently exclude the player's own times, which is not what "merged" means. Null
        /// falls back to <paramref name="existing"/>, which is correct for the shop board.</param>
        public static Id8LeaderboardRecord[] BuildBoard(
            Id8BoardSource source,
            IReadOnlyList<Id8LeaderboardRecord> existing,
            IReadOnlyList<Id8LeaderboardEntry> community,
            IReadOnlyList<Id8LeaderboardEntry> teknoParrot,
            IReadOnlyList<Id8LeaderboardRecord> localRecords = null)
        {
            bool keep = KeepsLocalRecords(source);
            var incoming = Combine(source, community, teknoParrot);

            if (!keep || localRecords == null)
                return Merge(existing, incoming, keep);

            // Rank the player's own rows in with the pool, and keep the target board's own rows
            // only as a source of filler.
            var withLocal = new List<Id8LeaderboardEntry>(incoming);
            foreach (Id8LeaderboardRecord r in localRecords)
            {
                if (r.IsFiller) continue;
                withLocal.Add(new Id8LeaderboardEntry
                {
                    Username = Id8Name.Decode(r.RawName),
                    CarId = r.CarId,
                    GoalMs = r.GoalMs,
                    Section1 = r.Section1,
                    Section2 = r.Section2,
                    Section3 = r.Section3,
                    UnixTime = r.UnixTime,
                });
            }
            return Merge(existing, Dedupe(withLocal), false);
        }

        /// <summary>Collapse rows that are the same RECORD, keeping the faster, and take the ten
        /// fastest.
        ///
        /// Same person in the same car is one record; the table's unique key says so. Same person
        /// in a different car is a different record and keeps its own place, which is what lets one
        /// driver hold several ranks on a course nobody else has driven.
        ///
        /// This keyed on the name alone, which was the last place a driver was collapsed to a
        /// single time. Combine had already been fixed and the board still showed one row, because
        /// a board that keeps local records comes through HERE instead: the by-car screen showed
        /// the owner's GT-R while the any-car board still showed only their AE86.</summary>
        private static IReadOnlyList<Id8LeaderboardEntry> Dedupe(IReadOnlyList<Id8LeaderboardEntry> pool)
        {
            var best = new Dictionary<string, Id8LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (Id8LeaderboardEntry e in pool)
            {
                if (e == null || e.GoalMs <= 0) continue;
                string name = Id8Name.Sanitize(e.Username);
                if (name.Length == 0) continue;
                string key = name + " " + e.CarId;
                if (!best.TryGetValue(key, out Id8LeaderboardEntry held)) { best[key] = e; order.Add(key); }
                else if (e.GoalMs < held.GoalMs) best[key] = e;
            }
            return order.Select(k => best[k]).OrderBy(e => e.GoalMs).Take(Ranks).ToList();
        }

        /// <summary>The name on a filler row.
        ///
        /// The game ships these as SEGA, and a board rarely has ten real times, so most of what a
        /// player sees is filler. Naming ours puts the plugin on the boards it filled, next to the
        /// times rather than above them, which is the only place we can put text in this game at
        /// all: the screen's own labels are pre-rendered images, not strings.
        ///
        /// It stays a placeholder in every other respect. The time is still the six-minute sentinel
        /// and the flag is still the game's own "this is filler" value, so nothing here invents a
        /// result or displaces one.</summary>
        public const string FillerName = "TF4ALL";

        /// <summary>A filler row, for topping a board up to ten.</summary>
        public static Id8LeaderboardRecord DefaultRow()
        {
            return new Id8LeaderboardRecord
            {
                RawName = Id8Name.Encode(FillerName),
                Reserved = new byte[] { 0, 0, Id8LeaderboardRecord.ConstantAt16 },
                Flags = Id8LeaderboardRecord.FlagDefault,
                PlayerId = 0,
                UnixTime = 0,
                Section1 = 0,
                Section2 = 0,
                Section3 = 0,
                GoalMs = Id8LeaderboardRecord.DefaultGoalMs,
            };
        }
    }
}
