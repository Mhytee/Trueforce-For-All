// The two FixturePrep commands behind the settings UI's Test-button clips
// (src/TrueforceForAll.Engine/TestClips): 'clip' cuts one from a recording,
// 'audition' plays one through its effect headless. See Program.cs for usage.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

internal static class TestClipTool
{
    // ---------------------------------------------------------------- clip

    public static int Cut(string[] args)
    {
        string inPath = args[1], outPath = args[2];
        double startSec = double.Parse(args[3], CultureInfo.InvariantCulture);
        double endSec   = double.Parse(args[4], CultureInfo.InvariantCulture);
        string keepArg = null, gearsArg = null;
        for (int i = 5; i < args.Length - 1; i++)
        {
            if (args[i] == "--keep") keepArg = args[++i];
            else if (args[i] == "--gears") gearsArg = args[++i];
        }
        if (string.IsNullOrEmpty(keepArg))
            throw new ArgumentException("clip needs --keep <col,col,...> (t_us is always kept)");

        // "N=1,10=hold": relabel the gear column. 'hold' repeats the previous
        // row's gear, for a recording that shows the neutral a shift passes
        // through where the live source now holds the old gear across it.
        var gearMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(gearsArg))
            foreach (string pair in gearsArg.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"bad --gears entry '{pair}'");
                gearMap[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }

        string[] lines = File.ReadAllLines(inPath);
        int headerAt = Array.FindIndex(lines, l => !l.StartsWith("#", StringComparison.Ordinal));
        if (headerAt < 0) throw new InvalidDataException("capture has no header line");
        string[] header = lines[headerAt].Split(',');
        int tCol = Array.IndexOf(header, "t_us");
        if (tCol < 0) throw new InvalidDataException("capture has no t_us column");

        // A name the header has is that column; a name it only has with the
        // _fl/_fr/_rl/_rr suffixes is that per-tire quad, all four.
        var keep = new List<int> { tCol };
        foreach (string name in keepArg.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            int col = Array.IndexOf(header, name);
            if (col >= 0) { if (!keep.Contains(col)) keep.Add(col); continue; }
            int fl = Array.IndexOf(header, name + "_fl");
            if (fl < 0) throw new ArgumentException($"no column or quad named '{name}'");
            foreach (string corner in new[] { "_fl", "_fr", "_rl", "_rr" })
                keep.Add(Array.IndexOf(header, name + corner));
        }
        int gearCol = Array.IndexOf(header, "gear");

        long t0 = -1, firstKeptUs = -1;
        int kept = 0;
        string prevGear = null;
        var sb = new StringBuilder();
        foreach (string meta in lines.Take(headerAt)) sb.Append(meta).Append('\n');
        sb.Append("# clip of ").Append(Path.GetFileName(inPath))
          .Append(' ').Append(startSec.ToString("0.###", CultureInfo.InvariantCulture))
          .Append("s-").Append(endSec.ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
        if (gearMap.Count > 0) sb.Append(" gears ").Append(gearsArg.Replace(',', ';'));
        sb.Append('\n');
        sb.Append(string.Join(",", keep.Select(c => header[c]))).Append('\n');

        for (int li = headerAt + 1; li < lines.Length; li++)
        {
            string line = lines[li];
            if (line.Length == 0 || line[0] == '#') continue;
            string[] cells = line.Split(',');
            long tUs = long.Parse(cells[tCol], CultureInfo.InvariantCulture);
            if (t0 < 0) t0 = tUs;

            // The gear relabel runs over EVERY row, kept or not, so a 'hold'
            // on the first kept row has the gear from before the window.
            string gear = gearCol >= 0 && gearCol < cells.Length ? cells[gearCol] : "";
            if (gearMap.TryGetValue(gear, out string mapped))
                gear = mapped == "hold" ? (prevGear ?? gear) : mapped;
            prevGear = gear;

            double sec = (tUs - t0) / 1e6;
            if (sec < startSec || sec > endSec) continue;
            if (firstKeptUs < 0) firstKeptUs = tUs;

            for (int k = 0; k < keep.Count; k++)
            {
                if (k > 0) sb.Append(',');
                int col = keep[k];
                string cell = col < cells.Length ? cells[col] : "";
                if (col == tCol) sb.Append((tUs - firstKeptUs).ToString(CultureInfo.InvariantCulture));
                else if (col == gearCol) sb.Append(gear);
                else sb.Append(Round(cell));
            }
            sb.Append('\n');
            kept++;
        }

        if (kept < 2) { Console.WriteLine("window holds fewer than two rows; nothing written"); return 1; }
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"kept {kept} rows, {keep.Count} columns, {sb.Length / 1024.0:F1} KB -> {outPath}");
        return 0;
    }

    // Five decimal places: a tenth of a millimetre of suspension travel, a
    // hundred-thousandth of a slip ratio. Far below anything an effect can
    // tell apart, and it takes a row from ~750 bytes of float noise to ~100.
    private static string Round(string cell)
    {
        if (cell.Length == 0) return cell;
        return double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture)
            : cell;
    }

    // ------------------------------------------------------------ audition

    public static int Audition(string[] args)
    {
        string clipPath = args[1], effectName = args[2];
        bool realtime = args.Contains("--realtime");

        Type type = typeof(TelemetryEffect).Assembly.GetType("TrueforceForAll.Plugin.Effects." + effectName);
        if (type == null || !typeof(TelemetryEffect).IsAssignableFrom(type))
            throw new ArgumentException($"no effect class named '{effectName}'");
        var effect = (TelemetryEffect)Activator.CreateInstance(type);

        // Property=value overrides, to hear what a slider does to the clip.
        foreach (string arg in args.Skip(3).Where(a => a.Contains('=') && !a.StartsWith("--")))
        {
            int eq = arg.IndexOf('=');
            var prop = type.GetProperty(arg.Substring(0, eq))
                       ?? throw new ArgumentException($"{effectName} has no property '{arg.Substring(0, eq)}'");
            Type pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object value = pt.IsEnum
                ? Enum.Parse(pt, arg.Substring(eq + 1), ignoreCase: true)
                : Convert.ChangeType(arg.Substring(eq + 1), pt, CultureInfo.InvariantCulture);
            prop.SetValue(effect, value);
        }

        var clip = CsvFrameReader.ReadFile(clipPath);
        effect.SeedNoise(12345);

        // The Test button's path, minus the wheel: one effect in a mixer, an
        // EngineLoop on a microsecond clock, a clip test started at time zero.
        var mixer = new Mixer();
        mixer.Add(effect);
        var sink = new CaptureSink();
        var loop = new EngineLoop(mixer, null, sink, new FrameResampler(1_000_000));
        int totalMs = loop.StartClipTest(effect, clip, 0);
        if (totalMs <= 0) { Console.WriteLine("clip did not start (fewer than two rows?)"); return 1; }

        var buf = new float[EngineLoop.BatchSamples];
        int ticks = totalMs + 50;
        if (realtime)
        {
            // Some effects time their holds on the wall clock (the redline
            // buzz's 80 ms, a collision's refractory window), which a fast
            // virtual run stretches across the whole clip. Paced to real time
            // they behave as they do on the wheel.
            var sw = Stopwatch.StartNew();
            for (int t = 0; t < ticks;)
            {
                long due = sw.ElapsedMilliseconds;
                while (t < due && t < ticks) loop.RunOneTick(buf, ++t * 1000L, 0f, 0f, 0f);
                Thread.Sleep(1);
            }
        }
        else
        {
            for (int t = 1; t <= ticks; t++) loop.RunOneTick(buf, t * 1000L, 0f, 0f, 0f);
        }

        // 100 ms bins of the rendered 4 kHz stream.
        const int BinSamples = 400;
        float peakAll = 0;
        int loudBins = 0, bins = 0;
        Console.WriteLine($"{effectName} <- {Path.GetFileName(clipPath)}  ({clip.Rows.Count} rows, {totalMs - EffectClipTest.TailMs} ms + {EffectClipTest.TailMs} ms tail{(realtime ? ", real time" : "")})");
        Console.WriteLine("    ms   peak    rms");
        for (int at = 0; at < sink.Samples.Count; at += BinSamples)
        {
            int n = Math.Min(BinSamples, sink.Samples.Count - at);
            float peak = 0; double sq = 0;
            for (int i = 0; i < n; i++)
            {
                float v = sink.Samples[at + i];
                peak = Math.Max(peak, Math.Abs(v));
                sq += v * v;
            }
            double rms = Math.Sqrt(sq / n);
            peakAll = Math.Max(peakAll, peak);
            bins++;
            if (peak > 0.01f) loudBins++;
            Console.WriteLine($"{at / 4,6}  {peak:F3}  {rms:F3}  {new string('#', (int)Math.Round(Math.Min(1.0, rms / 0.4) * 50))}");
        }
        Console.WriteLine($"peak {peakAll:F3}; {loudBins}/{bins} bins above 0.01; still testing at end: {effect.IsClipTesting}");
        return peakAll > 0.01f ? 0 : 1;
    }
}
