// What can go on the wheel's OLED, and where.
//
// The firmware owns font size and alignment: picking a layout IS picking how
// big each value is drawn and where it sits (see WheelOledChannel's header for
// the hardware-observed vocabulary). So a "screen" here is a layout plus one
// field per slot, and both the shipped presets and a user's custom arrangement
// are expressed the same way and rendered by the same code. Nothing renders
// twice, so a preset can never drift from what the editor would produce.
//
// Field keys/labels are index-matched and the pickers index straight into
// them, exactly like TrueforcePlugin.DashDriveContentKeys, so the two lists
// move together.

using System;
using System.Globalization;

namespace TrueforceForAll.Plugin
{
    /// <summary>The firmware layouts worth offering, named by shape rather
    /// than by their protocol letter. Slot widths are the firmware's, not
    /// ours; text longer than a slot is cut off by the wheel.</summary>
    public enum OledLayoutKind
    {
        /// <summary>G: one very large character, then three medium ones.</summary>
        BigLeft = 0,
        /// <summary>F: one medium character, then three very large ones.</summary>
        BigRight = 1,
        /// <summary>H: a small wide row above a larger one.</summary>
        Stacked = 2,
        /// <summary>J: four rows, small/large/small/large, centered.</summary>
        FourCenter = 3,
        /// <summary>I: the same four rows, right-aligned.</summary>
        FourRight = 4,
    }

    /// <summary>How the shift flash draws. The panel cannot centre its largest
    /// font (see OledDashController's header), so these two are a real trade
    /// rather than a preference: bigger, or centred and labelled.</summary>
    public enum OledFlashStyle
    {
        /// <summary>The 37px gear with the speed beside it, sitting left.</summary>
        BigGearAndSpeed = 0,
        /// <summary>"GEAR" small over the gear, centred, at 18px.</summary>
        CenteredGear = 1,
    }

    /// <summary>The shipped arrangements, plus Custom for a user-built one.</summary>
    public enum OledScreen
    {
        GearAndSpeed = 0,
        SpeedAndGear = 1,
        SpeedAndDelta = 2,
        SpeedGearAndDelta = 3,
        Vertical = 4,
        Custom = 5,
    }

    public static class OledScreenModel
    {
        /// <summary>Screen names short enough for the panel's large row (10
        /// characters), for the readout when a bound control cycles them.
        /// Index-matched to OledScreen.</summary>
        public static readonly string[] ScreenShortNames =
            { "GEAR+SPEED", "SPEED+GEAR", "SPD+DELTA", "SPD GEAR D", "SPD OVER G", "CUSTOM" };

        // ---- Field catalog -------------------------------------------------
        // Only what the plugin can actually fill. Revs are deliberately absent:
        // the wheel's rev lights already say it, better and without a glance.
        public static readonly string[] FieldKeys =
            { "Gear", "Speed", "SpeedUnit", "Delta", "Position", "Laps", "LastLap",
              "Custom", "None" };
        public static readonly string[] FieldLabels =
            { "Gear", "Speed", "Speed with unit", "Lap delta", "Position",
              "Lap of total", "Last lap time", "Custom text", "Empty" };

        public const string FieldNone = "None";
        public const string FieldCustom = "Custom";
        public const string FieldDelta = "Delta";

        // ---- Layout catalog ------------------------------------------------
        public static readonly OledLayoutKind[] LayoutKinds =
        {
            OledLayoutKind.BigLeft, OledLayoutKind.BigRight, OledLayoutKind.Stacked,
            OledLayoutKind.FourCenter, OledLayoutKind.FourRight,
        };
        public static readonly string[] LayoutLabels =
        {
            "Two side by side, first one huge",
            "Two side by side, second one huge",
            "Two stacked, second one larger",
            "Four rows, centered",
            "Four rows, right aligned",
        };

        public const int MaxSlots = 4;

        /// <summary>Character capacity of each slot, in firmware order.</summary>
        public static int[] SlotWidths(OledLayoutKind kind)
        {
            switch (kind)
            {
                case OledLayoutKind.BigLeft:
                case OledLayoutKind.BigRight: return new[] { 1, 3 };
                case OledLayoutKind.Stacked: return new[] { 21, 10 };
                default: return new[] { 19, 10, 19, 10 };
            }
        }

        public static int SlotCount(OledLayoutKind kind) => SlotWidths(kind).Length;

        /// <summary>True for a slot the firmware draws in TWO zones: the first
        /// character pinned to the left, everything after it right-aligned.
        /// Measured on a G PRO, and it is what made layout 7 look broken: a
        /// short string leaves a gap between the two zones, while a full-width
        /// one closes it and looks like a single field.
        ///
        /// A leading space skips the left zone, which is how a value gets
        /// cleanly right-aligned. Only the LARGE rows behave this way; the
        /// small wide rows draw normally, and the four-row CENTERED layout
        /// centres the whole string instead.</summary>
        public static bool SlotSplits(OledLayoutKind kind, int slot)
        {
            switch (kind)
            {
                case OledLayoutKind.Stacked: return slot == 1;
                case OledLayoutKind.FourRight: return slot == 1 || slot == 3;
                default: return false;
            }
        }

        /// <summary>Put a value cleanly on the right of a split slot by giving
        /// the left zone a space to swallow. A single character is left alone,
        /// since one character is exactly what the left zone is for.</summary>
        public static string RightAlignInSplitSlot(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length == 1) return text;
            return text[0] == ' ' ? text : " " + text;
        }

        /// <summary>How a slot is drawn, for the editor's per-slot hint. The
        /// user cannot change any of this, so saying it up front is the only
        /// way they can plan a screen.</summary>
        public static string SlotHint(OledLayoutKind kind, int slot)
        {
            int[] w = SlotWidths(kind);
            if (slot < 0 || slot >= w.Length) return "";
            string size;
            switch (kind)
            {
                case OledLayoutKind.BigLeft: size = slot == 0 ? "huge" : "medium"; break;
                case OledLayoutKind.BigRight: size = slot == 0 ? "medium" : "huge"; break;
                case OledLayoutKind.Stacked: size = slot == 0 ? "small" : "large"; break;
                default: size = (slot % 2 == 0) ? "small" : "large"; break;
            }
            string hint = $"{size}, up to {w[slot]} character" + (w[slot] == 1 ? "" : "s");
            if (SlotSplits(kind, slot))
                hint += "; 1st character sits left, the rest right. In custom text a leading space "
                      + "keeps it all on the right, and trailing spaces push it back left, so you "
                      + "can place it anywhere along the row";
            return hint;
        }

        // ---- Presets -------------------------------------------------------

        /// <summary>Resolve a shipped preset into the same layout-and-slots
        /// shape a custom screen uses. <paramref name="deltaOk"/> false drops
        /// the lap-delta rows AND their label, rather than leaving a heading
        /// over an empty space in a game that cannot report one.</summary>
        public static void Preset(OledScreen screen, bool useMph, bool deltaOk,
                                  out OledLayoutKind kind, out string[] slots, out string[] texts)
        {
            string speedLabel = useMph ? "SPEED MPH" : "SPEED KM/H";
            switch (screen)
            {
                case OledScreen.SpeedAndGear:
                    kind = OledLayoutKind.BigRight;
                    slots = new[] { "Gear", "Speed" };
                    texts = new string[2];
                    return;

                case OledScreen.SpeedAndDelta:
                    kind = OledLayoutKind.FourCenter;
                    slots = deltaOk
                        ? new[] { "Custom", "Speed", "Custom", "Delta" }
                        : new[] { "Custom", "Speed", "None", "None" };
                    texts = deltaOk
                        ? new[] { speedLabel, null, "LAP DELTA", null }
                        : new[] { speedLabel, null, null, null };
                    return;

                case OledScreen.SpeedGearAndDelta:
                    // Speed small on top, gear large under it, delta where the
                    // delta screen keeps it. Replaces an earlier arrangement
                    // that captioned the speed with "GEAR 3", which read as a
                    // label for the number below rather than a value of its own.
                    kind = OledLayoutKind.FourCenter;
                    slots = deltaOk
                        ? new[] { "SpeedUnit", "Gear", "Custom", "Delta" }
                        : new[] { "SpeedUnit", "Gear", "None", "None" };
                    texts = deltaOk
                        ? new[] { null, null, "LAP DELTA", null }
                        : new string[4];
                    return;

                case OledScreen.Vertical:
                    // Four-row CENTRED rather than the two-row layout this
                    // used to be. The two-row layout's large row splits, so a
                    // lone gear would be pinned to the left edge with nothing
                    // beside it; the centred rows put it under the speed where
                    // the name of the screen says it should be.
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "SpeedUnit", "Gear", "None", "None" };
                    texts = new string[4];
                    return;

                default:
                    kind = OledLayoutKind.BigLeft;
                    slots = new[] { "Gear", "Speed" };
                    texts = new string[2];
                    return;
            }
        }

        // ---- Rendering -----------------------------------------------------

        /// <summary>Turn one slot's field into the text the firmware draws.
        /// An unavailable value renders empty rather than a plausible zero:
        /// the panel has no way to caveat a number it is showing.</summary>
        public static string Render(string fieldKey, string customText,
                                    string gear, double speedKmh, bool useMph,
                                    double? lapDelta, bool deltaOk,
                                    int position, int currentLap, int totalLaps, int lastLapMs)
        {
            switch (fieldKey)
            {
                case "Gear": return GearGlyph(gear);
                case "Speed": return SpeedText(speedKmh, useMph);
                case "SpeedUnit": return SpeedText(speedKmh, useMph) + (useMph ? " MPH" : " KM/H");
                case "Delta": return deltaOk ? DeltaText(lapDelta) : "";
                // Zero is SimHub's "this game does not report it" for all
                // three, so these stay blank rather than printing P0 or 0/0.
                case "Position": return position > 0 ? "P" + position.ToString(CultureInfo.InvariantCulture) : "";
                case "Laps":
                    if (currentLap <= 0) return "";
                    return totalLaps > 0
                        ? currentLap.ToString(CultureInfo.InvariantCulture) + "/"
                          + totalLaps.ToString(CultureInfo.InvariantCulture)
                        : currentLap.ToString(CultureInfo.InvariantCulture);
                case "LastLap": return LapTimeText(lastLapMs);
                case "Custom": return customText ?? "";
                default: return "";
            }
        }

        /// <summary>A lap time for a 10-character row: m:ss.mmm, dropping the
        /// hour nobody laps in. Blank when there is no lap yet.</summary>
        public static string LapTimeText(int ms)
        {
            if (ms <= 0) return "";
            int totalSec = ms / 1000;
            int min = totalSec / 60;
            int sec = totalSec % 60;
            int milli = ms % 1000;
            return min.ToString(CultureInfo.InvariantCulture) + ":"
                 + sec.ToString("00", CultureInfo.InvariantCulture) + "."
                 + milli.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>SimHub's gear string squeezed into a single character.
        /// Gears above 9 keep their first digit; no layout in the firmware's
        /// vocabulary offers a wider field next to a large font.</summary>
        public static string GearGlyph(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return "-";
            char c = gear[0];
            if (c == '0') return "N";   // some sources report neutral as 0
            return c.ToString();
        }

        public static string SpeedText(double kmh, bool useMph)
        {
            if (double.IsNaN(kmh) || kmh <= 0) return "0";
            double v = useMph ? kmh * 0.621371 : kmh;
            if (v > 999) v = 999;
            return ((int)(v + 0.5)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Lap delta. The sign is always printed: the panel is
        /// monochrome, so nothing else separates gaining from losing.</summary>
        public static string DeltaText(double? delta)
        {
            if (!delta.HasValue || double.IsNaN(delta.Value)) return "--.--";
            double d = delta.Value;
            if (d > 99.99) d = 99.99;
            else if (d < -99.99) d = -99.99;
            return (d >= 0 ? "+" : "-") + Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>Sanitize a stored slot list to exactly the layout's slot
        /// count. Short, empty or unknown entries fall back to Empty, so a
        /// settings file written before a layout changed still loads.</summary>
        public static string[] SanitizeSlots(System.Collections.Generic.IList<string> stored,
                                             OledLayoutKind kind)
        {
            int n = SlotCount(kind);
            var outSlots = new string[n];
            for (int i = 0; i < n; i++)
            {
                string v = (stored != null && i < stored.Count) ? stored[i] : null;
                outSlots[i] = (v != null && Array.IndexOf(FieldKeys, v) >= 0) ? v : FieldNone;
            }
            return outSlots;
        }

        public static string[] SanitizeTexts(System.Collections.Generic.IList<string> stored, int n)
        {
            var outTexts = new string[n];
            for (int i = 0; i < n; i++)
                outTexts[i] = (stored != null && i < stored.Count) ? stored[i] : null;
            return outTexts;
        }
    }
}
