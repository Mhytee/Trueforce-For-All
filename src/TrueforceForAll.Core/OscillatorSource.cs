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
        /// Sample rate the source is being run at. Defaults to Trueforce's
        /// 4 kHz audio-haptic rate (matches what AC EVO sends; mescon's
        /// docs say 1 kHz but that produces FFB suppression on G PRO).
        /// </summary>
        public double SampleRate { get; set; } = 4000.0;

        /// <summary>For Noise waveform only: 1-pole low-pass cutoff (Hz) applied
        /// to the noise output. White noise above ~400 Hz can't be reproduced
        /// faithfully by a wheel motor and just adds graininess; cutting it
        /// gives a smoother rumble feel. Set ≤ 0 to disable. Ignored for
        /// non-Noise waveforms.</summary>
        public double NoiseLowpassHz { get; set; } = 400.0;

        public bool IsActive => Enabled && Amp > 0.0;

        // Synth-thread state.
        private double _phase;
        private readonly Random _rng = new Random();
        private float _noiseLpY;

        public void RenderAdd(float[] buffer, int count)
        {
            // Snapshot the parameters once at the start of the batch. Within a
            // 4-sample batch (4 ms) holding them constant is fine; the next
            // batch will pick up any UI change.
            Waveform w = Waveform;
            double freq = Freq;
            float amp = (float)Amp;
            double phaseStep = freq / SampleRate;

            // For Noise: apply a 1-pole IIR low-pass to remove high-frequency
            // graininess that the wheel motor can't reproduce as smooth rumble.
            // Compensate for energy loss with a small gain bump (sqrt(2/alpha)
            // is the analytic bandwidth-energy correction; ~1.6× at α=0.5).
            if (w == Waveform.Noise && NoiseLowpassHz > 0 && NoiseLowpassHz < SampleRate * 0.5)
            {
                float alpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * NoiseLowpassHz / SampleRate));
                float compensate = (float)Math.Sqrt(2.0 / Math.Max(0.0001, alpha));
                for (int i = 0; i < count; i++)
                {
                    float x = (float)(_rng.NextDouble() * 2.0 - 1.0);
                    _noiseLpY += alpha * (x - _noiseLpY);
                    buffer[i] += _noiseLpY * compensate * amp;
                }
                return;
            }

            for (int i = 0; i < count; i++)
            {
                buffer[i] += SampleAt(w, _phase) * amp;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= Math.Floor(_phase);
            }
        }

        /// <summary>
        /// Reset the oscillator's phase to 0. Useful when retriggering a
        /// transient effect (e.g. gear-shift jolt).
        /// </summary>
        public void ResetPhase() => _phase = 0;

        private float SampleAt(Waveform w, double phase)
        {
            switch (w)
            {
                case Waveform.Sine:     return (float)Math.Sin(2.0 * Math.PI * phase);
                case Waveform.Square:   return phase < 0.5 ? 1f : -1f;
                case Waveform.Saw:      return (float)(2.0 * phase - 1.0);
                case Waveform.Triangle:
                    return phase < 0.5
                        ? (float)(4.0 * phase - 1.0)
                        : (float)(3.0 - 4.0 * phase);
                case Waveform.Noise:    return (float)(_rng.NextDouble() * 2.0 - 1.0);
                default: return 0f;
            }
        }
    }
}
