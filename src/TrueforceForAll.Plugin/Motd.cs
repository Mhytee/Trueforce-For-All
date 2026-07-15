// Message of the Day (MOTD) client-side types.
//
// The plugin fetches the active feed from the backend `motd` table (anon-read
// over PostgREST; the RLS policy only returns rows that are active and inside
// their schedule window, so the client never filters by date). The feed is
// cached offline-first with a ~6h TTL and rendered in a dismissable strip at
// the top of the plugin UI. Design doc: docs/motd-design.md.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TrueforceForAll.Plugin
{
    /// <summary>How much of the MOTD feed the user wants on the top strip. A
    /// single radio in Settings. None still warns that important messages may
    /// be missed.</summary>
    public enum MotdLevel
    {
        /// <summary>Scheduled messages plus the daily pool message (tips, jokes, updates).</summary>
        All,
        /// <summary>Only messages flagged important (warnings, critical notices).</summary>
        Important,
        /// <summary>Hide everything.</summary>
        None,
    }

    /// <summary>One row from the backend `motd` feed.</summary>
    public sealed class MotdItem
    {
        public string Id         { get; set; }
        public string Kind       { get; set; }  // "scheduled" | "pool"
        public string Importance { get; set; }  // "normal" | "important"
        public string Category   { get; set; }  // announcement | community | tip | joke | reminder | sponsored
        public string Body       { get; set; }
        public string LinkUrl    { get; set; }  // null, or an https:// URL
        public string LinkLabel  { get; set; }

        // Recurrence. "none" for one-off scheduled messages; "yearly" repeats on
        // (RecurMonth, RecurDay) every year for RecurWindowDays. Evaluated
        // client-side in LOCAL time (see MotdStrip).
        public string Recurrence      { get; set; } = "none";
        public int?   RecurMonth      { get; set; }
        public int?   RecurDay        { get; set; }
        public int    RecurWindowDays { get; set; } = 1;
        // Optional anniversary anchor: when set and the body contains the
        // {years} token, the client substitutes (occurrence year - AnchorYear).
        public int?   AnchorYear      { get; set; }
        // Optional in-plugin action the message's button triggers instead of
        // opening a URL. null, "share" (opens the Spread-the-word modal), or
        // "link_discord" (starts the Discord link flow, which also joins the
        // server and unlocks roles; clients that predate the action fall
        // through to LinkUrl, so rows keep the plain invite as a fallback).
        public string Action          { get; set; }

        // Optional per-user audience filter, evaluated CLIENT-side against local
        // signals (supporter status, Discord link, contribution recency). null =
        // everyone. See MotdStrip.AudienceAllows and docs/motd-design.md. Values:
        //   supporter | non_supporter | non_discord | non_sharer | non_voter | non_factsubmitter
        public string Audience        { get; set; }

        [JsonIgnore] public bool IsPool      => string.Equals(Kind, "pool", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsImportant => string.Equals(Importance, "important", StringComparison.OrdinalIgnoreCase);
        // A quote row carries its text with the author in a trailing parenthetical;
        // the strip renders it with quotation marks + a dimmer attribution so it
        // reads as a quote at a glance (see MotdStrip.SetBodyContent).
        [JsonIgnore] public bool IsQuote     => string.Equals(Category, "quote", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool HasLink     => !string.IsNullOrWhiteSpace(LinkUrl);
        [JsonIgnore] public bool IsShareAction => string.Equals(Action, "share", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsLinkDiscordAction => string.Equals(Action, "link_discord", StringComparison.OrdinalIgnoreCase);
        // A "nag" is a promotional / call-to-action message (support, share, or any
        // audience-targeted nudge). Weighted down + cooldown-gated in the strip so
        // they stay useful but never constant.
        [JsonIgnore] public bool IsNag =>
            (!string.IsNullOrEmpty(Audience) && Audience.StartsWith("non_", StringComparison.OrdinalIgnoreCase))
            || string.Equals(Category, "support", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Category, "share", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsRecurring =>
            string.Equals(Recurrence, "yearly", StringComparison.OrdinalIgnoreCase)
            && RecurMonth.HasValue && RecurDay.HasValue;
    }

    /// <summary>Offline-first cache of the MOTD feed. <see cref="FetchedAtUtc"/>
    /// is an ISO-8601 ("o") UTC timestamp driving the ~6h TTL. Persisted in
    /// TrueforceSettings; re-fetchable, so Excluded from backup. A failed fetch
    /// never overwrites this (the cached feed keeps showing offline); an empty
    /// successful fetch DOES replace it (the server has nothing active now).</summary>
    public sealed class MotdCacheData
    {
        public string FetchedAtUtc { get; set; }
        public List<MotdItem> Items { get; set; } = new List<MotdItem>();
    }
}
