// Recursive least squares, used to learn how far a laggy force signal will move
// before the next one arrives.
//
// The problem it solves: a replayed telemetry signal is a fixed amount behind
// (one frame, for iRacing), and the amount it will move during that time depends
// on the car's steering rack, caster, tyre and speed, and on the wheel's own
// inertia and friction. A hand-set constant is therefore right in one condition
// and wrong in every other, which is a knob the user ends up re-tuning per car
// and never quite settling.
//
// RLS re-derives that relationship continuously instead. Each step it predicts,
// sees what actually happened, and nudges its coefficients toward whatever would
// have been correct. The size of each nudge is scaled by how uncertain it
// currently is about that coefficient, which is what the covariance tracks:
// a coefficient it has good evidence for barely moves, one it does not moves a
// lot. A forgetting factor decays old evidence so it keeps tracking rather than
// converging once and going deaf.
//
// CLOSED-LOOP HAZARD, and the reason the guards below are not optional. This
// runs inside a feedback loop: the prediction moves the wheel, the wheel's
// movement is one of the inputs, so a self-tuning gain can begin fitting its own
// influence rather than the car's physics. Control theory calls that closed-loop
// identification bias and the symptom is a predictor that slowly winds itself up
// until the wheel feels wrong. Three defences, all cheap:
//   1. A conservative forgetting factor, so no single stretch dominates.
//   2. A ceiling on the covariance, so uncertainty cannot blow up and hand a
//      later sample enormous authority (classic RLS windup).
//   3. A hard clamp on the output, applied by the caller, so even a bad fit
//      cannot produce a large force.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Online linear predictor: y = theta . x, with theta learned by
    /// recursive least squares. Not thread-safe by design; the FFB path owns one
    /// instance and drives it from a single thread.</summary>
    public sealed class RlsPredictor
    {
        private readonly int _n;
        private readonly double[] _theta;
        private readonly double[,] _p;
        private readonly double[] _k;     // gain, preallocated: this runs per frame
        private readonly double[] _px;    // P * x, preallocated

        private readonly double _lambda;
        private readonly double _pInit;
        private readonly double _pMax;

        /// <summary>Coefficient i. Diagnostics and tests only.</summary>
        public double Theta(int i) { return (i >= 0 && i < _n) ? _theta[i] : 0.0; }

        /// <summary>How many updates have been absorbed. The caller uses this to
        /// hold the prediction at zero until there is enough evidence to trust
        /// it, rather than acting on a fit made of two samples.</summary>
        public long Samples { get; private set; }

        /// <param name="n">Regressor count.</param>
        /// <param name="lambda">Forgetting factor in (0, 1]. Lower adapts faster
        /// and is noisier. 0.995 at 60 Hz weights roughly the last three seconds,
        /// which tracks a corner without chasing individual bumps.</param>
        /// <param name="pInit">Initial covariance. Large means "I know nothing",
        /// so the first samples move the fit a long way.</param>
        /// <param name="pMax">Ceiling on the covariance diagonal, the windup guard.</param>
        /// <param name="theta0">Starting coefficients. The point is to begin at a
        /// sensible NULL HYPOTHESIS rather than at zero, so the very first
        /// predictions are already reasonable instead of merely harmless. For a
        /// predictor whose regressors include the current value, seeding that
        /// coefficient to 1 starts it at "nothing will change", which is the
        /// right thing to believe before any evidence arrives.</param>
        public RlsPredictor(int n, double lambda = 0.995, double pInit = 100.0, double pMax = 1.0e4,
                            double[] theta0 = null)
        {
            if (n < 1) n = 1;
            _n = n;
            _theta = new double[n];
            _theta0 = new double[n];
            if (theta0 != null)
                for (int i = 0; i < n && i < theta0.Length; i++)
                    if (!IsBad(theta0[i])) _theta0[i] = theta0[i];
            _p = new double[n, n];
            _k = new double[n];
            _px = new double[n];
            _lambda = (lambda > 0.10 && lambda <= 1.0) ? lambda : 0.995;
            _pInit = pInit > 0.0 ? pInit : 100.0;
            _pMax = pMax > _pInit ? pMax : _pInit * 100.0;
            Reset();
        }

        private readonly double[] _theta0;

        /// <summary>Forget everything. Called on a car change, a session change,
        /// or any gap in telemetry: the old fit describes a different vehicle and
        /// carrying it over is worse than starting again.</summary>
        public void Reset()
        {
            Samples = 0;
            for (int i = 0; i < _n; i++)
            {
                _theta[i] = _theta0[i];
                for (int j = 0; j < _n; j++) _p[i, j] = (i == j) ? _pInit : 0.0;
            }
        }

        /// <summary>Predict from the current regressors. Returns 0 before any
        /// evidence exists, which is the safe answer rather than a guess.</summary>
        public double Predict(double[] x)
        {
            if (x == null || x.Length < _n) return 0.0;
            double y = 0.0;
            for (int i = 0; i < _n; i++) y += _theta[i] * x[i];
            return IsBad(y) ? 0.0 : y;
        }

        /// <summary>Absorb one observation: these regressors produced this value.
        /// Ignores anything non-finite rather than poisoning the fit with it.</summary>
        public void Update(double[] x, double y)
        {
            if (x == null || x.Length < _n || IsBad(y)) return;
            for (int i = 0; i < _n; i++) if (IsBad(x[i])) return;

            // px = P x, and xPx = x' P x
            double xpx = 0.0;
            for (int i = 0; i < _n; i++)
            {
                double s = 0.0;
                for (int j = 0; j < _n; j++) s += _p[i, j] * x[j];
                _px[i] = s;
                xpx += x[i] * s;
            }

            // Denominator guard. With tiny regressors this approaches lambda,
            // and letting it approach zero would produce an enormous gain from a
            // sample carrying almost no information.
            double den = _lambda + xpx;
            if (den < 1.0e-9 || IsBad(den)) return;

            for (int i = 0; i < _n; i++) _k[i] = _px[i] / den;

            // Innovation: how wrong the current fit was about this observation.
            double err = y - Predict(x);
            if (IsBad(err)) return;
            for (int i = 0; i < _n; i++)
            {
                _theta[i] += _k[i] * err;
                if (IsBad(_theta[i])) { Reset(); return; }
            }

            // P = (P - k (P x)') / lambda, then symmetrise. The explicit
            // symmetrisation matters: in floating point the update drifts
            // asymmetric over minutes and an asymmetric covariance eventually
            // produces a negative variance, which is meaningless and unstable.
            for (int i = 0; i < _n; i++)
                for (int j = 0; j < _n; j++)
                    _p[i, j] = (_p[i, j] - _k[i] * _px[j]) / _lambda;

            for (int i = 0; i < _n; i++)
            {
                for (int j = i + 1; j < _n; j++)
                {
                    double avg = 0.5 * (_p[i, j] + _p[j, i]);
                    _p[i, j] = avg;
                    _p[j, i] = avg;
                }
                // Windup guard, both directions. A diagonal that runs away hands
                // a later sample unlimited authority; one that goes negative is
                // numerically impossible and means the fit has broken.
                if (IsBad(_p[i, i]) || _p[i, i] <= 0.0) { Reset(); return; }
                if (_p[i, i] > _pMax) _p[i, i] = _pMax;
            }

            if (Samples < long.MaxValue) Samples++;
        }

        private static bool IsBad(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v);
        }
    }
}
