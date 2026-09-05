// Per-game arcade decoders. See ArcadeFfb.cs for the vocabulary they emit and
// for the sign convention (positive strength = wheel pushed RIGHT).
//
// THE SHAPE VARIES FAR MORE THAN IT LOOKS. Initial D's "one DWORD split into a
// command byte, a magnitude byte and a parameter byte" is ONE game family's
// encoding, not a house format. Sega Rally 3 and Ford Racing use the whole int
// as a small number. WMMT3, Dead Heat and Dead Heat Riders use FOUR separate int
// slots, one per effect. Arctic Thunder ignores the primary slot entirely and
// reads two others. So a decoder gets the whole block and takes what it needs.
//
// THE REFERENCE DOES NOT CLAMP. Its renderer turns a negative or over-unity
// strength into MAXIMUM force (`if (coeff < 0) coeff = 32767`), and several of
// these games can genuinely produce both from a stale or out-of-range slot. Each
// decoder below clamps at its own boundary, deliberately diverging: a stale slot
// should be silence, never a full-scale jolt into someone's hands.
//
// EVERY DECODER HERE EXCEPT THE INITIAL D FAMILY IS UNVERIFIED. They are
// transcribed from a reading of the reference source for games we do not own and
// cannot test. The Initial D decode was independently extracted twice and the two
// readings agreed; nothing else here has that.

using System;

namespace TrueforceForAll.Core
{
    /// <summary>Shared helpers for reading the 64-byte block. Slots are int-sized
    /// and little-endian; the reference reads them as SIGNED ints and several
    /// games depend on that, so signed is the default and unsigned is the
    /// explicit case.</summary>
    internal static class Jvs
    {
        public static int Slot(byte[] b, int index)
        {
            int off = index * 4;
            if (b == null || off < 0 || off + 4 > b.Length) return 0;
            return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
        }

        /// <summary>Clamp to 0..1. The reference does not do this and turns an
        /// over-range value into full force; we would rather lose a rare genuine
        /// peak than deliver a full-scale jolt from a stale slot.</summary>
        public static double Unit(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }

    /// <summary>Sega Rally 3 (game id 6).
    ///
    /// No byte decomposition at all: the whole int is one small number, 0..30,
    /// encoding direction and magnitude together. Magnitude is INVERTED in both
    /// bands, so a low number is a strong force.
    ///
    /// The only idle guard is zero, so a stale non-zero slot drives force. That
    /// is the reference's behaviour too; the clamp below at least bounds it.</summary>
    public sealed class SegaRally3Decoder : IArcadeFfbDecoder
    {
        public string Name => "Sega Rally 3";
        public int GameId => 6;
        public int SlotIndex => 2;
        public void Reset() { }

        public bool TryDecode(byte[] block, uint dword, ArcadeFfbState into)
        {
            int ff = Jvs.Slot(block, 2);
            if (ff <= 0) return false;
            // Above the encoding's range the reference goes negative and then
            // saturates to full force. Reject instead.
            if (ff > 30) return false;

            if (ff > 15)
            {
                // Source logs "moving wheel right"; DIRECTION_FROM_LEFT.
                into.Set(ArcadeTrigger.Constant(Jvs.Unit((31 - ff) / 15.0)));
            }
            else
            {
                // Source logs "moving wheel left"; DIRECTION_FROM_RIGHT.
                into.Set(ArcadeTrigger.Constant(-Jvs.Unit((16 - ff) / 15.0)));
            }
            return true;
        }
    }

    /// <summary>Ford Racing (game id 7).
    ///
    /// The slot is read SIGNED and compared against two numeric windows. Reading
    /// it unsigned makes the guards never fire, which is the most likely way to
    /// get this game silently wrong.
    ///
    /// Both windows divide by 9 even though the positive one is 15 wide, so the
    /// positive branch legitimately exceeds 1.0 below ff==7. The reference lets
    /// that saturate; we clamp.</summary>
    public sealed class FordRacingDecoder : IArcadeFfbDecoder
    {
        public string Name => "Ford Racing";
        public int GameId => 7;
        public int SlotIndex => 2;
        public void Reset() { }

        public bool TryDecode(byte[] block, uint dword, ArcadeFfbState into)
        {
            int ff = Jvs.Slot(block, 2);

            if (ff < -65505 && ff > -65515)
            {
                // DIRECTION_FROM_RIGHT: wheel goes left.
                into.Set(ArcadeTrigger.Constant(-Jvs.Unit((-65505 - ff) / 9.0)));
                return true;
            }
            if (ff > 0 && ff < 16)
            {
                // DIRECTION_FROM_LEFT: wheel goes right.
                into.Set(ArcadeTrigger.Constant(Jvs.Unit((16 - ff) / 9.0)));
                return true;
            }
            // Outside both windows the reference emits nothing and relies on SDL
            // expiry to decay the last force. We report "no command", and the
            // freshness window upstream does the decaying.
            return false;
        }
    }

    /// <summary>Arctic Thunder (game id 75).
    ///
    /// Ignores the primary slot entirely: vibration is int 7, steering is int 8.
    /// A naive implementation following only the game-id slot rule reads int 2
    /// and gets zeros forever.
    ///
    /// Two guards here are load-bearing. 0xFF on the vibration slot means IDLE,
    /// not maximum, so a parked cabinet buzzes at full without that test. And
    /// zero on the steering slot must be rejected before the centre comparison,
    /// or it takes the low branch and commands full right force on every idle
    /// frame.</summary>
    public sealed class ArcticThunderDecoder : IArcadeFfbDecoder
    {
        public string Name => "Arctic Thunder";
        public int GameId => 75;
        public int SlotIndex => 8;
        public void Reset() { }

        public bool TryDecode(byte[] block, uint dword, ArcadeFfbState into)
        {
            bool any = false;

            int vib = Jvs.Slot(block, 7);
            if (vib != 0 && vib != 0xFF && vib > 0 && vib < 0xFF)
            {
                // 50 ms period, no fade, re-issued continuously by the game.
                into.Set(ArcadeTrigger.Sine(Jvs.Unit(vib / 255.0), 50));
                any = true;
            }

            int ffb = Jvs.Slot(block, 8);
            if (ffb != 0)
            {
                if (ffb > 0x80)
                {
                    // DIRECTION_FROM_LEFT: wheel goes right.
                    into.Set(ArcadeTrigger.Constant(Jvs.Unit((ffb - 128.0) / 127.0)));
                    any = true;
                }
                else if (ffb < 0x80)
                {
                    // DIRECTION_FROM_RIGHT: wheel goes left.
                    into.Set(ArcadeTrigger.Constant(-Jvs.Unit((128.0 - ffb) / 127.0)));
                    any = true;
                }
                // Exactly 0x80 is centre and emits nothing.
            }

            return any;
        }
    }

    /// <summary>The WMMT3 family: four independent int slots, one per effect,
    /// scaled by 63 rather than 127. WMMT3 / WMMT3DX+ (id 61), Dead Heat (id 62)
    /// and Dead Heat Riders (id 63) are the same file with small differences, so
    /// they share this class and differ by the flags below.
    ///
    /// Two things here are genuinely strange and both are in the reference:
    ///   - reflect and centreOffset use OPPOSITE direction conventions, so the
    ///     same sign on the two channels means opposite ways.
    ///   - both channels can fire in one frame, and the reference has a single
    ///     constant-effect slot, so centreOffset silently overwrites reflect.
    ///     We reproduce that last-write-wins rather than summing, because summing
    ///     would produce a force the cabinet never asked for.</summary>
    public sealed class WmmtFamilyDecoder : IArcadeFfbDecoder
    {
        private readonly int _springSlot;
        private readonly double _springDiv;
        private readonly bool _reflectIsByte;

        /// <param name="springSlot">Where the spring coefficient lives: slot 6
        /// for WMMT3 and Dead Heat, slot 2 for Dead Heat Riders.</param>
        /// <param name="springDiv">63 for the WMMT3 family, 500 for Dead Heat
        /// Riders. There is no common divisor.</param>
        /// <param name="reflectIsByte">Dead Heat and Dead Heat Riders decode a
        /// negative reflect as an 8-bit wrap (values above 0x3F), while WMMT3
        /// uses a real signed 32-bit negative. That one branch is the whole
        /// difference between the files.</param>
        public WmmtFamilyDecoder(int gameId, string name, int springSlot, double springDiv, bool reflectIsByte)
        {
            GameId = gameId;
            Name = name;
            _springSlot = springSlot;
            _springDiv = springDiv;
            _reflectIsByte = reflectIsByte;
        }

        public string Name { get; private set; }
        public int GameId { get; private set; }
        public int SlotIndex => _springSlot;
        public void Reset() { }

        public bool TryDecode(byte[] block, uint dword, ArcadeFfbState into)
        {
            int setSpring       = Jvs.Slot(block, _springSlot);
            int setViscosity    = Jvs.Slot(block, 7);
            int setCenterOffset = Jvs.Slot(block, 8);
            int setReflect      = Jvs.Slot(block, 9);

            bool any = false;

            // A negative coefficient becomes FULL-SCALE spring in the reference.
            // Reject it: a cabinet asking for a negative spring is a stale slot,
            // not a command.
            if (setSpring > 0)
            {
                into.Set(ArcadeTrigger.Spring(Jvs.Unit(setSpring / _springDiv)));
                any = true;
            }
            if (setViscosity > 0)
            {
                into.Set(ArcadeTrigger.Friction(Jvs.Unit(setViscosity / 63.0)));
                any = true;
            }

            if (setReflect == 0 && setCenterOffset == 0)
            {
                // The explicit idle reset. It stops the directional force only:
                // the reference leaves spring and friction latched at their last
                // value, and so do we, because the game genuinely means them to
                // persist until replaced.
                into.Set(ArcadeTrigger.Constant(0));
                return true;
            }

            // Reflect first, then centre offset, because the second overwrites
            // the first on the reference's single constant slot.
            if (setReflect > 0 && setReflect <= 0x3F)
            {
                // DIRECTION_FROM_RIGHT: wheel goes left.
                into.Set(ArcadeTrigger.Constant(-Jvs.Unit(setReflect / 63.0)));
                any = true;
            }
            else if (_reflectIsByte && setReflect > 0x3F && setReflect <= 0xFF)
            {
                // 8-bit two's complement. The reference uses 0xFF where the wrap
                // wants 0x100, so the smallest negative produces zero force; that
                // off-by-one is faithfully preserved because changing it would
                // change the feel rather than fix a crash.
                into.Set(ArcadeTrigger.Constant(Jvs.Unit((0xFF - setReflect) / 63.0)));
                any = true;
            }
            else if (!_reflectIsByte && setReflect < 0)
            {
                // DIRECTION_FROM_LEFT: wheel goes right.
                into.Set(ArcadeTrigger.Constant(Jvs.Unit(Math.Abs((double)setReflect) / 63.0)));
                any = true;
            }

            if (setCenterOffset > 0 && setCenterOffset <= 0x3F)
            {
                // DIRECTION_FROM_LEFT: wheel goes right. Note this is the
                // OPPOSITE convention to reflect for the same sign.
                into.Set(ArcadeTrigger.Constant(Jvs.Unit(setCenterOffset / 63.0)));
                any = true;
            }
            else if (setCenterOffset < 0)
            {
                into.Set(ArcadeTrigger.Constant(-Jvs.Unit(Math.Abs((double)setCenterOffset) / 63.0)));
                any = true;
            }

            return any;
        }
    }
}
