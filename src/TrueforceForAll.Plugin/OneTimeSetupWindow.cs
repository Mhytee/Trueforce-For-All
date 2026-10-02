// One time setup: the first-run walkthrough. A fresh install meets it on the
// first open of the plugin page (SettingsControl.MaybeShowOneTimeSetup); an
// existing install only through the "Run One time setup" button on the
// Settings tab. Skippable at every step, and closing the window is a skip.
//
// Pages, each shown only when it applies:
//   Intro     what the plugin is, always first: the fundamentals, not the
//             online features (owner call 2026-10-02).
//   Checks    a supported wheel found, SimHub elevated, G HUB closed: the
//             three causes behind most "the wheel feels dead" reports.
//   View      Simple or Advanced on the Effects tab.
//   CarFacts  what the Car Facts panel is and when to touch it. Simple mode's
//             Engine card leaves engine type to it, and the redline buzz and
//             rev lights read their redline from it.
//   Lights    what the wheel's lights do: car-matched rev lights
//             (LovelyCarDataEnabled), only on a wheel whose patterns can be
//             changed, and idle lighting (IdleLedMode) on every wheel. Shown once
//             any supported wheel is found. Asked as the user moves, so a
//             wheel found after setup opened still brings the page in. A fixed strip already gets the
//             timing half through community features.
//   Community the networked-welcome disclosure (WelcomeWindow.BuildBody),
//             only while that welcome has not been seen. Near the end, beside
//             the account it leads into. Only reaching it counts as the
//             disclosure being given (WelcomeShown); a user who skips sooner
//             gets the standalone welcome later instead.
//   Account   sign in or create an account, run right here through the
//             normal sign-in window, then link Discord. Signed out: the whole
//             page. Signed in but not linked: just the Discord link, under its
//             own heading. Signed in and linked: left out by the caller. A
//             sign-in made on it shows as done if the user comes back to it. Last before Done: optional, so it
//             never stands in front of the steps that get the wheel working.
//   Done      how to come back here, plus a short "good to know" list of
//             the things most often reported as broken that are not.
//
// Every answer is an existing setting with its own control elsewhere, applied
// the moment it is picked through the callbacks, so a skip keeps what was
// already chosen and nothing here is state only this window knows about. The
// window commits nothing itself: the caller reads WelcomeShown afterwards,
// and the account page hands the sign-in to the caller's callback.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    internal sealed class OneTimeSetupWindow : Window
    {
        private static readonly Brush WindowBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        private static readonly Brush TextFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly Brush MutedFg  = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        private static readonly Brush HeaderFg = new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x4A));
        private static readonly Brush OkFg     = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        private static readonly Brush WarnFg   = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x4A));

        // One window per session, whichever path opened it.
        internal static bool ShownThisSession;

        /// <summary>The welcome page was on screen, so its disclosure was given
        /// and the caller commits the welcome exactly as WelcomeWindow's does.</summary>
        public bool WelcomeShown { get; private set; }

        // Each page with the rule for whether it shows. The rule is asked on
        // every move, not once at open, so a page that depends on live state
        // (rev lights need the wheel, which can be found a few seconds in)
        // appears as soon as it applies.
        private readonly List<Func<FrameworkElement>> _pages = new List<Func<FrameworkElement>>();
        private readonly List<Func<bool>> _pageShows = new List<Func<bool>>();
        private readonly ContentControl _host = new ContentControl();
        private readonly TextBlock _stepText;
        private readonly Button _backBtn, _nextBtn;
        private int _index;
        private int _welcomeIndex = -1;   // the community page, when present

        private readonly Func<bool> _isElevated;
        private readonly Func<bool> _isGHubRunning;
        private readonly Func<bool> _wheelFound;
        private readonly Func<string> _wheelName;
        private readonly Action<bool> _setSimpleMode;
        private readonly Action<bool> _setLovely;
        private readonly Func<bool> _isSignedIn;
        private readonly Action<Window> _signIn;
        private readonly Func<Task<bool>> _discordLinked;
        private readonly Func<Task<bool>> _linkDiscord;
        // The account page relabels Next as "Skip this step" while signed out.
        private Action _afterShow;
        private bool _simple;
        private bool _lovely;
        private readonly Func<bool> _lightsProgrammable;
        private AmbientLedMode _idle;
        private readonly Action<AmbientLedMode> _setIdle;

        public OneTimeSetupWindow(bool includeWelcome, bool includeAccount, Func<bool> includeLights,
                                  Func<bool> isElevated, Func<bool> isGHubRunning,
                                  Func<bool> wheelFound, Func<string> wheelName,
                                  bool simpleNow, Action<bool> setSimpleMode,
                                  bool lovelyNow, Action<bool> setLovely,
                                  Func<bool> lightsProgrammable,
                                  AmbientLedMode idleNow, Action<AmbientLedMode> setIdle,
                                  Func<bool> isSignedIn, Action<Window> signIn,
                                  Func<Task<bool>> discordLinked, Func<Task<bool>> linkDiscord)
        {
            _isSignedIn = isSignedIn;
            _signIn = signIn;
            _discordLinked = discordLinked;
            _linkDiscord = linkDiscord;
            _isElevated = isElevated;
            _wheelFound = wheelFound;
            _wheelName = wheelName;
            _isGHubRunning = isGHubRunning;
            _simple = simpleNow;
            _setSimpleMode = setSimpleMode;
            _lovely = lovelyNow;
            _setLovely = setLovely;
            _lightsProgrammable = lightsProgrammable;
            _idle = idleNow;
            _setIdle = setIdle;

            Title         = Loc.T("Setup_Title");
            Width         = 540;
            SizeToContent = SizeToContent.Height;
            Background    = WindowBg;
            Foreground    = TextFg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode    = ResizeMode.NoResize;

            AddPage(IntroPage);
            AddPage(ChecksPage);
            AddPage(ViewPage);
            AddPage(CarFactsPage);
            AddPage(LightsPage, () => Safe(includeLights, false));
            // Last before Done (owner call 2026-10-01): optional, so it comes
            // after the steps that get the wheel working rather than reading as
            // a gate in front of them.
            if (includeWelcome) { _welcomeIndex = _pages.Count; AddPage(WelcomePage); }
            if (includeAccount) AddPage(AccountPage);
            AddPage(DonePage);

            var root = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
            Content = root;

            var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var skipBtn = MakeButton(Loc.T("Setup_Skip"), primary: false);
            skipBtn.IsCancel = true;
            skipBtn.Click += (s, e) => Close();
            DockPanel.SetDock(skipBtn, Dock.Left);
            footer.Children.Add(skipBtn);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            _backBtn = MakeButton(Loc.T("Setup_Back"), primary: false);
            _backBtn.Margin = new Thickness(0, 0, 10, 0);
            _backBtn.Click += (s, e) => Show(Step(_index, -1));
            right.Children.Add(_backBtn);
            _nextBtn = MakeButton(Loc.T("Setup_Next"), primary: true);
            _nextBtn.IsDefault = true;
            _nextBtn.Click += (s, e) =>
            {
                int next = Step(_index, +1);
                if (next < 0) { Finish(); return; }
                Show(next);
            };
            right.Children.Add(_nextBtn);
            footer.Children.Add(right);

            _stepText = new TextBlock
            {
                Foreground = MutedFg, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            footer.Children.Add(_stepText);

            root.Children.Add(_host);
            Show(0);
        }

        private void AddPage(Func<FrameworkElement> build, Func<bool> shows = null)
        {
            _pages.Add(build);
            _pageShows.Add(shows ?? (() => true));
        }

        private bool PageShows(int i) => i >= 0 && i < _pages.Count && Safe(_pageShows[i], true);

        /// <summary>The next page that shows in the given direction, or -1.</summary>
        private int Step(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _pages.Count; i += dir)
                if (PageShows(i)) return i;
            return -1;
        }

        /// <summary>True when no page after the current one will show.</summary>
        private bool OnLastPage => Step(_index, +1) < 0;

        private void Show(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            _index = index;
            if (index == _welcomeIndex) WelcomeShown = true;
            _afterShow = null;
            _host.Content = _pages[index]();
            _backBtn.Visibility = Step(index, -1) < 0 ? Visibility.Hidden : Visibility.Visible;
            _nextBtn.Content = OnLastPage ? Loc.T("Setup_Finish") : Loc.T("Setup_Next");
            _afterShow?.Invoke();
            // Counted over the pages that show right now, so the total can grow
            // by one when the wheel turns up mid-way.
            int position = 0, total = 0;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (!PageShows(i)) continue;
                total++;
                if (i <= index) position++;
            }
            _stepText.Text = Loc.F("Setup_StepOf_Fmt", position, total);
        }

        private void Finish()
        {
            DialogResult = true;
            Close();
        }

        // ---- Pages ----

        private FrameworkElement IntroPage()
        {
            var page = Page(Loc.T("Setup_IntroHeading"), Loc.T("Setup_IntroTagline"));
            page.Children.Add(Feature(Loc.T("Setup_IntroTrueforceTitle"), Loc.T("Setup_IntroTrueforceBody")));
            page.Children.Add(Feature(Loc.T("Setup_IntroLightsTitle"), Loc.T("Setup_IntroLightsBody")));
            page.Children.Add(Feature(Loc.T("Setup_IntroScreenTitle"), Loc.T("Setup_IntroScreenBody")));
            page.Children.Add(Feature(Loc.T("Setup_IntroFfbTitle"), Loc.T("Setup_IntroFfbBody")));
            page.Children.Add(Feature(Loc.T("Setup_IntroDashTitle"), Loc.T("Setup_IntroDashBody")));
            page.Children.Add(new TextBlock
            {
                Text = Loc.T("Setup_IntroSetupTakes"),
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            return page;
        }

        private FrameworkElement WelcomePage() => WelcomeWindow.BuildBody();

        private FrameworkElement AccountPage()
        {
            // Already signed in when the page opens (the caller only includes it
            // then when Discord is not linked yet): the page is about Discord.
            bool signedInAtOpen = Safe(_isSignedIn, false);
            var page = signedInAtOpen
                ? Page(Loc.T("Setup_DiscordHeading"), Loc.T("Setup_DiscordBody"))
                : Page(Loc.T("Setup_AccountHeading"), Loc.T("Setup_AccountBody"));
            var signIn = MakeButton(Loc.T("Welcome_SignIn"), primary: true);
            signIn.HorizontalAlignment = HorizontalAlignment.Left;
            var signedIn = new TextBlock
            {
                Text = Loc.T("Setup_AccountSignedIn"),
                Foreground = OkFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            // Linking Discord needs the account (it ties contributions to the
            // person), so it only appears once signed in. Joining the server
            // itself needs nothing from us.
            var discordBtn = MakeButton(Loc.T("Setup_LinkDiscord"), primary: false);
            discordBtn.HorizontalAlignment = HorizontalAlignment.Left;
            discordBtn.Margin = new Thickness(0, 10, 0, 0);
            var discordStatus = new TextBlock
            {
                Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
            };

            // Nothing left to do here once linked; until then, moving on is a skip.
            void SetWayForward(bool finished)
            {
                if (IsShowing(page) && !OnLastPage)
                    _nextBtn.Content = finished ? Loc.T("Setup_Next") : Loc.T("Setup_SkipStep");
            }

            // justLinked: the link was made on this page, so "now" is true; an
            // account that arrived already linked gets its own line.
            void ShowDiscordState(bool linked, bool justLinked)
            {
                SetWayForward(linked);
                discordBtn.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
                if (linked)
                {
                    discordStatus.Text = justLinked ? Loc.T("Setup_DiscordLinked") : Loc.T("Setup_DiscordAlreadyLinked");
                    discordStatus.Foreground = OkFg;
                    discordStatus.Visibility = Visibility.Visible;
                }
            }

            async void Sync()
            {
                bool done = Safe(_isSignedIn, false);
                signIn.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
                // "You're signed in" only reports a sign-in made on this page.
                signedIn.Visibility = done && !signedInAtOpen ? Visibility.Visible : Visibility.Collapsed;
                // Optional either way: a skip until signed in and linked.
                SetWayForward(false);
                // Hidden until the link state is known, so an account that is
                // already linked never flashes a button it does not need.
                discordBtn.Visibility = Visibility.Collapsed;
                if (!done) return;
                bool linked = false;
                try { if (_discordLinked != null) linked = await _discordLinked(); }
                catch { }
                ShowDiscordState(linked, justLinked: false);
            }

            signIn.Click += (s, e) =>
            {
                try { _signIn?.Invoke(this); } catch { }
                Sync();
            };
            discordBtn.Click += async (s, e) =>
            {
                if (_linkDiscord == null) return;
                discordBtn.IsEnabled = false;
                discordStatus.Text = Loc.T("Account_DiscordLinkStatusOpening");
                discordStatus.Foreground = MutedFg;
                discordStatus.Visibility = Visibility.Visible;
                bool linked = false;
                try { linked = await _linkDiscord(); } catch { }
                discordBtn.IsEnabled = true;
                if (linked) ShowDiscordState(true, justLinked: true);
                else discordStatus.Text = Loc.T("Setup_DiscordNotLinked");
            };

            page.Children.Add(signIn);
            page.Children.Add(signedIn);
            page.Children.Add(discordBtn);
            page.Children.Add(discordStatus);
            page.Children.Add(new TextBlock
            {
                Text = Loc.T("Setup_AccountLater"),
                Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });
            _afterShow = Sync;
            return page;
        }

        private bool IsShowing(FrameworkElement page) => ReferenceEquals(_host.Content, page);

        private FrameworkElement ChecksPage()
        {
            var page = Page(Loc.T("Setup_ChecksHeading"), Loc.T("Setup_ChecksIntro"));
            var rows = new StackPanel();
            page.Children.Add(rows);

            void Fill()
            {
                rows.Children.Clear();
                // The wheel first: without one, nothing else on this page matters.
                bool wheel = Safe(_wheelFound, true);
                string name = null;
                try { name = _wheelName?.Invoke(); } catch { }
                rows.Children.Add(CheckRow(wheel, wheel
                    ? Loc.F("Setup_CheckWheelOk_Fmt", string.IsNullOrEmpty(name) ? Loc.T("Setup_CheckWheelGeneric") : name)
                    : Loc.T("Setup_CheckWheelMissing")));
                bool elevated = Safe(_isElevated, true);
                rows.Children.Add(CheckRow(elevated,
                    elevated ? Loc.T("Setup_CheckAdminOk") : Loc.T("Header_AdminWarningSimHubIsNotRunning")));
                bool ghub = Safe(_isGHubRunning, false);
                rows.Children.Add(CheckRow(!ghub,
                    ghub ? Loc.T("Header_GHubWarningLogitechGHubIsRunning") : Loc.T("Setup_CheckGHubOk")));
            }
            Fill();

            var again = MakeButton(Loc.T("Setup_CheckAgain"), primary: false);
            again.HorizontalAlignment = HorizontalAlignment.Left;
            again.Margin = new Thickness(26, 4, 0, 0);
            again.Click += (s, e) => Fill();
            page.Children.Add(again);
            return page;
        }

        private FrameworkElement ViewPage()
        {
            var page = Page(Loc.T("Setup_ViewHeading"), Loc.T("Setup_ViewIntro"));
            page.Children.Add(Choice("SetupView", Loc.T("Effects_ModeSimple"), Loc.T("Effects_ModeSimple_Tip"),
                _simple, () => { _simple = true; _setSimpleMode?.Invoke(true); }));
            page.Children.Add(Choice("SetupView", Loc.T("Effects_ModeAdvanced"), Loc.T("Effects_ModeAdvanced_Tip"),
                !_simple, () => { _simple = false; _setSimpleMode?.Invoke(false); }));
            return page;
        }

        private FrameworkElement LightsPage()
        {
            bool programmable = Safe(_lightsProgrammable, false);
            var page = Page(Loc.T("Setup_LightsPageHeading"), Loc.T("Setup_LightsPageIntro"));

            // Car-matched patterns borrow a custom slot, so they only exist on a
            // wheel whose pattern can be changed (G PRO, RS50).
            if (programmable)
            {
                page.Children.Add(SubHeading(Loc.T("Setup_LightsHeading")));
                // One switch: unticked is simply the pattern the user picked, the
                // same for every car, so it needs no option of its own.
                page.Children.Add(Toggle(Loc.T("Setup_LightsMatch"), Loc.T("Lightsync_GivesYourWheelThe"),
                    _lovely, on => { _lovely = on; _setLovely?.Invoke(on); }));
                // The dataset's licence requires the credit wherever its patterns
                // are offered, so it rides along here as it does on the LIGHTSYNC tab.
                page.Children.Add(new TextBlock
                {
                    Text = Loc.T("Setup_LightsCredit"),
                    Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14),
                });
            }

            // Every wheel: what the strip does while nobody is driving.
            page.Children.Add(SubHeading(Loc.T("Setup_IdleHeading")));
            page.Children.Add(Choice("SetupIdle", Loc.T("Header_MasterModeOff"), Loc.T("Setup_IdleOffDetail"),
                _idle == AmbientLedMode.Off, () => PickIdle(AmbientLedMode.Off)));
            page.Children.Add(Choice("SetupIdle", Loc.T("Settings_Sweep"), Loc.T("Setup_IdleSweepDetail"),
                _idle == AmbientLedMode.Sweep, () => PickIdle(AmbientLedMode.Sweep)));
            page.Children.Add(Choice("SetupIdle", Loc.T("Settings_AudioLevel"), Loc.T("Setup_IdleAudioDetail"),
                _idle == AmbientLedMode.AudioLevel, () => PickIdle(AmbientLedMode.AudioLevel)));

            // Where these live afterwards depends on the wheel: the LIGHTSYNC &
            // OLED tab on a programmable one, the Telemetry FFB tab otherwise.
            page.Children.Add(new TextBlock
            {
                Text = programmable ? Loc.T("Setup_LightsLater") : Loc.T("Setup_LightsLaterFixed"),
                Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return page;
        }

        private void PickIdle(AmbientLedMode mode)
        {
            _idle = mode;
            _setIdle?.Invoke(mode);
        }

        private static TextBlock SubHeading(string text) => new TextBlock
        {
            Text = text, Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };

        private FrameworkElement CarFactsPage()
        {
            var page = Page(Loc.T("Setup_CarFactsHeading"), Loc.T("Setup_CarFactsBody"));
            page.Children.Add(new TextBlock
            {
                Text = Loc.T("Setup_CarFactsFix"),
                Foreground = TextFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            return page;
        }

        private FrameworkElement DonePage()
        {
            var page = Page(Loc.T("Setup_DoneHeading"), Loc.T("Setup_DoneBody"));
            page.Children.Add(new TextBlock
            {
                Text = Loc.T("Setup_GoodToKnow"), Foreground = TextFg, FontSize = 13,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
            });
            page.Children.Add(Bullet(Loc.T("Setup_GoodToKnowNative")));
            page.Children.Add(Bullet(Loc.T("Setup_GoodToKnowLaunchOrder")));
            page.Children.Add(Bullet(Loc.T("Setup_GoodToKnowHelp")));
            return page;
        }

        // ---- Building blocks ----

        private static StackPanel Page(string heading, string intro)
        {
            var page = new StackPanel();
            page.Children.Add(new TextBlock
            {
                Text = heading, Foreground = HeaderFg, FontWeight = FontWeights.SemiBold, FontSize = 17,
                Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
            });
            page.Children.Add(new TextBlock
            {
                Text = intro, Foreground = MutedFg, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap,
            });
            return page;
        }

        // A headline feature: a bold title, then what it gives the driver.
        private static FrameworkElement Feature(string title, string body)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = "\u25CF", Foreground = OkFg, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0),
            });
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
            });
            stack.Children.Add(new TextBlock { Text = body, Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            return grid;
        }

        private static FrameworkElement Bullet(string text)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = "\u25CF", Foreground = OkFg, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0),
            });
            var body = new TextBlock { Text = text, Foreground = MutedFg, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            return grid;
        }

        private static FrameworkElement CheckRow(bool ok, string text)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var mark = new TextBlock
            {
                Text = ok ? "✓" : "!", Foreground = ok ? OkFg : WarnFg,
                FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Top,
            };
            grid.Children.Add(mark);
            var body = new TextBlock
            {
                Text = text, Foreground = TextFg, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            return grid;
        }

        private static CheckBox Toggle(string title, string detail, bool isChecked, Action<bool> onChange)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(detail))
                stack.Children.Add(new TextBlock
                {
                    Text = detail, Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            var cb = new CheckBox
            {
                Content = stack, IsChecked = isChecked, Foreground = TextFg,
                Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Top,
            };
            // Hooked after IsChecked is set, so building the page applies nothing.
            cb.Checked += (s, e) => onChange(true);
            cb.Unchecked += (s, e) => onChange(false);
            return cb;
        }

        private static RadioButton Choice(string group, string title, string detail, bool isChecked, Action onPick)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, Foreground = TextFg, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(detail))
                stack.Children.Add(new TextBlock
                {
                    Text = detail, Foreground = MutedFg, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            var rb = new RadioButton
            {
                GroupName = group, Content = stack, IsChecked = isChecked,
                Foreground = TextFg, Margin = new Thickness(0, 0, 0, 12),
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            // Hooked after IsChecked is set, so building the page applies nothing.
            rb.Checked += (s, e) => onPick();
            return rb;
        }

        private static Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                Foreground = primary ? WindowBg : TextFg,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Style = primary
                    ? WelcomeWindow.MakeFilledButtonStyle(WindowBg,
                        Color.FromRgb(0xE5, 0xC0, 0x4A), Color.FromRgb(0xF2, 0xD3, 0x71), Color.FromRgb(0xCF, 0xA9, 0x3A))
                    : WelcomeWindow.MakeFilledButtonStyle(TextFg,
                        Color.FromRgb(0x33, 0x33, 0x33), Color.FromRgb(0x40, 0x40, 0x40), Color.FromRgb(0x2B, 0x2B, 0x2B)),
            };
        }

        private static bool Safe(Func<bool> f, bool fallback)
        {
            try { return f != null ? f() : fallback; } catch { return fallback; }
        }
    }
}
