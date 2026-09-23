// Golden-metric baselines: the freeze/compare half of the parity gate.
//
// Compute() replays a fixture through a fresh HeadlessRig and reduces the
// output to windowed metrics: RMS, the minimum L0 duck multiplier, and
// Goertzel band power at a set of probe frequencies. WriteCsv/ReadCsv persist
// them next to the fixture ("#tfgold" files — CSV like everything else here,
// so baselines diff cleanly in git). Compare() checks a fresh run against a
// frozen baseline with dB tolerances and returns human-readable violations.
//
// Tolerances are deliberately loose enough for net48-x86 vs net8-x64 float
// codegen differences (the plan's rule: tolerance-based, never bit-exact) but
// tight enough that a behavior regression — a missing effect, a broken duck,
// a band shift — screams.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrueforceForAll.Core
{
    public sealed class MetricRow
    {
        public double TMs;
        public double Rms;
        public double DuckL0Min;
        public double[] BandPower;   // parallel to the probe list
    }

    public sealed class GoldenMetricsFile
    {
        public int WindowMs;
        public double[] ProbesHz;
        public List<MetricRow> Rows = new List<MetricRow>();
    }

    public static class GoldenMetrics
    {
        /// <summary>Default probe set: the effect carriers (gear 40, collision
        /// 50, pit 50, rev 90, DRS hum 120, ABS 150) plus low engine-order and
        /// slip-region probes. Real fixtures can extend this per clip.</summary>
        public static readonly double[] DefaultProbesHz = { 20, 40, 50, 90, 120, 150, 250, 400 };

        public const int DefaultWindowMs = 100;
        private const double StreamRateHz = 4000.0;

        // Below these floors a window is "silent" for the metric in question
        // and dB comparison is skipped (log of ~0 is meaningless noise).
        private const double RmsSilenceFloor  = 1e-4;
        private const double BandSilenceFloor = 1e-7;

        public static GoldenMetricsFile Compute(CsvFrameReader.Capture capture,
                                                double[] probesHz = null, int windowMs = DefaultWindowMs)
        {
            probesHz = probesHz ?? DefaultProbesHz;
            var rig = new HeadlessRig();
            rig.Run(capture);

            var outFile = new GoldenMetricsFile { WindowMs = windowMs, ProbesHz = probesHz };
            var samples = rig.Sink.Samples.ToArray();
            var ducks   = rig.DuckL0Trace;

            int winSamples = (int)(windowMs * StreamRateHz / 1000.0);   // 4 samples/ms
            int winTicks   = windowMs;                                  // 1 duck entry/ms

            for (int w = 0; (w + 1) * winSamples <= samples.Length; w++)
            {
                int off = w * winSamples;
                double sum = 0;
                for (int i = 0; i < winSamples; i++) { double v = samples[off + i]; sum += v * v; }

                double duckMin = 1.0;
                int dOff = w * winTicks;
                for (int i = 0; i < winTicks && dOff + i < ducks.Count; i++)
                    duckMin = Math.Min(duckMin, ducks[dOff + i]);

                var bands = new double[probesHz.Length];
                for (int p = 0; p < probesHz.Length; p++)
                    bands[p] = Goertzel.Power(samples, off, winSamples, probesHz[p], StreamRateHz);

                outFile.Rows.Add(new MetricRow
                {
                    TMs       = w * (double)windowMs,
                    Rms       = Math.Sqrt(sum / winSamples),
                    DuckL0Min = duckMin,
                    BandPower = bands,
                });
            }
            return outFile;
        }

        // ---------------- persistence ----------------

        public static void WriteCsv(GoldenMetricsFile m, string path)
        {
            var sb = new StringBuilder(m.Rows.Count * 96);
            sb.Append("#tfgold v=1 window_ms=").Append(m.WindowMs.ToString(CultureInfo.InvariantCulture))
              .Append(" probes=").Append(string.Join(";", Array.ConvertAll(m.ProbesHz, p => p.ToString("R", CultureInfo.InvariantCulture))))
              .Append('\n');
            sb.Append("t_ms,rms,duck_l0_min");
            foreach (var p in m.ProbesHz) sb.Append(",p").Append(p.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('\n');
            foreach (var r in m.Rows)
            {
                sb.Append(r.TMs.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Rms.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.DuckL0Min.ToString("R", CultureInfo.InvariantCulture));
                foreach (var b in r.BandPower)
                    sb.Append(',').Append(b.ToString("R", CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static GoldenMetricsFile ReadCsv(string path)
        {
            var m = new GoldenMetricsFile { WindowMs = DefaultWindowMs, ProbesHz = DefaultProbesHz };
            using (var reader = new StreamReader(path))
            {
                string line = reader.ReadLine();
                while (line != null && line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith("#tfgold", StringComparison.Ordinal))
                    {
                        foreach (var tok in line.Split(' '))
                        {
                            if (tok.StartsWith("window_ms=", StringComparison.Ordinal))
                                m.WindowMs = int.Parse(tok.Substring(10), CultureInfo.InvariantCulture);
                            else if (tok.StartsWith("probes=", StringComparison.Ordinal))
                            {
                                var parts = tok.Substring(7).Split(';');
                                m.ProbesHz = Array.ConvertAll(parts, s => double.Parse(s, CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    line = reader.ReadLine();
                }
                // line is now the column header; data follows.
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var c = line.Split(',');
                    var row = new MetricRow
                    {
                        TMs       = double.Parse(c[0], CultureInfo.InvariantCulture),
                        Rms       = double.Parse(c[1], CultureInfo.InvariantCulture),
                        DuckL0Min = double.Parse(c[2], CultureInfo.InvariantCulture),
                        BandPower = new double[c.Length - 3],
                    };
                    for (int i = 3; i < c.Length; i++)
                        row.BandPower[i - 3] = double.Parse(c[i], CultureInfo.InvariantCulture);
                    m.Rows.Add(row);
                }
            }
            return m;
        }

        // ---------------- comparison ----------------

        /// <summary>Compare a fresh run against a frozen baseline. Returns
        /// human-readable violations (empty = parity holds). dB tolerances
        /// apply where both sides are above the silence floor; a side crossing
        /// the floor while the other is loud is itself a violation (an effect
        /// went missing or appeared).</summary>
        public static List<string> Compare(GoldenMetricsFile golden, GoldenMetricsFile actual,
                                           double rmsTolDb = 1.0, double bandTolDb = 1.5,
                                           double duckTolAbs = 0.05)
        {
            var v = new List<string>();
            if (Math.Abs(golden.Rows.Count - actual.Rows.Count) > 1)
            {
                v.Add($"row count: golden {golden.Rows.Count} vs actual {actual.Rows.Count}");
                return v;   // structurally different; per-row diffs would spam
            }
            int n = Math.Min(golden.Rows.Count, actual.Rows.Count);
            int bands = Math.Min(golden.ProbesHz.Length, actual.ProbesHz.Length);

            for (int i = 0; i < n; i++)
            {
                var g = golden.Rows[i];
                var a = actual.Rows[i];

                CompareLevel(v, $"t={g.TMs}ms rms", g.Rms, a.Rms, RmsSilenceFloor, rmsTolDb);

                if (Math.Abs(g.DuckL0Min - a.DuckL0Min) > duckTolAbs)
                    v.Add($"t={g.TMs}ms duckL0: golden {g.DuckL0Min:F3} vs actual {a.DuckL0Min:F3}");

                for (int b = 0; b < bands; b++)
                {
                    // Band power is amplitude²; compare in dB on the sqrt.
                    CompareLevel(v, $"t={g.TMs}ms band {golden.ProbesHz[b]}Hz",
                                 Math.Sqrt(Math.Max(0, g.BandPower[b])),
                                 Math.Sqrt(Math.Max(0, a.BandPower[b])),
                                 Math.Sqrt(BandSilenceFloor), bandTolDb);
                }

                if (v.Count > 40)
                {
                    v.Add("... (truncated)");
                    return v;
                }
            }
            return v;
        }

        private static void CompareLevel(List<string> v, string what,
                                         double g, double a, double floor, double tolDb)
        {
            bool gs = g < floor, asilent = a < floor;
            if (gs && asilent) return;
            if (gs != asilent)
            {
                v.Add($"{what}: silence mismatch (golden {g:E2} vs actual {a:E2})");
                return;
            }
            double db = 20.0 * Math.Log10(a / g);
            if (Math.Abs(db) > tolDb)
                v.Add($"{what}: {db:+0.00;-0.00} dB (golden {g:E3} vs actual {a:E3})");
        }
    }
}
