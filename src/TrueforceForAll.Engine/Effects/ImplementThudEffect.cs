// Implement thud: the mechanical clunk of farm equipment changing state, a
// plow dropping into its linkage, a mower lifting for the headland turn.
// Retriggered on each edge of TelemetryFrame.ImplementWorking (the FS mod's
// lowered-or-powered flag); lowering lands the full thud, raising a softer
// one (linkage catching vs releasing). Same inline synth shape as
// GearShiftEffect, just lower and longer: implements are heavier than
// gearboxes. Fires only on sources that report the flag (Farming Simulator),
// inert everywhere else.
//
// The hydraulic hum is a living voice, not a flat tone: its pitch spools up
// from below the set hum frequency on each motion edge (pump loading at the
// start, the relief flick over the fade-out at the stop), rides a little
// higher the faster the linkage actually travels (mod 0.2.14+ reports the
// rate; older mods leave it at neutral), and carries a quiet phase-locked
// octave layer so it reads as machinery rather than a pure sine.
//
// Motion covers joints, folds, pipe travel (a combine's unloading pipe,
// mod 0.2.16+) and manually driven arms (loader/crane/telehandler moving
// tools, mod 0.2.18+). Discrete toggles with no travel (the straw-swath
// flap) arrive as an event counter and land the light release-weight thud.
// Multi-part machines report each STAGE end too (mod 0.2.22+): a lift
// finishing before the wings fold lands its own momentum-weighted thud
// while the hum plays on, so a big unfold reads clunk, hum, clunk, hum,
// then the full settle.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class ImplementThudEffect : TelemetryEffect
    {
        public override string Name => "Implement thud";

        /// <summary>Thud frequency (Hz). Lower than gear shift's 35: a
        /// three-point linkage is a bigger, duller mechanism.</summary>
        public float Freq { get; set; } = 30.0f;

        // Momentary alert around its knock frequency, same contract as the
        // gear-shift thud. While the hydraulic hum is speaking, the band
        // widens to cover it so the frequency-aware arbiter sees the voice
        // that is actually playing.
        public override EffectClass PriorityClass => EffectClass.Transient;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            loHz = Freq * 0.7;
            hiHz = Freq * 1.6;
            if (_humTarget || _humLevel > 0.01f)
            {
                // 0.8 covers the bend's low start; 2.3 covers the octave
                // layer with the speed ride on top (2 * 1.12, plus margin).
                loHz = Math.Min(loHz, HumFreq * 0.8);
                hiHz = Math.Max(hiHz, HumFreq * 2.3);
            }
        }

        /// <summary>Envelope length in milliseconds.</summary>
        public int EnvelopeMs { get; set; } = 120;

        /// <summary>Peak amplitude at the start of the envelope.</summary>
        public float PeakAmp { get; set; } = 0.35f;

        public Waveform Waveform { get; set; } = Waveform.Sine;

        /// <summary>Amplitude scale for the RAISE edge (working stops). The
        /// linkage releasing is a lighter event than the tool landing.</summary>
        public float RaiseAmp { get; set; } = 0.6f;

        /// <summary>Hydraulic hum amplitude while a lower/raise/fold is in
        /// motion (mod 0.2.8+ reports ImplementMoving). 0 = no hum.</summary>
        public float HumAmp { get; set; } = 0.30f;

        /// <summary>Hum frequency (Hz). A pump whine below the thud's knock
        /// so the two read as one mechanism.</summary>
        public float HumFreq { get; set; } = 46.0f;

        private const double SampleRateHz = 4000.0;

        /// <summary>How far below the hum pitch the spool-up bend starts,
        /// as a fraction (0.15 = start 15% low, glide up). Re-armed on both
        /// motion edges. 0 = no bend.</summary>
        public float BendDepth { get; set; } = 0.15f;

        /// <summary>How much the travel rate raises the hum pitch, as the
        /// fraction added at a fast lower (mod 0.2.14+ reports the rate;
        /// older mods stay neutral). 0 = fixed pitch.</summary>
        public float SpeedPitch { get; set; } = 0.12f;

        /// <summary>Amplitude of the phase-locked octave layer inside the
        /// hum voice. 0 = pure fundamental.</summary>
        public float HarmonicAmp { get; set; } = 0.22f;

        /// <summary>How much slow travel quiets the hum: while moving, the
        /// level target is (1 - SpeedVolume) + SpeedVolume * normalized
        /// rate. 0 = constant loudness; 1 = loudness fully tracks speed.
        /// A source with no rate signal (pre-0.2.14 mod) keeps full voice
        /// rather than dropping to the floor.</summary>
        public float SpeedVolume { get; set; } = 0.5f;

        // Fixed hum-dynamics timing (not settings: time constants, not feel).
        private const float BendAlpha        = 1f / 360f;  // ~90 ms glide at the 4 kHz effect rate
        private const float FastTravelPerSec = 0.8f;       // full-travel fraction/s that counts as fast
        private const float SpeedEmaAlpha    = 0.01f;      // ~200 ms smoothing at the 500 Hz telemetry tick

        // Manual-only settles (a stick-driven arm easing to a halt, mod
        // 0.2.19+) land at momentum weight: peak half a landing, scaled by
        // the speed at the stop, so feathering whispers and a snapped stick
        // gives a firm bump. Machine cycles keep the full working-flag
        // weight.
        private const float ManualSettlePeak = 0.5f;

        // Stage ends inside a running cycle (mod 0.2.22+): a wing latching
        // is a real mechanical event but not the tool reaching the ground,
        // so it sits between the raise and the full landing, scaled by the
        // stopping part's own speed.
        private const float PhaseSettlePeak = 0.55f;

        // Stage speeds are an order slower than a three-point lower: an
        // on-wheel trace of a folding implement (2026-08-10) put real stage
        // ends at 0.03-0.4 of full travel per second, so scaling them
        // against FastTravelPerSec pinned every one to the floor. Their own
        // reference, and a floor high enough that the quietest stage still
        // reads as an event.
        private const float PhaseFastPerSec = 0.10f;
        private const float PhaseFloor      = 0.45f;

        // OFF for release: stage thuds land ~65 ms after the part stops and
        // fire for internal cylinders that halt while the machine keeps
        // moving, which reads as mistimed on the wheel (owner, 2026-08-10).
        // The thud at the END of travel is unaffected. Detection, protocol
        // and plumbing stay live behind this flag; flipping it back on is
        // the whole cost of resuming, and the two known fixes are a shorter
        // stall dwell and a travelled-distance gate on the stalling part.
        private const bool PhaseThudsEnabled = false;

        // A stage lands while the hum is still playing, and the hum is the
        // louder voice (HumAmp 0.30 against a stage's ~0.12), so the clunk
        // was simply buried. Dipping the hum under each thud is what makes
        // it audible, and it is what the machine does anyway: the jolt
        // swamps the pump note for a moment.
        private const float ThudHumDuck     = 0.35f;      // dip to 35%
        private const float HumDuckRecover  = 1f / 600f;  // ~150 ms back up

        private bool?  _lastWorking;
        private bool?  _lastMoving;
        private int?   _lastEventCount;
        private bool   _testThudFired;
        private double _testSettlePhase;
        private float  _humLevel;            // 0..1 envelope, ramped in RenderAdd
        private bool   _humTarget;
        private double _humPhase;
        private float  _bendPos;             // 0..1 pitch glide, re-armed on motion edges
        private float  _speedEma;            // smoothed implement travel rate (fraction/s)
        private bool   _speedKnown;          // source reports a rate (mod 0.2.14+)
        private bool   _manualMotion;        // latched while moving: manual-only motion
        private int?   _lastPhaseCount;      // stage-end counter (mod 0.2.22+)
        private int    _envelopeRemaining;   // samples
        private int    _envelopeTotal = 80;
        private float  _envelopeAmpScale = 1.0f;
        private float  _humDuck = 1f;        // hum dip so a thud punches through
        private double _phase;
        private readonly Random _rng = new Random();

        public override bool IsActive
            => IsTesting || (Enabled && (_envelopeRemaining > 0 || _humLevel > 0.01f || _humTarget));

        public override double ActivityLevel
        {
            get
            {
                double thud = 0;
                int total = _envelopeTotal;
                int rem   = _envelopeRemaining;
                // Weighted by the thud's own amplitude: a quiet stage or
                // manual-settle bump must not duck the whole mix as hard as
                // a full landing does.
                if (total > 0 && rem > 0) thud = (double)rem / total * _envelopeAmpScale;
                // The hum ducks lower tiers only mildly: it is a texture
                // moment, not an alert peak.
                return Math.Max(thud, _humLevel * 0.4);
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;

            // Hydraulic hum: ramp the level toward its target (~60 ms in,
            // ~140 ms out at the 4 kHz effect rate) and synth a soft pump
            // whine. Runs independently of the thud envelope so the settle
            // clunk lands on top of the hum's tail.
            // Speed-shaped loudness: while moving, the target level sits on
            // a floor of (1 - SpeedVolume) and rises with the travel rate,
            // so a slow fold whispers and a fast lower speaks fully, and a
            // joint easing into its end stop fades under the settle thud.
            // No rate signal (older mod) keeps full voice, not the floor.
            float humTargetLevel = 0f;
            if (_humTarget)
            {
                humTargetLevel = !_speedKnown ? 1f
                    : (1f - SpeedVolume) + SpeedVolume
                        * Math.Min(_speedEma / FastTravelPerSec, 1f);
            }
            bool humAudible = _humLevel > 0.001f || _humTarget;
            if (humAudible && HumAmp > 0f)
            {
                float humScale = HumAmp * Gain * DuckMultiplier;
                float level = _humLevel;
                float bend  = _bendPos;
                float duck  = _humDuck;
                // Speed ride: the pump whines a touch higher the faster the
                // hydraulics travel. Constant across the buffer; the EMA in
                // OnTelemetry supplies the smoothness.
                float speedMult = 1f + SpeedPitch
                    * Math.Min(_speedEma / FastTravelPerSec, 1f);
                const float upAlpha   = 1f / 240f;   // ~60 ms to full
                const float downAlpha = 1f / 560f;   // ~140 ms to quiet
                for (int i = 0; i < count; i++)
                {
                    float alpha = humTargetLevel > level ? upAlpha : downAlpha;
                    level += (humTargetLevel - level) * alpha;
                    // Pitch glide: 1 - BendDepth at a fresh edge, easing up
                    // to the running pitch.
                    bend += (1f - bend) * BendAlpha;
                    double freq = HumFreq * (1f - BendDepth * (1f - bend)) * speedMult;
                    // Sine body, a phase-locked octave above it, and a
                    // whisper of noise so it reads as a pump, not a tone.
                    float v = (float)Math.Sin(2.0 * Math.PI * _humPhase) * 0.70f
                              + (float)Math.Sin(4.0 * Math.PI * _humPhase) * HarmonicAmp
                              + ((float)_rng.NextDouble() * 2f - 1f) * 0.13f;
                    buffer[i] += v * level * duck * humScale;
                    duck += (1f - duck) * HumDuckRecover;
                    _humPhase += freq / SampleRateHz;
                    if (_humPhase >= 1.0) _humPhase -= Math.Floor(_humPhase);
                }
                _humLevel = level;
                _bendPos  = bend;
                _humDuck  = duck > 1f ? 1f : duck;
            }
            else if (!_humTarget)
            {
                _humLevel = 0f;
            }

            int remaining = _envelopeRemaining;
            if (remaining <= 0) return;

            double phaseStep = Freq / SampleRateHz;
            float scale = PeakAmp * Gain * _envelopeAmpScale * DuckMultiplier;
            int total = _envelopeTotal;
            Waveform w = Waveform;

            for (int i = 0; i < count && remaining > 0; i++)
            {
                float env = (float)remaining / total;            // linear 1 → 0
                float v   = WaveformMath.SampleAt(w, _phase, _rng);
                buffer[i] += v * env * scale;
                _phase += phaseStep;
                if (_phase >= 1.0) _phase -= Math.Floor(_phase);
                remaining--;
            }
            _envelopeRemaining = remaining;
        }

        public override int TestPlay()
        {
            _phase    = 0;
            _humPhase = 0;
            _bendPos  = 0f;   // demo the spool-up bend
            _speedEma = 0f;   // no telemetry on the bench: neutral speed pitch
            _speedKnown = false;   // and full hum voice

            if (HumAmp <= 0f)
            {
                // Hum tuned out: the effect is just the settle clunk.
                _testThudFired = true;
                TriggerThud(landing: true);
                int thudOnly = EnvelopeMs + 100;
                StartTest(thudOnly);
                return thudOnly;
            }

            // Simulate a full lower: hydraulics hum while the linkage
            // travels, then the clunk lands as the motion settles, on top
            // of the hum's tail. TestUpdate fires the thud at the settle
            // point.
            const int humMs = 900;
            int duration = humMs + EnvelopeMs + 350;
            _testThudFired   = false;
            _humTarget       = true;
            _testSettlePhase = humMs / (double)duration;
            StartTest(duration);
            return duration;
        }

        public override void TestUpdate(double phase01)
        {
            if (_testThudFired || phase01 < _testSettlePhase) return;
            _humTarget     = false;
            _bendPos       = 0f;   // demo the stop-edge bend over the fade
            _testThudFired = true;
            TriggerThud(landing: true);
        }

        private void TriggerThud(bool landing) => TriggerThud(landing ? 1.0f : RaiseAmp);

        private void TriggerThud(float ampScale)
        {
            if (ampScale <= 0f) return;
            // Retriggering over a live envelope keeps the waveform running:
            // a stage thud landing a few milliseconds before the settle
            // would otherwise be cut mid-swing and restarted at phase 0,
            // which is a click at the head of the landing. Only a thud
            // starting from silence gets the clean attack.
            if (_envelopeRemaining <= 0) _phase = 0;
            _envelopeTotal = Math.Max(1, (int)(EnvelopeMs * SampleRateHz / 1000.0));
            _envelopeRemaining = _envelopeTotal;
            _envelopeAmpScale = ampScale;
            // Duck the hum under the clunk. Deeper for a louder thud, and
            // never lifted by a quieter one landing on top.
            float d = 1f - (1f - ThudHumDuck) * Math.Min(ampScale / 0.5f, 1f);
            if (d < _humDuck) _humDuck = d;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            bool? working = f.ImplementWorking;
            bool? moving  = f.ImplementMoving;

            if (!working.HasValue)
            {
                // Source doesn't report implements (or the mod is older):
                // stay seeded-empty so the first real value never fires.
                _lastWorking = null;
                _lastMoving  = null;
                _lastEventCount = null;
                _lastPhaseCount = null;
                _humTarget   = false;
                _speedEma    = 0f;
                _speedKnown  = false;
                _manualMotion = false;
                return;
            }

            // Discrete mechanism events (mod 0.2.16+): a counter bump is a
            // switch flip with no travel (straw-swath flap and kin), landed
            // as the light release-weight thud. Seeded on first sight so a
            // mid-session join never fires.
            int? evt = f.ImplementEvent;
            if (evt.HasValue)
            {
                // Strictly greater: the counter is session-monotonic, so a
                // DECREASE means the mod restarted (mission reload while
                // seated) and must reseed silently, not thud.
                if (_lastEventCount.HasValue && evt.Value > _lastEventCount.Value)
                    TriggerThud(landing: false);
                _lastEventCount = evt;
            }

            if (moving.HasValue)
            {
                // Motion-aware model (mod 0.2.8+): hydraulics hum while the
                // linkage or fold travels, and the thud lands when the
                // MOTION SETTLES, not when the target state flips (the game
                // flips the state at the keypress; the clunk belongs at the
                // end of travel). The working flag at settle time picks the
                // landing vs release weight.
                //
                // Both motion edges re-arm the pitch glide: the start bend
                // is the pump taking load, the stop bend replays the same
                // rise over the fade-out (the relief flick as the valve
                // closes), under the settle thud.
                if (_lastMoving.HasValue && _lastMoving.Value != moving.Value)
                    _bendPos = 0f;
                // Travel rate (mod 0.2.14+; null reads as neutral pitch
                // and full hum voice).
                _speedKnown = f.ImplementSpeed.HasValue;
                float spd = f.ImplementSpeed ?? 0f;
                if (spd < 0f) spd = 0f;
                _speedEma += (Math.Min(spd, 4f) - _speedEma) * SpeedEmaAlpha;
                // Latch the motion kind WHILE moving: at the settle frame
                // the flag already reads false again, so the latch is what
                // knows whether this motion was a stick-driven arm.
                if (moving.Value && f.ImplementManual.HasValue)
                    _manualMotion = f.ImplementManual.Value;
                _humTarget = moving.Value;
                bool settled = _lastMoving == true && moving.Value == false;
                if (settled)
                {
                    if (_manualMotion)
                    {
                        float speedNorm = Math.Min(_speedEma / FastTravelPerSec, 1f);
                        TriggerThud(ManualSettlePeak * (0.3f + 0.7f * speedNorm));
                        _manualMotion = false;
                    }
                    else
                    {
                        TriggerThud(landing: working.Value);
                    }
                }

                // Stage ends (mod 0.2.22+): one part of a multi-part cycle
                // finished while the cycle carries on, landed at momentum
                // weight from that part's own speed. A settle in this same
                // frame WINS: reseeding the counter without firing keeps a
                // stage bump from stepping on the full landing.
                int? phase = f.ImplementPhase;
                if (phase.HasValue)
                {
                    // Strictly greater, same reload-reseed rule as the
                    // event counter. RaiseAmp 0 is the user asking for no
                    // secondary clunks (it already silences raises and
                    // switch flips), so stages go with them.
                    if (PhaseThudsEnabled && !settled && RaiseAmp > 0f
                        && _lastPhaseCount.HasValue
                        && phase.Value > _lastPhaseCount.Value)
                    {
                        float phaseNorm = Math.Min(
                            (f.ImplementPhaseSpeed ?? 0f) / PhaseFastPerSec, 1f);
                        // A stage of a stick-driven arm is still hand work:
                        // it takes the quieter manual weight, not the
                        // machine-cycle one.
                        float peak = _manualMotion ? ManualSettlePeak : PhaseSettlePeak;
                        TriggerThud(peak * (PhaseFloor + (1f - PhaseFloor) * phaseNorm));
                    }
                    _lastPhaseCount = phase;
                }

                _lastMoving = moving;
                _lastWorking = working;   // tracked, but edges defer to settle
                return;
            }

            // Legacy model (mod 0.2.6/0.2.7): thud on the state edge.
            _humTarget = false;
            if (_lastWorking.HasValue && _lastWorking.Value != working.Value)
                TriggerThud(landing: working.Value);
            _lastWorking = working;
        }

        public override void Reset()
        {
            _lastWorking = null;
            _lastMoving  = null;
            _lastEventCount = null;
            _humTarget   = false;
            _humLevel    = 0f;
            _humPhase    = 0;
            _bendPos     = 0f;
            _speedEma    = 0f;
            _speedKnown  = false;
            _manualMotion = false;
            _lastPhaseCount = null;
            _humDuck     = 1f;
            _envelopeRemaining = 0;
            _phase = 0;
        }
    }
}
