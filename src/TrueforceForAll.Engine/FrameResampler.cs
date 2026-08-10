// Fixed-tick frame resampler — phase 1 of docs/haptic-engine-plan.md.
//
// Sources emit TelemetryFrames at whatever rate the game gives them (FM8
// ~60-80 Hz, AC ~333 Hz, SimHub ~60 Hz). The engine ticks effects at a fixed
// 500 Hz. This class bridges the two:
//
//   Fast sources (>= ~150 Hz): a jitter buffer holds ~1.5 source intervals of
//   lookahead and continuous fields are LERPED between the bracketing frames.
//   Cost: a few ms of added latency. Benefit: zero extrapolation error.
//
//   Slow sources (< ~150 Hz — the FM8 case this phase exists for): buffering
//   1.5 intervals of a 60 Hz stream would add ~25 ms of latency, so instead we
//   run at the live edge: an alpha-beta filter bank tracks (value, velocity)
//   per continuous channel and extrapolates from the newest frame. Zero added
//   latency; the horizon clamp bounds a dropped packet's damage.
//
//   Discrete fields (gear, flags, counts) are never interpolated: they
//   sample-and-hold from the newest frame at-or-before the sample time, so an
//   edge is emitted exactly once and never early.
//
// Mode switches use hysteresis (150/170 Hz) so a source hovering near the
// boundary doesn't flap between latency profiles.
//
// Threading: Ingest() on the source thread, TrySample() on the engine thread,
// guarded by a short lock around the small ring (held for nanoseconds; both
// call sites are well under 1 kHz). Time is caller-supplied ticks with a
// caller-supplied tick rate, so the replay harness drives this class on the
// fixture's own microsecond clock and gets deterministic output.

using System;

namespace TrueforceForAll.Core
{
    public sealed class FrameResampler
    {
        // Mode-select hysteresis (Hz). Between the two, the current mode holds.
        private const double LiveEdgeBelowHz  = 150.0;
        private const double BufferedAboveHz  = 170.0;

        // Jitter-buffer depth in source intervals (buffered mode).
        private const double BufferIntervals = 1.5;

        // Frames retained. 8 spans >100 ms at 60 Hz and >24 ms at 333 Hz —
        // enough lookback for bracketing plus a late tick.
        private const int RingSize = 8;

        // Keepalive / pause handling. Forza (FH6 especially) interleaves
        // all-zero "keepalive" frames between real physics frames, and streams
        // them continuously during a pause. Feeding those into the lerp/filter
        // path would sawtooth every channel to zero and back at frame rate —
        // the exact stutter bug the old DispatchFrame hold-quads logic fixed.
        // So: only physics frames enter the ring/filters; a non-physics frame
        // is latched aside, and it only becomes the OUTPUT once no physics
        // frame has arrived for HoldSeconds (a real pause, not an interleave).
        // Zeros then flow to effects exactly as they do today, so pause decay
        // is unchanged.
        private const double HoldSeconds = 0.35;
        // "Any sign of life": Forza keepalives zero the whole payload, but a
        // SimHub-fallback game with a missing MaxRpm must not have all its
        // frames misread as keepalives (effects would starve while driving).
        private static bool IsPhysicsFrame(in TelemetryFrame f)
            => f.MaxRpm > 0 || f.Rpms > 0 || f.SpeedKmh > 0.1 || f.Throttle01 > 0.01;

        private TelemetryFrame _lastNonPhysics;
        private bool _haveNonPhysics;

        private readonly object _gate = new object();
        private readonly TelemetryFrame[] _ring = new TelemetryFrame[RingSize];
        private int  _count;              // physics frames ingested (ring index = _count % RingSize)
        private double _emaIntervalTicks; // smoothed inter-frame interval
        private readonly long _ticksPerSecond;

        private bool _liveEdgeMode = true;   // start at the live edge (safe for any rate)

        // Alpha-beta bank for live-edge mode. Order must match FillContinuous /
        // PredictContinuous below. Scalars first, then 6 quads x 4 corners.
        private const int ScalarChannels = 12;
        private const int QuadChannels   = 6 * 4;
        private readonly AlphaBetaFilter[] _ab = new AlphaBetaFilter[ScalarChannels + QuadChannels];

        public FrameResampler(long ticksPerSecond)
        {
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _ticksPerSecond = ticksPerSecond;
            for (int i = 0; i < _ab.Length; i++)
                _ab[i] = new AlphaBetaFilter();
        }

        /// <summary>True when the last TrySample ran in live-edge (predicting)
        /// mode; false = buffered interpolation. Diagnostics/UI.</summary>
        public bool IsLiveEdge => _liveEdgeMode;

        /// <summary>Measured source rate from the smoothed inter-frame
        /// interval, 0 before two frames have arrived.</summary>
        public double MeasuredHz
        {
            get
            {
                double t = _emaIntervalTicks;
                return t > 0 ? _ticksPerSecond / t : 0;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _count = 0;
                _emaIntervalTicks = 0;
                _liveEdgeMode = true;
                _haveNonPhysics = false;
                for (int i = 0; i < _ab.Length; i++) _ab[i].Reset();
            }
        }

        /// <summary>Source thread: add a frame (CapturedAtTicks must be set,
        /// monotonically increasing).</summary>
        public void Ingest(in TelemetryFrame frame)
        {
            lock (_gate)
            {
                if (!IsPhysicsFrame(in frame))
                {
                    _lastNonPhysics = frame;
                    _haveNonPhysics = true;
                    return;
                }

                if (_count > 0)
                {
                    long prev = _ring[(_count - 1) % RingSize].CapturedAtTicks;
                    long dt = frame.CapturedAtTicks - prev;
                    if (dt <= 0) return;   // out-of-order / duplicate: drop
                    _emaIntervalTicks = _emaIntervalTicks > 0
                        ? _emaIntervalTicks * 0.9 + dt * 0.1
                        : dt;

                    // Feed the alpha-beta bank on every arrival so live-edge
                    // predictions are always warm regardless of current mode.
                    double dtSec = dt / (double)_ticksPerSecond;
                    UpdateFilters(in frame, dtSec);
                }
                else
                {
                    UpdateFilters(in frame, 0);   // seed
                }

                _ring[_count % RingSize] = frame;
                _count++;

                // Mode select with hysteresis.
                double hz = MeasuredHz;
                if (_liveEdgeMode && hz >= BufferedAboveHz) _liveEdgeMode = false;
                else if (!_liveEdgeMode && hz <= LiveEdgeBelowHz) _liveEdgeMode = true;
            }
        }

        /// <summary>Engine thread: produce the frame for time <paramref name="nowTicks"/>.
        /// False when no frames have been ingested yet. The returned frame's
        /// CapturedAtTicks is the sample time it represents.</summary>
        public bool TrySample(long nowTicks, out TelemetryFrame sample)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    // Nothing but keepalives so far (menus): pass them through
                    // so effects see the same zero frames they do today.
                    sample = _lastNonPhysics;
                    if (_haveNonPhysics) sample.CapturedAtTicks = nowTicks;
                    return _haveNonPhysics;
                }

                ref TelemetryFrame newest = ref _ring[(_count - 1) % RingSize];

                // Pause detection: keepalives keep arriving but physics frames
                // stopped for HoldSeconds -> the zero frame becomes the output
                // (effects decay exactly as on the old direct path). A lone
                // interleaved keepalive never gets this far because a physics
                // frame lands again within one source interval.
                if (_haveNonPhysics
                    && _lastNonPhysics.CapturedAtTicks > newest.CapturedAtTicks
                    && (nowTicks - newest.CapturedAtTicks) > (long)(HoldSeconds * _ticksPerSecond))
                {
                    sample = _lastNonPhysics;
                    sample.CapturedAtTicks = nowTicks;
                    return true;
                }

                if (_liveEdgeMode || _count == 1)
                {
                    // Live edge: discrete state from the newest frame,
                    // continuous channels extrapolated by the filter bank.
                    sample = newest;                                   // struct copy: discrete + defaults
                    double horizonSec = (nowTicks - newest.CapturedAtTicks) / (double)_ticksPerSecond;
                    if (horizonSec > 0) PredictContinuous(ref sample, horizonSec);
                    sample.CapturedAtTicks = nowTicks;
                    return true;
                }

                // Buffered mode: sample BufferIntervals behind the newest
                // frame (bounded by now — never sample the future).
                long delay = (long)(BufferIntervals * _emaIntervalTicks);
                long target = Math.Min(nowTicks, newest.CapturedAtTicks) - delay + (long)_emaIntervalTicks;
                // target sits ~0.5 interval behind newest: normally bracketed.

                // Find bracketing frames a (<= target) and b (> target).
                int oldest = Math.Max(0, _count - RingSize);
                TelemetryFrame a = _ring[oldest % RingSize];
                TelemetryFrame b = newest;
                bool haveB = false;
                for (int i = _count - 1; i >= oldest; i--)
                {
                    ref TelemetryFrame f = ref _ring[i % RingSize];
                    if (f.CapturedAtTicks <= target) { a = f; break; }
                    b = f; haveB = true;
                }

                if (!haveB || b.CapturedAtTicks <= a.CapturedAtTicks)
                {
                    // Target at/after newest (stall or startup): hold newest.
                    sample = newest;
                    sample.CapturedAtTicks = nowTicks;
                    return true;
                }

                double t = (target - a.CapturedAtTicks) /
                           (double)(b.CapturedAtTicks - a.CapturedAtTicks);
                if (t < 0) t = 0; else if (t > 1) t = 1;

                // Discrete fields sample-and-hold from 'a' (the newest frame
                // at-or-before the sample time) — never from the future frame,
                // so edges emit once and never early.
                sample = a;
                LerpContinuous(ref sample, in a, in b, t);
                sample.CapturedAtTicks = nowTicks;
                return true;
            }
        }

        // ---------------- continuous-channel plumbing ----------------
        // One place defines "what is continuous": FillContinuous reads the
        // channel values out of a frame; LerpContinuous and PredictContinuous
        // write blended/predicted values back. Nullable scalars blend only
        // when BOTH frames carry a value (else the sample-and-hold value from
        // 'a' stands). Quads blend only when both frames have quads.

        private static void LerpContinuous(ref TelemetryFrame s, in TelemetryFrame a, in TelemetryFrame b, double t)
        {
            s.SpeedKmh   = Lerp(a.SpeedKmh, b.SpeedKmh, t);
            s.Rpms       = Lerp(a.Rpms, b.Rpms, t);
            s.Throttle01 = Lerp(a.Throttle01, b.Throttle01, t);
            s.RpmPercent = Lerp(a.RpmPercent, b.RpmPercent, t);

            s.AccelerationHeave = LerpN(a.AccelerationHeave, b.AccelerationHeave, t);
            s.AccelerationSway  = LerpN(a.AccelerationSway,  b.AccelerationSway,  t);
            s.AccelerationSurge = LerpN(a.AccelerationSurge, b.AccelerationSurge, t);
            s.YawRateDegPerSec  = LerpN(a.YawRateDegPerSec,  b.YawRateDegPerSec,  t);
            s.SteeringAngle     = LerpN(a.SteeringAngle,     b.SteeringAngle,     t);
            s.Brake01           = LerpN(a.Brake01,           b.Brake01,           t);
            s.Clutch01          = LerpN(a.Clutch01,          b.Clutch01,          t);
            s.Handbrake01       = LerpN(a.Handbrake01,       b.Handbrake01,       t);
            s.WheelSlip         = LerpN(a.WheelSlip,         b.WheelSlip,         t);
            s.FrontSlipAngleRad     = LerpN(a.FrontSlipAngleRad,     b.FrontSlipAngleRad,     t);
            s.FrontSuspTravelMeters = LerpN(a.FrontSuspTravelMeters, b.FrontSuspTravelMeters, t);
            s.SurfaceRumble         = LerpN(a.SurfaceRumble,         b.SurfaceRumble,         t);

            if (a.HasTireQuads && b.HasTireQuads)
            {
                s.TireSlipRatio    = LerpQ(a.TireSlipRatio,    b.TireSlipRatio,    t);
                s.TireSlipAngleRad = LerpQ(a.TireSlipAngleRad, b.TireSlipAngleRad, t);
                s.TireCombinedSlip = LerpQ(a.TireCombinedSlip, b.TireCombinedSlip, t);
                s.SuspTravelM      = LerpQ(a.SuspTravelM,      b.SuspTravelM,      t);
                s.SurfaceRumbleQ   = LerpQ(a.SurfaceRumbleQ,   b.SurfaceRumbleQ,   t);
                s.WheelRotRadS     = LerpQ(a.WheelRotRadS,     b.WheelRotRadS,     t);
            }
        }

        private void UpdateFilters(in TelemetryFrame f, double dtSec)
        {
            int k = 0;
            U(ref k, f.SpeedKmh, dtSec);
            U(ref k, f.Rpms, dtSec);
            U(ref k, f.Throttle01, dtSec);
            U(ref k, f.RpmPercent, dtSec);
            UN(ref k, f.AccelerationHeave, dtSec);
            UN(ref k, f.AccelerationSway, dtSec);
            UN(ref k, f.AccelerationSurge, dtSec);
            UN(ref k, f.YawRateDegPerSec, dtSec);
            UN(ref k, f.SteeringAngle, dtSec);
            UN(ref k, f.WheelSlip, dtSec);
            UN(ref k, f.FrontSlipAngleRad, dtSec);
            UN(ref k, f.FrontSuspTravelMeters, dtSec);
            if (f.HasTireQuads)
            {
                UQ(ref k, f.TireSlipRatio, dtSec);
                UQ(ref k, f.TireSlipAngleRad, dtSec);
                UQ(ref k, f.TireCombinedSlip, dtSec);
                UQ(ref k, f.SuspTravelM, dtSec);
                UQ(ref k, f.SurfaceRumbleQ, dtSec);
                UQ(ref k, f.WheelRotRadS, dtSec);
            }
        }

        private void PredictContinuous(ref TelemetryFrame s, double horizonSec)
        {
            int k = 0;
            s.SpeedKmh   = Math.Max(0, P(ref k, horizonSec));
            s.Rpms       = Math.Max(0, P(ref k, horizonSec));
            s.Throttle01 = Clamp01(P(ref k, horizonSec));
            s.RpmPercent = Clamp01(P(ref k, horizonSec));
            s.AccelerationHeave = PN(ref k, s.AccelerationHeave, horizonSec);
            s.AccelerationSway  = PN(ref k, s.AccelerationSway, horizonSec);
            s.AccelerationSurge = PN(ref k, s.AccelerationSurge, horizonSec);
            s.YawRateDegPerSec  = PN(ref k, s.YawRateDegPerSec, horizonSec);
            s.SteeringAngle     = PN(ref k, s.SteeringAngle, horizonSec);
            s.WheelSlip         = PN(ref k, s.WheelSlip, horizonSec);
            s.FrontSlipAngleRad     = PN(ref k, s.FrontSlipAngleRad, horizonSec);
            s.FrontSuspTravelMeters = PN(ref k, s.FrontSuspTravelMeters, horizonSec);
            if (s.HasTireQuads)
            {
                s.TireSlipRatio    = PQ(ref k, horizonSec);
                s.TireSlipAngleRad = PQ(ref k, horizonSec);
                s.TireCombinedSlip = PQ(ref k, horizonSec);
                s.SuspTravelM      = PQ(ref k, horizonSec);
                s.SurfaceRumbleQ   = PQ(ref k, horizonSec);
                s.WheelRotRadS     = PQ(ref k, horizonSec);
            }
            // SurfaceRumble scalar intentionally not predicted: it's a texture
            // magnitude, and extrapolating texture invents detail. Hold.
        }

        private void U(ref int k, double v, double dt)  { _ab[k++].Update(v, dt); }
        private void UN(ref int k, double? v, double dt)
        {
            if (v.HasValue) _ab[k].Update(v.Value, dt);
            k++;
        }
        private void UQ(ref int k, in TireQuad q, double dt)
        {
            _ab[k++].Update(q.FL, dt);
            _ab[k++].Update(q.FR, dt);
            _ab[k++].Update(q.RL, dt);
            _ab[k++].Update(q.RR, dt);
        }
        private double P(ref int k, double h) => _ab[k++].PredictAt(h);
        private double? PN(ref int k, double? held, double h)
        {
            var f = _ab[k++];
            return held.HasValue && f.HasState ? f.PredictAt(h) : held;
        }
        private TireQuad PQ(ref int k, double h) => TireQuad.Of(
            _ab[k++].PredictAt(h), _ab[k++].PredictAt(h),
            _ab[k++].PredictAt(h), _ab[k++].PredictAt(h));

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double? LerpN(double? a, double? b, double t)
            => (a.HasValue && b.HasValue) ? a.Value + (b.Value - a.Value) * t : a;
        private static TireQuad LerpQ(in TireQuad a, in TireQuad b, double t) => TireQuad.Of(
            Lerp(a.FL, b.FL, t), Lerp(a.FR, b.FR, t), Lerp(a.RL, b.RL, t), Lerp(a.RR, b.RR, t));
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
