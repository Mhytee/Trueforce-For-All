// Frame-diff edge detection (phase 2 of docs/haptic-engine-plan.md). Turns
// the CTM's continuous signals into discrete Start/End events that transient
// effects can consume directly (kerb thump on RumbleStripStart, shift knock
// on GearChanged, judder onset on FrontBreakawayStart) instead of every
// effect re-implementing its own thresholding.
//
// Every Start/End pair is hysteresis-gated: Start fires crossing the upper
// threshold, End fires only after falling back through the LOWER one, so a
// signal hovering at the limit can't machine-gun events. State lives here;
// run one deriver per frame stream (the engine loop owns one, ticked at
// 500 Hz on resampled frames).

using System;

namespace TrueforceForAll.Core
{
    public sealed class EventDeriver
    {
        // Breakaway thresholds in grip-utilization units (~1.0 = the limit).
        public double BreakawayStart { get; set; } = 1.00;
        public double BreakawayEnd   { get; set; } = 0.90;

        // Slip-ratio thresholds. Negative ratio = wheel slower than the road
        // (lockup under braking); positive = faster (wheelspin).
        public double LockupStartRatio    { get; set; } = -0.50;
        public double LockupEndRatio      { get; set; } = -0.30;
        public double WheelspinStartRatio { get; set; } =  0.50;
        public double WheelspinEndRatio   { get; set; } =  0.30;

        private bool _frontOut, _rearOut, _lockup, _wheelspin, _rumble, _airborne;
        private string _gear;
        private bool _seededGear;

        /// <summary>Derive the edge events for this frame given the previous
        /// state. Call in frame order; returns the flags to stamp on the frame.</summary>
        public FrameEvents Derive(in TelemetryFrame f)
        {
            var ev = FrameEvents.None;

            if (f.FrontGrip01.HasValue)
                Hysteresis(f.FrontGrip01.Value, BreakawayStart, BreakawayEnd, ref _frontOut,
                           FrameEvents.FrontBreakawayStart, FrameEvents.FrontBreakawayEnd, ref ev);
            if (f.RearGrip01.HasValue)
                Hysteresis(f.RearGrip01.Value, BreakawayStart, BreakawayEnd, ref _rearOut,
                           FrameEvents.RearBreakawayStart, FrameEvents.RearBreakawayEnd, ref ev);

            if (f.HasTireQuads)
            {
                // Lockup keys on the MOST locked wheel (min ratio), wheelspin
                // on the most spun (max) — a single locked wheel is already a
                // flat-spot event even if the other three still roll.
                double minRatio = Math.Min(
                    Math.Min(f.TireSlipRatio.FL, f.TireSlipRatio.FR),
                    Math.Min(f.TireSlipRatio.RL, f.TireSlipRatio.RR));
                double maxRatio = Math.Max(
                    Math.Max(f.TireSlipRatio.FL, f.TireSlipRatio.FR),
                    Math.Max(f.TireSlipRatio.RL, f.TireSlipRatio.RR));

                Hysteresis(-minRatio, -LockupStartRatio, -LockupEndRatio, ref _lockup,
                           FrameEvents.LockupStart, FrameEvents.LockupEnd, ref ev);
                Hysteresis(maxRatio, WheelspinStartRatio, WheelspinEndRatio, ref _wheelspin,
                           FrameEvents.WheelspinStart, FrameEvents.WheelspinEnd, ref ev);
            }

            // Gear: seed silently on the first frame that carries one — a
            // deriver waking up mid-drive must not fire a phantom shift.
            if (!string.IsNullOrEmpty(f.Gear))
            {
                if (_seededGear && !string.Equals(f.Gear, _gear, StringComparison.Ordinal))
                    ev |= FrameEvents.GearChanged;
                _gear = f.Gear;
                _seededGear = true;
            }

            if (f.OnRumbleStrip.HasValue)
                BoolEdge(f.OnRumbleStrip.Value, ref _rumble,
                         FrameEvents.RumbleStripStart, FrameEvents.RumbleStripEnd, ref ev);
            if (f.Airborne.HasValue)
                BoolEdge(f.Airborne.Value, ref _airborne,
                         FrameEvents.AirborneStart, FrameEvents.AirborneEnd, ref ev);

            return ev;
        }

        private static void Hysteresis(double value, double startAt, double endAt,
            ref bool active, FrameEvents startFlag, FrameEvents endFlag, ref FrameEvents ev)
        {
            if (!active && value >= startAt) { active = true;  ev |= startFlag; }
            else if (active && value < endAt) { active = false; ev |= endFlag; }
        }

        private static void BoolEdge(bool value, ref bool active,
            FrameEvents startFlag, FrameEvents endFlag, ref FrameEvents ev)
        {
            if (value && !active) { active = true;  ev |= startFlag; }
            else if (!value && active) { active = false; ev |= endFlag; }
        }
    }
}
