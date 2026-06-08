// Rev-limiter haptic. A hard, fast buzz that engages when engine RPM reaches
// the shift point / hits the limiter, and holds while you sit on (or bounce
// off) it. An independent, more obvious shift cue than the engine-pulse
// timbre change near redline, for users who run engine pulse low or off, or
// who just want an unmistakable "shift now / on the limiter" signal.
//
// Trigger is RPM-relative against the most accurate "top of the rev range"
// the active source knows: the car's REDLINE / shift RPM when the game exposes
// one (RedlineRpm, from SimHub's CarSettings_RedLineRPM, available on iRacing,
// AC, and any SimHub-fallback title), else the hard rev limit (MaxRpm) for
// sources without a separate redline (e.g. Forza). engaged when
// rpm >= reference * Threshold. So the buzz fires at the real shift point where
// the sim publishes it, instead of always at a fraction of the absolute limiter.
// Almost every source surfaces RPM + at least MaxRpm, so unlike the pit limiter
// this needs no game-specific flag and works wherever RPM is known. A short hold
// debounces the limiter-bounce flicker (RPM oscillating around the limit) so the
// buzz stays steady instead of stuttering on/off.
//
// Defaults are tuned to read as urgent rather than as the pit limiter's deep
// thud: a higher carrier (90 Hz) with a fast 20 Hz stutter, like an
// aggressive soft-cut limiter.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    /// <summary>How the rev limiter picks its engage point. Auto trusts the
    /// game: fire at the redline when the source reports a sane one, else at a
    /// percentage of the hard limit. The two explicit modes are the manual
    /// override for when auto-detection misreads (e.g. a game reports a redline
    /// that's bogus-but-in-range, so the buzz never fires): force Percentage to
    /// always engage at a % of MaxRpm, or force Redline to always trust the
    /// reported redline.</summary>
    public enum RevLimiterEngageMode { Auto, Percentage, Redline }

    public sealed class RevLimiterEffect : TelemetryEffect
    {
        public override string Name => "Rev limiter";

        /// <summary>Engage-point selection. See <see cref="RevLimiterEngageMode"/>.
        /// Default Auto reproduces the prior behaviour exactly.</summary>
        public RevLimiterEngageMode EngageMode { get; set; } = RevLimiterEngageMode.Auto;

        /// <summary>Carrier tone within each pulse (Hz). Higher than the pit
        /// limiter's 50 Hz thud so it reads as an urgent buzz, not a low
        /// engine-cut pulse.</summary>
        public float Freq { get; set; } = 90.0f;

        /// <summary>How fast the pulse modulator stutters the carrier (Hz).
        /// 20 Hz is a fast, aggressive flutter matching a soft-cut limiter
        /// hammering the engine.</summary>
        public float PulseFreq { get; set; } = 20.0f;

        /// <summary>Fraction of each pulse period the carrier is audible.
        /// 0.5 = even on/off stutter.</summary>
        public float DutyCycle { get; set; } = 0.5f;

        public Waveform Waveform { get; set; } = Waveform.Square;

        /// <summary>Amplitude while engaged.</summary>
        public float ActiveAmp { get; set; } = 0.35f;

        /// <summary>Fraction of the reference RPM (the car's redline when the
        /// game exposes one, else MaxRpm) at which the buzz engages. On the
        /// percentage path (Forza and other no-redline sources) MaxRpm is the
        /// absolute limiter, which sits above where you actually upshift, so
        /// 0.85 fires as a usable shift cue instead of only when you bounce off
        /// the limiter; raise it toward 1.00 to fire later / right on the limiter.
        /// Clamped to [0.50, 1.00] at use so a stray value can't fire the buzz
        /// off idle or never fire.</summary>
        public float Threshold { get; set; } = 0.85f;

        /// <summary>Offset in RPM applied to the engage point ONLY on the
        /// real-redline path (ignored on the percentage path). Negative fires
        /// the buzz that many RPM BEFORE the redline (early shift cue);
        /// positive fires it after. 0 = right at the redline. The effective
        /// engage point is floored at <see cref="MinEngineRpm"/> so a large
        /// negative offset can't make it fire off idle.</summary>
        public float RedlineOffsetRpm { get; set; } = 0.0f;

        /// <summary>Optional CarFacts-supplied redline for the active car
        /// (set per car-change by the plugin from
        /// <c>Settings.CarFacts[...].EngineVariants[active].RedlineRpm</c>).
        /// When set AND the engage mode would pick "no redline" because the
        /// game doesn't expose one (Forza family), this is treated as the
        /// real redline. Cleared on car change. Per-machine, not preset-
        /// saved. Stage 1 wiring of the CarFacts layer.</summary>
        public int? CarFactsRedline { get; set; }

        /// <summary>Preset-supplied unified redline value. Mirrors
        /// <see cref="TrueforceForAll.Plugin.RevLimiterSettings.RedlineRpm"/>;
        /// the apply path copies the preset's value into the effect on
        /// each ApplyEngineSettings call. When set, the buzz fires at
        /// this RPM (plus <see cref="RedlineOffsetRpm"/>) regardless of
        /// the game's telemetry redline. Null = fall through to
        /// telemetry / CarFactsRedline / Threshold-percent fallback.</summary>
        public int? RedlineRpm { get; set; }

        /// <summary>Callback fired the first time legacy Threshold gets
        /// lazy-migrated to RedlineRpm. Plugin subscribes so it can
        /// persist the rewritten preset to disk + ship the implied
        /// RedlineRpm to the community DB. Empty by default.</summary>
        public event System.Action<int> ThresholdMigratedToRedline;

        private const double SampleRate = 4000.0;
        private const int HoldMs = 80;   // post-disengage decay window
        private static readonly long HoldStopwatchTicks =
            HoldMs * System.Diagnostics.Stopwatch.Frequency / 1000;

        // Below this RPM the engine is effectively off; never engage (avoids a
        // false buzz when MaxRpm is momentarily reported as a tiny value, which
        // would make a near-zero RPM clear the threshold).
        private const double MinEngineRpm = 100.0;

        // State
        private float  _amp;
        private long   _lastActiveTicks;     // Stopwatch.GetTimestamp() units
        private double _carrierPhase;
        private double _pulsePhase;

        public override bool IsActive => IsTesting || (Enabled && _amp > 0);

        // Activity level for sidechain ducking. Like the pit limiter, ducks
        // engine pulse + audio perceptibly (0.6) without fully muting them, so
        // the limiter buzz cuts through while the engine note stays present.
        public override double ActivityLevel => _amp > 0 ? 0.6 : 0;

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            if (_amp <= 0) return;

            double cStep = Math.Max(0.0, Freq) / SampleRate;
            double pStep = Math.Max(0.0, PulseFreq) / SampleRate;
            double duty  = Math.Min(1.0, Math.Max(0.0, (double)DutyCycle));
            float amp = _amp * Gain * DuckMultiplier;
            Waveform w = Waveform;

            for (int i = 0; i < count; i++)
            {
                if (_pulsePhase < duty)
                    buffer[i] += SampleAt(w, _carrierPhase) * amp;
                _carrierPhase += cStep;
                if (_carrierPhase >= 1.0) _carrierPhase -= Math.Floor(_carrierPhase);
                _pulsePhase   += pStep;
                if (_pulsePhase   >= 1.0) _pulsePhase   -= Math.Floor(_pulsePhase);
            }
        }

        public override int TestPlay()
        {
            _amp = ActiveAmp;
            StartTest(2000);
            return 2000;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            UpdateEngagement(f.Rpms, f.RedlineRpm, f.MaxRpm);
        }

        // RPM-threshold + hold logic, shared by live telemetry and the REV
        // self-test. Thresholds against the redline when the source provides it,
        // else the hard rev limit. Sets _amp; RenderAdd plays it.
        // Returns the absolute RPM the limiter should fire at, or null
        // when no value can be resolved (engine not running, no preset
        // value, no telemetry). The lazy-migration branch ALSO writes
        // RedlineRpm back to the effect + fires the
        // ThresholdMigratedToRedline event so the plugin can persist
        // and submit-to-community.
        //
        // RedlineRpm semantics across the modes:
        //   * Manual (or legacy "Redline" enum value): always use
        //     Settings.RedlineRpm if set; null = engine off.
        //   * Auto: prefer telemetry sanity-gated, then CarFactsRedline,
        //     then preset RedlineRpm, then lazy-migrated Threshold,
        //     then default 0.85 * MaxRpm.
        private int? ResolveEffectiveRedline(double redlineRpm, double maxRpm)
        {
            bool manualMode =
                EngageMode == RevLimiterEngageMode.Redline;   // legacy "Redline" -> Manual

            if (manualMode)
            {
                if (RedlineRpm.HasValue && RedlineRpm.Value > MinEngineRpm)
                    return RedlineRpm.Value;
                // Manual without a saved value: fall through to the
                // Auto cascade rather than failing closed. Matches
                // legacy behaviour where Redline mode with no telemetry
                // would still attempt the sanity-gated read.
            }

            // Auto cascade: telemetry redline (sanity-gated) first.
            if (redlineRpm > MinEngineRpm
                && (maxRpm <= MinEngineRpm
                    || (redlineRpm <= maxRpm * 1.02 && redlineRpm >= maxRpm * 0.5)))
                return (int)Math.Round(redlineRpm);

            // CarFacts redline (community / resolver-picked variant).
            if (CarFactsRedline.HasValue && CarFactsRedline.Value > MinEngineRpm)
                return CarFactsRedline.Value;

            // Preset's saved RedlineRpm (when the user has tuned it
            // for this preset).
            if (RedlineRpm.HasValue && RedlineRpm.Value > MinEngineRpm)
                return RedlineRpm.Value;

            // Lazy migration: legacy preset with a tuned Threshold
            // (anything other than the 0.85 default) + observable
            // MaxRpm. Compute the implied absolute redline once,
            // promote it onto the effect, and let the plugin persist
            // it via the migration event.
            if (maxRpm > MinEngineRpm
                && Math.Abs(Threshold - 0.85f) > 0.0001f
                && Threshold >= 0.50f && Threshold <= 1.00f)
            {
                double thr = Threshold;
                int migrated = (int)Math.Round(maxRpm * thr / 500.0) * 500;
                if (migrated > MinEngineRpm)
                {
                    RedlineRpm = migrated;
                    try { ThresholdMigratedToRedline?.Invoke(migrated); }
                    catch { /* event handler is plugin-side persistence; effect renders regardless */ }
                    return migrated;
                }
            }

            // Default fallback: 0.85 of MaxRpm so games without any
            // tuning still fire a sensible shift cue.
            if (maxRpm > MinEngineRpm)
                return (int)Math.Round(maxRpm * 0.85);

            return null;
        }

        private void UpdateEngagement(double rpm, double redlineRpm, double maxRpm)
        {
            // Unified engage-point resolution (in priority order):
            //   1. EngageMode = Manual + Settings.RedlineRpm set
            //      (legacy "Redline" enum value collapses here for
            //      backward compat).
            //   2. EngageMode = Auto:
            //      a. Game-reported redline (sanity-gated 0.5..1.02 x
            //         MaxRpm) - trust it.
            //      b. CarFacts-supplied redline for the active car
            //         (community / variant pick).
            //      c. Preset Settings.RedlineRpm if the user set one.
            //      d. Lazy-migrate: legacy Threshold-tuned presets get
            //         their Threshold * MaxRpm converted to RedlineRpm
            //         once MaxRpm is observed (fires the migration event
            //         so the plugin can persist + submit). The first
            //         frame of this branch ALSO uses the computed value
            //         so there's no stutter while migration is in
            //         flight.
            //      e. Default fallback: 0.85 x MaxRpm (the historical
            //         pre-tune behaviour).
            int? effectiveRedline = ResolveEffectiveRedline(redlineRpm, maxRpm);

            bool engaged = false;
            if (rpm >= MinEngineRpm)
            {
                if (effectiveRedline.HasValue && effectiveRedline.Value > MinEngineRpm)
                {
                    // Offset applies in every path now (the old code
                    // ignored it on the percentage path; under the
                    // unified model that's no longer a distinction).
                    double target = effectiveRedline.Value + RedlineOffsetRpm;
                    if (target < MinEngineRpm) target = MinEngineRpm;
                    engaged = rpm >= target;
                }
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (engaged) _lastActiveTicks = now;
            // Brief hold so RPM dipping a hair below the limit (limiter bounce,
            // a single low telemetry sample) doesn't chop the buzz. 80 ms is
            // ~1.6 pulses at 20 Hz.
            bool stillEngaged = _lastActiveTicks != 0
                && (now - _lastActiveTicks) < HoldStopwatchTicks;
            _amp = stillEngaged ? ActiveAmp : 0;
        }

        /// <summary>Open a render window for the REV self-test WITHOUT forcing
        /// _amp (unlike TestPlay, which slams it to ActiveAmp). The plugin then
        /// drives <see cref="DebugFeedRpm"/> across the window so the real
        /// threshold + hold logic decides when the buzz is on. Returns the
        /// duration in ms.</summary>
        public int StartRevTestWindow(int ms) { StartTest(ms); return ms; }

        /// <summary>Feed a synthetic (rpm, maxRpm) sample through the real
        /// engagement logic during a self-test. Runs regardless of IsTesting
        /// so the plugin's scheduled sequence controls the buzz; RenderAdd
        /// outputs the resulting _amp because the test window is open. The
        /// self-test sweep is built around the maxRpm percentage path, so we
        /// suppress the CarFactsRedline substitution for the duration of the
        /// call (otherwise a Forza car with a CarFacts-supplied redline in
        /// EngageMode.Redline would miss the sweep's 99%-of-MaxRpm peak).</summary>
        public void DebugFeedRpm(double rpm, double maxRpm)
        {
            var savedRedline = CarFactsRedline;
            CarFactsRedline = null;
            try { UpdateEngagement(rpm, 0.0, maxRpm); }
            finally { CarFactsRedline = savedRedline; }
        }

        public override void Reset()
        {
            _amp = 0;
            _lastActiveTicks = 0;
            _carrierPhase = 0;
            _pulsePhase = 0;
        }

        private static float SampleAt(Waveform w, double phase)
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
                default:                return 0f;
            }
        }
    }
}
