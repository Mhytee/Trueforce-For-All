// Puts real names and times on Initial D 8's in-game leaderboards, and sends the player's own runs
// to the community board.
//
// The boards ID8 shows have been dead since SEGA's servers went away: every row a player sees is
// the filler the game shipped with, name SEGA and a flat six minutes. All four boards are plain
// arrays in the game's memory, so filling them needs no server and no protocol, just a write.
//
// ORDER MATTERS HERE, in one specific way. The shop board is the only one holding the player's own
// records, and we write to it. So the snapshot has to be taken BEFORE the first write and reused
// afterwards: reading the live board later would feed our own community rows back in as though the
// player had set them, and then write them out again. The snapshot is also the backup, so one read
// serves both purposes.
//
// Nothing read from the game is ever submitted. A time in a save file carries no proof of who set
// it, the save is trivially editable, and we write to that board ourselves. Only runs observed as
// the game finishes them are sent, under the signed-in account.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    internal sealed class ArcadeLeaderboardService : IDisposable
    {
        /// <summary>Game key for the community rows. Matches the RPC's p_game.</summary>
        public const string GameKey = "ID8";

        /// <summary>TeknoParrot's own id for the title, for their board URL.</summary>
        private const string TeknoParrotGameId = "ID8";

        private readonly Func<TrueforceSettings> _settings;
        private readonly Action<string> _log;
        private readonly ArcadeLeaderboardClient _client;
        private readonly TeknoParrotLeaderboardCache _tpCache;
        private readonly string _backupPath;

        private Id8LeaderboardWriter _writer;

        /// <summary>Every board is filled once per attach, not once per course.</summary>
        private bool _filled;

        /// <summary>The shop board as it was before we ever wrote, per board slot. Both the backup
        /// and the only trustworthy source of the player's own records.</summary>
        private readonly Dictionary<int, Id8LeaderboardRecord[]> _preWriteShop =
            new Dictionary<int, Id8LeaderboardRecord[]>();

        /// <summary>The player's own PER-CAR records, keyed slot then CarID, read before we wrote.
        /// Same rule as the top-ten snapshot: taken once, never refreshed from a board we have
        /// since written to.</summary>
        private readonly Dictionary<int, Dictionary<int, Id8LeaderboardEntry>> _preWriteShopCars =
            new Dictionary<int, Dictionary<int, Id8LeaderboardEntry>>();

        public ArcadeLeaderboardService(Func<TrueforceSettings> settings, Action<string> log,
                                        Func<Task<string>> accessTokenProvider)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log;
            _client = new ArcadeLeaderboardClient(settings, log, accessTokenProvider);

            string dir = Path.Combine(TfPaths.CommonRoot, "TrueforceForAll-Arcade");
            _backupPath = Path.Combine(dir, "id8-shop-board-backup.json");
            _tpCache = new TeknoParrotLeaderboardCache(
                Path.Combine(dir, "teknoparrot-id8.json"), FetchPage);
        }

        private static string FetchPage(string url)
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                http.DefaultRequestHeaders.Add("User-Agent", "TrueforceForAll");
                return http.GetStringAsync(url).GetAwaiter().GetResult();
            }
        }

        /// <summary>Attach to the running game. Safe to call repeatedly.</summary>
        public bool Attach(Process game)
        {
            if (_writer != null) return true;
            _writer = Id8LeaderboardWriter.Open(game, out string error);
            if (_writer == null) { _log?.Invoke("[TF4ALL] Arcade leaderboards: " + error); return false; }
            if (!_writer.Resolve(out string why))
            {
                _log?.Invoke("[TF4ALL] Arcade leaderboards: " + why);
                _writer.Dispose(); _writer = null;
                return false;
            }
            _log?.Invoke("[TF4ALL] Arcade leaderboards: attached, " + _writer.CourseSlots + " board slots");
            return true;
        }

        public void Detach()
        {
            _writer?.Dispose();
            _writer = null;
            _filled = false;
        }

        /// <summary>Fill EVERY board, all 16 courses in both directions.
        ///
        /// Not just the course being driven. A player browsing the leaderboard menu looks at
        /// courses they have not touched this session, and filling only the current one left every
        /// other board showing SEGA's filler, which reads as the feature half working. The game has
        /// 32 boards and the whole community payload is at most 320 rows, so there is no reason to
        /// be shy about it.
        ///
        /// Runs once per attach. The <paramref name="force"/> path is for a settings change, where
        /// the boards must be rewritten without anything else having moved.</summary>
        public async Task FillAllAsync(CancellationToken ct, bool force = false)
        {
            var s = _settings();
            if (s?.Arcade == null || !s.Arcade.Id8LeaderboardsEnabled) return;
            if (_writer == null) return;
            if (_filled && !force) return;
            _filled = true;

            Dictionary<int, List<Id8LeaderboardEntry>> community = null;
            if (NeedsCommunity(s))
                community = await _client.GetAllBoardsAsync(GameKey, Id8Leaderboard.Ranks, ct)
                                         .ConfigureAwait(false);

            Dictionary<int, Dictionary<int, Id8LeaderboardEntry>> carBests = null;
            if (NeedsCommunity(s))
                carBests = await _client.GetCarBestsAsync(GameKey, ct).ConfigureAwait(false);

            Dictionary<string, LeaderboardBoard> tp = NeedsTeknoParrot(s) ? TeknoParrotBoards() : null;

            // Said once here rather than 32 times in the loop.
            bool haveCommunity = community != null;
            bool haveTekno = tp != null;
            if (!haveCommunity && Uses(s.Arcade.Id8OnlineBoardSource, Id8BoardSource.Community))
                _log?.Invoke("[TF4ALL] Arcade: no community rows this fill, leaving those boards as the game had them");
            if (!haveTekno && Uses(s.Arcade.Id8OnlineBoardSource, Id8BoardSource.TeknoParrot))
                _log?.Invoke("[TF4ALL] Arcade: no TeknoParrot rows this fill, leaving those boards as the game had them");

            int boards = 0, cars = 0;
            for (int course = 0; course <= 15; course++)
                for (int dir = 0; dir <= 1; dir++)
                {
                    if (ct.IsCancellationRequested) return;

                    int slot = Id8Leaderboard.SlotIndex(course, dir);

                    // Snapshot the shop board once, before anything is written to it. This is both
                    // the backup and the only trustworthy source of the player's own records.
                    if (!_preWriteShop.ContainsKey(slot))
                    {
                        var snap = _writer.ReadBoard(Id8Board.ShopTopTen, course, dir);
                        if (snap == null || snap.Length == 0) continue;
                        _preWriteShop[slot] = snap;
                        SaveBackup(slot, snap);
                    }

                    IReadOnlyList<Id8LeaderboardRecord> local =
                        Id8Leaderboard.LocalRecordsFrom(_preWriteShop[slot]);

                    // And the shop PER-CAR board, for the same reason: it is the only place the
                    // player's own per-car records exist, and the online per-car board is SEGA
                    // filler on every page. Without this, Merged on the online per-car board would
                    // leave the player off their own boards, exactly as it did on the top ten
                    // before the snapshot was passed through.
                    if (!_preWriteShopCars.ContainsKey(slot))
                        _preWriteShopCars[slot] = ReadShopCarRecords(course, dir);
                    Dictionary<int, Id8LeaderboardEntry> lBest = _preWriteShopCars[slot];

                    List<Id8LeaderboardEntry> cRows = null;
                    community?.TryGetValue(slot, out cRows);
                    List<Id8LeaderboardEntry> tRows = TeknoParrotRowsFrom(tp, course, dir);

                    if (HaveDataFor(s.Arcade.Id8OnlineBoardSource, haveCommunity, haveTekno))
                        Write(Id8Board.OnlineTopTen, s.Arcade.Id8OnlineBoardSource, course, dir, cRows, tRows, local);
                    if (HaveDataFor(s.Arcade.Id8ShopBoardSource, haveCommunity, haveTekno))
                        Write(Id8Board.ShopTopTen, s.Arcade.Id8ShopBoardSource, course, dir, cRows, tRows, local);

                    // Per-car boards hold one record per car rather than a ranking, so they are
                    // written slot by slot. Only community data can fill these: TeknoParrot names a
                    // car as free text and matching 47 of their strings onto CarIDs is its own job.
                    Dictionary<int, Id8LeaderboardEntry> cBest = null;
                    carBests?.TryGetValue(slot, out cBest);
                    Dictionary<int, Id8LeaderboardEntry> tBest = TeknoParrotCarBests(tp, course, dir);
                    cars += WritePerCar(Id8Board.OnlinePerCar, s.Arcade.Id8OnlineBoardSource, course, dir, cBest, tBest, lBest);
                    cars += WritePerCar(Id8Board.ShopPerCar, s.Arcade.Id8ShopBoardSource, course, dir, cBest, tBest, lBest);
                    boards++;
                }

            _log?.Invoke($"[TF4ALL] Arcade leaderboards: filled {boards} course/direction board(s) and {cars} per-car record(s)");
        }

        /// <summary>The player's own per-car records for one course, straight off the shop board.
        ///
        /// Read once, before we write, and treated exactly like the top-ten snapshot: display only,
        /// never submitted. A record in a save carries no proof of who set it and we write to that
        /// board ourselves.
        ///
        /// The name comes back out of the record, so it is whatever the game had, which for the
        /// player's own rows is their cabinet name rather than their TF4ALL username.</summary>
        private Dictionary<int, Id8LeaderboardEntry> ReadShopCarRecords(int courseId, int direction)
        {
            var found = new Dictionary<int, Id8LeaderboardEntry>();
            foreach (int carId in Id8CarTable.CarIdsInSlotOrder())
            {
                int page = Id8CarTable.SlotForCarId(carId);
                if (page < 0) continue;
                if (!_writer.TryReadRecord(Id8Board.ShopPerCar, courseId, direction, page,
                                           out Id8LeaderboardRecord rec))
                    continue;
                if (rec.IsFiller) continue;

                found[carId] = new Id8LeaderboardEntry
                {
                    Username = Id8Name.Decode(rec.RawName),
                    GoalMs = rec.GoalMs,
                    Section1 = rec.Section1,
                    Section2 = rec.Section2,
                    Section3 = rec.Section3,
                    UnixTime = rec.UnixTime,
                };
            }
            return found;
        }

        /// <summary>Fold one pool into the running per-car bests, keeping the faster time.</summary>
        private static void Absorb(Dictionary<int, Id8LeaderboardEntry> into,
                                   Dictionary<int, Id8LeaderboardEntry> from)
        {
            if (from == null) return;
            foreach (var kv in from)
            {
                if (kv.Value == null || kv.Value.GoalMs <= 0) continue;
                Id8LeaderboardEntry held;
                if (into.TryGetValue(kv.Key, out held) && held.GoalMs <= kv.Value.GoalMs) continue;
                into[kv.Key] = kv.Value;
            }
        }

        private static Dictionary<int, Id8LeaderboardEntry> TeknoParrotCarBests(
            Dictionary<string, LeaderboardBoard> boards, int courseId, int direction)
        {
            if (boards == null) return null;
            foreach (LeaderboardBoard b in boards.Values)
                if (TeknoParrotLeaderboard.CourseId(b.Course) == courseId &&
                    TeknoParrotLeaderboard.DirectionIndex(b.Direction) == direction)
                    return TeknoParrotLeaderboard.ToId8CarBests(b);
            return null;
        }

        /// <summary>Write each car's best onto its own page. Returns how many landed.
        ///
        /// The page is the car's SLOT, not its CarID and not a running index: a CarID is
        /// (maker &lt;&lt; 8) | member and the slot is that flattened, measured on the cabinet. Writing
        /// these sequentially would put every time on the wrong car, and it would look completely
        /// normal on screen.
        ///
        /// Existing rows are left alone unless we have something faster, so a car the community has
        /// no time for keeps whatever the game had.</summary>
        private int WritePerCar(Id8Board board, Id8BoardSource source, int courseId, int direction,
                                Dictionary<int, Id8LeaderboardEntry> community,
                                Dictionary<int, Id8LeaderboardEntry> teknoParrot,
                                Dictionary<int, Id8LeaderboardEntry> local)
        {
            // Pick the pools this source draws on, then take the faster of the two per car. Merged
            // means merged here as much as on the top-ten boards.
            var bests = new Dictionary<int, Id8LeaderboardEntry>();
            if (source == Id8BoardSource.Community || source == Id8BoardSource.Merged)
                Absorb(bests, community);
            if (source == Id8BoardSource.TeknoParrot || source == Id8BoardSource.Merged)
                Absorb(bests, teknoParrot);
            if (Id8Leaderboard.KeepsLocalRecords(source))
                Absorb(bests, local);
            if (bests.Count == 0) return 0;

            int written = 0;
            foreach (var kv in bests)
            {
                int slot = Id8CarTable.SlotForCarId(kv.Key);
                if (slot < 0) continue;              // a car the table does not know

                Id8LeaderboardEntry e = kv.Value;
                string name = Id8Name.Sanitize(e.Username);
                byte[] enc = Id8Name.Encode(name);
                if (enc == null || e.GoalMs <= 0) continue;

                // Never replace a real record with a slower one. On the shop board that row may be
                // the player's own, and demoting somebody's genuine best to show a community time
                // would be the wrong trade.
                if (_writer.TryReadRecord(board, courseId, direction, slot, out Id8LeaderboardRecord had)
                    && !had.IsFiller && had.GoalMs <= e.GoalMs)
                    continue;

                var row = new Id8LeaderboardRecord
                {
                    RawName = enc,
                    Reserved = new byte[] { 0, 0, Id8LeaderboardRecord.ConstantAt16 },
                    Flags = Id8LeaderboardRecord.FlagReal,
                    UnixTime = e.UnixTime,
                    Section1 = e.Section1,
                    Section2 = e.Section2,
                    Section3 = e.Section3,
                    GoalMs = e.GoalMs,
                };
                if (_writer.WriteRecord(board, courseId, direction, slot, row)) written++;
            }
            return written;
        }

        private void Write(Id8Board board, Id8BoardSource source, int courseId, int direction,
                           IReadOnlyList<Id8LeaderboardEntry> community,
                           IReadOnlyList<Id8LeaderboardEntry> tekno,
                           IReadOnlyList<Id8LeaderboardRecord> local)
        {
            var existing = _writer.ReadBoard(board, courseId, direction);
            var rows = Id8Leaderboard.BuildBoard(source, existing, community, tekno, local);
            int written = _writer.WriteBoard(board, courseId, direction, rows);
            _log?.Invoke($"[TF4ALL] Arcade {board} {source}: wrote {written}/{rows.Length} rows " +
                         $"for course {courseId} dir {direction}");
        }

        // Both gated on CommunityEnabled, the master "Enable community features (online)" switch.
        // PRIVACY.md promises that switch stops every online feature, and these two are the only
        // places this class reaches the network for board data: our backend and teknoparrot.com.
        // Without the gate the boards kept fetching with community features off, which is the one
        // promise a privacy switch cannot break.
        //
        // Local still fills with it off, and correctly so: "My own times" is read out of the game's
        // own save and sends nothing.

        private static bool NeedsCommunity(TrueforceSettings s) =>
            s.CommunityEnabled &&
            (Uses(s.Arcade.Id8OnlineBoardSource, Id8BoardSource.Community) ||
             Uses(s.Arcade.Id8ShopBoardSource, Id8BoardSource.Community));

        private static bool NeedsTeknoParrot(TrueforceSettings s) =>
            s.CommunityEnabled &&
            (Uses(s.Arcade.Id8OnlineBoardSource, Id8BoardSource.TeknoParrot) ||
             Uses(s.Arcade.Id8ShopBoardSource, Id8BoardSource.TeknoParrot));

        private static bool Uses(Id8BoardSource source, Id8BoardSource pool) =>
            source == pool || source == Id8BoardSource.Merged;

        /// <summary>Whether a board's chosen source has anything to write with.
        ///
        /// A source whose pool never arrived must leave the board ALONE rather than write an empty
        /// one. Community and TeknoParrot build with keepExisting false, so Merge over a null pool
        /// returns nothing but filler: a failed fetch, or community features being switched off,
        /// would wipe the board the player was reading and replace it with rows of TF4ALL. Merged
        /// and Local keep what is there and are safe either way, but they go through the same test
        /// so there is one rule rather than two.</summary>
        private static bool HaveDataFor(Id8BoardSource source, bool haveCommunity, bool haveTekno)
        {
            switch (source)
            {
                case Id8BoardSource.Community:   return haveCommunity;
                case Id8BoardSource.TeknoParrot: return haveTekno;
                case Id8BoardSource.Merged:      return haveCommunity || haveTekno;
                default:                         return true;   // Local: the snapshot is always there.
            }
        }

        /// <summary>The parsed site boards, fetched or from cache. Taken once per fill rather
        /// than per board: the cache read is cheap but the log line is not, and 32 identical
        /// "using a cache from 3 h ago" lines is noise.</summary>
        private Dictionary<string, LeaderboardBoard> TeknoParrotBoards()
        {
            var boards = _tpCache.GetBoards(TeknoParrotGameId, out TeknoParrotCacheResult r, out string detail);
            _log?.Invoke($"[TF4ALL] TeknoParrot leaderboard: {r}, {detail}");
            return boards;
        }

        private static List<Id8LeaderboardEntry> TeknoParrotRowsFrom(
            Dictionary<string, LeaderboardBoard> boards, int courseId, int direction)
        {
            if (boards == null) return null;
            foreach (LeaderboardBoard b in boards.Values)
                if (TeknoParrotLeaderboard.CourseId(b.Course) == courseId &&
                    TeknoParrotLeaderboard.DirectionIndex(b.Direction) == direction)
                    return TeknoParrotLeaderboard.ToId8Entries(b);
            return null;
        }

        /// <summary>Send a finished run. The server decides whether it is an improvement.</summary>
        public async Task SubmitAsync(Id8FinishedRun run, CancellationToken ct)
        {
            var s = _settings();
            if (run == null || s?.Arcade == null) return;
            if (!s.CommunityEnabled) return;   // The master switch outranks the per-feature one.
            if (!s.Arcade.Id8SubmitTimesEnabled) return;

            await _client.SubmitAsync(GameKey, run.CourseId, run.Direction, run.CarId, run.GoalMs,
                                      run.Sections, null,
                                      PluginVersion(), ct).ConfigureAwait(false);
        }

        /// <summary>Put the shop board back exactly as it was found. For the toggle going off.</summary>
        public void RestoreShopBoards()
        {
            if (_writer == null) return;
            foreach (var kv in _preWriteShop)
            {
                int course = kv.Key / 2, dir = kv.Key % 2;
                _writer.WriteBoard(Id8Board.ShopTopTen, course, dir, kv.Value);
            }
            _log?.Invoke($"[TF4ALL] Arcade: restored {_preWriteShop.Count} shop board(s)");
        }

        private static string PluginVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        private void SaveBackup(int slot, Id8LeaderboardRecord[] rows)
        {
            try
            {
                string dir = Path.GetDirectoryName(_backupPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var all = File.Exists(_backupPath)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Id8LeaderboardRecord[]>>(
                          File.ReadAllText(_backupPath)) ?? new Dictionary<string, Id8LeaderboardRecord[]>()
                    : new Dictionary<string, Id8LeaderboardRecord[]>();

                // Only ever record a slot once. A later snapshot could contain our own writes, and
                // overwriting the backup with those would lose the player's real records for good.
                string key = slot.ToString();
                if (all.ContainsKey(key)) return;

                all[key] = rows;
                File.WriteAllText(_backupPath, Newtonsoft.Json.JsonConvert.SerializeObject(all));
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade board backup failed: " + ex.Message);
            }
        }

        public void Dispose() => Detach();
    }
}
