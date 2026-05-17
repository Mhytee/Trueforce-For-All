// MAIRA -> TF4ALL FFB/RPM passthrough over a shared memory-mapped file.
//
// Why this exists: on the Logitech G PRO the rim rev LEDs (HID++ page
// 0x807A) and PID force feedback (HID++ page 0x8123) share one command
// processor on the wheel. When MAIRA streams PID FFB at ~320 Hz, any LED
// write stalls FFB ~1.5 s (measured). It is a device-level mutual
// exclusion, not fixable by threading or cadence.
//
// The fix is to keep PID off the HID++ pipe entirely. With MAIRA's
// "Pass FFB signal through TF4ALL" toggle on, MAIRA does NOT send PID to
// the wheel; it writes its computed force (normalized -1..1) plus RPM and
// the car's shift-light RPMs into this MMF. TF4ALL renders the force
// through the Trueforce ep3 stream (a separate physical resource) and
// drives the LEDs over HID++ itself. No PID anywhere => no contention,
// using the LED code already hardware-verified on the G PRO.
//
// Layout (little-endian, fixed 40 bytes), single writer (MAIRA), single
// reader (TF4ALL); a monotonic sequence written last makes torn reads
// detectable. No locking needed for one 4-byte-aligned producer.

using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class MairaIpcSource : IDisposable
    {
        public const string DefaultMapName = "Local\\TF4ALL_MAIRA_FFB_v1";
        public const int    MapSize        = 64;
        private const int   Magic          = 0x4D414952; // 'MAIR'
        private const int   Version        = 1;

        // Offsets
        private const int OffMagic   = 0;   // int32
        private const int OffVersion = 4;   // int32
        private const int OffSeqA    = 8;   // int64  (written first)
        private const int OffFfbNorm = 16;  // float  -1..1
        private const int OffRpm     = 20;  // float
        private const int OffSlFirst = 24;  // float
        private const int OffSlShift = 28;  // float
        private const int OffWriteMs = 32;  // int64  Unix ms (writer clock)
        private const int OffSeqB    = 40;  // int64  (written last; == SeqA when consistent)

        private readonly Action<string> _log;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private bool _opened;
        private long _lastSeq = -1;
        private long _lastSeenMs;          // local stopwatch ms when seq last advanced
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        // Last decoded payload (valid only when TryRefresh returned true).
        public float FfbNormalized { get; private set; }
        public float Rpm           { get; private set; }
        public float ShiftFirstRpm { get; private set; }
        public float ShiftShiftRpm { get; private set; }

        public bool IsOpen => _opened;
        public string Status =>
            !_opened ? "not connected (MAIRA passthrough off / MAIRA not running)"
                     : (_sw.ElapsedMilliseconds - _lastSeenMs) < 1000
                         ? "connected, receiving" : "connected, stale (no recent MAIRA data)";

        public MairaIpcSource(Action<string> log) { _log = log ?? (_ => { }); }

        public bool EnsureOpen()
        {
            if (_opened) return true;
            try
            {
                // OpenExisting: MAIRA (the writer) creates it. If MAIRA isn't
                // running yet we just keep trying on later ticks.
                _mmf = MemoryMappedFile.OpenExisting(DefaultMapName, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.Read);
                _opened = true;
                _log("[MAIRA-IPC] connected to MAIRA FFB shared memory.");
                return true;
            }
            catch (System.IO.FileNotFoundException) { return false; }
            catch (Exception ex) { _log($"[MAIRA-IPC] open failed: {ex.Message}"); return false; }
        }

        /// <summary>Read the latest consistent sample. Returns false if not
        /// open, torn, or unchanged. Updates the public fields on success.</summary>
        public bool TryRefresh()
        {
            if (!_opened && !EnsureOpen()) return false;
            try
            {
                int magic = _view.ReadInt32(OffMagic);
                if (magic != Magic) return false;
                long seqA = _view.ReadInt64(OffSeqA);
                float ffb = _view.ReadSingle(OffFfbNorm);
                float rpm = _view.ReadSingle(OffRpm);
                float slF = _view.ReadSingle(OffSlFirst);
                float slS = _view.ReadSingle(OffSlShift);
                long seqB = _view.ReadInt64(OffSeqB);
                if (seqA != seqB) return false;          // torn write, skip this tick
                if (seqA == _lastSeq) return false;       // nothing new

                _lastSeq = seqA;
                _lastSeenMs = _sw.ElapsedMilliseconds;
                FfbNormalized = ffb;
                Rpm = rpm; ShiftFirstRpm = slF; ShiftShiftRpm = slS;
                return true;
            }
            catch (Exception ex) { _log($"[MAIRA-IPC] read failed: {ex.Message}"); _opened = false; return false; }
        }

        /// <summary>FFB target for the device's FfbTargetProvider, in the same
        /// signed-int16 scale the USBPcap tap produced, or null if no fresh
        /// MAIRA data within <paramref name="maxAgeMs"/>.</summary>
        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            TryRefresh();
            if (!_opened) return null;
            if ((_sw.ElapsedMilliseconds - _lastSeenMs) > maxAgeMs) return null;
            float n = FfbNormalized;
            if (n > 1f) n = 1f; else if (n < -1f) n = -1f;
            return (short)(n * 32767f);
        }

        public void Dispose()
        {
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null; _mmf = null; _opened = false;
        }

        // ---- Writer helper (used by the MAIRA fork; kept here so the wire
        // format lives in exactly one place). MAIRA targets a different .NET
        // but this file is copied/ported there verbatim. ----
        public sealed class Writer : IDisposable
        {
            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _view;
            private long _seq;

            public Writer()
            {
                _mmf = MemoryMappedFile.CreateOrOpen(DefaultMapName, MapSize, MemoryMappedFileAccess.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.ReadWrite);
                _view.Write(OffMagic, Magic);
                _view.Write(OffVersion, Version);
            }

            public void Write(float ffbNormalized, float rpm, float slFirst, float slShift)
            {
                long seq = ++_seq;
                _view.Write(OffSeqA, seq);                 // mark in-progress
                Thread.MemoryBarrier();
                _view.Write(OffFfbNorm, ffbNormalized);
                _view.Write(OffRpm, rpm);
                _view.Write(OffSlFirst, slFirst);
                _view.Write(OffSlShift, slShift);
                _view.Write(OffWriteMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Thread.MemoryBarrier();
                _view.Write(OffSeqB, seq);                 // publish (== SeqA)
            }

            public void Dispose()
            {
                try { _view?.Dispose(); } catch { }
                try { _mmf?.Dispose(); } catch { }
            }
        }
    }
}
