// Position, velocity and acceleration of the physical wheel from any
// normalized position source (WheelSteeringReader's HID reports, game
// steering, or DirectInputWheel), for the condition-effect renderer.
//
// Two ingest paths:
//  - Update(pos, ...): position only; velocity comes from an alpha-beta
//    tracker (a 16-bit encoder differenced at 1 kHz is dominated by
//    quantization noise).
//  - UpdateWithVelocity(pos, vel, ...): the source already differenced and
//    smoothed its own velocity (DirectInputWheel's poll loop). Deriving it
//    AGAIN from quantized position was the slow-turn damper shake on the
//    rig (2026-09-01): one derivation, then one filter, never two.
//
// Either way the published Velocity passes a ~60 Hz one-pole (about 2.5 ms
// of lag: the same class of velocity filtering every shipping condition
// renderer applies; Simucube filters its axis speed harder), so near-zero
// speeds cannot chatter the damper. Units: position -1..1 across the
// configured rotation range, velocity in that per second, acceleration per
// second squared: the same units DirectInputWheel reports and DAMPCAL
// calibrates in, so gains transfer.
//
// Single-threaded by contract: Update* and the properties are called from
// the pump thread only (the provider lambda), at up to 1 kHz.

using System;

namespace TrueforceForAll.Core
{
    public sealed class WheelMotionEstimator
    {
        private readonly AlphaBetaFilter _pos = new AlphaBetaFilter
        {
            Alpha = 0.5,
            Beta  = 0.15,
            MaxHorizonSec = 0.05,
        };

        private double _velFiltered;
        private double _accel;
        private double _velSlow;
        private double _prevVel;
        private long   _prevTicks;
        private bool   _has;
        private double _posDirect;
        private bool   _directMode;

        /// <summary>Gap (seconds) beyond which the state is stale and resets
        /// instead of slewing: a source hand-over or a paused game must not
        /// produce a velocity spike.</summary>
        public double MaxGapSec { get; set; } = 0.25;

        /// <summary>Cutoff of the published-velocity one-pole, Hz.</summary>
        public double VelocityCutoffHz { get; set; } = 60;

        /// <summary>Time constant of the washout differentiator that
        /// produces Acceleration, seconds. Sets both its smoothing and its
        /// lag; inertia rendering wants trend over tick detail.</summary>
        public const double AccelTauSec = 0.05;

        public double Position => _directMode ? _posDirect : _pos.Value;
        public double Velocity => _velFiltered;
        public double Acceleration => _accel;
        public bool   HasState => _has;

        public void Reset()
        {
            _pos.Reset();
            _velFiltered = 0;
            _accel = 0;
            _velSlow = 0;
            _prevVel = 0;
            _prevTicks = 0;
            _has = false;
            _posDirect = 0;
            _directMode = false;
        }

        /// <summary>Ingest a position-only sample; velocity is derived by the
        /// alpha-beta tracker, then filtered.</summary>
        public void Update(double posNorm, long nowTicks, double ticksPerSecond)
        {
            if (posNorm > 1) posNorm = 1; else if (posNorm < -1) posNorm = -1;
            if (_directMode)
            {
                // Direct -> position-only hand-over: the alpha-beta filter
                // was never fed during direct mode, so running it against
                // minutes-stale state produces a velocity spike the damper
                // renders as a physical clunk (audit AT2-05). Reseed.
                _directMode = false;
                _pos.Reset();
                _pos.Update(posNorm, 0);
                _prevTicks = nowTicks;
                return;
            }
            _directMode = false;
            double dt = PrepareDt(nowTicks, ticksPerSecond, out bool fresh);
            if (fresh)
            {
                _pos.Update(posNorm, 0);
                return;
            }
            if (dt <= 0) return;
            _pos.Update(posNorm, dt);
            Publish(_pos.Velocity, dt);
        }

        /// <summary>Ingest a sample whose source already derived velocity
        /// (DirectInputWheel): no second differentiation, just the filter.</summary>
        public void UpdateWithVelocity(double posNorm, double velNormPerSec,
                                       long nowTicks, double ticksPerSecond)
        {
            if (posNorm > 1) posNorm = 1; else if (posNorm < -1) posNorm = -1;
            _directMode = true;
            _posDirect = posNorm;
            double dt = PrepareDt(nowTicks, ticksPerSecond, out bool fresh);
            if (fresh)
            {
                _velFiltered = velNormPerSec;
                _velSlow = velNormPerSec;
                return;
            }
            if (dt <= 0) return;
            Publish(velNormPerSec, dt);
        }

        // Common clock handling: returns dt, or flags a fresh/reset state.
        private double PrepareDt(long nowTicks, double ticksPerSecond, out bool fresh)
        {
            fresh = false;
            if (!_has)
            {
                _prevTicks = nowTicks;
                _prevVel = 0;
                _has = true;
                fresh = true;
                return 0;
            }
            double dt = (nowTicks - _prevTicks) / ticksPerSecond;
            if (dt <= 0) return 0;
            if (dt > MaxGapSec)
            {
                bool direct = _directMode;
                double pos = direct ? _posDirect : 0;
                Reset();
                _has = true;
                _prevTicks = nowTicks;
                _directMode = direct;
                _posDirect = pos;
                if (!direct) _pos.Update(pos, 0);
                fresh = true;
                return 0;
            }
            _prevTicks = nowTicks;
            return dt;
        }

        private void Publish(double rawVel, double dt)
        {
            // One-pole on the published velocity: near-zero speeds must not
            // chatter a damper (the rig's slow-turn shake).
            double hz = VelocityCutoffHz;
            if (hz > 0)
            {
                double a = dt / (dt + 1.0 / (2 * Math.PI * hz));
                _velFiltered += (rawVel - _velFiltered) * a;
            }
            else _velFiltered = rawVel;

            // Acceleration by washout differentiator, NOT by differencing
            // per tick. Velocity off a quantized encoder moves in steps, so
            // dv/dt at 1 kHz is a train of huge impulses (one step divided
            // by 0.001 s); a one-pole after that averages to the right
            // number but leaves ripple, and inertia is the one effect that
            // AMPLIFIES loop noise instead of suppressing it (it cancels
            // damping rather than adding it), so the ripple came through as
            // grain on the rig, 2026-09-01. Chasing velocity with a slow
            // one-pole and taking the gap gives the same derivative with no
            // division by dt at all:
            //
            //     v_slow += (v - v_slow) x dt/(tau+dt);  a = (v - v_slow)/tau
            //
            // which is a first-order high-pass scaled by 1/tau: exact for
            // steady acceleration, band-limited to ~1/(2 pi tau) ~ 3 Hz.
            // Inertia wants trend, not tick detail.
            double at = dt / (AccelTauSec + dt);
            _velSlow += (_velFiltered - _velSlow) * at;
            _accel = (_velFiltered - _velSlow) / AccelTauSec;
            _prevVel = _velFiltered;
        }
    }
}
