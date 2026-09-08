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

        /// <summary>Every board as RAW BYTES, exactly as it was found, keyed (board, slot).
        ///
        /// This is what makes the four sources safe to try. A user has to be able to set Everyone,
        /// look, set TeknoParrot, look, set their own times, then switch the whole thing off and be
        /// left with the save they started with. Nothing less than the original bytes can promise
        /// that: the decoded snapshots alongside this one hold the player's real rows for merging
        /// and deliberately drop filler, so rebuilding a board from them would return an
        /// approximation, and any byte of the 48 we do not model would be lost.
        ///
        /// Taken AFTER the sweep, which is what makes it correct on the second and later runs. The
        /// shop boards persist into the save, so on relaunch they still carry what we wrote last
        /// time; sweeping our own rows off first means what gets snapshotted is the game's state,
        /// not ours.
        ///
        /// All four boards, not just the two that persist. The online pair is rebuilt from the exe
        /// each launch, so leaving our rows there costs nothing permanent, but switching the
        /// feature off should undo it on screen straight away rather than at the next restart.</summary>
        private readonly Dictionary<long, byte[]> _preWriteBytes = new Dictionary<long, byte[]>();

        private static long RawKey(Id8Board board, int slot) { return ((long)board << 32) | (uint)slot; }

        /// <summary>The four tables, in one place, so a board cannot be missed from a sweep or a
        /// snapshot by being left off a list written out by hand a second time.</summary>
        internal static readonly Id8Board[] AllBoards =
        {
            Id8Board.OnlineTopTen, Id8Board.ShopTopTen, Id8Board.OnlinePerCar, Id8Board.ShopPerCar,
        };

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
            {
                // Deeper when a ladder is being drawn, because the window is cut from the whole
                // field and the player may be nowhere near the front. Ten is right for a board that
                // only ever shows its top ten, and asking for 200 on every fill would multiply the
                // payload by twenty for the boards that never use it.
                int depth = s.Arcade.Id8LadderClimbEnabled
                    ? Id8Leaderboard.LadderDepth : Id8Leaderboard.Ranks;
                community = await _client.GetAllBoardsAsync(GameKey, depth, ct)
                                         .ConfigureAwait(false);
            }

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

                    // The raw safety net, before anything is written to any of the four. Cheap:
                    // 32 slots by 120 pages by 48 bytes is under 200 KB for the whole game, taken
                    // once per board per session.
                    foreach (Id8Board b in AllBoards)
                    {
                        long rk = RawKey(b, slot);
                        if (_preWriteBytes.ContainsKey(rk)) continue;
                        byte[] raw = _writer.SnapshotBoard(b, course, dir);
                        if (raw != null) _preWriteBytes[rk] = raw;
                    }

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

                    // RECOVERY. The on-disk backup was write-only for its whole life: it was
                    // faithfully recorded before the first write of every session and no code ever
                    // read it back, so it could not help the one situation it existed for.
                    //
                    // It matters now because records HAVE been lost. Choosing Community on the shop
                    // board used to rebuild it without the player's rows, and on a board that loads
                    // from the save that deleted them. The owner's file holds 17 records at player
                    // id 5553014 that the live board no longer had.
                    //
                    // Folded in as another source of local records rather than written straight
                    // back, so they take their place on time like everything else, and a genuine
                    // row still on the board wins over a stale copy of itself. SaveBackup only ever
                    // records a slot once, so this cannot be poisoned by a later snapshot that
                    // already contains our own writes.
                    IReadOnlyList<Id8LeaderboardRecord> archived = ArchivedRecordsFor(slot);
                    if (archived.Count > 0)
                    {
                        var both = new List<Id8LeaderboardRecord>(local);
                        both.AddRange(archived);
                        local = both;
                    }

                    // And the shop PER-CAR board, for the same reason: it is the only place the
                    // player's own per-car records exist, and the online per-car board is SEGA
                    // filler on every page. Without this, Merged on the online per-car board would
                    // leave the player off their own boards, exactly as it did on the top ten
                    // before the snapshot was passed through.
                    if (!_preWriteShopCars.ContainsKey(slot))
                    {
                        // Two snapshots of the same board because they answer different questions.
                        // The decoded one is the player's records, for merging. The raw bytes are
                        // for putting the board back byte for byte when the feature is switched
                        // off, which decoded records cannot do: they carry no filler rows and no
                        // trailing bytes, so restoring from them would rebuild an approximation.
                        _preWriteShopCars[slot] = ReadShopCarRecords(course, dir);
                    }
                    // Re-read, every fill, not just the first. The snapshot is what the board
                    // looked like before we touched it and must stay that, but as the ONLY source
                    // of the player's per-car records it also froze them at attach: set a record
                    // mid-session and it could never appear, because the one place we looked was
                    // taken before it existed. That is why a lap could be driven, stored by the
                    // game, and still be missing from the board.
                    //
                    // Safe to read a board we have written to because IsOurs settles it: the game
                    // stamps a card id on a record it sets and every row we write carries zero, so
                    // ReadShopCarRecords keeps theirs and drops ours. Without that filter this
                    // would feed last session's community rows back in as the player's own.
                    var lBest = new Dictionary<int, Id8LeaderboardEntry>(_preWriteShopCars[slot]);
                    Absorb(lBest, ReadShopCarRecords(course, dir));

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
                        cars += WritePerCarRanked(Id8Board.OnlinePerCar, s.Arcade.Id8OnlineBoardSource, course, dir, cBest, tBest, lBest);
                    }
                    if (HaveDataFor(s.Arcade.Id8ShopBoardSource, haveCommunity, haveTekno))
                    {
                        cars += WritePerCarRanked(Id8Board.ShopPerCar, s.Arcade.Id8ShopBoardSource, course, dir, cBest, tBest, lBest);
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

            // THE RECORD SAYS WHICH CAR IT IS. The page does not, and has not since the board
            // started being written in ranked order: a genuine AE86 time that placed twenty-sixth
            // sits on page 25, and taking the car from the page would read it back as an RX-7.
            // That misattribution then rides through the merge and gets written to the RX-7's
            // ranking position, so the player's own record is not merely displayed wrong, it is
            // relabelled in the save. Reading the car id out of the record, which is where the
            // game keeps it, is correct under either layout.
            foreach (int page in CarSlots())          // every page 0..49, once
            {
                if (!_writer.TryReadRecord(Id8Board.ShopPerCar, courseId, direction, page,
                                           out Id8LeaderboardRecord rec))
                    continue;
                if (rec.IsFiller) continue;
                if (Id8Leaderboard.IsOurs(rec)) continue;   // our own write, not a record they set

                int carId = rec.CarId;
                if (Id8CarTable.Find(carId) == null) continue;   // not a car we can place

                // Two pages claiming the same car should not happen, but a half-written board is
                // not worth losing a record over: keep the faster, which is the same rule the
                // merge uses everywhere else.
                if (found.TryGetValue(carId, out Id8LeaderboardEntry held)
                    && held != null && held.GoalMs <= rec.GoalMs)
                    continue;

                found[carId] = new Id8LeaderboardEntry
                {
                    CarId = carId,
                    Username = Id8Name.Decode(rec.RawName),
                    GoalMs = rec.GoalMs,
                    Section1 = rec.Section1,
                    Section2 = rec.Section2,
                    Section3 = rec.Section3,
                    UnixTime = rec.UnixTime,
                    // These rows are the player's own by definition, so the card id has to survive
                    // the trip. Dropping it here would hand them back as ours on the next write and
                    // the following sweep would delete them.
                    PlayerId = rec.PlayerId,
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
            var boards = AllBoards;
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
                            bool probe = IsProbeMarker(Id8Name.Decode(had.RawName));

                            // A row carrying somebody's card id and a believable time is theirs.
                            // Leave it alone; that is the whole contract.
                            if (!ours && !cannotBeALap && !probe)
                            {
                                // Anything left standing carries a card id and a believable time,
                                // so it is somebody's record. Counted, with a few named, because
                                // "the probe rows are still there" and "cleared 0" together mean
                                // those rows are not ours by the player-id test and we need to see
                                // one rather than guess again.
                                kept++;
                                if (keptSample.Count < 64)
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
                foreach (string line in keptSample)
                    _log?.Invoke("[TF4ALL] Arcade: kept " + line);
            if (removed > 0)
                _log?.Invoke($"[TF4ALL] Arcade: removed {removed} stored record(s) that are probe markers " +
                             $"or cannot be a lap " +
                             $"(under {Id8Leaderboard.MinPlausibleMs / 1000}s). Originals saved to {_removedPath}");
        }

        /// <summary>Rows written by the probe that worked out which table was which.
        ///
        /// They cannot be recognised the way our normal writes can. The probe stamped them with
        /// the cabinet's own card id, so the player-id test sees them as genuine, and it chose
        /// times like 61000 that sit just above any sane floor. The only thing left that separates
        /// them from a real record is the name, which the probe generated to a fixed shape:
        /// ANYA-01, ANYB-01, PERA-01, PERB-01, being the any-car and per-car boards A and B.
        ///
        /// Matching on a name is a blunt instrument and it is used here precisely because nothing
        /// sharper exists. The shape is narrow enough that a real handle will not collide with it,
        /// and anything it does remove is written to the removals file first. Names the player has
        /// actually used, SPURGE among them, do not match and are left alone.</summary>
        private static bool IsProbeMarker(string decodedName)
        {
            if (string.IsNullOrEmpty(decodedName)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                decodedName.Trim(), @"^(ANY|PER)[A-Z]-[0-9]+$");
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

        /// <summary>Write a per-car board as a RANKING: fastest car first.
        ///
        /// The page is only a position, not a car. That was established the hard way: while the
        /// car id was being written as zero, every page displayed an AE86 Trueno, which cannot
        /// happen if the page decides the car. So the order is ours, and a board whose whole
        /// purpose is "the best time in each car" is far more use sorted by time than left in the
        /// game's internal car order.
        ///
        /// EVERY existing real record is folded in first, whatever the source setting says. This
        /// board is written slot by slot no longer: it is rebuilt end to end, so a record left out
        /// of the set is a record destroyed. The player's own times are not ours to drop because
        /// they picked Community on this board.
        ///
        /// One row per car, keeping the fastest, then ranked, then the remaining pages filled with
        /// our placeholder so the board reads consistently rather than trailing off into SEGA.</summary>
        private int WritePerCarRanked(Id8Board board, Id8BoardSource source, int courseId, int direction,
                                      Dictionary<int, Id8LeaderboardEntry> community,
                                      Dictionary<int, Id8LeaderboardEntry> teknoParrot,
                                      Dictionary<int, Id8LeaderboardEntry> local)
        {
            var bests = new Dictionary<int, Id8LeaderboardEntry>();

            // Whatever is already on the board and carries a card id is somebody's record. It goes
            // in before anything else so the pools can only beat it, never erase it.
            foreach (int slot in CarSlots())
            {
                if (!_writer.TryReadRecord(board, courseId, direction, slot, out Id8LeaderboardRecord had))
                    continue;
                if (had.IsFiller || Id8Leaderboard.IsOurs(had)) continue;
                if (!Id8Leaderboard.IsPlausibleLap(had.GoalMs)) continue;
                Absorb(bests, new Dictionary<int, Id8LeaderboardEntry>
                {
                    [had.CarId] = new Id8LeaderboardEntry
                    {
                        Username = Id8Name.Decode(had.RawName),
                        CarId = had.CarId,
                        GoalMs = had.GoalMs,
                        Section1 = had.Section1,
                        Section2 = had.Section2,
                        Section3 = had.Section3,
                        UnixTime = had.UnixTime,
                        // Carried, or writing this row back would re-stamp it as ours and the next
                        // sweep would delete somebody's real record as if we had put it there.
                        PlayerId = had.PlayerId,
                    },
                });
            }

            if (Id8Leaderboard.UsesCommunity(source)) Absorb(bests, community);
            if (Id8Leaderboard.UsesTeknoParrot(source)) Absorb(bests, teknoParrot);
            if (Id8Leaderboard.KeepsLocalRecords(source))
                Absorb(bests, local);

            var ranked = new List<Id8LeaderboardEntry>();
            foreach (var kv in bests)
            {
                Id8LeaderboardEntry e = kv.Value;
                if (e == null || !Id8Leaderboard.IsPlausibleLap(e.GoalMs)) continue;
                if (Id8Name.Encode(Id8Name.Sanitize(e.Username)) == null) continue;
                if (e.CarId <= 0 && kv.Key > 0) e.CarId = kv.Key;   // the key is the car
                ranked.Add(e);
            }
            ranked.Sort((x, y) => x.GoalMs.CompareTo(y.GoalMs));

            // FASTEST FIRST, down a screen whose row order is fixed. The page a record sits on
            // decides where it is drawn and the record's own car id decides what car name is
            // printed on it, so the two are independent and the board can be ranked: put the
            // fastest time on the page the screen draws first. That page is not page 0 in general.
            // Id8CarTable.DisplayOrder is the measured sequence and carries the evidence.
            //
            // This is what made the earlier attempt look half sorted. Writing rank i to page i put
            // the eleven fastest times on pages 0 to 10, which the screen draws first as a block,
            // so the buckets came out in time order while their contents were shuffled.
            // RANKED, ON BOTH PER-CAR BOARDS, AND THIS TIME IT IS MEASURED.
            //
            // Ranking puts a row on the page the screen draws in that position, so a car stops
            // sitting on its own page. That is fine for a screen that reads a page in order to draw
            // it, and would NOT be fine for anything that looks the table up BY CAR. Something does
            // look it up that way: the game computes a per-car page from a CarID at 0x00a01910 and
            // calls it while building the race HUD. So the question was whether the in-race target
            // time comes from there, because a target set in somebody else's car is not a cosmetic
            // slip: Time Attack consists of chasing that number.
            //
            // MEASURED ON THE CABINET AND THE ANSWER IS NO. With a distinct time written onto all
            // fifty pages of the shop per-car board, the game ignored every one of them. What the
            // menu and the in-race HUD both show is the store ANY-CAR top ten, [obj+0xb0], which is
            // the board the shop source fills and the one this feature exists to populate: the
            // player's own best against the store's best, with the store's best as the target.
            //
            // So the per-car boards are a browsing screen, and a browsing screen is far more use
            // ordered by time than left in the game's internal car order.
            //
            // The one cost, named rather than hidden: any other screen that shows a best time
            // beside a car, a car selector being the obvious candidate, would draw a time that
            // belongs to a different car. That is misleading where the target time would have been
            // harmful, and it is the trade this line represents. Flip it to
            // this to name OnlinePerCar alone to go back to the game's layout on the saved board.
            //
            // Note what this is NOT protecting against, so nobody reads it as cover: if the game
            // gates storing a per-car record on what is already on that page, filling the board
            // with faster times could suppress the player's own record. That risk is identical
            // whether or not we sort, so it is not a reason to leave the board unsorted. Untested.
            bool rankThisBoard = board == Id8Board.OnlinePerCar || board == Id8Board.ShopPerCar;

            int written = 0;
            int pos = 0;
            var claimed = new HashSet<int>();
            foreach (Id8LeaderboardEntry e in ranked)
            {
                if (Id8CarTable.Find(e.CarId) == null) continue;   // a car the table does not know

                int page;
                if (rankThisBoard)
                {
                    if (pos >= Id8CarTable.DisplayOrder.Length) break;
                    page = Id8CarTable.DisplayOrder[pos];
                }
                else
                {
                    page = Id8CarTable.SlotForCarId(e.CarId);
                    if (page < 0) continue;
                }

                var row = new Id8LeaderboardRecord
                {
                    RawName = Id8Name.Encode(Id8Name.Sanitize(e.Username)),
                    Reserved = Id8LeaderboardRecord.ReservedFor(e.CarId),
                    Flags = Id8LeaderboardRecord.FlagReal,
                    // 0 for anything out of the pools, which is our signature. A record folded in
                    // off the board keeps the card id it arrived with, so it stays theirs.
                    PlayerId = e.PlayerId,
                    UnixTime = e.UnixTime,
                    Section1 = e.Section1,
                    Section2 = e.Section2,
                    Section3 = e.Section3,
                    GoalMs = e.GoalMs,
                };
                if (_writer.WriteRecord(board, courseId, direction, page, row)) written++;
                claimed.Add(e.CarId);
                pos++;
            }

            // Then the cars nobody has a time on, each naming itself, so a board reads as the times
            // it has and then the rest of the roster rather than trailing off into SEGA. Every one
            // of the 50 appears exactly once either way: ranked rows are one per car.
            foreach (int carId in Id8CarTable.CarIdsInSlotOrder())
            {
                if (claimed.Contains(carId)) continue;

                int page;
                if (rankThisBoard)
                {
                    if (pos >= Id8CarTable.DisplayOrder.Length) break;
                    page = Id8CarTable.DisplayOrder[pos];
                }
                else
                {
                    page = Id8CarTable.SlotForCarId(carId);
                    if (page < 0) continue;
                }

                Id8LeaderboardRecord blank = Id8Leaderboard.DefaultRow();
                blank.Reserved = Id8LeaderboardRecord.ReservedFor(carId);
                _writer.WriteRecord(board, courseId, direction, page, blank);
                pos++;
            }
            return written;
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
            if (Id8Leaderboard.UsesCommunity(source)) Absorb(bests, community);
            if (Id8Leaderboard.UsesTeknoParrot(source)) Absorb(bests, teknoParrot);
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
            // LADDER CLIMB. Centre the board on the player rather than on the world records.
            //
            // The name has to be the one that appears in the POOL, and that is the cabinet name off
            // their own records, not their tf4all username: a TeknoParrot row is filed under
            // whatever the cabinet called them. Their own rows are folded into the same ranked set
            // before the window is cut, so matching on that name finds them wherever they sit.
            //
            // Null when the feature is off, when the source has no field to climb, or when they
            // have no record here yet, and BuildBoard falls back to the ordinary top ten. That last
            // case is deliberate: a course you have never driven has nothing to climb from, so the
            // fastest times are the right thing to show.
            string ladderFor = null;
            if (_settings()?.Arcade?.Id8LadderClimbEnabled == true && Id8Leaderboard.SupportsLadder(source))
                foreach (Id8LeaderboardRecord r in local ?? (IReadOnlyList<Id8LeaderboardRecord>)new Id8LeaderboardRecord[0])
                {
                    if (r.IsFiller) continue;
                    string n = Id8Name.Decode(r.RawName);
                    if (!string.IsNullOrEmpty(n)) { ladderFor = n; break; }
                }

            var rows = Id8Leaderboard.BuildBoard(source, existing, community, tekno, local, ladderFor);
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
            pool == Id8BoardSource.Community
                ? Id8Leaderboard.UsesCommunity(source)
                : Id8Leaderboard.UsesTeknoParrot(source);

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

            // NOTHING GOES OUT UNDER SOMEBODY'S NAME BEFORE THEY HAVE READ THE NOTICE.
            //
            // Submission is on by default, and the notice that discloses it can only appear while
            // the settings panel is open AND an arcade game is the active game. ID8 runs full
            // screen through TeknoParrot, so a player can drive for a whole session, publish their
            // username to the community board and to the weekly Discord post, and never once have
            // been shown the sentence that says so.
            //
            // Gating here rather than trying harder to show the dialog, because this is the side
            // that has to fail closed: a notice that is hard to surface is a UI problem, but a name
            // published without it is not recoverable. The run is simply not sent, and the next
            // time the panel is opened with a cabinet running the notice appears and submission
            // starts. Ticking the box by hand also counts as the disclosure being made.
            if (!s.Arcade.Id8SubmitNoticeShown)
            {
                _log?.Invoke("[TF4ALL] Arcade: run not submitted, the submission notice has not " +
                             "been shown yet. Open the settings panel while the cabinet is running.");
                return;
            }

            await _client.SubmitAsync(GameKey, run.CourseId, run.Direction, run.CarId, run.GoalMs,
                                      run.Sections, null,
                                      PluginVersion(), ct).ConfigureAwait(false);
        }

        /// <summary>Put the shop boards back exactly as they were found. For the toggle going off.
        ///
        /// BOTH shop boards. Restoring only the top ten left the per-car board carrying our rows
        /// after the user had switched the feature off, on the one board that persists to the save,
        /// which made "off" mean "stop writing" rather than "undo". The snapshot needed to fix it
        /// was already being taken; nothing was reading it.</summary>
        public void RestoreShopBoards()
        {
            if (_writer == null) return;

            int done = 0, failed = 0;
            foreach (var kv in _preWriteBytes)
            {
                var board = (Id8Board)(kv.Key >> 32);
                int slot = (int)(kv.Key & 0xffffffffL);
                if (_writer.RestoreBoard(board, slot / 2, slot % 2, kv.Value)) done++;
                else failed++;
            }

            // The snapshots stay. Restoring is not "we are finished with the game", it is the
            // toggle going off, and the toggle can come back on in the same session. Retaking them
            // from a board we have written to is exactly the mistake the snapshot exists to avoid.
            _log?.Invoke($"[TF4ALL] Arcade: restored {done} board(s) to how the game had them"
                         + (failed > 0 ? $", {failed} could not be written" : ""));
        }

        private static string PluginVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        /// <summary>The player's genuine records for one board out of the on-disk backup, or none.
        ///
        /// Read once and cached, because the file does not change while we are running: SaveBackup
        /// writes a slot the first time it is seen and never again.</summary>
        private IReadOnlyList<Id8LeaderboardRecord> ArchivedRecordsFor(int slot)
        {
            if (_archive == null)
            {
                _archive = new Dictionary<int, Id8LeaderboardRecord[]>();
                try
                {
                    if (File.Exists(_backupPath))
                    {
                        var all = Newtonsoft.Json.JsonConvert
                            .DeserializeObject<Dictionary<string, Id8LeaderboardRecord[]>>(
                                File.ReadAllText(_backupPath));
                        if (all != null)
                            foreach (var kv in all)
                                if (int.TryParse(kv.Key, out int k) && kv.Value != null)
                                    _archive[k] = kv.Value;
                    }
                }
                catch (Exception ex)
                {
                    // A backup we cannot read is not a reason to fail a fill. It only costs the
                    // recovery, and the live board is still the primary source.
                    _log?.Invoke("[TF4ALL] Arcade board backup could not be read: " + ex.Message);
                }
            }

            return _archive.TryGetValue(slot, out Id8LeaderboardRecord[] rows)
                ? Id8Leaderboard.LocalRecordsFrom(rows)
                : (IReadOnlyList<Id8LeaderboardRecord>)new Id8LeaderboardRecord[0];
        }

        private Dictionary<int, Id8LeaderboardRecord[]> _archive;

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
