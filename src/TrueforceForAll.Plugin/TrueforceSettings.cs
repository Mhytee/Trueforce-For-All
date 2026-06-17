// Persisted plugin settings. SimHub serializes this to JSON via
// PluginManager.GetCommonSettings / SaveCommonSettings.
//
// The same shape is also written/read by the Export / Import buttons in the
// settings panel, keep field names stable across versions so shared presets
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
        // wheel is told to return to its native FFB/Trueforce path, useful
        // for games that ship native Trueforce support (iRacing) where our
        // ep3 stream would conflict with the game's own.
        public bool PluginEnabled { get; set; } = true;

        // Auto-link with MAIRA. When on (default), TF4ALL watches for MAIRA's
        // "Pass FFB signal through TF4ALL" shared memory; the moment MAIRA's
        // toggle goes on (it then stops sending PID to the wheel and publishes
        // its force + RPM), TF4ALL renders that force through the Trueforce ep3
        // stream and drives the rim LEDs. No PID on the HID++ pipe => LEDs and
        // FFB stop fighting (the device-level 0x807A vs 0x8123 mutual
        // exclusion only bites when PID is present). When MAIRA isn't passing
        // through, the map is absent and TF4ALL uses the USBPcap FFB tap
        // exactly as before. Set false only to force the legacy USBPcap path
        // and ignore MAIRA entirely.
        public bool MairaFfbPassthrough { get; set; } = true;

        // Drive the wheel rim's RGB rev/shift LEDs from SimHub telemetry over
        // HID++ (separate channel from the Trueforce stream). Scoped to iRacing:
        // iRacing's native rev lights ride its Trueforce SDK hook, so MAIRA
        // users who disable in-game Trueforce lose them; this puts them back.
        // Default off (new hardware-output feature, opt-in).
        // On by default: it is gated to iRacing AND to MAIRA passthrough
        // being live (no PID on the HID++ pipe), so default-on only ever
        // drives LEDs in the safe iRacing+MAIRA configuration. Other games
        // and the no-MAIRA iRacing path never see it.
        public bool RpmLedsEnabled { get; set; } = true;

        // Gate for the rim-LED / MAIRA-passthrough settings section. Hidden
        // from the public UI until a tester types the access code (MAIRA or
        // TEST) in the box at the bottom of the settings page. The MAIRA
        // side is still in PR and unvalidated on RS50/G923, so this keeps
        // the half-feature out of sight for normal users. Once true it
        // stays unlocked for that install.
        public bool RpmLedUnlocked { get; set; } = false;

        // Per-game auto-remembered enable state. When the active game changes,
        // the plugin looks up this dict and applies the saved value (default
        // true for games never seen before). Independent of preset assignment.
        public Dictionary<string, bool> GameEnabled { get; set; } = new Dictionary<string, bool>();

        // Per-game audio-capture exe override. Keyed by SimHub GameName
        // (including Custom_xxx codes for user-added games), value is the
        // exe basename (no ".exe" suffix). Takes priority over the curated
        // ExeLabels dict and the fuzzy GameName matcher in CaptureTick. Use
        // when a game doesn't get found automatically, type its exe name
        // here and we'll capture from it.
        public Dictionary<string, string> AudioCaptureExeOverrides { get; set; } = new Dictionary<string, string>();

        // Per-(game, carId) cylinder lookup cache. Populated by CarCylinderResolver
        // when its heuristic detects a car not present in the shipped bake, so
        // the next session resolves instantly without re-reading ui_car.json.
        // Schema: outer key = SimHub GameName (e.g. "AssettoCorsa"), inner key
        // = carId, value = effective cylinder count (1..16; 0 reserved for EV
        // sentinel). Plugin owns invalidation via CarCylinderCacheVersion below
        //, bump that integer when the heuristic improves and all caches are
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
        public float FfbScale                 { get; set; } = 0.80f;
        // Sign of the FFB target sent to the motor. Defaults true (inverted),
        // which is correct on every wheel we've tested in the common games,
        // but NOT universal: RaceRoom (and possibly other games/wheels) report
        // the opposite convention, so the user-facing "Invert FFB sign" toggle
        // stays. Uncheck it when forces feel reversed.
        public bool  FfbInvertSign            { get; set; } = true;
        public float FfbSmoothTimeConstantMs  { get; set; } = 0.0f;

        // Stationary-spring "parking force". The plugin passes the game's own
        // FFB straight to the motor; a parked car generates ~no self-aligning
        // torque so the wheel feels limp. This adds a centering force that
        // (1) fades to zero by CutoffKmh and (2) only fills the deficit up to
        // a desired magnitude, so it never opposes the game's FFB and
        // disengages the instant the game provides its own centering. Needs
        // steering angle, populated only by sources that read it natively
        // (Assetto Corsa and the Forza Horizon games); a no-op everywhere
        // else. Global, not per-game: it's a wheel-comfort preference,
        // harmless where it can't engage. Default ON (2026-05-24): since it
        // only engages where steering is reported (AC + Forza) and is hard-
        // gated off for iRacing, default-on effectively means "on for AC and
        // Forza" and a no-op for everything else. Invert flips the centering
        // direction, the steer->FFB sign is wheel/protocol dependent;
        // confirmed on hardware that the centering force must be inverted, so
        // that's now unconditional (no toggle).
        public bool   StationarySpringEnabled   { get; set; } = true;
        // 1.0 = full felt scale at full lock when parked; slider allows up to
        // 2.0 for headroom, though past ~1/FfbScale the ±full-scale clamp /
        // motor ceiling caps it (you can't exceed the wheel's max torque).
        // Default 0.5 (owner's preferred general feel once the spring is driven
        // by the wheel's physical steering position).
        public double StationarySpringStrength  { get; set; } = 0.5;
        public double StationarySpringCutoffKmh { get; set; } = 12.0;  // spring fully gone at/above this speed

        // Issue #13: while the game is paused, fully leave Trueforce mode so the
        // wheel reverts to its native force feedback (e.g. Forza's own
        // auto-center) instead of the plugin holding the last captured force.
        // An active Trueforce stream overrides the wheel's native FFB, so a
        // parked car's held force walked the wheel to full lock (G923 / FH6).
        // On by default (standard behaviour); exposed only as a hand-edit escape
        // hatch, no UI, in case a wheel ever misbehaves on the pause/resume
        // transition.
        public bool   StopStreamOnPause          { get; set; } = true;

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
        public bool  FfbSpikeTamingEnabled    { get; set; } = true;
        // Algorithm switch (experimental A/B). True = pure slew-rate limiter
        // (iRacing-style, no amplitude reduction). False = transient detector
        // with magnitude threshold + soft cap. Each interprets
        // FfbSpikeMaxLsbPerMs differently: as a rate cap (LSB/ms) when true,
        // or as a magnitude threshold (LSB) when false. FfbPeakSoftLimitLsb
        // is only used by the transient detector.
        public bool  FfbSpikeUseSlewLimiter   { get; set; } = true;
        public float FfbSpikeMaxLsbPerMs      { get; set; } = 2508.36f;

        // Skip the captured-FFB → ep3 cur mirror. With this on, our active
        // packets carry cur = 0x8000 (silence center). The wheel uses cur
        // as motor torque and ignores ep0 whenever active packets are
        // streaming, so this means zero motor force from our path
        // appropriate ONLY for games that drive the wheel's motor through
        // their own native ep3 path (Forza Horizon, AC Rally, iRacing). For
        // games that rely on ep0 for FFB (vanilla AC, F1, PC2), enabling
        // this kills FFB entirely. Default off.
        public bool  SkipFfbPassthrough       { get; set; } = false;

        // Optional absolute path to USBPcapCMD.exe, set when the user picks a
        // custom USBPcap location via the "Browse..." action in the diagnostics
        // panel. Empty = use the standard auto-probe (env var, Program Files,
        // Program Files (x86)). Only checked when set; falls through to
        // auto-probe if the path no longer exists on disk.
        public string UsbPcapCmdPathOverride   { get; set; } = "";

        // Optional manual override for the FFB tap's USBPcap interface +
        // device address, set by the "Pick device manually" affordance when
        // auto-discovery via descriptor injection fails (typically because
        // USBPcap's descriptor cache is stale for a hot-plugged wheel). Empty
        // interface OR zero address = auto-discover. Both must be valid for
        // the override to take effect.
        public string ManualUsbPcapInterface     { get; set; } = "";
        public int    ManualUsbPcapDeviceAddress { get; set; } = 0;
        // Identity (VID/PID) of the device the user pinned. Lets the FFB tap's
        // self-heal re-locate the SAME device after a USB re-enumeration (new
        // address) without ever switching to a different device. 0 = unknown
        // (a pin saved before this existed); in that case the tap leaves the
        // pin untouched rather than guessing.
        public int    ManualUsbPcapVid           { get; set; } = 0;
        public int    ManualUsbPcapPid           { get; set; } = 0;

        // Reveals the "Pick device manually..." control in Diagnostics. Off by
        // default because auto-discovery + identity-based self-heal handle the
        // realistic failure modes for almost every user, and the override is
        // a foot-gun when set and forgotten (the pinned address goes stale
        // after a replug or a different USB port and the plugin loses sight
        // of the wheel). Power users who genuinely need it (multi-wheel
        // disambiguation, or a USBPcap interface mismatch they want to
        // override) can flip this on with the MANUALPIN access code.
        public bool   ShowManualOverrideUi       { get; set; } = false;

        // One-shot migration latch: on v0.1.21 -> v0.1.22 upgrade, clear any
        // saved manual override (stale pins from earlier sessions caused
        // false-positive "FFB stopped working" reports; see issue #17). Set
        // true after the migration runs once so it doesn't re-clear a pin
        // the user has since deliberately re-set via MANUALPIN.
        public bool   ManualOverrideClearedV0_1_22 { get; set; } = false;

        // Opt-in raw USB packet logging. When true, the FFB tap writes every
        // Set_Report observed on the wheel's USB address to a usb-trace.bin
        // file alongside SimHub's logs, for support to analyze offline. Off
        // by default because the file can grow quickly (~2-3 KB/sec of active
        // FFB) and because users should make an explicit choice about
        // including USB bus traffic in exported logs.
        public bool   LogUsbBytesEnabled         { get; set; } = false;

        // Opt-in experimental FFB-capture path (toggled by the FFBX access
        // code). Enables in-progress capture work that isn't yet proven on
        // hardware across the wheel range: HID++ very-long report 0x12
        // extraction + 0x11/0x12 resolver summing + a lower index-resolve
        // floor (issue #8, RS50 on FH6), and any future self-learning capture
        // heuristics. Off = shipped behaviour, so existing users are untouched
        // until a tester confirms a given wheel/game. Global, not per-preset:
        // it's a capture-system behaviour, not a tuning.
        public bool   ExperimentalFfbCapture     { get; set; } = false;

        // Latches once the user acts on (or dismisses) the one-time banner that
        // appears when experimental FFB detection was load-bearing in getting
        // their wheel working, asking them to file a compatibility report. Keeps
        // the prompt from re-nagging every session.
        public bool   ExperimentalSuccessReportDismissed { get; set; } = false;

        public float FfbPeakSoftLimitLsb      { get; set; } = 2061.90f;

        // Sidechain ducking applied to continuous effects (engine pulse, audio
        // capture) when transient effects (gear shift, ABS, road bumps,
        // traction loss) fire. Depth = max attenuation (0 = no duck, 1 = full
        // silence). Attack/Release in ms are the time constants for the
        // envelope's down/up directions.
        public float DuckDepth     { get; set; } = 0.60f;
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
        public CollisionSettings    Collision    { get; set; } = new CollisionSettings();
        public RevLimiterSettings   RevLimiter   { get; set; } = new RevLimiterSettings();

        // Airborne ducking coordinator. Global, not per-car/per-preset: it's a
        // wheel-comfort behaviour (suppress phantom output while the car is in
        // the air), same machine-level rationale as Sidechain ducking's living
        // outside the per-car override set. See AirborneEffect / AirborneSettings.
        public AirborneSettings     Airborne     { get; set; } = new AirborneSettings();

        // Per-machine performance tuning. Lives outside GameSettingsSnapshot
        // because ring sizes are a property of the machine (CPU, scheduler
        // load), not of the game/preset, sharing a preset shouldn't override
        // a friend's tuned ring sizes.
        public PerformanceSettings Performance { get; set; } = new PerformanceSettings();

        // Forza UDP listener config. Same machine-not-game rationale as
        // Performance: the port the user picks is local to their setup. Lives
        // here so it survives preset switches.
        public ForzaSettings Forza { get; set; } = new ForzaSettings();

        // Author name auto-stamped onto exported presets / car presets / packs.
        // Set once via the Backup & sync section; the export-info dialog
        // pre-fills it and writes back any edits the user makes there. Blank
        // by default; users who never set it just produce anonymous exports.
        public string SharingAuthor { get; set; } = "";

        // ---- Per-effect "NEW" badges + changelog banner (see EffectChangelog) ----

        // Effect IDs the user has acknowledged. An ID present here means
        // the per-effect "NEW" badge is suppressed for that section. Plugin
        // pre-seeds this with every known effect on fresh install (and on
        // first run for users upgrading from a pre-feature settings file),
        // so badges only ever surface for effects added in versions newer
        // than the running build at the time of stamp. Schema: list of
        // stable string IDs that match EffectChangelog.KnownEffectIds.
        public List<string> SeenEffects { get; set; } = new List<string>();

        // Last assembly version whose changelog banner the user has seen
        // (or, on fresh install, the version at the time of install).
        // ToString(3) format ("X.Y.Z"). Null/empty until first Init stamps
        // it. Drives the "what's new" banner: anything in EffectChangelog
        // with Version > this gets rolled up into the banner; dismissing
        // updates this to the running build.
        public string LastSeenVersion { get; set; }

        // ---- One-and-done "spread the word" prompt (see ShouldShowShareCta) ----

        // Cumulative seconds the plugin was actively driving the wheel with a
        // game running. Drives the word-of-mouth banner: it only surfaces
        // after the user has had real, working seat time, so it never fires
        // mid-troubleshooting. Flushed periodically and on shutdown (not every
        // frame), so treat it as an approximate odometer, not an exact clock.
        public double ActiveStreamingSeconds { get; set; } = 0.0;

        // Set true the first time the user dismisses or acts on the
        // word-of-mouth banner. Permanent: the prompt is one-and-done so it
        // never nags a user who already saw it.
        public bool ShareCtaDismissed { get; set; } = false;

        // Persisted sort preferences for the Manage Presets modal, one per
        // tab. Key matches a column's binding path (e.g. "Name",
        // "BuiltinLabel"); empty/null = natural order. Hydrated when the
        // dialog opens, rewritten on every header click.
        public ManageSort ManageGamesSort   { get; set; } = new ManageSort();
        public ManageSort ManageCarsSort    { get; set; } = new ManageSort();
        public ManageSort ManageCustomsSort { get; set; } = new ManageSort();

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

        // Games we've observed reporting a usable redline (sane CarSettings_-
        // RedLineRPM). Learned once per game and persisted so the rev-limiter
        // UI is stable: the engage-% row hides for redline games even before
        // telemetry flows next session. Positive-only (a game is added when a
        // sane redline is first seen; never removed). Forza never qualifies
        // (its redline reads out of range), so it keeps the engage-% control.
        public HashSet<string> GamesWithRedline { get; set; } = new HashSet<string>();

        // Set true once the GamesWithRedline set has been cleared for re-learning
        // under the realRedline rule (builds before that split faked a redline
        // from MaxRpm and over-learned every game). One-time migration in Init.
        public bool GamesWithRedlineRevalidated { get; set; } = false;

        // Set true once presets still on the old rev-limiter engage default
        // (0.97) have been bumped to the new default (0.85). On the Forza
        // percentage path 0.97 only fired when bouncing off the limiter, so the
        // buzz was effectively dead for most drivers (issue #8). One-time
        // migration in Init; only touches presets still at the exact old default.
        public bool RevLimiterThresholdDefaultMigrated { get; set; } = false;

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

        // User-saved custom engines. Global (one library across all presets);
        // EnginePulseSettings.CustomEngineId references entries by their Id.
        // The settings UI's "Custom..." action adds entries here, "Manage
        // customs..." edits/deletes them. Built-in engine layouts (V8 cross-
        // plane, Rotary 2-rotor, etc.) are immutable and live in
        // FiringPatternDb instead.
        public List<CustomEngineDef> CustomEngines { get; set; } = new List<CustomEngineDef>();
    }

    /// <summary>User-authored engine definition. Stored in
    /// <summary>Persisted sort state for one of the Manage Presets modal tabs.
    /// Empty Key = natural (insertion) order; populated Key matches the
    /// binding path of the column to sort on.</summary>
    public sealed class ManageSort
    {
        public string Key { get; set; }
        public bool   Descending { get; set; }
    }

    /// <see cref="TrueforceSettings.CustomEngines"/> and referenced by per-
    /// preset <see cref="EnginePulseSettings.CustomEngineId"/>. Holds either a
    /// firing-pattern string (combustion) or an electric flag + mode (EV).</summary>
    public sealed class CustomEngineDef
    {
        /// <summary>Stable identifier (Guid as string). Set on creation, never
        /// changes, preset references survive renames.</summary>
        public string Id { get; set; }

        /// <summary>User-supplied display name. Surfaces in the dropdown and
        /// the manage dialog. May be blank during in-progress edits, must be
        /// non-blank before save (UI enforces).</summary>
        public string Name { get; set; } = "";

        /// <summary>True = electric engine (no firing pattern, behavior from
        /// <see cref="ElectricMode"/>). False = combustion (pattern in
        /// <see cref="Pattern"/>).</summary>
        public bool IsElectric { get; set; }

        /// <summary>Behavior when <see cref="IsElectric"/> = true. Ignored
        /// for combustion entries.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public ElectricCarMode ElectricMode { get; set; } = ElectricCarMode.MutedHum;

        /// <summary>Firing-pattern string (positions[:amplitudes], comma-
        /// separated). Used only when <see cref="IsElectric"/> = false. See
        /// FiringPatternDb.ParseCustom. Empty string is treated as silence.</summary>
        public string Pattern { get; set; } = "";
    }

    /// <summary>Whole-settings snapshot saved per-game. Mirrors the top-level
    /// fields of <see cref="TrueforceSettings"/> minus the GamePresets dict
    /// (to avoid serialization recursion). When loaded, replaces all matching
    /// fields on the active settings.</summary>
    public sealed class GameSettingsSnapshot
    {
        // Defaults mirror TrueforceSettings' top-level class defaults so a
        // GameSettingsSnapshot deserialized from JSON missing these fields
        // gets the same starting state as a fresh-install Settings object.
        public float MasterGain                { get; set; } = 1.0f;
        public float FfbScale                  { get; set; } = 0.80f;
        public bool  FfbInvertSign             { get; set; } = true;
        public float FfbSmoothTimeConstantMs   { get; set; } = 0.0f;
        public bool  FfbSpikeTamingEnabled     { get; set; } = true;
        public bool  FfbSpikeUseSlewLimiter    { get; set; } = true;
        public float FfbSpikeMaxLsbPerMs       { get; set; } = 2508.36f;
        public float FfbPeakSoftLimitLsb       { get; set; } = 2061.90f;
        public bool  SkipFfbPassthrough        { get; set; } = false;
        public float DuckDepth                 { get; set; } = 0.60f;
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
        public CollisionSettings    Collision    { get; set; }
        public RevLimiterSettings   RevLimiter   { get; set; }
        // Airborne ducking travels with the preset (built-in presets seed it);
        // null in presets saved before it existed, handled on apply.
        public AirborneSettings     Airborne     { get; set; }

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
        public double LowpassCutoffHz  { get; set; } = 567.0;
        public double HighpassCutoffHz { get; set; } =  35.0;
    }

    /// <summary>Mode for the Performance tab. In Auto, the plugin starts at
    /// the smallest ring sizes and ratchets them up (one-way) when underruns
    /// or audio-ring lapping cross a 1-second threshold; the survived value
    /// is persisted across sessions. In Manual, ring sizes are user-fixed
    /// no automatic changes, for users who want guaranteed-stable behavior
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
    /// or Sim Racing Studio's 4123. The listener is opened only while a Forza
    /// title is the active game (FH4/5/6, FM); SimHub's GameName detection now
    /// covers the shipped Forza titles, so the old always-on escape hatch was
    /// retired.</summary>
    public sealed class ForzaSettings
    {
        public bool   Enabled       { get; set; } = true;
        public int    Port          { get; set; } = 5300;
        public string BindAddress   { get; set; } = "0.0.0.0";

        /// <summary>Re-broadcast every received Forza packet to a second
        /// destination. Solves the "I want SimHub dashboards AND Trueforce
        /// haptics from the same Forza title" coexistence problem: Forza
        /// only sends to one IP+port, so the user points Forza at us and we
        /// relay verbatim (no parsing, no transformation) to SimHub. Default
        /// off so the user explicitly opts in, when off, packets stop here.</summary>
        public bool   ForwardEnabled { get; set; } = false;

        /// <summary>Where to forward each received packet. 127.0.0.1 covers
        /// the common case of SimHub running on the same machine; users with
        /// a separate SimHub host can point here. Ignored when
        /// <see cref="ForwardEnabled"/> is false.</summary>
        public string ForwardHost    { get; set; } = "127.0.0.1";

        /// <summary>UDP port of the secondary listener (typically SimHub's
        /// configured Forza Data Out port, find it in SimHub's
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
        // each sample is 0.25 ms, so 8 = 2 ms, 128 = 32 ms. Defaults to the
        // minimum (8 = 2 ms) so low-latency hardware gets the best feel out
        // of the box. The two-way auto-ratchet bumps it up on the first
        // noisy moment and shrinks it back down once the system settles, so
        // it self-tunes to whatever the user's hardware actually needs.
        public int AudioRingSize { get; set; } = 8;
    }

    /// <summary>What EnginePulse should do when the resolver flags the
    /// active car as a pure EV. Combustion cars ignore this entirely.</summary>
    public enum ElectricCarMode
    {
        /// <summary>Play the same firing-frequency hum as a combustion car
        /// but at half amplitude. Real EVs aren't silent, many pump
        /// synthetic engine sound, so a muted hum reads more correctly
        /// than dead silence. Default.</summary>
        MutedHum,

        /// <summary>EnginePulse is fully muted on EVs. For users who want
        /// authentic silence (or just don't like the synthetic-engine
        /// approach). Other effects (RoadBumps, TractionLoss, etc.) still
        /// run normally, only the firing-rate hum is suppressed.</summary>
        Silent,
    }

    public sealed class EnginePulseSettings
    {
        public bool   Enabled   { get; set; } = true;
        // 0.07 reflects what's actually usable: the firing-pattern pulses
        // already deliver substantial energy at the wheel; 1.0 was over the
        // top for typical wheelbases.
        public float  Gain      { get; set; } = 0.07f;

        public float  Pitch     { get; set; } = 1.0f;     // multiplier on firing-freq calc
        public double LowpassHz { get; set; } = 510.0;    // matches the AC-tuned baseline

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;

        /// <summary>How EnginePulse handles cars the resolver flags as
        /// pure EVs (or when the user explicitly picks
        /// <see cref="EngineLayout.Electric"/>). Per-car preset overrides
        /// the global default like every other EnginePulseSettings field.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public ElectricCarMode ElectricMode { get; set; } = ElectricCarMode.MutedHum;

        /// <summary>Engine layout. Auto defers to the resolver / telemetry;
        /// any explicit value (V8 cross-plane, Rotary 2-rotor, Electric, etc.)
        /// wins. Custom uses the user-authored engine identified by
        /// <see cref="CustomEngineId"/> (or the legacy
        /// <see cref="CustomFiringPattern"/> string as a fallback during
        /// migration). Default Auto so fresh presets defer to detection.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineLayout Layout { get; set; } = EngineLayout.Auto;

        /// <summary>When <see cref="Layout"/> == Custom, the Id of the
        /// <see cref="CustomEngineDef"/> in
        /// <see cref="TrueforceSettings.CustomEngines"/> that defines the
        /// pattern / electric behavior. Empty when Layout != Custom or
        /// during legacy migration before the user has picked a saved
        /// custom.</summary>
        public string CustomEngineId { get; set; } = "";

        /// <summary>User-supplied firing pattern, used only when
        /// <see cref="Layout"/> == Custom. Format: comma-separated phase
        /// positions in [0, 1), optionally with ":amplitude" suffix per
        /// pulse. See FiringPatternDb.ParseCustom. Round-trips through the
        /// settings UI textbox so users can copy / paste their tuning back
        /// to us.</summary>
        public string CustomFiringPattern { get; set; } = "";

        /// <summary>Optional human-friendly name for a custom firing pattern.
        /// Built-in layouts ship with descriptive names; this lets users tag
        /// their own custom patterns the same way ("LS3 swap, dyno-tuned" /
        /// "Ferrari 360 flat-plane bias"). Surfaces in the engine-data
        /// submission body. Used only when Layout == Custom; ignored
        /// otherwise.</summary>
        public string CustomFiringPatternName { get; set; } = "";

        // ---- High-RPM perceptibility helpers ----
        //
        // Wheel motors mechanically lowpass at high firing frequencies, so
        // the pulse feels weak as RPM climbs. Two compensations, both on
        // by default:
        //
        //   LoadLayer: adds a sine at the engine cycle frequency (RPM/120 Hz)
        //     alongside the firing-rate wavetable. Phase-locked subharmonic
        //     of the firing rate; sweeps 7-58 Hz across idle-to-redline,
        //     right in the band the wheel responds to.
        //
        //   HighRpmBoost: ramps an extra gain factor on the firing pulse
        //     from 0 at 50% RPM to (Amount) at redline, partially
        //     compensating for the wheel's mechanical rolloff.
        public bool   LoadLayerEnabled    { get; set; } = true;
        public float  LoadLayerGain       { get; set; } = 0.80f;
        public bool   HighRpmBoostEnabled { get; set; } = true;
        public float  HighRpmBoostAmount  { get; set; } = 0.70f;

        // ---- Legacy migration fields (pre-2026-05-11) ----
        //
        // Pre-flat-enum settings stored Cylinders (int) + EngineConfig (enum)
        // + FiringOrderEnabled (bool) as the engine-shape definition. New
        // code reads/writes Layout only. These fields are kept on the type
        // so Newtonsoft can still deserialize old JSON (and serialize them
        // back at minimal cost), one-time migration in ApplyEngineSettings
        // folds them into Layout on first load.

        /// <summary>LEGACY (pre-flat-enum). Old per-cylinder count. Read on
        /// load and folded into <see cref="Layout"/> via
        /// FiringPatternDb.LayoutFromLegacy. Never read after migration.</summary>
        public int Cylinders { get; set; } = 0;

        /// <summary>LEGACY (pre-flat-enum). Old engine-layout enum paired
        /// with <see cref="Cylinders"/>. Folded into <see cref="Layout"/>
        /// on first load.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineConfig EngineConfig { get; set; } = EngineConfig.Auto;

        /// <summary>LEGACY (pre-flat-enum). Toggle between firing-pattern
        /// synthesis (true, the new default) and the uniform-pulse path
        /// (false). The legacy path was removed when Layout was
        /// introduced, this field exists only so old JSON still
        /// deserializes. Ignored at runtime.</summary>
        public bool FiringOrderEnabled { get; set; } = true;
    }

    public sealed class RoadBumpsSettings
    {
        // ---- Heave channel (universal) ----
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 0.45f;
        public float Freq    { get; set; } = 61.0f;        // unused when Waveform == Noise

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Triangle;

        // ---- Surface channel (Forza-only signal source today) ----
        // The surface oscillator is a separate voice with its own freq /
        // waveform / LP / HP, see RoadBumpsEffect for what each does. These
        // values still apply on non-Forza games but the channel just sits
        // silent because the source doesn't supply SurfaceRumble.
        public bool   SurfaceEnabled       { get; set; } = true;
        public float  SurfaceGain          { get; set; } = 0.70f;
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
        public float Gain        { get; set; } = 0.04f;
        public float Sensitivity { get; set; } = 0.18f;
        public float Freq        { get; set; } = 134.0f;   // unused when Waveform == Noise

        // Default 250 Hz LP is on the smoother side; raise toward 600+ for a
        // harsher tire-grit feel. 41 Hz HP cleans sub-audible drift without
        // taking meaningful energy out of the rumble band.
        public double NoiseLowpassHz  { get; set; } = 250.0;
        public double NoiseHighpassHz { get; set; } = 41.0;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Noise;
    }

    public sealed class GearShiftSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 0.40f;
        public float Freq    { get; set; } = 35.0f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class AbsClickSettings
    {
        public bool  Enabled        { get; set; } = true;
        public float Gain           { get; set; } = 0.14f;
        public float Freq           { get; set; } = 150.0f;
        public float PulseFreq      { get; set; } = 9.82f;
        public float DutyCycle      { get; set; } = 0.33f;
        public float TickDurationMs { get; set; } = 35.0f;

        [JsonConverter(typeof(StringEnumConverter))]
        public AbsMode Mode { get; set; } = AbsMode.Pulse;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class PitLimiterSettings
    {
        public bool  Enabled    { get; set; } = true;
        public float Gain       { get; set; } = 0.08f;
        public float Freq       { get; set; } = 50.0f;
        public float PulseFreq  { get; set; } = 4.34f;
        public float DutyCycle  { get; set; } = 0.48f;
        public float ActiveAmp  { get; set; } = 0.30f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class DrsSettings
    {
        public bool  Enabled       { get; set; } = true;
        public float Gain          { get; set; } = 0.28f;
        public float ActivationFreq { get; set; } = 60.0f;
        public int   ActivationMs  { get; set; } = 80;
        public float ActivationAmp { get; set; } = 0.50f;
        public float SustainedFreq { get; set; } = 120.0f;
        public float SustainedAmp  { get; set; } = 0.05f;

        // Activation chirp ("blip" on rising edge). Pre-split this field
        // drove both parts; kept under the original name so old presets
        // deserialize without a migration step.
        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;

        // Sustained tone ("trail" while DRS stays open). Added in 0.1.3 so
        // each layer can pick a shape that suits it (e.g. a sharp Square
        // blip with a softer Sine trail). Old presets that predate this
        // field deserialize to the default Square; users who want the
        // pre-0.1.3 monolithic-waveform behavior can set both fields the
        // same in the UI.
        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform SustainedWaveform { get; set; } = Waveform.Square;
    }

    public sealed class CollisionSettings
    {
        public bool  Enabled            { get; set; } = true;
        public float Gain               { get; set; } = 0.21f;
        public float Freq               { get; set; } = 50.0f;
        public int   EnvelopeMs         { get; set; } = 120;
        public float MinThreshold       { get; set; } = 0.14f;
        public float MinAmp             { get; set; } = 0.20f;
        public float MaxAmp             { get; set; } = 0.85f;
        public float NormalizationScale { get; set; } = 2.0f;
        public int   RefractoryMs       { get; set; } = 250;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    public sealed class RevLimiterSettings
    {
        // On by default (project owner's call, 2026-05-24): the rev-limiter
        // buzz is a useful shift cue most drivers want, so it ships enabled
        // rather than following the usual new-effect-disabled default.
        public bool  Enabled    { get; set; } = true;
        public float Gain       { get; set; } = 0.10f;
        public float Freq       { get; set; } = 90.0f;
        public float PulseFreq  { get; set; } = 20.0f;
        public float DutyCycle  { get; set; } = 0.5f;
        public float ActiveAmp  { get; set; } = 0.35f;
        // Fraction of MaxRpm on the percentage path (Forza and other no-redline
        // sources). MaxRpm there is the absolute limiter, which sits above where
        // you actually upshift, so 0.97 only fired when bouncing off the limiter
        // and most drivers never felt it (issue #8). 0.85 lands it as a usable
        // shift cue. Owner's call, 2026-06-01.
        public float Threshold  { get; set; } = 0.85f;   // fraction of MaxRpm

        // RPM offset applied on the real-redline path only (ignored on the
        // percentage path). Negative = fire before the redline, positive =
        // after, 0 = right at it. Lets redline-reporting games (AC, iRacing)
        // tune an early/late shift cue without a percentage.
        public float RedlineOffsetRpm { get; set; } = 0.0f;

        // Engage-point override. Auto = trust the game (fire at redline if it
        // reports a sane one, else at Threshold% of MaxRpm). Percentage / Redline
        // are the manual escape hatch when auto-detection misreads and the buzz
        // stops firing. Defaults to Auto so existing presets are unchanged.
        [JsonConverter(typeof(StringEnumConverter))]
        public RevLimiterEngageMode EngageMode { get; set; } = RevLimiterEngageMode.Auto;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Square;
    }

    /// <summary>Settings for the airborne-ducking coordinator (see
    /// AirborneEffect). Enabled by default: it's a calming behaviour (reduces
    /// phantom slip / engine / road output while the car is off the ground),
    /// not a new haptic voice, so shipping it on keeps the wheel feeling
    /// correct over jumps without the user having to discover it. Reduction is
    /// how hard to pull the chosen voices down (1 = silence); the per-voice
    /// flags pick which voices participate.</summary>
    public sealed class AirborneSettings
    {
        public bool  Enabled          { get; set; } = true;
        public float Reduction        { get; set; } = 1.0f;
        // Engine pulse defaults to NOT ducked: the engine keeps revving in the
        // air, so leaving its pulse playing reads as "still driving, just
        // weightless" rather than the engine cutting out. Reduction is about
        // killing the road / grip vibrations, not the engine.
        public bool  DuckEngine        { get; set; } = false;
        public bool  DuckAudio         { get; set; } = true;
        public bool  DuckRoadBumps     { get; set; } = true;
        public bool  DuckTractionLoss  { get; set; } = true;
        public bool  DuckRevLimiter    { get; set; } = true;
        public bool  DuckGearShift     { get; set; } = false;
        public bool  DuckAbs           { get; set; } = false;
        public bool  DuckPitLimiter    { get; set; } = false;
        public bool  DuckDrs           { get; set; } = false;
        public bool  DuckCollision     { get; set; } = false;
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
        // Optional sharing metadata. All three fields are user-supplied and
        // free-form; null/empty means "not provided" and the importer
        // gracefully omits them from the success dialog.
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
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
        // Optional sharing metadata. Set on export when the user chose to
        // include it; built-in / locally-saved files leave these blank.
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public CarOverride Override { get; set; }
    }

    /// <summary>Manifest written into a multi-preset pack zip. Lists the
    /// game presets and car presets bundled inside, so an importer can show
    /// counts before importing and skip files that don't match the manifest.
    /// Pack zip layout:
    ///   manifest.json
    ///   presets/&lt;PresetName&gt;.tfpreset
    ///   cars/&lt;CarId&gt;~&lt;PresetName&gt;.tfcar.json
    /// </summary>
    public sealed class PresetPackManifest
    {
        public const string FileType = "trueforce-pack";
        public string Type    { get; set; } = FileType;
        public int    Version { get; set; } = 1;
        public string ExportedAt { get; set; }
        // Pack-level sharing metadata. Each contained preset / car preset
        // also carries its own Author/Description/AuthorVersion when set;
        // the pack-level fields cover the bundle as a whole.
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public List<string> Presets { get; set; } = new List<string>();
        public List<PackedCarPreset> Cars { get; set; } = new List<PackedCarPreset>();
    }

    public sealed class PackedCarPreset
    {
        public string CarId      { get; set; }
        public string PresetName { get; set; }
        public string GameName   { get; set; }
        public string FileName   { get; set; }
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
        public CollisionSettings    Collision    { get; set; }
        public RevLimiterSettings   RevLimiter   { get; set; }
        public AudioCaptureSettings AudioCapture { get; set; }

        public bool IsEmpty =>
            EnginePulse == null && RoadBumps == null && TractionLoss == null &&
            GearShift   == null && AbsClick  == null && AudioCapture == null &&
            PitLimiter  == null && Drs       == null && Collision    == null &&
            RevLimiter  == null;
    }
}
