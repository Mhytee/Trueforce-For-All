// Engine firing-order patterns for EnginePulseEffect.
//
// The current pulse synthesis is a single sinusoid at firing frequency
// (RPM/60 × cyl/2). That gets the pitch right, but misses the *character*:
// a real V8 cross-plane has a "lopey" burble that a uniform pulse train
// can't reproduce, and a Ducati L-twin has a long gap between firings
// that a smooth sine completely flattens out.
//
// A firing pattern is described by:
//   Positions[]:  the phase within one full 720° (4-stroke) engine cycle
//                 at which each cylinder fires. Values in [0, 1).
//   Amplitudes[]: optional per-pulse amplitude weighting (default 1.0).
//                 Used to model cross-plane V8's secondary-order rumble:
//                 alternating L-R-L-L-R-L-R-R firing across the manifold
//                 produces uneven exhaust pulses per bank, perceived as a
//                 subharmonic at half the firing rate.
//
// For combustion engines we treat one engine cycle = 720° crank = 2 revs.
// Cycles per second = RPM / 120. The wavetable synthesis path renders one
// cycle of the pattern as a periodic waveform and plays it back at the
// cycle frequency, giving every pulse its true relative timing.
//
// EngineConfig is a small enum the user picks (or "Auto" infers from
// cylinder count). FiringPatternDb.Resolve returns the appropriate pattern.

using System;
using System.Globalization;

namespace TrueforceForAll.Plugin.Effects
{
    /// <summary>
    /// Engine layout selector. "Auto" infers from cylinder count using the
    /// most common modern configuration (V6 60° even-fire, V8 cross-plane,
    /// etc.). Explicit values let users dial in characterful engines that
    /// don't match the modern default.
    ///
    /// Kept as the internal type for CarCylinderResolver + BuiltinCarCylinders
    /// (which think in terms of cyl + layout pairs). The UI and EnginePulseEffect
    /// speak the flat <see cref="EngineLayout"/> instead. Translate via
    /// <see cref="FiringPatternDb.LayoutFromLegacy"/> at the boundary.
    /// </summary>
    public enum EngineConfig
    {
        Auto,            // pick from cylinder count
        Single,          // 1 cyl
        Inline,          // straight-N, even-fire (default for I3 / I4 / I5 / I6)
        Boxer,           // horizontally opposed, same firing intervals as inline
        V60,             // 60° V, even-fire V6 / V12
        V90Even,         // 90° V, even-fire V8 (flat-plane)
        V8CrossPlane,    // 90° V8, cross-plane crank, "lopey" American
        V8FlatPlane,     // 90° V8, flat-plane crank, Ferrari / Lotus / GT350
        V6OddFire,       // 90° V6 with shared crankpins, older Buick 3.8 etc.
        VTwin90,         // 90° V-twin (Ducati L-twin)
        VTwin45,         // 45° V-twin shared crankpin (Harley big-twin "potato")
        Rotary,          // Wankel, approximated as 2 firings per e-shaft rev
        Custom,          // user-supplied Positions / Amplitudes
    }

    /// <summary>
    /// Flat enum of every engine the user can pick from the dropdown. Each
    /// entry implies its own cylinder/rotor count and firing pattern, so the
    /// UI no longer needs a separate "cylinders" control. Append-only
    /// removing or renumbering invalidates any user's saved preset value.
    ///
    /// Auto = resolver decides (or fall back to a generic 6-cyl even-fire).
    /// Electric = treat as EV (no firing pattern, AutoGainScale takes over).
    /// Custom = user-supplied <see cref="FiringPattern"/> via the textbox.
    /// </summary>
    public enum EngineLayout
    {
        Auto,
        Electric,
        Single,
        Twin,
        Inline3,
        Inline4,
        Inline4CrossPlane,
        Inline5,
        Inline6,
        Boxer4,
        Boxer6,
        V6_60Even,
        V6_OddFire,
        V8CrossPlane,
        V8FlatPlane,
        V10_72,
        V12_60,
        W12_W16,
        VTwin90,
        VTwin60,
        VTwin45,
        V4,
        Rotary1,
        Rotary2,
        Rotary3,
        Rotary4,
        Custom,
        // Appended after Custom so existing presets keep their saved
        // values; legal because the field is serialized by name, not by
        // ordinal, and Newtonsoft tolerates trailing additions.
        Twin180,        // 180° parallel twin (Yamaha MT-07, R7, Aprilia RS660), 0°/180° firings then 540° silence
        V4TwinPulse,    // Ducati Panigale V4 "Twin Pulse", 0°/90°/290°/380°
    }

    /// <summary>One engine cycle's worth of firing events. Positions in
    /// [0, 1) representing phase within 720° crank (2 revs). Amplitudes
    /// default to 1.0 if not supplied.</summary>
    public sealed class FiringPattern
    {
        public string   Name        { get; set; }
        public double[] Positions   { get; set; }
        public double[] Amplitudes  { get; set; }   // null => all 1.0
        /// <summary>Number of firing events in one 720° cycle. Used by the
        /// synthesis math to scale amplitude vs the equivalent uniform
        /// pulse train, so swapping configs at the same cylinder count
        /// doesn't change overall loudness.</summary>
        public int Pulses => Positions?.Length ?? 0;

        public FiringPattern Clone()
        {
            return new FiringPattern
            {
                Name = Name,
                Positions  = Positions  == null ? null : (double[])Positions.Clone(),
                Amplitudes = Amplitudes == null ? null : (double[])Amplitudes.Clone(),
            };
        }
    }

    /// <summary>
    /// Built-in firing patterns + auto-resolution from (cyl, config).
    /// All patterns are normalized so Positions are sorted, in [0, 1), and
    /// the first entry is 0. Amplitudes are null when uniform.
    /// </summary>
    public static class FiringPatternDb
    {
        // ---------- public API ----------

        /// <summary>Resolve a firing pattern from a configured layout +
        /// cylinder count. Falls back to a uniform even-fire pattern when
        /// the requested combination has no built-in match. Never returns
        /// null, a 1-pulse pattern at phase 0 is the worst case.</summary>
        public static FiringPattern Resolve(int cyl, EngineConfig config)
        {
            if (cyl < 1) cyl = 1;
            if (cyl > 16) cyl = 16;

            switch (config)
            {
                case EngineConfig.Auto:
                    return ResolveAuto(cyl);

                case EngineConfig.Single:
                    return Even(1, "Single");

                case EngineConfig.Inline:
                case EngineConfig.Boxer:
                    return Even(cyl, config == EngineConfig.Boxer ? $"Boxer-{cyl}" : $"Inline-{cyl}");

                case EngineConfig.V60:
                    return Even(cyl, $"V{cyl} 60°");

                case EngineConfig.V90Even:
                    return Even(cyl, $"V{cyl} 90° even");

                case EngineConfig.V8CrossPlane:
                    return CrossPlaneV8();

                case EngineConfig.V8FlatPlane:
                    return Even(8, "V8 flat-plane");

                case EngineConfig.V6OddFire:
                    return V6OddFire();

                case EngineConfig.VTwin90:
                    // 90° L-twin (Ducati): 270° / 450° firing split.
                    return new FiringPattern
                    {
                        Name = "V-twin 90° (Ducati)",
                        Positions = new[] { 0.0, 270.0 / 720.0 },
                    };

                case EngineConfig.VTwin45:
                    // 45° V-twin (Harley): 315° / 405° split, the "potato".
                    return new FiringPattern
                    {
                        Name = "V-twin 45° (Harley)",
                        Positions = new[] { 0.0, 315.0 / 720.0 },
                    };

                case EngineConfig.Rotary:
                    // Wankel: 2 rotor firings per e-shaft rev for a 2-rotor;
                    // we model 4 evenly-spaced firings per 720° e-shaft cycle
                    // so the haptic firing rate matches what users perceive
                    // (rotaries scream high relative to RPM).
                    return Even(4, "Rotary 2-rotor");

                case EngineConfig.Custom:
                    // Caller supplies the pattern; never built-in.
                    return Even(cyl, "Custom (using even-fire fallback)");

                default:
                    return Even(cyl, $"Even-{cyl}");
            }
        }

        // ---------- public API: flat EngineLayout ----------

        /// <summary>True for the Electric layout. EnginePulseEffect treats
        /// this as a manual override of the resolver's IsElectric flag.</summary>
        public static bool IsElectric(EngineLayout layout) => layout == EngineLayout.Electric;

        /// <summary>Resolve a firing pattern for an <see cref="EngineLayout"/>.
        /// Custom + Auto + Electric have no built-in pattern (caller handles
        /// those specially): Custom uses the user-supplied pattern, Auto falls
        /// back to a generic 6-cyl even-fire, Electric skips synthesis. Every
        /// other layout returns a non-null pattern.</summary>
        public static FiringPattern ResolveLayout(EngineLayout layout)
        {
            switch (layout)
            {
                case EngineLayout.Auto:               return Even(6, "Auto (6-cyl fallback)");
                case EngineLayout.Electric:           return Even(1, "Electric");   // unused, but non-null
                case EngineLayout.Single:             return Even(1, "Single");
                case EngineLayout.Twin:               return Even(2, "Parallel twin");
                case EngineLayout.Inline3:            return Even(3, "Inline-3");
                case EngineLayout.Inline4:            return Even(4, "Inline-4");
                case EngineLayout.Inline4CrossPlane:  return Inline4CrossPlane();
                case EngineLayout.Inline5:            return Even(5, "Inline-5");
                case EngineLayout.Inline6:            return Even(6, "Inline-6");
                case EngineLayout.Boxer4:             return Boxer4Rumble();
                case EngineLayout.Boxer6:             return Even(6, "Boxer-6");
                case EngineLayout.V6_60Even:          return Even(6, "V6 60° even-fire");
                case EngineLayout.V6_OddFire:         return V6OddFire();
                case EngineLayout.V8CrossPlane:       return CrossPlaneV8();
                case EngineLayout.V8FlatPlane:        return Even(8, "V8 flat-plane");
                case EngineLayout.V10_72:             return Even(10, "V10 72° even-fire");
                case EngineLayout.V12_60:             return Even(12, "V12 60° even-fire");
                case EngineLayout.W12_W16:            return Even(12, "W12 / W16 even-fire");
                // Primary-pulse-stronger amplitude weighting on V-twins reflects the
                // way the longer-gap firing builds more manifold pressure before
                // combustion. Depth scaled with bank angle since the iconic
                // asymmetry varies (Harley 45° most pronounced, 60° subtle).
                case EngineLayout.VTwin90:
                    return new FiringPattern { Name = "V-twin 90° (Ducati)", Positions = new[] { 0.0, 270.0 / 720.0 }, Amplitudes = new[] { 1.15, 0.85 } };
                case EngineLayout.VTwin60:
                    return new FiringPattern { Name = "V-twin 60° (Aprilia/KTM)", Positions = new[] { 0.0, 300.0 / 720.0 }, Amplitudes = new[] { 1.08, 0.92 } };
                case EngineLayout.VTwin45:
                    return new FiringPattern { Name = "V-twin 45° (Harley)", Positions = new[] { 0.0, 315.0 / 720.0 }, Amplitudes = new[] { 1.20, 0.80 } };
                case EngineLayout.V4:                 return Even(4, "V4 even-fire (VFR, RSV4)");
                case EngineLayout.V4TwinPulse:        return V4TwinPulse();
                case EngineLayout.Twin180:            return Twin180();
                case EngineLayout.Rotary1:            return Even(2, "Rotary 1-rotor");
                case EngineLayout.Rotary2:            return Even(4, "Rotary 2-rotor");
                case EngineLayout.Rotary3:            return Even(6, "Rotary 3-rotor");
                case EngineLayout.Rotary4:            return Even(8, "Rotary 4-rotor");
                case EngineLayout.Custom:             return Even(4, "Custom (using even-fire fallback)");
                default:                              return Even(6, "Auto (6-cyl fallback)");
            }
        }

        /// <summary>Human-friendly display name for a layout. Used by the
        /// dropdown items and diagnostic readouts.</summary>
        public static string LayoutDisplayName(EngineLayout layout)
        {
            switch (layout)
            {
                case EngineLayout.Auto:               return "Auto (detect from car)";
                case EngineLayout.Electric:           return "Electric";
                case EngineLayout.Single:             return "Single-cylinder";
                case EngineLayout.Twin:               return "Parallel twin 360° (Honda CB-style)";
                case EngineLayout.Twin180:            return "Parallel twin 180° (MT-07, RS660)";
                case EngineLayout.Inline3:            return "Inline-3";
                case EngineLayout.Inline4:            return "Inline-4";
                case EngineLayout.Inline4CrossPlane:  return "Inline-4 crossplane (Yamaha R1)";
                case EngineLayout.Inline5:            return "Inline-5 (Audi, Volvo)";
                case EngineLayout.Inline6:            return "Inline-6 (BMW, 2JZ)";
                case EngineLayout.Boxer4:             return "Boxer-4 (Subaru)";
                case EngineLayout.Boxer6:             return "Boxer-6 (Porsche 911)";
                case EngineLayout.V6_60Even:          return "V6 60° even-fire";
                case EngineLayout.V6_OddFire:         return "V6 90° odd-fire (older muscle)";
                case EngineLayout.V8CrossPlane:       return "V8 cross-plane (American muscle)";
                case EngineLayout.V8FlatPlane:        return "V8 flat-plane (Ferrari, GT350)";
                case EngineLayout.V10_72:             return "V10 72° (F1, R8, Huracan)";
                case EngineLayout.V12_60:             return "V12 60° (Ferrari, Lambo, F1)";
                case EngineLayout.W12_W16:            return "W12 / W16 (Bentley, Bugatti)";
                case EngineLayout.VTwin90:            return "V-twin 90° (Ducati)";
                case EngineLayout.VTwin60:            return "V-twin 60° (Aprilia, KTM)";
                case EngineLayout.VTwin45:            return "V-twin 45° (Harley)";
                case EngineLayout.V4:                 return "V4 even-fire (VFR, RSV4)";
                case EngineLayout.V4TwinPulse:        return "V4 Twin Pulse (Panigale)";
                case EngineLayout.Rotary1:            return "Rotary 1-rotor";
                case EngineLayout.Rotary2:            return "Rotary 2-rotor (RX-7, RX-8)";
                case EngineLayout.Rotary3:            return "Rotary 3-rotor (Cosmo 20B)";
                case EngineLayout.Rotary4:            return "Rotary 4-rotor (787B)";
                case EngineLayout.Custom:             return "Custom (advanced)";
                default:                              return layout.ToString();
            }
        }

        /// <summary>Effective cyl count implied by a layout, used by the
        /// firing-frequency oscillator (RPM/60 × cyl/2). For rotary entries
        /// this returns the firing-equivalent count (rotors × 2) so the
        /// pitch math comes out right. Returns 6 for Auto / Custom (generic
        /// fallback) and 0 for Electric (synth is silent anyway).</summary>
        public static int EffectiveCylinders(EngineLayout layout)
        {
            switch (layout)
            {
                case EngineLayout.Electric:           return 0;
                case EngineLayout.Single:             return 1;
                case EngineLayout.Twin:
                case EngineLayout.Twin180:
                case EngineLayout.VTwin90:
                case EngineLayout.VTwin60:
                case EngineLayout.VTwin45:
                case EngineLayout.Rotary1:            return 2;
                case EngineLayout.Inline3:            return 3;
                case EngineLayout.Inline4:
                case EngineLayout.Inline4CrossPlane:
                case EngineLayout.Boxer4:
                case EngineLayout.V4:
                case EngineLayout.V4TwinPulse:
                case EngineLayout.Rotary2:            return 4;
                case EngineLayout.Inline5:            return 5;
                case EngineLayout.Inline6:
                case EngineLayout.Boxer6:
                case EngineLayout.V6_60Even:
                case EngineLayout.V6_OddFire:
                case EngineLayout.Rotary3:            return 6;
                case EngineLayout.V8CrossPlane:
                case EngineLayout.V8FlatPlane:
                case EngineLayout.Rotary4:            return 8;
                case EngineLayout.V10_72:             return 10;
                case EngineLayout.V12_60:
                case EngineLayout.W12_W16:            return 12;
                case EngineLayout.Auto:
                case EngineLayout.Custom:
                default:                              return 6;
            }
        }

        /// <summary>Translate legacy (Cylinders, EngineConfig, IsElectric)
        /// state into the flat <see cref="EngineLayout"/>. Used both at the
        /// resolver-output boundary (to write AutoLayout) and during a
        /// one-time settings migration. <paramref name="cyl"/> = 0 is the
        /// legacy "auto" sentinel.</summary>
        public static EngineLayout LayoutFromLegacy(int cyl, EngineConfig cfg, bool isElectric)
        {
            if (isElectric) return EngineLayout.Electric;
            if (cfg == EngineConfig.Custom) return EngineLayout.Custom;

            int n = cyl;
            if (n < 0) n = 0;

            switch (cfg)
            {
                case EngineConfig.Single:        return EngineLayout.Single;
                case EngineConfig.V8CrossPlane:  return EngineLayout.V8CrossPlane;
                case EngineConfig.V8FlatPlane:   return EngineLayout.V8FlatPlane;
                case EngineConfig.V6OddFire:     return EngineLayout.V6_OddFire;
                case EngineConfig.VTwin90:       return EngineLayout.VTwin90;
                case EngineConfig.VTwin45:       return EngineLayout.VTwin45;

                case EngineConfig.Rotary:
                    // Legacy convention: cyl is firing-equivalent (rotors × 2).
                    switch (n)
                    {
                        case 2:  return EngineLayout.Rotary1;
                        case 4:  return EngineLayout.Rotary2;
                        case 6:  return EngineLayout.Rotary3;
                        case 8:  return EngineLayout.Rotary4;
                        default: return EngineLayout.Rotary2;
                    }

                case EngineConfig.Inline:
                    switch (n)
                    {
                        case 1:  return EngineLayout.Single;
                        case 2:  return EngineLayout.Twin;
                        case 3:  return EngineLayout.Inline3;
                        case 4:  return EngineLayout.Inline4;
                        case 5:  return EngineLayout.Inline5;
                        case 6:  return EngineLayout.Inline6;
                        default: return EngineLayout.Inline4;
                    }

                case EngineConfig.Boxer:
                    return n >= 6 ? EngineLayout.Boxer6 : EngineLayout.Boxer4;

                case EngineConfig.V60:
                    if (n >= 12) return EngineLayout.V12_60;
                    return EngineLayout.V6_60Even;

                case EngineConfig.V90Even:
                    if (n >= 12) return EngineLayout.W12_W16;
                    if (n >= 10) return EngineLayout.V10_72;
                    return EngineLayout.V8FlatPlane;

                case EngineConfig.Auto:
                default:
                    // Best-effort guess from cyl alone for fresh resolver hits
                    // that didn't carry a layout.
                    switch (n)
                    {
                        case 1:  return EngineLayout.Single;
                        case 2:  return EngineLayout.Twin;
                        case 3:  return EngineLayout.Inline3;
                        case 4:  return EngineLayout.Inline4;
                        case 5:  return EngineLayout.Inline5;
                        case 6:  return EngineLayout.V6_60Even;
                        case 8:  return EngineLayout.V8CrossPlane;
                        case 10: return EngineLayout.V10_72;
                        case 12: return EngineLayout.V12_60;
                        case 16: return EngineLayout.W12_W16;
                        default: return EngineLayout.Auto;
                    }
            }
        }

        /// <summary>Inverse of <see cref="LayoutFromLegacy"/> for the layouts
        /// that have a clean (cylinders, EngineConfig) representation. Used by
        /// the community-injection path (synthesizing a CarFacts variant from
        /// a flat layout enum) and by the share-flow prompt. Returns false
        /// for Custom / Electric / Auto / unknown - the caller treats those
        /// as unsubmittable.</summary>
        public static bool TryLayoutToCylAndConfig(EngineLayout layout,
            out int cyl, out EngineConfig cfg)
        {
            switch (layout)
            {
                case EngineLayout.Single:        cyl = 1;  cfg = EngineConfig.Single;       return true;
                case EngineLayout.Twin:          cyl = 2;  cfg = EngineConfig.Inline;       return true;
                case EngineLayout.Inline3:       cyl = 3;  cfg = EngineConfig.Inline;       return true;
                case EngineLayout.Inline4:       cyl = 4;  cfg = EngineConfig.Inline;       return true;
                case EngineLayout.Inline5:       cyl = 5;  cfg = EngineConfig.Inline;       return true;
                case EngineLayout.Inline6:       cyl = 6;  cfg = EngineConfig.Inline;       return true;
                case EngineLayout.Boxer4:        cyl = 4;  cfg = EngineConfig.Boxer;        return true;
                case EngineLayout.Boxer6:        cyl = 6;  cfg = EngineConfig.Boxer;        return true;
                case EngineLayout.V6_60Even:     cyl = 6;  cfg = EngineConfig.V60;          return true;
                case EngineLayout.V6_OddFire:    cyl = 6;  cfg = EngineConfig.V6OddFire;    return true;
                case EngineLayout.V8CrossPlane:  cyl = 8;  cfg = EngineConfig.V8CrossPlane; return true;
                case EngineLayout.V8FlatPlane:   cyl = 8;  cfg = EngineConfig.V8FlatPlane;  return true;
                case EngineLayout.V10_72:        cyl = 10; cfg = EngineConfig.V90Even;      return true;
                case EngineLayout.V12_60:        cyl = 12; cfg = EngineConfig.V60;          return true;
                case EngineLayout.W12_W16:       cyl = 16; cfg = EngineConfig.V90Even;      return true;
                case EngineLayout.VTwin90:       cyl = 2;  cfg = EngineConfig.VTwin90;      return true;
                case EngineLayout.VTwin45:       cyl = 2;  cfg = EngineConfig.VTwin45;      return true;
                case EngineLayout.Rotary1:       cyl = 2;  cfg = EngineConfig.Rotary;       return true;
                case EngineLayout.Rotary2:       cyl = 4;  cfg = EngineConfig.Rotary;       return true;
                case EngineLayout.Rotary3:       cyl = 6;  cfg = EngineConfig.Rotary;       return true;
                case EngineLayout.Rotary4:       cyl = 8;  cfg = EngineConfig.Rotary;       return true;
                default: cyl = 0; cfg = EngineConfig.Auto; return false;
            }
        }

        // ---------- public pattern builders for the custom-engine authoring UI ----------

        /// <summary>Pattern shape used by the custom-engine authoring dialog.
        /// Collapses families that share a pulse-distribution kind (even-fire
        /// covers Inline / Boxer / V60° / V90° / V8 flat-plane / V10 / V12 /
        /// W12-16 / V4) into one entry plus a count spinner. Each shape that
        /// doesn't take a count (V8 cross-plane, V6 odd-fire, V-twins,
        /// Inline-4 crossplane) carries its own pattern with a locked count.
        /// Rotary takes a rotor count (1-4); the generated pattern is
        /// rotors × 2 evenly-spaced firings per 720°.</summary>
        public enum CustomEngineShape
        {
            EvenFire,
            V8CrossPlane,
            V6OddFire,
            VTwin90,
            VTwin60,
            VTwin45,
            Inline4CrossPlane,
            Rotary,
            Twin180,        // 180° crank parallel twin (MT-07, RS660)
            V4TwinPulse,    // Ducati Panigale V4 Twin Pulse
            Boxer4Rumble,   // Subaru-style alternating-amplitude flat-4
        }

        /// <summary>The implied count for a fixed-count shape, or null for
        /// shapes whose count is user-selectable (EvenFire, Rotary). The UI
        /// uses this to lock / unlock the count spinner.</summary>
        public static int? FixedCountForShape(CustomEngineShape shape)
        {
            switch (shape)
            {
                case CustomEngineShape.V8CrossPlane:       return 8;
                case CustomEngineShape.V6OddFire:          return 6;
                case CustomEngineShape.VTwin90:
                case CustomEngineShape.VTwin60:
                case CustomEngineShape.VTwin45:
                case CustomEngineShape.Twin180:            return 2;
                case CustomEngineShape.Inline4CrossPlane:
                case CustomEngineShape.V4TwinPulse:
                case CustomEngineShape.Boxer4Rumble:       return 4;
                default:                                    return null;
            }
        }

        /// <summary>Allowed count range for a shape. The custom-engine dialog
        /// uses this to constrain the spinner. Returns (min, max) inclusive.</summary>
        public static (int Min, int Max) CountRangeForShape(CustomEngineShape shape)
        {
            switch (shape)
            {
                case CustomEngineShape.EvenFire: return (1, 16);
                case CustomEngineShape.Rotary:   return (1, 4);
                default:
                    var n = FixedCountForShape(shape) ?? 1;
                    return (n, n);
            }
        }

        /// <summary>Generate a firing-pattern string (positions / amplitudes
        /// in the same format <see cref="ParseCustom"/> accepts) for a given
        /// shape + count. Used by the custom-engine authoring dialog to pre-
        /// fill the pattern textbox when the user picks a shape or adjusts
        /// the count spinner.</summary>
        public static string BuildPatternString(CustomEngineShape shape, int count)
        {
            FiringPattern pat;
            switch (shape)
            {
                case CustomEngineShape.V8CrossPlane:       pat = CrossPlaneV8(); break;
                case CustomEngineShape.V6OddFire:          pat = V6OddFire(); break;
                case CustomEngineShape.VTwin90:
                    pat = new FiringPattern { Name = "V-twin 90°", Positions = new[] { 0.0, 270.0 / 720.0 } };
                    break;
                case CustomEngineShape.VTwin60:
                    pat = new FiringPattern { Name = "V-twin 60°", Positions = new[] { 0.0, 300.0 / 720.0 } };
                    break;
                case CustomEngineShape.VTwin45:
                    pat = new FiringPattern { Name = "V-twin 45°", Positions = new[] { 0.0, 315.0 / 720.0 } };
                    break;
                case CustomEngineShape.Inline4CrossPlane:  pat = Inline4CrossPlane(); break;
                case CustomEngineShape.Twin180:            pat = Twin180(); break;
                case CustomEngineShape.V4TwinPulse:        pat = V4TwinPulse(); break;
                case CustomEngineShape.Boxer4Rumble:       pat = Boxer4Rumble(); break;
                case CustomEngineShape.Rotary:
                {
                    int rotors = count < 1 ? 1 : (count > 4 ? 4 : count);
                    pat = Even(rotors * 2, $"Rotary {rotors}-rotor");
                    break;
                }
                case CustomEngineShape.EvenFire:
                default:
                {
                    int n = count < 1 ? 1 : (count > 16 ? 16 : count);
                    pat = Even(n, $"Even-fire {n}");
                    break;
                }
            }
            return Format(pat);
        }

        // ---------- pattern factories ----------

        /// <summary>Even-fire pattern with N pulses uniformly spaced over
        /// the 720° cycle.</summary>
        private static FiringPattern Even(int n, string name)
        {
            if (n < 1) n = 1;
            var positions = new double[n];
            for (int i = 0; i < n; i++) positions[i] = (double)i / n;
            return new FiringPattern { Name = name, Positions = positions };
        }

        private static FiringPattern CrossPlaneV8()
        {
            // 8 evenly-spaced pulses but with alternating amplitudes that
            // reflect the unequal exhaust pulse spacing per bank produced
            // by an American firing order (e.g. 1-8-4-3-6-5-7-2). The
            // pattern repeats over a full 720° cycle, producing an audible
            // subharmonic at half the firing rate, the recognizable
            // cross-plane burble. Magnitudes empirically chosen for a
            // clear-but-not-exaggerated "lope": 15% modulation depth.
            var positions = new double[8];
            for (int i = 0; i < 8; i++) positions[i] = (double)i / 8;
            return new FiringPattern
            {
                Name       = "V8 cross-plane",
                Positions  = positions,
                Amplitudes = new[] { 1.00, 0.85, 1.00, 1.15, 0.85, 1.00, 1.15, 0.85 },
            };
        }

        private static FiringPattern Inline4CrossPlane()
        {
            // Yamaha R1 / "Big Bang" inline-4 crossplane crank: firing intervals
            // 270° / 180° / 90° / 180°. Cumulative crank degrees from cyl 1:
            // 0, 270, 450, 540. Mapped into [0, 1) over a 720° cycle.
            //
            // Amplitudes: pulse 1 (after the 360° wrap-around silence) gets a
            // slight emphasis since manifold pressure has recovered; the
            // cluster firings sit just below baseline. Subtle (~10% spread)
            // because the character is dominantly in the timing, not the
            // amplitude.
            return new FiringPattern
            {
                Name = "Inline-4 crossplane (Yamaha R1)",
                Positions = new[]
                {
                       0.0 / 720.0,
                     270.0 / 720.0,
                     450.0 / 720.0,
                     540.0 / 720.0,
                },
                Amplitudes = new[] { 1.05, 0.95, 1.00, 0.95 },
            };
        }

        private static FiringPattern V6OddFire()
        {
            // 90° V6 with shared crankpins, e.g. Buick 3.8 pre-1988.
            // Firing intervals 90°/150°/90°/150°/90°/150°.
            // Cumulative crank degrees: 0, 90, 240, 330, 480, 570.
            return new FiringPattern
            {
                Name = "V6 odd-fire (90°)",
                Positions = new[]
                {
                       0.0 / 720.0,
                      90.0 / 720.0,
                     240.0 / 720.0,
                     330.0 / 720.0,
                     480.0 / 720.0,
                     570.0 / 720.0,
                },
            };
        }

        private static FiringPattern Boxer4Rumble()
        {
            // Subaru-style flat-4. Even-fire timing (0°/180°/360°/540°), but
            // unequal-length headers historically pair cylinders into the
            // "rumble", two firings sound stronger per bank than the other
            // two. Modeled as alternating amplitude weighting which produces
            // a 2× firing-rate emphasis (half-firing-rate subharmonic) that
            // matches the classic boxer character.
            return new FiringPattern
            {
                Name = "Boxer-4 (Subaru rumble)",
                Positions = new[] { 0.0, 0.25, 0.5, 0.75 },
                Amplitudes = new[] { 1.08, 0.92, 1.08, 0.92 },
            };
        }

        private static FiringPattern V4TwinPulse()
        {
            // Ducati Panigale V4 "Twin Pulse": firing intervals
            // 90° / 200° / 90° / 340°. Cumulative crank degrees:
            // 0, 90, 290, 380. Two pairs of closely-spaced firings separated
            // by longer gaps, gives the V4 a V-twin-like cadence.
            return new FiringPattern
            {
                Name = "V4 Twin Pulse (Panigale)",
                Positions = new[]
                {
                       0.0 / 720.0,
                      90.0 / 720.0,
                     290.0 / 720.0,
                     380.0 / 720.0,
                },
            };
        }

        private static FiringPattern Twin180()
        {
            // 180° crank parallel twin (Yamaha MT-07, R7, Aprilia RS660, Trident).
            // Firings at 0° and 180°, then a 540° silence before the cycle
            // wraps. Uneven-fire, produces a strongly asymmetric "crossplane
            // twin" rhythm, completely different from the 360°-crank Honda
            // CB-style parallel twin we map Twin to.
            return new FiringPattern
            {
                Name = "Parallel twin 180° (MT-07, RS660)",
                Positions = new[] { 0.0, 180.0 / 720.0 },
            };
        }

        // ---------- auto-resolution from cylinder count ----------

        private static FiringPattern ResolveAuto(int cyl)
        {
            switch (cyl)
            {
                case 1:  return Even(1, "Single (auto)");
                case 2:  return Even(2, "Twin even-fire (auto)");
                case 3:  return Even(3, "Inline-3 (auto)");
                case 4:  return Even(4, "Inline-4 (auto)");
                case 5:  return Even(5, "Inline-5 (auto)");
                case 6:  return Even(6, "V6 60° / I6 even-fire (auto)");
                case 7:  return Even(7, "Even-7 (auto)");
                case 8:  return CrossPlaneV8();   // most common 8-cyl is American cross-plane
                case 9:  return Even(9, "Even-9 (auto)");
                case 10: return Even(10, "V10 even-fire (auto)");
                case 11: return Even(11, "Even-11 (auto)");
                case 12: return Even(12, "V12 60° (auto)");
                default: return Even(cyl, $"Even-{cyl} (auto)");
            }
        }

        // ---------- custom pattern parsing ----------

        /// <summary>
        /// Parse a user-supplied firing pattern from a comma-separated
        /// string. Two forms accepted:
        ///   "0, 0.25, 0.5, 0.75"                      , positions only
        ///   "0:1.0, 0.25:0.85, 0.5:1.0, 0.75:1.15"    , positions + amplitudes
        /// Whitespace is ignored. Positions are clamped to [0, 1) and
        /// sorted ascending. Returns null on parse failure (caller falls
        /// back to a built-in pattern).
        /// </summary>
        public static FiringPattern ParseCustom(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',');
            if (parts.Length < 1) return null;

            var positions  = new double[parts.Length];
            var amplitudes = new double[parts.Length];
            bool anyAmpSet = false;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length == 0) return null;
                int colon = p.IndexOf(':');
                string posStr = colon < 0 ? p : p.Substring(0, colon).Trim();
                string ampStr = colon < 0 ? null : p.Substring(colon + 1).Trim();
                if (!double.TryParse(posStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pos))
                    return null;
                pos = pos % 1.0;
                if (pos < 0) pos += 1.0;
                positions[i] = pos;
                if (ampStr != null)
                {
                    if (!double.TryParse(ampStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var amp))
                        return null;
                    if (amp < 0) amp = 0;
                    if (amp > 4) amp = 4;
                    amplitudes[i] = amp;
                    anyAmpSet = true;
                }
                else
                {
                    amplitudes[i] = 1.0;
                }
            }

            // Sort positions (and matching amplitudes) ascending.
            var idx = new int[parts.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => positions[a].CompareTo(positions[b]));
            var sortedPos = new double[parts.Length];
            var sortedAmp = new double[parts.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                sortedPos[i] = positions[idx[i]];
                sortedAmp[i] = amplitudes[idx[i]];
            }

            return new FiringPattern
            {
                Name       = "Custom",
                Positions  = sortedPos,
                Amplitudes = anyAmpSet ? sortedAmp : null,
            };
        }

        /// <summary>Format a pattern for the "advanced / submit-this-pattern"
        /// UI textbox so users can copy + paste it back to us when reporting
        /// an engine that doesn't sound right.</summary>
        public static string Format(FiringPattern p)
        {
            if (p == null || p.Positions == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Positions.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.####}", p.Positions[i]);
                if (p.Amplitudes != null)
                    sb.AppendFormat(CultureInfo.InvariantCulture, ":{0:0.##}", p.Amplitudes[i]);
            }
            return sb.ToString();
        }
    }
}
