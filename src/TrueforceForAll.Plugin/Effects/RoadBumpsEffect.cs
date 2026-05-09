// Road bumps + surface texture. Two synthesis paths sharing one settings
// section but with their own oscillators so each can be tuned independently:
//
//  A. HEAVE channel (universal). Vertical-acceleration transients —
//     curbs, jump landings, washboard bumps. Driven by
//     GameData.NewData.AccelerationHeave or the AC physics page's accGY.
//     One noise oscillator per RoadBumps instance, freq/waveform/LP user-
//     tunable. Threshold + FullScale gate the envelope so smooth driving
//     stays silent.
//
//  B. SURFACE channel (Forza-only today). Continuous tactile road feel.
//     Driven by Forza's SurfaceRumble[4] (max-abs across all four tires) —
//     the same channel Turn 10's own Trueforce path consumes inside Forza
//     Motorsport. Has its own oscillator so the user can pick a higher
//     frequency / brighter waveform / different LP cutoff for surface
//     texture without affecting the heave path's low-freq thump tuning.
//     Folds in a leading-edge pulse on OnRumbleStrip rising edges so kerb
//     hits feel percussive on top of the sustained texture.
//
// Both paths render into the SAME mixed output buffer (additively) so the
// total output stays in scale with Gain. SurfaceRumble channel is silent
// when the source doesn't supply it (AC, SimHub fallback) — the heave
// channel is what every game gets out of the box.

using System;
using System.Diagnostics;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class RoadBumpsEffect : TelemetryEffect
    {
        public override string Name => "Road bumps";

        // ---- Heave channel (universal) ----

        /// <summary>Heave magnitude (m/s² ish, game-dependent) at which the buzz begins.</summary>
        public float Threshold { get; set; } = 0.5f;

        /// <summary>Heave magnitude that produces full-output buzz.</summary>
        public float FullScale { get; set; } = 5.0f;

        /// <summary>Minimum speed (km/h) required for the effect to fire.</summary>
        public float MinSpeedKmh { get; set; } = 5.0f;

        public Waveform Waveform
        {
            get => _heave.Waveform;
            set => _heave.Waveform = value;
        }

        public double Freq
        {
            get => _heave.Freq;
            set => _heave.Freq = value;
        }

        // ---- Surface channel (Forza) ----

        /// <summary>Master toggle for the Forza-only surface-texture channel.
        /// When false the heave channel still runs as before; flipping this
        /// off is how non-Forza users keep RoadBumps unchanged.</summary>
        public bool SurfaceEnabled { get; set; } = true;

        /// <summary>Per-channel gain on the surface oscillator before the
        /// effect's overall Gain kicks in. 1.0 = baseline.</summary>
        public float SurfaceGain { get; set; } = 1.0f;

        /// <summary>Multiplier applied to Forza's SurfaceRumble channel value
        /// before driving the oscillator. Forza's SurfaceRumble lands ~0.05 on
        /// asphalt and spikes 0.5-0.8 on dirt / grass; 1.0 = use as-is.
        /// Tune up for a more aggressive road feel.</summary>
        public float SurfaceRumbleScale { get; set; } = 1.0f;

        public Waveform SurfaceWaveform
        {
            get => _surface.Waveform;
            set => _surface.Waveform = value;
        }

        public double SurfaceFreq
        {
            get => _surface.Freq;
            set => _surface.Freq = value;
        }

        public double SurfaceLowpassHz
        {
            get => _surface.NoiseLowpassHz;
            set => _surface.NoiseLowpassHz = value;
        }

        public double SurfaceHighpassHz
        {
            get => _surface.NoiseHighpassHz;
            set => _surface.NoiseHighpassHz = value;
        }

        /// <summary>Amplitude added on the rising edge of OnRumbleStrip
        /// (any-wheel-on-kerb). 0 = disabled (default). Largely redundant
        /// with the SurfaceRumble channel — kerbs spike SurfaceRumble on
        /// their own — so this is opt-in for users who want extra leading-
        /// edge "snap" if Forza's SurfaceRumble ramps too softly on first
        /// contact for their taste. Decays linearly over RumbleStripPulseMs.</summary>
        public float RumbleStripPulseAmp { get; set; } = 0f;

        /// <summary>Decay time of the rumble-strip leading-edge pulse, ms.</summary>
        public int RumbleStripPulseMs { get; set; } = 120;

        // ---- Oscillators ----

        private readonly OscillatorSource _heave = new OscillatorSource
        {
            Waveform   = Waveform.Noise,
            Freq       = 60,
            Amp        = 0,
            Enabled    = true,
            SampleRate = 4000.0,
        };

        private readonly OscillatorSource _surface = new OscillatorSource
        {
            Waveform        = Waveform.Noise,
            Freq            = 120,
            Amp             = 0,
            Enabled         = true,
            SampleRate      = 4000.0,
            NoiseLowpassHz  = 800.0,
            NoiseHighpassHz = 60.0,
        };

        public override bool IsActive =>
            IsTesting || (Enabled && (_heave.IsActive || (SurfaceEnabled && _surface.IsActive)));

        public override double ActivityLevel
        {
            // Both oscillators peak at ~0.30 × Gain at full intensity each;
            // total max is the sum. Report whichever channel is louder
            // relative to its own ceiling.
            get
            {
                double maxAmp = 0.30 * Math.Max(0.01, Gain);
                double a = _heave.Amp / maxAmp;
                double b = _surface.Amp / (maxAmp * Math.Max(0.01, SurfaceGain));
                return Math.Min(1.0, Math.Max(0.0, Math.Max(a, b)));
            }
        }

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            _heave.RenderAdd(buffer, count);
            if (SurfaceEnabled || IsTesting) _surface.RenderAdd(buffer, count);
        }

        public override int TestPlay()
        {
            _heave.Amp   = 0;
            _surface.Amp = 0;
            StartTest(2000);
            return 2000;
        }

        /// <summary>Test simulation: 4 quick heave-channel pulses simulating
        /// curb hits, with a surface-channel ramp-up underneath simulating
        /// driving onto rougher pavement (so users can hear/feel both
        /// channels behave during the test).</summary>
        public override void TestUpdate(double phase01)
        {
            // Heave: 4 sharp pulses.
            const int pulses = 4;
            double heaveEnv = 0;
            for (int i = 0; i < pulses; i++)
            {
                double pulseCenter = (i + 0.5) / pulses;
                double dist = phase01 - pulseCenter;
                if (dist >= 0 && dist < 0.08)
                {
                    double e = Math.Exp(-dist / 0.025);
                    if (e > heaveEnv) heaveEnv = e;
                }
            }
            _heave.Amp = heaveEnv * 0.30 * Gain;

            // Surface: linear ramp 0 → 0.4 across the test, then back down.
            double surfaceEnv = phase01 < 0.5 ? phase01 * 2.0 * 0.4 : (1.0 - phase01) * 2.0 * 0.4;
            _surface.Amp = surfaceEnv * 0.30 * Gain * SurfaceGain;
        }

        // Rumble-strip leading-edge pulse state. Edge resets the start time;
        // OnTelemetry derives the current envelope from age.
        private bool _prevOnRumbleStrip;
        private long _rsPulseStartTicks;     // Stopwatch.GetTimestamp() units
        private static readonly long StopwatchTicksPerMs = Stopwatch.Frequency / 1000;

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;
            if (f.SpeedKmh < MinSpeedKmh)
            {
                _heave.Amp = 0;
                _surface.Amp = 0;
                return;
            }

            // ---- Heave channel ----
            double heave = f.AccelerationHeave.GetValueOrDefault();
            double mag   = Math.Abs(heave);
            double heaveRange = Math.Max(0.01, FullScale - Threshold);
            double heaveNorm = (mag > Threshold) ? Math.Min(1.0, (mag - Threshold) / heaveRange) : 0;
            _heave.Amp = heaveNorm * 0.30 * Gain;

            // ---- Surface channel ----
            if (!SurfaceEnabled)
            {
                _surface.Amp = 0;
            }
            else
            {
                double surfaceNorm = 0;
                if (f.SurfaceRumble is double sr)
                    surfaceNorm = Math.Min(1.0, Math.Abs(sr) * SurfaceRumbleScale);

                bool onStrip = f.OnRumbleStrip ?? false;
                long nowSw = Stopwatch.GetTimestamp();
                if (onStrip && !_prevOnRumbleStrip) _rsPulseStartTicks = nowSw;
                _prevOnRumbleStrip = onStrip;

                double pulseNorm = 0;
                if (RumbleStripPulseAmp > 0 && _rsPulseStartTicks != 0 && RumbleStripPulseMs > 0)
                {
                    long ageMs = (nowSw - _rsPulseStartTicks) / StopwatchTicksPerMs;
                    if (ageMs >= 0 && ageMs < RumbleStripPulseMs)
                    {
                        double envelope = 1.0 - (double)ageMs / RumbleStripPulseMs;
                        pulseNorm = envelope * RumbleStripPulseAmp;
                    }
                    else
                    {
                        _rsPulseStartTicks = 0;
                    }
                }

                double surfaceTotal = Math.Min(1.0, Math.Max(surfaceNorm, pulseNorm));
                _surface.Amp = surfaceTotal * 0.30 * Gain * SurfaceGain;
            }
        }

        public override void Reset()
        {
            _prevOnRumbleStrip = false;
            _rsPulseStartTicks = 0;
            _heave.Amp = 0;
            _surface.Amp = 0;
        }
    }
}
