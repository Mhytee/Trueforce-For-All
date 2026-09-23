// Motor-characterization sweep (SWEEP access code): a 15 s logarithmic sine
// sweep, 8 Hz → 300 Hz, at modest fixed amplitude. Not a driving effect —
// it never responds to telemetry and stays out of the settings UI; its only
// job is TestPlay so the user can map which frequencies their motor
// actually renders (strong / buzzy / dead) and we can place effect bands
// where the hardware delivers instead of where the math says.
//
// Log sweep = equal time per octave, which matches perception; the current
// frequency is announced by feel alone (seconds 0-15 ≈ 8→300 Hz, midpoint
// ~50 Hz). Phase is integrated per-sample so the sweep is click-free.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class MotorSweepEffect : TelemetryEffect
    {
        public override string Name => "Motor sweep";

        public float StartHz    { get; set; } = 8f;
        public float EndHz      { get; set; } = 300f;
        public int   DurationMs { get; set; } = 15000;
        // 0.6 pre-master (~0.3 at the wheel with the default 0.5 MasterGain).
        // The original 0.25 landed at ~0.125 effective — below feel threshold
        // on the G923, the whole sweep played and registered as "nothing".
        // A characterization sweep must sit at driving-effect loudness or the
        // strong/buzzy/dead judgment is really just "quiet/quiet/quiet".
        public float Amp        { get; set; } = 0.6f;

        private const double SampleRateHz = 4000.0;

        private long _samplesRemaining;
        private long _samplesTotal = 1;
        private double _phase;

        public override bool IsActive => IsTesting || _samplesRemaining > 0;

        // Never participates in ducking arbitration.
        public override double ActivityLevel => 0;

        public override void RenderAdd(float[] buffer, int count)
        {
            long remaining = _samplesRemaining;
            if (remaining <= 0) return;

            double lnRatio = Math.Log(Math.Max(EndHz, StartHz + 1f) / Math.Max(1f, StartHz));
            long total = _samplesTotal;

            for (int i = 0; i < count && remaining > 0; i++)
            {
                double t01 = 1.0 - (double)remaining / total;
                double freq = StartHz * Math.Exp(lnRatio * t01);
                // 200 ms fade at both ends so start/stop never thump.
                double edge = Math.Min(1.0, Math.Min(t01, 1.0 - t01) * (DurationMs / 200.0));
                buffer[i] += (float)(Math.Sin(2.0 * Math.PI * _phase) * Amp * Gain * edge);
                _phase += freq / SampleRateHz;
                if (_phase >= 1.0) _phase -= 1.0;
                remaining--;
            }
            _samplesRemaining = remaining;
        }

        public override int TestPlay()
        {
            _samplesTotal     = Math.Max(1, (long)(DurationMs * SampleRateHz / 1000.0));
            _samplesRemaining = _samplesTotal;
            _phase            = 0;
            StartTest(DurationMs + 200);
            return DurationMs + 200;
        }

        public override void Reset()
        {
            _samplesRemaining = 0;
            _phase = 0;
        }
    }
}
