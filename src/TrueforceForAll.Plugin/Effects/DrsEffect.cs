// DRS (Drag Reduction System) haptic. F1 / F1-style sims: when the wing
// opens, the driver feels a release / buffeting sensation. Modeled as
// two layered components:
//
//   1. Activation chirp — short envelope-shaped burst on the rising edge
//      of DrsActive (wing opening). Tells the driver the system actually
//      engaged. Same shape as GearShiftEffect's thud, but higher-pitched
//      and shorter.
//
//   2. Sustained flutter — continuous low-amplitude tone while DRS stays
//      active. Reminds the driver they're in DRS mode without becoming
//      fatiguing. Settable to zero amp to disable the sustained part.
//
// Telemetry source: TelemetryFrame.DrsActive. Null on sources that don't
// expose it (most non-F1 games) — effect stays silent rather than firing
// false positives.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class DrsEffect : TelemetryEffect
    {
        public override string Name => "DRS";

        // ---- Activation chirp (rising edge) ----

        /// <summary>Carrier freq for the activation burst (Hz). Higher than
        /// pit limiter / engine pulse so it punches through other haptics.</summary>
        public float ActivationFreq { get; set; } = 120.0f;

        /// <summary>Duration of the activation envelope (ms).</summary>
        public int ActivationMs { get; set; } = 60;

        /// <summary>Peak amplitude of the activation burst.</summary>
        public float ActivationAmp { get; set; } = 0.30f;

        // ---- Sustained tone (held while active) ----

        /// <summary>Carrier freq while DRS stays open (Hz). Default below
        /// the activation freq so the chirp + sustained components are
        /// distinguishable.</summary>
        public float SustainedFreq { get; set; } = 70.0f;

        /// <summary>Amplitude of the sustained tone. Set to 0 to disable
        /// the sustained component (chirp-only mode).</summary>
        public float SustainedAmp { get; set; } = 0.12f;

        public Waveform Waveform { get; set; } = Waveform.Sine;

        private const double SampleRate = 4000.0;

        // Activation envelope state (samples remaining + total).
        private int    _envelopeRemaining;
        private int    _envelopeTotal;
        private double _activationPhase;

        // Sustained-tone state.
        private bool   _drsHeld;
        private int    _lastDrsValue;
        private double _sustainedPhase;

        public override bool IsActive
            => IsTesting || (Enabled && (_envelopeRemaining > 0 || _drsHeld));

        public override double ActivityLevel
        {
            get
            {
                if (_envelopeRemaining > 0 && _envelopeTotal > 0)
                    return Math.Min(1.0, (double)_envelopeRemaining / _envelopeTotal);
                return _drsHeld ? 0.4 : 0;
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float dm = DuckMultiplier;
            if (dm <= 0f) return;

            double aStep = Math.Max(0.0, ActivationFreq) / SampleRate;
            double sStep = Math.Max(0.0, SustainedFreq)  / SampleRate;
            float aAmp = ActivationAmp * Gain * dm;
            float sAmp = SustainedAmp  * Gain * dm;
            int total  = _envelopeTotal;
            Waveform w = Waveform;

            for (int i = 0; i < count; i++)
            {
                float sample = 0f;

                // Activation chirp (linear-decay envelope)
                if (_envelopeRemaining > 0 && total > 0)
                {
                    float env = (float)_envelopeRemaining / total;
                    sample += SampleAt(w, _activationPhase) * env * aAmp;
                    _activationPhase += aStep;
                    if (_activationPhase >= 1.0) _activationPhase -= Math.Floor(_activationPhase);
                    _envelopeRemaining--;
                }

                // Sustained tone while held
                if (_drsHeld && sAmp > 0f)
                {
                    sample += SampleAt(w, _sustainedPhase) * sAmp;
                    _sustainedPhase += sStep;
                    if (_sustainedPhase >= 1.0) _sustainedPhase -= Math.Floor(_sustainedPhase);
                }

                buffer[i] += sample;
            }
        }

        public override int TestPlay()
        {
            // Fire activation, hold sustained for a moment, then release.
            TriggerActivation();
            _drsHeld = true;
            StartTest(1500);
            return 1500;
        }

        public override void TestUpdate(double phase01)
        {
            // Drop the held flag near the end so the test naturally tails off.
            if (phase01 > 0.85) _drsHeld = false;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            int v = f.DrsActive ?? 0;
            // Rising edge → fire activation chirp.
            if (v > 0 && _lastDrsValue == 0) TriggerActivation();
            _lastDrsValue = v;
            _drsHeld = v > 0;
        }

        private void TriggerActivation()
        {
            int samples = (int)(ActivationMs * SampleRate / 1000.0);
            if (samples < 1) samples = 1;
            _envelopeTotal = samples;
            _envelopeRemaining = samples;
            _activationPhase = 0;
        }

        public override void Reset()
        {
            _envelopeRemaining = 0;
            _envelopeTotal = 0;
            _activationPhase = 0;
            _sustainedPhase = 0;
            _drsHeld = false;
            _lastDrsValue = 0;
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
