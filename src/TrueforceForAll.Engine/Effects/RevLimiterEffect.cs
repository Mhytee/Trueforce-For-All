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

        /// <summary>Live, unsaved redline the user is currently dragging on the
        /// slider in Auto mode (a per-variant draft). Top priority in the Auto
        /// cascade so the buzz + UI preview the dragged value immediately.
        /// Never persisted; the plugin clears it on variant / car change and
        /// when the draft is saved (promoted to the variant's UserRedlineRpm)
        /// or reverted.</summary>
        public int? PreviewRedlineRpm { get; set; }

        /// <summary>Manual-mode redline value. Mirrors
        /// <see cref="TrueforceForAll.Plugin.RevLimiterSettings.RedlineRpm"/>;
        /// the apply path copies the preset's value into the effect on
        /// each ApplyEngineSettings call. Honored ONLY in Manual mode, where
        /// the buzz fires at this fixed RPM (plus <see cref="RedlineOffsetRpm"/>)
        /// regardless of telemetry. In Auto mode the buzz point is derived live
        /// from the cascade (CarFacts / telemetry / Threshold-percent) so it
        /// follows MaxRpm changes.</summary>
        public int? RedlineRpm { get; set; }

        /// <summary>Last redline RPM the cascade resolved (the value the buzz
        /// actually fires at, before <see cref="RedlineOffsetRpm"/>). Updated
        /// every engagement pass so the settings UI can show the live, derived
        /// redline as MaxRpm / the active variant changes. Null until the
        /// first telemetry frame.</summary>
        public int? EffectiveRedlineRpm { get; private set; }

        /// <summary>True when the active car is electric. Set by the plugin from
        /// the EnginePulse electric resolution. An EV has no engine rev limit to
        /// buzz at, so the limiter stays silent on EVs UNLESS the user has
        /// explicitly opted in (Manual mode with a value, a pinned per-variant
        /// redline, or a live slider draft) - covers the rare modern EV with an
        /// enthusiast manual-style transmission.</summary>
        public bool IsElectric { get; set; }

        /// <summary>The active variant's user redlines keyed by gear (gear 0 =
        /// default / all-gears, 1..N = per-gear overrides). Set by the plugin on
        /// resolve. The buzz uses the current gear's entry, else gear 0. null /
        /// empty = no user redline.</summary>
        public System.Collections.Generic.Dictionary<int, int> UserGearRedlines { get; set; }

        /// <summary>The community per-gear redline consensus for the active variant
        /// keyed by forward gear (1..16). Set by the plugin's resolver, already
        /// support-floored per gear (each entry has real multi-driver agreement).
        /// Sits BELOW the user's own values and the game telemetry redline in the
        /// cascade, but lets an accurate community gear value reach the buzz even
        /// when the user hasn't adopted it. null / empty = none. The overall
        /// (gear-0) community value stays in <see cref="CarFactsRedline"/>.</summary>
        public System.Collections.Generic.Dictionary<int, int> CommunityGearRedlines { get; set; }

        // Current forward gear (1..N); 0 = neutral / reverse / unknown -> default.
        private int _currentGear;

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
                    buffer[i] += WaveformMath.SampleAt(w, _carrierPhase) * amp;
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
            _currentGear = ParseForwardGear(f.Gear);
            UpdateEngagement(f.Rpms, f.RedlineRpm, f.MaxRpm);
        }

        // Gear strings are "1".."8" (forward), "N"/"R"/"0" otherwise. Only forward
        // gears map to a per-gear override; everything else uses the default.
        private static int ParseForwardGear(string g)
            => (!string.IsNullOrEmpty(g) && int.TryParse(g, out int n) && n >= 1) ? n : 0;

        // RPM-threshold + hold logic, shared by live telemetry and the REV
        // self-test. Thresholds against the redline when the source provides it,
        // else the hard rev limit. Sets _amp; RenderAdd plays it.
        // Returns the absolute RPM the limiter should fire at, or null
        // when no value can be resolved (engine not running, no preset
        // value, no telemetry).
        //
        // RedlineRpm semantics across the modes:
        //   * Manual (or legacy "Redline" enum value): always use
        //     Settings.RedlineRpm if set; null = engine off.
        //   * Auto: live slider draft, then the user's per-gear/default
        //     redline, then the game telemetry redline (sanity-gated),
        //     then the community per-gear / variant consensus (banded),
        //     then Threshold-percent of the live MaxRpm, then the 0.85
        //     default. A stored RedlineRpm is NOT used in Auto (it can't
        //     follow MaxRpm); the percentage scales instead.
        //
        // Order matters: a USER value always beats telemetry (their
        // deliberate correction must never silently no-op), but community
        // consensus deliberately sits BELOW the game telemetry redline.
        // The game is the source of truth when it reports one; the adopt
        // prompt is the explicit path that promotes a community value into
        // a user value, which then outranks telemetry.
        /// <summary>True when the rev-limiter buzz is firing on the default
        /// 0.85 × MaxRpm estimate because no authoritative redline was
        /// available (no telemetry redline, no CarFacts, no tuned
        /// Threshold percentage). The Engine Pulse panel
        /// surfaces this as a badge so the user knows why the shift cue
        /// might feel off and gets a nudge to either set a value or
        /// share telemetry. False in Manual mode (the user explicitly
        /// chose their value), or whenever any other source contributed.
        /// </summary>
        public bool IsRedlineGuessed { get; private set; }

        private int? ResolveEffectiveRedline(double redlineRpm, double maxRpm)
        {
            bool manualMode =
                EngageMode == RevLimiterEngageMode.Redline;   // legacy "Redline" -> Manual

            if (manualMode)
            {
                if (RedlineRpm.HasValue && RedlineRpm.Value > MinEngineRpm)
                {
                    IsRedlineGuessed = false;
                    return RedlineRpm.Value;
                }
                // Manual without a saved value: fall through to the
                // Auto cascade rather than failing closed. Matches
                // legacy behaviour where Redline mode with no telemetry
                // would still attempt the sanity-gated read.
            }

            // Auto cascade is per-variant and DERIVED LIVE every frame: a live
            // slider draft, then the user's per-gear/default redline, then the
            // game telemetry redline, then the community value, then a percentage
            // of MaxRpm. (EV suppression is applied per current gear, below.)

            // Live slider draft (unsaved): the user is dragging a redline for
            // the current variant. Top priority so the buzz + readout preview
            // it immediately, before they commit it.
            if (PreviewRedlineRpm.HasValue && PreviewRedlineRpm.Value > MinEngineRpm)
            {
                IsRedlineGuessed = false;
                return PreviewRedlineRpm.Value;
            }

            // The user's own redline for the CURRENT gear, else their default
            // (gear 0). Per-gear entries override the default for that gear;
            // gears with no entry (and reverse / neutral) use the default. A
            // deliberate user value, so it wins over telemetry + community.
            if (UserGearRedlines != null && UserGearRedlines.Count > 0)
            {
                int ug = 0;
                if (_currentGear >= 1 && UserGearRedlines.TryGetValue(_currentGear, out int perGear))
                    ug = perGear;
                else if (UserGearRedlines.TryGetValue(0, out int dflt))
                    ug = dflt;
                if (ug > MinEngineRpm)
                {
                    IsRedlineGuessed = false;
                    return ug;
                }
            }

            // Electric cars have no engine rev limit to buzz at, so once the
            // user's own gear value (above) hasn't fired, the limiter stays
            // silent on an EV instead of falling through to telemetry / the
            // 0.85 guess. This is gear-aware: a per-gear opt-in only un-silences
            // the gear(s) the user configured (covering the rare enthusiast EV
            // with a manual-style transmission), not the whole car. A live
            // slider draft already returned above.
            if (IsElectric)
            {
                IsRedlineGuessed = false;
                return null;
            }

            // Game-reported telemetry redline (sanity-gated). The game is the
            // source of truth when it provides one, so it beats the crowd-sourced
            // community consensus below.
            if (redlineRpm > MinEngineRpm
                && (maxRpm <= MinEngineRpm
                    || (redlineRpm <= maxRpm * 1.02 && redlineRpm >= maxRpm * 0.5)))
            {
                IsRedlineGuessed = false;
                return (int)Math.Round(redlineRpm);
            }

            // Community per-gear consensus for the CURRENT gear (each gear was
            // agreed + support-floored independently by the plugin). More
            // specific than the overall community value below, so it wins for
            // that gear, but still sits below the game telemetry redline (the
            // game is the source of truth; the user adopts to promote a community
            // value above telemetry). Only forward gears; light sanity clamp so a
            // per-gear value can't sit above the rev ceiling.
            if (_currentGear >= 1 && CommunityGearRedlines != null
                && CommunityGearRedlines.TryGetValue(_currentGear, out int commGear)
                && commGear > MinEngineRpm
                && (maxRpm <= MinEngineRpm || commGear <= maxRpm * 1.05))
            {
                IsRedlineGuessed = false;
                return commGear;
            }

            // Community consensus for this variant (set by the plugin's resolver
            // into CarFactsRedline). Reached only when the user pinned nothing
            // and the game reported no redline (the Forza-style case). This is
            // untrusted crowd data AND the sole driver of the engage point
            // here, so it's sanity-banded against MaxRpm: an implausible
            // consensus (typo, wrong-car mapping, modded outlier) is rejected
            // and we fall through to the percentage / 0.85 default rather than
            // buzzing at a wrong RPM. EVs never reach this path (IsElectric
            // returned null above), so the floor can't clip a low EV redline.
            // See RevLimiterMath.IsCommunityRedlinePlausible for the band.
            if (CarFactsRedline.HasValue
                && RevLimiterMath.IsCommunityRedlinePlausible(
                    CarFactsRedline.Value, maxRpm, MinEngineRpm))
            {
                IsRedlineGuessed = false;
                return CarFactsRedline.Value;
            }

            // Percentage path: a tuned Threshold (anything other than the
            // 0.85 default) is applied to the LIVE MaxRpm every frame so the
            // buzz auto-follows in-game tune changes (Forza upgrades that move
            // the rev ceiling). No pinning, no one-time migration: Threshold is
            // the source of truth and re-derives each call. Snapped to 50 RPM
            // (not 500) so the derived value is precise without jittering on
            // telemetry noise.
            if (maxRpm > MinEngineRpm
                && Math.Abs(Threshold - 0.85f) > 0.0001f
                && Threshold >= 0.50f && Threshold <= 1.00f)
            {
                int derived = (int)Math.Round(maxRpm * Threshold / 50.0) * 50;
                if (derived > MinEngineRpm)
                {
                    IsRedlineGuessed = false;
                    return derived;
                }
            }

            // Default fallback: 0.85 of MaxRpm so games without any
            // tuning still fire a sensible shift cue. This IS the guess
            // case - no one has measured / corrected / submitted, so the
            // UI surfaces a badge nudging the user to fix it.
            if (maxRpm > MinEngineRpm)
            {
                IsRedlineGuessed = true;
                return (int)Math.Round(maxRpm * 0.85);
            }

            IsRedlineGuessed = false;
            return null;
        }

        private void UpdateEngagement(double rpm, double redlineRpm, double maxRpm)
        {
            // Unified engage-point resolution (in priority order):
            //   1. EngageMode = Manual + Settings.RedlineRpm set
            //      (legacy "Redline" enum value collapses here for
            //      backward compat).
            //   2. EngageMode = Auto (all DERIVED LIVE each frame, never
            //      pinned, so the buzz follows MaxRpm / variant changes):
            //      a. Live slider draft (PreviewRedlineRpm).
            //      b. The user's own redline for the current gear, else the
            //         gear-0 default (per-gear overrides + the default).
            //      c. Game-reported telemetry redline (sanity-gated 0.5..1.02
            //         x MaxRpm) - the game is the source of truth, so it beats
            //         the community consensus below.
            //      d. Community consensus (CarFactsRedline; mainly the no-
            //         telemetry-redline / Forza case).
            //      e. Threshold-percent of the live MaxRpm (legacy tuning).
            //      f. Default fallback: 0.85 x MaxRpm.
            //   (EVs stay silent after (b) unless the user opted that gear in.)
            int? effectiveRedline = ResolveEffectiveRedline(redlineRpm, maxRpm);
            EffectiveRedlineRpm = effectiveRedline;

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

        // Telemetry stopped: if the game closed while at the rev limit, the buzz
        // would hold forever. Drop the amplitude and clear the hold so it can't
        // re-engage off a stale timestamp.
        public override void OnTelemetryStall()
        {
            _amp = 0;
            _lastActiveTicks = 0;
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
            // Suppress every user/community tier so the sweep exercises only the
            // maxRpm percentage path it's built around (a pinned redline, a per-
            // gear value, or a live slider draft would otherwise fire across the
            // intended-silent phase).
            var savedRedline   = CarFactsRedline;
            var savedGears     = UserGearRedlines;
            var savedCommGears = CommunityGearRedlines;
            var savedPreview   = PreviewRedlineRpm;
            CarFactsRedline = null;
            UserGearRedlines = null;
            CommunityGearRedlines = null;
            PreviewRedlineRpm = null;
            try { UpdateEngagement(rpm, 0.0, maxRpm); }
            finally
            {
                CarFactsRedline = savedRedline;
                UserGearRedlines = savedGears;
                CommunityGearRedlines = savedCommGears;
                PreviewRedlineRpm = savedPreview;
            }
        }

        public override void Reset()
        {
            _amp = 0;
            _lastActiveTicks = 0;
            _carrierPhase = 0;
            _pulsePhase = 0;
            // Clear the resolved-redline readout so the UI doesn't briefly show
            // the previous car's redline between a car change and the first
            // telemetry frame for the new car (Reset runs in the car-change
            // handler before any new frame arrives).
            EffectiveRedlineRpm = null;
            IsRedlineGuessed = false;
            _currentGear = 0;   // default to the gear-0 redline until telemetry arrives
            // Drop the per-car redline tiers so they can't survive a Reset that
            // isn't immediately followed by a resolver pass (the resolver re-sets
            // all three on the next car's resolve). Keeps the "no prior car's
            // redline" invariant local to the effect.
            CommunityGearRedlines = null;
            UserGearRedlines = null;
            CarFactsRedline = null;
        }
    }
}
