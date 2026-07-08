// Per-axle slip voices (phase 6 shape, pulled forward onto the phase 2 CTM
// rollups): the wheel finally tells your hands WHICH END is sliding.
//
//   Front voice — tire scrub, 150–250 Hz: onset as FrontGrip01 approaches
//   the limit, pitch climbing with utilization. Past the limit the scrub is
//   amplitude-modulated by a ~14 Hz stick-slip judder (grip-slip-grip) whose
//   depth grows with the excess — classic understeer chatter.
//
//   Rear voice — pulse train, 40–70 Hz: same onset law on RearGrip01. Low
//   and thumpy so it reads as "the back of the car", distinct from the
//   front's high scrub even when both fire.
//
//   Balance-gated salience: GripBalance decides which voice matters. Deep
//   oversteer ducks the front scrub so the rear pulse owns the texture;
//   deep understeer mutes the rear. Both fade smoothly through neutral.
//
// Drives from the CTM rollups only (null rollups = silent), so the SimHub
// fallback and quad-less sources never fire it. TractionLossEffect stays as
// the undifferentiated fallback voice; on rollup sources you may want its
// gain lower since these voices carry the detail now.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class AxleSlipEffect : TelemetryEffect
    {
        public override string Name => "Axle slip";

        /// <summary>Utilization where a voice starts whispering; full
        /// amplitude at the limit (1.0).</summary>
        public float OnsetUtil { get; set; } = 0.85f;

        // Front scrub band.
        public float FrontFreqMinHz { get; set; } = 150f;
        public float FrontFreqMaxHz { get; set; } = 250f;
        public float FrontAmp       { get; set; } = 0.30f;

        // Stick-slip judder on the front voice past the limit.
        public float JudderHz       { get; set; } = 14f;
        public float JudderMaxDepth { get; set; } = 0.8f;

        // Rear pulse band.
        public float RearFreqMinHz { get; set; } = 40f;
        public float RearFreqMaxHz { get; set; } = 70f;
        public float RearAmp       { get; set; } = 0.35f;

        /// <summary>How hard the grip balance ducks the "other" axle's voice
        /// at full imbalance. 0.7 = the losing voice keeps 30%.</summary>
        public float SalienceDepth { get; set; } = 0.7f;

        /// <summary>GripBalance magnitude treated as full imbalance.</summary>
        public float SalienceFullAt { get; set; } = 0.35f;

        // Phase 6 contract: slip scrub IS grip information — top class, and
        // the band tracks the live voices so the arbiter ducks only what the
        // scrub actually masks.
        public override EffectClass PriorityClass => EffectClass.GripState;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            bool front = _frontAmp > 0.005f || _frontTargetAmp > 0.01f;
            bool rear  = _rearAmp  > 0.005f || _rearTargetAmp  > 0.01f;
            double lo = rear || !front ? _rearFreq  : _frontFreq;
            double hi = front || !rear ? _frontFreq : _rearFreq;
            loHz = Math.Min(lo, hi) * 0.8;
            hiHz = Math.Max(lo, hi) * 1.25;
        }

        /// <summary>Test layer 4 — predictive onset: lead the front voice by
        /// extrapolating utilization along its rate of change, so the scrub
        /// whispers BEFORE the limit arrives when you're rushing at it (a
        /// slow build gets no lead — it isn't a surprise). 0 = off.</summary>
        public float PredictiveLeadMs { get; set; } = 0f;

        /// <summary>Cap on how much utilization the lead can add — keeps a
        /// noisy derivative from screaming on kerb spikes.</summary>
        public float PredictiveMaxBoost { get; set; } = 0.25f;

        /// <summary>Test layer 5 — rev-locked rear pulse: the rear voice's
        /// pulse rate tracks the actual rear wheel revolution rate instead of
        /// the utilization-mapped band, so a burnout thumps at exactly the
        /// spinning wheels' speed. Falls back to the band mapping when the
        /// source has no wheel-rotation quad.</summary>
        public bool RevLockedRearPulse { get; set; } = false;

        private const double SampleRateHz = 4000.0;
        private const float  AmpEmaAlpha  = 0.25f;   // per 500 Hz telemetry tick

        // Telemetry-thread → render-thread state (float writes are atomic).
        private float _frontTargetAmp, _rearTargetAmp;
        private float _frontFreq = 150f, _rearFreq = 40f;
        private float _judderDepth;

        // Render-thread state.
        private float  _frontAmp, _rearAmp;
        private double _frontPhase, _rearPhase, _judderPhase;

        // Predictive-lead state (telemetry thread, 500 Hz cadence).
        private float _prevUF;
        private float _duDtEma;
        private const float TickSec = 0.002f;   // EngineLoop effect tick

        public override bool IsActive =>
            IsTesting || (Enabled && (_frontTargetAmp > 0.01f || _rearTargetAmp > 0.01f
                                      || _frontAmp > 0.005f || _rearAmp > 0.005f));

        public override double ActivityLevel => Math.Max(_frontAmp, _rearAmp);

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            if (!f.FrontGrip01.HasValue || !f.RearGrip01.HasValue)
            {
                _frontTargetAmp = 0f;
                _rearTargetAmp  = 0f;
                return;
            }

            float uF = (float)f.FrontGrip01.Value;
            float uR = (float)f.RearGrip01.Value;

            // Layer 4: predictive onset. Extrapolate the front utilization
            // PredictiveLeadMs into the future along its (smoothed) rate of
            // change — only forward (rising) and capped, so a settling or
            // noisy signal never pre-fires.
            float uFOnset = uF;
            if (PredictiveLeadMs > 0f)
            {
                float duDt = (uF - _prevUF) / TickSec;
                _duDtEma += (duDt - _duDtEma) * 0.15f;
                float boost = _duDtEma * (PredictiveLeadMs / 1000f);
                if (boost > 0f)
                    uFOnset = uF + Math.Min(boost, PredictiveMaxBoost);
            }
            _prevUF = uF;

            float front01 = Onset(uFOnset);
            float rear01  = Onset(uR);

            // Salience: positive balance = rear closer to the limit =
            // oversteer = duck the front; negative ducks the rear.
            float bal = (float)(f.GripBalance ?? 0.0);
            float s = Math.Min(Math.Abs(bal) / Math.Max(0.05f, SalienceFullAt), 1f) * SalienceDepth;
            if (bal > 0) front01 *= 1f - s;
            else if (bal < 0) rear01 *= 1f - s;

            _frontTargetAmp = front01 * FrontAmp;
            _rearTargetAmp  = rear01 * RearAmp;

            // Pitch climbs with utilization toward the band top at 1.5.
            _frontFreq = Lerp(FrontFreqMinHz, FrontFreqMaxHz, Math.Min(uF / 1.5f, 1f));

            // Layer 5: rev-locked rear pulse — thump at the spinning wheels'
            // actual revolution rate when the source provides it.
            if (RevLockedRearPulse && f.HasTireQuads)
            {
                double rearRadS = Math.Max(Math.Abs(f.WheelRotRadS.RL), Math.Abs(f.WheelRotRadS.RR));
                float revHz = (float)(rearRadS / (2.0 * Math.PI));
                _rearFreq = Math.Min(Math.Max(revHz, RearFreqMinHz), 90f);
            }
            else
            {
                _rearFreq = Lerp(RearFreqMinHz, RearFreqMaxHz, Math.Min(uR / 1.5f, 1f));
            }

            // Judder engages only past the limit, full depth half a unit out.
            _judderDepth = uF <= 1f ? 0f
                : Math.Min((uF - 1f) / 0.5f, 1f) * JudderMaxDepth;
        }

        private float Onset(float u)
        {
            float onset = OnsetUtil;
            if (u <= onset) return 0f;
            return Math.Min((u - onset) / Math.Max(0.05f, 1f - onset), 1f);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;

            float fTarget = _frontTargetAmp, rTarget = _rearTargetAmp;
            if (fTarget <= 0.0001f && rTarget <= 0.0001f
                && _frontAmp <= 0.0001f && _rearAmp <= 0.0001f) return;

            double fStep = _frontFreq / SampleRateHz;
            double rStep = _rearFreq / SampleRateHz;
            double jStep = JudderHz / SampleRateHz;
            float jDepth = _judderDepth;
            float scale = Gain * DuckMultiplier;

            for (int i = 0; i < count; i++)
            {
                // Per-sample amp smoothing keeps onset/release click-free even
                // if telemetry steps (tau ≈ 20 ms at 4 kHz).
                _frontAmp += (fTarget - _frontAmp) * 0.012f;
                _rearAmp  += (rTarget - _rearAmp) * 0.012f;

                if (_frontAmp > 0.0001f)
                {
                    float judder = jDepth <= 0f ? 1f
                        : 1f - jDepth * (0.5f + 0.5f * (float)Math.Sin(2.0 * Math.PI * _judderPhase));
                    buffer[i] += (float)Math.Sin(2.0 * Math.PI * _frontPhase) * _frontAmp * judder * scale;
                }
                if (_rearAmp > 0.0001f)
                    buffer[i] += (float)Math.Sin(2.0 * Math.PI * _rearPhase) * _rearAmp * scale;

                _frontPhase += fStep; if (_frontPhase >= 1.0) _frontPhase -= 1.0;
                _rearPhase  += rStep; if (_rearPhase >= 1.0) _rearPhase -= 1.0;
                _judderPhase += jStep; if (_judderPhase >= 1.0) _judderPhase -= 1.0;
            }
        }

        public override int TestPlay()
        {
            // Simulate a front breakaway with judder, then let TestUpdate
            // swing the balance rearward so both voices demo.
            _frontTargetAmp = FrontAmp;
            _rearTargetAmp  = 0f;
            _frontFreq      = FrontFreqMaxHz;
            _rearFreq       = RearFreqMaxHz;
            _judderDepth    = JudderMaxDepth;
            const int duration = 1200;
            StartTest(duration);
            return duration;
        }

        public override void TestUpdate(double phase01)
        {
            // First half: front scrub + judder. Second half: rear pulse.
            if (phase01 < 0.5)
            {
                _frontTargetAmp = FrontAmp;
                _rearTargetAmp  = 0f;
            }
            else
            {
                _frontTargetAmp = 0f;
                _rearTargetAmp  = RearAmp;
                _judderDepth    = 0f;
            }
        }

        public override void OnTelemetryStall()
        {
            _frontTargetAmp = 0f;
            _rearTargetAmp  = 0f;
            _judderDepth    = 0f;
        }

        public override void Reset()
        {
            _frontTargetAmp = 0f;
            _rearTargetAmp  = 0f;
            _judderDepth    = 0f;
            _frontAmp = 0f;
            _rearAmp  = 0f;
            _frontPhase = _rearPhase = _judderPhase = 0;
            _prevUF = 0f;
            _duDtEma = 0f;
        }
    }
}
