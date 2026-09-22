// Writes Initial D 8's in-game leaderboard tables.
//
// This is the ONE place in the plugin that writes to a game's memory, and it is deliberately
// separate from Id8MemoryTelemetry, which declares itself strictly read only and stays that way.
// Nothing here injects code, installs a hook or patches an instruction: it opens the process with
// PROCESS_VM_WRITE and calls WriteProcessMemory on four plain data arrays. What it writes are
// leaderboard rows, which the game's own display code reads on the next screen build.
//
// Why writing is safe to attempt at all: SEGA's ALL.Net and the per-shop server tower that used to
// fill these arrays are long dead, so every row a player sees is the filler the game shipped with
// (name SEGA, six minutes flat). Confirmed on the cabinet 2026-09-06: a marker written here shows
// up on the in-game leaderboard.
//
// The four tables hang off one object, at consecutive offsets. Which one a lookup uses is picked by
// the game's own argument: 0x00a01640 takes the any-car pair and 0x00a01910 the per-car pair, and
// in each the scope argument selects online or shop. See Id8Board.
//
// Bounds: every write is checked against the table's own extent before it happens. A record index
// is derived from the course and direction exactly as the game derives it, and a write that would
// land past the end of its table is refused rather than clamped, because a clamp would silently
// corrupt a neighbouring table.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrueforceForAll.Core
{
    /// <summary>Read/write access to the running game's leaderboard arrays.</summary>
    public sealed class Id8LeaderboardWriter : IDisposable
    {
        // Static roots. The exe has its relocations stripped and a fixed image base, so an address
        // recovered by analysing the file is valid in memory on every launch.
        private const uint SystemPtrVa = 0x013bb860;
        private const uint ExpectedImageBase = 0x00400000;
        private const int SystemToObj = 0x28;
        private const int ObjToCount = 0xa0;

        /// <summary>Pages on a top-ten board: the ten ranks.</summary>
        public const int TopTenPages = 10;

        /// <summary>Pages on a per-car board: one per car, and the game ships 50.</summary>
        public const int PerCarPages = 50;

        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessQueryInformation = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr wrote);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        private IntPtr _h;
        private readonly byte[] _word = new byte[4];

        /// <summary>Course-and-direction slots per page. Read from the game rather than assumed,
        /// because it is the multiplier the game's own address arithmetic uses.</summary>
        public int CourseSlots { get; private set; }

        private readonly Dictionary<Id8Board, uint> _bases = new Dictionary<Id8Board, uint>();

        private Id8LeaderboardWriter(IntPtr h) { _h = h; }

        /// <summary>Open the running game for reading and writing. Returns null with a reason when
        /// the game is not running, is not the build the map was built for, or refuses access.
        /// </summary>
        public static Id8LeaderboardWriter Open(Process game, out string error)
        {
            error = null;
            if (game == null) { error = "the game is not running"; return null; }

            uint baseAddr;
            try
            {
                ProcessModule m = game.MainModule;
                if (m == null) { error = "could not read the game's main module"; return null; }
                baseAddr = (uint)m.BaseAddress.ToInt64();
            }
            catch (Exception ex) { error = "could not read the game's main module: " + ex.Message; return null; }

            if (baseAddr != ExpectedImageBase)
            {
                // The analysed exe cannot relocate, so a different base means a different build and
                // nothing in the map applies. Refuse rather than write to guessed addresses.
                error = "the game loaded at 0x" + baseAddr.ToString("x8") + ", not the 0x" +
                        ExpectedImageBase.ToString("x8") + " the map was built for, so this is a " +
                        "different build and nothing may be written to it";
                return null;
            }

            IntPtr h = OpenProcess(
                ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation,
                false, game.Id);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = "OpenProcess failed with Win32 error " + err +
                        (err == 5 ? ". The game runs elevated under TeknoParrot, so SimHub has to be elevated too." : ".");
                return null;
            }

            return new Id8LeaderboardWriter(h);
        }

        /// <summary>Walk to the four tables. Must succeed before any read or write.</summary>
        public bool Resolve(out string error)
        {
            error = null;
            _bases.Clear();

            uint system = Deref(SystemPtrVa);
            if (system == 0) { error = "the game has not booted far enough for the leaderboards to exist"; return false; }

            uint obj = system + SystemToObj;
            if (!TryI32(obj + ObjToCount, out int count) || count <= 0 || count > 256)
            {
                error = "the slot count read back as " + count + ", which is not usable";
                return false;
            }
            CourseSlots = count;

            foreach (Id8Board b in new[] { Id8Board.ShopPerCar, Id8Board.ShopTopTen, Id8Board.OnlinePerCar, Id8Board.OnlineTopTen })
            {
                uint p = Deref((uint)(obj + (int)b));
                if (p == 0) { error = b + " is not allocated yet"; return false; }
                _bases[b] = p;
            }

            return LayoutLooksRight(out error);
        }

        /// <summary>Check the four tables are laid out the way the analysed build lays them out,
        /// and refuse to write if not.
        ///
        /// This matters because the plugin has to cope with exe variants. Translation patches and
        /// re-dumps are common for this game, and a byte-patch of the same build keeps every static
        /// address, so those work. A DIFFERENT build does not, and the failure mode is the bad one:
        /// [0x013bb860] would still dereference to something, a count could land in range by
        /// chance, and four non-null values would be read as table pointers. We would then write
        /// 48-byte records into whatever they actually point at.
        ///
        /// The signature used is the measured layout: the four tables are allocated CONTIGUOUSLY,
        /// so sorting the bases and checking each gap equals the size of the table below it is a
        /// coincidence four times over. Cheap, and it fails closed.</summary>
        private bool LayoutLooksRight(out string error)
        {
            error = null;

            var byAddr = new List<KeyValuePair<Id8Board, uint>>(_bases);
            byAddr.Sort((x, y) => x.Value.CompareTo(y.Value));

            for (int i = 0; i < byAddr.Count; i++)
                for (int j = i + 1; j < byAddr.Count; j++)
                    if (byAddr[i].Value == byAddr[j].Value)
                    {
                        error = "two leaderboard tables point at the same address, so this is not the build the map was made for";
                        return false;
                    }

            for (int i = 0; i + 1 < byAddr.Count; i++)
            {
                long gap = (long)byAddr[i + 1].Value - byAddr[i].Value;
                long expected = (long)Id8LeaderboardRecord.Size * CourseSlots * PagesFor(byAddr[i].Key);
                if (gap != expected)
                {
                    error = $"the leaderboard tables are not laid out as expected ({byAddr[i].Key} to " +
                            $"{byAddr[i + 1].Key} is {gap} bytes, not {expected}). This is a different " +
                            "build of the game, so nothing will be written to it.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Pages a board holds. The per-car boards page by car, the top-ten boards by rank.
        /// </summary>
        public static int PagesFor(Id8Board board)
        {
            return board == Id8Board.ShopPerCar || board == Id8Board.OnlinePerCar ? PerCarPages : TopTenPages;
        }

        /// <summary>Byte offset of a record within its table, refusing anything that would land
        /// outside it. Pure and public precisely because it is the safety-critical calculation
        /// here: these four tables sit CONSECUTIVELY in memory, so an offset one record too far
        /// does not fail, it silently writes into the neighbouring board.
        ///
        /// Returns false rather than clamping. A clamped write would corrupt a different row and
        /// look like it worked.</summary>
        public static bool TryRecordOffset(
            Id8Board board, int courseSlots, int courseId, int direction, int page, out long offset)
        {
            offset = 0;
            if (courseSlots <= 0 || courseSlots > 256) return false;
            if (page < 0 || page >= PagesFor(board)) return false;
            if (courseId < 0 || courseId > 15) return false;
            if (direction < 0 || direction > 1) return false;

            int slot = Id8Leaderboard.SlotIndex(courseId, direction);
            if (slot >= courseSlots) return false;

            long off = (long)Id8LeaderboardRecord.Size * (slot + (long)courseSlots * page);
            long extent = (long)Id8LeaderboardRecord.Size * courseSlots * PagesFor(board);
            if (off < 0 || off + Id8LeaderboardRecord.Size > extent) return false;

            offset = off;
            return true;
        }

        /// <summary>Address of one record, or 0 when it would fall outside the table.</summary>
        private uint AddressOf(Id8Board board, int courseId, int direction, int page)
        {
            if (!_bases.TryGetValue(board, out uint tableBase)) return 0;
            if (!TryRecordOffset(board, CourseSlots, courseId, direction, page, out long off)) return 0;
            return (uint)(tableBase + off);
        }

        /// <summary>Read the rows of one board for a course and direction.</summary>
        public Id8LeaderboardRecord[] ReadBoard(Id8Board board, int courseId, int direction)
        {
            int pages = PagesFor(board);
            var rows = new List<Id8LeaderboardRecord>(pages);
            var buf = new byte[Id8LeaderboardRecord.Size];

            for (int page = 0; page < pages; page++)
            {
                uint a = AddressOf(board, courseId, direction, page);
                if (a == 0 || !ReadRaw(a, buf, buf.Length)) break;
                rows.Add(Id8LeaderboardRecord.Read(buf, 0));
            }
            return rows.ToArray();
        }

        /// <summary>Write rows to a board, one per page from page 0. Returns how many landed.
        /// A row that will not fit stops the write rather than wrapping.</summary>
        public int WriteBoard(Id8Board board, int courseId, int direction, IReadOnlyList<Id8LeaderboardRecord> rows)
        {
            if (rows == null) return 0;
            var buf = new byte[Id8LeaderboardRecord.Size];
            int written = 0;

            for (int page = 0; page < rows.Count; page++)
            {
                uint a = AddressOf(board, courseId, direction, page);
                if (a == 0) break;
                rows[page].Write(buf, 0);
                if (!WriteRaw(a, buf, buf.Length)) break;
                written++;
            }
            return written;
        }

        /// <summary>Write ONE record at a given page.
        ///
        /// The per-car boards need this rather than <see cref="WriteBoard"/>: their page index is a
        /// car slot, and the cars that have times are scattered across the 50. Writing them
        /// sequentially from page 0 would put every car's time on the wrong car.</summary>
        public bool WriteRecord(Id8Board board, int courseId, int direction, int page, Id8LeaderboardRecord row)
        {
            uint a = AddressOf(board, courseId, direction, page);
            if (a == 0) return false;
            var buf = new byte[Id8LeaderboardRecord.Size];
            row.Write(buf, 0);
            return WriteRaw(a, buf, buf.Length);
        }

        /// <summary>Read ONE record at a given page.</summary>
        public bool TryReadRecord(Id8Board board, int courseId, int direction, int page,
                                  out Id8LeaderboardRecord row)
        {
            row = default(Id8LeaderboardRecord);
            uint a = AddressOf(board, courseId, direction, page);
            if (a == 0) return false;
            var buf = new byte[Id8LeaderboardRecord.Size];
            if (!ReadRaw(a, buf, buf.Length)) return false;
            row = Id8LeaderboardRecord.Read(buf, 0);
            return true;
        }

        /// <summary>Raw bytes of a whole board, for a backup that can be restored verbatim.
        /// Deliberately raw rather than a decoded model: restoring bytes cannot lose a field we
        /// never understood.</summary>
        public byte[] SnapshotBoard(Id8Board board, int courseId, int direction)
        {
            int pages = PagesFor(board);
            var all = new byte[pages * Id8LeaderboardRecord.Size];
            for (int page = 0; page < pages; page++)
            {
                uint a = AddressOf(board, courseId, direction, page);
                if (a == 0) return null;
                var one = new byte[Id8LeaderboardRecord.Size];
                if (!ReadRaw(a, one, one.Length)) return null;
                Array.Copy(one, 0, all, page * Id8LeaderboardRecord.Size, one.Length);
            }
            return all;
        }

        /// <summary>Put a snapshot back exactly as it was.</summary>
        public bool RestoreBoard(Id8Board board, int courseId, int direction, byte[] snapshot)
        {
            if (snapshot == null || snapshot.Length % Id8LeaderboardRecord.Size != 0) return false;
            int pages = snapshot.Length / Id8LeaderboardRecord.Size;
            if (pages > PagesFor(board)) return false;

            var one = new byte[Id8LeaderboardRecord.Size];
            for (int page = 0; page < pages; page++)
            {
                uint a = AddressOf(board, courseId, direction, page);
                if (a == 0) return false;
                Array.Copy(snapshot, page * Id8LeaderboardRecord.Size, one, 0, one.Length);
                if (!WriteRaw(a, one, one.Length)) return false;
            }
            return true;
        }

        private bool ReadRaw(uint addr, byte[] buf, int len)
        {
            if (_h == IntPtr.Zero || addr == 0 || len <= 0) return false;
            return ReadProcessMemory(_h, (IntPtr)addr, buf, len, out IntPtr got) && got.ToInt64() == len;
        }

        private bool WriteRaw(uint addr, byte[] buf, int len)
        {
            if (_h == IntPtr.Zero || addr == 0 || len <= 0) return false;
            return WriteProcessMemory(_h, (IntPtr)addr, buf, len, out IntPtr put) && put.ToInt64() == len;
        }

        private bool TryI32(uint addr, out int v)
        {
            v = 0;
            if (!ReadRaw(addr, _word, 4)) return false;
            v = BitConverter.ToInt32(_word, 0);
            return true;
        }

        private uint Deref(uint addr)
        {
            if (!ReadRaw(addr, _word, 4)) return 0;
            return BitConverter.ToUInt32(_word, 0);
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { CloseHandle(_h); _h = IntPtr.Zero; }
        }
    }
}
