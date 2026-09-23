// Aligned telemetry + game-FFB capture log (CSV).
//
// Purpose: build the dataset for remodeling FFB. On each telemetry frame we
// already have the physics (slip angle, suspension load, lateral accel, ...);
// we also sample the game's own FFB target that the USBPcap tap latched at the
// same instant. Writing the two side by side lets us see, offline, exactly how
// a game's stock FFB relates to its physics, e.g. confirm that Forza's force
// barely tracks front slip-angle saturation, which is the gap a self-aligning-
// torque model fills. No force is produced here; this is pure observation, so
// it's safe to run on a live wheel while driving normally.
//
// One row per telemetry frame. Columns are stable and documented in the header
// so a notebook / spreadsheet can load it with no out-of-band schema. Numbers
// are written InvariantCulture (decimal point, never comma) so the CSV parses
// the same regardless of the machine's locale. Nullable channels write an
// empty cell when the source didn't provide them (e.g. paused frames), so the
// analysis can tell "not provided" from "zero".

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrueforceForAll.Core
{
    public sealed class FfbCaptureLog : IDisposable
    {
        // Header is also the column contract for downstream analysis. Keep in
        // sync with FormatRow.
        public const string Header =
            "t_ms,is_active,speed_kmh,rpm,front_slip_angle_rad,front_susp_travel_m," +
            "lat_accel_ms2,yaw_deg_s,wheel_slip,surface_rumble,steer,game_ffb";

        // Safety cap so a forgotten-on capture can't fill the disk. At ~150 Hz
        // a row is ~80 bytes, so 50 MB is ~70 minutes of continuous driving.
        private const long MaxBytes = 50L * 1024 * 1024;
        // Flush cadence: often enough that a crash loses < ~0.3 s of data at
        // Forza's frame rate, rare enough that we're not fsync-bound per frame.
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

        /// <summary>Open (creating/truncating) the CSV at <paramref name="path"/>
        /// and write the header. Throws on an unopenable path so the caller can
        /// surface a clear error rather than silently capturing nothing.</summary>
        public FfbCaptureLog(string path, Action<string> log = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log ?? (_ => { });

            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // UTF-8 without BOM; a leading BOM trips some naive CSV readers.
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            WriteLine(Header);
            IsOpen = true;
            _log($"[FFB-CAPTURE] logging to {path}");
        }

        /// <summary>Append one aligned row. <paramref name="gameFfb"/> is the
        /// game's FFB target the tap latched at this frame (the same signed
        /// int16 scale the rest of the pipeline uses), or null when the tap had
        /// nothing fresh. Silently no-ops once closed or once the size cap is
        /// hit, so callers can fire it every frame without guarding.</summary>
        public void LogRow(in TelemetryFrame f, short? gameFfb)
        {
            lock (_gate)
            {
                if (!IsOpen) return;
                if (_bytesWritten >= MaxBytes)
                {
                    if (!_cappedLogged)
                    {
                        _log($"[FFB-CAPTURE] reached {MaxBytes / (1024 * 1024)} MB cap; " +
                             "stopping capture (data kept). Restart capture for a fresh file.");
                        _cappedLogged = true;
                        FlushInternal();
                    }
                    return;
                }

                WriteLine(FormatRow(in f, gameFfb));
                RowsWritten++;
                if (++_rowsSinceFlush >= FlushEveryRows) FlushInternal();
            }
        }

        // Build one CSV line. Active = the frame carries live physics (we use
        // FrontSlipAngleRad having a value as the "on track / not paused" signal,
        // since the Forza source nulls the SAT channels when IsRaceOn=0).
        internal static string FormatRow(in TelemetryFrame f, short? gameFfb)
        {
            var sb = new StringBuilder(96);
            long ms = f.CapturedAtTicks * 1000L / System.Diagnostics.Stopwatch.Frequency;
            bool active = f.FrontSlipAngleRad.HasValue;

            sb.Append(ms.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(active ? '1' : '0').Append(',');
            Num(sb, f.SpeedKmh);                 sb.Append(',');
            Num(sb, f.Rpms);                     sb.Append(',');
            NumN(sb, f.FrontSlipAngleRad);       sb.Append(',');
            NumN(sb, f.FrontSuspTravelMeters);   sb.Append(',');
            NumN(sb, f.AccelerationSway);        sb.Append(',');
            NumN(sb, f.YawRateDegPerSec);        sb.Append(',');
            NumN(sb, f.WheelSlip);               sb.Append(',');
            NumN(sb, f.SurfaceRumble);           sb.Append(',');
            NumN(sb, f.SteeringAngle);           sb.Append(',');
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

        private void WriteLine(string line)
        {
            _writer.Write(line);
            _writer.Write('\n');                 // '\n' not Environment.NewLine: stable across OSes
            _bytesWritten += line.Length + 1;
        }

        private void FlushInternal()
        {
            try { _writer.Flush(); } catch (Exception ex) { _log($"[FFB-CAPTURE] flush failed: {ex.Message}"); }
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
                _log($"[FFB-CAPTURE] closed {Path} ({RowsWritten} rows).");
            }
        }
    }
}
