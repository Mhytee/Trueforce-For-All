// Trueforce session + audio-haptic stream.
//
// Ported from mescon/logitech-rs50-linux-driver:
//   userspace/libtrueforce/src/session.c   (Open + InitSequence)
//   userspace/libtrueforce/src/stream.c    (ring buffer + 250 Hz packet pump)
//
// The wheel firmware's Trueforce path takes a 1 kHz audio-haptic stream,
// delivered as 64-byte HID output reports at 250 Hz, each carrying a
// 13-slot rolling sample window (4 new samples added per packet).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

namespace SimHubTrueforce.Core
{
    public sealed class TrueforceDevice : IDisposable
    {
        public const int PacketLen = 64;
        public const int Window = 13;          // total slots in rolling window
        public const int NewPerPacket = 4;     // new samples shifted in per packet
        // Ring sized for low control latency: 32 samples = 32 ms at 1 kHz.
        // libtrueforce uses 4096 because a game might push large bursts when
        // convenient; a real-time control loop (slider, telemetry) wants a
        // tight buffer so producer changes reach the wheel quickly. 32 ms is
        // imperceptible for haptics and gives ~24 ms of headroom for Gen1 GC
        // pauses before we underrun. Lower is risky on .NET.
        public const int RingSize = 32;        // power of two
        public const int PacketHz = 250;       // 1000 Hz / NewPerPacket

        private const int InitInterPacketUs = 2000; // 2 ms between init packets

        private readonly HidDevice _hidDevice;
        private HidStream _stream;
        private Thread _streamThread;

        private readonly object _streamLock = new object();
        private volatile bool _streamRunning;
        private volatile bool _shuttingDown;
        private volatile bool _paused;

        private byte _seq;

        // 13-slot rolling window of u16 offset-binary samples (newest at index Window-1).
        private readonly ushort[] _window = new ushort[Window];
        private ushort _lastCurrent = 0x8000;

        // Single-producer / single-consumer ring buffer. Indices wrap mod RingSize
        // (which is a power of two). Samples are stored as offset-binary u16.
        private readonly ushort[] _ring = new ushort[RingSize];
        private int _ringHead;  // producer index
        private int _ringTail;  // consumer index
        private readonly object _ringLock = new object();

        // Reusable packet buffer (re-zeroed on each tick).
        private readonly byte[] _packetBuf = new byte[PacketLen];

        public TrueforceDevice(HidDevice hidDevice)
        {
            _hidDevice = hidDevice ?? throw new ArgumentNullException(nameof(hidDevice));
        }

        public void Open()
        {
            if (_stream != null) return;

            var openConfig = new OpenConfiguration();
            // Best-effort; HidSharp may ignore unknown options on some platforms.
            _stream = _hidDevice.Open(openConfig);
            _stream.WriteTimeout = 250;
            _stream.ReadTimeout  = 250;
        }

        // Send the 68-packet init sequence twice, sequence counter restarted at 1
        // each pass. Per the protocol doc, two passes are required for cold-boot
        // reliability across G HUB captures.
        public void RunInitSequence()
        {
            if (_stream == null)
                throw new InvalidOperationException("Device not open");

            byte[] pkt = new byte[InitData.PacketLen];

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < InitData.PacketCount; i++)
                {
                    Buffer.BlockCopy(InitData.Packets[i], 0, pkt, 0, InitData.PacketLen);
                    pkt[InitData.SeqOffset] = (byte)((i + 1) & 0xFF);
                    _stream.Write(pkt);
                    PrecisionSleepUs(InitInterPacketUs);
                }
            }

            _seq = (byte)((InitData.PacketCount + 1) & 0xFF);
            for (int i = 0; i < Window; i++) _window[i] = 0x8000;
            _lastCurrent = 0x8000;
        }

        public void StartStream()
        {
            lock (_streamLock)
            {
                if (_streamRunning) return;
                _streamRunning = true;
                _shuttingDown = false;
                _paused = false;
                _streamThread = new Thread(StreamLoop)
                {
                    IsBackground = true,
                    Name = "TrueforceStream",
                    Priority = ThreadPriority.AboveNormal,
                };
                _streamThread.Start();
            }
        }

        public void StopStream()
        {
            Thread t;
            lock (_streamLock)
            {
                if (!_streamRunning) return;
                _shuttingDown = true;
                t = _streamThread;
            }
            // Wake any producer blocked on a full ring.
            lock (_ringLock) { Monitor.PulseAll(_ringLock); }
            t?.Join();
            lock (_streamLock)
            {
                _streamRunning = false;
                _streamThread = null;
            }
        }

        public void ClearStream()
        {
            lock (_ringLock)
            {
                _ringTail = _ringHead;
                Monitor.PulseAll(_ringLock);
            }
            for (int i = 0; i < Window; i++) _window[i] = 0x8000;
            _lastCurrent = 0x8000;
        }

        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;

        // Push samples in [-1.0, 1.0] float range. Blocks if the ring is full.
        public void PushFloats(float[] samples, int count)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count <= 0) return;

            lock (_ringLock)
            {
                for (int i = 0; i < count; i++)
                {
                    while (RingFreeUnlocked() == 0 && _streamRunning && !_shuttingDown)
                        Monitor.Wait(_ringLock);
                    if (_shuttingDown || !_streamRunning) return;

                    _ring[_ringHead & (RingSize - 1)] = FloatToWire(samples[i]);
                    _ringHead++;
                }
                Monitor.PulseAll(_ringLock);
            }
        }

        public void PushInt16(short[] samples, int count)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count <= 0) return;

            lock (_ringLock)
            {
                for (int i = 0; i < count; i++)
                {
                    while (RingFreeUnlocked() == 0 && _streamRunning && !_shuttingDown)
                        Monitor.Wait(_ringLock);
                    if (_shuttingDown || !_streamRunning) return;

                    _ring[_ringHead & (RingSize - 1)] = S16ToWire(samples[i]);
                    _ringHead++;
                }
                Monitor.PulseAll(_ringLock);
            }
        }

        // ---------- internals ----------

        private int RingOccupiedUnlocked() => (_ringHead - _ringTail) & (RingSize - 1);
        private int RingFreeUnlocked()     => RingSize - 1 - RingOccupiedUnlocked();

        private static ushort FloatToWire(float v)
        {
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            return (ushort)((int)(v * 32767f) + 0x8000);
        }

        private static ushort S16ToWire(short v)
        {
            return (ushort)((int)v + 0x8000);
        }

        private void StreamLoop()
        {
            // Bump the system timer to 1 ms granularity for the duration of the loop.
            TimeBeginPeriod(1);
            try
            {
                var sw = Stopwatch.StartNew();
                long periodTicks = Stopwatch.Frequency / PacketHz; // ticks per packet
                long nextTick = sw.ElapsedTicks + periodTicks;

                while (!_shuttingDown)
                {
                    StreamTick();

                    long now = sw.ElapsedTicks;
                    long remaining = nextTick - now;
                    if (remaining > 0)
                    {
                        // Coarse sleep down to ~1 ms remaining, then spin.
                        long oneMsTicks = Stopwatch.Frequency / 1000;
                        while (!_shuttingDown && (nextTick - sw.ElapsedTicks) > oneMsTicks)
                            Thread.Sleep(1);
                        while (!_shuttingDown && sw.ElapsedTicks < nextTick)
                            Thread.SpinWait(64);
                    }
                    nextTick += periodTicks;

                    // If we slipped more than one period (long stall), don't try to catch up
                    // by burst-writing — emit one packet per loop iteration.
                    if (sw.ElapsedTicks - nextTick > periodTicks)
                        nextTick = sw.ElapsedTicks + periodTicks;
                }
            }
            finally
            {
                TimeEndPeriod(1);
            }
        }

        private void StreamTick()
        {
            // Drain up to NewPerPacket samples from the ring (non-blocking).
            ushort[] newSamples = new ushort[NewPerPacket];
            int n = 0;
            lock (_ringLock)
            {
                while (n < NewPerPacket && _ringTail != _ringHead)
                {
                    newSamples[n++] = _ring[_ringTail & (RingSize - 1)];
                    _ringTail++;
                }
                if (n > 0) Monitor.PulseAll(_ringLock);
            }

            // Shift the window left by NewPerPacket, fill the tail with new samples
            // (or repeat the last known value on starvation).
            const int shift = NewPerPacket;
            // Array.Copy guarantees correct behaviour for overlapping ranges in
            // the same array; Buffer.BlockCopy doesn't make that guarantee in
            // its public contract (it uses memmove internally today, but that's
            // an implementation detail).
            Array.Copy(_window, shift, _window, 0, Window - shift);
            ushort last = _window[Window - shift - 1];
            for (int i = 0; i < shift; i++)
            {
                ushort v = (i < n) ? newSamples[i] : last;
                _window[Window - shift + i] = v;
                last = v;
            }
            _lastCurrent = _window[Window - 1];

            if (_paused) return;

            BuildPacket(_packetBuf, _seq++, _lastCurrent, _window);
            try
            {
                _stream.Write(_packetBuf);
            }
            catch
            {
                // On a write failure (device unplugged etc.) tear down the loop.
                _shuttingDown = true;
            }
        }

        private static void BuildPacket(byte[] pkt, byte seq, ushort current, ushort[] window)
        {
            Array.Clear(pkt, 0, PacketLen);
            pkt[0] = 0x01;          // HID report ID
            pkt[4] = 0x01;          // type: sample
            pkt[5] = seq;
            // bytes 6-9: current sample duplicated (LE)
            pkt[6] = (byte)(current & 0xFF);
            pkt[7] = (byte)(current >> 8);
            pkt[8] = (byte)(current & 0xFF);
            pkt[9] = (byte)(current >> 8);
            pkt[10] = (byte)NewPerPacket;
            pkt[11] = 0x0d;         // constant byte from captures
            // bytes 12..63: 13 slots of u16 LE duplicated (oldest first)
            for (int i = 0; i < Window; i++)
            {
                int p = 12 + i * 4;
                ushort v = window[i];
                pkt[p + 0] = (byte)(v & 0xFF);
                pkt[p + 1] = (byte)(v >> 8);
                pkt[p + 2] = (byte)(v & 0xFF);
                pkt[p + 3] = (byte)(v >> 8);
            }
        }

        public void Dispose()
        {
            try { StopStream(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }

        // ---------- timing helpers ----------

        private static void PrecisionSleepUs(int microseconds)
        {
            if (microseconds <= 0) return;
            // For init packet pacing (~2 ms), Thread.Sleep(2) under a 1 ms timer
            // resolution is close enough. Use Stopwatch to enforce a minimum.
            long target = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * microseconds) / 1_000_000L;
            int ms = microseconds / 1000;
            if (ms > 0) Thread.Sleep(ms);
            while (Stopwatch.GetTimestamp() < target) Thread.SpinWait(32);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uPeriod);
    }
}
