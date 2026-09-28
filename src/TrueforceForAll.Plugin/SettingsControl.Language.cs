// The language picker on the Settings tab, and the door to the Translate window.
//
// A new partial rather than more weight in SettingsControl.xaml.cs, which is
// already 17,700 lines (docs/agent-audit.md).
//
// Three things happen here. The combo is built from the languages that actually
// exist on this install, each shown under the name it calls itself, with Automatic
// first and naming what it currently resolves to, because that is what every
// install does until someone picks. Choosing one loads it immediately: the store
// raises its change event, the bound labels re-render and the relabel passes
// re-run, so nothing restarts. And the Translate button opens the editor on the
// language being shown, offering to start a new one when that language is
// English.
//
// The picker's own entries are never translated. A reader looking for their
// language wants to see "Deutsch", not "German" in a language they cannot read.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    public partial class SettingsControl
    {
        // The tag each row of the combo stands for. "" is the follow-the-host row
        // and null is the "start a new language" row, so the handler can tell
        // "picked English explicitly" from "let SimHub decide".
        private const string NewLanguageMarker = "\u0000new";

        private void RebuildLanguageSection()
        {
            var store = Loc.Instance;
            if (UiLanguageCombo == null || store == null) return;
            bool prior = _suppressEvents;
            _suppressEvents = true;
            try
            {
                UiLanguageCombo.Items.Clear();
                string chosen = _plugin?.Settings?.UiLanguage ?? "";

                // What "follow SimHub" currently resolves to, named so the row is
                // an answer rather than a question.
                string followingName = store.DisplayName(store.ActiveTag) ?? store.ActiveTag;
                UiLanguageCombo.Items.Add(new ComboBoxItem
                {
                    Tag = "",
                    Content = Loc.F("Settings_LanguageFollowHost_Fmt", followingName),
                });

                foreach (string tag in LocDiagnostics.KnownTags(store))
                {
                    string name = store.DisplayName(tag) ?? tag;
                    // The tag rides along so two languages that call themselves the
                    // same thing are still tellable apart, and the percentage says
                    // how much of the panel this one actually covers: switching to a
                    // language that is a tenth finished should not be a surprise.
                    UiLanguageCombo.Items.Add(new ComboBoxItem
                    {
                        Tag = tag,
                        Content = LanguageRowText(store, tag, name),
                    });
                }
                UiLanguageCombo.Items.Add(new ComboBoxItem
                {
                    Tag = NewLanguageMarker,
                    Content = Loc.T("Settings_LanguageStartNew"),
                });

                int pick = 0;
                for (int i = 0; i < UiLanguageCombo.Items.Count; i++)
                {
                    var item = UiLanguageCombo.Items[i] as ComboBoxItem;
                    if (item != null && string.Equals(item.Tag as string, chosen, StringComparison.OrdinalIgnoreCase))
                    {
                        pick = i;
                        break;
                    }
                }
                UiLanguageCombo.SelectedIndex = pick;
                UpdateLanguageNote();
            }
            finally { _suppressEvents = prior; }
        }

        /// <summary>What the panel is showing and how complete it is. A reader who
        /// sees a mix of their language and English deserves to know why.</summary>
        /// <summary>The community-translation switch: its state, and whether it is
        /// shown at all. An English panel hides it, because with "en" active the fetch
        /// makes no request, writes no file and adds no layer, so the row would offer
        /// a choice that changes nothing.</summary>
        private void SyncCommunityTranslationsRow()
        {
            var store = Loc.Instance;
            if (UseCommunityTranslationsCheck == null) return;
            bool english = store == null || store.ActiveTag == LocStore.EnglishTag;
            UseCommunityTranslationsCheck.Visibility = english ? Visibility.Collapsed : Visibility.Visible;
            var s = _plugin?.Settings;
            if (s == null) return;
            _suppressEvents = true;
            try
            {
                UseCommunityTranslationsCheck.IsChecked = s.UseCommunityTranslations;
                // Community features off means no network read at all, so the row is
                // there but cannot be acted on.
                UseCommunityTranslationsCheck.IsEnabled = s.CommunityEnabled;
            }
            finally { _suppressEvents = false; }
        }

        private void UseCommunityTranslations_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = _plugin?.Settings;
            var store = Loc.Instance;
            if (s == null) return;
            s.UseCommunityTranslations = UseCommunityTranslationsCheck.IsChecked == true;
            try { _plugin.PersistSettings(); } catch { }
            if (store != null)
            {
                // The layer is read or skipped from now on. Nothing is deleted either
                // way, so turning it back on costs no fetch.
                store.UseCommunityLayer = s.UseCommunityTranslations;
                store.Reload();
            }
            if (s.UseCommunityTranslations) _plugin.Translations?.RequestNow();
            UpdateLanguageNote();
        }

        private void UpdateLanguageNote()
        {
            // Here rather than at each call site: the section rebuild, a language
            // change and the checkbox's own handler all pass through this.
            SyncCommunityTranslationsRow();
            var store = Loc.Instance;
            if (UiLanguageNote == null || store == null) return;
            if (store.ActiveTag == LocStore.EnglishTag)
            {
                UiLanguageNote.Text = Loc.T("Settings_LanguageEnglishActive");
                return;
            }
            var summary = store.Describe(store.ActiveTag);
            int total = summary.DefinedCount + summary.Missing.Count;
            UiLanguageNote.Text = Loc.F("Settings_LanguageActive_Fmt",
                store.DisplayName(store.ActiveTag) ?? store.ActiveTag,
                summary.DefinedCount, total);
        }

        // Re-run on LanguageChanged: the two rows that are localized are the
        // follow-the-host row and the note. Everything else in the combo is a
        // language's own name and stays as it is.
        private void RelabelLanguageSection() => RebuildLanguageSection();

        private void UiLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin?.Settings == null) return;
            var item = UiLanguageCombo?.SelectedItem as ComboBoxItem;
            if (item == null) return;
            string tag = item.Tag as string ?? "";
            if (tag == NewLanguageMarker)
            {
                StartNewLanguage();
                return;
            }
            ApplyLanguage(tag);
        }

        /// <summary>Persist the pick and load it. Persisting first means a failure
        /// to load still leaves the choice made, which is the same order every
        /// other setting on this tab uses.</summary>
        private void ApplyLanguage(string tag)
        {
            _plugin.Settings.UiLanguage = tag ?? "";
            try { _plugin.PersistSettings(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[TF4ALL] Persist UiLanguage failed: " + ex.Message);
            }
            try
            {
                var store = Loc.Instance;
                if (store != null) store.Load(_plugin.ResolveUiLanguageTag());
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TF4ALL] Loading language '" + (tag ?? "") + "' failed: " + ex.Message);
            }
            UpdateLanguageNote();
        }

        /// <summary>Pick a language to translate out of every language Windows
        /// knows, then open the Translate window on it. Asking for a culture tag
        /// only helps someone who already knows theirs, and it left a fresh install
        /// looking as though no language were open. A new language is an empty file
        /// until the first row is saved, so nothing is written here.</summary>
        private void StartNewLanguage()
        {
            var store = Loc.Instance;
            var dlg = new LanguageChooserWindow(store, UsageLanguage.UiLang());
            dlg.Owner = Window.GetWindow(this);
            bool ok = dlg.ShowDialog() == true;
            RebuildLanguageSection();                 // put the selection back
            if (!ok || string.IsNullOrEmpty(dlg.ChosenTag)) return;
            OpenTranslateWindow(dlg.ChosenTag, dlg.ChosenName);
        }

        /// <summary>A picker row: the language's own name, its tag, and how much of
        /// the panel it covers on this PC. English is the source, so it carries no
        /// percentage of itself.</summary>
        private static string LanguageRowText(LocStore store, string tag, string name)
        {
            if (string.Equals(tag, LocStore.EnglishTag, StringComparison.OrdinalIgnoreCase))
                return Loc.F("Settings_LanguageRow_Fmt", name, tag);
            try
            {
                var s = store.Describe(tag);
                int all = s.DefinedCount + s.Missing.Count;
                if (all > 0)
                    return Loc.F("Settings_LanguageRowProgress_Fmt", name, tag,
                        (int)Math.Round(100.0 * s.DefinedCount / all));
            }
            catch { }
            return Loc.F("Settings_LanguageRow_Fmt", name, tag);
        }

        // The same shape LocStore accepts, so a tag that passes here cannot be
        // refused later: letters, digits and single hyphens between them. Internal
        // because the chooser window tests a typed tag with it too.
        internal static bool IsCultureTagShape(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 32) return false;
            if (tag[0] == '-' || tag[tag.Length - 1] == '-') return false;
            char prev = '\0';
            foreach (char c in tag)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-';
                if (!ok || (c == '-' && prev == '-')) return false;
                prev = c;
            }
            return true;
        }

        private void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            var store = Loc.Instance;
            if (store == null) return;
            // Translating English into English is not a thing, so the button asks
            // which language instead of opening an editor that can do nothing.
            if (store.ActiveTag == LocStore.EnglishTag) StartNewLanguage();
            else OpenTranslateWindow(store.ActiveTag, store.DisplayName(store.ActiveTag));
        }

        private void OpenTranslateWindow(string tag, string languageName)
        {
            var store = Loc.Instance;
            if (store == null) return;
            try
            {
                var win = new TranslateWindow(store, tag, languageName,
                    msg => SimHub.Logging.Current.Warn(msg), _plugin?.Translations);
                win.Owner = Window.GetWindow(this);
                win.ShowDialog();
                // The window writes the root override and reloads as it goes, so
                // by the time it closes the picker may have a language it did not
                // have before.
                RebuildLanguageSection();
            }
            catch (Exception ex)
            {
                TrueforceDialog.ShowError(Window.GetWindow(this), Loc.T("Settings_LanguageTranslateFailed"), ex);
            }
        }

        private void LanguageFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string root = Loc.Instance?.LanguagesRoot;
            if (string.IsNullOrEmpty(root)) return;
            try
            {
                System.IO.Directory.CreateDirectory(root);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("[TF4ALL] Opening the languages folder failed: " + ex.Message);
            }
        }
    }
}
