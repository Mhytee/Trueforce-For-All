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

namespace TrueforceForAll.Core
{
    public sealed class TrueforceDevice : IDisposable
    {
        public const int PacketLen = 64;
        public const int Window = 13;          // total slots in rolling window
        public const int NewPerPacket = 4;     // new samples shifted in per packet
        // 1000 packet/s × 4 new samples = 4 kHz audio-haptic rate. This is what
        // AC EVO empirically does on this wheel; at that rate the wheel firmware
        // allows our content to coexist with DirectInput FFB provided per-sample
        // amplitudes are small (≈ ±0.6% of full scale). mescon's docs claim
        // 250 pps / 1 kHz, but at that rate any audibly-felt amplitude trips
        // the wheel into Trueforce-dominant mode and FFB attenuates.
        public const int PacketHz = 1000;
        // 16 samples = 4 ms at 4 kHz. The ring naturally stays near-full in
        // steady state (producer back-pressures on PushFloats), so its depth
        // sets the audio latency. With timeBeginPeriod(1) and AboveNormal
        // priority on both producer and stream threads, StreamTick is reliable
        // to ~1 ms, so 4 ms gives ~3 ms of jitter headroom — tight but workable.
        // If underruns appear (audible dropouts), bump back to 32.
        public const int RingSize = 16;        // power of two

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

        // Optional FFB target source. Returns AC's most-recent FFB target as a
        // signed int16 if it was captured within FfbTargetMaxAgeMs, or null
        // otherwise. We use this as cur (bytes 6-9) for active packets so AC's
        // FFB drives the motor while our audio overlays in the rolling window.
        //
        // Threshold is large (10 seconds) because AC drops its HID++ FFB update
        // rate dramatically when the FFB target hasn't changed (stationary wheel,
        // straight road) — a tight threshold makes us flap between active and
        // keepalive on every quiet moment, which drops Trueforce audio. The
        // wheel firmware itself maintains the last-commanded force indefinitely
        // when AC stops sending updates, so mirroring that semantic is correct.
        public Func<short?> FfbTargetProvider { get; set; }
        public int FfbTargetMaxAgeMs { get; set; } = 10000;

        // FFB pass-through tuning. AC's HID++ feature 0x0e and the wheel's ep3
        // cur field use OPPOSITE sign conventions — empirically: turning right
        // and releasing produces a centering force in AC at negative LSBs, but
        // when copied as-is into ep3 cur the motor pulls in the direction of
        // the last input rather than toward center. So we negate by default.
        // FfbScale lets the user adjust felt strength if the wheel firmware
        // applies different gain to ep3 cur vs ep0 PID FFB (1.0 = identity).
        public bool  FfbInvertSign { get; set; } = true;
        public float FfbScale      { get; set; } = 1.0f;

        // IIR low-pass time constant (ms) applied to the captured FFB target
        // before it goes into ep3 cur. AC's HID++ FFB updates at ~140 Hz (every
        // 7 ms) but our StreamTick runs at 1 kHz, so without smoothing we'd
        // emit a 7-step staircase: same value 7 packets in a row, then a step.
        // The wheel motor "feels" steps as a faint mechanical tick. Smoothing
        // converts the staircase into a ramp; tradeoff is ~tau ms of lag.
        // 0 = no smoothing (sample-and-hold). 3 ms is a reasonable default.
        public float FfbSmoothTimeConstantMs { get; set; } = 3.0f;
        private float _smoothedFfb;

        // Force-active override. When the deadline is in the future, StreamTick
        // emits active packets even if the FFB tap is stale — so the settings
        // UI's "Test" button can drive audio through the wheel while AC isn't
        // running (otherwise we'd be in keepalive mode and the test would
        // be silent). Set via ForceActiveFor(durationMs).
        private long _forceActiveUntilTicks;
        public void ForceActiveFor(int durationMs)
        {
            long endTicks = DateTime.UtcNow.Ticks + durationMs * TimeSpan.TicksPerMillisecond;
            System.Threading.Interlocked.Exchange(ref _forceActiveUntilTicks, endTicks);
        }

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

        // Protocol-level mode commands. Per mescon's protocol doc:
        //   type 0x04 = stop/clear (init packet #67)  → wheel returns to its
        //               internal FFB-only mode; further sample packets ignored.
        //   type 0x03 = start/play (init packet #68)  → wheel re-enters
        //               Trueforce-active mode; sample packets drive the motor.
        // We queue a one-byte intent here; StreamTick fires the actual command
        // packet on its next tick (single-threaded write to the device).
        private volatile int _pendingCommand;   // 0 = none, 0x03 = start, 0x04 = stop
        public void SendStartCommand() { _pendingCommand = 0x03; }
        public void SendStopCommand()  { _pendingCommand = 0x04; }

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
            // Dispatch any pending protocol-level mode command first. We send
            // the command packet, update _paused, and skip the sample packet
            // for this tick to give the wheel a clean state-transition moment.
            int cmd = System.Threading.Interlocked.Exchange(ref _pendingCommand, 0);
            if (cmd != 0)
            {
                int templateIdx = (cmd == 0x04) ? 66 : 67;       // packet #67 / #68 (0-indexed)
                Buffer.BlockCopy(InitData.Packets[templateIdx], 0, _packetBuf, 0, PacketLen);
                _packetBuf[InitData.SeqOffset] = _seq++;
                try { _stream.Write(_packetBuf); }
                catch { _shuttingDown = true; return; }
                _paused = (cmd == 0x04);
                return;
            }

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

            // Two packet shapes the wheel firmware distinguishes (observed empirically
            // by diffing AC EVO's stream vs ours):
            //   "active"  bytes[10..11] = 04 0d, cur (bytes 6-9) drives the motor
            //             directly, window carries 4 new audio samples for overlay.
            //             While streaming active packets, the wheel uses cur as the
            //             motor torque target — overriding AC's ep0 HID++ FFB.
            //   "keepalive" bytes[10..11] = 00 00, window all zeros, cur=0x8000.
            //             The wheel ignores us and uses its normal ep0 HID++ FFB path.
            //
            // Decision: send "active" whenever the FFB tap has a fresh value. We
            // STAY in active mode continuously while AC is running, regardless of
            // whether Trueforce audio is currently playing — empirically, the
            // wheel's motor feel differs between ep0 PID FFB (keepalive mode) and
            // ep3 cur (active mode), and switching between them at audio start/end
            // is felt as "jerky" FFB. Window carries audio if we have any, else
            // silence-center samples (additive zero — wheel feels only cur).
            // Keepalive only fires when the FFB tap is stale (AC closed / idle
            // > FfbTargetMaxAgeMs), so any other game's native FFB still works
            // when our plugin is running but AC isn't.
            bool hasAudio = (n > 0);
            if (hasAudio)
            {
                bool allCenter = true;
                for (int i = 0; i < n; i++)
                {
                    if (newSamples[i] != 0x8000) { allCenter = false; break; }
                }
                if (allCenter) hasAudio = false;
            }

            if (_paused) return;

            short? ffbTargetMaybe = FfbTargetProvider?.Invoke();
            bool forceActive = DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _forceActiveUntilTicks);
            bool sendActive = ffbTargetMaybe.HasValue || forceActive;

            if (sendActive)
            {
                if (hasAudio)
                {
                    // Shift the window left by NewPerPacket and append new audio samples.
                    const int shift = NewPerPacket;
                    Array.Copy(_window, shift, _window, 0, Window - shift);
                    ushort last = _window[Window - shift - 1];
                    for (int i = 0; i < shift; i++)
                    {
                        ushort v = (i < n) ? newSamples[i] : last;
                        _window[Window - shift + i] = v;
                        last = v;
                    }
                }
                else
                {
                    // No audio content — fill the window with silence-center so
                    // the wheel's audio overlay contributes zero force, leaving
                    // only cur as the motor torque target.
                    for (int i = 0; i < Window; i++) _window[i] = 0x8000;
                }

                // IIR low-pass: ramp _smoothedFfb toward the latest captured FFB
                // at a rate set by the user-tunable time constant. Mathematically
                // equivalent to interpolating between AC's 7ms-spaced HID++ FFB
                // updates (which we'd otherwise emit as a step waveform).
                // When sendActive triggered via forceActive (test mode without
                // AC running), ffbTargetMaybe is null — fall back to 0x8000.
                ushort ffbCur = (ushort)0x8000;
                if (ffbTargetMaybe.HasValue)
                {
                    float raw = ffbTargetMaybe.Value;
                    float tau = FfbSmoothTimeConstantMs;
                    if (tau > 0f)
                    {
                        float alpha = 1f / (tau + 1f);
                        _smoothedFfb = _smoothedFfb * (1f - alpha) + raw * alpha;
                    }
                    else
                    {
                        _smoothedFfb = raw;
                    }

                    int t = (int)Math.Round(_smoothedFfb);
                    if (FfbInvertSign) t = -t;
                    if (FfbScale != 1.0f) t = (int)(t * FfbScale);
                    if (t >  32767) t =  32767;
                    if (t < -32768) t = -32768;
                    ffbCur = (ushort)(t + 0x8000);
                }
                _lastCurrent = ffbCur;
                BuildPacket(_packetBuf, _seq++, ffbCur, _window);
            }
            else
            {
                // FFB tap stale (AC closed / idle). Step out of the way and let
                // any native FFB through.
                for (int i = 0; i < Window; i++) _window[i] = 0x8000;
                _lastCurrent = 0x8000;
                _smoothedFfb = 0f;
                BuildSilentPacket(_packetBuf, _seq++);
            }

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

        // EVO-style silent keepalive: NewPerPacket=0, window literal zeros, cur=0x8000.
        // The wheel reads bytes[10..11] = 00 00 as "no audio in this packet, stay
        // available for DirectInput FFB on ep1."
        private static void BuildSilentPacket(byte[] pkt, byte seq)
        {
            Array.Clear(pkt, 0, PacketLen);
            pkt[0] = 0x01;
            pkt[4] = 0x01;
            pkt[5] = seq;
            pkt[6] = 0x00; pkt[7] = 0x80;     // cur = 0x8000 (silence center)
            pkt[8] = 0x00; pkt[9] = 0x80;
            // bytes 10..63 stay zero (Array.Clear above) - matches EVO silent format
        }

        private static void BuildPacket(byte[] pkt, byte seq, ushort current, ushort[] window)
        {
            Array.Clear(pkt, 0, PacketLen);
            pkt[0] = 0x01;          // HID report ID
            pkt[4] = 0x01;          // type: sample
            pkt[5] = seq;
            // bytes 6-9: current Trueforce sample (duplicated as two u16 LE).
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
