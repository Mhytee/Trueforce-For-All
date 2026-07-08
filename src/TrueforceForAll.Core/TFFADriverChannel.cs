// TFFADriverChannel - bridge between the user-mode plugin and the TFFA
// kernel filter driver's control device (\\?\TFFAControl). The control
// device is exposed by both filter variants (the HID-class TFFAFilter and
// the USB-layer TFFAUsbFilter), so this class is driver-neutral: it talks
// to whichever variant is installed and presenting the control device.
//
// Holding the control-device handle open claims wheel ownership at the
// kernel level: while we own it, any other process writing to the wheel
// gets its bytes intercepted by the driver and delivered to us via the
// inverted-call IOCTL_TFFA_RECV.
//
// WIRED: the decode-and-feed path. RecvLoop decodes the intercepted HID++
// 0x8123 fn2 motor target (int16) out of each delivered frame into _packed,
// and the plugin's FFB pipeline reads it back through TryGetFreshFfbTarget
// (same shape as UsbPcapFfbTap), so the game's force is routed through the
// Trueforce stream. Intercepted FFB + LED writes are absorbed (not echoed);
// the wheel only receives writes the plugin actively produces.
//
// NOT YET WIRED: the handshake echo. We do not yet echo intercepted game
// writes back to the wheel so the game's bidirectional HID++ protocol gets
// wheel responses; the onIntercepted callback currently logs/classifies
// only. That re-emit step is the remaining piece.
//
// Fault tolerance: if the driver isn't installed, the constructor logs
// and the channel goes into a no-op state (IsOpen = false). Nothing in
// this class is allowed to throw out to the plugin's lifecycle hooks.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TrueforceForAll.Core
{
    public sealed class TFFADriverChannel : IDisposable
    {
        // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
        // matches driver-side macros.
        private const uint IOCTL_TFFA_PING = 0x222000u;
        private const uint IOCTL_TFFA_RECV = 0x222004u;
        private const uint PING_MAGIC      = 0x54464641u; // 'TFFA' ASCII

        private const string ControlDevicePath = @"\\.\TFFAControl";

        // From winerror.h
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_BUSY           = 170;
        private const int ERROR_OPERATION_ABORTED = 995;

        private readonly Action<string> _log;
        private readonly Action<byte[], int> _onIntercepted;
        private readonly SafeFileHandle _handle;
        private Thread _worker;
        private volatile bool _stop;

        // FFB target packed-int storage (same shape as UsbPcapFfbTap so the
        // plugin's existing pipeline can drop us in interchangeably).
        // Low 16 bits = ffbTarget bit-pattern (signed int16), high 48 bits =
        // sample timestamp masked to 48 bits.
        private const long TimestampMask = 0x0000_FFFF_FFFF_FFFFL;
        private long _packed;
        private long _lastSampleTicks;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _ffbSamplesDecoded;

        // HID++ feature index for FFB (page 0x8123). Per-wheel; G PRO uses
        // 0x0e, RS50 uses 0x10. Seed with G PRO since that's the bring-up
        // wheel; per-wheel auto-resolution can be added later (the registry-
        // wide TryGetFreshFfbTarget already won't fire for indices we miss).
        // NOTE: this is decoded entirely from the intercepted byte stream,
        // not via WheelDiscovery; matches UsbPcapFfbTap's FfbFeatureIndexSeed.
        private byte _ffbFeatureIndex = 0x0e;

        public bool IsOpen   => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;
        public bool IsActive => IsOpen && _worker != null && _worker.IsAlive;
        public long FfbSamplesDecoded => Interlocked.Read(ref _ffbSamplesDecoded);

        /// <summary>Set the HID++ feature index that carries 0x8123 (FFB) for
        /// the active wheel. G PRO = 0x0e (default). RS50 = 0x10. Call this
        /// after discovery if you know the right index.</summary>
        public void SetFfbFeatureIndex(byte index) => _ffbFeatureIndex = index;

        /// <summary>Return the latest intercepted FFB motor target if it's
        /// fresher than <paramref name="maxAgeMs"/>. Mirrors
        /// UsbPcapFfbTap.TryGetFreshFfbTarget so the plugin's FFB pipeline can
        /// substitute this source when our driver-intercept path is active.</summary>
        public short? TryGetFreshFfbTarget(int maxAgeMs)
        {
            long packed = Interlocked.Read(ref _packed);
            if (packed == 0) return null;
            long timestamp = packed >> 16;
            short ffbTarget = (short)(ushort)(packed & 0xFFFF);
            long now = _sw.ElapsedTicks & TimestampMask;
            long ageTicks = (now - timestamp) & TimestampMask;
            long ageMs = ageTicks * 1000L / Stopwatch.Frequency;
            return ageMs <= maxAgeMs ? (short?)ffbTarget : null;
        }

        /// <summary>Open the driver control device and start the RECV worker.
        /// <paramref name="onIntercepted"/> is invoked on the worker thread
        /// each time the driver delivers intercepted bytes; treat it as an
        /// async callback. Logging is via <paramref name="log"/>.</summary>
        /// <remarks>Construction always succeeds. Inspect <see cref="IsOpen"/>
        /// to know whether the channel actually opened. Failures (driver not
        /// installed, device busy, etc.) are logged and the instance becomes
        /// a no-op that's safe to dispose.</remarks>
        public TFFADriverChannel(Action<string> log, Action<byte[], int> onIntercepted)
        {
            _log = log ?? (_ => { });
            _onIntercepted = onIntercepted ?? ((_, __) => { });

            try {
                _handle = CreateFileW(
                    ControlDevicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            } catch (Exception ex) {
                _log($"[TFFADriverChannel] CreateFile threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (_handle == null || _handle.IsInvalid) {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_FILE_NOT_FOUND) {
                    _log("[TFFADriverChannel] Control device not found - TFFA filter driver not installed or not loaded. Channel disabled.");
                } else if (err == ERROR_BUSY) {
                    _log("[TFFADriverChannel] Control device BUSY - another process is already the wheel owner. Channel disabled.");
                } else {
                    _log($"[TFFADriverChannel] Open failed: Win32 error {err}. Channel disabled.");
                }
                _handle?.Dispose();
                _handle = null;
                return;
            }

            // PING sanity check: prove the channel is wired correctly before we
            // start the RECV loop. A successful PING is also useful telemetry.
            if (!Ping(out uint magic)) {
                _log("[TFFADriverChannel] PING failed; closing channel.");
                _handle.Dispose();
                _handle = null;
                return;
            }
            if (magic != PING_MAGIC) {
                _log($"[TFFADriverChannel] PING magic mismatch (got 0x{magic:X8}, expected 0x{PING_MAGIC:X8}); closing.");
                _handle.Dispose();
                _handle = null;
                return;
            }

            _log("[TFFADriverChannel] Opened; PING ok; plugin is now the wheel owner. Starting RECV worker.");

            _worker = new Thread(RecvLoop) {
                IsBackground = true,
                Name = "TFFA-RECV"
            };
            _worker.Start();
        }

        public bool Ping(out uint magic)
        {
            magic = 0;
            if (!IsOpen) return false;

            byte[] buf = new byte[4];
            bool ok = DeviceIoControl(_handle, IOCTL_TFFA_PING,
                IntPtr.Zero, 0,
                buf, (uint)buf.Length,
                out _, IntPtr.Zero);
            if (!ok) {
                _log($"[TFFADriverChannel] PING DeviceIoControl failed: Win32 error {Marshal.GetLastWin32Error()}");
                return false;
            }
            magic = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        private void RecvLoop()
        {
            // 256 bytes covers the largest HID++ very-long (64) and Trueforce
            // audio chunks (64); larger gives headroom if a future write path
            // turns out to be bigger.
            byte[] buf = new byte[256];
            int recvCount = 0;

            while (!_stop) {
                if (!IsOpen) break;
                bool ok = DeviceIoControl(_handle, IOCTL_TFFA_RECV,
                    IntPtr.Zero, 0,
                    buf, (uint)buf.Length,
                    out uint bytesReturned, IntPtr.Zero);
                if (!ok) {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_OPERATION_ABORTED || _stop) {
                        _log("[TFFADriverChannel] RECV loop exiting (handle closed).");
                    } else {
                        _log($"[TFFADriverChannel] RECV loop fault: Win32 error {err}. Exiting.");
                    }
                    return;
                }

                recvCount++;
                int len = (int)bytesReturned;

                // Decode HID++ 0x8123 fn2 (G-series FFB) motor target if this
                // looks like an FFB report from a game. Same bit-layout as
                // UsbPcapFfbTap's HID++ extraction: report 0x11/0x12, devIdx
                // 0xff, featIdx == FfbFeatureIndex, funcByte high nibble = 0x20,
                // motor target int16 BE at offset 10-11.
                if (len >= 12
                    && (buf[0] == 0x11 || buf[0] == 0x12)
                    && buf[1] == 0xFF
                    && buf[2] == _ffbFeatureIndex
                    && (buf[3] & 0xF0) == 0x20)
                {
                    short target = (short)((buf[10] << 8) | buf[11]);
                    long ts = _sw.ElapsedTicks & TimestampMask;
                    long pk = (ts << 16) | (uint)(ushort)target;
                    Interlocked.Exchange(ref _packed, pk);
                    Interlocked.Exchange(ref _lastSampleTicks, ts);
                    Interlocked.Increment(ref _ffbSamplesDecoded);
                }

                try {
                    _onIntercepted(buf, len);
                } catch (Exception ex) {
                    // Never let the callback take down the worker.
                    _log($"[TFFADriverChannel] onIntercepted threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _log($"[TFFADriverChannel] RECV loop done. Total intercepted: {recvCount}");
        }

        public void Dispose()
        {
            _stop = true;
            try { _handle?.Dispose(); } catch { /* swallow */ }
            try {
                if (_worker != null && _worker.IsAlive) {
                    _worker.Join(TimeSpan.FromSeconds(2));
                }
            } catch { /* swallow */ }
        }

        // ---- P/Invoke ----------------------------------------------------

        private const uint GENERIC_READ  = 0x80000000u;
        private const uint GENERIC_WRITE = 0x40000000u;
        private const uint OPEN_EXISTING = 3u;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            [Out] byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }
}
