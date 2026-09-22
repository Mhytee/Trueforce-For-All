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
            I32(R3ESharedMemoryReader.OFF_ENGINE_TYPE, 1);   // ELECTRIC
            I32(R3ESharedMemoryReader.OFF_CONTROL_TYPE, 0);
            F32(R3ESharedMemoryReader.OFF_CAR_SPEED, 31.5f);
            I32(R3ESharedMemoryReader.OFF_GEAR, 4);
            F32(R3ESharedMemoryReader.OFF_STEER_INPUT_RAW, 0.32f);
            I32(R3ESharedMemoryReader.OFF_STEER_LOCK_DEG, 17);
            I32(R3ESharedMemoryReader.OFF_STEER_WHEEL_RANGE_DEG, 350);
            I32(R3ESharedMemoryReader.OFF_STEER_WHEEL_MAX_ROTATION, 1080);
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
            Assert.Equal(17, s.RoadWheelLockDeg);
            Assert.Equal(350, s.CarRotationDeg);
            Assert.Equal(1080, s.WheelMaxRotationDeg);
            Assert.Equal(4, s.Gear);
            Assert.Equal(4523, s.ModelId);
            Assert.Equal(1, s.EngineType);
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
            Assert.Equal(1268, R3ESharedMemoryReader.OFF_MODEL_ID);
            // VehicleInfo (1196) + DriverInfo.EngineType (100). Both halves come
            // from marshaling the struct the shipped SimHub reader uses, the
            // same way ModelId's 1268 was established.
            Assert.Equal(1296, R3ESharedMemoryReader.OFF_ENGINE_TYPE);
            Assert.Equal(1388, R3ESharedMemoryReader.OFF_CONTROL_TYPE);
            Assert.Equal(1524, R3ESharedMemoryReader.OFF_STEER_INPUT_RAW);
            // Straight after steer_input_raw in r3e.h (layout 3.5): the road
            // wheel's lock, then the car's steering wheel rotation. Both
            // r3e_int32, Pack = 1, so 1528 and 1532. Then aid_settings (5),
            // drs (4), pit_limiter, push_to_pass (5), brake_bias, two counters,
            // battery_soc, water_left, abs_setting, headlights: 22 four-byte
            // scalars, so steer_wheel_max_rotation is at 1536 + 88 = 1624.
            Assert.Equal(1528, R3ESharedMemoryReader.OFF_STEER_LOCK_DEG);
            Assert.Equal(1532, R3ESharedMemoryReader.OFF_STEER_WHEEL_RANGE_DEG);
            Assert.Equal(1624, R3ESharedMemoryReader.OFF_STEER_WHEEL_MAX_ROTATION);
            Assert.True(R3ESharedMemoryReader.BlockBytes
                >= R3ESharedMemoryReader.OFF_STEER_WHEEL_MAX_ROTATION + sizeof(int));
        }
    }
}
