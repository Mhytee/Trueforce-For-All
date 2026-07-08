// Trueforce session + audio-haptic stream.
//
// Ported from mescon/logitech-rs50-linux-driver:
//   userspace/libtrueforce/src/session.c   (Open + InitSequence)
//   userspace/libtrueforce/src/stream.c    (ring buffer + 250 Hz packet pump)
//
// The Trueforce stream is 64-byte HID output reports, each carrying a
// 13-slot rolling sample window (4 new samples added per packet), so
// sample rate = packet rate × 4. The packet rate is game-chosen, not a
// firmware requirement (confirmed by mescon, 2026-07): his ACC captures
// run 250-500 packets/s, our AC EVO capture runs 1000, and the wheel
// accepts the whole band. Rates above 1000 are unobserved.

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
        // empirically streams at this rate; we match it. Confirmed (mescon,
        // 2026-07): the rate is game-chosen and the wheel accepts at least
        // 250-1000 packet/s (his ACC captures run 250-500). We stay at 1000
        // by choice: the per-tick filter constants and the ring latency math
        // assume 1 tick = 1 ms, and a lower cadence would add delivery
        // latency. Separately, we observed that audibly-felt Trueforce
        // amplitudes coexist with ep0 DirectInput FFB at small per-sample
        // amplitudes (≈ ±0.6% of full scale) but override ep0 FFB at higher
        // amplitudes, we have not isolated whether packet rate, amplitude,
        // or both determine the coexistence regime (the rate-band
        // confirmation does not answer this).
        public const int PacketHz = 1000;
        // 8 samples = 2 ms at 4 kHz. The ring naturally stays near-full in
        // steady state (producer back-pressures on PushFloats), so its depth
        // sets the audio latency floor. With timeBeginPeriod(1), Highest
        // priority on the stream thread, and AboveNormal on the producer,
        // StreamTick is reliable to <1 ms, so 2 ms gives ~1 ms of jitter
        // headroom, aggressive but appropriate for high-bandwidth haptics.
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
        // Set true by StreamTick when a packet Write throws (wheel unplugged,
        // G HUB grabbed the HID, USB stall). Distinguishes an involuntary
        // stream death from a clean StopStream(): both set _shuttingDown, but
        // only a fault sets this. Cleared by StartStream(). The plugin's
        // recovery watchdog polls StreamFaulted to know a re-attach is due.
        private volatile bool _streamFaulted;
        // Set false by StopAcceptingSamples() to release blocked PushFloats /
        // PushInt16 callers ahead of full shutdown, lets the host drain the
        // producer without also halting the stream thread (which still needs
        // to push centre-wheel quietness samples to the wheel before Dispose).
        private volatile bool _acceptingSamples = true;

        private byte _seq;

        // Monotonic count of stream packets actually written to the wheel. The
        // FFB tap's liveness watchdog reads this as a heartbeat: while we're
        // streaming (not paused), it advances at ~1 kHz, so if our capture sees
        // nothing while this is climbing, the capture is broken. Interlocked
        // because it's read from the tap's watchdog thread (and SimHub is
        // 32-bit, so a plain long read/write isn't atomic).
        private long _packetsSent;
        public long PacketsSent => System.Threading.Interlocked.Read(ref _packetsSent);

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
        // FFB drives the motor while our audio overlays in the rolling window
        // (cur = torque target, window = additive overlay: confirmed by
        // mescon's Linux driver on RS50, 2026-07).
        //
        // Threshold is large (10 seconds) because AC drops its HID++ FFB update
        // rate dramatically when the FFB target hasn't changed (stationary wheel,
        // straight road), a tight threshold makes us flap between active and
        // keepalive on every quiet moment, which drops Trueforce audio. The
        // wheel firmware itself maintains the last-commanded force indefinitely
        // when AC stops sending updates, so mirroring that semantic is correct.
        public Func<short?> FfbTargetProvider { get; set; }
        public int FfbTargetMaxAgeMs { get; set; } = 10000;

        // FFB pass-through tuning. AC's HID++ feature 0x0e and the wheel's ep3
        // cur field use OPPOSITE sign conventions, empirically: turning right
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
        // 0 = no smoothing (sample-and-hold), chosen as default to prioritize
        // FFB responsiveness; users who feel the staircase as a mechanical tick
        // can dial in 1-3 ms via the slider.
        public float FfbSmoothTimeConstantMs { get; set; } = 0.0f;
        private float _smoothedFfb;

        // FFB band-split (Forza road-feel fix). Some games (Forza especially)
        // bake road/surface texture into the steering force as high-frequency
        // content. Streamed straight to cur it jerks the wheel (jitter) instead
        // of being felt as rumble. When enabled, the FFB target is split: a
        // one-pole low-pass (FfbTextureCutoffMs time constant) keeps the smooth
        // low-frequency steering weight in cur; the high-frequency remainder is
        // the road texture, injected into the Trueforce window (the audio-haptic
        // overlay) scaled by FfbTextureGain, so it's felt as rumble where it
        // belongs. Off (default) => byte-identical to the pre-split behaviour.
        public bool  FfbBandSplitEnabled { get; set; } = false;
        public float FfbTextureGain      { get; set; } = 1.0f;
        public float FfbTextureCutoffMs  { get; set; } = 12.0f;
        private float _bandLow;    // LPF stage 1 of the split; stream thread only
        private float _bandLow2;   // LPF stage 2, cascaded for a steeper (~-12 dB/oct)
                                   // rolloff so cur keeps only the smooth steering
                                   // force; one pole left enough high-frequency road
                                   // texture in cur to still jitter the wheel.

        // FFB spike taming: gates both the slew-rate limiter
        // (FfbSpikeMaxLsbPerMs) and the spike-attenuation cap
        // (FfbPeakSoftLimitLsb). When the gate is off, both are bypassed
        // regardless of their stored values, so users can flip the feature
        // off without losing their tuning. Default off; turned on per-game
        // via the AC built-in preset, or by the user via the UI checkbox.
        public bool FfbSpikeTamingEnabled { get; set; } = false;

        // Algorithm switch (A/B experiment, will likely collapse to a single
        // path once one wins). True = pure slew-rate limiter (iRacing's
        // approach: cap dV/dt, no amplitude reduction). False = transient
        // detector that compares post-scale |t| against a slow-follower
        // envelope and soft-caps the excess. FfbSpikeMaxLsbPerMs is read as
        // an LSB/ms rate in slew mode, or an LSB magnitude threshold in
        // transient mode. FfbPeakSoftLimitLsb is only used by transient mode.
        public bool FfbSpikeUseSlewLimiter { get; set; } = true;

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
        // around the same average force), both produce huge slew. We gate
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

        // Slow-follower envelope of |t| (post-scale FFB magnitude). Drives
        // the transient detector for spike attenuation: only the excess of
        // current magnitude OVER this envelope counts as a spike, so
        // sustained heavy cornering (envelope catches up) passes through
        // unattenuated, while sudden jumps (envelope lags) get capped.
        // 200ms time constant: fast enough to track corner-to-corner
        // load changes (multi-second timescale), slow enough that crash
        // impacts (~50-100ms) don't pull the envelope up before the cap
        // engages. Same TC for attack and release for simplicity.
        private const float SustainedFfbTimeConstantMs = 200f;
        private static readonly float SustainedFfbAlpha =
            1f / (SustainedFfbTimeConstantMs + 1f);
        private float _sustainedFfbEnv;

        // Force-active override. When the deadline is in the future, StreamTick
        // emits active packets even if the FFB tap is stale, so the settings
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

        /// <summary>True when the stream loop died because a packet write
        /// threw (wheel unplugged, HID grabbed, USB stall), as opposed to a
        /// clean StopStream(). The plugin's recovery watchdog polls this to
        /// trigger a transparent re-attach.</summary>
        public bool StreamFaulted => _streamFaulted;

        /// <summary>Test hook (FAULT access code): simulate an involuntary
        /// stream death (unplug / HID grab / USB stall) so the plugin's
        /// recovery watchdog re-attaches, without physically unplugging.
        /// Sets the same flags the real write-failure path sets, so the
        /// StreamLoop tears down and StreamFaulted reports true.</summary>
        public void DebugForceStreamFault()
        {
            _streamFaulted = true;
            _shuttingDown  = true;
        }

        public void StartStream()
        {
            lock (_streamLock)
            {
                if (_streamRunning) return;
                _streamRunning = true;
                _shuttingDown = false;
                _streamFaulted = false;
                _paused = false;
                _streamThread = new Thread(StreamLoop)
                {
                    IsBackground = true,
                    Name = "TrueforceStream",
                    // Highest (vs AboveNormal on the producer) so that on a
                    // contended system, Chrome update kicking in, antivirus
                    // scan, etc., packet emission keeps its 1 kHz cadence.
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
        /// Drains any in-flight samples (head/tail reset to 0), produces
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
        // internal stream thread running so any samples already queued, plus
        // the centre-wheel quietness pulse a subsequent ClearStream queues
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
            _sustainedFfbEnv = 0f;
            _bandLow         = 0f;
            _bandLow2        = 0f;
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

            // How the 1 kHz pump waits for each beat. The choice is load-bearing
            // for ALL system audio, not just ours:
            //
            //   Primary (Win10 1803+): a high-resolution waitable timer. The thread
            //   SLEEPS to each beat, yielding its core, and we lift it into the
            //   MMCSS "Pro Audio" band so it still wakes promptly at real-time
            //   priority under load. This matches the synthesis / capture / stdout
            //   threads, which already sleep in Pro Audio without trouble.
            //
            //   Fallback (older Windows, or if the timer misbehaves): the legacy
            //   coarse-sleep-then-busy-spin at ThreadPriority.Highest, WITHOUT Pro
            //   Audio. A thread that busy-spins while in the Pro Audio band pins a
            //   core at the audio engine's own priority and starves audiodg, which
            //   stutters every app's audio (e.g. YouTube). So spin and Pro Audio
            //   must never combine: Pro Audio is only ever entered on the sleeping
            //   path below.
            IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            // Reject a timer that returns instantly (a wrong due-time sign/width
            // would degenerate the sleep into a hot loop that still pins the core).
            bool useTimer = timer != IntPtr.Zero && WaitableTimerBlocks(timer);

            IntPtr mmcss = IntPtr.Zero;
            if (useTimer)
            {
                uint mmcssTaskIndex = 0;
                mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref mmcssTaskIndex);
                if (mmcss != IntPtr.Zero) AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);
            }

            try
            {
                var sw = Stopwatch.StartNew();
                long periodTicks = Stopwatch.Frequency / PacketHz; // ticks per packet
                long oneMsTicks  = Stopwatch.Frequency / 1000;
                long nextTick = sw.ElapsedTicks + periodTicks;

                while (!_shuttingDown)
                {
                    StreamTick();

                    long remaining = nextTick - sw.ElapsedTicks;
                    if (remaining > 0)
                    {
                        if (useTimer)
                        {
                            // Sleep to the deadline. Due time is in 100 ns units;
                            // NEGATIVE means a relative delay. Convert the remaining
                            // Stopwatch ticks to 100 ns units.
                            long due = -(remaining * 10_000_000L / Stopwatch.Frequency);
                            if (due == 0)
                            {
                                // Sub-100 ns sliver; just spin it out to the deadline.
                                while (!_shuttingDown && sw.ElapsedTicks < nextTick)
                                    Thread.SpinWait(64);
                            }
                            else if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                                     && WaitForSingleObject(timer, 100) != WAIT_FAILED)
                            {
                                // Woke at (or, under load, a little after) the beat.
                            }
                            else
                            {
                                // Real timer failure: latch to the spin path for the
                                // rest of the loop so we never block on a dead timer,
                                // and leave Pro Audio so we never spin inside it.
                                useTimer = false;
                                if (mmcss != IntPtr.Zero)
                                {
                                    AvRevertMmThreadCharacteristics(mmcss);
                                    mmcss = IntPtr.Zero;
                                }
                                while (!_shuttingDown && sw.ElapsedTicks < nextTick)
                                    Thread.SpinWait(64);
                            }
                        }
                        else
                        {
                            // Legacy precise wait at ThreadPriority.Highest (NOT Pro
                            // Audio): coarse sleep to ~1 ms out, then spin the rest.
                            while (!_shuttingDown && (nextTick - sw.ElapsedTicks) > oneMsTicks)
                                Thread.Sleep(1);
                            while (!_shuttingDown && sw.ElapsedTicks < nextTick)
                                Thread.SpinWait(64);
                        }
                    }
                    nextTick += periodTicks;

                    // If we slipped more than one period (long stall), don't try to catch up
                    // by burst-writing, emit one packet per loop iteration.
                    if (sw.ElapsedTicks - nextTick > periodTicks)
                        nextTick = sw.ElapsedTicks + periodTicks;
                }
            }
            finally
            {
                if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
                if (timer != IntPtr.Zero) CloseHandle(timer);
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
                catch { _streamFaulted = true; _shuttingDown = true; return; }
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
            // silent baselines). Semantics confirmed by mescon (2026-07, second
            // implementation on real RS50 hardware): cur (bytes 6-9) is the
            // motor torque target, the window is a purely additive audio
            // overlay on top of it, and byte 10 (the new-sample count) is the
            // audio demultiplexer. cur is honored in BOTH shapes, and byte 10 /
            // window / cur do not need to covary: byte10=0 with a non-center
            // cur is a valid force-only packet (his driver's standard shape;
            // we do not currently send it).
            //   "active"  bytes[10..11] = 04 0d, cur (bytes 6-9) carries the
            //             FFB target, window carries 4 new audio samples.
            //             When streaming this shape, the wheel uses cur as the
            //             motor torque target and ep0 HID++ FFB has no effect.
            //   "keepalive" bytes[10..11] = 00 00, window all zeros, cur=0x8000.
            //             When streaming this shape, the wheel uses its normal
            //             ep0 HID++ FFB path.
            // STILL OPEN: which field gates that ep0 fallback (byte 10, center
            // cur, or something else). mescon's data cannot settle it: his
            // Linux driver owns the wheel exclusively, so concurrent ep0 game
            // FFB never occurs in his setup. Tap-mode coexistence depends on
            // the answer; do not send force-only packets in tap mode until it
            // is isolated on hardware.
            //
            // Decision: send "active" whenever the FFB tap has a fresh value. We
            // STAY in active mode continuously while AC is running, regardless of
            // whether Trueforce audio is currently playing, empirically, the
            // wheel's motor feel differs between ep0 PID FFB (keepalive mode) and
            // ep3 cur (active mode), and switching between them at audio start/end
            // is felt as "jerky" FFB. Window carries audio if we have any, else
            // silence-center samples (additive zero, wheel feels only cur).
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

            // An explicit test (ForceActiveFor) overrides the pause gate so the
            // Test buttons play even when something has paused the device (e.g.
            // the StopStreamOnPause gate left Trueforce mode). A normal pause
            // with no test still emits nothing.
            bool forceActive = Stopwatch.GetTimestamp() < System.Threading.Interlocked.Read(ref _forceActiveUntilTicks);
            if (_paused && !forceActive) return;

            short? ffbTargetMaybe = FfbTargetProvider?.Invoke();
            bool sendActive = ffbTargetMaybe.HasValue || forceActive;

            if (sendActive)
            {
                // FFB band-split. When enabled, pull the high-frequency road
                // texture out of the FFB target: _bandLow (one-pole LPF) is the
                // smooth low-frequency steering weight that drives cur; the
                // remainder is the texture, injected into the window below as
                // rumble. OFF => bandTexture stays 0, the original window branches
                // run, and cur uses the raw target (byte-identical to before).
                int bandTexture = 0;
                bool splitActive = FfbBandSplitEnabled && ffbTargetMaybe.HasValue;
                if (splitActive)
                {
                    float r = ffbTargetMaybe.Value;
                    float a = 1f / (FfbTextureCutoffMs + 1f);
                    // Two cascaded one-pole low-passes (~-12 dB/oct) so cur keeps
                    // only the smooth steering force; a single pole left enough
                    // high-frequency road texture in cur to jitter the wheel.
                    _bandLow  += (r - _bandLow) * a;
                    _bandLow2 += (_bandLow - _bandLow2) * a;
                    int tex = (int)((r - _bandLow2) * FfbTextureGain);
                    if (tex > 32767) tex = 32767; else if (tex < -32768) tex = -32768;
                    bandTexture = tex;
                }

                if (splitActive)
                {
                    // Shift the window and append new samples = audio (if any) plus
                    // the road texture, summed in signed space and re-centred at
                    // 0x8000. With no audio this carries the texture alone.
                    const int shift = NewPerPacket;
                    Array.Copy(_window, shift, _window, 0, Window - shift);
                    ushort last = _window[Window - shift - 1];
                    for (int i = 0; i < shift; i++)
                    {
                        int audioSigned = hasAudio ? ((i < n ? newSamples[i] : last) - 0x8000) : 0;
                        int mixed = audioSigned + bandTexture;
                        if (mixed > 32767) mixed = 32767; else if (mixed < -32768) mixed = -32768;
                        ushort v = (ushort)(mixed + 0x8000);
                        _window[Window - shift + i] = v;
                        last = v;
                    }
                }
                else if (hasAudio)
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
                    // No audio content, fill the window with silence-center so
                    // the wheel's audio overlay contributes zero force, leaving
                    // only cur as the motor torque target (additive-overlay
                    // semantics confirmed on RS50 by mescon, 2026-07).
                    for (int i = 0; i < Window; i++) _window[i] = 0x8000;
                }

                // IIR low-pass: ramp _smoothedFfb toward the latest captured FFB
                // at a rate set by the user-tunable time constant. Mathematically
                // equivalent to interpolating between AC's 7ms-spaced HID++ FFB
                // updates (which we'd otherwise emit as a step waveform).
                // When sendActive triggered via forceActive (test mode without
                // AC running), ffbTargetMaybe is null, fall back to 0x8000.
                ushort ffbCur = (ushort)0x8000;
                if (ffbTargetMaybe.HasValue)
                {
                    // When band-splitting, cur is driven by the smooth low-
                    // frequency part only (the texture went to the window above);
                    // otherwise by the raw target, exactly as before.
                    float raw = splitActive ? _bandLow2 : (float)ffbTargetMaybe.Value;

                    // Update slew-rate sidechain. Peak-follow on rise (instant
                    // latch) + slow exponential decay (70 ms half-life) on
                    // fall. Latching means a single-tick spike is captured
                    // at full magnitude; decay means the env stays high for
                    // the duration AC actually sustains the elevated force.
                    //
                    // Directionality gate: rumble strips drive raw with rapid
                    // alternating-sign deltas of similar magnitude, the
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

                    // Slew-rate clamp (iRacing-style). Caps how fast the
                    // input can change per tick; preserves peak amplitude
                    // because the wheel still reaches the target value,
                    // just over a few extra ms. Only active in slew mode.
                    bool useSlew = FfbSpikeTamingEnabled && FfbSpikeUseSlewLimiter;
                    float maxDelta = useSlew ? FfbSpikeMaxLsbPerMs : 0f;
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

                    // Transient-detector soft ceiling. Tracks a slow-follower
                    // envelope of |t| (200ms TC) and treats only the excess
                    // of current magnitude over the envelope as a spike.
                    // Sustained heavy cornering (envelope catches up within
                    // ~half a second) passes through at full amplitude;
                    // sudden jumps (crash, big hit) blow past the envelope
                    // and trigger attenuation.
                    //
                    // Baseline floor is max(threshold, envelope): even if
                    // envelope is low, attenuation only engages once |t|
                    // exceeds the user-set threshold, so small-magnitude
                    // transients (light bumps) pass through unaffected.
                    //
                    // softExcess = cap * magExcess / (cap + magExcess) is a
                    // standard soft-knee: at magExcess = cap, softExcess =
                    // cap/2; asymptotes to cap as the spike grows. Output
                    // ceiling = baseline + softExcess, so peak FFB during a
                    // big crash asymptotes toward baseline + cap.
                    bool useTransient = FfbSpikeTamingEnabled && !FfbSpikeUseSlewLimiter;
                    float spikeCap = useTransient ? FfbPeakSoftLimitLsb : 0f;
                    float magThreshold = useTransient ? FfbSpikeMaxLsbPerMs : 0f;
                    int absT = t < 0 ? -t : t;
                    _sustainedFfbEnv += (absT - _sustainedFfbEnv) * SustainedFfbAlpha;
                    if (spikeCap > 0f && magThreshold > 0f)
                    {
                        float baseline = magThreshold > _sustainedFfbEnv ? magThreshold : _sustainedFfbEnv;
                        if (absT > baseline)
                        {
                            float magExcess = absT - baseline;
                            float softExcess = spikeCap * magExcess / (spikeCap + magExcess);
                            float factor = (baseline + softExcess) / absT;
                            t = (int)(t * factor);
                        }
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
                System.Threading.Interlocked.Increment(ref _packetsSent);
            }
            catch
            {
                // On a write failure (device unplugged etc.) tear down the
                // loop and flag it as a fault so the plugin's watchdog can
                // tell this apart from a clean StopStream() and re-attach.
                _streamFaulted = true;
                _shuttingDown = true;
            }
        }

        // EVO-style silent keepalive: NewPerPacket=0, window literal zeros, cur=0x8000.
        // byte10=0 means "no new audio samples" (confirmed by mescon, 2026-07)
        // and cur is still honored, so cur=0x8000 commands zero ep3 force.
        // That the wheel then falls back to its normal ep0 HID++ FFB path is
        // our own observation of this all-zero shape; which field gates the
        // fallback is still not isolated (the fields covary in EVO's captures,
        // and mescon's single-writer driver never exercises concurrent ep0).
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
            pkt[11] = 0x0d;         // constant in every capture, ours and
                                    // mescon's. Plausibly 13 = Window slot
                                    // count (byte 10 declares new samples,
                                    // byte 11 the window length); hypothesis,
                                    // not confirmed.
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

        // MMCSS (avrt.dll): raise the pump thread into the multimedia real-time
        // scheduling band for the life of the stream loop. AVRT_PRIORITY_HIGH = 1.
        private const int AVRT_PRIORITY_HIGH = 1;

        [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        // High-resolution waitable timer (kernel32). The HIGH_RESOLUTION flag needs
        // Windows 10 1803+; on older systems CreateWaitableTimerEx returns null and
        // the pump falls back to the busy-spin wait. Letting the 1 kHz pump sleep to
        // each beat instead of spinning a core is what keeps the Pro Audio thread
        // from starving the Windows audio mixer.
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private const uint WAIT_FAILED = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        // One-time sanity check that a high-resolution waitable timer actually
        // sleeps rather than returning instantly. A wrong due-time sign/width would
        // make SetWaitableTimer fire immediately, degenerating the intended sleep
        // into a busy loop that still pins the core (the very thing we are removing).
        // Arm it for ~1 ms once and confirm the wait really blocked before trusting
        // it for the stream loop.
        private static bool WaitableTimerBlocks(IntPtr timer)
        {
            try
            {
                long due = -10_000L; // 1 ms in 100 ns units (negative = relative)
                if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    return false;
                var sw = Stopwatch.StartNew();
                if (WaitForSingleObject(timer, 100) == WAIT_FAILED)
                    return false;
                return sw.Elapsed.TotalMilliseconds >= 0.3;
            }
            catch
            {
                return false;
            }
        }
    }
}
