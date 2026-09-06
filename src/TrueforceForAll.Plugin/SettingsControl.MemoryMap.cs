// The Memory Mapping tab.
//
// Hidden until the access code MEMMAP is typed, and hidden again on the next
// launch: the unlock is deliberately session-only, so a machine that ran one
// scan does not carry a developer tab around forever.
//
// It drives memscan.exe through ScanHost, which the plugin owns. Everything
// arriving from the scanner comes in on a reader thread and is marshalled here
// before it touches a control; nothing on this tab blocks the UI thread.
//
// The reason the tab exists rather than a console window: the scanner counts
// the driver through a timed script, and the driver is at the wheel. So the
// same cue that lands in the live box below also goes to the wheel's OLED and,
// for the last few seconds of a phase, to the rev lights. When the screen is
// not available the tab says so instead of pretending.
//
// Its own partial file because SettingsControl.xaml.cs is past 13,000 lines.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        // Session-only on purpose (see the file header). Nothing in
        // TrueforceSettings, so nothing to project into a backup either.
        private bool _memMapUnlocked;

        private DispatcherTimer _memMapTimer;
        private DispatcherTimer _memTargetDebounce;
        private TrueforcePlugin.ScanTarget _memMapTarget;

        // The phase currently being counted through, as the scanner last said
        // it. DateTime rather than a tick count because the arithmetic is in
        // whole seconds and the values are never large enough to wrap.
        private DateTime _memPhaseStartUtc;
        private double   _memPhaseSeconds;
        private string   _memPhaseCue = "";
        private bool     _memPhaseLive;
        // A run is in flight. Separate from _memPhaseLive on purpose: the first
        // thirty seconds of a run are a warm-up and a narrowing watch with no
        // phase at all, and a scanner that dies in there has to be reported too.
        private bool     _memRunLive;
        private int      _memLastSecondShown = -1;
        private DateTime _memLastCuePushUtc = DateTime.MinValue;

        private string _memCeilingLine = "";
        private string _memStageLine   = "";
        // Kept apart from the stage line because the two arrive out of order:
        // the abort's own verdict and done events land while the background
        // wait is still going, and whichever wrote last would otherwise erase
        // the other.
        private string _memStopLine    = "";
        private string _memOutDir;

        // ---- the session ----
        //
        // One recorded drive, mined many times. What the tab keeps about the
        // recording in flight: when it started, when it ended, how much the
        // scanner says it has written, and the tallies frozen at the end so the
        // summary line survives the next run resetting the plugin's counters.
        private const int MemSessionCapSeconds = 1800;
        private DateTime _memSessionStartedUtc = DateTime.MinValue;
        private DateTime _memSessionEndedUtc   = DateTime.MinValue;
        private double   _memSessionMb;
        private int      _memSessionMarks;
        private int      _memSessionShifts;
        private bool     _memMarkNamesReady;
        /// <summary>A mark just landed: its name goes on the wheel for two
        /// seconds, so a press at the rim is seen to have landed without a
        /// screen.</summary>
        private DateTime _memMarkFlashUntilUtc = DateTime.MinValue;
        private string   _memMarkFlashName = "";

        // A Stop is in flight: the abort has been sent and the scanner is being
        // given its three seconds. Suppresses the "it died on its own" line,
        // which would be a lie about a stop we asked for.
        private bool _memAborting;

        // What the reverse channel has carried, counted per command kind, so
        // the tab can say "4 shifts, 1 mark" rather than only ever showing the
        // last one. A shift that never landed is invisible otherwise, and the
        // whole gear search rests on those presses arriving.
        private readonly Dictionary<string, int> _memAckCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private string _memLastAckLine = "";

        /// <summary>Watch rows the scanner refused outright, which is what its
        /// list being full looks like from here. Counted rather than only logged:
        /// a row that is not being read cannot be ruled in or out, and it is
        /// indistinguishable from one that was judged and kept.</summary>
        private int _memWatchRefused;

        // What the wheel shows BEFORE the script starts. The first 30 seconds or
        // so of a run are a warm-up and a narrowing watch, with no phase events
        // at all, and the narrowing instruction is the most important one of the
        // whole run: every filter in it assumes the engine speed is changing. A
        // driver who cannot see this tab has to be told that at the wheel.
        private string _memWheelStageCue = "";
        private int    _memStageSecondsLeft;

        // One row per predicate id, so a predicate reported twice replaces its
        // row instead of stacking a second copy under it.
        private readonly Dictionary<string, TextBlock> _memLedgerRows =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        // What the scan threw away, in its own words. Kept as a list rather than
        // one line because a run can truncate more than one thing, and kept
        // AFTER the run ends: a warning that scrolls past while the operator is
        // at the wheel has told nobody anything. The scanner writes these to its
        // report file, which is exactly where they were being missed.
        private readonly List<string> _memWarnLines = new List<string>();

        /// <summary>Warnings past what the box will hold. A run carrying a dozen
        /// texts can raise two each, and eight is the most worth reading; the
        /// number that did not fit is still said, because a warning list that
        /// truncated itself silently would be the very thing it warns about.</summary>
        private int _memWarnHidden;

        private EventHandler<ScanEvent> _memScanEventHandler;
        private EventHandler            _memScanExitedHandler;
        // SimHub raises this whenever a binding is added, changed or deleted
        // anywhere, which is what makes the "bound yet?" line answer as soon as
        // the operator binds a paddle rather than on the next Look again.
        private EventHandler            _memInputMappingsHandler;

        private static readonly Brush MemPassBrush   = new SolidColorBrush(Color.FromRgb(0x7C, 0xCB, 0x7C));
        private static readonly Brush MemFailBrush   = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
        private static readonly Brush MemSkipBrush   = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly Brush MemAmberBrush  = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
        // Blue, and blue is used nowhere else on this tab, so a lit row reads as
        // "this one just moved" rather than as a pass or a warning.
        private static readonly Brush MemChangedBrush =
            new SolidColorBrush(Color.FromArgb(0x59, 0x4F, 0xA3, 0xE3));

        // ---- visibility -----------------------------------------------------

        /// <summary>Show or hide the tab, and never leave the selection stranded
        /// on it. Same shape as ApplyLightsyncTabVisibility.</summary>
        private void ApplyMemoryMapTabVisibility()
        {
            if (MemoryMapTab == null) return;
            bool on = _memMapUnlocked;
            if (!on)
            {
                // A scan is the tab's only activity, so the tab going away ends
                // it. Leaving a child process reading a game with no surface to
                // stop it from is the one outcome worth ruling out.
                //
                // Asked to stop rather than killed, even here: the run keeps
                // whatever it had, and the request costs nothing because the
                // waiting happens on a worker thread. There is no tab left to
                // report to, so the callback only clears the flag; without it
                // the tab would come back with its buttons dead, still waiting
                // on a scanner that stopped minutes ago.
                _memRunLive = false;
                _memAborting = true;
                try
                {
                    _plugin?.StopMemoryScanPolitely((clean, detail) =>
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            _memAborting = false;
                            SyncMemMapButtons();
                        })));
                }
                catch { _memAborting = false; }
                DetachScanHandlers();
                StopMemMapTimer();
                StopWatchTimers();
                if (MainTabs != null && ReferenceEquals(MainTabs.SelectedItem, MemoryMapTab))
                    MainTabs.SelectedItem = TelemetryFfbTab ?? MainTabs.Items[0];
            }
            MemoryMapTab.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            AttachBindingWatch(on);
            if (on)
            {
                RefreshMemoryMapTab();
                // Again once the tab has actually been built. The binding
                // editors have no model until they load, and a tab that is
                // opened and looked at without anything being touched would
                // otherwise sit on "bindings not read yet" for the whole
                // session.
                try
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        RefreshShiftBindingStatus();
                        RefreshMemMapPlan();
                    }), DispatcherPriority.Loaded);
                }
                catch { }
            }
        }

        /// <summary>Follow SimHub's binding list while the tab is up, and let go
        /// of it when the tab goes away. The tab is the only thing that cares
        /// whether a shift button has been bound, and a handler left on a
        /// panel-scoped object outlives the panel.</summary>
        private void AttachBindingWatch(bool on)
        {
            // STATIC on SimHub's side, which is why letting go of it matters
            // more than usual: a handler left on a static event keeps this
            // control, the whole settings panel and everything they reference
            // alive for the rest of the session, once per open.
            if (_memInputMappingsHandler != null)
            {
                try { SimHub.Plugins.PluginManager.InputMappingsChanged -= _memInputMappingsHandler; } catch { }
                _memInputMappingsHandler = null;
            }
            if (!on) return;
            _memInputMappingsHandler = (s, e) =>
            {
                // Raised off whichever thread noticed the change, so it is
                // marshalled rather than touching the control directly.
                try { Dispatcher.BeginInvoke((Action)RefreshShiftBindingStatus); } catch { }
            };
            try { SimHub.Plugins.PluginManager.InputMappingsChanged += _memInputMappingsHandler; }
            catch { _memInputMappingsHandler = null; }
        }

        /// <summary>The MEMMAP access code. Returns the line for the status
        /// box.</summary>
        private string ToggleMemoryMapTab(bool on)
        {
            _memMapUnlocked = on;
            ApplyMemoryMapTabVisibility();
            if (on && MainTabs != null && MemoryMapTab != null)
                MainTabs.SelectedItem = MemoryMapTab;
            return on
                ? "Memory Map tab unlocked. It reads a running game's memory and never writes to it. "
                  + "Hidden again on the next launch, or type MEMMAP OFF now."
                : "Memory Map tab hidden, and any running scan stopped.";
        }

        // ---- filling the tab in ---------------------------------------------

        /// <summary>Re-read everything the tab shows before you press Start: is
        /// the scanner deployed, and what is running.</summary>
        private void RefreshMemoryMapTab()
        {
            if (MemoryMapTab == null || _plugin == null) return;

            string exe = _plugin.MemoryScanExePath();
            bool have = exe != null;
            // The exe alone is not a deployment. memscan is framework-dependent,
            // so it needs its three sibling files as well, and copying only the
            // exe fails inside the .NET host before a single event line is
            // written: a silent start, then an exit with nothing to show.
            string incomplete = have ? MemoryScanDeployGap(exe) : null;
            bool ready = have && incomplete == null;
            if (MemMapMissingBox != null)
                MemMapMissingBox.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            if (!ready && MemMapMissingText != null)
            {
                var paths = _plugin.MemoryScanSearchPaths();
                MemMapMissingText.Text =
                    incomplete != null
                        ? "memscan.exe is here but its deployment is incomplete: " + incomplete
                          + " Copy the whole build folder, not just the exe. It also needs the "
                          + ".NET 8 runtime installed on this PC."
                    : paths.Count == 0
                        ? "memscan.exe could not be looked for: the plugin cannot work out its own folder."
                        : "memscan.exe is not built by this project and has to be copied in by hand. "
                          + "Looked in:\n" + string.Join("\n", paths);
            }

            RefreshMemMapTarget();
            // The watch list outlives every run and the tab itself, so it is
            // drawn from the saved list each time the tab is looked at rather
            // than only when something adds to it.
            RebuildWatchRows();
            // The map outlives all of it, including SimHub. Drawn here for the
            // same reason, and this is where a saved map is re-read, its entries
            // re-resolved and sanity checked, and anything that no longer makes
            // sense marked stale rather than believed.
            RefreshFieldFrame();
            SyncMemMapButtons();
        }

        /// <summary>Null when the scanner looks properly deployed, else which
        /// of its sibling files is missing.</summary>
        private static string MemoryScanDeployGap(string exe)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) return null;
                foreach (string need in new[] { "memscan.dll", "memscan.runtimeconfig.json", "memscan.deps.json" })
                    if (!System.IO.File.Exists(System.IO.Path.Combine(dir, need)))
                        return need + " is not beside it.";
            }
            catch { }
            return null;
        }

        private void RefreshMemMapTarget()
        {
            if (_plugin == null || MemMapTargetText == null) return;

            // A typed name wins: it is the fallback for exactly the case where
            // detection cannot see the cabinet.
            string typed = MemMapProcessBox?.Text;
            if (!string.IsNullOrWhiteSpace(typed))
            {
                var manual = _plugin.FindScanTargetByName(typed);
                _memMapTarget = manual;
                MemMapTargetText.Text = manual != null
                    ? $"{manual.ProcessName}.exe, pid {manual.Pid.ToString(CultureInfo.InvariantCulture)} (named below)."
                      + ElevationNote()
                    : $"Nothing called \"{typed.Trim()}\" is running.";
                PointMapAtTarget();
                return;
            }

            _memMapTarget = _plugin.DetectArcadeScanTarget();
            MemMapTargetText.Text = _memMapTarget != null
                ? $"{_memMapTarget.DisplayName}: {_memMapTarget.ProcessName}.exe, pid "
                  + _memMapTarget.Pid.ToString(CultureInfo.InvariantCulture) + "." + ElevationNote()
                : "No arcade cabinet found running. Start the game, then press Look again, "
                  + "or name the process yourself below.";
            // The map belongs to the process, so the target deciding is what
            // decides which map is open. A pid that changed is a relaunch, and a
            // relaunch is the moment every entry has to earn "live" again: that
            // is the whole thing a module offset or a pointer path claims to
            // survive, and an entry still marked live from the last launch would
            // be the one lie this cannot afford.
            PointMapAtTarget();
        }

        /// <summary>Said before the drive, not after it. Reading another
        /// process needs the same rights the FFB tap does, and a session spent
        /// driving a script only to come back to "access denied" is the one
        /// failure here that costs real time.</summary>
        private string ElevationNote()
            => _plugin?.IsRunningElevated == false
                ? "\nSimHub is not running as administrator, so the scanner will probably be "
                  + "refused when it tries to read the game. Turn on SimHub's own run-as-administrator "
                  + "setting and restart it first."
                : "";

        private void SyncMemMapButtons()
        {
            bool running = _plugin?.MemoryScanRunning == true || _memAborting;
            string exe   = _plugin?.MemoryScanExePath();
            bool have    = exe != null && MemoryScanDeployGap(exe) == null;
            bool ready   = have && !running && _memMapTarget != null;
            // A box that was filled in wrongly stops the run rather than being
            // quietly dropped: driving a session in the belief you declared
            // something is exactly the mistake this tab has to prevent.
            bool clean   = KnownValueProblem() == null;
            if (MemMapStartButton != null) MemMapStartButton.IsEnabled = ready && clean;
            // The survey needs the same scanner and the same target, and one scan
            // at a time, so it is gated exactly as the hunt is. It performs no
            // search of its own, so a bad known value cannot spoil it. Its OWN
            // box can: a census row limit the scanner refuses comes back as exit
            // code 2, which reads on this tab as "your memscan is too old".
            CensusStaticCap(out string censusIssue);
            if (MemMapSurveyButton != null) MemMapSurveyButton.IsEnabled = ready && censusIssue == null;
            // The search button lives with the map, where the values are, and
            // it is gated by what is actually filled in. PaintSearchPlanLine
            // owns it, for the same reason the plan sentence is built from the
            // plan: one place decides whether there is anything to search for.
            if (MemMapStopButton  != null) MemMapStopButton.IsEnabled  = running && !_memAborting;
            if (MemMapScriptCombo != null) MemMapScriptCombo.IsEnabled = !running;
            // The session: one button that records, and while a recording is
            // in flight the same button stops it. Any OTHER run in flight
            // disables it, because the scanner does one thing at a time.
            bool sessionLive = _plugin?.MemoryScanRunning == true && _memMapRunKind == MemMapRunKind.Session;
            if (MemMapSessionButton != null)
            {
                MemMapSessionButton.IsEnabled = have && _memMapTarget != null && !_memAborting
                                             && (!running || sessionLive);
                string want = sessionLive ? "Stop recording" : "Record a session";
                if (!string.Equals(MemMapSessionButton.Content as string, want, StringComparison.Ordinal))
                    MemMapSessionButton.Content = want;
            }
            // A mark from the tab is the same mark the bound button sends, and it
            // needs a scanner to land on.
            if (MemMapMarkNowButton != null) MemMapMarkNowButton.IsEnabled = _plugin?.MemoryScanRunning == true;
            EnsureMarkNames();
            RefreshSessionStatus();
            if (MemMapProcessBox != null) MemMapProcessBox.IsEnabled = !running;
            if (MemMapRefreshTargetButton != null) MemMapRefreshTargetButton.IsEnabled = !running;
            // The declared values are read once, when the run starts. Letting
            // them be edited afterwards would show one thing and have sent
            // another. The field rows' own boxes are frozen by
            // PaintSearchPlanLine, which is where they are drawn.
            if (MemMapGearOff   != null) MemMapGearOff.IsEnabled   = !running;
            if (MemMapGearLive  != null) MemMapGearLive.IsEnabled  = !running;
            if (MemMapGearTyped != null) MemMapGearTyped.IsEnabled = !running;
            RefreshGearSequenceLine();
            // The seed cap belongs to whichever gear search runs, so it follows
            // the two of them and not the sequence box.
            if (MemMapGearSeedCapBox != null)
                MemMapGearSeedCapBox.IsEnabled = !running && GearSearchMode() != MemGearMode.Off;
            // Same rule as every other box that goes on a command line: editable
            // until a run has taken its value, then frozen, so what is on screen
            // is what was sent.
            if (MemMapCensusCapBox != null) MemMapCensusCapBox.IsEnabled = !running;
            // Deliberately NOT gated on the run: a row added while a scan is in
            // flight is armed on that scan, and mid-run is exactly when the
            // operator wants to add one.
            SyncWatchButtons();
            RefreshWatchStatus();
            // Same rule for the map: a round can only be judged while something
            // is reading, and a pointer path can only be asked for while the
            // scanner is up, so both follow the run.
            SyncFieldButtons();
            RefreshShiftBindingStatus();
            RefreshMemMapPlan();
        }

        // ---- known values ----------------------------------------------------
        //
        // Things the operator can supply that the scan cannot work out for
        // itself, and each is stronger evidence than anything behaviour can
        // prove. Each is passed to the scanner ONLY when it has been asked for,
        // and the plan box says which of them that came to before anybody drives.

        /// <summary>Which of the three gear paths the tab is set to.</summary>
        private enum MemGearMode
        {
            /// <summary>Not looking for the gear at all.</summary>
            Off,
            /// <summary>Derived from the shift presses. The normal path: the
            /// bindings are already sending every press with its direction, and
            /// an unknown starting gear is the same constant offset the search
            /// solves for anyway, so nothing is declared and nothing can be
            /// declared wrongly.</summary>
            Live,
            /// <summary>A typed sequence. The override, for a session where the
            /// shift buttons cannot be bound.</summary>
            Typed,
        }

        private MemGearMode GearSearchMode()
        {
            if (MemMapGearTyped?.IsChecked == true) return MemGearMode.Typed;
            if (MemMapGearOff?.IsChecked   == true) return MemGearMode.Off;
            // Live is the default, and the default when the radios have not been
            // created yet, which is what the tab is set to in the XAML.
            return MemGearMode.Live;
        }

        /// <summary>What one search would look for, taken from the map's own
        /// rows. THE fields carry the values now, so there is one place a run
        /// and the sentence describing it both read from.</summary>
        private MemoryFieldSearch KnownSearchPlan()
            => Store?.PlanSearch() ?? new MemoryFieldSearch();

        /// <summary>What the declared-gear override would use, said where the
        /// override is chosen. The sequence itself is typed on the Gear row up
        /// in the map with every other value, and a second box here could
        /// disagree with the first.</summary>
        private void RefreshGearSequenceLine()
        {
            if (MemMapGearSeqText == null) return;
            var gearField = Store?.Find(MemoryFields.Gear);
            string seq = gearField?.KnownValue;
            bool typed = GearSearchMode() == MemGearMode.Typed;
            string line;
            if (!string.IsNullOrWhiteSpace(seq))
                line = typed
                    ? "The gears to declare: " + seq + ". Edit them on the Gear row up in the map."
                    : "The Gear row up in the map says " + seq
                      + ", which is NOT used while the gear is set the way it is above. Clear that row, or "
                      + "pick the override, so what is on screen is what will be searched for.";
            else
                line = typed
                    ? "The Gear row up in the map is empty, so there is nothing to declare. Write the gears "
                      + "in its box, or go back to driving and shifting."
                    : "Nothing declared, which is the stronger path.";
            if (!string.Equals(MemMapGearSeqText.Text, line, StringComparison.Ordinal))
                MemMapGearSeqText.Text = line;
            MemMapGearSeqText.Foreground = typed && string.IsNullOrWhiteSpace(seq) ? MemAmberBrush : MemSkipBrush;
        }

        /// <summary>The gear sequence, normalised to "1,2,3,4,3,2", or null when
        /// the override is not selected or the Gear row is empty. Non-null
        /// "problem" means it was declared and cannot be used.
        ///
        /// Gated on the MODE, not merely on the row having a value: the row is a
        /// value like any other, and a sequence left in it from last time must
        /// not turn a drive-and-shift run into a declared one behind the
        /// operator's back.</summary>
        private string KnownGearSequence(out string problem)
        {
            problem = null;
            if (GearSearchMode() != MemGearMode.Typed) return null;
            var plan = KnownSearchPlan();
            if (!string.IsNullOrEmpty(plan.GearSequence)) return plan.GearSequence;
            foreach (string p in plan.Problems)
                if (p.IndexOf("gear", StringComparison.OrdinalIgnoreCase) >= 0) { problem = p; return null; }
            problem = "The gears are set to be declared, but the Gear row up in the map is empty. "
                    + "Write the gears you are about to drive in its box, or switch to driving and shifting.";
            return null;
        }

        /// <summary>The one fixed number this run searches for, in whole units,
        /// or 0 when no field has one. Non-null "problem" means a field has one
        /// that cannot be used.
        ///
        /// The scanner takes ONE. Which field it came from is the plan's answer,
        /// not this method's, and the plan says out loud when a second field had
        /// one that could not ride along.</summary>
        private int KnownRedline(out string problem)
        {
            // The plan has already judged every value, and FirstValueProblem is
            // what reports a bad one. Judging it a second time here would be a
            // second opinion that could disagree with the row.
            problem = null;
            return StaticFromPlan(KnownSearchPlan());
        }

        /// <summary>The one fixed number a plan carries, in whole units.</summary>
        private static int StaticFromPlan(MemoryFieldSearch plan)
        {
            if (plan == null || !plan.HasStatic) return 0;
            double v = plan.StaticValue;
            if (v <= 0 || v > int.MaxValue) return 0;
            return (int)Math.Round(v);
        }

        /// <summary>How many candidate slots the gear seed may hold, or 0 for
        /// the scanner's own limit. Non-null "problem" means it was filled in and
        /// cannot be used.
        ///
        /// It exists because when the seed cap bites, the highest addresses are
        /// the ones dropped, and the answer may be among them. A run that says so
        /// is only useful if the number can be raised without editing anything,
        /// which is what this box is.</summary>
        private long GearSeedCap(out string problem)
        {
            problem = null;
            string raw = MemMapGearSeedCapBox?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            // Separators are allowed because the scanner PRINTS the number it
            // wants with them, and the obvious thing to do with "3,145,728" is
            // paste it back in.
            string cleaned = raw.Trim().Replace(",", "").Replace(" ", "").Replace("_", "");
            if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n <= 0)
            {
                problem = "The seed cap has to be a whole number of slots, like 20000000, "
                        + "or empty to use the scanner's own limit.";
                return 0;
            }
            if (n < 65536)
            {
                problem = "A seed cap of " + n.ToString("N0", CultureInfo.InvariantCulture)
                        + " slots is smaller than the sweep's own first block, so it would drop almost "
                        + "everything. The scanner never goes below 65,536.";
                return 0;
            }
            if (n > 2000000000L)
            {
                problem = "A seed cap of " + n.ToString("N0", CultureInfo.InvariantCulture)
                        + " slots is past what the table can hold. Two billion is the ceiling.";
                return 0;
            }
            return n;
        }

        /// <summary>How many never-moving values the census may list, or 0 for
        /// the scanner's own limit.
        ///
        /// Separators allowed for the same reason the seed cap allows them: the
        /// scanner PRINTS the number it wants and the obvious thing to do with
        /// it is paste it back. The scanner itself parses a plain integer, so
        /// they are stripped here rather than passed on.</summary>
        private long CensusStaticCap(out string problem)
        {
            problem = null;
            string raw = MemMapCensusCapBox?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string cleaned = raw.Trim().Replace(",", "").Replace(" ", "").Replace("_", "");
            if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n <= 0)
            {
                problem = "Census rows has to be a whole number, like 500000, or empty for the "
                        + "scanner's own limit of 200,000.";
                return 0;
            }
            // The scanner takes this as an int. Anything past that would be
            // rejected on the command line as a usage error, which reads on this
            // tab as "your memscan is too old" and is not.
            if (n > int.MaxValue)
            {
                problem = "Census rows tops out at " + int.MaxValue.ToString("N0", CultureInfo.InvariantCulture)
                        + ". Every row is about 150 bytes, so even a tenth of that is a file of a few hundred "
                        + "megabytes.";
                return 0;
            }
            return n;
        }

        /// <summary>How many shift presses a press-driven gear search waits for.
        ///
        /// It is the length of the session AND the strength of the evidence, and
        /// it is stated rather than left to a default nobody can see: the
        /// scanner ends the search on the last press and then waits, patiently
        /// and for minutes, for one that is never coming. Six presses left four
        /// survivors out of twenty million on the harness, so eight is a
        /// comfortable margin at about thirty seconds of ordinary driving.
        ///
        /// Named here so the plan box, the ground-truth line and the argument
        /// this end builds all say the same number.</summary>
        private const int MemGearPresses = 8;

        /// <summary>The first thing wrong with what has been typed, or null.
        ///
        /// The field rows are asked FIRST, because they are where the values
        /// live now: a redline typed as 74 has to stop the run here rather than
        /// be dropped from the command line and waited for.</summary>
        private string KnownValueProblem()
        {
            string valueProblem = Store?.FirstValueProblem();
            if (valueProblem != null) return valueProblem;
            KnownGearSequence(out string gearProblem);
            if (gearProblem != null) return gearProblem;
            GearSeedCap(out string capProblem);
            return capProblem;
        }

        /// <summary>Say what Start is going to do, in the same words the boxes
        /// use. Driving a whole session in the belief you declared something you
        /// left blank is the one mistake here that costs a real session, so this
        /// lists what WILL run and what will not, and never abbreviates the
        /// blanks away.</summary>
        private void RefreshMemMapPlan()
        {
            if (MemMapPlanText == null) return;

            string problem = KnownValueProblem();
            if (problem != null)
            {
                MemMapPlanText.Text = problem + "\nFix it or clear the box; nothing will start until then.";
                MemMapPlanText.Foreground = MemAmberBrush;
                return;
            }
            // ClearValue, not a brush of our own: the panel is themed and the
            // plain text colour belongs to the theme.
            MemMapPlanText.ClearValue(TextBlock.ForegroundProperty);

            var plan = KnownSearchPlan();
            var gearMode = GearSearchMode();
            string gears = KnownGearSequence(out _);
            int redline = KnownRedline(out _);
            long seedCap = GearSeedCap(out _);
            bool known = plan.AnyText || gearMode != MemGearMode.Off || redline > 0;

            var lines = new List<string>();
            // WHICH OF THE TWO RUNS, first and plainly. They cannot share one: a
            // known-value search returns before the scanner's recorder starts,
            // so it performs no script, keeps no predicate ledger and writes no
            // recording. Listing "the driving script, AND the car name, AND the
            // gears" described a run that has never been possible, and the gear
            // box arrives filled in, so that was the DEFAULT thing to read here.
            lines.Add(known
                ? "A known-value run. What you have asked for is read straight out of memory; the driving "
                  + "script " + SelectedMemMapScript() + " does NOT run, because those are two different "
                  + "runs. Clear the values on the map's rows, and set the gear to \"Do not look for the "
                  + "gear\", to drive the script instead."
                : "The driving script " + SelectedMemMapScript()
                  + ", and nothing known. Ask for something instead: what you can read off the screen, or "
                  + "the shift buttons you are already pressing, beats anything a script can work out.");

            // Every text value on the map, by the field it belongs to, because
            // one sweep now carries all of them and each field gets its own
            // findings back.
            if (plan.Needles.Count == 0)
                lines.Add("No text search: no row up in the map has a value in it.");
            else
            {
                var bits = new List<string>();
                foreach (var n in plan.Needles) bits.Add(n.FieldName + " = \"" + n.Text + "\"");
                lines.Add((plan.Needles.Count == 1 ? "One text search, " : plan.Needles.Count
                            .ToString(CultureInfo.InvariantCulture) + " text searches in ONE sweep, ")
                        + string.Join(", ", bits)
                        + ". Each is searched in four encodings, then whatever points at it, and each "
                        + "field keeps its own candidates.");
            }

            // The gear line has to make plain WHICH of the two happens when
            // Start is pressed, because they ask completely different things of
            // the person at the wheel: one waits for them, the other only wants
            // them to drive.
            switch (gearMode)
            {
                case MemGearMode.Live:
                    lines.Add("The gear, from your shift presses. Nothing is declared: drive normally, shift with "
                            + "the bound buttons, and the sequence is worked out from what you actually pressed. "
                            + "It ends after " + MemGearPresses.ToString(CultureInfo.InvariantCulture)
                            + " presses, and until then it waits, so press Stop if you want to finish on fewer. "
                            + ShiftBindingPlanNote());
                    break;
                case MemGearMode.Typed:
                    lines.Add("The gear sequence " + (gears ?? "?") + ", declared. You are asked for one gear at a "
                            + "time: select it, then press a bound shift button to say you are there. "
                            + ShiftBindingPlanNote());
                    break;
                default:
                    lines.Add("No gear search: it is set to \"Do not look for the gear\".");
                    break;
            }

            if (gearMode != MemGearMode.Off)
                lines.Add(seedCap > 0
                    ? "The gear seed is capped at " + seedCap.ToString("N0", CultureInfo.InvariantCulture)
                      + " slots, as typed."
                    : "The gear seed uses the scanner's own cap. If the run warns that it truncated, put a bigger "
                      + "number in the seed cap box and run it again.");

            lines.Add(plan.HasStatic
                ? "The fixed number " + redline.ToString(CultureInfo.InvariantCulture)
                  + " for " + plan.StaticFieldName
                  + (plan.AnyText
                     ? ", measured from whatever points at the text above, so the tightest tier can run and the "
                       + "answer says which tier it came from."
                     : ". With no text value to measure from there is nothing to be near, so only the widest "
                       + "tier runs and every hit is weak. Fill in a text row as well, usually the car.")
                : "No fixed-number search: no row up in the map has one filled in.");
            if (plan.StaticNotThisRun.Count > 0)
                lines.Add("NOT searched this run: " + string.Join(", ", plan.StaticNotThisRun)
                        + ". The scanner searches one fixed number at a time, so run them one after the other.");
            if (plan.NeedlesNotThisRun.Count > 0)
                lines.Add("NOT searched this run: " + string.Join(", ", plan.NeedlesNotThisRun)
                        + ". The scanner takes "
                        + MemoryValueKinds.MaxTextNeedles.ToString(CultureInfo.InvariantCulture)
                        + " text searches at once.");

            MemMapPlanText.Text = string.Join("\n", lines);
        }

        private string SelectedMemMapScript()
        {
            var item = MemMapScriptCombo?.SelectedItem as ComboBoxItem;
            string s = item?.Tag as string;
            return string.IsNullOrEmpty(s) ? "rpm-v1" : s;
        }

        // ---- the shift bindings ----------------------------------------------
        //
        // A gear hunt now rests entirely on these presses arriving, and when
        // they do not, nothing goes wrong LOUDLY: the run looks busy, the tab
        // looks alive, and the operator is at the wheel pulling a paddle that is
        // bound to nothing. So the tab says two things it did not before,
        // whether the buttons are bound at all, and how many presses have
        // actually gone down the wire this run.
        //
        // Read from SimHub's own binding rows through the editor controls that
        // are already on the tab, rather than a list of our own: they are the
        // same rows the operator edits an inch further up, so they cannot drift
        // apart.

        /// <summary>How many bindings an editor is showing: 0 for none, and -1
        /// when it cannot be told yet, which is a real third answer. A tab that
        /// has never been looked at has not created its editors, and reporting
        /// that as "not bound" would frighten someone whose paddles are fine.</summary>
        private static int BindingCount(SimHub.Plugins.UI.ControlsEditor editor)
        {
            try
            {
                var model = editor?.Model;
                if (model == null) return -1;
                var triggers = model.Triggers;
                return triggers == null ? 0 : triggers.Count;
            }
            catch { return -1; }
        }

        /// <summary>One clause for the plan box: whether the presses the gear
        /// search is about to rely on have anything to come from.</summary>
        private string ShiftBindingPlanNote()
        {
            int up = BindingCount(MemMapShiftUpEditor);
            int down = BindingCount(MemMapShiftDownEditor);
            if (up < 0 || down < 0) return "";
            if (up == 0 && down == 0)
                return "NEITHER shift button is bound yet, so no press can reach the scan and it will find nothing. "
                     + "Bind them below first.";
            if (up == 0)  return "Shift up is not bound, so only downshifts will reach the scan.";
            if (down == 0) return "Shift down is not bound, so only upshifts will reach the scan.";
            return "";
        }

        /// <summary>The line under the bindings: bound or not, and the tally of
        /// what has actually been sent this run.</summary>
        private void RefreshShiftBindingStatus()
        {
            if (MemMapShiftStatusText == null) return;

            int up = BindingCount(MemMapShiftUpEditor);
            int down = BindingCount(MemMapShiftDownEditor);
            int mark = BindingCount(MemMapRaceStartEditor);
            int markBtn = BindingCount(MemMapMarkEditor);

            var parts = new List<string>();
            bool unknown = up < 0 || down < 0;
            bool missing = !unknown && (up == 0 || down == 0);
            if (unknown)
                parts.Add("Bindings not read yet.");
            else if (up == 0 && down == 0)
                parts.Add("Shift up and shift down are NOT BOUND. Nothing will reach the scan.");
            else if (up == 0)
                parts.Add("Shift down is bound; shift up is NOT BOUND.");
            else if (down == 0)
                parts.Add("Shift up is bound; shift down is NOT BOUND.");
            else
                parts.Add("Shift up and shift down are bound.");
            if (!unknown && mark == 0) parts.Add("Race started is not bound (optional).");
            if (markBtn == 0)
                parts.Add("The mark button is not bound, so during a session marks can only be made with "
                        + "Mark now on this tab.");

            // The tally, and it is the half that catches a binding that exists
            // and is still sending nowhere.
            int sentUp = _plugin?.MemoryScanShiftUpSent ?? 0;
            int sentDown = _plugin?.MemoryScanShiftDownSent ?? 0;
            int sent = sentUp + sentDown;
            int dropped = _plugin?.MemoryScanPressesDropped ?? 0;
            _memAckCounts.TryGetValue("shift", out int acked);
            if (_memRunLive || sent > 0)
            {
                parts.Add(sent == 0
                    ? "No shift press has been sent this run."
                    : sent.ToString(CultureInfo.InvariantCulture) + " shift presses sent this run ("
                      + sentUp.ToString(CultureInfo.InvariantCulture) + " up, "
                      + sentDown.ToString(CultureInfo.InvariantCulture) + " down), "
                      + acked.ToString(CultureInfo.InvariantCulture) + " confirmed by the scanner.");
            }
            int marks = _plugin?.MemoryScanMarksSent ?? 0;
            if (marks > 0)
            {
                _memAckCounts.TryGetValue("mark", out int marksAcked);
                parts.Add(marks.ToString(CultureInfo.InvariantCulture) + (marks == 1 ? " mark" : " marks")
                        + " sent this run, " + marksAcked.ToString(CultureInfo.InvariantCulture)
                        + " confirmed by the scanner.");
            }
            if (dropped > 0)
                parts.Add(dropped.ToString(CultureInfo.InvariantCulture)
                        + " press(es) arrived with no scan running and went nowhere.");

            MemMapShiftStatusText.Text = string.Join("  ", parts);
            // Amber for a binding that is missing, and for a gear run that has
            // been going long enough to have seen a press and has seen none.
            // Not immediately: the first seconds of every run have no presses in
            // them, and a warning that is always on at the start is one nobody
            // reads at the end.
            bool silent = _memPressesExpected && _memRunLive && sent == 0
                       && _memWatchRunStartedUtc != DateTime.MinValue
                       && (DateTime.UtcNow - _memWatchRunStartedUtc) > TimeSpan.FromSeconds(25);
            if (missing || (!unknown && up == 0 && down == 0) || silent)
                MemMapShiftStatusText.Foreground = MemAmberBrush;
            else
                MemMapShiftStatusText.ClearValue(TextBlock.ForegroundProperty);
        }

        /// <summary>True while a run is in flight that cannot work without the
        /// presses. Only then is "no press yet" worth colouring.</summary>
        private bool _memPressesExpected;

        // ---- handlers --------------------------------------------------------

        private void MemMapRefreshTarget_Click(object sender, RoutedEventArgs e)
        {
            RefreshMemoryMapTab();
        }

        private void MemMapProcessBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            // Debounced, because resolving a name walks the whole process table
            // and clearing the box walks every TeknoParrot profile XML on top of
            // that. Doing either per keystroke put both on the UI thread five
            // times for a five letter name.
            if (_memTargetDebounce == null)
            {
                _memTargetDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _memTargetDebounce.Tick += (s2, e2) =>
                {
                    _memTargetDebounce.Stop();
                    RefreshMemMapTarget();
                    SyncMemMapButtons();
                };
            }
            _memTargetDebounce.Stop();
            _memTargetDebounce.Start();
        }

        private void MemMapScript_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Fires during XAML load too, from the item marked selected, before
            // the chained constructor has set _plugin. Nothing to sync then.
            if (_suppressEvents || _plugin == null) return;
            SyncMemMapButtons();
        }

        /// <summary>Any of the known-value boxes. Re-states the plan and
        /// re-gates the buttons, because both depend on what is typed.</summary>
        private void MemMapKnown_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            SyncMemMapButtons();
        }

        /// <summary>Which of the three gear paths. Re-states the plan and
        /// re-gates the sequence box, which is dead unless the override is
        /// picked.</summary>
        private void MemMapGearMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SyncMemMapButtons();
        }

        /// <summary>What the buttons last asked for. The runs share the whole
        /// start path except their arguments.</summary>
        private enum MemMapRunKind
        {
            /// <summary>The driving script, plus whatever was known.</summary>
            Hunt,
            /// <summary>Census the whole of memory, filter nothing, name
            /// nothing, and report the tier ladder.</summary>
            Survey,
            /// <summary>Every value the map's rows have been given, searched in
            /// one sweep. No script and no driving: the values were read off the
            /// screen, so there is nothing to perform.
            ///
            /// This is THE ONE ACTION. It replaced a single "Find it now" beside
            /// a single box, which could only ever search one thing and put its
            /// findings wherever the tab happened to be pointed.</summary>
            Known,
            /// <summary>Nothing but the watch list: a scanner kept alive so the
            /// rows have something reading for them while the operator goes and
            /// changes something in the game.</summary>
            Watch,
            /// <summary>Pointer paths to one address. Its own run because the
            /// scanner refuses to combine one with a search, a survey, a script
            /// or a watch-only host: a paths run walks backwards from an address
            /// somebody already has, and a search is how you get one.</summary>
            Paths,
            /// <summary>Record a session: the whole readable game, delta
            /// encoded, with every shift press and mark beside it, for as long
            /// as the operator drives. It searches for nothing and names
            /// nothing; the questions are asked of the file afterwards, and one
            /// drive answers every field.</summary>
            Session,
        }

        private MemMapRunKind _memMapRunKind = MemMapRunKind.Hunt;

        private void MemMapSurvey_Click(object sender, RoutedEventArgs e) => StartMemMapRun(MemMapRunKind.Survey);

        // ---- the session -------------------------------------------------------

        /// <summary>Record, or stop recording: one button, because at the wheel
        /// there is one thing to do and it is the same button both times.</summary>
        private void MemMapSession_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (_plugin.MemoryScanRunning && _memMapRunKind == MemMapRunKind.Session)
            {
                MemMapStop_Click(sender, e);
                return;
            }
            StartMemMapRun(MemMapRunKind.Session);
        }

        /// <summary>Fill the mark-name dropdown once, from the same table the
        /// wire uses, and pick whatever the plugin is currently set to send.</summary>
        private void EnsureMarkNames()
        {
            if (MemMapMarkNameCombo == null || _memMarkNamesReady) return;
            _memMarkNamesReady = true;
            bool was = _suppressEvents;
            _suppressEvents = true;
            try
            {
                MemMapMarkNameCombo.Items.Clear();
                string current = _plugin?.MemoryScanMarkName ?? MemoryMarks.DefaultName;
                ComboBoxItem pick = null;
                foreach (var p in MemoryMarks.Presets)
                {
                    var item = new ComboBoxItem { Content = p.Name, Tag = p, ToolTip = p.Separates };
                    MemMapMarkNameCombo.Items.Add(item);
                    if (string.Equals(p.Name, current, StringComparison.Ordinal)) pick = item;
                }
                if (MemMapMarkNameCombo.Items.Count > 0)
                    MemMapMarkNameCombo.SelectedItem = pick ?? MemMapMarkNameCombo.Items[0];
            }
            finally { _suppressEvents = was; }
            RefreshMarkHelp();
        }

        private MemoryMarks.Preset SelectedMarkPreset()
            => (MemMapMarkNameCombo?.SelectedItem as ComboBoxItem)?.Tag as MemoryMarks.Preset;

        /// <summary>What the chosen mark is for, and how far back it looks. Said
        /// beside the picker, because the window is the thing that decides
        /// whether a late tap still finds the change.</summary>
        private void RefreshMarkHelp()
        {
            if (MemMapMarkHelpText == null) return;
            var p = SelectedMarkPreset();
            MemMapMarkHelpText.Text = p == null ? "" :
                "Looks back " + p.LookbackSecs.ToString("0.#", CultureInfo.InvariantCulture)
                + " s from the tap. " + p.Separates;
        }

        private void MemMapMarkName_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var p = SelectedMarkPreset();
            if (p != null && _plugin != null) _plugin.MemoryScanMarkName = p.Name;
            RefreshMarkHelp();
        }

        /// <summary>The same mark the bound button sends, from the tab.</summary>
        private void MemMapMarkNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            string err = _plugin.SendMemoryScanMark(_plugin.MemoryScanMarkName);
            if (err != null) SetMemStage("The mark was not sent: " + err + ".");
            RefreshSessionStatus();
        }

        /// <summary>How long the recording has run, or ran, as m:ss.</summary>
        private string SessionElapsedText()
        {
            if (_memSessionStartedUtc == DateTime.MinValue) return "0:00";
            var end = _memSessionEndedUtc == DateTime.MinValue ? DateTime.UtcNow : _memSessionEndedUtc;
            var t = end - _memSessionStartedUtc;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":"
                 + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>The recording's size as the scanner last reported it. A
        /// session lives for ten minutes with the operator away from the screen,
        /// and "is it still writing" is the question they come back with, so
        /// the honest answer when the scanner has not said is that it has not
        /// said, rather than a zero that reads as a dead recording.</summary>
        private string SessionSizeText()
            => _memSessionMb > 0
                ? _memSessionMb.ToString(_memSessionMb >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB written"
                : "size not reported by the scanner yet";

        /// <summary>The line beside the session button: elapsed time, marks,
        /// shifts and megabytes while recording, and the same numbers frozen
        /// afterwards. Rewritten twice a second from the tick.</summary>
        private void RefreshSessionStatus()
        {
            if (MemMapSessionStatusText == null || _plugin == null) return;
            bool live = _plugin.MemoryScanRunning && _memMapRunKind == MemMapRunKind.Session;
            if (live)
            {
                // Read while the counters belong to this run, and kept, so the
                // summary after the run does not show the next run's zeros.
                _memSessionMarks  = _plugin.MemoryScanMarksSent;
                _memSessionShifts = _plugin.MemoryScanShiftUpSent + _plugin.MemoryScanShiftDownSent;
            }
            string text;
            if (live)
            {
                _memAckCounts.TryGetValue("mark", out int acked);
                text = "Recording " + SessionElapsedText() + ".  "
                     + _memSessionMarks.ToString(CultureInfo.InvariantCulture)
                     + (_memSessionMarks == 1 ? " mark" : " marks")
                     + (acked < _memSessionMarks
                        ? " (" + acked.ToString(CultureInfo.InvariantCulture) + " confirmed by the scanner)" : "")
                     + ", " + _memSessionShifts.ToString(CultureInfo.InvariantCulture)
                     + (_memSessionShifts == 1 ? " shift" : " shifts")
                     + ", " + SessionSizeText() + ".";
            }
            else if (_memSessionStartedUtc != DateTime.MinValue)
            {
                text = "Last recording: " + SessionElapsedText() + ", "
                     + _memSessionMarks.ToString(CultureInfo.InvariantCulture)
                     + (_memSessionMarks == 1 ? " mark" : " marks") + ", "
                     + _memSessionShifts.ToString(CultureInfo.InvariantCulture)
                     + (_memSessionShifts == 1 ? " shift" : " shifts") + ", "
                     + SessionSizeText() + "."
                     + (string.IsNullOrEmpty(_memOutDir) ? "" : "  It is in the results folder below.");
            }
            else text = "";
            if (!string.Equals(MemMapSessionStatusText.Text, text, StringComparison.Ordinal))
                MemMapSessionStatusText.Text = text;
        }

        /// <summary>What the scanner says it has written, off whatever event
        /// carries it. Only while a session is the run in flight: a survey or a
        /// hunt reporting its own file size is not this recording.</summary>
        private void NoteRecordingProgress(ScanEvent ev)
        {
            if (ev == null || _memMapRunKind != MemMapRunKind.Session) return;
            if (ev.MbWritten > 0) _memSessionMb = ev.MbWritten;
            else if (ev.BytesWritten > 0) _memSessionMb = ev.BytesWritten / 1048576.0;
        }

        /// <summary>The recording is over, one way or another. Freezes the
        /// clock and the tallies; idempotent, because the done event and the
        /// exit both end a run and either can arrive first.</summary>
        private void NoteSessionEnded()
        {
            if (_memMapRunKind != MemMapRunKind.Session) return;
            if (_memSessionStartedUtc == DateTime.MinValue || _memSessionEndedUtc != DateTime.MinValue) return;
            _memSessionEndedUtc = DateTime.UtcNow;
            RefreshSessionStatus();
        }

        /// <summary>The done line for a session. It names nothing by design, so
        /// exit codes that mean "unproven" or "stopped early" are the recording
        /// having worked, not a shortfall.</summary>
        private string SessionDoneLine(int exitCode)
        {
            if (exitCode == 0 || exitCode == 7 || exitCode == 9)
                return "The session was recorded (" + SessionElapsedText() + "). Everything it kept is in the "
                     + "folder below: the recording, and every shift press and mark beside it. The analysis that "
                     + "answers the map from it is the next round.";
            if (exitCode == 2)
                return "The recording could not start. " + ExitCodeMeaning(2);
            return "The recording ended with exit code " + exitCode.ToString(CultureInfo.InvariantCulture)
                 + ". " + ExitCodeMeaning(exitCode) + " Whatever it had already written is in the folder below.";
        }

        private void MemMapStart_Click(object sender, RoutedEventArgs e) => StartMemMapRun(MemMapRunKind.Hunt);

        private void StartMemMapRun(MemMapRunKind kind)
        {
            _memMapRunKind = kind;
            if (_plugin == null) return;

            // A hunt carries the declared values, so a box that cannot be read
            // stops it here rather than being silently dropped from the command
            // line. The other two runs declare nothing.
            if (kind == MemMapRunKind.Hunt || kind == MemMapRunKind.Known)
            {
                string problem = KnownValueProblem();
                if (problem != null)
                {
                    SetMemStage(problem);
                    ShowLiveBox(true);
                    return;
                }
            }
            if (kind == MemMapRunKind.Known && !KnownSearchPlan().AnyWithoutDriving)
            {
                // Checked here as well as on the button, because getting it
                // wrong is not a no-op: a request carrying nothing the scanner
                // recognises as a known value falls through to the DRIVING
                // SCRIPT, and the operator who pressed "search for these values"
                // would be counted through a run at the wheel instead.
                SetMemStage("There is nothing here to read straight out of memory. Fill in a text value or "
                          + "a fixed number on one of the map's rows. The gear is found by driving: record a "
                          + "session, or start the Driving script under Tools.");
                ShowLiveBox(true);
                return;
            }
            else if (kind == MemMapRunKind.Survey)
            {
                // Checked here rather than dropped silently. A number the
                // scanner refuses comes back as exit code 2, which reads on this
                // tab as a version mismatch and sends the operator looking for
                // the wrong thing; a number this end refuses says which box.
                CensusStaticCap(out string capProblem);
                if (capProblem != null)
                {
                    SetMemStage(capProblem);
                    ShowLiveBox(true);
                    return;
                }
            }

            RefreshMemMapTarget();
            if (_memMapTarget == null)
            {
                SetMemStage("Nothing to scan: no cabinet was found and no process name was given.");
                ShowLiveBox(true);
                return;
            }

            ClearMemMapRun(keepResults: kind == MemMapRunKind.Watch);
            ResetCandidateIntake();
            // What this run is looking for, worked out ONCE and used for both
            // the arguments and the routing, so a value edited while the scanner
            // is working cannot move where its findings land.
            var searchPlan = kind == MemMapRunKind.Known || kind == MemMapRunKind.Hunt
                ? KnownSearchPlan() : null;
            // Which field each finding is going to, decided BEFORE anybody
            // drives. A run whose findings landed on the wrong field is a
            // session wasted, and the field frame says where they went.
            AimRunAtField(kind, searchPlan);
            AttachScanHandlers();

            var req = new MemoryScanRequest
            {
                Pid         = _memMapTarget.Pid,
                ProcessName = _memMapTarget.ProcessName,
                Script      = SelectedMemMapScript(),
                Survey      = kind == MemMapRunKind.Survey,
                WatchHost   = kind == MemMapRunKind.Watch,
                Session     = kind == MemMapRunKind.Session,
                SessionSeconds = MemSessionCapSeconds,
            };
            if (kind == MemMapRunKind.Session)
            {
                // A new recording. The last one's summary line gives way to this
                // one's clock, and the mark name the button will send is whatever
                // the picker says right now.
                _memSessionStartedUtc = DateTime.UtcNow;
                _memSessionEndedUtc   = DateTime.MinValue;
                _memSessionMb = 0;
                _memSessionMarks = 0;
                _memSessionShifts = 0;
                _memMarkFlashUntilUtc = DateTime.MinValue;
                var preset = SelectedMarkPreset();
                if (preset != null) _plugin.MemoryScanMarkName = preset.Name;
            }
            if (kind == MemMapRunKind.Paths && !FillPathsRequest(req)) return;
            if (kind == MemMapRunKind.Survey)
            {
                // The only number a survey takes from this tab, and it is not a
                // recording budget: it decides how many of the never-moving
                // values reach the census file. Those are the redline, the car
                // id and the gear that sat still, so a survey that leaves them
                // out has not surveyed anything.
                req.CensusStaticCap = CensusStaticCap(out _);
            }
            else if (kind == MemMapRunKind.Known)
            {
                // Every value the map has been given, in ONE sweep. Deliberately
                // no gear and no script: those are the two things that need
                // somebody at the wheel, and the whole point of this button is
                // that it asks for nothing.
                FillNeedles(req, searchPlan);
                req.KnownRedline = StaticFromPlan(searchPlan);
            }
            else if (kind == MemMapRunKind.Hunt)
            {
                var gearMode = GearSearchMode();
                FillNeedles(req, searchPlan);
                req.KnownGearLive = gearMode == MemGearMode.Live;
                req.GearPresses   = MemGearPresses;
                req.KnownGear     = KnownGearSequence(out _);
                req.GearSeedCap   = GearSeedCap(out _);
                req.KnownRedline  = StaticFromPlan(searchPlan);
            }

            string err = _plugin.StartMemoryScan(req);
            if (err != null)
            {
                DetachScanHandlers();
                SetMemStage("The scan could not start: " + err + ".");
                ShowLiveBox(true);
                SyncMemMapButtons();
                return;
            }

            _memRunLive = true;
            // A new run has read nothing yet. Reset before the first refresh, or
            // the tab reports the last run's tick as this one's and calls values
            // live that nobody has read.
            _memWatchRunStartedUtc = DateTime.UtcNow;
            _memWatchLastUpdateUtc = DateTime.MinValue;
            _memWatchLastTick = 0;
            _memWatchStrangers = 0;
            _memWatchReported.Clear();
            RepaintWatchValues();
            ShowLiveBox(true);
            // A Hunt is several different runs depending on what was asked for,
            // and they ask completely different things of the person at the
            // wheel: a live gear search only wants them to drive and shift, a
            // declared one waits for them one gear at a time, a name-and-redline
            // search wants the game left alone, and an unasked run wants the
            // script driven.
            var mode = kind == MemMapRunKind.Hunt ? GearSearchMode() : MemGearMode.Off;
            bool liveGearRun  = kind == MemMapRunKind.Hunt && mode == MemGearMode.Live;
            bool typedGearRun = kind == MemMapRunKind.Hunt && mode == MemGearMode.Typed;
            bool gearRun = liveGearRun || typedGearRun;
            bool declaredNoDriving = kind == MemMapRunKind.Hunt && !gearRun && AnyKnownValueDeclared();
            // Only a gear run needs the presses, so only a gear run gets the
            // amber "nothing has arrived" line.
            _memPressesExpected = gearRun;
            if (MemMapCueText != null)
                MemMapCueText.Text =
                    kind == MemMapRunKind.Session    ? "Recording. Get in and drive: shift a lot, hold it to the limiter, "
                                                       + "do laps, stop dead, change car, crash, drift. Press the mark "
                                                       + "button at the END of each. Stop when you are done."
                  : kind == MemMapRunKind.Survey     ? "Nothing to do. Leave the game where it is."
                  : kind == MemMapRunKind.Known      ? "Nothing to do. Leave the game exactly as it is: the values you typed are the ones on screen now."
                  : kind == MemMapRunKind.Paths      ? "Nothing to do. Leave the game exactly where it is."
                  : kind == MemMapRunKind.Watch      ? "Go and change the thing you are watching for."
                  : declaredNoDriving                ? "Nothing to do. Leave the game where it is."
                  : liveGearRun                      ? "Get to the wheel. Drive, and shift with your bound buttons."
                  : typedGearRun                     ? "Get to the wheel. You will be asked for one gear at a time."
                                                     : "Get to the wheel.";
            if (MemMapPhaseText != null)
                MemMapPhaseText.Text =
                    kind == MemMapRunKind.Session    ? "Recording a session…"
                  : kind == MemMapRunKind.Survey     ? "Surveying memory…"
                  : kind == MemMapRunKind.Known      ? "Looking for what you filled in…"
                  : kind == MemMapRunKind.Paths      ? "Looking for a pointer path…"
                  : kind == MemMapRunKind.Watch      ? "Watching…"
                  : declaredNoDriving                ? "Reading memory…"
                                                     : "Starting…";
            if (kind == MemMapRunKind.Hunt) SetMemCommandLine(HuntGroundTruthLine());
            if (kind == MemMapRunKind.Session)
                SetMemCommandLine("Ground truth: every shift press, with its direction, and every mark, named \""
                                + _plugin.MemoryScanMarkName + "\" until you pick another. A shift is exact; a mark "
                                + "is the end of a lookback window chosen by its name. Nothing is searched for now: "
                                + "the recording is asked afterwards.");
            if (kind == MemMapRunKind.Known)
                SetMemCommandLine("Ground truth: " + KnownSearchPlan().Describe()
                                + " There is nothing to press and nothing to drive. Leave the game where it "
                                + "is until the run finishes.");
            if (kind == MemMapRunKind.Watch)
                SetMemStage("Watching "
                          + (_plugin.MemoryWatch.Count + _plugin.MemoryFields.ReadableCount)
                                .ToString(CultureInfo.InvariantCulture)
                          + " address(es) and nothing else: the watch list plus everything the map wants "
                          + "read, which is its candidates and every confirmed entry it has to check. This "
                          + "run searches for nothing and names nothing, so anything already found stays on "
                          + "screen. Go and change what you are testing for, then look at which row moved.");
            RefreshWheelCueLine();
            StartMemMapTimer();
            SyncMemMapButtons();
        }

        /// <summary>Put the plan's text values on the request, in map order.
        ///
        /// The FIRST one goes in the single-needle field and the rest in the
        /// list, which is not a detail: the single form is what the scanner's
        /// own gates drive, so the commonest run on this tab stays the shape
        /// that is tested end to end over there.</summary>
        private static void FillNeedles(MemoryScanRequest req, MemoryFieldSearch plan)
        {
            if (req == null || plan == null) return;
            req.KnownStrings = new List<string>();
            for (int i = 0; i < plan.Needles.Count; i++)
            {
                if (i == 0) req.KnownString = plan.Needles[i].Text;
                else req.KnownStrings.Add(plan.Needles[i].Text);
            }
        }

        /// <summary>True when something is known, which is what makes a run a
        /// known-value run rather than a driving one.</summary>
        private bool AnyKnownValueDeclared()
        {
            if (KnownSearchPlan().AnyText) return true;
            if (GearSearchMode() != MemGearMode.Off) return true;
            return KnownRedline(out _) > 0;
        }

        /// <summary>What the reverse channel is for, said once at the start of a
        /// hunt so the driver knows the buttons matter.</summary>
        private string HuntGroundTruthLine()
        {
            var mode = GearSearchMode();
            if (mode == MemGearMode.Live)
                // Nothing to wait for and nothing to declare. The presses ARE
                // the sequence, so the only instruction is to use the buttons.
                return "Ground truth: your shift presses, "
                     + MemGearPresses.ToString(CultureInfo.InvariantCulture)
                     + " of them. Drive normally and shift with the bound buttons; the "
                     + "gears are worked out from the presses, so there is nothing to declare and no cue to wait "
                     + "for. A press that changes nothing, like pulling for an upshift in top gear, is expected "
                     + "and does no harm. It ends on the last press, so press Stop to finish on fewer.";
            string gears = KnownGearSequence(out _);
            if (gears != null)
                // NOT "drive these gears". The scanner asks for them ONE AT A
                // TIME and waits: it reads memory between each, so getting ahead
                // of the cue samples the wrong gear.
                return "Ground truth: the gears " + gears + ", asked for one at a time. Select the gear the cue "
                     + "names, then press a bound shift button. Wait for the next cue before moving on.";
            if (AnyKnownValueDeclared())
                return "Ground truth: read straight out of memory, so there is nothing to press. Leave the game "
                     + "on this car until the run finishes.";
            return "Ground truth: nothing declared. Press your bound shift buttons anyway; every one of them is recorded.";
        }

        private void MemMapStop_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // ASKED, not killed. The scanner writes its recording, its CSVs and
            // its verdict on the way out, so a run stopped at 80 percent is
            // still worth reading; killing it throws all of that away. The wait
            // is on a worker thread, so the UI does not freeze for it.
            //
            // Nothing is detached here: the verdict, the findings and the done
            // event all arrive DURING the wait, and dropping the handlers now
            // would throw away the very thing the abort was asked for.
            _memAborting = true;
            SetMemStop("Asking the scanner to stop and write what it has…");
            if (MemMapCueText != null) MemMapCueText.Text = "Stopping.";
            SyncMemMapButtons();
            _plugin.StopMemoryScanPolitely((clean, detail) =>
                Dispatcher.BeginInvoke((Action)(() => OnMemMapStopped(clean, detail))));
        }

        /// <summary>The abort finished, one way or the other. Which one it was
        /// is the difference between a run that can be read and a run that
        /// cannot, so it is said rather than smoothed over.</summary>
        private void OnMemMapStopped(bool clean, string detail)
        {
            _memAborting = false;
            _memRunLive  = false;
            DetachScanHandlers();
            StopMemMapTimer();
            SetMemStop((detail ?? "Stopped.")
                     + (clean
                        ? " Its results are in the folder below."
                        : " Only what it had already written is in the folder below."));
            _memPhaseLive = false;
            if (MemMapCountdownText != null) MemMapCountdownText.Text = "";
            if (MemMapCueText != null) MemMapCueText.Text = "Stopped.";
            EndRunUi();
        }

        private void MemMapOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_memOutDir)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_memOutDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetMemStage("Could not open " + _memOutDir + ": " + ex.Message);
            }
        }

        // ---- the run ---------------------------------------------------------

        /// <summary>Wipe the last run off the tab.
        ///
        /// "keepResults" is for the watch host, and it is not a convenience: the
        /// operator gets to a watch by pressing Re-check this on a FINDING, and
        /// clearing the findings out from under them would take away the list
        /// they are working through, one row at a time, to add the next
        /// candidate. A watch host searches for nothing and names nothing, so it
        /// has no results of its own to put there instead.</summary>
        private void ClearMemMapRun(bool keepResults = false)
        {
            _memCeilingLine = "";
            _memStageLine   = "";
            _memStopLine    = "";
            _memAckCounts.Clear();
            _memLastAckLine = "";
            SetMemCommandLine("");
            _memPhaseLive   = false;
            _memPhaseCue    = "";
            _memPhaseSeconds = 0;
            _memLastSecondShown = -1;
            _memWheelStageCue = "";
            _memStageSecondsLeft = 0;
            if (MemMapCountdownText != null) MemMapCountdownText.Text = "";
            if (MemMapStageText != null) MemMapStageText.Text = "";
            if (keepResults) return;

            // Below the keepResults line on purpose. A watch host is started
            // FROM the run that warned, to go and check what it found, and
            // wiping the warning on the way would take away the reason the
            // operator is checking.
            _memWarnLines.Clear();
            _memWarnHidden = 0;
            RenderMemWarnLines();

            _memOutDir      = null;
            _memLedgerRows.Clear();
            _memFindingCount = 0;
            _memFindingOverflowRow = null;
            _memFindingsShown.Clear();
            MemMapLedgerHost?.Children.Clear();
            MemMapFindingsHost?.Children.Clear();
            // The raw list is per run. The CANDIDATES it fed are not: they
            // belong to the field and survive the run, the tab and SimHub, which
            // is the whole difference between a search and a map.
            if (MemMapFindingsExpander != null)
            {
                MemMapFindingsExpander.Visibility = Visibility.Collapsed;
                MemMapFindingsExpander.IsExpanded = false;
            }
            if (MemMapFindingsToFieldText != null) MemMapFindingsToFieldText.Text = "";
            if (MemMapWatchAllButton != null) MemMapWatchAllButton.Visibility = Visibility.Collapsed;
            if (MemMapLedgerExpander != null) MemMapLedgerExpander.Visibility = Visibility.Collapsed;
            if (MemMapVerdictBox != null) MemMapVerdictBox.Visibility = Visibility.Collapsed;
            if (MemMapSurveyResultBox != null) MemMapSurveyResultBox.Visibility = Visibility.Collapsed;
            if (MemMapOpenFolderButton != null) MemMapOpenFolderButton.IsEnabled = false;
        }

        /// <summary>The survey's own result block: what the census kept, which
        /// tier the recording budget allowed, and the ladder as the scanner
        /// printed it.
        ///
        /// The ladder is shown rather than summarised because it is the answer
        /// to the question a survey is asked. "How many candidates does a wide
        /// net catch and what would each threshold cost to record" is a table,
        /// and a table paraphrased into a sentence is a table thrown away.</summary>
        private void ShowSurveyResult(ScanEvent ev)
        {
            if (MemMapSurveyResultBox == null) return;
            string detail = FirstNonEmpty(ev.Detail, ev.Headline, ev.OneLine);
            if (MemMapSurveyText != null)
                MemMapSurveyText.Text = string.IsNullOrEmpty(detail)
                    ? "The survey finished. Its counts are in the report folder."
                    : detail;
            if (MemMapSurveyChosenText != null)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(ev.Chosen)) parts.Add("Recording tier: " + ev.Chosen.Trim() + ".");
                // Said here as well as in the amber block, because this is the
                // line an operator reads to decide whether to survey again, and
                // the box to change is named rather than the flag.
                if (ev.Dropped > 0)
                    parts.Add(ev.Dropped.ToString("N0", CultureInfo.InvariantCulture)
                            + " never-moving value(s) were left out. Put a bigger number in Census rows and "
                            + "survey again to keep them.");
                parts.Add("Nothing is named here on purpose: a survey counts, it does not answer.");
                MemMapSurveyChosenText.Text = string.Join("  ", parts);
            }
            if (MemMapSurveyLadderText != null)
            {
                // Trimmed at the end only. The leading spaces are the table's
                // own column alignment and a fixed-pitch block needs them.
                var lines = new List<string>();
                if (ev.Lines != null)
                    foreach (string line in ev.Lines)
                        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.TrimEnd());
                MemMapSurveyLadderText.Text = string.Join(Environment.NewLine, lines);
                MemMapSurveyLadderText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            MemMapSurveyResultBox.Visibility = Visibility.Visible;
        }

        private void AttachScanHandlers()
        {
            if (_plugin == null) return;
            DetachScanHandlers();
            _memScanEventHandler = (s, ev) =>
                Dispatcher.BeginInvoke((Action)(() => OnScanEvent(ev)));
            _memScanExitedHandler = (s, e) =>
                Dispatcher.BeginInvoke((Action)OnScanExited);
            _plugin.MemoryScanEvent  += _memScanEventHandler;
            _plugin.MemoryScanExited += _memScanExitedHandler;
        }

        private void DetachScanHandlers()
        {
            if (_plugin == null) return;
            if (_memScanEventHandler  != null) _plugin.MemoryScanEvent  -= _memScanEventHandler;
            if (_memScanExitedHandler != null) _plugin.MemoryScanExited -= _memScanExitedHandler;
            _memScanEventHandler  = null;
            _memScanExitedHandler = null;
        }

        /// <summary>One event from the scanner, already on the UI thread. An
        /// "ev" this build does not know is ignored, which is what lets the
        /// scanner half add kinds without a matching plugin release.</summary>
        private void OnScanEvent(ScanEvent ev)
        {
            if (ev == null) return;
            // Everything below runs on the DISPATCHER, posted from the scanner's
            // reader thread. An exception here is not a broken row on a tab: it
            // is an unhandled exception on SimHub's UI thread, from a line of
            // JSON that a scanner one version ahead is entitled to send. The
            // reader is already protected from a throwing handler; this is the
            // other half of that, and a scan that shows one wrong line beats a
            // SimHub that goes down mid-session at a cabinet.
            try { DispatchScanEvent(ev); }
            catch (Exception ex)
            {
                try
                {
                    SimHub.Logging.Current.Info("[TF4ALL] a memscan event could not be shown ("
                                              + (ev.Ev ?? "?") + "): " + ex.Message);
                }
                catch { }
            }
        }

        private void DispatchScanEvent(ScanEvent ev)
        {
            // Before the switch, off whatever kind of line carried it: the
            // scanner half is being written alongside this one, and whether it
            // reports what it has written on a stage line or a line of its own
            // is not something this half should have to know.
            NoteRecordingProgress(ev);
            switch ((ev.Ev ?? "").ToLowerInvariant())
            {
                case "stage":
                    // The watch stage is the one the operator acts on, so it is
                    // said in their language rather than the scanner's. Its own
                    // detail names the flag that caused it, which answers a
                    // question nobody at a cabinet is asking.
                    if (string.Equals(ev.Name, "watch", StringComparison.OrdinalIgnoreCase)
                        && (ev.Detail ?? "").IndexOf("ended", StringComparison.OrdinalIgnoreCase) < 0)
                        SetMemStage("Watching " + (_plugin?.MemoryWatch.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                                  + " address(es). Go and change the car, then look at which row moved. "
                                  + "A row that changes to the RIGHT thing is the answer; one that merely "
                                  + "changes is not.");
                    else
                        SetMemStage(string.IsNullOrEmpty(ev.Detail)
                            ? ev.Name
                            : ev.Name + ": " + ev.Detail);
                    // A stage that is really a warning gets promoted. The
                    // scanner's own truncation notices read as ordinary
                    // progress here and then scroll away under the next stage,
                    // which is the thing this round is fixing: they were only
                    // ever readable in the report file, after the session.
                    NoteWarningIfAny(ev.Detail);
                    // A stage is not a phase, so it carries no duration and no
                    // cue text. It still has an instruction, and the wheel is
                    // where the person who needs it is looking.
                    _memWheelStageCue = StageWheelCue(ev.Name);
                    // Ranking cannot overlap the script, so its arrival is what
                    // ends the last phase. Without this the wheel kept showing
                    // "OFF THROTTLE" through the seconds of ranking that follow,
                    // and the driver has nothing left to do by then.
                    if (string.Equals(ev.Name, "rank", StringComparison.OrdinalIgnoreCase))
                    {
                        _memPhaseLive = false;
                        _memLastSecondShown = -1;
                        if (MemMapCountdownText != null) MemMapCountdownText.Text = "";
                        if (MemMapCueText != null) MemMapCueText.Text = "Done driving. Working out the answer.";
                    }
                    if (!_memPhaseLive) PushCueToWheel(force: true);
                    break;

                case "ceiling":
                    // Printed before the drive on purpose: what this run can
                    // possibly prove, said before anyone spends a session on it.
                    _memCeilingLine = "Best this run can reach: " + (ev.Level ?? "?")
                                    + (string.IsNullOrEmpty(ev.Why) ? "." : ". " + ev.Why);
                    RenderMemStatusLines();
                    break;

                case "phase":
                    BeginPhase(ev);
                    break;

                case "countdown":
                    // The scanner's own count is authoritative; the local timer
                    // exists only to keep the number moving between these.
                    if (ev.SecondsLeft < 0) break;
                    if (_memPhaseLive)
                    {
                        _memPhaseStartUtc = DateTime.UtcNow.AddSeconds(ev.SecondsLeft - _memPhaseSeconds);
                        ShowCountdown(ev.SecondsLeft);
                    }
                    else
                    {
                        // The warm-up counts down too, before any phase exists.
                        // Dropping these left the tab and the wheel blank through
                        // the ten seconds the operator is meant to be getting
                        // into the car.
                        _memStageSecondsLeft = ev.SecondsLeft;
                        ShowCountdown(ev.SecondsLeft);
                        if (ev.SecondsLeft > 0
                            && ev.SecondsLeft <= TrueforceForAll.Core.WheelLedChannel.LedCount)
                            try { _plugin?.ShowScanCountdownLights(ev.SecondsLeft); } catch { }
                        PushCueToWheel(force: true);
                    }
                    break;

                case "prompt":
                    ShowPrompt(ev.N, ev.Of);
                    break;

                case "predicate":
                    AddPredicateRow(ev);
                    break;

                case "verdict":
                    // A watch host performs no script and declares nothing, so
                    // its verdict is UNPROVEN by construction and says nothing
                    // about anything. Showing it would replace the verdict of
                    // the search the operator is standing in the middle of with
                    // a word that carries no information.
                    //
                    // A survey is the same case for the same reason, and it was
                    // showing the box: a census names nothing ON PURPOSE, so
                    // ending it under a large UNPROVEN reads as a failed run
                    // rather than as the mode working. Its own block says what
                    // it counted.
                    if (_memMapRunKind != MemMapRunKind.Watch && _memMapRunKind != MemMapRunKind.Survey
                        && _memMapRunKind != MemMapRunKind.Paths && _memMapRunKind != MemMapRunKind.Session)
                        ShowVerdict(ev);
                    break;

                case "finding":
                    // Same reason, and one more: these rows are what the
                    // operator is adding to the watch list from, one at a time.
                    if (_memMapRunKind != MemMapRunKind.Watch && _memMapRunKind != MemMapRunKind.Paths
                        && _memMapRunKind != MemMapRunKind.Session)
                    {
                        AddFindingRow(ev);
                        // And the part that matters: a finding is a CANDIDATE
                        // for the field the tab is pointed at. The raw row is
                        // kept and folded away; the map is what a run is for.
                        NoteFindingAsCandidate(ev);
                    }
                    break;

                case "path":
                    // A route to an address through pointers, which is what
                    // turns a heap address into a map entry. The values this
                    // target actually gives up are not in the executable image,
                    // so without this half a confirmed answer is a number good
                    // for one launch.
                    NotePathEvent(ev);
                    break;

                case "path-check":
                    // One stored path, resolved and read against the game
                    // running now: the relaunch test. Its vocabulary is wider
                    // than pass or fail because the three ways it fails have
                    // three different fixes.
                    NotePathCheck(ev);
                    break;

                case "round":
                    // The scanner's own measurement of the round this tab just
                    // ran. Shown as corroboration, and checked against what this
                    // half decided: the two disagreeing is a bug in one of them
                    // that would otherwise never surface.
                    NoteScannerRound(ev);
                    break;

                case "survey":
                    // The census result. A survey names no address and earns no
                    // verdict, so every other renderer on this tab has nothing
                    // to show for it: the verdict box says UNPROVEN, which is
                    // correct and useless, and the findings list stays empty.
                    // Without this the one mode whose entire product is a count
                    // ends with a blank tab and a folder button.
                    ShowSurveyResult(ev);
                    break;

                case "warn":
                case "warning":
                    // What the run threw away, and the reason it matters here
                    // rather than in the report file: a seed that hits its cap
                    // drops the HIGHEST addresses, so the answer may be among
                    // them, and nobody at a wheel reads a report file. The seed
                    // cap box exists so this is actionable rather than merely
                    // sad.
                    AddWarning(FirstNonEmpty(ev.Detail, ev.Why, ev.OneLine, ev.Headline));
                    break;

                case "ack":
                    // The other half of the reverse channel. Proof a shift, a
                    // mark or the abort actually landed, and on which tick.
                    NoteScanAck(ev);
                    break;

                case "watch":
                    // One line per refresh carrying every watched row. Updated
                    // in place; the list is never rebuilt from here.
                    ApplyWatchEvent(ev);
                    break;

                case "done":
                    _memRunLive = false;
                    _memOutDir = ev.OutDir;
                    if (MemMapOpenFolderButton != null)
                        MemMapOpenFolderButton.IsEnabled = !string.IsNullOrEmpty(_memOutDir);
                    // A watch host is expected to end UNPROVEN with nothing
                    // named, because it looked for nothing. Reporting that as an
                    // exit code and a refusal would read as a failed run.
                    SetMemStage(
                        // A session names nothing by design, so its line is its
                        // own: "unproven" and "stopped early" are the recording
                        // having worked.
                        _memMapRunKind == MemMapRunKind.Session
                            ? SessionDoneLine(ev.ExitCode)
                        // A watch that never read anything did not "end", it
                        // failed, and the commonest reason is a memscan too old
                        // to know the flag this asks for. Swallowing the exit
                        // code here would leave that looking like a watch that
                        // simply found nothing to say.
                      : _memMapRunKind == MemMapRunKind.Watch && ev.ExitCode == 2
                            ? "The watch could not start. " + ExitCodeMeaning(2)
                      : _memMapRunKind == MemMapRunKind.Watch && _memWatchLastUpdateUtc == DateTime.MinValue
                            ? "The watch ended without reading anything, exit code "
                              + ev.ExitCode.ToString(CultureInfo.InvariantCulture) + ". "
                              + ExitCodeMeaning(ev.ExitCode)
                      : _memMapRunKind == MemMapRunKind.Watch
                            ? "The watch ended. The values on the list are the last ones read; press Read these "
                              + "now (under Tools) to start reading again."
                        // A survey ends UNPROVEN with nothing named, which is
                        // the mode working. Reporting exit code 9 and "nothing
                        // is named" as though it were a shortfall is the same
                        // mistake the verdict box was making.
                      : _memMapRunKind == MemMapRunKind.Survey && (ev.ExitCode == 9 || ev.ExitCode == 0)
                            ? "The survey finished. It named nothing, which is what a census does; what it "
                              + "counted is above, and every row of it is in survey.csv in the report folder."
                        // A run that carried several texts. Its exit code is the
                        // WORST needle's, so the one-line meanings below would
                        // report a run that filled in two fields as a run where
                        // nothing did what it was told to do.
                      : MultiNeedleOutcomeLine(ev.ExitCode) != null
                            ? MultiNeedleOutcomeLine(ev.ExitCode)
                      : ev.ExitCode == 0
                            ? "Finished."
                            : "Finished, exit code " + ev.ExitCode.ToString(CultureInfo.InvariantCulture)
                              + ". " + ExitCodeMeaning(ev.ExitCode));
                    EndRunUi();
                    break;
            }
        }

        private void OnScanExited()
        {
            // The scanner dying without a verdict is a real outcome and gets
            // said, rather than the tab sitting on a stage that will never end.
            // Keyed to the run, not to a phase: most of a run's silence is the
            // warm-up and the narrowing watch, where no phase is live at all.
            //
            // An abort in flight is not that: the exit is the one we asked for,
            // and OnMemMapStopped says what came of it.
            if (_memRunLive && !_memAborting)
                SetMemStage("The scanner stopped before the run finished, so nothing was named. "
                          + "Its own output is in SimHub.txt, on the lines marked [memscan].");
            _memRunLive = false;
            EndRunUi();
        }

        /// <summary>memscan's exit codes in one line each. The number on its own
        /// says nothing to whoever is reading this, and the reason is usually the
        /// whole answer.</summary>
        private static string ExitCodeMeaning(int code)
        {
            switch (code)
            {
                case 0:  return "Confirmed.";
                case 1:  return "That process was not running.";
                case 2:  return "The scanner rejected its command line. Usually that means this plugin asked for "
                              + "something the copy of memscan beside it is too old to know about: a recorded "
                              + "session, survey mode, "
                              + "the gear from your shift presses, the seed cap, the census row limit, or one of "
                              + "the known values. Update the scanner, or fall back to what it does know (declare "
                              + "the gears instead, and leave the seed cap and the census rows empty). The "
                              + "[memscan] lines in SimHub.txt name the argument, and the whole command line is "
                              + "logged when the run starts.";
                case 3:  return "Access denied. SimHub has to be running as administrator to read another process.";
                case 5:  return "The scanner failed unexpectedly; see the [memscan] lines in SimHub.txt.";
                case 6:  return "Nothing survived narrowing. The car has to be driven throughout that phase.";
                // A paths run uses this code for one thing only: it was stopped
                // while the pointer index was still being built. Said here as
                // well as in the general meaning, because on that run "wrote
                // what it had" would be the opposite of the truth: it deliberately
                // wrote nothing, since a partial index cannot tell a path that is
                // not there from one it had not reached yet.
                case 7:  return "It finished early. On a pointer-path run this means it was stopped before the "
                              + "index was finished, so nothing was searched and nothing was reported: run it "
                              + "again and let it finish. On any other run it wrote what it had.";
                case 9:  return "Unproven: no commanded evidence, so nothing is named.";
                case 10: return "Contested: something passed, and naming it would still not be safe.";
                case 11: return "Not found: nothing did what it was told to do.";
                default: return "";
            }
        }

        private void EndRunUi()
        {
            StopMemMapTimer();
            NoteSessionEnded();
            _memPhaseLive = false;
            _memWheelStageCue = "";
            _memStageSecondsLeft = 0;
            if (MemMapCountdownText != null) MemMapCountdownText.Text = "";
            // A killed run never emits done, so it never says where it wrote.
            // The plugin told it where, so the partial results are still
            // reachable: candidates.csv and regions.csv are usually already
            // there, and they are the expensive half of a session.
            if (string.IsNullOrEmpty(_memOutDir)) _memOutDir = _plugin?.LastMemoryScanOutDir;
            if (MemMapOpenFolderButton != null)
                MemMapOpenFolderButton.IsEnabled = !string.IsNullOrEmpty(_memOutDir);
            // The findings stopped arriving, so now they are drawn, saved and
            // armed, once. Doing it per finding would rebuild forty controls a
            // hundred and fifty times in the burst a name search arrives in.
            SettleFindingsIntoField();
            ShowFindingsDestination();
            // A path search that outlived its scanner has to say so rather than
            // leave a promotion hanging on an answer that is never coming.
            CheckPathWaitTimeout();
            SyncMemMapButtons();
        }

        private void BeginPhase(ScanEvent ev)
        {
            _memPhaseLive     = true;
            _memPhaseCue      = ev.Cue ?? "";
            _memPhaseSeconds  = ev.Seconds > 0 ? ev.Seconds : 0;
            _memPhaseStartUtc = DateTime.UtcNow;
            _memLastSecondShown = -1;
            _memLastCuePushUtc  = DateTime.MinValue;
            _memStageSecondsLeft = 0;

            if (MemMapPhaseText != null)
            {
                string of = ev.Of > 0
                    ? $"Step {(ev.Index + 1).ToString(CultureInfo.InvariantCulture)} of {ev.Of.ToString(CultureInfo.InvariantCulture)}"
                    : "Step";
                MemMapPhaseText.Text = string.IsNullOrEmpty(ev.Name) ? of : of + "   ·   " + ev.Name;
            }
            if (MemMapCueText != null) MemMapCueText.Text = _memPhaseCue;
            ShowLiveBox(true);
            StartMemMapTimer();
            PushCueToWheel(force: true);
        }

        private void ShowPrompt(int n, int of)
        {
            // A blip prompt is an instant, not a span: the big number becomes
            // which blip this is, and the whole strip lights for "now".
            string txt = of > 0
                ? n.ToString(CultureInfo.InvariantCulture) + "/" + of.ToString(CultureInfo.InvariantCulture)
                : n.ToString(CultureInfo.InvariantCulture);
            if (MemMapCountdownText != null) MemMapCountdownText.Text = txt;
            try
            {
                _plugin?.ShowScanCue("BLIP", txt);
                _plugin?.ShowScanCountdownLights(TrueforceForAll.Core.WheelLedChannel.LedCount);
            }
            catch { }
        }

        private void ShowCountdown(int secondsLeft)
        {
            if (MemMapCountdownText != null)
                MemMapCountdownText.Text = secondsLeft > 0
                    ? secondsLeft.ToString(CultureInfo.InvariantCulture)
                    : "";
        }

        private void AddPredicateRow(ScanEvent ev)
        {
            if (MemMapLedgerHost == null) return;
            if (MemMapLedgerExpander != null) MemMapLedgerExpander.Visibility = Visibility.Visible;

            string result = (ev.Result ?? "NOT-RUN").ToUpperInvariant();
            string key = string.IsNullOrEmpty(ev.Id) ? (ev.Name ?? Guid.NewGuid().ToString()) : ev.Id;
            string text = key
                        + (string.IsNullOrEmpty(ev.Name) ? "" : "  " + ev.Name)
                        + "   " + result
                        + (string.IsNullOrEmpty(ev.Detail) ? "" : "   " + ev.Detail);

            if (!_memLedgerRows.TryGetValue(key, out var row))
            {
                row = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                };
                _memLedgerRows[key] = row;
                MemMapLedgerHost.Children.Add(row);
            }
            row.Text = text;
            // PASS, FAIL and NOT-RUN are three outcomes, not two: a test that
            // never ran lowers what the run can claim and demotes nothing, so it
            // must not read like a failure.
            row.Foreground = result == "PASS" ? MemPassBrush
                           : result == "FAIL" ? MemFailBrush
                           : MemSkipBrush;
        }

        /// <summary>How many finding rows this tab will draw. A known-value
        /// search is not the commanded hunt, which reports a handful: a car name
        /// common enough to appear in a hundred draw buffers, or a redline hunted
        /// with nothing to be near, legitimately emits hundreds of findings, and
        /// the scanner's own caps are 200 strings plus 200 pointers plus the
        /// redline list. Every one of those is a control built on the UI thread.
        /// The file has all of them; this box is for reading.</summary>
        private const int MemMaxFindingRows = 60;
        private int _memFindingCount;
        private TextBlock _memFindingOverflowRow;
        /// <summary>The findings actually drawn, in order, so all of them can go
        /// on the watch list in one press.</summary>
        private readonly List<ScanEvent> _memFindingsShown = new List<ScanEvent>();

        private void AddFindingRow(ScanEvent ev)
        {
            if (MemMapFindingsHost == null) return;
            // The raw list is worth keeping and is not worth reading first, so
            // it appears folded away the moment there is anything in it.
            if (MemMapFindingsExpander != null && MemMapFindingsExpander.Visibility != Visibility.Visible)
                MemMapFindingsExpander.Visibility = Visibility.Visible;
            _memFindingCount++;
            if (_memFindingCount > MemMaxFindingRows)
            {
                // One row that counts, rather than a control per finding. It says
                // where the rest are, because "and 340 more" with no destination
                // is worse than the list it replaced.
                if (_memFindingOverflowRow == null)
                {
                    _memFindingOverflowRow = new TextBlock
                    {
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0),
                        Foreground = MemSkipBrush,
                    };
                    MemMapFindingsHost.Children.Add(_memFindingOverflowRow);
                }
                _memFindingOverflowRow.Text =
                    "... and " + (_memFindingCount - MemMaxFindingRows).ToString(CultureInfo.InvariantCulture)
                    + " more, not listed here. All of them are in known.txt in the results folder below. "
                    + "A search that returns this many has not answered anything on its own: narrow it, "
                    + "or change car and see which one moves.";
                return;
            }
            string conf = (ev.Confidence ?? "").ToUpperInvariant();
            var text = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Text = (ev.Address ?? "?")
                     + "   " + (ev.Type ?? "")
                     + (string.IsNullOrEmpty(ev.Label) ? "" : "   " + ev.Label)
                     + (conf.Length == 0 ? "" : "   " + conf.Replace('_', ' '))
                     // WHICH VALUE FOUND IT, on the row. One sweep carries
                     // several needles now, and a raw list that did not say
                     // would leave the operator unable to tell the car's hits
                     // from their own name's.
                     + (string.IsNullOrWhiteSpace(ev.Needle) ? "" : "   found by \"" + ev.Needle.Trim() + "\"")
                     + (string.IsNullOrEmpty(ev.Evidence) ? "" : "\n" + ev.Evidence),
                // Every finding carries the verdict word it earned, and a run
                // that refused to name anything says UNPROVEN on all of them. A
                // grey row is the difference between an address the scanner
                // handed over and one it listed while declining to.
                Foreground = conf == "CONFIRMED" ? MemPassBrush
                           : conf == "CONTESTED" ? MemAmberBrush
                           : MemSkipBrush,
            };

            // Every finding gets the one button that can settle it. A verdict
            // is an argument; a value that follows the car you just changed to
            // is proof, and this is the shortest path from one to the other.
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            row.Children.Add(text);
            if (!string.IsNullOrWhiteSpace(ev.Address))
            {
                var watch = new Button
                {
                    Content = "Re-check this",
                    Height = 24,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    Tag = ev,
                    ToolTip = "Put this address on the re-check list under Tools, so you can see its value change.",
                };
                watch.Click += MemMapWatchFinding_Click;
                Grid.SetColumn(watch, 1);
                row.Children.Add(watch);
                _memFindingsShown.Add(ev);
                if (MemMapWatchAllButton != null && _memFindingsShown.Count > 1)
                    MemMapWatchAllButton.Visibility = Visibility.Visible;
            }
            MemMapFindingsHost.Children.Add(row);
        }

        /// <summary>Put every finding on the watch list at once. A name search
        /// that came back with forty addresses has answered nothing by itself;
        /// watching all forty and then changing car answers it in one look, and
        /// pressing forty buttons to get there is the only thing in the way.</summary>
        private void MemMapWatchAll_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || _memFindingsShown.Count == 0) return;
            int added = 0, refused = 0;
            string lastProblem = null;
            foreach (var f in _memFindingsShown)
            {
                if (string.IsNullOrWhiteSpace(f.Address)) continue;
                WatchSpecForFinding(f, out string addr, out string kind, out int len, out string label);
                string problem = _plugin.AddMemoryWatchRow(addr, kind, len, label, out _);
                if (problem == null) added++;
                else { refused++; lastProblem = problem; }
            }
            // A search that came back with more addresses than the list holds is the case that needs
            // saying out loud: the ones that got on are the first ones the scanner reported, in
            // address order, and the answer is not more likely to be among them for that reason.
            _memWatchProblem = refused == 0
                ? null
                : added.ToString(CultureInfo.InvariantCulture) + " added, "
                  + refused.ToString(CultureInfo.InvariantCulture) + " not: " + lastProblem
                  + " These are the first " + added.ToString(CultureInfo.InvariantCulture)
                  + " in address order, not the most likely ones, so narrow the search or watch a "
                  + "few by hand instead.";
            RebuildWatchRows();
        }

        /// <summary>The address, kind, length and name to watch one finding
        /// with. The rules live in MemoryWatchList, where they can be tested
        /// against the scanner's real findings; this only supplies the text that
        /// was searched for.
        ///
        /// The finding says which needle found it, and that is the text a row is
        /// named after: once the car is changed, a row whose value no longer
        /// matches its own name is the answer, visible at a glance. A run
        /// carrying several needles would otherwise name every row after
        /// whichever value happened to be first.</summary>
        private void WatchSpecForFinding(ScanEvent f, out string addr, out string kind,
                                         out int len, out string label)
            => MemoryWatchList.SpecForFinding(f.Address, f.ModuleAddr, f.Type, f.WatchKind,
                                              f.WatchLen, f.Label, NeedleForFinding(f),
                                              out addr, out kind, out len, out label);

        /// <summary>The text one finding was found by, or the only text this run
        /// searched for when the finding did not say.</summary>
        private string NeedleForFinding(ScanEvent f)
        {
            if (!string.IsNullOrWhiteSpace(f?.Needle)) return f.Needle.Trim();
            var plan = KnownSearchPlan();
            return plan.Needles.Count == 1 ? plan.Needles[0].Text : null;
        }

        private void ShowVerdict(ScanEvent ev)
        {
            if (MemMapVerdictBox == null) return;
            MemMapVerdictBox.Visibility = Visibility.Visible;
            string level = (ev.Level ?? "").ToUpperInvariant();
            if (MemMapVerdictLevel != null)
            {
                MemMapVerdictLevel.Text = level.Length == 0 ? "No verdict" : level.Replace('_', ' ');
                MemMapVerdictLevel.Foreground = level == "CONFIRMED" ? MemPassBrush
                                              : level == "CONTESTED" ? MemAmberBrush
                                              : MemSkipBrush;
            }
            if (MemMapVerdictHeadline != null) MemMapVerdictHeadline.Text = ev.Headline ?? "";
            if (MemMapVerdictOneLine  != null) MemMapVerdictOneLine.Text  = ev.OneLine ?? "";
            // A verdict that mentions what was truncated is still a warning, and
            // it is the last thing the run says: it must not read as a result.
            NoteWarningIfAny(ev.Headline);
            NoteWarningIfAny(ev.OneLine);
            _memPhaseLive = false;
            if (MemMapCountdownText != null) MemMapCountdownText.Text = "";
            try { _plugin?.ShowScanCue("MEMORY MAP", level.Length == 0 ? "DONE" : level); } catch { }
        }

        // ---- status lines ----------------------------------------------------

        private void ShowLiveBox(bool on)
        {
            if (MemMapLiveBox != null)
                MemMapLiveBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetMemStage(string line)
        {
            _memStageLine = line ?? "";
            RenderMemStatusLines();
        }

        /// <summary>The outcome of a Stop, kept on its own line. It shares the
        /// box with the stage, and the two arrive out of order: the abort's own
        /// verdict and done events land while the wait for the exit is still
        /// running.</summary>
        private void SetMemStop(string line)
        {
            _memStopLine = line ?? "";
            RenderMemStatusLines();
        }

        private void RenderMemStatusLines()
        {
            if (MemMapStageText == null) return;
            var parts = new List<string>();
            if (_memCeilingLine.Length > 0) parts.Add(_memCeilingLine);
            if (_memStageLine.Length   > 0) parts.Add(_memStageLine);
            if (_memStopLine.Length    > 0) parts.Add(_memStopLine);
            MemMapStageText.Text = string.Join("\n", parts);
        }

        // ---- what the run threw away -----------------------------------------
        //
        // Every one of these used to reach the report file and nowhere else, and
        // the report file is read after the session, by which time the cabinet
        // is off. They are also the only class of message here that says the
        // answer might have been thrown away rather than absent, which is a
        // completely different thing to do next.

        /// <summary>The first of these that has anything in it.</summary>
        private static string FirstNonEmpty(params string[] candidates)
        {
            if (candidates == null) return null;
            foreach (string c in candidates)
                if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
            return null;
        }

        /// <summary>Promote a line that is really a warning, whatever event
        /// carried it. The scanner's own phrasing is matched rather than an
        /// event kind, so a notice that arrives as ordinary progress still gets
        /// out of the scroll and into the amber box.</summary>
        private void NoteWarningIfAny(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.IndexOf("may have been among them", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("were DROPPED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("hit its cap", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("dropped by the --survivor-cap", StringComparison.OrdinalIgnoreCase) >= 0
                || text.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                AddWarning(text);
        }

        /// <summary>Keep one warning, once. Repeats are common (a stage and its
        /// own summary say the same thing), and a box that lists the same line
        /// four times reads as four problems.</summary>
        private void AddWarning(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string line = text.Trim();
            foreach (string have in _memWarnLines)
                if (string.Equals(have, line, StringComparison.OrdinalIgnoreCase)) return;
            // Bounded: a run that decides to warn about every region would
            // otherwise turn this into the whole tab. COUNTED rather than
            // dropped, though. A run carrying a dozen texts can raise two
            // warnings each, and a list that quietly stopped at eight would be
            // doing the exact thing every one of those warnings exists to
            // report: leaving something out without saying so.
            if (_memWarnLines.Count >= 8) { _memWarnHidden++; RenderMemWarnLines(); return; }
            _memWarnLines.Add(line);
            RenderMemWarnLines();
        }

        private void RenderMemWarnLines()
        {
            if (MemMapWarnText == null) return;
            if (_memWarnLines.Count == 0)
            {
                MemMapWarnText.Text = "";
                MemMapWarnText.Visibility = Visibility.Collapsed;
                return;
            }
            var lines = new List<string>(_memWarnLines);
            // The one warning here that has something to DO about it. Saying
            // "raise --gear-seed-cap" to someone holding a wheel is not an
            // instruction; naming the box on this tab is.
            foreach (string w in _memWarnLines)
            {
                if (w.IndexOf("seed-cap", StringComparison.OrdinalIgnoreCase) >= 0
                    || w.IndexOf("seed hit its cap", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lines.Add("Put a bigger number in the Seed cap box (Tools, Driving script) and run it again. The addresses that "
                            + "were dropped are the highest ones, so what survived is not the whole answer.");
                    break;
                }
            }
            if (_memWarnHidden > 0)
                lines.Add("And " + _memWarnHidden.ToString(CultureInfo.InvariantCulture)
                        + " more warning(s) not shown here. All of them are in the report folder below.");
            MemMapWarnText.Text = string.Join("\n", lines);
            MemMapWarnText.Foreground = MemAmberBrush;
            MemMapWarnText.Visibility = Visibility.Visible;
        }

        /// <summary>The reverse channel's own line: what this end told the
        /// scanner, and what the scanner said came back.</summary>
        private void SetMemCommandLine(string line)
        {
            if (MemMapCommandText != null) MemMapCommandText.Text = line ?? "";
        }

        /// <summary>A command we sent came back applied. Counted per kind, so
        /// the line reads as a tally rather than only ever the last press: a
        /// shift that never landed is invisible otherwise, and the gear search
        /// rests entirely on those presses arriving.</summary>
        private void NoteScanAck(ScanEvent ev)
        {
            string cmd = (ev.Cmd ?? "?").Trim();

            // A round-start that has LANDED is the moment the scanner's own
            // measurement begins. This half's baselines are rebased onto it, so
            // the two measure the same window: without that, anything that moves
            // between the button and the scanner's next refresh is counted by one
            // half and not the other, and the cross-check reports an honest round
            // as unproved.
            if (cmd.Equals("round-start", StringComparison.OrdinalIgnoreCase))
                Store?.RebaseRound();

            // The watch list arms itself at the start of every run, which is one
            // command per row. Those are acked like everything else, and left in
            // this tally they would bury the shifts and gears the line exists
            // for under a dozen watch rows. The watch has a status line of its
            // own that says how many are live.
            //
            // A REFUSAL is the exception, and the reason there is one: the
            // scanner's watch list has a ceiling, and a row past it is refused
            // and then never read. That is invisible from here otherwise, and an
            // address nobody is reading looks exactly like one that failed a
            // round honestly.
            if (cmd.StartsWith("watch", StringComparison.OrdinalIgnoreCase)
                || cmd.Equals("unwatch", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(ev.Detail)
                    && ev.Detail.IndexOf("REFUSED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _memWatchRefused++;
                    AddWarning("The scanner refused a watch row: " + ev.Detail);
                }
                return;
            }
            _memAckCounts.TryGetValue(cmd, out int n);
            _memAckCounts[cmd] = n + 1;

            // A mark that landed is shown at the wheel by name for a moment, so
            // a press at the rim is seen to have counted without a screen; the
            // session line picks up the new tally on the same beat.
            if (cmd.Equals("mark", StringComparison.OrdinalIgnoreCase))
            {
                _memMarkFlashName = _plugin?.MemoryScanLastMark ?? "mark";
                _memMarkFlashUntilUtc = DateTime.UtcNow.AddSeconds(2);
                RefreshSessionStatus();
                if (_memMapRunKind == MemMapRunKind.Session) PushCueToWheel(force: true);
            }

            string what = cmd + (string.IsNullOrEmpty(ev.Detail) ? "" : " " + ev.Detail);
            _memLastAckLine = "Last sent: " + what
                            + (ev.AtTick > 0
                               ? ", recorded at tick " + ev.AtTick.ToString(CultureInfo.InvariantCulture) + "."
                               : ", recorded.");

            var tally = new List<string>();
            foreach (var kv in _memAckCounts)
                tally.Add(kv.Value.ToString(CultureInfo.InvariantCulture) + " " + kv.Key
                        + (kv.Value == 1 ? "" : "s"));
            SetMemCommandLine(_memLastAckLine + "   (" + string.Join(", ", tally) + " so far)");
        }

        /// <summary>Say where the cues are going. A wheel screen that is not
        /// available is a normal state (the screen is opt-in, and a game holding
        /// the pipe takes it away), and the honest answer is better than cues
        /// that silently only appear here.</summary>
        private void RefreshWheelCueLine()
        {
            if (MemMapWheelText == null) return;
            bool screen = _plugin?.ScanCueScreenAvailable == true;
            MemMapWheelText.Text = screen
                ? "Cues are on the wheel's screen as well as here."
                : "The wheel's screen is not available right now, so the cues are on this tab only. "
                  + "Switch the wheel-base screen on under LIGHTSYNC & OLED if you want them at the wheel.";
        }

        // ---- the tick --------------------------------------------------------

        private void StartMemMapTimer()
        {
            if (_memMapTimer == null)
            {
                _memMapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _memMapTimer.Tick += MemMapTimer_Tick;
            }
            _memMapTimer.Start();
        }

        private void StopMemMapTimer()
        {
            _memMapTimer?.Stop();
            _memTargetDebounce?.Stop();
            // The fade is left running deliberately: it stops itself once the
            // last lit row has gone out, and killing it here would leave a row
            // highlighted for good at the exact moment a run ends.
        }

        /// <summary>Everything the watch keeps ticking, stopped. Only for the
        /// tab going away: a half-typed name is written first, because the list
        /// is meant to outlive the tab.</summary>
        private void StopWatchTimers()
        {
            FlushWatchLabels();
            _memWatchFade?.Stop();
            foreach (var ui in _memWatchUi.Values)
            {
                if (!ui.Lit) continue;
                ui.Root.ClearValue(Border.BackgroundProperty);
                ui.Lit = false;
            }
            foreach (var ui in _memCandUi.Values)
            {
                if (!ui.Lit) continue;
                ui.Root.ClearValue(Border.BackgroundProperty);
                ui.Lit = false;
            }
        }

        private void MemMapTimer_Tick(object sender, EventArgs e)
        {
            // The status line has to keep telling the truth when NOTHING is
            // arriving, which is the case it exists for: a run that does not
            // watch, and a watch that has stopped. Both look identical to a line
            // that is only rewritten when a refresh comes in. Twice a second is
            // enough, and it is one text layout.
            if ((DateTime.UtcNow - _memWatchStatusShownUtc).TotalMilliseconds >= 500)
            {
                _memWatchStatusShownUtc = DateTime.UtcNow;
                RefreshWatchStatus();
                // Same reason, for the presses: a gear run receiving nothing
                // looks exactly like one receiving everything unless the tally
                // is redrawn while nothing is arriving.
                RefreshShiftBindingStatus();
                // And the recording's clock, which nothing else rewrites.
                RefreshSessionStatus();
                // And for a pointer path: "still searching" and "no path exists"
                // look identical to a line nobody rewrites, and the second is a
                // real answer the operator has to be able to act on.
                CheckPathWaitTimeout();
            }
            if (!_memPhaseLive)
            {
                // Warm-up and narrowing: no phase, but still an operator at the
                // wheel who needs to be told to drive. The readout expires on its
                // own, so it has to be re-pushed here or the screen goes blank
                // half a second into the instruction.
                PushCueToWheel(force: false);
                return;
            }
            if (_memPhaseSeconds > 0)
            {
                double left = _memPhaseSeconds - (DateTime.UtcNow - _memPhaseStartUtc).TotalSeconds;
                int secs = (int)Math.Ceiling(left);
                if (secs < 0) secs = 0;
                if (secs != _memLastSecondShown)
                {
                    _memLastSecondShown = secs;
                    ShowCountdown(secs);
                    // Rev lights for the last few seconds only: one level write
                    // per second, on a bar nothing else is driving during an
                    // arcade run, and it fades itself out afterwards.
                    if (secs > 0 && secs <= TrueforceForAll.Core.WheelLedChannel.LedCount)
                        try { _plugin?.ShowScanCountdownLights(secs); } catch { }
                }
            }
            PushCueToWheel(force: false);
        }

        /// <summary>Keep the cue on the wheel's screen. The readout it uses
        /// expires after about 1.6 s by design (it is normally a "you just
        /// nudged the gain" flash), so a cue that has to stay up for a five
        /// second hold is re-pushed about once a second.</summary>
        private void PushCueToWheel(bool force)
        {
            if (_plugin == null) return;
            // A survey and a name search ask the operator for nothing, so the
            // wheel is left alone through both. Telling somebody at the wheel to
            // drive, during a run that wants the game left exactly where it is,
            // is worse than showing them nothing.
            if (_memMapRunKind != MemMapRunKind.Hunt && _memMapRunKind != MemMapRunKind.Session) return;
            var now = DateTime.UtcNow;
            if (!force && (now - _memLastCuePushUtc).TotalMilliseconds < 900) return;
            _memLastCuePushUtc = now;
            if (force) RefreshWheelCueLine();
            try
            {
                if (_memMapRunKind == MemMapRunKind.Session)
                {
                    // Recording: the elapsed time, and for two seconds after a
                    // mark lands, its name. The operator is at the wheel and the
                    // screen is where they see that the press counted.
                    if (_plugin.MemoryScanRunning)
                    {
                        if (now < _memMarkFlashUntilUtc)
                            _plugin.ShowScanCue("MARK", (_memMarkFlashName ?? "").ToUpperInvariant());
                        else
                            _plugin.ShowScanCue("RECORDING", SessionElapsedText());
                    }
                    return;
                }
                if (_memPhaseLive)
                {
                    string value = _memLastSecondShown > 0
                        ? _memLastSecondShown.ToString(CultureInfo.InvariantCulture) + "S"
                        : "GO";
                    _plugin.ShowScanCue(ShortCue(_memPhaseCue), value);
                }
                else if (_memWheelStageCue.Length > 0)
                {
                    string value = _memStageSecondsLeft > 0
                        ? _memStageSecondsLeft.ToString(CultureInfo.InvariantCulture) + "S"
                        : "";
                    _plugin.ShowScanCue(_memWheelStageCue, value);
                }
            }
            catch { }
        }

        // ---- the watch list --------------------------------------------------
        //
        // Why this is here at all. Every other thing on this tab ends in a
        // verdict the operator has to believe, and believing one has already
        // gone wrong: a manifold-pressure decoy passed all six commanded tests
        // honestly and was named CONFIRMED in a program that has no engine,
        // because those tests describe the PEDAL rather than the crankshaft.
        //
        // A row here shows the live value at an address. Watch it, then go and
        // change the thing it is supposed to be, and the question answers
        // itself in seconds. That is the whole feature.
        //
        // Two rules govern the code below. Values are updated IN PLACE, never
        // by rebuilding the list: at 5 per second with tens of rows a rebuild
        // stutters the whole settings window. And a row whose read failed keeps
        // its place with the reason in it, because a value that stops being
        // readable is itself a finding.

        /// <summary>One row's controls, kept so a refresh can write straight
        /// into them.</summary>
        private sealed class MemWatchUiRow
        {
            public string Addr;                 // as sent, and as the scanner echoes it
            public Border Root;                 // what gets lit when the value moves
            public TextBox LabelBox;
            public TextBlock AddrText;
            public TextBlock KindText;
            public TextBlock ValueText;
            public TextBlock MovedText;
            /// <summary>The kind and length this row was drawn with, so a row
            /// re-added with a corrected kind is redrawn and one that only got
            /// a new name is left alone (the name is a live text box, and
            /// redrawing it mid-word would eat the word).</summary>
            public string KindShown;
            public DateTime LitUntilUtc;
            public bool Lit;
        }

        private readonly Dictionary<string, MemWatchUiRow> _memWatchUi =
            new Dictionary<string, MemWatchUiRow>(StringComparer.OrdinalIgnoreCase);

        private DispatcherTimer _memWatchFade;
        private DispatcherTimer _memWatchLabelSave;
        private bool _memWatchUiReady;
        private int _memWatchLastTick;
        private DateTime _memWatchLastUpdateUtc = DateTime.MinValue;
        private DateTime _memWatchStatusShownUtc = DateTime.MinValue;
        private int _memWatchStrangers;
        /// <summary>When the current run started, so "no values yet" can be told
        /// apart from "no values, and it has been ten seconds".</summary>
        private DateTime _memWatchRunStartedUtc = DateTime.MinValue;
        /// <summary>The addresses the last refresh actually carried. A row on
        /// this list that is not in there is a row the scanner refused or never
        /// received, and it looks exactly like a value that never changes, which
        /// is the one thing this feature must never do silently.</summary>
        private readonly HashSet<string> _memWatchReported =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Why the last add was refused, kept until something is added
        /// or removed. A live watch rewrites the status line several times a
        /// second, and an error that flashes past between two refreshes may as
        /// well not have been shown at all.</summary>
        private string _memWatchProblem;
        /// <summary>Addresses whose label has been typed but not yet written.
        /// Keyed rather than a flag so two rows edited quickly both land.</summary>
        private readonly List<string> _memWatchPendingLabels = new List<string>();

        /// <summary>How long a changed row stays lit. Long enough to catch out
        /// of the corner of an eye at 5 Hz, short enough that a value moving
        /// every refresh does not just look permanently lit.</summary>
        private static readonly TimeSpan MemWatchLitFor = TimeSpan.FromMilliseconds(900);

        /// <summary>The kind and rate pickers, filled once. Their contents come
        /// from the protocol, so they are built here rather than typed into the
        /// XAML twice.</summary>
        private void EnsureWatchUi()
        {
            if (_memWatchUiReady) return;
            _memWatchUiReady = true;

            if (MemMapWatchKindCombo != null && MemMapWatchKindCombo.Items.Count == 0)
            {
                foreach (string k in MemoryWatchList.Kinds)
                    MemMapWatchKindCombo.Items.Add(new ComboBoxItem { Content = k, Tag = k });
                MemMapWatchKindCombo.SelectedIndex = 0;
            }
            if (MemMapWatchRateCombo != null && MemMapWatchRateCombo.Items.Count == 0)
            {
                foreach (int hz in new[] { 1, 2, 5, 10, 20 })
                    MemMapWatchRateCombo.Items.Add(new ComboBoxItem
                    {
                        Content = hz.ToString(CultureInfo.InvariantCulture) + "/sec",
                        Tag = hz,
                    });
                SelectWatchRate(_plugin?.MemoryWatchHz ?? MemoryWatchList.DefaultHz);
            }
        }

        private void SelectWatchRate(int hz)
        {
            if (MemMapWatchRateCombo == null) return;
            bool was = _suppressEvents;
            _suppressEvents = true;
            try
            {
                foreach (var o in MemMapWatchRateCombo.Items)
                {
                    if (o is ComboBoxItem item && item.Tag is int v && v == hz)
                    {
                        MemMapWatchRateCombo.SelectedItem = item;
                        return;
                    }
                }
                MemMapWatchRateCombo.SelectedIndex = 2;   // 5/sec, the default
            }
            finally { _suppressEvents = was; }
        }

        private string SelectedWatchKind()
        {
            var item = MemMapWatchKindCombo?.SelectedItem as ComboBoxItem;
            string k = item?.Tag as string;
            return string.IsNullOrEmpty(k) ? "int32" : k;
        }

        /// <summary>Build the rows from the saved list. Called when the list
        /// itself changes, and never on a refresh.
        ///
        /// Two things it must not do. It must not rebuild when nothing about
        /// the list changed, because it is called every time the tab is looked
        /// at and a live watch would flicker. And when it does rebuild, the
        /// last value and the change count of a surviving row are carried over:
        /// those are the operator's evidence, and losing them because they
        /// clicked another tab would be losing the finding.</summary>
        private void RebuildWatchRows()
        {
            if (MemMapWatchHost == null || _plugin == null) return;
            EnsureWatchUi();

            var models = _plugin.MemoryWatch.Rows;
            if (WatchRowsAlreadyDrawn(models))
            {
                RefreshWatchStatus();
                SyncWatchButtons();
                return;
            }

            MemMapWatchHost.Children.Clear();
            _memWatchUi.Clear();

            foreach (var model in models)
            {
                var ui = BuildWatchRow(model);
                // What the row has been reading lives on the MODEL, so a redraw
                // for a new row or a corrected kind cannot lose it. Before that
                // it lived on the control, which meant a rebuild had to copy it
                // across by hand and a row that moved position lost its count.
                if (model.HasRead)
                {
                    ui.ValueText.Text = model.LastShown ?? "";
                    ui.ValueText.ToolTip = ui.ValueText.Text.Length > 0 ? ui.ValueText.Text : null;
                    ui.MovedText.Text = model.Changes > 0
                        ? model.Changes.ToString(CultureInfo.InvariantCulture) : "";
                }
                _memWatchUi[model.Addr] = ui;
                MemMapWatchHost.Children.Add(ui.Root);
            }
            if (MemMapWatchHeader != null)
                MemMapWatchHeader.Visibility = _memWatchUi.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            RefreshWatchStatus();
            SyncWatchButtons();
        }

        /// <summary>True when the rows on screen are already exactly these, in
        /// this order.</summary>
        private bool WatchRowsAlreadyDrawn(IReadOnlyList<MemoryWatchRow> models)
        {
            if (MemMapWatchHost == null) return false;
            if (models.Count != MemMapWatchHost.Children.Count) return false;
            if (models.Count != _memWatchUi.Count) return false;
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                if (!_memWatchUi.TryGetValue(m.Addr, out var ui)) return false;
                if (!ReferenceEquals(ui.Root, MemMapWatchHost.Children[i])) return false;
                string kindShown = m.Kind + (MemoryWatchList.IsSizedKind(m.Kind) && m.Len > 0
                                             ? " " + m.Len.ToString(CultureInfo.InvariantCulture) : "");
                if (!string.Equals(ui.KindShown, kindShown, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private MemWatchUiRow BuildWatchRow(MemoryWatchRow model)
        {
            var grid = new Grid();
            // These widths are mirrored in the XAML header. Change one, change
            // the other.
            foreach (double w in new double[] { 26, 154, 168, 66, -1, 56 })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            var remove = new Button
            {
                Content = "×",
                Width = 20,
                Height = 20,
                FontSize = 13,
                Padding = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = model.Addr,
                ToolTip = "Stop watching this address.",
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += MemMapWatchRemove_Click;
            Grid.SetColumn(remove, 0);
            grid.Children.Add(remove);

            var labelBox = new TextBox
            {
                Height = 22,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Tag = model.Addr,
                // Set BEFORE the handler is attached, so filling the box in does
                // not read as the operator renaming the row.
                Text = model.Label ?? "",
            };
            labelBox.TextChanged += MemMapWatchLabel_TextChanged;
            labelBox.LostFocus += (s, e) => FlushWatchLabels();
            var labelHost = new Border
            {
                BorderBrush = TryFindResource("EditableNumberBorder") as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 6, 0),
                Child = labelBox,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(labelHost, 1);
            grid.Children.Add(labelHost);

            var addrText = new TextBlock
            {
                Text = model.Addr,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = model.Addr,
            };
            Grid.SetColumn(addrText, 2);
            grid.Children.Add(addrText);

            string kindShown = model.Kind + (MemoryWatchList.IsSizedKind(model.Kind) && model.Len > 0
                                             ? " " + model.Len.ToString(CultureInfo.InvariantCulture) : "");
            var kindText = new TextBlock
            {
                Text = kindShown,
                FontSize = 11,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(kindText, 3);
            grid.Children.Add(kindText);

            // No wrapping, on purpose: a row that changes height when a long
            // string arrives makes the whole list jump under the eye that is
            // trying to spot which row moved.
            var valueText = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
            };
            Grid.SetColumn(valueText, 4);
            grid.Children.Add(valueText);

            var movedText = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "How many times this value has changed since the list was drawn.",
            };
            Grid.SetColumn(movedText, 5);
            grid.Children.Add(movedText);

            var root = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 2, 2, 2),
                Margin = new Thickness(0, 0, 0, 2),
                Child = grid,
            };

            return new MemWatchUiRow
            {
                Addr = model.Addr,
                Root = root,
                LabelBox = labelBox,
                AddrText = addrText,
                KindText = kindText,
                KindShown = kindShown,
                ValueText = valueText,
                MovedText = movedText,
            };
        }

        /// <summary>One refresh: every watched row, in one event. Updates in
        /// place and touches nothing else.</summary>
        private void ApplyWatchEvent(ScanEvent ev)
        {
            if (ev?.Rows == null) return;
            _memWatchLastTick = ev.Tick;
            _memWatchLastUpdateUtc = DateTime.UtcNow;
            int strangers = 0;

            // The comparison that answers the whole question lives in the list,
            // not here: it is the one piece of this feature worth testing
            // without a screen to point a camera at. This method paints what it
            // decided.
            // The map reads the same refresh. This is where an elimination round
            // gathers its evidence and where a confirmed entry is re-resolved,
            // re-read and sanity checked, all from one line off the wire: the
            // candidates and the entries are on the same watch list as
            // everything else, because the watch stream is the only live-read
            // channel there is and the plugin never reads memory itself.
            _plugin.MemoryFields.Observe(ev.Rows);

            var updates = _plugin.MemoryWatch.Apply(ev.Rows, out strangers);
            _memWatchReported.Clear();
            foreach (var r in ev.Rows) if (r?.Addr != null) _memWatchReported.Add(r.Addr);
            // A row the map asked for is not a stranger. Apply counts anything
            // the hand-curated watch list does not hold, and after this round the
            // map puts hundreds of its own rows on the same wire; without this
            // the status line would report the whole map as rows nobody asked
            // for, every refresh.
            if (strangers > 0)
            {
                // A set rather than MemoryWatch.Find per row: the map puts
                // hundreds of addresses on the wire, Find walks the list and
                // then re-parses the address when it misses, and this runs on
                // every refresh.
                var onTheList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in _plugin.MemoryWatch.Rows) onTheList.Add(w.Addr);
                int mapRows = 0;
                foreach (var r in ev.Rows)
                    if (r?.Addr != null && !onTheList.Contains(r.Addr)
                        && _plugin.MemoryFields.Knows(r.Addr)) mapRows++;
                strangers -= mapRows;
                if (strangers < 0) strangers = 0;
            }

            foreach (var u in updates)
            {
                if (!_memWatchUi.TryGetValue(u.Row.Addr, out var ui)) continue;
                string shown = u.Shown;

                if (!string.Equals(ui.ValueText.Text, shown, StringComparison.Ordinal))
                {
                    ui.ValueText.Text = shown;
                    ui.ValueText.ToolTip = shown.Length > 0 ? shown : null;
                }
                // A failed read is greyed rather than reddened: it is usually
                // the truth about the target (the car was unloaded, the region
                // was freed) rather than a fault.
                var want = u.Ok ? null : MemSkipBrush;
                if (!ReferenceEquals(ui.ValueText.Foreground, want))
                {
                    if (want == null) ui.ValueText.ClearValue(TextBlock.ForegroundProperty);
                    else ui.ValueText.Foreground = want;
                }
                // Where a module-relative row landed this launch. In the tooltip
                // rather than the column, because the address as WRITTEN is what
                // makes the row a map entry and it must stay readable.
                if (!string.IsNullOrEmpty(u.Resolved)
                    && !string.Equals(u.Resolved, ui.Addr, StringComparison.OrdinalIgnoreCase))
                {
                    string tip = ui.Addr + "  ->  " + u.Resolved;
                    if (!string.Equals(ui.AddrText.ToolTip as string, tip, StringComparison.Ordinal))
                        ui.AddrText.ToolTip = tip;
                }

                if (u.Changed)
                {
                    ui.MovedText.Text = u.Row.Changes.ToString(CultureInfo.InvariantCulture);
                    LightWatchRow(ui);
                }
            }

            // The status line carries the tick, so it changes on every refresh.
            // Rewriting it twenty times a second would be twenty text layouts a
            // second for a line nobody is reading that fast, on the same UI
            // thread as the values that are the point.
            bool strangersChanged = strangers != _memWatchStrangers;
            _memWatchStrangers = strangers;
            if (strangersChanged
                || (DateTime.UtcNow - _memWatchStatusShownUtc).TotalMilliseconds >= 250)
            {
                _memWatchStatusShownUtc = DateTime.UtcNow;
                RefreshWatchStatus();
            }
            // The map's own columns, on the same throttle and for the same
            // reason: forty candidate rows plus seven field rows repainted
            // twenty times a second is forty-seven text layouts nobody can read
            // that fast, on the thread that draws the values that are the point.
            if ((DateTime.UtcNow - _memCandPaintedUtc).TotalMilliseconds >= 200)
            {
                _memCandPaintedUtc = DateTime.UtcNow;
                PaintCandidateValues();
                PaintFieldRows();
            }
        }

        /// <summary>Light a row that just moved. This is the point of the whole
        /// feature: the operator changed the car and is looking for the row that
        /// followed.</summary>
        private void LightWatchRow(MemWatchUiRow ui)
        {
            ui.Root.Background = MemChangedBrush;
            ui.LitUntilUtc = DateTime.UtcNow + MemWatchLitFor;
            ui.Lit = true;
            if (_memWatchFade == null)
            {
                _memWatchFade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _memWatchFade.Tick += MemWatchFade_Tick;
            }
            _memWatchFade.Start();
        }

        private void MemWatchFade_Tick(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            bool anyLit = false;
            foreach (var ui in _memWatchUi.Values)
            {
                if (!ui.Lit) continue;
                if (now >= ui.LitUntilUtc)
                {
                    ui.Root.ClearValue(Border.BackgroundProperty);
                    ui.Lit = false;
                }
                else anyLit = true;
            }
            // The map's candidate rows fade on the same timer. Their own paint
            // only runs when a refresh arrives, so a row lit by the last refresh
            // before a watch stopped would stay lit for the rest of the session
            // and read as the one that moved.
            foreach (var ui in _memCandUi.Values)
            {
                if (!ui.Lit) continue;
                if (now >= ui.LitUntilUtc)
                {
                    ui.Root.ClearValue(Border.BackgroundProperty);
                    ui.Lit = false;
                }
                else anyLit = true;
            }
            if (!anyLit) _memWatchFade?.Stop();
        }

        /// <summary>Put every row's value column back in step with its model.
        /// Called when a run starts, because the plugin has just cleared what
        /// every row had been reading and the columns would otherwise keep
        /// showing the last run's numbers as though they were this one's.</summary>
        private void RepaintWatchValues()
        {
            if (_plugin == null) return;
            bool running = _plugin.MemoryScanRunning;
            foreach (var model in _plugin.MemoryWatch.Rows)
            {
                if (!_memWatchUi.TryGetValue(model.Addr, out var ui)) continue;
                ui.ValueText.Text = model.HasRead ? (model.LastShown ?? "")
                                  : running ? "reading…" : "";
                ui.ValueText.ToolTip = null;
                ui.MovedText.Text = model.Changes > 0
                    ? model.Changes.ToString(CultureInfo.InvariantCulture) : "";
            }
            RefreshWatchStatus();
        }

        /// <summary>What the list is doing, said plainly. "The values are old"
        /// and "the values are live" look identical otherwise, and the
        /// difference decides whether the row that sat still just proved
        /// something or proved nothing.</summary>
        private void RefreshWatchStatus()
        {
            if (MemMapWatchStatusText == null || _plugin == null) return;
            int rows = _plugin.MemoryWatch.Count;
            bool running = _plugin.MemoryScanRunning;

            // Stale values are dimmed as well as described. A row nobody is
            // reading must not look like a row that is being read.
            if (MemMapWatchHost != null) MemMapWatchHost.Opacity = running ? 1.0 : 0.6;

            var parts = new List<string>();
            // First, because it is the thing that needs answering.
            if (_memWatchProblem != null) parts.Add(_memWatchProblem);
            if (rows == 0)
            {
                parts.Add("Nothing is on the list. Add an address above, or press Re-check this on a finding.");
            }
            else if (running)
            {
                double sinceRefresh = _memWatchLastUpdateUtc == DateTime.MinValue
                    ? -1 : (DateTime.UtcNow - _memWatchLastUpdateUtc).TotalSeconds;
                double sinceStart = _memWatchRunStartedUtc == DateTime.MinValue
                    ? 0 : (DateTime.UtcNow - _memWatchRunStartedUtc).TotalSeconds;
                if (sinceRefresh < 0 && sinceStart > 6)
                {
                    // Not every run reads the watch list. A driving hunt does
                    // not, and neither does a survey. Saying "live" through one
                    // of those would be the worst thing this line could do: a
                    // row that never moved would look like an answer.
                    parts.Add(rows.ToString(CultureInfo.InvariantCulture)
                              + (rows == 1 ? " address, but nothing" : " addresses, but nothing")
                              + " is reading them: this run does not watch. Press Read these now for one that does.");
                }
                else if (sinceRefresh < 0)
                {
                    parts.Add(rows.ToString(CultureInfo.InvariantCulture)
                              + (rows == 1 ? " address." : " addresses.")
                              + " Waiting for the first refresh.");
                }
                else if (sinceRefresh > 3)
                {
                    parts.Add(rows.ToString(CultureInfo.InvariantCulture)
                              + (rows == 1 ? " address, FROZEN" : " addresses, FROZEN")
                              + ": the last refresh was "
                              + sinceRefresh.ToString("0", CultureInfo.InvariantCulture)
                              + " seconds ago, so these values are not live.");
                }
                else
                {
                    parts.Add(rows.ToString(CultureInfo.InvariantCulture)
                              + (rows == 1 ? " address, live" : " addresses, live")
                              + " at " + _plugin.MemoryWatchHz.ToString(CultureInfo.InvariantCulture)
                              + " per second. Last refresh at tick "
                              + _memWatchLastTick.ToString(CultureInfo.InvariantCulture) + ".");
                }
                // A row the scanner refused, or never got, updates forever with
                // nothing. That is indistinguishable from a value that does not
                // move, which is the exact judgement this feature exists to
                // support, so it is named rather than left to look like data.
                if (_memWatchReported.Count > 0)
                {
                    int missing = 0;
                    foreach (var r in _plugin.MemoryWatch.Rows)
                        if (!_memWatchReported.Contains(r.Addr)) missing++;
                    if (missing > 0)
                        parts.Add(missing.ToString(CultureInfo.InvariantCulture)
                                  + " of them are not being read at all: the scanner did not report them. "
                                  + "The reason is on the [memscan] lines in SimHub.txt.");
                }
            }
            else
            {
                parts.Add(rows.ToString(CultureInfo.InvariantCulture)
                          + (rows == 1 ? " address. " : " addresses. ")
                          + "Nothing is reading them right now, so these values are the last ones seen. "
                          + "Press Read these now, or start any other run.");
            }
            if (_memWatchStrangers > 0)
                parts.Add("The scanner is also watching "
                          + _memWatchStrangers.ToString(CultureInfo.InvariantCulture)
                          + " address(es) this list does not have; they are not shown.");
            string saveProblem = _plugin.MemoryWatchSaveProblem;
            if (saveProblem != null)
                parts.Add("The list could not be saved, so it will not survive a restart: " + saveProblem);
            MemMapWatchStatusText.Text = string.Join(" ", parts);
        }

        private void SyncWatchButtons()
        {
            if (_plugin == null) return;
            int rows = _plugin.MemoryWatch.Count;
            bool running = _plugin.MemoryScanRunning || _memAborting;
            string exe = _plugin.MemoryScanExePath();
            bool have = exe != null && MemoryScanDeployGap(exe) == null;

            if (MemMapWatchAddButton != null)
                MemMapWatchAddButton.IsEnabled = !string.IsNullOrWhiteSpace(MemMapWatchAddrBox?.Text);
            if (MemMapWatchClearButton != null)
                MemMapWatchClearButton.IsEnabled = rows > 0;
            // The watch host needs the same scanner and the same target every
            // other run needs, and it needs something to read. The MAP counts as
            // something to read: a saved map with entries and an empty watch
            // list is exactly the state a fresh launch is in, and a button
            // disabled there would leave that map permanently unverifiable.
            if (MemMapWatchStartButton != null)
                MemMapWatchStartButton.IsEnabled = have && !running && _memMapTarget != null
                                                && (rows > 0 || _plugin.MemoryFields.ReadableCount > 0);
        }

        private void MemMapWatchAddr_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            SyncWatchButtons();
        }

        private void MemMapWatchAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            int len = MemoryWatchList.DefaultLen;
            string rawLen = MemMapWatchLenBox?.Text;
            if (!string.IsNullOrWhiteSpace(rawLen)
                && !int.TryParse(rawLen.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out len))
            {
                _memWatchProblem = "The length has to be a whole number, up to "
                                 + MemoryWatchList.MaxLen.ToString(CultureInfo.InvariantCulture) + ".";
                RefreshWatchStatus();
                return;
            }
            if (!AddWatch(MemMapWatchAddrBox?.Text, SelectedWatchKind(), len, MemMapWatchLabelBox?.Text))
                return;   // what was typed stays in the box, so it can be fixed
            if (MemMapWatchAddrBox  != null) MemMapWatchAddrBox.Text = "";
            if (MemMapWatchLabelBox != null) MemMapWatchLabelBox.Text = "";
        }

        /// <summary>The one way a row gets on the list, from the box above or
        /// from a finding. Says why when it cannot, in the status line rather
        /// than a dialog, because this is a thing people do dozens of times in
        /// a session. The reason SURVIVES the next refresh: a live watch rewrites
        /// that line several times a second, and an error that flashes past
        /// between two refreshes may as well not have been shown.</summary>
        private bool AddWatch(string addr, string kind, int len, string label)
        {
            if (_plugin == null) return false;
            // Any half-typed name is written first: adding a row can redraw the
            // list, and a redraw takes the text boxes with it.
            FlushWatchLabels();
            string problem = _plugin.AddMemoryWatchRow(addr, kind, len, label, out var row);
            if (problem != null)
            {
                _memWatchProblem = problem;
                RefreshWatchStatus();
                return false;
            }
            _memWatchProblem = null;
            RebuildWatchRows();
            // A row added mid-run must not keep whatever it had shown before,
            // which for a corrected kind would be a lie about how it is now
            // being read, and for a fresh row a lie about an address nobody has
            // read yet.
            if (row != null)
            {
                row.HasRead = false;
                row.LastShown = null;
                row.Changes = 0;
                if (_memWatchUi.TryGetValue(row.Addr, out var ui))
                {
                    ui.ValueText.Text = _plugin.MemoryScanRunning ? "reading…" : "";
                    ui.MovedText.Text = "";
                }
            }
            return true;
        }

        private void MemMapWatchRemove_Click(object sender, RoutedEventArgs e)
        {
            string addr = (sender as Button)?.Tag as string;
            if (addr == null || _plugin == null) return;
            FlushWatchLabels();
            _memWatchProblem = null;
            if (_plugin.RemoveMemoryWatchRow(addr)) RebuildWatchRows();
        }

        private void MemMapWatchClear_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _memWatchPendingLabels.Clear();
            _memWatchProblem = null;
            _plugin.ClearMemoryWatchRows();
            RebuildWatchRows();
        }

        /// <summary>A row renamed. The model is updated on the keystroke so
        /// nothing is lost, and the file and the scanner are told a moment
        /// later: saving per keystroke would write the file five times for a
        /// five letter name.</summary>
        private void MemMapWatchLabel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var box = sender as TextBox;
            string addr = box?.Tag as string;
            if (addr == null) return;
            if (!_memWatchPendingLabels.Contains(addr)) _memWatchPendingLabels.Add(addr);
            if (_memWatchLabelSave == null)
            {
                _memWatchLabelSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _memWatchLabelSave.Tick += (s2, e2) => FlushWatchLabels();
            }
            _memWatchLabelSave.Stop();
            _memWatchLabelSave.Start();
        }

        private void FlushWatchLabels()
        {
            _memWatchLabelSave?.Stop();
            if (_plugin == null || _memWatchPendingLabels.Count == 0) return;
            var pending = new List<string>(_memWatchPendingLabels);
            _memWatchPendingLabels.Clear();
            foreach (string addr in pending)
            {
                if (!_memWatchUi.TryGetValue(addr, out var ui)) continue;
                _plugin.RelabelMemoryWatchRow(addr, ui.LabelBox.Text);
            }
            RefreshWatchStatus();
        }

        private void MemMapWatchRate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = MemMapWatchRateCombo?.SelectedItem as ComboBoxItem;
            if (item?.Tag is int hz) _plugin.MemoryWatchHz = hz;
            RefreshWatchStatus();
        }

        private void MemMapWatchStart_Click(object sender, RoutedEventArgs e)
            => StartMemMapRun(MemMapRunKind.Watch);

        /// <summary>Put a finding on the watch list, carrying its kind and its
        /// name across. This is the button that turns a claim into something
        /// checkable.</summary>
        private void MemMapWatchFinding_Click(object sender, RoutedEventArgs e)
        {
            var f = (sender as Button)?.Tag as ScanEvent;
            if (f == null) return;
            WatchSpecForFinding(f, out string addr, out string kind, out int len, out string label);
            AddWatch(addr, kind, len, label);
        }

        /// <summary>What the wheel says during a stage, which unlike a phase
        /// carries no cue of its own. Only four stage names exist and they are
        /// fixed by the protocol; anything else is shown on the tab alone rather
        /// than guessed at on the screen.</summary>
        private static string StageWheelCue(string stage)
        {
            switch ((stage ?? "").ToLowerInvariant())
            {
                // Warm-up and region enumeration both report as seed. Nothing is
                // sampled yet, so the instruction is the same for both.
                case "seed":   return "GET IN AND DRIVE";
                // The one that matters: every narrowing filter assumes the engine
                // speed is changing, so a parked car deletes the answer here.
                case "narrow": return "DRIVE, USE GEARS";
                case "record": return "FOLLOW THE CUES";
                case "rank":   return "DONE. WORKING";
                // A known-value search reports as "known" and asks for no
                // driving at all, so the wheel says what is happening rather
                // than telling a stationary operator to drive.
                case "known":  return "READING MEMORY";
                // The search is over and the rows are live. The one instruction
                // that settles the answer, on the screen the operator is
                // standing in front of.
                case "watch":  return "CHANGE THE CAR";
                default:       return "";
            }
        }

        /// <summary>The cue, cut down to what the wheel's small row can hold.
        /// The first clause carries the instruction ("STOPPED and IDLING" out of
        /// "STOPPED and IDLING. Touch nothing."), which is the half worth
        /// keeping.</summary>
        private static string ShortCue(string cue)
        {
            if (string.IsNullOrWhiteSpace(cue)) return "MEMORY MAP";
            string s = cue.Trim();
            int cut = s.IndexOfAny(new[] { '.', ',', ';', ':' });
            if (cut > 0) s = s.Substring(0, cut);
            s = s.Trim().ToUpperInvariant();
            if (s.Length > 18) s = s.Substring(0, 18).TrimEnd();
            return s.Length == 0 ? "MEMORY MAP" : s;
        }
    }
}
