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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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

        // Dev mode: reveal the per-row "Set as built-in" promote toolbar
        // buttons (games + cars). Set by the host from Settings.DevModeUnlocked.
        private bool _devMode;
        public bool DevMode
        {
            get => _devMode;
            set
            {
                _devMode = value;
                if (GamePromoteBuiltinBtn != null)
                    GamePromoteBuiltinBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (CarPromoteBuiltinBtn != null)
                    CarPromoteBuiltinBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (DevBar != null)
                    DevBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                if (DevFolderPathText != null)
                    DevFolderPathText.Text = _plugin?.BuiltinFolderPath ?? "";
                RefreshGameButtons();
                RefreshCarButtons();
            }
        }

        // ---- Developer tools (built-in folder maintenance) ----

        private void DevOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _plugin?.BuiltinFolderPath;
                if (string.IsNullOrEmpty(folder)) return;
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex) { SetDevStatus("Open folder failed: " + ex.Message); }
        }

        private void DevValidate_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var lines = _plugin.ValidateBuiltins();
            int issues = lines.Count(l => !l.StartsWith("OK"));
            string body = lines.Count == 0 ? "No built-ins loaded." : string.Join("\n", lines);
            MessageBox.Show(Window.GetWindow(this), body,
                $"Validate built-ins ({issues} issue{(issues == 1 ? "" : "s")})",
                MessageBoxButton.OK, issues > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            SetDevStatus(issues == 0
                ? $"Validated {lines.Count} built-in(s): all OK."
                : $"Validated {lines.Count} built-in(s): {issues} flagged (see dialog).");
        }

        private void DevRefreshLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            try
            {
                string msg = _plugin.ReloadLibraryFromFolders();
                RefreshLists();
                SetDevStatus(msg);
            }
            catch (Exception ex) { SetDevStatus("Refresh failed: " + ex.Message); }
        }

        private void SetDevStatus(string text)
        {
            if (DevStatusText != null) DevStatusText.Text = text;
        }

        /// <summary>Host hook to re-pull the lists after an external library
        /// change (dev import / reseed / export). Reload* raise LibraryChanged
        /// so the host refreshes its effects UI / live engine too.</summary>
        public void RefreshLists()
        {
            ReloadGames();
            ReloadCars();
            ReloadCustoms();
        }

        // Promote one or more game-preset rows to built-in. Checked rows take
        // priority (bulk); fall back to the highlighted selected row when
        // nothing is checked.
        private void GamePromoteBuiltin_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var targets = _gameRows.Where(r => r.IsChecked).Select(r => r.Name).ToList();
            if (targets.Count == 0)
            {
                var sel = SelectedGame;
                if (sel == null) return;
                targets.Add(sel.Name);
            }
            int ok = 0, failed = 0;
            string lastErr = null;
            foreach (var name in targets)
            {
                if (_plugin.PromoteGamePresetToBuiltin(name, out string err)) ok++;
                else { failed++; lastErr = err; }
            }
            RefreshLists();
            SetDevStatus(failed == 0
                ? (ok == 1 ? $"Promoted '{targets[0]}' to built-in." : $"Promoted {ok} preset(s) to built-in.")
                : $"Promoted {ok}, {failed} failed (last error: {lastErr}).");
        }

        // Same shape for cars: bulk-promote checked rows, else the selected one.
        private void CarPromoteBuiltin_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var targets = _carRows.Where(r => r.IsChecked)
                                  .Select(r => (r.CarId, r.PresetName))
                                  .ToList();
            if (targets.Count == 0)
            {
                var sel = SelectedCar;
                if (sel == null) return;
                targets.Add((sel.CarId, sel.PresetName));
            }
            int ok = 0, failed = 0;
            string lastErr = null;
            foreach (var (carId, presetName) in targets)
            {
                if (_plugin.PromoteCarPresetToBuiltin(carId, presetName, out string err)) ok++;
                else { failed++; lastErr = err; }
            }
            RefreshLists();
            SetDevStatus(failed == 0
                ? (ok == 1 ? $"Promoted car '{targets[0].CarId}' to built-in." : $"Promoted {ok} car preset(s) to built-in.")
                : $"Promoted {ok}, {failed} failed (last error: {lastErr}).");
        }

        // Row base: IsChecked needs to notify so the ItemContainerStyle's
        // DataTrigger(IsChecked=True) re-evaluates when the user clicks the
        // checkbox (a plain auto-property wouldn't fire change notifications,
        // so the row tint would stay stale).
        private abstract class PresetRowBase : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, _isCheckedArgs);
                }
            }
            private static readonly PropertyChangedEventArgs _isCheckedArgs
                = new PropertyChangedEventArgs(nameof(IsChecked));
        }

        private sealed class GameRow : PresetRowBase
        {
            public string Name { get; set; }
            public bool   Builtin { get; set; }
            // Source column label: "Built-In" for factory presets, "Local" for
            // the user's own, or the pack label for community-imported presets.
            // Populated at row build via the plugin's source resolver. Builtin
            // (above) stays as the protection flag the action buttons read.
            public string Source { get; set; } = "";
            public List<string> Defaults { get; set; } = new List<string>();
            public string DefaultForLabel => Defaults.Count == 0 ? "" : string.Join(", ", Defaults);
            // Bound by the expandable-row template's details area; populated
            // at row creation in ReloadGames so each row carries its own
            // summary string, no per-click rebuild needed.
            public string DetailsText { get; set; }
        }

        private sealed class CarRow : PresetRowBase
        {
            public string CarId { get; set; }
            public string PresetName { get; set; }
            // Suffix-stripped name for the grid's "Preset name" column. The
            // real PresetName keeps the " (Built-In)" suffix (it is the merged-
            // map key and the default-match key); the Source column now shows
            // "Built-In", so the suffix in the column would just be redundant.
            public string DisplayName { get; set; }
            // Human-readable car name surfaced by the CarFacts layer
            // (CarFactsBundle.CarName). Empty when no name is on file for
            // this car_id; the grid column then renders blank so the row
            // doesn't pretend a name exists.
            public string CarName { get; set; }
            public string GameName { get; set; }
            public string GameLabel => string.IsNullOrEmpty(GameName) ? "" : GameName;
            public bool   Builtin { get; set; }
            // Source column label, populated at row build (see GameRow.Source).
            public string Source { get; set; } = "";
            public bool   Active { get; set; }
            public string ActiveLabel => Active ? "★" : "";
            public string DetailsText { get; set; }
        }

        private sealed class CustomRow : PresetRowBase
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
        }

        private readonly ObservableCollection<GameRow>   _gameRows   = new ObservableCollection<GameRow>();
        private readonly ObservableCollection<CarRow>    _carRows    = new ObservableCollection<CarRow>();
        private readonly ObservableCollection<CustomRow> _customRows = new ObservableCollection<CustomRow>();

        // Per-DataGrid sort state. The sort key for each column comes from
        // its Binding.Path (e.g. "Name", "Source"). The checkbox
        // column has no Binding so it auto-skips. Base header text is
        // captured per column so the ▲/▼ indicator can be appended /
        // stripped without losing the original label.
        private sealed class ListSortState
        {
            public DataGrid List;
            public string CurrentSortKey;
            public bool   Descending;
            public readonly Dictionary<DataGridColumn, string> SortKeys
                = new Dictionary<DataGridColumn, string>();
            public readonly Dictionary<DataGridColumn, string> BaseHeaders
                = new Dictionary<DataGridColumn, string>();
        }
        private ListSortState _gameSort;
        private ListSortState _carSort;
        private ListSortState _customSort;

        // Filter state. Search strings come from the per-tab TextBox; the car
        // tab also has a per-game chip filter (null = all games). The default
        // CollectionViews of each list expose a Filter callback that combines
        // both. Re-applied via View.Refresh() on TextChanged / chip click.
        private string _gameSearch   = "";
        private string _carSearch    = "";
        private string _customSearch = "";
        private string _carGameFilter; // null = all
        // The "All" chip is kept around so we can re-check it programmatically
        // when ReloadCars rebuilds the chip set.
        private RadioButton _carGameAllChip;

        public PresetManagerControl()
        {
            InitializeComponent();
            GameList.ItemsSource   = _gameRows;
            CarList.ItemsSource       = _carRows;
            CustomList.ItemsSource    = _customRows;
            CommunityList.ItemsSource = _communityRows;
            // Wire the filter predicates onto each list's default view.
            CollectionViewSource.GetDefaultView(_gameRows).Filter   = GameRowFilter;
            CollectionViewSource.GetDefaultView(_carRows).Filter    = CarRowFilter;
            CollectionViewSource.GetDefaultView(_customRows).Filter = CustomRowFilter;

            _gameSort   = BuildSortState(GameList);
            _carSort    = BuildSortState(CarList);
            _customSort = BuildSortState(CustomList);
            GameList.AddHandler(DataGridColumnHeader.ClickEvent,   new RoutedEventHandler((s, e) => HandleHeaderClick(e, _gameSort)));
            CarList.AddHandler(DataGridColumnHeader.ClickEvent,    new RoutedEventHandler((s, e) => HandleHeaderClick(e, _carSort)));
            CustomList.AddHandler(DataGridColumnHeader.ClickEvent, new RoutedEventHandler((s, e) => HandleHeaderClick(e, _customSort)));

            // Watch every column's Width DP so a user drag-resize persists.
            // The ColumnDisplayIndexChanged handler covers reorders; widths
            // need a separate hook because WPF doesn't raise a "resize done"
            // event - the gripper writes straight to Column.Width.
            WatchColumnWidths(GameList);
            WatchColumnWidths(CarList);
            WatchColumnWidths(CustomList);
        }

        private void WatchColumnWidths(DataGrid dg)
        {
            var desc = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            if (desc == null) return;
            foreach (var col in dg.Columns)
            {
                // Skip the checkbox template column. It has no Binding so
                // PersistColumns would drop it anyway, but no point firing
                // PersistColumns on its layout passes either.
                if (!(col is DataGridBoundColumn)) continue;
                desc.AddValueChanged(col, (s, e) => PersistColumns(dg));
            }
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
        private static ListSortState BuildSortState(DataGrid dg)
        {
            var s = new ListSortState { List = dg };
            foreach (var col in dg.Columns)
            {
                // The text columns expose their binding directly. The
                // checkbox template column has no Binding, so it's auto
                // skipped here (no SortKey, so a header click on that column
                // becomes a no-op).
                string key = null;
                if (col is DataGridBoundColumn boundCol && boundCol.Binding is Binding b)
                    key = b.Path?.Path;
                if (string.IsNullOrEmpty(key) || !(col.Header is string str)) continue;
                s.SortKeys[col]    = key;
                s.BaseHeaders[col] = str;
            }
            return s;
        }

        private void HandleHeaderClick(RoutedEventArgs e, ListSortState s)
        {
            if (!(e.OriginalSource is DataGridColumnHeader hdr) || hdr.Column == null) return;
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

                HydrateColumnLayout(GameList,   s.ManageGamesColumns);
                HydrateColumnLayout(CarList,    s.ManageCarsColumns);
                HydrateColumnLayout(CustomList, s.ManageCustomsColumns);
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
            if (GamePanel == null || CarPanel == null || CustomPanel == null
                || CommunityPanel == null) return;
            GamePanel.Visibility      = SegGame.IsChecked      == true ? Visibility.Visible : Visibility.Collapsed;
            CarPanel.Visibility       = SegCar.IsChecked       == true ? Visibility.Visible : Visibility.Collapsed;
            CustomPanel.Visibility    = SegCustom.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
            CommunityPanel.Visibility = SegCommunity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // Lazy-load the community list the first time the user opens
            // the segment so the panel-open isn't tied to plugin startup.
            if (SegCommunity.IsChecked == true && _communityRows.Count == 0
                && !_communityFetchInFlight)
                _ = CommunityRefreshAsync();
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
                _plugin.Settings.Presets.TryGetValue(name, out var snap);
                bool builtin = _plugin.IsBuiltinPreset(name);
                _gameRows.Add(new GameRow
                {
                    Name        = name,
                    Builtin     = builtin,
                    Source      = _plugin.ResolveGamePresetSource(name, builtin),
                    Defaults    = defaults ?? new List<string>(),
                    DetailsText = BuildGameDetailsText(snap),
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
            // Cache the diff-baseline snapshot per game so we don't redo the
            // GameDefaults / Presets dictionary chase for every car preset.
            var baselineByGame = new Dictionary<string, GameSettingsSnapshot>(StringComparer.Ordinal);
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
                    string gameKey = entry.GameName ?? "";
                    if (!baselineByGame.TryGetValue(gameKey, out var baseline))
                    {
                        baseline = ResolveGameBaseline(gameKey);
                        baselineByGame[gameKey] = baseline;
                    }
                    string carDisplayName = entry.PresetName ?? "";
                    if (carDisplayName.EndsWith(TrueforcePlugin.BuiltinNameSuffix, StringComparison.Ordinal))
                        carDisplayName = carDisplayName.Substring(0, carDisplayName.Length - TrueforcePlugin.BuiltinNameSuffix.Length);
                    _carRows.Add(new CarRow
                    {
                        CarId       = carId,
                        PresetName  = entry.PresetName,
                        DisplayName = carDisplayName,
                        CarName     = ResolveCarNameForRow(entry.GameName, carId),
                        GameName    = entry.GameName,
                        Builtin     = entry.IsBuiltin,
                        Source      = _plugin.ResolveCarPresetSource(carId, entry.PresetName, entry.IsBuiltin,
                                          entry.PackName, entry.Author),
                        Active      = string.Equals(activeName, entry.PresetName, StringComparison.Ordinal),
                        DetailsText = BuildCarDetailsText(carId, entry, baseline),
                    });
                }
            }
            // Drop the active game filter if the chosen game is no longer
            // present, then rebuild the chip strip from the new row set.
            if (!string.IsNullOrEmpty(_carGameFilter)
                && !_carRows.Any(r => string.Equals(r.GameName ?? "", _carGameFilter, StringComparison.Ordinal)))
                _carGameFilter = null;
            RebuildCarGameChips();
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

        // ===================== Filter / search =====================

        // Game tab search: matches preset name + any of the "Default for"
        // game names. Empty string = pass-through.
        private bool GameRowFilter(object item)
        {
            if (string.IsNullOrEmpty(_gameSearch)) return true;
            var r = item as GameRow;
            if (r == null) return false;
            if (Contains(r.Name, _gameSearch)) return true;
            if (r.Defaults != null)
                foreach (var d in r.Defaults) if (Contains(d, _gameSearch)) return true;
            return false;
        }

        // Car tab: search matches car ID + preset name; game chips narrow by
        // GameName. Both compose (intersection).
        private bool CarRowFilter(object item)
        {
            var r = item as CarRow;
            if (r == null) return false;
            if (!string.IsNullOrEmpty(_carGameFilter)
                && !string.Equals(r.GameName ?? "", _carGameFilter, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrEmpty(_carSearch)) return true;
            return Contains(r.CarId, _carSearch) || Contains(r.PresetName, _carSearch);
        }

        private bool CustomRowFilter(object item)
        {
            if (string.IsNullOrEmpty(_customSearch)) return true;
            var r = item as CustomRow;
            return r != null && Contains(r.Name, _customSearch);
        }

        private static bool Contains(string haystack, string needle)
            => !string.IsNullOrEmpty(haystack)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _gameSearch = GameSearchBox?.Text?.Trim() ?? "";
            CollectionViewSource.GetDefaultView(_gameRows).Refresh();
        }

        private void CarSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _carSearch = CarSearchBox?.Text?.Trim() ?? "";
            CollectionViewSource.GetDefaultView(_carRows).Refresh();
        }

        private void CustomSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _customSearch = CustomSearchBox?.Text?.Trim() ?? "";
            CollectionViewSource.GetDefaultView(_customRows).Refresh();
        }

        // Walk the loaded car rows, derive the distinct game names, and build
        // a chip per game (+ an 'All' chip). Called from ReloadCars so the
        // chip strip reflects whatever's currently in the list. The chips are
        // RadioButtons in a shared GroupName so they're mutually exclusive.
        private void RebuildCarGameChips()
        {
            if (CarGameChips == null) return;
            CarGameChips.Children.Clear();
            _carGameAllChip = null;

            var games = _carRows
                .Select(r => r.GameName ?? "")
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Hide the strip when there's nothing useful to chip on (one or
            // zero games => 'All' is redundant).
            if (games.Count < 2)
            {
                _carGameFilter = null;
                CarGameChips.Visibility = Visibility.Collapsed;
                return;
            }
            CarGameChips.Visibility = Visibility.Visible;

            var all = new RadioButton
            {
                Content   = $"All ({_carRows.Count})",
                GroupName = "CarGameFilter",
                Style     = (Style)FindResource("ChipButton"),
                IsChecked = string.IsNullOrEmpty(_carGameFilter),
                Tag       = null,
            };
            all.Checked += CarGameChip_Checked;
            CarGameChips.Children.Add(all);
            _carGameAllChip = all;

            foreach (var g in games)
            {
                int count = _carRows.Count(r => string.Equals(r.GameName ?? "", g, StringComparison.Ordinal));
                var chip = new RadioButton
                {
                    Content   = $"{g} ({count})",
                    GroupName = "CarGameFilter",
                    Style     = (Style)FindResource("ChipButton"),
                    IsChecked = string.Equals(_carGameFilter, g, StringComparison.Ordinal),
                    Tag       = g,
                };
                chip.Checked += CarGameChip_Checked;
                CarGameChips.Children.Add(chip);
            }
        }

        private void CarGameChip_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            _carGameFilter = rb.Tag as string;   // null for All
            CollectionViewSource.GetDefaultView(_carRows).Refresh();
        }

        // ===================== Selection state =====================

        private GameRow SelectedGame   => GameList.SelectedItem as GameRow;
        private CarRow  SelectedCar    => CarList.SelectedItem  as CarRow;
        private CustomRow SelectedCustom => CustomList.SelectedItem as CustomRow;

        private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshGameButtons();
        }

        // Read-only summary of a game preset's contents (master gain, FFB,
        // and each effect's on/off + gain). Built once per row at ReloadGames
        // and bound by the expandable-row template's details area.
        private static string BuildGameDetailsText(GameSettingsSnapshot snap)
        {
            if (snap == null) return "";
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
            return sb.ToString().TrimEnd();
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

        // Per-car-preset summary: per-section, lists only the fields whose
        // override value differs from the baseline (the game's default
        // preset, resolved by ResolveGameBaseline). The baseline lets us
        // surface "what the user actually changed for this car" instead of
        // every field of every non-null override.
        //
        // If no baseline can be resolved (no game default, missing snapshot)
        // we fall back to dumping every field of the overridden section so
        // information isn't lost.
        private static string BuildCarDetailsText(string carId, CarPresetEntry entry, GameSettingsSnapshot baseline)
        {
            if (entry?.Override == null) return "";
            var ov = entry.Override;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Game: {(string.IsNullOrEmpty(entry.GameName) ? "(none)" : entry.GameName)}");
            sb.AppendLine($"Car ID: {carId}");
            sb.AppendLine($"Source: {(entry.IsBuiltin ? "Built-in" : "User preset")}");

            var sections = new System.Text.StringBuilder();
            AppendOverrideSection(sections, "Audio capture",    ov.AudioCapture, baseline?.AudioCapture);
            AppendOverrideSection(sections, "Engine pulse",     ov.EnginePulse,  baseline?.EnginePulse);
            AppendOverrideSection(sections, "Road bumps",       ov.RoadBumps,    baseline?.RoadBumps);
            AppendOverrideSection(sections, "Traction loss",    ov.TractionLoss, baseline?.TractionLoss);
            AppendOverrideSection(sections, "Gear shift",       ov.GearShift,    baseline?.GearShift);
            AppendOverrideSection(sections, "ABS",              ov.AbsClick,     baseline?.AbsClick);
            AppendOverrideSection(sections, "Pit limiter",      ov.PitLimiter,   baseline?.PitLimiter);
            AppendOverrideSection(sections, "DRS",              ov.Drs,          baseline?.Drs);
            AppendOverrideSection(sections, "Collision",        ov.Collision,    baseline?.Collision);
            AppendOverrideSection(sections, "Rev limiter",      ov.RevLimiter,   baseline?.RevLimiter);
            AppendOverrideSection(sections, "Airborne ducking", ov.Airborne,     baseline?.Airborne);

            sb.AppendLine();
            if (sections.Length > 0)
            {
                sb.AppendLine("Overrides:");
                sb.Append(sections);
            }
            else
            {
                sb.AppendLine("No section overrides (follows the game default).");
            }
            return sb.ToString().TrimEnd();
        }

        // Emit one section's worth of "field = value" lines covering only
        // fields whose override value differs from the baseline. If baseline
        // is null we can't diff, so list every field (better than hiding info).
        // No output if the section is null OR if nothing differs.
        private static void AppendOverrideSection(System.Text.StringBuilder sb, string label, object overrideSection, object baselineSection)
        {
            if (overrideSection == null) return;
            var lines = new List<string>();
            foreach (var prop in overrideSection.GetType().GetProperties(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                var ovValue = prop.GetValue(overrideSection);
                if (baselineSection != null)
                {
                    var baselineProp = baselineSection.GetType().GetProperty(prop.Name);
                    if (baselineProp != null)
                    {
                        var baselineValue = baselineProp.GetValue(baselineSection);
                        if (object.Equals(ovValue, baselineValue)) continue;
                    }
                }
                lines.Add($"  {PrettyFieldName(prop.Name)} = {FormatFieldValue(ovValue)}");
            }
            if (lines.Count == 0) return;
            sb.AppendLine(label + ":");
            foreach (var line in lines) sb.AppendLine(line);
        }

        // Insert spaces before each capital letter so PropertyNames render
        // as "Property Names". Acronyms left intact (e.g. "Rpm" -> "Rpm").
        private static string PrettyFieldName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length + 6);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Friendly value formatting for the tooltip. bool -> on/off, enums
        // print their name, floating point gets 2-decimal trim, strings stay
        // raw (quoted when empty so the absence is visible).
        private static string FormatFieldValue(object v)
        {
            if (v == null) return "(unset)";
            switch (v)
            {
                case bool   b: return b ? "on" : "off";
                case float  f: return f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                case double d: return d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                case int    i: return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case string s: return string.IsNullOrEmpty(s) ? "\"\"" : s;
                case Enum   e: return e.ToString();
                default:       return v.ToString();
            }
        }

        // Resolve the game-default snapshot for diffing per-car overrides:
        // first the binding in Settings.GameDefaults, then the user's active
        // preset, else null (fall through to dump-everything mode).
        private GameSettingsSnapshot ResolveGameBaseline(string gameName)
        {
            if (_plugin?.Settings?.Presets == null) return null;
            if (!string.IsNullOrEmpty(gameName)
                && _plugin.Settings.GameDefaults != null
                && _plugin.Settings.GameDefaults.TryGetValue(gameName, out var defName)
                && !string.IsNullOrEmpty(defName)
                && _plugin.Settings.Presets.TryGetValue(defName, out var snap)
                && snap != null)
                return snap;
            if (!string.IsNullOrEmpty(_plugin.ActivePresetName)
                && _plugin.Settings.Presets.TryGetValue(_plugin.ActivePresetName, out var active))
                return active;
            return null;
        }

        // ===================== Hover popup =====================

        // Track the row whose details are currently shown so we don't
        // rebuild + flicker the popup on every pixel of movement within
        // the same row.
        private object _hoveredRow;

        private void List_MouseMove(object sender, MouseEventArgs e)
        {
            var list = sender as DataGrid;
            if (list == null) { HideDetailsPopup(); return; }
            var row = FindRowUnderCursor(list, e);
            string text = (row as GameRow)?.DetailsText ?? (row as CarRow)?.DetailsText;
            if (string.IsNullOrEmpty(text)) { HideDetailsPopup(); return; }

            if (!object.ReferenceEquals(_hoveredRow, row))
            {
                _hoveredRow = row;
                DetailsPopupText.Text = text;
            }
            DetailsPopup.PlacementTarget = list;
            DetailsPopup.Placement = PlacementMode.Relative;
            var pos = e.GetPosition(list);
            DetailsPopup.HorizontalOffset = pos.X + 18;
            DetailsPopup.VerticalOffset   = pos.Y + 18;
            if (!DetailsPopup.IsOpen) DetailsPopup.IsOpen = true;
        }

        private void List_MouseLeave(object sender, MouseEventArgs e) => HideDetailsPopup();

        // Reload built-ins + user library from disk and refresh all three
        // tabs. Lets users drop files into the library folders (or edit
        // them outside SimHub) and pick the changes up without restarting
        // the plugin. Sort + column layout aren't reset; the row data
        // refills under the existing view.
        private void RefreshLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            try
            {
                _plugin.ReloadLibraryFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Trueforce] Refresh library failed: {ex.Message}");
                MessageBox.Show(Window.GetWindow(this),
                    "Refresh failed. Check the SimHub log for details.",
                    "Refresh library", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _initializing = true;
            try
            {
                ReloadGames();
                ReloadCars();
                ReloadCustoms();
            }
            finally { _initializing = false; }
        }

        // Keep the leftmost (select-all checkbox) column pinned at DisplayIndex 0
        // no matter what the user drags. CanUserReorder=False on the column +
        // FrozenColumnCount=1 on the DataGrid disallow dragging the checkbox
        // column itself, but the user could still drop ANOTHER column left of
        // it; snap it back here. Re-entrancy guard avoids the recursive
        // ColumnDisplayIndexChanged that our own DisplayIndex=0 write would
        // otherwise trigger.
        private bool _pinFirstAdjusting;
        private void List_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            var dg = sender as DataGrid;
            if (dg == null || dg.Columns.Count == 0) return;
            if (!_pinFirstAdjusting)
            {
                var first = dg.Columns[0];
                if (first.DisplayIndex != 0)
                {
                    _pinFirstAdjusting = true;
                    try { first.DisplayIndex = 0; }
                    finally { _pinFirstAdjusting = false; }
                }
            }
            // Persist after any user-driven reorder. Skipped during pin
            // snap-back (the second event in the pair will re-fire and
            // catch the final layout) and during hydration.
            PersistColumns(dg);
        }

        // Read every bound column's current DisplayIndex + Width and write
        // the snapshot back to settings. Skipped while hydrating to avoid
        // a write storm during the WPF layout pass that fires Width changes
        // for every column we touch.
        private bool _columnsHydrating;
        private void PersistColumns(DataGrid dg)
        {
            if (_columnsHydrating) return;
            if (_plugin?.Settings == null) return;
            var layout = new ManageColumnLayout();
            foreach (var col in dg.Columns)
            {
                if (!(col is DataGridBoundColumn bc) || !(bc.Binding is Binding b)) continue;
                var path = b.Path?.Path;
                if (string.IsNullOrEmpty(path)) continue;
                var w = col.Width;
                layout.Columns.Add(new ManageColumnState
                {
                    Key          = path,
                    DisplayIndex = col.DisplayIndex,
                    WidthValue   = w.IsAbsolute ? w.DisplayValue : w.Value,
                    WidthType    = w.UnitType.ToString()
                });
            }
            if      (dg == GameList)   _plugin.Settings.ManageGamesColumns   = layout;
            else if (dg == CarList)    _plugin.Settings.ManageCarsColumns    = layout;
            else if (dg == CustomList) _plugin.Settings.ManageCustomsColumns = layout;
            else return;
            _plugin.PersistSettings();
        }

        // Apply persisted widths first (layout pass), then DisplayIndex in
        // ascending order so columns slot into their saved positions without
        // colliding. Unknown binding paths (XAML renames) are ignored: the
        // user's saved entry simply doesn't bind to anything this session.
        private void HydrateColumnLayout(DataGrid dg, ManageColumnLayout layout)
        {
            if (layout?.Columns == null || layout.Columns.Count == 0) return;
            var byKey = new Dictionary<string, DataGridColumn>();
            foreach (var col in dg.Columns)
            {
                if (!(col is DataGridBoundColumn bc) || !(bc.Binding is Binding b)) continue;
                var path = b.Path?.Path;
                if (!string.IsNullOrEmpty(path)) byKey[path] = col;
            }
            _columnsHydrating = true;
            try
            {
                foreach (var saved in layout.Columns)
                {
                    if (string.IsNullOrEmpty(saved.Key)) continue;
                    if (!byKey.TryGetValue(saved.Key, out var col)) continue;
                    DataGridLengthUnitType unit;
                    switch (saved.WidthType)
                    {
                        case "Star":         unit = DataGridLengthUnitType.Star;         break;
                        case "Auto":         unit = DataGridLengthUnitType.Auto;         break;
                        case "SizeToCells":  unit = DataGridLengthUnitType.SizeToCells;  break;
                        case "SizeToHeader": unit = DataGridLengthUnitType.SizeToHeader; break;
                        default:             unit = DataGridLengthUnitType.Pixel;        break;
                    }
                    col.Width = new DataGridLength(saved.WidthValue, unit);
                }
                var ordered = layout.Columns
                    .Where(s => !string.IsNullOrEmpty(s.Key) && byKey.ContainsKey(s.Key))
                    .OrderBy(s => s.DisplayIndex)
                    .ToList();
                foreach (var saved in ordered)
                {
                    var col = byKey[saved.Key];
                    var target = saved.DisplayIndex;
                    if (target < 1) target = 1;                              // leave slot 0 for the pinned checkbox column
                    if (target > dg.Columns.Count - 1) target = dg.Columns.Count - 1;
                    if (col.DisplayIndex != target) col.DisplayIndex = target;
                }
            }
            finally { _columnsHydrating = false; }
        }

        private void HideDetailsPopup()
        {
            if (DetailsPopup != null && DetailsPopup.IsOpen) DetailsPopup.IsOpen = false;
            _hoveredRow = null;
        }

        // Walk the visual tree from the click's original source up to the
        // DataGridRow container (skipping checkbox / column cell content)
        // and return its DataContext (a PresetRowBase). Null if the cursor
        // is over header / scrollbar / blank list area.
        private static object FindRowUnderCursor(DataGrid list, MouseEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is DataGridRow item) return item.DataContext;
            }
            return null;
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

        // True while a header "select all" click is iterating its rows; the
        // per-row check handler skips re-syncing the header during that loop.
        private bool _bulkCheckInFlight;

        private void GameSelectAll_Click(object sender, RoutedEventArgs e)
            => HandleSelectAllClick(GameSelectAllCheck, GetVisible<GameRow>(_gameRows), RefreshGameButtons);

        private void CarSelectAll_Click(object sender, RoutedEventArgs e)
            => HandleSelectAllClick(CarSelectAllCheck, GetVisible<CarRow>(_carRows), RefreshCarButtons);

        private void CustomSelectAll_Click(object sender, RoutedEventArgs e)
            => HandleSelectAllClick(CustomSelectAllCheck, GetVisible<CustomRow>(_customRows), RefreshCustomButtons);

        // Iterate the filter-visible rows (so the header acts on what the
        // user can actually see) and stamp their IsChecked to match the
        // header. Guard rerun of UpdateSelectAllState() while doing it, then
        // run the row-button refresh once at the end.
        private void HandleSelectAllClick<T>(CheckBox header, IEnumerable<T> rows, Action refreshButtons)
            where T : PresetRowBase
        {
            if (header == null) return;
            bool target = header.IsChecked == true;
            try
            {
                _bulkCheckInFlight = true;
                foreach (var r in rows)
                    if (r.IsChecked != target) r.IsChecked = target;
            }
            finally { _bulkCheckInFlight = false; }
            refreshButtons?.Invoke();
        }

        // Walk the default CollectionView so the result respects the active
        // search / filter chip (header acts on visible rows only).
        private static IEnumerable<T> GetVisible<T>(System.Collections.IEnumerable source)
        {
            var view = CollectionViewSource.GetDefaultView(source);
            foreach (var item in view) if (item is T t) yield return t;
        }

        // Sync the header checkbox tri-state from how many visible rows are
        // checked: 0 -> false, all -> true, mixed -> null (indeterminate).
        private static void UpdateSelectAllHeader<T>(CheckBox header, IEnumerable<T> visibleRows)
            where T : PresetRowBase
        {
            if (header == null) return;
            int total = 0, on = 0;
            foreach (var r in visibleRows) { total++; if (r.IsChecked) on++; }
            // Header has IsThreeState=false; we still want indeterminate
            // visually -- setting IsChecked to null works for that even with
            // IsThreeState=false (it just blocks USER cycling through null).
            if (total == 0)       header.IsChecked = false;
            else if (on == 0)     header.IsChecked = false;
            else if (on == total) header.IsChecked = true;
            else                  header.IsChecked = null;
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
            // Promote works on the checked set when there is one (bulk), else
            // the highlighted row, so multi-select is supported, not blocked.
            if (GamePromoteBuiltinBtn != null)
            {
                bool promotable = _devMode && (checkedCount > 0 || anySelected);
                GamePromoteBuiltinBtn.IsEnabled = promotable;
                GamePromoteBuiltinBtn.Content   = checkedCount > 1
                    ? $"Set as built-in ({checkedCount})"
                    : "Set as built-in";
            }

            GameCheckedLabel.Text = checkedCount > 0
                ? $"{checkedCount} checked"
                : "";
            // Bulk delete labels: clue the user that the action applies to
            // the checked set, not the highlighted row.
            GameDeleteBtn.Content = checkedNonBuiltin > 0 ? $"Delete ({checkedNonBuiltin})" : "Delete";
            if (!_bulkCheckInFlight)
                UpdateSelectAllHeader(GameSelectAllCheck, GetVisible<GameRow>(_gameRows));
        }

        private void RefreshCarButtons()
        {
            var sel = SelectedCar;
            int   checkedCount      = _carRows.Count(r => r.IsChecked);
            int   checkedNonBuiltin = _carRows.Count(r => r.IsChecked && !r.Builtin);
            bool  anySelected       = sel != null;
            bool  selUserPreset     = anySelected && !sel.Builtin;

            // DEV authoring lets the owner rename + delete built-in car presets
            // too (the plugin write-throughs to the factory folder).
            bool carSelEditable = selUserPreset || (_devMode && anySelected);
            CarEditBtn.IsEnabled      = anySelected    && checkedCount <= 1;
            CarRenameBtn.IsEnabled    = carSelEditable && checkedCount <= 1;
            // Rename-car is per-car, not per-preset: anyone with a row
            // selected can name the car (built-in vs user, dev mode or
            // not). The CarFacts write is local to the user, the community
            // submission opts in via the Settings toggle.
            CarRenameCarBtn.IsEnabled = anySelected    && checkedCount <= 1;
            CarDuplicateBtn.IsEnabled = anySelected    && checkedCount <= 1;
            // Share-to-community: needs CommunityEnabled and a row. The
            // upload itself also gates on the backend URL + anon key being
            // present, but at the UI layer the toggle is the visible
            // contract.
            bool communityOn = _plugin?.Settings?.CommunityEnabled == true;
            CarShareBtn.IsEnabled = anySelected && checkedCount <= 1 && communityOn;
            CarShareBtn.ToolTip = communityOn
                ? "Upload this car preset to the community so other drivers can find it."
                : "Enable Community Contributions in Settings to share presets.";
            CarDeleteBtn.IsEnabled    = checkedNonBuiltin > 0 || carSelEditable;
            // Set-as-default: bulk supported (one row per car). Enabled if any
            // checked row isn't already its car's active default, else the
            // single-row rule (not already active).
            bool setDefaultable = checkedCount > 0
                ? _carRows.Any(r => r.IsChecked && !r.Active)
                : (anySelected && !sel.Active);
            CarSetActiveBtn.IsEnabled = setDefaultable;
            CarSetActiveBtn.Content   = checkedCount > 1
                ? $"Set as default ({checkedCount})"
                : "Set as default";
            // Same bulk-or-single rule as the game button.
            if (CarPromoteBuiltinBtn != null)
            {
                bool promotable = _devMode && (checkedCount > 0 || anySelected);
                CarPromoteBuiltinBtn.IsEnabled = promotable;
                CarPromoteBuiltinBtn.Content   = checkedCount > 1
                    ? $"Set as built-in ({checkedCount})"
                    : "Set as built-in";
            }

            CarCheckedLabel.Text = checkedCount > 0 ? $"{checkedCount} checked" : "";
            CarDeleteBtn.Content = checkedNonBuiltin > 0 ? $"Delete ({checkedNonBuiltin})" : "Delete";
            if (!_bulkCheckInFlight)
                UpdateSelectAllHeader(CarSelectAllCheck, GetVisible<CarRow>(_carRows));
        }

        private void RefreshCustomButtons()
        {
            int   checkedCount = _customRows.Count(r => r.IsChecked);
            bool  any          = SelectedCustom != null;

            CustomEditBtn.IsEnabled   = any && checkedCount <= 1;
            CustomDeleteBtn.IsEnabled = checkedCount > 0 || any;

            CustomCheckedLabel.Text = checkedCount > 0 ? $"{checkedCount} checked" : "";
            CustomDeleteBtn.Content = checkedCount > 0 ? $"Delete ({checkedCount})" : "Delete";
            if (!_bulkCheckInFlight)
                UpdateSelectAllHeader(CustomSelectAllCheck, GetVisible<CustomRow>(_customRows));
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

        private void ManagePacks_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsControl.RunManagePacksFlow(Window.GetWindow(this), _plugin))
            {
                // Removing or default-binding a pack rewrites game / car-default
                // bindings and may delete preset files; rebuild all three tabs.
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

        // Read the human-readable CarName fact for (game, carId) from
        // Settings.CarFacts. Used to populate the "Car name" column in the
        // car-presets grid. Empty string when no fact is on file so the
        // grid renders a blank cell instead of inventing a value.
        private string ResolveCarNameForRow(string game, string carId)
        {
            if (_plugin?.Settings?.CarFacts == null) return "";
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return "";
            string key = game + "/" + carId;
            return (_plugin.Settings.CarFacts.TryGetValue(key, out var bundle)
                    && bundle != null && !string.IsNullOrEmpty(bundle.CarName))
                ? bundle.CarName
                : "";
        }

        // Open the upload modal for the selected car preset. Reads the
        // on-disk CarOverride via the plugin's GetCarPresets helper (user
        // + factory built-ins both fair game), bundles any custom-engine
        // defs the override references, computes effect_tags from the
        // non-null section list, and hands off to PresetShareWindow. The
        // upload itself happens inside that modal so we can show progress
        // / error state inline.
        private async void CarShare_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null || _plugin == null) return;
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Enable Community Contributions in Settings to share presets.",
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Resolve the CarOverride from the preset library (user + builtins).
            var perCar = _plugin.GetCarPresets(sel.CarId);
            if (perCar == null
                || !perCar.TryGetValue(sel.PresetName, out var entry)
                || entry == null
                || entry.Override == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"Could not load preset '{sel.PresetName}' for car '{sel.CarId}'.",
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var customs = _plugin.CollectReferencedCustomEngines(
                null, new[] { entry.Override });

            // Serialize as JObject so the upload RPC sees pure JSON. The
            // server doesn't crack the body; receivers parse it back into
            // CarOverride + List<CustomEngineDef> on download.
            var body = new Newtonsoft.Json.Linq.JObject
            {
                ["override"] = Newtonsoft.Json.Linq.JToken.FromObject(entry.Override),
            };
            if (customs != null && customs.Count > 0)
                body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);

            // Effect tags from non-null sections on the override.
            var tags = new List<string>(8);
            if (entry.Override.EnginePulse  != null) tags.Add("engine");
            if (entry.Override.RevLimiter   != null) tags.Add("revlimiter");
            if (entry.Override.RoadBumps    != null) tags.Add("roadbumps");
            if (entry.Override.TractionLoss != null) tags.Add("tractionloss");
            if (entry.Override.GearShift    != null) tags.Add("gearshift");
            if (entry.Override.AbsClick     != null) tags.Add("abs");
            if (entry.Override.PitLimiter   != null) tags.Add("pitlimiter");
            if (entry.Override.Drs          != null) tags.Add("drs");
            if (entry.Override.Collision    != null) tags.Add("collision");
            if (entry.Override.AudioCapture != null) tags.Add("audio");
            if (entry.Override.Airborne     != null) tags.Add("airborne");

            string carDisplay = ResolveCarNameForRow(sel.GameName, sel.CarId);
            if (string.IsNullOrEmpty(carDisplay)) carDisplay = sel.CarId;

            var dialog = new PresetShareWindow(
                _plugin, sel.PresetName, sel.GameName ?? "",
                sel.CarId, carDisplay, body, tags)
            {
                Owner = Window.GetWindow(this),
            };
            bool? ok = dialog.ShowDialog();
            // The modal handles its own success/failure messaging; nothing
            // else needs to happen here on close. Keep the async signature
            // so future hooks (e.g. refresh after upload) have a place.
            await Task.CompletedTask;
        }

        // Per-car (not per-preset) rename: opens the styled
        // CarNameInputWindow, writes the result to the CarFacts bundle
        // (variant-blind, chassis-level), and fires the community
        // submission. Multiple presets for the same car share one name.
        // Operates on the SELECTED row's CarId.
        private void CarRenameCar_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null || _plugin == null) return;
            string game  = sel.GameName;
            string carId = sel.CarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return;

            string currentName = sel.CarName ?? "";
            var dialog = new CarNameInputWindow(carId, currentName)
            {
                Owner = Window.GetWindow(this),
            };
            bool? ok = dialog.ShowDialog();
            if (ok != true) return;
            string newName = dialog.EnteredName;
            if (string.IsNullOrEmpty(newName)) return;

            _plugin.WriteCarNameFact(game, carId, newName);
            _plugin.SubmitCarNameToCommunity(game, carId, newName);
            // Optimistically inject the new community name when the
            // edited car IS the active one - otherwise the resolver
            // would briefly attribute the change to whatever the prior
            // community consensus said.
            if (string.Equals(game, _plugin.ActiveGame, StringComparison.Ordinal)
                && string.Equals(carId, _plugin.ActiveCarId, StringComparison.Ordinal))
            {
                _plugin.NotifyCarNameConsensus(game, carId, new CarNameConsensus
                {
                    Name                  = newName,
                    SupportingSubmissions = 1,
                    Confirmations         = 0,
                    PayloadHash           = null,
                });
            }
            ReloadCars();
            SelectCarRow(sel.CarId, sel.PresetName);
        }

        private void CarRename_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null) return;
            // Built-in rename is DEV-only; the plugin write-throughs to factory.
            if (sel.Builtin && !_devMode) return;
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
            var checkedRows = _carRows.Where(r => r.IsChecked).ToList();
            if (checkedRows.Count == 0)
            {
                var sel = SelectedCar;
                if (sel == null || sel.Active) return;
                _plugin.SetCarDefaultPreset(sel.CarId, sel.PresetName);
                ReloadCars();
                SelectCarRow(sel.CarId, sel.PresetName);
                return;
            }

            // Bulk: only one default per car. Refuse if the checked set has
            // two rows for the same carId rather than silently picking one.
            var collisions = checkedRows
                .GroupBy(r => r.CarId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (collisions.Count > 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Each car can only have one default preset. The selection includes more than one row for:\n\n  " +
                        string.Join(", ", collisions) +
                        "\n\nUncheck the duplicates and try again.",
                    "Set as default", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int applied = 0, alreadyActive = 0;
            foreach (var r in checkedRows)
            {
                if (r.Active) { alreadyActive++; continue; }
                _plugin.SetCarDefaultPreset(r.CarId, r.PresetName);
                applied++;
            }
            ReloadCars();
            // Keep focus on the last row we just touched (if any), for context.
            var last = checkedRows.LastOrDefault(r => !r.Active) ?? checkedRows.Last();
            SelectCarRow(last.CarId, last.PresetName);
            if (applied + alreadyActive > 1)
            {
                string suffix = alreadyActive > 0 ? $" ({alreadyActive} were already the default)" : "";
                Window.GetWindow(this)?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show(Window.GetWindow(this),
                        $"Set {applied} preset(s) as their car's default{suffix}.",
                        "Set as default", MessageBoxButton.OK, MessageBoxImage.Information);
                }));
            }
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

        // ===================== Community browser =====================

        // Row in the Community list. Wraps a PresetSummary with the
        // formatting the grid binds against. MyVote and the description
        // ride along so the detail panel + Upvote/Downvote toggles can
        // reflect what the user already submitted in earlier sessions.
        private sealed class CommunityRow
        {
            public PresetSummary Summary { get; set; }
            public string Name        => Summary?.Name ?? "";
            public string Author      => string.IsNullOrEmpty(Summary?.Author) ? "(anonymous)" : Summary.Author;
            public string VoteLabel   => $"{Summary?.Upvotes ?? 0} / {Summary?.Downvotes ?? 0}";
            public int    Downloads   => Summary?.Downloads ?? 0;
            public string TagsLabel   => Summary?.EffectTags == null || Summary.EffectTags.Count == 0
                                          ? "" : string.Join(", ", Summary.EffectTags);
            public string Description => Summary?.Description ?? "";
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<CommunityRow> _communityRows =
            new System.Collections.ObjectModel.ObservableCollection<CommunityRow>();
        private bool _communityFetchInFlight;
        private string _communitySort = "wilson";
        // Tracks which (game, carId) the displayed list represents so we
        // can repopulate when the active car changes.
        private string _communityListedCarKey;

        private CommunityRow SelectedCommunity =>
            CommunityList?.SelectedItem as CommunityRow;

        // Called from the host (SettingsControl) when active car changes
        // so the community panel can show "Showing presets for: X" with
        // the live name + refresh when visible.
        public void OnActiveCarChanged()
        {
            UpdateCommunityActiveCarLabel();
            // If the user is currently looking at the Community panel,
            // immediately refresh so the list reflects the new car.
            if (CommunityPanel != null && CommunityPanel.Visibility == Visibility.Visible
                && _plugin != null)
                _ = CommunityRefreshAsync();
        }

        private void UpdateCommunityActiveCarLabel()
        {
            if (CommunityCarLabel == null || _plugin == null) return;
            string carId = _plugin.ActiveCarId;
            string display = _plugin.ActiveCarDisplayName;
            if (string.IsNullOrEmpty(carId))
                CommunityCarLabel.Text = "(none - load a car in the game first)";
            else
                CommunityCarLabel.Text = string.IsNullOrEmpty(display) || display == carId
                                         ? carId
                                         : $"{display}   {carId}";
        }

        private void CommunitySort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CommunitySortCombo == null) return;
            int idx = CommunitySortCombo.SelectedIndex;
            string newSort = idx == 1 ? "newest" : idx == 2 ? "downloads" : "wilson";
            if (newSort == _communitySort) return;
            _communitySort = newSort;
            if (CommunityPanel?.Visibility == Visibility.Visible)
                _ = CommunityRefreshAsync();
        }

        private void CommunityRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = CommunityRefreshAsync();
        }

        private async Task CommunityRefreshAsync()
        {
            if (_plugin == null || _communityFetchInFlight) return;
            UpdateCommunityActiveCarLabel();
            string game  = _plugin.ActiveGame;
            string carId = _plugin.ActiveCarId;
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId))
            {
                _communityRows.Clear();
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Load a car in the game to see community presets.";
                CommunityList_SelectionChanged(null, null);
                return;
            }
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                _communityRows.Clear();
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Community Contributions is off (toggle it in Settings).";
                CommunityList_SelectionChanged(null, null);
                return;
            }

            _communityFetchInFlight = true;
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Loading...";

            string capturedGame = game;
            string capturedCar  = carId;
            string capturedSort = _communitySort;
            List<PresetSummary> results = null;
            try
            {
                results = await Task.Run(() =>
                    _plugin.FetchCommunityPresetsForCar(capturedGame, capturedCar, capturedSort, 50));
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Fetch failed: " + ex.Message;
                _communityFetchInFlight = false;
                return;
            }
            _communityFetchInFlight = false;

            // If the user switched cars while the fetch was in flight,
            // drop the stale result rather than render the wrong list.
            if (_plugin.ActiveGame != capturedGame || _plugin.ActiveCarId != capturedCar)
                return;

            _communityListedCarKey = capturedGame + "/" + capturedCar;
            _communityRows.Clear();
            if (results == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Could not reach the community backend.";
                CommunityList_SelectionChanged(null, null);
                return;
            }
            foreach (var s in results)
                _communityRows.Add(new CommunityRow { Summary = s });
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = _communityRows.Count == 0
                    ? "No community presets for this car yet. Be the first to share."
                    : $"{_communityRows.Count} preset(s) found.";
            CommunityList_SelectionChanged(null, null);
        }

        private void CommunityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = SelectedCommunity;
            bool has = sel != null && sel.Summary != null;
            if (CommunityDescriptionText != null)
                CommunityDescriptionText.Text = has && !string.IsNullOrWhiteSpace(sel.Summary.Description)
                    ? sel.Summary.Description
                    : "";
            if (CommunityVoteUpBtn   != null) CommunityVoteUpBtn.IsEnabled   = has;
            if (CommunityVoteDownBtn != null) CommunityVoteDownBtn.IsEnabled = has;
            if (CommunityReportBtn   != null) CommunityReportBtn.IsEnabled   = has;
            if (CommunityDownloadBtn != null) CommunityDownloadBtn.IsEnabled = has;
        }

        private void CommunityVoteUp_Click(object sender, RoutedEventArgs e)
        {
            CommunityVote(+1);
        }

        private void CommunityVoteDown_Click(object sender, RoutedEventArgs e)
        {
            CommunityVote(-1);
        }

        private void CommunityVote(int value)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            _plugin.VoteCommunityPreset(sel.Summary.Id, value);
            // Optimistic counter update; server is authoritative on the
            // next refresh (the Wilson recompute might disagree if the
            // user previously voted the other way).
            if (value > 0) sel.Summary.Upvotes   += 1;
            else            sel.Summary.Downvotes += 1;
            int idx = _communityRows.IndexOf(sel);
            if (idx >= 0)
            {
                _communityRows.RemoveAt(idx);
                _communityRows.Insert(idx, sel);
                CommunityList.SelectedIndex = idx;
            }
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = value > 0 ? "Upvote recorded." : "Downvote recorded.";
        }

        private void CommunityReport_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"Report '{sel.Summary.Name}' for moderator review?",
                "Report preset", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            _plugin.ReportCommunityPreset(sel.Summary.Id);
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Reported. Thanks for flagging.";
        }

        private async void CommunityDownload_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Downloading...";

            string capturedId = sel.Summary.Id;
            PresetFull full = null;
            try
            {
                full = await Task.Run(() => _plugin.FetchCommunityPresetBody(capturedId));
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Download failed: " + ex.Message;
                return;
            }
            if (full?.Body == null || full.Summary == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Download returned no body.";
                return;
            }

            // The body JSON shape (set by PresetShareWindow): { override: {...}, custom_engines: [...] }
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
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Body parse failed: " + ex.Message;
                return;
            }
            if (ovr == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Body had no override section.";
                return;
            }

            // Section-picker: list non-null sections, default to all
            // checked; only checked sections get applied. Picker lives
            // on the import path (CarOverride section selection) - we
            // build it inline here as a small dialog.
            var picker = new CommunitySectionPickerWindow(
                full.Summary.Name, full.Summary.Author, ovr)
            {
                Owner = Window.GetWindow(this),
            };
            bool? pickerOk = picker.ShowDialog();
            if (pickerOk != true) return;
            var chosen = picker.ChosenSections;

            // Apply: zero out non-chosen sections, save as a new user
            // preset for the active car (or for full.Summary.CarId if
            // distinct - we trust the user's active car for now).
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
            if (chosen.Contains("audio"))        apply.AudioCapture = ovr.AudioCapture;
            if (chosen.Contains("airborne"))     apply.Airborne     = ovr.Airborne;

            // Merge any referenced custom engines into the user's
            // library so the imported EnginePulse.CustomEngineId
            // resolves (same shape as the file-based import path).
            if (customs != null && customs.Count > 0)
                _plugin.ImportCommunityCustomEngines(customs);

            // Save the preset under a community-distinct name. Append
            // " (community)" so the preset manager row makes the
            // source clear. If a same-named preset already exists for
            // this car, append a numeric suffix.
            string baseName = full.Summary.Name + " (community)";
            string presetName = baseName;
            int n = 2;
            var existing = _plugin.GetCarPresets(_plugin.ActiveCarId);
            while (existing != null && existing.ContainsKey(presetName))
            {
                presetName = baseName + " " + n;
                n++;
            }
            _plugin.SaveImportedCommunityCarPreset(
                _plugin.ActiveCarId, presetName, _plugin.ActiveGame, apply,
                full.Summary.Author, full.Summary.Description);
            _plugin.RecordCommunityPresetDownload(capturedId);

            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text =
                    $"Saved as '{presetName}'. Open it on the Car presets tab to use.";
            ReloadCars();
            LibraryChanged?.Invoke();
        }
    }
}
