// Base class for telemetry-driven haptic effects. Each effect is an
// ISampleSource that the Mixer renders alongside the audio-capture source —
// so audio loopback and telemetry voices stack additively on the wheel.
//
// OnTelemetry runs on the active ITelemetrySource's polling thread —
// SimHub's data tick (~60 Hz, capped by the IDataPlugin pipeline) for the
// fallback source, or a game-native MMF/UDP polling thread (e.g. ~333 Hz
// for AC) when an enhanced source is selected. RenderAdd runs on the
// Trueforce producer thread (1 kHz). Effects mutate their internal state
// in OnTelemetry; RenderAdd reads that state to produce samples.
// Cross-thread reads/writes of primitive fields are atomic on .NET for
// our purposes — eventual consistency is fine for haptics.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public abstract class TelemetryEffect : ISampleSource
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;

        public abstract string Name { get; }
        public abstract bool   IsActive { get; }
        public abstract void   RenderAdd(float[] buffer, int count);
        public virtual  void   OnTelemetry(TelemetryFrame frame) { }

        // Test mode — used by the settings UI's "Test" button to play the effect
        // at representative max parameters for a short duration without needing
        // the game to drive it via telemetry. Subclasses set their internal
        // parameters in TestPlay() and call StartTest(durationMs); OnTelemetry
        // checks IsTesting and skips updates so live telemetry doesn't overwrite
        // the test state.
        private long _testEndTicks;
        protected bool IsTesting => _testEndTicks != 0L && DateTime.UtcNow.Ticks < _testEndTicks;
        protected void StartTest(int durationMs)
        {
            _testEndTicks = DateTime.UtcNow.Ticks + durationMs * TimeSpan.TicksPerMillisecond;
        }

        /// <summary>Trigger the effect at representative max parameters. Returns
        /// duration in ms that the test will run for (so the caller can keep
        /// the device's ep3 active path open for at least that long).</summary>
        public virtual int TestPlay() => 0;

        /// <summary>Per-frame update during a test. The plugin calls this
        /// periodically (every ~16 ms) with a phase value 0.0 → 1.0 over the
        /// test duration. Effects override this to simulate dynamic behavior
        /// (RPM ramps, slip pulses, etc.) during the test rather than just
        /// playing a static value.</summary>
        public virtual void TestUpdate(double phase01) { }

        /// <summary>Current "loudness" of this effect in [0, 1], used by the
        /// plugin's sidechain ducking. Continuous effects (engine pulse,
        /// audio capture) duck their output proportional to the max
        /// ActivityLevel across transient effects (gear shift, ABS, road
        /// bumps, traction loss) so layered effects have perceptual headroom.
        /// Default 0 = doesn't trigger ducking (continuous effects override
        /// with their own implementation if they participate).</summary>
        public virtual double ActivityLevel => 0;

        /// <summary>Multiplier (0..1) applied to this effect's output by the
        /// plugin's sidechain ducker. 1.0 = full output, 0.0 = silent. Set
        /// by the plugin per-Mixer-tick before Render so transient effects
        /// can duck continuous ones.</summary>
        public float DuckMultiplier { get; set; } = 1.0f;
    }
}
