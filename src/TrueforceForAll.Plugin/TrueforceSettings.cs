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

        // Slew-rate limit on the captured FFB target — caps how fast the
        // signal can change per ms. Lets users run higher FFB scale safely
        // (a 30000-LSB curb spike gets spread over 30+ ms instead of yanking
        // the wheel). 0 = off.
        public float FfbSpikeMaxLsbPerMs      { get; set; } = 0f;

        // For games with native Trueforce support (AC Rally, iRacing, etc.)
        // — when on, the plugin still streams audio-haptic effects on ep3
        // but leaves bytes 6-9 (cur) at center so the wheel's actual force
        // comes from the game's own FFB path rather than our captured /
        // mirrored value. Avoids fighting with the game's native ep3 cur
        // writes. Default off.
        public bool  SkipFfbPassthrough       { get; set; } = false;

        // Soft peak cap (LSB) on the post-scale FFB target. Caps how large
        // a spike can get rather than how fast — pairs with FfbSpikeMaxLsbPerMs
        // to control both rate and amplitude. 0 = off.
        public float FfbPeakSoftLimitLsb      { get; set; } = 0f;

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
        public float FfbSpikeMaxLsbPerMs       { get; set; } = 0f;
        public float FfbPeakSoftLimitLsb       { get; set; } = 0f;
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

        public Dictionary<string, CarOverride> CarOverrides { get; set; }
    }

    public sealed class AudioCaptureSettings
    {
        public bool   Enabled          { get; set; } = true;
        public float  Gain             { get; set; } = 1.0f;
        public double LowpassCutoffHz  { get; set; } = 350.0;
        public double HighpassCutoffHz { get; set; } =  30.0;
    }

    public sealed class EnginePulseSettings
    {
        public bool   Enabled   { get; set; } = true;
        public float  Gain      { get; set; } = 1.0f;
        public int    Cylinders { get; set; } = 4;
        public float  Pitch     { get; set; } = 1.0f;     // multiplier on firing-freq calc
        public double LowpassHz { get; set; } = 0.0;       // 0 = disabled

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;
    }

    public sealed class RoadBumpsSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 1.0f;
        public float Freq    { get; set; } = 60.0f;        // unused when Waveform == Noise

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Noise;
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

    /// <summary>Standalone car-preset file. Wraps a single CarOverride for one
    /// car. GameName is informational only (so a friend importing a car preset
    /// knows which sim it was tuned for); the override is keyed on CarId.</summary>
    public sealed class CarPresetFile
    {
        public const string FileType = "trueforce-car-preset";
        public string Type    { get; set; } = FileType;
        public int    Version { get; set; } = 1;
        public string GameName { get; set; }
        public string CarId    { get; set; }
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
        public AudioCaptureSettings AudioCapture { get; set; }

        public bool IsEmpty =>
            EnginePulse == null && RoadBumps == null && TractionLoss == null &&
            GearShift   == null && AbsClick  == null && AudioCapture == null;
    }
}
