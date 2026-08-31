using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The horizon queue is pure timing, and timing bugs here are invisible on a
    // wheel: pairing inputs against the WRONG truth still produces a plausible
    // number, just a worthless one. So these pin the timing rather than the fit.
    public class HorizonPredictorTests
    {
        [Fact]
        public void LearnsNothingUntilTheFirstEntryMatures()
        {
            // With a horizon of 3, no observation is checkable until a fourth
            // step has arrived. Acting before that means acting on nothing.
            var p = new HorizonPredictor(2, horizon: 3);
            for (int i = 0; i < 3; i++)
            {
                p.Step(new[] { 1.0, i * 0.1 }, i * 0.1);
                Assert.Equal(0, p.Samples);
            }
            p.Step(new[] { 1.0, 0.3 }, 0.3);
            Assert.True(p.Samples > 0);
        }

        [Fact]
        public void PairsInputsWithTheTruthThatArrivesAHorizonLater()
        {
            // Signal climbs by exactly 1 per step. With horizon 2 the truth for
            // any step is that step's value plus 2, so a predictor seeded at
            // persistence must learn a constant offset of +2, not +1.
            var p = new HorizonPredictor(2, horizon: 2, lambda: 0.999,
                                         theta0: new[] { 0.0, 1.0 });
            double y = 0.0;
            for (int i = 0; i < 600; i++)
            {
                p.Step(new[] { 1.0, y }, y);
                y += 1.0;
            }
            // theta[0] is the constant term, theta[1] the coefficient on y.
            Assert.Equal(1.0, p.Theta(1), 2);
            Assert.Equal(2.0, p.Theta(0), 1);
        }

        [Fact]
        public void StartsAtPersistenceBeforeAnyEvidence()
        {
            // Seeded so the first prediction is "it will be what it is", which
            // is the right belief with no data, rather than "it will be zero".
            var p = new HorizonPredictor(2, horizon: 1, theta0: new[] { 0.0, 1.0 });
            double first = p.Step(new[] { 1.0, 7.5 }, 7.5);
            Assert.Equal(7.5, first, 6);
        }

        [Fact]
        public void ResetClearsTheQueueAsWellAsTheFit()
        {
            // A stale queue entry would be matured against a truth from a
            // different car, teaching it something that never happened.
            var p = new HorizonPredictor(2, horizon: 2);
            for (int i = 0; i < 50; i++) p.Step(new[] { 1.0, i * 1.0 }, i * 1.0);
            Assert.True(p.Samples > 0);

            p.Reset();
            Assert.Equal(0, p.Samples);
            p.Step(new[] { 1.0, 1.0 }, 1.0);
            p.Step(new[] { 1.0, 2.0 }, 2.0);
            Assert.Equal(0, p.Samples);   // queue really was emptied
        }

        [Fact]
        public void SurvivesNonFiniteInputWithoutCorruptingTheQueue()
        {
            var p = new HorizonPredictor(2, horizon: 2);
            for (int i = 0; i < 100; i++) p.Step(new[] { 1.0, i * 1.0 }, i * 1.0);
            long n = p.Samples;

            p.Step(new[] { 1.0, double.NaN }, 5.0);
            p.Step(new[] { 1.0, 5.0 }, double.PositiveInfinity);
            p.Step(null, 5.0);

            double y = p.Step(new[] { 1.0, 101.0 }, 101.0);
            Assert.False(double.IsNaN(y));
            Assert.False(double.IsInfinity(y));
            Assert.True(p.Samples >= n);
        }
    }
}
