// Rev-limiter haptic. A hard, fast buzz that engages when engine RPM reaches
// the shift point / hits the limiter, and holds while you sit on (or bounce
// off) it. An independent, more obvious shift cue than the engine-pulse
// timbre change near redline, for users who run engine pulse low or off, or
// who just want an unmistakable "shift now / on the limiter" signal.
//
// Trigger is the resolved redline for the active car: the user's own value
// (Car facts, per variant, per gear), else the game's telemetry redline
// (SimHub's CarSettings_RedLineRPM, available on iRacing, AC, and any
// SimHub-fallback title), else the community consensus, else 0.85 x the hard
// rev limit (MaxRpm) as the guess for sources without a redline (e.g. Forza).
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
    // (RevLimiterEngageMode and the Manual/percentage modes were retired with
    // the car-facts centralization: the redline is car truth edited in the Car
    // facts panel, and the cascade below is the only resolution path.)

    public sealed class RevLimiterEffect : TelemetryEffect
    {
        // Display name. "Redline buzz", not "Rev limiter": the effect fires
        // at the redline START (the shift point), a little below the actual
        // limiter cutoff, and the old name kept inviting users to submit the
        // limiter RPM as the redline. Code identifiers + the RevLimiter JSON
        // keys keep the old name for serialization compat.
        public override string Name => "Redline buzz";

        /// <summary>Carrier tone within each pulse (Hz). Higher than the pit
        /// limiter's 50 Hz thud so it reads as an urgent buzz, not a low
        /// engine-cut pulse.</summary>
        public float Freq { get; set; } = 90.0f;

        // Phase 6 contract: an urgent alert buzz around its carrier.
        public override EffectClass PriorityClass => EffectClass.Transient;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            loHz = Freq * 0.7;
            hiHz = Freq * 1.5;
        }

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

        /// <summary>Offset in RPM applied to the resolved engage point on
        /// every path. Negative fires the buzz that many RPM BEFORE the
        /// redline (early shift cue); positive fires it after. 0 = right at
        /// the redline. The effective engage point is floored at
        /// <see cref="MinEngineRpm"/> so a large negative offset can't make
        /// it fire off idle.</summary>
        public float RedlineOffsetRpm { get; set; } = 0.0f;

        /// <summary>Optional CarFacts-supplied redline for the active car
        /// (set per car-change by the plugin from
        /// <c>Settings.CarFacts[...].EngineVariants[active].RedlineRpm</c>).
        /// When set AND the engage mode would pick "no redline" because the
        /// game doesn't expose one (Forza family), this is treated as the
        /// real redline. Cleared on car change. Per-machine, not preset-
        /// saved. Stage 1 wiring of the CarFacts layer.</summary>
        public int? CarFactsRedline { get; set; }

        /// <summary>Last redline RPM the cascade resolved (the value the buzz
        /// actually fires at, before <see cref="RedlineOffsetRpm"/>). Updated
        /// every engagement pass so the settings UI can show the live, derived
        /// redline as MaxRpm / the active variant changes. Null until the
        /// first telemetry frame.</summary>
        public int? EffectiveRedlineRpm { get; private set; }

        /// <summary>True when the active car is electric. Set by the plugin from
        /// the EnginePulse electric resolution. An EV has no engine rev limit to
        /// buzz at, so the limiter stays silent on EVs UNLESS the user has
        /// explicitly opted in with a pinned per-variant / per-gear redline -
        /// covers the rare modern EV with an enthusiast manual-style
        /// transmission.</summary>
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

        /// <summary>Stay silent in the car's TOP gear. This effect is a SHIFT
        /// CUE, not a limiter simulation (see the Name comment above): it fires
        /// at the redline start, a little below the actual cutoff. In top gear
        /// there is nothing to shift into, so the cue has no action behind it,
        /// and on a long straight it buzzes continuously for as long as the car
        /// is near the limit. Requires a KNOWN gear count; when the source does
        /// not publish one this does nothing at all.</summary>
        public bool SuppressInTopGear { get; set; } = true;

        /// <summary>Forward gear count from the telemetry source, or null when
        /// unknown. Set per frame from <see cref="TelemetryFrame.ForwardGearCount"/>.
        /// Deliberately never inferred from the highest gear observed: that
        /// reads 5th as top until the driver first uses 6th, silencing the cue
        /// exactly when it matters most.</summary>
        public int? ForwardGearCount { get; set; }

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
            if (f.ForwardGearCount.HasValue) ForwardGearCount = f.ForwardGearCount;
            UpdateEngagement(f.Rpms, f.RedlineRpm, f.MaxRpm);
        }

        // Gear strings are "1".."8" (forward), "N"/"R"/"0" otherwise. Only forward
        // gears map to a per-gear override; everything else uses the default.
        private static int ParseForwardGear(string g)
            => (!string.IsNullOrEmpty(g) && int.TryParse(g, out int n) && n >= 1) ? n : 0;

        // RPM-threshold + hold logic, shared by live telemetry and the REV
        // self-test. Sets _amp; RenderAdd plays it. Returns the absolute RPM
        // the limiter should fire at, or null when no value can be resolved
        // (engine not running, no telemetry).
        //
        // Order matters: a USER value always beats telemetry (their
        // deliberate correction must never silently no-op), but community
        // consensus deliberately sits BELOW the game telemetry redline.
        // The game is the source of truth when it reports one; a user who
        // disagrees sets their own redline, which then outranks telemetry.
        /// <summary>True when the rev-limiter buzz is firing on the default
        /// 0.85 × MaxRpm estimate because no authoritative redline was
        /// available (no user value, no telemetry redline, no community
        /// value). The Engine Pulse panel surfaces this as a badge so the
        /// user knows why the shift cue might feel off and gets pointed at
        /// the Car facts redline to fix it. False whenever any real source
        /// contributed.</summary>
        public bool IsRedlineGuessed { get; private set; }

        /// <summary>Per-gear redlines from a published car-data set, keyed by
        /// forward gear number. Null or empty (the default, and every car with no
        /// published entry) leaves the cascade exactly as it was.
        ///
        /// Sits below the game's telemetry redline AND below our own community
        /// consensus, above only the 0.85 estimate. Named for the kind of source
        /// rather than the project supplying it, so swapping or adding datasets
        /// never reaches into this effect.</summary>
        public System.Collections.Generic.Dictionary<int, int> PublishedGearRedlines { get; set; }

        private int? ResolveEffectiveRedline(double redlineRpm, double maxRpm)
        {
            // The cascade is per-variant and DERIVED LIVE every frame: the
            // user's per-gear/default redline, then the game telemetry
            // redline, then the community value, then the 0.85 x MaxRpm
            // guess. (EV suppression is applied per current gear, below.)

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
            // with a manual-style transmission), not the whole car.
            if (IsElectric)
            {
                IsRedlineGuessed = false;
                return null;
            }

            // Published per-car dataset, keyed by forward gear (currently the
            // lovely-car-data project; the field is named for the KIND of source
            // so this effect never has to know which one).
            //
            // Ranked above the game's own telemetry redline (owner, 2026-08-24),
            // which reads backwards until you notice the two are not the same
            // quantity. Telemetry reports the ENGINE LIMITER. This table records
            // the point the real car's DASH lights up, per gear, which is the
            // point the sim renders on its own dash and therefore the point the
            // driver is actually shifting at. A shift cue that fires at the
            // limiter is late by exactly the gap between them, and the wheel
            // flashing at one number while the screen flashes at another is the
            // symptom that sent us looking. Only the user's own pinned value
            // outranks it, because that is someone correcting us about this car.
            //
            // It is also the only tier that can be per-gear on a car no sim
            // describes that way: the M4 GT3 redlines at 7150 in first and 7250
            // in sixth, and nothing in telemetry carries that.
            //
            // Landing it HERE rather than at the LED call site is the point: the
            // rev-light flash, the fill onset and the limiter buzz all read
            // EffectiveRedlineRpm, so they move together instead of the lights
            // flashing at one number while the wheel buzzes at another. Splicing
            // it in at the LED call site instead produced exactly that split.
            //
            // Forward gears only: _currentGear is 0 for reverse and neutral, and
            // a published reverse ramp says nothing about a shift point. Same
            // sanity clamp as the community per-gear tier.
            if (_currentGear >= 1 && PublishedGearRedlines != null
                && PublishedGearRedlines.TryGetValue(_currentGear, out int pubGear)
                && pubGear > MinEngineRpm
                && (maxRpm <= MinEngineRpm || pubGear <= maxRpm * 1.05))
            {
                IsRedlineGuessed = false;
                return pubGear;
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
                // THE CASE THIS BLEND EXISTS FOR: our community agreed one value
                // for the car, the dataset has a full per-gear set. Ours stays the
                // anchor; theirs only bends it per gear.
                return CarFactsRedline.Value;
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
            // Engage-point resolution, all DERIVED LIVE each frame (never
            // pinned, so the buzz follows MaxRpm / variant changes):
            //   a. The user's own redline for the current gear, else the
            //      gear-0 default (per-gear overrides + the default).
            //   b. Published per-car dataset, per forward gear
            //      (PublishedGearRedlines) - where the real car's DASH lights
            //      up, which is where the sim flashes and where the driver
            //      shifts. Above telemetry deliberately; see the tier itself.
            //   c. Game-reported telemetry redline (sanity-gated 0.5..1.02
            //      x MaxRpm) - the engine limiter, and the source of truth for
            //      cars the published set does not cover.
            //   d. Community consensus (CarFactsRedline; mainly the no-
            //      telemetry-redline / Forza case).
            //   e. Default fallback: 0.85 x MaxRpm (the guess; badged).
            //   (b) is the only tier that varies BETWEEN gears on a car no sim
            //   describes that way; (a) and the per-gear community tier are gear
            //   specific too. (c) and (d) give one figure for the whole car and
            //   are taken as-is.
            //   (EVs stay silent after (a) unless the user opted that gear in.)
            int? effectiveRedline = ResolveEffectiveRedline(redlineRpm, maxRpm);
            EffectiveRedlineRpm = effectiveRedline;

            // Top gear: nothing to shift into, so the cue has no action behind
            // it. Computed AFTER the redline resolution above so the settings
            // panel still shows the live derived redline, and applied through
            // the same hold/decay path below so the buzz fades out rather than
            // being chopped. Needs a real gear count (>= 2 so a single-speed
            // car cannot silence itself permanently); unknown means no opinion.
            bool topGearMuted = SuppressInTopGear
                && ForwardGearCount.HasValue && ForwardGearCount.Value >= 2
                && _currentGear >= ForwardGearCount.Value;

            bool engaged = false;
            if (rpm >= MinEngineRpm && !topGearMuted)
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
        /// self-test sweep is built around the 0.85 x MaxRpm fallback, so we
        /// suppress the user / community tiers for the duration of the call
        /// (a pinned redline or per-gear value would otherwise fire across
        /// the sweep's intended-silent phase).</summary>
        public void DebugFeedRpm(double rpm, double maxRpm)
        {
            var savedRedline   = CarFactsRedline;
            var savedGears     = UserGearRedlines;
            var savedCommGears = CommunityGearRedlines;
            CarFactsRedline = null;
            UserGearRedlines = null;
            CommunityGearRedlines = null;
            try { UpdateEngagement(rpm, 0.0, maxRpm); }
            finally
            {
                CarFactsRedline = savedRedline;
                UserGearRedlines = savedGears;
                CommunityGearRedlines = savedCommGears;
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
