// Dash remote bridge: exposes plugin state as SimHub properties and
// registers dash-triggerable actions so a DashStudio dashboard (served to
// a phone/tablet by SimHub's web dash server) can control TF4ALL while
// driving. Scope (owner-decided, 2026-07-20): master gain, audio capture
// gain, per-effect enable + gain, and the two feel-relevant car facts
// (engine layout, redline). Presets and car names stay desktop-only.
//
// Mechanics:
//  - Properties register via AttachDelegate and surface as
//    "TrueforcePlugin.Dash.*" (PluginManager prepends the class name).
//    Getters are polled on demand by SimHub; anything that walks the
//    car-facts store is served from a 500 ms snapshot cache instead of
//    resolving per poll.
//  - Actions register via AddAction and fire from dash ButtonItems'
//    TriggerAction ("TrueforcePlugin.DashFxEngineToggle"). AddAction
//    entries are deliberately NOT hardware-bindable (see the
//    AddInputMapping comment in Init); the two existing MasterGainUp/Down
//    input mappings stay for controller binding.
//  - Mutations follow the settings panel's write path: EnsureSectionDraft
//    (car-scoped edit lands in the car override, not the global),
//    mutate the ActiveXxx POCO, ApplyActiveCarOverride() to push live,
//    PersistSettings() (same immediate-persist choice as
//    NudgeMasterGain). Enabled/Gain are primitives read lock-free by the
//    render thread, so cross-thread writes are safe (TelemetryEffect
//    thread model).
//  - Car-fact edits reuse the headless store commits
//    (SaveActiveVariantUserEngine / SaveActiveVariantUserRedline) and
//    submit to the community ONLY on the silent fast path
//    (AutoSubmitCarFacts + CommunityEnabled). Any case the desktop would
//    resolve with a dialog (consent ask, limiter-suspect redline) saves
//    locally and skips the submit; the desktop flows remain the place
//    where dialogs happen.

using System;
using System.Collections.Generic;
using SimHub.Plugins;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    public sealed partial class TrueforcePlugin
    {
        /// <summary>Raised after any dash-remote mutation so an open
        /// SettingsControl can re-pull values (its sliders otherwise show
        /// stale state until the next reload). The home Feedback tile needs
        /// nothing: it polls on a 1 s timer.</summary>
        public event Action DashRemoteChanged;

        // Keypad / overlay state for the dash's engine-layout picker and
        // redline entry. Written on SimHub's action-trigger thread, read on
        // the property-poll thread; strings are reference-atomic so torn
        // reads are impossible and eventual consistency is fine here.
        private volatile string _dashOverlay = "";        // "" | "layout" | "redline"
        private volatile string _dashRedlineEntry = "";   // digits being typed

        // Per-session dedupe for silent car-fact submissions, mirroring the
        // desktop prompts' _enginePromptedThisSession semantics (same value
        // re-saved = no re-submit; a different value re-engages).
        private readonly HashSet<string> _dashFactSubmitted = new HashSet<string>();
        private readonly object _dashFactSubmittedLock = new object();

        // Audio gain mirrors the home Feedback tile: 0.05 steps, 0..3 range
        // (FeedbackBoxInjector.AudioMax).
        private const float DashAudioGainStep = 0.05f;
        private const float DashAudioGainMax  = 3.0f;

        // ------------------------------------------------------------------
        // Snapshot cache for the poll-heavy readouts. GetActiveCarFactsSummary
        // walks the variant store under _carFactsLock; at SimHub's property
        // poll rate that would be wasteful, so rebuild at most every 500 ms.
        // Rebuild races are harmless (idempotent, last write wins).
        // ------------------------------------------------------------------
        private sealed class DashSnapshot
        {
            public string Game = "", CarName = "", PresetName = "";
            public string EngineLayout = "", EngineSource = "", EnginePin = "Auto";
            public int Redline, MaxRpm;
            public string RedlineSource = "";
        }
        private DashSnapshot _dashSnap = new DashSnapshot();
        private int _dashSnapTick = int.MinValue;

        private DashSnapshot DashSnap()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _dashSnapTick) < 500) return _dashSnap;
            _dashSnapTick = now;   // set first so a throwing rebuild doesn't re-run per poll
            try
            {
                var s = new DashSnapshot
                {
                    Game       = _activeGame ?? "",
                    PresetName = _activePresetName ?? "",
                };
                var sum = GetActiveCarFactsSummary();
                s.CarName       = sum.CarName ?? "";
                s.EngineLayout  = sum.EngineTypeDisplay ?? "Auto";
                s.EngineSource  = sum.EngineTypeProvenance ?? "";
                s.Redline       = sum.EffectiveRedline ?? 0;
                s.MaxRpm        = sum.MaxRpm ?? 0;
                s.RedlineSource = sum.RedlineSource ?? "";
                var (pin, _) = GetActiveVariantUserEngine();
                s.EnginePin = (pin ?? Effects.EngineLayout.Auto).ToString();
                _dashSnap = s;
            }
            catch { /* keep serving the previous snapshot */ }
            return _dashSnap;
        }

        // ------------------------------------------------------------------
        // Per-effect dispatch table. All settings POCOs expose Enabled/Gain
        // with identical shapes but share no interface, so each row captures
        // its own accessors. SetGain == null means toggle-only on the dash
        // (Airborne: its strength is Reduction, a set-and-forget ducking
        // depth, not a live-tweak gain).
        // ------------------------------------------------------------------
        private sealed class DashFx
        {
            public string Key;
            public SectionKind Kind;
            public Func<bool> GetOn;
            public Action<bool> SetOn;
            public Func<float> GetGain;
            public Action<float> SetGain;
        }
        private DashFx[] _dashFx;

        private DashFx[] BuildDashFxTable() => new[]
        {
            new DashFx { Key = "Engine",     Kind = SectionKind.Engine,       GetOn = () => ActiveEngine.Enabled,       SetOn = v => ActiveEngine.Enabled = v,       GetGain = () => ActiveEngine.Gain,       SetGain = v => ActiveEngine.Gain = v },
            new DashFx { Key = "Bumps",      Kind = SectionKind.Bumps,        GetOn = () => ActiveBumps.Enabled,        SetOn = v => ActiveBumps.Enabled = v,        GetGain = () => ActiveBumps.Gain,        SetGain = v => ActiveBumps.Gain = v },
            new DashFx { Key = "Traction",   Kind = SectionKind.Traction,     GetOn = () => ActiveTraction.Enabled,     SetOn = v => ActiveTraction.Enabled = v,     GetGain = () => ActiveTraction.Gain,     SetGain = v => ActiveTraction.Gain = v },
            new DashFx { Key = "AxleSlip",   Kind = SectionKind.AxleSlip,     GetOn = () => ActiveAxleSlip.Enabled,     SetOn = v => ActiveAxleSlip.Enabled = v,     GetGain = () => ActiveAxleSlip.Gain,     SetGain = v => ActiveAxleSlip.Gain = v },
            new DashFx { Key = "Kerb",       Kind = SectionKind.KerbThump,    GetOn = () => ActiveKerbThump.Enabled,    SetOn = v => ActiveKerbThump.Enabled = v,    GetGain = () => ActiveKerbThump.Gain,    SetGain = v => ActiveKerbThump.Gain = v },
            new DashFx { Key = "Lockup",     Kind = SectionKind.LockupJudder, GetOn = () => ActiveLockupJudder.Enabled, SetOn = v => ActiveLockupJudder.Enabled = v, GetGain = () => ActiveLockupJudder.Gain, SetGain = v => ActiveLockupJudder.Gain = v },
            new DashFx { Key = "Shift",      Kind = SectionKind.Shift,        GetOn = () => ActiveShift.Enabled,        SetOn = v => ActiveShift.Enabled = v,        GetGain = () => ActiveShift.Gain,        SetGain = v => ActiveShift.Gain = v },
            new DashFx { Key = "Abs",        Kind = SectionKind.Abs,          GetOn = () => ActiveAbs.Enabled,          SetOn = v => ActiveAbs.Enabled = v,          GetGain = () => ActiveAbs.Gain,          SetGain = v => ActiveAbs.Gain = v },
            new DashFx { Key = "Pit",        Kind = SectionKind.PitLimiter,   GetOn = () => ActivePitLimiter.Enabled,   SetOn = v => ActivePitLimiter.Enabled = v,   GetGain = () => ActivePitLimiter.Gain,   SetGain = v => ActivePitLimiter.Gain = v },
            new DashFx { Key = "Drs",        Kind = SectionKind.Drs,          GetOn = () => ActiveDrs.Enabled,          SetOn = v => ActiveDrs.Enabled = v,          GetGain = () => ActiveDrs.Gain,          SetGain = v => ActiveDrs.Gain = v },
            new DashFx { Key = "Collision",  Kind = SectionKind.Collision,    GetOn = () => ActiveCollision.Enabled,    SetOn = v => ActiveCollision.Enabled = v,    GetGain = () => ActiveCollision.Gain,    SetGain = v => ActiveCollision.Gain = v },
            new DashFx { Key = "RevLimiter", Kind = SectionKind.RevLimiter,   GetOn = () => ActiveRevLimiter.Enabled,   SetOn = v => ActiveRevLimiter.Enabled = v,   GetGain = () => ActiveRevLimiter.Gain,   SetGain = v => ActiveRevLimiter.Gain = v },
            new DashFx { Key = "Airborne",   Kind = SectionKind.Airborne,     GetOn = () => ActiveAirborne.Enabled,     SetOn = v => ActiveAirborne.Enabled = v,     GetGain = null,                          SetGain = null },
        };

        // Multiplicative gain step so one press moves small gains (0.07) and
        // large gains (1.5) by a comparable feel amount. Floor + zero rules:
        // stepping down below 0.005 lands on exactly 0 (silence), stepping up
        // from 0 restarts at 0.01.
        private static float DashStepGain(float g, bool up)
        {
            if (up)
            {
                if (g < 0.005f) return 0.01f;
                float n = g * 1.12f;
                return n > 10f ? 10f : n;
            }
            float d = g / 1.12f;
            return d < 0.005f ? 0f : d;
        }

        private void RaiseDashRemoteChanged()
        {
            try { DashRemoteChanged?.Invoke(); } catch { }
        }

        // ==================================================================
        // Registration. Called once from Init; wrapped there so a SimHub API
        // hiccup can't abort plugin startup.
        // ==================================================================
        private void InitDashRemote(PluginManager pluginManager)
        {
            _dashFx = BuildDashFxTable();

            // ---------- properties: status ----------
            this.AttachDelegate("Dash.WheelOk", () =>
                _device != null && !_device.StreamFaulted
                && System.Threading.Volatile.Read(ref _recoveryInProgress) == 0);
            this.AttachDelegate("Dash.WheelStatus",  () => StreamStatus);
            this.AttachDelegate("Dash.Game",         () => DashSnap().Game);
            this.AttachDelegate("Dash.CarName",      () => DashSnap().CarName);
            this.AttachDelegate("Dash.PresetName",   () => DashSnap().PresetName);
            this.AttachDelegate("Dash.PluginOn",     () => PluginEnabled);
            this.AttachDelegate("Dash.MasterGain",   () => MasterGain);
            this.AttachDelegate("Dash.AudioGain",    () => ActiveAudioGain);
            this.AttachDelegate("Dash.AudioOn",      () => ActiveAudioEnabled);

            // ---------- properties: car facts ----------
            this.AttachDelegate("Dash.EngineLayout",       () => DashSnap().EngineLayout);
            this.AttachDelegate("Dash.EngineLayoutSource", () => DashSnap().EngineSource);
            this.AttachDelegate("Dash.EnginePin",          () => DashSnap().EnginePin);
            this.AttachDelegate("Dash.Redline",            () => DashSnap().Redline);
            this.AttachDelegate("Dash.RedlineSource",      () => DashSnap().RedlineSource);
            this.AttachDelegate("Dash.MaxRpm",             () => DashSnap().MaxRpm);
            this.AttachDelegate("Dash.Overlay",            () => _dashOverlay);
            this.AttachDelegate("Dash.RedlineEntry",       () => _dashRedlineEntry);

            // ---------- properties + actions: per effect ----------
            foreach (var fx in _dashFx)
            {
                var f = fx;   // capture per iteration
                this.AttachDelegate("Dash.Fx." + f.Key + ".On", () =>
                    Settings != null && f.GetOn());
                this.AddAction("DashFx" + f.Key + "Toggle", (a, b) =>
                    DashMutateFx(f, () => f.SetOn(!f.GetOn())));
                if (f.GetGain == null) continue;
                this.AttachDelegate("Dash.Fx." + f.Key + ".Gain", () =>
                    Settings == null ? 0f : f.GetGain());
                this.AddAction("DashFx" + f.Key + "GainUp", (a, b) =>
                    DashMutateFx(f, () => f.SetGain(DashStepGain(f.GetGain(), up: true))));
                this.AddAction("DashFx" + f.Key + "GainDown", (a, b) =>
                    DashMutateFx(f, () => f.SetGain(DashStepGain(f.GetGain(), up: false))));
            }

            // Audio capture is a peer voice, not a TelemetryEffect: it goes
            // through the live setters (same path as the home Feedback tile)
            // and ApplyAudioCaptureSettings, not ApplyActiveCarOverride.
            this.AttachDelegate("Dash.Fx.Audio.On",   () => ActiveAudioEnabled);
            this.AttachDelegate("Dash.Fx.Audio.Gain", () => ActiveAudioGain);
            this.AddAction("DashFxAudioToggle", (a, b) =>
            {
                if (Settings == null) return;
                SetActiveAudioEnabledLive(!ActiveAudioEnabled);
                PersistSettings();
                RaiseDashRemoteChanged();
            });
            this.AddAction("DashAudioGainUp",   (a, b) => DashNudgeAudioGain(+DashAudioGainStep));
            this.AddAction("DashAudioGainDown", (a, b) => DashNudgeAudioGain(-DashAudioGainStep));

            // ---------- actions: global ----------
            // Master gain reuses NudgeMasterGain (applies + persists + raises
            // MasterGainChangedExternally) with the user's configured step.
            this.AddAction("DashMasterGainUp",   (a, b) => NudgeMasterGain(+MasterGainStep));
            this.AddAction("DashMasterGainDown", (a, b) => NudgeMasterGain(-MasterGainStep));
            this.AddAction("DashPluginToggle",   (a, b) =>
            {
                SetPluginEnabled(!PluginEnabled);
                RaiseDashRemoteChanged();
            });

            // ---------- actions: engine layout picker ----------
            this.AddAction("DashEngineLayoutOpen",  (a, b) => { _dashOverlay = "layout"; });
            this.AddAction("DashEngineLayoutClose", (a, b) => { _dashOverlay = ""; });
            foreach (Effects.EngineLayout layout in Enum.GetValues(typeof(Effects.EngineLayout)))
            {
                // Custom needs a pattern picked from the library, a desktop
                // flow; the dash still DISPLAYS Custom when a variant uses it.
                if (layout == Effects.EngineLayout.Custom) continue;
                var l = layout;
                this.AddAction("DashEngineLayoutSet_" + l, (a, b) => DashSetEngineLayout(l));
            }

            // ---------- actions: redline steppers + keypad ----------
            this.AddAction("DashRedlineUp",     (a, b) => DashNudgeRedline(+50));
            this.AddAction("DashRedlineDown",   (a, b) => DashNudgeRedline(-50));
            this.AddAction("DashRedlineOpen",   (a, b) => { _dashRedlineEntry = ""; _dashOverlay = "redline"; });
            this.AddAction("DashRedlineCancel", (a, b) => { _dashOverlay = ""; _dashRedlineEntry = ""; });
            this.AddAction("DashRedlineBack",   (a, b) =>
            {
                var e = _dashRedlineEntry;
                if (e.Length > 0) _dashRedlineEntry = e.Substring(0, e.Length - 1);
            });
            for (int d = 0; d <= 9; d++)
            {
                var digit = d;
                this.AddAction("DashRedlineDigit" + digit, (a, b) =>
                {
                    var e = _dashRedlineEntry;
                    if (e.Length < 5) _dashRedlineEntry = e + digit;
                });
            }
            this.AddAction("DashRedlineSet", (a, b) => DashCommitRedlineEntry());

            SimHub.Logging.Current.Info("[TF4ALL] Dash remote bridge registered (properties + actions for the TF4ALL Remote dashboard).");
        }

        // Shared mutate path for table effects: draft, mutate, push live,
        // persist, notify. Mirrors the settings panel's handler shape
        // (EnsureSectionDraft + ActiveXxx write + Apply) plus the immediate
        // persist that NudgeMasterGain established for headless surfaces.
        private void DashMutateFx(DashFx f, Action mutate)
        {
            if (Settings == null) return;
            try
            {
                EnsureSectionDraft(f.Kind);
                mutate();
                ApplyActiveCarOverride();
                PersistSettings();
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[TF4ALL] Dash effect action failed ({f.Key}): {ex.Message}");
            }
        }

        private void DashNudgeAudioGain(float delta)
        {
            if (Settings == null) return;
            float next = ActiveAudioGain + delta;
            if (next < 0f) next = 0f;
            if (next > DashAudioGainMax) next = DashAudioGainMax;
            SetActiveAudioGainLive(next);
            PersistSettings();   // SetActiveAudioGainLive leaves persisting to the caller
            RaiseDashRemoteChanged();
        }

        private void DashSetEngineLayout(Effects.EngineLayout layout)
        {
            _dashOverlay = "";
            if (Settings == null) return;
            // Auto clears the pin (SaveActiveVariantUserEngine treats Auto as
            // null); returns false when no variant signature exists yet, i.e.
            // no telemetry has been observed for this car.
            if (!SaveActiveVariantUserEngine(layout, null)) return;
            _dashSnapTick = int.MinValue;   // show the new pin immediately
            DashTrySilentEngineSubmit(layout);
            RaiseDashRemoteChanged();
        }

        private void DashNudgeRedline(int delta)
        {
            if (Settings == null) return;
            // Step from the user's pin when present, else from the resolved
            // effective value; with nothing resolved yet there is no sane
            // base, so do nothing (the keypad still allows an absolute entry).
            int baseRpm = GetActiveVariantUserRedline() ?? (RevLimiter?.EffectiveRedlineRpm ?? 0);
            if (baseRpm < 500) return;
            int next = baseRpm + delta;
            if (next < 500) next = 500;
            if (next > 25000) next = 25000;
            if (SaveActiveVariantUserRedline(next).HasValue)
            {
                _dashSnapTick = int.MinValue;
                DashTrySilentRedlineSubmit(next);
                RaiseDashRemoteChanged();
            }
        }

        private void DashCommitRedlineEntry()
        {
            if (Settings == null) return;
            if (!int.TryParse(_dashRedlineEntry, out int rpm)) return;
            // Out-of-range entries stay in the keypad (overlay open, digits
            // kept) so the user sees the value did not take and can fix it.
            if (rpm < 500 || rpm > 25000) return;
            if (!SaveActiveVariantUserRedline(rpm).HasValue) return;
            _dashOverlay = "";
            _dashRedlineEntry = "";
            _dashSnapTick = int.MinValue;
            DashTrySilentRedlineSubmit(rpm);
            RaiseDashRemoteChanged();
        }

        // Silent community submit for a dash engine-layout pin. Desktop
        // parity (MaybePromptToSubmitEngineData) minus everything that needs
        // a dialog: consent must already be settled on the silent fast path,
        // and Auto / Electric / Custom never submit (Auto is "don't know",
        // Electric is a different feature, Custom has its own desktop path).
        private void DashTrySilentEngineSubmit(Effects.EngineLayout layout)
        {
            var s = Settings;
            if (s == null || !s.AutoSubmitCarFacts || !s.CommunityEnabled) return;
            if (layout == Effects.EngineLayout.Auto
                || layout == Effects.EngineLayout.Electric
                || layout == Effects.EngineLayout.Custom) return;
            string game = _activeGame, carId = _activeCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            // Desktop skips the share when the pick just agrees with
            // auto-detect: nothing to correct.
            var auto = EnginePulse?.AutoLayout;
            if (auto.HasValue && auto.Value == layout) return;
            lock (_dashFactSubmittedLock)
            {
                if (!_dashFactSubmitted.Add(carId + "|engine|" + layout)) return;
            }
            SubmitEngineLayoutToCommunity(game, carId, layout);
        }

        // Silent community submit for a dash redline pin. Desktop parity
        // (MaybePromptToSubmitRedlineData) with the two dialog cases turned
        // into skips: an unsettled consent never submits, and a
        // limiter-suspect value (within 2% of the observed rev ceiling) is
        // saved locally but not shared, because the desktop would have asked
        // "are you sure this isn't the limiter?" first.
        private void DashTrySilentRedlineSubmit(int claimed)
        {
            var s = Settings;
            if (s == null || !s.AutoSubmitCarFacts || !s.CommunityEnabled) return;
            string game = _activeGame, carId = _activeCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            var ep = EnginePulse;
            if (ep == null) return;
            // Games that report their own redline need no community value.
            if (ep.ObservedRedlineRpm >= 500) return;
            if (claimed < 500 || claimed > 25000) return;
            // Same 50 RPM banding as the desktop path so the two surfaces
            // feed one consensus row instead of fragmenting it.
            int implied = (int)Math.Round(claimed / 50.0) * 50;
            double maxRpm = ep.ObservedMaxRpm;
            if (maxRpm >= 500 && implied >= maxRpm * 0.98) return;   // limiter-suspect
            int? confirmed = GetActiveVariantConfirmedCommunityRedline();
            if (confirmed.HasValue && Math.Abs(confirmed.Value - implied) <= 100) return;
            lock (_dashFactSubmittedLock)
            {
                if (!_dashFactSubmitted.Add(carId + "|redline|" + implied)) return;
            }
            // Same payload shape as the desktop path: per-gear pins ride
            // along, snapped to the same 50 RPM bands.
            var perGear = new List<GearRedline>();
            var src = GetActiveVariantPerGearRedlines();
            if (src != null)
                foreach (var g in src)
                    if (g != null && g.Gear >= 1 && g.Gear <= 16 && g.Rpm >= 500 && g.Rpm <= 25000)
                        perGear.Add(new GearRedline { Gear = g.Gear, Rpm = (int)Math.Round(g.Rpm / 50.0) * 50 });
            SubmitRedlineToCommunity(game, carId, implied, perGear);
        }
    }
}
