// The field frame: the Memory Map tab's main view.
//
// What it replaced, and why. A search used to end in a flat list of findings,
// and two real name searches on the target returned 126 and 154 of them. Nobody
// reads 154 rows. Worse, sorting them by a score invites the operator to trust
// the order, and this tool has already put an engine-audio filter cutoff above
// a tachometer at 96.9 out of 100 in a program with no engine in it.
//
// So the main view is the MAP: one row per field, each either empty, holding
// candidates, or confirmed at an address with a name. A finding is a candidate
// FOR a field. Elimination is bulk, not click-through: the operator declares
// what they are about to do, does it, and everything that did not respond the
// way the field must respond is dropped in one go. The count falling is the
// product.
//
// Everything with a judgement in it lives in MemoryFieldMap.cs, where it is
// unit tested without a game, a scanner or a screen. This file paints, and
// nothing in it decides whether a candidate lives or dies.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        /// <summary>How many candidate rows get drawn. A search that returns a
        /// hundred and fifty is the case this exists for, and a hundred and
        /// fifty live controls on the UI thread is not. The ones past this are
        /// still candidates and are still eliminated with the rest: only the
        /// drawing is capped, which the overflow line says out loud.</summary>
        private const int MemMaxCandidateRows = 40;

        private sealed class MemFieldUiRow
        {
            public string Key;
            public Border Root;
            public TextBlock NameText;
            public TextBlock StateText;
            public TextBlock ValueText;
            public TextBlock RouteText;
        }

        private sealed class MemCandUiRow
        {
            public string Addr;
            public Border Root;
            public TextBlock ValueText;
            public TextBlock MovedText;
            /// <summary>The change count this row was last painted with. A row
            /// lights when that number goes UP, not when its text changes: the
            /// first value a row ever shows is the row arriving, and lighting it
            /// would say the operator's action moved something before they had
            /// done anything.</summary>
            public int ChangesShown;
            public DateTime LitUntilUtc;
            public bool Lit;
        }

        private readonly Dictionary<string, MemFieldUiRow> _memFieldUi =
            new Dictionary<string, MemFieldUiRow>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemCandUiRow> _memCandUi =
            new Dictionary<string, MemCandUiRow>(StringComparer.OrdinalIgnoreCase);

        private bool _memFieldsUiReady;
        private DateTime _memCandPaintedUtc = DateTime.MinValue;
        private string _memFieldProblem;
        /// <summary>Findings this run that could not be taken as candidates, and
        /// why. A search that overflows a field is the one case where the answer
        /// can be dropped without anybody noticing.</summary>
        private int _memCandRefused;
        private string _memCandRefusedWhy;
        /// <summary>Candidates that were taken but could not be put on the wire,
        /// and why. Different from refused and worse to hide: they ARE
        /// candidates, they sit in the list looking like every other row, and
        /// nothing is reading them, so a round cannot rule them in or out.</summary>
        private int _memCandUnarmed;
        private string _memCandUnarmedWhy;

        /// <summary>Pointer paths the scanner has reported this session, best
        /// first, keyed on the address they lead to. Not saved: a path is only
        /// worth keeping once it has been promoted into a map entry, and one
        /// found for a candidate that then got eliminated is noise.
        ///
        /// KEYED ON MemoryRoutes.AddressKey, never on the raw text. The key is
        /// written by whichever half sent the event, and the two halves pad an
        /// address differently: everything this cache holds arrives spelled the
        /// scanner's way and everything that looks in it asks the plugin's way.
        /// A plain string key misses every time on a 32-bit heap.</summary>
        private readonly Dictionary<string, List<ScanEvent>> _memPaths =
            new Dictionary<string, List<ScanEvent>>(StringComparer.Ordinal);

        /// <summary>A path search that was asked for and has not been answered.
        /// Held because the answer arrives later and asynchronously, and the
        /// thing waiting on it is a promotion that must not complete without
        /// one: a bare heap address is not a map entry.</summary>
        private sealed class MemPathWait
        {
            public string FieldKey;
            public string Addr;          // what paths are being sought for
            public string Label;         // the name a finished promotion will use
            public bool ForEntry;        // upgrade an entry, rather than finish a promotion
            public DateTime StartedUtc;
            public int Found;
            public bool GaveUp;
        }
        private MemPathWait _memPathWait;

        /// <summary>How long a path search is given before the tab says it has
        /// come back with nothing. Generous: a reverse index of several million
        /// pointer pairs plus a walk is not instant, and giving up early would
        /// send the operator to the escape hatch for no reason.</summary>
        private static readonly TimeSpan MemPathPatience = TimeSpan.FromSeconds(90);

        private static readonly TimeSpan MemCandLitFor = TimeSpan.FromMilliseconds(900);

        // ---- the field list --------------------------------------------------

        private MemoryFieldStore Store => _plugin?.MemoryFields;

        /// <summary>Point the map at whichever process the tab is aimed at. A
        /// map describes an executable's layout, so the process is the key and
        /// the game name is carried inside the file.
        ///
        /// A pid that changed means the game was restarted, which is the moment
        /// every entry has to earn "live" again: the whole value of a
        /// module-relative row or a pointer path is that it survives exactly
        /// this, and a row still marked live from the last launch would be the
        /// one lie this feature cannot afford.</summary>
        private int _memFieldsPid;

        private void PointMapAtTarget()
        {
            if (_plugin == null) return;
            var target = _memMapTarget;
            string key = target?.ProcessName;
            if (string.IsNullOrWhiteSpace(key))
                key = string.IsNullOrWhiteSpace(_plugin.ActiveGame) ? "unknown" : _plugin.ActiveGame;
            string game = target?.DisplayName ?? _plugin.ActiveGame;
            _plugin.LoadMemoryFields(key, game);
            int pid = target?.Pid ?? 0;
            if (pid != 0 && pid != _memFieldsPid)
            {
                _memFieldsPid = pid;
                _plugin.BeginMemoryFieldsLaunch();
            }
        }

        /// <summary>Redraw the field list, the summary and the panel for the
        /// selected field. Called when the map itself changes, never on a
        /// refresh of the values.</summary>
        private void RefreshFieldFrame()
        {
            if (MemMapFieldsHost == null || _plugin == null) return;
            EnsureFieldsUi();
            var store = Store;
            if (store == null) return;

            if (!FieldRowsAlreadyDrawn(store.Fields))
            {
                MemMapFieldsHost.Children.Clear();
                _memFieldUi.Clear();
                foreach (var f in store.Fields)
                {
                    var ui = BuildFieldRow(f);
                    _memFieldUi[f.Key] = ui;
                    MemMapFieldsHost.Children.Add(ui.Root);
                }
            }
            PaintFieldRows();
            PaintMapFileLine();
            RefreshSelectedField();
        }

        /// <summary>The blue a selected field row is picked out in. Static
        /// because the rows are repainted five times a second and a brush per
        /// row per refresh is thirty-five allocations a second for a colour that
        /// never changes.</summary>
        private static readonly Brush MemSelectedBrush =
            new SolidColorBrush(Color.FromArgb(0x33, 0x4F, 0xA3, 0xE3));

        private void EnsureFieldsUi()
        {
            if (_memFieldsUiReady) return;
            _memFieldsUiReady = true;
            PointMapAtTarget();
        }

        private bool FieldRowsAlreadyDrawn(IReadOnlyList<MemoryField> fields)
        {
            if (MemMapFieldsHost == null) return false;
            if (fields.Count != MemMapFieldsHost.Children.Count) return false;
            if (fields.Count != _memFieldUi.Count) return false;
            for (int i = 0; i < fields.Count; i++)
            {
                if (!_memFieldUi.TryGetValue(fields[i].Key, out var ui)) return false;
                if (!ReferenceEquals(ui.Root, MemMapFieldsHost.Children[i])) return false;
            }
            return true;
        }

        private MemFieldUiRow BuildFieldRow(MemoryField f)
        {
            var grid = new Grid();
            // Mirrored in the XAML header. Change one, change the other.
            foreach (double w in new double[] { 150, 128, -1, 118 })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            var name = new TextBlock
            {
                Text = f.Name ?? f.Key,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var state = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

            var value = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);

            var route = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(route, 3);
            grid.Children.Add(route);

            var root = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Child = grid,
                Tag = f.Key,
                ToolTip = "Work on this field: its candidates, and what it was confirmed at.",
            };
            root.MouseLeftButtonUp += MemMapField_Click;

            return new MemFieldUiRow
            {
                Key = f.Key,
                Root = root,
                NameText = name,
                StateText = state,
                ValueText = value,
                RouteText = route,
            };
        }

        /// <summary>Write the state, the live value and the anchoring into every
        /// field row. Cheap enough to call on a refresh: four text writes per
        /// field, and only when they changed.</summary>
        private void PaintFieldRows()
        {
            var store = Store;
            if (store == null) return;
            foreach (var f in store.Fields)
            {
                if (!_memFieldUi.TryGetValue(f.Key, out var ui)) continue;
                bool selected = string.Equals(f.Key, store.SelectedKey, StringComparison.OrdinalIgnoreCase);

                string stateText;
                Brush stateBrush;
                switch (f.State)
                {
                    case MemoryFieldState.Confirmed:
                        stateText = f.Entry.StateWord == "STALE" ? "confirmed, STALE"
                                  : f.Entry.StateWord == "live" ? "confirmed, live"
                                  : "confirmed";
                        stateBrush = f.Entry.StateWord == "STALE" ? MemFailBrush
                                   : f.Entry.StateWord == "live" ? MemPassBrush : MemSkipBrush;
                        break;
                    case MemoryFieldState.Candidates:
                        stateText = f.Candidates.Count.ToString(CultureInfo.InvariantCulture)
                                  + (f.Candidates.Count == 1 ? " candidate" : " candidates");
                        stateBrush = MemAmberBrush;
                        break;
                    default:
                        stateText = "nothing yet";
                        stateBrush = MemSkipBrush;
                        break;
                }
                if (!string.Equals(ui.StateText.Text, stateText, StringComparison.Ordinal))
                    ui.StateText.Text = stateText;
                if (!ReferenceEquals(ui.StateText.Foreground, stateBrush))
                    ui.StateText.Foreground = stateBrush;

                string value = f.Entry != null
                    ? (f.Entry.HasRead ? (f.Entry.Shown ?? "") : "")
                    : "";
                if (!string.Equals(ui.ValueText.Text, value, StringComparison.Ordinal))
                {
                    ui.ValueText.Text = value;
                    ui.ValueText.ToolTip = value.Length > 0 ? value : null;
                }

                string route = f.Entry == null ? ""
                             : MemoryRoutes.Word(f.Entry.Route)
                               + (f.Entry.Launches > 1
                                  ? ", " + f.Entry.Launches.ToString(CultureInfo.InvariantCulture) + " launches"
                                  : "");
                if (!string.Equals(ui.RouteText.Text, route, StringComparison.Ordinal))
                {
                    ui.RouteText.Text = route;
                    ui.RouteText.ToolTip = f.Entry == null ? null
                        : MemoryRoutes.Durability(f.Entry.Route);
                }

                var back = selected ? MemSelectedBrush : Brushes.Transparent;
                if (!ReferenceEquals(ui.Root.Background, back)) ui.Root.Background = back;
                var weight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                if (ui.NameText.FontWeight != weight) ui.NameText.FontWeight = weight;
            }

            if (MemMapFieldsSummaryText != null)
            {
                string summary = store.Summary();
                // What is on the list but NOT on the wire, said in the same
                // sentence as what is on the map. The scanner's watch list has a
                // ceiling shared with the hand-curated rows, and an address
                // nobody is reading can never be ruled in or out: from a round's
                // point of view it is indistinguishable from one that was read
                // and did not move, so it survives every round forever. Counted
                // in two places because it can happen in two: when the whole map
                // is armed, and when the scanner refuses a row outright.
                int notArmed = _plugin?.MemoryFieldsNotArmed ?? 0;
                if (notArmed > 0)
                    summary += "  " + notArmed.ToString(CultureInfo.InvariantCulture)
                             + " address(es) are on the map but NOT being read: the scanner's watch list holds "
                             + MemoryFieldStore.ScannerWatchRows.ToString(CultureInfo.InvariantCulture)
                             + " rows and " + (_plugin?.MemoryWatch.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                             + " of them are the watch list's own. A round cannot judge those, and it says so "
                             + "rather than ruling them out.";
                if (_memWatchRefused > 0)
                    summary += "  " + _memWatchRefused.ToString(CultureInfo.InvariantCulture)
                             + " row(s) were refused outright by the scanner this run.";
                if (!string.Equals(MemMapFieldsSummaryText.Text, summary, StringComparison.Ordinal))
                    MemMapFieldsSummaryText.Text = summary;
            }
        }

        /// <summary>Which file the map is in, and whether it saved. Out of the
        /// repaint on purpose: the rows are rewritten five times a second while
        /// a watch is live, and this line changes only when the target or a save
        /// does.</summary>
        private void PaintMapFileLine()
        {
            if (MemMapFieldsFileText == null || _plugin == null) return;
            var store = Store;
            var parts = new List<string>();
            parts.Add("Map file: " + _plugin.MemoryFieldsPath(_plugin.MemoryFieldsKey));
            if (store != null && !string.IsNullOrWhiteSpace(store.Game))
                parts.Add("It belongs to the process, not to the title, so it keeps working whatever "
                        + "SimHub is calling " + store.Game + ".");
            string save = _plugin.MemoryFieldsSaveProblem;
            if (save != null)
                parts.Add("It could not be saved, so nothing here will survive a restart: " + save);
            MemMapFieldsFileText.Text = string.Join("  ", parts);
        }

        private void MemMapField_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string key = (sender as Border)?.Tag as string;
            if (key == null) return;
            SelectField(key);
        }

        private void SelectField(string key)
        {
            var store = Store;
            if (store == null) return;
            if (string.Equals(store.SelectedKey, key, StringComparison.OrdinalIgnoreCase)) return;
            // A round belongs to the field it was started on. Switching away
            // mid-round would leave it running against candidates nobody is
            // looking at, and it would finish against whatever was selected when
            // the button was next pressed.
            if (store.RoundLive) store.CancelRound();
            store.SelectedKey = key;
            _memFieldProblem = null;
            // The lines under the candidates are about the field that was there
            // a moment ago. Cleared so the new field gets its own instruction
            // back rather than the last one's result.
            if (MemMapPromoteText != null) MemMapPromoteText.Text = "";
            if (MemMapEliminateText != null) MemMapEliminateText.Text = "";
            _plugin?.SaveMemoryFields();
            PaintFieldRows();
            PaintMapFileLine();
            RefreshSelectedField();
        }

        // ---- the selected field ----------------------------------------------

        private void RefreshSelectedField()
        {
            var store = Store;
            if (store == null || MemMapFieldBox == null) return;
            var field = store.Selected;
            if (field == null)
            {
                MemMapFieldBox.Visibility = Visibility.Collapsed;
                return;
            }
            MemMapFieldBox.Visibility = Visibility.Visible;
            var def = field.Def;

            if (MemMapFieldTitle != null)
                MemMapFieldTitle.Text = field.Name ?? field.Key;
            if (MemMapFieldWhat != null)
                MemMapFieldWhat.Text = def.What ?? "";

            RefreshEntryBox(field);
            RefreshEliminateBox(field);
            RebuildCandidateRows(field);
            RefreshPromoteRow(field);
        }

        private void RefreshEntryBox(MemoryField field)
        {
            if (MemMapEntryBox == null) return;
            var e = field.Entry;
            if (e == null)
            {
                MemMapEntryBox.Visibility = Visibility.Collapsed;
                return;
            }
            MemMapEntryBox.Visibility = Visibility.Visible;
            if (MemMapEntryText != null)
                MemMapEntryText.Text = e.Label + "   " + e.Addr + "   " + e.Kind
                                     + (MemoryWatchList.IsSizedKind(e.Kind) && e.Len > 0
                                        ? " " + e.Len.ToString(CultureInfo.InvariantCulture) : "");

            if (MemMapEntryStateText != null)
            {
                var parts = new List<string>();
                switch (e.StateWord)
                {
                    case "live":
                        parts.Add("LIVE. It resolved this launch and read back "
                                + (e.Shown ?? "") + ", which is plausible for this field.");
                        break;
                    case "STALE":
                        parts.Add("STALE: " + e.StaleWhy
                                + ". It is not being used for anything until it is found again.");
                        if (!string.IsNullOrEmpty(e.LastGood))
                            parts.Add("It used to read " + e.LastGood + ".");
                        break;
                    default:
                        parts.Add("Not checked this launch. Nothing has read it yet, so it is neither "
                                + "trusted nor written off. Start a watch with the game running.");
                        break;
                }
                MemMapEntryStateText.Text = string.Join(" ", parts);
                MemMapEntryStateText.Foreground = e.StateWord == "STALE" ? MemFailBrush
                                                : e.StateWord == "live" ? MemPassBrush : MemSkipBrush;
            }

            // A path search in flight owns this line: it is the only place its
            // progress is reported, and a routine resync overwriting it would
            // leave the operator watching a paragraph about durability while the
            // thing they pressed quietly finished or quietly did not.
            bool pathInFlight = _memPathWait != null && _memPathWait.ForEntry
                             && string.Equals(_memPathWait.FieldKey, field.Key, StringComparison.OrdinalIgnoreCase);
            if (MemMapEntryHowText != null && !pathInFlight)
            {
                var parts = new List<string>();
                parts.Add(MemoryRoutes.Durability(e.Route) + ".");
                if (!string.IsNullOrEmpty(e.Fallback))
                    parts.Add("Fallback: " + e.Fallback
                            + (e.FallbackVolatile ? " (good for the launch it was found in only)." : "."));
                parts.Add("Confirmed " + e.ConfirmedUtc.ToLocalTime()
                            .ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
                        + ": " + (e.ConfirmedHow ?? "no record") + ".");
                parts.Add(e.Launches > 1
                    ? "It has resolved and read plausibly in " + e.Launches.ToString(CultureInfo.InvariantCulture)
                      + " separate launches, which is what makes the route durable rather than lucky."
                    : e.Launches == 1
                        ? "It has only resolved in one launch so far. A route validated across a relaunch is "
                          + "worth a great deal more, so come back to it after restarting the game."
                        : "It has not been read in any launch yet.");
                if (!string.IsNullOrEmpty(e.Run)) parts.Add("Found by the run in " + e.Run + ".");
                MemMapEntryHowText.Text = string.Join("  ", parts);
            }

            if (MemMapEntryPathButton != null)
            {
                // Only worth offering when the entry is not already resolved
                // from something that cannot move.
                bool wants = e.Route == MemoryRoute.Absolute;
                MemMapEntryPathButton.Visibility = wants ? Visibility.Visible : Visibility.Collapsed;
                MemMapEntryPathButton.IsEnabled = wants && _plugin?.MemoryScanRunning == true;
            }
        }

        private void RefreshEliminateBox(MemoryField field)
        {
            if (MemMapEliminateBox == null) return;
            var store = Store;
            bool any = field.Candidates != null && field.Candidates.Count > 0;
            MemMapEliminateBox.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any) return;

            // The actions offered follow the field, so "I am about to change
            // car" is not on the list for a lap timer.
            if (MemMapEliminateCombo != null)
            {
                var want = field.Def.Actions;
                bool same = MemMapEliminateCombo.Items.Count == want.Count;
                if (same)
                    for (int i = 0; i < want.Count; i++)
                        if (!ReferenceEquals((MemMapEliminateCombo.Items[i] as ComboBoxItem)?.Tag, want[i]))
                        { same = false; break; }
                if (!same)
                {
                    bool was = _suppressEvents;
                    _suppressEvents = true;
                    try
                    {
                        MemMapEliminateCombo.Items.Clear();
                        foreach (var a in want)
                            MemMapEliminateCombo.Items.Add(new ComboBoxItem { Content = a.Prompt, Tag = a });
                        MemMapEliminateCombo.SelectedIndex = 0;
                    }
                    finally { _suppressEvents = was; }
                }
            }

            var chosen = SelectedEliminationAction();
            if (MemMapEliminateProvesText != null)
                MemMapEliminateProvesText.Text = store.RoundLive
                    ? "Round running: " + store.RoundAction.Prompt.ToLowerInvariant()
                      + ". Go and do it now, then press Done."
                    : chosen?.Proves ?? "";

            if (MemMapEliminateStartButton  != null) MemMapEliminateStartButton.IsEnabled  = !store.RoundLive;
            if (MemMapEliminateFinishButton != null) MemMapEliminateFinishButton.IsEnabled = store.RoundLive;
            if (MemMapEliminateCancelButton != null) MemMapEliminateCancelButton.IsEnabled = store.RoundLive;
            if (MemMapEliminateUndoButton   != null) MemMapEliminateUndoButton.IsEnabled   = store.CanUndo;
            if (MemMapEliminateCombo        != null) MemMapEliminateCombo.IsEnabled        = !store.RoundLive;
        }

        private MemoryEliminationAction SelectedEliminationAction()
            => (MemMapEliminateCombo?.SelectedItem as ComboBoxItem)?.Tag as MemoryEliminationAction;

        /// <summary>The parts of the field panel that follow whether a scanner
        /// is up, without rebuilding the candidate rows. Called from the same
        /// place every other button on this tab is synced, and a rebuild there
        /// would redraw forty controls every time a run changed state.</summary>
        private void SyncFieldButtons()
        {
            var field = Store?.Selected;
            if (field == null) return;
            RefreshEntryBox(field);
            RefreshEliminateBox(field);
            RefreshCandidateStatus(field, field.Candidates?.Count ?? 0, _memCandUi.Count);
            RefreshPromoteRow(field);
            PaintMapFileLine();
        }

        // ---- candidates -------------------------------------------------------

        private void RebuildCandidateRows(MemoryField field)
        {
            if (MemMapCandidateHost == null) return;
            MemMapCandidateHost.Children.Clear();
            _memCandUi.Clear();

            var ordered = Store?.CandidatesInOrder(field.Key);
            int total = ordered?.Count ?? 0;
            if (MemMapCandidateHeader != null)
                MemMapCandidateHeader.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;

            int drawn = 0;
            if (ordered != null)
            {
                foreach (var c in ordered)
                {
                    if (drawn >= MemMaxCandidateRows) break;
                    var ui = BuildCandidateRow(field.Key, c);
                    _memCandUi[c.Addr] = ui;
                    MemMapCandidateHost.Children.Add(ui.Root);
                    drawn++;
                }
            }
            PaintCandidateValues();
            RefreshCandidateStatus(field, total, drawn);
        }

        private MemCandUiRow BuildCandidateRow(string fieldKey, MemoryFieldCandidate c)
        {
            var grid = new Grid();
            // Mirrored in the XAML header. Change one, change the other.
            foreach (double w in new double[] { 24, 186, 64, 64, -1, 48, -2 })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w == -1 ? new GridLength(1, GridUnitType.Star)
                          : w == -2 ? GridLength.Auto
                          : new GridLength(w),
                });

            var remove = new Button
            {
                Content = "×",
                Width = 18,
                Height = 18,
                FontSize = 12,
                Padding = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = c.Addr,
                ToolTip = "Rule this one out by hand.",
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += MemMapCandidateRemove_Click;
            Grid.SetColumn(remove, 0);
            grid.Children.Add(remove);

            var addr = new TextBlock
            {
                Text = c.BestAddr,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = string.IsNullOrEmpty(c.Note) ? c.BestAddr : c.BestAddr + "\n" + c.Note,
            };
            Grid.SetColumn(addr, 1);
            grid.Children.Add(addr);

            // Anchored or not is a FACT about durability, not a guess about
            // which candidate is right, which is why it is the only thing on
            // this row allowed to look like a verdict.
            var anchor = new TextBlock
            {
                Text = c.IsAnchored ? "static" : "heap",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = c.IsAnchored ? MemPassBrush : MemSkipBrush,
                ToolTip = c.IsAnchored
                    ? "Inside a loaded image, so it resolves again after a relaunch."
                    : "On the heap. It needs a pointer path before it is worth writing into the map.",
            };
            Grid.SetColumn(anchor, 2);
            grid.Children.Add(anchor);

            var kind = new TextBlock
            {
                Text = c.Kind ?? "",
                FontSize = 11,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(kind, 3);
            grid.Children.Add(kind);

            var value = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0),
            };
            Grid.SetColumn(value, 4);
            grid.Children.Add(value);

            var moved = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "How many times this value has moved since the scan started.",
            };
            Grid.SetColumn(moved, 5);
            grid.Children.Add(moved);

            var promote = new Button
            {
                Content = "This is it",
                Height = 22,
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = c.Addr,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Promote this address into the map as " + (Store?.Selected?.Name ?? fieldKey) + ".",
            };
            promote.Click += MemMapCandidatePromote_Click;
            Grid.SetColumn(promote, 6);
            grid.Children.Add(promote);

            var root = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2, 2, 2, 2),
                Margin = new Thickness(0, 0, 0, 2),
                Child = grid,
            };

            // Seeded with what this candidate has already counted, so a field
            // reselected mid-watch does not light every row it redraws.
            return new MemCandUiRow
            {
                Addr = c.Addr,
                Root = root,
                ValueText = value,
                MovedText = moved,
                ChangesShown = c.Changes,
            };
        }

        /// <summary>Put the live value into every drawn candidate row. Called
        /// from the watch refresh, throttled: at 5 per second across forty rows
        /// this is the only thing on the tab that costs anything.</summary>
        private void PaintCandidateValues()
        {
            var store = Store;
            var field = store?.Selected;
            if (field?.Candidates == null) return;
            bool running = _plugin?.MemoryScanRunning == true;
            var now = DateTime.UtcNow;
            foreach (var c in field.Candidates)
            {
                if (!_memCandUi.TryGetValue(c.Addr, out var ui)) continue;
                string shown = c.HasRead ? (c.Shown ?? "") : running ? "reading…" : "";
                if (!string.Equals(ui.ValueText.Text, shown, StringComparison.Ordinal))
                {
                    ui.ValueText.Text = shown;
                    ui.ValueText.ToolTip = shown.Length > 0 ? shown : null;
                }
                // Lit on a CHANGE, which is the count going up, and not on the
                // row arriving. Watching for the one row that followed when you
                // changed the car is the whole point, and a list that lights
                // itself up the moment it starts reading answers nothing.
                if (c.Changes != ui.ChangesShown)
                {
                    if (c.Changes > ui.ChangesShown)
                    {
                        ui.Root.Background = MemChangedBrush;
                        ui.LitUntilUtc = now + MemCandLitFor;
                        ui.Lit = true;
                        // The watch list's fade timer sweeps these too. Started
                        // here because this paint only runs while refreshes are
                        // arriving, and a row lit by the last one before a watch
                        // stopped would stay lit for the session.
                        if (_memWatchFade == null)
                        {
                            _memWatchFade = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromMilliseconds(150) };
                            _memWatchFade.Tick += MemWatchFade_Tick;
                        }
                        _memWatchFade.Start();
                    }
                    ui.ChangesShown = c.Changes;
                }
                var want = c.HasRead && !c.LastOk ? MemSkipBrush : null;
                if (!ReferenceEquals(ui.ValueText.Foreground, want))
                {
                    if (want == null) ui.ValueText.ClearValue(TextBlock.ForegroundProperty);
                    else ui.ValueText.Foreground = want;
                }
                string moved = c.Changes > 0 ? c.Changes.ToString(CultureInfo.InvariantCulture) : "";
                if (!string.Equals(ui.MovedText.Text, moved, StringComparison.Ordinal))
                    ui.MovedText.Text = moved;
                if (ui.Lit && now >= ui.LitUntilUtc)
                {
                    ui.Root.ClearValue(Border.BackgroundProperty);
                    ui.Lit = false;
                }
            }
        }

        private void RefreshCandidateStatus(MemoryField field, int total, int drawn)
        {
            if (MemMapCandidateStatusText == null) return;
            var parts = new List<string>();
            if (_memFieldProblem != null) parts.Add(_memFieldProblem);

            if (total == 0)
            {
                parts.Add(field.Entry == null
                    ? "No candidates yet. Run a search below, and whatever it finds lands here."
                    : "No candidates: this field is confirmed. Press Find it again to start over.");
            }
            else
            {
                parts.Add(total.ToString(CultureInfo.InvariantCulture)
                        + (total == 1 ? " candidate." : " candidates."));
                if (drawn < total)
                    parts.Add("The first " + drawn.ToString(CultureInfo.InvariantCulture)
                            + " are drawn here, in address order rather than by any score, and the rest are "
                            + "not on screen. All " + total.ToString(CultureInfo.InvariantCulture)
                            + " are being watched and eliminated together, so a round still filters the ones "
                            + "you cannot see.");
                if (field.RoundsRun != null && field.RoundsRun.Count > 0)
                {
                    var words = new List<string>();
                    foreach (string id in field.RoundsRun)
                    {
                        var a = MemoryEliminationActions.ById(id);
                        if (a != null) words.Add(a.Prompt.Replace("I am about to ", ""));
                    }
                    if (words.Count > 0)
                        parts.Add("Already survived: " + string.Join(", then ", words) + ".");
                }
                bool running = _plugin?.MemoryScanRunning == true;
                if (!running)
                    parts.Add("Nothing is reading these right now, so a round would judge nothing. "
                            + "Press Watch now further down to start a scanner that does.");
            }
            MemMapCandidateStatusText.Text = string.Join("  ", parts);
        }

        private void RefreshPromoteRow(MemoryField field)
        {
            if (MemMapPromoteRow == null) return;
            bool any = field.Candidates != null && field.Candidates.Count > 0;
            MemMapPromoteRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (MemMapPromoteLabelBox != null && any
                && string.IsNullOrWhiteSpace(MemMapPromoteLabelBox.Text))
            {
                bool was = _suppressEvents;
                _suppressEvents = true;
                try { MemMapPromoteLabelBox.Text = field.Name ?? field.Key; }
                finally { _suppressEvents = was; }
            }
            if (MemMapPromoteText != null && MemMapPromoteText.Text.Length == 0 && any)
                MemMapPromoteText.Text =
                    "Press This is it on the row you believe. An address inside a loaded image goes straight "
                  + "into the map. One on the heap does not: the scanner is asked for a pointer path first, "
                  + "because a bare heap address is a number good for one launch and not a map entry.";
            // The escape hatch belongs to the field the promotion was started
            // on. Left visible across a field switch it would write an address
            // into whichever field it was actually waiting on, which is not the
            // one on screen.
            var pending = _memPathWait;
            bool mine = pending != null
                     && string.Equals(pending.FieldKey, field.Key, StringComparison.OrdinalIgnoreCase);
            if (MemMapPromoteAnywayButton != null)
            {
                bool offer = mine && pending.GaveUp && !pending.ForEntry;
                MemMapPromoteAnywayButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MemMapFindPathButton != null)
            {
                MemMapFindPathButton.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
                // A paths run is a run, so it needs everything every other run
                // needs: the scanner deployed, and a target to point it at. And
                // it is dead while one is in flight, or a second press would
                // stop the search it started and begin the same one again.
                string exe = _plugin?.MemoryScanExePath();
                bool busy = _plugin?.MemoryScanRunning == true && _memMapRunKind == MemMapRunKind.Paths;
                MemMapFindPathButton.IsEnabled = mine && !busy && _memMapTarget != null
                                              && exe != null && MemoryScanDeployGap(exe) == null;
            }
        }

        // ---- handlers ---------------------------------------------------------

        private void MemMapFieldNew_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (MemMapFieldAddButton != null)
                MemMapFieldAddButton.IsEnabled = !string.IsNullOrWhiteSpace(MemMapFieldNewBox?.Text);
        }

        private void MemMapFieldAdd_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            if (store == null) return;
            string problem = store.AddCustomField(MemMapFieldNewBox?.Text, out var field);
            if (problem != null) { _memFieldProblem = problem; RefreshSelectedField(); return; }
            _memFieldProblem = null;
            if (MemMapFieldNewBox != null) MemMapFieldNewBox.Text = "";
            store.SelectedKey = field.Key;
            _plugin?.SaveMemoryFields();
            RefreshFieldFrame();
        }

        private void MemMapEliminateAction_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var field = Store?.Selected;
            if (field != null) RefreshEliminateBox(field);
        }

        private void MemMapEliminateStart_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            var field = store?.Selected;
            if (field == null) return;
            var action = SelectedEliminationAction();
            string problem = store.BeginRound(field.Key, action?.Id);
            if (problem != null)
            {
                if (MemMapEliminateText != null) MemMapEliminateText.Text = problem;
                return;
            }
            _memRoundChangedHere = -1;
            // The scanner measures the round as well, over EVERY refresh rather
            // than only the ones this panel happened to see. It measures; this
            // side decides what the measurement means for a field. Sending the
            // declaration also puts it in the scanner's own report, which is
            // where the evidence behind a map entry has to be findable later.
            var judged = new List<string>(field.Candidates.Count);
            foreach (var c in field.Candidates) judged.Add(c.Addr);
            string sent = _plugin?.SendMemoryRoundStart(field.Key,
                action.Expect == MemoryFieldExpect.Change ? "change" : "hold", judged);
            if (MemMapEliminateText != null)
                MemMapEliminateText.Text =
                    "Round running on " + field.Candidates.Count.ToString(CultureInfo.InvariantCulture)
                  + " candidate(s). Go and " + action.Prompt.Replace("I am about to ", "")
                  + ", give it a couple of seconds so every row gets read, then press Done."
                  + (sent == null ? "" : "  The scanner was not told (" + sent
                     + "), so its own measurement will not be there to check this one against.");
            RefreshEliminateBox(field);
        }

        private void MemMapEliminateFinish_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            if (store == null) return;
            var result = store.FinishRound();
            if (result == null) return;
            // Asked for BEFORE this half prints its own answer, so the scanner's
            // count arrives to be appended to it rather than to a line that has
            // already been replaced.
            _memRoundChangedHere = result.Action.Expect == MemoryFieldExpect.Change
                ? result.After - result.Unread : result.Eliminated;
            _plugin?.SendMemoryRoundEnd();
            if (MemMapEliminateText != null) MemMapEliminateText.Text = result.Line;
            _plugin?.SaveMemoryFields();
            // The scanner is still holding the addresses that have just been
            // ruled out. Sent as a difference, never as a clear and a rebuild: a
            // clear takes the watch list down with it and blanks every value on
            // screen, which is the evidence the round just produced.
            _plugin?.ResyncMemoryFieldWatch();
            RefreshFieldFrame();
        }

        private void MemMapEliminateCancel_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            if (store == null) return;
            store.CancelRound();
            _memRoundChangedHere = -1;
            // The scanner is holding an open round of its own. Closed here as
            // well, or its next round-start would land on one already running.
            _plugin?.SendMemoryRoundEnd();
            if (MemMapEliminateText != null)
                MemMapEliminateText.Text = "Round cancelled. Nothing was ruled out.";
            RefreshEliminateBox(store.Selected);
        }

        private void MemMapEliminateUndo_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            if (store == null) return;
            if (!store.UndoLastRound()) return;
            if (MemMapEliminateText != null)
                MemMapEliminateText.Text = "Put back. The last round has been undone, so nothing it ruled out is lost.";
            _plugin?.SaveMemoryFields();
            _plugin?.ResyncMemoryFieldWatch();
            RefreshFieldFrame();
        }

        private void MemMapCandidateRemove_Click(object sender, RoutedEventArgs e)
        {
            string addr = (sender as Button)?.Tag as string;
            var store = Store;
            var field = store?.Selected;
            if (addr == null || field == null) return;
            if (!store.RemoveCandidate(field.Key, addr)) return;
            _plugin?.SaveMemoryFields();
            RefreshFieldFrame();
        }

        private void MemMapCandidatePromote_Click(object sender, RoutedEventArgs e)
        {
            string addr = (sender as Button)?.Tag as string;
            var store = Store;
            var field = store?.Selected;
            if (addr == null || field == null) return;

            MemoryFieldCandidate cand = null;
            foreach (var c in field.Candidates)
                if (string.Equals(c.Addr, addr, StringComparison.OrdinalIgnoreCase)) { cand = c; break; }
            if (cand == null) return;

            string label = MemMapPromoteLabelBox?.Text;

            // Already durable, or a path already found for it: promote now.
            if (cand.IsAnchored) { FinishPromotion(field.Key, addr, label, null); return; }
            string known = BestPathFor(addr);
            if (known != null) { FinishPromotion(field.Key, addr, label, known); return; }

            // On the heap and no path yet. A bare heap address is not a map
            // entry, so the scanner is asked for one BEFORE the entry exists at
            // all rather than after, which would leave a wrong-looking row in
            // the map while the answer was still being worked out.
            //
            // A path search is its own RUN, not a line on the wire: the scanner
            // refuses to combine one with a search or a watch-only host, on the
            // grounds that a search is how you get the address a path search
            // starts from. So this arms the request and the operator presses the
            // button, because starting it stops whatever is running now.
            _memPathWait = new MemPathWait
            {
                FieldKey = field.Key,
                Addr = addr,
                Label = label,
                ForEntry = false,
                StartedUtc = DateTime.UtcNow,
            };
            if (MemMapPromoteText != null)
                MemMapPromoteText.Text =
                    "That address is on the heap, so it is true for this launch of this process and nothing "
                  + "more. A pointer path is what makes it a map entry: the scanner reads memory for four "
                  + "byte values that land near it and walks back from them, preferring a root in the game's "
                  + "own executable, which cannot be moved. It only reads.  Press Find a pointer path to "
                  + "start that search. It is a run of its own, so it stops whatever is running now, and it "
                  + "keeps the rest of the map live while it works.";
            if (MemMapFindPathButton != null) MemMapFindPathButton.Visibility = Visibility.Visible;
            if (MemMapPromoteAnywayButton != null)
                MemMapPromoteAnywayButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>Start the pointer-path run the pending promotion is waiting
        /// on. Stops whatever is running first, because the scanner will not
        /// combine a paths run with anything else and would exit with a usage
        /// error rather than pick one.</summary>
        private void MemMapFindPath_Click(object sender, RoutedEventArgs e)
        {
            var wait = _memPathWait;
            if (wait == null || _plugin == null) return;
            wait.GaveUp = false;
            wait.Found = 0;
            wait.StartedUtc = DateTime.UtcNow;
            if (MemMapFindPathButton != null) MemMapFindPathButton.IsEnabled = false;
            if (_plugin.MemoryScanRunning)
            {
                SetMemStage("Stopping the current run so the pointer-path search can have the scanner.");
                _memAborting = true;
                SyncMemMapButtons();
                _plugin.StopMemoryScanPolitely((clean, detail) =>
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        _memAborting = false;
                        StartMemMapRun(MemMapRunKind.Paths);
                    })));
                return;
            }
            StartMemMapRun(MemMapRunKind.Paths);
        }

        /// <summary>Fill in the paths half of a scan request from the pending
        /// wait. False when there is nothing to search for.
        ///
        /// The field's plausibility range travels with it. That is not
        /// decoration: it is stored beside the path in the scanner's own file,
        /// and it is what lets a later validation run tell a chain that still
        /// resolves from one that still reads the field.</summary>
        private bool FillPathsRequest(MemoryScanRequest req)
        {
            var wait = _memPathWait;
            if (wait == null || string.IsNullOrWhiteSpace(wait.Addr))
            {
                SetMemStage("There is no address to find a pointer path to.");
                ShowLiveBox(true);
                return false;
            }
            var store = Store;
            var field = store?.Find(wait.FieldKey);
            string kind = null; int len = 0;
            if (field != null)
            {
                if (field.Entry != null && string.Equals(field.Entry.Addr, wait.Addr, StringComparison.OrdinalIgnoreCase))
                { kind = field.Entry.Kind; len = field.Entry.Len; }
                else if (field.Candidates != null)
                    foreach (var c in field.Candidates)
                        if (string.Equals(c.Addr, wait.Addr, StringComparison.OrdinalIgnoreCase))
                        { kind = c.Kind; len = c.Len; break; }
            }
            req.PathsTo = new List<string> { wait.Addr };
            req.PathDepth = TrueforcePlugin.MemoryPathDepth;
            req.PathWindow = TrueforcePlugin.MemoryPathWindow;
            req.PathMax = TrueforcePlugin.MemoryPathMax;
            req.PathKind = kind;
            req.PathLen = len;
            req.PathLabel = string.IsNullOrWhiteSpace(wait.Label) ? (field?.Name ?? wait.FieldKey) : wait.Label;
            var sanity = field?.Def.Sanity;
            if (sanity != null && sanity.Kind == MemorySanity.Shape.Number)
            { req.PathExpectMin = sanity.Min; req.PathExpectMax = sanity.Max; }
            // The rest of the map stays live through the search. Without this it
            // goes dark for however long the walk takes, and a candidate nobody
            // is reading cannot be ruled in or out either way.
            req.PathWatchHold = _plugin.MemoryWatch.Count > 0 || _plugin.MemoryFields.ReadableCount > 0;
            return true;
        }

        private void ShowPromoteAnyway(string fieldKey, string addr, string label, bool gaveUp)
        {
            _memPathWait = new MemPathWait
            {
                FieldKey = fieldKey,
                Addr = addr,
                Label = label,
                ForEntry = false,
                StartedUtc = DateTime.UtcNow,
                GaveUp = gaveUp,
            };
            if (MemMapPromoteAnywayButton != null)
                MemMapPromoteAnywayButton.Visibility = Visibility.Visible;
        }

        private void MemMapPromoteAnyway_Click(object sender, RoutedEventArgs e)
        {
            var wait = _memPathWait;
            if (wait == null) return;
            _memPathWait = null;
            if (MemMapPromoteAnywayButton != null)
                MemMapPromoteAnywayButton.Visibility = Visibility.Collapsed;
            if (MemMapFindPathButton != null) MemMapFindPathButton.Visibility = Visibility.Collapsed;
            FinishPromotion(wait.FieldKey, wait.Addr, wait.Label, null);
        }

        private void FinishPromotion(string fieldKey, string addr, string label, string pathAddr)
        {
            var store = Store;
            if (store == null) return;
            string problem = store.Promote(fieldKey, addr, label, pathAddr,
                                           _memOutDir ?? _plugin?.LastMemoryScanOutDir, out var entry);
            if (problem != null)
            {
                if (MemMapPromoteText != null) MemMapPromoteText.Text = problem;
                return;
            }
            _memPathWait = null;
            if (MemMapPromoteAnywayButton != null)
                MemMapPromoteAnywayButton.Visibility = Visibility.Collapsed;
            if (MemMapFindPathButton != null) MemMapFindPathButton.Visibility = Visibility.Collapsed;
            if (MemMapPromoteText != null)
                MemMapPromoteText.Text =
                    "Written into the map as " + entry.Label + ", "
                  + MemoryRoutes.Durability(entry.Route) + "."
                  + (entry.Route == MemoryRoute.Absolute
                     ? "  This one will not survive a relaunch, and the file says so. Find a pointer path for "
                       + "it before relying on it."
                     : "  Restart the game and come back: an entry that resolves in a second launch is the "
                       + "only kind that has actually been proved.");
            if (MemMapPromoteLabelBox != null) MemMapPromoteLabelBox.Text = "";
            _plugin?.SaveMemoryFields();
            // The map's rows changed, so what the scanner is reading has to
            // change with them: the entry has to be re-read to be checked, and
            // the candidates it replaced no longer need reading at all. Sent as
            // a difference so the values already on screen survive it.
            _plugin?.ResyncMemoryFieldWatch();
            RefreshFieldFrame();
        }

        private void MemMapEntryWatch_Click(object sender, RoutedEventArgs e)
        {
            var entry = Store?.Selected?.Entry;
            if (entry == null) return;
            AddWatch(entry.Addr, entry.Kind, entry.Len, entry.Label);
        }

        private void MemMapEntryDemote_Click(object sender, RoutedEventArgs e)
        {
            var store = Store;
            var field = store?.Selected;
            if (field?.Entry == null) return;
            if (!store.Demote(field.Key)) return;
            _plugin?.SaveMemoryFields();
            _plugin?.ResyncMemoryFieldWatch();
            RefreshFieldFrame();
        }

        private void MemMapEntryPath_Click(object sender, RoutedEventArgs e)
        {
            var field = Store?.Selected;
            var entry = field?.Entry;
            if (entry == null || _plugin == null) return;
            if (MemoryRoutes.Of(entry.Addr) != MemoryRoute.Absolute)
            {
                if (MemMapEntryHowText != null)
                    MemMapEntryHowText.Text = "This entry is already anchored on something that resolves itself, "
                                            + "so a pointer path would buy it nothing.";
                return;
            }
            _memPathWait = new MemPathWait
            {
                FieldKey = field.Key,
                Addr = entry.Addr,
                Label = entry.Label,
                ForEntry = true,
                StartedUtc = DateTime.UtcNow,
            };
            if (MemMapEntryHowText != null)
                MemMapEntryHowText.Text = "Ready to look for a pointer path to " + entry.Addr
                                        + ". It is a run of its own, so it stops whatever is running now. "
                                        + "Press Find a pointer path down beside the candidates to start it.";
            if (MemMapFindPathButton != null)
            {
                MemMapFindPathButton.Visibility = Visibility.Visible;
                MemMapFindPathButton.IsEnabled = true;
            }
        }

        // ---- pointer paths from the scanner -----------------------------------

        /// <summary>One path the scanner found. Kept per address, best first,
        /// where "best" is a statement about durability and nothing else: a root
        /// in an image that cannot move beats one in a DLL that does, then fewer
        /// dereferences, then a tighter window. A path found inside a 16 byte
        /// window is far stronger evidence than the same path found inside a
        /// 512 byte one, which is why the window is carried at all.</summary>
        private void NotePathEvent(ScanEvent ev)
        {
            // "target" is the absolute address the chain was found to land on.
            string target = (ev.PathTarget ?? ev.Address ?? "").Trim();
            string text = PathTextOf(ev);
            if (target.Length == 0) return;

            var wait = _memPathWait;
            // Through the shared comparison, never as text. The address this tab
            // asked for is padded to eight hex digits and the one the scanner
            // names back is not, so on a 32-bit heap the two strings never match
            // and the promotion this reply exists to finish would hang until the
            // patience ran out and then offer the escape hatch.
            bool mine = wait != null && MemoryRoutes.SameAddress(wait.Addr, target);

            if (text == null)
            {
                // A spec of nothing is the scanner saying there IS no path, which
                // is a real answer and not a failure: the value is reached some
                // other way. Reported as the answer it is rather than left to
                // time out looking like a search that is still running.
                if (mine) { wait.GaveUp = true; ReportNoPath(wait); }
                return;
            }

            string cacheKey = MemoryRoutes.AddressKey(target);
            if (!_memPaths.TryGetValue(cacheKey, out var list))
            {
                list = new List<ScanEvent>();
                _memPaths[cacheKey] = list;
            }
            foreach (var existing in list)
                if (string.Equals(PathTextOf(existing), text, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(ev);
            list.Sort(ComparePaths);

            if (!mine) return;
            wait.Found++;

            string best = PathTextOf(list[0]);
            if (wait.ForEntry) UpgradeEntryWithPath(wait, list[0], best);
            else
            {
                // The first path that arrives finishes the promotion, and a
                // better one arriving later is not chased: an entry that quietly
                // changed its route after the operator watched it being written
                // would be the map moving under them.
                var wasWait = _memPathWait;
                _memPathWait = null;
                FinishPromotion(wasWait.FieldKey, wasWait.Addr, wasWait.Label, best);
                if (MemMapPromoteText != null)
                    MemMapPromoteText.Text += "  " + DescribePath(list[0]);
            }
        }

        /// <summary>The scanner looked and there is no path. Said plainly, with
        /// the one thing that can still be done about it, because a search that
        /// answered "none" and a search still running look identical to anybody
        /// staring at a button.</summary>
        private void ReportNoPath(MemPathWait wait)
        {
            string line = "No pointer path was found to " + wait.Addr
                        + ". Nothing static points anywhere near it inside the window that was searched, "
                        + "which is a real answer: the value is reached some other way.";
            if (wait.ForEntry)
            {
                if (MemMapEntryHowText != null) MemMapEntryHowText.Text = line;
            }
            else
            {
                if (MemMapPromoteText != null)
                    MemMapPromoteText.Text = line
                        + "  You can write the address down as it is, but it is a number good for this launch "
                        + "only and the map will say so.";
                if (MemMapPromoteAnywayButton != null)
                    MemMapPromoteAnywayButton.Visibility = Visibility.Visible;
            }
            if (MemMapFindPathButton != null) MemMapFindPathButton.IsEnabled = true;
        }

        /// <summary>One stored path, resolved and read against the game running
        /// NOW. This is the scanner's own half of the relaunch test, and its
        /// vocabulary is wider than pass or fail on purpose: a chain that did
        /// not resolve, one that resolved onto a page that would not read, and
        /// one that read back something that cannot be the field have three
        /// different fixes.</summary>
        private void NotePathCheck(ScanEvent ev)
        {
            var store = Store;
            if (store == null) return;
            string spec = (ev.PathText ?? "").Trim();
            if (spec.Length == 0) return;
            string outcome = (ev.Outcome ?? "").Trim().ToUpperInvariant();
            foreach (var f in store.Fields)
            {
                var e = f.Entry;
                // Same reason as the path event: a spec whose ROOT is an absolute
                // address carries the padding difference inside its brackets, so
                // the two halves spell the same chain two ways.
                if (e == null || !MemoryRoutes.SameAddress(e.Addr, spec)) continue;
                e.CheckedThisLaunch = true;
                e.ResolveFailed = outcome == "BROKEN";
                switch (outcome)
                {
                    case "OK":
                        e.StaleWhy = null;
                        if (!string.IsNullOrEmpty(ev.PathValue)) e.LastGood = ev.PathValue;
                        if (!e.CountedThisLaunch) { e.Launches++; e.CountedThisLaunch = true; }
                        break;
                    case "RESOLVED":
                        // It resolved, and it was stored with no kind to read it
                        // by. Neither live nor stale: unproved, and saying so is
                        // the point of having more than two words.
                        e.StaleWhy = null;
                        e.CheckedThisLaunch = false;
                        break;
                    case "BROKEN":
                        e.StaleWhy = "the chain did not resolve: "
                                   + (string.IsNullOrEmpty(ev.Why) ? "a link in it is not there any more" : ev.Why);
                        break;
                    case "UNREADABLE":
                        e.StaleWhy = "it resolves but the page would not read: "
                                   + (string.IsNullOrEmpty(ev.Why) ? "no reason given" : ev.Why);
                        break;
                    case "IMPLAUSIBLE":
                        e.StaleWhy = "it resolves and reads, but "
                                   + (string.IsNullOrEmpty(ev.PathValue) ? "what came back" : ev.PathValue)
                                   + " cannot be this field: "
                                   + (string.IsNullOrEmpty(ev.Why) ? "it is outside its range" : ev.Why);
                        break;
                    default:
                        return;
                }
                RefreshFieldFrame();
                return;
            }
        }

        /// <summary>The scanner's own measurement of the round this tab just
        /// ran. Shown as corroboration, and CHECKED: the two halves measure the
        /// same refreshes, so disagreeing means one of them is wrong, and that
        /// is worth saying out loud rather than quietly preferring whichever
        /// answer happens to be on screen.</summary>
        private void NoteScannerRound(ScanEvent ev)
        {
            if (MemMapEliminateText == null) return;
            int changed = ev.ChangedCount > 0 ? ev.ChangedCount : (ev.Changed?.Count ?? 0);
            int unchanged = ev.UnchangedCount > 0 ? ev.UnchangedCount : (ev.Unchanged?.Count ?? 0);
            string line = "The scanner measured the same round over every refresh: "
                        + changed.ToString(CultureInfo.InvariantCulture) + " moved, "
                        + unchanged.ToString(CultureInfo.InvariantCulture) + " did not.";
            if (_memRoundChangedHere >= 0 && _memRoundChangedHere != changed)
                line += "  This tab counted " + _memRoundChangedHere.ToString(CultureInfo.InvariantCulture)
                      + " as having moved, which does not agree. One of the two is wrong, so treat this round "
                      + "as unproved and run it again rather than trusting either number.";
            MemMapEliminateText.Text += "  " + line;
        }

        /// <summary>What THIS half judged as having moved in the last round, so
        /// the scanner's own count can be checked against it. Negative until a
        /// round has been finished.</summary>
        private int _memRoundChangedHere = -1;

        private void UpgradeEntryWithPath(MemPathWait wait, ScanEvent path, string text)
        {
            var store = Store;
            var field = store?.Find(wait.FieldKey);
            var entry = field?.Entry;
            if (entry == null) { _memPathWait = null; return; }
            string norm = MemoryWatchList.NormalizeAddress(text, out string problem);
            if (norm == null)
            {
                if (MemMapEntryHowText != null)
                    MemMapEntryHowText.Text = "The scanner sent a path this cannot read (" + problem + "): " + text;
                _memPathWait = null;
                return;
            }
            // The address it was found FROM becomes the fallback, and it is only
            // good for the launch it was found in, which the file records rather
            // than leaves to be assumed.
            entry.Fallback = entry.Addr;
            entry.FallbackVolatile = MemoryRoutes.Of(entry.Addr) == MemoryRoute.Absolute;
            entry.Addr = norm;
            entry.ConfirmedHow = (entry.ConfirmedHow ?? "") + "; anchored later on a pointer path ("
                               + DescribePath(path) + ")";
            // A new route has proved nothing yet. It has to resolve and read
            // plausibly in this launch before it counts as live, exactly like an
            // entry loaded off disk.
            entry.CheckedThisLaunch = false;
            entry.CountedThisLaunch = false;
            entry.StaleWhy = null;
            entry.HasRead = false;
            entry.Shown = null;
            _memPathWait = null;
            _plugin?.SaveMemoryFields();
            _plugin?.ResyncMemoryFieldWatch();
            RefreshFieldFrame();
            if (MemMapEntryHowText != null)
                MemMapEntryHowText.Text = "Anchored on a pointer path: " + norm + ". " + DescribePath(path);
        }

        /// <summary>The path as one string, from whichever half of the event the
        /// scanner filled in. Composed from the root and the offsets when it
        /// sent those instead, so a scanner that reports the parts and one that
        /// reports the text both work.</summary>
        private static string PathTextOf(ScanEvent ev) => ScanPathOrder.TextOf(ev);

        /// <summary>Best first. The ordering itself lives in ScanPathOrder,
        /// beside the event it reads, because that file is the one the wire gate
        /// compiles: an ordering that only this tab can run can only be checked
        /// by reading it, and reading it is how it came to disagree with the
        /// scanner in the first place.</summary>
        private static int ComparePaths(ScanEvent a, ScanEvent b) => ScanPathOrder.Compare(a, b);

        private static string DescribePath(ScanEvent ev)
        {
            var parts = new List<string>();
            int depth = ev.PathDepth > 0 ? ev.PathDepth : (ev.PathOffsets?.Count ?? 0);
            if (depth > 0) parts.Add(depth.ToString(CultureInfo.InvariantCulture) + " dereference(s)");
            if (!string.IsNullOrWhiteSpace(ev.PathModule)) parts.Add("rooted in " + ev.PathModule.Trim());
            // The scanner MEASURES this from the module's PE header rather than
            // assuming it, and on this target it is the whole argument: the
            // game's own executable has its relocations stripped and no opt-in
            // to ASLR, so Windows cannot move it.
            parts.Add(ev.PathStatic
                ? "the root is in an image Windows cannot move, so it is the same address every launch"
                : "the root is in an image that does move, so it is resolved again on every read");
            if (string.Equals(ev.PathRootKind, "main-exe", StringComparison.OrdinalIgnoreCase))
                parts.Add("and it is the game's own executable, which is the strongest root there is");
            if (ev.PathWindow > 0)
                parts.Add("found inside a " + ev.PathWindow.ToString(CultureInfo.InvariantCulture)
                        + " byte window, and a tighter window is stronger evidence than a wide one");
            return string.Join(", ", parts) + ".";
        }

        private string BestPathFor(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return null;
            if (!_memPaths.TryGetValue(MemoryRoutes.AddressKey(addr), out var list) || list.Count == 0) return null;
            return PathTextOf(list[0]);
        }

        /// <summary>A path search that has been running long enough to have
        /// answered. Said out loud rather than left as a spinner: "no path
        /// exists" is a real answer and the operator has to be able to act on
        /// it.</summary>
        private void CheckPathWaitTimeout()
        {
            var wait = _memPathWait;
            if (wait == null || wait.GaveUp || wait.Found > 0) return;
            // Only while a paths run is actually in flight. A wait that is armed
            // and has not been STARTED yet is the ordinary state between pressing
            // This is it and pressing Find a pointer path, and timing that out
            // would tell the operator a search had failed before they had asked
            // for one.
            if (_memMapRunKind != MemMapRunKind.Paths) return;
            bool running = _plugin?.MemoryScanRunning == true;
            // A scanner that has gone is a definite answer, not a slow one:
            // waiting out the patience for a process that no longer exists would
            // leave a promotion hanging on a reply nobody can send.
            if (running && DateTime.UtcNow - wait.StartedUtc < MemPathPatience) return;
            wait.GaveUp = true;
            if (MemMapFindPathButton != null) MemMapFindPathButton.IsEnabled = true;
            string line = running
                ? "No pointer path has come back for " + wait.Addr + " yet. Either nothing static points "
                + "anywhere near it, or the walk is still running. "
                : "The paths run ended without naming a path to " + wait.Addr + ". ";
            if (wait.ForEntry)
            {
                if (MemMapEntryHowText != null) MemMapEntryHowText.Text = line;
            }
            else
            {
                if (MemMapPromoteText != null)
                    MemMapPromoteText.Text = line
                        + "You can write the address down as it is, but it is a number good for this launch only "
                        + "and the map will say so.";
                if (MemMapPromoteAnywayButton != null)
                    MemMapPromoteAnywayButton.Visibility = Visibility.Visible;
            }
        }

        // ---- findings become candidates ---------------------------------------

        /// <summary>One finding, added to the field the tab is pointed at. This
        /// is what turns a run's output from a wall of rows into work on one
        /// field, and it is why a run says which field it is feeding before it
        /// starts rather than after.</summary>
        private void NoteFindingAsCandidate(ScanEvent ev)
        {
            var store = Store;
            var field = store?.Selected;
            if (field == null || string.IsNullOrWhiteSpace(ev.Address)) return;

            MemoryWatchList.SpecForFinding(ev.Address, ev.ModuleAddr, ev.Type, ev.WatchKind,
                                           ev.WatchLen, ev.Label, KnownCarName(),
                                           out string addr, out string kind, out int len, out _);
            // SpecForFinding hands back the durable form when there is one, which
            // is what a watch row wants. A candidate keeps BOTH: the absolute
            // address is what a pointer path is searched from, and the module
            // form is what makes it promotable without one.
            string note = string.Join("  ", TrimAll(ev.Type, ev.Label, ev.Evidence));
            string problem = store.AddCandidate(field.Key, ev.Address, kind, len, ev.ModuleAddr, note,
                                                out var candidate);
            // Straight onto the wire, while the run that found it still exists.
            // A search emits its findings and then exits, so a candidate armed
            // after the run has ended is armed at a process that is going away,
            // and ruling anything out would need a second scanner started by
            // hand.
            if (problem == null && candidate != null && !_plugin.ArmOneMemoryFieldCandidate(candidate))
            {
                _memCandUnarmed++;
                _memCandUnarmedWhy = "the scanner's watch list holds "
                    + MemoryFieldStore.ScannerWatchRows.ToString(CultureInfo.InvariantCulture)
                    + " rows at once and "
                    + (_plugin?.MemoryWatch.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                    + " of them are the watch list's own, so these are not being read and cannot be "
                    + "ruled out either way. Clear some watch rows, or eliminate a round and try again.";
            }
            if (problem != null)
            {
                // Counted rather than swallowed. A search that overflowed the
                // field is exactly the case where an answer can go missing, and
                // a silent drop is indistinguishable from a search that simply
                // did not find it.
                _memCandRefused++;
                _memCandRefusedWhy = problem;
            }
            // Deliberately not redrawing per finding: a name search emits a
            // hundred and fifty of these in a burst, and a rebuild each time
            // would lock the UI thread for the whole run.
        }

        private static IEnumerable<string> TrimAll(params string[] parts)
        {
            foreach (string p in parts)
                if (!string.IsNullOrWhiteSpace(p)) yield return p.Trim();
        }

        /// <summary>Findings have stopped arriving: draw them, arm them and say
        /// where they went. Called once when a run ends rather than per finding,
        /// for the reason above.</summary>
        private void SettleFindingsIntoField()
        {
            var field = Store?.Selected;
            if (field == null) return;
            _plugin?.SaveMemoryFields();
            // Deliberately NOT re-armed here. Each candidate was put on the wire
            // as it arrived, while the run that found it was still alive, and a
            // re-arm at this point would clear a scanner that is on its way out
            // and blank every value the run left on screen.
            RefreshFieldFrame();
        }

        /// <summary>Which field this run's findings are landing on, said before
        /// anybody drives. A run that fed the wrong field is a session
        /// wasted.</summary>
        private void ShowFindingsDestination()
        {
            if (MemMapFindingsToFieldText == null) return;
            var field = Store?.Selected;
            // Only for a run that actually looked for something. A watch host
            // and a paths run find nothing by design, and telling the operator
            // where their findings went after one of those would be describing
            // work that never happened.
            if (field == null || _memFindingCount == 0) { MemMapFindingsToFieldText.Text = ""; return; }
            int n = field.Candidates?.Count ?? 0;
            string line =
                "These went to " + (field.Name ?? field.Key) + " as candidates. That field now holds "
              + n.ToString(CultureInfo.InvariantCulture) + ". Work on it up in the map: declare what you are "
              + "about to do, do it, and everything that did not respond is ruled out in one go.";
            if (_memCandRefused > 0)
                line += "  " + _memCandRefused.ToString(CultureInfo.InvariantCulture)
                      + " of them were NOT taken: " + (_memCandRefusedWhy ?? "no reason given")
                      + " The answer could be among the ones that were dropped, so narrow the search rather "
                      + "than working through what is here.";
            if (_memCandUnarmed > 0)
                line += "  " + _memCandUnarmed.ToString(CultureInfo.InvariantCulture)
                      + " of them are on the list but are NOT being read: " + (_memCandUnarmedWhy ?? "")
                      + " A round cannot rule those in or out, and it will say so rather than dropping them.";
            MemMapFindingsToFieldText.Text = line;
        }

        /// <summary>Point the run at the field its declared values are about, so
        /// a name search does not dump its findings onto whatever happened to be
        /// selected. Only moves the selection when the run is unambiguous about
        /// what it is looking for.</summary>
        private void AimRunAtField(MemMapRunKind kind)
        {
            var store = Store;
            if (store == null) return;
            string want = null;
            if (kind == MemMapRunKind.FindString) want = MemoryFields.CarName;
            else if (kind == MemMapRunKind.Hunt && GearSearchMode() != MemGearMode.Off
                     && string.IsNullOrWhiteSpace(KnownCarName())) want = MemoryFields.Gear;
            if (want == null) return;
            var field = store.Find(want);
            if (field == null || field.Entry != null) return;   // already confirmed: leave it alone
            SelectField(want);
        }

        /// <summary>A new run: the tally of findings this one could not take is
        /// its own, not the last three runs'.</summary>
        private void ResetCandidateIntake()
        {
            _memCandRefused = 0;
            _memCandRefusedWhy = null;
            _memCandUnarmed = 0;
            _memCandUnarmedWhy = null;
            // A refusal belongs to the scanner that made it. Carried across runs
            // it would report a full watch list on a run that had two rows on it.
            _memWatchRefused = 0;
        }
    }
}
