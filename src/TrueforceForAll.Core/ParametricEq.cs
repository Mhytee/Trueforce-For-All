// Trueforce parametric EQ: a bank of second-order IIR sections (RBJ Audio
// EQ Cookbook peaking, shelving and cut filters) run over the mixed
// Trueforce sample stream at the 4 kHz stream rate. The point is rig
// resonance control: a wheelbase, a rig or a desk rings at particular
// frequencies, and a narrow cut there removes the rattle without dulling
// the rest of the feel. Boosts are allowed too (some motors are weak in a
// band), capped so a boost cannot turn a texture into a lock-up.
//
// Threading: SetBands / Enabled are called from the UI thread; Process runs
// on the engine thread. The coefficient set is published as an immutable
// array (volatile reference, copy-on-write), and the per-section filter
// state lives in arrays only the engine thread touches. When Process sees a
// new coefficient set it carries the state of every band that survived (by
// band index) so a drag never restarts the filters and never clicks.
//
// Placement: Mixer.PostMix, i.e. after the sources are summed and BEFORE
// master gain and the [-1, 1] clamp. The EQ is linear, so gain order is
// irrelevant; clamping once, after the boost, is what matters.

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Core
{
    /// <summary>A stage that rewrites a rendered sample buffer in place.</summary>
    public interface ISampleProcessor
    {
        void Process(float[] buffer, int count);
    }

    public enum EqBandType
    {
        /// <summary>Bell: boost or cut around the frequency, Q sets the width.</summary>
        Peak = 0,
        /// <summary>Everything below the frequency moves by the gain.</summary>
        LowShelf = 1,
        /// <summary>Everything above the frequency moves by the gain.</summary>
        HighShelf = 2,
        /// <summary>12 dB/octave high-pass: removes content below the frequency. Gain unused.</summary>
        LowCut = 3,
        /// <summary>12 dB/octave low-pass: removes content above the frequency. Gain unused.</summary>
        HighCut = 4,
    }

    /// <summary>One EQ band, as persisted in settings. Plain properties so
    /// Newtonsoft round-trips it; ranges are enforced by
    /// <see cref="ParametricEq.Clamp"/> when the bank is built, never here,
    /// so a hand-edited file cannot build an unstable filter.</summary>
    public sealed class EqBand
    {
        public bool Enabled { get; set; } = true;
        public EqBandType Type { get; set; } = EqBandType.Peak;
        public double FrequencyHz { get; set; } = 100.0;
        public double GainDb { get; set; } = 0.0;
        public double Q { get; set; } = ParametricEq.DefaultQ;
        /// <summary>Cut steepness in dB per octave: 12, 24, 36 or 48 (LowCut
        /// and HighCut only, ignored elsewhere). Each 12 dB is one cascaded
        /// second-order section. Absent in older files, so 12.</summary>
        public int SlopeDbPerOct { get; set; } = 12;

        public EqBand Clone() => new EqBand
        {
            Enabled = Enabled, Type = Type, FrequencyHz = FrequencyHz, GainDb = GainDb, Q = Q,
            SlopeDbPerOct = SlopeDbPerOct,
        };

        public bool IsCut => Type == EqBandType.LowCut || Type == EqBandType.HighCut;

        /// <summary>True when the band contributes nothing (disabled, or a
        /// gain-driven type sitting at 0 dB).</summary>
        public bool IsBypass
        {
            get
            {
                if (!Enabled) return true;
                switch (Type)
                {
                    case EqBandType.Peak:
                    case EqBandType.LowShelf:
                    case EqBandType.HighShelf:
                        return Math.Abs(GainDb) < 0.005;
                    default:
                        return false;
                }
            }
        }
    }

    public sealed class ParametricEq : ISampleProcessor
    {
        public const double DefaultSampleRateHz = 4000.0;

        // Ranges. MaxFrequencyHz stays under the 4 kHz stream's 2 kHz Nyquist
        // with margin (a biquad centred right at Nyquist is degenerate). The
        // gain floor is deep enough to notch a resonance out entirely; the
        // boost cap keeps a band from turning texture into a pull.
        public const double MinFrequencyHz = 5.0;
        public const double MaxFrequencyHz = 1800.0;
        public const double MinGainDb = -30.0;
        public const double MaxGainDb = 12.0;
        public const double MinQ = 0.1;
        public const double MaxQ = 30.0;
        /// <summary>Bell / shelf starting width: 1.4 octaves, the usual EQ
        /// default (owner call 2026-09-24; 2.0 was tighter than people expect).</summary>
        public const double DefaultQ = 1.0;
        /// <summary>Shelf and cut starting Q: 0.707 is the Butterworth corner
        /// for a cut and the steepest shelf slope (S = 1) with no overshoot.
        /// A bell Q carried onto either bumps at the corner.</summary>
        public const double SlopeDefaultQ = 0.7071;
        public const int MaxBands = 32;

        /// <summary>The flat starting layout: six bells an octave apart across
        /// the band the effects live in, all at 0 dB. Visible handles to grab,
        /// no sound until one moves. Octave spacing suits the Q 1 width (1.4
        /// octaves) with modest overlap.</summary>
        public static readonly double[] FactoryFrequenciesHz = { 20, 40, 80, 160, 320, 640 };

        public static List<EqBand> FactoryBands()
        {
            var list = new List<EqBand>(FactoryFrequenciesHz.Length);
            foreach (double f in FactoryFrequenciesHz)
                list.Add(new EqBand { FrequencyHz = f, GainDb = 0, Q = DefaultQ, Type = EqBandType.Peak });
            return list;
        }

        /// <summary>The cut slopes offered, dB per octave.</summary>
        public static readonly int[] Slopes = { 12, 24, 36, 48 };

        public static int SnapSlope(int s)
        {
            int best = Slopes[0];
            foreach (int v in Slopes) if (Math.Abs(v - s) < Math.Abs(best - s)) best = v;
            return best;
        }

        private sealed class Section
        {
            public int BandIndex;
            public int Sub;                     // which of the band's cascaded sections
            public double B0, B1, B2, A1, A2;   // normalised (a0 = 1)
        }

        private static readonly Section[] NoSections = new Section[0];

        private readonly double _fs;
        private volatile Section[] _published = NoSections;
        private volatile bool _enabled = true;
        private volatile bool _resetRequested;

        // Engine-thread state.
        private Section[] _applied = NoSections;
        private double[] _z1 = new double[0];
        private double[] _z2 = new double[0];

        public ParametricEq(double sampleRateHz = DefaultSampleRateHz)
        {
            if (!(sampleRateHz > 0)) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
            _fs = sampleRateHz;
        }

        public double SampleRateHz => _fs;

        /// <summary>Master bypass. Off = samples pass through untouched and
        /// the filter state is cleared, so re-enabling starts clean.</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; if (!value) _resetRequested = true; }
        }

        /// <summary>Number of sections actually running (bypass bands are
        /// left out of the bank entirely).</summary>
        public int ActiveSectionCount => _published.Length;

        /// <summary>Clear the filter memory on the next Process (any thread).</summary>
        public void Reset() => _resetRequested = true;

        /// <summary>Clamp a band into the legal ranges and sanitise NaN. Used
        /// by the bank build and by the UI so both agree.</summary>
        public static void Clamp(EqBand b)
        {
            if (b == null) return;
            if (double.IsNaN(b.FrequencyHz) || double.IsInfinity(b.FrequencyHz)) b.FrequencyHz = 100.0;
            if (double.IsNaN(b.GainDb) || double.IsInfinity(b.GainDb)) b.GainDb = 0.0;
            if (double.IsNaN(b.Q) || double.IsInfinity(b.Q)) b.Q = DefaultQ;
            if (b.FrequencyHz < MinFrequencyHz) b.FrequencyHz = MinFrequencyHz;
            if (b.FrequencyHz > MaxFrequencyHz) b.FrequencyHz = MaxFrequencyHz;
            if (b.GainDb < MinGainDb) b.GainDb = MinGainDb;
            if (b.GainDb > MaxGainDb) b.GainDb = MaxGainDb;
            if (b.Q < MinQ) b.Q = MinQ;
            if (b.Q > MaxQ) b.Q = MaxQ;
            if (!Enum.IsDefined(typeof(EqBandType), b.Type)) b.Type = EqBandType.Peak;
            b.SlopeDbPerOct = SnapSlope(b.SlopeDbPerOct);
        }

        /// <summary>How many second-order sections a band expands to: one for
        /// bells and shelves, slope/12 for cuts.</summary>
        public static int SectionCount(EqBand b)
            => b != null && b.IsCut ? Math.Max(1, SnapSlope(b.SlopeDbPerOct) / 12) : 1;

        /// <summary>Coefficients of one of a band's sections. A multi-section
        /// cut uses the Butterworth Q spread for its order (so the corner sits
        /// at -3 dB and the passband stays flat), scaled by the band's own Q
        /// relative to 0.707 so the corner-sharpness drag works at any slope.</summary>
        public static void DesignSection(EqBand b, int sub, double fs,
                                         out double b0, out double b1, out double b2,
                                         out double a1, out double a2)
        {
            int n = SectionCount(b);
            if (n <= 1) { Design(b, fs, out b0, out b1, out b2, out a1, out a2); return; }
            if (sub < 0) sub = 0; else if (sub >= n) sub = n - 1;
            double qk = 1.0 / (2.0 * Math.Cos((2 * sub + 1) * Math.PI / (4.0 * n)));
            double scale = Math.Min(Math.Max(b.Q, MinQ), MaxQ) / SlopeDefaultQ;
            var one = new EqBand { Type = b.Type, FrequencyHz = b.FrequencyHz, GainDb = b.GainDb, Q = qk * scale, Enabled = true };
            Design(one, fs, out b0, out b1, out b2, out a1, out a2);
        }

        /// <summary>Publish a new band list. The list is read once, here;
        /// later edits to the same objects do nothing until the next call.
        /// Bands are clamped in place (see <see cref="Clamp"/>). Anything past
        /// <see cref="MaxBands"/> is ignored.</summary>
        public void SetBands(IList<EqBand> bands)
        {
            if (bands == null || bands.Count == 0) { _published = NoSections; return; }
            var list = new List<Section>(bands.Count);
            int n = Math.Min(bands.Count, MaxBands);
            for (int i = 0; i < n; i++)
            {
                var b = bands[i];
                if (b == null) continue;
                Clamp(b);
                if (b.IsBypass) continue;
                int secs = SectionCount(b);
                for (int sub = 0; sub < secs; sub++)
                {
                    var s = new Section { BandIndex = i, Sub = sub };
                    DesignSection(b, sub, _fs, out s.B0, out s.B1, out s.B2, out s.A1, out s.A2);
                    list.Add(s);
                }
            }
            _published = list.Count == 0 ? NoSections : list.ToArray();
        }

        /// <summary>RBJ cookbook coefficients, normalised so a0 = 1.</summary>
        public static void Design(EqBand b, double fs,
                                  out double b0, out double b1, out double b2,
                                  out double a1, out double a2)
        {
            double f = Math.Min(Math.Max(b.FrequencyHz, MinFrequencyHz), Math.Min(MaxFrequencyHz, fs * 0.49));
            double q = Math.Min(Math.Max(b.Q, MinQ), MaxQ);
            double g = Math.Min(Math.Max(b.GainDb, MinGainDb), MaxGainDb);
            double w0 = 2.0 * Math.PI * f / fs;
            double cosw = Math.Cos(w0);
            double sinw = Math.Sin(w0);
            double alpha = sinw / (2.0 * q);
            double A = Math.Pow(10.0, g / 40.0);
            double a0;
            switch (b.Type)
            {
                case EqBandType.LowShelf:
                {
                    double sqA2a = 2.0 * Math.Sqrt(A) * alpha;
                    b0 =      A * ((A + 1) - (A - 1) * cosw + sqA2a);
                    b1 =  2 * A * ((A - 1) - (A + 1) * cosw);
                    b2 =      A * ((A + 1) - (A - 1) * cosw - sqA2a);
                    a0 =           (A + 1) + (A - 1) * cosw + sqA2a;
                    a1 =     -2 * ((A - 1) + (A + 1) * cosw);
                    a2 =           (A + 1) + (A - 1) * cosw - sqA2a;
                    break;
                }
                case EqBandType.HighShelf:
                {
                    double sqA2a = 2.0 * Math.Sqrt(A) * alpha;
                    b0 =      A * ((A + 1) + (A - 1) * cosw + sqA2a);
                    b1 = -2 * A * ((A - 1) + (A + 1) * cosw);
                    b2 =      A * ((A + 1) + (A - 1) * cosw - sqA2a);
                    a0 =           (A + 1) - (A - 1) * cosw + sqA2a;
                    a1 =      2 * ((A - 1) - (A + 1) * cosw);
                    a2 =           (A + 1) - (A - 1) * cosw - sqA2a;
                    break;
                }
                case EqBandType.LowCut:
                    b0 =  (1 + cosw) / 2;
                    b1 = -(1 + cosw);
                    b2 =  (1 + cosw) / 2;
                    a0 =   1 + alpha;
                    a1 =  -2 * cosw;
                    a2 =   1 - alpha;
                    break;
                case EqBandType.HighCut:
                    b0 = (1 - cosw) / 2;
                    b1 =  1 - cosw;
                    b2 = (1 - cosw) / 2;
                    a0 =  1 + alpha;
                    a1 = -2 * cosw;
                    a2 =  1 - alpha;
                    break;
                default: // Peak
                    b0 =  1 + alpha * A;
                    b1 = -2 * cosw;
                    b2 =  1 - alpha * A;
                    a0 =  1 + alpha / A;
                    a1 = -2 * cosw;
                    a2 =  1 - alpha / A;
                    break;
            }
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        }

        /// <summary>Magnitude response of one band in dB at a frequency.</summary>
        public static double BandResponseDb(EqBand b, double freqHz, double fs = DefaultSampleRateHz)
        {
            if (b == null || b.IsBypass) return 0.0;
            int n = SectionCount(b);
            double db = 0.0;
            for (int sub = 0; sub < n; sub++)
            {
                DesignSection(b, sub, fs, out double b0, out double b1, out double b2, out double a1, out double a2);
                db += 20.0 * Math.Log10(Math.Max(1e-12, Magnitude(b0, b1, b2, a1, a2, freqHz, fs)));
            }
            return db;
        }

        /// <summary>Magnitude response of the whole published bank in dB at a
        /// frequency. Ignores <see cref="Enabled"/>: this is the curve the
        /// user is editing, drawn greyed out when the EQ is off.</summary>
        public double ResponseDb(double freqHz)
        {
            var secs = _published;
            double db = 0.0;
            for (int i = 0; i < secs.Length; i++)
            {
                var s = secs[i];
                db += 20.0 * Math.Log10(Math.Max(1e-12, Magnitude(s.B0, s.B1, s.B2, s.A1, s.A2, freqHz, _fs)));
            }
            return db;
        }

        /// <summary>Total response of an arbitrary band list (the UI draws a
        /// list that may differ from the published bank mid-edit).</summary>
        public static double ResponseDb(IList<EqBand> bands, double freqHz, double fs = DefaultSampleRateHz)
        {
            if (bands == null) return 0.0;
            double db = 0.0;
            for (int i = 0; i < bands.Count; i++) db += BandResponseDb(bands[i], freqHz, fs);
            return db;
        }

        private static double Magnitude(double b0, double b1, double b2, double a1, double a2, double freqHz, double fs)
        {
            // |H(e^jw)| with z^-1 = e^-jw.
            double w = 2.0 * Math.PI * freqHz / fs;
            double c1 = Math.Cos(w), s1 = Math.Sin(w);
            double c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
            double dr = 1.0 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
            double num = nr * nr + ni * ni;
            double den = dr * dr + di * di;
            if (den < 1e-24) return 1e6;
            return Math.Sqrt(num / den);
        }

        public void Process(float[] buffer, int count)
        {
            if (buffer == null || count <= 0) return;
            if (count > buffer.Length) count = buffer.Length;

            if (_resetRequested)
            {
                _resetRequested = false;
                Array.Clear(_z1, 0, _z1.Length);
                Array.Clear(_z2, 0, _z2.Length);
            }
            if (!_enabled) return;

            var secs = _published;
            if (!ReferenceEquals(secs, _applied)) Adopt(secs);
            if (secs.Length == 0) return;

            for (int i = 0; i < count; i++)
            {
                double x = buffer[i];
                for (int s = 0; s < secs.Length; s++)
                {
                    var c = secs[s];
                    // Direct Form II transposed, double state: the low bands
                    // sit at w0 ~ 0.01 rad where float coefficients lose the
                    // pole placement.
                    double y = c.B0 * x + _z1[s];
                    _z1[s] = c.B1 * x - c.A1 * y + _z2[s];
                    _z2[s] = c.B2 * x - c.A2 * y;
                    x = y;
                }
                buffer[i] = (float)x;
            }
        }

        // Carry filter memory across a coefficient swap by band index so a
        // slider drag (30+ publishes a second) never restarts the filters.
        private void Adopt(Section[] next)
        {
            var old = _applied;
            var oz1 = _z1;
            var oz2 = _z2;
            var z1 = new double[next.Length];
            var z2 = new double[next.Length];
            for (int i = 0; i < next.Length; i++)
            {
                int idx = next[i].BandIndex, sub = next[i].Sub;
                for (int j = 0; j < old.Length; j++)
                {
                    if (old[j].BandIndex != idx || old[j].Sub != sub) continue;
                    z1[i] = oz1[j];
                    z2[i] = oz2[j];
                    break;
                }
            }
            _z1 = z1;
            _z2 = z2;
            _applied = next;
        }
    }
}
