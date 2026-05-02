// A source of audio-haptic samples that can be additively mixed into a
// shared output buffer. The Mixer drives one or more of these per render,
// summing their contributions before applying master gain and pushing to
// the wheel.
//
// Examples:
//   - OscillatorSource: synthesised sine / square / saw / triangle / noise.
//   - (Phase 3) AudioCaptureSource: WASAPI process loopback.
//   - (Phase 2d) Telemetry voices: engine pulse from RPM, slip, surface, etc.

namespace SimHubTrueforce.Core
{
    public interface ISampleSource
    {
        /// <summary>
        /// Whether this source contributes anything right now. The Mixer
        /// skips inactive sources entirely (no Render call).
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Add this source's samples into <paramref name="buffer"/> for
        /// <paramref name="count"/> samples. Implementations must add
        /// (buffer[i] += ...), never overwrite, so multiple sources mix
        /// correctly when the Mixer chains them.
        /// </summary>
        void RenderAdd(float[] buffer, int count);
    }
}
