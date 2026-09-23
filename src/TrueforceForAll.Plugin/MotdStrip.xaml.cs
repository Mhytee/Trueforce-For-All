// The Message-of-the-Day strip. A thin, dismissable bar at the top of the
// plugin UI (above the tabs) that surfaces short owner-authored messages.
//
// Behavior (see docs/motd-design.md):
//   * Gated entirely under CommunityEnabled and the user's MotdLevel choice.
//   * Renders from the offline-first cache; refreshes in the background when the
//     ~6h TTL has lapsed (a failed fetch keeps the cache; an empty success
//     clears it). Empty -> the strip collapses to nothing.
//   * Multiple active messages rotate every 8s; rotation pauses while a message
//     is expanded. Scheduled messages take precedence; when none are active a
//     single pool message is shown, picked deterministically per day.
//   * Dismiss is per-message: scheduled = permanent (seen-id), pool = for-today.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueforceForAll.Plugin
{
    public partial class MotdStrip : UserControl
    {
        private TrueforcePlugin _plugin;
        private readonly DispatcherTimer _timer;
        private List<MotdItem> _display = new List<MotdItem>();
        private int _index;
        private bool _expanded;
        private bool _fetchInFlight;
        private bool _audienceChecked;
        // Dev-only date override for previewing MOTD on a chosen/random day
        // (MOTDROLL / MOTDDATE access codes). null = the real clock.
        private DateTime? _simDate;
        // Session-only "dismissed" ids while previewing: lets dismiss work in preview
        // (removes from the current preview view) without ever touching real dismiss
        // state. Reset whenever the preview date changes or preview is exited.
        private readonly System.Collections.Generic.HashSet<string> _simDismissed
            = new System.Collections.Generic.HashSet<string>();
        private static readonly Random _rng = new Random();

        /// <summary>Set by the host: invoked when a message's Share button is
        /// clicked (opens the in-plugin Spread-the-word modal).</summary>
        internal Action ShareRequested;
        internal Action LinkDiscordRequested;

        // Daily-flow tuning (docs/motd-design.md). All "random" rolls derive from
        // the LOCAL date, so they're stable within a day (no flicker on re-render)
        // and vary day to day.
        private const int MaxMessagesPerDay = 3;   // caps the bonus, not genuine scheduled
        private const int BonusPercent      = 30;  // chance/day of a second pool message
        private const int NagPercent        = 20;  // chance/day a nag is eligible
        private const int NagCooldownDays   = 3;   // minimum days between nags
        // Support (the Patreon ask) runs on its own budget so its rate is set here
        // rather than by how many other nags happen to be in the pool. It still
        // takes the one nag slot for the day when it fires.
        private const int SupportPercent      = 25; // chance/day the support ask is eligible
        // 14, not 7: the periodic support modal on the plugin page now carries the
        // bulk of the ask, so the strip backs off to keep the two from stacking.
        private const int SupportCooldownDays = 14; // minimum days between support asks

        private static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(8);
        private static readonly Brush AccentNormal    = MakeBrush("#3A6BBE");
        private static readonly Brush AccentImportant = MakeBrush("#FFC107");
        // Dimmer tone for a quote's author attribution, so it sits back from the line.
        private static readonly Brush AttributionDim  = MakeBrush("#9AA0A8");

        // Splits a quote body "Some line. (Author)" into its text and trailing-
        // parenthetical author. A body with no parenthetical (an unattributed
        // proverb) leaves group 2 empty and is shown in quotation marks alone.
        private static readonly Regex QuoteAuthor =
            new Regex(@"^(.*?)\s*\(([^()]+)\)\s*$", RegexOptions.Singleline | RegexOptions.Compiled);

        public MotdStrip()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = RotateInterval };
            _timer.Tick += OnTick;
            // Re-evaluate the "more" button when the row width changes (window resize,
            // or a link/share button taking space), since fit depends on width.
            BodyText.SizeChanged += (s, e) => UpdateMoreButton();
        }

        public void Init(TrueforcePlugin plugin)
        {
            _plugin = plugin;
            Rebuild();
        }

        /// <summary>Re-evaluate from settings + cache, and kick a refresh if the
        /// gate just opened. Call after CommunityEnabled or MotdLevel changes.</summary>
        public void Refresh(bool forceFetch = false)
        {
            Rebuild();
            MaybeRefresh(forceFetch);
        }

        /// <summary>Host hook: on a tab/view switch, collapse any expanded
        /// message so the auto-cycle resumes cleanly.</summary>
        public void OnHostViewChanged()
        {
            if (_expanded) { _expanded = false; RenderCurrent(); }
        }

        // The "now" the strip evaluates against: the simulated date if a dev
        // preview is active, otherwise the real clock.
        private DateTime EffNow => _simDate ?? DateTime.Now;

        /// <summary>Dev preview: render the strip as if it were <paramref name="date"/>
        /// (recurrence, anniversaries, and the day-stable pool/nag rolls all shift to
        /// it; dismissals + nag cooldown are bypassed so nothing is hidden). Returns a
        /// short summary for the access-code status.</summary>
        internal string SimulateDate(DateTime date)
        {
            _simDate = date.Date;
            _simDismissed.Clear();   // fresh preview: no simulated dismissals yet
            Rebuild();
            int n = _display?.Count ?? 0;
            return _simDate.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
                 + ": " + n + (n == 1 ? " message" : " messages");
        }

        /// <summary>Dev preview: pick a random day within the next year and simulate it.</summary>
        internal string SimulateRandomDay()
            => SimulateDate(DateTime.Now.Date.AddDays(_rng.Next(0, 365)));

        /// <summary>Leave dev preview mode and return to the real date.</summary>
        internal void ClearSimulation()
        {
            _simDate = null;
            _simDismissed.Clear();
            Rebuild();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Rebuild();
            if (!_timer.IsEnabled) _timer.Start();
            // Panel load = activity, so the start-up MaybeRefresh below is allowed to fetch when the
            // TTL has lapsed (the fetch is otherwise suppressed while idle to avoid background calls).
            try { _plugin?.NoteUserActivity(); } catch { }
            MaybeRefresh();
            EnsureAudienceKnown();
        }

        // Resolve supporter + Discord-link status once (async, signed-in only) so the
        // audience filter can hide support / Discord nudges from people who already
        // support or are in the server. Contribution-recency signals are local, so
        // they need no network. Re-renders when known.
        private void EnsureAudienceKnown()
        {
            if (_audienceChecked || _plugin == null) return;
            _audienceChecked = true;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await _plugin.GetSupporterTierAsync(System.Threading.CancellationToken.None).ConfigureAwait(false); }
                catch { }
                try { await _plugin.GetDiscordStatusAsync(System.Threading.CancellationToken.None).ConfigureAwait(false); }
                catch { }
                _ = Dispatcher.BeginInvoke(new Action(() => Rebuild()));
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

        private void OnTick(object sender, EventArgs e)
        {
            // Cheap staleness check covers sessions left running past the TTL.
            MaybeRefresh();
            // Pause rotation while expanded so there's time to read.
            if (_expanded || _display.Count <= 1) return;
            _index = (_index + 1) % _display.Count;
            RenderCurrent();
        }

        // ---- display set ----------------------------------------------------
        private void Rebuild()
        {
            _display = BuildDisplay();
            if (_index >= _display.Count) _index = 0;
            _expanded = false;
            RenderCurrent();
        }

        private List<MotdItem> BuildDisplay()
        {
            var s = _plugin?.Settings;
            // Gated under CommunityEnabled (networked feature) + the level choice.
            if (s == null || s.CommunityEnabled != true || s.MotdLevel == MotdLevel.None)
                return new List<MotdItem>();
            var items = s.MotdCache?.Items;
            if (items == null || items.Count == 0) return new List<MotdItem>();

            bool importantOnly = s.MotdLevel == MotdLevel.Important;
            var dismissed = s.MotdDismissedIds ?? new List<string>();
            var poolDismissed = s.MotdPoolDismissedOn ?? new Dictionary<string, string>();
            var recurDismissed = s.MotdRecurringDismissedOcc ?? new Dictionary<string, string>();
            bool sim = _simDate != null;
            DateTime todayDate = EffNow.Date;
            string today = todayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var scheduled = new List<MotdItem>();
            var pool = new List<MotdItem>();
            foreach (var m in items)
            {
                if (m == null || string.IsNullOrEmpty(m.Id) || string.IsNullOrWhiteSpace(m.Body)) continue;
                if (importantOnly && !m.IsImportant) continue;
                // Per-user audience filter (supporter / Discord / contribution recency).
                if (!AudienceAllows(m)) continue;
                if (m.IsPool)
                {
                    // Keep dismissed pool items as PICK candidates so the daily pick
                    // stays stable; dismissed picks are dropped from display below.
                    // (Filtering here would make dismissing today's pick shift to the
                    // next item, cycling through the whole pool one dismiss at a time.)
                    pool.Add(m);
                }
                else if (m.IsRecurring)
                {
                    // Show only inside this year's window; dismiss is per-occurrence.
                    string occ = ActiveOccurrenceToken(m, todayDate);
                    if (occ == null) continue;
                    bool gone = sim ? _simDismissed.Contains(m.Id)
                                    : (recurDismissed.TryGetValue(m.Id, out var rt) && rt == occ);
                    if (gone) continue;
                    scheduled.Add(m);
                }
                else
                {
                    bool gone = sim ? _simDismissed.Contains(m.Id) : dismissed.Contains(m.Id);
                    if (gone) continue;
                    scheduled.Add(m);
                }
            }

            // Dismissed-today check for a pool item (session-only set while previewing).
            bool PoolDismissed(MotdItem mi) => sim
                ? _simDismissed.Contains(mi.Id)
                : (poolDismissed.TryGetValue(mi.Id, out var on) && on == today);

            // Important-only: show every important message (rare/critical), no pacing.
            if (importantOnly)
            {
                var imp = new List<MotdItem>(scheduled);
                foreach (var p in pool) if (!PoolDismissed(p)) imp.Add(p);
                return imp;
            }

            // Normal flow: all active scheduled, then a paced pool pick on top.
            // (1) all scheduled, (2) a base pool item only when nothing is scheduled
            // (guarantees "never none"), (3) a day-stable bonus pool item, capped at
            // MaxMessagesPerDay total. The pick is computed over the FULL pool so it
            // stays stable; dismissed picks are then dropped from display, so dismissing
            // the day's message clears the strip instead of cycling to the next item.
            var result = new List<MotdItem>(scheduled);
            int dayKey = todayDate.Year * 10000 + todayDate.Month * 100 + todayDate.Day;
            int capacity = MaxMessagesPerDay - result.Count;

            int desired = (result.Count == 0 && pool.Count > 0) ? 1 : 0;
            if (DayChance(dayKey, "bonus", BonusPercent)) desired++;
            if (desired > capacity) desired = capacity;
            if (desired > pool.Count) desired = pool.Count;

            if (desired > 0)
                foreach (var p in PickPoolForDay(pool, desired, dayKey, today))
                    if (!PoolDismissed(p)) result.Add(p);
            return result;
        }

        // Whether a row passes the user's audience filter. null tag = everyone (a
        // legacy untagged `support` row still spares confirmed supporters). Signals:
        // supporter + Discord-link are resolved by EnsureAudienceKnown; the three
        // contribution recencies are local (LastSharedPresetOn etc.).
        private bool AudienceAllows(MotdItem m)
        {
            string a = m.Audience;
            if (string.IsNullOrEmpty(a))
                return !(string.Equals(m.Category, "support", StringComparison.OrdinalIgnoreCase)
                         && _plugin.LastKnownSupporter);
            switch (a)
            {
                case "supporter":         return _plugin.LastKnownSupporter;
                case "non_supporter":     return !_plugin.LastKnownSupporter;
                case "non_discord":       return !_plugin.LastKnownDiscordLinked;
                case "non_sharer":        return !ContributedRecently(_plugin.Settings?.LastSharedPresetOn);
                case "non_voter":         return !ContributedRecently(_plugin.Settings?.LastVotedOn);
                case "non_factsubmitter": return !ContributedRecently(_plugin.Settings?.LastSubmittedFactOn);
                default:                  return true; // unknown tag: show rather than hide
            }
        }

        private static bool ContributedRecently(DateTime? when)
            => when.HasValue && (DateTime.UtcNow - when.Value) < TrueforceSettings.MotdContributionRecency;

        // Choose `desired` distinct pool messages for the day, weighting toward
        // non-nags and allowing at most one nag, gated by a cooldown plus a daily
        // chance. Support draws on its own cooldown and gets first refusal on the
        // nag slot; the other nags then share what's left, so adding community or
        // share rows can no longer dilute the money ask. Picks are day-stable so
        // the strip doesn't reshuffle on re-render.
        private List<MotdItem> PickPoolForDay(List<MotdItem> pool, int desired, int dayKey, string today)
        {
            var support = new List<MotdItem>();
            var nags = new List<MotdItem>();
            var calm = new List<MotdItem>();
            foreach (var m in pool)
                (m.IsSupportNag ? support : m.IsNag ? nags : calm).Add(m);
            support.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            nags.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            calm.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            bool sim = _simDate != null;
            // Support first. Its roll and cooldown are independent, and firing it
            // stamps the shared nag date too, so the day still carries just one.
            bool supportToday = support.Count > 0 && (sim || SupportCooldownOk(today))
                                && DayChance(dayKey, "supportnag", SupportPercent);
            bool nagToday = !supportToday && nags.Count > 0 && (sim || NagCooldownOk(today))
                            && DayChance(dayKey, "nag", NagPercent);

            var picks = new List<MotdItem>();
            int calmWanted = desired - ((supportToday || nagToday) ? 1 : 0);
            if (calmWanted < 0) calmWanted = 0;
            AppendDistinct(picks, calm, calmWanted, dayKey, "calm");

            if (supportToday)
            {
                picks.Add(support[DayHash(dayKey, "supportpick") % support.Count]);
                if (!sim) NoteSupportNagShown(today);
            }
            else if (nagToday)
            {
                picks.Add(nags[DayHash(dayKey, "nagpick") % nags.Count]);
                if (!sim) NoteNagShown(today);
            }

            // Safety net: if calm couldn't fill a guaranteed slot (tiny pool) and we
            // aren't already showing a nag, fall back to one so we never under-fill.
            // In practice the non-nag pool is large and this never runs.
            if (picks.Count == 0 && desired > 0)
            {
                if (nags.Count > 0)
                {
                    picks.Add(nags[DayHash(dayKey, "nagfallback") % nags.Count]);
                    if (!sim) NoteNagShown(today);
                }
                else if (support.Count > 0)
                {
                    picks.Add(support[DayHash(dayKey, "supportfallback") % support.Count]);
                    if (!sim) NoteSupportNagShown(today);
                }
            }
            return picks;
        }

        // A nag is allowed today if one is already showing today (so it stays stable
        // across re-renders) or enough days have passed since the last one.
        private bool NagCooldownOk(string today)
        {
            string last = _plugin?.Settings?.MotdLastNagOn;
            if (string.IsNullOrEmpty(last)) return true;
            if (last == today) return true;
            if (DateTime.TryParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var lastDate))
                return (DateTime.Now.Date - lastDate).TotalDays >= NagCooldownDays;
            return true;
        }

        private void NoteNagShown(string today)
        {
            var s = _plugin?.Settings;
            if (s == null || s.MotdLastNagOn == today) return;
            s.MotdLastNagOn = today;
            try { _plugin.SaveMotdState(); } catch { }
        }

        // Same shape as NagCooldownOk, against the support-only date.
        private bool SupportCooldownOk(string today)
        {
            string last = _plugin?.Settings?.MotdLastSupportNagOn;
            if (string.IsNullOrEmpty(last)) return true;
            if (last == today) return true;
            if (DateTime.TryParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var lastDate))
                return (DateTime.Now.Date - lastDate).TotalDays >= SupportCooldownDays;
            return true;
        }

        // A support ask stamps both dates: its own cooldown, and the shared nag one
        // so the other nags stay off today and space themselves from it.
        private void NoteSupportNagShown(string today)
        {
            var s = _plugin?.Settings;
            if (s == null || (s.MotdLastSupportNagOn == today && s.MotdLastNagOn == today)) return;
            s.MotdLastSupportNagOn = today;
            s.MotdLastNagOn = today;
            try { _plugin.SaveMotdState(); } catch { }
        }

        // Append up to `count` distinct items from `source`, chosen day-stably. For
        // count <= 2 (all this needs) the start + stepped index are always distinct.
        private static void AppendDistinct(List<MotdItem> picks, List<MotdItem> source,
            int count, int dayKey, string salt)
        {
            if (count <= 0 || source.Count == 0) return;
            int n = source.Count;
            int start = DayHash(dayKey, salt) % n;
            int step = (n > 1) ? (1 + DayHash(dayKey, salt + "step") % (n - 1)) : 1;
            var seen = new HashSet<int>();
            int idx = start, added = 0;
            for (int i = 0; i < n && added < count; i++)
            {
                if (seen.Add(idx)) { picks.Add(source[idx]); added++; }
                idx = (idx + step) % n;
            }
        }

        // Stable (process-independent) hash of (salt, dayKey): FNV-1a. .NET's
        // string.GetHashCode is randomized per run, which would reshuffle picks on
        // restart, so we roll our own.
        private static int DayHash(int dayKey, string salt)
        {
            unchecked
            {
                uint h = 2166136261u;
                string s = salt + ":" + dayKey.ToString(CultureInfo.InvariantCulture);
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return (int)(h & 0x7fffffff);
            }
        }

        private static bool DayChance(int dayKey, string salt, int percent)
            => (DayHash(dayKey, salt) % 100) < percent;

        // If today (local) is inside a yearly message's window, returns that
        // occurrence's start-date token ("yyyy-MM-dd"); otherwise null. Checks
        // this year and last year so a Dec->Jan window wrap is handled, and
        // clamps Feb 29 to the last day of February in common years.
        private static string ActiveOccurrenceToken(MotdItem m, DateTime today)
        {
            if (!m.IsRecurring) return null;
            int month = m.RecurMonth.Value, day = m.RecurDay.Value;
            if (month < 1 || month > 12 || day < 1 || day > 31) return null;
            int window = m.RecurWindowDays > 0 ? m.RecurWindowDays : 1;
            for (int y = today.Year; y >= today.Year - 1; y--)
            {
                int dim = DateTime.DaysInMonth(y, month);
                var occ = new DateTime(y, month, Math.Min(day, dim));
                if (today >= occ && today < occ.AddDays(window))
                    return occ.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return null;
        }

        // Substitutes the one supported template token, {years}, in a recurring
        // message that carries an anchor year: (occurrence year - anchor year),
        // clamped at 0. Lets a birthday / anniversary message age itself.
        private string DisplayBody(MotdItem m)
        {
            string body = m?.Body ?? "";
            if (m != null && m.IsRecurring && m.AnchorYear.HasValue
                && body.IndexOf("{years}", StringComparison.Ordinal) >= 0)
            {
                var now = EffNow;
                string occ = ActiveOccurrenceToken(m, now.Date);
                int occYear = now.Year;
                if (occ != null && occ.Length >= 4 && int.TryParse(occ.Substring(0, 4), out var y))
                    occYear = y;
                int years = occYear - m.AnchorYear.Value;
                if (years < 0) years = 0;
                body = body.Replace("{years}", years.ToString(CultureInfo.InvariantCulture));
            }
            return body;
        }

        // ---- render ---------------------------------------------------------
        private void RenderCurrent()
        {
            if (_display.Count == 0)
            {
                StripRoot.Visibility = Visibility.Collapsed;
                return;
            }
            if (_index >= _display.Count) _index = 0;
            var m = _display[_index];
            string shown = DisplayBody(m);

            SetBodyContent(m, shown);
            BodyText.TextWrapping = _expanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
            BodyText.TextTrimming = _expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;

            // Important messages get an amber accent + warning glyph; otherwise a
            // blue accent with a megaphone (scheduled) or bulb (pool/tip).
            if (m.IsImportant)
            {
                StripRoot.BorderBrush = AccentImportant;
                IconText.Text = "⚠";            // warning
            }
            else
            {
                StripRoot.BorderBrush = AccentNormal;
                IconText.Text = m.IsPool ? "\U0001F4A1" /* bulb */
                              : m.IsRecurring ? "\U0001F4C5" /* calendar */
                              : "\U0001F4E3"; /* megaphone */
            }

            // "more"/"less" depends on whether the text actually overflows the row,
            // not on its length. Deferred to after layout so ActualWidth is valid.
            _ = Dispatcher.BeginInvoke(new Action(UpdateMoreButton),
                System.Windows.Threading.DispatcherPriority.Loaded);

            if (m.IsShareAction)
            {
                LinkButton.Visibility = Visibility.Visible;
                LinkButton.Content = string.IsNullOrWhiteSpace(m.LinkLabel) ? "Share" : m.LinkLabel;
            }
            else if (m.IsLinkDiscordAction)
            {
                LinkButton.Visibility = Visibility.Visible;
                LinkButton.Content = string.IsNullOrWhiteSpace(m.LinkLabel) ? "Link Discord" : m.LinkLabel;
            }
            else if (m.HasLink)
            {
                LinkButton.Visibility = Visibility.Visible;
                LinkButton.Content = string.IsNullOrWhiteSpace(m.LinkLabel) ? "Learn more" : m.LinkLabel;
            }
            else LinkButton.Visibility = Visibility.Collapsed;

            if (_display.Count > 1)
            {
                CountText.Visibility = Visibility.Visible;
                CountText.Text = (_index + 1) + "/" + _display.Count;
            }
            else CountText.Visibility = Visibility.Collapsed;

            StripRoot.Visibility = Visibility.Visible;
        }

        // Puts the message body into the strip. Quotes get typographic treatment
        // (curly quotation marks, italic text, a dimmer en-dash attribution) so they
        // read as quotes at a glance rather than plain text with a parenthesised
        // name; everything else is plain text. Works the same collapsed (single line,
        // ellipsis) and expanded (wrapped), since both are just inline runs.
        private void SetBodyContent(MotdItem m, string shown)
        {
            if (m != null && m.IsQuote && !string.IsNullOrWhiteSpace(shown))
            {
                var match = QuoteAuthor.Match(shown);
                string quote  = match.Success ? match.Groups[1].Value.Trim() : shown.Trim();
                string author = match.Success ? match.Groups[2].Value.Trim() : null;
                if (quote.Length == 0) { quote = shown.Trim(); author = null; } // all-parens guard

                BodyText.Inlines.Clear();
                // Curly quotation marks + en-dash attribution, written as ASCII escapes so the
                // source compiles identically regardless of file encoding.
                BodyText.Inlines.Add(new Run("\u201C" + quote + "\u201D") { FontStyle = FontStyles.Italic });
                if (!string.IsNullOrEmpty(author))
                    BodyText.Inlines.Add(new Run(" \u2013 " + author) { Foreground = AttributionDim });
                return;
            }
            // Setting .Text clears any prior inline runs (e.g. a quote shown before
            // rotating to this message), so plain messages render cleanly.
            BodyText.Text = shown;
        }

        // Show "more" only when the collapsed message is actually clipped by the row
        // width; show "less" while expanded so it can always be collapsed. Width-based,
        // so it accounts for a link/share button taking space and for window resizes.
        private void UpdateMoreButton()
        {
            if (MoreButton == null) return;
            if (_display.Count == 0) { MoreButton.Visibility = Visibility.Collapsed; return; }
            if (_expanded)
            {
                MoreButton.Content = "less";
                MoreButton.Visibility = Visibility.Visible;
                return;
            }
            MoreButton.Content = "more";
            MoreButton.Visibility = IsBodyTextTrimmed() ? Visibility.Visible : Visibility.Collapsed;
        }

        // True when the body's full single-line text is wider than the space the row
        // actually gives it (i.e. CharacterEllipsis is clipping it).
        private bool IsBodyTextTrimmed()
        {
            try
            {
                double avail = BodyText.ActualWidth;
                string text = BodyText.Text ?? "";
                if (avail <= 0 || text.Length == 0) return false;
                var typeface = new Typeface(BodyText.FontFamily, BodyText.FontStyle,
                    BodyText.FontWeight, BodyText.FontStretch);
                double pixelsPerDip = VisualTreeHelper.GetDpi(BodyText).PixelsPerDip;
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, BodyText.FlowDirection,
                    typeface, BodyText.FontSize, Brushes.Black, pixelsPerDip);
                return ft.WidthIncludingTrailingWhitespace > avail + 0.5;
            }
            catch { return false; }
        }

        // ---- actions --------------------------------------------------------
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            _expanded = !_expanded;
            RenderCurrent();
        }

        private void LinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_display.Count == 0) return;
            var m = _display[_index];
            if (m.IsShareAction) ShareRequested?.Invoke();
            else if (m.IsLinkDiscordAction) LinkDiscordRequested?.Invoke();
            else if (m.HasLink) OpenUrl(m.LinkUrl);
        }

        private void DismissButton_Click(object sender, RoutedEventArgs e)
        {
            if (_display.Count == 0) return;
            var m = _display[_index];
            if (string.IsNullOrEmpty(m.Id)) return;
            // In preview, dismissals are SIMULATED: session-only, never persisted, and
            // reset when the preview date changes or you exit (MOTDLIVE).
            if (_simDate != null) { _simDismissed.Add(m.Id); Rebuild(); return; }
            var s = _plugin?.Settings;
            if (s == null) return;
            if (m.IsPool)
            {
                if (s.MotdPoolDismissedOn == null) s.MotdPoolDismissedOn = new Dictionary<string, string>();
                s.MotdPoolDismissedOn[m.Id] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else if (m.IsRecurring)
            {
                // Dismiss only this year's occurrence; it returns next year.
                string occ = ActiveOccurrenceToken(m, DateTime.Now.Date)
                             ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (s.MotdRecurringDismissedOcc == null) s.MotdRecurringDismissedOcc = new Dictionary<string, string>();
                s.MotdRecurringDismissedOcc[m.Id] = occ;
            }
            else
            {
                if (s.MotdDismissedIds == null) s.MotdDismissedIds = new List<string>();
                if (!s.MotdDismissedIds.Contains(m.Id)) s.MotdDismissedIds.Add(m.Id);
            }
            _plugin.SaveMotdState();
            Rebuild();
        }

        // ---- fetch ----------------------------------------------------------
        private bool CacheFresh()
        {
            var c = _plugin?.Settings?.MotdCache;
            if (c == null || string.IsNullOrEmpty(c.FetchedAtUtc)) return false;
            if (!DateTime.TryParse(c.FetchedAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var when)) return false;
            return (DateTime.UtcNow - when.ToUniversalTime()) < TrueforceSettings.MotdCacheTtl;
        }

        private void MaybeRefresh(bool force = false)
        {
            var s = _plugin?.Settings;
            if (s == null || s.CommunityEnabled != true) return;
            if (_fetchInFlight) return;
            if (!force && CacheFresh()) return;
            // Don't fetch while idle, even if the TTL has lapsed: a plugin left open in the background
            // shouldn't keep pulling MOTD. The periodic tick re-checks, so the fetch happens on the
            // next tick once the user interacts (panel load / a click both mark activity).
            if (!force && !(_plugin?.IsUserActive ?? false)) return;
            _fetchInFlight = true;
            System.Threading.Tasks.Task.Run(async () =>
            {
                List<MotdItem> result = null;
                try { result = await _plugin.FetchMotdAsync().ConfigureAwait(false); }
                catch { result = null; }
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _fetchInFlight = false;
                    // null = fetch failed: keep the offline cache. A non-null list
                    // (even empty) is authoritative and replaces the cache.
                    if (result == null) return;
                    s.MotdCache = new MotdCacheData
                    {
                        FetchedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        Items = result,
                    };
                    _plugin.SaveMotdState();
                    Rebuild();
                }));
            });
        }

        private static Brush MakeBrush(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            catch { /* best-effort; a dead launcher must not crash the strip */ }
        }
    }
}
