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

        // Raised after a successful community-list refresh for the
        // car-kind for-car view. Lets the host (SettingsControl) drop
        // the active-card top-community cache so its dropdown picks up
        // any new community presets without waiting for a plugin
        // restart. Library is not changing, so LibraryChanged would be
        // the wrong signal; this is a separate event.
        public event Action CarCommunityListRefreshed;

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
                if (DevModeBanner != null)
                    DevModeBanner.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
            // XAML's IsChecked="True" on the default segment doesn't fire
            // Checked during construction, so force the segment + mode-wrap
            // visibility now or the Library/Community sub-pill stays hidden
            // until the user first clicks a segment pill.
            Segment_Checked(null, null);
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

        /// <summary>Open the Cars segment and flip the inline Library|
        /// Community sub-pill to Community. Used by the header
        /// "See all" affordance off the active-car community count
        /// chip so the user lands directly on the browse view.</summary>
        public void OpenCarCommunity()
        {
            if (SegCar != null) SegCar.IsChecked = true;
            if (CarModeCommunity != null) CarModeCommunity.IsChecked = true;
        }

        // Segmented selector clicked (or set programmatically). Show the chosen
        // view, hide the others. Community is no longer a separate top
        // segment; it's inlined under Cars (and later Games) via the
        // Mine|Community sub-pill. Guarded so it's a no-op during the
        // XAML load pass.
        private void Segment_Checked(object sender, RoutedEventArgs e)
        {
            if (GamePanel == null || CarPanel == null || CustomPanel == null
                || CommunityPanel == null) return;

            bool carActive    = SegCar.IsChecked    == true;
            bool gameActive   = SegGame.IsChecked   == true;
            bool customActive = SegCustom.IsChecked == true;
            bool packsActive  = SegPacks?.IsChecked == true;

            // Sub-pills appear alongside their owning top segment only.
            if (CarModeWrap    != null) CarModeWrap.Visibility    = carActive    ? Visibility.Visible : Visibility.Collapsed;
            if (GameModeWrap   != null) GameModeWrap.Visibility   = gameActive   ? Visibility.Visible : Visibility.Collapsed;
            if (CustomModeWrap != null) CustomModeWrap.Visibility = customActive ? Visibility.Visible : Visibility.Collapsed;
            if (PacksModeWrap  != null) PacksModeWrap.Visibility  = packsActive  ? Visibility.Visible : Visibility.Collapsed;

            if (carActive)
            {
                GamePanel.Visibility = Visibility.Collapsed;
                CustomPanel.Visibility = Visibility.Collapsed;
                if (PacksPanel != null) PacksPanel.Visibility = Visibility.Collapsed;
                CarMode_Checked(null, null);
                return;
            }
            if (gameActive)
            {
                CarPanel.Visibility = Visibility.Collapsed;
                CustomPanel.Visibility = Visibility.Collapsed;
                if (PacksPanel != null) PacksPanel.Visibility = Visibility.Collapsed;
                GameMode_Checked(null, null);
                return;
            }
            if (customActive)
            {
                GamePanel.Visibility = Visibility.Collapsed;
                CarPanel.Visibility  = Visibility.Collapsed;
                if (PacksPanel != null) PacksPanel.Visibility = Visibility.Collapsed;
                CustomMode_Checked(null, null);
                return;
            }
            if (packsActive)
            {
                GamePanel.Visibility   = Visibility.Collapsed;
                CarPanel.Visibility    = Visibility.Collapsed;
                CustomPanel.Visibility = Visibility.Collapsed;
                PacksMode_Checked(null, null);
                return;
            }
            // No segment matched (shouldn't happen; defensive).
            GamePanel.Visibility      = Visibility.Collapsed;
            CarPanel.Visibility       = Visibility.Collapsed;
            CustomPanel.Visibility    = Visibility.Collapsed;
            if (PacksPanel != null) PacksPanel.Visibility = Visibility.Collapsed;
            CommunityPanel.Visibility = Visibility.Collapsed;
        }

        // Inline Library|Community sub-pill on the Packs segment.
        // Library shows a compact intro panel that defers to the
        // existing PackManagerWindow ("Open installed packs..."); the
        // full install / set-as-default / remove flow already lives
        // there. Community swaps in the shared CommunityPanel re-scoped
        // to pack bundles.
        private void PacksMode_Checked(object sender, RoutedEventArgs e)
        {
            if (PacksPanel == null || CommunityPanel == null) return;
            if (SegPacks?.IsChecked != true) return;

            bool community = PacksModeCommunity?.IsChecked == true;
            PacksPanel.Visibility     = community ? Visibility.Collapsed : Visibility.Visible;
            CommunityPanel.Visibility = community ? Visibility.Visible   : Visibility.Collapsed;
            if (community)
            {
                bool kindSwitched = _communityKind != "pack";
                _communityKind = "pack";
                // Pack kind is always global; clear any leftover
                // trending state from a prior car/game fetch so the
                // scope radio label baselines cleanly.
                _lastFetchWasTrending = false;
                RelabelCommunityScopeRadio();
                if ((kindSwitched || _communityRows.Count == 0) && !_communityFetchInFlight)
                    _ = CommunityRefreshAsync();
            }
            else
            {
                // Library mode: refresh the inline installed-packs grid
                // every time the user lands here so renames / removes
                // via the older PackManagerWindow flow stay in sync.
                ReloadPacks();
            }
        }

        // Reentrancy guard for the share/pack handlers. Each handler is
        // async void with an EnsureUsernameBeforeShareAsync await before
        // ShowDialog, so a rapid double-click could otherwise start a
        // second handler before the first reaches its modal. We flip
        // this on entry, off in finally.
        private bool _shareInProgress;

        // Open the CreatePackWindow so the user can bundle eligible
        // entries from their library + upload as a community pack.
        // Permission-eligible = not built-in, AND (user-owned OR
        // community-sourced with AllowInPacks=true).
        private async void CreatePack_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if (_shareInProgress) return;
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Enable Community Contributions in Settings to share packs.",
                    "Create pack", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _shareInProgress = true;
            try
            {
                var owner = Window.GetWindow(this);
                // Username gate: signed-in users who never set a username
                // would otherwise see a raw backend "set a username first"
                // error from the eventual upload. Prompt now; abort cleanly
                // on cancel.
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    MessageBox.Show(owner,
                        "Pick a username before sharing (Settings > Account & community).",
                        "Create pack", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dlg = new CreatePackWindow(_plugin) { Owner = owner };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[Trueforce] Create pack failed: " + ex.Message);
                MessageBox.Show(Window.GetWindow(this),
                    "Create pack failed: " + ex.Message,
                    "Create pack", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shareInProgress = false; }
        }

        // Funnel entry from the My Uploads banner in the Community panel.
        // Same flow as the Packs-tab "Create a pack" button - kept as a
        // separate handler so changes to one don't surprise the other.
        // Calls CreatePack_Click as fire-and-forget; the async gate +
        // modal sequence still runs correctly.
        private void CreatePackFromUploads_Click(object sender, RoutedEventArgs e)
        {
            CreatePack_Click(sender, e);
        }

        // Toggle the My-Uploads pack-authoring banner. Visible only when
        // (a) Community is enabled, (b) the panel is showing My Uploads
        // mode, and (c) at least one upload is listed. Called from
        // CommunityRefreshAsync after rows populate and from
        // CommunityMode_Changed when the user flips modes.
        private void UpdateCreatePackFromUploadsBannerVisibility()
        {
            if (CreatePackFromUploadsBanner == null) return;
            bool show =
                _plugin?.Settings?.CommunityEnabled == true
                && _communityMode == "mine"
                && _communityRows.Count > 0;
            var want = show ? Visibility.Visible : Visibility.Collapsed;
            if (CreatePackFromUploadsBanner.Visibility != want)
                CreatePackFromUploadsBanner.Visibility = want;
        }

        // ---- Installed packs grid (Library mode) -------------------------

        public sealed class PackRow
        {
            internal InstalledPack Pack;
            public string Name          { get; set; }
            public string Author        { get; set; }
            public string Version       { get; set; }
            public int    EntryCount    { get; set; }
            public string ImportedLabel { get; set; }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<PackRow> _packRows
            = new System.Collections.ObjectModel.ObservableCollection<PackRow>();
        private bool _packsListInitialized;

        private void EnsurePacksListBound()
        {
            if (_packsListInitialized || PacksList == null) return;
            PacksList.ItemsSource = _packRows;
            _packsListInitialized = true;
        }

        private void ReloadPacks()
        {
            if (PacksList == null) return;
            EnsurePacksListBound();
            _packRows.Clear();
            var file = _plugin?.LoadInstalledPacks();
            if (file?.Packs == null) return;
            foreach (var p in file.Packs)
            {
                if (p == null) continue;
                _packRows.Add(new PackRow
                {
                    Pack          = p,
                    Name          = p.PackName ?? "(unnamed)",
                    Author        = p.Author ?? "",
                    Version       = p.AuthorVersion ?? "",
                    EntryCount    = p.Entries?.Count ?? 0,
                    ImportedLabel = p.ImportedAt == default(DateTime)
                                      ? ""
                                      : p.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                });
            }
            // Selection may now point at a removed pack; clear + re-eval
            // the action buttons.
            PacksList_SelectionChanged(null, null);
        }

        private void PacksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool any = PacksList?.SelectedItem is PackRow;
            if (PacksSetDefaultsBtn != null) PacksSetDefaultsBtn.IsEnabled = any;
            if (PacksRemoveBtn      != null) PacksRemoveBtn.IsEnabled      = any;
        }

        private void PacksSetDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (!(PacksList?.SelectedItem is PackRow row) || row.Pack == null || _plugin == null) return;
            var summary = _plugin.SetPackAsDefaults(row.Pack);
            if (summary == null) return;
            int total = summary.GameDefaultsSet + summary.CarDefaultsSet
                + summary.GameDefaultsOverwritten + summary.CarDefaultsOverwritten;
            string copy = total == 0
                ? "No default bindings changed."
                : $"Set {summary.GameDefaultsSet + summary.GameDefaultsOverwritten} game default(s) and "
                  + $"{summary.CarDefaultsSet + summary.CarDefaultsOverwritten} car default(s).";
            if (summary.GamePresetsSkipped > 0)
                copy += $"\nSkipped {summary.GamePresetsSkipped} game preset(s) the pack didn't tag with a target game.";
            MessageBox.Show(Window.GetWindow(this), copy,
                "Set pack as defaults", MessageBoxButton.OK, MessageBoxImage.Information);
            LibraryChanged?.Invoke();
        }

        private void PacksRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!(PacksList?.SelectedItem is PackRow row) || row.Pack == null || _plugin == null) return;
            if (MessageBox.Show(Window.GetWindow(this),
                $"Remove pack '{row.Name}'?\n\n"
                + "Every preset the pack installed will be deleted, except entries you've edited (those are preserved).",
                "Remove pack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var summary = _plugin.RemovePack(row.Pack);
            if (summary == null) return;
            MessageBox.Show(Window.GetWindow(this),
                $"Removed pack. Deleted {summary.EntriesDeleted} entr{(summary.EntriesDeleted == 1 ? "y" : "ies")}; "
                + $"kept {summary.EntriesKept} edited entr{(summary.EntriesKept == 1 ? "y" : "ies")}.",
                "Remove pack", MessageBoxButton.OK, MessageBoxImage.Information);
            ReloadPacks();
            ReloadGames();
            ReloadCars();
            ReloadCustoms();
            LibraryChanged?.Invoke();
        }

        // Inline Library|Community sub-pill on the Custom engines
        // segment. Library shows the local CustomPanel; Community
        // swaps in the shared CommunityPanel re-scoped to engines
        // (no game/car filter; engines are global).
        private void CustomMode_Checked(object sender, RoutedEventArgs e)
        {
            if (CustomPanel == null || CommunityPanel == null) return;
            if (SegCustom?.IsChecked != true) return;

            bool community = CustomModeCommunity?.IsChecked == true;
            CustomPanel.Visibility    = community ? Visibility.Collapsed : Visibility.Visible;
            CommunityPanel.Visibility = community ? Visibility.Visible   : Visibility.Collapsed;
            if (community)
            {
                bool kindSwitched = _communityKind != "engine";
                _communityKind = "engine";
                // Engine kind is always global; clear any leftover
                // trending state so the scope radio baselines cleanly.
                _lastFetchWasTrending = false;
                RelabelCommunityScopeRadio();
                if ((kindSwitched || _communityRows.Count == 0) && !_communityFetchInFlight)
                    _ = CommunityRefreshAsync();
            }
        }

        // Inline Library|Community sub-pill on the Cars segment.
        // Library shows the local CarPanel; Community swaps in
        // CommunityPanel scoped to the active car (lazy-loaded on
        // first reveal).
        private void CarMode_Checked(object sender, RoutedEventArgs e)
        {
            if (CarPanel == null || CommunityPanel == null) return;
            if (SegCar?.IsChecked != true) return;  // pill is hidden outside Cars

            bool community = CarModeCommunity?.IsChecked == true;
            CarPanel.Visibility       = community ? Visibility.Collapsed : Visibility.Visible;
            CommunityPanel.Visibility = community ? Visibility.Visible   : Visibility.Collapsed;
            if (community)
            {
                // Flip the kind so CommunityRefreshAsync hits the car
                // preset RPC. Refresh if the cached list is stale (kind
                // mismatch) or empty.
                bool kindSwitched = _communityKind != "car";
                _communityKind = "car";
                // Reset the trending flag so the scope radio doesn't
                // briefly mislabel using state from the prior kind's
                // last fetch; the post-fetch RelabelCommunityScopeRadio
                // will rewrite it from the new fetch's outcome.
                _lastFetchWasTrending = false;
                RelabelCommunityScopeRadio();
                if ((kindSwitched || _communityRows.Count == 0) && !_communityFetchInFlight)
                    _ = CommunityRefreshAsync();
            }
        }

        // Inline Library|Community sub-pill on the Games segment.
        // Library shows the local GamePanel; Community swaps in the
        // shared CommunityPanel re-scoped to game presets for the
        // active game (lazy-loaded on first reveal).
        private void GameMode_Checked(object sender, RoutedEventArgs e)
        {
            if (GamePanel == null || CommunityPanel == null) return;
            if (SegGame?.IsChecked != true) return;

            bool community = GameModeCommunity?.IsChecked == true;
            GamePanel.Visibility      = community ? Visibility.Collapsed : Visibility.Visible;
            CommunityPanel.Visibility = community ? Visibility.Visible   : Visibility.Collapsed;
            if (community)
            {
                bool kindSwitched = _communityKind != "game";
                _communityKind = "game";
                // Reset the trending flag so a prior car-kind trending
                // fetch doesn't bleed through into the new game-kind
                // scope label; the post-fetch RelabelCommunityScopeRadio
                // will rewrite it from the new fetch's outcome.
                _lastFetchWasTrending = false;
                RelabelCommunityScopeRadio();
                if ((kindSwitched || _communityRows.Count == 0) && !_communityFetchInFlight)
                    _ = CommunityRefreshAsync();
            }
        }

        // Keep the "For this car / For this game / All engines" radio
        // label in sync with the active kind. The Mine radio's copy
        // is kind-neutral ("My uploads") since FetchMyPresets returns
        // all three kinds in one set, each row tagged with Summary.Kind.
        private void RelabelCommunityScopeRadio()
        {
            if (CommunityModeForCar == null) return;
            // When the last completed fetch fell back to trending (no
            // active game/car), reflect that in the scope radio's label
            // so the user understands why they see cross-game results.
            bool trending = _lastFetchWasTrending && _communityMode == "for-car";
            switch (_communityKind)
            {
                case "game":   CommunityModeForCar.Content = trending ? "Trending games" : "For this game"; break;
                case "engine": CommunityModeForCar.Content = "All engines";   break;
                case "pack":   CommunityModeForCar.Content = "All packs";     break;
                default:       CommunityModeForCar.Content = trending ? "Trending cars" : "For this car";  break;
            }
            if (CommunityHelpText != null)
            {
                // "Mine" mode gets its own help text that surfaces the
                // Edit/Delete affordance (those buttons are collapsed
                // until a row you own is selected, which is otherwise
                // hard to discover).
                if (_communityMode == "mine")
                {
                    CommunityHelpText.Text =
                        "Your community uploads across every game and car. Select a row to reveal Edit and Delete; "
                        + "use Edit to update a preset's name, description, or body without resetting its votes and downloads.";
                }
                else switch (_communityKind)
                {
                    case "game":
                        CommunityHelpText.Text = trending
                            ? "No game loaded, so showing trending game presets from across the community. Load a game and refresh to see scoped results."
                            : "Browse + download community game presets for the game you're playing, or switch to your own uploads to manage them. "
                              + "Pick a row and Download to import; you'll get a section picker so you can take just the parts you want.";
                        break;
                    case "engine":
                        CommunityHelpText.Text =
                            "Browse + download community custom engines (cylinder patterns + layout), or switch to your own uploads to manage them. "
                            + "Pick a row and Download to add it to your library.";
                        break;
                    case "pack":
                        CommunityHelpText.Text =
                            "Browse + download community packs (bundles of game presets, car presets, and custom engines), or switch to your own uploads to manage them. "
                            + "Pick a row and Download to import every entry into the matching part of your library.";
                        break;
                    default:
                        CommunityHelpText.Text = trending
                            ? "No car loaded, so showing trending car presets from across the community. Load a car in your game and refresh to see scoped results."
                            : "Browse + download community presets for the car you're driving, or switch to your own uploads to manage them. "
                              + "Pick a row and Download to import; you'll get a section picker so you can take just the parts you want.";
                        break;
                }
            }
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
            if (GameListEmpty != null)
                GameListEmpty.Visibility = _gameRows.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
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
            if (CarListEmpty != null)
                CarListEmpty.Visibility = _carRows.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
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
            if (CustomListEmpty != null)
                CustomListEmpty.Visibility = _customRows.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
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
            if (GameShareBtn != null)
            {
                // Share gating mirrors CarShareBtn: community on + signed in
                // + single row + non-built-in. Server rejects re-uploads of
                // built-ins regardless, but disabling here makes the contract
                // visible up front rather than via a backend error.
                bool gShareCommunityOn = _plugin?.Settings?.CommunityEnabled == true;
                bool gShareSignedIn    = _plugin?.AuthIsSignedIn == true;
                GameShareBtn.IsEnabled = anySelected && checkedCount <= 1
                    && !sel.Builtin && gShareCommunityOn && gShareSignedIn;
                if (!gShareCommunityOn)
                    GameShareBtn.ToolTip = "Enable Community Contributions in Settings to share presets.";
                else if (!gShareSignedIn)
                    GameShareBtn.ToolTip = "Sign in (Account & community in Settings) to share presets.";
                else if (anySelected && sel.Builtin)
                    GameShareBtn.ToolTip = "Built-in presets ship with the plugin. Duplicate it to make your own version, then share that.";
                else
                    GameShareBtn.ToolTip = "Upload this game preset to the community so other drivers can find it.";
            }
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
            // Share-to-community: needs CommunityEnabled, a signed-in
            // session (server now requires it), and a row. The upload
            // itself also checks the backend URL + anon key; at the UI
            // layer the toggle + sign-in are the visible contract.
            // Additionally: presets the user downloaded from someone
            // else's community upload aren't shareable as their own --
            // gate by Override.CommunitySourceId (same identity model
            // as the header-bar Share buttons).
            bool communityOn = _plugin?.Settings?.CommunityEnabled == true;
            bool signedIn    = _plugin?.AuthIsSignedIn == true;
            bool isBuiltinSel = sel?.Builtin == true;
            // Resolve the selected row's CarOverride via the per-car
            // store so we can read its CommunitySourceId stamp.
            bool isCommunitySourced = false;
            if (anySelected && checkedCount <= 1 && sel != null
                && !string.IsNullOrEmpty(sel.CarId)
                && !string.IsNullOrEmpty(sel.PresetName))
            {
                var perCar = _plugin?.GetCarPresets(sel.CarId);
                if (perCar != null && perCar.TryGetValue(sel.PresetName, out var entry)
                    && entry?.Override != null
                    && !string.IsNullOrEmpty(entry.Override.CommunitySourceId))
                    isCommunitySourced = true;
            }
            CarShareBtn.IsEnabled = anySelected && checkedCount <= 1
                && communityOn && signedIn && !isCommunitySourced && !isBuiltinSel;
            if (!communityOn)
                CarShareBtn.ToolTip = "Enable Community Contributions in Settings to share presets.";
            else if (!signedIn)
                CarShareBtn.ToolTip = "Sign in (Account & community in Settings) to share presets.";
            else if (isBuiltinSel)
                CarShareBtn.ToolTip = "Built-in presets ship with the plugin -- no need to re-share.";
            else if (isCommunitySourced)
                CarShareBtn.ToolTip = "Shared by another driver. Duplicate to make your own version and share that.";
            else
                CarShareBtn.ToolTip = "Upload this car preset to the community so other drivers can find it.";
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
            // Share is single-row, requires community on. The button
            // itself stays in the DOM; just enable when actionable.
            if (CustomShareBtn != null)
                CustomShareBtn.IsEnabled = any && checkedCount <= 1
                    && _plugin?.Settings?.CommunityEnabled == true;
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

        // Share a game preset selected in the manager. Mirrors
        // HeaderGameShare_Click in SettingsControl, but reads the snapshot
        // from the selected row rather than the active preset, so a user
        // can share any saved preset without first making it active.
        private async void GameShare_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null || _plugin == null) return;
            if (_shareInProgress) return;
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Enable Community Contributions in Settings to share presets.",
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _shareInProgress = true;
            try
            {
                if (_plugin.Settings?.Presets == null
                    || !_plugin.Settings.Presets.TryGetValue(sel.Name, out var snap)
                    || snap == null)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        $"Could not load game preset '{sel.Name}'.",
                        "Share preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    MessageBox.Show(owner,
                        "Pick a username before sharing (Settings > Account & community).",
                        "Share preset", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var body = new Newtonsoft.Json.Linq.JObject
                {
                    ["snapshot"] = Newtonsoft.Json.Linq.JToken.FromObject(snap),
                };
                var customs = _plugin.CollectReferencedCustomEngines(
                    new[] { snap }, null);
                if (customs != null && customs.Count > 0)
                    body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);

                var tags = new List<string>(11);
                if (snap.EnginePulse  != null) tags.Add("engine");
                if (snap.RevLimiter   != null) tags.Add("revlimiter");
                if (snap.RoadBumps    != null) tags.Add("roadbumps");
                if (snap.TractionLoss != null) tags.Add("tractionloss");
                if (snap.GearShift    != null) tags.Add("gearshift");
                if (snap.AbsClick     != null) tags.Add("abs");
                if (snap.PitLimiter   != null) tags.Add("pitlimiter");
                if (snap.Drs          != null) tags.Add("drs");
                if (snap.Collision    != null) tags.Add("collision");
                if (snap.AudioCapture != null) tags.Add("audio");
                if (snap.Airborne     != null) tags.Add("airborne");

                string game = _plugin.ActiveGame ?? "";
                var dialog = PresetShareWindow.ForGame(_plugin, sel.Name, game, body, tags);
                dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[Trueforce] Share game preset failed: " + ex.Message);
                MessageBox.Show(Window.GetWindow(this),
                    "Share preset failed: " + ex.Message,
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shareInProgress = false; }
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

        // Multi-select default-binding editor. Pre-checks the games this
        // preset already auto-loads for, lets the user check/uncheck
        // others, then applies the diff: every newly-checked game gets
        // SetDefault to this preset, every newly-unchecked game (that
        // was checked before) gets cleared. Replaces the older
        // single-select Set + Clear button pair.
        private void GameSetDefault_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null || _plugin == null) return;
            var known = CollectKnownGames();
            if (known.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "No games seen yet. Launch a game once so SimHub registers it, then come back to bind a default preset.",
                    "Set default for game", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var before = new HashSet<string>(sel.Defaults ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            // Make sure any game the preset already binds to is listed
            // even if SimHub hasn't seen it in this session (e.g. user
            // hasn't launched it yet but we have a saved binding).
            foreach (var g in before)
                if (!string.IsNullOrEmpty(g)
                    && !known.Any(k => string.Equals(k, g, StringComparison.OrdinalIgnoreCase)))
                    known.Add(g);
            known.Sort(StringComparer.OrdinalIgnoreCase);

            var picked = PickMultipleFromList(
                $"Set default games for '{sel.Name}'",
                "Check every game this preset should auto-load for. Unchecking a game clears its current binding.",
                known, before);
            if (picked == null) return;

            foreach (var g in known)
            {
                bool wasOn = before.Contains(g);
                bool nowOn = picked.Contains(g);
                if (nowOn && !wasOn)
                    _plugin.SetDefaultPresetForGame(g, sel.Name);
                else if (!nowOn && wasOn)
                    _plugin.ClearDefaultPresetForGame(g);
            }
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
            if (_shareInProgress) return;
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Enable Community Contributions in Settings to share presets.",
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _shareInProgress = true;
            try
            {
                // Resolve + serialize INSIDE the try block so a
                // JsonSerializationException on JToken.FromObject can't
                // leak past the catch and crash the dispatcher.
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

                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    MessageBox.Show(owner,
                        "Pick a username before sharing (Settings > Account & community).",
                        "Share preset", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dialog = new PresetShareWindow(
                    _plugin, sel.PresetName, sel.GameName ?? "",
                    sel.CarId, carDisplay, body, tags)
                {
                    Owner = owner,
                };
                dialog.ShowDialog();
                // The modal handles its own success/failure messaging.
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[Trueforce] Share preset failed: " + ex.Message);
                MessageBox.Show(Window.GetWindow(this),
                    "Share preset failed: " + ex.Message,
                    "Share preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shareInProgress = false; }
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

        // Upload the selected custom engine via PresetShareWindow.ForEngine
        // so engines get the same Name / Description / Allow-in-packs /
        // inline-sign-in flow as car + game presets, instead of the
        // older YesNoCancel MessageBox + no-description path.
        private async void CustomShare_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedCustom;
            if (row?.Def == null || _plugin == null) return;
            if (_shareInProgress) return;
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Enable Community Contributions in Settings to share custom engines.",
                    "Share custom engine", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _shareInProgress = true;
            try
            {
                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    MessageBox.Show(owner,
                        "Pick a username before sharing (Settings > Account & community).",
                        "Share custom engine", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var body = Newtonsoft.Json.Linq.JObject.FromObject(row.Def);
                var dialog = PresetShareWindow.ForEngine(_plugin,
                    row.Def.Name ?? "Custom engine", body);
                dialog.Owner = owner;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[Trueforce] Share custom engine failed: " + ex.Message);
                MessageBox.Show(Window.GetWindow(this),
                    "Share custom engine failed: " + ex.Message,
                    "Share custom engine", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shareInProgress = false; }
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

        // Modal multi-select picker. Returns the chosen set or null on
        // Cancel. preChecked pre-selects entries (case-insensitive
        // matched against items). Used by the Set Default action so
        // toggling games on/off is one trip instead of two buttons
        // (Set + Clear) and N modal trips.
        private HashSet<string> PickMultipleFromList(string title, string helpText,
            IList<string> items, ICollection<string> preChecked)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 420,
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

            var preSet = new HashSet<string>(preChecked ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var checks = new List<CheckBox>(items.Count);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listPanel = new StackPanel();
            foreach (var it in items)
            {
                var cb = new CheckBox
                {
                    Content = it,
                    IsChecked = preSet.Contains(it),
                    Margin = new Thickness(0, 2, 0, 2),
                };
                checks.Add(cb);
                listPanel.Children.Add(cb);
            }
            sv.Content = listPanel;
            Grid.SetRow(sv, 1);
            grid.Children.Add(sv);

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

            HashSet<string> result = null;
            ok.Click += (s, args) =>
            {
                result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cb in checks)
                    if (cb.IsChecked == true && cb.Content is string s2)
                        result.Add(s2);
                win.DialogResult = true;
            };
            return win.ShowDialog() == true ? result : null;
        }

        // ===================== Community browser =====================

        // Row in the Community list. Wraps a PresetSummary with the
        // formatting the grid binds against. MyVote is mirrored on the
        // row so the Reddit-style up/down arrows can render a selected
        // state and the click handler can toggle vs retract correctly.
        private sealed class CommunityRow
        {
            public PresetSummary Summary { get; set; }
            public string Name        => Summary?.Name ?? "";
            public string Author      => string.IsNullOrEmpty(Summary?.Author) ? "(anonymous)" : Summary.Author;
            public string Game        => Summary?.Game ?? "";
            public string CarId       => Summary?.CarId ?? "";
            public string ScoreLabel  => (((Summary?.Upvotes ?? 0) - (Summary?.Downvotes ?? 0))).ToString();
            public int    Downloads   => Summary?.Downloads ?? 0;
            public string TagsLabel   => Summary?.EffectTags == null || Summary.EffectTags.Count == 0
                                          ? "" : string.Join(", ", Summary.EffectTags);
            public string Description => Summary?.Description ?? "";
            // Pre-computed game-preset tier label set by the post-fetch
            // ranking pass ("for <ActiveGame>" / "Universal" / "Other
            // games (N)"). Empty for non-game rows and when no fetch has
            // been ranked yet.
            public string TierBadge   { get; set; } = "";
            // -1 / 0 / +1. Drives arrow colors + click semantics.
            public int    MyVote      { get; set; }
            public System.Windows.Media.Brush UpArrowBrush =>
                MyVote == 1
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0xCC, 0x88))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80));
            public System.Windows.Media.Brush DownArrowBrush =>
                MyVote == -1
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x77, 0x77))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80));
        }

        // Tiered ranking for the game-presets browse list: rows tuned for
        // the active game first, Universal next, then Other. Within-tier
        // order is the server's sort order, preserved via OrderBy's
        // stability. When the plugin is idle (no ActiveGame), the Match
        // tier is empty so Universal floats to the top.
        private static List<PresetSummary> RankGamePresetsByTargetGames(
            List<PresetSummary> rows, string activeGame)
        {
            if (rows == null || rows.Count == 0) return rows;
            bool hasActive = !string.IsNullOrEmpty(activeGame);
            int Tier(PresetSummary p)
            {
                if (p?.TargetGames == null || p.TargetGames.Length == 0) return hasActive ? 1 : 0;
                if (hasActive)
                {
                    foreach (var g in p.TargetGames)
                        if (string.Equals(g, activeGame, StringComparison.OrdinalIgnoreCase))
                            return 0;
                    return 2;
                }
                return 1;
            }
            return rows.OrderBy(Tier).ToList();
        }

        private static string ComputeTierBadge(PresetSummary p, string activeGame)
        {
            if (p == null) return "";
            bool universal = p.TargetGames == null || p.TargetGames.Length == 0;
            if (universal) return "Universal";
            if (!string.IsNullOrEmpty(activeGame))
            {
                foreach (var g in p.TargetGames)
                    if (string.Equals(g, activeGame, StringComparison.OrdinalIgnoreCase))
                        return "for " + UiContentSanitizer.SafeDisplayText(activeGame, 32);
            }
            return "Other games (" + p.TargetGames.Length + ")";
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<CommunityRow> _communityRows =
            new System.Collections.ObjectModel.ObservableCollection<CommunityRow>();
        private bool _communityFetchInFlight;
        private string _communitySort = "wilson";
        // Tracks which (game, carId) the displayed list represents so we
        // can repopulate when the active car changes.
        private string _communityListedCarKey;
        // "for-car" (browse by active car) vs "mine" (signed-in user's
        // own uploads, across every car/game).
        private string _communityMode = "for-car";
        // Discovery kind: "car" (community car presets scoped to the
        // active car) or "game" (community game presets scoped to the
        // active game). Driven by which segment's Community sub-pill
        // is active. "Mine" mode is independent of kind and lives in
        // the Account expander; here we only browse.
        private string _communityKind = "car";
        // Set true after a CommunityRefreshAsync completes successfully
        // using the trending fallback (for-car mode + no game/car).
        // Decoupled from per-fetch locals so the synchronous label
        // updates in RelabelCommunityScopeRadio have a stable value
        // between fetches.
        private bool _lastFetchWasTrending;

        private CommunityRow SelectedCommunity =>
            CommunityList?.SelectedItem as CommunityRow;

        // Called from the host (SettingsControl) when active car changes
        // so the community panel can show "Showing presets for: X" with
        // the live name + refresh when visible.
        /// <summary>Called by the host when the active car (and possibly
        /// game) changed. gameAlsoChanged distinguishes a pure car
        /// switch within the same game from a game switch (which may
        /// or may not also bring a new car). Different community kinds
        /// have different scope sensitivity:
        ///   car   - game+car scoped, refresh on any change
        ///   game  - game-scoped, refresh only when game changed
        ///   engine, pack - global, never refresh on this signal
        /// </summary>
        public void OnActiveCarChanged(bool gameAlsoChanged = true)
        {
            UpdateCommunityActiveCarLabel();
            if (CommunityPanel == null
                || CommunityPanel.Visibility != Visibility.Visible
                || _plugin == null)
                return;
            bool shouldRefresh =
                _communityKind == "car"
                || (_communityKind == "game" && gameAlsoChanged);
            if (shouldRefresh)
                _ = CommunityRefreshAsync();
        }

        private void UpdateCommunityActiveCarLabel()
        {
            if (CommunityCarLabel == null || _plugin == null) return;
            bool isGameKind = _communityKind == "game";
            // Swap the leading label too so the row reads cleanly in
            // both modes ("Active game:" vs "Active car:").
            if (CommunityScopeLabel != null && _communityMode != "mine")
                CommunityScopeLabel.Text = isGameKind ? "Active game:" : "Active car:";

            if (isGameKind)
            {
                string game = _plugin.ActiveGame;
                CommunityCarLabel.Text = string.IsNullOrEmpty(game)
                    ? "(none - load a game first)"
                    : game;
                return;
            }
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

        private void CommunityMode_Changed(object sender, RoutedEventArgs e)
        {
            if (CommunityModeMine == null || _plugin == null) return;
            string newMode = CommunityModeMine.IsChecked == true ? "mine" : "for-car";
            if (newMode == _communityMode) return;
            _communityMode = newMode;
            // Label swap + refresh.
            if (CommunityScopeLabel != null)
                CommunityScopeLabel.Text = newMode == "mine" ? "Your uploads:" : "Active car:";
            // Clear the row set immediately so the banner-visibility
            // call below doesn't evaluate against stale for-car rows
            // when the user just switched into "mine" mode (the count
            // check would otherwise flash the banner visible against the
            // prior tab's contents before the fetch repopulates).
            _communityRows.Clear();
            // Trending state is per-mode (only the for-car mode can be
            // trending); reset on mode flip so the next RelabelCommunityScopeRadio
            // call has a clean baseline until the new fetch overwrites it.
            _lastFetchWasTrending = false;
            UpdateCreatePackFromUploadsBannerVisibility();
            // Update the help text immediately on mode switch so the
            // user sees the new mode's guidance (e.g., the Edit/Delete
            // hint in "mine" mode) without waiting for the fetch.
            RelabelCommunityScopeRadio();
            UpdateCommunityActiveCarLabel();
            _ = CommunityRefreshAsync();
        }

        private string SortKey()
        {
            switch (_communitySort)
            {
                case "newest":    return "newest";
                case "downloads": return "downloads";
                default:          return "for-car" == _communityMode ? "wilson" : "top";
            }
        }

        private async Task CommunityRefreshAsync()
        {
            if (_plugin == null || _communityFetchInFlight) return;
            UpdateCommunityActiveCarLabel();
            if (_plugin.Settings?.CommunityEnabled != true)
            {
                _communityRows.Clear();
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Community Contributions is off (toggle it in Settings).";
                UpdateCreatePackFromUploadsBannerVisibility();
                CommunityList_SelectionChanged(null, null);
                return;
            }

            // My uploads mode requires sign-in. For-car mode doesn't.
            if (_communityMode == "mine" && !_plugin.AuthIsSignedIn)
            {
                _communityRows.Clear();
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Sign in (Account & community in Settings) to see your uploads.";
                UpdateCreatePackFromUploadsBannerVisibility();
                CommunityList_SelectionChanged(null, null);
                return;
            }

            string game  = _plugin.ActiveGame;
            string carId = _plugin.ActiveCarId;
            // Scope requirements differ by kind: car needs game+car;
            // game needs only game; engine + pack are global (no scope).
            bool isGameKind   = _communityKind == "game";
            bool isEngineKind = _communityKind == "engine";
            bool isPackKind   = _communityKind == "pack";
            // Trending fallback: for-car mode normally needs a game+car
            // (or just game for game-kind), but when none is loaded the
            // panel used to dead-end. Engines + packs already browse
            // globally, so the asymmetry read as broken. Detect the
            // empty-scope case and pivot to a cross-game trending fetch
            // instead of bailing.
            bool useTrendingFallback = false;
            if (_communityMode == "for-car" && !isEngineKind && !isPackKind)
            {
                bool scopeMissing = string.IsNullOrEmpty(game)
                    || (!isGameKind && string.IsNullOrEmpty(carId));
                if (scopeMissing) useTrendingFallback = true;
            }

            _communityFetchInFlight = true;
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = useTrendingFallback
                    ? "Loading trending..."
                    : "Loading...";

            string capturedMode = _communityMode;
            string capturedKind = _communityKind;
            string capturedGame = game;
            string capturedCar  = carId;
            string capturedSort = SortKey();
            bool capturedTrending = useTrendingFallback;
            List<PresetSummary> results = null;
            try
            {
                if (capturedMode == "mine")
                {
                    // Mine-mode goes through an auth-bearer fetch, so
                    // call the async variant directly instead of
                    // wrapping a sync-over-async in Task.Run.
                    results = await _plugin.FetchMyCommunityPresetsAsync(capturedSort, 100);
                }
                else
                {
                    results = await Task.Run(() =>
                    {
                        if (capturedTrending)
                        {
                            // Trending fallback - cross-game, no car/game filter.
                            return capturedKind == "game"
                                ? _plugin.FetchCommunityTrendingGamePresets(capturedSort, 50)
                                : _plugin.FetchCommunityTrendingCarPresets(capturedSort, 50);
                        }
                        switch (capturedKind)
                        {
                            case "game":
                                return _plugin.FetchCommunityGamePresetsForGame(capturedGame, capturedSort, 50);
                            case "engine":
                                return _plugin.FetchCommunityCustomEngines(capturedSort, 50);
                            case "pack":
                                return _plugin.FetchCommunityPacks(capturedSort, 50);
                            default:
                                return _plugin.FetchCommunityPresetsForCar(capturedGame, capturedCar, capturedSort, 50);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Fetch failed: " + ex.Message;
                _communityFetchInFlight = false;
                return;
            }

            // Stale-fetch guard: if context shifted mid-flight, drop
            // the result. Car-kind cares about both game + car; game-
            // kind only about game; engine + pack + mine-mode about
            // neither. Trending: discard if a game/car has since
            // populated, since the next refresh would be scoped and
            // showing global rows would confuse the reader.
            if (capturedMode == "for-car" && capturedKind != "engine" && capturedKind != "pack")
            {
                if (capturedTrending)
                {
                    // Discard the trending fetch only when the new scope
                    // would let a scoped fetch actually run. Game-kind
                    // needs only an active game; car-kind needs BOTH game
                    // AND car (a game-only scope still can't run the
                    // FetchCommunityPresetsForCar call). Without this
                    // refinement the car-kind path would silently drop
                    // the trending result while no follow-up refresh ever
                    // fires, leaving the status label stuck on "Loading
                    // trending..." forever.
                    bool scopeNowPresent = !string.IsNullOrEmpty(_plugin.ActiveGame);
                    if (capturedKind != "game")
                        scopeNowPresent = scopeNowPresent && !string.IsNullOrEmpty(_plugin.ActiveCarId);
                    if (scopeNowPresent)
                    {
                        _communityFetchInFlight = false;
                        return;
                    }
                }
                else if (capturedKind == "game"
                    ? _plugin.ActiveGame != capturedGame
                    : (_plugin.ActiveGame != capturedGame || _plugin.ActiveCarId != capturedCar))
                {
                    _communityFetchInFlight = false;
                    return;
                }
            }
            // Drop result if mode or kind changed mid-flight.
            if (_communityMode != capturedMode || _communityKind != capturedKind)
            {
                _communityFetchInFlight = false;
                return;
            }

            // Persist trending-vs-scoped for label state (the radio /
            // scope label is repainted synchronously from segment
            // changes, so it needs a class-level flag, not a per-fetch
            // local). RelabelCommunityScopeRadio reads this on the next
            // tick.
            _lastFetchWasTrending = capturedTrending;
            _communityListedCarKey = capturedMode == "mine" ? "mine"
                : capturedKind == "engine" ? "engine"
                : capturedKind == "pack"   ? "pack"
                : capturedTrending         ? (capturedKind == "game" ? "trending-game" : "trending-car")
                : capturedKind == "game"   ? "game/" + capturedGame
                : capturedGame + "/" + capturedCar;
            _communityRows.Clear();
            if (results == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Could not reach the community backend.";
                CommunityList_SelectionChanged(null, null);
                _communityFetchInFlight = false;
                return;
            }
            // Game-kind: apply the target_games tier ranking + per-row
            // badge before the rows are inserted. Within-tier order is
            // preserved from the server's sort (Wilson / newest / downloads).
            if (capturedKind == "game")
                results = RankGamePresetsByTargetGames(results, _plugin?.ActiveGame);
            foreach (var s in results)
            {
                var row = new CommunityRow { Summary = s };
                if (capturedKind == "game")
                    row.TierBadge = ComputeTierBadge(s, _plugin?.ActiveGame);
                _communityRows.Add(row);
            }

            // Bulk-pull the signed-in user's votes on these rows so the
            // Reddit arrows can show selected state.
            if (_plugin.AuthIsSignedIn && _communityRows.Count > 0)
            {
                var ids = new List<string>(_communityRows.Count);
                foreach (var r in _communityRows)
                    if (!string.IsNullOrEmpty(r.Summary?.Id)) ids.Add(r.Summary.Id);
                Dictionary<string, int> map = null;
                // Direct await (the underlying FetchMyVotesAsync handles
                // its own threading); the Task.Run wrapper used to be
                // a band-aid for the sync-over-async pattern.
                try { map = await _plugin.FetchMyCommunityVotesAsync(ids); }
                catch { /* leave votes at 0 */ }
                if (map != null)
                {
                    for (int i = 0; i < _communityRows.Count; i++)
                    {
                        var r = _communityRows[i];
                        if (r.Summary?.Id != null && map.TryGetValue(r.Summary.Id, out int v))
                            r.MyVote = v;
                    }
                    // Replace rows to force WPF to re-resolve the brush bindings.
                    var snapshot = _communityRows.ToList();
                    _communityRows.Clear();
                    foreach (var r in snapshot) _communityRows.Add(r);
                }
            }

            if (CommunityStatusLabel != null)
            {
                string emptyMsg;
                if (capturedMode == "mine")
                    emptyMsg = "You haven't uploaded any presets yet.";
                else if (capturedTrending)
                    emptyMsg = capturedKind == "game"
                        ? "No trending game presets yet."
                        : capturedKind == "engine"
                            ? "No trending custom engines yet."
                            : capturedKind == "pack"
                                ? "No trending packs yet."
                                : "No trending car presets yet.";
                else
                    emptyMsg = capturedKind == "game"
                        ? "No community game presets yet. Be the first to share."
                        : capturedKind == "engine"
                            ? "No community custom engines yet. Be the first to share."
                            : capturedKind == "pack"
                                ? "No community packs yet. Be the first to share."
                                : "No community presets for this car yet. Be the first to share.";
                string foundMsg = capturedTrending
                    ? $"{_communityRows.Count} preset(s) found (trending - load a game/car for scoped results)."
                    : $"{_communityRows.Count} preset(s) found.";
                CommunityStatusLabel.Text = _communityRows.Count == 0 ? emptyMsg : foundMsg;
            }
            UpdateCreatePackFromUploadsBannerVisibility();
            // Repaint the scope radio + help text since the trending
            // flag may have just changed.
            RelabelCommunityScopeRadio();
            CommunityList_SelectionChanged(null, null);
            _communityFetchInFlight = false;

            // Tell the host its active-card top-community cache may be
            // stale: a car-kind for-car fetch for the currently active
            // car just landed (scoped fetch, not trending). Without this
            // signal, a Refresh click here doesn't propagate to the
            // active card's dropdown until a plugin restart.
            if (capturedKind == "car"
                && capturedMode == "for-car"
                && !capturedTrending
                && _plugin != null
                && string.Equals(capturedGame, _plugin.ActiveGame, StringComparison.Ordinal)
                && string.Equals(capturedCar,  _plugin.ActiveCarId, StringComparison.Ordinal))
            {
                CarCommunityListRefreshed?.Invoke();
            }
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
            if (CommunityPreviewBtn  != null) CommunityPreviewBtn.IsEnabled  = has;
            if (CommunityDownloadBtn != null) CommunityDownloadBtn.IsEnabled = has;

            // Edit + Delete visible only when the signed-in user owns the
            // row. Anonymous uploads (OwnerUserId null) never get the
            // affordance; signed-out viewers don't either even on rows
            // they uploaded - they have to sign back in first.
            bool ownsRow = has
                && _plugin?.AuthIsSignedIn == true
                && !string.IsNullOrEmpty(sel.Summary.OwnerUserId)
                && string.Equals(sel.Summary.OwnerUserId,
                                 _plugin.AuthSignedInUserId,
                                 StringComparison.Ordinal);
            if (CommunityEditBtn != null)
            {
                CommunityEditBtn.Visibility = ownsRow ? Visibility.Visible : Visibility.Collapsed;
                CommunityEditBtn.IsEnabled  = ownsRow;
            }
            if (CommunityDeleteBtn != null)
            {
                CommunityDeleteBtn.Visibility = ownsRow ? Visibility.Visible : Visibility.Collapsed;
                CommunityDeleteBtn.IsEnabled  = ownsRow;
            }
        }

        private async void CommunityEdit_Click(object sender, RoutedEventArgs e)
        {
            // Outer try/catch: the bodyBuilder lambda is invoked from
            // inside ShowDialog when the user clicks Save, so a
            // JsonSerializationException could surface here AFTER the
            // existing inner try/catch around UpdateCommunityPresetAsync.
            // Wrap the whole handler so neither path crashes the
            // dispatcher.
            try
            {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (!_plugin.AuthIsSignedIn) return;

            string kind = sel.Summary.Kind ?? "car";
            EditCommunityPresetWindow dlg;
            bool isGameRow = kind == "game";
            if (isGameRow)
            {
                // Game-preset editor: no body replace; target-games picker
                // pre-populated from the row.
                dlg = EditCommunityPresetWindow.ForGamePreset(
                    _plugin,
                    sel.Summary.Name, sel.Summary.Description,
                    sel.Summary.TargetGames, _plugin.ActiveGame,
                    sel.Summary.AllowInPacks);
                dlg.Owner = Window.GetWindow(this);
            }
            else
            {
                // Build the list of the user's local car presets for the
                // SAME car_id so the body-replacement picker only offers
                // presets that make sense for this row's car.
                var localPresets = new List<CarPresetEntry>();
                if (!string.IsNullOrEmpty(sel.Summary.CarId))
                {
                    var perCar = _plugin.GetCarPresets(sel.Summary.CarId);
                    if (perCar != null)
                        foreach (var kv in perCar)
                            if (kv.Value != null && kv.Value.Override != null)
                                localPresets.Add(kv.Value);
                }

                dlg = new EditCommunityPresetWindow(
                    sel.Summary.Name, sel.Summary.Description, sel.Summary.CarId,
                    localPresets,
                    bodyBuilder: ovr =>
                    {
                        var customs = _plugin.CollectReferencedCustomEngines(
                            null, new[] { ovr });
                        var body = new Newtonsoft.Json.Linq.JObject
                        {
                            ["override"] = Newtonsoft.Json.Linq.JToken.FromObject(ovr),
                        };
                        if (customs != null && customs.Count > 0)
                            body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);
                        return body;
                    },
                    tagsBuilder: BuildEffectTags,
                    currentAllowInPacks: sel.Summary.AllowInPacks)
                {
                    Owner = Window.GetWindow(this),
                };
            }
            bool? ok = dlg.ShowDialog();
            if (ok != true) return;

            if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "Updating...";
            bool success;
            try
            {
                if (isGameRow)
                {
                    success = await _plugin.UpdateCommunityGamePresetAsync(
                        sel.Summary.Id, dlg.NewName, dlg.NewDescription,
                        dlg.NewBody, dlg.NewEffectTags,
                        allowInPacks: dlg.NewAllowInPacks,
                        targetGames: dlg.NewTargetGames);
                }
                else
                {
                    success = await _plugin.UpdateCommunityPresetAsync(
                        sel.Summary.Id, dlg.NewName, dlg.NewDescription,
                        dlg.NewBody, dlg.NewEffectTags,
                        allowInPacks: dlg.NewAllowInPacks);
                }
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Update exception: " + ex.Message;
                return;
            }
            if (!success)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Update failed (sign in expired or permission denied).";
                return;
            }
            // Reflect locally + refresh from server next car-change.
            sel.Summary.Name = dlg.NewName;
            sel.Summary.Description = string.IsNullOrEmpty(dlg.NewDescription) ? null : dlg.NewDescription;
            if (dlg.NewEffectTags != null) sel.Summary.EffectTags = dlg.NewEffectTags;
            if (dlg.NewAllowInPacks.HasValue) sel.Summary.AllowInPacks = dlg.NewAllowInPacks.Value;
            if (isGameRow && dlg.NewTargetGames != null) sel.Summary.TargetGames = dlg.NewTargetGames;
            if (isGameRow) sel.TierBadge = ComputeTierBadge(sel.Summary, _plugin?.ActiveGame);
            int idx = _communityRows.IndexOf(sel);
            if (idx >= 0)
            {
                _communityRows.RemoveAt(idx);
                _communityRows.Insert(idx, sel);
                CommunityList.SelectedIndex = idx;
            }
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = dlg.NewBody != null ? "Updated (body replaced)." : "Updated.";
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[Trueforce] CommunityEdit failed: " + ex.Message);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Edit failed: " + ex.Message;
            }
        }

        // Reusable: compute effect_tags from a CarOverride's non-null
        // sections. Both upload + edit need it.
        private static List<string> BuildEffectTags(CarOverride ovr)
        {
            var tags = new List<string>(11);
            if (ovr == null) return tags;
            if (ovr.EnginePulse  != null) tags.Add("engine");
            if (ovr.RevLimiter   != null) tags.Add("revlimiter");
            if (ovr.RoadBumps    != null) tags.Add("roadbumps");
            if (ovr.TractionLoss != null) tags.Add("tractionloss");
            if (ovr.GearShift    != null) tags.Add("gearshift");
            if (ovr.AbsClick     != null) tags.Add("abs");
            if (ovr.PitLimiter   != null) tags.Add("pitlimiter");
            if (ovr.Drs          != null) tags.Add("drs");
            if (ovr.Collision    != null) tags.Add("collision");
            if (ovr.AudioCapture != null) tags.Add("audio");
            if (ovr.Airborne     != null) tags.Add("airborne");
            return tags;
        }

        private async void CommunityDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (!_plugin.AuthIsSignedIn) return;

            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"Delete '{sel.Summary.Name}'? Other drivers won't see it anymore. Vote history stays for moderation review.",
                "Delete preset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "Deleting...";
            bool success;
            try { success = await _plugin.DeleteCommunityPresetAsync(sel.Summary.Id); }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Delete exception: " + ex.Message;
                return;
            }
            if (!success)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Delete failed (sign in expired or permission denied).";
                return;
            }
            _communityRows.Remove(sel);
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Deleted.";
        }

        // Legacy bottom-row handlers (still wired to existing buttons in
        // XAML). Kept harmless - the per-row arrows are the primary
        // surface now. They route through the selected row for backward
        // compat.
        private void CommunityVoteUp_Click(object sender, RoutedEventArgs e)
        {
            ToggleVote(SelectedCommunity, +1);
        }

        private void CommunityVoteDown_Click(object sender, RoutedEventArgs e)
        {
            ToggleVote(SelectedCommunity, -1);
        }

        // Reddit-style per-row arrow handlers. Clicking the up arrow when
        // you've already upvoted retracts (value=0); clicking the down
        // arrow when upvoted flips. Sign-in required.
        private void CommunityVoteUpCell_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleVote((sender as FrameworkElement)?.Tag as CommunityRow, +1);
        }

        private void CommunityVoteDownCell_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleVote((sender as FrameworkElement)?.Tag as CommunityRow, -1);
        }

        private async void ToggleVote(CommunityRow row, int clicked)
        {
            if (row?.Summary == null || _plugin == null) return;
            if (!_plugin.AuthIsSignedIn)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Sign in (Account & community in Settings) to vote.";
                return;
            }
            // A refresh mid-fetch would replace _communityRows with new
            // CommunityRow objects, so RepaintCommunityRow's IndexOf
            // on the captured 'row' returns -1 and the optimistic rollback
            // would silently fail to repaint. Reject the vote with a
            // friendly nudge.
            if (_communityFetchInFlight)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Refresh in progress. Try voting again in a moment.";
                return;
            }
            int prev = row.MyVote;
            int next = (prev == clicked) ? 0 : clicked;  // toggle off or flip/set
            // Optimistic counter adjustment based on prev->next transition.
            if (prev == 1)  row.Summary.Upvotes   = Math.Max(0, row.Summary.Upvotes   - 1);
            if (prev == -1) row.Summary.Downvotes = Math.Max(0, row.Summary.Downvotes - 1);
            if (next == 1)  row.Summary.Upvotes   += 1;
            if (next == -1) row.Summary.Downvotes += 1;
            row.MyVote = next;
            RepaintCommunityRow(row);
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = next == 0
                    ? "Sending vote retraction..."
                    : (next == 1 ? "Sending upvote..." : "Sending downvote...");

            // Await the server. On failure roll the counters back to
            // 'prev' so the row doesn't lie about being voted on.
            string kind = row.Summary.Kind ?? _communityKind;
            bool ok = false;
            try
            {
                switch (kind)
                {
                    case "game":   ok = await _plugin.TryVoteCommunityGamePresetAsync(row.Summary.Id, next); break;
                    case "engine": ok = await _plugin.TryVoteCommunityCustomEngineAsync(row.Summary.Id, next); break;
                    case "pack":   ok = await _plugin.TryVoteCommunityPackAsync(row.Summary.Id, next); break;
                    default:       ok = await _plugin.TryVoteCommunityPresetAsync(row.Summary.Id, next); break;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                SimHub.Logging.Current.Info("[Trueforce] Vote failed: " + ex.Message);
            }

            if (!ok)
            {
                // Roll back to prev so UI matches reality.
                if (next == 1)  row.Summary.Upvotes   = Math.Max(0, row.Summary.Upvotes   - 1);
                if (next == -1) row.Summary.Downvotes = Math.Max(0, row.Summary.Downvotes - 1);
                if (prev == 1)  row.Summary.Upvotes   += 1;
                if (prev == -1) row.Summary.Downvotes += 1;
                row.MyVote = prev;
                RepaintCommunityRow(row);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Vote didn't go through. Sign-in may have expired; try again.";
                return;
            }

            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = next == 0
                    ? "Vote retracted."
                    : (next == 1 ? "Upvote recorded." : "Downvote recorded.");
        }

        // Force the grid to rebuild the row so WPF re-resolves arrow
        // brush + score bindings.
        private void RepaintCommunityRow(CommunityRow row)
        {
            int idx = _communityRows.IndexOf(row);
            if (idx < 0) return;
            _communityRows.RemoveAt(idx);
            _communityRows.Insert(idx, row);
            if (CommunityList != null) CommunityList.SelectedIndex = idx;
        }

        private void CommunityReport_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"Report '{sel.Summary.Name}' for moderator review?",
                "Report preset", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            switch (sel.Summary.Kind ?? _communityKind)
            {
                case "game":   _plugin.ReportCommunityGamePreset(sel.Summary.Id); break;
                case "engine": _plugin.ReportCommunityCustomEngine(sel.Summary.Id); break;
                case "pack":   _plugin.ReportCommunityPack(sel.Summary.Id); break;
                default:       _plugin.ReportCommunityPreset(sel.Summary.Id); break;
            }
            // Report RPCs are fire-and-forget at this layer; they log
            // internally on failure but never roundtrip a success
            // boolean here. Phrase the status as "submitted" rather
            // than "received" so we don't claim a confirmed delivery.
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Report submitted. Thanks for flagging.";
        }

        // Open the preview window: read-only view of the preset's
        // sections + key values. If the user hits "Download…" inside the
        // preview, route them straight through the normal download flow.
        private async void CommunityPreview_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "Loading preview...";

            string capturedId   = sel.Summary.Id;
            string capturedKind = sel.Summary.Kind ?? _communityKind ?? "car";
            PresetFull full = null;
            try
            {
                full = await Task.Run(() =>
                {
                    switch (capturedKind)
                    {
                        case "game":   return _plugin.FetchCommunityGamePresetBody(capturedId);
                        case "engine": return _plugin.FetchCommunityCustomEngineBody(capturedId);
                        case "pack":   return _plugin.FetchCommunityPackBody(capturedId);
                        default:       return _plugin.FetchCommunityPresetBody(capturedId);
                    }
                });
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Preview failed: " + ex.Message;
                return;
            }
            if (full?.Body == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Preview returned no body.";
                return;
            }
            var win = new PresetPreviewWindow(full.Summary, full.Body)
            {
                Owner = Window.GetWindow(this),
            };
            bool? ok = win.ShowDialog();
            if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "";
            if (ok == true && win.DownloadRequested)
                CommunityDownload_Click(this, e);  // re-fires the standard download path
        }

        private async void CommunityDownload_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Downloading...";

            string capturedId   = sel.Summary.Id;
            string capturedKind = sel.Summary.Kind ?? _communityKind ?? "car";
            PresetFull full = null;
            try
            {
                full = await Task.Run(() =>
                {
                    switch (capturedKind)
                    {
                        case "game":   return _plugin.FetchCommunityGamePresetBody(capturedId);
                        case "engine": return _plugin.FetchCommunityCustomEngineBody(capturedId);
                        case "pack":   return _plugin.FetchCommunityPackBody(capturedId);
                        default:       return _plugin.FetchCommunityPresetBody(capturedId);
                    }
                });
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

            // Game presets take a different import shape entirely:
            // body = { snapshot: GameSettingsSnapshot }. Save as a
            // user game preset and stamp the download tracker, then
            // bail; the car-preset section picker doesn't apply.
            if (capturedKind == "game")
            {
                GameSettingsSnapshot snap = null;
                List<CustomEngineDef> gameCustoms = null;
                try
                {
                    var snapToken = full.Body["snapshot"];
                    if (snapToken != null)
                        snap = snapToken.ToObject<GameSettingsSnapshot>();
                    // HeaderGameShare now piggybacks custom engines on
                    // the snapshot body; import them or recipients fall
                    // back to silence on any EnginePulse that points at
                    // a curator-only engine.
                    var ceToken = full.Body["custom_engines"];
                    if (ceToken is Newtonsoft.Json.Linq.JArray gca)
                        gameCustoms = gca.ToObject<List<CustomEngineDef>>();
                }
                catch (Exception ex)
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text = "Body parse failed: " + ex.Message;
                    return;
                }
                if (snap == null)
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text = "Body had no snapshot section.";
                    return;
                }

                if (gameCustoms != null && gameCustoms.Count > 0)
                    _plugin.ImportCommunityCustomEngines(gameCustoms);

                // Dedup against an existing local snapshot with the
                // same server uuid -- re-downloading shouldn't pile up
                // copies. Update detection still surfaces author edits
                // via FindCommunityPresetUpdatesAsync.
                if (_plugin.Settings?.Presets != null)
                {
                    foreach (var kv in _plugin.Settings.Presets)
                        if (kv.Value != null
                            && string.Equals(kv.Value.CommunitySourceId, full.Summary.Id,
                                             StringComparison.Ordinal))
                        {
                            if (CommunityStatusLabel != null)
                                CommunityStatusLabel.Text = $"You already have this preset as '{kv.Key}'.";
                            return;
                        }
                }

                string gpBase = full.Summary.Name + " (community)";
                string gpName = gpBase;
                int gn = 2;
                while (_plugin.Settings?.Presets != null
                    && _plugin.Settings.Presets.ContainsKey(gpName))
                {
                    gpName = gpBase + " " + gn;
                    gn++;
                }
                bool saved = _plugin.SaveImportedCommunityGamePreset(
                    gpName, snap, full.Summary.Author, full.Summary.Description,
                    communitySourceId: full.Summary.Id);
                if (!saved)
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text =
                            "Couldn't save the preset. Check disk space and permissions, then try again.";
                    return;
                }
                _plugin.RecordCommunityGamePresetDownload(capturedId);
                _plugin.RecordDownloadedCommunityPreset(
                    full.Summary.Id, gpName,
                    carId: "*", gameName: _plugin.ActiveGame,
                    contentVersion: full.Summary.ContentVersion, kind: "game",
                    allowInPacks: full.Summary.AllowInPacks);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = $"Saved as '{gpName}'.";
                LibraryChanged?.Invoke();
                return;
            }

            // Packs: body bundles multiple entries across kinds.
            // Each entry is imported in turn (skipping anything malformed),
            // then the bundle is recorded so the standard Installed
            // packs flow knows about it.
            if (capturedKind == "pack")
            {
                int imported = 0;
                int errors   = 0;
                int skipped  = 0;  // entries already in the library (by CommunitySourceId)

                // Game presets (entries: { name, snapshot,
                // source_id?, source_author?, allow_in_packs? }). Per-
                // entry try/catch so one broken row doesn't bail the
                // whole import. Per-entry RecordDownloadedCommunityPreset
                // so each item lands in the tracker. If the user already
                // has the item (matching CommunitySourceId), skip the
                // save entirely so packs don't duplicate locally-owned
                // entries on every import.
                if (full.Body["game_presets"] is Newtonsoft.Json.Linq.JArray gpArr)
                {
                    foreach (var entry in gpArr)
                    {
                        try
                        {
                            string name = entry?["name"]?.ToString();
                            var snap = entry?["snapshot"]?.ToObject<GameSettingsSnapshot>();
                            if (string.IsNullOrWhiteSpace(name) || snap == null) continue;
                            string entrySourceId = entry?["source_id"]?.ToString();
                            string entryAuthor   = entry?["source_author"]?.ToString() ?? full.Summary.Author;
                            bool   entryAllowPacks = entry?["allow_in_packs"]?.ToObject<bool>() ?? false;
                            // Dedup by CommunitySourceId: if the user
                            // already has a snapshot pointing at the
                            // same server uuid, leave it alone.
                            if (!string.IsNullOrEmpty(entrySourceId)
                                && _plugin.Settings?.Presets != null)
                            {
                                bool already = false;
                                foreach (var kv in _plugin.Settings.Presets)
                                    if (kv.Value != null
                                        && string.Equals(kv.Value.CommunitySourceId, entrySourceId,
                                                         StringComparison.Ordinal))
                                    { already = true; break; }
                                if (already) { skipped++; continue; }
                            }
                            string useName = name + " (community)";
                            int gpN = 2;
                            while (_plugin.Settings?.Presets != null
                                && _plugin.Settings.Presets.ContainsKey(useName))
                            {
                                useName = name + " (community) " + gpN;
                                gpN++;
                            }
                            bool entrySaved = _plugin.SaveImportedCommunityGamePreset(
                                useName, snap, entryAuthor, full.Summary.Description,
                                communitySourceId: entrySourceId);
                            if (!entrySaved)
                            {
                                errors++;
                                SimHub.Logging.Current.Info(
                                    $"[Trueforce] Pack entry (game) persist failed for '{useName}'.");
                                continue;
                            }
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                _plugin.RecordDownloadedCommunityPreset(
                                    entrySourceId, useName,
                                    carId: "*", gameName: _plugin.ActiveGame,
                                    contentVersion: 1, kind: "game",
                                    allowInPacks: entryAllowPacks);
                            }
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[Trueforce] Pack entry (game) import failed: {ex.Message}");
                        }
                    }
                }
                // Car presets (entries: { car_id, preset_name, game_name,
                // override, source_id?, source_author?, allow_in_packs? }).
                if (full.Body["car_presets"] is Newtonsoft.Json.Linq.JArray cpArr)
                {
                    foreach (var entry in cpArr)
                    {
                        try
                        {
                            string cid    = entry?["car_id"]?.ToString();
                            string pname  = entry?["preset_name"]?.ToString();
                            string gname  = entry?["game_name"]?.ToString() ?? _plugin.ActiveGame ?? "";
                            var carOvr = entry?["override"]?.ToObject<CarOverride>();
                            if (string.IsNullOrWhiteSpace(cid) || string.IsNullOrWhiteSpace(pname) || carOvr == null) continue;
                            string entrySourceId = entry?["source_id"]?.ToString();
                            string entryAuthor   = entry?["source_author"]?.ToString() ?? full.Summary.Author;
                            bool   entryAllowPacks = entry?["allow_in_packs"]?.ToObject<bool>() ?? false;
                            // Dedup: any car preset under any car id
                            // whose Override.CommunitySourceId matches.
                            // (Same car can have presets under different
                            // names, so we scan; cheap because GetCarPresets
                            // is in-memory.)
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                var existingPerCar = _plugin.GetCarPresets(cid);
                                bool already = false;
                                if (existingPerCar != null)
                                    foreach (var kv in existingPerCar)
                                        if (kv.Value?.Override != null
                                            && string.Equals(kv.Value.Override.CommunitySourceId, entrySourceId,
                                                             StringComparison.Ordinal))
                                        { already = true; break; }
                                if (already) { skipped++; continue; }
                            }
                            string useName = pname + " (community)";
                            int cpN = 2;
                            var carExisting = _plugin.GetCarPresets(cid);
                            while (carExisting != null && carExisting.ContainsKey(useName))
                            {
                                useName = pname + " (community) " + cpN;
                                cpN++;
                            }
                            _plugin.SaveImportedCommunityCarPreset(
                                cid, useName, gname, carOvr,
                                entryAuthor, full.Summary.Description,
                                communitySourceId: entrySourceId);
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                _plugin.RecordDownloadedCommunityPreset(
                                    entrySourceId, useName,
                                    carId: cid, gameName: gname,
                                    contentVersion: 1, kind: "car",
                                    allowInPacks: entryAllowPacks);
                            }
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[Trueforce] Pack entry (car) import failed: {ex.Message}");
                        }
                    }
                }
                // Custom engines (entries are bare CustomEngineDef +
                // optional source_id / allow_in_packs siblings). Engine
                // dedup is now naturally id-keyed because
                // SaveImportedCommunityCustomEngine uses the community
                // uuid AS the local Id and MergeImportedCustomEngines
                // skips duplicates -- so the same library scan would be
                // redundant. Track the skip count via the merge return.
                if (full.Body["custom_engines"] is Newtonsoft.Json.Linq.JArray ceArr)
                {
                    foreach (var entry in ceArr)
                    {
                        try
                        {
                            var def = entry?.ToObject<CustomEngineDef>();
                            if (def == null || string.IsNullOrWhiteSpace(def.Name)) continue;
                            string entrySourceId = entry?["source_id"]?.ToString();
                            bool   entryAllowPacks = entry?["allow_in_packs"]?.ToObject<bool>() ?? false;
                            // Pre-check the library: engine merge dedups
                            // by Id (which Save*Imported sets to the
                            // server uuid), but we still want a
                            // skipped++ tally for the post-import summary.
                            bool already = false;
                            if (!string.IsNullOrEmpty(entrySourceId)
                                && _plugin.Settings?.CustomEngines != null)
                            {
                                foreach (var local in _plugin.Settings.CustomEngines)
                                    if (local != null
                                        && (string.Equals(local.CommunitySourceId, entrySourceId, StringComparison.Ordinal)
                                            || string.Equals(local.Id, entrySourceId, StringComparison.Ordinal)))
                                    { already = true; break; }
                            }
                            if (already) { skipped++; continue; }
                            _plugin.SaveImportedCommunityCustomEngine(def,
                                communitySourceId: entrySourceId);
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                _plugin.RecordDownloadedCommunityPreset(
                                    entrySourceId, def.Name,
                                    carId: "*", gameName: "",
                                    contentVersion: 1, kind: "engine",
                                    allowInPacks: entryAllowPacks);
                            }
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[Trueforce] Pack entry (engine) import failed: {ex.Message}");
                        }
                    }
                }

                // Bump the server-side download counter only when at
                // least one entry actually imported AND nothing failed
                // - partial-failure attempts shouldn't inflate the
                // pack's popularity. The local tracker still records
                // (with contentVersion=0 below) so the next plugin
                // load re-surfaces it as "needs re-import."
                if (errors == 0 && imported > 0)
                    _plugin.RecordCommunityPackDownload(capturedId);
                // Only record the pack tracker at full ContentVersion
                // when every entry imported cleanly. On partial failure
                // we stamp SeenContentVersion=0 so the next plugin-load
                // update sweep treats the pack as "needs re-import" and
                // surfaces it again.
                _plugin.RecordDownloadedCommunityPreset(
                    full.Summary.Id, full.Summary.Name ?? "Pack",
                    carId: "*", gameName: "",
                    contentVersion: errors > 0 ? 0 : full.Summary.ContentVersion,
                    kind: "pack");
                if (CommunityStatusLabel != null)
                {
                    string s = $"'{full.Summary.Name}': ";
                    if (imported > 0) s += $"imported {imported}";
                    if (skipped  > 0) s += (imported > 0 ? ", " : "") + $"{skipped} already in your library";
                    if (errors   > 0) s += (imported + skipped > 0 ? ", " : "") + $"{errors} failed (check log)";
                    if (imported == 0 && skipped == 0 && errors == 0)
                        s += "no recognized entries";
                    CommunityStatusLabel.Text = s + ".";
                }
                LibraryChanged?.Invoke();
                return;
            }

            // Custom engines: body is a serialized CustomEngineDef.
            // Import path mirrors the piggyback ImportCommunityCustomEngines
            // pipeline that car-preset downloads use for referenced
            // engines. The Settings.CustomEngines dedup is content-aware.
            if (capturedKind == "engine")
            {
                CustomEngineDef def = null;
                try { def = full.Body.ToObject<CustomEngineDef>(); }
                catch (Exception ex)
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text = "Body parse failed: " + ex.Message;
                    return;
                }
                if (def == null || string.IsNullOrWhiteSpace(def.Name))
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text = "Engine body was empty or invalid.";
                    return;
                }
                // Dedup against the local engine library by server uuid.
                if (_plugin.Settings?.CustomEngines != null)
                {
                    foreach (var local in _plugin.Settings.CustomEngines)
                        if (local != null
                            && (string.Equals(local.CommunitySourceId, full.Summary.Id, StringComparison.Ordinal)
                                || string.Equals(local.Id, full.Summary.Id, StringComparison.Ordinal)))
                        {
                            if (CommunityStatusLabel != null)
                                CommunityStatusLabel.Text = $"You already have this engine as '{local.Name}'.";
                            return;
                        }
                }
                _plugin.SaveImportedCommunityCustomEngine(def,
                    communitySourceId: full.Summary.Id);
                _plugin.RecordCommunityCustomEngineDownload(capturedId);
                _plugin.RecordDownloadedCommunityPreset(
                    full.Summary.Id, def.Name,
                    carId: "*", gameName: "",
                    contentVersion: full.Summary.ContentVersion, kind: "engine",
                    allowInPacks: full.Summary.AllowInPacks);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = $"Saved engine '{def.Name}' to your library.";
                LibraryChanged?.Invoke();
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

            // Dedup against the user's local library for this car by
            // server uuid -- re-download shouldn't pile up copies.
            var existing = _plugin.GetCarPresets(_plugin.ActiveCarId);
            if (existing != null)
            {
                foreach (var kv in existing)
                    if (kv.Value?.Override != null
                        && string.Equals(kv.Value.Override.CommunitySourceId, full.Summary.Id,
                                         StringComparison.Ordinal))
                    {
                        if (CommunityStatusLabel != null)
                            CommunityStatusLabel.Text = $"You already have this preset as '{kv.Key}'.";
                        return;
                    }
            }

            // Save the preset under a community-distinct name. Append
            // " (community)" so the preset manager row makes the
            // source clear. If a same-named preset already exists for
            // this car, append a numeric suffix.
            string baseName = full.Summary.Name + " (community)";
            string presetName = baseName;
            int n = 2;
            while (existing != null && existing.ContainsKey(presetName))
            {
                presetName = baseName + " " + n;
                n++;
            }
            _plugin.SaveImportedCommunityCarPreset(
                _plugin.ActiveCarId, presetName, _plugin.ActiveGame, apply,
                full.Summary.Author, full.Summary.Description,
                communitySourceId: full.Summary.Id);
            _plugin.RecordCommunityPresetDownload(capturedId);
            // Track the download so the next plugin-load update check
            // knows where to look. Re-records on every download (the
            // tracker is keyed on preset_id) so a re-download after a
            // delete-then-re-download cycle stays in sync.
            _plugin.RecordDownloadedCommunityPreset(
                full.Summary.Id, presetName,
                _plugin.ActiveCarId, _plugin.ActiveGame,
                full.Summary.ContentVersion, kind: "car",
                allowInPacks: full.Summary.AllowInPacks);

            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text =
                    $"Saved as '{presetName}'. Open it on the Car presets tab to use.";
            ReloadCars();
            LibraryChanged?.Invoke();
        }
    }
}
