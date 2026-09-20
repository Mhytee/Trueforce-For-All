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

        /// <summary>Time constant of the band-limited differentiator that
        /// produces Acceleration, seconds, used for both of its poles. Sets
        /// the corner (~3 Hz), the smoothing and the lag; inertia rendering
        /// wants trend over tick detail. See the note in Publish for why the
        /// second pole is not optional.</summary>
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
            // steady acceleration below the corner at ~1/(2 pi tau) ~ 3 Hz.
            //
            // ONE POLE IS NOT ENOUGH, and that was the G923 inertia buzz
            // (2026-09-15 diagnosis, fixed 2026-09-17). A single-pole
            // filtered derivative stops rising at the corner and then holds
            // FLAT at 1/tau for every frequency above it, all the way to the
            // packet rate: at 50 Hz it reports velocity times twenty and
            // calls it acceleration. Rendered as inertia that is a twenty
            // times damper, and through the loop delay to the motor a damper
            // that far out is negative damping, which self-excites on a
            // geared wheel whose rotor sits in the gear lash.
            //
            // A real differentiator's response keeps rising, which no
            // sampled system can do (it is unbounded noise gain on a
            // quantized encoder), so every practical one adds a second pole
            // and rolls off after the corner. That is what this is: the same
            // derivative below 3 Hz, falling instead of flat above it.
            // Simucube and OpenFFBoard both run second-order filters on
            // their velocity input for the same reason.
            //
            //     a_raw = (v - v_slow)/tau ;  a += (a_raw - a) x dt/(tau+dt)
            //
            // Response: s / (1 + s tau)^2, magnitude w/(1+(w tau)^2). At
            // 50 Hz that is about 1.27 times velocity where one pole gave 20,
            // a sixteenfold cut in the band that buzzes, and it keeps falling
            // above that where one pole never did. Below the corner it is the
            // same derivative: at 1 Hz it reads 5.72 against a true 6.28,
            // which is the band hands actually drive. The cost is
            // a little phase lag between about 3 and 15 Hz, so a fast flick
            // reads its peak a few milliseconds late.
            double at = dt / (AccelTauSec + dt);
            _velSlow += (_velFiltered - _velSlow) * at;
            double accelRaw = (_velFiltered - _velSlow) / AccelTauSec;
            _accel += (accelRaw - _accel) * at;
            _prevVel = _velFiltered;
        }
    }
}
