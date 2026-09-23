// Builders for synthetic HID++ 0x8123 fn2 DOWNLOAD_EFFECT reports, used by
// the FXTEST tool: the plugin injects these into a test HidppEffectEngine so
// a hand-triggered effect exercises the exact decode-and-render path a real
// game's download does (docs/di-condition-engine.md). Byte layout matches
// the wire dialect: [0x12][0xff][featIdx][fn2|swid][slot][type|autostart]
// [len:2][delay:2][block...], big-endian.

using System;

namespace TrueforceForAll.Core
{
    public static class FxTestPayloads
    {
        /// <summary>Effect type byte for an FXTEST kind name, or 0 when the
        /// kind is unknown. Kinds: DAMPER, SPRING, FRICTION, INERTIA, SINE,
        /// SQUARE, TRIANGLE, SAWUP, SAWDOWN, RAMP.</summary>
        public static byte TypeForKind(string kind)
        {
            switch ((kind ?? "").Trim().ToUpperInvariant())
            {
                case "DAMPER":   return HidppEffectEngine.TypeDamper;
                case "SPRING":   return HidppEffectEngine.TypeSpring;
                case "FRICTION": return HidppEffectEngine.TypeFriction;
                case "INERTIA":  return HidppEffectEngine.TypeInertia;
                case "SINE":     return HidppEffectEngine.TypeSine;
                case "SQUARE":   return HidppEffectEngine.TypeSquare;
                case "TRIANGLE": return HidppEffectEngine.TypeTriangle;
                case "SAWUP":    return HidppEffectEngine.TypeSawtoothUp;
                case "SAWDOWN":  return HidppEffectEngine.TypeSawtoothDown;
                case "RAMP":     return HidppEffectEngine.TypeRamp;
                default:         return 0;
            }
        }

        /// <summary>A full-strength-parameter effect download for an FXTEST
        /// kind: conditions carry coeff = strengthPct of full scale (both
        /// sides, saturation full, no deadband, center 0); periodics carry
        /// magnitude = strengthPct and the given period; ramp slides from
        /// +strength to -strength over 3 s. Returns null for unknown kinds.
        /// Slot 1, autostart, infinite duration (ramp excepted).</summary>
        public static byte[] Build(string kind, int strengthPct, int periodMs)
        {
            byte type = TypeForKind(kind);
            if (type == 0) return null;
            if (strengthPct < 0) strengthPct = 0;
            if (strengthPct > 100) strengthPct = 100;
            short mag = (short)(strengthPct * 32767 / 100);

            var p = new byte[64];
            p[0] = 0x12; p[1] = 0xff; p[2] = 0x0e; p[3] = 0x2f;
            p[4] = 0x01;                                   // slot 1
            p[5] = (byte)(type | HidppEffectEngine.AutostartBit);
            // p[6..7] duration (0 = infinite), p[8..9] delay (0).

            if (type >= HidppEffectEngine.TypeSpring && type <= HidppEffectEngine.TypeInertia)
            {
                PutU16(p, 10, 0x7fff);                     // left sat (wire = sat >> 1)
                PutS16(p, 12, mag);                        // left coeff
                PutU16(p, 14, 0);                          // deadband
                PutS16(p, 16, 0);                          // center
                PutS16(p, 18, mag);                        // right coeff
                PutU16(p, 20, 0x7fff);                     // right sat
            }
            else if (type == HidppEffectEngine.TypeRamp)
            {
                PutU16(p, 6, 3000);                        // 3 s, so the slide is felt
                PutS16(p, 10, mag);
                PutS16(p, 12, (short)-mag);
                // envelope zeroed
            }
            else                                           // periodics
            {
                if (periodMs <= 0) periodMs = 250;
                PutS16(p, 10, mag);                        // magnitude
                PutS16(p, 12, 0);                          // offset
                PutU16(p, 14, (ushort)Math.Min(periodMs, 65535));
                PutU16(p, 16, 0);                          // phase
                // envelope zeroed
            }
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
