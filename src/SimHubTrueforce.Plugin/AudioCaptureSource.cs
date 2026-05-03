// Captures audio from the running game's process via ProcessLoopbackCapture
// (Windows process loopback API), downsamples to 1 kHz mono, and exposes the
// result as an ISampleSource so it slots into the existing Mixer /
// TrueforceDevice path.
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
using SimHubTrueforce.Core;

namespace SimHubTrueforce.Plugin
{
    public sealed class AudioCaptureSource : ISampleSource, IDisposable
    {
        public const double TargetRateHz = 1000.0;

        // 4 seconds of buffering at 1 kHz mono. The producer (WASAPI callback,
        // every ~10 ms) and consumer (Trueforce producer, every 4 ms) are both
        // fast; this is just safety headroom for GC pauses.
        private const int RingSamples = 4096;

        // Lowpass cutoff well below the 500 Hz Nyquist of our 1 kHz output rate.
        // Trueforce-relevant haptic content lives at 30-300 Hz anyway.
        private const double LowpassCutoffHz = 400.0;

        private ProcessLoopbackCapture _capture;
        private int _capturedPid;
        private Biquad _lowpassL = Biquad.Lowpass(LowpassCutoffHz, 48000); // re-init on Start()
        private Biquad _lowpassR = Biquad.Lowpass(LowpassCutoffHz, 48000);
        private double _phase;       // fractional input-sample position
        private double _phaseStep;   // srIn / TargetRateHz, so we emit one output every this many input samples

        private readonly float[] _ring = new float[RingSamples];
        private int _ringHead;       // producer index (capture)
        private int _ringTail;       // consumer index (RenderAdd)
        private readonly object _ringLock = new object();

        private volatile bool _captureActive;
        private float _peakSinceLastRead;
        private readonly object _peakLock = new object();

        // ---------- public knobs (mutated by the plugin's settings) ----------
        public bool Enabled { get; set; } = true;
        public float Gain { get; set; } = 1.0f;

        public bool IsActive => Enabled && _captureActive;
        public int  CapturedProcessId => _capturedPid;

        // ---------- lifecycle ----------

        /// <summary>
        /// Start capture for the given process ID. Replaces any active capture.
        /// No-op if pid matches the currently captured process.
        /// </summary>
        public void Start(int processId)
        {
            if (_capture != null && _capturedPid == processId && _captureActive) return;
            Stop();

            var capture = new ProcessLoopbackCapture(processId);
            var fmt = capture.WaveFormat;
            _phaseStep = fmt.SampleRate / TargetRateHz;
            _phase = 0;
            _lowpassL = Biquad.Lowpass(LowpassCutoffHz, fmt.SampleRate);
            _lowpassR = Biquad.Lowpass(LowpassCutoffHz, fmt.SampleRate);

            capture.DataAvailable    += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _capture = capture;
            _capturedPid = processId;
            _captureActive = true;
        }

        public void Stop()
        {
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            _capturedPid = 0;
            _captureActive = false;

            // Drain any leftover ring contents so a fresh Start doesn't replay stale audio.
            lock (_ringLock) { _ringTail = _ringHead; }
        }

        public void Dispose() => Stop();

        // ---------- ISampleSource ----------

        public void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled || count <= 0) return;
            float gain = Gain;

            lock (_ringLock)
            {
                // Pull up to `count` samples from the ring; if short, contribute
                // only what we have (additive zero for the rest is harmless).
                int avail = (_ringHead - _ringTail) & (RingSamples - 1);
                int n = Math.Min(count, avail);
                for (int i = 0; i < n; i++)
                {
                    buffer[i] += _ring[_ringTail & (RingSamples - 1)] * gain;
                    _ringTail++;
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

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            _captureActive = false;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_captureActive) return;

            var fmt = _capture.WaveFormat;
            int channels = fmt.Channels;
            int bytesPerSample = fmt.BitsPerSample / 8;
            int bytesPerFrame = bytesPerSample * channels;
            int frameCount = e.BytesRecorded / bytesPerFrame;
            if (frameCount == 0) return;

            // Local scratch — accumulate emitted output samples, push to ring once.
            // At 48 kHz input → 1 kHz output, frameCount/48 emissions per callback (~10 frames).
            int maxEmissions = frameCount + 1;
            float[] outBuf = new float[maxEmissions];
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
                float fL = _lowpassL.Process(L);
                float fR = _lowpassR.Process(R);
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
                    for (int i = 0; i < outIdx; i++)
                    {
                        _ring[_ringHead & (RingSamples - 1)] = outBuf[i];
                        _ringHead++;
                        // If we lap the consumer, drop the oldest sample (advance tail).
                        if (((_ringHead - _ringTail) & (RingSamples - 1)) == 0)
                            _ringTail++;
                    }
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

        // ---------- 2nd-order Butterworth biquad lowpass ----------

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
