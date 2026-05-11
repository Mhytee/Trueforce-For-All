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
        // 1000 packet/s × 4 new samples = 4 kHz audio-haptic rate. AC EVO
        // empirically streams at this rate; we match it. mescon's docs note
        // 250 pps / 1 kHz. Separately, we observed that audibly-felt
        // Trueforce amplitudes coexist with ep0 DirectInput FFB at small
        // per-sample amplitudes (≈ ±0.6% of full scale) but override ep0
        // FFB at higher amplitudes — we have not isolated whether packet
        // rate, amplitude, or both determine the coexistence regime.
        public const int PacketHz = 1000;
        // 8 samples = 2 ms at 4 kHz. The ring naturally stays near-full in
        // steady state (producer back-pressures on PushFloats), so its depth
        // sets the audio latency floor. With timeBeginPeriod(1), Highest
        // priority on the stream thread, and AboveNormal on the producer,
        // StreamTick is reliable to <1 ms, so 2 ms gives ~1 ms of jitter
        // headroom — aggressive but appropriate for high-bandwidth haptics.
        // If underruns appear (audible clicks during heavy GC / system load),
        // the auto-ratchet bumps capacity up one notch to 16, 32, or 64.
        // Backing array is sized to MaxRingSize so SetRingCapacity can resize
        // live; only `_ringCapacity` slots are in use at any moment. Capacity
        // must be a power of two (head/tail wrap with `& (cap - 1)`).
        public const int MaxRingSize     = 64;       // power of two
        public const int MinRingSize     = 8;        // power of two
        public const int DefaultRingSize = 8;        // matches Performance defaults

        private int _ringCapacity = DefaultRingSize;
        public int RingCapacity => System.Threading.Volatile.Read(ref _ringCapacity);

        private const int InitInterPacketUs = 2000; // 2 ms between init packets

        private readonly HidDevice _hidDevice;
        private HidStream _stream;
        private Thread _streamThread;

        private readonly object _streamLock = new object();
        private volatile bool _streamRunning;
        private volatile bool _shuttingDown;
        private volatile bool _paused;
        // Set false by StopAcceptingSamples() to release blocked PushFloats /
        // PushInt16 callers ahead of full shutdown — lets the host drain the
        // producer without also halting the stream thread (which still needs
        // to push centre-wheel quietness samples to the wheel before Dispose).
        private volatile bool _acceptingSamples = true;

        private byte _seq;

        // 13-slot rolling window of u16 offset-binary samples (newest at index Window-1).
        private readonly ushort[] _window = new ushort[Window];
        private ushort _lastCurrent = 0x8000;

        // Single-producer / single-consumer ring buffer. Indices wrap mod
        // _ringCapacity (always a power of two). Backing array is always
        // MaxRingSize so SetRingCapacity can resize live without reallocating.
        // Samples are stored as offset-binary u16.
        private readonly ushort[] _ring = new ushort[MaxRingSize];
        private int _ringHead;  // producer index
        private int _ringTail;  // consumer index
        private readonly object _ringLock = new object();

        // Underrun = StreamTick wanted NewPerPacket samples but got 0.
        // Counts are duration-quantized: the producer-visible counter only
        // ticks once per UnderrunQuantumTicks of continuous starvation, so a
        // sub-quantum scheduling blip contributes 0 and a longer stall
        // contributes one count per quantum (severity preserved through
        // proportional count). Only active after the producer has ever
        // delivered a sample (so cold-start ticks before the first push
        // don't inflate the counter). Used by the plugin's auto-ratchet to
        // bump _ringCapacity up on persistent loss; the quantum filters
        // trivial scheduling noise so the ratchet threshold maps to "real"
        // dropout time, not raw tick count.
        //
        // At StreamTick's 1 kHz cadence, 20 ticks = 20 ms.
        private const int UnderrunQuantumTicks = 20;
        private long _underrunCount;
        private bool _everReceivedSample;
        private int _currentUnderrunStreak;
        public long UnderrunCount => System.Threading.Interlocked.Read(ref _underrunCount);

        // Reusable packet buffer (re-zeroed on each tick).
        private readonly byte[] _packetBuf = new byte[PacketLen];

        // Reusable scratch for samples drained from the ring each StreamTick.
        // Single-threaded use (only StreamTick touches it) so no sync needed.
        private readonly ushort[] _newSamplesScratch = new ushort[NewPerPacket];

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
        // 7 ms) but our StreamTick runs at 1 kHz, so smoothing > 0 turns the
        // 7-step staircase into a ramp at the cost of ~tau ms of group delay.
        // 0 = no smoothing (sample-and-hold) — chosen as default to prioritize
        // FFB responsiveness; users who feel the staircase as a mechanical tick
        // can dial in 1-3 ms via the slider.
        public float FfbSmoothTimeConstantMs { get; set; } = 0.0f;
        private float _smoothedFfb;

        // FFB spike taming: gates both the slew-rate limiter
        // (FfbSpikeMaxLsbPerMs) and the spike-attenuation cap
        // (FfbPeakSoftLimitLsb). When the gate is off, both are bypassed
        // regardless of their stored values, so users can flip the feature
        // off without losing their tuning. Default off; turned on per-game
        // via the AC built-in preset, or by the user via the UI checkbox.
        public bool FfbSpikeTamingEnabled { get; set; } = false;

        // Slew-rate limit (LSB per ms) applied to the captured FFB target
        // BEFORE the smoothing IIR. Caps how fast the input can change in
        // either direction, so a sudden curb hit (which AC sends as a single
        // large step) gets spread over several ms and lands as a firm push
        // instead of a jolt that yanks the wheel out of your hands. Lets
        // users run a higher FFB scale safely (same average force, much
        // softer peaks). Active only when FfbSpikeTamingEnabled is true.
        // Tick rate is ~1 kHz so the LSB/ms value also approximates max
        // delta per tick.
        public float FfbSpikeMaxLsbPerMs { get; set; } = 2060.923f;
        private float _slewLimitedFfb;

        // Spike-attenuation cap. Detection sidechains off RAW input slew rate
        // (rate of change in LSB/ms): a curb / wall hit changes FFB at
        // 4000-15000+ LSB/ms while normal cornering inputs change at
        // 100-500 LSB/ms. Above SpikeSlewThresholdLsbPerMs we attenuate; at
        // slew = threshold + cap, gain factor = 0.5; as slew grows, factor
        // approaches 0. Active only when FfbSpikeTamingEnabled is true.
        //
        // Crucially, raw slew alone can't distinguish a wall hit (one big
        // unidirectional step) from a rumble strip (rapid +/-/+/- oscillation
        // around the same average force) — both produce huge slew. We gate
        // the slew-envelope update on a DIRECTIONALITY ratio (see
        // _sumDeltas / _sumAbsDeltas below): unidirectional events ride at
        // ~1.0, alternating-sign rumble drops to ~0.1-0.3. Slew only counts
        // when directionality is high, so the envelope stays low through
        // kerb buzz and pops on real impacts.
        public float FfbPeakSoftLimitLsb { get; set; } = 1561.78564f;
        // Below this slew rate, no attenuation regardless of cap setting.
        // 1000 LSB/ms is well above the rates produced by even hard cornering
        // and well below typical curb-hit slew. Hardcoded; could be exposed
        // if a game's cornering forces exceed this baseline.
        private const float SpikeSlewThresholdLsbPerMs = 1000f;
        // Directionality threshold in [0, 1]. Ratio of |sum of recent signed
        // deltas| to sum of |recent deltas|. 1.0 = every recent delta the
        // same sign (clean unidirectional shift); 0.0 = perfectly alternating.
        // 0.5 cleanly separates real spikes (typically 0.7-1.0 sustained
        // through the impact) from rumble oscillation (0.1-0.3 sustained).
        private const float SpikeDirectionalityMin = 0.5f;
        // Decay applied per tick to both signed and absolute delta sums.
        // ~10 ms time constant: long enough that 2-3 oscillation cycles of
        // a 100 Hz rumble are in the window (so the alternating-sign cancel
        // is observable), short enough that a clean step's directionality
        // stays high through the entire envelope-rise.
        private const float DirectionalityDecayPerTick = 0.909f;  // ~ 1 - 1/11 (TC ≈ 10 ms)
        // Half-life of the spike envelope in ms. Sets how long attenuation
        // persists after the actual slew event. AC sustains a curb-hit's
        // elevated force for ~50-100 ms; this half-life keeps attenuation
        // active through the whole impact rather than just the slew moment.
        private const float SpikeEnvHalfLifeMs = 70f;
        // Per-tick decay factor for the spike envelope. Both inputs to Math.Pow
        // are constants, so precompute once.
        private static readonly float SpikeEnvDecayPerTick =
            (float)Math.Pow(0.5, 1.0 / SpikeEnvHalfLifeMs);
        private float _prevRawForSlew;
        // Directionality state: exponentially-decayed sums of signed and
        // absolute raw-FFB deltas. Their ratio gives the directionality
        // metric in [0, 1]. Reset in ResetFfbFilters.
        private float _sumDeltas;
        private float _sumAbsDeltas;
        private float _spikeSlewEnv;

        // Force-active override. When the deadline is in the future, StreamTick
        // emits active packets even if the FFB tap is stale — so the settings
        // UI's "Test" button can drive audio through the wheel while AC isn't
        // running (otherwise we'd be in keepalive mode and the test would
        // be silent). Set via ForceActiveFor(durationMs).
        // Stored in Stopwatch.GetTimestamp() ticks (monotonic, immune to
        // wall-clock jumps from NTP / DST / manual changes).
        private long _forceActiveUntilTicks;
        public void ForceActiveFor(int durationMs)
        {
            long endTicks = Stopwatch.GetTimestamp() + durationMs * Stopwatch.Frequency / 1000;
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
                    // Highest (vs AboveNormal on the producer) so that on a
                    // contended system — Chrome update kicking in, antivirus
                    // scan, etc. — packet emission keeps its 1 kHz cadence.
                    // Underruns here are felt as audible clicks; the producer
                    // can absorb a missed cycle via the ring buffer.
                    Priority = ThreadPriority.Highest,
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

        /// <summary>Live-resize the ring buffer. <paramref name="newCapacity"/>
        /// must be a power of two in [MinRingSize, MaxRingSize]; the backing
        /// array is already sized to MaxRingSize so no allocation occurs.
        /// Drains any in-flight samples (head/tail reset to 0) — produces
        /// at most ~1 ms of audible silence at the wheel, vs. needing to
        /// stop and restart the stream which would be ~50 ms of silence.
        /// Wakes blocked producers so they observe the new free count.</summary>
        public void SetRingCapacity(int newCapacity)
        {
            if (newCapacity < MinRingSize || newCapacity > MaxRingSize)
                throw new ArgumentOutOfRangeException(nameof(newCapacity),
                    $"must be in [{MinRingSize}, {MaxRingSize}]");
            if ((newCapacity & (newCapacity - 1)) != 0)
                throw new ArgumentException("must be a power of two", nameof(newCapacity));

            lock (_ringLock)
            {
                if (_ringCapacity == newCapacity) return;
                _ringCapacity = newCapacity;
                _ringHead = 0;
                _ringTail = 0;
                Monitor.PulseAll(_ringLock);
            }
        }

        // Stop accepting new samples and wake any producer parked in PushFloats
        // so it can observe the application's shutdown signal. Leaves the
        // internal stream thread running so any samples already queued — plus
        // the centre-wheel quietness pulse a subsequent ClearStream queues —
        // still drain to the wheel before Dispose tears the HID stream down.
        public void StopAcceptingSamples()
        {
            _acceptingSamples = false;
            lock (_ringLock) { Monitor.PulseAll(_ringLock); }
        }

        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;

        // Clear FFB filter state. Called on car / game switch so the new car's
        // first frames don't get blended with the previous car's last sample
        // through the IIR / slew / spike-envelope chain.
        public void ResetFfbFilters()
        {
            _smoothedFfb     = 0f;
            _slewLimitedFfb  = 0f;
            _prevRawForSlew  = 0f;
            _sumDeltas       = 0f;
            _sumAbsDeltas    = 0f;
            _spikeSlewEnv    = 0f;
        }

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
                    while (RingFreeUnlocked() == 0 && _streamRunning && !_shuttingDown && _acceptingSamples)
                        Monitor.Wait(_ringLock);
                    if (_shuttingDown || !_streamRunning || !_acceptingSamples) return;

                    _ring[_ringHead & (_ringCapacity - 1)] = FloatToWire(samples[i]);
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
                    while (RingFreeUnlocked() == 0 && _streamRunning && !_shuttingDown && _acceptingSamples)
                        Monitor.Wait(_ringLock);
                    if (_shuttingDown || !_streamRunning || !_acceptingSamples) return;

                    _ring[_ringHead & (_ringCapacity - 1)] = S16ToWire(samples[i]);
                    _ringHead++;
                }
                Monitor.PulseAll(_ringLock);
            }
        }

        // ---------- internals ----------

        private int RingOccupiedUnlocked() => (_ringHead - _ringTail) & (_ringCapacity - 1);
        private int RingFreeUnlocked()     => _ringCapacity - 1 - RingOccupiedUnlocked();

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
            ushort[] newSamples = _newSamplesScratch;
            int n = 0;
            lock (_ringLock)
            {
                while (n < NewPerPacket && _ringTail != _ringHead)
                {
                    newSamples[n++] = _ring[_ringTail & (_ringCapacity - 1)];
                    _ringTail++;
                }
                if (n > 0) Monitor.PulseAll(_ringLock);
            }

            // Underrun bookkeeping. Track the current continuous-starvation
            // streak; emit one count to the public counter every full
            // UnderrunQuantumTicks the streak extends. Sub-quantum blips
            // never tick the counter (streak resets to 0 on the next good
            // tick). Only active once the producer has ever delivered a
            // sample (so cold-start ticks before the first push don't
            // inflate the counter), and only when the stream is actually
            // expecting to play (not paused / shutting down).
            if (n > 0)
            {
                _everReceivedSample = true;
                _currentUnderrunStreak = 0;
            }
            else if (_everReceivedSample && _streamRunning && !_paused && !_shuttingDown)
            {
                _currentUnderrunStreak++;
                if (_currentUnderrunStreak % UnderrunQuantumTicks == 0)
                    System.Threading.Interlocked.Increment(ref _underrunCount);
            }

            // Two packet shapes we send (observed by diffing AC EVO's stream vs
            // silent baselines — these three things change together; we have not
            // isolated which the wheel actually keys off):
            //   "active"  bytes[10..11] = 04 0d, cur (bytes 6-9) carries the
            //             FFB target, window carries 4 new audio samples.
            //             When streaming this shape, the wheel uses cur as the
            //             motor torque target and ep0 HID++ FFB has no effect.
            //   "keepalive" bytes[10..11] = 00 00, window all zeros, cur=0x8000.
            //             When streaming this shape, the wheel uses its normal
            //             ep0 HID++ FFB path.
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
            bool forceActive = Stopwatch.GetTimestamp() < System.Threading.Interlocked.Read(ref _forceActiveUntilTicks);
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

                    // Update slew-rate sidechain. Peak-follow on rise (instant
                    // latch) + slow exponential decay (70 ms half-life) on
                    // fall. Latching means a single-tick spike is captured
                    // at full magnitude; decay means the env stays high for
                    // the duration AC actually sustains the elevated force.
                    //
                    // Directionality gate: rumble strips drive raw with rapid
                    // alternating-sign deltas of similar magnitude — the
                    // signed-sum cancels but the abs-sum doesn't, so
                    // directionality drops to ~0.1-0.3 and we ignore the
                    // (otherwise huge) raw slew. A real wall hit is
                    // unidirectional, directionality stays high (~0.7-1.0),
                    // and the slew event registers in full.
                    float deltaRaw = raw - _prevRawForSlew;
                    _prevRawForSlew = raw;
                    float slewInst = Math.Abs(deltaRaw);
                    _sumDeltas    = _sumDeltas    * DirectionalityDecayPerTick + deltaRaw;
                    _sumAbsDeltas = _sumAbsDeltas * DirectionalityDecayPerTick + slewInst;
                    float directionality = (_sumAbsDeltas > 1f)
                        ? Math.Abs(_sumDeltas) / _sumAbsDeltas
                        : 0f;
                    // Only let directional slew set the envelope. Non-
                    // directional slew (rumble) decays the envelope as if
                    // nothing happened, so a kerb traversal never builds
                    // sustained attenuation.
                    bool directional = directionality >= SpikeDirectionalityMin;
                    if (directional && slewInst > _spikeSlewEnv)
                    {
                        _spikeSlewEnv = slewInst;
                    }
                    else
                    {
                        _spikeSlewEnv *= SpikeEnvDecayPerTick;
                    }

                    // Slew-rate limit: caps the input step a curb hit can
                    // produce. Smoothing afterwards turns the clamped step
                    // into a soft ramp, so a violent AC curb impact lands as
                    // a firm shove instead of a wheel-yank. Bypassed when the
                    // spike-taming gate is off, regardless of stored value.
                    float maxDelta = FfbSpikeTamingEnabled ? FfbSpikeMaxLsbPerMs : 0f;
                    if (maxDelta > 0f)
                    {
                        float delta = raw - _slewLimitedFfb;
                        if (delta >  maxDelta) delta =  maxDelta;
                        else if (delta < -maxDelta) delta = -maxDelta;
                        _slewLimitedFfb += delta;
                    }
                    else
                    {
                        _slewLimitedFfb = raw;
                    }

                    float tau = FfbSmoothTimeConstantMs;
                    if (tau > 0f)
                    {
                        float alpha = 1f / (tau + 1f);
                        _smoothedFfb = _smoothedFfb * (1f - alpha) + _slewLimitedFfb * alpha;
                    }
                    else
                    {
                        _smoothedFfb = _slewLimitedFfb;
                    }

                    int t = (int)Math.Round(_smoothedFfb);
                    if (FfbInvertSign) t = -t;
                    if (FfbScale != 1.0f) t = (int)(t * FfbScale);

                    // Multiplicative spike attenuation. Triggers when the
                    // peak-followed slew envelope exceeds SpikeSlewThreshold
                    // — anything below is normal cornering / steering input
                    // and passes through at full amp. Above threshold,
                    // factor = cap / (cap + slewExcess) asymptotes to 0 as
                    // slew grows; lower cap = stronger attenuation per LSB
                    // of slew excess. The env's slow decay extends the
                    // attenuation window for ~200-300 ms after a spike, so
                    // both the rise and AC's sustained "elevated force"
                    // phase are attenuated.
                    float spikeCap = FfbSpikeTamingEnabled ? FfbPeakSoftLimitLsb : 0f;
                    if (spikeCap > 0f && _spikeSlewEnv > SpikeSlewThresholdLsbPerMs)
                    {
                        float slewExcess = _spikeSlewEnv - SpikeSlewThresholdLsbPerMs;
                        float factor = spikeCap / (spikeCap + slewExcess);
                        t = (int)(t * factor);
                    }

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
        // When we send this shape the wheel uses its normal ep0 HID++ FFB path
        // (we haven't isolated which of bytes 10-11 / cur=0x8000 / zero window
        // is the actual trigger — they covary in EVO's captures).
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
