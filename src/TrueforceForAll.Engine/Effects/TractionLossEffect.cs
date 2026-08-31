// Traction-loss buzz: vibrates when the car loses grip, wheelspin under
// throttle, lockup under braking, oversteer/drift, etc.
//
// Two detection paths:
//
//   A. Direct (preferred). When TelemetryFrame.WheelSlip is supplied, i.e.
//      the source reads per-wheel slip ratio from the sim's shared memory
//      directly (AC's wheelSlip[]), we just normalize it. The source has
//      already load-weighted the four tyres into that scalar (LoadWeighting,
//      issue #30) so an airborne / lifted wheel doesn't buzz. Cleaner, no
//      cross-coupling between detectors, and matches what the sim itself
//      considers a slipping tire.
//
//   B. Heuristic (fallback). When the source can't measure slip (the SimHub
//      universal path, works for every SimHub-supported game), we infer it
//      from two signals:
//        1. Wheelspin: RPM rising sharply while speed isn't, gated on
//           throttle and below-redline. Lockup under braking is the same
//           shape with the inputs reversed.
//        2. Drift: slip angle β = acos(lateral_g / (speed × yaw_rate)). For
//           steady-state cornering, β=0 means tires grip; β>5° is sliding.
//      Combined with max(); both gated on speed and gear-out-of-neutral.
//
// Either path produces rawTraction in [0, 1]; EMA smoothing in OnTelemetry
// prevents single-frame jitter. Frequency scales with vehicle speed (real
// tire-screech pitch tracks tread strike rate) for tonal waveforms only.

using System;
using System.Diagnostics;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin.Effects
{
    public sealed class TractionLossEffect : TelemetryEffect
    {
        public override string Name => "Traction loss";

        /// <summary>Sink for the once-per-second traction diagnostic line.
        /// Injected by the host (the SimHub logger in the plugin) so the
        /// engine assembly carries no SimHub reference. Null = silent.</summary>
        public Action<string> DiagLog { get; set; }

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

        // Phase 6 contract: slip texture is grip information. Band = the
        // noise window for the default Noise waveform, else the tonal pitch
        // range; either way capped at the wheel-relevant 400 Hz.
        public override EffectClass PriorityClass => EffectClass.GripState;
        public override void GetCurrentBand(out double loHz, out double hiHz)
        {
            if (Waveform == Waveform.Noise)
            {
                loHz = Math.Max(10.0, NoiseHighpassHz);
                hiHz = Math.Min(400.0, Math.Max(loHz + 10.0, NoiseLowpassHz));
            }
            else
            {
                loHz = PitchBaseHz * 0.7;
                hiHz = Math.Min(400.0, PitchMaxHz);
            }
        }

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

        /// <summary>Lowpass cutoff (Hz) applied to the noise waveform. Lower
        /// = smoother rumble, higher = grittier. Ignored for tonal waveforms.</summary>
        public double NoiseLowpassHz
        {
            get => _noise.NoiseLowpassHz;
            set => _noise.NoiseLowpassHz = value;
        }

        /// <summary>Highpass cutoff (Hz) applied to the noise waveform after
        /// the lowpass. Removes sub-audible drift / thumping. Set 0 to
        /// disable. Ignored for tonal waveforms.</summary>
        public double NoiseHighpassHz
        {
            get => _noise.NoiseHighpassHz;
            set => _noise.NoiseHighpassHz = value;
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
        private float[] _scratch;

        // EMA alphas are per-TICK. Historically OnTelemetry ran at the source
        // rate and per-sample alphas deliberately made AC (333 Hz) respond ~5x
        // faster than SimHub (60 Hz) — that gain was the point of the enhanced
        // source. Since phase 1 the engine ticks effects at a FIXED 500 Hz on
        // resampled frames, so every game gets the fast response and the rate
        // dependence is gone. The alphas below are the old AC-rate values
        // rescaled to the 500 Hz tick (alpha' = 1-(1-alpha)^(333/500)) so the
        // reference feel — the one the whole effect was tuned against — is
        // preserved: attack tau ~4 ms, release tau ~8 ms.

        // RPM/speed heuristic state for wheelspin detection (AC's SpeedKmh and
        // GroundSpeedKmH are always identical, so we can't use that diff
        // need to fall back to "RPM rising faster than speed under throttle").
        // The derivative is windowed by COUNTING TICKS of the fixed 500 Hz
        // effect tick (10 ticks = 20 ms): a per-tick RPM delta is numerically
        // useless, and wall-clock windowing would break the replay harness,
        // which drives this pipeline on a virtual clock much faster than
        // real time. The estimate computed at a window boundary is held in
        // _wheelspinHold for the ticks in between. Any tick that skips the
        // heuristic (neutral, low speed, stall) invalidates the baseline: an
        // RPM delta spanning such a gap is free-revving, not wheelspin.
        private const int    WheelspinWindowTicks = 10;
        private const double WheelspinWindowSec   = WheelspinWindowTicks / 500.0;
        private double _prevRpm;
        private double _prevSpeed;
        private bool   _wheelspinSeeded;
        private int    _wheelspinTicks;
        private double _wheelspinHold;

        public override bool IsActive => IsTesting || (Enabled && _noise.IsActive);

        public override double ActivityLevel => Math.Min(1.0, Math.Max(0.0, _slipEma));

        public override void RenderAdd(float[] buffer, int count)
        {
            if (!Enabled && !IsTesting) return;
            float dm = DuckMultiplier;
            if (dm <= 0f) return;
            if (dm >= 0.999f)
            {
                _noise.RenderAdd(buffer, count);
                return;
            }
            // Render to scratch and scale before mixing, same pattern as
            // EnginePulse. We can't mutate _noise.Amp here because OnTelemetry
            // writes to it from the producer thread and the temporary clobber
            // would race with that writer.
            if (_scratch == null || _scratch.Length < count) _scratch = new float[count];
            Array.Clear(_scratch, 0, count);
            _noise.RenderAdd(_scratch, count);
            for (int i = 0; i < count; i++) buffer[i] += _scratch[i] * dm;
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
            // Sanitize a non-finite speed up front: a NaN slips through the
            // MinSpeedKmh gate below (every NaN comparison is false) and then
            // NaNs the oscillator frequency; an Infinity pins pitch at max.
            double speedKmh = f.SpeedKmh;
            if (double.IsNaN(speedKmh) || double.IsInfinity(speedKmh)) speedKmh = 0.0;
            double speedNormForPitch = Math.Min(1.0, Math.Max(0.0, speedKmh / Math.Max(1.0, PitchMaxKmh)));
            _noise.Freq = PitchBaseHz + speedNormForPitch * (PitchMaxHz - PitchBaseHz);

            // Engine free-revs in neutral; the heuristic's RPM-derivative path
            // is invalid, and the direct path doesn't need haptics during a
            // shift either. Decay and bail.
            if (string.Equals(f.Gear, "N", StringComparison.OrdinalIgnoreCase))
            {
                DecayAndEmit();
                return;
            }

            // Airborne: unloaded wheels can't lose traction, but the heuristic
            // misreads the state, a free-revving engine gives the exact
            // RPM-rises-while-speed-doesn't wheelspin signature, and a car
            // rotating in the air gives yaw with near-zero lateral g (issue #34).
            // Decay and bail like the neutral gate. The direct path's #30
            // load-weighting already zeroes an airborne reading; this also covers
            // the heuristic path, which has no per-wheel load to lean on. Airborne
            // is null on the generic SimHub source until #32 populates it (== true
            // stays false then, i.e. no-op); AC, Forza and the FS mod set it today.
            if (f.Airborne == true)
            {
                DecayAndEmit();
                return;
            }

            // Suppress at very low speed (heuristic math unstable, slow
            // standing wheelspin doesn't need haptic feedback either way).
            if (speedKmh < MinSpeedKmh)
            {
                DecayAndEmit();
                return;
            }

            double rawTraction = f.WheelSlip is double directSlip
                ? NormalizeDirectSlip(directSlip)
                : ComputeHeuristic(f, speedKmh);

            // Final firewall before the EMA: a non-finite rawTraction (bad
            // slip sample, or a heuristic divide on a garbage frame) would
            // get latched into _slipEma and, for NaN, never clear, the buzz
            // would be stuck on (or off) for the rest of the session.
            if (double.IsNaN(rawTraction) || double.IsInfinity(rawTraction))
                rawTraction = 0.0;

            // Tighter decay: when rawTraction is near zero, snap _slipEma down
            // quickly so the buzz ends within ~20 ms of grip recovery instead
            // of ringing on.
            if (rawTraction < 0.05)
            {
                _slipEma *= 0.63;      // AC-rate 0.5/frame rescaled to the 500 Hz tick
                if (_slipEma < 0.01) _slipEma = 0;
            }
            else
            {
                // AC-rate 0.5 / 0.3 rescaled to the 500 Hz tick (see header).
                double alpha = (rawTraction > _slipEma) ? 0.37 : 0.21;
                _slipEma = _slipEma * (1 - alpha) + rawTraction * alpha;
            }
            _noise.Amp = (float)(_slipEma * 0.40 * Gain);
        }

        // Telemetry stopped: if the game closed mid-slide, the buzz amplitude
        // would hold forever. Clear the smoothed slip and silence the noise.
        public override void OnTelemetryStall()
        {
            _slipEma = 0;
            _wheelspinSeeded = false;
            _wheelspinTicks  = 0;
            _wheelspinHold   = 0;
            _noise.Amp = 0;
        }

        private void DecayAndEmit()
        {
            _slipEma *= 0.54;      // AC-rate 0.4/frame rescaled to the 500 Hz tick (see header)
            // These ticks skip ComputeHeuristic, so the wheelspin baseline
            // dies with them: an RPM delta measured across a neutral / low-
            // speed gap is free-revving, not wheelspin.
            _wheelspinSeeded = false;
            _wheelspinTicks  = 0;
            _wheelspinHold   = 0;
            _noise.Amp = (float)(_slipEma * 0.40 * Gain);
        }

        // Direct-path normalization: 5% slip = effect activates, 50% = full.
        // Sensitivity widens both bounds (>1 makes the effect more eager,
        // <1 stricter). Mirrors the sensitivity feel of the heuristic path
        // so users don't need to re-tune when switching between AC and other
        // games.
        private double NormalizeDirectSlip(double slip)
        {
            // Floor at 0.1 (matches the slider's minimum). At Sensitivity=1.0
            // the deadband is 0.05 and full effect at slip=0.50; at 0.1 those
            // shift to 0.50 and 5.0 respectively, strict enough to ignore
            // routine cornering on grippy tires (AC's wheelSlip[] regularly
            // sits around 0.15-0.30 in normal driving).
            double slipMag  = Math.Abs(slip);
            double deadband = 0.05 / Math.Max(0.1, Sensitivity);
            double slipFull = 0.50 / Math.Max(0.1, Sensitivity);
            double excess   = Math.Max(0, slipMag - deadband);
            return Math.Min(1.0, excess / Math.Max(0.05, slipFull));
        }

        private double ComputeHeuristic(TelemetryFrame f, double speedKmh)
        {
            // ---------- Wheelspin (RPM rising faster than speed) ----------
            // AC's SpeedKmh and GroundSpeedKmH are always equal (verified from
            // diag log), so we can't use their diff. Fall back to the classic
            // heuristic: RPM rising sharply while speed isn't. Gated on
            // throttle and on RPM being well below redline (rules out limiter).
            // Tick-counted windowing per the field comment; the old wall-clock
            // "dtSec >= 0.005" gate never passed once the engine ticked this
            // at a fixed 500 Hz (every call rebased the baseline 2 ms after
            // the last), which silently disabled the detector.
            double throttlePct = f.Throttle01 * 100.0;
            if (!_wheelspinSeeded)
            {
                _wheelspinSeeded = true;
                _wheelspinTicks  = 0;
                _prevRpm   = f.Rpms;
                _prevSpeed = f.SpeedKmh;
            }
            else if (++_wheelspinTicks >= WheelspinWindowTicks)
            {
                double ws = 0;
                if (throttlePct >= 25.0
                    && f.MaxRpm > 0 && f.Rpms < f.MaxRpm * 0.95)   // not at limiter
                {
                    double dRpm   = (f.Rpms     - _prevRpm)   / WheelspinWindowSec;   // RPM/s
                    double dSpeed = (f.SpeedKmh - _prevSpeed) / WheelspinWindowSec;   // (km/h)/s
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
                        ws = Math.Min(1.0, rpmExcess * speedFactor);
                    }
                }
                _wheelspinHold  = ws;
                _wheelspinTicks = 0;
                _prevRpm   = f.Rpms;
                _prevSpeed = f.SpeedKmh;
            }
            // Mid-window ticks keep the baseline and the held value.
            double wheelspinNorm = _wheelspinHold;

            // ---------- Drift / oversteer (slip angle + transient detectors) ----------
            // Source delivers yaw rate in deg/s; convert to rad/s here.
            double speedMs    = speedKmh / 3.6;
            double yawRateDeg = Math.Abs(f.YawRateDegPerSec ?? 0);
            double yawRate    = yawRateDeg * Math.PI / 180.0;          // rad/s
            double swayRaw    = f.AccelerationSway ?? 0;
            double lateralG   = Math.Abs(swayRaw);                    // m/s²

            // Detector A, SLIP ANGLE (the physical signal we actually want).
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
            // MEASURED beats inferred, outright. A source that publishes its
            // velocity vector (iRacing) hands us the real angle between where the
            // car points and where it is going, instantaneously and with a sign.
            // The inference below is what made this effect feel like it was
            // reporting body rotation rather than grip: it can only be solved
            // when the car is ALREADY in settled circular motion, so the moment
            // of breakaway, which is the whole cue, violates its one assumption,
            // and acos amplifies noise precisely where the two inputs nearly
            // cancel, which is everywhere below a real slide.
            if (f.SideslipDeg is double measuredBeta)
            {
                slipAngleDeg = Math.Abs(measuredBeta);
            }
            else if (centripetalRequired > 1.0)
            {
                double cosBeta = lateralG / centripetalRequired;
                if (cosBeta > 1.0) cosBeta = 1.0;
                if (cosBeta < -1.0) cosBeta = -1.0;
                slipAngleDeg = Math.Acos(cosBeta) * 180.0 / Math.PI;
            }
            // 5° deadband (allow natural slip), 50° = full effect. Wider range
            // than the previous 25° so heavy drifts have headroom to feel
            // "louder than" moderate ones, gives ~5× dynamic range from
            // light slip to heavy across β=10°→50°, addressing "feels static."
            // Two different quantities need two different scales, and using one
            // for both is why the measured path only spoke at extreme slide.
            //
            // The 5 / 50 degree pair was calibrated against the INFERRED angle,
            // which acos inflates: noise between lateral g and yaw rate reads as
            // tens of degrees. Real sideslip is small. Measured on an MX-5 Cup at
            // 1.3 g of cornering, beta ran 0.2 to 2.3 degrees (owner log,
            // 2026-08-16), so a 50 degree full scale put a genuine slide at three
            // percent of the effect and asked a physically impossible angle for
            // all of it. That is the "only surfaces at extreme slide" report.
            //
            // Measured scale: 1 degree of deadband, to allow the slip every
            // cornering tire carries, and 10 degrees for full, a car well
            // sideways.
            double slipDeadband, slipFullDeg, fullFloor;
            if (f.SideslipDeg.HasValue)
            {
                slipDeadband =  1.0 / Math.Max(0.1, Sensitivity);
                slipFullDeg  = 10.0 / Math.Max(0.1, Sensitivity);
                // The inferred path floors full scale at 5 degrees, which is
                // wider than this ENTIRE range and would undo the rescale at any
                // sensitivity above 2. Guard against divide-by-zero, nothing more.
                fullFloor = 0.5;
            }
            else
            {
                slipDeadband = 5.0  / Math.Max(0.3, Sensitivity);
                slipFullDeg  = 50.0 / Math.Max(0.3, Sensitivity);
                fullFloor = 5.0;
            }
            double slipExcess   = Math.Max(0, slipAngleDeg - slipDeadband);
            double driftFromSlipAngle = Math.Min(1.0, slipExcess / Math.Max(fullFloor, slipFullDeg));

            // Detector B, centripetal imbalance (transient breakaway).
            // Catches the moment of rear breakout at speeds too low for the
            // slip-angle formula to be reliable, and during rapid yaw acceleration.
            // Silent when detector A is measured rather than inferred. This one
            // exists to cover A's blind spots, and it pays for that by firing on
            // yaw rate exceeding what lateral g implies, which is to say ON THE
            // CAR ROTATING. That is the "it goes off just from turning, and late"
            // complaint in one line: body rotation lags the tire letting go, so
            // as a grip cue it is both wrong and behind. With a true velocity
            // vector, A already sees the slide the instant lateral velocity
            // appears and needs no help.
            double expectedYaw = (speedMs > 0.1) ? lateralG / speedMs : 0;
            double yawExcess   = Math.Max(0, yawRate - expectedYaw - 0.08);
            double driftScale  = 0.33 / Math.Max(0.1, Sensitivity);
            double driftFromExcess = f.SideslipDeg.HasValue
                ? 0.0
                : Math.Min(1.0, yawExcess / Math.Max(0.05, driftScale));

            double driftNorm = Math.Max(driftFromSlipAngle, driftFromExcess);
            double rawTraction = Math.Max(wheelspinNorm, driftNorm);

            // ---------- TC intervention boost ----------
            // SimHub doesn't expose direct wheel slip, so when the game's own
            // traction control is intervening it's the most reliable
            // ground-truth signal we have that the tires are losing grip.
            // Raise rawTraction to a moderate floor (0.4) so a faint TC
            // intervention still produces felt feedback even when the
            // heuristic above missed it. Only floor, Math.Max preserves any
            // stronger reading from the heuristic. SimHub-source-only in
            // practice; AC/Forza paths use direct WheelSlip and skip this
            // function entirely.
            if (f.TcActive > 0)
            {
                double tcFloor = 0.4;
                if (rawTraction < tcFloor) rawTraction = tcFloor;
            }

            // Diagnostic, once per second, only when something interesting.
            // Wall-clock on purpose: this is log throttling, not signal math.
            long now = Stopwatch.GetTimestamp();
            if (rawTraction > _peakSlipSinceLastLog) _peakSlipSinceLastLog = rawTraction;
            if (now - _lastDiagLogTicks > Stopwatch.Frequency)
            {
                if (_peakSlipSinceLastLog > 0.05)
                {
                    // DiagLog, not SimHub.Logging: the Engine assembly has no
                    // SimHub reference; the plugin injects the logger sink.
                    DiagLog?.Invoke(
                        $"[TF4ALL] traction diag | spd={speedKmh:F1} thr={throttlePct:F0} | yawDeg={yawRateDeg:F1} sway={swayRaw:F2} cent={centripetalRequired:F2} β={slipAngleDeg:F1}°{(f.SideslipDeg.HasValue ? " MEASURED" : " inferred")} | dSlip={driftFromSlipAngle:F2} dExc={driftFromExcess:F2} ws={wheelspinNorm:F2} | peak={_peakSlipSinceLastLog:F2} ema={_slipEma:F2}");
                }
                _lastDiagLogTicks = now;
                _peakSlipSinceLastLog = 0;
            }
            return rawTraction;
        }

        public override void SeedNoise(int seed) => _noise.SeedNoise(seed);

        public override void Reset()
        {
            // Clear EMA + the dRpm/dSpeed history so the new car's first frame
            // doesn't compute a delta against the previous car's last sample
            // (which would spike the wheelspin heuristic on a 800 → 6000 RPM
            // jump from idling-car to running-car spawn).
            _slipEma              = 0;
            _prevRpm              = 0;
            _prevSpeed            = 0;
            _wheelspinSeeded      = false;
            _wheelspinTicks       = 0;
            _wheelspinHold        = 0;
            _lastDiagLogTicks     = 0;
            _peakSlipSinceLastLog = 0;
            _noise.Amp            = 0;
        }
    }
}
