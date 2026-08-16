using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly TrueforcePlugin _plugin;
        private readonly DispatcherTimer _meterTimer;
        private bool _suppressEvents;

        // Community-context cache for the engine-pulse panel. Keyed by
        // "{game}/{carId}" so a car change triggers exactly one fetch.
        // _engineCommunityCache holds the consensus we'll display + use
        // for the vote CAS guard; null means "no consensus" (display the
        // 'first' affordance). _engineCommunityFetchInFlight prevents
        // duplicate fetches when the per-tick UI refresh fires before the
        // first fetch returns.
        private string _engineCommunityFetchedKey;
        private bool   _engineCommunityFetchInFlight;
        private EngineLayoutConsensus _engineCommunityCache;
        // True while the auto-detect line above already reads
        // "Auto-detected: X (community)": a "Community: X" row underneath
        // would state the same fact twice, so the renderer collapses it.
        // Recomputed in the 4 Hz readout tick (which has the pin + detect
        // source in hand) right before RenderEngineCommunityRow runs.
        private bool _engineCommunityRowRedundant;
        // Sibling redline cache, populated by the same per-car fetch loop.
        // Drives the engine-pulse panel's "Car's redline" line.
        private RedlineConsensus _engineRedlineCache;
        private string _lastShownCarId;
        private string _lastShownGame;
        // Last text we pushed into the CarFacts name / redline boxes. The 16ms
        // panel refresh rewrites a box only when this changes, so clicking
        // Set/Save (which pulls focus off the box mid-click, then holds the
        // button longer than one tick) can't revert the user's uncommitted
        // text before the handler reads it. Without this guard the tick
        // clobbers the box back to the resolved value during the button-hold
        // and the stale value gets saved (the "redline won't set" bug).
        private string _lastCarFactsRedlineText;
        private string _lastCarFactsNameText;
        // One-shot per-session latch so the networked-welcome modal can
        // re-fire from the active-car/game change path (so upgraders who
        // never open Settings still see it) without being spammed every
        // time the user switches cars in the same session. Set true once
        // we attempt the show, regardless of outcome - the modal's own
        // HasSeenNetworkedWelcome / WelcomeNextShowAt gates still apply.
        private bool _welcomeTriggeredThisSession;

        // Forza "Not receiving packets?" auto-open. _forzaZeroSinceTicks marks
        // when the source first read zero packets (0 = not currently at zero);
        // once that stretch lasts ForzaStallExpandTicks we open the
        // troubleshooter a single time (latched by _forzaTroubleshootAutoExpanded
        // so a manual collapse sticks). _forceForzaStall is the STALL test-code
        // override that simulates the stalled state with no live Forza session.
        private long _forzaZeroSinceTicks;
        private bool _forzaTroubleshootAutoExpanded;
        private bool _forceForzaStall;
        // Dev-only (UPDATEDIRTY access code): arm the update modal to (1) report
        // unsaved changes so the pre-update warning fires, and (2) run a
        // locally-picked installer instead of downloading from GitHub. Cleared
        // when the update modal closes.
        private bool _updateLocalInstallerTest;
        private static readonly long ForzaStallExpandTicks =
            System.Diagnostics.Stopwatch.Frequency * 12;   // 12 s sustained zero packets
        // UDP test code: 0 = off, 1 = force Forza banner.
        // Lets us exercise the setup-banner -> "Set up..." -> jump flow without a
        // live (broken) Forza session.
        private int _forceUdpSetupBanner;
        // FZBANNERS test code: 0 = off, 1 = force the two info-tier Forza banners
        // (SimHub-fallback notice + discovered-port banner) visible so their
        // InfoBannerButton styling can be eyeballed without a live Forza session
        // in the relevant telemetry state.
        private int _forceForzaInfoBanners;
        // CarIds we've already prompted to submit engine data for in this
        // SimHub session. The save-time prompt only fires for cars with no
        // Keyed on "{carId}|{layoutEnum}" so each distinct layout pick gets
        // exactly one prompt per session - if the user dismisses or shares
        // for V8 then later picks Inline 6, that's a new pick worth
        // re-prompting. Cleared on plugin reload.
        private readonly HashSet<string> _enginePromptedThisSession
            = new HashSet<string>(StringComparer.Ordinal);
        // Dirty = current tuning has drifted from the active preset's saved
        // snapshot. Set by user changes; cleared by Apply/Save/Import and by
        // game-change auto-apply. Drives the "★ unsaved" suffix and the
        // unsaved-tuning confirmation prompts.
        private bool _dirty;

        // Per-section dirty state. The amber per-section "Save…" button is
        // itself the unsaved-changes indicator: visible only when the
        // section's dirty bit is set. The grey "↶" Revert button next to it
        // shows under the same condition AND when an active preset exists
        // (nothing to revert to otherwise). Master + Ducking are global-only;
        // their popover collapses to the preset choices (no per-car option).
        // Values mirror TrueforcePlugin.SectionKind so we can pass through.
        // Numeric values mirror TrueforcePlugin.SectionKind so we can pass
        // through with a cast.
        private enum EffectKind { Master = 0, Ducking = 1, Audio = 2, Engine = 3, Bumps = 4, Traction = 5, Shift = 6, Abs = 7, SpikeReduction = 8, PitLimiter = 9, Drs = 10, Collision = 11, RevLimiter = 12, Airborne = 13, StationarySpring = 14, AxleSlip = 15, KerbThump = 16, LockupJudder = 17, ImplementThud = 18 }
        private readonly bool[] _effectDirty = new bool[19];
        private System.Windows.Controls.Button GetEffectSaveBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return MasterSaveBtn;
                case EffectKind.Ducking:        return DuckingSaveBtn;
                case EffectKind.Audio:          return AudioSaveBtn;
                case EffectKind.Engine:         return EngineSaveBtn;
                case EffectKind.Bumps:          return BumpsSaveBtn;
                case EffectKind.Traction:       return TractionSaveBtn;
                case EffectKind.Shift:          return ShiftSaveBtn;
                case EffectKind.Abs:            return AbsSaveBtn;
                case EffectKind.SpikeReduction: return SpikeReductionSaveBtn;
                case EffectKind.StationarySpring: return StationarySpringSaveBtn;
                case EffectKind.PitLimiter:     return PitLimiterSaveBtn;
                case EffectKind.Drs:            return DrsSaveBtn;
                case EffectKind.Collision:      return CollisionSaveBtn;
                case EffectKind.RevLimiter:     return RevLimiterSaveBtn;
                case EffectKind.Airborne:       return AirborneSaveBtn;
                case EffectKind.AxleSlip:       return AxleSlipSaveBtn;
                case EffectKind.KerbThump:      return KerbThumpSaveBtn;
                case EffectKind.LockupJudder:   return LockupJudderSaveBtn;
                case EffectKind.ImplementThud:  return ImplementThudSaveBtn;
            }
            return null;
        }
        private System.Windows.Controls.Button GetEffectRevertBtn(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return MasterRevertBtn;
                case EffectKind.Ducking:        return DuckingRevertBtn;
                case EffectKind.Audio:          return AudioRevertBtn;
                case EffectKind.Engine:         return EngineRevertBtn;
                case EffectKind.Bumps:          return BumpsRevertBtn;
                case EffectKind.Traction:       return TractionRevertBtn;
                case EffectKind.Shift:          return ShiftRevertBtn;
                case EffectKind.Abs:            return AbsRevertBtn;
                case EffectKind.SpikeReduction: return SpikeReductionRevertBtn;
                case EffectKind.StationarySpring: return StationarySpringRevertBtn;
                case EffectKind.PitLimiter:     return PitLimiterRevertBtn;
                case EffectKind.Drs:            return DrsRevertBtn;
                case EffectKind.Collision:      return CollisionRevertBtn;
                case EffectKind.RevLimiter:     return RevLimiterRevertBtn;
                case EffectKind.Airborne:       return AirborneRevertBtn;
                case EffectKind.AxleSlip:       return AxleSlipRevertBtn;
                case EffectKind.KerbThump:      return KerbThumpRevertBtn;
                case EffectKind.LockupJudder:   return LockupJudderRevertBtn;
                case EffectKind.ImplementThud:  return ImplementThudRevertBtn;
            }
            return null;
        }
        private static string EffectLabel(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Master:         return "Master";
                case EffectKind.Ducking:        return "Sidechain ducking";
                case EffectKind.Audio:          return "Audio rumble";
                case EffectKind.Engine:         return "Engine pulse";
                case EffectKind.Bumps:          return "Road bumps";
                case EffectKind.Traction:       return "Traction loss";
                case EffectKind.Shift:          return "Gear shift";
                case EffectKind.Abs:            return "ABS pulse";
                case EffectKind.SpikeReduction: return "FFB spike reduction";
                case EffectKind.StationarySpring: return "Stationary spring";
                case EffectKind.PitLimiter:     return "Pit limiter";
                case EffectKind.Drs:            return "DRS";
                case EffectKind.Collision:      return "Collision";
                case EffectKind.RevLimiter:     return "Redline buzz";
                case EffectKind.Airborne:       return "Airborne ducking";
                case EffectKind.AxleSlip:       return "Axle slip";
                case EffectKind.KerbThump:      return "Curb thump";
                case EffectKind.LockupJudder:   return "Lockup judder";
                case EffectKind.ImplementThud:  return "Implement thud";
            }
            return "section";
        }
        // Master + Ducking + SpikeReduction are global-only. The save popover
        // hides the per-car option for these (no override concept).
        private static bool SectionHasCarScope(EffectKind w)
            => w != EffectKind.Master && w != EffectKind.Ducking && w != EffectKind.SpikeReduction
               && w != EffectKind.Airborne && w != EffectKind.StationarySpring
               && w != EffectKind.ImplementThud;

        // Sections whose EDIT handlers route through the per-car DRAFT model
        // (edits land in the car's in-memory override; explicit Save with
        // This-car / Game-default / Both / Reset). The plugin-side machinery is
        // generic, so rolling another section in is just adding it here AND
        // calling _plugin.EnsureSectionDraft(kind) in its edit handlers.
        private static bool SectionUsesDraftModel(EffectKind w)
            => w == EffectKind.RevLimiter;

        // The inline preset library hosted in the Presets tab (replaces the old
        // Manage Presets pop-up Window). Created in the plugin constructor.
        private PresetManagerControl _presetManager;
        private MotdStrip _motdStrip;

        public SettingsControl()
        {
            InitializeComponent();
            WireEditableReadouts();
            // The banner's Install is the same action as the install modal's
            // gold button; style them identically so it reads as one action.
            if (FsModInstallButton != null)
                ModalButtonTheme.Primary(FsModInstallButton);
        }

        public SettingsControl(TrueforcePlugin plugin) : this()
        {
            _plugin = plugin;

            // Support prompt fires on entering the plugin page, so it is scoped to
            // our own surface and never intrudes elsewhere in SimHub.
            IsVisibleChanged += OnSupportPromptVisibilityChanged;

            // Host the preset library inline as the Presets tab. LibraryChanged
            // keeps the always-visible header combos + the live engine in sync
            // after any mutation (rename / delete / set-default / import / …);
            // EditPresetRequested drives the dormant offline-edit flow.
            _presetManager = new PresetManagerControl();
            _presetManager.Init(_plugin);
            _presetManager.LibraryChanged += OnPresetLibraryChanged;
            // Drop the active-card top-community cache when the user
            // refreshes the community panel for the active car. Without
            // this the dropdown shows stale rows until the plugin
            // restarts.
            _presetManager.CarCommunityListRefreshed += OnCarCommunityListRefreshed;
            _presetManager.EditPresetRequested += name => EnterOfflineEditMode(name);
            _presetManager.EditCarPresetRequested += (carId, name) => EnterOfflineEditModeForCar(carId, name);
            _presetManager.UpdatesChipClicked += OnPresetManagerUpdatesChipClicked;
            PresetManagerHost.Children.Add(_presetManager);

            // Message-of-the-day strip across the top of all tabs. Hosted here so
            // it sits above the TabControl and shows on every tab.
            _motdStrip = new MotdStrip();
            _motdStrip.Init(_plugin);
            _motdStrip.ShareRequested = () => ShowShareDialog();
            // "link_discord" MOTD action: run the real Discord link flow (which
            // also joins the server and unlocks roles) instead of opening a bare
            // invite URL. Sign-in first when needed; the Account-tab handler
            // takes it from there.
            _motdStrip.LinkDiscordRequested = () =>
            {
                if (_plugin == null) return;
                if (!_plugin.AuthIsSignedIn)
                {
                    var signIn = new SignInWindow(_plugin) { Owner = Window.GetWindow(this) };
                    signIn.ShowDialog();
                }
                if (_plugin.AuthIsSignedIn) LinkDiscord_Click(null, null);
            };
            MotdStripHost.Content = _motdStrip;

            ApplyDevModeVisibility();

            // Header version readout. Read once at construction; doesn't change
            // at runtime within a session. ToString(3) drops the build/revision
            // components so users see "0.1.0" not "0.1.0.0".
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            HeaderVersionText.Text = asmVersion != null ? "v" + asmVersion.ToString(3) : "";

            // Diagnostics expander (collapsed by default). Verbose status.
            WheelText.Text  = plugin.WheelStatus;
            StreamText.Text = plugin.StreamStatus;
            FfbTapText.Text = plugin.FfbTapStatus;
            VoicesText.Text = plugin.ActiveVoiceCount.ToString();

            RefreshFromPlugin();
            UpdateStatusPill();

            // 60 Hz meter updates (matches WPF compositor) with exponential
            // interpolation = visibly smoother than 30 Hz + abrupt width snaps.
            _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _meterTimer.Tick += MeterTimer_Tick;

            // Activity signal for the sync coordinator: any interaction with the panel marks the user
            // "active", which is one of the two gates (the other is "a peer is online") that decide
            // whether the cloud pull runs. Tunneling Preview handlers on the root catch every click,
            // keypress, and scroll in any descendant, so no per-control wiring is needed. Idle past the
            // window => the pull goes dormant; the first interaction after that is a WAKE (catch-up pull).
            void MarkActive(object s, EventArgs e) { try { _plugin?.NoteUserActivity(); } catch { } }
            PreviewMouseDown  += MarkActive;
            PreviewKeyDown    += MarkActive;
            PreviewMouseWheel += MarkActive;
            Loaded   += (_, __) =>
            {
                _meterTimer.Start();
                // First-time welcome modal: shown once per (install,
                // user) on first open of a build that introduced the
                // community features. Defer via Dispatcher so the panel
                // is fully composited before the modal pops on top. The
                // latch lives inside MaybeShowNetworkedWelcome itself,
                // set only after the show-gates pass - so a pre-game
                // Loaded that returns early (e.g., backend not yet
                // configured) leaves the latch clear, and the later
                // active-game car-change path can still re-trigger it.
                Dispatcher.BeginInvoke(new Action(MaybeShowNetworkedWelcome),
                    System.Windows.Threading.DispatcherPriority.Background);
                // After the welcome modal (if any), check for community
                // preset updates against the user's downloaded list. Same
                // Dispatcher.Background slot so this never blocks panel
                // composition; the modal pops only if updates exist.
                Dispatcher.BeginInvoke(new Action(MaybeShowCommunityUpdates),
                    System.Windows.Threading.DispatcherPriority.Background);
                if (_plugin != null)
                {
                    // Opening the panel counts as activity: it wakes the sync coordinator (presence
                    // probe + catch-up pull if a peer is online) so a change from another device lands
                    // when you come to the UI, not only after the next interaction.
                    try { _plugin.NoteUserActivity(); } catch { }
                    _plugin.AutoRatchetBumped += OnAutoRatchetBumped;
                    _plugin.MasterGainChangedExternally += OnMasterGainChangedExternally;
                    // Dash remote edits mutate the same ActiveXxx POCOs the
                    // sliders bind; re-pull so an open panel tracks them.
                    _plugin.DashRemoteChanged += OnDashRemoteChanged;
                    // Auth identity change (sign-in, sign-out, refresh
                    // that flipped to a different email) must reset
                    // the per-session latches so the next user's
                    // welcome modal / community-update check don't get
                    // silently suppressed by the prior user's gate.
                    _plugin.AuthIdentityChanged += OnAuthIdentityChanged;
                    // Community toggle from any surface (this checkbox or the share
                    // funnel) runs the same follow-up via OnCommunityEnabledChanged.
                    _plugin.CommunityEnabledChanged += OnCommunityEnabledChanged;
                    // A cloud sync (pull/merge/restore) applies new values into Settings; refresh
                    // the visible sliders so a change synced from another PC shows live, not only
                    // after navigating away and back.
                    _plugin.LibraryReloaded += OnLibraryReloadedRefreshUi;
                }
                // SimHub caches this control, so navigating away and back does NOT
                // rebuild it. Re-pull values on every (re)load so edits made
                // elsewhere while we were hidden (e.g. the home-screen Feedback
                // gain tile) are reflected instead of showing stale slider values.
                RefreshFromPlugin();
                _ = RefreshAchievementsAndNotifyAsync();
            };
            Unloaded += (_, __) =>
            {
                _meterTimer.Stop();
                try { DismissToast(); } catch { }
                try { _discordLinkCts?.Cancel(); } catch { }
                if (_plugin != null)
                {
                    _plugin.AuthIdentityChanged -= OnAuthIdentityChanged;
                    _plugin.CommunityEnabledChanged -= OnCommunityEnabledChanged;
                    _plugin.AutoRatchetBumped -= OnAutoRatchetBumped;
                    _plugin.MasterGainChangedExternally -= OnMasterGainChangedExternally;
                    _plugin.DashRemoteChanged -= OnDashRemoteChanged;
                    _plugin.LibraryReloaded -= OnLibraryReloadedRefreshUi;
                }
            };
        }

        // (Removed unknown-variant prompt plumbing. The "register new
        // variant?" modal was replaced by silent auto-create in
        // TrueforcePlugin.EnsureVariantForLiveSignature: variant identity
        // IS the live telemetry signature, so there's nothing to ask the
        // user about. The Manage Variants UI still lets the user rename
        // or delete rows after the fact.)

        // The active local user changed (sign-in, sign-out, refresh
        // that flipped to a different email). Reset every per-session
        // latch + last-shown marker that would otherwise carry the
        // prior user's state into the next user's experience. The slot
        // manager in TrueforcePlugin has already mounted the new
        // user's DownloadedCommunityPresets + SharingAuthor before we
        // get here, so an immediate re-fire of MaybeShowCommunityUpdates
        // would scope to the right data set.
        private void OnAuthIdentityChanged(string newSlotKey)
        {
            _welcomeTriggeredThisSession        = false;
            WelcomeWindow.ShownThisSession      = false;
            _communityUpdatesCheckedThisSession = false;
            // Forget the last-shown car/game so RefreshFromPlugin's
            // per-change-driven nudges (welcome trigger, refresh) run
            // again for the new user instead of being suppressed.
            _lastShownCarId = null;
            _lastShownGame  = null;
            // Drop the active-card 'Top community presets' in-memory cache: its
            // PresetSummary rows carry per-user MyVote / OwnerUserId (Edit/Delete
            // affordance), which must not bleed across identities. (The on-disk
            // browse cache is already per-slot; this is its in-memory sibling.)
            _lastResolvedCarKey    = null;
            _cachedTopForActiveCar = null;
            // Invalidate any in-flight account stats / Discord-row fetch from the prior user,
            // and cancel any in-flight Discord link so it can't strand the new user's panel.
            unchecked { ++_accountStatsGen; ++_discordRowGen; }
            try { _discordLinkCts?.Cancel(); } catch { }
            // Refresh visible UI state for the new identity. This event can arrive on a
            // thread-pool thread (the auth client awaits with ConfigureAwait(false)), so marshal
            // all UI work to the dispatcher. The achievement baseline is account-keyed, so no
            // destructive reset is needed here.
            Action ui = () =>
            {
                try { RefreshAccountRow(); } catch { }
                try { RefreshFromPlugin(); } catch { }
                _ = RefreshAchievementsAndNotifyAsync();
            };
            if (Dispatcher.CheckAccess()) ui();
            else Dispatcher.BeginInvoke(ui);
        }

        // A bound controller button (Controls tab) nudged master gain while the
        // panel is open. Mirror the new value into the slider without re-firing
        // its ValueChanged (the plugin already applied + persisted it), then
        // surface the unsaved-preset state the way a manual drag would.
        // A cloud restore/sync reloaded settings into the plugin; re-pull every visible control so
        // a change synced from another device reflects on screen immediately (not just after
        // navigating away and back). Marshalled to the UI thread; RefreshFromPlugin suppresses
        // change events so this can't fire a spurious edit / re-sync.
        private void OnLibraryReloadedRefreshUi()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnLibraryReloadedRefreshUi)); return; }
            try { RefreshFromPlugin(); } catch { }
        }

        private void OnMasterGainChangedExternally()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_plugin == null) return;
                _suppressEvents = true;
                try
                {
                    MasterGainSlider.Value = _plugin.Settings?.MasterGain ?? MasterGainSlider.Value;
                    MasterGainText.Text = MasterGainSlider.Value.ToString("F2");
                }
                finally { _suppressEvents = false; }
                MarkEffectDirty(EffectKind.Master);
            }));
        }

        // A dash-remote action changed effect / audio / car-fact state; re-pull
        // everything (dash edits are already persisted plugin-side, so unlike
        // the master-gain path there is no per-control dirty bookkeeping to do;
        // RefreshFromPlugin suppresses change events while it loads values).
        private void OnDashRemoteChanged()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnDashRemoteChanged)); return; }
            try { RefreshFromPlugin(); } catch { }
        }

        /// <summary>Pull all visible UI values from the plugin's effective settings.</summary>
        public void RefreshFromPlugin()
        {
            if (_plugin == null) return;
            _suppressEvents = true;
            try
            {
                PluginEnabledCheck.IsChecked = _plugin.PluginEnabled;
                string game = _plugin.ActiveGame;
                // Hint moved off the card into the checkbox tooltip to reclaim a row.
                if (PluginEnabledCheck != null)
                    PluginEnabledCheck.ToolTip = string.IsNullOrEmpty(game)
                        ? "Choice is auto-remembered per game. Disable for games with native Trueforce (e.g. iRacing) so this plugin yields the wheel."
                        : $"Auto-remembered for '{game}'. Disable for games with native Trueforce (e.g. iRacing) so this plugin yields the wheel.";

                if (AuthorNameBox != null)
                    AuthorNameBox.Text = _plugin.Settings?.SharingAuthor ?? "";
                if (AccountAuthorBox != null)
                    AccountAuthorBox.Text = _plugin.Settings?.SharingAuthor ?? "";

                MasterGainSlider.Value = _plugin.Settings?.MasterGain ?? 1.0;
                MasterGainText.Text    = MasterGainSlider.Value.ToString("F2");
                MasterGainStepSlider.Value = _plugin.MasterGainStep;
                MasterGainStepText.Text    = _plugin.MasterGainStep.ToString("F2");
                if (ShowFeedbackBoxCheck != null)
                    ShowFeedbackBoxCheck.IsChecked = _plugin.Settings?.ShowFeedbackBox == true;
                if (ShowPerGearRedlineEditorCheck != null)
                    ShowPerGearRedlineEditorCheck.IsChecked = _plugin.Settings?.ShowPerGearRedlineEditor == true;
                if (CommunityEnabledCheck != null)
                    CommunityEnabledCheck.IsChecked = _plugin.Settings?.CommunityEnabled == true;
                if (EffectsTabShareButtonsCheck != null)
                    EffectsTabShareButtonsCheck.IsChecked = _plugin.Settings?.ShowEffectsTabShareButtons ?? true;
                if (UseCommunityCarFactsCheck != null)
                    UseCommunityCarFactsCheck.IsChecked = _plugin.Settings?.UseCommunityCarFacts == true;
                var motdLevel = _plugin.Settings?.MotdLevel ?? MotdLevel.All;
                if (MotdLevelAllRadio != null)       MotdLevelAllRadio.IsChecked       = motdLevel == MotdLevel.All;
                if (MotdLevelImportantRadio != null) MotdLevelImportantRadio.IsChecked = motdLevel == MotdLevel.Important;
                if (MotdLevelNoneRadio != null)      MotdLevelNoneRadio.IsChecked      = motdLevel == MotdLevel.None;
                if (MotdNoneWarning != null)
                    MotdNoneWarning.Visibility = motdLevel == MotdLevel.None ? Visibility.Visible : Visibility.Collapsed;
                if (AutoUpdateDownloadedPresetsCheck != null)
                    AutoUpdateDownloadedPresetsCheck.IsChecked = _plugin.Settings?.AutoUpdateDownloadedPresets == true;
                if (UpdateCheckIntervalCombo != null)
                    SelectComboByTag(UpdateCheckIntervalCombo,
                        (_plugin.Settings?.UpdateCheckIntervalHours ?? 2).ToString());
                if (BetaUpdatesCheck != null)
                {
                    BetaUpdatesCheck.IsChecked = _plugin.Settings?.BetaUpdatesEnabled == true;
                    RefreshBetaUpdateNote();
                }
                if (RemoteRevLtrRadio != null && RemoteRevOutsideInRadio != null)
                {
                    bool outsideIn = _plugin.Settings?.DashRevStripOutsideIn == true;
                    RemoteRevOutsideInRadio.IsChecked = outsideIn;
                    RemoteRevLtrRadio.IsChecked = !outsideIn;
                }
                if (RemoteDashRememberTabCheck != null)
                    RemoteDashRememberTabCheck.IsChecked = _plugin.Settings?.DashRememberLastTab != false;
                if (RemoteDashDefaultTabCombo != null)
                    RemoteDashDefaultTabCombo.IsEnabled = _plugin.Settings?.DashRememberLastTab == false;
                // Populates the default-tab combo too (enabled tabs only, in
                // the user's order) and re-selects the stored default.
                RebuildRemoteDashTabsEditor();
                RefreshRemoteDashDriveEditor();
                if (AutoSubmitCarFactsCheck != null)
                    AutoSubmitCarFactsCheck.IsChecked = _plugin.Settings?.AutoSubmitCarFacts == true;
                if (AutoSyncBackupCheck != null)
                    AutoSyncBackupCheck.IsChecked = _plugin.Settings?.AutoSyncBackupEnabled == true;
                if (CrossWheelFfbModeCombo != null)
                    SelectComboByTag(CrossWheelFfbModeCombo,
                        (_plugin.Settings?.CrossWheelFfbMode ?? CrossWheelFfbMode.Ask).ToString());
                RefreshCrossWheelFfbNotice();
                RefreshCommunityAuthRow();

                FfbScaleSlider.Value   = _plugin.Settings?.FfbScale ?? 1.0;
                FfbScaleText.Text      = FfbScaleSlider.Value.ToString("F2");
                StationarySpringCheck.IsChecked = _plugin.Settings?.StationarySpringEnabled ?? false;
                StationarySpringStrengthSlider.Value = _plugin.Settings?.StationarySpringStrength ?? 1.00;
                StationarySpringStrengthText.Text    = StationarySpringStrengthSlider.Value.ToString("F2");
                StationarySpringCutoffSlider.Value   = _plugin.Settings?.StationarySpringCutoffKmh ?? 12.0;
                StationarySpringCutoffText.Text      = ((int)StationarySpringCutoffSlider.Value).ToString();
                // Strength / fade-out sliders only matter when the spring is on.
                if (StationarySpringSliders != null)
                    StationarySpringSliders.Visibility =
                        (StationarySpringCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
                if (LogUsbBytesCheck != null)
                    LogUsbBytesCheck.IsChecked = _plugin.Settings?.LogUsbBytesEnabled ?? false;
                bool expFfb = _plugin.Settings?.ExperimentalFfbCapture ?? false;
                if (ExperimentalFfbCheck != null)     ExperimentalFfbCheck.IsChecked     = expFfb;

                // Driver testing mode checkbox: hidden until the DRIVER access
                // code has been entered (DriverTestingUnlocked persists the
                // revealed state); mirrors the MANUALPIN reveal restore.
                {
                    bool driverUnlocked = _plugin.Settings?.DriverTestingUnlocked == true;
                    var driverVis = driverUnlocked
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    if (DriverInterceptCheck != null)
                    {
                        DriverInterceptCheck.Visibility = driverVis;
                        DriverInterceptCheck.IsChecked  = _plugin.Settings?.ExperimentalDriverIntercept == true;
                    }
                    if (DriverInterceptHelp != null) DriverInterceptHelp.Visibility = driverVis;
                }
                FfbSmoothSlider.Value  = _plugin.Settings?.FfbSmoothTimeConstantMs ?? 0.0;
                FfbSmoothText.Text     = FfbSmoothSlider.Value.ToString("F1");
                if (FfbInvertCheck != null)
                    FfbInvertCheck.IsChecked = _plugin.Settings?.FfbInvertSign ?? true;
                SpikeTamingEnabledCheck.IsChecked  = _plugin.Settings?.FfbSpikeTamingEnabled  ?? false;
                if (StopStreamOnPauseCheck != null)
                    StopStreamOnPauseCheck.IsChecked = _plugin.Settings?.StopStreamOnPause ?? false;
                if (SpringTerrainCheck != null)
                    SpringTerrainCheck.IsChecked = _plugin.Settings?.SpringModeTerrainEnabled ?? false;
                if (SpringTerrainStrengthSlider != null)
                {
                    SpringTerrainStrengthSlider.Value = _plugin.Settings?.SpringModeTerrainGain ?? 1.0;
                    if (SpringTerrainStrengthText != null)
                        SpringTerrainStrengthText.Text = SpringTerrainStrengthSlider.Value.ToString("F2");
                }
                if (SpringCenterGainSlider != null)
                {
                    SpringCenterGainSlider.Value = _plugin.Settings?.SpringModeCenterGain ?? 1.0;
                    if (SpringCenterGainText != null)
                        SpringCenterGainText.Text = SpringCenterGainSlider.Value.ToString("F2");
                }
                if (SpringCenterFirmSlider != null)
                {
                    SpringCenterFirmSlider.Value = _plugin.Settings?.SpringModeCenterFirmness ?? 0.5;
                    if (SpringCenterFirmText != null)
                        SpringCenterFirmText.Text = SpringCenterFirmSlider.Value.ToString("F2");
                }
                if (SpringSpeedFxSlider != null)
                {
                    SpringSpeedFxSlider.Value = _plugin.Settings?.SpringModeSpeedEffect ?? 0.65;
                    if (SpringSpeedFxText != null)
                        SpringSpeedFxText.Text = SpringSpeedFxSlider.Value.ToString("F2");
                }
                if (SpringDragCheck != null)
                    SpringDragCheck.IsChecked = _plugin.Settings?.SpringModeDragEnabled ?? false;
                if (SpringDragGainSlider != null)
                {
                    SpringDragGainSlider.Value = _plugin.Settings?.SpringModeDragGain ?? 1.0;
                    if (SpringDragGainText != null)
                        SpringDragGainText.Text = SpringDragGainSlider.Value.ToString("F2");
                }
                if (SpringDragStrainSlider != null)
                {
                    SpringDragStrainSlider.Value = _plugin.Settings?.SpringModeDragStrainFraction ?? 0.35;
                    if (SpringDragStrainText != null)
                        SpringDragStrainText.Text = SpringDragStrainSlider.Value.ToString("F2");
                }
                if (SpringStrengthSlider != null)
                {
                    SpringStrengthSlider.Value = _plugin.Settings?.SpringModeStrength ?? 1.0;
                    if (SpringStrengthText != null)
                        SpringStrengthText.Text = SpringStrengthSlider.Value.ToString("F2");
                }
                UpdateIRacingClipWarning();
                // Same targeting rule as the writes: show the number the force
                // path is actually using for the active car. UpdateIRacingMaxNmText
                // fills the box itself, so there is nothing to seed here.
                UpdateIRacingMaxNmText();
                if (IRacingGainSlider != null)
                {
                    IRacingGainSlider.Value = _plugin.Settings?.IRacingForceGain ?? 1.0;
                    if (IRacingGainText != null)
                        IRacingGainText.Text = IRacingGainSlider.Value.ToString("F2");
                }
                if (IRacingSmoothCheck != null)
                    IRacingSmoothCheck.IsChecked = _plugin.Settings?.IRacingUse360Hz == true;
                if (IRacingForceModeCombo != null)
                {
                    int m = _plugin.Settings?.IRacingForceMode ?? 0;
                    IRacingForceModeCombo.SelectedIndex = (m == 1) ? 1 : 0;
                }
                if (IRacingPredictSlider != null)
                {
                    IRacingPredictSlider.Value = _plugin.Settings?.IRacingPredictGain ?? 1.0;
                    if (IRacingPredictText != null)
                        IRacingPredictText.Text = IRacingPredictSlider.Value.ToString("F2");
                }
                UpdateIRacingPredictVisibility();
                if (SpringMinForceSlider != null)
                {
                    SpringMinForceSlider.Value = _plugin.Settings?.SpringModeMinForce ?? 0.05;
                    if (SpringMinForceText != null)
                        SpringMinForceText.Text = SpringMinForceSlider.Value.ToString("F2");
                }
                // ActiveImplementThud, not Settings.ImplementThud: the section
                // is global-only, but legacy data could carry a car override
                // and the apply path reads override-first; the UI must show
                // what plays.
                var implThud = _plugin.ActiveImplementThud;
                if (ImplementThudCheck != null)
                    ImplementThudCheck.IsChecked = implThud?.Enabled ?? true;
                if (ImplementThudGainSlider != null)
                {
                    ImplementThudGainSlider.Value = implThud?.Gain ?? 1.0f;
                    if (ImplementThudGainText != null)
                        ImplementThudGainText.Text = ImplementThudGainSlider.Value.ToString("F2");
                }
                if (ImplementThudFreqSlider != null)
                {
                    ImplementThudFreqSlider.Value = implThud?.Freq ?? 30.0f;
                    if (ImplementThudFreqText != null)
                        ImplementThudFreqText.Text = ((int)ImplementThudFreqSlider.Value).ToString();
                }
                if (ImplementThudRaiseSlider != null)
                {
                    ImplementThudRaiseSlider.Value = implThud?.RaiseAmp ?? 0.6f;
                    if (ImplementThudRaiseText != null)
                        ImplementThudRaiseText.Text = ImplementThudRaiseSlider.Value.ToString("F2");
                }
                if (ImplementThudHumSlider != null)
                {
                    ImplementThudHumSlider.Value = implThud?.HumAmp ?? 0.30f;
                    if (ImplementThudHumText != null)
                        ImplementThudHumText.Text = ImplementThudHumSlider.Value.ToString("F2");
                }
                if (ImplementThudHumFreqSlider != null)
                {
                    ImplementThudHumFreqSlider.Value = implThud?.HumFreq ?? 46.0f;
                    if (ImplementThudHumFreqText != null)
                        ImplementThudHumFreqText.Text = ((int)ImplementThudHumFreqSlider.Value).ToString();
                }
                if (ImplementThudBendSlider != null)
                {
                    ImplementThudBendSlider.Value = implThud?.BendDepth ?? 0.15f;
                    if (ImplementThudBendText != null)
                        ImplementThudBendText.Text = ImplementThudBendSlider.Value.ToString("F2");
                }
                if (ImplementThudSpeedPitchSlider != null)
                {
                    ImplementThudSpeedPitchSlider.Value = implThud?.SpeedPitch ?? 0.12f;
                    if (ImplementThudSpeedPitchText != null)
                        ImplementThudSpeedPitchText.Text = ImplementThudSpeedPitchSlider.Value.ToString("F2");
                }
                if (ImplementThudSpeedVolSlider != null)
                {
                    ImplementThudSpeedVolSlider.Value = implThud?.SpeedVolume ?? 0.5f;
                    if (ImplementThudSpeedVolText != null)
                        ImplementThudSpeedVolText.Text = ImplementThudSpeedVolSlider.Value.ToString("F2");
                }
                if (ImplementThudHarmonicSlider != null)
                {
                    ImplementThudHarmonicSlider.Value = implThud?.HarmonicAmp ?? 0.22f;
                    if (ImplementThudHarmonicText != null)
                        ImplementThudHarmonicText.Text = ImplementThudHarmonicSlider.Value.ToString("F2");
                }
                if (ImplementThudWaveformCombo != null)
                {
                    string wname = (implThud?.Waveform
                                    ?? TrueforceForAll.Core.Waveform.Sine).ToString();
                    foreach (System.Windows.Controls.ComboBoxItem it in ImplementThudWaveformCombo.Items)
                        if ((it.Content as string) == wname) { ImplementThudWaveformCombo.SelectedItem = it; break; }
                }
                if (SpringWeightCheck != null)
                    SpringWeightCheck.IsChecked = _plugin.Settings?.SpringModeChassisWeightEnabled ?? false;
                if (SpringWeightGainSlider != null)
                {
                    SpringWeightGainSlider.Value = _plugin.Settings?.SpringModeChassisWeightGain ?? 1.0;
                    if (SpringWeightGainText != null)
                        SpringWeightGainText.Text = SpringWeightGainSlider.Value.ToString("F2");
                }
                bool spikeSlewMode = _plugin.Settings?.FfbSpikeUseSlewLimiter ?? true;
                SpikeModeSlewRadio.IsChecked      = spikeSlewMode;
                SpikeModeTransientRadio.IsChecked = !spikeSlewMode;
                UpdateSpikeModeUi();
                FfbSpikeLimitSlider.Value = _plugin.Settings?.FfbSpikeMaxLsbPerMs ?? 0.0;
                FfbSpikeLimitText.Text    = FfbSpikeLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbSpikeLimitSlider.Value).ToString();
                FfbPeakLimitSlider.Value  = _plugin.Settings?.FfbPeakSoftLimitLsb ?? 0.0;
                FfbPeakLimitText.Text     = FfbPeakLimitSlider.Value <= 0
                    ? "off"
                    : ((int)FfbPeakLimitSlider.Value).ToString();

                // Telemetry based FFB (Mode B) tab: global settings, applied
                // live via ApplyModeBFromSettings / ApplyModeBFeel.
                var mbs = _plugin.Settings;
                if (mbs != null && ModeBEnabledCheck != null)
                {
                    // Per-game opt-in: the checkbox reflects and toggles the
                    // ACTIVE game, and is disabled when the active game has no
                    // Mode B support (or none is running).
                    string mbGame = _plugin.ActiveGame;
                    bool mbSupported = _plugin.ActiveGameSupportsModeB;
                    // Spring-mode game (Farming Simulator): the force is the
                    // game's own spring, so the Forza tuning recipe and the
                    // per-game Enable are irrelevant and hide as a block. The
                    // spring toggle, rev lights, and the wheel screen stay:
                    // they work in spring mode. The "not available here" badge
                    // also stays hidden; it would tell the user to go start a
                    // Forza while the tab IS relevant to this game.
                    bool springGame = !string.IsNullOrEmpty(mbGame)
                        && mbGame.StartsWith("FarmingSimulator", StringComparison.Ordinal);
                    // iRacing RESHAPES the sim's own steering torque instead of
                    // synthesizing force from slip, so the entire Forza recipe
                    // below (SAT gain, peak utilisation, drop floor, grip
                    // learner, lockup gate, reversal softening, phase lead,
                    // centering) feeds a model that never runs there. Roughly
                    // thirty controls that cannot do anything, and a dead knob
                    // reads as broken. Same rule the spring games already use,
                    // extended from a two-way split to a three-way one.
                    bool reshapeGame = _plugin.ActiveGameIsReshapeGame;
                    // Neither synthesis-only panel applies to spring OR reshape.
                    bool hideForzaRecipe = springGame || reshapeGame;
                    if (ModeBForzaTuningPanel != null)
                        ModeBForzaTuningPanel.Visibility = hideForzaRecipe
                            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (ModeBForzaTuningPanel2 != null)
                        ModeBForzaTuningPanel2.Visibility = hideForzaRecipe
                            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    // The iRacing controls, which are the only force tunables
                    // that DO anything there. Damping is deliberately not in
                    // here: it sits between the two Forza panels precisely so it
                    // survives them being hidden, and the velocity damper runs
                    // on all three pipelines.
                    if (IRacingTuningPanel != null)
                        IRacingTuningPanel.Visibility = reshapeGame
                            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

                    // The tapped-path corrections are switched off under the
                    // iRacing reshape (the device skips them), so the two
                    // controls that drive them are genuinely dead there and
                    // hide. Smoothing stays: it is applied further down the
                    // device chain and still shapes the force.
                    //
                    // The whole group is also renamed, because "pass-through" is
                    // the wrong word for a mode that passes nothing through: it
                    // reshapes the sim's force and authors what reaches the wheel.
                    var deadInReshape = reshapeGame
                        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (FfbScaleRow    != null) FfbScaleRow.Visibility    = deadInReshape;
                    if (FfbInvertCheck != null) FfbInvertCheck.Visibility = deadInReshape;
                    // The stationary spring is hard-skipped for iRacing in
                    // ApplyStationarySpring, and now that this group shares the
                    // tab it would be a dead section sitting next to live ones.
                    // iRacing's own torque already weights a parked car, which is
                    // the whole job the spring exists to do elsewhere.
                    if (StationarySpringExpander != null)
                        StationarySpringExpander.Visibility = deadInReshape;
                    if (FfbPassthroughHeader != null)
                        FfbPassthroughHeader.Text = reshapeGame ? "Wheel output" : "FFB pass-through";

                    // Tab name. "Telemetry FFB" is wrong for iRacing, where the
                    // force is not built FROM telemetry but is the sim's own
                    // force reshaped. Plain "FFB" was wrong too, just
                    // differently: this tab also owns the rev lights and the
                    // wheel screen, so naming it after one of the three
                    // undersells the other two.
                    //
                    // The parenthesised list follows the HARDWARE. Only the
                    // G PRO, RS50 and the G923 Xbox have a base screen, and
                    // advertising OLED to someone whose wheel has none is a
                    // promise the tab cannot keep: they would open it looking
                    // for a section that is (correctly) hidden. Same source of
                    // truth the OLED section itself uses, so the two can never
                    // disagree.
                    if (TelemetryFfbTab != null)
                    {
                        TelemetryFfbTab.Header = reshapeGame
                            ? (_plugin.WheelHasOledScreen ? "Wheel (FFB, LED, OLED)" : "Wheel (FFB, LED)")
                            : "Telemetry FFB";
                    }
                    // Spring-mode enhancements show ONLY in spring games; in
                    // Forza the composer has its own kick layer and these
                    // controls would be dead weight there.
                    if (SpringTerrainPanel != null)
                        SpringTerrainPanel.Visibility = springGame
                            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    // The FS essentials block (Strength above Damping) shows
                    // and hides together with the enhancements panel.
                    if (SpringEssentialsPanel != null)
                        SpringEssentialsPanel.Visibility = springGame
                            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    // Implement thud on the Effects tab: FS-only for the same
                    // dead-knob reason as Surface texture being Forza-only.
                    if (ImplementThudExpander != null)
                        ImplementThudExpander.Visibility = springGame
                            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    // The Road bumps "Surface texture (Forza)" section only
                    // shows while a Forza title is active: no other source
                    // supplies SurfaceRumble, and dead knobs read as broken.
                    if (BumpsSurfacePanel != null)
                    {
                        bool forzaGame = mbGame == "FM8"
                            || (mbGame != null && mbGame.StartsWith("FH", StringComparison.Ordinal));
                        BumpsSurfacePanel.Visibility = forzaGame
                            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    }
                    // Per-game effect availability (owner rule 2026-08-08,
                    // extending the Surface-texture precedent): an effect
                    // whose telemetry a game never provides is a dead knob
                    // there, and dead knobs read as broken, so the whole
                    // section hides. No active game = show everything.
                    // FS: no ABS/pits/DRS; axle slip, collision and airborne
                    // ARE driven there, and the per-call notes below cover
                    // the rest of the traction family plus the kerb fold.
                    // Forza: its telemetry carries no ABS flag.
                    {
                        bool fzGame = mbGame == "FM8"
                            || (mbGame != null && mbGame.StartsWith("FH", StringComparison.Ordinal));
                        void ShowEffect(System.Windows.Controls.Expander exp, bool supported)
                        {
                            if (exp != null)
                                exp.Visibility = supported
                                    ? System.Windows.Visibility.Visible
                                    : System.Windows.Visibility.Collapsed;
                        }
                        ShowEffect(AbsExpander,          !springGame && !fzGame);
                        // Horizon has no pits and no DRS either (owner call).
                        ShowEffect(PitLimiterExpander,   !springGame && !fzGame);
                        ShowEffect(DrsExpander,          !springGame && !fzGame);
                        // Collision (100 Hz speed-delta surge) and Axle slip
                        // (per-axle rollups) are driven in FS and stay
                        // visible. Traction loss hides there: Axle slip is
                        // FS's one slip voice (owner call); the effect
                        // itself is hard-gated off for FS in
                        // ApplyTractionSettings. Lockup judder hides too
                        // (owner call 2026-08-08): the signed quads flow and
                        // the effect renders (Test-button verified), but
                        // FS's brake model likely never lets a wheel rotate
                        // slower than the road, so the trigger is presumed
                        // unreachable; the quads stay for Axle slip's
                        // braking gate and the rev-locked pulse.
                        ShowEffect(TractionExpander,     !springGame);
                        // iRacing: Lockup judder is not merely unfed, it is not
                        // FEEDABLE. It needs a signed slip ratio and per-wheel
                        // rotation speed, and iRacing publishes neither, so there
                        // is no honest source and no prospect of one. Hidden
                        // rather than shown doing nothing.
                        //
                        // Traction loss STAYS: it falls back to a heuristic on
                        // RPM, throttle, speed, yaw rate and lateral g, all of
                        // which iRacing provides, so the voice really does work
                        // there. Only its TC-intervention confidence boost is
                        // dead, since iRacing publishes the driver's TC SETTING
                        // (TractionControlSetting/Switch) but never a flag saying
                        // it is currently cutting in.
                        ShowEffect(LockupJudderExpander, !springGame && !reshapeGame);
                        // Axle slip needs front/rear grip rollups, which need
                        // per-tire slip. iRacing has none, so this would have to
                        // be a bicycle-model ESTIMATE rather than a measurement.
                        // Hidden until that exists, if it ever does.
                        ShowEffect(AxleSlipExpander,     !reshapeGame);
                        // Airborne IS driven in FS (all-wheels-off from the
                        // mod's per-wheel contact flags), so it stays
                        // visible everywhere.
                        // FS naming fold (owner call 2026-08-08): no kerbs
                        // exist in FS, so the standalone Kerb thump section
                        // hides there and its voice becomes the "Leading
                        // edge" slider inside this section, which renames to
                        // "Terrain texture" (the haptic sibling of the FFB
                        // tab's Terrain feel). Forza keeps the shipped
                        // names; the KerbThump settings identity is shared,
                        // so presets carry one block either way.
                        ShowEffect(KerbThumpExpander,    !springGame);
                        if (SlipEnabledCheck != null)
                            SlipEnabledCheck.Content = springGame ? "Terrain texture" : "Road bumps & curbs";
                        if (BumpsLeadingEdgePanel != null)
                            BumpsLeadingEdgePanel.Visibility = springGame
                                ? System.Windows.Visibility.Visible
                                : System.Windows.Visibility.Collapsed;
                    }
                    // Standing enhanced-telemetry banner, three states (the FS
                    // analogue of Forza's using-SimHub-fallback notice): mod
                    // missing = the install offer with its button; mod
                    // installed but the game not running it = enable-in-game
                    // instruction, no button; enhanced feeding = hidden.
                    // _fsModBannerHold keeps the post-install success text up
                    // instead of blinking away on the next refresh tick; it
                    // yields the moment the enhanced feed arrives, and it is
                    // FS-scoped: leaving the game clears it and resets the
                    // banner, so switching to Forza can never strand an FS
                    // banner on that layout.
                    if (FsModBanner != null)
                    {
                        if (!springGame)
                        {
                            _fsModBannerHold = false;
                            FsModBanner.Visibility = System.Windows.Visibility.Collapsed;
                            if (FsModInstallButton != null)
                                FsModInstallButton.Visibility = System.Windows.Visibility.Visible;
                            if (FsModBannerText != null)
                                FsModBannerText.Text = "Install the TF4ALL Enhanced Telemetry mod for enhanced force feedback in Farming Simulator.";
                        }
                        else
                        {
                            int fsState = _plugin.FsEnhancedTelemetryState();
                            if (fsState == 0)
                            {
                                _fsModBannerHold = false;
                                FsModBanner.Visibility = System.Windows.Visibility.Collapsed;
                                if (FsModInstallButton != null)
                                    FsModInstallButton.Visibility = System.Windows.Visibility.Visible;
                                if (FsModBannerText != null)
                                    FsModBannerText.Text = "Install the TF4ALL Enhanced Telemetry mod for enhanced force feedback in Farming Simulator.";
                            }
                            else if (_fsModBannerHold)
                            {
                                // Post-install success text stays until the
                                // feed arrives or the game changes.
                            }
                            else if (fsState == 1)
                            {
                                FsModBanner.Visibility = System.Windows.Visibility.Visible;
                                if (FsModInstallButton != null)
                                    FsModInstallButton.Visibility = System.Windows.Visibility.Visible;
                                if (FsModBannerText != null)
                                    FsModBannerText.Text = "Install the TF4ALL Enhanced Telemetry mod for enhanced force feedback in Farming Simulator.";
                            }
                            else if (fsState == 2)
                            {
                                FsModBanner.Visibility = System.Windows.Visibility.Visible;
                                if (FsModInstallButton != null)
                                    FsModInstallButton.Visibility = System.Windows.Visibility.Collapsed;
                                if (FsModBannerText != null)
                                    FsModBannerText.Text = "The TF4ALL Enhanced Telemetry mod is installed but not reaching the plugin. Enable it in the game's mod screen when loading your save, or restart Farming Simulator if SimHub restarted while the game was running.";
                            }
                            else if (fsState == 4)
                            {
                                // Feeding, but from an older mod than the
                                // plugin just refreshed onto disk: the game
                                // only reads its mods folder at launch, so a
                                // refresh always trails by one restart.
                                FsModBanner.Visibility = System.Windows.Visibility.Visible;
                                if (FsModInstallButton != null)
                                    FsModInstallButton.Visibility = System.Windows.Visibility.Collapsed;
                                if (FsModBannerText != null)
                                    FsModBannerText.Text = "The TF4ALL Enhanced Telemetry mod was updated. Restart Farming Simulator to load the new version; until then some newer features stay off.";
                            }
                            else
                            {
                                FsModBanner.Visibility = System.Windows.Visibility.Collapsed;
                            }
                        }
                    }
                    // Contextual intro: each game's player reads only the
                    // sentence that applies to them. FS: the plugin REPLACES
                    // the game's FFB with the synthetic model (2026-08-08
                    // owner call, every wheel), so the game's FFB setting is
                    // feel-neutral while the plugin streams; "leave it on"
                    // survives only as fallback advice for when SimHub isn't
                    // running (owner confirmed the no-difference observation
                    // 2026-08-09).
                    if (ModeBIntroText != null)
                        ModeBIntroText.Text = springGame
                            ? "In Farming Simulator the plugin replaces the game's basic force " +
                              "feedback with its own steering model built from the game's physics, " +
                              "and engages by itself. The game's force feedback setting doesn't " +
                              "change the feel while SimHub runs; leaving it on just keeps native " +
                              "FFB as a fallback when SimHub is closed. Also works in Forza " +
                              "Motorsport (2023) and Forza Horizon 4, 5, and 6."
                            : reshapeGame
                            ? "The plugin takes force feedback over from iRacing rather than inventing " +
                              "its own. iRacing works out what the car's steering is doing; you stop it " +
                              "driving the wheel directly, and the plugin reads those same forces and " +
                              "delivers them over Trueforce instead. The car still feels like the car, " +
                              "and your rev lights and wheel screen work again, because nothing is " +
                              "fighting over the wheel any more. Two switches make the handover: turn " +
                              "iRacing's force feedback OFF in its options (do not just set its strength " +
                              "to 0, the plugin reads that number), and set loadTrueForceAPI=0 in app.ini."
                            : "The wheel's steering force is built from telemetry instead of the game's " +
                              "own FFB. Works in Forza Motorsport (2023) and Forza Horizon 4, 5, and 6. " +
                              "Set the game's force feedback and vibration to 0 so this is the only " +
                              "force on the wheel. Farming Simulator 22 and 25 are supported too, " +
                              "through the spring option below.";
                    ModeBEnabledCheck.Visibility = springGame
                        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    // The controls stay visible in every game so the section can be
                    // seen and pre-tuned without a supported title running. When the
                    // active game can't feed Mode B, a badge at the top of the tab
                    // explains why and the per-game Enable box greys out.
                    ModeBEnabledCheck.IsChecked = _plugin.ModeBEnabledForActiveGame;
                    ModeBEnabledCheck.IsEnabled = mbSupported;
                    // "Telemetry Based FFB" is a promise we do not keep in
                    // iRacing. Everywhere else the force IS built from telemetry:
                    // a slip model invents it and the game contributes nothing.
                    // In iRacing we take the sim's OWN steering force and deliver
                    // it through Trueforce, which is a different thing entirely,
                    // and this is the master switch someone reads first.
                    // "Send iRacing's own force feedback to the wheel" was
                    // accurate and still confusing, because the setup tells the
                    // user to TURN OFF iRacing's force feedback, so the two read
                    // as contradicting each other.
                    //
                    // They do not: what gets switched off is iRacing's own OUTPUT
                    // path to the wheel, and what we send is the steering force
                    // the car is generating, read from telemetry and delivered
                    // over Trueforce. Same physics, different route. Framing it
                    // as a HANDOVER makes the disable step read as part of the
                    // feature instead of an argument with it.
                    ModeBEnabledCheck.Content = reshapeGame
                        ? "Take over force feedback for iRacing"
                        : "Enable Telemetry Based FFB for this game";
                    // Same correction wherever the phrase is user-facing. A
                    // heading and a checkbox that disagree about what the feature
                    // IS are worse than either being wrong alone.
                    if (TeleFfbSectionHeader != null)
                        TeleFfbSectionHeader.Text = reshapeGame
                            ? "Force feedback" : "Telemetry Based FFB";
                    if (WheelLightsNeedNote != null)
                        WheelLightsNeedNote.Text = reshapeGame
                            ? "These need the force feedback above switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; taking the force over ourselves frees them. That is why iRacing needs its own force feedback turned off, and why the lights and screen come back once it is."
                            : "These need Telemetry Based FFB switched on. Writing to them while a game runs its own force feedback makes that force feedback cut out, because they share one channel on the wheel; replacing the game's force feedback frees them. A custom driver that would enable them in every game is in testing, but it needs to be signed by Microsoft first.";
                    if (ModeBUnsupportedBadge != null)
                        ModeBUnsupportedBadge.Visibility = mbSupported || springGame
                            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (!mbSupported && ModeBUnsupportedBadgeText != null)
                        ModeBUnsupportedBadgeText.Text = string.IsNullOrEmpty(mbGame)
                            ? "No supported game is running. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. Start one of those to turn it on. You can still see and pre-tune the controls below."
                            : $"Not available in {ModeBGameDisplayName(mbGame)}. Telemetry Based FFB works in Forza Motorsport (2023), Forza Horizon 4, 5, and 6, and Farming Simulator 22 and 25. It also enables your wheel's rev lights. Start one of those to turn it on.";
                    if (ModeBGameNote != null)
                    {
                        if (springGame)
                        {
                            ModeBGameNote.Visibility = System.Windows.Visibility.Collapsed;
                        }
                        else if (mbSupported)
                        {
                            // iRacing takes the OPPOSITE instruction, and getting
                            // this wrong breaks it silently. The reshape divides
                            // the sim's torque by its own SteeringWheelMaxForceNm,
                            // so a user who sets in-sim force to 0 zeroes our
                            // divisor, trips the maxNm guard and gets no force at
                            // all with nothing on screen to explain why. What it
                            // needs is force feedback DISABLED, with max force
                            // left wherever they like it.
                            string note = reshapeGame
                                ? "Applies to iRacing. Turn iRacing's own force feedback OFF (do not set its strength to 0, the plugin reads that number), and set loadTrueForceAPI=0 in app.ini."
                                : $"Applies to {ModeBGameDisplayName(mbGame)}. Set that game's own force feedback and vibration to 0.";
                            if (mbGame == "FM8")
                                note += " Forza Motorsport has native Trueforce, so also enable the plugin for it at the top of this panel.";
                            ModeBGameNote.Text = note;
                            ModeBGameNote.Visibility = System.Windows.Visibility.Visible;
                        }
                        else
                        {
                            // The top badge carries the "not available here" message, so
                            // hide the per-checkbox note rather than say it twice.
                            ModeBGameNote.Visibility = System.Windows.Visibility.Collapsed;
                        }
                    }
                    ModeBSignCheck.IsChecked    = mbs.ModeBSign < 0f;
                    ModeBStrengthSlider.Value   = mbs.ModeBSatGain;
                    ModeBStrengthText.Text      = mbs.ModeBSatGain.ToString("F2");
                    ModeBAutoStrengthCheck.IsChecked = mbs.ModeBAutoStrength;
                    // Center feel (ModeBDirSoft) is code-only now (BDIRK): Direct
                    // centering + damping own center calm, so the slider left the UI.
                    ModeBDamperSlider.Value     = mbs.ModeBDamper;
                    ModeBDamperText.Text        = mbs.ModeBDamper.ToString("F2");
                    ModeBMinForceSlider.Value   = mbs.ModeBMinForce;
                    ModeBMinForceText.Text      = mbs.ModeBMinForce.ToString("F2");
                    ModeBRecoverSlider.Value    = mbs.ModeBLockupRecoverMs;
                    ModeBRecoverText.Text       = mbs.ModeBLockupRecoverMs.ToString("F0");
                    ModeBCenterSlider.Value     = mbs.ModeBCenter;
                    ModeBCenterText.Text        = mbs.ModeBCenter.ToString("F2");
                    ModeBLatSlider.Value        = mbs.ModeBLatGain;
                    ModeBLatText.Text           = mbs.ModeBLatGain.ToString("F2");
                    ModeBPeakSlider.Value       = mbs.ModeBPeakUtil;
                    ModeBPeakText.Text          = mbs.ModeBPeakUtil.ToString("F2");
                    ModeBFloorSlider.Value      = mbs.ModeBDropFloor;
                    ModeBFloorText.Text         = mbs.ModeBDropFloor.ToString("F2");
                    ModeBRiseSlider.Value       = mbs.ModeBRiseGamma;
                    ModeBRiseText.Text          = mbs.ModeBRiseGamma.ToString("F2");
                    ModeBSmoothSlider.Value     = mbs.ModeBEmaMs;
                    ModeBSmoothText.Text        = mbs.ModeBEmaMs.ToString("F0");
                    ModeBCompressorCheck.IsChecked  = mbs.ModeBCompressor;
                    ModeBSuspLoadCheck.IsChecked    = mbs.ModeBSuspensionLoad;
                    ModeBEarlyPeakCheck.IsChecked   = mbs.ModeBEarlyTorquePeak;
                    ModeBRoadKickCheck.IsChecked    = mbs.ModeBRoadKick;
                    ModeBRoadKickGainSlider.Value   = mbs.ModeBRoadKickGain;
                    ModeBRoadKickGainText.Text      = mbs.ModeBRoadKickGain.ToString("F2");
                    ModeBReversalDampCheck.IsChecked = mbs.ModeBReversalDamp;
                    ModeBReversalGainSlider.Value    = mbs.ModeBReversalDampGain;
                    ModeBReversalGainText.Text       = mbs.ModeBReversalDampGain.ToString("F2");
                    ModeBPhaseLeadCheck.IsChecked    = mbs.ModeBPhaseLead;
                    ModeBPhaseLeadSlider.Value       = mbs.ModeBPhaseLeadMs;
                    ModeBPhaseLeadText.Text          = mbs.ModeBPhaseLeadMs.ToString("F0");
                    // "Adaptive grip & braking feel" master reflects grip auto-cal;
                    // it drives friction-circle + brake-learn together on toggle.
                    ModeBGripCalCheck.IsChecked     = mbs.ModeBGripAutoCal;
                    UpdateModeBGripLimitVisibility();   // hide the grip-limit slider while the learner owns the limit
                    ModeBLateralDemandCheck.IsChecked = mbs.ModeBLateralDemand;
                    // Direct centering is always on (hidden MBCPD failsafe only);
                    // its look-ahead slider lives under Centering.
                    ModeBCenterLeadSlider.Value  = mbs.ModeBCenterLeadMs;
                    ModeBCenterLeadText.Text     = mbs.ModeBCenterLeadMs.ToString("F0");
                }
                if (ModeBContentionWarning != null)
                    ModeBContentionWarning.Visibility = _plugin.ModeBContentionDetected
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                if (ModeBSlipStarvedWarning != null)
                    ModeBSlipStarvedWarning.Visibility = _plugin.ModeBSlipStarvedDetected
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                // Performance section
                var perf = _plugin.Settings?.Performance;
                if (perf != null)
                {
                    PerfAutoRadio.IsChecked   = perf.Mode == PerformanceMode.Auto;
                    PerfManualRadio.IsChecked = perf.Mode == PerformanceMode.Manual;
                    bool manual = perf.Mode == PerformanceMode.Manual;
                    PerfTfRingSlider.IsEnabled    = manual;
                    PerfAudioRingSlider.IsEnabled = manual;
                    PerfTfRingSlider.Value    = perf.TfRingSize;
                    PerfAudioRingSlider.Value = perf.AudioRingSize;
                    PerfTfRingText.Text    = FormatRing(perf.TfRingSize);
                    PerfAudioRingText.Text = FormatRing(perf.AudioRingSize);
                }

                if (DuckingEnabledCheck != null)
                    DuckingEnabledCheck.IsChecked = _plugin.Settings?.DuckingEnabled ?? true;
                DuckDepthSlider.Value   = _plugin.Settings?.DuckDepth ?? 0.5;
                DuckDepthText.Text      = DuckDepthSlider.Value.ToString("F2");
                DuckAttackSlider.Value  = _plugin.Settings?.DuckAttackMs ?? 5.0;
                DuckAttackText.Text     = ((int)DuckAttackSlider.Value).ToString();
                DuckReleaseSlider.Value = _plugin.Settings?.DuckReleaseMs ?? 80.0;
                DuckReleaseText.Text    = ((int)DuckReleaseSlider.Value).ToString();
                if (DuckFrequencyAwareCheck != null)
                    DuckFrequencyAwareCheck.IsChecked = _plugin.Settings?.DuckFrequencyAware ?? false;

                var audio = _plugin.ActiveAudio;
                AudioEnabledCheck.IsChecked      = audio?.Enabled ?? false;
                AudioGainSlider.Value            = audio?.Gain ?? 1.0;
                AudioGainText.Text               = AudioGainSlider.Value.ToString("F2");
                AudioFilterSlider.Value          = audio?.LowpassCutoffHz ?? 350.0;
                AudioFilterText.Text             = ((int)AudioFilterSlider.Value).ToString();
                AudioHighpassSlider.Value        = audio?.HighpassCutoffHz ?? 30.0;
                AudioHighpassText.Text           = ((int)AudioHighpassSlider.Value).ToString();

                RefreshPresetSection();

                CaptureExeOverrideBox.Text = _plugin.ActiveCaptureExeOverride ?? "";

                // Rim rev/shift LEDs (hidden iRacing section + Mode B toggle)
                if (RpmLedStatusText != null)
                    RpmLedStatusText.Text = _plugin.RpmLedStatus;
                if (ModeBRevLightsCheck != null)
                    ModeBRevLightsCheck.IsChecked = _plugin.Settings?.ModeBRevLightsEnabled != false;

                // Wheel-base Dynamic OLED (experimental, default off). The whole
                // section is hidden on a wheel with no screen: a G923 owner has
                // no use for any of it, and an option you cannot use reads as a
                // feature you are missing.
                if (OledSection != null)
                    OledSection.Visibility = _plugin.WheelHasOledScreen
                        ? Visibility.Visible : Visibility.Collapsed;
                if (WheelLightsHeaderText != null)
                    WheelLightsHeaderText.Text = _plugin.WheelHasOledScreen
                        ? "Wheel lights and screen" : "Wheel lights";
                if (ModeBOledCheck != null)
                    ModeBOledCheck.IsChecked = _plugin.Settings?.ModeBOledEnabled == true;
                // The screen picker is filled and selected by Tag in
                // RefreshOledEditor: its offered order is not the enum's order,
                // so a SelectedIndex cast would pick the wrong entry.
                if (OledMphCheck != null)
                    OledMphCheck.IsChecked = _plugin.Settings?.OledUseMph == true;
                if (OledShiftFlashCheck != null)
                    OledShiftFlashCheck.IsChecked = _plugin.Settings?.OledShiftFlash != false;
                if (OledFlashStyleCombo != null)
                    OledFlashStyleCombo.SelectedIndex =
                        (int)(_plugin.Settings?.OledShiftFlashStyle ?? OledFlashStyle.CenteredGear);
                if (OledLapResultCheck != null)
                    OledLapResultCheck.IsChecked = _plugin.Settings?.OledLapResult != false;
                if (OledGreetingCheck != null)
                    OledGreetingCheck.IsChecked = _plugin.Settings?.OledGreetingEnabled != false;
                if (OledGreetingBox != null)
                    OledGreetingBox.Text = _plugin.Settings?.OledGreetingText ?? "HELLO WORLD";
                if (OledStatusText != null)
                    OledStatusText.Text = _plugin.OledStatus;
                RefreshRevLightPicker();
                RefreshOledEditor();

                // Forza section
                var fz = _plugin.Settings?.Forza;
                if (fz != null)
                {
                    ForzaPortBox.Text                  = fz.Port.ToString();
                    ForzaBindBox.Text                  = fz.BindAddress ?? "0.0.0.0";
                    ForzaForwardEnabledCheck.IsChecked = fz.ForwardEnabled;
                    ForzaForwardHostBox.Text           = fz.ForwardHost ?? "127.0.0.1";
                    ForzaForwardPortBox.Text           = fz.ForwardPort > 0 ? fz.ForwardPort.ToString() : "";
                }

                // Header strip context. Prefer the resolver's DisplayName when
                // available so opaque ordinals (Forza "Car_424") render as the
                // actual car name ("1997 Mazda RX-7"). Falls back to carId for
                // games whose carIds are already descriptive (AC) or for cars
                // not in the catalog.
                HeaderGameText.Text = string.IsNullOrEmpty(game) ? "(none)" : game;
                // Fire the iRacing app.ini notice once per transition into iRacing
                // (deferred off the dispatcher so a modal never blocks this refresh).
                string curGameForNotice = _plugin?.ActiveGame;
                if (!string.Equals(_lastGameForIracingNotice, curGameForNotice, StringComparison.Ordinal))
                {
                    _lastGameForIracingNotice = curGameForNotice;
                    if (string.Equals(curGameForNotice, "IRacing", StringComparison.Ordinal))
                        Dispatcher.BeginInvoke(new Action(MaybeShowIracingTrueforceNotice),
                            System.Windows.Threading.DispatcherPriority.Background);
                }
                // The Mode B intro is NOT fired here. Launching inside a
                // capable game (e.g. the FH6 profile) would pop it on SimHub's
                // home screen, stacked over the networked welcome. It now shows
                // only when the user opens the Telemetry Based FFB tab
                // (MainTabs_SelectionChanged), so it is always in context.
                string headerCar =
                    !string.IsNullOrEmpty(_plugin.ActiveCarDisplayName) ? _plugin.ActiveCarDisplayName
                    : !string.IsNullOrEmpty(_plugin.ActiveCarId)        ? _plugin.ActiveCarId
                    : "(none)";
                // Append the community alternative when the user has a
                // local rename that disagrees - so a renamed car still
                // surfaces the canonical community name as context.
                string communityAlt = _plugin.ActiveCarCommunityDisplayName;
                if (!string.IsNullOrEmpty(communityAlt))
                    headerCar += $"   (community: {communityAlt})";
                HeaderCarText.Text  = headerCar;

                bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);

                // Discoverability surface: hide the count chip eagerly, then
                // kick a background fetch on car/game change. The fetch sets
                // visibility + content when results arrive (or stays hidden
                // when count == 0 / community is off / no car).
                MaybeRefreshCarCommunityCountAsync();

                // Engine
                var es = _plugin.ActiveEngine;
                if (es != null)
                {
                    EngineEnabledCheck.IsChecked      = es.Enabled;
                    EngineGainSlider.Value            = es.Gain;
                    EngineGainText.Text               = es.Gain.ToString("F2");
                    EnginePitchSlider.Value           = es.Pitch;
                    EnginePitchText.Text              = es.Pitch.ToString("F2");
                    EngineLowpassSlider.Value         = es.LowpassHz;
                    EngineLowpassText.Text            = ((int)es.LowpassHz).ToString();
                    SelectWaveform(EngineWaveformCombo, es.Waveform);
                    if (EngineElectricModeCombo != null)
                        EngineElectricModeCombo.SelectedIndex =
                            es.ElectricMode == ElectricCarMode.Silent ? 1 : 0;

                    // High-RPM helpers (Load layer + boost)
                    if (EngineLoadLayerCheck != null)
                    {
                        EngineLoadLayerCheck.IsChecked       = es.LoadLayerEnabled;
                        EngineLoadLayerGainSlider.Value      = es.LoadLayerGain;
                        EngineLoadLayerGainText.Text         = es.LoadLayerGain.ToString("F2");
                        EngineHighRpmBoostCheck.IsChecked    = es.HighRpmBoostEnabled;
                        EngineHighRpmBoostSlider.Value       = es.HighRpmBoostAmount;
                        EngineHighRpmBoostText.Text          = es.HighRpmBoostAmount.ToString("F2");
                    }

                    // Car facts engine dropdown is populated dynamically
                    // (built-ins + user-saved customs); selection reflects the
                    // active variant's pin.
                    RebuildEngineLayoutDropdown();
                }
                RefreshCarFactsPanel();
                RebuildPerGearEditors();
                // Bumps
                var bs = _plugin.ActiveBumps;
                if (bs != null)
                {
                    SlipEnabledCheck.IsChecked     = bs.Enabled;
                    SlipGainSlider.Value           = bs.Gain;
                    SlipGainText.Text              = bs.Gain.ToString("F2");
                    SelectWaveform(BumpsWaveformCombo, bs.Waveform);
                    BumpsFreqSlider.Value          = bs.Freq;
                    BumpsFreqText.Text             = ((int)bs.Freq).ToString();
                    BumpsSurfaceEnabledCheck.IsChecked      = bs.SurfaceEnabled;
                    BumpsSurfaceGainSlider.Value            = bs.SurfaceGain;
                    BumpsSurfaceGainText.Text               = bs.SurfaceGain.ToString("F2");
                    BumpsSurfaceFreqSlider.Value            = bs.SurfaceFreq;
                    BumpsSurfaceFreqText.Text               = ((int)bs.SurfaceFreq).ToString();
                    SelectWaveform(BumpsSurfaceWaveformCombo, bs.SurfaceWaveform);
                    BumpsSurfaceRumbleScaleSlider.Value     = bs.SurfaceRumbleScale;
                    BumpsSurfaceRumbleScaleText.Text        = bs.SurfaceRumbleScale.ToString("F2");
                }
                // Traction
                var ts = _plugin.ActiveTraction;
                if (ts != null)
                {
                    TractionEnabledCheck.IsChecked       = ts.Enabled;
                    TractionGainSlider.Value             = ts.Gain;
                    TractionGainText.Text                = ts.Gain.ToString("F2");
                    TractionSensitivitySlider.Value      = ts.Sensitivity;
                    TractionSensitivityText.Text         = ts.Sensitivity.ToString("F2");
                    SelectWaveform(TractionWaveformCombo, ts.Waveform);
                    TractionFreqSlider.Value             = ts.Freq;
                    TractionFreqText.Text                = ((int)ts.Freq).ToString();
                    TractionNoiseLpSlider.Value          = ts.NoiseLowpassHz;
                    TractionNoiseLpText.Text             = ((int)ts.NoiseLowpassHz).ToString();
                    TractionNoiseHpSlider.Value          = ts.NoiseHighpassHz;
                    TractionNoiseHpText.Text             = ((int)ts.NoiseHighpassHz).ToString();
                }
                // Shift
                var ss = _plugin.ActiveShift;
                if (ss != null)
                {
                    ShiftEnabledCheck.IsChecked      = ss.Enabled;
                    ShiftGainSlider.Value            = ss.Gain;
                    ShiftGainText.Text               = ss.Gain.ToString("F2");
                    ShiftFreqSlider.Value            = ss.Freq;
                    ShiftFreqText.Text               = ((int)ss.Freq).ToString();
                    SelectWaveform(ShiftWaveformCombo, ss.Waveform);
                }
                // ABS
                var abs = _plugin.ActiveAbs;
                if (abs != null)
                {
                    AbsEnabledCheck.IsChecked     = abs.Enabled;
                    AbsGainSlider.Value           = abs.Gain;
                    AbsGainText.Text              = abs.Gain.ToString("F2");
                    AbsFreqSlider.Value           = abs.Freq;
                    AbsFreqText.Text              = ((int)abs.Freq).ToString();
                    AbsPulseFreqSlider.Value      = abs.PulseFreq;
                    AbsPulseFreqText.Text         = abs.PulseFreq.ToString("F1");
                    AbsDutyCycleSlider.Value      = abs.DutyCycle;
                    AbsDutyCycleText.Text         = abs.DutyCycle.ToString("F2");
                    AbsModeCombo.SelectedIndex    = (int)abs.Mode;
                    SelectWaveform(AbsWaveformCombo, abs.Waveform);
                }
                // Pit limiter
                var pl = _plugin.ActivePitLimiter;
                if (pl != null && PitLimiterEnabledCheck != null)
                {
                    PitLimiterEnabledCheck.IsChecked    = pl.Enabled;
                    PitLimiterGainSlider.Value          = pl.Gain;
                    PitLimiterGainText.Text             = pl.Gain.ToString("F2");
                    SelectWaveform(PitLimiterWaveformCombo, pl.Waveform);
                    PitLimiterFreqSlider.Value          = pl.Freq;
                    PitLimiterFreqText.Text             = ((int)pl.Freq).ToString();
                    PitLimiterPulseFreqSlider.Value     = pl.PulseFreq;
                    PitLimiterPulseFreqText.Text        = pl.PulseFreq.ToString("F1");
                    PitLimiterDutyCycleSlider.Value     = pl.DutyCycle;
                    PitLimiterDutyCycleText.Text        = pl.DutyCycle.ToString("F2");
                    PitLimiterActiveAmpSlider.Value     = pl.ActiveAmp;
                    PitLimiterActiveAmpText.Text        = pl.ActiveAmp.ToString("F2");
                }
                // DRS
                var drs = _plugin.ActiveDrs;
                if (drs != null && DrsEnabledCheck != null)
                {
                    DrsEnabledCheck.IsChecked       = drs.Enabled;
                    DrsGainSlider.Value             = drs.Gain;
                    DrsGainText.Text                = drs.Gain.ToString("F2");
                    SelectWaveform(DrsWaveformCombo, drs.Waveform);
                    DrsActivationFreqSlider.Value   = drs.ActivationFreq;
                    DrsActivationFreqText.Text      = ((int)drs.ActivationFreq).ToString();
                    DrsActivationMsSlider.Value     = drs.ActivationMs;
                    DrsActivationMsText.Text        = drs.ActivationMs.ToString();
                    DrsActivationAmpSlider.Value    = drs.ActivationAmp;
                    DrsActivationAmpText.Text       = drs.ActivationAmp.ToString("F2");
                    DrsSustainedFreqSlider.Value    = drs.SustainedFreq;
                    DrsSustainedFreqText.Text       = ((int)drs.SustainedFreq).ToString();
                    DrsSustainedAmpSlider.Value     = drs.SustainedAmp;
                    DrsSustainedAmpText.Text        = drs.SustainedAmp.ToString("F2");
                    SelectWaveform(DrsSustainedWaveformCombo, drs.SustainedWaveform);
                }
                // Collision (per-car overridable like the other effects)
                var coll = _plugin.ActiveCollision;
                if (coll != null && CollisionEnabledCheck != null)
                {
                    CollisionEnabledCheck.IsChecked    = coll.Enabled;
                    CollisionGainSlider.Value          = coll.Gain;
                    CollisionGainText.Text             = coll.Gain.ToString("F2");
                    CollisionMinThresholdSlider.Value  = coll.MinThreshold;
                    CollisionMinThresholdText.Text     = coll.MinThreshold.ToString("F2");
                    CollisionMaxAmpSlider.Value        = coll.MaxAmp;
                    CollisionMaxAmpText.Text           = coll.MaxAmp.ToString("F2");
                    CollisionFreqSlider.Value          = coll.Freq;
                    CollisionFreqText.Text             = ((int)coll.Freq).ToString();
                    CollisionEnvelopeMsSlider.Value    = coll.EnvelopeMs;
                    CollisionEnvelopeMsText.Text       = coll.EnvelopeMs.ToString();
                    SelectWaveform(CollisionWaveformCombo, coll.Waveform);
                }
                // Axle slip (per-car overridable like the other effects)
                var axle = _plugin.ActiveAxleSlip;
                if (axle != null && AxleSlipEnabledCheck != null)
                {
                    AxleSlipEnabledCheck.IsChecked    = axle.Enabled;
                    AxleSlipGainSlider.Value          = axle.Gain;
                    AxleSlipGainText.Text             = axle.Gain.ToString("F2");
                    AxleSlipPredictiveCheck.IsChecked = axle.PredictiveSlip;
                    AxleSlipRevLockedCheck.IsChecked  = axle.RevLockedRearPulse;
                    AxleSlipFrontStrengthSlider.Value = axle.FrontStrength;
                    AxleSlipFrontStrengthText.Text    = axle.FrontStrength.ToString("F2");
                    AxleSlipRearStrengthSlider.Value  = axle.RearStrength;
                    AxleSlipRearStrengthText.Text     = axle.RearStrength.ToString("F2");
                    AxleSlipFrontPitchSlider.Value    = axle.FrontPitchHz;
                    AxleSlipFrontPitchText.Text       = ((int)axle.FrontPitchHz).ToString();
                    AxleSlipRearPitchSlider.Value     = axle.RearPitchHz;
                    AxleSlipRearPitchText.Text        = ((int)axle.RearPitchHz).ToString();
                    AxleSlipJudderSlider.Value        = axle.JudderDepth;
                    AxleSlipJudderText.Text           = axle.JudderDepth.ToString("F2");
                    AxleSlipOnsetSlider.Value         = axle.OnsetUtil;
                    AxleSlipOnsetText.Text            = axle.OnsetUtil.ToString("F2");
                }
                // Kerb thump (per-car overridable like the other effects)
                var kerb = _plugin.ActiveKerbThump;
                if (kerb != null && KerbThumpEnabledCheck != null)
                {
                    KerbThumpEnabledCheck.IsChecked   = kerb.Enabled;
                    KerbThumpGainSlider.Value         = kerb.Gain;
                    KerbThumpGainText.Text            = kerb.Gain.ToString("F2");
                    KerbThumpFreqSlider.Value         = kerb.Freq;
                    KerbThumpFreqText.Text            = ((int)kerb.Freq).ToString();
                }
                // The FS "Leading edge" fold of the same block: disabled
                // reads as 0 (that slider has no separate enable).
                if (kerb != null && BumpsLeadingEdgeSlider != null)
                {
                    BumpsLeadingEdgeSlider.Value = kerb.Enabled ? kerb.Gain : 0f;
                    if (BumpsLeadingEdgeText != null)
                        BumpsLeadingEdgeText.Text = BumpsLeadingEdgeSlider.Value.ToString("F2");
                }
                // Lockup judder (per-car overridable like the other effects)
                var lockup = _plugin.ActiveLockupJudder;
                if (lockup != null && LockupJudderEnabledCheck != null)
                {
                    LockupJudderEnabledCheck.IsChecked = lockup.Enabled;
                    LockupJudderGainSlider.Value       = lockup.Gain;
                    LockupJudderGainText.Text          = lockup.Gain.ToString("F2");
                }
                // Rev limiter (per-car overridable like the other effects)
                var rl = _plugin.ActiveRevLimiter;
                if (rl != null && RevLimiterEnabledCheck != null)
                {
                    RevLimiterEnabledCheck.IsChecked    = rl.Enabled;
                    RevLimiterGainSlider.Value          = rl.Gain;
                    RevLimiterGainText.Text             = rl.Gain.ToString("F2");
                    SelectWaveform(RevLimiterWaveformCombo, rl.Waveform);
                    RevLimiterFreqSlider.Value          = rl.Freq;
                    RevLimiterFreqText.Text             = ((int)rl.Freq).ToString();
                    RevLimiterPulseFreqSlider.Value     = rl.PulseFreq;
                    RevLimiterPulseFreqText.Text        = rl.PulseFreq.ToString("F1");
                    RevLimiterDutyCycleSlider.Value     = rl.DutyCycle;
                    RevLimiterDutyCycleText.Text        = rl.DutyCycle.ToString("F2");
                    RevLimiterActiveAmpSlider.Value     = rl.ActiveAmp;
                    RevLimiterActiveAmpText.Text        = rl.ActiveAmp.ToString("F2");

                    // (The engage-mode combo and redline slider were retired:
                    // the redline is edited in the Car facts panel only.)
                    RevLimiterOffsetSlider.Value = rl.RedlineOffsetRpm;
                    RevLimiterOffsetText.Text    = FormatRedlineOffset(rl.RedlineOffsetRpm);
                }

                // Airborne ducking (per-car capable). Reads the effective values
                // (the active car's override if present, else the global).
                var air = _plugin.ActiveAirborne;
                if (air != null && AirborneEnabledCheck != null)
                {
                    AirborneEnabledCheck.IsChecked       = air.Enabled;
                    AirborneReductionSlider.Value        = air.Reduction;
                    AirborneReductionText.Text           = ((int)Math.Round(air.Reduction * 100)).ToString() + "%";
                    AirborneDuckEngineCheck.IsChecked    = air.DuckEngine;
                    AirborneDuckAudioCheck.IsChecked     = air.DuckAudio;
                    AirborneDuckRoadBumpsCheck.IsChecked = air.DuckRoadBumps;
                    AirborneDuckTractionCheck.IsChecked  = air.DuckTractionLoss;
                    AirborneDuckRevLimiterCheck.IsChecked= air.DuckRevLimiter;
                    AirborneDuckGearShiftCheck.IsChecked = air.DuckGearShift;
                    AirborneDuckAbsCheck.IsChecked       = air.DuckAbs;
                    AirborneDuckPitLimiterCheck.IsChecked= air.DuckPitLimiter;
                    AirborneDuckDrsCheck.IsChecked       = air.DuckDrs;
                    AirborneDuckCollisionCheck.IsChecked = air.DuckCollision;
                }

                // Override badges in expander headers. Visible only when this
                // section has its own per-car override active.
                AudioOverrideBadge.Visibility    = (_plugin.IsAudioOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                EngineOverrideBadge.Visibility   = (_plugin.IsEngineOverridden   && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                BumpsOverrideBadge.Visibility    = (_plugin.IsBumpsOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                TractionOverrideBadge.Visibility = (_plugin.IsTractionOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                ShiftOverrideBadge.Visibility    = (_plugin.IsShiftOverridden    && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                AbsOverrideBadge.Visibility      = (_plugin.IsAbsOverridden      && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (AbsUnsupportedBadge != null)
                    AbsUnsupportedBadge.Visibility = _plugin.ShowAbsUnsupportedBadge ? Visibility.Visible : Visibility.Collapsed;
                if (StationarySpringUnsupportedBadge != null)
                    StationarySpringUnsupportedBadge.Visibility = _plugin.ActiveSourceSupportsStationarySpring ? Visibility.Collapsed : Visibility.Visible;
                if (PitLimiterOverrideBadge != null)
                    PitLimiterOverrideBadge.Visibility = (_plugin.IsPitLimiterOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (DrsOverrideBadge != null)
                    DrsOverrideBadge.Visibility        = (_plugin.IsDrsOverridden        && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (CollisionOverrideBadge != null)
                    CollisionOverrideBadge.Visibility  = (_plugin.IsCollisionOverridden  && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (AxleSlipOverrideBadge != null)
                    AxleSlipOverrideBadge.Visibility   = (_plugin.IsAxleSlipOverridden   && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (KerbThumpOverrideBadge != null)
                    KerbThumpOverrideBadge.Visibility  = (_plugin.IsKerbThumpOverridden  && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (LockupJudderOverrideBadge != null)
                    LockupJudderOverrideBadge.Visibility = (_plugin.IsLockupJudderOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;
                if (RevLimiterOverrideBadge != null)
                    RevLimiterOverrideBadge.Visibility = (_plugin.IsRevLimiterOverridden && carDetected) ? Visibility.Visible : Visibility.Collapsed;

                // Conditional dimming/hiding on dependent settings:
                //  - Traction noise LP/HP only matter for the Noise waveform.
                //  - ABS pulse rate/duty only matter in Pulse mode.
                bool tractionIsNoise = ts != null && ts.Waveform == Waveform.Noise;
                TractionNoiseLpRow.Visibility = tractionIsNoise ? Visibility.Visible : Visibility.Collapsed;
                TractionNoiseHpRow.Visibility = tractionIsNoise ? Visibility.Visible : Visibility.Collapsed;
                AbsPulseControls.IsEnabled    = abs == null || abs.Mode == AbsMode.Pulse;
            }
            finally { _suppressEvents = false; }

            // After all UI controls have been re-synced from plugin state,
            // re-derive each section's dirty bit from the (now-current)
            // values vs the active preset's snapshot. This catches scope
            // changes (override on/off), preset apply, game/car switches,
            // and toggles back to original values.
            RecomputeAllEffectDirty();
            UpdateOfflineEditBanner();
        }

        // Toggle the offline-edit banner's visibility and title text based
        // on whether the plugin is currently in offline-edit mode. Called
        // from RefreshFromPlugin and whenever the mode transitions.
        private void UpdateOfflineEditBanner()
        {
            if (OfflineEditBanner == null) return;

            // While car-editing, only per-car effects are editable; grey the
            // game-global controls so a global change can't be made (and lost)
            // inside a car edit.
            bool carEdit = _plugin != null && _plugin.IsOfflineEditingCar;
            SetCarEditLock(carEdit);

            // Car-preset offline edit takes precedence (it freezes the car).
            if (carEdit)
            {
                OfflineEditBanner.Visibility = Visibility.Visible;
                OfflineEditTitle.Text = $"Editing per-car settings for '{_plugin.OfflineEditingCarId}' (preset '{_plugin.OfflineEditingCarPresetName}')";
                if (OfflineEditHint != null)
                    OfflineEditHint.Text = "The game defaults are shown (and locked) for context; only the per-car effects are editable. Save the usual way, or Revert a section to undo it. Done finishes and returns to your live setup.";
                return;
            }

            string editing = _plugin?.OfflineEditingPresetName;
            if (string.IsNullOrEmpty(editing))
            {
                OfflineEditBanner.Visibility = Visibility.Collapsed;
                return;
            }
            OfflineEditBanner.Visibility = Visibility.Visible;
            OfflineEditTitle.Text = $"Editing preset '{editing}'";
            if (OfflineEditHint != null)
                OfflineEditHint.Text = "Save the usual way, or Revert a section to undo it. Done finishes editing.";
        }

        // While editing a car preset, only per-car effects are editable. Lock the
        // game-global controls (master gain, FFB tweaks, iRacing LEDs) so a global
        // change can't be made/lost inside a car edit. The car-scoped effect
        // expanders (engine, bumps, traction, shift, ABS, pit limiter, DRS,
        // collision, rev limiter, audio, airborne) stay editable.
        private void SetCarEditLock(bool locked)
        {
            bool en = !locked;
            if (MasterGainSlider != null) MasterGainSlider.IsEnabled = en;
            if (MasterGainText  != null)  MasterGainText.IsEnabled   = en;
            if (MasterSaveBtn   != null)  MasterSaveBtn.IsEnabled     = en;
            if (MasterRevertBtn != null)  MasterRevertBtn.IsEnabled   = en;
            if (FfbTweaksExpander != null) FfbTweaksExpander.IsEnabled = en;
            // Airborne is per-car, so it stays editable during car-edit.
            if (RpmLedSection     != null) RpmLedSection.IsEnabled     = en;
        }

        // Public entry point used by ManagePresetsDialog when the user picks
        // Edit on a row. Load the preset and flip the banner on.
        public void EnterOfflineEditMode(string presetName)
        {
            if (_plugin == null || string.IsNullOrEmpty(presetName)) return;
            if (!_plugin.EnterOfflineEdit(presetName)) return;
            ClearDirty();
            RefreshFromPlugin();
            // Land on the Effects tab where the controls (and the offline-edit
            // banner with Save / Save as new / Discard) are.
            if (MainTabs != null) MainTabs.SelectedIndex = 0;
        }

        // "Done": finish the edit session. Saving itself happens the normal way
        // (per-section / header Save) during the edit; Done commits anything
        // still unsaved and restores the prior state. Built-ins fork to a new
        // name. With nothing unsaved it just exits.
        private void OfflineEditDone_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            bool car = _plugin.IsOfflineEditingCar;
            if (!car && !_plugin.IsOfflineEditing) return;

            bool dirty = car ? _plugin.IsActiveCarPresetDirty() : _dirty;
            if (!dirty)
            {
                // No-op edit: leave edit mode and restore prior state (no write,
                // no built-in fork prompt).
                if (car) _plugin.ExitOfflineEditCarDiscard();
                else     _plugin.ExitOfflineEditDiscard();
                ClearDirty();
                RefreshFromPlugin();
                return;
            }

            // DEV mode: save the built-in in place (the plugin write-throughs
            // to the factory folder). Non-dev: fork into a new user preset.
            // The user-friendly fork drops the " (Built-In)" suffix and
            // saves silently when the resulting name is free, so saving an
            // edit to a built-in feels like "saved" instead of "answer a
            // dialog every time." Only when the stripped name is already
            // taken by a user preset do we fall back to the rename prompt.
            if (car)
            {
                if (_plugin.IsActiveCarPresetBuiltin() && !_plugin.DevMode)
                {
                    string carFull = _plugin.OfflineEditingCarPresetName;
                    string carClean = TrueforcePlugin.ToDiskName(carFull);
                    string carId = _plugin.OfflineEditingCarId;
                    var existing = _plugin.GetCarPresets(carId);
                    bool carClash = existing != null && existing.ContainsKey(carClean);
                    if (!carClash)
                    {
                        // Silent fork: keep the new preset applied + bound
                        // as the car's default (built into SaveActiveCarPresetAs).
                        if (!_plugin.ExitOfflineEditCarSaveAsAndApply(carClean))
                        {
                            TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                            return;
                        }
                    }
                    else
                    {
                        PromptAndSaveAsNewCar(carFull);
                        return;
                    }
                }
                else if (!_plugin.ExitOfflineEditCarSave())
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                    return;
                }
            }
            else
            {
                string name = _plugin.OfflineEditingPresetName;
                if (_plugin.IsBuiltinPreset(name) && !_plugin.DevMode)
                {
                    string clean = TrueforcePlugin.ToDiskName(name);
                    bool clash = _plugin.Settings?.Presets?.ContainsKey(clean) == true;
                    if (!clash)
                    {
                        // Silent fork: keep the new preset active + bound as
                        // the game default.
                        if (!_plugin.ExitOfflineEditSaveAsAndApply(clean))
                        {
                            TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                            return;
                        }
                    }
                    else
                    {
                        PromptAndSaveAsNew(name);
                        return;
                    }
                }
                else if (!_plugin.ExitOfflineEditSave())
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                    return;
                }
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        // Car variant of PromptAndSaveAsNew: prompt for a name and fork the car
        // edits into a new preset for the edited car. Strips the
        // " (Built-In)" suffix from the suggested name so a fork from a
        // built-in doesn't pre-fill "X (Built-In) (edited)".
        private void PromptAndSaveAsNewCar(string suggestedBaseName)
        {
            string carId = _plugin.OfflineEditingCarId;
            string baseName = TrueforcePlugin.HasBuiltinSuffix(suggestedBaseName ?? "")
                ? TrueforcePlugin.ToDiskName(suggestedBaseName)
                : suggestedBaseName;
            string suggested = string.IsNullOrEmpty(baseName) ? "My preset" : baseName + " (edited)";
            string newName = PromptForName("Save as new car preset", "New preset name:", suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            var existing = _plugin.GetCarPresets(carId);
            if (existing != null && existing.ContainsKey(newName))
            {
                if (TrueforceDialog.Show(Window.GetWindow(this),
                    "Overwrite preset?",
                    $"A preset called '{newName}' already exists for this car. Overwrite?",
                    DialogKind.Confirm) != true) return;
                _plugin.DeleteCarPreset(carId, newName);
            }
            if (!_plugin.ExitOfflineEditCarSaveAs(newName))
            {
                TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        private void PromptAndSaveAsNew(string suggestedBaseName)
        {
            // Strip " (Built-In)" before composing the suggestion so a
            // built-in fork pre-fills "X (edited)" instead of
            // "X (Built-In) (edited)".
            string baseName = TrueforcePlugin.HasBuiltinSuffix(suggestedBaseName ?? "")
                ? TrueforcePlugin.ToDiskName(suggestedBaseName)
                : suggestedBaseName;
            string suggested = string.IsNullOrEmpty(baseName)
                ? "My preset"
                : baseName + " (edited)";
            string newName = PromptForName("Save as new preset", "New preset name:", suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (_plugin.Settings?.Presets?.ContainsKey(newName) == true)
            {
                if (TrueforceDialog.Show(Window.GetWindow(this),
                    "Overwrite preset?",
                    $"A preset called '{newName}' already exists. Overwrite?",
                    DialogKind.Confirm) != true) return;
                _plugin.DeletePreset(newName);
            }
            if (!_plugin.ExitOfflineEditSaveAs(newName))
            {
                TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
        }

        private static void SelectWaveform(ComboBox combo, Waveform w) => combo.SelectedIndex = (int)w;
        private static Waveform WaveformOf(ComboBox combo)
        {
            int idx = combo.SelectedIndex; if (idx < 0) idx = 0;
            return (Waveform)idx;
        }

        // ---------- meter tick + active-car sync ----------

        // Tick-rate probe state (dev mode only): the timer asks for 16 ms;
        // these measure what it really gets, for the perf-review backlog
        // entry on heavy per-tick refreshes.
        private long _tickProbeStartTs;
        private long _tickProbeLastTs;
        private int _tickProbeCount;
        private double _tickProbeWorstGapMs;

        private void MeterTimer_Tick(object sender, EventArgs e)
        {
            // Dev-gated: measure the timer's REAL cadence and log it every
            // ~10 s. One line per 10 s, only with dev mode unlocked.
            if (_plugin?.Settings?.DevModeUnlocked == true)
            {
                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_tickProbeLastTs != 0)
                {
                    double gapMs = (nowTs - _tickProbeLastTs) * 1000.0
                                   / System.Diagnostics.Stopwatch.Frequency;
                    if (gapMs > _tickProbeWorstGapMs) _tickProbeWorstGapMs = gapMs;
                }
                _tickProbeLastTs = nowTs;
                _tickProbeCount++;
                if (_tickProbeStartTs == 0) _tickProbeStartTs = nowTs;
                else
                {
                    double spanS = (nowTs - _tickProbeStartTs)
                                   / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (spanS >= 10.0)
                    {
                        SimHub.Logging.Current.Info(
                            $"[TF4ALL] UI meter tick: {_tickProbeCount / spanS:0.0} Hz "
                            + $"over {spanS:0.0} s, worst gap {_tickProbeWorstGapMs:0} ms "
                            + "(asked: 16 ms / 60 Hz)");
                        _tickProbeCount = 0;
                        _tickProbeStartTs = nowTs;
                        _tickProbeWorstGapMs = 0;
                    }
                }
            }

            // (Was: per-tick variant-prompt recompute. Now silent
            // auto-create happens at the end of every CarFacts resolve
            // pass inside the plugin, so the UI tick has nothing to do
            // here for variants.)

            // Redline-coverage badge: track whether the cascade is in
            // the "guess" branch. Tick-driven because the underlying
            // flag flips with each ResolveEffectiveRedline call.
            RefreshRedlineGuessBadge();

            RefreshCarFactsPanel();
            RefreshIRacingAutoReadiness();
            RefreshModeBAutoStrengthReadiness();

            var src = _plugin?.AudioCapture;
            if (src != null)
            {
                float peak = src.ReadAndResetPeak();
                if (peak > 1f) peak = 1f;
                double cur = AudioLevelMeter.Value;
                AudioLevelMeter.Value = peak > cur ? peak : cur * 0.85;
                CaptureStatusText.Text = _plugin.CaptureStatus;
            }
            if (_plugin != null)
            {
                FfbTapText.Text = _plugin.FfbTapStatus;
                StreamText.Text = _plugin.StreamStatus;
                VoicesText.Text = _plugin.ActiveVoiceCount.ToString();

                // Surface USBPcap recovery actions only when USBPcap is
                // missing. Keeps the diagnostics row uncluttered in the
                // common case where everything's installed correctly.
                if (UsbPcapBrowseButton != null && UsbPcapReinstallButton != null)
                {
                    var want = _plugin.IsUsbPcapAvailable
                        ? System.Windows.Visibility.Collapsed
                        : System.Windows.Visibility.Visible;
                    if (UsbPcapBrowseButton.Visibility != want)    UsbPcapBrowseButton.Visibility    = want;
                    if (UsbPcapReinstallButton.Visibility != want) UsbPcapReinstallButton.Visibility = want;
                }

                // UDP telemetry section: Forza is the only UDP game, so its
                // config is always shown as the body of the expander.
                UpdateUdpSectionVisibility();
                if (RpmLedSection != null && RpmLedSection.Visibility != System.Windows.Visibility.Collapsed)
                {
                    // Permanently hidden (2026-08-01): the external FFB handoff
                    // this section configured has been removed, so there is
                    // nothing left to configure. The shell survives only for
                    // the LED Test button.
                    RpmLedSection.Visibility = System.Windows.Visibility.Collapsed;
                }

                // "Pick device manually..." buttons (Diagnostics + the
                // contextual one in the FfbTapPicker banner). Hidden by
                // default (auto-discovery + identity-based self-heal cover
                // realistic failure modes); revealed by the MANUALPIN access
                // code for power users who genuinely need to pin.
                {
                    var want = (_plugin.Settings?.ShowManualOverrideUi == true)
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (UsbPcapPickDeviceButton != null
                        && UsbPcapPickDeviceButton.Visibility != want)
                        UsbPcapPickDeviceButton.Visibility = want;
                    if (FfbTapPickerBannerButton != null
                        && FfbTapPickerBannerButton.Visibility != want)
                        FfbTapPickerBannerButton.Visibility = want;
                }

                // Max force number box: dash / rim nudges change the value
                // underneath this editable box, so re-fill it here at a gentle
                // cadence. Time-based, not tick-based: the meter timer asks
                // for 16 ms but its real cadence sags under load (a 30-tick
                // divider here took several seconds on the rig), so gate on
                // the clock.
                if ((DateTime.UtcNow - _maxNmLastFillUtc).TotalMilliseconds >= 400)
                {
                    _maxNmLastFillUtc = DateTime.UtcNow;
                    UpdateIRacingMaxNmText();
                    UpdateIRacingClipWarning();
                    // A nudge (dash / rim) that changed the value under the
                    // "Set to X ..." press confirmation must move that number
                    // too, or the line claims the wheel is somewhere it is not.
                    double curNm = _plugin.GetEditableMaxForceNm();
                    bool moved = _maxNmLastSeen >= 0 && Math.Abs(curNm - _maxNmLastSeen) > 0.05;
                    _maxNmLastSeen = curNm;
                    if (moved && curNm > 0.5 && IRacingAutoMaxForceStatus != null
                        && IRacingAutoMaxForceStatus.Visibility == Visibility.Visible
                        && IRacingAutoMaxForceStatus.Text.StartsWith("Set to "))
                    {
                        IRacingAutoMaxForceStatus.Text = "Set to " + curNm.ToString("F1")
                            + " Nm. Watching again from now, so a clean lap plus another press redoes it.";
                    }
                }

                // Mode B contention warning (Telemetry Based FFB tab). The
                // plugin latches ModeBContentionDetected when the game keeps
                // streaming its own FFB while Mode B drives the wheel;
                // surfaced here so it appears without a tab switch.
                // Change-only write keeps the 60 Hz tick cheap.
                if (ModeBContentionWarning != null)
                {
                    var want = _plugin.ModeBContentionDetected
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (ModeBContentionWarning.Visibility != want)
                        ModeBContentionWarning.Visibility = want;
                }

                // Slip-starved fallback banner (same tab, same cadence): Mode B
                // armed with no slip telemetry, game FFB passed through.
                if (ModeBSlipStarvedWarning != null)
                {
                    var want = _plugin.ModeBSlipStarvedDetected
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (ModeBSlipStarvedWarning.Visibility != want)
                        ModeBSlipStarvedWarning.Visibility = want;
                }

                // Header update controls. When an update is available, the
                // "Check for updates" link + transient status hide and a
                // prominent "Update to vX.Y.Z" button takes their place inline
                // with the version readout. Otherwise the link stays visible
                // so users can re-poll on demand.
                var upd = _plugin.UpdateChecker;
                bool hasUpdate = upd != null && upd.IsUpdateAvailable;
                if (UpdateAvailableButton != null)
                {
                    var want = hasUpdate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    if (UpdateAvailableButton.Visibility != want) UpdateAvailableButton.Visibility = want;
                    if (hasUpdate && UpdateAvailableButtonText != null)
                    {
                        string desired = upd.IsDowngrade
                            ? $"Switch back to v{upd.LatestVersionDisplay}  →"
                            : $"Update to v{upd.LatestVersionDisplay}  →";
                        if (UpdateAvailableButtonText.Text != desired) UpdateAvailableButtonText.Text = desired;
                    }
                }
                if (CheckForUpdatesButton != null)
                {
                    var want = hasUpdate ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                    if (CheckForUpdatesButton.Visibility != want) CheckForUpdatesButton.Visibility = want;
                }
                if (CheckForUpdatesStatus != null && hasUpdate && CheckForUpdatesStatus.Visibility != System.Windows.Visibility.Collapsed)
                {
                    // Don't keep a stale "Up to date" / "Checking..." line
                    // visible next to the prominent update CTA.
                    CheckForUpdatesStatus.Visibility = System.Windows.Visibility.Collapsed;
                }
                else if (CheckForUpdatesStatus != null && !hasUpdate && CheckForUpdatesStatus.Visibility != System.Windows.Visibility.Visible)
                {
                    CheckForUpdatesStatus.Visibility = System.Windows.Visibility.Visible;
                }

                // (The header "check community for updates" link was removed; community-preset
                // update review lives in the Preset browser's updates chip.)

                // "What's new" banner + per-effect NEW badges. Both driven by
                // the plugin's SeenEffects / LastSeenVersion state.
                RefreshChangelogBanner();
                RefreshShareCtaBanner();
                RefreshExperimentalSuccessBanner();
                RefreshNewBadges();

                // Forza listener status: the source object exposes packet
                // count + last IsRaceOn. When the source isn't active (the
                // active game isn't a Forza title), we show "(idle)".
                // This is the user's primary "is my Data Out
                // wiring working" feedback so make it specific.
                // Engine auto-detect indicator: shows the layout the resolver
                // chose for the active car when no pin is set, or surfaces
                // what Auto would be when the user has pinned a type in Car
                // facts. Throttled: the pin + summary reads take the car-facts
                // lock, and 4 Hz is plenty for a readout (the 16 ms tick was
                // hitting that lock ~5 times per frame).
                if (EngineLayoutAutoText != null && DateTime.UtcNow >= _engineReadoutNextTick)
                {
                    _engineReadoutNextTick = DateTime.UtcNow.AddMilliseconds(250);
                    var ep = _plugin.EnginePulse;
                    string activeCar = _plugin.ActiveCarId;
                    // The user's engine choice is the Car facts variant pin
                    // (the preset Layout field is legacy since the 2026-07
                    // centralization).
                    var (pinnedLayout, pinnedCustomId) = _plugin.GetActiveVariantUserEngine();
                    bool userIsAuto = pinnedLayout == null;
                    // Rev ceiling readout: rides the END of this same line
                    // (the separate Car facts meta line was removed).
                    var facts = _plugin.GetActiveCarFactsSummary();
                    string maxPart = facts.HasCar && facts.MaxRpm.HasValue
                        ? facts.MaxRpm.Value + " max RPM" : "";

                    // Per-gear editor auto-show latch. The car/game latch below
                    // misses VARIANT changes (telemetry disambiguating a
                    // multi-variant car after load, a mid-session tune swap), so
                    // without this a variant with saved per-gear redlines could
                    // drive the buzz while the editor stays hidden. Skip while
                    // the user is typing in a per-gear box so an in-progress
                    // edit isn't destroyed; the next pass catches up.
                    string perGearKey = (facts.ActiveVariantId ?? "") + "|" + facts.PerGearOverrideCount;
                    if (perGearKey != _perGearShownKey
                        && (CarFactsPerGearRows == null || !CarFactsPerGearRows.IsKeyboardFocusWithin))
                    {
                        _perGearShownKey = perGearKey;
                        RebuildPerGearEditors();
                    }

                    if (ep != null && userIsAuto && ep.AutoLayout is Effects.EngineLayout autoL)
                    {
                        string detectSrc = ep.AutoLayoutSource;
                        string srcSuffix = FriendlyDetectSourceSuffix(detectSrc);
                        string autoLine = $"Auto-detected: {Effects.FiringPatternDb.LayoutDisplayName(autoL)}{srcSuffix}";
                        // When community is the source AND the resolver could
                        // also reach a concrete non-community value, surface
                        // it as passive context. Lets the user see what
                        // they'd get from the built-in path and decide
                        // whether to pin that value in the Car facts engine
                        // dropdown (which commits + submits through the usual
                        // pin path). No inline action.
                        if (string.Equals(detectSrc, "community", StringComparison.OrdinalIgnoreCase)
                            && ep.NonCommunityAutoLayout is Effects.EngineLayout altLayout
                            && altLayout != autoL)
                        {
                            string altSrcLabel = ep.NonCommunityAutoLayoutSource;
                            string altSrcWord;
                            if (string.Equals(altSrcLabel, "baked", StringComparison.OrdinalIgnoreCase))
                                altSrcWord = "built-in";
                            else if (string.Equals(altSrcLabel, "cache", StringComparison.OrdinalIgnoreCase))
                                altSrcWord = "cache";
                            else if (string.Equals(altSrcLabel, "swap-override", StringComparison.OrdinalIgnoreCase))
                                altSrcWord = "swap data";
                            else
                                altSrcWord = altSrcLabel;
                            autoLine += string.IsNullOrEmpty(altSrcWord)
                                ? $". Alternative: {Effects.FiringPatternDb.LayoutDisplayName(altLayout)}"
                                : $". {char.ToUpper(altSrcWord[0])}{altSrcWord.Substring(1)} says: "
                                  + Effects.FiringPatternDb.LayoutDisplayName(altLayout);
                        }
                        EngineLayoutAutoText.Text = autoLine;
                    }
                    else if (ep != null && userIsAuto
                             && string.IsNullOrEmpty(ep.AutoLayoutSource)
                             && !string.IsNullOrEmpty(activeCar))
                    {
                        EngineLayoutAutoText.Text =
                            $"Could not auto-detect engine type for '{activeCar}'. "
                            + "Pick the closest match in the Engine dropdown above; the Engine pulse Test button can help you A/B.";
                    }
                    else if (ep != null && pinnedLayout is Effects.EngineLayout pinnedL
                             && ep.AutoLayout is Effects.EngineLayout autoOverridden
                             && autoOverridden != pinnedL)
                    {
                        // User pin that disagrees with the resolver.
                        EngineLayoutAutoText.Text =
                            $"Your pick: {DescribePinnedEngine(pinnedL, pinnedCustomId)}. "
                            + $"Auto would be {Effects.FiringPatternDb.LayoutDisplayName(autoOverridden)}"
                            + $"{FriendlyDetectSourceSuffix(ep.AutoLayoutSource)}. "
                            + "Pick Auto to use detection.";
                    }
                    else if (pinnedLayout is Effects.EngineLayout pinnedPick)
                    {
                        // Pin agrees with auto-detect (or no auto value yet):
                        // still mark the type as the user's own pick.
                        EngineLayoutAutoText.Text =
                            $"Your pick: {DescribePinnedEngine(pinnedPick, pinnedCustomId)}";
                    }
                    else
                    {
                        EngineLayoutAutoText.Text = "";
                    }
                    if (!string.IsNullOrEmpty(maxPart))
                        EngineLayoutAutoText.Text = string.IsNullOrEmpty(EngineLayoutAutoText.Text)
                            ? maxPart
                            : EngineLayoutAutoText.Text + "  ·  " + maxPart;

                    // Keep the combo honest (#2): its selection only synced on
                    // rebuild events, which on a car change run BEFORE telemetry
                    // has produced the variant signature - so a pin resolving
                    // seconds later left the combo saying Auto while this line
                    // said "Your pick". The pin is already in hand here; re-sync
                    // the index whenever reality changed, but never while the
                    // user is interacting or a browse-commit is pending.
                    if (CarFactsEngineCombo != null && CarFactsEngineCombo.ItemsSource != null
                        && !CarFactsEngineCombo.IsDropDownOpen
                        && !CarFactsEngineCombo.IsKeyboardFocusWithin
                        && !_enginePinCommitPending)
                    {
                        int want = FindEngineDropdownIndex(
                            pinnedLayout ?? Effects.EngineLayout.Auto, pinnedCustomId ?? "");
                        if (CarFactsEngineCombo.SelectedIndex != want)
                        {
                            bool oldSup = _suppressEvents;
                            _suppressEvents = true;
                            try { CarFactsEngineCombo.SelectedIndex = want; }
                            finally { _suppressEvents = oldSup; }
                        }
                    }

                    // Engine-data submission fires directly from the Car
                    // facts engine pick (CommitEnginePin ->
                    // MaybePromptToSubmitEngineData); there is no panel
                    // button and no save-flow hook needed.

                    // Community context: surface what other drivers said
                    // for THIS car upfront so the user can confirm or correct
                    // it before any save. Debounced on (game, carId) so we
                    // only fetch when the car actually changes. The
                    // render call reflects the latest auto-detect state
                    // each tick - Confirm button shows up as soon as an
                    // auto-detected layout is available, even before the
                    // community fetch returns.
                    // The community row is context, not a second readout: when
                    // the user is on Auto and the resolver's pick came FROM the
                    // community (the line above already says "(community)"),
                    // repeating the value underneath is noise. Keep the row for
                    // the cases where it adds information: a user pin overriding
                    // it, or detection that used a different source.
                    _engineCommunityRowRedundant = userIsAuto && ep != null
                        && string.Equals(ep.AutoLayoutSource, "community",
                            StringComparison.OrdinalIgnoreCase);
                    MaybeRefreshEngineCommunityContext(_plugin.ActiveGame, activeCar);
                    RenderEngineCommunityRow();
                }

                // Set by the Forza block below; drives the persistent
                // UDP-setup banner once Forza is running but silent.
                bool forzaNeedsSetup = false;

                if (ForzaStatusText != null)
                {
                    // Use the keep-alive Forza listener, not the active source:
                    // while on the SimHub fallback the active source is SimHub
                    // even though the Forza listener is still bound, and the
                    // status/forward UI should reflect the listener.
                    var fzSrc = _plugin.ForzaUdpSource;

                    // True once we're confident nothing is arriving and the
                    // user should see the troubleshooter: either the STALL test
                    // code is active, or the source has sat at zero packets past
                    // the sustain threshold. Only the "never received anything"
                    // case auto-opens; a count that stopped climbing mid-session
                    // is usually a normal quit-to-menu, not a config problem.
                    bool zeroPacketsSustained = false;

                    if (_forceForzaStall)
                    {
                        ForzaStatusText.Text =
                            "No packets arriving now (0 Hz). 0 received this session. If you're in-game, check Forza Data Out config + the troubleshooter below. [STALL test active]";
                        zeroPacketsSustained = true;
                    }
                    else if (fzSrc == null)
                    {
                        ForzaStatusText.Text = "(idle, not active for current game)";
                        _forzaZeroSinceTicks = 0;
                        _forzaTroubleshootAutoExpanded = false;
                    }
                    else if (fzSrc.PacketsReceived == 0)
                    {
                        ForzaStatusText.Text =
                            $"Listening on {(_plugin.Settings?.Forza?.BindAddress ?? "0.0.0.0")}:{(_plugin.Settings?.Forza?.Port ?? 0)}, no packets yet (check Forza Data Out config + the troubleshooter below)";
                        // Stamp when the zero-packet stretch began so we can
                        // auto-open the troubleshooter once it's sustained.
                        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (_forzaZeroSinceTicks == 0) _forzaZeroSinceTicks = nowTicks;
                        if (nowTicks - _forzaZeroSinceTicks >= ForzaStallExpandTicks)
                            zeroPacketsSustained = true;
                    }
                    else
                    {
                        // Packets have arrived: clear the zero-stretch tracking
                        // and re-arm the one-shot so a later genuine stall can
                        // open the troubleshooter again.
                        _forzaZeroSinceTicks = 0;
                        _forzaTroubleshootAutoExpanded = false;
                        // PacketsReceived is a lifetime session total and never
                        // decreases, so on its own it can't tell the user
                        // whether data is arriving right now. MeasuredHz zeroes
                        // out after 1s of silence, so it's the live-flow truth.
                        // When Hz is 0 we have a stale count: packets arrived at
                        // some point this session but nothing is coming in now.
                        // The driving/paused label is only meaningful while live
                        // (LastIsRaceOn is itself frozen at the last packet), so
                        // we only show it when Hz > 0.
                        double hz = fzSrc.MeasuredHz;
                        if (hz > 0)
                        {
                            string state = fzSrc.LastIsRaceOn ? "driving" : "paused / in menu";
                            ForzaStatusText.Text =
                                $"Receiving at ~{hz:0} Hz, {state}. {fzSrc.PacketsReceived:N0} packets this session.";
                        }
                        else
                        {
                            ForzaStatusText.Text =
                                $"No packets arriving now (0 Hz). {fzSrc.PacketsReceived:N0} received earlier this session. If you're in-game, check Forza Data Out config + the troubleshooter below.";
                        }
                    }

                    // Auto-open the "Not receiving packets?" troubleshooter once
                    // we're confident nothing is arriving, so the loopback /
                    // port / firewall guidance reaches the user without them
                    // having to find and expand it. One-shot: a user who closes
                    // it isn't fought on every refresh tick (the latch re-arms
                    // only when packets resume, above).
                    if (zeroPacketsSustained
                        && !_forzaTroubleshootAutoExpanded
                        && ForzaTroubleshootExpander != null)
                    {
                        ForzaTroubleshootExpander.IsExpanded = true;
                        _forzaTroubleshootAutoExpanded = true;
                    }

                    // Prompt UDP setup only when the Forza process is actually
                    // running (not just a selected-but-closed profile) and
                    // nothing has arrived past the sustain window. The STALL
                    // test code bypasses the running gate on purpose.
                    forzaNeedsSetup = zeroPacketsSustained
                                      && (_plugin.IsForzaRunning || _forceForzaStall);

                    if (ForzaForwardStatusText != null)
                    {
                        var fwd = _plugin.Settings?.Forza;
                        if (fwd == null || !fwd.ForwardEnabled)
                        {
                            ForzaForwardStatusText.Text = "(disabled)";
                        }
                        else if (fzSrc == null)
                        {
                            ForzaForwardStatusText.Text = "(armed, will relay once a Forza title is detected)";
                        }
                        else
                        {
                            ForzaForwardStatusText.Text =
                                $"{fzSrc.PacketsForwarded:N0} packets relayed to {fwd.ForwardHost}:{fwd.ForwardPort}";
                        }
                    }

                    // Discovered-port banner: shown only when the active
                    // source is Forza UDP and the plugin found an alternate.
                    if (ForzaDiscoveryBanner != null)
                    {
                        int alt = _plugin.DiscoveredAlternatePort;
                        bool show = _forceForzaInfoBanners == 1 || (fzSrc != null && alt > 0);
                        ForzaDiscoveryBanner.Visibility = show
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
                        if (show && ForzaDiscoveryText != null)
                        {
                            ForzaDiscoveryText.Text = alt > 0
                                ? $"Forza packets detected on port {alt}. Switch to it?"
                                : "Forza packets detected on a different port. Switch to it?";
                        }
                    }
                }

                // UDP test code override (forces the banner without a session).
                if (_forceUdpSetupBanner == 1) forzaNeedsSetup = true;

                // The SimHub-fallback notice and the no-telemetry setup banner
                // are mutually exclusive states: fallback means telemetry IS
                // reaching SimHub (working but suboptimal), so it takes
                // precedence over the "nothing is arriving" banner.
                bool onSimHubFallback = _plugin.ForzaOnSimHubFallback;
                if (onSimHubFallback) forzaNeedsSetup = false;
                UpdateUdpSetupBanner(forzaNeedsSetup);
                UpdateForzaFallbackBanner(onSimHubFallback || _forceForzaInfoBanners == 1);
            }

            // Telemetry-source line in Diagnostics: source name + live measured Hz,
            // plus an "audio only" suffix when frames are arriving but contain no
            // useful physics data (custom SimHub games without telemetry).
            var telSrc = _plugin?.TelemetrySource;
            if (telSrc != null)
            {
                string label = telSrc.IsEnhanced ? "Enhanced Effects" : "SimHub";
                double hz = telSrc.MeasuredHz;
                string baseText = hz > 0 ? $"{label} · {hz:0} Hz" : $"{label} · idle";
                bool gameRunning = !string.IsNullOrEmpty(_plugin.ActiveGame);
                bool audioOnly   = gameRunning && hz > 0 && !_plugin.HasUsefulTelemetry;
                if (audioOnly) baseText += " · audio only";
                TelemetrySourceText.Text = baseText;

                // Grey out the telemetry-effect controls (engine pulse, road
                // bumps, traction loss, gear shift, ABS) ONLY when a game is
                // running and we've established it provides no telemetry.
                // No game → keep enabled so the user can still tune presets
                // for any future session. Test buttons stay reachable from
                // the disabled panel because IsEnabled=false dims but doesn't
                // remove access. Actually IsEnabled=false on a StackPanel
                // disables children, so Test buttons would be inert too.
                // That matches "no data, no point testing" intent.
                if (TelemetryEffectsPanel != null)
                    TelemetryEffectsPanel.IsEnabled = !audioOnly;
            }

            UpdateStatusPill();

            // Keep the account-chip connection dot live: flips green<->amber as
            // token-refresh health changes in the background (guarded so it only
            // repaints on a transition; no network read).
            UpdateAccountChipDotLive();

            // Card issues (G HUB / admin / wheel-quiet / FFB-tap / unverified)
            // are coalesced into one banner at a time. See RefreshCardIssues.
            RefreshCardIssues();

            // Performance counters update every meter tick (cheap; array sum
            // of 60 longs). Doesn't depend on any expander being open.
            UpdatePerfCounters();

            string carId = _plugin?.ActiveCarId;
            string game  = _plugin?.ActiveGame;
            if (carId != _lastShownCarId || game != _lastShownGame)
            {
                bool gameChanged = game != _lastShownGame;
                _lastShownCarId = carId;
                _lastShownGame  = game;
                // Game change → plugin may have auto-applied a preset for the
                // new game. Treat the resulting state as the saved baseline so
                // the "★ unsaved" indicator doesn't fire spuriously when the
                // user hasn't changed anything yet.
                ClearDirty();
                RefreshFromPlugin();
                // Notify the preset manager so its community panel can
                // re-scope its label + (when looking at car-kind or a
                // game-kind where the game changed) refresh the list.
                // PresetManagerControl decides internally whether to skip
                // the fetch for car-agnostic kinds.
                _presetManager?.OnActiveCarChanged(gameChanged);

                // First active-car/game change of the session: nudge the
                // networked-welcome modal so an upgrader who never opens
                // Settings still sees the community pitch. The modal's own
                // HasSeenNetworkedWelcome + WelcomeNextShowAt + backend-config
                // gates short-circuit redundant pops; the session latch
                // additionally prevents a re-fire when the user switches cars
                // mid-session. Defer via Dispatcher so the meter tick isn't
                // blocked by ShowDialog.
                if (!_welcomeTriggeredThisSession
                    && _plugin?.Settings != null
                    && !_plugin.Settings.HasSeenNetworkedWelcome)
                {
                    _welcomeTriggeredThisSession = true;
                    Dispatcher.BeginInvoke(new Action(MaybeShowNetworkedWelcome),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        // Status-pill colors. Cached as static brushes so the 60 Hz tick
        // doesn't allocate a fresh brush on every refresh.
        private static readonly System.Windows.Media.SolidColorBrush PillGreenBg   = MakeBrush("#3D8B40");
        private static readonly System.Windows.Media.SolidColorBrush PillGreenDot  = MakeBrush("#A5D6A7");
        private static readonly System.Windows.Media.SolidColorBrush PillAmberBg   = MakeBrush("#B26A00");
        private static readonly System.Windows.Media.SolidColorBrush PillAmberDot  = MakeBrush("#FFCC80");
        private static readonly System.Windows.Media.SolidColorBrush PillGreyBg    = MakeBrush("#666666");
        private static readonly System.Windows.Media.SolidColorBrush PillGreyDot   = MakeBrush("#BDBDBD");
        private static readonly System.Windows.Media.SolidColorBrush PillMutedBg   = MakeBrush("#5D8B40");
        private static readonly System.Windows.Media.SolidColorBrush PillMutedDot  = MakeBrush("#C5E1A5");
        private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
        {
            var b = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        // Header account-chip connection dot. Sits on the dark header card (not on
        // a colored pill), so these run a touch more saturated than the pill dots.
        // Only two honest states: green = signed in (community on), grey = signed in
        // but community features toggled off. We deliberately don't show a live
        // "connection" state - that can't be truthful without polling, which the
        // sync-cost rework removed. A revoked/expired session signs out, so that
        // case shows the chip's "Sign in" text, not a dot.
        private static readonly System.Windows.Media.SolidColorBrush ChipDotGreen = MakeBrush("#66BB6A");
        private static readonly System.Windows.Media.SolidColorBrush ChipDotGrey  = MakeBrush("#9E9E9E");

        // State shown by the account-chip dot. Only meaningful while signed in;
        // signed-out hides the dot entirely (the chip reads "Sign in").
        private enum AccountDot { Green, Grey }

        // Cached so the meter tick only repaints the dot on an actual transition.
        // Null while signed out so the next sign-in always applies a fresh state.
        private AccountDot? _lastAccountDot;

        // Caller guarantees signed in. Grey when the user has community features
        // off (offline by choice, not an error); green otherwise.
        private AccountDot ComputeAccountDot()
            => _plugin?.Settings?.CommunityEnabled == true ? AccountDot.Green : AccountDot.Grey;

        private void ApplyAccountDot(AccountDot dot)
        {
            if (AccountChipIcon == null) return;
            // Green = filled, grey = hollow ring. Both are the same Ellipse, so they
            // are exactly the same diameter (the glyph approach mismatched ● vs ◯).
            if (dot == AccountDot.Grey)
            {
                AccountChipIcon.Fill            = System.Windows.Media.Brushes.Transparent;
                AccountChipIcon.Stroke          = ChipDotGrey;
                AccountChipIcon.StrokeThickness = 1.5;
            }
            else
            {
                AccountChipIcon.Fill            = ChipDotGreen;
                AccountChipIcon.Stroke          = null;
                AccountChipIcon.StrokeThickness = 0;
            }
            _lastAccountDot = dot;
        }

        private string BuildAccountChipTooltip(AccountDot dot)
        {
            string email = _plugin?.AuthSignedInEmail;
            string who   = string.IsNullOrEmpty(email) ? "Signed in." : "Signed in as " + email + ".";
            string state = dot == AccountDot.Grey ? " Community features are off." : " Connected.";
            return who + state + " Click for account options.";
        }

        // Live recolor from the meter tick: flips green<->grey when the community
        // toggle changes, without a full RefreshAccountChip. Guarded so it only
        // touches the brush on a real transition. (OnCommunityEnabledChanged also
        // refreshes the chip; this is the cheap backstop for any path that flips
        // the setting without firing that event.)
        private void UpdateAccountChipDotLive()
        {
            if (_plugin == null || AccountChipIcon == null) return;
            if (!_plugin.AuthIsSignedIn) return;                 // chip hidden / "Sign in"
            if (AccountChipIcon.Visibility != Visibility.Visible) return;
            var dot = ComputeAccountDot();
            if (_lastAccountDot == dot) return;
            ApplyAccountDot(dot);
            AccountChipButton.ToolTip = BuildAccountChipTooltip(dot);
        }

        /// <summary>Drives the single colored status pill in the header strip.
        /// Boils all the wheel/stream/telemetry strings down to one of:
        /// Disabled / Wheel not detected / Stream stopped / Ready /
        /// Active / Audio only / Waiting for telemetry. The optional
        /// "✨ Enhanced N Hz" hype badge appears only when a per-game
        /// enhanced telemetry source is actively delivering frames.</summary>
        private void UpdateStatusPill()
        {
            if (_plugin == null) return;

            bool   enabled    = _plugin.PluginEnabled;
            string wheelStr   = _plugin.WheelStatus  ?? "";
            string streamStr  = _plugin.StreamStatus ?? "";
            bool   wheelOk    = !wheelStr.StartsWith("Not detected");
            bool   streamOk   = streamStr.StartsWith("Streaming");
            bool   gameOn     = !string.IsNullOrEmpty(_plugin.ActiveGame);

            var    telSrc     = _plugin.TelemetrySource;
            double hz         = telSrc?.MeasuredHz ?? 0;
            bool   isEnhanced = telSrc?.IsEnhanced ?? false;
            bool   useful     = _plugin.HasUsefulTelemetry;

            string text;
            System.Windows.Media.SolidColorBrush bg, dot;

            if (!enabled)                       { text = "Disabled";              bg = PillGreyBg;  dot = PillGreyDot;  }
            else if (!wheelOk)                  { text = "Wheel not detected";    bg = PillAmberBg; dot = PillAmberDot; }
            else if (!streamOk)                 { text = "Stream stopped";        bg = PillAmberBg; dot = PillAmberDot; }
            else if (!gameOn)
            {
                // SimHub reports no telemetry-emitting game. Before falling back
                // to the green "Ready" idle state (which reads as "no game"),
                // check the real process table: Forza Horizon stops its Data Out
                // the moment you pause, so the game can be very much running
                // while SimHub says otherwise. A live process means paused / in
                // a menu, not back at the desktop.
                if (_plugin.IsKnownGameProcessRunning(out _))
                                                { text = "In menu / paused";      bg = PillAmberBg; dot = PillAmberDot; }
                else                            { text = "Ready";                 bg = PillGreenBg; dot = PillGreenDot; }
            }
            else if (hz > 0 && useful)          { text = "Active";                bg = PillGreenBg; dot = PillGreenDot; }
            else if (hz > 0)                    { text = "Audio only";            bg = PillMutedBg; dot = PillMutedDot; }
            else                                { text = "Waiting for telemetry"; bg = PillAmberBg; dot = PillAmberDot; }

            StatusPillText.Text   = text;
            StatusPill.Background = bg;
            StatusPillDot.Fill    = dot;

            // Enhanced hype badge: only when actually delivering frames from
            // a per-game source (not the generic SimHub pipe), and the plugin
            // is actually doing something with them.
            if (enabled && wheelOk && streamOk && isEnhanced && hz > 0)
            {
                EnhancedBadge.Visibility = Visibility.Visible;
                EnhancedBadgeText.Text   = $"✨ Enhanced · {hz:0} Hz";
            }
            else
            {
                EnhancedBadge.Visibility = Visibility.Collapsed;
            }
        }

        // True once the user clicks "+N more": expands the coalesced issue stack
        // to show every active banner. Auto-resets when the active count drops
        // back to one (or zero), so a transient pile-up doesn't leave the card
        // stuck expanded.
        private bool _showAllIssues;

        // Decide which card-issue banners want to show this tick (G HUB / admin
        // / wheel-quiet / FFB-tap / unverified), set their text, then hand the
        // ordered list to CoalesceCardIssues. Severity order: the two FFB
        // blockers (admin, G HUB) first, then the FFB-tap divergence, then the
        // soft "why is my wheel quiet" diagnostic, then the informational
        // unverified-wheel notice. Called from the meter tick and from the
        // "+N more" toggle so a click re-renders immediately.
        private void RefreshCardIssues()
        {
            string diag = _plugin?.WheelQuietDiagnostic;
            bool wantQuiet = !string.IsNullOrEmpty(diag);

            // When G HUB is running the quiet diagnostic's top cause IS "G HUB is
            // running", which the dedicated red G HUB banner (also in this group)
            // already states, so the card showed the same problem as two warnings.
            // Drop the diagnostic copy in exactly that case; a different, higher-
            // ranked quiet cause (plugin disabled, master gain 0) is not the G HUB
            // string and still shows alongside the banner.
            if (wantQuiet && string.Equals(diag, TrueforcePlugin.GHubQuietDiagnosticMessage, StringComparison.Ordinal))
                wantQuiet = false;

            if (wantQuiet && WheelQuietDiagnosticText != null && WheelQuietDiagnosticText.Text != diag)
                WheelQuietDiagnosticText.Text = diag;

            bool wantGHub  = _plugin?.IsLogitechGHubRunning ?? false;
            bool wantAdmin = _plugin != null && !_plugin.IsRunningElevated;
            bool wantFfb   = _plugin?.ShouldShowFfbTapPickerBanner ?? false;

            string notice = _plugin?.UnverifiedWheelNotice;
            bool wantUnverified = !string.IsNullOrEmpty(notice);
            if (wantUnverified && UnverifiedWheelText != null && UnverifiedWheelText.Text != notice)
                UnverifiedWheelText.Text = notice;

            // Dropped-default notice: a game's default binding pointed at a
            // preset name that no longer exists, so the built-in took over.
            var droppedDefaults = _plugin?.DroppedGameDefaultNotices;
            bool wantDropped = droppedDefaults != null && droppedDefaults.Count > 0;
            if (wantDropped && DefaultDroppedText != null)
            {
                string games = string.Join(", ", droppedDefaults);
                string txt = $"The default preset for {games} couldn't be found (it was renamed or removed), so the built-in default took over. Pick a preset and use Set as default to rebind.";
                if (DefaultDroppedText.Text != txt) DefaultDroppedText.Text = txt;
            }

            CoalesceCardIssues(
                new UIElement[] { AdminWarningBox, GHubWarningBox, FfbTapPickerBanner, WheelQuietDiagnosticBox, UnverifiedWheelBanner, DefaultDroppedBanner },
                new bool[]      { wantAdmin,       wantGHub,       wantFfb,            wantQuiet,                wantUnverified,        wantDropped });
        }

        private void DefaultDroppedDismiss_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.ClearDroppedGameDefaultNotices();
            RefreshCardIssues();
        }

        // Render the coalesced card-issue banners. Shows the highest-severity
        // active banner (array order = severity), collapses the rest, and shows
        // a "+N more" bar when more than one is active. Clicking the bar flips
        // _showAllIssues to reveal (or re-hide) the full stack. banners and
        // wants are parallel arrays of equal length.
        private void CoalesceCardIssues(UIElement[] banners, bool[] wants)
        {
            if (banners == null || wants == null) return;
            int n = Math.Min(banners.Length, wants.Length);

            int activeCount = 0;
            for (int i = 0; i < n; i++) if (wants[i]) activeCount++;

            // Back to one (or zero) issues -> nothing left to expand.
            if (activeCount <= 1) _showAllIssues = false;

            bool shownOne = false;
            for (int i = 0; i < n; i++)
            {
                if (banners[i] == null) continue;
                bool show;
                if (!wants[i])           show = false;
                else if (_showAllIssues) show = true;
                else if (!shownOne)      { show = true; shownOne = true; }
                else                     show = false;
                banners[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (MoreIssuesBar != null)
            {
                if (activeCount > 1 && !_showAllIssues)
                {
                    int more = activeCount - 1;
                    if (MoreIssuesText != null)
                        MoreIssuesText.Text = more == 1 ? "+1 more issue ▾" : $"+{more} more issues ▾";
                    MoreIssuesBar.Visibility = Visibility.Visible;
                }
                else if (activeCount > 1 && _showAllIssues)
                {
                    if (MoreIssuesText != null) MoreIssuesText.Text = "Show fewer ▴";
                    MoreIssuesBar.Visibility = Visibility.Visible;
                }
                else
                {
                    MoreIssuesBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        // "+N more" / "Show fewer" toggle for the coalesced issues region.
        private void MoreIssues_Click(object sender, MouseButtonEventArgs e)
        {
            _showAllIssues = !_showAllIssues;
            RefreshCardIssues(); // re-render now instead of waiting for the next tick
        }

        // Apply() is called by per-effect handlers AFTER the _suppressEvents
        // guard, so reaching it implies a real user change → push to live
        // device and recompute the affected effect's dirty state. Per-car
        // file is NOT auto-written: saves are explicit (Save… → For this car).
        // First touch on an effect also clears its "NEW" badge.
        private void Apply(EffectKind which)
        {
            _plugin.ApplyActiveCarOverride();
            MarkEffectDirty(which);
            string id = EffectIdFor(which);
            if (id != null && _plugin.IsEffectUnseen(id))
            {
                _plugin.MarkEffectSeen(id);
                RefreshNewBadges();
            }
        }

        // Effect-ID strings shared with EffectChangelog.KnownEffectIds and
        // the per-effect SeenEffects entries. Null for global-only sections
        // (Master, Ducking, SpikeReduction) that don't get a NEW badge.
        private static string EffectIdFor(EffectKind which)
        {
            switch (which)
            {
                case EffectKind.Audio:      return "Audio";
                case EffectKind.Engine:     return "Engine";
                case EffectKind.Bumps:      return "Bumps";
                case EffectKind.Traction:   return "Traction";
                case EffectKind.Shift:      return "Shift";
                case EffectKind.Abs:        return "Abs";
                case EffectKind.PitLimiter: return "PitLimiter";
                case EffectKind.Drs:        return "Drs";
                case EffectKind.Collision:  return "Collision";
                case EffectKind.RevLimiter: return "RevLimiter";
                case EffectKind.AxleSlip:     return "AxleSlip";
                case EffectKind.KerbThump:    return "KerbThump";
                case EffectKind.LockupJudder: return "LockupJudder";
                default:                    return null;
            }
        }

        // Maps an effect-ID string to its NEW-badge Border in the header.
        // Returns null for unknown IDs.
        private System.Windows.Controls.Border GetNewBadge(string effectId)
        {
            switch (effectId)
            {
                case "Audio":      return AudioNewBadge;
                case "Engine":     return EngineNewBadge;
                case "Bumps":      return BumpsNewBadge;
                case "Traction":   return TractionNewBadge;
                case "Shift":      return ShiftNewBadge;
                case "Abs":        return AbsNewBadge;
                case "PitLimiter": return PitLimiterNewBadge;
                case "Drs":        return DrsNewBadge;
                case "Collision":  return CollisionNewBadge;
                case "RevLimiter": return RevLimiterNewBadge;
                case "Airborne":   return AirborneNewBadge;
                case "AxleSlip":     return AxleSlipNewBadge;
                case "KerbThump":    return KerbThumpNewBadge;
                case "LockupJudder": return LockupJudderNewBadge;
                case "ImplementThud": return ImplementThudNewBadge;
                default:           return null;
            }
        }

        /// <summary>Recompute every NEW badge's visibility from the
        /// plugin's SeenEffects state. Called on construction, after
        /// MarkEffectSeen, and from RefreshFromPlugin so external state
        /// changes (e.g. import bringing in a SeenEffects snapshot) refresh
        /// the visible chrome.</summary>
        private void RefreshNewBadges()
        {
            if (_plugin == null) return;
            foreach (var id in EffectChangelog.KnownEffectIds)
            {
                var badge = GetNewBadge(id);
                if (badge == null) continue;
                badge.Visibility = _plugin.IsEffectUnseen(id)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

        /// <summary>Fires once when the user opens an effect's expander.
        /// Counts as an "I've seen this" interaction and clears the NEW
        /// badge for that section. Routes via the Expander.Name suffix
        /// ("EngineExpander" → "Engine") to keep the XAML hookups one-shot.</summary>
        private void EffectExpander_Expanded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var exp = sender as System.Windows.Controls.Expander;
            if (exp == null) return;
            string name = exp.Name ?? "";
            const string suffix = "Expander";
            if (!name.EndsWith(suffix)) return;
            string id = name.Substring(0, name.Length - suffix.Length);
            if (!_plugin.IsEffectUnseen(id)) return;
            _plugin.MarkEffectSeen(id);
            RefreshNewBadges();
        }

        /// <summary>Show / hide the "What's new" banner based on whether
        /// the running build is newer than the user's stamped LastSeenVersion.
        /// Header reads "What's new in v{CurrentVersion}". Idempotent. Called
        /// from RefreshFromPlugin.</summary>
        private void RefreshChangelogBanner()
        {
            if (_plugin == null || WhatsNewBanner == null) return;
            bool show = _plugin.HasUnseenChangelog;
            var want = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (WhatsNewBanner.Visibility != want) WhatsNewBanner.Visibility = want;
            if (show && WhatsNewBannerText != null)
            {
                var curr = _plugin.UpdateChecker?.CurrentVersion;
                if (curr != null)
                {
                    string desired = "What's new in v" + curr.ToString(3);
                    if (WhatsNewBannerText.Text != desired) WhatsNewBannerText.Text = desired;
                }
            }
        }

        // ---- Word-of-mouth banner ----------------------------------------

        private const string ShareProjectUrl =
            "https://github.com/Mhytee/Trueforce-For-All";

        // Nexus Mods is by far the highest-traffic distribution channel
        // (~3500 views vs handfuls on the other mirrors), so it's the
        // primary public-facing landing page when someone wants to send a
        // link rather than make a post.
        private const string ShareNexusUrl =
            "https://www.nexusmods.com/forzahorizon6/mods/34";

        // ---------------- support prompt ----------------
        //
        // Shown when the user navigates INTO the plugin page, never anywhere else
        // in SimHub, and never mid-session (the plugin's own gate refuses while a
        // game is running). Pacing, the ever-supported latch and every other guard
        // live in TrueforcePlugin.ShouldShowSupportPrompt; this is only the trigger.
        private bool _supportPromptShownThisRun;

        // Stand-in hour count for the SUPPORT dev preview when nothing is banked.
        private const int SupportPromptPreviewHours = 40;
        // Ceiling for the displayed hours, well past any real odometer.
        private const double MaxDisplayHours = 1000000.0;

        private void OnSupportPromptVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || _supportPromptShownThisRun) return;
            if (_plugin == null || !_plugin.ShouldShowSupportPrompt) return;
            _supportPromptShownThisRun = true;
            // Let the page finish rendering first: a modal thrown up during the
            // visibility change itself lands on a half-drawn tab.
            Dispatcher.BeginInvoke(new Action(() => ShowSupportPrompt()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Shows the support modal and records how it was answered.
        /// recordPacing:false is the dev preview, which leaves the ladder alone.</summary>
        private void ShowSupportPrompt(bool recordPacing = true)
        {
            if (_plugin == null) return;
            // Clamp before the cast. The SHARE dev code parks the odometer at
            // double.MaxValue, and casting that to int overflows to a negative,
            // which would silently drop the hours line instead of showing it.
            double banked = (_plugin.Settings?.ActiveStreamingSeconds ?? 0.0) / 3600.0;
            if (!(banked > 0.0)) banked = 0.0;                 // also catches NaN
            if (banked > MaxDisplayHours) banked = MaxDisplayHours;
            int hours = (int)banked;
            // The preview always shows the earned-hours line, even on a machine with
            // no banked seat time, since checking that line renders is most of the
            // point of previewing. Real hours win whenever there are any.
            if (!recordPacing && hours < 1) hours = SupportPromptPreviewHours;
            var win = new SupportPromptWindow(hours);
            try { win.Owner = Window.GetWindow(this); } catch { }
            try { win.ShowDialog(); } catch { return; }
            if (recordPacing) _plugin.NoteSupportPromptShown(declined: !win.WentToPatreon);
        }

        /// <summary>Shows the one-and-done word-of-mouth banner once the user
        /// has banked enough working seat time. Idempotent; safe to call
        /// every RefreshFromPlugin.</summary>
        private void RefreshShareCtaBanner()
        {
            if (_plugin == null || ShareCtaBanner == null) return;
            bool show = _plugin.ShouldShowShareCta;
            var want = show ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
            if (ShareCtaBanner.Visibility != want) ShareCtaBanner.Visibility = want;
        }

        private void ShareCtaBanner_Click(object sender, MouseButtonEventArgs e)
        {
            // One-and-done: opening the dialog from the banner latches the
            // banner off whether or not the user shares. The persistent
            // "Spread the word" row at the bottom stays available forever.
            ShowShareDialog();
            _plugin?.DismissShareCta();
            RefreshShareCtaBanner();
        }

        private void ShareCtaDismiss_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissShareCta();
            RefreshShareCtaBanner();
        }

        // "FFB compatibility reports" Discussions category. Slug is GitHub's
        // lowercase-hyphenated form of the category name. If the slug ever
        // 404s (category renamed/removed), GitHub lands the user on the
        // discussions/new chooser, which still works, just unfiltered.
        private const string FfbReportDiscussionsBase =
            "https://github.com/Mhytee/Trueforce-For-All/discussions/new?category=ffb-compatibility-reports";

        private void RefreshExperimentalSuccessBanner()
        {
            if (_plugin == null || ExperimentalSuccessBanner == null) return;
            var want = _plugin.ShouldShowExperimentalSuccessReport
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            if (ExperimentalSuccessBanner.Visibility != want) ExperimentalSuccessBanner.Visibility = want;
        }

        // "Yes, it's working": the human confirms FFB actually works, so this
        // is a real, attributable success. Open the prefilled report and latch.
        private void ExperimentalSuccessYes_Click(object sender, RoutedEventArgs e)
        {
            OpenFfbCompatibilityReport();
            _plugin?.DismissExperimentalSuccessReport();   // one-and-done, like the share CTA
            RefreshExperimentalSuccessBanner();
        }

        // "No, still no FFB": our capture signal was a false positive (or FFB is
        // there but wrong). Don't generate a success report; send them to the
        // diagnostics/troubleshooter instead, and latch so we stop asking.
        private void ExperimentalSuccessNo_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissExperimentalSuccessReport();
            RefreshExperimentalSuccessBanner();
            // Jump to the Settings tab with Diagnostics expanded: self-test,
            // the experimental toggle, manual device picker and USBPcap
            // reinstall all live there.
            if (DiagnosticsExpander != null) DiagnosticsExpander.IsExpanded = true;
            OpenAdvancedSettings_Click(this, null);
        }

        private void ExperimentalSuccessDismiss_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.DismissExperimentalSuccessReport();
            RefreshExperimentalSuccessBanner();
        }

        // Open a prefilled compatibility-report discussion. VID/PID, game,
        // version and the capture fingerprint are filled in automatically so
        // the report carries exactly what we need to graduate a wheel out of
        // experimental; the user just hits submit. Framed as a success report,
        // not a bug, so it isn't mistaken for an issue.
        private void OpenFfbCompatibilityReport()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            string game    = _plugin?.ActiveGame ?? "(none)";
            string wheel   = _plugin?.WheelStatus ?? "(unknown)";
            string vidpid  = (_plugin != null && (_plugin.HidWheelVid != 0 || _plugin.HidWheelPid != 0))
                ? $"{_plugin.HidWheelVid:X4}:{_plugin.HidWheelPid:X4}" : "unknown";
            string fp      = _plugin?.CaptureFingerprint ?? "(not confirmed)";

            string title = $"[FFB report] {vidpid} on {game}";
            string body  =
                  "Experimental FFB detection fixed my wheel. (Success report, not a bug.)\n\n"
                + $"- Wheel: {wheel}  ({vidpid})\n"
                + $"- Game: {game}\n"
                + $"- Plugin: {version}\n"
                + $"- Capture: {fp}\n\n"
                + "Anything else worth noting (other games tested, what wasn't working before): \n\n"
                + "(Optional but very helpful: use Settings > Diagnostics > Export logs and drag the zip in here.)\n";

            string url = FfbReportDiscussionsBase
                       + "&title=" + Uri.EscapeDataString(title)
                       + "&body="  + Uri.EscapeDataString(body);
            OpenUrl(url);
        }

        // Persistent bottom-of-panel row. Same actions as the dialog, always
        // available, never touches the one-and-done latch.
        private void SharePanelButton_Click(object sender, RoutedEventArgs e) => ShowShareDialog();

        // ---- Share targets -----------------------------------------------
        //
        // All web-intent targets pass only the URL, never prefilled body
        // text. Auto-filled marketing copy reads as spam to the user's
        // followers and turns people off. (A "Copy a ready-to-paste post"
        // button was considered as an opt-in for the longer blurb, but
        // intentionally dropped: the bare link-outs are the whole surface.)
        //
        // Reddit: the maintainer's own Reddit account is banned (see the
        // README note), but third parties posting on the project's behalf
        // is exactly the gap this share dialog fills. We use `selftext=true`
        // to force the text-post composer (link-type submissions read as
        // low-effort drive-by spam on most subreddits) and seed the body
        // with just the URL so a preview/embed renders. Title and any
        // additional context stay empty so the poster writes their own.

        private static string XIntentUrl =>
            "https://twitter.com/intent/tweet?url="
            + Uri.EscapeDataString(ShareProjectUrl);

        private static string RedditSubmitUrl =>
            "https://www.reddit.com/submit?selftext=true&text="
            + Uri.EscapeDataString(ShareProjectUrl);

        private static string FacebookShareUrl =>
            "https://www.facebook.com/sharer/sharer.php?u="
            + Uri.EscapeDataString(ShareProjectUrl);

        /// <summary>Themed share dialog. Built in code (like the What's new
        /// modal) so it paints in SimHub's dark chrome instead of falling
        /// back to the white system default.</summary>
        // Official single-path brand logos (Simple Icons, CC0), embedded as
        // vector geometry so the share dialog ships zero image assets. 24x24
        // viewBox. The "F1 " prefix selects nonzero fill to match the SVG
        // winding rule so the logo cut-outs render correctly (WPF Path defaults
        // to even-odd, which would invert some of the holes).
        private const string XLogoPath = "F1 M14.234 10.162 22.977 0h-2.072l-7.591 8.824L7.251 0H.258l9.168 13.343L.258 24H2.33l8.016-9.318L16.749 24h6.993zm-2.837 3.299-.929-1.329L3.076 1.56h3.182l5.965 8.532.929 1.329 7.754 11.09h-3.182z";
        private const string RedditLogoPath = "F1 M12 0C5.373 0 0 5.373 0 12c0 3.314 1.343 6.314 3.515 8.485l-2.286 2.286C.775 23.225 1.097 24 1.738 24H12c6.627 0 12-5.373 12-12S18.627 0 12 0Zm4.388 3.199c1.104 0 1.999.895 1.999 1.999 0 1.105-.895 2-1.999 2-.946 0-1.739-.657-1.947-1.539v.002c-1.147.162-2.032 1.15-2.032 2.341v.007c1.776.067 3.4.567 4.686 1.363.473-.363 1.064-.58 1.707-.58 1.547 0 2.802 1.254 2.802 2.802 0 1.117-.655 2.081-1.601 2.531-.088 3.256-3.637 5.876-7.997 5.876-4.361 0-7.905-2.617-7.998-5.87-.954-.447-1.614-1.415-1.614-2.538 0-1.548 1.255-2.802 2.803-2.802.645 0 1.239.218 1.712.585 1.275-.79 2.881-1.291 4.64-1.365v-.01c0-1.663 1.263-3.034 2.88-3.207.188-.911.993-1.595 1.959-1.595Zm-8.085 8.376c-.784 0-1.459.78-1.506 1.797-.047 1.016.64 1.429 1.426 1.429.786 0 1.371-.369 1.418-1.385.047-1.017-.553-1.841-1.338-1.841Zm7.406 0c-.786 0-1.385.824-1.338 1.841.047 1.017.634 1.385 1.418 1.385.785 0 1.473-.413 1.426-1.429-.046-1.017-.721-1.797-1.506-1.797Zm-3.703 4.013c-.974 0-1.907.048-2.77.135-.147.015-.241.168-.183.305.483 1.154 1.622 1.964 2.953 1.964 1.33 0 2.47-.81 2.953-1.964.057-.137-.037-.29-.184-.305-.863-.087-1.795-.135-2.769-.135Z";
        private const string FacebookLogoPath = "F1 M9.101 23.691v-7.98H6.627v-3.667h2.474v-1.58c0-4.085 1.848-5.978 5.858-5.978.401 0 .955.042 1.468.103a8.68 8.68 0 0 1 1.141.195v3.325a8.623 8.623 0 0 0-.653-.036 26.805 26.805 0 0 0-.733-.009c-.707 0-1.259.096-1.675.309a1.686 1.686 0 0 0-.679.622c-.258.42-.374.995-.374 1.752v1.297h3.919l-.386 2.103-.287 1.564h-3.246v8.245C19.396 23.238 24 18.179 24 12.044c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.628 3.874 10.35 9.101 11.647Z";
        private const string GithubLogoPath = "F1 M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12";
        private const string NexusLogoPath = "F1 M17.376 0c-.993 0-2.18.686-2.907 1.182-1.676-.36-4.036-.545-6.787.635-1.365-.513-2.425-.562-3.32-.488a2.16 2.16 0 0 0-1.27.429c-.33.22-2.788 2.69-3.069 4.652C-.15 7.508.68 8.932 1.218 9.718c-.44 1.76-.2 4.572.517 6.188-.353 1.041-.713 2.089-.664 3.205.01.584.061 1.188.398 1.684C1.72 21.19 4.528 24 6.545 24c.957 0 1.93-.428 3.07-1.24 2.16.383 4.402.348 6.448-.532 2.573 1.001 4.224.625 4.84.162.587-.457 2.826-2.915 3.07-4.622.1-.672-.023-1.638-1.226-3.397a10.983 10.983 0 0 0-.501-6.455c.396-1.069.673-2.188.59-3.337-.015-.68-.221-1.167-.487-1.507-.209-.335-2.415-2.39-4.028-2.91A3.105 3.105 0 0 0 17.376 0m-.03 2.082c.65.015 2.155 1.093 3.01 1.906l.355.34c-.959-.163-2.125.428-3.26 1.55a10.28 10.28 0 0 0-1.358 1.595c-.28.384-.517.768-.753 1.285l1.18.635-3.895 1.477-1.122-4.18 1.033.547c1.358-3.102 2.524-3.973 3.232-4.416h.015a5.12 5.12 0 0 1 1.49-.724zM12 3.065a8.932 8.932 0 0 1 2.22.279 7.67 7.67 0 0 0-.42.488 8.403 8.403 0 0 0-1.8-.196 8.336 8.336 0 0 0-5.897 2.432 7.86 7.86 0 0 1-.37-.433A8.905 8.905 0 0 1 12 3.065m-7.076.305c.71-.002 1.309.127 2.2.466a9.526 9.526 0 0 0-1.713 1.337c-.327-.542-.624-1.156-.488-1.803m-.606.042c-.162.96.428 2.126 1.55 3.264.457.487 1.003.945 1.594 1.358.383.281.767.517 1.283.754l.62-1.182 1.49 3.914-4.176 1.122.546-1.033c-3.099-1.36-3.969-2.526-4.412-3.235v-.015a5.144 5.144 0 0 1-.723-1.491l-.015-.074c.015-.65 1.092-2.156 1.904-3.013Zm16.035 1.483a1.259 1.259 0 0 1 .26.015l.14.023a5.05 5.05 0 0 1-.13 1.137v.015c-.1.383-.228.765-.377 1.148a9.526 9.526 0 0 0-1.346-1.776c.547-.357 1.051-.546 1.453-.562M18.43 5.8a8.903 8.903 0 0 1 2.506 6.2 8.937 8.937 0 0 1-.27 2.183 7.658 7.658 0 0 0-.488-.425A8.407 8.407 0 0 0 20.364 12 8.334 8.334 0 0 0 18 6.173a7.904 7.904 0 0 1 .429-.373M3.315 9.905c.157.148.319.29.488.425A8.417 8.417 0 0 0 3.636 12c0 2.248.887 4.286 2.327 5.788a8.11 8.11 0 0 1-.426.376A8.902 8.902 0 0 1 3.065 12a8.937 8.937 0 0 1 .25-2.095m13.988 1.541-.546 1.034c3.098 1.359 3.969 2.526 4.412 3.235v.014c.34.488.575.99.723 1.492l.014.074c-.014.65-1.092 2.156-1.903 3.013l-.34.354c.163-.96-.427-2.127-1.549-3.264a10.298 10.298 0 0 0-1.594-1.359 7.008 7.008 0 0 0-1.283-.753l-.605 1.152-1.505-3.87zm-6.006 1.684 1.121 4.18-1.033-.547c-1.357 3.102-2.523 3.973-3.231 4.416h-.015c-.487.34-.989.576-1.49.724l-.074.015c-.65-.015-2.154-1.093-3.01-1.906l-.354-.34c.959.163 2.124-.428 3.26-1.55.488-.458.945-1.004 1.358-1.595.28-.384.517-.768.753-1.285l-1.166-.635ZM3.72 16.663A9.526 9.526 0 0 0 5.086 18.5c-.697.47-1.33.665-1.777.59l-.138-.024c0-.367.038-.748.128-1.137v-.015c.11-.417.254-.835.42-1.252m14.131 1.314c.129.14.253.283.372.43A8.904 8.904 0 0 1 12 20.936a8.932 8.932 0 0 1-2.282-.296 7.757 7.757 0 0 0 .417-.487 8.335 8.335 0 0 0 7.716-2.175m.696.889c.43.666.607 1.267.534 1.698l-.023.138a5.034 5.034 0 0 1-1.136-.128h-.014a10.718 10.718 0 0 1-1.114-.366 9.526 9.526 0 0 0 1.753-1.342";

        private void ShowShareDialog()
        {
            var win = new Window
            {
                Title = "Spread the word",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "Help another driver find TF4ALL",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Almost nobody knows this plugin exists. A YouTube video, "
                     + "a Reddit post, or a link to a friend is the best way to "
                     + "help get the word out.",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });

            // Brand-colored cards with the platform's own logo. Built as
            // Borders (not Buttons) so SimHub's stock theme can't override the
            // brand fills; the label is handed to the click action so the
            // copy-link rows can flash "Link copied" feedback in place.
            Border MakeShareRow(string label, string geometry, Brush bg, Brush border, Action<TextBlock> onClick)
            {
                var icon = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(geometry),
                    Fill = System.Windows.Media.Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 17, Height = 17,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                };
                var lbl = new TextBlock
                {
                    Text = label,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 13, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var rowSp = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 14, 0),
                };
                rowSp.Children.Add(icon);
                rowSp.Children.Add(lbl);
                var bd = new Border
                {
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(border == null ? 0 : 1),
                    CornerRadius = new CornerRadius(6),
                    Height = 40,
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = rowSp,
                };
                bd.MouseLeftButtonUp += (_, __) => onClick(lbl);
                bd.MouseEnter += (_, __) => bd.Opacity = 0.88;
                bd.MouseLeave += (_, __) => bd.Opacity = 1.0;
                return bd;
            }

            void CopyLink(string url, TextBlock label)
            {
                string original = label.Text;
                // Retry-based copy; on a genuine failure say so instead of
                // claiming "Link copied" over an empty clipboard.
                label.Text = TryCopyToClipboard(url) ? "Link copied" : "Copy failed, try again";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                t.Tick += (_, __) => { label.Text = original; t.Stop(); };
                t.Start();
            }

            var darkBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            root.Children.Add(MakeShareRow("Post on X", XLogoPath,
                new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)), darkBorder,
                _ => OpenUrl(XIntentUrl)));
            root.Children.Add(MakeShareRow("Post on Reddit", RedditLogoPath,
                new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x00)), null,
                _ => OpenUrl(RedditSubmitUrl)));
            root.Children.Add(MakeShareRow("Share on Facebook", FacebookLogoPath,
                new SolidColorBrush(Color.FromRgb(0x18, 0x77, 0xF2)), null,
                _ => OpenUrl(FacebookShareUrl)));
            root.Children.Add(MakeShareRow("Copy GitHub link", GithubLogoPath,
                new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x23)), darkBorder,
                lbl => CopyLink(ShareProjectUrl, lbl)));
            root.Children.Add(MakeShareRow("Copy Nexus link", NexusLogoPath,
                new SolidColorBrush(Color.FromRgb(0xC7, 0x7D, 0x33)), null,
                lbl => CopyLink(ShareNexusUrl, lbl)));

            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "Close",
                Width = 90,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
                IsCancel = true,
            };
            closeBtn.Click += (_, __) => win.Close();
            root.Children.Add(closeBtn);

            win.Content = root;
            win.ShowDialog();
        }

        private void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;
            UpdateHeaderPresetDisplay();
        }

        private void ClearDirty()
        {
            // Always cascade through per-section dirty too: every place that
            // calls ClearDirty (preset apply / save / import / game change)
            // implies a fresh baseline for the entire state, not just the
            // global-drift indicator.
            ClearAllEffectDirty();
            if (!_dirty) return;
            _dirty = false;
            UpdateHeaderPresetDisplay();
        }

        /// <summary>Recompute the section's dirty state by asking the plugin
        /// whether its values still match the active preset's snapshot. This
        /// replaces sticky-bit MarkEffectDirty so that changing a value and
        /// changing it back clears the dirty indicator.</summary>
        private void MarkEffectDirty(EffectKind which)
        {
            bool dirty;
            var kind = (TrueforcePlugin.SectionKind)(int)which;
            if (_plugin == null) dirty = true;
            else if (!_plugin.SectionHasAnchor(kind))
            {
                // No game-preset snapshot AND no per-car override anchor:
                // fall back to sticky-true so a global edit without a saved
                // baseline still surfaces as unsaved.
                dirty = true;
            }
            else
            {
                dirty = _plugin.IsSectionDirty(kind);
            }

            if (_effectDirty[(int)which] == dirty) return;
            _effectDirty[(int)which] = dirty;

            var saveBtn   = GetEffectSaveBtn(which);
            var revertBtn = GetEffectRevertBtn(which);
            if (saveBtn != null) saveBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            // Revert only makes sense with an anchor.
            if (revertBtn != null)
                revertBtn.Visibility = (dirty && !string.IsNullOrEmpty(_plugin?.ActivePresetName))
                    ? Visibility.Visible : Visibility.Collapsed;

            // Recompute global dirty from the per-section bits.
            UpdateGlobalDirtyFromEffects();
        }

        /// <summary>Recompute every section's dirty state from the plugin.
        /// Called from the end of RefreshFromPlugin (so override toggles,
        /// game/car switches, preset apply, etc. all sync the per-section
        /// indicators correctly without each handler having to enumerate).</summary>
        private void RecomputeAllEffectDirty()
        {
            if (_plugin == null) return;
            bool hasPreset = !string.IsNullOrEmpty(_plugin.ActivePresetName);
            for (int i = 0; i < _effectDirty.Length; i++)
            {
                var kind = (TrueforcePlugin.SectionKind)i;
                bool dirty;
                if (!_plugin.SectionHasAnchor(kind))
                {
                    // No anchor: preserve sticky bit so a no-preset edit
                    // doesn't get auto-cleared by a refresh.
                    dirty = _effectDirty[i];
                }
                else
                {
                    dirty = _plugin.IsSectionDirty(kind);
                }
                if (_effectDirty[i] == dirty) continue;
                _effectDirty[i] = dirty;
                var saveBtn   = GetEffectSaveBtn((EffectKind)i);
                var revertBtn = GetEffectRevertBtn((EffectKind)i);
                if (saveBtn   != null) saveBtn.Visibility   = dirty ? Visibility.Visible : Visibility.Collapsed;
                if (revertBtn != null) revertBtn.Visibility = (dirty && hasPreset) ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateGlobalDirtyFromEffects();
        }

        private void UpdateGlobalDirtyFromEffects()
        {
            // Non-car-specific drift: any per-section bit differs from the
            // active game preset. _effectDirty is indexed 1:1 with SectionKind,
            // so this also covers the inherently non-per-car sections (Master,
            // Ducking, Spike reduction) whose changes only ever belong at the
            // game/global level, not a car. (Sticky-true when no game preset is
            // active, so unsaved tuning still surfaces.)
            int dirtyCount = 0;
            for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) dirtyCount++;
            bool gameDirty = dirtyCount > 0;

            // Car-level drift, checked independently: a car-preset-only edit
            // might not light any per-section bit (those compare against the
            // game preset). IsActiveCarPresetDirty compares live vs saved car
            // override directly and stays accurate either way.
            bool carDetected = !string.IsNullOrEmpty(_plugin?.ActiveCarId);
            bool carDirty = carDetected && (_plugin?.IsActiveCarPresetDirty() ?? false);

            bool any = gameDirty || carDirty;
            if (any != _dirty)
            {
                _dirty = any;
                UpdateHeaderPresetDisplay();
            }

            // Contextual "Save all" buttons in the header (these replaced the
            // old bottom global Save). They save every changed section at once
            // and are in addition to the per-section Save buttons.
            //   * Car-side button shows on car-specific drift; saves to the
            //     active car's override.
            //   * Game-side button shows on ANY unsaved tuning, because the
            //     game default is a valid target for two things: non-car-
            //     specific changes (Master/Ducking/Spike + game-preset drift)
            //     AND promoting the CURRENT car's settings up to be the game
            //     default. So whenever the car side is dirty, the game side is
            //     offered too (the popover then lets you pick car / game /
            //     both).
            // During car-edit the loaded game preset is only a temporary baseline,
            // so the GAME-side header actions are suppressed (saving to them would
            // be wrong); the CAR-side Save buttons stay, since saving the car is
            // exactly the point.
            bool carEdit = _plugin?.IsOfflineEditingCar == true;
            // "Save all" only when more than one section changed; a lone edit
            // reads better as plain "Save" ("all" implies a batch).
            string saveLabel = dirtyCount > 1 ? "★ Save all" : "★ Save";
            if (HeaderGameSaveAllBtn != null)
            {
                HeaderGameSaveAllBtn.Visibility = (any && !carEdit) ? Visibility.Visible : Visibility.Collapsed;
                HeaderGameSaveAllBtn.Content = saveLabel;
            }
            if (HeaderCarSaveAllBtn != null)
            {
                HeaderCarSaveAllBtn.Visibility = carDirty ? Visibility.Visible : Visibility.Collapsed;
                HeaderCarSaveAllBtn.Content = saveLabel;
            }

            // "Save as new…" mirrors its Save-all neighbour: it only makes sense
            // when there's unsaved tuning to capture under a new name.
            if (HeaderSaveAsNewBtn != null)
                HeaderSaveAsNewBtn.Visibility = (any && !carEdit) ? Visibility.Visible : Visibility.Collapsed;
            if (HeaderCarSaveAsNewBtn != null)
                HeaderCarSaveAsNewBtn.Visibility = carDirty ? Visibility.Visible : Visibility.Collapsed;

            UpdateHeaderShareButtons();
        }

        // Gates the Share... buttons next to the game-preset combo and
        // the car-preset combo. Both stay hidden unless community is on
        // AND the user is the eligible sharer of what's active:
        //   * built-in presets are never shareable (already ship to
        //     everyone)
        //   * presets the user downloaded from someone else's
        //     community upload are not shareable as their own
        //     (they'd be claiming someone else's work). Identity is
        //     by stable CommunitySourceId on the snapshot/override,
        //     not by local name -- rename/duplicate don't re-enable
        //     Share. The original uploader can re-share via their
        //     own library; downstream users can re-bundle inside a
        //     pack only when the author opted in via allow_in_packs.
        private void UpdateHeaderShareButtons()
        {
            // Option C: the card's Share buttons no longer hinge on community/sign-in
            // (the click funnel handles those). They show whenever the user keeps them
            // on and a preset is present; per-preset reasons still disable them.
            bool showCardShare = _plugin?.Settings?.ShowEffectsTabShareButtons != false;

            if (HeaderGameShareBtn != null)
            {
                string activePreset = _plugin?.ActivePresetName ?? "";
                bool present     = !string.IsNullOrEmpty(activePreset);
                bool isBuiltin   = present && (_plugin?.IsBuiltinPreset(activePreset) ?? false);
                bool isCommunity = present && IsActiveGamePresetCommunitySourced(activePreset);
                // Resolve the snapshot once so we can read both the
                // CommunitySourceId gate above and the upload-tracking
                // stamps written at successful upload time.
                GameSettingsSnapshot headerGameSnap = null;
                if (present && _plugin?.Settings?.Presets != null)
                    _plugin.Settings.Presets.TryGetValue(activePreset, out headerGameSnap);
                bool headerGameHasPriorUpload = headerGameSnap != null
                    && !string.IsNullOrEmpty(headerGameSnap.CommunityUploadedById);
                bool headerGameMatchesUpload = false;
                if (headerGameHasPriorUpload
                    && !string.IsNullOrEmpty(headerGameSnap.CommunityUploadedBodyHash))
                {
                    string headerGameCurrentHash = PresetBodyHasher.ComputeGameSnapshotBodyHash(headerGameSnap);
                    headerGameMatchesUpload = string.Equals(
                        headerGameCurrentHash, headerGameSnap.CommunityUploadedBodyHash, StringComparison.Ordinal);
                }
                bool shareable = present && !isBuiltin && !isCommunity && !headerGameMatchesUpload;
                // Visible-but-disabled with a tooltip explains why Share is greyed
                // out (built-in vs downloaded). Hidden entirely only when the user
                // turned the card's Share buttons off or no preset is active.
                if (showCardShare && present)
                {
                    HeaderGameShareBtn.Visibility = Visibility.Visible;
                    HeaderGameShareBtn.IsEnabled  = shareable;
                    if (isBuiltin)
                        HeaderGameShareBtn.ToolTip = "This is a built-in preset and ships with the plugin. There's no need to re-share it.";
                    else if (isCommunity)
                        HeaderGameShareBtn.ToolTip = "Shared by another driver. Duplicate to make your own version and share that.";
                    else if (headerGameMatchesUpload)
                        HeaderGameShareBtn.ToolTip = $"This matches your last upload ({headerGameSnap.CommunityUploadedVersion ?? "v1"}). Edit it to share an update.";
                    else if (headerGameHasPriorUpload)
                        HeaderGameShareBtn.ToolTip = "Update your last upload or share as new (click to choose).";
                    else
                        HeaderGameShareBtn.ToolTip = "Share this game preset with the community.";
                }
                else
                {
                    HeaderGameShareBtn.Visibility = Visibility.Collapsed;
                }
            }

            if (HeaderCarShareBtn != null)
            {
                var pick = HeaderCarPresetCombo?.SelectedItem
                    is System.Windows.Controls.ComboBoxItem ci
                    ? ci.Tag as PresetPick : null;
                bool present = pick != null && pick.IsCar
                    && !pick.ClearCar
                    && !string.IsNullOrEmpty(pick.Name)
                    && !string.IsNullOrEmpty(pick.CarId);
                bool isBuiltin   = present && IsCarPresetBuiltin(pick.CarId, pick.Name);
                bool isCommunity = present && IsCarPresetCommunitySourced(pick.CarId, pick.Name);
                // Resolve the entry's CarOverride so we can read the
                // upload-tracking stamps written at successful upload time.
                CarOverride headerCarOvr = null;
                if (present)
                {
                    var perCar = _plugin?.GetCarPresets(pick.CarId);
                    if (perCar != null && perCar.TryGetValue(pick.Name, out var headerCarEntry)
                        && headerCarEntry?.Override != null)
                        headerCarOvr = headerCarEntry.Override;
                }
                bool headerCarHasPriorUpload = headerCarOvr != null
                    && !string.IsNullOrEmpty(headerCarOvr.CommunityUploadedById);
                bool headerCarMatchesUpload = false;
                if (headerCarHasPriorUpload
                    && !string.IsNullOrEmpty(headerCarOvr.CommunityUploadedBodyHash))
                {
                    string headerCarCurrentHash = PresetBodyHasher.ComputeCarOverrideHash(headerCarOvr);
                    headerCarMatchesUpload = string.Equals(
                        headerCarCurrentHash, headerCarOvr.CommunityUploadedBodyHash, StringComparison.Ordinal);
                }
                bool shareable = present && !isBuiltin && !isCommunity && !headerCarMatchesUpload;
                if (showCardShare && present)
                {
                    HeaderCarShareBtn.Visibility = Visibility.Visible;
                    HeaderCarShareBtn.IsEnabled  = shareable;
                    if (isBuiltin)
                        HeaderCarShareBtn.ToolTip = "This is a built-in car preset and ships with the plugin. Duplicate it to make your own version, then share that.";
                    else if (isCommunity)
                        HeaderCarShareBtn.ToolTip = "Shared by another driver. Duplicate to make your own version and share that.";
                    else if (headerCarMatchesUpload)
                        HeaderCarShareBtn.ToolTip = $"This matches your last upload ({headerCarOvr.CommunityUploadedVersion ?? "v1"}). Edit it to share an update.";
                    else if (headerCarHasPriorUpload)
                        HeaderCarShareBtn.ToolTip = "Update your last upload or share as new (click to choose).";
                    else
                        HeaderCarShareBtn.ToolTip = "Share this car preset with the community.";
                }
                else
                {
                    HeaderCarShareBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        // True when the named game preset's snapshot carries a
        // CommunitySourceId (i.e. it arrived via community download
        // or pack import). Identity walks the in-memory snapshot;
        // rename/duplicate that doesn't touch CommunitySourceId stays
        // gated, closing the rename-to-reshare permission hole.
        private bool IsActiveGamePresetCommunitySourced(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            if (_plugin?.Settings?.Presets == null) return false;
            return _plugin.Settings.Presets.TryGetValue(presetName, out var snap)
                && snap != null
                && !string.IsNullOrEmpty(snap.CommunitySourceId);
        }

        // True when the (carId, presetName) car preset's override
        // carries a CommunitySourceId. Same id-based identity model
        // as the game-preset gate.
        private bool IsCarPresetCommunitySourced(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            var perCar = _plugin?.GetCarPresets(carId);
            if (perCar == null) return false;
            return perCar.TryGetValue(presetName, out var entry)
                && entry?.Override != null
                && !string.IsNullOrEmpty(entry.Override.CommunitySourceId);
        }

        // True when the (carId, presetName) car preset is a built-in
        // (ships with the plugin). Built-ins are never shareable because
        // everyone already has them. Parallels IsBuiltinPreset for game
        // presets; the per-car CarPresetEntry carries the IsBuiltin flag.
        private bool IsCarPresetBuiltin(string carId, string presetName)
        {
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return false;
            // Agree with the core's suffix-agnostic classification: a user
            // file named like a factory preset is treated as built-in by
            // every save/delete path, so the Share gate must too, or one row
            // gets contradictory built-in-vs-local treatment on one screen.
            if (_plugin != null && _plugin.IsCarPresetBuiltin(carId, presetName)) return true;
            var perCar = _plugin?.GetCarPresets(carId);
            if (perCar == null) return false;
            return perCar.TryGetValue(presetName, out var entry)
                && entry != null
                && entry.IsBuiltin;
        }

        // Share the currently-active GAME preset (the whole-game
        // settings snapshot under _plugin.ActivePresetName). Body is
        // a serialized GameSettingsSnapshot; the receiver re-hydrates
        // it via Settings.Presets on download.
        // Reentrancy guard for the two header share buttons. async void
        // + an await before the modal opens means a rapid double-click
        // can otherwise spawn two share dialogs in sequence.
        private bool _headerShareInProgress;

        private async void HeaderGameShare_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            if (_headerShareInProgress) return;
            // Set the reentrancy guard BEFORE any validation, and move
            // the validation MessageBoxes INSIDE the try block so any
            // throw from MessageBox.Show / Settings access cannot
            // leak out of the async-void handler.
            _headerShareInProgress = true;
            try
            {
            if (!ShareGate.EnsureReady(Window.GetWindow(this), _plugin, "Share preset")) return;
            string name = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(name)
                || _plugin.Settings.Presets == null
                || !_plugin.Settings.Presets.TryGetValue(name, out var snap)
                || snap == null)
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Share preset",
                    "No game preset is currently active. Save your tuning as a preset first.",
                    DialogKind.Warning);
                return;
            }
            var owner = Window.GetWindow(this);
            if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
            {
                TrueforceDialog.Show(owner,
                    "Share preset",
                    "Pick a username before sharing (Account tab).",
                    DialogKind.Info);
                return;
            }

            var body = new Newtonsoft.Json.Linq.JObject
            {
                ["snapshot"] = PresetSharingClient.BuildShareableSnapshotToken(snap),
            };
            // Mirror PresetManagerControl.CarShare_Click + pack share:
            // bundle any CustomEngineDefs the snapshot references
            // (via EnginePulse.CustomEngineId or per-car-override
            // engine ids) so a recipient whose library doesn't have
            // the engine doesn't fall back to silence on import.
            var customs = _plugin.CollectReferencedCustomEngines(
                new[] { snap }, null);
            if (customs != null && customs.Count > 0)
                body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);

            // Effect tags from non-null sections on the snapshot. Same
            // whitelist the server enforces, same order as car presets.
            var tags = new List<string>(14);
            if (snap.EnginePulse  != null) tags.Add("engine");
            if (snap.RevLimiter   != null) tags.Add("revlimiter");
            if (snap.RoadBumps    != null) tags.Add("roadbumps");
            if (snap.TractionLoss != null) tags.Add("tractionloss");
            if (snap.GearShift    != null) tags.Add("gearshift");
            if (snap.AbsClick     != null) tags.Add("abs");
            if (snap.PitLimiter   != null) tags.Add("pitlimiter");
            if (snap.Drs          != null) tags.Add("drs");
            if (snap.Collision    != null) tags.Add("collision");
            if (snap.AxleSlip     != null) tags.Add("axleslip");
            if (snap.KerbThump    != null) tags.Add("kerbthump");
            if (snap.LockupJudder != null) tags.Add("lockupjudder");
            if (snap.AudioCapture != null) tags.Add("audio");
            if (snap.Airborne     != null) tags.Add("airborne");

            string game = _plugin.ActiveGame ?? "";

            string shareName = name;
            bool   isUpdatePath = false;
            string existingUploadId = null;
            bool userOwnsUpload = !string.IsNullOrEmpty(snap.CommunityUploadedById)
                && string.Equals(snap.CommunityUploadedByUserId,
                                 _plugin.AuthSignedInUserId, StringComparison.Ordinal);
            if (userOwnsUpload)
            {
                string currentHash = PresetBodyHasher.ComputeGameSnapshotBodyHash(snap);
                bool bodyChanged = !string.Equals(currentHash,
                    snap.CommunityUploadedBodyHash, StringComparison.Ordinal);
                if (bodyChanged)
                {
                    string nextVer = PresetManagerControl.NextVersionLabel(snap.CommunityUploadedVersion);
                    var chooser = new UpdateVsNewChooserWindow(
                        "Re-share '" + name + "'",
                        "You already uploaded this preset to the community. Update your existing upload, or share a fresh copy as a new preset?",
                        "Update existing (" + nextVer + ")",
                        "Share as new preset")
                    {
                        Owner = owner,
                    };
                    bool? pick = chooser.ShowDialog();
                    if (pick != true) return;
                    if (chooser.IsUpdate)
                    {
                        isUpdatePath = true;
                        existingUploadId = snap.CommunityUploadedById;
                    }
                    else
                    {
                        string suggested = name + " " + nextVer;
                        string newName = _presetManager?.PromptForName("Share as a new preset",
                            "Community name for this new upload (your local preset's name stays the same):",
                            suggested);
                        if (string.IsNullOrWhiteSpace(newName)) return;
                        shareName = newName.Trim();
                    }
                }
                else
                {
                    // Unchanged uploads never re-share (owner rule, 2026-07-13);
                    // mirrors the Preset Manager handlers.
                    TrueforceDialog.Show(owner, "Already shared",
                        "'" + name + "' is already shared to the community and hasn't changed since. Tweak the preset first, or use Edit… on your upload in the Community list to change its name or description.",
                        DialogKind.Info);
                    return;
                }
            }

            var dialog = PresetShareWindow.ForGame(_plugin, shareName, game, body, tags);
            dialog.Owner = owner;
            dialog.IsUpdate = isUpdatePath;
            dialog.ExistingUploadId = existingUploadId;

            bool? ok = dialog.ShowDialog();
            if (ok == true && !string.IsNullOrEmpty(dialog.UploadedPresetId))
            {
                string finalHash = PresetBodyHasher.ComputeGameSnapshotBodyHash(snap);
                _plugin.StampGamePresetAsUploaded(
                    name, dialog.UploadedPresetId, finalHash,
                    dialog.UploadedContentVersion,
                    allowInPacks: dialog.UploadedAllowInPacks);
                UpdateHeaderShareButtons();
            }
            }
            catch (Exception ex)
            {
                // async-void event handler: an uncaught exception here
                // would propagate to the WPF dispatcher and crash the
                // plugin. Show the user a recoverable error instead.
                SimHub.Logging.Current.Info("[TF4ALL] Share preset failed: " + ex.Message);
                var owner = Window.GetWindow(this);
                TrueforceDialog.ShowError(owner,
                    "Couldn't share that preset. Check your connection and try again.",
                    ex);
            }
            finally { _headerShareInProgress = false; }
        }

        // Share the car preset currently selected in HeaderCarPresetCombo.
        // Same bundling shape as PresetManagerControl.CarShare_Click so
        // receivers parse a single uniform body schema (override +
        // optional custom_engines).
        private async void HeaderCarShare_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            if (_headerShareInProgress) return;
            // Set the reentrancy guard BEFORE validation so a throw in
            // any MessageBox / Settings access during validation goes
            // through the catch instead of out the async-void edge.
            _headerShareInProgress = true;
            try
            {
            if (!ShareGate.EnsureReady(Window.GetWindow(this), _plugin, "Share preset")) return;

            var pick = HeaderCarPresetCombo?.SelectedItem
                is System.Windows.Controls.ComboBoxItem ci
                ? ci.Tag as PresetPick : null;
            if (pick == null || !pick.IsCar || pick.ClearCar
                || string.IsNullOrEmpty(pick.Name) || string.IsNullOrEmpty(pick.CarId))
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Share preset",
                    "Pick a real car preset (not \"None\") before sharing.",
                    DialogKind.Info);
                return;
            }

            var perCar = _plugin.GetCarPresets(pick.CarId);
            if (perCar == null
                || !perCar.TryGetValue(pick.Name, out var entry)
                || entry == null
                || entry.Override == null)
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Share preset",
                    $"Could not load preset '{pick.Name}' for car '{pick.CarId}'.",
                    DialogKind.Warning);
                return;
            }
            var owner = Window.GetWindow(this);
            if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
            {
                TrueforceDialog.Show(owner,
                    "Share preset",
                    "Pick a username before sharing (Account tab).",
                    DialogKind.Info);
                return;
            }

            var customs = _plugin.CollectReferencedCustomEngines(
                null, new[] { entry.Override });

            var body = new Newtonsoft.Json.Linq.JObject
            {
                ["override"] = Newtonsoft.Json.Linq.JToken.FromObject(entry.Override),
            };
            if (customs != null && customs.Count > 0)
                body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);

            var tags = new List<string>(14);
            if (entry.Override.EnginePulse  != null) tags.Add("engine");
            if (entry.Override.RevLimiter   != null) tags.Add("revlimiter");
            if (entry.Override.RoadBumps    != null) tags.Add("roadbumps");
            if (entry.Override.TractionLoss != null) tags.Add("tractionloss");
            if (entry.Override.GearShift    != null) tags.Add("gearshift");
            if (entry.Override.AbsClick     != null) tags.Add("abs");
            if (entry.Override.PitLimiter   != null) tags.Add("pitlimiter");
            if (entry.Override.Drs          != null) tags.Add("drs");
            if (entry.Override.Collision    != null) tags.Add("collision");
            if (entry.Override.AxleSlip     != null) tags.Add("axleslip");
            if (entry.Override.KerbThump    != null) tags.Add("kerbthump");
            if (entry.Override.LockupJudder != null) tags.Add("lockupjudder");
            if (entry.Override.AudioCapture != null) tags.Add("audio");
            if (entry.Override.Airborne     != null) tags.Add("airborne");

            // Prefer the community CarName when available (parallels
            // PresetManagerControl.ResolveCarNameForRow); fall back to
            // the raw car id so the modal always shows something.
            string carDisplay = pick.CarId;
            if (!string.IsNullOrEmpty(entry.GameName)
                && _plugin.Settings.CarFacts != null
                && _plugin.Settings.CarFacts.TryGetValue(
                    entry.GameName + "/" + pick.CarId, out var bundle)
                && bundle != null
                && !string.IsNullOrEmpty(bundle.CarName))
            {
                carDisplay = bundle.CarName;
            }

            string shareName = pick.Name;
            bool   isUpdatePath = false;
            string existingUploadId = null;
            bool userOwnsUpload = !string.IsNullOrEmpty(entry.Override.CommunityUploadedById)
                && string.Equals(entry.Override.CommunityUploadedByUserId,
                                 _plugin.AuthSignedInUserId, StringComparison.Ordinal);
            if (userOwnsUpload)
            {
                string currentHash = PresetBodyHasher.ComputeCarOverrideHash(entry.Override);
                bool bodyChanged = !string.Equals(currentHash,
                    entry.Override.CommunityUploadedBodyHash, StringComparison.Ordinal);
                if (bodyChanged)
                {
                    string nextVer = PresetManagerControl.NextVersionLabel(entry.Override.CommunityUploadedVersion);
                    var chooser = new UpdateVsNewChooserWindow(
                        "Re-share '" + pick.Name + "'",
                        "You already uploaded this preset to the community. Update your existing upload, or share a fresh copy as a new preset?",
                        "Update existing (" + nextVer + ")",
                        "Share as new preset")
                    {
                        Owner = owner,
                    };
                    bool? pickChoice = chooser.ShowDialog();
                    if (pickChoice != true) return;
                    if (chooser.IsUpdate)
                    {
                        isUpdatePath = true;
                        existingUploadId = entry.Override.CommunityUploadedById;
                    }
                    else
                    {
                        string suggested = pick.Name + " " + nextVer;
                        string newName = _presetManager?.PromptForName("Share as a new preset",
                            "Community name for this new upload (your local preset's name stays the same):",
                            suggested);
                        if (string.IsNullOrWhiteSpace(newName)) return;
                        shareName = newName.Trim();
                    }
                }
                else
                {
                    // Unchanged uploads never re-share (owner rule, 2026-07-13);
                    // mirrors the Preset Manager handlers.
                    TrueforceDialog.Show(owner, "Already shared",
                        "'" + pick.Name + "' is already shared to the community and hasn't changed since. Tweak the preset first, or use Edit… on your upload in the Community list to change its name or description.",
                        DialogKind.Info);
                    return;
                }
            }

            var dialog = PresetShareWindow.ForCar(_plugin,
                shareName, entry.GameName ?? "",
                pick.CarId, carDisplay, body, tags);
            dialog.Owner = owner;
            dialog.IsUpdate = isUpdatePath;
            dialog.ExistingUploadId = existingUploadId;

            bool? ok = dialog.ShowDialog();
            if (ok == true && !string.IsNullOrEmpty(dialog.UploadedPresetId))
            {
                string finalHash = PresetBodyHasher.ComputeCarOverrideHash(entry.Override);
                _plugin.StampCarPresetAsUploaded(
                    pick.CarId, pick.Name,
                    dialog.UploadedPresetId, finalHash,
                    dialog.UploadedContentVersion,
                    allowInPacks: dialog.UploadedAllowInPacks);
                UpdateHeaderShareButtons();
            }
            }
            catch (Exception ex)
            {
                // async-void handler: any uncaught throw would crash
                // the WPF dispatcher. Show a recoverable error instead.
                SimHub.Logging.Current.Info("[TF4ALL] Share preset failed: " + ex.Message);
                var ownerWnd = Window.GetWindow(this);
                TrueforceDialog.ShowError(ownerWnd,
                    "Couldn't share that preset. Check your connection and try again.",
                    ex);
            }
            finally { _headerShareInProgress = false; }
        }

        // ===================== Header car community count chip =====================
        // Discoverability surface: when the user loads a car and the
        // community has tunes for it, the header chip "* N community"
        // surfaces them inline. Click -> popover with the top 5; each
        // row has a one-click Apply that downloads + imports + flips
        // the active car preset. "See all" inside the popover jumps to
        // the full Preset Manager Cars-Community view.

        private string _lastResolvedCarKey;    // "game/carId" of the last successful fetch
        private List<PresetSummary> _cachedTopForActiveCar;

        // Kicked off from RefreshFromPlugin whenever the active car /
        // game / community-enabled state may have changed. Hides the
        // Populates _cachedTopForActiveCar so the active-card car
        // preset dropdown's "── Top community presets ──" section has
        // data when (game, carId) changes. Used to also drive a header
        // count chip; that chip is gone, the cache stays. The GET RPC
        // is anonymous-friendly so we still fetch when CommunityEnabled
        // is false (the dropdown only renders when the cache fills, so
        // there's no visible noise either way; surfacing the section
        // for first-time users is the discovery surface).
        private void MaybeRefreshCarCommunityCountAsync()
        {
            if (_plugin?.Settings == null) return;
            if (string.IsNullOrEmpty(_plugin.Settings.CommunityBackendUrl)
                || string.IsNullOrEmpty(_plugin.Settings.CommunityBackendAnonKey))
                return;
            string game  = _plugin.ActiveGame;
            string carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;

            string carKey = game + "/" + carId;
            // Same car as last fetch and we already have a cache - reuse.
            // Cache invalidation on community-panel refresh nulls
            // _lastResolvedCarKey so the next call here re-fetches.
            if (string.Equals(carKey, _lastResolvedCarKey, StringComparison.Ordinal)
                && _cachedTopForActiveCar != null)
                return;

            string capturedGame  = game;
            string capturedCarId = carId;
            string capturedKey   = carKey;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                List<PresetSummary> rows = null;
                try
                {
                    rows = _plugin.FetchCommunityPresetsForCar(
                        capturedGame, capturedCarId, sort: "wilson", limit: 5);
                }
                catch { /* swallow; dropdown section stays empty */ }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Stale-fetch guard: car changed mid-flight, drop.
                    if (!string.Equals(capturedKey,
                            (_plugin?.ActiveGame ?? "") + "/" + (_plugin?.ActiveCarId ?? ""),
                            StringComparison.Ordinal))
                        return;
                    _lastResolvedCarKey      = capturedKey;
                    _cachedTopForActiveCar   = rows ?? new List<PresetSummary>();
                    // Re-render the car preset picker so the new top-
                    // community section becomes visible.
                    RefreshCarPresetPicker();
                }));
            });
        }

        // Picker click on a "── Top community presets ──" row routes here.
        // Mirrors PresetManagerControl.CommunityPreview_Click + the car-
        // preset download branch: fetch the body, open PresetPreviewWindow
        // (the safe-default UX the owner picked), if the user confirms,
        // run CommunitySectionPickerWindow so they choose which sections
        // to import, then save under "<name> (community)" with the
        // community source id stamped so the permission gate + update
        // tracker recognise it. Silent on any failure beyond a status-
        // label hint; this is a discovery surface, not a workflow that
        // should pop up errors mid-drive.
        private async System.Threading.Tasks.Task OpenCommunityPreviewFromPickerAsync(string communityId)
        {
            if (_plugin == null || string.IsNullOrEmpty(communityId)) return;
            PresetSummary summary = null;
            if (_cachedTopForActiveCar != null)
            {
                foreach (var s in _cachedTopForActiveCar)
                    if (s != null && string.Equals(s.Id, communityId, StringComparison.Ordinal))
                    { summary = s; break; }
            }
            if (summary == null) return;

            PresetFull full = null;
            try
            {
                full = await System.Threading.Tasks.Task.Run(
                    () => _plugin.FetchCommunityPresetBody(communityId));
            }
            catch { return; }
            if (full?.Body == null || full.Summary == null) return;

            var owner = Window.GetWindow(this);
            var win = new PresetPreviewWindow(full.Summary, full.Body)
            {
                Owner = owner,
            };
            bool? ok = win.ShowDialog();
            if (ok != true || !win.DownloadRequested) return;

            // Parse the override + bundled custom engines off the body the
            // same way PresetManagerControl's download path does.
            CarOverride ovr = null;
            List<CustomEngineDef> customs = null;
            try
            {
                var ovrToken = full.Body["override"];
                if (ovrToken != null)
                    ovr = ovrToken.ToObject<CarOverride>();
                var ceToken = full.Body["custom_engines"];
                if (ceToken is Newtonsoft.Json.Linq.JArray ja)
                    customs = ja.ToObject<List<CustomEngineDef>>();
            }
            catch { return; }
            if (ovr == null) return;

            var picker = new CommunitySectionPickerWindow(
                full.Summary.Name, full.Summary.Author, ovr)
            {
                Owner = owner,
            };
            if (picker.ShowDialog() != true) return;
            var chosen = picker.ChosenSections;

            var apply = new CarOverride();
            if (chosen.Contains("engine"))       apply.EnginePulse  = ovr.EnginePulse;
            if (chosen.Contains("revlimiter"))   apply.RevLimiter   = ovr.RevLimiter;
            if (chosen.Contains("roadbumps"))    apply.RoadBumps    = ovr.RoadBumps;
            if (chosen.Contains("tractionloss")) apply.TractionLoss = ovr.TractionLoss;
            if (chosen.Contains("gearshift"))    apply.GearShift    = ovr.GearShift;
            if (chosen.Contains("abs"))          apply.AbsClick     = ovr.AbsClick;
            if (chosen.Contains("pitlimiter"))   apply.PitLimiter   = ovr.PitLimiter;
            if (chosen.Contains("drs"))          apply.Drs          = ovr.Drs;
            if (chosen.Contains("collision"))    apply.Collision    = ovr.Collision;
            if (chosen.Contains("axleslip"))     apply.AxleSlip     = ovr.AxleSlip;
            if (chosen.Contains("kerbthump"))    apply.KerbThump    = ovr.KerbThump;
            if (chosen.Contains("lockupjudder")) apply.LockupJudder = ovr.LockupJudder;
            if (chosen.Contains("audio"))        apply.AudioCapture = ovr.AudioCapture;
            if (chosen.Contains("airborne"))     apply.Airborne     = ovr.Airborne;

            if (customs != null && customs.Count > 0)
                _plugin.ImportCommunityCustomEngines(customs);

            string activeCarId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(activeCarId)) return;
            var existing = _plugin.GetCarPresets(activeCarId);
            if (existing != null)
            {
                foreach (var kv in existing)
                    if (kv.Value?.Override != null
                        && string.Equals(kv.Value.Override.CommunitySourceId, full.Summary.Id,
                                         StringComparison.Ordinal))
                        return;
            }

            string baseName = full.Summary.Name + " (community)";
            string presetName = baseName;
            int n = 2;
            while (existing != null && existing.ContainsKey(presetName))
            {
                presetName = baseName + " " + n;
                n++;
            }
            _plugin.SaveImportedCommunityCarPreset(
                activeCarId, presetName, _plugin.ActiveGame ?? "", apply,
                full.Summary.Author, full.Summary.Description,
                communitySourceId: full.Summary.Id,
                allowInPacks: full.Summary.AllowInPacks);
            _plugin.RecordCommunityPresetDownload(full.Summary.Id);
            _plugin.RecordDownloadedCommunityPreset(
                full.Summary.Id, presetName,
                activeCarId, _plugin.ActiveGame ?? "",
                full.Summary.ContentVersion, kind: "car",
                allowInPacks: full.Summary.AllowInPacks,
                originalBodyHash: PresetBodyHasher.ComputeCarOverrideHash(apply),
                ownerUserId: full.Summary.OwnerUserId);

            // Refresh the picker so the freshly-imported preset appears
            // under "── This car ──" and the user can flip the combo to
            // it. We intentionally do NOT auto-bind, so the user keeps
            // explicit control over which preset becomes the car's default.
            RefreshCarPresetPicker();
        }

        // Removed: RenderCarCommunityChip + HeaderCarCommunityCount_Click.
        // The header chip + CommunityForCarPopover (direct-apply path)
        // were redundant with the picker's "── Top community presets ──"
        // section and bypassed the preview-first apply flow. Cache fill
        // logic stays in MaybeRefreshCarCommunityCountAsync since the
        // dropdown reads it.

        // Each header Save button saves directly to the scope it sits next to:
        // the game-side button overwrites the active game preset, the car-side
        // button saves the current tuning to the active car's override. No
        // chooser -- the button's PLACEMENT is the target choice. (To also push
        // a car tune up to the game default, or vice-versa, click the other
        // Save button too.)
        private void HeaderGameSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            SaveAllGameDefaults();
        }

        private void HeaderCarSaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            SaveAllForCar();
        }

        private void ClearEffectDirty(EffectKind which)
        {
            if (!_effectDirty[(int)which]) return;
            _effectDirty[(int)which] = false;
            var saveBtn   = GetEffectSaveBtn(which);
            var revertBtn = GetEffectRevertBtn(which);
            if (saveBtn   != null) saveBtn.Visibility   = Visibility.Collapsed;
            if (revertBtn != null) revertBtn.Visibility = Visibility.Collapsed;
        }

        private void ClearAllEffectDirty()
        {
            for (int i = 0; i < _effectDirty.Length; i++)
            {
                _effectDirty[i] = false;
                var saveBtn   = GetEffectSaveBtn((EffectKind)i);
                var revertBtn = GetEffectRevertBtn((EffectKind)i);
                if (saveBtn   != null) saveBtn.Visibility   = Visibility.Collapsed;
                if (revertBtn != null) revertBtn.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Composes the preset header line based on active preset, game
        /// default, and whether tuning has drifted from the saved snapshot.
        ///
        ///   "MyGT3"                                  active matches saved snapshot
        ///   "MyGT3 · default for this game"          active is also the game default
        ///   "MyGT3 · game default: Stock"            active set, different default exists
        ///   "MyGT3 · ★ unsaved"                       active set, tuning has drifted
        ///   "MyGT3 · default for this game · ★ unsaved" both
        ///   "(unsaved tuning)"                        no active preset, user tuning live
        ///   "(unsaved tuning) · game default: Stock"  same with a known default
        ///   "(none)"                                  no preset, no default, no dirty
        /// </summary>
        private void UpdateHeaderPresetDisplay()
        {
            if (_plugin == null || HeaderPresetCombo == null) return;

            // Unsaved state is now signalled by the header "★ Save all" buttons
            // (driven from UpdateGlobalDirtyFromEffects), so there's no separate
            // badge to toggle here.
            // The pickers carry the active preset names. Rebuild both (game and
            // car) so they reflect current state.
            RefreshGamePresetPicker();
            RefreshCarPresetPicker();
        }

        // Descriptor stashed on each selectable HeaderPresetCombo row so the
        // changed-handler can route a click to the right plugin call. Section
        // headers carry Tag=null and are non-selectable.
        private sealed class PresetPick
        {
            public bool   IsCar;    // false = game-library preset; true = a car preset
            public string CarId;    // the car a car-preset belongs to
            public string Name;
            public bool   ClearCar; // true = the "None" row: clear this car's selection
            // When non-empty, the row is a community top-pick suggestion;
            // clicking opens PresetPreviewWindow (download confirms inside
            // the modal) rather than persisting a binding directly.
            public string CommunityId;
        }

        // Rebuild the GAME-preset picker in the header: just the game-library
        // presets, built-ins first then user, alpha within each. Selection
        // tracks the active game preset. (Car presets live in the separate
        // HeaderCarPresetCombo picker.)
        private void RefreshGamePresetPicker()
        {
            if (_plugin == null || HeaderPresetCombo == null) return;

            string activeP = _plugin.ActivePresetName;
            string defName = _plugin.DefaultPresetForActiveGame;

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                HeaderPresetCombo.Items.Clear();

                // Game-library presets, built-ins first then user, alpha within
                // each (the dictionary key order is otherwise unsorted). The
                // dropdown scrolls (MaxDropDownHeight) and supports type-to-jump,
                // so a long list stays navigable.
                if (_plugin.PresetNames != null)
                {
                    var gameNames = _plugin.PresetNames.ToList();
                    gameNames.Sort((a, b) =>
                    {
                        bool ba = _plugin.IsBuiltinPreset(a), bb = _plugin.IsBuiltinPreset(b);
                        if (ba != bb) return ba ? -1 : 1;
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });
                    foreach (var name in gameNames)
                    {
                        string suffix = string.Equals(name, defName, StringComparison.Ordinal)
                            ? "  ★ default" : "";
                        HeaderPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                        {
                            Content = ToBuiltinDisplay(name) + suffix,
                            Tag     = new PresetPick { IsCar = false, CarId = null, Name = name },
                        });
                    }
                }

                // Select the row matching the active game preset.
                HeaderPresetCombo.SelectedItem = null;
                foreach (var obj in HeaderPresetCombo.Items)
                {
                    if (!(obj is System.Windows.Controls.ComboBoxItem ci)) continue;
                    if (!(ci.Tag is PresetPick pick)) continue;
                    if (!pick.IsCar && string.Equals(pick.Name, activeP, StringComparison.Ordinal))
                    {
                        HeaderPresetCombo.SelectedItem = ci;
                        break;
                    }
                }
            }
            finally { _suppressEvents = prevSuppress; }
        }

        // Rebuild the CAR-preset picker in the header. When a car is loaded it
        // lists that car's presets (built-ins first, alpha) plus every OTHER
        // car's presets (each selectable copies its tuning onto the current car,
        // ApplyCopy). A display-only "None - using game default" row shows when
        // the active car has no preset; a "No car loaded" placeholder shows and
        // disables the combo when no car is detected.
        private void RefreshCarPresetPicker()
        {
            if (_plugin == null || HeaderCarPresetCombo == null) return;

            string carId      = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);
            string activeCar   = carDetected ? _plugin.GetActiveCarPresetName(carId) : null;

            // Local helper: build the built-in-first-then-alpha ordering used
            // for a car's preset list.
            List<CarPresetEntry> Ordered(IReadOnlyDictionary<string, CarPresetEntry> p)
            {
                var o = new List<CarPresetEntry>();
                if (p != null)
                {
                    foreach (var kv in p) if (kv.Value.IsBuiltin) o.Add(kv.Value);
                    foreach (var kv in p) if (!kv.Value.IsBuiltin) o.Add(kv.Value);
                    o.Sort((a, b) =>
                    {
                        if (a.IsBuiltin != b.IsBuiltin) return a.IsBuiltin ? -1 : 1;
                        return string.Compare(a.PresetName, b.PresetName, StringComparison.OrdinalIgnoreCase);
                    });
                }
                return o;
            }

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                HeaderCarPresetCombo.Items.Clear();
                HeaderCarPresetCombo.IsEnabled = true;   // always available, even with no car / paused

                System.Windows.Controls.ComboBoxItem toSelect = null;

                if (!carDetected)
                {
                    // No car identified yet (menu / paused before a car loaded).
                    // Still usable: a prompt + every car's presets. Picking one
                    // pins that car for editing so edits + Save target it.
                    var prompt = new System.Windows.Controls.ComboBoxItem
                    {
                        Content = "Select a car preset to edit…",
                        Tag = null, IsEnabled = false, IsHitTestVisible = false,
                        Opacity = 0.6, FontStyle = FontStyles.Italic,
                    };
                    HeaderCarPresetCombo.Items.Add(prompt);
                    toSelect = prompt;
                }
                else
                {
                    // Selectable "None" row: the car uses no per-car preset and
                    // inherits the game preset. Selected when the car has no
                    // active preset; selectable at any time so the user can
                    // clear a preset back to none (drops the per-car override).
                    string gameDefault = _plugin.DefaultPresetForActiveGame;
                    string inherit = string.IsNullOrEmpty(gameDefault)
                        ? "use game preset"
                        : $"use game preset: {ToBuiltinDisplay(gameDefault)}";
                    var noneRow = new System.Windows.Controls.ComboBoxItem
                    {
                        Content = $"None ({inherit})",
                        Tag = new PresetPick { IsCar = true, CarId = carId, ClearCar = true },
                    };
                    HeaderCarPresetCombo.Items.Add(noneRow);
                    toSelect = noneRow;

                    HeaderCarPresetCombo.Items.Add(MakeSectionHeader($"── This car: {carId} ──"));
                    int localCount = 0;
                    foreach (var entry in Ordered(_plugin.GetCarPresets(carId)))
                    {
                        var ci = new System.Windows.Controls.ComboBoxItem
                        {
                            Content = ToBuiltinDisplay(entry.PresetName),
                            Tag     = new PresetPick { IsCar = true, CarId = carId, Name = entry.PresetName },
                        };
                        HeaderCarPresetCombo.Items.Add(ci);
                        if (string.Equals(entry.PresetName, activeCar, StringComparison.Ordinal)) toSelect = ci;
                        localCount++;
                    }

                    // Top community presets for this (game, carId), sourced from
                    // the same cache that fills the community-count header chip
                    // (MaybeRefreshCarCommunityCountAsync, populated on car change).
                    // Selecting a community row opens PresetPreviewWindow so the
                    // user can read the description / sections / author before
                    // committing to a download (consistent with the Community
                    // panel's Preview action). If the local section is empty we
                    // already added the "── This car ──" header above it; the
                    // community section gets its own header underneath either
                    // way so the source of each suggestion is obvious.
                    string activeKey = (_plugin.ActiveGame ?? "") + "/" + carId;
                    if (string.Equals(activeKey, _lastResolvedCarKey, StringComparison.Ordinal)
                        && _cachedTopForActiveCar != null
                        && _cachedTopForActiveCar.Count > 0)
                    {
                        HeaderCarPresetCombo.Items.Add(MakeSectionHeader(localCount > 0
                            ? "── Top community presets ──"
                            : "── Top community presets (no local presets yet) ──"));
                        foreach (var s in _cachedTopForActiveCar)
                        {
                            if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                            string author = string.IsNullOrEmpty(s.Author) ? "anonymous" : s.Author;
                            HeaderCarPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                            {
                                Content = $"{s.Name} - by {author}",
                                Tag     = new PresetPick { IsCar = true, CarId = carId, Name = s.Name, CommunityId = s.Id },
                            });
                        }
                    }
                }

                // No-car case: show every car the user can edit, grouped by
                // game so they can pick which one to work on. Scoped to the
                // active game when there is one (browsing your library while
                // a game is loaded shouldn't surface cars for a different
                // game; that lives in the Presets Manager). With no active
                // game either, fall back to showing everything so the picker
                // is still usable from the Settings tab outside a session.
                // When a car IS detected, this block is skipped entirely -
                // the picker stays focused on the active car. Use the
                // Presets Manager to edit a different car's preset.
                if (!carDetected)
                {
                    var allCars = _plugin.GetAllCarPresets();
                    if (allCars != null)
                    {
                        string activeGame = _plugin.ActiveGame ?? "";
                        bool gameKnown = !string.IsNullOrEmpty(activeGame);

                        var byGame = new Dictionary<string, List<KeyValuePair<string, IReadOnlyDictionary<string, CarPresetEntry>>>>();
                        foreach (var carKv in allCars)
                        {
                            if (carKv.Value == null || carKv.Value.Count == 0) continue;
                            string g = "";
                            foreach (var p in carKv.Value) { g = p.Value.GameName ?? ""; break; }
                            if (gameKnown && !string.Equals(g, activeGame, StringComparison.Ordinal))
                                continue;
                            if (!byGame.TryGetValue(g, out var list))
                            {
                                list = new List<KeyValuePair<string, IReadOnlyDictionary<string, CarPresetEntry>>>();
                                byGame[g] = list;
                            }
                            list.Add(carKv);
                        }

                        foreach (var g in byGame.Keys
                                     .OrderBy(x => string.Equals(x, activeGame, StringComparison.Ordinal) ? 0 : 1)
                                     .ThenBy(GameDisplayName, StringComparer.OrdinalIgnoreCase))
                        {
                            HeaderCarPresetCombo.Items.Add(MakeSectionHeader($"── {GameDisplayName(g)} ──"));
                            var cars = byGame[g];
                            cars.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                            foreach (var carKv in cars)
                            {
                                foreach (var entry in Ordered(carKv.Value))
                                {
                                    HeaderCarPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                                    {
                                        Content = $"{carKv.Key} · {ToBuiltinDisplay(entry.PresetName)}",
                                        Tag     = new PresetPick { IsCar = true, CarId = carKv.Key, Name = entry.PresetName },
                                    });
                                }
                            }
                        }
                    }
                }

                HeaderCarPresetCombo.SelectedItem = toSelect;
            }
            finally { _suppressEvents = prevSuppress; }

            // Re-evaluate the inline community vote control + one-time nudge
            // for the (possibly newly) applied car preset. Runs after the
            // picker rebuild so the resolved active preset name is current,
            // and also catches the async _cachedTopForActiveCar fill (which
            // re-enters via RefreshCarPresetPicker) so the score populates
            // once community data arrives.
            UpdateActiveCardVoteControl();
        }

        // ===================== Active-card community vote =====================
        // Inline rate-what-you-run control + one-time nudge. The browse-list
        // (PresetManagerControl) owns the full vote grid; this is the small
        // surface in the active card for the preset the user is CURRENTLY
        // running. Reuses the same _plugin.TryVoteCommunity*Async path and
        // the same optimistic+rollback shape.

        // Community id of the applied car preset the vote control is bound
        // to (null = control hidden / not a community download). Captured so
        // the click handlers know what to vote on without re-resolving.
        private string _activeCardVoteCommunityId;
        // Cached optimistic score for the bound item. Seeded from the
        // _cachedTopForActiveCar summary when present; nudged on vote so the
        // label moves even when the browse summary isn't in the cache.
        private int _activeCardVoteUp;
        private int _activeCardVoteDown;
        // Reentrancy guard: an in-flight vote await mustn't be clobbered by a
        // refresh-driven rebuild or a double click.
        private bool _activeCardVoteInFlight;

        // Rebuilds the active-card vote control from the applied car preset.
        // Shows the up/down arrows + score only when that preset is a
        // downloaded community item; hides everything otherwise. Also drives
        // the once-per-session UseCount bump and the one-time nudge.
        private void UpdateActiveCardVoteControl()
        {
            if (_plugin == null || CommunityVoteRow == null) return;

            string carId = _plugin.ActiveCarId;
            string presetName = string.IsNullOrEmpty(carId)
                ? null : _plugin.GetActiveCarPresetName(carId);

            // Bump UseCount at most once this session AND resolve the applied
            // preset's community id in one call.
            string communityId = (!string.IsNullOrEmpty(carId) && !string.IsNullOrEmpty(presetName))
                ? _plugin.NoteActiveCommunityCarPresetUsed(carId, presetName)
                : null;

            bool isDownloadedCommunity =
                !string.IsNullOrEmpty(communityId)
                && _plugin.HasDownloadedCommunity(communityId);

            if (!isDownloadedCommunity)
            {
                _activeCardVoteCommunityId = null;
                CommunityVoteRow.Visibility   = Visibility.Collapsed;
                if (CommunityVoteNudge != null)
                    CommunityVoteNudge.Visibility = Visibility.Collapsed;
                return;
            }

            _activeCardVoteCommunityId = communityId;

            // Seed the score from the browse summary cache when it's there
            // (the applied preset is often one of the top picks for the car).
            // Don't reset an in-flight optimistic score under the user.
            if (!_activeCardVoteInFlight)
            {
                var summary = FindCachedSummary(communityId);
                _activeCardVoteUp   = summary?.Upvotes   ?? _activeCardVoteUp;
                _activeCardVoteDown = summary?.Downvotes ?? _activeCardVoteDown;
            }

            int myVote = 0;
            if (_plugin.Settings?.DownloadedCommunityPresets != null
                && _plugin.Settings.DownloadedCommunityPresets.TryGetValue(communityId, out var rec)
                && rec != null)
                myVote = rec.MyVote;

            RenderActiveCardVote(myVote);
            CommunityVoteRow.Visibility = Visibility.Visible;
            if (CommunityVoteStatus != null && !_activeCardVoteInFlight)
                CommunityVoteStatus.Text = "";

            MaybeShowVoteNudge(communityId);
        }

        // Look up a cached PresetSummary for the given community id (the
        // active-car top-picks cache the picker shares). Null when absent.
        private PresetSummary FindCachedSummary(string communityId)
        {
            if (_cachedTopForActiveCar == null || string.IsNullOrEmpty(communityId))
                return null;
            foreach (var s in _cachedTopForActiveCar)
                if (s != null && string.Equals(s.Id, communityId, StringComparison.Ordinal))
                    return s;
            return null;
        }

        // Paint the score label + highlight the chosen arrow for myVote.
        private void RenderActiveCardVote(int myVote)
        {
            // Net score only - matches the row-level community list
            // (ScoreLabel = upvotes - downvotes). The arrows next to
            // this number ARE the up/down chips, so repeating ▲/▼
            // inside the number was redundant chrome.
            if (CommunityVoteScore != null)
                CommunityVoteScore.Text = (_activeCardVoteUp - _activeCardVoteDown)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (CommunityVoteUpArrow != null)
                CommunityVoteUpArrow.Foreground = (myVote == 1)
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xA5, 0xD6, 0xA7))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            if (CommunityVoteDownArrow != null)
                CommunityVoteDownArrow.Foreground = (myVote == -1)
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xEF, 0x9A, 0x9A))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        private void ActiveCardVoteUp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _ = CastActiveCardVoteAsync(+1);
        }

        private void ActiveCardVoteDown_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _ = CastActiveCardVoteAsync(-1);
        }

        // Cast / toggle a vote from the active-card control. Mirrors
        // PresetManagerControl.ToggleVote's optimistic+rollback shape but
        // against the inline score + the DownloadedPresetRecord.MyVote.
        // clicked is +1 / -1; clicking the already-selected direction
        // retracts (value 0).
        private async System.Threading.Tasks.Task CastActiveCardVoteAsync(int clicked)
        {
            if (_plugin == null) return;
            string communityId = _activeCardVoteCommunityId;
            if (string.IsNullOrEmpty(communityId)) return;
            if (_activeCardVoteInFlight) return;

            if (!_plugin.AuthIsSignedIn)
            {
                if (CommunityVoteStatus != null)
                    CommunityVoteStatus.Text = "Sign in (Settings) to vote.";
                return;
            }

            int prev = 0;
            if (_plugin.Settings?.DownloadedCommunityPresets != null
                && _plugin.Settings.DownloadedCommunityPresets.TryGetValue(communityId, out var rec0)
                && rec0 != null)
                prev = rec0.MyVote;

            int next = (prev == clicked) ? 0 : clicked;

            // Optimistic score + arrow update.
            if (prev == 1)  _activeCardVoteUp   = Math.Max(0, _activeCardVoteUp   - 1);
            if (prev == -1) _activeCardVoteDown = Math.Max(0, _activeCardVoteDown - 1);
            if (next == 1)  _activeCardVoteUp   += 1;
            if (next == -1) _activeCardVoteDown += 1;
            RenderActiveCardVote(next);
            if (CommunityVoteStatus != null)
                CommunityVoteStatus.Text = next == 0
                    ? "Sending..." : (next == 1 ? "Sending..." : "Sending...");

            _activeCardVoteInFlight = true;
            bool ok = false;
            try
            {
                // Active-card preset is always a car preset (this control only
                // shows for an applied car preset), so the car vote RPC is the
                // right route.
                ok = await _plugin.TryVoteCommunityPresetAsync(communityId, next);
            }
            catch (Exception ex)
            {
                ok = false;
                SimHub.Logging.Current.Info("[TF4ALL] Active-card vote failed: " + ex.Message);
            }
            finally { _activeCardVoteInFlight = false; }

            if (!ok)
            {
                // Roll the optimistic score back to prev.
                if (next == 1)  _activeCardVoteUp   = Math.Max(0, _activeCardVoteUp   - 1);
                if (next == -1) _activeCardVoteDown = Math.Max(0, _activeCardVoteDown - 1);
                if (prev == 1)  _activeCardVoteUp   += 1;
                if (prev == -1) _activeCardVoteDown += 1;
                RenderActiveCardVote(prev);
                if (CommunityVoteStatus != null)
                    CommunityVoteStatus.Text = "Vote didn't go through; try again.";
                return;
            }

            // Persist the new vote on the record.
            _plugin.SetDownloadedCommunityVote(communityId, next);
            // Keep the shared browse cache in sync so re-opening the picker /
            // manager reflects the new score.
            var cached = FindCachedSummary(communityId);
            if (cached != null)
            {
                cached.Upvotes   = _activeCardVoteUp;
                cached.Downvotes = _activeCardVoteDown;
                cached.MyVote    = next;
            }
            // Drop the on-disk browse-cache family (the active-card preset is a car
            // preset) so the manager / picker reflect the new vote + Wilson order on
            // their next open instead of serving the pre-vote cached list.
            _plugin.InvalidateBrowseCacheForKind("car", _plugin.ActiveGame, _plugin.ActiveCarId);
            if (CommunityVoteStatus != null)
                CommunityVoteStatus.Text = next == 0
                    ? "Vote retracted." : (next == 1 ? "Thanks for the upvote." : "Downvote recorded.");

            // A successful downvote offers to remove the preset + revert.
            if (next == -1)
                OfferUninstallAfterDownvote(communityId);
        }

        // After a downvote, offer to remove the preset and revert the active
        // car to its previous setup. Confirm dialog is fine here (this is a
        // deliberate user action, not an ambient nudge).
        private void OfferUninstallAfterDownvote(string communityId)
        {
            if (_plugin == null || string.IsNullOrEmpty(communityId)) return;
            string carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(carId)) return;

            string name = null;
            if (_plugin.Settings?.DownloadedCommunityPresets != null
                && _plugin.Settings.DownloadedCommunityPresets.TryGetValue(communityId, out var rec)
                && rec != null)
                name = rec.LocalPresetName;
            if (string.IsNullOrEmpty(name)) name = "this preset";

            if (TrueforceDialog.Show(Window.GetWindow(this),
                "Remove preset",
                $"Remove \"{name}\" and revert to your previous setup?",
                DialogKind.Confirm) != true) return;   // keep the downvote, leave it applied

            _plugin.ClearActiveCarPreset(carId);
            RefreshFromPlugin();
        }

        // ----- One-time inline "rate it?" nudge -----

        // Evaluate + show the nudge for the given applied community preset.
        // All five gates must hold (see task spec). On fire: latch the
        // per-item flag + global cooldown clock, persist, and reveal the
        // inline prompt. Never a MessageBox.
        private void MaybeShowVoteNudge(string communityId)
        {
            if (CommunityVoteNudge == null) return;
            // Default hidden; only the success path below reveals it.
            if (_plugin?.Settings?.DownloadedCommunityPresets == null
                || string.IsNullOrEmpty(communityId)
                || !_plugin.Settings.DownloadedCommunityPresets.TryGetValue(communityId, out var rec)
                || rec == null)
            {
                CommunityVoteNudge.Visibility = Visibility.Collapsed;
                return;
            }

            bool eligible =
                   rec.MyVote == 0
                && !rec.PromptedForVote
                && rec.UseCount >= TrueforcePlugin.VoteNudgeMinUses
                // Dormant once the user has ignored the nudge too many times
                // in a row (reset when they vote from one).
                && (_plugin.Settings?.ConsecutiveVoteNudgeDismissals ?? 0)
                       < TrueforcePlugin.VoteNudgeMaxConsecutiveDismissals
                // Suppress when the persistent inline vote row is already
                // visible: showing both the row + the nudge ends up as two
                // copies of the same ▲ / ▼ pair, which the owner flagged
                // as redundant. The persistent row is enough.
                && (CommunityVoteRow == null || CommunityVoteRow.Visibility != Visibility.Visible);

            // Global cooldown: after ANY nudge fired, suppress all nudges.
            if (eligible)
            {
                var last = _plugin.Settings.LastVoteNudgeUtc;
                if (last.HasValue
                    && (DateTime.UtcNow - last.Value)
                        <= TimeSpan.FromHours(TrueforcePlugin.VoteNudgeGlobalCooldownHours))
                    eligible = false;
            }

            if (!eligible)
            {
                CommunityVoteNudge.Visibility = Visibility.Collapsed;
                return;
            }

            // Fire: latch so it never returns for this item + start the
            // global cooldown, then show inline.
            _plugin.MarkVoteNudgeShown(communityId);
            if (CommunityVoteNudgeText != null)
                CommunityVoteNudgeText.Text =
                    $"You've been running \"{rec.LocalPresetName}\". Rate it?";
            CommunityVoteNudge.Visibility = Visibility.Visible;
        }

        private void ActiveCardNudgeGood_Click(object sender, RoutedEventArgs e)
        {
            if (CommunityVoteNudge != null) CommunityVoteNudge.Visibility = Visibility.Collapsed;
            // Engaged with a nudge -> clear the dismissal streak.
            _plugin?.ResetVoteNudgeDismissals();
            _ = CastActiveCardVoteAsync(+1);
        }

        private void ActiveCardNudgeBad_Click(object sender, RoutedEventArgs e)
        {
            if (CommunityVoteNudge != null) CommunityVoteNudge.Visibility = Visibility.Collapsed;
            // Engaged with a nudge -> clear the dismissal streak.
            _plugin?.ResetVoteNudgeDismissals();
            _ = CastActiveCardVoteAsync(-1);
        }

        private void ActiveCardNudgeLater_Click(object sender, RoutedEventArgs e)
        {
            // Dismiss. PromptedForVote was already latched when the nudge
            // fired, so it won't return for this item; bump the consecutive-
            // dismissal streak so the nudge goes dormant if ignored repeatedly.
            if (CommunityVoteNudge != null) CommunityVoteNudge.Visibility = Visibility.Collapsed;
            _plugin?.NoteVoteNudgeDismissed();
        }

        // Friendly display name for a game code (the car-preset GameName), for
        // the car picker's per-game section headers. Falls back to the raw code.
        private static string GameDisplayName(string game)
        {
            switch (game)
            {
                case "FH6":  return "Forza Horizon 6";
                case "FH5":  return "Forza Horizon 5";
                case "FH4":  return "Forza Horizon 4";
                case "AssettoCorsa": return "Assetto Corsa";
                case "AssettoCorsaCompetizione": return "Assetto Corsa Competizione";
                case "IRacing": return "iRacing";
                case "Wreckfest2": return "Wreckfest 2";
                case null:
                case "": return "Other";
                default: return game;
            }
        }

        // Build a non-selectable, dimmed/bold section-header row for the
        // grouped active-preset picker.
        private static System.Windows.Controls.ComboBoxItem MakeSectionHeader(string text)
        {
            return new System.Windows.Controls.ComboBoxItem
            {
                Content          = text,
                Tag              = null,
                IsEnabled        = false,
                IsHitTestVisible = false,
                FontWeight       = FontWeights.Bold,
                Opacity          = 0.6,
            };
        }

        // GAME-preset picker. Applies a game-library preset and, when a game
        // is active, binds it as that game's default in the same step
        // (select-is-default, matching the car picker's semantics). The old
        // separate "Set as default" link is gone.
        private void HeaderPresetCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (!(HeaderPresetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)) return;
            if (!(item.Tag is PresetPick pick) || pick.IsCar || string.IsNullOrEmpty(pick.Name)) return;

            // No-op when the picked row already matches the active game preset.
            if (string.Equals(pick.Name, _plugin.ActivePresetName, StringComparison.Ordinal))
                return;

            // Unsaved-changes confirm (mirrors PresetCombo_Changed).
            if (_dirty)
            {
                if (TrueforceDialog.Show(Window.GetWindow(this),
                        "Discard unsaved tuning?",
                        $"Apply preset '{pick.Name}'? Your unsaved tuning will be discarded.\n\nCancel to keep editing and Save first.",
                        DialogKind.Destructive, okLabel: "Discard", cancelLabel: "Cancel") != true)
                {
                    // Cancelled: revert the picker selection without re-entering
                    // this handler.
                    RefreshGamePresetPicker();
                    return;
                }
            }

            if (!_plugin.ApplyPreset(pick.Name))
            {
                TrueforceDialog.Show(null, "Trueforce For All", $"Could not apply '{pick.Name}' (preset missing).", DialogKind.Error);
                return;
            }
            // No active game (browsing presets from the menu / no game
            // running): apply-only, there is nothing to bind against.
            // Also apply-only during offline preset/car edits: there the
            // pick is a temporary comparison baseline, not a choice of what
            // this game should auto-load (during a car edit _activeGame is
            // even pinned to the EDITED car's game). The removed
            // Set-as-default button had the same car-edit suppression.
            if (!string.IsNullOrEmpty(_plugin.ActiveGame)
                && !_plugin.IsOfflineEditing
                && !_plugin.IsOfflineEditingCar)
                _plugin.SetDefaultPresetForActiveGame(pick.Name);
            ClearDirty();
            RefreshFromPlugin();
        }

        // CAR-preset picker. Switches the active car's own preset, or copies
        // another car's tuning onto the current car (ApplyCopy).
        private void HeaderCarPresetCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (!(HeaderCarPresetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)) return;
            // Ignore the display-only rows (e.g. "Select a car..." prompt, Tag=null).
            if (!(item.Tag is PresetPick pick)) return;

            // Community-row click: hand off to PresetPreviewWindow. The
            // preview modal owns the download decision so the user reads
            // the description / sections / author before anything binds.
            // Reverts the combo selection to its prior state so the picker
            // doesn't look like the community row "stuck" when in fact
            // nothing was applied yet.
            if (!string.IsNullOrEmpty(pick.CommunityId))
            {
                _ = OpenCommunityPreviewFromPickerAsync(pick.CommunityId);
                RefreshCarPresetPicker();
                return;
            }

            // "None" row: clear this car's preset back to the game preset.
            // Under the per-user defaults model, ClearActiveCarPreset
            // drops the active user's override first (they fall back to
            // any install-wide binding) and only touches the device-
            // wide file if they have no override - so on a shared
            // install, clearing your binding won't unbind it for the
            // other accounts.
            if (pick.ClearCar)
            {
                // No-op only if nothing would change: the car already has no
                // preset AND there's no parked pin to release (we're actually
                // driving it, where the pin must stay). When parked + pinned,
                // clicking None still releases the pin so the list un-filters
                // back to the full per-game list and Car Facts hides.
                if (string.IsNullOrEmpty(_plugin.GetActiveCarPresetName(pick.CarId))
                    && string.Equals(pick.CarId, _plugin.ActiveCarId, StringComparison.Ordinal)
                    && _plugin.IsLiveCarPresent)
                    return;
                if (_dirty)
                {
                    if (TrueforceDialog.Show(Window.GetWindow(this),
                            "Clear car preset?",
                            "Clear this car's preset and use the game preset? Your unsaved tuning will be discarded.\n\nCancel to keep editing and Save first.",
                            DialogKind.Destructive, okLabel: "Discard", cancelLabel: "Cancel") != true)
                    { RefreshCarPresetPicker(); return; }
                }
                _plugin.ClearActiveCarPreset(pick.CarId);
                ClearDirty();
                RefreshFromPlugin();
                return;
            }

            if (string.IsNullOrEmpty(pick.Name)) return;

            // No-op only when re-picking the ACTIVE car's current preset.
            // Picking a different car (or a different preset) always pins +
            // loads it, even if it happens to be that other car's own default.
            if (string.Equals(pick.CarId, _plugin.ActiveCarId, StringComparison.Ordinal)
                && string.Equals(pick.Name, _plugin.GetActiveCarPresetName(pick.CarId), StringComparison.Ordinal))
                return;

            // Unsaved-changes confirm (mirrors PresetCombo_Changed).
            if (_dirty)
            {
                if (TrueforceDialog.Show(Window.GetWindow(this),
                        "Discard unsaved tuning?",
                        $"Apply preset '{pick.Name}'? Your unsaved tuning will be discarded.\n\nCancel to keep editing and Save first.",
                        DialogKind.Destructive, okLabel: "Discard", cancelLabel: "Cancel") != true)
                {
                    // Cancelled: revert the picker selection without re-entering
                    // this handler.
                    RefreshCarPresetPicker();
                    return;
                }
            }

            // Pin the picked car as the active car for editing and load its
            // preset (works for the car you're in, another car, or no car at
            // all). Edits + Save then target this car/preset; live telemetry
            // re-asserts the real car when you're actually driving.
            _plugin.SelectCarForEditing(pick.CarId, pick.Name);
            ClearDirty();
            RefreshFromPlugin();
        }

        // ---------- Master / Audio ----------

        private void PluginEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            // Plugin-enabled is a per-game preference (GameEnabled dict), not
            // part of preset content, so it doesn't dirty the active preset.
            _plugin.SetPluginEnabled(PluginEnabledCheck.IsChecked == true);
        }

        private void MasterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            MasterGainText.Text = v.ToString("F2");
            // Master gain is a global setting, not preset-scoped: apply live and
            // persist (like MasterGainStep). No dirty/save flow, so it never
            // shows a Save button or forks a built-in preset. The persist is
            // debounced: a drag fires dozens of ValueChanged ticks per second
            // and each persist is a full settings serialize + disk write.
            _plugin.MasterGain = v;
            SchedulePersistDebounced();
        }
        // Controls tab: how far each bound-button press moves master gain.
        // Per-machine (not preset-saved), persisted on the same debounce.
        private void MasterGainStepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            MasterGainStepText.Text = v.ToString("F2");
            _plugin.MasterGainStep = v;
            SchedulePersistDebounced();
        }

        // Debounced PersistSettings for slider-drag handlers: the live value is
        // already applied per tick above; only the disk write is deferred, so a
        // drag costs one serialize instead of dozens. Fires on the UI thread.
        // The plugin outlives this panel, so a trailing tick after the panel
        // closes still persists correctly.
        private System.Windows.Threading.DispatcherTimer _persistDebounce;
        private void SchedulePersistDebounced()
        {
            if (_persistDebounce == null)
            {
                _persistDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400),
                };
                _persistDebounce.Tick += (s, e2) =>
                {
                    _persistDebounce.Stop();
                    try { _plugin?.PersistSettings(); } catch { }
                };
            }
            _persistDebounce.Stop();
            _persistDebounce.Start();
        }

        // ---------- Editable slider readouts ----------

        // Readouts that show a transformed value (percent = value * 100). Typed
        // input is divided by this before being written to the slider; keyed by
        // the readout field name. Everything else maps 1:1.
        private static readonly Dictionary<string, double> _readoutScale =
            new Dictionary<string, double>
            {
                ["AirborneReductionText"]   = 100.0,
            };

        // Make every "<X>Slider" + "<X>Text" pair a click-to-type field. Each
        // box writes back to its OWN slider (its own range), and that slider's
        // existing ValueChanged still formats the display (units, precision,
        // percent), so values stay per-slider, nothing is normalized. Paired by
        // reflection over the generated x:Name fields, so new sliders are
        // covered automatically as long as they keep the naming convention.
        private void WireEditableReadouts()
        {
            var fields = GetType().GetFields(System.Reflection.BindingFlags.Instance
                                             | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Public);
            var byName = new Dictionary<string, System.Reflection.FieldInfo>();
            foreach (var f in fields) byName[f.Name] = f;

            foreach (var f in fields)
            {
                if (f.FieldType != typeof(Slider) || !f.Name.EndsWith("Slider")) continue;
                string readoutName = f.Name.Substring(0, f.Name.Length - "Slider".Length) + "Text";
                if (!byName.TryGetValue(readoutName, out var rf) || rf.FieldType != typeof(TextBox)) continue;
                if (f.GetValue(this) is Slider slider && rf.GetValue(this) is TextBox box)
                    AttachEditableReadout(box, slider, readoutName);
            }
        }

        private void AttachEditableReadout(TextBox box, Slider slider, string readoutName)
        {
            double scale = _readoutScale.TryGetValue(readoutName, out var sc) ? sc : 1.0;

            // Flat readout look (no border/background), but now focusable + typeable.
            box.BorderThickness = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.Padding = new Thickness(0);
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            box.TextAlignment = TextAlignment.Right;
            box.HorizontalContentAlignment = HorizontalAlignment.Right;
            box.Cursor = Cursors.IBeam;
            if (box.ToolTip == null) box.ToolTip = "Click to type an exact value.";

            box.GotKeyboardFocus += (s, e) =>
            {
                // Swap the formatted display (e.g. "8 (2ms)", "75%") for a clean
                // editable number in display units, and select it.
                box.Tag = box.Text;
                box.Text = (slider.Value * scale).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                box.Dispatcher.BeginInvoke(new Action(box.SelectAll), DispatcherPriority.Input);
            };
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)       { CommitReadout(box, slider, scale); Keyboard.ClearFocus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { RestoreReadout(box);               Keyboard.ClearFocus(); e.Handled = true; }
            };
            box.LostFocus += (s, e) => CommitReadout(box, slider, scale);
        }

        // Parse the first number the user typed and write it back to the slider
        // (clamped to the slider's range, undoing any display scale). The cached
        // display in box.Tag doubles as an "edit in progress" sentinel so Enter
        // followed by LostFocus only commits once (re-parsing a rounded display
        // like "86%" would otherwise drift the value).
        private void CommitReadout(TextBox box, Slider slider, double scale)
        {
            if (!(box.Tag is string cached)) return; // not in an edit session
            box.Tag = null;                          // end the session
            var m = System.Text.RegularExpressions.Regex.Match(box.Text ?? "", @"-?\d+(\.\d+)?");
            if (m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out double typed))
            {
                double val = typed / scale;
                if (val < slider.Minimum) val = slider.Minimum;
                if (val > slider.Maximum) val = slider.Maximum;
                if (Math.Abs(val - slider.Value) > 1e-9)
                {
                    slider.Value = val; // fires ValueChanged -> reformats + applies
                    return;
                }
            }
            box.Text = cached; // no-op or unparseable: restore the formatted display
        }

        private void RestoreReadout(TextBox box)
        {
            if (box.Tag is string cached) box.Text = cached;
            box.Tag = null;
        }
        private void FfbScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbScaleText.Text = v.ToString("F2");
            _plugin.SetFfbScale(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbInvert_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbInvertSign(FfbInvertCheck.IsChecked == true);
            MarkEffectDirty(EffectKind.Master);
        }

        // Stationary-spring handlers. Per-game preset-scoped (its own Save/Revert
        // section, like FFB spike reduction): edits update the live value and
        // mark the section dirty; the Save button commits it to the active preset.
        private void StationarySpring_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool on = StationarySpringCheck.IsChecked == true;
            _plugin.SetStationarySpringEnabled(on);
            if (StationarySpringSliders != null)
                StationarySpringSliders.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            MarkEffectDirty(EffectKind.StationarySpring);
        }

        // Pause hand-back toggle (shipped default-on since 0.2.7). Global
        // setting; SetStopStreamOnPause persists internally, so no extra
        // PersistSettings() call here. Mirrors the NOLOCK access code (which
        // keeps this checkbox in sync via the handler in CommitAccessCode).
        private void StopStreamOnPause_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetStopStreamOnPause(StopStreamOnPauseCheck.IsChecked == true);
        }

        // ---- Telemetry based FFB (Mode B) handlers. Global settings (not
        // preset-scoped), applied live: the tunables funnel through
        // ApplyModeBFromSettings, the feel-feature toggles through
        // ApplyModeBFeel. Checkbox changes persist immediately; slider drags
        // apply per tick and defer the disk write to the shared debounce. ----
        private void ModeBEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            // Per-game toggle: enable Mode B for the ACTIVE game only (it
            // persists + re-arms inside the plugin). Off unsupported games the
            // checkbox is disabled, so this only fires for supported ones.
            _plugin.SetModeBEnabledForActiveGame(ModeBEnabledCheck.IsChecked == true);
        }

        // SimHub GameName -> friendly label for the Mode B tab note.
        private static string ModeBGameDisplayName(string game)
        {
            switch (game)
            {
                case "FM8": return "Forza Motorsport";
                case "FH4": return "Forza Horizon 4";
                case "FH5": return "Forza Horizon 5";
                case "FH6": return "Forza Horizon 6";
                default:    return string.IsNullOrEmpty(game) ? "this game" : game;
            }
        }

        // "Reverse force direction": ModeBSign is a multiplier (1 normal,
        // -1 flipped), shown as a checkbox. Flip it if the wheel pulls into
        // the slide instead of countering it.
        private void ModeBSign_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.ModeBSign = ModeBSignCheck.IsChecked == true ? -1f : 1f;
            _plugin.ApplyModeBFromSettings();
            try { _plugin.PersistSettings(); } catch { }
        }

        // One handler for every Mode B tunable slider (main + Advanced
        // tuning). Writes ONLY the slider that fired: the old write-all pass
        // raced the dash's Tele-FFB knobs (a phone edit landing mid-drag was
        // silently overwritten with this panel's stale slider positions) and
        // was the reason slider ranges had to cover access-code clamp
        // ranges. Readouts still refresh in one pass so they stay in
        // lockstep with settings changed elsewhere.
        // Slider ranges SHOULD still cover the matching SetModeBParam
        // access-code clamp range: WPF clamps a loaded out-of-range value to
        // the slider Max, and dragging THAT slider would persist the clamp.
        private void ModeBSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            if      (ReferenceEquals(sender, ModeBStrengthSlider)) s.ModeBSatGain         = (float)ModeBStrengthSlider.Value;
            else if (ReferenceEquals(sender, ModeBDamperSlider))   s.ModeBDamper          = (float)ModeBDamperSlider.Value;
            else if (ReferenceEquals(sender, ModeBCenterSlider))   s.ModeBCenter          = (float)ModeBCenterSlider.Value;
            else if (ReferenceEquals(sender, ModeBLatSlider))      s.ModeBLatGain         = (float)ModeBLatSlider.Value;
            else if (ReferenceEquals(sender, ModeBPeakSlider))     s.ModeBPeakUtil        = (float)ModeBPeakSlider.Value;
            else if (ReferenceEquals(sender, ModeBFloorSlider))    s.ModeBDropFloor       = (float)ModeBFloorSlider.Value;
            else if (ReferenceEquals(sender, ModeBRiseSlider))     s.ModeBRiseGamma       = (float)ModeBRiseSlider.Value;
            else if (ReferenceEquals(sender, ModeBSmoothSlider))   s.ModeBEmaMs           = (float)ModeBSmoothSlider.Value;
            else if (ReferenceEquals(sender, ModeBRecoverSlider))  s.ModeBLockupRecoverMs = (float)ModeBRecoverSlider.Value;
            else if (ReferenceEquals(sender, ModeBMinForceSlider)) s.ModeBMinForce        = (float)ModeBMinForceSlider.Value;
            ModeBStrengthText.Text = s.ModeBSatGain.ToString("F2");
            ModeBDamperText.Text   = s.ModeBDamper.ToString("F2");
            ModeBCenterText.Text   = s.ModeBCenter.ToString("F2");
            ModeBLatText.Text      = s.ModeBLatGain.ToString("F2");
            ModeBPeakText.Text     = s.ModeBPeakUtil.ToString("F2");
            ModeBFloorText.Text    = s.ModeBDropFloor.ToString("F2");
            ModeBRiseText.Text     = s.ModeBRiseGamma.ToString("F2");
            ModeBSmoothText.Text   = s.ModeBEmaMs.ToString("F0");
            ModeBRecoverText.Text  = s.ModeBLockupRecoverMs.ToString("F0");
            ModeBMinForceText.Text = s.ModeBMinForce.ToString("F2");
            _plugin.ApplyModeBFromSettings();
            SchedulePersistDebounced();
        }

        // Show the global "Grip limit" slider only when per-car grip auto-cal is
        // OFF. With it on, the learner owns the per-car limit and the global
        // slider would just double-scale it, so it is hidden to avoid confusion.
        private void UpdateModeBGripLimitVisibility()
        {
            if (ModeBGripLimitRow == null || ModeBGripCalCheck == null) return;
            ModeBGripLimitRow.Visibility = ModeBGripCalCheck.IsChecked == true
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        // Feel-feature checkboxes (compressor, suspension load, early torque
        // peak, road kick, grip auto-cal).
        private void ModeBFeel_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            s.ModeBCompressor         = ModeBCompressorCheck.IsChecked == true;
            s.ModeBSuspensionLoad     = ModeBSuspLoadCheck.IsChecked == true;
            s.ModeBEarlyTorquePeak    = ModeBEarlyPeakCheck.IsChecked == true;
            s.ModeBRoadKick           = ModeBRoadKickCheck.IsChecked == true;
            s.ModeBReversalDamp       = ModeBReversalDampCheck.IsChecked == true;
            s.ModeBPhaseLead          = ModeBPhaseLeadCheck.IsChecked == true;
            // One "Adaptive grip & braking feel" master toggle drives the whole
            // learned-limit stack together: per-car grip auto-cal, the friction-
            // circle braking law, and the learned braking-grip radius. They ship
            // on and reinforce each other, so exposing them as three separate A/B
            // checkboxes only invited half-on states. The access codes BCIRCLE /
            // BLEARN still flip friction-circle / brake-learn independently for
            // dev A/B (which can desync them from the master until it's toggled).
            bool adaptive = ModeBGripCalCheck.IsChecked == true;
            s.ModeBGripAutoCal           = adaptive;
            s.ModeBFrictionCircle        = adaptive;
            s.ModeBLongitudinalGripLearn = adaptive;
            UpdateModeBGripLimitVisibility();   // grip-limit slider only shows in manual mode (adaptive off)
            s.ModeBLateralDemand         = ModeBLateralDemandCheck.IsChecked == true;
            s.ModeBAutoStrength          = ModeBAutoStrengthCheck.IsChecked == true;
            // ModeBCenterPd stays untouched here: Direct centering is always on
            // (default true) with only the hidden MBCPD dev code as a failsafe.
            _plugin.ApplyModeBFeel();
            try { _plugin.PersistSettings(); } catch { }
        }

        private void ModeBRoadKickGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)ModeBRoadKickGainSlider.Value;
            _plugin.Settings.ModeBRoadKickGain = v;
            ModeBRoadKickGainText.Text = v.ToString("F2");
            _plugin.ApplyModeBFeel();
            SchedulePersistDebounced();
        }

        private void ModeBReversalGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)ModeBReversalGainSlider.Value;
            _plugin.Settings.ModeBReversalDampGain = v;
            ModeBReversalGainText.Text = v.ToString("F2");
            _plugin.ApplyModeBFeel();
            SchedulePersistDebounced();
        }

        private void ModeBPhaseLeadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)ModeBPhaseLeadSlider.Value;
            _plugin.Settings.ModeBPhaseLeadMs = v;
            ModeBPhaseLeadText.Text = v.ToString("F0");
            _plugin.ApplyModeBFeel();
            SchedulePersistDebounced();
        }

        private void ModeBCenterLeadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)ModeBCenterLeadSlider.Value;
            _plugin.Settings.ModeBCenterLeadMs = v;
            ModeBCenterLeadText.Text = v.ToString("F0");
            _plugin.ApplyModeBFeel();
            SchedulePersistDebounced();
        }

        // "Reset FFB tuning to defaults": restore the whole Mode B recipe (every
        // slider + feel toggle) to the shipped baseline. Confirms first because
        // it discards custom tuning with no undo; leaves the per-game enable and
        // each car's learned grip calibration alone. RefreshFromPlugin re-syncs
        // the controls from the now-default settings.
        private void ModeBReset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // Named by the family the active game actually uses: the reset only
            // touches that one, so a confirm saying "all" would be a lie in
            // either direction (an FS reset leaves Forza alone and vice versa).
            bool spring = TrueforcePlugin.IsSpringModeGame(_plugin.ActiveGame);
            string which = spring ? "Farming Simulator" : "Forza";
            string other = spring ? "Forza" : "Farming Simulator";
            bool? ok = TrueforceDialog.Show(Window.GetWindow(this),
                "Reset Telemetry Based FFB",
                "Reset the " + which + " tuning to the defaults?\n\nThis puts every slider and feel-feature toggle on this tab back to your wheel's defaults (the G PRO, RS50, and G923 each have their own). Your " + other + " setup is left alone, and so are your per-game on/off choices, each car's learned grip calibration, and your rev lights and screen settings.",
                DialogKind.Confirm, okLabel: "Reset", cancelLabel: "Cancel");
            if (ok != true) return;
            _plugin.ResetModeBTuningToDefaults();
            RefreshFromPlugin();
        }

        private void StationarySpringStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            StationarySpringStrengthText.Text = e.NewValue.ToString("F2");
            _plugin.SetStationarySpringStrength(e.NewValue);
            MarkEffectDirty(EffectKind.StationarySpring);
        }
        private void StationarySpringCutoffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            StationarySpringCutoffText.Text = ((int)e.NewValue).ToString();
            _plugin.SetStationarySpringCutoffKmh(e.NewValue);
            MarkEffectDirty(EffectKind.StationarySpring);
        }
        private void SpikeTamingEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbSpikeTamingEnabled(SpikeTamingEnabledCheck.IsChecked == true);
            MarkEffectDirty(EffectKind.SpikeReduction);
        }
        private void SpikeMode_Changed(object sender, RoutedEventArgs e)
        {
            // The pure-UI swap runs even during a suppressed refresh so the
            // label / help / description always match the selected method.
            UpdateSpikeModeUi();
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetFfbSpikeUseSlewLimiter(SpikeModeSlewRadio?.IsChecked == true);
            MarkEffectDirty(EffectKind.SpikeReduction);
        }

        // Swaps the shared limit slider's label + help and the method
        // description for the selected method, and shows the transient-only
        // "Cap softness" row only in transient mode. Pure UI, no settings
        // writes, so it is safe to call during a suppressed refresh.
        private void UpdateSpikeModeUi()
        {
            if (SpikeModeSlewRadio == null) return; // designer / pre-init
            bool slew = SpikeModeSlewRadio.IsChecked == true;
            if (SpikeModeDescription != null)
                SpikeModeDescription.Text = slew
                    ? "Slew-rate limiter (iRacing-style): caps how fast the force is allowed to change. No amplitude reduction, sustained forces always reach full strength; a sharp spike just gets spread across a few extra milliseconds."
                    : "Transient detector: soft-caps only the part of a sudden jump that exceeds your threshold. Sustained heavy cornering passes through at full strength; crashes and big curb hits get rounded off.";
            if (FfbSpikeLimitLabel != null)
                FfbSpikeLimitLabel.Text = slew ? "Slew rate:" : "Spike threshold:";
            if (FfbSpikeLimitHelp != null)
                FfbSpikeLimitHelp.Text = slew
                    ? "Maximum change in force per millisecond. Lower spreads harsh spikes over more time (softer); higher lets force move faster (sharper)."
                    : "Minimum force magnitude before the cap can engage. Below this, forces pass through untouched. Lower catches more spikes; higher only the biggest hits.";
            if (SpikeCapSoftnessRow != null)
                SpikeCapSoftnessRow.Visibility = slew
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
        }

        private void CaptureExeOverride_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitCaptureExeOverride();
        }
        private void CaptureExeOverride_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Enter commits and moves focus off the textbox so the LostFocus
            // handler doesn't fire a duplicate save.
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitCaptureExeOverride();
                System.Windows.Input.Keyboard.ClearFocus();
                e.Handled = true;
            }
        }
        private void CommitCaptureExeOverride()
        {
            if (_suppressEvents || _plugin == null) return;
            string game = _plugin.ActiveGame;
            if (string.IsNullOrEmpty(game))
            {
                // No active game. Nothing to scope the override under.
                // Clear any stale text so it's not misleading.
                if (!string.IsNullOrEmpty(CaptureExeOverrideBox.Text))
                    CaptureExeOverrideBox.Text = "";
                return;
            }
            _plugin.SetAudioCaptureExeOverride(game, CaptureExeOverrideBox.Text);
        }
        private void FfbSmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbSmoothText.Text = v.ToString("F1");
            _plugin.SetFfbSmoothMs(v);
            MarkEffectDirty(EffectKind.Master);
        }
        private void FfbSpikeLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbSpikeLimitText.Text = v <= 0 ? "off" : ((int)v).ToString();
            _plugin.SetFfbSpikeMaxLsbPerMs(v);
            MarkEffectDirty(EffectKind.SpikeReduction);
        }
        private void FfbPeakLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            FfbPeakLimitText.Text = v <= 0 ? "off" : ((int)v).ToString();
            _plugin.SetFfbPeakSoftLimitLsb(v);
            MarkEffectDirty(EffectKind.SpikeReduction);
        }
        private void DuckingEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DuckingEnabled = DuckingEnabledCheck.IsChecked == true;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckDepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckDepthText.Text = v.ToString("F2");
            _plugin.Settings.DuckDepth = v;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckAttackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckAttackText.Text = ((int)v).ToString();
            _plugin.Settings.DuckAttackMs = v;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckReleaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            DuckReleaseText.Text = ((int)v).ToString();
            _plugin.Settings.DuckReleaseMs = v;
            MarkEffectDirty(EffectKind.Ducking);
        }
        private void DuckFrequencyAware_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DuckFrequencyAware = DuckFrequencyAwareCheck.IsChecked == true;
            MarkEffectDirty(EffectKind.Ducking);
        }

        private void EngineTest_Click   (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.EnginePulse);
        private void BumpsTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.RoadBumps);
        private void TractionTest_Click (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.TractionLoss);
        private void ShiftTest_Click    (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.GearShift);
        private void AbsTest_Click      (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.AbsClick);
        private void PitLimiterTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.PitLimiter);
        private void DrsTest_Click       (object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.Drs);

        // One-click health report. Aggregates every status signal that's
        // otherwise spread across the Diagnostics rows + the quiet-wheel hint
        // into a single pass/fail checklist, then fires a short thud so the
        // user gets a physical confirmation the wheel is actually driven.
        // Reads live state only; never reopens a healthy device.
        private void SelfTest_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var sb = new System.Text.StringBuilder();

            bool usbpcap   = _plugin.IsUsbPcapAvailable;
            bool ghub      = _plugin.IsLogitechGHubRunning;
            string wheel   = _plugin.WheelStatus ?? "";
            bool wheelOk   = !wheel.StartsWith("Not detected", StringComparison.OrdinalIgnoreCase)
                          && !wheel.StartsWith("Open failed", StringComparison.OrdinalIgnoreCase)
                          && wheel.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0;
            string stream  = _plugin.StreamStatus ?? "";
            bool streamOk  = stream.StartsWith("Streaming", StringComparison.OrdinalIgnoreCase);
            string tap     = _plugin.FfbTapStatus ?? "";
            bool tapStarted = tap.StartsWith("Tapping", StringComparison.OrdinalIgnoreCase);
            bool tapLive    = _plugin.FfbTapTargetFresh(750);
            var src        = _plugin.TelemetrySource;
            double hz      = src?.MeasuredHz ?? 0;
            bool gameRun   = !string.IsNullOrEmpty(_plugin.ActiveGame);
            bool elevated  = _plugin.IsRunningElevated;

            sb.AppendLine((usbpcap ? "[OK]   " : "[FAIL] ") + "USBPcap installed"
                + (usbpcap ? "" : " (FFB pass-through off; use Reinstall below)"));
            sb.AppendLine(elevated
                ? "[OK]   SimHub running as administrator"
                : "[FAIL] SimHub is NOT running as administrator. Required for reliable force feedback. "
                    + "Easiest fix: enable 'Run as administrator' in SimHub's Settings, then restart SimHub.");
            sb.AppendLine((ghub ? "[FAIL] " : "[OK]   ")
                + (ghub ? "Logitech G HUB is running. Close it; it blocks the wheel."
                        : "Logitech G HUB not running"));
            sb.AppendLine((wheelOk ? "[OK]   " : "[FAIL] ") + "Wheel: "
                + (string.IsNullOrEmpty(wheel) ? "(unknown)" : wheel));
            if (!wheelOk)
            {
                // Wheel not opened: distinguish three causes so the hint is
                // actionable. (1) supported wheel present but its haptic
                // endpoint is missing (G HUB / driver / partial enumeration);
                // (2) a wheel-like Logitech device on an unsupported PID
                // (console mode); (3) nothing wheel-like (generic hint).
                System.Collections.Generic.List<WheelMatch> noEndpoint = null;
                System.Collections.Generic.List<WheelDiscovery.UnsupportedWheel> consoleish = null;
                try { noEndpoint = WheelDiscovery.FindSupportedWithoutTrueforceInterface(); }
                catch { }
                try { consoleish = WheelDiscovery.FindUnsupportedWheelLike(); }
                catch { }

                if (noEndpoint != null && noEndpoint.Count > 0)
                {
                    var w = noEndpoint[0];
                    sb.AppendLine($"       Wheel recognized ({w.Model}) but its Trueforce/haptic USB "
                        + "interface isn't available (the 'wheel present but no haptic endpoint' case).");
                    sb.AppendLine("       Fix: open G HUB once and let it finish detecting the wheel (this loads "
                        + "the wheel's full interface set), then close G HUB and restart SimHub. If it "
                        + "persists, reboot.");
                }
                else if (consoleish != null && consoleish.Count > 0)
                {
                    var w0 = consoleish[0];
                    sb.AppendLine($"       Found a Logitech wheel in an unsupported mode: {w0.Name} "
                        + $"(PID 0x{w0.Pid:X4}). Open G HUB once and let it detect the wheel, then close "
                        + "G HUB and restart SimHub.");
                }
                else
                {
                    sb.AppendLine("       If your wheel is plugged in, open G HUB once and let it detect the "
                        + "wheel, then close G HUB and restart SimHub (G HUB must stay closed while this plugin runs).");
                }
            }
            sb.AppendLine((streamOk ? "[OK]   " : "[FAIL] ") + "Stream: "
                + (string.IsNullOrEmpty(stream) ? "(unknown)" : stream));
            // Tap "started" only proves the USBPcap process is up; tapLive
            // proves a game FFB target was actually decoded off the bus in
            // the last 750 ms, the real liveness signal.
            // The tap can only confirm LIVE pass-through while a game is
            // actually producing forces; "started" just means the USBPcap
            // capture is up and the wheel's interface was found. So no game =
            // not verifiable here, not a failure.
            // Sentinel line replaced live by the FFB watch (see below) once the
            // user has had a chance to actually produce force.
            const string FfbLiveWatchSentinel = "@@FFB_LIVE_WATCH@@";
            bool ffbLiveWatch = false;
            if (tapLive)
                sb.AppendLine("[OK]   FFB pass-through: live (game forces seen on the bus)");
            else if (tapStarted && !gameRun)
                sb.AppendLine("[skip] FFB pass-through: tap running. Start a session, then re-run this "
                    + "test while turning the wheel / driving so it can catch the game's forces.");
            else if (tapStarted)
            {
                // A game is running but no force in the last 750 ms (menu, or
                // the user simply hasn't moved yet). Don't conclude with a
                // stale "skip" the user can't act on: hold the test open and
                // watch live while they make FFB happen (resolved below).
                sb.AppendLine(FfbLiveWatchSentinel);
                ffbLiveWatch = true;
            }
            else if (wheelOk && usbpcap
                     && !tap.StartsWith("Discovering", StringComparison.OrdinalIgnoreCase))
            {
                // HID/Windows opened the wheel but USBPcap never found it on
                // the bus: the "non-standard USB" failure. The wheel is on a
                // USB controller/port USBPcap doesn't cover, or USBPcap's
                // driver hasn't attached since install. FFB pass-through stays
                // dead until the bus is visible to the capture driver.
                sb.AppendLine("[FAIL] FFB pass-through: Windows sees your wheel but USBPcap can't capture it on the USB bus.");
                sb.AppendLine("       Likely the wheel is on a USB port/controller USBPcap doesn't cover, or USBPcap needs a reboot since it was installed.");
                sb.AppendLine("       Try, in order: reboot (if USBPcap was just installed); move the wheel to a different USB port (rear motherboard ports work best, avoid front-panel ports and hubs); run SimHub as administrator; or use 'Pick device manually' below.");
            }
            else
                sb.AppendLine("[skip] FFB pass-through: " + (string.IsNullOrEmpty(tap) ? "(not started)" : tap));
            // FFB not confirmed live and experimental detection is off: a wheel
            // sending force in a shape the default path doesn't recognize is
            // exactly what experimental mode widens, so suggest it.
            if (!tapLive && !ffbLiveWatch && !(_plugin.Settings?.ExperimentalFfbCapture ?? false))
                sb.AppendLine("       If your wheel should have force feedback but you feel none, turn on "
                    + "'Enable experimental FFB detection' (Effects tab, under FFB tweaks), then drive a few seconds.");
            if (!gameRun && _plugin.IsKnownGameProcessRunning(out string pausedGame))
                sb.AppendLine($"[skip] Telemetry: '{pausedGame}' is running but paused or in a menu (telemetry resumes on track)");
            else if (!gameRun)
                sb.AppendLine("[skip] Telemetry: no game running (start a game, load a session)");
            else
                sb.AppendLine((hz > 0 ? "[OK]   " : "[skip] ") + "Telemetry: "
                    + (src?.Name ?? "?") + (hz > 0 ? $" ({hz:0} Hz)" : " (idle; in a menu or paused?)"));

            string diag = _plugin.WheelQuietDiagnostic;
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(diag)
                ? "Overall: healthy."
                : "Most-blocking issue: " + diag);

            sb.AppendLine();
            string checklist = sb.ToString();

            if (streamOk)
            {
                // Sustained ~2.5 s traction-loss rumble: unmistakable even on
                // a quiet gear-driven G923, unlike the brief gear thud.
                _plugin.TestEffect(_plugin.TractionLoss);

                if (ffbLiveWatch)
                {
                    // Hold the result open and poll for game FFB for a few
                    // seconds so the user can actually trigger it (turn the
                    // wheel / drive) and SEE the verdict update, instead of a
                    // snapshot that's stale before they can act.
                    SelfTestResultText.Text = checklist.Replace(FfbLiveWatchSentinel,
                        "[..]   FFB pass-through: in a session (not a menu), TURN THE WHEEL or drive NOW. Watching for ~6 s...")
                        + "(Also sent a 2.5 s test rumble to confirm the wheel responds to our output.)";
                    SelfTestResultText.Visibility = Visibility.Visible;
                    SelfTestButton.IsEnabled = false;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        bool seen = false;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.Elapsed.TotalSeconds < 6.0)
                        {
                            if (_plugin.FfbTapTargetFresh(750)) { seen = true; break; }
                            System.Threading.Thread.Sleep(150);
                        }
                        Dispatcher.Invoke(() =>
                        {
                            string verdict = seen
                                ? "[OK]   FFB pass-through: LIVE - game forces captured when you moved. Pass-through is working."
                                : "[skip] FFB pass-through: no game forces seen in 6 s. Be in an ACTIVE session (not a menu/paused) and turn the wheel / drive while the watch runs."
                                  + ((_plugin.Settings?.ExperimentalFfbCapture ?? false)
                                      ? ""
                                      : " If you still feel no force feedback, turn on 'Enable experimental FFB detection' and try again.");
                            SelfTestResultText.Text = checklist.Replace(FfbLiveWatchSentinel, verdict);
                            SelfTestButton.IsEnabled = true;
                        });
                    });
                    return;
                }

                SelfTestResultText.Text =
                    checklist + "Sent a 2.5 s test rumble now: you should feel a "
                    + "clear sustained buzz build and fade.";
                SelfTestResultText.Visibility = Visibility.Visible;
                return;
            }

            // Stream is down: the live FFB watch doesn't apply (fix the stream
            // first). Resolve any sentinel to a static line so it never leaks.
            if (ffbLiveWatch)
                checklist = checklist.Replace(FfbLiveWatchSentinel,
                    "[skip] FFB pass-through: not verified (stream is down; fix that first, then re-test while driving).");

            // Stream is down: run the active staged probe (discovery, then
            // open + init + tap + stream). The init sequence blocks ~150 ms,
            // so it runs off the UI thread to not freeze SimHub; the button
            // is disabled until the staged result is appended.
            SelfTestResultText.Text       = checklist + "Stream not active: running active device probe...";
            SelfTestResultText.Visibility = Visibility.Visible;
            SelfTestButton.IsEnabled      = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                string probe;
                try { probe = _plugin.RunActiveDeviceProbe(); }
                catch (Exception ex) { probe = "[FAIL] Probe crashed: " + ex.Message; }
                Dispatcher.Invoke(() =>
                {
                    SelfTestResultText.Text  = checklist + "Active device probe:\n" + probe;
                    SelfTestButton.IsEnabled = true;
                });
            });
        }
        private void AudioEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.Enabled = AudioEnabledCheck.IsChecked == true;
            Apply(EffectKind.Audio);
        }
        private void AudioGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            float v = (float)e.NewValue;
            AudioGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.Gain = v;
            Apply(EffectKind.Audio);
        }
        private void AudioFilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioFilterText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.LowpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }
        private void AudioHighpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.ActiveAudio == null) return;
            double v = e.NewValue;
            AudioHighpassText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Audio);
            _plugin.ActiveAudio.HighpassCutoffHz = v;
            Apply(EffectKind.Audio);
        }

        // ---------- Active car ----------

        // ---------- Active car preset dropdown / save / delete ----------

        /// <summary>Strip a trailing " (default)" suffix from a preset name
        /// so the suggested fork name doesn't end up as
        /// "X (default) (something)". Returns the original string when no
        /// suffix is present.</summary>
        private static string StripDefaultSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string suffix = " (default)";
            return name.EndsWith(suffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        // Built-in presets are stored with a trailing " (default)" suffix;
        // UI surfaces relabel it " (built-in)". Shared with the dash remote
        // via BuiltinPresets.ToDisplayName (rationale documented there).
        private static string ToBuiltinDisplay(string name)
            => BuiltinPresets.ToDisplayName(name);

        /// <summary>Modal name-prompt for car preset save. Disallows empty
        /// names, the suffix "(default)" (built-in territory), and names
        /// already taken by another preset for this car. Returns the
        /// chosen name, or null on cancel / invalid.</summary>
        private string PromptForCarPresetName(string title, string body, string initial,
                                              IReadOnlyDictionary<string, CarPresetEntry> existing)
        {
            var win = new Window
            {
                Title  = title,
                Width  = 460,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            var box = new System.Windows.Controls.TextBox { Text = initial ?? "", Height = 26 };
            sp.Children.Add(box);
            var error = new TextBlock { FontSize = 11, Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(error);
            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok     = new Button { Content = "Save", Width = 80, Height = 26, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
            btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);
            win.Content = sp;

            string chosen = null;
            ok.Click += (_, __) =>
            {
                string name = (box.Text ?? "").Trim();
                if (string.IsNullOrEmpty(name)) { error.Text = "Name can't be empty."; return; }
                if (name.EndsWith("(default)", StringComparison.OrdinalIgnoreCase))
                {
                    error.Text = "Names ending with '(default)' are reserved for built-ins.";
                    return;
                }
                if (existing != null && existing.ContainsKey(name))
                {
                    error.Text = $"A preset named '{name}' already exists for this car.";
                    return;
                }
                chosen = name;
                win.DialogResult = true;
                win.Close();
            };
            box.Focus();
            box.SelectAll();
            win.ShowDialog();
            return chosen;
        }

        // ---------- Engine pulse ----------

        private void EngineEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Enabled = EngineEnabledCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Gain = v;
            Apply(EffectKind.Engine);
        }
        // GitHub issue template URL. Pre-fills title + body with the active
        // car's state so users can submit corrections in one click. No need
        // to remember the carId, the source attribution, or the format.
        private const string ReportIssuesBase = "https://github.com/Mhytee/Trueforce-For-All/issues/new";
        private const string RepoUrl          = "https://github.com/Mhytee/Trueforce-For-All";

        private void ReportIssue_Click(object sender, RoutedEventArgs e)
        {
            // Offer to bundle logs before opening the issue form. GitHub's URL
            // length limits mean we can't paste logs into the body anyway; the
            // user attaches the zip after the form opens.
            var choice = TrueforceDialog.Show(Window.GetWindow(this),
                "Include logs?",
                "Attach your SimHub logs to this bug report? They make wheel and USBPcap issues much "
                + "easier to diagnose. Logs export to a zip on your Desktop; drag it into the GitHub "
                + "issue after the form opens.\n\n"
                + "Close this window to cancel the report.",
                DialogKind.Confirm, okLabel: "Attach logs", cancelLabel: "Skip logs",
                goldOk: true);
            if (choice == null) return;          // closed/Esc = cancel the whole report
            string zipPath = null;
            if (choice == true)
            {
                // Re-use the export-logs path. If it fails, surface the error
                // but still open the issue form so the user can file something.
                zipPath = TryExportLogs(silentOnSuccess: false);
            }

            // Generic "report a bug / feature request" path. Everything the
            // plugin can know is auto-filled so triage never waits on a
            // "which wheel? which SimHub?" round-trip, and the status block
            // travels in the body itself: a report filed WITHOUT the log zip
            // still shows wheel/stream/capture state (that context otherwise
            // exists only inside the zip's manifest).
            string game = _plugin?.ActiveGame ?? "(none)";
            string carId = _plugin?.ActiveCarId ?? "(none)";
            string versions = TrueforcePlugin.GetAssemblyVersionLine(out bool versionMismatch);

            // Live wheel first; fall back to the persisted last-used model so
            // an "it broke, I unplugged it" report still names the hardware.
            string wheel = _plugin?.WheelStatus;
            if (string.IsNullOrEmpty(wheel) || wheel.StartsWith("Not detected", StringComparison.Ordinal))
            {
                string lastUsed = _plugin?.Settings?.LastUsedWheel;
                wheel = string.IsNullOrEmpty(lastUsed)
                    ? null
                    : $"{lastUsed} (last used; not detected right now)";
            }

            // SimHub can decline to tell us its version; ask rather than print
            // something wrong, since a filled-looking field stops people asking.
            string simHub = TrueforcePlugin.GetSimHubVersion();

            string logsLine = zipPath != null
                ? $"**Logs:** drag `{System.IO.Path.GetFileName(zipPath)}` from your Desktop into this issue\n"
                : "**Logs:** none attached (the Export logs button in the Settings tab makes a zip worth adding)\n";
            // Placeholders are italic, never <angle brackets>: GitHub parses
            // "<describe the issue>" as an HTML tag and renders nothing at all,
            // so an unedited report would post with blank sections. Values go in
            // code spans because they are raw device strings; the USBPcap
            // interface in the tap status ("\\.\USBPcap2") would otherwise lose
            // a backslash to Markdown escaping.
            string body =
                  "**What happened?**\n_describe the issue_\n\n"
                + "**Steps to reproduce**\n1. \n2. \n\n"
                + "**Expected behavior**\n_what should have happened_\n\n"
                + "---\n"
                + "**Environment** (auto-filled)\n"
                + $"- Plugin: {versions}{(versionMismatch ? "  **[VERSION MISMATCH - stale DLL?]**" : "")}\n"
                + $"- SimHub: {(simHub == null ? "_fill in: shown on SimHub's About screen_" : "`" + simHub + "`")}\n"
                + $"- Windows: {TrueforcePlugin.GetWindowsVersionLine()}\n"
                + $"- Wheel: {(wheel == null ? "_fill in: e.g. G PRO, RS50, G923; none detected_" : "`" + wheel + "`")}\n"
                + $"- Active game: `{game}`\n"
                + $"- Active car: `{carId}`\n"
                + $"- Stream: `{_plugin?.StreamStatus ?? "(n/a)"}`\n"
                + $"- FFB tap: `{_plugin?.FfbTapStatus ?? "(n/a)"}`\n"
                + $"- Capture: `{_plugin?.CaptureFingerprint ?? "(not confirmed this session)"}`\n"
                + $"- Telemetry source: `{_plugin?.TelemetrySource?.Name ?? "(none)"}`\n"
                + "\n" + logsLine;
            string url = ReportIssuesBase
                       + "?title=" + Uri.EscapeDataString("[bug] ")
                       + "&body="  + Uri.EscapeDataString(body);
            OpenUrl(url);
        }

        // Standalone "Export logs" button. Same exporter the Report Issue
        // dialog uses; opens Explorer to the resulting zip so users can drag
        // it into the bug report or share it directly with support.
        private void ExportLogs_Click(object sender, RoutedEventArgs e)
        {
            TryExportLogs(silentOnSuccess: false);
        }

        // Zips SimHub's log directory + the Trueforce settings file to the
        // user's Desktop and opens Explorer with the new zip selected. Logged
        // errors instead of silent so partial failures (e.g. one log file
        // locked by SimHub) don't kill the export. Returns the zip path on
        // success or null on failure.
        private string TryExportLogs(bool silentOnSuccess)
        {
            try
            {
                // SimHub install dir. We're loaded into SimHubWPF.exe; using
                // the host process's MainModule path is more reliable than
                // walking up from our own assembly (we live under PluginsData).
                string simHubRoot = System.IO.Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                string logsDir   = System.IO.Path.Combine(simHubRoot, "Logs");
                string debugLog  = System.IO.Path.Combine(simHubRoot, "debug.log");

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string zipPath = System.IO.Path.Combine(desktop, $"Trueforce-logs-{ts}.zip");

                using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Create))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    if (System.IO.Directory.Exists(logsDir))
                    {
                        foreach (var f in System.IO.Directory.GetFiles(logsDir))
                        {
                            try { AddFileToZip(zip, f, "Logs/" + System.IO.Path.GetFileName(f)); }
                            catch (Exception ex) { TryAddNoteToZip(zip, "Logs/_" + System.IO.Path.GetFileName(f) + ".error.txt", ex.Message); }
                        }
                    }
                    if (System.IO.File.Exists(debugLog))
                    {
                        try { AddFileToZip(zip, debugLog, "debug.log"); }
                        catch (Exception ex) { TryAddNoteToZip(zip, "debug.log.error.txt", ex.Message); }
                    }
                    // Full settings snapshot for diagnosis (UDP ports / bind /
                    // forward, per-effect config, car overrides, the lot). We
                    // re-serialize the in-memory object because the plugin persists
                    // through SimHub's common-settings store, not a locatable file.
                    // REDACT secrets/PII first: users attach this zip to public
                    // GitHub issues, so the auth session (tokens + email), the
                    // remembered email, and the per-user slot data must never travel.
                    // Also stripped: the anonymous car-fact submitter GUID and the
                    // community author name (both link the reporter to their
                    // community activity), the backup-sync envelope (a second,
                    // unredacted copy of the portable settings), the achievement
                    // baseline (embeds the auth user id verbatim), and the free
                    // text users type for their own screens (the dash idle-card
                    // driver name, the wheel OLED greeting and its custom slots)
                    // since a real first name is the most natural thing to put
                    // there and none of it helps diagnose anything.
                    // Keep this list in sync when adding any secret/PII setting.
                    try
                    {
                        var snapshot = _plugin?.Settings;
                        if (snapshot != null)
                        {
                            var jo = Newtonsoft.Json.Linq.JObject.FromObject(snapshot);
                            foreach (var secret in new[] { "AuthSession", "LastSignInEmail",
                                "LegacyDataOwnerEmail", "UserSlots", "ActiveSlotKey",
                                "CarFactsAnonId", "SharingAuthor",
                                "BackupLastSyncedEnvelopeJson", "BackupLastSyncedRevision",
                                "AchievementBaseline", "DashIdleDriverName",
                                "OledGreetingText", "OledCustomTexts" })
                                jo.Remove(secret);
                            // Community lineage stamps live INSIDE CustomEngines[],
                            // Presets[*], CarOverrides[*] and DownloadedCommunityPresets[*]
                            // entries, where a top-level Remove can never reach: the
                            // account uuid stamped on the user's own uploads (the same
                            // value as the redacted AuthSession.UserId), the row id of
                            // their public listing, the cached uploader uuid on
                            // downloads, and author display names. All are identity
                            // joins with zero diagnostic value, so strip them wherever
                            // they appear in the tree.
                            var lineage = new HashSet<string> {
                                "CommunityUploadedByUserId", "CommunityUploadedById",
                                "OwnerUserId", "Author", "SharingAuthor" };
                            foreach (var prop in jo.Descendants()
                                .OfType<Newtonsoft.Json.Linq.JProperty>()
                                .Where(p => lineage.Contains(p.Name)).ToList())
                                prop.Remove();
                            // Everything above makes this snapshot deliberately
                            // lossy, and some of what it drops is portable data
                            // that Import would NOT restore from the live object
                            // (the anon car-fact id, the sharing author, the
                            // upload stamps). Without a marker the file still
                            // satisfies LooksLikeSettingsBackup, so a user who
                            // reaches for it as a backup would be told it is one
                            // and then silently lose those fields. Flag it so
                            // Import refuses it outright.
                            jo[DiagnosticSnapshotMarker] = true;
                            TryAddNoteToZip(zip, "Trueforce-settings.json",
                                jo.ToString(Newtonsoft.Json.Formatting.Indented));
                        }
                    }
                    catch (Exception ex)
                    {
                        TryAddNoteToZip(zip, "Trueforce-settings.json.error.txt", ex.ToString());
                    }
                    // Opt-in raw USB packet trace. Only present when the user
                    // explicitly enabled the Diagnostics toggle. Real pcap
                    // file with USBPcap link type; bundled into the zip so
                    // support can open it with Wireshark + USBPcap dissector.
                    string usbTrace = TrueforcePlugin.GetUsbTraceLogPath();
                    if (System.IO.File.Exists(usbTrace))
                    {
                        try { AddFileToZip(zip, usbTrace, "usb-trace.pcap"); }
                        catch (Exception ex) { TryAddNoteToZip(zip, "usb-trace.pcap.error.txt", ex.Message); }
                    }
                    // Mini context manifest so support knows what version
                    // generated the zip without unpacking everything. The
                    // three-way assembly line carries the same MISMATCH marker
                    // as the startup log cross-check: a stale Core/Engine DLL
                    // (the top dead-wheel cause) is page-1 visible.
                    string versions = TrueforcePlugin.GetAssemblyVersionLine(out bool versionMismatch);
                    // At-a-glance UDP config so a wrong port / non-local bind
                    // address (the common "no telemetry" cause) is visible in the
                    // manifest without opening the full settings JSON below.
                    var fz = _plugin?.Settings?.Forza;
                    string forzaLine = fz == null ? "(n/a)"
                        : $"enabled={fz.Enabled} port={fz.Port} bind={fz.BindAddress} " +
                          $"forward={(fz.ForwardEnabled ? $"{fz.ForwardHost}:{fz.ForwardPort}" : "off")}";
                    string manifest =
                        $"Generated: {DateTime.Now:o}\n" +
                        $"Assembly versions: {versions}{(versionMismatch ? "  [VERSION MISMATCH - stale DLL; Plugin, Core and Engine must ship as a set]" : "")}\n" +
                        $"SimHub version: {TrueforcePlugin.GetSimHubVersion() ?? "(unavailable)"}\n" +
                        $"Windows: {TrueforcePlugin.GetWindowsVersionLine()}\n" +
                        $"Active game: {_plugin?.ActiveGame ?? "(none)"}\n" +
                        $"Active car: {_plugin?.ActiveCarId ?? "(none)"}\n" +
                        $"Telemetry source: {_plugin?.TelemetrySource?.Name ?? "(none)"}\n" +
                        $"Wheel status: {_plugin?.WheelStatus}\n" +
                        $"Stream status: {_plugin?.StreamStatus}\n" +
                        $"FFB tap status: {_plugin?.FfbTapStatus}\n" +
                        $"Capture: {_plugin?.CaptureFingerprint ?? "(not confirmed this session)"}\n" +
                        $"Experimental FFB capture: {(_plugin?.Settings?.ExperimentalFfbCapture ?? false ? "ON" : "OFF")}\n" +
                        $"Forza UDP: {forzaLine}\n" +
                        $"Manual USBPcap override: {(_plugin?.HasManualUsbPcapDevice ?? false ? $"{_plugin.Settings.ManualUsbPcapInterface} dev {_plugin.Settings.ManualUsbPcapDeviceAddress}" : "(none)")}\n" +
                        $"USB byte logging: {(_plugin?.Settings?.LogUsbBytesEnabled ?? false ? "enabled" : "disabled")}\n" +
                        $"Full settings: see Trueforce-settings.json in this zip\n" +
                        $"SimHub root: {simHubRoot}\n";
                    TryAddNoteToZip(zip, "manifest.txt", manifest);
                }

                // Reveal in Explorer so users don't have to hunt for it.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"")
                    {
                        UseShellExecute = true,
                    });
                }
                catch { }

                // The Explorer reveal above is the confirmation; the standalone
                // "Logs exported" modal was redundant with it, so it's dropped.
                return zipPath;
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(null,
                    "Couldn't export the logs. Make sure the folder is writable, then try again.",
                    ex);
                return null;
            }
        }

        // Copy a file into a zip entry while tolerating the file being held by
        // another process (SimHub is actively writing to its current log).
        // Opens with shared read so we don't error on an in-use rolling log.
        private static void AddFileToZip(System.IO.Compression.ZipArchive zip, string sourcePath, string entryName)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
            using (var inStream = new System.IO.FileStream(sourcePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var outStream = entry.Open())
            {
                inStream.CopyTo(outStream);
            }
        }

        private static void TryAddNoteToZip(System.IO.Compression.ZipArchive zip, string entryName, string text)
        {
            try
            {
                var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                using (var outStream = entry.Open())
                using (var w = new System.IO.StreamWriter(outStream))
                    w.Write(text);
            }
            catch { }
        }

        // Toggle raw USB packet logging on/off. Persists the choice and
        // applies it to the live FFB tap so the next packet observed starts
        // (or stops) writing to usb-trace.bin alongside SimHub's logs.
        private void LogUsbBytes_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || LogUsbBytesCheck == null) return;
            _plugin.SetUsbBytesLoggingEnabled(LogUsbBytesCheck.IsChecked == true);
        }

        private void ExperimentalFfbDetection_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || ExperimentalFfbCheck == null) return;
            _plugin.SetExperimentalFfbCapture(ExperimentalFfbCheck.IsChecked == true);
        }

        // Standing mod-install banner on the Telemetry FFB tab. The hold flag
        // keeps the success text visible after an install; the banner state
        // machine otherwise belongs to RefreshFromPlugin.
        private bool _fsModBannerHold;
        private void FsModInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || FsModBannerText == null) return;
            string err = _plugin.InstallFsModForActiveGame();
            _fsModBannerHold = err == null;
            FsModBannerText.Text = err == null
                ? "Installed. It loads the next time Farming Simulator starts, so restart the game if it is running now."
                : "Install failed: " + err + ".";
            if (err == null && FsModInstallButton != null)
                FsModInstallButton.Visibility = System.Windows.Visibility.Collapsed;
        }

        // Terrain feel (spring-mode enhancement). Settings-only: the sampler
        // reads both every telemetry tick, so changes apply live.
        private void SpringTerrain_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringTerrainCheck == null) return;
            _plugin.Settings.SpringModeTerrainEnabled = SpringTerrainCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void SpringTerrainStrength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringTerrainStrengthSlider == null) return;
            _plugin.Settings.SpringModeTerrainGain = SpringTerrainStrengthSlider.Value;
            if (SpringTerrainStrengthText != null)
                SpringTerrainStrengthText.Text = SpringTerrainStrengthSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        // The other spring-mode enhancements: same settings-only live-apply
        // pattern as terrain (the force path reads these every tick).
        private void SpringCenterGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringCenterGainSlider == null) return;
            _plugin.Settings.SpringModeCenterGain = SpringCenterGainSlider.Value;
            if (SpringCenterGainText != null)
                SpringCenterGainText.Text = SpringCenterGainSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        private void SpringCenterFirm_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringCenterFirmSlider == null) return;
            _plugin.Settings.SpringModeCenterFirmness = SpringCenterFirmSlider.Value;
            if (SpringCenterFirmText != null)
                SpringCenterFirmText.Text = SpringCenterFirmSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        private void SpringSpeedFx_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringSpeedFxSlider == null) return;
            _plugin.Settings.SpringModeSpeedEffect = SpringSpeedFxSlider.Value;
            if (SpringSpeedFxText != null)
                SpringSpeedFxText.Text = SpringSpeedFxSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        private void SpringDrag_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringDragCheck == null) return;
            _plugin.Settings.SpringModeDragEnabled = SpringDragCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void SpringDragGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringDragGainSlider == null) return;
            _plugin.Settings.SpringModeDragGain = SpringDragGainSlider.Value;
            if (SpringDragGainText != null)
                SpringDragGainText.Text = SpringDragGainSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        private void SpringStrength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringStrengthSlider == null) return;
            _plugin.Settings.SpringModeStrength = SpringStrengthSlider.Value;
            if (SpringStrengthText != null)
                SpringStrengthText.Text = SpringStrengthSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        // iRacing's OWN strength (IRacingForceGain), separate from the Forza
        // ModeBSatGain for the same reason the spring has its own: the paths
        // are different pipelines. This one multiplies the sim's already
        // normalized torque, so 1.00 means "exactly what iRacing asked for",
        // where the Forza gain is a peak-torque fraction defaulting to 0.50.
        // Read live by ComputeIRacingForce; no apply call needed.
        private void IRacingGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || IRacingGainSlider == null) return;
            _plugin.Settings.IRacingForceGain = (float)IRacingGainSlider.Value;
            if (IRacingGainText != null)
                IRacingGainText.Text = IRacingGainSlider.Value.ToString("F2");
            UpdateIRacingClipWarning();
            _plugin.PersistSettings();
        }

        // A/B between the two ways of rendering the sub-tick detail. Live: the
        // 1 kHz force path reads the setting every pass, so the wheel changes
        // character under your hands without a restart, which is the only way
        // to compare them honestly.
        private void IRacingForceMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || IRacingForceModeCombo == null) return;
            int mode = IRacingForceModeCombo.SelectedIndex;
            if (mode < 0) mode = 0;
            _plugin.Settings.IRacingForceMode = mode;
            _plugin.PersistSettings();
            UpdateIRacingPredictVisibility();
        }

        private void IRacingMaxNm_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Any keystroke marks the box actively edited, so the periodic
            // re-fill backs off (see MaxNmBoxBeingEdited).
            _maxNmLastTypedUtc = DateTime.UtcNow;
            if (e.Key == System.Windows.Input.Key.Enter) CommitIRacingMaxNm();
        }

        private void IRacingMaxNm_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitIRacingMaxNm();
        }

        // An EMPTY box means "follow iRacing's own number", which is stored as 0.
        // Typing 0 means the same thing rather than "no force": there is no car
        // whose peak torque is zero, so the only sane reading of a 0 is the
        // sentinel. Anything unparseable snaps back to what is really in use, so
        // a typo cannot silently leave the wheel somewhere the box does not show.
        private void CommitIRacingMaxNm()
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || IRacingMaxNmBox == null) return;
            string raw = IRacingMaxNmBox.Text?.Trim();
            double v;
            if (string.IsNullOrEmpty(raw)) v = 0.0;
            else if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.CurrentCulture, out v)
                     && !double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                UpdateIRacingMaxNmText();   // unreadable: restore the live value
                return;
            }
            if (v < 0.0) v = 0.0;
            if (v > 200.0) v = 200.0;   // a sanity ceiling, not a wheel rating
            // Writes whatever the force path is actually using (the active
            // car's slot in per-car mode, else the shared override), so the
            // iRacing-style nudge works: bump the number to make THIS car
            // heavier or lighter without touching Strength.
            _plugin.SetEditableMaxForceNm(v);
            UpdateIRacingMaxNmText();
            UpdateIRacingClipWarning();
        }

        // Predictive clip warning, shown at the moment of adjustment. The dash
        // already reports clipping as it HAPPENS; this is the other half, telling
        // you before you drive that the setting you just chose cannot fit.
        //
        // Uses the real observed peak against the real divisor where possible,
        // rather than assuming Max force came from Auto. Someone who typed a
        // deliberately high Max force is not clipping at Strength 1.2 and should
        // not be told they are. With no peak yet (a fresh car, or straight after
        // an Auto press reset the observer) it falls back to Auto's own 10
        // percent margin, which is the honest assumption when Auto set the value.
        private void UpdateIRacingClipWarning()
        {
            if (IRacingClipWarn == null || _plugin == null) return;
            double strength = _plugin.Settings?.IRacingForceGain ?? 1.0;
            double peak = _plugin.IRacingObservedPeakNm;
            double maxF = _plugin.IRacingEffectiveMaxForceNm;

            double predicted = (peak > 0.5 && maxF > 0.5)
                ? peak * strength / maxF
                : strength / 1.10;

            IRacingClipWarn.Visibility = predicted > 1.0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // One shot, exactly like iRacing's own auto button: drive, press, keep a
        // number you can see. Not a mode that keeps adjusting underneath you.
        private void IRacingAutoMaxForce_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // Refuses before the peak has settled, for the same reason the dash
            // button greys out: pressing on an out-lap calibrates against a force
            // the car has not produced yet, and everything afterwards is too
            // strong with nothing on screen to say why.
            if (!_plugin.IRacingPeakSettled)
            {
                if (IRacingAutoMaxForceStatus != null)
                {
                    IRacingAutoMaxForceStatus.Text = _plugin.IRacingObservedPeakNm <= 0.5
                        ? "Drive a lap first, then press this."
                        : "Still learning what this car pushes. Keep driving; this lights up when the reading has held steady.";
                    IRacingAutoMaxForceStatus.Visibility = Visibility.Visible;
                }
                return;
            }
            double applied = _plugin.ApplyIRacingAutoMaxForce();
            if (IRacingAutoMaxForceStatus != null)
            {
                IRacingAutoMaxForceStatus.Text = applied > 0.5
                    ? "Set to " + applied.ToString("F1") + " Nm. Watching again from now, so a clean lap plus another press redoes it."
                    : "Drive a lap first, then press this.";
                IRacingAutoMaxForceStatus.Visibility = Visibility.Visible;
            }
            _irAutoReadyShown = null;   // force the readiness line to redraw
            RefreshFromPlugin();
        }

        // Mode B auto strength: the same "say what you know" treatment the
        // iRacing panel got. This learner is continuous rather than one-shot, so
        // there is no button to light; what a user needs instead is whether it
        // has enough seat time on this car for its number to mean anything, and
        // what that number currently is.
        private string _mbAutoStrengthShown;
        // The click message holds the line for a few seconds. Without this the
        // 16 ms tick recomputes a learner string, finds it different from what
        // the click just wrote, and stamps over the acknowledgement inside one
        // frame, which reads as a dead button.
        private int _mbAutoStrengthHoldUntilMs;

        private void RefreshModeBAutoStrengthReadiness()
        {
            if (ModeBAutoStrengthStatus == null || _plugin == null) return;
            if (ModeBForzaTuningPanel == null
                || ModeBForzaTuningPanel.Visibility != Visibility.Visible) return;
            if (_mbAutoStrengthHoldUntilMs != 0)
            {
                if (Environment.TickCount - _mbAutoStrengthHoldUntilMs < 0) return;
                _mbAutoStrengthHoldUntilMs = 0;
            }

            string text;
            double conf = _plugin.ModeBStrengthConfidence;
            double scale = _plugin.ModeBAutoStrengthScale;
            bool on = _plugin.Settings?.ModeBAutoStrength == true;
            if (!_plugin.ModeBEnabledForActiveGame)
            {
                text = "";
            }
            else if (!on)
            {
                // The learner keeps running with the box unticked (so ticking it
                // later applies everything learned so far), but nothing is being
                // applied, and saying "applying x1.00" under an unticked box
                // would claim the opposite.
                text = conf >= 1.0
                    ? "This car is learned. Tick the box to apply it."
                    : "Off. It still learns in the background, so ticking it later applies what it has.";
            }
            else if (conf <= 0.0)
            {
                text = "Nothing learned for this car yet. Drive it near the limit for a minute.";
            }
            else if (conf < 1.0)
            {
                text = "Learning this car: " + ((int)Math.Round(conf * 100.0)) + " percent."
                     + " Applying x" + scale.ToString("0.00") + " so far.";
            }
            else
            {
                text = "Learned. Applying x" + scale.ToString("0.00") + " to this car.";
                // A peak pinned to its own sanity ceiling is not a result about
                // the car, it is the learner running out of range, and every car
                // that gets there lands on the same scale. Say so rather than
                // presenting the floor as a measurement.
                if (_plugin.ModeBStrengthRailed)
                    text += "  (At the limit of what it can measure, so this is the"
                          + " smallest scale it will apply.)";
                else if (_plugin.ModeBGripConfidence < 1.0)
                    text += "  (Grip calibration is still settling underneath it.)";
            }

            if (text == _mbAutoStrengthShown) return;
            _mbAutoStrengthShown = text;
            ModeBAutoStrengthStatus.Text = text;
        }

        // Shares RESETGRIP's path: one saved slot holds this car's grip peak and
        // its force peak, so there is no honest way to clear one and keep the
        // other, and the copy says as much.
        private void ModeBRelearnCar_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string status = _plugin.RequestGripCalReset();
            if (ModeBAutoStrengthStatus != null)
            {
                ModeBAutoStrengthStatus.Text = status;
                _mbAutoStrengthShown = status;
                _mbAutoStrengthHoldUntilMs = Environment.TickCount + 6000;
                if (_mbAutoStrengthHoldUntilMs == 0) _mbAutoStrengthHoldUntilMs = 1;
            }
        }

        // Greys the Auto button until the car's peak has stopped climbing, and
        // says how far along that is. Ticked rather than event-driven because
        // readiness is a function of elapsed time, not of anything the user does,
        // and the whole point is that it arrives while they are looking away.
        // Only touches the UI when the displayed state actually changes.
        private bool? _irAutoReadyShown;
        private int _irAutoPctShown = -1;
        private string _irAutoBtnShown;

        private void RefreshIRacingAutoReadiness()
        {
            if (IRacingAutoMaxForceBtn == null || _plugin == null) return;
            if (IRacingTuningPanel == null || IRacingTuningPanel.Visibility != Visibility.Visible) return;

            bool ready = _plugin.IRacingPeakSettled;
            int pct = (int)Math.Round(_plugin.IRacingPeakConfidence * 100.0);
            double offer = _plugin.IRacingLearnedMaxNm;
            // The button CARRIES the number it would write. Next to a text box, a
            // button called "Set" reads as "commit what I typed" and would appear
            // to clobber it; "Use 23.8" can only mean one thing, and it doubles
            // as the readout of what the learner currently thinks, so there is no
            // separate suggestion label to keep in sync.
            string label = offer > 0.5
                ? "Use " + offer.ToString("F1")
                : "Auto";
            if (_irAutoReadyShown == ready && _irAutoPctShown == pct && _irAutoBtnShown == label) return;
            bool wasReady = _irAutoReadyShown == true;
            _irAutoReadyShown = ready;
            _irAutoPctShown = pct;
            _irAutoBtnShown = label;

            IRacingAutoMaxForceBtn.Content = label;
            // Genuinely disabled below settled, not just dimmed: a dim button that
            // still works is a button that says "not yet" and then does it anyway,
            // and taking the number early is exactly the mistake this greying is
            // meant to prevent. Matches the wheel-button action, which refuses.
            IRacingAutoMaxForceBtn.IsEnabled = ready;
            IRacingAutoMaxForceBtn.Opacity = ready ? 1.0 : 0.45;
            if (IRacingAutoMaxForceStatus == null) return;
            // A press leaves its own confirmation on this line; don't stamp over
            // it until the state moves on from where the press left it.
            if (ready)
            {
                if (!wasReady)
                {
                    IRacingAutoMaxForceStatus.Text = "Ready. Finish a clean lap, then take that number.";
                    IRacingAutoMaxForceStatus.Visibility = Visibility.Visible;
                }
            }
            else if (pct > 0)
            {
                IRacingAutoMaxForceStatus.Text = "Learning what this car pushes: " + pct + " percent.";
                IRacingAutoMaxForceStatus.Visibility = Visibility.Visible;
            }
        }

        // Clock gate for the periodic number-box re-fill above (400 ms).
        private DateTime _maxNmLastFillUtc = DateTime.MinValue;

        // Editing detector for that re-fill. IsFocused alone is wrong for it:
        // WPF keyboard focus sticks to the box from one click until something
        // else takes it, so a box touched once was never re-filled again
        // (owner, 2026-08-15: nudges only showed up after tab switches).
        // "Being edited" = focused AND typed in within the last few seconds.
        private DateTime _maxNmLastTypedUtc = DateTime.MinValue;
        private bool MaxNmBoxBeingEdited =>
            IRacingMaxNmBox != null && IRacingMaxNmBox.IsKeyboardFocused
            && (DateTime.UtcNow - _maxNmLastTypedUtc).TotalSeconds < 5.0;

        // Last externally-observed value, for moving the "Set to X" press
        // confirmation when a nudge changes the number underneath it.
        private double _maxNmLastSeen = -1.0;

        private void UpdateIRacingMaxNmText()
        {
            if (IRacingMaxNmText == null) return;
            var st = _plugin?.Settings;
            double shown = 0.0;
            string src = "";
            if (st != null && st.IRacingMaxForcePerCar && st.IRacingMaxForceByCar != null
                && !string.IsNullOrEmpty(_plugin.ActiveCarId)
                && st.IRacingMaxForceByCar.TryGetValue(_plugin.ActiveCarId, out float pc) && pc > 0.5f)
            {
                shown = pc; src = " (this car)";
            }
            else if (st != null && st.IRacingMaxForceNmOverride > 0.5f)
            {
                shown = st.IRacingMaxForceNmOverride;
            }

            // The box carries the number and the label beside it carries where
            // that number came from. Split this way an empty box is a readable
            // state ("following iRacing, which says 18.5") instead of the old
            // slider's hard-left zero, which read as no force at all.
            if (shown > 0.5)
            {
                if (!MaxNmBoxBeingEdited)
                    IRacingMaxNmBox.Text = shown.ToString("F1");
                IRacingMaxNmText.Text = string.IsNullOrEmpty(src)
                    ? "for every car"
                    : "for this car";
                return;
            }
            // Nothing set: we fall back to whatever iRacing itself is using, so
            // say so and show the number. A bare "Auto" would leave the user
            // unable to tell a working fallback from a broken one.
            if (!MaxNmBoxBeingEdited) IRacingMaxNmBox.Text = "";
            double live = _plugin?.IRacingLiveMaxForceNm ?? 0.0;
            IRacingMaxNmText.Text = live > 0.5
                ? "following iRacing, which says " + live.ToString("F1")
                : "following iRacing";
        }

        private void IRacingPredict_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || IRacingPredictSlider == null) return;
            _plugin.Settings.IRacingPredictGain = (float)IRacingPredictSlider.Value;
            if (IRacingPredictText != null)
                IRacingPredictText.Text = IRacingPredictSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        // Prediction only means anything in Replay mode: it exists to cancel a
        // lag that Lead mode never incurs. Hiding it in Lead keeps the panel
        // honest rather than offering a knob that does nothing.
        private void UpdateIRacingPredictVisibility()
        {
            bool replay = _plugin?.Settings?.IRacingForceMode == 1;
            var want = replay ? Visibility.Visible : Visibility.Collapsed;
            if (IRacingPredictRow  != null) IRacingPredictRow.Visibility  = want;
            if (IRacingPredictHelp != null) IRacingPredictHelp.Visibility = want;
        }

        private void IRacingSmooth_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || IRacingSmoothCheck == null) return;
            _plugin.Settings.IRacingUse360Hz = IRacingSmoothCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        // FS's OWN min-force floor (SpringModeMinForce), separate from the
        // Forza one: force characters differ per game (owner call
        // 2026-08-09). Read live by the spring/kick paths; no apply needed.
        private void SpringMinForce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringMinForceSlider == null) return;
            _plugin.Settings.SpringModeMinForce = SpringMinForceSlider.Value;
            if (SpringMinForceText != null)
                SpringMinForceText.Text = SpringMinForceSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        // Implement thud: global-only settings (no per-car draft), applied
        // live via the plugin's public apply.
        // Implement thud is a GLOBAL-ONLY section (no car scope on either
        // side), so the handlers edit ActiveImplementThud (= the global
        // block; no EnsureSectionDraft, there is no car draft to make) and
        // report into the dirty system instead of persisting immediately:
        // changes surface the section's Save/Revert buttons + the star pill.
        private void ImplementThud_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudCheck == null) return;
            _plugin.ActiveImplementThud.Enabled = ImplementThudCheck.IsChecked == true;
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudGainSlider == null) return;
            _plugin.ActiveImplementThud.Gain = (float)ImplementThudGainSlider.Value;
            if (ImplementThudGainText != null)
                ImplementThudGainText.Text = ImplementThudGainSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudTest_Click(object sender, RoutedEventArgs e)
            => _plugin?.TestEffect(_plugin.ImplementThud);

        private void ImplementThudWaveform_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudWaveformCombo == null) return;
            var item = ImplementThudWaveformCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
            if (item?.Content is string name
                && Enum.TryParse<TrueforceForAll.Core.Waveform>(name, out var w))
            {
                _plugin.ActiveImplementThud.Waveform = w;
                _plugin.ApplyImplementThudSettings();
                MarkEffectDirty(EffectKind.ImplementThud);
            }
        }

        private void ImplementThudFreq_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudFreqSlider == null) return;
            _plugin.ActiveImplementThud.Freq = (float)ImplementThudFreqSlider.Value;
            if (ImplementThudFreqText != null)
                ImplementThudFreqText.Text = ((int)ImplementThudFreqSlider.Value).ToString();
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudRaise_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudRaiseSlider == null) return;
            _plugin.ActiveImplementThud.RaiseAmp = (float)ImplementThudRaiseSlider.Value;
            if (ImplementThudRaiseText != null)
                ImplementThudRaiseText.Text = ImplementThudRaiseSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudHum_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudHumSlider == null) return;
            _plugin.ActiveImplementThud.HumAmp = (float)ImplementThudHumSlider.Value;
            if (ImplementThudHumText != null)
                ImplementThudHumText.Text = ImplementThudHumSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudHumFreq_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudHumFreqSlider == null) return;
            _plugin.ActiveImplementThud.HumFreq = (float)ImplementThudHumFreqSlider.Value;
            if (ImplementThudHumFreqText != null)
                ImplementThudHumFreqText.Text = ((int)ImplementThudHumFreqSlider.Value).ToString();
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudBend_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudBendSlider == null) return;
            _plugin.ActiveImplementThud.BendDepth = (float)ImplementThudBendSlider.Value;
            if (ImplementThudBendText != null)
                ImplementThudBendText.Text = ImplementThudBendSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudSpeedPitch_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudSpeedPitchSlider == null) return;
            _plugin.ActiveImplementThud.SpeedPitch = (float)ImplementThudSpeedPitchSlider.Value;
            if (ImplementThudSpeedPitchText != null)
                ImplementThudSpeedPitchText.Text = ImplementThudSpeedPitchSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudSpeedVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudSpeedVolSlider == null) return;
            _plugin.ActiveImplementThud.SpeedVolume = (float)ImplementThudSpeedVolSlider.Value;
            if (ImplementThudSpeedVolText != null)
                ImplementThudSpeedVolText.Text = ImplementThudSpeedVolSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void ImplementThudHarmonic_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.ImplementThud == null
                || ImplementThudHarmonicSlider == null) return;
            _plugin.ActiveImplementThud.HarmonicAmp = (float)ImplementThudHarmonicSlider.Value;
            if (ImplementThudHarmonicText != null)
                ImplementThudHarmonicText.Text = ImplementThudHarmonicSlider.Value.ToString("F2");
            _plugin.ApplyImplementThudSettings();
            MarkEffectDirty(EffectKind.ImplementThud);
        }

        private void SpringDragStrain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringDragStrainSlider == null) return;
            _plugin.Settings.SpringModeDragStrainFraction = SpringDragStrainSlider.Value;
            if (SpringDragStrainText != null)
                SpringDragStrainText.Text = SpringDragStrainSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        private void SpringWeight_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringWeightCheck == null) return;
            _plugin.Settings.SpringModeChassisWeightEnabled = SpringWeightCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void SpringWeightGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || SpringWeightGainSlider == null) return;
            _plugin.Settings.SpringModeChassisWeightGain = SpringWeightGainSlider.Value;
            if (SpringWeightGainText != null)
                SpringWeightGainText.Text = SpringWeightGainSlider.Value.ToString("F2");
            _plugin.PersistSettings();
        }

        // Driver testing mode checkbox (hidden until the DRIVER access code
        // reveals it). Toggles the actual on/off (ExperimentalDriverIntercept)
        // and persists; takes effect on the next plugin start.
        private void DriverIntercept_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _plugin.Settings == null
                || DriverInterceptCheck == null) return;
            bool on = DriverInterceptCheck.IsChecked == true;
            _plugin.Settings.ExperimentalDriverIntercept = on;
            _plugin.PersistSettings();
            if (AccessCodeStatus != null)
                AccessCodeStatus.Text = on
                    ? "Driver testing mode ON (restart SimHub / re-detect to apply). Needs the TFFA filter driver installed."
                    : "Driver testing mode OFF (restart SimHub / re-detect to apply).";
        }

        // Open the manual USB-device picker dialog. Always available, not
        // gated on auto-discovery failing. Users can override our detection
        // at any time. Selection persists across restarts.
        private void UsbPcapPickDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new UsbDevicePickerWindow(_plugin);
            try { dlg.Owner = Window.GetWindow(this); } catch { }
            dlg.ShowDialog();
        }

        private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

        // Privacy policy: what the online features store, why, and how to
        // remove it. Linked from the Community toggle and the Account tab.
        internal const string PrivacyPolicyUrl = "https://github.com/Mhytee/Trueforce-For-All/blob/beta/PRIVACY.md";
        private void PrivacyPolicy_Click(object sender, RoutedEventArgs e) => OpenUrl(PrivacyPolicyUrl);

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(null,
                    $"Couldn't open your browser. Copy this link into it instead:\n\n{url}",
                    ex);
            }
        }

        // Engine-data submission state. Drives both the Engine-section
        // button (visibility + label) and the save-time prompt:
        //   None       = nothing worth submitting (no detection AND no user
        //                data, OR detection present and user agrees -- the
        //                "CONFIRM" case that adds noise without info).
        //   Contribute = no resolver/telemetry detection, user has tuned
        //                non-default engine values. Form is a contribution.
        //   Correct    = resolver/telemetry produced a value, user has
        //                tuned something that disagrees (cylinder count,
        //                layout, custom pattern, or "we said EV, user
        //                implies combustion"). Form is a correction.
        private enum EngineSubmitState { None, Contribute, Correct }

        // Classifier shared by the pin-time prompt and the form body's
        // CONFIRM/CONTRIB/CORRECTION marker. Reads the resolver state via
        // _plugin.EnginePulse and the user's choice via the Car facts pin.
        private EngineSubmitState GetEngineSubmitState()
        {
            if (_plugin == null) return EngineSubmitState.None;
            var ep = _plugin.EnginePulse;
            if (ep == null) return EngineSubmitState.None;

            string src = ep.AutoLayoutSource;
            bool detected = !string.IsNullOrEmpty(src) && ep.AutoLayout.HasValue;
            var (pinLayout, pinCustomId) = _plugin.GetActiveVariantUserEngine();
            var userLayout = pinLayout ?? Effects.EngineLayout.Auto;

            bool layoutDiff = userLayout != Effects.EngineLayout.Auto
                           && (!detected || userLayout != ep.AutoLayout.Value);
            bool customDiff = userLayout == Effects.EngineLayout.Custom
                           && !string.IsNullOrEmpty(pinCustomId);

            bool anyDiff   = layoutDiff || customDiff;
            bool userHasData = userLayout != Effects.EngineLayout.Auto;

            if (!detected)
                return userHasData ? EngineSubmitState.Contribute : EngineSubmitState.None;
            return anyDiff ? EngineSubmitState.Correct : EngineSubmitState.None;
        }

        // Engine-data submission target: a Google Form with a single
        // long-answer field. We URL-encode the structured markdown body
        // and prefill the field via &entry.<id>=<body>. No GitHub account
        // Save-flow prompt: when the user just saved their preset and the
        // committed engine layout diverges from auto-detect, open a small
        // modal showing one checkbox per FIELD that differs (cylinders,
        // engine type) with a "from X -> to Y" description. Whichever boxes
        // they leave ticked get submitted as separate community facts; un-
        // ticking lets the user assert only what they're sure about.
        //
        // Local CarFacts is written whenever the user clicks Submit, using
        // (cyl, config) snapped from the user's Layout choice. We don't
        // try to write a half-correction locally (e.g. cyl only, leave
        // config from auto-detect) - CarFacts variants are atomic and a
        // partial local write would conflict with future community pulls.
        //
        // Classifier (GetEngineSubmitState) is reused unchanged:
        //   Contribute: no detection, user added data.
        //   Correct:    detection present, user's saved values disagree.
        // Custom-engine submission flow. Fires from the engine pin path
        // when the pinned engine is a Custom. Resolves the pinned
        // CustomEngineId to a CustomEngineDef in the library, then runs
        // the one-time car-data consent gate and submits silently via
        // SubmitCustomEngineAsync. Receivers synthesize the def
        // transiently - no library import.
        private void MaybePromptToSubmitCustomEngineData(string carId, string game)
        {
            if (_plugin == null) return;
            var (pinLayout, pinCustomId) = _plugin.GetActiveVariantUserEngine();
            if (pinLayout != Effects.EngineLayout.Custom) return;
            if (string.IsNullOrEmpty(pinCustomId)) return;

            // Look up the def in the user's library. If the pin
            // references a Guid that no longer exists locally there's
            // nothing to share (the engine wouldn't play anyway).
            CustomEngineDef def = null;
            if (_plugin.Settings?.CustomEngines != null)
            {
                foreach (var c in _plugin.Settings.CustomEngines)
                {
                    if (c != null && string.Equals(c.Id, pinCustomId, StringComparison.Ordinal))
                    { def = c; break; }
                }
            }
            if (def == null) return;
            string defName = (def.Name ?? "").Trim();
            if (defName.Length < 2 || defName.Length > 96) return;

            // Dedupe per (car, defId) so same-save-of-same-custom stays
            // silent; swapping to a different custom or built-in
            // re-engages the prompt.
            string dedupeKey = carId + "|custom|" + def.Id;
            if (!_enginePromptedThisSession.Add(dedupeKey)) return;

            // One-time consent gate (replaces the old per-fact share modal).
            // Once granted, measured facts submit silently from here on.
            if (!CarFactsConsentGate.EnsureConsent(Window.GetWindow(this), _plugin)) return;
            SyncAutoSubmitCheckboxFromSettings();

            ShareCustomEngineToCommunity(game, carId, def);
        }

        // Submit a custom-engine correction + optimistic local injection.
        // Shared by the share-modal path and the standing auto-submit path.
        private void ShareCustomEngineToCommunity(string game, string carId, CustomEngineDef def)
        {
            string defName = (def.Name ?? "").Trim();
            string modeStr = def.ElectricMode.ToString().ToUpperInvariant();
            _plugin.SubmitCustomEngineToCommunity(game, carId,
                defName, def.Pattern ?? "", def.IsElectric, modeStr);

            // Optimistic injection: pretend community now reflects the
            // submission. Resolver picks it up immediately; next per-car
            // refetch reconciles server stickiness.
            _engineCommunityCache = new EngineLayoutConsensus
            {
                Layout                = "CUSTOM",
                SupportingSubmissions = (_engineCommunityCache?.SupportingSubmissions ?? 0) + 1,
                Custom                = new CommunityCustomEngine
                {
                    Name         = defName,
                    Pattern      = def.Pattern ?? "",
                    IsElectric   = def.IsElectric,
                    ElectricMode = modeStr,
                },
            };
            _plugin.NotifyCommunityConsensus(game, carId,
                _plugin.ComputeActiveCarVariantSignatureForActive(),
                _engineCommunityCache);
            RenderEngineCommunityRow();
        }

        // Same shape as MaybePromptToSubmitEngineData but for the redline
        // fact. Gated by TELEMETRY SHAPE, not by game: any source that
        // reports MaxRpm but no usable redline is where a user's saved
        // value is a genuine claim worth submitting. When the source DOES
        // report a redline (AC, iRacing) the game itself is the source of
        // truth - so the prompt stays silent.
        private void MaybePromptToSubmitRedlineData(string carId, int? overrideRedline = null)
        {
            if (_plugin == null || string.IsNullOrEmpty(carId)) return;
            // Consent settled as "no" (or granted but community turned off):
            // nothing can be submitted. Bail before burning the per-car
            // dedupe slot so a future consenting session still engages.
            // Sign-in is NOT required: signed-out submissions use the
            // anon fallback id; signed-in ones are keyed to the account.
            if (!CarFactsConsentGate.CanSubmitOrAsk(_plugin)) return;
            string game = _plugin.ActiveGame;
            if (string.IsNullOrEmpty(game)) return;
            var ep = _plugin.EnginePulse;
            if (ep == null) return;

            // Only relevant when the game doesn't expose its own redline
            // (Forza family). With a telemetry redline the game itself
            // is the source of truth - no community correction needed.
            if (ep.ObservedRedlineRpm >= 500) return;
            // The claimed value: an explicit Car facts Set passes it in;
            // otherwise the variant's saved user redline. The 0.85 * MaxRpm
            // fallback is a guess, not a claim, so nothing is submitted
            // when the user never set anything.
            int claimed;
            if (overrideRedline.HasValue) claimed = overrideRedline.Value;
            else
            {
                int? userRl = _plugin.GetActiveVariantUserRedline();
                if (!userRl.HasValue) return;
                claimed = userRl.Value;
            }
            if (claimed < 500 || claimed > 25000) return;
            // Snap to 50 RPM bands: precise enough to be useful, coarse enough
            // that near-identical user values don't fragment consensus.
            int impliedRedline = (int)Math.Round(claimed / 50.0) * 50;

            // Dedupe per (carId, value) so we don't badger on repeat saves
            // of the same threshold; a different threshold re-engages the
            // prompt. Uses the existing engine-prompt set so the two
            // share-flows feel coherent.
            string dedupeKey = carId + "|redline|" + impliedRedline;
            if (!_enginePromptedThisSession.Add(dedupeKey)) return;

            // Skip / phrase against the CONFIRMED community consensus only (>= the
            // confirmed support floor). A lone, unconfirmed value - very often the
            // user's own, reflected back - must never be cited as "the community
            // says X", and must not skip a legitimate correction prompt.
            int? effectiveRedline = _plugin.GetActiveVariantConfirmedCommunityRedline();
            if (effectiveRedline.HasValue
                && Math.Abs(effectiveRedline.Value - impliedRedline) <= 100)
                return;

            // Soft guard against the most common car-facts mistake: submitting
            // the rev LIMITER cutoff instead of the redline start. The limiter
            // sits above the redline and the plausibility band can't reject it,
            // so a value sitting right at the observed max RPM is almost
            // certainly the limiter. Warn once; the user can still submit.
            if (!ConfirmRedlineNotLimiter(impliedRedline)) return;

            // One-time consent gate (replaces the old per-value Submit /
            // Always submit / Not now prompt). Once granted, redline saves
            // submit silently from here on.
            if (!CarFactsConsentGate.EnsureConsent(Window.GetWindow(this), _plugin)) return;
            SyncAutoSubmitCheckboxFromSettings();

            // Include the user's per-gear overrides (snapped to 50) so this path
            // submits the SAME payload shape as the explicit Share button -
            // otherwise the two emit competing consensus candidates.
            var src = _plugin.GetActiveVariantPerGearRedlines();
            var perGear = new System.Collections.Generic.List<GearRedline>();
            if (src != null)
                foreach (var g in src)
                    if (g != null && g.Gear >= 1 && g.Gear <= 16 && g.Rpm >= 500 && g.Rpm <= 25000)
                        perGear.Add(new GearRedline { Gear = g.Gear, Rpm = (int)Math.Round(g.Rpm / 50.0) * 50 });
            _plugin.SubmitRedlineToCommunity(game, carId, impliedRedline, perGear);
            // Don't fake a local consensus: re-fetch the real server value.
            RefreshActiveCommunityRedlineFromServer();
        }

        // Soft guard for the redline share flows: warn when the value about to
        // be submitted sits within 2% of the observed max RPM, because that is
        // almost certainly the rev limiter cutoff, not the redline start we
        // want. The community plausibility band can't reject a limiter value,
        // so this nudge is the only automated defense. Fails open (no ceiling
        // observed = no warning) and never blocks: the user can submit anyway.
        // Returns true to proceed with the submit.
        private bool ConfirmRedlineNotLimiter(int redlineRpm)
        {
            double maxRpm = _plugin?.EnginePulse?.ObservedMaxRpm ?? 0;
            if (maxRpm < 500) return true;                 // no ceiling to compare against
            if (redlineRpm < maxRpm * 0.98) return true;   // comfortably below the limiter
            return TrueforceDialog.Show(Window.GetWindow(this),
                "That looks like the rev limiter",
                $"{redlineRpm} RPM is right at this car's max RPM ({(int)Math.Round(maxRpm)}), which is "
                + "usually the rev limiter cutoff, not the redline. The redline is where the tachometer "
                + "turns red and you should upshift, a little below the limiter. Submit anyway?",
                DialogKind.Warning, okLabel: "Submit anyway", cancelLabel: "Cancel") == true;
        }

        // CONFIRM cases (agrees with detection) and "no data" skip the prompt.
        //
        // Dedupes per car per session so a user who declines isn't badgered
        // on every subsequent save of the same car.
        private void MaybePromptToSubmitEngineData(string carId)
        {
            if (_plugin == null || string.IsNullOrEmpty(carId)) return;
            // Consent settled as "no" (or granted but community turned off):
            // bail before burning the dedupe slot. Mirrors the redline path.
            // Sign-in is NOT required: signed-out submissions use the
            // anon fallback id; signed-in ones are keyed to the account.
            if (!CarFactsConsentGate.CanSubmitOrAsk(_plugin)) return;
            var state = GetEngineSubmitState();
            if (state == EngineSubmitState.None) return;

            string game = _plugin.ActiveGame;
            if (string.IsNullOrEmpty(game)) return;
            var ep = _plugin.EnginePulse;

            // Custom layouts get a dedicated submit path: the def (name +
            // pattern + electric) rides the payload so receivers can
            // synthesize without having it in their library. Auto and
            // Electric never submit - Auto is "I don't know" and Electric
            // is a different feature.
            var (pinLayout, _) = _plugin.GetActiveVariantUserEngine();
            var userLayout = pinLayout ?? Effects.EngineLayout.Auto;
            if (userLayout == Effects.EngineLayout.Custom)
            {
                MaybePromptToSubmitCustomEngineData(carId, game);
                return;
            }
            if (!TryLayoutToCylAndConfig(userLayout, out int userCyl, out var userCfg)) return;

            // Per-session dedupe scoped to BOTH the car AND the layout pick.
            // Saves of the same value after dismissal don't badger; picking a
            // different layout re-engages the prompt so the user can still
            // submit corrections after an initial Not-now.
            string dedupeKey = carId + "|" + userLayout.ToString();
            if (!_enginePromptedThisSession.Add(dedupeKey)) return;

            // Skip the prompt entirely when the user's pick matches the
            // auto-detect: nothing to share.
            if (ep != null && ep.AutoLayout.HasValue && ep.AutoLayout.Value == userLayout) return;

            // One-time consent gate (replaces the old First / Confirming /
            // Alternative share modal and its consensus fetch). Once granted,
            // layout corrections submit silently from here on.
            if (!CarFactsConsentGate.EnsureConsent(Window.GetWindow(this), _plugin)) return;
            SyncAutoSubmitCheckboxFromSettings();

            ShareEngineLayoutToCommunity(game, carId, userLayout);
        }

        // Submit a built-in engine-layout correction + optimistic local
        // injection. Shared by the share-modal path and the standing
        // auto-submit path.
        private void ShareEngineLayoutToCommunity(string game, string carId,
            Effects.EngineLayout userLayout)
        {
            string userLayoutEnum = userLayout.ToString().ToUpperInvariant();

            // Share = submit to community ONLY. The local CarFacts User-source
            // write was removed because writing it made Auto-detect permanently
            // shadow the resolver cascade for this car ("locked correction" -
            // user couldn't go back to auto). The user's submission seeds the
            // consensus row server-side; the next per-car community fetch
            // returns it as the Community-source value.
            _plugin.SubmitEngineLayoutToCommunity(game, carId, userLayout);

            // Optimistic local injection: pretend the community already
            // reflects this submission so the resolver flips the auto-detect
            // label to "(community-confirmed)" without waiting for the next
            // car-change refetch. Next refetch reconciles server stickiness.
            if (_engineCommunityCache == null
                || !string.Equals(_engineCommunityCache.Layout, userLayoutEnum,
                                  StringComparison.OrdinalIgnoreCase))
            {
                _engineCommunityCache = new EngineLayoutConsensus
                {
                    Layout                = userLayoutEnum,
                    SupportingSubmissions = (_engineCommunityCache?.SupportingSubmissions ?? 0) + 1,
                };
            }
            else
            {
                _engineCommunityCache.SupportingSubmissions += 1;
            }
            _plugin.NotifyCommunityConsensus(game, carId,
                _plugin.ComputeActiveCarVariantSignatureForActive(),
                _engineCommunityCache);
            RenderEngineCommunityRow();
        }

        // Sync the Settings checkbox to the persisted AutoSubmitCarFacts
        // value after the consent gate may have flipped it (the gate has no
        // access to this panel's controls; the community checkbox syncs
        // itself via the CommunityEnabledChanged event).
        private void SyncAutoSubmitCheckboxFromSettings()
        {
            if (AutoSubmitCarFactsCheck == null || _plugin?.Settings == null) return;
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try { AutoSubmitCarFactsCheck.IsChecked = _plugin.Settings.AutoSubmitCarFacts; }
            finally { _suppressEvents = prev; }
        }

        // ---------- Community context row (Car facts panel) ----------

        // Fetch the current community consensus for (game, carId) at most
        // once per car change, then render the community-context row with
        // either a "first to share" prompt or the consensus + vote buttons.
        // Called every UI tick from the engine-pulse refresh branch; the
        // _engineCommunityFetchedKey guard makes it a no-op until the
        // active car changes.
        private void MaybeRefreshEngineCommunityContext(string game, string carId, bool force = false)
        {
            if (EngineCommunityText == null) return;
            // Apply gate: if the user isn't using community car facts, show + fetch
            // nothing. (Networking is a separate gate, checked below before fetching.)
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)
                || _plugin == null
                || _plugin.Settings?.UseCommunityCarFacts != true)
            {
                EngineCommunityText.Visibility = System.Windows.Visibility.Collapsed;
                _engineCommunityFetchedKey = null;
                _engineCommunityCache = null;
                return;
            }

            // Key embeds the variant signature so an in-session engine swap (Forza)
            // re-evaluates with the new signature without waiting for a car change.
            string capturedSignature = _plugin.ComputeActiveCarVariantSignatureForActive() ?? "";
            string key = game + "/" + carId + "/" + capturedSignature;
            if (!force && _engineCommunityFetchedKey == key) return;
            _engineCommunityFetchedKey = key;

            // LOCAL-FIRST: apply whatever is cached right now (instant + offline),
            // and reflect it in the community-context row. No network.
            var cached = _plugin.ReplayCommunityCache(game, carId, capturedSignature);
            _engineCommunityCache = cached?.Layout;
            _engineRedlineCache   = cached?.Redline;
            RenderEngineCommunityRow();

            // NETWORK refresh only when the networking master is on AND the
            // cache is stale (past the TTL) or a manual refresh forced it.
            // Sign-in is NOT required: consensus reads are anon-readable
            // (migration 0100) and the fetch primitives fall back to the anon
            // key, so account-free users receive community car facts too. The
            // old sign-in gate here silently starved signed-out users of
            // engine/name/redline facts while 0.2.4 promises them account-free.
            bool networkOn = _plugin.Settings?.CommunityEnabled == true;
            bool stale = !_plugin.IsCommunityCacheFresh(game, carId, capturedSignature);
            if (!networkOn || (!force && !stale)) return;

            if (_engineCommunityFetchInFlight) return;
            _engineCommunityFetchInFlight = true;

            string capturedGame = game;
            string capturedCar  = carId;
            System.Threading.Tasks.Task.Run(() =>
            {
                // All three fact-types share a per-car cadence, so they ride on the
                // same background task. The captured signature is stamped into the
                // cache write + the Notify calls so a swap that lands mid-fetch is
                // recognized as stale.
                EngineLayoutConsensus layoutResult  = null;
                CarNameConsensus      nameResult    = null;
                RedlineConsensus      redlineResult = null;
                try { layoutResult  = _plugin.FetchEngineLayoutConsensus(capturedGame, capturedCar); }
                catch { /* swallowed; keep the cached values */ }
                try { nameResult    = _plugin.FetchCarNameConsensus(capturedGame, capturedCar); }
                catch { /* same */ }
                try { redlineResult = _plugin.FetchRedlineConsensus(capturedGame, capturedCar); }
                catch { /* same */ }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _engineCommunityFetchInFlight = false;
                    if (_engineCommunityFetchedKey != capturedGame + "/" + capturedCar + "/" + capturedSignature) return;
                    // A fetch that returns NOTHING is almost always a network
                    // failure (offline / timeout), and the fetch primitives can't
                    // distinguish that from "this car has no community data". Treat
                    // all-null as "no update": never overwrite or delete a good
                    // cache (or clear the already-applied in-memory values) on a
                    // failed refresh. The replayed cache stays in effect - this is
                    // the offline / expired-but-unreachable path. (A genuinely
                    // empty car simply re-checks on its next visit; harmless.)
                    bool anyData = layoutResult != null || nameResult != null || redlineResult != null;
                    if (!anyData) { RenderEngineCommunityRow(); return; }
                    _engineCommunityCache = layoutResult;
                    _engineRedlineCache   = redlineResult;
                    // Persist the fresh fetch so it applies offline and suppresses
                    // the next refresh until the TTL expires. A fact type that is
                    // genuinely absent rides as null here (backend was reachable,
                    // since something else came back), correctly clearing just that
                    // type; the all-null failure case was handled above.
                    _plugin.WriteCommunityFactCache(capturedGame, capturedCar, capturedSignature,
                        nameResult, layoutResult, redlineResult);
                    _plugin.NotifyCarNameConsensus(capturedGame, capturedCar, nameResult);
                    _plugin.NotifyRedlineConsensus(capturedGame, capturedCar, capturedSignature, redlineResult);
                    _plugin.NotifyCommunityConsensus(capturedGame, capturedCar, capturedSignature, layoutResult);
                    RenderEngineCommunityRow();
                }));
            });
        }

        // Render the passive community-context line under the auto-detect
        // row. Shown only when a consensus actually exists for this car
        // (a "no data" line would be noise). Corrections happen through
        // the dropdown -> save -> share-modal flow, not here.
        private void RenderEngineCommunityRow()
        {
            if (EngineCommunityText == null) return;

            if (_engineCommunityCache == null || _engineCommunityRowRedundant)
            {
                EngineCommunityText.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            string layoutDisplay = _engineCommunityCache.Layout;
            if (Enum.TryParse<Effects.EngineLayout>(_engineCommunityCache.Layout, true, out var consLayout))
                layoutDisplay = Effects.FiringPatternDb.LayoutDisplayName(consLayout);

            int supporters = _engineCommunityCache.SupportingSubmissions;
            string countTag = supporters > 0 ? $" ({supporters})" : "";
            EngineCommunityText.Text = $"Community: {layoutDisplay}{countTag}";
            EngineCommunityText.Visibility = System.Windows.Visibility.Visible;
        }


        // Thin local alias for Effects.FiringPatternDb.TryLayoutToCylAndConfig
        // so existing callers in this file keep their unqualified call sites.
        private static bool TryLayoutToCylAndConfig(Effects.EngineLayout layout,
            out int cyl, out Effects.EngineConfig cfg)
            => Effects.FiringPatternDb.TryLayoutToCylAndConfig(layout, out cyl, out cfg);


        private void EnginePitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EnginePitchText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Pitch = v;
            Apply(EffectKind.Engine);
        }
        private void EngineLowpassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            EngineLowpassText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LowpassHz = v;
            Apply(EffectKind.Engine);
        }
        private void EngineWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.Waveform = WaveformOf(EngineWaveformCombo);
            Apply(EffectKind.Engine);
        }
        private void EngineElectricMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.ElectricMode = EngineElectricModeCombo.SelectedIndex == 1
                ? ElectricCarMode.Silent
                : ElectricCarMode.MutedHum;
            Apply(EffectKind.Engine);
        }
        private void EngineLoadLayer_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LoadLayerEnabled = EngineLoadLayerCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineLoadLayerGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineLoadLayerGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.LoadLayerGain = v;
            Apply(EffectKind.Engine);
        }
        private void EngineHighRpmBoost_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.HighRpmBoostEnabled = EngineHighRpmBoostCheck.IsChecked == true;
            Apply(EffectKind.Engine);
        }
        private void EngineHighRpmBoostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            EngineHighRpmBoostText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Engine);
            _plugin.ActiveEngine.HighRpmBoostAmount = v;
            Apply(EffectKind.Engine);
        }

        // ---------- Car facts engine dropdown (dynamic: built-ins + customs) ----------

        // Each combobox entry is a DropdownItem so the SelectionChanged handler
        // can branch on kind. Built-ins map to an EngineLayout enum value;
        // Custom entries carry the CustomEngineDef they reference.
        private enum EngineDropdownKind { BuiltIn, Custom }
        private sealed class EngineDropdownItem
        {
            public EngineDropdownKind   Kind;
            public Effects.EngineLayout BuiltIn;   // when Kind == BuiltIn
            public CustomEngineDef      Custom;    // when Kind == Custom
            public string               Display;
            public override string ToString() => Display;
        }
        private readonly List<EngineDropdownItem> _engineItems = new List<EngineDropdownItem>();

        // (Removed with the 2026-07 engine centralization: the Engine pulse
        // panel's variant readout row + its Manage link. The Car facts panel's
        // "Manage variants…" link is the one entry point; the manage window
        // itself shows which variant is live.)

        /// <summary>Rebuild the Car facts engine dropdown with the current
        /// built-ins and the user's saved custom engines. Selection reflects
        /// the active variant's engine pin (Auto when none).</summary>
        private void RebuildEngineLayoutDropdown()
        {
            if (CarFactsEngineCombo == null) return;
            Effects.EngineLayout? pinLayout = null;
            string pinCustomId = "";
            if (_plugin != null) (pinLayout, pinCustomId) = _plugin.GetActiveVariantUserEngine();
            var targetLayout   = pinLayout ?? Effects.EngineLayout.Auto;
            var targetCustomId = pinCustomId ?? "";

            _engineItems.Clear();
            foreach (Effects.EngineLayout l in Enum.GetValues(typeof(Effects.EngineLayout)))
            {
                if (l == Effects.EngineLayout.Custom) continue;   // customs are listed by name below
                _engineItems.Add(new EngineDropdownItem
                {
                    Kind    = EngineDropdownKind.BuiltIn,
                    BuiltIn = l,
                    Display = Effects.FiringPatternDb.LayoutDisplayName(l),
                });
            }
            var customs = _plugin?.Settings?.CustomEngines;
            if (customs != null)
            {
                foreach (var c in customs)
                {
                    if (c == null) continue;
                    string name = string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name;
                    _engineItems.Add(new EngineDropdownItem
                    {
                        Kind    = EngineDropdownKind.Custom,
                        Custom  = c,
                        Display = c.IsElectric ? $"{name}  (electric custom)" : $"{name}  (custom)",
                    });
                }
            }
            // Custom-engine authoring lives in the Manage variants modal
            // (footer link) since 2026-07-19, so the dropdown lists only
            // real engine values and carries no action links here.

            // A dangling custom pin (engine deleted elsewhere / restored onto
            // a machine without it) gets its own entry so the combo shows the
            // stored truth instead of silently displaying Auto while the pin
            // still exists (mirrors the variants-modal fix).
            if (pinLayout == Effects.EngineLayout.Custom && !string.IsNullOrEmpty(targetCustomId))
            {
                bool inLibrary = false;
                foreach (var it in _engineItems)
                    if (it.Kind == EngineDropdownKind.Custom
                        && string.Equals(it.Custom?.Id, targetCustomId, StringComparison.Ordinal))
                    { inLibrary = true; break; }
                if (!inLibrary)
                    _engineItems.Add(new EngineDropdownItem
                    {
                        Kind    = EngineDropdownKind.Custom,
                        Custom  = new CustomEngineDef { Id = targetCustomId, Name = "(missing custom engine, falling back to Auto)" },
                        Display = "(missing custom engine, falling back to Auto)",
                    });
            }

            int idx = FindEngineDropdownIndex(targetLayout, targetCustomId);
            bool old = _suppressEvents;
            _suppressEvents = true;
            try
            {
                CarFactsEngineCombo.ItemsSource = null;
                CarFactsEngineCombo.ItemsSource = _engineItems;
                CarFactsEngineCombo.SelectedIndex = idx;
                // The rebuild snapped the selection back to the stored pin, so
                // any deferred browse-commit is now moot.
                _enginePinCommitPending = false;
            }
            finally { _suppressEvents = old; }
        }

        private int FindEngineDropdownIndex(Effects.EngineLayout layout, string customId)
        {
            if (layout == Effects.EngineLayout.Custom)
            {
                for (int i = 0; i < _engineItems.Count; i++)
                {
                    var it = _engineItems[i];
                    if (it.Kind == EngineDropdownKind.Custom
                        && string.Equals(it.Custom?.Id, customId, StringComparison.Ordinal))
                        return i;
                }
                return 0;   // referenced custom missing, fall back to Auto
            }
            for (int i = 0; i < _engineItems.Count; i++)
            {
                var it = _engineItems[i];
                if (it.Kind == EngineDropdownKind.BuiltIn && it.BuiltIn == layout) return i;
            }
            return 0;
        }

        // The Car facts engine picker: the single engine-type entry point
        // since the 2026-07 centralization.
        //
        // Commit discipline: a closed, focused ComboBox raises SelectionChanged
        // on every arrow key / wheel notch, and committing per change persisted
        // a pin (plus, with consent granted, a SILENT community engine
        // submission) per keystroke. So only a dropdown-open change commits
        // immediately; closed-combo browsing sets a pending flag that commits
        // ONCE when the user is done (focus leaves / dropdown closes).
        private bool _enginePinCommitPending;

        private void CarFactsEngine_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (CarFactsEngineCombo != null && !CarFactsEngineCombo.IsDropDownOpen)
            {
                _enginePinCommitPending = true;   // keyboard/wheel browsing: defer
                return;
            }
            _enginePinCommitPending = false;
            ApplyEngineDropdownSelection(CarFactsEngineCombo?.SelectedItem as EngineDropdownItem);
        }

        private void CarFactsEngine_DropDownClosed(object sender, EventArgs e)
            => CommitPendingEnginePin();

        private void CarFactsEngine_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
            => CommitPendingEnginePin();

        private void CommitPendingEnginePin()
        {
            if (!_enginePinCommitPending || _suppressEvents || _plugin == null) return;
            _enginePinCommitPending = false;
            ApplyEngineDropdownSelection(CarFactsEngineCombo?.SelectedItem as EngineDropdownItem);
        }

        // Friendly wording for EnginePulse.AutoLayoutSource tokens, shared by
        // the auto-detected and your-pick readout branches so raw tokens like
        // "(baked)" never reach the user. "user-set" hides the suffix: those
        // variants are stale pre-community-share data, no longer in the
        // resolver cascade, and the line should read as plain auto output.
        private static string FriendlyDetectSourceSuffix(string src)
        {
            if (string.Equals(src, "telemetry", StringComparison.OrdinalIgnoreCase)) return " (from telemetry)";
            if (string.Equals(src, "baked",     StringComparison.OrdinalIgnoreCase)) return " (from built-in car list)";
            if (string.Equals(src, "cache",     StringComparison.OrdinalIgnoreCase)) return " (cached from earlier session)";
            if (string.Equals(src, "community", StringComparison.OrdinalIgnoreCase)) return " (community-confirmed)";
            if (string.Equals(src, "user-set",  StringComparison.OrdinalIgnoreCase)) return "";
            return string.IsNullOrEmpty(src) ? "" : $" (heuristic: {src})";
        }

        // "Your pick" wording: a pinned custom engine reads by its NAME, not
        // the generic "Custom (advanced)" enum label.
        private string DescribePinnedEngine(Effects.EngineLayout layout, string customId)
        {
            if (layout != Effects.EngineLayout.Custom)
                return Effects.FiringPatternDb.LayoutDisplayName(layout);
            var customs = _plugin?.Settings?.CustomEngines;
            if (customs != null && !string.IsNullOrEmpty(customId))
                foreach (var c in customs)
                    if (c != null && string.Equals(c.Id, customId, StringComparison.Ordinal))
                        return string.IsNullOrWhiteSpace(c.Name) ? "a custom engine" : c.Name + " (custom)";
            return "a missing custom engine (falling back to Auto)";
        }

        private void ApplyEngineDropdownSelection(EngineDropdownItem item)
        {
            if (item == null || _plugin == null) return;

            switch (item.Kind)
            {
                case EngineDropdownKind.BuiltIn:
                    CommitEnginePin(item.BuiltIn, "");
                    break;

                case EngineDropdownKind.Custom:
                    CommitEnginePin(Effects.EngineLayout.Custom, item.Custom?.Id ?? "");
                    break;
            }
        }

        // Commit an engine pick as the active variant's Car facts pin. The
        // pin is car truth: it persists in CarFacts (not the preset), wins
        // the resolution cascade, and Auto clears it so detection takes over
        // again. Mirrors CarFactsRedlineSet_Click, including the self-gating
        // community submit.
        private void CommitEnginePin(Effects.EngineLayout layout, string customId)
        {
            if (_plugin == null) return;
            bool ok = _plugin.SaveActiveVariantUserEngine(layout, customId);
            if (!ok)
            {
                // No variant signature yet (telemetry hasn't identified the
                // engine). Snap the dropdown back and tell the user why.
                RebuildEngineLayoutDropdown();
                TrueforceDialog.Show(Window.GetWindow(this), "Engine type",
                    "Drive the car for a moment first. The plugin identifies the engine "
                    + "variant from telemetry, then your pick sticks to that variant.",
                    DialogKind.Info);
                return;
            }
            RebuildEngineLayoutDropdown();
            RefreshCarFactsPanel();
            if (layout != Effects.EngineLayout.Auto)
                MaybePromptToSubmitEngineData(_plugin.ActiveCarId);
        }

        // (The main-panel "Create custom engine…" / "Manage customs…" links were
        // retired 2026-07-19: authoring lives in the Manage variants modal
        // (CarFactsVariantsWindow.CreateCustom_Click), and the Customs tab of
        // the Preset library manages the collection.)

        /// <summary>Bring the inline preset library (Presets tab) forward,
        /// optionally selecting one of its inner tabs. Mutations made there
        /// flow back through OnPresetLibraryChanged, so no post-close refresh
        /// is needed any more.</summary>
        private void ShowPresetManager(PresetManagerControl.InitialTab initialTab = PresetManagerControl.InitialTab.GamePresets)
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.CustomEngines == null)
                _plugin.Settings.CustomEngines = new List<CustomEngineDef>();
            _presetManager?.SelectTab(initialTab);
            if (MainTabs != null && PresetsTab != null)
                MainTabs.SelectedItem = PresetsTab;
        }

        // Called whenever the inline preset library mutates the library
        // (rename / duplicate / delete / set-default / set-active / import /
        // custom-engine edit). Mirrors what the old dialog did on close:
        // re-pull the header combos, rebuild the engine dropdown, and re-apply
        // the live engine so renames / deletions land immediately.
        private void OnPresetLibraryChanged()
        {
            if (_plugin == null) return;
            RefreshFromPlugin();
            RebuildEngineLayoutDropdown();
            Apply(EffectKind.Engine);
            // A custom-engine edit changes resolution inputs (the pinned
            // pattern), so re-run car-facts resolution.
            _plugin.ReresolveActiveCarFacts();
        }

        // Community-list refresh for the active car landed. Drop the
        // (game, carId) cache so the next MaybeRefreshCarCommunityCountAsync
        // re-fetches, then re-fire it + rebuild the active-card car
        // preset picker so the dropdown's "Top community presets"
        // section reflects the new rows immediately.
        private void OnCarCommunityListRefreshed()
        {
            _lastResolvedCarKey    = null;
            _cachedTopForActiveCar = null;
            MaybeRefreshCarCommunityCountAsync();
            RefreshCarPresetPicker();
        }

        // "Edit" on a car-preset row: enter car offline-edit mode (freezes the
        // car so a live telemetry car change can't clobber it or misdirect a
        // save), then land on the Effects tab with the Save / Save as new /
        // Discard banner.
        public void EnterOfflineEditModeForCar(string carId, string presetName)
        {
            if (_plugin == null || string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return;
            if (!_plugin.EnterOfflineEditCar(carId, presetName)) return;
            ClearDirty();
            RefreshFromPlugin();
            if (MainTabs != null) MainTabs.SelectedIndex = 0; // Effects tab
        }


        // Persistent nudge for Forza UDP telemetry: shown when Forza is running
        // but no packets are arriving, so a silent wheel points the user at the
        // Forza UDP setup on the Settings tab.
        private void UpdateUdpSetupBanner(bool forzaNeedsSetup)
        {
            if (UdpSetupBanner == null) return;
            if (forzaNeedsSetup)
            {
                UdpSetupBannerText.Text = "Forza is running but no telemetry is reaching the plugin. Forza sends telemetry over UDP, which has to be turned on in-game (Data Out) and pointed at the plugin.";
                UdpSetupBanner.Visibility = Visibility.Visible;
            }
            else
            {
                UdpSetupBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void UdpSetupBannerButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs != null && SettingsTab != null) MainTabs.SelectedItem = SettingsTab;
            if (UdpTelemetryExpander != null) UdpTelemetryExpander.IsExpanded = true;
            if (ForzaTroubleshootExpander != null)
                ForzaTroubleshootExpander.IsExpanded = true;
            // Defer the scroll until the Settings tab has laid out its content.
            FrameworkElement target = ForzaSection;
            if (target != null)
                Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()),
                    DispatcherPriority.Background);
        }

        // Drives the "running on SimHub fallback" info banner. Distinct from the
        // no-telemetry setup banner: here telemetry IS reaching SimHub, we just
        // want the user to re-point Data Out at the plugin for richer detail.
        private void UpdateForzaFallbackBanner(bool show)
        {
            if (ForzaFallbackBanner == null) return;
            ForzaFallbackBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ForzaFallbackBannerButton_Click(object sender, RoutedEventArgs e)
        {
            // The user is already receiving telemetry (via SimHub), so jump
            // straight to the Forza setup section and the forward fields rather
            // than the "not receiving packets?" troubleshooter.
            if (MainTabs != null && SettingsTab != null) MainTabs.SelectedItem = SettingsTab;
            if (UdpTelemetryExpander != null) UdpTelemetryExpander.IsExpanded = true;
            FrameworkElement target = ForzaSection;
            if (target != null)
                Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()),
                    DispatcherPriority.Background);
        }

        // Home-screen gain tile toggle.
        private void ShowFeedbackBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetShowFeedbackBox(ShowFeedbackBoxCheck.IsChecked == true);
        }

        // Single umbrella toggle: off = no submissions out (CommunityClient
        // returns from ShouldSubmit), and once Backend Phase 2's consensus
        // pull lands, no pulls in either. The flag already gates the submit
        // path today; tying the future pull to the same flag keeps the user
        // model simple. (Apply-vs-network is now split: "Enable community
        // features (online)" is the networking master; "Use community car facts"
        // governs whether car-fact data is applied. See UseCommunityCarFacts_Changed.)
        // The checkbox only flips the setting; SetCommunityEnabled raises
        // CommunityEnabledChanged and OnCommunityEnabledChanged does the UI
        // follow-up. Routing through the event means the share funnel (which also
        // calls SetCommunityEnabled) gets the identical follow-up.
        private void CommunityEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetCommunityEnabled(CommunityEnabledCheck.IsChecked == true);
        }

        private void OnCommunityEnabledChanged(bool on)
        {
            // The event could be raised off the UI thread in theory; marshal so the
            // control writes below are always on the UI thread.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => OnCommunityEnabledChanged(on)));
                return;
            }
            // Keep the checkbox in sync when the toggle came from the share funnel
            // rather than from this checkbox.
            if (CommunityEnabledCheck != null && (CommunityEnabledCheck.IsChecked == true) != on)
            {
                bool prev = _suppressEvents;
                _suppressEvents = true;
                try { CommunityEnabledCheck.IsChecked = on; }
                finally { _suppressEvents = prev; }
            }
            // Re-evaluate the per-car community context next tick (turning
            // networking on lets a stale cache refresh; off stops refreshing
            // but keeps applying the cache via UseCommunityCarFacts).
            _engineCommunityFetchedKey = null;
            // Re-toggling community ON should allow the update-check to
            // re-run; otherwise the per-session latch keeps the
            // notification suppressed until the next plugin restart.
            if (on) _communityUpdatesCheckedThisSession = false;
            RefreshAccountRow();
            UpdateHeaderShareButtons();
            // Coming back online: mark the re-fetchable network caches stale so the
            // next pulls are fresh, without wiping the offline copies.
            if (on) _plugin?.MarkAllNetworkCachesStale();
            // Turning community on/off opens or closes the MOTD gate; refresh the
            // strip, forcing a fetch when it was just enabled (doubles as the
            // manual "pull updates now" lever, bypassing the ~6h cache TTL).
            _motdStrip?.Refresh(forceFetch: on);
            // Re-evaluate the Preset Manager's community gate so the browser
            // appears/disappears immediately instead of staying stale until a
            // manual refresh or restart.
            _presetManager?.RefreshCommunityGate();
        }

        // UI toggle: show or hide the active-card preset Share buttons. Pure UI
        // preference, so persist and re-evaluate the header buttons.
        private void EffectsTabShareButtons_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.ShowEffectsTabShareButtons = EffectsTabShareButtonsCheck.IsChecked == true;
            _plugin.PersistSettings();
            UpdateHeaderShareButtons();
        }

        // UI toggle: show or hide the Car facts per-gear redline editor. Pure UI
        // preference; saved per-gear values keep applying to the wheel, and a
        // variant that has them auto-shows the editor (see RebuildPerGearEditors).
        private void ShowPerGearRedlineEditor_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.ShowPerGearRedlineEditor = ShowPerGearRedlineEditorCheck.IsChecked == true;
            _plugin.PersistSettings();
            RebuildPerGearEditors();
        }

        // MOTD level radio: All / Important / None. None is honored literally
        // (the strip hides); the inline warning makes that tradeoff explicit.
        private void MotdLevel_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            MotdLevel level = MotdLevelNoneRadio?.IsChecked == true ? MotdLevel.None
                            : MotdLevelImportantRadio?.IsChecked == true ? MotdLevel.Important
                            : MotdLevel.All;
            _plugin.SetMotdLevel(level);
            if (MotdNoneWarning != null)
                MotdNoneWarning.Visibility = level == MotdLevel.None ? Visibility.Visible : Visibility.Collapsed;
            _motdStrip?.Refresh();
        }

        // "Use community car facts" toggle: governs whether community names /
        // engine layouts / redlines are APPLIED (cache-first, works offline),
        // independent of the networking master. The plugin applies or clears it
        // on the active car immediately; reset the per-car latch so the context
        // row re-evaluates (replay or hide) on the next tick.
        private void UseCommunityCarFacts_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (UseCommunityCarFactsCheck == null) return;
            bool newOn = UseCommunityCarFactsCheck.IsChecked == true;
            _plugin.SetUseCommunityCarFacts(newOn);
            _engineCommunityFetchedKey = null;
            RefreshFromPlugin();
        }

        // Auto-update toggle. Persists the new value and lets the per-
        // session update-check latch re-run so the user sees auto-applies
        // (or the residual modal) without restarting the plugin.
        private void AutoUpdateDownloadedPresets_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (AutoUpdateDownloadedPresetsCheck == null) return;
            bool newOn = AutoUpdateDownloadedPresetsCheck.IsChecked == true;
            _plugin.Settings.AutoUpdateDownloadedPresets = newOn;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist AutoUpdateDownloadedPresets failed: " + ex.Message);
            }
            if (newOn) _communityUpdatesCheckedThisSession = false;
        }

        // App-update cadence dropdown. Persists the chosen interval (hours);
        // the plugin's background re-poll timer reads it live each tick, so the
        // new cadence takes effect without a restart. 0 = "On startup only".
        private void UpdateCheckIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (!(UpdateCheckIntervalCombo?.SelectedItem is ComboBoxItem item)) return;
            if (!int.TryParse(item.Tag as string, out int hours)) return;
            _plugin.Settings.UpdateCheckIntervalHours = hours;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist UpdateCheckIntervalHours failed: " + ex.Message);
            }
        }

        // Remote dash rev-strip direction radios (Settings tab). Persists the
        // flag; the dash reads it live via Dash.RevOutsideIn each poll, so
        // the strip flips without reloading the dashboard.
        private void RemoteRevDirection_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashRevStripOutsideIn = RemoteRevOutsideInRadio?.IsChecked == true;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist DashRevStripOutsideIn failed: " + ex.Message);
            }
        }

        // Remote dash opening-tab prefs (Settings tab). Remember-last wins
        // while on, so the default-tab dropdown greys out; both only steer
        // which tab the dash STARTS on at the next SimHub launch (the dash
        // keeps its current tab for this session).
        private void RemoteDashRememberTab_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            bool remember = RemoteDashRememberTabCheck?.IsChecked == true;
            _plugin.Settings.DashRememberLastTab = remember;
            if (RemoteDashDefaultTabCombo != null)
                RemoteDashDefaultTabCombo.IsEnabled = !remember;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist DashRememberLastTab failed: " + ex.Message);
            }
        }

        private void RemoteDashDefaultTab_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (!(RemoteDashDefaultTabCombo?.SelectedItem is ComboBoxItem item)) return;
            if (!int.TryParse(item.Tag as string, out int tab)) return;
            _plugin.Settings.DashDefaultTab = tab;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist DashDefaultTab failed: " + ex.Message);
            }
        }

        // ---------- TF4ALL Dash tab layout editor ----------
        // One row per dash tab: enable checkbox + up/down reorder buttons.
        // Rebuilt wholesale after every change (six rows, cheap). The
        // plugin's GetDashTabFullOrder is the single sanitizer for the
        // stored layout, and RefreshDashTabSlots pushes the result to the
        // dash live, so changes apply with no dashboard reload. Display
        // names are indexed by SCREEN index (0=Gains .. 6=Drive), matching
        // DashTabNames on the plugin side, and shared with the default-tab
        // dropdown. Keep it the same LENGTH as that table: a screen missing
        // here shows as "Tab 6" in the editor, and worse, reads as an invalid
        // default and gets quietly reset the next time the editor rebuilds.
        private static readonly string[] RemoteDashTabNames =
            { "Gains", "Car facts", "Effects", "Presets", "Visualizer", "Tele-FFB", "Drive" };

        private static string RemoteDashTabName(int tab) =>
            tab >= 0 && tab < RemoteDashTabNames.Length ? RemoteDashTabNames[tab] : "Tab " + tab;

        // Layout signature of the last-built editor; unchanged signature =
        // skip the rebuild (see below).
        private string _remoteDashTabsSignature;

        private ComboBox[] RemoteDashDriveCombos() => new[]
        {
            RemoteDashDriveSlot0Combo, RemoteDashDriveSlot1Combo,
            RemoteDashDriveSlot2Combo, RemoteDashDriveSlot3Combo,
        };

        /// <summary>Fill the Drive-tab box pickers from the plugin's content
        /// list and select what each slot currently holds. Items are built
        /// once (the list is static); only the selection is re-applied on
        /// later refreshes, so an open dropdown is never rebuilt underneath
        /// the user.</summary>
        private void RefreshRemoteDashDriveEditor()
        {
            var combos = RemoteDashDriveCombos();
            if (combos[0] == null || _plugin == null) return;
            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                var keys   = TrueforcePlugin.DashDriveContentKeys;
                var labels = TrueforcePlugin.DashDriveContentLabels;
                var slots  = _plugin.GetDashDriveSlots();
                for (int i = 0; i < combos.Length; i++)
                {
                    var cb = combos[i];
                    if (cb == null) continue;
                    if (cb.Items.Count != keys.Length)
                    {
                        cb.Items.Clear();
                        for (int k = 0; k < keys.Length; k++)
                            cb.Items.Add(new ComboBoxItem { Content = labels[k], Tag = keys[k] });
                    }
                    string want = i < slots.Length ? slots[i] : "None";
                    foreach (ComboBoxItem it in cb.Items)
                        if ((it.Tag as string) == want) { cb.SelectedItem = it; break; }
                }
                if (RemoteDashDrivePerGameCheck != null)
                    RemoteDashDrivePerGameCheck.IsChecked = _plugin.Settings?.DashDriveSlotsPerGame == true;
                if (RemoteDashFlagsCheck != null)
                    RemoteDashFlagsCheck.IsChecked = _plugin.Settings?.DashFlagsEnabled == true;
                if (RemoteDashPedalsCheck != null)
                    RemoteDashPedalsCheck.IsChecked = _plugin.Settings?.DashDrivePedals != false;
                if (RemoteDashRevCenteredCheck != null)
                    RemoteDashRevCenteredCheck.IsChecked = _plugin.Settings?.DashRevStripCentered == true;
                if (RemoteDashThemeCombo != null)
                {
                    var names = TrueforcePlugin.DashThemeNames();
                    if (RemoteDashThemeCombo.Items.Count != names.Length)
                    {
                        RemoteDashThemeCombo.Items.Clear();
                        foreach (string n in names) RemoteDashThemeCombo.Items.Add(n);
                    }
                    int ti = Array.FindIndex(names, n =>
                        string.Equals(n, _plugin.Settings?.DashTheme, StringComparison.OrdinalIgnoreCase));
                    RemoteDashThemeCombo.SelectedIndex = ti < 0 ? 0 : ti;
                }
                if (RemoteDashSpotterCheck != null)
                    RemoteDashSpotterCheck.IsChecked = _plugin.Settings?.DashSpotterEnabled != false;
                if (RemoteDashIncidentsCheck != null)
                    RemoteDashIncidentsCheck.IsChecked = _plugin.Settings?.DashIncidentsEnabled != false;
                if (RemoteDashIdleCheck != null)
                    RemoteDashIdleCheck.IsChecked = _plugin.Settings?.DashIdleEnabled != false;
                if (RemoteDashIdleDelayBox != null)
                    RemoteDashIdleDelayBox.Text = (_plugin.Settings?.DashIdleDelaySeconds ?? 30).ToString();
                if (RemoteDashIdleNameBox != null)
                    RemoteDashIdleNameBox.Text = _plugin.Settings?.DashIdleDriverName ?? "";
                if (RemoteDashIdleNumberBox != null)
                    RemoteDashIdleNumberBox.Text = _plugin.Settings?.DashIdleNumber ?? "";
                if (RemoteDashIdleNameAboveCheck != null)
                    RemoteDashIdleNameAboveCheck.IsChecked = _plugin.Settings?.DashIdleNameAbove == true;
                if (RemoteDashIdleStyleCombo != null)
                {
                    if (RemoteDashIdleStyleCombo.Items.Count == 0)
                        foreach (string lbl in IdleStyleLabels) RemoteDashIdleStyleCombo.Items.Add(lbl);
                    int si = Array.IndexOf(IdleStyleKeys, _plugin.Settings?.DashIdleStyle ?? "Topo");
                    if (si < 0 && _plugin?.Settings != null)
                    {
                        // A retired style left the box showing the first entry
                        // while the setting still said something else, so the
                        // box and the card disagreed until it was touched.
                        _plugin.Settings.DashIdleStyle = IdleStyleKeys[0];
                        si = 0;
                        PersistIdle();
                    }
                    RemoteDashIdleStyleCombo.SelectedIndex = si < 0 ? 0 : si;
                }
                if (RemoteDashIdleFontCombo != null)
                {
                    if (RemoteDashIdleFontCombo.Items.Count == 0)
                        foreach (string lbl in IdleFontLabels) RemoteDashIdleFontCombo.Items.Add(lbl);
                    int fi = Array.IndexOf(IdleFontValues, _plugin.Settings?.DashIdleFont ?? "");
                    RemoteDashIdleFontCombo.SelectedIndex = fi < 0 ? 0 : fi;
                }
                if (RemoteDashIdleColorCombo != null)
                {
                    if (RemoteDashIdleColorCombo.Items.Count == 0)
                        foreach (string lbl in IdleColorNames) RemoteDashIdleColorCombo.Items.Add(lbl);
                    int ci = Array.IndexOf(IdleColorHex, _plugin.Settings?.DashIdleColor ?? "#FFF2F4F8");
                    RemoteDashIdleColorCombo.SelectedIndex = ci < 0 ? 0 : ci;
                }
                bool twoRows = _plugin.Settings?.DashDriveTwoRows != false;
                if (RemoteDashDriveTwoRowsRadio != null) RemoteDashDriveTwoRowsRadio.IsChecked = twoRows;
                if (RemoteDashDriveOneRowRadio != null) RemoteDashDriveOneRowRadio.IsChecked = !twoRows;
                // The top pair is not drawn in the one-row layout, so its
                // pickers would be lying about what the dash shows.
                if (RemoteDashDriveSlot0Combo != null) RemoteDashDriveSlot0Combo.IsEnabled = twoRows;
                if (RemoteDashDriveSlot1Combo != null) RemoteDashDriveSlot1Combo.IsEnabled = twoRows;
                if (RemoteDashDriveSlot0Label != null) RemoteDashDriveSlot0Label.Opacity = twoRows ? 1.0 : 0.5;
                if (RemoteDashDriveSlot1Label != null) RemoteDashDriveSlot1Label.Opacity = twoRows ? 1.0 : 0.5;
            }
            finally { _suppressEvents = prevSuppress; }
        }

        private void RemoteDashPedals_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashDrivePedals = RemoteDashPedalsCheck?.IsChecked == true;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist DashDrivePedals failed: " + ex.Message);
            }
        }

        private void RemoteDashRevCentered_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashRevStripCentered = RemoteDashRevCenteredCheck?.IsChecked == true;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist DashRevStripCentered failed: " + ex.Message);
            }
        }

        private void RemoteDashSpotter_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashSpotterEnabled = RemoteDashSpotterCheck?.IsChecked == true;
            PersistIdle();
        }

        private void RemoteDashIncidents_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashIncidentsEnabled = RemoteDashIncidentsCheck?.IsChecked == true;
            PersistIdle();
        }

        private void RemoteDashTheme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var names = TrueforcePlugin.DashThemeNames();
            int i = RemoteDashThemeCombo?.SelectedIndex ?? -1;
            if (i < 0 || i >= names.Length) return;
            _plugin.Settings.DashTheme = names[i];
            // No notify needed: the dash polls Dash.Theme.* every update, so
            // it repaints itself on the next frame with no reload.
            PersistIdle();
        }

        // ---------------- idle card ----------------
        // Style and color are fixed lists rather than free text: both feed a
        // dash formula, and a typo there fails silently as a blank card.
        private static readonly string[] IdleStyleKeys   = { "Pipes", "Fractal", "Topo", "Caustics", "Bubbles", "Ribbon", "Wave", "Pulse", "Aurora", "Streaks", "Plain" };
        private static readonly string[] IdleStyleLabels = { "Pipes", "Fractal zoom", "Contours", "Caustics", "Bubbles", "Ribbon", "Wave", "Pulse", "Aurora", "Streaks", "Plain" };
        // Families that ship broadly enough to be there on a phone, a tablet
        // and a PC alike. Empty is the dashboard's own font.
        private static readonly string[] IdleFontValues =
            { "", "Segoe UI", "Arial", "Impact", "Consolas", "Georgia", "Trebuchet MS" };
        private static readonly string[] IdleFontLabels =
            { "Default", "Segoe UI", "Arial", "Impact", "Consolas", "Georgia", "Trebuchet" };

        private static readonly string[] IdleColorHex =
            { "#FFF2F4F8", "#FFE8C547", "#FF37D67A", "#FF4FA3F7", "#FFE5484D", "#FFC77DF5" };
        private static readonly string[] IdleColorNames =
            { "White", "Gold", "Green", "Blue", "Red", "Violet" };

        private void RemoteDashIdle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashIdleEnabled = RemoteDashIdleCheck?.IsChecked == true;
            PersistIdle();
        }

        private void RemoteDashIdleStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int i = RemoteDashIdleStyleCombo?.SelectedIndex ?? -1;
            if (i < 0 || i >= IdleStyleKeys.Length) return;
            _plugin.Settings.DashIdleStyle = IdleStyleKeys[i];
            PersistIdle();
        }

        private void RemoteDashIdleFont_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int i = RemoteDashIdleFontCombo?.SelectedIndex ?? -1;
            if (i < 0 || i >= IdleFontValues.Length) return;
            _plugin.Settings.DashIdleFont = IdleFontValues[i];
            PersistIdle();
        }

        private void RemoteDashIdleColor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int i = RemoteDashIdleColorCombo?.SelectedIndex ?? -1;
            if (i < 0 || i >= IdleColorHex.Length) return;
            _plugin.Settings.DashIdleColor = IdleColorHex[i];
            PersistIdle();
        }

        private void RemoteDashIdleNameAbove_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashIdleNameAbove = RemoteDashIdleNameAboveCheck?.IsChecked == true;
            PersistIdle();
        }

        private void RemoteDashIdleName_Commit(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashIdleDriverName = (RemoteDashIdleNameBox?.Text ?? "").Trim();
            PersistIdle();
        }

        private void RemoteDashIdleNumber_Commit(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashIdleNumber = (RemoteDashIdleNumberBox?.Text ?? "").Trim();
            PersistIdle();
        }

        private void RemoteDashIdleDelay_Commit(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int v;
            if (!int.TryParse((RemoteDashIdleDelayBox?.Text ?? "").Trim(), out v)) v = 30;
            if (v < 0) v = 0;
            if (v > 3600) v = 3600;   // an hour is already "never" in practice
            _plugin.Settings.DashIdleDelaySeconds = v;
            _suppressEvents = true;
            try { if (RemoteDashIdleDelayBox != null) RemoteDashIdleDelayBox.Text = v.ToString(); }
            finally { _suppressEvents = false; }
            PersistIdle();
        }

        // Enter commits without waiting for focus to move, which on a settings
        // page is otherwise the difference between typing a name and keeping it.
        private void RemoteDashIdleName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) RemoteDashIdleName_Commit(sender, null);
        }

        private void RemoteDashIdleNumber_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) RemoteDashIdleNumber_Commit(sender, null);
        }

        private void RemoteDashIdleDelay_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) RemoteDashIdleDelay_Commit(sender, null);
        }

        private void PersistIdle()
        {
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist idle settings failed: " + ex.Message);
            }
        }

        private void RemoteDashFlags_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashFlagsEnabled = RemoteDashFlagsCheck?.IsChecked == true;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist DashFlagsEnabled failed: " + ex.Message);
            }
        }

        private void RemoteDashDriveRows_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashDriveTwoRows = RemoteDashDriveTwoRowsRadio?.IsChecked == true;
            SaveRemoteDashDriveLayout();
        }

        private void RemoteDashDriveSlot_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var combos = RemoteDashDriveCombos();
            var list = new List<string>(combos.Length);
            foreach (var cb in combos)
                list.Add((cb?.SelectedItem as ComboBoxItem)?.Tag as string ?? "None");
            _plugin.SetDashDriveSlots(list);
            SaveRemoteDashDriveLayout();
        }

        /// <summary>Per-game layouts on or off. Nothing is copied either way:
        /// the save below republishes the slot map and re-reads the pickers
        /// from whichever list is in force now, which is the same list as a
        /// moment ago until the user actually re-picks a box.</summary>
        private void RemoteDashDrivePerGame_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.DashDriveSlotsPerGame = RemoteDashDrivePerGameCheck?.IsChecked == true;
            SaveRemoteDashDriveLayout();
        }

        private void SaveRemoteDashDriveLayout()
        {
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist Drive tab layout failed: " + ex.Message);
            }
            // Republish the cached slot map so the dash follows immediately.
            _plugin.RefreshDashTabSlots();
            RefreshRemoteDashDriveEditor();
        }

        private void RebuildRemoteDashTabsEditor()
        {
            if (RemoteDashTabsPanel == null || _plugin?.Settings == null) return;
            // Building rows fires Checked/SelectionChanged; hold events off
            // without clobbering an outer hydration pass's own suppression.
            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                var order = _plugin.GetDashTabFullOrder();
                // Effective, not stored: on a fresh install the factory-off
                // tabs must show unticked here or the editor disagrees with
                // the dash it is editing.
                var disabled = new HashSet<int>(_plugin.DashEffectiveDisabledTabs());
                int enabledCount = 0;
                int firstEnabled = 0;
                bool anyEnabled = false;
                foreach (int t in order)
                    if (!disabled.Contains(t))
                    {
                        enabledCount++;
                        if (!anyEnabled) { firstEnabled = t; anyEnabled = true; }
                    }

                // Normalize the stored default when its tab is hidden or
                // unknown: the dash startup read snaps to the first enabled
                // tab anyway, so COMMIT that snap instead of displaying a
                // value the disk does not hold. Without this the fallback
                // was uncommittable (re-picking the shown item fires no
                // SelectionChanged) and re-enabling the tab weeks later
                // silently resurrected the stale default.
                int def = _plugin.Settings.DashDefaultTab;
                if (anyEnabled && def != firstEnabled
                    && (def < 0 || def >= RemoteDashTabNames.Length || disabled.Contains(def)))
                {
                    _plugin.Settings.DashDefaultTab = firstEnabled;
                    try { _plugin.PersistSettings(); }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Info(
                            "[TF4ALL] Persist DashDefaultTab failed: " + ex.Message);
                    }
                }

                // Skip no-op rebuilds: RefreshFromPlugin runs on every dash
                // action (DashRemoteChanged fires per phone tap), and a
                // wholesale Children.Clear mid mouse-press swallows the
                // click (the pressed control leaves the visual tree) and
                // resets an open default-tab dropdown. Only a real layout
                // or default change rebuilds.
                // Sorted: a set's enumeration order is not part of its
                // contract, and an unstable signature rebuilds on every tap.
                var disabledSig = new List<int>(disabled); disabledSig.Sort();
                string sig = string.Join(",", order) + "|"
                    + string.Join(",", disabledSig) + "|"
                    + _plugin.Settings.DashDefaultTab;
                if (sig == _remoteDashTabsSignature && RemoteDashTabsPanel.Children.Count > 0)
                    return;
                _remoteDashTabsSignature = sig;

                RemoteDashTabsPanel.Children.Clear();
                for (int pos = 0; pos < order.Count; pos++)
                {
                    int tab = order[pos];
                    bool on = !disabled.Contains(tab);
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    var up = new Button
                    {
                        Content = "▲", Width = 26, Height = 20, FontSize = 10,
                        Padding = new Thickness(0),
                        IsEnabled = pos > 0,
                        ToolTip = "Move this tab left on the dash",
                    };
                    int posUp = pos;
                    up.Click += (s, args) => RemoteDashTabMove(posUp, -1);
                    row.Children.Add(up);
                    var down = new Button
                    {
                        Content = "▼", Width = 26, Height = 20, FontSize = 10,
                        Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
                        IsEnabled = pos < order.Count - 1,
                        ToolTip = "Move this tab right on the dash",
                    };
                    int posDown = pos;
                    down.Click += (s, args) => RemoteDashTabMove(posDown, +1);
                    row.Children.Add(down);
                    var check = new CheckBox
                    {
                        Content = RemoteDashTabName(tab),
                        IsChecked = on,
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        // The dash needs at least one screen, so the last
                        // enabled tab's checkbox locks itself.
                        IsEnabled = !(on && enabledCount == 1),
                    };
                    if (on && enabledCount == 1)
                        check.ToolTip = "At least one tab must stay on";
                    int tabId = tab;
                    check.Checked += (s, args) => RemoteDashTabToggle(tabId, true);
                    check.Unchecked += (s, args) => RemoteDashTabToggle(tabId, false);
                    row.Children.Add(check);
                    RemoteDashTabsPanel.Children.Add(row);
                }

                // Default-tab dropdown mirrors the layout: enabled tabs only,
                // in display order. A stored default that is now disabled
                // shows as the first enabled tab, which is exactly what the
                // plugin's startup read snaps to, so the combo never lies.
                if (RemoteDashDefaultTabCombo != null)
                {
                    RemoteDashDefaultTabCombo.Items.Clear();
                    foreach (int t in order)
                    {
                        if (disabled.Contains(t)) continue;
                        RemoteDashDefaultTabCombo.Items.Add(new ComboBoxItem
                        {
                            Tag = t.ToString(),
                            Content = RemoteDashTabName(t),
                        });
                    }
                    SelectComboByTag(RemoteDashDefaultTabCombo,
                        _plugin.Settings.DashDefaultTab.ToString());
                    if (RemoteDashDefaultTabCombo.SelectedIndex < 0
                        && RemoteDashDefaultTabCombo.Items.Count > 0)
                        RemoteDashDefaultTabCombo.SelectedIndex = 0;
                }
            }
            finally { _suppressEvents = prevSuppress; }
        }

        private void RemoteDashTabMove(int pos, int delta)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var order = _plugin.GetDashTabFullOrder();
            int other = pos + delta;
            if (pos < 0 || pos >= order.Count || other < 0 || other >= order.Count) return;
            int tmp = order[pos]; order[pos] = order[other]; order[other] = tmp;
            SaveRemoteDashTabLayout(order, null);
        }

        private void RemoteDashTabToggle(int tab, bool on)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var disabled = _plugin.DashEffectiveDisabledTabs();
            if (on)
            {
                disabled.RemoveAll(t => t == tab);
            }
            else if (!disabled.Contains(tab))
            {
                disabled.Add(tab);
                // The last enabled tab's checkbox is locked, but guard
                // anyway (a rebuild can race a queued click): never let the
                // layout go all-disabled.
                bool anyEnabled = false;
                foreach (int t in _plugin.GetDashTabFullOrder())
                    if (!disabled.Contains(t)) { anyEnabled = true; break; }
                if (!anyEnabled) { RebuildRemoteDashTabsEditor(); return; }
            }
            SaveRemoteDashTabLayout(null, disabled);
        }

        // Persists the layout, pushes it to the live dash slot map, and
        // rebuilds the editor (which also refreshes the default-tab combo).
        // Lists are fresh copies, never mutated after being handed to
        // Settings: the settings serializer may walk them concurrently.
        private void SaveRemoteDashTabLayout(List<int> order, List<int> disabled)
        {
            if (_plugin?.Settings == null) return;
            // The first edit of any kind freezes what the user is currently
            // looking at before applying their change. Without it, reordering
            // tabs on a fresh install would write an order, which flips the
            // layout from "factory" to "configured", which reads the empty
            // disabled list literally and silently switches a factory-off tab
            // back on. After this both lists are taken at face value.
            if (order == null &&
                (_plugin.Settings.DashTabOrder == null || _plugin.Settings.DashTabOrder.Count == 0))
                order = _plugin.GetDashTabFullOrder();
            if (disabled == null &&
                (_plugin.Settings.DashTabsDisabled == null || _plugin.Settings.DashTabsDisabled.Count == 0))
                disabled = _plugin.DashEffectiveDisabledTabs();
            if (order != null) _plugin.Settings.DashTabOrder = order;
            if (disabled != null) _plugin.Settings.DashTabsDisabled = disabled;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist dash tab layout failed: " + ex.Message);
            }
            _plugin.RefreshDashTabSlots();
            RebuildRemoteDashTabsEditor();
        }

        // TF4ALL Dash phone-access funnel (header phone button + Settings
        // tab). Opens our QR dialog (DashPhoneWindow) deep-linked to the
        // dash, so scanning lands straight on TF4ALL Dash instead of
        // SimHub's dashboard list. Our own dialog rather than SimHub's
        // MobileAccessAssistant: that one has no copyable URL and no room
        // for the same-network / add-to-home-screen guidance.
        private void DashPhoneAccess_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                new DashPhoneWindow { Owner = Window.GetWindow(this) }.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Dash phone-access window failed: " + ex.Message);
            }
        }

        // Select the ComboBoxItem whose Tag matches the given string (used to
        // map a stored scalar back onto a fixed dropdown). No-op if none match.
        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null || tag == null) return;
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem item
                    && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return;
                }
        }

        private void AutoSubmitCarFacts_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (AutoSubmitCarFactsCheck == null) return;
            _plugin.Settings.AutoSubmitCarFacts = AutoSubmitCarFactsCheck.IsChecked == true;
            // Toggling here answers the one-time consent ask either way (never
            // re-ask someone who already decided in Settings); turning it on
            // needs the anon submitter id minted.
            _plugin.Settings.CarFactsConsentAsked = true;
            if (_plugin.Settings.AutoSubmitCarFacts) _plugin.EnsureCarFactsAnonId();
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Persist AutoSubmitCarFacts failed: " + ex.Message);
            }
        }

        // ---- Cloud backup / sync (Phase 2) ----

        private void AutoSyncBackup_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (AutoSyncBackupCheck == null) return;
            _plugin.Settings.AutoSyncBackupEnabled = AutoSyncBackupCheck.IsChecked == true;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist AutoSyncBackup failed: " + ex.Message);
            }
            _plugin.UpdateAutoPullTimer();   // start/stop the cloud poll to match the toggle
        }

        // Cross-wheel FFB policy: FFB tuning always backs up; this controls whether
        // it is applied on a restore/sync from a different wheel model (Ask /
        // Always / Never).
        private void CrossWheelFfbMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null || CrossWheelFfbModeCombo == null) return;
            var tag = (CrossWheelFfbModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            CrossWheelFfbMode mode;
            if (!Enum.TryParse(tag, out mode)) mode = CrossWheelFfbMode.Ask;
            _plugin.Settings.CrossWheelFfbMode = mode;
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist CrossWheelFfbMode failed: " + ex.Message);
            }
        }

        // Reflect the plugin's current cross-wheel FFB policy in the combo without
        // re-firing the change handler.
        private void SyncCrossWheelFfbModeCombo()
        {
            if (CrossWheelFfbModeCombo == null || _plugin?.Settings == null) return;
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try { SelectComboByTag(CrossWheelFfbModeCombo, _plugin.Settings.CrossWheelFfbMode.ToString()); }
            finally { _suppressEvents = prev; }
        }

        // Show/hide the "FFB tuning held back" notice from the pending stash a
        // cross-wheel-gated restore left. Called from RefreshFromPlugin and after
        // apply/dismiss.
        private void RefreshCrossWheelFfbNotice()
        {
            if (CrossWheelFfbNotice == null) return;
            bool pending = _plugin != null && _plugin.HasPendingCrossWheelFfb;
            CrossWheelFfbNotice.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            if (pending && CrossWheelFfbNoticeText != null)
            {
                string from = _plugin.PendingCrossWheelFfbSource;
                string here = _plugin.Settings?.LastUsedWheel;
                string fromTxt = string.IsNullOrWhiteSpace(from) ? "another wheel" : from;
                string hereTxt = string.IsNullOrWhiteSpace(here) ? "this wheel" : here;
                CrossWheelFfbNoticeText.Text =
                    $"Force feedback tuning from your {fromTxt} was not applied because this PC uses a {hereTxt}. "
                    + "Your other settings synced normally. Apply the FFB tuning here anyway, or dismiss to keep this PC's tuning.";
            }
        }

        private void CrossWheelFfbApply_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            bool remember = CrossWheelFfbRememberCheck?.IsChecked == true;
            bool applied = _plugin.ApplyPendingCrossWheelFfb(remember);
            if (CrossWheelFfbRememberCheck != null) CrossWheelFfbRememberCheck.IsChecked = false;
            RefreshCrossWheelFfbNotice();
            if (applied)
            {
                // The Mode B / FFB controls (and the policy combo, if remembered)
                // now reflect the applied tuning.
                RefreshFromPlugin();
                SetBackupStatus("FFB tuning applied.");
            }
        }

        private void CrossWheelFfbDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            bool remember = CrossWheelFfbRememberCheck?.IsChecked == true;
            _plugin.DismissPendingCrossWheelFfb(remember);
            if (CrossWheelFfbRememberCheck != null) CrossWheelFfbRememberCheck.IsChecked = false;
            RefreshCrossWheelFfbNotice();
            SyncCrossWheelFfbModeCombo();   // reflect a remembered Never
        }

        private async void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            SetBackupStatus("Backing up…");
            if (BackupNowBtn != null) BackupNowBtn.IsEnabled = false;
            try
            {
                var outcome = await _plugin.BackupNowAsync();
                if (outcome.Status == BackupStatus.Diverged)
                {
                    var dlg = new BackupConflictWindow(outcome.CloudDeviceLabel, FormatBackupWhen(outcome.CloudWhenUtc))
                    {
                        Owner = Window.GetWindow(this),
                    };
                    bool? ok = dlg.ShowDialog();
                    if (ok != true || dlg.Choice == BackupConflictChoice.Cancel) { SetBackupStatus(""); return; }
                    SetBackupStatus("Resolving…");
                    var resolved = await _plugin.ResolveBackupConflictAsync(dlg.Choice, dlg.KeepCloudSettings);
                    SetBackupStatus(resolved.Message);
                }
                else
                {
                    SetBackupStatus(outcome.Message);
                }
            }
            catch (Exception ex) { SetBackupStatus("Couldn't back up. Check the folder and your connection, then try again."); TrueforceDialog.LogError("Backup", ex); }
            finally { if (BackupNowBtn != null) BackupNowBtn.IsEnabled = true; }
        }

        private async void RestoreFromCloud_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var confirm = TrueforceDialog.Show(Window.GetWindow(this),
                "Restore from cloud",
                "Download your cloud backup and apply it to this PC? Your local-only presets are kept; global settings are replaced by the cloud's.",
                DialogKind.Destructive, okLabel: "Restore", cancelLabel: "Cancel");
            if (confirm != true) return;
            SetBackupStatus("Restoring…");
            if (RestoreFromCloudBtn != null) RestoreFromCloudBtn.IsEnabled = false;
            try
            {
                var outcome = await _plugin.RestoreFromCloudAsync();
                SetBackupStatus(outcome.Message);
            }
            catch (Exception ex) { SetBackupStatus("Couldn't restore. Check the folder and your connection, then try again."); TrueforceDialog.LogError("Restore", ex); }
            finally { if (RestoreFromCloudBtn != null) RestoreFromCloudBtn.IsEnabled = true; }
        }

        private void SetBackupStatus(string msg)
        {
            if (BackupStatusText != null) BackupStatusText.Text = msg ?? "";
        }

        // Open Explorer with the just-saved file selected. Invoked by the
        // "Show in folder" link on an inline success line, so revealing is now a
        // user choice rather than an automatic window pop after every export.
        private static void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch { }
        }

        // Inline success line for exports / backups: a plain message plus, when a
        // saved file path is given, a "Show in folder" link that reveals it in
        // Explorer only if clicked. Replaces the old OK-to-dismiss success dialogs
        // and the earlier auto-open-Explorer-on-every-export behavior.
        // Siblings (deliberately separate semantics, don't merge): FlashSaveStatus
        // (paired header save labels, shared timed clear) and ClearStatusAfter
        // (generic per-label timed clear with optional fade).
        internal static void ShowSavedStatus(System.Windows.Controls.TextBlock block, string message, string savedPath)
        {
            if (block == null) return;
            block.Inlines.Clear();
            block.Inlines.Add(new System.Windows.Documents.Run(message ?? ""));
            if (!string.IsNullOrEmpty(savedPath) && System.IO.File.Exists(savedPath))
            {
                block.Inlines.Add(new System.Windows.Documents.Run("   "));
                var link = new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Show in folder"))
                {
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x6F, 0xB1, 0xFF)),
                };
                string p = savedPath;
                link.Click += (s, e) => RevealInExplorer(p);
                block.Inlines.Add(link);
            }
        }

        // ---- Discord link (Phase 2, M5) ----

        // Cancels the in-flight link (browser/loopback wait) on re-click, panel teardown, or
        // sign-out so an abandoned consent can't strand the UI for the full consent timeout.
        private System.Threading.CancellationTokenSource _discordLinkCts;
        private bool _discordLinkInProgress;
        // Newest-wins guard so a slow get_my_discord can't paint stale link state onto a
        // signed-out / switched-user panel. Mirrors _accountStatsGen.
        private int _discordRowGen;

        private async void LinkDiscord_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // While a link is running, the same button cancels it.
            if (_discordLinkInProgress) { try { _discordLinkCts?.Cancel(); } catch { } return; }
            if (!_plugin.AuthIsSignedIn) { SetDiscordStatus("Sign in first, then join Discord."); return; }

            _discordLinkInProgress = true;
            _discordLinkCts?.Dispose();
            _discordLinkCts = new System.Threading.CancellationTokenSource();
            SetDiscordStatus("Opening Discord in your browser…");
            if (LinkDiscordBtn != null) LinkDiscordBtn.Content = "Cancel";
            if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.IsEnabled = false;
            try
            {
                var res = await _plugin.LinkDiscordAsync(_discordLinkCts.Token);
                SetDiscordStatus(res.Message);
                if (res.Ok)
                {
                    _ = _plugin.SyncMyRolesAsync(System.Threading.CancellationToken.None);
                    _ = RefreshAchievementsAndNotifyAsync();
                }
            }
            catch (Exception ex) { SetDiscordStatus("Couldn't link Discord. Check your connection and try again."); TrueforceDialog.LogError("Discord link", ex); }
            finally
            {
                _discordLinkInProgress = false;
                try { _discordLinkCts?.Dispose(); } catch { }
                _discordLinkCts = null;
                if (LinkDiscordBtn != null) { LinkDiscordBtn.Content = "Join Discord"; LinkDiscordBtn.IsEnabled = true; }
                if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.IsEnabled = true;
                await RefreshDiscordRowAsync();
            }
        }

        private async void UnlinkDiscord_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var confirm = TrueforceDialog.Show(Window.GetWindow(this),
                "Unlink Discord",
                "Unlink your Discord account from Trueforce For All? You can re-link any time.",
                DialogKind.Destructive, okLabel: "Unlink", cancelLabel: "Cancel");
            if (confirm != true) return;
            if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.IsEnabled = false;
            try
            {
                var res = await _plugin.UnlinkDiscordAsync(System.Threading.CancellationToken.None);
                SetDiscordStatus(res.Message);
                await RefreshDiscordRowAsync();
            }
            catch (Exception ex) { SetDiscordStatus("Couldn't unlink Discord. Check your connection and try again."); TrueforceDialog.LogError("Discord unlink", ex); }
            finally { if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.IsEnabled = true; }
        }

        private void SetDiscordStatus(string msg)
        {
            if (DiscordLinkStatusText != null) DiscordLinkStatusText.Text = msg ?? "";
        }

        // ---- Supporter badge (Phase 2) ----

        private int _supporterBadgeGen;

        // Show the supporter tier. A dev DISPLAY override (the SUPPORTER access code) wins for
        // preview; otherwise the real entitlement from get_my_entitlement. Display-only: this
        // never gates backup (server-side RLS does). Hidden when not a supporter.
        private async Task RefreshSupporterBadgeAsync()
        {
            if (SupporterBadge == null) return;
            string dev = _plugin?.Settings?.DevSupporterBadgeOverride ?? "";
            if (!string.IsNullOrEmpty(dev)) { ShowSupporterBadge(dev, true); return; }
            if (_plugin == null || !_plugin.AuthIsSignedIn)
            {
                SupporterBadge.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            int gen = unchecked(++_supporterBadgeGen);
            try
            {
                var (isSupporter, tier, _) = await _plugin.GetSupporterTierAsync(System.Threading.CancellationToken.None);
                if (gen != _supporterBadgeGen || _plugin == null || !_plugin.AuthIsSignedIn) return;
                string devNow = _plugin.Settings?.DevSupporterBadgeOverride ?? "";   // override set mid-await still wins
                if (!string.IsNullOrEmpty(devNow)) { ShowSupporterBadge(devNow, true); return; }
                if (isSupporter) ShowSupporterBadge(string.IsNullOrEmpty(tier) ? "Supporter" : tier, false);
                else SupporterBadge.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch { /* leave hidden */ }
        }

        private void ShowSupporterBadge(string tier, bool preview)
        {
            if (SupporterBadge == null || SupporterBadgeText == null) return;
            string t = (tier ?? "").Trim().ToLowerInvariant();
            string accent = t.Contains("platinum") ? "#FFCDD3DE"
                          : t.Contains("gold")     ? "#FFE5C04A"
                          :                          "#FF5AA0E5";
            try
            {
                var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accent);
                var bg = col; bg.A = 0x33;
                SupporterBadge.BorderBrush    = new System.Windows.Media.SolidColorBrush(col);
                SupporterBadge.Background      = new System.Windows.Media.SolidColorBrush(bg);
                SupporterBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(col);
            }
            catch { /* keep XAML defaults */ }
            SupporterBadgeText.Text = (string.IsNullOrEmpty(tier) ? "Supporter" : tier) + (preview ? "  (preview)" : "");
            SupporterBadge.Visibility = System.Windows.Visibility.Visible;
        }

        // ---- Cloud upload gating (supporter-only) ----
        private int _cloudGatingGen;

        // Dev display-only preview of the lapsed cloud-backup state (LAPSED access code). -1 = off;
        // otherwise an index into the representative days-out cycle below. Never persisted, never
        // enables uploads, never touches the real entitlement.
        private int _lapsedPreviewIndex = -1;
        private static readonly int[] _lapsedPreviewDays = { 400, 180, 30, 7, 1 };

        // Gate the cloud UPLOAD controls (Back up now, Auto-sync) on real supporter status, and the
        // whole section on sign-in. Download (Restore from cloud) stays active for any signed-in user,
        // matching the server policy (migration 0034: SELECT ungated, INSERT/UPDATE require
        // is_active_supporter). Three non-supporter states:
        //   * never a supporter (no retain date): plain "supporter feature" note, no restore pitch
        //     (there is nothing stored to restore).
        //   * lapsed (retain date set): contextual lapsed note + an orange "Data removal in: X"
        //     countdown once the deletion date is within the warning window.
        //   * supporter: uploads enabled, no notes.
        // Uses the REAL entitlement, never the dev display override, so the SUPPORTER preview code
        // can't appear to unlock uploads.
        private async Task RefreshCloudBackupGatingAsync()
        {
            if (BackupNowBtn == null) return;   // panel not built yet
            if (_lapsedPreviewIndex >= 0)
            {
                // Dev preview of the lapsed look. Uploads stay OFF (display only, grants nothing);
                // download stays active if signed in. Representative removal date from the cycle.
                if (RestoreFromCloudBtn != null) RestoreFromCloudBtn.IsEnabled = _plugin != null && _plugin.AuthIsSignedIn;
                SetCloudUploadEnabled(false);
                SetCloudBackupMessage("Preview (lapsed): your support has paused, so new cloud backups are off. You can still use \"Restore from cloud\" to download any backup you saved.");
                int dp = _lapsedPreviewDays[_lapsedPreviewIndex];
                SetRemovalCountdown(dp <= 180 ? (DateTime?)DateTime.UtcNow.AddDays(dp) : (DateTime?)null);
                return;
            }
            if (_plugin == null || !_plugin.AuthIsSignedIn)
            {
                if (RestoreFromCloudBtn != null) RestoreFromCloudBtn.IsEnabled = false;
                SetCloudUploadEnabled(false);
                SetCloudBackupMessage(null);
                SetRemovalCountdown(null);
                return;
            }
            if (RestoreFromCloudBtn != null) RestoreFromCloudBtn.IsEnabled = true;   // download stays active
            int gen = unchecked(++_cloudGatingGen);
            bool isSupporter = false;
            DateTime? retain = null;
            try
            {
                var (sup, _, ru) = await _plugin.GetSupporterTierAsync(System.Threading.CancellationToken.None);
                if (gen != _cloudGatingGen || _plugin == null || !_plugin.AuthIsSignedIn) return;
                isSupporter = sup;
                retain = ru;
            }
            catch { /* on error, gate uploads closed (safe default) */ }

            if (isSupporter)
            {
                SetCloudUploadEnabled(true);     // uploads enabled, no notes
                SetCloudBackupMessage(null);
                SetRemovalCountdown(null);
                return;
            }

            // Non-supporter: grey out the upload controls; the download button stays active.
            SetCloudUploadEnabled(false);
            if (retain.HasValue)
            {
                // Lapsed supporter whose backup is still inside the 2-year retention window.
                SetCloudBackupMessage("Your support has paused, so new cloud backups are off. You can still use \"Restore from cloud\" to download any backup you saved.");
                double daysLeft = (retain.Value - DateTime.UtcNow).TotalDays;
                SetRemovalCountdown(daysLeft <= 180 ? retain : (DateTime?)null);   // surface the countdown as removal approaches
            }
            else
            {
                // Never a supporter: nothing is stored, so don't pitch a restore. Just explain the gate.
                SetCloudBackupMessage("Support on Patreon (Account tab) to enable cloud backup.");
                SetRemovalCountdown(null);
            }
        }

        // Enable/disable the two UPLOAD controls together (Back up now + Auto-sync).
        private void SetCloudUploadEnabled(bool canUpload)
        {
            if (BackupNowBtn != null) BackupNowBtn.IsEnabled = canUpload;
            if (AutoSyncBackupCheck != null) AutoSyncBackupCheck.IsEnabled = canUpload;
        }

        // Beta (pre-release) update channel opt-in, open to everyone. Turning it
        // ON asks one confirmation (beta builds are less tested than stable);
        // turning it OFF is always silent, since that's the beta build's
        // switch-back-to-main path. Persists the flag and pushes the resulting
        // channel into the update poller immediately, so a prerelease can
        // surface in the banner without a restart.
        private void BetaUpdates_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;

            // Confirm only a real off-to-on flip, not the panel-load sync of
            // the checkbox to an already-on setting (e.g. after auto-enroll).
            if (BetaUpdatesCheck?.IsChecked == true && !_plugin.Settings.BetaUpdatesEnabled)
            {
                bool? ok = TrueforceDialog.Show(Window.GetWindow(this),
                    "Get beta updates?",
                    "Beta builds bring new effects and fixes first, but they are "
                    + "less tested than stable releases. You can switch back to "
                    + "the main release any time by turning this off.",
                    DialogKind.Confirm, "Get beta updates", "Not now");
                if (ok != true)
                {
                    _suppressEvents = true;
                    try { BetaUpdatesCheck.IsChecked = false; }
                    finally { _suppressEvents = false; }
                    return;
                }
            }

            _plugin.Settings.BetaUpdatesEnabled = BetaUpdatesCheck?.IsChecked == true;
            // Unticking on a beta build is an explicit opt-out: stamp the
            // enroll latch so a later release check can't re-enroll this
            // version behind the user's back (see AcknowledgeBetaOptOut).
            if (BetaUpdatesCheck?.IsChecked != true) _plugin.AcknowledgeBetaOptOut();
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Persist BetaUpdatesEnabled failed: " + ex.Message);
            }
            _plugin.ApplyUpdateChannel();
            RefreshBetaUpdateNote();
        }

        // Refresh the beta note when the Updates section is opened, so it
        // reflects the current channel state on view.
        private void UpdatesExpander_Expanded(object sender, RoutedEventArgs e)
        {
            RefreshBetaUpdateNote();
        }

        // Contextual note under the "beta updates" toggle. Purely local: the
        // channel is open to everyone, so this depends only on the toggle and
        // on whether the running build is itself a prerelease. Also re-affirms
        // the update channel so the poller matches what the note says.
        private void RefreshBetaUpdateNote()
        {
            if (BetaUpdatesCheck == null) return;   // panel not built yet

            SetBetaUpdatesNote(BetaUpdatesCheck.IsChecked == true
                ? "You're on the beta channel: the in-app updater will offer pre-release builds."
                : (_plugin?.UpdateChecker?.CurrentVersionIsPrerelease == true
                    ? "Beta is off: the updater offers the newest main release so you can switch back."
                    : null));

            _plugin?.ApplyUpdateChannel();
        }

        // Contextual note under the beta toggle (channel state). Null or empty
        // hides it.
        private void SetBetaUpdatesNote(string msg)
        {
            if (BetaUpdatesNote == null) return;
            if (string.IsNullOrEmpty(msg))
            {
                BetaUpdatesNote.Visibility = Visibility.Collapsed;
                BetaUpdatesNote.Text = "";
            }
            else
            {
                BetaUpdatesNote.Text = msg;
                BetaUpdatesNote.Visibility = Visibility.Visible;
            }
        }

        // Contextual note under the cloud-backup buttons (supporter gate / lapsed). Null hides it.
        private void SetCloudBackupMessage(string msg)
        {
            if (CloudUploadHint == null) return;
            CloudUploadHint.Text = msg ?? "";
            CloudUploadHint.Visibility = string.IsNullOrEmpty(msg)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        // Orange "Data removal in: X" line, shown only as the deletion date approaches. Null hides it.
        private void SetRemovalCountdown(DateTime? retainUtc)
        {
            if (CloudRemovalText == null) return;
            if (!retainUtc.HasValue)
            {
                CloudRemovalText.Text = "";
                CloudRemovalText.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            double days = (retainUtc.Value - DateTime.UtcNow).TotalDays;
            if (days < 0) days = 0;
            CloudRemovalText.Text = "Data removal in: " + FormatRemoval(days);
            CloudRemovalText.Visibility = System.Windows.Visibility.Visible;
        }

        private static string FormatRemoval(double days)
        {
            if (days < 1) return "less than a day";
            int d = (int)Math.Round(days);
            if (d == 1) return "1 day";
            if (d < 45) return d + " days";
            int months = (int)Math.Round(days / 30.0);
            return months <= 1 ? "about 1 month" : "about " + months + " months";
        }

        // Tab lazy-load. The Account block is expanded by default on its own tab, so
        // Expander.Expanded won't fire on open; refresh account state when the Account tab is
        // selected, and load the supporters wall when the Support tab is selected. SelectionChanged
        // is a routed event that also bubbles up from ComboBoxes inside any tab, so ignore
        // anything that didn't originate from the TabControl itself.
        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
            if (_plugin == null || MainTabs == null) return;
            // Collapse any expanded MOTD message on a view switch so the cycle resumes.
            _motdStrip?.OnHostViewChanged();
            if (AccountTab != null && ReferenceEquals(MainTabs.SelectedItem, AccountTab))
            {
                // Opening the Account tab is explicit intent to see current account state, so force a
                // fresh entitlement + Discord read (otherwise a newly-entitled supporter could stay
                // locked out of backup until the weekly backstop). Invalidate once here; the refreshes
                // below repopulate the cache, and the single-flight coalesces the entitlement double-read
                // (supporter badge + cloud-gating) into one network call.
                _plugin.InvalidateAccountStatusCache();
                // Only fetch stats when the section that shows them is open, so the
                // "Loading..." feedback and any error are actually visible. When it's
                // collapsed, AccountDetails_Expanded fetches on first open instead.
                if (AccountDetailsExpander?.IsExpanded == true) RefreshAccountStats();
                _ = RefreshDiscordRowAsync();
                _ = RefreshPatreonRowAsync();
                _ = RefreshSupporterBadgeAsync();
                _ = RefreshCloudBackupGatingAsync();
            }
            else if (SupportTab != null && ReferenceEquals(MainTabs.SelectedItem, SupportTab))
            {
                _ = RefreshSupportersWallAsync();
            }
            else if (EffectsTab != null && ReferenceEquals(MainTabs.SelectedItem, EffectsTab))
            {
                // Opening the Effects view counts toward auto-retiring NEW badges the
                // user keeps ignoring; refresh the chrome if any badge just cleared.
                if (_plugin.NoteEffectsViewOpened()) RefreshNewBadges();
            }
            else if (TelemetryFfbTab != null && ReferenceEquals(MainTabs.SelectedItem, TelemetryFfbTab))
            {
                // The one-time Mode B intro shows here, on explicit navigation to the
                // section, rather than auto-popping on the home screen. Deferred to
                // Background so the tab paints first and the modal opens over it;
                // MaybeShowModeBIntro no-ops if already seen or the game can't use it.
                Dispatcher.BeginInvoke(new Action(MaybeShowModeBIntro),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private int _supportersWallGen;

        // Load + render the public supporters wall (first name + last initial, sourced
        // from Patreon server-side). Renders even when signed out, but only when the
        // community networking master is on: the wall fetch was the one network call
        // that ignored CommunityEnabled, which broke the privacy policy's "offline
        // until you opt in" statement. Reloaded each time the Account tab is shown so
        // a new patron appears without restarting SimHub.
        private async Task RefreshSupportersWallAsync()
        {
            if (SupportersWallPanel == null || _plugin == null) return;
            StopCloudSim();   // every path below rebuilds or clears the wall
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                // Bump the generation so an in-flight fetch from a previous
                // refresh can't complete and paint the cloud over this notice.
                unchecked { ++_supportersWallGen; }
                SupportersWallPanel.Children.Clear();
                if (SupportersWallStatus != null)
                {
                    SupportersWallStatus.Visibility = System.Windows.Visibility.Visible;
                    SupportersWallStatus.Text = "Turn on community features (Settings tab) to load the supporters wall.";
                }
                return;
            }
            int gen = unchecked(++_supportersWallGen);
            if (SupportersWallStatus != null)
            {
                SupportersWallStatus.Visibility = System.Windows.Visibility.Visible;
                SupportersWallStatus.Text = "Loading supporters…";
            }

            System.Collections.Generic.List<SupportersClient.SupporterRow> rows;
            try { rows = await _plugin.GetSupportersAsync(System.Threading.CancellationToken.None); }
            catch { rows = null; }
            if (gen != _supportersWallGen || SupportersWallPanel == null) return;

            SupportersWallPanel.Children.Clear();
            if (rows == null)
            {
                // Load failed (not configured / unreachable / error) — distinct
                // from a genuinely empty roster so we don't tell real supporters
                // they don't exist.
                if (SupportersWallStatus != null)
                {
                    SupportersWallStatus.Visibility = System.Windows.Visibility.Visible;
                    SupportersWallStatus.Text = "Couldn't load supporters. Check your connection and try again.";
                }
                return;
            }
            if (rows.Count == 0)
            {
                if (SupportersWallStatus != null)
                {
                    SupportersWallStatus.Visibility = System.Windows.Visibility.Visible;
                    SupportersWallStatus.Text = "Be the first to support Trueforce For All.";
                }
                return;
            }
            if (SupportersWallStatus != null) SupportersWallStatus.Visibility = System.Windows.Visibility.Collapsed;
            RenderSupportersCloud(rows);
        }

        // ---- Supporters cloud physics ----
        // The wall is a lightweight physics toy, not a grid: every pill is a soft body
        // spring-anchored to its spot in a rank-ordered spiral (rank 0 in the middle, the
        // rest packed around it), pills shove each other apart on contact, and the mouse
        // plows through and scatters them before they drift home. The sim only runs while
        // something is moving or the cursor is over the canvas, then sleeps.

        private sealed class CloudBody
        {
            public System.Windows.FrameworkElement El;
            public double X, Y, VX, VY, AX, AY, W, H;   // position, velocity, anchor, size
        }

        private System.Windows.Threading.DispatcherTimer _cloudTimer;
        private System.Collections.Generic.List<CloudBody> _cloudBodies;
        private System.Windows.Controls.Canvas _cloudCanvas;
        private System.Windows.Point? _cloudMouse;
        private int _cloudCalmFrames;

        // Re-render support: the first render can run before layout has
        // measured the panel (ActualWidth 0 -> width fallback), and a resized
        // window would otherwise keep a stale canvas width forever.
        private System.Collections.Generic.List<SupportersClient.SupporterRow> _cloudRows;
        private double _cloudRenderedWidth;
        private bool _cloudSizeHooked;

        private void HookCloudSizeOnce()
        {
            if (_cloudSizeHooked || SupportersWallPanel == null) return;
            _cloudSizeHooked = true;
            SupportersWallPanel.SizeChanged += (s, e) =>
            {
                if (_cloudRows == null || _cloudCanvas == null) return;
                if (SupportersWallPanel.Children.Count == 0) return;
                double w = SupportersWallPanel.ActualWidth;
                if (double.IsNaN(w) || w < 100) return;
                double target = Math.Max(240, Math.Min(w, 700));
                if (Math.Abs(target - _cloudRenderedWidth) < 40) return;
                RenderSupportersCloud(_cloudRows);
            };
        }

        private void RenderSupportersCloud(System.Collections.Generic.List<SupportersClient.SupporterRow> rows)
        {
            StopCloudSim();
            // A re-render (size change) replaces the previous canvas; the
            // full refresh path clears the panel separately.
            if (_cloudCanvas != null) SupportersWallPanel.Children.Remove(_cloudCanvas);
            double width = SupportersWallPanel.ActualWidth;
            bool measured = !double.IsNaN(width) && width >= 100;
            width = measured ? Math.Max(240, Math.Min(width, 700)) : 660;
            _cloudRows = rows;
            _cloudRenderedWidth = width;
            HookCloudSizeOnce();

            var canvas = new System.Windows.Controls.Canvas
            {
                Width               = width,
                Background          = System.Windows.Media.Brushes.Transparent,   // hit-test empty space
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            };

            // Measure chips and anchor them along a vertically squashed Archimedean spiral
            // in rank order, so the biggest names sit in the middle of a wide cloud.
            // The spiral is CLAMPED horizontally to the canvas: once a ring hits the
            // walls, growth continues downward (the Support tab scrolls) instead of
            // past them, where the anchor spring and the wall bounce would fight
            // forever and keep the sim awake.
            var bodies = new System.Collections.Generic.List<CloudBody>();
            var placed = new System.Collections.Generic.List<System.Windows.Rect>();
            var inf = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
            double halfW = Math.Max(80, (width - 16) / 2);
            for (int i = 0; i < rows.Count; i++)
            {
                var el = (System.Windows.FrameworkElement)BuildSupporterChip(rows[i], i);
                el.Measure(inf);
                double w = el.DesiredSize.Width, h = el.DesiredSize.Height;
                bool Fits(System.Windows.Rect r) => w >= halfW * 2 || (r.X >= -halfW && r.Right <= halfW);
                var rect = new System.Windows.Rect(-w / 2, -h / 2, w, h);
                for (double t = 0.35; t < 2000 && (CloudOverlaps(rect, placed) || !Fits(rect)); t += 0.35)
                {
                    double r = 4 + 5.5 * t;
                    rect.X = r * Math.Cos(t) - w / 2;
                    rect.Y = 0.62 * r * Math.Sin(t) - h / 2;
                }
                placed.Add(rect);
                bodies.Add(new CloudBody { El = el, W = w, H = h, AX = rect.X, AY = rect.Y });
            }

            // Shift anchors into canvas space and size the canvas to fit the cloud.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var r in placed)
            {
                minX = Math.Min(minX, r.X); minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom);
            }
            double padX = Math.Max(8, (width - (maxX - minX)) / 2);
            canvas.Height = Math.Max(140, (maxY - minY) + 16);
            var rnd = new Random(12345);   // fixed seed: same gentle drift-in every open
            foreach (var b in bodies)
            {
                b.AX += padX - minX; b.AY += 8 - minY;
                b.X = b.AX + rnd.NextDouble() * 40 - 20;
                b.Y = b.AY + rnd.NextDouble() * 30 - 15;
                b.VX = rnd.NextDouble() * 2 - 1;
                b.VY = rnd.NextDouble() * 2 - 1;
                System.Windows.Controls.Canvas.SetLeft(b.El, b.X);
                System.Windows.Controls.Canvas.SetTop(b.El, b.Y);
                canvas.Children.Add(b.El);
            }

            canvas.MouseMove  += (s, e) => { _cloudMouse = e.GetPosition(canvas); StartCloudSim(); };
            canvas.MouseLeave += (s, e) => _cloudMouse = null;
            canvas.IsVisibleChanged += (s, e) => { if (!canvas.IsVisible) StopCloudSim(); };

            _cloudBodies = bodies;
            _cloudCanvas = canvas;
            SupportersWallPanel.Children.Add(canvas);
            StartCloudSim();   // let the drift-in settle
        }

        private static bool CloudOverlaps(System.Windows.Rect rect, System.Collections.Generic.List<System.Windows.Rect> placed)
        {
            rect.Inflate(4, 3);
            foreach (var p in placed) if (rect.IntersectsWith(p)) return true;
            return false;
        }

        private void StartCloudSim()
        {
            if (_cloudTimer == null)
            {
                _cloudTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(16),
                };
                _cloudTimer.Tick += (s, e) => CloudTick();
            }
            _cloudCalmFrames = 0;
            _cloudTimer.Start();
        }

        private void StopCloudSim() => _cloudTimer?.Stop();

        // One 60 fps physics step: anchor springs, mouse shove, pairwise separation,
        // damping, wall bounce. Sleeps itself once everything settles and the cursor left.
        private void CloudTick()
        {
            var bodies = _cloudBodies; var canvas = _cloudCanvas;
            if (bodies == null || canvas == null) { StopCloudSim(); return; }

            foreach (var b in bodies)
            {
                b.VX += (b.AX - b.X) * 0.015;
                b.VY += (b.AY - b.Y) * 0.015;
            }
            if (_cloudMouse is System.Windows.Point m)
            {
                const double reach = 120;
                foreach (var b in bodies)
                {
                    double dx = (b.X + b.W / 2) - m.X, dy = (b.Y + b.H / 2) - m.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1) { dx = 1; dy = 0; d = 1; }
                    if (d < reach)
                    {
                        double f = 1 - d / reach; f = f * f * 2.6;
                        b.VX += dx / d * f; b.VY += dy / d * f;
                    }
                }
            }
            for (int i = 0; i < bodies.Count; i++)
                for (int j = i + 1; j < bodies.Count; j++)
                {
                    var a = bodies[i]; var b = bodies[j];
                    double dx = (a.X + a.W / 2) - (b.X + b.W / 2);
                    double dy = (a.Y + a.H / 2) - (b.Y + b.H / 2);
                    // Rectangle-ish separation: distance normalized by combined half-sizes.
                    double px = (a.W + b.W) / 2 + 6, py = (a.H + b.H) / 2 + 4;
                    double nx = dx / px, ny = dy / py;
                    double nd = Math.Sqrt(nx * nx + ny * ny);
                    if (nd >= 1 || nd < 0.0001) continue;
                    double push = (1 - nd) * 0.9;
                    double ux = nx / nd, uy = ny / nd;
                    a.VX += ux * push; a.VY += uy * push * 1.2;
                    b.VX -= ux * push; b.VY -= uy * push * 1.2;
                }

            bool calm = _cloudMouse == null;
            foreach (var b in bodies)
            {
                b.VX *= 0.86; b.VY *= 0.86;
                double sp = Math.Sqrt(b.VX * b.VX + b.VY * b.VY);
                if (sp > 14) { b.VX *= 14 / sp; b.VY *= 14 / sp; }
                b.X += b.VX; b.Y += b.VY;
                if (b.X < 0) { b.X = 0; b.VX = Math.Abs(b.VX) * 0.5; }
                if (b.Y < 0) { b.Y = 0; b.VY = Math.Abs(b.VY) * 0.5; }
                if (b.X + b.W > canvas.Width)  { b.X = canvas.Width - b.W;  b.VX = -Math.Abs(b.VX) * 0.5; }
                if (b.Y + b.H > canvas.Height) { b.Y = canvas.Height - b.H; b.VY = -Math.Abs(b.VY) * 0.5; }
                System.Windows.Controls.Canvas.SetLeft(b.El, b.X);
                System.Windows.Controls.Canvas.SetTop(b.El, b.Y);
                if (sp > 0.06) calm = false;
            }
            if (calm) { if (++_cloudCalmFrames > 30) StopCloudSim(); }
            else _cloudCalmFrames = 0;
        }

        // A pill per supporter. Patreon patrons are tinted by tier STANDING (server-computed
        // tier_level ordinal: 1 gold, 2 silver, 3+ bronze) and carry a small tier badge on the
        // right; one-time donors stay the neutral blue and show the name alone. The name scales
        // by rank (24px at the top easing toward the 13px baseline): rank is the only amount
        // signal the server sends, and a fixed decay keeps sizes stable as the roster grows.
        private System.Windows.UIElement BuildSupporterChip(SupportersClient.SupporterRow row, int rank)
        {
            double nameSize = 13 + 11 * Math.Pow(0.8, rank);
            bool patreon = string.Equals(row.Source, "patreon", StringComparison.OrdinalIgnoreCase);
            string accent = !patreon || row.TierLevel == null ? "#FF5AA0E5"
                          : row.TierLevel == 1                ? "#FFE5C04A"
                          : row.TierLevel == 2                ? "#FFC6CDDA"
                          :                                     "#FFCD8A54";
            var col = System.Windows.Media.Colors.SteelBlue;
            try { col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accent); } catch { }
            var bg = col; bg.A = 0x22;
            var border = new System.Windows.Controls.Border
            {
                CornerRadius    = new System.Windows.CornerRadius(9),
                Padding         = new System.Windows.Thickness(10, 3, 10, 3),
                Margin          = new System.Windows.Thickness(3),
                Background      = new System.Windows.Media.SolidColorBrush(bg),
                BorderBrush     = new System.Windows.Media.SolidColorBrush(col),
                BorderThickness = new System.Windows.Thickness(1),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            border.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            var hoverScale = new System.Windows.Media.ScaleTransform(1.0, 1.0);
            border.RenderTransform = hoverScale;
            border.MouseEnter += (s, e) => AnimateChipHover(border, hoverScale, bg, true);
            border.MouseLeave += (s, e) => AnimateChipHover(border, hoverScale, bg, false);
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            if (patreon && !string.IsNullOrWhiteSpace(row.Tier))
            {
                var badgeBg = col; badgeBg.A = 0x3A;
                var badge = new System.Windows.Controls.Border
                {
                    CornerRadius      = new System.Windows.CornerRadius(6),
                    Padding           = new System.Windows.Thickness(6, 1, 6, 1),
                    Margin            = new System.Windows.Thickness(8, 0, 0, 0),
                    Background        = new System.Windows.Media.SolidColorBrush(badgeBg),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text       = row.Tier,
                        FontSize   = Math.Max(9, nameSize * 0.55),
                        FontWeight = System.Windows.FontWeights.SemiBold,
                        Foreground = new System.Windows.Media.SolidColorBrush(col),
                    },
                };
                System.Windows.Controls.DockPanel.SetDock(badge, System.Windows.Controls.Dock.Right);
                dock.Children.Add(badge);
                border.ToolTip = "Patreon supporter (" + row.Tier + ")";
            }
            else
            {
                // A Patreon row can arrive with no entitled tier (legacy or
                // custom pledges): still a patron, so never label them
                // one-time.
                border.ToolTip = patreon ? "Patreon supporter" : "One-time supporter";
            }
            var text = new System.Windows.Controls.TextBlock
            {
                Text              = row.Name,
                FontSize          = nameSize,
                FontWeight        = System.Windows.FontWeights.SemiBold,
                Foreground        = new System.Windows.Media.SolidColorBrush(col),
                TextTrimming      = System.Windows.TextTrimming.CharacterEllipsis,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            dock.Children.Add(text);
            border.Child = dock;
            return border;
        }

        // Cloud hover: the pill swells slightly and its fill warms up, then settles back.
        private static void AnimateChipHover(System.Windows.Controls.Border chip,
            System.Windows.Media.ScaleTransform scale, System.Windows.Media.Color restBg, bool entering)
        {
            var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var grow = new System.Windows.Media.Animation.DoubleAnimation(entering ? 1.12 : 1.0,
                TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
            var toBg = restBg; if (entering) toBg.A = 0x55;
            var fill = new System.Windows.Media.Animation.ColorAnimation(toBg,
                TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
            (chip.Background as System.Windows.Media.SolidColorBrush)?.BeginAnimation(
                System.Windows.Media.SolidColorBrush.ColorProperty, fill);
        }

        // ---- Achievement tracker + celebration toast (Phase 2, M5) ----

        private readonly System.Collections.Generic.Queue<(string label, bool needsLink)> _toastQueue = new System.Collections.Generic.Queue<(string label, bool needsLink)>();
        private System.Windows.Threading.DispatcherTimer _toastTimer;
        private int _achievementsRefreshGen;
        private int _toastPreviewIndex;
        private bool _discordLinked;          // cached link state for the toast's "Link" button
        private bool _achievementsWindowOpen; // single-flight guard for the tracker modal

        private void AchievementCrown_Click(object sender, RoutedEventArgs e) => _ = OpenAchievementsWindowAsync();

        private void AchievementToast_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Ignore clicks that land on a child button (× close or the Link button).
            var d = e.OriginalSource as System.Windows.DependencyObject;
            while (d is System.Windows.Media.Visual) { if (d is Button) return; d = System.Windows.Media.VisualTreeHelper.GetParent(d); }
            DismissToast();
            _ = OpenAchievementsWindowAsync();
        }

        private void AchievementToastLink_Click(object sender, RoutedEventArgs e)
        {
            DismissToast();
            _ = OpenAchievementsWindowAsync(startLinking: true);   // opens the tracker straight into "check your browser"
        }

        private void AchievementToastClose_Click(object sender, RoutedEventArgs e) => DismissToast();

        // Open the Achievements tracker: clear the new-achievement dot, fire an immediate
        // self-scoped role sync (claim what's earned), and show the latest progress.
        private async Task OpenAchievementsWindowAsync(bool startLinking = false)
        {
            if (_plugin == null) return;
            if (!_plugin.AuthIsSignedIn)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Achievements",
                    "Sign in to see your achievements.",
                    DialogKind.Info);
                return;
            }
            if (_achievementsWindowOpen) return;   // single-flight: don't stack modal windows
            _achievementsWindowOpen = true;
            try
            {
                DismissToast();   // a queued toast can't re-trigger while the tracker is open

                System.Collections.Generic.List<AchievementClient.AchievementRow> list = null;
                bool linked = false;
                try
                {
                    list = await _plugin.GetAchievementsAsync(System.Threading.CancellationToken.None);
                    var (isLinked, _) = await _plugin.GetDiscordStatusAsync(System.Threading.CancellationToken.None);
                    linked = isLinked; _discordLinked = isLinked;
                }
                catch { }
                if (list == null)
                {
                    TrueforceDialog.Show(Window.GetWindow(this), "Achievements",
                        "Couldn't load your achievements right now. Try again in a moment.",
                        DialogKind.Info);
                    return;
                }

                // Clear the new-achievement dot only AFTER a successful load.
                if (_plugin.Settings != null && _plugin.Settings.AchievementUnseen)
                {
                    _plugin.Settings.AchievementUnseen = false;
                    try { _plugin.PersistSettings(); } catch { }
                }
                RefreshAchievementCrown();

                _ = _plugin.SyncMyRolesAsync(System.Threading.CancellationToken.None);   // claim in the background

                bool showCeleb = _plugin.Settings?.ShowAchievementCelebrations ?? true;
                var win = new AchievementsWindow(list, linked, showCeleb,
                    onLinkAsync: () => LinkDiscordCoreAsync(),
                    onReloadAsync: async () =>
                    {
                        var fresh = await _plugin.GetAchievementsAsync(System.Threading.CancellationToken.None);
                        var (lk, _u) = await _plugin.GetDiscordStatusAsync(System.Threading.CancellationToken.None);
                        _discordLinked = lk;
                        if (lk) _ = _plugin.SyncMyRolesAsync(System.Threading.CancellationToken.None);
                        return (fresh, lk);
                    },
                    onToggleCelebrations: (on) =>
                    {
                        if (_plugin?.Settings == null) return;
                        _plugin.Settings.ShowAchievementCelebrations = on;
                        try { _plugin.PersistSettings(); } catch { }
                    },
                    autoStartLink: startLinking)
                { Owner = Window.GetWindow(this) };
                win.ShowDialog();
            }
            finally { _achievementsWindowOpen = false; }
        }

        private async Task<bool> LinkDiscordCoreAsync()
        {
            if (_plugin == null) return false;
            if (_discordLinkInProgress) return false;
            if (!_plugin.AuthIsSignedIn) { SetDiscordStatus("Sign in first, then join Discord."); return false; }
            _discordLinkInProgress = true;
            _discordLinkCts?.Dispose();
            _discordLinkCts = new System.Threading.CancellationTokenSource();
            bool ok = false;
            try
            {
                var res = await _plugin.LinkDiscordAsync(_discordLinkCts.Token);
                SetDiscordStatus(res.Message);
                ok = res.Ok && _plugin.AuthIsSignedIn;
                if (ok)
                {
                    _ = _plugin.SyncMyRolesAsync(System.Threading.CancellationToken.None);
                    _ = RefreshAchievementsAndNotifyAsync();
                }
            }
            catch (Exception ex) { SetDiscordStatus("Couldn't link Discord. Check your connection and try again."); TrueforceDialog.LogError("Discord link", ex); ok = false; }
            finally
            {
                _discordLinkInProgress = false;
                try { _discordLinkCts?.Dispose(); } catch { }
                _discordLinkCts = null;
                await RefreshDiscordRowAsync();
            }
            return ok;
        }

        // Header crown + its new-achievement dot.
        private void RefreshAchievementCrown()
        {
            if (AchievementCrownButton == null) return;
            bool signedIn = _plugin != null && _plugin.AuthIsSignedIn;
            AchievementCrownButton.Visibility = signedIn ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (AchievementCrownDot != null)
                AchievementCrownDot.Visibility = (signedIn && _plugin.Settings?.AchievementUnseen == true)
                    ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        // Fetch progress, detect genuinely-new earns vs the saved baseline, celebrate (toast
        // if enabled) + raise the notify dot. Seeds silently on first run for an account.
        private async Task RefreshAchievementsAndNotifyAsync()
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn || _plugin.Settings == null) return;
            int gen = unchecked(++_achievementsRefreshGen);
            System.Collections.Generic.List<AchievementClient.AchievementRow> list;
            try { list = await _plugin.GetAchievementsAsync(System.Threading.CancellationToken.None); }
            catch { return; }
            if (list == null || gen != _achievementsRefreshGen || _plugin?.Settings == null || !_plugin.AuthIsSignedIn) return;

            // The baseline is keyed by account id so switching users (or a leaked baseline on
            // disk) re-seeds silently for the new account instead of diffing against the old one.
            string uid = _plugin.Settings.AuthSession?.UserId ?? "";
            var earned = new System.Collections.Generic.List<string>();
            foreach (var a in list) if (a.Earned) earned.Add(a.Key);
            earned.Sort(StringComparer.Ordinal);
            string newCsv = earned.Count == 0 ? "-" : string.Join(",", earned);
            string newStored = uid + "|" + newCsv;

            string stored = _plugin.Settings.AchievementBaseline ?? "";
            int sep = stored.IndexOf('|');
            string storedUid = sep >= 0 ? stored.Substring(0, sep) : null;
            string storedCsv = sep >= 0 ? stored.Substring(sep + 1) : null;

            if (storedCsv == null || storedUid != uid)
            {
                // First run for this account (or switched accounts): seed silently.
                _plugin.Settings.AchievementBaseline = newStored;
                if (_plugin.Settings.AchievementUnseen) { _plugin.Settings.AchievementUnseen = false; RefreshAchievementCrown(); }
                try { _plugin.PersistSettings(); } catch { }
                return;
            }

            var prevSet = new System.Collections.Generic.HashSet<string>(
                storedCsv == "-" ? new string[0] : storedCsv.Split(','), StringComparer.Ordinal);
            var fresh = new System.Collections.Generic.List<AchievementClient.AchievementRow>();
            foreach (var a in list) if (a.Earned && !prevSet.Contains(a.Key)) fresh.Add(a);

            if (fresh.Count > 0)
            {
                _plugin.Settings.AchievementBaseline = newStored;
                _plugin.Settings.AchievementUnseen = true;
                try { _plugin.PersistSettings(); } catch { }
                RefreshAchievementCrown();
                if (_plugin.Settings.ShowAchievementCelebrations)
                    foreach (var a in fresh) EnqueueAchievementToast(a.Label ?? a.Key, !_discordLinked);
            }
            else if (newStored != stored)
            {
                _plugin.Settings.AchievementBaseline = newStored;   // lost one / set changed
                try { _plugin.PersistSettings(); } catch { }
            }
        }

        private void EnqueueAchievementToast(string label, bool needsLink)
        {
            if (string.IsNullOrEmpty(label)) label = "Achievement";
            _toastQueue.Enqueue((label, needsLink));
            if (AchievementToast != null && AchievementToast.Visibility != System.Windows.Visibility.Visible)
                ShowNextToast();
        }

        private void ShowNextToast()
        {
            if (AchievementToast == null) return;
            if (_toastQueue.Count == 0) { HideToast(); return; }
            var (label, needsLink) = _toastQueue.Dequeue();
            if (AchievementToastBody != null)
                AchievementToastBody.Text = label + (needsLink ? ".  Link your Discord to join the server and claim the role." : ".  Click to view.");
            if (AchievementToastLinkBtn != null)
                AchievementToastLinkBtn.Visibility = needsLink ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            AchievementToast.Visibility = System.Windows.Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ShowNextToast(); };
            }
            _toastTimer.Stop(); _toastTimer.Start();
        }

        private void HideToast()
        {
            if (_toastTimer != null) _toastTimer.Stop();
            if (AchievementToast != null) AchievementToast.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void DismissToast()
        {
            _toastQueue.Clear();
            HideToast();
        }

        // Dev preview: fire a toast cycling through achievements, WITHOUT touching the real
        // baseline/"earned once" state (so it never suppresses a genuine first-earn).
        private async Task PreviewAchievementToastAsync()
        {
            string label = "Tuner";
            try
            {
                var list = _plugin != null ? await _plugin.GetAchievementsAsync(System.Threading.CancellationToken.None) : null;
                if (list != null && list.Count > 0)
                    label = list[_toastPreviewIndex % list.Count].Label ?? "Achievement";
            }
            catch { }
            bool needsLink = (_toastPreviewIndex % 2 == 1);   // alternate so both toast variants preview
            _toastPreviewIndex++;
            EnqueueAchievementToast(label, needsLink);
        }

        // WARNEMAIL dev code: which warning stage (1..5) the next trigger sends. Cycles 1->5->1.
        private int _warnPreviewStage = 1;

        // Email the stage-N backup-deletion warning to the signed-in user's own address, so we
        // can preview the escalating copy. The server scopes it to the JWT email claim and never
        // touches the real entitlement / retention timer.
        private async Task SendWarnPreviewAndReport(int stage)
        {
            if (_plugin == null) return;
            try
            {
                var (ok, message) = await _plugin.SendWarnEmailPreviewAsync(stage, System.Threading.CancellationToken.None);
                if (AccessCodeStatus != null) AccessCodeStatus.Text = message;
            }
            catch (Exception ex) { if (AccessCodeStatus != null) AccessCodeStatus.Text = "Couldn't send the email. Check your connection and try again."; TrueforceDialog.LogError("Warning email", ex); }
        }

        // Reflect the current link state. Signed out -> prompt + disabled; signed in -> query
        // get_my_discord and show "Linked as <name>" with the Unlink button. Skips button churn
        // while a link is in progress, and uses a generation + signed-in re-check so a stale
        // in-flight result can't clobber a newer (signed-out / switched-user) state.
        private async Task RefreshDiscordRowAsync()
        {
            if (_plugin == null || DiscordLinkStatusText == null) return;
            if (_discordLinkInProgress) return;           // don't fight the live link flow
            int gen = unchecked(++_discordRowGen);
            if (!_plugin.AuthIsSignedIn)
            {
                SetDiscordStatus("Sign in to join Discord.");
                if (LinkDiscordBtn != null) { LinkDiscordBtn.IsEnabled = false; LinkDiscordBtn.Content = "Join Discord"; LinkDiscordBtn.ToolTip = "Sign in first, then you can join the community Discord."; }
                if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            if (LinkDiscordBtn != null) LinkDiscordBtn.IsEnabled = true;
            try
            {
                var (linked, username) = await _plugin.GetDiscordStatusAsync(System.Threading.CancellationToken.None);
                // Bail if a newer refresh started, a link began, or the user signed out mid-call.
                if (gen != _discordRowGen || _plugin == null || _discordLinkInProgress || !_plugin.AuthIsSignedIn) return;
                _discordLinked = linked;
                if (linked)
                {
                    SetDiscordStatus(string.IsNullOrEmpty(username) ? "Joined the community Discord." : "Joined as " + username + ".");
                    if (LinkDiscordBtn != null) { LinkDiscordBtn.Content = "Linked"; LinkDiscordBtn.ToolTip = "You're a member of the community Discord. Wooo!"; }
                    if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    SetDiscordStatus("Joins you to the community Discord and links your account, so your contributions earn roles.");
                    if (LinkDiscordBtn != null) { LinkDiscordBtn.Content = "Join Discord"; LinkDiscordBtn.ToolTip = "You're not a member of the community Discord yet. Click to join."; }
                    if (UnlinkDiscordBtn != null) UnlinkDiscordBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch { /* leave the current text */ }
        }

        // ---- Patreon link (mirror of the Discord link handlers) ----
        private bool _patreonLinkInProgress;
        private System.Threading.CancellationTokenSource _patreonLinkCts;
        private int _patreonRowGen;

        private async void LinkPatreon_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // While a link is running, the same button cancels it.
            if (_patreonLinkInProgress) { try { _patreonLinkCts?.Cancel(); } catch { } return; }
            if (!_plugin.AuthIsSignedIn) { SetPatreonStatus("Sign in first, then link Patreon."); return; }

            _patreonLinkInProgress = true;
            _patreonLinkCts?.Dispose();
            _patreonLinkCts = new System.Threading.CancellationTokenSource();
            SetPatreonStatus("Opening Patreon in your browser…");
            if (LinkPatreonBtn != null) LinkPatreonBtn.Content = "Cancel";
            if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.IsEnabled = false;
            try
            {
                var res = await _plugin.LinkPatreonAsync(_patreonLinkCts.Token);
                SetPatreonStatus(res.Message);
                if (res.Ok)
                {
                    // A successful link may have flipped supporter status and/or auto-linked Discord.
                    _ = RefreshSupporterBadgeAsync();
                    _ = RefreshCloudBackupGatingAsync();
                    _ = RefreshDiscordRowAsync();
                }
            }
            catch (Exception ex) { SetPatreonStatus("Couldn't link Patreon. Check your connection and try again."); TrueforceDialog.LogError("Patreon link", ex); }
            finally
            {
                _patreonLinkInProgress = false;
                try { _patreonLinkCts?.Dispose(); } catch { }
                _patreonLinkCts = null;
                if (LinkPatreonBtn != null) { LinkPatreonBtn.Content = "Link Patreon"; LinkPatreonBtn.IsEnabled = true; }
                if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.IsEnabled = true;
                await RefreshPatreonRowAsync();
            }
        }

        private async void UnlinkPatreon_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var confirm = TrueforceDialog.Show(Window.GetWindow(this),
                "Unlink Patreon",
                "Unlink your Patreon account from Trueforce For All? You can re-link any time.",
                DialogKind.Destructive, okLabel: "Unlink", cancelLabel: "Cancel");
            if (confirm != true) return;
            if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.IsEnabled = false;
            try
            {
                var res = await _plugin.UnlinkPatreonAsync(System.Threading.CancellationToken.None);
                SetPatreonStatus(res.Message);
                await RefreshPatreonRowAsync();
            }
            catch (Exception ex) { SetPatreonStatus("Couldn't unlink Patreon. Check your connection and try again."); TrueforceDialog.LogError("Patreon unlink", ex); }
            finally { if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.IsEnabled = true; }
        }

        private void SetPatreonStatus(string msg)
        {
            if (PatreonLinkStatusText != null) PatreonLinkStatusText.Text = msg ?? "";
        }

        private async Task RefreshPatreonRowAsync()
        {
            if (_plugin == null || PatreonLinkStatusText == null) return;
            if (_patreonLinkInProgress) return;           // don't fight the live link flow
            int gen = unchecked(++_patreonRowGen);
            if (!_plugin.AuthIsSignedIn)
            {
                SetPatreonStatus("Sign in to link Patreon.");
                if (LinkPatreonBtn != null) { LinkPatreonBtn.IsEnabled = false; LinkPatreonBtn.Content = "Link Patreon"; }
                if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            if (LinkPatreonBtn != null) LinkPatreonBtn.IsEnabled = true;
            try
            {
                var (linked, name) = await _plugin.GetPatreonStatusAsync(System.Threading.CancellationToken.None);
                if (gen != _patreonRowGen || _plugin == null || _patreonLinkInProgress || !_plugin.AuthIsSignedIn) return;
                if (linked)
                {
                    SetPatreonStatus(string.IsNullOrEmpty(name) ? "Linked." : "Linked as " + name + ".");
                    if (LinkPatreonBtn != null) LinkPatreonBtn.Content = "Linked";
                    if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    SetPatreonStatus("Link Patreon to unlock the supporter badge and cloud backup from your pledge.");
                    if (LinkPatreonBtn != null) LinkPatreonBtn.Content = "Link Patreon";
                    if (UnlinkPatreonBtn != null) UnlinkPatreonBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch { /* leave the current text */ }
        }

        private static string FormatBackupWhen(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("g");
            return "";
        }

        // Sync the Account expander to the current plugin auth state.
        // Sign-in is independent of the Community toggle - users can
        // claim a username even with community pull/push off.
        // Username is server-authoritative: SharingAuthor mirrors
        // profile.username when signed in, or is blank for anonymous.
        // Identity = signed-in username OR anonymous. No freeform.
        private void RefreshAccountRow()
        {
            // Keep the header-card chip in lockstep with the Settings-tab
            // Account section. The chip has its own null guards, so refresh
            // it even when the Account section's controls aren't realized.
            RefreshAccountChip();
            // Auth state may have just changed (sign-in / sign-out / account
            // switch); flip the Preset Manager community gate to match. Placed
            // before the early-return below so it runs even when the Account
            // section controls aren't realized (RefreshCommunityGate self-guards).
            if (_plugin != null) _presetManager?.RefreshCommunityGate();
            if (AccountStatusLabel == null || _plugin == null) return;
            if (_plugin.AuthIsSignedIn)
            {
                string email = _plugin.AuthSignedInEmail ?? "(unknown email)";
                AccountStatusLabel.Text = "Signed in as " + email + ".";
                AccountAuthBtn.Content = "Sign out";
                if (AccountChangeEmailRow != null)
                {
                    AccountChangeEmailRow.Visibility = System.Windows.Visibility.Visible;
                    if (AccountEmailValue != null) AccountEmailValue.Text = email;
                }
                string uname = _plugin.Settings?.SharingAuthor ?? "";
                if (AccountUsernameDisplay != null)
                    AccountUsernameDisplay.Text = string.IsNullOrEmpty(uname)
                        ? "(not set yet)"
                        : uname;
                if (AccountChangeUsernameBtn != null)
                    AccountChangeUsernameBtn.Visibility = System.Windows.Visibility.Visible;
                if (AccountUsernameHelp != null) AccountUsernameHelp.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                AccountStatusLabel.Text = "Not signed in. Sign in to use community features.";
                AccountAuthBtn.Content = "Sign in";
                if (AccountChangeEmailRow != null)
                    AccountChangeEmailRow.Visibility = System.Windows.Visibility.Collapsed;
                if (AccountUsernameDisplay != null)
                    AccountUsernameDisplay.Text = "(anonymous)";
                if (AccountChangeUsernameBtn != null)
                    AccountChangeUsernameBtn.Visibility = System.Windows.Visibility.Collapsed;
                if (AccountUsernameHelp != null) AccountUsernameHelp.Visibility = System.Windows.Visibility.Visible;
            }
            if (AccountAuthorStatus != null) AccountAuthorStatus.Text = "";
            AccountAuthBtn.IsEnabled = true;
            _ = RefreshDiscordRowAsync();
            _ = RefreshPatreonRowAsync();
            _ = RefreshSupporterBadgeAsync();
            _ = RefreshCloudBackupGatingAsync();
        }

        // Header-card account chip. Signed out -> "Sign in" (no icon/caret).
        // Signed in -> the username (or email prefix) with a circle + caret
        // hinting at the dropdown. Tooltip switches to match.
        private void RefreshAccountChip()
        {
            if (AccountChipButton == null || _plugin == null) return;
            if (_plugin.AuthIsSignedIn)
            {
                string uname = _plugin.Settings?.SharingAuthor;
                string label = !string.IsNullOrEmpty(uname)
                    ? uname
                    : EmailPrefix(_plugin.AuthSignedInEmail);
                if (string.IsNullOrEmpty(label)) label = "Account";
                if (AccountChipText  != null) AccountChipText.Text       = label;
                if (AccountChipIcon  != null) AccountChipIcon.Visibility  = System.Windows.Visibility.Visible;
                if (AccountChipCaret != null) AccountChipCaret.Visibility = System.Windows.Visibility.Visible;
                // The icon doubles as a connection dot: green = connected,
                // amber = signed in but can't reach the server, grey = community
                // features off. Live transitions are picked up by the meter tick.
                var dot = ComputeAccountDot();
                ApplyAccountDot(dot);
                AccountChipButton.ToolTip = BuildAccountChipTooltip(dot);
            }
            else
            {
                if (AccountChipText  != null) AccountChipText.Text       = "Sign in";
                if (AccountChipIcon  != null) AccountChipIcon.Visibility  = System.Windows.Visibility.Collapsed;
                if (AccountChipCaret != null) AccountChipCaret.Visibility = System.Windows.Visibility.Collapsed;
                AccountChipButton.ToolTip = "Sign in to share presets and use community car data.";
                _lastAccountDot = null;
            }
            RefreshAchievementCrown();
        }

        // Signed out -> open the sign-in dialog (same flow as the Settings-tab
        // Account button). Signed in -> drop a small menu: jump to the full
        // Account section, or sign out.
        private void AccountChip_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (!_plugin.AuthIsSignedIn)
            {
                var dialog = new SignInWindow(_plugin) { Owner = Window.GetWindow(this) };
                bool? ok = dialog.ShowDialog();
                if (ok == true)
                {
                    BootstrapUsernameAfterSignIn();
                    RefreshAccountRow();
                }
                return;
            }

            var menu = new ContextMenu();
            var achievements = new MenuItem { Header = "Achievements…" };
            achievements.Click += (s, ev) => { _ = OpenAchievementsWindowAsync(); };
            var manage = new MenuItem { Header = "Account settings…" };
            manage.Click += (s, ev) => OpenAccountSection();
            var signOut = new MenuItem { Header = "Sign out" };
            signOut.Click += (s, ev) =>
            {
                var confirm = TrueforceDialog.Show(
                    Window.GetWindow(this),
                    "Sign out",
                    "Sign out of your community account?",
                    DialogKind.Destructive, okLabel: "Sign out", cancelLabel: "Cancel");
                if (confirm != true) return;
                _plugin.AuthSignOut();
                RefreshAccountRow();
            };
            menu.Items.Add(achievements);
            menu.Items.Add(manage);
            menu.Items.Add(signOut);
            menu.PlacementTarget = AccountChipButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Bring the Account tab forward (the chip's "Account settings…" target). The account
        // section is the whole Account tab now (no inner expander), so navigating there is enough.
        private void OpenAccountSection()
        {
            if (MainTabs != null && AccountTab != null) MainTabs.SelectedItem = AccountTab;
        }

        // Rename your username. Opens PickUsernameWindow seeded with
        // the current value; server enforces uniqueness via set_username.
        private async void AccountChangeUsername_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn) return;
            string current = _plugin.Settings?.SharingAuthor ?? "";
            // Pre-resolve a default in case the current username is null
            // (rare; shouldn't happen post-signup but covers the edge).
            string seed = string.IsNullOrEmpty(current)
                ? await ResolveAvailableUsernameSeedAsync(EmailPrefix(_plugin.AuthSignedInEmail))
                : current;
            var picker = new PickUsernameWindow(_plugin, seed)
            {
                Owner = Window.GetWindow(this),
            };
            bool? ok = picker.ShowDialog();
            if (ok == true && !string.IsNullOrEmpty(picker.ChosenUsername))
            {
                _plugin.Settings.SharingAuthor = picker.ChosenUsername;
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                if (AuthorNameBox    != null) AuthorNameBox.Text    = picker.ChosenUsername;
                if (AccountAuthorBox != null) AccountAuthorBox.Text = picker.ChosenUsername;
                RefreshAccountRow();
            }
        }

        private static string EmailPrefix(string email)
        {
            if (string.IsNullOrEmpty(email)) return "";
            int at = email.IndexOf('@');
            return at > 0 ? email.Substring(0, at) : email;
        }

        // Compat shim for the original CommunityEnabled_Changed call site.
        private void RefreshCommunityAuthRow() => RefreshAccountRow();

        private void AccountAuth_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (_plugin.AuthIsSignedIn)
            {
                var confirm = TrueforceDialog.Show(
                    Window.GetWindow(this),
                    "Sign out",
                    "Sign out of your community account?",
                    DialogKind.Destructive, okLabel: "Sign out", cancelLabel: "Cancel");
                if (confirm != true) return;
                try { _discordLinkCts?.Cancel(); } catch { }
                _plugin.AuthSignOut();
                RefreshAccountRow();
                return;
            }
            var dialog = new SignInWindow(_plugin) { Owner = Window.GetWindow(this) };
            bool? ok = dialog.ShowDialog();
            if (ok == true)
            {
                BootstrapUsernameAfterSignIn();
                RefreshAccountRow();
            }
        }

        // Walk preferred -> preferred2 -> preferred3 ... until we find
        // an available username (or hit the cap). Returns whatever's
        // available, or the bare cleaned preferred when nothing in the
        // first 100 variants is free.
        private async Task<string> ResolveAvailableUsernameSeedAsync(string preferred)
        {
            if (string.IsNullOrWhiteSpace(preferred)) return "";
            // Strip anything the username regex would reject so the
            // server's first availability check doesn't fail on format.
            var sb = new System.Text.StringBuilder();
            foreach (var ch in preferred)
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            string cleaned = sb.ToString();
            if (cleaned.Length < 3) return cleaned;
            if (cleaned.Length > 32) cleaned = cleaned.Substring(0, 32);

            UsernameAvailability r;
            try { r = await _plugin.AuthCheckUsernameAvailableAsync(cleaned); }
            catch { return cleaned; }
            if (r == UsernameAvailability.Available || r == UsernameAvailability.SelfOwned)
                return cleaned;
            // Only auto-suffix on collision; format / reserved / network
            // failures fall through so the user picks manually.
            if (r != UsernameAvailability.Taken) return cleaned;

            for (int n = 2; n <= 99; n++)
            {
                string suffix = n.ToString();
                string baseStr = cleaned;
                if (baseStr.Length + suffix.Length > 32)
                    baseStr = baseStr.Substring(0, 32 - suffix.Length);
                string candidate = baseStr + suffix;
                UsernameAvailability cr;
                try { cr = await _plugin.AuthCheckUsernameAvailableAsync(candidate); }
                catch { return cleaned; }
                if (cr == UsernameAvailability.Available) return candidate;
            }
            return cleaned;
        }

        // After a successful sign-in, reconcile the local SharingAuthor
        // with the user's server-side profile.username.
        //   * If they already have a profile username -> sync into
        //     SharingAuthor (their username always wins over the local
        //     freeform value when signed in).
        //   * If their profile is missing username -> open
        //     PickUsernameWindow seeded from email prefix (or existing
        //     SharingAuthor if it passes the format check). The server
        //     enforces uniqueness; the modal renders live availability.
        private async void BootstrapUsernameAfterSignIn()
        {
            if (_plugin?.Settings == null) return;
            if (!_plugin.AuthIsSignedIn) return;

            (bool signedIn, string _, string username) = (false, null, null);
            try { (signedIn, _, username) = await _plugin.AuthGetMyProfileAsync(); }
            catch { /* fall through to prompt with seeded default */ }
            if (!signedIn) return;

            if (!string.IsNullOrWhiteSpace(username))
            {
                // Server has a username; make local Author match.
                _plugin.Settings.SharingAuthor = username;
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                if (AuthorNameBox    != null) AuthorNameBox.Text    = username;
                if (AccountAuthorBox != null) AccountAuthorBox.Text = username;
                // Repaint the visible Account label + header chip, which read
                // SharingAuthor. AuthorNameBox/AccountAuthorBox are hidden
                // binding stubs, so without this the account row keeps showing
                // "(not set yet)" until the next refresh - this method is
                // async-void, so RefreshAccountRow already ran (with an empty
                // SharingAuthor) before this server fetch resolved. This is the
                // path a second machine hits: the username exists server-side
                // but only lands locally here.
                RefreshAccountRow();
                return;
            }

            // Signed in but the server profile has no username: any local
            // SharingAuthor is a leftover (signed-out freeform alias, or a
            // previous account's name - plain sign-out doesn't clear it),
            // and the account row would present it as a username the upload
            // RPCs reject. Clear it so local state agrees with the server
            // before we offer the picker; keep the value only as a picker
            // seed fallback. Safe to set the boxes directly:
            // AccountAuthor_Changed is LostFocus-wired, so programmatic
            // Text assignment doesn't fire a rename RPC.
            string staleAlias = _plugin.Settings.SharingAuthor ?? "";
            if (!string.IsNullOrWhiteSpace(staleAlias))
            {
                _plugin.Settings.SharingAuthor = "";
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                if (AuthorNameBox    != null) AuthorNameBox.Text    = "";
                if (AccountAuthorBox != null) AccountAuthorBox.Text = "";
                RefreshAccountRow();
            }

            // No username yet - open the picker with an already-available
            // default so the user can just hit Save. Prefer the email
            // prefix; fall back to the freeform alias we just cleared.
            // If the bare prefix is taken, try +2, +3, ... up to +99
            // before giving up and seeding the picker with the bare
            // prefix (user types something else manually).
            string emailLocal = _plugin.AuthSignedInEmail ?? "";
            int atIdx = emailLocal.IndexOf('@');
            string preferred = atIdx > 0 ? emailLocal.Substring(0, atIdx) : emailLocal;
            if (string.IsNullOrWhiteSpace(preferred)) preferred = staleAlias;
            string seed = await ResolveAvailableUsernameSeedAsync(preferred);

            // Owner re-fetch after the await: SimHub caches the control,
            // so this async-void path may resume on an unloaded control
            // (Window.GetWindow returns null). Bail rather than open an
            // un-owned floaty picker. Log the skip so a later "set a
            // username first" upload error can be traced back to this
            // missed bootstrap (otherwise it surfaces as an opaque
            // backend error with no breadcrumb).
            var owner = Window.GetWindow(this);
            if (owner == null)
            {
                SimHub.Logging.Current.Info(
                    "[TF4ALL] Username prompt skipped: Trueforce panel unloaded before bootstrap finished. " +
                    "Re-open the Account tab to pick a username.");
                return;
            }
            var picker = new PickUsernameWindow(_plugin, seed) { Owner = owner };
            bool? ok = picker.ShowDialog();
            if (ok == true && !string.IsNullOrEmpty(picker.ChosenUsername))
            {
                _plugin.Settings.SharingAuthor = picker.ChosenUsername;
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                if (AuthorNameBox    != null) AuthorNameBox.Text    = picker.ChosenUsername;
                if (AccountAuthorBox != null) AccountAuthorBox.Text = picker.ChosenUsername;
                // Repaint the visible Account label + header chip (see the
                // server-username branch above for why the hidden boxes alone
                // aren't enough).
                RefreshAccountRow();
            }
        }

        // Account-expander author / username edits. Two paths:
        //   Signed out: behave like the legacy AuthorNameBox - free-text
        //     SharingAuthor, persisted locally. No uniqueness check; the
        //     value is the anonymous-upload alias.
        //   Signed in: route through set_username RPC. Uniqueness is
        //     server-enforced. On success the local SharingAuthor + both
        //     boxes update. On failure the box reverts and a hint shows.
        private async void AccountAuthor_Changed(object sender, RoutedEventArgs e)
        {
            // Outer try/catch: this is async void, so any uncaught
            // exception (server error, JSON parse, control-unloaded
            // null deref) propagates to the dispatcher and crashes
            // the plugin. Wrap with a generic recovery.
            try
            {
            if (_suppressEvents || _plugin?.Settings == null || AccountAuthorBox == null) return;
            // Snapshot active slot so a sign-in/out during the await below can't land the write in the wrong partition.
            string slotKeyAtEntry = _plugin.Settings.ActiveSlotKey ?? "";
            string newAuthor = (AccountAuthorBox.Text ?? "").Trim();
            string oldAuthor = (_plugin.Settings.SharingAuthor ?? "").Trim();
            if (string.Equals(newAuthor, oldAuthor, System.StringComparison.Ordinal))
            {
                if (AccountAuthorStatus != null) AccountAuthorStatus.Text = "";
                return;
            }

            if (!_plugin.AuthIsSignedIn)
            {
                // Anonymous path: legacy behavior (local persist + the
                // existing CommitAuthorName backfill prompt).
                if (AuthorNameBox != null) AuthorNameBox.Text = newAuthor;
                CommitAuthorName();
                if (AccountAuthorStatus != null) AccountAuthorStatus.Text = "Saved.";
                return;
            }

            // Signed in: validate + claim server-side.
            if (AccountAuthorStatus != null)
            {
                AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xD8));
                AccountAuthorStatus.Text = "Saving...";
            }
            UsernameAvailability result;
            try { result = await _plugin.AuthSetUsernameAsync(newAuthor); }
            catch (Exception ex)
            {
                if (AccountAuthorStatus != null)
                {
                    AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
                    AccountAuthorStatus.Text = "Couldn't save your username. Check your connection and try again.";
                }
                TrueforceDialog.LogError("Username save", ex);
                AccountAuthorBox.Text = oldAuthor;
                return;
            }
            // Abort if the active slot swapped during the await; otherwise SharingAuthor lands in the wrong partition.
            if ((_plugin.Settings.ActiveSlotKey ?? "") != slotKeyAtEntry)
            {
                SimHub.Logging.Current.Info("[TF4ALL] AccountAuthor_Changed aborted: slot changed during server call");
                AccountAuthorBox.Text = oldAuthor;
                if (AccountAuthorStatus != null) AccountAuthorStatus.Text = "";
                return;
            }
            if (result == UsernameAvailability.Available
                || result == UsernameAvailability.SelfOwned)
            {
                _plugin.Settings.SharingAuthor = newAuthor;
                // Server already accepted the username; if local persist
                // fails, surface it so the user knows the disk copy is
                // out of sync (they may need to investigate disk perms).
                bool persisted = true;
                try { _plugin.PersistSettings(); }
                catch (Exception ex)
                {
                    persisted = false;
                    SimHub.Logging.Current.Info("[TF4ALL] Persist username failed: " + ex.Message);
                }
                if (AuthorNameBox != null) AuthorNameBox.Text = newAuthor;
                if (AccountAuthorStatus != null)
                {
                    if (persisted)
                    {
                        AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
                        AccountAuthorStatus.Text = "Saved.";
                    }
                    else
                    {
                        AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
                        AccountAuthorStatus.Text = "Accepted on the server, but the local save failed.";
                    }
                }
                return;
            }
            // Revert + hint.
            AccountAuthorBox.Text = oldAuthor;
            if (AccountAuthorStatus != null)
            {
                AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
                switch (result)
                {
                    case UsernameAvailability.Format:
                        AccountAuthorStatus.Text = "3-32 chars; letters / numbers / underscore."; break;
                    case UsernameAvailability.Reserved:
                        AccountAuthorStatus.Text = "Reserved name. Pick another."; break;
                    case UsernameAvailability.Taken:
                        AccountAuthorStatus.Text = "Taken. Pick another."; break;
                    default:
                        AccountAuthorStatus.Text = "Could not save."; break;
                }
            }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] AccountAuthor_Changed failed: " + ex.Message);
                if (AccountAuthorStatus != null)
                {
                    AccountAuthorStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x96, 0x55));
                    AccountAuthorStatus.Text = "Couldn't save your username. Check your connection and try again.";
                }
                TrueforceDialog.LogError("Username save", ex);
            }
        }

        private void AccountAuthor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                AccountAuthor_Changed(sender, null);
        }

        // Inline status for routine change-email outcomes (validation / errors), so
        // account feedback stays on the inline channel like Patreon / Discord link
        // and unlink. Red for errors, muted for neutral info; hidden when empty.
        private void SetChangeEmailStatus(string msg, bool isError)
        {
            if (AccountChangeEmailStatus == null) return;
            if (string.IsNullOrEmpty(msg))
            {
                AccountChangeEmailStatus.Text = "";
                AccountChangeEmailStatus.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            AccountChangeEmailStatus.Text = msg;
            AccountChangeEmailStatus.Foreground = new System.Windows.Media.SolidColorBrush(isError
                ? System.Windows.Media.Color.FromRgb(0xE0, 0x8A, 0x8A)
                : System.Windows.Media.Color.FromRgb(0xB0, 0xB0, 0xB0));
            AccountChangeEmailStatus.Visibility = System.Windows.Visibility.Visible;
        }

        // Change-email button. Real flow now: prompt for the new
        // address, PUT it to Supabase Auth, server sends a confirmation
        // link to the new inbox. Old address keeps working until they
        // click the link.
        private async void AccountChangeEmail_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn) return;
            string current = _plugin.AuthSignedInEmail ?? "(unknown)";
            var dlg = new TwoLineEditWindow(
                title:      "Change email",
                line1Label: "New email address",
                line1Init:  "",
                line2Label: null,
                line2Init:  null)
            {
                Owner = Window.GetWindow(this),
            };
            bool? ok = dlg.ShowDialog();
            if (ok != true) return;
            SetChangeEmailStatus(null, false);
            string newEmail = (dlg.Line1Result ?? "").Trim();
            if (!newEmail.Contains("@") || newEmail.IndexOf('.', newEmail.IndexOf('@')) <= newEmail.IndexOf('@'))
            {
                SetChangeEmailStatus("Enter a valid email address.", true);
                return;
            }
            if (string.Equals(newEmail.ToLowerInvariant(), current.ToLowerInvariant(),
                              StringComparison.Ordinal))
            {
                SetChangeEmailStatus("That's already your email.", false);
                return;
            }
            AuthCallResult result;
            try { result = await _plugin.AuthUpdateEmailAsync(newEmail); }
            catch (Exception ex)
            {
                SetChangeEmailStatus("Couldn't update your email. Check your connection and try again.", true);
                TrueforceDialog.LogError("Change email", ex);
                return;
            }
            if (result == AuthCallResult.Ok)
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Change email",
                    "We sent a confirmation link to " + newEmail + ". "
                    + "Click it from that inbox to finish the change. "
                    + "Your current address keeps working until then.",
                    DialogKind.Info);
                return;
            }
            string copy;
            switch (result)
            {
                case AuthCallResult.RateLimited:
                    copy = "Slow down a moment, then try again."; break;
                case AuthCallResult.InvalidInput:
                    copy = "That email isn't valid."; break;
                case AuthCallResult.NetworkFailure:
                    copy = "Could not reach the sign-in server."; break;
                default:
                    copy = "Could not update your email. The address may already be taken."; break;
            }
            SetChangeEmailStatus(copy, true);
        }

        // The Backup & sync expander (Settings) holds the cloud-backup controls; refresh their
        // supporter gating when it's opened. (Account-tab refreshes happen on tab selection.)
        private void BackupExpander_Expanded(object sender, RoutedEventArgs e)
        {
            _ = RefreshCloudBackupGatingAsync();
        }

        // Account stats + sessions render inside this collapsible section, so fetch
        // them when it opens (mirrors BackupExpander_Expanded). The tab-open path
        // only fetches when this is already expanded, so the "Loading..." feedback
        // is never stranded behind a collapsed header.
        private void AccountDetails_Expanded(object sender, RoutedEventArgs e)
        {
            RefreshAccountStats();
        }

        // Pull account stats on demand. Cheap RPC; no caching here so
        // edits made elsewhere (a fresh upload, a new vote) show up next
        // time the expander opens.
        // Generation counter so a stale in-flight fetch can't overwrite
        // a newer Expander-open's UI. Bumped on every entry to
        // RefreshAccountStats; the post-await code only renders if it
        // still matches.
        private int _accountStatsGen;

        private async void RefreshAccountStats()
        {
            // Outer try/catch: async void called from expander open;
            // any uncaught throw (JSON parse, control unloaded, etc.)
            // would crash the dispatcher. Falls back to a friendly
            // "Could not load stats" line.
            try
            {
            if (_plugin == null || AccountStatsBox == null) return;
            if (!_plugin.AuthIsSignedIn)
            {
                AccountStatsBox.Visibility = Visibility.Collapsed;
                if (AccountSessionsBox != null) AccountSessionsBox.Visibility = Visibility.Collapsed;
                if (AccountDangerZone != null) AccountDangerZone.Visibility = Visibility.Collapsed;
                return;
            }
            AccountStatsBox.Visibility = Visibility.Visible;
            if (AccountDangerZone != null) AccountDangerZone.Visibility = Visibility.Visible;
            int gen = unchecked(++_accountStatsGen);
            // Initial copy while the RPC runs.
            AccountStatsCreated.Text   = "Loading...";
            AccountStatsUploads.Text   = "";
            AccountStatsDownloads.Text = "";
            AccountStatsVotes.Text     = "";

            Newtonsoft.Json.Linq.JObject root = null;
            try { root = await _plugin.FetchAccountStatsAsync(); }
            catch { /* fall through */ }
            // Drop stale results: the user collapsed + re-opened the
            // expander while we were awaiting, OR signed out / a
            // different user mounted, OR the panel unloaded. In any of
            // those cases the newer RefreshAccountStats call owns the
            // UI now.
            if (gen != _accountStatsGen) return;
            if (root == null)
            {
                AccountStatsCreated.Text = "Could not load stats.";
                return;
            }
            string createdRaw = root["created_at"]?.ToString();
            DateTime created;
            if (DateTime.TryParse(createdRaw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out created))
            {
                int days = (int)Math.Max(0, (DateTime.UtcNow - created.ToUniversalTime()).TotalDays);
                AccountStatsCreated.Text = "Joined " + created.ToLocalTime().ToString("yyyy-MM-dd")
                                           + "  (" + days + " day" + (days == 1 ? "" : "s") + " ago)";
            }
            else
            {
                AccountStatsCreated.Text = "Joined: " + (createdRaw ?? "(unknown)");
            }
            int uploads   = root["upload_count"]?.ToObject<int>() ?? 0;
            long dls      = root["downloads_received"]?.ToObject<long>() ?? 0;
            long upvotes  = root["upvotes_received"]?.ToObject<long>() ?? 0;
            long downvotes= root["downvotes_received"]?.ToObject<long>() ?? 0;
            AccountStatsUploads.Text   = "Uploads: "   + uploads;
            AccountStatsDownloads.Text = "Downloads received: " + dls;
            AccountStatsVotes.Text     = "Votes received: +" + upvotes + " / -" + downvotes;

            // Active sessions across devices. Loaded via the get_my_sessions
            // RPC (async); the card builds one row per session and offers a
            // per-row revoke plus "sign out everywhere else".
            if (AccountSessionsBox != null)
            {
                AccountSessionsBox.Visibility = Visibility.Visible;
                RefreshAccountSessions();
            }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] RefreshAccountStats failed: " + ex.Message);
                if (AccountStatsCreated != null) AccountStatsCreated.Text = "Could not load stats.";
            }
        }

        // Generation guard for the async session list (a stale RPC return must
        // not repaint the card after the user collapsed/re-opened or signed out).
        private int _accountSessionsGen;

        private async void RefreshAccountSessions()
        {
            try
            {
                if (_plugin == null || AccountSessionsBox == null) return;
                if (!_plugin.AuthIsSignedIn) { AccountSessionsBox.Visibility = Visibility.Collapsed; return; }

                // Opportunistic heartbeat: if THIS device was revoked from elsewhere, opening
                // the Account tab signs it out now instead of waiting for the next timer tick.
                _plugin.PokeSessionHeartbeat();

                int gen = unchecked(++_accountSessionsGen);
                if (AccountSessionsList != null) AccountSessionsList.Children.Clear();
                if (AccountSignOutOthersBtn != null) AccountSignOutOthersBtn.Visibility = Visibility.Collapsed;
                SetSessionsStatus("Loading sessions…");

                System.Collections.Generic.List<SessionClient.SessionRow> sessions = null;
                try { sessions = await _plugin.AuthGetSessionsAsync(System.Threading.CancellationToken.None); }
                catch { /* fall through to error copy */ }

                if (gen != _accountSessionsGen) return;   // superseded or signed out mid-await

                // Feed the sync coordinator's presence gate for free: the Account tab already loaded
                // the session list, so reuse it to decide whether a peer is online (drives the pull +
                // heartbeat gates) instead of making a separate get_my_sessions probe.
                try { _plugin.ApplySessionsForPresence(sessions); } catch { }

                if (sessions == null) { SetSessionsStatus("Couldn't load your sessions."); return; }
                if (sessions.Count == 0) { SetSessionsStatus("No active sessions found."); return; }

                SetSessionsStatus(null);   // hide status, show the rows
                int others = 0;
                foreach (var s in sessions)
                {
                    if (!s.IsCurrent) others++;
                    AccountSessionsList.Children.Add(BuildSessionRow(s));
                }
                if (AccountSignOutOthersBtn != null)
                {
                    AccountSignOutOthersBtn.Content = "Sign out everywhere else";
                    AccountSignOutOthersBtn.IsEnabled = others > 0;
                    AccountSignOutOthersBtn.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] RefreshAccountSessions failed: " + ex.Message);
                SetSessionsStatus("Couldn't load your sessions.");
            }
        }

        private void SetSessionsStatus(string text)
        {
            if (AccountSessionsStatus == null) return;
            if (string.IsNullOrEmpty(text))
            {
                AccountSessionsStatus.Text = "";
                AccountSessionsStatus.Visibility = Visibility.Collapsed;
            }
            else
            {
                AccountSessionsStatus.Text = text;
                AccountSessionsStatus.Visibility = Visibility.Visible;
            }
        }

        // One session row: device label + signed-in / last-active lines on the
        // left, and (for non-current sessions) a Revoke button on the right.
        private UIElement BuildSessionRow(SessionClient.SessionRow s)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var deviceLine = new TextBlock
            {
                Text = s.IsCurrent ? "This device" : DescribeSession(s),
                FontSize = 11,
                FontWeight = s.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = s.IsCurrent
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A))   // gold for current
                    : new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)),
                TextWrapping = TextWrapping.Wrap,
            };
            if (!string.IsNullOrEmpty(s.UserAgent)) deviceLine.ToolTip = s.UserAgent;
            info.Children.Add(deviceLine);

            // Hardware line: last-used wheel + plugin version, once the device has
            // heartbeated (null/blank for a brand-new sign-in or an old client).
            string wheel = (s.Wheel ?? "").Trim();
            string ver   = (s.PluginVersion ?? "").Trim();
            if (wheel.Length > 0 || ver.Length > 0)
            {
                string hw = wheel.Length > 0 ? "Wheel: " + wheel : "";
                if (ver.Length > 0) hw += (hw.Length > 0 ? "    " : "") + "TF4ALL v" + ver;
                info.Children.Add(new TextBlock
                {
                    Text = hw,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)),
                    Margin = new Thickness(0, 1, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            // s.LastActive already folds in the heartbeat last-seen server-side (greatest()).
            string lastActive = s.LastActive.HasValue
                ? s.LastActive.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "unknown";
            string signedIn = s.CreatedAt.HasValue
                ? s.CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "unknown";
            info.Children.Add(new TextBlock
            {
                Text = "Last active: " + lastActive + "    Signed in: " + signedIn,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)),
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            if (!s.IsCurrent)
            {
                var revoke = new Button
                {
                    Content = "Revoke",
                    Tag = s.Id,
                    Padding = new Thickness(8, 1, 8, 1),
                    Height = 22,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = "Sign this device out of your account.",
                };
                revoke.Click += AccountRevokeSession_Click;
                Grid.SetColumn(revoke, 1);
                grid.Children.Add(revoke);
            }

            return grid;
        }

        // Human label for a non-current session. Prefer the heartbeat's device name
        // (the computer name); fall back to the descriptive User-Agent set at sign-in,
        // then a generic label, and append the IP so devices stay distinguishable.
        private static string DescribeSession(SessionClient.SessionRow s)
        {
            string label = (s.DeviceName ?? "").Trim();
            if (label.Length == 0)
            {
                string ua = (s.UserAgent ?? "").Trim();
                if (ua.Length > 0 && !ua.StartsWith("Go-http", StringComparison.OrdinalIgnoreCase))
                    label = ua.Length <= 48 ? ua : ua.Substring(0, 48) + "...";
            }
            if (label.Length == 0) label = "Other device";
            if (!string.IsNullOrEmpty(s.Ip)) label += "  (" + s.Ip + ")";
            return label;
        }

        private void AccountSessionsRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAccountSessions();
        }

        private async void AccountRevokeSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                string id = btn?.Tag as string;
                if (string.IsNullOrEmpty(id) || _plugin == null) return;

                var confirm = TrueforceDialog.Show(null,
                    "Revoke session",
                    "Sign this device out of your account? A running device signs out within a few minutes; an offline or sleeping one signs out the next time it comes online.",
                    DialogKind.Destructive, okLabel: "Revoke", cancelLabel: "Cancel");
                if (confirm != true) return;

                if (btn != null) { btn.IsEnabled = false; btn.Content = "Revoking..."; }
                bool ok = await _plugin.AuthRevokeSessionAsync(id, System.Threading.CancellationToken.None);
                if (ok)
                {
                    RefreshAccountSessions();
                }
                else
                {
                    if (btn != null) { btn.IsEnabled = true; btn.Content = "Revoke"; }
                    SetSessionsStatus("Couldn't revoke that session. It may have already ended.");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Revoke session failed: " + ex.Message);
                // Don't leave the button stuck on "Revoking..."; restore it and
                // tell the user it didn't go through. (btn is scoped to the try,
                // so re-derive it from sender here.)
                if (sender is Button b) { b.IsEnabled = true; b.Content = "Revoke"; }
                SetSessionsStatus("Couldn't revoke that session. Check your connection and try again.");
            }
        }

        private async void AccountSignOutOthers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_plugin == null) return;
                var confirm = TrueforceDialog.Show(null,
                    "Sign out everywhere else",
                    "Sign out every other device? Running devices sign out within a few minutes; offline ones when they next come online. This device stays signed in.",
                    DialogKind.Destructive, okLabel: "Sign out", cancelLabel: "Cancel");
                if (confirm != true) return;

                if (AccountSignOutOthersBtn != null) { AccountSignOutOthersBtn.IsEnabled = false; AccountSignOutOthersBtn.Content = "Signing out..."; }
                bool ok = await _plugin.AuthSignOutOtherSessionsAsync();
                if (ok)
                {
                    RefreshAccountSessions();
                }
                else
                {
                    if (AccountSignOutOthersBtn != null) { AccountSignOutOthersBtn.IsEnabled = true; AccountSignOutOthersBtn.Content = "Sign out everywhere else"; }
                    SetSessionsStatus("Couldn't sign out the other devices. Check your connection and try again.");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Sign-out-others failed: " + ex.Message);
                if (AccountSignOutOthersBtn != null) { AccountSignOutOthersBtn.IsEnabled = true; AccountSignOutOthersBtn.Content = "Sign out everywhere else"; }
                SetSessionsStatus("Couldn't sign out the other devices. Check your connection and try again.");
            }
        }

        // Export-my-data button. Dumps the export_my_data jsonb to a
        // .json file the user picks. Useful for GDPR-style review and
        // for taking your contributions with you on account delete.
        private async void AccountExport_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "trueforce-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
                Filter = "JSON file (*.json)|*.json",
                Title = "Export my data",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            string json = null;
            try { json = await _plugin.ExportMyDataRawAsync(); }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't export your data. Make sure the file isn't open and you can write to that folder, then try again.",
                    ex);
                return;
            }
            if (string.IsNullOrEmpty(json))
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Export my data", "Export returned nothing.",
                    DialogKind.Warning);
                return;
            }
            try { System.IO.File.WriteAllText(dlg.FileName, json); }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't export your data. Make sure the file isn't open and you can write to that folder, then try again.",
                    ex);
                return;
            }
            ShowSavedStatus(AccountExportStatus,
                "Exported to " + System.IO.Path.GetFileName(dlg.FileName) + ".",
                dlg.FileName);
        }

        // Delete-account button. Two-step confirm because it's irreversible
        // for the account record (presets stay; vote/submission history
        // anonymizes). User must type DELETE to proceed.
        private async void AccountDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || !_plugin.AuthIsSignedIn) return;
            string email = _plugin.AuthSignedInEmail ?? "(unknown)";
            var confirm1 = TrueforceDialog.Show(Window.GetWindow(this),
                "Delete account",
                "Delete the account for " + email + "?\n\n"
                + "Your presets stay (people who downloaded them keep them), but your name comes off them and your account is gone for good. Your vote and car-fact contribution history is anonymized, and your cloud backup is deleted.",
                DialogKind.Destructive, okLabel: "Delete account", cancelLabel: "Cancel");
            if (confirm1 != true) return;

            // Hard sanity gate: require the user to type DELETE.
            var dlg = new TwoLineEditWindow(
                title:      "Confirm account deletion",
                line1Label: "Type DELETE to confirm",
                line1Init:  "",
                line2Label: null,
                line2Init:  null)
            {
                Owner = Window.GetWindow(this),
            };
            bool? typed = dlg.ShowDialog();
            if (typed != true) return;
            if (!string.Equals((dlg.Line1Result ?? "").Trim(), "DELETE", StringComparison.Ordinal))
            {
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Delete account",
                    "Cancelled. Type DELETE exactly to confirm next time.",
                    DialogKind.Info);
                return;
            }

            // In-flight guard: the backup delete + RPC can take tens of
            // seconds worst-case; keep the button from starting a second
            // concurrent deletion and show that something is happening.
            if (AccountDeleteBtn != null)
            {
                AccountDeleteBtn.IsEnabled = false;
                AccountDeleteBtn.Content   = "Deleting…";
            }
            try
            {
                bool ok = false;
                try { ok = await _plugin.DeleteMyAccountAsync(); }
                catch (Exception ex)
                {
                    TrueforceDialog.ShowError(Window.GetWindow(this),
                        "Couldn't delete your account. Check your connection and try again.",
                        ex);
                    return;
                }
                if (!ok)
                {
                    TrueforceDialog.Show(Window.GetWindow(this),
                        "Delete account",
                        "Delete failed. The server might be unreachable; try again.",
                        DialogKind.Warning);
                    return;
                }
                // Server deleted the auth user; clear local session.
                _plugin.AuthSignOut();
                if (_plugin.Settings != null)
                {
                    _plugin.Settings.SharingAuthor = "";
                    try { _plugin.PersistSettings(); }
                    catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                }
                if (AuthorNameBox    != null) AuthorNameBox.Text    = "";
                if (AccountAuthorBox != null) AccountAuthorBox.Text = "";
                RefreshAccountRow();
                if (AccountDetailsExpander?.IsExpanded == true) RefreshAccountStats();
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Delete account",
                    "Account deleted.",
                    DialogKind.Info);
            }
            finally
            {
                // Restore is required on the failure paths; harmless on
                // success since the account UI is rebuilt above.
                if (AccountDeleteBtn != null)
                {
                    AccountDeleteBtn.IsEnabled = true;
                    AccountDeleteBtn.Content   = "Delete account";
                }
            }
        }

        // Plugin-load update notification. Runs once per panel-open
        // (gated by a per-session flag) so we don't badger the user if
        // they reload the panel. Backend rate limits the underlying
        // RPC so a fast loop is fine even without the gate.
        private bool _communityUpdatesCheckedThisSession;

        private async void MaybeShowCommunityUpdates()
        {
            if (_communityUpdatesCheckedThisSession) return;
            _communityUpdatesCheckedThisSession = true;
            if (_plugin?.Settings?.CommunityEnabled != true) return;
            if (_plugin.Settings.DownloadedCommunityPresets == null
                || _plugin.Settings.DownloadedCommunityPresets.Count == 0)
                return;

            List<(PresetSummary Server, DownloadedPresetRecord Local)> updates = null;
            try { updates = await _plugin.FindCommunityPresetUpdatesAsync(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Update check failed: " + ex.Message);
                _presetManager?.RefreshUpdatesChip(0);
                return;
            }
            if (updates == null || updates.Count == 0)
            {
                _presetManager?.RefreshUpdatesChip(0);
                return;
            }

            // Auto-apply: silently update any row the user hasn't edited
            // locally (OriginalBodyHash still matches). The residual set
            // (edited rows + unsupported kinds) is what the modal shows.
            int autoApplied = updates.Count;
            try { updates = await _plugin.AutoApplyCommunityPresetUpdatesAsync(updates); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Auto-apply update sweep failed: " + ex.Message);
            }
            autoApplied -= updates?.Count ?? 0;
            _presetManager?.RefreshUpdatesChip(updates?.Count ?? 0);
            if (updates == null || updates.Count == 0) return;

            // Owner-null guard: this method is async void scheduled on
            // the Dispatcher and may resume after SimHub navigated away
            // from the Trueforce panel, at which point Window.GetWindow
            // returns null and any modal we'd open would orphan. The
            // captured owner is reused for both the updates dialog and
            // the summary MessageBox to keep them anchored to the same
            // window through the async tail.
            var owner = Window.GetWindow(this);
            if (owner == null) return;

            var dialog = new PresetUpdatesAvailableWindow(updates) { Owner = owner };
            bool? ok = dialog.ShowDialog();
            if (ok != true)
            {
                await RecomputeUpdatesChipAsync();
                return;
            }

            int applied = 0, skipped = 0;
            foreach (var o in dialog.Outcomes)
            {
                if (o.Action == PresetUpdatesAvailableWindow.RowAction.Skip)
                {
                    _plugin.AcknowledgeCommunityPresetVersion(o.Id, o.ServerContentVersion);
                    skipped++;
                    continue;
                }
                if (o.Action != PresetUpdatesAvailableWindow.RowAction.Update) continue;
                try
                {
                    if (await ApplyCommunityUpdate(o)) applied++;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info(
                        "[TF4ALL] Apply update failed for " + o.Id + ": " + ex.Message);
                }
            }
            await RecomputeUpdatesChipAsync();
            if (applied + skipped + autoApplied > 0)
            {
                string summary = $"Updates: {applied} applied, {skipped} skipped";
                if (autoApplied > 0) summary += ", " + autoApplied + " auto-updated";
                summary += ".";
                TrueforceDialog.Show(owner,
                    "Community preset updates",
                    summary,
                    DialogKind.Info);
            }
        }

        // Cheap re-fetch of the pending update count so the persistent
        // chip in PresetManagerControl stays accurate after a modal
        // close, manual check, or auto-apply sweep. Swallows errors so
        // a stale count never breaks the panel; logged for diagnostics.
        private async Task RecomputeUpdatesChipAsync()
        {
            if (_plugin == null || _presetManager == null) return;
            try
            {
                var fresh = await _plugin.FindCommunityPresetUpdatesAsync();
                _presetManager.RefreshUpdatesChip(fresh?.Count ?? 0);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Updates chip refresh failed: " + ex.Message);
            }
        }

        // Chip-driven review flow. Mirrors CommunityCheckNowBtn_Click but
        // skips the status-label fade since the chip is its own
        // affordance. Re-fetches the update list, opens the same modal,
        // processes apply / acknowledge outcomes, and refreshes the chip.
        private async void OnPresetManagerUpdatesChipClicked()
        {
            if (_plugin == null) return;
            List<(PresetSummary Server, DownloadedPresetRecord Local)> updates;
            try { updates = await _plugin.FindCommunityPresetUpdatesAsync(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Updates chip check failed: " + ex.Message);
                _presetManager?.RefreshUpdatesChip(0);
                return;
            }
            int autoApplied = 0;
            if (updates != null && updates.Count > 0)
            {
                int before = updates.Count;
                try { updates = await _plugin.AutoApplyCommunityPresetUpdatesAsync(updates); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("[TF4ALL] Auto-apply update sweep failed: " + ex.Message);
                }
                autoApplied = before - (updates?.Count ?? 0);
            }
            int residual = updates?.Count ?? 0;
            _presetManager?.RefreshUpdatesChip(residual);
            if (residual == 0) return;

            var owner = Window.GetWindow(this);
            if (owner == null) return;
            var dialog = new PresetUpdatesAvailableWindow(updates) { Owner = owner };
            bool? ok = dialog.ShowDialog();
            if (ok != true)
            {
                await RecomputeUpdatesChipAsync();
                return;
            }
            int applied = 0, skipped = 0;
            foreach (var o in dialog.Outcomes)
            {
                if (o.Action == PresetUpdatesAvailableWindow.RowAction.Skip)
                {
                    _plugin.AcknowledgeCommunityPresetVersion(o.Id, o.ServerContentVersion);
                    skipped++;
                    continue;
                }
                if (o.Action != PresetUpdatesAvailableWindow.RowAction.Update) continue;
                try
                {
                    if (await ApplyCommunityUpdate(o)) applied++;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info(
                        "[TF4ALL] Apply update failed for " + o.Id + ": " + ex.Message);
                }
            }
            await RecomputeUpdatesChipAsync();
            if (applied + skipped + autoApplied > 0)
            {
                string summary = $"Updates: {applied} applied, {skipped} skipped";
                if (autoApplied > 0) summary += ", " + autoApplied + " auto-updated";
                summary += ".";
                TrueforceDialog.Show(owner,
                    "Community preset updates",
                    summary,
                    DialogKind.Info);
            }
        }

        // Re-fetch + re-apply a single update. Overwrites the local
        // preset file under the same name; bumps SeenContentVersion on
        // success so the prompt won't return for this revision.
        private async Task<bool> ApplyCommunityUpdate(PresetUpdatesAvailableWindow.RowOutcome outcome)
        {
            if (_plugin == null || string.IsNullOrEmpty(outcome.Id)) return false;
            PresetFull full = null;
            try { full = await Task.Run(() => _plugin.FetchCommunityPresetBody(outcome.Id)); }
            catch { return false; }
            if (full?.Body == null || full.Summary == null) return false;

            // Parse override + customs the same way the manual download does.
            CarOverride ovr = null;
            List<CustomEngineDef> customs = null;
            try
            {
                var ovrToken = full.Body["override"];
                if (ovrToken != null) ovr = ovrToken.ToObject<CarOverride>();
                var ceToken = full.Body["custom_engines"];
                if (ceToken is Newtonsoft.Json.Linq.JArray ja)
                    customs = ja.ToObject<List<CustomEngineDef>>();
            }
            catch { return false; }
            if (ovr == null) return false;

            // Overwrite under the same local name. No section picker on
            // update - the user opted in to this preset originally;
            // bringing in the latest in full is the expected behavior.
            if (customs != null && customs.Count > 0)
                _plugin.ImportCommunityCustomEngines(customs);
            _plugin.SaveImportedCommunityCarPreset(
                full.Summary.CarId ?? _plugin.ActiveCarId,
                outcome.LocalPresetName,
                full.Summary.Game ?? _plugin.ActiveGame,
                ovr,
                full.Summary.Author, full.Summary.Description,
                communitySourceId: full.Summary.Id,
                allowInPacks: full.Summary.AllowInPacks);
            _plugin.AcknowledgeCommunityPresetVersion(outcome.Id, outcome.ServerContentVersion);
            return true;
        }

        // Conditional welcome modal. Shown once (backend configured +
        // HasSeen=false), latched on ANY dismissal: the welcome is a
        // proceed with an optional account, not a consent gate, so there
        // is no decline / re-show cadence anymore. WelcomeNextShowAt is
        // still honored as a gate for legacy mid-cadence settings files.
        // iRacing app.ini Trueforce notice. Re-fires each time iRacing becomes
        // the active game until the user dismisses it for good. Trigger lives in
        // RefreshFromPlugin (transition into "IRacing"); this is the show step.
        private string _lastGameForIracingNotice;
        private bool _iracingNoticeShowing;
        private const string IracingTrueforceNoticeBody =
            "The plugin can take force feedback over from iRacing: iRacing still works out what the\n" +
            "car is doing, it just stops driving the wheel itself, and the plugin delivers those same\n" +
            "forces over Trueforce. That is what frees your rev lights and wheel screen.\n\n" +
            "Two switches hand it over, and they are in different places.\n\n" +
            "1. Fully close iRacing.\n" +
            "2. Open app.ini in your iRacing documents folder (usually Documents\\iRacing).\n" +
            "3. Change loadTrueForceAPI=1 to loadTrueForceAPI=0, then save. This releases the\n" +
            "   Trueforce connection the plugin needs.\n" +
            "4. Relaunch iRacing and turn its own force feedback OFF in the in-sim options.\n" +
            "   Do NOT just set the strength to 0: the plugin reads that number to know what\n" +
            "   full force means for your car, so leave it wherever you like it.\n" +
            "5. In the plugin, enable it for iRacing, then tick Telemetry Based FFB on that tab.\n\n" +
            "The plugin then plays iRacing's own steering force through the wheel, and your rev\n" +
            "lights and wheel screen come back with it. Miss either switch and it stays silent.";

        private void MaybeShowIracingTrueforceNotice()
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.IRacingTrueforceNoticeDismissed) return;
            if (!string.Equals(_plugin.ActiveGame, "IRacing", StringComparison.Ordinal)) return;
            // SimHub caches this control; a dispatched action can fire after
            // Unloaded, when GetWindow returns null and a modal would detach.
            var owner = Window.GetWindow(this);
            if (owner == null) return;
            if (_iracingNoticeShowing) return;
            _iracingNoticeShowing = true;
            try
            {
                bool? r = TrueforceDialog.Show(owner,
                    "Using Trueforce For All in iRacing",
                    IracingTrueforceNoticeBody,
                    DialogKind.Info,
                    okLabel: "Got it, don't show again",
                    cancelLabel: "Remind me later",
                    goldOk: true);
                // true = dismiss for good. false/null (Remind me later / X) leaves
                // the latch off, so it re-appears on the next iRacing launch.
                if (r == true)
                {
                    _plugin.Settings.IRacingTrueforceNoticeDismissed = true;
                    _plugin.PersistSettings();
                }
            }
            finally { _iracingNoticeShowing = false; }
        }

        // Telemetry Based FFB (Mode B) intro. Shown once, the first time a
        // Mode-B-capable game (FM8 / FH5 / FH6) is the active game. Trigger is
        // the user opening the Telemetry Based FFB tab (MainTabs_SelectionChanged),
        // so it appears in context instead of over SimHub's home screen. The
        // HasSeenModeBIntro flag keeps it one-time; this is the show step.
        private bool _modeBIntroShowing;
        private void MaybeShowModeBIntro()
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.HasSeenModeBIntro) return;
            if (!_plugin.ActiveGameSupportsModeB) return;
            var owner = Window.GetWindow(this);
            if (owner == null) return;
            if (_modeBIntroShowing) return;
            _modeBIntroShowing = true;
            try
            {
                string body =
                    "Instead of passing this game's own force feedback through, the plugin "
                    + "can build the steering force itself. There's a real sense of grip. "
                    + "The wheel goes light as the front washes out and it pulls into a "
                    + "countersteer as the rear steps out.\n\n"
                    + "It also unlocks your wheel's rev lights: they share a channel with "
                    + "game force feedback, so they can only run when the plugin owns the "
                    + "whole signal, as it does here.\n\n"
                    + "To try it, set this game's force feedback and vibration to 0 in its "
                    + "wheel settings, so the plugin is the only force on the wheel. "
                    + "Then activate it below.";
                bool? r = TrueforceDialog.Show(owner,
                    "Telemetry Based FFB is available",
                    body,
                    DialogKind.Info,
                    okLabel: "Activate for this game",
                    cancelLabel: "Not now",
                    goldOk: true);
                // One-time: mark seen on any outcome so it never re-nags.
                _plugin.Settings.HasSeenModeBIntro = true;
                if (r == true)
                {
                    _plugin.SetModeBEnabledForActiveGame(true);
                    if (ModeBEnabledCheck != null)
                    {
                        bool prev = _suppressEvents;
                        _suppressEvents = true;
                        try { ModeBEnabledCheck.IsChecked = true; }
                        finally { _suppressEvents = prev; }
                    }
                }
                _plugin.PersistSettings();
            }
            finally { _modeBIntroShowing = false; }
        }

        private void MaybeShowNetworkedWelcome()
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.HasSeenNetworkedWelcome) return;
            // Backend not configured - nothing to pitch yet. Don't latch
            // _welcomeTriggeredThisSession on this branch so a later
            // car-change can re-attempt once the backend is in place.
            if (string.IsNullOrEmpty(_plugin.Settings.CommunityBackendUrl)
                || string.IsNullOrEmpty(_plugin.Settings.CommunityBackendAnonKey))
                return;
            // Re-show delay enforcement. Same not-yet-actionable rule
            // as above: do not consume the per-session latch.
            var nextAt = _plugin.Settings.WelcomeNextShowAt;
            if (nextAt.HasValue && nextAt.Value > DateTime.UtcNow) return;
            // Owner-null guard: this method is invoked via
            // Dispatcher.BeginInvoke from both Loaded and MeterTimer_Tick.
            // SimHub caches the control across navigations, so the
            // dispatched action can fire after Unloaded - by which point
            // Window.GetWindow(this) returns null. ShowDialog with a null
            // Owner detaches the dialog from the parent window, which
            // breaks the modal contract. Bail rather than show an
            // orphaned dialog; the next session's Loaded will retry.
            var owner = Window.GetWindow(this);
            if (owner == null) return;

            // Process-wide guard: never stack a second welcome modal. The
            // init/telemetry path and this control's own Loaded + car-change
            // dispatches can otherwise have a queued ShowDialog open on top of
            // the one already on screen; the second would survive the first's
            // dismissal. Checked at the commit point so the early-return gates
            // above don't consume it.
            if (WelcomeWindow.ShownThisSession) return;
            WelcomeWindow.ShownThisSession = true;

            // We're committed to showing the dialog: consume the session
            // latch so concurrent dispatches from Loaded + MeterTimer
            // don't queue a second one behind this.
            _welcomeTriggeredThisSession = true;

            var welcome = new WelcomeWindow { Owner = owner };
            welcome.ShowDialog();

            // The welcome is a PROCEED, not a consent gate: community
            // features and car-data sharing are the default posture, so ANY
            // dismissal (either button, Esc, the X) latches the welcome and
            // leaves them on. The modal's disclosure line is the sharing
            // notice, so the consent modal must never re-ask. Opt-out lives
            // in Settings.
            _plugin.Settings.HasSeenNetworkedWelcome = true;
            _plugin.Settings.WelcomeNextShowAt = null;
            if (_plugin.Settings.CommunityEnabled != true)
            {
                _plugin.SetCommunityEnabled(true);
                if (CommunityEnabledCheck != null) CommunityEnabledCheck.IsChecked = true;
            }
            if (_plugin.Settings.AutoSubmitCarFacts != true)
            {
                _plugin.Settings.AutoSubmitCarFacts = true;
                SyncAutoSubmitCheckboxFromSettings();
            }
            _plugin.Settings.CarFactsConsentAsked = true;
            _plugin.EnsureCarFactsAnonId();
            try { _plugin.PersistSettings(); }
            catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
            RefreshAccountRow();

            // Optional account: run sign-in AFTER the proceed commit, so a
            // cancelled sign-in changes nothing (the Account tab remains).
            if (welcome.SignInRequested)
            {
                if (_plugin.AuthIsSignedIn)
                {
                    // Already signed in (e.g. session restored from a prior
                    // install): still run username bootstrap so the first
                    // upload doesn't surface "set a username first".
                    BootstrapUsernameAfterSignIn();
                }
                else
                {
                    var signIn = new SignInWindow(_plugin) { Owner = owner };
                    bool? signedIn = signIn.ShowDialog();
                    if (signedIn == true && _plugin.AuthIsSignedIn)
                        BootstrapUsernameAfterSignIn();
                }
                RefreshAccountRow();
            }
        }

        // Forza is the only UDP-telemetry game, so its config is always the
        // body of the UDP telemetry expander (shown directly in XAML).
        private void UpdateUdpSectionVisibility()
        {
        }

        // (The firing-pattern readout was removed 2026-07-19: raw phase
        // positions were developer noise. The auto-detect line under the Car
        // facts engine dropdown is the user-facing engine feedback.)

        // ---------- Road bumps ----------

        private void SlipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Enabled = SlipEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void SlipGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            SlipGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Gain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Waveform = WaveformOf(BumpsWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.Freq = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceEnabled = BumpsSurfaceEnabledCheck.IsChecked == true;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceGain = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceFreq = v;
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceWaveform = WaveformOf(BumpsSurfaceWaveformCombo);
            Apply(EffectKind.Bumps);
        }
        private void BumpsSurfaceRumbleScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            BumpsSurfaceRumbleScaleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Bumps);
            _plugin.ActiveBumps.SurfaceRumbleScale = v;
            Apply(EffectKind.Bumps);
        }
        // ---------- Traction loss ----------

        private void TractionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Enabled = TractionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Traction);
        }
        private void TractionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Gain = v;
            Apply(EffectKind.Traction);
        }
        private void TractionSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionSensitivityText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Sensitivity = v;
            Apply(EffectKind.Traction);
        }
        private void TractionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var wf = WaveformOf(TractionWaveformCombo);
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Waveform = wf;
            // Show/hide noise filter rows live (they only matter for Noise).
            var vis = wf == Waveform.Noise ? Visibility.Visible : Visibility.Collapsed;
            TractionNoiseLpRow.Visibility = vis;
            TractionNoiseHpRow.Visibility = vis;
            Apply(EffectKind.Traction);
        }
        private void TractionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            TractionFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.Freq = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseLpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseLpText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.NoiseLowpassHz = v;
            Apply(EffectKind.Traction);
        }
        private void TractionNoiseHpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            double v = e.NewValue;
            TractionNoiseHpText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Traction);
            _plugin.ActiveTraction.NoiseHighpassHz = v;
            Apply(EffectKind.Traction);
        }

        // ---------- Gear shift ----------

        private void ShiftEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Enabled = ShiftEnabledCheck.IsChecked == true;
            Apply(EffectKind.Shift);
        }
        private void ShiftGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Gain = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            ShiftFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Freq = v;
            Apply(EffectKind.Shift);
        }
        private void ShiftWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Shift);
            _plugin.ActiveShift.Waveform = WaveformOf(ShiftWaveformCombo);
            Apply(EffectKind.Shift);
        }

        // ---------- Pit limiter ----------

        private void PitLimiterEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Enabled = PitLimiterEnabledCheck.IsChecked == true;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Gain = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Waveform = WaveformOf(PitLimiterWaveformCombo);
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.Freq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.PulseFreq = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.DutyCycle = v;
            Apply(EffectKind.PitLimiter);
        }
        private void PitLimiterActiveAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            PitLimiterActiveAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.PitLimiter);
            _plugin.ActivePitLimiter.ActiveAmp = v;
            Apply(EffectKind.PitLimiter);
        }

        private void RevLimiterEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Enabled = RevLimiterEnabledCheck.IsChecked == true;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Gain = v;
            Apply(EffectKind.RevLimiter);
        }
        // (The redline slider, its Manual mode, and the per-variant draft
        // Save/Revert row were retired with the car-facts centralization:
        // the Car facts panel's redline box + Set button is the single
        // editor, committing straight to the variant.)

        // (The inline "confirm this community redline" banner was retired with
        // the set-not-share framing pass. The cascade applies community values
        // directly, corrections are just setting your own redline, and with
        // auto-submit on by default agreement accrues from other drivers'
        // saves; a dedicated confirm affordance was community chrome asking
        // users to interact for the system's sake.)

        // ---- Car Facts summary panel (active card) ----

        // Render the at-a-glance facts. The expander is ALWAYS visible (people
        // open the plugin before starting the game; they should discover that
        // car facts exist and that a wrong-feeling rev limiter or shift lights
        // get fixed here). With no active car the body is just the hint line;
        // the fuller summary renders only while the expander is open. Skips the
        // name / redline boxes while they're focused so it doesn't clobber an
        // edit.
        private void RefreshCarFactsPanel()
        {
            if (CarFactsExpander == null || _plugin == null) return;
            bool hasCar = !string.IsNullOrEmpty(_plugin.ActiveCarId);
            if (CarFactsNoCarHint != null)
                CarFactsNoCarHint.Visibility = hasCar ? Visibility.Collapsed : Visibility.Visible;
            if (CarFactsRowsPanel != null)
                CarFactsRowsPanel.Visibility = hasCar ? Visibility.Visible : Visibility.Collapsed;
            if (!hasCar || !CarFactsExpander.IsExpanded) return;

            var s = _plugin.GetActiveCarFactsSummary();
            if (!s.HasCar) return;

            RefreshCarRevLightRow();

            string carNameText = s.CarName ?? "";
            // The raw car id ("Car_242") is the display fallback for unnamed
            // cars, not a name: show the box empty instead so typing a real
            // name doesn't start with deleting the id.
            if (string.Equals(carNameText, _plugin.ActiveCarId, StringComparison.Ordinal))
                carNameText = "";
            if (CarFactsNameBox != null && !CarFactsNameBox.IsKeyboardFocusWithin
                && carNameText != _lastCarFactsNameText)
            {
                _lastCarFactsNameText = carNameText;
                CarFactsNameBox.Text = carNameText;
            }
            // Pre-fill from the SIM when nothing is saved yet. iRacing publishes
            // the official car name (CarScreenName), so there is no reason to
            // make the user type what the game already told us.
            //
            // Deliberately does NOT touch _lastCarFactsNameText: that tracks the
            // SAVED name, and moving it here would make the pre-filled text look
            // already-saved, hiding the Save button and quietly ensuring the
            // name is never stored or shared. Leaving it alone means the button
            // appears, so accepting the sim's name is one click.
            if (CarFactsNameBox != null && !CarFactsNameBox.IsKeyboardFocusWithin
                && string.IsNullOrEmpty((CarFactsNameBox.Text ?? "").Trim())
                && string.IsNullOrEmpty(carNameText))
            {
                string simName = _plugin.ActiveSimCarName;
                if (!string.IsNullOrEmpty(simName)) CarFactsNameBox.Text = simName;
            }
            // Recompute the Save button here too: setting the box above to an
            // unchanged string raises no TextChanged, so a just-saved name would
            // otherwise leave the button showing.
            UpdateCarFactsNameSaveVisibility();
            if (CarFactsCommunityNameText != null)
            {
                bool show = !string.IsNullOrEmpty(s.CommunityCarName);
                CarFactsCommunityNameText.Text = show ? "Community name: " + s.CommunityCarName : "";
                CarFactsCommunityNameText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            // The engine TYPE is the inline CarFactsEngineCombo (populated by
            // RebuildEngineLayoutDropdown). Make sure it's filled the first
            // time the panel opens. The auto-detect line right below the
            // combo (EngineLayoutAutoText) carries the rev-ceiling readout on
            // the same line, so there is no separate meta line anymore.
            if (CarFactsEngineCombo != null && CarFactsEngineCombo.ItemsSource == null)
                RebuildEngineLayoutDropdown();

            // Prefer the user's EXACT pinned value over the resolver's
            // EffectiveRedlineRpm, which only recomputes on a telemetry frame
            // (so right after Set, while paused, it would show the stale value).
            int? shownRedline = s.UserRedline ?? s.EffectiveRedline;
            string redlineText = shownRedline?.ToString() ?? "";
            if (CarFactsRedlineBox != null && !CarFactsRedlineBox.IsKeyboardFocusWithin
                && redlineText != _lastCarFactsRedlineText)
            {
                _lastCarFactsRedlineText = redlineText;
                CarFactsRedlineBox.Text = redlineText;
            }
            if (CarFactsRedlineSourceText != null)
                CarFactsRedlineSourceText.Text = RedlineSourceFriendly(s.RedlineSource);

            // Surface that we have nothing from the community yet. Purely
            // factual: no thank-you after a submission, no "share yours"
            // invite, no Share button (Set routes through the submit path by
            // itself). Setting a redline is just saving a value; framing it
            // as an act of sharing only adds pressure.
            if (CarFactsNoCommunityNudge != null)
            {
                bool showNudge = _plugin.Settings?.CommunityEnabled == true
                                 && !s.CommunityRedline.HasValue;
                if (showNudge)
                    CarFactsNoCommunityNudge.Text = "No community redline exists for this car yet.";
                CarFactsNoCommunityNudge.Visibility = showNudge ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Provenance phrased so it reads as "where this value came from", not as
        // an instruction (the old bare "Community" read like "set to community").
        private static string RedlineSourceFriendly(string src)
        {
            switch (src)
            {
                // A value the user set themselves needs no label: they set
                // it, the box shows it. Labels are for external sources.
                case "user":                  return "";
                case "community":             return "(from the community)";
                case "community_unconfirmed": return "(from the community, unconfirmed)";
                case "game":                  return "(from the game)";
                case "estimated":             return "(estimated guess)";
                // Auto % of max (including the 0.85 default): no suffix.
                // "(from your % setting)" explained plumbing nobody asked
                // about; the number speaks for itself.
                case "derived":               return "";
                default:                      return "";
            }
        }

        // The Car facts "Save" button next to the name shows only when the box
        // holds a valid name that differs from the saved one (_lastCarFactsNameText,
        // set by RefreshCarFactsPanel). Driven from the TextChanged event AND from
        // the refresh path (a programmatic set to an unchanged string raises no
        // TextChanged, so the refresh must recompute too, e.g. right after a save).
        private void CarFactsNameBox_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateCarFactsNameSaveVisibility();

        private void UpdateCarFactsNameSaveVisibility()
        {
            if (CarFactsNameSaveBtn == null || CarFactsNameBox == null) return;
            string cur   = (CarFactsNameBox.Text ?? "").Trim();
            string saved = (_lastCarFactsNameText ?? "").Trim();
            bool changed = cur.Length >= 2 && !string.Equals(cur, saved, StringComparison.Ordinal);
            var want = changed ? Visibility.Visible : Visibility.Collapsed;
            if (CarFactsNameSaveBtn.Visibility != want) CarFactsNameSaveBtn.Visibility = want;
        }

        private void CarFactsNameSave_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || CarFactsNameBox == null) return;
            string game = _plugin.ActiveGame, carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            string name = (CarFactsNameBox.Text ?? "").Trim();
            if (name.Length < 2)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Car name",
                    "Enter a name of at least 2 characters.", DialogKind.Info);
                return;
            }
            // Unified official-name flow: local write + a self-gating silent
            // community submit (sharing on = submit, opted out = local-only),
            // exactly like the redline Set above. No confirm/correct modal.
            CarNameShareFlow.SetNameAndMaybeShare(_plugin, game, carId, name,
                Window.GetWindow(this));
            RefreshFromPlugin();
        }

        // Enter in the name box saves it, same as clicking Save. Gated on the
        // Save button being live (a valid, changed name), so a stray Enter on an
        // empty or unchanged field is a silent no-op rather than popping the
        // "min 2 characters" nag. Consuming the key stops the bell either way.
        private void CarFactsName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            if (CarFactsNameSaveBtn != null
                && CarFactsNameSaveBtn.Visibility == Visibility.Visible)
                CarFactsNameSave_Click(sender, null);
        }

        // Enter in the redline box applies it, same as clicking Set.
        private void CarFactsRedline_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; CarFactsRedlineSet_Click(sender, null); }
        }

        private void CarFactsRedlineSet_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || CarFactsRedlineBox == null) return;
            if (!int.TryParse((CarFactsRedlineBox.Text ?? "").Trim(), out int rpm)
                || rpm < 500 || rpm > 25000)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Redline",
                    "Enter the redline start (where the tachometer turns red), from 500 to 25000 RPM.", DialogKind.Info);
                return;
            }
            int? saved = _plugin.SaveActiveVariantUserRedline(rpm);
            if (saved == null)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Redline",
                    "Couldn't identify this car's engine variant yet. Drive for a moment so the "
                    + "plugin sees the rev range, then set it again.", DialogKind.Info);
                return;
            }
            // Set is the whole story: it routes through the self-gating submit
            // path (sharing on = silent submit, consent unasked = the one-time
            // ask, sharing off = local-only save). There is no separate Share
            // button anymore; saving a value IS how it reaches the community.
            MaybePromptToSubmitRedlineData(_plugin.ActiveCarId, saved.Value);
            RefreshFromPlugin();
        }

        // (The manual "Share with community" button and its shared-this-session
        // latch were removed with the set-not-share framing pass: Set routes
        // through the same self-gating submit path as any save, so a separate
        // share affordance only reframed saving a value as a contribution act.)

        // Re-pull the real community redline consensus from the server and push it
        // into the plugin (replacing the old optimistic local-injection on share).
        // The truth is what the server returns; with the support floor, the user's
        // own lone submission won't masquerade as "the community".
        private void RefreshActiveCommunityRedlineFromServer()
        {
            if (_plugin == null) return;
            string game = _plugin.ActiveGame, carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            string capturedGame = game, capturedCar = carId;
            string capturedSig = _plugin.ComputeActiveCarVariantSignatureForActive() ?? "";
            System.Threading.Tasks.Task.Run(() =>
            {
                RedlineConsensus result = null;
                try { result = _plugin.FetchRedlineConsensus(capturedGame, capturedCar); }
                catch { /* swallowed; leave the readout as-is */ }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_plugin == null) return;
                    if (_plugin.ActiveGame != capturedGame || _plugin.ActiveCarId != capturedCar) return;
                    _engineRedlineCache = result;
                    _plugin.NotifyRedlineConsensus(capturedGame, capturedCar, capturedSig, result);
                    RefreshCarFactsPanel();
                }));
            });
        }

        // ---- Per-gear redline editor (Car Facts panel; the Rev Limiter
        // section's duplicate copy was retired with the centralization) ----

        // Last (variant id | override count) the meter tick rebuilt the per-gear
        // editor for; owned by the 4 Hz block in MeterTimer_Tick.
        private string _perGearShownKey;

        private void RebuildPerGearEditors()
        {
            if (_plugin == null) return;
            bool carDetected = !string.IsNullOrEmpty(_plugin.ActiveCarId);
            var list = carDetected ? _plugin.GetActiveVariantPerGearRedlines() : null;
            // Hidden by default: the editor shows only when the user opts in via
            // the Settings toggle, or when this variant already carries per-gear
            // values, so stored data is never in effect without visible UI.
            bool show = carDetected && (_plugin.Settings?.ShowPerGearRedlineEditor == true
                                        || (list != null && list.Count > 0));
            BuildPerGearRows(CarFactsPerGearRows, CarFactsPerGearExpander, list, show);
        }

        private void BuildPerGearRows(StackPanel container, Expander expander,
            IReadOnlyList<GearRedline> list, bool show)
        {
            if (expander != null) expander.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (container == null) return;
            container.Children.Clear();
            if (!show || list == null) return;
            foreach (var gr in list)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock { Text = "Gear " + gr.Gear, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lbl, 0);
                var box = new TextBox { Text = gr.Rpm.ToString(), VerticalAlignment = VerticalAlignment.Center, Tag = gr.Gear };
                box.LostFocus += PerGearRpm_Commit;
                box.KeyDown += PerGearRpm_KeyDown;
                Grid.SetColumn(box, 1);
                var rpmLbl = new TextBlock { Text = "RPM", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                Grid.SetColumn(rpmLbl, 2);
                var rm = new Button { Content = "Remove", Tag = gr.Gear, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center };
                rm.Click += PerGearRemove_Click;
                Grid.SetColumn(rm, 3);
                row.Children.Add(lbl); row.Children.Add(box); row.Children.Add(rpmLbl); row.Children.Add(rm);
                container.Children.Add(row);
            }
        }

        private void PerGearRpm_Commit(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (!(sender is TextBox tb) || !(tb.Tag is int gear)) return;
            if (int.TryParse((tb.Text ?? "").Trim(), out int rpm) && rpm >= 500 && rpm <= 25000)
            {
                _plugin.SetActiveVariantPerGearRedline(gear, rpm);
                // Mirror the committed value into the sibling panel's matching
                // box WITHOUT a full rebuild (a rebuild clears all rows and would
                // destroy the Tab target mid-transition).
                MirrorPerGearBox(gear, rpm.ToString());
            }
            else
            {
                // Invalid / out of range: revert to the stored value. No modal
                // here - this fires on LostFocus, including during a Tab.
                int stored = 0;
                var list = _plugin.GetActiveVariantPerGearRedlines();
                if (list != null)
                    foreach (var gr in list) if (gr.Gear == gear) { stored = gr.Rpm; break; }
                MirrorPerGearBox(gear, stored > 0 ? stored.ToString() : "");
            }
        }

        // Set the rpm box for a given gear without rebuilding rows, so
        // focus / Tab order survives an edit.
        private void MirrorPerGearBox(int gear, string text)
        {
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                SetPerGearBoxText(CarFactsPerGearRows, gear, text);
            }
            finally { _suppressEvents = prev; }
        }

        private static void SetPerGearBoxText(StackPanel container, int gear, string text)
        {
            if (container == null) return;
            foreach (var child in container.Children)
                if (child is Grid g)
                    foreach (var c in g.Children)
                        if (c is TextBox tb && tb.Tag is int tg && tg == gear) { tb.Text = text; return; }
        }

        private void PerGearRpm_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) PerGearRpm_Commit(sender, null);
        }

        private void PerGearRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (sender is Button b && b.Tag is int gear)
            {
                _plugin.RemoveActiveVariantPerGearRedline(gear);
                RefreshFromPlugin();
            }
        }

        private void CarFactsAddGear_Click(object sender, RoutedEventArgs e) => AddGearCommon();
        private void AddGearCommon()
        {
            if (_plugin == null) return;
            int g = _plugin.AddActiveVariantGear();
            if (g == 0)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Add gear",
                    "Couldn't add a gear yet. Drive for a moment so the plugin identifies this car's "
                    + "engine variant (or you're already at the 16-gear limit).", DialogKind.Info);
                return;
            }
            RefreshFromPlugin();
        }

        // Manual "refresh community facts now" for the active car. Bypasses the
        // 7-day TTL. Guides the user when the relevant toggles are off rather than
        // silently doing nothing.
        private void CarFactsRefreshCommunity_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string game = _plugin.ActiveGame, carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;
            if (_plugin.Settings?.UseCommunityCarFacts != true)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Refresh community facts",
                    "Turn on 'Use community car facts' to use community data for your cars.", DialogKind.Info);
                return;
            }
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                TrueforceDialog.Show(Window.GetWindow(this), "Refresh community facts",
                    "Turn on 'Enable community features (online)' to refresh from the server. Your cached values keep working offline.",
                    DialogKind.Info);
                return;
            }
            MaybeRefreshEngineCommunityContext(game, carId, force: true);
            RefreshFromPlugin();
        }

        private void CarFactsManageVariants_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string carId = _plugin.ActiveCarId;
            string game  = _plugin.ActiveGame;
            if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(game)) return;
            var win = new CarFactsVariantsWindow(_plugin, game, carId, _plugin.ActiveCarDisplayName)
            {
                Owner = Window.GetWindow(this),
            };
            win.ShowDialog();
            // The window mutates via the plugin's helpers (which re-resolve
            // internally); rebuild so a delete / rename lands immediately.
            RebuildEngineLayoutDropdown();
            RefreshFromPlugin();
        }

        // Refresh the "redline is a guess" badge from RevLimiter.IsRedlineGuessed.
        // Called from MeterTimer_Tick so the badge appears/disappears as
        // the cascade re-resolves each frame. Visible only when the effect
        // is enabled and the cascade fell to the default 0.85 × MaxRpm branch.
        private void RefreshRedlineGuessBadge()
        {
            if (RedlineGuessBadge == null) return;
            var settings = _plugin?.ActiveRevLimiter;
            var effect   = _plugin?.RevLimiter;
            bool show = settings != null && effect != null
                && settings.Enabled
                && effect.IsRedlineGuessed;
            var want = show ? Visibility.Visible : Visibility.Collapsed;
            if (RedlineGuessBadge.Visibility != want)
                RedlineGuessBadge.Visibility = want;
        }

        private void RedlineGuessSetValue_Click(object sender, RoutedEventArgs e)
        {
            // Jump to the Car facts redline box (the single redline editor):
            // expand the panel and hand it the cursor, for users who weren't
            // sure where to find it.
            try
            {
                if (CarFactsExpander != null) CarFactsExpander.IsExpanded = true;
                CarFactsRedlineBox?.BringIntoView();
                CarFactsRedlineBox?.Focus();
                if (CarFactsRedlineBox != null)
                    CarFactsRedlineBox.CaretIndex = CarFactsRedlineBox.Text?.Length ?? 0;
            }
            catch { }
        }

        private void RevLimiterWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Waveform = WaveformOf(RevLimiterWaveformCombo);
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterOffsetText.Text = FormatRedlineOffset(v);
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.RedlineOffsetRpm = v;
            Apply(EffectKind.RevLimiter);
        }

        // "0", "-500", "+250" — signed RPM, no decimals.
        private static string FormatRedlineOffset(double rpm)
        {
            int r = (int)Math.Round(rpm);
            return r > 0 ? "+" + r.ToString() : r.ToString();
        }
        private void RevLimiterFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.Freq = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.PulseFreq = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.DutyCycle = v;
            Apply(EffectKind.RevLimiter);
        }
        private void RevLimiterActiveAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            RevLimiterActiveAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.RevLimiter);
            _plugin.ActiveRevLimiter.ActiveAmp = v;
            Apply(EffectKind.RevLimiter);
        }

        // ---------- Airborne ducking (per-car capable, like the other effects:
        // EnsureSectionDraft routes the edit to the active car's override when a
        // car is loaded, else the global; changes surface a Save button + feed
        // the ★ Save all pill) ----------

        private void AirborneEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Airborne);
            _plugin.ActiveAirborne.Enabled = AirborneEnabledCheck.IsChecked == true;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }
        private void AirborneReductionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            float v = (float)e.NewValue;
            AirborneReductionText.Text = ((int)Math.Round(v * 100)).ToString() + "%";
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Airborne);
            _plugin.ActiveAirborne.Reduction = v;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }
        // One handler for every per-effect toggle: read all the checkboxes back
        // into the effective Airborne settings, re-apply, mark dirty.
        private void AirborneToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Airborne);
            var a = _plugin.ActiveAirborne;
            a.DuckEngine       = AirborneDuckEngineCheck.IsChecked    == true;
            a.DuckAudio        = AirborneDuckAudioCheck.IsChecked     == true;
            a.DuckRoadBumps    = AirborneDuckRoadBumpsCheck.IsChecked == true;
            a.DuckTractionLoss = AirborneDuckTractionCheck.IsChecked  == true;
            a.DuckRevLimiter   = AirborneDuckRevLimiterCheck.IsChecked== true;
            a.DuckGearShift    = AirborneDuckGearShiftCheck.IsChecked == true;
            a.DuckAbs          = AirborneDuckAbsCheck.IsChecked       == true;
            a.DuckPitLimiter   = AirborneDuckPitLimiterCheck.IsChecked== true;
            a.DuckDrs          = AirborneDuckDrsCheck.IsChecked       == true;
            a.DuckCollision    = AirborneDuckCollisionCheck.IsChecked == true;
            _plugin.ApplyAirborneSettings();
            MarkEffectDirty(EffectKind.Airborne);
        }

        // ---------- DRS ----------

        private void DrsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Enabled = DrsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Drs);
        }
        private void DrsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Gain = v;
            Apply(EffectKind.Drs);
        }
        private void DrsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.Waveform = WaveformOf(DrsWaveformCombo);
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedWaveform = WaveformOf(DrsSustainedWaveformCombo);
            Apply(EffectKind.Drs);
        }
        private void CollisionWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Waveform = WaveformOf(CollisionWaveformCombo);
            Apply(EffectKind.Collision);
        }
        private void DrsActivationFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            DrsActivationMsText.Text = v.ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationMs = v;
            Apply(EffectKind.Drs);
        }
        private void DrsActivationAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsActivationAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.ActivationAmp = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedFreq = v;
            Apply(EffectKind.Drs);
        }
        private void DrsSustainedAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            DrsSustainedAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Drs);
            _plugin.ActiveDrs.SustainedAmp = v;
            Apply(EffectKind.Drs);
        }

        // ---------- Collision ----------

        private void CollisionEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Enabled = CollisionEnabledCheck.IsChecked == true;
            Apply(EffectKind.Collision);
        }
        private void CollisionGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Gain = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMinThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMinThresholdText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.MinThreshold = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionMaxAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionMaxAmpText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.MaxAmp = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            CollisionFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.Freq = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionEnvelopeMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = (int)e.NewValue;
            CollisionEnvelopeMsText.Text = v.ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Collision);
            _plugin.ActiveCollision.EnvelopeMs = v;
            Apply(EffectKind.Collision);
        }
        private void CollisionTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.Collision);
        private void RevLimiterTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.RevLimiter);

        // ---------- Axle slip ----------

        private void AxleSlipEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.Enabled = AxleSlipEnabledCheck.IsChecked == true;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.Gain = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipPredictive_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.PredictiveSlip = AxleSlipPredictiveCheck.IsChecked == true;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipRevLocked_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.RevLockedRearPulse = AxleSlipRevLockedCheck.IsChecked == true;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipFrontStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipFrontStrengthText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.FrontStrength = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipRearStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipRearStrengthText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.RearStrength = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipFrontPitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipFrontPitchText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.FrontPitchHz = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipRearPitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipRearPitchText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.RearPitchHz = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipJudderSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipJudderText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.JudderDepth = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipOnsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AxleSlipOnsetText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.AxleSlip);
            _plugin.ActiveAxleSlip.OnsetUtil = v;
            Apply(EffectKind.AxleSlip);
        }
        private void AxleSlipTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.AxleSlip);

        // ---------- Kerb thump ----------

        private void KerbThumpEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.KerbThump);
            _plugin.ActiveKerbThump.Enabled = KerbThumpEnabledCheck.IsChecked == true;
            Apply(EffectKind.KerbThump);
        }
        private void KerbThumpGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            KerbThumpGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.KerbThump);
            _plugin.ActiveKerbThump.Gain = v;
            Apply(EffectKind.KerbThump);
        }

        // FS "Leading edge": the Kerb thump block folded into the Terrain
        // texture section. One slider = gain with 0-as-off (the folded
        // shape has no separate enable checkbox). Writes the ACTIVE
        // SETTINGS directly, not a per-car draft: the standalone section
        // owning the draft's Save/Revert chips is hidden in FS, so a draft
        // would be invisible and silently discarded on car change. Direct
        // writes persist like every other FS slider and land in the game
        // preset on save. (FS car ids are FS-only, so no legacy per-car
        // KerbThump override can mask this write.)
        private void BumpsLeadingEdge_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin?.Settings?.KerbThump == null
                || BumpsLeadingEdgeSlider == null) return;
            float v = (float)BumpsLeadingEdgeSlider.Value;
            if (BumpsLeadingEdgeText != null)
                BumpsLeadingEdgeText.Text = v.ToString("F2");
            _plugin.Settings.KerbThump.Gain = v;
            _plugin.Settings.KerbThump.Enabled = v > 0.005f;
            Apply(EffectKind.KerbThump);
            _plugin.PersistSettings();
        }
        private void KerbThumpFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            KerbThumpFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.KerbThump);
            _plugin.ActiveKerbThump.Freq = v;
            Apply(EffectKind.KerbThump);
        }
        private void KerbThumpTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.KerbThump);

        // ---------- Lockup judder ----------

        private void LockupJudderEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.LockupJudder);
            _plugin.ActiveLockupJudder.Enabled = LockupJudderEnabledCheck.IsChecked == true;
            Apply(EffectKind.LockupJudder);
        }
        private void LockupJudderGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            LockupJudderGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.LockupJudder);
            _plugin.ActiveLockupJudder.Gain = v;
            Apply(EffectKind.LockupJudder);
        }
        private void LockupJudderTest_Click(object sender, RoutedEventArgs e) => _plugin?.TestEffect(_plugin.LockupJudder);

        // ---------- ABS ----------

        private void AbsEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Enabled = AbsEnabledCheck.IsChecked == true;
            Apply(EffectKind.Abs);
        }
        private void AbsGainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsGainText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Gain = v;
            Apply(EffectKind.Abs);
        }
        private void AbsFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsFreqText.Text = ((int)v).ToString();
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Freq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsPulseFreqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsPulseFreqText.Text = v.ToString("F1");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.PulseFreq = v;
            Apply(EffectKind.Abs);
        }
        private void AbsDutyCycleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            float v = (float)e.NewValue;
            AbsDutyCycleText.Text = v.ToString("F2");
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.DutyCycle = v;
            Apply(EffectKind.Abs);
        }
        private void AbsMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int idx = AbsModeCombo.SelectedIndex; if (idx < 0) idx = 0;
            var mode = (AbsMode)idx;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Mode = mode;
            // Pulse rate / duty are unused in PerTick mode (grey them live).
            AbsPulseControls.IsEnabled = mode == AbsMode.Pulse;
            Apply(EffectKind.Abs);
        }
        private void AbsWaveform_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.EnsureSectionDraft(TrueforcePlugin.SectionKind.Abs);
            _plugin.ActiveAbs.Waveform = WaveformOf(AbsWaveformCombo);
            Apply(EffectKind.Abs);
        }

        // ---------- Forza UDP listener ----------
        // No enable/disable toggle: the Forza UDP reader is the only source of
        // Forza's per-tire data, so it is always on for Forza (see
        // SwapTelemetrySource). Only the port / bind / forwarding are tunable.

        // ---------- Tester access code (unlocks the rim-LED section) ----------

        private void AccessCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitAccessCode();
        }

        private void AccessCode_Changed(object sender, RoutedEventArgs e) => CommitAccessCode();

        // Author name field handlers. Commit on Enter or LostFocus. Both
        // delegate to CommitAuthorName which trims, persists to
        // Settings.SharingAuthor, and on the first blank-to-set transition
        // prompts the user about backfilling existing presets.
        private void AuthorName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitAuthorName();
        }

        private void AuthorName_Changed(object sender, RoutedEventArgs e) => CommitAuthorName();

        private void CommitAuthorName()
        {
            if (_suppressEvents || _plugin?.Settings == null || AuthorNameBox == null) return;
            string newAuthor = (AuthorNameBox.Text ?? "").Trim();
            string oldAuthor = (_plugin.Settings.SharingAuthor ?? "").Trim();
            if (string.Equals(newAuthor, oldAuthor, System.StringComparison.Ordinal))
            {
                if (AuthorNameStatus != null) AuthorNameStatus.Text = "";
                return;
            }
            _plugin.Settings.SharingAuthor = newAuthor;
            try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }

            // Blank-to-set transition: offer to backfill existing local
            // presets the user authored before setting the name. Stays
            // opt-in. Only acts on files whose Author AND PackName are
            // both blank (so community packs are never relabeled).
            if (string.IsNullOrEmpty(oldAuthor) && !string.IsNullOrEmpty(newAuthor))
            {
                var msg = $"Stamp \"{newAuthor}\" on the presets you've already saved? "
                        + "This updates presets that don't have an author yet (your own work) and leaves community-pack presets alone.";
                var resp = TrueforceDialog.Show(Window.GetWindow(this), "Trueforce For All", msg,
                    DialogKind.Confirm);
                if (resp == true)
                {
                    int n = _plugin.BackfillAuthorOnLocalPresets(newAuthor);
                    if (AuthorNameStatus != null)
                        AuthorNameStatus.Text = n == 0
                            ? "Saved. Nothing needed backfilling."
                            : $"Saved. Stamped {n} existing preset(s).";
                    return;
                }
            }
            if (AuthorNameStatus != null) AuthorNameStatus.Text = "Saved.";
        }

        // Single source of truth for the HELP / CODES listing. When you add a
        // new access code in CommitAccessCode below, add a line here too so
        // HELP stays accurate as the set of codes grows.
        private const string TestCodeCatalog =
            "Trueforce For All test codes (type one in the access box):\n\n" +
            "HELP / CODES   Show this list.\n" +
            "SHARE          Force the 'spread the word' banner on now.\n" +
            "SUPPORT        Preview the periodic Patreon support modal now (pacing untouched).\n" +
            "SUPPORTRESET   Reset the support-prompt ladder back to its first rung.\n" +
            "RATCHET        Play the auto-tuned ring-buffer banner sequence.\n" +
            "STALL          Simulate a Forza 'no packets' stall + open the troubleshooter + show the UDP setup banner (toggle).\n" +
            "CAPTURE        Toggle the aligned telemetry+FFB capture CSV (v2 golden fixture format) under Documents\\TrueforceForAll.\n" +
            "UDP            Toggle the persistent UDP setup banner to test the 'Set up...' jump: off -> Forza -> off.\n" +
            "FZBANNERS      Toggle the two info-tier Forza banners (SimHub-fallback notice + discovered-port) on to eyeball their button styling.\n" +
            "SPRING         Desk test of the stationary spring (motor pushes one way, then the other).\n" +
            "REV            Redline buzz from a synthetic redline (tests the RPM trigger + hold).\n" +
            "WHATSNEW       Re-show the 'What's new' banner and all NEW effect badges.\n" +
            "WELCOME        Reset the networked-welcome modal AND the Mode B intro seen state and re-trigger them now (HasSeenNetworkedWelcome / WelcomeDeclineCount / WelcomeNextShowAt / HasSeenModeBIntro all cleared).\n" +
            "MOTDFLUSH      Clear the Message-of-the-day cache + all MOTD dismissals and refetch now (so dismissed/edited messages reappear; bypasses the ~6h cache).\n" +
            "MOTDROLL       Preview the MOTD strip on a RANDOM upcoming day (shows which day in the strip + status). Run again to re-roll. Dismissals + nag cooldown bypassed.\n" +
            "MOTDDATE<MMDDYYYY>  Preview the MOTD strip as if it were that date, e.g. MOTDDATE12252026, to see upcoming messages before they trigger.\n" +
            "MOTDLIVE       Leave MOTD preview and return to today's real messages.\n" +
            "CACHEFACTS     Clear the community car-facts cache (names/engine types/redlines); it refetches per car.\n" +
            "CACHEBROWSE    Clear the community preset browse cache (the browse/search result lists).\n" +
            "CLEARCACHES    Clear ALL re-fetchable network caches at once: MOTD (+dismissals), community car-facts, browse lists. Leaves your car corrections, local detections, and sign-in alone.\n" +
            "STALECACHES    Mark all re-fetchable network caches stale (MOTD, community facts, browse lists) so they refetch fresh, WITHOUT wiping the offline copies. Soft version of CLEARCACHES.\n" +
            "PREVIEW        Render the release-notes markdown on your clipboard exactly as the in-app 'What's new' will (copy the GitHub notes first, then type PREVIEW).\n" +
            "UPDATE         Simulate an available update (banner + update dialog).\n" +
            "CLOSESIM       Pick an installer and run it with /CloseSimHub=1 to test the silent SimHub auto-close. Closes SimHub.\n" +
            "UPDATEDIRTY    Simulate an update with unsaved changes, then run a locally-picked installer instead of downloading (tests the pre-update 'unsaved changes' warning and the silent SimHub close). Closes SimHub.\n" +
            "UPDATEPOLL     Simulate a release shipping AFTER launch: arms a fake newer release that only a BACKGROUND re-check applies, on a fast cadence (every 5s; UPDATEPOLL<n> for n seconds), so the 'Update to vX.Y.Z' banner appears on its own within seconds, no restart. Tests the periodic re-check end-to-end. Run again to stop + clear. Toggle.\n" +
            "FAULT          Force a stream fault to test auto-reconnect.\n" +
            "NOFFB          Simulate the FFB tap capturing no game force feedback while driving (tests the whole-bus retry + 'try another USB port' notice). Toggle.\n" +
            "FFBX           Opt in to the experimental FFB-capture path (HID++ report 0x12 + faster index resolve; issue #8 RS50/FH6). Persists. Toggle.\n" +
            "ACFFB          Tap-free Assetto Corsa FFB: stream the game's own force value read from AC's shared memory (finalFF) instead of the USBPcap capture. Keep in-game FFB ON (gain 0 silences it). AC only; other games are untouched. Persists. Toggle.\n" +
            "DRIVER         Driver testing mode: route FFB through the kernel filter driver (sole wheel ownership). Needs the TFFA filter driver installed. Persists. Toggle.\n" +
            "NOLOCK         Toggle 'Hand the wheel back to the game while paused' (on by default; the checkbox lives under Force feedback (advanced)). Off = the plugin keeps hold of the wheel and releases its force to zero while paused. Persists. Toggle.\n" +
            "FFBOK          Force the 'is your FFB working?' success banner on now, to test the Yes (report) and No (troubleshooter) paths.\n" +
            "HOMEBOX        Toggle the TF4ALL master + audio gain tile in SimHub's home 'Feedback' section (next to Motors/Wind). On by default now; the real switch is Settings > Extras. This is just a quick dev toggle.\n" +
            "FRESH          Filter the Presets tab to built-in (factory) presets only, to preview the fresh-install library. Hides your own presets without deleting them. Toggle.\n" +
            "DEV            Unlock the Developer tools bar (Presets tab) + per-row 'Set as built-in' promote buttons: maintain the file-based built-in folder (validate / open / promote selected or checked). Persists. Toggle.\n" +
            "FOLDDEFAULTS   DEV one-shot: for every car whose default points at a user preset, promote that user preset to a factory built-in (replaces existing built-ins for the car), repoint the factory car-default, and delete the user preset. Other user presets for the same car stay put. Idempotent.\n" +
            "NORMALIZEFORZA DEV one-shot: rename legacy Forza_<n> car ids to Car_<n> (matches SimHub's data feed). If both exist, Car_<n> wins and Forza_<n> is dropped. Touches factory + user folders, car-defaults files, and Settings.CarDefaults/CarOverrides. Idempotent.\n" +
            "MANUALPIN      Reveal the Diagnostics 'Pick device manually...' control (hidden by default; auto-discovery + self-heal handle almost every case). Persists. Toggle.\n" +
            "F8SWEEP / F8   Experimental: sweep the rev lights via the legacy F8 12 command on the wheel's gamepad collection (off the HID++ FFB pipe). Writes at forza-wheel-leds' ~60 Hz rate by default (worst-case FFB test): drive a sim and check the LEDs sweep AND the FFB stays solid. Toggle. F8SLOW = paced write-on-change (our footprint, for comparison); 'F8SWEEP <ms>' = custom resend interval.\n" +
            "TRACE          Toggle the high-rate FFB signal-chain trace (game force vs plugin output vs steering, full provider rate); second TRACE dumps the CSV under Documents\\TrueforceForAll.\n" +
            "SWEEP          Motor characterization: 15 s log-sine force sweep 8-300 Hz through the wheel (hands lightly on the rim). SWEEP1..SWEEP6 = one octave band each (~5 s): 8-16, 16-32, 32-63, 63-125, 125-250, 250-400 Hz.\n" +
            "MODEB <0|1>    Arm/disarm telemetry based FFB (Mode B) directly, bypassing the capable-game gate (dev override). Persists and syncs the Telemetry Based FFB tab checkbox.\n" +
            "B* <value>     Live Mode B tuning, e.g. 'BSAT 1.2': BSAT strength, BRISE weight buildup, BPEAK grip limit, BFLOOR slide lightness, BEMA smoothing ms, BDAMP damping, BCENTER centering, BLAT cornering weight, BDIRK center feel, BRECOVER lockup-recovery ms, BLOCKPT lockup slip point, BCIRCLE 1/0 friction-circle braking, BLEARN 1/0 auto braking grip per car, BGTRIM braking-grip trim, BAUTOS 1/0 auto strength per car, BLDEM 1/0 lateral-demand force, BMINF min force floor, MBCPD 1/0 direct centering + BCLEAD look-ahead ms, MBREV 1/0 reversal damping + BREVG strength, MBLEAD 1/0 anticipation + BLEAD lead ms, BSIGN 1/-1 force direction (all persist); BFULL full-slip point + BSPD full-force speed km/h are live-only.\n" +
            "OLEDTEST       Show sample wheel-screen frames on the OLED so you can check it works (no game running).\n" +
            "OLEDDIAG       Ask the wheel for its own OLED layout table, log it, then show a lettered ruler that maps each layout's field boundaries. How this wheel's layouts were decoded.\n" +
            "OLEDMS <ms>    How often the wheel's OLED may be redrawn, in milliseconds (20-1000; default 100 = 10 per second). Lower is smoother and uses more of the wheel's shared command channel. Type OLEDMS with no number to read the current value. Persists.\n" +
            "OLEDANY        Run the wheel's OLED screen regardless of Telemetry Based FFB, to find out whether writing the screen really does cut a game's own force feedback the way the rev lights do (never tested for the screen; the restriction is inherited). EXPECT THE FORCE TO DROP OUT. Persists. Toggle.\n" +
            "RESETGRIP      Wipe the learned grip auto-calibration for the ACTIVE car variant (peak + confidence) and re-learn from scratch. Also clears that car's learned auto strength, which shares the same saved slot. Use after a tune or tire change that leaves the old calibration feeling off. Same as the Re-learn car button on the Telemetry Based FFB tab.\n" +
            "PREVIEWOFF     Toggle the import preview modal off; falls back to today's silent commit-on-pick path. Persists. Toggle.\n" +
            "SUPPORTER      Preview the supporter badge: cycles none -> Supporter -> Gold -> Platinum. DISPLAY ONLY (does not grant supporter access). Persists.\n" +
            "TOAST          Preview the achievement celebration toast (cycles achievements). Does NOT count toward the celebrate-once baseline.\n" +
            "SHOWALL        Reveal hidden/secret achievements (OG, Founding Supporter) in the tracker even when unearned, for testing. Reopen the tracker after toggling. Persists. Toggle.\n" +
            "WARNEMAIL      Email yourself the backup-deletion warning, cycling 6mo -> 3mo -> 1mo -> 1wk -> 1day each use. Preview only; never changes your real data or timer.\n" +
            "LAPSED         Preview the lapsed cloud-backup look in the Account tab (note + orange 'Data removal in: X'), cycling 400d -> 180d -> 30d -> 7d -> 1d -> off. Display only; uploads stay off, nothing changed.\n" +
            "IRRAW          Throwaway iRacing probe (delete after use): logs whether SimHub's raw data object reaches us live per tick, whether SteeringWheelTorque carries force while iRacing's own force feedback is disabled, and whether the 360 Hz SteeringWheelTorque_ST array is reachable. One arming dump plus one '[TF4ALL] IRRAW' line every 5 s in SimHub.txt. Toggle.";

        private void CommitAccessCode()
        {
            if (AccessCodeBox == null) return;
            ExecuteAccessCode((AccessCodeBox.Text ?? string.Empty).Trim());
        }

        // Open the scrollable test-code browser. It carries its own input box so
        // codes can be run without closing it; each run reuses ExecuteAccessCode
        // and surfaces the resulting status back into the window.
        private void ShowTestCodesWindow()
        {
            var win = new TestCodesWindow(TestCodeCatalog, c =>
            {
                if (string.IsNullOrWhiteSpace(c)) return null;
                string code = c.Trim();
                // Don't recurse into another codes window from inside this one.
                if (code.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                    || code.Equals("CODES", StringComparison.OrdinalIgnoreCase) || code == "?")
                    return "Already showing the code list.";
                if (AccessCodeStatus != null) AccessCodeStatus.Text = string.Empty;
                ExecuteAccessCode(code);
                return AccessCodeStatus?.Text;
            })
            { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }

        // Validate a calendar date from MOTDDATE's MMDDYYYY digits.
        private static bool TryBuildDate(int year, int month, int day, out DateTime date)
        {
            date = default(DateTime);
            if (year < 1 || year > 9999 || month < 1 || month > 12) return false;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            date = new DateTime(year, month, day);
            return true;
        }

        private void ExecuteAccessCode(string code)
        {
            if (_suppressEvents || _plugin?.Settings == null || AccessCodeBox == null) return;
            if (string.IsNullOrEmpty(code)) return;

            // Live Mode B tuning: "NAME value" (e.g. "BSAT 1.2", "BEMA 30",
            // "MODEB 1"). Two tokens with a numeric second token; single-word
            // codes below never match. Dispatches to the plugin-side
            // clamp+apply switch (SetModeBParam) and echoes its status. Names
            // that map to a Settings field persist; BFULL/BSPD are live-only
            // model probes.
            {
                var parts = code.Split(new[] { ' ', '=', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && float.TryParse(parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float mbVal))
                {
                    string pn = parts[0].ToUpperInvariant();
                    if (pn == "MODEB" || pn == "BSIGN" || pn == "BSAT"
                        || pn == "BPEAK" || pn == "BFLOOR" || pn == "BFULL" || pn == "BSPD"
                        || pn == "BRISE" || pn == "BEMA" || pn == "BDAMP" || pn == "BCENTER"
                        || pn == "BLAT" || pn == "BDIRK"
                        || pn == "BRECOVER" || pn == "BLOCKPT" || pn == "BCIRCLE"
                        || pn == "BLEARN" || pn == "BGTRIM" || pn == "BLDEM"
                        || pn == "BAUTOS"
                        || pn == "MBREV" || pn == "BREVG"
                       
                        || pn == "MBLEAD" || pn == "BLEAD"
                        || pn == "BMINF" || pn == "MBCPD" || pn == "BCLEAD")
                    {
                        string st = _plugin.SetModeBParam(pn, mbVal);
                        // Re-sync ALL controls from the now-updated settings
                        // (RefreshFromPlugin suppresses its own events).
                        // Without this, a code-set value sat in Settings while
                        // its slider/checkbox stayed stale, and a later drag
                        // of that stale slider (ModeBSlider_ValueChanged) or
                        // the write-all ModeBFeel_Changed silently persisted
                        // the stale control state back over it, reverting the
                        // code.
                        RefreshFromPlugin();
                        AccessCodeBox.Text = string.Empty;
                        if (AccessCodeStatus != null) AccessCodeStatus.Text = "Set " + st + " (live).";
                        return;
                    }
                }
            }

            // Self-documenting: open the scrollable test-code browser.
            if (code.Equals("HELP", StringComparison.OrdinalIgnoreCase)
                || code.Equals("CODES", StringComparison.OrdinalIgnoreCase)
                || code == "?")
            {
                AccessCodeBox.Text = string.Empty;
                ShowTestCodesWindow();
                if (AccessCodeStatus != null) AccessCodeStatus.Text = "Opened the test-code list.";
                return;
            }
            // Dev-only: flush the Message-of-the-day cache + every dismissal so a
            // fresh fetch repopulates and previously-dismissed (or edited)
            // messages reappear. Bypasses the ~6h cache TTL for testing.
            if (code.Equals("MOTDFLUSH", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.MotdCache = new MotdCacheData();
                _plugin.Settings.MotdDismissedIds?.Clear();
                _plugin.Settings.MotdPoolDismissedOn?.Clear();
                _plugin.Settings.MotdRecurringDismissedOcc?.Clear();
                _plugin.SaveMotdState();
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.Refresh(forceFetch: true);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "MOTD cache + dismissals cleared; refetching.";
                return;
            }
            // Dev-only: preview the MOTD strip as if it were a random upcoming day
            // (date-stable rolls shift to it; dismissals + nag cooldown bypassed).
            // Lets you eyeball pool variety + upcoming recurring messages on demand.
            if (code.Equals("MOTDROLL", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string summary = _motdStrip?.SimulateRandomDay();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = summary != null
                        ? "MOTD preview, random day " + summary + ". MOTDROLL again to re-roll, MOTDLIVE to exit."
                        : "MOTD strip unavailable.";
                return;
            }
            // Dev-only: preview the MOTD strip as if it were a specific date, entered
            // as MMDDYYYY right after the code (e.g. MOTDDATE12252026), to check
            // upcoming recurring messages before they trigger.
            if (code.StartsWith("MOTDDATE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string digits = code.Substring("MOTDDATE".Length).Trim();
                DateTime simDate = default(DateTime);
                bool parsedDate = digits.Length == 8
                    && int.TryParse(digits.Substring(0, 2), out int mm)
                    && int.TryParse(digits.Substring(2, 2), out int dd)
                    && int.TryParse(digits.Substring(4, 4), out int yyyy)
                    && TryBuildDate(yyyy, mm, dd, out simDate);
                if (parsedDate)
                {
                    string summary = _motdStrip?.SimulateDate(simDate);
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "MOTD preview " + summary + ". MOTDLIVE to exit.";
                }
                else if (AccessCodeStatus != null)
                {
                    AccessCodeStatus.Text = "Enter the date as MMDDYYYY, e.g. MOTDDATE12252026.";
                }
                return;
            }
            // Dev-only: leave MOTD preview and return to today's real messages.
            if (code.Equals("MOTDLIVE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.ClearSimulation();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "MOTD preview off; showing today's messages.";
                return;
            }
            // Dev-only: clear the community car-facts cache (names/engine types/
            // redlines). Refetches per car on the next car open.
            if (code.Equals("RESETGRIP", StringComparison.OrdinalIgnoreCase))
            {
                string gripStatus = _plugin.RequestGripCalReset();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null) AccessCodeStatus.Text = gripStatus;
                return;
            }
            if (code.Equals("CACHEFACTS", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.CommunityFactCache?.Clear();
                _engineCommunityFetchedKey = null;   // active car re-evaluates next tick
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Community car-facts cache cleared; it refetches per car.";
                return;
            }
            // Dev-only: clear the community preset browse cache (result lists).
            if (code.Equals("CACHEBROWSE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.ClearBrowseCache();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Community preset browse cache cleared.";
                return;
            }
            // Dev-only: clear every re-fetchable NETWORK cache at once. Excludes
            // the destructive / local ones on purpose (car corrections, local
            // cylinder detections, sign-in).
            if (code.Equals("CLEARCACHES", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.MotdCache = new MotdCacheData();
                _plugin.Settings.MotdDismissedIds?.Clear();
                _plugin.Settings.MotdPoolDismissedOn?.Clear();
                _plugin.Settings.MotdRecurringDismissedOcc?.Clear();
                _plugin.Settings.CommunityFactCache?.Clear();
                _engineCommunityFetchedKey = null;
                _plugin.ClearBrowseCache();
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.Refresh(forceFetch: true);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Cleared all network caches (MOTD, community facts, browse lists).";
                return;
            }
            // Dev-only: mark every re-fetchable network cache stale WITHOUT wiping
            // it, so the next access refetches fresh while offline copies still
            // apply. Soft counterpart to CLEARCACHES.
            if (code.Equals("STALECACHES", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.MarkAllNetworkCachesStale();
                _engineCommunityFetchedKey = null;   // active car re-evaluates next tick
                AccessCodeBox.Text = string.Empty;
                _motdStrip?.Refresh(forceFetch: true);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "All network caches marked stale; refetching fresh (offline copies kept).";
                return;
            }
            // Dev-only: launch a chosen installer with /CloseSimHub=1 so the
            // installer's silent SimHub auto-close (what the in-app Update
            // button passes once the user accepts) can be tested without a real
            // update download. WARNING: the picked installer closes SimHub.
            if (code.Equals("CLOSESIM", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = "Pick the TrueforceForAll-Setup .exe to run with /CloseSimHub=1",
                    Filter = "Installer (*.exe)|*.exe",
                };
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName)
                        {
                            UseShellExecute = true,
                            Arguments = "/CloseSimHub=1",
                        });
                        if (AccessCodeStatus != null)
                            AccessCodeStatus.Text = "Launched installer with /CloseSimHub=1 (test).";
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.ShowError(Window.GetWindow(this),
                            "Couldn't launch. See the SimHub log, then try again.",
                            ex);
                    }
                }
                return;
            }
            // Dev-only: show the periodic support modal right now, ignoring the
            // seat-time ladder, the idle gate and the ever-supported latch. Pacing
            // is NOT advanced, so this is a pure preview and repeatable.
            if (code.Equals("SUPPORT", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Support prompt shown (preview; pacing untouched).";
                ShowSupportPrompt(recordPacing: false);
                return;
            }
            // Dev-only: put the support-prompt ladder back to the start (next ask
            // due at the first rung again) and clear the decline back-off.
            if (code.Equals("SUPPORTRESET", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.SupportPromptCount = 0;
                _plugin.Settings.SupportPromptDeclineCount = 0;
                _plugin.Settings.SupportPromptLastUtc = null;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Support-prompt pacing reset.";
                return;
            }
            // Dev-only: force the one-and-done word-of-mouth banner to show
            // now (it normally needs ~2h of banked seat time). Lets us see
            // the real banner + share dialog before any of it ships. Resets
            // the dismissed latch too, so it's repeatable.
            if (code.Equals("SHARE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.ActiveStreamingSeconds = double.MaxValue;
                _plugin.Settings.ShareCtaDismissed = false;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Word-of-mouth banner forced on (test).";
                RefreshShareCtaBanner();
                return;
            }

            // Dev-only: fire a synthetic auto-ratchet sequence so the
            // inline "Auto-tuned ring buffer" banner can be exercised
            // without waiting for real underruns. The full UP-then-DOWN
            // arc demonstrates:
            //   t=0.0s   TF up  8 → 16    → banner appears
            //   t=0.7s   TF up  16 → 32   → same banner, "8 → 32"
            //   t=1.4s   Audio up 16 → 32 → same banner, both rings
            //   t=3.5s   TF down 32 → 16  → TF segment shrinks to "8 → 16"
            //   t=4.2s   TF down 16 → 8   → TF back to start, segment drops
            //   t=4.9s   Audio down 32 → 16 → audio back to start, banner auto-dismisses
            if (code.Equals("RATCHET", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin != null)
                {
                    _plugin.DebugFireRatchet(true,  8,  16);
                    ScheduleDispatcherDelay(700,  () => _plugin.DebugFireRatchet(true,  16, 32));
                    ScheduleDispatcherDelay(1400, () => _plugin.DebugFireRatchet(false, 16, 32));
                    ScheduleDispatcherDelay(3500, () => _plugin.DebugFireRatchet(true,  32, 16));
                    ScheduleDispatcherDelay(4200, () => _plugin.DebugFireRatchet(true,  16, 8));
                    ScheduleDispatcherDelay(4900, () => _plugin.DebugFireRatchet(false, 32, 16));
                }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Synthetic ratchet sequence fired (UP then DOWN; ~5 s total). Banner should appear, evolve, then auto-dismiss as both rings return to start.";
                return;
            }

            // Dev-only: toggle the aligned telemetry+FFB capture log (v2 golden
            // format, the replay-harness fixture recorder). Pure observation;
            // safe while driving. See TrueforcePlugin.ToggleFfbCapture.
            if (code.Equals("CAPTURE", StringComparison.OrdinalIgnoreCase))
            {
                string status = _plugin.ToggleFfbCapture();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = status;
                return;
            }

            // Dev-only: simulate a Forza "no packets" stall so the status line
            // reads stalled and the "Not receiving packets?" troubleshooter
            // auto-opens, without needing a live (broken) Forza session. Toggle:
            // type STALL again to clear. The actual UI change lands on the next
            // refresh tick (re-arming the auto-expand latch each toggle).
            if (code.Equals("STALL", StringComparison.OrdinalIgnoreCase))
            {
                _forceForzaStall = !_forceForzaStall;
                _forzaTroubleshootAutoExpanded = false;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _forceForzaStall
                        ? "Forza stall simulated: status reads stalled, the troubleshooter auto-opens, and the UDP setup banner shows (type STALL again to clear)."
                        : "Forza stall simulation cleared.";
                return;
            }

            // Toggle the persistent UDP-setup banner so the whole flow (banner ->
            // "Set up..." -> jump to the Forza UDP section on the Settings tab)
            // can be tested without a live session: off -> Forza -> off.
            // Lands on the next refresh tick.
            if (code.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            {
                _forceUdpSetupBanner = (_forceUdpSetupBanner + 1) % 2;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text =
                        _forceUdpSetupBanner == 1 ? "UDP setup banner: simulating Forza (type UDP again to clear)."
                      : "UDP setup banner simulation cleared.";
                return;
            }

            // Toggle the two info-tier Forza banners (SimHub-fallback notice +
            // discovered-port banner) so their InfoBannerButton styling can be
            // checked without getting Forza into those telemetry states. Lands
            // on the next refresh tick, same as UDP.
            if (code.Equals("FZBANNERS", StringComparison.OrdinalIgnoreCase))
            {
                _forceForzaInfoBanners = (_forceForzaInfoBanners + 1) % 2;
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text =
                        _forceForzaInfoBanners == 1 ? "Forza info banners forced on (type FZBANNERS again to clear)."
                      : "Forza info banners cleared.";
                return;
            }

            // Dev-only: filter the preset library to built-ins only, so we can
            // see the fresh-install library a brand-new user gets. Toggle: type
            // FRESH again to restore the full view. Nothing is deleted; the
            // user's own presets are just hidden while it's on.
            if (code.Equals("FRESH", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_presetManager != null)
                {
                    _presetManager.BuiltinsOnly = !_presetManager.BuiltinsOnly;
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = _presetManager.BuiltinsOnly
                            ? "Built-ins-only view ON: the Presets tab now shows only the shipped factory presets (your own are hidden, not deleted). Type FRESH again to restore."
                            : "Built-ins-only view OFF: your full preset library is back.";
                }
                return;
            }

            // Dev-only: cycle the supporter BADGE through tiers so the badge UI can be
            // previewed without being a real supporter. DISPLAY ONLY: it sets a local
            // override feeding only the badge label; the real backup gate is enforced
            // server-side (RLS), so this never grants supporter access. Persists. Cycles
            // none -> Supporter -> Gold Supporter -> Platinum Supporter -> none.
            if (code.Equals("SUPPORTER", StringComparison.OrdinalIgnoreCase)
                || code.Equals("BADGE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string cur = _plugin.Settings.DevSupporterBadgeOverride ?? "";
                string next;
                switch (cur)
                {
                    case "":               next = "Supporter";          break;
                    case "Supporter":      next = "Gold Supporter";     break;
                    case "Gold Supporter": next = "Platinum Supporter"; break;
                    default:               next = "";                   break;
                }
                _plugin.Settings.DevSupporterBadgeOverride = next;
                try { _plugin.PersistSettings(); } catch { }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = string.IsNullOrEmpty(next)
                        ? "Supporter badge override cleared (shows your real entitlement again)."
                        : "Supporter badge preview: " + next + " (DISPLAY ONLY; does not grant supporter access).";
                _ = RefreshSupporterBadgeAsync();
                return;
            }

            // Dev-only: preview the achievement celebration toast, cycling through the
            // achievements on each use. Does NOT affect the real "earned once" baseline.
            if (code.Equals("TOAST", StringComparison.OrdinalIgnoreCase)
                || code.Equals("CELEBRATE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                _ = PreviewAchievementToastAsync();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Preview achievement toast fired (does not affect your real progress).";
                return;
            }

            // Dev-only: reveal the secret achievements (OG, Founding Supporter) in the tracker even
            // when unearned, by asking the server to include them (get_my_achievements p_include_secret).
            // Toggle; persists (machine-local).
            if (code.Equals("SHOWALL", StringComparison.OrdinalIgnoreCase)
                || code.Equals("ALLACHIEVEMENTS", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                bool next = !_plugin.Settings.DevShowAllAchievements;
                _plugin.Settings.DevShowAllAchievements = next;
                try { _plugin.PersistSettings(); } catch { }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = next
                        ? "Showing ALL achievements incl. hidden ones. Reopen the achievements tracker to see them."
                        : "Hidden achievements back to normal (shown only when earned).";
                return;
            }

            // Dev-only: email yourself the backup-deletion warning, cycling stage 1..5
            // (6mo / 3mo / 1mo / 1wk / 1day) on each use so you can see the escalating copy.
            // Sends to your signed-in address; never changes your real entitlement / timer.
            if (code.Equals("WARNEMAIL", StringComparison.OrdinalIgnoreCase)
                || code.Equals("WARN", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_plugin == null || !_plugin.AuthIsSignedIn)
                {
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = "Sign in first to test the warning email.";
                    return;
                }
                int stage = _warnPreviewStage;
                _warnPreviewStage = stage >= 5 ? 1 : stage + 1;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Sending warning email stage " + stage + "/5 to your address...";
                _ = SendWarnPreviewAndReport(stage);
                return;
            }

            // Dev-only: cycle a DISPLAY-ONLY preview of the lapsed cloud-backup state in the Account
            // tab (the contextual note + the orange "Data removal in: X" countdown), stepping the
            // representative removal date 400d -> 180d -> 30d -> 7d -> 1d -> off. Never enables
            // uploads and never touches your real entitlement; it only changes what is drawn.
            if (code.Equals("LAPSED", StringComparison.OrdinalIgnoreCase)
                || code.Equals("LAPSE", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                _lapsedPreviewIndex++;
                if (_lapsedPreviewIndex >= _lapsedPreviewDays.Length) _lapsedPreviewIndex = -1;   // wrap to off
                _ = RefreshCloudBackupGatingAsync();
                if (AccessCodeStatus != null)
                {
                    AccessCodeStatus.Text = _lapsedPreviewIndex < 0
                        ? "Lapsed cloud-backup preview off (showing your real state). Open the Account tab to see it."
                        : "Lapsed cloud-backup preview: removal in ~" + _lapsedPreviewDays[_lapsedPreviewIndex]
                          + " days (display only; uploads stay off, nothing changed). Open the Account tab to see it.";
                }
                return;
            }

            // Dev-only: toggle the experimental legacy "F8 12" rev-LED sweep on
            // the wheel's gamepad/DirectInput collection (off the HID++ FFB pipe).
            // Confirms on hardware whether the legacy LED command lights the strip
            // AND coexists with live FFB -- a non-contending LED path we could use
            // in every game, not just iRacing. Toggle: type it again to stop
            // (LEDs off). See LegacyLedF8Channel + project_led_ffb_contention_model.
            {
                var f8parts = code.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string f8cmd = f8parts.Length > 0 ? f8parts[0].ToUpperInvariant() : string.Empty;
                if (f8cmd == "F8SWEEP" || f8cmd == "F8" || f8cmd == "F8FAST"
                    || f8cmd == "F8SPAM" || f8cmd == "F8SLOW")
                {
                    AccessCodeBox.Text = string.Empty;
                    int? resend = null;        // null => simple on/off toggle (paced)
                    if (f8cmd == "F8FAST" || f8cmd == "F8SPAM") resend = 16;
                    else if (f8cmd == "F8SLOW") resend = 0;
                    else if (f8parts.Length > 1)
                    {
                        string a = f8parts[1].ToUpperInvariant();
                        if (a == "FAST" || a == "SPAM") resend = 16;
                        else if (a == "SLOW") resend = 0;
                        else if (int.TryParse(f8parts[1], out int ms))
                            resend = Math.Max(0, Math.Min(1000, ms));
                    }
                    string status = _plugin?.ToggleF8LedSweep(resend) ?? "(plugin not ready)";
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = status;
                    return;
                }
            }

            // Dev-only: unlock the Developer panel (built-in folder
            // maintenance: export/import/reseed/validate/open). Persisted so it
            // stays on across restarts on a dev machine. Toggle.
            if (code.Equals("DEV", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.DevModeUnlocked = !_plugin.Settings.DevModeUnlocked;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                ApplyDevModeVisibility();
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.Settings.DevModeUnlocked
                        ? "Developer mode ON: the Presets tab now shows the Developer tools bar + per-row 'Set as built-in' promote buttons. Type DEV again to hide."
                        : "Developer mode OFF.";
                return;
            }

            // Dev-only one-shot: for every car whose default points at a USER
            // preset, promote that user preset to a factory built-in (replacing
            // existing built-in(s) for that car), repoint the factory car-default,
            // and delete the user preset. Other user presets for the same car
            // stay put. Idempotent.
            // Dev-only one-shot: legacy 'Forza_<ordinal>' car ids from the old
            // UDP fallback get normalized to 'Car_<ordinal>' (what SimHub's
            // data feed emits for Forza). Per-car rule: if Car_<n> already
            // exists, drop the Forza_<n>; otherwise rename Forza_<n> to
            // Car_<n> and rewrite the inner CarId/PresetName fields.
            if (code.Equals("NORMALIZEFORZA", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_plugin != null)
                {
                    int n = _plugin.DevNormalizeForzaCarIds(out var details);
                    if (n > 0) _presetManager?.RefreshLists();
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = details;
                    TrueforceDialog.Show(Window.GetWindow(this),
                        "Trueforce For All: normalize Forza car ids", details,
                        DialogKind.Info);
                }
                return;
            }

            if (code.Equals("FOLDDEFAULTS", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                if (_plugin != null)
                {
                    int n = _plugin.DevConsolidateUserCarDefaults(out var details);
                    // The plugin reloads its own caches; the preset manager
                    // caches its row collections separately and needs an
                    // explicit refresh.
                    if (n > 0) _presetManager?.RefreshLists();
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = details;
                    if (n > 0)
                        TrueforceDialog.Show(Window.GetWindow(this),
                            "Trueforce For All: consolidated car defaults", details,
                            DialogKind.Info);
                }
                return;
            }

            // Dev-only: feel the stationary spring on the desk without a game.
            // Drives a synthetic centering force that flips direction every
            // ~1.5 s for ~6 s, bypassing the enabled/speed/steering gates.
            // Lets us verify strength + the force-vs-position direction. It
            // does NOT verify a given game's steering sign (that needs a
            // session); it confirms the spring's own mapping is correct.
            if (code.Equals("SPRING", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.StartStationarySpringTest();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stationary-spring test (~6 s): hold the wheel; the motor pushes one way, then the other, every ~1.5 s, so you can feel the spring strength and direction. (Needs the wheel connected and streaming.)";
                return;
            }

            // Dev-only: exercise the rev limiter's RPM threshold + hold from
            // synthetic telemetry (the Test button only plays the buzz). Sweeps
            // below threshold (silent) -> over redline (buzz) -> off.
            if (code.Equals("REV", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugRevLimiterBounce();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Rev limiter test (~5 s): silent for ~1.5 s (below redline), buzzes ~2.7 s (at/over redline), then stops. Confirms it engages on RPM and holds, independent of the Test button.";
                return;
            }

            // Dev-only: re-show the "What's new" changelog banner and every
            // per-effect NEW badge by clearing the seen state. Lets us verify
            // the release UX (banner copy + the new RevLimiter badge) without
            // hand-editing the settings file.
            if (code.Equals("WHATSNEW", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugResetChangelogSeen();
                RefreshChangelogBanner();
                RefreshNewBadges();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Changelog state reset: the 'What's new' banner and all NEW effect badges are showing again (banner lists full history).";
                return;
            }

            // Dev-only: reset the networked-welcome modal seen state so
            // the pitch fires again immediately. Clears all three
            // gating fields: HasSeenNetworkedWelcome (the hard latch),
            // WelcomeDeclineCount (the second-decline-locks counter),
            // and WelcomeNextShowAt (the 14-day re-show timer).
            if (code.Equals("WELCOME", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.HasSeenNetworkedWelcome = false;
                _plugin.Settings.WelcomeDeclineCount     = 0;
                _plugin.Settings.WelcomeNextShowAt       = null;
                // Also reset the Mode B intro so both first-run modals can be retested.
                _plugin.Settings.HasSeenModeBIntro       = false;
                try { _plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Networked-welcome reset (opening now); Mode B intro re-armed for the Telemetry Based FFB tab.";
                // Re-trigger via the same gate the normal startup path
                // uses so any preconditions (backend URL configured,
                // etc.) apply identically. Clear the per-session guard
                // first or the reset would no-op after an earlier show.
                WelcomeWindow.ShownThisSession = false;
                MaybeShowNetworkedWelcome();
                // The Mode B intro is NOT force-shown here: it would pop over
                // whatever screen you're on (and stack on the welcome, which is
                // the exact behavior we moved it off of). Clearing HasSeenModeBIntro
                // above re-arms it; it shows when the Telemetry Based FFB tab opens.
                return;
            }

            // Dev/test: preview the EXACT in-app render of release-notes
            // markdown from the clipboard. Since the plugin can't fetch an
            // unpublished draft from GitHub, copy the draft's markdown body,
            // type PREVIEW, and see it rendered through the same code path the
            // live What's-new uses.
            if (code.Equals("PREVIEW", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string md = null;
                try { if (System.Windows.Clipboard.ContainsText()) md = System.Windows.Clipboard.GetText(); }
                catch { }
                if (string.IsNullOrWhiteSpace(md))
                {
                    if (AccessCodeStatus != null)
                        AccessCodeStatus.Text = "Clipboard has no text. Copy the release-notes markdown first, then type PREVIEW.";
                    return;
                }
                ShowNotesPreview(md);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Previewed the clipboard markdown as the in-app What's new would render it.";
                return;
            }

            // Dev-only: pretend a newer release exists so the update banner +
            // update modal can be verified locally. Appears on the next status
            // refresh tick (~1 s).
            if (code.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.UpdateChecker?.DebugSimulateUpdateAvailable();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.UpdateChecker != null
                        ? "Simulated update available: the update banner appears within ~1 s; click it to see the update modal."
                        : "Update checker unavailable.";
                return;
            }

            // Dev-only: like UPDATE, but also (1) reports unsaved changes so the
            // pre-update warning fires, and (2) opens the update modal in a mode
            // where "Update now" runs a locally-picked installer instead of
            // downloading from GitHub. Exercises the whole in-app update flow
            // (unsaved-changes guard, then launch the installer with
            // /CloseSimHub=1) without a real release. WARNING: the picked
            // installer closes SimHub.
            if (code.Equals("UPDATEDIRTY", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                var checker = _plugin.UpdateChecker;
                if (checker == null)
                {
                    if (AccessCodeStatus != null) AccessCodeStatus.Text = "Update checker unavailable.";
                    return;
                }
                checker.DebugSimulateUpdateAvailable();
                _updateLocalInstallerTest = true;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Local-installer update test: the unsaved-changes warning is armed; pick an installer when you click Update now.";
                ShowUpdateModal();
                return;
            }

            // Dev-only: simulate a release shipping AFTER launch. Arms a fake
            // newer release that only the background re-check applies, plus a
            // fast cadence, so the update banner appears on its own within a few
            // seconds (no restart, no real release) instead of after hours.
            // Optional trailing number sets the cadence in seconds (e.g.
            // UPDATEPOLL3); defaults to 5s. Run again to stop + clear.
            if (code.StartsWith("UPDATEPOLL", StringComparison.OrdinalIgnoreCase))
            {
                AccessCodeBox.Text = string.Empty;
                string rest = code.Substring("UPDATEPOLL".Length).Trim();
                int seconds = 5;
                if (rest.Length > 0 && int.TryParse(rest, out int parsed) && parsed > 0)
                    seconds = parsed;
                int active = _plugin.DebugToggleFastUpdatePolling(seconds);
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = active > 0
                        ? $"Armed: no banner yet, but within ~{active}s a background re-check discovers a simulated newer release and the 'Update to vX.Y.Z' banner appears on its own. No restart. Type UPDATEPOLL again to stop + clear."
                        : "Stopped: simulated release cleared, back to normal update cadence.";
                return;
            }

            // Dev-only: force the wheel into the stream-fault state so the
            // recovery watchdog re-attaches it. Verifies the "Stream lost -
            // auto-reconnecting" status + transparent recovery without
            // physically unplugging.
            if (code.Equals("FAULT", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugForceStreamFault();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Stream fault forced: status should read 'Stream lost, auto-reconnecting', then recover within a few seconds (watchdog re-attaches the wheel).";
                return;
            }

            // Dev/tester: high-rate FFB signal-chain trace (game force vs the
            // plugin's output vs steering, at the full provider rate). Type
            // TRACE to start, reproduce the issue, TRACE again to dump the
            // CSV under Documents\TrueforceForAll.
            if (code.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
            {
                string traceStatus = _plugin.ToggleFfbTrace();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null) AccessCodeStatus.Text = traceStatus;
                return;
            }

            // THROWAWAY DIAGNOSTIC SPIKE. Answers, from SimHub.txt on a live
            // rig, whether SimHub's raw iRacing object reaches a third-party
            // plugin per tick, whether SteeringWheelTorque carries force while
            // iRacing's own force feedback is disabled, and whether the 360 Hz
            // SteeringWheelTorque_ST array is reachable through the telemetry
            // object's dictionary base. Delete with the probe in
            // TrueforcePlugin.DebugToggleIracingRawProbe.
            if (code.Equals("IRRAW", StringComparison.OrdinalIgnoreCase))
            {
                bool irRawOn = _plugin.DebugToggleIracingRawProbe();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = irRawOn
                        ? "iRacing raw-telemetry probe armed. Get on track and drive: it writes one arming dump plus one '[TF4ALL] IRRAW' line every 5 seconds to SimHub.txt. Drive about 30 s with iRacing's own force feedback DISABLED, then about 30 s with it enabled, then type IRRAW again to stop."
                        : "iRacing raw-telemetry probe stopped. Look for '[TF4ALL] IRRAW' lines in SimHub.txt.";
                return;
            }

            // Motor-characterization sweeps. Plain SWEEP: 15 s log sine, 8 to
            // 300 Hz, even time per octave. SWEEP1..SWEEP6: one octave band
            // each (~5 s) so a tester can judge bands independently instead
            // of tracking seconds inside one long run. The length guard keeps
            // this from matching F8SWEEP (the legacy rev-LED sweep) or any
            // longer SWEEP-prefixed input.
            if (code.StartsWith("SWEEP", StringComparison.OrdinalIgnoreCase)
                && (code.Length == 5 || (code.Length == 6 && code[5] >= '1' && code[5] <= '6')))
            {
                var sweep = _plugin.MotorSweep;
                string what;
                if (code.Length == 6)
                {
                    // Octave bands: 1:8-16, 2:16-32, 3:32-63, 4:63-125,
                    // 5:125-250, 6:250-400 (the top band overshoots 300 on
                    // purpose to find the true ceiling).
                    float[] edges = { 8f, 16f, 32f, 63f, 125f, 250f, 400f };
                    int band = code[5] - '1';
                    sweep.StartHz    = edges[band];
                    sweep.EndHz      = edges[band + 1];
                    sweep.DurationMs = 5000;
                    what = $"Band {band + 1}/6: {edges[band]:0}-{edges[band + 1]:0} Hz for 5 s.";
                }
                else
                {
                    sweep.StartHz    = 8f;
                    sweep.EndHz      = 300f;
                    sweep.DurationMs = 15000;
                    what = "Full sweep (15 s): 8-300 Hz, even time per octave. For band-by-band judging use SWEEP1..SWEEP6.";
                }
                _plugin.TestEffect(sweep);
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = what + " Hands lightly on the rim; rate it strong / buzzy / weak / dead.";
                return;
            }

            // Simulate "tap sees the wheel + FFB on the wire but can't extract
            // it." Drive after enabling: the tap retries in whole-bus mode after
            // ~8 s and surfaces the no-FFB notice after ~15 s. Toggle off to
            // restore real FFB pass-through. FFB will be limp while it's on.
            if (code.Equals("NOFFB", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleSimulateNoFfb();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Simulating no-FFB capture. Drive for ~15s: the FFB tap should switch to whole-bus capture (~8s), then the FFB-tap status should show a 'try a different USB port' notice. Type NOFFB again to stop (FFB stays limp until you do)."
                        : "Stopped simulating no-FFB capture. Force feedback pass-through restored.";
                return;
            }

            // Opt in to the experimental FFB-capture path (issue #8: HID++
            // very-long report 0x12 + lower index-resolve floor). Persisted and
            // applied live; drive to let it re-resolve the FFB index. Toggle.
            if (code.Equals("FFBX", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleExperimentalCapture();
                // Reflect the new state on both checkboxes without re-firing
                // their handlers (the plugin setter already ran above).
                var prevSuppress = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    if (ExperimentalFfbCheck != null) ExperimentalFfbCheck.IsChecked = on;
                }
                finally { _suppressEvents = prevSuppress; }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Experimental FFB capture ON (persists). Load a game and drive for a few seconds so the tap re-resolves the FFB index; check the FFB-tap status. Type FFBX again to turn it off."
                        : "Experimental FFB capture OFF. Back to the standard capture path.";
                return;
            }

            // Tap-free AC FFB: re-inject AC's shared-memory finalFF instead of
            // the USBPcap wire capture. The shm value wins while fresh; the
            // pcap tap (if present at all) remains the fallback. Persisted and
            // applied to the live AC source immediately. Toggle.
            if (code.Equals("ACFFB", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleAcShmFfb();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "AC shared-memory FFB ON (persists). Drive in Assetto Corsa with in-game FFB ENABLED (gain above 0): the wheel force now comes from AC's finalFF, no USBPcap involved. If forces feel reversed or wrong in strength, note it; that calibration is exactly what this test decides. Type ACFFB again to turn off."
                        : "AC shared-memory FFB OFF. Back to the USBPcap capture path.";
                return;
            }

            // Driver testing mode: route FFB through the TFFA kernel filter
            // driver (sole wheel ownership) instead of the USBPcap tap. Needs
            // the TFFA filter driver installed. The code both REVEALS the
            // hidden "Driver testing mode" checkbox (persisted via
            // DriverTestingUnlocked, mirrors MANUALPIN) AND toggles the feature
            // on/off (ExperimentalDriverIntercept). Once revealed the checkbox
            // stays visible across restarts and the user drives it from there.
            // Applied on the next plugin init (re-detect / restart SimHub).
            if (code.Equals("DRIVER", StringComparison.OrdinalIgnoreCase))
            {
                // DRIVER is a full on/off for driver testing mode. First entry:
                // unlock + reveal the checkbox and turn the feature on. Type
                // DRIVER again to turn it off AND hide the checkbox again (back
                // to the default hidden state). While unlocked, the revealed
                // checkbox toggles the feature without hiding it.
                bool unlocked = false;
                if (_plugin?.Settings != null)
                {
                    unlocked = !_plugin.Settings.DriverTestingUnlocked;
                    _plugin.Settings.DriverTestingUnlocked       = unlocked;
                    _plugin.Settings.ExperimentalDriverIntercept = unlocked;
                    _plugin.PersistSettings();
                }
                AccessCodeBox.Text = string.Empty;
                // Reflect the new state without re-firing the checkbox's Changed
                // handler (the setters already ran above).
                var prevSuppress = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    var vis = unlocked ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    if (DriverInterceptCheck != null)
                    {
                        DriverInterceptCheck.Visibility = vis;
                        DriverInterceptCheck.IsChecked  = unlocked;
                    }
                    if (DriverInterceptHelp != null)
                        DriverInterceptHelp.Visibility = vis;
                }
                finally { _suppressEvents = prevSuppress; }
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = unlocked
                        ? "Driver testing mode ON (restart SimHub / re-detect to apply). Needs the TFFA filter driver installed. A checkbox is now shown in the Settings tab; type DRIVER again to turn it off and hide it."
                        : "Driver testing mode OFF and hidden. Type DRIVER again to turn it back on.";
                return;
            }

            // Issue #13 test: stop the Trueforce stream while paused so the wheel
            // hands back to the game's native FFB / auto-center. Persists. Toggle.
            if (code.Equals("OLEDTEST", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin == null) return;
                _plugin.TestOled();
                PollOledStatus();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Showing sample screens on the wheel. Watch the Screen status "
                        + "line beside the wheel-screen settings for each step. Run it with no game driving "
                        + "the wheel: writing the screen while a game sends its own force feedback cuts that "
                        + "force feedback out.";
                return;
            }

            if (code.Equals("OLEDDIAG", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin == null) return;
                _plugin.ReportOledLayouts();
                PollOledStatus();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Reading the wheel's own layout table into the log, then showing "
                        + "a lettered ruler so each layout's field boundaries can be read off the screen. "
                        + "Look for [OLED] lines in SimHub.txt.";
                return;
            }

            if (code.StartsWith("OLEDMS", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin?.Settings == null) return;
                string arg = code.Substring(6).Trim();
                if (int.TryParse(arg, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int ms))
                {
                    if (ms < 20) ms = 20; else if (ms > 1000) ms = 1000;
                    _plugin.Settings.OledWriteIntervalMs = ms;
                    _plugin.PersistSettings();
                }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                {
                    int cur = _plugin.Settings.OledWriteIntervalMs;
                    AccessCodeStatus.Text = $"OLED write interval: {cur} ms (about {1000 / Math.Max(1, cur)} "
                        + "updates a second). Lower is smoother and uses more of the wheel's command channel. "
                        + "Drive with it and watch the force feedback: if it starts to feel soft or cuts, "
                        + "you have found the limit. OLEDMS with no number just reports the current value.";
                }
                return;
            }

            if (code.Equals("OLEDANY", StringComparison.OrdinalIgnoreCase))
            {
                if (_plugin?.Settings == null) return;
                bool on = !_plugin.Settings.OledIgnoreModeBGate;
                _plugin.Settings.OledIgnoreModeBGate = on;
                _plugin.PersistSettings();
                if (!on) _plugin.TurnOffOled();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "OLED gate OFF: the wheel screen now runs in any game, including ones sending their own force feedback. This is the experiment, so EXPECT the force to cut out; if it does, that confirms the screen shares the rev lights' limitation. Type OLEDANY again to put the gate back."
                        : "OLED gate back on: the wheel screen runs only under Telemetry Based FFB again.";
                return;
            }

            if (code.Equals("NOLOCK", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleStopStreamOnPause();
                // Keep the visible checkbox in sync without re-firing its handler.
                var prevSuppress = _suppressEvents;
                _suppressEvents = true;
                try { if (StopStreamOnPauseCheck != null) StopStreamOnPauseCheck.IsChecked = on; }
                finally { _suppressEvents = prevSuppress; }
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Pause behavior: hand the wheel back to the game while paused, ON (the default; persists)."
                        : "Pause behavior: hand-back OFF (persists). The plugin keeps hold of the wheel and releases its force to zero while paused. Type NOLOCK again for the default.";
                return;
            }

            // Opt in to the experimental home-screen Feedback gain tile. Splices a
            // Trueforce master + audio gain box into SimHub's hardcoded Feedback
            // section (next to Motors/Wind) via a defensive visual-tree injection.
            // Persisted and applied live. Toggle.
            if (code.Equals("HOMEBOX", StringComparison.OrdinalIgnoreCase))
            {
                bool on = _plugin.DebugToggleFeedbackBox();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Home Feedback gain tile ON (persists). Open SimHub's home screen; a 'Trueforce' box appears next to Motors/Wind. If it doesn't show, the home tab may not be open yet, switch to it. Type HOMEBOX again to turn it off."
                        : "Home Feedback gain tile OFF. Removed from the home screen.";
                return;
            }

            // Escape hatch for the import preview modal. Off by default; flip
            // on to fall back to today's silent commit-on-pick path if the
            // modal breaks on a specific file. Persists.
            if (code.Equals("PREVIEWOFF", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.Settings.ImportPreviewBypass = !_plugin.Settings.ImportPreviewBypass;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = _plugin.Settings.ImportPreviewBypass
                        ? "Import preview OFF. Picks commit silently on Import. Type PREVIEWOFF again to re-enable."
                        : "Import preview ON. Import shows a confirmation modal before committing.";
                return;
            }

            // Dev/test: force the "is your FFB working?" success banner on now,
            // so the Yes (prefilled report) and No (troubleshooter) paths can be
            // exercised without real hardware. Dismiss (or click either button)
            // clears it.
            if (code.Equals("FFBOK", StringComparison.OrdinalIgnoreCase))
            {
                _plugin.DebugShowSuccessBanner();
                RefreshExperimentalSuccessBanner();
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "Success banner forced on (test). Click 'Yes, it's working' to see the prefilled report, or 'No' for the troubleshooter. The x or either button clears it.";
                return;
            }

            // Reveal (or hide) the Diagnostics "Pick device manually..."
            // control. Off by default since auto-discovery + identity-based
            // self-heal cover the realistic failure modes and a forgotten
            // pin silently breaks FFB after the wheel changes USB address
            // (issue #17). Power users with a real need (multi-wheel
            // disambiguation, or a USBPcap interface mismatch they want to
            // override) flip it on here; persists across restarts.
            if (code.Equals("MANUALPIN", StringComparison.OrdinalIgnoreCase))
            {
                bool on = !(_plugin.Settings.ShowManualOverrideUi);
                _plugin.Settings.ShowManualOverrideUi = on;
                _plugin.PersistSettings();
                AccessCodeBox.Text = string.Empty;
                var pickerVis = on
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
                if (UsbPcapPickDeviceButton    != null) UsbPcapPickDeviceButton.Visibility    = pickerVis;
                if (FfbTapPickerBannerButton   != null) FfbTapPickerBannerButton.Visibility   = pickerVis;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = on
                        ? "Manual device picker revealed (Diagnostics + the contextual banner). Persists. Type MANUALPIN again to hide it."
                        : "Manual device picker hidden (persists).";
                return;
            }

            if (code.Equals("TEST", StringComparison.OrdinalIgnoreCase))
            {
                // Retired 2026-08-01: the iRacing section is permanently
                // hidden (the external FFB handoff it configured is gone).
                // Answer instead of silently ignoring a code that used to work.
                AccessCodeBox.Text = string.Empty;
                if (AccessCodeStatus != null)
                    AccessCodeStatus.Text = "The iRacing section is retired. Rev lights now have a toggle on the Telemetry FFB tab.";
                return;
            }

            // Give a visible result for a typed-but-unrecognized code instead
            // of swallowing it silently (blank input stays silent).
            if (!string.IsNullOrWhiteSpace(code) && AccessCodeStatus != null)
                AccessCodeStatus.Text = "Code not recognized. Type HELP to list valid codes.";
        }

        private void ResetNotices_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ResetOneTimeNotices();
            // Also clear this session's guards so notices can re-fire without a restart.
            _lastGameForIracingNotice = null;
            _welcomeTriggeredThisSession = false;
            WelcomeWindow.ShownThisSession = false;
            try { RefreshFromPlugin(); } catch { }
            if (ResetNoticesStatus != null)
                ResetNoticesStatus.Text = "Done. One-time messages will show again.";
        }

        // ---------- Developer panel (built-in folder maintenance) ----------

        // Reflect Settings.DevModeUnlocked into the preset manager (which now
        // owns the Developer tools bar + per-row export buttons).
        private void ApplyDevModeVisibility()
        {
            bool on = _plugin?.Settings?.DevModeUnlocked == true;
            if (_presetManager != null)
                _presetManager.DevMode = on;
            if (BackupSelfTestButton != null)
                BackupSelfTestButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        // Dev-only: run the full backup round-trip + classification audit in-process
        // and show the pass/fail checklist. Same RunRoundTrip the console harness uses;
        // the audit lines flag any unclassified TrueforceSettings field (the one-line
        // classification reminder when a new top-level setting/effect is added).
        private void BackupSelfTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (ok, lines) = BackupSelfTest.RunRoundTrip();
                TrueforceDialog.Show(null,
                    ok ? "Backup self-test: PASS" : "Backup self-test: FAIL",
                    string.Join(Environment.NewLine, lines),
                    ok ? DialogKind.Info : DialogKind.Warning);
            }
            catch (Exception ex)
            {
                TrueforceDialog.Show(null, "Backup self-test",
                    "Backup self-test threw: " + ex.Message,
                    DialogKind.Error);
            }
        }

        // ---------- Rim rev/shift LEDs ----------

        private void ModeBRevLights_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.ModeBRevLightsEnabled = ModeBRevLightsCheck.IsChecked == true;
            _plugin.PersistSettings();
            if (!_plugin.Settings.ModeBRevLightsEnabled) _plugin.TurnOffRpmLeds();
        }

        // Labels for effects 1-9 (index = effect number; [0] unused). The
        // wheel stores the patterns; picking one sets the wheel's single
        // selector, exactly like the base's own menu. The 1-4 direction
        // names are INFERRED from mescon's 9.4.1 direction table
        // (1=inside-out, 2=outside-in, 3=L->R, 4=R->L); effect 2 =
        // outside-in is confirmed on hardware and matches that table, the
        // other three are eyeball-checkable in seconds via the pick preview.
        private static readonly string[] RevLightEffectLabels =
        {
            "(unused)",
            "Inside-out (built-in 1)", "Outside-in (built-in 2)",
            "Left to right (built-in 3)", "Right to left (built-in 4)",
            "Custom slot 1", "Custom slot 2", "Custom slot 3",
            "Custom slot 4", "Custom slot 5",
        };

        // Fill a pattern combo with effects 1-9 (Tag = effect number).
        private void FillPatternCombo(ComboBox combo, System.Windows.Input.MouseButtonEventHandler onItemClick)
        {
            for (int i = 1; i < RevLightEffectLabels.Length; i++)
            {
                var it = new ComboBoxItem { Content = RevLightEffectLabels[i], Tag = i };
                // Re-picking the SELECTED item raises no SelectionChanged;
                // the click handler replays the preview for that case.
                it.PreviewMouseLeftButtonUp += onItemClick;
                combo.Items.Add(it);
            }
        }

        /// <summary>Sync one pattern combo to the wheel's known selection.
        /// Both pickers show the same thing: where the wheel actually is
        /// (empty until the channel has read it once).</summary>
        private void SyncPatternCombo(ComboBox combo)
        {
            int v = _plugin.GetWheelPatternSelection();
            combo.SelectedIndex = (v >= 1 && v <= 9) ? v - 1 : -1;
        }

        /// <summary>Sync a row's Remember button + status line to the active
        /// car and the pattern the row's combo is showing (the combo, not
        /// the channel, so the state is right in the instant after a pick,
        /// before the background write lands).</summary>
        private void SyncRememberControls(ComboBox combo, System.Windows.Controls.Button btn,
            TextBlock status, System.Windows.Documents.Run run)
        {
            bool hasCar = !string.IsNullOrEmpty(_plugin.ActiveCarId);
            int sel = (combo?.SelectedItem as ComboBoxItem)?.Tag is int t ? t : 0;
            int? rem = hasCar ? _plugin.GetCarRememberedPattern() : null;
            if (btn != null)
            {
                btn.IsEnabled = hasCar && sel >= 1 && sel <= 9 && rem != sel;
                btn.Content = (rem.HasValue && rem.Value == sel)
                    ? "Remembered" : "Remember for this car";
                btn.ToolTip = hasCar
                    ? "Optional: re-apply this pattern whenever this car loads. "
                      + "Your pick is already on the wheel either way."
                    : "Needs an active car. Your pick is already on the wheel either way.";
            }
            if (status != null)
            {
                bool show = rem.HasValue && rem.Value >= 1 && rem.Value <= 9;
                status.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show && run != null)
                    run.Text = $"Remembered: {RevLightEffectLabels[rem.Value]}";
            }
        }

        /// <summary>Sync the Wheel-lights row (combo + remember controls).
        /// A blank combo means the wheel's selection has never been read;
        /// kick a background channel open (safe-gated in the plugin) and
        /// poll briefly so the value fills in by itself instead of waiting
        /// for a game or the Test button to open the channel.</summary>
        private void RefreshRevLightPicker()
        {
            if (RevLightEffectCombo == null || _plugin?.Settings == null) return;
            // Fixed-look strips (G923) get no picker at all; a pattern
            // selector there reads as a feature the owner is missing.
            bool selectable = _plugin.WheelHasSelectableLightPattern;
            if (RevLightPatternRow != null)
                RevLightPatternRow.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
            if (RevLightPatternHelp != null)
                RevLightPatternHelp.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
            if (!selectable) return;
            if (RevLightEffectCombo.IsDropDownOpen) return;
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (RevLightEffectCombo.Items.Count == 0)
                    FillPatternCombo(RevLightEffectCombo, RevLightItem_Clicked);
                SyncPatternCombo(RevLightEffectCombo);
                SyncRememberControls(RevLightEffectCombo, RevLightRememberBtn,
                    RevLightRememberStatus, RevLightRememberRun);
            }
            finally { _suppressEvents = prev; }

            if (_plugin.GetWheelPatternSelection() <= 0) KickPatternRead();
        }

        // One-shot read-back poll: the channel open runs in the background
        // and there is no callback path from it to this panel, so poll a few
        // times and stop the moment the selection is known (or give up
        // quietly; blank remains the honest state for an absent wheel).
        private System.Windows.Threading.DispatcherTimer _revReadPoll;
        private int _revReadPollTicks;

        private void KickPatternRead()
        {
            _plugin.EnsureWheelPatternRead();
            if (_revReadPoll != null) return;
            _revReadPollTicks = 0;
            _revReadPoll = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(700) };
            _revReadPoll.Tick += (s, a) =>
            {
                bool known = _plugin?.GetWheelPatternSelection() > 0;
                if (known || ++_revReadPollTicks > 15)
                {
                    _revReadPoll.Stop();
                    _revReadPoll = null;
                    if (known)
                    {
                        RefreshRevLightPicker();
                        RefreshCarRevLightRow();
                    }
                }
            };
            _revReadPoll.Start();
        }

        /// <summary>Sync the Car facts row: the SAME nine items and the SAME
        /// value as the Wheel-lights picker (two remotes for the wheel's one
        /// selector), plus this row's remember controls. Left alone while
        /// the dropdown is open so the poll that drives RefreshCarFactsPanel
        /// cannot yank a menu out from under the user's cursor.</summary>
        private void RefreshCarRevLightRow()
        {
            if (CarRevLightCombo == null || _plugin?.Settings == null) return;
            bool selectable = _plugin.WheelHasSelectableLightPattern;
            if (CarRevLightSeparator != null)
                CarRevLightSeparator.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
            if (CarRevLightRow != null)
                CarRevLightRow.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
            if (CarRevLightRowHelp != null)
                CarRevLightRowHelp.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
            if (!selectable) return;
            if (CarRevLightCombo.IsDropDownOpen) return;
            bool prev = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (CarRevLightCombo.Items.Count == 0)
                    FillPatternCombo(CarRevLightCombo, CarRevLightItem_Clicked);
                SyncPatternCombo(CarRevLightCombo);
                SyncRememberControls(CarRevLightCombo, CarRevLightRememberBtn,
                    CarRevLightRememberStatus, CarRevLightRememberRun);
            }
            finally { _suppressEvents = prev; }
        }

        private void CarRevLight_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var picked = CarRevLightCombo.SelectedItem as ComboBoxItem;
            if (!(picked?.Tag is int v)) return;
            _plugin.PickRevLightPattern(v);
            RefreshRevLightPicker();   // the other remote for the same selector
            SyncRememberControls(CarRevLightCombo, CarRevLightRememberBtn,
                CarRevLightRememberStatus, CarRevLightRememberRun);
            _plugin.PreviewRevLightPattern();
        }

        private void CarRevLightItem_Clicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            if (!ReferenceEquals(CarRevLightCombo.SelectedItem, sender)) return;
            _plugin.PreviewRevLightPattern();
        }

        private void RevLightItem_Clicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            // Only the re-pick of the CURRENT item: a real change raises
            // SelectionChanged and the changed-handler previews it.
            if (!ReferenceEquals(RevLightEffectCombo.SelectedItem, sender)) return;
            _plugin.PreviewRevLightPattern();
        }

        /// <summary>Shared by both rows' Remember buttons: store the pattern
        /// the row's combo currently shows for the active car.</summary>
        private void RevLightRemember_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            var combo = ReferenceEquals(sender, CarRevLightRememberBtn)
                ? CarRevLightCombo : RevLightEffectCombo;
            if (!((combo?.SelectedItem as ComboBoxItem)?.Tag is int v) || v < 1) return;
            _plugin.RememberPatternForActiveCar(v);
            RefreshRevLightPicker();
            RefreshCarRevLightRow();
        }

        /// <summary>Shared by both rows' Forget links. Deliberately leaves
        /// the wheel's current selection alone: removing automation must
        /// not yank the lights.</summary>
        private void RevLightForget_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            _plugin.ForgetPatternForActiveCar();
            RefreshRevLightPicker();
            RefreshCarRevLightRow();
        }

        private void RevLightEffect_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var picked = RevLightEffectCombo.SelectedItem as ComboBoxItem;
            if (!(picked?.Tag is int v)) return;
            // Sets the wheelbase's selection, exactly like the base's own
            // menu (staged with a log line while a game's own FFB holds the
            // pipe). Then one preview cycle wherever that is safe.
            _plugin.PickRevLightPattern(v);
            RefreshCarRevLightRow();
            SyncRememberControls(RevLightEffectCombo, RevLightRememberBtn,
                RevLightRememberStatus, RevLightRememberRun);
            _plugin.PreviewRevLightPattern();
        }

        // ---- Wheel-base Dynamic OLED (experimental) ----------------------
        // Mirrors the rev-light handlers: the screen shares that HID++ pipe and
        // its Mode B gate, so turning it off must hand the panel back at once
        // rather than leaving the last frame frozen on the wheel.

        private void ModeBOled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.ModeBOledEnabled = ModeBOledCheck.IsChecked == true;
            _plugin.PersistSettings();
            if (!_plugin.Settings.ModeBOledEnabled) _plugin.TurnOffOled();
        }

        private void OledScreen_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var picked = OledScreenCombo.SelectedItem as ComboBoxItem;
            if (!(picked?.Tag is OledScreen chosen)) return;
            var s = _plugin.Settings;
            var was = s.OledScreen;
            s.OledScreen = chosen;
            // First time into the editor, start from the screen they were just
            // on instead of four empty slots. Nobody wants to build a screen
            // from nothing, and a blank panel on the wheel looks like a bug.
            if (s.OledScreen == OledScreen.Custom && was != OledScreen.Custom
                && (s.OledCustomSlots == null || s.OledCustomSlots.Count == 0))
            {
                OledScreenModel.Preset(was, s.OledUseMph, deltaOk: true,
                                       out var kind, out var slots, out var texts);
                s.OledCustomLayout = kind;
                s.OledCustomSlots = new List<string>(slots);
                s.OledCustomTexts = new List<string>(
                    OledScreenModel.SanitizeTexts(texts, OledScreenModel.MaxSlots));
            }
            _plugin.PersistSettings();
            RefreshOledEditor();
            PreviewOledNow();
        }

        private ComboBox[] OledSlotCombos() => new[]
            { OledSlot0Combo, OledSlot1Combo, OledSlot2Combo, OledSlot3Combo };
        private TextBox[] OledSlotTexts() => new[]
            { OledSlot0Text, OledSlot1Text, OledSlot2Text, OledSlot3Text };
        private TextBlock[] OledSlotLabels() => new[]
            { OledSlot0Label, OledSlot1Label, OledSlot2Label, OledSlot3Label };
        private UIElement[] OledSlotRows() => new UIElement[]
            { OledSlot0Row, OledSlot1Row, OledSlot2Row, OledSlot3Row };

        /// <summary>Show the custom editor only for "Build my own", size it to
        /// the chosen layout's slot count, and put each slot's fixed size and
        /// capacity in its label. The firmware owns all of that, so stating it
        /// is the only way someone can plan a screen instead of guessing and
        /// watching text get cut off on the wheel.</summary>
        private void RefreshOledEditor()
        {
            if (OledCustomPanel == null || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            bool custom = s.OledScreen == OledScreen.Custom;
            OledCustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            bool prevSuppress = _suppressEvents;
            _suppressEvents = true;
            try
            {
                if (OledScreenCombo != null && OledScreenCombo.Items.Count == 0)
                    for (int k = 0; k < OledScreenModel.ScreenOrder.Length; k++)
                        OledScreenCombo.Items.Add(new ComboBoxItem
                        {
                            Content = OledScreenModel.ScreenOrderLabels[k],
                            Tag = OledScreenModel.ScreenOrder[k],
                        });
                if (OledScreenCombo != null)
                {
                    OledScreenCombo.SelectedItem = null;
                    foreach (ComboBoxItem it in OledScreenCombo.Items)
                        if (it.Tag is OledScreen sc && sc == s.OledScreen)
                        { OledScreenCombo.SelectedItem = it; break; }
                    // A screen that was retired is still a valid stored value
                    // but is no longer in the list, which would leave the box
                    // blank and the user unable to see what they are on. Move
                    // them to the first offered screen instead of pretending.
                    if (OledScreenCombo.SelectedItem == null && OledScreenCombo.Items.Count > 0)
                    {
                        OledScreenCombo.SelectedIndex = 0;
                        s.OledScreen = OledScreenModel.ScreenOrder[0];
                        _plugin.PersistSettings();
                    }
                }

                if (OledLayoutCombo != null && OledLayoutCombo.Items.Count == 0)
                    foreach (string lbl in OledScreenModel.LayoutLabels)
                        OledLayoutCombo.Items.Add(lbl);
                int li = Array.IndexOf(OledScreenModel.LayoutKinds, s.OledCustomLayout);
                if (OledLayoutCombo != null) OledLayoutCombo.SelectedIndex = li < 0 ? 0 : li;

                var kind = s.OledCustomLayout;
                int n = OledScreenModel.SlotCount(kind);
                var slots = OledScreenModel.SanitizeSlots(s.OledCustomSlots, kind);
                var texts = OledScreenModel.SanitizeTexts(s.OledCustomTexts, OledScreenModel.MaxSlots);

                var combos = OledSlotCombos();
                var boxes = OledSlotTexts();
                var labels = OledSlotLabels();
                var rows = OledSlotRows();
                for (int i = 0; i < OledScreenModel.MaxSlots; i++)
                {
                    if (rows[i] != null)
                        rows[i].Visibility = i < n ? Visibility.Visible : Visibility.Collapsed;
                    if (i >= n) continue;

                    if (labels[i] != null)
                        labels[i].Text = $"Slot {i + 1} ({OledScreenModel.SlotHint(kind, i)}):";

                    // A meter slot offers meters, a text slot offers text
                    // fields. Rebuilt whenever the slot changes kind, which a
                    // layout change can do underneath a given slot number.
                    bool isGauge = OledScreenModel.SlotIsGauge(kind, i);
                    string[] fieldKeys   = isGauge ? OledScreenModel.GaugeFieldKeys   : OledScreenModel.FieldKeys;
                    string[] fieldLabels = isGauge ? OledScreenModel.GaugeFieldLabels : OledScreenModel.FieldLabels;

                    var cb = combos[i];
                    if (cb != null)
                    {
                        bool rebuild = cb.Items.Count != fieldKeys.Length
                            || !(cb.Items.Count > 0
                                 && ((cb.Items[0] as ComboBoxItem)?.Tag as string) == fieldKeys[0]);
                        if (rebuild)
                        {
                            cb.Items.Clear();
                            for (int k = 0; k < fieldKeys.Length; k++)
                                cb.Items.Add(new ComboBoxItem { Content = fieldLabels[k], Tag = fieldKeys[k] });
                        }
                        cb.SelectedItem = null;
                        foreach (ComboBoxItem it in cb.Items)
                            if ((it.Tag as string) == slots[i]) { cb.SelectedItem = it; break; }
                    }

                    if (boxes[i] != null)
                    {
                        // Custom text only, and never on a meter.
                        boxes[i].Visibility = (!isGauge && slots[i] == OledScreenModel.FieldCustom)
                            ? Visibility.Visible : Visibility.Collapsed;
                        boxes[i].Text = texts[i] ?? "";
                        int wid = OledScreenModel.SlotWidths(kind)[i];
                        boxes[i].MaxLength = wid > 0 ? wid : 1;
                    }
                }
            }
            finally { _suppressEvents = prevSuppress; }

            RefreshOledDeltaNotice();
        }

        /// <summary>Draw whatever the editor currently describes on the wheel,
        /// so a layout can be judged where it will be read rather than guessed
        /// at from a dropdown. Silent when the channel is not up yet; explains
        /// itself when a game is holding the pipe, since a preview that just
        /// does nothing looks broken.</summary>
        private void PreviewOledNow()
        {
            if (_plugin?.Settings == null) return;
            if (_plugin.Settings.ModeBOledEnabled != true) return;
            int ms = _plugin.PreviewOledScreen();
            if (ms >= 0 || OledStatusText == null) return;
            OledStatusText.Text = "preview held back: telemetry is arriving, so a game may be sending "
                + "its own force feedback and writing the screen now could cut it. Close the game, or "
                + "turn on Telemetry Based FFB, and the preview works.";
        }

        /// <summary>Say so when the running game never reports a lap delta,
        /// rather than letting a delta screen sit blank with no explanation.
        /// Same evidence the dash's box picker greys "Lap times" out with.</summary>
        private void RefreshOledDeltaNotice()
        {
            if (OledDeltaUnavailableText == null || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            bool wantsDelta =
                s.OledScreen == OledScreen.SpeedAndDelta ||
                s.OledScreen == OledScreen.SpeedGearAndDelta ||
                (s.OledScreen == OledScreen.Custom && s.OledCustomSlots != null
                 && s.OledCustomSlots.Contains(OledScreenModel.FieldDelta));

            bool show = wantsDelta && !_plugin.GameReportsLapDelta;
            OledDeltaUnavailableText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
                OledDeltaUnavailableText.Text =
                    (_plugin.ActiveGame ?? "This game") + " does not report a lap delta, so that "
                    + "part of the screen stays empty. The rest still works.";
        }

        private void OledLayout_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int i = OledLayoutCombo.SelectedIndex;
            if (i < 0 || i >= OledScreenModel.LayoutKinds.Length) return;
            _plugin.Settings.OledCustomLayout = OledScreenModel.LayoutKinds[i];
            _plugin.PersistSettings();
            RefreshOledEditor();
            PreviewOledNow();
        }

        private void OledSlot_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            var combos = OledSlotCombos();
            int n = OledScreenModel.SlotCount(s.OledCustomLayout);
            var slots = OledScreenModel.SanitizeSlots(s.OledCustomSlots, s.OledCustomLayout);
            for (int i = 0; i < n; i++)
            {
                var it = combos[i]?.SelectedItem as ComboBoxItem;
                if (it?.Tag is string key) slots[i] = key;
            }
            s.OledCustomSlots = new List<string>(slots);
            _plugin.PersistSettings();
            RefreshOledEditor();
            PreviewOledNow();
        }

        private void OledSlotText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var s = _plugin.Settings;
            var boxes = OledSlotTexts();
            var texts = OledScreenModel.SanitizeTexts(s.OledCustomTexts, OledScreenModel.MaxSlots);
            for (int i = 0; i < OledScreenModel.MaxSlots; i++)
                if (boxes[i] != null) texts[i] = boxes[i].Text ?? "";
            s.OledCustomTexts = new List<string>(texts);
            _plugin.PersistSettings();
            PreviewOledNow();
            // Deliberately no RefreshOledEditor here: rebuilding the boxes while
            // someone is typing in one of them would move the caret.
        }

        private void OledShiftFlash_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.OledShiftFlash = OledShiftFlashCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void OledFlashStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            int i = OledFlashStyleCombo.SelectedIndex;
            if (i < 0) return;
            _plugin.Settings.OledShiftFlashStyle = (OledFlashStyle)i;
            _plugin.PersistSettings();
        }

        private void OledGreeting_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.OledGreetingEnabled = OledGreetingCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void OledGreetingText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.OledGreetingText = OledGreetingBox.Text ?? "";
            _plugin.PersistSettings();
        }

        private void OledLapResult_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.OledLapResult = OledLapResultCheck.IsChecked == true;
            _plugin.PersistSettings();
        }

        private void OledMph_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            _plugin.Settings.OledUseMph = OledMphCheck.IsChecked == true;
            _plugin.PersistSettings();
            PreviewOledNow();
        }

        // The OLED sample sequence and the layout report used to be buttons in
        // the settings section. They are diagnostics, not settings, and the
        // section had grown too long to spend two controls on them, so they
        // moved to access codes (OLEDTEST / OLEDDIAG). The code behind them is
        // unchanged and still worth having: the report is how this wheel's
        // layout table was read in the first place.

        /// <summary>Live-poll the controller's status while a sequence runs, so
        /// a wheel that never answers says so in the panel and not only in the
        /// log, and so each step names itself while it is on screen.</summary>
        private void PollOledStatus()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            int idleTicks = 0;
            t.Tick += (s2, e2) =>
            {
                if (OledStatusText != null) OledStatusText.Text = _plugin.OledStatus;
                if (_plugin.OledIsTesting) idleTicks = 0;
                else if (++idleTicks > 4) t.Stop();   // ~1s after it ends
            };
            t.Start();
        }

        private void RpmLedTest_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.TestRpmLeds();
            // Live-poll the controller's status so the current effect mode is
            // visible in the panel while the sweep runs (the log timing is
            // hard to eyeball against the wheel). Stop a moment after the
            // test ends so the final "LEDs off" line shows.
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            int idleTicks = 0;
            t.Tick += (s2, e2) =>
            {
                if (RpmLedStatusText != null) RpmLedStatusText.Text = _plugin.RpmLedStatus;
                if (_plugin.RpmLedIsTesting) idleTicks = 0;
                else if (++idleTicks > 4) t.Stop();   // ~1s after test ends
            };
            t.Start();
        }

        private void ForzaPort_LostFocus(object sender, RoutedEventArgs e) => CommitForzaPort();
        private void ForzaPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaPort();
        }
        private void CommitForzaPort()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaPortBox.Text?.Trim();
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.Forza.Port != port)
                {
                    _plugin.Settings.Forza.Port = port;
                    _plugin.ApplyForzaSettings();
                }
            }
            else
            {
                // Reject invalid input by snapping back to the saved value.
                ForzaPortBox.Text = _plugin.Settings.Forza.Port.ToString();
            }
        }

        private void ForzaBind_LostFocus(object sender, RoutedEventArgs e) => CommitForzaBind();
        private void ForzaBind_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaBind();
        }
        private void CommitForzaBind()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaBindBox.Text?.Trim() ?? "";
            // Empty / blank → 0.0.0.0 (any). Garbage stays accepted but the
            // plugin's parser falls back to Any too, so it's consistent.
            if (string.IsNullOrWhiteSpace(raw)) raw = "0.0.0.0";
            if (_plugin.Settings.Forza.BindAddress != raw)
            {
                _plugin.Settings.Forza.BindAddress = raw;
                _plugin.ApplyForzaSettings();
            }
        }

        // ---- Port discovery banner handlers ----
        // The plugin exposes a single DiscoveredAlternatePort and
        // AdoptDiscoveredAlternatePort handles it based on the active source type.
        private void ForzaDiscoveryAdopt_Click(object sender, RoutedEventArgs e)
            => _plugin?.AdoptDiscoveredAlternatePort();
        private void ForzaDiscoveryDismiss_Click(object sender, RoutedEventArgs e)
            => _plugin?.DismissDiscoveredAlternatePort();

        private void ForzaForwardEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            _plugin.Settings.Forza.ForwardEnabled = ForzaForwardEnabledCheck.IsChecked == true;
            _plugin.ApplyForzaSettings();
        }

        private void ForzaForwardHost_LostFocus(object sender, RoutedEventArgs e) => CommitForzaForwardHost();
        private void ForzaForwardHost_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaForwardHost();
        }
        private void CommitForzaForwardHost()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaForwardHostBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) raw = "127.0.0.1";
            if (_plugin.Settings.Forza.ForwardHost != raw)
            {
                _plugin.Settings.Forza.ForwardHost = raw;
                _plugin.ApplyForzaSettings();
            }
        }

        private void ForzaForwardPort_LostFocus(object sender, RoutedEventArgs e) => CommitForzaForwardPort();
        private void ForzaForwardPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CommitForzaForwardPort();
        }
        private void CommitForzaForwardPort()
        {
            if (_suppressEvents || _plugin?.Settings?.Forza == null) return;
            string raw = ForzaForwardPortBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Blank = clear / disable target. Listener stays open but
                // forwarder no-ops (BuildForzaForwardEndpoint returns null).
                if (_plugin.Settings.Forza.ForwardPort != 0)
                {
                    _plugin.Settings.Forza.ForwardPort = 0;
                    _plugin.ApplyForzaSettings();
                }
                return;
            }
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535)
            {
                if (_plugin.Settings.Forza.ForwardPort != port)
                {
                    _plugin.Settings.Forza.ForwardPort = port;
                    _plugin.ApplyForzaSettings();
                }
            }
            else
            {
                // Snap back to saved value on invalid input so the textbox
                // doesn't keep showing user's typo.
                ForzaForwardPortBox.Text = _plugin.Settings.Forza.ForwardPort > 0
                    ? _plugin.Settings.Forza.ForwardPort.ToString()
                    : "";
            }
        }

        // Throttle for the engine readout block in the meter tick (see the
        // EngineLayoutAutoText block): readout + combo re-sync run at 4 Hz.
        private DateTime _engineReadoutNextTick = DateTime.MinValue;

        // ---------- Per-effect Save popover ----------

        private void EffectSave_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (!(sender is System.Windows.Controls.Button b) || !(b.Tag is string tag)) return;
            if (!Enum.TryParse<EffectKind>(tag, out var which)) return;
            // During car-edit the loaded game preset is only a temporary baseline,
            // so the car override is the one valid save target. Save this section
            // straight to the car instead of offering the game-scope popover.
            // Global-only sections have no car slot: routing them here dead-ends
            // in the override machinery's default cases ("Couldn't save"), so
            // they keep their normal game-preset save even mid car-edit.
            if (_plugin.IsOfflineEditingCar && SectionHasCarScope(which)) { ApplyEffectSaveForCar(which); return; }
            // (The engine-layout-only fast path was removed with the 2026-07
            // centralization: the engine type lives in Car facts and can no
            // longer dirty the Engine section.)
            ShowEffectSavePopover(which);
        }

        private void EffectRevert_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (!(sender is System.Windows.Controls.Button b) || !(b.Tag is string tag)) return;
            if (!Enum.TryParse<EffectKind>(tag, out var which)) return;
            string activeP = _plugin.ActivePresetName;
            if (string.IsNullOrEmpty(activeP)) return;  // nothing to revert to
            string label = EffectLabel(which);
            // No confirm: the button only appears while that section is dirty,
            // it says Revert, and it discards exactly the unsaved changes the
            // dirty marker is already showing. A modal on every revert was
            // friction with nothing to protect.
            // Car-scoped sections: revert discards the unsaved per-car DRAFT,
            // restoring this car's saved override (or the game default if none),
            // rather than reverting the global section to the active preset.
            bool reverted;
            if (SectionUsesDraftModel(which) && !string.IsNullOrEmpty(_plugin.ActiveCarId))
            {
                _plugin.RevertSectionDraft((TrueforcePlugin.SectionKind)(int)which);
                reverted = true;
            }
            else
            {
                // EffectKind values mirror SectionKind, so pass through directly.
                reverted = _plugin.RevertSection((TrueforcePlugin.SectionKind)(int)which);
            }
            if (reverted)
            {
                ClearEffectDirty(which);
                // Global ★ unsaved indicator: clear only when ALL sections
                // are now clean.
                bool anyDirty = false;
                for (int i = 0; i < _effectDirty.Length; i++) if (_effectDirty[i]) { anyDirty = true; break; }
                if (!anyDirty) ClearDirty();
                RefreshFromPlugin();
            }
        }

        /// <summary>Per-effect Save popover, simplified 2026-07-22 (owner
        /// call: the adaptive five-way stack read as a wall). Exactly three
        /// choices, matching the dash's save chooser: This car only / Game
        /// preset / Both, with detail in tooltips. Reset-to-default stays
        /// reachable as a quiet link (this modal is its only entrance) since
        /// it is a different verb than saving. With no car detected (or a
        /// section with no per-car concept) there is no choice to make, so
        /// the popover is skipped and the save goes straight to the game
        /// preset.</summary>
        private void ShowEffectSavePopover(EffectKind which)
        {
            string carId       = _plugin.ActiveCarId;
            bool   carDetected = !string.IsNullOrEmpty(carId);
            string activeP     = _plugin.ActivePresetName;
            bool   hasPreset   = !string.IsNullOrEmpty(activeP);
            bool   builtin     = hasPreset && _plugin.IsBuiltinPreset(activeP);
            string label       = EffectLabel(which);
            bool   carScope    = SectionHasCarScope(which);

            if (!carScope || !carDetected)
            {
                SaveEffectSectionToGamePreset(which);
                return;
            }

            var win = new Window
            {
                Title  = $"Save {label}",
                Width  = 420,
                // Grow to fit however many choice buttons this section shows.
                // A fixed height clipped the bottom option + the Cancel row
                // whenever a section offered 3+ saves (e.g. RevLimiter: For
                // this car / Both / Reset to default / Update game defaults).
                SizeToContent = SizeToContent.Height,
                MinHeight     = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode    = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Save current {label} settings to…",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var kind = (TrueforcePlugin.SectionKind)(int)which;
            bool hasGame = !string.IsNullOrEmpty(_plugin.ActiveGame);

            // Exactly three choices; tooltips carry the contextual detail
            // the old per-option explainer paragraphs spelled out inline.
            var carBtn = new Button
            {
                Content = "This car only",
                Height = 32, Margin = new Thickness(0, 0, 0, 6),
                ToolTip = $"Saves the current {label} values into this car's preset ({carId}). "
                    + "Other cars and the game preset are untouched. On a built-in car preset, saves a copy as a new user preset.",
            };
            sp.Children.Add(carBtn);

            string presetTip = !hasPreset
                ? (hasGame
                    ? "No preset is active: saves your tuning as a new preset and makes it this game's default."
                    : "No preset is active: saves your tuning as a new preset.")
                : builtin
                    ? $"'{activeP}' is built-in and can't be overwritten: saves a copy as a new user preset and makes it this game's default."
                    : $"Overwrites '{activeP}' with the current {label} values. Cars follow it unless they have their own override.";
            var presetBtn = new Button
            {
                Content = "Game preset",
                Height = 32, Margin = new Thickness(0, 0, 0, 6),
                ToolTip = presetTip,
            };
            sp.Children.Add(presetBtn);

            var bothBtn = new Button
            {
                Content = "Both",
                Height = 32, Margin = new Thickness(0, 0, 0, 12),
                ToolTip = "Saves to the game preset AND pins this car with its own copy, "
                    + "so the car keeps these values even if the game preset changes later.",
            };
            sp.Children.Add(bothBtn);

            // Contextual "recommended" highlight (green): point at the save that
            // fits the current setup instead of always Game preset.
            //   no car preset bound                 -> Game preset
            //   effect already in the car's preset  -> This car only
            //   car preset bound, effect not yet in it -> Both
            var greenStyle = TryFindResource("PopoverPrimaryButton") as Style;
            bool carPresetActive = !string.IsNullOrEmpty(_plugin.GetActiveCarPresetName(carId));
            if (!carPresetActive)
                presetBtn.Style = greenStyle;
            else if (_plugin.IsSectionInSavedCarOverride(kind))
                carBtn.Style = greenStyle;
            else
                bothBtn.Style = greenStyle;

            // No reset-to-default entry here (owner call 2026-07-22): setting
            // the car's preset to None in the picker covers "follow the game
            // default", so the per-section reset link only added noise.

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);

            win.Content = sp;

            carBtn.Click += (s, args) =>
            {
                ApplyEffectSaveForCar(which);
                win.DialogResult = true;
            };
            presetBtn.Click += (s, args) =>
            {
                if (!SaveEffectSectionToGamePreset(which)) return;   // keep open + dirty on a failed write
                win.DialogResult = true;
            };
            bothBtn.Click += (s, args) =>
            {
                // Game half first (may fork with a naming prompt), then re-pin
                // the car copy and run the car half, which itself forks on a
                // built-in car preset. The snapshot runs AFTER the preset save
                // so SavePresetAs's draft fold can't empty the car copy (the
                // dash's BOTH had exactly that bug); this also replaces the
                // old SaveSectionToBoth call, whose either-half-OK return hid
                // car-half failures on factory car presets.
                if (!SaveEffectSectionToGamePreset(which)) return;
                _plugin.SnapshotSectionToCarOverride(kind);
                ApplyEffectSaveForCar(which);
                win.DialogResult = true;
            };

            win.ShowDialog();
        }

        /// <summary>Release the car layer's claim on a section after a game-side
        /// save, surfacing the one car-file write that used to be swallowed: if
        /// patching the bound car preset to follow the new game default fails
        /// (e.g. a read-only car folder), warn instead of reporting a clean save,
        /// because the car would otherwise silently revert on the next load.</summary>
        private void ReleaseCarSectionOrWarn(TrueforcePlugin.SectionKind kind)
        {
            if (!_plugin.ReleaseSectionFromCarLayer(kind))
                TrueforceDialog.Show(null, "Trueforce For All",
                    "Saved to the game preset, but couldn't update this car's own preset to follow it, so this car may revert to its previous value after a restart. See the SimHub log for details.",
                    DialogKind.Warning);
        }

        /// <summary>Save one section into the active game preset: in place on
        /// a user preset; fork (with the naming prompt) when the active
        /// preset is built-in or absent. Shared by the save popover's Game
        /// preset / Both choices and by the no-car path that skips the
        /// popover entirely. Returns false on a failed in-place write, a
        /// failed fork, or a cancelled name prompt (callers keep the popover
        /// open and the dirty bit set, and the Both flow skips the car
        /// half).</summary>
        private bool SaveEffectSectionToGamePreset(EffectKind which)
        {
            string activeP   = _plugin.ActivePresetName;
            bool   hasPreset = !string.IsNullOrEmpty(activeP);
            bool   builtin   = hasPreset && _plugin.IsBuiltinPreset(activeP);
            var    kind      = (TrueforcePlugin.SectionKind)(int)which;

            // Two-phase promote around every branch: the copy half runs
            // before the preset write (so the snapshot sees the section's
            // live values), but the destructive half - releasing the car
            // layer's claim, which patches the SAVED car preset file - only
            // runs after the write is CONFIRMED. A cancel or failure used to
            // leave the car file already stripped (2026-07 audit blocker).
            //
            // Fork paths (no preset / built-in) always capture whole state --
            // a freshly-forked preset IS the snapshot.
            if (!hasPreset)
            {
                _plugin.CopySectionToGlobals(kind);
                if (!SaveAsNewPresetFromUi()) return false;
                ReleaseCarSectionOrWarn(kind);
                // The fork's own refresh ran BEFORE the release and can
                // re-light this effect against the pre-release baseline;
                // recompute now that the baseline is settled.
                ClearEffectDirty(which);
                RefreshFromPlugin();
                return true;
            }
            if (builtin)
            {
                _plugin.CopySectionToGlobals(kind);
                if (!ForkAndSaveAsGamePreset()) return false;
                ReleaseCarSectionOrWarn(kind);
                ClearEffectDirty(which);
                RefreshFromPlugin();
                return true;
            }

            // Per-section save: patch ONLY this section into the active
            // preset; other sections keep their saved values and their dirty
            // bits remain (the ★ Save all button commits everything at once).
            _plugin.CopySectionToGlobals(kind);
            if (!_plugin.SaveSectionToActivePreset(kind))
            {
                TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                return false;
            }
            ReleaseCarSectionOrWarn(kind);
            ClearEffectDirty(which);
            RefreshFromPlugin();
            return true;
        }

        /// <summary>Per-car save for one effect: writes the section's
        /// current values to the active car preset's file. Forks to a new
        /// user preset (whole-state) when on a built-in / no preset yet.
        /// On a user car preset, asks the user whether to save just this
        /// section or every dirty section.</summary>
        private void ApplyEffectSaveForCar(EffectKind which)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            bool   onBuiltin  = !string.IsNullOrEmpty(activeName)
                                && _plugin.IsCarPresetBuiltin(carId, activeName);
            // DEV authoring: built-ins save in place (the plugin write-throughs
            // to the factory folder). Non-dev: fork to a new user preset.
            bool   isFork     = string.IsNullOrEmpty(activeName)
                                || (onBuiltin && !_plugin.DevMode);

            if (isFork)
            {
                // Fork to a new user car preset: whole-state. The new file
                // captures every section the user has tuned, since the
                // preset IS the snapshot of the user's intent at fork time.
                _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)(int)which);
                // Strip the " (Built-In)" suffix from the suggested name
                // (NOT " (default)" - that's the legacy preset-default
                // suffix, different concept). Without this the suggestion
                // includes "(Built-In)" and collides with the merged-dict
                // key for the factory entry, so silentOk always falls
                // false and the rename prompt fires.
                // Pre-fill rules:
                //   • Forking a built-in: strip the " (Built-In)" suffix off
                //     the built-in's own name so the suggestion doesn't
                //     immediately collide with the factory entry.
                //   • Fresh fork (no built-in basis): prefer the active car's
                //     human display name (resolved from CarFacts CarName ->
                //     community consensus -> baked name tables) so Forza
                //     ordinals like "Car_2267" pre-fill as "2016 Mazda MX-5"
                //     instead. Falls back to carId only when no name has
                //     been resolved for this car yet.
                string fallbackName = !string.IsNullOrEmpty(_plugin.ActiveCarDisplayName)
                    ? _plugin.ActiveCarDisplayName : carId;
                string suggestion = onBuiltin ? TrueforcePlugin.ToDiskName(activeName) : fallbackName;
                // A factory default's stripped name ("Car_455 (default)")
                // is still the factory DISK name: a user file saved under
                // it is shadowed by the builtin merge (invisible in the
                // dropdown, inert after restart). Suggest the car's
                // display name instead.
                if (_plugin.IsCarPresetBuiltin(carId, suggestion)) suggestion = fallbackName;
                var existing = _plugin.GetCarPresets(carId);
                // Taken name: append (n) instead of falling back to the
                // type-a-name prompt (owner call 2026-07-22). The prompt
                // only remains for the no-suggestion edge.
                if (!string.IsNullOrEmpty(suggestion))
                {
                    string baseSuggestion = suggestion;
                    int n = 2;
                    while ((existing != null && existing.ContainsKey(suggestion))
                           || _plugin.IsCarPresetBuiltin(carId, suggestion))
                        suggestion = $"{baseSuggestion} ({n++})";
                }
                bool silentOk = !string.IsNullOrEmpty(suggestion);
                if (silentOk)
                {
                    if (!_plugin.SaveActiveCarPresetAs(suggestion))
                    {
                        TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                        return;
                    }
                }
                else
                {
                    string newName = PromptForCarPresetName(
                        title: "Save as new car preset",
                        body: onBuiltin
                            ? $"'{activeName}' is a built-in default. Save the current tuning as a new user preset for '{carId}':"
                            : $"Save the current tuning as a new user preset for '{carId}':",
                        initial: suggestion,
                        existing: existing);
                    if (string.IsNullOrEmpty(newName)) return;
                    if (!_plugin.SaveActiveCarPresetAs(newName))
                    {
                        TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                        return;
                    }
                }
            }
            else
            {
                // Overwrite existing user car preset: save ONLY this section.
                // The ★ Save all button handles every dirty section, so no
                // scope prompt here. Patch just the targeted section into the
                // on-disk override; other sections keep their saved values and
                // their dirty bits persist after RecomputeAllEffectDirty.
                _plugin.SnapshotSectionToCarOverride((TrueforcePlugin.SectionKind)(int)which);
                bool ok = _plugin.SaveSectionToActiveCarOverride(
                    (TrueforcePlugin.SectionKind)(int)which);
                if (!ok)
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Info);
                    return;
                }
            }
            RefreshFromPlugin();
            // Tell the Preset Manager the local library just changed so its
            // rows reflect the new / updated car preset without the user
            // having to hit Refresh library by hand.
            _presetManager?.OnLocalLibraryChanged();
            // Prompt only where the saved values still carry a per-car fact
            // (RevLimiter -> redline). The Engine hook was removed with the
            // 2026-07 centralization: the engine pick lives in Car facts and
            // submits from CommitEnginePin, so an Engine-section save is feel
            // only and must not re-submit the pin.
            if (which == EffectKind.RevLimiter) MaybePromptToSubmitRedlineData(carId);
        }

        /// <summary>Save current full state as a new named preset (same flow
        /// as the existing "Save as new" preset library button).</summary>
        // Returns false when the user cancels the prompt / declines the
        // overwrite, or the save fails - callers that stage destructive
        // follow-ups (the popover's release of a car section) must not run
        // them on a cancel that saved nothing.
        private bool SaveAsNewPresetFromUi()
        {
            string suggested = _plugin.ActiveGame ?? "My preset";
            string name = PromptForName("Save as new preset", "Preset name:", suggested);
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            // Confirm overwrite if the name collides.
            bool exists = false;
            if (_plugin.PresetNames != null)
                foreach (var n in _plugin.PresetNames) { if (n == name) { exists = true; break; } }
            if (exists && TrueforceDialog.Show(Window.GetWindow(this),
                    "Overwrite preset?",
                    $"A preset called '{name}' already exists. Overwrite?",
                    DialogKind.Confirm) != true)
                return false;
            bool reused = false;
            if (!_plugin.SavePresetAs(name))
            {
                // Duplicate-content refusal reuses the identical preset
                // instead of dead-ending (parity with ForkAndSaveAsGamePreset
                // and the dash). Any other failure is a real error.
                string dup = ReuseDuplicateOrNull();
                if (dup == null)
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                    return false;
                }
                name   = dup;
                reused = true;
            }
            // Bind it as this game's default so the save actually sticks across
            // sessions (a "save to game defaults" that didn't bind would leave
            // the preset orphaned and not auto-load next time).
            string game = _plugin.ActiveGame;
            if (!string.IsNullOrEmpty(game))
                _plugin.SetDefaultPresetForActiveGame(name);
            ClearDirty();
            RefreshFromPlugin();
            // Tell the Preset Manager the local library just changed so the
            // newly-saved game preset shows up in its list automatically.
            _presetManager?.OnLocalLibraryChanged();
            FlashSaveStatus(HeaderGameSaveStatus, reused
                ? $"Same as '{name}', now active ✓"
                : $"Saved as '{name}' ✓");
            return true;
        }

        // When SavePresetAs refused because the current tuning is content-
        // identical to an existing preset (LastLocalDuplicateName set), switch
        // to that preset - preserving the user's personal FFB scale, which is
        // excluded from the identity hash and must not be yanked by a save -
        // instead of dead-ending. Returns the reused preset's name, or null
        // when the refusal was not a duplicate one (caller shows the error).
        private string ReuseDuplicateOrNull()
        {
            string dup = _plugin.LastLocalDuplicateName;
            if (string.IsNullOrEmpty(dup)) return null;
            return _plugin.ApplyPresetKeepingPersonalFfb(dup) ? dup : null;
        }

        // ---------- Preset library ----------

        private void RefreshPresetSection()
        {
            if (_plugin == null) return;
            // The active-preset dropdowns live in the header context card;
            // rebuilt by RefreshGamePresetPicker / RefreshCarPresetPicker.
            // The old conditional "Set as default" button is gone: picking a
            // preset while a game is active now binds it in the same step.
            UpdateHeaderPresetDisplay();
        }

        // The old global "Save preset" chooser (save to this car / game default
        // / both, in one modal) was removed: the two header Save buttons now
        // save directly to their own scope (see HeaderGameSaveAll_Click /
        // HeaderCarSaveAll_Click), which is clearer than routing both through a
        // three-option popover.

        // Save all dirty tuning to the active game-default preset: forks from a
        // built-in / creates one when there's none, else overwrites in place.
        // Shows a confirmation. The popover choice is the user's intent, so no
        // extra "overwrite?" prompt. Shared by the global Save popover.
        // bindAsDefault: only the explicit "Both" path passes true. A plain
        // game-side save overwrites the active preset file and leaves the
        // auto-load binding alone, so saving an edit never silently changes
        // which preset this game loads (that's the "Set as default" button's
        // job). The fork branch below establishes its own binding.
        // Transient "Saved" confirmation shown inline next to the header Save
        // buttons, replacing the old blocking success dialogs (audit A2 — a
        // one-click Save shouldn't interrupt with a modal you must dismiss).
        // Only one save happens at a time, so a single shared timer clears
        // whichever label is showing.
        // Siblings (deliberately separate semantics, don't merge): ShowSavedStatus
        // (persistent message + "Show in folder" link) and ClearStatusAfter
        // (generic per-label timed clear with optional fade).
        private System.Windows.Threading.DispatcherTimer _saveStatusTimer;
        private void FlashSaveStatus(TextBlock target, string msg)
        {
            if (HeaderGameSaveStatus != null) HeaderGameSaveStatus.Text = "";
            if (HeaderCarSaveStatus  != null) HeaderCarSaveStatus.Text  = "";
            if (target == null) return;
            target.Text = msg;
            _saveStatusTimer?.Stop();
            _saveStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _saveStatusTimer.Tick += (_, __) =>
            {
                _saveStatusTimer.Stop();
                if (HeaderGameSaveStatus != null) HeaderGameSaveStatus.Text = "";
                if (HeaderCarSaveStatus  != null) HeaderCarSaveStatus.Text  = "";
            };
            _saveStatusTimer.Start();
        }

        private void SaveAllGameDefaults(bool bindAsDefault = false)
        {
            string activeP = _plugin.ActivePresetName;
            bool   builtin = !string.IsNullOrEmpty(activeP) && _plugin.IsBuiltinPreset(activeP);
            if (string.IsNullOrEmpty(activeP) || builtin)
            {
                ForkAndSaveAsGamePreset();   // shows its own "Saved as…" confirmation
                return;
            }
            if (!_plugin.SavePresetAs(activeP))
            {
                TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                return;
            }
            if (bindAsDefault && !string.IsNullOrEmpty(_plugin.ActiveGame))
                _plugin.SetDefaultPresetForActiveGame(activeP);
            bool isDefault = !string.IsNullOrEmpty(_plugin.ActiveGame)
                && string.Equals(activeP, _plugin.DefaultPresetForActiveGame, StringComparison.Ordinal);
            ClearDirty();
            RefreshFromPlugin();
            _presetManager?.OnLocalLibraryChanged();
            FlashSaveStatus(HeaderGameSaveStatus, isDefault ? "Saved as game default ✓" : "Saved ✓");
        }

        // Save all dirty car-scoped tuning to the active car's override (forks a
        // user car preset from a built-in if needed). Flashes an inline confirm.
        private void SaveAllForCar()
        {
            if (!SaveActiveCarPresetWithFork()) return;   // user cancelled fork prompt
            RecomputeAllEffectDirty();
            RefreshFromPlugin();
            FlashSaveStatus(HeaderCarSaveStatus, "Saved ✓");
        }

        /// <summary>Save the live car override to its active preset file.
        /// On a built-in, prompts for a new user-preset name and forks; on
        /// a user preset, in-place save. Returns false if the user
        /// cancelled the fork prompt (caller should abort the save chain).
        /// Returns true if save succeeded or there was nothing to save.</summary>
        private bool SaveActiveCarPresetWithFork()
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return true;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            bool   onBuiltin  = !string.IsNullOrEmpty(activeName)
                                && _plugin.IsCarPresetBuiltin(carId, activeName);

            bool ok;
            // DEV authoring: built-ins save in place via PersistActiveCarOverride
            // (which write-throughs to factory). Non-dev: fork.
            if (string.IsNullOrEmpty(activeName) || (onBuiltin && !_plugin.DevMode))
            {
                // ToDiskName strips " (Built-In)" (NOT " (default)" which is
                // a separate legacy suffix). Without this the suggestion
                // collides with the merged-dict key for the factory entry.
                // Pre-fill rules:
                //   • Forking a built-in: strip the " (Built-In)" suffix off
                //     the built-in's own name so the suggestion doesn't
                //     immediately collide with the factory entry.
                //   • Fresh fork (no built-in basis): prefer the active car's
                //     human display name (resolved from CarFacts CarName ->
                //     community consensus -> baked name tables) so Forza
                //     ordinals like "Car_2267" pre-fill as "2016 Mazda MX-5"
                //     instead. Falls back to carId only when no name has
                //     been resolved for this car yet.
                string fallbackName = !string.IsNullOrEmpty(_plugin.ActiveCarDisplayName)
                    ? _plugin.ActiveCarDisplayName : carId;
                string suggestion = onBuiltin ? TrueforcePlugin.ToDiskName(activeName) : fallbackName;
                // A factory default's stripped name ("Car_455 (default)")
                // is still the factory DISK name: a user file saved under
                // it is shadowed by the builtin merge (invisible in the
                // dropdown, inert after restart). Suggest the car's
                // display name instead.
                if (_plugin.IsCarPresetBuiltin(carId, suggestion)) suggestion = fallbackName;
                var existing = _plugin.GetCarPresets(carId);
                // Silent fork: suggest the stripped builtin name (or the
                // carId when forking fresh). The user clicked Save with
                // intent to save THIS car's tuning - asking them to type
                // a name they can't really disagree with is friction, so
                // a taken name gets (n) appended instead of a prompt
                // (owner call 2026-07-22).
                if (!string.IsNullOrEmpty(suggestion))
                {
                    string baseSuggestion = suggestion;
                    int n = 2;
                    while ((existing != null && existing.ContainsKey(suggestion))
                           || _plugin.IsCarPresetBuiltin(carId, suggestion))
                        suggestion = $"{baseSuggestion} ({n++})";
                }
                bool silentOk = !string.IsNullOrEmpty(suggestion);
                if (silentOk)
                {
                    if (!_plugin.SaveActiveCarPresetAs(suggestion))
                    {
                        TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                        return false;
                    }
                }
                else
                {
                    string newName = PromptForCarPresetName(
                        title: "Save unsaved car-preset changes",
                        body: onBuiltin
                            ? $"'{activeName}' is a built-in default. Save the current tuning as a new user preset for '{fallbackName}':"
                            : $"Save the current tuning as a new user preset for '{fallbackName}':",
                        initial: suggestion,
                        existing: existing);
                    if (string.IsNullOrEmpty(newName)) return false; // cancelled
                    if (!_plugin.SaveActiveCarPresetAs(newName))
                    {
                        TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                        return false;
                    }
                }
                ok = true;
            }
            else
            {
                ok = _plugin.PersistActiveCarOverride();
            }
            if (ok)
            {
                // (Engine submit removed 2026-07: the pick submits from
                // CommitEnginePin, not from car-preset saves.)
                MaybePromptToSubmitRedlineData(carId);
            }
            return ok;
        }

        /// <summary>Fork-on-save flow: create a new user preset named after
        /// the game (or after the built-in being forked, minus the
        /// " (default)" suffix) and bind it as the game's default. If a
        /// preset with that name already exists, append " (1)", " (2)" until
        /// unique. When the current tuning is content-identical to an
        /// existing preset, reuses that preset instead of dead-ending on
        /// the duplicate rule. Falls back to the Save as… name prompt when
        /// there's no game context to derive a name from. Returns false
        /// only on a real save failure.</summary>
        private bool ForkAndSaveAsGamePreset()
        {
            string activeP = _plugin.ActivePresetName;
            string game    = _plugin.ActiveGame;
            string baseName;
            // Prefer "<built-in> minus (default)" so fork inherits the friendly name.
            const string defaultSuffix = " (default)";
            if (!string.IsNullOrEmpty(activeP) && activeP.EndsWith(defaultSuffix))
                baseName = activeP.Substring(0, activeP.Length - defaultSuffix.Length);
            else if (!string.IsNullOrEmpty(game))
                baseName = game;
            else
            {
                // No game and no built-in to fork from. Fall back to name
                // prompt; its outcome propagates so a cancel isn't success.
                return SaveAsNewPresetFromUi();
            }

            string newName = baseName;
            // De-dupe if collision.
            if (_plugin.PresetNames != null)
            {
                var existing = new System.Collections.Generic.HashSet<string>(_plugin.PresetNames);
                int i = 1;
                while (existing.Contains(newName)) newName = $"{baseName} ({i++})";
            }

            bool reused = false;
            if (!_plugin.SavePresetAs(newName))
            {
                // Content-identical to an existing preset: reuse that preset
                // instead of dead-ending on the duplicate rule (the dash's
                // BOTH got this treatment first; the desktop fork hit the
                // same wall against a leftover earlier fork on 2026-07-22).
                // LastLocalDuplicateName is only set by the duplicate-content
                // refusal; anything else is a real failure.
                string dup = ReuseDuplicateOrNull();
                if (dup == null)
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                    return false;
                }
                newName = dup;
                reused  = true;
            }
            // Auto-bind as game's default if a game is loaded.
            if (!string.IsNullOrEmpty(game))
                _plugin.SetDefaultPresetForActiveGame(newName);
            ClearDirty();
            RefreshFromPlugin();
            // Tell the Preset Manager the local library changed so the fork
            // shows up in its list without a manual refresh.
            _presetManager?.OnLocalLibraryChanged();
            FlashSaveStatus(HeaderGameSaveStatus, reused
                ? $"Same as '{newName}', now active ✓"
                : $"Saved as '{newName}' ✓");
            return true;
        }

        private void SaveAsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string suggested = _plugin.ActivePresetName ?? _plugin.ActiveGame ?? "My preset";
            string name = PromptForName("Save preset as", "Preset name:", suggested);
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            // Confirm overwrite if name collides.
            bool exists = false;
            if (_plugin.PresetNames != null)
                foreach (var n in _plugin.PresetNames) { if (n == name) { exists = true; break; } }
            if (exists && TrueforceDialog.Show(Window.GetWindow(this),
                    "Overwrite preset?",
                    $"A preset called '{name}' already exists. Overwrite?",
                    DialogKind.Confirm) != true)
                return;

            bool reused = false;
            if (!_plugin.SavePresetAs(name))
            {
                // Duplicate-content refusal: reuse the identical preset rather
                // than dead-end. SavePresetAs already folded the active car's
                // draft edits into the globals, so a bare error would leave
                // that promotion stranded; reusing the identical preset makes
                // the folded state the live+saved state consistently.
                string dup = ReuseDuplicateOrNull();
                if (dup == null)
                {
                    TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                    return;
                }
                name   = dup;
                reused = true;
            }
            ClearDirty();
            RefreshFromPlugin();
            _presetManager?.OnLocalLibraryChanged();
            if (reused)
                FlashSaveStatus(HeaderGameSaveStatus, $"Same as '{name}', now active ✓");
        }

        // Car-side "Save as new…": save the active car's current tuning under a
        // new car-preset name (never overwrites the active one). Shown only when
        // this car has unsaved tuning (mirrors the car ★ Save all button).
        private void CarSaveAsNew_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.ActiveCarId)) return;
            string carId      = _plugin.ActiveCarId;
            string activeName = _plugin.GetActiveCarPresetName(carId);
            // Strip the " (Built-In)" suffix so the "Save as new" dialog
            // doesn't pre-fill a name that would immediately collide with
            // the factory entry (StripDefaultSuffix targets " (default)",
            // a separate legacy suffix).
            // Pre-fill priority:
            //   1. Current preset's stripped name (lets a fork start from the
            //      preset the user is currently driving).
            //   2. Active car's resolved display name (CarFacts CarName ->
            //      community -> baked tables) so Forza ordinals don't
            //      pre-fill as "Car_2267".
            //   3. carId itself as the last resort.
            string fallbackName = !string.IsNullOrEmpty(_plugin.ActiveCarDisplayName)
                ? _plugin.ActiveCarDisplayName : carId;
            string suggestion = string.IsNullOrEmpty(activeName)
                ? fallbackName
                : TrueforcePlugin.ToDiskName(activeName);
            string newName = PromptForCarPresetName(
                title: "Save as new car preset",
                body:  $"Save the current tuning as a new user preset for '{carId}':",
                initial: suggestion,
                existing: _plugin.GetCarPresets(carId));
            if (string.IsNullOrEmpty(newName)) return;   // cancelled
            if (!_plugin.SaveActiveCarPresetAs(newName))
            {
                TrueforceDialog.Show(null, "Trueforce For All", "Couldn't save. See the SimHub log for details, then try again.", DialogKind.Warning);
                return;
            }
            ClearDirty();
            RefreshFromPlugin();
            // (Engine submit removed 2026-07: the pick submits from
            // CommitEnginePin, not from car-preset saves.)
            MaybePromptToSubmitRedlineData(carId);
        }

        // Per-preset Delete and Clear-default live in the preset manager (Presets
        // tab) now; the inline buttons were removed in the unified-picker refactor.
        // The header's inline "Set as default" went next (select-is-default:
        // picking a preset in the header combo binds it to the active game).

        // ---------- Export / Import (Backup & sync) ----------
        //
        // Two top-level entry points wired to the Backup & sync section:
        //   Export_Click → open PackPickerWindow filtered to user presets
        //     (built-ins hidden, active car preset pre-checked), save the
        //     selection as a .tfpack.
        //   Import_Click → open a file dialog, auto-detect the file kind
        //     by extension + the JSON "Type" marker, route to the matching
        //     ImportPack / ImportPreset / ImportCarPreset / ImportSettings.
        //
        // The individual export/import handlers that used to be wired to
        // dedicated buttons (preset, car preset, pack, all-settings) were
        // collapsed away when Backup & sync shrank to two buttons. The
        // underlying plugin APIs are still used by the smart import router
        // here and by ManagePresetsDialog's per-row Export buttons.

        // Pop the Author/Description/Version dialog. Author pre-fills from
        // SharingAuthor; on OK, persists the (possibly-edited) author back so
        // the next export pre-fills with what the user just typed. Returns
        // false on Cancel; true with the (possibly-blank) values on OK.
        // Static so ManagePresetsDialog can drive the same export flow from
        // its own Window context.
        internal static bool PromptForExportMetadata(Window owner, TrueforcePlugin plugin,
            string title, string subjectKind,
            out string author, out string description, out string authorVersion,
            out string packName, out bool allowInPacks,
            bool includePackName = false)
        {
            author = description = authorVersion = packName = null;
            allowInPacks = false;
            if (plugin?.Settings == null) return false;

            var dlg = new PresetMetadataDialog(title, subjectKind,
                plugin.Settings.SharingAuthor, "", "",
                includePackName: includePackName, defaultPackName: null,
                defaultAllowInPacks: false)
            {
                Owner = owner,
            };
            if (dlg.Owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (dlg.ShowDialog() != true) return false;

            author        = dlg.Author;
            description   = dlg.Description;
            authorVersion = dlg.AuthorVersion;
            packName      = dlg.PackName;
            allowInPacks  = dlg.AllowInPacks;

            string newAuthor = author?.Trim() ?? "";
            if (newAuthor != (plugin.Settings.SharingAuthor ?? ""))
            {
                plugin.Settings.SharingAuthor = newAuthor;
                try { plugin.PersistSettings(); }
                catch (Exception ex) { SimHub.Logging.Current.Info("[TF4ALL] Persist settings failed: " + ex.Message); }
            }
            return true;
        }

        /// <summary>Tiny inline name-prompt dialog. WPF has no built-in
        /// InputBox; this draws a 360x140 modal with TextBox + OK/Cancel.</summary>
        private string PromptForName(string title, string label, string defaultValue)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var tb = new TextBox { Text = defaultValue ?? "" };
            sp.Children.Add(tb);
            var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                                          HorizontalAlignment = HorizontalAlignment.Right,
                                          Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);
            win.Content = sp;

            string result = null;
            ok.Click += (s, args) => { result = tb.Text; win.DialogResult = true; };
            win.Loaded += (s, args) => { tb.Focus(); tb.SelectAll(); };
            return win.ShowDialog() == true ? result : null;
        }

        private static string MakeFileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "preset";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return new string(arr);
        }

        // Export: opens the pack picker (built-ins hidden, active car
        // preset pre-checked) and saves the selection as a .tfpack.
        // Backup: zip everything Trueforce-owned (Settings + user/) for moving
        // to another machine. Distinct from preset Import/Export which lives
        // in the Preset Manager.
        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "Zip (*.zip)|*.zip",
                FileName = $"TF4ALL-backup-{DateTime.Now:yyyy-MM-dd}.zip",
                Title    = "Backup all Trueforce data",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                _plugin.BackupAllToZip(dlg.FileName);
                ShowSavedStatus(BackupFileStatus,
                    "Backed up to " + System.IO.Path.GetFileName(dlg.FileName) + ".",
                    dlg.FileName);
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't back up. Check the folder and your connection, then try again.",
                    ex);
            }
        }

        // Restore from a backup zip. The options dialog offers two independent
        // choices: replace vs merge the preset library, and apply vs keep the
        // backup's settings. Replace moves the current user/ folder aside to
        // .pre-restore-<timestamp>/ as a safety net; merge is additive and asks
        // which copy to keep on every name clash.
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var owner = Window.GetWindow(this);
            var open = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "TF4ALL backup (*.zip)|*.zip|All files (*.*)|*.*",
                Title  = "Restore from backup",
            };
            if (open.ShowDialog(owner) != true) return;
            string path = open.FileName;

            var opts = new RestoreOptionsWindow { Owner = owner };
            if (owner == null) opts.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (opts.ShowDialog() != true) return;
            bool merge = opts.MergeLibrary;
            bool applySettings = opts.ApplyBackupSettings;

            try
            {
                if (!merge)
                {
                    // Replace: destructive, confirm once more (text adapts to the settings choice).
                    string body = applySettings
                        ? "Replace will swap your presets, car tunings, defaults, AND settings for the backup's."
                        : "Replace will swap your presets, car tunings, and defaults for the backup's, and keep your current settings.";
                    body += "\n\nYour current library is moved to a .pre-restore-<timestamp> folder next to it as a safety net, but the live state becomes the backup's.\n\nContinue?";
                    var confirm = TrueforceDialog.Show(owner, "Restore (replace)", body,
                        DialogKind.Destructive, okLabel: "Replace everything", cancelLabel: "Cancel");
                    if (confirm != true) return;

                    int n = _plugin.RestoreAllFromZip(path, applySettings);
                    ClearDirty();
                    RefreshFromPlugin();
                    ShowSavedStatus(BackupFileStatus, n == 0
                        ? "Restore finished. The archive had no preset files."
                        : $"Restored {n} file(s). Your previous library is kept alongside as a safety net.", null);
                    return;
                }

                // Merge: scan for clashes first so the user sees the scope.
                var scan = _plugin.ScanBackupZipForMerge(path);
                var conflicts = scan.conflicts;
                int newCount = scan.newPresets;
                int newEngines = scan.newEngines;
                int newPacks = scan.newPacks;

                if (newCount == 0 && conflicts.Count == 0 && newEngines == 0 && newPacks == 0 && !applySettings)
                {
                    ShowSavedStatus(BackupFileStatus, "Nothing new to merge; your library already has this backup's contents.", null);
                    return;
                }

                var parts = new List<string>();
                if (newCount > 0)   parts.Add($"{newCount} preset(s)");
                if (newEngines > 0) parts.Add($"{newEngines} custom engine(s)");
                if (newPacks > 0)   parts.Add($"{newPacks} pack(s)");
                string summary = parts.Count > 0
                    ? "Merge will add " + string.Join(", ", parts) + "."
                    : "Merge: nothing new to add.";
                if (conflicts.Count > 0) summary += $"\n\n{conflicts.Count} name clash(es) to resolve; you'll pick which copy to keep for each.";
                if (applySettings) summary += "\n\nThe backup's settings will also be applied.";
                summary += "\n\nContinue?";
                if (TrueforceDialog.Show(owner, "Restore (merge)", summary,
                        DialogKind.Info, okLabel: "Continue", cancelLabel: "Cancel", goldOk: true) != true)
                    return;

                // Resolve clashes: per-item Keep mine / Use the backup's, with a
                // "do this for all remaining" checkbox (and a plain 2-button prompt
                // for the last one, where a batch option is meaningless).
                var overwrite = new HashSet<string>(StringComparer.Ordinal);
                bool? batch = null;   // null = ask; true = use backup for all; false = keep mine for all
                for (int i = 0; i < conflicts.Count; i++)
                {
                    var c = conflicts[i];
                    bool useBackup;
                    if (batch.HasValue)
                    {
                        useBackup = batch.Value;
                    }
                    else
                    {
                        int remaining = conflicts.Count - i;
                        string msg = $"A {c.Kind} named \"{c.DisplayName}\" already exists ({i + 1} of {conflicts.Count}).\n\nKeep your copy, or replace it with the backup's?";
                        if (remaining > 1)
                        {
                            bool applyAll;
                            bool? r = TrueforceDialog.ShowConfirmWithCheckbox(owner, "Name clash", msg,
                                $"Do this for all {remaining} remaining clashes", false, out applyAll,
                                okLabel: "Use the backup's", cancelLabel: "Keep mine");
                            useBackup = (r == true);
                            if (applyAll) batch = useBackup;
                        }
                        else
                        {
                            bool? r = TrueforceDialog.Show(owner, "Name clash", msg,
                                DialogKind.Info, okLabel: "Use the backup's", cancelLabel: "Keep mine");
                            useBackup = (r == true);
                        }
                    }
                    if (useBackup) overwrite.Add(c.RelPath);
                }

                int merged = _plugin.MergeBackupFromZip(path, overwrite, applySettings);
                ClearDirty();
                RefreshFromPlugin();
                ShowSavedStatus(BackupFileStatus,
                    $"Merged {merged} preset(s)" + (applySettings ? " and applied the backup's settings" : "") + ".", null);
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(owner,
                    "Couldn't restore. Check the folder and your connection, then try again.",
                    ex);
            }
        }

        // Legacy handlers retained for static RunExportFlow / RunImportFlow's
        // co-location; not wired from XAML anymore (preset I/O moved to the
        // Preset Manager). Keeps ManagePresetsDialog's call to the static
        // bodies working without recompiling the public API.
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            RunExportFlow(Window.GetWindow(this), _plugin);
        }

        // Import: routes based on file extension + JSON "Type" marker.
        //   .tfpack / .zip → ImportPack
        //   JSON with Type=trueforce-preset       → ImportPreset
        //   JSON with Type=trueforce-car-preset   → ImportCarPreset
        //   JSON with no recognized Type          → ImportSettings (destructive,
        //                                            confirmed first)
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (RunImportFlow(Window.GetWindow(this), _plugin))
            {
                ClearDirty();
                RefreshFromPlugin();
            }
        }

        // Static body of Export_Click so ManagePresetsDialog can run the
        // same flow with itself as the owner Window (otherwise nested modals
        // appear behind the manage dialog).
        // Returns the path of the file just saved (so the caller can show an
        // inline "Show in folder" line), or null if the flow was cancelled or
        // failed. No longer opens Explorer itself.
        internal static string RunExportFlow(Window owner, TrueforcePlugin plugin)
        {
            if (plugin == null) return null;

            var presets = plugin.GetExportablePresetNames()
                .Where(n => !plugin.IsBuiltinPreset(n))
                .ToList();
            var cars = plugin.GetExportableCarPresets()
                .Where(c => !c.IsBuiltin)
                .ToList();
            if (presets.Count == 0 && cars.Count == 0)
            {
                TrueforceDialog.Show(owner, "Trueforce For All",
                                "Nothing to share yet. Save a preset (or a per-car tuning) first.",
                                DialogKind.Info);
                return null;
            }

            string preferCarId = plugin.ActiveCarId;

            // preset name -> set of GameNames it's a default for. Combines
            // shipped GameDefaultBindings with the user's saved GameDefaults
            // so checking a preset on the left filters the car list down to
            // that preset's games.
            var presetGameMappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            void AddMapping(string p, string g)
            {
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(g)) return;
                if (!presetGameMappings.TryGetValue(p, out var list))
                {
                    list = new List<string>();
                    presetGameMappings[p] = list;
                }
                if (list is List<string> mut && !mut.Contains(g)) mut.Add(g);
            }
            foreach (var kv in BuiltinPresets.GameDefaultBindings) AddMapping(kv.Value, kv.Key);
            if (plugin.Settings?.GameDefaults != null)
                foreach (var kv in plugin.Settings.GameDefaults) AddMapping(kv.Value, kv.Key);

            var picker = new PackPickerWindow(presets, cars, exportMode: true, preferCarId, presetGameMappings)
            {
                Owner = owner,
            };
            if (picker.Owner == null) picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (picker.ShowDialog() != true) return null;

            var pickedPresets = picker.SelectedPresetNames;
            var pickedCars    = picker.SelectedCarPresets;
            if (pickedPresets.Count == 0 && pickedCars.Count == 0) return null;

            // Output type is decided by the count of picked items:
            //   1 picked → single loose .tfpreset.json / .tfcar.json file
            //   2+ picked → .tfpack archive with pack identity
            // No mode toggle; the rule keeps a pack from ever being a single
            // preset masquerading as a curated bundle.
            int totalPicked = pickedPresets.Count + pickedCars.Count;
            bool isPack = totalPicked > 1;

            // Capture metadata in one dialog. Pack mode requires Pack Name;
            // single-file mode doesn't show it.
            if (!PromptForExportMetadata(owner, plugin, "Export",
                isPack ? "pack" : "preset",
                out string author, out string desc, out string ver,
                out string packName, out bool allowInPacks,
                includePackName: isPack)) return null;

            if (!isPack)
            {
                // Single loose file: pick one .tfpreset.json or .tfcar.json
                // depending on what the user picked, write it directly to
                // the user-chosen path.
                if (pickedPresets.Count == 1)
                {
                    string nm = pickedPresets[0];
                    var dlg1 = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter     = "TF4ALL preset (*.tfpreset.json)|*.tfpreset.json",
                        FileName   = MakeFileSafe(nm) + ".tfpreset.json",
                        DefaultExt = "tfpreset.json",
                        Title      = "Export preset",
                    };
                    if (dlg1.ShowDialog(owner) != true) return null;
                    try
                    {
                        plugin.ExportSinglePreset(dlg1.FileName, nm, author, desc, ver, allowInPacks);
                        return dlg1.FileName;
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.ShowError(owner,
                            "Couldn't export. Make sure the file isn't open and you can write to that folder, then try again.",
                            ex);
                    }
                }
                else if (pickedCars.Count == 1)
                {
                    var car = pickedCars[0];
                    var dlg2 = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter     = "TF4ALL car preset (*.tfcar.json)|*.tfcar.json",
                        FileName   = MakeFileSafe($"{car.CarId}~{car.PresetName}") + ".tfcar.json",
                        DefaultExt = "tfcar.json",
                        Title      = "Export car preset",
                    };
                    if (dlg2.ShowDialog(owner) != true) return null;
                    try
                    {
                        plugin.ExportSingleCarPreset(dlg2.FileName, car.CarId, car.PresetName, author, desc, ver, allowInPacks);
                        return dlg2.FileName;
                    }
                    catch (Exception ex)
                    {
                        TrueforceDialog.ShowError(owner,
                            "Couldn't export. Make sure the file isn't open and you can write to that folder, then try again.",
                            ex);
                    }
                }
                return null;
            }

            // Pack: archive output. Pack-level author = the user (curator);
            // per-preset authors are preserved by ExportPack so original
            // contributors keep credit.
            string defaultName = $"TF4ALL-pack-{DateTime.Now:yyyy-MM-dd}.tfpack";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter   = "TF4ALL pack (*.tfpack)|*.tfpack|Zip (*.zip)|*.zip",
                FileName = defaultName,
                Title    = "Export pack",
            };
            if (dlg.ShowDialog(owner) != true) return null;
            try
            {
                plugin.ExportPack(
                    dlg.FileName,
                    pickedPresets,
                    pickedCars.ConvertAll(e2 => (e2.CarId, e2.PresetName)),
                    author, desc, ver, packName, allowInPacks);
                return dlg.FileName;
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(owner, "Couldn't export. Make sure the file isn't open and you can write to that folder, then try again.", ex);
            }
            return null;
        }

        // Static body of Import_Click. Returns true if anything was imported
        // so the caller can refresh its own UI (the main panel reapplies the
        // imported settings; the manage dialog reloads its lists).
        //
        // Open the Pack Manager modal for browsing imported community packs.
        // Each action inside the window (Set as defaults / Remove) routes
        // through the plugin's matching method; the window refreshes itself
        // after every action. Returns true when at least one action was
        // performed, so the caller can rebuild its own tabs in case car or
        // game defaults moved.
        internal static bool RunManagePacksFlow(Window owner, TrueforcePlugin plugin)
        {
            if (plugin == null) return false;
            bool anyAction = false;
            var win = new PackManagerWindow(
                loadPacks:     plugin.LoadInstalledPacks,
                setAsDefaults: (p, policy) =>
                {
                    anyAction = true;
                    return plugin.SetPackAsDefaults(p, policy);
                },
                previewDefaults: plugin.PreviewPackDefaults,
                removePack:    p =>
                {
                    anyAction = true;
                    return plugin.RemovePack(p);
                })
            {
                Owner = owner,
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.ShowDialog();
            return anyAction;
        }

        // Multi-select: each picked file is routed by its own extension +
        // JSON "Type" marker, and the results are folded into one summary
        // dialog at the end. Per-file failures are collected and surfaced
        // alongside the count of successful imports so one malformed file
        // doesn't abort the batch.
        internal static bool RunImportFlow(Window owner, TrueforcePlugin plugin)
        {
            if (plugin == null) return false;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "TF4ALL files (*.tfpack;*.tfpreset.json;*.tfcar.json;*.zip;*.json)"
                         + "|*.tfpack;*.tfpreset.json;*.tfcar.json;*.zip;*.json"
                         + "|All files (*.*)|*.*",
                Title       = "Import",
                Multiselect = true,
            };
            if (dlg.ShowDialog(owner) != true) return false;
            var paths = dlg.FileNames;
            if (paths == null || paths.Length == 0) return false;

            int presetsImported  = 0;
            int carsImported     = 0;
            int packsImported    = 0;
            int settingsImported = 0;
            int filesSkipped     = 0;
            var failures         = new List<string>();

            // Classify every picked path into an ImportCandidate (peeks
            // manifest / type / inner fields without writing). Then if any
            // candidate has previewable content, route through the preview
            // modal so the user can deselect rows + toggle set-as-default
            // before committing. Settings-backup + classify-failure
            // candidates skip the modal (they need their own confirmation
            // path or have nothing to choose), but their dispatch still
            // happens in the same loop afterward so the summary at the end
            // covers the whole batch.
            //
            // PREVIEWOFF access code bypasses the preview entirely
            // (commit-on-pick path), kept as an escape hatch while the
            // modal stabilizes.
            bool previewBypass = plugin.Settings != null && plugin.Settings.ImportPreviewBypass;
            var candidates = new List<ImportCandidate>(paths.Length);
            foreach (var path in paths)
                candidates.Add(BuildImportCandidate(plugin, path));

            bool anyPreviewable = candidates.Any(c =>
                (c.Kind == ImportCandidateKind.Pack
                 || c.Kind == ImportCandidateKind.GamePreset
                 || c.Kind == ImportCandidateKind.CarPreset)
                && c.Items != null && c.Items.Count > 0);

            if (anyPreviewable && !previewBypass)
            {
                var previewable = candidates
                    .Where(c => c.Kind == ImportCandidateKind.Pack
                             || c.Kind == ImportCandidateKind.GamePreset
                             || c.Kind == ImportCandidateKind.CarPreset)
                    .ToList();
                var preview = new ImportPreviewWindow(previewable) { Owner = owner };
                if (preview.Owner == null) preview.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (preview.ShowDialog() != true) return false;
                // The dialog mutates IsChecked / SetAsDefault on each
                // PreviewItem; the dispatch loop below reads those flags.
            }

            foreach (var c in candidates)
            {
                try
                {
                    switch (c.Kind)
                    {
                        case ImportCandidateKind.Pack:
                        {
                            if (previewBypass)
                            {
                                // Full-pack import via the old commit-on-pick
                                // path. Bypasses BuildImportCandidate's
                                // manifest-shaped enumeration so even
                                // manifest-incomplete zips import the full
                                // set of zip entries.
                                var r0 = plugin.ImportPack(c.Path);
                                if (r0.PresetsImported > 0 || r0.CarsImported > 0)
                                {
                                    presetsImported += r0.PresetsImported;
                                    carsImported    += r0.CarsImported;
                                    packsImported++;
                                }
                                else
                                {
                                    filesSkipped++;
                                }
                                continue;
                            }
                            // Preview path: collect the user's per-row choices.
                            // Skip the candidate when every item got unchecked
                            // in the modal — nothing to commit and we'd
                            // register a phantom empty pack.
                            var keptGames = c.Items
                                .Where(i => i.Kind == "game" && i.IsChecked)
                                .Select(i => i.Name).ToList();
                            var keptCars = c.Items
                                .Where(i => i.Kind == "car" && i.IsChecked)
                                .Select(i => (i.CarId, i.Name)).ToList();
                            if (keptGames.Count == 0 && keptCars.Count == 0) { filesSkipped++; continue; }

                            var setCarDefault = new HashSet<(string, string)>(
                                c.Items
                                    .Where(i => i.Kind == "car" && i.IsChecked && i.SetAsDefault)
                                    .Select(i => (i.CarId, i.Name)));
                            var setGameDefault = new HashSet<string>(StringComparer.Ordinal);   // V1: empty
                            var r = plugin.ImportPackSelective(c.Path,
                                new HashSet<string>(keptGames, StringComparer.Ordinal),
                                new HashSet<(string, string)>(keptCars),
                                setGameDefault, setCarDefault);
                            if (r.PresetsImported > 0 || r.CarsImported > 0)
                            {
                                presetsImported += r.PresetsImported;
                                carsImported    += r.CarsImported;
                                packsImported++;
                            }
                            else
                            {
                                filesSkipped++;
                            }
                            continue;
                        }

                        case ImportCandidateKind.GamePreset:
                        {
                            // Loose game preset is a single item: skip if
                            // the user unchecked it in the modal.
                            var only = c.Items?.FirstOrDefault();
                            if (only != null && !only.IsChecked) { filesSkipped++; continue; }
                            plugin.ImportPreset(c.Path);
                            presetsImported++;
                            continue;
                        }

                        case ImportCandidateKind.CarPreset:
                        {
                            var only = c.Items?.FirstOrDefault();
                            if (only != null && !only.IsChecked) { filesSkipped++; continue; }
                            var result = plugin.ImportCarPreset(c.Path);
                            carsImported++;
                            // ImportCarPreset already sets CarDefaults[carId]=presetName
                            // unconditionally as part of its existing behavior,
                            // so the user's set-as-default toggle is honored by
                            // default. Nothing extra to wire here for V1.
                            continue;
                        }

                        case ImportCandidateKind.SettingsBackup:
                        {
                            // Destructive: prompt per file, outside the
                            // per-item-checkbox model.
                            var confirm = TrueforceDialog.Show(owner,
                                "Trueforce For All",
                                $"'{System.IO.Path.GetFileName(c.Path)}' looks like a full TF4ALL settings backup. Importing replaces all current settings (master, audio, every effect, all per-car overrides). Continue with this file?",
                                DialogKind.Destructive, okLabel: "Replace everything", cancelLabel: "Cancel");
                            if (confirm == true)
                            {
                                plugin.ImportSettings(c.Path);
                                settingsImported++;
                            }
                            else
                            {
                                filesSkipped++;
                            }
                            continue;
                        }

                        case ImportCandidateKind.Unknown:
                        default:
                        {
                            filesSkipped++;
                            failures.Add($"{System.IO.Path.GetFileName(c.Path)}: {c.FailureMessage ?? "unrecognized file"}");
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{System.IO.Path.GetFileName(c.Path)}: {ex.Message}");
                }
            }

            // Build the summary line.
            var counts = new List<string>();
            if (presetsImported  > 0) counts.Add($"{presetsImported} preset(s)");
            if (carsImported     > 0) counts.Add($"{carsImported} car preset(s)");
            if (packsImported    > 0) counts.Add($"{packsImported} pack(s)");
            if (settingsImported > 0) counts.Add($"{settingsImported} settings backup(s)");

            var msg = new System.Text.StringBuilder();
            msg.Append(counts.Count == 0
                ? "Nothing imported."
                : "Imported " + string.Join(", ", counts) + $" from {paths.Length} file(s).");
            if (filesSkipped > 0) msg.Append($"\n{filesSkipped} file(s) skipped.");
            if (failures.Count > 0)
            {
                msg.Append($"\n\n{failures.Count} file(s) failed:");
                foreach (var f in failures) msg.Append("\n  ").Append(f);
            }

            var kind = failures.Count > 0
                ? DialogKind.Warning
                : DialogKind.Info;
            TrueforceDialog.Show(owner, "Trueforce For All", msg.ToString(), kind);

            return presetsImported > 0 || carsImported > 0 || packsImported > 0 || settingsImported > 0;
        }

        // Parse just the top-level "Type" string from a JSON file. Returns
        // null on parse failure or when the field is missing.
        private static string PeekJsonType(string json)
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                return jo["Type"]?.ToString();
            }
            catch { return null; }
        }

        // True if the JSON looks like a full TF4ALL settings backup (the raw
        // TrueforceSettings shape, which carries no Type field). Requires a
        // couple of distinctive top-level fields so an unrecognized or
        // malformed file is never mistaken for a backup and offered the
        // destructive replace-all path.
        // Stamped into the redacted settings snapshot that ships inside an
        // exported log zip. That file is diagnostic only: identity and community
        // lineage are stripped out of it, and some of what's stripped is data
        // Import would not put back, so it must never be offered as a restorable
        // backup however much it otherwise resembles one.
        internal const string DiagnosticSnapshotMarker = "_Tf4allDiagnosticSnapshot";

        private static bool IsDiagnosticSnapshot(string json)
        {
            try { return Newtonsoft.Json.Linq.JObject.Parse(json)[DiagnosticSnapshotMarker] != null; }
            catch { return false; }
        }

        private static bool LooksLikeSettingsBackup(string json)
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                if (jo[DiagnosticSnapshotMarker] != null) return false;
                return jo["MasterGain"] != null && (jo["Performance"] != null || jo["Forza"] != null);
            }
            catch { return false; }
        }

        // ---------- Import preview types ----------
        // ImportCandidate + ImportPreviewItem describe what one picked file
        // contributes to the preview modal. RunImportFlow builds one
        // ImportCandidate per picked path via BuildImportCandidate, the modal
        // surfaces them with per-row include + set-as-default checkboxes,
        // and on OK the candidates are dispatched (selective for packs,
        // existing ImportPreset/ImportCarPreset for loose).

        internal enum ImportCandidateKind { Pack, GamePreset, CarPreset, SettingsBackup, Unknown }

        internal sealed class ImportPreviewItem
        {
            public string Kind { get; set; }            // "game" or "car"
            public string Name { get; set; }            // game preset name OR car preset name
            public string CarId { get; set; }           // populated for car rows
            public string GameName { get; set; }        // game the preset/car belongs to (display + default-resolution hint)
            public bool   IsChecked { get; set; }       // include in import
            public bool   SetAsDefault { get; set; }    // bind as game/car default in same pass
        }

        internal sealed class ImportCandidate
        {
            public string Path { get; set; }
            public ImportCandidateKind Kind { get; set; }
            public string LooseJson { get; set; }       // cached JSON for loose files (Preset/Car/SettingsBackup), null for packs
            public PresetPackManifest Manifest { get; set; }   // populated for Kind=Pack
            public List<ImportPreviewItem> Items { get; set; } = new List<ImportPreviewItem>();
            public string FailureMessage { get; set; }  // populated when classification itself failed (read-error, unsupported)
        }

        // Classify one picked path into an ImportCandidate. Reads (peeks) the
        // file's metadata WITHOUT writing anything. For packs, opens the zip
        // and peeks manifest.json to enumerate contained items. For loose
        // JSONs, peeks the top-level Type marker + the inner PresetName /
        // CarId / GameName so the preview row has something to show. Never
        // throws on bad input; returns a candidate with Kind=Unknown and a
        // populated FailureMessage instead.
        internal static ImportCandidate BuildImportCandidate(TrueforcePlugin plugin, string path)
        {
            var c = new ImportCandidate { Path = path, Kind = ImportCandidateKind.Unknown };
            try
            {
                string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext == ".tfpack" || ext == ".zip")
                {
                    var manifest = plugin.PeekPackManifest(path);
                    if (manifest == null)
                    {
                        c.FailureMessage = "Not a TF4ALL pack (no manifest.json inside).";
                        return c;
                    }
                    c.Kind = ImportCandidateKind.Pack;
                    c.Manifest = manifest;
                    // Read the REAL PresetName from each .tfpreset entry's
                    // PresetFile.PresetName field rather than deriving it
                    // from the (sanitized) zip entry path. ExportPack runs
                    // SanitizeForZip(presetName) on the entry filename but
                    // keeps the original name inside the file, so any preset
                    // whose name contained '/ \\ : * ? " < > |' would
                    // mismatch ImportPackSelective's include-set lookup and
                    // silently drop. Reading the inner field also gives us
                    // the right display name in the modal.
                    try
                    {
                        using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.StartsWith("presets/", StringComparison.OrdinalIgnoreCase)
                                    && entry.FullName.EndsWith(".tfpreset", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        using (var es = entry.Open())
                                        using (var sr = new System.IO.StreamReader(es))
                                        {
                                            var jo = Newtonsoft.Json.Linq.JObject.Parse(sr.ReadToEnd());
                                            string realName = (string)jo["PresetName"];
                                            if (string.IsNullOrEmpty(realName))
                                                realName = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
                                            c.Items.Add(new ImportPreviewItem
                                            {
                                                Kind = "game", Name = realName,
                                                IsChecked = true, SetAsDefault = false,
                                            });
                                        }
                                    }
                                    catch { /* malformed entry; skip */ }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn($"[TF4ALL] BuildImportCandidate: peek of pack presets failed: {ex.Message}");
                    }
                    if (manifest.Cars != null)
                    {
                        foreach (var packed in manifest.Cars)
                        {
                            if (packed == null || string.IsNullOrEmpty(packed.CarId)) continue;
                            c.Items.Add(new ImportPreviewItem
                            {
                                Kind = "car",
                                CarId = packed.CarId,
                                Name = string.IsNullOrEmpty(packed.PresetName) ? packed.CarId : packed.PresetName,
                                GameName = packed.GameName,
                                IsChecked = true, SetAsDefault = false,
                            });
                        }
                    }
                    return c;
                }

                // Loose JSON. Read once, peek Type + a few inner fields.
                string json = System.IO.File.ReadAllText(path);
                c.LooseJson = json;
                string type = PeekJsonType(json);
                if (type == PresetFile.FileType)
                {
                    c.Kind = ImportCandidateKind.GamePreset;
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                        string presetName = (string)jo["PresetName"] ?? System.IO.Path.GetFileNameWithoutExtension(path);
                        string gameName   = (string)(jo["Snapshot"]?["GameName"]);
                        c.Items.Add(new ImportPreviewItem
                        {
                            Kind = "game", Name = presetName, GameName = gameName,
                            IsChecked = true, SetAsDefault = false,
                        });
                    }
                    catch { /* keep the candidate, fall back to filename */ }
                    return c;
                }
                if (type == CarPresetFile.FileType)
                {
                    c.Kind = ImportCandidateKind.CarPreset;
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                        string presetName = (string)jo["PresetName"];
                        string carId      = (string)jo["CarId"];
                        string gameName   = (string)jo["GameName"];
                        c.Items.Add(new ImportPreviewItem
                        {
                            Kind = "car",
                            CarId = carId,
                            Name = string.IsNullOrEmpty(presetName) ? carId : presetName,
                            GameName = gameName,
                            IsChecked = true, SetAsDefault = false,
                        });
                    }
                    catch { /* keep the candidate, fall back to filename */ }
                    return c;
                }
                if (LooksLikeSettingsBackup(json))
                {
                    c.Kind = ImportCandidateKind.SettingsBackup;
                    return c;
                }
                // Tell the truth about the one file that looks like a backup but
                // deliberately isn't, so the user isn't left guessing why the
                // settings JSON from their log zip won't import.
                if (IsDiagnosticSnapshot(json))
                {
                    c.FailureMessage = "This is the redacted diagnostic copy from a log export, "
                        + "not a backup. Use Settings > Backup for a file you can restore.";
                    return c;
                }
                c.FailureMessage = "Unrecognized file (not a TF4ALL preset, car preset, pack, or settings backup).";
                return c;
            }
            catch (Exception ex)
            {
                c.FailureMessage = ex.Message;
                return c;
            }
        }

        // ---------- Performance tab ----------

        private static string FormatRing(int samples)
        {
            // Each sample at 4 kHz = 0.25 ms.
            double ms = samples * 0.25;
            return $"{samples} ({ms:0.#}ms)";
        }

        private void PerfMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var mode = PerfManualRadio.IsChecked == true ? PerformanceMode.Manual : PerformanceMode.Auto;
            _plugin.SetPerformanceMode(mode);
            bool manual = mode == PerformanceMode.Manual;
            PerfTfRingSlider.IsEnabled    = manual;
            PerfAudioRingSlider.IsEnabled = manual;
        }

        private void PerfTfRingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Bail before touching any UI / plugin state when invoked
            // during XAML load: the parameterless constructor runs
            // InitializeComponent() before the chained ctor sets _plugin,
            // and the slider's initial Value-set fires this handler with
            // _plugin == null and other named UI elements possibly not yet
            // wired (NRE'd PerfTfRingText.Text in 0.1.0-localtest4).
            if (_suppressEvents || _plugin == null) return;
            // The slider snaps to TickFrequency=8 so e.NewValue is already
            // {8,16,24,32,40,48,56,64}. Round to nearest pow2 to avoid the
            // 24/40/48/56 in-betweens (Apply() also sanitizes defensively).
            int v = NearestPow2((int)Math.Round(e.NewValue), 8, 64);
            if (PerfTfRingText != null) PerfTfRingText.Text = FormatRing(v);
            // Only push down to the device in Manual mode (in Auto, the
            // ratchet owns ring sizes and slider edits would conflict).
            if (_plugin.Settings?.Performance?.Mode == PerformanceMode.Manual)
                _plugin.ApplyTfRingSize(v);
        }

        private void PerfAudioRingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int v = NearestPow2((int)Math.Round(e.NewValue), 16, 128);
            if (PerfAudioRingText != null) PerfAudioRingText.Text = FormatRing(v);
            if (_plugin.Settings?.Performance?.Mode == PerformanceMode.Manual)
                _plugin.ApplyAudioRingSize(v);
        }

        private void PerfReset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ResetPerformanceToLowest();
            RefreshFromPlugin();
        }

        private static int NearestPow2(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            int p = 1;
            while ((p << 1) <= v) p <<= 1;
            if (p < min) p = min;
            return p;
        }

        // Rolling 60-second underrun / glitch counters. We sample every meter
        // tick; a 60-bucket second-aligned ring tracks events-per-second so
        // we can show "events in last 60 s" without long-running reset.
        private long _perfLastTfCount, _perfLastAudioCount;
        private long _perfLastBucketSec;
        private readonly long[] _perfTfBucket    = new long[60];
        private readonly long[] _perfAudioBucket = new long[60];

        private void UpdatePerfCounters()
        {
            if (_plugin == null) return;
            long tfNow = _plugin.TfRingUnderruns;
            long auNow = _plugin.AudioRingGlitches;
            long tfDelta = tfNow - _perfLastTfCount;
            long auDelta = auNow - _perfLastAudioCount;
            _perfLastTfCount = tfNow;
            _perfLastAudioCount = auNow;

            long sec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_perfLastBucketSec == 0) _perfLastBucketSec = sec;
            // Advance buckets. Clear any seconds we skipped (idle UI).
            int gap = (int)Math.Min(60, sec - _perfLastBucketSec);
            for (int i = 0; i < gap; i++)
            {
                int idx = (int)((_perfLastBucketSec + 1 + i) % 60);
                _perfTfBucket[idx] = 0;
                _perfAudioBucket[idx] = 0;
            }
            _perfLastBucketSec = sec;
            int curIdx = (int)(sec % 60);
            _perfTfBucket[curIdx]    += tfDelta;
            _perfAudioBucket[curIdx] += auDelta;

            long tfWindow = 0, auWindow = 0;
            for (int i = 0; i < 60; i++) { tfWindow += _perfTfBucket[i]; auWindow += _perfAudioBucket[i]; }
            string tfLabel = $" (cap {_plugin.CurrentTfRingSize})";
            string auLabel = $" (cap {_plugin.CurrentAudioRingSize})";
            PerfCountersText.Text =
                $"Output ring{tfLabel}: {tfWindow} underruns/min · " +
                $"Audio ring{auLabel}: {auWindow} glitches/min";
        }

        // Inline ratchet-notice banner state. Per-ring "original" caps are
        // pinned to whatever was active when the banner first became
        // visible; "latest" updates with each subsequent bump. Both rings
        // share one banner so the user gets at most one consolidated
        // notice no matter how many bumps land in the same session.
        // Cleared on Revert / Dismiss. Not persisted (the resized caps are
        // already saved by Apply*RingSize; the banner itself is ephemeral).
        private int? _ratchetTfOriginalCap;
        private int  _ratchetTfLatestCap;
        private int? _ratchetAudioOriginalCap;
        private int  _ratchetAudioLatestCap;

        private void OnAutoRatchetBumped(bool isTf, int oldCap, int newCap)
        {
            // Fired on the producer thread. Marshal to UI to refresh the
            // live Performance-tab readout (ring caps / counters) so it
            // stays accurate while Auto adjusts. The old inline notice
            // banner is intentionally NOT shown: in Auto the ratchet is
            // self-healing (walks all the way back down once things go
            // quiet, and re-drains a warm-started seed next session), so a
            // per-bump banner over-signals a value the user delegated to
            // Auto. Don't block the producer; BeginInvoke is fire-and-forget.
            try
            {
                Dispatcher.BeginInvoke(new Action(RefreshFromPlugin));
            }
            catch { }
        }

        /// <summary>Update (or first-time show) the inline ratchet notice
        /// banner. Per-ring "original" cap is pinned on first event so
        /// successive bumps consolidate into one banner that always shows
        /// the net delta from session start (or first event of this run).
        /// Handles both UP and DOWN events; if every tracked ring's
        /// latest cap returns to its pinned original, the banner auto-
        /// dismisses because there's nothing notable left to show.
        /// Replaces the old centered Window modal that stole foreground
        /// and re-opened on every bump.</summary>
        private void UpdateRatchetNotice(bool isTf, int oldCap, int newCap)
        {
            if (isTf)
            {
                if (_ratchetTfOriginalCap == null) _ratchetTfOriginalCap = oldCap;
                _ratchetTfLatestCap = newCap;
            }
            else
            {
                if (_ratchetAudioOriginalCap == null) _ratchetAudioOriginalCap = oldCap;
                _ratchetAudioLatestCap = newCap;
            }

            // If every tracked ring is back to where it started, there's
            // nothing to report. Auto-dismiss so the user isn't left
            // staring at a stale "ring was bumped" notice after the
            // ratchet itself decided to walk it back down.
            bool tfBackToStart = _ratchetTfOriginalCap is int tfStart
                                 && _ratchetTfLatestCap == tfStart;
            bool auBackToStart = _ratchetAudioOriginalCap is int auStart
                                 && _ratchetAudioLatestCap == auStart;
            bool anythingToShow =
                (_ratchetTfOriginalCap    is int && !tfBackToStart) ||
                (_ratchetAudioOriginalCap is int && !auBackToStart);
            if (!anythingToShow)
            {
                ClearRatchetNotice();
                return;
            }

            // Build the consolidated detail line. Each ring contributes a
            // "label: oldCap → newCap samples (oldMs → newMs ms)" segment;
            // segments are joined with " · ". Ms = samples * 0.25 (4 kHz
            // sample rate = 1000 packets/s × 4 per packet; 1 sample =
            // 0.25 ms). Rings that have returned
            // to their original cap are omitted from this run's notice.
            var segments = new System.Collections.Generic.List<string>();
            if (_ratchetTfOriginalCap is int tfOrig && !tfBackToStart)
            {
                segments.Add(
                    $"Output ring: {tfOrig} → {_ratchetTfLatestCap} samples " +
                    $"({tfOrig * 0.25:0.#} → {_ratchetTfLatestCap * 0.25:0.#} ms)");
            }
            if (_ratchetAudioOriginalCap is int auOrig && !auBackToStart)
            {
                segments.Add(
                    $"Audio ring: {auOrig} → {_ratchetAudioLatestCap} samples " +
                    $"({auOrig * 0.25:0.#} → {_ratchetAudioLatestCap * 0.25:0.#} ms)");
            }

            if (RatchetNoticeDetail != null)
            {
                RatchetNoticeDetail.Text = string.Join("  ·  ", segments)
                    + ". Persisted across sessions. Revert to restore the size(s) at the start of this run, or dismiss to keep the current size(s).";
            }
            if (RatchetNoticeBanner != null)
                RatchetNoticeBanner.Visibility = Visibility.Visible;
        }

        private void RatchetNoticeRevert_Click(object sender, RoutedEventArgs e)
        {
            // Revert every ring that bumped this session back to its
            // original pre-bump cap. Apply* persists and resizes live.
            if (_ratchetTfOriginalCap is int tfOrig && _plugin != null)
                _plugin.ApplyTfRingSize(tfOrig);
            if (_ratchetAudioOriginalCap is int auOrig && _plugin != null)
                _plugin.ApplyAudioRingSize(auOrig);
            ClearRatchetNotice();
            RefreshFromPlugin();
        }

        private void RatchetNoticeDismiss_Click(object sender, RoutedEventArgs e)
        {
            ClearRatchetNotice();
        }

        private void ClearRatchetNotice()
        {
            _ratchetTfOriginalCap = null;
            _ratchetTfLatestCap = 0;
            _ratchetAudioOriginalCap = null;
            _ratchetAudioLatestCap = 0;
            if (RatchetNoticeBanner != null)
                RatchetNoticeBanner.Visibility = Visibility.Collapsed;
        }

        // One-shot dispatcher-thread timer. Used by the RATCHET test code
        // to stage synthetic ratchet events with visible delays between
        // them so the consolidation behavior is observable.
        private void ScheduleDispatcherDelay(int delayMs, Action action)
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs),
            };
            t.Tick += (_, __) =>
            {
                t.Stop();
                try { action(); } catch { }
            };
            t.Start();
        }

        // ---------- Support ----------

        private const string DonateUrl  = "https://ko-fi.com/mhytee";
        private const string PatreonUrl = "https://www.patreon.com/Mhytee";
        private const string PayPalUrl  = "https://www.paypal.me/mhytee";

        // Transient inline status for the Support tab so "Opening…" / "Link
        // copied." don't linger forever. Keyed per-label; fade=true animates the
        // label out instead of a hard clear.
        // Siblings (deliberately separate semantics, don't merge): ShowSavedStatus
        // (persistent message + "Show in folder" link) and FlashSaveStatus
        // (paired header save labels, shared timed clear).
        private readonly System.Collections.Generic.Dictionary<TextBlock, System.Windows.Threading.DispatcherTimer> _supportStatusTimers
            = new System.Collections.Generic.Dictionary<TextBlock, System.Windows.Threading.DispatcherTimer>();
        private void ClearStatusAfter(TextBlock label, double seconds, bool fade)
        {
            if (label == null) return;
            if (_supportStatusTimers.TryGetValue(label, out var prev)) prev.Stop();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (_, __) =>
            {
                t.Stop();
                if (!fade) { label.Text = ""; return; }
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(450)));
                anim.Completed += (_, ___) =>
                {
                    label.Text = "";
                    label.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);   // release -> restore base opacity
                };
                label.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
            };
            _supportStatusTimers[label] = t;
            t.Start();
        }

        private void Patreon_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "Opening Patreon in your browser…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PatreonUrl) { UseShellExecute = true });
                ClearStatusAfter(SupportOpenStatus, 3.0, fade: false);   // launched: drop the notice once it's open
            }
            catch (Exception ex)
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "";
                SimHub.Logging.Current.Info("[TF4ALL] Open Patreon failed: " + ex.Message);
                TrueforceDialog.Show(null, "Trueforce For All",
                    $"Couldn't open your browser. Copy this link into it instead:\n\n{PatreonUrl}",
                    DialogKind.Error);
            }
        }

        private void Donate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "Opening Ko-fi in your browser…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DonateUrl)
                {
                    UseShellExecute = true,  // .NET Framework 4.8 launches the URL via the default browser
                });
                ClearStatusAfter(SupportOpenStatus, 3.0, fade: false);
            }
            catch (Exception ex)
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "";
                SimHub.Logging.Current.Info("[TF4ALL] Open Ko-fi failed: " + ex.Message);
                TrueforceDialog.Show(null, "Trueforce For All",
                                $"Couldn't open your browser. Copy this link into it instead:\n\n{DonateUrl}",
                                DialogKind.Error);
            }
        }

        private void Paypal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "Opening PayPal in your browser…";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PayPalUrl) { UseShellExecute = true });
                ClearStatusAfter(SupportOpenStatus, 3.0, fade: false);
            }
            catch (Exception ex)
            {
                if (SupportOpenStatus != null) SupportOpenStatus.Text = "";
                SimHub.Logging.Current.Info("[TF4ALL] Open PayPal failed: " + ex.Message);
                TrueforceDialog.Show(null, "Trueforce For All",
                    $"Couldn't open your browser. Copy this link into it instead:\n\n{PayPalUrl}",
                    DialogKind.Error);
            }
        }

        // Copy the project link for sharing. Brief inline confirmation that fades
        // after a few seconds; falls back to showing the URL if the clipboard is
        // locked by another app.
        // Clipboard.SetText throws CLIPBRD_E_CANT_OPEN whenever another
        // process briefly holds the clipboard (overlays, clipboard managers,
        // RDP sessions). A few short retries clears it almost every time;
        // SetDataObject(copy: true) keeps the text available after the
        // plugin closes. Used by the share dialog's copy rows.
        internal static bool TryCopyToClipboard(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
            return false;
        }

        // ---------- Update CTA / modal ----------

        // Paint a fresh Window to match the host SimHub BaseDark theme. Code-
        // behind modals don't inherit the panel's theme styles automatically;
        // without this, every Window we open lands on the system default
        // (white background, black text) which is unreadable inside SimHub.
        // Internal because ManagePresetsDialog's nested modals call it too.
        internal static void ApplyDarkTheme(Window win)
        {
            win.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            // TextElement.Foreground is the inherited property that TextBlock,
            // Button content, etc all pick up by default. Setting it on the
            // Window propagates to every descendant that doesn't override.
            TextElement.SetForeground(win, new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)));
        }

        private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
        {
            ShowUpdateModal();
        }

        // ---------- Advanced settings ----------

        // Performance, Sidechain ducking, and Diagnostics live inline in
        // AdvancedSettingsHost at the bottom of the Settings tab. This entry
        // point used to open a modal; now it just brings the Settings tab
        // forward and expands Diagnostics. Kept because the "No, still no FFB"
        // path (ExperimentalSuccessNo_Click) routes users here.
        private void OpenAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs != null && SettingsTab != null)
                MainTabs.SelectedItem = SettingsTab;
            if (DiagnosticsExpander != null)
            {
                DiagnosticsExpander.IsExpanded = true;
                // Defer the scroll until the tab's content has laid out.
                Dispatcher.BeginInvoke(new Action(() => DiagnosticsExpander.BringIntoView()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // Render a GitHub-flavored Markdown release body as a stack of styled
        // TextBlocks. Supports headings (#..######) and bullets (- / *); other
        // syntax falls through as plain text. We don't pull in a real markdown
        // parser because the release notes only ever use these two constructs
        // and we want zero added dependencies in net48 plugin land.
        private static StackPanel RenderReleaseNotes(string body)
        {
            var panel = new StackPanel();
            if (string.IsNullOrWhiteSpace(body))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "(No release notes published.)",
                    FontSize = 12,
                    Opacity = 0.7,
                });
                return panel;
            }

            // Normalize line endings: GitHub bodies usually arrive with \r\n.
            string[] lines = body.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            bool prevWasBlank = false;
            bool imageNoteShown = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? "";
                string trimmed = raw.TrimStart();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Collapse runs of blank lines into a single small gap.
                    if (!prevWasBlank && panel.Children.Count > 0)
                    {
                        panel.Children.Add(new TextBlock { Height = 6 });
                        prevWasBlank = true;
                    }
                    continue;
                }
                prevWasBlank = false;

                // Markdown images (![alt](url), or link-wrapped [![...]) and
                // the HTML ones GitHub writes when you drag a file into the
                // release editor (<img src="...user-attachments/...">, and
                // <video> / <picture> for the same reason).
                // This renderer is text-only (and WPF wouldn't animate a
                // release GIF anyway), so image lines are dropped instead of
                // showing as raw markup. One dim pointer per body tells
                // in-app readers where the visuals live.
                if (trimmed.StartsWith("![", StringComparison.Ordinal)
                    || trimmed.StartsWith("[![", StringComparison.Ordinal)
                    || trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<video", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<picture", StringComparison.OrdinalIgnoreCase))
                {
                    if (!imageNoteShown)
                    {
                        imageNoteShown = true;
                        panel.Children.Add(new TextBlock
                        {
                            Text = "(screenshots on the GitHub release page)",
                            FontSize = 11,
                            Opacity = 0.55,
                            Margin = new Thickness(0, 0, 0, 2),
                        });
                    }
                    continue;
                }

                // Blockquote ("> ..." lines), including GitHub alert callouts
                // ("> [!WARNING]" etc.). Consecutive quote lines collapse into
                // one left-accented callout box; the quoted content re-enters
                // this renderer, so headers/bullets/bold inside it work.
                if (trimmed[0] == '>')
                {
                    var quoted = new System.Collections.Generic.List<string>();
                    int j = i;
                    while (j < lines.Length)
                    {
                        string q = (lines[j] ?? "").TrimStart();
                        if (q.Length == 0 || q[0] != '>') break;
                        string innerLine = q.Substring(1);
                        if (innerLine.StartsWith(" ", StringComparison.Ordinal))
                            innerLine = innerLine.Substring(1);
                        quoted.Add(innerLine);
                        j++;
                    }
                    panel.Children.Add(BuildQuoteCallout(quoted));
                    i = j - 1;   // loop ++ lands on the first non-quote line
                    continue;
                }

                // Heading levels 1..3 (deeper levels fall through to plain).
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;
                if (hashCount >= 1 && hashCount <= 3
                    && hashCount < trimmed.Length && trimmed[hashCount] == ' ')
                {
                    string text = trimmed.Substring(hashCount + 1).Trim();
                    double size = hashCount == 1 ? 16 : hashCount == 2 ? 14 : 13;
                    var hdr = new TextBlock
                    {
                        Text = text,
                        FontSize = size,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 10, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    // Gold the section (###) headers to match the bundled
                    // changelog's grouped look; keep the title (#/##) default.
                    if (hashCount >= 3)
                        hdr.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x86, 0x0B));
                    panel.Children.Add(hdr);
                    continue;
                }

                // Bullet rows ("- foo" / "* foo"). Use a real bullet glyph
                // indented one step. Inline **bold** spans get rendered as
                // bold runs so release notes like "- **Headline.** desc"
                // don't show literal asterisks.
                if (trimmed.Length >= 2
                    && (trimmed[0] == '-' || trimmed[0] == '*')
                    && trimmed[1] == ' ')
                {
                    string content = trimmed.Substring(2);
                    // Two-tier: when the bullet opens with a **bold** lead-in
                    // (our "- **Headline:** description" shape), render the
                    // headline as a bulleted bold line and the rest as a dimmed,
                    // indented description line, echoing the bundled changelog.
                    if (content.StartsWith("**", StringComparison.Ordinal))
                    {
                        int close = content.IndexOf("**", 2, StringComparison.Ordinal);
                        if (close > 2)
                        {
                            string headline = content.Substring(2, close - 2);
                            string desc = content.Substring(close + 2).TrimStart();
                            var hl = new TextBlock
                            {
                                FontSize = 12,
                                Margin = new Thickness(8, 4, 0, 0),
                                TextWrapping = TextWrapping.Wrap,
                            };
                            hl.Inlines.Add(new Run("• "));
                            // Through the inline renderer (re-wrapped in **
                            // so it keeps the bold weight) instead of a raw
                            // bold Run: headlines can carry links, like the
                            // bold Patreon link in the v0.2.1 warning.
                            AppendInlineMarkdown(hl, "**" + headline + "**");
                            panel.Children.Add(hl);
                            if (desc.Length > 0)
                            {
                                var db = new TextBlock
                                {
                                    FontSize = 11,
                                    Opacity = 0.7,
                                    Margin = new Thickness(22, 2, 0, 0),
                                    TextWrapping = TextWrapping.Wrap,
                                };
                                AppendInlineMarkdown(db, desc);
                                panel.Children.Add(db);
                            }
                            continue;
                        }
                    }
                    var tb = new TextBlock
                    {
                        FontSize = 12,
                        Margin = new Thickness(8, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    tb.Inlines.Add(new Run("• "));
                    AppendInlineMarkdown(tb, content);
                    panel.Children.Add(tb);
                    continue;
                }

                // Plain paragraph line. Same **bold** treatment as bullets.
                var para = new TextBlock
                {
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
                AppendInlineMarkdown(para, trimmed);
                panel.Children.Add(para);
            }
            return panel;
        }

        // A "> quoted" block as a left-accented callout box. A GitHub alert
        // marker ("[!WARNING]" etc.) as the first quoted line picks the title
        // and accent color, echoing how GitHub renders it; a plain quote gets
        // a neutral bar and no title. Content re-enters RenderReleaseNotes,
        // so everything the renderer knows works inside the box too.
        private static Border BuildQuoteCallout(System.Collections.Generic.List<string> quoted)
        {
            string title  = null;
            Color  accent = Color.FromRgb(0x88, 0x88, 0x88);
            int start = 0;
            string first = null;
            for (int k = 0; k < quoted.Count; k++)
            {
                if (!string.IsNullOrWhiteSpace(quoted[k])) { first = quoted[k].Trim(); start = k; break; }
            }
            if (first != null
                && first.StartsWith("[!", StringComparison.Ordinal)
                && first.EndsWith("]", StringComparison.Ordinal))
            {
                switch (first.Substring(2, first.Length - 3).ToUpperInvariant())
                {
                    case "WARNING":   title = "Warning";   accent = Color.FromRgb(0xE5, 0xC0, 0x4A); break;
                    case "CAUTION":   title = "Caution";   accent = Color.FromRgb(0xE0, 0x62, 0x5A); break;
                    case "IMPORTANT": title = "Important"; accent = Color.FromRgb(0xB0, 0x87, 0xE8); break;
                    case "NOTE":      title = "Note";      accent = Color.FromRgb(0x6C, 0xA0, 0xDD); break;
                    case "TIP":       title = "Tip";       accent = Color.FromRgb(0x5F, 0xB8, 0x6A); break;
                }
                if (title != null) start++;   // the marker line itself isn't content
            }

            StackPanel inner;
            string bodyText = string.Join("\n", quoted.Skip(start));
            inner = string.IsNullOrWhiteSpace(bodyText) ? new StackPanel() : RenderReleaseNotes(bodyText);
            if (title != null)
            {
                inner.Children.Insert(0, new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(accent),
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }
            return new Border
            {
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x16, accent.R, accent.G, accent.B)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 6, 0, 6),
                Child = inner,
            };
        }

        // Append `text` to a TextBlock's Inlines, rendering **bold** runs in
        // bold and [label](https://url) as clickable links. Anything outside
        // those is plain. An unclosed `**` stays literal rather than being
        // dropped, so a body that opens bold without closing degrades
        // gracefully; a malformed or non-http link stays literal too. Links
        // may sit inside bold spans (and carry the bold weight); bold markers
        // inside a link label are consumed as styling, not shown.
        private static void AppendInlineMarkdown(TextBlock tb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool bold = false;
            var sb = new System.Text.StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                tb.Inlines.Add(new Run(sb.ToString())
                {
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                });
                sb.Clear();
            }
            int i = 0;
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    // Opening bold needs a closer somewhere ahead; otherwise
                    // the marker is literal text.
                    if (!bold && text.IndexOf("**", i + 2, StringComparison.Ordinal) < 0)
                    {
                        sb.Append("**");
                        i += 2;
                        continue;
                    }
                    Flush();
                    bold = !bold;
                    i += 2;
                    continue;
                }
                if (text[i] == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket)
                        {
                            string label = text.Substring(i + 1, closeBracket - i - 1).Replace("**", "");
                            string url   = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                            if (label.Length > 0
                                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                            {
                                Flush();
                                var link = new System.Windows.Documents.Hyperlink(new Run(label)
                                {
                                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                                })
                                {
                                    NavigateUri = uri,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xEE)),
                                };
                                link.RequestNavigate += (s, e) =>
                                {
                                    try
                                    {
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                            e.Uri.AbsoluteUri) { UseShellExecute = true });
                                    }
                                    catch { }
                                };
                                tb.Inlines.Add(link);
                                i = closeParen + 1;
                                continue;
                            }
                        }
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            Flush();
        }

        // Manual "Check for updates" link in the header. The plugin already
        // fires a one-shot check on Init, but users opening the panel hours
        // later need a way to re-poll without restarting SimHub. The
        // settings-panel timer tick (RefreshFromPlugin) picks up the new
        // result automatically, so this handler only has to drive the
        // transient status label next to the button.
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            var upd = _plugin?.UpdateChecker;
            if (upd == null) return;
            if (CheckForUpdatesButton == null || CheckForUpdatesStatus == null) return;

            CheckForUpdatesButton.IsEnabled = false;
            CheckForUpdatesStatus.Text = "Checking…";
            try
            {
                await upd.CheckAsync(_plugin.UpdateCheckerToken);
                // Reset the background re-poll clock so the next automatic check
                // is measured from this manual one, not from startup.
                _plugin.MarkUpdateChecked();
                // Beta auto-enroll + channel re-select, so a manual check
                // behaves exactly like the startup one.
                _plugin.RefreshUpdateChannel();
                if (upd.IsUpdateAvailable)
                    CheckForUpdatesStatus.Text = upd.IsDowngrade
                        ? $"v{upd.LatestVersionDisplay} available (back to the main release)"
                        : $"v{upd.LatestVersionDisplay} available";
                else if (!string.IsNullOrEmpty(upd.LastError))
                    CheckForUpdatesStatus.Text = "Couldn't reach GitHub";
                else
                    CheckForUpdatesStatus.Text = "You're up to date";
            }
            catch
            {
                CheckForUpdatesStatus.Text = "Check failed";
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }

            // Fade the status after a few seconds. Captured snapshot avoids
            // clobbering a newer status if the user clicks again quickly.
            string captured = CheckForUpdatesStatus.Text;
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (CheckForUpdatesStatus != null && CheckForUpdatesStatus.Text == captured)
                CheckForUpdatesStatus.Text = "";
        }

        // (The header "Check now for community updates" button was removed; community-preset
        // update review is reached from the Preset browser's updates chip, which calls the
        // same FindCommunityPresetUpdatesAsync / PresetUpdatesAvailableWindow plumbing.)

        /// <summary>True when there are unsaved tuning changes (a Save / Revert
        /// button is showing): any per-section dirty bit, or the active car
        /// preset drifting from its saved override. Used to warn before an
        /// update force-closes SimHub and discards them.</summary>
        private bool HasUnsavedChanges()
        {
            if (_plugin == null) return false;
            if (_updateLocalInstallerTest) return true;   // forced by the UPDATEDIRTY test code
            for (int i = 0; i < _effectDirty.Length; i++)
                if (_effectDirty[i]) return true;
            return !string.IsNullOrEmpty(_plugin.ActiveCarId)
                   && _plugin.IsActiveCarPresetDirty();
        }

        /// <summary>Modal showing the latest release notes plus an "Update now"
        /// button that downloads the installer to %TEMP% with a progress bar
        /// and ShellExecutes it. The installer's IsSimHubRunning loop handles
        /// the "close SimHub first" case once the user clicks Run.</summary>
        private void ShowUpdateModal()
        {
            var upd = _plugin?.UpdateChecker;
            if (upd == null || !upd.IsUpdateAvailable) return;

            var win = new Window
            {
                Title = "Trueforce For All: update available",
                Width = 600,
                Height = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new DockPanel { Margin = new Thickness(16) };

            // Header: version transition
            var header = new TextBlock
            {
                Text = $"v{upd.CurrentVersion.ToString(3)}  →  v{upd.LatestVersionDisplay}",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Footer area (buttons + progress) docked to bottom.
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);

            var status = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4),
                Text = "",
            };
            footer.Children.Add(status);

            var progress = new ProgressBar
            {
                Height = 6,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };
            footer.Children.Add(progress);

            var btnRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var dismissBtn = new System.Windows.Controls.Button
            {
                Content = "Dismiss",
                Width = 90,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };
            // Green "confirm" styling so the primary action stands out
            // against the dark modal chrome and the muted Dismiss button.
            var updateBtn = new System.Windows.Controls.Button
            {
                Content = "Update now",
                Width = 120,
                Height = 28,
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0x40)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x6E, 0x32)),
                FontWeight = FontWeights.SemiBold,
            };
            btnRow.Children.Add(dismissBtn);
            btnRow.Children.Add(updateBtn);
            footer.Children.Add(btnRow);

            root.Children.Add(footer);

            // Center: scrollable release notes. GitHub release bodies are
            // Markdown; we render a minimal subset (headers, bullets) so the
            // literal "## Heading" and "- item" prefixes don't leak through.
            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Padding = new Thickness(10),
            };
            notesScroll.Content = RenderReleaseNotes(upd.ReleaseNotes);
            root.Children.Add(notesScroll);

            win.Content = root;

            dismissBtn.Click += (_, __) => win.Close();
            updateBtn.Click += async (_, __) =>
            {
                // Updating force-closes SimHub to replace the plugin, which
                // discards any unsaved tuning (effect edits aren't written to
                // disk until saved/closed, and we don't get a graceful close).
                // If there are unsaved changes, confirm first; once the user
                // accepts here, the installer closes SimHub without re-asking.
                if (HasUnsavedChanges())
                {
                    var confirm = TrueforceDialog.Show(Window.GetWindow(this),
                        "Trueforce For All: unsaved changes",
                        "You have unsaved changes that will be discarded if you continue.\n\n" +
                        "Update now? (No to go back and save first.)",
                        DialogKind.Destructive, okLabel: "Update", cancelLabel: "Cancel");
                    if (confirm != true) return;
                }

                // Stable -> beta crossing: snapshot presets + settings before
                // the beta's one-way migrations run, so the switch-back offer
                // can restore them later (see PreBetaBackup). The reverse
                // crossing (the switch-back itself) asks up front whether to
                // put that snapshot back; both file operations run after the
                // download succeeds, right before the installer launches.
                bool crossingToBeta = !upd.CurrentVersionIsPrerelease && upd.LatestIsPrerelease;
                bool restoreBackup  = false;
                // The persistence-suppressed check skips the prompt when a
                // restore already ran this session (installer launch was
                // cancelled): the disk is already restored, so a retry should
                // just download and launch again.
                if (upd.IsDowngrade && !_plugin.SettingsPersistenceSuppressed)
                {
                    var backup = PreBetaBackup.TryRead();
                    if (backup != null && PreBetaBackup.IsCompatibleWith(upd.LatestVersionTag, backup))
                    {
                        var pick = TrueforceDialog.Show(Window.GetWindow(this),
                            "Restore your pre-beta backup?",
                            $"Your presets and settings were backed up before your first beta install "
                            + $"(v{backup.FromVersion}, {backup.TakenUtc:yyyy-MM-dd}). Restore them so the "
                            + "main release picks up exactly where you left off? Anything added or "
                            + "changed while on the beta will be replaced.",
                            DialogKind.Confirm, okLabel: "Restore backup", cancelLabel: "Keep current data");
                        restoreBackup = pick == true;
                    }
                }

                updateBtn.IsEnabled = false;
                dismissBtn.IsEnabled = false;

                try
                {
                    string path;
                    if (_updateLocalInstallerTest)
                    {
                        // Test mode (UPDATEDIRTY): skip the GitHub download and
                        // run a locally-picked installer through the same launch
                        // path instead, so the whole in-app update flow can be
                        // exercised without a real release.
                        var pick = new Microsoft.Win32.OpenFileDialog
                        {
                            Title  = "Pick the installer to run (test; runs with /CloseSimHub=1)",
                            Filter = "Installer (*.exe)|*.exe",
                        };
                        if (pick.ShowDialog() != true)
                        {
                            status.Text = "Cancelled (no installer picked).";
                            updateBtn.IsEnabled = true;
                            dismissBtn.IsEnabled = true;
                            return;
                        }
                        path = pick.FileName;
                    }
                    else
                    {
                        progress.Visibility = Visibility.Visible;
                        progress.IsIndeterminate = true;
                        status.Text = "Downloading installer...";
                        path = await upd.DownloadInstallerAsync((received, total) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (total > 0)
                                {
                                    progress.IsIndeterminate = false;
                                    progress.Maximum = total;
                                    progress.Value = received;
                                    status.Text = $"Downloading installer... {(received / 1024.0 / 1024.0):F1} / {(total / 1024.0 / 1024.0):F1} MB";
                                }
                                else
                                {
                                    status.Text = $"Downloading installer... {(received / 1024.0 / 1024.0):F1} MB";
                                }
                            });
                        }, _plugin.UpdateCheckerToken);
                    }

                    if (crossingToBeta)
                    {
                        status.Text = "Backing up your presets and settings...";
                        // Flush in-memory state first so the snapshot is current.
                        _plugin.PersistSettings();
                        bool ok = await Task.Run(() =>
                            PreBetaBackup.TryTake(upd.CurrentVersion.ToString(3)));
                        if (!ok)
                        {
                            var cont = TrueforceDialog.Show(Window.GetWindow(this),
                                "Backup didn't complete",
                                "Couldn't back up your presets and settings before the beta "
                                + "install (see the SimHub log). You can still update; switching "
                                + "back to the main release later just won't offer an automatic "
                                + "restore.",
                                DialogKind.Destructive, okLabel: "Update anyway", cancelLabel: "Cancel");
                            if (cont != true)
                            {
                                status.Text = "Update cancelled.";
                                progress.Visibility = Visibility.Collapsed;
                                updateBtn.IsEnabled = true;
                                dismissBtn.IsEnabled = true;
                                return;
                            }
                        }
                    }

                    if (restoreBackup)
                    {
                        status.Text = "Restoring your pre-beta presets and settings...";
                        // From here the restored files on disk are the source of
                        // truth; block every later persist (including the End()
                        // save when the installer closes SimHub) so this
                        // session's beta-format state can't clobber them.
                        _plugin.SuppressSettingsPersistence();
                        try
                        {
                            string runningVersion = upd.CurrentVersion.ToString(3);
                            await Task.Run(() => PreBetaBackup.Restore(runningVersion));
                        }
                        catch (Exception rex)
                        {
                            _plugin.ResumeSettingsPersistence();
                            TrueforceDialog.LogError("Pre-beta restore", rex);
                            status.Text = "Restore failed; update cancelled. See the SimHub log.";
                            progress.Visibility = Visibility.Collapsed;
                            updateBtn.IsEnabled = true;
                            dismissBtn.IsEnabled = true;
                            return;
                        }
                    }

                    status.Text = "Launching installer...";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        // The user already accepted (incl. discarding unsaved
                        // changes), so tell the installer to close SimHub
                        // without prompting again.
                        Arguments = "/CloseSimHub=1",
                    });
                    win.Close();
                }
                catch (Exception ex)
                {
                    status.Text = "Couldn't apply the update. See the SimHub log, then try again.";
                    TrueforceDialog.LogError("Update", ex);
                    progress.Visibility = Visibility.Collapsed;
                    updateBtn.IsEnabled = true;
                    dismissBtn.IsEnabled = true;
                }
            };

            win.ShowDialog();
            // Disarm the local-installer test once the modal closes (no-op for
            // the real update path, which never sets it).
            _updateLocalInstallerTest = false;
        }

        // ---------- "What's new" banner / modal ----------

        private void WhatsNewBanner_Click(object sender, MouseButtonEventArgs e)
        {
            ShowWhatsNewModal();
        }

        /// <summary>Modal listing every release between the user's stamped
        /// LastSeenVersion and the running build. Prefers GitHub release
        /// notes (canonical source: GitHub release body for each version);
        /// falls back to the in-source EffectChangelog when the GH fetch
        /// hasn't completed or failed, so offline / first-launch flows
        /// still show something. Closing the modal stamps LastSeenVersion
        /// to the running build and hides the banner for this version.</summary>
        private void ShowWhatsNewModal()
        {
            if (_plugin == null) return;

            // GitHub release notes are the canonical source for this modal (so
            // notes can be fixed post-release without a plugin update); the
            // bundled EffectChangelog is the offline fallback. RenderReleaseNotes
            // styles the markdown to echo the bundled changelog's look (gold
            // section headers, dimmed two-tier entries).
            var ghReleases = _plugin.GetGitHubReleasesForBanner();
            var pending    = _plugin.GetPendingChangelog();
            // Prefer GitHub notes only when a release exists for the RUNNING
            // version. On a dev build ahead of any public release (no matching
            // release yet), fall back to the bundled EffectChangelog so changelogs
            // can be authored and previewed as we go instead of showing stale
            // notes from the last public release below this version.
            bool useGitHub = ghReleases != null && ghReleases.Count > 0
                             && _plugin.GitHubHasReleaseForCurrentVersion();
            bool useLocal  = !useGitHub && pending != null && pending.Count > 0;
            // The banner fires on any version upgrade (HasUnseenChangelog is
            // version-based, independent of bundled content), so an offline
            // upgrade with no EffectChangelog entry for this build would be a
            // dead banner click. Keep the early-out only when there's genuinely
            // nothing new; otherwise fall through to a short offline note below.
            if (!useGitHub && !useLocal && !_plugin.HasUnseenChangelog) return;

            var win = new Window
            {
                Title = "Trueforce For All: what's new",
                Width = 600,
                Height = 480,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = "What's new",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            var gotItBtn = new System.Windows.Controls.Button
            {
                Content = "Got it",
                Width = 100,
                Height = 28,
                IsDefault = true,
                IsCancel = true,
            };
            footer.Children.Add(gotItBtn);
            root.Children.Add(footer);

            var bodyStack = new StackPanel();
            if (useGitHub)
            {
                // Render each release's GitHub body as Markdown via the same
                // helper the update modal uses. Body is canonical; the version
                // header is added by us so users still see a clear divider
                // between releases even if a body forgets its own heading.
                for (int i = 0; i < ghReleases.Count; i++)
                {
                    var r = ghReleases[i];
                    string title = string.IsNullOrEmpty(r.Title) ? ("v" + r.Version.ToString(3)) : r.Title;
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14,
                        Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6),
                    });
                    bodyStack.Children.Add(RenderReleaseNotes(r.Body));
                }
            }
            else if (useLocal)
            {
                // Offline fallback: EffectChangelog. Same rendering shape as
                // before this refactor so the in-source structured form still
                // looks right when network is unavailable.
                var ordered = new List<ChangelogVersion>(pending);
                ordered.Sort((a, b) => b.Version.CompareTo(a.Version));
                for (int i = 0; i < ordered.Count; i++)
                {
                    var ver = ordered[i];
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = "v" + ver.Version.ToString(3) + (string.IsNullOrEmpty(ver.Title) ? "" : "  ·  " + ver.Title),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14,
                        Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6),
                    });
                    if (ver.Entries != null)
                    {
                        string lastGroup = null;
                        foreach (var entry in ver.Entries)
                        {
                            if (entry == null) continue;
                            // Group subheader (e.g. "New effects", "Bug fixes")
                            // whenever the group changes.
                            if (!string.IsNullOrEmpty(entry.Group) && entry.Group != lastGroup)
                            {
                                bodyStack.Children.Add(new TextBlock
                                {
                                    Text = entry.Group,
                                    FontWeight = FontWeights.SemiBold,
                                    FontSize = 13,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x86, 0x0B)),
                                    Margin = new Thickness(0, 10, 0, 2),
                                });
                                lastGroup = entry.Group;
                            }
                            if (!string.IsNullOrEmpty(entry.Headline))
                            {
                                bodyStack.Children.Add(new TextBlock
                                {
                                    Text = "• " + entry.Headline,
                                    TextWrapping = TextWrapping.Wrap,
                                    FontSize = 12,
                                    Margin = new Thickness(0, 4, 0, 0),
                                });
                            }
                            if (!string.IsNullOrEmpty(entry.Description))
                            {
                                bodyStack.Children.Add(new TextBlock
                                {
                                    Text = entry.Description,
                                    TextWrapping = TextWrapping.Wrap,
                                    FontSize = 11,
                                    Opacity = 0.7,
                                    Margin = new Thickness(14, 2, 0, 0),
                                });
                            }
                        }
                    }
                }
            }
            else
            {
                // Offline and no bundled changelog entry for this build: GitHub
                // notes are canonical but couldn't be fetched, so point the user
                // there instead of opening a blank modal.
                var curr = _plugin.UpdateChecker?.CurrentVersion;
                string ver = curr != null ? curr.ToString(3) : "this version";
                bodyStack.Children.Add(new TextBlock
                {
                    Text = "You're offline. The full release notes for v" + ver +
                           " are on the project's GitHub releases page once you're back online.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                });
            }

            var notesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Padding = new Thickness(12),
                Content = bodyStack,
            };
            root.Children.Add(notesScroll);

            win.Content = root;
            gotItBtn.Click += (_, __) => win.Close();
            win.Closed += (_, __) =>
            {
                _plugin.DismissChangelog();
                RefreshChangelogBanner();
            };

            win.ShowDialog();
        }

        // Dev/test: render arbitrary release-notes markdown through the exact
        // production path (RenderReleaseNotes) so the GitHub notes can be
        // previewed before publishing (the plugin can't fetch an unpublished
        // draft). Driven by the PREVIEW access code from clipboard text.
        private void ShowNotesPreview(string markdown)
        {
            var win = new Window
            {
                Title = "What's new (notes preview)",
                Width = 600,
                Height = 480,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApplyDarkTheme(win);

            var root = new DockPanel { Margin = new Thickness(16) };
            var header = new TextBlock
            {
                Text = "What's new (preview of the GitHub notes render)",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            var close = new System.Windows.Controls.Button { Content = "Close", Width = 100, Height = 28, IsDefault = true, IsCancel = true };
            close.Click += (_, __) => win.Close();
            footer.Children.Add(close);
            root.Children.Add(footer);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Padding = new Thickness(12),
                Content = RenderReleaseNotes(markdown),
            };
            root.Children.Add(scroll);

            win.Content = root;
            win.ShowDialog();
        }

        // Open a file picker for USBPcapCMD.exe and tell the plugin to persist
        // it as the override path. Filtered to USBPcapCMD.exe specifically: the
        // file the FFB tap actually invokes. After Apply, the plugin restarts
        // the tap so the new path takes effect immediately.
        private void UsbPcapBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title       = "Locate USBPcapCMD.exe",
                Filter      = "USBPcapCMD.exe|USBPcapCMD.exe|All executables (*.exe)|*.exe",
                FileName    = "USBPcapCMD.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            string leaf = System.IO.Path.GetFileName(path);
            if (!string.Equals(leaf, "USBPcapCMD.exe", StringComparison.OrdinalIgnoreCase))
            {
                TrueforceDialog.Show(null, "Trueforce For All",
                    "That file isn't USBPcapCMD.exe. Pick USBPcapCMD.exe from your USBPcap install folder.",
                    DialogKind.Warning);
                return;
            }

            _plugin.ApplyUsbPcapPathOverride(path);
        }

        // Launch the bundled USBPcap installer. Confirms first because it
        // triggers a UAC prompt and modifies a kernel driver. The plugin
        // runs the install + tap restart on a background thread; the
        // FFB pass-through status will update through the normal tick.
        private void UsbPcapReinstall_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (TrueforceDialog.Show(null, "Trueforce For All",
                    "Run the bundled USBPcap installer? This needs admin (UAC prompt) and reinstalls the USB capture driver. SimHub doesn't need to restart afterwards.",
                    DialogKind.Confirm, okLabel: "Run installer", cancelLabel: "Cancel") != true)
                return;
            _plugin.ReinstallUsbPcapAsync();
        }
    }
}
