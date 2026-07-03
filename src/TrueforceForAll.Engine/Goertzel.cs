// Single-bin spectral energy probe (Goertzel algorithm). The harness asserts
// band occupancy with a handful of known probe frequencies (effect carriers,
// slip-texture bands) instead of a full FFT — we always know which bins
// matter, and Goertzel is O(N) per bin with no allocation. Also the future
// live band visualizer's workhorse.

using System;

namespace TrueforceForAll.Core
{
    public static class Goertzel
    {
        /// <summary>Normalized power of <paramref name="freqHz"/> over
        /// samples[offset..offset+count). Returns mean-square amplitude of the
        /// bin (so a full-scale sine at exactly freqHz reads ~0.5 regardless
        /// of window length). sampleRate is the stream rate (4000 for the
        /// TrueForce window).</summary>
        public static double Power(float[] samples, int offset, int count, double freqHz, double sampleRate)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count <= 0 || offset < 0 || offset + count > samples.Length) return 0;

            double k     = freqHz / sampleRate;
            double w     = 2.0 * Math.PI * k;
            double coeff = 2.0 * Math.Cos(w);
            double s0 = 0, s1 = 0, s2 = 0;
            for (int i = 0; i < count; i++)
            {
                s0 = samples[offset + i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            // Standard Goertzel power, normalized by N² then doubled to make a
            // unit sine read amplitude²/2 like an RMS² measure.
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            double n2 = (double)count * count;
            return 2.0 * power / n2;
        }
    }
}
