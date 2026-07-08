// Single-oscillator audio-haptic voice: sine / square / saw / triangle / noise
// at a controllable frequency and amplitude. Composable into a Mixer.
//
// Properties (Waveform / Freq / Amp / Enabled) are intentionally just fields
// behind property accessors; reads and writes are atomic on x64 .NET for
// these primitive types and the small jitter from torn reads is harmless.
// Phase, however, is owned exclusively by the synth thread (only RenderAdd
// touches it), so no cross-thread access on it.

using System;

namespace TrueforceForAll.Core
{
    public enum Waveform
    {
        Sine,
        Square,
        Saw,
        Triangle,
        Noise,
    }

    public sealed class OscillatorSource : ISampleSource
    {
        public Waveform Waveform { get; set; } = Waveform.Sine;

        /// <summary>Frequency in Hz. Default 80 Hz (a comfortable mid-band test tone).</summary>
        public double Freq { get; set; } = 80.0;

        /// <summary>Amplitude in [0, 1]. Peak of the generated waveform = Amp.</summary>
        public double Amp { get; set; } = 0.3;

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Sample rate the source is being run at. Defaults to 4 kHz to match
        /// what AC EVO empirically streams (our chosen 1000 packets/s × 4
        /// samples per packet). Confirmed (mescon, 2026-07): the wheel does
        /// not require a specific rate; games choose anywhere in at least the
        /// 250-1000 packets/s band. 4 kHz is a choice we match, not a
        /// requirement.
        /// </summary>
        public double SampleRate { get; set; } = 4000.0;

        /// <summary>For Noise waveform only: 1-pole low-pass cutoff (Hz) applied
        /// to the noise output. White noise above ~400 Hz can't be reproduced
        /// faithfully by a wheel motor and just adds graininess; cutting it
        /// gives a smoother rumble feel. Set ≤ 0 to disable. Ignored for
        /// non-Noise waveforms.</summary>
        public double NoiseLowpassHz { get; set; } = 400.0;

        /// <summary>For Noise waveform only: 1-pole high-pass cutoff (Hz)
        /// applied AFTER the low-pass. Removes sub-audible drift / thumping
        /// from the noise floor that the wheel motor can render as low-rate
        /// jolts. Set ≤ 0 to disable. Typical values 20-50 Hz.</summary>
        public double NoiseHighpassHz { get; set; } = 0.0;

        public bool IsActive => Enabled && Amp > 0.0;

        // Synth-thread state.
        private double _phase;
        private readonly Random _rng = new Random();
        private float _noiseLpY;
        // 1-pole highpass state. y[n] = α·(y[n-1] + x[n] - x[n-1]).
        private float _noiseHpY;
        private float _noiseHpPrev;

        public void RenderAdd(float[] buffer, int count)
        {
            // Snapshot the parameters once at the start of the batch. Within a
            // 4-sample batch (4 ms) holding them constant is fine; the next
            // batch will pick up any UI change.
            Waveform w = Waveform;
            double freq = Freq;
            float amp = (float)Amp;
            double phaseStep = freq / SampleRate;

            // For Noise: apply 1-pole IIR low-pass (removes high-frequency
            // graininess the wheel motor can't reproduce as smooth rumble),
            // then 1-pole IIR high-pass (removes sub-audible drift / DC
            // wander the motor renders as occasional thumps). Either filter
            // is bypassed when its cutoff is ≤ 0.
            if (w == Waveform.Noise)
            {
                bool useLp = NoiseLowpassHz  > 0 && NoiseLowpassHz  < SampleRate * 0.5;
                bool useHp = NoiseHighpassHz > 0 && NoiseHighpassHz < SampleRate * 0.5;

                float lpAlpha = useLp
                    ? (float)(1.0 - Math.Exp(-2.0 * Math.PI * NoiseLowpassHz / SampleRate))
                    : 1.0f;
                // HP coefficient for y[n] = α·(y[n-1] + x[n] - x[n-1]):
                // α near 1 = aggressive HP, α near 0 = gentle.
                float hpAlpha = useHp
                    ? (float)Math.Exp(-2.0 * Math.PI * NoiseHighpassHz / SampleRate)
                    : 1.0f;
                // Compensate for LP energy loss (sqrt(2/α) is the analytic
                // bandwidth-energy correction; ~1.6× at α=0.5). The HP at low
                // cutoff loses negligible energy so we don't compensate it.
                float compensate = useLp
                    ? (float)Math.Sqrt(2.0 / Math.Max(0.0001, lpAlpha))
                    : 1.0f;

                for (int i = 0; i < count; i++)
                {
                    float x = (float)(_rng.NextDouble() * 2.0 - 1.0);
                    if (useLp) _noiseLpY += lpAlpha * (x - _noiseLpY);
                    else       _noiseLpY = x;
                    float y = _noiseLpY;
                    if (useHp)
                    {
                        float yHp = hpAlpha * (_noiseHpY + y - _noiseHpPrev);
                        _noiseHpY = yHp;
                        _noiseHpPrev = y;
                        y = yHp;
                    }
                    buffer[i] += y * compensate * amp;
                }
                return;
            }

            for (int i = 0; i < count; i++)
            {
                buffer[i] += WaveformMath.SampleAt(w, _phase, _rng) * amp;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= Math.Floor(_phase);
            }
        }

        /// <summary>
        /// Reset the oscillator's phase to 0. Useful when retriggering a
        /// transient effect (e.g. gear-shift jolt).
        /// </summary>
        public void ResetPhase() => _phase = 0;
    }
}
