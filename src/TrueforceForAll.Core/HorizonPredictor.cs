// N-step-ahead predictor over an RlsPredictor.
//
// The problem this exists to solve is a timing one, not a maths one. To learn
// "where will this signal be N steps from now", you cannot pair the current
// inputs with the current answer: at the moment you make a prediction, the truth
// it should be judged against has not happened yet. It arrives N steps later, by
// which time the inputs that produced it are gone.
//
// So the inputs are QUEUED. Each step enqueues the regressors used, and once a
// queued entry has aged the full horizon, the value that has now arrived IS the
// truth for it, and the pair goes to the fit. That is the entire trick.
//
// Why the horizon is a parameter rather than fixed at one: how far behind a
// replayed signal really runs depends on the transport. iRacing's own frame is
// one step, but a hop through another process adds more, and until that is
// measured the honest thing is to make the reach adjustable rather than to
// assume it is exactly one.
//
// The pending queue is the standard way to train a predictor against truth it
// cannot see yet: hold each regressor row until the sample it was predicting
// actually arrives, then fit that pair. The regressor set and the guards here
// are our own.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Predicts a value N steps ahead, learning from observations that
    /// only become checkable N steps after the fact. Single-threaded by design.</summary>
    public sealed class HorizonPredictor
    {
        private readonly RlsPredictor _rls;
        private readonly int _horizon;
        private readonly int _n;

        private readonly double[][] _pending;   // regressors awaiting their truth
        private int _head;
        private int _count;

        public HorizonPredictor(int regressors, int horizon, double lambda = 0.995,
                                double pInit = 100.0, double[] theta0 = null)
        {
            _n = Math.Max(1, regressors);
            _horizon = Math.Max(1, horizon);
            _rls = new RlsPredictor(_n, lambda, pInit, theta0: theta0);
            // Horizon + 2 so a step can enqueue before the oldest matures
            // without ever overwriting an entry still waiting for its truth.
            _pending = new double[_horizon + 2][];
            for (int i = 0; i < _pending.Length; i++) _pending[i] = new double[_n];
        }

        /// <summary>Updates absorbed so far. Zero until the first entry has aged
        /// the full horizon, which is why callers must not act on the output
        /// before this has grown.</summary>
        public long Samples { get { return _rls.Samples; } }

        public double Theta(int i) { return _rls.Theta(i); }

        public void Reset()
        {
            _rls.Reset();
            _head = 0;
            _count = 0;
        }

        /// <summary>One step: learn from whatever has now matured, then predict.
        /// <paramref name="yNow"/> is the value that has just arrived, which is
        /// simultaneously the TRUTH for the entry enqueued a horizon ago and part
        /// of the INPUT for the prediction being made now.</summary>
        public double Step(double[] x, double yNow)
        {
            if (x == null || x.Length < _n || double.IsNaN(yNow) || double.IsInfinity(yNow))
                return 0.0;

            // ENQUEUE BEFORE MATURING, and the order is the whole correctness
            // argument. Counting the current step means the entry from exactly
            // <horizon> steps ago becomes the oldest at precisely the moment its
            // truth arrives. Checking maturity first instead makes every pair a
            // step too old, which still trains happily and still produces
            // plausible numbers, just ones that answer the wrong question.
            int tail = (_head + _count) % _pending.Length;
            var slot = _pending[tail];
            for (int i = 0; i < _n; i++) slot[i] = x[i];
            if (_count < _pending.Length) _count++;
            else _head = (_head + 1) % _pending.Length;   // cannot happen, but never corrupt the queue

            // The entry from a horizon ago is now checkable, and yNow is exactly
            // what it was trying to predict.
            if (_count > _horizon)
            {
                var matured = _pending[_head];
                _head = (_head + 1) % _pending.Length;
                _count--;
                _rls.Update(matured, yNow);
            }

            return _rls.Predict(x);
        }
    }
}
