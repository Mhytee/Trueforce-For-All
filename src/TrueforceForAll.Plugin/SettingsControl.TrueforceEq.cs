// The Trueforce EQ section on the Effects tab: the curve editor
// (EqCurveEditor, hosted in TrueforceEqCurveHost) plus one code-built row
// per band (on / type / Hz / dB / Q / slope / remove), a multi-level Undo,
// the EQ's own preset list in the header (built-ins, then the user's saved
// curves, with Save… and delete), and the live spectrum feed (a Goertzel
// bank over the plugin's pre/post EQ taps on a UI timer that only runs
// while the expander is open). Global like master gain: every edit applies
// live through TrueforcePlugin.ApplyTrueforceEq and persists on the shared
// debounce. EQ presets are their own list, never part of a game preset.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        private const string TrueforceEqEffectId = "TrueforceEq";

        private sealed class EqRow
        {
            public int Index;
            public Grid Root;
            public CheckBox On;
            public ComboBox Type;
            public TextBox Hz, Db, Q;
            public ComboBox Slope;
            public Button Remove;
        }

        private EqCurveEditor _eqEditor;
        private readonly List<EqRow> _eqRows = new List<EqRow>();
        private bool _eqRowsSuppress;

        private static readonly Brush EqRowSelectedBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xE5, 0xC0, 0x4A));
        private static string[] EqTypeLabels =>
            new[]{ Loc.T("Eq_Bell"), Loc.T("Eq_LowShelf"), Loc.T("Eq_HighShelf"), Loc.T("Eq_LowCut"), Loc.T("Eq_HighCut") };
        private static readonly string[] EqSlopeLabels = { "12 dB", "24 dB", "36 dB", "48 dB" };

        // Undo: whole-list snapshots plus the preset they belonged to, newest
        // last. Gestures on the same band within EqUndoCoalesceMs share one
        // entry (a drag, a run of arrow presses, a scroll burst), so Undo
        // steps back by intent, not by tick.
        private sealed class EqUndoEntry
        {
            public List<EqBand> Bands;
            public string PresetId;
        }
        private readonly List<EqUndoEntry> _eqUndo = new List<EqUndoEntry>();
        private const int EqUndoDepth = 40;
        private const double EqUndoCoalesceMs = 700;
        private string _eqUndoLastKey;
        private long _eqUndoLastStamp;

        // Live spectrum.
        private const int EqSpecWindow = 800;      // 200 ms at 4 kHz
        private const int EqSpecBins = 32;
        private const double EqSpecLoHz = 12.0, EqSpecHiHz = 1700.0;
        private const float EqSpecReleaseDbPerTick = 2.5f;
        private DispatcherTimer _eqSpecTimer;
        private float[] _eqSpecRaw, _eqSpecWin;
        private double[] _eqSpecHann, _eqSpecHz;
        private float[] _eqSpecPost, _eqSpecPre;
        private long _eqSpecLastWritten = -1;
        private bool _eqSpecHooked;

        // Preset list. The combo's items are rebuilt from state each time it
        // changes (preset picked, saved, deleted, or the curve moving on or
        // off its preset); _eqPresetSuppress guards the rebuild itself, and a
        // click on an item is applied when the dropdown closes, never while
        // WPF is still processing that item.
        private const string EqPresetCustomTag = "custom";
        private const int EqPresetNameMax = 40;
        private bool _eqPresetSuppress;
        private string _eqPresetClickId;
        private string _eqPresetShownKey;

        private List<EqBand> EqBands => _plugin?.Settings?.TrueforceEqBands;

        private void EnsureEqEditor()
        {
            if (_eqEditor != null || TrueforceEqCurveHost == null) return;
            _eqEditor = new EqCurveEditor();
            _eqEditor.EditStarted   += i => PushEqUndo("edit" + i);
            _eqEditor.BandChanged   += i => OnEqBandEdited(i, live: true);
            _eqEditor.BandCommitted += i => OnEqBandEdited(i, live: false);
            _eqEditor.UndoRequested += EqUndo;
            _eqEditor.BandAddRequested += b =>
            {
                var bands = EqBands;
                if (bands == null || bands.Count >= ParametricEq.MaxBands) return;
                PushEqUndo("add");
                bands.Add(b);
                EqStructureChanged(bands.Count - 1);
            };
            _eqEditor.BandRemoveRequested += i =>
            {
                var bands = EqBands;
                if (bands == null || i < 0 || i >= bands.Count) return;
                PushEqUndo("remove");
                bands.RemoveAt(i);
                EqStructureChanged(-1);
            };
            _eqEditor.SelectionChanged += () => HighlightEqRow(_eqEditor.SelectedIndex);
            TrueforceEqCurveHost.Child = _eqEditor;
            if (TrueforceEqDeletePresetButton != null) ModalButtonTheme.Destructive(TrueforceEqDeletePresetButton);

            if (!_eqSpecHooked && TrueforceEqExpander != null)
            {
                _eqSpecHooked = true;
                TrueforceEqExpander.Expanded  += (s, e) => StartEqSpectrum();
                TrueforceEqExpander.Collapsed += (s, e) => StopEqSpectrum();
                Unloaded += (s, e) => StopEqSpectrum();
                Loaded   += (s, e) => { if (TrueforceEqExpander.IsExpanded) StartEqSpectrum(); };
            }
        }

        /// <summary>Called from RefreshFromPlugin (inside _suppressEvents).</summary>
        private void RefreshTrueforceEq()
        {
            var s = _plugin?.Settings;
            if (TrueforceEqEnabledCheck == null || s == null) return;
            EnsureEqEditor();
            if (s.TrueforceEqBands == null) s.TrueforceEqBands = new List<EqBand>();
            TrueforceEqEnabledCheck.IsChecked = s.TrueforceEqEnabled;
            if (_eqEditor != null)
            {
                _eqEditor.Bands = s.TrueforceEqBands;
                _eqEditor.EqActive = s.TrueforceEqEnabled;
            }
            RebuildEqRows();
            _eqPresetShownKey = null;   // a refresh may follow a language change
            RefreshEqPresetCombo();
            UpdateEqUndoButton();
            if (TrueforceEqExpander != null && TrueforceEqExpander.IsExpanded && IsLoaded) StartEqSpectrum();
        }

        /// <summary>A band's values changed (editor gesture or a row edit).
        /// Live = mid-drag: apply and redraw, persist on the debounce.</summary>
        private void OnEqBandEdited(int index, bool live)
        {
            _plugin?.ApplyTrueforceEq();
            SyncEqRow(index);
            _eqEditor?.InvalidateVisual();
            RefreshEqPresetCombo();
            SchedulePersistDebounced();
            if (!live) MarkEqSeen();
        }

        /// <summary>Bands were added, removed or replaced: rebuild the rows.</summary>
        private void EqStructureChanged(int select)
        {
            _plugin?.ApplyTrueforceEq();
            if (_eqEditor != null)
            {
                _eqEditor.Bands = EqBands;
                _eqEditor.SelectedIndex = select;
            }
            RebuildEqRows();
            RefreshEqPresetCombo();
            UpdateEqUndoButton();
            SchedulePersistDebounced();
            MarkEqSeen();
        }

        private void MarkEqSeen()
        {
            if (_plugin == null || !_plugin.IsEffectUnseen(TrueforceEqEffectId)) return;
            _plugin.MarkEffectSeen(TrueforceEqEffectId);
            RefreshNewBadges();
        }

        private void TrueforceEqEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            bool on = TrueforceEqEnabledCheck.IsChecked == true;
            _plugin.Settings.TrueforceEqEnabled = on;
            _plugin.ApplyTrueforceEq();
            if (_eqEditor != null) _eqEditor.EqActive = on;
            SchedulePersistDebounced();
            MarkEqSeen();
        }

        private void TrueforceEqAddBand_Click(object sender, RoutedEventArgs e)
        {
            var bands = EqBands;
            if (bands == null || bands.Count >= ParametricEq.MaxBands) return;
            PushEqUndo("add");
            bands.Add(new EqBand { FrequencyHz = 100, GainDb = 0, Q = ParametricEq.DefaultQ, Type = EqBandType.Peak });
            EqStructureChanged(bands.Count - 1);
        }

        private void TrueforceEqUndo_Click(object sender, RoutedEventArgs e) => EqUndo();

        // ---------- presets ----------

        private static readonly string[] EqBuiltinIds =
            { TrueforcePlugin.EqPresetIdFlat, TrueforcePlugin.EqPresetIdCleanBass };

        /// <summary>A built-in's display name, or null for an unknown id.
        /// Literal keys so the language checks see them.</summary>
        private static string EqBuiltinName(string id)
        {
            switch (id)
            {
                case TrueforcePlugin.EqPresetIdFlat:      return Loc.T("Eq_PresetFlat");
                case TrueforcePlugin.EqPresetIdCleanBass: return Loc.T("Eq_PresetCleanBass");
                default:                                  return null;
            }
        }

        private static List<EqBand> EqBuiltinBands(string id)
        {
            switch (id)
            {
                case TrueforcePlugin.EqPresetIdFlat:      return EqCurves.Flat();
                case TrueforcePlugin.EqPresetIdCleanBass: return EqCurves.CleanBass();
                default:                                  return null;
            }
        }

        private List<EqPreset> EqUserPresets
        {
            get
            {
                var s = _plugin?.Settings;
                if (s == null) return null;
                if (s.TrueforceEqUserPresets == null) s.TrueforceEqUserPresets = new List<EqPreset>();
                return s.TrueforceEqUserPresets;
            }
        }

        private EqPreset FindEqUserPreset(string name)
        {
            var list = EqUserPresets;
            if (list == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (var p in list)
                if (p != null && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private static bool IsEqUserId(string id)
            => id != null && id.StartsWith(TrueforcePlugin.EqPresetUserPrefix, StringComparison.Ordinal);

        private static string EqUserNameOf(string id)
            => IsEqUserId(id) ? id.Substring(TrueforcePlugin.EqPresetUserPrefix.Length) : null;

        /// <summary>The bands a preset id stands for (a fresh copy for a
        /// built-in, the stored list for a user preset), or null.</summary>
        private List<EqBand> EqPresetBands(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (IsEqUserId(id)) return FindEqUserPreset(EqUserNameOf(id))?.Bands;
            return EqBuiltinBands(id);
        }

        private string EqPresetDisplayName(string id)
        {
            if (IsEqUserId(id)) return FindEqUserPreset(EqUserNameOf(id))?.Name ?? EqUserNameOf(id);
            return EqBuiltinName(id) ?? id;
        }

        /// <summary>The preset the curve belongs to and whether it has been
        /// edited since. The tracked id wins while it still exists; an
        /// untracked curve (older settings, a deleted preset) is matched
        /// against every preset. Null id = Custom.</summary>
        private void ResolveEqPreset(out string id, out bool edited)
        {
            id = null;
            edited = false;
            var bands = EqBands;
            if (bands == null) return;
            string tracked = _plugin?.Settings?.TrueforceEqPresetId;
            var trackedBands = EqPresetBands(tracked);
            if (trackedBands != null)
            {
                id = tracked;
                edited = !ParametricEq.BandsEqual(trackedBands, bands);
                return;
            }
            foreach (var b in EqBuiltinIds)
                if (ParametricEq.BandsEqual(EqBuiltinBands(b), bands)) { id = b; return; }
            var users = EqUserPresets;
            if (users != null)
                foreach (var p in users)
                    if (p != null && ParametricEq.BandsEqual(p.Bands, bands)) { id = TrueforcePlugin.EqPresetUserPrefix + p.Name; return; }
        }

        /// <summary>Rebuild the preset list when what it shows has changed.
        /// Cheap enough to call on every drag tick: it only touches the
        /// control when the selected preset, its edited mark, or the list of
        /// saved presets moves.</summary>
        private void RefreshEqPresetCombo()
        {
            if (TrueforceEqPresetCombo == null || _plugin?.Settings == null) return;
            if (TrueforceEqPresetCombo.IsDropDownOpen) return;
            ResolveEqPreset(out string id, out bool edited);

            var users = new List<EqPreset>();
            if (EqUserPresets != null)
                foreach (var p in EqUserPresets)
                    if (p != null && !string.IsNullOrWhiteSpace(p.Name)) users.Add(p);
            users.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            var key = new System.Text.StringBuilder();
            key.Append(id ?? EqPresetCustomTag).Append('|').Append(edited ? 1 : 0).Append('|').Append(Loc.T("Eq_PresetFlat"));
            foreach (var p in users) key.Append('|').Append(p.Name);
            string shown = key.ToString();

            bool isUser = IsEqUserId(id);
            if (TrueforceEqSaveButton != null)
                TrueforceEqSaveButton.Visibility = id == null || edited ? Visibility.Visible : Visibility.Collapsed;
            if (TrueforceEqDeletePresetButton != null)
                TrueforceEqDeletePresetButton.Visibility = isUser ? Visibility.Visible : Visibility.Collapsed;

            if (shown == _eqPresetShownKey) return;
            _eqPresetShownKey = shown;

            _eqPresetSuppress = true;
            try
            {
                var combo = TrueforceEqPresetCombo;
                combo.Items.Clear();
                ComboBoxItem selected = null;

                if (id == null)
                {
                    selected = NewEqPresetItem(EqPresetCustomTag, Loc.T("Eq_Custom"));
                    selected.IsEnabled = false;
                    combo.Items.Add(selected);
                }
                foreach (var b in EqBuiltinIds)
                {
                    bool mine = b == id;
                    var item = NewEqPresetItem(b, mine && edited ? Loc.F("Eq_PresetEdited_Fmt", EqBuiltinName(b)) : EqBuiltinName(b));
                    combo.Items.Add(item);
                    if (mine) selected = item;
                }
                if (users.Count > 0) combo.Items.Add(new Separator());
                foreach (var p in users)
                {
                    string pid = TrueforcePlugin.EqPresetUserPrefix + p.Name;
                    bool mine = isUser && string.Equals(EqUserNameOf(id), p.Name, StringComparison.OrdinalIgnoreCase);
                    var item = NewEqPresetItem(pid, mine && edited ? Loc.F("Eq_PresetEdited_Fmt", p.Name) : p.Name);
                    combo.Items.Add(item);
                    if (mine) selected = item;
                }
                combo.SelectedItem = selected;
            }
            finally { _eqPresetSuppress = false; }
        }

        private ComboBoxItem NewEqPresetItem(string tag, string text)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            // Remember which item was clicked; applied once the dropdown has
            // closed. Picking the preset already shown still counts: that is
            // how an edited preset goes back to its saved curve.
            item.PreviewMouseLeftButtonUp += (s, e) => _eqPresetClickId = tag;
            return item;
        }

        private void TrueforceEqPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Mouse picks are handled in DropDownClosed. This is the keyboard
            // on a closed list (arrow keys step through the presets).
            if (_eqPresetSuppress || TrueforceEqPresetCombo.IsDropDownOpen) return;
            string id = (TrueforceEqPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            RequestEqPreset(id, clicked: false);
        }

        private void TrueforceEqPresetCombo_DropDownClosed(object sender, EventArgs e)
        {
            bool clicked = _eqPresetClickId != null;
            string id = _eqPresetClickId ?? (TrueforceEqPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            _eqPresetClickId = null;
            RequestEqPreset(id, clicked);
        }

        private void RequestEqPreset(string id, bool clicked)
        {
            ResolveEqPreset(out string current, out bool edited);
            bool apply = !string.IsNullOrEmpty(id) && id != EqPresetCustomTag
                         && (id != current || (clicked && edited));
            // Deferred either way: the combo is still inside its own event.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (apply) ApplyEqPreset(id);
                else { _eqPresetShownKey = null; RefreshEqPresetCombo(); }
            }), DispatcherPriority.Background);
        }

        private void ApplyEqPreset(string id)
        {
            var bands = EqBands;
            var src = EqPresetBands(id);
            if (bands == null || src == null) return;
            PushEqUndo("preset");
            bands.Clear();
            bands.AddRange(ParametricEq.CloneBands(src));
            _plugin.Settings.TrueforceEqPresetId = id;
            _eqPresetShownKey = null;
            EqStructureChanged(-1);
        }

        private void TrueforceEqSave_Click(object sender, RoutedEventArgs e)
        {
            var bands = EqBands;
            var users = EqUserPresets;
            if (bands == null || users == null) return;
            ResolveEqPreset(out string current, out _);
            string suggested = IsEqUserId(current) ? EqPresetDisplayName(current) : "";

            string name = PromptForName(Loc.T("Eq_SavePresetTitle"), Loc.T("Common_PresetName"), suggested);
            name = (name ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Length > EqPresetNameMax) name = name.Substring(0, EqPresetNameMax).TrimEnd();

            foreach (var b in EqBuiltinIds)
            {
                if (!string.Equals(EqBuiltinName(b), name, StringComparison.CurrentCultureIgnoreCase)) continue;
                TrueforceDialog.Show(Window.GetWindow(this), Loc.T("Eq_SavePresetTitle"),
                    Loc.F("Eq_NameTakenByBuiltin_Fmt", name), DialogKind.Warning);
                return;
            }

            var existing = FindEqUserPreset(name);
            bool updatingCurrent = existing != null && IsEqUserId(current)
                && string.Equals(EqUserNameOf(current), existing.Name, StringComparison.OrdinalIgnoreCase);
            if (existing != null && !updatingCurrent)
            {
                bool? ok = TrueforceDialog.Show(Window.GetWindow(this), Loc.T("Eq_ReplacePresetTitle"),
                    Loc.F("Eq_ReplacePreset_Fmt", existing.Name), DialogKind.Confirm,
                    okLabel: Loc.T("Eq_Replace"), cancelLabel: Loc.T("Common_Cancel"));
                if (ok != true) return;
            }

            if (existing == null)
            {
                existing = new EqPreset();
                users.Add(existing);
            }
            existing.Name = name;
            existing.Bands = ParametricEq.CloneBands(bands);
            _plugin.Settings.TrueforceEqPresetId = TrueforcePlugin.EqPresetUserPrefix + name;
            _eqPresetShownKey = null;
            RefreshEqPresetCombo();
            SchedulePersistDebounced();
            MarkEqSeen();
        }

        private void TrueforceEqDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            ResolveEqPreset(out string current, out _);
            if (!IsEqUserId(current)) return;
            var preset = FindEqUserPreset(EqUserNameOf(current));
            if (preset == null) return;
            bool? ok = TrueforceDialog.Show(Window.GetWindow(this), Loc.T("Eq_DeletePresetTitle"),
                Loc.F("Eq_DeletePreset_Fmt", preset.Name), DialogKind.Destructive,
                okLabel: Loc.T("Common_Delete"), cancelLabel: Loc.T("Common_Cancel"));
            if (ok != true) return;
            EqUserPresets?.Remove(preset);
            // The curve stays on the graph; it just no longer has a name.
            _plugin.Settings.TrueforceEqPresetId = "";
            _eqPresetShownKey = null;
            RefreshEqPresetCombo();
            SchedulePersistDebounced();
        }

        // ---------- undo ----------

        private void PushEqUndo(string key)
        {
            var bands = EqBands;
            if (bands == null) return;
            long now = Stopwatch.GetTimestamp();
            double sinceMs = (now - _eqUndoLastStamp) * 1000.0 / Stopwatch.Frequency;
            if (key == _eqUndoLastKey && _eqUndo.Count > 0 && sinceMs < EqUndoCoalesceMs)
            {
                _eqUndoLastStamp = now;   // keep the burst open
                return;
            }
            _eqUndo.Add(new EqUndoEntry
            {
                Bands = ParametricEq.CloneBands(bands),
                PresetId = _plugin?.Settings?.TrueforceEqPresetId ?? "",
            });
            if (_eqUndo.Count > EqUndoDepth) _eqUndo.RemoveAt(0);
            _eqUndoLastKey = key;
            _eqUndoLastStamp = now;
            UpdateEqUndoButton();
        }

        private void EqUndo()
        {
            var bands = EqBands;
            if (bands == null || _eqUndo.Count == 0) return;
            var snap = _eqUndo[_eqUndo.Count - 1];
            _eqUndo.RemoveAt(_eqUndo.Count - 1);
            _eqUndoLastKey = null;
            bands.Clear();
            bands.AddRange(snap.Bands);
            if (_plugin?.Settings != null) _plugin.Settings.TrueforceEqPresetId = snap.PresetId ?? "";
            _eqPresetShownKey = null;
            EqStructureChanged(-1);
        }

        // Same behaviour as the effects' revert buttons: not shown until there
        // is something to step back to, then the grey arrow appears in the
        // header. Hidden rather than Collapsed so the preset list keeps its place.
        private void UpdateEqUndoButton()
        {
            if (TrueforceEqUndoButton != null)
                TrueforceEqUndoButton.Visibility = _eqUndo.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        // ---------- live spectrum ----------

        private void StartEqSpectrum()
        {
            if (_eqEditor == null || _plugin == null) return;
            if (_eqSpecTimer == null)
            {
                _eqSpecRaw  = new float[EqSpecWindow];
                _eqSpecWin  = new float[EqSpecWindow];
                _eqSpecHann = new double[EqSpecWindow];
                for (int i = 0; i < EqSpecWindow; i++)
                    _eqSpecHann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (EqSpecWindow - 1));
                _eqSpecHz = new double[EqSpecBins];
                for (int k = 0; k < EqSpecBins; k++)
                    _eqSpecHz[k] = EqSpecLoHz * Math.Pow(EqSpecHiHz / EqSpecLoHz, k / (double)(EqSpecBins - 1));
                _eqSpecPost = new float[EqSpecBins];
                _eqSpecPre  = new float[EqSpecBins];
                for (int k = 0; k < EqSpecBins; k++) { _eqSpecPost[k] = (float)EqCurveEditor.SpecMinDb; _eqSpecPre[k] = (float)EqCurveEditor.SpecMinDb; }
                _eqSpecTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
                _eqSpecTimer.Tick += (s, e) => EqSpectrumTick();
                _eqEditor.SpectrumHz = _eqSpecHz;
                _eqEditor.SpectrumPostDb = _eqSpecPost;
                _eqEditor.SpectrumPreDb = _eqSpecPre;
            }
            if (!_eqSpecTimer.IsEnabled) _eqSpecTimer.Start();
        }

        private void StopEqSpectrum()
        {
            _eqSpecTimer?.Stop();
        }

        private void EqSpectrumTick()
        {
            var post = _plugin?.EqSpectrumPost;
            var pre  = _plugin?.EqSpectrumPre;
            if (post == null || pre == null || _eqEditor == null) return;

            long written = post.Written;
            bool fresh = written != _eqSpecLastWritten;
            _eqSpecLastWritten = written;

            bool any = false;
            if (fresh)
            {
                any |= EqAnalyze(post, _eqSpecPost);
                any |= EqAnalyze(pre, _eqSpecPre);
            }
            else
            {
                // Stream idle (no game, no test): let the bars fall away.
                any |= EqDecay(_eqSpecPost);
                any |= EqDecay(_eqSpecPre);
            }
            if (any || fresh) _eqEditor.InvalidateVisual();
        }

        private bool EqAnalyze(SpectrumTap tap, float[] dst)
        {
            int n = tap.CopyLatest(_eqSpecRaw, EqSpecWindow);
            if (n < EqSpecWindow) return EqDecay(dst);
            for (int i = 0; i < n; i++) _eqSpecWin[i] = (float)(_eqSpecRaw[i] * _eqSpecHann[i]);
            bool any = false;
            for (int k = 0; k < EqSpecBins; k++)
            {
                // Goertzel reads a full-scale sine as 0.5; the Hann window
                // takes 0.375 of the power back out.
                double p = Goertzel.Power(_eqSpecWin, 0, n, _eqSpecHz[k], ParametricEq.DefaultSampleRateHz) / 0.375;
                double db = 10.0 * Math.Log10(Math.Max(p, 1e-12) / 0.5);
                if (db < EqCurveEditor.SpecMinDb) db = EqCurveEditor.SpecMinDb;
                if (db > 6) db = 6;
                float held = dst[k] - EqSpecReleaseDbPerTick;
                float v = (float)db > held ? (float)db : held;
                if (v < (float)EqCurveEditor.SpecMinDb) v = (float)EqCurveEditor.SpecMinDb;
                dst[k] = v;
                if (v > EqCurveEditor.SpecMinDb + 0.5) any = true;
            }
            return any;
        }

        private static bool EqDecay(float[] dst)
        {
            bool any = false;
            for (int k = 0; k < dst.Length; k++)
            {
                if (dst[k] <= (float)EqCurveEditor.SpecMinDb) continue;
                dst[k] -= EqSpecReleaseDbPerTick;
                if (dst[k] < (float)EqCurveEditor.SpecMinDb) dst[k] = (float)EqCurveEditor.SpecMinDb;
                any = true;
            }
            return any;
        }

        // ---------- band rows ----------

        private static readonly GridLength[] EqColumnWidths =
        {
            new GridLength(26), new GridLength(40), new GridLength(104), new GridLength(78),
            new GridLength(78), new GridLength(66), new GridLength(74), new GridLength(30),
        };

        private Grid NewEqGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var w in EqColumnWidths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        private TextBlock EqHeaderLabel(string text, int col)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            var style = TryFindResource("HelpText") as Style;
            if (style != null) t.Style = style;
            Grid.SetColumn(t, col);
            return t;
        }

        /// <summary>Rebuilds the rows when the band count changed; otherwise
        /// just re-syncs their values. RefreshFromPlugin runs from many
        /// sites (car change, sync, library reload), and tearing the rows
        /// down under a user typing in one would drop their edit.</summary>
        private void RebuildEqRows()
        {
            if (TrueforceEqBandRows == null) return;
            var bands = EqBands;
            int count = bands?.Count ?? 0;
            if (count > 0 && _eqRows.Count == count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (bands[i] == null) bands[i] = new EqBand();
                    SyncEqRowValues(_eqRows[i], bands[i]);
                }
                HighlightEqRow(_eqEditor?.SelectedIndex ?? -1);
                return;
            }
            _eqRowsSuppress = true;
            try
            {
                TrueforceEqBandRows.Children.Clear();
                _eqRows.Clear();
                if (bands == null || bands.Count == 0)
                {
                    var empty = new TextBlock
                    {
                        Text = Loc.T("Eq_NoBandsAddOne"),
                        Margin = new Thickness(0, 4, 0, 0),
                    };
                    var style = TryFindResource("HelpText") as Style;
                    if (style != null) empty.Style = style;
                    TrueforceEqBandRows.Children.Add(empty);
                    return;
                }

                var header = NewEqGrid();
                header.Children.Add(EqHeaderLabel("#", 0));
                header.Children.Add(EqHeaderLabel(Loc.T("Eq_On"), 1));
                header.Children.Add(EqHeaderLabel(Loc.T("PresetManager_CustomListType_Header"), 2));
                header.Children.Add(EqHeaderLabel(Loc.T("Eq_FreqHz"), 3));
                header.Children.Add(EqHeaderLabel(Loc.T("Eq_GainDB"), 4));
                header.Children.Add(EqHeaderLabel("Q", 5));
                header.Children.Add(EqHeaderLabel(Loc.T("Eq_Slope"), 6));
                TrueforceEqBandRows.Children.Add(header);

                for (int i = 0; i < bands.Count; i++)
                {
                    if (bands[i] == null) bands[i] = new EqBand();
                    var row = BuildEqRow(i, bands[i]);
                    _eqRows.Add(row);
                    TrueforceEqBandRows.Children.Add(row.Root);
                }
                HighlightEqRow(_eqEditor?.SelectedIndex ?? -1);
            }
            finally { _eqRowsSuppress = false; }
        }

        private EqRow BuildEqRow(int index, EqBand band)
        {
            var row = new EqRow { Index = index, Root = NewEqGrid() };
            row.Root.Background = Brushes.Transparent;
            row.Root.MouseLeftButtonDown += (s, e) => { if (_eqEditor != null) _eqEditor.SelectedIndex = row.Index; };

            var num = new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(num, 0);
            row.Root.Children.Add(num);

            row.On = new CheckBox
            {
                IsChecked = band.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = Loc.T("Eq_TurnsBandOffWithout"),
            };
            row.On.Checked   += (s, e) => EqRowToggle(row);
            row.On.Unchecked += (s, e) => EqRowToggle(row);
            Grid.SetColumn(row.On, 1);
            row.Root.Children.Add(row.On);

            row.Type = new ComboBox { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Height = 24 };
            foreach (var label in EqTypeLabels) row.Type.Items.Add(label);
            int typeIdx = (int)band.Type;
            row.Type.SelectedIndex = typeIdx >= 0 && typeIdx < EqTypeLabels.Length ? typeIdx : 0;
            row.Type.ToolTip = Loc.T("Eq_BellBoostCutAround");
            row.Type.SelectionChanged += (s, e) => EqRowTypeChanged(row);
            Grid.SetColumn(row.Type, 2);
            row.Root.Children.Add(row.Type);

            row.Hz = EqNumberBox(row, 3, Loc.T("Eq_CenterCornerFrequency5"));
            row.Db = EqNumberBox(row, 4, Loc.T("Eq_BoostCut3012"));
            row.Q  = EqNumberBox(row, 5, Loc.T("Eq_WidthHigherQNarrower"));

            row.Slope = new ComboBox { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Height = 24 };
            foreach (var label in EqSlopeLabels) row.Slope.Items.Add(label);
            row.Slope.SelectedIndex = Math.Max(0, Array.IndexOf(ParametricEq.Slopes, ParametricEq.SnapSlope(band.SlopeDbPerOct)));
            row.Slope.ToolTip = Loc.T("Eq_HowSteeplyCutFalls");
            row.Slope.SelectionChanged += (s, e) => EqRowSlopeChanged(row);
            Grid.SetColumn(row.Slope, 6);
            row.Root.Children.Add(row.Slope);

            row.Remove = new Button
            {
                Content = "×",
                Width = 22, Height = 22,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.T("Eq_RemoveBand"),
                Cursor = Cursors.Hand,
            };
            ModalButtonTheme.Destructive(row.Remove);
            row.Remove.Click += (s, e) =>
            {
                var bands = EqBands;
                if (bands == null || row.Index < 0 || row.Index >= bands.Count) return;
                PushEqUndo("remove");
                bands.RemoveAt(row.Index);
                EqStructureChanged(-1);
            };
            Grid.SetColumn(row.Remove, 7);
            row.Root.Children.Add(row.Remove);

            SyncEqRowValues(row, band);
            return row;
        }

        private TextBox EqNumberBox(EqRow row, int col, string tip)
        {
            var box = new TextBox
            {
                Margin = new Thickness(2, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 24,
                ToolTip = tip,
            };
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Return)
                {
                    CommitEqRow(row);
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    var b = EqBandAt(row.Index);
                    if (b != null) SyncEqRowValues(row, b);
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (s, e) => CommitEqRow(row);
            box.GotKeyboardFocus += (s, e) => { if (_eqEditor != null) _eqEditor.SelectedIndex = row.Index; box.SelectAll(); };
            Grid.SetColumn(box, col);
            row.Root.Children.Add(box);
            return box;
        }

        private EqBand EqBandAt(int index)
        {
            var bands = EqBands;
            return bands != null && index >= 0 && index < bands.Count ? bands[index] : null;
        }

        private static bool TryParseNumber(string text, out double value)
        {
            text = (text ?? "").Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
            // "1,5" on an invariant box, "1.5" on a comma locale: try the other separator.
            string swapped = text.Contains(",") ? text.Replace(',', '.') : text.Replace('.', ',');
            if (double.TryParse(swapped, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return double.TryParse(swapped, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private void CommitEqRow(EqRow row)
        {
            if (_eqRowsSuppress) return;
            var b = EqBandAt(row.Index);
            if (b == null) return;
            bool hasHz = TryParseNumber(row.Hz.Text, out double hz) && Math.Abs(hz - b.FrequencyHz) > 1e-9;
            bool hasDb = TryParseNumber(row.Db.Text, out double db) && Math.Abs(db - b.GainDb) > 1e-9;
            bool hasQ  = TryParseNumber(row.Q.Text,  out double q)  && Math.Abs(q - b.Q) > 1e-9;
            if (!hasHz && !hasDb && !hasQ) { SyncEqRowValues(row, b); return; }
            PushEqUndo("row" + row.Index);
            if (hasHz) b.FrequencyHz = hz;
            if (hasDb) b.GainDb = db;
            if (hasQ)  b.Q = q;
            ParametricEq.Clamp(b);
            OnEqBandEdited(row.Index, live: false);
        }

        private void EqRowToggle(EqRow row)
        {
            if (_eqRowsSuppress) return;
            var b = EqBandAt(row.Index);
            if (b == null) return;
            PushEqUndo("toggle" + row.Index);
            b.Enabled = row.On.IsChecked == true;
            OnEqBandEdited(row.Index, live: false);
        }

        private void EqRowTypeChanged(EqRow row)
        {
            if (_eqRowsSuppress) return;
            var b = EqBandAt(row.Index);
            if (b == null) return;
            int idx = row.Type.SelectedIndex;
            if (idx < 0 || idx >= EqTypeLabels.Length) return;
            var newType = (EqBandType)idx;
            if (newType == b.Type) return;
            PushEqUndo("type" + row.Index);
            bool wasBell = b.Type == EqBandType.Peak;
            bool isBell  = newType == EqBandType.Peak;
            // Shelves and cuts want Q 0.707 (Butterworth corner, steepest
            // shelf with no overshoot); a bell Q left on them bumps at the
            // corner. Coming back to a bell, restore the bell default.
            if (!isBell && wasBell) b.Q = ParametricEq.SlopeDefaultQ;
            else if (isBell && !wasBell) b.Q = ParametricEq.DefaultQ;
            b.Type = newType;
            ParametricEq.Clamp(b);
            OnEqBandEdited(row.Index, live: false);
        }

        private void EqRowSlopeChanged(EqRow row)
        {
            if (_eqRowsSuppress) return;
            var b = EqBandAt(row.Index);
            if (b == null) return;
            int idx = row.Slope.SelectedIndex;
            if (idx < 0 || idx >= ParametricEq.Slopes.Length) return;
            int slope = ParametricEq.Slopes[idx];
            if (slope == b.SlopeDbPerOct) return;
            PushEqUndo("slope" + row.Index);
            b.SlopeDbPerOct = slope;
            ParametricEq.Clamp(b);
            OnEqBandEdited(row.Index, live: false);
        }

        /// <summary>Re-format one row from its band (after a drag or a
        /// commit). A box the user is typing in is left alone.</summary>
        private void SyncEqRow(int index)
        {
            if (index < 0 || index >= _eqRows.Count) return;
            var b = EqBandAt(index);
            if (b == null) return;
            SyncEqRowValues(_eqRows[index], b);
        }

        private void SyncEqRowValues(EqRow row, EqBand b)
        {
            bool was = _eqRowsSuppress;
            _eqRowsSuppress = true;
            try
            {
                if (!row.Hz.IsKeyboardFocused) row.Hz.Text = EqCurveEditor.FormatHz(b.FrequencyHz);
                if (!row.Db.IsKeyboardFocused) row.Db.Text = b.GainDb.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
                if (!row.Q.IsKeyboardFocused)  row.Q.Text  = b.Q.ToString("0.00", CultureInfo.InvariantCulture);
                bool cut = b.IsCut;
                row.Db.IsEnabled = !cut;
                row.Db.Opacity = cut ? 0.4 : 1.0;
                row.Slope.IsEnabled = cut;
                row.Slope.Opacity = cut ? 1.0 : 0.4;
                int slopeIdx = Math.Max(0, Array.IndexOf(ParametricEq.Slopes, ParametricEq.SnapSlope(b.SlopeDbPerOct)));
                if (row.Slope.SelectedIndex != slopeIdx) row.Slope.SelectedIndex = slopeIdx;
                if (row.On.IsChecked != b.Enabled) row.On.IsChecked = b.Enabled;
                int typeIdx = (int)b.Type;
                if (typeIdx >= 0 && typeIdx < EqTypeLabels.Length && row.Type.SelectedIndex != typeIdx) row.Type.SelectedIndex = typeIdx;
                double rowOpacity = b.Enabled ? 1.0 : 0.55;
                row.Type.Opacity = rowOpacity;
                row.Hz.Opacity = rowOpacity;
                row.Q.Opacity = rowOpacity;
            }
            finally { _eqRowsSuppress = was; }
        }

        private void HighlightEqRow(int selected)
        {
            foreach (var row in _eqRows)
                row.Root.Background = row.Index == selected ? EqRowSelectedBrush : Brushes.Transparent;
        }
    }
}
