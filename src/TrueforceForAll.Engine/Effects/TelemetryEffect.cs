// Base class for telemetry-driven haptic effects. Each effect is an
// ISampleSource that the Mixer renders alongside the audio-capture source
// so audio loopback and telemetry voices stack additively on the wheel.
//
// OnTelemetry runs on the active ITelemetrySource's polling thread
// SimHub's data tick (~60 Hz, capped by the IDataPlugin pipeline) for the
// fallback source, or a game-native MMF/UDP polling thread (e.g. ~333 Hz
// for AC) when an enhanced source is selected. RenderAdd runs on the
// Trueforce producer thread (1 kHz). Effects mutate their internal state
// in OnTelemetry; RenderAdd reads that state to produce samples.
// Cross-thread reads/writes of primitive fields are atomic on .NET for
// our purposes, eventual consistency is fine for haptics.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    /// <summary>Priority class for the phase-6 band arbiter. Ordering is the
    /// ducking hierarchy: a class ducks everything below it (band-gated).
    /// GripState is top because what the tire is doing is the highest-value
    /// signal a steering wheel can carry.</summary>
    public enum EffectClass
    {
        Ambience  = 0,   // engine pulse, road texture, DRS hum, captured audio
        Transient = 1,   // gear, ABS, kerb thump, collision, rev/pit alerts
        GripState = 2,   // slip scrub, lockup judder, traction loss
    }

    public abstract class TelemetryEffect : ISampleSource
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;

        public abstract string Name { get; }
        public abstract bool   IsActive { get; }
        public abstract void   RenderAdd(float[] buffer, int count);
        public virtual  void   OnTelemetry(TelemetryFrame frame) { }

        // Clear any internal state that would carry across a car / game switch.
        // Default is no-op; effects with edge-detected or last-value state
        // (gear tracking, ABS rising-edge) override.
        public virtual void Reset() { }

        /// <summary>Reseed any noise oscillators this effect owns. Production
        /// never calls it; the replay harness does before a run so two replays
        /// of the same fixture are byte-identical and golden metrics aren't
        /// chasing RNG. Default no-op (most effects are deterministic).</summary>
        public virtual void SeedNoise(int seed) { }

        // The telemetry feed went stale: the active source stopped emitting
        // frames entirely (game closed mid-session, crashed, or its shared-
        // memory page froze). Effects that hold a SUSTAINED amplitude set by
        // the last frame (engine pulse, rev/pit limiter, DRS hum, traction
        // buzz) would otherwise keep that amplitude forever, so the wheel
        // replays the last sensation until the user switches games. Override
        // to drop that held amplitude to silence. Default is no-op; transient
        // effects decay on their own and need nothing here. The plugin's stall
        // watchdog calls this once per stall episode.
        public virtual void OnTelemetryStall() { }

        // Test mode, used by the settings UI's "Test" button to play the effect
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

        // ---- Phase 6 effect contract (band arbiter) ----

        /// <summary>Ducking priority class. Default Ambience (bottom:
        /// ducked by everything above); grip-information and alert effects
        /// override.</summary>
        public virtual EffectClass PriorityClass => EffectClass.Ambience;

        /// <summary>Instantaneous frequency band of this effect's content in
        /// Hz. The arbiter only ducks where bands actually overlap, so a
        /// tight honest band buys an effect immunity from unrelated voices.
        /// Default is broadband (20..400 = "assume it masks everything"),
        /// which is the conservative choice for effects that don't
        /// override.</summary>
        public virtual void GetCurrentBand(out double loHz, out double hiHz)
        {
            loHz = 20.0;
            hiHz = 400.0;
        }
    }
}
