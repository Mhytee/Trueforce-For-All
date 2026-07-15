// Polls GitHub Releases for a newer Trueforce For All version and exposes
// the result + a download helper for the in-app update modal.
//
// Lifecycle: TrueforcePlugin.Init kicks off CheckAsync on a background task
// (fire-and-forget). The settings panel timer tick reads IsUpdateAvailable
// and the metadata properties; if a newer release is up, it surfaces a
// banner. Clicking the banner opens a modal that calls DownloadInstallerAsync
// and then ShellExecutes the resulting .exe.
//
// Design notes:
//   - Version comes from the plugin assembly (csproj <Version>), so each
//     release just bumps that and the runtime stays in sync.
//   - GitHub's API requires a User-Agent header; we send "TrueforceForAll/X.Y".
//   - Network failures are silent, no banner if we can't reach GitHub.
//     LastError is exposed for diagnostics but not surfaced in the UI.
//   - TLS 1.2 is forced explicitly because net48 defaults to TLS 1.0/1.1
//     on older Windows installs and GitHub's API drops those.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TrueforceForAll.Plugin
{
    /// <summary>One published GitHub release, parsed into the fields the
    /// in-app surfaces care about. Drives both the update modal (latest
    /// non-prerelease) and the "What's new" banner (every release in
    /// (LastSeenVersion, CurrentVersion]).</summary>
    public sealed class ReleaseInfo
    {
        public Version Version      { get; set; }
        public string  TagName      { get; set; }
        public string  Title        { get; set; }
        public string  Body         { get; set; }
        public string  HtmlUrl      { get; set; }
        public string  DownloadUrl  { get; set; }
        public bool    IsPrerelease { get; set; }
    }

    public sealed class UpdateChecker
    {
        // Switched from /releases/latest to /releases?per_page=50 so the
        // What's new modal can render every release between LastSeenVersion
        // and CurrentVersion off a single API call. 50 covers years of
        // backlog; the project ships < 1 release/week.
        private const string ReleasesUrl =
            "https://api.github.com/repos/Mhytee/Trueforce-For-All/releases?per_page=50";

        public Version CurrentVersion { get; }
        public string  LatestVersionTag { get; private set; }
        public string  ReleaseNotes     { get; private set; }
        public string  DownloadUrl      { get; private set; }
        public string  ReleasePageUrl   { get; private set; }
        public string  LastError        { get; private set; }

        // Full list of published releases, newest first, parsed from the
        // /releases endpoint. Empty until CheckAsync succeeds; falls back
        // to empty (not null) on network failure so callers can `.Count`
        // without a null check. Includes prereleases; consumers filter.
        public IReadOnlyList<ReleaseInfo> AllReleases { get; private set; }
            = Array.Empty<ReleaseInfo>();

        private bool _includePrereleases;

        /// <summary>The update channel. When true (the "Beta" channel, an
        /// opt-in open to everyone), prereleases are eligible to be the
        /// "latest" upgrade target; when false (Stable, the default) they're
        /// skipped. Assigning re-runs the latest selection over the already
        /// fetched <see cref="AllReleases"/>, so a channel switch at runtime
        /// refreshes the banner/modal with no network round-trip. The plugin
        /// sets this from Settings.BetaUpdatesEnabled.</summary>
        public bool IncludePrereleases
        {
            get => _includePrereleases;
            set
            {
                if (_includePrereleases == value) return;
                _includePrereleases = value;
                SelectLatest();
            }
        }

        public Action<string> Logger { get; set; }

        public UpdateChecker()
        {
            CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version
                             ?? new Version(0, 0, 0, 0);
        }

        /// <summary>True iff the latest eligible release is a higher version than
        /// the running plugin's assembly version, OR the switch-back case: a beta
        /// build on the Stable channel offers the newest stable release even
        /// though it's older (see <see cref="IsDowngrade"/>). Returns false on
        /// any parse failure or when no check has succeeded yet.</summary>
        public bool IsUpdateAvailable
        {
            get
            {
                var latest = ParseVersion(LatestVersionTag);
                if (latest == null) return false;
                return latest > CurrentVersion || IsDowngrade;
            }
        }

        /// <summary>True when the RUNNING build matches a GitHub release flagged
        /// prerelease, i.e. this install came from the beta branch. Computed from
        /// the fetched release list; false until a check succeeds.</summary>
        public bool CurrentVersionIsPrerelease { get; private set; }

        /// <summary>True when the release currently offered as "latest" is a
        /// GitHub prerelease, i.e. accepting the update moves this install onto
        /// the beta branch. Drives the pre-beta backup in the update modal
        /// (a stable-to-beta crossing snapshots user data first).</summary>
        public bool LatestIsPrerelease { get; private set; }

        /// <summary>Set by the plugin: true when the user EXPLICITLY toggled the
        /// beta channel off (Settings.BetaUpdatesEnabled == false) after having
        /// been enrolled on this very build. Distinguishes "I want back on main"
        /// from a beta build that simply hasn't been auto-enrolled yet.</summary>
        public bool OfferStableSwitchBack { get; set; }

        /// <summary>True when the offered "update" actually moves BACK to the
        /// newest stable release: the user runs a beta (prerelease) build and has
        /// explicitly toggled the beta channel off, so the newest eligible release
        /// is older than the running version. The UI phrases this as switching
        /// back to the main release rather than an upgrade.</summary>
        public bool IsDowngrade
        {
            get
            {
                if (_includePrereleases || !OfferStableSwitchBack || !CurrentVersionIsPrerelease) return false;
                var latest = ParseVersion(LatestVersionTag);
                return latest != null && Cmp3(latest, CurrentVersion) < 0;
            }
        }

        /// <summary>Test hook (UPDATE access code): pretend a newer release is
        /// available so the "update available" banner + modal can be verified
        /// locally, without an actual newer GitHub release. Sets the latest
        /// tag to one minor version above the running build.</summary>
        public void DebugSimulateUpdateAvailable()
        {
            var c = CurrentVersion ?? new Version(0, 0, 0);
            LatestVersionTag = "v" + new Version(c.Major, c.Minor + 1, 0).ToString(3);
        }

        /// <summary>Test hook (UPDATEPOLL access code): when true, EVERY CheckAsync
        /// forces the result to "a newer release exists" (one minor above the
        /// running build) once the network call has run. Distinct from
        /// DebugSimulateUpdateAvailable, which a subsequent real CheckAsync would
        /// immediately overwrite: this re-applies the fake on each poll, so a
        /// tester can confirm that a BACKGROUND re-check discovers a release that
        /// "shipped after launch" and surfaces the banner on its own, with no
        /// restart and no real GitHub release. Sticky until DebugClearForcedUpdate.</summary>
        public bool DebugForceUpdateAvailable { get; set; }

        /// <summary>Clear the UPDATEPOLL override and drop the simulated tag so the
        /// banner hides immediately; the next real CheckAsync repopulates the true
        /// state.</summary>
        public void DebugClearForcedUpdate()
        {
            DebugForceUpdateAvailable = false;
            LatestVersionTag = null;
        }

        /// <summary>Display string for the latest tag, with the leading "v"
        /// stripped if present so the UI can render "0.1.1" not "v0.1.1".</summary>
        public string LatestVersionDisplay
        {
            get
            {
                string t = LatestVersionTag;
                if (string.IsNullOrEmpty(t)) return "";
                return (t[0] == 'v' || t[0] == 'V') ? t.Substring(1) : t;
            }
        }

        /// <summary>One-shot check. Safe to fire-and-forget. Network and parse
        /// errors are caught and stored in LastError. The optional cancellation
        /// token lets the plugin abort an in-flight request on End() so a
        /// stalled GitHub call can't outlive the plugin instance.</summary>
        public async Task CheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                EnableTls12();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    ConfigureHeaders(http);
                    using (var resp = await http.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var arr = JArray.Parse(json);

                        // Parse every release in one pass. The list endpoint
                        // returns newest-first; preserve that order so the
                        // What's new modal can iterate without re-sorting.
                        var list = new List<ReleaseInfo>(arr.Count);
                        foreach (var r in arr)
                        {
                            string tag = (string)r["tag_name"];
                            var ver = ParseVersion(tag);
                            if (ver == null) continue;
                            list.Add(new ReleaseInfo
                            {
                                Version      = ver,
                                TagName      = tag,
                                Title        = (string)r["name"],
                                Body         = (string)r["body"],
                                HtmlUrl      = (string)r["html_url"],
                                DownloadUrl  = FindExeAsset(r["assets"] as JArray),
                                IsPrerelease = (bool?)r["prerelease"] ?? false,
                            });
                        }
                        AllReleases = list;

                        // Publish the "latest" upgrade target from the fresh list,
                        // honoring the current channel (Stable skips prereleases;
                        // Beta includes them). Factored out so a channel switch at
                        // runtime re-selects off the cached list with no re-fetch.
                        SelectLatest();
                        LastError        = null;

                        Log($"Update check OK: parsed={list.Count} latest={LatestVersionTag} current={CurrentVersion} hasUpdate={IsUpdateAvailable}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Plugin teardown or explicit cancel: don't treat as error.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // UPDATEPOLL test override: re-apply the simulated newer release
                // after every poll (success OR failure) so a background re-check
                // visibly surfaces the banner without a real GitHub release.
                if (DebugForceUpdateAvailable) DebugSimulateUpdateAvailable();
            }
        }

        /// <summary>Pick the newest release eligible for the current channel and
        /// publish it into the Latest* / DownloadUrl fields the banner and update
        /// modal read. On Stable, prereleases are skipped; on Beta they count.
        /// Operates purely on the cached <see cref="AllReleases"/>, so it's cheap
        /// and safe to call on a channel switch as well as after each fetch.</summary>
        private void SelectLatest()
        {
            ReleaseInfo latest = null;
            bool currentIsPre = false;
            foreach (var r in AllReleases)
            {
                if (r == null || r.Version == null) continue;
                // Is the RUNNING build one of the prereleases? Drives beta-channel
                // auto-enroll and the stable-channel switch-back (downgrade) offer.
                if (r.IsPrerelease && Cmp3(r.Version, CurrentVersion) == 0) currentIsPre = true;
                if (r.IsPrerelease && !_includePrereleases) continue;
                if (latest == null || r.Version > latest.Version) latest = r;
            }
            CurrentVersionIsPrerelease = currentIsPre;
            LatestIsPrerelease = latest?.IsPrerelease == true;
            LatestVersionTag = latest?.TagName;
            ReleaseNotes     = latest?.Body;
            ReleasePageUrl   = latest?.HtmlUrl;
            DownloadUrl      = latest?.DownloadUrl;
        }

        /// <summary>Download the installer .exe to %TEMP% and return its path.
        /// Reports progress as (bytesReceived, totalBytes) where totalBytes is
        /// -1 if the server didn't send a Content-Length. Throws on network
        /// or filesystem error; caller is expected to surface the message.</summary>
        public async Task<string> DownloadInstallerAsync(
            Action<long, long> onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(DownloadUrl))
                throw new InvalidOperationException("No installer URL on the latest release.");
            // Refuse any DownloadUrl that doesn't match the canonical
            // Mhytee/Trueforce-For-All release-download path. Stops a
            // hostile or compromised release feed from redirecting the
            // user to an attacker-controlled installer.
            if (!ChannelValidation.IsTrustedGitHubReleaseUrl(DownloadUrl))
                throw new InvalidOperationException($"Untrusted download URL: {DownloadUrl}");

            EnableTls12();
            string fileName = $"TrueforceForAll-Setup-{LatestVersionDisplay}.exe";
            // TOCTOU defense: write into a GUID-named .tmp first so a
            // pre-existing predictable-name file at the destination can't
            // be swapped in before we open it. The final move + hidden attr
            // happens once the bytes are fully on disk.
            string destPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".tmp");

            // Per-request timeout is intentionally generous (slow connections
            // pulling a multi-MB installer); cancellationToken is the fast path
            // for End() / user-cancel.
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                ConfigureHeaders(http);
                using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1L;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(destPath))
                    {
                        try { File.SetAttributes(destPath, FileAttributes.Hidden); } catch { }
                        var buf = new byte[81920];
                        long received = 0;
                        int read;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, read, cancellationToken).ConfigureAwait(false);
                            received += read;
                            try { onProgress?.Invoke(received, total); } catch { }
                        }
                    }
                }
                // Move the verified temp file to its final name. .NET 4.8 has no
                // 3-arg File.Move overload, so delete-then-move is the portable
                // pattern. Best-effort: if delete fails, the move will surface
                // the real error.
                string finalPath = Path.Combine(Path.GetTempPath(), fileName);
                try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                File.Move(destPath, finalPath);
                destPath = finalPath;
            }
            Log($"Downloaded installer to {destPath}");
            return destPath;
        }

        // --- helpers -----------------------------------------------------

        private static void EnableTls12()
        {
            // Net48 on older Windows defaults to TLS 1.0/1.1, which GitHub
            // rejects. OR-in TLS 1.2 to whatever's already enabled.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private void ConfigureHeaders(HttpClient http)
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TrueforceForAll/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        private static string FindExeAsset(JArray assets)
        {
            if (assets == null) return null;
            foreach (var a in assets)
            {
                string name = (string)a["name"];
                if (!string.IsNullOrEmpty(name)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (string)a["browser_download_url"];
            }
            return null;
        }

        // 3-component version compare (Major/Minor/Build, missing part = 0) so
        // the 4-part assembly version (0.2.0.0) compares equal to a "v0.2.0"
        // release tag instead of greater.
        private static int Cmp3(Version a, Version b)
        {
            int c = a.Major.CompareTo(b.Major); if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);     if (c != 0) return c;
            return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
        }

        // Strip leading "v"/"V" + any pre-release suffix ("0.1.0-rc1" → "0.1.0")
        // before parsing. Returns null if the result isn't a valid Version.
        private static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            int dash = t.IndexOf('-');
            if (dash >= 0) t = t.Substring(0, dash);
            return Version.TryParse(t, out var v) ? v : null;
        }

        private void Log(string msg)
        {
            try { Logger?.Invoke(msg); } catch { }
        }
    }
}
