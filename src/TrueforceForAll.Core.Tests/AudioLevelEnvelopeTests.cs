// Turning audio peaks into a bar that uses the bar.
//
// Every test here is a property that a real capture disproved of some earlier
// design, so they are regression tests for shipped behaviour rather than
// speculation. Two 30 s captures from the owner's machine, minutes apart,
// spanned 13 dB and 3.6 dB of program range; the first two designs each worked
// on one and failed on the other.

using System;
using System.Collections.Generic;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    public class AudioLevelEnvelopeTests
    {
        private const double Poll = 0.031;   // what Thread.Sleep(16) really costs

        private static double Peak(double db) => Math.Pow(10.0, db / 20.0);

        /// <summary>Run a program through the envelope and report which of the
        /// eleven LED levels it lit, in the order a ten-step strip would.</summary>
        private static SortedSet<int> LevelsFor(IEnumerable<double> programDb)
        {
            var env = new AudioLevelEnvelope();
            var seen = new SortedSet<int>();
            foreach (double db in programDb)
                seen.Add((int)Math.Floor(env.Push(Peak(db), Poll) * 10 + 0.5));
            return seen;
        }

        /// <summary>A program sweeping smoothly between two levels, sampled at
        /// the meter's real rate for `seconds`.
        ///
        /// Smoothly and not as a square wave, which was the first version and
        /// was a bad fixture: a two-valued input can only ever light two LEDs,
        /// so it says nothing about whether the strip is being used and it
        /// failed a test the envelope was passing.</summary>
        private static IEnumerable<double> Program(double quietDb, double loudDb, double seconds)
        {
            int n = (int)(seconds / Poll);
            for (int i = 0; i < n; i++)
            {
                double phase = (i * Poll) / 2.0;              // one sweep every 2 s
                double t = 1.0 - Math.Abs(2.0 * (phase - Math.Floor(phase)) - 1.0);
                yield return quietDb + t * (loudDb - quietDb);
            }
        }

        [Fact]
        public void AQuietSourceStillFillsTheStrip()
        {
            // The first design's failure: a fixed window under 0 dBFS. This
            // program never rises above -25 dB, which a 24 dB window read as a
            // permanently dark strip.
            var levels = LevelsFor(Program(-28.7, -25.1, 20));
            Assert.Contains(10, levels);
            Assert.True(levels.Count >= 6, $"only {levels.Count} levels used: {string.Join(",", levels)}");
        }

        [Fact]
        public void ASteadySourceDoesNotPinAtFull()
        {
            // The second design's failure: a ceiling that follows the loudest
            // recent peak with an instant attack IS the current sample whenever
            // the signal reaches a new local maximum, so the bar sat at full for
            // 93% of a real capture. The quiet half of this program must reach
            // the bottom half of the strip.
            var env = new AudioLevelEnvelope();
            int atFull = 0, n = 0;
            foreach (double db in Program(-28.7, -25.1, 20))
            {
                double v = env.Push(Peak(db), Poll);
                if (v > 0.95) atFull++;
                n++;
            }
            Assert.True(atFull < n * 0.75, $"bar sat at full for {100.0 * atFull / n:0}% of the program");
        }

        [Fact]
        public void TheSameProgramAtAnyVolumeReadsTheSame()
        {
            // The point of moving both ends: turning the system volume down is
            // not supposed to change the picture, and this is what neither fixed
            // scale could do.
            var loud  = LevelsFor(Program(-22, -9, 20));
            var quiet = LevelsFor(Program(-52, -39, 20));
            Assert.Equal(loud, quiet);
        }

        [Fact]
        public void SilenceReadsEmpty()
        {
            var env = new AudioLevelEnvelope();
            Assert.Equal(0.0, env.Push(0.0, Poll));
            Assert.Equal(0.0, env.Push(-1.0, Poll));
        }

        [Fact]
        public void AGapInTheAudioDoesNotRescaleTheStrip()
        {
            // Silence contributes to neither end, so a pause shorter than the
            // window leaves the scale exactly where it was. The property is
            // that the same sound reads the same either side of the gap, NOT
            // that it reads low: after a gap long enough to empty the window
            // the first sound legitimately defines the scale on its own, which
            // is what ResetForgetsTheProgram pins.
            var withGap = new AudioLevelEnvelope();
            var without = new AudioLevelEnvelope();
            foreach (double db in Program(-28, -25, 10))
            {
                withGap.Push(Peak(db), Poll);
                without.Push(Peak(db), Poll);
            }
            // A second of nothing, well inside the window.
            for (int i = 0; i < 30; i++) Assert.Equal(0.0, withGap.Push(0.0, Poll));

            Assert.Equal(without.Push(Peak(-28), Poll), withGap.Push(Peak(-28), Poll), 6);
        }

        [Fact]
        public void FollowsASongChangeWithinAFewSeconds()
        {
            // The window is the time the strip spends looking wrong after a
            // track change, because a quieter song cannot be recognised until
            // the louder one has aged out. At fifteen seconds the owner noticed
            // the wait; this pins the shorter window that replaced it.
            var env = new AudioLevelEnvelope();
            foreach (double db in Program(-22, -9, 20)) env.Push(Peak(db), Poll);

            double t = 0, best = 0;
            foreach (double db in Program(-40, -27, 30))
            {
                best = Math.Max(best, env.Push(Peak(db), Poll));
                t += Poll;
                if (best >= 0.9) break;
            }
            Assert.True(best >= 0.9 && t < 10.0,
                $"took {t:0.0}s to use the strip again on a quieter track");
        }

        [Fact]
        public void AnEndpointsIdleNoiseFloorIsSilence()
        {
            // The bug an absolute gate exists for, with the real numbers. The
            // owner's Voicemeeter device never reports a true zero: it returns
            // 2.328e-10, or -192.7 dBFS, on every sample while nothing plays.
            // Without the gate the window filled with that one constant value,
            // the envelope scaled itself onto the noise floor as if it were the
            // program, and the whole strip lit up in a silent room.
            var env = new AudioLevelEnvelope();
            for (int i = 0; i < 2000; i++)
                Assert.Equal(0.0, env.Push(2.328e-10, Poll));
        }

        [Fact]
        public void RealAudioAfterThatNoiseFloorIsNotJudgedAgainstIt()
        {
            var env = new AudioLevelEnvelope();
            for (int i = 0; i < 2000; i++) env.Push(2.328e-10, Poll);
            // The gated silence taught it nothing, so this program scales
            // against itself exactly as it would from cold.
            var fresh = new AudioLevelEnvelope();
            double a = 0, b = 0;
            foreach (double db in Program(-28, -25, 10))
            {
                a = env.Push(Peak(db), Poll);
                b = fresh.Push(Peak(db), Poll);
            }
            Assert.Equal(b, a, 6);
        }

        [Fact]
        public void AnUnchangingToneIsNotAmplifiedIntoNoise()
        {
            // With both ends free, a dead-steady tone has hi == lo. The minimum
            // span is what stops that dividing by zero and what stops the strip
            // becoming a display of the last bit of the peak reading.
            var env = new AudioLevelEnvelope();
            double last = 0;
            for (int i = 0; i < 1000; i++) last = env.Push(Peak(-20), Poll);
            Assert.InRange(last, 0.0, 1.0);
            Assert.False(double.IsNaN(last));
        }

        [Fact]
        public void StaysInRangeOnAnythingItIsHanded()
        {
            var env = new AudioLevelEnvelope();
            var rng = new Random(7);
            for (int i = 0; i < 5000; i++)
            {
                double peak = rng.NextDouble() < 0.1 ? 0 : rng.NextDouble() * 1.5;
                double dt = rng.NextDouble() < 0.05 ? 0 : rng.NextDouble() * 0.2;
                double v = env.Push(peak, dt);
                Assert.InRange(v, 0.0, 1.0);
            }
        }

        [Fact]
        public void ResetForgetsTheProgram()
        {
            var env = new AudioLevelEnvelope();
            foreach (double db in Program(-40, -10, 10)) env.Push(Peak(db), Poll);
            env.Reset();
            // A first reading is its own loudest AND quietest, so there is no
            // range to place it in. Centred in the minimum span it sits mid
            // strip and can move either way; hung from the top of that span it
            // would read full, which is how a held note and a noise floor both
            // used to light the whole bar.
            Assert.Equal(0.5, env.Push(Peak(-30), Poll), 6);
        }
    }
}
