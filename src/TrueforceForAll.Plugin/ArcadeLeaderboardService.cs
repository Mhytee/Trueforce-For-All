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

        /// <summary>Where a record we destroy is written down before it goes. Only ever
        /// touched for a row carrying a real card id, which is the player's own data.</summary>
        private readonly string _removedPath;

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
            _removedPath = Path.Combine(dir, "id8-removed-records.json");
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

            ClearOurRows();

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
                    if (HaveDataFor(s.Arcade.Id8OnlineBoardSource, haveCommunity, haveTekno))
                    {
                        cars += WritePerCar(Id8Board.OnlinePerCar, s.Arcade.Id8OnlineBoardSource, course, dir, cBest, tBest, lBest);
                        BrandFiller(Id8Board.OnlinePerCar, course, dir);
                    }
                    if (HaveDataFor(s.Arcade.Id8ShopBoardSource, haveCommunity, haveTekno))
                    {
                        cars += WritePerCar(Id8Board.ShopPerCar, s.Arcade.Id8ShopBoardSource, course, dir, cBest, tBest, lBest);
                        BrandFiller(Id8Board.ShopPerCar, course, dir);
                    }
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
                    CarId = carId,
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
        /// <summary>Put back everything we ever wrote, before writing anything new.
        ///
        /// Our writes persist: the game saves the board, so rows from previous sessions are still
        /// there at startup. Without this, three things go wrong and all of them are invisible.
        /// Probe rows written while working the feature out survive indefinitely on boards nothing
        /// snapshots. A per-car time we wrote once can never be displaced by a slower but current
        /// one. And a source the player has since switched away from leaves its rows behind, so a
        /// board set to Community keeps showing TeknoParrot names forever.
        ///
        /// Only rows carrying our zero player id are touched. The player's own records, which the
        /// cabinet stamps with their card id, are left exactly as they are: this is a reset of our
        /// own output, not of their save.</summary>
        private void ClearOurRows()
        {
            var boards = new[] { Id8Board.OnlineTopTen, Id8Board.ShopTopTen,
                                 Id8Board.OnlinePerCar, Id8Board.ShopPerCar };
            int cleared = 0, removed = 0, kept = 0;
            var keptSample = new List<string>();
            Id8LeaderboardRecord blank = Id8Leaderboard.DefaultRow();

            for (int course = 0; course <= 15; course++)
                for (int dir = 0; dir <= 1; dir++)
                    foreach (Id8Board board in boards)
                    {
                        // Per-car boards are addressed by the car's slot, so walk the car table
                        // rather than a range: those are exactly the slots we could ever have
                        // written, and the writer refuses anything outside the table anyway.
                        bool perCar = board == Id8Board.OnlinePerCar || board == Id8Board.ShopPerCar;
                        foreach (int slot in perCar ? CarSlots() : RankSlots())
                        {
                            if (!_writer.TryReadRecord(board, course, dir, slot, out Id8LeaderboardRecord had))
                                continue;
                            if (had.IsFiller) continue;

                            bool ours = Id8Leaderboard.IsOurs(had);
                            bool cannotBeALap = !Id8Leaderboard.IsPlausibleLap(had.GoalMs);

                            // A row carrying somebody's card id and a believable time is theirs.
                            // Leave it alone; that is the whole contract.
                            if (!ours && !cannotBeALap)
                            {
                                // Anything left standing carries a card id and a believable time,
                                // so it is somebody's record. Counted, with a few named, because
                                // "the probe rows are still there" and "cleared 0" together mean
                                // those rows are not ours by the player-id test and we need to see
                                // one rather than guess again.
                                kept++;
                                if (keptSample.Count < 6)
                                    keptSample.Add($"{Id8Name.Decode(had.RawName)} {had.GoalMs}ms " +
                                                   $"pid={had.PlayerId} car={had.CarId} on {board} c{course}d{dir}s{slot}");
                                continue;
                            }

                            // Removing a row with a real card id destroys save data, so write down
                            // exactly what it was first. Ours we simply overwrite: it was never
                            // theirs to lose.
                            if (!ours)
                            {
                                RecordRemoval(board, course, dir, slot, had);
                                removed++;
                            }

                            if (_writer.WriteRecord(board, course, dir, slot, blank) && ours) cleared++;
                        }
                    }

            _log?.Invoke($"[TF4ALL] Arcade: swept the boards, cleared {cleared} of ours, " +
                         $"removed {removed} unusable, left {kept} real record(s) alone");
            if (keptSample.Count > 0)
                _log?.Invoke("[TF4ALL] Arcade: records left in place: " + string.Join(" | ", keptSample));
            if (removed > 0)
                _log?.Invoke($"[TF4ALL] Arcade: removed {removed} stored record(s) that cannot be a lap " +
                             $"(under {Id8Leaderboard.MinPlausibleMs / 1000}s). Originals saved to {_removedPath}");
        }

        /// <summary>Append a record we are about to destroy to a file that is never overwritten.
        ///
        /// This only ever runs for a row carrying a real card id, which is to say the player's own
        /// save data. The shop top-ten backup does not cover it: that file holds one board, and an
        /// unusable value can sit on any of the four. Keyed so the same slot is only recorded once,
        /// for the same reason SaveBackup is: a second pass would otherwise overwrite the original
        /// with whatever we had already put there.</summary>
        private void RecordRemoval(Id8Board board, int courseId, int direction, int slot,
                                   Id8LeaderboardRecord row)
        {
            try
            {
                string dir = Path.GetDirectoryName(_removedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var all = File.Exists(_removedPath)
                    ? Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Id8LeaderboardRecord>>(
                          File.ReadAllText(_removedPath)) ?? new Dictionary<string, Id8LeaderboardRecord>()
                    : new Dictionary<string, Id8LeaderboardRecord>();

                string key = $"{board}:{courseId}:{direction}:{slot}";
                if (all.ContainsKey(key)) return;

                all[key] = row;
                File.WriteAllText(_removedPath, Newtonsoft.Json.JsonConvert.SerializeObject(all));
            }
            catch (Exception ex)
            {
                _log?.Invoke("[TF4ALL] Arcade: could not record the removal: " + ex.Message);
            }
        }

        private static IEnumerable<int> RankSlots()
        {
            for (int i = 0; i < Id8Leaderboard.Ranks; i++) yield return i;
        }

        private static IEnumerable<int> CarSlots()
        {
            foreach (int carId in Id8CarTable.CarIdsInSlotOrder())
            {
                int slot = Id8CarTable.SlotForCarId(carId);
                if (slot >= 0) yield return slot;
            }
        }

        /// <summary>Replace the game's placeholder with ours on a per-car board we are filling.
        ///
        /// The top-ten boards get our filler for free, because the merge rebuilds all ten rows. A
        /// per-car board is written slot by slot, so a car nobody has a time for keeps SEGA's row,
        /// and the same board ends up showing two different placeholders depending on whether that
        /// particular car happened to be in the data. With TeknoParrot covering about 634 of the
        /// 1600 slots, that is most of them.
        ///
        /// Only filler is touched, so a real record can never be lost here, and only on a board we
        /// are actually filling: branding a board we then leave empty would claim credit for a
        /// wipe.</summary>
        private void BrandFiller(Id8Board board, int courseId, int direction)
        {
            Id8LeaderboardRecord blank = Id8Leaderboard.DefaultRow();
            foreach (int slot in CarSlots())
            {
                if (!_writer.TryReadRecord(board, courseId, direction, slot, out Id8LeaderboardRecord had))
                    continue;
                if (!had.IsFiller) continue;
                if (Id8Name.Decode(had.RawName) == Id8Leaderboard.FillerName) continue;   // already ours
                _writer.WriteRecord(board, courseId, direction, slot, blank);
            }
        }

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

                // Never replace THE PLAYER'S real record with a slower one: demoting somebody's
                // genuine best to show a community time would be the wrong trade.
                //
                // A row we wrote ourselves gets no such protection, and that distinction is the
                // whole point. Our writes persist in the save, so without it a community time we
                // put here once could never be displaced by a later, slower, but current one, and
                // the per-car boards would silently freeze at whatever the fastest thing we ever
                // saw was, long after it left the source.
                if (_writer.TryReadRecord(board, courseId, direction, slot, out Id8LeaderboardRecord had)
                    && !had.IsFiller && !Id8Leaderboard.IsOurs(had) && had.GoalMs <= e.GoalMs)
                    continue;

                var row = new Id8LeaderboardRecord
                {
                    RawName = enc,
                    // kv.Key IS the car, so this board never has to guess.
                    Reserved = Id8LeaderboardRecord.ReservedFor(kv.Key),
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
