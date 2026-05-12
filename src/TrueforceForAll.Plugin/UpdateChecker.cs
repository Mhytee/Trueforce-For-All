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
//   - Network failures are silent — no banner if we can't reach GitHub.
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

        public Action<string> Logger { get; set; }

        public UpdateChecker()
        {
            CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version
                             ?? new Version(0, 0, 0, 0);
        }

        /// <summary>True iff the latest release tag parses to a higher version
        /// than the running plugin's assembly version. Returns false on any
        /// parse failure or when no check has succeeded yet.</summary>
        public bool IsUpdateAvailable
        {
            get
            {
                var latest = ParseVersion(LatestVersionTag);
                return latest != null && latest > CurrentVersion;
            }
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

                        // "Latest" for the update-available check = newest
                        // non-prerelease. Prereleases shouldn't surface as
                        // upgrade targets for regular users.
                        ReleaseInfo latest = null;
                        foreach (var r in list)
                        {
                            if (r.IsPrerelease) continue;
                            if (latest == null || r.Version > latest.Version) latest = r;
                        }
                        LatestVersionTag = latest?.TagName;
                        ReleaseNotes     = latest?.Body;
                        ReleasePageUrl   = latest?.HtmlUrl;
                        DownloadUrl      = latest?.DownloadUrl;
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

            EnableTls12();
            string fileName = $"TrueforceForAll-Setup-{LatestVersionDisplay}.exe";
            string destPath = Path.Combine(Path.GetTempPath(), fileName);

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
