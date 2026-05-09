// Modal dialog used at export time to collect optional sharing metadata
// (Author, Description, Version) for a preset / car preset / pack. All
// three fields are optional; the user can clear any of them and click OK
// to skip including that field. Author is pre-filled from
// TrueforceSettings.SharingAuthor and the dialog reports back the final
// values so the plugin can persist any change to the saved author.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class PresetMetadataDialog : Window
    {
        public string Author        { get; private set; }
        public string Description   { get; private set; }
        public string AuthorVersion { get; private set; }

        public PresetMetadataDialog(string title, string subjectKind,
            string defaultAuthor, string defaultDescription, string defaultAuthorVersion)
        {
            Title = title;
            Width = 480;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock
            {
                Text = $"Optional info for the {subjectKind}. Leave anything blank to omit it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
            });

            sp.Children.Add(BuildLabel("Author"));
            var tbAuthor = new TextBox { Text = defaultAuthor ?? "", Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(tbAuthor);

            sp.Children.Add(BuildLabel("Version"));
            var tbVersion = new TextBox { Text = defaultAuthorVersion ?? "", Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(tbVersion);

            sp.Children.Add(BuildLabel("Description"));
            var tbDesc = new TextBox
            {
                Text = defaultDescription ?? "",
                Margin = new Thickness(0, 0, 0, 12),
                Height = 80,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            sp.Children.Add(tbDesc);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var ok = new Button { Content = "Save & export", Width = 130, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            sp.Children.Add(btnRow);

            ok.Click += (s, e) =>
            {
                Author        = tbAuthor.Text;
                Description   = tbDesc.Text;
                AuthorVersion = tbVersion.Text;
                DialogResult = true;
            };

            Loaded += (s, e) => { tbAuthor.Focus(); tbAuthor.SelectAll(); };

            Content = sp;
        }

        private static UIElement BuildLabel(string text)
            => new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold };
    }
}
