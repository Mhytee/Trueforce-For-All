using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Guards the byte offsets the RaceRoom shared-memory reader extracts from
    // the "$R3E" block. The offsets come from the public r3e-api Shared struct
    // (Pack = 1, layout major version 3), verified 2026-08-31 by marshaling the
    // same struct the shipped SimHub RaceRoom reader uses. If a field ever
    // reads garbage on a rig, this test is where the assumed layout lives.
    public class R3ESharedMemoryReaderTests
    {
        private static byte[] Block()
        {
            var b = new byte[R3ESharedMemoryReader.BlockBytes];
            void I32(int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
            void F32(int off, float v) => BitConverter.GetBytes(v).CopyTo(b, off);
            void F64(int off, double v) => BitConverter.GetBytes(v).CopyTo(b, off);

            I32(R3ESharedMemoryReader.OFF_VERSION_MAJOR, 3);
            I32(R3ESharedMemoryReader.OFF_VERSION_MINOR, 5);
            I32(R3ESharedMemoryReader.OFF_GAME_PAUSED, 1);
            I32(R3ESharedMemoryReader.OFF_GAME_IN_MENUS, 0);
            I32(R3ESharedMemoryReader.OFF_GAME_IN_REPLAY, 1);
            I32(R3ESharedMemoryReader.OFF_IN_GARAGE, 0);
            I32(R3ESharedMemoryReader.OFF_SIM_TICKS, 123456);
            F64(R3ESharedMemoryReader.OFF_SIM_TIME, 87.5);
            F64(R3ESharedMemoryReader.OFF_STEERING_FORCE, -3.25);
            F64(R3ESharedMemoryReader.OFF_STEERING_PCT, -0.41);
            I32(R3ESharedMemoryReader.OFF_MODEL_ID, 4523);
            I32(R3ESharedMemoryReader.OFF_CONTROL_TYPE, 0);
            F32(R3ESharedMemoryReader.OFF_CAR_SPEED, 31.5f);
            I32(R3ESharedMemoryReader.OFF_GEAR, 4);
            F32(R3ESharedMemoryReader.OFF_STEER_INPUT_RAW, 0.32f);
            return b;
        }

        [Fact]
        public void Parse_ReadsEveryFieldFromItsOffset()
        {
            var s = R3ESharedMemoryReader.Parse(Block());

            Assert.Equal(123456, s.SimTicks);
            Assert.Equal(87.5, s.SimTimeSeconds, 10);
            Assert.Equal(-3.25, s.SteeringForce, 10);
            Assert.Equal(-0.41, s.SteeringForcePct, 10);
            Assert.Equal(0, s.ControlType);
            Assert.True(s.GamePaused);
            Assert.False(s.GameInMenus);
            Assert.True(s.GameInReplay);
            Assert.False(s.InGarage);
            Assert.Equal(31.5f, s.CarSpeedMps, 4);
            Assert.Equal(0.32f, s.SteerInputRaw, 4);
            Assert.Equal(4, s.Gear);
            Assert.Equal(4523, s.ModelId);
        }

        [Fact]
        public void Offsets_MatchTheR3eApiLayout()
        {
            // The absolute positions, spelled out so a constant edit cannot
            // silently shift a field: Player starts at 40, SteeringForce is
            // 280 bytes into PlayerData, ControlType follows the 64-byte
            // PlayerName that starts at 1324.
            Assert.Equal(44, R3ESharedMemoryReader.OFF_SIM_TICKS);
            Assert.Equal(320, R3ESharedMemoryReader.OFF_STEERING_FORCE);
            Assert.Equal(328, R3ESharedMemoryReader.OFF_STEERING_PCT);
            Assert.Equal(1388, R3ESharedMemoryReader.OFF_CONTROL_TYPE);
            Assert.Equal(1524, R3ESharedMemoryReader.OFF_STEER_INPUT_RAW);
            Assert.True(R3ESharedMemoryReader.BlockBytes
                > R3ESharedMemoryReader.OFF_STEER_INPUT_RAW + sizeof(float));
        }
    }
}
