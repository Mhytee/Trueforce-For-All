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
        /// <summary>C: one horizontal meter, nothing else.</summary>
        Gauge = 5,
        /// <summary>D: a meter, a thin second meter, and an 11-char caption.</summary>
        GaugeLabel = 6,
        /// <summary>E: a meter, a thin second meter, and two text fields.</summary>
        GaugeTwoText = 7,
    }

    /// <summary>What a slot accepts. A meter takes a number from 0 to 1 and the
    /// firmware fills a bar with it; everything else takes text.</summary>
    public enum OledSlotKind { Text = 0, Gauge = 1 }

    /// <summary>How the shift flash draws. The panel cannot center its largest
    /// font (see OledDashController's header), so these two are a real trade
    /// rather than a preference: bigger, or centered and labelled.</summary>
    public enum OledFlashStyle
    {
        /// <summary>The 37px gear with the speed beside it, sitting left.</summary>
        BigGearAndSpeed = 0,
        /// <summary>"GEAR" small over the gear, centered, at 18px.</summary>
        CenteredGear = 1,
    }

    /// <summary>The shipped arrangements, plus Custom for a user-built one.
    /// Values are PERSISTED, so new entries go on the end and nothing is ever
    /// renumbered; the order they are offered in is ScreenOrder below.</summary>
    public enum OledScreen
    {
        GearAndSpeed = 0,
        SpeedAndGear = 1,
        SpeedAndDelta = 2,
        SpeedGearAndDelta = 3,
        Vertical = 4,
        Custom = 5,
        SpeedOverGear = 6,
        GearOverSpeed = 7,
        SpeedOnly = 8,
        GearOnly = 9,
        SpeedOverGearPlain = 10,
        GearOverSpeedPlain = 11,
        GearAndDelta = 12,
    }

    public static class OledScreenModel
    {
        /// <summary>Screen names short enough for the panel's large row (10
        /// characters), for the readout when a bound control cycles them.
        /// Index-matched to OledScreen.</summary>
        public static readonly string[] ScreenShortNames =
            { "GEAR+SPEED", "SPEED+GEAR", "SPD+DELTA", "SPD GEAR D", "SPD OVER G", "CUSTOM",
              "SPEED/GEAR", "GEAR/SPEED", "SPEED", "GEAR", "SPD GEAR", "GEAR SPD",
              "GEAR+DELTA" };

        /// <summary>The order screens are offered and cycled in, which is NOT
        /// the enum's numeric order: the labelled pair are the ones to reach
        /// for, so they lead, and Build my own sits at the end where a
        /// catch-all belongs. Enum values stay put because they are persisted.</summary>
        public static readonly OledScreen[] ScreenOrder =
        {
            OledScreen.SpeedOverGear, OledScreen.GearOverSpeed,
            OledScreen.SpeedOverGearPlain, OledScreen.GearOverSpeedPlain,
            OledScreen.SpeedOnly, OledScreen.GearOnly,
            OledScreen.GearAndSpeed, OledScreen.SpeedAndGear,
            OledScreen.SpeedAndDelta, OledScreen.GearAndDelta,
            OledScreen.SpeedGearAndDelta,
            OledScreen.Custom,
        };
        public static readonly string[] ScreenOrderLabels =
        {
            "Speed over gear, labelled",
            "Gear over speed, labelled",
            "Speed over gear",
            "Gear over speed",
            "Speed only",
            "Gear only",
            "Big gear, speed beside it",
            "Big speed, gear beside it",
            "Speed and lap delta",
            "Gear and lap delta",
            "Speed, gear and lap delta",
            "Build my own",
        };

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
            OledLayoutKind.Gauge, OledLayoutKind.GaugeLabel, OledLayoutKind.GaugeTwoText,
        };
        public static readonly string[] LayoutLabels =
        {
            "Two side by side, first one huge",
            "Two side by side, second one huge",
            "Two stacked, second one larger",
            "Four rows, centered",
            "Four rows, right aligned",
            "One meter",
            "Two meters and a caption",
            "Two meters and two texts",
        };

        // ---- Meter catalog -------------------------------------------------
        // What a 0-to-1 slot can show. Only values the plugin genuinely has as
        // a fraction; there is no brake channel in the telemetry frame, so
        // there is no brake meter.
        public static readonly string[] GaugeFieldKeys =
            { "Throttle", "Brake", "Clutch", "Handbrake", "Revs",
              "FfbTorque", "TrueforceLevel", "FrontGrip", "RearGrip", "Steering", "None" };
        public static readonly string[] GaugeFieldLabels =
            { "Throttle", "Brake", "Clutch", "Handbrake", "Revs",
              "Force output", "Trueforce texture", "Front grip", "Rear grip", "Steering", "Empty" };

        public const int MaxSlots = 4;

        /// <summary>Character capacity of each slot, in firmware order.</summary>
        public static int[] SlotWidths(OledLayoutKind kind)
        {
            switch (kind)
            {
                case OledLayoutKind.BigLeft:
                case OledLayoutKind.BigRight: return new[] { 1, 3 };
                case OledLayoutKind.Stacked: return new[] { 21, 10 };
                // Meter slots have no character width; the zeroes are padding
                // so widths and kinds stay index-matched.
                case OledLayoutKind.Gauge: return new[] { 0 };
                case OledLayoutKind.GaugeLabel: return new[] { 0, 0, 11 };
                case OledLayoutKind.GaugeTwoText: return new[] { 0, 0, 7, 3 };
                default: return new[] { 19, 10, 19, 10 };
            }
        }

        /// <summary>Which slots are meters rather than text, index-matched to
        /// SlotWidths. The meter layouts lead with their bars so the editor
        /// lists them in the order the panel draws them, top to bottom.</summary>
        public static OledSlotKind[] SlotKinds(OledLayoutKind kind)
        {
            switch (kind)
            {
                case OledLayoutKind.Gauge:
                    return new[] { OledSlotKind.Gauge };
                case OledLayoutKind.GaugeLabel:
                    return new[] { OledSlotKind.Gauge, OledSlotKind.Gauge, OledSlotKind.Text };
                case OledLayoutKind.GaugeTwoText:
                    return new[] { OledSlotKind.Gauge, OledSlotKind.Gauge,
                                   OledSlotKind.Text, OledSlotKind.Text };
                default:
                    var all = new OledSlotKind[SlotWidths(kind).Length];
                    return all;   // Text is 0
            }
        }

        public static bool SlotIsGauge(OledLayoutKind kind, int slot)
        {
            var k = SlotKinds(kind);
            return slot >= 0 && slot < k.Length && k[slot] == OledSlotKind.Gauge;
        }

        /// <summary>A meter's value, 0 to 1. Steering is the odd one: it runs
        /// -1 to 1, so it is folded onto the bar with center at half full. The
        /// bar only ever fills from the left, so that is a position readout
        /// rather than a needle, and there is no way to make it one.</summary>
        public static double RenderGauge(string fieldKey, double throttle01, double rpmPercent,
                                         double? frontGrip, double? rearGrip, double? steering,
                                         double brake01, double clutch01, double handbrake01,
                                         double ffbTorque01, double trueforceLevel01)
        {
            switch (fieldKey)
            {
                case "Throttle": return Clamp01(throttle01);
                case "Brake": return Clamp01(brake01);
                case "Clutch": return Clamp01(clutch01);
                case "Handbrake": return Clamp01(handbrake01);
                case "Revs": return Clamp01(rpmPercent);
                // Ours, not the game's: how hard the wheel is being driven right
                // now, and how much texture is riding on the Trueforce stream.
                // Force is a MAGNITUDE, because a bar that fills from one end
                // cannot show a direction without giving up half its range.
                case "FfbTorque": return Clamp01(ffbTorque01);
                case "TrueforceLevel": return Clamp01(trueforceLevel01);
                case "FrontGrip": return frontGrip.HasValue ? Clamp01(frontGrip.Value) : 0.0;
                case "RearGrip": return rearGrip.HasValue ? Clamp01(rearGrip.Value) : 0.0;
                case "Steering": return steering.HasValue ? Clamp01((steering.Value + 1.0) * 0.5) : 0.5;
                default: return 0.0;
            }
        }

        private static double Clamp01(double v) =>
            double.IsNaN(v) ? 0.0 : v < 0 ? 0.0 : v > 1 ? 1.0 : v;

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
        /// centers the whole string instead.</summary>
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
            if (SlotIsGauge(kind, slot))
                return slot == 1 && kind != OledLayoutKind.Gauge
                    ? "thin meter, fills from the left"
                    : "meter, fills from the left";
            string size;
            switch (kind)
            {
                case OledLayoutKind.BigLeft: size = slot == 0 ? "huge" : "medium"; break;
                case OledLayoutKind.BigRight: size = slot == 0 ? "medium" : "huge"; break;
                case OledLayoutKind.Stacked: size = slot == 0 ? "small" : "large"; break;
                case OledLayoutKind.GaugeLabel:
                case OledLayoutKind.GaugeTwoText: size = "caption"; break;
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

                // The sibling of the speed-and-delta screen, and captioned in
                // the same voice as that pair rather than the newer ones: a
                // screen should match the one it sits beside.
                case OledScreen.GearAndDelta:
                    kind = OledLayoutKind.FourCenter;
                    slots = deltaOk
                        ? new[] { "Custom", "Gear", "Custom", "Delta" }
                        : new[] { "Custom", "Gear", "None", "None" };
                    texts = deltaOk
                        ? new[] { "GEAR", null, "LAP DELTA", null }
                        : new[] { "GEAR", null, null, null };
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

                // The labelled pair. Four centered rows: a small caption over a
                // large value, twice. The value carries the unit so the caption
                // does not have to, and the gear gets a ten-character row, so
                // this is also the arrangement that copes with a farm gearbox.
                case OledScreen.SpeedOverGear:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "Custom", "SpeedUnit", "Custom", "Gear" };
                    texts = new[] { "Speed", null, "Gear", null };
                    return;

                case OledScreen.GearOverSpeed:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "Custom", "Gear", "Custom", "SpeedUnit" };
                    texts = new[] { "Gear", null, "Speed", null };
                    return;

                // Values only, no captions. The four-row layout alternates
                // small and large, so BOTH values take large rows and the small
                // ones are left empty: a value in a small row reads as a label
                // for whatever is under it, which is exactly what made the
                // retired "speed above gear" screen look wrong.
                case OledScreen.SpeedOverGearPlain:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "None", "SpeedUnit", "None", "Gear" };
                    texts = new string[4];
                    return;

                case OledScreen.GearOverSpeedPlain:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "None", "Gear", "None", "SpeedUnit" };
                    texts = new string[4];
                    return;

                // One value, captioned. The small row is a label and the large
                // row is the number, which is what each row is shaped for.
                case OledScreen.SpeedOnly:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "Custom", "SpeedUnit", "None", "None" };
                    texts = new[] { "Speed", null, null, null };
                    return;

                case OledScreen.GearOnly:
                    kind = OledLayoutKind.FourCenter;
                    slots = new[] { "Custom", "Gear", "None", "None" };
                    texts = new[] { "Gear", null, null, null };
                    return;

                // RETIRED, and no longer offered: it put the small speed VALUE
                // over the gear, so the speed read as a caption for the gear.
                // The enum value is persisted, so it stays and renders the
                // labelled screen that replaced it.
                case OledScreen.Vertical:

                default:
                    kind = OledLayoutKind.BigLeft;
                    slots = new[] { "Gear", "Speed" };
                    texts = new string[2];
                    return;
            }
        }

        /// <summary>Swap a shipped screen for one that can show the whole gear,
        /// when the one chosen cannot.
        ///
        /// The two side-by-side layouts give the gear exactly ONE character,
        /// which is right for a game that counts 1 to 6 and useless in Farming
        /// Simulator, where it runs -6 to 18 and some gears are named. Seeing
        /// the whole gear matters more than seeing it huge, so those screens
        /// step aside for the centered four rows: speed small on top, gear large
        /// under it with ten characters to play with.
        ///
        /// Presets only. A custom screen is the user's own arrangement and is
        /// left exactly as built; the editor's slot hint states each slot's
        /// width so a narrow one is a choice rather than a surprise.</summary>
        public static void FitGear(string gear, ref OledLayoutKind kind,
                                   ref string[] slots, ref string[] texts)
        {
            int need = (gear ?? "").Trim().Length;
            if (need <= 1) return;

            int[] w = SlotWidths(kind);
            bool cramped = false;
            for (int i = 0; i < slots.Length && i < w.Length; i++)
                if (slots[i] == "Gear" && w[i] < need) { cramped = true; break; }
            if (!cramped) return;

            kind = OledLayoutKind.FourCenter;
            slots = new[] { "SpeedUnit", "Gear", "None", "None" };
            texts = new string[4];
        }

        // ---- Rendering -----------------------------------------------------

        /// <summary>Turn one slot's field into the text the firmware draws.
        /// An unavailable value renders empty rather than a plausible zero:
        /// the panel has no way to caveat a number it is showing.</summary>
        public static string Render(string fieldKey, string customText,
                                    string gear, double speedKmh, bool useMph,
                                    double? lapDelta, bool deltaOk,
                                    int position, int currentLap, int totalLaps, int lastLapMs,
                                    int slotWidth)
        {
            switch (fieldKey)
            {
                case "Gear": return GearText(gear, slotWidth);
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

        /// <summary>A gear, fitted to the space the layout actually gives it.
        ///
        /// Not every game counts 1 to 6. Farming Simulator runs -6 to 18, so
        /// the old "take the first character" rule turned 18 into 1 and -6 into
        /// a bare minus sign, which is worse than showing nothing: both read as
        /// a plausible, wrong gear. Anything that fits is now printed in full.
        ///
        /// A one-character slot genuinely cannot hold "18", so it falls back to
        /// what still means something: any negative gear is reverse, zero is
        /// neutral, single digits are themselves, and a two-digit gear keeps
        /// its LAST digit, because that is the one that changes as you shift.
        /// That last case is ambiguous by construction; the editor's slot hint
        /// says so, and the four-row screens have room for the real thing.</summary>
        public static string GearText(string gear, int maxWidth)
        {
            string g = (gear ?? "").Trim();
            if (g.Length == 0) return "-";
            if (maxWidth <= 0) return "";
            // Neutral BEFORE the fits-verbatim return below. A source that
            // reports neutral as "0" (Farming Simulator does, whenever the mod
            // has no gear name for the vehicle) is one character long, so it
            // would otherwise pass straight through and put a literal 0 on the
            // panel where every other game shows N.
            if (g == "0") return "N";
            if (g.Length <= maxWidth) return g;

            bool negative = g[0] == '-';
            if (maxWidth == 1)
            {
                if (negative) return "R";
                return g.Substring(g.Length - 1);
            }
            // Wider but still short: keep the sign, then the rightmost digits,
            // so the sign is never the thing that gets dropped.
            string digits = negative ? g.Substring(1) : g;
            int room = negative ? maxWidth - 1 : maxWidth;
            if (room < 1) room = 1;
            if (digits.Length > room) digits = digits.Substring(digits.Length - room);
            return negative ? "-" + digits : digits;
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
                // Each slot is validated against ITS OWN catalog: a text field
                // stored against a slot that is now a meter (or the reverse,
                // after a layout change) falls back to Empty rather than
                // rendering as something the slot cannot draw.
                string[] catalog = SlotIsGauge(kind, i) ? GaugeFieldKeys : FieldKeys;
                outSlots[i] = (v != null && Array.IndexOf(catalog, v) >= 0) ? v : FieldNone;
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
