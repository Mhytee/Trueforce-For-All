using System;
using System.Threading;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Boundary sanitizer regression (2026-07-05 review finding): a corrupt
    // UDP frame carrying NaN/Infinity in any sled float must parse to finite
    // values, because one NaN reaching the Mode B / effect EMA chains poisons
    // them for the rest of the session (ema += (NaN - ema) * a never
    // recovers) and Mode B is FM8's only force source.
    public class NanSanitizerTests
    {
        private const int Fm8Length = 331;
        private const int OFF_IS_RACE_ON  = 0;
        private const int OFF_ENGINE_MAX  = 8;
        private const int OFF_CURRENT_RPM = 16;
        private const int OFF_ACCEL_X     = 20;
        private const int OFF_COMBINED_FL = 180;
        private const int OFF_SLIP_ANGLE_FL = 164;

        private static void PutFloat(byte[] b, int off, float v) =>
            BitConverter.GetBytes(v).CopyTo(b, off);

        [Fact]
        public void CorruptFloats_ParseAsFiniteZero_NeverNaN()
        {
            var b = new byte[Fm8Length];
            BitConverter.GetBytes(1).CopyTo(b, OFF_IS_RACE_ON);
            PutFloat(b, OFF_ENGINE_MAX, 8000f);
            PutFloat(b, OFF_CURRENT_RPM, float.NaN);
            PutFloat(b, OFF_ACCEL_X, float.PositiveInfinity);
            PutFloat(b, OFF_COMBINED_FL, float.NaN);            // FL combined slip
            PutFloat(b, OFF_COMBINED_FL + 4, float.NegativeInfinity);
            PutFloat(b, OFF_SLIP_ANGLE_FL, float.NaN);

            var src = new ForzaUdpTelemetrySource(5300);
            src.ParsePacket(b, Fm8Length);
            Thread.Sleep(450);                                   // spend the settle window
            var f = src.ParsePacket(b, Fm8Length);

            AssertFinite(f.Rpms, "Rpms");
            AssertFinite(f.AccelerationSway ?? 0, "Sway");
            AssertFinite(f.TireCombinedSlip.FL, "combined FL");
            AssertFinite(f.TireCombinedSlip.FR, "combined FR");
            AssertFinite(f.FrontSlipAngleRad ?? 0, "slip angle");

            // And the CTM stays clean downstream of a corrupt frame.
            CtmComposer.Compose(ref f);
            if (f.FrontGrip01.HasValue)
            {
                AssertFinite(f.FrontGrip01.Value, "FrontGrip01");
                AssertFinite(f.RearGrip01.Value, "RearGrip01");
            }
        }

        private static void AssertFinite(double v, string name)
            => Assert.False(double.IsNaN(v) || double.IsInfinity(v), $"{name} not finite: {v}");
    }
}
