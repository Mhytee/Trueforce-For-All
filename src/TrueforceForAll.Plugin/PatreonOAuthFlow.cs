// Phase 2: the browser + loopback half of the Patreon account link. Opens Patreon's consent
// page in the user's default browser, then catches the OAuth redirect on a short-lived local
// HttpListener and returns the authorization code. The code is exchanged server-side by the
// patreon-link Edge Function (PatreonLinkClient), so the Patreon client secret never lives in
// the plugin. Mirrors DiscordOAuthFlow: CSRF covered by a one-time `state`, listener binds
// loopback only and accepts exactly one matching request before shutting down.

using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrueforceForAll.Plugin.Localization;

namespace TrueforceForAll.Plugin
{
    /// <summary>User-presentable failure of the Patreon OAuth dance (deny / timeout /
    /// could-not-open-port). Carries a message safe to show in the settings status line.</summary>
    internal sealed class PatreonOAuthException : Exception
    {
        public PatreonOAuthException(string message) : base(message) { }
    }

    internal static class PatreonOAuthFlow
    {
        public sealed class Result
        {
            public string Code;
            public string RedirectUri;
        }

        private static readonly TimeSpan WaitForConsent = TimeSpan.FromMinutes(3);

        /// <summary>Open the Patreon authorize page and capture the loopback redirect. Throws
        /// PatreonOAuthException for user-facing failures, OperationCanceledException if
        /// <paramref name="ct"/> is cancelled.</summary>
        public static async Task<Result> AuthorizeAsync(
            string clientId, string[] redirectUris, string scope,
            Action<string> log, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientId)) throw new PatreonOAuthException(Loc.T("Account_PatreonLinkingIsnT"));
            if (redirectUris == null || redirectUris.Length == 0) throw new PatreonOAuthException(Loc.T("Account_PatreonLinkingIsnT"));

            HttpListener listener = null;
            string redirectUri = null;
            foreach (var candidate in redirectUris)
            {
                string prefix = candidate.EndsWith("/") ? candidate : candidate + "/";
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.Start();
                    listener = l;
                    redirectUri = prefix;
                    break;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[TF4ALL] Patreon: couldn't bind {prefix}: {ex.Message}");
                }
            }
            if (listener == null)
                throw new PatreonOAuthException(Loc.T("Account_CouldnTOpenLocal2"));

            try
            {
                string state = Guid.NewGuid().ToString("N");
                string authorizeUrl =
                    "https://www.patreon.com/oauth2/authorize"
                    + "?response_type=code"
                    + "&client_id=" + Uri.EscapeDataString(clientId)
                    + "&scope=" + Uri.EscapeDataString(string.IsNullOrEmpty(scope) ? "identity" : scope)
                    + "&state=" + state
                    + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);

                // Log the URL so support has a manual fallback if the browser didn't actually open.
                log?.Invoke("[TF4ALL] Patreon authorize URL: " + authorizeUrl);
                try { Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true }); }
                catch (Exception ex) { throw new PatreonOAuthException(Loc.F("Account_CouldnTOpenBrowser_Fmt", ex.Message)); }

                HttpListenerContext context;
                using (var timeoutCts = new CancellationTokenSource(WaitForConsent))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    // Keep listening until OUR callback (carrying the one-time state) arrives. Any
                    // other local probe hitting the fixed loopback port gets a 404 and is ignored,
                    // so it can't consume the single slot and strand Patreon's real redirect.
                    while (true)
                    {
                        var ctxTask = listener.GetContextAsync();
                        var finished = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                        if (finished != ctxTask)
                        {
                            ObserveAbandoned(ctxTask);
                            if (timeoutCts.IsCancellationRequested)
                                throw new PatreonOAuthException(Loc.T("Account_TimedOutWaitingPatreon"));
                            throw new OperationCanceledException(ct);
                        }
                        var candidate = await ctxTask.ConfigureAwait(false);
                        if (candidate.Request.QueryString["state"] == state) { context = candidate; break; }
                        try { candidate.Response.StatusCode = 404; candidate.Response.Close(); } catch { }
                    }
                }

                var req = context.Request;
                string code = req.QueryString["code"];
                string error = req.QueryString["error"];
                bool ok = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code);
                WriteBrowserResponse(context.Response, ok);

                if (!string.IsNullOrEmpty(error))
                    throw new PatreonOAuthException(Loc.T("Account_PatreonLinkingWasCancelled"));
                if (string.IsNullOrEmpty(code))
                    throw new PatreonOAuthException(Loc.T("Account_NoAuthorizationCodePatreon"));

                return new Result { Code = code, RedirectUri = redirectUri };
            }
            finally
            {
                try { listener.Stop(); listener.Close(); } catch { }
            }
        }

        private static void ObserveAbandoned(Task<HttpListenerContext> t)
        {
            t.ContinueWith(x => { var _ = x.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private static void WriteBrowserResponse(HttpListenerResponse resp, bool ok)
        {
            try
            {
                string html = ok
                    ? "<!doctype html><html><head><meta charset=\"utf-8\"><title>Trueforce</title></head>"
                      + "<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#1b1b1f;color:#eee;text-align:center;padding-top:60px\">"
                      + "<h2 style=\"color:#e9b949\">Patreon linked</h2>"
                      + "<p>You can close this tab and return to SimHub.</p></body></html>"
                    : "<!doctype html><html><head><meta charset=\"utf-8\"><title>Trueforce</title></head>"
                      + "<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#1b1b1f;color:#eee;text-align:center;padding-top:60px\">"
                      + "<h2 style=\"color:#d66\">Linking didn't complete</h2>"
                      + "<p>You can close this tab and try again in SimHub.</p></body></html>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                resp.StatusCode = 200;
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                resp.OutputStream.Write(buf, 0, buf.Length);
                resp.OutputStream.Close();
            }
            catch { /* the browser may have already navigated away */ }
        }
    }
}
