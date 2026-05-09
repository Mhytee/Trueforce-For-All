// Baked carId ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ effective-cylinder-count lookup for known cars. Used by
// CarCylinderResolver to seed EnginePulseEffect.AutoCylinders (and the
// EV gain scale) without waiting for the user to configure each car.
//
// "Effective cylinders" = the value we feed the firing-frequency formula
// (RPM/60 ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â cyl/2). This is a real cylinder count for piston engines and
// a rotor-equivalent for Wankels (2-rotor ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ 4, 3-rotor ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ 6, 4-rotor ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ 8).
// See EnginePulseEffect.cs for the math derivation.
//
// Coverage today: Assetto Corsa Kunos lineup ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â 55 pre-DLC + 123 ks_* DLC
// = 178 entries. Mods are handled by the heuristic fallback in
// CarCylinderResolver. Engine-swapped mods that share a Kunos carId are
// rare; users override per-car via the existing car-preset system, which
// always wins over this lookup.
//
// Maintenance: when Kunos ships new cars (rare these days), append rows.
// The values come from manufacturer-published engine specs, cross-checked
// against AC's car descriptions.

using System;
using System.Collections.Generic;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    /// <summary>Per-car spec used by CarCylinderResolver. Cylinder count is
    /// 1..12 (12 covers all common engines, including Mazda 4-rotor mapped
    /// to 8). IsElectric flags pure-EV cars whose firing frequency math
    /// doesn't apply ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â the resolver leaves cylinders alone but tells
    /// EnginePulse to halve its amplitude (real EVs aren't silent; many
    /// pump synthetic engine sound, so "muted hum" reads more correctly
    /// than "off"). Hybrids with a real combustion engine are NOT marked
    /// electric ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â their cylinder count is the engine's cylinder count.</summary>
    public readonly struct BuiltinCarSpec
    {
        public int          Cylinders    { get; }
        public bool         IsElectric   { get; }
        /// <summary>Engine layout for firing-order pattern synthesis. Auto
        /// (default) means "let FiringPatternDb pick the modern default
        /// from cyl count" — V6 60° / V8 cross-plane / V12 60°. Explicit
        /// values capture the characterful exceptions: V8 flat-plane
        /// (Ferrari / Lotus / GT350), Boxer (Subaru / Porsche flat),
        /// Rotary (Mazda RX), V-twin variants (Ducati / Harley), etc.</summary>
        public EngineConfig EngineConfig { get; }
        public BuiltinCarSpec(int cyl, bool electric = false, EngineConfig config = EngineConfig.Auto)
        {
            Cylinders = cyl; IsElectric = electric; EngineConfig = config;
        }
        public BuiltinCarSpec(int cyl, EngineConfig config)
            : this(cyl, electric: false, config: config) { }
    }

    internal static class BuiltinCarCylinders
    {
        public static bool TryGet(string gameName, string carId, out BuiltinCarSpec spec)
        {
            spec = default;
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(carId)) return false;
            return ByGame.TryGetValue(gameName, out var inner)
                && inner.TryGetValue(carId, out spec);
        }

        // ---- Assetto Corsa: full Kunos lineup (vanilla + every DLC) ----
        //
        // Cylinder count is the firing-frequency-equivalent count.
        // Rotary engines (Mazda RX-7 13B 2-rotor ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ 4 effective; 787B 4-rotor
        // ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾Ãƒâ€šÃ‚Â¢ 8 effective) match the math derivation in EnginePulseEffect.
        //
        // NOTE: AssettoCorsa is declared before ByGame because C# initializes
        // static fields in declaration order ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â ByGame's initializer references
        // AssettoCorsa, so the inner dict must exist first.

        private static readonly IReadOnlyDictionary<string, BuiltinCarSpec> AssettoCorsa
            = new Dictionary<string, BuiltinCarSpec>(StringComparer.OrdinalIgnoreCase)
        {
            // ----- Pre-DLC (vanilla AC, no `ks_` prefix) -----
            ["abarth500"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.4L Multiair I4 turbo
            ["abarth500_s1"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["alfa_romeo_giulietta_qv"]        = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1750 TBi I4
            ["alfa_romeo_mito_qv"]             = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.4L turbo I4
            ["bmw_1m"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),         // N54B30 I6
            ["bmw_1m_s3"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bmw_m3_e30"]                     = new BuiltinCarSpec(4, EngineConfig.Inline),         // S14B23 I4
            ["bmw_m3_e30_drift"]               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bmw_m3_e30_dtm"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bmw_m3_e30_gra"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bmw_m3_e30_s1"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bmw_m3_e92"]                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // S65B40 V8 cross-plane
            ["bmw_m3_e92_drift"]               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["bmw_m3_e92_s1"]                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["bmw_z4"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),         // N54 I6 (Z4 35is)
            ["bmw_z4_drift"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bmw_z4_s1"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bmw_z4_gt3"]                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // P65 V8 race engine (BMW V8s are cross-plane)
            ["ferrari_312t"]                   = new BuiltinCarSpec(12, EngineConfig.Boxer),         // 1975 F1 flat-12 (180° V12 = boxer-12)
            ["ferrari_458"]                    = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // F136FB V8 flat-plane
            ["ferrari_458_gt2"]                = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ferrari_458_s3"]                 = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ferrari_599xxevo"]               = new BuiltinCarSpec(12, EngineConfig.V60),           // F140-derived V12 60°
            ["ferrari_f40"]                    = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // F120A 2.9L V8 twin-turbo flat-plane
            ["ferrari_f40_s3"]                 = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ferrari_laferrari"]              = new BuiltinCarSpec(12, EngineConfig.V60),           // F140 V12 60°
            ["ktm_xbow_r"]                     = new BuiltinCarSpec(4, EngineConfig.Inline),         // Audi 2.0 TFSI EA113
            ["lotus_2_eleven"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),         // Toyota 2ZZ-GE supercharged I4
            ["lotus_2_eleven_gt4"]             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_49"]                       = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // Cosworth DFV (flat-plane)
            ["lotus_98t"]                      = new BuiltinCarSpec(6, EngineConfig.V60),            // Renault EF15B V6 turbo (90° but even-fire — V60 close enough for haptics)
            ["lotus_elise_sc"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),         // Toyota 2ZZ-GE
            ["lotus_elise_sc_s1"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_elise_sc_s2"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_evora_gtc"]                = new BuiltinCarSpec(6, EngineConfig.V60),            // Toyota 2GR-FE V6 60°
            ["lotus_evora_gte"]                = new BuiltinCarSpec(6, EngineConfig.V60),
            ["lotus_evora_gte_carbon"]         = new BuiltinCarSpec(6, EngineConfig.V60),
            ["lotus_evora_gx"]                 = new BuiltinCarSpec(6, EngineConfig.V60),
            ["lotus_evora_s"]                  = new BuiltinCarSpec(6, EngineConfig.V60),
            ["lotus_evora_s_s2"]               = new BuiltinCarSpec(6, EngineConfig.V60),
            ["lotus_exige_240"]                = new BuiltinCarSpec(4, EngineConfig.Inline),         // Toyota 2ZZ-GE
            ["lotus_exige_240_s3"]             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_exige_s"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_exige_s_roadster"]         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_exige_scura"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mclaren_mp412c"]                 = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M838T V8 flat-plane
            ["mclaren_mp412c_gt3"]             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["mercedes_sls"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // M159 V8 cross-plane
            ["mercedes_sls_gt3"]               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["p4-5_2011"]                      = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // Glickenhaus P4/5 Comp - F430-derived V8 flat-plane
            ["pagani_huayra"]                  = new BuiltinCarSpec(12, EngineConfig.V60),           // AMG M158 V12 BiTurbo 60°
            ["pagani_zonda_r"]                 = new BuiltinCarSpec(12, EngineConfig.V60),           // AMG M120 V12 60°
            ["ruf_yellowbird"]                 = new BuiltinCarSpec(6, EngineConfig.Boxer),          // 930-derived flat-6 twin-turbo
            ["shelby_cobra_427sc"]             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // Ford 427 V8 cross-plane
            ["tatuusfa1"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),         // Abarth 1.4L turbo I4

            // ----- DLC (`ks_` prefix) -----
            ["ks_abarth_595ss"]                = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.4L turbo I4
            ["ks_abarth_595ss_s1"]             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_abarth_595ss_s2"]             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_abarth500_assetto_corse"]     = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_alfa_33_stradale"]            = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // Tipo 33 V8 2.0L (Italian race V8 = flat-plane)
            ["ks_alfa_giulia_qv"]              = new BuiltinCarSpec(6, EngineConfig.V60),            // F154-derived V6 2.9L twin-turbo
            ["ks_alfa_giulia_qv_rftuned"]      = new BuiltinCarSpec(6, EngineConfig.V60),
            ["ks_alfa_mito_qv"]                = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.4L turbo I4
            ["ks_alfa_romeo_155_v6"]           = new BuiltinCarSpec(6, EngineConfig.V60),            // 155 V6 TI DTM
            ["ks_alfa_romeo_4c"]               = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.75L turbo I4
            ["ks_alfa_romeo_gta"]              = new BuiltinCarSpec(4, EngineConfig.Inline),         // Giulia Sprint GTA Twin Cam I4
            ["ks_audi_a1s1"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),         // S1 quattro 2.0 TFSI
            ["ks_audi_r18_etron_quattro"]      = new BuiltinCarSpec(6, EngineConfig.V60),            // V6 TDI hybrid (engine cyl)
            ["ks_audi_r8_lms"]                 = new BuiltinCarSpec(10, EngineConfig.V90Even),       // 5.2L V10 90° (Lambo/Audi shared)
            ["ks_audi_r8_lms_2016"]            = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_audi_r8_plus"]                = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_audi_s4_97_tuned"]            = new BuiltinCarSpec(6, EngineConfig.V60),            // B5 S4 2.7L biturbo V6
            ["ks_audi_sport_quattro"]          = new BuiltinCarSpec(5, EngineConfig.Inline),         // Group B 2.1L turbo I5
            ["ks_audi_sport_quattro_rally"]    = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["ks_audi_sport_quattro_s1"]       = new BuiltinCarSpec(5, EngineConfig.Inline),         // S1 E2 evolution I5
            ["ks_audi_tt_cup"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),         // 2.0 TFSI
            ["ks_audi_tt_vln"]                 = new BuiltinCarSpec(5, EngineConfig.Inline),         // TT-RS-based 2.5 TFSI I5
            ["ks_bmw_m235i_racing"]            = new BuiltinCarSpec(6, EngineConfig.Inline),         // N55 I6 turbo
            ["ks_bmw_m4"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),         // S55 I6 BiTurbo
            ["ks_bmw_m4_akrapovic"]            = new BuiltinCarSpec(6, EngineConfig.Inline),
            // NOTE: ks_bmw_m4_g_power_V1_Modified_By_VincToreto_Drift carries the
            // ks_ prefix but is a third-party mod, not a Kunos car. Heuristic
            // fallback handles it.
            ["ks_corvette_c7_stingray"]        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // LT1 V8 cross-plane
            ["ks_corvette_c7r"]                = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // LS5.5R V8 cross-plane
            ["ks_ferrari_250_gto"]             = new BuiltinCarSpec(12, EngineConfig.V60),           // Colombo V12 60°
            ["ks_ferrari_288_gto"]             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // F114B V8 twin-turbo flat-plane
            ["ks_ferrari_312_67"]              = new BuiltinCarSpec(12, EngineConfig.V60),           // 1967 F1 V12 60°
            ["ks_ferrari_330_p4"]              = new BuiltinCarSpec(12, EngineConfig.V60),           // 4.0L V12 60° sports prototype
            ["ks_ferrari_488_challenge_evo"]   = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // F154 V8 twin-turbo flat-plane
            ["ks_ferrari_488_gt3"]             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ks_ferrari_488_gt3_2020"]        = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ks_ferrari_488_gtb"]             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ks_ferrari_812_superfast"]       = new BuiltinCarSpec(12, EngineConfig.V60),           // F140 V12 60°
            ["ks_ferrari_f138"]                = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // 2013 F1 V8 flat-plane
            ["ks_ferrari_f2004"]               = new BuiltinCarSpec(10, EngineConfig.V90Even),       // 2004 F1 V10 053 (Ferrari V10 was 90°)
            ["ks_ferrari_fxx_k"]               = new BuiltinCarSpec(12, EngineConfig.V60),           // F140 V12 hybrid 60°
            ["ks_ferrari_sf15t"]               = new BuiltinCarSpec(6, EngineConfig.V60),            // 2015 F1 V6 turbo hybrid
            ["ks_ferrari_sf70h"]               = new BuiltinCarSpec(6, EngineConfig.V60),            // 2017 F1 V6 turbo hybrid
            ["ks_ford_escort_mk1"]             = new BuiltinCarSpec(4, EngineConfig.Inline),         // Lotus Twin Cam I4
            ["ks_ford_gt40"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // Ford V8 cross-plane
            ["ks_ford_mustang_2015"]           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // Coyote 5.0 V8 cross-plane
            ["ks_glickenhaus_scg003"]          = new BuiltinCarSpec(6, EngineConfig.V60),            // HPD 3.5L V6 twin-turbo (race)
            ["ks_lamborghini_aventador_sv"]    = new BuiltinCarSpec(12, EngineConfig.V60),           // L539 V12 60°
            ["ks_lamborghini_countach"]        = new BuiltinCarSpec(12, EngineConfig.V60),           // L502 V12 60°
            ["ks_lamborghini_countach_s1"]     = new BuiltinCarSpec(12, EngineConfig.V60),
            ["ks_lamborghini_gallardo_sl"]     = new BuiltinCarSpec(10, EngineConfig.V90Even),       // V10 90°
            ["ks_lamborghini_gallardo_sl_s3"]  = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_lamborghini_huracan_gt3"]     = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_lamborghini_huracan_performante"] = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_lamborghini_huracan_st"]      = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["ks_lamborghini_miura_sv"]        = new BuiltinCarSpec(12, EngineConfig.V60),           // V12 60°
            ["ks_lamborghini_sesto_elemento"]  = new BuiltinCarSpec(10, EngineConfig.V90Even),       // Gallardo-derived V10
            ["ks_lotus_25"]                    = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // 1962 F1 Coventry Climax V8 (race = flat-plane)
            ["ks_lotus_3_eleven"]              = new BuiltinCarSpec(6, EngineConfig.V60),            // Toyota 2GR-FE V6 supercharged
            ["ks_lotus_72d"]                   = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // Cosworth DFV (flat-plane)
            ["ks_maserati_250f_12cyl"]         = new BuiltinCarSpec(12, EngineConfig.V60),           // explicit in carId, 1950s F1 V12
            ["ks_maserati_250f_6cyl"]          = new BuiltinCarSpec(6, EngineConfig.Inline),         // explicit in carId, 1950s F1 I6
            ["ks_maserati_alfieri"]            = new BuiltinCarSpec(6, EngineConfig.V60),            // F154-derived V6 (concept)
            ["ks_maserati_gt_mc_gt4"]          = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M139 (Ferrari F136 derivative) flat-plane
            ["ks_maserati_levante"]            = new BuiltinCarSpec(6, EngineConfig.V60),            // F160 V6 twin-turbo
            ["ks_maserati_mc12_gt1"]           = new BuiltinCarSpec(12, EngineConfig.V60),           // Enzo F140-derived V12
            ["ks_maserati_quattroporte"]       = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M139 V8 4.7L (F136 derivative)
            ["ks_mazda_787b"]                  = new BuiltinCarSpec(8, EngineConfig.Rotary),         // R26B 4-rotor (cyl=8 equivalent)
            ["ks_mazda_miata"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),         // 1.6L I4
            ["ks_mazda_mx5_cup"]               = new BuiltinCarSpec(4, EngineConfig.Inline),         // 2.0L SkyActiv I4
            ["ks_mazda_mx5_nd"]                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_mazda_rx7_spirit_r"]          = new BuiltinCarSpec(4, EngineConfig.Rotary),         // 13B 2-rotor (cyl=4 equivalent)
            ["ks_mazda_rx7_tuned"]             = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["ks_mclaren_570s"]                = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M838TE V8 flat-plane
            ["ks_mclaren_650_gt3"]             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M838T V8 flat-plane
            ["ks_mclaren_f1_gtr"]              = new BuiltinCarSpec(12, EngineConfig.V60),           // BMW S70 V12 60°
            ["ks_mclaren_p1"]                  = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // M838TQ V8 hybrid flat-plane
            ["ks_mclaren_p1_gtr"]              = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["ks_mercedes_190_evo2"]           = new BuiltinCarSpec(4, EngineConfig.Inline),         // M102 Cosworth 2.5 I4
            ["ks_mercedes_amg_gt3"]            = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // M159 V8 cross-plane
            ["ks_mercedes_c9"]                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // M119 V8 turbo (Group C) cross-plane
            ["ks_nissan_370z"]                 = new BuiltinCarSpec(6, EngineConfig.V60),            // VQ37VHR V6 60°
            ["ks_nissan_gtr"]                  = new BuiltinCarSpec(6, EngineConfig.V60),            // VR38DETT V6 60°
            ["ks_nissan_gtr_gt3"]              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["ks_nissan_skyline_r34"]          = new BuiltinCarSpec(6, EngineConfig.Inline),         // RB26DETT I6
            ["ks_pagani_huayra_bc"]            = new BuiltinCarSpec(12, EngineConfig.V60),           // AMG M158 V12 60°
            ["ks_porsche_718_boxster_s"]       = new BuiltinCarSpec(4, EngineConfig.Boxer),          // MA1.41 flat-4 turbo
            ["ks_porsche_718_boxster_s_pdk"]   = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["ks_porsche_718_cayman_s"]        = new BuiltinCarSpec(4, EngineConfig.Boxer),          // flat-4 turbo
            ["ks_porsche_718_spyder_rs"]       = new BuiltinCarSpec(6, EngineConfig.Boxer),          // GT3-derived 4.0L flat-6
            ["ks_porsche_908_lh"]              = new BuiltinCarSpec(8, EngineConfig.Boxer),          // 3.0L flat-8 (Porsche racing flat-8)
            ["ks_porsche_911_carrera_rsr"]     = new BuiltinCarSpec(6, EngineConfig.Boxer),          // flat-6
            ["ks_porsche_911_gt1"]             = new BuiltinCarSpec(6, EngineConfig.Boxer),          // Mezger 3.2L flat-6 turbo
            ["ks_porsche_911_gt3_cup_2017"]    = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_porsche_911_gt3_r_2016"]      = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_porsche_911_gt3_rs"]          = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_porsche_911_r"]               = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_porsche_911_rsr_2017"]        = new BuiltinCarSpec(6, EngineConfig.Boxer),          // mid-engined flat-6
            ["ks_porsche_917_30"]              = new BuiltinCarSpec(12, EngineConfig.Boxer),         // flat-12 turbo (180° = boxer-12)
            ["ks_porsche_917_k"]               = new BuiltinCarSpec(12, EngineConfig.Boxer),         // flat-12
            ["ks_porsche_918_spyder"]          = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // 4.6L V8 hybrid (Porsche flat-plane V8)
            ["ks_porsche_919_hybrid_2015"]     = new BuiltinCarSpec(4, EngineConfig.V90Even),        // 2.0L V4 turbo hybrid (LMP1, 90° V4)
            ["ks_porsche_919_hybrid_2016"]     = new BuiltinCarSpec(4, EngineConfig.V90Even),
            ["ks_porsche_935_78_moby_dick"]    = new BuiltinCarSpec(6, EngineConfig.Boxer),          // flat-6 twin-turbo
            ["ks_porsche_962c_longtail"]       = new BuiltinCarSpec(6, EngineConfig.Boxer),          // flat-6 twin-turbo
            ["ks_porsche_962c_shorttail"]      = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_porsche_991_carrera_s"]       = new BuiltinCarSpec(6, EngineConfig.Boxer),          // 3.0L flat-6 twin-turbo
            ["ks_porsche_991_turbo_s"]         = new BuiltinCarSpec(6, EngineConfig.Boxer),          // 3.8L flat-6 twin-turbo
            ["ks_porsche_cayenne"]             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // Cayenne Turbo S V8 (Audi-shared cross-plane)
            ["ks_porsche_cayman_gt4_clubsport"] = new BuiltinCarSpec(6, EngineConfig.Boxer),         // 3.8L flat-6
            ["ks_porsche_cayman_gt4_std"]      = new BuiltinCarSpec(6, EngineConfig.Boxer),          // 981 GT4 flat-6
            ["ks_porsche_macan"]               = new BuiltinCarSpec(6, EngineConfig.V60),            // V6 turbo (Audi-shared)
            ["ks_porsche_panamera"]            = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),   // 4.0L V8 twin-turbo (Audi-shared cross-plane)
            ["ks_praga_r1"]                    = new BuiltinCarSpec(4, EngineConfig.Inline),         // Renault 2.0 turbo I4
            ["ks_ruf_rt12r"]                   = new BuiltinCarSpec(6, EngineConfig.Boxer),          // 911-derived flat-6 twin-turbo
            ["ks_ruf_rt12r_awd"]               = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["ks_toyota_ae86"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),         // 4A-GE I4
            ["ks_toyota_ae86_drift"]           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_toyota_ae86_tuned"]           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ks_toyota_celica_st185"]         = new BuiltinCarSpec(4, EngineConfig.Inline),         // 3S-GTE I4 turbo
            ["ks_toyota_gt86"]                 = new BuiltinCarSpec(4, EngineConfig.Boxer),          // FA20 flat-4
            ["ks_toyota_supra_mkiv"]           = new BuiltinCarSpec(6, EngineConfig.Inline),         // 2JZ-GTE I6
            ["ks_toyota_supra_mkiv_drift"]     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ks_toyota_supra_mkiv_tuned"]     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ks_toyota_ts040"]                = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // 3.7L V8 hybrid (LMP1 race V8 = flat-plane)

            // ===== Heuristic-derived (auto-baked from probe + preserved old-only) =====
            // Cascade: cylword > tag > codename > desc > chassis. EngineConfig comes
            // from the same probe pass; "old-bake" preserves entries no longer detected
            // by the current heuristic so we do not lose coverage on bake refresh.

            // ----- cylword (8 entries) -----
            ["a3dr_ferrari_512tr"]                              = new BuiltinCarSpec(12, EngineConfig.V60),
            ["acw_subaru_grb"]                                  = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["alfa_romeo_giulietta_qv_le"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["BMW_M3_E36_AC_SPEED_VIP"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bmw_x6m_e71_traffic"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_bmw_g80_vsgts_spec"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgn_prvvy_x3m_2022"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgyslo_honda_beat"]                               = new BuiltinCarSpec(3, EngineConfig.Inline),

            // ----- tag (72 entries) -----
            ["ace_charger"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ace_lt1"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ad_am_one77"]                                     = new BuiltinCarSpec(12, EngineConfig.V60),       // Aston Martin One-77, 7.3L V12 60° (probe matched stray "V8" in description)
            ["art_diablo_gtr"]                                  = new BuiltinCarSpec(12, EngineConfig.V60),
            ["as_aston_martin_victor"]                          = new BuiltinCarSpec(12, EngineConfig.V60),
            ["BigRedStuntsCat"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["bz_corvette_zr1_c6_v2"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["CesarYeeMiata"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["cityboi_c7"]                                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["CityBoiV2"]                                       = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["de_accord_12_v3"]                                 = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["gbe_corvette_z06_prvvy_tuned"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["GioCamaroSS"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["gsp_charger_pursuit_a"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["j8_honda_nsx_na1_type_r"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["j8_honda_nsx_tuned"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["kc_3v_v2"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_c5"]                                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_challenger"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_chevyss_v2"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_fbody"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_gxgxp"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_sn95"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_v1"]                                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["mby_ford_foxbody_gt"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["NForce_326_c5"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_astonmartin_vantage_vlct"]                 = new BuiltinCarSpec(12, EngineConfig.V60),
            ["nohesi_chevrolet_corvette_c6"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_ct5v"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_ford_mustang_gt350"]                       = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),    // GT350 Voodoo 5.2L flat-plane V8 (only American factory flat-plane V8)
            ["nohesi_ford_mustang_gtd"]                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesituned_dodge_viper"]                         = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["nstymobn_ss"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_lexus_isf_aimgain"]                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_mustang_2024_tuned"]                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ps_350z"]                                         = new BuiltinCarSpec(6, EngineConfig.V60),
            ["ps_e36"]                                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["public_2012_camaro"]                              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_350z"]                                     = new BuiltinCarSpec(6, EngineConfig.V60),
            ["public_3v_v2"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_c5"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_ctsv1"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_e36"]                                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["public_fbody"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_g35_coupe_v2"]                             = new BuiltinCarSpec(6, EngineConfig.V60),
            ["public_g35_v2"]                                   = new BuiltinCarSpec(6, EngineConfig.V60),
            ["public_gs400_v1"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_gto_v5"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_is300_v2"]                                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["public_newedge2"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_rt"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_s550_v1"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_sc400"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane), // Lexus SC400, 1UZ-FE V8 cross-plane — probe tag "i6" was wrong
            ["public_srt8_v2"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_ss"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_vic"]                                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["public_wbscat"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["statik_camaro"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["TrailblazerSS"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_c6z06"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_c7_v2"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_cateye_v2"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_ctsvcoupe"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_fox"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_g8gt_v2"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_miata"]                                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["vip_s550"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_scatpack"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_ws6"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["wlf_mitsubishi_fto"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["woo_ss"]                                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ybfavbarber_g8"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),

            // ----- codename (86 entries) -----
            ["1stclasspilot_e36"]                               = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["adc_toyota_cresta__420"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_toyota_jzx100_chaser__420"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_toyota_ke70_flat"]                            = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["adc_toyota_ke70_quad"]                            = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["atdt_bmw_e46_m3"]                                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["atdt_s2000_a_spec"]                               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bg_mercedes_amg_gt_black_jp"]                     = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["cfd23_brz_banet"]                                 = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["cfd23_nissan_s14_s_stoeckli"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ddm_nissan_skyline_hr31_house"]                   = new BuiltinCarSpec(6, EngineConfig.V60),
            ["dngs_toyota_ae86_levin_coupe"]                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["dthwsh_nissan_240sx_c_missile"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["dthwsh_nissan_240sx_onevia_wonder"]               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["dthwsh_nissan_silvia_ps13_bn"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["dthwsh_nissan_silvia_ps13_miyabi"]                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["dthwsh_nissan_silvia_ps13_w9"]                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["dthwsh_nissan_silvia_s14_kouki_doof"]             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["dthwsh_nissan_silvia_s15_psduce"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["exclusive_300"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["fumi_cp_bmw_e36_3ff_zoub"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_cp_bmw_e36_starfo"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_cp_bmw_jzx36"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_cp_nissan_200sx_s13"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["fumi_cp_nissan_ps13_rb25"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_cp_nissan_s14_fdc"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_cp_nissan_s15_2jz"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["fumi_kortug_street_s15"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gd_chevy_vette_c3_zr1"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["glo93foxbody"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["gmp_abflug_s900"]                                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gravygarage_oaktree_street_s13_tye"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gravygarage_oaktree_street_s13_tye_2023"]         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gravygarage_revive_street_s13_matt"]              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["gravygarage_street_s14_draper"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["idp1st_takumi_fujiwara_ae86_trueno"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp2nd3rd4th_takumi_fujiwara_ae86_trueno"]        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp4th_takumi_fujiwara_ae86_trueno_carbon"]       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp5th_takumi_fujiwara_ae86_trueno_carbon"]       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ltkaeri_honda_s2000_gt1_amuse"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mitsubishi_starion_race"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["mitsubishi_starion_race_rally"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["NForce_BN_S15"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["NForce_G35"]                                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["NForce_onevia"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["NForce_S14"]                                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_s550_mustang"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nissan_skyline_r34_omori_factory_s1"]             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nissan_skyline_r34_v-specperformance"]            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_chrysler_300_hellcat"]                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_hellcat_charger"]                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_hellcat_redeye_challenger"]                = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_hellcat_redeye_charger"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["sh_nissan_rps13"]                                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["some1_corvette_c4_zr1_1990_s1"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["streetcarpack_nissan_rps13_180sx"]                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_sil80"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_silvia_s13"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_silvia_s14_zenki"]           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["tando_buddies_180sx"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["tando_buddies_cresta"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_laurel"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_s14"]                               = new BuiltinCarSpec(4, EngineConfig.V8CrossPlane),
            ["tgyslo_toyota_ae86_shinji"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["vdc_bmw_e92_public"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_nissan_r33_public"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_nissan_s14_zenki_public"]                     = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["vdc_nissan_s15_public"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_nissan_s15_public_2jz"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_toyota_gt86_public"]                          = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["vdc_toyota_jzx100_markii_public"]                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_toyota_supra_mkiii_public"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vdc_toyota_supra_mkv_public"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["vip_hellcat"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_v2_sedan"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["wdts_nissan_180sx"]                               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wdts_nissan_laurel_c33"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["wdts_nissan_silvia_s13"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wdts_nissan_silvia_s14"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wdts_nissan_silvia_s15"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wdts_nissan_skyline_hr34"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["wdts_nissan_skyline_r32"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["wdts_toyota_ae86"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wdts_toyota_cresta_jzx100"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["wdts_toyota_mark_ii_jzx90"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["yg_mitsubishi_lancer_evolution_9_varis"]          = new BuiltinCarSpec(4, EngineConfig.Inline),

            // ----- desc (121 entries) -----
            ["act_oom500"]                                      = new BuiltinCarSpec(6, EngineConfig.Inline),    // BMW M5 E34 OOM-500 (250 km/h tune), S38B36 / S38B38 I6
            ["as_singer_dls"]                                   = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["audi_rs3_2022_LMC"]                               = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["bati_e46_nspec85"]                                = new BuiltinCarSpec(10, EngineConfig.Inline),
            ["bdc_streetspec_350z_v4"]                          = new BuiltinCarSpec(4, EngineConfig.V60),
            ["bdc_streetspec_ae86_v4"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_altezza_v4"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_aristo_v4"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_e36_v4"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_miata_v4"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_r32_v4"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_rx7_v4"]                           = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["bdc_streetspec_s13_v4"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bdc_streetspec_s14_v4"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bk_dodge_charger_srt_hellcat_redeye_widebody_rftuned"] = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["BMW_M3_COMPETITION_F80_AC_SPEED_X_MIRKOSS"]       = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["bmw_m3_gt2"]                                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["bmw_m6_e63"]                                      = new BuiltinCarSpec(10, EngineConfig.V8CrossPlane),
            ["camaro_zl1_1le_prvvy"]                            = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ChaceG35s"]                                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["cky_evo_ix_mr"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["corvette_c4_zr1_1995"]                            = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ddm_daihatsu_copen_street"]                       = new BuiltinCarSpec(3, EngineConfig.Inline),    // Daihatsu Copen, KF-DET I3 — probe misread "B6" in description as Boxer-6
            ["ddm_mugen_civic_aero_ek9"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_mugen_civic_ek9"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_nissan_silvia_s14k"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_nissan_silvia_s14k_opt"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_toyota_mr2_sw20"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_toyota_mr2_sw20_shuto"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_toyota_mrs_c_one"]                            = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_toyota_mrs_haru"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_toyota_supra_ma70"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ford_anglia_1966"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),    // Kent OHV I4 (probe Boxer was wrong)
            ["ford_anglia_1966_s1"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),    // Kent OHV I4 (S1 trim)
            ["fumi_cp_bmw_e36_missil"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gd_toyota_supra_gr"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),    // GR Supra A90, B58 I6
            ["gmp_e60_m5_normal_manual"]                        = new BuiltinCarSpec(10, EngineConfig.V8CrossPlane),
            ["gravygarage_street_omega"]                        = new BuiltinCarSpec(6, EngineConfig.V60),    // Holden Omega V6 (V8 swaps overridden per-car)
            ["honda_prelude_88"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hsrc_subaru_gc8"]                                 = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["ld_austin_na"]                                    = new BuiltinCarSpec(4, EngineConfig.Inline),    // LD Austin NA Miata B6ZE I4
            ["ld_josh_370z_v2"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),    // Nissan 370Z with V8 swap
            ["ld_mike_fc3s"]                                    = new BuiltinCarSpec(4, EngineConfig.Rotary),    // FC RX-7 13B 2-rotor (cyl=4 equiv)
            ["ld_trey_gt86"]                                    = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["lotus_esprit_V8"]                                 = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["lotus_exige_v6_cup"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["mercedes_amg_c63s_2017_tuned"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["Mercedes_Benz_C63_AMG_Brabus_w204_swap"]          = new BuiltinCarSpec(6, EngineConfig.V8CrossPlane),
            ["mercedes_clk_gtr_supersport"]                     = new BuiltinCarSpec(12, EngineConfig.V8FlatPlane),
            ["mmpower_bmw_e39_m5"]                              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ms_bmw_m3_e46"]                                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ms-mazda_mx5_nb2_01-se_brg-v1.4"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["NForce_a80_supra"]                                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_a90_supra"]                                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_Altezza"]                                  = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_bdc_e36"]                                  = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_E46"]                                      = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["NForce_M4"]                                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_s15"]                                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nissan_300zx"]                                    = new BuiltinCarSpec(10, EngineConfig.V60),
            ["nohesi_bmw_e90"]                                  = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["nohesi_bmw_m2_f87_comp_kyrex"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_c63_23"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_cadillac_ctsv_vlct"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_lamborghini_svj_63"]                       = new BuiltinCarSpec(12, EngineConfig.V60),
            ["nohesi_m5_comp"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_m5_e60_hjckd"]                             = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["nohesi_mercedes_c63_amg_w204_blackseries"]        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_mercedes_e63_w213"]                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_police_ford_explorer"]                     = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_police_ford_f150"]                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_police_hellcat_charger"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_police_m5_comp"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_porsche_gt3rs"]                            = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_realistic_audi_rs6_c8"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_bmw_f82_m4lci_vlct"]             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_bmw_m2_f87_comp_kyrex"]          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_bmw_m3_e46"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_cadillac_ctsv_vlct"]             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_chevrolet_c8_zr1"]               = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["nohesi_realistic_ford_mustang_gtd"]               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_ford_mustang_terminator_vlct"]   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_lamborghini_urus_chris"]         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_lamborghini_urus_performante_vlct"] = new BuiltinCarSpec(8, EngineConfig.V90Even),
            ["nohesi_realistic_lexus_is500"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_mercedes_c63_amg_w204_blackseries"] = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_mercedes_e63_w213"]              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_mercedes_gt63"]                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_mercedes_gt63se"]                = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_nissan_gtr_r35_vlct"]            = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_realistic_porsche_gt3rs_vlct"]             = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_realistic_toyota_chaser_jzx100"]           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_toyota_gr86_adro"]               = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["nohesi_toyota_chaser_jzx100"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_traffic_bmw_i7"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_traffic_bmw_x5"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_velocity_one_s"]                           = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["pg_pantera_gr4"]                                  = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["porsche_911_gt3_2022_manual_online"]              = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["prvvy_bmw_335i_2012"]                             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["prvvy_mercedes_benz_e63s_2022"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_x_tgn_bmw_f81_tuned"]                       = new BuiltinCarSpec(6, EngineConfig.V8CrossPlane),
            ["rize_ferrari_f355_challenge_persephone"]          = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["rtm_volkswagen_touareg_r50_traffic"]              = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["set_honda_civic_ek4"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["simhq_c4"]                                        = new BuiltinCarSpec(6, EngineConfig.V8CrossPlane),
            ["simhq_f8x"]                                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["simhq_jzx"]                                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["sl_toyota_supra_mkiv_ridox"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["some1_corvette_c4_zr1_1990"]                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["srp_toyota_supra_mkiv_interceptor"]               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["sts_celica_zzt231"]                               = new BuiltinCarSpec(10, EngineConfig.V8FlatPlane),
            ["subaru_impreza_gc8_sti_typera_v"]                 = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["tgn_bmw_m6_gran_coupe_prvvy"]                     = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["tgn_chevrolet_camaro_z28_nohesi"]                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["tgn_mercedes_benz_amg_gtr"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["tgn_mercedes_benz_cls63_amg_brabus"]              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["vdc_bmw_e46_public"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),    // BMW E46, M54/S54 I6
            ["vdc_bmw_f22_hgk_public"]                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vdc_mitsubishi_evo_x_public"]                     = new BuiltinCarSpec(8, EngineConfig.Inline),
            ["vdc_nissan_silvia_180sx_public"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),    // Silvia/180SX stock SR/CA I4

            // ----- chassis (203 entries) -----
            ["06_ygt_53_bmw_e46"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["06_ygt_53_nissan_350z"]                           = new BuiltinCarSpec(6, EngineConfig.V60),
            ["a3dr_porsche_993_c2"]                             = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["a3dr_porsche_993_c4"]                             = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["a3dr_viper_rt10"]                                 = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["adc_nissan_180sx__420"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["adc_nissan_laurel__420"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_nissan_s14_kouki__420"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["adc_nissan_skyline_r31__420"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_nissan_skyline_r31_sedan__420"]               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_nissan_skyline_r32__420"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_toyota_jzx_90"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_toyota_soarer__420"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["aegis_mitsubishi_lancer_evolution_v_gsr"]         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["alm_supra_a60"]                                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["amy_ek_cup"]                                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["amy_honda_dc2_turbo"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["amy_honda_ek9_turbo"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["art_skyline_r32_gtr"]                             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["as_bmw_m3_g80_rftuned"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["as_bmw_m4_competition_g82_By_Ceky_Performance_XDrive"] = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["as_porsche_964_turbo_s"]                          = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["austin_mini_cooper_s_1000"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["austin_mini_cooper_s_appk"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["aw_nissan_silvia_specr_sp_alt"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bati_e46_nspec54"]                                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bdc_compspec_s14_v2"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bksy_nissan_skyline_r34_vspec_ii_nur"]            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["BMW_M3_G80_CS_AC_SPEED_X_MIRKOSS"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["crl_bmw_m3_g80_tuned"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ddm_honda_civic_fd2"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_honda_s2000_ap1"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_honda_s2000_ap2"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_mitsubishi_evo_iv_gsr"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_nissan_silvia_s15"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ddm_nissan_skyline_bnr32"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ddm_subaru_22b"]                                  = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["ft_porsche_911_carrera_rs_27_touring"]            = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["gaya_street_ae86trueno"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gd_porsche_959"]                                  = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["gmp_bnr34_the_inevitable"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gmp_jzs161_ridox"]                                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["GrA_bmw_e36_compact_Trackday"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gravygarage_beater_jzx90"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gravygarage_oaktree_street_s14_vic"]              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gravygarage_street_180sx_corbett"]                = new BuiltinCarSpec(6, EngineConfig.Inline),    // 180SX with JZ swap (I6)
            ["gravygarage_street_180sx_meade"]                  = new BuiltinCarSpec(6, EngineConfig.Inline),    // 180SX with JZ swap (I6)
            ["gravygarage_street_ae86_readie"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gravygarage_street_e36_compact"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),    // BMW E36 compact, M42/M44 I4
            ["gravygarage_street_e36_touring"]                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),    // BMW E36 touring, V8 swap (M62/LS)
            ["gravygarage_street_e46"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gravygarage_street_jzx90"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["gravygarage_street_miata"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["gravygarage_street_s13_brent"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),    // S13 with JZ swap (I6)
            ["gravygarage_street_s13_tim"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),    // S13 with JZ swap (I6)
            ["gravygarage_street_s14_joel"]                     = new BuiltinCarSpec(6, EngineConfig.Inline),    // S14 with JZ swap (I6)
            ["gravygarage_team_kamikaze_s15_hayden"]            = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hk51_p1_spec_dc5"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hk51_p1_spec_ek9"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hk51_p1_spec_em1"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hk51_p1_spec_em1_pushin_p_tuned"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["hk51_p1_spec_evo8"]                               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["honda_acty_ha3"]                                  = new BuiltinCarSpec(6, EngineConfig.V60),
            ["honda_s2000_a_spec"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp4th_honda_s2000_godarm"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp4th_ichijo_mitsubishi_lancer_evo_vi_tme_gsr"]  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp5th_nissan_skyline_gtr_r32_rin_hojo"]          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ig_impreza_sti_99_ver6"]                          = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["initiald_ae86_levin"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["j8_ae86_tuned_coupe"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["j8_eunos_roadster_tuned"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["j8_nissan_r32_vspec_2"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["j8_subaru_typer_ver6_99"]                         = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["jvs_mitsubishi_evo6_tme"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["kc_e36"]                                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["kc_foxbody"]                                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_s14"]                                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["kc_zl1"]                                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kx_lan_evo_v_initiald"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mby_mercedes_190e_evolution_ii"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mitsubishi_evo8"]                                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mpw_police_dodge_charger_street"]                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ms_mazda_mx5_nd"]                                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ms_nissan_r33_gtst_typem"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_180sx"]                                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["NForce_370z"]                                     = new BuiltinCarSpec(6, EngineConfig.V60),
            ["NForce_E30"]                                      = new BuiltinCarSpec(6, EngineConfig.Rotary),
            ["NForce_gr86"]                                     = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["nohesi_370z_widebody"]                            = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_bimmerplug_m4"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_f82_m4lci_vlct"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m3_e46"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m3_e92_adro"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m3_f80"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m3_g80"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m4_g82_adro_chris"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_chevrolet_c8_zr1"]                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_chevrolet_corvette_c7_zr1_vlct"]           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_ford_mustang_mk1_vlct"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_ford_mustang_terminator_vlct"]             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_g82_comp_coupe"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_honda_s2000"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_kimera_evo37_kz"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_mitsubishi_evo8"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_porsche_992_gt3_adro"]                     = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_porsche_gt3rs_vlct"]                       = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_porsche_singer_abflug"]                    = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_realistic_bmw_m4_g82_adro_chris"]          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_honda_civic_fl5_typer_adro"]     = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_realistic_porsche_992_gt3_adro"]           = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesi_realistic_toyota_supra_mk5_adro"]          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_rs6_c7"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_subaru_wrx_sti_gd_kz"]                     = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["nohesi_toyota_supra_mk4"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_toyota_supra_mk5_adro_widebody"]           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_zl1_1le"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesituned_as_porsche_cayman_718_gt4_rs"]        = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["nohesituned_evon_rftuned"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesituned_nsx-r"]                               = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nypd_charger"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["p3_mitsubishi_evo8"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["porsche_993_gt"]                                  = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["porsche_993_gt_s1"]                               = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["prvvy_bmw_m3_f80_comp_single_turbo"]              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["prvvy_e30_widebody_4rotor_tt"]                    = new BuiltinCarSpec(8, EngineConfig.Rotary),    // E30 with 26B 4-rotor swap
            ["prvvy_honda_civic_sedan_99"]                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["prvvy_porsche_911_ta_spec"]                       = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["prvvy_porsche_gt3rs_2023_stock"]                  = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["prvvy_spec_c7"]                                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvyx_nissan_350z"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["pxh_1"]                                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["rize_traffic_toyota_celica_supra"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["rtp_honda_integra_dc2_spoon2"]                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["simt_nissan_skyline_gtr_32"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["slideboizz_bmw_e46_v1.1"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["spoon_s2000_ap2"]                                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["srp_bcnr33_wangan"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["srp_honda_s2000_legendary"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["srp_mitsubishi_evo_5_kai"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_ek9_typer"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_honda_civic_eg6"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_honda_dc2_typer"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_honda_nsxr"]                        = new BuiltinCarSpec(6, EngineConfig.V60),
            ["streetcarpack_honda_s2000_ap2"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_350z"]                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["streetcarpack_nissan_r32_gtr"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_nissan_r33_gtr"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_nissan_r34_gtr"]                    = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_nissan_silvia_s15"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_skyline_r31"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_subaru_impreza_22b"]                = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["streetcarpack_toyota_gt86"]                       = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["streetcarpack_toyota_trueno_ae86"]                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["sw_porsche_991_turbo_sport_2017"]                 = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["swarm_bullet_r32"]                                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_dan_s14"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_dyouknit_z"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_fluffs_mx5"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_jordi_e46"]                                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_lhd_darktinman_e36"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_lhd_oscars13"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_lhd_scoobys14"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_lhd_sierra_s15"]                            = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_lhd_victor_c5"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["swarm_lhd_zulu_350z"]                             = new BuiltinCarSpec(6, EngineConfig.V60),
            ["swarm_max_e46"]                                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_mitty_cefiro"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_nelson_stagea"]                             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_sam_r32"]                                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_smokey_jzx81"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_tbzstyle_s13"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_ticklles_e46"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_350z"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["tando_buddies_e36"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_jzx100"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_rx7"]                               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["tando_buddies_s15"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),    // S15 Silvia, JZ swap (codename)
            ["tando_buddies_z32"]                               = new BuiltinCarSpec(6, EngineConfig.V60),
            ["tando_buddies_zenki_stock_hood"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["tgn_bmw_m3_e90_2012_tuned"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgn_bmw_m4_g82_tuned"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgn_smart_nypd_cruiser"]                          = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["tgn_x_prvvy_bmw_m340i_g20"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgyslo_honda_integra_dc2_acura"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["traffic_aegis_toyota_markii_taxi"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["traffic_dodge_challenger_srt"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["traffic_dodge_challenger_srt_v2"]                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["traffic_pp_sti_04"]                               = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["traffic_pp_sti_wrx"]                              = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["traffic_pp_z"]                                    = new BuiltinCarSpec(6, EngineConfig.V60),
            ["tw_honda_civic_eg6"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["tw_nissan_sil80"]                                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["twodogscrovettec6v2"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vdc_chevrolet_corvette_public"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vdc_nissan_ps13_public"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["vdc_shelby_supersnake_public"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_s197"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_zl1"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["wdts_toyota_soarer"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["wm_mazda_rx7_fd_rgo"]                             = new BuiltinCarSpec(6, EngineConfig.Rotary),
            ["wm_nissan_fairlady_z_s30"]                        = new BuiltinCarSpec(6, EngineConfig.V60),
            ["wm_nissan_s15"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["wm_porsche_911_930"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),

            // ----- old-bake (176 entries) -----
            ["38_hb_444"]                                       = new BuiltinCarSpec(6),
            ["ac_legends_bmw_2002"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["acdfr_infinity_q60"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["adc_ford_au_falcon_420"]                          = new BuiltinCarSpec(6, EngineConfig.V60),
            ["adc_holden_commodore_vk__420"]                    = new BuiltinCarSpec(6, EngineConfig.V60),
            ["adc_lexus_is200_xe-10__420"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["adc_nissan_fairlady_432__420"]                    = new BuiltinCarSpec(6, EngineConfig.V60),
            ["arch_ruf_ctr_1987"]                               = new BuiltinCarSpec(6, EngineConfig.Boxer),
            ["art_nissan_gtr_bcnr33_600r"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["bati_fd3s_rx7"]                                   = new BuiltinCarSpec(4, EngineConfig.Rotary),     // RX-7 FD3S, 13B-REW 2-rotor (cyl=4 equivalent) — probe misread description
            ["bg_mercedes_amg_one"]                             = new BuiltinCarSpec(6, EngineConfig.V90Even),
            ["bh_civic_turbo"]                                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["bio_g35"]                                         = new BuiltinCarSpec(6, EngineConfig.V60),
            ["bmw_m5_f10_editTakumimlz"]                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["chimmybandido_v2"]                                = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["coastline_mercedes_190e_street"]                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["crown_s210_police"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["ddm_mazda_fc3s_re"]                               = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["ddm_mazda_rx7_infini_fc3s"]                       = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["dzrm8_knight"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ecf_lotus_europa_wolf"]                           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ford_rs200"]                                      = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["ford_transit"]                                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["fullmetal_mercedes_c32"]                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["glo_ss"]                                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["gmp_e60_m5_ericsson"]                             = new BuiltinCarSpec(10, EngineConfig.V90Even),    // E60 M5 S85 V10 90 deg
            ["gmp_e60_m5_ysfactory"]                            = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["gravygarage_oaktree_street_200sx_alex"]           = new BuiltinCarSpec(4, EngineConfig.Inline),    // Nissan 200SX SR/CA I4 (probe matched "V2" version-tag)
            ["gravygarage_street_gs300"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),    // Lexus GS300, 2JZ-GE I6
            ["gravygarage_street_jzx100_mkii"]                  = new BuiltinCarSpec(6, EngineConfig.Inline),    // Toyota JZX100 Mark II, 1JZ-GTE I6
            ["gravygarage_team_kamikaze_c33_ben"]               = new BuiltinCarSpec(6, EngineConfig.Inline),    // Nissan Laurel C33, RB I6
            ["gtr_nismo_24_prvvy_spec"]                         = new BuiltinCarSpec(6, EngineConfig.V60),
            ["HDC_VW_Caddy"]                                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["idp4th_rx7_fd_keisuke"]                           = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["ig_toyota_gr86_premium"]                          = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["infiniti_q50_awd_prvvy"]                          = new BuiltinCarSpec(6, EngineConfig.V60),
            ["j8_mitsubishi_gto_twin_turbo_91"]                 = new BuiltinCarSpec(6, EngineConfig.V60),
            ["j8_mitsubishi_gto_twin_turbo_91_haru_spec"]       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["j8_toyota_celica_tuned"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["j8_toyota_supra_rz_97"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["jf_mclaren_f1_1994"]                              = new BuiltinCarSpec(12, EngineConfig.V60),
            ["kc_g35"]                                          = new BuiltinCarSpec(6, EngineConfig.V60),
            ["kc_g37ipl"]                                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["kc_gs300"]                                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["kc_is300"]                                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["kc_ls400"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["kc_tbss"]                                         = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["ks_bmw_m4_g_power_V1_Modified_By_VincToreto_Drift"] = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["lamborghini_gallardo_superleggera_nasher_Ju"]     = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["lamborghini_murcielago_lp640"]                    = new BuiltinCarSpec(12, EngineConfig.V60),       // Murcielago LP640, L539 V12 60° (probe matched "V2" / "640" tail)
            ["lotus_elise_sport_190_99"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_exos_125"]                                  = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["lotus_exos_125_s1"]                               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["M2_Competition_prvvy_tgn"]                        = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["mlgz_x_prvvy_mercedes_c300_2022_tuned"]           = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["mpw_police_ford_interceptor_utility_street"]      = new BuiltinCarSpec(6, EngineConfig.V60),
            ["mpw_police_ford_taurus_unmarked"]                 = new BuiltinCarSpec(6, EngineConfig.V60),
            ["mtn_victoria"]                                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane), // Crown Victoria / Mercury Marauder, 4.6L modular V8 cross-plane
            ["naz_jza80_ridox_modern"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["NForce_RX8"]                                      = new BuiltinCarSpec(6, EngineConfig.Rotary),
            ["nohesi_audi_rs3_saloon_vlct"]                     = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["nohesi_audi_rs5_f5"]                              = new BuiltinCarSpec(6, EngineConfig.V60),    // Audi RS5 F5, EA839 2.9L V6 BiTurbo
            ["nohesi_audi_rsq8"]                                = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_bentley_continental_gt"]                   = new BuiltinCarSpec(12, EngineConfig.V60),
            ["nohesi_bmw_340i_f30"]                             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_e39_m5_vlct"]                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_bmw_f81_m3"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_g87_adro"]                             = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_g87_adro_v2"]                          = new BuiltinCarSpec(6, EngineConfig.Inline),    // BMW M2 G87, S58 I6 (probe matched "V2" suffix in carId)
            ["nohesi_bmw_m2_f87"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m3_g81"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_m340i_g20_chris"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_x3m_f97_adro"]                         = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_bmw_x5m_f95"]                              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_ferrari_812_nlargo"]                       = new BuiltinCarSpec(12, EngineConfig.V60),
            ["nohesi_infiniti_g37"]                             = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_infiniti_q50s_vlct"]                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_jeep_trackhawk"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_lamborghini_huracan_lp610"]                = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["nohesi_lamborghini_temerario_vlct"]               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_lamborghini_urus_chris"]                   = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_lamborghini_urus_performante_vlct"]        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),    // Urus 4.0 TFSI shared with Audi RS6/RSQ8 (cross-plane)
            ["nohesi_lexus_is500"]                              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_lexus_lfa_nurburgring"]                    = new BuiltinCarSpec(10, EngineConfig.V90Even),
            ["nohesi_lotus_evora_gt430"]                        = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_mazda_rx7_fc_vlct"]                        = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["nohesi_mazda_rx7_fd"]                             = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["nohesi_mclaren_600lt_novitec"]                    = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["nohesi_mclaren_720s"]                             = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["nohesi_mclaren_w1"]                               = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["nohesi_mercedes_brabus_gt600"]                    = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_mercedes_c63_coupe"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_mercedes_cls_c218"]                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_mercedes_gt63"]                            = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_mercedes_w201_vlct"]                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_mitsubishi_3000_gt"]                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_nissan_gtr_r35_vlct"]                      = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nohesi_realistic_audi_rs3_saloon_vlct"]           = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["nohesi_realistic_bmw_f90_m5cs_vlct"]              = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nohesi_realistic_bmw_g87_adro_v2"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_realistic_bmw_x3m_f97_adro"]               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["nohesi_toyota_gr_yaris"]                          = new BuiltinCarSpec(3, EngineConfig.Inline),
            ["nohesi_toyota_gr86_adrowide"]                     = new BuiltinCarSpec(4, EngineConfig.Boxer),
            ["nohesi_volkswagen_golf_r_mk8"]                    = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesi_vw_golf_r"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["nohesituned_quadrifoglio"]                        = new BuiltinCarSpec(6, EngineConfig.V60),
            ["nstymobn_v2"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["nypd_ford"]                                       = new BuiltinCarSpec(6, EngineConfig.V60),
            ["oneweek_hillman_imp"]                             = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["oneweek_hillman_imp_a1"]                          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["peugeot_205_gti_1.9_gutmann_t16v_222cv"]          = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["porsche_vision_960_turismo"]                      = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_audi_rs7_23"]                               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_bmw_f82m4_swim_spec"]                       = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["prvvy_bmw_m340i_g20_freshhkiicks_tuned"]          = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["prvvy_bmw_m5cs_2022_tuned"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_cadillac_ct5vbw_tuned"]                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_infiniti_q50_red_sport_2020"]               = new BuiltinCarSpec(6, EngineConfig.V60),
            ["prvvy_mlgz_tgn_bmw_m2_g87_tuned"]                 = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["prvvy_spec_bmw_m8"]                               = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["prvvy_trackhawk_spec_quan"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["pschd_mazda_rx7_amemiya"]                         = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["pxw_sl_v4"]                                       = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["rda_mazda_rx7"]                                   = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["rize_efini_rx7_fd3s_keisuke_1"]                   = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["rize_traffic_mazda_rx8_se3p"]                     = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["rtm_hennessey_venom_f5"]                          = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),    // Hennessey Fury 6.6L V8 (web-verified cross-plane pushrod, not flat-plane)
            ["sa_e63s_pushin_p_tune"]                           = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["sa_rs3fbo_pushin_p_tuned"]                        = new BuiltinCarSpec(5, EngineConfig.Inline),
            ["simhq_bt"]                                        = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["sjbarzcc_g8"]                                     = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["slang_ferrari_f40"]                               = new BuiltinCarSpec(8, EngineConfig.V8FlatPlane),
            ["snp_zhonghua_zidantou_wangan_spec"]               = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["spear_lamborghini_lp640_veilside"]                = new BuiltinCarSpec(12, EngineConfig.V60),
            ["streetcarpack_celicc_supra"]                      = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_datsun_510"]                        = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_honda_crx"]                         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_mazda_rx7_fc3s"]                    = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["streetcarpack_mazda_rx7_fd3s"]                    = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["streetcarpack_mazda_rx8"]                         = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["streetcarpack_mitsubishi_lancer_evo_gsr"]         = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_nissan_300zx"]                      = new BuiltinCarSpec(6, EngineConfig.V60),
            ["streetcarpack_nissan_skyline_gtr_2000"]           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_toyota_mr2_sw20"]                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["streetcarpack_toyota_supra_jza70"]                = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["streetcarpack_toyota_supra_rz"]                   = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["suzuki_cappuccino_hayabusa_swap"]                 = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["swarm_charlie_jzx100"]                            = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_cowdoy_vf"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["swarm_delta_fdrx7"]                               = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["swarm_flyby_gs300"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_fullagaming_r31"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_Juliett_a70"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_lewi_altezza"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_lhd_foxtrot_foxbody"]                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["swarm_lhd_sc300_suzie"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["swarm_lhd_uzi_fc1uz"]                             = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["swarm_maniac_300zx"]                              = new BuiltinCarSpec(6, EngineConfig.V60),
            ["swarm_narraz_rbs13"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_er34"]                              = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tando_buddies_verossa"]                           = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tgn_infiniti_g37_prvvy"]                          = new BuiltinCarSpec(6, EngineConfig.V60),
            ["tgn_lexus_rcft"]                                  = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["tnt_hv_bmw_csi_m6"]                               = new BuiltinCarSpec(6, EngineConfig.Inline),
            ["tommykaira_zzs_pub"]                              = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["toy_celica_cs"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["toy_celica_rs"]                                   = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["traffic_pp_rx8"]                                  = new BuiltinCarSpec(4, EngineConfig.Rotary),
            ["trvbl_2105_drift"]                                = new BuiltinCarSpec(4, EngineConfig.Inline),
            ["vallejopd_interceptor"]                           = new BuiltinCarSpec(6, EngineConfig.V60),
            ["vdc_mazda_rx7_20b_public"]                        = new BuiltinCarSpec(6, EngineConfig.Rotary),
            ["vdc_mazda_rx8_public"]                            = new BuiltinCarSpec(6, EngineConfig.Rotary),
            ["vdc_nissan_gtr_35_public"]                        = new BuiltinCarSpec(6, EngineConfig.V60),
            ["vdc_nissan_z_public"]                             = new BuiltinCarSpec(6, EngineConfig.V60),
            ["VersedSingleCab"]                                 = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),
            ["vip_ctsv3"]                                       = new BuiltinCarSpec(8, EngineConfig.V8CrossPlane),

        };

        /// <summary>Lookup keyed first by SimHub GameName, then by carId
        /// (case-insensitive on the inner key ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â AC carIds are filesystem
        /// folders so case can vary slightly across Steam vs CM installs).</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, BuiltinCarSpec>> ByGame
            = new Dictionary<string, IReadOnlyDictionary<string, BuiltinCarSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AssettoCorsa"] = AssettoCorsa,
        };
    }
}
