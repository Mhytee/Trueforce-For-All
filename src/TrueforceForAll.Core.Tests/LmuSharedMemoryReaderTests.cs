using System;
using System.Text;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Guards the byte offsets the Le Mans Ultimate reader extracts from the
    // "LMU_Data" block. The numbers come from compiling offsetof() against the
    // sim's own SDK headers (Support\SharedMemoryInterface, pack(4)) on
    // 2026-09-20. If a field ever reads garbage on a rig, this test is where
    // the assumed layout lives.
    public class LmuSharedMemoryReaderTests
    {
        private static byte[] Telem()
        {
            var b = new byte[LmuSharedMemoryReader.TELEM_STRIDE];
            void I32(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
            void F32(int off, float v) => BitConverter.GetBytes(v).CopyTo(b, off);
            void F64(int off, double v) => BitConverter.GetBytes(v).CopyTo(b, off);

            I32(LmuSharedMemoryReader.TI_ID, 7);
            F64(LmuSharedMemoryReader.TI_DELTA_TIME, 0.0025);
            F64(LmuSharedMemoryReader.TI_ELAPSED_TIME, 812.5);
            Encoding.ASCII.GetBytes("Porsche 963\0").CopyTo(b, LmuSharedMemoryReader.TI_VEHICLE_NAME);
            F64(LmuSharedMemoryReader.TI_LOCAL_VEL, 3.0);
            F64(LmuSharedMemoryReader.TI_LOCAL_VEL + 8, 0.0);
            F64(LmuSharedMemoryReader.TI_LOCAL_VEL + 16, -4.0);
            I32(LmuSharedMemoryReader.TI_GEAR, 5);
            F64(LmuSharedMemoryReader.TI_ENGINE_RPM, 7450.0);
            F64(LmuSharedMemoryReader.TI_STEER_UNFILTERED, -0.35);
            F64(LmuSharedMemoryReader.TI_STEER_FILTERED, -0.33);
            F64(LmuSharedMemoryReader.TI_SHAFT_TORQUE, -6.75);
            F32(LmuSharedMemoryReader.TI_VISUAL_RANGE, 540f);
            F32(LmuSharedMemoryReader.TI_PHYSICAL_RANGE, 900f);
            return b;
        }

        [Fact]
        public void ParseTelem_ReadsEveryFieldFromItsOffset()
        {
            var b = Telem();
            var s = LmuSharedMemoryReader.ParseTelem(b);

            Assert.Equal(7, s.SlotId);
            Assert.Equal(0.0025, s.DeltaTime, 10);
            Assert.Equal(812.5, s.ElapsedTime, 10);
            Assert.Equal(-6.75, s.ShaftTorqueNm, 10);
            Assert.Equal(-0.35f, s.SteerUnfiltered, 4);
            Assert.Equal(-0.33f, s.SteerFiltered, 4);
            Assert.Equal(540f, s.VisualRangeDeg, 3);
            Assert.Equal(900f, s.PhysicalRangeDeg, 3);
            Assert.Equal(5f, s.SpeedMps, 4);      // |(3, 0, -4)|
            Assert.Equal(5, s.Gear);
            Assert.Equal(7450f, s.Rpm, 2);
            Assert.Equal("Porsche 963", LmuSharedMemoryReader.ReadCString(b, LmuSharedMemoryReader.TI_VEHICLE_NAME, 64));
        }

        [Fact]
        public void ParseVehicleScoring_ReadsThePlayerStateFields()
        {
            var b = new byte[LmuSharedMemoryReader.VEH_SCORING_STRIDE];
            BitConverter.GetBytes(7).CopyTo(b, LmuSharedMemoryReader.VS_ID);
            b[LmuSharedMemoryReader.VS_IS_PLAYER] = 1;
            b[LmuSharedMemoryReader.VS_CONTROL] = unchecked((byte)(sbyte)2);
            b[LmuSharedMemoryReader.VS_IN_PITS] = 1;
            b[LmuSharedMemoryReader.VS_IN_GARAGE_STALL] = 0;

            var r = LmuSharedMemoryReader.ParseVehicleScoring(b);
            Assert.Equal(7, r.SlotId);
            Assert.True(r.IsPlayer);
            Assert.Equal(2, r.Control);
            Assert.True(r.InPits);
            Assert.False(r.InGarageStall);

            b[LmuSharedMemoryReader.VS_CONTROL] = unchecked((byte)(sbyte)-1);
            Assert.Equal(-1, LmuSharedMemoryReader.ParseVehicleScoring(b).Control);
        }

        [Fact]
        public void Offsets_MatchTheCompiledLayout()
        {
            // The absolute positions the compiler reported for the sim's
            // headers, spelled out so a constant edit cannot silently shift a
            // field: generic at 0 (events[16] then gameVersion then FFBTorque),
            // scoring at 1632, telemetry at 128464 with a 4-byte header.
            Assert.Equal(324824, LmuSharedMemoryReader.LayoutBytes);
            Assert.Equal(64, LmuSharedMemoryReader.OFF_GAME_VERSION);
            Assert.Equal(68, LmuSharedMemoryReader.OFF_FFB_TORQUE);
            Assert.Equal(1736, LmuSharedMemoryReader.OFF_NUM_VEHICLES);
            Assert.Equal(1740, LmuSharedMemoryReader.OFF_GAME_PHASE);
            Assert.Equal(1747, LmuSharedMemoryReader.OFF_IN_REALTIME);
            Assert.Equal(2192, LmuSharedMemoryReader.OFF_VEH_SCORING);
            Assert.Equal(584, LmuSharedMemoryReader.VEH_SCORING_STRIDE);
            Assert.Equal(128465, LmuSharedMemoryReader.OFF_PLAYER_VEHICLE_IDX);
            Assert.Equal(128466, LmuSharedMemoryReader.OFF_PLAYER_HAS_VEHICLE);
            Assert.Equal(128468, LmuSharedMemoryReader.OFF_TELEM_INFO);
            Assert.Equal(1888, LmuSharedMemoryReader.TELEM_STRIDE);
            // Every vehicle's block fits inside the layout: the last one ends
            // four bytes short of it (the telemetry struct's tail padding,
            // which the compiler reported as 324824 against a 324820 sum).
            int lastEnd = LmuSharedMemoryReader.OFF_TELEM_INFO
                + LmuSharedMemoryReader.MaxVehicles * LmuSharedMemoryReader.TELEM_STRIDE;
            Assert.Equal(324820, lastEnd);
            Assert.True(lastEnd <= LmuSharedMemoryReader.LayoutBytes);
            Assert.Equal(452, LmuSharedMemoryReader.TI_SHAFT_TORQUE);
            Assert.Equal(692, LmuSharedMemoryReader.TI_PHYSICAL_RANGE);
            Assert.Equal(660, LmuSharedMemoryReader.TI_VISUAL_RANGE);
        }
    }
}
