// Persisted plugin settings. SimHub serializes this to JSON via
// PluginManager.GetCommonSettings / SaveCommonSettings.
//
// The same shape is also written/read by the Export / Import buttons in the
// settings panel, keep field names stable across versions so shared presets
// stay valid.

using System;
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

        // One-time latch for the iRacing "disable native Trueforce in app.ini"
        // notice. Set only when the user dismisses it for good ("Got it, don't
        // show again"). Machine-local in backup: the app.ini change is per-PC,
        // so a restored backup on a new machine should show the notice again.
        public bool IRacingTrueforceNoticeDismissed { get; set; } = false;

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

        // Telemetry based FFB: per-car grip-limit auto-calibration state
        // (GripPeakLearner), keyed "GameName|CarId". Written as the player
        // drives (where each car's combined-slip metric actually tops out
        // plus how much near-limit seat time backs that estimate) so the
        // next session starts calibrated. Zero user action; the grip
        // auto-cal checkbox gates application, not learning persistence.
        public Dictionary<string, CarGripCal> CarGripCalibration { get; set; }
            = new Dictionary<string, CarGripCal>();

        // Car facts layer: community-vetted (or scanner-detected) truth per
        // (game, carId). Replaces the cylinder-only CarCylinderCache as
        // first-class storage for engine layout, redline, and car-name
        // facts. Keyed by "{game}/{carId}". Empty when no facts are known
        // (apply path falls through to the scanner / heuristic). See the
        // CarFactsBundle / EngineVariant classes above and the
        // project_car_facts_layer memory entry for the architecture.
        public Dictionary<string, CarFactsBundle> CarFacts { get; set; }
            = new Dictionary<string, CarFactsBundle>();

        // User's default-variant choice per car: "{game}/{carId}" ->
        // EngineVariant.Id. Used at apply time when the bundle has more
        // than one variant AND telemetry can't unambiguously pick one
        // (most non-Forza cases). Single-variant cars and unambiguous
        // telemetry matches bypass this dict entirely.
        public Dictionary<string, string> CarFactsSelection { get; set; }
            = new Dictionary<string, string>();

        // One-time migration latch. Flips true after the CarCylinderCache
        // entries have been promoted to seed Stock EngineVariants in the
        // CarFacts dict above. Migrate-once + idempotent.
        public bool CarFactsMigratedV1 { get; set; } = false;

        // One-time migration latch. Flips true after car presets whose name
        // matches the ordinal "Car_NNN" pattern have been renamed to their
        // baked human-readable car name (per game, via
        // BuiltinCarCylinders.TryGetDisplayName). Saves "Car_2267" presets
        // as "1997 Mazda RX-7" so the Preset Manager rows read sensibly out
        // of the box for Forza Horizon. Idempotent.
        public bool CarPresetOrdinalNamesMigratedV1 { get; set; } = false;

        // V2 re-runs the same rename pass with two fixes vs V1: (a) V1 ran
        // before _carStore was initialised in Init() so the LoadAll guard
        // tripped and nothing got migrated, (b) V1 only consulted the baked
        // name tables and missed user-set Settings.CarFacts[k].CarName
        // overrides. V2 calls the migration after _carStore is alive and
        // walks CarFacts user names first, then baked names. Set
        // CarPresetOrdinalNamesMigratedV2 to true once the corrected pass
        // has run; safe to keep both flags forever.
        public bool CarPresetOrdinalNamesMigratedV2 { get; set; } = false;

        // One-time migration latch. Flips true after legacy "Forza_NNN" car
        // ids have been normalized to "Car_NNN". Released builds stored Forza
        // car tunings under the id "Forza_<ordinal>" (the old UDP-fallback
        // shape); this branch's telemetry emits "Car_<ordinal>" and rewrites
        // any incoming Forza_<n> to Car_<n> before every lookup, so an
        // un-normalized Forza_<n> car file / default binding can never be
        // found again and the user's per-car tuning silently stops applying.
        // On the first launch after this build ships we run the same
        // normalization the NORMALIZEFORZA dev tool performs (rename folders,
        // rewrite CarId + PresetName + both car-defaults.json files + the live
        // Settings.CarDefaults/CarOverrides dicts) so those tunings keep
        // applying. Idempotent and content-preserving (a Forza_<n> that
        // collides with an existing Car_<n> is merged, never dropped).
        public bool ForzaCarIdsNormalizedV1 { get; set; } = false;

        // Community backend settings. Defaults are inert: CommunityEnabled
        // is off, no HTTP calls are made. Enabling it activates fire-and-
        // forget submission of User-source CarFacts corrections + (later)
        // pulls of trusted consensus entries on startup. See
        // supabase/README.md for the schema and setup.
        //
        // Identity (submitter_id) is derived server-side from a hash of the
        // client IP and a rotating salt, so the plugin holds no persistent
        // submitter id and cannot frame another user. Anti-spam is enforced
        // server-side via a per-IP rate limit. The anon API key shipped to
        // the backend is treated as PUBLIC by the schema design - the only
        // operations it can perform are: SELECT from car_fact_consensus,
        // and CALL submit_car_fact (car-fact consensus is confirm/correct
        // based, not voted).
        //
        // Backend URL + anon key are blank by default. For release builds
        // they get baked in as CommunityClient constants and the toggle is
        // the only switch users see. For dev builds the user can override
        // via these fields to point at a staging project.
        public bool   CommunityEnabled         { get; set; } = false;

        // Show the preset "Share" buttons on the active car/game header card (the
        // Effects tab). Default on for discovery; a user who uses community data but
        // never shares presets can turn this off to declutter. The Preset Manager
        // stays the place to share regardless. Does not affect car-fact sharing.
        public bool   ShowEffectsTabShareButtons { get; set; } = true;

        public string CommunityBackendUrl     { get; set; } = "";
        public string CommunityBackendAnonKey { get; set; } = "";

        // Whether community CAR-FACT data (names, engine layouts, redlines) is
        // APPLIED to the user's cars. Split out from CommunityEnabled (which is
        // the networking master, also gating preset sharing) so a user can keep
        // using already-fetched community car facts WITHOUT live networking:
        //   CommunityEnabled off + UseCommunityCarFacts on = apply the local
        //   cache only, never hit the server. Default on so existing community
        //   users keep their car facts. Networking (fetch/submit) still requires
        //   CommunityEnabled; this only governs whether the data is used.
        public bool   UseCommunityCarFacts     { get; set; } = true;

        // Local, persisted cache of the per-car community fetch (name + layout +
        // redline consensus) keyed by "game/carId/variantSignature", with a
        // fetch timestamp for TTL. Lets the plugin apply community car facts
        // offline / between fetches and refresh only when stale (CommunityCacheTtl)
        // or on a manual refresh, instead of pulling every car open. Re-fetchable,
        // so it is MachineLocal (not backed up). Never null after first use.
        public Dictionary<string, CommunityFactCacheEntry> CommunityFactCache { get; set; }
            = new Dictionary<string, CommunityFactCacheEntry>();

        // How long a cached community-fact entry is considered fresh before the
        // next car-open triggers a background refresh (manual refresh ignores it).
        // Kept as a constant (not user-facing) - the user-facing control is the
        // refresh icon in the Car Facts panel.
        [JsonIgnore]
        public static readonly TimeSpan CommunityCacheTtl = TimeSpan.FromDays(7);

        // ---- Message of the Day (MOTD) -------------------------------------
        // How much of the MOTD feed the user wants on the top strip. PORTABLE
        // (a genuine user choice). Selecting None warns in the UI that important
        // messages may be missed; it is honored literally (no "None still shows
        // critical" exception). See docs/motd-design.md.
        [JsonConverter(typeof(StringEnumConverter))]
        public MotdLevel MotdLevel { get; set; } = MotdLevel.All;

        // Offline-first cache of the MOTD feed (~6h TTL via FetchedAtUtc).
        // Re-fetchable, so EXCLUDED from backup. Never null after first use.
        public MotdCacheData MotdCache { get; set; } = new MotdCacheData();

        // IDs of scheduled/important messages the user dismissed permanently
        // (seen-id model). Transient UI state, EXCLUDED from backup.
        public List<string> MotdDismissedIds { get; set; } = new List<string>();

        // Pool messages dismissed "for today": message id -> "yyyy-MM-dd" of the
        // dismissal. A pool message reappears on a later day. EXCLUDED.
        public Dictionary<string, string> MotdPoolDismissedOn { get; set; }
            = new Dictionary<string, string>();

        // Recurring messages dismissed for the CURRENT occurrence only (they
        // return next year): message id -> occurrence-start-date token
        // ("yyyy-MM-dd"). Transient UI state, EXCLUDED from backup.
        public Dictionary<string, string> MotdRecurringDismissedOcc { get; set; }
            = new Dictionary<string, string>();

        // ---- MOTD audience / nag pacing (client-side personalization) -------
        // Last time the user performed each contribution (UTC). Drives the
        // recency-gated suppression of the matching community nudge: a `non_sharer`
        // nudge is hidden only while LastSharedPresetOn is within MotdContributionRecency
        // (60 days); after that the nudge returns so a lapsed contributor gets
        // re-invited. null = never. EXCLUDED from backup (nag/learned state that
        // re-learns harmlessly on a second PC).
        public DateTime? LastSharedPresetOn  { get; set; }
        public DateTime? LastVotedOn         { get; set; }
        public DateTime? LastSubmittedFactOn { get; set; }

        // Local date ("yyyy-MM-dd") a MOTD nag last appeared, for the few-day nag
        // cooldown that keeps promo / call-to-action messages from running
        // back-to-back. EXCLUDED from backup. null = none yet.
        public string MotdLastNagOn { get; set; }

        // How recent a contribution must be to suppress its community nudge.
        [JsonIgnore]
        public static readonly TimeSpan MotdContributionRecency = TimeSpan.FromDays(60);

        // How long a cached MOTD feed is considered fresh before a background
        // refresh. The dial between server load and important-message latency.
        [JsonIgnore]
        public static readonly TimeSpan MotdCacheTtl = TimeSpan.FromHours(6);

        // Opt-in auto-apply for community preset updates the user previously
        // downloaded. Default off. Only applies when the local body still
        // matches the original-download hash (no local edits since download);
        // any local edit makes the entry sticky and the user must accept the
        // update manually via PresetUpdatesAvailableWindow.
        public bool   AutoUpdateDownloadedPresets { get; set; } = false;

        // How often, in hours, the plugin re-polls GitHub Releases for a newer
        // Trueforce For All build during a running session. A check ALWAYS runs
        // once at startup and on every manual "Check for updates" click,
        // regardless of this value; this governs only the background re-check
        // cadence so a long-running session still discovers a release that
        // shipped after launch. 0 = "On startup only" (no background re-check).
        // Surfaced as the Settings-tab "Updates" dropdown. Allowed: 0/1/2/6/24.
        public int    UpdateCheckIntervalHours { get; set; } = 2;

        // Opt-in standing consent: once on, the user's car-fact corrections
        // (redline, engine layout, car name) are submitted to the community
        // without the per-edit "share this?" prompt. Default off; the user
        // turns it on by picking "Always submit" in that prompt, or via the
        // Settings checkbox. Still gated by CommunityEnabled + sign-in exactly
        // like the prompts, so it never sends anything the prompt wouldn't have.
        public bool   AutoSubmitCarFacts { get; set; } = false;

        // Single "last view was online" latch for the Preset Manager: true when
        // the user left the manager in Community / My uploads, so the next open
        // restores the online view. (Historically one latch per segment; the
        // 3-way nav overhaul collapsed them and reuses this flag globally, see
        // HydrateModeToggles / PersistManagerMode in PresetManagerControl.)
        public bool ManagerCommunityForCars    { get; set; } = false;

        public float MasterGain { get; set; } = 1.0f;

        // Step master gain moves on each press of a bound Controls-tab action
        // (master gain up / down). Per-machine, not preset-saved. Defaults to
        // the master-gain slider's small step (0.05).
        public float MasterGainStep { get; set; } = 0.05f;

        // Inject a Trueforce quick-gain box into SimHub's home-screen "Feedback"
        // section, next to Motors/Wind. That section is hardcoded in SimHubWPF
        // with no plugin extension point, so the box is added by a defensive
        // runtime visual-tree splice (FeedbackBoxInjector) that fails silently
        // if SimHub's home layout changes. On by default (with a Settings toggle
        // to remove it); FeedbackBoxDefaultedOn migrates existing installs on.
        public bool ShowFeedbackBox { get; set; } = true;

        // One-time migration latch: flips existing installs' ShowFeedbackBox on
        // once, when they update to the build that made the home tile default-on,
        // without overriding a later user opt-out.
        public bool FeedbackBoxDefaultedOn { get; set; } = false;

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

        // Issue #13 test path (NOLOCK access code). When on, the plugin fully
        // leaves Trueforce mode while the game is paused (SendStopCommand +
        // Pause) so the wheel reverts to its native FFB, e.g. Forza's own
        // auto-center, instead of us streaming a substitute force. This is the
        // "behave like the plugin is disabled while paused" approach, which is
        // the only thing confirmed to stop the G923/FH6 full-lock. Default off;
        // the existing return-zero pause-release stays the default until this
        // is validated on hardware, after which it can become the standard
        // behaviour and this toggle removed.
        public bool  StopStreamOnPause        { get; set; } = false;

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

        // EXPERIMENTAL: claim sole wheel ownership through the TFFA kernel
        // filter driver and route the game's intercepted HID++ FFB writes
        // through the Trueforce stream. Off = no driver code path is taken
        // at all (the FFB pipeline and lifecycle behave exactly as shipped);
        // needs the TFFA filter driver installed to do anything. The actual
        // on/off; the DRIVER access code first reveals a hidden checkbox
        // (DriverTestingUnlocked below) and flips this on.
        public bool   ExperimentalDriverIntercept { get; set; } = false;

        // Whether the hidden "Driver testing mode" checkbox has been revealed
        // (via the DRIVER access code). Persists the revealed state so the
        // checkbox stays visible across restarts once unlocked, while
        // ExperimentalDriverIntercept above remains the actual on/off. Off =
        // fully hidden for anyone who has never typed DRIVER. Mirrors the
        // ShowManualOverrideUi unlock pattern.
        public bool   DriverTestingUnlocked       { get; set; } = false;

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
        public bool  DuckingEnabled { get; set; } = true;
        public float DuckDepth     { get; set; } = 0.60f;
        public float DuckAttackMs  { get; set; } = 5.0f;
        public float DuckReleaseMs { get; set; } = 80.0f;
        // Frequency-aware ducking: only ducks effects that overlap in
        // frequency, so a slide stays crisp through the engine pulse instead
        // of blending into it. Off = classic full-band ducking.
        public bool  DuckFrequencyAware { get; set; } = false;

        // ---- Telemetry based FFB (Mode B): the wheel's steering force is
        // built from telemetry (slip angle, tire load, speed) instead of the
        // game's own FFB. Default OFF (owner decision): it REPLACES the
        // game's force, so the user opts in from the Telemetry Based FFB tab.
        // Arms only on Mode B capable games (IsModeBCapableGame: FM8, where
        // classic FFB is unavailable anyway, plus FH5/FH6, run with in-game
        // FFB and vibration at 0). Every other game keeps its normal path.
        // Global-only for now: none of this travels in presets. Defaults are
        // the first on-wheel-tuned recipe (2026-07-03), G PRO-validated on
        // FH6 2026-07-08. ----
        public bool  ModeBEnabled   { get; set; } = false;
        public float ModeBSatGain   { get; set; } = 1.0f;    // peak torque fraction
        public float ModeBRiseGamma { get; set; } = 0.5f;    // <1 = weight arrives in normal cornering
        public float ModeBPeakUtil  { get; set; } = 1.0f;    // combined-slip value treated as the grip limit
        public float ModeBDropFloor { get; set; } = 0.20f;   // torque left past the limit
        public float ModeBEmaMs     { get; set; } = 40f;     // input smoothing time constant
        public float ModeBSign      { get; set; } = 1f;      // SAT direction (BSIGN; -1 flips)
        public float ModeBDamper    { get; set; } = 0.15f;   // wheel weight: velocity damping (Mode B only)
        public float ModeBCenter    { get; set; } = 0.10f;   // wheel weight: speed-scaled centering (Mode B only)
        public float ModeBLatGain     { get; set; } = 0.6f;  // cornering weight: +gain per lateral g (BLAT)
        public float ModeBCounterGain { get; set; } = 0.5f;  // slide counter-force on rear breakaway (BCS)
        public float ModeBDirSoft     { get; set; } = 0.12f; // center flat-spot width (BDIRK); 0 = raw linear

        // Mode B feel features (the haptic-engine layers 6-11, all validated
        // on-wheel and graduated to default ON there; the Mode B master
        // switch above is the real gate).
        public bool  ModeBCompressor         { get; set; } = true;   // soft-knee ceiling on the force
        public bool  ModeBSuspensionLoad     { get; set; } = true;   // steering load from suspension compression
        public bool  ModeBEarlyTorquePeak    { get; set; } = true;   // torque plateaus at 75% utilization
        public bool  ModeBRoadKick           { get; set; } = true;   // one-wheel bump kick in the force channel
        public float ModeBRoadKickGain       { get; set; } = 1.0f;   // kick strength
        public bool  ModeBSlideCounterGrowth { get; set; } = true;   // counter-force grows with slide depth
        public bool  ModeBGripAutoCal        { get; set; } = true;   // per-car grip-limit auto-calibration

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
        public AxleSlipSettings     AxleSlip     { get; set; } = new AxleSlipSettings();
        public KerbThumpSettings    KerbThump    { get; set; } = new KerbThumpSettings();
        public LockupJudderSettings LockupJudder { get; set; } = new LockupJudderSettings();

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

        // Built-in preset source folder. Blank = use the shipped default next
        // to the plugin DLL (<dll>\TrueforceForAll-Presets). A user can point this at
        // a moved folder (repair) or a shared "preset pack" to swap the seed
        // set. Machine-local, survives preset switches. See BuiltinPresets.
        public string BuiltinPresetsFolder { get; set; } = "";

        // User-imports folder. Drop community / shared preset files here and
        // they get auto-imported into the library as USER presets on next
        // plugin start, then moved to an 'imported' archive subfolder. Blank =
        // default beside the plugin DLL (<dll>\TrueforceForAll-Imports).
        public string UserImportsFolder { get; set; } = "";

        // User-library folder. Holds the user's own (non-builtin) presets as
        // files, mirroring the built-in folder layout (games/, cars/<game>/,
        // game-defaults.json, car-defaults.json). Blank = default at
        // <SimHub>\PluginsData\Common\TrueforceForAll-Library. The previous
        // model kept user presets inside the Presets dict below; this folder
        // replaces that, with a one-time migration on first launch.
        public string UserLibraryFolder { get; set; } = "";

        // Flips to true after the one-time migration moves the legacy in-dict
        // user game presets (Settings.Presets / GameDefaults entries that
        // weren't built-ins) into files in the user-library folder. Once true,
        // the dicts are treated as a transient runtime cache rebuilt from disk
        // on each Init.
        public bool PresetsMigratedV2 { get; set; } = false;

        // Flips to true after the one-time car migration moves legacy
        // TrueforceCars/*.tfcar.json files into the user library cars/
        // tree and Settings.CarDefaults into car-defaults.json. Separate from
        // PresetsMigratedV2 because game migration shipped first; users who
        // already migrated games still need to migrate cars when this lands.
        public bool CarsMigratedV2 { get; set; } = false;

        // Flips to true after the one-time cleanup pass that walks
        // user/games and user/cars looking for files whose stems match a
        // current OR retired built-in name (IsFactoryBuiltinName). The
        // PresetsMigratedV2 check used an incomplete RetiredBuiltinNames
        // when it ran, so users who upgraded across the pre-V2 to file-
        // based-factory boundary had old (default)-named built-ins land in
        // user/ as if they were user-authored. This pass archives them to
        // user/{games,cars}/.cleanup-<timestamp>/ and drops matching
        // entries from user/{game,car}-defaults.json so the factory seed
        // takes over.
        public bool LegacyBuiltinsCleanedV1 { get; set; } = false;

        // Flips to true after the one-time folder restructure moves
        //   <SimHub>\TrueforceForAll-Presets             -> PluginsData\Common\TrueforceForAll\factory
        //   <SimHub>\PluginsData\Common\TrueforceForAll-Library -> .../TrueforceForAll\user
        //   <SimHub>\PluginsData\Common\TrueforceForAll-Imports -> .../TrueforceForAll\user\import
        // and stamps so it doesn't run again. The new layout collapses three
        // sibling folders into one root with the two real roles (factory, user)
        // and the import inbox as a subfolder of user.
        public bool FoldersRestructuredV3 { get; set; } = false;

        // ---- Serialization gates for the now-runtime-cache dicts ----
        //
        // Settings.Presets / GameDefaults / CarDefaults / CarOverrides are
        // rebuilt on every Init from the file-based folders (BuiltinPresets,
        // UserPresets, _carStore), so post-migration they no longer need to
        // persist to GeneralSettings.json. We can't [JsonIgnore] them outright
        // because the one-time legacy migrations read those dicts on Init for
        // upgrading users; the gate has to be 'serialize until the matching
        // migration latch is set, then drop'. Newtonsoft only consults
        // ShouldSerialize on WRITE, so reads still populate the dicts for the
        // migration to find. After the migration clears the dicts and flips the
        // latch, subsequent saves omit them and the file stays clean.
        public bool ShouldSerializePresets()       => !PresetsMigratedV2;
        public bool ShouldSerializeGameDefaults()  => !PresetsMigratedV2;
        public bool ShouldSerializeCarDefaults()   => !CarsMigratedV2;
        public bool ShouldSerializeCarOverrides()  => !CarsMigratedV2;

        // Developer mode unlock. Set by the DEV access code; reveals the
        // Developer panel + built-in export/import/reseed/validate buttons.
        // Persisted so it stays on across restarts on a dev machine.
        public bool DevModeUnlocked { get; set; } = false;

        // Escape hatch for the import preview modal. Toggled by the PREVIEWOFF
        // access code. When true, RunImportFlow falls back to today's silent
        // commit-on-pick path (per-file dispatch + summary MessageBox) so a
        // user can recover if the modal breaks on a specific file. Default
        // false: preview is on for everyone.
        public bool ImportPreviewBypass { get; set; } = false;

        // Author name auto-stamped onto exported presets / car presets / packs.
        // Set once via the Backup & sync section; the export-info dialog
        // pre-fills it and writes back any edits the user makes there. Blank
        // by default; users who never set it just produce anonymous exports.
        public string SharingAuthor { get; set; } = "";

        // Supabase Auth session (email OTP). Persisted so a signed-in
        // user stays signed in across plugin restarts. Null when not
        // signed in; on access, the auth client refreshes the token if
        // it's within the refresh window. The session unlocks edit/delete
        // on the user's own preset uploads.
        public CommunityAuthSession AuthSession { get; set; }

        // "Remember my email" convenience for the sign-in modal. When on (the
        // default), the email typed at the last code request is prefilled next
        // time the sign-in window opens, so a returning user doesn't retype it.
        // MACHINE-LOCAL: a per-PC login convenience tied to this install's usage
        // (like LegacyDataOwnerEmail); it never travels in cloud backup.
        // LastSignInEmail is stored DPAPI-encrypted at rest (SignInWindow
        // StoreEmail/LoadEmail, "dpapi:" prefix); legacy plaintext values are
        // read as-is and re-encrypted on the next send.
        public bool RememberSignInEmail { get; set; } = true;
        public string LastSignInEmail { get; set; } = "";

        // The last Trueforce wheel model detected on THIS PC, as a short chassis
        // label (e.g. "G PRO"). Shown in the Account "Active sessions" list so a
        // device is identifiable by its wheel, and reported by the session
        // heartbeat. MACHINE-LOCAL: describes this PC's hardware, so it never
        // travels in cloud backup. Empty until a wheel is first detected; sticky
        // across unplugs and restarts (only overwritten by a new detection).
        public string LastUsedWheel { get; set; } = "";

        // Cloud-backup sync bookkeeping (Phase 2). The Storage object `version` of
        // the backup this PC last pushed or pulled. MACHINE-LOCAL: each PC tracks its
        // own sync point and it is NEVER itself backed up. Used to detect divergence
        // (cloud changed since this PC last synced) so a manual backup can
        // fast-forward, or on conflict offer the merge dialog. Empty = never synced.
        public string BackupLastSyncedRevision { get; set; } = "";

        // Last-synced portable settings projection (Settings+Forza JObjects, serialized): the COMMON
        // ANCESTOR for the auto-sync field-level 3-way merge, so edits on each PC to DIFFERENT
        // settings fields both survive instead of one side winning the whole object. Per-PC sync
        // bookkeeping; never itself backed up. Empty = no baseline yet.
        public string BackupLastSyncedEnvelopeJson { get; set; } = "";

        // Auto-sync backup (Phase 2). When on, the plugin pushes a fresh cloud backup
        // shortly after settings/preset changes (debounced, FAST-FORWARD ONLY: on a
        // detected divergence it does NOT silently merge, it logs and waits for a manual
        // reconcile via the conflict dialog). MACHINE-LOCAL: each PC opts in to
        // background uploads explicitly, so a freshly-restored second PC never starts
        // pushing unprompted.
        public bool AutoSyncBackupEnabled { get; set; } = false;

        // DEV/TEST ONLY: forces the supporter BADGE to display a given tier
        // ("Supporter" / "Gold Supporter" / "Platinum Supporter"); empty = show the real
        // entitlement. Set via the SUPPORTER access code. MACHINE-LOCAL + display-only: it
        // feeds ONLY the badge label, never the backup gate (which is enforced server-side
        // by RLS), so it can never grant real supporter access.
        public string DevSupporterBadgeOverride { get; set; } = "";

        // Dev/test: when true, the achievements tracker requests secret achievements too (OG,
        // Founding Supporter) even when unearned, so they can be previewed. Toggled by the SHOWALL
        // access code. MACHINE-LOCAL (not backed up).
        public bool DevShowAllAchievements { get; set; }

        // Show in-plugin achievement celebration toasts (default on). A global opt-out;
        // each achievement only celebrates once regardless. PORTABLE preference.
        public bool ShowAchievementCelebrations { get; set; } = true;

        // Baseline of already-seen earned achievement keys (CSV) so a newly-EARNED one is
        // celebrated exactly once. Seeded silently on first run (no flood for existing earns)
        // and reset on account switch. EXCLUDED from backup (transient local detection state;
        // a fresh PC re-seeds its own baseline silently).
        public string AchievementBaseline { get; set; } = "";

        // Notification-dot state: a newly-earned achievement the user hasn't opened the
        // tracker to see yet. Set on detect, cleared when the Achievements window opens.
        // EXCLUDED from backup (transient local UI state; resets on account switch).
        public bool AchievementUnseen { get; set; } = false;

        // Welcome modal lifecycle. We show the pitch up to TWO times:
        // first plugin load (DeclineCount==0, NextShowAt==null), then
        // again after WelcomeReshowDays (~14 days) if the user picked
        // "Maybe later". A second decline flips HasSeenNetworkedWelcome
        // true and we never nag again. "Sign in now" (whether or not the
        // sign-in flow completes) counts as action-taken and stops the
        // pitch immediately.
        public bool      HasSeenNetworkedWelcome { get; set; } = false;
        public int       WelcomeDeclineCount     { get; set; } = 0;
        public DateTime? WelcomeNextShowAt       { get; set; } = null;

        // Tracks community presets the user has downloaded so we can
        // notify them when the upstream curator publishes a new
        // version. Key = preset uuid (string), value = the local-side
        // metadata we need to render "X has an update" and to compare
        // versions on plugin-load. The "Skip" action in the update
        // notification bumps SeenContentVersion to the latest so a
        // single dismissal lasts until the NEXT real edit.
        //
        // PER-USER PARTITION: when more than one Supabase account uses
        // the same install (a shared family PC, a sim rig at a friend's
        // place), each account keeps its OWN copy of this dict + its
        // own SharingAuthor in Settings.UserSlots. At any given moment
        // the field below is a reference to UserSlots[ActiveSlotKey]'s
        // dict, so existing read/write call sites are unaffected. A
        // future cloud-sync feature will push/pull each slot to the
        // server; for now slots are local-only and survive sign-out.
        public Dictionary<string, DownloadedPresetRecord> DownloadedCommunityPresets { get; set; }
            = new Dictionary<string, DownloadedPresetRecord>();

        // Global cooldown clock for the one-time "rate the preset you've
        // been running" nudge. After ANY nudge fires we stamp UtcNow here
        // and suppress all further nudges for VoteNudgeGlobalCooldownHours,
        // so a user who downloads several presets in one sitting isn't
        // nagged for each. Null = no nudge has ever fired.
        public DateTime? LastVoteNudgeUtc { get; set; } = null;

        // Consecutive times the user dismissed a vote nudge with "Later"
        // without voting. Reset to 0 whenever they vote from a nudge. Once it
        // reaches VoteNudgeMaxConsecutiveDismissals the nudge goes dormant - a
        // user who keeps ignoring it has signalled they won't engage, so we
        // stop nagging; anyone who votes resets it and keeps being asked about
        // future presets.
        public int      ConsecutiveVoteNudgeDismissals { get; set; } = 0;

        // Per-local-user data slots. Key = lower-cased email (the
        // user's Supabase identity) or "" for anonymous activity.
        // Mounting / migration is handled by TrueforcePlugin's slot
        // manager, NOT by direct dictionary mutation; treat this as
        // backing storage.
        public Dictionary<string, UserDataSlot> UserSlots { get; set; }
            = new Dictionary<string, UserDataSlot>();

        // The key whose slot is currently mounted into the legacy
        // DownloadedCommunityPresets + SharingAuthor fields. The key is
        // the immutable Supabase user-id (empty string = anonymous), so a
        // later email change can't orphan a slot. Set by the slot manager
        // on sign-in / sign-out; do not write directly.
        public string ActiveSlotKey { get; set; } = "";

        // One-time migration latch: the first run after the slot
        // feature ships moves any pre-existing DownloadedCommunityPresets
        // + SharingAuthor into the appropriate slot.
        public bool UserSlotsMigratedV1 { get; set; } = false;

        // One-time migration latch: re-key any email-keyed slots (from the
        // never-shipped email scheme / pre-user-id test builds) to the
        // immutable user-id, so keying is stable across an email change.
        public bool SlotsKeyedByUserIdV1 { get; set; } = false;

        // Snapshot of the email that owned legacy (pre-slot) data; set once on first sign-in so migration targets the right slot even if the user re-auths before EnsureUserSlotsMounted runs.
        public string LegacyDataOwnerEmail { get; set; } = "";

        // ---- Per-effect "NEW" badges + changelog banner (see EffectChangelog) ----

        // Effect IDs the user has acknowledged. An ID present here means
        // the per-effect "NEW" badge is suppressed for that section. Plugin
        // pre-seeds this with every known effect on fresh install (and on
        // first run for users upgrading from a pre-feature settings file),
        // so badges only ever surface for effects added in versions newer
        // than the running build at the time of stamp. Schema: list of
        // stable string IDs that match EffectChangelog.KnownEffectIds.
        public List<string> SeenEffects { get; set; } = new List<string>();

        // Auto-retire of "NEW" effect badges the user keeps ignoring. NewEffectViewCount
        // counts Effects-tab opens while at least one badge is showing; at the dismiss
        // threshold the still-unseen effects are marked seen. NewEffectBadgeUnseenBaseline
        // records the unseen count the counter is tracking, so a newly shipped effect
        // (which grows the unseen set) restarts the countdown and gets its full run of views.
        public int NewEffectViewCount { get; set; } = 0;
        public int NewEffectBadgeUnseenBaseline { get; set; } = 0;

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

        // Persisted sort preferences for the preset manager, one per
        // tab. Key matches a column's binding path (e.g. "Name",
        // "Source"); empty/null = natural order. Hydrated when the
        // dialog opens, rewritten on every header click.
        public ManageSort ManageGamesSort   { get; set; } = new ManageSort();
        public ManageSort ManageCarsSort    { get; set; } = new ManageSort();
        public ManageSort ManageCustomsSort { get; set; } = new ManageSort();

        // Persisted column layout (width + display order) for the preset
        // manager tabs. Keyed by column binding path the same way sort is.
        // Empty list = use XAML declared layout; populated entries override
        // per column. Rewritten on every drag-resize and every reorder.
        public ManageColumnLayout ManageGamesColumns   { get; set; } = new ManageColumnLayout();
        public ManageColumnLayout ManageCarsColumns    { get; set; } = new ManageColumnLayout();
        public ManageColumnLayout ManageCustomsColumns { get; set; } = new ManageColumnLayout();

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

    /// <summary>Persisted per-car grip-calibration snapshot (telemetry based
    /// FFB). Mirrors GripPeakLearner's export surface: the learned metric
    /// ceiling and the near-limit seconds that back it (confidence).</summary>
    public sealed class CarGripCal
    {
        public float Peak          { get; set; } = 1.0f;
        public float QualifyingSec { get; set; }
    }

    /// <summary>User-authored engine definition. Stored in
    /// <summary>Persisted sort state for one of the preset manager tabs.
    /// Empty Key = natural (insertion) order; populated Key matches the
    /// binding path of the column to sort on.</summary>
    public sealed class ManageSort
    {
        public string Key { get; set; }
        public bool   Descending { get; set; }
    }

    /// <summary>Persisted layout (width + display order) for one preset
    /// manager tab. Columns identified by binding path so renames in XAML
    /// don't silently apply stale widths/orders to the wrong column.</summary>
    public sealed class ManageColumnLayout
    {
        public System.Collections.Generic.List<ManageColumnState> Columns { get; set; }
            = new System.Collections.Generic.List<ManageColumnState>();
    }

    public sealed class ManageColumnState
    {
        public string Key { get; set; }            // binding path, e.g. "Name"
        public int    DisplayIndex { get; set; }   // 0-based visual position
        public double WidthValue { get; set; }     // numeric width
        public string WidthType { get; set; }      // "Star" | "Pixel" | "Auto" | "SizeToCells" | "SizeToHeader"
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

        /// <summary>Server uuid of the community row this engine was
        /// downloaded from. Null when the user authored it locally.
        /// Survives rename / duplicate / edit and is the identity the
        /// pack creator + Share-button gate look up against (so they
        /// trust ids, not local names). Stamped by
        /// SaveImportedCommunityCustomEngine and the pack import
        /// path; cleared if the user explicitly forks the engine.
        /// Tier 3 community metadata; pre-existing engines deserialize
        /// it as null so legacy library files keep loading unchanged.</summary>
        public string CommunitySourceId { get; set; }

        /// <summary>Server uuid of the community row this user uploaded
        /// this engine to (distinct from CommunitySourceId which tracks
        /// downloads). Null = never uploaded by current user. Used by the
        /// Share button gate to detect a user-owned re-uploadable row.</summary>
        public string CommunityUploadedById { get; set; }

        /// <summary>The user uuid that owns CommunityUploadedById, stamped
        /// from AuthSignedInUserId at upload time. Lets the gate tell
        /// "I uploaded this" from "I downloaded my own upload".</summary>
        public string CommunityUploadedByUserId { get; set; }

        /// <summary>SHA256 hex of the body at last successful upload (or
        /// update). Null until first upload. Drives the "Share disabled
        /// when current body matches last upload" gate.</summary>
        public string CommunityUploadedBodyHash { get; set; }

        /// <summary>Auto-computed display version ("v1", "v2", ...) derived
        /// from the server's content_version at upload time. Null until
        /// first upload, never user-editable.</summary>
        public string CommunityUploadedVersion { get; set; }

        /// <summary>Author's "ok to re-bundle into someone else's pack"
        /// permission. Mirrors the field on GameSettingsSnapshot - travels
        /// with export/import so peer-to-peer share preserves the original
        /// author's choice without depending on the per-download tracker.
        /// Nullable: null falls back to the tracker for legacy installs.</summary>
        public bool? CommunityAllowInPacks { get; set; }

        /// <summary>Optional credit field. Set on export by stamping the
        /// curator's SharingAuthor when the def doesn't already have one,
        /// preserved on import so a recipient who acquires "MyV12 by Mhytee"
        /// via a shared preset can see who authored it. Locally-created
        /// defs leave this blank until shared (and until the local user has
        /// set Settings.SharingAuthor).</summary>
        public string Author { get; set; }
    }

    // ============================================================
    // Car Facts layer
    //
    // Splits the preset model into two layers:
    //   - Presets carry preferences (gain, scale, waveform).
    //   - CarFacts carries community-vetted truth about a car (engine
    //     layout, redline RPM, human-readable name).
    //
    // The existing EnginePulseSettings.Layout enum's Auto value is the
    // trigger to consult this layer. When Layout is anything other than
    // Auto, the preset is overriding the facts — sharing the preset ships
    // the override, but CarFacts on the recipient's side is untouched.
    //
    // Variants exist because Forza-style games allow in-game engine swaps
    // that change cylinder count + redline while keeping the same carId.
    // Most cars in most games have exactly one variant, in which case the
    // picker UX never shows. See project_car_facts_layer memory entry for
    // full design notes.
    // ============================================================

    /// <summary>Where a CarFacts value came from. Drives the per-field source
    /// line in the UI ("scanner detected" vs "community" vs "you corrected")
    /// and gates community-submission prompts. (The community support count
    /// shown alongside comes from the live consensus, not from this
    /// enum.)</summary>
    public enum CarFactSource
    {
        /// <summary>Plugin's runtime scanner / heuristic. The starting point
        /// for most cars before community data exists.</summary>
        Scanner,
        /// <summary>Pulled from the community DB with the trusted-tier
        /// threshold (Wilson-ranked).</summary>
        Community,
        /// <summary>User typed / corrected this value locally. Wins over
        /// community + scanner when present.</summary>
        User,
        /// <summary>User registered this variant via the "new variant
        /// detected" prompt (auto-detect-and-confirm flow). Ranks below
        /// the legacy "Correct..." User source but above Community so
        /// freshly-named variants are picked first when their signature
        /// matches telemetry. Distinct from User so the legacy filter
        /// at PickStoredVariant (which excludes User to keep corrections
        /// out of the matching pool) doesn't accidentally hide newly-
        /// registered variants.</summary>
        UserVariant,
        /// <summary>Game telemetry supplies the value directly each session
        /// (e.g. AC's CarSettings_RedLineRPM). Treated as the truth and
        /// never persisted — the apply path reads it live each time.</summary>
        GameTelemetry,
        /// <summary>Synthesized at lookup time from BuiltinCarCylinders (the
        /// in-DLL curated table covering AC + FH5). Never persisted to disk:
        /// the variant is constructed on the fly so future bake updates flow
        /// automatically. Conceptually a curated baseline that any stored
        /// User / Community correction can override.</summary>
        Baked,
        /// <summary>A Baked variant whose cylinder + config came from the
        /// AC swap-override pass: BuiltinCarCylinders had the chassis as
        /// (say) 4-cyl Inline, but the car's ui_car.json description carried
        /// a "swap" marker plus a known engine codename (LS / 2JZ / RB26),
        /// and CarCylinderResolver's TryAcSwapOverride rewrote the layout
        /// to match the swap. Surfaced as its own source so the diagnostic
        /// label tells you the refinement fired (and a wrong swap-override
        /// can be reported / corrected separately from a wrong base bake).</summary>
        SwapOverride,
    }

    /// <summary>One engine configuration for a (game, carId). For most cars
    /// the bundle has exactly one variant ("Stock") and the user never sees
    /// a picker. Forza in-game engine swaps create same-carId-multiple-
    /// variants, in which case the UI surfaces a default selector and the
    /// plugin auto-picks via telemetry when it can disambiguate.</summary>
    public sealed class EngineVariant
    {
        /// <summary>Stable identifier. Survives label renames so the user's
        /// CarFactsSelection doesn't get invalidated when a moderator
        /// cleans up community labels.</summary>
        public string Id { get; set; }

        /// <summary>Display label. "Stock V8" / "SR20 swap" / "LSx swap".
        /// Auto-detected variants land with a generic label that the user
        /// edits at confirmation time.</summary>
        public string Label { get; set; }

        /// <summary>Cylinder count. 0 means "unknown / use heuristic."</summary>
        public int Cylinders { get; set; }

        /// <summary>Engine configuration (V / Inline / Boxer / Rotary /
        /// Electric / Auto). Paired with Cylinders, drives the firing-pattern
        /// derivation via FiringPatternDb. Auto = unknown.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineConfig EngineConfig { get; set; } = EngineConfig.Auto;

        /// <summary>Optional custom firing-pattern string for community
        /// submissions that don't fit a stock pattern. Used in place of
        /// EngineLayout-derived pattern when non-empty. Format mirrors
        /// CustomEngineDef.Pattern (FiringPatternDb.ParseCustom).</summary>
        public string CustomFiringPattern { get; set; } = "";

        /// <summary>Optional absolute redline RPM. Only meaningful for games
        /// whose telemetry doesn't expose a trustworthy RedLineRPM value
        /// (Forza family). null = fall through to telemetry or MaxRpm
        /// threshold.</summary>
        public int? RedlineRpm { get; set; }

        /// <summary>The user's own saved redline for THIS variant (set via the
        /// redline slider + "Save for this variant"). Wins over the community
        /// consensus and the telemetry/percentage fallback for this variant
        /// only. null = the user hasn't pinned one; follow the cascade
        /// (community / telemetry / percentage / default).</summary>
        public int? UserRedlineRpm { get; set; }

        /// <summary>The community redline PROFILE (overall + per-gear) the user
        /// declined to adopt for this variant, as a stable signature string
        /// ("overall;gear:rpm;..."). Per-gear-aware decline memory: suppresses
        /// the adopt offer for this exact profile, but a community shift in any
        /// gear produces a new signature so the offer can surface again.
        /// null = never declined.</summary>
        public string DeclinedCommunityRedlineSig { get; set; }

        /// <summary>The community value the user ADOPTED for this variant (copied
        /// into <see cref="RedlineRpm"/> so it survives offline / cache clears).
        /// Tracked separately so that if the community consensus later moves away
        /// from the adopted value, the "community changed - switch?" prompt can
        /// surface again. null = the user hasn't adopted a community value.</summary>
        public int? AdoptedCommunityRedlineRpm { get; set; }

        /// <summary>Optional per-gear redline overrides for THIS variant (user-set).
        /// When the current forward gear has an entry the rev limiter buzzes at
        /// that RPM instead of the single redline; gears with no entry (and
        /// reverse / neutral) fall back to the single value. null / empty = none.
        /// User-set, so it overrides community + telemetry, same as the single
        /// pinned redline.</summary>
        public List<GearRedline> PerGearRedlines { get; set; }

        /// <summary>Optional engine rev ceiling (MaxRpm). Captured at the
        /// moment the variant was auto-created from telemetry. Discriminates
        /// engine swaps in games that report MaxRpm but not RedlineRpm
        /// (Forza family): an AE86 stock 7400-RPM and a 4AGE swap 9000-RPM
        /// have the same cylinder count but different MaxRpm bands, so
        /// signature comparison via MaxRpm catches the swap. null on
        /// legacy rows from before this field existed; auto-fills on next
        /// telemetry observation via the silent variant upgrade path.</summary>
        public int? MaxRpm { get; set; }

        /// <summary>Where this variant came from. Drives the source label
        /// in the manage-variants UI.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public CarFactSource Source { get; set; } = CarFactSource.Scanner;

        /// <summary>Community support count for this variant: a downstream copy
        /// of the consensus supporting_submissions (distinct submitters of the
        /// winning payload). Stamped only on the synthesized community variant
        /// (TrueforcePlugin.TrySynthesizeCommunityVariant); locally created
        /// variants stay 0. Read as a secondary tiebreaker after SourcePriority
        /// when picking a default variant. It does NOT drive any UI text (the
        /// "Community: X (N)" line reads the live consensus directly), and
        /// writing it confirms nothing to the backend.</summary>
        public int Confirmations { get; set; }
    }

    /// <summary>One per-gear redline entry for a variant. Gear is the forward
    /// gear number (1..N); the gear-0 / default redline is stored separately on
    /// the variant (EngineVariant.UserRedlineRpm), and the two are presented as
    /// one unified list in the UI (gear 0 = "Default (all gears)").</summary>
    public sealed class GearRedline
    {
        public int Gear { get; set; }   // forward gear: 1..N
        public int Rpm  { get; set; }
    }

    /// <summary>One cached community fetch for a (game, carId, variantSignature):
    /// the name / engine-layout / redline consensus snapshots plus when they were
    /// fetched (UTC, ISO-8601). Lets the plugin apply community car facts offline
    /// and refresh only when stale (CommunityCacheTtl) or on a manual refresh,
    /// instead of pulling on every car open. Any field may be null (no consensus
    /// for that fact type). MachineLocal: re-fetchable, not backed up.</summary>
    public sealed class CommunityFactCacheEntry
    {
        public string FetchedAtUtc { get; set; }
        public RedlineConsensus      Redline { get; set; }
        public EngineLayoutConsensus Layout  { get; set; }
        public CarNameConsensus      Name    { get; set; }
    }

    /// <summary>Per-download bookkeeping for community presets the user
    /// has imported. Lets the plugin detect when the curator publishes a
    /// new version. SeenContentVersion advances to match server when the
    /// user either applies an update or "Skip"s it; downloads start at
    /// whatever the server's current content_version was at download
    /// time.</summary>
    public sealed class DownloadedPresetRecord
    {
        public string   LocalPresetName     { get; set; }   // name we saved it under
        public string   CarId               { get; set; }
        public string   GameName            { get; set; }
        public int      SeenContentVersion  { get; set; } = 1;
        public DateTime DownloadedAt        { get; set; }
        // "car" / "game" / "engine" / "pack". Determines which server
        // table the id lives in for the update-check pass.
        public string   Kind                { get; set; } = "car";
        // Permission the original author set at upload. The pack
        // creator UI uses this to decide whether a downloaded item
        // may be re-bundled in a pack the local user is building.
        // Conservative default false matches the server default.
        public bool     AllowInPacks        { get; set; } = false;
        // Cached uploader uuid from PresetSummary.OwnerUserId at download.
        // Lets the gate tell "I downloaded my own upload" from "someone else's".
        public string   OwnerUserId         { get; set; }
        // SHA256 hex of the body AS DOWNLOADED. Auto-update only fires when
        // the user has not locally edited the body since this hash was stamped.
        public string   OriginalBodyHash    { get; set; }

        // ---- Community voting UX (rate-what-you-run) -------------------
        // Last known vote the local user cast on this item: -1 / 0 / 1.
        // Mirrors the server side so the active-card vote control can
        // render the chosen direction without a round trip. Default 0.
        public int      MyVote              { get; set; } = 0;
        // Set true once the one-time "rate it?" nudge has been shown for
        // this item (whether the user voted, downvoted, or dismissed with
        // Later). Prevents the nudge from ever returning for this item.
        public bool     PromptedForVote     { get; set; } = false;
        // Times this downloaded preset has been the active car's applied
        // preset across plugin sessions. Incremented at most once per
        // session. Gates the nudge (>= VoteNudgeMinUses).
        public int      UseCount            { get; set; } = 0;
    }

    /// <summary>Truth about a single car: chassis-level facts plus a list
    /// of engine variants. Keyed in Settings.CarFacts by "{game}/{carId}".
    /// CarName is independent of variant — same chassis carries the same
    /// human name regardless of what engine the player has swapped in.</summary>
    public sealed class CarFactsBundle
    {
        /// <summary>Human-readable name for the car. Replaces / supplements
        /// the ordinal-ID display ("Car_2267 → 1997 Mazda RX-7") in games
        /// that don't expose human names. Null = unknown; UI shows the raw
        /// carId with a "submit a name" affordance.</summary>
        public string CarName { get; set; }

        /// <summary>Engine variants known for this carId. Most cars have
        /// exactly one (Stock). Forza in-game swaps create additional
        /// variants. Empty = no engine data known yet.</summary>
        public List<EngineVariant> EngineVariants { get; set; } = new List<EngineVariant>();
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
        // NOTE: MasterGain is intentionally NOT here. Master gain is a global
        // setting (top-level TrueforceSettings.MasterGain, auto-persisted),
        // not preset-scoped. Old preset JSON may still carry a "MasterGain"
        // key; it is ignored on load.
        public float FfbScale                  { get; set; } = 0.80f;
        public bool  FfbInvertSign             { get; set; } = true;
        public float FfbSmoothTimeConstantMs   { get; set; } = 0.0f;
        public bool  FfbSpikeTamingEnabled     { get; set; } = true;
        public bool  FfbSpikeUseSlewLimiter    { get; set; } = true;
        public float FfbSpikeMaxLsbPerMs       { get; set; } = 2508.36f;
        public float FfbPeakSoftLimitLsb       { get; set; } = 2061.90f;
        public bool  DuckingEnabled            { get; set; } = true;
        public float DuckDepth                 { get; set; } = 0.60f;
        public float DuckAttackMs              { get; set; } = 5.0f;
        public float DuckReleaseMs             { get; set; } = 80.0f;
        public bool  DuckFrequencyAware        { get; set; } = false;

        // Stationary spring (parked-car centering). Per-game preset-scoped.
        // Nullable: a preset saved before this lived in the snapshot carries
        // null = "no opinion", and ApplyGamePreset leaves the user's current
        // value untouched (same migration pattern as Airborne). Values, when
        // written, mirror the top-level TrueforceSettings defaults.
        public bool?   StationarySpringEnabled   { get; set; }
        public double? StationarySpringStrength  { get; set; }
        public double? StationarySpringCutoffKmh { get; set; }

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
        // Grip/kerb trio. Null in presets saved before these effects existed;
        // apply leaves the user's current values untouched (same migration
        // pattern as Airborne below).
        public AxleSlipSettings     AxleSlip     { get; set; }
        public KerbThumpSettings    KerbThump    { get; set; }
        public LockupJudderSettings LockupJudder { get; set; }
        // Airborne ducking travels with the preset (built-in presets seed it);
        // null in presets saved before it existed, handled on apply.
        public AirborneSettings     Airborne     { get; set; }

        public Dictionary<string, CarOverride> CarOverrides { get; set; }

        // Attribution fields. Null on legacy snapshots; populated by the
        // local-save path from Settings.SharingAuthor (and on import from
        // the PresetFile/PresetPackManifest wrapper). The Preset Manager's
        // Source column reads Author + PackName for user-created presets
        // when there's no InstalledPacks sidecar entry to attribute the
        // row. Mirrors the shape on CarPresetFile.
        public string Author        { get; set; }
        public string AuthorVersion { get; set; }
        public string PackName      { get; set; }
        public string Description   { get; set; }

        /// <summary>Server uuid of the community row this game preset
        /// snapshot was downloaded from. Null = locally authored.
        /// Used by the pack creator + Share-button gate to identify
        /// the item by stable server id instead of fuzzy local name,
        /// surviving rename / duplicate / edit.</summary>
        public string CommunitySourceId { get; set; }

        /// <summary>Server uuid of the community row this user uploaded
        /// this game snapshot to (distinct from CommunitySourceId, which
        /// tracks downloads). Null = never uploaded by current user. Used
        /// by the Share button gate to detect a user-owned re-uploadable row.</summary>
        public string CommunityUploadedById { get; set; }

        /// <summary>The user uuid that owns CommunityUploadedById, stamped
        /// from AuthSignedInUserId at upload time. Lets the gate tell
        /// "I uploaded this" from "I downloaded my own upload".</summary>
        public string CommunityUploadedByUserId { get; set; }

        /// <summary>SHA256 hex of the body at last successful upload (or
        /// update). Null until first upload. Drives the "Share disabled
        /// when current body matches last upload" gate.</summary>
        public string CommunityUploadedBodyHash { get; set; }

        /// <summary>Auto-computed display version ("v1", "v2", ...) derived
        /// from the server's content_version at upload time. Null until
        /// first upload, never user-editable.</summary>
        public string CommunityUploadedVersion { get; set; }

        /// <summary>Author's "ok to re-bundle into someone else's pack"
        /// permission. Stamped from PresetSummary.AllowInPacks at download
        /// time, from the upload modal's checkbox at upload time, and
        /// preserved across export/import (this lives on the snapshot
        /// so a JSON file carries it to a recipient without needing the
        /// per-download tracker entry). Nullable: null means "no value
        /// stamped, fall back to the DownloadedCommunityPresets tracker"
        /// for legacy installs that pre-date this field.</summary>
        public bool? CommunityAllowInPacks { get; set; }
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

        /// <summary>Mask short raceOn=0 gaps (in-game replay loops, rewinds)
        /// on the forwarded copy so SimHub never sees a disconnect. Every
        /// reconnect makes ShakeIt tear down and rebuild its audio output 5 s
        /// later, cutting the shakers mid-replay. Gaps longer than ~15 s
        /// (real menu stays) still disconnect honestly. Only affects the
        /// forward path; the plugin's own pause detection always sees the
        /// game's real session state.</summary>
        public bool   ForwardGapBridge { get; set; } = true;
    }

    public sealed class PerformanceSettings
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public PerformanceMode Mode { get; set; } = PerformanceMode.Auto;

        // Trueforce stream ring depth (samples; pow-of-two; 8..64). At 4 kHz
        // each sample is 0.25 ms, so 8 = 2 ms, 64 = 16 ms.
        public int TfRingSize { get; set; } = 8;

        // Audio loopback ring depth (samples; pow-of-two; 16..128). At 4 kHz
        // each sample is 0.25 ms, so 16 = 4 ms, 128 = 32 ms. Defaults to the
        // minimum (16 = 4 ms): one WASAPI engine-period burst (~10.7 decimated
        // samples) cannot fit a smaller ring, so 8 always laps and ratchets
        // straight back to 16 (see AudioCaptureSource ring-depth notes). The
        // two-way auto-ratchet bumps it up under sustained pressure and shrinks
        // it back to 16 once the system settles. Persisted 8s from older builds
        // are clamped up to 16 on load via SanitizePow2.
        public int AudioRingSize { get; set; } = 16;
    }

    // ElectricCarMode moved to the Engine assembly with EnginePulseEffect
    // (Engine/Effects/ElectricCarMode.cs), same namespace, phase-0b move.

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

    /// <summary>Settings for the axle-slip texture: feel which axle is letting
    /// go, a high scrub as the front washes wide, a deep pulse as the rear
    /// steps out. The louder axle is the one losing grip. Off by default (new
    /// effect baseline); needs per-tire telemetry (Forza games today).
    /// PredictiveSlip starts the texture a fixed validated 150 ms before the
    /// slip fully develops; RevLockedRearPulse locks the rear pulse rate to
    /// the actual rear wheel rev rate when per-tire data allows.</summary>
    public sealed class AxleSlipSettings
    {
        public bool  Enabled            { get; set; } = false;
        public float Gain               { get; set; } = 1.0f;
        public bool  PredictiveSlip     { get; set; } = true;
        public bool  RevLockedRearPulse { get; set; } = true;
    }

    /// <summary>Settings for the kerb thump: a single firm whack the instant
    /// a wheel first touches a kerb, distinct from the rumble that follows.
    /// Scales with speed. Off by default (new effect baseline); needs kerb
    /// telemetry (Forza games today). Gain ships hot (1.6) per on-wheel
    /// validation; Freq sits below the 40 Hz gear thud so the two stay
    /// distinct.</summary>
    public sealed class KerbThumpSettings
    {
        public bool  Enabled { get; set; } = false;
        public float Gain    { get; set; } = 1.6f;
        public float Freq    { get; set; } = 30.0f;
    }

    /// <summary>Settings for the lockup judder: a flat-spot pulse while a
    /// braking tire is locked, slowing with the car the way a real flat spot
    /// would. Off by default (new effect baseline); needs per-tire telemetry
    /// (Forza games today).</summary>
    public sealed class LockupJudderSettings
    {
        public bool  Enabled { get; set; } = false;
        public float Gain    { get; set; } = 1.0f;
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
        // Fraction of MaxRpm on the percentage path. DEPRECATED:
        // kept for legacy preset deserialization + a one-shot lazy
        // migration to RedlineRpm below. New presets should leave
        // this at default (0.85) and tune RedlineRpm instead. The
        // effect's lazy migration converts Threshold-only presets to
        // RedlineRpm the first time MaxRpm telemetry is observed.
        public float Threshold  { get; set; } = 0.85f;

        // Absolute redline RPM. The unified rev-limiter knob: when set,
        // the buzz fires at this RPM (plus RedlineOffsetRpm) regardless
        // of whether the game telemetry exposes its own redline. Null =
        // fall through to telemetry redline (when sane) or to
        // MaxRpm * 0.85 as the runtime default. Replaces the old
        // Threshold-percent + Redline-mode bifurcation so Forza-family
        // and AC-family titles share one control. Migrated lazily from
        // legacy Threshold values when the user drives the car.
        public int?  RedlineRpm { get; set; } = null;

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
        // True for files exported FROM a currently-shipped factory built-in.
        // Sharing-context signal: a recipient can tell the file represents a
        // shipped baseline rather than a user creation. Mirrors
        // CarPresetFile.IsBuiltin. Doesn't affect on-disk storage in user/
        // or factory/games (those write the bare GameSettingsSnapshot, not
        // a PresetFile wrapper); only the export/import format uses this.
        public bool   IsBuiltin     { get; set; }
        // Optional sharing metadata. All four fields are user-supplied and
        // free-form; null/empty means "not provided" and the importer
        // gracefully omits them from the success dialog. PackName tags this
        // preset as part of a named pack; (Author, AuthorVersion, PackName)
        // is the pack identity used for "delete pack" / "filter by pack" /
        // "set pack default" operations. Empty PackName = loose preset.
        public string PackName      { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public GameSettingsSnapshot Snapshot { get; set; }
        // Custom firing-pattern definitions referenced by Snapshot.EnginePulse
        // .CustomEngineId. Travels with the preset so a recipient gets the
        // actual pattern data, not just a dangling Guid. Empty/null = preset
        // doesn't reference any custom engine. On import the recipient's
        // Settings.CustomEngines absorbs missing-by-Id entries (existing
        // local defs win on Id collision; see ImportPreset).
        public List<CustomEngineDef> CustomEngines { get; set; }
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
        // PackName tags this car preset as part of a named pack; same
        // semantics as PresetFile.PackName (empty = loose).
        public string PackName      { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public CarOverride Override { get; set; }
        // Custom firing-pattern definitions referenced by Override.EnginePulse
        // .CustomEngineId. Same shape and import semantics as
        // PresetFile.CustomEngines (recipient's Settings.CustomEngines
        // absorbs missing-by-Id entries; local wins on collision).
        public List<CustomEngineDef> CustomEngines { get; set; }
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
        // also carries its own Author/Description/AuthorVersion/PackName
        // when set; the pack-level fields cover the bundle as a whole.
        // PackName is the human-friendly identity of the pack (user-typed
        // on export); (Author, AuthorVersion, PackName) is the tuple a
        // later "delete pack" / "set pack as default" UI keys off.
        public string PackName      { get; set; }
        public string Author        { get; set; }
        public string Description   { get; set; }
        public string AuthorVersion { get; set; }
        public List<string> Presets { get; set; } = new List<string>();
        public List<PackedCarPreset> Cars { get; set; } = new List<PackedCarPreset>();
        // Custom firing-pattern definitions referenced by any contained
        // preset's EnginePulse.CustomEngineId. Deduped across the whole
        // pack so a pattern shared by N presets ships once. ImportPack
        // (and ImportPackSelective when its kept-items reference them)
        // merges these into the recipient's Settings.CustomEngines on
        // import; local wins on Id collision.
        public List<CustomEngineDef> CustomEngines { get; set; }
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
        public AxleSlipSettings     AxleSlip     { get; set; }
        public KerbThumpSettings    KerbThump    { get; set; }
        public LockupJudderSettings LockupJudder { get; set; }
        public AudioCaptureSettings AudioCapture { get; set; }
        public AirborneSettings     Airborne     { get; set; }

        /// <summary>Server uuid of the community row this car preset
        /// override was downloaded from. Null = locally authored.
        /// Used by the pack creator + Share-button gate to identify
        /// the item by stable server id instead of fuzzy local name,
        /// surviving rename / duplicate / edit.</summary>
        public string CommunitySourceId { get; set; }

        /// <summary>Server uuid of the community row this user uploaded
        /// this car override to (distinct from CommunitySourceId, which
        /// tracks downloads). Null = never uploaded by current user. Used
        /// by the Share button gate to detect a user-owned re-uploadable row.</summary>
        public string CommunityUploadedById { get; set; }

        /// <summary>The user uuid that owns CommunityUploadedById, stamped
        /// from AuthSignedInUserId at upload time. Lets the gate tell
        /// "I uploaded this" from "I downloaded my own upload".</summary>
        public string CommunityUploadedByUserId { get; set; }

        /// <summary>SHA256 hex of the body at last successful upload (or
        /// update). Null until first upload. Drives the "Share disabled
        /// when current body matches last upload" gate.</summary>
        public string CommunityUploadedBodyHash { get; set; }

        /// <summary>Auto-computed display version ("v1", "v2", ...) derived
        /// from the server's content_version at upload time. Null until
        /// first upload, never user-editable.</summary>
        public string CommunityUploadedVersion { get; set; }

        /// <summary>Author's "ok to re-bundle into someone else's pack"
        /// permission. Same semantics as the field on GameSettingsSnapshot /
        /// CustomEngineDef: stamped at download (from PresetSummary), at
        /// upload (from the modal checkbox), and preserved across
        /// export/import. Nullable so legacy CarOverride entries (without
        /// the field) fall back to the DownloadedCommunityPresets tracker.</summary>
        public bool? CommunityAllowInPacks { get; set; }

        public bool IsEmpty =>
            EnginePulse == null && RoadBumps == null && TractionLoss == null &&
            GearShift   == null && AbsClick  == null && AudioCapture == null &&
            PitLimiter  == null && Drs       == null && Collision    == null &&
            RevLimiter  == null && AxleSlip  == null && KerbThump    == null &&
            LockupJudder == null && Airborne == null;

        /// <summary>True when this override carries community lineage (download/
        /// upload tracking) even with no effect sections. Such an override must
        /// NOT be deleted by the store just because IsEmpty is true, or the
        /// Share gate loses the lineage. (IsEmpty stays effect-only so the
        /// in-memory dict-removal semantics elsewhere are unchanged.)</summary>
        public bool HasCommunityTracking =>
            !string.IsNullOrEmpty(CommunitySourceId)
            || !string.IsNullOrEmpty(CommunityUploadedById)
            || !string.IsNullOrEmpty(CommunityUploadedByUserId)
            || !string.IsNullOrEmpty(CommunityUploadedBodyHash)
            || !string.IsNullOrEmpty(CommunityUploadedVersion)
            || CommunityAllowInPacks.HasValue;
    }

    /// <summary>Per-local-user data partition. Holds the state that
    /// belongs to a specific Supabase account on this install, so a
    /// sign-out doesn't lose the user's data and a second user signing
    /// in on the same machine doesn't see the first user's history.
    /// Keyed in Settings.UserSlots by the immutable Supabase user-id (or
    /// "" for anonymous activity) so an email change never orphans a
    /// slot. Designed so a future cloud-sync feature can push/pull a slot
    /// to the server without changing call sites.</summary>
    public sealed class UserDataSlot
    {
        public Dictionary<string, DownloadedPresetRecord> DownloadedCommunityPresets { get; set; }
            = new Dictionary<string, DownloadedPresetRecord>();
        public string SharingAuthor { get; set; } = "";

        // Per-user OVERRIDE of game / car preset defaults. Sparse: only
        // contains entries the user explicitly set after the device-
        // wide baseline (stored on disk in the user-library folder's
        // game-defaults.json / car-defaults.json) was first established.
        // Read precedence: slot override -> device-wide file -> factory
        // seed. Effective view is rebuilt into Settings.GameDefaults /
        // CarDefaults on every slot mount.
        public Dictionary<string, string> OverrideGameDefaults { get; set; }
            = new Dictionary<string, string>();
        public Dictionary<string, string> OverrideCarDefaults { get; set; }
            = new Dictionary<string, string>();

        // Explicit "None for this car" decisions the active user made.
        // When the rebuild stacks factory + device-wide + slot-override
        // for Settings.CarDefaults, any carId listed here is removed last
        // so the user's None choice survives even when a factory built-in
        // binding exists for that car. Cleared on SwitchActiveCarPreset
        // (the user re-bound it explicitly so the suppression is gone).
        public HashSet<string> SuppressedCarDefaults { get; set; }
            = new HashSet<string>(StringComparer.Ordinal);

        // ---- Per-account profile (feel/effects + library + sync state) ----
        // A signed-in account gets its OWN settings profile and preset library, isolated from
        // other accounts on the same PC. Only the portable subset (the same one that travels to
        // the cloud) is per-account; true device/hardware config stays global on TrueforceSettings.

        // Filesystem-safe token (Supabase user_id UUID preferred) naming this account's private
        // library folder under <root>\accounts\<token>. Cached so it survives sign-out (when the
        // live AuthSession.UserId is gone). Empty for the anonymous slot (uses the shared folder).
        public string LibraryToken { get; set; } = "";
        // Latch: this account's library folder has been seeded (copied) from the shared library.
        public bool LibrarySeededV1 { get; set; } = false;
        // Latch: ProfileSettingsJson has been populated at least once (distinguishes a brand-new
        // account, which inherits the current live profile, from one with a saved profile to apply).
        public bool ProfileSeededV1 { get; set; } = false;
        // The account's saved PORTABLE settings projection (effects/gains/car facts/custom engines/
        // feature toggles), minus the community-history fields above which mount separately. Applied
        // onto live settings on switch-in, refreshed from live on switch-out.
        public string ProfileSettingsJson { get; set; } = "";
        // The account's Forza portable fields ({Enabled, Port}) as JSON, or empty.
        public string ProfileForzaJson { get; set; } = "";
        // Auto-sync opt-in follows the account (per the per-account profile choice).
        public bool AutoSyncBackupEnabled { get; set; } = false;
        // Per-account cloud-sync bookkeeping so each account syncs against its OWN baseline (no
        // cross-account contamination when two accounts share one PC).
        public string BackupLastSyncedRevision { get; set; } = "";
        public string BackupLastSyncedEnvelopeJson { get; set; } = "";
    }
}
