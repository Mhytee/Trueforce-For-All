// Canonical waveform sampling. Formerly copy-pasted privately into every
// effect that renders a Waveform (8 copies); any change to how a shape
// renders (or a new shape) happens here and nowhere else.

using System;

namespace TrueforceForAll.Core
{
    public static class WaveformMath
    {
        /// <summary>Sample the waveform at phase01 in [0, 1). Noise is NOT
        /// supported here (returns 0): callers that render noise need a Random
        /// source, use the rng overload.</summary>
        public static float SampleAt(Waveform w, double phase01)
        {
            switch (w)
            {
                case Waveform.Sine:     return (float)Math.Sin(2.0 * Math.PI * phase01);
                case Waveform.Square:   return phase01 < 0.5 ? 1f : -1f;
                case Waveform.Saw:      return (float)(2.0 * phase01 - 1.0);
                case Waveform.Triangle:
                    return phase01 < 0.5
                        ? (float)(4.0 * phase01 - 1.0)
                        : (float)(3.0 - 4.0 * phase01);
                default:                return 0f;
            }
        }

        /// <summary>Sample the waveform at phase01 in [0, 1), rendering Noise
        /// as white noise from the caller's Random (callers own their rng so
        /// noise character stays per-voice and thread-confined).</summary>
        public static float SampleAt(Waveform w, double phase01, Random rng)
        {
            if (w == Waveform.Noise) return (float)(rng.NextDouble() * 2.0 - 1.0);
            return SampleAt(w, phase01);
        }

        /// <summary>Wavetable-seed variant (double precision): Noise is
        /// unstable as a wavetable seed, so it (and anything unknown) falls
        /// back to Sine instead of silence. Used by the engine-pulse
        /// wavetable builder.</summary>
        public static double SampleForWavetable(Waveform w, double phase01)
        {
            switch (w)
            {
                case Waveform.Sine:     return Math.Sin(2.0 * Math.PI * phase01);
                case Waveform.Square:   return phase01 < 0.5 ? 1.0 : -1.0;
                case Waveform.Saw:      return 2.0 * phase01 - 1.0;
                case Waveform.Triangle: return phase01 < 0.5
                                            ? 4.0 * phase01 - 1.0
                                            : 3.0 - 4.0 * phase01;
                case Waveform.Noise:    // unstable as a wavetable seed; fall back to sine
                default:                return Math.Sin(2.0 * Math.PI * phase01);
            }
        }
    }
}
