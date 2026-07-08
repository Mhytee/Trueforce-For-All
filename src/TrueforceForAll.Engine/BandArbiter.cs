// Frequency-aware priority ducking — phase 6 of docs/haptic-engine-plan.md.
//
// The tier ducker (DuckingController's L0-L3 ladder) ducks by CATEGORY:
// any transient ducks every continuous voice, whether or not they actually
// mask each other. But the G923 renders everything below ~250 Hz
// (docs/steering-feel-physics.md), and masking is a FREQUENCY phenomenon:
// a 35 Hz gear thump does not hide a 200 Hz front scrub, so ducking the
// scrub for it throws away grip information for nothing. Conversely an
// engine pulse sitting right on the scrub band buries exactly the cue that
// tells you the front is letting go.
//
// The arbiter ducks by CLASS x ACTIVITY x BAND OVERLAP:
//
//   GripState  (axle slip, lockup judder, traction loss)  — never ducked;
//              what the tire is doing is the highest-value signal on a
//              steering wheel and always wins its band.
//   Transient  (gear, ABS, kerb thump, collision, rev/pit) — ducked only
//              by GripState activity overlapping its band.
//   Ambience   (engine pulse, road texture, DRS hum, captured audio) —
//              ducked by both classes, GripState hardest (0.85: slip
//              ducks engine pulse HARD, per the plan).
//
// Effects declare their class and INSTANTANEOUS band via the phase-6
// effect contract (TelemetryEffect.PriorityClass / GetCurrentBand); the
// arbiter needs no per-effect knowledge. Attack/release smoothing reuses
// the SmoothDuck shape (fast duck, slow release) per target.

using System;
using System.Collections.Generic;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core
{
    public sealed class BandArbiter
    {
        /// <summary>GripState → Ambience duck depth (the plan's 0.85: slip
        /// activity all but silences same-band ambience).</summary>
        public float GripToAmbienceDepth { get; set; } = 0.85f;

        /// <summary>GripState → Transient duck depth. Moderate: alerts still
        /// punch through, they just yield headroom to same-band grip cues.</summary>
        public float GripToTransientDepth { get; set; } = 0.40f;

        // Transient → Ambience uses the user's sidechain depth slider,
        // passed per tick — same knob that drove the tier model.

        /// <summary>Band assumed for the captured-audio duck target (an
        /// IDuckable, not a TelemetryEffect): the wheel-relevant span of the
        /// audio path's default HP/LP window.</summary>
        public double AudioBandLoHz { get; set; } = 35.0;
        public double AudioBandHiHz { get; set; } = 250.0;

        private TelemetryEffect[] _targets;
        private float[] _mult;
        private readonly Dictionary<TelemetryEffect, int> _index
            = new Dictionary<TelemetryEffect, int>();
        private float _audioMult = 1f;

        /// <summary>Wire the effect set once. Order defines internal indexing;
        /// re-binding resets all multipliers to 1.</summary>
        public void Bind(TelemetryEffect[] effects)
        {
            _targets = effects ?? Array.Empty<TelemetryEffect>();
            _mult = new float[_targets.Length];
            _index.Clear();
            for (int i = 0; i < _targets.Length; i++)
            {
                _mult[i] = 1f;
                if (_targets[i] != null) _index[_targets[i]] = i;
            }
            _audioMult = 1f;
        }

        public bool IsBound => _targets != null;

        /// <summary>One arbitration step (engine tick rate). Computes the
        /// smoothed duck multiplier for every bound effect and the audio
        /// target.</summary>
        public void Arbitrate(float transientToAmbienceDepth, float attackMs, float releaseMs)
        {
            var fx = _targets;
            if (fx == null) return;

            for (int t = 0; t < fx.Length; t++)
            {
                var target = fx[t];
                if (target == null) continue;
                target.GetCurrentBand(out double tLo, out double tHi);
                double duck = StrongestDuckAgainst(
                    target.PriorityClass, tLo, tHi, transientToAmbienceDepth, exclude: target);
                _mult[t] = SmoothDuck(_mult[t], (float)Math.Max(0.0, 1.0 - duck), attackMs, releaseMs);
            }

            double audioDuck = StrongestDuckAgainst(
                EffectClass.Ambience, AudioBandLoHz, AudioBandHiHz,
                transientToAmbienceDepth, exclude: null);
            _audioMult = SmoothDuck(_audioMult, (float)Math.Max(0.0, 1.0 - audioDuck), attackMs, releaseMs);
        }

        /// <summary>Smoothed multiplier for a bound effect; 1.0 for anything
        /// unbound (never duck what we don't know).</summary>
        public float MultiplierFor(TelemetryEffect e)
            => e != null && _index.TryGetValue(e, out int i) ? _mult[i] : 1f;

        /// <summary>Smoothed multiplier for the captured-audio target.</summary>
        public float AudioMultiplier => _audioMult;

        private double StrongestDuckAgainst(EffectClass targetClass,
            double tLo, double tHi, float userDepth, TelemetryEffect exclude)
        {
            var fx = _targets;
            double duck = 0.0;
            for (int d = 0; d < fx.Length; d++)
            {
                var ducker = fx[d];
                if (ducker == null || ReferenceEquals(ducker, exclude)) continue;
                var dc = ducker.PriorityClass;
                if (dc <= targetClass) continue;          // only higher classes duck
                double act = ducker.ActivityLevel;
                if (act <= 0.001) continue;
                ducker.GetCurrentBand(out double dLo, out double dHi);
                double ov = Overlap01(dLo, dHi, tLo, tHi);
                if (ov <= 0.0) continue;                  // disjoint bands never mask
                float depth = DepthFor(dc, targetClass, userDepth);
                double v = depth * Math.Min(act, 1.0) * ov;
                if (v > duck) duck = v;
            }
            return duck;
        }

        private float DepthFor(EffectClass ducker, EffectClass target, float userDepth)
        {
            if (ducker == EffectClass.GripState)
                return target == EffectClass.Ambience ? GripToAmbienceDepth : GripToTransientDepth;
            return userDepth;   // Transient → Ambience: the user's slider
        }

        /// <summary>Fraction of the TARGET's band covered by the ducker's
        /// band, 0..1. Degenerate (zero-width) target bands count as fully
        /// covered when inside the ducker's span.</summary>
        internal static double Overlap01(double dLo, double dHi, double tLo, double tHi)
        {
            double lo = Math.Max(dLo, tLo);
            double hi = Math.Min(dHi, tHi);
            if (hi <= lo) return 0.0;
            double span = tHi - tLo;
            if (span <= 1.0) return 1.0;
            return Math.Min(1.0, (hi - lo) / span);
        }

        private static float SmoothDuck(float current, float target, float attackMs, float releaseMs)
        {
            float tau   = (target < current) ? attackMs : releaseMs;
            float alpha = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tau)));
            return current * (1f - alpha) + target * alpha;
        }
    }
}
