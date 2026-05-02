// Multi-voice mixer for the Trueforce sample stream.
//
// Sums all active ISampleSources, applies a master gain, and clamps to
// [-1, 1] before handing samples off to TrueforceDevice. The master gain
// defaults to a conservative 0.5 — see Phase 1 testing notes: the G PRO's
// DD motor crosses from "vibration" into "FFB pull" somewhere around
// 0.7 effective amplitude, depending on frequency. 0.5 is comfortably
// inside the vibration regime.

using System;
using System.Collections.Generic;

namespace SimHubTrueforce.Core
{
    public sealed class Mixer : ISampleSource
    {
        public IList<ISampleSource> Sources { get; } = new List<ISampleSource>();

        /// <summary>
        /// Final scaling applied to the summed mix before clamping. Defaults
        /// to 0.5 to stay safely in the vibration regime. Test rigs may push
        /// higher (sine test goes to 0.7); production callers (the SimHub
        /// plugin) should leave this at 0.5 or below.
        /// </summary>
        public float MasterGain { get; set; } = 0.5f;

        public bool IsActive => true;

        public void Render(float[] buffer, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count > buffer.Length) count = buffer.Length;

            Array.Clear(buffer, 0, count);

            // Snapshot the sources collection (callers may mutate Sources from
            // a different thread; iterating live would risk InvalidOperationException).
            // For now we accept the per-render alloc; if it shows up in profiling
            // we'll add a swappable read-only snapshot.
            var sources = Sources;
            int n = sources.Count;
            for (int s = 0; s < n; s++)
            {
                var src = sources[s];
                if (src != null && src.IsActive)
                    src.RenderAdd(buffer, count);
            }

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
            // Lazily allocate a scratch buffer the first time we're nested.
            if (_scratch == null || _scratch.Length < count) _scratch = new float[count];
            Render(_scratch, count);
            for (int i = 0; i < count; i++) buffer[i] += _scratch[i];
        }
        private float[] _scratch;
    }
}
