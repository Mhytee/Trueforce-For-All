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
            TrueforceDialog.Show(Window.GetWindow(this),
                $"Validate built-ins ({issues} issue{(issues == 1 ? "" : "s")})",
                body,
                issues > 0 ? DialogKind.Warning : DialogKind.Info);
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
            public string DetailsText { get; set; }
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
            // Redraw automatically when a cloud restore/merge reloads the library (idempotent
            // re-subscribe so a second Init can't double-fire).
            _plugin.LibraryReloaded -= OnLibraryReloaded;
            _plugin.LibraryReloaded += OnLibraryReloaded;
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
            // Restore the global Library|Community view mode before the first
            // ApplyView, otherwise the strip is on its XAML "Library" default
            // and the user's last choice is lost on plugin restart. NOTE:
            // _initializing is already false here (reset in the finally
            // above), so the Mode_Checked this raises runs a REAL ApplyView
            // pass, and the explicit Segment_Checked below then runs a second
            // one. Benign today: the write-back no-ops on value equality and
            // the in-flight guard stops a duplicate fetch - but it is a
            // known double-apply, not gated as previously claimed. If it ever
            // needs fixing, use a dedicated Init-scoped hydration flag; do
            // NOT suppress Mode_Checked/Segment_Checked globally (the host's
            // SelectTab / OpenCarCommunity rely on them at runtime).
            HydrateModeToggles();
            // XAML's IsChecked="True" on the default segment doesn't fire
            // Checked during construction, so force the first ApplyView now or
            // the panels stay hidden until the user first clicks a pill.
            Segment_Checked(null, null);
        }

        // Restore the global view mode. ManagerCommunityForCars is reused as
        // the single "last view was online (Community/My uploads)" latch (the
        // other per-segment bools are now vestigial). My uploads isn't
        // separately persisted; an online latch restores to Community.
        private void HydrateModeToggles()
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            if (s.ManagerCommunityForCars && ModeCommunity != null)
                ModeCommunity.IsChecked = true;
        }

        // Persist the global Library-vs-online choice. Skipped during the
        // initial ApplyView sweep (so hydration doesn't churn the file) and
        // when no settings instance is wired up yet.
        private void PersistManagerMode(bool online)
        {
            if (_initializing) return;
            var s = _plugin?.Settings;
            if (s == null) return;
            if (s.ManagerCommunityForCars != online)
            {
                s.ManagerCommunityForCars = online;
                _plugin.PersistSettings();
            }
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

        /// <summary>Open the Cars segment in Community view. Used by the header
        /// "See all" affordance off the active-car community count chip so the
        /// user lands directly on the browse view.</summary>
        public void OpenCarCommunity()
        {
            if (SegCar != null) SegCar.IsChecked = true;
            if (ModeCommunity != null) ModeCommunity.IsChecked = true;
            ApplyView();
        }

        // Raised when the user clicks the "X updates available" chip near
        // the segment selector. The host (SettingsControl) reuses its
        // existing review flow (FindCommunityPresetUpdatesAsync ->
        // PresetUpdatesAvailableWindow with apply / acknowledge) so all
        // the update-application logic stays in one place.
        public event Action UpdatesChipClicked;

        /// <summary>Set the count shown on the persistent updates chip
        /// near the segment selector. Zero hides the chip; positive
        /// values render "{N} updates available" (singular "1 update
        /// available"). Driven by the host after every check, auto-apply
        /// sweep, and modal close.</summary>
        public void RefreshUpdatesChip(int count)
        {
            if (UpdatesChip == null) return;
            if (count <= 0)
            {
                UpdatesChip.Visibility = Visibility.Collapsed;
                UpdatesChip.Content = "";
                return;
            }
            UpdatesChip.Visibility = Visibility.Visible;
            UpdatesChip.Content = count == 1
                ? "↻ 1 update available"
                : "↻ " + count + " updates available";
        }

        private void UpdatesChip_Click(object sender, RoutedEventArgs e)
        {
            UpdatesChipClicked?.Invoke();
        }

        // Segment pill clicked (or set programmatically): re-apply the active
        // segment under the current global view mode. Guarded so it's a no-op
        // during the XAML load pass.
        private void Segment_Checked(object sender, RoutedEventArgs e) => ApplyView();

        // Global view-mode strip (Library | Community | My uploads) changed:
        // re-apply the active segment under the new mode. Because the mode is
        // global, it survives a segment switch instead of resetting to Library.
        private void Mode_Checked(object sender, RoutedEventArgs e) => ApplyView();

        // Resolve the active segment's kind, then show its local library panel
        // (Library mode) or the shared community panel (Community / My uploads
        // mode). Both the segment pills and the mode strip route here.
        private void ApplyView()
        {
            if (GamePanel == null || CarPanel == null || CustomPanel == null
                || CommunityPanel == null) return;

            string kind =
                  SegCar?.IsChecked    == true ? "car"
                : SegCustom?.IsChecked == true ? "engine"
                : SegPacks?.IsChecked  == true ? "pack"
                : "game";   // SegGame is the default

            bool library = ModeLibrary?.IsChecked == true;
            bool uploads = ModeUploads?.IsChecked == true;   // else Community
            PersistManagerMode(online: !library);

            // Only when first switching INTO My uploads (not on every re-apply,
            // e.g. segment switches while already there), check for moderator
            // notices on this account. Lazy + contextual: the fetch only runs
            // for someone actually looking at their own uploads.
            bool enteringUploads = uploads && !_wasUploads;
            _wasUploads = uploads;

            // Hide everything; the branch below reveals exactly one panel.
            GamePanel.Visibility   = Visibility.Collapsed;
            CarPanel.Visibility    = Visibility.Collapsed;
            CustomPanel.Visibility = Visibility.Collapsed;
            if (PacksPanel != null) PacksPanel.Visibility = Visibility.Collapsed;
            CommunityPanel.Visibility = Visibility.Collapsed;

            if (library)
            {
                switch (kind)
                {
                    case "car":    CarPanel.Visibility    = Visibility.Visible; break;
                    case "engine": CustomPanel.Visibility = Visibility.Visible; break;
                    case "pack":
                        if (PacksPanel != null) PacksPanel.Visibility = Visibility.Visible;
                        ReloadPacks();   // keep the installed-packs grid in sync
                        break;
                    default:       GamePanel.Visibility   = Visibility.Visible; break;
                }
                return;
            }

            CommunityPanel.Visibility = Visibility.Visible;
            EnterCommunity(kind, uploads ? "mine" : "for-car");
            if (enteringUploads) { var _ignore = CheckModerationNoticesAsync(); }
        }

        // Set true while the My-uploads mode is active, so the notice check fires
        // once per entry rather than on every ApplyView.
        private bool _wasUploads;

        // Pop the moderation-notices modal when entering My uploads, but only if
        // there's an unacknowledged open notice (force:false, so it doesn't nag).
        private System.Threading.Tasks.Task CheckModerationNoticesAsync()
            => ModerationNoticesWindow.MaybeShowAsync(_plugin, Window.GetWindow(this), force: false);

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
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Create pack")) return;
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
                    TrueforceDialog.Show(owner,
                        "Create pack",
                        "Pick a username before sharing (Account tab).",
                        DialogKind.Info);
                    return;
                }
                var dlg = new CreatePackWindow(_plugin) { Owner = owner };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Create pack failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't upload. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
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
            public string DetailsText   { get; set; }
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
                    DetailsText   = BuildPackDetailsText(p),
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
            var pack = row.Pack;
            var owner = Window.GetWindow(this);

            // Warn before clobbering existing defaults: overwrite all, skip only
            // the conflicting ones, or cancel. No prompt when nothing conflicts.
            var preview = _plugin.PreviewPackDefaults(pack);
            SetDefaultsConflictPolicy policy = SetDefaultsConflictPolicy.OverwriteAll;
            if (preview.ConflictCount > 0)
            {
                int total = preview.FreshCount + preview.ConflictCount;
                var choice = TrueforceDialog.ShowChoice(owner,
                    "Set pack as defaults",
                    $"This pack sets {total} default binding(s). {preview.ConflictCount} of them already have a default for that game or car.\n\n"
                    + "\"Overwrite all\" replaces every existing default. \"Skip existing\" keeps your current defaults and only fills in the ones you haven't set yet.",
                    primaryLabel:   "Overwrite all",
                    secondaryLabel: "Skip existing",
                    cancelLabel:    "Cancel");
                if (choice == DialogChoice.Cancel) return;
                policy = choice == DialogChoice.Primary
                    ? SetDefaultsConflictPolicy.OverwriteAll
                    : SetDefaultsConflictPolicy.SkipConflicts;
            }

            try
            {
                var summary = _plugin.SetPackAsDefaults(pack, policy);
                if (PacksStatusLabel != null) PacksStatusLabel.Text = FormatSetDefaultsSummary(summary);
                LibraryChanged?.Invoke();
            }
            catch (Exception ex)
            {
                if (PacksStatusLabel != null) PacksStatusLabel.Text = "Couldn't set the defaults. See the SimHub log, then try again.";
                TrueforceDialog.LogError("Set pack defaults", ex);
            }
        }

        // Mirrors PackManagerWindow.FormatSetDefaultsSummary so the inline Packs
        // pane produces the same result string as the standalone manager.
        private static string FormatSetDefaultsSummary(SetDefaultsSummary s)
        {
            if (s == null) return "";
            int gOver = s.GameDefaultsOverwritten;
            int cOver = s.CarDefaultsOverwritten;
            string overText = (gOver + cOver) > 0 ? $" (overwrote {gOver + cOver} existing)" : "";
            int kept = s.GameDefaultsSkippedConflict + s.CarDefaultsSkippedConflict;
            string keptText = kept > 0 ? $" · kept {kept} existing default(s)" : "";
            string skipText = s.GamePresetsSkipped > 0
                ? $" · skipped {s.GamePresetsSkipped} game preset(s) without a game mapping"
                : "";
            return $"Set defaults: {s.GameDefaultsSet} game, {s.CarDefaultsSet} car{overText}{keptText}{skipText}.";
        }

        private void PacksRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!(PacksList?.SelectedItem is PackRow row) || row.Pack == null || _plugin == null) return;
            var owner = Window.GetWindow(this);

            // What removing this pack would touch (computed before deleting).
            var impact  = _plugin.AnalyzePackRemoval(row.Pack);
            var options = new RemovePackOptions();

            // Initial confirm.
            if (TrueforceDialog.Show(owner,
                "Remove pack",
                $"Remove pack '{row.Name}'?\n\n"
                + "Every preset the pack installed will be deleted, except entries you've edited (those are preserved).",
                DialogKind.Destructive, okLabel: "Remove", cancelLabel: "Cancel") != true)
                return;

            // Edited entries: let the user keep their changes or remove them too.
            if (impact.EditedEntryCount > 0)
            {
                var choice = TrueforceDialog.ShowChoice(owner,
                    "Edited entries",
                    $"{impact.EditedEntryCount} item(s) in this pack have changes you made since downloading. "
                    + "Keep your edited copies, or remove them too?",
                    primaryLabel: "Keep edited", secondaryLabel: "Remove all", cancelLabel: "Cancel");
                if (choice == DialogChoice.Cancel) return;
                options.RemoveEditedEntries = choice == DialogChoice.Secondary;
            }

            // Shared engines: bundled engines still used outside this pack.
            if (impact.HasSharedEngines)
            {
                int packs   = 0, presets = 0;
                foreach (var se in impact.SharedEngines) { packs += se.OtherPackCount; presets += se.OutsidePresetRefs; }
                var who = new List<string>();
                if (presets > 0) who.Add($"{presets} preset(s) outside this pack");
                if (packs   > 0) who.Add($"{packs} other installed pack(s)");
                string n = impact.SharedEngines.Count == 1 ? "1 custom engine" : $"{impact.SharedEngines.Count} custom engines";

                var choice = TrueforceDialog.ShowChoice(owner,
                    "Shared custom engines",
                    $"{n} in this pack are also used by {string.Join(" and ", who)}. "
                    + "Keep these engines, or delete them? (Anything left pointing at a deleted engine switches to Auto.)",
                    primaryLabel: "Keep engines", secondaryLabel: "Delete engines", cancelLabel: "Cancel");
                if (choice == DialogChoice.Cancel) return;
                options.DeleteSharedEngines = choice == DialogChoice.Secondary;
            }

            RemovePackSummary summary;
            try
            {
                summary = _plugin.RemovePack(row.Pack, options);
            }
            catch (Exception ex)
            {
                if (PacksStatusLabel != null) PacksStatusLabel.Text = "Couldn't remove the pack. See the SimHub log, then try again.";
                TrueforceDialog.LogError("Remove pack", ex);
                return;
            }
            if (summary == null) return;
            if (PacksStatusLabel != null)
                PacksStatusLabel.Text = $"Removed pack. Deleted {summary.EntriesDeleted} entr{(summary.EntriesDeleted == 1 ? "y" : "ies")}; "
                    + $"kept {summary.EntriesKept} entr{(summary.EntriesKept == 1 ? "y" : "ies")}.";
            ReloadPacks();
            ReloadGames();
            ReloadCars();
            ReloadCustoms();
            LibraryChanged?.Invoke();
        }

        // (The per-segment Library|Community|My-uploads handlers + their shared
        // ApplySegmentMode were folded into the single global ApplyView when the
        // view mode became one overall control instead of one strip per segment.)

        // Point the shared CommunityPanel at a (kind, mode) pair and fetch
        // when the view actually changed or has nothing loaded. Folds in the
        // label / help / scope housekeeping the old in-panel mode radio did.
        // ---- Community access gate (enable + sign-in) ----------------------

        // Browse is account-required, so the community panel needs BOTH online
        // features on AND a signed-in account. When either is missing this shows
        // a centered empty-state with a button that clears the missing gate,
        // instead of an empty list or a bare status line. Returns true if the
        // gate is up (caller should not fetch).
        private bool ApplyCommunityGate()
        {
            bool enabled  = _plugin?.Settings?.CommunityEnabled == true;
            bool signedIn = _plugin?.AuthIsSignedIn == true;
            if (!enabled)
            {
                ShowCommunityGate(
                    "Community presets are off",
                    "Turn on community features to browse and download presets shared by other drivers.",
                    "Enable community features");
                return true;
            }
            if (!signedIn)
            {
                ShowCommunityGate(
                    "Sign in to browse community presets",
                    "Community presets need a free account. Sign in or create one to browse and download.",
                    "Sign in / Sign up");
                return true;
            }
            HideCommunityGate();
            return false;
        }

        private void ShowCommunityGate(string title, string body, string button)
        {
            if (CommunityGatePanel == null) return;
            if (CommunityGateTitle != null) CommunityGateTitle.Text = title;
            if (CommunityGateBody  != null) CommunityGateBody.Text  = body;
            if (CommunityGateBtn   != null) CommunityGateBtn.Content = button;
            CommunityGatePanel.Visibility = Visibility.Visible;
            // Hide the browser chrome behind the gate.
            SetVisible(CommunityHelpText,       false);
            SetVisible(CommunitySearchRow,      false);
            SetVisible(CommunityGameChips,      false);
            SetVisible(CommunityScopeRow,       false);
            SetVisible(CommunityActionsRow,     false);
            SetVisible(CommunityDescriptionRow, false);
            SetVisible(CommunityShowMoreBtn,    false);
            SetVisible(CommunityList,           false);
            _communityRows.Clear();
            CommunityList_SelectionChanged(null, null);
        }

        private void HideCommunityGate()
        {
            if (CommunityGatePanel == null) return;
            CommunityGatePanel.Visibility = Visibility.Collapsed;
            // Restore the always-on chrome; search/chips are set per kind by
            // ConfigureCommunityFilterVisibility and "Show more" by the fetch.
            SetVisible(CommunityHelpText,       true);
            SetVisible(CommunityScopeRow,       true);
            SetVisible(CommunityActionsRow,     true);
            SetVisible(CommunityDescriptionRow, true);
            SetVisible(CommunityList,           true);
        }

        private static void SetVisible(UIElement el, bool visible)
        {
            if (el != null) el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Gate button: clear whichever gate is up (turn community on, then sign
        // in if needed), then load the browser. The panel itself is the consent
        // surface, so we flip the toggle directly rather than re-prompting.
        private void CommunityGate_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null) return;
            var owner = Window.GetWindow(this);
            if (_plugin.Settings.CommunityEnabled != true)
                _plugin.SetCommunityEnabled(true);   // raises CommunityEnabledChanged
            if (_plugin.AuthIsSignedIn != true)
            {
                try
                {
                    var dlg = new SignInWindow(_plugin) { Owner = owner };
                    dlg.ShowDialog();
                }
                catch { /* a dialog failure shouldn't crash the panel */ }
            }
            // Re-render for the new state: loads the browser if both gates are now
            // cleared, or re-shows the sign-in gate if the user backed out.
            EnterCommunity(_communityKind ?? "car", _communityMode ?? "for-car");
        }

        // Host hook: re-evaluate the community access gate after an enable/disable
        // toggle OR a sign-in / sign-out, so the panel flips between the gate and
        // the browser live (no manual refresh or restart). Cheap + idempotent:
        // only (re)fetches on a gated->open transition or an empty list, so it's
        // safe to call from the frequent account-refresh path as well as the
        // toggle event.
        public void RefreshCommunityGate()
        {
            if (CommunityPanel == null || CommunityPanel.Visibility != Visibility.Visible) return;
            bool wasGated = CommunityGatePanel != null
                            && CommunityGatePanel.Visibility == Visibility.Visible;
            if (ApplyCommunityGate()) return;            // still / now gated -> gate shown
            // Now open. Reload only when we just left the gate (e.g. just signed
            // in) or have no rows yet, so frequent callers don't refetch.
            if (wasGated || _communityRows.Count == 0)
            {
                ConfigureCommunityFilterVisibility();    // restore search/chips per kind
                if (!_communityFetchInFlight) _ = CommunityRefreshAsync();
            }
        }

        private void EnterCommunity(string kind, string mode)
        {
            bool changed = _communityKind != kind || _communityMode != mode;
            _communityKind = kind;
            _communityMode = mode;
            // Reset trending so the scope chrome baselines cleanly until the
            // next fetch result overwrites it.
            _lastFetchWasTrending = false;
            // On a real view switch, clear the search box and re-default the
            // game filter to the active game (preserving them across an
            // unchanged re-entry so returning to the tab keeps your browse).
            if (changed) ResetCommunityFilters();
            // Gate on every view ENTRY, not just on fetch: community may have
            // been disabled (or the user signed out) while this panel was
            // hidden, where RefreshCommunityGate deliberately no-ops. Without
            // this, re-entering an unchanged view with cached rows skips the
            // fetch below - and with it the gate check at the top of
            // CommunityRefreshAsync - leaving the full browser (Download /
            // vote) live behind a cleared consent. Before the chrome
            // housekeeping so ConfigureCommunityFilterVisibility can't re-show
            // the search row over the gate.
            if (ApplyCommunityGate()) return;
            ConfigureCommunityFilterVisibility();
            if (CommunityScopeLabel != null)
                CommunityScopeLabel.Text = mode == "mine"
                    ? "Your uploads:"
                    : (kind == "game" ? "Active game:" : "Active car:");
            RelabelCommunityScopeRadio();
            UpdateCommunityActiveCarLabel();
            // The displayed list may belong to a car/game the user has since
            // switched away from: the active car can change while this panel
            // is hidden, where OnActiveCarChanged skips the refetch. Compare
            // the scope the rows represent against the live scope so a return
            // visit reloads the right list instead of stranding stale rows
            // until a manual refresh.
            bool scopeStale = !string.Equals(_communityListedCarKey,
                ExpectedCommunityListKey(), StringComparison.Ordinal);
            // On a real view change (or a stale scope) drop the prior rows
            // first so the empty state / share-CTA checks don't flash against
            // stale contents before the fetch repopulates.
            if (changed || scopeStale) _communityRows.Clear();
            if ((changed || scopeStale || _communityRows.Count == 0) && !_communityFetchInFlight)
                _ = CommunityRefreshAsync();
        }

        // True when the active-game filter is exactly the single live active
        // game (the un-broadened "for this car / this game" default).
        private bool IsDefaultGameScope()
        {
            string ag = _plugin?.ActiveGame;
            return !string.IsNullOrEmpty(ag)
                && _communitySelectedGames.Count == 1
                && _communitySelectedGames.Contains(ag);
        }

        // True when the user has widened past the active-car default, either by
        // typing a search or by changing the game filter. Engines/packs have no
        // game scope, so only a search broadens them. Mine-mode never broadens.
        private bool IsCommunityBroadened()
        {
            if (_communityMode == "mine") return false;
            if (!string.IsNullOrEmpty((_communitySearch ?? "").Trim())) return true;
            if (_communityKind == "engine" || _communityKind == "pack") return false;
            return !IsDefaultGameScope();
        }

        // Sorted snapshot of the selected games for stable list keys; empty
        // means "all games".
        private List<string> SelectedGamesSorted()
        {
            var list = new List<string>(_communitySelectedGames);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // The scope key the currently-displayed community list *should* carry
        // for the live state, mirroring how CommunityRefreshAsync stamps
        // _communityListedCarKey after a fetch. Used on panel re-entry to
        // detect that the active car/game (or the filters) changed while the
        // panel was hidden, so we reload instead of showing stale rows.
        private string ExpectedCommunityListKey()
        {
            string kind = _communityKind;
            if (_communityMode == "mine") return "mine/" + kind;
            if (IsCommunityBroadened())
                return "browse/" + kind + "/" + string.Join("+", SelectedGamesSorted())
                       + "/" + (_communitySearch ?? "").Trim();
            if (kind == "engine") return "engine";
            if (kind == "pack")   return "pack";
            string game = _plugin?.ActiveGame;
            string car  = _plugin?.ActiveCarId;
            bool scopeMissing = string.IsNullOrEmpty(game)
                || (kind != "game" && string.IsNullOrEmpty(car));
            if (scopeMissing)
                return kind == "game" ? "trending-game" : "trending-car";
            return kind == "game" ? "game/" + game : game + "/" + car;
        }

        // ---- Community search + game filter --------------------------------

        // Clear the search box (suppressing its event) and re-default the game
        // filter to the active game, then rebuild the chips. Called on a kind/
        // mode switch and when the active game changes under a default scope.
        private void ResetCommunityFilters()
        {
            // Kill any armed search debounce: _suppressSearchEvent only stops
            // the programmatic text-clear below from (re)starting the timer, it
            // does NOT stop one already armed by prior typing, which would
            // otherwise fire a redundant fetch ~350ms after the view switch.
            _communitySearchDebounce?.Stop();
            _suppressSearchEvent = true;
            try { if (CommunitySearchBox != null) CommunitySearchBox.Text = ""; }
            finally { _suppressSearchEvent = false; }
            _communitySearch = "";
            _communitySelectedGames.Clear();
            string ag = _plugin?.ActiveGame;
            if (!string.IsNullOrEmpty(ag)) _communitySelectedGames.Add(ag);
            RebuildCommunityGameChips();
        }

        // Search applies to every browse kind; the game chips only to car /
        // game presets. Both hidden in "mine" mode.
        private void ConfigureCommunityFilterVisibility()
        {
            bool mine = _communityMode == "mine";
            bool isCarOrGame = _communityKind == "car" || _communityKind == "game";
            if (CommunitySearchRow != null)
                CommunitySearchRow.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
            if (CommunityGameChips != null)
                CommunityGameChips.Visibility = (!mine && isCarOrGame)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RebuildCommunityGameChips()
        {
            if (CommunityGameChips == null || _plugin == null) return;
            CommunityGameChips.Children.Clear();
            var games = _plugin.GetCommunityFilterGames();

            var all = new CheckBox
            {
                Content   = "All games",
                Style     = (Style)FindResource("ToggleChipButton"),
                IsChecked = _communitySelectedGames.Count == 0,
                Tag       = null,
                ToolTip   = "Browse presets from every game.",
            };
            all.Click += CommunityGameChip_Click;
            CommunityGameChips.Children.Add(all);

            foreach (var g in games)
            {
                var chip = new CheckBox
                {
                    Content   = g,
                    Style     = (Style)FindResource("ToggleChipButton"),
                    IsChecked = _communitySelectedGames.Contains(g),
                    Tag       = g,
                };
                chip.Click += CommunityGameChip_Click;
                CommunityGameChips.Children.Add(chip);
            }
        }

        private void CommunityGameChip_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb)) return;
            string g = cb.Tag as string;   // null = "All games"
            if (g == null)
            {
                // "All games" -> clear specific picks (empty set = all).
                _communitySelectedGames.Clear();
            }
            else
            {
                if (cb.IsChecked == true) _communitySelectedGames.Add(g);
                else _communitySelectedGames.Remove(g);
            }
            // Sync chip check states (a specific pick clears All; clearing the
            // last specific pick re-selects All). Programmatic IsChecked sets
            // don't fire Click, so this won't recurse.
            bool allMode = _communitySelectedGames.Count == 0;
            foreach (var child in CommunityGameChips.Children)
            {
                if (!(child is CheckBox c)) continue;
                string tag = c.Tag as string;
                c.IsChecked = tag == null ? allMode : _communitySelectedGames.Contains(tag);
            }
            if (CommunityPanel?.Visibility == Visibility.Visible)
                _ = CommunityRefreshAsync();
        }

        private void CommunitySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchEvent) return;
            string q = CommunitySearchBox?.Text?.Trim() ?? "";
            // Minimum useful query: a 1-char term substring-matches a huge
            // slice of the car catalog (hundreds of resolved ids into the
            // request URL) for near-random results. Under 2 chars, treat as
            // no search. This single choke point keeps every consumer of
            // _communitySearch consistent (broaden check, list key, status
            // label, fetch capture, stale-fetch compare): a 1-char term
            // simply never broadens and no fetch fires (the == compare below
            // also suppresses the redundant 0->1-char refresh).
            if (q.Length < 2) q = "";
            if (q == _communitySearch) return;
            _communitySearch = q;
            // Debounce so a network search fires once after typing settles,
            // not once per keystroke.
            if (_communitySearchDebounce == null)
            {
                _communitySearchDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(350),
                };
                _communitySearchDebounce.Tick += (s2, e2) =>
                {
                    _communitySearchDebounce.Stop();
                    if (CommunityPanel?.Visibility == Visibility.Visible)
                        _ = CommunityRefreshAsync();
                };
            }
            _communitySearchDebounce.Stop();
            _communitySearchDebounce.Start();
        }

        private void CommunitySearchClear_Click(object sender, RoutedEventArgs e)
        {
            if (CommunitySearchBox != null) CommunitySearchBox.Text = "";  // fires TextChanged
        }

        // Keep the community panel's kind-dependent chrome in sync: the Car
        // ID column only applies to car presets, and the help text reflects
        // the active kind + mode. (The "For this car / All cars" scope radio
        // this used to relabel is gone; scope is now shown by the active-
        // car/game label row and the segment strip above.)
        private void RelabelCommunityScopeRadio()
        {
            // Car ID column is meaningful only for car presets; hide it
            // for the game/engine/pack kinds where every row's CarId is
            // empty (game presets are per-game, not per-car).
            if (CommunityCarIdCol != null)
                CommunityCarIdCol.Visibility = _communityKind == "car"
                    ? Visibility.Visible : Visibility.Collapsed;
            // Pack rows have no game / tier / effect-sections, but they do
            // have a bundled-preset count. Swap those columns out for the
            // Presets count so a pack row reads cleanly instead of as a row
            // of empty cells. Other kinds keep their normal column set.
            bool isPack = _communityKind == "pack";
            if (CommunityItemCountCol != null)
                CommunityItemCountCol.Visibility = isPack ? Visibility.Visible : Visibility.Collapsed;
            if (CommunityGameCol != null)
                CommunityGameCol.Visibility = isPack ? Visibility.Collapsed : Visibility.Visible;
            if (CommunityScopeCol != null)
                CommunityScopeCol.Visibility = isPack ? Visibility.Collapsed : Visibility.Visible;
            if (CommunitySectionsCol != null)
                CommunitySectionsCol.Visibility = isPack ? Visibility.Collapsed : Visibility.Visible;
            // The moderation Status column only makes sense for your own uploads.
            if (CommunityStateCol != null)
                CommunityStateCol.Visibility = _communityMode == "mine"
                    ? Visibility.Visible : Visibility.Collapsed;
            // When the last fetch fell back to "no scope" (no active
            // game/car), the help text says we're showing everything so it
            // matches what the list actually shows.
            bool unscoped = _lastFetchWasTrending && _communityMode == "for-car";
            if (CommunityHelpText != null)
            {
                // "Mine" mode gets its own help text that surfaces the
                // Edit/Delete affordance (those buttons are collapsed
                // until a row you own is selected, which is otherwise
                // hard to discover). Rows are already filtered to the
                // active segment's kind, so the copy stays kind-neutral.
                if (_communityMode == "mine")
                {
                    CommunityHelpText.Text =
                        "Your community uploads. Select a row to reveal Edit and Delete; "
                        + "use Edit to update a preset's name, description, or body without resetting its votes and downloads.";
                }
                else switch (_communityKind)
                {
                    case "game":
                        CommunityHelpText.Text = unscoped
                            ? "No game loaded, so showing every game preset the community has shared. Load a game and refresh to filter."
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
                        CommunityHelpText.Text = unscoped
                            ? "No car loaded, so showing every car preset the community has shared. Load a car in your game and refresh to filter."
                            : "Browse + download community presets for the car you're driving, or switch to your own uploads to manage them. "
                              + "Pick a row and Download to import; you'll get a section picker so you can take just the parts you want.";
                        break;
                }
            }
        }

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
                    if (c != null) _customRows.Add(new CustomRow { Def = c, DetailsText = BuildCustomDetailsText(c) });
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
        private static string BuildGameDetailsText(GameSettingsSnapshot snap, bool community = false)
        {
            if (snap == null) return "";
            var sb = new System.Text.StringBuilder();
            // FFB scale is personal: a shared preset carries no such value, so
            // omit the scale part from community previews. Master gain is a
            // global setting (not preset-scoped), so it is never shown per-preset.
            sb.AppendLine(community
                ? $"FFB pass-through: smooth {snap.FfbSmoothTimeConstantMs:0} ms, invert {(snap.FfbInvertSign ? "on" : "off")}"
                : $"FFB pass-through: scale {snap.FfbScale:0.##}, smooth {snap.FfbSmoothTimeConstantMs:0} ms, invert {(snap.FfbInvertSign ? "on" : "off")}");
            sb.AppendLine($"FFB spike reduction: {(snap.FfbSpikeTamingEnabled ? "on" : "off")}");
            if (snap.StationarySpringEnabled.HasValue)
                sb.AppendLine($"Stationary spring: {(snap.StationarySpringEnabled.Value ? "on" : "off")} (strength {(snap.StationarySpringStrength ?? 0):0.##})");
            AppendEffectLine(sb, "Audio rumble",    snap.AudioCapture);
            AppendEffectLine(sb, "Engine pulse",     snap.EnginePulse);
            AppendEffectLine(sb, "Road bumps",       snap.RoadBumps);
            AppendEffectLine(sb, "Traction loss",    snap.TractionLoss);
            AppendEffectLine(sb, "Axle slip",        snap.AxleSlip);
            AppendEffectLine(sb, "Kerb thump",       snap.KerbThump);
            AppendEffectLine(sb, "Lockup judder",    snap.LockupJudder);
            AppendEffectLine(sb, "Gear shift",       snap.GearShift);
            AppendEffectLine(sb, "ABS",              snap.AbsClick);
            AppendEffectLine(sb, "Pit limiter",      snap.PitLimiter);
            AppendEffectLine(sb, "DRS",              snap.Drs);
            AppendEffectLine(sb, "Collision",        snap.Collision);
            AppendEffectLine(sb, "Rev limiter",      snap.RevLimiter);
            AppendEffectLine(sb, "Airborne ducking", snap.Airborne);
            sb.AppendLine($"Sidechain ducking: {(snap.DuckingEnabled ? "on" : "off")} (depth {snap.DuckDepth:0.##})");
            return sb.ToString().TrimEnd();
        }

        private static void AppendEffectLine(System.Text.StringBuilder sb, string label, object eff)
        {
            if (eff == null) { sb.AppendLine($"{label}: (effect default)"); return; }
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
            AppendOverrideSection(sections, "Audio rumble",    ov.AudioCapture, baseline?.AudioCapture);
            AppendOverrideSection(sections, "Engine pulse",     ov.EnginePulse,  baseline?.EnginePulse);
            AppendOverrideSection(sections, "Road bumps",       ov.RoadBumps,    baseline?.RoadBumps);
            AppendOverrideSection(sections, "Traction loss",    ov.TractionLoss, baseline?.TractionLoss);
            AppendOverrideSection(sections, "Axle slip",        ov.AxleSlip,     baseline?.AxleSlip);
            AppendOverrideSection(sections, "Kerb thump",       ov.KerbThump,    baseline?.KerbThump);
            AppendOverrideSection(sections, "Lockup judder",    ov.LockupJudder, baseline?.LockupJudder);
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

        // Hover tooltip for the Custom engines library. Mirrors
        // BuildGame/CarDetailsText: humans-first summary of the engine's
        // shape so the user can see what they're about to load without
        // opening it. Electric / combustion gates the body.
        private static string BuildCustomDetailsText(CustomEngineDef def)
        {
            if (def == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Name: {(string.IsNullOrEmpty(def.Name) ? "(unnamed)" : def.Name)}");
            if (def.IsElectric)
            {
                string mode = def.ElectricMode == ElectricCarMode.Silent ? "Silent" : "Muted hum";
                sb.AppendLine($"Type: Electric ({mode})");
            }
            else
            {
                int pulses = 0;
                if (!string.IsNullOrWhiteSpace(def.Pattern))
                {
                    foreach (var ch in def.Pattern) if (ch == ',') pulses++;
                    pulses++;
                }
                sb.AppendLine($"Type: Combustion ({pulses} pulse{(pulses == 1 ? "" : "s")})");
                if (!string.IsNullOrWhiteSpace(def.Pattern))
                    sb.AppendLine($"Pattern: {def.Pattern}");
            }
            if (!string.IsNullOrEmpty(def.CommunityUploadedVersion))
                sb.AppendLine($"Your upload: {def.CommunityUploadedVersion}");
            return sb.ToString().TrimEnd();
        }

        // Hover tooltip for the installed-packs library. Shows the
        // shipping author/version + what the pack actually contains
        // so the user can size up the bundle without opening it.
        private static string BuildPackDetailsText(InstalledPack p)
        {
            if (p == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pack: {(string.IsNullOrEmpty(p.PackName) ? "(unnamed)" : p.PackName)}");
            if (!string.IsNullOrEmpty(p.Author))        sb.AppendLine($"Author: {p.Author}");
            if (!string.IsNullOrEmpty(p.AuthorVersion)) sb.AppendLine($"Version: {p.AuthorVersion}");
            int games = 0, cars = 0, engines = 0;
            if (p.Entries != null)
            {
                foreach (var e in p.Entries)
                {
                    if (e == null) continue;
                    if (e.Kind == InstalledPackEntry.KindGame) games++;
                    else if (e.Kind == InstalledPackEntry.KindCar) cars++;
                    else if (e.Kind == InstalledPackEntry.KindEngine) engines++;
                }
            }
            var parts = new List<string>
            {
                $"{games} game preset{(games == 1 ? "" : "s")}",
                $"{cars} car preset{(cars == 1 ? "" : "s")}",
            };
            if (engines > 0) parts.Add($"{engines} custom engine{(engines == 1 ? "" : "s")}");
            sb.AppendLine($"Entries: {(p.Entries?.Count ?? 0)} ({string.Join(", ", parts)})");
            if (p.ImportedAt != default(DateTime))
                sb.AppendLine($"Imported: {p.ImportedAt.ToLocalTime():yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                sb.AppendLine();
                sb.AppendLine(p.Description.Trim());
            }
            return sb.ToString().TrimEnd();
        }

        // Hover tooltip for the community browser. Renders summary
        // metadata (votes, downloads, scope, tags, description) plus
        // - when body != null - the per-section detail that matches
        // what the local-library tooltips show (gain values etc).
        // Bodies are lazy-fetched on first hover; the bare summary
        // text serves as the placeholder until the fetch lands.
        private static string BuildCommunityDetailsText(PresetSummary p, string tierBadge,
            Newtonsoft.Json.Linq.JObject body = null)
        {
            if (p == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Name: {(string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name)}");
            sb.AppendLine($"Author: {(string.IsNullOrEmpty(p.Author) ? "(anonymous)" : p.Author)}");
            int score = p.Upvotes - p.Downvotes;
            sb.AppendLine($"Score: {score} (▲{p.Upvotes} / ▼{p.Downvotes})   Downloads: {p.Downloads}");
            if (!string.IsNullOrEmpty(p.Game))   sb.AppendLine($"Game: {p.Game}");
            if (!string.IsNullOrEmpty(p.CarId))  sb.AppendLine($"Car ID: {p.CarId}");
            if (string.Equals(p.Kind, "game", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(tierBadge))
                sb.AppendLine($"Scope: {tierBadge}");
            if (string.Equals(p.Kind, "pack", StringComparison.OrdinalIgnoreCase))
            {
                if (p.EntryCount > 0)
                    sb.AppendLine($"Items: {p.EntryCount}");
                if (!string.IsNullOrEmpty(p.AuthorVersion))
                    sb.AppendLine($"Pack version: {p.AuthorVersion}");
            }
            if (p.EffectTags != null && p.EffectTags.Count > 0)
                sb.AppendLine($"Tags: {EffectTagLabels.JoinLabels(p.EffectTags)}");
            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                sb.AppendLine();
                sb.AppendLine(p.Description.Trim());
            }
            if (body != null)
            {
                string bodyText = RenderCommunityBodyDetail(p?.Kind, body);
                if (!string.IsNullOrEmpty(bodyText))
                {
                    sb.AppendLine();
                    sb.Append(bodyText);
                }
            }
            return sb.ToString().TrimEnd();
        }

        // Body -> per-section detail string, kind-routed:
        //   game   - parse body.snapshot as GameSettingsSnapshot, reuse BuildGameDetailsText
        //   car    - parse body.override  as CarOverride, render its non-null sections (no baseline diff)
        //   engine - parse body as CustomEngineDef, reuse BuildCustomDetailsText
        //   pack   - count entries per kind from body.game_presets / car_presets / custom_engines
        // Returns empty string on a body shape we don't recognize so the
        // caller's summary header still renders cleanly.
        private static string RenderCommunityBodyDetail(string kind, Newtonsoft.Json.Linq.JObject body)
        {
            if (body == null) return "";
            try
            {
                switch ((kind ?? "car").ToLowerInvariant())
                {
                    case "game":
                    {
                        var snapTok = body["snapshot"];
                        if (snapTok == null) return "";
                        var snap = snapTok.ToObject<GameSettingsSnapshot>();
                        return snap == null ? "" : BuildGameDetailsText(snap, community: true);
                    }
                    case "engine":
                    {
                        var def = body.ToObject<CustomEngineDef>();
                        return def == null ? "" : BuildCustomDetailsText(def);
                    }
                    case "pack":
                    {
                        var sb = new System.Text.StringBuilder();
                        int games   = (body["game_presets"]    as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                        int cars    = (body["car_presets"]     as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                        int engines = (body["custom_engines"]  as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                        sb.AppendLine("Contents:");
                        sb.AppendLine($"  Game presets: {games}");
                        sb.AppendLine($"  Car presets: {cars}");
                        sb.AppendLine($"  Custom engines: {engines}");
                        return sb.ToString().TrimEnd();
                    }
                    default:
                    {
                        var ovTok = body["override"];
                        if (ovTok == null) return "";
                        var ov = ovTok.ToObject<CarOverride>();
                        if (ov == null) return "";
                        var sb = new System.Text.StringBuilder();
                        var sections = new System.Text.StringBuilder();
                        AppendOverrideSection(sections, "Audio rumble",    ov.AudioCapture, null);
                        AppendOverrideSection(sections, "Engine pulse",     ov.EnginePulse,  null);
                        AppendOverrideSection(sections, "Road bumps",       ov.RoadBumps,    null);
                        AppendOverrideSection(sections, "Traction loss",    ov.TractionLoss, null);
                        AppendOverrideSection(sections, "Axle slip",        ov.AxleSlip,     null);
                        AppendOverrideSection(sections, "Kerb thump",       ov.KerbThump,    null);
                        AppendOverrideSection(sections, "Lockup judder",    ov.LockupJudder, null);
                        AppendOverrideSection(sections, "Gear shift",       ov.GearShift,    null);
                        AppendOverrideSection(sections, "ABS",              ov.AbsClick,     null);
                        AppendOverrideSection(sections, "Pit limiter",      ov.PitLimiter,   null);
                        AppendOverrideSection(sections, "DRS",              ov.Drs,          null);
                        AppendOverrideSection(sections, "Collision",        ov.Collision,    null);
                        AppendOverrideSection(sections, "Rev limiter",      ov.RevLimiter,   null);
                        AppendOverrideSection(sections, "Airborne ducking", ov.Airborne,     null);
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
                }
            }
            catch
            {
                // Don't surface a parse failure as a popup error - just
                // skip the body detail and leave the summary header.
                return "";
            }
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
            string text = (row as GameRow)?.DetailsText
                        ?? (row as CarRow)?.DetailsText
                        ?? (row as CustomRow)?.DetailsText
                        ?? (row as PackRow)?.DetailsText
                        ?? (row as CommunityRow)?.DetailsText;
            if (string.IsNullOrEmpty(text)) { HideDetailsPopup(); return; }

            if (!object.ReferenceEquals(_hoveredRow, row))
            {
                _hoveredRow = row;
                DetailsPopupText.Text = text;
                // Community rows: kick off a body lazy-fetch on first
                // hover so the tooltip can match local-library parity
                // (per-section on/off + gain). The Body fetch is async
                // so the popup shows the summary text immediately and
                // the richer body detail folds in when the fetch lands.
                MaybeStartCommunityBodyFetch(row as CommunityRow);
            }
            DetailsPopup.PlacementTarget = list;
            DetailsPopup.Placement = PlacementMode.Relative;
            var pos = e.GetPosition(list);
            DetailsPopup.HorizontalOffset = pos.X + 18;
            DetailsPopup.VerticalOffset   = pos.Y + 18;
            if (!DetailsPopup.IsOpen) DetailsPopup.IsOpen = true;
        }

        // Fire-and-forget: if this community row has no body yet and
        // no fetch is in flight, pull the body and rebuild its
        // DetailsText. Updates the open popup if the user is still
        // hovering the same row when the fetch lands.
        private async void MaybeStartCommunityBodyFetch(CommunityRow row)
        {
            if (row == null || _plugin == null) return;
            if (row.Body != null) return;
            if (row.BodyFetchInFlight) return;
            if (row.Summary == null || string.IsNullOrEmpty(row.Summary.Id)) return;
            row.BodyFetchInFlight = true;
            string id = row.Summary.Id;
            string kind = (row.Summary.Kind ?? _communityKind ?? "car").ToLowerInvariant();
            PresetFull full = null;
            try
            {
                full = await System.Threading.Tasks.Task.Run(() =>
                {
                    switch (kind)
                    {
                        case "game":   return _plugin.FetchCommunityGamePresetBody(id);
                        case "engine": return _plugin.FetchCommunityCustomEngineBody(id);
                        case "pack":   return _plugin.FetchCommunityPackBody(id);
                        default:       return _plugin.FetchCommunityPresetBody(id);
                    }
                });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Hover body fetch failed: " + ex.Message);
            }
            finally { row.BodyFetchInFlight = false; }
            if (full?.Body == null) return;
            // BuildCommunityDetailsText parses the server body JSON; guard the
            // post-await UI work so a malformed body can't escape this async
            // void handler (an escaped exception could tear down SimHub).
            try
            {
                row.Body = full.Body;
                row.DetailsText = BuildCommunityDetailsText(row.Summary, row.TierBadge, full.Body);
                // If the user is still hovering this same row, swap the
                // popup text in place so the body content appears without
                // them having to move off and back on.
                if (object.ReferenceEquals(_hoveredRow, row)
                    && DetailsPopup != null && DetailsPopup.IsOpen)
                {
                    DetailsPopupText.Text = row.DetailsText;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Hover body render failed: " + ex.Message);
            }
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
            // Context-aware refresh: while the shared community panel is up
            // (Community or My uploads), re-fetch the server list; otherwise
            // reload the local library from disk. One icon, the right action
            // per view (replaces the old separate community Refresh button).
            if (CommunityPanel != null && CommunityPanel.Visibility == Visibility.Visible)
            {
                _ = CommunityRefreshAsync(force: true);
                return;
            }
            try
            {
                _plugin.ReloadLibraryFromFolders();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[TF4ALL] Refresh library failed: {ex.Message}");
                TrueforceDialog.Show(Window.GetWindow(this),
                    "Refresh library",
                    "Refresh failed. Check the SimHub log for details.",
                    DialogKind.Warning);
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

        // A cloud restore/merge re-applied the library to live state; redraw all tabs so the user
        // doesn't have to hit Refresh library. Marshalled to the UI thread defensively (the event
        // fires from the restore apply path).
        private void OnLibraryReloaded()
        {
            if (_plugin == null) return;
            Action redraw = () =>
            {
                _initializing = true;
                try { ReloadGames(); ReloadCars(); ReloadCustoms(); }
                catch (Exception ex) { SimHub.Logging.Current.Warn($"[TF4ALL] Preset browser auto-refresh failed: {ex.Message}"); }
                finally { _initializing = false; }
            };
            var d = Dispatcher;
            if (d != null && !d.CheckAccess()) d.BeginInvoke(redraw);
            else redraw();
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
            // DEV authoring can delete built-ins too, so the deletable count
            // (Delete button label + enable) includes them in dev mode.
            int   checkedDeletable    = _devMode ? checkedCount : checkedNonBuiltin;
            bool  anySelected         = sel != null;
            bool  selUserPreset       = anySelected && !sel.Builtin;

            // DEV authoring lets the owner act on built-ins too (rename / delete).
            bool selEditable = selUserPreset || (_devMode && anySelected);
            GameEditBtn.IsEnabled         = anySelected   && checkedCount <= 1;
            GameRenameBtn.IsEnabled       = selEditable   && checkedCount <= 1;
            GameDuplicateBtn.IsEnabled    = anySelected   && checkedCount <= 1;
            GameDeleteBtn.IsEnabled       = checkedDeletable > 0 || selEditable;
            GameSetDefaultBtn.IsEnabled   = anySelected   && checkedCount <= 1;
            if (GameShareBtn != null)
            {
                // The button stays enabled even when community is off or the user
                // is signed out; the click funnel (EnsureShareGatesReady) turns
                // those gates into a "turn it on?" / sign-in prompt. Here we only
                // disable for per-preset reasons: no row, multi-select, built-in,
                // or an unchanged copy of the last upload.
                // Resolve the selected row's snapshot so we can read the
                // upload-tracking stamps written by StampGamePresetAsUploaded.
                GameSettingsSnapshot gShareSnap = null;
                if (anySelected
                    && _plugin?.Settings?.Presets != null
                    && !string.IsNullOrEmpty(sel.Name))
                    _plugin.Settings.Presets.TryGetValue(sel.Name, out gShareSnap);
                bool gShareHasPriorUpload = gShareSnap != null
                    && !string.IsNullOrEmpty(gShareSnap.CommunityUploadedById);
                bool gShareMatchesUpload = false;
                if (gShareHasPriorUpload
                    && !string.IsNullOrEmpty(gShareSnap.CommunityUploadedBodyHash))
                {
                    string gShareCurrentHash = PresetBodyHasher.ComputeGameSnapshotBodyHash(gShareSnap);
                    gShareMatchesUpload = string.Equals(
                        gShareCurrentHash, gShareSnap.CommunityUploadedBodyHash, StringComparison.Ordinal);
                }
                // Multi-checked + all eligible -> repurpose into "Share pack"
                // (parity with CarShareBtn). Bypass the single-row gating
                // and route to CreatePackWindow on click.
                bool gameBulkPackEligible = checkedCount >= 2 && checkedNonBuiltin == checkedCount;
                _gameShareIsPackMode = gameBulkPackEligible;
                if (gameBulkPackEligible)
                {
                    GameShareBtn.Content   = "Share pack";
                    GameShareBtn.IsEnabled = true;
                    GameShareBtn.ToolTip   = $"Bundle these {checkedCount} presets into a community pack.";
                }
                else
                {
                    GameShareBtn.Content = "Share";
                    GameShareBtn.IsEnabled = anySelected && checkedCount <= 1
                        && !sel.Builtin && !gShareMatchesUpload;
                    if (anySelected && sel.Builtin)
                        GameShareBtn.ToolTip = "Built-in presets ship with the plugin. Duplicate it to make your own version, then share that.";
                    else if (gShareMatchesUpload)
                        GameShareBtn.ToolTip = $"This matches your last upload ({gShareSnap.CommunityUploadedVersion ?? "v1"}). Edit it to share an update.";
                    else if (gShareHasPriorUpload)
                        GameShareBtn.ToolTip = "Update your last upload or share as new (click to choose).";
                    else
                        GameShareBtn.ToolTip = "Upload this game preset to the community so other drivers can find it.";
                }
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
            GameDeleteBtn.Content = checkedDeletable > 0 ? $"Delete ({checkedDeletable})" : "Delete";
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
            CarDuplicateBtn.IsEnabled = anySelected    && checkedCount <= 1;
            // Share-to-community: needs CommunityEnabled, a signed-in
            // session (server now requires it), and a row. The upload
            // itself also checks the backend URL + anon key; at the UI
            // layer the toggle + sign-in are the visible contract.
            // Additionally: presets the user downloaded from someone
            // else's community upload aren't shareable as their own --
            // gate by Override.CommunitySourceId (same identity model
            // as the header-bar Share buttons).
            bool isBuiltinSel = sel?.Builtin == true;
            // Resolve the selected row's CarOverride via the per-car
            // store so we can read its CommunitySourceId stamp and the
            // upload-tracking stamps written at successful upload time.
            bool isCommunitySourced = false;
            CarOverride carShareOvr = null;
            if (anySelected && checkedCount <= 1 && sel != null
                && !string.IsNullOrEmpty(sel.CarId)
                && !string.IsNullOrEmpty(sel.PresetName))
            {
                var perCar = _plugin?.GetCarPresets(sel.CarId);
                if (perCar != null && perCar.TryGetValue(sel.PresetName, out var entry)
                    && entry?.Override != null)
                {
                    carShareOvr = entry.Override;
                    if (!string.IsNullOrEmpty(entry.Override.CommunitySourceId))
                        isCommunitySourced = true;
                }
            }
            bool carShareHasPriorUpload = carShareOvr != null
                && !string.IsNullOrEmpty(carShareOvr.CommunityUploadedById);
            bool carShareMatchesUpload = false;
            if (carShareHasPriorUpload
                && !string.IsNullOrEmpty(carShareOvr.CommunityUploadedBodyHash))
            {
                string carShareCurrentHash = PresetBodyHasher.ComputeCarOverrideHash(carShareOvr);
                carShareMatchesUpload = string.Equals(
                    carShareCurrentHash, carShareOvr.CommunityUploadedBodyHash, StringComparison.Ordinal);
            }
            // Multi-checked + all eligible (not built-in) repurposes
            // Share into "Share pack" - the button feels like the
            // natural surface for "do something with this multi-select"
            // and pack-creation is exactly that. Eligibility is the
            // not-built-in gate; community-sourced rows without
            // AllowInPacks would just be silently dropped by the
            // CreatePackWindow filter so we don't need to test that
            // here.
            bool carBulkPackEligible = checkedCount >= 2 && checkedNonBuiltin == checkedCount;
            _carShareIsPackMode = carBulkPackEligible;
            if (carBulkPackEligible)
            {
                CarShareBtn.Content   = "Share pack";
                CarShareBtn.IsEnabled = true;
                CarShareBtn.ToolTip   = $"Bundle these {checkedCount} presets into a community pack.";
                return;
            }
            CarShareBtn.Content = "Share";
            CarShareBtn.IsEnabled = anySelected && checkedCount <= 1
                && !isCommunitySourced && !isBuiltinSel
                && !carShareMatchesUpload;
            if (isBuiltinSel)
                CarShareBtn.ToolTip = "Built-in presets ship with the plugin. Duplicate it to make your own version, then share that.";
            else if (isCommunitySourced)
                CarShareBtn.ToolTip = "Shared by another driver. Duplicate to make your own version and share that.";
            else if (carShareMatchesUpload)
                CarShareBtn.ToolTip = $"This matches your last upload ({carShareOvr.CommunityUploadedVersion ?? "v1"}). Edit it to share an update.";
            else if (carShareHasPriorUpload)
                CarShareBtn.ToolTip = "Update your last upload or share as new (click to choose).";
            else
                CarShareBtn.ToolTip = "Upload this car preset to the community so other drivers can find it.";
            // DEV authoring can delete built-in car presets, so the deletable
            // count (label + enable) includes them in dev mode.
            int carCheckedDeletable = _devMode ? checkedCount : checkedNonBuiltin;
            CarDeleteBtn.IsEnabled    = carCheckedDeletable > 0 || carSelEditable;
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
            CarDeleteBtn.Content = carCheckedDeletable > 0 ? $"Delete ({carCheckedDeletable})" : "Delete";
            if (!_bulkCheckInFlight)
                UpdateSelectAllHeader(CarSelectAllCheck, GetVisible<CarRow>(_carRows));
        }

        private void RefreshCustomButtons()
        {
            int   checkedCount = _customRows.Count(r => r.IsChecked);
            var   selCustom    = SelectedCustom;
            bool  any          = selCustom != null;

            CustomEditBtn.IsEnabled   = any && checkedCount <= 1;
            // Share is single-row, requires community on. The button
            // itself stays in the DOM; just enable when actionable.
            if (CustomShareBtn != null)
            {
                CustomEngineDef cuShareDef = selCustom?.Def;
                bool cuShareHasPriorUpload = cuShareDef != null
                    && !string.IsNullOrEmpty(cuShareDef.CommunityUploadedById);
                bool cuShareMatchesUpload = false;
                if (cuShareHasPriorUpload
                    && !string.IsNullOrEmpty(cuShareDef.CommunityUploadedBodyHash))
                {
                    string cuShareCurrentHash = PresetBodyHasher.ComputeCustomEngineHash(cuShareDef);
                    cuShareMatchesUpload = string.Equals(
                        cuShareCurrentHash, cuShareDef.CommunityUploadedBodyHash, StringComparison.Ordinal);
                }
                CustomShareBtn.IsEnabled = any && checkedCount <= 1
                    && !cuShareMatchesUpload;
                if (cuShareMatchesUpload)
                    CustomShareBtn.ToolTip = $"This matches your last upload ({cuShareDef.CommunityUploadedVersion ?? "v1"}). Edit it to share an update.";
                else if (cuShareHasPriorUpload)
                    CustomShareBtn.ToolTip = "Update your last upload or share as new (click to choose).";
                else
                    CustomShareBtn.ToolTip = "Upload this custom engine to the community.";
            }
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
            string newName = PromptForName("Rename preset", "New name:", sel.Name, name =>
                string.IsNullOrWhiteSpace(name) ? "Enter a name."
                : (name != sel.Name && _plugin.Settings?.Presets?.ContainsKey(name) == true)
                    ? $"A preset named '{name}' already exists." : null);
            if (newName == null) return;   // cancelled
            newName = newName.Trim();
            if (newName == sel.Name) return;
            if (!_plugin.RenamePreset(sel.Name, newName))
            {
                SetLib(GameLibStatus, "Couldn't rename. See the SimHub log.");
                return;
            }
            ReloadGames();
            SelectGameByName(newName);
            SetLib(GameLibStatus, $"Renamed to '{newName}'.");
        }

        private void GameDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null) return;
            // No name prompt: the duplicate self-names with the numeric-suffix
            // convention ("Name (2)"), so it can't collide, and the user
            // renames afterwards if they want something else (owner call
            // 2026-07-13).
            string newName = _plugin.DuplicatePreset(sel.Name);
            if (newName == null)
            {
                SetLib(GameLibStatus, "Couldn't duplicate. See the SimHub log.");
                return;
            }
            ReloadGames();
            SelectGameByName(newName);
            SetLib(GameLibStatus, $"Duplicated as '{newName}'.");
        }

        // Share a game preset selected in the manager. Mirrors
        // HeaderGameShare_Click in SettingsControl, but reads the snapshot
        // from the selected row rather than the active preset, so a user
        // can share any saved preset without first making it active.
        // Option C share funnel. The Share / Create-a-pack buttons stay enabled
        // even when the two community gates aren't met; instead of dead-ending, a
        // click converts each gate into the action that clears it. Community off ->
        // confirm, then flip it on (it's the networking master switch, so we ask
        // first). Signed out -> open the sign-in / sign-up modal. Returns false if
        // the user backs out of either step, so the caller aborts quietly.
        private bool EnsureShareGatesReady(Window owner, string title)
            => ShareGate.EnsureReady(owner, _plugin, title);

        private async void GameShare_Click(object sender, RoutedEventArgs e)
        {
            // Bulk-checked + all eligible -> the button is in pack mode
            // (see RefreshGameButtons), open CreatePackWindow with the
            // checked rows pre-ticked.
            if (_gameShareIsPackMode)
            {
                OpenSharePackWindowForGames(_gameRows
                    .Where(r => r.IsChecked && !r.Builtin)
                    .Select(r => r.Name)
                    .ToList());
                return;
            }
            var sel = SelectedGame;
            if (sel == null) return;
            await ShareGamePresetByNameAsync(sel.Name);
        }

        // Shared by GameShare_Click and the empty-state CTA in the
        // community panel. Sharing by name lets both the row-driven
        // path (selected row in the Game preset list) and the CTA
        // path (the user's currently-active game preset, surfaced
        // when the community list is empty) hit the same flow.
        private async System.Threading.Tasks.Task ShareGamePresetByNameAsync(string presetName)
        {
            if (_plugin == null || string.IsNullOrEmpty(presetName)) return;
            if (_shareInProgress) return;
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Share preset")) return;
            _shareInProgress = true;
            try
            {
                if (_plugin.Settings?.Presets == null
                    || !_plugin.Settings.Presets.TryGetValue(presetName, out var snap)
                    || snap == null)
                {
                    TrueforceDialog.Show(Window.GetWindow(this),
                        "Share preset",
                        $"Could not load game preset '{presetName}'.",
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
                var customs = _plugin.CollectReferencedCustomEngines(
                    new[] { snap }, null);
                if (customs != null && customs.Count > 0)
                    body["custom_engines"] = Newtonsoft.Json.Linq.JToken.FromObject(customs);

                var tags = new List<string>(14);
                if (snap.EnginePulse  != null) tags.Add("engine");
                if (snap.RevLimiter   != null) tags.Add("revlimiter");
                if (snap.RoadBumps    != null) tags.Add("roadbumps");
                if (snap.TractionLoss != null) tags.Add("tractionloss");
                if (snap.AxleSlip     != null) tags.Add("axleslip");
                if (snap.KerbThump    != null) tags.Add("kerbthump");
                if (snap.LockupJudder != null) tags.Add("lockupjudder");
                if (snap.GearShift    != null) tags.Add("gearshift");
                if (snap.AbsClick     != null) tags.Add("abs");
                if (snap.PitLimiter   != null) tags.Add("pitlimiter");
                if (snap.Drs          != null) tags.Add("drs");
                if (snap.Collision    != null) tags.Add("collision");
                if (snap.AudioCapture != null) tags.Add("audio");
                if (snap.Airborne     != null) tags.Add("airborne");

                string game = _plugin.ActiveGame ?? "";

                // Resolve share intent BEFORE constructing the share
                // dialog so a "share as new" path can carry a different
                // community-side name (the local preset name doesn't
                // change either way).
                string shareName = presetName;
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
                        string nextVer = NextVersionLabel(snap.CommunityUploadedVersion);
                        var chooser = new UpdateVsNewChooserWindow(
                            "Re-share '" + presetName + "'",
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
                            string suggested = presetName + " " + nextVer;
                            string newName = PromptForName("Share as a new preset",
                                "Community name for this new upload (your local preset's name stays the same):",
                                suggested);
                            if (string.IsNullOrWhiteSpace(newName)) return;
                            shareName = newName.Trim();
                        }
                    }
                    else
                    {
                        // Unchanged uploads never re-share (owner rule,
                        // 2026-07-13): it would only mint an identical
                        // duplicate row. No bypass on purpose; name and
                        // description edits go through Community > Edit….
                        TrueforceDialog.Show(owner, "Already shared",
                            "'" + presetName + "' is already shared to the community and hasn't changed since. Tweak the preset first, or use Edit… on your upload in the Community list to change its name or description.",
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
                        presetName, dialog.UploadedPresetId, finalHash,
                        dialog.UploadedContentVersion,
                        allowInPacks: dialog.UploadedAllowInPacks);
                    RefreshGameButtons();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Share game preset failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't share that preset. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
        }

        private void GameDelete_Click(object sender, RoutedEventArgs e)
        {
            // Bulk path: any checkboxes ticked = delete the whole set (built-
            // ins are filtered out since the plugin refuses them).
            // Any checkbox ticked = bulk mode: act on the checked deletable set
            // only, never fall through to the highlighted row (checking rows then
            // getting the highlighted one deleted surprised users). DEV may delete
            // built-ins, so include them in dev mode; otherwise they're filtered.
            bool anyChecked = _gameRows.Any(r => r.IsChecked);
            if (anyChecked)
            {
                var bulk = _gameRows.Where(r => r.IsChecked && (_devMode || !r.Builtin)).ToList();
                if (bulk.Count == 0) return;   // only built-ins checked outside dev
                int affected = bulk.SelectMany(r => r.Defaults).Distinct().Count();
                string detail = affected > 0
                    ? $"\n\n{affected} game default binding(s) will be cleared."
                    : "";
                string list = string.Join(", ", bulk.Take(10).Select(r => "'" + r.Name + "'"))
                    + (bulk.Count > 10 ? $" and {bulk.Count - 10} more" : "");
                if (TrueforceDialog.Show(Window.GetWindow(this), "Delete presets", $"Delete {bulk.Count} preset(s)?\n\n{list}{detail}",
                    DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel") != true) return;
                foreach (var r in bulk) _plugin.DeletePreset(r.Name);
                ReloadGames();
                SetLib(GameLibStatus, $"Deleted {bulk.Count} preset(s).");
                return;
            }

            var sel = SelectedGame;
            if (sel == null || (sel.Builtin && !_devMode)) return;   // DEV may delete built-ins
            string warning = sel.Defaults.Count > 0
                ? $"Delete preset '{sel.Name}'?\n\nIt's currently the default for: {string.Join(", ", sel.Defaults)}. Those games will lose their auto-load binding."
                : $"Delete preset '{sel.Name}'?";
            if (TrueforceDialog.Show(Window.GetWindow(this), "Delete preset", warning, DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel")
                != true) return;
            string deleted = sel.Name;
            _plugin.DeletePreset(sel.Name);
            ReloadGames();
            SetLib(GameLibStatus, $"Deleted '{deleted}'.");
        }

        // Export / Import: routed through SettingsControl's shared flow so this
        // matches the Backup & sync buttons. Owner = the host window, so the
        // pack picker / metadata dialog / file pickers sit above the panel.
        private void DialogExport_Click(object sender, RoutedEventArgs e)
        {
            var saved = SettingsControl.RunExportFlow(Window.GetWindow(this), _plugin);
            if (!string.IsNullOrEmpty(saved))
                SettingsControl.ShowSavedStatus(IoStatusLabel,
                    "Exported to " + System.IO.Path.GetFileName(saved) + ".", saved);
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
                // bindings and may delete preset files; rebuild all tabs incl.
                // the installed-packs grid so a removed pack drops immediately.
                ReloadGames();
                ReloadCars();
                ReloadCustoms();
                ReloadPacks();
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
                SetLib(GameLibStatus, "No games seen yet. Launch a game once so SimHub registers it, then bind a default.");
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
            SetLib(GameLibStatus, $"Updated default games for '{sel.Name}'.");
        }

        private void GameEdit_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedGame;
            if (sel == null) return;
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
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(carId)) return "";
            // 1. User-renamed CarFacts name wins (explicit override).
            string key = game + "/" + carId;
            if (_plugin?.Settings?.CarFacts != null
                && _plugin.Settings.CarFacts.TryGetValue(key, out var bundle)
                && bundle != null && !string.IsNullOrEmpty(bundle.CarName))
                return bundle.CarName;
            // 2. Catalog fallback (FH5 cylinder-table names / FH6 name-only
            //    table) so rows show a real name even before any rename. Not
            //    persisted; the user can still Rename to override.
            if (BuiltinCarCylinders.TryGetDisplayName(game, carId, out var catalogName))
                return catalogName;
            // 3. Auto-derive from underscore-separated carIds (AC modder
            //    filenames like "bmw_m3_e30" → "Bmw M3 E30"). Skips ordinal
            //    patterns like "Car_2267" where we have no real info; those
            //    stay blank so the row reads as "needs a name."
            string derived = TryDeriveReadableCarName(carId);
            return derived ?? "";
        }

        // Title-case an underscore/hyphen-separated carId into a readable
        // name. Returns null for ordinal patterns (Car_2267, etc.) so they
        // stay blank rather than pretending we know the car. AC, ACC, AMS2
        // and most non-Forza titles use descriptive carIds that benefit
        // from this; Forza ordinals do not.
        private static string TryDeriveReadableCarName(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return null;
            // Ordinal pattern (Car_\d+): blank cell instead of "Car 2267"
            // so unnamed cars are visually distinct from named ones.
            if (System.Text.RegularExpressions.Regex.IsMatch(carId,
                    @"^Car_\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return null;
            var tokens = carId.Split(new[] { '_', '-' },
                System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                string t = tokens[i];
                if (t.Length == 0) continue;
                // Capitalize first letter; rest as-is so abbreviations
                // like "GT" and "RX" stay uppercase when the modder
                // already wrote them that way.
                sb.Append(char.ToUpperInvariant(t[0]));
                if (t.Length > 1) sb.Append(t.Substring(1));
            }
            return sb.ToString();
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
            // Bulk-checked + all eligible: the button is in pack mode
            // (see RefreshCarButtons), so open CreatePackWindow with
            // the checked rows pre-ticked.
            if (_carShareIsPackMode)
            {
                OpenSharePackWindowForCars(_carRows
                    .Where(r => r.IsChecked && !r.Builtin)
                    .Select(r => new KeyValuePair<string, string>(r.CarId, r.PresetName))
                    .ToList());
                return;
            }
            var sel = SelectedCar;
            if (sel == null) return;
            await ShareCarPresetByNameAsync(sel.CarId, sel.PresetName, sel.GameName);
        }

        // Tracks whether CarShareBtn is currently relabeled as "Share
        // pack" so its click handler knows to route to the pack flow
        // instead of the single-row share flow. Flipped in
        // RefreshCarButtons based on the multi-select gate.
        private bool _carShareIsPackMode;
        private bool _gameShareIsPackMode;

        // Open the create-pack modal with the given car rows pre-checked.
        // Shares the same gate (CommunityEnabled + username) as the
        // standalone CreatePack_Click entry point so the experience is
        // identical from either trigger.
        private async void OpenSharePackWindowForCars(
            List<KeyValuePair<string, string>> carRows)
        {
            if (_plugin == null || carRows == null || carRows.Count == 0) return;
            if (_shareInProgress) return;
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Share pack")) return;
            _shareInProgress = true;
            try
            {
                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    TrueforceDialog.Show(owner,
                        "Share pack",
                        "Pick a username before sharing (Account tab).",
                        DialogKind.Info);
                    return;
                }
                var dlg = new CreatePackWindow(_plugin) { Owner = owner };
                dlg.PrecheckCarPresets(carRows);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Share pack (car bulk) failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't share that pack. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
        }

        // Same flow as the car bulk path but for the Games segment.
        private async void OpenSharePackWindowForGames(List<string> gamePresetNames)
        {
            if (_plugin == null || gamePresetNames == null || gamePresetNames.Count == 0) return;
            if (_shareInProgress) return;
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Share pack")) return;
            _shareInProgress = true;
            try
            {
                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    TrueforceDialog.Show(owner,
                        "Share pack",
                        "Pick a username before sharing (Account tab).",
                        DialogKind.Info);
                    return;
                }
                var dlg = new CreatePackWindow(_plugin) { Owner = owner };
                dlg.PrecheckGamePresets(gamePresetNames);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Share pack (game bulk) failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't share that pack. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
        }

        // Shared by CarShare_Click and the empty-state CTA. The CTA
        // passes the currently-active car's binding (carId from
        // ActiveCarId, presetName from CarDefaults, game from
        // ActiveGame) so a user looking at an empty community list
        // can publish their own tune in one click.
        private async System.Threading.Tasks.Task ShareCarPresetByNameAsync(string carId, string presetName, string gameName)
        {
            if (_plugin == null || string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(presetName)) return;
            if (_shareInProgress) return;
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Share preset")) return;
            _shareInProgress = true;
            try
            {
                var perCar = _plugin.GetCarPresets(carId);
                if (perCar == null
                    || !perCar.TryGetValue(presetName, out var entry)
                    || entry == null
                    || entry.Override == null)
                {
                    TrueforceDialog.Show(Window.GetWindow(this),
                        "Share preset",
                        $"Could not load preset '{presetName}' for car '{carId}'.",
                        DialogKind.Warning);
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
                if (entry.Override.AxleSlip     != null) tags.Add("axleslip");
                if (entry.Override.KerbThump    != null) tags.Add("kerbthump");
                if (entry.Override.LockupJudder != null) tags.Add("lockupjudder");
                if (entry.Override.GearShift    != null) tags.Add("gearshift");
                if (entry.Override.AbsClick     != null) tags.Add("abs");
                if (entry.Override.PitLimiter   != null) tags.Add("pitlimiter");
                if (entry.Override.Drs          != null) tags.Add("drs");
                if (entry.Override.Collision    != null) tags.Add("collision");
                if (entry.Override.AudioCapture != null) tags.Add("audio");
                if (entry.Override.Airborne     != null) tags.Add("airborne");

                string resolvedGame = string.IsNullOrEmpty(gameName)
                    ? entry.GameName ?? _plugin.ActiveGame ?? ""
                    : gameName;
                string carDisplay = ResolveCarNameForRow(resolvedGame, carId);
                if (string.IsNullOrEmpty(carDisplay)) carDisplay = carId;

                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    TrueforceDialog.Show(owner,
                        "Share preset",
                        "Pick a username before sharing (Account tab).",
                        DialogKind.Info);
                    return;
                }
                string shareName = presetName;
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
                        string nextVer = NextVersionLabel(entry.Override.CommunityUploadedVersion);
                        var chooser = new UpdateVsNewChooserWindow(
                            "Re-share '" + presetName + "'",
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
                            existingUploadId = entry.Override.CommunityUploadedById;
                        }
                        else
                        {
                            string suggested = presetName + " " + nextVer;
                            string newName = PromptForName("Share as a new preset",
                                "Community name for this new upload (your local preset's name stays the same):",
                                suggested);
                            if (string.IsNullOrWhiteSpace(newName)) return;
                            shareName = newName.Trim();
                        }
                    }
                    else
                    {
                        // Unchanged uploads never re-share (owner rule,
                        // 2026-07-13); see the game-preset handler.
                        TrueforceDialog.Show(owner, "Already shared",
                            "'" + presetName + "' is already shared to the community and hasn't changed since. Tweak the preset first, or use Edit… on your upload in the Community list to change its name or description.",
                            DialogKind.Info);
                        return;
                    }
                }

                var dialog = PresetShareWindow.ForCar(
                    _plugin, shareName, resolvedGame,
                    carId, carDisplay, body, tags);
                dialog.Owner = owner;
                dialog.IsUpdate = isUpdatePath;
                dialog.ExistingUploadId = existingUploadId;

                bool? ok = dialog.ShowDialog();
                if (ok == true && !string.IsNullOrEmpty(dialog.UploadedPresetId))
                {
                    string finalHash = PresetBodyHasher.ComputeCarOverrideHash(entry.Override);
                    _plugin.StampCarPresetAsUploaded(
                        carId, presetName,
                        dialog.UploadedPresetId, finalHash,
                        dialog.UploadedContentVersion,
                        allowInPacks: dialog.UploadedAllowInPacks);
                    RefreshCarButtons();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Share preset failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't share that preset. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
        }

        // CTA click handler. EmptyShareCtaBtn.Tag carries an
        // EmptyShareCtaPayload set by UpdateEmptyShareCta describing
        // what to share for the current empty-state scope.
        private async void EmptyShareCta_Click(object sender, RoutedEventArgs e)
        {
            var payload = EmptyShareCtaBtn?.Tag as EmptyShareCtaPayload;
            if (payload == null) return;
            if (payload.Kind == "game")
                await ShareGamePresetByNameAsync(payload.PresetName);
            else if (payload.Kind == "car")
                await ShareCarPresetByNameAsync(payload.CarId, payload.PresetName, payload.GameName);
        }

        private sealed class EmptyShareCtaPayload
        {
            public string Kind;        // "game" or "car"
            public string PresetName;
            public string CarId;       // car only
            public string GameName;    // car only
        }

        // Compute + apply visibility for the empty-state share CTA.
        // Shown when the community list is empty for a scope where
        // the user already has a local non-builtin preset they could
        // share - i.e. they likely have tuning others would benefit
        // from. Quietly hidden otherwise.
        private void UpdateEmptyShareCta(string kind, string mode, bool trending,
                                         string scopeGame, string scopeCar)
        {
            if (EmptyShareCtaBtn == null) return;
            EmptyShareCtaBtn.Visibility = System.Windows.Visibility.Collapsed;
            EmptyShareCtaBtn.Tag = null;
            if (_communityRows.Count > 0) return;
            if (mode == "mine") return;
            if (trending) return;
            if (_plugin == null) return;
            if (_plugin.Settings?.CommunityEnabled != true) return;
            if (_plugin.AuthIsSignedIn != true) return;

            EmptyShareCtaPayload payload = null;
            string label = null;
            if (kind == "game")
            {
                if (string.IsNullOrEmpty(_plugin.ActiveGame)) return;
                string activeName = _plugin.ActivePresetName;
                if (string.IsNullOrEmpty(activeName)) return;
                if (_plugin.IsBuiltinPreset(activeName)) return;
                payload = new EmptyShareCtaPayload { Kind = "game", PresetName = activeName };
                label = "Share your '" + ShortenForCta(activeName) + "' tune";
            }
            else if (kind == "car")
            {
                string activeCar = _plugin.ActiveCarId;
                string activeGame = _plugin.ActiveGame;
                if (string.IsNullOrEmpty(activeCar) || string.IsNullOrEmpty(activeGame)) return;
                if (!string.Equals(activeGame, scopeGame, StringComparison.Ordinal)) return;
                if (!string.Equals(activeCar,  scopeCar,  StringComparison.Ordinal)) return;
                string activeName = _plugin.GetActiveCarPresetName(activeCar);
                if (string.IsNullOrEmpty(activeName)) return;
                var perCar = _plugin.GetCarPresets(activeCar);
                if (perCar == null
                    || !perCar.TryGetValue(activeName, out var entry)
                    || entry == null
                    || entry.IsBuiltin)
                    return;
                payload = new EmptyShareCtaPayload
                {
                    Kind = "car", PresetName = activeName,
                    CarId = activeCar, GameName = activeGame,
                };
                label = "Share your '" + ShortenForCta(activeName) + "' tune";
            }
            else return;

            EmptyShareCtaBtn.Tag = payload;
            EmptyShareCtaBtn.Content = label;
            EmptyShareCtaBtn.Visibility = System.Windows.Visibility.Visible;
        }

        // Bound the preset name in the empty-state share CTA so a long name
        // doesn't blow out (the button auto-sizes to its text now) or clip.
        private static string ShortenForCta(string name)
            => (name != null && name.Length > 30) ? name.Substring(0, 29) + "…" : name;

        // (The per-car "Set name…" button was removed from the library row:
        // it duplicated the header card's Rename, which already appears for the
        // edited car during Edit. Car naming now lives there + the active card.
        // The shared CarNameShareFlow is still used by those surfaces.)

        private void CarRename_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null) return;
            // Built-in rename is DEV-only; the plugin write-throughs to factory.
            if (sel.Builtin && !_devMode) return;
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in _carRows)
                if (string.Equals(r.CarId, sel.CarId, StringComparison.Ordinal))
                    existing.Add(r.PresetName);
            string newName = PromptForName("Rename car preset",
                $"New name for '{sel.CarId}' preset:", sel.PresetName, name =>
                    string.IsNullOrWhiteSpace(name) ? "Enter a name."
                    : (name != sel.PresetName && existing.Contains(name))
                        ? $"A preset named '{name}' already exists for this car." : null);
            if (newName == null) return;   // cancelled
            newName = newName.Trim();
            if (newName == sel.PresetName) return;
            if (!_plugin.RenameCarPreset(sel.CarId, sel.PresetName, newName))
            {
                SetLib(CarLibStatus, "Couldn't rename. See the SimHub log.");
                return;
            }
            ReloadCars();
            SelectCarRow(sel.CarId, newName);
            SetLib(CarLibStatus, $"Renamed to '{newName}'.");
        }

        private void CarDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedCar;
            if (sel == null) return;
            // No name prompt: self-names with the numeric-suffix convention;
            // see GameDuplicate_Click (owner call 2026-07-13).
            string newName = _plugin.DuplicateCarPreset(sel.CarId, sel.PresetName);
            if (newName == null)
            {
                SetLib(CarLibStatus, "Couldn't duplicate. See the SimHub log.");
                return;
            }
            ReloadCars();
            SelectCarRow(sel.CarId, newName);
            SetLib(CarLibStatus, $"Duplicated as '{newName}'.");
        }

        private void CarDelete_Click(object sender, RoutedEventArgs e)
        {
            // Bulk mode when any row is checked: act on the checked deletable set
            // only, never fall through to the highlighted row. DEV may delete
            // built-in car presets; otherwise they're filtered out.
            bool anyChecked = _carRows.Any(r => r.IsChecked);
            if (anyChecked)
            {
                var bulk = _carRows.Where(r => r.IsChecked && (_devMode || !r.Builtin)).ToList();
                if (bulk.Count == 0) return;   // only built-ins checked outside dev
                int active = bulk.Count(r => r.Active);
                string detail = active > 0
                    ? $"\n\n{active} of the selected preset(s) are currently the default for their car. Those cars will fall back to their built-in default or globals."
                    : "";
                if (TrueforceDialog.Show(Window.GetWindow(this),
                    "Delete car presets",
                    $"Delete {bulk.Count} car preset(s)?{detail}",
                    DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel") != true) return;
                foreach (var r in bulk) _plugin.DeleteCarPreset(r.CarId, r.PresetName);
                ReloadCars();
                SetLib(CarLibStatus, $"Deleted {bulk.Count} car preset(s).");
                return;
            }

            var sel = SelectedCar;
            if (sel == null || (sel.Builtin && !_devMode)) return;   // DEV may delete built-in car presets
            string warning = sel.Active
                ? $"Delete preset '{sel.PresetName}' for car '{sel.CarId}'?\n\nIt's currently the default for this car; the car will fall back to its built-in default (or globals)."
                : $"Delete preset '{sel.PresetName}' for car '{sel.CarId}'?";
            if (TrueforceDialog.Show(Window.GetWindow(this), "Delete car preset", warning,
                DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel") != true) return;
            string deleted = sel.PresetName;
            _plugin.DeleteCarPreset(sel.CarId, sel.PresetName);
            ReloadCars();
            SetLib(CarLibStatus, $"Deleted '{deleted}'.");
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
                SetLib(CarLibStatus, $"Set '{sel.PresetName}' as this car's default.");
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
                SetLib(CarLibStatus, "Each car can have one default. Uncheck the extra rows for: " + string.Join(", ", collisions));
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
                string suffix = alreadyActive > 0 ? $" ({alreadyActive} already the default)" : "";
                SetLib(CarLibStatus, $"Set {applied} preset(s) as their car's default{suffix}.");
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
            if (!EnsureShareGatesReady(Window.GetWindow(this), "Share custom engine")) return;
            _shareInProgress = true;
            try
            {
                var owner = Window.GetWindow(this);
                if (!await PickUsernameWindow.EnsureUsernameBeforeShareAsync(_plugin, owner))
                {
                    TrueforceDialog.Show(owner,
                        "Share custom engine",
                        "Pick a username before sharing (Account tab).",
                        DialogKind.Info);
                    return;
                }
                var body = Newtonsoft.Json.Linq.JObject.FromObject(row.Def);
                var def = row.Def;
                string baseName = def.Name ?? "Custom engine";

                string shareName = baseName;
                bool   isUpdatePath = false;
                string existingUploadId = null;
                bool userOwnsUpload = !string.IsNullOrEmpty(def.CommunityUploadedById)
                    && string.Equals(def.CommunityUploadedByUserId,
                                     _plugin.AuthSignedInUserId, StringComparison.Ordinal);
                if (userOwnsUpload)
                {
                    string currentHash = PresetBodyHasher.ComputeCustomEngineHash(def);
                    bool bodyChanged = !string.Equals(currentHash,
                        def.CommunityUploadedBodyHash, StringComparison.Ordinal);
                    if (bodyChanged)
                    {
                        string nextVer = NextVersionLabel(def.CommunityUploadedVersion);
                        var chooser = new UpdateVsNewChooserWindow(
                            "Re-share '" + baseName + "'",
                            "You already uploaded this engine to the community. Update your existing upload, or share a fresh copy as a new engine?",
                            "Update existing (" + nextVer + ")",
                            "Share as new engine")
                        {
                            Owner = owner,
                        };
                        bool? pick = chooser.ShowDialog();
                        if (pick != true) return;
                        if (chooser.IsUpdate)
                        {
                            isUpdatePath = true;
                            existingUploadId = def.CommunityUploadedById;
                        }
                        else
                        {
                            string suggested = baseName + " " + nextVer;
                            string newName = PromptForName("Share as a new engine",
                                "Community name for this new upload (your local engine's name stays the same):",
                                suggested);
                            if (string.IsNullOrWhiteSpace(newName)) return;
                            shareName = newName.Trim();
                        }
                    }
                    else
                    {
                        // Unchanged uploads never re-share (owner rule,
                        // 2026-07-13); see the game-preset handler.
                        TrueforceDialog.Show(owner, "Already shared",
                            "'" + baseName + "' is already shared to the community and hasn't changed since. Tweak the engine first, or use Edit… on your upload in the Community list to change its name or description.",
                            DialogKind.Info);
                        return;
                    }
                }

                var dialog = PresetShareWindow.ForEngine(_plugin, shareName, body);
                dialog.Owner = owner;
                dialog.IsUpdate = isUpdatePath;
                dialog.ExistingUploadId = existingUploadId;

                bool? ok = dialog.ShowDialog();
                if (ok == true && !string.IsNullOrEmpty(dialog.UploadedPresetId))
                {
                    string finalHash = PresetBodyHasher.ComputeCustomEngineHash(def);
                    _plugin.StampCustomEngineAsUploaded(
                        def.Id, dialog.UploadedPresetId, finalHash,
                        dialog.UploadedContentVersion,
                        allowInPacks: dialog.UploadedAllowInPacks);
                    RefreshCustomButtons();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Share custom engine failed: " + ex.Message);
                TrueforceDialog.ShowError(Window.GetWindow(this),
                    "Couldn't share that custom engine. Check your connection and try again.", ex);
            }
            finally { _shareInProgress = false; }
        }

        private void CustomDelete_Click(object sender, RoutedEventArgs e)
        {
            // Bulk mode when any row is checked: act on the checked set only,
            // never fall through to the highlighted row.
            bool anyChecked = _customRows.Any(r => r.IsChecked);
            List<CustomRow> targets = anyChecked
                ? _customRows.Where(r => r.IsChecked && r.Def != null).ToList()
                : (SelectedCustom?.Def != null ? new List<CustomRow> { SelectedCustom } : null);
            if (targets == null || targets.Count == 0) return;

            DeleteCustomEnginesWithConfirm(targets);
        }

        // Confirm + delete custom engines, surfacing how many presets currently
        // use them. Deleted engines no longer leave presets silent: any preset /
        // override / active config that referenced one falls back to Auto engine
        // mode (DeleteCustomEngines does the eager rewrite + live re-apply).
        private void DeleteCustomEnginesWithConfirm(List<CustomRow> targets)
        {
            if (targets == null || targets.Count == 0 || _plugin == null) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in targets) if (r?.Def?.Id != null) ids.Add(r.Def.Id);
            if (ids.Count == 0) return;

            int usageTotal = 0;
            foreach (var id in ids) usageTotal += _plugin.GetEngineUsage(id).TotalPresetRefs;

            bool one = targets.Count == 1;
            string title = one ? "Delete custom engine" : "Delete custom engines";
            string head  = one
                ? $"Delete custom engine '{targets[0].Def.Name}'?"
                : $"Delete {targets.Count} custom engine(s)?";
            string usageClause = usageTotal > 0
                ? $"\n\n{usageTotal} preset(s) use {(one ? "it" : "them")}. "
                  + $"Those presets will switch to Auto engine mode until you pick another."
                : "";

            if (TrueforceDialog.Show(Window.GetWindow(this),
                title, head + usageClause,
                DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel")
                != true) return;

            _plugin.DeleteCustomEngines(ids);
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
        // "v1" -> "v2", "v17" -> "v18", null/garbage -> "v1". Used by the
        // Update-vs-Share-as-new chooser to display the version the user
        // will land on, not the one they came from.
        internal static string NextVersionLabel(string currentVersion)
        {
            if (string.IsNullOrEmpty(currentVersion)) return "v1";
            string trimmed = currentVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? currentVersion.Substring(1)
                : currentVersion;
            if (!int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int n))
                return "v1";
            return "v" + (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // validate: optional. Given the trimmed input, returns an error string to
        // show inline (dialog stays open) or null if the name is acceptable. Used
        // to reject a blank/colliding name in-place instead of a follow-up modal.
        internal string PromptForName(string title, string label, string defaultValue,
            Func<string, string> validate = null)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                MinHeight = 150,
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
            var err = new TextBlock
            {
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x6C, 0x6C)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
            };
            sp.Children.Add(err);
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
            ok.Click += (s, args) =>
            {
                string error = validate != null ? validate((tb.Text ?? "").Trim()) : null;
                if (error != null)
                {
                    err.Text = error;
                    err.Visibility = Visibility.Visible;
                    tb.Focus(); tb.SelectAll();
                    return;   // keep the dialog open so the user can fix the name
                }
                result = tb.Text;
                win.DialogResult = true;
            };
            win.Loaded += (s, args) => { tb.Focus(); tb.SelectAll(); };
            return win.ShowDialog() == true ? result : null;
        }

        private static void SetLib(TextBlock label, string msg) { if (label != null) label.Text = msg; }

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
                                          ? "" : EffectTagLabels.JoinLabels(Summary.EffectTags);
            // Pack-only: bundled item count (presets + custom engines) for the
            // "Items" column. Blank for non-pack rows (column hidden anyway).
            public string PackItemCount =>
                string.Equals(Summary?.Kind, "pack", StringComparison.OrdinalIgnoreCase)
                    ? (Summary?.EntryCount ?? 0).ToString()
                    : "";
            public string Description => Summary?.Description ?? "";
            // Moderation state for "My uploads" rows. "live" for everything in
            // browse mode; suppressed own-uploads carry removed/suspended/under_review.
            public string State { get; set; } = "live";
            public string NoticeId { get; set; }   // latest moderation notice, for the fix/appeal jump
            public bool Appealable { get; set; } = true;  // false = final removal (no review path)
            public bool IsModerated => !string.Equals(State, "live", StringComparison.OrdinalIgnoreCase);
            public string StateLabel
            {
                get
                {
                    switch ((State ?? "live").ToLowerInvariant())
                    {
                        case "removed":      return "Removed";
                        case "suspended":    return "Suspended";
                        case "under_review": return "Under review";
                        default:             return "";
                    }
                }
            }
            // Grey out moderated rows so removed/suspended items read as inactive.
            public double RowOpacity => IsModerated ? 0.55 : 1.0;
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
            // Cursor-following details popup, populated from the
            // PresetSummary the row wraps when each fetch lands. Mirrors
            // the GameRow/CarRow pattern. Replaced by a richer
            // body-driven build the first time the user hovers and the
            // body lazy-fetch lands.
            public string DetailsText { get; set; }
            // Lazy-fetched full body. Null until first hover triggers
            // FetchCommunity*Body; once set the row's DetailsText is
            // upgraded to match the local-library tooltip parity (per-
            // section on/off + gain for game/car presets).
            public Newtonsoft.Json.Linq.JObject Body { get; set; }
            // Guard so a slow hover doesn't kick off N concurrent body
            // fetches for the same row.
            public bool BodyFetchInFlight { get; set; }
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
        // Community search box state. Non-empty = a cross-scope (all games /
        // all cars) name+author search, which doubles as cross-car browsing.
        // Empty = the normal active-car/game scoped browse.
        private string _communitySearch = "";
        // Debounce so we fire one network search after typing settles, not
        // one per keystroke.
        private System.Windows.Threading.DispatcherTimer _communitySearchDebounce;
        // Set while we programmatically clear the search box (on segment /
        // sub-tab switch) so its TextChanged handler doesn't kick off a
        // second fetch on top of the one EnterCommunity already runs.
        private bool _suppressSearchEvent;
        // Game filter for the community browse. Empty = all games. The default
        // on entering a car/game community view is { active game }, which keeps
        // the "for this car" behavior; widening it (or typing a search) flips
        // the view into a cross-car browse.
        private readonly HashSet<string> _communitySelectedGames
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            // Refresh the local lists so the row for the newly-active car
            // (or the new game's presets) appears without the user having
            // to hit Refresh library by hand. Cheap (in-memory rebuild
            // from _plugin.GetAllCarPresets and Settings.Presets); only
            // the community fetch involves network I/O.
            if (_plugin != null)
            {
                ReloadCars();
                if (gameAlsoChanged) ReloadGames();
            }
            if (CommunityPanel == null
                || CommunityPanel.Visibility != Visibility.Visible
                || _plugin == null)
                return;
            // Mine mode is car-independent; a broadened browse/search is scoped
            // to the chosen games, not the active car. Neither reacts here.
            if (_communityMode == "mine" || IsCommunityBroadened())
                return;
            // Default (per-car) scope: keep the game filter tracking the new
            // active game so the chips + default scope stay correct.
            if (gameAlsoChanged) ResetCommunityFilters();
            bool shouldRefresh =
                _communityKind == "car"
                || (_communityKind == "game" && gameAlsoChanged);
            if (shouldRefresh)
                _ = CommunityRefreshAsync();
        }

        // Host hook for "the user just saved or downloaded a preset that
        // could change the rows the manager shows." Always cheap: an
        // in-memory rebuild from the plugin's current state. The host
        // calls this from preset-save / preset-download / preset-delete
        // paths so the rows reflect reality without a manual refresh.
        public void OnLocalLibraryChanged()
        {
            if (_plugin == null) return;
            ReloadGames();
            ReloadCars();
            ReloadCustoms();
        }

        private void UpdateCommunityActiveCarLabel()
        {
            if (CommunityCarLabel == null || _plugin == null) return;
            bool isGameKind = _communityKind == "game";
            // Broadened browse/search: the active-car scope no longer drives
            // the list, so the label reflects what's actually being shown.
            if (_communityMode != "mine" && IsCommunityBroadened())
            {
                if (CommunityScopeLabel != null) CommunityScopeLabel.Text = "Browsing:";
                var sel = SelectedGamesSorted();
                string scope = sel.Count == 0 ? "all games" : string.Join(", ", sel);
                string term = (_communitySearch ?? "").Trim();
                CommunityCarLabel.Text = term.Length > 0 ? $"\"{term}\" in {scope}" : scope;
                return;
            }
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

        private void CommunityShowMore_Click(object sender, RoutedEventArgs e)
        {
            // Load the next page. Appends to the list and grows the browse
            // cache for this view. Offset = rows the server has returned so
            // far (NOT _communityRows.Count, which is post-dedup and drifts
            // low whenever a page overlap was deduped away).
            _ = CommunityRefreshAsync(offset: _communityServerOffset);
        }

        // (Community refresh is now driven by the shared context-aware
        // RefreshLibrary_Click icon; the for-car vs My-uploads mode is
        // driven by the per-segment strip via EnterCommunity. The old
        // CommunityRefresh_Click + CommunityMode_Changed handlers were
        // removed when their controls were folded into the segment strip.)

        private string SortKey()
        {
            switch (_communitySort)
            {
                case "newest":    return "newest";
                case "downloads": return "downloads";
                default:          return "for-car" == _communityMode ? "wilson" : "top";
            }
        }

        // Browse page size. Entering a view pulls the first page; "Show more"
        // pulls another page and the browse cache grows to hold the
        // accumulated list.
        private const int CommunityPageSize = 25;

        // Rows the SERVER has handed us for the current view (accumulated raw
        // page sizes). "Show more" must page by this, not _communityRows.Count:
        // the rendered rows are post-dedup, so after any dedup drop the row
        // count under-counts the true server offset and the next page
        // re-requests rows that dedup away again ("Show more does nothing").
        private int _communityServerOffset;

        // A scope/segment/search/game-filter switch while a fetch is in flight
        // suppresses the switch's own fetch (the in-flight guard at the top of
        // CommunityRefreshAsync), and the original fetch then discards itself as
        // stale. With nothing left to load the new scope, the panel would strand
        // on "Loading..." forever. Re-fire here, but via the dispatcher so it
        // runs AFTER the current call unwinds and its finally has cleared the
        // in-flight latch (a synchronous re-fire would set the latch only to
        // have the unwinding finally clear it out from under the new fetch).
        private void ReissueCommunityFetchAfterUnwind(bool force)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => { _ = CommunityRefreshAsync(force); }));
        }

        private async Task CommunityRefreshAsync(bool force = false, int offset = 0)
        {
            if (_plugin == null || _communityFetchInFlight) return;
            UpdateCommunityActiveCarLabel();
            // Gate: community off OR signed out -> show the enable / sign-in
            // empty-state instead of fetching. Browse requires an account, so
            // both conditions block the list (covers every refresh call path).
            if (ApplyCommunityGate()) return;

            string game  = _plugin.ActiveGame;
            string carId = _plugin.ActiveCarId;
            // Scope requirements differ by kind: car needs game+car;
            // game needs only game; engine + pack are global (no scope).
            bool isGameKind   = _communityKind == "game";
            bool isEngineKind = _communityKind == "engine";
            bool isPackKind   = _communityKind == "pack";
            // Broaden: a search term and/or a widened game filter flips the
            // panel from the active-car default into a cross-car browse across
            // the selected games. The active-car scope (and its trending
            // fallback) only apply when NOT broadened.
            bool broaden = IsCommunityBroadened();
            string searchTerm = (_communitySearch ?? "").Trim();
            // Empty selection = all games; otherwise the chosen set.
            var selectedGames = SelectedGamesSorted();
            List<string> gamesFilter = selectedGames.Count == 0 ? null : selectedGames;

            // Trending fallback: for-car mode normally needs a game+car
            // (or just game for game-kind), but when none is loaded the
            // panel used to dead-end. Engines + packs already browse
            // globally, so the asymmetry read as broken. Detect the
            // empty-scope case and pivot to a cross-game trending fetch
            // instead of bailing. Not used once broadened (browse handles
            // a missing game via an empty game filter).
            bool useTrendingFallback = false;
            if (_communityMode == "for-car" && !isEngineKind && !isPackKind && !broaden)
            {
                bool scopeMissing = string.IsNullOrEmpty(game)
                    || (!isGameKind && string.IsNullOrEmpty(carId));
                if (scopeMissing) useTrendingFallback = true;
            }

            _communityFetchInFlight = true;
            // Safety net: once the in-flight latch is set, ALWAYS release it in
            // the finally below, even if the post-fetch UI build throws. Without
            // this a single exception anywhere downstream would wedge the latch
            // true and freeze the whole browser (refresh / switch / search /
            // vote) until a plugin restart. The catch keeps the panel from
            // sitting stuck on "Loading..." after an unexpected failure.
            try
            {
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Loading...";
            StartRefreshSpin();

            string capturedMode = _communityMode;
            string capturedKind = _communityKind;
            string capturedGame = game;
            string capturedCar  = carId;
            string capturedSort = SortKey();
            string capturedSearch = searchTerm;
            bool capturedTrending = useTrendingFallback;
            bool capturedBroaden = broaden;
            var capturedGames = selectedGames;
            List<PresetSummary> results = null;
            try
            {
                if (capturedMode == "mine")
                {
                    // Mine-mode goes through an auth-bearer fetch, so
                    // call the async variant directly instead of
                    // wrapping a sync-over-async in Task.Run. The RPC
                    // returns every kind unioned together; filter to
                    // the active segment so toggling to My Uploads on
                    // the Games tab shows only game-preset uploads,
                    // not the whole bundle.
                    var all = await _plugin.FetchMyCommunityPresetsAsync(capturedSort, 100);
                    if (all != null)
                    {
                        string wantKind = string.IsNullOrEmpty(capturedKind) ? "car" : capturedKind;
                        results = all.Where(s => string.Equals(
                            string.IsNullOrEmpty(s?.Kind) ? "car" : s.Kind,
                            wantKind, StringComparison.Ordinal)).ToList();
                    }
                }
                else if (capturedBroaden)
                {
                    // Cross-car browse / search across the selected games.
                    results = await Task.Run(() =>
                        _plugin.BrowseOrSearchCommunity(capturedKind, capturedSearch, gamesFilter,
                            capturedSort, CommunityPageSize, offset));
                }
                else
                {
                    results = await Task.Run(() =>
                    {
                        if (capturedTrending)
                        {
                            // Trending fallback - cross-game, no car/game filter.
                            return capturedKind == "game"
                                ? _plugin.FetchCommunityTrendingGamePresets(capturedSort, CommunityPageSize, force, offset)
                                : _plugin.FetchCommunityTrendingCarPresets(capturedSort, CommunityPageSize, force, offset);
                        }
                        switch (capturedKind)
                        {
                            case "game":
                                return _plugin.FetchCommunityGamePresetsForGame(capturedGame, capturedSort, CommunityPageSize, force, offset);
                            case "engine":
                                return _plugin.FetchCommunityCustomEngines(capturedSort, CommunityPageSize, force, offset);
                            case "pack":
                                return _plugin.FetchCommunityPacks(capturedSort, CommunityPageSize, force, offset);
                            default:
                                return _plugin.FetchCommunityPresetsForCar(capturedGame, capturedCar, capturedSort, CommunityPageSize, force, offset);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Couldn't load the community list. Check your connection, then Refresh.";
                TrueforceDialog.LogError("Community fetch", ex);
                _communityFetchInFlight = false;
                return;
            }

            // Stale-fetch guard: if context shifted mid-flight, drop
            // the result. Car-kind cares about both game + car; game-
            // kind only about game; engine + pack + mine-mode about
            // neither. Trending: discard if a game/car has since
            // populated, since the next refresh would be scoped and
            // showing global rows would confuse the reader.
            if (capturedMode == "for-car" && capturedKind != "engine" && capturedKind != "pack" && !capturedBroaden)
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
                        ReissueCommunityFetchAfterUnwind(force);
                        return;
                    }
                }
                else if (capturedKind == "game"
                    ? _plugin.ActiveGame != capturedGame
                    : (_plugin.ActiveGame != capturedGame || _plugin.ActiveCarId != capturedCar))
                {
                    _communityFetchInFlight = false;
                    ReissueCommunityFetchAfterUnwind(force);
                    return;
                }
            }
            // Drop result if mode / kind / search / game-filter changed
            // mid-flight (a newer fetch is, or will be, in flight).
            if (_communityMode != capturedMode || _communityKind != capturedKind
                || !string.Equals((_communitySearch ?? "").Trim(), capturedSearch, StringComparison.Ordinal)
                || !string.Join("+", SelectedGamesSorted()).Equals(string.Join("+", capturedGames), StringComparison.Ordinal))
            {
                _communityFetchInFlight = false;
                ReissueCommunityFetchAfterUnwind(force);
                return;
            }
            // Gate re-check: community may have been disabled or the user
            // signed out while the fetch was in flight (RefreshCommunityGate
            // raised the gate under us). The entry check at the top can't see
            // that, and committing here would repopulate the rows and re-show
            // the Show-more button OVER the gate. Discard instead - no
            // reissue, there's nothing to fetch while gated. ApplyCommunityGate
            // (rather than a raw flag check) also renders the gate for any
            // path where the state flipped without the host hook firing.
            if (ApplyCommunityGate())
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
            _communityListedCarKey = capturedMode == "mine" ? "mine/" + capturedKind
                : capturedBroaden          ? "browse/" + capturedKind + "/" + string.Join("+", capturedGames) + "/" + capturedSearch
                : capturedKind == "engine" ? "engine"
                : capturedKind == "pack"   ? "pack"
                : capturedTrending         ? (capturedKind == "game" ? "trending-game" : "trending-car")
                : capturedKind == "game"   ? "game/" + capturedGame
                : capturedGame + "/" + capturedCar;
            // Fresh browse / refresh (offset 0) replaces the list; a load-more
            // (offset > 0) keeps the existing rows and appends.
            if (offset == 0) _communityRows.Clear();
            if (results == null)
            {
                if (CommunityStatusLabel != null)
                    // Load-more failure keeps the rows already on screen, so don't
                    // claim a hard outage; only the initial/refresh case is empty.
                    CommunityStatusLabel.Text = offset > 0
                        ? $"{_communityRows.Count} preset(s) found (couldn't load more right now)."
                        : "Could not reach the community backend.";
                CommunityList_SelectionChanged(null, null);
                _communityFetchInFlight = false;
                return;
            }
            // The size of THIS page (the new rows on a load-more, or the whole list
            // on an offset-0 cache hit) drives the Show-more affordance below. Capture
            // it before game-kind re-ranking reassigns 'results' to the full set.
            int pageCount = results.Count;
            // Track the true server-side row count for paging (see field note).
            // On an offset-0 cache hit results is the accumulated list, so the
            // assignment (not +=) is still the correct next-page offset.
            if (offset == 0) _communityServerOffset = pageCount;
            else             _communityServerOffset += pageCount;
            // Game-kind: the target_games tier ranking (tier 0 = matches the active
            // game) must order the WHOLE accumulated list, not just the new page, or
            // a tier-0 row from page 2 would sit below tier-2 rows from page 1. So on
            // a load-more, re-rank existing + new together and rebuild; otherwise the
            // server's within-tier sort is preserved.
            if (capturedKind == "game")
            {
                if (offset > 0)
                {
                    var combined = new List<PresetSummary>(_communityRows.Count + results.Count);
                    foreach (var r in _communityRows) if (r?.Summary != null) combined.Add(r.Summary);
                    combined.AddRange(results);
                    results = combined;
                    _communityRows.Clear();   // rebuild from the re-ranked combined set
                }
                results = RankGamePresetsByTargetGames(results, _plugin?.ActiveGame);
            }
            // Dedup by Id so a concurrent server shift between pages can't render the
            // same preset twice (defensive; the id sort tiebreaker handles the
            // stable case).
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in _communityRows) if (r?.Summary?.Id != null) present.Add(r.Summary.Id);
            foreach (var s in results)
            {
                if (s == null) continue;
                if (s.Id != null && !present.Add(s.Id)) continue;
                var row = new CommunityRow { Summary = s };
                if (capturedKind == "game")
                    row.TierBadge = ComputeTierBadge(s, _plugin?.ActiveGame);
                row.DetailsText = BuildCommunityDetailsText(s, row.TierBadge);
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

            // Mine mode: append the user's SUPPRESSED uploads of this kind (the
            // normal listing hides them) as greyed, state-tagged rows that open
            // the fix/appeal flow when selected. get_my_uploads unions every kind.
            if (capturedMode == "mine" && _plugin.AuthIsSignedIn)
            {
                List<ModerationClient.MyUpload> mine = null;
                try { mine = await _plugin.GetMyUploadsAsync(); } catch { /* leave list as-is */ }
                if (mine != null)
                {
                    string seg = string.IsNullOrEmpty(capturedKind) ? "car" : capturedKind;
                    // segment vocab (car/game/engine/pack) -> get_my_uploads vocab.
                    string rpcKind = seg == "car" ? "preset"
                                   : seg == "game" ? "game_preset"
                                   : seg == "engine" ? "custom_engine" : seg;
                    foreach (var u in mine)
                    {
                        if (u == null || string.Equals(u.State, "live", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(u.Kind, rpcKind, StringComparison.Ordinal)) continue;
                        if (u.Id != null && !present.Add(u.Id)) continue;
                        var summary = new PresetSummary
                        {
                            Id = u.Id, Name = u.Name, Description = u.Description,
                            Downloads = u.Downloads, Kind = seg,
                            EntryCount = u.EntryCount,   // packs: real item count (0 for other kinds)
                        };
                        _communityRows.Add(new CommunityRow { Summary = summary, State = u.State, NoticeId = u.NoticeId, Appealable = u.Appealable });
                    }
                }
            }

            if (CommunityStatusLabel != null)
            {
                string emptyMsg;
                string foundMsg;
                if (capturedBroaden)
                {
                    bool hasQuery = capturedSearch.Length > 0;
                    emptyMsg = hasQuery
                        ? $"No matches for \"{capturedSearch}\"."
                        : "No community presets for the selected games.";
                    foundMsg = hasQuery
                        ? $"{_communityRows.Count} result(s) for \"{capturedSearch}\"."
                        : $"{_communityRows.Count} result(s).";
                }
                else
                {
                    if (capturedMode == "mine")
                        emptyMsg = capturedKind == "game"
                            ? "You haven't uploaded any game presets yet."
                            : capturedKind == "engine"
                                ? "You haven't uploaded any custom engines yet."
                                : capturedKind == "pack"
                                    ? "You haven't uploaded any packs yet."
                                    : "You haven't uploaded any car presets yet.";
                    else if (capturedTrending)
                        emptyMsg = capturedKind == "game"
                            ? "No community game presets shared yet."
                            : capturedKind == "engine"
                                ? "No community custom engines shared yet."
                                : capturedKind == "pack"
                                    ? "No community packs shared yet."
                                    : "No community car presets shared yet.";
                    else
                        emptyMsg = capturedKind == "game"
                            ? "No community game presets yet. Be the first to share."
                            : capturedKind == "engine"
                                ? "No community custom engines yet. Be the first to share."
                                : capturedKind == "pack"
                                    ? "No community packs yet. Be the first to share."
                                    : "No community presets for this car yet. Be the first to share.";
                    foundMsg = capturedTrending
                        ? $"{_communityRows.Count} preset(s) found (load a game/car to filter)."
                        : $"{_communityRows.Count} preset(s) found.";
                }
                CommunityStatusLabel.Text = _communityRows.Count == 0 ? emptyMsg : foundMsg;
            }
            // The "be the first to share for this car" CTA only makes sense in
            // the active-car default view, not a broadened browse/search.
            if (capturedBroaden)
            {
                if (EmptyShareCtaBtn != null)
                {
                    EmptyShareCtaBtn.Visibility = Visibility.Collapsed;
                    EmptyShareCtaBtn.Tag = null;
                }
            }
            else
            {
                UpdateEmptyShareCta(capturedKind, capturedMode, capturedTrending, capturedGame, capturedCar);
            }
            // "Show more" is offered when this page came back full (so there may be
            // another page) and we're not in mine-mode (a fixed unioned pull). On a
            // cache hit at offset 0, results is the accumulated list, which keeps
            // the affordance until a page comes back short.
            if (CommunityShowMoreBtn != null)
                CommunityShowMoreBtn.Visibility =
                    (capturedMode != "mine" && pageCount >= CommunityPageSize)
                        ? Visibility.Visible : Visibility.Collapsed;
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
            if (offset == 0
                && capturedKind == "car"
                && capturedMode == "for-car"
                && !capturedTrending
                && !capturedBroaden
                && _plugin != null
                && string.Equals(capturedGame, _plugin.ActiveGame, StringComparison.Ordinal)
                && string.Equals(capturedCar,  _plugin.ActiveCarId, StringComparison.Ordinal))
            {
                CarCommunityListRefreshed?.Invoke();
            }
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Couldn't load the community list. Check your connection, then Refresh.";
                TrueforceDialog.LogError("Community list", ex);
            }
            finally
            {
                _communityFetchInFlight = false;
                StopRefreshSpin();
            }
        }

        // Continuous rotation of the Refresh glyph while a community fetch is in
        // flight, so a slow load shows visible motion instead of looking frozen.
        // (Library refreshes are synchronous disk reloads, so they never spin.)
        private void StartRefreshSpin()
        {
            if (RefreshGlyphRotate == null) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                new System.Windows.Duration(TimeSpan.FromSeconds(0.9)))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            RefreshGlyphRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, anim);
        }

        private void StopRefreshSpin()
        {
            if (RefreshGlyphRotate == null) return;
            RefreshGlyphRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            RefreshGlyphRotate.Angle = 0;
        }

        // Grey out moderated own-upload rows by dimming the realized row container
        // directly. Done here (LoadingRow) rather than via a DataGridRow style so the
        // theme's hover / selection accent (orange) survives on Community / My-uploads
        // rows the same way it does on the Library lists. Fires again on container
        // recycle, so a recycled row's opacity always matches its current item.
        private void CommunityList_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Opacity = (e.Row.Item as CommunityRow)?.RowOpacity ?? 1.0;
        }

        private void CommunityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = SelectedCommunity;
            bool has = sel != null && sel.Summary != null;
            if (CommunityDescriptionText != null)
                CommunityDescriptionText.Text = has && !string.IsNullOrWhiteSpace(sel.Summary.Description)
                    ? sel.Summary.Description
                    : "";
            // A moderated own-upload row (removed/suspended/under review) is
            // hidden from the community, so the browse actions don't apply; it
            // gets the Fix/appeal button instead.
            bool moderated = has && sel.IsModerated;
            // Fix/appeal only when there's actually a review path: not already
            // under review, and not a final (non-appealable) removal.
            bool canAppeal = moderated && sel.Appealable
                && !string.Equals(sel.State, "under_review", StringComparison.OrdinalIgnoreCase);
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
            // Report is for OTHER people's content: never your own upload (the
            // server rejects self-reports too) and never a moderated/hidden row.
            if (CommunityReportBtn   != null) CommunityReportBtn.IsEnabled   = has && !moderated && !ownsRow;
            // Download is a consume action; hide it in "My uploads" (a manage
            // view) since you authored these. To re-pull one onto a new machine,
            // it still appears, downloadable, in the Community browse.
            bool mineScope = _communityMode == "mine";
            if (CommunityDownloadBtn != null)
            {
                CommunityDownloadBtn.Visibility = mineScope ? Visibility.Collapsed : Visibility.Visible;
                CommunityDownloadBtn.IsEnabled  = has && !moderated && !mineScope;
            }
            if (CommunityFixAppealBtn != null)
            {
                CommunityFixAppealBtn.Visibility = canAppeal ? Visibility.Visible : Visibility.Collapsed;
                CommunityFixAppealBtn.IsEnabled  = canAppeal;
            }

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

        // Open the moderation modal so the user can fix + request review of a
        // removed/suspended own upload. force:true so it opens even if they
        // already acknowledged. Refresh the list after, since an appeal flips
        // the row to "under review".
        private async void CommunityFixAppeal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ModerationNoticesWindow.MaybeShowAsync(_plugin, Window.GetWindow(this), force: true).ConfigureAwait(true);
                if (CommunityPanel?.Visibility == Visibility.Visible) _ = CommunityRefreshAsync(force: true);
            }
            catch { /* never let the moderation modal break the list */ }
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

            // Fall back to the active segment kind (like delete/report do) so a
            // browse-result row with an empty Summary.Kind edits the right table.
            string kind = sel.Summary.Kind ?? _communityKind ?? "car";
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
                    CommunityStatusLabel.Text = "Couldn't save your changes. Check your connection and try again.";
                TrueforceDialog.LogError("Community update", ex);
                return;
            }
            if (!success)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Update failed (sign in expired or permission denied).";
                return;
            }
            // Drop the cached browse list so the edited name/body don't linger
            // from cache on the next open (parity with the delete path).
            _plugin.InvalidateBrowseCacheForKind(kind, sel.Summary.Game, sel.Summary.CarId);
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
                SimHub.Logging.Current.Info("[TF4ALL] CommunityEdit failed: " + ex.Message);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Couldn't save your changes. Check your connection and try again.";
                TrueforceDialog.LogError("Community edit", ex);
            }
        }

        // Reusable: compute effect_tags from a CarOverride's non-null
        // sections. Both upload + edit need it.
        private static List<string> BuildEffectTags(CarOverride ovr)
        {
            var tags = new List<string>(14);
            if (ovr == null) return tags;
            if (ovr.EnginePulse  != null) tags.Add("engine");
            if (ovr.RevLimiter   != null) tags.Add("revlimiter");
            if (ovr.RoadBumps    != null) tags.Add("roadbumps");
            if (ovr.TractionLoss != null) tags.Add("tractionloss");
            if (ovr.AxleSlip     != null) tags.Add("axleslip");
            if (ovr.KerbThump    != null) tags.Add("kerbthump");
            if (ovr.LockupJudder != null) tags.Add("lockupjudder");
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
            // Mirror ToggleVote's guard: a refresh mid-flight risks acting on a
            // row the completion is about to replace, AND the in-flight fetch's
            // late cache write is the other half of the delete-resurrection
            // race the store's generation token closes. Friendly no-op instead.
            // (Safe: the try/finally in CommunityRefreshAsync means the latch
            // can't wedge true, so Delete can't be disabled permanently.)
            if (_communityFetchInFlight)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Refresh in progress. Try deleting again in a moment.";
                return;
            }

            var confirm = TrueforceDialog.Show(Window.GetWindow(this),
                "Delete preset",
                $"Delete '{sel.Summary.Name}'? Other drivers won't see it anymore.",
                DialogKind.Destructive, okLabel: "Delete", cancelLabel: "Cancel");
            if (confirm != true) return;

            // Outer guard: this is an async void handler, so anything escaping
            // it goes to the WPF dispatcher (no handler installed = potential
            // SimHub crash). The success path below touches disk (cache
            // invalidation) and host code (CarCommunityListRefreshed), both of
            // which can throw. The confirm-cancel above stays outside so a
            // cancel is never logged as an error.
            try
            {
            if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "Deleting...";
            bool success;
            string delKind = sel.Summary.Kind ?? _communityKind ?? "car";
            try { success = await _plugin.DeleteCommunityItemAsync(delKind, sel.Summary.Id); }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Couldn't delete that. Check your connection and try again.";
                TrueforceDialog.LogError("Community delete", ex);
                return;
            }
            if (!success)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Delete failed (sign in expired or permission denied).";
                return;
            }
            _communityRows.Remove(sel);
            // Drop the cached browse list this preset was in so it doesn't reappear
            // from cache on the next open (and trending reflects the removal).
            _plugin.InvalidateBrowseCacheForKind(sel.Summary.Kind, sel.Summary.Game, sel.Summary.CarId);
            // The active-card 'Top community presets' surface keeps its own in-memory
            // copy and won't re-read the (now invalidated) disk cache until it's
            // reset; nudge it so a car preset you just deleted doesn't linger there.
            if (string.IsNullOrEmpty(sel.Summary.Kind) || sel.Summary.Kind == "car")
                CarCommunityListRefreshed?.Invoke();
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Deleted.";
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Delete post-processing failed: " + ex.Message);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Deleted, but the local list may be stale. Refresh to resync.";
            }
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
                    CommunityStatusLabel.Text = "Sign in (Account tab) to vote.";
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
            // Server gate: a FRESH vote requires the user to have downloaded
            // the item (RPC raises "download before voting"). Surface that
            // locally before the optimistic UI bounces. The row.MyVote == 0
            // guard lets an EXISTING vote still be retracted / changed even
            // if the local download record is gone.
            if (!_plugin.HasDownloadedCommunity(row.Summary.Id) && row.MyVote == 0)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Download this preset first to rate it.";
                return;
            }

            // Outer guard (same reason as CommunityDelete_Click): async void
            // handler whose success path hits disk (cache invalidation) and
            // host code (CarCommunityListRefreshed) outside the inner RPC
            // try/catch; an escaped exception could tear down SimHub.
            try
            {
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
                SimHub.Logging.Current.Info("[TF4ALL] Vote failed: " + ex.Message);
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
            // A vote changes Wilson rank -> reorders the cached browse list. Drop
            // the affected family so the new order shows on the next open.
            _plugin.InvalidateBrowseCacheForKind(kind, row.Summary.Game, row.Summary.CarId);
            // Also reset the active-card 'Top community presets' surface (car only)
            // so its in-memory copy re-reads the new Wilson order.
            if (string.IsNullOrEmpty(kind) || kind == "car")
                CarCommunityListRefreshed?.Invoke();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info("[TF4ALL] Vote post-processing failed: " + ex.Message);
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Vote hit a local error. Refresh to resync the list.";
            }
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
            var kind = sel.Summary.Kind ?? _communityKind;
            string subjectKind;
            switch (kind)
            {
                case "game":   subjectKind = "game preset";   break;
                case "engine": subjectKind = "custom engine"; break;
                case "pack":   subjectKind = "pack";          break;
                default:       subjectKind = "preset";        break;
            }
            // Collect a reason (required) + optional note so the report that
            // reaches moderation carries context, not just a bare flag.
            var dlg = new ReportDialog(sel.Summary.Name, subjectKind)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() != true) return;
            switch (kind)
            {
                case "game":   _plugin.ReportCommunityGamePreset(sel.Summary.Id, dlg.SelectedCategory, dlg.Note); break;
                case "engine": _plugin.ReportCommunityCustomEngine(sel.Summary.Id, dlg.SelectedCategory, dlg.Note); break;
                case "pack":   _plugin.ReportCommunityPack(sel.Summary.Id, dlg.SelectedCategory, dlg.Note); break;
                default:       _plugin.ReportCommunityPreset(sel.Summary.Id, dlg.SelectedCategory, dlg.Note); break;
            }
            // Report RPCs are fire-and-forget at this layer; they log
            // internally on failure but never roundtrip a success
            // boolean here. Phrase the status as "submitted" rather
            // than "received" so we don't claim a confirmed delivery.
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Report submitted. Thanks for flagging.";
        }

        // Open the preview window: read-only view of the preset's
        // sections + key values. The Download button opens this preview first so
        // the import is an explicit, visible step (no silent one-click import);
        // the user confirms with "Download selected" inside, which imports via
        // PerformCommunityDownload.
        private async void CommunityDownload_Click(object sender, RoutedEventArgs e)
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
                    CommunityStatusLabel.Text = "Couldn't open the preview. Check your connection and try again.";
                TrueforceDialog.LogError("Community preview", ex);
                return;
            }
            if (full?.Body == null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Preview returned no body.";
                return;
            }
            // The preview window parses the server-supplied body JSON in its
            // ctor/renderers; a malformed or wrong-typed community body throws
            // there. This runs after the fetch's try/catch, and an exception
            // escaping an async void handler can tear down SimHub, so guard it.
            try
            {
                var win = new PresetPreviewWindow(full.Summary, full.Body)
                {
                    Owner = Window.GetWindow(this),
                };
                bool? ok = win.ShowDialog();
                if (CommunityStatusLabel != null) CommunityStatusLabel.Text = "";
                if (ok == true && win.DownloadRequested)
                {
                    // For packs, carry the user's per-item ticks into the download
                    // path so only the selected entries import (null = take all).
                    if (string.Equals(capturedKind, "pack", StringComparison.OrdinalIgnoreCase))
                        _pendingPackSelection = win.GetSelectedPackKeysOrNull();
                    PerformCommunityDownload();  // import the confirmed selection
                }
            }
            catch (Exception ex)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = "Couldn't open the preview. Check your connection and try again.";
                TrueforceDialog.LogError("Community preview", ex);
            }
        }

        // Set from the preview's "Download selected" ticks just before
        // PerformCommunityDownload runs: the pack entry keys to import, or null
        // to take everything. Consumed into a local at the top of that method so
        // it's tied to this invocation.
        private HashSet<string> _pendingPackSelection;

        // Performs the actual import. Reached only from CommunityDownload_Click
        // after the user confirms in the preview modal; _pendingPackSelection
        // carries the per-entry ticks (null = take everything).
        private async void PerformCommunityDownload()
        {
            var sel = SelectedCommunity;
            if (sel?.Summary == null || _plugin == null) return;
            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text = "Downloading...";

            // Consume any pending pack selection from a preview hand-off.
            var packSelection = _pendingPackSelection;
            _pendingPackSelection = null;

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
                    CommunityStatusLabel.Text = "Couldn't download that preset. Check your connection and try again.";
                TrueforceDialog.LogError("Community download", ex);
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
                        CommunityStatusLabel.Text = "That preset's data couldn't be read (it may be from a newer version).";
                    TrueforceDialog.LogError("Community body parse", ex);
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
                // Identical-content dedup (owner rule 2026-07-13): the same
                // tuning under a different server id must not pile up either.
                string dupName = _plugin.FindGamePresetNameWithSameContent(snap);
                if (dupName != null)
                {
                    if (CommunityStatusLabel != null)
                        CommunityStatusLabel.Text = $"You already have an identical preset: '{dupName}'.";
                    return;
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
                    communitySourceId: full.Summary.Id,
                    allowInPacks: full.Summary.AllowInPacks);
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
                    allowInPacks: full.Summary.AllowInPacks,
                    originalBodyHash: PresetBodyHasher.ComputeGameSnapshotBodyHash(snap),
                    ownerUserId: full.Summary.OwnerUserId);
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
                // Identity of each entry we actually wrote this download, so the
                // pack registers in the Library -> Packs grid (and is removable /
                // set-as-default later). Built full OR partial; only freshly
                // imported entries land here (deduped/skipped ones don't).
                var packEntries = new List<InstalledPackEntry>();
                // packSelection == null = full install (direct Download button).
                // Otherwise only the entry keys the user ticked in Preview.
                bool packFullTake = packSelection == null;
                bool PackIncluded(string bucket, Newtonsoft.Json.Linq.JToken entryTok) =>
                    packSelection == null
                    || packSelection.Contains(PresetPreviewWindow.PackEntryKey(bucket, entryTok as Newtonsoft.Json.Linq.JObject));

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
                        if (!PackIncluded("game", entry)) continue;
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
                                communitySourceId: entrySourceId,
                                allowInPacks: entryAllowPacks);
                            if (!entrySaved)
                            {
                                errors++;
                                SimHub.Logging.Current.Info(
                                    $"[TF4ALL] Pack entry (game) persist failed for '{useName}'.");
                                continue;
                            }
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                _plugin.RecordDownloadedCommunityPreset(
                                    entrySourceId, useName,
                                    carId: "*", gameName: _plugin.ActiveGame,
                                    contentVersion: 1, kind: "game",
                                    allowInPacks: entryAllowPacks,
                                    originalBodyHash: PresetBodyHasher.ComputeGameSnapshotBodyHash(snap),
                                    ownerUserId: null);
                            }
                            packEntries.Add(new InstalledPackEntry
                            {
                                Kind         = InstalledPackEntry.KindGame,
                                Name         = useName,
                                BaselineHash = _plugin.HashInstalledGamePresetFile(useName),
                            });
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[TF4ALL] Pack entry (game) import failed: {ex.Message}");
                        }
                    }
                }
                // Car presets (entries: { car_id, preset_name, game_name,
                // override, source_id?, source_author?, allow_in_packs? }).
                if (full.Body["car_presets"] is Newtonsoft.Json.Linq.JArray cpArr)
                {
                    foreach (var entry in cpArr)
                    {
                        if (!PackIncluded("car", entry)) continue;
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
                                communitySourceId: entrySourceId,
                                allowInPacks: entryAllowPacks);
                            if (!string.IsNullOrEmpty(entrySourceId))
                            {
                                _plugin.RecordDownloadedCommunityPreset(
                                    entrySourceId, useName,
                                    carId: cid, gameName: gname,
                                    contentVersion: 1, kind: "car",
                                    allowInPacks: entryAllowPacks,
                                    originalBodyHash: PresetBodyHasher.ComputeCarOverrideHash(carOvr),
                                    ownerUserId: null);
                            }
                            packEntries.Add(new InstalledPackEntry
                            {
                                Kind         = InstalledPackEntry.KindCar,
                                CarId        = cid,
                                PresetName   = useName,
                                GameName     = gname,
                                BaselineHash = _plugin.HashInstalledCarPresetFile(cid, useName),
                            });
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[TF4ALL] Pack entry (car) import failed: {ex.Message}");
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
                        if (!PackIncluded("engine", entry)) continue;
                        try
                        {
                            var def = entry?.ToObject<CustomEngineDef>();
                            if (def == null || string.IsNullOrWhiteSpace(def.Name)) continue;
                            string entrySourceId = entry?["source_id"]?.ToString();
                            bool   entryAllowPacks = entry?["allow_in_packs"]?.ToObject<bool>() ?? false;
                            // Pre-check the library: engine merge dedups by Id
                            // (which Save*Imported sets to the server uuid). When
                            // it's already present we skip the save (skipped++) but
                            // still claim it as a pack entry below, using the
                            // existing library def's id.
                            CustomEngineDef existingEngine = null;
                            if (!string.IsNullOrEmpty(entrySourceId)
                                && _plugin.Settings?.CustomEngines != null)
                            {
                                foreach (var local in _plugin.Settings.CustomEngines)
                                    if (local != null
                                        && (string.Equals(local.CommunitySourceId, entrySourceId, StringComparison.Ordinal)
                                            || string.Equals(local.Id, entrySourceId, StringComparison.Ordinal)))
                                    { existingEngine = local; break; }
                            }
                            string engineLibId;
                            if (existingEngine != null)
                            {
                                skipped++;
                                engineLibId = existingEngine.Id;
                            }
                            else
                            {
                                _plugin.SaveImportedCommunityCustomEngine(def,
                                    communitySourceId: entrySourceId,
                                    allowInPacks: entryAllowPacks);
                                // SaveImportedCommunityCustomEngine sets def.Id to
                                // the library id.
                                engineLibId = def.Id;
                                if (!string.IsNullOrEmpty(entrySourceId))
                                {
                                    _plugin.RecordDownloadedCommunityPreset(
                                        entrySourceId, def.Name,
                                        carId: "*", gameName: "",
                                        contentVersion: 1, kind: "engine",
                                        allowInPacks: entryAllowPacks,
                                        originalBodyHash: PresetBodyHasher.ComputeCustomEngineHash(def),
                                        ownerUserId: null);
                                }
                                imported++;
                            }
                            // Claim this engine as a pack entry whether newly added
                            // or already present, so "N other packs contain this
                            // engine" is meaningful on removal. Removal only deletes
                            // an engine nothing else references.
                            if (!string.IsNullOrEmpty(engineLibId))
                                packEntries.Add(new InstalledPackEntry
                                {
                                    Kind         = InstalledPackEntry.KindEngine,
                                    EngineId     = engineLibId,
                                    Name         = def.Name,
                                    BaselineHash = _plugin.HashInstalledEngine(engineLibId),
                                });
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            SimHub.Logging.Current.Info($"[TF4ALL] Pack entry (engine) import failed: {ex.Message}");
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
                // Register the pack in the Library -> Packs grid whenever we
                // wrote at least one entry, full take or partial. Merges by
                // CommunitySourceId so a repeat / partial download folds into the
                // existing row instead of duplicating it. Custom engines ARE
                // listed (KindEngine entries added above, with EngineId +
                // BaselineHash) so "N other packs contain this engine" is
                // meaningful on removal; removal still only deletes an engine
                // nothing else references.
                if (packEntries.Count > 0)
                    _plugin.RegisterCommunityPack(new InstalledPack
                    {
                        PackName          = full.Summary.Name ?? "Community pack",
                        Author            = full.Summary.Author,
                        AuthorVersion     = full.Summary.AuthorVersion,
                        Description       = full.Summary.Description,
                        ImportedAt        = DateTime.Now,
                        CommunitySourceId = full.Summary.Id,
                        Entries           = packEntries,
                    });
                // Record the pack as an installed bundle only on a full take.
                // A cherry-pick (subset selected in Preview) isn't a managed
                // bundle - the individual entries are still tracked above, but
                // the pack itself shouldn't show as fully installed. On a full
                // take with a partial failure we stamp SeenContentVersion=0 so
                // the next plugin-load update sweep re-surfaces it.
                if (packFullTake)
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
                        CommunityStatusLabel.Text = "That preset's data couldn't be read (it may be from a newer version).";
                    TrueforceDialog.LogError("Community body parse", ex);
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
                    communitySourceId: full.Summary.Id,
                    allowInPacks: full.Summary.AllowInPacks);
                _plugin.RecordCommunityCustomEngineDownload(capturedId);
                _plugin.RecordDownloadedCommunityPreset(
                    full.Summary.Id, def.Name,
                    carId: "*", gameName: "",
                    contentVersion: full.Summary.ContentVersion, kind: "engine",
                    allowInPacks: full.Summary.AllowInPacks,
                    originalBodyHash: PresetBodyHasher.ComputeCustomEngineHash(def),
                    ownerUserId: full.Summary.OwnerUserId);
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
                    CommunityStatusLabel.Text = "That preset's data couldn't be read (it may be from a newer version).";
                TrueforceDialog.LogError("Community body parse", ex);
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
            if (chosen.Contains("axleslip"))     apply.AxleSlip     = ovr.AxleSlip;
            if (chosen.Contains("kerbthump"))    apply.KerbThump    = ovr.KerbThump;
            if (chosen.Contains("lockupjudder")) apply.LockupJudder = ovr.LockupJudder;
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
            // Identical-content dedup (owner rule 2026-07-13): the same
            // tuning under a different server id must not pile up either.
            string carDupName = _plugin.FindCarPresetNameWithSameContent(_plugin.ActiveCarId, apply);
            if (carDupName != null)
            {
                if (CommunityStatusLabel != null)
                    CommunityStatusLabel.Text = $"You already have an identical preset: '{carDupName}'.";
                return;
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
                communitySourceId: full.Summary.Id,
                allowInPacks: full.Summary.AllowInPacks);
            _plugin.RecordCommunityPresetDownload(capturedId);
            // Track the download so the next plugin-load update check
            // knows where to look. Re-records on every download (the
            // tracker is keyed on preset_id) so a re-download after a
            // delete-then-re-download cycle stays in sync.
            _plugin.RecordDownloadedCommunityPreset(
                full.Summary.Id, presetName,
                _plugin.ActiveCarId, _plugin.ActiveGame,
                full.Summary.ContentVersion, kind: "car",
                allowInPacks: full.Summary.AllowInPacks,
                originalBodyHash: PresetBodyHasher.ComputeCarOverrideHash(apply),
                ownerUserId: full.Summary.OwnerUserId);

            if (CommunityStatusLabel != null)
                CommunityStatusLabel.Text =
                    $"Saved as '{presetName}'. Open it on the Car presets tab to use.";
            ReloadCars();
            LibraryChanged?.Invoke();
        }
    }
}
