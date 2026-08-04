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
using System.Drawing;
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
        // "redline" | "fx:<Key>" (a _dashFx table key) | "modeb:<Key>" (a
        // _dashModeB table key). The title doubles as the
        // validation-feedback line: SET with a bad value swaps it for a
        // specific error (with the field's range), and the next keypress
        // restores the base title.
        private volatile string _dashKeypadTarget = "";
        private volatile string _dashKeypadTitle = "";
        private volatile string _dashKeypadBaseTitle = "";
        private float _dashKeypadMin, _dashKeypadMax;   // valid range for the open session

        // Active tab for the dash's tab-bar navigation (screen indices:
        // 0=Home, 1=Car facts, 2=Effects, 3=Presets, 4=Visualizer,
        // 5=Tele-FFB). Every dash screen gates its ScreenEnabledExpression
        // on Dash.Tab, so the plugin owns which screen shows and the tab
        // bar is the only navigation. Replaces screen swiping: the web
        // viewer fires ButtonItems on touch-down and swallows the gesture,
        // so on a button-dense screen starting a swipe kept triggering
        // whatever the finger landed on without ever changing screen. With
        // exactly one screen enabled, SimHub's swipe/NextScreen is inert.
        // Seeded at Init from Settings (DashLastTab or DashDefaultTab per
        // the remember-last-tab pref); tab taps write back DashLastTab.
        private const int DashTabCount = 7;
        private volatile int _dashTab;

        // Tab-bar SLOT indirection so users can hide and reorder tabs from
        // the Settings tab. The djson's six slots are position-fixed; each
        // binds its label / visibility / highlight to Dash.TabSlot<i>.* and
        // fires DashTabSlotSelect<i>, and the plugin maps slots to screens
        // here. _dashTabSlots = enabled screen indices in display order
        // (never empty; sanitizer falls back to Drive). Volatile reference
        // swap: rebuilt on the UI/action thread, read per property poll.
        // Index-matched with RemoteDashTabNames in SettingsControl; add a
        // screen to one and it goes in the other.
        private static readonly string[] DashTabNames =
            { "GAINS", "CAR FACTS", "EFFECTS", "PRESETS", "VISUALIZER", "TELE-FFB", "DRIVE" };
        // Factory display order: Drive leads (it is the while-driving screen),
        // then Home, and Tele-FFB sits between Effects and Presets. An empty
        // stored DashTabOrder resolves to exactly this. NOTE an existing
        // install keeps its saved order and picks Drive up at the END, since
        // the sanitizer appends unknown-to-the-stored-list tabs rather than
        // reordering what the user chose.
        private static readonly int[] DashTabFactoryOrder = { 6, 0, 1, 4, 2, 5, 3 };
        // Tabs a fresh install starts with switched OFF. Gains is here
        // because the Drive tab's own gains box covers it, so the tab is a
        // duplicate for most people; it is one checkbox away in Settings.
        // Applied ONLY when the user has never touched the tab editor (an
        // empty DashTabOrder), so nobody's existing layout is rearranged by
        // an update, and an empty disabled list still means "all on" for
        // anyone who has configured tabs. It cannot be a default on the
        // settings field itself: see project-settings-json-append-landmine.
        private static readonly int[] DashTabFactoryDisabled = { 0 };
        private volatile int[] _dashTabSlots = new int[0];

        // ------------------------------------------------------------------
        // Drive screen: four corner boxes around the gear readout. The dash
        // emits EVERY content widget into EVERY slot and shows one, gated on
        // Dash.Drive.Slot<i>; the plugin just publishes which key each slot
        // holds. That keeps swapping instant (no dash regeneration) at the
        // cost of item count, which is why the scope option renders a reduced
        // column count in a box rather than the full Visualizer trace.
        // Everything except CarFacts and Scope binds to SimHub's own game
        // properties, so no telemetry is plumbed through this plugin for it.
        // ------------------------------------------------------------------
        // Damage is deliberately absent: no shipped SimHub dashboard binds a
        // damage property, so the names are unverified, and Forza's damage
        // model is thin anyway. A relative (drivers ahead / behind) box is the
        // obvious next option: PersistantTrackerPlugin.DriverAhead_NN_* and
        // DriverBehind_NN_* carry it without the obsolete leaderboard item.
        // Sorted by LABEL, with Empty pinned last: it is the absence of a
        // choice rather than one of them, so it does not belong in the E's.
        // Keys and labels are index-matched, and the on-dash picker indexes
        // a tile straight into these, so all three lists move together.
        internal static readonly string[] DashDriveContentKeys =
            { "CarFacts", "Damage", "Friction", "Fuel", "GCircle", "Home",
              "Inputs", "Delta", "Presets", "Radar", "Relative",
              "TyreTemps", "TyreWear", "Scope", "None" };
        // Friendly labels for the Settings-tab pickers, index-matched above.
        internal static readonly string[] DashDriveContentLabels =
            { "Car facts", "Damage", "Friction circle", "Fuel", "G circle",
              "Gains", "Inputs", "Lap delta + times", "Presets", "Radar",
              "Relative", "Tyre temps", "Tyre wear", "Visualizer", "Empty" };
        // Slot order: top-left, top-right, bottom-left, bottom-right. The
        // bottom pair is what a phone sees when two-row layout is off, so the
        // two most useful boxes live there.
        private static readonly string[] DashDriveFactorySlots =
            { "CarFacts", "TyreTemps", "Scope", "GCircle" };
        internal const int DashDriveSlotCount = 4;
        // Which box the on-dash picker is editing. Set when it opens, read
        // by the picker's title and by every tile it offers.
        private volatile int _dashDriveEditSlot;
        // Cached sanitized slots, refreshed alongside the tab slot map so the
        // property getters never re-walk settings per poll.
        private volatile string[] _dashDriveSlots = (string[])DashDriveFactorySlots.Clone();

        /// <summary>The four Drive-screen slot contents, sanitized: an unknown
        /// or missing entry falls back to that slot's factory default, so an
        /// empty stored list is exactly the shipped layout.</summary>
        internal string[] GetDashDriveSlots()
        {
            var outp = new string[DashDriveSlotCount];
            var stored = Settings?.DashDriveSlots;
            for (int i = 0; i < DashDriveSlotCount; i++)
            {
                string v = stored != null && i < stored.Count ? stored[i] : null;
                // Lap times merged into Lap delta, so anyone already using it
                // lands on the box that absorbed it rather than being reset to
                // a factory default with nothing to do with their choice.
                if (v == "LapTimes") v = "Delta";
                outp[i] = !string.IsNullOrEmpty(v)
                          && Array.IndexOf(DashDriveContentKeys, v) >= 0
                    ? v
                    : DashDriveFactorySlots[i];
            }
            return outp;
        }

        /// <summary>Full tab order (screen indices, disabled tabs included),
        /// sanitized. Shared by the dash slot map and the Settings-tab
        /// editor. Unknown indices drop; tabs the stored list doesn't know
        /// (empty list, or a tab added by an update) append at the end in
        /// factory order. Dedupe keeps the LAST occurrence deliberately:
        /// Json.NET's Auto ObjectCreationHandling appends a stored array
        /// onto a pre-initialized list, so a list corrupted by the old
        /// non-empty default reads factory-prefix + user-order-suffix, and
        /// last-wins recovers the user's order instead of the prefix.</summary>
        internal List<int> GetDashTabFullOrder()
        {
            var order = new List<int>(DashTabCount);
            var seen = new HashSet<int>();
            var stored = Settings?.DashTabOrder;
            if (stored != null)
                for (int i = stored.Count - 1; i >= 0; i--)
                {
                    int t = stored[i];
                    if (t >= 0 && t < DashTabCount && seen.Add(t)) order.Add(t);
                }
            order.Reverse();
            foreach (int t in DashTabFactoryOrder)
                if (seen.Add(t)) order.Add(t);
            return order;
        }

        /// <summary>Which tabs are switched off right now. A stored order is
        /// the signal that the user has been through the tab editor, so from
        /// then on their disabled list is taken literally, empty included.
        /// Before that they are on factory settings and get the factory set.
        /// Shared with the Settings editor so both agree on what is off.</summary>
        internal List<int> DashEffectiveDisabledTabs()
        {
            var stored = Settings?.DashTabsDisabled;
            if (stored != null && stored.Count > 0) return new List<int>(stored);
            bool configured = Settings?.DashTabOrder != null && Settings.DashTabOrder.Count > 0;
            return configured ? new List<int>() : new List<int>(DashTabFactoryDisabled);
        }

        /// <summary>Rebuild the slot map from settings. Called at Init, by
        /// the Settings-tab editor after any layout change, and by the
        /// restore/import/account-switch paths that rewrite the layout; if
        /// the current tab just got disabled, snaps to the first enabled
        /// one.</summary>
        internal void RefreshDashTabSlots()
        {
            // Drive-screen box assignments ride the same refresh, so every
            // caller that reacts to a settings change (editor, restore,
            // import, account switch) picks both up.
            _dashDriveSlots = GetDashDriveSlots();
            var order = GetDashTabFullOrder();
            var disabled = DashEffectiveDisabledTabs();
            if (disabled.Count > 0)
                order.RemoveAll(t => disabled.Contains(t));
            if (order.Count == 0) order.Add(0);   // hand-edited file disabled everything
            _dashTabSlots = order.ToArray();
            if (Array.IndexOf(_dashTabSlots, _dashTab) < 0)
            {
                // The snap is a forced navigation: drop any open overlay
                // exactly like DashSelectTab does, else an overlay whose
                // members live only on the now-disabled screen strands with
                // every button on the dash hidden (dead remote until a
                // SimHub restart).
                _dashTab = _dashTabSlots[0];
                _dashOverlay = "";
                _dashPresetScope = "";
            }
        }

        // Live RPM for the rev strip, stashed per frame in DispatchFrame.
        // The strip's percent uses OUR effective redline (user pin >
        // community > telemetry > estimate), which is the whole point: on
        // Forza a generic SimHub rev bar keys off the limiter, not the
        // redline start.
        private volatile float _dashLiveRpm;
        // Gear + speed for the Drive tab, stashed from the same frame.
        private volatile string _dashLiveGear = "";
        private volatile float _dashLiveSpeedKmh;

        // Telemetry FFB's own grip model, run for the dash while its force
        // path is off. Utilization was never a product of composing a force:
        // it is an EMA of the front combined-slip channel over the learned
        // grip peak, and both of those exist whenever telemetry does. Kept in
        // its own EMA so a dash box can never perturb the FFB state, and fed
        // from DispatchFrame, which runs regardless.
        private volatile float _dashModelGripEma;
        private volatile float _dashModelUtil;
        private volatile bool  _dashGripChannelSeen;

        // Fallback reference for games with no slip channel at all: the
        // hardest combined load this car has actually taken. Every car and
        // surface has its own ceiling, so a fixed number would read 40% in a
        // road car and peg in a race car. Floored at 0.8 g so a gentle cruise
        // cannot make itself the reference and report being at the limit, and
        // it bleeds down slowly so one kerb strike does not flatten the scale.
        private const float DashGripFloorG = 0.8f;
        private float _dashGripPeakG = DashGripFloorG;
        private int   _dashGripCarKey;

        private int _dashGripTick;

        /// <summary>Runs the grip half of the Telemetry FFB model on the
        /// telemetry thread, so the friction circle reads the same number
        /// whether or not that model is driving the wheel. Mirrors the EMA and
        /// the peak divisor in ComputeModeBForce, including the asymmetry:
        /// breaking away tracks fast because the go-light cue must be instant,
        /// re-gripping is slowed 4x because a tyre reloads over its relaxation
        /// length rather than snapping.</summary>
        private void DashUpdateModelGrip()
        {
            float combined = _lastFrontCombined;
            if (combined > 0f) _dashGripChannelSeen = true;
            if (!_dashGripChannelSeen) return;

            int now = Environment.TickCount;
            int last = _dashGripTick;
            _dashGripTick = now;
            // First frame, or a gap (paused, alt-tabbed): seed rather than
            // integrate across it.
            double dtMs = last == 0 ? 0 : unchecked(now - last);
            if (dtMs <= 0 || dtMs > 250)
            {
                _dashModelGripEma = combined;
            }
            else
            {
                double tau = Math.Max(2f, _pModeBEmaMs);
                float alpha = (float)(1.0 - Math.Exp(-dtMs / tau));
                float a = combined >= _dashModelGripEma
                    ? alpha
                    : (float)(1.0 - Math.Exp(-dtMs / (4.0 * tau)));
                _dashModelGripEma += (combined - _dashModelGripEma) * a;
            }
            double peakBase = _mbAutoCalOn ? 1.0 : _pModeBPeakU;
            double peakDiv  = Math.Max(0.2, peakBase * (double)_mbCalPeak);
            _dashModelUtil  = (float)TrueforceForAll.Core.ModeBComposer
                .UtilizationFloor(_dashModelGripEma / peakDiv);
        }

        // ---------- themes ----------
        // A theme is a PALETTE, not a layout: the dashboard binds its
        // structural colours to these, so switching one repaints every
        // screen live with no reload. Semantic colours (green for good, red
        // for trouble) are deliberately NOT themed, because a theme that
        // can turn a warning green is a theme that can lie.
        //
        // Adding one is a row here plus a row in DashThemeNames. Nothing in
        // the dashboard generator needs to know it exists.
        internal sealed class DashTheme
        {
            public string Name, Bg, Card, CardEdge, Sub, Btn, BtnEdge, Tile, TileOn, Text, Muted;
            // Three accents for the idle card's ambient art. Not used for
            // anything that carries meaning, so a theme is free to be loud
            // here without a loud theme being able to misreport anything.
            public string Accent1, Accent2, Accent3;
        }

        internal static readonly DashTheme[] DashThemes =
        {
            new DashTheme {
                Name = "Midnight", Bg = "#FF000000", Card = "#00FFFFFF", CardEdge = "#FF4E5668",
                Sub = "#FF0E0E10", Btn = "#FF1C1C20", BtnEdge = "#FF3A4150",
                Tile = "#FF141414", TileOn = "#FF23503A", Text = "#FFF2F4F8", Muted = "#FF8B93A7",
                Accent1 = "#FF2E6FA8", Accent2 = "#FF2E9478", Accent3 = "#FF6A47A0" },
            new DashTheme {
                Name = "Slate", Bg = "#FF101216", Card = "#FF1B1F27", CardEdge = "#00FFFFFF",
                Sub = "#FF232936", Btn = "#FF232936", BtnEdge = "#00FFFFFF",
                Tile = "#FF232936", TileOn = "#FF23503A", Text = "#FFF2F4F8", Muted = "#FF8B93A7",
                Accent1 = "#FF3D6FB5", Accent2 = "#FF37D67A", Accent3 = "#FF5A6478" },
            new DashTheme {
                Name = "Carbon", Bg = "#FF0A0B0D", Card = "#FF141619", CardEdge = "#FF2A2E36",
                Sub = "#FF101216", Btn = "#FF1E222A", BtnEdge = "#FF343A45",
                Tile = "#FF1E222A", TileOn = "#FF23503A", Text = "#FFEDEFF3", Muted = "#FF858C99",
                Accent1 = "#FF3A4150", Accent2 = "#FF4A5262", Accent3 = "#FF2E3440" },
            new DashTheme {
                Name = "Blueprint", Bg = "#FF06121F", Card = "#00FFFFFF", CardEdge = "#FF2C6E9B",
                Sub = "#FF091A2B", Btn = "#FF0D2438", BtnEdge = "#FF2C6E9B",
                Tile = "#FF0D2438", TileOn = "#FF14503F", Text = "#FFDCEBF7", Muted = "#FF7FA6C4",
                Accent1 = "#FF2C6E9B", Accent2 = "#FF3E96C9", Accent3 = "#FF1B4C6E" },
            // The loud half. These exist because the first four are safe and
            // safe gets boring on a screen you look at every session.
            new DashTheme {
                Name = "Ember", Bg = "#FF120705", Card = "#00FFFFFF", CardEdge = "#FF8A3B1E",
                Sub = "#FF1B0C08", Btn = "#FF25100A", BtnEdge = "#FF8A3B1E",
                Tile = "#FF25100A", TileOn = "#FF5C3410", Text = "#FFFFEDE4", Muted = "#FFC08C74",
                Accent1 = "#FFD1541F", Accent2 = "#FFE8912F", Accent3 = "#FF8A1F1F" },
            new DashTheme {
                Name = "Neon", Bg = "#FF05030B", Card = "#00FFFFFF", CardEdge = "#FF7A2BC4",
                Sub = "#FF0C0718", Btn = "#FF150C28", BtnEdge = "#FFB13BE8",
                Tile = "#FF150C28", TileOn = "#FF1F5C4A", Text = "#FFF6ECFF", Muted = "#FFA98CC4",
                Accent1 = "#FFB13BE8", Accent2 = "#FF23D3E8", Accent3 = "#FFE8329B" },
            new DashTheme {
                Name = "Forest", Bg = "#FF040D08", Card = "#00FFFFFF", CardEdge = "#FF2F7A4A",
                Sub = "#FF08160E", Btn = "#FF0D2115", BtnEdge = "#FF2F7A4A",
                Tile = "#FF0D2115", TileOn = "#FF2A6B3C", Text = "#FFE8F5EC", Muted = "#FF86AE95",
                Accent1 = "#FF2F7A4A", Accent2 = "#FF6FBF73", Accent3 = "#FF1B4F3A" },
            new DashTheme {
                Name = "Mono", Bg = "#FF000000", Card = "#00FFFFFF", CardEdge = "#FFB8BCC4",
                Sub = "#FF121212", Btn = "#FF1C1C1C", BtnEdge = "#FF9A9EA6",
                Tile = "#FF1C1C1C", TileOn = "#FF4A4A4A", Text = "#FFFFFFFF", Muted = "#FF9A9EA6",
                Accent1 = "#FF6E7278", Accent2 = "#FF9A9EA6", Accent3 = "#FF44484E" },
        };

        internal static string[] DashThemeNames()
        {
            var n = new string[DashThemes.Length];
            for (int i = 0; i < DashThemes.Length; i++) n[i] = DashThemes[i].Name;
            return n;
        }

        /// <summary>The selected theme, or the first one when the stored name
        /// is unknown: a dashboard with no palette is an unreadable dashboard,
        /// so this never returns null.</summary>
        internal DashTheme ActiveDashTheme()
        {
            string want = Settings?.DashTheme;
            if (!string.IsNullOrEmpty(want))
                foreach (var t in DashThemes)
                    if (string.Equals(t.Name, want, StringComparison.OrdinalIgnoreCase)) return t;
            return DashThemes[0];
        }

        // ---------- radar ----------
        // Opponents carry RelativeCoordinatesToPlayer, a PointF already in
        // the player's own frame, and a length in metres. SimHub's own
        // SpotterCarLeft/Right is a bare yes or no with no distance in it,
        // and its radar item colours every opponent alike, so both the dot
        // colours and the proximity warning are worked out here.
        internal const int   RadarDots   = 8;
        internal const float RadarRangeM = 40f;   // the rim
        internal const float RadarMidM   = 20f;   // white becomes yellow
        internal const float RadarNearM  = 8f;    // yellow becomes red

        // Normalised to the circle, -1..1, y negative ahead. Level is 0 for
        // an empty slot, then 1 far, 2 middle, 3 close, which is what picks
        // the dot colour. Quadrants are the diagonals, front/right/rear/left,
        // 0 clear, 1 something in there, 2 something close.
        private volatile float[] _radarX = new float[RadarDots];
        private volatile float[] _radarY = new float[RadarDots];
        private volatile int[]   _radarLvl = new int[RadarDots];
        private volatile int[]   _radarQuad = new int[4];

        /// <summary>Rebuild the radar from this frame's opponents. Called from
        /// DataUpdate; one walk of a list SimHub has already built, and it
        /// stops at once when there is nobody out there. Nearest first, so in
        /// heavy traffic the closest cars are the ones that get drawn.</summary>
        private void DashUpdateRadar(GameReaderCommon.GameData data)
        {
            var xs = new float[RadarDots];
            var ys = new float[RadarDots];
            var lv = new int[RadarDots];
            var qd = new int[4];

            var opps = data?.NewData?.Opponents;
            if (opps != null && opps.Count > 0)
            {
                var near = new List<KeyValuePair<double, PointF>>(RadarDots + 8);
                foreach (var o in opps)
                {
                    if (o == null || o.IsPlayer || !o.IsConnected) continue;
                    if (o.IsCarInPit || o.IsCarInPitLane) continue;
                    var rc = o.RelativeCoordinatesToPlayer;
                    if (!rc.HasValue) continue;
                    float rx = rc.Value.X, ry = rc.Value.Y;
                    if (float.IsNaN(rx) || float.IsNaN(ry)) continue;
                    double d = o.RelativeVectorLengthToPlayer;
                    if (d <= 0 || double.IsNaN(d)) d = Math.Sqrt(rx * rx + ry * ry);
                    if (d > RadarRangeM) continue;
                    near.Add(new KeyValuePair<double, PointF>(d, new PointF(rx, ry)));

                    // Diagonals as the sector boundaries, so a car dead ahead
                    // lands wholly in front rather than half in two corners.
                    // Axis convention CONFIRMED on track 2026-08-03: positive
                    // X is the player's right, positive Y is behind. SimHub
                    // documents neither, so do not "tidy" these signs.
                    double bearing = Math.Atan2(rx, -ry) * (180.0 / Math.PI);
                    if (bearing < 0) bearing += 360.0;
                    int q = (int)Math.Floor(((bearing + 45.0) % 360.0) / 90.0);
                    if (q < 0 || q > 3) continue;
                    int ql = d <= RadarNearM ? 2 : (d <= RadarMidM ? 1 : 0);
                    if (ql > qd[q]) qd[q] = ql;
                }
                near.Sort((a, b) => a.Key.CompareTo(b.Key));
                for (int i = 0; i < near.Count && i < RadarDots; i++)
                {
                    xs[i] = near[i].Value.X / RadarRangeM;
                    ys[i] = near[i].Value.Y / RadarRangeM;
                    double d = near[i].Key;
                    lv[i] = d <= RadarNearM ? 3 : (d <= RadarMidM ? 2 : 1);
                }
            }
            _radarX = xs; _radarY = ys; _radarLvl = lv; _radarQuad = qd;
        }

        // Idle mode: how long the car has been stopped, and whether the user
        // waved this stop away. Both are per-stop, not persisted.
        private const int IdlePhaseMs = 60000;
        private int  _dashIdleSinceTick;
        private volatile bool _dashIdleDismissed;
        private bool _dashIdleGameWasOn;

        /// <summary>Should the idle card be showing.
        ///
        /// Two different situations, and only one of them is a timer. With no
        /// game running there is no dashboard to show, so the card is the
        /// screen: it appears at once. With a game running but the car sitting
        /// still, the real dashboard is the useful thing and the card is an
        /// interruption, so that case waits out the delay the user chose and
        /// is meant to be set long.
        ///
        /// Driving always clears it, and so does a game appearing, which is
        /// the clearest "I am back" there is.</summary>
        private bool DashIdleActive()
        {
            if (Settings?.DashIdleEnabled != true) return false;
            // Never over an open keypad or picker. Those are the one set of
            // buttons the hide pass leaves live, being an overlay's own, and
            // a user part way through typing a redline is plainly still here.
            if (!string.IsNullOrEmpty(_dashOverlay)) return false;

            bool gameOn = !string.IsNullOrEmpty(_currentGameName);
            if (gameOn != _dashIdleGameWasOn)
            {
                _dashIdleGameWasOn = gameOn;
                _dashIdleSinceTick = 0;
                if (gameOn) _dashIdleDismissed = false;
            }
            if (!gameOn) return !_dashIdleDismissed;

            bool driving = !_telemetryStalled
                && (_telemetrySource?.IsSessionActive ?? true)
                && _dashLiveSpeedKmh > 3f;
            int now = Environment.TickCount;
            if (driving)
            {
                _dashIdleSinceTick = 0;
                _dashIdleDismissed = false;
                return false;
            }
            if (_dashIdleSinceTick == 0) _dashIdleSinceTick = now == 0 ? 1 : now;
            if (_dashIdleDismissed) return false;
            int delayMs = Math.Max(0, Settings.DashIdleDelaySeconds) * 1000;
            return unchecked(now - _dashIdleSinceTick) >= delayMs;
        }

        /// <summary>Plugin version as the idle card shows it. Trailing zero
        /// revision dropped: 0.2.6.0 is noise, 0.2.6 is the release.</summary>
        private static string DashPluginVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "";
            return v.Revision == 0
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }

        /// <summary>Grip in use for the friction circle, with Telemetry FFB's
        /// force path switched off. Same model and same numbers where the game
        /// gives a slip channel; measured load against this car's hardest where
        /// it does not.</summary>
        private float DashMeasuredGripUse()
        {
            if (_telemetryStalled) return 0f;
            if (_dashGripChannelSeen) return _dashModelUtil;

            float lat = _lastSwayAccel / 9.81f, lon = _lastSurgeAccel / 9.81f;
            if (float.IsNaN(lat) || float.IsNaN(lon)) return 0f;
            float g = (float)Math.Sqrt(lat * lat + lon * lon);
            // A car change starts the reference over: the old ceiling says
            // nothing about the new car.
            int carKey = (_activeCarId ?? "").GetHashCode();
            if (carKey != _dashGripCarKey)
            {
                _dashGripCarKey = carKey;
                _dashGripPeakG  = DashGripFloorG;
            }
            if (g > _dashGripPeakG) _dashGripPeakG = g;
            else _dashGripPeakG = Math.Max(DashGripFloorG, _dashGripPeakG - 0.00002f);
            return g / _dashGripPeakG;
        }
        // Driver inputs for the Drive tab's inputs box. Steer is -2 when the
        // active source reports no steering at all.
        private volatile float _dashLiveThrottle;
        private volatile float _dashLiveSteer = -2f;
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
            // Spike-reduction badge: the device counts reduced packets at
            // 1 kHz; comparing counts at the 32 ms column rate catches the
            // 1 ms events a boolean sample would miss. First sample only
            // seeds the baseline (a count carried over from earlier driving
            // must not light the badge on dash open). Same 150 ms hold +
            // 1.5 s fade contract as the clip glow; yellow on the dash.
            int tamedCount = dev?.SpikeTameCount ?? 0;
            if (_dashSpikeSeen.HasValue && tamedCount != _dashSpikeSeen.Value)
            {
                System.Threading.Interlocked.Exchange(ref _dashSpikeUntilTicks,
                    now + Stopwatch.Frequency * 150 / 1000);
            }
            _dashSpikeSeen = tamedCount;
            _scopeHead = (h + 1) % ScopeCols;
            _scopeAccum = 0f;
        }
        private float _scopeFfbSmooth;   // producer-thread only
        private volatile int _dashClipSign;
        private long _dashClipUntilTicks; // Interlocked; clip hold; glow decays from its expiry
        private int? _dashSpikeSeen;      // producer-thread only; null until first sample
        private long _dashSpikeUntilTicks; // Interlocked; spike hold; glow decays from its expiry

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
            // Tele-FFB screen state. Snapshot-served (not per-poll) because
            // the enabled check walks the ModeBGameEnabled dictionary, which
            // the toggle mutates on another thread.
            public bool ModeBSupported;
            public bool ModeBOn;
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
                // Mirror the desktop header ("headerCar" in SettingsControl): a
                // detected car with no friendly name yet must still read as a
                // car, never "No car detected". Games whose car ids don't match
                // their names (e.g. Wreckfest 2's "car11:default") have an empty
                // CarName until the community fills the car facts, so fall back
                // to the raw carId the desktop already shows. A filled-in name
                // takes over. Display-only: Dash.CarName never feeds a submit.
                s.CarName = !string.IsNullOrWhiteSpace(sum.CarName)
                    ? sum.CarName
                    : (_activeCarId ?? "");
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
                s.ModeBSupported = ActiveGameSupportsModeB;
                s.ModeBOn        = ModeBEnabledForActiveGame;
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
            // Ceiling for the dash steppers and keypad, matched PER EFFECT to
            // that effect's desktop slider Maximum. A dash value above the
            // slider's range cannot be represented on the desktop: WPF coerces
            // the thumb to the max while the readout shows the raw number, and
            // the next drag of that slider writes the coerced value back,
            // silently discarding the dash tune.
            public float Max;
        }
        private DashFx[] _dashFx;

        private DashFx[] BuildDashFxTable() => new[]
        {
            new DashFx { Key = "Engine", Max = 2f,     Kind = SectionKind.Engine,       GetOn = () => ActiveEngine.Enabled,       SetOn = v => ActiveEngine.Enabled = v,       GetGain = () => ActiveEngine.Gain,       SetGain = v => ActiveEngine.Gain = v },
            new DashFx { Key = "Bumps", Max = 2f,      Kind = SectionKind.Bumps,        GetOn = () => ActiveBumps.Enabled,        SetOn = v => ActiveBumps.Enabled = v,        GetGain = () => ActiveBumps.Gain,        SetGain = v => ActiveBumps.Gain = v },
            new DashFx { Key = "Traction", Max = 2f,   Kind = SectionKind.Traction,     GetOn = () => ActiveTraction.Enabled,     SetOn = v => ActiveTraction.Enabled = v,     GetGain = () => ActiveTraction.Gain,     SetGain = v => ActiveTraction.Gain = v },
            new DashFx { Key = "AxleSlip", Max = 3f,   Kind = SectionKind.AxleSlip,     GetOn = () => ActiveAxleSlip.Enabled,     SetOn = v => ActiveAxleSlip.Enabled = v,     GetGain = () => ActiveAxleSlip.Gain,     SetGain = v => ActiveAxleSlip.Gain = v },
            new DashFx { Key = "Kerb", Max = 3f,       Kind = SectionKind.KerbThump,    GetOn = () => ActiveKerbThump.Enabled,    SetOn = v => ActiveKerbThump.Enabled = v,    GetGain = () => ActiveKerbThump.Gain,    SetGain = v => ActiveKerbThump.Gain = v },
            new DashFx { Key = "Lockup", Max = 3f,     Kind = SectionKind.LockupJudder, GetOn = () => ActiveLockupJudder.Enabled, SetOn = v => ActiveLockupJudder.Enabled = v, GetGain = () => ActiveLockupJudder.Gain, SetGain = v => ActiveLockupJudder.Gain = v },
            new DashFx { Key = "Shift", Max = 2f,      Kind = SectionKind.Shift,        GetOn = () => ActiveShift.Enabled,        SetOn = v => ActiveShift.Enabled = v,        GetGain = () => ActiveShift.Gain,        SetGain = v => ActiveShift.Gain = v },
            new DashFx { Key = "Abs", Max = 2f,        Kind = SectionKind.Abs,          GetOn = () => ActiveAbs.Enabled,          SetOn = v => ActiveAbs.Enabled = v,          GetGain = () => ActiveAbs.Gain,          SetGain = v => ActiveAbs.Gain = v },
            new DashFx { Key = "Pit", Max = 2f,        Kind = SectionKind.PitLimiter,   GetOn = () => ActivePitLimiter.Enabled,   SetOn = v => ActivePitLimiter.Enabled = v,   GetGain = () => ActivePitLimiter.Gain,   SetGain = v => ActivePitLimiter.Gain = v },
            new DashFx { Key = "Drs", Max = 2f,        Kind = SectionKind.Drs,          GetOn = () => ActiveDrs.Enabled,          SetOn = v => ActiveDrs.Enabled = v,          GetGain = () => ActiveDrs.Gain,          SetGain = v => ActiveDrs.Gain = v },
            new DashFx { Key = "Collision", Max = 2f,  Kind = SectionKind.Collision,    GetOn = () => ActiveCollision.Enabled,    SetOn = v => ActiveCollision.Enabled = v,    GetGain = () => ActiveCollision.Gain,    SetGain = v => ActiveCollision.Gain = v },
            new DashFx { Key = "RevLimiter", Max = 2f, Kind = SectionKind.RevLimiter,   GetOn = () => ActiveRevLimiter.Enabled,   SetOn = v => ActiveRevLimiter.Enabled = v,   GetGain = () => ActiveRevLimiter.Gain,   SetGain = v => ActiveRevLimiter.Gain = v },
            new DashFx { Key = "Airborne", Max = 0f,   Kind = SectionKind.Airborne,     GetOn = () => ActiveAirborne.Enabled,     SetOn = v => ActiveAirborne.Enabled = v,     GetGain = null,                          SetGain = null },
        };

        // Multiplicative gain step so one press moves small gains (0.07) and
        // large gains (1.5) by a comparable feel amount. Floor + zero rules:
        // stepping down below 0.005 lands on exactly 0 (silence), stepping up
        // from 0 restarts at 0.01.
        private static float DashStepGain(float g, bool up, float max)
        {
            if (max <= 0f) max = 2f;   // table default guard
            if (up)
            {
                if (g >= max) return max;
                if (g < 0.005f) return 0.01f;
                float n = g * 1.12f;
                return n > max ? max : n;
            }
            float d = g / 1.12f;
            return d < 0.005f ? 0f : d;
        }

        // ------------------------------------------------------------------
        // Telemetry FFB (Mode B) knobs for the dash's Tele-FFB screen. These
        // are GLOBAL settings (no SectionKind, no preset/car scope), so the
        // mutate path is the master-gain shape: direct settings write +
        // ApplyModeBFromSettings + PersistSettings. Deliberately does NOT
        // touch the UNSAVED/SAVE bar: there is nothing preset-side to save,
        // exactly like the desktop Telemetry FFB tab. Ranges mirror the
        // desktop sliders: WPF clamps an out-of-range hydrated value to a
        // slider's Max, so a wider dash range would get silently clamped
        // the next time that slider is dragged. Steps are absolute, not
        // the effects' multiplicative x1.12: these are feel ranges with
        // meaningful zeros.
        // ------------------------------------------------------------------
        private sealed class DashModeBKnob
        {
            public string Key;      // property/action suffix + keypad routing key
            public string Label;    // keypad title
            public float Min, Max, Step;
            public string Fmt;      // current-value display format
            public Func<TrueforceSettings, float> Get;
            public Action<TrueforceSettings, float> Set;
        }
        private DashModeBKnob[] _dashModeB;

        private static DashModeBKnob[] BuildDashModeBTable() => new[]
        {
            new DashModeBKnob { Key = "Strength", Label = "STRENGTH",         Min = 0.05f, Max = 1.5f, Step = 0.05f, Fmt = "0.00", Get = s => s.ModeBSatGain,         Set = (s, v) => s.ModeBSatGain = v },
            new DashModeBKnob { Key = "MinForce", Label = "MIN FORCE",        Min = 0f,    Max = 0.5f, Step = 0.01f, Fmt = "0.00", Get = s => s.ModeBMinForce,        Set = (s, v) => s.ModeBMinForce = v },
            new DashModeBKnob { Key = "Damper",   Label = "DAMPING",          Min = 0f,    Max = 0.6f, Step = 0.02f, Fmt = "0.00", Get = s => s.ModeBDamper,          Set = (s, v) => s.ModeBDamper = v },
            new DashModeBKnob { Key = "Center",   Label = "CENTERING",        Min = 0f,    Max = 0.5f, Step = 0.02f, Fmt = "0.00", Get = s => s.ModeBCenter,          Set = (s, v) => s.ModeBCenter = v },
            new DashModeBKnob { Key = "Lat",      Label = "CORNERING WEIGHT", Min = 0f,    Max = 2f,   Step = 0.05f, Fmt = "0.00", Get = s => s.ModeBLatGain,         Set = (s, v) => s.ModeBLatGain = v },
            new DashModeBKnob { Key = "Rise",     Label = "WEIGHT BUILDUP",   Min = 0.2f,  Max = 2f,   Step = 0.05f, Fmt = "0.00", Get = s => s.ModeBRiseGamma,       Set = (s, v) => s.ModeBRiseGamma = v },
            new DashModeBKnob { Key = "Reversal", Label = "REVERSAL DAMPING", Min = 0f,    Max = 1f,   Step = 0.05f, Fmt = "0.00", Get = s => s.ModeBReversalDampGain, Set = (s, v) => s.ModeBReversalDampGain = v },
            new DashModeBKnob { Key = "Smooth",   Label = "SMOOTHING MS",     Min = 5f,    Max = 100f, Step = 5f,    Fmt = "0",    Get = s => s.ModeBEmaMs,           Set = (s, v) => s.ModeBEmaMs = v },
        };

        private void DashNudgeModeB(DashModeBKnob k, float delta)
        {
            var s = Settings;
            if (s == null) return;
            DashNoteActivity();
            // Round to 3 decimals so repeated float adds don't accumulate
            // dust in the readout (0.13 + 0.02 must show 0.15, not 0.1500001).
            float next = (float)Math.Round(k.Get(s) + delta, 3);
            if (next < k.Min) next = k.Min;
            if (next > k.Max) next = k.Max;
            DashSetModeB(k, next);
        }

        // Shared commit for steppers and the keypad. All eight knobs are
        // tunables consumed by ApplyModeBFromSettings (none are feel toggles),
        // so one apply call pushes the live model; the 1 kHz FFB thread picks
        // the volatiles up next tick, no re-arm needed.
        private void DashSetModeB(DashModeBKnob k, float value)
        {
            var s = Settings;
            if (s == null) return;
            try
            {
                k.Set(s, value);
                ApplyModeBFromSettings();
                PersistSettings();
                RaiseDashRemoteChanged();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Dash Telemetry FFB action failed (" + k.Key + "): " + ex.Message);
            }
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
            _dashModeB = BuildDashModeBTable();

            // Opening tab: last used when the user opted into remembering it
            // (default), else their chosen fixed default. Clamped so a
            // settings file written by a future dash with more tabs can't
            // strand this one on a screen that doesn't exist; the slot
            // refresh then snaps a disabled tab to the first enabled one.
            int startTab = Settings?.DashRememberLastTab != false
                ? (Settings?.DashLastTab ?? 0)
                : (Settings?.DashDefaultTab ?? 0);
            _dashTab = Math.Max(0, Math.Min(DashTabCount - 1, startTab));
            RefreshDashTabSlots();

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
            // Spike-reduction badge glow: same contract as FfbClipGlow
            // (solid 1 while the hold is active, then linear fade to 0
            // over 1.5 s); the dash binds it to the yellow SPIKE badge
            // layer's Opacity on the visualizer.
            this.AttachDelegate("Dash.Scope.SpikeGlow", () =>
            {
                long until = System.Threading.Interlocked.Read(ref _dashSpikeUntilTicks);
                if (until == 0) return 0f;
                long nowT = Stopwatch.GetTimestamp();
                if (nowT < until) return 1f;
                float sec = (float)(nowT - until) / Stopwatch.Frequency;
                float g = 1f - sec / 1.5f;
                return g > 0f ? g : 0f;
            });

            // ---------- properties: rev strip (polled at display rate) ----------
            // ---------- properties: Drive screen slots ----------
            // Each box on the Drive screen renders every content option and
            // shows the one whose key matches its slot property, so a change
            // in Settings applies on the next poll with no dash reload.
            for (int sl = 0; sl < DashDriveSlotCount; sl++)
            {
                int idx = sl;
                this.AttachDelegate("Dash.Drive.Slot" + idx, () =>
                {
                    var slots = _dashDriveSlots;
                    return idx < slots.Length ? slots[idx] : "None";
                });
            }
            this.AttachDelegate("Dash.Drive.TwoRows", () => Settings?.DashDriveTwoRows != false);
            this.AttachDelegate("Dash.Drive.EditSlot", () =>
            {
                switch (_dashDriveEditSlot)
                {
                    case 0: return "TOP LEFT";
                    case 1: return "TOP RIGHT";
                    case 2: return "BOTTOM LEFT";
                    default: return "BOTTOM RIGHT";
                }
            });
            // Friction circle: our own Mode B numbers, not the game's. Util is
            // how much of the tyre's grip the model is using (1 = the limit);
            // the g pair gives the direction the load is coming from, taken
            // from the same accelerations the crash duck reads so the box
            // works on any telemetry source we support.
            this.AttachDelegate("Dash.FlagsOn",     () => Settings?.DashFlagsEnabled == true);
            this.AttachDelegate("Dash.RevCentered", () => Settings?.DashRevStripCentered == true);
            this.AttachDelegate("Dash.SpotterOn", () => Settings?.DashSpotterEnabled != false);

            // Structural colours the dashboard paints itself with.
            this.AttachDelegate("Dash.Theme.Bg",       () => ActiveDashTheme().Bg);
            this.AttachDelegate("Dash.Theme.Card",     () => ActiveDashTheme().Card);
            this.AttachDelegate("Dash.Theme.CardEdge", () => ActiveDashTheme().CardEdge);
            this.AttachDelegate("Dash.Theme.Sub",      () => ActiveDashTheme().Sub);
            this.AttachDelegate("Dash.Theme.Btn",      () => ActiveDashTheme().Btn);
            this.AttachDelegate("Dash.Theme.BtnEdge",  () => ActiveDashTheme().BtnEdge);
            this.AttachDelegate("Dash.Theme.Tile",     () => ActiveDashTheme().Tile);
            this.AttachDelegate("Dash.Theme.TileOn",   () => ActiveDashTheme().TileOn);
            this.AttachDelegate("Dash.Theme.Text",     () => ActiveDashTheme().Text);
            this.AttachDelegate("Dash.Theme.Muted",    () => ActiveDashTheme().Muted);
            this.AttachDelegate("Dash.Theme.Accent1",  () => ActiveDashTheme().Accent1);
            this.AttachDelegate("Dash.Theme.Accent2",  () => ActiveDashTheme().Accent2);
            this.AttachDelegate("Dash.Theme.Accent3",  () => ActiveDashTheme().Accent3);
            // A 0..1 phase over 1.2 s, for anything that should pulse. Derived
            // here rather than in the dash so every connected screen pulses
            // together, the same reason the rev flash is plugin side.
            this.AttachDelegate("Dash.PulseT", () =>
                (Environment.TickCount & 0x7FFFFFFF) % 1200 / 1200f);
            // Radar: per dot a position and a level for its colour, plus
            // one level per quadrant so the wedge needs no arithmetic.
            for (int i = 0; i < RadarDots; i++)
            {
                int k = i;   // captured per delegate, not shared
                this.AttachDelegate("Dash.Radar.D" + k + "X", () =>
                { var a = _radarX; return k < a.Length ? a[k] : 9f; });
                this.AttachDelegate("Dash.Radar.D" + k + "Y", () =>
                { var a = _radarY; return k < a.Length ? a[k] : 9f; });
                this.AttachDelegate("Dash.Radar.D" + k + "L", () =>
                { var a = _radarLvl; return k < a.Length ? a[k] : 0; });
            }
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                this.AttachDelegate("Dash.Radar.Q" + k, () =>
                { var a = _radarQuad; return k < a.Length ? a[k] : 0; });
            }
            this.AttachDelegate("Dash.Idle.On",     () => DashIdleActive());
            this.AttachDelegate("Dash.Idle.Style",  () => Settings?.DashIdleStyle ?? "Aurora");
            this.AttachDelegate("Dash.Idle.Name",   () => Settings?.DashIdleDriverName ?? "");
            this.AttachDelegate("Dash.Idle.Number", () => Settings?.DashIdleNumber ?? "");
            this.AttachDelegate("Dash.Idle.NameAbove", () => Settings?.DashIdleNameAbove == true);
            this.AttachDelegate("Dash.Idle.Font", () => Settings?.DashIdleFont ?? "");
            this.AttachDelegate("Dash.Idle.Color",  () =>
            {
                string c = Settings?.DashIdleColor;
                return string.IsNullOrWhiteSpace(c) ? "#FFF2F4F8" : c;
            });
            // Animation phase, 0..1 over 20 s. A PHASE rather than a clock so
            // every curve built on it closes seamlessly at the wrap, and
            // derived plugin-side like the rev flash so every connected dash
            // animates in step rather than each drifting on its own timer.
            this.AttachDelegate("Dash.Idle.T", () =>
                (Environment.TickCount & 0x7FFFFFFF) % IdlePhaseMs / (float)IdlePhaseMs);
            // Plugin status, which is the other half of what an idle screen is
            // for: it is the one time anyone is looking at the dash and not at
            // the road.
            this.AttachDelegate("Dash.Version",   () => DashPluginVersion());
            this.AttachDelegate("Dash.Supporter", () => LastKnownSupporter);
            this.AttachDelegate("Dash.UpdateReady", () =>
                UpdateChecker != null && UpdateChecker.IsUpdateAvailable);
            this.AttachDelegate("Dash.UpdateVersion", () =>
                UpdateChecker != null && UpdateChecker.IsUpdateAvailable
                    ? (UpdateChecker.LatestVersionTag ?? "") : "");
            this.AttachDelegate("Dash.DrivePedals", () => Settings?.DashDrivePedals != false);

            // ---------- properties: Forza dash extras ----------
            // A Forza player usually has "Also forward to SimHub" off, which
            // leaves SimHub's own game properties empty for the whole session,
            // so the Drive tab's tyre / fuel / lap boxes would sit on their
            // "not reported" notice while the data is arriving at OUR
            // listener. These republish what we parse, and the dash prefers
            // them over SimHub's when they are live. Zero means "this title
            // does not report it": Motorsport fills tyre temps and lap data,
            // Horizon leaves parts of it empty, and only the FM2023 packet
            // carries wear at all.
            this.AttachDelegate("Dash.Forza.Live",     () => ForzaUdpSource?.DashExtras != null);
            // Is there a car on a track right now. The Drive boxes use it to
            // decide whether an absent value is a limit of the GAME or just
            // this moment: "this game does not report tyre temperatures" is a
            // claim about the title, and pausing is not evidence for it.
            // Frames are arriving, and where a source knows the difference
            // (Forza keeps sending while paused), it says we are on track.
            // A SimHub-fed game has no such flag, so there the stall watchdog
            // is the whole test, which is right: pausing stops its frames.
            this.AttachDelegate("Dash.SessionLive", () =>
                !_telemetryStalled && (_telemetrySource?.IsSessionActive ?? true));
            this.AttachDelegate("Dash.Forza.TempFL",   () => ForzaUdpSource?.DashExtras?.TireTempFL ?? 0f);
            this.AttachDelegate("Dash.Forza.TempFR",   () => ForzaUdpSource?.DashExtras?.TireTempFR ?? 0f);
            this.AttachDelegate("Dash.Forza.TempRL",   () => ForzaUdpSource?.DashExtras?.TireTempRL ?? 0f);
            this.AttachDelegate("Dash.Forza.TempRR",   () => ForzaUdpSource?.DashExtras?.TireTempRR ?? 0f);
            this.AttachDelegate("Dash.Forza.HasWear",  () => ForzaUdpSource?.DashExtras?.HasWear == true);
            this.AttachDelegate("Dash.Forza.WearFL",   () => ForzaUdpSource?.DashExtras?.TireWearFL ?? 0f);
            this.AttachDelegate("Dash.Forza.WearFR",   () => ForzaUdpSource?.DashExtras?.TireWearFR ?? 0f);
            this.AttachDelegate("Dash.Forza.WearRL",   () => ForzaUdpSource?.DashExtras?.TireWearRL ?? 0f);
            this.AttachDelegate("Dash.Forza.WearRR",   () => ForzaUdpSource?.DashExtras?.TireWearRR ?? 0f);
            // Forza reports fuel as a tank fraction, so publish a percentage.
            this.AttachDelegate("Dash.Forza.FuelPct",  () => (ForzaUdpSource?.DashExtras?.FuelFraction ?? 0f) * 100f);
            this.AttachDelegate("Dash.Forza.Boost",    () => ForzaUdpSource?.DashExtras?.Boost ?? 0f);
            this.AttachDelegate("Dash.Forza.BestLap",  () => ForzaUdpSource?.DashExtras?.BestLapSec ?? 0f);
            this.AttachDelegate("Dash.Forza.LastLap",  () => ForzaUdpSource?.DashExtras?.LastLapSec ?? 0f);
            this.AttachDelegate("Dash.Forza.CurLap",   () => ForzaUdpSource?.DashExtras?.CurrentLapSec ?? 0f);
            this.AttachDelegate("Dash.Forza.Position", () => ForzaUdpSource?.DashExtras?.RacePosition ?? 0);
            // Gear and speed off the live frame, so the Drive tab's centre
            // works on whichever telemetry source is running rather than
            // only when SimHub is being fed. Empty gear and a zero speed
            // read as "no telemetry", which the dash falls back from.
            this.AttachDelegate("Dash.Gear",     () => _telemetryStalled ? "" : _dashLiveGear);
            this.AttachDelegate("Dash.SpeedKmh", () => _telemetryStalled ? 0f : _dashLiveSpeedKmh);
            // Inputs box. Throttle and steering come off the frame (so they
            // work on every source we support); brake rides the Forza extras
            // because the force path never needed it and it is not on the
            // frame. Steering has no SimHub equivalent at all.
            this.AttachDelegate("Dash.Throttle", () => _telemetryStalled ? 0f : _dashLiveThrottle);
            this.AttachDelegate("Dash.Steer",    () => _telemetryStalled ? -2f : _dashLiveSteer);
            this.AttachDelegate("Dash.Brake",    () => ForzaUdpSource?.DashExtras?.Brake01 ?? 0f);
            // -1 means "this source does not report it", which the inputs box
            // reads as "hide the bar". A clutch or handbrake that is genuinely
            // released reports 0 and still draws, so an automatic shows an
            // empty clutch bar rather than losing it.
            this.AttachDelegate("Dash.Clutch",    () => (float?)ForzaUdpSource?.DashExtras?.Clutch01 ?? -1f);
            this.AttachDelegate("Dash.Handbrake", () => (float?)ForzaUdpSource?.DashExtras?.Handbrake01 ?? -1f);
            // Grip in use, for the friction circle. Telemetry FFB has the
            // better answer, because its model knows what the tyre is doing
            // rather than only what the car ended up doing, so it wins when
            // it is running. With it off the measured accelerations still
            // say plenty: how hard the car is loaded right now against the
            // hardest it has been loaded, which needs nothing but the
            // accelerations the g circle already uses.
            this.AttachDelegate("Dash.Drive.Util", () =>
                ActiveGameSupportsModeB && DashSnap().ModeBOn ? ModeBUtilization : DashMeasuredGripUse());
            this.AttachDelegate("Dash.Drive.GLat",  () => _lastSwayAccel  / 9.81f);
            this.AttachDelegate("Dash.Drive.GLong", () => _lastSurgeAccel / 9.81f);

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
                    DashMutateFx(f, () => f.SetGain(DashStepGain(f.GetGain(), up: true, max: f.Max))));
                this.AddAction("DashFx" + f.Key + "GainDown", (a, b) =>
                    DashMutateFx(f, () => f.SetGain(DashStepGain(f.GetGain(), up: false, max: f.Max))));
                this.AddAction("DashFx" + f.Key + "GainOpen", (a, b) =>
                    DashOpenKeypad("fx:" + f.Key, f.Key.ToUpperInvariant() + " GAIN (now "
                        + (Settings == null ? 0f : f.GetGain()).ToString("0.###")
                        + ", max " + f.Max.ToString("0.##") + ")", 0f, f.Max));
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
                // Stage into the car layer first, exactly like the desktop's
                // audio handlers: without this the edit lands on the GLOBAL
                // audio settings, so the UNSAVED bar lights but REVERT finds
                // nothing to undo and the change is already permanent.
                EnsureSectionDraft(SectionKind.Audio);
                SetActiveAudioEnabledLive(!ActiveAudioEnabled);
                PersistSettings();
                DashRecordDirty(SectionKind.Audio);
                RaiseDashRemoteChanged();
            });
            this.AddAction("DashAudioGainUp",   (a, b) => DashNudgeAudioGain(+DashAudioGainStep));
            this.AddAction("DashAudioGainDown", (a, b) => DashNudgeAudioGain(-DashAudioGainStep));

            // ---------- properties + actions: Telemetry FFB (Tele-FFB tab) ----------
            // Mode B settings are global (no preset/car scope), so the
            // mutate path is the master-gain shape and none of this touches
            // the UNSAVED/SAVE bar. Supported/On ride the snapshot: the
            // enabled check walks the ModeBGameEnabled dictionary.
            this.AttachDelegate("Dash.ModeB.Supported",   () => DashSnap().ModeBSupported);
            this.AttachDelegate("Dash.ModeB.On",          () => DashSnap().ModeBOn);
            this.AttachDelegate("Dash.ModeB.RevLightsOn", () => Settings?.ModeBRevLightsEnabled != false);
            this.AddAction("DashModeBToggle", (a, b) =>
            {
                if (Settings == null) return;
                DashNoteActivity();
                if (!ActiveGameSupportsModeB)
                {
                    DashToast(string.IsNullOrEmpty(_activeGame)
                        ? "NO GAME RUNNING - START DRIVING FIRST"
                        : "TELEMETRY FFB IS NOT AVAILABLE FOR THIS GAME");
                    return;
                }
                // Applies live + persists; per-game flag, deliberately not
                // part of the shared recipe.
                SetModeBEnabledForActiveGame(!ModeBEnabledForActiveGame);
                _dashSnapValid = false;
                RaiseDashRemoteChanged();
            });
            this.AddAction("DashModeBRevLightsToggle", (a, b) =>
            {
                if (Settings == null) return;
                DashNoteActivity();
                Settings.ModeBRevLightsEnabled = !Settings.ModeBRevLightsEnabled;
                PersistSettings();
                // Desktop parity (ModeBRevLights_Changed): douse the rim
                // LEDs immediately on disable instead of leaving the last
                // frame lit until the next natural write.
                if (!Settings.ModeBRevLightsEnabled) TurnOffRpmLeds();
                RaiseDashRemoteChanged();
            });
            // Tap the gear column on the Drive tab to switch the rev strip
            // between full width and the gear column. It is the one dash
            // setting you want to try rather than reason about, and the
            // column has no other tap target, so the whole thing is the
            // control. Also on a Settings checkbox for discoverability.
            // Dismiss idle for this stop. Deliberately NOT a setting: it
            // clears itself the moment the car moves, so a tap means "not
            // now" rather than "never again".
            this.AddAction("DashIdleExit", (a, b) =>
            {
                DashNoteActivity();
                _dashIdleDismissed = true;
                RaiseDashRemoteChanged();
            });
            // Change what a Drive box shows, from the dash. Four openers and
            // one tile per content type: the picker applies to whichever box
            // was tapped, so the tiles do not need to know about slots.
            for (int i = 0; i < DashDriveSlotCount; i++)
            {
                int slot = i;   // captured per action, not shared
                this.AddAction("DashDriveBoxOpen" + slot, (a2, b2) =>
                {
                    DashNoteActivity();
                    _dashDriveEditSlot = slot;
                    _dashOverlay = "drivebox";
                    RaiseDashRemoteChanged();
                });
            }
            for (int i = 0; i < DashDriveContentKeys.Length; i++)
            {
                int idx = i;
                this.AddAction("DashDriveBoxPick" + idx, (a2, b2) =>
                {
                    if (Settings == null) return;
                    DashNoteActivity();
                    var cur = GetDashDriveSlots();
                    int slot = _dashDriveEditSlot;
                    if (slot < 0 || slot >= cur.Length) { _dashOverlay = ""; return; }
                    cur[slot] = DashDriveContentKeys[idx];
                    // Stored as a plain list of four, which is what the
                    // sanitizer expects to read back.
                    Settings.DashDriveSlots = new List<string>(cur);
                    PersistSettings();
                    RefreshDashTabSlots();
                    _dashOverlay = "";
                    DashToast("BOX SET TO " + DashDriveContentLabels[idx].ToUpperInvariant());
                    RaiseDashRemoteChanged();
                });
            }
            this.AddAction("DashDriveBoxCancel", (a2, b2) =>
            {
                _dashOverlay = "";
                RaiseDashRemoteChanged();
            });
            this.AddAction("DashRevStripSpanToggle", (a, b) =>
            {
                if (Settings == null) return;
                DashNoteActivity();
                Settings.DashRevStripCentered = !Settings.DashRevStripCentered;
                PersistSettings();
                DashToast(Settings.DashRevStripCentered
                    ? "REV STRIP OVER THE GEAR"
                    : "REV STRIP FULL WIDTH");
                RaiseDashRemoteChanged();
            });
            foreach (var kb in _dashModeB)
            {
                var k = kb;   // capture per iteration
                this.AttachDelegate("Dash.ModeB." + k.Key, () =>
                {
                    var s = Settings;
                    return s == null ? 0f : k.Get(s);
                });
                this.AddAction("DashModeB" + k.Key + "Up",   (a, b) => DashNudgeModeB(k, +k.Step));
                this.AddAction("DashModeB" + k.Key + "Down", (a, b) => DashNudgeModeB(k, -k.Step));
                this.AddAction("DashModeB" + k.Key + "Open", (a, b) =>
                {
                    var s = Settings;
                    if (s == null) return;
                    DashOpenKeypad("modeb:" + k.Key,
                        k.Label + " (now " + k.Get(s).ToString(k.Fmt) + ", "
                        + k.Min.ToString("0.##") + "-" + k.Max.ToString("0.##") + ")",
                        k.Min, k.Max);
                });
            }

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

            // ---------- properties + actions: tab-bar navigation ----------
            // The bar is six position-fixed SLOTS; each binds its label,
            // visibility and highlight to these properties and fires the
            // slot action, and the plugin maps slots to screens per the
            // user's layout. Direct DashTabSelect<screen> actions stay
            // registered for compatibility with an older deployed djson.
            for (int i = 0; i < DashTabCount; i++)
            {
                int slot = i;
                this.AttachDelegate("Dash.TabSlot" + slot + ".Label", () =>
                {
                    var slots = _dashTabSlots;
                    return slot < slots.Length ? DashTabNames[slots[slot]] : "";
                });
                this.AttachDelegate("Dash.TabSlot" + slot + ".On", () =>
                    slot < _dashTabSlots.Length);
                this.AttachDelegate("Dash.TabSlot" + slot + ".Active", () =>
                {
                    var slots = _dashTabSlots;
                    return slot < slots.Length && slots[slot] == _dashTab;
                });
                this.AddAction("DashTabSlotSelect" + slot, (a, b) =>
                {
                    var slots = _dashTabSlots;
                    if (slot >= slots.Length) return;
                    DashSelectTab(slots[slot]);
                });
                this.AddAction("DashTabSelect" + slot, (a, b) => DashSelectTab(slot));
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

        // Shared tab switch for slot taps and the legacy direct-select
        // actions. Tapping the already-active tab is a no-op past the
        // overlay drop (the slot buttons stay live on the active slot).
        private void DashSelectTab(int tab)
        {
            if (tab < 0 || tab >= DashTabCount) return;
            // Disabled tabs are not navigable: a legacy DashTabSelect action
            // from an old deployed djson (or a slot tap racing a desktop
            // disable) must not park the dash on a hidden screen.
            if (Array.IndexOf(_dashTabSlots, tab) < 0) return;
            DashNoteActivity();
            _dashTab = tab;
            // The dash hides the bar while an overlay is up, so overlay
            // state seen here is stale; drop it rather than strand an open
            // overlay on the new tab.
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
            // Close the desktop-disable race: if RefreshDashTabSlots
            // published a layout without this tab between the membership
            // check above and the write, re-snap here; whichever of the two
            // runs last leaves a consistent state.
            var slots = _dashTabSlots;
            if (slots.Length > 0 && Array.IndexOf(slots, _dashTab) < 0)
            {
                _dashTab = slots[0];
                _dashOverlay = "";
                _dashPresetScope = "";
            }
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
            EnsureSectionDraft(SectionKind.Audio);   // car layer, so REVERT can undo it
            // Reaching for the gain on a capture that is off means the user
            // wants to hear it: turning it up otherwise does nothing at all
            // and reads as a broken control. Winding it all the way to zero
            // is the same statement in reverse, so it switches capture off
            // rather than leaving it running on a silent gain.
            if (next <= 0f)
            {
                if (ActiveAudioEnabled) SetActiveAudioEnabledLive(false);
            }
            else if (!ActiveAudioEnabled)
            {
                SetActiveAudioEnabledLive(true);
            }
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
                EnsureSectionDraft(SectionKind.Audio);   // car layer, so REVERT can undo it
                // Same reasoning as the steppers: typing a gain in means the
                // user expects to hear it.
                if (!ActiveAudioEnabled && v > 0f) SetActiveAudioEnabledLive(true);
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
            if (target.StartsWith("modeb:", StringComparison.Ordinal))
            {
                // Range already validated against the min/max stamped at
                // open (the knob's desktop slider range).
                string key = target.Substring(6);
                foreach (var k in _dashModeB)
                {
                    if (k.Key != key) continue;
                    DashSetModeB(k, v);
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
