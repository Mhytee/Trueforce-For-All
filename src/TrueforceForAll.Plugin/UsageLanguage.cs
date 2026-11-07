// Two stateless facts about the machine's language, computed at the single
// usage-ping call site (TrueforcePlugin, below the ShareUsageStats gate) and
// sent with the once-a-day ping through TelemetryClient.SendPing. Nothing here
// is persisted: no setting, no backup line, nothing in the settings blob. They
// answer one question: which translations of the plugin UI would reach people
// who run it in a language other than English today.
//
//   UiLang  : the language Windows itself is displayed in, the first entry of
//             the user's preferred UI languages. The language this person
//             reads their computer in.
//   FmtLang : the language subtag of the Windows regional-format locale, the
//             country picked at setup that decides date and number formats.
//             It catches the German or Brazilian who runs Windows in English,
//             whom UiLang alone counts as English.
//
// Why Win32 and not CultureInfo.CurrentUICulture: SimHub's startup pins
// CurrentCulture, CurrentUICulture and both CultureInfo.DefaultThread*
// properties to "en-US" before any plugin loads, so the managed properties
// report English for every user on Earth. That pin is also why the old
// per-language car-name bucket (CommunityNameLocaleSig) only ever emitted
// "lang=en". This code does not depend on the pin being there: the Win32
// calls read the user's Windows settings directly and are correct either way.
// Do not "simplify" this back to CurrentUICulture, CurrentCulture or
// RegionInfo.CurrentRegion; all three are pinned.
//
// Why two letters and no region: the usage report promises it cannot be tied
// to you. A bare "de" narrows nothing; "de-AT" plus wheel plus games plus 161
// settings keys starts to. Every value leaving this class is exactly two
// lowercase ASCII letters or null, and the server re-checks that.
//
// Why null and never "en" on failure: a wrong default is silent data
// corruption, and it biases the exact answer this ping exists to find (how
// many non-English users there are). Null is an honest "unknown" and the
// server stores it as such.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TrueforceForAll.Plugin
{
    internal static class UsageLanguage
    {
        // GetUserPreferredUILanguages flag: return locale names ("de-DE"), not LCIDs.
        private const uint MUI_LANGUAGE_NAME = 0x8;

        // LOCALE_NAME_MAX_LENGTH from winnls.h, including the terminating null.
        private const int LOCALE_NAME_MAX_LENGTH = 85;

        // This API has no A/W-suffixed variants, hence ExactSpelling. PULONG is
        // 32-bit on x86 Windows, which is where SimHub runs, so uint here, never
        // ulong or UIntPtr. Called twice: a null buffer with pcch = 0 reports the
        // size needed, then a buffer of that size receives a double-null
        // terminated list ("de-DE\0en-US\0\0") whose first entry is the
        // preferred language.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool GetUserPreferredUILanguages(
            uint dwFlags, out uint pulNumLanguages, char[] pwszLanguagesBuffer, ref uint pcchLanguagesBuffer);

        // Returns the length written including the terminating null, 0 on failure.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetUserDefaultLocaleName(StringBuilder lpLocaleName, int cchLocaleName);

        /// <summary>The language Windows is displayed in, as a lowercase
        /// two-letter ISO 639-1 code, or null when it cannot be learned.</summary>
        internal static string UiLang()
        {
            string lang = null;
            try { lang = TwoLetter(FirstPreferredUiLanguage()); }
            catch { lang = null; }
            if (lang != null) return lang;

            // Fallback: the language Windows was installed in. It is read from
            // the OS rather than the thread, so SimHub's culture pin does not
            // touch it. Less accurate than the preferred-UI list when the user
            // changed display language after install, but honest.
            try { lang = TwoLetter(CultureInfo.InstalledUICulture?.Name); }
            catch { lang = null; }
            return lang;
        }

        /// <summary>The language subtag of the Windows regional-format locale,
        /// as a lowercase two-letter ISO 639-1 code, or null. No managed
        /// fallback on purpose: RegionInfo.CurrentRegion and CurrentCulture are
        /// pinned to en-US inside SimHub and would answer "en" for everyone.</summary>
        internal static string FmtLang()
        {
            try
            {
                var sb = new StringBuilder(LOCALE_NAME_MAX_LENGTH);
                int len = GetUserDefaultLocaleName(sb, LOCALE_NAME_MAX_LENGTH);
                if (len <= 0) return null;
                return TwoLetter(sb.ToString());
            }
            catch
            {
                return null;
            }
        }

        // The first (preferred) tag of the user's UI-language list, or null.
        private static string FirstPreferredUiLanguage()
        {
            uint count;
            uint cch = 0;
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out count, null, ref cch) || cch == 0)
                return null;

            var buffer = new char[cch];
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out count, buffer, ref cch) || count == 0)
                return null;

            int end = Array.IndexOf(buffer, '\0');
            if (end < 0) end = buffer.Length;
            return end > 0 ? new string(buffer, 0, end) : null;
        }

        // "de-DE" becomes "de"; "en" stays "en". Anything that is not exactly
        // two ASCII letters (three-letter codes such as "haw" included) becomes
        // null, so the privacy text's "two-letter" is true everywhere.
        private static string TwoLetter(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            int dash = tag.IndexOf('-');
            string head = (dash >= 0 ? tag.Substring(0, dash) : tag).Trim().ToLowerInvariant();
            if (head.Length != 2 || head == "iv") return null;   // "iv" is .NET's invariant marker, not a language
            for (int i = 0; i < head.Length; i++)
            {
                char c = head[i];
                if (c < 'a' || c > 'z') return null;
            }
            return head;
        }
    }
}
