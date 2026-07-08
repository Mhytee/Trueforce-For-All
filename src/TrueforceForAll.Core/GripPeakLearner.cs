// Per-car grip-limit auto-calibration (haptic-engine phase 5, layer 11).
//
// Forza's combined-slip metric nominally reads 1.0 at the grip limit, but
// the actual ceiling varies car to car: a stiff GT car may top out at 0.85
// (the wheel never reaches the drop cue) while a loose classic sustains 1.4
// (the wheel lives saturated). This learner watches the front-axle grip
// channel while the player actually corners and converges on where THIS
// car's metric really peaks, so Mode B can normalize utilization per car
// with zero user action.
//
// Estimator: an asymmetric-time-constant EMA over qualifying samples
// (moving + cornering). Rises fast (RiseTauSec of cornering time) so a lap
// of pushing finds the ceiling; falls slow (FallTauSec) so straights and
// cruising don't erode it. Each input is clamped to SpikeClamp x the
// current estimate, so a spin or crash — seconds of absurd slip — can only
// nudge the peak a few percent, and the slow fall heals even that.
//
// Application is confidence-faded: EffectivePeak starts at exactly 1.0
// (no behavior change) and blends toward the learned peak as near-limit
// seat time accumulates, full trust after FullConfidenceSec. A player who
// never pushes the car simply keeps the nominal calibration.
//
// Pure model: no clocks, no telemetry types, no allocation after
// construction. The caller ticks it from the telemetry thread and persists
// Peak / QualifyingSec per car via Export/Restore.

using System;

namespace TrueforceForAll.Core
{
    public sealed class GripPeakLearner
    {
        /// <summary>Below this speed nothing is learned: parked and
        /// crawling cars emit grip noise, not grip information.</summary>
        public double MinSpeedKmh { get; set; } = 30.0;

        /// <summary>Pre-filter time constant (ms) on the raw grip channel.
        /// Melts single-frame telemetry spikes before they reach the
        /// estimator.</summary>
        public double PreEmaMs { get; set; } = 200.0;

        /// <summary>Utilization (after pre-filter) that counts as
        /// "cornering". Only cornering time teaches the ceiling; straights
        /// neither raise nor erode it.</summary>
        public double CorneringFloor { get; set; } = 0.35;

        /// <summary>Rise time constant, in seconds of cornering time. Small
        /// so one hard lap converges.</summary>
        public double RiseTauSec { get; set; } = 5.0;

        /// <summary>Fall time constant, in seconds of cornering time. Large
        /// so the estimate sits near the top of the cornering distribution
        /// instead of its mean.</summary>
        public double FallTauSec { get; set; } = 45.0;

        /// <summary>Max multiple of the current estimate a single input may
        /// claim. Caps how hard a sustained spin can drag the peak up.</summary>
        public double SpikeClamp { get; set; } = 1.2;

        /// <summary>Near-limit seconds (utilization above 0.7 x peak) for
        /// full confidence.</summary>
        public double FullConfidenceSec { get; set; } = 60.0;

        /// <summary>Hard sanity range for the learned peak.</summary>
        public double PeakMin { get; set; } = 0.6;
        public double PeakMax { get; set; } = 2.0;

        /// <summary>Learned ceiling of the grip metric. Starts at the
        /// nominal 1.0 and is only trusted via <see cref="EffectivePeak"/>.</summary>
        public double Peak { get; private set; } = 1.0;

        /// <summary>Accumulated near-limit seconds (drives confidence).</summary>
        public double QualifyingSec { get; private set; }

        public double Confidence =>
            Math.Min(QualifyingSec / Math.Max(1.0, FullConfidenceSec), 1.0);

        /// <summary>What the caller divides utilization by: 1.0 until the
        /// learner has earned trust, then the learned peak. Never outside
        /// [PeakMin, PeakMax].</summary>
        public double EffectivePeak
        {
            get
            {
                double p = Math.Min(Math.Max(Peak, PeakMin), PeakMax);
                return 1.0 + (p - 1.0) * Confidence;
            }
        }

        private double _preEma;
        private bool   _seeded;

        /// <summary>Feed one telemetry sample. u = raw front-axle combined
        /// slip (grip metric), dtMs = time since the previous sample.</summary>
        public void Tick(double u, double speedKmh, double dtMs)
        {
            if (double.IsNaN(u) || double.IsInfinity(u) || u < 0.0) return;
            if (dtMs <= 0.0) return;
            if (dtMs > 100.0) dtMs = 100.0;
            if (speedKmh < MinSpeedKmh) { _seeded = false; return; }

            if (!_seeded) { _preEma = Math.Min(u, Peak); _seeded = true; }
            double preAlpha = 1.0 - Math.Exp(-dtMs / Math.Max(10.0, PreEmaMs));
            _preEma += (u - _preEma) * preAlpha;

            if (_preEma < CorneringFloor) return;   // straights teach nothing

            double dtSec = dtMs / 1000.0;
            double x = Math.Min(_preEma, Peak * SpikeClamp);
            double tau = x > Peak ? RiseTauSec : FallTauSec;
            Peak += (x - Peak) * (1.0 - Math.Exp(-dtSec / tau));
            if (Peak < PeakMin) Peak = PeakMin;
            else if (Peak > PeakMax) Peak = PeakMax;

            if (_preEma > 0.7 * Peak)
                QualifyingSec = Math.Min(QualifyingSec + dtSec, 10.0 * FullConfidenceSec);
        }

        /// <summary>Restore persisted state (car change / session start).
        /// Out-of-range values fall back to the fresh-car defaults rather
        /// than trusting a corrupt settings file.</summary>
        public void Restore(double peak, double qualifyingSec)
        {
            Peak = (double.IsNaN(peak) || peak < PeakMin || peak > PeakMax) ? 1.0 : peak;
            QualifyingSec = (double.IsNaN(qualifyingSec) || qualifyingSec < 0.0)
                ? 0.0
                : Math.Min(qualifyingSec, 10.0 * FullConfidenceSec);
            _seeded = false;
        }

        /// <summary>Back to fresh-car state.</summary>
        public void Reset() => Restore(1.0, 0.0);
    }
}
