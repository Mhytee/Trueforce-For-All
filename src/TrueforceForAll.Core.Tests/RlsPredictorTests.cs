using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The predictor sits inside a force loop, so the properties worth pinning
    // are not "is the fit accurate" but "can it ever misbehave": does it stay
    // silent before it knows anything, does it actually converge, does it track
    // a change, and can bad input or a long run break it.
    public class RlsPredictorTests
    {
        [Fact]
        public void PredictsZeroBeforeAnyEvidence()
        {
            var p = new RlsPredictor(3);
            Assert.Equal(0, p.Samples);
            Assert.Equal(0.0, p.Predict(new[] { 1.0, 2.0, 1.0 }));
        }

        [Fact]
        public void LearnsAKnownLinearRelationship()
        {
            // y = 2*x0 - 0.5*x1 + 0.25
            var p = new RlsPredictor(3);
            var rng = new Random(1234);
            for (int i = 0; i < 500; i++)
            {
                double a = rng.NextDouble() * 2.0 - 1.0;
                double b = rng.NextDouble() * 2.0 - 1.0;
                var x = new[] { a, b, 1.0 };
                p.Update(x, 2.0 * a - 0.5 * b + 0.25);
            }
            Assert.Equal(2.0,  p.Theta(0), 3);
            Assert.Equal(-0.5, p.Theta(1), 3);
            Assert.Equal(0.25, p.Theta(2), 3);
        }

        [Fact]
        public void TracksARelationshipThatChanges()
        {
            // The forgetting factor exists so a new car or a new speed re-tunes
            // it rather than being outvoted by everything that came before.
            var p = new RlsPredictor(3, lambda: 0.98);
            var rng = new Random(99);
            for (int i = 0; i < 400; i++)
            {
                double a = rng.NextDouble() * 2.0 - 1.0;
                p.Update(new[] { a, 0.0, 1.0 }, 3.0 * a);
            }
            Assert.Equal(3.0, p.Theta(0), 2);

            for (int i = 0; i < 400; i++)
            {
                double a = rng.NextDouble() * 2.0 - 1.0;
                p.Update(new[] { a, 0.0, 1.0 }, -1.0 * a);
            }
            Assert.Equal(-1.0, p.Theta(0), 2);
        }

        [Fact]
        public void IgnoresNonFiniteInputInsteadOfPoisoningTheFit()
        {
            var p = new RlsPredictor(3);
            for (int i = 0; i < 200; i++)
                p.Update(new[] { 0.5, 0.0, 1.0 }, 1.0);
            double before = p.Theta(0);
            long n = p.Samples;

            p.Update(new[] { double.NaN, 0.0, 1.0 }, 1.0);
            p.Update(new[] { 0.5, 0.0, 1.0 }, double.PositiveInfinity);
            p.Update(null, 1.0);

            Assert.Equal(n, p.Samples);            // nothing absorbed
            Assert.Equal(before, p.Theta(0), 9);   // fit untouched
            Assert.False(double.IsNaN(p.Predict(new[] { 0.5, 0.0, 1.0 })));
        }

        [Fact]
        public void StaysFiniteOverALongRunOfConstantInput()
        {
            // Constant regressors are the classic covariance-windup case: no new
            // information, but a forgetting factor keeps inflating uncertainty.
            // Half an hour at 60 Hz.
            var p = new RlsPredictor(3);
            for (int i = 0; i < 108000; i++)
                p.Update(new[] { 1.0, 1.0, 1.0 }, 1.0);

            double y = p.Predict(new[] { 1.0, 1.0, 1.0 });
            Assert.False(double.IsNaN(y));
            Assert.False(double.IsInfinity(y));
            Assert.True(Math.Abs(y) < 100.0, "prediction ran away: " + y);
        }

        [Fact]
        public void ResetForgetsEverything()
        {
            var p = new RlsPredictor(3);
            for (int i = 0; i < 100; i++)
                p.Update(new[] { 1.0, 0.0, 1.0 }, 5.0);
            Assert.NotEqual(0.0, p.Predict(new[] { 1.0, 0.0, 1.0 }));

            p.Reset();
            Assert.Equal(0, p.Samples);
            Assert.Equal(0.0, p.Predict(new[] { 1.0, 0.0, 1.0 }));
        }
    }
}
