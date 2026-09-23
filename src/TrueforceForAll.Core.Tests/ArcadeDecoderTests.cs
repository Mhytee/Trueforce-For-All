using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // Locks in the arcade decode guards and sign conventions.
    //
    // These matter more than most tests in this project because they cannot be
    // checked any other way: every game here except Initial D is one neither the
    // owner nor anyone testing this has a cabinet for, so a wrong guard would
    // ship silently and read as "the wheel does nothing" or, worse, as a
    // full-scale jolt from a stale memory slot.
    //
    // Sign convention throughout: positive strength pushes the wheel RIGHT. The
    // reference names its direction constants for what GENERATES the force, so
    // DIRECTION_FROM_LEFT means the wheel goes right.
    public class ArcadeDecoderTests
    {
        private static byte[] Block() => new byte[TeknoParrotJvsTelemetrySource.BlockBytes];

        private static void PutSlot(byte[] b, int index, int value)
        {
            int off = index * 4;
            b[off]     = (byte)(value & 0xff);
            b[off + 1] = (byte)((value >> 8) & 0xff);
            b[off + 2] = (byte)((value >> 16) & 0xff);
            b[off + 3] = (byte)((value >> 24) & 0xff);
        }

        private static uint Dword(byte[] b, int index)
        {
            int off = index * 4;
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        // ---- Initial D family ----

        // The single most dangerous mistake in this decode: command 0x00 carries
        // a literal 0x01 marker, not a level. Reading it as a magnitude gives
        // 1/127 of full scale, which is silence where the cabinet asked for a
        // full-strength centring spring.
        [Fact]
        public void InitialD_FixedSpringCommand_IsFullStrength_NotOneOver127()
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, 0x000001);            // cmd 0x00, param 0x00, mag 0x01
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(ArcadeTriggerKind.Spring, st.Spring.Kind);
            Assert.Equal(1.0, st.Spring.Strength, 6);
        }

        // Same command with any other magnitude byte is not a command at all.
        [Fact]
        public void InitialD_FixedSpringCommand_WithoutMarker_IsRejected()
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, 0x000040);
            Assert.False(d.TryDecode(b, Dword(b, 2), new ArcadeFfbState()));
        }

        // The two constant-force directions do NOT share a magnitude scale. The
        // one that pushes right uses (128 - mag), so a low byte is a strong
        // force. Treating it as a sign flip makes the car pull harder one way.
        [Theory]
        [InlineData(0x01, 0x00,  1.0)]           // from-left, low byte  -> full RIGHT
        [InlineData(0x7f, 0x00,  0.007874)]      // from-left, high byte -> nearly nothing
        [InlineData(0x7f, 0x01, -1.0)]           // from-right, high byte -> full LEFT
        [InlineData(0x01, 0x01, -0.007874)]      // from-right, low byte -> nearly nothing
        public void InitialD_ConstantForce_ScalesAsymmetricallyByDirection(int mag, int param, double expected)
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, (0x04 << 16) | (param << 8) | mag);
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(ArcadeTriggerKind.Constant, st.Constant.Kind);
            Assert.Equal(expected, st.Constant.Strength, 5);
        }

        // A direction byte the game never sends is not a command.
        [Fact]
        public void InitialD_ConstantForce_UnknownDirectionByte_IsRejected()
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, (0x04 << 16) | (0x07 << 8) | 0x40);
            Assert.False(d.TryDecode(b, Dword(b, 2), new ArcadeFfbState()));
        }

        // The period byte is scaled onto 0..120 ms, not passed through. Getting
        // this wrong changes the rumble frequency by a factor of two.
        [Theory]
        [InlineData(0x7f, 120)]
        [InlineData(0x40, 60)]
        public void InitialD_SinePeriod_IsScaledToMilliseconds(int param, int expectedMs)
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, (0x05 << 16) | (param << 8) | 0x40);
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(ArcadeTriggerKind.Sine, st.Periodic.Kind);
            Assert.Equal(expectedMs, st.Periodic.PeriodMs);
        }

        // Both bytes are guarded: a zero in either means no effect, and a zero
        // period would render silently in the engine.
        [Theory]
        [InlineData(0x00, 0x40)]
        [InlineData(0x40, 0x00)]
        public void InitialD_Sine_RejectsZeroInEitherByte(int param, int mag)
        {
            var d = ArcadeDecoders.ForGameId(18);
            var b = Block();
            PutSlot(b, 2, (0x05 << 16) | (param << 8) | mag);
            Assert.False(d.TryDecode(b, Dword(b, 2), new ArcadeFfbState()));
        }

        [Fact]
        public void InitialD_SixSevenAndEight_ShareOneDecoder()
        {
            Assert.NotNull(ArcadeDecoders.ForGameId(8));
            Assert.NotNull(ArcadeDecoders.ForGameId(17));
            Assert.NotNull(ArcadeDecoders.ForGameId(18));
        }

        [Fact]
        public void UnknownGameId_HasNoDecoder_RatherThanAWrongOne()
        {
            // Guessing another game's protocol would put invented force on the
            // wheel, so null is the correct answer here.
            Assert.Null(ArcadeDecoders.ForGameId(9999));
        }

        // ---- Sega Rally 3: whole int, inverted magnitude ----

        [Theory]
        [InlineData(16,  1.0)]        // strongest right
        [InlineData(30,  0.066667)]   // weakest right
        [InlineData(1,  -1.0)]        // strongest left
        [InlineData(15, -0.066667)]   // weakest left
        public void SegaRally3_InvertsMagnitudeInBothBands(int ff, double expected)
        {
            var d = ArcadeDecoders.ForGameId(6);
            var b = Block();
            PutSlot(b, 2, ff);
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(expected, st.Constant.Strength, 5);
        }

        // Zero is the only idle guard the game has, and values past the encoding
        // range go negative in the reference and saturate to full force there.
        [Theory]
        [InlineData(0)]
        [InlineData(31)]
        [InlineData(9999)]
        public void SegaRally3_RejectsIdleAndOutOfRange(int ff)
        {
            var d = ArcadeDecoders.ForGameId(6);
            var b = Block();
            PutSlot(b, 2, ff);
            Assert.False(d.TryDecode(b, Dword(b, 2), new ArcadeFfbState()));
        }

        // ---- Ford Racing: signed windows ----

        // Read this slot unsigned and the negative window never matches, which is
        // the quiet way to lose half this game's force feedback.
        [Fact]
        public void FordRacing_NegativeWindow_RequiresSignedRead()
        {
            var d = ArcadeDecoders.ForGameId(7);
            var b = Block();
            PutSlot(b, 2, -65514);            // strongest in the negative window
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(-1.0, st.Constant.Strength, 5);
        }

        // The positive window is 15 wide but divides by 9, so it legitimately
        // exceeds full scale below ff==7. The reference saturates; we clamp.
        [Fact]
        public void FordRacing_PositiveWindow_ClampsOverUnity()
        {
            var d = ArcadeDecoders.ForGameId(7);
            var b = Block();
            PutSlot(b, 2, 1);                 // (16-1)/9 = 1.667
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, Dword(b, 2), st));
            Assert.Equal(1.0, st.Constant.Strength, 6);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        [InlineData(-65505)]
        [InlineData(-70000)]
        public void FordRacing_OutsideBothWindows_IsNoCommand(int ff)
        {
            var d = ArcadeDecoders.ForGameId(7);
            var b = Block();
            PutSlot(b, 2, ff);
            Assert.False(d.TryDecode(b, Dword(b, 2), new ArcadeFfbState()));
        }

        // ---- Arctic Thunder: two slots, neither of them the usual one ----

        // 0xFF is the IDLE sentinel on the vibration slot, not maximum. Without
        // this test a parked cabinet would buzz at full strength forever.
        [Theory]
        [InlineData(0xFF)]
        [InlineData(0)]
        public void ArcticThunder_VibrationIdleSentinels_ProduceNothing(int vib)
        {
            var d = ArcadeDecoders.ForGameId(75);
            var b = Block();
            PutSlot(b, 7, vib);
            var st = new ArcadeFfbState();
            d.TryDecode(b, 0, st);
            Assert.False(st.Periodic.Present);
        }

        // Zero on the steering slot must be rejected BEFORE the centre
        // comparison. Without the guard it takes the low branch and commands
        // full right force on every idle frame.
        [Fact]
        public void ArcticThunder_ZeroSteering_IsIdle_NotFullRight()
        {
            var d = ArcadeDecoders.ForGameId(75);
            var b = Block();
            PutSlot(b, 8, 0);
            var st = new ArcadeFfbState();
            d.TryDecode(b, 0, st);
            Assert.False(st.Constant.Present);
        }

        [Theory]
        [InlineData(0xFF,  1.0)]      // high  -> wheel right
        [InlineData(0x01, -1.0)]      // low   -> wheel left
        [InlineData(0x80,  0.0)]      // centre is a one-value dead zone
        public void ArcticThunder_SteeringIsCentredOn0x80(int ffb, double expected)
        {
            var d = ArcadeDecoders.ForGameId(75);
            var b = Block();
            PutSlot(b, 8, ffb);
            var st = new ArcadeFfbState();
            d.TryDecode(b, 0, st);
            if (ffb == 0x80) Assert.False(st.Constant.Present);
            else Assert.Equal(expected, st.Constant.Strength, 4);
        }

        [Fact]
        public void ArcticThunder_BothChannelsCanFireInOneFrame()
        {
            var d = ArcadeDecoders.ForGameId(75);
            var b = Block();
            PutSlot(b, 7, 128);
            PutSlot(b, 8, 0xC0);
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, 0, st));
            Assert.True(st.Periodic.Present);
            Assert.True(st.Constant.Present);
        }

        // ---- WMMT3 family: four slots, opposite conventions ----

        [Fact]
        public void Wmmt3_ReadsFourIndependentSlots()
        {
            var d = ArcadeDecoders.ForGameId(61);
            var b = Block();
            PutSlot(b, 6, 63);     // spring, full
            PutSlot(b, 7, 63);     // viscosity -> friction, full
            PutSlot(b, 9, 0x20);   // reflect
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, 0, st));
            Assert.Equal(1.0, st.Spring.Strength, 6);
            Assert.Equal(1.0, st.Friction.Strength, 6);
            Assert.True(st.Constant.Present);
        }

        // Reflect and centre offset use OPPOSITE direction conventions: the same
        // positive value means opposite ways on the two channels.
        [Fact]
        public void Wmmt3_ReflectAndCenterOffset_HaveOppositeDirections()
        {
            var d = ArcadeDecoders.ForGameId(61);

            var b1 = Block();
            PutSlot(b1, 9, 0x20);          // positive reflect -> wheel LEFT
            var s1 = new ArcadeFfbState();
            d.TryDecode(b1, 0, s1);

            var b2 = Block();
            PutSlot(b2, 8, 0x20);          // positive centre offset -> wheel RIGHT
            var s2 = new ArcadeFfbState();
            d.TryDecode(b2, 0, s2);

            Assert.True(s1.Constant.Strength < 0);
            Assert.True(s2.Constant.Strength > 0);
        }

        // The reference has one constant-effect slot, so when both channels fire
        // the centre offset silently overwrites reflect. Summing would produce a
        // force the cabinet never asked for.
        [Fact]
        public void Wmmt3_CenterOffsetOverwritesReflect_RatherThanSumming()
        {
            var d = ArcadeDecoders.ForGameId(61);
            var b = Block();
            PutSlot(b, 9, 0x3F);   // reflect, full, would be -1.0
            PutSlot(b, 8, 0x20);   // centre offset, about +0.5
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, 0, st));
            Assert.Equal(0x20 / 63.0, st.Constant.Strength, 5);
        }

        [Fact]
        public void Wmmt3_BothDirectionalChannelsZero_IsAnExplicitStop()
        {
            var d = ArcadeDecoders.ForGameId(61);
            var b = Block();
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, 0, st));
            Assert.Equal(ArcadeTriggerKind.Constant, st.Constant.Kind);
            Assert.Equal(0.0, st.Constant.Strength, 6);
        }

        // A negative coefficient becomes a FULL-SCALE spring in the reference.
        // That is a stale slot, not a command, and it must not reach the wheel.
        [Fact]
        public void Wmmt3_NegativeSpring_IsRejected_NotFullScale()
        {
            var d = ArcadeDecoders.ForGameId(61);
            var b = Block();
            PutSlot(b, 6, -20);
            var st = new ArcadeFfbState();
            d.TryDecode(b, 0, st);
            Assert.False(st.Spring.Present);
        }

        // Dead Heat is WMMT3 with exactly one branch changed: a negative reflect
        // arrives as an 8-bit wrap rather than a signed int.
        [Fact]
        public void DeadHeat_DecodesNegativeReflectAsEightBitWrap()
        {
            var dh = ArcadeDecoders.ForGameId(62);
            var b = Block();
            PutSlot(b, 9, 0xC1);           // byte -63
            var st = new ArcadeFfbState();
            Assert.True(dh.TryDecode(b, 0, st));
            Assert.True(st.Constant.Strength > 0);

            // WMMT3 given the same value sees a large positive, which is in its
            // dead band and commands nothing.
            var wm = ArcadeDecoders.ForGameId(61);
            var st2 = new ArcadeFfbState();
            wm.TryDecode(b, 0, st2);
            Assert.False(st2.Constant.Present);
        }

        // Dead Heat Riders puts its spring on a different slot with a different
        // divisor. There is no common divisor across this family.
        [Fact]
        public void DeadHeatRiders_SpringUsesSlotTwoAndFiveHundredScale()
        {
            var d = ArcadeDecoders.ForGameId(63);
            var b = Block();
            PutSlot(b, 2, 250);
            var st = new ArcadeFfbState();
            Assert.True(d.TryDecode(b, 0, st));
            Assert.Equal(0.5, st.Spring.Strength, 6);
        }

        // ---- Games deliberately not implemented ----

        [Theory]
        [InlineData(19)]   // Pokken: no wheel force at all, two vibration motors
        [InlineData(54)]   // Storm Racer G: needs game memory, and an ini scale we could not establish
        [InlineData(55)]   // D1GP: same
        public void GamesWeCannotDecodeFaithfully_HaveNoDecoder(int gameId)
        {
            Assert.Null(ArcadeDecoders.ForGameId(gameId));
        }
    }
}
