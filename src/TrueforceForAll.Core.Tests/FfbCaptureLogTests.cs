using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Tests the aligned telemetry + game-FFB capture log: the CSV row format
    // (the column contract downstream analysis depends on) and the file
    // round-trip. FormatRow is internal, exercised directly via InternalsVisibleTo.
    public class FfbCaptureLogTests
    {
        // A populated "driving" frame with a known capture tick so t_ms is
        // deterministic: ticks == frequency -> exactly 1000 ms.
        private static TelemetryFrame DrivingFrame() => new TelemetryFrame
        {
            CapturedAtTicks       = Stopwatch.Frequency,   // -> 1000 ms
            SpeedKmh              = 108.0,
            Rpms                  = 5000.0,
            FrontSlipAngleRad     = 0.12,
            FrontSuspTravelMeters = 0.05,
            AccelerationSway      = 3.5,
            YawRateDegPerSec      = 10.0,
            WheelSlip             = 0.7,
            SurfaceRumble         = 0.2,
            SteeringAngle         = 0.25,
        };

        private static string[] Cols(string row) => row.Split(',');
        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        [Fact]
        public void FormatRow_WritesAllColumns_InOrder()
        {
            var cols = Cols(FfbCaptureLog.FormatRow(DrivingFrame(), (short)1234));

            Assert.Equal(12, cols.Length);                 // matches Header column count
            Assert.Equal("1000", cols[0]);                 // t_ms
            Assert.Equal("1", cols[1]);                    // is_active (slip angle present)
            Assert.Equal(108.0, D(cols[2]), 6);            // speed_kmh
            Assert.Equal(5000.0, D(cols[3]), 6);           // rpm
            Assert.Equal(0.12, D(cols[4]), 6);             // front_slip_angle_rad
            Assert.Equal(0.05, D(cols[5]), 6);             // front_susp_travel_m
            Assert.Equal(3.5, D(cols[6]), 6);              // lat_accel
            Assert.Equal(0.7, D(cols[8]), 6);              // wheel_slip
            Assert.Equal(0.25, D(cols[10]), 6);            // steer
            Assert.Equal("1234", cols[11]);                // game_ffb
        }

        [Fact]
        public void Header_ColumnCount_MatchesRow()
        {
            Assert.Equal(FfbCaptureLog.Header.Split(',').Length,
                         Cols(FfbCaptureLog.FormatRow(DrivingFrame(), (short)0)).Length);
        }

        [Fact]
        public void FormatRow_NullChannels_AreEmptyCells_AndInactive()
        {
            // A paused frame: SAT channels null, no fresh game FFB. Empty cells
            // let analysis distinguish "not provided" from "zero".
            var paused = new TelemetryFrame { CapturedAtTicks = 0, SpeedKmh = 0, Rpms = 0 };
            var cols = Cols(FfbCaptureLog.FormatRow(paused, null));

            Assert.Equal("0", cols[1]);    // is_active
            Assert.Equal("", cols[4]);     // front_slip_angle_rad (null)
            Assert.Equal("", cols[5]);     // front_susp_travel_m (null)
            Assert.Equal("", cols[11]);    // game_ffb (null)
        }

        [Fact]
        public void FormatRow_UsesInvariantDecimalPoint()
        {
            var f = DrivingFrame();
            f.FrontSlipAngleRad = 1.5;
            var col = Cols(FfbCaptureLog.FormatRow(f, (short)0))[4];

            Assert.Contains(".", col);          // never a locale comma
            Assert.DoesNotContain(",", col);    // (would have split into another column anyway)
            Assert.Equal(1.5, D(col), 6);
        }

        [Fact]
        public void File_RoundTrips_HeaderThenRows()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tf4all_capture_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (var log = new FfbCaptureLog(path))
                {
                    log.LogRow(DrivingFrame(), (short)100);
                    log.LogRow(DrivingFrame(), (short)-100);
                    Assert.Equal(2, log.RowsWritten);
                }   // dispose flushes + closes

                var lines = File.ReadAllLines(path);
                Assert.Equal(3, lines.Length);                 // header + 2 rows
                Assert.Equal(FfbCaptureLog.Header, lines[0]);
                Assert.Equal("100", Cols(lines[1])[11]);
                Assert.Equal("-100", Cols(lines[2])[11]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void LogRow_AfterDispose_IsNoOp()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tf4all_capture_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var log = new FfbCaptureLog(path);
                log.Dispose();
                log.LogRow(DrivingFrame(), (short)0);          // must not throw
                Assert.Equal(0, log.RowsWritten);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
