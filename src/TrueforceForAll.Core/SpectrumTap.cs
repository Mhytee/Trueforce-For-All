// Two small stages for the Mixer's post-mix slot.
//
// ProcessorChain runs stages in order (the plugin chains a pre-EQ tap, the
// EQ, and a post-EQ tap). SpectrumTap copies the samples that pass through
// it into a ring the UI thread reads for the live spectrum behind the EQ
// curve. The tap is display-only: the reader tolerates a torn read across
// the write pointer rather than making the engine thread take a lock.

using System;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class ProcessorChain : ISampleProcessor
    {
        private readonly ISampleProcessor[] _stages;

        public ProcessorChain(params ISampleProcessor[] stages)
        {
            _stages = stages ?? new ISampleProcessor[0];
        }

        public void Process(float[] buffer, int count)
        {
            for (int i = 0; i < _stages.Length; i++)
            {
                var s = _stages[i];
                if (s != null) s.Process(buffer, count);
            }
        }
    }

    public sealed class SpectrumTap : ISampleProcessor
    {
        /// <summary>Ring size, a power of two. ~1 s at the 4 kHz stream rate.</summary>
        public const int Capacity = 4096;
        private const int Mask = Capacity - 1;

        private readonly float[] _ring = new float[Capacity];
        private long _written;

        /// <summary>Total samples ever written. A reader that sees the same
        /// value twice knows nothing new arrived and can skip its analysis.</summary>
        public long Written => Volatile.Read(ref _written);

        public void Process(float[] buffer, int count)
        {
            if (buffer == null || count <= 0) return;
            if (count > buffer.Length) count = buffer.Length;
            long w = _written;
            for (int i = 0; i < count; i++) _ring[(int)((w + i) & Mask)] = buffer[i];
            Volatile.Write(ref _written, w + count);
        }

        /// <summary>Copy the newest <paramref name="count"/> samples, oldest
        /// first, into <paramref name="dst"/>. Returns 0 when fewer than
        /// <paramref name="count"/> samples have ever been written.</summary>
        public int CopyLatest(float[] dst, int count)
        {
            if (dst == null) return 0;
            if (count > dst.Length) count = dst.Length;
            if (count > Capacity) count = Capacity;
            long w = Written;
            if (count <= 0 || w < count) return 0;
            long start = w - count;
            for (int i = 0; i < count; i++) dst[i] = _ring[(int)((start + i) & Mask)];
            return count;
        }
    }
}
