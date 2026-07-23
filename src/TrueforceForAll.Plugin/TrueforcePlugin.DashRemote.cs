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
//    where dialogs happen. Redline shares ride a quiet-window debounce
//    (DashScheduleRedlineShare) so a burst of stepper taps submits only
//    the value the user settled on.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Active tab for the dash's tab-bar navigation (0=Home, 1=Car
        // facts, 2=Effects, 3=Presets, 4=Visualizer). Every dash screen gates
        // its ScreenEnabledExpression on Dash.Tab, so the plugin owns
        // which screen shows and the tab bar is the only navigation.
        // Replaces screen swiping: the web viewer fires ButtonItems on
        // touch-down and swallows the gesture, so on a button-dense
        // screen starting a swipe kept triggering whatever the finger
        // landed on without ever changing screen. With exactly one
        // screen enabled, SimHub's swipe/NextScreen is inert.
        // Seeded at Init from Settings (DashLastTab or DashDefaultTab per
        // the remember-last-tab pref); tab taps write back DashLastTab.
        private const int DashTabCount = 5;
        private volatile int _dashTab;

        // Live RPM for the rev strip, stashed per frame in DispatchFrame.
        // The strip's percent uses OUR effective redline (user pin >
        // community > telemetry > estimate), which is the whole point: on
        // Forza a generic SimHub rev bar keys off the limiter, not the
        // redline start.
        private volatile float _dashLiveRpm;
        // Redline hysteresis latch for Dash.RevFlash, mirroring the wheel
        // LED latch's 1% dead band. Only touched by the property getter.
        private bool _dashRevFlashLatch;

        // Transient feedback line ("toast") for actions that cannot run right
        // now (no game / no car / desktop edit open). The dash shows a bar on
        // every screen while Dash.Toast is non-empty; expiry is served by the
        // getter so no timer is needed.
        private volatile string _dashToast = "";
        private int _dashToastAtTick;
        private const int DashToastMs = 2000;

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
        // Sentinel row at index 0 of the CAR picker list: clears the car's
        // preset (desktop "None" row parity). Selection is intercepted BY
        // INDEX (car scope, i == 0), never by comparing against this string
        // (a user preset could share the name; worst case there is a
        // cosmetic double-highlight, never a wrong action). Must be
        // non-empty (empty slots hide their row + tap zone in the djson)
        // and must not end in " (default)" (ToDisplayName rewrites that).
        private const string DashCarPresetNoneRow = "NONE  (USE GAME PRESET)";

        // Per-session dedupe for silent car-fact submissions, mirroring the
        // desktop prompts' _enginePromptedThisSession semantics (same value
        // re-saved = no re-submit; a different value re-engages).
        private readonly HashSet<string> _dashFactSubmitted = new HashSet<string>();
        private readonly object _dashFactSubmittedLock = new object();

        // Debounce for the silent redline share. Every 50 RPM stepper tap
        // saves locally, but sharing each intermediate step would spray the
        // community with values the user was only passing through. Each
        // save (re)arms a short countdown and only the value still on the
        // table when it runs out is submitted: a quick burst of taps shares
        // once, taps spaced past the window still share individually (the
        // per-value dedupe above applies as ever). Game/car are captured at
        // arm time so a car swap mid-countdown drops the pending share
        // instead of attributing it to the new car.
        private const int DashRedlineShareQuietMs = 3000;
        private System.Threading.Timer _dashRedlineShareTimer;
        private readonly object _dashRedlineShareLock = new object();
        private bool _dashRedlineSharePending;
        private int _dashRedlineShareValue;
        private string _dashRedlineShareGame, _dashRedlineShareCar;

        // Audio gain mirrors the home Feedback tile: 0.05 steps, 0..3 range
        // (FeedbackBoxInjector.AudioMax).
        private const float DashAudioGainStep = 0.05f;
        private const float DashAudioGainMax  = 3.0f;

        // ------------------------------------------------------------------
        // Unsaved-tuning tracking for the dash's Save/Revert bar. Effect and
        // audio edits are DRAFTS under the desktop's draft model: a car
        // change discards them and a restart re-resolves the car's preset
        // from disk, so without an explicit save a dash tune silently
        // evaporates. The dash tracks the sections IT edited since the last
        // save/revert; the bar shows while the set is non-empty AND the
        // recorded car still matches (a car swap already discarded the
        // draft, so stale dirtiness must not outlive it).
        // ------------------------------------------------------------------
        private readonly HashSet<SectionKind> _dashDirty = new HashSet<SectionKind>();
        private readonly object _dashDirtyLock = new object();
        private string _dashDirtyCarId = "";

        private void DashRecordDirty(SectionKind kind)
        {
            // Anchored sections are governed ENTIRELY by the truthful
            // IsSectionDirty compare: an edit-then-undo (toggle off, toggle
            // back on) must read clean again, so nothing sticky may linger
            // for them. The local sticky set exists only for anchor-less
            // sections (no active preset = nothing to compare against),
            // mirroring the desktop's sticky bit.
            bool anchorless = false;
            try { anchorless = !SectionHasAnchor(kind); } catch { }
            if (anchorless)
            {
                lock (_dashDirtyLock)
                {
                    string car = _activeCarId ?? "";
                    if (_dashDirtyCarId != car) { _dashDirty.Clear(); _dashDirtyCarId = car; }
                    _dashDirty.Add(kind);
                }
            }
            _dashSnapValid = false;   // bar reflects the edit on the next poll
        }

        private void DashClearDirty()
        {
            lock (_dashDirtyLock) { _dashDirty.Clear(); }
            _dashSnapValid = false;
        }

        // Truthful cross-surface dirtiness (owner decision 2026-07-21: dash
        // and desktop indicators cross-track). A section counts as dirty
        // when live state differs from its saved anchor, via the SAME
        // IsSectionDirty the desktop's Save buttons use, so a desktop
        // slider edit lights the dash bar and vice versa (the desktop side
        // already recomputes from IsSectionDirty on DashRemoteChanged ->
        // RefreshFromPlugin). The dash-local set supplements the
        // anchor-less case (no active preset = nothing to compare
        // against), where the desktop keeps a sticky bit for the same
        // reason.
        private SectionKind[] DashDirtySections()
        {
            var list = new List<SectionKind>();
            try
            {
                foreach (SectionKind k in Enum.GetValues(typeof(SectionKind)))
                    if (SectionHasAnchor(k) && IsSectionDirty(k)) list.Add(k);
            }
            catch { /* comparison trouble reads as clean; the local set below still contributes */ }
            SectionKind[] sticky;
            lock (_dashDirtyLock)
            {
                if (_dashDirtyCarId == (_activeCarId ?? ""))
                {
                    sticky = new SectionKind[_dashDirty.Count];
                    _dashDirty.CopyTo(sticky);
                }
                else
                {
                    _dashDirty.Clear();   // car changed; that draft is gone
                    sticky = new SectionKind[0];
                }
            }
            foreach (var k in sticky)
            {
                // A section that has GAINED an anchor since it was recorded
                // (a preset was applied) is governed by the compare now;
                // its sticky entry must not keep the bar lit.
                bool anchorless = false;
                try { anchorless = !SectionHasAnchor(k); } catch { }
                if (anchorless && !list.Contains(k)) list.Add(k);
            }
            return list.ToArray();
        }

        // Car-level drift the per-section compare can miss (car-preset-only
        // edits anchor to the car file, not the game preset).
        private bool DashCarDrift()
        {
            try
            {
                return !string.IsNullOrEmpty(_activeCarId) && IsActiveCarPresetDirty();
            }
            catch { return false; }
        }

        private bool DashHasDirty() => DashDirtySections().Length > 0 || DashCarDrift();

        // Is there anything the REVERT button could actually undo? Revert
        // needs a saved baseline to restore. Anchored dirty sections and
        // car-preset drift have one; an anchor-less edit (no active preset,
        // so nothing to compare against) does not - the bar still lights so
        // SAVE can capture it into a new preset, but there is nothing to
        // revert TO. The dash gates the revert button on this so the user
        // never sees a revert affordance that can't do anything.
        private bool DashCanRevert()
        {
            try
            {
                foreach (SectionKind k in Enum.GetValues(typeof(SectionKind)))
                    if (SectionHasAnchor(k) && IsSectionDirty(k)) return true;
            }
            catch { /* comparison trouble reads as not-revertable */ }
            return DashCarDrift();
        }

        // All car-scoped sections, used when only car-level drift is
        // detected: patching every car-scope section from live IS a
        // whole-override save/revert, and it reuses the per-section paths
        // (including the built-in fork fallback).
        private SectionKind[] DashAllCarScopeSections()
        {
            var list = new List<SectionKind>();
            foreach (SectionKind k in Enum.GetValues(typeof(SectionKind)))
                if (SectionHasCarScope(k)) list.Add(k);
            return list.ToArray();
        }

        // ------------------------------------------------------------------
        // Signal scope (the dash's Visualizer screen): two stacked scrolling
        // traces sampled on the producer thread. Texture = peak abs of each
        // rendered 1 kHz batch (the actual ep3 haptic stream, 0..1); FFB =
        // the signed force the device actually wrote to ep3 cur (post
        // smoothing/scale/spike taming, re-oriented to game direction,
        // -1..1). One column advances every ScopeColMs,
        // so ScopeCols columns = ~2.5 s of scrolling history. Rings are
        // written by the producer thread and read by the property-poll
        // thread; float element reads are atomic, a torn column is one
        // frame of cosmetic noise at worst.
        // ------------------------------------------------------------------
        private const int ScopeCols  = 78;
        private const int ScopeColMs = 32;
        private readonly float[] _scopeTex = new float[ScopeCols];
        private readonly float[] _scopeFfb = new float[ScopeCols];
        private volatile int _scopeHead;
        private float _scopeAccum;      // producer-thread only
        private long _scopeNextColTicks;

        /// <summary>Called once per producer tick with the batch RunOneTick
        /// just rendered/pushed. Allocation-free; the hot-path cost is four
        /// abs/max ops and a Stopwatch read.</summary>
        internal void DashScopeTick(float[] buf, int count)
        {
            float peak = _scopeAccum;
            for (int i = 0; i < count; i++)
            {
                float a = buf[i];
                if (a < 0f) a = -a;
                if (a > peak) peak = a;
            }
            _scopeAccum = peak;

            long now = Stopwatch.GetTimestamp();
            if (_scopeNextColTicks == 0)
                _scopeNextColTicks = now + Stopwatch.Frequency * ScopeColMs / 1000;
            if (now < _scopeNextColTicks) return;
            _scopeNextColTicks = now + Stopwatch.Frequency * ScopeColMs / 1000;

            int h = _scopeHead;
            // Envelope decay blend: a column never drops below 55% of its
            // predecessor, so single-slice transients read as a flowing
            // waveform instead of isolated flicker (display only; the raw
            // peak still tops the column when it is the larger value).
            float tex = peak > 1f ? 1f : peak;
            float prevTex = _scopeTex[(h + ScopeCols - 1) % ScopeCols] * 0.55f;
            _scopeTex[h] = tex > prevTex ? tex : prevTex;
            // Post-processing output: the force actually written to ep3 cur
            // (smoothing, scale and spike taming applied), re-oriented to the
            // game's force direction so the trace reads like the game's FFB
            // and clip marks land on the rail the line is pinned to.
            var dev = _device;
            float ffb = (dev?.LastFfbOutput ?? (short)0) / 32768f;
            if (dev != null)
            {
                if (dev.FfbInvertSign) ffb = -ffb;
                // Undo display attenuation: with FfbScale < 1 the output tops
                // out at the scale value, which parked the clip point mid-lane
                // on the dash. Normalizing puts the rail (= clipping) at the
                // lane edge; scale >= 1 already rails at 1.0 via the clamp.
                float sc = dev.FfbScale;
                if (sc > 0.05f && sc < 1f) ffb /= sc;
            }
            if (ffb > 1f) ffb = 1f; else if (ffb < -1f) ffb = -1f;
            // Light one-pole smoothing so the line trace bends instead of
            // stepping between 32 ms samples.
            _scopeFfbSmooth += (ffb - _scopeFfbSmooth) * 0.5f;
            _scopeFfb[h] = _scopeFfbSmooth;
            // Clip detection on the SMOOTHED value, i.e. exactly what the
            // dash line draws (the chart samples Ffb77 = this), so a clip
            // can only register when the drawn line actually reaches the
            // rail. 0.98 of full scale is within 2 px of the rail on the
            // 800x480 dash.
            if (_scopeFfbSmooth >= 0.98f || _scopeFfbSmooth <= -0.98f)
            {
                _dashClipSign = _scopeFfbSmooth > 0f ? 1 : -1;
                // 150 ms hold: keeps the strips' display-rate sampling from
                // missing a clip AND bridges micro-dips below the threshold
                // so a hovering force reads as one continuous clip. The glow
                // (badge + line red) stays SOLID until this expires, then
                // crossfades out over 1.5 s.
                System.Threading.Interlocked.Exchange(ref _dashClipUntilTicks,
                    now + Stopwatch.Frequency * 150 / 1000);
            }
            _scopeHead = (h + 1) % ScopeCols;
            _scopeAccum = 0f;
        }
        private float _scopeFfbSmooth;   // producer-thread only
        private volatile int _dashClipSign;
        private long _dashClipUntilTicks; // Interlocked; clip hold; glow decays from its expiry

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
            public bool TuningDirty;
            public bool CanRevert;
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
                // Dirty compare (18 IsSectionDirty calls) rides the 500 ms
                // snapshot cadence rather than the per-frame property poll.
                s.TuningDirty = DashHasDirty();
                s.CanRevert   = DashCanRevert();
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

            // Opening tab: last used when the user opted into remembering it
            // (default), else their chosen fixed default. Clamped so a
            // settings file written by a future dash with more tabs can't
            // strand this one on a screen that doesn't exist.
            int startTab = Settings?.DashRememberLastTab != false
                ? (Settings?.DashLastTab ?? 0)
                : (Settings?.DashDefaultTab ?? 0);
            _dashTab = Math.Max(0, Math.Min(DashTabCount - 1, startTab));

            // ---------- properties: status ----------
            this.AttachDelegate("Dash.WheelOk", () =>
            {
                // Snapshot _device: the recovery worker nulls it between a
                // poll's null check and the dereference otherwise (teardown
                // window NRE thrown into SimHub's property engine).
                var d = _device;
                return d != null && !d.StreamFaulted
                    && System.Threading.Volatile.Read(ref _recoveryInProgress) == 0;
            });
            this.AttachDelegate("Dash.WheelStatus",  () => StreamStatus);
            this.AttachDelegate("Dash.Game",         () => DashSnap().Game);
            this.AttachDelegate("Dash.CarName",      () => DashSnap().CarName);
            // Built-ins are stored " (default)" but display " (built-in)"
            // everywhere (desktop dropdowns do the same relabel). Applied at
            // the display delegates only; _dashPresetList and the snapshot
            // stay raw because select/apply needs the stored names.
            this.AttachDelegate("Dash.PresetName",   () => BuiltinPresets.ToDisplayName(DashSnap().PresetName));
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
            this.AttachDelegate("Dash.Tab",                () => _dashTab);
            this.AttachDelegate("Dash.KeypadEntry",        () => _dashKeypadEntry);
            this.AttachDelegate("Dash.KeypadTitle",        () => _dashKeypadTitle);
            // ---------- properties: tuning save / revert ----------
            this.AttachDelegate("Dash.TuningDirty", () => DashSnap().TuningDirty);
            this.AttachDelegate("Dash.CanRevert",   () => DashSnap().CanRevert);
            this.AttachDelegate("Dash.SaveContext", () =>
            {
                var s = DashSnap();
                string c = s.CarName != "" ? s.CarName : "(no car)";
                string p = s.PresetName != "" ? s.PresetName : "(manual tune)";
                return "CAR  " + c + "      GAME PRESET  " + p;
            });

            // ---------- properties: signal scope (polled at display rate) ----------
            // Index 0 = oldest column (left edge), ScopeCols-1 = newest.
            for (int c = 0; c < ScopeCols; c++)
            {
                int idx = c;
                this.AttachDelegate("Dash.Scope.Tex" + idx, () => _scopeTex[(_scopeHead + idx) % ScopeCols]);
                this.AttachDelegate("Dash.Scope.Ffb" + idx, () => _scopeFfb[(_scopeHead + idx) % ScopeCols]);
            }
            // Live clip state (+1/-1/0, 150 ms hold): drives the rail
            // marker strips on the visualizer.
            this.AttachDelegate("Dash.Scope.FfbClip", () =>
                Stopwatch.GetTimestamp() < System.Threading.Interlocked.Read(ref _dashClipUntilTicks)
                    ? _dashClipSign : 0);
            // Badge + whole-line glow: SOLID 1 while the clip hold is
            // active (still clipping), then decaying linearly to 0 over
            // 1.5 s from the moment clipping stops. The dash binds it to
            // the red badge layer's Opacity and lerps the line color
            // amber -> red by it (formulas have no clock, so the fade is
            // computed here).
            this.AttachDelegate("Dash.Scope.FfbClipGlow", () =>
            {
                long until = System.Threading.Interlocked.Read(ref _dashClipUntilTicks);
                if (until == 0) return 0f;
                long nowT = Stopwatch.GetTimestamp();
                if (nowT < until) return 1f;
                float sec = (float)(nowT - until) / Stopwatch.Frequency;
                float g = 1f - sec / 1.5f;
                return g > 0f ? g : 0f;
            });

            // ---------- properties: rev strip (polled at display rate) ----------
            this.AttachDelegate("Dash.RevOutsideIn", () => Settings?.DashRevStripOutsideIn == true);
            this.AttachDelegate("Dash.Rpm", () => _telemetryStalled ? 0 : (int)_dashLiveRpm);
            this.AttachDelegate("Dash.RpmPct", () =>
            {
                if (_telemetryStalled) return 0f;
                float rpm = _dashLiveRpm;
                int redline = RevLimiter?.EffectiveRedlineRpm ?? 0;
                if (redline < 500 || rpm <= 0f) return 0f;
                float pct = rpm / redline * 100f;
                return pct > 120f ? 120f : pct;
            });
            // Flash gate for the rev strip: steady true below the redline,
            // blinking at/above it. Cadence AND phase deliberately match the
            // wheel's own rev lights (RpmLedController.OnFrame): the same
            // UTC-ms clock and the same 185 ms half-period (~2.7 Hz, the
            // iRacing-style shift blink), so a wheel-mounted remote flashes
            // in step with the rim LEDs. The on-condition mirrors the
            // wheel's redline latch too (on AT the line, released below 99%
            // of it) so both start and stop flashing at the same moments.
            this.AttachDelegate("Dash.RevFlash", () =>
            {
                if (_telemetryStalled) { _dashRevFlashLatch = false; return true; }
                float rpm = _dashLiveRpm;
                int redline = RevLimiter?.EffectiveRedlineRpm ?? 0;
                if (redline < 500) { _dashRevFlashLatch = false; return true; }
                if (_dashRevFlashLatch) { if (rpm < redline * 0.99f) _dashRevFlashLatch = false; }
                else if (rpm >= redline) _dashRevFlashLatch = true;
                if (!_dashRevFlashLatch) return true;
                long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                return ((nowMs / 185L) & 1L) == 0L;
            });
            this.AttachDelegate("Dash.Toast", () =>
            {
                if (_dashToast.Length == 0) return "";
                int age = unchecked(Environment.TickCount - _dashToastAtTick);
                return age < 0 || age > DashToastMs ? "" : _dashToast;
            });

            // ---------- properties: preset picker ----------
            this.AttachDelegate("Dash.CarPresetName",    () => BuiltinPresets.ToDisplayName(DashSnap().CarPresetName));
            this.AttachDelegate("Dash.Preset.Title",     () => _dashPresetTitle);
            // Current + slots relabel identically, so the djson's
            // slot-equals-current highlight match still holds.
            this.AttachDelegate("Dash.Preset.Current",   () => BuiltinPresets.ToDisplayName(_dashPresetCurrent));
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
                    return i >= 0 && i < list.Length ? BuiltinPresets.ToDisplayName(list[i]) : "";
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
                DashRecordDirty(SectionKind.Audio);
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

            // ---------- actions: tab-bar navigation ----------
            for (int t = 0; t < DashTabCount; t++)
            {
                int tab = t;
                this.AddAction("DashTabSelect" + tab, (a, b) =>
                {
                    DashNoteActivity();
                    _dashTab = tab;
                    // The dash hides the bar while an overlay is up, so
                    // overlay state seen here is stale; drop it rather
                    // than strand an open overlay on the new tab.
                    _dashOverlay = "";
                    _dashPresetScope = "";
                    // Record for remember-last-tab. Disk write only while the
                    // pref is on (crash resilience); off, the field still
                    // rides along with the next ordinary settings save.
                    if (Settings != null && Settings.DashLastTab != tab)
                    {
                        Settings.DashLastTab = tab;
                        if (Settings.DashRememberLastTab)
                        {
                            try { PersistSettings(); }
                            catch (Exception ex)
                            {
                                SimHub.Logging.Current.Info(
                                    "[TF4ALL] Persist DashLastTab failed: " + ex.Message);
                            }
                        }
                    }
                });
            }

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

            // ---------- actions: tuning save / revert ----------
            // SAVE opens the scope chooser (a one-overlay miniature of the
            // desktop save popover); with no car active there is nothing
            // car-scoped to choose, so it saves straight to the game preset.
            this.AddAction("DashTuneSaveOpen", (a, b) =>
            {
                DashNoteActivity();
                if (IsOfflineEditing || IsOfflineEditingCar)
                {
                    DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                    return;
                }
                if (!DashHasDirty()) { DashToast("NO UNSAVED TUNING"); return; }
                if (string.IsNullOrEmpty(_activeCarId)) { DashSaveTuningToGame(); return; }
                _dashOverlay = "savescope";
            });
            this.AddAction("DashTuneSaveCancel", (a, b) =>
            {
                if (_dashOverlay == "savescope") _dashOverlay = "";
            });
            this.AddAction("DashTuneSaveCar",  (a, b) => { _dashOverlay = ""; DashSaveTuningToCar(); });
            this.AddAction("DashTuneSaveGame", (a, b) => { _dashOverlay = ""; DashSaveTuningToGame(); });
            this.AddAction("DashTuneSaveBoth", (a, b) => { _dashOverlay = ""; DashSaveTuningToBoth(); });
            this.AddAction("DashTuneRevert", (a, b) =>
            {
                DashNoteActivity();
                if (IsOfflineEditing || IsOfflineEditingCar)
                {
                    DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                    return;
                }
                var dirty = DashDirtySections();
                if (dirty.Length == 0 && DashCarDrift()) dirty = DashAllCarScopeSections();
                if (dirty.Length == 0) { DashToast("NO UNSAVED TUNING"); return; }
                bool anyReverted = false;
                foreach (var k in dirty)
                {
                    // Car-scoped sections revert their draft (falls back to
                    // the preset with no car). Global-only sections (Master,
                    // Ducking, Spike reduction, Stationary spring) have no
                    // car draft; RevertSectionDraft dead-ended on them with
                    // a car active, leaving the bar lit after a "REVERTED"
                    // toast.
                    try
                    {
                        bool r = SectionHasCarScope(k) ? RevertSectionDraft(k) : RevertSection(k);
                        anyReverted |= r;
                    }
                    catch { }
                }
                if (!anyReverted)
                {
                    // Nothing had a saved baseline to revert to (e.g. an
                    // anchor-less edit with no active preset). Don't clear the
                    // bar or persist over an edit we never undid, and don't
                    // claim success.
                    DashToast("NOTHING TO REVERT");
                    return;
                }
                DashClearDirty();
                PersistSettings();
                DashToast("REVERTED TO SAVED");
                RaiseDashRemoteChanged();
            });

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
                DashRecordDirty(f.Kind);
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
            DashRecordDirty(SectionKind.Audio);
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
                DashScheduleRedlineShare(next);
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
                DashScheduleRedlineShare(rpm);
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
                DashRecordDirty(SectionKind.Audio);
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

        // ==================================================================
        // Tuning save implementations. Each mirrors a desktop save-popover
        // scope using the same headless plugin paths; toasts carry the
        // outcome since the dash has no dialogs.
        // ==================================================================

        // THIS CAR ONLY: patch the dirty sections into the car's saved
        // preset file. A built-in car preset (or a car with none) cannot be
        // edited in place; mirror the desktop's silent fork instead:
        // SaveActiveCarPresetAs saves the whole live override as a new user
        // car preset and binds it as this car's default.
        private void DashSaveTuningToCar()
        {
            DashNoteActivity();
            if (IsOfflineEditing || IsOfflineEditingCar)
            {
                DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                return;
            }
            if (string.IsNullOrEmpty(_activeCarId)) { DashToast("NO CAR DETECTED"); return; }
            var dirty = DashDirtySections();
            // Car-level drift with no per-section hit: patch every car-scope
            // section, which is a whole-override save through the same path.
            if (dirty.Length == 0 && DashCarDrift()) dirty = DashAllCarScopeSections();
            // Global-only sections (Master, Ducking, Spike reduction,
            // Stationary spring) cannot live in a car file, and
            // SaveSectionToActiveCarOverride refuses them; unfiltered they
            // tripped the fork fallback into minting a junk "<car> tune"
            // preset on EVERY retry whenever cross-tracked desktop drift
            // included one, silently rebinding the car each time.
            var carScoped = new List<SectionKind>();
            bool globalLeftover = false;
            foreach (var k in dirty)
            {
                if (SectionHasCarScope(k)) carScoped.Add(k);
                else globalLeftover = true;
            }
            if (carScoped.Count == 0)
            {
                DashToast(dirty.Length > 0
                    ? "THOSE CHANGES ARE GAME-WIDE - USE GAME PRESET"
                    : "NO UNSAVED TUNING");
                return;
            }
            string carPresetName = GetActiveCarPresetName(_activeCarId);
            bool carBuiltinLocked = !string.IsNullOrEmpty(carPresetName)
                && IsCarPresetBuiltin(_activeCarId, carPresetName) && !DevMode;
            try
            {
                bool allOk = true;
                foreach (var k in carScoped)
                {
                    if (!SaveSectionToActiveCarOverride(k)) { allOk = false; break; }
                }
                if (!allOk)
                {
                    Settings.CarOverrides.TryGetValue(_activeCarId, out var liveOvr);
                    bool liveEmpty = liveOvr == null || liveOvr.IsEmpty;
                    // Reset-to-default commit: the user cleared every section
                    // (empty live override) on a car bound to a writable user
                    // preset. That's "follow the game default", not a fork -
                    // persist the empty override, which deletes the car file,
                    // exactly as the desktop car Save does. The section-level
                    // save can't express this (it refuses an absent override),
                    // so it misrouted into the fork, which then refused the
                    // empty override and dead-ended with SAVE FAILED.
                    if (liveEmpty && !carBuiltinLocked && !string.IsNullOrEmpty(carPresetName))
                    {
                        bool committed;
                        lock (_carFactsLock) { committed = PersistActiveCarOverride(); }
                        if (!committed) { DashToast("SAVE FAILED (see the SimHub log)"); return; }
                        ApplyActiveCarOverride();
                        DashClearDirty();
                        _dashSnapValid = false;
                        DashToast("SAVED TO THIS CAR");
                        RaiseDashRemoteChanged();
                        return;
                    }
                    string name = DashUniqueCarPresetName();
                    bool forked;
                    // Same lock the preset picker's apply takes: the fork now
                    // reloads CarOverrides via SwitchActiveCarPreset, which
                    // can race the data thread's car-change draft handling
                    // when triggered from the dash action thread.
                    lock (_carFactsLock) { forked = SaveActiveCarPresetAs(name); }
                    if (!forked)
                    {
                        DashToast("SAVE FAILED (see the SimHub log)");
                        return;
                    }
                    DashClearDirty();
                    _dashSnapValid = false;
                    DashToast("SAVED AS NEW CAR PRESET: " + name.ToUpperInvariant());
                    RaiseDashRemoteChanged();
                    return;
                }
                DashClearDirty();
                _dashSnapValid = false;
                DashToast(globalLeftover
                    ? "SAVED TO THIS CAR (GAME-WIDE CHANGES NEED GAME PRESET)"
                    : "SAVED TO THIS CAR");
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Dash car save failed: " + ex.Message);
                DashToast("SAVE FAILED (see the SimHub log)");
            }
        }

        // GAME PRESET: the dirty sections become the game default (promoted
        // to the global sections and written into the active preset); the
        // car follows it. A built-in active preset forks to a user copy
        // automatically (same SavePresetAs flow the desktop's fork uses,
        // owner decision 2026-07-21: no desktop-detour errors), and no
        // active preset at all auto-creates one named after the game.
        private void DashSaveTuningToGame()
        {
            DashNoteActivity();
            if (IsOfflineEditing || IsOfflineEditingCar)
            {
                // During a desktop offline edit _activePresetName is pinned
                // to the preset UNDER EDIT; a dash save here would write the
                // half-finished edit baseline into it behind the desktop
                // session's back.
                DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                return;
            }
            var dirty = DashDirtySections();
            if (dirty.Length == 0) { DashToast("NO UNSAVED TUNING"); return; }
            string preset = _activePresetName;
            bool fork = string.IsNullOrEmpty(preset) || (IsBuiltinPreset(preset) && !DevMode);
            try
            {
                // Two-phase promote, same as the desktop popover: copy the
                // sections' live values up to the globals BEFORE the preset
                // write (non-destructive), and only release the car layer -
                // which patches the saved car file - AFTER the write is
                // confirmed. The old code promoted (and stripped the car
                // file) up front, so a failed SavePresetAs left the car file
                // already rewritten under a SAVE FAILED toast.
                foreach (var k in dirty) CopySectionToGlobals(k);
                if (fork)
                {
                    string newName = DashUniqueGamePresetName(
                        !string.IsNullOrEmpty(preset) ? preset
                        : (string.IsNullOrEmpty(_activeGame) ? "My preset" : _activeGame));
                    bool reused = false;
                    if (!SavePresetAs(newName))
                    {
                        // Duplicate-content refusal (owner rule: a new name
                        // must not duplicate an existing preset's tuning):
                        // the preset the user wants already exists with
                        // exactly these values, so REUSE it instead of
                        // dead-ending. Any other failure stays an error.
                        string dup = LastLocalDuplicateName;
                        if (string.IsNullOrEmpty(dup))
                        {
                            DashToast("SAVE FAILED (see the SimHub log)");
                            return;
                        }
                        newName = dup;
                        // Keep the user's personal FFB: it's excluded from the
                        // identity hash, so the reused preset's stored FFB
                        // must not yank live wheel strength.
                        ApplyPresetKeepingPersonalFfb(dup);
                        reused = true;
                    }
                    foreach (var k in dirty) ReleaseSectionFromCarLayer(k);
                    // Desktop fork parity (ForkAndSaveAsGamePreset): the fork
                    // is what's playing, so it becomes the game default too.
                    // Without this the built-in re-loads next session and the
                    // fork looks lost. NOT during offline edits: there
                    // _activeGame is pinned to the EDITED preset's game, and
                    // binding would rewrite that game's default behind the
                    // desktop edit session's back.
                    if (!string.IsNullOrEmpty(_activeGame)
                        && !IsOfflineEditing && !IsOfflineEditingCar)
                        SetDefaultPresetForGame(_activeGame, newName);
                    ApplyActiveCarOverride();
                    PersistSettings();
                    DashClearDirty();
                    _dashSnapValid = false;
                    DashToast((reused ? "SAME AS EXISTING PRESET: " : "SAVED AS NEW PRESET: ")
                        + newName.ToUpperInvariant());
                    RaiseDashRemoteChanged();
                    return;
                }
                bool allSectionsOk = true;
                foreach (var k in dirty)
                {
                    if (!SaveSectionToActivePreset(k)) allSectionsOk = false;
                }
                // Release only after the preset write succeeded for every
                // section; a partial failure leaves the car layer (and the
                // dirty bar) intact so the user can retry.
                if (allSectionsOk)
                    foreach (var k in dirty) ReleaseSectionFromCarLayer(k);
                ApplyActiveCarOverride();
                PersistSettings();
                DashClearDirty();
                _dashSnapValid = false;
                DashToast(allSectionsOk
                    ? "SAVED TO PRESET: " + preset.ToUpperInvariant()
                    : "PARTLY SAVED TO PRESET (see the SimHub log)");
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Dash game-preset save failed: " + ex.Message);
                DashToast("SAVE FAILED (see the SimHub log)");
            }
        }

        // Fork/create name for game presets: the built-in's name (or the
        // game's), de-duped the same way the desktop fork does.
        private string DashUniqueGamePresetName(string baseName)
        {
            // Built-ins are STORED with a structural " (default)" suffix; a
            // fork named "Forza Horizon (default) (1)" reads like nonsense,
            // so strip it from the base before de-duping.
            const string defaultSuffix = " (default)";
            if (baseName != null && baseName.EndsWith(defaultSuffix, StringComparison.Ordinal))
                baseName = baseName.Substring(0, baseName.Length - defaultSuffix.Length);
            var existing = new HashSet<string>(PresetNames ?? Enumerable.Empty<string>());
            string name = baseName;
            int i = 1;
            while (existing.Contains(name)) name = baseName + " (" + i++ + ")";
            return name;
        }

        // BOTH: game default + this car keeps its own pinned copy. The car
        // half's writability is decided UP FRONT: SaveSectionToBoth returns
        // okDefault OR okCar, so a refused car write (built-in car preset,
        // no bound car preset) still read as success, the fork fallback
        // never fired, and the toast claimed "+ THIS CAR" while the car
        // file stayed factory and the tune reverted on restart. Global-only
        // sections ride the preset half only.
        private void DashSaveTuningToBoth()
        {
            DashNoteActivity();
            if (IsOfflineEditing || IsOfflineEditingCar)
            {
                DashToast("BLOCKED - FINISH THE PRESET EDIT OPEN IN SIMHUB FIRST");
                return;
            }
            var dirty = DashDirtySections();
            if (dirty.Length == 0) { DashToast("NO UNSAVED TUNING"); return; }
            if (string.IsNullOrEmpty(_activeCarId)) { DashSaveTuningToGame(); return; }
            string preset = _activePresetName;
            bool forkPreset = string.IsNullOrEmpty(preset) || (IsBuiltinPreset(preset) && !DevMode);
            string carPresetName = GetActiveCarPresetName(_activeCarId);
            // DEV authoring writes through to a factory car preset (like
            // the THIS-CAR path and desktop do), so a built-in is writable
            // in DevMode; non-dev forks below.
            bool carWritable = !string.IsNullOrEmpty(carPresetName)
                && (!IsCarPresetBuiltin(_activeCarId, carPresetName) || DevMode);
            var carScoped = new List<SectionKind>();
            foreach (var k in dirty)
                if (SectionHasCarScope(k)) carScoped.Add(k);
            try
            {
                // ---- game-preset half (all dirty sections) ----
                string forkName = null;
                bool reusedPreset = false;
                bool presetOk = true;
                if (forkPreset)
                {
                    // Copy (not promote) up front: non-destructive, so a
                    // failed SavePresetAs can't leave the car file stripped.
                    // BOTH keeps the car pinned, so we re-pin the sections
                    // afterward rather than releasing the car layer.
                    foreach (var k in dirty) CopySectionToGlobals(k);
                    forkName = DashUniqueGamePresetName(
                        !string.IsNullOrEmpty(preset) ? preset
                        : (string.IsNullOrEmpty(_activeGame) ? "My preset" : _activeGame));
                    if (!SavePresetAs(forkName))
                    {
                        // Duplicate-content refusal: reuse the identical
                        // existing preset (same rationale as the GAME path),
                        // preserving the user's personal FFB.
                        string dup = LastLocalDuplicateName;
                        if (string.IsNullOrEmpty(dup))
                        {
                            DashToast("SAVE FAILED (see the SimHub log)");
                            return;
                        }
                        forkName = dup;
                        ApplyPresetKeepingPersonalFfb(dup);
                        reusedPreset = true;
                    }
                    // Fork parity with DashSaveTuningToGame: the fork is
                    // what's playing, so it becomes the game default too.
                    if (!string.IsNullOrEmpty(_activeGame))
                        SetDefaultPresetForGame(_activeGame, forkName);
                    // Re-pin the car copy from the globals (which now hold
                    // the saved values) so the car half writes them.
                    foreach (var k in carScoped) SnapshotSectionToCarOverride(k);
                }
                else
                {
                    foreach (var k in dirty)
                    {
                        if (SectionHasCarScope(k))
                        {
                            CopySectionToGlobals(k);
                            SnapshotSectionToCarOverride(k);
                        }
                        if (!SaveSectionToActivePreset(k)) presetOk = false;
                    }
                }

                // ---- car half (car-scoped sections only) ----
                bool carHalfOk = true;
                bool carForked = false;
                string carForkName = null;
                if (carScoped.Count > 0)
                {
                    if (carWritable)
                    {
                        foreach (var k in carScoped)
                        {
                            if (!SaveSectionToActiveCarOverride(k)) { carHalfOk = false; break; }
                        }
                    }
                    else
                    {
                        carHalfOk = false;   // built-in or unbound: fork below
                    }
                    if (!carHalfOk)
                    {
                        carForkName = DashUniqueCarPresetName();
                        // Locked for the same reason as the THIS-CAR fork.
                        lock (_carFactsLock) { carHalfOk = SaveActiveCarPresetAs(carForkName); }
                        carForked = carHalfOk;
                    }
                }

                ApplyActiveCarOverride();
                PersistSettings();
                DashClearDirty();
                _dashSnapValid = false;
                string presetPart = forkName != null
                    ? (reusedPreset ? "SAME AS EXISTING PRESET: " : "SAVED AS NEW PRESET: ") + forkName.ToUpperInvariant()
                    : (presetOk ? "SAVED TO PRESET" : "PARTLY SAVED TO PRESET (see log)");
                string carPart = carScoped.Count == 0
                    ? ""
                    : carForked ? " + NEW CAR PRESET: " + carForkName.ToUpperInvariant()
                    : carHalfOk ? " + THIS CAR"
                    : "; CAR COPY FAILED (see log)";
                DashToast(presetPart + carPart);
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Dash save-to-both failed: " + ex.Message);
                DashToast("SAVE FAILED (see the SimHub log)");
            }
        }

        // Fork name for the silent car-preset fork: the car's display name
        // plus "tune", made unique against the car's existing presets.
        private string DashUniqueCarPresetName()
        {
            string baseName = string.IsNullOrEmpty(_activeCarDisplayName)
                ? "Dash tune" : _activeCarDisplayName + " tune";
            IReadOnlyDictionary<string, CarPresetEntry> existing = null;
            try { existing = GetCarPresets(_activeCarId); } catch { }
            if (existing == null)
            {
                // Store unreadable: NEVER hand back the bare base name.
                // SaveActiveCarPresetAs overwrites by name and earlier forks
                // used exactly this base, so a bare fallback would silently
                // clobber a previous tune. A tick suffix keeps it unique.
                return baseName + " " + (Environment.TickCount & 0xFFFF);
            }
            string name = baseName;
            int n = 2;
            while (existing.ContainsKey(name)) name = baseName + " " + n++;
            return name;
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
                    var names = entries
                        .OrderBy(kv => kv.Value != null && kv.Value.IsBuiltin ? 0 : 1)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => kv.Key)
                        .ToArray();
                    // NONE row first (desktop parity): row 1 always clears
                    // back to the game preset. See DashCarPresetNoneRow.
                    list = new string[names.Length + 1];
                    list[0] = DashCarPresetNoneRow;
                    Array.Copy(names, 0, list, 1, names.Length);
                    current = GetActiveCarPresetName(carId) ?? "";
                    // No active car preset = the NONE row is the current one,
                    // so the overlay highlights it (djson compares strings).
                    if (current.Length == 0) current = DashCarPresetNoneRow;
                    // Key the empty-library title off the REAL entries; the
                    // sentinel row alone doesn't count as having presets.
                    title = names.Length > 0 ? "CAR PRESETS" : "CAR PRESETS  (none for this car)";
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
                    if (i == 0)
                    {
                        // NONE row (index-keyed, see DashCarPresetNoneRow):
                        // clear the car's preset back to the game preset.
                        lock (_carFactsLock)
                        {
                            if (!ClearActiveCarPreset(carId)) return;
                        }
                        name = DashCarPresetNoneRow;
                    }
                    else
                    {
                        lock (_carFactsLock)
                        {
                            if (!SelectCarForEditing(carId, name)) return;
                        }
                    }
                }
                else
                {
                    if (!ApplyPreset(name)) return;
                    // Select-is-default (desktop header parity): picking a
                    // game preset while a game is running binds it as that
                    // game's auto-load default in the same step.
                    if (!string.IsNullOrEmpty(_activeGame))
                        SetDefaultPresetForGame(_activeGame, name);
                }
                _dashPresetCurrent = name;
                // Applying a preset replaces the live tuning wholesale; any
                // dash draft it overwrote is no longer revertable/saveable.
                DashClearDirty();
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

        // (Re)arm the share countdown with the latest saved value. Called in
        // place of a direct DashTrySilentRedlineSubmit by both the steppers
        // and the keypad, so "type it, then fine-tune with taps" also
        // collapses into one submission. The local save has already happened
        // by the time this runs; only the community share waits.
        private void DashScheduleRedlineShare(int claimed)
        {
            lock (_dashRedlineShareLock)
            {
                _dashRedlineSharePending = true;
                _dashRedlineShareValue   = claimed;
                _dashRedlineShareGame    = _activeGame;
                _dashRedlineShareCar     = _activeCarId;
                if (_dashRedlineShareTimer == null)
                    _dashRedlineShareTimer = new System.Threading.Timer(
                        _ => DashFlushRedlineShare(), null,
                        DashRedlineShareQuietMs, System.Threading.Timeout.Infinite);
                else
                    _dashRedlineShareTimer.Change(
                        DashRedlineShareQuietMs, System.Threading.Timeout.Infinite);
            }
        }

        // Timer body; End also calls it so quitting SimHub inside the quiet
        // window doesn't drop the last share. No-op when nothing is pending.
        private void DashFlushRedlineShare()
        {
            int claimed; string game, carId;
            lock (_dashRedlineShareLock)
            {
                if (!_dashRedlineSharePending) return;
                _dashRedlineSharePending = false;
                claimed = _dashRedlineShareValue;
                game    = _dashRedlineShareGame;
                carId   = _dashRedlineShareCar;
            }
            try { DashTrySilentRedlineSubmit(claimed, game, carId); } catch { }
        }

        // Silent community submit for a dash redline pin. Desktop parity
        // (MaybePromptToSubmitRedlineData) with the two dialog cases turned
        // into skips: an unsettled consent never submits, and a
        // limiter-suspect value (within 2% of the observed rev ceiling) is
        // saved locally but not shared, because the desktop would have asked
        // "are you sure this isn't the limiter?" first.
        // Takes the CAPTURED game/car pair from arm time: re-reading the
        // live fields here opened a window where a car swap landing between
        // the flush's check and the submit attributed the old car's redline
        // to the new car. The single mismatch gate below both drops stale
        // values and keeps the active-car state reads (observed ceiling,
        // per-gear pins) coherent with the pair being submitted.
        private void DashTrySilentRedlineSubmit(int claimed, string game, string carId)
        {
            var s = Settings;
            if (s == null || !s.AutoSubmitCarFacts || !s.CommunityEnabled) return;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (game != _activeGame || carId != _activeCarId) return;   // armed for a departed car: drop
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
