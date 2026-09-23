using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // THE parity gate. Every fixture clip in Fixtures/ that has a frozen
    // golden beside it (clip.csv + clip.golden.csv, made with the FixturePrep
    // tool) is replayed through the production pipeline and compared within
    // tolerance. Adding a fixture pair arms a new gate with zero code change;
    // a behavior-changing phase re-freezes the goldens deliberately (with the
    // rationale in the commit) — see docs/haptic-engine-plan.md.
    public class FixtureParityTests
    {
        // Fixtures live in the test PROJECT dir (checked in), not the output
        // dir; walk up from bin/<cfg>/<tfm> to find them.
        private static string FixturesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Fixtures");
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static IEnumerable<object[]> FixturePairs()
        {
            int found = 0;
            string dir = FixturesDir();
            if (dir != null)
            {
                foreach (var clip in Directory.GetFiles(dir, "*.csv").OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (clip.EndsWith(".golden.csv", StringComparison.OrdinalIgnoreCase)) continue;
                    string golden = Path.Combine(dir,
                        Path.GetFileNameWithoutExtension(clip) + ".golden.csv");
                    if (File.Exists(golden)) { found++; yield return new object[] { clip, golden }; }
                }
            }
            // xunit fails a Theory with zero data rows; an unarmed gate must be
            // green, so yield a no-op sentinel until real pairs exist.
            if (found == 0) yield return new object[] { null, null };
        }

        [Theory]
        [MemberData(nameof(FixturePairs))]
        public void Fixture_MatchesFrozenGolden(string clipPath, string goldenPath)
        {
            if (clipPath == null) return;   // unarmed gate (no fixture pairs yet)
            var actual = GoldenMetrics.Compute(CsvFrameReader.ReadFile(clipPath));
            var golden = GoldenMetrics.ReadCsv(goldenPath);
            var violations = GoldenMetrics.Compare(golden, actual);
            Assert.True(violations.Count == 0,
                $"{Path.GetFileName(clipPath)} parity failed:\n  " + string.Join("\n  ", violations));
        }

        [Fact]
        public void FixtureLibrary_StatusNote()
        {
            // Not an assertion — a visibility breadcrumb in test output. The
            // suite is green with zero fixtures (nothing to gate yet); once
            // pairs exist, the Theory above carries the real checks.
            string dir = FixturesDir();
            int pairs = FixturePairs().Count();
            Console.WriteLine(dir == null
                ? "Fixtures/ not found — parity gate unarmed."
                : $"Fixtures dir: {dir}, armed pairs: {pairs}");
            Assert.True(true);
        }
    }
}
