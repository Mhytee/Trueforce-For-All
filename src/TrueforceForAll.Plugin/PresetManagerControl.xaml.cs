// Inline manager for the user's preset library, hosted on the Presets tab.
// Three segments: game presets (Settings.Presets), car presets
// (TrueforceCars/*.tfcar.json), and custom engines (Settings.CustomEngines).
// Usable without a game or car loaded so users can prune / rename / export /
// re-bind their library at any time. Mutations raise LibraryChanged so the
// host SettingsControl refreshes its always-visible header preset combos.
//
// Phase 1 scope (this file): metadata actions only, rename, duplicate,
// delete, export, and game-default binding. Editing a preset's effect
// parameters offline lands in Phase 2 (load the preset into the live
// SettingsControl with a banner + save/discard prompt on auto-switch).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace TrueforceForAll.Plugin
{
    public partial class PresetManagerControl : UserControl
    {
        public enum InitialTab { GamePresets, CarPresets, CustomEngines }

        private TrueforcePlugin _plugin;

        // Raised after any library-mutating action (rename / duplicate / delete /
        // set-default / set-active / import / custom-engine edit). The host
        // (SettingsControl) subscribes to refresh its always-visible header
        // combos and re-apply the live engine. Suppressed during the initial
        // Init load so the host isn't churned on first display.
        public event Action LibraryChanged;

        // Raised when the user clicks Edit on a game-preset row. The host
        // transitions the live panel into offline-edit mode for the named preset.
        public event Action<string> EditPresetRequested;

        // Raised when the user clicks Edit on a car-preset row (carId, presetName).
        // The host pins that car as active, loads the preset, and switches to the
        // Effects tab so the per-car edit/save flow targets it.
        public event Action<string, string> EditCarPresetRequested;


        // True only during Init's first Reload* pass, so those reloads don't
        // fire LibraryChanged.
        private bool _initializing;

        // Dev/test view: when on, the game- and car-preset lists show only the
        // built-in (factory) presets, hiding the user's own. Nothing is
        // deleted; it just filters the rows so we can eyeball the fresh-install
        // library a new user would see. Toggled by the FRESH access code.
        private bool _builtinsOnly;
        public bool BuiltinsOnly
        {
            get => _builtinsOnly;
            set
            {
                if (_builtinsOnly == value) return;
                _builtinsOnly = value;
                ReloadGames();
                ReloadCars();
            }
        }

        // Dev mode: reveal the per-row "Export as built-in" toolbar buttons
        // (games + cars). Set by the host from Settings.DevModeUnlocked.
        private bool _devMode;
        public bool DevMode
        {
            get => _devMode;
            set
            {
                _devMode = value;
                if (GameExportBuiltinBtn != null)
                    GameExportBuiltinBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (CarExportBuiltinBtn != null)
                    CarExportBuiltinBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                RefreshGameButtons();
                RefreshCarButtons();
            }
        }

        // Raised when the user clicks "Export as built-in" on a row (DEV mode).
        public event Action<string> ExportGameAsBuiltinRequested;
        public event Action<string, string> ExportCarAsBuiltinRequested;

        /// <summary>Host hook to re-pull the lists after an external library
        /// change (dev import / reseed / export).</summary>
        public void RefreshLists()
        {
            ReloadGames();
            ReloadCars();
            ReloadCustoms();
        }

        private void GameExportBuiltin_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel != null) ExportGameAsBuiltinRequested?.Invoke(sel.Name);
        }

        private void CarExportBuiltin_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel != null) ExportCarAsBuiltinRequested?.Invoke(sel.CarId, sel.PresetName);
        }

        // IsChecked on every row model backs the checkbox column. Plain bool
        // is enough: WPF writes UI → model via the TwoWay binding, and the
        // checkbox's own visual state handles its display. Re-adding rows on
        // Reload resets IsChecked to false implicitly (fresh instances).
        private sealed class GameRow
        {
            public string Name { get; set; }
            public bool   Builtin { get; set; }
            public string BuiltinLabel => Builtin ? "Built-in" : "";
            public List<string> Defaults { get; set; } = new List<string>();
            public string DefaultForLabel => Defaults.Count == 0 ? "" : string.Join(", ", Defaults);
            public bool   IsChecked { get; set; }
        }

        private sealed class CarRow
        {
            public string CarId { get; set; }
            public string PresetName { get; set; }
            public string GameName { get; set; }
            public string GameLabel => string.IsNullOrEmpty(GameName) ? "" : GameName;
            public bool   Builtin { get; set; }
            public string BuiltinLabel => Builtin ? "Built-in" : "";
            public bool   Active { get; set; }
            public string ActiveLabel => Active ? "★" : "";
            public bool   IsChecked { get; set; }
        }

        private sealed class CustomRow
        {
            public CustomEngineDef Def { get; set; }
            public string Name => Def?.Name ?? "";
            public string TypeLabel => Def != null && Def.IsElectric ? "Electric" : "Combustion";
            public string Detail
            {
                get
                {
                    if (Def == null) return "";
                    if (Def.IsElectric)
                        return Def.ElectricMode == ElectricCarMode.Silent ? "Silent" : "Muted hum";
                    int pulses = 0;
                    if (!string.IsNullOrWhiteSpace(Def.Pattern))
                    {
                        foreach (var ch in Def.Pattern) if (ch == ',') pulses++;
                        pulses++;
                    }
                    return pulses > 0 ? $"{pulses} pulses" : "(empty)";
                }
            }
            public bool   IsChecked { get; set; }
        }

        private readonly ObservableCollection<GameRow>   _gameRows   = new ObservableCollection<GameRow>();
        private readonly ObservableCollection<CarRow>    _carRows    = new ObservableCollection<CarRow>();
        private readonly ObservableCollection<CustomRow> _customRows = new ObservableCollection<CustomRow>();

        // Per-ListView sort state. The sort key for each column comes from
        // its DisplayMemberBinding.Path (e.g. "Name", "BuiltinLabel"), so the
        // checkbox column auto-skips (no DisplayMemberBinding). Base header
        // text is captured per column so the ▲/▼ indicator can be appended /
        // stripped without losing the original label.
        private sealed class ListSortState
        {
            public ListView List;
            public string CurrentSortKey;
            public bool   Descending;
            public readonly Dictionary<GridViewColumn, string> SortKeys
                = new Dictionary<GridViewColumn, string>();
            public readonly Dictionary<GridViewColumn, string> BaseHeaders
                = new Dictionary<GridViewColumn, string>();
        }
        private ListSortState _gameSort;
        private ListSortState _carSort;
        private ListSortState _customSort;

        public PresetManagerControl()
        {
            InitializeComponent();
            GameList.ItemsSource   = _gameRows;
            CarList.ItemsSource    = _carRows;
            CustomList.ItemsSource = _customRows;

            _gameSort   = BuildSortState(GameList);
            _carSort    = BuildSortState(CarList);
            _customSort = BuildSortState(CustomList);
            GameList.AddHandler(GridViewColumnHeader.ClickEvent,   new RoutedEventHandler((s, e) => HandleHeaderClick(e, _gameSort)));
            CarList.AddHandler(GridViewColumnHeader.ClickEvent,    new RoutedEventHandler((s, e) => HandleHeaderClick(e, _carSort)));
            CustomList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((s, e) => HandleHeaderClick(e, _customSort)));

            // GridView has no star-width column, so flex the primary name column
            // (index 1, after the checkbox) to fill the panel width. Was a fixed
            // 860px dialog; this makes it fit the narrower Presets tab.
            MakeFlexColumn(GameList, 1);
            MakeFlexColumn(CarList, 2);   // Car ID column (after Game)
            MakeFlexColumn(CustomList, 1);

            // GridView columns are user-resizable but have no built-in minimum,
            // so a divider can be dragged to zero and hide the column (and its
            // header). Clamp each column's width to at least its header text.
            ApplyColumnMinWidths(GameList);
            ApplyColumnMinWidths(CarList);
            ApplyColumnMinWidths(CustomList);
        }

        // Keep the flex column sized to whatever space the other (fixed) columns
        // leave. Recomputed on every resize of the list.
        private void MakeFlexColumn(ListView lv, int flexIndex)
        {
            if (lv == null) return;
            lv.SizeChanged += (s, e) => { if (e.WidthChanged) ResizeFlexColumn(lv, flexIndex); };
            lv.Loaded      += (s, e) => ResizeFlexColumn(lv, flexIndex);
        }

        private static void ResizeFlexColumn(ListView lv, int flexIndex)
        {
            if (!(lv.View is GridView gv) || flexIndex >= gv.Columns.Count) return;
            double others = 0;
            for (int i = 0; i < gv.Columns.Count; i++)
                if (i != flexIndex) others += gv.Columns[i].ActualWidth;
            // Leave room for the vertical scrollbar + content padding.
            double avail = lv.ActualWidth - others - 32;
            if (avail < 120) avail = 120;
            gv.Columns[flexIndex].Width = avail;
        }

        // Enforce a per-column minimum width (header text + padding). The header
        // gripper writes straight to GridViewColumn.Width with no floor, so we
        // watch that property and snap it back up if a drag takes it below the
        // minimum. Template-independent, unlike a header MinWidth.
        private void ApplyColumnMinWidths(ListView lv)
        {
            if (!(lv?.View is GridView gv)) return;
            var dpd = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
            if (dpd == null) return;
            foreach (var column in gv.Columns)
            {
                string text = column.Header as string;
                double min = string.IsNullOrEmpty(text) ? 28 : MeasureHeaderText(lv, text) + 26;
                var col = column; // capture per iteration
                dpd.AddValueChanged(col, (s, e) =>
                {
                    if (!double.IsNaN(col.Width) && col.Width < min) col.Width = min;
                });
            }
        }

        private static double MeasureHeaderText(Control owner, string text)
        {
            try
            {
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner).PixelsPerDip;
                var tf = new System.Windows.Media.Typeface(owner.FontFamily, owner.FontStyle, owner.FontWeight, owner.FontStretch);
                var ft = new System.Windows.Media.FormattedText(
                    text, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, tf, owner.FontSize,
                    System.Windows.Media.Brushes.Black, dpi);
                return ft.Width;
            }
            catch { return text.Length * 8.0; }
        }

        // Hydrate one ListSortState from a persisted ManageSort + apply.
        // Called from Init once the plugin reference is wired so the list opens
        // at the user's last-used sort.
        private static void HydrateSort(ListSortState state, ManageSort pref)
        {
            if (pref == null || string.IsNullOrEmpty(pref.Key)) return;
            // Only apply if the saved key still matches one of the current
            // columns, guards against renamed bindings between versions.
            bool known = false;
            foreach (var k in state.SortKeys.Values)
                if (string.Equals(k, pref.Key, StringComparison.Ordinal)) { known = true; break; }
            if (!known) return;
            ApplySort(state, pref.Key, pref.Descending);
        }

        // Capture each sortable column's base header text + sort key once so
        // the click handler can rewrite Header with the arrow without losing
        // the original label. Sortable = column has a DisplayMemberBinding
        // with a Path (the property to sort on); the checkbox column has
        // none and is skipped.
        private static ListSortState BuildSortState(ListView lv)
        {
            var s = new ListSortState { List = lv };
            if (lv.View is GridView gv)
            {
                foreach (var col in gv.Columns)
                {
                    var key = (col.DisplayMemberBinding as Binding)?.Path?.Path;
                    if (string.IsNullOrEmpty(key) || !(col.Header is string str)) continue;
                    s.SortKeys[col]    = key;
                    s.BaseHeaders[col] = str;
                }
            }
            return s;
        }

        private void HandleHeaderClick(RoutedEventArgs e, ListSortState s)
        {
            if (!(e.OriginalSource is GridViewColumnHeader hdr) || hdr.Column == null) return;
            if (!s.SortKeys.TryGetValue(hdr.Column, out var sortKey)) return;

            bool descending = string.Equals(s.CurrentSortKey, sortKey, StringComparison.Ordinal)
                ? !s.Descending
                : false;
            ApplySort(s, sortKey, descending);
            PersistSort(s);
        }

        // Apply (or clear, when sortKey is null/empty) a sort on a ListSortState:
        // rewrites the view's SortDescriptions, updates header text with ▲/▼,
        // and caches the new (key, direction) on the state itself.
        private static void ApplySort(ListSortState s, string sortKey, bool descending)
        {
            s.CurrentSortKey = sortKey;
            s.Descending     = descending;

            var view = CollectionViewSource.GetDefaultView(s.List.ItemsSource);
            if (view == null) return;
            view.SortDescriptions.Clear();
            if (!string.IsNullOrEmpty(sortKey))
                view.SortDescriptions.Add(new SortDescription(sortKey,
                    descending ? ListSortDirection.Descending : ListSortDirection.Ascending));

            foreach (var kv in s.BaseHeaders)
            {
                if (!s.SortKeys.TryGetValue(kv.Key, out var k)) continue;
                kv.Key.Header = !string.IsNullOrEmpty(sortKey) && string.Equals(k, sortKey, StringComparison.Ordinal)
                    ? kv.Value + (descending ? " ▼" : " ▲")
                    : kv.Value;
            }
        }

        // Write the sort state for one tab back into Settings + flush.
        // Triggered on every header click so a SimHub crash mid-session
        // still leaves the user's last sort persisted.
        private void PersistSort(ListSortState s)
        {
            if (_plugin?.Settings == null) return;
            var pref = new ManageSort { Key = s.CurrentSortKey, Descending = s.Descending };
            if      (s == _gameSort)   _plugin.Settings.ManageGamesSort   = pref;
            else if (s == _carSort)    _plugin.Settings.ManageCarsSort    = pref;
            else if (s == _customSort) _plugin.Settings.ManageCustomsSort = pref;
            else return;
            _plugin.PersistSettings();
        }

        public void Init(TrueforcePlugin plugin, InitialTab initialTab = InitialTab.GamePresets)
        {
            _plugin = plugin;
            _initializing = true;
            try
            {
                ReloadGames();
                ReloadCars();
                ReloadCustoms();
            }
            finally { _initializing = false; }

            // Restore last-used sort per tab from settings. Reload* clears
            // and re-fills the ObservableCollections but the view's
            // SortDescriptions live on top, so applying sort after the
            // refill is the correct order.
            var s = plugin?.Settings;
            if (s != null)
            {
                HydrateSort(_gameSort,   s.ManageGamesSort);
                HydrateSort(_carSort,    s.ManageCarsSort);
                HydrateSort(_customSort, s.ManageCustomsSort);
            }

            SelectTab(initialTab);
        }

        /// <summary>Bring one of the inner tabs (game / car / custom) forward.
        /// Called by the host when it switches to the Presets tab from a
        /// context-specific entry point (e.g. "manage custom engines").</summary>
        public void SelectTab(InitialTab tab)
        {
            switch (tab)
            {
                case InitialTab.CarPresets:    if (SegCar    != null) SegCar.IsChecked    = true; break;
                case InitialTab.CustomEngines: if (SegCustom != null) SegCustom.IsChecked = true; break;
                default:                       if (SegGame   != null) SegGame.IsChecked   = true; break;
            }
        }

        // Segmented selector clicked (or set programmatically). Show the chosen
        // view, hide the other two. Guarded so it's a no-op during the XAML
        // load pass, when SegGame's default IsChecked fires before the panels
        // exist (initial visibility is set in XAML instead).
        private void Segment_Checked(object sender, RoutedEventArgs e)
        {
            if (GamePanel == null || CarPanel == null || CustomPanel == null) return;
            GamePanel.Visibility   = SegGame.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
            CarPanel.Visibility    = SegCar.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
            CustomPanel.Visibility = SegCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // Last preset the user clicked Edit on. Kept for reference; the actual
        // hand-off to offline-edit mode now happens via the EditPresetRequested
        // event (the control is embedded, there's no dialog close to read on).
        public string RequestedEditPresetName { get; private set; }

        // ===================== Reload =====================

        // Fire LibraryChanged unless we're inside Init's first load. Called at
        // the tail of each Reload*, which the mutating handlers all run.
        private void MaybeNotifyChanged()
        {
            if (!_initializing) LibraryChanged?.Invoke();
        }

        private void ReloadGames()
        {
            _gameRows.Clear();
            if (_plugin?.Settings?.Presets == null) { GameList_SelectionChanged(null, null); return; }
            // Build a reverse index from preset → list of games defaulting to it.
            var reverseDefaults = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (_plugin.Settings.GameDefaults != null)
            {
                foreach (var kv in _plugin.Settings.GameDefaults)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    if (!reverseDefaults.TryGetValue(kv.Value, out var list))
                    {
                        list = new List<string>();
                        reverseDefaults[kv.Value] = list;
                    }
                    list.Add(kv.Key);
                }
            }
            // Alphabetical so the list is stable across reloads.
            foreach (var name in _plugin.Settings.Presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (_builtinsOnly && !_plugin.IsBuiltinPreset(name)) continue;
                reverseDefaults.TryGetValue(name, out var defaults);
                _gameRows.Add(new GameRow
                {
                    Name     = name,
                    Builtin  = _plugin.IsBuiltinPreset(name),
                    Defaults = defaults ?? new List<string>(),
                });
            }
            GameList_SelectionChanged(null, null);
            MaybeNotifyChanged();
        }

        private void ReloadCars()
        {
            _carRows.Clear();
            if (_plugin == null) { CarList_SelectionChanged(null, null); return; }
            var all = _plugin.GetAllCarPresets();
            var carDefaults = _plugin.Settings?.CarDefaults
                ?? new Dictionary<string, string>();
            // Group by game, then by car, then preset name: all of a game's cars
            // sit together, and a car's presets sit together under it. A car's
            // game comes from its presets (they share one); empty games sort first.
            foreach (var carKv in all
                .OrderBy(k => k.Value.Values.Select(v => v.GameName).FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string carId = carKv.Key;
                carDefaults.TryGetValue(carId, out var activeName);
                foreach (var presetKv in carKv.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = presetKv.Value;
                    if (_builtinsOnly && !entry.IsBuiltin) continue;
                    _carRows.Add(new CarRow
                    {
                        CarId      = carId,
                        PresetName = entry.PresetName,
                        GameName   = entry.GameName,
                        Builtin    = entry.IsBuiltin,
                        Active     = string.Equals(activeName, entry.PresetName, StringComparison.Ordinal),
                    });
                }
            }
            CarList_SelectionChanged(null, null);
            MaybeNotifyChanged();
        }

        private void ReloadCustoms()
        {
            _customRows.Clear();
            var list = _plugin?.Settings?.CustomEngines;
            if (list != null)
            {
                foreach (var c in list)
                    if (c != null) _customRows.Add(new CustomRow { Def = c });
            }
            CustomList_SelectionChanged(null, null);
            MaybeNotifyChanged();
        }

        // ===================== Selection state =====================

        private GameRow SelectedGame   => GameList.SelectedItem as GameRow;
        private CarRow  SelectedCar    => CarList.SelectedItem  as CarRow;
        private CustomRow SelectedCustom => CustomList.SelectedItem as CustomRow;

        private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshGameButtons();
            UpdateGameDetails();
        }

        // Read-only summary of the selected game preset's contents (master gain,
        // FFB, and each effect's on/off + gain). Reflection over the effect
        // objects keeps this in step with new effects without per-type code.
        private void UpdateGameDetails()
        {
            if (GameDetailsText == null) return;
            var sel = SelectedGame;
            if (sel == null || _plugin?.Settings?.Presets == null
                || !_plugin.Settings.Presets.TryGetValue(sel.Name, out var snap) || snap == null)
            {
                GameDetailsText.Text = "Select a preset to see the settings it contains.";
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Master gain: {snap.MasterGain:0.##}");
            sb.AppendLine($"FFB pass-through: scale {snap.FfbScale:0.##}, smooth {snap.FfbSmoothTimeConstantMs:0} ms, invert {(snap.FfbInvertSign ? "on" : "off")}");
            sb.AppendLine($"FFB spike reduction: {(snap.FfbSpikeTamingEnabled ? "on" : "off")}");
            AppendEffectLine(sb, "Audio capture",    snap.AudioCapture);
            AppendEffectLine(sb, "Engine pulse",     snap.EnginePulse);
            AppendEffectLine(sb, "Road bumps",       snap.RoadBumps);
            AppendEffectLine(sb, "Traction loss",    snap.TractionLoss);
            AppendEffectLine(sb, "Gear shift",       snap.GearShift);
            AppendEffectLine(sb, "ABS",              snap.AbsClick);
            AppendEffectLine(sb, "Pit limiter",      snap.PitLimiter);
            AppendEffectLine(sb, "DRS",              snap.Drs);
            AppendEffectLine(sb, "Collision",        snap.Collision);
            AppendEffectLine(sb, "Rev limiter",      snap.RevLimiter);
            AppendEffectLine(sb, "Airborne ducking", snap.Airborne);
            GameDetailsText.Text = sb.ToString().TrimEnd();
        }

        private static void AppendEffectLine(System.Text.StringBuilder sb, string label, object eff)
        {
            if (eff == null) { sb.AppendLine($"{label}: (preset default)"); return; }
            var t = eff.GetType();
            bool enabled = (t.GetProperty("Enabled")?.GetValue(eff) as bool?) ?? true;
            string gainStr = "";
            if (enabled)
            {
                var g = t.GetProperty("Gain")?.GetValue(eff);
                if (g is float gf)       gainStr = $" (gain {gf:0.##})";
                else if (g is double gd) gainStr = $" (gain {gd:0.##})";
            }
            sb.AppendLine($"{label}: {(enabled ? "on" : "off")}{gainStr}");
        }

        private void CarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshCarButtons();
        }

        private void CustomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshCustomButtons();
        }

        // Fires from any row checkbox toggle in any tab, figure out which
        // tab the sender belongs to via the row type and refresh that tab's
        // button states + checked-count label. The XAML wires the same
        // handler on all three checkbox columns so we only maintain one.
        private void RowCheckBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb)) return;
            // Checking a row also highlights it, so the single-row actions
            // (Rename / Duplicate / Set default / Set active / Edit) treat a
            // check as a selection. Unchecking the highlighted row clears it.
            if (cb.IsChecked == true)
            {
                switch (cb.DataContext)
                {
                    case GameRow gr:    GameList.SelectedItem   = gr; break;
                    case CarRow cr:     CarList.SelectedItem    = cr; break;
                    case CustomRow cu:  CustomList.SelectedItem = cu; break;
                }
            }
            else
            {
                if (cb.DataContext is GameRow gr && ReferenceEquals(GameList.SelectedItem, gr))     GameList.SelectedItem   = null;
                if (cb.DataContext is CarRow cr && ReferenceEquals(CarList.SelectedItem, cr))       CarList.SelectedItem    = null;
                if (cb.DataContext is CustomRow cu && ReferenceEquals(CustomList.SelectedItem, cu)) CustomList.SelectedItem = null;
            }
            switch (cb.DataContext)
            {
                case GameRow _:   RefreshGameButtons();   break;
                case CarRow _:    RefreshCarButtons();    break;
                case CustomRow _: RefreshCustomButtons(); break;
            }
        }

        // Bulk-capable buttons (Delete, Export) light up when any row is
        // checked, even if the highlighted row is built-in or no row is
        // selected, the action operates on the checked set in that case.
        // Single-row buttons (Rename, Duplicate, Set default, Edit) still key
        // off the highlighted row only.
        private void RefreshGameButtons()
        {
            var sel = SelectedGame;
            int   checkedCount        = _gameRows.Count(r => r.IsChecked);
            int   checkedNonBuiltin   = _gameRows.Count(r => r.IsChecked && !r.Builtin);
            bool  anySelected         = sel != null;
            bool  selUserPreset       = anySelected && !sel.Builtin;

            // DEV authoring lets the owner act on built-ins too (rename / delete).
            bool selEditable = selUserPreset || (_devMode && anySelected);
            GameEditBtn.IsEnabled         = anySelected   && checkedCount <= 1;
            GameRenameBtn.IsEnabled       = selEditable   && checkedCount <= 1;
            GameDuplicateBtn.IsEnabled    = anySelected   && checkedCount <= 1;
            GameDeleteBtn.IsEnabled       = checkedNonBuiltin > 0 || selEditable;
            GameSetDefaultBtn.IsEnabled   = anySelected   && checkedCount <= 1;
            GameClearDefaultBtn.IsEnabled = anySelected   && checkedCount <= 1 && sel.Defaults.Count > 0;
            if (GameExportBuiltinBtn != null)
                GameExportBuiltinBtn.IsEnabled = _devMode && anySelected && checkedCount <= 1;

            GameCheckedLabel.Text = checkedCount > 0
                ? $"{checkedCount} checked"
                : "";
            // Bulk delete labels: clue the user that the action applies to
            // the checked set, not the highlighted row.
            GameDeleteBtn.Content = checkedNonBuiltin > 0 ? $"Delete ({checkedNonBuiltin})" : "Delete";
        }

        private void RefreshCarButtons()
        {
            var sel = SelectedCar;
            int   checkedCount      = _carRows.Count(r => r.IsChecked);
            int   checkedNonBuiltin = _carRows.Count(r => r.IsChecked && !r.Builtin);
            bool  anySelected       = sel != null;
            bool  selUserPreset     = anySelected && !sel.Builtin;

            // DEV authoring lets the owner delete built-in car presets too
            // (rename stays user-only: car built-ins are keyed by carId).
            bool carSelDeletable = selUserPreset || (_devMode && anySelected);
            CarEditBtn.IsEnabled      = anySelected   && checkedCount <= 1;
            CarRenameBtn.IsEnabled    = selUserPreset && checkedCount <= 1;
            CarDuplicateBtn.IsEnabled = anySelected   && checkedCount <= 1;
            CarDeleteBtn.IsEnabled    = checkedNonBuiltin > 0 || carSelDeletable;
            CarSetActiveBtn.IsEnabled = anySelected   && checkedCount <= 1 && !sel.Active;
            if (CarExportBuiltinBtn != null)
                CarExportBuiltinBtn.IsEnabled = _devMode && anySelected && checkedCount <= 1;

            CarCheckedLabel.Text = checkedCount > 0 ? $"{checkedCount} checked" : "";
            CarDeleteBtn.Content = checkedNonBuiltin > 0 ? $"Delete ({checkedNonBuiltin})" : "Delete";
        }

        private void RefreshCustomButtons()
        {
            int   checkedCount = _customRows.Count(r => r.IsChecked);
            bool  any          = SelectedCustom != null;

            CustomEditBtn.IsEnabled   = any && checkedCount <= 1;
            CustomDeleteBtn.IsEnabled = checkedCount > 0 || any;

            CustomCheckedLabel.Text = checkedCount > 0 ? $"{checkedCount} checked" : "";
            CustomDeleteBtn.Content = checkedCount > 0 ? $"Delete ({checkedCount})" : "Delete";
        }

        // ===================== Game preset actions =====================

        private void GameRename_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null || (sel.Builtin && !_devMode)) return;   // DEV may rename built-ins
            string newName = PromptForName("Rename preset", "New name:", sel.Name);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (newName == sel.Name) return;
            if (_plugin.Settings?.Presets?.ContainsKey(newName) == true)
            {
                MessageBox.Show(Window.GetWindow(this), $"A preset named '{newName}' already exists.", "Rename preset",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_plugin.RenamePreset(sel.Name, newName))
            {
                MessageBox.Show(Window.GetWindow(this), "Rename failed.", "Rename preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ReloadGames();
            SelectGameByName(newName);
        }

        private void GameDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null) return;
            string suggested = SuggestCopyName(sel.Name, n => _plugin.Settings?.Presets?.ContainsKey(n) == true);
            string newName = PromptForName("Duplicate preset", "New preset name:", suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (_plugin.Settings?.Presets?.ContainsKey(newName) == true)
            {
                MessageBox.Show(Window.GetWindow(this), $"A preset named '{newName}' already exists.", "Duplicate preset",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_plugin.DuplicatePreset(sel.Name, newName))
            {
                MessageBox.Show(Window.GetWindow(this), "Duplicate failed.", "Duplicate preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ReloadGames();
            SelectGameByName(newName);
        }

        private void GameDelete_Click(object sender, RoutedEventArgs e)
        {
            // Bulk path: any checkboxes ticked = delete the whole set (built-
            // ins are filtered out since the plugin refuses them).
            var bulk = _gameRows.Where(r => r.IsChecked && !r.Builtin).ToList();
            if (bulk.Count > 0)
            {
                int affected = bulk.SelectMany(r => r.Defaults).Distinct().Count();
                string detail = affected > 0
                    ? $"\n\n{affected} game default binding(s) will be cleared."
                    : "";
                string list = string.Join(", ", bulk.Take(10).Select(r => "'" + r.Name + "'"))
                    + (bulk.Count > 10 ? $" and {bulk.Count - 10} more" : "");
                if (MessageBox.Show(Window.GetWindow(this), $"Delete {bulk.Count} preset(s)?\n\n{list}{detail}",
                    "Delete presets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var r in bulk) _plugin.DeletePreset(r.Name);
                ReloadGames();
                return;
            }

            var sel = SelectedGame;
            if (sel == null || (sel.Builtin && !_devMode)) return;   // DEV may delete built-ins
            string warning = sel.Defaults.Count > 0
                ? $"Delete preset '{sel.Name}'?\n\nIt's currently the default for: {string.Join(", ", sel.Defaults)}. Those games will lose their auto-load binding."
                : $"Delete preset '{sel.Name}'?";
            if (MessageBox.Show(Window.GetWindow(this), warning, "Delete preset", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            _plugin.DeletePreset(sel.Name);
            ReloadGames();
        }

        // Export / Import: routed through SettingsControl's shared flow so this
        // matches the Backup & sync buttons. Owner = the host window, so the
        // pack picker / metadata dialog / file pickers sit above the panel.
        private void DialogExport_Click(object sender, RoutedEventArgs e)
        {
            SettingsControl.RunExportFlow(Window.GetWindow(this), _plugin);
        }

        private void DialogImport_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsControl.RunImportFlow(Window.GetWindow(this), _plugin))
            {
                // Imported preset / car preset / pack / settings, reload all
                // three tabs since any kind of import can touch any tab's view.
                ReloadGames();
                ReloadCars();
                ReloadCustoms();
            }
        }

        private void GameSetDefault_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null) return;
            var known = CollectKnownGames();
            if (known.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "No games seen yet. Launch a game once so SimHub registers it, then come back to bind a default preset.",
                    "Set default for game", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string game = PickFromList("Set default for game",
                $"Pick a game to auto-load preset '{sel.Name}' for. Listed games are ones SimHub has seen on your machine.",
                known);
            if (string.IsNullOrEmpty(game)) return;
            _plugin.SetDefaultPresetForGame(game, sel.Name);
            ReloadGames();
            SelectGameByName(sel.Name);
        }

        private void GameClearDefault_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null || sel.Defaults.Count == 0) return;
            string game = sel.Defaults.Count == 1
                ? sel.Defaults[0]
                : PickFromList("Clear default for game",
                    $"This preset is the default for multiple games. Pick which game's binding to clear.",
                    sel.Defaults);
            if (string.IsNullOrEmpty(game)) return;
            _plugin.ClearDefaultPresetForGame(game);
            ReloadGames();
            SelectGameByName(sel.Name);
        }

        private void GameEdit_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null) return;
            RequestedEditPresetName = sel.Name;
            EditPresetRequested?.Invoke(sel.Name);
        }


        private void SelectGameByName(string name)
        {
            foreach (var r in _gameRows)
            {
                if (string.Equals(r.Name, name, StringComparison.Ordinal))
                {
                    GameList.SelectedItem = r;
                    GameList.ScrollIntoView(r);
                    break;
                }
            }
        }

        // ===================== Car preset actions =====================

        private void CarRename_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null || sel.Builtin) return;
            string newName = PromptForName("Rename car preset",
                $"New name for '{sel.CarId}' preset:", sel.PresetName);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (newName == sel.PresetName) return;
            if (!_plugin.RenameCarPreset(sel.CarId, sel.PresetName, newName))
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Rename failed. A preset with that name may already exist for this car, or the source preset is a built-in.",
                    "Rename car preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ReloadCars();
            SelectCarRow(sel.CarId, newName);
        }

        private void CarDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null) return;
            // Build the existing-names set for this car so suggestion logic
            // doesn't propose a name that's already taken.
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in _carRows)
                if (string.Equals(r.CarId, sel.CarId, StringComparison.Ordinal))
                    existing.Add(r.PresetName);
            string suggested = SuggestCopyName(sel.PresetName, n => existing.Contains(n));
            string newName = PromptForName("Duplicate car preset",
                $"New preset name for '{sel.CarId}':", suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (existing.Contains(newName))
            {
                MessageBox.Show(Window.GetWindow(this), $"A preset named '{newName}' already exists for this car.",
                    "Duplicate car preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_plugin.DuplicateCarPreset(sel.CarId, sel.PresetName, newName))
            {
                MessageBox.Show(Window.GetWindow(this), "Duplicate failed.", "Duplicate car preset",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ReloadCars();
            SelectCarRow(sel.CarId, newName);
        }

        private void CarDelete_Click(object sender, RoutedEventArgs e)
        {
            var bulk = _carRows.Where(r => r.IsChecked && !r.Builtin).ToList();
            if (bulk.Count > 0)
            {
                int active = bulk.Count(r => r.Active);
                string detail = active > 0
                    ? $"\n\n{active} of the selected preset(s) are currently the default for their car. Those cars will fall back to their built-in default or globals."
                    : "";
                if (MessageBox.Show(Window.GetWindow(this),
                    $"Delete {bulk.Count} car preset(s)?{detail}",
                    "Delete car presets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var r in bulk) _plugin.DeleteCarPreset(r.CarId, r.PresetName);
                ReloadCars();
                return;
            }

            var sel = SelectedCar;
            if (sel == null || (sel.Builtin && !_devMode)) return;   // DEV may delete built-in car presets
            string warning = sel.Active
                ? $"Delete preset '{sel.PresetName}' for car '{sel.CarId}'?\n\nIt's currently the default for this car; the car will fall back to its built-in default (or globals)."
                : $"Delete preset '{sel.PresetName}' for car '{sel.CarId}'?";
            if (MessageBox.Show(Window.GetWindow(this), warning, "Delete car preset",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _plugin.DeleteCarPreset(sel.CarId, sel.PresetName);
            ReloadCars();
        }

        private void CarSetActive_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null || sel.Active) return;
            _plugin.SetCarDefaultPreset(sel.CarId, sel.PresetName);  // DEV: also writes car-defaults.json
            ReloadCars();
            SelectCarRow(sel.CarId, sel.PresetName);
        }

        private void CarEdit_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null) return;
            EditCarPresetRequested?.Invoke(sel.CarId, sel.PresetName);
        }

        private void SelectCarRow(string carId, string presetName)
        {
            foreach (var r in _carRows)
            {
                if (string.Equals(r.CarId, carId, StringComparison.Ordinal)
                    && string.Equals(r.PresetName, presetName, StringComparison.Ordinal))
                {
                    CarList.SelectedItem = r;
                    CarList.ScrollIntoView(r);
                    break;
                }
            }
        }

        // ===================== Custom engine actions =====================

        private void CustomEdit_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedCustom;
            if (row?.Def == null) return;
            // Edit a draft copy so Cancel doesn't half-apply; on Save copy
            // values back into the live entry (Id stays the same so any
            // preset reference survives).
            var draft = new CustomEngineDef
            {
                Id           = row.Def.Id,
                Name         = row.Def.Name,
                IsElectric   = row.Def.IsElectric,
                ElectricMode = row.Def.ElectricMode,
                Pattern      = row.Def.Pattern,
            };
            var editor = new CustomEngineEditor { Owner = Window.GetWindow(this) };
            editor.Init(draft, "Edit custom engine");
            if (editor.ShowDialog() == true && editor.Saved)
            {
                row.Def.Name         = draft.Name;
                row.Def.IsElectric   = draft.IsElectric;
                row.Def.ElectricMode = draft.ElectricMode;
                row.Def.Pattern      = draft.Pattern;
                _plugin.PersistSettings();
                ReloadCustoms();
                SelectCustomById(row.Def.Id);
            }
        }

        private void CustomDelete_Click(object sender, RoutedEventArgs e)
        {
            var bulk = _customRows.Where(r => r.IsChecked && r.Def != null).ToList();
            if (bulk.Count > 0)
            {
                if (MessageBox.Show(Window.GetWindow(this),
                    $"Delete {bulk.Count} custom engine(s)?\n\n"
                    + "Presets that referenced them will fall back to silence until you repick from the engine dropdown.",
                    "Delete custom engines", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes) return;
                var ids = new HashSet<string>(bulk.Select(r => r.Def.Id), StringComparer.Ordinal);
                _plugin.Settings.CustomEngines.RemoveAll(c => c != null && ids.Contains(c.Id));
                _plugin.PersistSettings();
                ReloadCustoms();
                return;
            }

            var row = SelectedCustom;
            if (row?.Def == null) return;
            if (MessageBox.Show(Window.GetWindow(this),
                $"Delete custom engine '{row.Def.Name}'?\n\n"
                + "Presets that referenced it will fall back to silence until you repick from the engine dropdown.",
                "Delete custom engine", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            _plugin.Settings.CustomEngines.RemoveAll(c => c != null && c.Id == row.Def.Id);
            _plugin.PersistSettings();
            ReloadCustoms();
        }

        private void SelectCustomById(string id)
        {
            foreach (var r in _customRows)
            {
                if (r.Def != null && string.Equals(r.Def.Id, id, StringComparison.Ordinal))
                {
                    CustomList.SelectedItem = r;
                    CustomList.ScrollIntoView(r);
                    break;
                }
            }
        }

        // ===================== Helpers =====================

        // Build the list of games we know about: union of every game with a
        // default binding, every game with a per-game enable entry, and the
        // currently-active game if any. Sorted, deduped.
        private List<string> CollectKnownGames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_plugin?.Settings?.GameDefaults != null)
                foreach (var k in _plugin.Settings.GameDefaults.Keys)
                    if (!string.IsNullOrEmpty(k)) set.Add(k);
            if (_plugin?.Settings?.GameEnabled != null)
                foreach (var k in _plugin.Settings.GameEnabled.Keys)
                    if (!string.IsNullOrEmpty(k)) set.Add(k);
            string active = _plugin?.ActiveGame;
            if (!string.IsNullOrEmpty(active)) set.Add(active);
            return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Suggest "Name (copy)", "Name (copy 2)", ... until one isn't taken.
        // Caller passes a predicate that returns true when a candidate already
        // exists in the target collection.
        private static string SuggestCopyName(string baseName, Func<string, bool> exists)
        {
            string candidate = baseName + " (copy)";
            if (!exists(candidate)) return candidate;
            for (int i = 2; i < 1000; i++)
            {
                candidate = $"{baseName} (copy {i})";
                if (!exists(candidate)) return candidate;
            }
            return baseName + " (copy)";
        }

        // Mirror of SettingsControl.PromptForName so this control stays self-
        // contained. Returns the trimmed text or null on Cancel.
        private string PromptForName(string title, string label, string defaultValue)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            SettingsControl.ApplyDarkTheme(win);
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var tb = new TextBox { Text = defaultValue ?? "" };
            sp.Children.Add(tb);
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
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

        // Modal list picker. Returns the selected item or null on Cancel.
        private string PickFromList(string title, string helpText, IList<string> items)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };
            SettingsControl.ApplyDarkTheme(win);
            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var help = new TextBlock
            {
                Text = helpText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(help, 0);
            grid.Children.Add(help);
            var lb = new ListBox();
            foreach (var it in items) lb.Items.Add(it);
            if (lb.Items.Count > 0) lb.SelectedIndex = 0;
            Grid.SetRow(lb, 1);
            grid.Children.Add(lb);
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            Grid.SetRow(btnRow, 2);
            grid.Children.Add(btnRow);
            win.Content = grid;
            string result = null;
            ok.Click += (s, args) => { result = lb.SelectedItem as string; if (result != null) win.DialogResult = true; };
            lb.MouseDoubleClick += (s, args) => { result = lb.SelectedItem as string; if (result != null) win.DialogResult = true; };
            return win.ShowDialog() == true ? result : null;
        }
    }
}
