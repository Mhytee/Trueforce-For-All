// Road bumps / curb-rumble buzz: noise gated by the magnitude of vertical
// acceleration (GameData.NewData.AccelerationHeave) so that hitting a kerb
// or driving over rough terrain gives a tactile rumble through the wheel.
//
// Why this instead of "wheel slip": SimHub's universal StatusDataBase
// doesn't expose per-tyre slip ratio (it's a game-specific value). Vertical
// acceleration is always available and maps cleanly to a feel-it-through-
// the-wheel sensation.

using System;
using GameReaderCommon;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class RoadBumpsEffect : TelemetryEffect
    {
        public override string Name => "Road bumps";

        /// <summary>Heave magnitude (m/s² ish, game-dependent) at which the buzz begins.</summary>
        public float Threshold { get; set; } = 0.5f;

        /// <summary>Heave magnitude that produces full-output buzz.</summary>
        public float FullScale { get; set; } = 5.0f;

        /// <summary>Minimum speed (km/h) required for the effect to fire.</summary>
        public float MinSpeedKmh { get; set; } = 5.0f;

        public Waveform Waveform
        {
            get => _noise.Waveform;
            set => _noise.Waveform = value;
        }

        public double Freq
        {
            get => _noise.Freq;
            set => _noise.Freq = value;
        }

        private readonly OscillatorSource _noise = new OscillatorSource
        {
            Waveform   = Waveform.Noise,
            Freq       = 60,
            Amp        = 0,
            Enabled    = true,
            SampleRate = 4000.0,
        };

        public override bool IsActive => IsTesting || (Enabled && _noise.IsActive);

        public override double ActivityLevel
        {
            // _noise.Amp peaks at ~0.30 × Gain at full intensity. Normalize.
            get
            {
                double maxAmp = 0.30 * Math.Max(0.01, Gain);
                return Math.Min(1.0, Math.Max(0.0, _noise.Amp / maxAmp));
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            _noise.RenderAdd(buffer, count);
        }

        public override int TestPlay()
        {
            _noise.Amp = 0;
            StartTest(2000);
            return 2000;
        }

        /// <summary>Test simulation: 4 quick pulses simulating curb hits at
        /// regular intervals. Each pulse rises sharply and decays over ~150 ms.</summary>
        public override void TestUpdate(double phase01)
        {
            const int pulses = 4;
            double envelope = 0;
            for (int i = 0; i < pulses; i++)
            {
                double pulseCenter = (i + 0.5) / pulses;
                double dist = phase01 - pulseCenter;
                if (dist >= 0 && dist < 0.08)
                {
                    // exponential decay 1 → 0 across 80 ms slice
                    double e = Math.Exp(-dist / 0.025);
                    if (e > envelope) envelope = e;
                }
            }
            _noise.Amp = envelope * 0.30 * Gain;
        }

        public override void OnTelemetry(GameData data)
        {
            if (IsTesting) return;
            var d = data?.NewData;
            if (d == null) { _noise.Amp = 0; return; }
            if (d.SpeedKmh < MinSpeedKmh) { _noise.Amp = 0; return; }

            // AccelerationHeave is double? — null when the source game doesn't
            // surface it. Treat null as silent.
            double heave = d.AccelerationHeave.GetValueOrDefault();
            double mag = Math.Abs(heave);
            if (mag < Threshold) { _noise.Amp = 0; return; }

            double range = Math.Max(0.01, FullScale - Threshold);
            double norm  = Math.Min(1.0, (mag - Threshold) / range);
            _noise.Amp   = norm * 0.30 * Gain;
        }
    }
}
