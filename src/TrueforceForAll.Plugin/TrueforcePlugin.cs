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

        // Per-car preset files — one .tfcar.json per car, the canonical
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

        private TrueforceDevice _device;
        private AudioCaptureSource _audio;
        private HelperHost _helperHost;
        private UsbPcapFfbTap _ffbTap;

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
        private TelemetryEffect[] _effects;

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

        /// <summary>True when the active game is one SimHub has a telemetry
        /// reader for — i.e. anything with a non-Custom GameName. SimHub's
        /// "Custom_*" code is a definitive marker that the user added the
        /// game manually and SimHub has no built-in way to source telemetry,
        /// so engine/RPM/speed-driven effects can't fire. Built-in games
        /// keep this true even at the main menu / paused — we don't grey
        /// out the panel just because telemetry isn't flowing right now.</summary>
        public bool HasUsefulTelemetry =>
            !string.IsNullOrEmpty(_activeGame)
            && !_activeGame.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);

        // Cached slow-rate fields from the most recent SimHub DataUpdate.
        // When an enhanced source is active, DispatchFrame overlays these
        // onto each frame: MaxRpm is static per car (no benefit to physics
        // rate), and AC's `physics.abs` is the *configuration level*, not
        // pump activity — SimHub derives a usable AbsActive signal that we
        // inherit instead of re-implementing.
        private double _lastSimHubMaxRpm;
        private int    _lastSimHubAbsActive;

        // Throttle for retrying enhanced-source acquisition. AC's shared memory
        // page only appears once the game loads into a session, but SimHub
        // reports GameName as soon as the AC process starts (often a minute
        // before the MMF exists). Without a retry, that first-attempt failure
        // would strand us on SimHub fallback for the whole session.
        private long _lastEnhancedRetryTicks;

        // Auto-ratchet state. Snapshots the underrun/glitch counters once per
        // second; when delta crosses RatchetThreshold, the corresponding ring
        // is bumped one notch (UP). The "survived" capacity is persisted to
        // Settings so reinstalls don't re-glitch sessions; manual reset is
        // available from the Performance tab.
        //
        // Ratchet-DOWN is asymmetric — slow + hysteresis-protected — so a
        // brief noisy moment (Chrome update kicking in, antivirus scan)
        // doesn't leave the ring permanently inflated. After UP fires, a
        // 5-minute cooldown blocks any DOWN; after the cooldown, sustained
        // 60+ seconds of zero underruns triggers a one-notch DOWN step.
        // Each step requires another 60 seconds of quiet before the next,
        // so descent is gradual. UP fires fast (1s window); DOWN waits 60s
        // → if noise returns within 5 min, the cooldown prevents any DOWN
        // and oscillation can't form.
        private const int  RatchetWindowMs       = 1000;
        private const long RatchetThreshold      = 3;     // underruns/laps in 1 s to trigger UP
        private const int  RatchetDownQuietMs    = 60_000;   // 60 s of zero deltas → eligible for one DOWN step
        private const int  RatchetDownCooldownMs = 300_000;  // 5 min after any UP before DOWN allowed
        private long _autoRatchetLastCheckTicks;
        private long _autoRatchetLastTfCount;
        private long _autoRatchetLastAudioCount;
        // Stopwatch ticks of the most recent non-zero delta. Reset to "now"
        // any time we see ANY underrun/glitch in the 1s window. The 60s
        // quiet test compares (now - lastSeen) against RatchetDownQuietMs.
        private long _tfLastUnderrunSeenTicks;
        private long _audioLastUnderrunSeenTicks;
        // Stopwatch ticks of the most recent ratchet action (up OR down).
        // 5-minute cooldown gates any DOWN step against this.
        private long _tfLastRatchetActionTicks;
        private long _audioLastRatchetActionTicks;

        // Fired on the producer thread when auto-ratchet bumps a ring size.
        // Args: isTfRing (true = Trueforce stream ring, false = audio ring),
        // oldCapacity, newCapacity. SettingsControl subscribes to show the
        // dismissable Revert/OK modal — must marshal to the UI thread.
        public event Action<bool, int, int> AutoRatchetBumped;

        // Per-car override tracking. Updated on each DataUpdate; if the CarId
        // changes we re-apply per-section overrides (or fall back to globals).
        private string _activeCarId;
        public string ActiveCarId => _activeCarId;

        // Active game + active preset tracking. Presets are a named library
        // (Settings.Presets) that the user can apply to any game. GameDefaults
        // optionally binds a game to auto-load a specific preset on game change.
        // _activePresetName is the most-recently-applied preset (or null if
        // current settings are unsaved/manually-tuned).
        private string _activeGame;
        private string _activePresetName;
        public string ActiveGame        => _activeGame;
        public string ActivePresetName  => _activePresetName;

        public IEnumerable<string> PresetNames =>
            Settings?.Presets != null ? (IEnumerable<string>)Settings.Presets.Keys : Array.Empty<string>();

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

                // 3. Wheel device state. WheelStatus is set by the discovery
                //    + open path; "Not detected" is the default.
                string wheel = WheelStatus ?? "";
                if (wheel.StartsWith("Not detected", StringComparison.OrdinalIgnoreCase))
                    return "Wheel not detected. Plug in your G PRO / RS50, or close any app that's holding the device exclusively.";
                if (wheel.StartsWith("Open failed", StringComparison.OrdinalIgnoreCase)
                 || wheel.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Wheel reports: {wheel}. Try unplugging and reconnecting the wheel.";

                // 4. HID stream state.
                string stream = StreamStatus ?? "";
                if (!stream.StartsWith("Streaming", StringComparison.OrdinalIgnoreCase))
                    return $"Wheel stream is '{stream}'. The plugin is opened but not actively driving the wheel — check the Diagnostics panel.";

                // 5. No game / not in session
                if (string.IsNullOrEmpty(_activeGame))
                    return "No game running. Start a supported game and load into a session.";

                var src = _telemetrySource;
                double hz = src?.MeasuredHz ?? 0;
                if (hz <= 0)
                    return $"'{_activeGame}' is detected but no telemetry is arriving. You may be in a menu or paused.";

                // 6. All telemetry-driven effects disabled. Engine pulse,
                //    bumps, traction, gear, ABS, pit limiter, DRS — if all
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

                // 7. Audio capture configured-on but not actually attached
                //    to a game process. Common when the user enabled audio
                //    capture but didn't pick the game's process.
                if (_audio != null && _audio.Enabled && _audio.IsActive == false
                    && _audio.CapturedProcessId == 0
                    && !string.IsNullOrEmpty(_activeGame))
                {
                    return $"Audio capture is enabled but not attached to '{_activeGame}'. Pick the game process in the Audio section.";
                }

                // 8. Sidechain ducker over-aggressive (engine pulse muted
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

        /// <summary>Cylinder + EngineConfig resolver coverage since plugin
        /// init. Counts increment exactly once per car-change resolution
        /// (cache rehits don't double-count). Surfaced in the settings UI's
        /// firing-order section so users can see how often we're producing
        /// specific layout info vs falling back to Auto.</summary>
        public string EngineConfigCoverageText
        {
            get
            {
                var (cylBaked, cylHeu, cfgBaked, cfgHeu) = CarCylinderResolver.GetCoverageCounters();
                int cylTotal = cylBaked + cylHeu;
                int cfgTotal = cfgBaked + cfgHeu;
                if (cylTotal == 0) return "No cars resolved yet.";
                return $"Resolved {cylTotal} car(s) this session: " +
                       $"cylinders {cylBaked} baked + {cylHeu} heuristic, " +
                       $"engine config {cfgBaked} baked + {cfgHeu} heuristic " +
                       $"({(cylTotal == 0 ? 0 : cfgTotal * 100 / cylTotal)}% layout coverage).";
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
                finally { System.Threading.Interlocked.Decrement(ref _activeTestTasks); }
            });
        }

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Trueforce] Init: loading settings...");
            Settings = this.ReadCommonSettings("GeneralSettings", () => new TrueforceSettings());
            // Defensive nulls in case a pre-2.x settings file was deserialized
            // without the new dictionaries.
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            if (Settings.GameEnabled  == null) Settings.GameEnabled  = new Dictionary<string, bool>();
            if (Settings.Performance  == null) Settings.Performance  = new PerformanceSettings();
            if (Settings.Forza        == null) Settings.Forza        = new ForzaSettings();
            if (Settings.CarCylinderCache == null)
                Settings.CarCylinderCache = new Dictionary<string, Dictionary<string, int>>();

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

            _mixer.MasterGain = Settings.MasterGain;

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
            WheelStatus = $"{match.Model}  (VID 0x{match.Vid:X4}, PID 0x{match.Pid:X4})";
            SimHub.Logging.Current.Info($"[Trueforce] Found {WheelStatus}.");

            try
            {
                _device = new TrueforceDevice(match.Device);
                _device.Open();

                // Init sequence is required: empirically, skipping it leaves the
                // wheel in slower-default-rate mode and Trueforce response is
                // noticeably delayed (~game tick of latency). It does NOT cause
                // the FFB-suppression problem either way — diagnosed 2026-05-03.
                SimHub.Logging.Current.Info("[Trueforce] Sending init sequence (68 packets x 2)...");
                _device.RunInitSequence();

                // Spawn the USBPcap FFB tap. Reads AC's outgoing HID++ FFB target
                // off the bus and feeds it to TrueforceDevice so we can mirror it
                // into ep3 bytes 6-9 — without this, our ep3 stream overrides AC's
                // FFB with zero motor torque whenever Trueforce content plays.
                // No args = auto-discover via WheelUsbDiscovery; env vars override
                // for debugging or unusual setups.
                string ifaceOverride = Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_INTERFACE");
                int.TryParse(Environment.GetEnvironmentVariable("SIMHUBTF_USBPCAP_DEVICE"), out var devOverride);
                _ffbTap = new UsbPcapFfbTap(ifaceOverride, devOverride)
                {
                    Logger = msg => SimHub.Logging.Current.Info($"[Trueforce] {msg}"),
                };
                _device.FfbTargetProvider = () =>
                {
                    // SkipFfbPassthrough: return Some(0) so the device sends
                    // active packets (audio plays) but writes center to ep3
                    // bytes 6-9, leaving the wheel's actual force to the
                    // game's native FFB path. Used for games with built-in
                    // Trueforce (AC Rally, iRacing) where mirroring our
                    // captured FFB target fights with the game's own writes.
                    if (Settings != null && Settings.SkipFfbPassthrough) return (short?)0;
                    return _ffbTap?.TryGetFreshFfbTarget(_device.FfbTargetMaxAgeMs);
                };
                _device.FfbScale                 = Settings.FfbScale;
                _device.FfbInvertSign            = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs  = Settings.FfbSmoothTimeConstantMs;
                _device.FfbSpikeTamingEnabled    = Settings.FfbSpikeTamingEnabled;
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

                _ffbTap.Start();

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
            _effects = new TelemetryEffect[] { EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick, PitLimiter, Drs };
            foreach (var fx in _effects) _mixer.Add(fx);
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

            // Background GitHub-releases check. One-shot, fire-and-forget.
            // Result is stored on the UpdateChecker; the settings panel timer
            // tick reads IsUpdateAvailable and surfaces a banner. Failures
            // are silent — no banner if we can't reach GitHub. The CTS is
            // cancelled in End() so a stalled HTTP call can't outlive the
            // plugin instance and write to a dead UpdateChecker.
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
        }
        private System.Threading.CancellationTokenSource _updateCheckerCts;
        public System.Threading.CancellationToken UpdateCheckerToken
            => _updateCheckerCts?.Token ?? System.Threading.CancellationToken.None;

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

            try { _audio?.Dispose(); } catch { }
            _audio = null;

            try { _helperHost?.Dispose(); } catch { }
            _helperHost = null;

            try { _capturedProcess?.Dispose(); } catch { }
            _capturedProcess = null;

            // Wake the producer if it's parked inside PushFloats on a full
            // ring — the plugin's _shuttingDown flag doesn't propagate into
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
                if (!string.IsNullOrEmpty(gameName) && Settings?.GameDefaults != null
                    && Settings.GameDefaults.TryGetValue(gameName, out var presetName)
                    && !string.IsNullOrEmpty(presetName)
                    && Settings.Presets != null
                    && Settings.Presets.TryGetValue(presetName, out var snap) && snap != null)
                {
                    ApplyGamePreset(snap);
                    _activePresetName = presetName;
                    SimHub.Logging.Current.Info($"[Trueforce] Loaded preset '{presetName}' as default for '{gameName}'.");
                }

                // Per-game master enable: default true for unseen games, else
                // honor the saved choice. Don't persist here — game-change
                // shouldn't write back the same value we just read.
                if (Settings != null)
                {
                    bool wantEnabled = true;
                    if (!string.IsNullOrEmpty(gameName) && Settings.GameEnabled != null
                        && Settings.GameEnabled.TryGetValue(gameName, out var saved))
                        wantEnabled = saved;
                    if (Settings.PluginEnabled != wantEnabled)
                        SetPluginEnabled(wantEnabled, persistForActiveGame: false);
                }
            }

            // Track car changes and apply per-car override (or revert).
            string carId = data?.NewData?.CarId ?? data?.NewData?.CarModel;
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
                // New car — discard the previous car's auto-detected cylinder
                // count so the next telemetry frame populates fresh from the
                // new car's NumCylinders. (No-op for non-Forza sources that
                // never fill it.)
                if (EnginePulse != null)
                {
                    EnginePulse.AutoCylinders = null;
                    EnginePulse.IsElectric = false;
                    EnginePulse.AutoCylinderSource = null;

                    // Seed AutoCylinders from baked lookup / heuristic for
                    // games that don't ship cylinder count in telemetry
                    // (AC, etc.). Forza sets NumCylinders directly each
                    // frame and will overwrite this on the next tick — no
                    // conflict, since the values agree for any car in both
                    // lookups. EVs leave AutoCylinders null (so the user's
                    // configured Cylinders stays in effect) and the IsElectric
                    // flag drives AutoGainScale via the user's ElectricMode
                    // setting (MutedHum=0.5x, Silent=0x). ApplyEngineSettings
                    // below sets ElectricMode from the active preset.
                    if (CarCylinderResolver.TryResolve(_activeGame, carId, out var carSpec))
                    {
                        if (carSpec.IsElectric)
                        {
                            EnginePulse.IsElectric = true;
                        }
                        else
                        {
                            EnginePulse.AutoCylinders = carSpec.Cylinders;
                        }
                        EnginePulse.AutoCylinderSource = carSpec.Source;
                        // Seed firing-order layout from the resolver. Only writes
                        // when the active preset has EngineConfig=Auto so an
                        // explicit user override (e.g. "I want this Mustang to
                        // sound flat-plane") wins. Saved EngineConfig != Auto
                        // is treated as the user's intentional choice.
                        if (carSpec.EngineConfig != Effects.EngineConfig.Auto
                            && EnginePulse.EngineConfig == Effects.EngineConfig.Auto)
                        {
                            EnginePulse.EngineConfig = carSpec.EngineConfig;
                        }
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Car '{carId}' resolved: cyl={carSpec.Cylinders}, "
                            + $"electric={carSpec.IsElectric}, source={carSpec.Source}, "
                            + $"engineConfig={carSpec.EngineConfig} ({carSpec.EngineConfigSource ?? "auto"})");
                    }
                    else if (_telemetrySource?.ProvidesNumCylinders == true)
                    {
                        // Resolver missed but the active source will populate
                        // NumCylinders shortly (Forza UDP). Label the source
                        // now so the UI doesn't briefly show "couldn't detect"
                        // between car-change and first frame. AutoCylinders
                        // itself stays null until OnTelemetry runs.
                        EnginePulse.AutoCylinderSource = "telemetry";
                    }
                    else if (!string.IsNullOrEmpty(carId))
                    {
                        SimHub.Logging.Current.Info(
                            $"[Trueforce] Car '{carId}' not auto-resolved — user can set cylinders manually.");
                    }
                }
                ApplyActiveCarOverride();
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

            // Cache slow-rate fields for the enhanced-source overlay step in
            // DispatchFrame. Always populated from SimHub regardless of which
            // source is currently dispatching, so the cache stays warm during
            // an enhanced run and is immediately available when AC starts.
            var nd = data?.NewData;
            if (nd != null)
            {
                _lastSimHubMaxRpm    = nd.MaxRpm;
                _lastSimHubAbsActive = nd.ABSActive;
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
        }

        // ---------- Performance auto-ratchet ----------

        /// <summary>Polled from ProducerLoop. Once per RatchetWindowMs, snapshots
        /// the device + audio glitch counters and bumps the corresponding ring
        /// capacity if the per-window delta crossed RatchetThreshold. One-way
        /// only — never shrinks. Survived capacities are persisted to Settings
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
            // starts clean has a real quiet-window anchor — without this,
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
            if (tfDelta >= RatchetThreshold && perf.TfRingSize < TrueforceDevice.MaxRingSize)
            {
                int oldCap = perf.TfRingSize;
                int newCap = oldCap * 2;
                if (newCap > TrueforceDevice.MaxRingSize) newCap = TrueforceDevice.MaxRingSize;
                ApplyTfRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet UP: Trueforce ring {oldCap} → {newCap} after {tfDelta} underruns/s.");
                _tfLastRatchetActionTicks = now;
                FireRatchetEvent(true, oldCap, newCap);
            }

            if (audioDelta >= RatchetThreshold && perf.AudioRingSize < AudioCaptureSource.MaxRingSamples)
            {
                int oldCap = perf.AudioRingSize;
                int newCap = oldCap * 2;
                if (newCap > AudioCaptureSource.MaxRingSamples) newCap = AudioCaptureSource.MaxRingSamples;
                ApplyAudioRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet UP: audio ring {oldCap} → {newCap} after {audioDelta} glitches/s.");
                _audioLastRatchetActionTicks = now;
                FireRatchetEvent(false, oldCap, newCap);
            }

            // ----- Ratchet DOWN -----
            // Conditions, all must hold:
            //   - capacity is above its minimum
            //   - quiet window: no underruns/glitches for >= RatchetDownQuietMs
            //   - cooldown: no ratchet action (up or down) for
            //     >= RatchetDownCooldownMs
            // Quiet operation: log entry only, no UI event fire (don't want
            // a modal interrupting the user every minute as the ring drains).
            long quietTicks    = Stopwatch.Frequency * RatchetDownQuietMs    / 1000L;
            long cooldownTicks = Stopwatch.Frequency * RatchetDownCooldownMs / 1000L;

            if (perf.TfRingSize > TrueforceDevice.MinRingSize
                && _tfLastUnderrunSeenTicks  != 0
                && (now - _tfLastUnderrunSeenTicks)    >= quietTicks
                && (_tfLastRatchetActionTicks == 0
                    || (now - _tfLastRatchetActionTicks) >= cooldownTicks))
            {
                int oldCap = perf.TfRingSize;
                int newCap = oldCap / 2;
                if (newCap < TrueforceDevice.MinRingSize) newCap = TrueforceDevice.MinRingSize;
                ApplyTfRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet DOWN: Trueforce ring {oldCap} → {newCap} after {RatchetDownQuietMs / 1000} s of quiet.");
                _tfLastRatchetActionTicks = now;
                // Re-arm the cooldown for the next step too — DOWN is
                // gradual, one notch at a time, with another full cooldown
                // period between steps. Avoids halving twice in a single
                // tick if we'd been quiet for hours.
            }

            if (perf.AudioRingSize > AudioCaptureSource.MinRingSamples
                && _audioLastUnderrunSeenTicks  != 0
                && (now - _audioLastUnderrunSeenTicks)    >= quietTicks
                && (_audioLastRatchetActionTicks == 0
                    || (now - _audioLastRatchetActionTicks) >= cooldownTicks))
            {
                int oldCap = perf.AudioRingSize;
                int newCap = oldCap / 2;
                if (newCap < AudioCaptureSource.MinRingSamples) newCap = AudioCaptureSource.MinRingSamples;
                ApplyAudioRingSize(newCap);
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Auto-ratchet DOWN: audio ring {oldCap} → {newCap} after {RatchetDownQuietMs / 1000} s of quiet.");
                _audioLastRatchetActionTicks = now;
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
            if (IsForzaGameName(game)) return true;
            // Always-listen lets users force the Forza source on for an FH6
            // build SimHub doesn't know yet, etc.
            if (Settings?.Forza?.AlwaysListen == true && Settings.Forza.Enabled) return true;
            return false;
        }

        /// <summary>True when the Forza UDP section should be visible in the
        /// settings UI. Shown only when a Forza title is active or when the
        /// user has AlwaysListen enabled (so they can find the toggle to
        /// turn it off without launching Forza first). Hidden otherwise to
        /// keep the panel uncluttered for non-Forza users.</summary>
        public bool ShouldShowForzaSection =>
            IsForzaGameName(_activeGame)
            || (Settings?.Forza?.AlwaysListen == true);

        /// <summary>True if SimHub's GameName looks like a Forza title.
        /// Conservative match against the known names — extending if FH6
        /// ships with a slightly different convention is one literal away.</summary>
        private static bool IsForzaGameName(string game)
        {
            if (string.IsNullOrEmpty(game)) return false;
            return game == "ForzaHorizon5"
                || game == "ForzaHorizon6"
                || game == "ForzaHorizon4"
                || game == "ForzaMotorsport"
                || game == "ForzaMotorsport8"
                || game == "ForzaMotorsport7";
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
            else if (IsForzaGameName(game)
                     || (Settings?.Forza?.AlwaysListen == true && Settings.Forza.Enabled))
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
            if (newSource == null) newSource = _simHubSource;
            if (newSource == _telemetrySource) return;

            // Detach old's dispatch BEFORE attaching new's so DispatchFrame is
            // never invoked from two threads concurrently. Both fields are
            // ref-typed; .NET guarantees torn-tear-safe writes.
            var old = _telemetrySource;
            if (old != null) old.OnFrame = null;
            newSource.OnFrame = DispatchFrame;
            _telemetrySource = newSource;

            // Dispose the previous enhanced source. _simHubSource is the
            // long-lived fallback and stays alive for the plugin's lifetime.
            if (old != null && old != _simHubSource && old != newSource)
            {
                try { old.Dispose(); } catch { }
            }

            SimHub.Logging.Current.Info(
                $"[Trueforce] Telemetry source: {newSource.Name} (enhanced={newSource.IsEnhanced}).");
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
        // invalid — the source treats null as "don't forward." Hostname (vs
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
        /// if the active source is Forza. Settings are saved unconditionally;
        /// the source rebind is a no-op when Forza isn't currently active.</summary>
        public void ApplyForzaSettings()
        {
            if (Settings?.Forza == null) return;
            this.SaveCommonSettings("GeneralSettings", Settings);

            // Re-evaluate source choice for the active game so a port change
            // takes effect immediately. SwapTelemetrySource handles the case
            // where Forza isn't currently active (no-op) and the case where
            // it's active (tear down + rebind on new port).
            if (!string.IsNullOrEmpty(_activeGame)
                && (IsForzaGameName(_activeGame)
                    || (Settings.Forza.AlwaysListen && Settings.Forza.Enabled)))
            {
                // Force a rebuild by routing through the existing fallback
                // first so the dispose path runs cleanly.
                if (_telemetrySource is ForzaUdpTelemetrySource)
                {
                    var oldFz = _telemetrySource;
                    if (oldFz != null) oldFz.OnFrame = null;
                    _simHubSource.OnFrame = DispatchFrame;
                    _telemetrySource = _simHubSource;
                    try { oldFz?.Dispose(); } catch { }
                }
                SwapTelemetrySource(_activeGame);
            }
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
        public bool IsAudioOverridden      => GetActiveCarOverride()?.AudioCapture != null;

        // ----- per-section: toggle override on/off (snapshots globals when on) -----
        public void SetEngineOverride(bool on)     => ToggleSectionOverride(on, get: o => o.EnginePulse,  set: (o, v) => o.EnginePulse  = v, snapshot: () => Clone(Settings.EnginePulse));
        public void SetBumpsOverride(bool on)      => ToggleSectionOverride(on, get: o => o.RoadBumps,    set: (o, v) => o.RoadBumps    = v, snapshot: () => Clone(Settings.RoadBumps));
        public void SetTractionOverride(bool on)   => ToggleSectionOverride(on, get: o => o.TractionLoss, set: (o, v) => o.TractionLoss = v, snapshot: () => Clone(Settings.TractionLoss));
        public void SetShiftOverride(bool on)      => ToggleSectionOverride(on, get: o => o.GearShift,    set: (o, v) => o.GearShift    = v, snapshot: () => Clone(Settings.GearShift));
        public void SetAbsOverride(bool on)        => ToggleSectionOverride(on, get: o => o.AbsClick,     set: (o, v) => o.AbsClick     = v, snapshot: () => Clone(Settings.AbsClick));
        public void SetPitLimiterOverride(bool on) => ToggleSectionOverride(on, get: o => o.PitLimiter,   set: (o, v) => o.PitLimiter   = v, snapshot: () => Clone(Settings.PitLimiter));
        public void SetDrsOverride(bool on)        => ToggleSectionOverride(on, get: o => o.Drs,          set: (o, v) => o.Drs          = v, snapshot: () => Clone(Settings.Drs));
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
        public AudioCaptureSettings ActiveAudio    => GetActiveCarOverride()?.AudioCapture ?? Settings.AudioCapture;

        // ----- apply settings to live effect -----
        private void ApplyEngineSettings(EnginePulseSettings s)
        {
            if (EnginePulse == null || s == null) return;
            EnginePulse.Enabled            = s.Enabled;
            EnginePulse.Gain               = s.Gain;
            EnginePulse.Cylinders          = s.Cylinders;
            EnginePulse.PitchMultiplier    = s.Pitch;
            EnginePulse.LowpassHz          = s.LowpassHz;
            EnginePulse.Waveform           = s.Waveform;
            EnginePulse.ElectricMode       = s.ElectricMode;
            EnginePulse.EngineConfig       = s.EngineConfig;
            EnginePulse.CustomPattern      = string.IsNullOrWhiteSpace(s.CustomFiringPattern)
                ? null
                : Effects.FiringPatternDb.ParseCustom(s.CustomFiringPattern);
            EnginePulse.FiringOrderEnabled = s.FiringOrderEnabled;
            // Auto-cylinders precedence is now derived from s.Cylinders:
            // 0 = "use auto-detected", 1..16 = explicit manual override.
            // EnginePulseEffect.UseAutoCylinders is computed from this so we
            // don't need to flip an external flag here. The IsEngineOverridden
            // flag still gates other plumbing (per-car preset detection,
            // savability) but no longer controls auto-cyl behavior.
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
            Drs.Enabled        = s.Enabled;
            Drs.Gain           = s.Gain;
            Drs.ActivationFreq = s.ActivationFreq;
            Drs.ActivationMs   = s.ActivationMs;
            Drs.ActivationAmp  = s.ActivationAmp;
            Drs.SustainedFreq  = s.SustainedFreq;
            Drs.SustainedAmp   = s.SustainedAmp;
            Drs.Waveform       = s.Waveform;
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
                Enabled = s.Enabled, Gain = s.Gain, Cylinders = s.Cylinders, Pitch = s.Pitch,
                LowpassHz = s.LowpassHz, Waveform = s.Waveform, ElectricMode = s.ElectricMode,
                FiringOrderEnabled = s.FiringOrderEnabled, EngineConfig = s.EngineConfig,
                CustomFiringPattern = s.CustomFiringPattern,
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
            => new DrsSettings          { Enabled = s.Enabled, Gain = s.Gain, ActivationFreq = s.ActivationFreq, ActivationMs = s.ActivationMs, ActivationAmp = s.ActivationAmp, SustainedFreq = s.SustainedFreq, SustainedAmp = s.SustainedAmp, Waveform = s.Waveform };

        // ---------- preset library ----------

        /// <summary>Install any built-in preset that isn't already in the
        /// user's library. Run on every Init — idempotent. Also auto-binds
        /// the matching <see cref="BuiltinPresets.GameDefaultBindings"/>
        /// entries as the game's default IF the user has no default for
        /// that game yet (we don't override their custom choice).</summary>
        private void InstallBuiltinPresetsIfMissing()
        {
            if (Settings == null) return;
            if (Settings.Presets      == null) Settings.Presets      = new Dictionary<string, GameSettingsSnapshot>();
            if (Settings.GameDefaults == null) Settings.GameDefaults = new Dictionary<string, string>();
            int added = 0;
            foreach (var kv in BuiltinPresets.BuiltinPresetJsons)
            {
                if (Settings.Presets.ContainsKey(kv.Key)) continue;
                try
                {
                    var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSettingsSnapshot>(kv.Value);
                    if (snap != null)
                    {
                        Settings.Presets[kv.Key] = snap;
                        added++;
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
            if (added > 0)
            {
                this.SaveCommonSettings("GeneralSettings", Settings);
                SimHub.Logging.Current.Info($"[Trueforce] Installed {added} built-in preset(s).");
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
            int migrated = 0;
            foreach (var kv in new Dictionary<string, CarOverride>(Settings.CarOverrides))
            {
                if (kv.Value == null || kv.Value.IsEmpty) continue;
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

            if (migrated > 0 || builtinsWritten > 0)
                SimHub.Logging.Current.Info(
                    $"[Trueforce] Car presets: migrated {migrated} legacy entries, wrote {builtinsWritten} builtin preset(s).");
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
                case SectionKind.Audio:      if (ovr.AudioCapture != null) { Settings.AudioCapture = CloneOrNull(ovr.AudioCapture); ovr.AudioCapture = null; } break;
                default: return;
            }
            if (ovr.IsEmpty) Settings.CarOverrides.Remove(_activeCarId);
            PersistActiveCarOverride();
            ApplyActiveCarOverride();
        }

        /// <summary>True if the named preset is a built-in / read-only one.
        /// Built-ins refuse delete and refuse in-place overwrite — the UI
        /// forks to a user-named preset instead.</summary>
        public bool IsBuiltinPreset(string presetName) => BuiltinPresets.IsBuiltin(presetName);

        /// <summary>One-time migration of legacy per-game presets (keyed by
        /// game name with no separate "preset library" concept) into the new
        /// model: each becomes a preset named after the game, and the game's
        /// default is bound to it. Idempotent — runs once when GamePresets is
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

        // ---------- per-section dirty check (vs active preset) ----------

        /// <summary>True iff the current values for this section differ from
        /// the active preset's snapshot. False when there's no active preset
        /// (no anchor). Used by the UI to show/hide per-section "Save" /
        /// "Revert" buttons based on actual drift, not on a sticky flag —
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
        // equal — which is what users expect when they drag a slider away
        // and back. Without these, slider-snap noise stays "dirty" forever.
        private static bool EqF2(double a, double b) => Math.Abs(a - b) < 0.005;
        private static bool EqF1(double a, double b) => Math.Abs(a - b) < 0.05;
        private static bool EqI (double a, double b) => Math.Abs(a - b) < 0.5;

        private enum EffectField { Audio, Engine, Bumps, Traction, Shift, Abs, PitLimiter, Drs }

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
            }
            return true;
        }

        private static bool Eq(EnginePulseSettings a, EnginePulseSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain,      b.Gain)
                && a.Cylinders == b.Cylinders
                && EqF2(a.Pitch,     b.Pitch)
                && EqI (a.LowpassHz, b.LowpassHz)
                && a.Waveform == b.Waveform
                && a.ElectricMode == b.ElectricMode
                && a.FiringOrderEnabled == b.FiringOrderEnabled
                && a.EngineConfig == b.EngineConfig
                && string.Equals(a.CustomFiringPattern ?? "", b.CustomFiringPattern ?? "", System.StringComparison.Ordinal);
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
        public enum SectionKind { Master, Ducking, Audio, Engine, Bumps, Traction, Shift, Abs, SpikeReduction, PitLimiter, Drs }

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
                    Settings.FfbSpikeTamingEnabled = snap.FfbSpikeTamingEnabled;
                    Settings.FfbSpikeMaxLsbPerMs   = snap.FfbSpikeMaxLsbPerMs;
                    Settings.FfbPeakSoftLimitLsb   = snap.FfbPeakSoftLimitLsb;
                    if (_device != null)
                    {
                        _device.FfbSpikeTamingEnabled = Settings.FfbSpikeTamingEnabled;
                        _device.FfbSpikeMaxLsbPerMs   = Settings.FfbSpikeMaxLsbPerMs;
                        _device.FfbPeakSoftLimitLsb   = Settings.FfbPeakSoftLimitLsb;
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

        /// <summary>Generic per-effect revert: routes the snapshot's saved
        /// section into either the per-car override slot (if the snapshot
        /// had one for the current car) or the global slot (otherwise,
        /// dropping any current car override). Caller is responsible for
        /// pushing the resulting state live (ApplyActiveCarOverride etc.).</summary>
        private void RevertEffectScopeAware<TSection>(
            TSection snapGlobal,
            Dictionary<string, CarOverride> snapOverrides,
            Func<CarOverride, TSection> getSnapCarSection,
            Action<TSection> applyToGlobal,
            Action<CarOverride, TSection> applyToCarOverride,
            Action<CarOverride> clearCarOverride) where TSection : class
        {
            string carId = _activeCarId;
            CarOverride snapCar = null;
            if (carId != null && snapOverrides != null) snapOverrides.TryGetValue(carId, out snapCar);
            var snapCarSection = snapCar != null ? getSnapCarSection(snapCar) : null;

            if (snapCarSection != null && carId != null)
            {
                if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();
                if (!Settings.CarOverrides.TryGetValue(carId, out var liveCo) || liveCo == null)
                    Settings.CarOverrides[carId] = liveCo = new CarOverride();
                applyToCarOverride(liveCo, snapCarSection);
            }
            else
            {
                if (carId != null && Settings.CarOverrides != null
                    && Settings.CarOverrides.TryGetValue(carId, out var liveCo) && liveCo != null)
                    clearCarOverride(liveCo);
                if (snapGlobal != null) applyToGlobal(snapGlobal);
            }
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
        /// as the active preset. Refuses to overwrite built-in presets — the
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

        /// <summary>Delete a preset from the library. Also clears any
        /// GameDefaults entries that pointed to this preset. Refuses on
        /// built-in presets — they're factory defaults the user can always
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

        /// <summary>Copy snapshot fields into Settings and re-push to live components.</summary>
        private void ApplyGamePreset(GameSettingsSnapshot snap)
        {
            if (snap == null || Settings == null) return;

            Settings.MasterGain              = snap.MasterGain;
            Settings.FfbScale                = snap.FfbScale;
            Settings.FfbInvertSign           = snap.FfbInvertSign;
            Settings.FfbSmoothTimeConstantMs = snap.FfbSmoothTimeConstantMs;
            Settings.FfbSpikeTamingEnabled   = snap.FfbSpikeTamingEnabled;
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
            // Per-car overrides are no longer carried by game presets (Model G):
            // they live in <plugin data>/Cars/<carId>.tfcar.json files,
            // independent of the active preset. Switching presets doesn't
            // touch per-car tuning. Legacy snap.CarOverrides (from old
            // saved presets) is intentionally ignored here — migration
            // already extracted any useful data into per-car files.

            // Push live: master, FFB tap, audio, and effects (via car-override apply).
            _mixer.MasterGain = Settings.MasterGain;
            if (_device != null)
            {
                _device.FfbScale                = Settings.FfbScale;
                _device.FfbInvertSign           = Settings.FfbInvertSign;
                _device.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
                _device.FfbSpikeTamingEnabled   = Settings.FfbSpikeTamingEnabled;
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
                    AudioCapture = CloneOrNull(o.AudioCapture),
                };
            }
            return d;
        }

        // ---------- single-preset export/import (sharing) ----------

        /// <summary>Snapshot of the current top-level settings — used by
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
                // CarOverrides intentionally omitted — per-car tuning is
                // managed via per-car .tfcar.json files post-Model-G.
            };
        }

        /// <summary>Write a named preset (or the current settings if the name
        /// doesn't exist in the library yet) to a shareable JSON file. The
        /// file carries the preset name but no game binding.</summary>
        public void ExportPreset(string presetName, string path)
        {
            if (Settings == null || string.IsNullOrEmpty(presetName)) return;
            GameSettingsSnapshot snap;
            if (Settings.Presets == null || !Settings.Presets.TryGetValue(presetName, out snap) || snap == null)
                snap = SnapshotCurrentAsPreset();
            var file = new PresetFile { PresetName = presetName, Snapshot = snap };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported preset '{presetName}' to {path}.");
        }

        /// <summary>Read a preset file and store it in the library under the
        /// name embedded in the file. Does NOT auto-apply or auto-bind to a
        /// game — the user explicitly chooses what to do with it next.</summary>
        /// <returns>The preset name imported (for UI feedback).</returns>
        public string ImportPreset(string path)
        {
            if (Settings == null) return null;
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<PresetFile>(json);
            if (file == null || file.Snapshot == null || string.IsNullOrEmpty(file.PresetName))
                throw new System.IO.InvalidDataException("Not a valid Trueforce preset file.");
            if (file.Type != PresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{PresetFile.FileType}'.");

            if (Settings.Presets == null) Settings.Presets = new Dictionary<string, GameSettingsSnapshot>();
            Settings.Presets[file.PresetName] = file.Snapshot;
            this.SaveCommonSettings("GeneralSettings", Settings);

            SimHub.Logging.Current.Info($"[Trueforce] Imported preset '{file.PresetName}' from {path}.");
            return file.PresetName;
        }

        /// <summary>Export the active car's override as a standalone file.
        /// If no override exists yet, captures the current ActiveX section
        /// values so the user can share their tuning without committing it
        /// to a per-car override first.</summary>
        public void ExportActiveCarPreset(string path)
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
            // false on export — only the plugin's bundled factory files are
            // built-ins; an exported community preset is always user-tier.
            string presetName = GetActiveCarPresetName(_activeCarId) ?? _activeCarId;
            var file = new CarPresetFile
            {
                GameName   = _activeGame,
                CarId      = _activeCarId,
                PresetName = StripDefaultSuffixForExport(presetName),
                IsBuiltin  = false,
                Override   = ovr,
            };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset '{file.PresetName}' for '{_activeCarId}' to {path}.");
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
        /// <returns>The car id from the imported file (for UI feedback).</returns>
        public string ImportCarPreset(string path)
        {
            if (Settings == null || _carStore == null) return null;
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(json);
            if (file == null || file.Override == null || string.IsNullOrEmpty(file.CarId))
                throw new System.IO.InvalidDataException("Not a valid Trueforce car-preset file.");
            if (file.Type != CarPresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{CarPresetFile.FileType}'.");

            // PresetName may be missing on legacy v1 imports — fall back to
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
            return file.CarId;
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
        // We pull the same data SimHub uses for game detection — the user
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

                    // Process exited — tear down and fall through to the scan.
                    SimHub.Logging.Current.Info($"[Trueforce] Captured process {_capturedProcess.Id} exited; releasing.");
                    try { _capturedProcess.Dispose(); } catch { }
                    _capturedProcess = null;
                    _audio.Stop();
                    _helperHost?.SetTargetPid(0);
                }

                // Scan path: walk the process table once. Match priority:
                //   1. Per-game user override (Settings.AudioCaptureExeOverrides)
                //      — highest priority so users can fix any miss.
                //   2. Curated exe→label dict (known-quirky exes like ACC's
                //      "AC2-Win64-Shipping" or AC's short "acs").
                //   3. SimHub Custom-game ProcessNames (read from
                //      CustomGames.json — same source SimHub uses for game
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
        // u16 conversion produces exactly 0x8000 — TrueforceDevice's silence
        // detection requires exact-center samples to choose the keepalive packet
        // shape. ~3e-4 corresponds to ±10 LSB out of 32767, well below any
        // perceptible content but above floating-point noise.
        private const float SilenceFloor = 3e-4f;

        // Sidechain ducking state. Three buses, all driven by max-activity
        // of relevant effects with the same depth/attack/release params:
        //
        //   _duckSmoothed           — Bus 1. Driven by ALL transients +
        //                             modal flags (RoadBumps, TractionLoss,
        //                             GearShift, AbsClick, PitLimiter, Drs).
        //                             Applied to EnginePulse + audio capture
        //                             so any "event" haptic ducks the
        //                             continuous background.
        //   _duckSmoothedMomentary  — Bus 2. Truly-momentary transients only
        //                             (RoadBumps, GearShift, AbsClick) —
        //                             excludes the sustained ones.
        //                             Applied to TractionLoss so a sustained
        //                             slide doesn't drown out an ABS pump
        //                             or curb hit on top of it.
        //   _duckSmoothedDrsSustained — Bus 3. Driven by the transients that
        //                             should override a held-DRS hum
        //                             (AbsClick, TractionLoss, GearShift).
        //                             Applied to DrsEffect.SustainedDuck-
        //                             Multiplier — only the sustained tone;
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

            // Bus 2: truly-momentary transients only — excludes TractionLoss
            // because a sustained slide is itself a "constant effect" relative
            // to the impulse-shaped events below. PitLimiter / Drs sustained
            // are also excluded — they represent ongoing modes, not
            // momentary events.
            double maxMomentary = 0;
            if (RoadBumps != null) maxMomentary = Math.Max(maxMomentary, RoadBumps.ActivityLevel);
            if (GearShift != null) maxMomentary = Math.Max(maxMomentary, GearShift.ActivityLevel);
            if (AbsClick  != null) maxMomentary = Math.Max(maxMomentary, AbsClick.ActivityLevel);

            // Bus 3: drives DRS sustained ducking. ABS pumps, traction loss,
            // gear shifts all happen "on top of" a held DRS — those
            // signals matter more in the moment than the DRS hum, so we
            // duck the hum to make room for them.
            double maxDrsTransient = 0;
            if (AbsClick     != null) maxDrsTransient = Math.Max(maxDrsTransient, AbsClick.ActivityLevel);
            if (TractionLoss != null) maxDrsTransient = Math.Max(maxDrsTransient, TractionLoss.ActivityLevel);
            if (GearShift    != null) maxDrsTransient = Math.Max(maxDrsTransient, GearShift.ActivityLevel);

            float depth     = Settings?.DuckDepth     ?? 0.5f;
            float attackMs  = Settings?.DuckAttackMs  ?? 5.0f;
            float releaseMs = Settings?.DuckReleaseMs ?? 80.0f;

            // IIR with attack-or-release time constant (dt ≈ 1 ms — producer
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
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}
