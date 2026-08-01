// Per-car braking-grip learner: finds the front longitudinal slip ratio at the
// braking grip knee (the friction circle's longitudinal radius) so Mode B's
// friction-circle / lockup laws lighten the wheel at each car's REAL braking
// limit instead of a hand-set global constant (issue #38 follow-up).
//
// Why not reuse the lateral GripPeakLearner shape (top of the slip distribution):
// braking slip does not behave like cornering slip. A locked wheel sits at slip
// ratio ~1.0 indefinitely, well PAST the grip limit, so the top of the braking
// distribution drifts toward the lockup value, not the knee. So this learns the
// slip ratio AT PEAK DECELERATION instead: the grip limit is where braking force
// (deceleration) peaks; brake past the knee and the tyre slides, so decel DROPS
// while slip climbs. Within a hard-braking event we track the running peak decel
// and the slip ratio at that instant, and on event end fold that slip-at-peak
// into an EMA. The lockup tail is excluded for free (its lower decel never beats
// the event peak).
//
// Two robustness lessons from the first on-wheel logs, baked in here:
//   * Impact/kerb deceleration spikes (2+ g, not braking) polluted the estimate.
//     The raw decel is capped to a physical braking max and pre-smoothed, so a
//     one-frame crash can't register as braking.
//   * Gating on a fraction of the car's best-ever decel froze learning after one
//     hard stop (the bar rose above what normal braking reaches). Instead an
//     ABSOLUTE hard-braking floor admits an event, and its contribution is
//     WEIGHTED by how hard it was (toward a full-weight decel), so the knee is
//     learned from the hardest braking without a fragile global-max gate.
//
// Pure model: no clocks, no telemetry types, no allocation after construction.
// Applied confidence-faded from a caller-supplied default, so a car with no hard
// braking seat time simply keeps the default calibration.

using System;

namespace TrueforceForAll.Core
{
    public sealed class BrakingGripLearner
    {
        /// <summary>Below this speed nothing is learned (parked/crawl slip is noise).</summary>
        public double MinSpeedKmh { get; set; } = 40.0;

        /// <summary>Deceleration (m/s^2) below which no event forms. Low, so a
        /// stop that starts soft and builds to hard is one event.</summary>
        public double MinDecelMs2 { get; set; } = 3.0;

        /// <summary>Deceleration (m/s^2) an event must PEAK at to teach at all:
        /// only genuinely hard braking has its slip near the knee. Below this the
        /// tyre is not near its limit and its slip sits below the knee, so it
        /// would bias the radius low.</summary>
        public double MinLearnDecelMs2 { get; set; } = 7.0;

        /// <summary>Deceleration (m/s^2) at which an event contributes full
        /// weight; between the floor and here the weight ramps 0..1, so the knee
        /// is learned mostly from the hardest stops without a global-max gate.</summary>
        public double FullWeightDecelMs2 { get; set; } = 11.0;

        /// <summary>Physical ceiling for braking deceleration (m/s^2). Anything
        /// above this is a crash / kerb impact, not braking, and is capped before
        /// it can reach the estimate.</summary>
        public double MaxRealBrakeMs2 { get; set; } = 20.0;

        /// <summary>Pre-filter time constant (ms) on the decel: melts
        /// single-frame spikes so only sustained braking registers.</summary>
        public double DecelPreEmaMs { get; set; } = 120.0;

        /// <summary>Pre-filter time constant (ms) on the slip ratio, so the
        /// slip-at-peak is a short average, not a single-frame value that a
        /// transient could land on.</summary>
        public double SlipPreEmaMs { get; set; } = 80.0;

        /// <summary>Lateral acceleration (m/s^2) above which the tyre is spending
        /// grip sideways, so the braking slip stops reading the pure longitudinal
        /// limit. Keeps learning to straight-line braking.</summary>
        public double MaxLateralMs2 { get; set; } = 4.0;

        /// <summary>Fraction of the event's running peak decel that counts as
        /// "near the knee" for confidence accounting.</summary>
        public double NearPeakFrac { get; set; } = 0.9;

        /// <summary>Per-event learning rate for the limit-slip EMA (scaled by the
        /// event's intensity weight).</summary>
        public double EventAlpha { get; set; } = 0.3;

        /// <summary>Near-knee braking seconds for full confidence.</summary>
        public double FullConfidenceSec { get; set; } = 12.0;

        /// <summary>Hard sanity range for the learned limit slip.</summary>
        public double LimitSlipMin { get; set; } = 0.3;
        public double LimitSlipMax { get; set; } = 2.0;

        /// <summary>Value the learner fades toward from (the caller's manual /
        /// default braking-grip point); set by the caller so turning the learner
        /// on eases from the value the wheel already used. float, so the cross-
        /// thread write (UI) / read (telemetry) stays atomic like the sibling
        /// Mode B force params.</summary>
        public float DefaultLimitSlip { get; set; } = 0.8f;

        /// <summary>Learned slip ratio at the braking grip knee.</summary>
        public double LimitSlip { get; private set; } = 0.8;

        /// <summary>Learned peak braking deceleration (m/s^2), diagnostic only,
        /// from the capped + smoothed signal so impacts do not inflate it.</summary>
        public double PeakDecel { get; private set; }

        /// <summary>Accumulated (weighted) near-knee braking seconds.</summary>
        public double QualifyingSec { get; private set; }

        public double Confidence =>
            Math.Min(QualifyingSec / Math.Max(1.0, FullConfidenceSec), 1.0);

        /// <summary>Confidence-faded limit slip: the caller default until seat
        /// time is earned, then the learned knee. Never outside the sanity range.</summary>
        public double EffectiveLimitSlip
        {
            get
            {
                double l = Math.Min(Math.Max(LimitSlip, LimitSlipMin), LimitSlipMax);
                return DefaultLimitSlip + (l - DefaultLimitSlip) * Confidence;
            }
        }

        /// <summary>True while inside a braking event (diagnostic).</summary>
        public bool InEvent => _inEvent;

        private double _decelEma;
        private double _slipEma;
        private bool   _inEvent;
        private double _eventPeakDecel;
        private double _slipAtPeak;
        private double _eventNearSec;

        /// <summary>Feed one telemetry sample.
        /// <paramref name="frontSlipRatioAbs"/>: worst front |slip ratio|.
        /// <paramref name="decelMs2"/>: deceleration magnitude, i.e. max(0, -a_long).
        /// <paramref name="lateralMs2"/>: |lateral acceleration|.</summary>
        public void Tick(double frontSlipRatioAbs, double decelMs2,
                         double speedKmh, double lateralMs2, double dtMs)
        {
            if (double.IsNaN(frontSlipRatioAbs) || double.IsNaN(decelMs2)
                || double.IsNaN(lateralMs2) || double.IsNaN(speedKmh) || double.IsNaN(dtMs)) return;
            if (dtMs <= 0.0) return;
            if (dtMs > 100.0) dtMs = 100.0;
            double dtSec = dtMs / 1000.0;

            // Above the physical braking ceiling it is a crash / kerb impact,
            // not braking: ignore the frame entirely so it can't touch the decel
            // or slip filters, the event peak, or the estimate.
            if (decelMs2 > MaxRealBrakeMs2) return;
            double dRaw = decelMs2 < 0.0 ? 0.0 : decelMs2;
            double preD = 1.0 - Math.Exp(-dtMs / Math.Max(10.0, DecelPreEmaMs));
            double preS = 1.0 - Math.Exp(-dtMs / Math.Max(10.0, SlipPreEmaMs));
            _decelEma += (dRaw - _decelEma) * preD;
            _slipEma  += ((frontSlipRatioAbs < 0.0 ? 0.0 : frontSlipRatioAbs) - _slipEma) * preS;
            double d = _decelEma;

            bool braking = speedKmh >= MinSpeedKmh
                        && d >= MinDecelMs2
                        && lateralMs2 <= MaxLateralMs2;

            if (braking)
            {
                _inEvent = true;
                if (d > _eventPeakDecel)
                {
                    _eventPeakDecel = d;
                    _slipAtPeak     = _slipEma;   // short-averaged slip at the hardest point so far
                }
                if (d >= NearPeakFrac * _eventPeakDecel)
                    _eventNearSec += dtSec;
                if (d > PeakDecel) PeakDecel = d;
                else PeakDecel += (d - PeakDecel) * (1.0 - Math.Exp(-dtSec / 60.0));
            }
            else if (_inEvent)
            {
                // Event ended: fold the slip-at-peak-decel in, but only from
                // near-limit braking (absolute floor), weighted by how hard the
                // event was so the knee is learned from the hardest stops. The
                // lockup tail never reaches here: its lower decel never beat the
                // event peak, so _slipAtPeak stayed at the knee.
                if (_eventPeakDecel >= MinLearnDecelMs2)
                {
                    double span = Math.Max(0.1, FullWeightDecelMs2 - MinLearnDecelMs2);
                    double w = (_eventPeakDecel - MinLearnDecelMs2) / span;
                    if (w > 1.0) w = 1.0; else if (w < 0.0) w = 0.0;
                    double s = Math.Min(Math.Max(_slipAtPeak, LimitSlipMin), LimitSlipMax);
                    LimitSlip += (s - LimitSlip) * EventAlpha * w;
                    QualifyingSec = Math.Min(QualifyingSec + _eventNearSec * w, 10.0 * FullConfidenceSec);
                }
                _inEvent = false;
                _eventPeakDecel = 0.0;
                _slipAtPeak = 0.0;
                _eventNearSec = 0.0;
            }
        }

        /// <summary>Restore persisted state. Out-of-range values fall back to the
        /// default rather than trusting a corrupt settings file.</summary>
        public void Restore(double limitSlip, double qualifyingSec)
        {
            LimitSlip = (double.IsNaN(limitSlip) || limitSlip < LimitSlipMin || limitSlip > LimitSlipMax)
                ? DefaultLimitSlip : limitSlip;
            QualifyingSec = (double.IsNaN(qualifyingSec) || qualifyingSec < 0.0)
                ? 0.0 : Math.Min(qualifyingSec, 10.0 * FullConfidenceSec);
            _decelEma = 0.0; _slipEma = 0.0; _inEvent = false; _eventPeakDecel = 0.0; _slipAtPeak = 0.0; _eventNearSec = 0.0;
        }

        /// <summary>Back to fresh-car state (fades from the current default).</summary>
        public void Reset()
        {
            LimitSlip = DefaultLimitSlip;
            PeakDecel = 0.0;
            QualifyingSec = 0.0;
            _decelEma = 0.0; _slipEma = 0.0; _inEvent = false; _eventPeakDecel = 0.0; _slipAtPeak = 0.0; _eventNearSec = 0.0;
        }
    }
}
