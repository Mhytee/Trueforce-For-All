// SimHub plugin owning the Trueforce HID session and the audio-haptic Mixer.
//
// Lifecycle:
//   Init: load settings → discover wheel → open + init + start stream →
//         create AudioCaptureSource (per-process loopback, retargeted on
//         game start/stop) and add it to the Mixer.
//   DataUpdate: track current game name / process for the capture timer.
//   End: save settings, stop producer + capture, clean up the device.
//
// The producer thread runs independently of the SimHub data tick because
// Trueforce wants 1 kHz samples; SimHub's data ticks vary by game (60-200 Hz
// typical) and would be too coarse to drive the stream directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    // Description deliberately omits the version: PluginDescription requires a
    // compile-time-constant string, and the assembly version (driven by
    // <Version> in TrueforceForAll.Plugin.csproj) is already surfaced at
    // runtime by UpdateChecker, the settings panel header, and the changelog
    // dialog. Adding it here too just creates a stale-copy hazard on bumps.
    [PluginDescription("Logitech Trueforce-compatible haptics for any SimHub-supported game on G PRO, RS50 and G923 wheels.")]
    [PluginAuthor("Mhytee")]
    [PluginName("Trueforce For All")]
    public sealed class TrueforcePlugin : IDataPlugin, IWPFSettingsV2
    {
        private const int BatchSamples = TrueforceDevice.NewPerPacket; // one packet's worth

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle => "Trueforce For All";
        public ImageSource PictureIcon => null;

        public TrueforceSettings Settings { get; private set; }

        private readonly Mixer _mixer = new Mixer();

        // Per-car preset files, one .tfcar.json per car, the canonical
        // home for car-specific tuning post-Model G refactor. Game presets
        // no longer carry CarOverrides; switching presets doesn't touch
        // per-car values.
        private CarPresetStore _carStore;

        // Sidecar registry of imported community packs (installed-packs.json at
        // the user library root). Preserves pack identity that the bare
        // game-preset snapshots and car files don't otherwise carry; the
        // Preset Manager's Source column reads it to attribute imported rows.
        private InstalledPacksStore _installedPacks;

        // Snapshot of each car's override AS OF its last save / load. Used
        // by IsSectionDirty to tell whether an override section has been
        // edited since the last save, without re-reading the file. Updated
        // by PersistActiveCarOverride / SaveActiveCarPresetAs / preset
        // switch; invalidated by DeleteCarPreset.
        private Dictionary<string, CarOverride> _lastPersistedCarOverrides = new Dictionary<string, CarOverride>();

        // Tracks (gameName + "|" + carId) pairs we've already considered for
        // GameName backfill this session. Prevents re-scanning the car folder
        // every time the user toggles back to the same car.
        private readonly HashSet<string> _gameNameBackfillDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Same dedup set, but for DisplayName backfill — renames legacy presets
        // whose PresetName was just the carId (e.g. "Car_424") to the resolver's
        // DisplayName ("1997 Mazda RX-7") so the UI shows real car names instead
        // of opaque ordinals. Only rewrites presets where the user clearly never
        // customized the name; user-renamed presets are left alone.
        private readonly HashSet<string> _displayNameBackfillDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private TrueforceDevice _device;
        private AudioCaptureSource _audio;
        private HelperHost _helperHost;
        private UsbPcapFfbTap _ffbTap;
        private FeedbackBoxInjector _feedbackInjector;
        // Reads the wheel's physical steering off its HID controller interface,
        // so the stationary spring has a position to work with even when the
        // game reports none (Forza pause / pre-race countdown). See
        // WheelSteeringReader and ApplyStationarySpring.
        private WheelSteeringReader _steeringReader;
        private MairaIpcSource _mairaIpc;

        // Snapshot of the HID-side wheel match (Vid/Pid/Model) we found in Init.
        // Held so the manual USB-device picker can highlight the row that
        // matches the wheel HID has already enumerated.
        private ushort _hidWheelVid;
        private ushort _hidWheelPid;
        public ushort HidWheelVid => _hidWheelVid;
        public ushort HidWheelPid => _hidWheelPid;

        // Background GitHub-releases check. Kicked off async in Init; the
        // settings panel polls IsUpdateAvailable in its timer tick to decide
        // whether to surface the update banner. Network failures are silent.
        private UpdateChecker _updateChecker;
        public UpdateChecker UpdateChecker => _updateChecker;
        private Thread _producerThread;
        private volatile bool _shuttingDown;
        // Number of TestEffect background tasks currently running. Drained at
        // End() so they can't keep mutating effect state after the device has
        // been disposed.
        private int _activeTestTasks;

        public EnginePulseEffect  EnginePulse  { get; private set; }
        public RoadBumpsEffect    RoadBumps    { get; private set; }
        public TractionLossEffect TractionLoss { get; private set; }
        public GearShiftEffect    GearShift    { get; private set; }
        public AbsClickEffect     AbsClick     { get; private set; }
        public PitLimiterEffect   PitLimiter   { get; private set; }
        public DrsEffect          Drs          { get; private set; }
        public CollisionEffect    Collision    { get; private set; }
        public RevLimiterEffect   RevLimiter   { get; private set; }
        // Coordinator voice (no audio of its own): ducks the others when the
        // car is airborne. See AirborneEffect.
        public AirborneEffect     Airborne     { get; private set; }
        private TelemetryEffect[] _effects;

        // Rim rev/shift LEDs over HID++ (iRacing-scoped, separate from the
        // Trueforce stream). Lazily opens its own HID handle on first gated
        // frame; never touches the ep3 audio-haptic device.
        private RpmLedController _rpmLeds;

        // Active telemetry source. The plugin currently always uses
        // SimHubTelemetrySource (universal, ~60 Hz from the SimHub data
        // pipeline). Per-game enhanced sources (AC native MMF, etc.) will
        // be hot-swapped here on game change. _simHubSource is held as a
        // typed field because we feed it from DataUpdate; _telemetrySource
        // is what the rest of the plugin treats as "the current source"
        // for status / UI / future polymorphic dispatch.
        private SimHubTelemetrySource _simHubSource;
        private ITelemetrySource      _telemetrySource;
        public  ITelemetrySource      TelemetrySource => _telemetrySource;

        // ---- Port discovery ----
        // When a UDP source (Forza) has been running without
        // receiving anything, kick off a scan across known alternate
        // ports to find where the game is actually sending. UI subscribes
        // to DiscoveredAlternatePort to surface a "switch to port X?"
        // banner. The first scan fires DiscoveryNoPacketsTriggerMs after
        // the source starts; if it doesn't find anything (or finds
        // nothing the user adopts) we retry every DiscoveryRetryIntervalMs
        // while the source keeps receiving zero packets, covers the case
        // where the user enables UDP in the game minutes after Trueforce
        // started.
        private const int DiscoveryNoPacketsTriggerMs = 10_000;
        private const int DiscoveryScanTimeoutMs      = 8_000;
        private const int DiscoveryRetryIntervalMs    = 60_000;
        private long  _discoverySourceStartedTicks;
        // Ticks when the next scan attempt becomes eligible. 0 means
        // "compute from source-start + initial trigger delay".
        private long  _discoveryNextAttemptTicks;
        private object _discoverySourceKey;
        private int  _discoveredAlternatePort;
        public int DiscoveredAlternatePort => System.Threading.Volatile.Read(ref _discoveredAlternatePort);
        /// <summary>Fired on a worker thread when a port scan succeeds.
        /// Args: (gameKind "forza", discoveredPort).</summary>
        public event Action<string, int> AlternatePortDiscovered;

        /// <summary>True when the active game is one SimHub has a telemetry
        /// reader for, i.e. anything with a non-Custom GameName. SimHub's
        /// "Custom_*" code is a definitive marker that the user added the
        /// game manually and SimHub has no built-in way to source telemetry,
        /// so engine/RPM/speed-driven effects can't fire. Built-in games
        /// keep this true even at the main menu / paused, we don't grey
        /// out the panel just because telemetry isn't flowing right now.</summary>
        public bool HasUsefulTelemetry =>
            !string.IsNullOrEmpty(_activeGame)
            && !_activeGame.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);

        // Cached slow-rate fields from the most recent SimHub DataUpdate.
        // When an enhanced source is active, DispatchFrame overlays these
        // onto each frame: MaxRpm is static per car (no benefit to physics
        // rate), and AC's `physics.abs` is the *configuration level*, not
        // pump activity, SimHub derives a usable AbsActive signal that we
        // inherit instead of re-implementing. PitLimiter and DRS are also
        // overlaid: AC's MMF source and Forza UDP both leave them null, but
        // SimHub's per-game readers know how to extract them, so we mirror
        // SimHub's value into enhanced frames so the effects fire on AC pit
        // lane and any DRS-equipped sim using an enhanced source.
        private double _lastSimHubMaxRpm;
        private double _lastSimHubRedlineRpm;
        private int    _lastSimHubAbsActive;
        private int?   _lastSimHubPitLimiterActive;
        private int?   _lastSimHubDrsActive;

        // Motion state latched for the stationary-spring FFB floor, written on
        // the telemetry thread (DispatchFrame), read on the Trueforce stream
        // thread (the FfbTargetProvider lambda, 1 kHz). float fields: 32-bit
        // access is atomic even on 32-bit SimHub, so the stream thread can
        // never observe a torn value that would jolt the wheel. _lastSteerTicks
        // is a freshness stamp (Stopwatch ticks); when steering goes stale
        // (game closed, or a source that doesn't report steering took over)
        // the spring self-disengages without needing an explicit reset hook.
        private volatile float _lastSteerNorm;
        private volatile float _lastSpeedKmh;
        private long _lastSteerTicks;
        private static readonly long SteerMaxAgeTicks = Stopwatch.Frequency / 2; // 500 ms

        // Smoothed steering used by the spring. Eased toward _lastSteerNorm on
        // each provider call (~250 Hz) so a low-resolution source (Forza's
        // 8-bit Steer, ~254 steps lock-to-lock) doesn't translate its quantized
        // steps into perceptible force notches in the centering spring, and so
        // the spring interpolates smoothly between a source's slower telemetry
        // updates. Stream-thread-only (the provider lambda); no sync needed.
        // High-resolution sources (AC's float) are already smooth; the tiny
        // added lag is harmless for a parked-car comfort force.
        private float _springSteerEma;
        // ~50 ms time constant at the 250 Hz provider rate. Fast enough to
        // track parking maneuvers, slow enough to dissolve 8-bit quantization.
        private const float SpringSteerEmaAlpha = 0.08f;

        // Stationary-spring desk self-test (SPRING code). When the deadline is
        // in the future, ApplyStationarySpring drives a synthetic centering
        // force whose direction alternates every ~1.5 s at zero speed,
        // bypassing the enabled / freshness / null-target gates, so the
        // spring's strength and force direction can be felt on the desk with
        // no game running. Stream-thread reads via Interlocked (long isn't
        // atomic on 32-bit SimHub). NOTE: verifies the spring's own
        // force-vs-steer-sign mapping, not whether a given game reports
        // steering with the sign we expect.
        private long _springTestEndTicks;
        private const double SpringTestDurationSec = 6.0;

        // Throttle for retrying enhanced-source acquisition. AC's shared memory
        // page only appears once the game loads into a session, but SimHub
        // reports GameName as soon as the AC process starts (often a minute
        // before the MMF exists). Without a retry, that first-attempt failure
        // would strand us on SimHub fallback for the whole session.
        private long _lastEnhancedRetryTicks;

        // Logitech G HUB process detection. G HUB claims the wheel's HID
        // interface and blocks our HidSharp open call, so when it's running
        // the plugin can't talk to the wheel. We poll the process list once
        // every ~5 s (cheap; one allocation) and surface the result both as
        // a dedicated status banner in the settings UI and as the most-
        // blocking item in WheelQuietDiagnostic so users land on the real
        // root cause instead of "wheel not detected." _gHubLastLoggedState
        // ensures we only log on transitions, not every poll.
        private long _lastGHubCheckTicks;
        private volatile bool _isGHubRunning;

        // Device recovery watchdog. The whole bring-up (discover -> open ->
        // init -> FFB tap -> stream) used to run once in Init; if the wheel
        // was absent, G HUB was holding the HID, or the stream later faulted
        // (hot-unplug, USB stall), the plugin stayed dead until SimHub was
        // restarted. The watchdog (MaybeRecoverDevice, polled from DataUpdate)
        // re-attaches transparently: it fires when _device is null or the
        // stream has faulted, is gated off while G HUB is running (can't open
        // the HID then, so it self-heals the instant G HUB closes), throttled
        // so a permanently-absent wheel doesn't churn, and runs the blocking
        // bring-up on a thread-pool thread so it never stalls SimHub's tick.
        // _recoveryInProgress is the single-flight guard and also drives the
        // StreamStatus "Reconnecting..." text.
        private volatile bool _recoveryInProgress;
        private long _lastRecoveryAttemptTicks;
        private static readonly long RecoveryIntervalTicks = Stopwatch.Frequency * 3; // 3 s
        // Throttle for the verbose "wheel not found" discovery diagnostic: the
        // recovery watchdog retries discovery every few seconds, so without this
        // a persistent not-detected state would log the full HID landscape on
        // every tick. Logs at most once per minute (and always on the first miss).
        private long _lastDiscoveryDiagTicks;
        private bool _gHubLastLoggedState;
        // Only the G HUB *UI* process (lghub.exe) gates wheel access. We
        // intentionally do NOT gate on lghub_agent.exe (Logitech's always-on
        // background agent): it keeps running after the user quits G HUB and
        // does not hold the wheel's MI_02 Trueforce interface, so gating on it
        // latched _isGHubRunning true for the whole session and permanently
        // blocked the recovery watchdog. See the detection block below.
        private const string GHubProcessName = "lghub";
        private static readonly long GHubCheckIntervalTicks = Stopwatch.Frequency * 5;

        // Snapshot every Logitech-related process running right now (G HUB,
        // its agent + updater, older Gaming Software, etc.) as a single log
        // line. Lets a support bundle answer "what was running when the user
        // hit this state" without us guessing from a partial diag. Matches
        // are case-insensitive substring on ProcessName (no .exe), so we
        // catch lghub, lghub_agent, lghub_updater, lghub_system_tray, LCore,
        // LGS, Logi*, etc. Cheap: one Process.GetProcesses() + a string
        // contains per process. Returns "(none)" when nothing matches.
        private static string SnapshotLogitechProcesses()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                bool any = false;
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (string.IsNullOrEmpty(name)) continue;
                        string lower = name.ToLowerInvariant();
                        if (lower.IndexOf("lghub", StringComparison.Ordinal) >= 0
                         || lower.IndexOf("ghub",  StringComparison.Ordinal) >= 0
                         || lower.IndexOf("logi",  StringComparison.Ordinal) >= 0
                         || lower.IndexOf("lgs",   StringComparison.Ordinal) >= 0)
                        {
                            if (any) sb.Append(", ");
                            sb.Append(name).Append(".exe (pid ").Append(p.Id).Append(")");
                            any = true;
                        }
                    }
                    catch { /* per-process access can fail; skip silently */ }
                    finally { try { p.Dispose(); } catch { } }
                }
                return any ? sb.ToString() : "(none)";
            }
            catch (Exception ex)
            {
                return $"(enumeration failed: {ex.GetType().Name})";
            }
        }

        /// <summary>True when Logitech G HUB (or its agent) is detected
        /// running. UI binds to this to show a warning banner. Updated on a
        /// 5-second poll from DataUpdate; first-detection logs to SimHub log.</summary>
        public bool IsLogitechGHubRunning => _isGHubRunning;

        // Auto-ratchet state. Snapshots the underrun/glitch counters once per
        // second; when delta crosses RatchetThreshold, the corresponding ring
        // is bumped one notch (UP). The "survived" capacity is persisted to
        // Settings so reinstalls don't re-glitch sessions; manual reset is
        // available from the Performance tab.
        //
        // Ratchet-DOWN is asymmetric, slow + hysteresis-protected, so a
        // brief noisy moment (Chrome update kicking in, antivirus scan)
        // doesn't leave the ring permanently inflated. After UP fires, a
        // 5-minute cooldown blocks any DOWN; after the cooldown, sustained
        // 60+ seconds of zero underruns triggers the FIRST one-notch DOWN
        // step. Subsequent DOWN steps use a much shorter 30s cooldown so a
        // transient load spike (track loading, replay scrub, alt-tab shock)
        // doesn't lock the ring inflated for 20+ minutes; once it's been
        // quiet for the full 5min and we've started descending, we trust the
        // descent and accelerate. UP fires fast (1s window); if noise
        // returns mid-descent it re-arms the long 5min cooldown.
        private const int  RatchetWindowMs           = 1000;
        // UP trigger: a single noisy window isn't enough. One-off CPU
        // stalls, USB hiccups, and brief game stutters don't reflect
        // sustained pressure on the ring, so we require BOTH the current
        // and previous 1-second windows to cross the threshold before UP
        // fires.
        //
        // Units note: underrun/glitch counters are duration-quantized at
        // ~20 ms per count (see UnderrunQuantumTicks in TrueforceDevice
        // and GlitchQuantumTicks in AudioCaptureSource). Sub-quantum
        // scheduling blips contribute 0, so a threshold of 5/s means
        // ~100 ms of cumulative real dropout per second. Combined with
        // the 2-window gate, UP fires only after ~200 ms of cumulative
        // dropout sustained across 2 consecutive seconds, which is a
        // genuine "ring is undersized" signal rather than tick noise.
        private const long RatchetThreshold          = 5;     // quantized events/s, REQUIRED IN 2 CONSECUTIVE WINDOWS
        private const int  RatchetDownQuietMs        = 60_000;   // 60 s of zero deltas → eligible for any DOWN step
        private const int  RatchetDownCooldownMs     = 300_000;  // 5 min after an UP before the FIRST DOWN allowed
        private const int  RatchetDownFastCooldownMs = 30_000;   // 30 s between subsequent DOWN steps once descent has started
        private long _autoRatchetLastCheckTicks;
        private long _autoRatchetLastTfCount;
        private long _autoRatchetLastAudioCount;
        // Previous window's deltas, for the "2 consecutive windows" UP gate.
        // Both _prevTfOverThreshold and the current tfDelta must cross
        // RatchetThreshold before UP fires.
        private bool _prevTfOverThreshold;
        private bool _prevAudioOverThreshold;
        // Stopwatch ticks of the most recent non-zero delta. Reset to "now"
        // any time we see ANY underrun/glitch in the 1s window. The 60s
        // quiet test compares (now - lastSeen) against RatchetDownQuietMs.
        private long _tfLastUnderrunSeenTicks;
        private long _audioLastUnderrunSeenTicks;
        // Stopwatch ticks of the most recent ratchet action (up OR down).
        // 5-minute cooldown gates any DOWN step against this.
        private long _tfLastRatchetActionTicks;
        private long _audioLastRatchetActionTicks;
        // True iff the last action on this ring was a DOWN step. Lets the
        // DOWN cooldown switch to the fast 30s value once descent has begun
        //, UP re-arms the long 5min cooldown by clearing this.
        private bool _tfLastActionWasDown;
        private bool _audioLastActionWasDown;

        // Fired on the producer thread when auto-ratchet bumps a ring size.
        // Args: isTfRing (true = Trueforce stream ring, false = audio ring),
        // oldCapacity, newCapacity. SettingsControl subscribes to show the
        // dismissable Revert/OK modal, must marshal to the UI thread.
        public event Action<bool, int, int> AutoRatchetBumped;

        // Per-car override tracking. Updated on each DataUpdate; if the CarId
        // changes we re-apply per-section overrides (or fall back to globals).
        private string _activeCarId;
        public string ActiveCarId => _activeCarId;

        // Human-readable name of the active car (e.g. "2017 Acura NSX"), set
        // by the car-change handler from CarCylinderResolver.Result.DisplayName
        // when a catalog hit provides one. Cleared on car change. Used to
        // auto-name per-car presets so the user sees the actual car name
        // instead of an opaque ordinal ("3445"). Null when no catalog hit.
        private string _activeCarDisplayName;
        public string ActiveCarDisplayName => _activeCarDisplayName;

        // Active game + active preset tracking. Presets are a named library
        // (Settings.Presets) that the user can apply to any game. GameDefaults
        // optionally binds a game to auto-load a specific preset on game change.
        // _activePresetName is the most-recently-applied preset (or null if
        // current settings are unsaved/manually-tuned).
        private string _activeGame;
        private string _activePresetName;
        public string ActiveGame        => _activeGame;
        public string ActivePresetName  => _activePresetName;
        public bool   ActiveGameIsNativeTrueforce => IsNativeTrueforceGame(_activeGame);

        public IEnumerable<string> PresetNames =>
            Settings?.Presets != null ? (IEnumerable<string>)Settings.Presets.Keys : Array.Empty<string>();

        // ---- Offline preset editing ----
        //
        // When the user picks Edit on a preset row in the Manage dialog, the
        // SettingsControl flips the live state to that preset and shows a
        // banner so users can author/edit without the matching game running.
        // While the flag is set, the DataUpdate-driven "auto-apply this
        // game's default" path is suppressed so a backgrounded game change
        // doesn't quietly clobber the user's in-progress edits. Exit happens
        // via Save / Save as new / Discard on the banner.
        private string _offlineEditPresetName;
        private GameSettingsSnapshot _preEditSnapshot;
        private string _preEditActivePresetName;

        public string OfflineEditingPresetName => _offlineEditPresetName;
        public bool   IsOfflineEditing         => !string.IsNullOrEmpty(_offlineEditPresetName);

        // Car-preset offline edit. Mirrors the game-preset flow above but for a
        // single car's override: loads the matching game default as the baseline
        // (so the override doesn't read as spuriously dirty against the wrong
        // game preset), pins the car, freezes it against live telemetry (see
        // DataUpdate), and restores the pre-edit state on exit. Save / Save as
        // new / Discard via the same banner.
        private string _offlineEditCarId;
        private string _offlineEditCarPresetName;
        private GameSettingsSnapshot _preEditCarSnapshot;
        private string _preEditCarActiveId;
        private string _preEditCarActivePresetName;

        public bool   IsOfflineEditingCar        => !string.IsNullOrEmpty(_offlineEditCarId);
        public string OfflineEditingCarId        => _offlineEditCarId;
        public string OfflineEditingCarPresetName => _offlineEditCarPresetName;

        /// <summary>Preset name bound as the auto-load default for the active
        /// game, or null if the active game has no default assigned.</summary>
        public string DefaultPresetForActiveGame
        {
            get
            {
                if (string.IsNullOrEmpty(_activeGame) || Settings?.GameDefaults == null) return null;
                Settings.GameDefaults.TryGetValue(_activeGame, out var p);
                return p;
            }
        }

        /// <summary>True when the active game has been observed to report a
        /// usable redline (learned + persisted in Settings.GamesWithRedline).
        /// The rev limiter fires AT the redline for these, so the engage-%
        /// control is irrelevant and the UI hides it. Reading the learned flag
        /// (not live telemetry) keeps the UI stable: it's correct immediately on
        /// settings open, before any data flows, for games seen before. Forza
        /// never qualifies (its redline reads out of range), so it keeps the
        /// engage-% control.</summary>
        public bool ActiveSourceUsesRedline =>
            !string.IsNullOrEmpty(_activeGame)
            && !IsForzaGameName(_activeGame)
            && Settings?.GamesWithRedline != null
            && Settings.GamesWithRedline.Contains(_activeGame);

        // Capture-targeting state. The poll thread (1 Hz) walks the process
        // table, decides which sim to capture, and tells HelperHost to retarget.
        private volatile string _currentGameName;
        private Thread _capturePollThread;
        private string _captureStatus = "Idle (no game running)";

        // Status surfaced to the SettingsControl.
        public string WheelStatus    { get; private set; } = "Not detected";

        // Backing field for StreamStatus. The public property overlays the
        // live device fault / recovery state so a wheel unplugged mid-session
        // is reflected the instant it happens, not on the next watchdog tick
        // (and never reads a stale "Streaming" while the wheel is dead).
        private string _streamStatus = "Stopped";
        public string StreamStatus
        {
            get
            {
                if (_recoveryInProgress) return "Reconnecting to wheel...";
                var d = _device;
                if (d != null && d.StreamFaulted)
                    return "Stream lost - auto-reconnecting (replug the wheel, or close G HUB, if this persists)";
                return _streamStatus;
            }
        }
        public string CaptureStatus  => _captureStatus;
        public string FfbTapStatus =>
            (_ffbTap?.Status ?? "Not started")
            + (OverridePinIsNonWheel
                ? "  -  The USB device you pinned isn't a Logitech wheel, so no force feedback will ever be captured. Clear the override (use auto) or pick the wheel."
                : "")
            + (_noFfbCaptureNotice != null ? "  -  " + _noFfbCaptureNotice : "");
        public int    ActiveVoiceCount => _mixer.SourceCount;

        // The wire shape that produced the first captured FFB sample this
        // session (transport / report ID / feature index / encoding + which
        // experimental sub-mechanism, if any, was load-bearing). Null until
        // capture is confirmed. Surfaced in the Export-logs manifest and the
        // experimental-success report.
        public string CaptureFingerprint =>
            _ffbTap?.CaptureFingerprint
            ?? (_debugForceSuccessBanner
                ? "transport=ep0-ctrl reportId=0x12 featIdx=0x10 encoding=hidpp-int16be@10 experimental=ON needed=[report0x12, loweredFloor] (TEST)"
                : null);

        // Dev/test (FFBOK access code): force the success banner on so both the
        // Yes (report) and No (troubleshooter) paths can be exercised without
        // real hardware. CaptureFingerprint returns a synthetic TEST value while
        // this is set so the prefilled report has something to show.
        private bool _debugForceSuccessBanner;
        public void DebugShowSuccessBanner()
        {
            _debugForceSuccessBanner = true;
            if (Settings != null) Settings.ExperimentalSuccessReportDismissed = false;
        }

        // True when experimental FFB detection was actually load-bearing in
        // getting capture working this session (the fingerprint lists a needed
        // sub-mechanism), and the user hasn't dismissed the prompt yet. Drives
        // the one-time "is it working?" banner. Requires the experimental
        // toggle on AND needed=[...] to be non-empty, so users who had it on but
        // would have worked anyway are never prompted.
        public bool ShouldShowExperimentalSuccessReport
        {
            get
            {
                if (Settings == null || Settings.ExperimentalSuccessReportDismissed) return false;
                if (_debugForceSuccessBanner) return true;   // dev/test override
                if (!Settings.ExperimentalFfbCapture) return false;
                string fp = _ffbTap?.CaptureFingerprint;
                if (string.IsNullOrEmpty(fp)) return false;
                return fp.Contains("needed=[") && !fp.Contains("needed=[none]");
            }
        }

        public void DismissExperimentalSuccessReport()
        {
            _debugForceSuccessBanner = false;   // clear the test override too
            if (Settings != null) Settings.ExperimentalSuccessReportDismissed = true;
            PersistSettings();
        }

        // True when a manual override pins a device whose identity we know and
        // it isn't a supported Logitech wheel (so the FFB tap can't possibly get
        // data from it). Drives the targeted notice in FfbTapStatus. Unknown
        // identity (legacy pin, vid=0) returns false: we don't accuse a pin we
        // can't identify.
        public bool OverridePinIsNonWheel
        {
            get
            {
                if (!HasManualUsbPcapDevice || Settings == null) return false;
                ushort vid = (ushort)Settings.ManualUsbPcapVid;
                ushort pid = (ushort)Settings.ManualUsbPcapPid;
                if (vid == 0) return false;
                return !WheelDiscovery.IsSupportedWheel(vid, pid);
            }
        }

        // Set by the FFB tap's OnNoFfbWarning escalation: the tap is on the
        // right wheel and the game is driving, but no force feedback reaches our
        // capture even in whole-bus mode (a USBPcap port-coverage problem).
        // Surfaced through FfbTapStatus. Cleared when FFB is captured again.
        private volatile string _noFfbCaptureNotice;

        // Non-null when the detected wheel is a supported-by-inference PID
        // (Xbox G923) we haven't hardware-verified. Surfaced as an info
        // banner so the user knows to report the one divergence we can't
        // rule out. Null for hardware-confirmed wheels.
        private string _unverifiedWheelNotice;
        public string UnverifiedWheelNotice => _unverifiedWheelNotice;

        // True when USBPcapCMD.exe is locatable right now (override path, env
        // var, or default Program Files paths). Cheap probe; the settings UI
        // polls this on its tick to show/hide the Browse + Reinstall buttons.
        public bool IsUsbPcapAvailable =>
            UsbPcapFfbTap.LocateUsbPcapCmd(Settings?.UsbPcapCmdPathOverride) != null;

        // Whether the SimHub process is running elevated (administrator).
        // Cached: elevation can't change without a process restart. USBPcap's
        // FFB capture is far more reliable elevated, and some games/setups
        // only pass force feedback through when SimHub is admin (e.g. a user's
        // RaceRoom FFB worked only as admin), so the UI prompts for it when
        // false. Treated as effectively required.
        private bool? _isElevatedCache;
        public bool IsRunningElevated
        {
            get
            {
                if (_isElevatedCache.HasValue) return _isElevatedCache.Value;
                bool e = false;
                try
                {
                    using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                        e = new System.Security.Principal.WindowsPrincipal(id)
                            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { }
                _isElevatedCache = e;
                return e;
            }
        }

        // True when HID enumeration found a supported wheel (so Trueforce
        // effects play) but USBPcap discovery couldn't find it on the bus
        // (so FFB pass-through is broken, the game's own force feedback
        // gets clobbered). This is the smoking-gun divergence pattern that
        // motivates surfacing the manual-picker call to action prominently
        // rather than burying it in Diagnostics.
        //
        // Suppressed when:
        //   - HID hasn't found a wheel yet (nothing to diverge from)
        //   - User already has a manual override set (they've fixed it)
        //   - FFB tap is actually tapping (status starts with "Tapping")
        //   - USBPcap isn't installed (separate Browse/Reinstall UX handles it)
        public bool ShouldShowFfbTapPickerBanner
        {
            get
            {
                if (Settings == null) return false;
                if (_hidWheelVid == 0 && _hidWheelPid == 0) return false;
                if (HasManualUsbPcapDevice) return false;
                if (!IsUsbPcapAvailable) return false;
                string status = _ffbTap?.Status ?? "";
                if (status.StartsWith("Tapping", StringComparison.OrdinalIgnoreCase)) return false;
                // Only the "no supported wheel found" outcome warrants the
                // picker prompt. Other failure modes (USBPcap missing,
                // permission denied with explicit text, etc.) are surfaced
                // through Diagnostics; the picker won't help.
                return status.IndexOf("No supported wheel found", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>Why-is-my-wheel-quiet diagnostic. Walks a decision tree
        /// of plausible "no haptic output" causes and returns the most-
        /// blocking one as a single actionable line, or null when the
        /// plugin looks healthy. Surfaced in the settings UI as a warning
        /// hint below the status pill so users see the actual root cause
        /// instead of mentally combining five separate status fields.</summary>
        public string WheelQuietDiagnostic
        {
            get
            {
                if (Settings == null) return "Settings not loaded yet.";

                // 1. Hard master switch
                if (!Settings.PluginEnabled)
                {
                    if (!string.IsNullOrEmpty(_activeGame)
                        && Settings.GameEnabled != null
                        && Settings.GameEnabled.TryGetValue(_activeGame, out var ge)
                        && !ge)
                        return $"Plugin is disabled for '{_activeGame}'. Re-enable via the master switch (auto-remembers per game).";
                    return "Plugin is disabled. Click the 'Plugin enabled' checkbox at the top to turn it on.";
                }

                // 2. Master gain at zero
                if (Settings.MasterGain <= 0.005f)
                    return "Master gain is at 0. Slide it up in the Master section.";

                // 3. G HUB blocking wheel access. Ranks higher than "wheel not
                //    detected" because G HUB is the actual cause; surfacing the
                //    real fix saves the user a debugging detour.
                if (_isGHubRunning)
                    return "Logitech G HUB is running. It claims the wheel and blocks Trueforce. Close G HUB, then restart SimHub.";

                // 4. Wheel device state. WheelStatus is set by the discovery
                //    + open path; "Not detected" is the default.
                string wheel = WheelStatus ?? "";
                if (wheel.StartsWith("Not detected", StringComparison.OrdinalIgnoreCase))
                    return "Wheel not detected. Plug in your G PRO / RS50 / G923, or close any app that's holding the device exclusively.";
                if (wheel.StartsWith("Open failed", StringComparison.OrdinalIgnoreCase)
                 || wheel.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Wheel reports: {wheel}. Try unplugging and reconnecting the wheel.";

                // 5. HID stream state.
                string stream = StreamStatus ?? "";
                if (!stream.StartsWith("Streaming", StringComparison.OrdinalIgnoreCase))
                    return $"Wheel stream is '{stream}'. The plugin is opened but not actively driving the wheel, check the Diagnostics panel.";

                // 6. No game actually running. _activeGame can be a selected-
                //    but-closed profile, so gate on _currentGameName, which is
                //    only set while the process is up (data.GameRunning). Avoids
                //    falsely reporting a closed game as "detected but no telemetry".
                if (string.IsNullOrEmpty(_currentGameName))
                    return "No game running. Start a supported game and load into a session.";

                var src = _telemetrySource;
                double hz = src?.MeasuredHz ?? 0;
                if (hz <= 0)
                {
                    // Forza delivers telemetry over UDP. If nothing has EVER
                    // arrived this session it's a UDP-setup problem, not a pause,
                    // so point at the setup instead of the generic menu/paused
                    // line (which would just duplicate the UDP setup banner).
                    long udpPackets = -1;
                    if (src is TrueforceForAll.Core.ForzaUdpTelemetrySource fz) udpPackets = fz.PacketsReceived;
                    // Nothing has ever arrived: this is a UDP-setup case, owned
                    // by the dedicated setup banner (with its "Set up..." button).
                    // Stay silent here so we don't duplicate that with a text
                    // instruction; the button is the action.
                    if (udpPackets == 0)
                        return null;
                    return $"'{_currentGameName}' is detected but no telemetry is arriving. You may be in a menu or paused.";
                }

                // 7. All telemetry-driven effects disabled. Engine pulse,
                //    bumps, traction, gear, ABS, pit limiter, DRS, if all
                //    seven are off and audio capture is also off, nothing
                //    can produce output.
                bool anyEffectOn =
                       (EnginePulse  != null && EnginePulse.Enabled)
                    || (RoadBumps    != null && RoadBumps.Enabled)
                    || (TractionLoss != null && TractionLoss.Enabled)
                    || (GearShift    != null && GearShift.Enabled)
                    || (AbsClick     != null && AbsClick.Enabled)
                    || (PitLimiter   != null && PitLimiter.Enabled)
                    || (Drs          != null && Drs.Enabled)
                    || (_audio       != null && _audio.Enabled);
                if (!anyEffectOn)
                    return "Every effect channel is disabled. Enable at least one effect or turn on audio capture.";

                // 8. Audio capture configured-on but not actually attached
                //    to a game process. Common when the user enabled audio
                //    capture but didn't pick the game's process.
                if (_audio != null && _audio.Enabled && _audio.IsActive == false
                    && _audio.CapturedProcessId == 0
                    && !string.IsNullOrEmpty(_activeGame))
                {
                    return $"Audio capture is enabled but not attached to '{_activeGame}'. Pick the game process in the Audio section.";
                }

                // 9. Sidechain ducker over-aggressive (engine pulse muted
                //    near to silence). Detects misconfigured ducker depth
                //    that swallows everything.
                if (EnginePulse != null && EnginePulse.DuckMultiplier < 0.05f
                    && Settings.DuckDepth > 0.95f)
                {
                    return "Sidechain ducker is muting nearly all output. Try lowering Depth in the Sidechain ducking section.";
                }

                return null;   // healthy
            }
        }

        public AudioCaptureSource AudioCapture => _audio;

        // Live counters surfaced to the Performance tab for the underrun
        // readout. Pull these on the UI's polling timer.
        public long TfRingUnderruns      => _device?.UnderrunCount ?? 0;
        public long AudioRingGlitches    => _audio?.GlitchCount ?? 0;
        public int  CurrentTfRingSize    => _device?.RingCapacity ?? 0;
        public int  CurrentAudioRingSize => _audio?.RingCapacity ?? 0;

        public float MasterGain
        {
            get => _mixer.MasterGain;
            set { _mixer.MasterGain = value; if (Settings != null) Settings.MasterGain = value; }
        }

        // Raised when master gain is changed outside the settings panel (e.g. a
        // bound controller button via the Controls tab). An open SettingsControl
        // subscribes to keep its slider in step.
        public event Action MasterGainChangedExternally;

        // Master-gain min/max mirror the settings slider (Minimum=0, Maximum=2).
        private const float MasterGainMin = 0f;
        private const float MasterGainMax = 2f;

        // Per-press step for the bindable master-gain actions, persisted in
        // Settings and editable from the Controls tab. Clamped to [0.01, 0.5]
        // so a stored value can't make a press do nothing or slam the gain.
        public float MasterGainStep
        {
            get
            {
                float s = Settings?.MasterGainStep ?? 0.05f;
                if (s < 0.01f) s = 0.01f;
                if (s > 0.5f)  s = 0.5f;
                return s;
            }
            set { if (Settings != null) Settings.MasterGainStep = value; }
        }

        /// <summary>Nudge master gain by <paramref name="delta"/>, clamped to the
        /// slider's [0, 2] range. Applies live (mixer + Settings), persists, and
        /// raises MasterGainChangedExternally. Backs the bindable Controls-tab
        /// "master gain +/-" actions.</summary>
        public void NudgeMasterGain(float delta)
        {
            float cur = MasterGain;
            float next = cur + delta;
            if (next < MasterGainMin) next = MasterGainMin;
            if (next > MasterGainMax) next = MasterGainMax;
            if (Math.Abs(next - cur) < 0.0001f) return;
            MasterGain = next;
            PersistSettings();
            try { MasterGainChangedExternally?.Invoke(); } catch { }
        }

        // Current effective audio-capture gain (active car override if any, else
        // the global setting). Read by the home Feedback tile to mirror the
        // settings panel's Audio "Gain" slider.
        public float ActiveAudioGain => ActiveAudio?.Gain ?? 0f;

        // Apply an audio-capture gain live (settings object + running _audio
        // source) without persisting; the caller debounces the disk write. Mirrors
        // the settings panel's AudioGainSlider path (ActiveAudio + apply).
        public void SetActiveAudioGainLive(float v)
        {
            var a = ActiveAudio;
            if (a == null) return;
            a.Gain = v;
            ApplyAudioCaptureSettings(a);
        }

        // Whether audio haptics (the loopback-capture layer) are enabled for the
        // active settings. Read + toggled by the home Feedback tile's audio button.
        public bool ActiveAudioEnabled => ActiveAudio?.Enabled ?? false;

        public void SetActiveAudioEnabledLive(bool on)
        {
            var a = ActiveAudio;
            if (a == null) return;
            a.Enabled = on;
            ApplyAudioCaptureSettings(a);
        }

        // Persisted opt-in for the home-screen Feedback gain tile. Toggled by the
        // HOMEBOX access code. Applies live to the injector so the tile appears /
        // disappears without a SimHub restart.
        public void SetShowFeedbackBox(bool on)
        {
            if (Settings != null) Settings.ShowFeedbackBox = on;
            PersistSettings();
            _feedbackInjector?.SetEnabled(on);
        }

        public bool DebugToggleFeedbackBox()
        {
            bool on = !(Settings?.ShowFeedbackBox ?? false);
            SetShowFeedbackBox(on);
            return on;
        }

        public bool PluginEnabled => Settings?.PluginEnabled ?? true;

        /// <summary>Toggle the master enable. When disabled, sends the protocol
        /// Stop command so the wheel returns to its native FFB / Trueforce
        /// path (e.g. iRacing's own Trueforce takes over) and the producer
        /// loop skips rendering. When re-enabled, sends Start and resumes.
        /// If <paramref name="persistForActiveGame"/> is true and a game is
        /// detected, the choice is auto-remembered for that game.</summary>
        public void SetPluginEnabled(bool enabled, bool persistForActiveGame = true)
        {
            if (Settings == null) return;
            bool wasEnabled = Settings.PluginEnabled;
            Settings.PluginEnabled = enabled;

            if (persistForActiveGame && !string.IsNullOrEmpty(_activeGame))
            {
                Settings.GameEnabled[_activeGame] = enabled;
                this.SaveCommonSettings("GeneralSettings", Settings);
            }

            if (wasEnabled == enabled) return;
            if (!enabled)
            {
                _device?.SendStopCommand();
                _device?.Pause();
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Plugin disabled{(string.IsNullOrEmpty(_activeGame) ? "" : $" for '{_activeGame}'")}.");
            }
            else
            {
                _device?.Resume();
                _device?.SendStartCommand();
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Plugin enabled{(string.IsNullOrEmpty(_activeGame) ? "" : $" for '{_activeGame}'")}.");
            }
        }

        public void SetFfbScale(float v)
        {
            if (_device != null) _device.FfbScale = v;
            if (Settings != null) Settings.FfbScale = v;
        }

        public void SetFfbInvertSign(bool v)
        {
            if (_device != null) _device.FfbInvertSign = v;
            if (Settings != null) Settings.FfbInvertSign = v;
        }

        public void SetFfbSmoothMs(float v)
        {
            if (_device != null) _device.FfbSmoothTimeConstantMs = v;
            if (Settings != null) Settings.FfbSmoothTimeConstantMs = v;
        }

        public void SetFfbSpikeMaxLsbPerMs(float v)
        {
            if (_device != null) _device.FfbSpikeMaxLsbPerMs = v;
            if (Settings != null) Settings.FfbSpikeMaxLsbPerMs = v;
        }

        public void SetFfbPeakSoftLimitLsb(float v)
        {
            if (_device != null) _device.FfbPeakSoftLimitLsb = v;
            if (Settings != null) Settings.FfbPeakSoftLimitLsb = v;
        }

        public void SetFfbSpikeTamingEnabled(bool v)
        {
            if (_device != null) _device.FfbSpikeTamingEnabled = v;
            if (Settings != null) Settings.FfbSpikeTamingEnabled = v;
        }

        public void SetFfbSpikeUseSlewLimiter(bool v)
        {
            if (_device != null) _device.FfbSpikeUseSlewLimiter = v;
            if (Settings != null) Settings.FfbSpikeUseSlewLimiter = v;
        }

        public void SetSkipFfbPassthrough(bool v)
        {
            // Stored on Settings only; the FfbTargetProvider lambda reads it
            // each tick so the change takes effect immediately without
            // touching the device.
            if (Settings != null) Settings.SkipFfbPassthrough = v;
        }

        // Stationary-spring setters. Settings-only, like SkipFfbPassthrough:
        // the FfbTargetProvider lambda reads them every tick so changes apply
        // live. The UI persists via PersistSettings() after calling these.
        public void SetStationarySpringEnabled(bool v)
        { if (Settings != null) Settings.StationarySpringEnabled = v; }
        public void SetStationarySpringStrength(double v)
        { if (Settings != null) Settings.StationarySpringStrength = v; }
        public void SetStationarySpringCutoffKmh(double v)
        { if (Settings != null) Settings.StationarySpringCutoffKmh = v; }

        // Fast gate on the baseline FFB path: when the spring is off AND no
        // desk self-test is armed, return the game's FFB target untouched
        // WITHOUT entering the spring logic at all. Keeps the stationary
        // spring entirely out of the FFB regression surface for the common
        // (feature-off) case. One atomic read + a bool when off. ApplyStationary
        // Spring keeps its own equivalent guards too, so it's still correct if
        // called directly.
        private short? ApplyStationarySpringIfActive(short? gameTarget)
        {
            bool testArmed = System.Threading.Interlocked.Read(ref _springTestEndTicks) != 0;
            var s = Settings;
            if (!testArmed && (s == null || !s.StationarySpringEnabled))
                return gameTarget;
            return ApplyStationarySpring(gameTarget);
        }

        // Stationary-spring FFB floor. The plugin streams the game's own FFB
        // to the motor; a parked car produces ~0 self-aligning torque, so the
        // wheel goes limp. This tops the centering force up to a desired
        // magnitude that fades out by a low cutoff speed. It only ever fills a
        // DEFICIT in the game's same-direction force, never reduces or opposes
        // it, so it cannot fight the game's FFB and auto-disengages the moment
        // the game does its own centering or the car starts moving.
        //
        // Returns the (possibly augmented) target in the GAME's FFB sign space
        // (FfbInvertSign / FfbScale / spike taming are applied downstream in
        // TrueforceDevice, so the spring rides through the same chain).
        // No-ops, returning the input unchanged, when: disabled; iRacing
        // (MAIRA already produces real standstill weight, and iRacing is the
        // sanctioned MAIRA exception); the tap is stale (null, keepalive,
        // stay out of native FFB's way); steering is stale or unavailable
        // (any non-AC source); or already above the cutoff speed.
        private short? ApplyStationarySpring(short? gameTarget)
        {
            // SPRING desk self-test: while the deadline is in the future,
            // synthesize a centering force with a simulated wheel position
            // that flips direction every ~1.5 s, so the user feels the spring
            // push one way then the other without a game. Bypasses every
            // normal gate (enabled / speed / steering freshness / null tap).
            long springTestEnd = System.Threading.Interlocked.Read(ref _springTestEndTicks);
            if (springTestEnd != 0)
            {
                long nowSt = Stopwatch.GetTimestamp();
                if (nowSt >= springTestEnd)
                {
                    System.Threading.Interlocked.CompareExchange(ref _springTestEndTicks, 0, springTestEnd);
                }
                else
                {
                    var ts = Settings;
                    double remainSec  = (springTestEnd - nowSt) / (double)Stopwatch.Frequency;
                    double elapsedSec = SpringTestDurationSec - remainSec;
                    float steerT = ((int)(elapsedSec / 1.5) % 2 == 0) ? 0.8f : -0.8f;
                    float strengthT = Math.Max(0.4f, (float)(ts?.StationarySpringStrength ?? 1.0));
                    const float MaxLsbT = 32767f;
                    float ffbScaleT = _device != null ? _device.FfbScale : 1f;
                    if (ffbScaleT < 0.05f) ffbScaleT = 0.05f;
                    float desiredMagT = strengthT * Math.Abs(steerT) * MaxLsbT / ffbScaleT;
                    float dirT = (steerT > 0f) ? 1f : -1f;
                    int baseT = gameTarget ?? 0;
                    int resultT = baseT + (int)(desiredMagT * dirT);
                    if (resultT >  32767) resultT =  32767;
                    else if (resultT < -32768) resultT = -32768;
                    return (short)resultT;
                }
            }

            var s = Settings;
            if (s == null || !s.StationarySpringEnabled) return gameTarget;
            if (!gameTarget.HasValue) return gameTarget;
            if (string.Equals(_activeGame, "IRacing", StringComparison.Ordinal))
                return gameTarget;
            // Forza exclusion. The spring conflicted with Forza's force
            // feedback during the matchmaking-found transition and could
            // pull the wheel to the rotational stop, sometimes with violent
            // back-and-forth FFB against the stop. The precise interaction
            // wasn't isolated (direction is correct in normal use, and
            // several narrower gates were tried without reliably catching
            // the case), so the spring is bypassed at the source-type level
            // (user's call, 2026-05-28). The source-type check is more
            // robust than _activeGame string-matching: ForzaUdpTelemetry
            // Source is what feeds the lambda regardless of which Forza
            // title resolved the game id. Other sources unchanged.
            if (_telemetrySource is ForzaUdpTelemetrySource)
                return gameTarget;

            // Steering source. While the game is reporting steering, use ITS
            // value: it shares the game's own FFB reference frame, so the spring
            // and the game's forces agree and the feel stays smooth (AC reports
            // a high-rate steering angle whenever the sim is running, stationary
            // or moving, engine on or off). Switch to the wheel's PHYSICAL
            // position ONLY when the game has genuinely stopped reporting it:
            //   - game steering stale (sim paused / telemetry stopped), or
            //   - an authoritative-session source says we're paused even though
            //     frames keep coming (Forza zeros steering through the pre-race
            //     countdown / pause).
            // In those states there's no live game FFB to fight, so the physical
            // wheel position is safe. Using physical WHILE driving made it fight
            // the game's own FFB near center (different reference frames) -> jerky.
            long now = Stopwatch.GetTimestamp();
            long gameAge = now - System.Threading.Interlocked.Read(ref _lastSteerTicks);
            var src2 = _telemetrySource;
            bool authoritativePaused = (src2?.HasAuthoritativeSessionState ?? false)
                                       && !(src2?.IsSessionActive ?? true);
            bool gameStale = gameAge > SteerMaxAgeTicks;
            var sr = _steeringReader;
            bool physFresh = sr != null && sr.LastUpdateTicks != 0
                             && (now - sr.LastUpdateTicks) <= SteerMaxAgeTicks;
            float steerRaw;
            if (!gameStale && !authoritativePaused)
                steerRaw = _lastSteerNorm;          // game is tracking the wheel: use it (smooth)
            else if (physFresh)
                steerRaw = sr.SteerNorm;            // paused / countdown: physical position
            else if (!gameStale)
                steerRaw = _lastSteerNorm;          // no physical reader; fall back to game value
            else
                return gameTarget;                  // no steering at all

            float cutoff = (float)s.StationarySpringCutoffKmh;
            float speed  = _lastSpeedKmh;
            if (cutoff <= 0f || speed >= cutoff) return gameTarget;

            if (steerRaw >  1f) steerRaw =  1f;
            else if (steerRaw < -1f) steerRaw = -1f;

            // Low-pass the position so a coarse (8-bit) source doesn't notch
            // the force and so we interpolate between telemetry updates. We
            // only reach here while the spring is actively engaged, so the EMA
            // updates every provider call; a stale gap resumes from the last
            // value and re-converges within the time constant.
            _springSteerEma += SpringSteerEmaAlpha * (steerRaw - _springSteerEma);
            float steer = _springSteerEma;

            // Linear speed fade: full at standstill, zero at cutoff. The
            // deficit-fill below is the primary safety mechanism; this fade
            // is a belt-and-braces backstop so the spring is provably gone
            // by the time real cornering forces exist.
            float fade = 1f - (speed / cutoff);
            if (fade <= 0f) return gameTarget;

            // Desired centering magnitude (FFB LSB). Strength is a fraction of
            // full *felt* scale at full lock while parked; scales with |steer|
            // so there's no notch at center and it grows as you wind lock on.
            const float MaxLsb = 32767f;
            float desiredMag = (float)s.StationarySpringStrength
                             * Math.Abs(steer) * fade * MaxLsb;
            if (desiredMag < 1f) return gameTarget;

            // Pre-compensate the downstream FfbScale. Everything we return is
            // multiplied by FfbScale in TrueforceDevice, so without this the
            // user's FFB-strength trim (default 0.80) silently caps the spring
            // well below "full scale" and the strength slider can't reach a
            // meaningful ceiling. Dividing here makes strength=1.0 land at
            // true full motor torque regardless of FfbScale. Floor the divisor
            // so a tiny FfbScale can't explode the value (the ±32767 clamp
            // below is the real safety net either way).
            float ffbScale = _device != null ? _device.FfbScale : 1f;
            if (ffbScale < 0.05f) ffbScale = 0.05f;
            desiredMag /= ffbScale;

            // Re-centering direction in the game's FFB sign space. The
            // steer->FFB sign relationship is wheel/protocol AND source
            // dependent: confirmed correct on AC + G PRO (the un-inverted form
            // pushed away from center). Forza's Steer sign convention is
            // assumed to match AC's here but is a hardware-verify item; if it
            // re-centers the wrong way in Forza, the source should flip its
            // SteeringAngle sign rather than special-casing here.
            float dir = (steer > 0f) ? 1f : -1f;

            int g = gameTarget.Value;
            // Deficit-fill: only the game force already pushing the SAME way as
            // our centering counts toward the floor. Top it up to desiredMag,
            // never beyond, never against, so the spring can't fight the game's
            // own FFB and self-disengages once the game centers.
            float have = (Math.Sign((float)g) == Math.Sign(dir)) ? Math.Abs((float)g) : 0f;
            float add  = desiredMag - have;
            if (add <= 0f) return gameTarget;                // game already centers enough

            int result = g + (int)(add * dir);
            if (result >  32767) result =  32767;
            else if (result < -32768) result = -32768;
            return (short)result;
        }

        /// <summary>Set or clear the audio-capture exe override for a game.
        /// Pass null/whitespace to clear. Drops any currently-captured
        /// process so the next capture tick re-evaluates against the new
        /// override within ~1 s.</summary>
        public void SetAudioCaptureExeOverride(string game, string exe)
        {
            if (Settings == null || string.IsNullOrEmpty(game)) return;
            if (Settings.AudioCaptureExeOverrides == null)
                Settings.AudioCaptureExeOverrides = new Dictionary<string, string>();

            string trimmed = exe?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 4);

            if (string.IsNullOrEmpty(trimmed))
                Settings.AudioCaptureExeOverrides.Remove(game);
            else
                Settings.AudioCaptureExeOverrides[game] = trimmed;

            this.SaveCommonSettings("GeneralSettings", Settings);

            // Force a re-scan: drop the cached process so the next CaptureTick
            // doesn't fast-path the alive-check on the wrong process.
            var prev = System.Threading.Interlocked.Exchange(ref _capturedProcess, null);
            if (prev != null)
            {
                try { prev.Dispose(); } catch { }
                try { _audio?.Stop(); } catch { }
                try { _helperHost?.SetTargetPid(0); } catch { }
            }
        }

        /// <summary>The exe override currently configured for the active
        /// game (or null if none). Used by the UI to populate the textbox.</summary>
        public string ActiveCaptureExeOverride
        {
            get
            {
                if (string.IsNullOrEmpty(_activeGame) || Settings?.AudioCaptureExeOverrides == null) return null;
                return Settings.AudioCaptureExeOverrides.TryGetValue(_activeGame, out var v) ? v : null;
            }
        }

        /// <summary>Trigger an effect's test playback. Forces the device into
        /// active ep3 mode for the duration so the test is audible even when
        /// AC isn't running (no FFB tap data → would otherwise be keepalive).
        /// Drives effect.TestUpdate(phase) at ~60 Hz over the test window so
        /// effects can simulate dynamic behavior (RPM ramps, slip pulses, etc).</summary>
        public void TestEffect(TelemetryEffect effect)
        {
            if (effect == null)
            {
                SimHub.Logging.Current.Info("[Trueforce] TestEffect: effect was null");
                return;
            }
            if (_device == null)
            {
                SimHub.Logging.Current.Info($"[Trueforce] TestEffect '{effect.Name}': device not initialized");
                return;
            }
            int durationMs = effect.TestPlay();
            SimHub.Logging.Current.Info($"[Trueforce] TestEffect '{effect.Name}' duration={durationMs} ms");
            if (durationMs <= 0) return;

            _device.ForceActiveFor(durationMs + 200);

            long startTicks = DateTime.UtcNow.Ticks;
            long endTicks   = startTicks + durationMs * TimeSpan.TicksPerMillisecond;
            System.Threading.Interlocked.Increment(ref _activeTestTasks);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (DateTime.UtcNow.Ticks < endTicks && !_shuttingDown)
                    {
                        long now = DateTime.UtcNow.Ticks;
                        double elapsedMs = (now - startTicks) / (double)TimeSpan.TicksPerMillisecond;
                        double phase = Math.Min(1.0, Math.Max(0, elapsedMs / durationMs));
                        try { effect.TestUpdate(phase); } catch { }
                        Thread.Sleep(16);  // ~60 Hz update rate
                    }
                }
                catch { }
                finally
                {
                    // Clear any state TestPlay/TestUpdate latched (amplitudes,
                    // envelopes, hold timers) so it doesn't bleed into other
                    // effects on subsequent renders. Without this, e.g. ABS
                    // Pulse mode leaves _amp = ActiveAmp*Gain set after the
                    // test ends; with no telemetry to zero it back out (user
                    // is in the settings panel, no game running), the pulse
                    // keeps rendering and contaminates every later test.
                    try { effect.Reset(); } catch { }
                    System.Threading.Interlocked.Decrement(ref _activeTestTasks);
                }
            });
        }

        /// <summary>SPRING access-code test. Opens the device active path and
        /// arms the stationary-spring self-test window (see the synthetic
        /// branch in ApplyStationarySpring): the wheel pushes one way, then the
        /// other, every ~1.5 s for a few seconds, so the user can feel the
        /// spring's strength and force direction on the desk with no game.
        /// Engages regardless of whether the spring is enabled in settings.</summary>
        public void StartStationarySpringTest()
        {
            if (_device == null) return;
            int ms = (int)(SpringTestDurationSec * 1000);
            System.Threading.Interlocked.Exchange(
                ref _springTestEndTicks,
                Stopwatch.GetTimestamp() + (long)(SpringTestDurationSec * Stopwatch.Frequency));
            _device.ForceActiveFor(ms + 200);
        }

        /// <summary>REV access-code test. Exercises the rev limiter's real
        /// RPM-threshold + hold logic (not just the buzz playback the Test
        /// button gives) by feeding a synthetic redline sequence over a render
        /// window: silent below threshold, engaged buzz at/over redline, then
        /// silent once RPM drops off the limiter. Felt on the wheel with no
        /// game running (mirrors TestEffect's ForceActiveFor + drive loop).</summary>
        public void DebugRevLimiterBounce()
        {
            if (RevLimiter == null || _device == null) return;
            const double maxRpm = 8000.0;
            int durationMs = RevLimiter.StartRevTestWindow(5200);
            _device.ForceActiveFor(durationMs + 200);

            long start = DateTime.UtcNow.Ticks;
            long end   = start + durationMs * TimeSpan.TicksPerMillisecond;
            System.Threading.Interlocked.Increment(ref _activeTestTasks);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    while (DateTime.UtcNow.Ticks < end && !_shuttingDown)
                    {
                        double ms = (DateTime.UtcNow.Ticks - start) / (double)TimeSpan.TicksPerMillisecond;
                        // Below threshold (silent) -> over redline (buzz; the
                        // 16 ms re-feed keeps the 80 ms hold satisfied so it
                        // reads like sitting on the limiter) -> dropped off.
                        double rpm = (ms < 1500) ? maxRpm * 0.95
                                   : (ms < 4200) ? maxRpm * 0.99
                                   :               maxRpm * 0.62;
                        RevLimiter.DebugFeedRpm(rpm, maxRpm);
                        Thread.Sleep(16);
                    }
                }
                catch { }
                finally
                {
                    try { RevLimiter.Reset(); } catch { }
                    System.Threading.Interlocked.Decrement(ref _activeTestTasks);
                }
            });
        }

        /// <summary>FAULT access-code test: force the live device into the
        /// stream-fault state so the recovery watchdog (MaybeRecoverDevice)
        /// re-attaches it, exercising the "Stream lost - auto-reconnecting"
        /// status + transparent recovery without physically unplugging.</summary>
        public void DebugForceStreamFault() => _device?.DebugForceStreamFault();

        /// <summary>WHATSNEW access-code test: clear the changelog/seen state
        /// so the "What's new" banner and every per-effect NEW badge reappear.
        /// Resets to "0.0.0" so the banner shows the full history (enough to
        /// confirm the banner + the newest entry/badge render). The UI layer
        /// refreshes the banner + badges after calling this.</summary>
        public void DebugResetChangelogSeen()
        {
            if (Settings == null) return;
            Settings.SeenEffects = new System.Collections.Generic.List<string>();
            Settings.LastSeenVersion = "0.0.0";
            PersistSettings();
        }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Trueforce] Init: loading settings...");
            SimHub.Logging.Current.Info(
                "[Trueforce] Logitech processes at startup: " + SnapshotLogitechProcesses());
            // wasFreshInstall flips iff the factory ran, which only happens
            // when SimHub had no prior settings file for us, the cleanest
            // signal for "this is a first-run install" that the SimHub
            // ReadCommonSettings API gives us.
            bool wasFreshInstall = false;
            Settings = this.ReadCommonSettings("GeneralSettings", () => { wasFreshInstall = true; return new TrueforceSettings(); });
            // Defensive nulls in case a pre-2.x settings file was deserialized
            // without the new dictionaries.
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.GameEnabled  == null) Settings.GameEnabled  = new Dictionary<string, bool>();
            // One-time folder restructure: move the three legacy sibling
            // folders (TrueforceForAll-Presets / -Library / -Imports) into the
            // collapsed PluginsData\Common\TrueforceForAll\{factory,user,user\import}
            // layout. Idempotent; skipped once stamped. Runs BEFORE the stores
            // load so they read from the new location on the first new launch.
            if (!Settings.FoldersRestructuredV3)
            {
                RestructureFoldersIfNeeded();
                Settings.FoldersRestructuredV3 = true;
            }
            // Load the file-based built-in presets (folder override -> shipped
            // default) before anything seeds or queries them below.
            BuiltinPresets.Initialize(Settings.BuiltinPresetsFolder);
            // Load the user-library folder (where user-saved presets live as
            // files; mirrors the built-in folder layout). Defaults under
            // PluginsData/Common, user-writable.
            UserPresets.Initialize(Settings.UserLibraryFolder);
            // One-time migration: legacy in-dict user presets -> user-library
            // files. Backs the settings file up first. The migration's
            // skip-factory-name rule (IsFactoryBuiltinName, currently-shipped
            // OR RetiredBuiltinNames) drops factory entries on the floor so
            // they never land in the library; the user library carries only
            // genuinely-user content from here on. Skipped once stamped.
            if (!Settings.PresetsMigratedV2)
            {
                MigrateLegacyUserPresetsToFolder();
                Settings.PresetsMigratedV2 = true;
            }
            // One-time car migration: legacy TrueforceCars/*.tfcar.json ->
            // user library cars/ tree + Settings.CarDefaults -> car-defaults.json.
            // Separate flag from the game migration because it shipped later;
            // users who already migrated games still need this on first run.
            if (!Settings.CarsMigratedV2)
            {
                MigrateLegacyUserCarsToFolder();
                Settings.CarsMigratedV2 = true;
            }
            // Housekeeping: an earlier build of the car migration named its
            // backup folder "TrueforceCars.bak-...". Rename any leftover to the
            // on-brand name so users don't see a bare "Trueforce" folder. Idem-
            // potent; runs every Init and no-ops once everything's renamed.
            RebrandLegacyCarsBackup();
            if (Settings.Performance  == null) Settings.Performance  = new PerformanceSettings();
            if (Settings.Forza        == null) Settings.Forza        = new ForzaSettings();
            if (Settings.SeenEffects  == null) Settings.SeenEffects  = new List<string>();
            if (Settings.CarCylinderCache == null)
                Settings.CarCylinderCache = new Dictionary<string, Dictionary<string, int>>();
            if (Settings.GamesWithRedline == null)
                Settings.GamesWithRedline = new HashSet<string>();
            // Scrub any Forza title an earlier build wrongly learned as a
            // redline game (its per-car redline can pass the sanity gate, but
            // Forza has no precise rev limit). Self-heals affected settings so
            // the engage-% control comes back without the user resetting.
            Settings.GamesWithRedline.RemoveWhere(IsForzaGameName);

            // One-time re-validation: builds before the realRedline split faked
            // a redline from MaxRpm, so the learning gate accepted EVERY
            // non-Forza game the user drove (MaxRpm always passes the
            // 0.5..1.02x gate against itself). Those stale entries would now
            // hide the engage-% control for games that actually take the
            // percentage path. Clear the set once so it re-learns under the new
            // rule (only games reporting a real CarSettings_RedLineRPM qualify).
            if (!Settings.GamesWithRedlineRevalidated)
            {
                Settings.GamesWithRedline.Clear();
                Settings.GamesWithRedlineRevalidated = true;
                try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            }

            // One-time: the home-screen gain tile is now on by default. Flip it
            // on for existing installs that predate that change, once, so an
            // update lights it up. A later user opt-out (Settings toggle) sticks
            // because the latch is already set.
            if (!Settings.FeedbackBoxDefaultedOn)
            {
                Settings.ShowFeedbackBox = true;
                Settings.FeedbackBoxDefaultedOn = true;
                try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            }

            // One-shot v0.1.21 -> v0.1.22 migration: clear any saved manual
            // USBPcap override. Stale pins (the wheel later moved to a
            // different address / USBPcap interface) caused users to report
            // FFB stopped working after an update when in fact the override
            // was pointing at thin air (issue #17). Auto-discovery +
            // identity-based self-heal handle the realistic failure modes
            // for almost every user, so we lean on those by default. A user
            // who genuinely needs to pin can flip the UI back with the
            // MANUALPIN access code and re-pick. The latch prevents this
            // from re-clearing a deliberate post-migration pin.
            if (!Settings.ManualOverrideClearedV0_1_22)
            {
                bool hadOverride = !string.IsNullOrEmpty(Settings.ManualUsbPcapInterface)
                                || Settings.ManualUsbPcapDeviceAddress > 0;
                if (hadOverride)
                {
                    SimHub.Logging.Current.Info(
                        "[Trueforce] Migration: cleared saved manual USBPcap override " +
                        $"({Settings.ManualUsbPcapInterface} dev {Settings.ManualUsbPcapDeviceAddress}); " +
                        "auto-discovery now picks the wheel. Re-enable manual pinning with the MANUALPIN access code if needed.");
                    Settings.ManualUsbPcapInterface = "";
                    Settings.ManualUsbPcapDeviceAddress = 0;
                    Settings.ManualUsbPcapVid = 0;
                    Settings.ManualUsbPcapPid = 0;
                }
                Settings.ManualOverrideClearedV0_1_22 = true;
                try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            }

            // Fresh install (factory ran) or first run on a settings file
            // written before the badge feature existed (LastSeenVersion never
            // stamped): pre-seed every known effect as already-seen and
            // stamp LastSeenVersion to the running build. Without this, an
            // existing user upgrading from a pre-feature version would get
            // badges on every effect they've already been using, useless
            // noise. After this seed, badges only ever fire for effects
            // introduced in versions strictly newer than this one.
            if (wasFreshInstall || string.IsNullOrEmpty(Settings.LastSeenVersion))
            {
                foreach (var id in EffectChangelog.KnownEffectIds)
                {
                    if (!Settings.SeenEffects.Contains(id)) Settings.SeenEffects.Add(id);
                }
                Settings.LastSeenVersion = CurrentVersionString();
                this.SaveCommonSettings("GeneralSettings", Settings);
            }

            // Hand the resolver a reference to the persisted cache. New heuristic
            // hits get written through and flushed to disk on the next settings
            // save. Version mismatch (heuristic improvement) clears the cache.
            int cacheVer = Settings.CarCylinderCacheVersion;
            CarCylinderResolver.AttachPersistentCache(Settings.CarCylinderCache, ref cacheVer);
            Settings.CarCylinderCacheVersion = cacheVer;
            // One-time migration: promote the cylinder-only CarCylinderCache
            // entries into seed Stock EngineVariants in Settings.CarFacts so
            // the new apply path has something to work with on day one.
            // Idempotent; flag-gated.
            if (!Settings.CarFactsMigratedV1)
            {
                MigrateCarCylinderCacheToCarFacts();
                Settings.CarFactsMigratedV1 = true;
            }
            MigrateLegacyGamePresets();
            MigrateSpikeTamingFlag();
            InstallBuiltinPresetsIfMissing();

            // Per-car file store: load files into Settings.CarOverrides
            // (file wins on conflict), then migrate any existing
            // Settings.CarOverrides / preset.CarOverrides into files for
            // cars that don't already have one. Files become the canonical
            // store; Settings.CarOverrides is now an in-memory cache only.
            // Car preset store points at the user library folder (the cars/
            // subfolder under TrueforceForAll-Library). Built-in cars live in
            // BuiltinPresets and merge in via LoadAndMigrateCarPresets.
            _carStore = new CarPresetStore(
                () => UserPresets.CurrentFolder,
                msg => SimHub.Logging.Current.Info(msg));
            _installedPacks = new InstalledPacksStore(
                () => UserPresets.CurrentFolder,
                msg => SimHub.Logging.Current.Info(msg));
            LoadAndMigrateCarPresets();

            // One-time cleanup: walk user/games + user/cars looking for
            // files that are leftovers from before the file-based factory
            // (commit c89c3f7 era). Game match = IsFactoryBuiltinName
            // (current built-in OR retired-name list); car match =
            // (carId, presetName) tuple exists in factory's CarPresetJsons,
            // OR the file's own IsBuiltin tag is true, OR presetName
            // matches IsFactoryBuiltinName. Archives matches to a backup
            // folder + drops the matching entries from user/game-defaults
            // and user/car-defaults so the factory seed takes over.
            //
            // Runs AFTER LoadAndMigrateCarPresets so user/cars reflects
            // the full set of user-tier car files (step 1 of that method
            // creates files from legacy Settings.CarOverrides). Flag
            // stamps so it runs exactly once.
            if (!Settings.LegacyBuiltinsCleanedV1)
            {
                CleanupLegacyBuiltinsInUserLibrary();
                Settings.LegacyBuiltinsCleanedV1 = true;
                // Re-run the car cache rebuild so Settings.CarDefaults
                // reflects the post-cleanup state (factory seed for
                // bindings whose user-side entry just got dropped).
                LoadAndMigrateCarPresets();
            }
            MigrateEngineHighRpmHelpersDefaults();
            // One-shot: bump rev-limiter engage threshold 0.97 -> 0.85 for
            // presets still on the old default (issue #8). Runs after the car
            // store loads so .tfcar.json files are migrated too. Latched so a
            // user who later re-picks 0.97 keeps it.
            if (!Settings.RevLimiterThresholdDefaultMigrated)
            {
                MigrateRevLimiterThresholdDefault();
                Settings.RevLimiterThresholdDefaultMigrated = true;
                try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            }

            // Make sure all three folders exist with their READMEs, then auto-
            // import anything the user dropped into the imports folder. All
            // best-effort: folder access errors degrade gracefully.
            // One-time: the imports inbox was renamed from "drop" to "import";
            // move a leftover default "drop" folder to the new name.
            try
            {
                string legacyDropFolder = Path.Combine(UserPresets.DefaultFolder, "drop");
                string newImportFolder  = Path.Combine(UserPresets.DefaultFolder, UserImportsFolderName);
                if (Directory.Exists(legacyDropFolder) && !Directory.Exists(newImportFolder))
                    Directory.Move(legacyDropFolder, newImportFolder);
            }
            catch { /* best-effort rename */ }
            WriteReadmeIfMissing(BuiltinPresets.CurrentFolder, BuiltinReadmeText);
            WriteReadmeIfMissing(UserPresets.CurrentFolder, UserLibraryReadmeText);
            WriteReadmeIfMissing(UserImportsFolderPath, ImportsReadmeText);
            ImportFromUserImportsFolder();

            _mixer.MasterGain = Settings.MasterGain;

            // Start the GitHub update poller BEFORE the wheel-discovery early
            // exit so a user whose wheel is unplugged (or whose G HUB is
            // holding the HID) can still discover that a fix shipped. Without
            // this, the plugin returns out of Init below and _updateChecker
            // stays null, so the in-panel banner + Check-for-updates button
            // are dead. The check itself doesn't touch wheel state.
            _updateCheckerCts = new System.Threading.CancellationTokenSource();
            _updateChecker = new UpdateChecker
            {
                Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
            };
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await _updateChecker.CheckAsync(_updateCheckerCts.Token); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Update check task crashed: {ex.Message}");
                }
            });

            // Bring up the wheel (discover -> open -> init -> FFB tap ->
            // stream). Extracted so the recovery watchdog (MaybeRecoverDevice)
            // can re-run exactly this on a hot-replug / G-HUB-closed / init
            // retry. If it fails now we DON'T bail out of Init: the rest of
            // the plugin (telemetry, effects, audio capture, capture-poll
            // thread) still comes up so the watchdog only has to re-attach
            // the device, not reconstruct the whole pipeline.
            if (!TryBringUpDevice())
                SimHub.Logging.Current.Warn(
                    "[Trueforce] Wheel not ready at startup; the plugin will "
                    + "keep retrying automatically (replug the wheel / close G HUB).");

            InitPipeline();

            // Bindable actions for SimHub's Controls system. Surfaced in our
            // Controls tab via embedded ControlsEditor widgets, and also in
            // SimHub's global Controls & Events list. Each press nudges master
            // gain by the slider's small-step (0.05), clamped to [0, 2].
            // Wrapped so a SimHub API hiccup can't abort Init.
            try
            {
                pluginManager.AddAction("TrueforceForAll.MasterGainUp", GetType(),
                    (pm, a) => NudgeMasterGain(+MasterGainStep), (pm, a) => { });
                pluginManager.AddAction("TrueforceForAll.MasterGainDown", GetType(),
                    (pm, a) => NudgeMasterGain(-MasterGainStep), (pm, a) => { });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Trueforce] AddAction (master gain) failed: {ex.Message}");
            }
        }

        // Discover the wheel, open it, run the init sequence, start the FFB
        // tap and the 1 kHz stream. Returns true on success. Safe to call
        // repeatedly: the recovery watchdog calls this after CleanupDevice()
        // when the stream has faulted or no wheel was present yet. The
        // producer thread is plugin-lifetime and reused across re-attaches,
        // it's only created on the first successful bring-up.
        private bool TryBringUpDevice()
        {
            if (_shuttingDown) return false;

            SimHub.Logging.Current.Info("[Trueforce] Discovering wheel...");
            var matches = WheelDiscovery.FindAll();
            if (matches.Count == 0)
            {
                WheelStatus = "Not detected (open/close G HUB once, then restart SimHub)";
                SimHub.Logging.Current.Warn(
                    "[Trueforce] No supported wheel found. Make sure a supported wheel " +
                    "(G PRO / RS50 / G923) is plugged in. If it is, open G HUB once and let it " +
                    "finish detecting the wheel, then close G HUB (it must stay closed while this " +
                    "plugin runs) and restart SimHub.");
                LogDiscoveryDiagnostic();
                return false;
            }

            var match = matches[0];
            _hidWheelVid = match.Vid;
            _hidWheelPid = match.Pid;
            WheelStatus = $"{match.Model}  (VID 0x{match.Vid:X4}, PID 0x{match.Pid:X4})"
                        + (match.Unverified ? "  [unconfirmed model]" : "");
            SimHub.Logging.Current.Info($"[Trueforce] Found {WheelStatus}.");

            // Unverified PIDs: resolve + stream by inference from the shared
            // HID++ family, but not hardware-tested. None today (every supported
            // PID is owner-confirmed), so this is dormant until a new wheel is
            // added on inference alone. Surface a
            // notice asking the user to report the one failure mode we can't
            // rule out (init/handshake divergence: Trueforce effects play but
            // game FFB pass-through stays silent).
            if (match.Unverified)
            {
                _unverifiedWheelNotice =
                    $"{match.Model} is supported by inference but not hardware-tested. " +
                    "If Trueforce effects work but your game's force feedback is silent, " +
                    "please report it (Feedback > Report an issue, attach Export logs).";
                SimHub.Logging.Current.Warn($"[Trueforce] {_unverifiedWheelNotice}");
            }
            else
            {
                _unverifiedWheelNotice = null;
            }

            try
            {
                _device = new TrueforceDevice(match.Device);
                _device.Open();

                // Init sequence is required: empirically, skipping it leaves the
                // wheel in slower-default-rate mode and Trueforce response is
                // noticeably delayed (~game tick of latency). It does NOT cause
                // the FFB-suppression problem either way, diagnosed 2026-05-03.
                SimHub.Logging.Current.Info("[Trueforce] Sending init sequence (68 packets x 2)...");
                _device.RunInitSequence();

                // Spawn the USBPcap FFB tap. Reads AC's outgoing HID++ FFB target
                // off the bus and feeds it to TrueforceDevice so we can mirror it
                // into ep3 bytes 6-9, without this, our ep3 stream overrides AC's
                // FFB with zero motor torque whenever Trueforce content plays.
                // Override precedence: env var > persisted manual picker > auto.
                // The persisted picker exists because USBPcap's descriptor-cache
                // can go stale for hot-plugged wheels, leaving auto-discovery
                // unable to find a wheel that HID enumeration sees fine.
                // MAIRA auto-link: TF4ALL always watches for MAIRA's shared
                // memory. When the user flips MAIRA's "Pass FFB signal through
                // TF4ALL" toggle, MAIRA starts publishing and stops sending PID
                // to the wheel; we detect that and prefer the shared-memory FFB
                // (and drive the LEDs), no separate TF4ALL toggle needed. When
                // MAIRA isn't passing through, the map is absent and we fall
                // back to the USBPcap FFB tap exactly as before. The legacy
                // USBPcap path is always set up as that fallback.
                bool mairaAutoLink = Settings == null || Settings.MairaFfbPassthrough;
                if (mairaAutoLink)
                {
                    // MAIRA passes FFB only. LEDs are driven by TF4ALL's
                    // normal SimHub telemetry path (DispatchFrame ->
                    // RpmLedController), the accurate per-car implementation;
                    // no PID on the HID++ pipe in this mode so it's safe.
                    _mairaIpc = new MairaIpcSource(msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"));
                }

                var (ifaceOverride, devOverride) = ResolveUsbPcapOverride();
                _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride, Settings?.UsbPcapCmdPathOverride)
                {
                    Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                    HostElevated = IsRunningElevated,
                };
                _ffbTap.SetHidDiscoveredWheel(match.Vid, match.Pid);
                _ffbTap.SetOverrideIdentity((ushort)(Settings?.ManualUsbPcapVid ?? 0), (ushort)(Settings?.ManualUsbPcapPid ?? 0));
                WireFfbTapCallbacks(_ffbTap);
                ApplyUsbBytesLoggingSetting();

                // Physical steering reader for the stationary spring (works when
                // the game reports no steering, e.g. a Forza countdown / pause).
                try
                {
                    _steeringReader = new WheelSteeringReader(match.Vid, match.Pid)
                    {
                        Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                    };
                    _steeringReader.Start();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Steering reader failed to start: {ex.Message}");
                    _steeringReader = null;
                }
                _device.FfbTargetProvider = () =>
                {
                    // Prefer MAIRA shared-memory FFB when it's live (its toggle
                    // is on and it's publishing). Scoped to the iRacing profile
                    // only , MAIRA is an iRacing app, and we don't want a stale
                    // map to hijack FFB in other games. No PID is on the HID++
                    // pipe in this mode, so LEDs + FFB coexist.
                    if (string.Equals(_activeGame, "IRacing", StringComparison.Ordinal))
                    {
                        var fromMaira = _mairaIpc?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs);
                        if (fromMaira.HasValue) return fromMaira;
                    }

                    // Pause release (issue #13). When the game is paused / in a
                    // menu, replaying the last captured force is wrong: the FFB
                    // tap keeps returning that value for up to FfbTargetMaxAgeMs
                    // (10 s), and with no self-aligning torque on a stationary
                    // car a held force walks the wheel to its rotational stop
                    // ("snaps to full lock"). Return 0 (cur = 0x8000, zero motor
                    // force) so the wheel goes free instead.
                    //   - An authoritative pause flag (Forza IsRaceOn) is the
                    //     reliable case: telemetry keeps flowing during a Forza
                    //     pause, so only the flag tells us we're paused.
                    //   - MeasuredHz <= 0 covers games that simply stop sending
                    //     telemetry when paused, for any source.
                    // We do NOT release on the physics proxy alone, since it
                    // also reads inactive at a legitimate standstill (grid,
                    // stall), which would drop FFB mid-session.
                    var src = _telemetrySource;
                    if (src != null && !src.IsSessionActive
                        && (src.HasAuthoritativeSessionState || src.MeasuredHz <= 0))
                    {
                        // Paused / menu / pre-race countdown / race-end results.
                        // Earlier versions tried a SHORT-freshness pass-through
                        // here on the theory that paused games either stop
                        // sending FFB (so the tap goes stale and we release) or
                        // send a gentle centering force we should preserve. FH6
                        // does neither: it keeps re-emitting FFB packets during
                        // pause / results / pre-race transitions that often
                        // carry the frozen pre-pause force (the wheel position
                        // at the moment the pause hit, which during cornering
                        // is a hard force pegged to one side). The tap
                        // re-timestamps on every captured packet regardless of
                        // value, so the freshness check never expired and we
                        // streamed that force as cur on ep3, and the wheel
                        // snapped to or sat on full lock until the user resumed.
                        //
                        // We also intentionally SKIP the stationary-spring
                        // layer here. The spring is designed for a STATIONARY
                        // car during an ACTIVE driving session (e.g. AC's
                        // pre-race grid). It conflicts with Forza's FFB across
                        // pause-state transitions; we addressed that with a
                        // source-type exclusion in ApplyStationarySpring, and
                        // bypassing it here keeps the wheel truly free during
                        // pause-release for every other source as well.
                        return (short?)0;
                    }

                    // SkipFfbPassthrough: return Some(0) so the device sends
                    // active packets (audio plays) with cur = 0x8000. The
                    // wheel uses cur as motor torque and IGNORES ep0 once
                    // active packets are streaming, so this means zero motor
                    // force from the FFB-target path. Only correct for games
                    // that drive the wheel's motor through their own native
                    // ep3 path (Forza Horizon, AC Rally, iRacing); for games
                    // that rely on ep0 (vanilla AC, F1, PC2), this kills FFB.
                    if (Settings != null && Settings.SkipFfbPassthrough)
                        return ApplyStationarySpringIfActive((short?)0);
                    return ApplyStationarySpringIfActive(_ffbTap?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs));
                };
                _device.FfbScale                 = Settings.FfbScale;
                _device.FfbInvertSign            = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs  = Settings.FfbSmoothTimeConstantMs;
                _device.FfbSpikeTamingEnabled    = Settings.FfbSpikeTamingEnabled;
                _device.FfbSpikeUseSlewLimiter   = Settings.FfbSpikeUseSlewLimiter;
                _device.FfbSpikeMaxLsbPerMs      = Settings.FfbSpikeMaxLsbPerMs;
                _device.FfbPeakSoftLimitLsb      = Settings.FfbPeakSoftLimitLsb;

                // Apply persisted ring capacity. Sanitize: clamp to allowed
                // range and force pow2 so a hand-edited settings file can't
                // crash the plugin. Settings is updated back so the UI sees
                // the same value the device runs on.
                Settings.Performance.TfRingSize = SanitizePow2(
                    Settings.Performance.TfRingSize,
                    TrueforceDevice.MinRingSize, TrueforceDevice.MaxRingSize, TrueforceDevice.DefaultRingSize);
                _device.SetRingCapacity(Settings.Performance.TfRingSize);

                _ffbTap?.Start();

                _device.StartStream();

                // The producer is plugin-lifetime and reads the _device field
                // each iteration, so a re-attach reuses the existing thread
                // (it idles harmlessly while _device is null between attempts,
                // see ProducerLoop). Only create it on the first bring-up, or
                // if it somehow died, never a second concurrent producer.
                if (_producerThread == null || !_producerThread.IsAlive)
                {
                    _producerThread = new Thread(ProducerLoop)
                    {
                        IsBackground = true,
                        Name = "TrueforceProducer",
                        Priority = ThreadPriority.AboveNormal,
                    };
                    _producerThread.Start();
                }

                _streamStatus = "Streaming (1 kHz, 250 packets/s)";
                SimHub.Logging.Current.Info("[Trueforce] Stream started.");
            }
            catch (Exception ex)
            {
                _streamStatus = $"Init failed: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Init failed", ex);
                CleanupDevice();
                return false;
            }
            return true;
        }

        // Verbose diagnostic for the "wheel not detected" state: dumps what HidSharp
        // currently sees on the Logitech VID so a recurrence in the field tells us
        // whether the wheel is genuinely absent, present without its MI_02 Trueforce
        // interface (G HUB holding it / a partial enumeration), or in console mode
        // (wheel-like name, unsupported PID). Throttled to once per minute by
        // _lastDiscoveryDiagTicks; the recovery watchdog retries every few seconds.
        private void LogDiscoveryDiagnostic()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastDiscoveryDiagTicks != 0
                && now - _lastDiscoveryDiagTicks < Stopwatch.Frequency * 60)
                return;
            _lastDiscoveryDiagTicks = now;

            try
            {
                int total = 0, mi02 = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var dev in HidSharp.DeviceList.Local.GetHidDevices(WheelDiscovery.LogitechVid))
                {
                    total++;
                    int pid; try { pid = dev.ProductID; } catch { pid = 0; }
                    string path; try { path = dev.DevicePath ?? ""; } catch { path = ""; }
                    bool isMi02 = path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMi02) mi02++;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append($"0x{pid:X4}").Append(isMi02 ? " [MI_02]" : "");
                }
                string hids = total == 0 ? "none"
                    : $"{total} interface(s) [MI_02 present: {mi02 > 0}] PIDs {sb}";

                int consoleMode = 0;
                try { consoleMode = WheelDiscovery.FindUnsupportedWheelLike().Count; } catch { }
                int noTfIface = 0;
                try { noTfIface = WheelDiscovery.FindSupportedWithoutTrueforceInterface().Count; } catch { }

                SimHub.Logging.Current.Info(
                    "[Trueforce] Discovery diagnostic (wheel not found): "
                    + $"Logitech HID = {hids}; supported-wheel-without-MI_02 = {noTfIface}; "
                    + $"wheel-like-unsupported-PID (console mode?) = {consoleMode}; "
                    + $"G HUB UI running = {_isGHubRunning}.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Trueforce] Discovery diagnostic failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // One-time pipeline setup: loopback helper, audio capture, telemetry
        // effects, telemetry source, capture-poll thread. Split out of Init so
        // device bring-up can fail and retry independently. Runs exactly once
        // from Init regardless of whether the wheel came up; the recovery
        // watchdog only re-attaches the device (TryBringUpDevice), never this,
        // so effects/audio/telemetry are constructed a single time and simply
        // start producing the moment the device re-attaches.
        private void InitPipeline()
        {
            // Spawn the loopback helper child process. It does the actual
            // per-process WASAPI loopback in modern .NET (where COM interop is
            // reliable), and streams audio bytes back to us over stdout.
            try
            {
                string pluginDir = System.IO.Path.GetDirectoryName(typeof(TrueforcePlugin).Assembly.Location);
                string helperExe = System.IO.Path.Combine(pluginDir, "TrueforceForAll.LoopbackHelper.exe");
                _helperHost = new HelperHost(helperExe);
                _helperHost.Spawn();
                SimHub.Logging.Current.Info($"[Trueforce] Loopback helper spawned ({helperExe}).");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[Trueforce] Failed to spawn loopback helper", ex);
                _helperHost = null;
            }

            // Audio capture: create the source, attach it to the helper, hook
            // it into the mixer. Capture stays inactive (silent) until the poll
            // thread detects a sim and tells the host to retarget.
            _audio = new AudioCaptureSource
            {
                Enabled = Settings.AudioCapture.Enabled,
                Gain    = Settings.AudioCapture.Gain,
            };
            Settings.Performance.AudioRingSize = SanitizePow2(
                Settings.Performance.AudioRingSize,
                AudioCaptureSource.MinRingSamples, AudioCaptureSource.MaxRingSamples, AudioCaptureSource.DefaultRingSamples);
            _audio.SetRingCapacity(Settings.Performance.AudioRingSize);
            if (_helperHost != null) _audio.Attach(_helperHost);
            _mixer.Add(_audio);

            // Telemetry effects: instantiate from settings, register in the
            // mixer in display order. Each effect is fed via the active
            // ITelemetrySource's OnFrame callback (see DispatchFrame below).
            EnginePulse  = new EnginePulseEffect();
            RoadBumps    = new RoadBumpsEffect();
            TractionLoss = new TractionLossEffect();
            GearShift    = new GearShiftEffect();
            AbsClick     = new AbsClickEffect();
            PitLimiter   = new PitLimiterEffect();
            Drs          = new DrsEffect();
            Collision    = new CollisionEffect();
            RevLimiter   = new RevLimiterEffect();
            Airborne     = new AirborneEffect();
            // Airborne is last: it's a coordinator, not a voice, but it still
            // needs OnTelemetry (to read frame.Airborne) and Reset, both of
            // which the plugin fans out over _effects. Its no-op RenderAdd in
            // the mixer costs nothing.
            _effects = new TelemetryEffect[] { EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick, PitLimiter, Drs, Collision, RevLimiter, Airborne };
            foreach (var fx in _effects) _mixer.Add(fx);

            _rpmLeds = new RpmLedController(msg => SimHub.Logging.Current.Info(msg));
            // Pull initial values from globals (no car detected yet).
            ApplyActiveCarOverride();

            // Telemetry source: SimHub fallback initially. The first DataUpdate
            // tick triggers the game-change block (since _activeGame starts
            // null) and SwapTelemetrySource picks an enhanced source if the
            // running game has one.
            _simHubSource = new SimHubTelemetrySource { OnFrame = DispatchFrame };
            _simHubSource.Start();
            _telemetrySource = _simHubSource;
            SimHub.Logging.Current.Info($"[Trueforce] Telemetry source: {_telemetrySource.Name}.");

            _capturePollThread = new Thread(CapturePollLoop)
            {
                IsBackground = true,
                Name = "TrueforceCapturePoll",
            };
            _capturePollThread.Start();
            SimHub.Logging.Current.Info("[Trueforce] Audio capture armed; waiting for a supported game to start.");

            // Optional, default-off: splice a Trueforce gain tile into SimHub's
            // home "Feedback" section. Self-retrying + defensive; if the home tab
            // isn't realized yet (or the layout differs), it just won't appear.
            _feedbackInjector = new FeedbackBoxInjector(this);
            _feedbackInjector.Start();
        }
        private System.Threading.CancellationTokenSource _updateCheckerCts;
        public System.Threading.CancellationToken UpdateCheckerToken
            => _updateCheckerCts?.Token ?? System.Threading.CancellationToken.None;

        // ---- "NEW" badges + changelog banner ----

        /// <summary>Plugin assembly version in ToString(3) form ("X.Y.Z").
        /// Used as the stamp on Settings.LastSeenVersion + as the upper
        /// bound of the pending-changelog comparison.</summary>
        public static string CurrentVersionString()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? v.ToString(3) : "0.0.0";
        }

        /// <summary>True iff <paramref name="effectId"/> hasn't been seen by
        /// the user yet. Drives the per-section "NEW" badge in the settings
        /// UI. Defensive: returns false for null/unknown IDs so the UI can
        /// query freely without guarding.</summary>
        public bool IsEffectUnseen(string effectId)
        {
            if (Settings?.SeenEffects == null || string.IsNullOrEmpty(effectId)) return false;
            return !Settings.SeenEffects.Contains(effectId);
        }

        /// <summary>Record that the user has seen / interacted with the
        /// given effect; the "NEW" badge stops showing. Idempotent.
        /// Persists settings only on an actual state change so the chatty
        /// per-slider call site doesn't write the file on every value tick.</summary>
        public void MarkEffectSeen(string effectId)
        {
            if (Settings == null || string.IsNullOrEmpty(effectId)) return;
            if (Settings.SeenEffects == null) Settings.SeenEffects = new List<string>();
            if (Settings.SeenEffects.Contains(effectId)) return;
            Settings.SeenEffects.Add(effectId);
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>Returns every changelog version strictly newer than the
        /// user's stamped LastSeenVersion. Empty = nothing to surface; the
        /// banner stays hidden.</summary>
        public IReadOnlyList<ChangelogVersion> GetPendingChangelog()
        {
            if (Settings == null) return Array.Empty<ChangelogVersion>();
            EffectChangelog.TryParseVersion(Settings.LastSeenVersion, out var since);
            return EffectChangelog.EntriesNewerThan(since);
        }

        /// <summary>True when LastSeenVersion is strictly older than the
        /// running plugin's assembly version. Drives the What's new banner's
        /// visibility independently of EffectChangelog content, so the
        /// banner fires for any version upgrade even after we stop adding
        /// EffectChangelog entries (GitHub release notes are canonical now).
        /// Compares on Major.Minor.Build only because LastSeenVersion is
        /// stamped via ToString(3); a four-component Assembly Version with
        /// Revision=0 would otherwise read as "newer" than the parsed
        /// three-component value (Revision=-1) and pin the banner on
        /// forever after dismissal.</summary>
        public bool HasUnseenChangelog
        {
            get
            {
                if (Settings == null || UpdateChecker == null) return false;
                if (!EffectChangelog.TryParseVersion(Settings.LastSeenVersion, out var since)) return false;
                return Compare3(UpdateChecker.CurrentVersion, since) > 0;
            }
        }

        // 3-component Version comparison (Major.Minor.Build). Missing
        // components on either side are treated as 0 so a freshly-parsed
        // "0.1.7" (Build=7, Revision=-1) compares equal to an Assembly
        // "0.1.7.0" (Build=7, Revision=0).
        private static int Compare3(Version a, Version b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;
            int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
            int aBuild = a.Build < 0 ? 0 : a.Build;
            int bBuild = b.Build < 0 ? 0 : b.Build;
            return aBuild.CompareTo(bBuild);
        }

        /// <summary>Filter the fetched GitHub release list to the releases
        /// the post-upgrade What's new modal should display: strictly newer
        /// than LastSeenVersion, no newer than the running build, and not
        /// prereleases. Returns empty when the fetch hasn't completed or
        /// failed (caller falls back to EffectChangelog).</summary>
        public IReadOnlyList<ReleaseInfo> GetGitHubReleasesForBanner()
        {
            if (Settings == null || UpdateChecker?.AllReleases == null
                || UpdateChecker.AllReleases.Count == 0)
                return Array.Empty<ReleaseInfo>();
            if (!EffectChangelog.TryParseVersion(Settings.LastSeenVersion, out var since) || since == null)
                return Array.Empty<ReleaseInfo>();
            var current = UpdateChecker.CurrentVersion;
            var list = new List<ReleaseInfo>();
            foreach (var r in UpdateChecker.AllReleases)
            {
                if (r == null || r.IsPrerelease || r.Version == null) continue;
                if (r.Version <= since) continue;
                if (r.Version > current) continue;
                list.Add(r);
            }
            list.Sort((a, b) => b.Version.CompareTo(a.Version));
            return list;
        }

        /// <summary>Stamps LastSeenVersion to the running build. Hides the
        /// banner permanently for this version. Idempotent.</summary>
        public void DismissChangelog()
        {
            if (Settings == null) return;
            string current = CurrentVersionString();
            if (Settings.LastSeenVersion == current) return;
            Settings.LastSeenVersion = current;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        // ---- Word-of-mouth prompt ----------------------------------------

        // Earned-value gate. The banner only surfaces after this many
        // cumulative seconds of the wheel actually being driven with a game
        // running, so it never lands on a user who is still fighting setup.
        private const double ShareCtaThresholdSeconds = 2.0 * 60.0 * 60.0; // 2 h

        // Flush the in-memory odometer to settings at most this often, so we
        // don't write the settings file every frame.
        private const double StreamFlushIntervalSeconds = 120.0;

        // Stopwatch ts of the previous AccumulateStreamingTime call (0 = unset),
        // and seconds counted since the last flush to Settings.
        private long _streamClockTicks;
        private double _streamSecondsSinceFlush;

        /// <summary>True once the user has banked past the earned-seat-time
        /// threshold and hasn't already dismissed or acted on the prompt.
        /// One-and-done: ShareCtaDismissed latches it off forever.</summary>
        public bool ShouldShowShareCta
        {
            get
            {
                return Settings != null
                    && !Settings.ShareCtaDismissed
                    && Settings.ActiveStreamingSeconds >= ShareCtaThresholdSeconds;
            }
        }

        /// <summary>Latches the word-of-mouth prompt off permanently. Called
        /// whether the user acts on it or dismisses it. Idempotent.</summary>
        public void DismissShareCta()
        {
            if (Settings == null || Settings.ShareCtaDismissed) return;
            Settings.ShareCtaDismissed = true;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        // Called once per producer iteration. Adds wall time to the odometer
        // only while we're actually driving a running game; a long gap (sleep,
        // debugger, wheel gone) is discarded by the dt ceiling so idle time
        // never counts toward the earned-value threshold.
        private void AccumulateStreamingTime()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = _streamClockTicks;
            _streamClockTicks = now;
            if (prev == 0) return; // first sample: just establish the baseline

            if (Settings == null || !Settings.PluginEnabled || _device == null
                || string.IsNullOrEmpty(_currentGameName))
                return;

            double dt = (now - prev) / (double)Stopwatch.Frequency;
            if (dt <= 0.0 || dt > 2.0) return; // discard stalls / resume gaps

            _streamSecondsSinceFlush += dt;
            if (_streamSecondsSinceFlush < StreamFlushIntervalSeconds) return;
            Settings.ActiveStreamingSeconds += _streamSecondsSinceFlush;
            _streamSecondsSinceFlush = 0.0;
            try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
        }

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;

            // Tear down the home Feedback tile (best-effort; dispatches to the UI
            // thread, which may already be shutting down).
            try { _feedbackInjector?.Stop(); } catch { }
            _feedbackInjector = null;

            // Cancel any in-flight update check / installer download so they
            // don't outlive the plugin and write to a dead instance.
            try { _updateCheckerCts?.Cancel(); } catch { }
            try { _updateCheckerCts?.Dispose(); } catch { }
            _updateCheckerCts = null;

            // Drain in-flight TestEffect tasks. They poll _shuttingDown every
            // ~16 ms, so a short bounded wait is plenty in practice; the
            // bound just means we don't deadlock if one is hung in TestUpdate.
            System.Threading.SpinWait.SpinUntil(
                () => System.Threading.Volatile.Read(ref _activeTestTasks) == 0,
                250);

            // Let any in-flight device-recovery thread-pool item finish so it
            // can't resurrect a device after we've torn one down. It already
            // checks _shuttingDown after CleanupDevice, so it bails fast; the
            // bound just prevents a hang if a bring-up is mid-init-sequence.
            System.Threading.SpinWait.SpinUntil(() => !_recoveryInProgress, 1000);

            try { _capturePollThread?.Join(2000); } catch { }
            _capturePollThread = null;

            // Stop the active telemetry source so PushFromGameData becomes a
            // no-op for any late SimHub tick that lands during teardown.
            try { _telemetrySource?.Dispose(); } catch { }
            _telemetrySource = null;
            _simHubSource    = null;

            // Fold any unflushed earned seat time into the odometer so a
            // clean shutdown doesn't lose the last partial flush window.
            if (Settings != null && _streamSecondsSinceFlush > 0.0)
            {
                Settings.ActiveStreamingSeconds += _streamSecondsSinceFlush;
                _streamSecondsSinceFlush = 0.0;
            }

            // UI changes are written through to Settings on the fly, so just save.
            if (Settings != null) this.SaveCommonSettings("GeneralSettings", Settings);

            try { _rpmLeds?.Dispose(); } catch { }
            _rpmLeds = null;

            try { _audio?.Dispose(); } catch { }
            _audio = null;

            try { _helperHost?.Dispose(); } catch { }
            _helperHost = null;

            try { _capturedProcess?.Dispose(); } catch { }
            _capturedProcess = null;

            // Wake the producer if it's parked inside PushFloats on a full
            // ring, the plugin's _shuttingDown flag doesn't propagate into
            // the device's wait condition, so without this the join below can
            // time out and leave the producer alive while CleanupDevice tears
            // the device down underneath it.
            try { _device?.StopAcceptingSamples(); } catch { }

            try { _producerThread?.Join(2000); } catch { }
            if (_producerThread != null && _producerThread.IsAlive)
                SimHub.Logging.Current.Warn("[Trueforce] Producer thread did not exit cleanly.");
            _producerThread = null;

            try { _device?.ClearStream(); } catch { }
            // Brief pause so the centre-wheel samples drain to the device.
            Thread.Sleep(60);
            CleanupDevice();
            SimHub.Logging.Current.Info("[Trueforce] Plugin stopped.");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _currentGameName = data?.GameRunning == true ? data.GameName : null;

            // Continuous (telemetry-independent) tick: tell the FFB tap whether
            // force feedback should be flowing right now, from the active
            // source's session signal (Forza IsRaceOn, else a universal
            // engine/motion proxy). Set here, not in the per-frame handler, so
            // it correctly goes false when telemetry stops (pause/menu). Also
            // clears the no-FFB notice once force feedback is captured again.
            if (_ffbTap != null)
            {
                _ffbTap.GameFfbExpected = _telemetrySource?.IsSessionActive ?? false;
                if (_noFfbCaptureNotice != null && _ffbTap.MsSinceLastSample < 1000)
                    _noFfbCaptureNotice = null;
            }

            // Track game changes and auto-apply that game's default preset
            // (if one is bound in GameDefaults). Done before per-car override
            // so the loaded preset's CarOverrides dict is in place by the
            // time ApplyActiveCarOverride runs below.
            string gameName = data?.GameName;
            if (gameName != _activeGame)
            {
                _activeGame = gameName;
                SwapTelemetrySource(gameName);
                // Auto-apply the bound game default, UNLESS the user is
                // offline-editing a preset. In that case we don't clobber
                // their in-progress edits; the SettingsControl banner stays
                // up so they can decide (Save / Save as new / Discard).
                if (!IsOfflineEditing
                    && !string.IsNullOrEmpty(gameName) && Settings?.GameDefaults != null
                    && Settings.GameDefaults.TryGetValue(gameName, out var presetName)
                    && !string.IsNullOrEmpty(presetName)
                    && Settings.Presets != null
                    && Settings.Presets.TryGetValue(presetName, out var snap) && snap != null)
                {
                    ApplyGamePreset(snap);
                    _activePresetName = presetName;
                    SimHub.Logging.Current.Info($"[Trueforce] Loaded preset '{presetName}' as default for '{gameName}'.");
                }
                else if (!IsOfflineEditing
                    && !string.IsNullOrEmpty(gameName)
                    && Settings?.GameDefaults != null
                    && !Settings.GameDefaults.ContainsKey(gameName)
                    && !IsNativeTrueforceGame(gameName))
                {
                    // Unmapped game (no built-in binding, no user default):
                    // give it its own preset seeded from the Assetto Corsa
                    // baseline so a new / unsupported title starts from the
                    // user's tuned-on-a-GPRO AC values instead of bland class
                    // defaults, and so edits land in a per-game preset they
                    // keep rather than silently mutating globals.
                    EnsureSeededGamePreset(gameName);
                }

                // Per-game master enable. Default is "true" for unseen games,
                // EXCEPT for games that ship native Trueforce (Forza Motorsport
                // 2023), for those we default to "false" so our ep3 stream
                // doesn't fight the game's own Trueforce path. Saved values
                // always win over defaults so a user who explicitly enabled
                // us for a native-TF game keeps that choice.
                if (Settings != null)
                {
                    bool savedValue = false;
                    bool sawSaved = !string.IsNullOrEmpty(gameName)
                        && Settings.GameEnabled != null
                        && Settings.GameEnabled.TryGetValue(gameName, out savedValue);
                    bool wantEnabled;
                    if (sawSaved) { wantEnabled = savedValue; }
                    else if (IsNativeTrueforceGame(gameName))
                    {
                        wantEnabled = false;
                        // Persist so the user's per-game UI reflects "off" the
                        // first time and they understand we backed off.
                        if (Settings.GameEnabled == null)
                            Settings.GameEnabled = new Dictionary<string, bool>();
                        Settings.GameEnabled[gameName] = false;
                        try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Auto-disabling for '{gameName}' (ships native Trueforce). Re-enable manually if you prefer our stream.");
                    }
                    else { wantEnabled = true; }
                    if (Settings.PluginEnabled != wantEnabled)
                        SetPluginEnabled(wantEnabled, persistForActiveGame: false);
                }
            }

            // Track car changes and apply per-car override (or revert).
            string carId = data?.NewData?.CarId ?? data?.NewData?.CarModel;
            // Forza fallback when SimHub's data feed has no CarId: a user with
            // UDP forwarding misconfigured, or who hasn't selected a Forza
            // profile in SimHub, gives us null here even while the Forza game
            // is live and our UDP source IS reading packets. Our source parses
            // CarOrdinal direct from the UDP stream so we can still detect
            // per-car switches and apply per-car overrides without depending
            // on SimHub's profile being wired up. Prefix Car_<ordinal> to
            // match SimHub's NewData.CarModel format for Forza, so the
            // SimHub-supplied path and this fallback converge on one key for
            // the same physical car. (Was Forza_<ordinal> historically;
            // legacy entries are migrated by the NORMALIZEFORZA access code.)
            if (string.IsNullOrEmpty(carId) && IsForzaGameName(_activeGame))
            {
                var fzForCarId = _telemetrySource as ForzaUdpTelemetrySource;
                int? ordinal = fzForCarId?.CurrentCarOrdinal;
                if (ordinal.HasValue) carId = "Car_" + ordinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // Runtime alias: a legacy Forza_<n> id arriving from any source
            // (older SimHub build, third-party plugin, an older Trueforce
            // install that wasn't normalized yet) gets rewritten to Car_<n>
            // before any lookup. Without this, a stored Car_<n> preset would
            // miss when telemetry happens to feed us the Forza_ shape.
            // Forza-game scoped so a hypothetical non-Forza carId starting
            // with "Forza_" wouldn't get clobbered.
            if (!string.IsNullOrEmpty(carId)
                && IsForzaGameName(_activeGame)
                && carId.StartsWith("Forza_", System.StringComparison.Ordinal))
            {
                carId = "Car_" + carId.Substring("Forza_".Length);
            }
            // Forza un-sets the active car whenever the game loses focus
            // (alt-tab, screensaver, etc.), then re-sets it the moment focus
            // returns. Treating those transient nulls as a real "car gone"
            // event resets every effect's auto-cylinder / IIR state and
            // strands the user on a no-car-selected settings UI while
            // they're trying to tune. Latch the previous carId across null
            // gaps for Forza specifically; a real car switch (going to a
            // different car) still passes through because the next non-null
            // carId differs from _activeCarId. A real game-exit changes
            // _activeGame first (handled above), so this latch doesn't
            // hold a stale Forza car into another game.
            if (string.IsNullOrEmpty(carId)
                && !string.IsNullOrEmpty(_activeCarId)
                && IsForzaGameName(_activeGame))
            {
                carId = _activeCarId;
            }
            // While offline-editing a car preset, freeze the active car to the
            // one being edited: a live car change must not switch away (which
            // would discard the edit and aim a save at the wrong car).
            if (IsOfflineEditingCar)
                carId = _offlineEditCarId;
            if (carId != _activeCarId)
            {
                // Switching cars discards the outgoing car's UNSAVED draft
                // (per-car edits that weren't saved): restore its in-memory
                // override to the persisted baseline before we move on.
                DiscardUnsavedCarDraft(_activeCarId);
                _activeCarId = carId;
                // Clear any per-car edge-detected / IIR state on the effects and
                // the device's FFB filter chain so the new car's first frames
                // don't get blended with the previous car's last sample (e.g. a
                // spurious gear thud from a 4→1 apparent transition, or an FFB
                // smoothing transient biased toward the old car's last torque).
                if (_effects != null)
                {
                    for (int i = 0; i < _effects.Length; i++)
                    {
                        try { _effects[i].Reset(); } catch { }
                    }
                }
                _device?.ResetFfbFilters();
                // New car, discard the previous car's auto-detected layout so
                // the next resolver hit (or first telemetry frame) populates
                // fresh.
                // CarFacts redline for this car: cleared every car change,
                // populated below when an active variant has a RedlineRpm.
                // Lives outside the EnginePulse block since RevLimiter has
                // its own lifecycle and may exist before EnginePulse hits
                // the layout cascade.
                if (RevLimiter != null) RevLimiter.CarFactsRedline = null;

                if (EnginePulse != null)
                {
                    EnginePulse.AutoLayout = null;
                    EnginePulse.AutoLayoutSource = null;
                    EnginePulse.CatalogCyl = null;
                    _activeCarDisplayName = null;

                    // Seed AutoLayout from baked lookup / heuristic for games
                    // that don't ship cylinder count in telemetry (AC, etc.).
                    // Forza populates NumCylinders directly each frame and
                    // OnTelemetry converts that to AutoLayout, they agree on
                    // any car in both lookups. The user's saved Layout
                    // (when not Auto) always wins via EffectiveLayout, so this
                    // is purely the auto-default cascade.
                    // CarFacts (the community-vetted layer) feeds variants for
                    // this car. Two separable contributions:
                    //   1. Cylinders + EngineConfig → AutoLayout
                    //   2. RedlineRpm + CarName     → applied independently
                    // The CarCylinderCache migration seeded Stock variants for
                    // cars the user has already driven, so on day one (1) hits
                    // the same set the legacy resolver used to. (2) is largely
                    // dormant until community submissions arrive in later
                    // stages. The split apply lets a redline-only or name-only
                    // bundle still contribute without forcing a cylinder count.
                    bool haveVariant = TryResolveActiveVariant(_activeGame, carId, out var v);

                    // CarName from the bundle (chassis-level, variant-
                    // independent) wins when set.
                    string ck = _activeGame + "/" + carId;
                    if (Settings.CarFacts != null
                        && Settings.CarFacts.TryGetValue(ck, out var bundle)
                        && bundle != null
                        && !string.IsNullOrEmpty(bundle.CarName))
                        _activeCarDisplayName = bundle.CarName;

                    // RedlineRpm rides any usable variant, even one with
                    // Cylinders=0 (the "unknown / use heuristic" sentinel from
                    // the EngineVariant contract). The RevLimiter substitutes
                    // it for a missing telemetry redline; if a layout fallback
                    // runs below, the redline survives.
                    if (haveVariant && RevLimiter != null
                        && v.RedlineRpm.HasValue && v.RedlineRpm.Value > 0)
                        RevLimiter.CarFactsRedline = v.RedlineRpm.Value;

                    // Layout: use the variant when cylinders are valid. The
                    // bake-wins-over-stale-cache invariant is now handled
                    // inside TryResolveActiveVariant: an unconfirmed Scanner
                    // seed yields to the virtual Baked variant for any car
                    // BuiltinCarCylinders knows about.
                    bool variantUsableForLayout = haveVariant
                        && v.Cylinders >= 1 && v.Cylinders <= 16;

                    if (variantUsableForLayout)
                    {
                        EnginePulse.AutoLayout = Effects.FiringPatternDb.LayoutFromLegacy(
                            v.Cylinders, v.EngineConfig, false);
                        EnginePulse.AutoLayoutSource = MapCarFactSourceToUiLabel(v.Source);
                        EnginePulse.CatalogCyl = v.Cylinders;
                        // CarName fallback when the bundle didn't set one:
                        // resolver still knows AC's ui_car.json etc.
                        if (string.IsNullOrEmpty(_activeCarDisplayName)
                            && CarCylinderResolver.TryResolve(_activeGame, carId, out var ds))
                            _activeCarDisplayName = ds.DisplayName;
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Car '{carId}' resolved from CarFacts: "
                            + $"variant='{v.Label}', cyl={v.Cylinders}, config={v.EngineConfig}, "
                            + $"redline={(v.RedlineRpm.HasValue ? v.RedlineRpm.Value.ToString() : "-")}, "
                            + $"source={v.Source} -> layout={EnginePulse.AutoLayout}");
                    }
                    else if (CarCylinderResolver.TryResolve(_activeGame, carId, out var carSpec))
                    {
                        EnginePulse.AutoLayout = Effects.FiringPatternDb.LayoutFromLegacy(
                            carSpec.Cylinders, carSpec.EngineConfig, carSpec.IsElectric);
                        EnginePulse.AutoLayoutSource = carSpec.Source;
                        EnginePulse.CatalogCyl = carSpec.Cylinders;
                        if (string.IsNullOrEmpty(_activeCarDisplayName))
                            _activeCarDisplayName = carSpec.DisplayName;
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Car '{carId}' resolved: cyl={carSpec.Cylinders}, "
                            + $"electric={carSpec.IsElectric}, source={carSpec.Source}, "
                            + $"engineConfig={carSpec.EngineConfig} ({carSpec.EngineConfigSource ?? "auto"}), "
                            + $"name={carSpec.DisplayName ?? "(none)"}"
                            + $" -> layout={EnginePulse.AutoLayout}");
                    }
                    else if (_telemetrySource?.ProvidesNumCylinders == true)
                    {
                        // Resolver missed but the active source will populate
                        // NumCylinders shortly (Forza UDP). Label the source
                        // now so the UI doesn't briefly show "couldn't detect"
                        // between car-change and first frame. AutoLayout
                        // itself stays null until OnTelemetry runs.
                        EnginePulse.AutoLayoutSource = "telemetry";
                    }
                    else if (!string.IsNullOrEmpty(carId))
                    {
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Car '{carId}' not auto-resolved, user can set engine layout manually.");
                    }
                }
                // Re-resolve the new car's preset from disk so the applied
                // tuning always MATCHES the car you just switched to, and so a
                // car you cleared to "None" earlier in the session re-resolves
                // to its default (built-in, or the game preset) when you come
                // back to it (None is transient, never sticky). When the car is
                // gone (carId null, e.g. back to a menu) we can't resolve, so
                // fall back to applying globals as before.
                if (!string.IsNullOrEmpty(_activeCarId))
                    ReloadActiveCarOverrideFromStore();
                else
                    ApplyActiveCarOverride();
                // Opportunistic backfill: legacy migration (and pre-fix
                // built-ins) wrote car preset files with GameName="" because
                // no game was active at the time. Now that we know which game
                // this car belongs to, retag any of its on-disk presets that
                // are still empty. Skips built-ins (refreshed by
                // InstallOrUpdateBuiltinCarPresets on Init from the bundled
                // JSON), skips files already tagged. Dedup'd per
                // (game, carId) per session.
                if (_carStore != null
                    && !string.IsNullOrEmpty(_activeGame)
                    && !string.IsNullOrEmpty(_activeCarId)
                    && _gameNameBackfillDone.Add(_activeGame + "|" + _activeCarId))
                {
                    BackfillGameNameForActiveCar();
                }
                // Similar one-shot per-car for DisplayName: rename legacy
                // presets whose name was just the carId (e.g. "Car_424") into
                // the friendlier resolver-provided name ("1997 Mazda RX-7").
                // Gated on resolver having a DisplayName for this car so it
                // doesn't fire for AC where carIds are already descriptive.
                if (_carStore != null
                    && !string.IsNullOrEmpty(_activeGame)
                    && !string.IsNullOrEmpty(_activeCarId)
                    && !string.IsNullOrEmpty(_activeCarDisplayName)
                    && _displayNameBackfillDone.Add(_activeGame + "|" + _activeCarId))
                {
                    BackfillDisplayNameForActiveCar();
                }
            }

            // G HUB UI presence check. Polled every ~5 s; logs on transitions.
            // Surfaced in the UI as a warning banner because the G HUB UI can
            // block our HID open call and is a common "wheel doesn't respond"
            // cause. Only lghub.exe counts here (not the background agent).
            {
                long nowG = Stopwatch.GetTimestamp();
                if (nowG - _lastGHubCheckTicks > GHubCheckIntervalTicks)
                {
                    _lastGHubCheckTicks = nowG;
                    bool running = false;
                    try
                    {
                        // GetProcessesByName("lghub") matches ONLY lghub.exe (the
                        // G HUB UI), not lghub_agent / lghub_updater / lghub_system_tray.
                        // Deliberate: the background agent is essentially always
                        // present, so counting it here made _isGHubRunning latch true
                        // for the whole session and stopped MaybeRecoverDevice from
                        // ever re-attaching after an idle-time stream fault. The wheel
                        // then stayed "not detected" until SimHub restarted (which
                        // re-runs the ungated startup bring-up). The agent does not
                        // hold the wheel's MI_02 Trueforce interface.
                        if (System.Diagnostics.Process.GetProcessesByName(GHubProcessName).Length > 0)
                        {
                            running = true;
                        }
                    }
                    catch { /* process enumeration can fail under some sandbox conditions; treat as not-running */ }
                    if (running != _gHubLastLoggedState)
                    {
                        _gHubLastLoggedState = running;
                        SimHub.Logging.Current.Info(
                            running
                                ? "[Trueforce] Logitech G HUB detected. It claims the wheel's HID interface and blocks Trueforce. Close G HUB and restart SimHub."
                                : "[Trueforce] Logitech G HUB no longer detected. Wheel access should be available again.");
                        SimHub.Logging.Current.Info(
                            "[Trueforce] Logitech processes running: " + SnapshotLogitechProcesses());
                    }
                    _isGHubRunning = running;
                }
            }

            // Transparently re-attach the wheel after a hot-unplug, a faulted
            // stream, a closed-G-HUB, or a wheel that wasn't present at
            // startup. Cheap when healthy (a couple of field checks); the
            // blocking bring-up runs off-thread.
            MaybeRecoverDevice();

            // Retry enhanced-source acquisition once per second while we have
            // an enhanced-eligible game running but are still on the SimHub
            // fallback. Covers the AC menu→session window (MMF not yet
            // created), and any other source that needs to wait for the game
            // to be fully loaded before its data surface is available.
            if (!string.IsNullOrEmpty(_activeGame)
                && _telemetrySource != null && !_telemetrySource.IsEnhanced
                && IsEnhancedEligible(_activeGame))
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _lastEnhancedRetryTicks > Stopwatch.Frequency)
                {
                    _lastEnhancedRetryTicks = now;
                    SwapTelemetrySource(_activeGame, silent: true);
                }
            }

            MaybeStartPortDiscovery();

            // Cache slow-rate fields for the enhanced-source overlay step in
            // DispatchFrame. Always populated from SimHub regardless of which
            // source is currently dispatching, so the cache stays warm during
            // an enhanced run and is immediately available when AC starts.
            var nd = data?.NewData;
            if (nd != null)
            {
                _lastSimHubMaxRpm           = nd.MaxRpm;
                _lastSimHubAbsActive        = nd.ABSActive;
                _lastSimHubPitLimiterActive = nd.PitLimiterOn;
                _lastSimHubDrsActive        = nd.DRSEnabled;
            }

            // Hand the GameData to the SimHub source. It builds a
            // TelemetryFrame and fires OnFrame → DispatchFrame, which is
            // where we update audio gain and fan out to effects. Done this
            // way so an enhanced source (AC MMF, etc.) drives the same
            // dispatch path at its native rate without forking effect code.
            _simHubSource?.PushFromGameData(data);

            // Cache the SimHub-computed redline RPM after the push (it's derived
            // there from CarSettings_RedLineRPM). Overlaid onto enhanced-source
            // frames in DispatchFrame so the rev limiter gets the real redline
            // on AC / etc. Mirror it EXACTLY, including 0: LastRedlineRpm is now
            // 0 for games that report no real redline, and an old `> 0` guard
            // would leave a previous redline-game's value cached and bleed it
            // onto a no-redline game (firing the buzz at a phantom redline).
            if (_simHubSource != null)
                _lastSimHubRedlineRpm = _simHubSource.LastRedlineRpm;

            // Learn, once per game, that this title reports a usable redline,
            // so the rev-limiter UI is stable (engage-% row hides for redline
            // games even before telemetry flows next session). Positive-only;
            // persisted once on first observation. Forza is excluded outright:
            // its per-car redline can legitimately land inside the sanity gate
            // (an RX-7 in FH6 passed it), but Forza has no precise rev limit we
            // can fire AT, so it must keep the engage-% control. The sanity gate
            // alone wasn't enough; the game-family exclusion is the real rule.
            if (!string.IsNullOrEmpty(_activeGame) && !IsForzaGameName(_activeGame)
                && Settings?.GamesWithRedline != null
                && !Settings.GamesWithRedline.Contains(_activeGame))
            {
                double r = _lastSimHubRedlineRpm, m = _lastSimHubMaxRpm;
                if (r > 100.0 && (m <= 100.0 || (r <= m * 1.02 && r >= m * 0.5)))
                {
                    Settings.GamesWithRedline.Add(_activeGame);
                    PersistSettings();
                }
            }
        }

        /// <summary>OnFrame handler bound to whichever ITelemetrySource is
        /// currently active. Runs on the source's polling thread (SimHub's
        /// data tick today; an MMF reader thread once enhanced sources land).
        /// Updates audio-throttle modulation and dispatches to each effect;
        /// per-effect exceptions are swallowed so one bad effect can't
        /// break the rest of the haptic pipeline.</summary>
        private void DispatchFrame(TelemetryFrame frame)
        {
            // Enhanced sources (AC MMF, etc.) deliberately skip slow-rate
            // fields whose physics-rate fidelity wouldn't be perceptible.
            // Overlay them from the cached SimHub reading so effects see a
            // complete frame regardless of which source is active.
            var src = _telemetrySource;
            if (src != null && src.IsEnhanced)
            {
                frame.MaxRpm    = _lastSimHubMaxRpm;
                frame.AbsActive = _lastSimHubAbsActive;
                // Redline RPM: only fill when the enhanced source didn't supply
                // its own (none do today), so the rev limiter can threshold
                // against the real shift point where SimHub knows it. NEVER for
                // Forza: SimHub's Forza redline is unreliable (often in-range
                // but wrong), and the rev limiter's own sanity gate can't tell a
                // bogus-but-in-range value from a real one, so it would switch
                // to fire-at-redline and silently stop engaging. Leaving it 0
                // forces the limiter onto the MaxRpm*Threshold (engage-%) path,
                // matching ActiveSourceUsesRedline (which also excludes Forza).
                if (frame.RedlineRpm <= 0 && !IsForzaGameName(_activeGame))
                    frame.RedlineRpm = _lastSimHubRedlineRpm;
                // Only overlay PitLimiter/DRS when the enhanced source itself
                // didn't populate them, preserves any future enhanced source
                // that does read them natively (e.g., a richer AC plugin
                // reading the static page's pit-lane flags).
                if (frame.PitLimiterActive == null) frame.PitLimiterActive = _lastSimHubPitLimiterActive;
                if (frame.DrsActive        == null) frame.DrsActive        = _lastSimHubDrsActive;
            }

            // Universal collision derivation: if the source didn't populate
            // CollisionMagnitude (only PC2's opponent-collision signal does
            // directly), derive from a sudden three-axis accel spike.
            // Threshold ≈ 5g (≈49 m/s²), well above hard cornering
            // (~1.5-2g) and hard braking (~1g), squarely in "something hit
            // something" territory. Surge (longitudinal) catches head-on /
            // rear-end impacts; sway catches T-bones; heave catches hard
            // landings and curb slams. Normalized: each ~50 m/s² over the
            // threshold = 1.0 magnitude unit, capped in the effect.
            if (frame.CollisionMagnitude == null)
            {
                const double CollisionThresholdMps2 = 49.0;   // ≈ 5g
                const double NormalizePerMps2       = 0.02;   // 1.0 magnitude per ~50 m/s² over threshold
                double sway  = frame.AccelerationSway  ?? 0;
                double heave = frame.AccelerationHeave ?? 0;
                double surge = frame.AccelerationSurge ?? 0;
                double peak  = Math.Max(Math.Abs(surge),
                               Math.Max(Math.Abs(sway), Math.Abs(heave)));
                if (peak > CollisionThresholdMps2)
                {
                    frame.CollisionMagnitude = (peak - CollisionThresholdMps2) * NormalizePerMps2;
                }
            }

            // Latch motion for the stationary-spring FFB floor. Speed is
            // universal; steering is stamped only when the active source
            // actually reports it (AC), so the spring stays disengaged on
            // sources that don't (the freshness check in the provider).
            _lastSpeedKmh = (float)frame.SpeedKmh;
            if (frame.SteeringAngle.HasValue)
            {
                _lastSteerNorm = (float)frame.SteeringAngle.Value;
                System.Threading.Interlocked.Exchange(ref _lastSteerTicks, Stopwatch.GetTimestamp());
            }

            // The FFB tap's "force feedback should be flowing" hint is set from
            // IsSessionActive in DataUpdate (a continuous tick), not here, so it
            // also goes false when telemetry stops (pause/menu) instead of
            // sticking at its last value.

            if (_audio != null)
                _audio.ThrottleNormalized = (float)frame.Throttle01;

            if (_effects != null)
            {
                for (int i = 0; i < _effects.Length; i++)
                {
                    try { _effects[i].OnTelemetry(frame); }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Error($"[Trueforce] {_effects[i].Name} telemetry error", ex);
                    }
                }
            }

            // Rim rev/shift LEDs. Gated to iRacing (where MAIRA users lose
            // native rev lights after disabling in-game Trueforce) and the
            // opt-in setting. Independent HID++ channel; can't disturb FFB or
            // the ep3 stream even when it shares the wheel with native FFB.
            if (_rpmLeds != null)
            {
                // LEDs are only SAFE when there is no PID on the wheel's HID++
                // pipe, i.e. MAIRA passthrough is live (MAIRA publishing to
                // shared memory, PID suppressed). In the no-MAIRA iRacing path
                // (Trueforce disabled in app.ini) iRacing sends PID and an LED
                // write stalls FFB ~1.5 s, so auto-suppress LEDs there. The
                // setting can be on; it just can't fight FFB.
                bool mairaLive = _mairaIpc != null && _mairaIpc.IsOpen;
                bool gate = (Settings?.RpmLedsEnabled ?? false)
                            && string.Equals(_activeGame, "IRacing", StringComparison.Ordinal)
                            && mairaLive;
                try
                {
                    _rpmLeds.OnFrame(frame.RpmPercent, frame.Rpms, frame.MaxRpm,
                                     frame.RedlineReached, gate);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[Trueforce] RPM-LED telemetry error", ex);
                }
            }
        }

        /// <summary>Run the simulated rev/shift sweep on the rim LEDs (settings
        /// "Test" button). Opens the HID++ channel on demand so it works with
        /// nothing running; safe regardless of active game.</summary>
        public void TestRpmLeds()
        {
            if (_rpmLeds == null) { SimHub.Logging.Current.Info("[RPM-LED] controller not initialized"); return; }
            int ms = _rpmLeds.RunTest();
            SimHub.Logging.Current.Info($"[RPM-LED] Test started, duration={ms} ms ({_rpmLeds.Status})");
        }

        /// <summary>Force the rim LEDs off (feature unchecked / plugin
        /// disabled). No telemetry frames arrive after that to drive the
        /// gate-off path, so callers must invoke this explicitly.</summary>
        public void TurnOffRpmLeds() => _rpmLeds?.ForceOff();

        public string RpmLedStatus => _rpmLeds?.Status ?? "(n/a)";
        public bool RpmLedIsTesting => _rpmLeds?.IsTesting ?? false;

        // ---------- Performance auto-ratchet ----------

        /// <summary>Polled from ProducerLoop. Once per RatchetWindowMs, snapshots
        /// the device + audio glitch counters and bumps the corresponding ring
        /// capacity if the per-window delta crossed RatchetThreshold. One-way
        /// only, never shrinks. Survived capacities are persisted to Settings
        /// so the user doesn't re-pay the discovery glitch cost next session.
        /// In Manual mode the ratchet is bypassed; user controls sizes directly.</summary>
        private void CheckAutoRatchet()
        {
            var perf = Settings?.Performance;
            if (perf == null || perf.Mode != PerformanceMode.Auto) return;
            if (_device == null) return;

            long now = Stopwatch.GetTimestamp();
            long windowTicks = Stopwatch.Frequency * RatchetWindowMs / 1000L;
            if (_autoRatchetLastCheckTicks != 0 && (now - _autoRatchetLastCheckTicks) < windowTicks) return;

            long tfNow    = _device.UnderrunCount;
            long audioNow = _audio?.GlitchCount ?? 0;

            // Skip the very first tick (no baseline yet). Also seed the
            // last-underrun-seen timestamps to "now" so a session that
            // starts clean has a real quiet-window anchor, without this,
            // _tfLastUnderrunSeenTicks would stay 0 and the (now - 0)
            // arithmetic would falsely make every clean session look like
            // it had been quiet for the entire wall-clock since boot.
            if (_autoRatchetLastCheckTicks == 0)
            {
                _autoRatchetLastTfCount    = tfNow;
                _autoRatchetLastAudioCount = audioNow;
                _autoRatchetLastCheckTicks = now;
                _tfLastUnderrunSeenTicks    = now;
                _audioLastUnderrunSeenTicks = now;
                return;
            }

            long tfDelta    = tfNow    - _autoRatchetLastTfCount;
            long audioDelta = audioNow - _autoRatchetLastAudioCount;
            _autoRatchetLastTfCount    = tfNow;
            _autoRatchetLastAudioCount = audioNow;
            _autoRatchetLastCheckTicks = now;

            // Update last-underrun-seen timestamps. Any non-zero delta
            // resets the quiet timer; this is what the DOWN check measures
            // against. Initialize on the first non-zero value so we don't
            // start with "60s ago" and step down immediately on a quiet
            // session.
            if (tfDelta    > 0) _tfLastUnderrunSeenTicks    = now;
            if (audioDelta > 0) _audioLastUnderrunSeenTicks = now;

            // ----- Ratchet UP -----
            // Forza UDP exposes IsRaceOn, when paused / in menu / loading,
            // CPU spikes there don't reflect race-time conditions, so don't
            // bake a ratchet UP from them. Other sources don't have an
            // equivalent flag; SimHub's own GameRunning isn't precise enough
            // (it's true the moment the launcher is up, including loading).
            bool suppressUp = false;
            var fz = _telemetrySource as ForzaUdpTelemetrySource;
            if (fz != null && !fz.LastIsRaceOn) suppressUp = true;

            // Two-window confirmation gate: only fire UP when BOTH the
            // previous and current window crossed the threshold. Filters
            // out one-off blips (single CPU stall, brief USB hiccup) that
            // aren't actually sustained pressure on the ring.
            bool tfOver    = tfDelta    >= RatchetThreshold;
            bool audioOver = audioDelta >= RatchetThreshold;
            bool tfFireUp    = tfOver    && _prevTfOverThreshold;
            bool audioFireUp = audioOver && _prevAudioOverThreshold;
            _prevTfOverThreshold    = tfOver;
            _prevAudioOverThreshold = audioOver;

            if (!suppressUp && tfFireUp && perf.TfRingSize < TrueforceDevice.MaxRingSize)
            {
                int oldCap = perf.TfRingSize;
                int newCap = oldCap * 2;
                if (newCap > TrueforceDevice.MaxRingSize) newCap = TrueforceDevice.MaxRingSize;
                ApplyTfRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet UP: Trueforce ring {oldCap} → {newCap} after {tfDelta} dropout-events/s (~{tfDelta * 20} ms cumulative, sustained 2 windows).");
                _tfLastRatchetActionTicks = now;
                _tfLastActionWasDown = false;
                _prevTfOverThreshold = false;   // re-arm the 2-window requirement
                FireRatchetEvent(true, oldCap, newCap);
            }

            if (!suppressUp && audioFireUp && perf.AudioRingSize < AudioCaptureSource.MaxRingSamples)
            {
                int oldCap = perf.AudioRingSize;
                int newCap = oldCap * 2;
                if (newCap > AudioCaptureSource.MaxRingSamples) newCap = AudioCaptureSource.MaxRingSamples;
                ApplyAudioRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet UP: audio ring {oldCap} → {newCap} after {audioDelta} dropout-events/s (~{audioDelta * 20} ms cumulative or laps, sustained 2 windows).");
                _audioLastRatchetActionTicks = now;
                _audioLastActionWasDown = false;
                _prevAudioOverThreshold = false;   // re-arm the 2-window requirement
                FireRatchetEvent(false, oldCap, newCap);
            }

            // ----- Ratchet DOWN -----
            // Conditions, all must hold:
            //   - capacity is above its minimum
            //   - quiet window: no underruns/glitches for >= RatchetDownQuietMs
            //   - cooldown: no ratchet action (up or down) for >=
            //     RatchetDownCooldownMs after an UP, or >=
            //     RatchetDownFastCooldownMs once descent has started.
            // Quiet operation: log entry only, no UI event fire (don't want
            // a modal interrupting the user every minute as the ring drains).
            long quietTicks         = Stopwatch.Frequency * RatchetDownQuietMs        / 1000L;
            long slowCooldownTicks  = Stopwatch.Frequency * RatchetDownCooldownMs     / 1000L;
            long fastCooldownTicks  = Stopwatch.Frequency * RatchetDownFastCooldownMs / 1000L;

            long tfCooldown    = _tfLastActionWasDown    ? fastCooldownTicks : slowCooldownTicks;
            long audioCooldown = _audioLastActionWasDown ? fastCooldownTicks : slowCooldownTicks;

            if (perf.TfRingSize > TrueforceDevice.MinRingSize
                && _tfLastUnderrunSeenTicks  != 0
                && (now - _tfLastUnderrunSeenTicks)    >= quietTicks
                && (_tfLastRatchetActionTicks == 0
                    || (now - _tfLastRatchetActionTicks) >= tfCooldown))
            {
                int oldCap = perf.TfRingSize;
                int newCap = oldCap / 2;
                if (newCap < TrueforceDevice.MinRingSize) newCap = TrueforceDevice.MinRingSize;
                ApplyTfRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet DOWN: Trueforce ring {oldCap} → {newCap} after {RatchetDownQuietMs / 1000} s of quiet.");
                _tfLastRatchetActionTicks = now;
                _tfLastActionWasDown = true;
                // Fire the event on DOWN too so the UI banner stays
                // accurate. Without this, a banner showing "8 → 16" would
                // remain stale after the ring auto-shrank back to 8; the
                // UI auto-dismisses when latest == original.
                FireRatchetEvent(true, oldCap, newCap);
            }

            if (perf.AudioRingSize > AudioCaptureSource.MinRingSamples
                && _audioLastUnderrunSeenTicks  != 0
                && (now - _audioLastUnderrunSeenTicks)    >= quietTicks
                && (_audioLastRatchetActionTicks == 0
                    || (now - _audioLastRatchetActionTicks) >= audioCooldown))
            {
                int oldCap = perf.AudioRingSize;
                int newCap = oldCap / 2;
                if (newCap < AudioCaptureSource.MinRingSamples) newCap = AudioCaptureSource.MinRingSamples;
                ApplyAudioRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet DOWN: audio ring {oldCap} → {newCap} after {RatchetDownQuietMs / 1000} s of quiet.");
                _audioLastRatchetActionTicks = now;
                _audioLastActionWasDown = true;
                FireRatchetEvent(false, oldCap, newCap);
            }
        }

        private void FireRatchetEvent(bool isTf, int oldCap, int newCap)
        {
            try { AutoRatchetBumped?.Invoke(isTf, oldCap, newCap); } catch { }
        }

        /// <summary>Dev-only hook: fire a synthetic AutoRatchetBumped event so
        /// the inline notice banner can be exercised without waiting for real
        /// underruns. Wired through the access-code box ("RATCHET").</summary>
        public void DebugFireRatchet(bool isTf, int oldCap, int newCap)
            => FireRatchetEvent(isTf, oldCap, newCap);

        /// <summary>Apply a new Trueforce ring size to the live device and persist.
        /// Called by both the auto-ratchet path and Manual-mode UI sliders.</summary>
        public void ApplyTfRingSize(int newCapacity)
        {
            if (Settings?.Performance == null || _device == null) return;
            int sane = SanitizePow2(newCapacity, TrueforceDevice.MinRingSize, TrueforceDevice.MaxRingSize,
                                    TrueforceDevice.DefaultRingSize);
            Settings.Performance.TfRingSize = sane;
            _device.SetRingCapacity(sane);
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>Apply a new audio ring size to the live capture source and persist.</summary>
        public void ApplyAudioRingSize(int newCapacity)
        {
            if (Settings?.Performance == null || _audio == null) return;
            int sane = SanitizePow2(newCapacity, AudioCaptureSource.MinRingSamples,
                                    AudioCaptureSource.MaxRingSamples, AudioCaptureSource.DefaultRingSamples);
            Settings.Performance.AudioRingSize = sane;
            _audio.SetRingCapacity(sane);
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>Reset both rings to the smallest configured value. Auto mode
        /// will re-discover whether the machine can hold them; Manual mode
        /// keeps the small value until the user changes it.</summary>
        public void ResetPerformanceToLowest()
        {
            ApplyTfRingSize(TrueforceDevice.MinRingSize);
            ApplyAudioRingSize(AudioCaptureSource.MinRingSamples);
        }

        public void SetPerformanceMode(PerformanceMode mode)
        {
            if (Settings?.Performance == null) return;
            Settings.Performance.Mode = mode;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>Clamp <paramref name="value"/> to [min, max] and round down
        /// to the nearest power of two. Used to defensively sanitize a value
        /// pulled from settings (which a hand-edited file could set to anything).</summary>
        private static int SanitizePow2(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            int p = 1;
            while ((p << 1) <= value) p <<= 1;
            if (p < min) p = min;
            if (p > max) p = max;
            return p;
        }

        // ---------- enhanced source selection ----------

        /// <summary>True when <paramref name="game"/> has a per-game enhanced
        /// source we should attempt to instantiate. Used both at game-change
        /// and as the gate for the periodic retry loop. Forza covers FH4/5/6
        /// and FM (2023) since they share the same Data Out wire format.</summary>
        private bool IsEnhancedEligible(string game)
        {
            if (game == "AssettoCorsa") return true;
            if (IsForzaGameName(game)) return true;
            return false;
        }

        /// <summary>Save current Settings to SimHub's common-settings store.
        /// UI code calls this after touching settings outside the on-the-fly
        /// path (e.g., persisting SharingAuthor from the export-info dialog).</summary>
        public void PersistSettings()
        {
            if (Settings == null) return;
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        /// <summary>True when the Forza UDP section should be visible in the
        /// settings UI. Shown only while a Forza title is the active game;
        /// hidden in every other game so the panel stays uncluttered for
        /// the non-Forza majority.</summary>
        public bool ShouldShowForzaSection =>
            IsForzaGameName(_activeGame);

        /// <summary>True only while the game's process is actually running
        /// (SimHub data.GameRunning), not merely when a profile is selected.
        /// _currentGameName is null whenever nothing is running, so these are
        /// false for a profile that's open with the game closed. Used to gate
        /// the "set up UDP" prompt so it never fires on a selected-but-not-
        /// launched title.</summary>
        public bool IsGameRunning  => !string.IsNullOrEmpty(_currentGameName);
        public bool IsForzaRunning => IsForzaGameName(_currentGameName);

        /// <summary>True when the rim rev-LED + MAIRA section should be
        /// visible. iRacing-only: that is the sole game where the LEDs
        /// (and the MAIRA passthrough that makes them safe) apply.</summary>
        public bool ShouldShowRpmLedSection =>
            string.Equals(_activeGame, "IRacing", StringComparison.Ordinal);

        /// <summary>True when the active game's telemetry includes ABS
        /// pump activity. Forza's Data Out wire format (FH4/FH5/FH6) does
        /// not surface this, and neither does SimHub's universal reader
        /// for those titles, so the ABS effect can't fire there. Drives a
        /// "not exposed by Forza UDP" badge in the settings UI so users
        /// don't tune the section expecting feedback that will never
        /// arrive. Other games default to true (they may or may not
        /// actually emit ABS; we surface it when they do).</summary>
        public bool ActiveGameSupportsAbs =>
            string.IsNullOrEmpty(_activeGame) || !IsForzaGameName(_activeGame);

        /// <summary>True when the "ABS not exposed by Forza UDP" badge should
        /// show. We only want it in a Forza context, not always: when editing
        /// a car preset, judge by that car preset's own game; when editing a
        /// game preset, judge by whether that preset is a Forza game's default;
        /// otherwise judge by the running/active game. This stops the badge
        /// from appearing while a non-Forza preset is open.</summary>
        public bool ShowAbsUnsupportedBadge
        {
            get
            {
                // Editing a car preset: the car preset carries its own game.
                if (IsOfflineEditingCar)
                {
                    var g = GetCarPresetGame(_offlineEditCarId, _offlineEditCarPresetName);
                    return !string.IsNullOrEmpty(g) && IsForzaGameName(g);
                }
                // Editing a game preset: show only if it's a Forza game default.
                if (IsOfflineEditing)
                    return PresetIsForzaGameDefault(_offlineEditPresetName);
                // Live: judge by the active game.
                return !ActiveGameSupportsAbs;
            }
        }

        // True if any Forza game's auto-load default maps to this preset name.
        private bool PresetIsForzaGameDefault(string presetName)
        {
            if (string.IsNullOrEmpty(presetName) || Settings?.GameDefaults == null) return false;
            foreach (var kv in Settings.GameDefaults)
                if (IsForzaGameName(kv.Key) && string.Equals(kv.Value, presetName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>True when the stationary spring is supported on the
        /// active source. The spring conflicted with Forza's force feedback
        /// across pause and matchmaking transitions and could pull the wheel
        /// to its rotational stop; the precise mechanism wasn't isolated and
        /// several narrower gates were tried without reliably catching every
        /// case, so it is bypassed outright for the Forza UDP source. Drives
        /// a "not used in Forza" badge next to the Stationary spring
        /// checkbox so users don't tune the section expecting a behavior
        /// that won't apply. Other sources unchanged.</summary>
        public bool ActiveSourceSupportsStationarySpring =>
            !(_telemetrySource is ForzaUdpTelemetrySource);

        /// <summary>True if SimHub's GameName looks like any Forza title
        /// (Horizon or Motorsport). Drives Forza UDP section visibility.
        /// FM is included even though we auto-disable for it: the Data Out
        /// wire format is shared, so a user who manually re-enables for FM
        /// should still be able to configure the listener.</summary>
        private static bool IsForzaGameName(string game)
        {
            if (string.IsNullOrEmpty(game)) return false;
            return game == "FH4"
                || game == "FH5"
                || game == "FH6"
                || game == "FM7"
                || game == "FM8";
        }

        /// <summary>True if the game ships native Trueforce on PC. Plugin
        /// auto-disables on first encounter so our ep3 stream doesn't fight
        /// the game's own. Users can manually re-enable via the master
        /// toggle if they prefer our effects layered on top. SimHub
        /// GameName values verified against the SimHub install
        /// (LookupTables and PluginsData folder names).</summary>
        private static bool IsNativeTrueforceGame(string game)
        {
            if (string.IsNullOrEmpty(game)) return false;
            // Forza Motorsport (2023 reboot, internally FM8) ships native
            // Trueforce per Logitech's announcement. FM7 does NOT ship
            // native Trueforce, so it falls through to the Forza UDP
            // source for our enhanced effects.
            return game == "FM8"
                || game == "IRacing"
                || game == "AssettoCorsaCompetizione"
                || game == "AssettoCorsaRally"
                || game == "AssettoCorsaEVO"
                || game == "Automobilista2"
                || game == "BeamNgDrive"
                || game == "F12022"
                || game == "F12023"
                || game == "F12024"
                || game == "F12025"
                || game == "EAWRC23"
                || game == "PCars3"
                || game == "CodemastersGrid2019"
                || game == "WRC10"
                || game == "WRCGenerations"
                || game == "TDUSC"
                || game == "LMU";
        }

        /// <summary>Pick the right ITelemetrySource for <paramref name="game"/>
        /// (AC's MMF reader for "AssettoCorsa", Forza UDP listener for any
        /// Forza title, SimHub fallback otherwise) and hand-off OnFrame so
        /// exactly one source dispatches at a time. Called from DataUpdate on
        /// the SimHub data thread; the new source's polling thread is fully
        /// started before the old source is detached, so the briefest
        /// possible window of "no dispatch" covers the swap.
        /// Pass <paramref name="silent"/>=true on retry attempts so we don't
        /// log a "fell back" message every second while AC is loading.</summary>
        private void SwapTelemetrySource(string game, bool silent = false)
        {
            ITelemetrySource newSource = null;
            if (game == "AssettoCorsa")
            {
                var ac = new AcSharedMemoryTelemetrySource
                {
                    Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                };
                try
                {
                    ac.Start();
                    newSource = ac;
                }
                catch (Exception ex)
                {
                    try { ac.Dispose(); } catch { }
                    if (!silent)
                    {
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] AC enhanced source unavailable ({ex.GetType().Name}): {ex.Message}; falling back to SimHub.");
                    }
                }
            }
            else if (IsForzaGameName(game))
            {
                // The Forza UDP reader is the only source of Forza's per-tire
                // surface / kerb / cylinder data, so it is always on for Forza
                // (no user toggle). It binds only while a Forza title is the
                // active game; a port conflict falls back to SimHub below.
                {
                    try
                    {
                        var bindIp = ParseIpOrAny(Settings.Forza.BindAddress);
                        var forwardTo = BuildForzaForwardEndpoint(Settings.Forza);
                        var fz = new ForzaUdpTelemetrySource(Settings.Forza.Port, bindIp, forwardTo)
                        {
                            Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                        };
                        fz.Start();
                        newSource = fz;
                    }
                    catch (Exception ex)
                    {
                        if (!silent)
                        {
                            SimHub.Logging.Current.Info(
                                $"[Trueforce] Forza UDP source unavailable on port {Settings.Forza.Port} " +
                                $"({ex.GetType().Name}): {ex.Message}; falling back to SimHub. " +
                                "If another listener (SimHub itself, Sim Racing Studio) holds the port, change Trueforce's port to a free one and re-point Forza's Data Out to it.");
                        }
                    }
                }
            }
            if (newSource == null) newSource = _simHubSource;
            if (newSource == _telemetrySource) return;

            // Detach old's dispatch BEFORE attaching new's so DispatchFrame is
            // never invoked from two threads concurrently. Both fields are
            // ref-typed; .NET guarantees torn-tear-safe writes.
            var old = _telemetrySource;
            if (old != null) old.OnFrame = null;
            newSource.OnFrame = DispatchFrame;
            _telemetrySource = newSource;

            // Reset port-discovery state on every source swap so a fresh
            // start (Forza was idle or just launched, port changed in
            // settings, etc.) restarts the discovery cycle.
            _discoverySourceStartedTicks = Stopwatch.GetTimestamp();
            _discoveryNextAttemptTicks   = 0;
            _discoverySourceKey = newSource;
            System.Threading.Volatile.Write(ref _discoveredAlternatePort, 0);

            // Dispose the previous enhanced source. _simHubSource is the
            // long-lived fallback and stays alive for the plugin's lifetime.
            if (old != null && old != _simHubSource && old != newSource)
            {
                try { old.Dispose(); } catch { }
            }

            SimHub.Logging.Current.Info(
                $"[Trueforce] Telemetry source: {newSource.Name} (enhanced={newSource.IsEnhanced}).");
        }

        // ---------- Port discovery ----------

        // Polled from DataUpdate. Triggers a scan when:
        //   - The active source is a UDP source (Forza).
        //   - It's been running for >DiscoveryNoPacketsTriggerMs without
        //     receiving any packets.
        //   - The retry-interval gate has elapsed (DiscoveryRetryIntervalMs
        //     between attempts) so we keep trying if the user enables UDP
        //     in the game minutes after Trueforce starts.
        // Runs each scan on a background thread so the SimHub data tick
        // isn't blocked. _discoveryScanInFlight prevents overlapping runs.
        private bool _discoveryScanInFlight;
        private void MaybeStartPortDiscovery()
        {
            if (_discoveryScanInFlight) return;

            var src = _telemetrySource;
            if (src == null || src != _discoverySourceKey) return;

            // Source must be a UDP one with a "received N packets" counter
            // we can read. AC's MMF source and the SimHub fallback don't
            // benefit from port discovery.
            long received;
            int[] candidates;
            int currentPort;
            string kind;
            Func<byte[], int, bool> validator;
            if (src is ForzaUdpTelemetrySource fz)
            {
                received    = fz.PacketsReceived;
                candidates  = ForzaUdpTelemetrySource.DiscoveryCandidatePorts;
                currentPort = Settings?.Forza?.Port ?? 0;
                validator   = ForzaUdpTelemetrySource.IsValidPacketCandidate;
                kind        = "forza";
            }
            else return;

            if (received > 0) return;

            long now = Stopwatch.GetTimestamp();
            // First attempt: source must have been running at least
            // DiscoveryNoPacketsTriggerMs. Subsequent attempts: gated by
            // _discoveryNextAttemptTicks (set by the previous attempt's
            // completion).
            if (_discoveryNextAttemptTicks == 0)
            {
                long elapsedMs = (now - _discoverySourceStartedTicks) * 1000L / Stopwatch.Frequency;
                if (elapsedMs < DiscoveryNoPacketsTriggerMs) return;
            }
            else if (now < _discoveryNextAttemptTicks)
            {
                return;
            }

            // Filter the candidate list to skip the user's currently-configured
            // port, we already know that one isn't receiving (received==0).
            var filtered = new List<int>(candidates.Length);
            foreach (int p in candidates) if (p != currentPort) filtered.Add(p);
            if (filtered.Count == 0) return;

            _discoveryScanInFlight = true;

            var bindIp = ParseIpOrAny(Settings?.Forza?.BindAddress);
            System.Threading.Tasks.Task.Run(() =>
            {
                int hit = 0;
                try
                {
                    hit = UdpPortScanner.Scan(filtered, bindIp, validator,
                        DiscoveryScanTimeoutMs, System.Threading.CancellationToken.None);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Port discovery error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _discoveryScanInFlight = false;
                    // Schedule the next allowed attempt. If the user has
                    // already adopted a discovered port and the source is
                    // now receiving packets, the receive-check at the top
                    // of MaybeStartPortDiscovery will short-circuit; we
                    // still set the gate so any future "back to zero"
                    // window has a fresh deadline rather than firing
                    // immediately.
                    _discoveryNextAttemptTicks = Stopwatch.GetTimestamp()
                        + Stopwatch.Frequency * DiscoveryRetryIntervalMs / 1000L;
                }

                if (hit > 0)
                {
                    System.Threading.Volatile.Write(ref _discoveredAlternatePort, hit);
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] Detected {kind} packets on alternate port {hit}.");
                    try { AlternatePortDiscovered?.Invoke(kind, hit); } catch { }
                }
            });
        }

        /// <summary>UI hook: switch the Forza listener to the
        /// just-discovered port and persist. Returns true if the switch
        /// was applied.</summary>
        public bool AdoptDiscoveredAlternatePort()
        {
            int port = DiscoveredAlternatePort;
            if (port <= 0 || Settings == null) return false;

            var src = _telemetrySource;
            if (src is ForzaUdpTelemetrySource && Settings.Forza != null)
            {
                Settings.Forza.Port = port;
                ApplyForzaSettings();
            }
            else return false;

            System.Threading.Volatile.Write(ref _discoveredAlternatePort, 0);
            return true;
        }

        /// <summary>UI hook: dismiss the discovered-port banner without
        /// switching. Suppresses re-discovery for the remainder of this
        /// source instance.</summary>
        public void DismissDiscoveredAlternatePort()
        {
            System.Threading.Volatile.Write(ref _discoveredAlternatePort, 0);
        }

        // 0.0.0.0 / blank / unparseable → IPAddress.Any so the listener accepts
        // packets on every local interface. Specific IPs are honored as-is.
        private static IPAddress ParseIpOrAny(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return IPAddress.Any;
            return IPAddress.TryParse(s.Trim(), out var ip) ? ip : IPAddress.Any;
        }

        // Build the forward endpoint used by ForzaUdpTelemetrySource.
        // Returns null when forwarding is disabled or the user's host/port is
        // invalid, the source treats null as "don't forward." Hostname (vs
        // IP) lookups go through Dns.GetHostAddresses so users can type
        // "localhost" or a NAS hostname; first resolved address wins.
        private static IPEndPoint BuildForzaForwardEndpoint(ForzaSettings fs)
        {
            if (fs == null || !fs.ForwardEnabled) return null;
            if (fs.ForwardPort < 1 || fs.ForwardPort > 65535) return null;
            string host = string.IsNullOrWhiteSpace(fs.ForwardHost) ? "127.0.0.1" : fs.ForwardHost.Trim();
            try
            {
                if (IPAddress.TryParse(host, out var ip))
                    return new IPEndPoint(ip, fs.ForwardPort);
                var addrs = System.Net.Dns.GetHostAddresses(host);
                foreach (var a in addrs)
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return new IPEndPoint(a, fs.ForwardPort);
                }
            }
            catch { }
            return null;
        }

        /// <summary>UI hook: persist the Forza section and rebind the listener
        /// when needed. Settings are saved unconditionally. The source is
        /// rebuilt whenever either (a) a Forza source is currently running
        /// (so port/bind/forward changes take effect, or a disable tears it
        /// down) or (b) a Forza title is the active game (so a port/bind
        /// change applies without waiting for a game change).</summary>
        public void ApplyForzaSettings()
        {
            if (Settings?.Forza == null) return;
            this.SaveCommonSettings("GeneralSettings", Settings);

            bool currentlyForza = _telemetrySource is ForzaUdpTelemetrySource;
            bool shouldListen   = !string.IsNullOrEmpty(_activeGame) && IsForzaGameName(_activeGame);
            if (!currentlyForza && !shouldListen) return;

            // Route through the SimHub fallback first so the old source's
            // dispose runs cleanly before SwapTelemetrySource (re)builds.
            // SwapTelemetrySource decides what to attach next based on the
            // new settings + active game; if we're disabling, it'll fall
            // through to a non-Forza source (SimHub).
            if (currentlyForza)
            {
                var oldFz = _telemetrySource;
                oldFz.OnFrame = null;
                _simHubSource.OnFrame = DispatchFrame;
                _telemetrySource = _simHubSource;
                try { oldFz.Dispose(); } catch { }
            }
            SwapTelemetrySource(_activeGame);
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        // ---------- per-car overrides (per-section) ----------

        public CarOverride GetActiveCarOverride()
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null) return null;
            Settings.CarOverrides.TryGetValue(_activeCarId, out var o);
            return o;
        }

        /// <summary>Apply per-section overrides for the active car (or revert to globals).</summary>
        public void ApplyActiveCarOverride()
        {
            if (Settings == null) return;
            var ovr = GetActiveCarOverride();

            ApplyEngineSettings(ovr?.EnginePulse  ?? Settings.EnginePulse);
            ApplyBumpsSettings (ovr?.RoadBumps    ?? Settings.RoadBumps);
            ApplyTractionSettings(ovr?.TractionLoss ?? Settings.TractionLoss);
            ApplyShiftSettings (ovr?.GearShift    ?? Settings.GearShift);
            ApplyAbsSettings   (ovr?.AbsClick     ?? Settings.AbsClick);
            ApplyPitLimiterSettings(ovr?.PitLimiter ?? Settings.PitLimiter);
            ApplyDrsSettings   (ovr?.Drs          ?? Settings.Drs);
            ApplyCollisionSettings(ovr?.Collision ?? Settings.Collision);
            ApplyRevLimiterSettings(ovr?.RevLimiter ?? Settings.RevLimiter);
            ApplyAudioCaptureSettings(ovr?.AudioCapture ?? Settings.AudioCapture);
            ApplyAirborneSettings();   // global, no per-car override
        }

        /// <summary>Copy another car preset's override values onto the ACTIVE
        /// car and apply them live. Used by the header picker when the user
        /// selects a DIFFERENT car's preset: its tuning lands on the current
        /// car as an (unsaved) override they can then save. No-op when no car
        /// is loaded or the source is null.</summary>
        public bool ApplyCarOverrideToActiveCar(CarOverride source)
        {
            if (Settings == null || string.IsNullOrEmpty(_activeCarId) || source == null) return false;
            if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();
            Settings.CarOverrides[_activeCarId] = CloneCarOverride(source);
            ApplyActiveCarOverride();
            return true;
        }

        // ===================================================================
        // Per-car override DRAFT model (PILOT: RevLimiter).
        // While a car is loaded, slider edits land in the car's IN-MEMORY
        // override (created on first edit from the current effective value),
        // never the global default, so tuning one car can't leak to others.
        // Nothing persists until an explicit Save. _lastPersistedCarOverrides
        // is the saved baseline; the gap to Settings.CarOverrides is the unsaved
        // draft, which is discarded on car change.
        // ===================================================================

        // Called before a section edit: ensure the car's in-memory override has
        // THIS section (seeded from the current effective value) so the edit
        // writes to the car, not the global. No-op with no car loaded (edits
        // then target the global default, as intended) or for non-car sections.
        public void EnsureSectionDraft(SectionKind kind)
        {
            if (!string.IsNullOrEmpty(_activeCarId) && SectionHasCarScope(kind))
                SnapshotSectionToCarOverride(kind);   // creates the section from global only when absent
        }

        // "Reset to default" draft: drop THIS section's override in memory so it
        // previews the game default live. Not persisted; Save commits the
        // removal (follow default), Revert restores the saved override.
        public void ResetSectionToDefaultDraft(SectionKind kind)
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings?.CarOverrides == null) return;
            if (Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr) && ovr != null)
            {
                ClearOverrideSection(ovr, kind);
                if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            }
            ApplyActiveCarOverride();
        }

        // Discard a car's UNSAVED draft (on car change): restore its in-memory
        // override from the persisted baseline, or drop it if nothing was saved.
        private void DiscardUnsavedCarDraft(string carId)
        {
            if (string.IsNullOrEmpty(carId) || Settings?.CarOverrides == null) return;
            if (_lastPersistedCarOverrides != null
                && _lastPersistedCarOverrides.TryGetValue(carId, out var saved)
                && saved != null && !saved.IsEmpty)
                Settings.CarOverrides[carId] = CloneCarOverride(saved);
            else
                Settings.CarOverrides.Remove(carId);
        }

        // Revert THIS section's draft to the car's saved state (persisted
        // override, or the game default if none). No car loaded → revert the
        // global section to the active preset (existing behavior).
        public void RevertSectionDraft(SectionKind kind)
        {
            if (string.IsNullOrEmpty(_activeCarId)) { RevertSection(kind); return; }
            if (Settings?.CarOverrides == null) return;
            CarOverride saved = null;
            _lastPersistedCarOverrides?.TryGetValue(_activeCarId, out saved);
            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            if (OverrideHasSection(saved, kind))
            {
                if (ovr == null) { ovr = new CarOverride(); Settings.CarOverrides[_activeCarId] = ovr; }
                CopyOverrideSection(saved, ovr, kind);
            }
            else if (ovr != null)
            {
                ClearOverrideSection(ovr, kind);
                if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            }
            ApplyActiveCarOverride();
        }

        // Save scope: the draft becomes the GAME DEFAULT and stays pinned to
        // this car (both the default and this car's override get the values).
        // Reuses PromoteSectionToGlobal (lift override -> global) but re-pins the
        // override afterward, then persists both the preset and the car file.
        public bool SaveSectionToBoth(SectionKind kind)
        {
            PromoteSectionToGlobal(kind);                  // global = override values; drops the override
            SnapshotSectionToCarOverride(kind);            // re-pin: override = global (the same values)
            bool okDefault = SaveSectionToActivePreset(kind);
            bool okCar     = SaveSectionToActiveCarOverride(kind);
            ApplyActiveCarOverride();
            return okDefault || okCar;
        }

        // Commit a "reset" draft: persist that this car has NO override for the
        // section (follows the game default). Patches the on-disk car file to
        // drop just this section.
        public bool CommitSectionFollowDefault(SectionKind kind)
        {
            ResetSectionToDefaultDraft(kind);                  // clear in-memory section (preview default)
            return RemoveSectionFromActiveCarOverrideFile(kind);
        }

        // Patch the active car's on-disk override file to DROP a section, so the
        // car follows the game default for it. Mirrors SaveSectionToActiveCarOverride
        // but clears the section and needs no live in-memory override.
        public bool RemoveSectionFromActiveCarOverrideFile(SectionKind kind)
        {
            if (_carStore == null || string.IsNullOrEmpty(_activeCarId)) return false;
            string presetName = GetActiveCarPresetName(_activeCarId);
            if (string.IsNullOrEmpty(presetName)) return false;
            bool isBuiltin = IsCarPresetBuiltin(_activeCarId, presetName);
            if (isBuiltin && !DevMode) return false;
            if (!_lastPersistedCarOverrides.TryGetValue(_activeCarId, out var prev) || prev == null)
                return true;   // nothing persisted -> already follows the default

            var patched = CloneCarOverride(prev);
            ClearOverrideSection(patched, kind);
            if (isBuiltin)
            {
                WriteCarBuiltinThroughDev(_activeCarId, presetName, patched);
            }
            else
            {
                _carStore.Save(_activeCarId, presetName, _activeGame ?? "", patched, isBuiltin: false,
                    defaultAuthor: CurrentAuthorForStamp());
                if (patched.IsEmpty)
                    _lastPersistedCarOverrides.Remove(_activeCarId);
                else
                    _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(patched);
            }
            SimHub.Logging.Current.Info($"[Trueforce] Cleared {kind} override for '{_activeCarId}' (follows game default).");
            return true;
        }

        // For the save popover: is THIS section a "reset" draft (override cleared
        // in memory while one is still persisted)? Then Save means "follow the
        // game default" (commit the removal), not a scope choice.
        public bool IsSectionResetDraft(SectionKind kind)
        {
            if (string.IsNullOrEmpty(_activeCarId)) return false;
            CarOverride live = null, saved = null;
            Settings?.CarOverrides?.TryGetValue(_activeCarId, out live);
            _lastPersistedCarOverrides?.TryGetValue(_activeCarId, out saved);
            return !OverrideHasSection(live, kind) && OverrideHasSection(saved, kind);
        }

        // True when the active car's in-memory override has THIS section (a
        // saved override or an unsaved draft). Drives the popover's
        // reset/follow-default affordances.
        public bool IsSectionOverridden(SectionKind kind) => OverrideHasSection(GetActiveCarOverride(), kind);

        // Sections that support a per-car override (have a field on CarOverride).
        // Master / Ducking / SpikeReduction are global-only.
        public static bool SectionHasCarScope(SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Engine:
                case SectionKind.Bumps:
                case SectionKind.Traction:
                case SectionKind.Shift:
                case SectionKind.Abs:
                case SectionKind.PitLimiter:
                case SectionKind.Drs:
                case SectionKind.Collision:
                case SectionKind.RevLimiter:
                case SectionKind.Audio:
                case SectionKind.Airborne:
                    return true;
                default:
                    return false;
            }
        }

        // ---- generic per-section accessors on a CarOverride ----
        private static bool OverrideHasSection(CarOverride ovr, SectionKind kind)
        {
            if (ovr == null) return false;
            switch (kind)
            {
                case SectionKind.Engine:     return ovr.EnginePulse  != null;
                case SectionKind.Bumps:      return ovr.RoadBumps    != null;
                case SectionKind.Traction:   return ovr.TractionLoss != null;
                case SectionKind.Shift:      return ovr.GearShift    != null;
                case SectionKind.Abs:        return ovr.AbsClick     != null;
                case SectionKind.PitLimiter: return ovr.PitLimiter   != null;
                case SectionKind.Drs:        return ovr.Drs          != null;
                case SectionKind.Collision:  return ovr.Collision    != null;
                case SectionKind.RevLimiter: return ovr.RevLimiter   != null;
                case SectionKind.Audio:      return ovr.AudioCapture != null;
                case SectionKind.Airborne:   return ovr.Airborne     != null;
                default: return false;
            }
        }
        private static void ClearOverrideSection(CarOverride ovr, SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Engine:     ovr.EnginePulse  = null; break;
                case SectionKind.Bumps:      ovr.RoadBumps    = null; break;
                case SectionKind.Traction:   ovr.TractionLoss = null; break;
                case SectionKind.Shift:      ovr.GearShift    = null; break;
                case SectionKind.Abs:        ovr.AbsClick     = null; break;
                case SectionKind.PitLimiter: ovr.PitLimiter   = null; break;
                case SectionKind.Drs:        ovr.Drs          = null; break;
                case SectionKind.Collision:  ovr.Collision    = null; break;
                case SectionKind.RevLimiter: ovr.RevLimiter   = null; break;
                case SectionKind.Audio:      ovr.AudioCapture = null; break;
                case SectionKind.Airborne:   ovr.Airborne     = null; break;
            }
        }
        private void CopyOverrideSection(CarOverride from, CarOverride to, SectionKind kind)
        {
            switch (kind)
            {
                case SectionKind.Engine:     to.EnginePulse  = Clone(from.EnginePulse);  break;
                case SectionKind.Bumps:      to.RoadBumps    = Clone(from.RoadBumps);    break;
                case SectionKind.Traction:   to.TractionLoss = Clone(from.TractionLoss); break;
                case SectionKind.Shift:      to.GearShift    = Clone(from.GearShift);    break;
                case SectionKind.Abs:        to.AbsClick     = Clone(from.AbsClick);     break;
                case SectionKind.PitLimiter: to.PitLimiter   = Clone(from.PitLimiter);   break;
                case SectionKind.Drs:        to.Drs          = Clone(from.Drs);          break;
                case SectionKind.Collision:  to.Collision    = Clone(from.Collision);    break;
                case SectionKind.RevLimiter: to.RevLimiter   = Clone(from.RevLimiter);   break;
                case SectionKind.Audio:      to.AudioCapture = CloneOrNull(from.AudioCapture); break;
                case SectionKind.Airborne:   to.Airborne     = Clone(from.Airborne);     break;
            }
        }

        // ----- per-section: is this section overridden for the active car? -----
        public bool IsEngineOverridden     => GetActiveCarOverride()?.EnginePulse  != null;
        public bool IsBumpsOverridden      => GetActiveCarOverride()?.RoadBumps    != null;
        public bool IsTractionOverridden   => GetActiveCarOverride()?.TractionLoss != null;
        public bool IsShiftOverridden      => GetActiveCarOverride()?.GearShift    != null;
        public bool IsAbsOverridden        => GetActiveCarOverride()?.AbsClick     != null;
        public bool IsPitLimiterOverridden => GetActiveCarOverride()?.PitLimiter   != null;
        public bool IsDrsOverridden        => GetActiveCarOverride()?.Drs          != null;
        public bool IsCollisionOverridden  => GetActiveCarOverride()?.Collision    != null;
        public bool IsRevLimiterOverridden => GetActiveCarOverride()?.RevLimiter   != null;
        public bool IsAudioOverridden      => GetActiveCarOverride()?.AudioCapture != null;

        // ----- per-section: toggle override on/off (snapshots globals when on) -----
        public void SetEngineOverride(bool on)     => ToggleSectionOverride(on, get: o => o.EnginePulse,  set: (o, v) => o.EnginePulse  = v, snapshot: () => Clone(Settings.EnginePulse));
        public void SetBumpsOverride(bool on)      => ToggleSectionOverride(on, get: o => o.RoadBumps,    set: (o, v) => o.RoadBumps    = v, snapshot: () => Clone(Settings.RoadBumps));
        public void SetTractionOverride(bool on)   => ToggleSectionOverride(on, get: o => o.TractionLoss, set: (o, v) => o.TractionLoss = v, snapshot: () => Clone(Settings.TractionLoss));
        public void SetShiftOverride(bool on)      => ToggleSectionOverride(on, get: o => o.GearShift,    set: (o, v) => o.GearShift    = v, snapshot: () => Clone(Settings.GearShift));
        public void SetAbsOverride(bool on)        => ToggleSectionOverride(on, get: o => o.AbsClick,     set: (o, v) => o.AbsClick     = v, snapshot: () => Clone(Settings.AbsClick));
        public void SetPitLimiterOverride(bool on) => ToggleSectionOverride(on, get: o => o.PitLimiter,   set: (o, v) => o.PitLimiter   = v, snapshot: () => Clone(Settings.PitLimiter));
        public void SetDrsOverride(bool on)        => ToggleSectionOverride(on, get: o => o.Drs,          set: (o, v) => o.Drs          = v, snapshot: () => Clone(Settings.Drs));
        public void SetCollisionOverride(bool on)  => ToggleSectionOverride(on, get: o => o.Collision,    set: (o, v) => o.Collision    = v, snapshot: () => Clone(Settings.Collision));
        public void SetRevLimiterOverride(bool on) => ToggleSectionOverride(on, get: o => o.RevLimiter,   set: (o, v) => o.RevLimiter   = v, snapshot: () => Clone(Settings.RevLimiter));
        public void SetAudioOverride(bool on)      => ToggleSectionOverride(on, get: o => o.AudioCapture, set: (o, v) => o.AudioCapture = v, snapshot: () => CloneOrNull(Settings.AudioCapture));

        private void ToggleSectionOverride<T>(bool on,
            Func<CarOverride, T> get,
            Action<CarOverride, T> set,
            Func<T> snapshot) where T : class
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null) return;
            if (!Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr))
            {
                if (!on) return;     // toggling off when none exists is a no-op
                ovr = new CarOverride();
                Settings.CarOverrides[_activeCarId] = ovr;
            }
            set(ovr, on ? snapshot() : null);
            if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            PersistActiveCarOverride();
            ApplyActiveCarOverride();
        }

        // ----- write helpers used by the UI sliders -----
        // Each routes to the per-car section if it's overridden, else to the global section.
        public EnginePulseSettings  ActiveEngine   => GetActiveCarOverride()?.EnginePulse  ?? Settings.EnginePulse;
        public RoadBumpsSettings    ActiveBumps    => GetActiveCarOverride()?.RoadBumps    ?? Settings.RoadBumps;
        public TractionLossSettings ActiveTraction => GetActiveCarOverride()?.TractionLoss ?? Settings.TractionLoss;
        public GearShiftSettings    ActiveShift    => GetActiveCarOverride()?.GearShift    ?? Settings.GearShift;
        public AbsClickSettings     ActiveAbs        => GetActiveCarOverride()?.AbsClick     ?? Settings.AbsClick;
        public PitLimiterSettings   ActivePitLimiter => GetActiveCarOverride()?.PitLimiter   ?? Settings.PitLimiter;
        public DrsSettings          ActiveDrs        => GetActiveCarOverride()?.Drs          ?? Settings.Drs;
        public CollisionSettings    ActiveCollision  => GetActiveCarOverride()?.Collision    ?? Settings.Collision;
        public RevLimiterSettings   ActiveRevLimiter => GetActiveCarOverride()?.RevLimiter   ?? Settings.RevLimiter;
        public AudioCaptureSettings ActiveAudio    => GetActiveCarOverride()?.AudioCapture ?? Settings.AudioCapture;
        public AirborneSettings     ActiveAirborne => GetActiveCarOverride()?.Airborne     ?? Settings.Airborne;

        // ----- apply settings to live effect -----
        private void ApplyEngineSettings(EnginePulseSettings s)
        {
            if (EnginePulse == null || s == null) return;

            // One-shot legacy migration: pre-flat-enum settings carry
            // (Cylinders, EngineConfig, FiringOrderEnabled) and a default-Auto
            // Layout. Fold them into Layout and clear so we don't migrate
            // again on the next apply. Layout != Auto means the user (or a
            // prior migration) has already set the new field.
            if (s.Layout == Effects.EngineLayout.Auto
                && (s.Cylinders != 0 || s.EngineConfig != Effects.EngineConfig.Auto))
            {
                s.Layout = Effects.FiringPatternDb.LayoutFromLegacy(
                    s.Cylinders, s.EngineConfig, false);
                s.Cylinders     = 0;
                s.EngineConfig  = Effects.EngineConfig.Auto;
            }

            // Custom-engine library migration: pre-library presets stored the
            // custom pattern inline in CustomFiringPattern + Name. Mint a
            // library entry (if one with this pattern doesn't exist yet),
            // point CustomEngineId at it, and clear the inline strings so the
            // next apply skips this branch.
            if (s.Layout == Effects.EngineLayout.Custom
                && string.IsNullOrEmpty(s.CustomEngineId)
                && !string.IsNullOrWhiteSpace(s.CustomFiringPattern))
            {
                var def = new CustomEngineDef
                {
                    Id         = Guid.NewGuid().ToString("N"),
                    Name       = string.IsNullOrWhiteSpace(s.CustomFiringPatternName)
                                    ? "Imported custom"
                                    : s.CustomFiringPatternName.Trim(),
                    IsElectric = false,
                    Pattern    = s.CustomFiringPattern,
                };
                if (Settings.CustomEngines == null)
                    Settings.CustomEngines = new System.Collections.Generic.List<CustomEngineDef>();
                Settings.CustomEngines.Add(def);
                s.CustomEngineId           = def.Id;
                s.CustomFiringPattern      = "";
                s.CustomFiringPatternName  = "";
            }

            EnginePulse.Enabled            = s.Enabled;
            EnginePulse.Gain               = s.Gain;
            EnginePulse.PitchMultiplier    = s.Pitch;
            EnginePulse.LowpassHz          = s.LowpassHz;
            EnginePulse.Waveform           = s.Waveform;
            EnginePulse.Layout             = s.Layout;
            EnginePulse.LoadLayerEnabled   = s.LoadLayerEnabled;
            EnginePulse.LoadLayerGain      = s.LoadLayerGain;
            EnginePulse.HighRpmBoostEnabled = s.HighRpmBoostEnabled;
            EnginePulse.HighRpmBoostAmount = s.HighRpmBoostAmount;

            // Custom-engine resolution. When Layout == Custom, look up the
            // referenced entry in the global library and write its pattern /
            // electric flag into the runtime effect. Missing entries fall
            // back to silence (CustomPattern=null) and a logged warning so
            // the user notices and can repick.
            CustomEngineDef activeCustom = null;
            if (s.Layout == Effects.EngineLayout.Custom
                && !string.IsNullOrEmpty(s.CustomEngineId)
                && Settings.CustomEngines != null)
            {
                foreach (var c in Settings.CustomEngines)
                {
                    if (string.Equals(c?.Id, s.CustomEngineId, StringComparison.Ordinal))
                    {
                        activeCustom = c;
                        break;
                    }
                }
                if (activeCustom == null)
                {
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] Preset references custom engine Id '{s.CustomEngineId}' "
                        + "that's no longer in the library, falling back to silence.");
                }
            }
            EnginePulse.ActiveCustomIsElectric = activeCustom != null && activeCustom.IsElectric;
            EnginePulse.CustomPattern = activeCustom != null && !activeCustom.IsElectric
                                        && !string.IsNullOrWhiteSpace(activeCustom.Pattern)
                ? Effects.FiringPatternDb.ParseCustom(activeCustom.Pattern)
                : null;

            // ElectricMode cascade: if the active custom is electric, its mode
            // wins (a per-custom override of the per-preset default). Else
            // the per-preset setting drives EV behavior, matching the prior
            // single-Electric-mode model.
            EnginePulse.ElectricMode = (activeCustom != null && activeCustom.IsElectric)
                ? activeCustom.ElectricMode
                : s.ElectricMode;
        }
        private void ApplyBumpsSettings(RoadBumpsSettings s)
        {
            if (RoadBumps == null || s == null) return;
            RoadBumps.Enabled            = s.Enabled;
            RoadBumps.Gain               = s.Gain;
            RoadBumps.Waveform           = s.Waveform;
            RoadBumps.Freq               = s.Freq;
            RoadBumps.SurfaceEnabled     = s.SurfaceEnabled;
            RoadBumps.SurfaceGain        = s.SurfaceGain;
            RoadBumps.SurfaceFreq        = s.SurfaceFreq;
            RoadBumps.SurfaceWaveform    = s.SurfaceWaveform;
            RoadBumps.SurfaceLowpassHz   = s.SurfaceLowpassHz;
            RoadBumps.SurfaceHighpassHz  = s.SurfaceHighpassHz;
            RoadBumps.SurfaceRumbleScale = s.SurfaceRumbleScale;
            RoadBumps.RumbleStripPulseAmp = s.RumbleStripPulseAmp;
            RoadBumps.RumbleStripPulseMs  = s.RumbleStripPulseMs;
        }
        private void ApplyTractionSettings(TractionLossSettings s)
        {
            if (TractionLoss == null || s == null) return;
            TractionLoss.Enabled         = s.Enabled;
            TractionLoss.Gain            = s.Gain;
            TractionLoss.Sensitivity     = s.Sensitivity;
            TractionLoss.Waveform        = s.Waveform;
            TractionLoss.Freq            = s.Freq;
            TractionLoss.NoiseLowpassHz  = s.NoiseLowpassHz;
            TractionLoss.NoiseHighpassHz = s.NoiseHighpassHz;
        }
        private void ApplyShiftSettings(GearShiftSettings s)
        {
            if (GearShift == null || s == null) return;
            GearShift.Enabled  = s.Enabled;
            GearShift.Gain     = s.Gain;
            GearShift.Freq     = s.Freq;
            GearShift.Waveform = s.Waveform;
        }
        private void ApplyAbsSettings(AbsClickSettings s)
        {
            if (AbsClick == null || s == null) return;
            AbsClick.Enabled        = s.Enabled;
            AbsClick.Gain           = s.Gain;
            AbsClick.Freq           = s.Freq;
            AbsClick.PulseFreq      = s.PulseFreq;
            AbsClick.DutyCycle      = s.DutyCycle;
            AbsClick.TickDurationMs = s.TickDurationMs;
            AbsClick.Mode           = s.Mode;
            AbsClick.Waveform       = s.Waveform;
        }
        private void ApplyPitLimiterSettings(PitLimiterSettings s)
        {
            if (PitLimiter == null || s == null) return;
            PitLimiter.Enabled    = s.Enabled;
            PitLimiter.Gain       = s.Gain;
            PitLimiter.Freq       = s.Freq;
            PitLimiter.PulseFreq  = s.PulseFreq;
            PitLimiter.DutyCycle  = s.DutyCycle;
            PitLimiter.ActiveAmp  = s.ActiveAmp;
            PitLimiter.Waveform   = s.Waveform;
        }
        private void ApplyDrsSettings(DrsSettings s)
        {
            if (Drs == null || s == null) return;
            Drs.Enabled           = s.Enabled;
            Drs.Gain              = s.Gain;
            Drs.ActivationFreq    = s.ActivationFreq;
            Drs.ActivationMs      = s.ActivationMs;
            Drs.ActivationAmp     = s.ActivationAmp;
            Drs.SustainedFreq     = s.SustainedFreq;
            Drs.SustainedAmp      = s.SustainedAmp;
            Drs.Waveform          = s.Waveform;
            Drs.SustainedWaveform = s.SustainedWaveform;
        }
        private void ApplyCollisionSettings(CollisionSettings s)
        {
            if (Collision == null || s == null) return;
            Collision.Enabled            = s.Enabled;
            Collision.Gain               = s.Gain;
            Collision.Freq               = s.Freq;
            Collision.EnvelopeMs         = s.EnvelopeMs;
            Collision.MinThreshold       = s.MinThreshold;
            Collision.MinAmp             = s.MinAmp;
            Collision.MaxAmp             = s.MaxAmp;
            Collision.NormalizationScale = s.NormalizationScale;
            Collision.RefractoryMs       = s.RefractoryMs;
            Collision.Waveform           = s.Waveform;
        }
        private void ApplyRevLimiterSettings(RevLimiterSettings s)
        {
            if (RevLimiter == null || s == null) return;
            RevLimiter.Enabled   = s.Enabled;
            RevLimiter.Gain      = s.Gain;
            RevLimiter.Freq      = s.Freq;
            RevLimiter.PulseFreq = s.PulseFreq;
            RevLimiter.DutyCycle = s.DutyCycle;
            RevLimiter.ActiveAmp = s.ActiveAmp;
            RevLimiter.Threshold = s.Threshold;
            RevLimiter.RedlineOffsetRpm = s.RedlineOffsetRpm;
            RevLimiter.EngageMode = s.EngageMode;
            RevLimiter.Waveform  = s.Waveform;
        }
        // Airborne ducking can be per-car (override) or global; this reads the
        // effective value (ActiveAirborne = car override if present, else the
        // global). Public so the settings panel can re-apply live after an edit.
        public void ApplyAirborneSettings()
        {
            var s = ActiveAirborne;
            if (Airborne == null || s == null) return;
            Airborne.Enabled          = s.Enabled;
            Airborne.Reduction        = s.Reduction;
            Airborne.DuckEngine       = s.DuckEngine;
            Airborne.DuckAudio        = s.DuckAudio;
            Airborne.DuckRoadBumps    = s.DuckRoadBumps;
            Airborne.DuckTractionLoss = s.DuckTractionLoss;
            Airborne.DuckRevLimiter   = s.DuckRevLimiter;
            Airborne.DuckGearShift    = s.DuckGearShift;
            Airborne.DuckAbs          = s.DuckAbs;
            Airborne.DuckPitLimiter   = s.DuckPitLimiter;
            Airborne.DuckDrs          = s.DuckDrs;
            Airborne.DuckCollision    = s.DuckCollision;
        }
        private void ApplyAudioCaptureSettings(AudioCaptureSettings s)
        {
            if (_audio == null || s == null) return;
            _audio.Enabled          = s.Enabled;
            _audio.Gain             = s.Gain;
            _audio.LowpassCutoffHz  = s.LowpassCutoffHz;
            _audio.HighpassCutoffHz = s.HighpassCutoffHz;
        }

        // ----- shallow clones used when toggling override on -----
        private static EnginePulseSettings  Clone(EnginePulseSettings s)
            => new EnginePulseSettings  {
                Enabled = s.Enabled, Gain = s.Gain, Pitch = s.Pitch,
                LowpassHz = s.LowpassHz, Waveform = s.Waveform, ElectricMode = s.ElectricMode,
                Layout = s.Layout, CustomEngineId = s.CustomEngineId,
                CustomFiringPattern = s.CustomFiringPattern,
                CustomFiringPatternName = s.CustomFiringPatternName,
                LoadLayerEnabled = s.LoadLayerEnabled,
                LoadLayerGain    = s.LoadLayerGain,
                HighRpmBoostEnabled = s.HighRpmBoostEnabled,
                HighRpmBoostAmount  = s.HighRpmBoostAmount,
            };
        private static RoadBumpsSettings    Clone(RoadBumpsSettings s)
            => new RoadBumpsSettings    {
                Enabled = s.Enabled, Gain = s.Gain, Waveform = s.Waveform, Freq = s.Freq,
                SurfaceEnabled = s.SurfaceEnabled, SurfaceGain = s.SurfaceGain, SurfaceFreq = s.SurfaceFreq,
                SurfaceWaveform = s.SurfaceWaveform, SurfaceLowpassHz = s.SurfaceLowpassHz,
                SurfaceHighpassHz = s.SurfaceHighpassHz, SurfaceRumbleScale = s.SurfaceRumbleScale,
                RumbleStripPulseAmp = s.RumbleStripPulseAmp, RumbleStripPulseMs = s.RumbleStripPulseMs,
            };
        private static TractionLossSettings Clone(TractionLossSettings s)
            => new TractionLossSettings { Enabled = s.Enabled, Gain = s.Gain, Sensitivity = s.Sensitivity, Waveform = s.Waveform, Freq = s.Freq, NoiseLowpassHz = s.NoiseLowpassHz, NoiseHighpassHz = s.NoiseHighpassHz };
        private static GearShiftSettings    Clone(GearShiftSettings s)
            => new GearShiftSettings    { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, Waveform = s.Waveform };
        private static AbsClickSettings     Clone(AbsClickSettings s)
            => new AbsClickSettings     { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, PulseFreq = s.PulseFreq, DutyCycle = s.DutyCycle, TickDurationMs = s.TickDurationMs, Mode = s.Mode, Waveform = s.Waveform };
        private static PitLimiterSettings   Clone(PitLimiterSettings s)
            => new PitLimiterSettings   { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, PulseFreq = s.PulseFreq, DutyCycle = s.DutyCycle, ActiveAmp = s.ActiveAmp, Waveform = s.Waveform };
        private static DrsSettings          Clone(DrsSettings s)
            => new DrsSettings          { Enabled = s.Enabled, Gain = s.Gain, ActivationFreq = s.ActivationFreq, ActivationMs = s.ActivationMs, ActivationAmp = s.ActivationAmp, SustainedFreq = s.SustainedFreq, SustainedAmp = s.SustainedAmp, Waveform = s.Waveform, SustainedWaveform = s.SustainedWaveform };
        private static CollisionSettings    Clone(CollisionSettings s)
            => new CollisionSettings    { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, EnvelopeMs = s.EnvelopeMs, MinThreshold = s.MinThreshold, MinAmp = s.MinAmp, MaxAmp = s.MaxAmp, NormalizationScale = s.NormalizationScale, RefractoryMs = s.RefractoryMs, Waveform = s.Waveform };
        private static RevLimiterSettings   Clone(RevLimiterSettings s)
            => new RevLimiterSettings   { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, PulseFreq = s.PulseFreq, DutyCycle = s.DutyCycle, ActiveAmp = s.ActiveAmp, Threshold = s.Threshold, RedlineOffsetRpm = s.RedlineOffsetRpm, EngageMode = s.EngageMode, Waveform = s.Waveform };
        private static AirborneSettings     Clone(AirborneSettings s)
            => new AirborneSettings     { Enabled = s.Enabled, Reduction = s.Reduction, DuckEngine = s.DuckEngine, DuckAudio = s.DuckAudio, DuckRoadBumps = s.DuckRoadBumps, DuckTractionLoss = s.DuckTractionLoss, DuckRevLimiter = s.DuckRevLimiter, DuckGearShift = s.DuckGearShift, DuckAbs = s.DuckAbs, DuckPitLimiter = s.DuckPitLimiter, DuckDrs = s.DuckDrs, DuckCollision = s.DuckCollision };

        // ---------- preset library ----------

        /// <summary>Refresh built-in presets to the shipped JSON. Run on
        /// every Init. Always overwrites the entries in
        /// <c>Settings.Presets</c> keyed by a built-in name, because built-ins
        /// are user-read-only (in-place save forks to a new name) and the
        /// shipped JSON is the source of truth. This catches the case where
        /// an earlier release shipped a preset without a later-added section
        /// (e.g. pre-0.1.3 AC preset had no PitLimiter / Drs / Collision):
        /// without the overwrite, the stale preset lingers in the user's
        /// settings file and the new sections deserialize as null, so the
        /// section sits as permanently-dirty against the C# defaults.
        /// Also auto-binds <see cref="BuiltinPresets.GameDefaultBindings"/>
        /// as each game's default IF the user has no default for that game
        /// yet (we don't override their custom choice).</summary>
        /// <summary>Compatibility shim: previous Init flow + the dev import /
        /// reseed actions called this. New model rebuilds the runtime cache
        /// from both folders (built-in + user library), so just delegate.</summary>
        private void InstallBuiltinPresetsIfMissing()
            => RebuildPresetCacheFromFolders();

        /// <summary>Rebuild the runtime Settings.Presets / GameDefaults cache
        /// from the file folders: user library first (mark as user), then
        /// built-ins overwrite same-named entries (mark as built-in, factory
        /// content wins on collision). Built-in game defaults are seeded for
        /// any game the user hasn't chosen yet. Settings.Presets and
        /// Settings.GameDefaults are runtime caches now, the persistent
        /// storage lives in the folders.</summary>
        private void RebuildPresetCacheFromFolders()
        {
            if (Settings == null) return;
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();

            Settings.Presets.Clear();
            Settings.GameDefaults.Clear();

            // 1) User library: the user's own (non-builtin) presets.
            foreach (var kv in UserPresets.PresetJsons)
            {
                try
                {
                    var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(kv.Value);
                    if (snap != null) Settings.Presets[kv.Key] = snap;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Failed to load user preset '{kv.Key}': {ex.Message}");
                }
            }
            foreach (var kv in UserPresets.GameDefaults)
                Settings.GameDefaults[kv.Key] = kv.Value;

            // 2) Built-ins: overwrite same-named entries (factory wins). The
            //    shipped JSON is the source of truth for built-ins, so this
            //    also catches the "built-in shipped before a later-added
            //    section" case (the section deserializes as null otherwise).
            foreach (var kv in BuiltinPresets.BuiltinPresetJsons)
            {
                try
                {
                    var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(kv.Value);
                    if (snap != null) Settings.Presets[kv.Key] = snap;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Failed to load built-in preset '{kv.Key}': {ex.Message}");
                }
            }
            // Self-heal stale or case-drifted user game-default bindings.
            // Settings.Presets is keyed by the literal filename stem; if a
            // user/game-defaults.json value doesn't match any current key,
            // either correct the case (case-insensitive match) or drop the
            // binding so the factory seed below can fill it in. Runs AFTER
            // factory presets are loaded so factory-named bindings (e.g.
            // "Assetto Corsa  (Built-In)") aren't falsely flagged stale.
            // Closes the class of "Default for column silently empty
            // because user/ and factory/ disagree on case" bugs.
            {
                var rekey = new List<KeyValuePair<string, string>>();
                var stale = new List<string>();
                foreach (var kv in Settings.GameDefaults)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    if (Settings.Presets.ContainsKey(kv.Value)) continue;
                    string ciMatch = null;
                    foreach (var k in Settings.Presets.Keys)
                    {
                        if (string.Equals(k, kv.Value, StringComparison.OrdinalIgnoreCase))
                        { ciMatch = k; break; }
                    }
                    if (ciMatch != null) rekey.Add(new KeyValuePair<string, string>(kv.Key, ciMatch));
                    else                 stale.Add(kv.Key);
                }
                foreach (var p in rekey)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Game default for '{p.Key}' normalized: '{Settings.GameDefaults[p.Key]}' -> '{p.Value}'.");
                    Settings.GameDefaults[p.Key] = p.Value;
                }
                foreach (var k in stale)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Game default for '{k}' dropped: target '{Settings.GameDefaults[k]}' no longer exists.");
                    Settings.GameDefaults.Remove(k);
                }
            }

            // Seed built-in game defaults only for games the user hasn't chosen.
            foreach (var kv in BuiltinPresets.GameDefaultBindings)
            {
                if (!Settings.GameDefaults.ContainsKey(kv.Key)
                    && Settings.Presets.ContainsKey(kv.Value))
                {
                    Settings.GameDefaults[kv.Key] = kv.Value;
                }
            }

            // Self-heal: strip any stray cache entries whose name collides with
            // a reserved metadata file (e.g. a folder-scan that briefly treated
            // car-defaults.json as a "car-defaults" preset).
            foreach (var reserved in BuiltinPresetStore.ReservedPresetNames)
            {
                if (Settings.Presets.Remove(reserved))
                    SimHub.Logging.Current.Info($"[Trueforce] Removed stray reserved preset '{reserved}' from the cache.");
                var orphans = new List<string>();
                foreach (var kv in Settings.GameDefaults)
                    if (kv.Value == reserved) orphans.Add(kv.Key);
                foreach (var k in orphans) Settings.GameDefaults.Remove(k);
            }
        }

        /// <summary>One-time migration: move the legacy in-dict user presets +
        /// game defaults out of Settings.Presets / Settings.GameDefaults into
        /// files in the user-library folder, then clear the dicts. Built-in
        /// entries are skipped (they re-seed from the built-in folder).
        /// Backs the GeneralSettings.json file up first.</summary>
        private void MigrateLegacyUserPresetsToFolder()
        {
            int presetsCount = Settings.Presets?.Count ?? 0;
            int defaultsCount = Settings.GameDefaults?.Count ?? 0;
            if (presetsCount == 0 && defaultsCount == 0)
            {
                SimHub.Logging.Current.Info("[Trueforce] User-preset migration: nothing in the legacy dicts to move.");
                return;
            }

            BackupSettingsFile("user-preset-migration");

            int migratedPresets = 0, skippedBuiltins = 0, failedPresets = 0;
            if (Settings.Presets != null)
            {
                foreach (var kv in Settings.Presets)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    // Skip names that ARE or WERE shipped as factory built-ins.
                    // Currently-shipped: the built-in folder is the source of
                    // truth; the cache rebuild re-seeds them. Retired (in the
                    // RetiredBuiltinNames list): drop on the floor; the user
                    // shouldn't end up with a "(default)"-suffixed copy in
                    // their library after this migration.
                    if (IsFactoryBuiltinName(kv.Key)) { skippedBuiltins++; continue; }
                    try
                    {
                        string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                            kv.Value, Newtonsoft.Json.Formatting.Indented);
                        BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, kv.Key, json);
                        migratedPresets++;
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Failed to migrate user preset '{kv.Key}': {ex.Message}");
                        failedPresets++;
                    }
                }
            }

            int migratedDefaults = 0, skippedFactoryDefaults = 0;
            if (Settings.GameDefaults != null)
            {
                foreach (var kv in Settings.GameDefaults)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    // If the user's default for this game points at a factory
                    // built-in (current OR retired) just drop the binding.
                    //   - Currently-shipped target: the built-in folder's
                    //     game-defaults.json already provides the same mapping.
                    //   - Retired target: the preset doesn't exist anymore, so
                    //     keeping the binding would dangle.
                    // Only user-named-preset defaults need to be carried over.
                    if (IsFactoryBuiltinName(kv.Value))
                    {
                        skippedFactoryDefaults++;
                        continue;
                    }
                    try
                    {
                        BuiltinPresetWriter.SetGameDefault(UserPresets.CurrentFolder, kv.Key, kv.Value);
                        migratedDefaults++;
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Failed to migrate game default for '{kv.Key}': {ex.Message}");
                    }
                }
            }

            UserPresets.Reload();

            // Clear the legacy dicts; they're a runtime cache now and will be
            // repopulated by RebuildPresetCacheFromFolders.
            Settings.Presets.Clear();
            Settings.GameDefaults.Clear();

            UserPresets.Reload();

            SimHub.Logging.Current.Info(
                $"[Trueforce] User-game-preset migration: moved {migratedPresets} preset(s) and {migratedDefaults} game-default(s) to '{UserPresets.CurrentFolder}' (skipped {skippedBuiltins} factory built-in name(s) + {skippedFactoryDefaults} factory-bound default(s); {failedPresets} failed).");
        }

        /// <summary>Rename any leftover bare-"Trueforce" car backup folders to
        /// the on-brand "TrueforceForAll-LegacyCars.bak-*" form. Runs every
        /// Init; idempotent (no-op once the directory listing is clean).
        /// "Trueforce" is Logitech's mark, so a folder reading "TrueforceCars"
        /// on disk after our migration was a branding leak. Pure rename, no
        /// content change. Failures are logged and non-fatal.</summary>
        private void RebrandLegacyCarsBackup()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string parent = Path.Combine(baseDir, "PluginsData", "Common");
                if (!Directory.Exists(parent)) return;
                foreach (var path in Directory.GetDirectories(parent, "TrueforceCars.bak*"))
                {
                    try
                    {
                        string name = Path.GetFileName(path);
                        // Strip the "TrueforceCars" prefix and preserve whatever
                        // came after it (`.bak-user-car-migration-<ts>` etc.)
                        // so the timestamp / tag survives.
                        string tail = name.Substring("TrueforceCars".Length);
                        string newName = "TrueforceForAll-LegacyCars" + tail;
                        string dst = Path.Combine(parent, newName);
                        if (Directory.Exists(dst)) continue; // collision: leave the old one alone
                        Directory.Move(path, dst);
                        SimHub.Logging.Current.Info($"[Trueforce] Renamed legacy backup '{name}' -> '{newName}' (on-brand).");
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Couldn't rename legacy backup '{Path.GetFileName(path)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] RebrandLegacyCarsBackup failed: {ex.Message}");
            }
        }

        /// <summary>One-time folder restructure: collapse the three legacy
        /// sibling folders into the single TrueforceForAll root with factory /
        /// user subfolders. Conservative: if the destination already exists,
        /// the legacy folder is renamed with a timestamped .legacy- suffix so
        /// nothing is silently merged and the user can see the leftover. Gated
        /// by Settings.FoldersRestructuredV3 in the caller.</summary>
        private void RestructureFoldersIfNeeded()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string commonDir = Path.Combine(baseDir, "PluginsData", "Common");
                // Three legacy sources -> new homes (already wired into the
                // default-folder getters; see BuiltinPresets / UserPresets /
                // UserImportsFolderPath).
                MoveLegacyFolder(Path.Combine(baseDir,   "TrueforceForAll-Presets"), BuiltinPresets.DefaultFolder, "factory");
                MoveLegacyFolder(Path.Combine(commonDir, "TrueforceForAll-Library"), UserPresets.DefaultFolder,    "user");
                MoveLegacyFolder(Path.Combine(commonDir, "TrueforceForAll-Imports"), UserImportsFolderPath,        "user-drop");
                // Move preserved every file, including the stale README.txt
                // from the legacy layout (which talked about the old folder
                // names). Drop those at the new locations so Init's
                // WriteReadmeIfMissing fills them in with the current text.
                // READMEs are informational only (no user data), safe to recreate.
                TryDeleteFile(Path.Combine(BuiltinPresets.DefaultFolder, "README.txt"));
                TryDeleteFile(Path.Combine(UserPresets.DefaultFolder,    "README.txt"));
                TryDeleteFile(Path.Combine(UserImportsFolderPath,        "README.txt"));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Folder restructure failed: {ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        // Move a single legacy folder to its new home. If the destination
        // already exists (a fresh installer run already wrote it), the source
        // is renamed to <source>.legacy-<ts> so the user can see what was left
        // behind and decide whether to drop it.
        private static void MoveLegacyFolder(string from, string to, string role)
        {
            try
            {
                if (!Directory.Exists(from)) return;
                if (Directory.Exists(to))
                {
                    string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string parked = from + $".legacy-{ts}";
                    Directory.Move(from, parked);
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] Folder restructure ({role}): destination already present at '{to}', parked the legacy source as '{Path.GetFileName(parked)}'.");
                    return;
                }
                string parent = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                Directory.Move(from, to);
                SimHub.Logging.Current.Info($"[Trueforce] Folder restructure ({role}): moved '{from}' -> '{to}'.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Folder restructure ({role}) failed for '{from}': {ex.Message}");
            }
        }

        /// <summary>One-time car migration: move legacy TrueforceCars/*.tfcar.json
        /// user files into the user library cars/<game>/<carId>/<preset>.json
        /// layout, and Settings.CarDefaults into car-defaults.json. Backs up
        /// the legacy folder. Skipped after the first run.</summary>
        private void MigrateLegacyUserCarsToFolder()
        {
            int migratedCarFiles = 0, skippedBuiltinCars = 0, failedCars = 0;
            int migratedCarDefaults = 0;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string legacyCarsFolder = Path.Combine(baseDir, "PluginsData", "Common", "TrueforceCars");
                if (Directory.Exists(legacyCarsFolder))
                {
                    foreach (var path in Directory.GetFiles(legacyCarsFolder, "*.tfcar.json"))
                    {
                        try
                        {
                            var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(File.ReadAllText(path));
                            if (f == null || string.IsNullOrEmpty(f.CarId) || f.Override == null) { failedCars++; continue; }
                            // Built-in car files re-seed from the built-in folder; skip them here.
                            if (f.IsBuiltin) { skippedBuiltinCars++; continue; }
                            // Skip files that duplicate a factory built-in car
                            // (content match): the factory version provides the
                            // car with its game, so a user copy is redundant.
                            if (IsFactoryCarDuplicate(f.CarId, f.Override)) { skippedBuiltinCars++; continue; }
                            string presetName = string.IsNullOrEmpty(f.PresetName) ? f.CarId : f.PresetName;
                            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                                new CarPresetFile
                                {
                                    Type       = CarPresetFile.FileType,
                                    Version    = 2,
                                    GameName   = f.GameName ?? "",
                                    CarId      = f.CarId,
                                    PresetName = presetName,
                                    IsBuiltin  = false,
                                    Override   = f.Override,
                                },
                                Newtonsoft.Json.Formatting.Indented);
                            BuiltinPresetWriter.WriteCar(UserPresets.CurrentFolder, f.GameName ?? "", f.CarId, presetName, json);
                            migratedCarFiles++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Failed to migrate car file '{Path.GetFileName(path)}': {ex.Message}");
                            failedCars++;
                        }
                    }
                    // Rename the legacy folder so it isn't re-migrated and the
                    // user can see the original copy if anything looks off.
                    // Use the on-brand name so the leftover folder on disk reads
                    // as ours and not as "Trueforce" (which is Logitech's mark).
                    try
                    {
                        string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        string bakRoot = Path.Combine(baseDir, "PluginsData", "Common",
                            $"TrueforceForAll-LegacyCars.bak-{ts}");
                        Directory.Move(legacyCarsFolder, bakRoot);
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Couldn't rename legacy TrueforceCars folder: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Car-file migration failed: {ex.Message}");
            }

            // Car defaults: carry over only user-pointing entries. A binding
            // pointing at a factory-shipped (carId, presetName) is dropped
            // because the built-in folder's car-defaults.json provides that
            // mapping; a binding pointing at a name no longer in the built-in
            // folder would dangle.
            int skippedFactoryCarDefaults = 0;
            if (Settings.CarDefaults != null)
            {
                foreach (var kv in Settings.CarDefaults)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    // Built-in car preset key in BuiltinPresets.CarPresetJsons is
                    // "<carId>/<presetName>". If the user's default matches a
                    // shipped built-in car, the factory's car-defaults.json
                    // (or LoadAndMigrateCarPresets seeding) will provide it.
                    if (BuiltinPresets.CarPresetJsons.ContainsKey($"{kv.Key}/{kv.Value}"))
                    {
                        skippedFactoryCarDefaults++;
                        continue;
                    }
                    try
                    {
                        BuiltinPresetWriter.SetCarDefault(UserPresets.CurrentFolder, kv.Key, kv.Value);
                        migratedCarDefaults++;
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Failed to migrate car-default for '{kv.Key}': {ex.Message}");
                    }
                }
                Settings.CarDefaults.Clear();
            }

            UserPresets.Reload();

            SimHub.Logging.Current.Info(
                $"[Trueforce] User-car migration: moved {migratedCarFiles} car file(s) and {migratedCarDefaults} car-default(s) to '{UserPresets.CurrentFolder}' (skipped {skippedBuiltinCars} built-in car file(s) + {skippedFactoryCarDefaults} factory-bound default(s); {failedCars} failed).");
        }

        // Rewrite legacy "Forza_<n>" identifiers to today's "Car_<n>" form.
        // Used by the cleanup pass when matching user car files against
        // BuiltinPresets.CarPresetJsons keys. Mirrors the runtime alias
        // applied in CarOverridesForCar at ~line 2325 so a pre-NORMALIZEFORZA
        // file's (CarId, PresetName) tuple finds its current factory entry.
        private static string NormalizeForzaPrefix(string s)
            => s != null && s.StartsWith("Forza_", StringComparison.Ordinal)
                ? "Car_" + s.Substring("Forza_".Length)
                : s;

        /// <summary>Walk user/games and user/cars looking for files whose
        /// stems match a current OR retired built-in name, archive them to
        /// a timestamped backup folder, and drop the matching entries from
        /// user/game-defaults.json + user/car-defaults.json so the factory
        /// seed takes over on the next cache rebuild. Idempotent because
        /// the caller stamps LegacyBuiltinsCleanedV1=true; restoring files
        /// from the backup and clearing the flag re-runs it.</summary>
        private void CleanupLegacyBuiltinsInUserLibrary()
        {
            string folder = UserPresets.CurrentFolder;
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
                return;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            // ---- Games ----
            int archivedGames = 0;
            var droppedGameDefaults = new List<string>();
            try
            {
                string gamesDir = System.IO.Path.Combine(folder, "games");
                if (System.IO.Directory.Exists(gamesDir))
                {
                    string backupDir = System.IO.Path.Combine(gamesDir, $".cleanup-{stamp}");
                    bool createdBackup = false;
                    var archivedNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var path in System.IO.Directory.GetFiles(gamesDir, "*.json"))
                    {
                        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (!IsFactoryBuiltinName(stem)) continue;
                        if (!createdBackup) { System.IO.Directory.CreateDirectory(backupDir); createdBackup = true; }
                        string dest = System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(path));
                        try
                        {
                            if (System.IO.File.Exists(dest)) System.IO.File.Delete(dest);
                            System.IO.File.Move(path, dest);
                            archivedNames.Add(stem);
                            archivedGames++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup: couldn't archive '{path}': {ex.Message}");
                        }
                    }

                    // Drop user/game-defaults entries that pointed at any
                    // file we just archived; the factory seed re-binds.
                    if (archivedNames.Count > 0)
                    {
                        string dpath = System.IO.Path.Combine(folder, BuiltinPresetStore.GameDefaultsFileName);
                        if (System.IO.File.Exists(dpath))
                        {
                            try
                            {
                                var o = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(dpath));
                                var stale = new List<string>();
                                foreach (var p in o.Properties())
                                    if (archivedNames.Contains((string)p.Value)) stale.Add(p.Name);
                                foreach (var k in stale)
                                {
                                    o.Remove(k);
                                    droppedGameDefaults.Add(k);
                                }
                                if (stale.Count > 0)
                                    System.IO.File.WriteAllText(dpath,
                                        o.ToString(Newtonsoft.Json.Formatting.Indented));
                            }
                            catch (Exception ex)
                            {
                                SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup: couldn't rewrite user game-defaults: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup (games) failed: {ex.Message}");
            }

            // ---- Cars ----
            // Reads each user-tier car file and asks whether it's a leftover
            // duplicate of a factory built-in. Match signals (any one
            // triggers archive):
            //   (a) inside-file (CarId, PresetName), with carId normalized
            //       Forza_<n> -> Car_<n>, matches a key in
            //       BuiltinPresets.CarPresetJsons. The normalization
            //       handles legacy Forza_<n> ids that pre-date the
            //       NORMALIZEFORZA migration; the inside-file CarId
            //       handles names containing ':' (Wreckfest car11:default)
            //       that the path sanitizes to '_'.
            //   (b) file's own IsBuiltin=true tag (rare for user files but
            //       possible if migration ever marked one).
            //   (c) PresetName matches IsFactoryBuiltinName (the game-side
            //       check, in case a car preset was named like a retired
            //       game built-in).
            // Refuses to match on carId alone: the user may have
            // legitimately authored a custom preset under a different
            // PresetName for the same car (e.g. user's
            // ks_toyota_ae86_tuned vs factory's ks_toyota_ae86_tuned1).
            //
            // For files we DON'T archive (genuine user presets) that have
            // an empty GameName, infer the GameName from factory's
            // carId->game map and rewrite the file in place so the manager
            // shows the correct game in the row.
            int archivedCars = 0;
            int rewroteCarGameNames = 0;
            var droppedCarDefaults = new List<string>();
            try
            {
                string carsRoot = System.IO.Path.Combine(folder, "cars");
                if (System.IO.Directory.Exists(carsRoot))
                {
                    // Pre-compute factory carId -> game map for GameName
                    // inference on retained user files. Walks the factory
                    // cars/ directory once.
                    var factoryCarGames = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        string factoryCarsRoot = System.IO.Path.Combine(BuiltinPresets.CurrentFolder ?? "", "cars");
                        if (System.IO.Directory.Exists(factoryCarsRoot))
                        {
                            foreach (var gameDir in System.IO.Directory.GetDirectories(factoryCarsRoot))
                            {
                                string gameName = System.IO.Path.GetFileName(gameDir);
                                foreach (var path in System.IO.Directory.GetFiles(gameDir, "*.json", System.IO.SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(System.IO.File.ReadAllText(path));
                                        if (f != null && !string.IsNullOrEmpty(f.CarId))
                                            factoryCarGames[f.CarId] = gameName;
                                    }
                                    catch { /* malformed factory file, skip */ }
                                }
                            }
                        }
                    }
                    catch { /* leave map empty if scan fails */ }

                    string backupRoot = System.IO.Path.Combine(carsRoot, $".cleanup-{stamp}");
                    bool createdBackup = false;
                    var archivedCarKeys = new HashSet<string>(StringComparer.Ordinal); // carId/presetName
                    var archivedCarPresetNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var gameDir in System.IO.Directory.GetDirectories(carsRoot))
                    {
                        string gameName = System.IO.Path.GetFileName(gameDir);
                        if (gameName.StartsWith(".cleanup-", StringComparison.Ordinal)) continue;
                        foreach (var carDir in System.IO.Directory.GetDirectories(gameDir))
                        {
                            string pathCarId = System.IO.Path.GetFileName(carDir);
                            foreach (var path in System.IO.Directory.GetFiles(carDir, "*.json"))
                            {
                                string pathPresetName = System.IO.Path.GetFileNameWithoutExtension(path);

                                // Load the file once: gets the real (inside-
                                // file) CarId + PresetName + IsBuiltin and
                                // lets us rewrite the GameName below if we
                                // keep the file.
                                CarPresetFile carFile = null;
                                try { carFile = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(System.IO.File.ReadAllText(path)); }
                                catch { /* malformed; leave it alone */ continue; }
                                if (carFile == null) continue;

                                string realCarId     = string.IsNullOrEmpty(carFile.CarId)      ? pathCarId      : carFile.CarId;
                                string realPresetName = string.IsNullOrEmpty(carFile.PresetName) ? pathPresetName : carFile.PresetName;
                                // Normalize Forza_<n> -> Car_<n> on BOTH the
                                // carId and the presetName: pre-NORMALIZEFORZA
                                // legacy stamped both halves with the same
                                // string (Save(kv.Key, kv.Key, ...)), and
                                // factory now uses Car_<n> for both.
                                string normCarId      = NormalizeForzaPrefix(realCarId);
                                string normPresetName = NormalizeForzaPrefix(realPresetName);
                                string factoryKey     = normCarId + "/" + normPresetName;

                                // Archive a user car only when it is genuinely a
                                // factory copy: marked built-in, OR its tuning
                                // CONTENT matches a factory preset for this car
                                // (not merely a name collision, so a customized
                                // car sharing a factory name is kept), OR the
                                // preset name is a factory game-builtin name.
                                bool flagged = carFile.IsBuiltin
                                            || IsFactoryCarDuplicate(realCarId, carFile.Override)
                                            || IsFactoryBuiltinName(realPresetName);

                                if (!flagged)
                                {
                                    // Keep this file as a genuine user
                                    // preset. If GameName is empty and the
                                    // factory has a binding for this carId,
                                    // populate GameName and rewrite the
                                    // file so the manager shows it.
                                    if (string.IsNullOrEmpty(carFile.GameName)
                                        && factoryCarGames.TryGetValue(realCarId, out var inferredGame))
                                    {
                                        try
                                        {
                                            carFile.GameName = inferredGame;
                                            System.IO.File.WriteAllText(path,
                                                Newtonsoft.Json.JsonConvert.SerializeObject(carFile, Newtonsoft.Json.Formatting.Indented));
                                            rewroteCarGameNames++;
                                        }
                                        catch (Exception ex)
                                        {
                                            SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup: couldn't backfill GameName for '{path}': {ex.Message}");
                                        }
                                    }
                                    continue;
                                }

                                string destDir = System.IO.Path.Combine(backupRoot, gameName, pathCarId);
                                if (!createdBackup) { System.IO.Directory.CreateDirectory(backupRoot); createdBackup = true; }
                                System.IO.Directory.CreateDirectory(destDir);
                                string dest = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                                try
                                {
                                    if (System.IO.File.Exists(dest)) System.IO.File.Delete(dest);
                                    System.IO.File.Move(path, dest);
                                    archivedCarKeys.Add(factoryKey);
                                    archivedCarPresetNames.Add(realPresetName);
                                    archivedCars++;
                                }
                                catch (Exception ex)
                                {
                                    SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup: couldn't archive car '{path}': {ex.Message}");
                                }
                            }
                        }
                    }

                    // Drop user/car-defaults entries whose target preset
                    // no longer exists for that carId in the merged map
                    // (user-tier + factory). Broader than archive-tracking:
                    // catches bindings that were already stale before this
                    // run (e.g. "Forza_4268 (default)" pointing at a preset
                    // name that never existed, or one renamed long ago).
                    // Without this, every Init logs the same "Car default
                    // dropped" warnings forever because the in-memory
                    // self-heal in LoadAndMigrateCarPresets never writes
                    // back to disk.
                    string carDefaultsPath = System.IO.Path.Combine(folder, BuiltinPresetStore.CarDefaultsFileName);
                    if (System.IO.File.Exists(carDefaultsPath))
                    {
                        try
                        {
                            // Build the merged-loaded map once for membership checks.
                            var loaded = _carStore != null ? _carStore.LoadAll() : new Dictionary<string, Dictionary<string, CarPresetEntry>>();
                            MergeBuiltinCarPresetsInto(loaded);

                            var o = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(carDefaultsPath));
                            var stale = new List<string>();
                            foreach (var p in o.Properties())
                            {
                                string carIdKey   = p.Name;
                                string presetVal  = (string)p.Value;
                                if (string.IsNullOrEmpty(carIdKey) || string.IsNullOrEmpty(presetVal)) { stale.Add(carIdKey); continue; }
                                if (!loaded.TryGetValue(carIdKey, out var perCar)) { stale.Add(carIdKey); continue; }
                                if (perCar.ContainsKey(presetVal)) continue;  // exact match, keep
                                // Case-insensitive tolerance, matching the runtime self-heal.
                                bool ci = false;
                                foreach (var k in perCar.Keys)
                                    if (string.Equals(k, presetVal, StringComparison.OrdinalIgnoreCase)) { ci = true; break; }
                                if (!ci) stale.Add(carIdKey);
                            }
                            foreach (var k in stale)
                            {
                                o.Remove(k);
                                droppedCarDefaults.Add(k);
                            }
                            if (stale.Count > 0)
                                System.IO.File.WriteAllText(carDefaultsPath,
                                    o.ToString(Newtonsoft.Json.Formatting.Indented));
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup: couldn't rewrite user car-defaults: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Legacy-builtin cleanup (cars) failed: {ex.Message}");
            }

            // Force the in-memory user stores to reflect what's on disk now.
            try { UserPresets.Reload(); } catch { }

            SimHub.Logging.Current.Info(
                $"[Trueforce] Legacy-builtin cleanup: archived {archivedGames} game file(s) + {archivedCars} car file(s); dropped {droppedGameDefaults.Count} game-default binding(s) ({string.Join(",", droppedGameDefaults)}) + {droppedCarDefaults.Count} car-default binding(s) ({string.Join(",", droppedCarDefaults)}); backfilled GameName on {rewroteCarGameNames} retained user car file(s).");
        }

        // Game-preset names we have shipped as factory built-ins in the past
        // and have since RETIRED (dropped the file from the shipped folder).
        // Used only by the one-time MigrateLegacyUserPresetsToFolder to skip
        // these entries when a v0.1.20-era user upgrades, so a retired factory
        // name like "F1 25 (default)" doesn't get migrated into the user
        // library as a strangely-named user preset.
        //
        // Currently-shipped built-ins (Assetto Corsa (default), Wreckfest 2,
        // Forza Horizon, iRacing) DON'T go here, BuiltinPresets.IsBuiltin
        // catches them. Add a name to this list whenever you retire a
        // built-in. After the migration latch is set this list is dormant for
        // that user; it only matters for someone still upgrading from v0.1.20.
        private static readonly HashSet<string> RetiredBuiltinNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // F1 25 was a brief experimental built-in, retired when we
            // confirmed F1 22-25 are native-Trueforce out of scope.
            "F1 25 (default)",
            // Names from the pre-file-based-factory era (before commit
            // c89c3f7). Each was a hard-coded BuiltinPresetJsons entry in
            // BuiltinPresets.cs at the time. The current shipped factory
            // files have the " (Built-In)" suffix and live on disk under
            // factory/games/, so any user-tier file still named with the
            // old "(default)" suffix is a leftover from a pre-V2 install,
            // not a user-authored preset. IsFactoryBuiltinName uses this
            // list to recognise them so the cleanup migration archives
            // them and lets the file-based factory take over.
            "Assetto Corsa (default)",
            "Forza Horizon (default)",
            "iRacing (default)",
            "Wreckfest 2 (default)",
        };

        // True if the name is either currently shipped as a built-in or was
        // shipped and later retired. Used to recognise legacy factory entries
        // in Settings.Presets during the one-time migration.
        private bool IsFactoryBuiltinName(string name)
            => !string.IsNullOrEmpty(name)
            && (BuiltinPresets.IsBuiltin(name) || RetiredBuiltinNames.Contains(name));

        // True when the user's car override is content-identical to a factory
        // built-in car preset for the same carId (carId-normalized). Such a
        // file duplicates a shipped preset and shouldn't be imported as a user
        // copy: the factory built-in already provides the car, with its game.
        // A genuinely-different tuning returns false and is kept + imported.
        private bool IsFactoryCarDuplicate(string carId, CarOverride ovr)
        {
            if (string.IsNullOrEmpty(carId) || ovr == null) return false;
            string normCarId = NormalizeForzaPrefix(carId);
            string ovrJson = null;
            foreach (var kvp in BuiltinPresets.CarPresetJsons)
            {
                int slash = kvp.Key.IndexOf('/');
                if (slash <= 0) continue;
                if (!string.Equals(NormalizeForzaPrefix(kvp.Key.Substring(0, slash)), normCarId, StringComparison.Ordinal))
                    continue;
                try
                {
                    var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(kvp.Value);
                    if (f?.Override == null) continue;
                    ovrJson = ovrJson ?? Newtonsoft.Json.JsonConvert.SerializeObject(ovr);
                    if (Newtonsoft.Json.JsonConvert.SerializeObject(f.Override) == ovrJson) return true;
                }
                catch { /* malformed factory entry, skip */ }
            }
            return false;
        }

        // carId (normalized) -> game, built from the loaded factory car
        // presets. Recovers the game for cache-migrated cars whose source
        // (Settings.CarOverrides) carries no game. Empty if factory isn't
        // loaded yet (callers then fall back to the active game).
        private Dictionary<string, string> BuildFactoryCarGameMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in BuiltinPresets.CarPresetJsons)
            {
                try
                {
                    var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(kvp.Value);
                    if (f != null && !string.IsNullOrEmpty(f.CarId) && !string.IsNullOrEmpty(f.GameName))
                        map[NormalizeForzaPrefix(f.CarId)] = f.GameName;
                }
                catch { /* malformed factory entry, skip */ }
            }
            return map;
        }

        /// <summary>Make a timestamped sibling backup of the plugin's
        /// GeneralSettings.json. Used before destructive migrations.</summary>
        private void BackupSettingsFile(string tag)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string src = Path.Combine(baseDir, "PluginsData", "Common", "TrueforcePlugin.GeneralSettings.json");
                if (!File.Exists(src)) return;
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dst = src + $".bak-{tag}-{ts}";
                File.Copy(src, dst, overwrite: false);
                SimHub.Logging.Current.Info($"[Trueforce] Backed up settings to '{Path.GetFileName(dst)}'.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Settings backup ({tag}) failed: {ex.Message}");
            }
        }

        // ===================================================================
        // Dev tooling: built-in folder maintenance (behind the DEV access
        // code / Settings.DevModeUnlocked). These write to / re-sync the
        // built-in source folder so defaults can be maintained as data files
        // without a recompile. See BuiltinPresets / BuiltinPresetWriter.
        // ===================================================================

        /// <summary>Top-level effect sections a complete game built-in should
        /// carry. Drives the Validate "missing section" check.</summary>
        private static readonly string[] BuiltinGameSections =
        {
            "AudioCapture", "EnginePulse", "RoadBumps", "TractionLoss", "GearShift",
            "AbsClick", "PitLimiter", "Drs", "Collision", "RevLimiter", "Airborne",
        };

        public string BuiltinFolderPath => BuiltinPresets.CurrentFolder;

        // ----- User imports folder (drop-in community packs) -----

        public const string UserImportsFolderName = "import";
        public const string UserImportsArchiveSubfolder = "imported";

        /// <summary>Import inbox for community / shared preset files. Lives
        /// inside the user folder (<c>...\TrueforceForAll\user\import</c>) so
        /// it's writable without admin. Auto-imported on plugin start into the
        /// user library and the originals are moved into the
        /// <c>imported/&lt;timestamp&gt;/</c> archive subfolder. Honours
        /// <c>Settings.UserImportsFolder</c> if set, so a user can point it
        /// anywhere they like.</summary>
        public string UserImportsFolderPath
        {
            get
            {
                var s = Settings?.UserImportsFolder;
                if (!string.IsNullOrWhiteSpace(s)) return s;
                return Path.Combine(UserPresets.DefaultFolder, UserImportsFolderName);
            }
        }

        // README written into each folder on first run so users see the
        // intent without opening the docs. Only written if absent (preserves
        // any custom edits).
        private static void WriteReadmeIfMissing(string folder, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "README.txt");
                if (!File.Exists(path)) File.WriteAllText(path, text);
            }
            catch { /* best-effort */ }
        }

        private const string BuiltinReadmeText =
            "Trueforce For All - factory folder\r\n" +
            "\r\n" +
            "This folder ships with the plugin. Files here are FACTORY presets:\r\n" +
            "they show with a 'Built-in' badge in the UI, cannot be deleted or\r\n" +
            "overwritten through the normal Save/Rename/Delete flow, and define\r\n" +
            "the default tuning that loads when you run a supported game.\r\n" +
            "\r\n" +
            "Layout (same as the sibling 'user' folder):\r\n" +
            "  game-defaults.json            (game -> default preset name)\r\n" +
            "  games/<Preset Name>.json      (one GameSettingsSnapshot per preset)\r\n" +
            "  car-defaults.json             (carId -> default car preset name)\r\n" +
            "  cars/<GameName>/<carId>/<PresetName>.json\r\n" +
            "\r\n" +
            "Do NOT drop your own presets here. The easy way to add a shared\r\n" +
            "preset someone sent you is to drop the file into 'user\\drop\\'; the\r\n" +
            "plugin reads each file's content and routes it to the right place\r\n" +
            "under the 'user' folder on the next SimHub start.\r\n";

        private const string UserLibraryReadmeText =
            "Trueforce For All - user folder\r\n" +
            "\r\n" +
            "This folder holds your own presets and defaults. Same subfolder layout\r\n" +
            "as the sibling 'factory' folder, the plugin manages the contents:\r\n" +
            "  game-defaults.json            (your per-game default preset map)\r\n" +
            "  games/<Preset Name>.json      (game presets you saved)\r\n" +
            "  car-defaults.json             (your per-car default preset map)\r\n" +
            "  cars/<GameName>/<carId>/<PresetName>.json   (car presets you saved)\r\n" +
            "  drop/                         (inbox for shared files; see below)\r\n" +
            "\r\n" +
            "Edits made here take effect on the next SimHub start.\r\n" +
            "\r\n" +
            "Adding a shared / community preset:\r\n" +
            "Drop the file into the 'drop\\' subfolder. The plugin handles all the\r\n" +
            "routing (it reads each file's content to figure out where it belongs)\r\n" +
            "and archives the original. Works for single preset files, multi-preset\r\n" +
            "packs, and .tfpack zips. See drop\\README.txt for the full list.\r\n" +
            "\r\n" +
            "Power users can place loose files manually instead, a game preset .json\r\n" +
            "in 'games\\' is picked up on next start. Car presets are harder to place\r\n" +
            "by hand because the path needs the right GameName and carId, so 'drop\\'\r\n" +
            "is the friendlier option there.\r\n";

        private const string ImportsReadmeText =
            "Trueforce For All - import folder\r\n" +
            "\r\n" +
            "Drop any shared / community Trueforce For All file here. On the next\r\n" +
            "SimHub start the plugin reads each file's content, routes it to the\r\n" +
            "matching place under the parent 'user' folder, and moves the original\r\n" +
            "into 'imported/<date>/' so it doesn't import twice.\r\n" +
            "\r\n" +
            "Supported file types (auto-detected by the top-level Type field):\r\n" +
            "  - Game preset    (Type 'trueforce-preset')      -> ..\\games\\<name>.json\r\n" +
            "  - Car preset     (Type 'trueforce-car-preset')  -> ..\\cars\\<Game>\\<carId>\\<name>.json\r\n" +
            "  - Pack           (Type 'trueforce-pack', or a .tfpack zip)\r\n" +
            "                                                  -> unpacked, each entry routed as above\r\n" +
            "\r\n" +
            "You can also subfolder anything you drop here; the scan recurses, so an\r\n" +
            "unzipped pack folder works as-is.\r\n" +
            "\r\n" +
            "If you'd rather route a preset by hand, a game preset .json placed\r\n" +
            "directly in '..\\games\\' is picked up by the regular folder scan on the\r\n" +
            "next start (no archiving, just lives there). Car presets are usually\r\n" +
            "easier to drop here than to place manually under the right GameName /\r\n" +
            "carId subfolders.\r\n" +
            "\r\n" +
            "You can also use the Import button in the Presets tab.\r\n";

        /// <summary>Scan the user-imports folder for *.json files and import
        /// each by content type. Imported files move into
        /// <c>imported/&lt;timestamp&gt;/</c> so they don't re-import. Quiet:
        /// logs only, no UI. Safe to call once per plugin start.</summary>
        public string ImportFromUserImportsFolder()
        {
            string folder = UserImportsFolderPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            int gameOk = 0, carOk = 0, packGameOk = 0, packCarOk = 0, failed = 0;
            var imported = new List<string>();
            string archiveRoot = Path.Combine(folder, UserImportsArchiveSubfolder);
            // Recurse so a dropped-in folder (e.g. an unzipped pack) also imports.
            // Skip anything already inside the archive subfolder.
            foreach (var path in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                if (path.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string type = ReadFileTypeField(path);
                    if (string.Equals(type, PresetFile.FileType, StringComparison.Ordinal))
                    {
                        ImportPreset(path);
                        gameOk++; imported.Add(path);
                    }
                    else if (string.Equals(type, CarPresetFile.FileType, StringComparison.Ordinal))
                    {
                        ImportCarPreset(path);
                        carOk++; imported.Add(path);
                    }
                    else if (string.Equals(type, PresetPackManifest.FileType, StringComparison.Ordinal))
                    {
                        var r = ImportPack(path);
                        packGameOk += r.PresetsImported;
                        packCarOk  += r.CarsImported;
                        imported.Add(path);
                    }
                    else
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Skipping unknown file in imports folder: {Path.GetFileName(path)} (Type='{type}').");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Failed to import '{Path.GetFileName(path)}': {ex.Message}");
                    failed++;
                }
            }

            // Pack archives are binary (not *.json), so scan them separately and
            // route each to ImportPack. A dropped .tfpack/.zip now installs here
            // the same way the manual Import button handles it.
            var packFiles = new List<string>();
            packFiles.AddRange(Directory.GetFiles(folder, "*.tfpack", SearchOption.AllDirectories));
            packFiles.AddRange(Directory.GetFiles(folder, "*.zip", SearchOption.AllDirectories));
            foreach (var path in packFiles)
            {
                if (path.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var r = ImportPack(path);
                    packGameOk += r.PresetsImported;
                    packCarOk  += r.CarsImported;
                    imported.Add(path);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Failed to import pack '{Path.GetFileName(path)}': {ex.Message}");
                    failed++;
                }
            }

            if (imported.Count > 0)
            {
                try
                {
                    string archive = Path.Combine(folder, UserImportsArchiveSubfolder,
                        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(archive);
                    foreach (var p in imported)
                    {
                        try { File.Move(p, Path.Combine(archive, Path.GetFileName(p))); }
                        catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] Couldn't archive imported file '{p}': {ex.Message}"); }
                    }
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] Couldn't create imports archive: {ex.Message}"); }
            }

            int total = gameOk + carOk + packGameOk + packCarOk;
            if (total == 0 && failed == 0) return null;
            string msg = $"Imports folder scan: {gameOk} game, {carOk} car";
            if (packGameOk + packCarOk > 0) msg += $", pack ({packGameOk} game + {packCarOk} car)";
            if (failed > 0) msg += $", {failed} skipped/failed";
            msg += ".";
            SimHub.Logging.Current.Info($"[Trueforce] {msg}");
            return msg;
        }

        // Read just the top-level Type field from a JSON file, fast and tolerant.
        private static string ReadFileTypeField(string path)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                return (string)o["Type"] ?? "";
            }
            catch { return ""; }
        }

        /// <summary>DEV authoring mode (the app owner). When on, saving a
        /// preset writes it through to the built-in folder: editing a built-in
        /// overwrites it in place, a new preset becomes a built-in. Non-dev
        /// users keep the protective fork-on-built-in behaviour.</summary>
        public bool DevMode => Settings?.DevModeUnlocked == true;

        // Persist a game preset snapshot to its file. Picks the right folder:
        //   - EXISTING built-in AND DevMode  -> built-in folder (DEV authoring).
        //   - Everything else                -> user library folder.
        //   - Built-in name in non-DEV       -> caller refuses; we just no-op.
        // After writing, reloads the touched store and rebuilds the runtime
        // cache so Settings.Presets reflects the new file content.
        private void PersistGamePresetToFolder(string presetName, GameSettingsSnapshot snap)
        {
            if (string.IsNullOrEmpty(presetName) || snap == null) return;
            bool isBuiltin = IsBuiltinPreset(presetName);
            if (isBuiltin && !DevMode) return; // protective; caller already refused
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    snap, Newtonsoft.Json.Formatting.Indented);
                if (isBuiltin)
                {
                    BuiltinPresetWriter.WriteGame(BuiltinPresets.CurrentFolder, presetName, json);
                    BuiltinPresets.Reload();
                    SimHub.Logging.Current.Info($"[Trueforce] DEV: wrote '{presetName}' to the built-in folder.");
                }
                else
                {
                    BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, presetName, json);
                    UserPresets.Reload();
                    SimHub.Logging.Current.Info($"[Trueforce] Wrote user preset '{presetName}' to the library folder.");
                }
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Persist '{presetName}' failed: {ex.Message}");
            }
        }

        /// <summary>Export one library game preset into the built-in folder as
        /// a data file (+ manifest entry), then reload the store.</summary>
        public bool PromoteGamePresetToBuiltin(string presetName, out string error)
        {
            error = null;
            try
            {
                if (Settings?.Presets == null
                    || !Settings.Presets.TryGetValue(presetName, out var snap) || snap == null)
                { error = "preset not found in library"; return false; }
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    snap, Newtonsoft.Json.Formatting.Indented);
                string rel = BuiltinPresetWriter.WriteGame(BuiltinPresets.CurrentFolder, presetName, json);
                BuiltinPresets.Reload();
                SimHub.Logging.Current.Info($"[Trueforce] Exported game preset '{presetName}' -> built-in '{rel}'.");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Export one library car preset into the built-in folder as a
        /// CarPresetFile (IsBuiltin=true, PresetName "&lt;carId&gt; (default)").</summary>
        public bool PromoteCarPresetToBuiltin(string carId, string presetName, out string error)
        {
            error = null;
            try
            {
                var all = GetAllCarPresets();
                if (all == null || !all.TryGetValue(carId, out var perCar)
                    || !perCar.TryGetValue(presetName, out var entry) || entry == null)
                { error = "car preset not found"; return false; }

                // Promote always writes to the factory folder; presetName from
                // UI (typically a user row, but harmless on a factory row) may
                // carry the display suffix -- strip to the on-disk form.
                string diskName = ToDiskName(presetName);
                var file = new CarPresetFile
                {
                    Type       = CarPresetFile.FileType,
                    Version    = 2,
                    GameName   = entry.GameName ?? "",
                    CarId      = carId,
                    PresetName = diskName,
                    IsBuiltin  = true,
                    Override   = entry.Override,
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    file, Newtonsoft.Json.Formatting.Indented);
                string rel = BuiltinPresetWriter.WriteCar(BuiltinPresets.CurrentFolder, file.GameName, carId, diskName, json);
                // First built-in for this car becomes its default; otherwise
                // leave the existing default (owner picks via Set as default).
                if (!BuiltinPresets.CarDefaultBindings.ContainsKey(carId))
                    BuiltinPresetWriter.SetCarDefault(BuiltinPresets.CurrentFolder, carId, diskName);
                BuiltinPresets.Reload();
                SimHub.Logging.Current.Info($"[Trueforce] Exported car preset '{carId}/{diskName}' -> built-in '{rel}'.");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>Reload the folder and (re)install its built-ins into the
        /// library: game presets overwrite by name, car factory files refresh.
        /// Additive, never removes. No standalone UI button (DEV authoring
        /// already reloads after each write; restart picks up external edits);
        /// reserved for Phase 3 folder-switch / GitHub-repair to call.</summary>
        public string ImportBuiltinsFromFolder()
        {
            BuiltinPresets.Reload();
            InstallBuiltinPresetsIfMissing();
            LoadAndMigrateCarPresets();
            string msg = $"Imported from folder: {BuiltinPresets.BuiltinPresetJsons.Count} game + {BuiltinPresets.CarPresetJsons.Count} car built-in(s).";
            SimHub.Logging.Current.Info($"[Trueforce] {msg}");
            return msg;
        }

        /// <summary>Make the library's built-ins match the folder exactly:
        /// drop game built-ins the folder no longer has (and any GameDefaults
        /// pointing at them), then re-import. Car factory files refresh via
        /// LoadAndMigrateCarPresets. No standalone UI button (the in-app delete
        /// already prunes); reserved for the Phase 3 change-folder / GitHub
        /// repair flows, which should run a full replace after switching.</summary>
        public string ReseedBuiltinsFromFolder()
        {
            var oldGameBuiltins = new List<string>(BuiltinPresets.BuiltinPresetJsons.Keys);
            BuiltinPresets.Reload();
            var newSet = new HashSet<string>(BuiltinPresets.BuiltinPresetJsons.Keys, StringComparer.Ordinal);
            int removed = 0;
            if (Settings?.Presets != null)
                foreach (var name in oldGameBuiltins)
                    if (!newSet.Contains(name) && Settings.Presets.Remove(name))
                    {
                        removed++;
                        if (Settings.GameDefaults != null)
                        {
                            var orphans = new List<string>();
                            foreach (var kv in Settings.GameDefaults)
                                if (kv.Value == name) orphans.Add(kv.Key);
                            foreach (var k in orphans) Settings.GameDefaults.Remove(k);
                        }
                    }
            InstallBuiltinPresetsIfMissing();
            LoadAndMigrateCarPresets();
            this.SaveCommonSettings("GeneralSettings", Settings);
            string msg = $"Reseeded from folder: {newSet.Count} game built-in(s) (removed {removed} stale), {BuiltinPresets.CarPresetJsons.Count} car built-in(s).";
            SimHub.Logging.Current.Info($"[Trueforce] {msg}");
            return msg;
        }

        /// <summary>Validate the built-in folder (parse + missing-section
        /// flag). Returns human-readable lines for the dev panel.</summary>
        public List<string> ValidateBuiltins()
            => BuiltinPresets.Validate(BuiltinGameSections);

        /// <summary>DEV one-shot: normalize legacy "Forza_&lt;n&gt;" car ids
        /// to "Car_&lt;n&gt;" so they match what SimHub's data feed reports
        /// (Trueforce's UDP fallback used to emit Forza_&lt;n&gt;; now emits
        /// Car_&lt;n&gt;). Per-car rules:
        /// <list type="bullet">
        ///   <item>Both "Car_&lt;n&gt;" and "Forza_&lt;n&gt;" exist: keep Car,
        ///   delete the Forza directory.</item>
        ///   <item>Only "Forza_&lt;n&gt;" exists: rename to "Car_&lt;n&gt;",
        ///   rewriting each file's CarId field and the inner PresetName when
        ///   it embedded the old carId (e.g. "Forza_455 (default)" -&gt;
        ///   "Car_455 (default)").</item>
        /// </list>
        /// Applies to factory + user folders, both car-defaults.json files,
        /// and the live Settings.CarDefaults + Settings.CarOverrides
        /// dicts. Reloads stores at the end and persists settings.</summary>
        public int DevNormalizeForzaCarIds(out string summary)
        {
            var lines = new List<string>();
            int renamed = 0, deduped = 0, dictHits = 0;

            NormalizeForzaInFolder(BuiltinPresets.CurrentFolder, "factory", lines, ref renamed, ref deduped);
            NormalizeForzaInFolder(UserPresets.CurrentFolder,    "user",    lines, ref renamed, ref deduped);

            NormalizeForzaInCarDefaultsFile(BuiltinPresets.CurrentFolder, "factory", lines);
            NormalizeForzaInCarDefaultsFile(UserPresets.CurrentFolder,    "user",    lines);

            if (Settings?.CarDefaults  != null) dictHits += NormalizeForzaInStringDict(Settings.CarDefaults);
            if (Settings?.CarOverrides != null) dictHits += NormalizeForzaInOverrideDict(Settings.CarOverrides);
            if (dictHits > 0) lines.Add($"Settings dicts: rewrote {dictHits} entry/entries");

            int total = renamed + deduped + dictHits;
            if (total > 0)
            {
                BuiltinPresets.Reload();
                UserPresets.Reload();
                LoadAndMigrateCarPresets();
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] DEV: normalized {renamed} carId rename(s), {deduped} dedup(s), {dictHits} settings entries.");
            }
            summary = total == 0
                ? "No Forza_xxx car ids found. (Already normalized.)"
                : $"Normalized {renamed} car directory/ies, deduped {deduped}, rewrote {dictHits} settings entries.";
            if (lines.Count > 0) summary += "\n" + string.Join("\n", lines);
            return total;
        }

        private static void NormalizeForzaInFolder(string root, string label, List<string> lines, ref int renamed, ref int deduped)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string carsRoot = Path.Combine(root, "cars");
            if (!Directory.Exists(carsRoot)) return;

            foreach (var gameDir in Directory.GetDirectories(carsRoot))
            {
                string gameName = Path.GetFileName(gameDir);
                foreach (var carDir in Directory.GetDirectories(gameDir))
                {
                    string oldCarId = Path.GetFileName(carDir);
                    if (!oldCarId.StartsWith("Forza_", StringComparison.Ordinal)) continue;
                    string newCarId = "Car_" + oldCarId.Substring("Forza_".Length);
                    string newCarDir = Path.Combine(gameDir, newCarId);
                    if (Directory.Exists(newCarDir))
                    {
                        // Collision: Car_<n> wins; drop the Forza_<n> tree.
                        try { Directory.Delete(carDir, recursive: true); } catch { }
                        deduped++;
                        lines.Add($"{label}/{gameName}/{oldCarId}: deleted (Car_<n> equivalent exists)");
                        continue;
                    }
                    int fileCount = 0;
                    foreach (var path in Directory.GetFiles(carDir, "*.json"))
                    {
                        try
                        {
                            var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(File.ReadAllText(path));
                            if (f == null) continue;
                            f.CarId = newCarId;
                            string newPresetName = f.PresetName;
                            if (!string.IsNullOrEmpty(newPresetName))
                            {
                                if (newPresetName == oldCarId)
                                    newPresetName = newCarId;
                                else if (newPresetName.StartsWith(oldCarId + " ", StringComparison.Ordinal)
                                      || newPresetName.StartsWith(oldCarId + "(", StringComparison.Ordinal))
                                    newPresetName = newCarId + newPresetName.Substring(oldCarId.Length);
                                f.PresetName = newPresetName;
                            }
                            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                                f, Newtonsoft.Json.Formatting.Indented);
                            BuiltinPresetWriter.WriteCar(root, gameName, newCarId, newPresetName ?? newCarId, json);
                            fileCount++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] NormalizeForza: failed to rewrite '{path}': {ex.Message}");
                        }
                    }
                    // Drop the now-stale Forza_<n> directory.
                    try { Directory.Delete(carDir, recursive: true); } catch { }
                    renamed++;
                    lines.Add($"{label}/{gameName}/{oldCarId} -> {newCarId} ({fileCount} file(s))");
                }
            }
        }

        private static int NormalizeForzaInStringDict(Dictionary<string, string> d)
        {
            int n = 0;
            foreach (var key in new List<string>(d.Keys))
            {
                if (!key.StartsWith("Forza_", StringComparison.Ordinal)) continue;
                string newKey = "Car_" + key.Substring("Forza_".Length);
                string val = d[key];
                d.Remove(key);
                if (!d.ContainsKey(newKey)) d[newKey] = val;
                n++;
            }
            return n;
        }

        private static int NormalizeForzaInOverrideDict(Dictionary<string, CarOverride> d)
        {
            int n = 0;
            foreach (var key in new List<string>(d.Keys))
            {
                if (!key.StartsWith("Forza_", StringComparison.Ordinal)) continue;
                string newKey = "Car_" + key.Substring("Forza_".Length);
                var val = d[key];
                d.Remove(key);
                if (!d.ContainsKey(newKey)) d[newKey] = val;
                n++;
            }
            return n;
        }

        private static void NormalizeForzaInCarDefaultsFile(string folder, string label, List<string> lines)
        {
            if (string.IsNullOrEmpty(folder)) return;
            string path = Path.Combine(folder, BuiltinPresetStore.CarDefaultsFileName);
            if (!File.Exists(path)) return;
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                var props = new List<Newtonsoft.Json.Linq.JProperty>(o.Properties());
                bool changed = false;
                foreach (var p in props)
                {
                    if (!p.Name.StartsWith("Forza_", StringComparison.Ordinal)) continue;
                    string newKey = "Car_" + p.Name.Substring("Forza_".Length);
                    if (o[newKey] != null)
                    {
                        p.Remove();
                        changed = true;
                        lines.Add($"{label}/car-defaults.json: dropped {p.Name} (Car_<n> exists)");
                        continue;
                    }
                    var v = p.Value;
                    string oldName = p.Name;
                    p.Remove();
                    o[newKey] = v;
                    changed = true;
                    lines.Add($"{label}/car-defaults.json: {oldName} -> {newKey}");
                }
                if (changed) File.WriteAllText(path, o.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] NormalizeForza car-defaults '{path}': {ex.Message}");
            }
        }

        /// <summary>Re-scan factory + user folders from disk and rebuild every
        /// runtime cache (game preset map, car defaults, car store). Use after
        /// external edits to the folders (drop-in files, manual edits) so the
        /// in-memory state catches up without a SimHub restart. The caller
        /// still needs to call PresetManagerControl.RefreshLists() to redraw
        /// the UI rows.</summary>
        public string ReloadLibraryFromFolders()
        {
            // Install anything dropped into the import folder first, so a Refresh
            // picks up newly-dropped presets/packs (not just at plugin startup).
            string importMsg = null;
            try { importMsg = ImportFromUserImportsFolder(); }
            catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] Import-folder scan on refresh failed: {ex.Message}"); }
            BuiltinPresets.Reload();
            UserPresets.Reload();
            RebuildPresetCacheFromFolders();
            LoadAndMigrateCarPresets();
            string msg = $"Reloaded library from folders: {BuiltinPresets.BuiltinPresetJsons.Count} game + {BuiltinPresets.CarPresetJsons.Count} car built-in(s), {UserPresets.PresetJsons.Count} user game preset(s), {UserPresets.CarPresetJsons.Count} user car preset(s).";
            if (!string.IsNullOrEmpty(importMsg)) msg = importMsg + " " + msg;
            SimHub.Logging.Current.Info($"[Trueforce] {msg}");
            return msg;
        }

        /// <summary>DEV-only one-shot car-default consolidation. For each car
        /// whose active default is a USER preset, promote that user preset to a
        /// factory built-in (replacing any existing factory built-in(s) for the
        /// same car), bind the factory car-default to it, and delete the user
        /// preset + user-side car-default binding. Cars whose default is
        /// already a built-in are skipped. Other user presets for the same car
        /// stay put. Returns a summary line + per-car log for the status box.</summary>
        public int DevConsolidateUserCarDefaults(out string summary)
        {
            summary = "";
            var lines = new List<string>();
            int promoted = 0, skipped = 0;
            if (_carStore == null || Settings?.CarDefaults == null)
            { summary = "No car defaults to consolidate."; return 0; }

            var userLoaded = _carStore.LoadAll();
            // Iterate a copy: we mutate Settings.CarDefaults inside the loop.
            foreach (var kv in new Dictionary<string, string>(Settings.CarDefaults))
            {
                string carId = kv.Key, defaultName = kv.Value;
                if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(defaultName)) continue;

                // Default already points at a built-in for this car? Nothing to do.
                if (BuiltinPresets.CarPresetJsons.ContainsKey(carId + "/" + defaultName))
                    continue;

                if (!userLoaded.TryGetValue(carId, out var perCar)
                    || !perCar.TryGetValue(defaultName, out var entry)
                    || entry == null)
                { lines.Add($"{carId}: skipped (user preset '{defaultName}' not found)"); skipped++; continue; }

                string game = entry.GameName ?? "";

                // Find every existing factory built-in for this car and delete
                // them. The new default takes their place.
                var factoryHits = new List<string>();   // preset names
                foreach (var bkv in BuiltinPresets.CarPresetJsons)
                {
                    int slash = bkv.Key.IndexOf('/');
                    if (slash < 0) continue;
                    if (bkv.Key.Substring(0, slash) != carId) continue;
                    factoryHits.Add(bkv.Key.Substring(slash + 1));
                }
                foreach (var name in factoryHits)
                {
                    string g = game;
                    try
                    {
                        var bf = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(
                            BuiltinPresets.CarPresetJsons[carId + "/" + name]);
                        if (bf != null && !string.IsNullOrEmpty(bf.GameName)) g = bf.GameName;
                    }
                    catch { /* fall back to entry's game */ }
                    BuiltinPresetWriter.DeleteCar(BuiltinPresets.CurrentFolder, g, carId, name);
                }

                // Write the user preset into factory (IsBuiltin=true), bind the
                // factory car-default to it, then drop the user-side file and
                // any user-side binding for this car.
                var newFile = new CarPresetFile
                {
                    Type = CarPresetFile.FileType, Version = 2,
                    GameName = game, CarId = carId,
                    PresetName = defaultName, IsBuiltin = true, Override = entry.Override,
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(newFile, Newtonsoft.Json.Formatting.Indented);
                BuiltinPresetWriter.WriteCar(BuiltinPresets.CurrentFolder, game, carId, defaultName, json);
                BuiltinPresetWriter.SetCarDefault(BuiltinPresets.CurrentFolder, carId, defaultName);

                _carStore.Delete(carId, defaultName);
                BuiltinPresetWriter.RemoveCarDefault(UserPresets.CurrentFolder, carId);

                lines.Add($"{carId}: promoted '{defaultName}' (replaced {factoryHits.Count} existing built-in(s))");
                promoted++;
            }

            if (promoted > 0)
            {
                BuiltinPresets.Reload();
                UserPresets.Reload();
                LoadAndMigrateCarPresets();
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] DEV: consolidated {promoted} car default(s) into factory built-ins.");
            }

            summary = promoted == 0
                ? "Nothing to consolidate (every car default already points at a built-in)."
                : $"Promoted {promoted} car default(s) to built-ins (skipped {skipped}).";
            if (lines.Count > 0) summary += "\n" + string.Join("\n", lines);
            return promoted;
        }

        /// <summary>Give an unmapped game its own preset seeded from the
        /// Assetto Corsa baseline (which ships as the user's AC tuning) and
        /// bind it as that game's default, then apply it live. No-op if the
        /// game already has a preset of that name (binds to it instead). Called
        /// from the game-change path the first time an unmapped title appears.</summary>
        private void EnsureSeededGamePreset(string gameName)
        {
            if (Settings == null || string.IsNullOrEmpty(gameName)) return;
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();

            // Seed from the AC baseline (always installed by
            // InstallBuiltinPresetsIfMissing). Bail safely if it's somehow absent.
            if (!Settings.Presets.TryGetValue("Assetto Corsa (default)", out var seed) || seed == null)
                return;

            string presetName = gameName;
            if (!Settings.Presets.ContainsKey(presetName))
            {
                // Write the seeded copy as a user-library file (it's a fresh,
                // user-mutable preset for this newly-seen game).
                try
                {
                    var clone = CloneSnapshot(seed);
                    // Stamp a sentinel PackName so BackfillAuthorOnLocalPresets
                    // recognises this as a shipped seed, not user-authored
                    // work, and skips stamping the user's name on it.
                    // SaveSectionToActivePreset clears the sentinel on the
                    // user's first edit so the preset becomes "theirs" the
                    // moment they touch a slider.
                    clone.PackName = SeededPresetSentinel;
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                        clone, Newtonsoft.Json.Formatting.Indented);
                    BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, presetName, json);
                    UserPresets.Reload();
                    RebuildPresetCacheFromFolders();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Seeded preset write for '{presetName}' failed: {ex.Message}");
                }
            }

            SetDefaultPresetForGame(gameName, presetName);

            if (Settings.Presets.TryGetValue(presetName, out var applied))
                ApplyGamePreset(applied);
            _activePresetName = presetName;
            SimHub.Logging.Current.Info(
                $"[Trueforce] No default for '{gameName}'; created preset '{presetName}' seeded from the Assetto Corsa baseline.");
        }

        // Deep-clone a snapshot via JSON round-trip so the new game's preset
        // doesn't alias the AC baseline's nested section objects.
        private static GameSettingsSnapshot CloneSnapshot(GameSettingsSnapshot s)
            => Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(
                   Newtonsoft.Json.JsonConvert.SerializeObject(s));

        /// <summary>One-shot migration + initial load for per-car preset files.
        /// Files are the canonical store post-Model-G. Steps, in order:
        ///
        ///   1. Seed file store from legacy data sources (in-Settings
        ///      CarOverrides, preset-nested CarOverrides) for upgrades from
        ///      pre-files versions. File-wins-on-conflict so re-runs don't
        ///      clobber files the user has already edited.
        ///   2. Migrate v1 single-preset-per-car files to the v2
        ///      multi-preset naming (<c>&lt;carId&gt;~&lt;presetName&gt;.tfcar.json</c>)
        ///      with PresetName=CarId. Each migrated car gets a CarDefaults
        ///      entry pointing at its migrated user preset, so the active
        ///      override doesn't change.
        ///   3. Install / refresh built-in factory car presets shipped via
        ///      BuiltinCarPresets. Always rewrites factory files so a future
        ///      release that updates a default just lands.
        ///   4. Load every preset back into memory and resolve the active
        ///      preset for each car into Settings.CarOverrides (the live
        ///      cache the effects read from).</summary>
        private void LoadAndMigrateCarPresets()
        {
            if (Settings == null || _carStore == null) return;
            if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();
            if (Settings.CarDefaults  == null) Settings.CarDefaults  = new Dictionary<string, string>();

            // Load the user-side car files once. Reused for the existence check
            // in steps 1 + 2 AND for the cache rebuild in step 4. The check is
            // "does this carId have ANY preset file yet?", not "is there a file
            // specifically named <carId>/<carId>.json", a v0.1.20 user whose
            // car preset was renamed (file at <carId>/<Custom name>.json) would
            // otherwise get a duplicate written by step 1.
            var loaded = _carStore.LoadAll();
            var carIdsWithUserFiles = new HashSet<string>(loaded.Keys, StringComparer.Ordinal);

            // Factory carId -> game map: recovers the game for cache-migrated
            // cars (Settings.CarOverrides is keyed by carId only, no game).
            var factoryCarGames = BuildFactoryCarGameMap();

            // 1) Migrate Settings.CarOverrides → files (only if no file yet for
            //    that carId). Use carId as the preset name so the migrated
            //    entry has a stable identity in the new multi-preset model.
            //
            //    CarDefaults.ContainsKey gates this: once a car has any
            //    binding (its own user file OR a built-in default), the
            //    migration for that car is done. Step 5 below repopulates
            //    Settings.CarOverrides from disk on every load, so without
            //    this guard a deleted user-tier file would silently resurrect
            //    on next launch from the cached live override.
            int migrated = 0;
            foreach (var kv in new Dictionary<string, CarOverride>(Settings.CarOverrides))
            {
                if (kv.Value == null || kv.Value.IsEmpty) continue;
                if (Settings.CarDefaults.ContainsKey(kv.Key)) continue;
                if (carIdsWithUserFiles.Contains(kv.Key)) continue;
                // Skip cars that duplicate a factory built-in: the factory
                // version provides it with its game. Skipping at import (vs
                // import-then-archive) avoids the blank-game Unknown copies the
                // old path created when the .tfcar.json migration was skipped.
                if (IsFactoryCarDuplicate(kv.Key, kv.Value)) continue;
                string g1 = factoryCarGames.TryGetValue(NormalizeForzaPrefix(kv.Key), out var fg1) ? fg1 : (_activeGame ?? "");
                _carStore.Save(kv.Key, kv.Key, g1, kv.Value, isBuiltin: false);
                carIdsWithUserFiles.Add(kv.Key);
                if (!Settings.CarDefaults.ContainsKey(kv.Key))
                    Settings.CarDefaults[kv.Key] = kv.Key;
                migrated++;
            }
            // 2) Migrate each preset's CarOverrides → files (file-wins).
            //    Game presets going forward don't include CarOverrides, but
            //    legacy data may still be present in saved presets.
            if (Settings.Presets != null)
            {
                foreach (var presetKv in Settings.Presets)
                {
                    var snap = presetKv.Value;
                    if (snap?.CarOverrides == null) continue;
                    foreach (var carKv in snap.CarOverrides)
                    {
                        if (carKv.Value == null || carKv.Value.IsEmpty) continue;
                        if (carIdsWithUserFiles.Contains(carKv.Key)) continue;
                        if (IsFactoryCarDuplicate(carKv.Key, carKv.Value)) continue;
                        string g2 = factoryCarGames.TryGetValue(NormalizeForzaPrefix(carKv.Key), out var fg2) ? fg2 : (_activeGame ?? "");
                        _carStore.Save(carKv.Key, carKv.Key, g2, carKv.Value, isBuiltin: false);
                        carIdsWithUserFiles.Add(carKv.Key);
                        if (!Settings.CarDefaults.ContainsKey(carKv.Key))
                            Settings.CarDefaults[carKv.Key] = carKv.Key;
                        migrated++;
                    }
                }
            }
            // If steps 1 or 2 actually wrote any new files, refresh the loaded
            // map so step 4's resolution sees them.
            if (migrated > 0) loaded = _carStore.LoadAll();

            // 3) Rebuild Settings.CarDefaults from the folders. It's a runtime
            //    cache now; user library wins on collision (their explicit
            //    choice overrides the factory seed). Any stale persisted dict
            //    value is dropped (the user library file is the truth).
            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults.Clear();
            // Factory car-defaults.json values are on-disk names (no suffix);
            // the merged car-preset map keys factory entries with the
            // " (Built-In)" suffix to keep them separate from any same-named
            // user file. Stamp the suffix when copying factory bindings so
            // ResolveActiveCarPresetName can find the factory entry by key.
            // User bindings are written by SwitchActiveCarPreset already in
            // the right form (suffixed when the user picked a factory row),
            // so they pass through unchanged and override factory on collision.
            foreach (var kv in BuiltinPresets.CarDefaultBindings)
                Settings.CarDefaults[kv.Key] = kv.Value + BuiltinNameSuffix;
            foreach (var kv in UserPresets.CarDefaults)
                Settings.CarDefaults[kv.Key] = kv.Value;

            // 4) Merge BUILT-IN cars from the built-in folder on top of the
            //    user-file map we loaded at the top of this method. Built-in
            //    wins on a (carId, presetName) collision (factory IS the
            //    truth for those). Resolve the active preset per car into
            //    Settings.CarOverrides.
            MergeBuiltinCarPresetsInto(loaded);

            foreach (var carKv in loaded)
            {
                string carId    = carKv.Key;
                var perCar      = carKv.Value;
                string activeName = ResolveActiveCarPresetName(carId, perCar);
                if (activeName != null && perCar.TryGetValue(activeName, out var entry))
                {
                    Settings.CarOverrides[carId] = entry.Override;
                    _lastPersistedCarOverrides[carId] = CloneCarOverride(entry.Override);
                    if (!Settings.CarDefaults.ContainsKey(carId))
                        Settings.CarDefaults[carId] = activeName;
                }
            }

            // Self-heal stale or case-drifted user car-default bindings,
            // same idea as the game-default normalization in
            // RebuildPresetCacheFromFolders. If a Settings.CarDefaults
            // value doesn't match any preset name for that carId in the
            // merged map, either correct the case (case-insensitive) or
            // drop the entry. Avoids "this car has a binding but the
            // active preset never resolves" silent failures.
            {
                var rekey = new List<KeyValuePair<string, string>>();
                var stale = new List<string>();
                foreach (var kv in Settings.CarDefaults)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    if (!loaded.TryGetValue(kv.Key, out var perCar)) { stale.Add(kv.Key); continue; }
                    if (perCar.ContainsKey(kv.Value)) continue;
                    string ciMatch = null;
                    foreach (var k in perCar.Keys)
                    {
                        if (string.Equals(k, kv.Value, StringComparison.OrdinalIgnoreCase))
                        { ciMatch = k; break; }
                    }
                    if (ciMatch != null) rekey.Add(new KeyValuePair<string, string>(kv.Key, ciMatch));
                    else                 stale.Add(kv.Key);
                }
                foreach (var p in rekey)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Car default for '{p.Key}' normalized: '{Settings.CarDefaults[p.Key]}' -> '{p.Value}'.");
                    Settings.CarDefaults[p.Key] = p.Value;
                }
                foreach (var k in stale)
                {
                    SimHub.Logging.Current.Info($"[Trueforce] Car default for '{k}' dropped: target '{Settings.CarDefaults[k]}' no longer exists.");
                    Settings.CarDefaults.Remove(k);
                }
            }

            if (migrated > 0)
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Car presets: migrated {migrated} legacy entries.");
        }

        // Add the built-in car presets (loaded from the built-in folder via
        // BuiltinPresets.CarPresetJsons) into the in-memory map alongside the
        // user files. Built-in entries overwrite same-named user entries
        // (factory wins; user can't normally save with a built-in name).
        private void MergeBuiltinCarPresetsInto(Dictionary<string, Dictionary<string, CarPresetEntry>> map)
        {
            foreach (var kv in BuiltinPresets.CarPresetJsons)
            {
                try
                {
                    var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(kv.Value);
                    if (f == null || string.IsNullOrEmpty(f.CarId) || f.Override == null) continue;
                    string presetName = string.IsNullOrEmpty(f.PresetName) ? f.CarId : f.PresetName;
                    var entry = new CarPresetEntry
                    {
                        CarId      = f.CarId,
                        PresetName = presetName,
                        GameName   = f.GameName ?? "",
                        IsBuiltin  = true,
                        Override   = f.Override,
                    };
                    if (!map.TryGetValue(f.CarId, out var perCar))
                    {
                        perCar = new Dictionary<string, CarPresetEntry>(StringComparer.Ordinal);
                        map[f.CarId] = perCar;
                    }
                    // Stamp every factory entry with a " (Built-In)" suffix in
                    // both the entry's PresetName AND the perCar dict key.
                    // This is purely a display-layer rename: the disk file
                    // stays at the original name; the suffix is added here so
                    // (1) the Preset Manager row shows it's a built-in and
                    // (2) user vs factory entries with the same on-disk name
                    // never collide in the merged map. Plugin methods strip
                    // the suffix at disk-op boundaries via ToDiskName.
                    string displayName = presetName + BuiltinNameSuffix;
                    entry.PresetName = displayName;
                    perCar[displayName] = entry;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Skipping malformed built-in car preset '{kv.Key}': {ex.Message}");
                }
            }
        }

        /// <summary>Rewrite every on-disk car-preset file for the active car
        /// whose GameName is empty, stamping the current <c>_activeGame</c>.
        /// Skips built-in (default) files because those refresh from
        /// BuiltinCarPresets on Init via InstallOrUpdateBuiltinCarPresets.
        /// Called from DataUpdate's car-change branch behind a per-session
        /// (game, carId) dedup so it runs at most once per pair per session.</summary>
        private void BackfillGameNameForActiveCar()
        {
            try
            {
                var loaded = _carStore.LoadAll();
                if (!loaded.TryGetValue(_activeCarId, out var perCar)) return;
                int rewritten = 0;
                foreach (var entry in perCar.Values)
                {
                    if (entry == null || entry.IsBuiltin) continue;
                    if (!string.IsNullOrEmpty(entry.GameName)) continue;
                    _carStore.Save(entry.CarId, entry.PresetName, _activeGame, entry.Override, isBuiltin: false);
                    rewritten++;
                }
                if (rewritten > 0)
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] Backfilled GameName='{_activeGame}' on {rewritten} preset(s) for car '{_activeCarId}'.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    $"[Trueforce] BackfillGameNameForActiveCar('{_activeCarId}', '{_activeGame}') failed: {ex.Message}");
            }
        }

        /// <summary>Rename presets whose PresetName equals the carId (the
        /// historical default for newly-saved presets pre-DisplayName) to the
        /// resolver-provided DisplayName. Idempotent — once the rename has
        /// happened, subsequent invocations find PresetName != CarId and skip.
        /// Updates CarDefaults so the pointer to the active preset stays
        /// correct. Skips when the target DisplayName name already exists
        /// (don't clobber a user-saved file with the same name).</summary>
        private void BackfillDisplayNameForActiveCar()
        {
            string carId       = _activeCarId;
            string displayName = _activeCarDisplayName;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(displayName)) return;
            // Equal names → nothing to do (e.g. AC carIds are already descriptive)
            if (string.Equals(carId, displayName, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var loaded = _carStore.LoadAll();
                if (!loaded.TryGetValue(carId, out var perCar)) return;
                int rewritten = 0;
                // Snapshot keys: we mutate the store inside the loop.
                var names = new List<string>(perCar.Keys);
                foreach (var oldName in names)
                {
                    var entry = perCar[oldName];
                    if (entry == null || entry.IsBuiltin) continue;
                    // Only rename when the user clearly never customized the
                    // preset name. Heuristic: PresetName equals CarId.
                    if (!string.Equals(entry.PresetName, carId, StringComparison.OrdinalIgnoreCase)) continue;
                    // Don't clobber an existing different file at the target name
                    if (_carStore.Exists(carId, displayName)) continue;
                    // Save under new name (writes the new file) and delete the old.
                    _carStore.Save(carId, displayName, _activeGame, entry.Override, isBuiltin: false);
                    _carStore.Delete(carId, oldName);
                    // Re-point CarDefaults if it pointed at the renamed preset.
                    if (Settings?.CarDefaults != null
                        && Settings.CarDefaults.TryGetValue(carId, out var ptr)
                        && string.Equals(ptr, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.CarDefaults[carId] = displayName;
                    }
                    // If this was the currently-active preset name, update it
                    // so the UI reflects the rename without needing a reload.
                    if (string.Equals(_activePresetName, oldName, StringComparison.OrdinalIgnoreCase))
                        _activePresetName = displayName;
                    rewritten++;
                }
                if (rewritten > 0)
                {
                    this.SaveCommonSettings("GeneralSettings", Settings);
                    SimHub.Logging.Current.Info(
                        $"[Trueforce] Renamed {rewritten} preset(s) for car '{carId}' to '{displayName}'.");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    $"[Trueforce] BackfillDisplayNameForActiveCar('{_activeCarId}') failed: {ex.Message}");
            }
        }

        /// <summary>Pick the preset to load for a car. CarDefaults[carId]
        /// wins if it points at a real preset on disk; else fall back to the
        /// first built-in "(default)" preset for the car; else null (no
        /// active preset, effects fall through to globals).</summary>
        private string ResolveActiveCarPresetName(string carId,
            IReadOnlyDictionary<string, CarPresetEntry> perCar)
        {
            if (Settings?.CarDefaults != null
                && Settings.CarDefaults.TryGetValue(carId, out var name)
                && !string.IsNullOrEmpty(name)
                && perCar.ContainsKey(name))
                return name;
            foreach (var kv in perCar)
                if (kv.Value.IsBuiltin) return kv.Key;
            return null;
        }

        /// <summary>Deep-clone a CarOverride so the last-persisted snapshot
        /// is independent of the live in-memory override.</summary>
        private static CarOverride CloneCarOverride(CarOverride o)
        {
            if (o == null) return null;
            return new CarOverride
            {
                EnginePulse  = o.EnginePulse  == null ? null : Clone(o.EnginePulse),
                RoadBumps    = o.RoadBumps    == null ? null : Clone(o.RoadBumps),
                TractionLoss = o.TractionLoss == null ? null : Clone(o.TractionLoss),
                GearShift    = o.GearShift    == null ? null : Clone(o.GearShift),
                AbsClick     = o.AbsClick     == null ? null : Clone(o.AbsClick),
                PitLimiter   = o.PitLimiter   == null ? null : Clone(o.PitLimiter),
                Drs          = o.Drs          == null ? null : Clone(o.Drs),
                Collision    = o.Collision    == null ? null : Clone(o.Collision),
                RevLimiter   = o.RevLimiter   == null ? null : Clone(o.RevLimiter),
                AudioCapture = CloneOrNull(o.AudioCapture),
                Airborne     = o.Airborne     == null ? null : Clone(o.Airborne),
            };
        }

        /// <summary>Write the active car's override to the active car preset
        /// file (Settings.CarDefaults[activeCarId]) and update the
        /// last-persisted snapshot. Refuses to overwrite a built-in factory
        /// preset; callers must check IsActiveCarPresetBuiltin() and fork
        /// via SaveActiveCarPresetAs(newName) instead. Returns true if the
        /// write happened.</summary>
        public bool PersistActiveCarOverride()
        {
            if (_carStore == null || string.IsNullOrEmpty(_activeCarId) || Settings?.CarOverrides == null) return false;
            string presetName = GetActiveCarPresetName(_activeCarId);
            if (string.IsNullOrEmpty(presetName)) return false;
            // DEV authoring: built-in cars get written through to the factory
            // folder, mirroring the SavePresetAs DEV bypass for games.
            if (IsCarPresetBuiltin(_activeCarId, presetName))
            {
                if (!DevMode) return false;   // non-dev: caller forks via SaveActiveCarPresetAs
                WriteCarBuiltinThroughDev(_activeCarId, presetName);
                return true;
            }

            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            _carStore.Save(_activeCarId, presetName, _activeGame ?? "", ovr, isBuiltin: false,
                defaultAuthor: CurrentAuthorForStamp());
            if (ovr == null || ovr.IsEmpty)
                _lastPersistedCarOverrides.Remove(_activeCarId);
            else
                _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(ovr);
            return true;
        }

        /// <summary>Fork the current live override into a new user preset
        /// for the active car. Sets CarDefaults[activeCarId] to the new
        /// name. Returns true on success. Used by the UI fork-on-default
        /// flow when the user saves changes while on a built-in preset.</summary>
        public bool SaveActiveCarPresetAs(string newPresetName)
        {
            if (_carStore == null || Settings == null || string.IsNullOrEmpty(_activeCarId)) return false;
            if (string.IsNullOrEmpty(newPresetName)) return false;
            // User-typed name might inadvertently carry the display-only
            // " (Built-In)" suffix (e.g. forked from a built-in's auto-suggest);
            // strip so the on-disk filename and CarDefaults binding are clean.
            string newDisk = ToDiskName(newPresetName);
            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            _carStore.Save(_activeCarId, newDisk, _activeGame ?? "", ovr, isBuiltin: false,
                defaultAuthor: CurrentAuthorForStamp());
            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults[_activeCarId] = newDisk;
            if (ovr == null || ovr.IsEmpty)
                _lastPersistedCarOverrides.Remove(_activeCarId);
            else
                _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(ovr);
            return true;
        }

        /// <summary>Wipe a car's per-car file AND its in-memory override.
        /// Refused for built-in presets (delete-protection). The car will
        /// fall back to the next-best preset (factory default if one exists,
        /// else globals) on its next ApplyActiveCarOverride.</summary>
        public bool DeleteCarPreset(string carId, string presetName)
        {
            if (_carStore == null || string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            bool wasBuiltin = IsCarPresetBuiltin(carId, presetName);
            if (wasBuiltin && !DevMode) return false;   // DEV authoring may delete built-ins
            // presetName from UI carries the " (Built-In)" suffix for factory
            // entries; disk file names never do. Strip to the on-disk form for
            // any file/dict lookup.
            string diskName = ToDiskName(presetName);
            string game = GetCarPresetGame(carId, presetName);
            _carStore.Delete(carId, diskName);
            if (Settings?.CarDefaults != null
                && Settings.CarDefaults.TryGetValue(carId, out var active)
                && string.Equals(active, presetName, StringComparison.Ordinal))
            {
                Settings.CarDefaults.Remove(carId);
                if (carId == _activeCarId)
                    ReloadActiveCarOverrideFromStore();
            }
            if (wasBuiltin && DevMode)
            {
                try
                {
                    BuiltinPresetWriter.DeleteCar(BuiltinPresets.CurrentFolder, game ?? "", carId, diskName);
                    // If that was the car's built-in default, drop the binding.
                    // CarDefaultBindings values come from car-defaults.json (disk
                    // form, never suffixed), so compare against diskName.
                    if (BuiltinPresets.CarDefaultBindings.TryGetValue(carId, out var def)
                        && string.Equals(def, diskName, StringComparison.Ordinal))
                        BuiltinPresetWriter.RemoveCarDefault(BuiltinPresets.CurrentFolder, carId);
                    BuiltinPresets.Reload();
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] DEV delete car folder file failed: {ex.Message}"); }
            }
            return true;
        }

        /// <summary>Rename a car preset on disk. Updates CarDefaults for that
        /// car if the renamed preset was the active one. Refuses on built-ins
        /// and when the target name already exists for that car. Used by the
        /// preset manager.</summary>
        public bool RenameCarPreset(string carId, string oldName, string newName)
        {
            if (_carStore == null || Settings == null) return false;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;
            bool wasBuiltin = IsCarPresetBuiltin(carId, oldName);
            if (wasBuiltin && !DevMode)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to rename built-in car preset '{carId}/{oldName}'.");
                return false;
            }
            // The names from UI may carry the " (Built-In)" suffix (factory
            // rows) -- strip to the disk form for file/dict ops. Don't allow
            // the suffix in user-typed newName either (that's display sugar,
            // not a real name).
            string oldDisk = ToDiskName(oldName);
            string newDisk = ToDiskName(newName);
            // Collision check needs to consider built-ins too in DEV (otherwise
            // we'd silently overwrite a sibling built-in for the same car).
            if (_carStore.Exists(carId, newDisk)) return false;
            if (BuiltinPresets.CarPresetJsons.ContainsKey(carId + "/" + newDisk)) return false;

            if (wasBuiltin)
            {
                // DEV: rename the factory file + rewrite its inner PresetName so
                // the load key (carId + "/" + PresetName) matches the new filename.
                string game = GetCarPresetGame(carId, oldName) ?? "";
                BuiltinPresetWriter.RenameCar(BuiltinPresets.CurrentFolder, game, carId, oldDisk, newDisk);
                try
                {
                    if (BuiltinPresets.CarPresetJsons.TryGetValue(carId + "/" + oldDisk, out var staleJson))
                    {
                        var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(staleJson);
                        if (f != null)
                        {
                            f.PresetName = newDisk;
                            f.IsBuiltin = true;
                            string json = Newtonsoft.Json.JsonConvert.SerializeObject(f, Newtonsoft.Json.Formatting.Indented);
                            BuiltinPresetWriter.WriteCar(BuiltinPresets.CurrentFolder, f.GameName ?? game, carId, newDisk, json);
                        }
                    }
                }
                catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] DEV rename PresetName rewrite failed: {ex.Message}"); }
                BuiltinPresets.Reload();
                LoadAndMigrateCarPresets();
                if (carId == _activeCarId) ReloadActiveCarOverrideFromStore();
                SimHub.Logging.Current.Info($"[Trueforce] DEV: renamed built-in car preset '{carId}/{oldDisk}' to '{newDisk}'.");
                return true;
            }

            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar) || !perCar.TryGetValue(oldDisk, out var entry)) return false;

            // Keep pack identity across a rename so the Source column doesn't
            // flip to "Local" for an imported preset the user renamed.
            _carStore.Save(carId, newDisk, entry.GameName ?? "", entry.Override, isBuiltin: false,
                packName: entry.PackName, author: entry.Author);
            _carStore.Delete(carId, oldDisk);

            if (Settings.CarDefaults != null
                && Settings.CarDefaults.TryGetValue(carId, out var active)
                && string.Equals(active, oldName, StringComparison.Ordinal))
            {
                Settings.CarDefaults[carId] = newDisk;
                this.SaveCommonSettings("GeneralSettings", Settings);
            }
            if (carId == _activeCarId) ReloadActiveCarOverrideFromStore();
            SimHub.Logging.Current.Info($"[Trueforce] Renamed car preset '{carId}/{oldName}' to '{newName}'.");
            return true;
        }

        /// <summary>Deep-copy a car preset under a new name (same carId).
        /// JSON round-trip clone so the new file is independent. Refuses if
        /// the target already exists.</summary>
        public bool DuplicateCarPreset(string carId, string sourceName, string newName)
        {
            if (_carStore == null) return false;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(newName)) return false;
            // sourceName may be a factory display name (suffixed); pull from
            // the merged map (which has both user + suffixed factory entries)
            // and persist the clone as a user file under the on-disk newName.
            string sourceDisk = ToDiskName(sourceName);
            string newDisk    = ToDiskName(newName);
            if (_carStore.Exists(carId, newDisk)) return false;

            CarPresetEntry entry = null;
            var all = GetAllCarPresets();
            if (all != null && all.TryGetValue(carId, out var perCarAll))
                perCarAll.TryGetValue(sourceName, out entry);

            if (entry == null) return false;
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(entry.Override);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<CarOverride>(json);
            // A duplicate of an imported preset stays attributed to its
            // pack. When the source had no Author, stamp the duplicator (the
            // user) via defaultAuthor so the new preset reads as theirs in
            // the Source column. Carry Description + AuthorVersion too so
            // the duplicate is a faithful copy, not a metadata-stripped one.
            _carStore.Save(carId, newDisk, entry.GameName ?? "", clone, isBuiltin: false,
                packName:      entry.PackName,
                author:        entry.Author,
                description:   entry.Description,
                authorVersion: entry.AuthorVersion,
                defaultAuthor: CurrentAuthorForStamp());
            SimHub.Logging.Current.Info($"[Trueforce] Duplicated car preset '{carId}/{sourceDisk}' as '{newDisk}'.");
            return true;
        }

        /// <summary>Re-resolve the active preset for the active car after a
        /// preset switch / delete and update Settings.CarOverrides + the
        /// persisted-snapshot baseline + the live effect parameters.</summary>
        public void ReloadActiveCarOverrideFromStore()
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null || _carStore == null) return;
            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(_activeCarId, out var perCar) || perCar.Count == 0)
            {
                Settings.CarOverrides.Remove(_activeCarId);
                _lastPersistedCarOverrides.Remove(_activeCarId);
                ApplyActiveCarOverride();
                return;
            }
            string activeName = ResolveActiveCarPresetName(_activeCarId, perCar);
            if (activeName != null && perCar.TryGetValue(activeName, out var entry))
            {
                Settings.CarOverrides[_activeCarId] = entry.Override;
                _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(entry.Override);
                if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
                Settings.CarDefaults[_activeCarId] = activeName;
            }
            else
            {
                Settings.CarOverrides.Remove(_activeCarId);
                _lastPersistedCarOverrides.Remove(_activeCarId);
            }
            ApplyActiveCarOverride();
        }

        /// <summary>Switch the active preset for a car to the named one and
        /// reload the override into live state. Used by the dropdown.</summary>
        public bool SwitchActiveCarPreset(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            if (Settings == null) return false;
            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults[carId] = presetName;
            // Persist the user choice to the user library's car-defaults.json
            // (the persistent storage; Settings.CarDefaults is a runtime cache
            // rebuilt from disk on every Init).
            try
            {
                BuiltinPresetWriter.SetCarDefault(UserPresets.CurrentFolder, carId, presetName);
                UserPresets.Reload();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Persist car-default for '{carId}' failed: {ex.Message}");
            }
            if (carId == _activeCarId) ReloadActiveCarOverrideFromStore();
            return true;
        }

        /// <summary>Set a car's default preset (the Presets-tab "Set as default"
        /// action). Same as SwitchActiveCarPreset, but in DEV authoring mode it
        /// also writes car-defaults.json so the built-in default is recorded in
        /// the folder. (SwitchActiveCarPreset itself stays folder-free since
        /// it's also used while just selecting a car to edit.)</summary>
        public bool SetCarDefaultPreset(string carId, string presetName)
        {
            if (!SwitchActiveCarPreset(carId, presetName)) return false;
            if (DevMode)
            {
                // Factory car-defaults.json references on-disk filenames -- strip
                // the display suffix before writing or future loads of the file
                // would point at "<name> (Built-In)" which doesn't exist on disk.
                try { BuiltinPresetWriter.SetCarDefault(BuiltinPresets.CurrentFolder, carId, ToDiskName(presetName)); BuiltinPresets.Reload(); }
                catch (Exception ex) { SimHub.Logging.Current.Warn($"[Trueforce] DEV write car-default failed: {ex.Message}"); }
            }
            return true;
        }

        /// <summary>Make <paramref name="carId"/> the active car for editing AND
        /// switch it to <paramref name="presetName"/>, even when telemetry hasn't
        /// identified a car (paused / menu) or you're physically in a different
        /// car. Pins _activeCarId so the normal per-car flow (live override,
        /// slider edits, Save) targets this car and its preset loads live. While
        /// you're actually driving, the next telemetry frame re-asserts the real
        /// car; while paused / at a menu the pin holds. Lets the car-preset
        /// picker work any time, which is what the UI needs.</summary>
        public bool SelectCarForEditing(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            _activeCarId = carId;                            // pin as active
            return SwitchActiveCarPreset(carId, presetName); // carId == _activeCarId now -> reloads + applies
        }

        /// <summary>Clear a car's per-car preset selection so it stops applying
        /// a per-car override and falls back to the game preset's globals. Pins
        /// the car as active, drops its CarDefaults entry and live override, and
        /// re-applies (no override = game globals), then persists. The car
        /// picker's "None" row calls this. The saved preset FILES are left on
        /// disk (this is a deselect, not a delete); note that a car shipping a
        /// built-in factory preset resolves back to that built-in the next time
        /// it loads, while a car with only user presets clears to the game
        /// preset.</summary>
        public bool ClearActiveCarPreset(string carId)
        {
            if (string.IsNullOrEmpty(carId) || Settings == null) return false;
            _activeCarId = carId;   // pin so the apply targets this car
            Settings.CarDefaults?.Remove(carId);
            Settings.CarOverrides?.Remove(carId);
            _lastPersistedCarOverrides?.Remove(carId);
            ApplyActiveCarOverride();   // no override now -> game globals
            PersistSettings();
            return true;
        }

        /// <summary>Returns the preset name currently active for a car
        /// (CarDefaults lookup), or null if unset.</summary>
        public string GetActiveCarPresetName(string carId)
        {
            if (Settings?.CarDefaults == null || string.IsNullOrEmpty(carId)) return null;
            return Settings.CarDefaults.TryGetValue(carId, out var n) ? n : null;
        }

        /// <summary>Returns all presets currently on disk for a car. Empty
        /// dict if the car has none. Used by the UI to populate the
        /// per-car preset dropdown. Includes factory built-ins merged on top
        /// of user files (built-in wins on a same-name collision).</summary>
        public IReadOnlyDictionary<string, CarPresetEntry> GetCarPresets(string carId)
        {
            if (_carStore == null || string.IsNullOrEmpty(carId))
                return new Dictionary<string, CarPresetEntry>();
            var loaded = _carStore.LoadAll();
            MergeBuiltinCarPresetsInto(loaded);
            return loaded.TryGetValue(carId, out var perCar)
                ? perCar
                : new Dictionary<string, CarPresetEntry>();
        }

        /// <summary>Returns every car preset across every car, indexed by
        /// carId then presetName. Single LoadAll pass for the preset manager
        /// (used when no specific car is active). Includes factory built-ins
        /// so the preset manager can tag them with the Built-in badge.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, CarPresetEntry>> GetAllCarPresets()
        {
            if (_carStore == null)
                return new Dictionary<string, IReadOnlyDictionary<string, CarPresetEntry>>();
            var raw = _carStore.LoadAll();
            MergeBuiltinCarPresetsInto(raw);
            var wrapped = new Dictionary<string, IReadOnlyDictionary<string, CarPresetEntry>>(raw.Count);
            foreach (var kv in raw) wrapped[kv.Key] = kv.Value;
            return wrapped;
        }

        /// <summary>Display-layer suffix that MergeBuiltinCarPresetsInto stamps
        /// on every factory car preset's PresetName, so user vs factory entries
        /// with the same on-disk name don't collide in the merged map and so the
        /// UI row clearly reads as a built-in. Disk filenames never include the
        /// suffix; ToDiskName strips it back off at disk-op boundaries.</summary>
        public const string BuiltinNameSuffix = " (Built-In)";

        /// <summary>True iff <paramref name="name"/> ends with the built-in
        /// display suffix (i.e. was stamped by MergeBuiltinCarPresetsInto).</summary>
        internal static bool HasBuiltinSuffix(string name)
            => !string.IsNullOrEmpty(name)
            && name.EndsWith(BuiltinNameSuffix, StringComparison.Ordinal);

        /// <summary>Return the on-disk PresetName by stripping the
        /// " (Built-In)" suffix when present. Idempotent; safe to call on
        /// names that don't have the suffix (user presets, factory names
        /// already loaded from disk).</summary>
        internal static string ToDiskName(string name)
            => HasBuiltinSuffix(name)
                ? name.Substring(0, name.Length - BuiltinNameSuffix.Length)
                : name;

        /// <summary>True iff the named car preset is a factory built-in.
        /// Requires the display suffix AND a matching unsuffixed entry in
        /// BuiltinPresets.CarPresetJsons: that way a user file literally
        /// named "X" (with the factory also shipping "X") isn't mistaken
        /// for the factory entry just because the names overlap.</summary>
        public bool IsCarPresetBuiltin(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName))
                return false;
            if (!HasBuiltinSuffix(presetName)) return false;
            string disk = ToDiskName(presetName);
            return BuiltinPresets.CarPresetJsons.ContainsKey(carId + "/" + disk);
        }

        // ---------- Source attribution (Preset Manager "Source" column) ----------

        /// <summary>Label shown in the Preset Manager's Source column for a
        /// game preset row. "Built-In" for factory presets, the pack label for
        /// presets recorded in the installed-packs sidecar, the snapshot's
        /// own PackName/Author (for stamped local saves or recovered pack
        /// metadata when the sidecar entry was lost), else "Local".</summary>
        public string ResolveGamePresetSource(string presetName, bool isBuiltin)
        {
            if (isBuiltin) return "Built-In";
            var pack = _installedPacks?.FindPackForGame(presetName);
            var fromPack = PackLabel(pack?.PackName, pack?.Author);
            if (fromPack != null) return fromPack;
            // Fallback: read the snapshot's own attribution. Catches both
            // user-stamped local presets (snap.Author = SharingAuthor) and
            // orphan pack-imported presets whose sidecar entry was lost.
            // Seeded-from-AC-baseline sentinel is treated as Local (the user
            // hasn't touched it yet; once they do, the sentinel clears).
            GameSettingsSnapshot snap = null;
            if (Settings?.Presets != null) Settings.Presets.TryGetValue(presetName, out snap);
            string snapPack = NullIfBlank(snap?.PackName);
            if (string.Equals(snapPack, SeededPresetSentinel, StringComparison.Ordinal)) snapPack = null;
            return PackLabel(snapPack, snap?.Author) ?? "Local";
        }

        // Author string stamped on locally-saved presets. Null when the user
        // hasn't set Settings.SharingAuthor, in which case saves leave Author
        // blank and ResolveGamePresetSource falls through to "Local". Single
        // source of truth so the policy lives in one place.
        private string CurrentAuthorForStamp() => NullIfBlank(Settings?.SharingAuthor);

        // Sentinel PackName stamped on the AC-baseline seed file written by
        // EnsureSeededGamePreset. Used in two places: (a) BackfillAuthorOnLocal-
        // Presets skips snapshots carrying this so an untouched seed never
        // gets attributed to the user; (b) SaveSectionToActivePreset clears
        // it on the user's first edit, after which the preset behaves like
        // any other user-authored work. The literal string is chosen to be
        // unambiguous if it ever leaks into the UI (which it shouldn't).
        internal const string SeededPresetSentinel = "(seeded from Assetto Corsa baseline)";

        /// <summary>One-shot backfill: walk every user-library game-preset
        /// and car-preset file, set Author = <paramref name="author"/> on
        /// the ones whose Author AND PackName are both blank (= the user's
        /// own unattributed work). Files that already carry pack lineage or
        /// an explicit author are left alone. Returns the count of files
        /// touched. Triggered from the Settings UI on the first blank-to-set
        /// transition of Settings.SharingAuthor.</summary>
        public int BackfillAuthorOnLocalPresets(string author)
        {
            author = NullIfBlank(author);
            if (author == null) return 0;
            int touched = 0;

            // ---- Game presets ----
            try
            {
                string gamesDir = System.IO.Path.Combine(UserPresets.CurrentFolder ?? "", "games");
                if (System.IO.Directory.Exists(gamesDir))
                {
                    foreach (var path in System.IO.Directory.GetFiles(gamesDir, "*.json"))
                    {
                        try
                        {
                            var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(
                                System.IO.File.ReadAllText(path));
                            if (snap == null) continue;
                            if (!string.IsNullOrEmpty(snap.Author) || !string.IsNullOrEmpty(snap.PackName)) continue;
                            snap.Author = author;
                            System.IO.File.WriteAllText(path,
                                Newtonsoft.Json.JsonConvert.SerializeObject(snap, Newtonsoft.Json.Formatting.Indented));
                            touched++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Backfill: couldn't update '{path}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Backfill (games) failed: {ex.Message}");
            }

            // ---- Car presets ----
            try
            {
                string carsRoot = System.IO.Path.Combine(UserPresets.CurrentFolder ?? "", "cars");
                if (System.IO.Directory.Exists(carsRoot))
                {
                    foreach (var path in System.IO.Directory.GetFiles(carsRoot, "*.json", System.IO.SearchOption.AllDirectories))
                    {
                        // Skip the cleanup-archive backups produced by the
                        // legacy-builtin cleanup migration.
                        if (path.IndexOf($"{System.IO.Path.DirectorySeparatorChar}.cleanup-", StringComparison.Ordinal) >= 0)
                            continue;
                        try
                        {
                            var f = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(
                                System.IO.File.ReadAllText(path));
                            if (f == null || f.Override == null) continue;
                            if (!string.IsNullOrEmpty(f.Author) || !string.IsNullOrEmpty(f.PackName)) continue;
                            f.Author = author;
                            System.IO.File.WriteAllText(path,
                                Newtonsoft.Json.JsonConvert.SerializeObject(f, Newtonsoft.Json.Formatting.Indented));
                            touched++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Backfill: couldn't update '{path}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Backfill (cars) failed: {ex.Message}");
            }

            // Reload + rebuild so the in-memory cache reflects the new
            // attribution and the Source column refreshes on next read.
            try
            {
                UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Backfill reload failed: {ex.Message}");
            }

            SimHub.Logging.Current.Info($"[Trueforce] Author backfill: stamped '{author}' on {touched} local preset file(s).");
            return touched;
        }

        /// <summary>Label shown in the Preset Manager's Source column for a car
        /// preset row. "Built-In" for factory presets; else the pack label when
        /// the preset is in the installed-packs sidecar OR the on-disk car file
        /// carries pack metadata (passed in by the caller); else "Local".</summary>
        public string ResolveCarPresetSource(string carId, string presetName, bool isBuiltin,
            string filePackName = null, string fileAuthor = null)
        {
            if (isBuiltin) return "Built-In";
            // Sidecar first (richest identity), then the car file's own
            // PackName/Author (loose-imported single car files), then Local.
            var pack = _installedPacks?.FindPackForCar(carId, presetName);
            return PackLabel(pack?.PackName, pack?.Author)
                ?? PackLabel(filePackName, fileAuthor)
                ?? "Local";
        }

        // Compose a Source label from a pack name and author. Prefer
        // "PackName (Author)" when both exist, else PackName, else Author.
        // Returns null when neither is present (caller falls back to "Local").
        private static string PackLabel(string packName, string author)
        {
            string p = NullIfBlank(packName);
            string a = NullIfBlank(author);
            if (p != null && a != null) return $"{p} ({a})";
            return p ?? a;
        }

        /// <summary>True iff the active preset for the active car is a
        /// factory built-in. Used by the UI to gate save behavior.</summary>
        public bool IsActiveCarPresetBuiltin()
        {
            if (string.IsNullOrEmpty(_activeCarId)) return false;
            string presetName = GetActiveCarPresetName(_activeCarId);
            return !string.IsNullOrEmpty(presetName) && IsCarPresetBuiltin(_activeCarId, presetName);
        }

        /// <summary>True iff the live per-car override for the active car
        /// has drifted from the snapshot last loaded from disk. Used by the
        /// UI to roll car-preset edits into the global "★ unsaved"
        /// indicator and to gate the Save preset button's car-side save
        /// step.</summary>
        public bool IsActiveCarPresetDirty()
        {
            if (Settings?.CarOverrides == null || string.IsNullOrEmpty(_activeCarId)) return false;
            Settings.CarOverrides.TryGetValue(_activeCarId, out var live);
            _lastPersistedCarOverrides.TryGetValue(_activeCarId, out var saved);

            bool liveEmpty  = live  == null || live.IsEmpty;
            bool savedEmpty = saved == null || saved.IsEmpty;
            if (liveEmpty && savedEmpty) return false;   // nothing on either side

            // Compare EFFECTIVE per-section values: a section absent from an
            // override means "follow the game default", so effective = the
            // override's section if present, else the active preset's section
            // (snap). This mirrors EffectEquals and is what lets a change-then-
            // revert clear the dirty state: a draft override whose values equal
            // the game default reads clean against a car with no saved override,
            // instead of sticking dirty forever just because EnsureSectionDraft
            // materialized an override. (The old CarOverrideEquals compared
            // live-vs-saved directly, so a default-valued draft stayed dirty,
            // and it ignored PitLimiter/Drs/Collision/RevLimiter entirely.)
            GameSettingsSnapshot snap = null;
            if (!string.IsNullOrEmpty(_activePresetName) && Settings.Presets != null)
                Settings.Presets.TryGetValue(_activePresetName, out snap);

            if (!Eq(live?.EnginePulse  ?? snap?.EnginePulse,  saved?.EnginePulse  ?? snap?.EnginePulse))  return true;
            if (!Eq(live?.RoadBumps    ?? snap?.RoadBumps,    saved?.RoadBumps    ?? snap?.RoadBumps))    return true;
            if (!Eq(live?.TractionLoss ?? snap?.TractionLoss, saved?.TractionLoss ?? snap?.TractionLoss)) return true;
            if (!Eq(live?.GearShift    ?? snap?.GearShift,    saved?.GearShift    ?? snap?.GearShift))    return true;
            if (!Eq(live?.AbsClick     ?? snap?.AbsClick,     saved?.AbsClick     ?? snap?.AbsClick))     return true;
            if (!Eq(live?.PitLimiter   ?? snap?.PitLimiter,   saved?.PitLimiter   ?? snap?.PitLimiter))   return true;
            if (!Eq(live?.Drs          ?? snap?.Drs,          saved?.Drs          ?? snap?.Drs))          return true;
            if (!Eq(live?.Collision    ?? snap?.Collision,    saved?.Collision    ?? snap?.Collision))    return true;
            if (!Eq(live?.AudioCapture ?? snap?.AudioCapture, saved?.AudioCapture ?? snap?.AudioCapture)) return true;
            if (!Eq(live?.RevLimiter   ?? snap?.RevLimiter ?? new RevLimiterSettings(),
                    saved?.RevLimiter  ?? snap?.RevLimiter ?? new RevLimiterSettings())) return true;
            return false;
        }

        /// <summary>Snapshot a section's current values into the per-car
        /// override (in-memory only; does NOT write to disk). If the
        /// section already has an override, keeps it as-is. After this call
        /// Settings.CarOverrides[activeCarId] reflects what would be saved
        /// to disk; the caller decides whether to persist (in-place save)
        /// or fork to a new user preset.</summary>
        public void SnapshotSectionToCarOverride(SectionKind kind)
        {
            if (string.IsNullOrEmpty(_activeCarId) || Settings == null) return;
            if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();
            if (!Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr) || ovr == null)
            {
                ovr = new CarOverride();
                Settings.CarOverrides[_activeCarId] = ovr;
            }
            switch (kind)
            {
                case SectionKind.Engine:   if (ovr.EnginePulse  == null) ovr.EnginePulse  = Clone(Settings.EnginePulse);    break;
                case SectionKind.Bumps:    if (ovr.RoadBumps    == null) ovr.RoadBumps    = Clone(Settings.RoadBumps);      break;
                case SectionKind.Traction: if (ovr.TractionLoss == null) ovr.TractionLoss = Clone(Settings.TractionLoss);   break;
                case SectionKind.Shift:    if (ovr.GearShift    == null) ovr.GearShift    = Clone(Settings.GearShift);      break;
                case SectionKind.Abs:        if (ovr.AbsClick     == null) ovr.AbsClick     = Clone(Settings.AbsClick);       break;
                case SectionKind.PitLimiter: if (ovr.PitLimiter   == null) ovr.PitLimiter   = Clone(Settings.PitLimiter);     break;
                case SectionKind.Drs:        if (ovr.Drs          == null) ovr.Drs          = Clone(Settings.Drs);            break;
                case SectionKind.Collision:  if (ovr.Collision    == null) ovr.Collision    = Clone(Settings.Collision);      break;
                case SectionKind.RevLimiter: if (ovr.RevLimiter   == null) ovr.RevLimiter   = Clone(Settings.RevLimiter);     break;
                case SectionKind.Audio:      if (ovr.AudioCapture == null) ovr.AudioCapture = CloneOrNull(Settings.AudioCapture); break;
                case SectionKind.Airborne:   if (ovr.Airborne     == null) ovr.Airborne     = Clone(Settings.Airborne);       break;
                default: return;  // Master / Ducking / SpikeReduction aren't per-car
            }
            ApplyActiveCarOverride();
        }

        /// <summary>"Update game defaults" save action when the section is
        /// car-overridden: lifts the override values up to the global
        /// section, then drops the override (so the new global takes
        /// effect for this car too). Caller should follow with
        /// SavePresetAs to commit the new global into the active preset.</summary>
        public void PromoteSectionToGlobal(SectionKind kind)
        {
            if (Settings == null || string.IsNullOrEmpty(_activeCarId)) return;
            if (Settings.CarOverrides == null
                || !Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr)
                || ovr == null) return;
            switch (kind)
            {
                case SectionKind.Engine:   if (ovr.EnginePulse  != null) { Settings.EnginePulse  = Clone(ovr.EnginePulse);    ovr.EnginePulse  = null; } break;
                case SectionKind.Bumps:    if (ovr.RoadBumps    != null) { Settings.RoadBumps    = Clone(ovr.RoadBumps);      ovr.RoadBumps    = null; } break;
                case SectionKind.Traction: if (ovr.TractionLoss != null) { Settings.TractionLoss = Clone(ovr.TractionLoss);   ovr.TractionLoss = null; } break;
                case SectionKind.Shift:    if (ovr.GearShift    != null) { Settings.GearShift    = Clone(ovr.GearShift);      ovr.GearShift    = null; } break;
                case SectionKind.Abs:        if (ovr.AbsClick     != null) { Settings.AbsClick     = Clone(ovr.AbsClick);       ovr.AbsClick     = null; } break;
                case SectionKind.PitLimiter: if (ovr.PitLimiter   != null) { Settings.PitLimiter   = Clone(ovr.PitLimiter);     ovr.PitLimiter   = null; } break;
                case SectionKind.Drs:        if (ovr.Drs          != null) { Settings.Drs          = Clone(ovr.Drs);            ovr.Drs          = null; } break;
                case SectionKind.Collision:  if (ovr.Collision    != null) { Settings.Collision    = Clone(ovr.Collision);      ovr.Collision    = null; } break;
                case SectionKind.RevLimiter: if (ovr.RevLimiter   != null) { Settings.RevLimiter   = Clone(ovr.RevLimiter);     ovr.RevLimiter   = null; } break;
                case SectionKind.Audio:      if (ovr.AudioCapture != null) { Settings.AudioCapture = CloneOrNull(ovr.AudioCapture); ovr.AudioCapture = null; } break;
                case SectionKind.Airborne:   if (ovr.Airborne     != null) { Settings.Airborne     = Clone(ovr.Airborne);       ovr.Airborne     = null; } break;
                default: return;
            }
            if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            PersistActiveCarOverride();
            ApplyActiveCarOverride();
        }

        /// <summary>True if the named preset is a built-in / read-only one.
        /// Built-ins refuse delete and refuse in-place overwrite, the UI
        /// forks to a user-named preset instead.</summary>
        public bool IsBuiltinPreset(string presetName) => BuiltinPresets.IsBuiltin(presetName);

        /// <summary>One-time migration of legacy per-game presets (keyed by
        /// game name with no separate "preset library" concept) into the new
        /// model: each becomes a preset named after the game, and the game's
        /// default is bound to it. Idempotent, runs once when GamePresets is
        /// non-empty and the new fields are still empty.</summary>
        private void MigrateLegacyGamePresets()
        {
            if (Settings?.GamePresets == null || Settings.GamePresets.Count == 0) return;
            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            int moved = 0;
            foreach (var kv in Settings.GamePresets)
            {
                if (kv.Value == null) continue;
                if (!Settings.Presets.ContainsKey(kv.Key))
                    Settings.Presets[kv.Key] = kv.Value;
                if (!Settings.GameDefaults.ContainsKey(kv.Key))
                    Settings.GameDefaults[kv.Key] = kv.Key;
                moved++;
            }
            Settings.GamePresets.Clear();
            if (moved > 0)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Migrated {moved} legacy game-preset(s) to named library.");
            }
        }

        // Promote existing Settings.CarCylinderCache entries to seed
        // EngineVariants in Settings.CarFacts. The cache is the cylinder-only
        // ancestor of the CarFacts layer; on the first run after this build
        // ships, each cached entry becomes a single "Stock" variant inside
        // its bundle. Idempotent: gated by Settings.CarFactsMigratedV1 at
        // the call site, and per-key skipped if a bundle already exists
        // (so a partial migration mid-flight wouldn't clobber edits).
        //
        // Cache shape: Dictionary<gameName, Dictionary<carId, encodedInt>>.
        // Encoding (mirrors CarCylinderResolver): EvSentinel (-1) = electric,
        // otherwise bits 0-4 = cylinder count (1-16), bits 8-11 = EngineConfig.
        private void MigrateCarCylinderCacheToCarFacts()
        {
            const int EvSentinel  = -1;
            const int CylBits     = 0x1F;
            const int ConfigShift = 8;
            const int ConfigBits  = 0xF00;

            if (Settings?.CarCylinderCache == null) return;
            if (Settings.CarFacts == null)
                Settings.CarFacts = new Dictionary<string, CarFactsBundle>();

            int seeded = 0;
            foreach (var gameKv in Settings.CarCylinderCache)
            {
                string game = gameKv.Key;
                if (string.IsNullOrEmpty(game) || gameKv.Value == null) continue;
                foreach (var carKv in gameKv.Value)
                {
                    string carId = carKv.Key;
                    if (string.IsNullOrEmpty(carId)) continue;
                    int encoded = carKv.Value;

                    // Skip EV entries: the legacy cache stored electric as
                    // a sentinel and the EnginePulse path detects electric
                    // separately via the resolver. Variants only carry
                    // EngineConfig values that the FiringPatternDb knows,
                    // so seeding "electric" as a variant would mislead.
                    if (encoded == EvSentinel) continue;

                    int cyl = encoded & CylBits;
                    int cfgIdx = (encoded & ConfigBits) >> ConfigShift;
                    if (cfgIdx < 0 || cfgIdx > (int)EngineConfig.Custom) cfgIdx = 0;
                    EngineConfig cfg = (EngineConfig)cfgIdx;
                    if (cyl < 1 || cyl > 16) continue;

                    string key = game + "/" + carId;
                    if (Settings.CarFacts.TryGetValue(key, out var existing) && existing != null
                        && existing.EngineVariants != null && existing.EngineVariants.Count > 0)
                        continue;

                    var variant = new EngineVariant
                    {
                        Id            = Guid.NewGuid().ToString("N"),
                        Label         = "Stock",
                        Cylinders     = cyl,
                        EngineConfig  = cfg,
                        RedlineRpm    = null,
                        Source        = CarFactSource.Scanner,
                        Confirmations = 0,
                    };
                    var bundle = existing ?? new CarFactsBundle();
                    if (bundle.EngineVariants == null)
                        bundle.EngineVariants = new List<EngineVariant>();
                    bundle.EngineVariants.Add(variant);
                    Settings.CarFacts[key] = bundle;
                    seeded++;
                }
            }

            if (seeded > 0)
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Seeded {seeded} car-fact variant(s) from the cylinder cache.");
        }

        // CarFacts lookup at apply time. Resolution order:
        //   1. Authoritative stored variant for (game,carId) — User, Community,
        //      or Scanner with Confirmations > 0 — wins outright (user-
        //      confirmed local truth or community-vetted data).
        //   2. Else if BuiltinCarCylinders has an entry, synthesize a virtual
        //      "Baked" variant (Source=Baked, Id="baked:{game}/{carId}"). The
        //      bake is the curated baseline shipped in the DLL (~890 AC cars
        //      + ~840 FH5 cars); we surface it through CarFacts so the apply
        //      cascade, the future Stage 2 variant picker, and submission
        //      flows all see one unified source of car facts.
        //   3. Else if an unconfirmed Scanner-seed stored variant exists (the
        //      legacy CarCylinderCache migration cohort), use it as last
        //      resort.
        //   4. Otherwise no variant; caller falls through to the heuristic
        //      tokenizer / telemetry paths.
        // (Telemetry-based "unambiguous match" disambiguation is Stage 3.)
        private bool TryResolveActiveVariant(string game, string carId, out EngineVariant variant)
        {
            variant = null;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return false;

            EngineVariant storedPick = PickStoredVariant(game, carId);
            bool storedIsAuthoritative = storedPick != null
                && !(storedPick.Source == CarFactSource.Scanner && storedPick.Confirmations == 0);
            if (storedIsAuthoritative)
            {
                variant = storedPick;
                return true;
            }

            // Bake-class hit: defer to CarCylinderResolver which applies AC
            // swap-override + DetectEngineConfig refinements on top of the
            // raw bake values. Gating on BuiltinCarCylinders.TryGet first
            // means we only enter this branch for cars the bake knows about
            // (resolver heuristic-tokenizer hits are NOT surfaced here as
            // Baked: they stay in the legacy fallback path with Source
            // labels like "cylword" / "tag" / "codename").
            // EV special case: bake's IsElectric flag has no first-class
            // representation in EngineVariant yet, and a stale ICE Scanner
            // seed shouldn't shadow a known-electric bake entry. Return
            // false so the caller drops into the legacy resolver branch,
            // which handles EngineLayout.Electric correctly.
            if (BuiltinCarCylinders.TryGet(game, carId, out var rawBake))
            {
                if (rawBake.IsElectric) return false;
                if (CarCylinderResolver.TryResolve(game, carId, out var refined)
                    && refined != null
                    && !refined.IsElectric
                    && refined.Cylinders >= 1 && refined.Cylinders <= 16)
                {
                    // Preserve the AC swap-override attribution when the
                    // refinement pass changed the layout: lets the diagnostic
                    // banner read "(heuristic: swap-override)" instead of
                    // "(from built-in car list)" so a wrong swap-override
                    // is distinguishable from a wrong base bake entry.
                    var resolvedSource = string.Equals(refined.Source,
                        "swap-override", StringComparison.OrdinalIgnoreCase)
                        ? CarFactSource.SwapOverride
                        : CarFactSource.Baked;
                    variant = new EngineVariant
                    {
                        Id            = "baked:" + game + "/" + carId,
                        Label         = "Stock",
                        Cylinders     = refined.Cylinders,
                        EngineConfig  = refined.EngineConfig,
                        RedlineRpm    = null,
                        Source        = resolvedSource,
                        Confirmations = 0,
                    };
                    return true;
                }
            }

            if (storedPick != null)
            {
                variant = storedPick;
                return true;
            }

            return false;
        }

        // Picks one variant from the stored CarFacts bundle (if any), or null
        // when no stored data exists. Selection rules within the bundle:
        //   - Single variant → use it.
        //   - CarFactsSelection set → use that variant by Id.
        //   - Else highest-Confirmations variant (first-seen breaks ties).
        private EngineVariant PickStoredVariant(string game, string carId)
        {
            if (Settings?.CarFacts == null) return null;
            string key = game + "/" + carId;
            if (!Settings.CarFacts.TryGetValue(key, out var bundle) || bundle == null) return null;
            var variants = bundle.EngineVariants;
            if (variants == null || variants.Count == 0) return null;

            if (variants.Count == 1) return variants[0];

            if (Settings.CarFactsSelection != null
                && Settings.CarFactsSelection.TryGetValue(key, out var chosenId)
                && !string.IsNullOrEmpty(chosenId))
            {
                for (int i = 0; i < variants.Count; i++)
                    if (variants[i] != null && variants[i].Id == chosenId)
                        return variants[i];
            }

            EngineVariant best = null;
            int bestConf = int.MinValue;
            for (int i = 0; i < variants.Count; i++)
            {
                var cand = variants[i];
                if (cand == null) continue;
                if (cand.Confirmations > bestConf)
                {
                    best = cand;
                    bestConf = cand.Confirmations;
                }
            }
            return best;
        }

        // Map CarFactSource onto the source-label vocabulary the engine-layout
        // status renderer (SettingsControl.xaml.cs EngineLayoutAutoText)
        // already understands. Without this mapping the raw enum-name lowercase
        // ("scanner", "community", ...) falls through to the renderer's
        // "(heuristic: X)" catch-all and existing-cache users would see their
        // label silently regress from "cached from earlier session" to
        // "(heuristic: scanner)" on upgrade. Scanner-source variants in
        // Stage 1 are exclusively the cylinder-cache migration cohort, so
        // mapping them to "cache" preserves the pre-upgrade label exactly.
        private static string MapCarFactSourceToUiLabel(CarFactSource s)
        {
            switch (s)
            {
                case CarFactSource.Scanner:       return "cache";
                case CarFactSource.Community:     return "community";
                case CarFactSource.User:          return "user-set";
                case CarFactSource.GameTelemetry: return "telemetry";
                case CarFactSource.Baked:         return "baked";
                case CarFactSource.SwapOverride:  return "swap-override";
                default:                          return s.ToString().ToLowerInvariant();
            }
        }

        // FfbSpikeTamingEnabled was added after FfbSpikeMaxLsbPerMs /
        // FfbPeakSoftLimitLsb were already in the wild. Pre-flag versions had
        // either non-zero value mean "active". On upgrade, persisted settings
        // and saved presets carry the tuned values but no flag, so the flag
        // would default false and silently disable spike taming for users
        // who'd already tuned it. Infer the flag from the legacy values: if
        // either is non-zero, treat the user as having opted in.
        private void MigrateSpikeTamingFlag()
        {
            if (Settings == null) return;
            bool changed = false;
            if (!Settings.FfbSpikeTamingEnabled &&
                (Settings.FfbSpikeMaxLsbPerMs > 0f || Settings.FfbPeakSoftLimitLsb > 0f))
            {
                Settings.FfbSpikeTamingEnabled = true;
                changed = true;
            }
            if (Settings.Presets != null)
            {
                foreach (var snap in Settings.Presets.Values)
                {
                    if (snap == null) continue;
                    if (!snap.FfbSpikeTamingEnabled &&
                        (snap.FfbSpikeMaxLsbPerMs > 0f || snap.FfbPeakSoftLimitLsb > 0f))
                    {
                        snap.FfbSpikeTamingEnabled = true;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info("[Trueforce] Migrated FFB spike taming flag from legacy values.");
            }
        }

        // EnginePulse LoadLayer + HighRpmBoost shipped Off at 0.3 / 0.4 in
        // 0.1.7-0.1.8. In 0.1.9 the defaults flipped to On at 0.8 / 0.7. Saved
        // presets carry the old serialized values, so a flat default change
        // here doesn't reach them. Migrate per-field: if the saved value
        // exactly matches the prior default (Off + 0.3 / 0.4), treat it as
        // never-customized and upgrade. Any non-default tuning is preserved.
        // Touches Settings.EnginePulse, every game-preset snapshot, the live
        // CarOverrides cache, and rewrites any matching .tfcar.json files so
        // non-active per-car presets also pick up the new defaults.
        private void MigrateEngineHighRpmHelpersDefaults()
        {
            if (Settings == null) return;
            bool changed = false;

            if (MigrateEngineHighRpmFields(Settings.EnginePulse)) changed = true;

            if (Settings.Presets != null)
            {
                foreach (var snap in Settings.Presets.Values)
                {
                    if (snap?.EnginePulse == null) continue;
                    if (MigrateEngineHighRpmFields(snap.EnginePulse)) changed = true;
                }
            }

            if (Settings.CarOverrides != null)
            {
                foreach (var ovr in Settings.CarOverrides.Values)
                {
                    if (ovr?.EnginePulse == null) continue;
                    if (MigrateEngineHighRpmFields(ovr.EnginePulse)) changed = true;
                }
                // Resync _lastPersistedCarOverrides so dirty checks against the
                // live cache match the just-migrated values (otherwise every
                // car would read as dirty on first load).
                if (changed)
                {
                    foreach (var kv in Settings.CarOverrides)
                    {
                        if (kv.Value == null) continue;
                        _lastPersistedCarOverrides[kv.Key] = CloneCarOverride(kv.Value);
                    }
                }
            }

            // Rewrite .tfcar.json files for any non-active preset (or active
            // ones whose live cache we just migrated) so the new defaults
            // persist across launches.
            if (_carStore != null)
            {
                var loaded = _carStore.LoadAll();
                foreach (var carKv in loaded)
                {
                    foreach (var entry in carKv.Value.Values)
                    {
                        if (entry?.Override?.EnginePulse == null) continue;
                        if (MigrateEngineHighRpmFields(entry.Override.EnginePulse))
                        {
                            _carStore.Save(entry.CarId, entry.PresetName, entry.GameName,
                                           entry.Override, entry.IsBuiltin);
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info(
                    "[Trueforce] Migrated EnginePulse LoadLayer / HighRpmBoost defaults "
                    + "(Off @ 0.3 / 0.4 -> On @ 0.8 / 0.7) for presets at the old defaults.");
            }
        }

        private static bool MigrateEngineHighRpmFields(EnginePulseSettings s)
        {
            if (s == null) return false;
            bool changed = false;
            if (!s.LoadLayerEnabled && System.Math.Abs(s.LoadLayerGain - 0.3f) < 0.001f)
            {
                s.LoadLayerEnabled = true;
                s.LoadLayerGain    = 0.80f;
                changed = true;
            }
            if (!s.HighRpmBoostEnabled && System.Math.Abs(s.HighRpmBoostAmount - 0.4f) < 0.001f)
            {
                s.HighRpmBoostEnabled = true;
                s.HighRpmBoostAmount  = 0.70f;
                changed = true;
            }
            return changed;
        }

        // One-shot: bump the rev-limiter engage threshold from the old 0.97
        // default to the new 0.85 default everywhere a RevLimiterSettings is
        // persisted (active settings, game-preset snapshots, car overrides, and
        // .tfcar.json files). Only rewrites a value still at the exact old
        // default; any threshold the user moved off 0.97 is left alone. Mirrors
        // MigrateEngineHighRpmHelpersDefaults so it follows the same proven path.
        private void MigrateRevLimiterThresholdDefault()
        {
            if (Settings == null) return;
            bool changed = false;

            if (MigrateRevLimiterThresholdField(Settings.RevLimiter)) changed = true;

            if (Settings.Presets != null)
            {
                foreach (var snap in Settings.Presets.Values)
                {
                    if (snap?.RevLimiter == null) continue;
                    if (MigrateRevLimiterThresholdField(snap.RevLimiter)) changed = true;
                }
            }

            if (Settings.CarOverrides != null)
            {
                foreach (var ovr in Settings.CarOverrides.Values)
                {
                    if (ovr?.RevLimiter == null) continue;
                    if (MigrateRevLimiterThresholdField(ovr.RevLimiter)) changed = true;
                }
                // Resync _lastPersistedCarOverrides so the dirty check against
                // the live cache matches the just-migrated values (otherwise
                // every touched car would read as dirty on first load).
                if (changed)
                {
                    foreach (var kv in Settings.CarOverrides)
                    {
                        if (kv.Value == null) continue;
                        _lastPersistedCarOverrides[kv.Key] = CloneCarOverride(kv.Value);
                    }
                }
            }

            // Rewrite .tfcar.json files for any car preset still at the old
            // default so the new value persists across launches.
            if (_carStore != null)
            {
                var loaded = _carStore.LoadAll();
                foreach (var carKv in loaded)
                {
                    foreach (var entry in carKv.Value.Values)
                    {
                        if (entry?.Override?.RevLimiter == null) continue;
                        if (MigrateRevLimiterThresholdField(entry.Override.RevLimiter))
                        {
                            _carStore.Save(entry.CarId, entry.PresetName, entry.GameName,
                                           entry.Override, entry.IsBuiltin);
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info(
                    "[Trueforce] Migrated RevLimiter engage threshold default "
                    + "(0.97 -> 0.85) for presets at the old default.");
            }
        }

        private static bool MigrateRevLimiterThresholdField(RevLimiterSettings s)
        {
            if (s == null) return false;
            if (System.Math.Abs(s.Threshold - 0.97f) < 0.001f)
            {
                s.Threshold = 0.85f;
                return true;
            }
            return false;
        }

        // ---------- per-section dirty check (vs active preset) ----------

        /// <summary>True iff the current values for this section differ from
        /// the active preset's snapshot. False when there's no active preset
        /// (no anchor). Used by the UI to show/hide per-section "Save" /
        /// "Revert" buttons based on actual drift, not on a sticky flag
        /// so changing a value and changing it back clears the dirty state.</summary>
        public bool IsSectionDirty(SectionKind kind)
        {
            if (Settings == null) return false;
            bool hasGamePreset = !string.IsNullOrEmpty(_activePresetName)
                && Settings.Presets != null
                && Settings.Presets.TryGetValue(_activePresetName, out var snap)
                && snap != null;

            if (hasGamePreset)
            {
                Settings.Presets.TryGetValue(_activePresetName, out snap);
                switch (kind)
                {
                    case SectionKind.Master:         return !MasterEquals(snap);
                    case SectionKind.Ducking:        return !DuckingEquals(snap);
                    case SectionKind.Airborne:       return !AirborneEquals(snap);
                    case SectionKind.SpikeReduction: return !SpikeReductionEquals(snap);
                    case SectionKind.Audio:    return !EffectEquals(snap, EffectField.Audio);
                    case SectionKind.Engine:   return !EffectEquals(snap, EffectField.Engine);
                    case SectionKind.Bumps:    return !EffectEquals(snap, EffectField.Bumps);
                    case SectionKind.Traction: return !EffectEquals(snap, EffectField.Traction);
                    case SectionKind.Shift:    return !EffectEquals(snap, EffectField.Shift);
                    case SectionKind.Abs:        return !EffectEquals(snap, EffectField.Abs);
                    case SectionKind.PitLimiter: return !EffectEquals(snap, EffectField.PitLimiter);
                    case SectionKind.Drs:        return !EffectEquals(snap, EffectField.Drs);
                    case SectionKind.Collision:  return !EffectEquals(snap, EffectField.Collision);
                    case SectionKind.RevLimiter: return !EffectEquals(snap, EffectField.RevLimiter);
                }
                return false;
            }

            // Fallback when no game preset is active: sections with a
            // per-car override compare live override vs saved override.
            // Sections without an override (or non-per-car kinds like
            // Master / Ducking) have no anchor and return false; the UI
            // layer falls back to sticky-true via SectionHasAnchor for
            // those.
            if (!string.IsNullOrEmpty(_activeCarId) && Settings.CarOverrides != null)
            {
                Settings.CarOverrides.TryGetValue(_activeCarId, out var liveCo);
                _lastPersistedCarOverrides.TryGetValue(_activeCarId, out var savedCo);
                switch (kind)
                {
                    case SectionKind.Audio:    if (liveCo?.AudioCapture != null) return !Eq(liveCo.AudioCapture, savedCo?.AudioCapture); break;
                    case SectionKind.Engine:   if (liveCo?.EnginePulse  != null) return !Eq(liveCo.EnginePulse,  savedCo?.EnginePulse);  break;
                    case SectionKind.Bumps:    if (liveCo?.RoadBumps    != null) return !Eq(liveCo.RoadBumps,    savedCo?.RoadBumps);    break;
                    case SectionKind.Traction: if (liveCo?.TractionLoss != null) return !Eq(liveCo.TractionLoss, savedCo?.TractionLoss); break;
                    case SectionKind.Shift:    if (liveCo?.GearShift    != null) return !Eq(liveCo.GearShift,    savedCo?.GearShift);    break;
                    case SectionKind.Abs:        if (liveCo?.AbsClick     != null) return !Eq(liveCo.AbsClick,     savedCo?.AbsClick);     break;
                    case SectionKind.PitLimiter: if (liveCo?.PitLimiter   != null) return !Eq(liveCo.PitLimiter,   savedCo?.PitLimiter);   break;
                    case SectionKind.Drs:        if (liveCo?.Drs          != null) return !Eq(liveCo.Drs,          savedCo?.Drs);          break;
                    case SectionKind.Collision:  if (liveCo?.Collision    != null) return !Eq(liveCo.Collision,    savedCo?.Collision);    break;
                    case SectionKind.RevLimiter: if (liveCo?.RevLimiter   != null) return !Eq(liveCo.RevLimiter,   savedCo?.RevLimiter);   break;
                    case SectionKind.Airborne:   if (liveCo?.Airborne     != null) return !Eq(liveCo.Airborne,     savedCo?.Airborne);     break;
                }
            }
            return false;
        }

        /// <summary>True iff the section has either a game-preset snapshot
        /// or a per-car override to compare against. Used by the UI to
        /// pick between IsSectionDirty (precise) and sticky-true (fallback
        /// when no anchor exists).</summary>
        public bool SectionHasAnchor(SectionKind kind)
        {
            if (Settings == null) return false;
            bool hasGamePreset = !string.IsNullOrEmpty(_activePresetName)
                && Settings.Presets != null
                && Settings.Presets.ContainsKey(_activePresetName);
            if (hasGamePreset) return true;

            // Master / Ducking / SpikeReduction are not per-car, so without
            // a game preset they have no anchor.
            if (kind == SectionKind.Master
                || kind == SectionKind.Ducking
                || kind == SectionKind.SpikeReduction) return false;
            if (string.IsNullOrEmpty(_activeCarId) || Settings.CarOverrides == null) return false;
            if (!Settings.CarOverrides.TryGetValue(_activeCarId, out var liveCo) || liveCo == null) return false;
            switch (kind)
            {
                case SectionKind.Audio:    return liveCo.AudioCapture != null;
                case SectionKind.Engine:   return liveCo.EnginePulse  != null;
                case SectionKind.Bumps:    return liveCo.RoadBumps    != null;
                case SectionKind.Traction: return liveCo.TractionLoss != null;
                case SectionKind.Shift:    return liveCo.GearShift    != null;
                case SectionKind.Abs:        return liveCo.AbsClick     != null;
                case SectionKind.PitLimiter: return liveCo.PitLimiter   != null;
                case SectionKind.Drs:        return liveCo.Drs          != null;
                case SectionKind.Collision:  return liveCo.Collision    != null;
                case SectionKind.RevLimiter: return liveCo.RevLimiter   != null;
                case SectionKind.Airborne:   return liveCo.Airborne     != null;
            }
            return false;
        }

        private bool MasterEquals(GameSettingsSnapshot snap)
        {
            return EqF2(Settings.MasterGain,              snap.MasterGain)
                && EqF2(Settings.FfbScale,                snap.FfbScale)
                &&     Settings.FfbInvertSign          == snap.FfbInvertSign
                && EqF1(Settings.FfbSmoothTimeConstantMs, snap.FfbSmoothTimeConstantMs)
                &&     Settings.SkipFfbPassthrough     == snap.SkipFfbPassthrough;
        }

        private bool SpikeReductionEquals(GameSettingsSnapshot snap)
        {
            return Settings.FfbSpikeTamingEnabled  == snap.FfbSpikeTamingEnabled
                &&     Settings.FfbSpikeUseSlewLimiter == snap.FfbSpikeUseSlewLimiter
                && EqI(Settings.FfbSpikeMaxLsbPerMs,  snap.FfbSpikeMaxLsbPerMs)
                && EqI(Settings.FfbPeakSoftLimitLsb,  snap.FfbPeakSoftLimitLsb);
        }

        private bool DuckingEquals(GameSettingsSnapshot snap)
        {
            return EqF2(Settings.DuckDepth,    snap.DuckDepth)
                && EqI (Settings.DuckAttackMs, snap.DuckAttackMs)
                && EqI (Settings.DuckReleaseMs, snap.DuckReleaseMs);
        }

        // Airborne ducking is a global (non-per-car) section. A preset saved
        // before this effect existed has no Airborne block (snap.Airborne ==
        // null); treat that as "no opinion" and compare against the shipped
        // default so the section reads clean until the user actually changes it.
        private bool AirborneEquals(GameSettingsSnapshot snap)
        {
            var a = ActiveAirborne ?? new AirborneSettings();
            var b = snap.Airborne  ?? new AirborneSettings();
            return a.Enabled          == b.Enabled
                && EqF2(a.Reduction, b.Reduction)
                && a.DuckEngine       == b.DuckEngine
                && a.DuckAudio        == b.DuckAudio
                && a.DuckRoadBumps    == b.DuckRoadBumps
                && a.DuckTractionLoss == b.DuckTractionLoss
                && a.DuckRevLimiter   == b.DuckRevLimiter
                && a.DuckGearShift    == b.DuckGearShift
                && a.DuckAbs          == b.DuckAbs
                && a.DuckPitLimiter   == b.DuckPitLimiter
                && a.DuckDrs          == b.DuckDrs
                && a.DuckCollision    == b.DuckCollision;
        }

        // Tolerances match the UI's display precision so that two values
        // displayed as the same string (e.g. "0.07", "60", "0.0") count as
        // equal, which is what users expect when they drag a slider away
        // and back. Without these, slider-snap noise stays "dirty" forever.
        // Round both sides to the precision the UI shows, then exact-compare.
        // Distance-based epsilon was off by a factor of two at the F2 boundary
        // (two values both displayed as "0.39" can differ by up to ~0.01,
        // but the old < 0.005 tolerance treated them as unequal -- so the
        // dirty marker stayed lit after a slider returned to the same
        // visible value).
        private static bool EqF2(double a, double b) => Math.Round(a, 2) == Math.Round(b, 2);
        private static bool EqF1(double a, double b) => Math.Round(a, 1) == Math.Round(b, 1);
        private static bool EqI (double a, double b) => Math.Round(a, 0) == Math.Round(b, 0);

        private enum EffectField { Audio, Engine, Bumps, Traction, Shift, Abs, PitLimiter, Drs, Collision, RevLimiter }

        /// <summary>Scope-aware equals for dirty detection.
        ///
        /// • If the active car has a per-car override for this section, the
        ///   "saved baseline" is the per-car file's content (tracked via
        ///   _lastPersistedCarOverrides). Edits to the override since the
        ///   last "For this car" save show as dirty.
        /// • Otherwise, the saved baseline is the active preset's global
        ///   section. Edits show as dirty until "Update game defaults".</summary>
        private bool EffectEquals(GameSettingsSnapshot snap, EffectField f)
        {
            string carId = _activeCarId;
            CarOverride liveCo = null;
            CarOverride savedCo = null;
            if (carId != null)
            {
                if (Settings.CarOverrides != null) Settings.CarOverrides.TryGetValue(carId, out liveCo);
                _lastPersistedCarOverrides.TryGetValue(carId, out savedCo);
            }
            switch (f)
            {
                // For a car-overridden section the saved baseline is the saved
                // override if one exists, ELSE the game default (snap): a car
                // with no saved override "follows the game default", so a draft
                // override whose values match the default must read clean. (This
                // is what lets toggling an effect off then back on clear the
                // dirty state when the car had no prior override.)
                case EffectField.Audio:
                    if (liveCo?.AudioCapture != null) return Eq(liveCo.AudioCapture, savedCo?.AudioCapture ?? snap.AudioCapture);
                    return Eq(Settings.AudioCapture, snap.AudioCapture);
                case EffectField.Engine:
                    if (liveCo?.EnginePulse  != null) return Eq(liveCo.EnginePulse,  savedCo?.EnginePulse  ?? snap.EnginePulse);
                    return Eq(Settings.EnginePulse,  snap.EnginePulse);
                case EffectField.Bumps:
                    if (liveCo?.RoadBumps    != null) return Eq(liveCo.RoadBumps,    savedCo?.RoadBumps    ?? snap.RoadBumps);
                    return Eq(Settings.RoadBumps,    snap.RoadBumps);
                case EffectField.Traction:
                    if (liveCo?.TractionLoss != null) return Eq(liveCo.TractionLoss, savedCo?.TractionLoss ?? snap.TractionLoss);
                    return Eq(Settings.TractionLoss, snap.TractionLoss);
                case EffectField.Shift:
                    if (liveCo?.GearShift    != null) return Eq(liveCo.GearShift,    savedCo?.GearShift    ?? snap.GearShift);
                    return Eq(Settings.GearShift,    snap.GearShift);
                case EffectField.Abs:
                    if (liveCo?.AbsClick     != null) return Eq(liveCo.AbsClick,     savedCo?.AbsClick     ?? snap.AbsClick);
                    return Eq(Settings.AbsClick,     snap.AbsClick);
                case EffectField.PitLimiter:
                    if (liveCo?.PitLimiter   != null) return Eq(liveCo.PitLimiter,   savedCo?.PitLimiter   ?? snap.PitLimiter);
                    return Eq(Settings.PitLimiter,   snap.PitLimiter);
                case EffectField.Drs:
                    if (liveCo?.Drs          != null) return Eq(liveCo.Drs,          savedCo?.Drs          ?? snap.Drs);
                    return Eq(Settings.Drs,          snap.Drs);
                case EffectField.Collision:
                    if (liveCo?.Collision    != null) return Eq(liveCo.Collision,    savedCo?.Collision    ?? snap.Collision);
                    return Eq(Settings.Collision,    snap.Collision);
                case EffectField.RevLimiter:
                    if (liveCo?.RevLimiter   != null) return Eq(liveCo.RevLimiter,   savedCo?.RevLimiter   ?? snap.RevLimiter ?? new RevLimiterSettings());
                    // A preset saved before this effect existed has no
                    // RevLimiter section (snap.RevLimiter == null). Treat that
                    // as "no opinion" and compare against the shipped default,
                    // so the section reads clean (no phantom Save button)
                    // until the user actually changes it. Newer effects can't
                    // rely on every old preset JSON being regenerated.
                    return Eq(Settings.RevLimiter,   snap.RevLimiter ?? new RevLimiterSettings());
            }
            return true;
        }

        private static bool Eq(EnginePulseSettings a, EnginePulseSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,      b.Gain)
                && EqF2(a.Pitch,     b.Pitch)
                && EqI (a.LowpassHz, b.LowpassHz)
                && a.Waveform == b.Waveform
                && a.ElectricMode == b.ElectricMode
                && a.Layout == b.Layout
                && string.Equals(a.CustomEngineId ?? "", b.CustomEngineId ?? "", System.StringComparison.Ordinal)
                && string.Equals(a.CustomFiringPattern ?? "", b.CustomFiringPattern ?? "", System.StringComparison.Ordinal)
                && string.Equals(a.CustomFiringPatternName ?? "", b.CustomFiringPatternName ?? "", System.StringComparison.Ordinal)
                && a.LoadLayerEnabled    == b.LoadLayerEnabled
                && EqF2(a.LoadLayerGain,      b.LoadLayerGain)
                && a.HighRpmBoostEnabled == b.HighRpmBoostEnabled
                && EqF2(a.HighRpmBoostAmount, b.HighRpmBoostAmount);
        }
        private static bool Eq(RoadBumpsSettings a, RoadBumpsSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain, b.Gain)
                && EqI (a.Freq, b.Freq)
                && a.Waveform == b.Waveform
                && a.SurfaceEnabled == b.SurfaceEnabled
                && EqF2(a.SurfaceGain, b.SurfaceGain)
                && EqI (a.SurfaceFreq, b.SurfaceFreq)
                && a.SurfaceWaveform == b.SurfaceWaveform
                && EqI (a.SurfaceLowpassHz,  b.SurfaceLowpassHz)
                && EqI (a.SurfaceHighpassHz, b.SurfaceHighpassHz)
                && EqF2(a.SurfaceRumbleScale, b.SurfaceRumbleScale)
                && EqF2(a.RumbleStripPulseAmp, b.RumbleStripPulseAmp)
                && a.RumbleStripPulseMs == b.RumbleStripPulseMs;
        }
        private static bool Eq(TractionLossSettings a, TractionLossSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,            b.Gain)
                && EqF2(a.Sensitivity,     b.Sensitivity)
                && EqI (a.Freq,            b.Freq)
                && EqI (a.NoiseLowpassHz,  b.NoiseLowpassHz)
                && EqI (a.NoiseHighpassHz, b.NoiseHighpassHz)
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(GearShiftSettings a, GearShiftSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain, b.Gain)
                && EqI (a.Freq, b.Freq)
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(AbsClickSettings a, AbsClickSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,           b.Gain)
                && EqI (a.Freq,           b.Freq)
                && EqF1(a.PulseFreq,      b.PulseFreq)
                && EqF2(a.DutyCycle,      b.DutyCycle)
                && EqI (a.TickDurationMs, b.TickDurationMs)
                && a.Mode == b.Mode && a.Waveform == b.Waveform;
        }
        private static bool Eq(PitLimiterSettings a, PitLimiterSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,      b.Gain)
                && EqI (a.Freq,      b.Freq)
                && EqF1(a.PulseFreq, b.PulseFreq)
                && EqF2(a.DutyCycle, b.DutyCycle)
                && EqF2(a.ActiveAmp, b.ActiveAmp)
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(DrsSettings a, DrsSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,           b.Gain)
                && EqI (a.ActivationFreq, b.ActivationFreq)
                && a.ActivationMs == b.ActivationMs
                && EqF2(a.ActivationAmp,  b.ActivationAmp)
                && EqI (a.SustainedFreq,  b.SustainedFreq)
                && EqF2(a.SustainedAmp,   b.SustainedAmp)
                && a.Waveform          == b.Waveform
                && a.SustainedWaveform == b.SustainedWaveform;
        }
        private static bool Eq(CollisionSettings a, CollisionSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,               b.Gain)
                && EqI (a.Freq,               b.Freq)
                && a.EnvelopeMs == b.EnvelopeMs
                && EqF2(a.MinThreshold,       b.MinThreshold)
                && EqF2(a.MinAmp,             b.MinAmp)
                && EqF2(a.MaxAmp,             b.MaxAmp)
                && EqF2(a.NormalizationScale, b.NormalizationScale)
                && a.RefractoryMs == b.RefractoryMs
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(RevLimiterSettings a, RevLimiterSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,      b.Gain)
                && EqI (a.Freq,      b.Freq)
                && EqF1(a.PulseFreq, b.PulseFreq)
                && EqF2(a.DutyCycle, b.DutyCycle)
                && EqF2(a.ActiveAmp, b.ActiveAmp)
                && EqF2(a.Threshold, b.Threshold)
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(AudioCaptureSettings a, AudioCaptureSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,             b.Gain)
                && EqI (a.LowpassCutoffHz,  b.LowpassCutoffHz)
                && EqI (a.HighpassCutoffHz, b.HighpassCutoffHz);
        }
        private static bool Eq(AirborneSettings a, AirborneSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled          == b.Enabled
                && EqF2(a.Reduction, b.Reduction)
                && a.DuckEngine       == b.DuckEngine
                && a.DuckAudio        == b.DuckAudio
                && a.DuckRoadBumps    == b.DuckRoadBumps
                && a.DuckTractionLoss == b.DuckTractionLoss
                && a.DuckRevLimiter   == b.DuckRevLimiter
                && a.DuckGearShift    == b.DuckGearShift
                && a.DuckAbs          == b.DuckAbs
                && a.DuckPitLimiter   == b.DuckPitLimiter
                && a.DuckDrs          == b.DuckDrs
                && a.DuckCollision    == b.DuckCollision;
        }

        // ---------- per-section revert (from active preset) ----------

        /// <summary>Section identifier used by <see cref="RevertSection"/>.
        /// Mirrors the per-section "Save…" / "Revert" buttons in the UI:
        /// Master and Ducking are global-only; the rest have a per-car
        /// override component that revert respects.</summary>
        public enum SectionKind { Master, Ducking, Audio, Engine, Bumps, Traction, Shift, Abs, SpikeReduction, PitLimiter, Drs, Collision, RevLimiter, Airborne }

        /// <summary>Revert one section to the active preset's saved snapshot.
        /// Scope-aware: if the snapshot has a per-car override for the
        /// current car, that override is restored; otherwise the override is
        /// dropped and the global section is restored. No-op when there's
        /// no active preset (nothing to revert to).</summary>
        public bool RevertSection(SectionKind kind)
        {
            if (Settings == null || string.IsNullOrEmpty(_activePresetName)) return false;
            if (Settings.Presets == null || !Settings.Presets.TryGetValue(_activePresetName, out var snap) || snap == null) return false;

            switch (kind)
            {
                case SectionKind.Master:
                    Settings.MasterGain              = snap.MasterGain;
                    Settings.FfbScale                = snap.FfbScale;
                    Settings.FfbInvertSign           = snap.FfbInvertSign;
                    Settings.FfbSmoothTimeConstantMs = snap.FfbSmoothTimeConstantMs;
                    Settings.SkipFfbPassthrough      = snap.SkipFfbPassthrough;
                    _mixer.MasterGain = Settings.MasterGain;
                    if (_device != null)
                    {
                        _device.FfbScale                = Settings.FfbScale;
                        _device.FfbInvertSign           = Settings.FfbInvertSign;
                        _device.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
                    }
                    return true;

                case SectionKind.SpikeReduction:
                    Settings.FfbSpikeTamingEnabled  = snap.FfbSpikeTamingEnabled;
                    Settings.FfbSpikeUseSlewLimiter = snap.FfbSpikeUseSlewLimiter;
                    Settings.FfbSpikeMaxLsbPerMs    = snap.FfbSpikeMaxLsbPerMs;
                    Settings.FfbPeakSoftLimitLsb    = snap.FfbPeakSoftLimitLsb;
                    if (_device != null)
                    {
                        _device.FfbSpikeTamingEnabled  = Settings.FfbSpikeTamingEnabled;
                        _device.FfbSpikeUseSlewLimiter = Settings.FfbSpikeUseSlewLimiter;
                        _device.FfbSpikeMaxLsbPerMs    = Settings.FfbSpikeMaxLsbPerMs;
                        _device.FfbPeakSoftLimitLsb    = Settings.FfbPeakSoftLimitLsb;
                    }
                    return true;

                case SectionKind.Ducking:
                    Settings.DuckDepth     = snap.DuckDepth;
                    Settings.DuckAttackMs  = snap.DuckAttackMs;
                    Settings.DuckReleaseMs = snap.DuckReleaseMs;
                    return true;

                case SectionKind.Airborne:
                    // Global section. Restore from the preset's saved block, or
                    // the shipped default when the preset predates the effect.
                    Settings.Airborne = Clone(snap.Airborne ?? new AirborneSettings());
                    ApplyAirborneSettings();
                    return true;

                case SectionKind.Engine:
                    RevertEffectScopeAware(
                        snap.EnginePulse,
                        snap.CarOverrides,
                        co => co?.EnginePulse,
                        s => Settings.EnginePulse = Clone(s),
                        (co, v) => co.EnginePulse = Clone(v),
                        co => co.EnginePulse = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Bumps:
                    RevertEffectScopeAware(
                        snap.RoadBumps,
                        snap.CarOverrides,
                        co => co?.RoadBumps,
                        s => Settings.RoadBumps = Clone(s),
                        (co, v) => co.RoadBumps = Clone(v),
                        co => co.RoadBumps = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Traction:
                    RevertEffectScopeAware(
                        snap.TractionLoss,
                        snap.CarOverrides,
                        co => co?.TractionLoss,
                        s => Settings.TractionLoss = Clone(s),
                        (co, v) => co.TractionLoss = Clone(v),
                        co => co.TractionLoss = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Shift:
                    RevertEffectScopeAware(
                        snap.GearShift,
                        snap.CarOverrides,
                        co => co?.GearShift,
                        s => Settings.GearShift = Clone(s),
                        (co, v) => co.GearShift = Clone(v),
                        co => co.GearShift = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Abs:
                    RevertEffectScopeAware(
                        snap.AbsClick,
                        snap.CarOverrides,
                        co => co?.AbsClick,
                        s => Settings.AbsClick = Clone(s),
                        (co, v) => co.AbsClick = Clone(v),
                        co => co.AbsClick = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.PitLimiter:
                    RevertEffectScopeAware(
                        snap.PitLimiter,
                        snap.CarOverrides,
                        co => co?.PitLimiter,
                        s => Settings.PitLimiter = Clone(s),
                        (co, v) => co.PitLimiter = Clone(v),
                        co => co.PitLimiter = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Drs:
                    RevertEffectScopeAware(
                        snap.Drs,
                        snap.CarOverrides,
                        co => co?.Drs,
                        s => Settings.Drs = Clone(s),
                        (co, v) => co.Drs = Clone(v),
                        co => co.Drs = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Collision:
                    RevertEffectScopeAware(
                        snap.Collision,
                        snap.CarOverrides,
                        co => co?.Collision,
                        s => Settings.Collision = Clone(s),
                        (co, v) => co.Collision = Clone(v),
                        co => co.Collision = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.RevLimiter:
                    RevertEffectScopeAware(
                        snap.RevLimiter,
                        snap.CarOverrides,
                        co => co?.RevLimiter,
                        s => Settings.RevLimiter = Clone(s),
                        (co, v) => co.RevLimiter = Clone(v),
                        co => co.RevLimiter = null);
                    ApplyActiveCarOverride();
                    return true;

                case SectionKind.Audio:
                    RevertEffectScopeAware(
                        snap.AudioCapture,
                        snap.CarOverrides,
                        co => co?.AudioCapture,
                        s => Settings.AudioCapture = CloneOrNull(s),
                        (co, v) => co.AudioCapture = CloneOrNull(v),
                        co => co.AudioCapture = null);
                    ApplyActiveCarOverride();
                    if (_audio != null && Settings.AudioCapture != null)
                    {
                        _audio.Enabled          = Settings.AudioCapture.Enabled;
                        _audio.Gain             = Settings.AudioCapture.Gain;
                        _audio.LowpassCutoffHz  = Settings.AudioCapture.LowpassCutoffHz;
                        _audio.HighpassCutoffHz = Settings.AudioCapture.HighpassCutoffHz;
                    }
                    return true;
            }
            return false;
        }

        /// <summary>Generic per-effect revert. Scope-aware:
        ///   * If the live car override has this section, the user is
        ///     editing in car-preset scope; restore from the on-disk car
        ///     preset (cached as _lastPersistedCarOverrides). If the saved
        ///     car preset didn't include this section, drop the override
        ///     (section falls back to global).
        ///   * Otherwise, restore the global section from the active
        ///     game-preset snapshot.
        ///   * The legacy snap.CarOverrides path is kept for old presets
        ///     that still carry per-car data; modern (Model G) presets
        ///     have snap.CarOverrides == null and only the live-override
        ///     branch fires.
        /// Caller is responsible for pushing the resulting state live
        /// (ApplyActiveCarOverride etc.).</summary>
        private void RevertEffectScopeAware<TSection>(
            TSection snapGlobal,
            Dictionary<string, CarOverride> snapOverrides,
            Func<CarOverride, TSection> getSnapCarSection,
            Action<TSection> applyToGlobal,
            Action<CarOverride, TSection> applyToCarOverride,
            Action<CarOverride> clearCarOverride) where TSection : class
        {
            string carId = _activeCarId;

            // Live car-preset override path: dirty came from car-preset
            // edits, so revert restores the saved car preset's section.
            if (carId != null && Settings.CarOverrides != null
                && Settings.CarOverrides.TryGetValue(carId, out var liveCo) && liveCo != null
                && getSnapCarSection(liveCo) != null)
            {
                _lastPersistedCarOverrides.TryGetValue(carId, out var savedCo);
                var savedCarSection = savedCo != null ? getSnapCarSection(savedCo) : null;
                if (savedCarSection != null)
                {
                    applyToCarOverride(liveCo, savedCarSection);
                }
                else
                {
                    // Section wasn't in the saved car preset; drop the
                    // override so the section falls back to the game-preset
                    // global (restored below).
                    clearCarOverride(liveCo);
                    if (snapGlobal != null) applyToGlobal(snapGlobal);
                }
                return;
            }

            // Legacy snap.CarOverrides path (pre-Model-G presets).
            CarOverride snapCar = null;
            if (carId != null && snapOverrides != null) snapOverrides.TryGetValue(carId, out snapCar);
            var snapCarSection = snapCar != null ? getSnapCarSection(snapCar) : null;
            if (snapCarSection != null && carId != null)
            {
                if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();
                if (!Settings.CarOverrides.TryGetValue(carId, out liveCo) || liveCo == null)
                    Settings.CarOverrides[carId] = liveCo = new CarOverride();
                applyToCarOverride(liveCo, snapCarSection);
                return;
            }

            // Plain global revert: no per-car scope involved.
            if (snapGlobal != null) applyToGlobal(snapGlobal);
        }

        /// <summary>Apply a named preset from the library. Sets it as the
        /// currently-active preset. No game default is changed.</summary>
        /// <returns>true if applied; false if the preset name doesn't exist.</returns>
        public bool ApplyPreset(string presetName)
        {
            if (Settings?.Presets == null || string.IsNullOrEmpty(presetName)) return false;
            if (!Settings.Presets.TryGetValue(presetName, out var snap) || snap == null) return false;
            ApplyGamePreset(snap);
            _activePresetName = presetName;
            SimHub.Logging.Current.Info($"[Trueforce] Applied preset '{presetName}'.");
            return true;
        }

        /// <summary>Snapshot the current settings into the library under the
        /// given name. Overwrites any existing preset with that name. Sets it
        /// as the active preset. Refuses to overwrite built-in presets, the
        /// UI must fork to a user-named preset for those.</summary>
        public void SavePresetAs(string presetName)
        {
            if (Settings == null || string.IsNullOrEmpty(presetName)) return;
            // Non-dev: built-ins are read-only (caller forks). DEV authoring
            // mode may overwrite a built-in in place; user presets always save.
            if (IsBuiltinPreset(presetName) && !DevMode)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to overwrite built-in preset '{presetName}'.");
                return;
            }
            PersistGamePresetToFolder(presetName, SnapshotCurrentAsPreset());
            _activePresetName = presetName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Saved preset '{presetName}'.");
        }

        /// <summary>Save ONLY the targeted section into the active preset's
        /// in-memory snapshot, leaving every other section untouched on disk.
        /// Inverse of <see cref="RevertSection"/>. Returns false when the
        /// active preset is missing or built-in (built-ins can't be
        /// overwritten in place; caller forks instead). Caller is
        /// responsible for clearing the section's dirty bit + refreshing
        /// the UI.</summary>
        public bool SaveSectionToActivePreset(SectionKind kind)
        {
            if (Settings?.Presets == null) return false;
            if (string.IsNullOrEmpty(_activePresetName)) return false;
            if (IsBuiltinPreset(_activePresetName) && !DevMode) return false; // DEV may edit built-ins in place
            if (!Settings.Presets.TryGetValue(_activePresetName, out var snap) || snap == null) return false;

            switch (kind)
            {
                case SectionKind.Master:
                    snap.MasterGain              = Settings.MasterGain;
                    snap.FfbScale                = Settings.FfbScale;
                    snap.FfbInvertSign           = Settings.FfbInvertSign;
                    snap.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
                    snap.SkipFfbPassthrough      = Settings.SkipFfbPassthrough;
                    break;
                case SectionKind.SpikeReduction:
                    snap.FfbSpikeTamingEnabled  = Settings.FfbSpikeTamingEnabled;
                    snap.FfbSpikeUseSlewLimiter = Settings.FfbSpikeUseSlewLimiter;
                    snap.FfbSpikeMaxLsbPerMs    = Settings.FfbSpikeMaxLsbPerMs;
                    snap.FfbPeakSoftLimitLsb    = Settings.FfbPeakSoftLimitLsb;
                    break;
                case SectionKind.Ducking:
                    snap.DuckDepth     = Settings.DuckDepth;
                    snap.DuckAttackMs  = Settings.DuckAttackMs;
                    snap.DuckReleaseMs = Settings.DuckReleaseMs;
                    break;
                case SectionKind.Airborne:
                    snap.Airborne = Clone(Settings.Airborne);
                    break;
                case SectionKind.Engine:     snap.EnginePulse  = Clone(Settings.EnginePulse);     break;
                case SectionKind.Bumps:      snap.RoadBumps    = Clone(Settings.RoadBumps);       break;
                case SectionKind.Traction:   snap.TractionLoss = Clone(Settings.TractionLoss);    break;
                case SectionKind.Shift:      snap.GearShift    = Clone(Settings.GearShift);       break;
                case SectionKind.Abs:        snap.AbsClick     = Clone(Settings.AbsClick);        break;
                case SectionKind.PitLimiter: snap.PitLimiter   = Clone(Settings.PitLimiter);      break;
                case SectionKind.Drs:        snap.Drs          = Clone(Settings.Drs);             break;
                case SectionKind.Collision:  snap.Collision    = Clone(Settings.Collision);       break;
                case SectionKind.RevLimiter: snap.RevLimiter   = Clone(Settings.RevLimiter);      break;
                case SectionKind.Audio:      snap.AudioCapture = CloneOrNull(Settings.AudioCapture); break;
                default: return false;
            }
            // Clear the seeded-preset sentinel on first edit so the row
            // becomes the user's work the moment they touch a slider.
            // (Without this clear, the PackName sentinel would block the
            // attribution stamp below and the next backfill prompt.)
            if (string.Equals(NullIfBlank(snap.PackName), SeededPresetSentinel, StringComparison.Ordinal))
                snap.PackName = null;
            // Attribute the section edit to the current author when the
            // preset doesn't already carry attribution (community packs +
            // already-stamped local saves keep theirs).
            if (NullIfBlank(snap.Author) == null && NullIfBlank(snap.PackName) == null)
                snap.Author = CurrentAuthorForStamp();
            // Persist the whole preset to its file (user library or, for DEV
            // editing a built-in, the built-in folder). Then save plugin
            // settings (the legacy Presets dict ends up empty after rebuild,
            // which is the new normal).
            PersistGamePresetToFolder(_activePresetName, snap);
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Saved {kind} into preset '{_activePresetName}' (scoped).");
            return true;
        }

        /// <summary>Save ONLY the targeted section into the active car's
        /// preset file on disk. Patches the in-memory "last persisted" car
        /// override snapshot for this section (using the live value),
        /// writes the patched override out, and updates the persisted
        /// snapshot. Other sections in the car preset file stay at their
        /// previously-saved values. Returns false on built-ins (forks
        /// instead) or when there's no active car / car preset.</summary>
        public bool SaveSectionToActiveCarOverride(SectionKind kind)
        {
            if (_carStore == null || string.IsNullOrEmpty(_activeCarId)) return false;
            string presetName = GetActiveCarPresetName(_activeCarId);
            if (string.IsNullOrEmpty(presetName)) return false;
            bool isBuiltin = IsCarPresetBuiltin(_activeCarId, presetName);
            if (isBuiltin && !DevMode) return false;   // non-dev: caller forks
            if (Settings?.CarOverrides == null) return false;
            if (!Settings.CarOverrides.TryGetValue(_activeCarId, out var live) || live == null) return false;

            // Build the to-be-persisted override by starting from whatever's
            // currently on disk (cached in _lastPersistedCarOverrides) and
            // patching in just the targeted section from live.
            CarOverride patched;
            if (_lastPersistedCarOverrides.TryGetValue(_activeCarId, out var prev) && prev != null)
                patched = CloneCarOverride(prev);
            else
                patched = new CarOverride();

            switch (kind)
            {
                case SectionKind.Engine:     patched.EnginePulse  = live.EnginePulse  != null ? Clone(live.EnginePulse)  : null; break;
                case SectionKind.Bumps:      patched.RoadBumps    = live.RoadBumps    != null ? Clone(live.RoadBumps)    : null; break;
                case SectionKind.Traction:   patched.TractionLoss = live.TractionLoss != null ? Clone(live.TractionLoss) : null; break;
                case SectionKind.Shift:      patched.GearShift    = live.GearShift    != null ? Clone(live.GearShift)    : null; break;
                case SectionKind.Abs:        patched.AbsClick     = live.AbsClick     != null ? Clone(live.AbsClick)     : null; break;
                case SectionKind.PitLimiter: patched.PitLimiter   = live.PitLimiter   != null ? Clone(live.PitLimiter)   : null; break;
                case SectionKind.Drs:        patched.Drs          = live.Drs          != null ? Clone(live.Drs)          : null; break;
                case SectionKind.Collision:  patched.Collision    = live.Collision    != null ? Clone(live.Collision)    : null; break;
                case SectionKind.RevLimiter: patched.RevLimiter   = live.RevLimiter   != null ? Clone(live.RevLimiter)   : null; break;
                case SectionKind.Audio:      patched.AudioCapture = CloneOrNull(live.AudioCapture); break;
                case SectionKind.Airborne:   patched.Airborne     = live.Airborne     != null ? Clone(live.Airborne)     : null; break;
                default: return false;
            }

            if (isBuiltin)
            {
                // DEV: write through to the factory folder. LoadAndMigrateCarPresets
                // (called inside the helper) refreshes the live + persisted caches.
                WriteCarBuiltinThroughDev(_activeCarId, presetName, patched);
            }
            else
            {
                _carStore.Save(_activeCarId, presetName, _activeGame ?? "", patched, isBuiltin: false,
                    defaultAuthor: CurrentAuthorForStamp());
                if (patched.IsEmpty)
                    _lastPersistedCarOverrides.Remove(_activeCarId);
                else
                    _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(patched);
            }
            SimHub.Logging.Current.Info($"[Trueforce] Saved {kind} into car preset '{presetName}' for '{_activeCarId}' (scoped).");
            return true;
        }

        /// <summary>Delete a preset from the library. Also clears any
        /// GameDefaults entries that pointed to this preset. Refuses on
        /// built-in presets, they're factory defaults the user can always
        /// fall back to.</summary>
        public bool DeletePreset(string presetName)
        {
            if (Settings?.Presets == null || string.IsNullOrEmpty(presetName)) return false;
            bool wasBuiltin = IsBuiltinPreset(presetName);
            // Non-dev: built-ins are protected. DEV authoring may delete them
            // (also removes the folder file + its default bindings below).
            if (wasBuiltin && !DevMode)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to delete built-in preset '{presetName}'.");
                return false;
            }

            // Delete the preset file from its source folder + drop any defaults
            // (in that folder) that pointed at the now-deleted preset.
            try
            {
                string folder = wasBuiltin ? BuiltinPresets.CurrentFolder : UserPresets.CurrentFolder;
                BuiltinPresetWriter.DeleteGame(folder, presetName);
                // Remove default bindings that referenced this preset in the
                // folder. Defaults live in user library by default; if the
                // built-in folder has a default to this name, drop that too.
                foreach (var src in new[] { UserPresets.GameDefaults, wasBuiltin ? BuiltinPresets.GameDefaultBindings : null })
                {
                    if (src == null) continue;
                    var hits = new List<string>();
                    foreach (var kv in src)
                        if (kv.Value == presetName) hits.Add(kv.Key);
                    string defFolder = src == UserPresets.GameDefaults ? UserPresets.CurrentFolder : BuiltinPresets.CurrentFolder;
                    foreach (var k in hits) BuiltinPresetWriter.RemoveGameDefault(defFolder, k);
                }
                if (wasBuiltin) BuiltinPresets.Reload(); else UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Delete '{presetName}' failed: {ex.Message}");
                return false;
            }

            if (_activePresetName == presetName) _activePresetName = null;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Deleted preset '{presetName}'.");
            return true;
        }

        /// <summary>Bind a preset as the auto-load default for the active game.
        /// Subsequent game changes into this game will apply the preset.</summary>
        public void SetDefaultPresetForActiveGame(string presetName)
        {
            if (string.IsNullOrEmpty(_activeGame)) return;
            SetDefaultPresetForGame(_activeGame, presetName);
        }

        /// <summary>Remove the auto-load binding for the active game.</summary>
        public void ClearDefaultPresetForActiveGame()
        {
            if (string.IsNullOrEmpty(_activeGame)) return;
            ClearDefaultPresetForGame(_activeGame);
        }

        /// <summary>Rename a game preset in the library. Updates any
        /// GameDefaults entries that pointed to the old name and the
        /// ActivePresetName if it was the renamed one. Refuses on built-ins
        /// (factory names are part of the brand) and when the target name
        /// already exists. Used by the preset manager.</summary>
        public bool RenamePreset(string oldName, string newName)
        {
            if (Settings?.Presets == null) return false;
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;
            bool wasBuiltin = IsBuiltinPreset(oldName);
            // Non-dev: built-in names are part of the brand. DEV authoring may
            // rename them (renames the folder file + repoints defaults below).
            if (wasBuiltin && !DevMode)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to rename built-in preset '{oldName}'.");
                return false;
            }
            if (!Settings.Presets.TryGetValue(oldName, out var snap) || snap == null) return false;
            if (Settings.Presets.ContainsKey(newName)) return false;

            // Rename the file in the right folder; RenameGame also repoints any
            // game-defaults entries in that folder from old -> new.
            try
            {
                string folder = wasBuiltin ? BuiltinPresets.CurrentFolder : UserPresets.CurrentFolder;
                BuiltinPresetWriter.RenameGame(folder, oldName, newName);
                if (wasBuiltin) BuiltinPresets.Reload(); else UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Rename '{oldName}' -> '{newName}' failed: {ex.Message}");
                return false;
            }

            if (_activePresetName == oldName) _activePresetName = newName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Renamed preset '{oldName}' to '{newName}'.");
            return true;
        }

        /// <summary>Deep-copy a preset under a new name. JSON round-trip so the
        /// clone is independent of the source. Refuses if the target already
        /// exists in the library.</summary>
        public bool DuplicatePreset(string sourceName, string newName)
        {
            if (Settings?.Presets == null) return false;
            if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(newName)) return false;
            if (!Settings.Presets.TryGetValue(sourceName, out var snap) || snap == null) return false;
            if (Settings.Presets.ContainsKey(newName)) return false;

            // The duplicate always lands in the USER library (a copy of a
            // built-in becomes a normal user preset; the owner uses DEV +
            // Export-as-built-in to promote one). Stamp the duplicator as
            // the author when the source had none (the user IS the author
            // of this divergent copy); preserve existing attribution so
            // forks of a community pack stay linked to their origin.
            if (NullIfBlank(snap.Author) == null && NullIfBlank(snap.PackName) == null)
            {
                snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(snap));
                snap.Author = CurrentAuthorForStamp();
            }
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(snap, Newtonsoft.Json.Formatting.Indented);
            try
            {
                BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, newName, json);
                UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Duplicate '{sourceName}' -> '{newName}' failed: {ex.Message}");
                return false;
            }
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Duplicated preset '{sourceName}' as '{newName}'.");
            return true;
        }

        /// <summary>Bind a preset to auto-load for any game (not just the
        /// active one). Used by the preset manager's per-row Set
        /// default action. Returns false if the named preset isn't in the
        /// library.</summary>
        public bool SetDefaultPresetForGame(string gameName, string presetName)
        {
            if (Settings == null) return false;
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(presetName)) return false;
            if (Settings.Presets == null || !Settings.Presets.ContainsKey(presetName)) return false;
            try
            {
                // DEV authoring writes the BUILT-IN folder's defaults map (the
                // factory baseline); regular users write the USER library's
                // (their personal choice that overrides the built-in seed).
                string folder = DevMode ? BuiltinPresets.CurrentFolder : UserPresets.CurrentFolder;
                BuiltinPresetWriter.SetGameDefault(folder, gameName, presetName);
                if (DevMode) BuiltinPresets.Reload(); else UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Set game default for '{gameName}' failed: {ex.Message}");
                return false;
            }
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] '{presetName}' set as default for '{gameName}'.");
            return true;
        }

        /// <summary>Drop the auto-load binding for a specific game.</summary>
        public bool ClearDefaultPresetForGame(string gameName)
        {
            if (Settings?.GameDefaults == null || string.IsNullOrEmpty(gameName)) return false;
            try
            {
                string folder = DevMode ? BuiltinPresets.CurrentFolder : UserPresets.CurrentFolder;
                BuiltinPresetWriter.RemoveGameDefault(folder, gameName);
                if (DevMode) BuiltinPresets.Reload(); else UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Clear game default for '{gameName}' failed: {ex.Message}");
                return false;
            }
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Cleared default preset for '{gameName}'.");
            return true;
        }

        /// <summary>Enter offline edit mode for a named preset. Snapshots the
        /// current live settings (so Discard can restore them) then applies
        /// the preset so the user can tweak its sections in the regular UI.
        /// Returns false if the preset doesn't exist. Callers (the
        /// SettingsControl) update their banner UI on success.</summary>
        public bool EnterOfflineEdit(string presetName)
        {
            if (Settings?.Presets == null || string.IsNullOrEmpty(presetName)) return false;
            if (!Settings.Presets.TryGetValue(presetName, out var snap) || snap == null) return false;
            _preEditSnapshot = SnapshotCurrentAsPreset();
            _preEditActivePresetName = _activePresetName;
            ApplyGamePreset(snap);
            _activePresetName = presetName;
            _offlineEditPresetName = presetName;
            SimHub.Logging.Current.Info($"[Trueforce] Offline edit mode: editing preset '{presetName}'.");
            return true;
        }

        /// <summary>Exit offline edit mode by writing the in-memory edits
        /// back into the preset being edited. Built-ins refuse in-place
        /// overwrite, caller falls back to <see cref="ExitOfflineEditSaveAs"/>
        /// for those. Returns false on the built-in case so the caller can
        /// prompt for a new name.</summary>
        public bool ExitOfflineEditSave()
        {
            if (!IsOfflineEditing) return true;
            string name = _offlineEditPresetName;
            // Non-dev: built-ins fork (caller prompts for a new name). DEV
            // authoring mode overwrites the built-in in place (SavePresetAs
            // write-through updates the folder file too).
            if (IsBuiltinPreset(name) && !DevMode)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Can't overwrite built-in preset '{name}' on edit-mode save; fork via Save as new.");
                return false;
            }
            SavePresetAs(name);            // persist edits to the edited preset
            RestorePreEditGameState();     // return to the previously-active preset
            SimHub.Logging.Current.Info($"[Trueforce] Saved '{name}' and exited offline edit (restored prior preset).");
            return true;
        }

        /// <summary>Exit offline edit mode by saving the in-memory edits as
        /// a brand-new preset (forking off a built-in), then returning to the
        /// previously-active preset.</summary>
        public bool ExitOfflineEditSaveAs(string newName)
        {
            if (!IsOfflineEditing || string.IsNullOrEmpty(newName)) return false;
            if (Settings.Presets != null && Settings.Presets.ContainsKey(newName)) return false;
            SavePresetAs(newName);
            RestorePreEditGameState();
            SimHub.Logging.Current.Info($"[Trueforce] Saved as new '{newName}' and exited offline edit (restored prior preset).");
            return true;
        }

        /// <summary>Exit offline edit mode and restore the previously-active
        /// preset (dropping any unsaved edits). Used by the banner's Done (when
        /// nothing's unsaved) and the discard path.</summary>
        public void ExitOfflineEditDiscard()
        {
            if (!IsOfflineEditing) return;
            string was = _offlineEditPresetName;
            RestorePreEditGameState();
            SimHub.Logging.Current.Info($"[Trueforce] Exited offline edit mode (dropped unsaved edits to '{was}').");
        }

        // Re-apply the preset that was active before editing began, by name from
        // the library. Handles both cases correctly: if you edited your active
        // preset, re-loading it picks up its now-saved values (after Save) or the
        // unchanged saved values (after a no-save exit, reverting unsaved tweaks);
        // if you edited a different preset, this returns you to the one you were
        // running. Falls back to the pre-edit live snapshot if the prior preset
        // is gone.
        private void RestorePreEditGameState()
        {
            string prior = _preEditActivePresetName;
            _offlineEditPresetName = null;
            if (!string.IsNullOrEmpty(prior) && Settings?.Presets != null
                && Settings.Presets.TryGetValue(prior, out var snap) && snap != null)
            {
                ApplyGamePreset(snap);
                _activePresetName = prior;
            }
            else if (_preEditSnapshot != null)
            {
                ApplyGamePreset(_preEditSnapshot);
                _activePresetName = prior;
            }
            _preEditSnapshot = null;
            _preEditActivePresetName = null;
        }

        // ---------- Car-preset offline edit ----------

        private string GetCarPresetGame(string carId, string presetName)
        {
            var presets = GetCarPresets(carId);
            return presets != null && presets.TryGetValue(presetName, out var e) ? e?.GameName : null;
        }

        /// <summary>Enter offline edit for one car preset. Snapshots the live
        /// state, loads that car's GAME default preset as the baseline (so the
        /// override sits on the right globals and nothing reads as spuriously
        /// dirty), then pins the car and loads its override. The car is frozen
        /// against live telemetry until exit (see DataUpdate). Returns false if
        /// the inputs are empty.</summary>
        public bool EnterOfflineEditCar(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;

            _preEditCarSnapshot         = SnapshotCurrentAsPreset();
            _preEditCarActiveId         = _activeCarId;
            _preEditCarActivePresetName = _activePresetName;

            // Baseline: the default game preset for the car's own game, so the
            // override compares against the right globals (fixes the "wrong
            // preset -> everything dirty" case when the running game differs).
            string carGame = GetCarPresetGame(carId, presetName);
            if (!string.IsNullOrEmpty(carGame)
                && Settings?.GameDefaults != null
                && Settings.GameDefaults.TryGetValue(carGame, out var gp)
                && !string.IsNullOrEmpty(gp)
                && Settings.Presets != null
                && Settings.Presets.TryGetValue(gp, out var gsnap) && gsnap != null)
            {
                ApplyGamePreset(gsnap);
                _activePresetName = gp;
            }

            if (!SelectCarForEditing(carId, presetName)) return false;
            _offlineEditCarId         = carId;
            _offlineEditCarPresetName = presetName;
            SimHub.Logging.Current.Info($"[Trueforce] Offline edit mode: editing car preset '{presetName}' for '{carId}' (baseline game '{carGame}').");
            return true;
        }

        // Restore the live state captured when car-edit began, then unfreeze so
        // telemetry re-asserts the real car. Shared by all three exits.
        private void RestorePreEditCarState()
        {
            _offlineEditCarId         = null;   // unfreeze first
            _offlineEditCarPresetName = null;
            if (_preEditCarSnapshot != null) ApplyGamePreset(_preEditCarSnapshot);
            _activePresetName = _preEditCarActivePresetName;
            _activeCarId      = _preEditCarActiveId;
            _preEditCarSnapshot         = null;
            _preEditCarActiveId         = null;
            _preEditCarActivePresetName = null;
        }

        // DEV authoring: write the frozen car's edited override through to the
        // built-in folder as a CarPresetFile (IsBuiltin=true), reload, and push
        // the folder built-ins back into the live car store so the edit is
        // active. Source of truth stays the folder.
        private void WriteCarBuiltinThroughDev(string carId, string presetName)
        {
            if (Settings?.CarOverrides == null
                || !Settings.CarOverrides.TryGetValue(carId, out var ov) || ov == null) return;
            WriteCarBuiltinThroughDev(carId, presetName, ov);
        }

        // DEV authoring overload: write an explicit override through to the
        // built-in folder. Used by section-scoped saves where the value being
        // persisted is patched (live override + one section replacement), not
        // the live override as-is.
        private void WriteCarBuiltinThroughDev(string carId, string presetName, CarOverride ov)
        {
            try
            {
                if (ov == null) return;
                // Factory file uses on-disk name (no display suffix).
                string diskName = ToDiskName(presetName);
                string game = GetCarPresetGame(carId, presetName) ?? _activeGame ?? "";
                var file = new CarPresetFile
                {
                    Type = CarPresetFile.FileType, Version = 2,
                    GameName = game, CarId = carId,
                    PresetName = diskName, IsBuiltin = true, Override = ov,
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    file, Newtonsoft.Json.Formatting.Indented);
                BuiltinPresetWriter.WriteCar(BuiltinPresets.CurrentFolder, game, carId, diskName, json);
                BuiltinPresets.Reload();
                LoadAndMigrateCarPresets();   // push folder built-ins into the live store
                SimHub.Logging.Current.Info($"[Trueforce] DEV: wrote car '{carId}' through to the built-in folder.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] DEV car write-through failed for '{carId}': {ex.Message}");
            }
        }

        /// <summary>Save the car-edit in place (to the frozen car's preset).
        /// Non-dev refuses a built-in (returns false so the caller forks via
        /// Save as new). DEV authoring mode writes the edit through to the
        /// built-in folder instead. On success restores the pre-edit state.</summary>
        public bool ExitOfflineEditCarSave()
        {
            if (!IsOfflineEditingCar) return true;
            // DEV editing an existing built-in car: write the edit through to
            // its folder file. A new/user car preset stays a user preset
            // (promote later via 'Set as built-in').
            if (DevMode && IsActiveCarPresetBuiltin())
            {
                WriteCarBuiltinThroughDev(_offlineEditCarId, _offlineEditCarPresetName);
                RestorePreEditCarState();
                return true;
            }
            if (IsActiveCarPresetBuiltin()) return false;   // non-dev: fork
            PersistActiveCarOverride();
            RestorePreEditCarState();
            return true;
        }

        public bool ExitOfflineEditCarSaveAs(string newName)
        {
            if (!IsOfflineEditingCar || string.IsNullOrEmpty(newName)) return false;
            if (!SaveActiveCarPresetAs(newName)) return false;
            RestorePreEditCarState();
            return true;
        }

        public void ExitOfflineEditCarDiscard()
        {
            if (!IsOfflineEditingCar) return;
            DiscardUnsavedCarDraft(_offlineEditCarId); // drop the unsaved override edits
            RestorePreEditCarState();
        }

        /// <summary>Copy snapshot fields into Settings and re-push to live components.</summary>
        private void ApplyGamePreset(GameSettingsSnapshot snap)
        {
            if (snap == null || Settings == null) return;

            Settings.MasterGain              = snap.MasterGain;
            Settings.FfbScale                = snap.FfbScale;
            Settings.FfbInvertSign           = snap.FfbInvertSign;
            Settings.FfbSmoothTimeConstantMs = snap.FfbSmoothTimeConstantMs;
            Settings.FfbSpikeTamingEnabled   = snap.FfbSpikeTamingEnabled;
            Settings.FfbSpikeUseSlewLimiter  = snap.FfbSpikeUseSlewLimiter;
            Settings.FfbSpikeMaxLsbPerMs     = snap.FfbSpikeMaxLsbPerMs;
            Settings.FfbPeakSoftLimitLsb     = snap.FfbPeakSoftLimitLsb;
            Settings.SkipFfbPassthrough      = snap.SkipFfbPassthrough;
            Settings.DuckDepth               = snap.DuckDepth;
            Settings.DuckAttackMs            = snap.DuckAttackMs;
            Settings.DuckReleaseMs           = snap.DuckReleaseMs;

            if (snap.AudioCapture != null) Settings.AudioCapture = CloneOrNull(snap.AudioCapture);
            if (snap.EnginePulse  != null) Settings.EnginePulse  = Clone(snap.EnginePulse);
            if (snap.RoadBumps    != null) Settings.RoadBumps    = Clone(snap.RoadBumps);
            if (snap.TractionLoss != null) Settings.TractionLoss = Clone(snap.TractionLoss);
            if (snap.GearShift    != null) Settings.GearShift    = Clone(snap.GearShift);
            if (snap.AbsClick     != null) Settings.AbsClick     = Clone(snap.AbsClick);
            if (snap.PitLimiter   != null) Settings.PitLimiter   = Clone(snap.PitLimiter);
            if (snap.Drs          != null) Settings.Drs          = Clone(snap.Drs);
            if (snap.Collision    != null) Settings.Collision    = Clone(snap.Collision);
            if (snap.RevLimiter   != null) Settings.RevLimiter   = Clone(snap.RevLimiter);
            // Airborne ducking travels with the preset. Null on presets saved
            // before it existed; leave the live (global default) value in that
            // case so old presets don't wipe it. ApplyActiveCarOverride below
            // pushes it to the effect via ApplyAirborneSettings.
            if (snap.Airborne     != null) Settings.Airborne     = Clone(snap.Airborne);
            // Per-car overrides are no longer carried by game presets (Model G):
            // they live in <plugin data>/Cars/<carId>.tfcar.json files,
            // independent of the active preset. Switching presets doesn't
            // touch per-car tuning. Legacy snap.CarOverrides (from old
            // saved presets) is intentionally ignored here, migration
            // already extracted any useful data into per-car files.

            // Push live: master, FFB tap, audio, and effects (via car-override apply).
            _mixer.MasterGain = Settings.MasterGain;
            if (_device != null)
            {
                _device.FfbScale                = Settings.FfbScale;
                _device.FfbInvertSign           = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
                _device.FfbSpikeTamingEnabled   = Settings.FfbSpikeTamingEnabled;
                _device.FfbSpikeUseSlewLimiter  = Settings.FfbSpikeUseSlewLimiter;
                _device.FfbSpikeMaxLsbPerMs     = Settings.FfbSpikeMaxLsbPerMs;
                _device.FfbPeakSoftLimitLsb     = Settings.FfbPeakSoftLimitLsb;
            }
            if (_audio != null)
            {
                _audio.Enabled          = Settings.AudioCapture.Enabled;
                _audio.Gain             = Settings.AudioCapture.Gain;
                _audio.LowpassCutoffHz  = Settings.AudioCapture.LowpassCutoffHz;
                _audio.HighpassCutoffHz = Settings.AudioCapture.HighpassCutoffHz;
            }
            ApplyActiveCarOverride();
        }

        private static AudioCaptureSettings CloneOrNull(AudioCaptureSettings s)
            => s == null ? null : new AudioCaptureSettings { Enabled = s.Enabled, Gain = s.Gain, LowpassCutoffHz = s.LowpassCutoffHz, HighpassCutoffHz = s.HighpassCutoffHz };

        private static Dictionary<string, CarOverride> CloneOverrides(Dictionary<string, CarOverride> src)
        {
            if (src == null) return new Dictionary<string, CarOverride>();
            var d = new Dictionary<string, CarOverride>(src.Count);
            foreach (var kv in src)
            {
                var o = kv.Value;
                if (o == null) continue;
                d[kv.Key] = new CarOverride
                {
                    EnginePulse  = o.EnginePulse  == null ? null : Clone(o.EnginePulse),
                    RoadBumps    = o.RoadBumps    == null ? null : Clone(o.RoadBumps),
                    TractionLoss = o.TractionLoss == null ? null : Clone(o.TractionLoss),
                    GearShift    = o.GearShift    == null ? null : Clone(o.GearShift),
                    AbsClick     = o.AbsClick     == null ? null : Clone(o.AbsClick),
                    PitLimiter   = o.PitLimiter   == null ? null : Clone(o.PitLimiter),
                    Drs          = o.Drs          == null ? null : Clone(o.Drs),
                    Collision    = o.Collision    == null ? null : Clone(o.Collision),
                    RevLimiter   = o.RevLimiter   == null ? null : Clone(o.RevLimiter),
                    AudioCapture = CloneOrNull(o.AudioCapture),
                };
            }
            return d;
        }

        // ---------- single-preset export/import (sharing) ----------

        /// <summary>Snapshot of the current top-level settings, used by
        /// both "Save preset" and "Export preset". Per-car overrides are
        /// intentionally omitted: in Model G they live in per-car files
        /// independent of game presets, so applying a preset never touches
        /// per-car tuning.</summary>
        private GameSettingsSnapshot SnapshotCurrentAsPreset()
        {
            return new GameSettingsSnapshot
            {
                MasterGain              = Settings.MasterGain,
                FfbScale                = Settings.FfbScale,
                FfbInvertSign           = Settings.FfbInvertSign,
                FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs,
                FfbSpikeTamingEnabled   = Settings.FfbSpikeTamingEnabled,
                FfbSpikeUseSlewLimiter  = Settings.FfbSpikeUseSlewLimiter,
                FfbSpikeMaxLsbPerMs     = Settings.FfbSpikeMaxLsbPerMs,
                FfbPeakSoftLimitLsb     = Settings.FfbPeakSoftLimitLsb,
                SkipFfbPassthrough      = Settings.SkipFfbPassthrough,
                DuckDepth               = Settings.DuckDepth,
                DuckAttackMs            = Settings.DuckAttackMs,
                DuckReleaseMs           = Settings.DuckReleaseMs,
                AudioCapture            = CloneOrNull(Settings.AudioCapture),
                EnginePulse             = Clone(Settings.EnginePulse),
                RoadBumps               = Clone(Settings.RoadBumps),
                TractionLoss            = Clone(Settings.TractionLoss),
                GearShift               = Clone(Settings.GearShift),
                AbsClick                = Clone(Settings.AbsClick),
                PitLimiter              = Clone(Settings.PitLimiter),
                Drs                     = Clone(Settings.Drs),
                Collision               = Clone(Settings.Collision),
                RevLimiter              = Clone(Settings.RevLimiter),
                Airborne                = Clone(Settings.Airborne),
                // Attribution: stamp the persistent author so the Preset
                // Manager's Source column attributes this row to its
                // author. Null when SharingAuthor is unset (column reads
                // "Local"). Pack identity fields (PackName/AuthorVersion/
                // Description) stay null on user-saved snapshots — those
                // are populated only by the pack-import code path so
                // shared community presets retain their lineage.
                Author                  = CurrentAuthorForStamp(),
                // CarOverrides intentionally omitted, per-car tuning is
                // managed via per-car .tfcar.json files post-Model-G.
            };
        }

        /// <summary>Write a named preset (or the current settings if the name
        /// doesn't exist in the library yet) to a shareable JSON file. The
        /// file carries the preset name but no game binding. Metadata fields
        /// (Author/Description/AuthorVersion) are optional, pass null to
        /// omit; the importer just won't surface them.</summary>
        public void ExportPreset(string presetName, string path,
            string author = null, string description = null, string authorVersion = null)
        {
            if (Settings == null || string.IsNullOrEmpty(presetName)) return;
            GameSettingsSnapshot snap;
            if (Settings.Presets == null || !Settings.Presets.TryGetValue(presetName, out snap) || snap == null)
                snap = SnapshotCurrentAsPreset();
            var file = new PresetFile
            {
                PresetName    = presetName,
                Snapshot      = snap,
                Author        = NullIfBlank(author),
                Description   = NullIfBlank(description),
                AuthorVersion = NullIfBlank(authorVersion),
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported preset '{presetName}' to {path}.");
        }

        // Trim and return null on blank so JSON serialization omits empty
        // strings instead of writing them out (cleaner files, and the
        // importer's null-check logic is straightforward).
        private static string NullIfBlank(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        // Merge incoming CustomEngineDefs into the user's library by Id.
        // Existing local defs ALWAYS win on collision — the Id is supposed
        // to be stable across renames, so different content under the same
        // Id is either a genuine local edit the user shouldn't lose, or
        // backend corruption we shouldn't pave over. Logs both cases.
        //
        // Returns (added, skipped). Optionally constrains to a referenced-
        // ids subset (ImportPackSelective passes it; full-pack imports
        // pass null = merge every def in the wrapper).
        private (int added, int skipped) MergeImportedCustomEngines(
            IEnumerable<CustomEngineDef> incoming,
            HashSet<string> referencedIds = null)
        {
            if (incoming == null) return (0, 0);
            if (Settings == null) return (0, 0);
            if (Settings.CustomEngines == null) Settings.CustomEngines = new List<CustomEngineDef>();
            var existing = new Dictionary<string, CustomEngineDef>(StringComparer.Ordinal);
            foreach (var def in Settings.CustomEngines)
                if (def != null && !string.IsNullOrEmpty(def.Id) && !existing.ContainsKey(def.Id))
                    existing[def.Id] = def;

            int added = 0, skipped = 0;
            foreach (var def in incoming)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                if (referencedIds != null && !referencedIds.Contains(def.Id)) continue;
                if (existing.TryGetValue(def.Id, out var local))
                {
                    if (!string.Equals(local.Pattern, def.Pattern, StringComparison.Ordinal)
                        || local.IsElectric != def.IsElectric
                        || local.ElectricMode != def.ElectricMode)
                    {
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Custom-engine import: keeping local def '{local.Name}' (Id {def.Id}); incoming '{def.Name}' has different content.");
                    }
                    skipped++;
                    continue;
                }
                Settings.CustomEngines.Add(new CustomEngineDef
                {
                    Id           = def.Id,
                    Name         = def.Name,
                    IsElectric   = def.IsElectric,
                    ElectricMode = def.ElectricMode,
                    Pattern      = def.Pattern,
                    Author       = NullIfBlank(def.Author),
                });
                existing[def.Id] = def;
                added++;
            }
            return (added, skipped);
        }

        // Collect the CustomEngineDefs referenced by the given snapshots and
        // car overrides. Walks each EnginePulse.CustomEngineId, resolves
        // against Settings.CustomEngines (the user's library), and returns
        // the matching defs (deduped by Id, only the ones that are actually
        // in the library — dangling refs are silently dropped because the
        // recipient can't do anything with them either). Stamps a curator
        // Author on each resolved def when it has none and the curator's
        // SharingAuthor is set, so a recipient who imports a "MyV12" by
        // Mhytee sees the credit.
        //
        // Pass either or both collections; nulls are tolerated.
        private List<CustomEngineDef> CollectReferencedCustomEngines(
            IEnumerable<GameSettingsSnapshot> snapshots,
            IEnumerable<CarOverride> carOverrides)
        {
            if (Settings?.CustomEngines == null || Settings.CustomEngines.Count == 0)
                return null;
            var wantedIds = new HashSet<string>(StringComparer.Ordinal);
            if (snapshots != null)
            {
                foreach (var snap in snapshots)
                {
                    if (snap == null) continue;
                    string id = NullIfBlank(snap.EnginePulse?.CustomEngineId);
                    if (id != null) wantedIds.Add(id);
                    // Legacy pre-Model-G presets carried per-car overrides
                    // inline on the snapshot. Apply still reads these
                    // (TrueforcePlugin.cs:7395-7406), so an override that
                    // references a custom engine WOULD silently dangle on
                    // the recipient if we didn't walk them here.
                    if (snap.CarOverrides != null)
                    {
                        foreach (var inlineOvr in snap.CarOverrides.Values)
                        {
                            string nestedId = NullIfBlank(inlineOvr?.EnginePulse?.CustomEngineId);
                            if (nestedId != null) wantedIds.Add(nestedId);
                        }
                    }
                }
            }
            if (carOverrides != null)
            {
                foreach (var ovr in carOverrides)
                {
                    var ep = ovr?.EnginePulse;
                    if (ep == null) continue;
                    string id = NullIfBlank(ep.CustomEngineId);
                    if (id != null) wantedIds.Add(id);
                }
            }
            if (wantedIds.Count == 0) return null;

            string curatorAuthor = NullIfBlank(Settings?.SharingAuthor);
            var result = new List<CustomEngineDef>();
            foreach (var def in Settings.CustomEngines)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                if (!wantedIds.Contains(def.Id)) continue;
                // Shallow-copy the def so we don't mutate the in-memory
                // library when stamping the curator Author for unattributed
                // defs.
                result.Add(new CustomEngineDef
                {
                    Id           = def.Id,
                    Name         = def.Name,
                    IsElectric   = def.IsElectric,
                    ElectricMode = def.ElectricMode,
                    Pattern      = def.Pattern,
                    Author       = NullIfBlank(def.Author) ?? curatorAuthor,
                });
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>Read a preset file and store it in the library under the
        /// name embedded in the file. Does NOT auto-apply or auto-bind to a
        /// game, the user explicitly chooses what to do with it next.
        /// Returns a result struct with the imported name plus any author /
        /// description / version metadata in the file (all nullable).</summary>
        public ImportPresetResult ImportPreset(string path)
        {
            if (Settings == null) return default(ImportPresetResult);
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<PresetFile>(json);
            if (file == null || file.Snapshot == null || string.IsNullOrEmpty(file.PresetName))
                throw new System.IO.InvalidDataException("Not a valid TF4ALL preset file.");
            if (file.Type != PresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{PresetFile.FileType}'.");

            // Imports always land in the USER library as user presets, even
            // if the file's name happens to collide with a built-in (the
            // BuiltinPresetWriter is name-keyed; user library writes are
            // independent of the built-in folder). Fold the wrapper's
            // attribution onto the snapshot before serialising so the
            // recipient's disk preserves the original author + pack lineage
            // (per-snapshot fields beat the wrapper to keep authored
            // attribution when both are present).
            file.Snapshot.Author        = NullIfBlank(file.Snapshot.Author)        ?? NullIfBlank(file.Author);
            file.Snapshot.Description   = NullIfBlank(file.Snapshot.Description)   ?? NullIfBlank(file.Description);
            file.Snapshot.AuthorVersion = NullIfBlank(file.Snapshot.AuthorVersion) ?? NullIfBlank(file.AuthorVersion);
            file.Snapshot.PackName      = NullIfBlank(file.Snapshot.PackName)      ?? NullIfBlank(file.PackName);

            // Merge any custom firing-pattern defs the file carried into the
            // user's library so the preset's EnginePulse.CustomEngineId
            // resolves at apply time instead of falling through to silence.
            // Local wins on Id collision.
            var customEngineMerge = MergeImportedCustomEngines(file.CustomEngines);
            string snapJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                file.Snapshot, Newtonsoft.Json.Formatting.Indented);
            // Never clobber an existing user preset of the same name.
            string importName = MakeUniqueGamePresetName(file.PresetName);
            BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, importName, snapJson);
            UserPresets.Reload();
            RebuildPresetCacheFromFolders();
            this.SaveCommonSettings("GeneralSettings", Settings);

            SimHub.Logging.Current.Info($"[Trueforce] Imported preset '{importName}' from {path} into the user library.");
            return new ImportPresetResult
            {
                PresetName    = importName,
                Author        = file.Author,
                Description   = file.Description,
                AuthorVersion = file.AuthorVersion,
            };
        }

        public struct ImportPresetResult
        {
            public string PresetName;
            public string Author;
            public string Description;
            public string AuthorVersion;
        }

        public struct ImportCarPresetResult
        {
            public string CarId;
            public string PresetName;
            public string Author;
            public string Description;
            public string AuthorVersion;
        }

        public struct ImportPackResult
        {
            public int PresetsImported;
            public int CarsImported;
            public string Author;
            public string Description;
            public string AuthorVersion;
        }

        /// <summary>Export the active car's override as a standalone file.
        /// If no override exists yet, captures the current ActiveX section
        /// values so the user can share their tuning without committing it
        /// to a per-car override first.</summary>
        public void ExportActiveCarPreset(string path,
            string author = null, string description = null, string authorVersion = null)
        {
            if (Settings == null || string.IsNullOrEmpty(_activeCarId)) return;
            CarOverride ovr = GetActiveCarOverride();
            if (ovr == null || ovr.IsEmpty)
            {
                // Build a full override from the current active sections so the
                // exported file is self-contained even if the user hasn't
                // toggled "Override for this car" yet.
                ovr = new CarOverride
                {
                    EnginePulse  = Clone(ActiveEngine),
                    RoadBumps    = Clone(ActiveBumps),
                    TractionLoss = Clone(ActiveTraction),
                    GearShift    = Clone(ActiveShift),
                    AbsClick     = Clone(ActiveAbs),
                    AudioCapture = CloneOrNull(ActiveAudio),
                };
            }
            // Carry the active preset name into the exported file so a
            // recipient sees what the author named it. IsBuiltin is forced
            // false on export, only the plugin's bundled factory files are
            // built-ins; an exported community preset is always user-tier.
            string presetName = GetActiveCarPresetName(_activeCarId) ?? _activeCarId;
            var file = new CarPresetFile
            {
                GameName      = _activeGame,
                CarId         = _activeCarId,
                PresetName    = StripDefaultSuffixForExport(presetName),
                IsBuiltin     = false,
                Author        = NullIfBlank(author),
                Description   = NullIfBlank(description),
                AuthorVersion = NullIfBlank(authorVersion),
                Override      = ovr,
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset '{file.PresetName}' for '{_activeCarId}' to {path}.");
        }

        /// <summary>Export a specific car preset (arbitrary carId / presetName)
        /// to a shareable JSON file. Used by the preset manager where
        /// the user can pick any preset regardless of which car is currently
        /// active. Returns false if the preset doesn't exist on disk.</summary>
        public bool ExportCarPreset(string carId, string presetName, string path,
            string author = null, string description = null, string authorVersion = null)
        {
            if (_carStore == null) return false;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName) || string.IsNullOrEmpty(path)) return false;
            // presetName from UI may be a factory display name (with the
            // " (Built-In)" suffix). _carStore.LoadAll keys are on-disk; strip
            // before lookup. The export file's PresetName also gets the
            // on-disk form so re-import doesn't reproduce the suffix.
            string diskName = ToDiskName(presetName);
            var loaded = _carStore.LoadAll();
            CarPresetEntry entry = null;
            if (loaded.TryGetValue(carId, out var perCar))
                perCar.TryGetValue(diskName, out entry);
            // Fall back to factory if the user side doesn't have it.
            if (entry == null && BuiltinPresets.CarPresetJsons.TryGetValue(carId + "/" + diskName, out var factJson))
            {
                try { entry = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(factJson)
                    is CarPresetFile fac && fac != null
                    ? new CarPresetEntry { CarId = carId, PresetName = diskName, GameName = fac.GameName ?? "", IsBuiltin = true, Override = fac.Override } : null;
                } catch { }
            }
            if (entry == null) return false;
            var file = new CarPresetFile
            {
                GameName      = entry.GameName ?? "",
                CarId         = carId,
                PresetName    = StripDefaultSuffixForExport(diskName),
                IsBuiltin     = false,
                Author        = NullIfBlank(author),
                Description   = NullIfBlank(description),
                AuthorVersion = NullIfBlank(authorVersion),
                Override      = entry.Override,
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset '{carId}/{diskName}' to {path}.");
            return true;
        }

        // Strip a trailing " (default)" so an exported built-in doesn't
        // claim to be a built-in on import (which we'd refuse to honor
        // anyway, but the UX is cleaner without the suffix).
        private static string StripDefaultSuffixForExport(string name)
        {
            const string suffix = " (default)";
            return !string.IsNullOrEmpty(name) && name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        /// <summary>Read a car-preset file and add it to the multi-preset
        /// library under (CarId, PresetName) with IsBuiltin forced to false.
        /// If a user preset with the same name already exists for this car,
        /// appends "(N)" to keep both. Sets CarDefaults[carId] = imported
        /// preset name so the import becomes the active preset for that
        /// car. Applied immediately if the imported CarId matches the
        /// active car.</summary>
        /// <returns>Imported carId, final preset name, and any sharing metadata.</returns>
        public ImportCarPresetResult ImportCarPreset(string path)
        {
            if (Settings == null || _carStore == null) return default(ImportCarPresetResult);
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(json);
            if (file == null || file.Override == null || string.IsNullOrEmpty(file.CarId))
                throw new System.IO.InvalidDataException("Not a valid TF4ALL car-preset file.");
            if (file.Type != CarPresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{CarPresetFile.FileType}'.");

            // Merge any custom firing-pattern defs the file carried into the
            // user's library so the override's EnginePulse.CustomEngineId
            // resolves at apply time. Local wins on Id collision.
            var customEngineMerge = MergeImportedCustomEngines(file.CustomEngines);

            // PresetName may be missing on legacy v1 imports, fall back to
            // the carId. IsBuiltin is force-cleared regardless of source.
            string desired = string.IsNullOrEmpty(file.PresetName) ? file.CarId : file.PresetName;
            string presetName = MakeUniqueCarPresetName(file.CarId, desired);
            // Preserve the source file's attribution + pack identity on disk.
            // Without this the row would land unattributed, the manager's
            // Source column would read "Local", and the original author's
            // credit would be silently erased on first re-save.
            _carStore.Save(file.CarId, presetName, file.GameName ?? "", file.Override, isBuiltin: false,
                packName:      NullIfBlank(file.PackName),
                author:        NullIfBlank(file.Author),
                description:   NullIfBlank(file.Description),
                authorVersion: NullIfBlank(file.AuthorVersion));

            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults[file.CarId] = presetName;
            // Persist through to the user library's car-defaults.json so the
            // binding survives Init's rebuild (Settings.CarDefaults is a
            // runtime cache post-V2 migration; ShouldSerializeCarDefaults
            // returns false then, so the in-memory write alone would silently
            // revert on next plugin start).
            BuiltinPresetWriter.SetCarDefault(UserPresets.CurrentFolder, file.CarId, presetName);
            this.SaveCommonSettings("GeneralSettings", Settings);

            if (file.CarId == _activeCarId) ReloadActiveCarOverrideFromStore();
            SimHub.Logging.Current.Info(
                $"[Trueforce] Imported car preset '{presetName}' for '{file.CarId}' from {path}.");
            return new ImportCarPresetResult
            {
                CarId         = file.CarId,
                PresetName    = presetName,
                Author        = file.Author,
                Description   = file.Description,
                AuthorVersion = file.AuthorVersion,
            };
        }

        // Append "(2)", "(3)", … to the desired name until it's unique
        // among the existing presets for this car. Avoids accidentally
        // clobbering a user preset with the same name as the import.
        private string MakeUniqueCarPresetName(string carId, string desired)
        {
            if (_carStore == null || string.IsNullOrEmpty(desired)) return desired;
            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar)) return desired;
            if (!perCar.ContainsKey(desired)) return desired;
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{desired} ({i})";
                if (!perCar.ContainsKey(candidate)) return candidate;
            }
            return $"{desired} ({DateTime.Now:HHmmss})";
        }

        // Append "(2)", "(3)", … to a game preset name until it's unique among
        // the user's existing presets (and any imported earlier in the same
        // batch via alsoUsed). Mirrors MakeUniqueCarPresetName: an import never
        // clobbers an existing user game preset of the same name.
        private string MakeUniqueGamePresetName(string desired, HashSet<string> alsoUsed = null)
        {
            if (string.IsNullOrEmpty(desired)) return desired;
            bool Taken(string n) => UserPresets.HasGamePreset(n) || (alsoUsed != null && alsoUsed.Contains(n));
            if (!Taken(desired)) return desired;
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{desired} ({i})";
                if (!Taken(candidate)) return candidate;
            }
            return $"{desired} ({DateTime.Now:HHmmss})";
        }

        // ---------- preset pack (multi-preset zip) ----------

        /// <summary>Returns the names of all game presets in the library, in
        /// alphabetical order. Used by the export-pack picker.</summary>
        public List<string> GetExportablePresetNames()
        {
            if (Settings?.Presets == null) return new List<string>();
            var names = new List<string>(Settings.Presets.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Returns every (carId, presetName, gameName) tuple
        /// currently on disk, sorted by carId then preset name. Used by the
        /// export-pack picker.</summary>
        public List<CarPresetEntry> GetExportableCarPresets()
        {
            var result = new List<CarPresetEntry>();
            if (_carStore == null) return result;
            var loaded = _carStore.LoadAll();
            foreach (var carKv in loaded)
            {
                foreach (var pKv in carKv.Value)
                    result.Add(pKv.Value);
            }
            result.Sort((a, b) =>
            {
                int c = string.Compare(a.CarId, b.CarId, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.PresetName, b.PresetName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>Bundle the selected game presets and car presets into a
        /// .tfpack zip. Pass null for either collection to mean "include all
        /// of that kind". Layout: manifest.json, presets/Name.tfpreset,
        /// cars/CarId~PresetName.tfcar.json. Built-in car presets are exported
        /// with IsBuiltin forced false (only the bundled factory files are
        /// genuine built-ins).</summary>
        public (int presetsExported, int carsExported) ExportPack(
            string path,
            IEnumerable<string> presetNames,
            IEnumerable<(string CarId, string PresetName)> carPresets,
            string author = null, string description = null, string authorVersion = null,
            string packName = null)
        {
            if (Settings == null) return (0, 0);

            // Materialize selection: null = all available.
            var pickedPresets = presetNames != null
                ? new HashSet<string>(presetNames, StringComparer.Ordinal)
                : null;
            var pickedCars = carPresets != null
                ? new HashSet<(string, string)>(carPresets)
                : null;

            string normAuthor = NullIfBlank(author);
            string normDesc   = NullIfBlank(description);
            string normVer    = NullIfBlank(authorVersion);
            string normPack   = NullIfBlank(packName);
            var manifest = new PresetPackManifest
            {
                ExportedAt    = DateTime.UtcNow.ToString("o"),
                PackName      = normPack,
                Author        = normAuthor,
                Description   = normDesc,
                AuthorVersion = normVer,
            };

            int presetsCount = 0, carsCount = 0;
            // Track every included snapshot + override so we can collect the
            // CustomEngineDefs they reference once at the manifest write,
            // deduped across the whole pack (a custom engine used by 3
            // presets ships once, not three times).
            var includedSnapshots = new List<GameSettingsSnapshot>();
            var includedOverrides = new List<CarOverride>();
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                // ---- game presets ----
                if (Settings.Presets != null)
                {
                    foreach (var kv in Settings.Presets)
                    {
                        if (pickedPresets != null && !pickedPresets.Contains(kv.Key)) continue;
                        if (kv.Value == null) continue;
                        includedSnapshots.Add(kv.Value);

                        string entryName = "presets/" + SanitizeForZip(kv.Key) + ".tfpreset";
                        // PackName at the per-preset level = the pack-level
                        // PackName (tags every entry as belonging to this
                        // pack so a later "delete pack" knows which entries
                        // it owns). But Author/Description/AuthorVersion
                        // PRESERVE the source preset's own metadata when
                        // present — a curator can't strip an original
                        // contributor's credit by adding their work to a pack.
                        // Only when the source has no Author do we fall back
                        // to the curator's pack-level Author.
                        var file = new PresetFile
                        {
                            PresetName    = kv.Key,
                            Snapshot      = kv.Value,
                            PackName      = normPack,
                            Author        = NullIfBlank(kv.Value.Author)        ?? normAuthor,
                            Description   = NullIfBlank(kv.Value.Description)   ?? normDesc,
                            AuthorVersion = NullIfBlank(kv.Value.AuthorVersion) ?? normVer,
                        };
                        WriteJsonZipEntry(zip, entryName, file);
                        manifest.Presets.Add(entryName);
                        presetsCount++;
                    }
                }

                // ---- car presets ----
                if (_carStore != null)
                {
                    var loaded = _carStore.LoadAll();
                    foreach (var carKv in loaded)
                    {
                        foreach (var pKv in carKv.Value)
                        {
                            var entry = pKv.Value;
                            var key = (entry.CarId, entry.PresetName);
                            if (pickedCars != null && !pickedCars.Contains(key)) continue;
                            if (entry.Override != null) includedOverrides.Add(entry.Override);

                            string entryName = "cars/" + SanitizeForZip(entry.CarId) + "~"
                                + SanitizeForZip(entry.PresetName) + ".tfcar.json";
                            // Per-row attribution preserves the source car
                            // preset's own metadata (so a curator can't strip
                            // credit by adding someone else's preset to their
                            // pack). PackName at the per-row level = the
                            // pack-level tag so "delete pack" knows which
                            // entries it owns.
                            var file = new CarPresetFile
                            {
                                GameName      = entry.GameName ?? "",
                                CarId         = entry.CarId,
                                PresetName    = entry.PresetName,
                                IsBuiltin     = false,
                                PackName      = normPack,
                                Author        = NullIfBlank(entry.Author)        ?? normAuthor,
                                Description   = NullIfBlank(entry.Description)   ?? normDesc,
                                AuthorVersion = NullIfBlank(entry.AuthorVersion) ?? normVer,
                                Override      = entry.Override,
                            };
                            WriteJsonZipEntry(zip, entryName, file);
                            manifest.Cars.Add(new PackedCarPreset
                            {
                                CarId      = entry.CarId,
                                PresetName = entry.PresetName,
                                GameName   = entry.GameName ?? "",
                                FileName   = entryName,
                            });
                            carsCount++;
                        }
                    }
                }

                // Attach the deduped CustomEngineDefs referenced by any of
                // the packed snapshots/overrides so a recipient gets the
                // pattern data, not just dangling CustomEngineIds.
                manifest.CustomEngines = CollectReferencedCustomEngines(includedSnapshots, includedOverrides);

                WriteJsonZipEntry(zip, "manifest.json", manifest);

                // README inside the archive so a recipient who extracts the
                // zip first (instead of using Import directly) sees how to
                // install it without leaving the folder.
                var readmeEntry = zip.CreateEntry("README.txt");
                using (var ws = readmeEntry.Open())
                using (var sw = new System.IO.StreamWriter(ws, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(BuildPackReadme(normPack, normAuthor, normVer, presetsCount, carsCount));
                }
            }

            SimHub.Logging.Current.Info(
                $"[Trueforce] Exported pack to {path}: {presetsCount} game preset(s), {carsCount} car preset(s).");
            return (presetsCount, carsCount);
        }

        // README written into pack zips. Helps a recipient who extracts the
        // archive instead of importing it directly. Mentions the pack
        // identity (name + author + version) and the step-by-step Import
        // flow inside SimHub.
        private static string BuildPackReadme(string packName, string author, string version,
            int presetsCount, int carsCount)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Trueforce For All - preset pack");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(packName))   sb.AppendLine($"Pack:    {packName}");
            if (!string.IsNullOrEmpty(author))     sb.AppendLine($"Author:  {author}");
            if (!string.IsNullOrEmpty(version))    sb.AppendLine($"Version: {version}");
            if (presetsCount > 0 || carsCount > 0)
            {
                var parts = new List<string>();
                if (presetsCount > 0) parts.Add($"{presetsCount} game preset(s)");
                if (carsCount    > 0) parts.Add($"{carsCount} car preset(s)");
                sb.AppendLine($"Contents: " + string.Join(", ", parts));
            }
            sb.AppendLine();
            sb.AppendLine("To install:");
            sb.AppendLine("  1. Open SimHub.");
            sb.AppendLine("  2. Left sidebar -> Trueforce For All plugin.");
            sb.AppendLine("  3. Click the Presets tab.");
            sb.AppendLine("  4. Click Import (top right of the preset list).");
            sb.AppendLine("  5. Pick this .tfpack file (or this extracted folder).");
            sb.AppendLine();
            sb.AppendLine("All presets in the pack share the (Author, Version, Pack Name) identity,");
            sb.AppendLine("so the manager can later show them grouped and let you remove or set");
            sb.AppendLine("them as defaults as a unit.");
            return sb.ToString();
        }

        // ExportLoose + BuildLooseExportReadme used to live here. They wrote
        // a folder of .tfpreset.json / .tfcar.json plus a README explaining
        // how to import them. Retired by the count-based export rule (single
        // picked = single loose file; multi picked = always a pack), which
        // gives the same affordance with simpler attribution preservation.
        // The originals had stricter author preservation than ExportPack and
        // would have been a foot-gun if some future code path called them
        // again, so they were deleted rather than left as dead code. If
        // "bulk loose export" comes back, build it on the same source-Author-
        // preserves pattern as ExportSinglePreset.

        /// <summary>Write a single game preset to a .tfpreset.json file at
        /// the user-chosen path. Author / Description / AuthorVersion come
        /// from the export-metadata dialog (the curator); per-preset author
        /// already stamped on the snapshot is preserved when present so the
        /// original contributor keeps credit.</summary>
        public void ExportSinglePreset(string path, string presetName,
            string author = null, string description = null, string authorVersion = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Output path is empty.", nameof(path));
            if (string.IsNullOrEmpty(presetName)) throw new ArgumentException("Preset name is empty.", nameof(presetName));
            if (Settings?.Presets == null || !Settings.Presets.TryGetValue(presetName, out var snap) || snap == null)
                throw new InvalidOperationException($"Preset '{presetName}' no longer exists in the library.");

            // Per-preset author: keep the snapshot's own when present; only
            // fall back to the curator's author for unattributed presets.
            string finalAuthor  = NullIfBlank(snap.Author)        ?? NullIfBlank(author);
            string finalDesc    = NullIfBlank(snap.Description)   ?? NullIfBlank(description);
            string finalVer     = NullIfBlank(snap.AuthorVersion) ?? NullIfBlank(authorVersion);

            var file = new PresetFile
            {
                PresetName    = presetName,
                Snapshot      = snap,
                Author        = finalAuthor,
                Description   = finalDesc,
                AuthorVersion = finalVer,
                CustomEngines = CollectReferencedCustomEngines(new[] { snap }, carOverrides: null),
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported single preset '{presetName}' to {path}.");
        }

        /// <summary>Write a single car preset to a .tfcar.json file at the
        /// user-chosen path. Preserves the source car preset's PackName /
        /// Author / Description / AuthorVersion when present (so a curator
        /// can't redistribute someone else's preset stripped of credit),
        /// falling back to the export-metadata dialog values only when the
        /// source itself was unattributed.</summary>
        public void ExportSingleCarPreset(string path, string carId, string presetName,
            string author = null, string description = null, string authorVersion = null)
        {
            if (_carStore == null) throw new InvalidOperationException("Car preset store is not initialised.");
            if (string.IsNullOrEmpty(path))       throw new ArgumentException("Output path is empty.", nameof(path));
            if (string.IsNullOrEmpty(carId))      throw new ArgumentException("CarId is empty.", nameof(carId));
            if (string.IsNullOrEmpty(presetName)) throw new ArgumentException("Preset name is empty.", nameof(presetName));

            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar)
                || !perCar.TryGetValue(presetName, out var entry)
                || entry == null || entry.Override == null)
                throw new InvalidOperationException($"Car preset '{carId}/{presetName}' no longer exists in the library.");

            string finalAuthor = NullIfBlank(entry.Author)        ?? NullIfBlank(author);
            string finalDesc   = NullIfBlank(entry.Description)   ?? NullIfBlank(description);
            string finalVer    = NullIfBlank(entry.AuthorVersion) ?? NullIfBlank(authorVersion);
            string finalPack   = NullIfBlank(entry.PackName);   // preserve source pack tag; no curator override

            var file = new CarPresetFile
            {
                GameName      = entry.GameName ?? "",
                CarId         = entry.CarId,
                PresetName    = ToDiskName(entry.PresetName),
                IsBuiltin     = false,
                PackName      = finalPack,
                Author        = finalAuthor,
                Description   = finalDesc,
                AuthorVersion = finalVer,
                Override      = entry.Override,
                CustomEngines = CollectReferencedCustomEngines(snapshots: null, carOverrides: new[] { entry.Override }),
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported single car preset '{carId}/{presetName}' to {path}.");
        }

        /// <summary>Read every preset and car-preset file in the pack zip.
        /// Game presets land in Settings.Presets (overwriting any with the
        /// same name); car presets go through MakeUniqueCarPresetName so a
        /// name collision keeps both. Returns a (presets, cars) count.</summary>
        /// <summary>Open a .tfpack / .zip read-only and return its manifest +
        /// the list of contained items WITHOUT writing anything to disk. Used
        /// by the import preview to show pack metadata + a checklist before
        /// the user commits. Returns null when the file isn't a zip or has
        /// no manifest.json (caller treats those as "loose multi-file bundle"
        /// rather than a structured pack).</summary>
        public PresetPackManifest PeekPackManifest(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                {
                    var manifestEntry = zip.GetEntry("manifest.json");
                    if (manifestEntry == null) return null;
                    var manifest = ReadJsonZipEntry<PresetPackManifest>(manifestEntry);
                    if (manifest == null) return null;
                    if (!string.IsNullOrEmpty(manifest.Type) && manifest.Type != PresetPackManifest.FileType) return null;
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] PeekPackManifest({path}) failed: {ex.Message}");
                return null;
            }
        }

        public ImportPackResult ImportPack(string path)
        {
            if (Settings == null) return default(ImportPackResult);

            string packAuthor = null, packDesc = null, packVer = null, packName = null;
            int presetsImported = 0, carsImported = 0;
            // Identity entries this import actually wrote (actual unique names),
            // recorded into the installed-packs sidecar at the end so a later
            // "delete pack / filter by pack" feature and the Source column can
            // attribute these rows to their pack.
            var packEntries = new List<InstalledPackEntry>();
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
            {
                // Validate manifest if present (we don't strictly need it to
                // import, but Type-mismatch is a useful early failure for the
                // "user picked the wrong zip" case).
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry != null)
                {
                    var manifest = ReadJsonZipEntry<PresetPackManifest>(manifestEntry);
                    if (manifest != null)
                    {
                        if (!string.IsNullOrEmpty(manifest.Type)
                            && manifest.Type != PresetPackManifest.FileType)
                        {
                            throw new System.IO.InvalidDataException(
                                $"Wrong pack type '{manifest.Type}'. Expected '{PresetPackManifest.FileType}'.");
                        }
                        packAuthor = manifest.Author;
                        packDesc   = manifest.Description;
                        packVer    = manifest.AuthorVersion;
                        packName   = manifest.PackName;
                        // Full-pack import: merge every CustomEngineDef the
                        // manifest carried so any contained preset's
                        // EnginePulse.CustomEngineId resolves on apply.
                        MergeImportedCustomEngines(manifest.CustomEngines);
                    }
                }

                if (Settings.Presets == null)
                    Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();

                // Track game-preset names chosen in this pack so two entries
                // with the same name (or a clash with an existing user preset)
                // don't clobber each other. Mirrors the car unique-name guard.
                var importedGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("presets/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfpreset", StringComparison.OrdinalIgnoreCase))
                    {
                        var pf = ReadJsonZipEntry<PresetFile>(entry);
                        if (pf == null || pf.Snapshot == null || string.IsNullOrEmpty(pf.PresetName)) continue;
                        if (pf.Type != PresetFile.FileType) continue;
                        // Write to the user library folder (imported pack
                        // entries are user presets, not factory built-ins).
                        try
                        {
                            // Fold wrapper attribution onto the snapshot so
                            // each preset's on-disk file carries its own
                            // author + pack lineage. Per-snapshot fields
                            // beat the wrapper (preserve original credit
                            // when the curator stamped over a community
                            // contributor's work).
                            pf.Snapshot.Author        = NullIfBlank(pf.Snapshot.Author)        ?? NullIfBlank(pf.Author)        ?? packAuthor;
                            pf.Snapshot.Description   = NullIfBlank(pf.Snapshot.Description)   ?? NullIfBlank(pf.Description)   ?? packDesc;
                            pf.Snapshot.AuthorVersion = NullIfBlank(pf.Snapshot.AuthorVersion) ?? NullIfBlank(pf.AuthorVersion) ?? packVer;
                            pf.Snapshot.PackName      = NullIfBlank(pf.Snapshot.PackName)      ?? NullIfBlank(pf.PackName)      ?? packName;
                            string snapJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                                pf.Snapshot, Newtonsoft.Json.Formatting.Indented);
                            string gName = MakeUniqueGamePresetName(pf.PresetName, importedGameNames);
                            importedGameNames.Add(gName);
                            BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, gName, snapJson);
                            // Record the ACTUAL name written (post-uniquing) so
                            // the sidecar identity matches what's on disk.
                            // BaselineHash stamps the JSON we just wrote so the
                            // Pack Manager can detect user edits before destructive
                            // remove (hash mismatch → user touched it → keep).
                            packEntries.Add(new InstalledPackEntry
                            {
                                Kind         = InstalledPackEntry.KindGame,
                                Name         = gName,
                                BaselineHash = InstalledPacksStore.ComputeContentHash(snapJson),
                            });
                            presetsImported++;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Pack import of preset '{pf.PresetName}' failed: {ex.Message}");
                        }
                    }
                    else if (entry.FullName.StartsWith("cars/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfcar.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var cf = ReadJsonZipEntry<CarPresetFile>(entry);
                        if (cf == null || cf.Override == null || string.IsNullOrEmpty(cf.CarId)) continue;
                        if (cf.Type != CarPresetFile.FileType) continue;

                        string desired = string.IsNullOrEmpty(cf.PresetName) ? cf.CarId : cf.PresetName;
                        string presetName = MakeUniqueCarPresetName(cf.CarId, desired);
                        // Persist the pack identity onto the car file so it
                        // survives on disk. Prefer the file's own metadata
                        // (per-preset), fall back to the pack-level fields.
                        _carStore?.Save(cf.CarId, presetName, cf.GameName ?? "", cf.Override, isBuiltin: false,
                            packName:      cf.PackName      ?? packName,
                            author:        cf.Author        ?? packAuthor,
                            description:   cf.Description    ?? packDesc,
                            authorVersion: cf.AuthorVersion ?? packVer);
                        // Record the ACTUAL carId + name written (post-uniquing).
                        // GameName lets the Pack Manager locate the file without
                        // walking the cars/ tree; BaselineHash is read from the
                        // just-saved file (CarPresetStore.Save folds in existing
                        // attribution so we can't hash the input directly).
                        packEntries.Add(new InstalledPackEntry
                        {
                            Kind         = InstalledPackEntry.KindCar,
                            CarId        = cf.CarId,
                            PresetName   = presetName,
                            GameName     = cf.GameName ?? "",
                            BaselineHash = TryHashCarPresetFile(cf.CarId, presetName),
                        });
                        carsImported++;
                    }
                }
            }

            // Record the pack identity in the sidecar so future "delete pack /
            // filter by pack / set pack as default" and the Source column can
            // attribute these rows. One record per ImportPack call. Fall back
            // to a sensible label when no PackName was supplied (the author, or
            // a generic "Imported pack").
            if (packEntries.Count > 0)
            {
                string label = NullIfBlank(packName) ?? NullIfBlank(packAuthor) ?? "Imported pack";
                _installedPacks?.AddPack(new InstalledPack
                {
                    PackName      = label,
                    Author        = packAuthor,
                    AuthorVersion = packVer,
                    Description    = packDesc,
                    ImportedAt    = DateTime.Now,
                    Entries       = packEntries,
                });
            }

            // Game presets just landed in the user library folder; reload +
            // rebuild the cache so they show up in the library immediately.
            if (presetsImported > 0)
            {
                UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            this.SaveCommonSettings("GeneralSettings", Settings);
            if (!string.IsNullOrEmpty(_activeCarId)) ReloadActiveCarOverrideFromStore();

            SimHub.Logging.Current.Info(
                $"[Trueforce] Imported pack from {path}: {presetsImported} game preset(s), {carsImported} car preset(s).");
            return new ImportPackResult
            {
                PresetsImported = presetsImported,
                CarsImported    = carsImported,
                Author          = packAuthor,
                Description     = packDesc,
                AuthorVersion   = packVer,
            };
        }

        /// <summary>Import only the selected entries from a .tfpack / .zip,
        /// and optionally bind a per-row entry as the game-default or
        /// car-default in the same pass. Behaves like ImportPack for entries
        /// the user kept; entries not in the include sets are skipped with no
        /// disk write. Mirrors ImportPack's installed-packs sidecar registration
        /// + unique-naming so a partial pack still attributes its rows.
        ///
        /// <paramref name="includedGamePresets"/> = pack-side preset names to
        /// import. <paramref name="includedCarPresets"/> = (CarId, PresetName)
        /// tuples to import (tuple as written in the pack, NOT post-uniquing).
        /// <paramref name="setGameDefaultFor"/> = subset of pack-side game
        /// preset names that should be bound as the default for the
        /// PresetFile.Snapshot.GameName inside the pack entry. <paramref
        /// name="setCarDefaultFor"/> = subset of (CarId, PresetName) tuples
        /// that should be bound as CarDefaults[CarId]=postUniquingName.</summary>
        public ImportPackResult ImportPackSelective(string path,
            HashSet<string> includedGamePresets,
            HashSet<(string CarId, string PresetName)> includedCarPresets,
            HashSet<string> setGameDefaultFor,
            HashSet<(string CarId, string PresetName)> setCarDefaultFor)
        {
            if (Settings == null) return default(ImportPackResult);
            if (includedGamePresets == null) includedGamePresets = new HashSet<string>(StringComparer.Ordinal);
            if (includedCarPresets  == null) includedCarPresets  = new HashSet<(string, string)>();
            if (setGameDefaultFor   == null) setGameDefaultFor   = new HashSet<string>(StringComparer.Ordinal);
            if (setCarDefaultFor    == null) setCarDefaultFor    = new HashSet<(string, string)>();
            // No-op short-circuit: if a future caller passes both include sets
            // empty (CLI script, automated reseed) we'd otherwise open the zip,
            // walk it, write nothing, and still register a phantom empty
            // InstalledPack. Cheap to skip.
            if (includedGamePresets.Count == 0 && includedCarPresets.Count == 0)
                return default(ImportPackResult);

            string packAuthor = null, packDesc = null, packVer = null, packName = null;
            int presetsImported = 0, carsImported = 0;
            int carDefaultsSet = 0;
            var packEntries = new List<InstalledPackEntry>();
            // Defer the CustomEngines merge until after the iteration so we
            // can constrain to only the defs actually referenced by the
            // user-selected items (selective imports shouldn't pollute the
            // recipient's library with defs from rows the user deselected).
            List<CustomEngineDef> manifestCustomEngines = null;
            var referencedCustomEngineIds = new HashSet<string>(StringComparer.Ordinal);

            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
            {
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry != null)
                {
                    var manifest = ReadJsonZipEntry<PresetPackManifest>(manifestEntry);
                    if (manifest != null)
                    {
                        if (!string.IsNullOrEmpty(manifest.Type) && manifest.Type != PresetPackManifest.FileType)
                            throw new System.IO.InvalidDataException(
                                $"Wrong pack type '{manifest.Type}'. Expected '{PresetPackManifest.FileType}'.");
                        packAuthor = manifest.Author;
                        packDesc   = manifest.Description;
                        packVer    = manifest.AuthorVersion;
                        packName   = manifest.PackName;
                        manifestCustomEngines = manifest.CustomEngines;
                    }
                }

                if (Settings.Presets == null)
                    Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
                if (Settings.GameDefaults == null)
                    Settings.GameDefaults = new Dictionary<string, string>();
                if (Settings.CarDefaults  == null)
                    Settings.CarDefaults  = new Dictionary<string, string>();

                var importedGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("presets/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfpreset", StringComparison.OrdinalIgnoreCase))
                    {
                        var pf = ReadJsonZipEntry<PresetFile>(entry);
                        if (pf == null || pf.Snapshot == null || string.IsNullOrEmpty(pf.PresetName)) continue;
                        if (pf.Type != PresetFile.FileType) continue;
                        if (!includedGamePresets.Contains(pf.PresetName)) continue;
                        try
                        {
                            // Fold wrapper attribution onto the snapshot
                            // (same rule as full ImportPack).
                            pf.Snapshot.Author        = NullIfBlank(pf.Snapshot.Author)        ?? NullIfBlank(pf.Author)        ?? packAuthor;
                            pf.Snapshot.Description   = NullIfBlank(pf.Snapshot.Description)   ?? NullIfBlank(pf.Description)   ?? packDesc;
                            pf.Snapshot.AuthorVersion = NullIfBlank(pf.Snapshot.AuthorVersion) ?? NullIfBlank(pf.AuthorVersion) ?? packVer;
                            pf.Snapshot.PackName      = NullIfBlank(pf.Snapshot.PackName)      ?? NullIfBlank(pf.PackName)      ?? packName;
                            string snapJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                                pf.Snapshot, Newtonsoft.Json.Formatting.Indented);
                            string gName = MakeUniqueGamePresetName(pf.PresetName, importedGameNames);
                            importedGameNames.Add(gName);
                            BuiltinPresetWriter.WriteGame(UserPresets.CurrentFolder, gName, snapJson);
                            packEntries.Add(new InstalledPackEntry
                            {
                                Kind         = InstalledPackEntry.KindGame,
                                Name         = gName,
                                BaselineHash = InstalledPacksStore.ComputeContentHash(snapJson),
                            });
                            presetsImported++;
                            // Track the custom engine the imported preset references
                            // (if any) so the post-loop merge brings its def along.
                            string epCustomId = NullIfBlank(pf.Snapshot.EnginePulse?.CustomEngineId);
                            if (epCustomId != null) referencedCustomEngineIds.Add(epCustomId);
                            // Legacy snap.CarOverrides path: nested per-car
                            // overrides on a pre-Model-G snapshot also need
                            // their CustomEngineId pulled into the merge set
                            // or the dangling-pointer hole reopens for that
                            // case.
                            if (pf.Snapshot.CarOverrides != null)
                            {
                                foreach (var inlineOvr in pf.Snapshot.CarOverrides.Values)
                                {
                                    string nestedId = NullIfBlank(inlineOvr?.EnginePulse?.CustomEngineId);
                                    if (nestedId != null) referencedCustomEngineIds.Add(nestedId);
                                }
                            }

                            // Set-as-default for game presets requires a
                            // (preset -> game) mapping that the bare
                            // PresetFile + GameSettingsSnapshot doesn't carry.
                            // The preview modal disables the toggle for game
                            // rows accordingly; this branch stays for the day
                            // we add a Defaults hint to PresetPackManifest.
                            if (setGameDefaultFor.Contains(pf.PresetName))
                            {
                                SimHub.Logging.Current.Info(
                                    $"[Trueforce] Selective pack import: set-as-default for game preset '{gName}' requested but not yet supported (data model doesn't carry preset->game mapping).");
                            }
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Selective pack import of preset '{pf.PresetName}' failed: {ex.Message}");
                        }
                    }
                    else if (entry.FullName.StartsWith("cars/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfcar.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var cf = ReadJsonZipEntry<CarPresetFile>(entry);
                        if (cf == null || cf.Override == null || string.IsNullOrEmpty(cf.CarId)) continue;
                        if (cf.Type != CarPresetFile.FileType) continue;

                        string desired = string.IsNullOrEmpty(cf.PresetName) ? cf.CarId : cf.PresetName;
                        var packKey = (cf.CarId, desired);
                        if (!includedCarPresets.Contains(packKey)) continue;

                        string presetName = MakeUniqueCarPresetName(cf.CarId, desired);
                        _carStore?.Save(cf.CarId, presetName, cf.GameName ?? "", cf.Override, isBuiltin: false,
                            packName:      cf.PackName      ?? packName,
                            author:        cf.Author        ?? packAuthor,
                            description:   cf.Description    ?? packDesc,
                            authorVersion: cf.AuthorVersion ?? packVer);
                        packEntries.Add(new InstalledPackEntry
                        {
                            Kind         = InstalledPackEntry.KindCar,
                            CarId        = cf.CarId,
                            PresetName   = presetName,
                            GameName     = cf.GameName ?? "",
                            BaselineHash = TryHashCarPresetFile(cf.CarId, presetName),
                        });
                        carsImported++;
                        string carCustomId = NullIfBlank(cf.Override.EnginePulse?.CustomEngineId);
                        if (carCustomId != null) referencedCustomEngineIds.Add(carCustomId);

                        // Set-as-default for this car. Settings.CarDefaults
                        // is a runtime cache (ShouldSerializeCarDefaults is
                        // false post-V2 migration), so the in-memory write
                        // alone would silently revert on next Init. Persist
                        // through to the user library's car-defaults.json
                        // (BuiltinPresetWriter.SetCarDefault) so the binding
                        // survives restarts.
                        if (setCarDefaultFor.Contains(packKey))
                        {
                            Settings.CarDefaults[cf.CarId] = presetName;
                            BuiltinPresetWriter.SetCarDefault(UserPresets.CurrentFolder, cf.CarId, presetName);
                            carDefaultsSet++;
                        }
                    }
                }
            }

            // Merge only the CustomEngineDefs actually referenced by the
            // items the user kept. Skipping unselected rows' defs keeps the
            // recipient's library clean of patterns they didn't ask for.
            if (manifestCustomEngines != null && referencedCustomEngineIds.Count > 0)
                MergeImportedCustomEngines(manifestCustomEngines, referencedCustomEngineIds);

            if (packEntries.Count > 0)
            {
                string label = NullIfBlank(packName) ?? NullIfBlank(packAuthor) ?? "Imported pack";
                _installedPacks?.AddPack(new InstalledPack
                {
                    PackName      = label,
                    Author        = packAuthor,
                    AuthorVersion = packVer,
                    Description   = packDesc,
                    ImportedAt    = DateTime.Now,
                    Entries       = packEntries,
                });
            }

            if (presetsImported > 0)
            {
                UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            this.SaveCommonSettings("GeneralSettings", Settings);
            if (!string.IsNullOrEmpty(_activeCarId)) ReloadActiveCarOverrideFromStore();

            SimHub.Logging.Current.Info(
                $"[Trueforce] Selective pack import from {path}: {presetsImported}/{includedGamePresets.Count} game preset(s), {carsImported}/{includedCarPresets.Count} car preset(s), {carDefaultsSet} car default(s) set.");
            return new ImportPackResult
            {
                PresetsImported = presetsImported,
                CarsImported    = carsImported,
                Author          = packAuthor,
                Description     = packDesc,
                AuthorVersion   = packVer,
            };
        }

        /// <summary>Walk an installed pack's entries and bind each one as the
        /// active default (game preset for its game key(s), car preset for its
        /// carId). Game entries without a DefaultForGames hint are counted as
        /// skipped. existing defaults are overwritten silently and tallied so
        /// the caller's summary toast can surface what changed.</summary>
        public SetDefaultsSummary SetPackAsDefaults(InstalledPack pack)
        {
            var summary = new SetDefaultsSummary();
            if (pack?.Entries == null) return summary;

            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.CarDefaults  == null) Settings.CarDefaults  = new Dictionary<string, string>();

            string folder = UserPresets.CurrentFolder;
            bool activeCarBindingChanged = false;

            foreach (var e in pack.Entries)
            {
                if (e == null) continue;
                if (e.Kind == InstalledPackEntry.KindCar)
                {
                    if (string.IsNullOrEmpty(e.CarId) || string.IsNullOrEmpty(e.PresetName)) continue;
                    if (Settings.CarDefaults.TryGetValue(e.CarId, out string prev)
                        && !string.Equals(prev, e.PresetName, StringComparison.Ordinal))
                        summary.CarDefaultsOverwritten++;
                    Settings.CarDefaults[e.CarId] = e.PresetName;
                    BuiltinPresetWriter.SetCarDefault(folder, e.CarId, e.PresetName);
                    summary.CarDefaultsSet++;
                    if (string.Equals(_activeCarId, e.CarId, StringComparison.Ordinal))
                        activeCarBindingChanged = true;
                }
                else if (e.Kind == InstalledPackEntry.KindGame)
                {
                    // Game preset → game-default binding needs to know which
                    // game(s) the pack intends this preset to be the default
                    // for. That hint lives on DefaultForGames (populated from
                    // the pack manifest when present). When missing we skip
                    // rather than guessing from the user's current
                    // GameDefaultBindings, which would silently overwrite the
                    // pack author's intent.
                    if (e.DefaultForGames == null || e.DefaultForGames.Count == 0 || string.IsNullOrEmpty(e.Name))
                    {
                        summary.GamePresetsSkipped++;
                        continue;
                    }
                    foreach (var gameKey in e.DefaultForGames)
                    {
                        if (string.IsNullOrEmpty(gameKey)) continue;
                        if (Settings.GameDefaults.TryGetValue(gameKey, out string prev)
                            && !string.Equals(prev, e.Name, StringComparison.Ordinal))
                            summary.GameDefaultsOverwritten++;
                        Settings.GameDefaults[gameKey] = e.Name;
                        BuiltinPresetWriter.SetGameDefault(folder, gameKey, e.Name);
                        summary.GameDefaultsSet++;
                    }
                }
            }

            this.SaveCommonSettings("GeneralSettings", Settings);
            if (activeCarBindingChanged) ReloadActiveCarOverrideFromStore();
            return summary;
        }

        /// <summary>Destructively remove an installed pack: delete each entry
        /// whose current on-disk hash still matches its install-time
        /// BaselineHash (so the user hasn't edited it), leave entries the user
        /// touched in place, and drop the pack record either way. Entries
        /// imported before BaselineHash was tracked are conservatively kept so
        /// we never delete data we can't reason about.</summary>
        public RemovePackSummary RemovePack(InstalledPack pack)
        {
            var summary = new RemovePackSummary();
            if (pack == null) return summary;

            string folder = UserPresets.CurrentFolder;
            bool gameTouched = false, carTouched = false;

            if (pack.Entries != null)
            {
                foreach (var e in pack.Entries)
                {
                    if (e == null) continue;
                    if (e.Kind == InstalledPackEntry.KindGame)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        if (IsEntrySafeToDelete(e, gameName: e.Name, isCar: false))
                        {
                            try { BuiltinPresetWriter.DeleteGame(folder, e.Name); summary.EntriesDeleted++; gameTouched = true; }
                            catch (Exception ex)
                            {
                                SimHub.Logging.Current.Warn($"[Trueforce] Pack remove: delete of game preset '{e.Name}' failed: {ex.Message}");
                                summary.EntriesKept++;
                            }
                        }
                        else summary.EntriesKept++;
                    }
                    else if (e.Kind == InstalledPackEntry.KindCar)
                    {
                        if (string.IsNullOrEmpty(e.CarId) || string.IsNullOrEmpty(e.PresetName)) continue;
                        if (IsEntrySafeToDelete(e, gameName: null, isCar: true))
                        {
                            try { _carStore?.Delete(e.CarId, e.PresetName); summary.EntriesDeleted++; carTouched = true; }
                            catch (Exception ex)
                            {
                                SimHub.Logging.Current.Warn($"[Trueforce] Pack remove: delete of car preset '{e.CarId}/{e.PresetName}' failed: {ex.Message}");
                                summary.EntriesKept++;
                            }
                        }
                        else summary.EntriesKept++;
                    }
                }
            }

            _installedPacks?.RemovePack(pack);

            if (gameTouched)
            {
                UserPresets.Reload();
                RebuildPresetCacheFromFolders();
            }
            if (carTouched && !string.IsNullOrEmpty(_activeCarId)) ReloadActiveCarOverrideFromStore();

            return summary;
        }

        // True iff RemovePack should delete this entry. Safe = we cannot prove
        // the user edited it. Unsafe = we CAN prove it (hash recorded AND
        // current file hash differs). Pre-this-work entries with no recorded
        // BaselineHash are treated as safe because the user explicitly invoked
        // destructive remove; refusing to delete them would silently leave the
        // pack's contents behind on the every-pack-installed-before-this-build
        // case, which is what we hit when the feature first shipped. The
        // hash-protected safety net still works for any pack imported AFTER
        // the BaselineHash code went live, which is the only window where we
        // can detect a user edit at all.
        private bool IsEntrySafeToDelete(InstalledPackEntry e, string gameName, bool isCar)
        {
            if (e == null) return false;
            if (string.IsNullOrEmpty(e.BaselineHash)) return true;
            try
            {
                string path;
                if (isCar)
                {
                    path = _carStore?.FindCarFile(e.CarId, e.PresetName);
                }
                else
                {
                    path = BuiltinPresetWriter.GetGamePresetPath(UserPresets.CurrentFolder, gameName);
                }
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return true;
                string current = InstalledPacksStore.ComputeContentHash(System.IO.File.ReadAllText(path));
                return string.Equals(current, e.BaselineHash, StringComparison.Ordinal);
            }
            catch
            {
                // If we can't even read the file, fall back to "safe to delete"
                // so the destructive button keeps doing what it advertises.
                // BuiltinPresetWriter.DeleteGame / CarPresetStore.Delete are
                // both no-op-safe on missing files.
                return true;
            }
        }

        /// <summary>Reload the installed-packs registry from disk. Wired into
        /// PackManagerWindow so it always sees current state without holding
        /// onto a stale cached file across reopens.</summary>
        public InstalledPacksFile LoadInstalledPacks() => _installedPacks?.Load() ?? new InstalledPacksFile();

        // Stamp the baseline hash on a freshly-saved car preset. CarPresetStore.Save
        // folds existing on-disk attribution into the serialized output, so the
        // input override JSON wouldn't match what landed on disk; reading the
        // file back is the only way to capture an accurate baseline.
        private string TryHashCarPresetFile(string carId, string presetName)
        {
            try
            {
                string path = _carStore?.FindCarFile(carId, presetName);
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                return InstalledPacksStore.ComputeContentHash(System.IO.File.ReadAllText(path));
            }
            catch { return null; }
        }

        // Replace anything not-safe-in-a-zip-entry-name with '_'. Zip handles
        // most chars fine, but '/' and '\\' would create unintended directory
        // structure and a few oddballs trip up some unzip tools.
        private static string SanitizeForZip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                char c = arr[i];
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|' || c < ' ')
                    arr[i] = '_';
            }
            return new string(arr);
        }

        private static void WriteJsonZipEntry(System.IO.Compression.ZipArchive zip, string entryName, object obj)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new System.IO.StreamWriter(s))
            {
                w.Write(Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented));
            }
        }

        private static T ReadJsonZipEntry<T>(System.IO.Compression.ZipArchiveEntry entry) where T : class
        {
            try
            {
                using (var s = entry.Open())
                using (var r = new System.IO.StreamReader(s))
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(r.ReadToEnd());
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- export / import ----------

        public void ExportSettings(string path)
        {
            if (Settings == null) return;
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(path, json);
            SimHub.Logging.Current.Info($"[Trueforce] Settings exported to {path}.");
        }

        /// <summary>Bundle ALL user-owned Trueforce data into one zip archive
        /// for moving to another machine. Contents:
        ///   GeneralSettings.json (the plugin's settings JSON)
        ///   user/                 (every preset, default, and metadata file
        ///                          the user owns; factory/ is intentionally
        ///                          excluded because the plugin ships with it).
        /// No customization, no per-preset metadata dialog. Conceptually
        /// different from ExportPack / ExportSinglePreset: backup is a
        /// snapshot of state, not a curated share.</summary>
        public (int fileCount, long totalBytes) BackupAllToZip(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath)) return (0, 0);
            int fileCount = 0;
            long totalBytes = 0;

            using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                // 1) Plugin settings JSON. Re-serialize live Settings so the
                // backup matches the running state, not whatever's on disk
                // (which lags an unflushed in-memory change).
                if (Settings != null)
                {
                    string settingsJson = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
                    var entry = zip.CreateEntry("GeneralSettings.json", System.IO.Compression.CompressionLevel.Optimal);
                    using (var ws = entry.Open())
                    using (var sw = new System.IO.StreamWriter(ws, new System.Text.UTF8Encoding(false)))
                        sw.Write(settingsJson);
                    fileCount++;
                    totalBytes += settingsJson.Length;
                }

                // 2) Everything under user/ — preset files, defaults files,
                // installed-packs sidecar, README. Skip .cleanup-<timestamp>/
                // backup directories produced by the legacy-builtin migration.
                string userRoot = UserPresets.CurrentFolder;
                if (!string.IsNullOrEmpty(userRoot) && System.IO.Directory.Exists(userRoot))
                {
                    foreach (var path in System.IO.Directory.GetFiles(userRoot, "*", System.IO.SearchOption.AllDirectories))
                    {
                        if (path.IndexOf($"{System.IO.Path.DirectorySeparatorChar}.cleanup-", StringComparison.Ordinal) >= 0)
                            continue;
                        string rel = "user/" + path.Substring(userRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                        try
                        {
                            var bytes = System.IO.File.ReadAllBytes(path);
                            var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                            using (var ws = entry.Open())
                                ws.Write(bytes, 0, bytes.Length);
                            fileCount++;
                            totalBytes += bytes.LongLength;
                        }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn($"[Trueforce] Backup: couldn't add '{path}': {ex.Message}");
                        }
                    }
                }
            }
            SimHub.Logging.Current.Info($"[Trueforce] Backup wrote {fileCount} entries ({totalBytes} bytes) to {zipPath}.");
            return (fileCount, totalBytes);
        }

        /// <summary>Replace ALL user-owned Trueforce data with the contents of
        /// a backup zip produced by BackupAllToZip. Wipes the user/ folder
        /// (after backing it up to a sibling .pre-restore-<timestamp>/ folder
        /// for safety), extracts the archive in its place, restores
        /// GeneralSettings.json into Settings, and reloads everything from
        /// disk. Returns the number of files restored.
        ///
        /// Destructive: the caller must have confirmed with the user.</summary>
        public int RestoreAllFromZip(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath) || !System.IO.File.Exists(zipPath)) return 0;

            string userRoot = UserPresets.CurrentFolder;
            if (string.IsNullOrEmpty(userRoot)) throw new InvalidOperationException("User library folder is not set.");
            // Trim any trailing path separator so the .pre-restore-<stamp>
            // sibling computation produces a sibling rather than a child path
            // (e.g. "C:\path\user\.pre-restore-..." instead of the intended
            // "C:\path\user.pre-restore-...").
            userRoot = userRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            // Snapshot live Settings + the previous-user-folder path BEFORE
            // mutating disk, so we can roll back to byte-identical pre-state
            // if extraction or ImportSettings fails. The confirmation dialog
            // promised a safety net; this is the implementation.
            TrueforceSettings preState = null;
            try
            {
                preState = Newtonsoft.Json.JsonConvert.DeserializeObject<TrueforceSettings>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(Settings));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Restore: couldn't snapshot live Settings (will continue without rollback safety net): {ex.Message}");
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safeDir = userRoot + ".pre-restore-" + stamp;
            bool movedAside = false;
            if (System.IO.Directory.Exists(userRoot))
            {
                try
                {
                    System.IO.Directory.Move(userRoot, safeDir);
                    movedAside = true;
                }
                catch (Exception ex)
                {
                    // Abort: continuing with extract would overwrite live
                    // data without a safety net, breaking the confirmation
                    // dialog's promise.
                    throw new InvalidOperationException(
                        $"Could not move the existing user library out of the way ({ex.Message}). Close any open file handles in the folder and try again. No changes were made.", ex);
                }
            }
            System.IO.Directory.CreateDirectory(userRoot);

            int restored = 0;
            string settingsJson = null;
            try
            {
                using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                        string name = entry.FullName.Replace('\\', '/');
                        if (name == "GeneralSettings.json")
                        {
                            using (var es = entry.Open())
                            using (var sr = new System.IO.StreamReader(es))
                                settingsJson = sr.ReadToEnd();
                            continue;
                        }
                        if (name.StartsWith("user/", StringComparison.Ordinal))
                        {
                            string rel = name.Substring("user/".Length);
                            string dest = System.IO.Path.Combine(userRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                            using (var es = entry.Open())
                            using (var ws = System.IO.File.Create(dest))
                                es.CopyTo(ws);
                            restored++;
                        }
                    }
                }

                // Apply settings JSON via the existing ImportSettings path so
                // all the live-effect plumbing + re-cache + active-car reload
                // runs. Any failure here will be caught + rolled back below.
                if (!string.IsNullOrEmpty(settingsJson))
                {
                    string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tf4all-restore-{stamp}.json");
                    System.IO.File.WriteAllText(tmp, settingsJson);
                    try { ImportSettings(tmp); }
                    finally { try { System.IO.File.Delete(tmp); } catch { } }

                    // The extracted user/ is already in post-V3 shape (every
                    // user/games file is a wrapped or bare snapshot, every
                    // car file is a CarPresetFile, every defaults file is in
                    // the new location). Force-flag the migrations as done so
                    // a backup from an older plugin version doesn't trigger
                    // the cleanup-migrations again on next Init and silently
                    // archive presets the user considers theirs.
                    if (Settings != null)
                    {
                        Settings.PresetsMigratedV2      = true;
                        Settings.CarsMigratedV2         = true;
                        Settings.LegacyBuiltinsCleanedV1 = true;
                        Settings.FoldersRestructuredV3  = true;
                    }
                }
                else
                {
                    UserPresets.Reload();
                    RebuildPresetCacheFromFolders();
                    LoadAndMigrateCarPresets();
                }
            }
            catch (Exception ex)
            {
                // Roll back: delete the partial extract, move the original
                // user folder back, restore the live Settings snapshot.
                try
                {
                    if (System.IO.Directory.Exists(userRoot)) System.IO.Directory.Delete(userRoot, recursive: true);
                }
                catch (Exception delEx)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Restore rollback: couldn't delete partial extract: {delEx.Message}");
                }
                if (movedAside && System.IO.Directory.Exists(safeDir))
                {
                    try { System.IO.Directory.Move(safeDir, userRoot); }
                    catch (Exception mvEx)
                    {
                        SimHub.Logging.Current.Warn($"[Trueforce] Restore rollback: couldn't move original user library back: {mvEx.Message}. Original is preserved at {safeDir}.");
                    }
                }
                if (preState != null)
                {
                    Settings = preState;
                    try
                    {
                        UserPresets.Reload();
                        RebuildPresetCacheFromFolders();
                        LoadAndMigrateCarPresets();
                    }
                    catch { /* swallow; the user is being shown an error already */ }
                }
                throw new InvalidOperationException($"Restore failed and was rolled back: {ex.Message}", ex);
            }

            SimHub.Logging.Current.Info($"[Trueforce] Restore extracted {restored} user file(s) from {zipPath} (previous library archived at {safeDir}).");
            return restored;
        }

        /// <summary>Replace settings from a JSON file; live effects are re-derived from the new settings.</summary>
        public void ImportSettings(string path)
        {
            string json = System.IO.File.ReadAllText(path);
            var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<TrueforceSettings>(json);
            if (imported == null) throw new System.IO.InvalidDataException("File did not contain valid TrueforceSettings JSON.");
            Settings = imported;
            _mixer.MasterGain = Settings.MasterGain;
            if (_audio != null)
            {
                _audio.Enabled          = Settings.AudioCapture.Enabled;
                _audio.Gain             = Settings.AudioCapture.Gain;
                _audio.LowpassCutoffHz  = Settings.AudioCapture.LowpassCutoffHz;
                _audio.HighpassCutoffHz = Settings.AudioCapture.HighpassCutoffHz;
            }
            // A legacy backup may include the old dict-based Presets / GameDefaults.
            // Force the one-time migration to run again so they move into files.
            if (!Settings.PresetsMigratedV2
                && ((Settings.Presets?.Count ?? 0) > 0 || (Settings.GameDefaults?.Count ?? 0) > 0))
            {
                MigrateLegacyUserPresetsToFolder();
                Settings.PresetsMigratedV2 = true;
            }
            // Rebuild the runtime cache from the (potentially updated) folders.
            RebuildPresetCacheFromFolders();
            ApplyActiveCarOverride();
            SimHub.Logging.Current.Info($"[Trueforce] Settings imported from {path}.");
        }

        // ---------- capture targeting ----------

        // Curated exe-basename → friendly label map for known sims. AC's
        // "acs" exe is too short for the fuzzy fallback to match safely
        // (collisions with random 3-letter process names), so the curated
        // dict stays the primary lookup. The fuzzy fallback handles unknown
        // games where the exe name resembles the SimHub GameName, and a
        // per-game user override (Settings.AudioCaptureExeOverrides) covers
        // anything neither catches.
        private static readonly Dictionary<string, string> ExeLabels = BuildExeLabels(new Dictionary<string, string[]>
        {
            { "AssettoCorsa",             new[] { "AssettoCorsa", "acs" } },
            { "AssettoCorsaCompetizione", new[] { "AC2-Win64-Shipping", "acc" } },
            { "iRacing",                  new[] { "iRacingSim64DX11", "iRacingSim", "iracing" } },
            { "RaceRoomRacingExperience", new[] { "RRRE64", "RRRE" } },
            { "F1_22",                    new[] { "F1_22", "F1_22_dx12" } },
            { "F1_23",                    new[] { "F1_23", "F1_23_dx12" } },
            { "AutomobilistaII",          new[] { "AMS2", "AMS2AVX" } },
            // Forza Horizon: SimHub's GameName ("FH4"/"FH5"/"FH6") is too short
            // for the >= 4 char fuzzy-match guard, so map the canonical exe
            // names explicitly. FH4/FH5 confirmed; FH6 is an educated guess
            // by Playground's naming pattern and will be corrected once the
            // retail build ships.
            { "FH4",                      new[] { "ForzaHorizon4" } },
            { "FH5",                      new[] { "ForzaHorizon5" } },
            { "FH6",                      new[] { "ForzaHorizon6" } },
        });

        private static Dictionary<string, string> BuildExeLabels(Dictionary<string, string[]> games)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in games)
                foreach (var exe in kv.Value)
                    d[exe] = kv.Key;
            return d;
        }

        // SimHub stores user-added "Custom Game" profiles in CustomGames.json
        // under the SimHub install dir's PluginsData folder. Each entry has
        // Code (the "Custom_<guid>" string SimHub reports as data.GameName),
        // Name (the friendly name the user typed), and ProcessNames
        // (comma-separated exe basenames the user configured for detection).
        // We pull the same data SimHub uses for game detection, the user
        // doesn't have to configure their exe in two places.
        private sealed class CustomGameInfo
        {
            public string Name;
            public string[] ProcessNames;     // basenames (no .exe), case-insensitive lookup
        }
        private Dictionary<string, CustomGameInfo> _customGamesCache;
        private DateTime _customGamesCacheLoadedAt;
        private const int CustomGamesCacheStaleSeconds = 60;

        /// <summary>Resolve a SimHub Custom_xxx GameName to its user-configured
        /// friendly name and exe list. Returns null for non-custom games or
        /// when the entry isn't present. Cached for 60 s; cache is bypassed
        /// when the requested code isn't already cached, so newly-added
        /// custom games are picked up within one capture tick.</summary>
        private CustomGameInfo TryGetCustomGameInfo(string gameCode)
        {
            if (string.IsNullOrEmpty(gameCode)
                || !gameCode.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
                return null;

            var now = DateTime.UtcNow;
            bool cacheStale = _customGamesCache == null
                              || (now - _customGamesCacheLoadedAt).TotalSeconds > CustomGamesCacheStaleSeconds;
            bool cacheMissForCode = _customGamesCache != null && !_customGamesCache.ContainsKey(gameCode);
            if (cacheStale || cacheMissForCode) LoadCustomGames();

            return _customGamesCache != null && _customGamesCache.TryGetValue(gameCode, out var info) ? info : null;
        }

        private void LoadCustomGames()
        {
            var newCache = new Dictionary<string, CustomGameInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Plugin DLL lives in the SimHub install dir, so this resolves
                // wherever SimHub is installed.
                string simHubDir = Path.GetDirectoryName(typeof(TrueforcePlugin).Assembly.Location);
                string path = Path.Combine(simHubDir, "PluginsData", "CustomGames.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
                    foreach (var item in arr)
                    {
                        string code = item["Code"]?.ToString();
                        if (string.IsNullOrEmpty(code)) continue;
                        string name = item["Name"]?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(name)) name = code;
                        string procStr = item["ProcessNames"]?.ToString() ?? "";
                        var procs = new List<string>();
                        foreach (var raw in procStr.Split(','))
                        {
                            string p = raw.Trim();
                            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                p = p.Substring(0, p.Length - 4);
                            if (!string.IsNullOrEmpty(p)) procs.Add(p);
                        }
                        newCache[code] = new CustomGameInfo
                        {
                            Name = name,
                            ProcessNames = procs.ToArray(),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Trueforce] Could not read CustomGames.json: {ex.Message}");
            }
            _customGamesCache = newCache;
            _customGamesCacheLoadedAt = DateTime.UtcNow;
        }

        /// <summary>True if <paramref name="procName"/> looks like a sensible
        /// match for SimHub's <paramref name="gameName"/>. Compares after
        /// stripping non-alphanumeric chars (so "NASCAR 25" matches "NASCAR25"
        /// and "Nascar25-Win64-Shipping"), case-insensitive, with substring
        /// containment in either direction. Empty inputs never match.</summary>
        private static bool ProcessMatchesGameName(string procName, string gameName)
        {
            string normProc = NormalizeForMatch(procName);
            string normGame = NormalizeForMatch(gameName);
            // Require at least 4 chars on the shorter side so generic 1-3
            // letter names ("F1") don't pull in wildcards across the system.
            int min = Math.Min(normProc.Length, normGame.Length);
            if (min < 4) return false;
            return normProc.IndexOf(normGame, StringComparison.OrdinalIgnoreCase) >= 0
                || normGame.IndexOf(normProc, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // The Process handle for the game we're currently capturing (or null).
        // We hold this so per-tick alive-checks use HasExited (cheap, uses the
        // existing handle) instead of re-walking the process table.
        private Process _capturedProcess;

        private void CapturePollLoop()
        {
            // Initial settle delay so plugin Init can finish on the SimHub side
            // before we start hammering the process table.
            Thread.Sleep(500);
            while (!_shuttingDown)
            {
                CaptureTick();
                // 1 Hz polling. Sleep in small slices so shutdown is responsive.
                for (int i = 0; i < 10 && !_shuttingDown; i++)
                    Thread.Sleep(100);
            }
        }

        private void CaptureTick()
        {
            if (_shuttingDown || _audio == null) return;

            try
            {
                // Fast path: if we already have a captured process, just check
                // whether it's still alive. No process-table scan.
                if (_capturedProcess != null)
                {
                    bool stillAlive = false;
                    try { stillAlive = !_capturedProcess.HasExited; } catch { /* invalid handle */ }
                    if (stillAlive) return;

                    // Process exited, tear down and fall through to the scan.
                    SimHub.Logging.Current.Info($"[Trueforce] Captured process {_capturedProcess.Id} exited; releasing.");
                    try { _capturedProcess.Dispose(); } catch { }
                    _capturedProcess = null;
                    _audio.Stop();
                    _helperHost?.SetTargetPid(0);
                }

                // Scan path: walk the process table once. Match priority:
                //   1. Per-game user override (Settings.AudioCaptureExeOverrides)
                //     , highest priority so users can fix any miss.
                //   2. Curated exe→label dict (known-quirky exes like ACC's
                //      "AC2-Win64-Shipping" or AC's short "acs").
                //   3. SimHub Custom-game ProcessNames (read from
                //      CustomGames.json, same source SimHub uses for game
                //      detection, so the user only configures their exe once).
                //   4. Fuzzy match against the active GameName (or the
                //      Custom-game friendly Name) so non-custom games whose
                //      exe naturally resembles their SimHub name auto-resolve.
                // Disposes every Process we don't keep so handles aren't leaked.
                Process keep = null;
                string label = null;
                string activeGame = _activeGame;   // snapshot for thread safety
                string overrideExe = null;
                if (Settings?.AudioCaptureExeOverrides != null
                    && !string.IsNullOrEmpty(activeGame)
                    && Settings.AudioCaptureExeOverrides.TryGetValue(activeGame, out var ovr))
                    overrideExe = ovr;
                CustomGameInfo customInfo = TryGetCustomGameInfo(activeGame);
                HashSet<string> customProcs = null;
                if (customInfo?.ProcessNames != null && customInfo.ProcessNames.Length > 0)
                    customProcs = new HashSet<string>(customInfo.ProcessNames, StringComparer.OrdinalIgnoreCase);
                string fuzzyTarget = !string.IsNullOrEmpty(customInfo?.Name) ? customInfo.Name : activeGame;
                string overrideLabel = customInfo?.Name ?? activeGame;

                Process[] all;
                try { all = Process.GetProcesses(); }
                catch { all = Array.Empty<Process>(); }

                foreach (var p in all)
                {
                    if (keep != null) { p.Dispose(); continue; }
                    if (overrideExe != null
                        && p.ProcessName.Equals(overrideExe, StringComparison.OrdinalIgnoreCase))
                    {
                        keep = p;
                        label = overrideLabel;
                        continue;
                    }
                    if (ExeLabels.TryGetValue(p.ProcessName, out string lbl))
                    {
                        keep = p;
                        label = lbl;
                        continue;
                    }
                    if (customProcs != null && customProcs.Contains(p.ProcessName))
                    {
                        keep = p;
                        label = customInfo.Name;
                        continue;
                    }
                    if (!string.IsNullOrEmpty(fuzzyTarget)
                        && ProcessMatchesGameName(p.ProcessName, fuzzyTarget))
                    {
                        keep = p;
                        label = fuzzyTarget;
                        continue;
                    }
                    p.Dispose();
                }

                if (keep == null)
                {
                    _captureStatus = "Idle (no supported game running)";
                    return;
                }

                _capturedProcess = keep;
                _audio.Start(keep.Id);
                _helperHost?.SetTargetPid(keep.Id);
                _captureStatus = $"Capturing {label} (PID {keep.Id})";
                SimHub.Logging.Current.Info($"[Trueforce] {_captureStatus}.");
            }
            catch (Exception ex)
            {
                _captureStatus = $"Capture error: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Capture retarget failed", ex);
            }
        }
        // ---------- producer ----------

        // Float-space silence floor. Samples with |v| < this are zeroed so the
        // u16 conversion produces exactly 0x8000, TrueforceDevice's silence
        // detection requires exact-center samples to choose the keepalive packet
        // shape. ~3e-4 corresponds to ±10 LSB out of 32767, well below any
        // perceptible content but above floating-point noise.
        private const float SilenceFloor = 3e-4f;

        // Layered sidechain ducking. Effects sit in priority tiers; an effect
        // is ducked by the strongest activity at a STRICTLY HIGHER tier.
        // Same-tier and lower-tier effects never duck it.
        //
        //   L3  ABS, gear shift, collision        (top: sharp momentary alerts;
        //                                           sources only, never ducked)
        //   L2  rev limiter, pit limiter          (mode buzzes; duck L0/L1,
        //                                           ducked by L3)
        //   L1  road feel, traction loss, DRS hum (duck L0, ducked by L2/L3)
        //   L0  engine pulse, captured audio      (bottom: ducked by anything)
        //
        // So: a curb (L1) ducks the engine; a gear shift (L3) ducks road feel
        // and the rev-limiter buzz; the rev-limiter buzz (L2) ducks engine +
        // road feel but is itself ducked by ABS / shifts. Each tier of TARGETS
        // gets its own attack/release-smoothed multiplier. DRS's activation
        // chirp ignores ducking by design (handled inside DrsEffect); only its
        // sustained hum is ducked here (treated as L1).
        private float _duckL0 = 1.0f;   // engine + audio
        private float _duckL1 = 1.0f;   // road feel + traction + DRS hum
        private float _duckL2 = 1.0f;   // rev + pit limiter

        // Airborne duck envelope. Separate, orthogonal stage from the sidechain
        // tiers above: it ramps toward (1 - Reduction) while the car is in the
        // air and back to 1 on landing, and is folded multiplicatively into the
        // chosen targets' DuckMultiplier so airborne + sidechain ducking stack.
        private float _duckAir = 1.0f;

        private static float SmoothDuck(float current, float target, float attackMs, float releaseMs)
        {
            // Fast attack (duck quickly when an event hits), slow release.
            // dt ≈ 1 ms (producer pushes ~1 batch/ms); alpha = 1 - exp(-dt/tau).
            float tau   = (target < current) ? attackMs : releaseMs;
            float alpha = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tau)));
            return current * (1f - alpha) + target * alpha;
        }

        private void UpdateDucking()
        {
            // Max activity at each tier.
            double l1 = 0, l2 = 0, l3 = 0;
            if (RoadBumps    != null) l1 = Math.Max(l1, RoadBumps.ActivityLevel);
            if (TractionLoss != null) l1 = Math.Max(l1, TractionLoss.ActivityLevel);
            if (Drs          != null) l1 = Math.Max(l1, Drs.ActivityLevel);
            if (RevLimiter   != null) l2 = Math.Max(l2, RevLimiter.ActivityLevel);
            if (PitLimiter   != null) l2 = Math.Max(l2, PitLimiter.ActivityLevel);
            if (AbsClick     != null) l3 = Math.Max(l3, AbsClick.ActivityLevel);
            if (GearShift    != null) l3 = Math.Max(l3, GearShift.ActivityLevel);
            if (Collision    != null) l3 = Math.Max(l3, Collision.ActivityLevel);

            // Strongest activity strictly above each target tier.
            double above0 = Math.Max(l1, Math.Max(l2, l3));   // ducks L0 (engine/audio)
            double above1 = Math.Max(l2, l3);                  // ducks L1 (road/traction/DRS hum)
            double above2 = l3;                                 // ducks L2 (rev/pit limiter)

            float depth     = Settings?.DuckDepth     ?? 0.5f;
            float attackMs  = Settings?.DuckAttackMs  ?? 5.0f;
            float releaseMs = Settings?.DuckReleaseMs ?? 80.0f;

            float t0 = (float)Math.Max(0.0, 1.0 - depth * above0);
            float t1 = (float)Math.Max(0.0, 1.0 - depth * above1);
            float t2 = (float)Math.Max(0.0, 1.0 - depth * above2);

            _duckL0 = SmoothDuck(_duckL0, t0, attackMs, releaseMs);
            _duckL1 = SmoothDuck(_duckL1, t1, attackMs, releaseMs);
            _duckL2 = SmoothDuck(_duckL2, t2, attackMs, releaseMs);

            // Airborne stage. Ramp the envelope toward (1 - Reduction) while
            // airborne, back to 1 on landing, then fold it into each target the
            // user opted in. Multiplicative on top of the sidechain values so
            // the two duckers compose. Reuses the duck attack/release times
            // (fast in on takeoff, smooth out on touchdown).
            bool  airActive = Airborne != null && Airborne.AirborneActive;
            float airReduce = Airborne?.Reduction ?? 0f;
            float airTarget = airActive ? Math.Max(0f, 1f - airReduce) : 1f;
            _duckAir = SmoothDuck(_duckAir, airTarget, attackMs, releaseMs);

            float a0 = _duckL0, a1 = _duckL1, a2 = _duckL2;
            // Build a per-effect airborne factor: _duckAir if opted in, else 1.
            float fEngine   = (Airborne != null && Airborne.DuckEngine)       ? _duckAir : 1f;
            float fAudio    = (Airborne != null && Airborne.DuckAudio)        ? _duckAir : 1f;
            float fBumps    = (Airborne != null && Airborne.DuckRoadBumps)    ? _duckAir : 1f;
            float fTraction = (Airborne != null && Airborne.DuckTractionLoss) ? _duckAir : 1f;
            float fRev      = (Airborne != null && Airborne.DuckRevLimiter)   ? _duckAir : 1f;
            float fPit      = (Airborne != null && Airborne.DuckPitLimiter)   ? _duckAir : 1f;
            float fDrs      = (Airborne != null && Airborne.DuckDrs)          ? _duckAir : 1f;
            // L3 alert voices (gear shift, ABS, collision) sit above every
            // sidechain tier so the sidechain never touches their multiplier;
            // the airborne factor is therefore the ONLY thing that ducks them,
            // and their base is 1.0.
            float fShift    = (Airborne != null && Airborne.DuckGearShift)    ? _duckAir : 1f;
            float fAbs      = (Airborne != null && Airborne.DuckAbs)          ? _duckAir : 1f;
            float fColl     = (Airborne != null && Airborne.DuckCollision)    ? _duckAir : 1f;

            if (EnginePulse  != null) EnginePulse.DuckMultiplier   = a0 * fEngine;
            if (_audio       != null) _audio.DuckMultiplier        = a0 * fAudio;
            if (RoadBumps    != null) RoadBumps.DuckMultiplier      = a1 * fBumps;
            if (TractionLoss != null) TractionLoss.DuckMultiplier   = a1 * fTraction;
            if (Drs          != null) Drs.SustainedDuckMultiplier   = a1 * fDrs;
            if (RevLimiter   != null) RevLimiter.DuckMultiplier     = a2 * fRev;
            if (PitLimiter   != null) PitLimiter.DuckMultiplier     = a2 * fPit;
            if (GearShift    != null) GearShift.DuckMultiplier      = fShift;
            if (AbsClick     != null) AbsClick.DuckMultiplier       = fAbs;
            if (Collision    != null) Collision.DuckMultiplier      = fColl;
        }

        private void ProducerLoop()
        {
            float[] buf = new float[BatchSamples];

            while (!_shuttingDown)
            {
                // Master disable: skip rendering entirely. The wheel was told
                // to Stop in SetPluginEnabled, so it's running on native FFB.
                // Sleep ~the duration of one batch (4 samples × 0.25 ms) before
                // re-checking, to avoid a hot spin.
                if (Settings != null && !Settings.PluginEnabled)
                {
                    Thread.Sleep(20);
                    continue;
                }

                // No device right now (not yet attached, or torn down while
                // the recovery watchdog re-attaches). Rendering would just be
                // discarded by the null PushFloats below, so idle instead of
                // hot-spinning the CPU for the seconds/minutes the wheel may
                // be gone. The watchdog flips _device back and we resume.
                if (_device == null)
                {
                    Thread.Sleep(20);
                    continue;
                }

                // Auto-ratchet check (cheap when the per-second window hasn't
                // elapsed). Fires the ring-bumped event on this thread; UI
                // marshals to its own thread for the modal.
                try { CheckAutoRatchet(); } catch { }

                // Defense-in-depth: catch any exception from ducking, render,
                // or an effect's RenderAdd so a single bad frame (NaN, future
                // regression) can't kill the producer and silently mute the
                // wheel. Logged with a hot-path-safe rate limit.
                try
                {
                    UpdateDucking();
                    _mixer.Render(buf, BatchSamples);
                    for (int i = 0; i < BatchSamples; i++)
                    {
                        float v = buf[i];
                        if (v < SilenceFloor && v > -SilenceFloor) buf[i] = 0f;
                    }
                }
                catch (Exception ex)
                {
                    LogProducerError("render", ex);
                    Array.Clear(buf, 0, BatchSamples);
                }
                try
                {
                    _device?.PushFloats(buf, BatchSamples);
                }
                catch
                {
                    break;
                }

                // Bank earned seat time for the one-and-done word-of-mouth
                // prompt. Cheap: one Stopwatch read + a gate; only writes the
                // settings file once every StreamFlushIntervalSeconds.
                try { AccumulateStreamingTime(); } catch { }
            }
        }

        // Producer-loop error rate limiter: log at most one render exception
        // per 5 seconds so a sustained bad-frame source doesn't spam the log
        // and stall the producer on I/O.
        private long _lastProducerErrTicks;
        private void LogProducerError(string phase, Exception ex)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long sinceTicks = now - _lastProducerErrTicks;
            if (_lastProducerErrTicks != 0 && sinceTicks < System.Diagnostics.Stopwatch.Frequency * 5)
                return;
            _lastProducerErrTicks = now;
            try
            {
                SimHub.Logging.Current.Error(
                    $"[Trueforce] producer {phase} error (rate-limited 1/5s): {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        // Polled from DataUpdate's hot path. Detects a missing / faulted
        // device and re-attaches it transparently. Inline cost is just the
        // gating checks; the blocking bring-up (HID open + ~136 ms init
        // sequence + tap spawn) is offloaded to the thread pool so SimHub's
        // tick never stalls. Single-flight via _recoveryInProgress.
        private void MaybeRecoverDevice()
        {
            if (_shuttingDown || _recoveryInProgress) return;

            var d = _device;
            bool needsRecovery = d == null || d.StreamFaulted;
            if (!needsRecovery) return;

            // G HUB holds the wheel's HID exclusively; opening would just
            // fail. Skip while it's running; the first tick after the user
            // closes G HUB clears this gate and recovery proceeds, so the
            // wheel comes back with no plugin reload.
            if (_isGHubRunning) return;

            long now = Stopwatch.GetTimestamp();
            if (_lastRecoveryAttemptTicks != 0
                && now - _lastRecoveryAttemptTicks < RecoveryIntervalTicks)
                return;
            _lastRecoveryAttemptTicks = now;
            _recoveryInProgress = true;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Dispose the faulted device + its FFB tap (null-safe if
                    // there was no device yet). The plugin-lifetime producer
                    // thread is intentionally left running; it idles while
                    // _device is null and resumes the instant it's back.
                    CleanupDevice();
                    if (_shuttingDown) return;
                    bool ok = TryBringUpDevice();
                    SimHub.Logging.Current.Info(ok
                        ? "[Trueforce] Wheel re-attached; stream resumed."
                        : "[Trueforce] Wheel re-attach failed; will keep retrying.");
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[Trueforce] Wheel re-attach crashed", ex);
                }
                finally
                {
                    _recoveryInProgress = false;
                }
            });
        }

        /// <summary>True when the FFB tap has decoded a game FFB target off
        /// the USB bus within <paramref name="maxAgeMs"/>. Proves the tap is
        /// genuinely mirroring the game's force feedback, not merely
        /// "started", which is what the Diagnostics self-test needs to tell a
        /// live pass-through from a tap that attached but is seeing nothing.
        /// Returns false (not an error) when no FFB-producing game is running;
        /// the caller phrases that case accordingly.</summary>
        public bool FfbTapTargetFresh(int maxAgeMs)
        {
            var tap = _ffbTap;
            return tap != null && tap.TryGetFreshFfbTarget(maxAgeMs).HasValue;
        }

        /// <summary>Active staged device probe for the Diagnostics self-test.
        /// Non-destructive when the device is already healthy (returns a note,
        /// does NOT reopen a working wheel). When the stream is down/faulted
        /// it runs a real bring-up, discovery, then open + init + tap +
        /// stream via TryBringUpDevice, and reports each stage's outcome
        /// using the now-accurate status strings (open/init failures surface
        /// with their exception text via StreamStatus). Blocking (~150 ms for
        /// the init sequence); callers invoke it off the UI thread. Shares
        /// the _recoveryInProgress single-flight with the watchdog so the two
        /// can't run a bring-up at the same time.</summary>
        public string RunActiveDeviceProbe()
        {
            var d = _device;
            if (d != null && !d.StreamFaulted)
                return "Device already healthy: active probe skipped (it never "
                     + "reopens a working wheel). Status lines above are live.";

            if (_isGHubRunning)
                return "Cannot probe: Logitech G HUB is running and holds the "
                     + "wheel's HID. Close G HUB, then run the self-test again.";

            if (_recoveryInProgress)
                return "A re-attach is already in progress. Wait a moment and "
                     + "run the self-test again.";

            _recoveryInProgress = true;
            _lastRecoveryAttemptTicks = Stopwatch.GetTimestamp();
            var sb = new System.Text.StringBuilder();
            try
            {
                var matches = WheelDiscovery.FindAll();
                if (matches.Count == 0)
                {
                    sb.AppendLine("[FAIL] Discovery: no supported wheel on the bus.");
                    sb.Append("        Plug in a G PRO / RS50 / G923 and close G HUB.");
                    return sb.ToString();
                }
                sb.AppendLine($"[OK]   Discovery: {matches[0].Model} "
                    + $"(VID 0x{matches[0].Vid:X4}, PID 0x{matches[0].Pid:X4})");

                CleanupDevice();
                if (_shuttingDown) return "Aborted (plugin shutting down).";

                bool ok = TryBringUpDevice();
                sb.AppendLine((ok ? "[OK]   " : "[FAIL] ")
                    + "Open + init sequence + stream: "
                    + (StreamStatus ?? "(unknown)"));
                sb.AppendLine("       Wheel:  " + (WheelStatus ?? "(unknown)"));
                sb.Append("       FFB tap: " + (FfbTapStatus ?? "(not started)"));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return sb + "\n[FAIL] Probe crashed: " + ex.Message;
            }
            finally
            {
                _recoveryInProgress = false;
            }
        }

        private void CleanupDevice()
        {
            try { _mairaIpc?.Dispose(); } catch { }
            _mairaIpc = null;
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _steeringReader?.Dispose(); } catch { }
            _steeringReader = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        // Persist the user-picked USBPcap path and restart the FFB tap with
        // the new probe. Returns true if the tap is now running. Called from
        // the settings panel's Browse action. The caller has already
        // validated the path; we don't re-validate here.
        public bool ApplyUsbPcapPathOverride(string usbPcapCmdPath)
        {
            if (Settings == null) return false;
            Settings.UsbPcapCmdPathOverride = usbPcapCmdPath ?? "";
            try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            return RestartFfbTap();
        }

        // Resolve the FFB-tap interface+address override in precedence order:
        // env var (debug/testing) > persisted manual picker (Settings) > auto.
        // Returns (null, 0) when neither override is set, which the tap reads
        // as "auto-discover".
        private (string iface, int dev) ResolveUsbPcapOverride()
        {
            string ifaceEnv = Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_INTERFACE");
            if (!string.IsNullOrEmpty(ifaceEnv)
                && int.TryParse(Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_DEVICE"), out var devEnv)
                && devEnv > 0)
            {
                return (ifaceEnv, devEnv);
            }
            if (Settings != null
                && !string.IsNullOrEmpty(Settings.ManualUsbPcapInterface)
                && Settings.ManualUsbPcapDeviceAddress > 0)
            {
                return (Settings.ManualUsbPcapInterface, Settings.ManualUsbPcapDeviceAddress);
            }
            return (null, 0);
        }

        // Persist the user-picked USB device address + USBPcap interface from
        // the manual picker dialog and restart the FFB tap to apply it. Empty
        // iface OR zero address clears the override (= back to auto-discover).
        // Called from SettingsControl's "Pick device manually" dialog.
        public bool ApplyManualUsbPcapDevice(string iface, int deviceAddress, ushort vid = 0, ushort pid = 0)
        {
            if (Settings == null) return false;
            Settings.ManualUsbPcapInterface     = iface ?? "";
            Settings.ManualUsbPcapDeviceAddress = deviceAddress > 0 ? deviceAddress : 0;
            // Remember the pinned device's identity so the tap can follow THIS
            // device to a new address after a replug, never switching wheels.
            Settings.ManualUsbPcapVid           = deviceAddress > 0 ? vid : 0;
            Settings.ManualUsbPcapPid           = deviceAddress > 0 ? pid : 0;
            try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            SimHub.Logging.Current.Info(
                $"[Trueforce] Manual USB device {(deviceAddress > 0 ? $"set to {iface} dev {deviceAddress}" : "cleared")}.");
            return RestartFfbTap();
        }

        // Wire the FFB tap's self-heal callbacks. Shared by both tap-creation
        // sites so they stay consistent.
        // Dev/test (NOFFB access code): makes the tap see the wheel and the
        // game's FFB on the wire but never extract it, so the no-FFB self-heal
        // (-A retry, then the warning) can be exercised on a working wheel.
        private bool _simulateNoFfb;
        public bool DebugToggleSimulateNoFfb()
        {
            _simulateNoFfb = !_simulateNoFfb;
            if (_ffbTap != null) _ffbTap.SimulateNoFfbCapture = _simulateNoFfb;
            if (!_simulateNoFfb) _noFfbCaptureNotice = null;
            SimHub.Logging.Current.Info($"[Trueforce] Simulate-no-FFB-capture {(_simulateNoFfb ? "ON" : "OFF")}.");
            return _simulateNoFfb;
        }

        // Opt-in experimental FFB-capture path, shared by the Diagnostics
        // checkbox and the FFBX access code. Persists Settings.ExperimentalFfbCapture,
        // applies it to the live tap, and re-arms the feature-index resolver so
        // the change takes effect without a SimHub restart. Off = shipped behaviour.
        public void SetExperimentalFfbCapture(bool on)
        {
            if (Settings != null) Settings.ExperimentalFfbCapture = on;
            PersistSettings();
            if (_ffbTap != null)
            {
                _ffbTap.ExperimentalCapture = on;
                _ffbTap.ResetFeatureIndexResolution();
            }
            SimHub.Logging.Current.Info($"[Trueforce] Experimental FFB capture {(on ? "ON" : "OFF")}.");
        }

        public bool DebugToggleExperimentalCapture()
        {
            bool on = !(Settings?.ExperimentalFfbCapture ?? false);
            SetExperimentalFfbCapture(on);
            return on;
        }

        private void WireFfbTapCallbacks(UsbPcapFfbTap tap)
        {
            if (tap == null) return;

            // Re-apply the dev/test no-FFB simulation across tap restarts.
            tap.SimulateNoFfbCapture = _simulateNoFfb;

            // Re-apply the experimental capture opt-in across tap restarts.
            tap.ExperimentalCapture = Settings?.ExperimentalFfbCapture ?? false;

            // Heartbeat for the liveness watchdog: our stream's packets-sent
            // count. Reads the current device each call, so it survives device
            // swaps. While streaming this climbs ~1 kHz.
            tap.SetSendActivityProbe(() => _device?.PacketsSent ?? 0);

            // The tap healed a drifted address (same wheel identity, new USBPcap
            // location after a replug/re-enumeration). If the user has a manual
            // override pinned, persist the corrected address so it survives a
            // restart. Runs on the tap's reader thread; just a settings write,
            // the tap has already retargeted itself, so no restart here.
            tap.OnDeviceRelocated = (iface, addr) =>
            {
                try
                {
                    if (Settings != null && HasManualUsbPcapDevice
                        && (Settings.ManualUsbPcapInterface != iface
                            || Settings.ManualUsbPcapDeviceAddress != addr))
                    {
                        Settings.ManualUsbPcapInterface     = iface ?? "";
                        Settings.ManualUsbPcapDeviceAddress = addr > 0 ? addr : 0;
                        this.SaveCommonSettings("GeneralSettings", Settings);
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Updated saved USB device override to {iface} dev {addr} (wheel re-enumerated).");
                    }
                }
                catch { }
            };

            // The tap is on the right wheel and the game is driving, but no force
            // feedback reaches our capture even in whole-bus mode. Surface it.
            tap.OnNoFfbWarning = msg =>
            {
                // If experimental FFB detection is off, point the user at it:
                // a wheel sending force in a shape the default path doesn't
                // recognize (e.g. RS50 on report 0x12) is exactly this case.
                if (Settings != null && !Settings.ExperimentalFfbCapture)
                    msg += " If your wheel should have force feedback, try turning on 'Enable experimental FFB detection' (Effects tab, under FFB tweaks), then drive a few seconds.";
                _noFfbCaptureNotice = msg;
                SimHub.Logging.Current.Warn($"[Trueforce] {msg}");
            };
        }

        // True when the user has a manual USB-device override active.
        public bool HasManualUsbPcapDevice =>
            Settings != null
            && !string.IsNullOrEmpty(Settings.ManualUsbPcapInterface)
            && Settings.ManualUsbPcapDeviceAddress > 0;

        // Read-only snapshot of where the active tap is currently capturing.
        // Used by the picker to surface "ACTIVE" on the right row, and to
        // include the active device in the list even when a fresh descriptor
        // scan misses it (the tap's USBPcap process can shadow the picker's
        // scan on the same interface).
        public string ActiveFfbTapInterface     => _ffbTap?.CurrentInterface;
        public int    ActiveFfbTapDeviceAddress => _ffbTap?.CurrentDeviceAddress ?? 0;

        // Dispose the FFB tap WITHOUT restarting it. Used by the manual-
        // device picker while it runs its descriptor scan: USBPcap captures
        // from another process on the same interface can prevent injected
        // descriptors from reaching our parallel scan. The picker calls
        // RestartFfbTap() on close to resume capture.
        public void StopFfbTap()
        {
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _steeringReader?.Dispose(); } catch { }
            _steeringReader = null;
        }

        // Dispose the current FFB tap and spawn a fresh one. Used after the
        // user changes the USBPcap override path or reinstalls USBPcap so
        // the new binary takes effect without restarting SimHub. No-op when
        // no device is active (the next device init will pick up the new
        // setting automatically).
        public bool RestartFfbTap()
        {
            if (_device == null) return false;

            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _steeringReader?.Dispose(); } catch { }
            _steeringReader = null;

            var (ifaceOverride, devOverride) = ResolveUsbPcapOverride();
            _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride, Settings?.UsbPcapCmdPathOverride)
            {
                Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                HostElevated = IsRunningElevated,
            };
            if (_hidWheelVid != 0 || _hidWheelPid != 0)
                _ffbTap.SetHidDiscoveredWheel(_hidWheelVid, _hidWheelPid);
            _ffbTap.SetOverrideIdentity((ushort)(Settings?.ManualUsbPcapVid ?? 0), (ushort)(Settings?.ManualUsbPcapPid ?? 0));
            WireFfbTapCallbacks(_ffbTap);
            ApplyUsbBytesLoggingSetting();
            return _ffbTap.Start();
        }

        // Returns the path where usb-trace.pcap should live: alongside
        // SimHub's log dir, so the Export Logs zip picks it up next to the
        // .txt logs without any additional plumbing. Computed from the host
        // process path rather than our assembly path because we live in
        // PluginsData but SimHub's log dir is at the install root. Written
        // as a real pcap with DLT_USBPCAP so Wireshark opens it directly.
        public static string GetUsbTraceLogPath()
        {
            string simHubRoot = System.IO.Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            return System.IO.Path.Combine(simHubRoot, "usb-trace.pcap");
        }

        // Apply the persisted LogUsbBytesEnabled flag to the live FFB tap.
        // Called after each tap (re)start and from the Diagnostics toggle
        // handler. Idempotent and safe on a null tap.
        public void ApplyUsbBytesLoggingSetting()
        {
            if (_ffbTap == null) return;
            bool enabled = Settings?.LogUsbBytesEnabled ?? false;
            _ffbTap.SetRawPacketLogPath(enabled ? GetUsbTraceLogPath() : null);
        }

        // Toggle the raw USB packet log. Persists the new state and applies
        // it to the live tap immediately. Called from the Diagnostics
        // checkbox in SettingsControl.
        public void SetUsbBytesLoggingEnabled(bool enabled)
        {
            if (Settings == null) return;
            if (Settings.LogUsbBytesEnabled == enabled) return;
            Settings.LogUsbBytesEnabled = enabled;
            try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            ApplyUsbBytesLoggingSetting();
            SimHub.Logging.Current.Info($"[Trueforce] USB byte logging {(enabled ? "enabled" : "disabled")}.");
        }

        // Launches the bundled USBPcap installer (silent /S, elevated). Called
        // from the settings panel's Reinstall action when USBPcap is missing
        // or broken. Runs the wait + restart on a background thread so the
        // SimHub UI thread doesn't freeze during the install. Status updates
        // surface through the existing FfbTapStatus -> FfbTapText polling.
        public void ReinstallUsbPcapAsync()
        {
            string pluginDir = System.IO.Path.GetDirectoryName(typeof(TrueforcePlugin).Assembly.Location);
            string setup = System.IO.Path.Combine(pluginDir, "vendor", "USBPcapSetup.exe");
            if (!System.IO.File.Exists(setup))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] USBPcap setup not found at {setup}. Was the plugin installed via the official installer?");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var psi = new ProcessStartInfo(setup, "/S")
                    {
                        UseShellExecute = true, // required for the runas verb
                        Verb = "runas",         // triggers UAC; USBPcap install needs admin
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit();
                    }
                    SimHub.Logging.Current.Info("[Trueforce] USBPcap installer finished. Re-probing.");

                    // Clear any user-set override so the fresh install gets
                    // picked up from the default Program Files paths.
                    if (Settings != null) Settings.UsbPcapCmdPathOverride = "";
                    try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
                    RestartFfbTap();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // User cancelled the UAC prompt, or another shell-execute
                    // failure. Swallow without restarting the tap.
                    SimHub.Logging.Current.Info("[Trueforce] USBPcap install cancelled or blocked by UAC.");
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[Trueforce] USBPcap install failed", ex);
                }
            });
        }
    }
}
