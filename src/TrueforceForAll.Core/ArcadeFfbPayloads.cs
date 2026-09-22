// Builders for synthetic HID++ 0x8123 fn2 DOWNLOAD_EFFECT reports carrying an
// ARCADE cabinet's force command, so an emulated arcade game's effects render
// through the same decode-and-render path a real game's download takes
// (docs/di-condition-engine.md).
//
// Why byte payloads rather than a parameter API: HidppEffectEngine has no other
// ingest. Its whole public surface takes wire bytes, deliberately, so that every
// effect in the system has travelled the same parser. FxTestPayloads does this
// for the bench; this does it for the cabinet.
//
// Layout, matching the wire dialect:
//   [0x12][0xff][featIdx][fn2|swid][slot][type|autostart][len:2][delay:2][block]
// big-endian throughout.

using System;

namespace TrueforceForAll.Core
{
    public static class ArcadeFfbPayloads
    {
        /// <summary>A decoded 0..1 strength to the engine's s16 coefficient scale.
        /// Decoding a cabinet's raw byte into that 0..1 is the decoder's job and
        /// is not the same arithmetic for every command, so it deliberately does
        /// not happen here.</summary>
        public static short Coeff(double strength01, double scale)
        {
            double m = strength01 * scale;
            if (m > 1.0) m = 1.0;
            if (m < 0.0) m = 0.0;
            return (short)Math.Round(m * 32767.0);
        }

        /// <summary>The engine effect type for an arcade trigger, or 0 when the
        /// trigger is not one the engine renders. Constant is deliberately 0: the
        /// engine refuses constants because the scalar path owns them, and Rumble
        /// is a gamepad-motor call in the reference implementation rather than a
        /// wheel effect.</summary>
        public static byte TypeFor(ArcadeTriggerKind kind)
        {
            switch (kind)
            {
                case ArcadeTriggerKind.Spring:       return HidppEffectEngine.TypeSpring;
                case ArcadeTriggerKind.Damper:       return HidppEffectEngine.TypeDamper;
                case ArcadeTriggerKind.Friction:     return HidppEffectEngine.TypeFriction;
                case ArcadeTriggerKind.Inertia:      return HidppEffectEngine.TypeInertia;
                case ArcadeTriggerKind.Sine:         return HidppEffectEngine.TypeSine;
                case ArcadeTriggerKind.Triangle:     return HidppEffectEngine.TypeTriangle;
                case ArcadeTriggerKind.SawtoothUp:   return HidppEffectEngine.TypeSawtoothUp;
                case ArcadeTriggerKind.SawtoothDown: return HidppEffectEngine.TypeSawtoothDown;
                default:                             return 0;
            }
        }

        /// <summary>A condition effect carrying every parameter the trigger has.
        /// Covers spring, damper, friction and inertia, which differ only by the
        /// type byte and by what the engine evaluates them against. The caller
        /// re-issues this on the SAME slot when a value changes, which the engine
        /// treats as a parameter update rather than a new effect.
        /// Saturation, deadband and centre are passed through rather than assumed,
        /// because when the source is a renderer's output those fields are exactly
        /// where the user's own force tuning ended up.</summary>
        public static byte[] Condition(byte slot, byte type, ArcadeTrigger t, double scale)
        {
            var p = NewReport(slot, type);
            PutU16(p, 10, (ushort)Coeff(t.SatLeft <= 0 ? 1.0 : t.SatLeft, 1.0));
            PutS16(p, 12, Signed(t.CoeffLeft, scale));
            PutU16(p, 14, (ushort)Coeff(t.Deadband, 1.0));
            PutS16(p, 16, Signed(t.Center, 1.0));
            PutS16(p, 18, Signed(t.CoeffRight, scale));
            PutU16(p, 20, (ushort)Coeff(t.SatRight <= 0 ? 1.0 : t.SatRight, 1.0));
            return p;
        }

        /// <summary>Signed coefficient scale, keeping the sign: a condition can
        /// legitimately pull the other way.</summary>
        public static short Signed(double v, double scale)
        {
            double m = v * scale;
            if (double.IsNaN(m)) return 0;
            if (m > 1.0) m = 1.0;
            if (m < -1.0) m = -1.0;
            return (short)Math.Round(m * 32767.0);
        }

        /// <summary>A periodic: sine, triangle or either sawtooth. A period of 0
        /// renders SILENTLY as zero in the engine, which reads on the rig as "the
        /// effect does nothing", so it is floored here rather than passed
        /// through.</summary>
        public static byte[] Periodic(byte slot, byte type, ArcadeTrigger t, double scale)
        {
            int periodMs = t.PeriodMs;
            if (periodMs < 4) periodMs = 4;             // 250 Hz ceiling
            if (periodMs > 1000) periodMs = 1000;
            var p = NewReport(slot, type);
            PutS16(p, 10, Signed(t.Strength, scale));
            PutS16(p, 12, Signed(t.Offset, 1.0));
            PutU16(p, 14, (ushort)periodMs);
            PutU16(p, 16, (ushort)(Clamp01(t.Phase) * 65535.0));
            // envelope left zeroed: it is ignored on an infinite effect anyway
            return p;
        }

        private static double Clamp01(double v)
            => double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>Builds whichever payload a trigger calls for, or null when the
        /// engine does not render that trigger.</summary>
        public static byte[] For(byte slot, ArcadeTrigger t, double scale)
        {
            byte type = TypeFor(t.Kind);
            if (type == 0) return null;
            bool periodic = t.Kind == ArcadeTriggerKind.Sine
                         || t.Kind == ArcadeTriggerKind.Triangle
                         || t.Kind == ArcadeTriggerKind.SawtoothUp
                         || t.Kind == ArcadeTriggerKind.SawtoothDown;
            return periodic ? Periodic(slot, type, t, scale) : Condition(slot, type, t, scale);
        }

        // Infinite duration, no delay, autostart set. Without the autostart bit a
        // freshly downloaded slot stays stopped and nothing plays.
        private static byte[] NewReport(byte slot, byte type)
        {
            var p = new byte[64];
            p[0] = 0x12; p[1] = 0xff; p[2] = 0x0e; p[3] = 0x2f;
            p[4] = slot;
            p[5] = (byte)(type | HidppEffectEngine.AutostartBit);
            // p[6..7] duration 0 = infinite, p[8..9] delay 0.
            return p;
        }

        private static void PutU16(byte[] p, int off, ushort v)
        {
            p[off] = (byte)(v >> 8);
            p[off + 1] = (byte)v;
        }

        private static void PutS16(byte[] p, int off, short v) => PutU16(p, off, (ushort)v);
    }
}
