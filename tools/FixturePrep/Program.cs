// Fixture prep for the replay harness. Turns raw CAPTURE files into trimmed
// fixture clips and frozen golden baselines:
//
//   dotnet run --project tools/FixturePrep -- info   capture.csv
//   dotnet run --project tools/FixturePrep -- trim   capture.csv clip.csv 12.5 32.5
//   dotnet run --project tools/FixturePrep -- freeze clip.csv   [clip.golden.csv]
//   dotnet run --project tools/FixturePrep -- check  clip.csv   clip.golden.csv
//
// info   — duration, rate, activity and signal ranges, to pick trim windows.
// trim   — keep rows whose time is within [start, end] seconds of the first
//          row. RAW line copy (metadata + header untouched, rows byte-
//          identical) so trimming can never alter what was recorded.
// freeze — replay the clip through the headless rig, write the golden
//          metrics CSV beside it. Do this ONCE per behavior-approved build.
// check  — replay and compare against a frozen golden; prints violations,
//          exit code 1 if parity fails. This is what the parity test runs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TrueforceForAll.Core;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args.Length > 0 ? args[0].ToLowerInvariant() : "")
            {
                case "info":   return Info(args[1]);
                case "trim":   return Trim(args[1], args[2],
                                           double.Parse(args[3], CultureInfo.InvariantCulture),
                                           double.Parse(args[4], CultureInfo.InvariantCulture));
                case "freeze": return Freeze(args[1], args.Length > 2 ? args[2] : null);
                case "check":  return Check(args[1], args[2]);
                default:
                    Console.WriteLine("usage: FixturePrep info <capture.csv>");
                    Console.WriteLine("       FixturePrep trim <capture.csv> <clip.csv> <startSec> <endSec>");
                    Console.WriteLine("       FixturePrep freeze <clip.csv> [golden.csv]");
                    Console.WriteLine("       FixturePrep check <clip.csv> <golden.csv>");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Info(string path)
    {
        var cap = CsvFrameReader.ReadFile(path);
        if (cap.Rows.Count == 0) { Console.WriteLine("empty capture"); return 1; }

        long t0 = cap.Rows[0].TUs, t1 = cap.Rows[cap.Rows.Count - 1].TUs;
        double durSec = (t1 - t0) / 1e6;
        double rate = cap.Rows.Count > 1 ? (cap.Rows.Count - 1) / Math.Max(1e-9, durSec) : 0;
        int active = cap.Rows.Count(r => r.SessionActive);
        int withFfb = cap.Rows.Count(r => r.GameFfb.HasValue);
        int withQuads = cap.Rows.Count(r => r.Frame.HasTireQuads);
        double maxSpeed = cap.Rows.Max(r => r.Frame.SpeedKmh);
        double maxSlip = cap.Rows.Max(r => r.Frame.WheelSlip ?? 0);
        double maxSlipAngle = cap.Rows.Max(r => Math.Abs(r.Frame.FrontSlipAngleRad ?? 0));

        Console.WriteLine($"src={cap.SourceName} game={cap.GameName} schema=v{cap.SchemaVersion}");
        Console.WriteLine($"rows={cap.Rows.Count} duration={durSec:F1}s rate~={rate:F0}Hz");
        Console.WriteLine($"active={100.0 * active / cap.Rows.Count:F0}%  game_ffb={100.0 * withFfb / cap.Rows.Count:F0}%  quads={100.0 * withQuads / cap.Rows.Count:F0}%");
        Console.WriteLine($"max: speed={maxSpeed:F0}km/h combinedSlip={maxSlip:F2} |frontSlipAngle|={maxSlipAngle:F3}rad");

        // Per-10s activity sketch so trim windows are easy to pick blind.
        Console.WriteLine("timeline (10s bins: speed_max / slip_max):");
        foreach (var bin in cap.Rows.GroupBy(r => (r.TUs - t0) / 10_000_000L).OrderBy(g => g.Key))
            Console.WriteLine($"  {bin.Key * 10,4}s  spd {bin.Max(r => r.Frame.SpeedKmh),5:F0}  slip {bin.Max(r => r.Frame.WheelSlip ?? 0):F2}");
        return 0;
    }

    private static int Trim(string inPath, string outPath, double startSec, double endSec)
    {
        // Raw text pass-through: metadata + header verbatim, keep data lines
        // whose first column (t_us) falls inside the window. Rows stay
        // byte-identical to the recording.
        long t0 = -1, kept = 0, total = 0;
        long loUs = 0, hiUs = 0;
        using (var reader = new StreamReader(inPath))
        using (var writer = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false)))
        {
            string line;
            bool pastHeader = false;
            while ((line = reader.ReadLine()) != null)
            {
                if (!pastHeader)
                {
                    writer.Write(line); writer.Write('\n');
                    if (!line.StartsWith("#", StringComparison.Ordinal)) pastHeader = true;   // the column header
                    continue;
                }
                if (line.Length == 0) continue;
                total++;
                int comma = line.IndexOf(',');
                long tUs = long.Parse(line.Substring(0, comma), CultureInfo.InvariantCulture);
                if (t0 < 0)
                {
                    t0 = tUs;
                    loUs = t0 + (long)(startSec * 1e6);
                    hiUs = t0 + (long)(endSec * 1e6);
                }
                if (tUs >= loUs && tUs <= hiUs)
                {
                    writer.Write(line); writer.Write('\n');
                    kept++;
                }
            }
        }
        Console.WriteLine($"kept {kept}/{total} rows -> {outPath}");
        return kept > 0 ? 0 : 1;
    }

    private static int Freeze(string clipPath, string goldenPath)
    {
        goldenPath = goldenPath ?? DefaultGoldenPath(clipPath);
        var cap = CsvFrameReader.ReadFile(clipPath);
        var metrics = GoldenMetrics.Compute(cap);
        GoldenMetrics.WriteCsv(metrics, goldenPath);
        Console.WriteLine($"froze {metrics.Rows.Count} windows ({metrics.WindowMs}ms) -> {goldenPath}");
        return 0;
    }

    private static int Check(string clipPath, string goldenPath)
    {
        var cap = CsvFrameReader.ReadFile(clipPath);
        var actual = GoldenMetrics.Compute(cap);
        var golden = GoldenMetrics.ReadCsv(goldenPath);
        List<string> violations = GoldenMetrics.Compare(golden, actual);
        if (violations.Count == 0)
        {
            Console.WriteLine($"PARITY OK ({actual.Rows.Count} windows)");
            return 0;
        }
        Console.WriteLine($"PARITY FAILED ({violations.Count} violations):");
        foreach (var v in violations) Console.WriteLine("  " + v);
        return 1;
    }

    internal static string DefaultGoldenPath(string clipPath)
    {
        // clip.csv -> clip.golden.csv
        string dir = Path.GetDirectoryName(Path.GetFullPath(clipPath));
        string name = Path.GetFileNameWithoutExtension(clipPath);
        return Path.Combine(dir ?? "", name + ".golden.csv");
    }
}
