// Direct reader for iRacing's telemetry shared memory.
//
// Why not just keep using SimHub's copy: going through SimHub cost 43 of 8,421
// ticks over 140 seconds (0.51%), measured on the owner's rig. Each dropped tick
// takes six 360 Hz sub-samples with it, so it is a 16.7 ms hole in the force
// stream, and the loss is jittery rather than constant, which nothing downstream
// can compensate for. Reading the map ourselves also drops a dependency on
// SimHub's internal ICarsReader / iRacingSDK type names, which we currently
// reach by reflection and which they may rename at any release.
//
// DO NOT WAIT ON Local\IRSDKDataValidEvent. This is the single most important
// thing in this file. SimHub's own iRacingSDK.dll already opens that event and
// waits on it in-process, and it is an AUTO-RESET event: SetEvent releases
// exactly ONE waiter. A second waiter inside the same process therefore does not
// observe the signal, it STEALS roughly half of them, dropping both our source
// and SimHub's own reader to about 30 Hz and filling SimHub's log with missing
// sample warnings. That would degrade the very fallback we are keeping, and
// break iRacing for every other plugin in the host.
//
// Polling is also simply better here: four int reads on a 1 ms cadence detect a
// new tick within ~1 ms, where the event only fires at the sim's 16.7 ms frame
// rate. Same approach AcSharedMemoryTelemetrySource already uses.
//
// Wire format verified against four independent sources: the canonical
// irsdk_defines.h (vipoo/irsdk), pyirsdk, IRSDKSharper's IL, and SimHub's own
// shipped iRacingSDK.dll. All four agree byte for byte.

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TrueforceForAll.Core
{
    /// <summary>Reads iRacing's telemetry map directly. Owns a poll thread and
    /// publishes whole, tear-free frames; callers resolve channels by name.</summary>
    public sealed class IRacingSdkReader : IDisposable
    {
        public const string MemMapName = "Local\\IRSDKMemMapFileName";

        // Header layout, all int32. Verified across four sources.
        private const int OffVer               = 0;
        private const int OffStatus            = 4;
        private const int OffTickRate          = 8;
        private const int OffSessionInfoUpdate = 12;
        private const int OffSessionInfoLen    = 16;
        private const int OffSessionInfoOffset = 20;
        private const int OffNumVars           = 24;
        private const int OffVarHeaderOffset   = 28;
        private const int OffNumBuf            = 32;
        private const int OffBufLen            = 36;
        // pad1[2] occupies 40..47.
        private const int OffVarBuf            = 48;   // varBuf[4], 16 bytes each
        private const int VarBufStride         = 16;   // tickCount, bufOffset, pad[2]
        private const int MaxBufs              = 4;

        // varHeader: type, offset, count, countAsTime + pad[3], name[32],
        // desc[64], unit[32].
        private const int VarHeaderStride = 144;
        private const int VhType   = 0;
        private const int VhOffset = 4;
        private const int VhCount  = 8;
        private const int VhName   = 16;
        private const int VarNameLen = 32;

        private const int StatusConnected = 1;

        // irsdk_VarType and its widths.
        public const int VarChar = 0, VarBool = 1, VarInt = 2, VarBitField = 3, VarFloat = 4, VarDouble = 5;
        private static readonly int[] VarTypeBytes = { 1, 1, 4, 4, 4, 8 };

        public sealed class VarDef
        {
            public int Type;
            public int Offset;
            public int Count;
        }

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Thread _thread;
        private volatile bool _stopping;
        private int _running;

        private readonly object _swap = new object();
        private Dictionary<string, VarDef> _vars = new Dictionary<string, VarDef>(StringComparer.Ordinal);
        private byte[] _frame;              // the published, tear-free row
        private int _frameLen;
        private int _lastTick = int.MinValue;
        private int _sessionInfoUpdate = -1;
        private string _sessionYaml;

        /// <summary>Raised on the poll thread once per new tick, after the frame
        /// has been published. Kept deliberately thin: heavy work here stalls
        /// detection of the next tick.</summary>
        public Action OnTick;
        public Action<string> Logger;

        /// <summary>True while the sim is running AND publishing. Status is the
        /// sim's own connected flag, so this goes false when iRacing exits
        /// without us having to infer it from silence.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>Ticks seen, and ticks the sim advanced. The difference is a
        /// direct measure of what this class exists to eliminate, so it is worth
        /// carrying rather than assuming it is zero.</summary>
        public long TicksSeen { get; private set; }
        public long TicksMissed { get; private set; }

        public string SessionYaml { get { lock (_swap) { return _sessionYaml; } } }
        public int SessionInfoUpdate { get { lock (_swap) { return _sessionInfoUpdate; } } }

        public bool Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return true;
            try
            {
                _mmf  = MemoryMappedFile.OpenExisting(MemMapName, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            catch
            {
                // iRacing is simply not running. Not an error, and not worth a
                // log line every retry.
                Interlocked.Exchange(ref _running, 0);
                Cleanup();
                return false;
            }

            _stopping = false;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "IRacingSdkReader",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Log("iRacing direct telemetry reader started.");
            return true;
        }

        public void Stop()
        {
            _stopping = true;
            try { if (_thread != null) _thread.Join(2000); } catch { }
            _thread = null;
            Cleanup();
            lock (_swap)
            {
                _vars = new Dictionary<string, VarDef>(StringComparer.Ordinal);
                _frame = null; _frameLen = 0;
                _sessionYaml = null; _sessionInfoUpdate = -1;
            }
            _lastTick = int.MinValue;
            IsConnected = false;
            Interlocked.Exchange(ref _running, 0);
        }

        public void Dispose() { Stop(); }

        private void Cleanup()
        {
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null; _mmf = null;
        }

        private void PollLoop()
        {
            // Without this Thread.Sleep(1) decays to the host's default ~15 ms
            // tick and the whole point of polling is lost. Reference counted, so
            // the matching EndPeriod is not optional.
            TimeBeginPeriod(1);
            try
            {
                while (!_stopping)
                {
                    try { PollOnce(); }
                    catch (Exception ex) { Log("iRacing reader poll error: " + ex.GetType().Name + ": " + ex.Message); }
                    Thread.Sleep(1);
                }
            }
            finally { TimeEndPeriod(1); }
        }

        private void PollOnce()
        {
            var view = _view;
            if (view == null) return;

            int status = view.ReadInt32(OffStatus);
            IsConnected = (status & StatusConnected) != 0;
            if (!IsConnected) return;

            int numBuf = view.ReadInt32(OffNumBuf);
            int bufLen = view.ReadInt32(OffBufLen);
            if (numBuf < 1 || numBuf > MaxBufs || bufLen < 1 || bufLen > (1 << 24)) return;

            // Newest published buffer.
            int latest = 0, latestTick = view.ReadInt32(OffVarBuf + OffTickBuf(0));
            for (int i = 1; i < numBuf; i++)
            {
                int t = view.ReadInt32(OffVarBuf + OffTickBuf(i));
                if (t > latestTick) { latestTick = t; latest = i; }
            }
            if (latestTick == _lastTick || latestTick <= 0) return;

            // The variable table is republished on a session or car change, so
            // it is re-read whenever its declared shape moves rather than once.
            int numVars = view.ReadInt32(OffNumVars);
            int varHdrOff = view.ReadInt32(OffVarHeaderOffset);
            EnsureVarTable(view, numVars, varHdrOff);

            int bufOffset = view.ReadInt32(OffVarBuf + OffTickBuf(latest) + 4);
            if (bufOffset < 0) return;

            byte[] row = new byte[bufLen];

            // TEAR GUARD, and the naive version of this is NOT safe. The obvious
            // protocol (note the tick, copy, check the tick again) fails on a
            // real interleaving: if this thread is descheduled long enough for
            // the writer to come all the way around and start writing tick N+numBuf
            // back into this same buffer, it will not have published the new
            // tickCount yet, so the recheck still reads N and the half-written
            // row is accepted. At 60 Hz that is invisible in telemetry, but these
            // bytes feed a 1 kHz force interpolator, so a torn sub-tick array
            // becomes a one-millisecond torque spike with no log line.
            //
            // The extra condition closes it: buffer `latest` is next rewritten at
            // tick N + numBuf, and the writer publishes only after finishing, so
            // the first observable moment it could be inside our buffer is when
            // some other buffer reads N + numBuf - 1. In the healthy case the max
            // is still N and this costs nothing.
            bool ok = false;
            for (int attempt = 0; attempt < 2 && !ok; attempt++)
            {
                int pre = view.ReadInt32(OffVarBuf + OffTickBuf(latest));
                view.ReadArray(bufOffset, row, 0, bufLen);
                int post = view.ReadInt32(OffVarBuf + OffTickBuf(latest));
                if (post != pre) continue;

                int postMax = pre;
                for (int i = 0; i < numBuf; i++)
                {
                    int t = view.ReadInt32(OffVarBuf + OffTickBuf(i));
                    if (t > postMax) postMax = t;
                }
                if (postMax - pre >= numBuf - 1) continue;   // writer may be inside our row

                latestTick = pre;
                ok = true;
            }
            if (!ok) return;   // drop this frame rather than publish a torn one

            // Session info, only when the sim says it changed. It is ~13 KB of
            // YAML and re-reading it per tick would be pure waste.
            int siUpdate = view.ReadInt32(OffSessionInfoUpdate);
            string yaml = null;
            if (siUpdate != _sessionInfoUpdate)
            {
                int siLen = view.ReadInt32(OffSessionInfoLen);
                int siOff = view.ReadInt32(OffSessionInfoOffset);
                if (siLen > 0 && siLen < (1 << 22) && siOff > 0)
                {
                    var raw = new byte[siLen];
                    view.ReadArray(siOff, raw, 0, siLen);
                    int end = Array.IndexOf(raw, (byte)0);
                    if (end < 0) end = siLen;
                    // Windows-1252 is the legacy default and what every reader
                    // in the wild assumes; ASCII would mangle accented driver
                    // names, and UTF8 would mis-decode the same bytes.
                    yaml = Latin1(raw, end);
                }
            }

            if (_lastTick != int.MinValue && latestTick > _lastTick)
            {
                long gap = latestTick - _lastTick - 1;
                if (gap > 0) TicksMissed += gap;
            }
            _lastTick = latestTick;
            TicksSeen++;

            lock (_swap)
            {
                _frame = row;
                _frameLen = bufLen;
                if (yaml != null) { _sessionYaml = yaml; _sessionInfoUpdate = siUpdate; }
            }

            var cb = OnTick;
            if (cb != null) { try { cb(); } catch { } }
        }

        private static int OffTickBuf(int i) { return i * VarBufStride; }

        private int _varTableVars = -1;
        private int _varTableOff = -1;

        private void EnsureVarTable(MemoryMappedViewAccessor view, int numVars, int varHdrOff)
        {
            if (numVars <= 0 || numVars > 4096 || varHdrOff <= 0) return;
            if (numVars == _varTableVars && varHdrOff == _varTableOff) return;

            var map = new Dictionary<string, VarDef>(StringComparer.Ordinal);
            var nameBuf = new byte[VarNameLen];
            for (int i = 0; i < numVars; i++)
            {
                int b = varHdrOff + i * VarHeaderStride;
                int type = view.ReadInt32(b + VhType);
                int off  = view.ReadInt32(b + VhOffset);
                int cnt  = view.ReadInt32(b + VhCount);
                if (type < 0 || type > VarDouble || off < 0 || cnt < 1) continue;
                view.ReadArray(b + VhName, nameBuf, 0, VarNameLen);
                int end = Array.IndexOf(nameBuf, (byte)0);
                if (end <= 0) continue;
                string name = Encoding.ASCII.GetString(nameBuf, 0, end);
                map[name] = new VarDef { Type = type, Offset = off, Count = cnt };
            }
            lock (_swap) { _vars = map; }
            _varTableVars = numVars;
            _varTableOff = varHdrOff;
            Log("iRacing variable table: " + map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " channels.");
        }

        /// <summary>Snapshot the published frame and the current name table
        /// together, so a caller reading several channels cannot straddle a
        /// republish and resolve half its names against the wrong layout.</summary>
        public bool TryGetSnapshot(out byte[] frame, out Dictionary<string, VarDef> vars)
        {
            lock (_swap)
            {
                frame = _frame;
                vars = _vars;
                return frame != null && vars != null && vars.Count > 0;
            }
        }

        public static bool TryReadDouble(byte[] frame, VarDef v, int index, out double value)
        {
            value = 0.0;
            if (frame == null || v == null || index < 0 || index >= v.Count) return false;
            if (v.Type < 0 || v.Type > VarDouble) return false;
            int at = v.Offset + index * VarTypeBytes[v.Type];
            if (at < 0 || at + VarTypeBytes[v.Type] > frame.Length) return false;
            switch (v.Type)
            {
                case VarChar:     value = frame[at]; return true;
                case VarBool:     value = frame[at] != 0 ? 1.0 : 0.0; return true;
                case VarInt:
                case VarBitField: value = BitConverter.ToInt32(frame, at); return true;
                case VarFloat:
                {
                    float f = BitConverter.ToSingle(frame, at);
                    if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                    value = f; return true;
                }
                case VarDouble:
                {
                    double d = BitConverter.ToDouble(frame, at);
                    if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                    value = d; return true;
                }
            }
            return false;
        }

        /// <summary>Read a whole array channel (the _ST sub-tick arrays).</summary>
        public static float[] TryReadArray(byte[] frame, VarDef v)
        {
            if (frame == null || v == null || v.Count < 1) return null;
            var outv = new float[v.Count];
            for (int i = 0; i < v.Count; i++)
            {
                double d;
                if (!TryReadDouble(frame, v, i, out d)) return null;
                outv[i] = (float)d;
            }
            return outv;
        }

        // Windows-1252 without depending on a code page provider being
        // registered, which netstandard2.0 consumers cannot assume. The range
        // this covers is what driver names actually use.
        private static string Latin1(byte[] b, int len)
        {
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append((char)b[i]);
            return sb.ToString();
        }

        private void Log(string m)
        {
            var l = Logger;
            if (l != null) { try { l(m); } catch { } }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);
    }
}
