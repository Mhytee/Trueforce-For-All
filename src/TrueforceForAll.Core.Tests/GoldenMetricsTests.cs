using System;
using System.IO;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The freeze/compare half of the parity gate. Determinism is the load-
    // bearing property: two runs of the same fixture must produce identical
    // metrics (seeded noise), otherwise every golden comparison chases RNG.
    public class GoldenMetricsTests
    {
        private static CsvFrameReader.Capture Fixture(int rows, Func<int, TelemetryFrame> frameAt)
        {
            var cap = new CsvFrameReader.Capture { SchemaVersion = 2, SourceName = "synthetic", GameName = "test" };
            for (int i = 0; i < rows; i++)
                cap.Rows.Add(new ReplayRow { TUs = 1_000_000L + i * 10_000L, SessionActive = true, Frame = frameAt(i) });
            return cap;
        }

        private static TelemetryFrame Frame(double rpm = 3000, string gear = "3", double slip = 0.0) =>
            new TelemetryFrame
            {
                Rpms = rpm, MaxRpm = 8000, Throttle01 = 0.5, SpeedKmh = 100,
                Gear = gear, WheelSlip = slip, NumCylinders = 6,
                AccelerationSway = 0.0, AccelerationHeave = 0.0, AccelerationSurge = 0.0,
                YawRateDegPerSec = 0.0,
            };

        [Fact]
        public void Compute_IsDeterministic_EvenWithNoiseEffectsActive()
        {
            // Slip > 0 drives TractionLoss's NOISE oscillator — the seeded-RNG
            // path. Two independent rigs must agree exactly.
            var cap = Fixture(150, i => Frame(slip: i >= 50 ? 0.85 : 0.0));

            var m1 = GoldenMetrics.Compute(cap);
            var m2 = GoldenMetrics.Compute(cap);

            var violations = GoldenMetrics.Compare(m1, m2, rmsTolDb: 0.01, bandTolDb: 0.01, duckTolAbs: 0.001);
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void Compare_FlagsBehaviorChange()
        {
            // Same steady base, one has a gear shift — the duck envelope and
            // the 40 Hz shift-thud band must diverge.
            var steady = GoldenMetrics.Compute(Fixture(150, _ => Frame()));
            var shifty = GoldenMetrics.Compute(Fixture(150, i => Frame(gear: i >= 75 ? "4" : "3")));

            var violations = GoldenMetrics.Compare(steady, shifty);
            Assert.True(violations.Count > 0, "a gear shift should not pass parity against a steady baseline");
        }

        [Fact]
        public void Metrics_RoundTrip_ThroughCsv()
        {
            string path = Path.Combine(Path.GetTempPath(), "tfgold_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var m = GoldenMetrics.Compute(Fixture(100, i => Frame(slip: i >= 50 ? 0.8 : 0.0)));
                GoldenMetrics.WriteCsv(m, path);
                var r = GoldenMetrics.ReadCsv(path);

                Assert.Equal(m.WindowMs, r.WindowMs);
                Assert.Equal(m.ProbesHz, r.ProbesHz);
                Assert.Equal(m.Rows.Count, r.Rows.Count);
                var violations = GoldenMetrics.Compare(m, r, rmsTolDb: 0.001, bandTolDb: 0.001, duckTolAbs: 1e-9);
                Assert.True(violations.Count == 0, string.Join("\n", violations));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
