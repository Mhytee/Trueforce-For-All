// Traction-loss buzz: vibrates when the car loses grip — wheelspin under
// throttle, lockup under braking, oversteer/drift, etc.
//
// Detection approach (works for AC, ACC, AC EVO and any game whose SimHub
// reader populates the standard StatusDataBase fields):
//
//   1. Wheelspin / lockup — abs(SpeedKmh - GroundSpeedKmH).
//      SpeedKmh comes from the wheel speedometer; GroundSpeedKmH is the car's
//      true ground speed from physics. When wheels spin faster than ground
//      (drag-strip launch, throttle-on oversteer) or slower (lockup under
//      braking), the difference IS the slip. Clean, unambiguous.
//
//   2. Drift / oversteer — yaw rate exceeding what the lateral G implies.
//      In steady-state cornering the centripetal balance gives
//      lateral_g ≈ speed × yaw_rate. If actual yaw rate >> lateral_g / speed,
//      the car is rotating faster than its grip can sustain — that's drift.
//
// Both signals are normalized to [0, 1] and combined with max() so any one
// path triggers the effect. EMA smoothing prevents single-frame jitter.
//
// Frequency scales with vehicle speed (real tire-screech pitch tracks tread
// strike rate). Only matters for tonal waveforms (Saw/Square/Sine/Triangle);
// Noise has no fundamental.

using System;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class TractionLossEffect : TelemetryEffect
    {
        public override string Name => "Traction loss";

        /// <summary>1.0 = baseline, &lt;1 stricter, &gt;1 more sensitive.</summary>
        public float Sensitivity { get; set; } = 1.0f;

        /// <summary>Below this speed the effect is suppressed (math is unstable
        /// at very low speeds and slow standing wheelspin doesn't need haptic
        /// feedback). Default 5 km/h is generous.</summary>
        public float MinSpeedKmh { get; set; } = 5.0f;

        /// <summary>Pitch scaling for tonal waveforms. Real tire squeal pitch
        /// tracks wheel rotational speed (tread strikes per second). At 0 km/h
        /// the screech is at PitchBaseHz; at PitchMaxKmh it reaches PitchMaxHz.
        /// Ignored when Waveform == Noise.</summary>
        public float PitchBaseHz  { get; set; } = 80.0f;
        public float PitchMaxHz   { get; set; } = 600.0f;
        public float PitchMaxKmh  { get; set; } = 200.0f;

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
            Freq       = 100,
            Amp        = 0,
            Enabled    = true,
            SampleRate = 4000.0,
        };

        private double _slipEma;
        private long   _lastDiagLogTicks;
        private double _peakSlipSinceLastLog;

        // RPM/speed heuristic state for wheelspin detection (AC's SpeedKmh and
        // GroundSpeedKmH are always identical, so we can't use that diff —
        // need to fall back to "RPM rising faster than speed under throttle").
        private double _prevRpm;
        private double _prevSpeed;
        private long   _prevTicks;

        public override bool IsActive => IsTesting || (Enabled && _noise.IsActive);

        public override double ActivityLevel => Math.Min(1.0, Math.Max(0.0, _slipEma));

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            _noise.RenderAdd(buffer, count);
        }

        public override int TestPlay()
        {
            _noise.Amp = 0;
            StartTest(2500);
            return 2500;
        }

        /// <summary>Test simulation: slip builds up, holds at peak (drift in
        /// progress), then decays. Speed sweeps 50→150 km/h so the pitch
        /// scaling is audible if a tonal waveform is selected.</summary>
        public override void TestUpdate(double phase01)
        {
            double slipNorm;
            if (phase01 < 0.3) slipNorm = phase01 / 0.3;
            else if (phase01 < 0.7) slipNorm = 1.0;
            else slipNorm = Math.Max(0, (1.0 - phase01) / 0.3);

            double speedKmh = 50 + 100 * phase01;
            double speedNorm = Math.Min(1.0, Math.Max(0, speedKmh / Math.Max(1.0, PitchMaxKmh)));
            _noise.Freq = PitchBaseHz + speedNorm * (PitchMaxHz - PitchBaseHz);
            _noise.Amp  = slipNorm * 0.40 * Gain;
        }

        public override void OnTelemetry(TelemetryFrame f)
        {
            if (IsTesting) return;

            // Pitch scales with vehicle speed (tonal waveforms only).
            double speedKmh = f.SpeedKmh;
            double speedNormForPitch = Math.Min(1.0, Math.Max(0.0, speedKmh / Math.Max(1.0, PitchMaxKmh)));
            _noise.Freq = PitchBaseHz + speedNormForPitch * (PitchMaxHz - PitchBaseHz);

            // Engine free-revs in neutral; speed-based signals are still valid
            // but RPM is not. Skip during shifts to avoid false-fires.
            string gear = f.Gear;
            if (string.Equals(gear, "N", StringComparison.OrdinalIgnoreCase))
            {
                _slipEma *= 0.4;
                _noise.Amp = (float)(_slipEma * 0.40 * Gain);
                return;
            }

            // Suppress at very low speed (math unstable, drift doesn't matter).
            if (speedKmh < MinSpeedKmh)
            {
                _slipEma *= 0.4;
                _noise.Amp = (float)(_slipEma * 0.40 * Gain);
                return;
            }

            // ---------- Wheelspin (RPM rising faster than speed) ----------
            // AC's SpeedKmh and GroundSpeedKmH are always equal (verified from
            // diag log), so we can't use their diff. Fall back to the classic
            // heuristic: RPM rising sharply while speed isn't. Gated on
            // throttle and on RPM being well below redline (rules out limiter).
            long now = DateTime.UtcNow.Ticks;
            double throttlePct = f.Throttle01 * 100.0;
            double wheelspinNorm = 0;
            if (_prevTicks != 0)
            {
                double dtSec = (now - _prevTicks) / 10_000_000.0;
                if (dtSec >= 0.005 && dtSec <= 0.5
                    && throttlePct >= 25.0
                    && f.MaxRpm > 0 && f.Rpms < f.MaxRpm * 0.95)   // not at limiter
                {
                    double dRpm   = (f.Rpms     - _prevRpm)   / dtSec;   // RPM/s
                    double dSpeed = (f.SpeedKmh - _prevSpeed) / dtSec;   // (km/h)/s
                    double rpmRise   = Math.Max(0.0, dRpm);
                    double speedRise = Math.Max(0.0, dSpeed);
                    // Threshold rebased to 500 RPM/s (was 1500); empirically
                    // Sens=1 didn't trigger reliably with 1500, user had to
                    // crank to Sens=3 (which gave 500). 500 = sane default.
                    double rpmThreshold = 500.0 / Math.Max(0.1, Sensitivity);
                    if (rpmRise > rpmThreshold)
                    {
                        double rpmExcess   = (rpmRise - rpmThreshold) / 2000.0;
                        double speedFactor = Math.Max(0.0, 1.0 - speedRise / 12.0);
                        wheelspinNorm = Math.Min(1.0, rpmExcess * speedFactor);
                    }
                }
            }
            _prevTicks = now;
            _prevRpm   = f.Rpms;
            _prevSpeed = f.SpeedKmh;

            // ---------- Drift / oversteer (slip angle + transient detectors) ----------
            // Source delivers yaw rate in deg/s; convert to rad/s here.
            double speedMs    = speedKmh / 3.6;
            double yawRateDeg = Math.Abs(f.YawRateDegPerSec ?? 0);
            double yawRate    = yawRateDeg * Math.PI / 180.0;          // rad/s
            double swayRaw    = f.AccelerationSway ?? 0;
            double lateralG   = Math.Abs(swayRaw);                    // m/s²

            // Detector A — SLIP ANGLE (the physical signal we actually want).
            // For any car in circular motion: AccelerationSway = v × yaw_rate × cos(β),
            // where β is the slip angle (heading vs velocity-vector angle). Solving:
            //   β = acos( lateral_g / (speed × yaw_rate) )
            // β=0 means tires grip perfectly; β>5° means tires are sliding; β>30°
            // is a hard drift. Units are degrees → the THRESHOLD is car-independent.
            // Math is unstable at low yaw_rate × speed (denominator small), so we
            // gate the slip-angle detector behind a small centripetal-magnitude
            // floor and let detector B handle low-speed transients.
            double slipAngleDeg = 0;
            double centripetalRequired = speedMs * yawRate;            // m/s²
            if (centripetalRequired > 1.0)
            {
                double cosBeta = lateralG / centripetalRequired;
                if (cosBeta > 1.0) cosBeta = 1.0;
                if (cosBeta < -1.0) cosBeta = -1.0;
                slipAngleDeg = Math.Acos(cosBeta) * 180.0 / Math.PI;
            }
            // 5° deadband (allow natural slip), 50° = full effect. Wider range
            // than the previous 25° so heavy drifts have headroom to feel
            // "louder than" moderate ones — gives ~5× dynamic range from
            // light slip to heavy across β=10°→50°, addressing "feels static."
            double slipDeadband = 5.0  / Math.Max(0.3, Sensitivity);
            double slipFullDeg  = 50.0 / Math.Max(0.3, Sensitivity);
            double slipExcess   = Math.Max(0, slipAngleDeg - slipDeadband);
            double driftFromSlipAngle = Math.Min(1.0, slipExcess / Math.Max(5.0, slipFullDeg));

            // Detector B — centripetal imbalance (transient breakaway).
            // Catches the moment of rear breakout at speeds too low for the
            // slip-angle formula to be reliable, and during rapid yaw acceleration.
            double expectedYaw = (speedMs > 0.1) ? lateralG / speedMs : 0;
            double yawExcess   = Math.Max(0, yawRate - expectedYaw - 0.08);
            double driftScale  = 0.33 / Math.Max(0.1, Sensitivity);
            double driftFromExcess = Math.Min(1.0, yawExcess / Math.Max(0.05, driftScale));

            double driftNorm = Math.Max(driftFromSlipAngle, driftFromExcess);
            double rawTraction = Math.Max(wheelspinNorm, driftNorm);

            // Tighter decay: when rawTraction is near zero, snap _slipEma down
            // quickly so the buzz ends within ~100 ms of grip recovery instead
            // of ringing on for half a second.
            if (rawTraction < 0.05)
            {
                _slipEma *= 0.5;       // ~50% per tick → near-zero in 4 ticks
                if (_slipEma < 0.01) _slipEma = 0;
            }
            else
            {
                double alpha = (rawTraction > _slipEma) ? 0.5 : 0.3;
                _slipEma = _slipEma * (1 - alpha) + rawTraction * alpha;
            }
            _noise.Amp = (float)(_slipEma * 0.40 * Gain);

            // Diagnostic — once per second, only when something interesting.
            if (rawTraction > _peakSlipSinceLastLog) _peakSlipSinceLastLog = rawTraction;
            if (now - _lastDiagLogTicks > TimeSpan.TicksPerSecond)
            {
                if (_peakSlipSinceLastLog > 0.05)
                {
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] traction diag | spd={speedKmh:F1} thr={throttlePct:F0} | yawDeg={yawRateDeg:F1} sway={swayRaw:F2} cent={centripetalRequired:F2} β={slipAngleDeg:F1}° | dSlip={driftFromSlipAngle:F2} dExc={driftFromExcess:F2} ws={wheelspinNorm:F2} | peak={_peakSlipSinceLastLog:F2} ema={_slipEma:F2}");
                }
                _lastDiagLogTicks = now;
                _peakSlipSinceLastLog = 0;
            }
        }
    }
}
