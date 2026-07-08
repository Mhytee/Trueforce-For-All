// Golden telemetry capture (CSV schema v2) — the replay-fixture recorder.
//
// This is the successor to FfbCaptureLog's 12-column analysis CSV: one row per
// telemetry frame carrying the FULL TelemetryFrame (including the per-tire
// quads) plus the game-FFB sample the tap latched at the same instant. The
// replay harness feeds these files back through the whole engine headless, so
// every refactor phase can prove parity against recordings made on the
// pre-refactor build. FfbCaptureLog stays for the lightweight FFB-modeling
// workflow; this one is the fixture format.
//
// Format rules (same discipline as FfbCaptureLog, they are what make the files
// replayable):
//   - First line is a '#tfcap' metadata comment (version, source, game, date);
//     readers skip '#' lines.
//   - Header line is the column contract; keep in sync with FormatRow.
//   - InvariantCulture numbers (decimal point, never comma).
//   - Empty cell = "source didn't provide it" — distinct from zero. The quads
//     write empty when HasTireQuads is false.
//   - t_us preserves the ORIGINAL irregular inter-frame timing (that's what
//     the jitter buffer / resampler get tested against), computed exactly from
//     Stopwatch ticks with no float rounding.
//   - '\n' line endings, UTF-8 without BOM.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrueforceForAll.Core
{
    public sealed class GoldenCaptureLog : IDisposable
    {
        public const int SchemaVersion = 2;

        // Column contract. Keep in sync with FormatRow.
        public const string Header =
            "t_us,session_active,speed_kmh,rpm,max_rpm,throttle,gear,abs,tc,pit,drs,kers," +
            "accel_surge,accel_sway,accel_heave,yaw_deg_s,steer,airborne,collision_mag," +
            "wheel_slip,front_slip_angle_rad,front_susp_travel_m,surface_rumble,on_strip," +
            "slip_ratio_fl,slip_ratio_fr,slip_ratio_rl,slip_ratio_rr," +
            "slip_angle_fl,slip_angle_fr,slip_angle_rl,slip_angle_rr," +
            "combined_fl,combined_fr,combined_rl,combined_rr," +
            "susp_m_fl,susp_m_fr,susp_m_rl,susp_m_rr," +
            "rumble_fl,rumble_fr,rumble_rl,rumble_rr," +
            "wheel_rot_fl,wheel_rot_fr,wheel_rot_rl,wheel_rot_rr," +
            "num_cyl,rpm_pct,redline_rpm,redline_hit,game_ffb";

        // Safety cap so a forgotten-on capture can't fill the disk. Rows are
        // ~3x FfbCaptureLog's, so 150 MB keeps the same ~70 min of driving.
        private const long MaxBytes = 150L * 1024 * 1024;
        private const int FlushEveryRows = 50;

        private readonly object _gate = new object();
        private readonly Action<string> _log;
        private StreamWriter _writer;
        private long _bytesWritten;
        private int _rowsSinceFlush;
        private bool _cappedLogged;

        public string Path { get; }
        public long RowsWritten { get; private set; }
        public bool IsOpen { get; private set; }

        /// <summary>Open (creating/truncating) the CSV at <paramref name="path"/>,
        /// write the metadata comment and header. <paramref name="sourceName"/> /
        /// <paramref name="gameName"/> are free-text provenance for the fixture
        /// library (whatever the active telemetry source / SimHub report).</summary>
        public GoldenCaptureLog(string path, string sourceName, string gameName, Action<string> log = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log ?? (_ => { });

            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            WriteLine(MetaLine(sourceName, gameName, DateTime.UtcNow));
            WriteLine(Header);
            IsOpen = true;
            _log($"[GOLDEN-CAPTURE] logging v{SchemaVersion} to {path}");
        }

        internal static string MetaLine(string sourceName, string gameName, DateTime utcNow)
        {
            // Commas stripped from free-text fields so the '#' line can never
            // confuse a reader that splits before checking for comments.
            string Clean(string s) => string.IsNullOrEmpty(s) ? "unknown" : s.Replace(',', ';');
            return $"#tfcap v={SchemaVersion} src={Clean(sourceName)} game={Clean(gameName)} " +
                   $"recorded={utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}";
        }

        /// <summary>Append one aligned row. <paramref name="sessionActive"/> is
        /// the active source's IsSessionActive at this frame (authoritative
        /// pause signal where the game has one). <paramref name="gameFfb"/> is
        /// the tap's latched game FFB target, or null when nothing fresh.
        /// Silently no-ops once closed or capped, so callers can fire it every
        /// frame without guarding.</summary>
        public void LogRow(in TelemetryFrame f, bool sessionActive, short? gameFfb)
        {
            lock (_gate)
            {
                if (!IsOpen) return;
                if (_bytesWritten >= MaxBytes)
                {
                    if (!_cappedLogged)
                    {
                        _log($"[GOLDEN-CAPTURE] reached {MaxBytes / (1024 * 1024)} MB cap; " +
                             "stopping capture (data kept). Restart capture for a fresh file.");
                        _cappedLogged = true;
                        FlushInternal();
                    }
                    return;
                }

                WriteLine(FormatRow(in f, sessionActive, gameFfb));
                RowsWritten++;
                if (++_rowsSinceFlush >= FlushEveryRows) FlushInternal();
            }
        }

        internal static string FormatRow(in TelemetryFrame f, bool sessionActive, short? gameFfb)
        {
            var sb = new StringBuilder(384);

            // Exact microseconds from Stopwatch ticks: split into whole seconds
            // and remainder so the multiply can't overflow long or round through
            // a double, even days into an uptime.
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long us = (f.CapturedAtTicks / freq) * 1_000_000L
                    + (f.CapturedAtTicks % freq) * 1_000_000L / freq;

            sb.Append(us.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(sessionActive ? '1' : '0').Append(',');
            Num(sb, f.SpeedKmh);               sb.Append(',');
            Num(sb, f.Rpms);                   sb.Append(',');
            Num(sb, f.MaxRpm);                 sb.Append(',');
            Num(sb, f.Throttle01);             sb.Append(',');
            sb.Append(f.Gear ?? "");           sb.Append(',');
            sb.Append(f.AbsActive.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(f.TcActive.ToString(CultureInfo.InvariantCulture)).Append(',');
            IntN(sb, f.PitLimiterActive);      sb.Append(',');
            IntN(sb, f.DrsActive);             sb.Append(',');
            IntN(sb, f.KersActive);            sb.Append(',');

            NumN(sb, f.AccelerationSurge);     sb.Append(',');
            NumN(sb, f.AccelerationSway);      sb.Append(',');
            NumN(sb, f.AccelerationHeave);     sb.Append(',');
            NumN(sb, f.YawRateDegPerSec);      sb.Append(',');
            NumN(sb, f.SteeringAngle);         sb.Append(',');
            BoolN(sb, f.Airborne);             sb.Append(',');
            NumN(sb, f.CollisionMagnitude);    sb.Append(',');

            NumN(sb, f.WheelSlip);             sb.Append(',');
            NumN(sb, f.FrontSlipAngleRad);     sb.Append(',');
            NumN(sb, f.FrontSuspTravelMeters); sb.Append(',');
            NumN(sb, f.SurfaceRumble);         sb.Append(',');
            BoolN(sb, f.OnRumbleStrip);        sb.Append(',');

            Quad(sb, f.HasTireQuads, f.TireSlipRatio);
            Quad(sb, f.HasTireQuads, f.TireSlipAngleRad);
            Quad(sb, f.HasTireQuads, f.TireCombinedSlip);
            Quad(sb, f.HasTireQuads, f.SuspTravelM);
            Quad(sb, f.HasTireQuads, f.SurfaceRumbleQ);
            Quad(sb, f.HasTireQuads, f.WheelRotRadS);

            IntN(sb, f.NumCylinders);          sb.Append(',');
            Num(sb, f.RpmPercent);             sb.Append(',');
            Num(sb, f.RedlineRpm);             sb.Append(',');
            sb.Append(f.RedlineReached ? '1' : '0').Append(',');
            sb.Append(gameFfb.HasValue ? gameFfb.Value.ToString(CultureInfo.InvariantCulture) : "");
            return sb.ToString();
        }

        // 'R' round-trip format keeps full precision without locale commas.
        private static void Num(StringBuilder sb, double v) =>
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        private static void NumN(StringBuilder sb, double? v)
        {
            if (v.HasValue) sb.Append(v.Value.ToString("R", CultureInfo.InvariantCulture));
        }
        private static void IntN(StringBuilder sb, int? v)
        {
            if (v.HasValue) sb.Append(v.Value.ToString(CultureInfo.InvariantCulture));
        }
        private static void BoolN(StringBuilder sb, bool? v)
        {
            if (v.HasValue) sb.Append(v.Value ? '1' : '0');
        }
        private static void Quad(StringBuilder sb, bool has, in TireQuad q)
        {
            if (has)
            {
                Num(sb, q.FL); sb.Append(',');
                Num(sb, q.FR); sb.Append(',');
                Num(sb, q.RL); sb.Append(',');
                Num(sb, q.RR); sb.Append(',');
            }
            else
            {
                sb.Append(",,,,");
            }
        }

        private void WriteLine(string line)
        {
            _writer.Write(line);
            _writer.Write('\n');                 // '\n' not Environment.NewLine: stable across OSes
            _bytesWritten += line.Length + 1;
        }

        private void FlushInternal()
        {
            try { _writer.Flush(); } catch (Exception ex) { _log($"[GOLDEN-CAPTURE] flush failed: {ex.Message}"); }
            _rowsSinceFlush = 0;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!IsOpen) return;
                IsOpen = false;
                try { _writer?.Flush(); } catch { }
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                _log($"[GOLDEN-CAPTURE] closed {Path} ({RowsWritten} rows).");
            }
        }
    }
}
