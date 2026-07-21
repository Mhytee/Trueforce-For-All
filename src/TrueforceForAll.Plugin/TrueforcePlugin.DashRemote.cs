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
using System.Linq;
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

        // Keypad / overlay state for the dash's engine-layout picker and the
        // shared numeric keypad. Written on SimHub's action-trigger thread,
        // read on the property-poll thread; strings are reference-atomic so
        // torn reads are impossible and eventual consistency is fine here.
        private volatile string _dashOverlay = "";        // "" | "layout" | "keypad" | "presets"
        private volatile string _dashKeypadEntry = "";    // digits being typed
        // What the keypad edits when SET is pressed: "master" | "audio" |
        // "redline" | "fx:<Key>" (a _dashFx table key). The title doubles as
        // the validation-feedback line: SET with a bad value swaps it for a
        // specific error (with the field's range), and the next keypress
        // restores the base title.
        private volatile string _dashKeypadTarget = "";
        private volatile string _dashKeypadTitle = "";
        private volatile string _dashKeypadBaseTitle = "";
        private float _dashKeypadMin, _dashKeypadMax;   // valid range for the open session

        // Transient feedback line ("toast") for actions that cannot run right
        // now (no game / no car / desktop edit open). The dash shows a bar on
        // every screen while Dash.Toast is non-empty; expiry is served by the
        // getter so no timer is needed.
        private volatile string _dashToast = "";
        private int _dashToastAtTick;
        private const int DashToastMs = 3500;

        private void DashToast(string message)
        {
            _dashToastAtTick = Environment.TickCount;
            _dashToast = message;
        }

        // Gate for the per-car surfaces (car facts, car presets). Explains
        // WHY the tap did nothing instead of silently no-opping.
        private bool DashRequireCar()
        {
            if (string.IsNullOrEmpty(_activeGame))
            {
                DashToast("NO GAME RUNNING - START DRIVING FIRST");
                return false;
            }
            if (string.IsNullOrEmpty(_activeCarId))
            {
                DashToast("NO CAR DETECTED - GET IN A CAR FIRST");
                return false;
            }
            return true;
        }

        // Preset picker state. The name list is built ONCE at open (car
        // presets come off disk via GetCarPresets; slot getters must never
        // touch the store per poll) and paged 8 rows at a time.
        private volatile string _dashPresetScope = "";              // "game" | "car"
        private volatile string _dashPresetTitle = "";
        private volatile string _dashPresetCurrent = "";            // highlight row
        private volatile string[] _dashPresetList = new string[0];
        private int _dashPresetPage;
        private const int DashPresetRows = 8;

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
            public string Game = "", CarName = "", PresetName = "", CarPresetName = "";
            public string EngineLayout = "", EngineSource = "", EnginePin = "Auto";
            public int Redline, MaxRpm;
            public string RedlineSource = "";
        }
        private DashSnapshot _dashSnap = new DashSnapshot();
        // Freshness is an explicit flag + tick pair, NOT an int.MinValue
        // sentinel tick: TickCount - int.MinValue wraps negative, which reads
        // as "fresh" and permanently serves the empty initial snapshot (the
        // v2 on-wheel bug: dash showed "No game" with a car loaded).
        private int _dashSnapTick;
        private volatile bool _dashSnapValid;

        private DashSnapshot DashSnap()
        {
            int now = Environment.TickCount;
            if (_dashSnapValid && unchecked(now - _dashSnapTick) < 500) return _dashSnap;
            // set first so a throwing rebuild doesn't re-run per poll
            _dashSnapTick = now;
            _dashSnapValid = true;
            try
            {
                var s = new DashSnapshot
                {
                    Game       = _activeGame ?? "",
                    PresetName = _activePresetName ?? "",
                };
                if (!string.IsNullOrEmpty(_activeCarId))
                    s.CarPresetName = GetActiveCarPresetName(_activeCarId) ?? "";
                var sum = GetActiveCarFactsSummary();
                s.CarName       = sum.CarName ?? "";
                s.EngineLayout  = sum.EngineTypeDisplay ?? "Auto";
                s.EngineSource  = sum.EngineTypeProvenance ?? "";
                // Prefer the user's pin over the resolved value: resolution
                // only re-runs on telemetry frames, so right after a keypad
                // set (or while paused) the resolved number can lag and make
                // a successful save look ignored. Same choice the desktop
                // makes (CarFactsSummary.UserRedline doc).
                s.Redline       = sum.UserRedline ?? sum.EffectiveRedline ?? 0;
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

        // Remote taps are user activity (owner decision 2026-07-20): they
        // prove a human is at this rig, so they feed the same activity
        // signal the desktop panel stamps, which gates the cloud auto-pull.
        // Stamped in the shared action sinks rather than per registration;
        // every dash flow passes through one of them.
        private void DashNoteActivity()
        {
            try { NoteUserActivity(); } catch { }
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
            this.AttachDelegate("Dash.KeypadEntry",        () => _dashKeypadEntry);
            this.AttachDelegate("Dash.KeypadTitle",        () => _dashKeypadTitle);
            this.AttachDelegate("Dash.Toast", () =>
            {
                if (_dashToast.Length == 0) return "";
                int age = unchecked(Environment.TickCount - _dashToastAtTick);
                return age < 0 || age > DashToastMs ? "" : _dashToast;
            });

            // ---------- properties: preset picker ----------
            this.AttachDelegate("Dash.CarPresetName",    () => DashSnap().CarPresetName);
            this.AttachDelegate("Dash.Preset.Title",     () => _dashPresetTitle);
            this.AttachDelegate("Dash.Preset.Current",   () => _dashPresetCurrent);
            this.AttachDelegate("Dash.Preset.PageLabel", () =>
            {
                var list = _dashPresetList;
                int pages = Math.Max(1, (list.Length + DashPresetRows - 1) / DashPresetRows);
                return (Math.Min(_dashPresetPage, pages - 1) + 1) + "/" + pages;
            });
            for (int slot = 1; slot <= DashPresetRows; slot++)
            {
                int idx = slot - 1;
                this.AttachDelegate("Dash.Preset.Slot" + slot, () =>
                {
                    var list = _dashPresetList;
                    int i = _dashPresetPage * DashPresetRows + idx;
                    return i >= 0 && i < list.Length ? list[i] : "";
                });
            }

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
                this.AddAction("DashFx" + f.Key + "GainOpen", (a, b) =>
                    DashOpenKeypad("fx:" + f.Key, f.Key.ToUpperInvariant() + " GAIN (now "
                        + (Settings == null ? 0f : f.GetGain()).ToString("0.###") + ", max 10)", 0f, 10f));
            }

            // Audio capture is a peer voice, not a TelemetryEffect: it goes
            // through the live setters (same path as the home Feedback tile)
            // and ApplyAudioCaptureSettings, not ApplyActiveCarOverride.
            this.AttachDelegate("Dash.Fx.Audio.On",   () => ActiveAudioEnabled);
            this.AttachDelegate("Dash.Fx.Audio.Gain", () => ActiveAudioGain);
            this.AddAction("DashFxAudioToggle", (a, b) =>
            {
                if (Settings == null) return;
                DashNoteActivity();
                SetActiveAudioEnabledLive(!ActiveAudioEnabled);
                PersistSettings();
                RaiseDashRemoteChanged();
            });
            this.AddAction("DashAudioGainUp",   (a, b) => DashNudgeAudioGain(+DashAudioGainStep));
            this.AddAction("DashAudioGainDown", (a, b) => DashNudgeAudioGain(-DashAudioGainStep));

            // ---------- actions: global ----------
            // Master gain reuses NudgeMasterGain (applies + persists + raises
            // MasterGainChangedExternally) with the user's configured step.
            this.AddAction("DashMasterGainUp",   (a, b) => { DashNoteActivity(); NudgeMasterGain(+MasterGainStep); });
            this.AddAction("DashMasterGainDown", (a, b) => { DashNoteActivity(); NudgeMasterGain(-MasterGainStep); });
            this.AddAction("DashPluginToggle",   (a, b) =>
            {
                DashNoteActivity();
                SetPluginEnabled(!PluginEnabled);
                RaiseDashRemoteChanged();
            });

            // ---------- actions: engine layout picker ----------
            this.AddAction("DashEngineLayoutOpen",  (a, b) =>
            {
                if (!DashRequireCar()) return;
                _dashOverlay = "layout";
            });
            this.AddAction("DashEngineLayoutClose", (a, b) => { _dashOverlay = ""; });
            foreach (Effects.EngineLayout layout in Enum.GetValues(typeof(Effects.EngineLayout)))
            {
                // Custom needs a pattern picked from the library, a desktop
                // flow; the dash still DISPLAYS Custom when a variant uses it.
                if (layout == Effects.EngineLayout.Custom) continue;
                var l = layout;
                this.AddAction("DashEngineLayoutSet_" + l, (a, b) => DashSetEngineLayout(l));
            }

            // ---------- actions: redline steppers ----------
            this.AddAction("DashRedlineUp",   (a, b) => DashNudgeRedline(+50));
            this.AddAction("DashRedlineDown", (a, b) => DashNudgeRedline(-50));

            // ---------- actions: shared numeric keypad ----------
            // One keypad serves every tap-to-type value; the open action
            // stamps the target + a title that shows the current value.
            this.AddAction("DashRedlineOpen", (a, b) =>
            {
                if (!DashRequireCar()) return;
                int cur = GetActiveVariantUserRedline() ?? (RevLimiter?.EffectiveRedlineRpm ?? 0);
                DashOpenKeypad("redline",
                    "REDLINE RPM (" + (cur >= 500 ? "now " + cur + ", " : "") + "500-25000)",
                    500f, 25000f);
            });
            this.AddAction("DashMasterGainOpen", (a, b) =>
                DashOpenKeypad("master", "MASTER GAIN (now " + MasterGain.ToString("0.00") + ", max 2)", 0f, 2f));
            this.AddAction("DashAudioGainOpen", (a, b) =>
                DashOpenKeypad("audio", "AUDIO GAIN (now " + ActiveAudioGain.ToString("0.00") + ", max 3)", 0f, DashAudioGainMax));
            this.AddAction("DashKeypadCancel", (a, b) => { _dashOverlay = ""; _dashKeypadEntry = ""; _dashKeypadTarget = ""; });
            this.AddAction("DashKeypadBack", (a, b) =>
            {
                _dashKeypadTitle = _dashKeypadBaseTitle;   // typing clears any error
                var e = _dashKeypadEntry;
                if (e.Length > 0) _dashKeypadEntry = e.Substring(0, e.Length - 1);
            });
            this.AddAction("DashKeypadDot", (a, b) =>
            {
                _dashKeypadTitle = _dashKeypadBaseTitle;
                // Redline is integer-only; gains take one decimal point.
                if (_dashKeypadTarget == "redline") return;
                var e = _dashKeypadEntry;
                if (e.Length < 6 && !e.Contains(".")) _dashKeypadEntry = (e.Length == 0 ? "0." : e + ".");
            });
            for (int d = 0; d <= 9; d++)
            {
                var digit = d;
                this.AddAction("DashKeypadDigit" + digit, (a, b) =>
                {
                    _dashKeypadTitle = _dashKeypadBaseTitle;
                    var e = _dashKeypadEntry;
                    if (e.Length < 6) _dashKeypadEntry = e + digit;
                });
            }
            this.AddAction("DashKeypadSet", (a, b) => DashCommitKeypadEntry());

            // ---------- actions: preset picker ----------
            this.AddAction("DashPresetOpenGame", (a, b) => DashOpenPresetPicker("game"));
            this.AddAction("DashPresetOpenCar",  (a, b) => DashOpenPresetPicker("car"));
            this.AddAction("DashPresetClose",    (a, b) =>
            {
                _dashOverlay = "";
                _dashPresetScope = "";
            });
            this.AddAction("DashPresetPrev", (a, b) => DashPresetTurnPage(-1));
            this.AddAction("DashPresetNext", (a, b) => DashPresetTurnPage(+1));
            for (int slot = 1; slot <= DashPresetRows; slot++)
            {
                int idx = slot - 1;
                this.AddAction("DashPresetSelect" + slot, (a, b) => DashPresetSelect(idx));
            }

            SimHub.Logging.Current.Info("[TF4ALL] Dash remote bridge registered (properties + actions for the TF4ALL Remote dashboard).");
        }

        // Shared mutate path for table effects: draft, mutate, push live,
        // persist, notify. Mirrors the settings panel's handler shape
        // (EnsureSectionDraft + ActiveXxx write + Apply) plus the immediate
        // persist that NudgeMasterGain established for headless surfaces.
        private void DashMutateFx(DashFx f, Action mutate)
        {
            if (Settings == null) return;
            DashNoteActivity();
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
            DashNoteActivity();
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
            DashNoteActivity();
            if (!DashRequireCar()) return;
            // Auto clears the pin (SaveActiveVariantUserEngine treats Auto as
            // null); returns false when no variant signature exists yet, i.e.
            // no telemetry has been observed for this car.
            if (!SaveActiveVariantUserEngine(layout, null))
            {
                DashToast("NOT SAVED - DRIVE THE CAR A MOMENT FIRST");
                return;
            }
            _dashSnapValid = false;   // show the new pin immediately
            DashTrySilentEngineSubmit(layout);
            RaiseDashRemoteChanged();
        }

        private void DashNudgeRedline(int delta)
        {
            if (Settings == null) return;
            DashNoteActivity();
            if (!DashRequireCar()) return;
            // Step from the user's pin when present, else from the resolved
            // effective value; with nothing resolved yet there is no base to
            // step from - point at the keypad instead.
            int baseRpm = GetActiveVariantUserRedline() ?? (RevLimiter?.EffectiveRedlineRpm ?? 0);
            if (baseRpm < 500)
            {
                DashToast("NO REDLINE KNOWN YET - TAP THE VALUE TO TYPE ONE");
                return;
            }
            int next = baseRpm + delta;
            if (next < 500) next = 500;
            if (next > 25000) next = 25000;
            if (SaveActiveVariantUserRedline(next).HasValue)
            {
                _dashSnapValid = false;
                DashTrySilentRedlineSubmit(next);
                RaiseDashRemoteChanged();
            }
        }

        private void DashOpenKeypad(string target, string title, float min, float max)
        {
            DashNoteActivity();
            _dashKeypadTarget    = target;
            _dashKeypadTitle     = title;
            _dashKeypadBaseTitle = title;
            _dashKeypadMin       = min;
            _dashKeypadMax       = max;
            _dashKeypadEntry     = "";
            _dashOverlay         = "keypad";
        }

        // Validation feedback: swap the title for the error; the digits stay
        // so the user can see and fix what they typed. Any keypress restores
        // the base title.
        private void DashKeypadError(string message)
        {
            _dashKeypadTitle = message;
        }

        // SET on the shared keypad. Invalid / out-of-range entries leave the
        // keypad open with the digits kept and put a specific error (with the
        // field's range) in the title so the user sees WHY it did not take.
        private void DashCommitKeypadEntry()
        {
            if (Settings == null) return;
            DashNoteActivity();
            string target = _dashKeypadTarget;
            string entry  = _dashKeypadEntry;

            if (entry.Length == 0)
            {
                DashKeypadError("TYPE A VALUE, THEN SET");
                return;
            }

            if (target == "redline")
            {
                if (!int.TryParse(entry, out int rpm))
                {
                    DashKeypadError("NOT A WHOLE NUMBER");
                    return;
                }
                if (rpm < 500)   { DashKeypadError("TOO LOW, MINIMUM 500");    return; }
                if (rpm > 25000) { DashKeypadError("TOO HIGH, MAXIMUM 25000"); return; }
                if (!SaveActiveVariantUserRedline(rpm).HasValue)
                {
                    // No variant signature yet (no telemetry observed for
                    // this car) or no active car. Surface it in the keypad
                    // instead of silently keeping the digits.
                    DashKeypadError("NOT SAVED (drive the car a moment first)");
                    SimHub.Logging.Current.Info(
                        $"[TF4ALL] Dash redline set {rpm} rejected: no live variant signature (game='{_activeGame}', car='{_activeCarId}').");
                    return;
                }
                SimHub.Logging.Current.Info($"[TF4ALL] Dash redline set {rpm} saved for '{_activeCarId}'.");
                DashCloseKeypad();
                _dashSnapValid = false;
                DashTrySilentRedlineSubmit(rpm);
                RaiseDashRemoteChanged();
                return;
            }

            if (!float.TryParse(entry, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                DashKeypadError("NOT A NUMBER");
                return;
            }
            if (v < _dashKeypadMin)
            {
                DashKeypadError("TOO LOW, MINIMUM " + _dashKeypadMin.ToString("0.##"));
                return;
            }
            if (v > _dashKeypadMax)
            {
                DashKeypadError("TOO HIGH, MAXIMUM " + _dashKeypadMax.ToString("0.##"));
                return;
            }

            if (target == "master")
            {
                MasterGain = v;
                PersistSettings();
                try { MasterGainChangedExternally?.Invoke(); } catch { }
                DashCloseKeypad();
                RaiseDashRemoteChanged();
                return;
            }
            if (target == "audio")
            {
                SetActiveAudioGainLive(v);
                PersistSettings();
                DashCloseKeypad();
                RaiseDashRemoteChanged();
                return;
            }
            if (target.StartsWith("fx:", StringComparison.Ordinal))
            {
                string key = target.Substring(3);
                foreach (var f in _dashFx)
                {
                    if (f.Key != key || f.SetGain == null) continue;
                    DashMutateFx(f, () => f.SetGain(v));
                    DashCloseKeypad();
                    return;
                }
            }
        }

        private void DashCloseKeypad()
        {
            _dashOverlay = "";
            _dashKeypadEntry = "";
            _dashKeypadTarget = "";
        }

        // Build the picker list ONCE at open. Ordering mirrors the desktop
        // pickers: built-ins first, then alphabetical. Car presets come off
        // disk here (GetCarPresets -> store LoadAll), which is fine once per
        // open but must never happen in a slot property getter.
        private void DashOpenPresetPicker(string scope)
        {
            if (Settings == null) return;
            DashNoteActivity();
            try
            {
                string[] list;
                string current;
                string title;
                if (scope == "car")
                {
                    // Toast instead of an empty picker: matches the other
                    // per-car surfaces (car facts).
                    if (!DashRequireCar()) return;
                    string carId = _activeCarId;
                    var entries = GetCarPresets(carId);
                    list = entries
                        .OrderBy(kv => kv.Value != null && kv.Value.IsBuiltin ? 0 : 1)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => kv.Key)
                        .ToArray();
                    current = GetActiveCarPresetName(carId) ?? "";
                    title = list.Length > 0 ? "CAR PRESETS" : "CAR PRESETS  (none for this car)";
                }
                else
                {
                    scope = "game";
                    list = PresetNames
                        .OrderBy(n => IsBuiltinPreset(n) ? 0 : 1)
                        .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    current = _activePresetName ?? "";
                    title = list.Length > 0 ? "GAME PRESETS" : "GAME PRESETS  (library empty)";
                }
                _dashPresetList = list;
                _dashPresetCurrent = current;
                _dashPresetTitle = title;
                // open on the page containing the current selection
                int at = string.IsNullOrEmpty(current) ? -1 : Array.IndexOf(list, current);
                _dashPresetPage = at >= 0 ? at / DashPresetRows : 0;
                _dashPresetScope = scope;
                _dashOverlay = "presets";
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[TF4ALL] Dash preset picker open failed ({scope}): {ex.Message}");
            }
        }

        private void DashPresetTurnPage(int delta)
        {
            var list = _dashPresetList;
            int pages = Math.Max(1, (list.Length + DashPresetRows - 1) / DashPresetRows);
            int next = _dashPresetPage + delta;
            if (next < 0) next = 0;
            if (next > pages - 1) next = pages - 1;
            _dashPresetPage = next;
        }

        private void DashPresetSelect(int rowIdx)
        {
            if (Settings == null) return;
            DashNoteActivity();
            var list = _dashPresetList;
            int i = _dashPresetPage * DashPresetRows + rowIdx;
            if (i < 0 || i >= list.Length) return;
            string name = list[i];
            // The offline-edit banner means unsaved authoring is in flight;
            // the auto-apply path suppresses itself for the same reason, and
            // the plugin apply methods do NOT guard this themselves.
            if (IsOfflineEditing || IsOfflineEditingCar)
            {
                DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                SimHub.Logging.Current.Info("[TF4ALL] Dash preset apply skipped: an offline preset edit is in progress.");
                return;
            }
            try
            {
                if (_dashPresetScope == "car")
                {
                    string carId = _activeCarId;
                    if (string.IsNullOrEmpty(carId)) return;
                    // The UI only ever runs this on the WPF thread; from the
                    // dash trigger thread the CarOverrides reload could race
                    // the data thread's car-change draft handling, so take the
                    // same lock that path uses (reentrant, so the PersistCore
                    // inside is fine).
                    lock (_carFactsLock)
                    {
                        if (!SelectCarForEditing(carId, name)) return;
                    }
                }
                else
                {
                    if (!ApplyPreset(name)) return;
                }
                _dashPresetCurrent = name;
                _dashOverlay = "";
                _dashPresetScope = "";
                _dashSnapValid = false;
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[TF4ALL] Dash preset apply failed ({name}): {ex.Message}");
            }
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
