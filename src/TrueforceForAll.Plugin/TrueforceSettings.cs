// Persisted plugin settings. SimHub serializes this to JSON via
// PluginManager.GetCommonSettings / SaveCommonSettings.
//
// The same shape is also written/read by the Export / Import buttons in the
// settings panel — keep field names stable across versions so shared presets
// stay valid.

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    public sealed class TrueforceSettings
    {
        // Master enable. When false, ProducerLoop skips rendering and the
        // wheel is told to return to its native FFB/Trueforce path — useful
        // for games that ship native Trueforce support (iRacing) where our
        // ep3 stream would conflict with the game's own.
        public bool PluginEnabled { get; set; } = true;

        // Per-game auto-remembered enable state. When the active game changes,
        // the plugin looks up this dict and applies the saved value (default
        // true for games never seen before). Independent of preset assignment.
        public Dictionary<string, bool> GameEnabled { get; set; } = new Dictionary<string, bool>();

        // Per-game audio-capture exe override. Keyed by SimHub GameName
        // (including Custom_xxx codes for user-added games), value is the
        // exe basename (no ".exe" suffix). Takes priority over the curated
        // ExeLabels dict and the fuzzy GameName matcher in CaptureTick. Use
        // when a game doesn't get found automatically — type its exe name
        // here and we'll capture from it.
        public Dictionary<string, string> AudioCaptureExeOverrides { get; set; } = new Dictionary<string, string>();

        // Per-(game, carId) cylinder lookup cache. Populated by CarCylinderResolver
        // when its heuristic detects a car not present in the shipped bake — so
        // the next session resolves instantly without re-reading ui_car.json.
        // Schema: outer key = SimHub GameName (e.g. "AssettoCorsa"), inner key
        // = carId, value = effective cylinder count (1..16; 0 reserved for EV
        // sentinel). Plugin owns invalidation via CarCylinderCacheVersion below
        // — bump that integer when the heuristic improves and all caches are
        // discarded next load.
        public Dictionary<string, Dictionary<string, int>> CarCylinderCache { get; set; }
            = new Dictionary<string, Dictionary<string, int>>();

        // Bump when heuristic patterns change in a way that invalidates prior
        // cache entries. On load, CarCylinderResolver compares this to its
        // own constant; mismatch clears the cache so cars get re-detected
        // against the improved heuristic.
        public int CarCylinderCacheVersion { get; set; } = 1;

        public float MasterGain { get; set; } = 1.0f;

        // FFB pass-through tuning. Scale lets users dial down the felt strength
        // when their wheel firmware applies a different gain to ep3 cur than
        // to ep0 PID FFB; invert flips sign in case AC's HID++ feature 0x0e
        // convention disagrees with ep3 cur (default true matches AC). Smooth
        // converts AC's 7ms-staircase FFB target into a ramp by IIR low-pass
        // (0 ms = no smoothing).
        public float FfbScale                 { get; set; } = 1.0f;
        public bool  FfbInvertSign            { get; set; } = true;
        public float FfbSmoothTimeConstantMs  { get; set; } = 0.0f;

        // FFB spike taming: tames AC's over-the-top curb / collision FFB so
        // it lands as a firm shove instead of a wheel-yanking jolt. Two
        // knobs: FfbSpikeMaxLsbPerMs caps slew rate (LSB/ms); FfbPeakSoftLimitLsb
        // sets attenuation strength when slew exceeds the spike-detect
        // threshold. Defaults are the values that feel right on a GPRO; users
        // can fine-tune attenuation in the UI. The rate cap rarely needs to
        // change so it lives behind an Advanced section.
        // Enabled flag gates both: when false, runtime treats them as 0
        // regardless of stored values, so users can flip the feature off
        // without losing their tuning.
        public bool  FfbSpikeTamingEnabled    { get; set; } = false;
        public float FfbSpikeMaxLsbPerMs      { get; set; } = 2060.923f;

        // For games with native Trueforce support (AC Rally, iRacing, etc.):
        // when on, the plugin still streams audio-haptic effects on ep3
        // but leaves bytes 6-9 (cur) at center so the wheel's actual force
        // comes from the game's own FFB path rather than our captured /
        // mirrored value. Avoids fighting with the game's native ep3 cur
        // writes. Default off.
        public bool  SkipFfbPassthrough       { get; set; } = false;

        public float FfbPeakSoftLimitLsb      { get; set; } = 1561.78564f;

        // Sidechain ducking applied to continuous effects (engine pulse, audio
        // capture) when transient effects (gear shift, ABS, road bumps,
        // traction loss) fire. Depth = max attenuation (0 = no duck, 1 = full
        // silence). Attack/Release in ms are the time constants for the
        // envelope's down/up directions.
        public float DuckDepth     { get; set; } = 0.5f;
        public float DuckAttackMs  { get; set; } = 5.0f;
        public float DuckReleaseMs { get; set; } = 80.0f;

        public AudioCaptureSettings AudioCapture { get; set; } = new AudioCaptureSettings();
        public EnginePulseSettings  EnginePulse  { get; set; } = new EnginePulseSettings();
        public RoadBumpsSettings    RoadBumps    { get; set; } = new RoadBumpsSettings();
        public TractionLossSettings TractionLoss { get; set; } = new TractionLossSettings();
        public GearShiftSettings    GearShift    { get; set; } = new GearShiftSettings();
        public AbsClickSettings     AbsClick     { get; set; } = new AbsClickSettings();
        public PitLimiterSettings   PitLimiter   { get; set; } = new PitLimiterSettings();
        public DrsSettings          Drs          { get; set; } = new DrsSettings();

        // Per-machine performance tuning. Lives outside GameSettingsSnapshot
        // because ring sizes are a property of the machine (CPU, scheduler
        // load), not of the game/preset — sharing a preset shouldn't override
        // a friend's tuned ring sizes.
        public PerformanceSettings Performance { get; set; } = new PerformanceSettings();

        // Forza UDP listener config. Same machine-not-game rationale as
        // Performance: the port the user picks is local to their setup. Lives
        // here so it survives preset switches.
        public ForzaSettings Forza { get; set; } = new ForzaSettings();

        // Keyed by GameData.NewData.CarId. Override entries supersede the
        // global engine settings whenever that car is the active one.
        public Dictionary<string, CarOverride> CarOverrides { get; set; } = new Dictionary<string, CarOverride>();

        // Named, portable settings snapshots. Keyed by user-chosen preset name
        // (not by game). The user picks any preset and applies it to any game;
        // game-specific auto-load is configured via GameDefaults below.
        public Dictionary<string, GameSettingsSnapshot> Presets { get; set; } = new Dictionary<string, GameSettingsSnapshot>();

        // Per-game default preset assignment. Maps GameData.GameName to a
        // preset name in Presets. When a game change is detected, if the
        // game has a default assigned, that preset auto-loads.
        public Dictionary<string, string> GameDefaults { get; set; } = new Dictionary<string, string>();

        // Per-car active preset assignment. Maps CarId to a preset name in
        // the on-disk car-preset library (TrueforceCars/). When a car is
        // detected, the assigned preset's CarOverride loads into the live
        // CarOverrides cache. Mirrors GameDefaults: a car can have multiple
        // saved presets (factory + user + imports) and the user picks which
        // is active per car. Unset = fall back to the factory "(default)"
        // preset for that car if one exists, else no override.
        public Dictionary<string, string> CarDefaults { get; set; } = new Dictionary<string, string>();

        // LEGACY (pre-2026-05-04): previously presets were keyed by game name
        // with no separate "preset library" concept. Loaded transparently for
        // backward compat and migrated to Presets + GameDefaults on first
        // plugin Init after upgrade. New code never writes to it.
        public Dictionary<string, GameSettingsSnapshot> GamePresets { get; set; } = new Dictionary<string, GameSettingsSnapshot>();
    }

    /// <summary>Whole-settings snapshot saved per-game. Mirrors the top-level
    /// fields of <see cref="TrueforceSettings"/> minus the GamePresets dict
    /// (to avoid serialization recursion). When loaded, replaces all matching
    /// fields on the active settings.</summary>
    public sealed class GameSettingsSnapshot
    {
        public float MasterGain                { get; set; } = 1.0f;
        public float FfbScale                  { get; set; } = 1.0f;
        public bool  FfbInvertSign             { get; set; } = true;
        public float FfbSmoothTimeConstantMs   { get; set; } = 0.0f;
        public bool  FfbSpikeTamingEnabled     { get; set; } = false;
        public float FfbSpikeMaxLsbPerMs       { get; set; } = 2060.923f;
        public float FfbPeakSoftLimitLsb       { get; set; } = 1561.78564f;
        public bool  SkipFfbPassthrough        { get; set; } = false;
        public float DuckDepth                 { get; set; } = 0.5f;
        public float DuckAttackMs              { get; set; } = 5.0f;
        public float DuckReleaseMs             { get; set; } = 80.0f;

        public AudioCaptureSettings AudioCapture { get; set; }
        public EnginePulseSettings  EnginePulse  { get; set; }
        public RoadBumpsSettings    RoadBumps    { get; set; }
        public TractionLossSettings TractionLoss { get; set; }
        public GearShiftSettings    GearShift    { get; set; }
        public AbsClickSettings     AbsClick     { get; set; }
        public PitLimiterSettings   PitLimiter   { get; set; }
        public DrsSettings          Drs          { get; set; }

        public Dictionary<string, CarOverride> CarOverrides { get; set; }
    }

    public sealed class AudioCaptureSettings
    {
        public bool   Enabled          { get; set; } = true;
        // 0.06 reflects the much-lower-than-1.0 gain that's actually usable
        // in practice. Game audio routed through the wheel as haptics is
        // intense even at 5-10% gain; 1.0 is well past clipping for most
        // games on most wheelbases.
        public float  Gain             { get; set; } = 0.06f;
        public double LowpassCutoffHz  { get; set; } = 350.0;
        public double HighpassCutoffHz { get; set; } =  30.0;
    }

    /// <summary>Mode for the Performance tab. In Auto, the plugin starts at
    /// the smallest ring sizes and ratchets them up (one-way) when underruns
    /// or audio-ring lapping cross a 1-second threshold; the survived value
    /// is persisted across sessions. In Manual, ring sizes are user-fixed —
    /// no automatic changes — for users who want guaranteed-stable behavior
    /// (streamers) or to force-test lower values.</summary>
    public enum PerformanceMode { Auto, Manual }

    /// <summary>Forza Data Out UDP listener. The user enables UDP RACE
    /// TELEMETRY in Forza's Settings → HUD and Gameplay menu and sets the
    /// destination IP and port; the plugin opens a socket on
    /// <see cref="BindAddress"/>:<see cref="Port"/> to receive the packets.
    ///
    /// Two real-world gotchas users hit:
    ///   - MS Store / UWP build: Windows network isolation blocks UDP loopback
    ///     for the Forza AppContainer. They have to run CheckNetIsolation.exe
    ///     to add a loopback exemption (or send to a LAN IP).
    ///   - Steam build: FH5 sends to the gateway IP, not 127.0.0.1, so a naive
    ///     loopback listener gets nothing. They have to send to their LAN IP.
    ///
    /// Default port 5300 picked to avoid colliding with SimHub's typical 4123
    /// or Sim Racing Studio's 4123. AlwaysListen lets users force the listener
    /// on for FH6 launch / unrecognized Forza variants where SimHub's GameName
    /// detection won't have caught up yet.</summary>
    public sealed class ForzaSettings
    {
        public bool   Enabled       { get; set; } = true;
        public int    Port          { get; set; } = 5300;
        public string BindAddress   { get; set; } = "0.0.0.0";

        /// <summary>When true, the listener stays open regardless of which
        /// game SimHub thinks is running. Use for FH6 day-1 (before SimHub's
        /// GameName recognizes it) or any Forza variant where the in-game
        /// telemetry stream is on but the auto-detect fails.</summary>
        public bool   AlwaysListen  { get; set; } = false;

        /// <summary>Re-broadcast every received Forza packet to a second
        /// destination. Solves the "I want SimHub dashboards AND Trueforce
        /// haptics from the same Forza title" coexistence problem: Forza
        /// only sends to one IP+port, so the user points Forza at us and we
        /// relay verbatim (no parsing, no transformation) to SimHub. Default
        /// off so the user explicitly opts in — when off, packets stop here.</summary>
        public bool   ForwardEnabled { get; set; } = false;

        /// <summary>Where to forward each received packet. 127.0.0.1 covers
        /// the common case of SimHub running on the same machine; users with
        /// a separate SimHub host can point here. Ignored when
        /// <see cref="ForwardEnabled"/> is false.</summary>
        public string ForwardHost    { get; set; } = "127.0.0.1";

        /// <summary>UDP port of the secondary listener (typically SimHub's
        /// configured Forza Data Out port — find it in SimHub's
        /// Game → Forza Horizon settings, in the "UDP port" field). Same
        /// value the user originally typed into SimHub when they set it up.
        /// Ignored when <see cref="ForwardEnabled"/> is false.</summary>
        public int    ForwardPort    { get; set; } = 0;
    }

    public sealed class PerformanceSettings
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public PerformanceMode Mode { get; set; } = PerformanceMode.Auto;

        // Trueforce stream ring depth (samples; pow-of-two; 8..64). At 4 kHz
        // each sample is 0.25 ms, so 8 = 2 ms, 64 = 16 ms.
        public int TfRingSize { get; set; } = 8;

        // Audio loopback ring depth (samples; pow-of-two; 8..128). At 4 kHz
        // each sample is 0.25 ms, so 8 = 2 ms, 128 = 32 ms. Default 16 (4 ms)
        // fits a typical 3 ms WASAPI burst (~12 samples post-decimation) with
        // headroom; 8 is only viable on very-low-latency audio drivers.
        public int AudioRingSize { get; set; } = 16;
    }

    /// <summary>What EnginePulse should do when the resolver flags the
    /// active car as a pure EV. Combustion cars ignore this entirely.</summary>
    public enum ElectricCarMode
    {
        /// <summary>Play the same firing-frequency hum as a combustion car
        /// but at half amplitude. Real EVs aren't silent — many pump
        /// synthetic engine sound — so a muted hum reads more correctly
        /// than dead silence. Default.</summary>
        MutedHum,

        /// <summary>EnginePulse is fully muted on EVs. For users who want
        /// authentic silence (or just don't like the synthetic-engine
        /// approach). Other effects (RoadBumps, TractionLoss, etc.) still
        /// run normally — only the firing-rate hum is suppressed.</summary>
        Silent,
    }

    public sealed class EnginePulseSettings
    {
        public bool   Enabled   { get; set; } = true;
        public float  Gain      { get; set; } = 1.0f;

        // Cylinders=0 is the "use auto-detected count" sentinel. When 0,
        // EnginePulseEffect.EffectiveCylinders returns AutoCylinders if
        // the resolver populated one (or falls back to a sane default of
        // 6 if there's no auto value). When non-zero, the user has dialed
        // in a specific count and that value wins regardless of detection.
        // Default is 0 (auto) so a fresh install or fresh per-car preset
        // automatically uses the resolver's answer for the active car.
        // Existing users' saved settings retain their explicit value and
        // continue to behave as before — only fresh state defaults to auto.
        public int    Cylinders { get; set; } = 0;
        public float  Pitch     { get; set; } = 1.0f;     // multiplier on firing-freq calc
        public double LowpassHz { get; set; } = 0.0;       // 0 = disabled

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;

        /// <summary>How EnginePulse handles cars the resolver flags as
        /// pure EVs. Per-car preset overrides the global default like
        /// every other EnginePulseSettings field, so a user who likes the
        /// muted hum globally can still pick Silent for one specific EV
        /// (or vice versa).</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public ElectricCarMode ElectricMode { get; set; } = ElectricCarMode.MutedHum;

        // ---- Firing-order pattern (Batch 1) ----

        /// <summary>When true, EnginePulse renders the engine's true firing
        /// pattern instead of a uniform pulse train at firing frequency.
        /// Distinguishes V8 cross-plane lope, Ducati L-twin gap, V6 odd-fire
        /// lump, etc. Defaults to true so new installs and fresh per-car
        /// presets get the feel improvement automatically. The toggle is
        /// still wired so existing users can A/B; long-term plan is to
        /// remove the toggle entirely once it has been validated in the
        /// wild.</summary>
        public bool FiringOrderEnabled { get; set; } = true;

        /// <summary>Engine layout. Auto picks from cylinder count using the
        /// modern-default config (V6 60° / V8 cross-plane / V12 60°).
        /// Explicit values get the characterful patterns: V8 flat-plane,
        /// V-twin variants, V6 odd-fire, rotary, custom.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineConfig EngineConfig { get; set; } = EngineConfig.Auto;

        /// <summary>User-supplied firing pattern, used only when
        /// EngineConfig == Custom. Format: comma-separated phase positions
        /// in [0, 1), optionally with ":amplitude" suffix per pulse. See
        /// FiringPatternDb.ParseCustom. Round-trips through the settings UI
        /// textbox so users can copy / paste their tuning back to us.</summary>
        public string CustomFiringPattern { get; set; } = "";
    }

    public sealed class RoadBumpsSettings
    {
        // ---- Heave channel (universal) ----
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;
        public float Freq    { get; set; } = 60.0f;        // unused when Waveform == Noise

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Noise;

        // ---- Surface channel (Forza-only signal source today) ----
        // The surface oscillator is a separate voice with its own freq /
        // waveform / LP / HP — see RoadBumpsEffect for what each does. These
        // values still apply on non-Forza games but the channel just sits
        // silent because the source doesn't supply SurfaceRumble.
        public bool   SurfaceEnabled       { get; set; } = true;
        public float  SurfaceGain          { get; set; } = 1.0f;
        public float  SurfaceFreq          { get; set; } = 120.0f;
        public float  SurfaceRumbleScale   { get; set; } = 1.0f;
        public double SurfaceLowpassHz     { get; set; } = 800.0;
        public double SurfaceHighpassHz    { get; set; } =  60.0;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform SurfaceWaveform    { get; set; } = Waveform.Noise;

        // Rumble-strip leading-edge pulse: opt-in (0 = off by default).
        // SurfaceRumble already spikes on kerbs so the pulse is largely
        // redundant; expose it for users who want extra leading-edge
        // "snap" if their feel of the pure-envelope path comes up soft.
        public float RumbleStripPulseAmp { get; set; } = 0f;
        public int   RumbleStripPulseMs  { get; set; } = 120;
    }

    public sealed class TractionLossSettings
    {
        public bool  Enabled     { get; set; } = true;
        public float Gain        { get; set; } = 1.0f;
        public float Sensitivity { get; set; } = 1.0f;
        public float Freq        { get; set; } = 100.0f;   // unused when Waveform == Noise

        // Default 250 Hz LP is on the smoother side; raise toward 600+ for a
        // harsher tire-grit feel. 30 Hz HP cleans sub-audible drift without
        // taking meaningful energy out of the rumble band.
        public double NoiseLowpassHz  { get; set; } = 250.0;
        public double NoiseHighpassHz { get; set; } = 30.0;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Noise;
    }

    public sealed class GearShiftSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;
        public float Freq    { get; set; } = 40.0f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;
    }

    public sealed class AbsClickSettings
    {
        public bool  Enabled        { get; set; } = true;
        public float Gain           { get; set; } = 1.0f;
        public float Freq           { get; set; } = 80.0f;
        public float PulseFreq      { get; set; } = 12.0f;
        public float DutyCycle      { get; set; } = 0.4f;
        public float TickDurationMs { get; set; } = 35.0f;

        [JsonConverter(typeof(StringEnumConverter))]
        public AbsMode Mode { get; set; } = AbsMode.Pulse;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class PitLimiterSettings
    {
        public bool  Enabled    { get; set; } = true;
        public float Gain       { get; set; } = 1.0f;
        public float Freq       { get; set; } = 50.0f;
        public float PulseFreq  { get; set; } = 6.0f;
        public float DutyCycle  { get; set; } = 0.6f;
        public float ActiveAmp  { get; set; } = 0.30f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class DrsSettings
    {
        public bool  Enabled       { get; set; } = true;
        public float Gain          { get; set; } = 1.0f;
        public float ActivationFreq { get; set; } = 120.0f;
        public int   ActivationMs  { get; set; } = 60;
        public float ActivationAmp { get; set; } = 0.30f;
        public float SustainedFreq { get; set; } = 70.0f;
        public float SustainedAmp  { get; set; } = 0.12f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;
    }

    /// <summary>Standalone preset file. Wraps a GameSettingsSnapshot with a
    /// user-chosen name so it can be imported into any user's library and
    /// applied to any game. Format used by "Export preset" and shared between
    /// users (or downloaded as part of a pack).</summary>
    public sealed class PresetFile
    {
        public const string FileType = "trueforce-preset";
        public string Type    { get; set; } = FileType;
        public int    Version { get; set; } = 1;
        public string PresetName { get; set; }
        public GameSettingsSnapshot Snapshot { get; set; }
    }

    /// <summary>Standalone car-preset file. Wraps a single named CarOverride
    /// for one car. GameName is informational only (so a friend importing a
    /// car preset knows which sim it was tuned for); the override is keyed
    /// on CarId + PresetName. Multiple files can exist per car: a factory
    /// "(default)" preset shipped via BuiltinCarPresets, and any number of
    /// user-saved presets named whatever the user chose.</summary>
    public sealed class CarPresetFile
    {
        public const string FileType = "trueforce-car-preset";
        public string Type    { get; set; } = FileType;
        public int    Version { get; set; } = 2;
        public string GameName { get; set; }
        public string CarId    { get; set; }
        // Added in v2. Old files (v1) loaded with PresetName == null are
        // treated as legacy user presets and get migrated to PresetName=CarId
        // by LoadAndMigrateCarPresets on first run.
        public string PresetName { get; set; }
        // True for files written by InstallOrUpdateBuiltinCarPresets from
        // BuiltinCarPresets shipped with the plugin. The runtime refuses to
        // overwrite these when the user saves changes (forks to a new user
        // preset instead) and refuses to delete them via the UI.
        public bool   IsBuiltin { get; set; }
        public CarOverride Override { get; set; }
    }

    /// <summary>
    /// Per-car override snapshot. Each section field is nullable: null = use the
    /// matching global setting, non-null = use these values for this car. The
    /// user toggles "Override for this car" per section in the UI; toggling on
    /// snapshots the current global section into the override, toggling off
    /// nulls it.
    /// </summary>
    public sealed class CarOverride
    {
        public EnginePulseSettings  EnginePulse  { get; set; }   // null => use global
        public RoadBumpsSettings    RoadBumps    { get; set; }
        public TractionLossSettings TractionLoss { get; set; }
        public GearShiftSettings    GearShift    { get; set; }
        public AbsClickSettings     AbsClick     { get; set; }
        public PitLimiterSettings   PitLimiter   { get; set; }
        public DrsSettings          Drs          { get; set; }
        public AudioCaptureSettings AudioCapture { get; set; }

        public bool IsEmpty =>
            EnginePulse == null && RoadBumps == null && TractionLoss == null &&
            GearShift   == null && AbsClick  == null && AudioCapture == null &&
            PitLimiter  == null && Drs       == null;
    }
}
