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
    /// <summary>What the plugin is allowed to do at all: the master switch every
    /// subsystem asks before it starts. Off means off, including the wheel's
    /// lights, which used to keep being written while "disabled".</summary>
    public enum TrueforceMasterMode
    {
        Off = 0,            // touch nothing: no stream, no capture, no wheel writes
        LightsyncOnly = 1,  // set the wheel's Lightsync patterns only: no ep3 stream, no FFB tap, no effects
        // "Normal", not "Full": this is the default and the one nearly everyone
        // runs everywhere, so it should read as the baseline rather than the top of
        // a ladder. "Full" invited "what am I missing?" in the other two.
        Normal = 2,         // everything, with the per-game switch underneath it
    }

    /// <summary>How a PC treats FFB tuning synced from a DIFFERENT wheel model.
    /// FFB always backs up regardless; this only governs whether it is APPLIED on
    /// restore/sync onto a mismatched wheel. Per-PC (tied to this device's wheel).</summary>
    public enum CrossWheelFfbMode
    {
        Ask = 0,     // show a notice with apply-anyway / dismiss (default)
        Always = 1,  // apply it, no wheel gate
        Never = 2,   // withhold it silently, no notice
    }

    public sealed class TrueforceSettings
    {
        // Master enable, RETIRED as an input on 2026-08-24 and kept as a
        // DERIVED mirror of MasterMode == Full (the RpmLedUnlocked precedent
        // below). Still written on every mode change, still backed up, still
        // read by the ep3 pause gate and the producer loop, so an older build
        // reading a newer settings file sees exactly the answer it expects:
        // Lightsync-only and Off both look like "disabled" to anything that
        // only knows the bool, which is the safe reading in both cases.
        //
        // When false, ProducerLoop skips rendering and the wheel is told to
        // return to its native FFB/Trueforce path, useful for games that ship
        // native Trueforce support (iRacing) where our ep3 stream would
        // conflict with the game's own.
        public bool PluginEnabled { get; set; } = true;

        // The real master switch. Off touches nothing at all; LightsyncOnly
        // runs the wheel's lights and nothing else (no ep3 stream, no USBPcap
        // capture, no helper exe, no telemetry sources, no effects); Full is
        // everything, with the per-game switch underneath it.
        //
        // Defaults to Full so a fresh install behaves exactly as it always
        // has. Existing installs are translated once by MasterModeMigratedV1.
        [JsonConverter(typeof(StringEnumConverter))]
        public TrueforceMasterMode MasterMode { get; set; } = TrueforceMasterMode.Normal;

        // One-shot latch for the PluginEnabled -> MasterMode translation.
        // EXCLUDED from backup: carrying "already migrated" onto a machine
        // whose settings predate the mode would make it skip its own
        // translation and inherit a default instead of the user's choice.
        public bool MasterModeMigratedV1 { get; set; } = false;

        // Drive the wheel's rev lights while Telemetry Based FFB (Mode B) is
        // on. On by default: Mode B means the game's own FFB is quiet on the
        // HID++ pipe (tap-proven, fail-closed in the gate), so the 0x807A
        // LED writes are safe. A toggle exists because Mode B will reach
        // games that drive the wheel's rev lights natively; there the user
        // turns ours off. This is now the ONLY rim-LED gate: the second one
        // rode an external iRacing FFB handoff that has been removed, and
        // native iRacing telemetry FFB will bring its own gate when it lands.
        // (Replaces RpmLedsEnabled, retired 2026-08-01: one master switch
        // labeled "iRacing" silently gated the Mode B lights too, and a
        // stored false darkened the wheel with no visible cause.)
        public bool ModeBRevLightsEnabled { get; set; } = true;

        // Per-car rev-light pattern picks: "{game}/{carId}" -> effect 1-9
        // (1-4 built-in patterns, 2 = outside-in; 5-9 the wheel's custom
        // slots), same key shape as CarFactsSelection. ABSENCE of a key =
        // the wheelbase's own selection, which is the only "default" there
        // is: a global tier existed for a few hours on dev (RevLightEffect,
        // removed 2026-08-12) and was cut because the wheelbase's own
        // selector already IS the every-car choice, and a second one only
        // added a concept and a resolver for the pickers to disagree over.
        // We only ever SELECT a pattern the wheel already stores (fn3 =
        // SET_EFFECT); the wheel's own selection is snapshotted at arm and
        // put back when the lights release, so nothing here is permanent on
        // the wheel. A preference, not a CarFact: custom slots 5-9 name
        // THIS user's wheelbase slots, which mean nothing on another user's
        // wheel, so this never travels in presets or community data.
        // Collection must default EMPTY (the settings loader appends).
        // Legacy 0 values (from the removed tier's testing day) read as
        // "wheelbase's selection" and are dropped on the next write.
        public Dictionary<string, int> CarRevLightEffect { get; set; }
            = new Dictionary<string, int>();

        // Per-car choice of one of OUR light patterns, keyed "game/carId" to the
        // pattern's id. Separate from CarRevLightEffect above rather than
        // overloading its int: the two name different things (a wheel effect
        // number versus a pattern in our library), and keeping them apart means
        // every existing remembered effect keeps working untouched. A car with an
        // entry here wins, since choosing one of our patterns is the more
        // specific act. Same rule as above: default EMPTY, never null, because
        // the settings loader appends onto whatever is here.
        public Dictionary<string, string> CarLightPattern { get; set; }
            = new Dictionary<string, string>();

        // Drive the Dynamic OLED on the wheel's base (G PRO / RS50 only) while
        // Telemetry Based FFB (Mode B) is on. Same pipe and therefore the same
        // rule as the rev lights above: a non-force write to HID++ cuts any
        // force flowing there, so this shares the Mode B + tap-proven-quiet
        // gate exactly.
        //
        // Default ON, like the rev lights. It was off while the protocol was
        // known only from third-party RS50 captures; since then a G PRO has
        // been confirmed to answer feature 0x8130, the gate has been proven
        // necessary AND sufficient on hardware, and the panel is handed back
        // whenever the gate closes, so the wheel's own menu is never lost.
        public bool ModeBOledEnabled { get; set; } = true;

        // Which arrangement the OLED shows. The firmware owns font size and
        // alignment, so picking a screen IS picking how big each value is
        // drawn. See OledScreen; Custom uses the three fields below.
        // Gear over speed, both labelled, on the centered four rows. The owner
        // built the pair by hand in the editor and settled on this order after
        // running them (2026-08-10): every value is named, nothing is cropped,
        // and it copes with a gearbox counting past 9.
        public OledScreen OledScreen { get; set; } = OledScreen.GearOverSpeed;

        // A user-built screen: which firmware layout, and which field goes in
        // each of its slots, with the text for any slot set to Custom.
        //
        // Both lists MUST default EMPTY for the same loader reason as
        // DashDriveSlots: SimHub's bare serializer APPENDS a stored array onto
        // a non-empty initializer. Empty means "nothing chosen yet", and
        // OledScreenModel.Sanitize* fills a short or unknown list with Empty
        // slots, so a layout change needs no migration.
        public OledLayoutKind OledCustomLayout { get; set; } = OledLayoutKind.FourCenter;
        public List<string> OledCustomSlots { get; set; } = new List<string>();
        public List<string> OledCustomTexts { get; set; } = new List<string>();

        // Show the OLED speed in mph instead of km/h. Local to the display
        // only; nothing else in the plugin changes units.
        public bool OledUseMph { get; set; } = true;

        // Flash the gear on the OLED for a moment when you shift. On by
        // default and self-suppressing: it never fires on a screen that already
        // shows the gear, which includes the default one, so turning it on
        // costs nothing until you pick a screen where the gear is not visible.
        public bool OledShiftFlash { get; set; } = true;

        // Which of the two shift flashes to draw. Centered is the default: the
        // wheel cannot center its largest font, so the big one sits off to one
        // side, and a flash you read at a glance is better centered than large.
        public OledFlashStyle OledShiftFlashStyle { get; set; } = OledFlashStyle.CenteredGear;

        // Minimum gap between writes to the OLED, in milliseconds. 20 = 50 Hz.
        // Lower is smoother and costs more of the shared HID++ pipe; the pipe
        // is force-free whenever this feature is allowed to run, so the old
        // 200 ms, and the 100 ms after it, were caution rather than a measured
        // limit. 20 is the owner's own setting, run on a G PRO (2026-08-10);
        // it is unverified on an RS50. Tunable live with the OLEDMS access
        // code so the real ceiling can be found on other hardware.
        public int OledWriteIntervalMs { get; set; } = 20;

        // A greeting the first time the screen comes up in a SimHub run: the
        // plugin name on the small row and this scrolling under it. Once per
        // run, not per session, so it stays a hello rather than a habit.
        public bool OledGreetingEnabled { get; set; } = true;
        public string OledGreetingText { get; set; } = "HELLO WORLD";

        // TEST OVERRIDE (access code OLEDANY). Runs the OLED regardless of the
        // Mode B + quiet-FFB gate, so someone can find out whether a screen
        // write actually disturbs a game's own HID++ force the way an LED write
        // does. That has never been tested for this feature: the restriction is
        // inherited from the rev lights. If it turns out to be harmless the
        // screen could work in every game, which is why the question is worth a
        // deliberately unsafe switch. Off by default and undocumented.
        public bool OledIgnoreModeBGate { get; set; } = false;

        // Take the OLED over for a few seconds when a lap finishes: the time
        // alone for a personal best, the time over the delta otherwise.
        public bool OledLapResult { get; set; } = true;

        // Retired unlock for the old rim-LED settings section. The section is
        // permanently hidden since 2026-08-01 and the external FFB handoff it
        // configured has been removed. The property stays only so old settings
        // files and backups deserialize cleanly.
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

        // Telemetry based FFB: per-VARIANT grip-limit auto-calibration state
        // (GripPeakLearner), keyed "GameName|CarId|VariantSignature". The
        // signature is ENGINE-derived (cylinders + banded max rpm + banded
        // redline), so a tune that changes the engine stores separately, but
        // a tire-only (or aero / suspension) tune keeps the same slot: the
        // learner re-converges in place (rises within a lap of pushing, falls
        // over a few minutes of cornering time), and the RESETGRIP access
        // code wipes the active slot on demand. Written as the player drives
        // (where each variant's combined-slip metric actually tops out plus
        // how much near-limit seat time backs that estimate) so the next
        // session starts calibrated. Zero user action; the grip auto-cal
        // checkbox gates application, not learning persistence.
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

        // One-time migration latch (2026-07 engine centralization). Flips
        // true after per-car engine picks (CarOverrides[..].EnginePulse
        // Layout / CustomEngineId) have been relocated into the matching
        // car's variant UserEngineLayout pin in CarFacts. Game-preset-scoped
        // picks are dropped deliberately: they aren't car-bound. Migrate-once
        // + idempotent.
        public bool EngineChoiceMovedToCarFactsV1 { get; set; } = false;

        // One-time cleanup latch (follow-up to the relocation above): car
        // overrides whose ONLY content was the engine pick get their
        // EnginePulse section dropped, clearing the stray "overridden"
        // Engine badge and unfreezing feel. Migrate-once + idempotent.
        public bool EngineOnlyOverridesPrunedV1 { get; set; } = false;

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

        // Community backend settings. ON BY DEFAULT (owner decision
        // 2026-07-16): car facts are trivial non-personal data (redlines,
        // engine types, car names), so the posture is opt-out with
        // disclosure, not a consent ceremony. The networked welcome modal is
        // the disclosure moment (it says sharing is on and where the off
        // switch lives); PRIVACY.md documents what is sent. Existing
        // installs whose settings file carries the old explicit false get a
        // one-time welcome re-pitch (CommunityDefaultOnRepitchedV1) because
        // the old pitch required an account and this one doesn't.
        //
        // Car facts are account-FREE (since 0100): no username is ever shown
        // to other users, and no account is needed to contribute. Signed-in
        // submissions still carry the user token so the server keys them to
        // the account (achievements, moderation, export/delete); signed-out
        // submissions fall back to CarFactsAnonId, a client-minted random
        // GUID prefixed 'anon:' server-side, rate-limited per id AND per
        // hashed client IP. Consensus reads work on the plain anon key.
        // Account features (preset browser/sharing, votes, backup) still
        // require sign-in.
        //
        // Backend URL + anon key are blank by default. For release builds
        // they get baked in as CommunityClient constants and the toggle is
        // the only switch users see. For dev builds the user can override
        // via these fields to point at a staging project.
        public bool   CommunityEnabled         { get; set; } = true;

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

        // Shorter TTL for a PARTIAL fetch (at least one fact type came back
        // empty). Empty is ambiguous: the community may genuinely have no
        // redline for that car, or that one call may have failed while its
        // siblings succeeded. The full TTL would freeze a failed call's result
        // as fact for a week, so the car would keep running on its estimated
        // redline (wrong rev lights, wrong buzz) with no way to notice.
        // Retrying in hours costs one request and covers both cases.
        [JsonIgnore]
        public static readonly TimeSpan CommunityCachePartialTtl = TimeSpan.FromHours(3);

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

        // Local date ("yyyy-MM-dd") a MOTD support message last appeared. Support
        // draws on this cooldown instead of the shared nag one, so the money ask
        // keeps a steady rate no matter how many other nags exist. Showing one
        // also stamps MotdLastNagOn, so a day still carries at most one nag.
        // EXCLUDED from backup. null = none yet.
        public string MotdLastSupportNagOn { get; set; }

        // ---- support prompt (the periodic Patreon modal on the plugin page) ----

        // Latched true the first time the backend confirms this account has ever
        // backed the project (is_supporter now, or a non-null supporter_since from
        // any past membership). Never cleared: supporting once retires the prompt
        // for good, and the latch keeps that true while offline or signed out.
        // PORTABLE: a fact about the person, not the machine.
        public bool HasEverSupported { get; set; }

        // How many times the support prompt has been shown, which position on the
        // seat-time ladder we're at, and when it last appeared (UTC, for the
        // real-time floor). Consecutive declines push the next one further out;
        // clicking through to Patreon resets that. All EXCLUDED from backup.
        public int       SupportPromptCount { get; set; }
        public int       SupportPromptDeclineCount { get; set; }
        public DateTime? SupportPromptLastUtc { get; set; }

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

        // Opt-in "Beta" update channel, open to everyone. When on, the in-app
        // updater treats GitHub prereleases as eligible upgrade targets, so
        // testers get pre-release builds through the same "Update to vX.Y.Z"
        // button as stable. Default off (betas are less tested; the toggle
        // confirms that on the way in). Prereleases stay public on GitHub
        // regardless; this only drives the in-app delivery.
        public bool   BetaUpdatesEnabled { get; set; } = false;

        // One-shot latch for the beta-build auto-enroll (X.Y.Z of the build that
        // was acknowledged). Running a build matching a GitHub prerelease flips
        // BetaUpdatesEnabled on automatically once per version and records it
        // here; toggling beta off afterwards sticks (no re-enroll) and instead
        // surfaces the switch-back-to-main offer.
        // Per-install state; excluded from backup.
        public string BetaAutoEnrolledVersion { get; set; }

        // Opt-in standing consent: once on, the user's car-fact corrections
        // (redline, engine layout, car name) are submitted to the community
        // without per-edit prompts. Default ON (same opt-out posture as
        // CommunityEnabled; the welcome modal is the disclosure); the
        // Settings checkbox withdraws it. Submissions never show a username,
        // need no account, and are gated by CommunityEnabled; car names
        // additionally keep their confirm/correct modal because a name is
        // editorial, not measured.
        public bool   AutoSubmitCarFacts { get; set; } = true;

        // Latch for the "share car data with the community?" consent modal
        // (CarFactsConsentGate). With sharing on by default the modal is a
        // residual path: it only fires for upgraders who had community ON
        // but sharing OFF in their stored settings (they answered a stricter
        // pitch, so their next fact-worthy save asks once). True once
        // answered either way; explicit Share/Confirm clicks may re-offer.
        public bool   CarFactsConsentAsked { get; set; } = false;

        // Fallback submitter identity for SIGNED-OUT car-fact submissions: a
        // random GUID minted when consent is granted, sent as
        // submit_car_fact's p_anon_id so consensus can count distinct
        // contributors without requiring an account. Ignored server-side
        // when a user token rides the request (signed-in submissions are
        // keyed to the account for achievements). Never derived from
        // hardware. Travels in backups so one human stays one contributor
        // across PCs.
        public string CarFactsAnonId { get; set; } = "";

        // One-time latch for the community-default flip: existing installs
        // whose settings file carries CommunityEnabled=false from the old
        // opt-in default get the networked welcome re-shown once (the old
        // pitch required an account; the new proceed doesn't). Fresh
        // installs never need the reset (community already on).
        public bool   CommunityDefaultOnRepitchedV1 { get; set; } = false;

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

        // Show the per-gear redline editor in the Car facts panel. Default off:
        // community data showed nobody shares per-gear values, so the editor is
        // opt-in clutter control. UI-only; saved per-gear values keep applying
        // to the wheel, and a variant that has them shows the editor regardless
        // (see RebuildPerGearEditors) so stored data is never invisibly active.
        public bool ShowPerGearRedlineEditor { get; set; } = false;

        // TF4ALL Remote dash: rev-strip fill direction. false = left to right;
        // true = outside-in (default), both ends lighting first and converging
        // on the center, which is what the wheel's own rev lights do and what
        // most cars with center-converge shift lights do.
        // Surfaced in the Settings tab's "Remote dashboard" section; the dash
        // reads it live (Dash.RevOutsideIn) so a change applies instantly.
        //
        // Read only when DashRevStripAuto is off, or when it is on and there is
        // no wheel to follow. Kept as its own field rather than folded into a
        // three-way mode so turning Auto off returns the user to the direction
        // they had picked instead of a default.
        public bool DashRevStripOutsideIn { get; set; } = true;

        // TF4ALL Remote dash: let the rev strip follow the wheel instead of
        // drawing its own. On (default) the strip takes its colors, its fill
        // direction and its switch-on points from the wheel's live rev lights
        // (Dash.Lights.*), so the phone shows the rim, per-car published light
        // data included. Falls back to the strip's own green-amber-red ramp on
        // any rig with no level-capable Logitech wheel, so this costs nothing to
        // leave on.
        public bool DashRevStripAuto { get; set; } = true;

        // TF4ALL Remote dash: which tab the dash opens on when SimHub starts.
        // Remember-last wins while on (the dash reopens where it was left,
        // surviving restarts); with it off, DashDefaultTab is the fixed
        // opening tab. DashLastTab is bookkeeping, not a user choice: written
        // on every tab tap, read only when DashRememberLastTab is true.
        // These are SCREEN indices, not positions in the bar: 0=Gains,
        // 1=Car facts, 2=Effects, 3=Presets, 4=Visualizer, 5=Tele-FFB,
        // 6=Drive (clamped at read for forward compat). Both default to
        // Drive, which leads the factory order and is the screen the dash
        // exists for; 0 would open on Gains, which now ships switched off.
        public bool DashRememberLastTab { get; set; } = true;
        public int  DashDefaultTab      { get; set; } = 6;
        public int  DashLastTab         { get; set; } = 6;

        // TF4ALL Dash "Drive" screen: what each of the four corner boxes shows,
        // in slot order (top-left, top-right, bottom-left, bottom-right), plus
        // whether both rows are used. Values are content keys from
        // TrueforcePlugin.DashDriveContentKeys; an unknown or missing entry
        // falls back to that slot's factory default, so an empty list means
        // "the shipped Forza-friendly layout" and a content type added by an
        // update needs no migration.
        // MUST default EMPTY for the same loader reason as DashTabOrder above.
        // Defaults (Forza-first, since that is where Telemetry FFB runs):
        // car facts, lap times, the FFB scope, tyre temps. Standings are
        // deliberately absent: SimHub's leaderboard item is obsolete and needs
        // map/coordinate data no Forza title provides.
        // TwoRows false uses only the bottom pair, which is the phone layout
        // (the bottom boxes grow to full height); true is the tablet layout.
        public List<string> DashDriveSlots { get; set; } = new List<string>();
        public bool DashDriveTwoRows { get; set; } = true;

        // Per-game Drive-tab layouts. The boxes read game telemetry and the
        // games disagree wildly about what they report, so a layout that fills
        // up in one title is half "this game does not report it" in the next.
        //
        // Off is the old single-layout behaviour and stays the default: this
        // only earns its keep for someone who plays several titles, and a
        // setting that silently splits one layout into many is a bad surprise
        // for everyone else.
        //
        // On, a change made while a game is running is stored against that
        // game, and DashDriveSlots above keeps its job as the layout for every
        // game with no entry of its own (which is also where a change made
        // with no game running still goes). So turning this on changes nothing
        // until you actually re-pick a box, and turning it off again returns
        // you to the shared layout with the per-game ones kept.
        //
        // Keyed by SimHub GameName, like GameEnabled and GameDefaults.
        // MUST default EMPTY for the same loader reason as DashDriveSlots.
        public bool DashDriveSlotsPerGame { get; set; } = true;
        public Dictionary<string, List<string>> DashDriveSlotsByGame { get; set; }
            = new Dictionary<string, List<string>>();

        // What each game has been SEEN to report, so the box picker can grey
        // out a box the game cannot fill without anyone hand-listing every
        // title. Keyed by SimHub GameName; the value is the comma-wrapped set
        // of content keys that have arrived at least once, and it only ever
        // grows.
        //
        // Positive evidence only. "Seen once" is proof a game reports
        // something; "not seen yet" is proof of nothing, which is why the
        // seconds below exist: a box is greyed out only after that game has
        // been DRIVEN for long enough that never having seen the value means
        // something. Parked time does not count, or a session spent sitting
        // in the pit box would teach us the game has no lap timing.
        //
        // Deliberately not the whole list of boxes. Damage cannot be learned
        // (SimHub reports zero damage and no damage identically), and the two
        // car-list boxes cannot either, since a driver who only ever hotlaps
        // alone would otherwise teach us their game has no opponents. Those
        // stay with the hand-written table in the plugin.
        // MUST default EMPTY for the same loader reason as DashDriveSlots.
        public Dictionary<string, string> DashDriveSeen { get; set; }
            = new Dictionary<string, string>();
        public Dictionary<string, int> DashDriveDrivenSec { get; set; }
            = new Dictionary<string, int>();

        // TF4ALL Dash: show a colored race-flag band across the top of
        // whichever screen is open. Reads SimHub's own flag properties, so it
        // only ever lights up in games that report flags (the Forza titles
        // report none, which is why this is opt-in rather than always on).
        // On by default: a flag is the one thing on a dash you cannot afford
        // to miss, and in a game that reports none the band simply never
        // appears, so it costs those players nothing.
        public bool DashFlagsEnabled { get; set; } = true;

        // TF4ALL Dash rev strip, DRIVE TAB ONLY: true (default) narrows it to
        // the space above the gear between the two box columns, false spans
        // the full width. Every other screen is always full width, because
        // the middle of their header row is where the title and car name
        // live and there is nothing to narrow to.
        public bool DashRevStripCentered { get; set; } = true;

        // TF4ALL Dash Drive tab: thin throttle and brake bars either side of
        // the gear with a steering indicator beneath it. Uses space the gear
        // column has spare, and is independent of the Inputs box, which shows
        // the same three in a card.
        public bool DashDrivePedals { get; set; } = true;

        // TF4ALL Dash spotter: a bar down the edge of whichever tab is open
        // when a car is alongside on that side. SimHub works this out from
        // the session's opponents, so it lights in games that report car
        // positions and simply never appears in the ones that do not, the
        // Forza titles included.
        public bool DashSpotterEnabled { get; set; } = true;

        // TF4ALL Dash incident points: the running count under the speed on the
        // Drive tab, a band across the top each time it moves, and the same
        // announcement on the wheel base's OLED. iRacing only, because it is
        // the only title that publishes a count; everywhere else the readout
        // and the band simply never appear.
        // On by default for the same reason the flag band is: it costs players
        // of every other game nothing, and an incident you did not notice is
        // the one that ends your race.
        public bool DashIncidentsEnabled { get; set; } = true;

        // TF4ALL Dash idle mode: a full-screen card over whatever tab is open
        // once the car has been sitting still, showing an ambient animation,
        // the driver's name and number, and the plugin's own status. Drawn as
        // an overlay rather than a screen of its own so there is nothing to
        // get stuck in: any sign of driving clears it.
        public bool   DashIdleEnabled      { get; set; } = true;
        // ONLY the game-is-running-but-parked case. With no game running the
        // card shows at once, because then there is no dashboard to show and
        // the card is the screen. Defaults long: sitting in a pit box or a
        // menu is not a reason to take the dashboard away.
        public int    DashIdleDelaySeconds { get; set; } = 600;
        // Which built-in ambient animation. Custom images and video are a
        // later pass: the dashboard file is rebuilt on every update, so user
        // media has to live somewhere the rebuild cannot reach.
        public string DashIdleStyle        { get; set; } = "Pipes";
        public string DashIdleDriverName   { get; set; } = "";
        public string DashIdleNumber       { get; set; } = "";
        public string DashIdleColor        { get; set; } = "#FFF2F4F8";
        // Name above the number rather than under it. Both read fine; which
        // one looks right depends on the number, so it is a choice.
        public bool   DashIdleNameAbove    { get; set; } = false;
        // Font family for the idle card's name and number. Empty means the
        // dashboard's own. These are names the VIEWING device has to have,
        // so the picker only offers families that ship broadly; an unknown
        // one falls back silently rather than failing.
        public string DashIdleFont        { get; set; } = "";

        // Dashboard theme, by name. Themes are PALETTES: the layout is the
        // same whichever is picked, because color binds live and geometry
        // does not. Unknown names fall back to the first theme rather than
        // leaving the dash unpainted.
        public string DashTheme { get; set; } = "Neon";

        // TF4ALL Dash tab layout. DashTabOrder holds SCREEN indices (0=Drive,
        // 1=Car facts, 2=Effects, 3=Presets, 4=Visualizer, 5=Tele-FFB) in the
        // user's display order; DashTabsDisabled hides tabs without losing
        // their position. Sanitized at read (GetDashTabFullOrder): unknown
        // indices drop, missing ones append in factory order, so an empty
        // list means factory order and a tab added by an update shows up
        // enabled for everyone, customized layouts included.
        // MUST default EMPTY: SimHub's settings loader deserializes with a
        // bare JsonSerializer (ObjectCreationHandling.Auto), which APPENDS
        // the stored array onto a pre-populated initializer list instead of
        // replacing it; a non-empty default here made every restart prepend
        // the factory order and silently revert the user's layout.
        // At least one tab always stays enabled: the Settings editor blocks
        // disabling the last one and the reader falls back to Drive if a
        // hand-edited file disables everything.
        public List<int> DashTabOrder     { get; set; } = new List<int>();
        public List<int> DashTabsDisabled { get; set; } = new List<int>();

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

        // Classic-spring emulation: for games that command their force
        // feedback as a parametric spring on the classic Logitech protocol
        // (Farming Simulator 25; the wheel firmware normally renders it, but
        // not while in Trueforce mode). The plugin evaluates the game's
        // spring against the wheel's physical position instead. Default on
        // (owner call 2026-08-07): arming requires spring-only FFB on the
        // bus, so games with streamed FFB never see it.
        public bool ClassicSpringEmulationEnabled { get; set; } = true;

        // Spring-mode enhancements, each its own toggle so they can be
        // hardware-tested one at a time. Terrain feel: ground roughness from
        // the game's rigid-body physics (FS25 vehicleComponents), high-passed
        // into steering kicks on top of the emulated spring. Default off
        // until validated on hardware.
        // Factory = the owner's tuned FS recipe (2026-08-09): enhancements
        // ship ON. Changes here require a ModeBDefaultsGeneration bump plus
        // an entry in PreviousShippedModeBRecipes (the spring fields joined
        // the per-field defaults merge with generation 6).
        public bool   SpringModeTerrainEnabled { get; set; } = true;
        public double SpringModeTerrainGain    { get; set; } = 1.0;

        // Further spring-mode enhancements, one toggle each (owner directive:
        // testable one at a time). Implement drag: engine load weights the
        // steering (working the field feels different from driving to it).
        // Cornering weight: yaw-rate-derived lateral load scales the spring
        // (chassis dynamics from the game mod). Bump haptics has no toggle:
        // the FS source always pulses OnRumbleStrip on hard suspension
        // transients, and the Kerb thump effect's own Enabled is the control.
        // Centering strength for the synthetic spring (multiplier on the
        // built-in curve). 0 = spring fully off, the empirical answer to
        // "do we even need one": damper, terrain and effects keep running.
        public double SpringModeCenterGain           { get; set; } = 1.0;
        // On-center firmness: how linear the curve is near straight-ahead
        // (0 = soft quadratic center, 1 = crisp near-linear). Separate from
        // strength because raising strength alone also heavies the edges.
        public double SpringModeCenterFirmness       { get; set; } = 0.85;
        // How much speed strengthens the centering (0 = constant at all
        // speeds, 1 = fully speed-scaled with a limp standstill).
        public double SpringModeSpeedEffect          { get; set; } = 0.70;
        // Overall spring-mode force multiplier (spring + terrain + drag +
        // cornering weight; damping unaffected), the FS counterpart of the
        // Forza Strength slider. 0.80 is the owner's own G PRO setting
        // (2026-08-10); the G923 holds the older 1.0 in ApplyWheelDefaults,
        // because the belt motor needs the headroom the direct drive does not.
        public double SpringModeStrength             { get; set; } = 0.80;
        // FS's own min-force floor, separate from ModeBMinForce (owner call
        // 2026-08-09: games' force characters differ, so the floor is per
        // game). Lifts terrain kicks and the spring beyond slight deflection
        // above a belt wheel's internal friction; speed-gated so a parked
        // wheel stays limp, deflection-gated so straight-ahead can't buzz.
        public double SpringModeMinForce             { get; set; } = 0.0;
        public bool   SpringModeDragEnabled          { get; set; } = true;
        public double SpringModeDragGain             { get; set; } = 1.0;
        // How much of the drag weight plain engine strain applies while no
        // implement is working (0 = implements only, 1 = strain counts in
        // full, same as working). Only meaningful with mod >= 0.2.6; older
        // mods can't tell the two apart and always apply full weight.
        public double SpringModeDragStrainFraction   { get; set; } = 0.50;
        public bool   SpringModeChassisWeightEnabled { get; set; } = true;
        public double SpringModeChassisWeightGain    { get; set; } = 1.0;

        // TF4ALL Enhanced Telemetry game-mod install state (Farming Simulator).
        // Machine-local: the mod lives in THIS PC's game folders. Declined
        // silences only the first-run dialog; the Telemetry FFB tab banner
        // keeps offering. Versions are per game generation (FS22/FS25 have
        // separate installs); MUST default empty (SimHub's bare-serializer
        // load appends stored entries onto non-empty initializers).
        public bool FsModInstallDeclined { get; set; }
        public Dictionary<string, string> FsModInstalledVersions { get; set; }
            = new Dictionary<string, string>();
        // 1.0 = full felt scale at full lock when parked; slider allows up to
        // 2.0 for headroom, though past ~1/FfbScale the ±full-scale clamp /
        // motor ceiling caps it (you can't exceed the wheel's max torque).
        // Default 0.5 (owner's preferred general feel once the spring is driven
        // by the wheel's physical steering position).
        public double StationarySpringStrength  { get; set; } = 0.5;
        public double StationarySpringCutoffKmh { get; set; } = 12.0;  // spring fully gone at/above this speed

        // FFB spike taming: tames AC's over-the-top kerb / collision FFB so
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

        // Hand the wheel back to the game while paused (NOLOCK access code +
        // the checkbox under Force feedback (advanced)). When on, the plugin
        // fully leaves Trueforce mode while the game is paused (SendStopCommand
        // + Pause) so the wheel reverts to its native FFB, e.g. Forza's own
        // auto-center, instead of us streaming a substitute force. This is
        // what stops the G923/FH6 pause full-lock (issue #13). Stable shipped
        // it DEFAULT-ON in v0.1.24; the 0.2.x line left it opt-in by
        // oversight, silently regressing upgraders, so 0.2.7 restores the
        // shipped default (owner call 2026-08-15). The checkbox stays as the
        // escape hatch for anyone who prefers the plugin to keep the wheel.
        public bool  StopStreamOnPause        { get; set; } = true;

        // One-time default repair marker for the above: beta-era settings
        // files carry a stored false nobody chose (the regressed default), so
        // the first 0.2.7 launch flips StopStreamOnPause on once. A user who
        // turns it off afterward stays off.
        public bool  StopStreamOnPauseMigrated { get; set; } = false;

        // Release the wheel when the game is no longer the foreground window.
        // Sibling of StopStreamOnPause, for the games its test cannot reach.
        // That one needs an authoritative session flag or telemetry to stop; a
        // title that keeps its car idling in the background satisfies neither,
        // so alt-tabbing out of Wreckfest left the tap's last captured force
        // streaming for its full 10 s hold with no self-aligning torque to
        // oppose it, and the wheel walked to its rotational stop (owner,
        // 2026-08-16). Same full-lock class as issue #13, different trigger.
        public bool  ReleaseForceOnFocusLoss  { get; set; } = true;

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

        // Tap-free AC force feedback (ACFFB access code): while driving
        // Assetto Corsa, re-inject the game's own final FFB value (finalFF,
        // read from AC's physics shared memory at 1 kHz) into the Trueforce
        // stream instead of decoding the wire with the USBPcap tap. finalFF
        // is AC's post-gain output signal, so in-game FFB must stay ON for
        // it to carry force. Global, not per-preset: it selects a capture
        // path, not a tuning. Off = shipped behaviour (pcap tap only).
        public bool   AcShmFfbEnabled            { get; set; } = false;

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
        // game's own FFB. It REPLACES the game's force, so it is strictly
        // OPT-IN, PER GAME, and never forced on: enabled from the Telemetry
        // Based FFB tab for whichever game is active. Keyed by SimHub GameName;
        // a missing key = off. Only games with enough telemetry are offered
        // (IsModeBCapableGame: the Forza titles FM8 / FH4 / FH5 / FH6, run with
        // the game's own force feedback and vibration at 0). The tuning below
        // is global (shared across games); defaults are the owner's G PRO
        // recipe as dialed in on the wheel (re-snapshot 2026-08-01 evening
        // from the live wheel after the drift sessions, values rounded to
        // what the sliders display; strength is the one per-wheel value,
        // see ApplyWheelDefaults). ----
        public Dictionary<string, bool> ModeBGameEnabled { get; set; }
            = new Dictionary<string, bool>();
        public float ModeBSatGain   { get; set; } = 0.50f; // peak torque fraction; the G PRO default (owner 2026-08-01). RS50/G923 get their own via ApplyWheelDefaults.
        // Strength for the iRacing RESHAPE path, deliberately its own field
        // rather than sharing ModeBSatGain. That one is a peak-torque fraction
        // for the synthesis model, defaults to 0.50 and is tuned per wheel; the
        // reshape path is already normalized by the sim's own
        // SteeringWheelMaxForceNm, so its honest default is 1.0, meaning
        // "deliver exactly the torque iRacing asked for". Wheel-independent for
        // the same reason: the driver's in-sim max force setting already
        // encodes their wheel.
        public float IRacingForceGain { get; set; } = 1.0f;

        // Full scale in Nm: the torque at which the wheel is asked for
        // everything it has. 0 means "ask iRacing", which is the default and is
        // usually right, because iRacing's own max-force setting is exactly this
        // number and the driver already tuned it.
        //
        // An override exists because relying on it silently is confusing: that
        // setting lives inside a sim whose force feedback the user has just been
        // told to switch off, so it looks like a dead knob controlling a live
        // one. Anybody who would rather state their wheel's rating here and
        // forget iRacing's menu can, and anybody whose iRacing value is nonsense
        // is no longer stuck with it.
        public float IRacingMaxForceNmOverride { get; set; } = 0.0f;

        // Where full scale comes from, which decides whether cars keep their
        // relative weight. The mechanism is one line of arithmetic:
        //
        //     force = torque / divisor
        //
        // A divisor that VARIES PER CAR and equals that car's own peak makes
        // every car arrive at full force at its own limit: 10/10 and 20/20 both
        // reach 1, so the cars are flattened BY DEFINITION. A divisor that is
        // the SAME for every car preserves the ratio: 10/D against 20/D is still
        // 2:1, so a heavy car really does push harder.
        //
        // Note this depends only on whether the divisor varies, NOT on where it
        // came from. A per-car number flattens whether it was learned or read
        // out of iRacing, which is easy to get backwards when naming these.
        // (A planned mode-enum for this taxonomy was removed unread in 0.2.7;
        // the per-car flag below plus the override above ARE the mechanism, and
        // a stable-reference "relative" mode is a later-cycle design.)

        // Keep a separate Max force per car, the way iRacing itself does.
        //
        // This single switch is what decides whether cars keep their relative
        // weight, and it decides it for the reason the arithmetic above gives:
        //   OFF, one shared number is the divisor for everything, so a car that
        //        makes twice the torque pushes twice as hard. Set it from your
        //        HEAVIEST car and nothing clips anywhere, while lighter cars sit
        //        honestly below it.
        //   ON,  each car gets its own, so every car reaches full force at its
        //        own limit. Nothing clips, nothing feels weak, and nothing is
        //        distinguishable either.
        // Off by default: keeping cars distinct is the behaviour people expect
        // when they have not asked for anything, and flattening is the opinion.
        // RETIRED as a choice (owner, 2026-08-15): forced true at load, and the
        // checkbox is gone. With nothing set both positions behaved identically
        // (they fall through to iRacing's own per-car number), so it only ever
        // decided where a typed number or an Auto press LANDED, while its copy
        // sold it as deciding whether cars feel different. Off it was also a
        // trap: Auto in a light car wrote the shared number and every heavier
        // car then clipped. Per car matches how iRacing itself stores max
        // force. The field stays so the resolution order below still reads
        // per-car slot, then the legacy shared override for cars never tuned,
        // then the sim's own figure.
        public bool IRacingMaxForcePerCar { get; set; } = false;

        // Per-car Max force in Nm, keyed by iRacing CarPath. Written by the Auto
        // button, one car at a time, exactly like iRacing's own. Empty default
        // matters: the settings loader APPENDS onto collections rather than
        // replacing them, so a non-empty initializer would accumulate.
        public Dictionary<string, float> IRacingMaxForceByCar { get; set; }
            = new Dictionary<string, float>();
        // Use iRacing's 360 Hz sub-tick torque as SLOPE, projecting the newest
        // value forward between frames instead of holding it.
        //
        // This is not the earlier interpolation, which replayed the six samples
        // from oldest to newest and so ran a full frame behind. That is pure
        // phase lag inside a loop that closes through the sim (wheel position
        // in, steering torque out), and it made the wheel oscillate at a
        // standstill with the swings growing. Projecting forward instead is
        // phase LEAD, which settles such a loop rather than upsetting it, and
        // it still uses all six samples: they supply the trend.
        //
        // Bounded on purpose (never more than one frame ahead, never more than
        // 15 percent of full scale away from the measured value), because an
        // unbounded lead term is its own instability.
        public bool IRacingUse360Hz { get; set; } = true;

        // Which of the two ways of turning iRacing's 360 Hz torque into a 1 kHz
        // stream is in use. Both render all six sub-samples; they differ in what
        // they do about the fact that those samples describe the frame that just
        // ENDED, so replaying them faithfully is inherently a frame behind.
        //
        //   0 = Lead.   Fit a line to the six, send that line projected forward
        //               (so the part that pushes against your hands is current),
        //               and replay only the leftover detail late. Nothing
        //               loop-critical is delayed. Risk: a sharp hit has energy
        //               in both parts, so splitting it can smear the strike.
        //   1 = Replay. Play the six out in order through a Hermite curve,
        //               keeping each event whole, then pull the whole thing
        //               forward using the WHEEL's own velocity.
        //               That predictor input is measured locally with no
        //               telemetry delay, which makes it a better basis for
        //               cancelling lag than extrapolating the laggy signal from
        //               itself. Risk: prediction gain needs tuning per wheel.
        //
        // Kept as a user choice rather than a decision baked in blind: they are
        // different trades, not better and worse, and the wheel is the judge.
        //
        // THE WHEEL JUDGED (owner rig, G PRO, 2026-08-15): Lead RINGS. Engine
        // texture built into a growing oscillation that no damper setting
        // cured, while Replay in the same session stayed clean, and turning
        // prediction off brought the ring back in either mode. Read together
        // that says the loop is delay-limited and Replay's predictor is what
        // holds it together: its input is the WHEEL's own velocity, measured
        // locally, so it supplies phase lead where the loop lost it. Lead
        // projects the laggy telemetry from itself instead, which amplifies
        // exactly the fast content the loop then feeds back. So Replay is the
        // default now; Lead stays selectable for anyone whose wheel disagrees.
        public int IRacingForceMode { get; set; } = 1;

        // How far to TRUST the learned prediction. Not a tuning step: the whole
        // point of learning the correction is that the user should not have to
        // find a number. 1.0 means "use what it worked out", which is the
        // correct default for a value derived from this car at this speed.
        //
        // It stays adjustable as an escape hatch, not as a dial to hunt with.
        // Prediction can feel nervous to some drivers even when it is accurate,
        // and 0 turns it off entirely, leaving a faithful but slightly late
        // replay. Anyone who finds themselves changing this per car should tell
        // us, because that would mean the learning is not doing its job.
        //
        // (Shipped briefly at 0.5, which was incoherent: halving a value the
        // predictor derived is just a hand-tuned gain wearing a disguise.)
        public float IRacingPredictGain { get; set; } = 1.0f;
        public float ModeBRiseGamma { get; set; } = 0.80f;   // <1 = weight arrives in normal cornering
        public float ModeBPeakUtil  { get; set; } = 1.0f;    // combined-slip value treated as the grip limit
        public float ModeBDropFloor { get; set; } = 0.50f;   // torque left past the limit
        public float ModeBEmaMs     { get; set; } = 40f;     // input smoothing time constant
        public float ModeBSign      { get; set; } = 1f;      // SAT direction (BSIGN; -1 flips)
        public float ModeBDamper    { get; set; } = 0.07f;   // "Damping" slider: velocity damping (Mode B only)
        public float ModeBCenter    { get; set; } = 0.25f;   // "Centering" slider: speed-scaled centering (Mode B only)
        public float ModeBLatGain     { get; set; } = 0.60f; // cornering weight: +gain per lateral g (BLAT)
        public float ModeBDirSoft     { get; set; } = 0f;    // center flat-spot width (BDIRK); 0 = raw linear (Direct centering + the damper own center calm now)
        public float ModeBLockupRecoverMs { get; set; } = 30f; // "Lockup recovery" slider (BRECOVER): how fast force returns after lockup/wheelspin eases
        public float ModeBLockupPoint { get; set; } = 0.8f; // |slip ratio| treated as full lockup (BLOCKPT); higher = wheel keeps its weight deeper into braking before lightening (owner 2026-07-24 on-wheel)
        public float ModeBMinForce  { get; set; } = 0.05f;   // "Min force" slider (BMINF): smallest force the wheel renders; lifts faint detail above the motor's friction floor. 0.05 on every wheel since the 2026-08-07 retune (the G923 used to take 0.25 via ApplyWheelDefaults).

        // Mode B feel features (the haptic-engine layers 6-11, all validated
        // on-wheel and graduated to default ON there; the Mode B master
        // switch above is the real gate).
        public bool  ModeBCompressor         { get; set; } = true;   // soft-knee ceiling on the force
        public bool  ModeBSuspensionLoad     { get; set; } = true;   // steering load from suspension compression
        public bool  ModeBEarlyTorquePeak    { get; set; } = true;   // torque plateaus at 75% utilization
        public bool  ModeBRoadKick           { get; set; } = true;   // one-wheel bump kick in the force channel
        public float ModeBRoadKickGain       { get; set; } = 0.40f;  // kick strength
        public bool  ModeBReversalDamp       { get; set; } = true;   // fade force while a slide is caught back toward center, so the direction switch stops snapping (MBREV); default ON 2026-08-01 (mountain-drift validation)
        public float ModeBReversalDampGain   { get; set; } = 0.50f;  // reversal-damping strength, 0..1 (BREVG)
        public bool  ModeBPhaseLead          { get; set; } = true;   // lead the force ahead of the wheel to cancel telemetry-loop lag so a released wheel settles instead of oscillating (MBLEAD); default ON 2026-08-01
        public float ModeBPhaseLeadMs        { get; set; } = 40f;    // phase-lead prediction horizon in ms (BLEAD)
        public bool  ModeBCenterPd           { get; set; } = true;   // centering springs on the wheel's OWN position (HID reader) with a velocity look-ahead, so the pull toward straight is fresh and cannot ring (MBCPD); default ON 2026-08-01
        public float ModeBCenterLeadMs       { get; set; } = 40f;    // direct-centering look-ahead in ms (BCLEAD): how far ahead of the wheel's motion the spring aims
        public bool  ModeBGripAutoCal        { get; set; } = true;   // per-car grip-limit auto-calibration
        public bool  ModeBAutoStrength       { get; set; } = false;  // per-car auto strength (BAUTOS): learned force-peak scale so every car lands at the Strength slider's heaviness, iRacing style. Owner validated on-wheel 2026-08-09; ships default OFF by choice, because the natural per-car spread is a feature too.
        public bool  ModeBFrictionCircle     { get; set; } = true;   // friction-circle braking law replaces the lockup gate (BCIRCLE); default ON as of 0.2.5 (owner on-wheel: generally better than the gate)
        public bool  ModeBLongitudinalGripLearn { get; set; } = true;  // auto braking-grip: circle/gate radius follows each car's grip-cal peak instead of the manual point (BLEARN); default ON as of 0.2.5
        public float ModeBGripTrim { get; set; } = 1.0f; // radius = trim x grip-cal peak when auto braking-grip is on (BGTRIM; 1 = the raw detected grip)
        public bool  ModeBLateralDemand { get; set; } = true; // base the SAT force on LATERAL (cornering) grip so straight-line braking cannot pump the feedback loop (BLDEM); default ON 2026-08-01 (mountain-drift validation)

        // Cross-wheel FFB sync policy. Mode B / FFB tuning is wheel-specific (a
        // curve dialed in on a G PRO is wrong on a G923), so the Mode B settings
        // + CarGripCalibration still BACK UP but, by default, are NOT applied on
        // restore/sync to a device running a different wheel model. Ask (default)
        // prompts once per gated pull with apply-anyway/dismiss; the notice's
        // "remember my choice" flips this to Always / Never so auto-sync stops
        // re-prompting. Matched by chassis label (LastUsedWheel: "G PRO" / "RS50"
        // / "G923"); console transport ignored. Per-PC (this device's wheel), so
        // Excluded from backup.
        public CrossWheelFfbMode CrossWheelFfbMode { get; set; } = CrossWheelFfbMode.Ask;

        // "Apply anyway" retention for a cross-wheel-gated restore: the skipped
        // Mode B / grip keys as a JSON object string, plus the wheel model they
        // were tuned on. Persisted (survives a restart) so the notice can keep
        // offering to apply them until the user acts. Per-PC + transient, so
        // NOT backed up (Excluded). Empty = nothing pending.
        public string PendingCrossWheelFfb       { get; set; } = "";
        public string PendingCrossWheelFfbSource { get; set; } = "";

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

        // Implement thud (Farming Simulator). Global-only, same rationale as
        // Airborne: one game family, one context, preset scoping would be
        // dead machinery. See ImplementThudEffect / ImplementThudSettings.
        public ImplementThudSettings ImplementThud { get; set; } = new ImplementThudSettings();

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

        // LIGHTSYNC tab unlock. Set by the LIGHTSYNC access code while the
        // lighting rework is in development. Locked (the default, and what any
        // release shipped mid-rework carries) leaves the wheel-lights controls
        // exactly where they have always been, on the Telemetry FFB tab; the
        // new tab does not exist for the user. Unlocked reveals the tab and
        // MOVES that one block into it, so there is only ever one copy of the
        // controls and one set of handlers.
        // Ships ON. The lighting work is released, so the tab is simply part
        // of the plugin now, and this survives only as the toggle that puts the
        // controls back on the Telemetry FFB tab for anyone who preferred them
        // there. The LIGHTSYNC access code still flips it.
        public bool LightsyncTabUnlocked { get; set; } = true;

        // One-time: an install that predates the release has this stored FALSE,
        // and a changed default never reaches a value already on disk, so those
        // users would silently keep the tab hidden. Flipped once, then latched,
        // so anyone who deliberately turns it off afterwards stays off.
        public bool LightsyncReleasedMigrated { get; set; } = false;

        // Per-car rev-light data from the community lovely-car-data project
        // (CC BY-NC-SA 4.0). When on, a car we have data for lights on its own
        // switch-on points and per-gear redlines instead of our one-size ramp.
        // Reaches a third-party host (raw.githubusercontent.com) and caches per
        // machine, so it is off until the user asks for it. Doubly gated for now:
        // the control lives on the LIGHTSYNC tab, which is itself behind the
        // access code while the lighting rework is in development.
        public bool LovelyCarDataEnabled { get; set; } = false;

        // Which LIGHTSYNC custom slot the plugin borrows: 0..4 to pin CUSTOM 1..5,
        // or -1 (the default) to work it out itself.
        //
        // Automatic is the default because WHICH slot gets borrowed is plumbing,
        // not a decision anyone wants to make. Asked to show a pattern, the
        // plugin uses the slot the wheel is already displaying when that is a
        // custom one, so nothing visibly moves; otherwise it takes the last slot.
        // Either way the contents are read and saved before the first write and
        // handed back on exit, so borrowing stays reversible. Portable: the five
        // slots exist on any of these wheelbases, so the preference travels.
        public int LightsyncDynamicSlot { get; set; } = -1;

        // Pin every deliberate pattern pick to the car you are in, without having
        // to press Remember. Off by default: pinning is a per-car commitment, and
        // someone flicking through patterns to look at them should not end up with
        // whichever one they stopped on bound to that car forever.
        //
        // Only DELIBERATE picks count (a dropdown, a pattern-editor row, a bound
        // cycle button). Applying a pattern the car already remembers, or restoring
        // one, does not re-pin anything.
        public bool AlwaysRememberCarPattern { get; set; } = false;

        // Per-channel trim for what the LEDs actually emit. See LedColorGain.
        // The colors we store are sRGB intent; these three say how far each
        // channel has to be cut for this particular wheel to render that intent
        // correctly, because the red die is typically the weak one and a
        // nominal yellow arrives looking like lime.
        //
        // NULL MEANS NEVER CHOSEN: this install has no opinion, so the trim
        // resolves from the shipped values in LedColorGain via
        // TrueforcePlugin.EffectiveLedTrim. A concrete value is a DELIBERATE
        // choice made on the sliders, 1.0 included, and no default overwrites
        // it again.
        //
        // That is the whole reason these are nullable. A plain float cannot
        // tell "never touched" from "turned the correction off on purpose",
        // and those two must behave differently on the next launch and on any
        // future retune of the shipped numbers. It also means Reset can put a
        // user back on the shipped tuning rather than stranding them on
        // identity, which is a much easier button to press by accident.
        //
        // Three scalars rather than a float?[3] on purpose: SimHub's loader
        // deserialises with ObjectCreationHandling.Auto and APPENDS onto a
        // pre-populated collection instead of replacing it, so an array with a
        // default would load back with six entries. See the same warning on
        // DashTabOrder and IRacingMaxForceByCar. Auto does not affect a
        // Nullable<T>: there is no instance to reuse and no Add to call.
        // One-time hint on the LIGHTSYNC tab explaining that a bound button
        // walks the whole pattern library, with SimHub's binder embedded in it.
        // Set only when the user dismisses it for good; it also self-suppresses
        // once the action is actually bound, so this latch only covers the
        // "I read it and I am not binding anything" case. Nag state, so it is
        // Excluded from backup: it re-shows harmlessly on a second PC.
        // One-time modal on first LIGHTSYNC open, explaining what the tab lifts
        // off the wheel's own five-pattern menu. Latched on ANY outcome so it
        // never re-nags, same as HasSeenModeBIntro. Nag state, so Excluded from
        // backup: it re-shows harmlessly on a second PC.
        public bool HasSeenLightsyncIntro { get; set; } = false;

        public bool LightsyncCycleHintDismissed { get; set; } = false;

        public float? LedTrimR { get; set; }
        public float? LedTrimG { get; set; }
        public float? LedTrimB { get; set; }

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

        // One-shot wheel-defaults latch: the chassis the per-wheel Mode B
        // defaults were last evaluated for on THIS PC (see ApplyWheelDefaults).
        // The defaults only ever apply while the tuning is an untouched shipped
        // recipe, so tuned setups are never touched; the latch stops
        // re-evaluation on every reconnect. Per-PC hardware state like
        // LastUsedWheel: machine-local, never travels.
        public string WheelDefaultsApplied { get; set; } = "";

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

        // Welcome modal lifecycle. The welcome is a PROCEED (disclosure +
        // optional account): shown once, latched on any dismissal.
        // DeclineCount / NextShowAt are legacy from the old up-to-two-
        // pitches consent flow; NextShowAt is still honored as a show gate
        // for settings files mid-cadence, DeclineCount is only ever reset.
        public bool      HasSeenNetworkedWelcome { get; set; } = false;
        public int       WelcomeDeclineCount     { get; set; } = 0;
        public DateTime? WelcomeNextShowAt       { get; set; } = null;
        // One-time intro for Telemetry Based FFB (Mode B), shown the first time
        // a Mode-B-capable game (FM8 / FH5 / FH6) is the active game. Per-machine.
        public bool      HasSeenModeBIntro       { get; set; } = false;

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
    /// ceiling and the near-limit seconds that back it (confidence). Also
    /// carries the auto-strength learner's force-peak pair (same learner
    /// class, force domain); ForcePeak 0 = never learned, so entries written
    /// by older builds deserialize as strength-unlearned and stay identity.</summary>
    public sealed class CarGripCal
    {
        public float Peak          { get; set; } = 1.0f;
        public float QualifyingSec { get; set; }
        public float ForcePeak     { get; set; }
        public float ForceQualSec  { get; set; }
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

        /// <summary>The user's own engine-type pick for THIS variant (set via
        /// the Car facts engine dropdown). Wins over the auto-detected /
        /// community layout for this variant only, mirroring
        /// <see cref="UserRedlineRpm"/>: an explicit pin that beats the
        /// cascade but leaves auto-detection running underneath. null = no
        /// pin; follow the cascade (variant facts / community / telemetry /
        /// heuristic).</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineLayout? UserEngineLayout { get; set; }

        /// <summary>When <see cref="UserEngineLayout"/> == Custom, the Id of
        /// the <see cref="CustomEngineDef"/> in
        /// <see cref="TrueforceSettings.CustomEngines"/> that defines the
        /// pattern / electric behavior. Empty otherwise. Cleared when that
        /// custom engine is deleted from the library.</summary>
        public string UserCustomEngineId { get; set; } = "";

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
        // At least one fact type came back empty, so this entry expires on the
        // short TTL (CommunityCachePartialTtl) instead of the full one. Absent
        // in older cache files, which deserialize to false and keep the full
        // TTL: harmless, and they age out on their own.
        public bool Partial { get; set; }
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
        // Implement thud (FS linkage clunk). Null in presets saved before
        // the effect existed; apply keeps the user's current values.
        public ImplementThudSettings ImplementThud { get; set; }

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

        /// <summary>LEGACY (pre-2026-07 car-facts centralization). The engine
        /// type used to be a preset-scoped pick; it now lives in Car facts as
        /// the per-variant <see cref="EngineVariant.UserEngineLayout"/> pin.
        /// Kept so old JSON deserializes and so the one-time
        /// EngineChoiceMovedToCarFactsV1 migration can relocate per-car
        /// values. Never read at runtime after migration.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public EngineLayout Layout { get; set; } = EngineLayout.Auto;

        /// <summary>LEGACY. Companion of <see cref="Layout"/> == Custom: the
        /// Id of the library <see cref="CustomEngineDef"/> it pointed at.
        /// Relocated by the same migration as Layout; never read at runtime
        /// after migration.</summary>
        public string CustomEngineId { get; set; } = "";

        /// <summary>LEGACY (pre-custom-library). Inline user-supplied firing
        /// pattern; the one-time migration in the engine-choice relocation
        /// mints a library <see cref="CustomEngineDef"/> from it. Format:
        /// comma-separated phase positions in [0, 1), optionally with
        /// ":amplitude" per pulse (FiringPatternDb.ParseCustom). Never read
        /// after migration.</summary>
        public string CustomFiringPattern { get; set; } = "";

        /// <summary>LEGACY. Human-friendly name tag for
        /// <see cref="CustomFiringPattern"/>; migrated with it.</summary>
        public string CustomFiringPatternName { get; set; } = "";

        // ---- Serialization gates (2026-07 centralization) ----
        // The legacy engine-type fields DESERIALIZE (old files and the
        // one-time migration need them) but are never written again: new
        // saves, uploads, backups and healed files all drop the keys, so a
        // recipient on any plugin version falls back to auto-detection
        // instead of inheriting a stale preset engine.
        public bool ShouldSerializeLayout()                  => false;
        public bool ShouldSerializeCustomEngineId()          => false;
        public bool ShouldSerializeCustomFiringPattern()     => false;
        public bool ShouldSerializeCustomFiringPatternName() => false;
        public bool ShouldSerializeCylinders()               => false;
        public bool ShouldSerializeEngineConfig()            => false;
        public bool ShouldSerializeFiringOrderEnabled()      => false;

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
        // + FiringOrderEnabled (bool) as the engine-shape definition. These
        // fields are kept on the type so Newtonsoft can still deserialize old
        // JSON; the one-time EngineChoiceMovedToCarFactsV1 migration folds
        // them when relocating per-car engine picks into variant pins.
        // Runtime never reads them (2026-07 centralization).

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

    // Implement thud (Farming Simulator): the linkage clunk when equipment
    // lowers or raises. Global-only like Airborne (no preset/per-car slots):
    // it fires in exactly one game family and one context, so preset
    // scoping would be dead machinery.
    public sealed class ImplementThudSettings
    {
        public bool  Enabled  { get; set; } = true;
        public float Gain     { get; set; } = 1.0f;
        public float Freq     { get; set; } = 30.0f;
        // Raise-edge amplitude relative to lowering (linkage releasing vs
        // the tool landing).
        public float RaiseAmp { get; set; } = 0.6f;
        // Hydraulic hum while a lower/raise/fold is in motion (mod 0.2.8+).
        public float HumAmp   { get; set; } = 0.30f;
        public float HumFreq  { get; set; } = 46.0f;
        // Hum pitch dynamics: how far below the hum pitch the spool-up bend
        // starts (fraction), how much travel speed raises the pitch
        // (fraction at a fast lower, mod 0.2.14+), and the level of the
        // octave layer inside the hum voice.
        public float BendDepth   { get; set; } = 0.15f;
        public float SpeedPitch  { get; set; } = 0.12f;
        public float HarmonicAmp { get; set; } = 0.22f;
        // How much slow travel quiets the hum: its loudness floor is
        // 1 - SpeedVolume, so 0 = constant loudness, 1 = fully speed-tracked.
        public float SpeedVolume { get; set; } = 0.5f;

        [JsonConverter(typeof(StringEnumConverter))]
        public Waveform Waveform { get; set; } = Waveform.Sine;
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
    /// steps out. The louder axle is the one losing grip. On by default
    /// (owner's call, 2026-07-14, with the tuned baseline): inert without
    /// per-tire telemetry (Forza, Assetto Corsa, and Farming Simulator with
    /// the telemetry mod today), so other games are unaffected.
    /// PredictiveSlip starts the texture a fixed validated 150 ms before the
    /// slip fully develops; RevLockedRearPulse locks the rear pulse rate to
    /// the actual rear wheel rev rate when per-tire data allows.</summary>
    public sealed class AxleSlipSettings
    {
        public bool  Enabled            { get; set; } = true;
        public float Gain               { get; set; } = 0.095f;  // owner-tuned G PRO baseline (2026-07-14)
        public bool  PredictiveSlip     { get; set; } = true;
        public bool  RevLockedRearPulse { get; set; } = true;

        // Tuning sliders (2026-07-14). Defaults are the owner-tuned G PRO
        // baseline of 2026-07-14 (also baked into the shipped built-in
        // presets); presets saved before these fields existed deserialize to
        // this baseline. Strengths are per-voice volumes under the master
        // Gain. Pitches are band CENTERS: the engine still sweeps a
        // proportional band around them as slip builds (front 0.75x..1.25x of
        // center, rear 40/55x..70/55x), so retuning the pitch keeps the
        // climb-with-slip character.
        public float FrontStrength { get; set; } = 0.30f;
        public float RearStrength  { get; set; } = 0.205f;
        public float FrontPitchHz  { get; set; } = 200f;
        public float RearPitchHz   { get; set; } = 35f;
        public float JudderDepth   { get; set; } = 0.8f;
        public float OnsetUtil     { get; set; } = 0.85f;
    }

    /// <summary>Settings for the kerb thump: a single firm whack the instant
    /// a wheel first touches a kerb, distinct from the rumble that follows.
    /// Scales with speed. On by default (owner's call, 2026-07-14): inert
    /// without kerb telemetry (Forza's kerb flag; in Farming Simulator the
    /// source synthesizes hits from hard suspension transients and the voice
    /// renders as the Terrain texture leading edge). Gain defaults to the
    /// owner-tuned G PRO baseline (2026-07-14); the original 1.6 was dialed
    /// in on a G923, whose weaker motor needed it much hotter. Freq sits
    /// below the 40 Hz gear thud so the two stay distinct.</summary>
    public sealed class KerbThumpSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 0.135f;   // owner-tuned G PRO baseline (2026-07-14)
        public float Freq    { get; set; } = 30.0f;
    }

    /// <summary>Settings for the lockup judder: a flat-spot pulse while a
    /// braking tire is locked, slowing with the car the way a real flat spot
    /// would. On by default (owner's call, 2026-07-14): inert without
    /// per-tire telemetry (Forza and Assetto Corsa today; the section hides
    /// for Farming Simulator, whose brake model is presumed never to lock a
    /// wheel).</summary>
    public sealed class LockupJudderSettings
    {
        public bool  Enabled { get; set; } = true;
        public float Gain    { get; set; } = 0.14f;   // owner-tuned G PRO baseline (2026-07-14)
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
        // (Threshold, RedlineRpm, and EngageMode were retired with the
        // car-facts centralization, 2026-07-17: the redline is car truth,
        // edited in the Car facts panel and stored per variant, not a
        // preset knob. Old preset JSONs carrying those keys deserialize
        // harmlessly; the values are ignored. A user whose Manual redline
        // stops applying will feel the buzz move and set the redline in
        // Car facts, which also serves the community.)

        // RPM offset applied to the resolved engage point on every path.
        // Negative = fire before the redline, positive = after, 0 = right
        // at it. The one preset-scoped feel knob for WHERE the buzz fires;
        // the redline itself comes from the Car facts cascade.
        public float RedlineOffsetRpm { get; set; } = 0.0f;

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
        public ImplementThudSettings ImplementThud { get; set; }

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
            LockupJudder == null && Airborne == null && ImplementThud == null;

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
