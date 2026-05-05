// ABS engagement haptic. Two modes:
//   Pulse  — continuous carrier modulated by an internal pulse rate (12 Hz
//            default). The "rrr-rrr-rrr" feel that's stable regardless of
//            how the game reports ABSActive.
//   PerTick — fires a single short click envelope on each rising edge of
//            ABSActive. If the game's ABSActive flag tracks the actual ABS
//            valve cycle, this gives the most authentic feel — what you
//            feel matches what the simulated pump is doing.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public enum AbsMode
    {
        Pulse,    // continuous carrier × internal pulse modulator
        PerTick,  // one click envelope per ABSActive rising edge
    }

    public sealed class AbsClickEffect : TelemetryEffect
    {
        public override string Name => "ABS";

        [JsonConverter(typeof(StringEnumConverter))]
        public AbsMode Mode { get; set; } = AbsMode.Pulse;

        /// <summary>Carrier tone freq within each pulse / tick (Hz).</summary>
        public float Freq { get; set; } = 80.0f;

        /// <summary>Pulse rate (Hz) — Pulse mode only. Real ABS valves cycle
        /// at 10-15 Hz; default 12.</summary>
        public float PulseFreq { get; set; } = 12.0f;

        /// <summary>Fraction of each pulse period that's audible. Pulse mode only.</summary>
        public float DutyCycle { get; set; } = 0.4f;

        /// <summary>Length of each tick's envelope in ms. PerTick mode only.</summary>
        public float TickDurationMs { get; set; } = 35.0f;

        public Waveform Waveform { get; set; } = Waveform.Square;

        /// <summary>Amplitude when engaged.</summary>
        public float ActiveAmp { get; set; } = 0.25f;

        private const double SampleRate = 4000.0;
        private const int HoldMs = 120;   // Pulse-mode hold

        // Pulse-mode state
        private float  _amp;
        private long   _lastActiveTicks;

        // PerTick-mode state
        private int    _tickEnvelopeRemaining;
        private int    _tickEnvelopeTotal;
        private int    _lastAbsValue;

        // Shared
        private double _carrierPhase;
        private double _pulsePhase;
        private long   _lastAbsLogTicks;
        private int    _lastLoggedAbsValue = -1;

        public override bool IsActive
            => IsTesting
               || (Enabled && (_amp > 0 || _tickEnvelopeRemaining > 0));

        public override double ActivityLevel
        {
            get
            {
                if (Mode == AbsMode.PerTick)
                {
                    int total = _tickEnvelopeTotal;
                    int rem = _tickEnvelopeRemaining;
                    if (total <= 0 || rem <= 0) return 0;
                    return (double)rem / total;
                }
                return _amp > 0 ? 1.0 : 0;
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;

            double cStep = Math.Max(0.0, Freq) / SampleRate;
            Waveform w = Waveform;

            if (Mode == AbsMode.PerTick)
            {
                if (_tickEnvelopeRemaining <= 0) return;
                int total = _tickEnvelopeTotal;
                float baseAmp = ActiveAmp * Gain;
                for (int i = 0; i < count && _tickEnvelopeRemaining > 0; i++)
                {
                    float env = (float)_tickEnvelopeRemaining / total;
                    buffer[i] += SampleAt(w, _carrierPhase) * env * baseAmp;
                    _carrierPhase += cStep;
                    if (_carrierPhase >= 1.0) _carrierPhase -= Math.Floor(_carrierPhase);
                    _tickEnvelopeRemaining--;
                }
                return;
            }

            // Pulse mode
            if (_amp <= 0) return;
            double pStep = Math.Max(0.0, PulseFreq) / SampleRate;
            float amp = _amp;
            double duty = Math.Min(1.0, Math.Max(0.0, (double)DutyCycle));

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
            if (Mode == AbsMode.PerTick)
            {
                // Fire a few ticks across the test duration so the user feels the
                // shape of the click envelope. Trigger now; the test loop won't
                // re-fire — that's fine, single tick demos the timbre.
                TriggerTick();
                StartTest(500);
                return 500;
            }
            _amp = ActiveAmp * Gain;
            StartTest(2000);
            return 2000;
        }

        public override void TestUpdate(double phase01)
        {
            // PerTick mode test: fire ticks at PulseFreq across the test duration.
            // Pulse mode test: nothing dynamic needed (carrier+modulator runs).
            if (Mode == AbsMode.PerTick)
            {
                // Roughly fire one tick every (1/PulseFreq) seconds during test.
                // For 500 ms duration at 12 Hz, that's 6 ticks.
                double tickInterval = 1.0 / Math.Max(1.0, PulseFreq);
                double testDurationSec = 0.5;
                double tickPhaseProgress = phase01 / (tickInterval / testDurationSec);
                int ticksDone = (int)Math.Floor(tickPhaseProgress);
                if (ticksDone > _testTicksFiredCount)
                {
                    TriggerTick();
                    _testTicksFiredCount = ticksDone;
                }
                if (phase01 < 0.05) _testTicksFiredCount = 0;  // reset at test start
            }
        }
        private int _testTicksFiredCount;

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            int absValue = f.AbsActive;
            long now = DateTime.UtcNow.Ticks;

            if (Mode == AbsMode.PerTick)
            {
                // Rising edge → fire one tick. Ignore the level itself; AC reports
                // the ACTUAL pump cycle, so each rising edge is one valve-close.
                if (absValue > 0 && _lastAbsValue == 0)
                    TriggerTick();
                _lastAbsValue = absValue;
                _amp = 0;   // pulse-mode amp unused
            }
            else
            {
                // Pulse mode with hold (decouples our pulse rate from game flicker).
                if (absValue > 0) _lastActiveTicks = now;
                bool stillEngaged = _lastActiveTicks != 0
                    && (now - _lastActiveTicks) < HoldMs * TimeSpan.TicksPerMillisecond;
                _amp = stillEngaged ? ActiveAmp * Gain : 0;
                _lastAbsValue = absValue;
            }

            // Diagnostic — log when ABSActive changes value or every 2s while active.
            if (absValue != _lastLoggedAbsValue
                || (absValue > 0 && now - _lastAbsLogTicks > 2 * TimeSpan.TicksPerSecond))
            {
                SimHub.Logging.Current.Info($"[Trueforce] ABS diag: ABSActive={absValue} mode={Mode} amp={_amp:F3} tickRem={_tickEnvelopeRemaining}");
                _lastLoggedAbsValue = absValue;
                _lastAbsLogTicks = now;
            }
        }

        private void TriggerTick()
        {
            int samples = (int)(TickDurationMs * SampleRate / 1000.0);
            if (samples < 1) samples = 1;
            _tickEnvelopeTotal = samples;
            _tickEnvelopeRemaining = samples;
            _carrierPhase = 0;
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
