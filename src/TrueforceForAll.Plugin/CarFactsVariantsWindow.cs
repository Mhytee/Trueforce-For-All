// Manage saved engine variants for one car. Listing surfaces what
// GetActiveCarVariants() returns (i.e. only stored, non-legacy-User
// rows; synthetic Baked variants from BuiltinCarCylinders never appear
// because they're not in the bundle). Each row exposes inline rename,
// an Engine dropdown (the per-variant UserEngineLayout pin: unlike the
// Car facts combo, which can only pin the variant currently being
// driven, this edits ANY stored variant), and Delete. The Source
// column tells the user where the row came from (yours / community /
// baked / scanner) so they can decide whether editing it makes sense.
// Custom-engine authoring lives here too ("Create custom engine…"
// in the footer), next to the dropdowns that use the library.
//
// The window watches _plugin.ActiveCarId on a 500ms tick and auto-
// closes if the user navigates away (Forza car-change while the
// modal is open). All mutations route through TrueforcePlugin's
// public helpers so save + re-resolve stays consistent with the
// inline picker.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    internal sealed class CarFactsVariantsWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush PanelBg  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush InputBg  = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush BorderFg = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        private readonly TrueforcePlugin _plugin;
        private readonly string _game;
        private readonly string _carId;
        private readonly DispatcherTimer _carChangeWatcher;

        private DataGrid _grid;
        private TextBlock _emptyHint;

        public sealed class Row
        {
            public string Id           { get; set; }
            public string Label        { get; set; }
            public string SourceLabel  { get; set; }
            public int    Cylinders    { get; set; }
            public string Redline      { get; set; }
            public bool   CanEdit      { get; set; }  // false on Baked / Scanner authoritative rows
            public bool   CanDelete    { get; set; }  // CanEdit AND not the variant currently being driven
            public List<EngineOption> EngineOptions { get; set; }
            public int    EngineIndex  { get; set; }  // index into EngineOptions matching the pin (0 = Auto)
        }

        // One entry of the per-row Engine dropdown. Layout == null means
        // Auto (no pin: the resolver cascade decides).
        public sealed class EngineOption
        {
            public string Display { get; set; }
            public Effects.EngineLayout? Layout { get; set; }
            public string CustomId { get; set; } = "";
            public override string ToString() => Display;
        }

        public CarFactsVariantsWindow(TrueforcePlugin plugin, string game, string carId, string carDisplayName)
        {
            _plugin = plugin;
            _game   = game;
            _carId  = carId;

            Title         = "Manage engine variants";
            // Column minimums sum to ~710px; anything under ~770 shows a
            // horizontal scrollbar with the Delete column cut off.
            Width         = 780;
            // Grow to fit the variants, but cap the height and let the grid
            // scroll past that so many variants don't stretch the window off-screen.
            SizeToContent = SizeToContent.Height;
            MinHeight     = 320;
            MaxHeight     = 620;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
            Content = root;

            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            header.Children.Add(new TextBlock
            {
                Text = "Manage engine variants",
                Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 15,
                Margin = new Thickness(0, 0, 0, 4),
            });
            string subtitle = string.IsNullOrEmpty(carDisplayName) || carDisplayName == carId
                ? carId
                : carDisplayName + "   (" + carId + ")";
            header.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
            });
            header.Children.Add(new TextBlock
            {
                Text = "Variants are created automatically the first time this car shows a new engine. "
                     + "The Engine dropdown pins that variant's engine type; Auto lets detection decide. "
                     + "Built-in rows come from the car list and can't be edited.",
                Foreground = MutedFg, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // Bottom row first so it doesn't fight LastChildFill with the
            // DataGrid for vertical space: Create custom engine… on the
            // left (the library the Engine dropdowns draw from), Close right.
            var btnRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(btnRow, Dock.Bottom);
            var closeBtn = new Button
            {
                Content = "Close", Width = 90,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            btnRow.Children.Add(closeBtn);
            var createLink = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var createHl = new Hyperlink(new Run("Create custom engine…"))
            {
                ToolTip = "Author a firing pattern nothing built-in matches. Saved to your library, then pick it in a variant's Engine dropdown.",
            };
            createHl.Click += CreateCustom_Click;
            createLink.Inlines.Add(createHl);
            btnRow.Children.Add(createLink);
            root.Children.Add(btnRow);

            _emptyHint = new TextBlock
            {
                Text = "No saved variants yet. One is added automatically the next time this car shows a new engine.",
                Foreground = MutedFg, FontSize = 12, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            _grid = BuildGrid();
            // Host both in the one fill slot so the empty-state hint centers
            // in the grid's area instead of docking as a strip above the
            // footer (Reload's visibility toggles decide which one shows).
            var contentHost = new Grid();
            contentHost.Children.Add(_emptyHint);
            contentHost.Children.Add(_grid);
            root.Children.Add(contentHost);

            Reload();

            // Watch for active-car changes while the modal is open. A
            // Forza in-game swap could move the user off the car this
            // window was opened for; safer to auto-close than to keep
            // editing the wrong bundle.
            _carChangeWatcher = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _carChangeWatcher.Tick += (s, e) =>
            {
                if (_plugin == null) { Close(); return; }
                if (!string.Equals(_plugin.ActiveCarId, _carId, StringComparison.Ordinal)
                    || !string.Equals(_plugin.ActiveGame, _game, StringComparison.Ordinal))
                    Close();
            };
            _carChangeWatcher.Start();

            Closed += (s, e) => _carChangeWatcher.Stop();
        }

        private DataGrid BuildGrid()
        {
            var g = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows      = false,
                CanUserDeleteRows   = false,
                CanUserResizeRows   = false,
                CanUserSortColumns  = false,
                CanUserReorderColumns = false,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowHeaderWidth      = 0,
                Background          = InputBg,
                Foreground          = TextFg,
                BorderBrush         = BorderFg,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                SelectionMode       = DataGridSelectionMode.Single,
            };

            // Label column with inline edit. Commits via CellEditEnding
            // below so we can plumb the rename into the plugin.
            var labelCol = new DataGridTextColumn
            {
                Header = "Label (click to rename)",
                Binding = new Binding("Label") { Mode = BindingMode.TwoWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 160,
            };
            // The rename hint hangs off the column header instead of the
            // intro paragraph. BasedOn keeps whatever implicit header style
            // is in scope, so this column still matches its siblings.
            var labelHeaderStyle = new Style(typeof(DataGridColumnHeader),
                TryFindResource(typeof(DataGridColumnHeader)) as Style);
            labelHeaderStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                "Rename a row for your own reference. The label is cosmetic and stays on this PC; it is never shared."));
            labelCol.HeaderStyle = labelHeaderStyle;
            g.Columns.Add(labelCol);

            g.Columns.Add(new DataGridTextColumn
            {
                Header = "Source", Width = 100, IsReadOnly = true,
                Binding = new Binding("SourceLabel"),
            });
            g.Columns.Add(new DataGridTextColumn
            {
                Header = "Cyl", Width = 60, IsReadOnly = true,
                Binding = new Binding("Cylinders"),
            });

            // Engine pin dropdown. Auto = no pin (detection decides); any
            // other entry pins that engine type on THIS variant, the same
            // UserEngineLayout the Car facts combo writes for the live one.
            var engineTemplate = new DataTemplate();
            var comboFactory = new FrameworkElementFactory(typeof(ComboBox));
            comboFactory.SetBinding(ComboBox.ItemsSourceProperty, new Binding("EngineOptions"));
            comboFactory.SetBinding(ComboBox.SelectedIndexProperty, new Binding("EngineIndex") { Mode = BindingMode.TwoWay });
            comboFactory.SetBinding(UIElement.IsEnabledProperty, new Binding("CanEdit"));
            comboFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
            comboFactory.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(EngineCell_Changed));
            engineTemplate.VisualTree = comboFactory;
            g.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Engine", Width = 170,
                CellTemplate = engineTemplate,
            });

            g.Columns.Add(new DataGridTextColumn
            {
                Header = "Redline", Width = 130, IsReadOnly = true,
                Binding = new Binding("Redline"),
            });

            // Delete button column. Disabled for built-in rows AND for the
            // variant currently being driven (telemetry would just recreate it).
            var deleteTemplate = new DataTemplate();
            var btnFactory = new FrameworkElementFactory(typeof(Button));
            btnFactory.SetValue(Button.ContentProperty, "Delete");
            btnFactory.SetBinding(Button.IsEnabledProperty, new Binding("CanDelete"));
            btnFactory.SetValue(Button.ToolTipProperty,
                "Delete a stale or misidentified variant. The one you're currently driving can't be deleted (it auto-detects from telemetry). If that engine turns up again, a fresh row is created.");
            btnFactory.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            btnFactory.SetValue(Button.PaddingProperty, new Thickness(8, 2, 8, 2));
            btnFactory.SetValue(Button.MarginProperty, new Thickness(4, 2, 4, 2));
            btnFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(DeleteCell_Click));
            deleteTemplate.VisualTree = btnFactory;
            g.Columns.Add(new DataGridTemplateColumn
            {
                Header = "", Width = 90, CanUserResize = false,
                CellTemplate = deleteTemplate,
            });

            g.CellEditEnding += Grid_CellEditEnding;
            return g;
        }

        // Build one row's Engine dropdown entries: Auto (labelled with what
        // detection actually resolves to for THIS variant, so the user can
        // judge whether to override it), every built-in layout, then the
        // user's saved custom engines. No action entries - authoring goes
        // through the footer link.
        private List<EngineOption> BuildEngineOptions(EngineVariant v)
        {
            // Mirror the resolver: a variant's auto layout derives from its
            // stored cylinders + config; EngineConfig.Custom rows ride a
            // community custom pattern instead.
            string autoLabel = "Auto (not detected yet)";
            if (v != null && v.EngineConfig == Effects.EngineConfig.Custom)
                autoLabel = "Auto (community custom)";
            else if (v != null && v.Cylinders >= 1 && v.Cylinders <= 16)
                autoLabel = "Auto (" + Effects.FiringPatternDb.LayoutDisplayName(
                    Effects.FiringPatternDb.LayoutFromLegacy(v.Cylinders, v.EngineConfig, false)) + ")";
            var options = new List<EngineOption>
            {
                new EngineOption { Display = autoLabel, Layout = null },
            };
            foreach (Effects.EngineLayout l in Enum.GetValues(typeof(Effects.EngineLayout)))
            {
                if (l == Effects.EngineLayout.Auto || l == Effects.EngineLayout.Custom) continue;
                options.Add(new EngineOption
                {
                    Display = Effects.FiringPatternDb.LayoutDisplayName(l),
                    Layout  = l,
                });
            }
            var customs = _plugin?.Settings?.CustomEngines;
            if (customs != null)
            {
                foreach (var c in customs)
                {
                    if (c == null) continue;
                    string name = string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name;
                    options.Add(new EngineOption
                    {
                        Display  = c.IsElectric ? name + "  (electric custom)" : name + "  (custom)",
                        Layout   = Effects.EngineLayout.Custom,
                        CustomId = c.Id ?? "",
                    });
                }
            }
            // A dangling custom pin (engine deleted elsewhere / settings
            // restored on a machine without it) gets its own entry so the
            // displayed selection EQUALS the stored state. Without this the
            // combo fell back to Auto and the change handler "fixed" the
            // mismatch by writing Auto - merely opening this window silently
            // erased the pin. This way the breakage is visible, and only a
            // deliberate pick overwrites it.
            if (v != null && v.UserEngineLayout == Effects.EngineLayout.Custom
                && !string.IsNullOrEmpty(v.UserCustomEngineId))
            {
                bool inLibrary = false;
                foreach (var o in options)
                    if (o.Layout == Effects.EngineLayout.Custom
                        && string.Equals(o.CustomId, v.UserCustomEngineId, StringComparison.Ordinal))
                    { inLibrary = true; break; }
                if (!inLibrary)
                    options.Add(new EngineOption
                    {
                        Display  = "(missing custom engine, falling back to Auto)",
                        Layout   = Effects.EngineLayout.Custom,
                        CustomId = v.UserCustomEngineId,
                    });
            }
            return options;
        }

        private static int FindEngineOptionIndex(List<EngineOption> options,
            Effects.EngineLayout? pinLayout, string pinCustomId)
        {
            if (pinLayout == null) return 0;
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                if (o.Layout != pinLayout) continue;
                if (pinLayout == Effects.EngineLayout.Custom
                    && !string.Equals(o.CustomId, pinCustomId ?? "", StringComparison.Ordinal)) continue;
                return i;
            }
            return 0;   // dangling custom pin reads as Auto (matches the resolver's safety net)
        }

        private void Reload()
        {
            if (_plugin == null) { Close(); return; }
            var variants = _plugin.GetActiveCarVariants();
            // The variant currently being driven can't be deleted: telemetry
            // would just recreate it, so the delete would appear not to work.
            string activeId = null;
            try { activeId = _plugin.GetActiveResolvedVariant()?.Id; } catch { }
            var rows = new System.Collections.Generic.List<Row>();
            if (variants != null)
            {
                foreach (var v in variants)
                {
                    if (v == null) continue;
                    bool canEdit = v.Source != CarFactSource.Baked;
                    var engineOptions = BuildEngineOptions(v);
                    // Show the redline this variant actually resolves to, mirroring
                    // what the rev limiter uses now that "adopt" is retired: the
                    // user's own pin, else the community consensus cached for this
                    // variant's signature (when car facts are on), else a stored
                    // absolute (telemetry-captured; Forza has none), else "auto"
                    // (derived live). Community values are tagged so they read as
                    // not-the-user's-own.
                    int? redline = _plugin.ResolveVariantDisplayRedline(_game, _carId, v, out string redSrc);
                    string redlineText =
                        !redline.HasValue                 ? "auto" :
                        redSrc == "community"             ? redline.Value + " (community)" :
                        redSrc == "community_unconfirmed" ? redline.Value + " (unconfirmed)" :
                                                            redline.Value.ToString();
                    rows.Add(new Row
                    {
                        Id          = v.Id,
                        Label       = string.IsNullOrEmpty(v.Label) ? "(unnamed)" : v.Label,
                        SourceLabel = string.Equals(v.Source.ToString(), "Baked", StringComparison.OrdinalIgnoreCase)
                                        ? "Built-in" : v.Source.ToString(),
                        Cylinders   = v.Cylinders,
                        Redline     = redlineText,
                        // Built-in (Baked) baselines are synthesized at
                        // lookup time from the cylinder bake, not stored
                        // in the user's bundle, so rename / delete on them
                        // would no-op (the resolver would just re-synthesize
                        // the same row). Every other source IS persistent
                        // bundle state - rename to add a friendly label,
                        // delete to drop a misidentified row.
                        CanEdit     = canEdit,
                        CanDelete   = canEdit && v.Id != activeId,
                        EngineOptions = engineOptions,
                        EngineIndex   = FindEngineOptionIndex(engineOptions,
                                            v.UserEngineLayout, v.UserCustomEngineId),
                    });
                }
            }
            _grid.ItemsSource = rows;
            bool empty = rows.Count == 0;
            _grid.Visibility       = empty ? Visibility.Collapsed : Visibility.Visible;
            _emptyHint.Visibility  = empty ? Visibility.Visible    : Visibility.Collapsed;
        }

        private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (!(e.Row.Item is Row row)) return;
            if (!(e.EditingElement is TextBox tb)) return;
            string newLabel = (tb.Text ?? "").Trim();
            if (string.IsNullOrEmpty(newLabel))
            {
                // Reject blank: revert UI to the pre-edit label.
                tb.Text = row.Label;
                return;
            }
            if (string.Equals(newLabel, row.Label, StringComparison.Ordinal)) return;
            if (!row.CanEdit)
            {
                tb.Text = row.Label;
                return;
            }
            if (_plugin.RenameActiveCarVariant(row.Id, newLabel))
                row.Label = newLabel;
            else
                tb.Text = row.Label;
        }

        // Per-row Engine dropdown commit. Fires on the initial binding too,
        // so it no-ops when the selection already matches the stored pin.
        private void EngineCell_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_plugin == null) return;
            if (!(sender is ComboBox cb) || !(cb.DataContext is Row row)) return;
            // Only user-driven changes may write: initial bindings, container
            // recycling and programmatic resets arrive with the dropdown
            // closed and no keyboard focus. Belt-and-braces on top of the
            // stored-pin compare below.
            if (!cb.IsDropDownOpen && !cb.IsKeyboardFocusWithin) return;
            if (!(cb.SelectedItem is EngineOption opt)) return;
            var (curLayout, curCustomId) = _plugin.GetActiveCarVariantUserEngineById(row.Id);
            string newCustomId = opt.Layout == Effects.EngineLayout.Custom ? (opt.CustomId ?? "") : "";
            if (curLayout == opt.Layout
                && string.Equals(curCustomId ?? "", newCustomId, StringComparison.Ordinal))
                return;
            _plugin.SaveActiveCarVariantUserEngineById(row.Id, opt.Layout, opt.CustomId);
        }

        // Author a new custom engine into the library, then refresh so it
        // appears in every row's Engine dropdown. No auto-pin: with several
        // variants listed, the user picks which one gets it.
        private void CreateCustom_Click(object sender, RoutedEventArgs e)
        {
            SimHub.Logging.Current.Info("[TF4ALL] Create custom engine clicked (variants modal)");
            if (_plugin?.Settings == null) return;
            var def = new CustomEngineDef { Id = Guid.NewGuid().ToString("N") };
            var dlg = new CustomEngineEditor { Owner = this };
            dlg.Init(def, "Create custom engine");
            if (dlg.ShowDialog() != true || !dlg.Saved) return;
            if (_plugin.Settings.CustomEngines == null)
                _plugin.Settings.CustomEngines = new List<CustomEngineDef>();
            _plugin.Settings.CustomEngines.Add(def);
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            { SimHub.Logging.Current.Info("[TF4ALL] Persist new custom engine failed: " + ex.Message); }
            Reload();
        }

        private void DeleteCell_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b)) return;
            if (!(b.DataContext is Row row)) return;
            if (!row.CanDelete) return;
            if (TrueforceDialog.Show(this,
                    "Delete variant?",
                    "Remove the variant \"" + row.Label + "\"? This car falls back to its next-best engine guess.",
                    DialogKind.Destructive) != true) return;
            if (_plugin.DeleteActiveCarVariant(row.Id))
                Reload();
        }
    }
}
