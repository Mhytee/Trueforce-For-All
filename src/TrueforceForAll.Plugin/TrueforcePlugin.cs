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

        // Snapshot of each car's override AS OF its last save. Used by
        // IsSectionDirty to tell whether an override section has been
        // edited since the last "For this car" save, without re-reading
        // the file. Updated on every PersistActiveCarOverride; invalidated
        // by ResetCarToGameDefaults.
        private Dictionary<string, CarOverride> _lastPersistedCarOverrides = new Dictionary<string, CarOverride>();

        private TrueforceDevice _device;
        private AudioCaptureSource _audio;
        private HelperHost _helperHost;
        private UsbPcapFfbTap _ffbTap;
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
        public int    ActiveVoiceCount => _mixer.Sources.Count;
        public AudioCaptureSource AudioCapture => _audio;

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
            MigrateLegacyGamePresets();
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
                _device.FfbSpikeMaxLsbPerMs      = Settings.FfbSpikeMaxLsbPerMs;
                _device.FfbPeakSoftLimitLsb      = Settings.FfbPeakSoftLimitLsb;
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
            if (_helperHost != null) _audio.Attach(_helperHost);
            _mixer.Sources.Add(_audio);

            // Telemetry effects: instantiate from settings, register in the
            // mixer in display order. Each effect is fed via the active
            // ITelemetrySource's OnFrame callback (see DispatchFrame below).
            EnginePulse  = new EnginePulseEffect();
            RoadBumps    = new RoadBumpsEffect();
            TractionLoss = new TractionLossEffect();
            GearShift    = new GearShiftEffect();
            AbsClick     = new AbsClickEffect();
            _effects = new TelemetryEffect[] { EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick };
            foreach (var fx in _effects) _mixer.Sources.Add(fx);
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

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;

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

        /// <summary>True when <paramref name="game"/> has a per-game enhanced
        /// source we should attempt to instantiate. Used both at game-change
        /// and as the gate for the periodic retry loop.</summary>
        private static bool IsEnhancedEligible(string game) => game == "AssettoCorsa";

        /// <summary>Pick the right ITelemetrySource for <paramref name="game"/>
        /// (AC's MMF reader for "AssettoCorsa", SimHub fallback otherwise) and
        /// hand-off OnFrame so exactly one source dispatches at a time. Called
        /// from DataUpdate on the SimHub data thread; the new source's polling
        /// thread is fully started before the old source is detached, so the
        /// briefest possible window of "no dispatch" covers the swap.
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
            ApplyAudioCaptureSettings(ovr?.AudioCapture ?? Settings.AudioCapture);
        }

        // ----- per-section: is this section overridden for the active car? -----
        public bool IsEngineOverridden  => GetActiveCarOverride()?.EnginePulse  != null;
        public bool IsBumpsOverridden   => GetActiveCarOverride()?.RoadBumps    != null;
        public bool IsTractionOverridden=> GetActiveCarOverride()?.TractionLoss != null;
        public bool IsShiftOverridden   => GetActiveCarOverride()?.GearShift    != null;
        public bool IsAbsOverridden     => GetActiveCarOverride()?.AbsClick     != null;
        public bool IsAudioOverridden   => GetActiveCarOverride()?.AudioCapture != null;

        // ----- per-section: toggle override on/off (snapshots globals when on) -----
        public void SetEngineOverride(bool on)   => ToggleSectionOverride(on, get: o => o.EnginePulse,  set: (o, v) => o.EnginePulse  = v, snapshot: () => Clone(Settings.EnginePulse));
        public void SetBumpsOverride(bool on)    => ToggleSectionOverride(on, get: o => o.RoadBumps,    set: (o, v) => o.RoadBumps    = v, snapshot: () => Clone(Settings.RoadBumps));
        public void SetTractionOverride(bool on) => ToggleSectionOverride(on, get: o => o.TractionLoss, set: (o, v) => o.TractionLoss = v, snapshot: () => Clone(Settings.TractionLoss));
        public void SetShiftOverride(bool on)    => ToggleSectionOverride(on, get: o => o.GearShift,    set: (o, v) => o.GearShift    = v, snapshot: () => Clone(Settings.GearShift));
        public void SetAbsOverride(bool on)      => ToggleSectionOverride(on, get: o => o.AbsClick,     set: (o, v) => o.AbsClick     = v, snapshot: () => Clone(Settings.AbsClick));
        public void SetAudioOverride(bool on)    => ToggleSectionOverride(on, get: o => o.AudioCapture, set: (o, v) => o.AudioCapture = v, snapshot: () => CloneOrNull(Settings.AudioCapture));

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
        public AbsClickSettings     ActiveAbs      => GetActiveCarOverride()?.AbsClick     ?? Settings.AbsClick;
        public AudioCaptureSettings ActiveAudio    => GetActiveCarOverride()?.AudioCapture ?? Settings.AudioCapture;

        // ----- apply settings to live effect -----
        private void ApplyEngineSettings(EnginePulseSettings s)
        {
            if (EnginePulse == null || s == null) return;
            EnginePulse.Enabled         = s.Enabled;
            EnginePulse.Gain            = s.Gain;
            EnginePulse.Cylinders       = s.Cylinders;
            EnginePulse.PitchMultiplier = s.Pitch;
            EnginePulse.LowpassHz       = s.LowpassHz;
            EnginePulse.Waveform        = s.Waveform;
        }
        private void ApplyBumpsSettings(RoadBumpsSettings s)
        {
            if (RoadBumps == null || s == null) return;
            RoadBumps.Enabled  = s.Enabled;
            RoadBumps.Gain     = s.Gain;
            RoadBumps.Waveform = s.Waveform;
            RoadBumps.Freq     = s.Freq;
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
            => new EnginePulseSettings  { Enabled = s.Enabled, Gain = s.Gain, Cylinders = s.Cylinders, Pitch = s.Pitch, LowpassHz = s.LowpassHz, Waveform = s.Waveform };
        private static RoadBumpsSettings    Clone(RoadBumpsSettings s)
            => new RoadBumpsSettings    { Enabled = s.Enabled, Gain = s.Gain, Waveform = s.Waveform, Freq = s.Freq };
        private static TractionLossSettings Clone(TractionLossSettings s)
            => new TractionLossSettings { Enabled = s.Enabled, Gain = s.Gain, Sensitivity = s.Sensitivity, Waveform = s.Waveform, Freq = s.Freq, NoiseLowpassHz = s.NoiseLowpassHz, NoiseHighpassHz = s.NoiseHighpassHz };
        private static GearShiftSettings    Clone(GearShiftSettings s)
            => new GearShiftSettings    { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, Waveform = s.Waveform };
        private static AbsClickSettings     Clone(AbsClickSettings s)
            => new AbsClickSettings     { Enabled = s.Enabled, Gain = s.Gain, Freq = s.Freq, PulseFreq = s.PulseFreq, DutyCycle = s.DutyCycle, TickDurationMs = s.TickDurationMs, Mode = s.Mode, Waveform = s.Waveform };

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
        /// Files are the canonical store post-Model-G; this routine seeds
        /// the store from any existing Settings.CarOverrides / preset.CarOverrides
        /// data on first run after upgrade, then loads files back into the
        /// in-memory cache (Settings.CarOverrides) so reads are fast.
        ///
        /// File-wins-on-conflict ordering: pre-existing files are authoritative,
        /// so re-running migration won't clobber files the user has already
        /// edited.</summary>
        private void LoadAndMigrateCarPresets()
        {
            if (Settings == null || _carStore == null) return;
            if (Settings.CarOverrides == null) Settings.CarOverrides = new Dictionary<string, CarOverride>();

            // 1) Migrate Settings.CarOverrides → files (only if no file yet).
            int migrated = 0;
            foreach (var kv in new Dictionary<string, CarOverride>(Settings.CarOverrides))
            {
                if (kv.Value == null || kv.Value.IsEmpty) continue;
                if (_carStore.Exists(kv.Key)) continue;
                _carStore.Save(kv.Key, _activeGame ?? "", kv.Value);
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
                        if (_carStore.Exists(carKv.Key)) continue;
                        _carStore.Save(carKv.Key, "", carKv.Value);
                        migrated++;
                    }
                }
            }
            // 3) Load all files back into the in-memory cache AND seed the
            //    last-persisted snapshot so freshly-loaded cars start clean.
            var loaded = _carStore.LoadAll();
            foreach (var kv in loaded)
            {
                Settings.CarOverrides[kv.Key] = kv.Value;
                _lastPersistedCarOverrides[kv.Key] = CloneCarOverride(kv.Value);
            }
            if (migrated > 0)
                SimHub.Logging.Current.Info($"[Trueforce] Migrated {migrated} per-car override(s) to per-car files.");
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
                AudioCapture = CloneOrNull(o.AudioCapture),
            };
        }

        /// <summary>Write the active car's override to its per-car file
        /// and update the last-persisted snapshot used for dirty checks.
        /// Called by explicit user save actions ("For this car",
        /// "Update game defaults"). NOT auto-called from slider edits —
        /// per-car saves are explicit.</summary>
        public void PersistActiveCarOverride()
        {
            if (_carStore == null || string.IsNullOrEmpty(_activeCarId) || Settings?.CarOverrides == null) return;
            Settings.CarOverrides.TryGetValue(_activeCarId, out var ovr);
            _carStore.Save(_activeCarId, _activeGame ?? "", ovr);
            // Refresh dirty baseline. Empty / null override → no saved file
            // exists, so drop from the snapshot too.
            if (ovr == null || ovr.IsEmpty)
                _lastPersistedCarOverrides.Remove(_activeCarId);
            else
                _lastPersistedCarOverrides[_activeCarId] = CloneCarOverride(ovr);
        }

        /// <summary>Wipe a car's per-car file AND its in-memory override.
        /// The car will fall back to the game preset's globals on its next
        /// detection.</summary>
        public void ResetCarToGameDefaults(string carId)
        {
            if (_carStore == null || string.IsNullOrEmpty(carId)) return;
            _carStore.Delete(carId);
            if (Settings?.CarOverrides != null) Settings.CarOverrides.Remove(carId);
            _lastPersistedCarOverrides.Remove(carId);
            if (carId == _activeCarId) ApplyActiveCarOverride();
        }

        /// <summary>"For this car" save action: ensures the section has a
        /// per-car override containing the section's current values, then
        /// persists the file. If no override exists yet, snapshots from
        /// globals (where current edits live). If override already exists,
        /// keeps the existing values (where current edits live) and just
        /// writes them through. Either way, the file matches the in-memory
        /// state after this call.</summary>
        public void SaveSectionForCar(SectionKind kind)
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
                case SectionKind.Abs:      if (ovr.AbsClick     == null) ovr.AbsClick     = Clone(Settings.AbsClick);       break;
                case SectionKind.Audio:    if (ovr.AudioCapture == null) ovr.AudioCapture = CloneOrNull(Settings.AudioCapture); break;
                default: return;  // Master / Ducking aren't per-car
            }
            PersistActiveCarOverride();
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
                case SectionKind.Abs:      if (ovr.AbsClick     != null) { Settings.AbsClick     = Clone(ovr.AbsClick);       ovr.AbsClick     = null; } break;
                case SectionKind.Audio:    if (ovr.AudioCapture != null) { Settings.AudioCapture = CloneOrNull(ovr.AudioCapture); ovr.AudioCapture = null; } break;
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

        // ---------- per-section dirty check (vs active preset) ----------

        /// <summary>True iff the current values for this section differ from
        /// the active preset's snapshot. False when there's no active preset
        /// (no anchor). Used by the UI to show/hide per-section "Save" /
        /// "Revert" buttons based on actual drift, not on a sticky flag —
        /// so changing a value and changing it back clears the dirty state.</summary>
        public bool IsSectionDirty(SectionKind kind)
        {
            if (Settings == null || string.IsNullOrEmpty(_activePresetName)) return false;
            if (Settings.Presets == null || !Settings.Presets.TryGetValue(_activePresetName, out var snap) || snap == null) return false;

            switch (kind)
            {
                case SectionKind.Master:   return !MasterEquals(snap);
                case SectionKind.Ducking:  return !DuckingEquals(snap);
                case SectionKind.Audio:    return !EffectEquals(snap, EffectField.Audio);
                case SectionKind.Engine:   return !EffectEquals(snap, EffectField.Engine);
                case SectionKind.Bumps:    return !EffectEquals(snap, EffectField.Bumps);
                case SectionKind.Traction: return !EffectEquals(snap, EffectField.Traction);
                case SectionKind.Shift:    return !EffectEquals(snap, EffectField.Shift);
                case SectionKind.Abs:      return !EffectEquals(snap, EffectField.Abs);
            }
            return false;
        }

        private bool MasterEquals(GameSettingsSnapshot snap)
        {
            return EqF2(Settings.MasterGain,              snap.MasterGain)
                && EqF2(Settings.FfbScale,                snap.FfbScale)
                &&     Settings.FfbInvertSign          == snap.FfbInvertSign
                && EqF1(Settings.FfbSmoothTimeConstantMs, snap.FfbSmoothTimeConstantMs)
                && EqI (Settings.FfbSpikeMaxLsbPerMs,     snap.FfbSpikeMaxLsbPerMs)
                && EqI (Settings.FfbPeakSoftLimitLsb,     snap.FfbPeakSoftLimitLsb)
                &&     Settings.SkipFfbPassthrough     == snap.SkipFfbPassthrough;
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

        private enum EffectField { Audio, Engine, Bumps, Traction, Shift, Abs }

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
                && a.Waveform == b.Waveform;
        }
        private static bool Eq(RoadBumpsSettings a, RoadBumpsSettings b)
        {
            if (a == null || b == null) return a == b;
            return a.Enabled == b.Enabled
                && EqF2(a.Gain, b.Gain)
                && EqI (a.Freq, b.Freq)
                && a.Waveform == b.Waveform;
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
        public enum SectionKind { Master, Ducking, Audio, Engine, Bumps, Traction, Shift, Abs }

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
                    Settings.FfbSpikeMaxLsbPerMs     = snap.FfbSpikeMaxLsbPerMs;
                    Settings.FfbPeakSoftLimitLsb     = snap.FfbPeakSoftLimitLsb;
                    Settings.SkipFfbPassthrough      = snap.SkipFfbPassthrough;
                    _mixer.MasterGain = Settings.MasterGain;
                    if (_device != null)
                    {
                        _device.FfbScale                = Settings.FfbScale;
                        _device.FfbInvertSign           = Settings.FfbInvertSign;
                        _device.FfbSmoothTimeConstantMs = Settings.FfbSmoothTimeConstantMs;
                        _device.FfbSpikeMaxLsbPerMs     = Settings.FfbSpikeMaxLsbPerMs;
                        _device.FfbPeakSoftLimitLsb     = Settings.FfbPeakSoftLimitLsb;
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
            var file = new CarPresetFile { GameName = _activeGame, CarId = _activeCarId, Override = ovr };
            System.IO.File.WriteAllText(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            SimHub.Logging.Current.Info($"[Trueforce] Exported car preset for '{_activeCarId}' to {path}.");
        }

        /// <summary>Read a car-preset file and store under its CarId in
        /// CarOverrides. If the imported CarId matches the active car, the
        /// override is applied immediately.</summary>
        /// <returns>The car id from the imported file (for UI feedback).</returns>
        public string ImportCarPreset(string path)
        {
            if (Settings == null) return null;
            string json = System.IO.File.ReadAllText(path);
            var file = Newtonsoft.Json.JsonConvert.DeserializeObject<CarPresetFile>(json);
            if (file == null || file.Override == null || string.IsNullOrEmpty(file.CarId))
                throw new System.IO.InvalidDataException("Not a valid Trueforce car-preset file.");
            if (file.Type != CarPresetFile.FileType)
                throw new System.IO.InvalidDataException($"Wrong file type '{file.Type}'. Expected '{CarPresetFile.FileType}'.");

            Settings.CarOverrides[file.CarId] = file.Override;
            this.SaveCommonSettings("GeneralSettings", Settings);

            if (file.CarId == _activeCarId) ApplyActiveCarOverride();
            SimHub.Logging.Current.Info($"[Trueforce] Imported car preset for '{file.CarId}' from {path}.");
            return file.CarId;
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

        // Sidechain ducking state. Two buses, both driven by max-activity of
        // the relevant transients with the same depth/attack/release params:
        //
        //   _duckSmoothed         — driven by ALL transients (incl. TractionLoss);
        //                           applied to EnginePulse + audio capture.
        //   _duckSmoothedMomentary — driven by only the truly-momentary transients
        //                           (RoadBumps, GearShift, AbsClick); applied to
        //                           TractionLoss so a sustained slide doesn't
        //                           drown out an ABS pump or curb hit happening
        //                           on top of it. TractionLoss is excluded from
        //                           its own bus (effects don't duck themselves).
        private float _duckSmoothed          = 1.0f;
        private float _duckSmoothedMomentary = 1.0f;

        private void UpdateDucking()
        {
            // Bus 1 (existing): all transients, ducks engine + audio.
            double maxAll = 0;
            if (RoadBumps    != null) maxAll = Math.Max(maxAll, RoadBumps.ActivityLevel);
            if (TractionLoss != null) maxAll = Math.Max(maxAll, TractionLoss.ActivityLevel);
            if (GearShift    != null) maxAll = Math.Max(maxAll, GearShift.ActivityLevel);
            if (AbsClick     != null) maxAll = Math.Max(maxAll, AbsClick.ActivityLevel);

            // Bus 2 (new): truly-momentary transients only — excludes
            // TractionLoss because a sustained slide is itself a "constant
            // effect" relative to the impulse-shaped events below.
            double maxMomentary = 0;
            if (RoadBumps != null) maxMomentary = Math.Max(maxMomentary, RoadBumps.ActivityLevel);
            if (GearShift != null) maxMomentary = Math.Max(maxMomentary, GearShift.ActivityLevel);
            if (AbsClick  != null) maxMomentary = Math.Max(maxMomentary, AbsClick.ActivityLevel);

            float depth     = Settings?.DuckDepth     ?? 0.5f;
            float attackMs  = Settings?.DuckAttackMs  ?? 5.0f;
            float releaseMs = Settings?.DuckReleaseMs ?? 80.0f;

            // IIR with attack-or-release time constant (dt ≈ 1 ms — producer
            // pushes ~1 batch per ms). alpha = 1 - exp(-dt/tau).
            float targetAll       = (float)Math.Max(0.0, 1.0 - depth * maxAll);
            float targetMomentary = (float)Math.Max(0.0, 1.0 - depth * maxMomentary);

            float tauAllMs       = (targetAll       < _duckSmoothed)          ? attackMs : releaseMs;
            float tauMomentaryMs = (targetMomentary < _duckSmoothedMomentary) ? attackMs : releaseMs;
            float alphaAll       = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauAllMs)));
            float alphaMomentary = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tauMomentaryMs)));

            _duckSmoothed          = _duckSmoothed          * (1f - alphaAll)       + targetAll       * alphaAll;
            _duckSmoothedMomentary = _duckSmoothedMomentary * (1f - alphaMomentary) + targetMomentary * alphaMomentary;

            if (EnginePulse  != null) EnginePulse.DuckMultiplier  = _duckSmoothed;
            if (_audio       != null) _audio.DuckMultiplier       = _duckSmoothed;
            if (TractionLoss != null) TractionLoss.DuckMultiplier = _duckSmoothedMomentary;
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

                UpdateDucking();
                _mixer.Render(buf, BatchSamples);
                for (int i = 0; i < BatchSamples; i++)
                {
                    float v = buf[i];
                    if (v < SilenceFloor && v > -SilenceFloor) buf[i] = 0f;
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

        private void CleanupDevice()
        {
            try { _ffbTap?.Dispose(); } catch { }
            _ffbTap = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}
