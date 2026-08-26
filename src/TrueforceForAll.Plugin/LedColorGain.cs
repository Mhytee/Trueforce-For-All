// Per-channel trim for what the wheel's LEDs actually emit.
//
// The colours we send are sRGB: #FFFF00 means yellow because that is what
// yellow is on a screen. An LED package is not a screen. Its three dies have
// their own relative brightness, and on the wheels we have looked at the red
// die is the weak one, so equal red and green drive mixes green-dominant and a
// nominal yellow arrives on the rim looking like lime.
//
// Nothing else in the pipeline touches a colour byte, by design: patterns are
// stored as INTENT so they mean the same thing on anyone's wheel and can be
// shared, and car data published upstream is not ours to rewrite. That leaves
// exactly one place for a correction, which is the last moment before the
// bytes go out. This is that correction.
//
// The gains only ever matter as a RATIO between channels. That is the whole
// insight behind how this works: if a colour lights only one channel there is
// no ratio to get wrong, so cutting it corrects nothing and costs brightness
// for free. A pure blue would lose two thirds of its drive to fix a hue error
// it cannot have.
//
// So the correction is applied per LED and then renormalised: scale all three
// channels back up until the brightest one returns to where it started.
// Scaling all three by the same factor leaves their ratios alone, so the hue
// stays corrected while the headroom comes back.
//
// How much comes back depends on WHICH channel is the colour's peak, and this
// is the part that is easy to get backwards. The renormalising factor is
// peakIn/peakOut, so it only exceeds 1 when the peak channel is one being cut.
// The channel with the HIGHEST gain is the reference: it is never cut, which
// means a colour peaked on it renormalises by exactly 1 and keeps the full cut
// on its other channels.
//
// With the shipped gains the reference is red, so:
//   - pure green, pure blue: restored exactly, no light lost.
//   - green-dominant mixes: scaled up, can end brighter than authored.
//   - yellow, white, orange, every red-peaked mix: factor of 1, no refund.
//     Yellow (255,255,0) goes out as (255,155,0) and white as (255,155,165),
//     which on the emission model these gains imply is roughly a quarter of
//     the light. That is the real price of the hue fix.
// None of this is a property of red as such. Set the sliders so green is the
// highest and green becomes the reference, and the cost moves to the greens.
//
// That holds even if the firmware runs its own transfer curve, as long as the
// curve is the same on all three channels: scaling drive scales all three
// outputs by the same factor, so the ratio survives. A curve does change the
// SIZE of the quarter above, which is measured in drive, not in photons.
//
// What it gives up, deliberately: the unnormalised form also evened out
// brightness BETWEEN colours, since the green die out-emits the red one at
// equal drive. Renormalising hands that back, so greens and blues are once
// again brighter than reds, and a green-to-yellow ramp dips a little at the
// yellow end. Hue was the complaint; uneven brightness is the wheel's natural
// state and what everyone is already used to.
//
// MUST NOT move into TrueforceForAll.Core. WheelLedChannel.TryWriteSlot is
// also how RestoreSlot hands a user their own borrowed slot back, and it
// verifies that write by reading the bytes returned. A gain down there would
// fail that check forever, so the loan would never settle, and it would re-cut
// the user's own colours on every restore, walking them toward black.
//
// Self-contained (BCL only) so it link-compiles into the tests.

using System;

namespace TrueforceForAll.Plugin
{
    /// <summary>Per-channel duty trim applied to a packed RGB triple array on
    /// its way to the wheel. Gains of 1,1,1 are the identity.</summary>
    public static class LedColorGain
    {
        /// <summary>Apply per-channel gains to a packed RGB array (3 bytes per
        /// LED). Returns a NEW array; the input is never mutated, because
        /// callers keep using the original after the write.</summary>
        public static byte[] Apply(byte[] rgb, float gR, float gG, float gB)
        {
            if (rgb == null) return null;

            // Normalise so the largest gain is 1. A caller that hands us
            // (1.0, 0.6, 0.5) means "cut green and blue"; one that hands us
            // (2.0, 1.2, 1.0) means the same ratio, and without this it would
            // instead mean "clip everything bright". Ratios are the only thing
            // a hue correction can carry, so we keep the ratio and drop the
            // scale.
            float m = Math.Max(gR, Math.Max(gG, gB));
            if (m > 1f) { gR /= m; gG /= m; gB /= m; }

            var outp = new byte[rgb.Length];
            for (int i = 0; i + 2 < rgb.Length; i += 3)
            {
                int r = rgb[i + 0], g = rgb[i + 1], b = rgb[i + 2];

                double fr = r * (double)gR;
                double fg = g * (double)gG;
                double fb = b * (double)gB;

                // Renormalise this LED on its own. Per LED, not per pattern: a
                // strip is ten independent colours, and letting one bright LED
                // set the scale for the rest would flatten every gradient.
                int    peakIn  = Math.Max(r, Math.Max(g, b));
                double peakOut = Math.Max(fr, Math.Max(fg, fb));
                if (peakOut > 0.0)
                {
                    double s = peakIn / peakOut;
                    fr *= s; fg *= s; fb *= s;
                }

                outp[i + 0] = Round(fr);
                outp[i + 1] = Round(fg);
                outp[i + 2] = Round(fb);
            }
            // A trailing partial triple (never happens on a well-formed strip,
            // but the array length is not ours to trust) copies through rather
            // than arriving as zeros.
            for (int i = rgb.Length - rgb.Length % 3; i < rgb.Length; i++)
                outp[i] = rgb[i];
            return outp;
        }

        /// <summary>The trim this plugin ships with, measured by eye on a G PRO
        /// (2026-08-23): a nominal white reads R255 G150 B80, so red is the weak
        /// die and the other two come down to meet it. Green settled at 60,
        /// which is where the white-point measurement had already put it (150
        /// of 255), so two independent routes agree on it. Blue ended far above
        /// its measured 31, at 65, after the shipped patterns were tuned by eye
        /// against it: cutting blue hard enough to neutralise white cost more in
        /// brightness than it bought back.
        ///
        /// Most of the shipped patterns in LightPatternLibrary were authored by
        /// eye at THESE values, so moving them is not a free knob. See the test
        /// that pins them.
        ///
        /// Applied to every wheel rather than to the G PRO alone. That is a
        /// smaller claim than it sounds: only a wheel that can take a slot
        /// write can receive it at all, which today is the two G PRO editions
        /// and the RS50, and renormalising means a wrong number cannot touch a
        /// pure colour anyway. Split this per chassis the moment anyone reports
        /// their wheel looking off.</summary>
        public const float ShippedR = 1.00f;
        public const float ShippedG = 0.606f;
        public const float ShippedB = 0.649f;

        /// <summary>True when these gains are the identity, so callers can skip
        /// the copy and, more importantly, so the comparison sites below stay
        /// byte-for-byte identical to their old behaviour on an uncalibrated
        /// wheel.</summary>
        public static bool IsIdentity(float gR, float gG, float gB)
            => gR == 1f && gG == 1f && gB == 1f;

        // Rounding is away-from-zero and fixed forever. The sites that compare
        // what is on the wheel against what the library holds do it by running
        // the library value through Apply and comparing the RESULTS, never by
        // dividing the gain back out. Division does not round-trip at 8 bits
        // (250 at 0.51 gives 128, and 128 back gives 251), so an inverse would
        // miss those comparisons by a count or two and re-upload five slots
        // every time the tab opened.
        //
        // Rounded once, at the end, after the renormalising scale. Rounding per
        // channel before scaling would quantise twice and let a dim LED drift
        // off its own hue.
        private static byte Round(double v)
        {
            int x = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return (byte)(x < 0 ? 0 : x > 255 ? 255 : x);
        }
    }
}
