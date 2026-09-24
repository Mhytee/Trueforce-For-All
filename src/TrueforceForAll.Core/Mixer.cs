// Multi-voice mixer for the Trueforce sample stream.
//
// Sums all active ISampleSources, applies a master gain, and clamps to
// [-1, 1] before handing samples off to TrueforceDevice. The master gain
// defaults to a conservative 0.5, see Phase 1 testing notes: the G PRO's
// DD motor crosses from "vibration" into "FFB pull" somewhere around
// 0.7 effective amplitude, depending on frequency. 0.5 is comfortably
// inside the vibration regime.

using System;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class Mixer : ISampleSource
    {
        // Copy-on-write source list. Render reads _snapshot lock-free
        // (volatile read of the reference); Add / Remove take _mutateLock,
        // build a new array, and publish it. Rare mutations (settings
        // wiring at startup, the helper-host swapping the audio source)
        // mean the audio thread never blocks or allocates here.
        private volatile ISampleSource[] _snapshot = Array.Empty<ISampleSource>();
        private readonly object _mutateLock = new object();

        /// <summary>
        /// Final scaling applied to the summed mix before clamping. Defaults
        /// to 0.5 to stay safely in the vibration regime. Test rigs may push
        /// higher (sine test goes to 0.7); production callers (the SimHub
        /// plugin) should leave this at 0.5 or below.
        /// </summary>
        public float MasterGain { get; set; } = 0.5f;

        /// <summary>
        /// Optional stage run over the summed mix BEFORE master gain and the
        /// clamp (the Trueforce EQ in production, null in bare rigs). Set once
        /// at wiring time; Render reads it per call.
        /// </summary>
        public ISampleProcessor PostMix { get; set; }

        public bool IsActive => true;

        public int SourceCount => _snapshot.Length;

        public void Add(ISampleSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            lock (_mutateLock)
            {
                var old = _snapshot;
                var next = new ISampleSource[old.Length + 1];
                Array.Copy(old, next, old.Length);
                next[old.Length] = source;
                _snapshot = next;
            }
        }

        public bool Remove(ISampleSource source)
        {
            if (source == null) return false;
            lock (_mutateLock)
            {
                var old = _snapshot;
                int idx = Array.IndexOf(old, source);
                if (idx < 0) return false;
                var next = new ISampleSource[old.Length - 1];
                if (idx > 0) Array.Copy(old, 0, next, 0, idx);
                if (idx < old.Length - 1) Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
                _snapshot = next;
                return true;
            }
        }

        public void Render(float[] buffer, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count > buffer.Length) count = buffer.Length;

            Array.Clear(buffer, 0, count);

            // Volatile read: any source visible to us was Add()-ed in a
            // happens-before sense. Iteration is safe even if a concurrent
            // mutator publishes a new snapshot, we keep iterating the one
            // we already read; the next Render picks up the change.
            var sources = _snapshot;
            for (int s = 0; s < sources.Length; s++)
            {
                var src = sources[s];
                if (src != null && src.IsActive)
                    src.RenderAdd(buffer, count);
            }

            var post = PostMix;
            if (post != null) post.Process(buffer, count);

            float g = MasterGain;
            for (int i = 0; i < count; i++)
            {
                float v = buffer[i] * g;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                buffer[i] = v;
            }
        }

        // ISampleSource impl: when used as a sub-mixer, contributes additively.
        public void RenderAdd(float[] buffer, int count)
        {
            if (_scratch == null || _scratch.Length < count) _scratch = new float[count];
            Render(_scratch, count);
            for (int i = 0; i < count; i++) buffer[i] += _scratch[i];
        }
        private float[] _scratch;
    }
}
