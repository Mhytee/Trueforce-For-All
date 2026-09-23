// Fetching TeknoParrot's public leaderboard, and not fetching it more than we need to.
//
// Their page is a 3 MB HTML document that changes slowly: it is a community high-score table, not
// a live feed. Pulling it on every game launch would be rude to somebody else's server for no
// benefit, so the parsed result is cached on disk and only refreshed when it goes stale.
//
// The cache stores the PARSED boards, not the raw HTML. Parsed is roughly a thirtieth of the size,
// and it means a cache hit costs no parsing either.
//
// Failure policy is deliberate: if the fetch fails for any reason, a stale cache is used no matter
// how old it is. A leaderboard from last week is worth vastly more than an empty board, and the
// player is offline or their server is down either way. Only a total absence of cache yields
// nothing.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace TrueforceForAll.Core
{
    /// <summary>What a cache lookup did, for the log line.</summary>
    public enum TeknoParrotCacheResult
    {
        /// <summary>Served from a cache that was still inside its TTL.</summary>
        Fresh,

        /// <summary>Fetched and stored.</summary>
        Fetched,

        /// <summary>The fetch failed and a stale cache was used instead.</summary>
        StaleFallback,

        /// <summary>No cache and the fetch failed, so there is nothing to show.</summary>
        Unavailable,
    }

    public sealed class TeknoParrotLeaderboardCache
    {
        /// <summary>How long a cached copy is considered current.
        ///
        /// A day, because their board changes about weekly: it is a community high-score table
        /// that people submit to occasionally, not a feed. Refetching every few hours would put
        /// load on somebody else's server for a page that is almost always byte-identical, and it
        /// would not make a single board more accurate. A day still means a new record there shows
        /// up here the next time you play.</summary>
        public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

        private readonly string _path;
        private readonly Func<string, string> _fetch;
        private readonly Func<DateTime> _clock;

        /// <param name="cachePath">File to keep the parsed boards in.</param>
        /// <param name="fetch">Given a URL, return the page body. Injected so the cache logic can
        /// be tested without a network, and so the caller owns the HTTP stack.</param>
        /// <param name="clock">UTC now. Injected so expiry can be tested without waiting.</param>
        public TeknoParrotLeaderboardCache(string cachePath, Func<string, string> fetch, Func<DateTime> clock = null)
        {
            _path = cachePath;
            _fetch = fetch;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        private sealed class CacheFile
        {
            public DateTime FetchedUtc;
            public Dictionary<string, LeaderboardBoard> Boards;
        }

        /// <summary>Boards for a game, from cache when it is current and from the network when it
        /// is not. Never throws: a leaderboard is a nicety, and nothing here is worth taking the
        /// plugin down for.</summary>
        public Dictionary<string, LeaderboardBoard> GetBoards(
            string teknoParrotGameId, out TeknoParrotCacheResult result, out string detail)
        {
            result = TeknoParrotCacheResult.Unavailable;
            detail = null;

            CacheFile cached = ReadCache();
            if (cached?.Boards != null && cached.Boards.Count > 0)
            {
                TimeSpan age = _clock() - cached.FetchedUtc;
                if (age >= TimeSpan.Zero && age < Ttl)
                {
                    result = TeknoParrotCacheResult.Fresh;
                    detail = "cached " + Describe(age) + " ago, " + cached.Boards.Count + " boards";
                    return cached.Boards;
                }
            }

            string url = TeknoParrotLeaderboard.PageUrl(teknoParrotGameId);
            try
            {
                string html = _fetch(url);
                var boards = TeknoParrotLeaderboard.Parse(html);
                if (boards.Count == 0)
                {
                    // A 200 that parses to nothing means their markup moved. Keep whatever we had
                    // rather than overwriting a good cache with an empty one.
                    throw new InvalidOperationException("the page fetched but no boards parsed, so the site's markup has probably changed");
                }

                WriteCache(new CacheFile { FetchedUtc = _clock(), Boards = boards });
                result = TeknoParrotCacheResult.Fetched;
                detail = boards.Count + " boards";
                return boards;
            }
            catch (Exception ex)
            {
                if (cached?.Boards != null && cached.Boards.Count > 0)
                {
                    result = TeknoParrotCacheResult.StaleFallback;
                    detail = "fetch failed (" + ex.Message + "), using a cache from " +
                             Describe(_clock() - cached.FetchedUtc) + " ago";
                    return cached.Boards;
                }

                result = TeknoParrotCacheResult.Unavailable;
                detail = "fetch failed (" + ex.Message + ") and there is no cache";
                return new Dictionary<string, LeaderboardBoard>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string Describe(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return "under a minute";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + " min";
            if (age.TotalDays < 1) return (int)age.TotalHours + " h";
            return (int)age.TotalDays + " d";
        }

        private CacheFile ReadCache()
        {
            try
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return null;
                return JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_path));
            }
            catch
            {
                // A corrupt or half-written cache is the same as no cache: refetch.
                return null;
            }
        }

        private void WriteCache(CacheFile file)
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Write beside it and move, so a crash mid-write cannot leave a truncated cache
                // that then has to be detected as corrupt on the next run.
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(file));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch
            {
                // Failing to cache is not failing: the boards were still fetched.
            }
        }
    }
}
