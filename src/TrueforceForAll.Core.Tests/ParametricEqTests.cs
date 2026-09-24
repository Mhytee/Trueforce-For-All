using System;
using System.Collections.Generic;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The Trueforce EQ: a biquad bank on the mixed sample stream. These pin
    // the cookbook designs against measured tone power (Goertzel), the state
    // carry across coefficient swaps, and the Mixer placement.
    public class ParametricEqTests
    {
        private const double Fs = 4000.0;

        private static float[] Sine(double hz, int n, float amp = 0.5f)
        {
            var s = new float[n];
            for (int i = 0; i < n; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Fs));
            return s;
        }

        private static float[] Run(ParametricEq eq, float[] input, int batch = 4)
        {
            var outp = (float[])input.Clone();
            var buf = new float[batch];
            for (int off = 0; off < outp.Length; off += batch)
            {
                int n = Math.Min(batch, outp.Length - off);
                Array.Copy(outp, off, buf, 0, n);
                eq.Process(buf, n);
                Array.Copy(buf, 0, outp, off, n);
            }
            return outp;
        }

        /// <summary>Measured gain in dB of a tone through the EQ, over the
        /// steady-state tail (the first second is dropped so filter
        /// settling never counts).</summary>
        private static double MeasuredDb(ParametricEq eq, double hz)
        {
            int n = 4 * 4000;
            var input = Sine(hz, n);
            var output = Run(eq, input);
            int skip = 4000, len = n - skip;
            double pin = Goertzel.Power(input, skip, len, hz, Fs);
            double pout = Goertzel.Power(output, skip, len, hz, Fs);
            return 10.0 * Math.Log10(pout / pin);
        }

        [Fact]
        public void NoBands_IsIdentity()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand>());
            var input = Sine(120, 400);
            var output = Run(eq, input);
            for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i]);
            Assert.Equal(0, eq.ActiveSectionCount);
        }

        [Fact]
        public void FlatFactoryBands_AreBypassed()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(ParametricEq.FactoryBands());
            Assert.Equal(0, eq.ActiveSectionCount);
            Assert.Equal(6, ParametricEq.FactoryBands().Count);
            Assert.Equal(1.0, ParametricEq.DefaultQ);
            var input = Sine(63, 400);
            var output = Run(eq, input);
            for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i]);
        }

        [Fact]
        public void PeakCut_AttenuatesCentre_LeavesFarBandsAlone()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 150, GainDb = -12, Q = 2 } });
            Assert.Equal(1, eq.ActiveSectionCount);

            double at150 = MeasuredDb(eq, 150);
            double at40  = MeasuredDb(eq, 40);
            double at600 = MeasuredDb(eq, 600);
            Assert.True(Math.Abs(at150 - (-12)) < 0.3, $"centre {at150:F2} dB");
            Assert.True(Math.Abs(at40) < 0.5,  $"40 Hz {at40:F2} dB");
            Assert.True(Math.Abs(at600) < 0.5, $"600 Hz {at600:F2} dB");
        }

        [Fact]
        public void PeakBoost_MatchesDesignedGain()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 80, GainDb = 6, Q = 1 } });
            double at80 = MeasuredDb(eq, 80);
            Assert.True(Math.Abs(at80 - 6) < 0.3, $"centre {at80:F2} dB");
        }

        [Fact]
        public void ResponseDb_MatchesMeasurement()
        {
            var eq = new ParametricEq(Fs);
            var bands = new List<EqBand>
            {
                new EqBand { FrequencyHz = 60,  GainDb = -8, Q = 3 },
                new EqBand { FrequencyHz = 300, GainDb = 4,  Q = 0.7 },
                new EqBand { FrequencyHz = 40,  Type = EqBandType.LowShelf, GainDb = -6, Q = 0.7 },
            };
            eq.SetBands(bands);
            foreach (double f in new[] { 20.0, 60.0, 120.0, 300.0, 700.0 })
            {
                double predicted = eq.ResponseDb(f);
                double alsoStatic = ParametricEq.ResponseDb(bands, f, Fs);
                double measured = MeasuredDb(eq, f);
                Assert.True(Math.Abs(predicted - alsoStatic) < 1e-9);
                Assert.True(Math.Abs(predicted - measured) < 0.35, $"{f} Hz: predicted {predicted:F2}, measured {measured:F2}");
            }
        }

        [Fact]
        public void Shelves_MoveTheRightSide()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 100, Type = EqBandType.HighShelf, GainDb = -10, Q = 0.7 } });
            Assert.True(MeasuredDb(eq, 20) > -1.0);
            Assert.True(MeasuredDb(eq, 800) < -9.0);

            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 100, Type = EqBandType.LowShelf, GainDb = -10, Q = 0.7 } });
            Assert.True(MeasuredDb(eq, 15) < -9.0);
            Assert.True(MeasuredDb(eq, 800) > -1.0);
        }

        [Fact]
        public void ShelvesAndCuts_AtSlopeDefaultQ_HaveNoOvershoot()
        {
            // The UI snaps shelves and cuts to SlopeDefaultQ on selection
            // because a bell Q bumps at the corner. Pin that the snapped
            // response is monotonic across the whole band.
            var cases = new[]
            {
                new EqBand { FrequencyHz = 100, Type = EqBandType.LowShelf,  GainDb = -10, Q = ParametricEq.SlopeDefaultQ },
                new EqBand { FrequencyHz = 100, Type = EqBandType.HighShelf, GainDb = -10, Q = ParametricEq.SlopeDefaultQ },
                new EqBand { FrequencyHz = 100, Type = EqBandType.LowShelf,  GainDb = 8,   Q = ParametricEq.SlopeDefaultQ },
                new EqBand { FrequencyHz = 150, Type = EqBandType.LowCut,    Q = ParametricEq.SlopeDefaultQ },
                new EqBand { FrequencyHz = 150, Type = EqBandType.HighCut,   Q = ParametricEq.SlopeDefaultQ },
            };
            foreach (var b in cases)
            {
                double prev = ParametricEq.BandResponseDb(b, 8, Fs);
                int sign = 0;
                for (double f = 9; f <= 1700; f *= 1.02)
                {
                    double cur = ParametricEq.BandResponseDb(b, f, Fs);
                    double d = cur - prev;
                    if (Math.Abs(d) > 1e-6)
                    {
                        int s = Math.Sign(d);
                        Assert.True(sign == 0 || s == sign, $"{b.Type} {b.GainDb} dB reverses slope at {f:F0} Hz");
                        sign = s;
                    }
                    prev = cur;
                }
                // A bell Q on the same shelf DOES bump, which is the point of the snap.
            }
            var bumpy = new EqBand { FrequencyHz = 100, Type = EqBandType.LowShelf, GainDb = -10, Q = 2.0 };
            double flat = ParametricEq.BandResponseDb(bumpy, 1500, Fs);
            double nearCorner = ParametricEq.BandResponseDb(bumpy, 160, Fs);
            Assert.True(nearCorner > flat + 0.5, "a Q 2 shelf should overshoot above the corner");
        }

        [Fact]
        public void Cuts_RollOffPastTheCorner_AndIgnoreGain()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 200, Type = EqBandType.HighCut, GainDb = 12, Q = 0.7071 } });
            Assert.True(Math.Abs(MeasuredDb(eq, 200) - (-3)) < 0.5);
            Assert.True(MeasuredDb(eq, 800) < -20);
            Assert.True(MeasuredDb(eq, 30) > -0.3);

            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 60, Type = EqBandType.LowCut, GainDb = 12, Q = 0.7071 } });
            Assert.True(Math.Abs(MeasuredDb(eq, 60) - (-3)) < 0.5);
            Assert.True(MeasuredDb(eq, 12) < -20);
            Assert.True(MeasuredDb(eq, 400) > -0.3);
        }

        [Fact]
        public void CutSlopes_CascadeButterworth_CornerStaysAt3dB()
        {
            var eq = new ParametricEq(Fs);

            var s24 = new EqBand { FrequencyHz = 200, Type = EqBandType.HighCut, Q = ParametricEq.SlopeDefaultQ, SlopeDbPerOct = 24 };
            Assert.Equal(2, ParametricEq.SectionCount(s24));
            eq.SetBands(new List<EqBand> { s24 });
            Assert.Equal(2, eq.ActiveSectionCount);
            Assert.True(Math.Abs(MeasuredDb(eq, 200) - (-3)) < 0.5, $"24 dB corner {MeasuredDb(eq, 200):F2}");
            Assert.True(MeasuredDb(eq, 800) < -40, $"24 dB two octaves out {MeasuredDb(eq, 800):F2}");
            Assert.True(MeasuredDb(eq, 100) > -0.5, "passband flat");

            var s48 = new EqBand { FrequencyHz = 200, Type = EqBandType.HighCut, Q = ParametricEq.SlopeDefaultQ, SlopeDbPerOct = 48 };
            Assert.Equal(4, ParametricEq.SectionCount(s48));
            eq.SetBands(new List<EqBand> { s48 });
            Assert.True(Math.Abs(MeasuredDb(eq, 200) - (-3)) < 0.5, $"48 dB corner {MeasuredDb(eq, 200):F2}");
            Assert.True(MeasuredDb(eq, 400) < -40, $"48 dB one octave out {MeasuredDb(eq, 400):F2}");
            Assert.True(MeasuredDb(eq, 100) > -0.5, "passband flat");

            // Low cut mirrors, and the predicted curve matches the cascade.
            var lc = new EqBand { FrequencyHz = 100, Type = EqBandType.LowCut, Q = ParametricEq.SlopeDefaultQ, SlopeDbPerOct = 36 };
            eq.SetBands(new List<EqBand> { lc });
            Assert.True(MeasuredDb(eq, 25) < -60, $"36 dB two octaves down {MeasuredDb(eq, 25):F2}");
            foreach (double f in new[] { 50.0, 100.0, 400.0 })
                Assert.True(Math.Abs(ParametricEq.BandResponseDb(lc, f, Fs) - MeasuredDb(eq, f)) < 0.5, $"{f} Hz predicted vs measured");

            // Slope means nothing on a bell; hostile values snap to the menu.
            var bell = new EqBand { FrequencyHz = 100, GainDb = -6, SlopeDbPerOct = 48 };
            Assert.Equal(1, ParametricEq.SectionCount(bell));
            var odd = new EqBand { Type = EqBandType.LowCut, SlopeDbPerOct = 30 };
            ParametricEq.Clamp(odd);
            Assert.Equal(24, odd.SlopeDbPerOct);
        }

        [Fact]
        public void SpectrumTap_KeepsNewestSamples_ChainRunsInOrder()
        {
            var tap = new SpectrumTap();
            var buf = new float[4];
            Assert.Equal(0, tap.CopyLatest(new float[8], 8));
            for (int t = 0; t < 10; t++)
            {
                for (int i = 0; i < 4; i++) buf[i] = t * 4 + i;
                tap.Process(buf, 4);
            }
            Assert.Equal(40, tap.Written);
            var last = new float[8];
            Assert.Equal(8, tap.CopyLatest(last, 8));
            for (int i = 0; i < 8; i++) Assert.Equal(32 + i, last[i]);

            // Chain: tap, then a -12 dB bell, then tap: the two taps disagree
            // at the bell frequency by the cut.
            var pre = new SpectrumTap();
            var post = new SpectrumTap();
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 150, GainDb = -12, Q = 2 } });
            var chain = new ProcessorChain(pre, eq, post);
            var sig = Sine(150, 8000);
            var b4 = new float[4];
            for (int off = 0; off < sig.Length; off += 4)
            {
                Array.Copy(sig, off, b4, 0, 4);
                chain.Process(b4, 4);
            }
            var a = new float[4000]; var b = new float[4000];
            pre.CopyLatest(a, 4000); post.CopyLatest(b, 4000);
            double pa = Goertzel.Power(a, 0, 4000, 150, Fs);
            double pb = Goertzel.Power(b, 0, 4000, 150, Fs);
            Assert.True(Math.Abs(10 * Math.Log10(pb / pa) - (-12)) < 0.3);
        }

        [Fact]
        public void DisabledBand_AndDisabledEq_AreBypass()
        {
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 150, GainDb = -20, Q = 2, Enabled = false } });
            Assert.Equal(0, eq.ActiveSectionCount);
            Assert.True(Math.Abs(MeasuredDb(eq, 150)) < 1e-6);

            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 150, GainDb = -20, Q = 2 } });
            eq.Enabled = false;
            var input = Sine(150, 400);
            var output = Run(eq, input);
            for (int i = 0; i < input.Length; i++) Assert.Equal(input[i], output[i]);
            Assert.True(Math.Abs(eq.ResponseDb(1000)) < 0.5, "a Q 2 bell is nearly flat two and a half octaves out");
            Assert.True(eq.ResponseDb(150) < -19, "curve still reports the edit while bypassed");
        }

        [Fact]
        public void Clamp_SanitisesHostileValues()
        {
            var b = new EqBand { FrequencyHz = double.NaN, GainDb = 99, Q = -5, Type = (EqBandType)77 };
            ParametricEq.Clamp(b);
            Assert.Equal(100.0, b.FrequencyHz);
            Assert.Equal(ParametricEq.MaxGainDb, b.GainDb);
            Assert.Equal(ParametricEq.MinQ, b.Q);
            Assert.Equal(EqBandType.Peak, b.Type);

            var hi = new EqBand { FrequencyHz = 5000, GainDb = -99, Q = 1e9 };
            ParametricEq.Clamp(hi);
            Assert.Equal(ParametricEq.MaxFrequencyHz, hi.FrequencyHz);
            Assert.Equal(ParametricEq.MinGainDb, hi.GainDb);
            Assert.Equal(ParametricEq.MaxQ, hi.Q);

            // A clamped extreme still builds a stable, finite filter.
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { b, hi });
            var output = Run(eq, Sine(100, 8000));
            foreach (float v in output) Assert.False(float.IsNaN(v) || float.IsInfinity(v));
        }

        [Fact]
        public void SwappingCoefficientsMidStream_CarriesState_NoClick()
        {
            // A cut at 150 Hz, published twice a millisecond with the gain
            // nudged each time (a UI drag): the output must stay a smooth
            // sine, never a restart transient.
            var eq = new ParametricEq(Fs);
            var band = new EqBand { FrequencyHz = 150, GainDb = -6, Q = 2 };
            var list = new List<EqBand> { band };
            eq.SetBands(list);

            int n = 8000;
            var input = Sine(150, n);
            var output = new float[n];
            var buf = new float[4];
            for (int off = 0; off < n; off += 4)
            {
                Array.Copy(input, off, buf, 0, 4);
                eq.Process(buf, 4);
                Array.Copy(buf, 0, output, off, 4);
                if (off >= 4000 && off % 8 == 0)
                {
                    band.GainDb = -6 - 0.01 * ((off - 4000) / 8 % 100);
                    eq.SetBands(list);
                }
            }
            // Largest step between successive samples in the tail vs. the
            // input's own largest step: a restart would show as a jump far
            // above the sine's slope.
            double maxIn = 0, maxOut = 0;
            for (int i = 4001; i < n; i++)
            {
                maxIn  = Math.Max(maxIn,  Math.Abs(input[i] - input[i - 1]));
                maxOut = Math.Max(maxOut, Math.Abs(output[i] - output[i - 1]));
            }
            Assert.True(maxOut < maxIn * 1.05, $"max step out {maxOut:F4} vs in {maxIn:F4}");
        }

        [Fact]
        public void Mixer_RunsPostMix_BeforeGainAndClamp()
        {
            var mixer = new Mixer { MasterGain = 1.0f };
            var eq = new ParametricEq(Fs);
            eq.SetBands(new List<EqBand> { new EqBand { FrequencyHz = 100, Type = EqBandType.HighCut, Q = 0.7 } });
            mixer.PostMix = eq;
            mixer.Add(new ConstSource(0.9f));

            // A DC-ish constant through a 100 Hz low-pass passes; an 800 Hz
            // tone at 0.9 would be cut. The clamp still holds after the EQ.
            var buf = new float[4];
            for (int i = 0; i < 2000; i++) mixer.Render(buf, 4);
            Assert.True(Math.Abs(buf[3] - 0.9f) < 0.01);

            mixer.MasterGain = 2.0f;
            for (int i = 0; i < 2000; i++) mixer.Render(buf, 4);
            Assert.Equal(1.0f, buf[3]);
        }

        private sealed class ConstSource : ISampleSource
        {
            private readonly float _v;
            public ConstSource(float v) { _v = v; }
            public bool IsActive => true;
            public void RenderAdd(float[] buffer, int count) { for (int i = 0; i < count; i++) buffer[i] += _v; }
        }
    }
}
