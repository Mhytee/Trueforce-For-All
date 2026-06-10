// Manage saved engine variants for one car. Listing surfaces what
// GetActiveCarVariants() returns (i.e. only stored, non-legacy-User
// rows; synthetic Baked variants from BuiltinCarCylinders never appear
// because they're not in the bundle). Each row exposes inline rename
// + Delete; the Source column tells the user where the row came from
// (yours / community / baked / scanner) so they can decide whether
// editing it makes sense.
//
// The window watches _plugin.ActiveCarId on a 500ms tick and auto-
// closes if the user navigates away (Forza car-change while the
// modal is open). All mutations route through TrueforcePlugin's
// public helpers so save + re-resolve stays consistent with the
// inline picker.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        }

        public CarFactsVariantsWindow(TrueforcePlugin plugin, string game, string carId, string carDisplayName)
        {
            _plugin = plugin;
            _game   = game;
            _carId  = carId;

            Title         = "Manage engine variants";
            Width         = 600;
            Height        = 380;
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
                Text = "Variants are auto-created from telemetry on each new engine signature for this car. "
                     + "Rename a row by clicking its label (cosmetic, stays local). Delete drops the row; "
                     + "if telemetry observes that same engine again, a fresh row gets created. "
                     + "Built-in (Baked) rows come from the car list and can't be edited.",
                Foreground = MutedFg, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // Bottom Close row first so it doesn't fight LastChildFill
            // with the DataGrid for vertical space.
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            DockPanel.SetDock(btnRow, Dock.Bottom);
            var closeBtn = new Button
            {
                Content = "Close", Width = 90,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = TextFg, Background = PanelBg, IsCancel = true,
            };
            closeBtn.Click += (s, e) => Close();
            btnRow.Children.Add(closeBtn);
            root.Children.Add(btnRow);

            _emptyHint = new TextBlock
            {
                Text = "No saved variants. Register one from the 'New engine variant' prompt next time it pops.",
                Foreground = MutedFg, FontSize = 12, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            DockPanel.SetDock(_emptyHint, Dock.Bottom);
            root.Children.Add(_emptyHint);

            _grid = BuildGrid();
            root.Children.Add(_grid);

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
                Header = "Label",
                Binding = new Binding("Label") { Mode = BindingMode.TwoWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 160,
            };
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
            g.Columns.Add(new DataGridTextColumn
            {
                Header = "Redline", Width = 80, IsReadOnly = true,
                Binding = new Binding("Redline"),
            });

            // Delete button column. Disabled for non-editable rows.
            var deleteTemplate = new DataTemplate();
            var btnFactory = new FrameworkElementFactory(typeof(Button));
            btnFactory.SetValue(Button.ContentProperty, "Delete");
            btnFactory.SetBinding(Button.IsEnabledProperty, new Binding("CanEdit"));
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

        private void Reload()
        {
            if (_plugin == null) { Close(); return; }
            var variants = _plugin.GetActiveCarVariants();
            var rows = new System.Collections.Generic.List<Row>();
            if (variants != null)
            {
                foreach (var v in variants)
                {
                    if (v == null) continue;
                    rows.Add(new Row
                    {
                        Id          = v.Id,
                        Label       = string.IsNullOrEmpty(v.Label) ? "(unnamed)" : v.Label,
                        SourceLabel = v.Source.ToString(),
                        Cylinders   = v.Cylinders,
                        Redline     = v.RedlineRpm.HasValue ? v.RedlineRpm.Value.ToString() : "-",
                        // Built-in (Baked) baselines are synthesized at
                        // lookup time from the cylinder bake, not stored
                        // in the user's bundle, so rename / delete on them
                        // would no-op (the resolver would just re-synthesize
                        // the same row). Every other source IS persistent
                        // bundle state - rename to add a friendly label,
                        // delete to drop a misidentified row.
                        CanEdit     = v.Source != CarFactSource.Baked,
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

        private void DeleteCell_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b)) return;
            if (!(b.DataContext is Row row)) return;
            if (!row.CanEdit) return;
            if (TrueforceDialog.Show(this,
                    "Delete variant?",
                    "Remove the variant \"" + row.Label + "\"? The resolver will fall back to the next-best source for this car.",
                    DialogKind.Destructive) != true) return;
            if (_plugin.DeleteActiveCarVariant(row.Id))
                Reload();
        }
    }
}
