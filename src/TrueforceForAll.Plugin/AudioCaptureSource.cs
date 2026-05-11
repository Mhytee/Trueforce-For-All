// DSP pipeline that downsamples per-process loopback audio to 1 kHz mono
// and exposes it as an ISampleSource for the Mixer / TrueforceDevice path.
//
// Audio frames are pushed in via Feed() (called by HelperHost when the
// TrueforceForAll.LoopbackHelper child process delivers a chunk over stdout).
// The actual loopback runs in a separate .NET 8 process — this class is
// agnostic to the source, it just consumes byte buffers in the agreed
// format (48 kHz / 2-channel / 32-bit IEEE float) or whatever the WaveFormat
// passed to Start() declares.
//
// Pipeline per input frame:
//   stereo float @ 48 kHz   →  per-channel 2nd-order Butterworth LPF @ 400 Hz
//                           →  L+R / 2 (mono)
//                           →  decimate to 1 kHz via phase accumulator
//                           →  ring buffer
//   1 kHz ring              →  RenderAdd pulls + applies Gain

using System;
using System.Threading;
using NAudio.Wave;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed class AudioCaptureSource : ISampleSource, IDisposable
    {
        public const double TargetRateHz = 4000.0;

        // Backing array sized to the maximum so SetRingCapacity can resize
        // live without reallocating; only `_ringCapacity` slots are in use.
        // Capacity must be a power of two (the head/tail wrap with `& (cap-1)`).
        // At 4 kHz each sample is 0.25 ms, so 8=2ms, 16=4ms, 32=8ms,
        // 64=16ms, 128=32ms.
        //
        // Default starts at the minimum (8 = 2 ms) so users on low-latency
        // audio drivers get the lowest possible round-trip out of the box,
        // and users on slower hardware get auto-tuned up by the ratchet.
        // WASAPI loopback (via IAudioClient3) typically fires at ~3 ms with
        // ~12 decimated samples per burst, so on most systems the very
        // first noisy moment will trigger a ratchet UP to 16 (4 ms — the
        // safe default for typical hardware). Subsequent quiet sessions
        // ratchet back DOWN, so the system self-tunes to whatever the
        // user's hardware actually needs.
        public const int MaxRingSamples     = 128;     // power of two
        public const int MinRingSamples     = 8;       // power of two
        public const int DefaultRingSamples = 8;       // 2 ms — start low, ratchet auto-tunes up if needed

        private int _ringCapacity = DefaultRingSamples;
        public int RingCapacity => System.Threading.Volatile.Read(ref _ringCapacity);

        // Combined "ring stress" counter: incremented when WASAPI delivers
        // more samples than fit (lap = drop oldest, audible click) OR when
        // the producer asks for samples the ring doesn't have (underrun =
        // partial silence). Either signal means the ring is too small for
        // the current scheduling load.
        //
        // Underruns are duration-quantized: only continuous-starvation
        // streaks crossing GlitchQuantumTicks increment the counter, so a
        // single-tick scheduling blip contributes 0 and a sustained stall
        // contributes one count per quantum (severity preserved through
        // proportional count). WASAPI laps already arrive as discrete
        // callback events (not per-tick), so they're counted as-is.
        // At the producer's ~1 kHz pull rate, 20 ticks = ~20 ms.
        private const int GlitchQuantumTicks = 20;
        private long _glitchCount;
        private int _currentGlitchStreak;
        public long GlitchCount => System.Threading.Interlocked.Read(ref _glitchCount);

        // Default lowpass cutoff. 350 Hz keeps the haptic-relevant rumble
        // band (0-300 Hz feels good through the wheel) while stripping
        // 400+ Hz "graininess" that doesn't translate well to motor torque.
        // Tunable at runtime — the previous 1500 Hz default brought through
        // too much high-frequency content; 350 is a better starting point.
        public const double DefaultLowpassCutoffHz  = 350.0;
        public const double DefaultHighpassCutoffHz =  30.0;

        private double _lowpassCutoffHz  = DefaultLowpassCutoffHz;
        private double _highpassCutoffHz = DefaultHighpassCutoffHz;

        public double LowpassCutoffHz
        {
            get => _lowpassCutoffHz;
            set
            {
                if (Math.Abs(_lowpassCutoffHz - value) < 0.5) return;
                _lowpassCutoffHz = value;
                int rate = _captureFormat?.SampleRate ?? 48000;
                _lowpassL = Biquad.Lowpass(_lowpassCutoffHz, rate);
                _lowpassR = Biquad.Lowpass(_lowpassCutoffHz, rate);
            }
        }

        /// <summary>High-pass cutoff (Hz). Removes sub-haptic content (DC drift,
        /// rumble below feel-able freq) so the wheel motor's torque doesn't
        /// spend energy on signals that don't translate to perceived feel.
        /// 0 = disabled (passthrough). Default 30 Hz.</summary>
        public double HighpassCutoffHz
        {
            get => _highpassCutoffHz;
            set
            {
                if (Math.Abs(_highpassCutoffHz - value) < 0.5) return;
                _highpassCutoffHz = value;
                int rate = _captureFormat?.SampleRate ?? 48000;
                _highpassL = _highpassCutoffHz > 0
                    ? Biquad.Highpass(_highpassCutoffHz, rate)
                    : Biquad.Bypass();
                _highpassR = _highpassCutoffHz > 0
                    ? Biquad.Highpass(_highpassCutoffHz, rate)
                    : Biquad.Bypass();
            }
        }

        private HelperHost _host;
        private int _capturedPid;
        private Biquad _lowpassL  = Biquad.Lowpass(DefaultLowpassCutoffHz, 48000); // re-init on Start()
        private Biquad _lowpassR  = Biquad.Lowpass(DefaultLowpassCutoffHz, 48000);
        private Biquad _highpassL = Biquad.Highpass(DefaultHighpassCutoffHz, 48000);
        private Biquad _highpassR = Biquad.Highpass(DefaultHighpassCutoffHz, 48000);
        private WaveFormat _captureFormat;
        private double _phase;       // fractional input-sample position
        private double _phaseStep;   // srIn / TargetRateHz, so we emit one output every this many input samples

        private readonly float[] _ring = new float[MaxRingSamples];
        private int _ringHead;       // producer index (capture)
        private int _ringTail;       // consumer index (RenderAdd)
        private readonly object _ringLock = new object();

        // Reusable scratch for downsampled emissions per WASAPI callback.
        // Single-producer (the helper-host pump thread is the only writer),
        // so no sync needed. Grown lazily if a callback exceeds current size.
        private float[] _outBufScratch = new float[256];
        // Tracks whether RenderAdd has ever pulled at least one sample, so we
        // don't count "underrun" during the cold-start period before the
        // capture callback has fired.
        private bool _everPulledSample;

        private volatile bool _captureActive;
        private float _peakSinceLastRead;
        private readonly object _peakLock = new object();

        // ---------- public knobs (mutated by the plugin's settings) ----------
        public bool Enabled { get; set; } = true;
        public float Gain { get; set; } = 1.0f;

        /// <summary>Extra gain scaled by current throttle position. Real engines
        /// produce more audible kick the moment the throttle opens — without
        /// this multiplier, the captured game audio's natural mix can feel
        /// flat compared to native Trueforce. 0.5 = up to +50% gain at full
        /// throttle on top of base Gain. Set to 0 to disable.</summary>
        public float ThrottleBoost { get; set; } = 0.5f;

        /// <summary>Current throttle position in [0, 1]. Updated from telemetry
        /// by the plugin's DataUpdate. RenderAdd reads this lock-free.</summary>
        public float ThrottleNormalized { get; set; }

        /// <summary>Sidechain duck multiplier (0..1). Set by the plugin per
        /// Mixer cycle so transient effects (gear shift, ABS, slip) can duck
        /// the captured audio for perceptual headroom.</summary>
        public float DuckMultiplier { get; set; } = 1.0f;

        public bool IsActive => Enabled && _captureActive;
        public int  CapturedProcessId => _capturedPid;

        // ---------- lifecycle ----------

        /// <summary>
        /// Bind to a HelperHost. The DSP pipeline begins consuming data the
        /// moment the host delivers DataAvailable events (i.e. once the host's
        /// own SetTargetPid has been called).
        /// </summary>
        public void Attach(HelperHost host)
        {
            if (_host == host) return;
            Detach();
            _host = host;
            _captureFormat = host.WaveFormat;
            _phaseStep = _captureFormat.SampleRate / TargetRateHz;
            _phase = 0;
            _lowpassL  = Biquad.Lowpass(LowpassCutoffHz, _captureFormat.SampleRate);
            _lowpassR  = Biquad.Lowpass(LowpassCutoffHz, _captureFormat.SampleRate);
            _highpassL = HighpassCutoffHz > 0 ? Biquad.Highpass(HighpassCutoffHz, _captureFormat.SampleRate) : Biquad.Bypass();
            _highpassR = HighpassCutoffHz > 0 ? Biquad.Highpass(HighpassCutoffHz, _captureFormat.SampleRate) : Biquad.Bypass();
            _host.DataAvailable += OnDataAvailable;
        }

        public void Detach()
        {
            if (_host != null)
            {
                _host.DataAvailable -= OnDataAvailable;
                _host = null;
            }
        }

        /// <summary>
        /// Mark capture as active. Called by the plugin when a sim is detected.
        /// </summary>
        public void Start(int processId)
        {
            _capturedPid = processId;
            _captureActive = true;
            // Reset the filter state so we don't bleed silence-zeros through
            // them when capture (re)starts after a gap.
            int rate = _captureFormat?.SampleRate ?? 48000;
            _lowpassL  = Biquad.Lowpass(LowpassCutoffHz, rate);
            _lowpassR  = Biquad.Lowpass(LowpassCutoffHz, rate);
            _highpassL = HighpassCutoffHz > 0 ? Biquad.Highpass(HighpassCutoffHz, rate) : Biquad.Bypass();
            _highpassR = HighpassCutoffHz > 0 ? Biquad.Highpass(HighpassCutoffHz, rate) : Biquad.Bypass();
            _phase = 0;
        }

        /// <summary>
        /// Mark capture as inactive and drain the ring so old audio doesn't
        /// replay on the next Start.
        /// </summary>
        public void Stop()
        {
            _captureActive = false;
            _capturedPid = 0;
            lock (_ringLock) { _ringTail = _ringHead; }
        }

        public void Dispose() { Stop(); Detach(); }

        /// <summary>Live-resize the ring. <paramref name="newCapacity"/> must
        /// be a power of two in [MinRingSamples, MaxRingSamples]; backing
        /// array is already at MaxRingSamples so no allocation. Drops any
        /// in-flight samples (head/tail reset) — at most a single ~10 ms
        /// audio gap, vs. needing to stop/restart the capture chain.</summary>
        public void SetRingCapacity(int newCapacity)
        {
            if (newCapacity < MinRingSamples || newCapacity > MaxRingSamples)
                throw new ArgumentOutOfRangeException(nameof(newCapacity),
                    $"must be in [{MinRingSamples}, {MaxRingSamples}]");
            if ((newCapacity & (newCapacity - 1)) != 0)
                throw new ArgumentException("must be a power of two", nameof(newCapacity));

            lock (_ringLock)
            {
                if (_ringCapacity == newCapacity) return;
                _ringCapacity = newCapacity;
                _ringHead = 0;
                _ringTail = 0;
            }
        }

        // ---------- ISampleSource ----------

        public void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled || count <= 0) return;
            float t = ThrottleNormalized;
            if (t < 0) t = 0; else if (t > 1f) t = 1f;
            float gain = Gain * (1f + ThrottleBoost * t) * DuckMultiplier;
            if (gain <= 0f) return;

            lock (_ringLock)
            {
                // Pull up to `count` samples from the ring; if short, contribute
                // only what we have (additive zero for the rest is harmless).
                int cap   = _ringCapacity;
                int avail = (_ringHead - _ringTail) & (cap - 1);
                int n = Math.Min(count, avail);
                for (int i = 0; i < n; i++)
                {
                    buffer[i] += _ring[_ringTail & (cap - 1)] * gain;
                    _ringTail++;
                }
                // Underrun: capture is active but ring didn't have enough
                // samples to satisfy the producer's request. Only counted
                // after the capture has ever delivered any samples (so the
                // gap before the first WASAPI callback doesn't inflate the
                // count). avail < count is the trigger. Duration-quantized:
                // emit one count per GlitchQuantumTicks of continuous
                // starvation. Sub-quantum blips never tick the counter
                // (streak resets to 0 on the next good pull).
                if (n > 0)
                {
                    _everPulledSample = true;
                    _currentGlitchStreak = 0;
                }
                else if (_everPulledSample && _captureActive && count > 0)
                {
                    _currentGlitchStreak++;
                    if (_currentGlitchStreak % GlitchQuantumTicks == 0)
                        System.Threading.Interlocked.Increment(ref _glitchCount);
                }
            }
        }

        // ---------- diagnostics ----------

        /// <summary>
        /// Peak amplitude observed since the previous call, in [0, 1] range
        /// (post-gain). Reset to 0 on each read. Useful for a UI level meter.
        /// </summary>
        public float ReadAndResetPeak()
        {
            lock (_peakLock)
            {
                float p = _peakSinceLastRead;
                _peakSinceLastRead = 0f;
                return p;
            }
        }

        // ---------- capture callback ----------

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_captureActive) return;

            var fmt = _captureFormat;
            if (fmt == null) return;
            int channels = fmt.Channels;
            int bytesPerSample = fmt.BitsPerSample / 8;
            int bytesPerFrame = bytesPerSample * channels;
            int frameCount = e.BytesRecorded / bytesPerFrame;
            if (frameCount == 0) return;

            // Reusable scratch — accumulate emitted output samples, push to ring once.
            // At 48 kHz input → 1 kHz output, frameCount/48 emissions per callback (~10 frames).
            int maxEmissions = frameCount + 1;
            if (_outBufScratch.Length < maxEmissions)
                _outBufScratch = new float[maxEmissions];
            float[] outBuf = _outBufScratch;
            int outIdx = 0;

            byte[] buf = e.Buffer;
            float gain = Gain;
            float peak = 0f;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt.Encoding == WaveFormatEncoding.Extensible && bytesPerSample == 4);

            for (int i = 0; i < frameCount; i++)
            {
                int byteOffset = i * bytesPerFrame;
                float L, R;
                if (isFloat && bytesPerSample == 4)
                {
                    L = BitConverter.ToSingle(buf, byteOffset);
                    R = channels > 1 ? BitConverter.ToSingle(buf, byteOffset + 4) : L;
                }
                else if (bytesPerSample == 2)
                {
                    short sL = (short)(buf[byteOffset] | (buf[byteOffset + 1] << 8));
                    short sR = channels > 1
                        ? (short)(buf[byteOffset + 2] | (buf[byteOffset + 3] << 8))
                        : sL;
                    L = sL * (1f / 32768f);
                    R = sR * (1f / 32768f);
                }
                else
                {
                    // Unsupported (24-bit, etc.) — skip frame.
                    continue;
                }

                // Lowpass per-channel before mixing avoids the slight phase weirdness
                // of filtering after the L+R sum (cheap to do separately).
                // Filter chain: highpass → lowpass = bandpass for haptic-relevant
                // frequency range (default 30-350 Hz). Highpass first removes DC
                // drift / sub-haptic content; lowpass cuts grainy high freq.
                float hL = _highpassL.Process(L);
                float hR = _highpassR.Process(R);
                float fL = _lowpassL.Process(hL);
                float fR = _lowpassR.Process(hR);
                float mono = (fL + fR) * 0.5f;

                // Phase accumulator: one input sample per loop, emit when phase
                // crosses _phaseStep. Sample-and-hold is fine after a brick-wall LPF.
                _phase += 1.0;
                if (_phase >= _phaseStep)
                {
                    _phase -= _phaseStep;
                    if (outIdx < maxEmissions)
                    {
                        outBuf[outIdx++] = mono;
                        float a = mono >= 0 ? mono : -mono;
                        if (a > peak) peak = a;
                    }
                }
            }

            if (outIdx > 0)
            {
                lock (_ringLock)
                {
                    int cap = _ringCapacity;
                    int laps = 0;
                    for (int i = 0; i < outIdx; i++)
                    {
                        _ring[_ringHead & (cap - 1)] = outBuf[i];
                        _ringHead++;
                        // If we lap the consumer, drop the oldest sample (advance tail).
                        if (((_ringHead - _ringTail) & (cap - 1)) == 0)
                        {
                            _ringTail++;
                            laps++;
                        }
                    }
                    if (laps > 0)
                        System.Threading.Interlocked.Increment(ref _glitchCount);
                }
            }

            if (peak > 0f)
            {
                float postGain = peak * gain;
                lock (_peakLock)
                {
                    if (postGain > _peakSinceLastRead) _peakSinceLastRead = postGain;
                }
            }
        }

        // ---------- 2nd-order Butterworth biquad lowpass / highpass ----------

        private struct Biquad
        {
            // Direct Form II Transposed.
            public float b0, b1, b2, a1, a2;
            public float z1, z2;

            public static Biquad Lowpass(double cutoffHz, double sampleRateHz)
            {
                double w0 = 2.0 * Math.PI * cutoffHz / sampleRateHz;
                double cosw0 = Math.Cos(w0);
                double sinw0 = Math.Sin(w0);
                double Q = 0.7071;            // Butterworth (1/sqrt(2))
                double alpha = sinw0 / (2.0 * Q);

                double b0 = (1.0 - cosw0) / 2.0;
                double b1 =  1.0 - cosw0;
                double b2 = (1.0 - cosw0) / 2.0;
                double a0 =  1.0 + alpha;
                double a1 = -2.0 * cosw0;
                double a2 =  1.0 - alpha;

                return new Biquad
                {
                    b0 = (float)(b0 / a0),
                    b1 = (float)(b1 / a0),
                    b2 = (float)(b2 / a0),
                    a1 = (float)(a1 / a0),
                    a2 = (float)(a2 / a0),
                };
            }

            public static Biquad Highpass(double cutoffHz, double sampleRateHz)
            {
                double w0 = 2.0 * Math.PI * cutoffHz / sampleRateHz;
                double cosw0 = Math.Cos(w0);
                double sinw0 = Math.Sin(w0);
                double Q = 0.7071;
                double alpha = sinw0 / (2.0 * Q);

                double b0 =  (1.0 + cosw0) / 2.0;
                double b1 = -(1.0 + cosw0);
                double b2 =  (1.0 + cosw0) / 2.0;
                double a0 =   1.0 + alpha;
                double a1 =  -2.0 * cosw0;
                double a2 =   1.0 - alpha;

                return new Biquad
                {
                    b0 = (float)(b0 / a0),
                    b1 = (float)(b1 / a0),
                    b2 = (float)(b2 / a0),
                    a1 = (float)(a1 / a0),
                    a2 = (float)(a2 / a0),
                };
            }

            /// <summary>Bypass biquad (transparent passthrough). Used when the
            /// user disables a filter stage by setting cutoff to 0.</summary>
            public static Biquad Bypass() => new Biquad { b0 = 1f };

            public float Process(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }
    }
}
