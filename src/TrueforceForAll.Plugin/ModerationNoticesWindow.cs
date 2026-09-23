// Modal shown when a user opens "My uploads" and has open moderation notices
// (a moderator warned/removed an upload, or banned the account). Lists every
// open notice; each row offers Acknowledge (mark read, stop the auto-pop) and
// Request review (opens a text box -> request_review RPC -> appeal card to the
// mods). Notices already under review show as read-only "review requested".
//
// "Open" = appeal_state none|denied. Built in code (no XAML) like the other
// plugin dialogs so the csproj picks it up without a build-pipeline entry.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrueforceForAll.Plugin
{
    internal sealed class ModerationNoticesWindow : Window
    {
        private readonly TrueforcePlugin _plugin;
        private readonly StackPanel _list;

        // Fetch the caller's notices and, if any are open, show the modal.
        // force=false (My uploads): only pops when something is unacknowledged,
        // so it doesn't nag. force=true (blocked upload): pops on any open notice
        // so the user gets the appeal path right where they hit the wall.
        // No-op when community is off / signed out / nothing open.
        internal static async System.Threading.Tasks.Task MaybeShowAsync(
            TrueforcePlugin plugin, Window owner, bool force)
        {
            try
            {
                if (plugin?.Settings?.CommunityEnabled != true) return;
                if (string.IsNullOrEmpty(plugin.Settings?.AuthSession?.UserId)) return;
                var notices = await plugin.GetMyModerationNoticesAsync().ConfigureAwait(true);
                if (notices == null) return;
                var open = notices.FindAll(n => n.IsPending);   // appeal_state none/denied
                if (open.Count == 0) return;
                if (!force && !open.Exists(n => !n.Acknowledged)) return;
                new ModerationNoticesWindow(plugin, open) { Owner = owner }.ShowDialog();
            }
            catch { /* never let a notice check break the caller */ }
        }

        public ModerationNoticesWindow(TrueforcePlugin plugin, List<ModerationClient.NoticeRow> notices)
        {
            _plugin = plugin;
            Title = "Moderation notices";
            Width = 540;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SettingsControl.ApplyDarkTheme(this);

            var root = new DockPanel { Margin = new Thickness(14) };

            root.Children.Add(new TextBlock
            {
                Text = "A moderator flagged some of your community content. Fix it (for example, rename it), or explain why it was a mistake, then request a review.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
            });
            DockPanel.SetDock(root.Children[0], Dock.Top);

            var closeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var close = new Button { Content = "Close", Width = 90, Height = 28, IsCancel = true };
            closeRow.Children.Add(close);
            DockPanel.SetDock(closeRow, Dock.Bottom);
            root.Children.Add(closeRow);

            _list = new StackPanel();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
            root.Children.Add(scroll);

            foreach (var n in notices)
                _list.Children.Add(BuildCard(n));

            Content = root;
        }

        private static string Heading(ModerationClient.NoticeRow n)
        {
            switch ((n.Kind ?? "").ToLowerInvariant())
            {
                case "ban":     return "Your account was restricted";
                case "removal": return "An upload was removed";
                default:        return "An upload was hidden";
            }
        }

        private Border BuildCard(ModerationClient.NoticeRow n)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var sp = new StackPanel();

            sp.Children.Add(new TextBlock
            {
                Text = Heading(n) + (string.IsNullOrEmpty(n.ItemName) ? "" : $": {n.ItemName}"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x40)),
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
            sp.Children.Add(new TextBlock
            {
                Text = n.Message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                Margin = new Thickness(0, 0, 0, 8),
            });

            var status = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            var ack = new Button { Content = "Acknowledge", Width = 110, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
            ack.Click += async (s, e) =>
            {
                ack.IsEnabled = false;
                try
                {
                    bool ok = await _plugin.AcknowledgeModerationNoticeAsync(n.Id).ConfigureAwait(true);
                    status.Text = ok ? "Acknowledged." : "Couldn't reach the server, try again.";
                    status.Visibility = Visibility.Visible;
                    if (!ok) ack.IsEnabled = true;
                }
                catch
                {
                    // async void: an unhandled throw here would crash SimHub's
                    // dispatcher. Degrade to the retry path instead.
                    status.Text = "Couldn't reach the server, try again.";
                    status.Visibility = Visibility.Visible;
                    ack.IsEnabled = true;
                }
            };
            btnRow.Children.Add(ack);

            if (n.Appealable)
            {
                var appeal = new Button { Content = n.HasTarget ? "Fix & request review" : "Request review", Width = 160, Height = 26 };
                appeal.Click += async (s, e) =>
                {
                    var res = AppealForm.Prompt(this, n);
                    if (res == null) return; // cancelled
                    appeal.IsEnabled = false; ack.IsEnabled = false;
                    try
                    {
                        bool ok = await _plugin.RequestModerationReviewAsync(n.Id, res.Note, res.ProposedName, res.ProposedDescription).ConfigureAwait(true);
                        status.Text = ok
                            ? "Review requested. A moderator will take a look."
                            : "Couldn't submit the appeal, try again.";
                        status.Visibility = Visibility.Visible;
                        if (!ok) { appeal.IsEnabled = true; ack.IsEnabled = true; }
                    }
                    catch
                    {
                        // async void: don't let a throwing network call crash
                        // the dispatcher. Re-enable so the user can retry.
                        status.Text = "Couldn't submit the appeal, try again.";
                        status.Visibility = Visibility.Visible;
                        appeal.IsEnabled = true; ack.IsEnabled = true;
                    }
                };
                btnRow.Children.Add(appeal);
            }
            else
            {
                btnRow.Children.Add(new TextBlock
                {
                    Text = "Final (no appeal)",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            sp.Children.Add(btnRow);
            sp.Children.Add(status);

            card.Child = sp;
            return card;
        }

        /// <summary>The user's appeal: a note plus an optional proposed fix.
        /// ProposedName/Description are null when unchanged from the current values.</summary>
        internal sealed class AppealResult
        {
            public string Note;
            public string ProposedName;
            public string ProposedDescription;
        }

        /// <summary>Fix-and-appeal form. For an item notice it lets the user edit
        /// the name/description (prefilled) so the mod sees an old->new diff and,
        /// on Reinstate, applies the fix. For a ban it's just a note. Returns the
        /// result on submit, or null if cancelled.</summary>
        private sealed class AppealForm : Window
        {
            private readonly TextBox _name, _desc, _note;
            private readonly ModerationClient.NoticeRow _n;

            private AppealForm(ModerationClient.NoticeRow n)
            {
                _n = n;
                Title = "Request review";
                Width = 460; Height = n.HasTarget ? 470 : 280;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
                SettingsControl.ApplyDarkTheme(this);

                var sp = new StackPanel { Margin = new Thickness(14) };
                sp.Children.Add(new TextBlock
                {
                    Text = n.HasTarget
                        ? "Fix the issue here (for example, rename it), then add a note. A moderator reviews your change and restores it if approved."
                        : "Tell the moderators why this should be reviewed.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                });

                if (n.HasTarget)
                {
                    sp.Children.Add(Label("Name"));
                    _name = Input(n.ItemName ?? "", false);
                    sp.Children.Add(_name);
                    sp.Children.Add(Label("Description"));
                    _desc = Input(n.ItemDescription ?? "", true, 70);
                    sp.Children.Add(_desc);
                }

                sp.Children.Add(Label("Note to moderator"));
                _note = Input("", true, 80);
                sp.Children.Add(_note);

                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
                var ok = new Button { Content = "Submit appeal", Width = 130, Height = 28, IsDefault = true };
                ok.Click += (s, e) => { DialogResult = true; };
                row.Children.Add(cancel); row.Children.Add(ok);
                sp.Children.Add(row);
                Content = sp;
                Loaded += (s, e) => { if (_name != null) { _name.Focus(); _name.SelectAll(); } else _note.Focus(); };
            }

            private static UIElement Label(string t)
                => new TextBlock { Text = t, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold };

            private static TextBox Input(string text, bool multiline, double height = 0)
            {
                var tb = new TextBox
                {
                    Text = text ?? "", Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(4, 2, 4, 2), MaxLength = 1000,
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    CaretBrush = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)),
                };
                if (multiline)
                {
                    tb.Height = height > 0 ? height : 70;
                    tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap;
                    tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
                return tb;
            }

            public static AppealResult Prompt(Window owner, ModerationClient.NoticeRow n)
            {
                var d = new AppealForm(n) { Owner = owner };
                if (d.ShowDialog() != true) return null;
                var res = new AppealResult { Note = (d._note.Text ?? "").Trim() };
                if (n.HasTarget)
                {
                    var nm = (d._name.Text ?? "").Trim();
                    var ds = (d._desc.Text ?? "").Trim();
                    // Only send a proposed value when it actually changed.
                    res.ProposedName = (nm.Length > 0 && nm != (n.ItemName ?? "").Trim()) ? nm : null;
                    res.ProposedDescription = (ds != (n.ItemDescription ?? "").Trim()) ? ds : null;
                }
                return res;
            }
        }
    }
}
