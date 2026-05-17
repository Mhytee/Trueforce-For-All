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
    [PluginDescription("Logitech Trueforce-compatible haptics for any SimHub-supported game on G PRO and RS50 wheels.")]
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
        private MairaIpcSource _mairaIpc;
        private Thread _mairaLedThread;
        private volatile bool _mairaLedStop;

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
        // When a UDP source (Forza or F1) has been running without
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
        /// Args: (gameKind "forza"/"f1", discoveredPort).</summary>
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
        private int    _lastSimHubAbsActive;
        private int?   _lastSimHubPitLimiterActive;
        private int?   _lastSimHubDrsActive;

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
        private bool _gHubLastLoggedState;
        private const string GHubProcessName = "lghub";
        private const string GHubAgentProcessName = "lghub_agent";
        private static readonly long GHubCheckIntervalTicks = Stopwatch.Frequency * 5;

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

        // Capture-targeting state. The poll thread (1 Hz) walks the process
        // table, decides which sim to capture, and tells HelperHost to retarget.
        private volatile string _currentGameName;
        private Thread _capturePollThread;
        private string _captureStatus = "Idle (no game running)";

        // Status surfaced to the SettingsControl.
        public string WheelStatus    { get; private set; } = "Not detected";
        public string StreamStatus   { get; private set; } = "Stopped";
        public string CaptureStatus  => _captureStatus;
        public string FfbTapStatus   => _ffbTap?.Status ?? "Not started";
        public int    ActiveVoiceCount => _mixer.SourceCount;

        // True when USBPcapCMD.exe is locatable right now (override path, env
        // var, or default Program Files paths). Cheap probe; the settings UI
        // polls this on its tick to show/hide the Browse + Reinstall buttons.
        public bool IsUsbPcapAvailable =>
            UsbPcapFfbTap.LocateUsbPcapCmd(Settings?.UsbPcapCmdPathOverride) != null;

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
                    return "Logitech G HUB is running. It claims the wheel and blocks Trueforce. Close G HUB, then reload this plugin from SimHub's Plugins page.";

                // 4. Wheel device state. WheelStatus is set by the discovery
                //    + open path; "Not detected" is the default.
                string wheel = WheelStatus ?? "";
                if (wheel.StartsWith("Not detected", StringComparison.OrdinalIgnoreCase))
                    return "Wheel not detected. Plug in your G PRO / RS50, or close any app that's holding the device exclusively.";
                if (wheel.StartsWith("Open failed", StringComparison.OrdinalIgnoreCase)
                 || wheel.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Wheel reports: {wheel}. Try unplugging and reconnecting the wheel.";

                // 5. HID stream state.
                string stream = StreamStatus ?? "";
                if (!stream.StartsWith("Streaming", StringComparison.OrdinalIgnoreCase))
                    return $"Wheel stream is '{stream}'. The plugin is opened but not actively driving the wheel, check the Diagnostics panel.";

                // 6. No game / not in session
                if (string.IsNullOrEmpty(_activeGame))
                    return "No game running. Start a supported game and load into a session.";

                var src = _telemetrySource;
                double hz = src?.MeasuredHz ?? 0;
                if (hz <= 0)
                    return $"'{_activeGame}' is detected but no telemetry is arriving. You may be in a menu or paused.";

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

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Trueforce] Init: loading settings...");
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
            if (Settings.Performance  == null) Settings.Performance  = new PerformanceSettings();
            if (Settings.Forza        == null) Settings.Forza        = new ForzaSettings();
            if (Settings.SeenEffects  == null) Settings.SeenEffects  = new List<string>();
            if (Settings.CarCylinderCache == null)
                Settings.CarCylinderCache = new Dictionary<string, Dictionary<string, int>>();

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
            MigrateLegacyGamePresets();
            MigrateSpikeTamingFlag();
            InstallBuiltinPresetsIfMissing();

            // Per-car file store: load files into Settings.CarOverrides
            // (file wins on conflict), then migrate any existing
            // Settings.CarOverrides / preset.CarOverrides into files for
            // cars that don't already have one. Files become the canonical
            // store; Settings.CarOverrides is now an in-memory cache only.
            _carStore = new CarPresetStore(msg => SimHub.Logging.Current.Info(msg));
            LoadAndMigrateCarPresets();
            MigrateEngineHighRpmHelpersDefaults();

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

            SimHub.Logging.Current.Info("[Trueforce] Discovering wheel...");
            var matches = WheelDiscovery.FindAll();
            if (matches.Count == 0)
            {
                WheelStatus = "Not detected (close G HUB and reload plugins)";
                SimHub.Logging.Current.Warn(
                    "[Trueforce] No supported wheel found. Is G HUB closed? " +
                    "Plug in a G PRO / RS50 and reload SimHub plugins.");
                return;
            }

            var match = matches[0];
            _hidWheelVid = match.Vid;
            _hidWheelPid = match.Pid;
            WheelStatus = $"{match.Model}  (VID 0x{match.Vid:X4}, PID 0x{match.Pid:X4})";
            SimHub.Logging.Current.Info($"[Trueforce] Found {WheelStatus}.");

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
                if (Settings != null && Settings.MairaFfbPassthrough)
                {
                    // MAIRA passthrough: no USBPcap tap, no PID on the HID++
                    // pipe. MAIRA writes its force into shared memory; we
                    // render it through ep3 and (separately) drive the LEDs.
                    _mairaIpc = new MairaIpcSource(msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"));
                    _device.FfbTargetProvider = () =>
                        _mairaIpc?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs);
                    SimHub.Logging.Current.Info("[Trueforce] MAIRA FFB passthrough mode: USBPcap tap disabled, FFB via shared memory.");
                    StartMairaLedPump();
                }
                else
                {
                    var (ifaceOverride, devOverride) = ResolveUsbPcapOverride();
                    _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride, Settings?.UsbPcapCmdPathOverride)
                    {
                        Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                    };
                    _ffbTap.SetHidDiscoveredWheel(match.Vid, match.Pid);
                    ApplyUsbBytesLoggingSetting();
                    _device.FfbTargetProvider = () =>
                    {
                        // SkipFfbPassthrough: return Some(0) so the device sends
                        // active packets (audio plays) with cur = 0x8000. The
                        // wheel uses cur as motor torque and IGNORES ep0 once
                        // active packets are streaming, so this means zero motor
                        // force from the FFB-target path. Only correct for games
                        // that drive the wheel's motor through their own native
                        // ep3 path (Forza Horizon, AC Rally, iRacing); for games
                        // that rely on ep0 (vanilla AC, F1, PC2), this kills FFB.
                        if (Settings != null && Settings.SkipFfbPassthrough) return (short?)0;
                        return _ffbTap?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs);
                    };
                }
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

                _producerThread = new Thread(ProducerLoop)
                {
                    IsBackground = true,
                    Name = "TrueforceProducer",
                    Priority = ThreadPriority.AboveNormal,
                };
                _producerThread.Start();

                StreamStatus = "Streaming (1 kHz, 250 packets/s)";
                SimHub.Logging.Current.Info("[Trueforce] Stream started.");
            }
            catch (Exception ex)
            {
                StreamStatus = $"Init failed: {ex.Message}";
                SimHub.Logging.Current.Error("[Trueforce] Init failed", ex);
                CleanupDevice();
                return;
            }

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
            _effects = new TelemetryEffect[] { EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick, PitLimiter, Drs, Collision };
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

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;

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

            try { _capturePollThread?.Join(2000); } catch { }
            _capturePollThread = null;

            // Stop the active telemetry source so PushFromGameData becomes a
            // no-op for any late SimHub tick that lands during teardown.
            try { _telemetrySource?.Dispose(); } catch { }
            _telemetrySource = null;
            _simHubSource    = null;

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
            if (carId != _activeCarId)
            {
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
                    if (CarCylinderResolver.TryResolve(_activeGame, carId, out var carSpec))
                    {
                        EnginePulse.AutoLayout = Effects.FiringPatternDb.LayoutFromLegacy(
                            carSpec.Cylinders, carSpec.EngineConfig, carSpec.IsElectric);
                        EnginePulse.AutoLayoutSource = carSpec.Source;
                        EnginePulse.CatalogCyl = carSpec.Cylinders;
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

            // G HUB presence check. Polled every ~5 s; logs on transitions.
            // Surfaced in the UI as a warning banner because G HUB blocks our
            // HID open call and is by far the most common "wheel doesn't
            // respond" cause.
            {
                long nowG = Stopwatch.GetTimestamp();
                if (nowG - _lastGHubCheckTicks > GHubCheckIntervalTicks)
                {
                    _lastGHubCheckTicks = nowG;
                    bool running = false;
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName(GHubProcessName).Length > 0
                         || System.Diagnostics.Process.GetProcessesByName(GHubAgentProcessName).Length > 0)
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
                                ? "[Trueforce] Logitech G HUB detected. It claims the wheel's HID interface and blocks Trueforce. Close G HUB and reload the plugin."
                                : "[Trueforce] Logitech G HUB no longer detected. Wheel access should be available again.");
                    }
                    _isGHubRunning = running;
                }
            }

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
                bool gate = (Settings?.RpmLedsEnabled ?? false)
                            && string.Equals(_activeGame, "IRacing", StringComparison.Ordinal);
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
            }
        }

        private void FireRatchetEvent(bool isTf, int oldCap, int newCap)
        {
            try { AutoRatchetBumped?.Invoke(isTf, oldCap, newCap); } catch { }
        }

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
            if (game == "PCars2") return true;
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

        /// <summary>True when the F1 UDP section should be visible.</summary>
        public bool ShouldShowF1Section =>
            IsF1GameName(_activeGame)
            || (Settings?.F1?.AlwaysListen == true);

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

        /// <summary>True if SimHub's GameName looks like an EA / Codemasters
        /// F1 title we target. Currently F1 25 is the only validated wire
        /// format; older games (F1 22/23/24) may receive packets but the
        /// source skips PacketFormat != 2025 with a one-time log line. The
        /// UI section still renders for those names so a user can confirm
        /// that's why packets aren't parsing.</summary>
        private static bool IsF1GameName(string game)
        {
            if (string.IsNullOrEmpty(game)) return false;
            return game == "F12025"
                || game == "F12024"
                || game == "F12023"
                || game == "F12022";
        }

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
            else if (game == "PCars2")
            {
                var pc2 = new Pcars2SharedMemoryTelemetrySource
                {
                    Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                };
                try
                {
                    pc2.Start();
                    newSource = pc2;
                }
                catch (Exception ex)
                {
                    try { pc2.Dispose(); } catch { }
                    if (!silent)
                    {
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] PC2 enhanced source unavailable ({ex.GetType().Name}): {ex.Message}; falling back to SimHub. " +
                            "If PC2 is running, ensure 'Use Shared Memory' is enabled in Options > Visuals.");
                    }
                }
            }
            else if (IsForzaGameName(game))
            {
                if (Settings?.Forza?.Enabled == true)
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
            else if (IsF1GameName(game)
                     || (Settings?.F1?.AlwaysListen == true && Settings.F1.Enabled))
            {
                if (Settings?.F1?.Enabled == true)
                {
                    try
                    {
                        var bindIp = ParseIpOrAny(Settings.F1.BindAddress);
                        var forwardTo = BuildF1ForwardEndpoint(Settings.F1);
                        var f1 = new F1UdpTelemetrySource(Settings.F1.Port, bindIp, forwardTo)
                        {
                            Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                        };
                        f1.Start();
                        newSource = f1;
                    }
                    catch (Exception ex)
                    {
                        if (!silent)
                        {
                            SimHub.Logging.Current.Info(
                                $"[Trueforce] F1 UDP source unavailable on port {Settings.F1.Port} " +
                                $"({ex.GetType().Name}): {ex.Message}; falling back to SimHub. " +
                                "If another listener holds the port, change Trueforce's port to a free one and re-point F1's UDP Telemetry to it.");
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
            // start (Forza was idle, F1 just launched, port changed in
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
        //   - The active source is a UDP source (Forza or F1).
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
            else if (src is F1UdpTelemetrySource f1)
            {
                received    = f1.PacketsReceived;
                candidates  = F1UdpTelemetrySource.DiscoveryCandidatePorts;
                currentPort = Settings?.F1?.Port ?? 0;
                validator   = F1UdpTelemetrySource.IsValidPacketCandidate;
                kind        = "f1";
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

            var bindIp = ParseIpOrAny(
                kind == "forza" ? Settings?.Forza?.BindAddress : Settings?.F1?.BindAddress);
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

        /// <summary>UI hook: switch the F1 (or Forza) listener to the
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
            else if (src is F1UdpTelemetrySource && Settings.F1 != null)
            {
                Settings.F1.Port = port;
                ApplyF1Settings();
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
        /// <summary>Same as BuildForzaForwardEndpoint, for the F1 forwarder.</summary>
        private static IPEndPoint BuildF1ForwardEndpoint(F1Settings fs)
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
            // through to a non-Forza source (F1 if applicable, else SimHub).
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

        /// <summary>Same shape as ApplyForzaSettings, for the F1 source.</summary>
        public void ApplyF1Settings()
        {
            if (Settings?.F1 == null) return;
            this.SaveCommonSettings("GeneralSettings", Settings);

            bool currentlyF1 = _telemetrySource is F1UdpTelemetrySource;
            bool shouldListen = (Settings.F1.AlwaysListen && Settings.F1.Enabled)
                             || (!string.IsNullOrEmpty(_activeGame) && IsF1GameName(_activeGame));
            if (!currentlyF1 && !shouldListen) return;

            if (currentlyF1)
            {
                var oldF1 = _telemetrySource;
                oldF1.OnFrame = null;
                _simHubSource.OnFrame = DispatchFrame;
                _telemetrySource = _simHubSource;
                try { oldF1.Dispose(); } catch { }
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
            ApplyAudioCaptureSettings(ovr?.AudioCapture ?? Settings.AudioCapture);
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
        public AudioCaptureSettings ActiveAudio    => GetActiveCarOverride()?.AudioCapture ?? Settings.AudioCapture;

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
        private void InstallBuiltinPresetsIfMissing()
        {
            if (Settings == null) return;
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            int refreshed = 0;
            foreach (var kv in BuiltinPresets.BuiltinPresetJsons)
            {
                try
                {
                    var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(kv.Value);
                    if (snap != null)
                    {
                        Settings.Presets[kv.Key] = snap;
                        refreshed++;
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Trueforce] Failed to install built-in preset '{kv.Key}': {ex.Message}");
                }
            }
            // Bind game defaults if user hasn't chosen one for that game.
            foreach (var kv in BuiltinPresets.GameDefaultBindings)
            {
                if (!Settings.GameDefaults.ContainsKey(kv.Key)
                    && Settings.Presets.ContainsKey(kv.Value))
                {
                    Settings.GameDefaults[kv.Key] = kv.Value;
                }
            }
            if (refreshed > 0)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Refreshed {refreshed} built-in preset(s).");
            }
        }

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

            // 1) Migrate Settings.CarOverrides → files (only if no file yet).
            //    Use carId as the preset name so the migrated entry has a
            //    stable identity in the new multi-preset model.
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
                if (_carStore.Exists(kv.Key, kv.Key)) continue;
                _carStore.Save(kv.Key, kv.Key, _activeGame ?? "", kv.Value, isBuiltin: false);
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
                        if (_carStore.Exists(carKv.Key, carKv.Key)) continue;
                        _carStore.Save(carKv.Key, carKv.Key, "", carKv.Value, isBuiltin: false);
                        if (!Settings.CarDefaults.ContainsKey(carKv.Key))
                            Settings.CarDefaults[carKv.Key] = carKv.Key;
                        migrated++;
                    }
                }
            }

            // 3) Migrate v1 single-preset files to v2 multi-preset naming
            //    (<carId>.tfcar.json → <carId>~<carId>.tfcar.json with
            //    PresetName=CarId, IsBuiltin=false). For each carId migrated
            //    that doesn't yet have a CarDefaults entry, point CarDefaults
            //    at the migrated user preset so the user's existing tunings
            //    stay active.
            var legacyMigrated = _carStore.MigrateLegacyFiles();
            foreach (var carId in legacyMigrated)
            {
                if (!Settings.CarDefaults.ContainsKey(carId))
                    Settings.CarDefaults[carId] = carId;
                migrated++;
            }

            // 4) Install / refresh built-in factory car presets. Always runs:
            //    if a future release updates a default tuning the new content
            //    lands; user-saved files are untouched (different filenames).
            int builtinsWritten = _carStore.InstallOrUpdateBuiltinCarPresets(BuiltinCarPresets.PresetJsons);

            // 5) Load every preset back into memory and resolve the active
            //    preset per car into Settings.CarOverrides. Active selection:
            //    Settings.CarDefaults[carId] → that preset; else the first
            //    builtin "(default)" found; else nothing.
            var loaded = _carStore.LoadAll();

            // 5.5) Propagate GameName from any built-in entry to sibling user
            //      entries for the same carId that still have an empty
            //      GameName. Catches: legacy v1 → v2 migration files; the
            //      Settings.CarOverrides → file migration (which ran with
            //      _activeGame=null at Init); and pre-fix builtins (which
            //      shipped with GameName=""). Without this, those sibling
            //      user files land under "Other" in the export modal even
            //      though we already know what game they belong to via the
            //      built-in alongside them.
            int propagated = 0;
            foreach (var carKv in loaded)
            {
                string knownGame = null;
                foreach (var entry in carKv.Value.Values)
                {
                    if (entry.IsBuiltin && !string.IsNullOrEmpty(entry.GameName))
                    {
                        knownGame = entry.GameName;
                        break;
                    }
                }
                if (knownGame == null) continue;
                foreach (var entry in carKv.Value.Values)
                {
                    if (entry.IsBuiltin) continue;
                    if (!string.IsNullOrEmpty(entry.GameName)) continue;
                    _carStore.Save(entry.CarId, entry.PresetName, knownGame, entry.Override, isBuiltin: false);
                    entry.GameName = knownGame;
                    propagated++;
                }
            }

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

            if (migrated > 0 || builtinsWritten > 0 || propagated > 0)
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Car presets: migrated {migrated} legacy entries, wrote {builtinsWritten} builtin preset(s), propagated GameName to {propagated} sibling user preset(s).");
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
                AudioCapture = CloneOrNull(o.AudioCapture),
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
            if (IsCarPresetBuiltin(_activeCarId, presetName)) return false;

            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            _carStore.Save(_activeCarId, presetName, _activeGame ?? "", ovr, isBuiltin: false);
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
            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            _carStore.Save(_activeCarId, newPresetName, _activeGame ?? "", ovr, isBuiltin: false);
            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults[_activeCarId] = newPresetName;
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
            if (IsCarPresetBuiltin(carId, presetName)) return false;
            _carStore.Delete(carId, presetName);
            if (Settings?.CarDefaults != null
                && Settings.CarDefaults.TryGetValue(carId, out var active)
                && string.Equals(active, presetName, StringComparison.Ordinal))
            {
                Settings.CarDefaults.Remove(carId);
                if (carId == _activeCarId)
                    ReloadActiveCarOverrideFromStore();
            }
            return true;
        }

        /// <summary>Rename a car preset on disk. Updates CarDefaults for that
        /// car if the renamed preset was the active one. Refuses on built-ins
        /// and when the target name already exists for that car. Used by the
        /// Manage Presets dialog.</summary>
        public bool RenameCarPreset(string carId, string oldName, string newName)
        {
            if (_carStore == null || Settings == null) return false;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;
            if (IsCarPresetBuiltin(carId, oldName))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to rename built-in car preset '{carId}/{oldName}'.");
                return false;
            }
            if (_carStore.Exists(carId, newName)) return false;

            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar) || !perCar.TryGetValue(oldName, out var entry)) return false;

            _carStore.Save(carId, newName, entry.GameName ?? "", entry.Override, isBuiltin: false);
            _carStore.Delete(carId, oldName);

            if (Settings.CarDefaults != null
                && Settings.CarDefaults.TryGetValue(carId, out var active)
                && string.Equals(active, oldName, StringComparison.Ordinal))
            {
                Settings.CarDefaults[carId] = newName;
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
            if (_carStore.Exists(carId, newName)) return false;

            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar) || !perCar.TryGetValue(sourceName, out var entry)) return false;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(entry.Override);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<CarOverride>(json);
            _carStore.Save(carId, newName, entry.GameName ?? "", clone, isBuiltin: false);
            SimHub.Logging.Current.Info($"[Trueforce] Duplicated car preset '{carId}/{sourceName}' as '{newName}'.");
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
            if (carId == _activeCarId) ReloadActiveCarOverrideFromStore();
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
        /// per-car preset dropdown.</summary>
        public IReadOnlyDictionary<string, CarPresetEntry> GetCarPresets(string carId)
        {
            if (_carStore == null || string.IsNullOrEmpty(carId))
                return new Dictionary<string, CarPresetEntry>();
            var loaded = _carStore.LoadAll();
            return loaded.TryGetValue(carId, out var perCar)
                ? perCar
                : new Dictionary<string, CarPresetEntry>();
        }

        /// <summary>Returns every car preset across every car, indexed by
        /// carId then presetName. Single LoadAll pass for the Manage Presets
        /// dialog (used when no specific car is active).</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, CarPresetEntry>> GetAllCarPresets()
        {
            if (_carStore == null)
                return new Dictionary<string, IReadOnlyDictionary<string, CarPresetEntry>>();
            var raw = _carStore.LoadAll();
            var wrapped = new Dictionary<string, IReadOnlyDictionary<string, CarPresetEntry>>(raw.Count);
            foreach (var kv in raw) wrapped[kv.Key] = kv.Value;
            return wrapped;
        }

        /// <summary>True iff the named car preset is a factory built-in.
        /// Built-ins refuse delete and refuse in-place save; the UI must
        /// fork to a user-named preset.</summary>
        public bool IsCarPresetBuiltin(string carId, string presetName)
        {
            if (_carStore == null || string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName))
                return false;
            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar)) return false;
            return perCar.TryGetValue(presetName, out var entry) && entry.IsBuiltin;
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
            return !CarOverrideEquals(live, saved);
        }

        // Deep equality on a CarOverride. Both null = equal; one null +
        // other empty = equal (an empty override is the same as no override
        // for save / dirty purposes); otherwise per-section pairwise via
        // the existing Eq helpers, treating null sub-sections as equal only
        // when both sides are null.
        private static bool CarOverrideEquals(CarOverride a, CarOverride b)
        {
            bool aEmpty = a == null || a.IsEmpty;
            bool bEmpty = b == null || b.IsEmpty;
            if (aEmpty && bEmpty) return true;
            if (aEmpty || bEmpty) return false;
            return Eq(a.EnginePulse,  b.EnginePulse)
                && Eq(a.RoadBumps,    b.RoadBumps)
                && Eq(a.TractionLoss, b.TractionLoss)
                && Eq(a.GearShift,    b.GearShift)
                && Eq(a.AbsClick,     b.AbsClick)
                && Eq(a.AudioCapture, b.AudioCapture);
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
                case SectionKind.Audio:      if (ovr.AudioCapture == null) ovr.AudioCapture = CloneOrNull(Settings.AudioCapture); break;
                default: return;  // Master / Ducking aren't per-car
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
                case SectionKind.Audio:      if (ovr.AudioCapture != null) { Settings.AudioCapture = CloneOrNull(ovr.AudioCapture); ovr.AudioCapture = null; } break;
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

        private enum EffectField { Audio, Engine, Bumps, Traction, Shift, Abs, PitLimiter, Drs, Collision }

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
                case EffectField.Audio:
                    if (liveCo?.AudioCapture != null) return Eq(liveCo.AudioCapture, savedCo?.AudioCapture);
                    return Eq(Settings.AudioCapture, snap.AudioCapture);
                case EffectField.Engine:
                    if (liveCo?.EnginePulse  != null) return Eq(liveCo.EnginePulse,  savedCo?.EnginePulse);
                    return Eq(Settings.EnginePulse,  snap.EnginePulse);
                case EffectField.Bumps:
                    if (liveCo?.RoadBumps    != null) return Eq(liveCo.RoadBumps,    savedCo?.RoadBumps);
                    return Eq(Settings.RoadBumps,    snap.RoadBumps);
                case EffectField.Traction:
                    if (liveCo?.TractionLoss != null) return Eq(liveCo.TractionLoss, savedCo?.TractionLoss);
                    return Eq(Settings.TractionLoss, snap.TractionLoss);
                case EffectField.Shift:
                    if (liveCo?.GearShift    != null) return Eq(liveCo.GearShift,    savedCo?.GearShift);
                    return Eq(Settings.GearShift,    snap.GearShift);
                case EffectField.Abs:
                    if (liveCo?.AbsClick     != null) return Eq(liveCo.AbsClick,     savedCo?.AbsClick);
                    return Eq(Settings.AbsClick,     snap.AbsClick);
                case EffectField.PitLimiter:
                    if (liveCo?.PitLimiter   != null) return Eq(liveCo.PitLimiter,   savedCo?.PitLimiter);
                    return Eq(Settings.PitLimiter,   snap.PitLimiter);
                case EffectField.Drs:
                    if (liveCo?.Drs          != null) return Eq(liveCo.Drs,          savedCo?.Drs);
                    return Eq(Settings.Drs,          snap.Drs);
                case EffectField.Collision:
                    if (liveCo?.Collision    != null) return Eq(liveCo.Collision,    savedCo?.Collision);
                    return Eq(Settings.Collision,    snap.Collision);
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
        private static bool Eq(AudioCaptureSettings a, AudioCaptureSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,             b.Gain)
                && EqI (a.LowpassCutoffHz,  b.LowpassCutoffHz)
                && EqI (a.HighpassCutoffHz, b.HighpassCutoffHz);
        }

        // ---------- per-section revert (from active preset) ----------

        /// <summary>Section identifier used by <see cref="RevertSection"/>.
        /// Mirrors the per-section "Save…" / "Revert" buttons in the UI:
        /// Master and Ducking are global-only; the rest have a per-car
        /// override component that revert respects.</summary>
        public enum SectionKind { Master, Ducking, Audio, Engine, Bumps, Traction, Shift, Abs, SpikeReduction, PitLimiter, Drs, Collision }

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
            if (IsBuiltinPreset(presetName))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to overwrite built-in preset '{presetName}'.");
                return;
            }
            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            Settings.Presets[presetName] = SnapshotCurrentAsPreset();
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
            if (IsBuiltinPreset(_activePresetName)) return false;
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
                case SectionKind.Engine:     snap.EnginePulse  = Clone(Settings.EnginePulse);     break;
                case SectionKind.Bumps:      snap.RoadBumps    = Clone(Settings.RoadBumps);       break;
                case SectionKind.Traction:   snap.TractionLoss = Clone(Settings.TractionLoss);    break;
                case SectionKind.Shift:      snap.GearShift    = Clone(Settings.GearShift);       break;
                case SectionKind.Abs:        snap.AbsClick     = Clone(Settings.AbsClick);        break;
                case SectionKind.PitLimiter: snap.PitLimiter   = Clone(Settings.PitLimiter);      break;
                case SectionKind.Drs:        snap.Drs          = Clone(Settings.Drs);             break;
                case SectionKind.Collision:  snap.Collision    = Clone(Settings.Collision);       break;
                case SectionKind.Audio:      snap.AudioCapture = CloneOrNull(Settings.AudioCapture); break;
                default: return false;
            }
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
            if (IsCarPresetBuiltin(_activeCarId, presetName)) return false;
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
                case SectionKind.Audio:      patched.AudioCapture = CloneOrNull(live.AudioCapture); break;
                default: return false;
            }

            _carStore.Save(_activeCarId, presetName, _activeGame ?? "", patched, isBuiltin: false);
            if (patched.IsEmpty)
                _lastPersistedCarOverrides.Remove(_activeCarId);
            else
                _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(patched);
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
            if (IsBuiltinPreset(presetName))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to delete built-in preset '{presetName}'.");
                return false;
            }
            if (!Settings.Presets.Remove(presetName)) return false;

            // Drop any game defaults that pointed to this preset.
            if (Settings.GameDefaults != null)
            {
                var orphans = new List<string>();
                foreach (var kv in Settings.GameDefaults)
                    if (kv.Value == presetName) orphans.Add(kv.Key);
                foreach (var k in orphans) Settings.GameDefaults.Remove(k);
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
            if (Settings == null || string.IsNullOrEmpty(_activeGame) || string.IsNullOrEmpty(presetName)) return;
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.Presets == null || !Settings.Presets.ContainsKey(presetName)) return;
            Settings.GameDefaults[_activeGame] = presetName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] '{presetName}' set as default for '{_activeGame}'.");
        }

        /// <summary>Remove the auto-load binding for the active game.</summary>
        public void ClearDefaultPresetForActiveGame()
        {
            if (Settings?.GameDefaults == null || string.IsNullOrEmpty(_activeGame)) return;
            if (Settings.GameDefaults.Remove(_activeGame))
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Cleared default preset for '{_activeGame}'.");
            }
        }

        /// <summary>Rename a game preset in the library. Updates any
        /// GameDefaults entries that pointed to the old name and the
        /// ActivePresetName if it was the renamed one. Refuses on built-ins
        /// (factory names are part of the brand) and when the target name
        /// already exists. Used by the Manage Presets dialog.</summary>
        public bool RenamePreset(string oldName, string newName)
        {
            if (Settings?.Presets == null) return false;
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;
            if (IsBuiltinPreset(oldName))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refusing to rename built-in preset '{oldName}'.");
                return false;
            }
            if (!Settings.Presets.TryGetValue(oldName, out var snap) || snap == null) return false;
            if (Settings.Presets.ContainsKey(newName)) return false;

            Settings.Presets.Remove(oldName);
            Settings.Presets[newName] = snap;

            if (Settings.GameDefaults != null)
            {
                var keys = new List<string>();
                foreach (var kv in Settings.GameDefaults)
                    if (kv.Value == oldName) keys.Add(kv.Key);
                foreach (var k in keys) Settings.GameDefaults[k] = newName;
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

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(snap);
            var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(json);
            Settings.Presets[newName] = clone;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] Duplicated preset '{sourceName}' as '{newName}'.");
            return true;
        }

        /// <summary>Bind a preset to auto-load for any game (not just the
        /// active one). Used by the Manage Presets dialog's per-row Set
        /// default action. Returns false if the named preset isn't in the
        /// library.</summary>
        public bool SetDefaultPresetForGame(string gameName, string presetName)
        {
            if (Settings == null) return false;
            if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(presetName)) return false;
            if (Settings.Presets == null || !Settings.Presets.ContainsKey(presetName)) return false;
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            Settings.GameDefaults[gameName] = presetName;
            this.SaveCommonSettings("GeneralSettings", Settings);
            SimHub.Logging.Current.Info($"[Trueforce] '{presetName}' set as default for '{gameName}'.");
            return true;
        }

        /// <summary>Drop the auto-load binding for a specific game.</summary>
        public bool ClearDefaultPresetForGame(string gameName)
        {
            if (Settings?.GameDefaults == null || string.IsNullOrEmpty(gameName)) return false;
            if (!Settings.GameDefaults.Remove(gameName)) return false;
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
            if (IsBuiltinPreset(name))
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Can't overwrite built-in preset '{name}' on edit-mode save; fork via Save as new.");
                return false;
            }
            SavePresetAs(name);   // SavePresetAs persists + sets _activePresetName
            _offlineEditPresetName = null;
            _preEditSnapshot = null;
            _preEditActivePresetName = null;
            SimHub.Logging.Current.Info($"[Trueforce] Exited offline edit mode (saved '{name}').");
            return true;
        }

        /// <summary>Exit offline edit mode by saving the in-memory edits as
        /// a brand-new preset. Useful for forking off a built-in or just
        /// keeping the original preset untouched.</summary>
        public bool ExitOfflineEditSaveAs(string newName)
        {
            if (!IsOfflineEditing || string.IsNullOrEmpty(newName)) return false;
            if (Settings.Presets != null && Settings.Presets.ContainsKey(newName)) return false;
            SavePresetAs(newName);
            _offlineEditPresetName = null;
            _preEditSnapshot = null;
            _preEditActivePresetName = null;
            SimHub.Logging.Current.Info($"[Trueforce] Exited offline edit mode (saved as new '{newName}').");
            return true;
        }

        /// <summary>Exit offline edit mode and restore the pre-edit live
        /// state (everything the user had loaded before they clicked Edit).
        /// Used by the banner's Discard button.</summary>
        public void ExitOfflineEditDiscard()
        {
            if (!IsOfflineEditing) return;
            if (_preEditSnapshot != null)
                ApplyGamePreset(_preEditSnapshot);
            _activePresetName = _preEditActivePresetName;
            string was = _offlineEditPresetName;
            _offlineEditPresetName = null;
            _preEditSnapshot = null;
            _preEditActivePresetName = null;
            SimHub.Logging.Current.Info($"[Trueforce] Exited offline edit mode (discarded edits to '{was}').");
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

            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            Settings.Presets[file.PresetName] = file.Snapshot;
            this.SaveCommonSettings("GeneralSettings", Settings);

            SimHub.Logging.Current.Info($"[Trueforce] Imported preset '{file.PresetName}' from {path}.");
            return new ImportPresetResult
            {
                PresetName    = file.PresetName,
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
        /// to a shareable JSON file. Used by the Manage Presets dialog where
        /// the user can pick any preset regardless of which car is currently
        /// active. Returns false if the preset doesn't exist on disk.</summary>
        public bool ExportCarPreset(string carId, string presetName, string path,
            string author = null, string description = null, string authorVersion = null)
        {
            if (_carStore == null) return false;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName) || string.IsNullOrEmpty(path)) return false;
            var loaded = _carStore.LoadAll();
            if (!loaded.TryGetValue(carId, out var perCar) || !perCar.TryGetValue(presetName, out var entry)) return false;
            var file = new CarPresetFile
            {
                GameName      = entry.GameName ?? "",
                CarId         = carId,
                PresetName    = StripDefaultSuffixForExport(presetName),
                IsBuiltin     = false,
                Author        = NullIfBlank(author),
                Description   = NullIfBlank(description),
                AuthorVersion = NullIfBlank(authorVersion),
                Override      = entry.Override,
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset '{carId}/{presetName}' to {path}.");
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

            // PresetName may be missing on legacy v1 imports, fall back to
            // the carId. IsBuiltin is force-cleared regardless of source.
            string desired = string.IsNullOrEmpty(file.PresetName) ? file.CarId : file.PresetName;
            string presetName = MakeUniqueCarPresetName(file.CarId, desired);
            _carStore.Save(file.CarId, presetName, file.GameName ?? "", file.Override, isBuiltin: false);

            if (Settings.CarDefaults == null) Settings.CarDefaults = new Dictionary<string, string>();
            Settings.CarDefaults[file.CarId] = presetName;
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
            string author = null, string description = null, string authorVersion = null)
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
            var manifest = new PresetPackManifest
            {
                ExportedAt    = DateTime.UtcNow.ToString("o"),
                Author        = normAuthor,
                Description   = normDesc,
                AuthorVersion = normVer,
            };

            int presetsCount = 0, carsCount = 0;
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

                        string entryName = "presets/" + SanitizeForZip(kv.Key) + ".tfpreset";
                        var file = new PresetFile
                        {
                            PresetName    = kv.Key,
                            Snapshot      = kv.Value,
                            Author        = normAuthor,
                            Description   = normDesc,
                            AuthorVersion = normVer,
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

                            string entryName = "cars/" + SanitizeForZip(entry.CarId) + "~"
                                + SanitizeForZip(entry.PresetName) + ".tfcar.json";
                            var file = new CarPresetFile
                            {
                                GameName      = entry.GameName ?? "",
                                CarId         = entry.CarId,
                                PresetName    = entry.PresetName,
                                IsBuiltin     = false, // shareable copies are user-tier
                                Author        = normAuthor,
                                Description   = normDesc,
                                AuthorVersion = normVer,
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

                WriteJsonZipEntry(zip, "manifest.json", manifest);
            }

            SimHub.Logging.Current.Info(
                $"[Trueforce] Exported pack to {path}: {presetsCount} game preset(s), {carsCount} car preset(s).");
            return (presetsCount, carsCount);
        }

        /// <summary>Read every preset and car-preset file in the pack zip.
        /// Game presets land in Settings.Presets (overwriting any with the
        /// same name); car presets go through MakeUniqueCarPresetName so a
        /// name collision keeps both. Returns a (presets, cars) count.</summary>
        public ImportPackResult ImportPack(string path)
        {
            if (Settings == null) return default(ImportPackResult);

            string packAuthor = null, packDesc = null, packVer = null;
            int presetsImported = 0, carsImported = 0;
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
                    }
                }

                if (Settings.Presets == null)
                    Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();

                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("presets/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfpreset", StringComparison.OrdinalIgnoreCase))
                    {
                        var pf = ReadJsonZipEntry<PresetFile>(entry);
                        if (pf == null || pf.Snapshot == null || string.IsNullOrEmpty(pf.PresetName)) continue;
                        if (pf.Type != PresetFile.FileType) continue;
                        Settings.Presets[pf.PresetName] = pf.Snapshot;
                        presetsImported++;
                    }
                    else if (entry.FullName.StartsWith("cars/", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.EndsWith(".tfcar.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var cf = ReadJsonZipEntry<CarPresetFile>(entry);
                        if (cf == null || cf.Override == null || string.IsNullOrEmpty(cf.CarId)) continue;
                        if (cf.Type != CarPresetFile.FileType) continue;

                        string desired = string.IsNullOrEmpty(cf.PresetName) ? cf.CarId : cf.PresetName;
                        string presetName = MakeUniqueCarPresetName(cf.CarId, desired);
                        _carStore?.Save(cf.CarId, presetName, cf.GameName ?? "", cf.Override, isBuiltin: false);
                        carsImported++;
                    }
                }
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

        // Sidechain ducking state. Three buses, all driven by max-activity
        // of relevant effects with the same depth/attack/release params:
        //
        //   _duckSmoothed          , Bus 1. Driven by ALL transients +
        //                             modal flags (RoadBumps, TractionLoss,
        //                             GearShift, AbsClick, PitLimiter, Drs).
        //                             Applied to EnginePulse + audio capture
        //                             so any "event" haptic ducks the
        //                             continuous background.
        //   _duckSmoothedMomentary , Bus 2. Truly-momentary transients only
        //                             (RoadBumps, GearShift, AbsClick)
        //                             excludes the sustained ones.
        //                             Applied to TractionLoss so a sustained
        //                             slide doesn't drown out an ABS pump
        //                             or curb hit on top of it.
        //   _duckSmoothedDrsSustained, Bus 3. Driven by the transients that
        //                             should override a held-DRS hum
        //                             (AbsClick, TractionLoss, GearShift).
        //                             Applied to DrsEffect.SustainedDuck-
        //                             Multiplier, only the sustained tone;
        //                             the activation chirp ignores all
        //                             ducking by design (alert event).
        private float _duckSmoothed             = 1.0f;
        private float _duckSmoothedMomentary    = 1.0f;
        private float _duckSmoothedDrsSustained = 1.0f;

        private void UpdateDucking()
        {
            // Bus 1: all transients + modal flags, ducks engine + audio.
            double maxAll = 0;
            if (RoadBumps    != null) maxAll = Math.Max(maxAll, RoadBumps.ActivityLevel);
            if (TractionLoss != null) maxAll = Math.Max(maxAll, TractionLoss.ActivityLevel);
            if (GearShift    != null) maxAll = Math.Max(maxAll, GearShift.ActivityLevel);
            if (AbsClick     != null) maxAll = Math.Max(maxAll, AbsClick.ActivityLevel);
            if (PitLimiter   != null) maxAll = Math.Max(maxAll, PitLimiter.ActivityLevel);
            if (Drs          != null) maxAll = Math.Max(maxAll, Drs.ActivityLevel);

            // Bus 2: truly-momentary transients only, excludes TractionLoss
            // because a sustained slide is itself a "constant effect" relative
            // to the impulse-shaped events below. PitLimiter / Drs sustained
            // are also excluded, they represent ongoing modes, not
            // momentary events.
            double maxMomentary = 0;
            if (RoadBumps != null) maxMomentary = Math.Max(maxMomentary, RoadBumps.ActivityLevel);
            if (GearShift != null) maxMomentary = Math.Max(maxMomentary, GearShift.ActivityLevel);
            if (AbsClick  != null) maxMomentary = Math.Max(maxMomentary, AbsClick.ActivityLevel);

            // Bus 3: drives DRS sustained ducking. ABS pumps, traction loss,
            // gear shifts all happen "on top of" a held DRS, those
            // signals matter more in the moment than the DRS hum, so we
            // duck the hum to make room for them.
            double maxDrsTransient = 0;
            if (AbsClick     != null) maxDrsTransient = Math.Max(maxDrsTransient, AbsClick.ActivityLevel);
            if (TractionLoss != null) maxDrsTransient = Math.Max(maxDrsTransient, TractionLoss.ActivityLevel);
            if (GearShift    != null) maxDrsTransient = Math.Max(maxDrsTransient, GearShift.ActivityLevel);

            float depth     = Settings?.DuckDepth     ?? 0.5f;
            float attackMs  = Settings?.DuckAttackMs  ?? 5.0f;
            float releaseMs = Settings?.DuckReleaseMs ?? 80.0f;

            // IIR with attack-or-release time constant (dt ≈ 1 ms, producer
            // pushes ~1 batch per ms). alpha = 1 - exp(-dt/tau).
            float targetAll          = (float)Math.Max(0.0, 1.0 - depth * maxAll);
            float targetMomentary    = (float)Math.Max(0.0, 1.0 - depth * maxMomentary);
            float targetDrsSustained = (float)Math.Max(0.0, 1.0 - depth * maxDrsTransient);

            float tauAllMs          = (targetAll          < _duckSmoothed)             ? attackMs : releaseMs;
            float tauMomentaryMs    = (targetMomentary    < _duckSmoothedMomentary)    ? attackMs : releaseMs;
            float tauDrsSustainedMs = (targetDrsSustained < _duckSmoothedDrsSustained) ? attackMs : releaseMs;
            float alphaAll          = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauAllMs)));
            float alphaMomentary    = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauMomentaryMs)));
            float alphaDrsSustained = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauDrsSustainedMs)));

            _duckSmoothed             = _duckSmoothed             * (1f - alphaAll)          + targetAll          * alphaAll;
            _duckSmoothedMomentary    = _duckSmoothedMomentary    * (1f - alphaMomentary)    + targetMomentary    * alphaMomentary;
            _duckSmoothedDrsSustained = _duckSmoothedDrsSustained * (1f - alphaDrsSustained) + targetDrsSustained * alphaDrsSustained;

            if (EnginePulse  != null) EnginePulse.DuckMultiplier  = _duckSmoothed;
            if (_audio       != null) _audio.DuckMultiplier       = _duckSmoothed;
            if (TractionLoss != null) TractionLoss.DuckMultiplier = _duckSmoothedMomentary;
            if (Drs          != null) Drs.SustainedDuckMultiplier = _duckSmoothedDrsSustained;
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

        private void CleanupDevice()
        {
            StopMairaLedPump();
            try { _mairaIpc?.Dispose(); } catch { }
            _mairaIpc = null;
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        // ---- MAIRA passthrough LED pump --------------------------------
        // In MAIRA passthrough mode there is no SimHub game frame to drive the
        // rim LEDs from, so pump RpmLedController off the IPC RPM/shift values
        // at ~60 Hz. The LED HID++ writes are safe here because MAIRA is NOT
        // sending PID to the wheel, the HID++ pipe is otherwise idle.
        private void StartMairaLedPump()
        {
            _mairaLedStop = false;
            _mairaLedThread = new Thread(MairaLedLoop)
            { IsBackground = true, Name = "MairaRpmLedPump" };
            _mairaLedThread.Start();
        }

        private void StopMairaLedPump()
        {
            _mairaLedStop = true;
            var t = _mairaLedThread; _mairaLedThread = null;
            try { t?.Join(400); } catch { }
        }

        private void MairaLedLoop()
        {
            while (!_mairaLedStop)
            {
                try
                {
                    Thread.Sleep(16);
                    if (_mairaLedStop) break;
                    var ipc = _mairaIpc;
                    var leds = _rpmLeds;
                    if (ipc == null || leds == null) continue;

                    bool gate = (Settings?.MairaFfbPassthrough ?? false)
                                && (Settings?.RpmLedsEnabled ?? false);
                    if (!gate) { leds.OnFrame(0, 0, 0, false, false); continue; }

                    ipc.TryRefresh();
                    float rpm = ipc.Rpm, slF = ipc.ShiftFirstRpm, slS = ipc.ShiftShiftRpm;
                    double pct = 0;
                    if (slS > slF && rpm > 0) pct = (rpm - slF) / (slS - slF);
                    bool redline = slS > 0 && rpm >= slS;
                    leds.OnFrame(pct, rpm, 0, redline, ipc.IsOpen);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[Trueforce] MAIRA LED pump error", ex);
                }
            }
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
        public bool ApplyManualUsbPcapDevice(string iface, int deviceAddress)
        {
            if (Settings == null) return false;
            Settings.ManualUsbPcapInterface     = iface ?? "";
            Settings.ManualUsbPcapDeviceAddress = deviceAddress > 0 ? deviceAddress : 0;
            try { this.SaveCommonSettings("GeneralSettings", Settings); } catch { }
            SimHub.Logging.Current.Info(
                $"[Trueforce] Manual USB device {(deviceAddress > 0 ? $"set to {iface} dev {deviceAddress}" : "cleared")}.");
            return RestartFfbTap();
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

            var (ifaceOverride, devOverride) = ResolveUsbPcapOverride();
            _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride, Settings?.UsbPcapCmdPathOverride)
            {
                Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
            };
            if (_hidWheelVid != 0 || _hidWheelPid != 0)
                _ffbTap.SetHidDiscoveredWheel(_hidWheelVid, _hidWheelPid);
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
