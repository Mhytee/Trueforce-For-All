// Reader for GoldenCaptureLog v2 fixtures: the inverse of FormatRow. Parses a
// '#tfcap' capture CSV back into timestamped TelemetryFrames so the replay
// harness (and the future replay-to-wheel mode) can feed recordings through
// the engine. Column lookup is header-driven, not positional, so fixtures
// survive future column additions; unknown columns are ignored, missing ones
// read as null/default. Empty cell = null, same contract as the writer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TrueforceForAll.Core
{
    public sealed class ReplayRow
    {
        public long TUs;                 // original capture timestamp, microseconds
        public bool SessionActive;
        public short? GameFfb;
        public TelemetryFrame Frame;     // CapturedAtTicks NOT set; the replay source stamps it
    }

    public static class CsvFrameReader
    {
        public sealed class Capture
        {
            public int SchemaVersion;
            public string SourceName = "unknown";
            public string GameName   = "unknown";
            public List<ReplayRow> Rows = new List<ReplayRow>();
        }

        public static Capture ReadFile(string path)
        {
            using (var reader = new StreamReader(path))
                return Read(reader);
        }

        public static Capture Read(TextReader reader)
        {
            var cap = new Capture();
            string line = reader.ReadLine();

            // Metadata comment(s). '#tfcap v=2 src=... game=... recorded=...'
            while (line != null && line.StartsWith("#", StringComparison.Ordinal))
            {
                if (line.StartsWith("#tfcap", StringComparison.Ordinal))
                {
                    foreach (var tok in line.Split(' '))
                    {
                        if (tok.StartsWith("v=", StringComparison.Ordinal) &&
                            int.TryParse(tok.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                            cap.SchemaVersion = v;
                        else if (tok.StartsWith("src=", StringComparison.Ordinal))
                            cap.SourceName = tok.Substring(4);
                        else if (tok.StartsWith("game=", StringComparison.Ordinal))
                            cap.GameName = tok.Substring(5);
                    }
                }
                line = reader.ReadLine();
            }

            if (line == null) throw new InvalidDataException("capture has no header line");
            var col = BuildColumnMap(line);

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                cap.Rows.Add(ParseRow(line.Split(','), col));
            }
            return cap;
        }

        private static Dictionary<string, int> BuildColumnMap(string headerLine)
        {
            var names = headerLine.Split(',');
            var map = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) map[names[i]] = i;
            return map;
        }

        private static ReplayRow ParseRow(string[] cells, Dictionary<string, int> col)
        {
            string Cell(string name)
            {
                return col.TryGetValue(name, out int i) && i < cells.Length ? cells[i] : "";
            }
            double D(string name)      { var c = Cell(name); return c.Length == 0 ? 0.0 : double.Parse(c, CultureInfo.InvariantCulture); }
            double? DN(string name)    { var c = Cell(name); return c.Length == 0 ? (double?)null : double.Parse(c, CultureInfo.InvariantCulture); }
            int I(string name)         { var c = Cell(name); return c.Length == 0 ? 0 : int.Parse(c, CultureInfo.InvariantCulture); }
            int? IN(string name)       { var c = Cell(name); return c.Length == 0 ? (int?)null : int.Parse(c, CultureInfo.InvariantCulture); }
            bool? BN(string name)      { var c = Cell(name); return c.Length == 0 ? (bool?)null : c == "1"; }
            bool B(string name)        { return Cell(name) == "1"; }
            long L(string name)        { var c = Cell(name); return c.Length == 0 ? 0L : long.Parse(c, CultureInfo.InvariantCulture); }

            bool hasQuads = Cell("slip_ratio_fl").Length != 0;
            TireQuad Q(string prefix)
            {
                if (!hasQuads) return default;
                return TireQuad.Of(D(prefix + "_fl"), D(prefix + "_fr"), D(prefix + "_rl"), D(prefix + "_rr"));
            }

            var frame = new TelemetryFrame
            {
                SpeedKmh              = D("speed_kmh"),
                Rpms                  = D("rpm"),
                MaxRpm                = D("max_rpm"),
                Throttle01            = D("throttle"),
                Gear                  = Cell("gear"),
                AbsActive             = I("abs"),
                TcActive              = I("tc"),
                PitLimiterActive      = IN("pit"),
                DrsActive             = IN("drs"),
                KersActive            = IN("kers"),
                AccelerationSurge     = DN("accel_surge"),
                AccelerationSway      = DN("accel_sway"),
                AccelerationHeave     = DN("accel_heave"),
                YawRateDegPerSec      = DN("yaw_deg_s"),
                SteeringAngle         = DN("steer"),
                Airborne              = BN("airborne"),
                CollisionMagnitude    = DN("collision_mag"),
                WheelSlip             = DN("wheel_slip"),
                FrontSlipAngleRad     = DN("front_slip_angle_rad"),
                FrontSuspTravelMeters = DN("front_susp_travel_m"),
                SurfaceRumble         = DN("surface_rumble"),
                OnRumbleStrip         = BN("on_strip"),
                NumCylinders          = IN("num_cyl"),
                RpmPercent            = D("rpm_pct"),
                RedlineRpm            = D("redline_rpm"),
                RedlineReached        = B("redline_hit"),
                HasTireQuads          = hasQuads,
                TireSlipRatio         = Q("slip_ratio"),
                TireSlipAngleRad      = Q("slip_angle"),
                TireCombinedSlip      = Q("combined"),
                SuspTravelM           = Q("susp_m"),
                SurfaceRumbleQ        = Q("rumble"),
                WheelRotRadS          = Q("wheel_rot"),
            };

            string ffbCell = Cell("game_ffb");
            return new ReplayRow
            {
                TUs           = L("t_us"),
                SessionActive = B("session_active"),
                GameFfb       = ffbCell.Length == 0 ? (short?)null : short.Parse(ffbCell, CultureInfo.InvariantCulture),
                Frame         = frame,
            };
        }
    }
}
