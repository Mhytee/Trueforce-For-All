// Shared base for telemetry sources: MeasuredHz tracking (EMA on
// inter-frame intervals), CapturedAtTicks stamping, and the universal
// IsSessionActive physics proxy. See ITelemetrySource.cs for the contract
// and threading notes; TelemetryFrame.cs for the data shape.

using System;
using System.Diagnostics;

namespace TrueforceForAll.Core
{
    /// <summary>Default base class for sources. Handles MeasuredHz tracking
    /// (EMA on inter-frame intervals) and stamps CapturedAtTicks. Subclasses
    /// build a TelemetryFrame and call EmitFrame.</summary>
    public abstract class TelemetrySourceBase : ITelemetrySource
    {
        public abstract string Name { get; }
        public abstract bool IsEnhanced { get; }
        public abstract bool IsRunning { get; }

        // Default false. Sources that surface NumCylinders override to true.
        public virtual bool ProvidesNumCylinders => false;

        public Action<TelemetryFrame> OnFrame { get; set; }

        public abstract void Start();
        public abstract void Stop();

        public virtual void Dispose() { Stop(); }

        // We smooth the inter-frame INTERVAL (an EMA on dt), then report its
        // reciprocal. Averaging 1/dt directly (the old approach) is biased
        // high: 1/x is convex, so mean(1/dt) >= 1/mean(dt) by Jensen's
        // inequality, and any timing jitter inflates the readout. UDP delivery
        // in particular is bursty: the OS hands the receive thread two
        // coalesced datagrams microseconds apart, producing a momentary
        // instantaneous rate of thousands of Hz that drags an EMA-of-rate well
        // above the true packet cadence (a real 60 Hz Forza stream read as
        // 130-150 Hz). Averaging dt linearly cancels those bursts against the
        // gaps that follow them, so 1/mean(dt) recovers the true throughput.
        // The public getter zeros out if the source has gone quiet so the UI
        // shows 0 Hz when a game is paused / unloaded rather than a stale value.
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastFrameTicks;
        private double _emaIntervalSec;
        private const double Alpha = 0.1;       // EMA smoothing factor
        private const double IdleTimeoutSec = 1.0;

        public double MeasuredHz
        {
            get
            {
                long last = System.Threading.Interlocked.Read(ref _lastFrameTicks);
                if (last == 0) return 0;
                double sinceSec = (_sw.ElapsedTicks - last) / (double)Stopwatch.Frequency;
                if (sinceSec > IdleTimeoutSec) return 0;
                double interval = _emaIntervalSec;
                return interval > 0 ? 1.0 / interval : 0;
            }
        }

        public double MsSinceLastFrame
        {
            get
            {
                long last = System.Threading.Interlocked.Read(ref _lastFrameTicks);
                if (last == 0) return double.PositiveInfinity;
                return (_sw.ElapsedTicks - last) * 1000.0 / Stopwatch.Frequency;
            }
        }

        protected void EmitFrame(TelemetryFrame frame)
        {
            long now = _sw.ElapsedTicks;
            long last = _lastFrameTicks;
            if (last != 0)
            {
                double dtSec = (now - last) / (double)Stopwatch.Frequency;
                if (dtSec > 0)
                {
                    _emaIntervalSec = _emaIntervalSec > 0
                        ? _emaIntervalSec * (1.0 - Alpha) + dtSec * Alpha
                        : dtSec;
                }
            }
            System.Threading.Interlocked.Exchange(ref _lastFrameTicks, now);
            frame.CapturedAtTicks = now;
            _lastFrame = frame;
            OnFrame?.Invoke(frame);
        }

        // Last frame emitted, for the default IsSessionActive physics proxy.
        private TelemetryFrame _lastFrame;

        // Universal "force feedback should be flowing" signal, derived from the
        // last frame so it works for any game through the SimHub fallback. False
        // when no frames are arriving (idle / paused / menu where telemetry
        // stops), otherwise true when the car looks live: engine running,
        // moving, or pedal input. Sources with an explicit session flag override.
        public virtual bool IsSessionActive
        {
            get
            {
                if (MeasuredHz <= 0) return false;   // no telemetry flowing
                var f = _lastFrame;
                return f.Rpms > 1.0 || f.SpeedKmh > 2.0 || f.Throttle01 > 0.02;
            }
        }

        // Base sources infer IsSessionActive from physics, not an authoritative
        // pause flag. Sources with a real session signal (Forza) override this.
        public virtual bool HasAuthoritativeSessionState => false;
    }
}
