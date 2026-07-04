# Message of the Day (MOTD)

Status: design only, not yet built (2026-06-19). Nothing in this doc exists in code or in the database yet.

## Goal

A thin, dismissable strip at the top of the plugin UI (spanning all tabs) that surfaces short messages to users who are not in Discord or not actively watching it. It doubles as a place for community updates, light personality (tips, jokes, good vibes), scheduled announcements, and eventually time-limited sponsored messages.

## Surface and behavior

- Thin strip pinned to the top, visible across all tabs.
- Empty state collapses to nothing. No active message means no strip, so the idle footprint is zero.
- Rotation: when multiple messages are active at once, auto-advance every 8 seconds.
  - Auto-advance pauses while a message is expanded, so the user has time to read.
  - Switching views or tabs collapses any expanded message and resumes the auto-cycle.
- Expand: messages too long for one line get a "show more" / expand control. One line is preferred.
- Links: a message may carry a single link (label plus url). https-only, opened in the external browser. Rendered as plain styled text, no arbitrary HTML (same lockdown posture as the release-notes renderer).

## Dismiss

Per-message, with behavior depending on kind:

- Scheduled / important messages: permanent dismiss via a seen-id. Never shown again once dismissed.
- Generic pool messages: dismiss-for-today only, so the light recurring vibes can resurface another day instead of the pool burning down as each one is dismissed once.

## Content model

Two independent axes:

- importance: normal | important. Drives the user's tier filter.
- category: announcement | community | sponsored (extensible). Drives the future granular controls, the random pool selection, and the eventual Patreon "hide sponsored" gate.

community updates are importance=normal (only "All" sees them). important is reserved for things worth interrupting people with.

### Generic pool

- A set of evergreen messages (tips, jokes, reminders, good vibes) with no schedule window.
- Server-side, not bundled in the plugin. Consequence: when offline or with community disabled, there is no pool message either.
- When there is no scheduled message active for the day, show one message from the pool.
- The daily pick is date-seeded so it is stable for the whole day and across restarts: the server returns the whole active pool and the client picks pool[hash(today) % pool.length]. Rolls over at midnight.

### Scheduling

- starts_at / ends_at define an active window. Null start means active now, null end means no expiry.
- Sponsored or otherwise time-limited messages use this window.
- Overlapping active windows rotate (see Surface and behavior).

## User setting

A single radio: All / Important / None.

- All (default): scheduled messages plus the daily pool message (tips, jokes, community updates).
- Important: only messages flagged importance=important (warnings, critical announcements).
- None: hides everything. Selecting None shows an inline warning that the user may miss important messages.
- Honored literally. No "None still shows critical" exception, because forcing messages past an explicit off switch erodes trust in the toggle. Consequence: important messages do not reach users on None.

## Network gating

- MOTD is a networked feature, so it gates under CommunityEnabled (the existing network master switch).
- When CommunityEnabled is off, the user is offline, or the fetch fails: no MOTD is shown, the strip stays collapsed. Same effective result as None for those users. Acceptable and consistent with the rest of the plugin.

## Caching and fetch strategy

MOTD is a message of the day, so the client does not refetch on every plugin open. The dataset is tiny (a handful of pool messages plus a few scheduled ones), so a single fetch pulls the whole relevant set and the client works from cache.

- One fetch returns all currently-active, in-window rows (scheduled messages plus the full pool) in a single small payload. The RLS policy hides future/embargoed and expired rows, so the payload is exactly what is showable now.
- The client persists this set with a TTL and reuses the existing offline-first cache-with-TTL pattern from community car facts.
- The client computes the daily pick and the rotation locally from the cached set. No server hit while the cache is fresh.
- The server enforces the schedule window in the RLS policy, so embargoed/future messages never reach the client early and expired ones drop out automatically. The client just renders what it receives.
- Refetch happens lazily: when the plugin opens and the cache is past TTL, plus an hourly staleness check for sessions left running for days.

TTL is the dial between server load and how fast an important message reaches an already-running user. Default lean is ~6 hours: trivial load, important messages land within hours. If important messages need to propagate within minutes, add a conditional fetch (a cheap "nothing changed" check, full refetch only when something was posted). The conditional fetch is a later optimization, not required for v1.

### Serving model: anon-read over PostgREST (as built, migration 0067)

Clients read the motd table directly over PostgREST using the anon key, the same mechanism that already serves car_fact_consensus to every plugin on startup (migration 0001). No edge function, storage bucket, or regeneration job.

- The table grants SELECT to anon, but the RLS policy (motd_public_read_active) only ever returns rows that are active and inside their [starts_at, ends_at) window. now() in the policy is the gate.
- This keeps the schedule window server-side: embargoed/future messages (a sponsor launching next week) never appear in the API early, and expired messages drop out with no cron.
- Writes have no anon/authenticated grant, so only the service_role key (owner, via MCP) can author. There is no in-plugin admin surface.

At this scale the read load is trivial and identical in shape to the existing car-facts pulls, especially with the ~6h client cache. The static-JSON-behind-a-CDN approach was considered and deferred as premature: if read load ever matters, a pre-rendered feed file is a drop-in upgrade where the client only swaps the URL.

## Data model (server)

Table motd (migration 0067):

- id (uuid)
- kind: scheduled | pool (scheduled = shown whenever active, permanent dismiss; pool = evergreen filler, date-seeded daily pick, dismiss-for-today)
- importance: normal | important (drives the All / Important / None tier filter)
- category (migration 0069): announcement | community (plugin community: Discord events, contests) | onthisday (history, incl. driver birthdays and RIP notes) | holiday (holiday recognition) | tip | joke | reminder | sponsored
- body (text, 1..500 chars, one line preferred)
- link_url (nullable, https-only, enforced by a check constraint)
- link_label (nullable, 1..40 chars)
- starts_at (nullable = active now) -- the OVERALL validity window
- ends_at (nullable = never expires)
- recurrence (migration 0068): none | yearly
- recur_month / recur_day (the per-year calendar date for a yearly message)
- recur_window_days (default 1; how many days a yearly message stays up)
- active (bool, default true)
- created_at, updated_at

Reads: anon SELECT over PostgREST, filtered to the active window by the RLS policy (see Serving model). No public write path. Writes: owner-only via service_role (post_motd helper or direct insert).

## Authoring

- For now: Supabase-direct. Most secure, free, and authoring is infrequent.
  - Primary path: ask Claude to insert the row via the Supabase MCP tools.
  - Backup path: the post_motd helper, for example `select post_motd('text', 'https://...', 'important')` for a plain announcement, or `select post_motd('Thanks to <sponsor>', 'https://...', 'normal', 'scheduled', 'sponsored', null, now(), now() + interval '7 days')` for a one-week sponsor run. Defaults: scheduled / announcement / active now / never expires.
- Avoided: in-plugin authoring UI. It would put an owner-write path and admin UI inside the binary shipped to every user, which is the surface recent security audits were shrinking.
- Someday: a small owner-auth web dashboard if authoring volume grows, or if a scheduling calendar with live preview becomes worthwhile. Keeps admin code out of the plugin.

## Recurring messages (migration 0068)

A recurring message is one scheduled row that repeats on a calendar date every year (birthdays, RIP notes, "on this day", holidays). Editing only its `body` changes what shows next occurrence without touching the schedule.

- Evaluated client-side in local time, NOT in the RLS policy. Two reasons: "on this day" events should light up on the user's local date (the server is UTC), and the client holds the rule so the message flips on exactly at local midnight regardless of the ~6h fetch cache. (A server-gated window could be up to 6h late.)
- `starts_at` / `ends_at` stay the OVERALL validity window (when the rule is in effect at all, usually open). `recur_month` / `recur_day` are the per-year date, shown for `recur_window_days`. The RLS policy is unchanged: a recurring row with an open window is returned year-round and the client decides each day.
- `MotdStrip.ActiveOccurrenceToken` clamps Feb 29 to Feb 28 in common years and checks this year and last year so a Dec->Jan window wrap is handled.
- Dismiss branches on the recurrence tag (no separate tag): recurring dismisses for the current occurrence only and returns next year (keyed by the occurrence start-date token in `MotdRecurringDismissedOcc`); one-off scheduled still dismisses forever; pool still dismisses for today.
- Authoring: `post_motd_annual(body, month, day[, link, importance, category='onthisday', window_days, label])`. Yearly only for now; monthly/weekly are an easy later add.

## Deferred / future

- Sponsored category messages (none exist yet). The window fields already support time-limited sponsor runs.
- Patreon-gated "hide sponsored" toggle, only once sponsored messages exist.
- Granular per-type checkbox matrix, only if users ask. The single radio covers the current handful of categories.
- Per-language / regional localization: one nullable `lang` column + a client locale filter (null = everyone), precedent in the car-facts per-language consensus. Backward-compatible add when localization becomes a priority.
- "Recurring random pool" (one random pick per day within an occurrence) if rotation across multiple same-day recurring rows ever proves insufficient.

## Build steps

1. [DONE] DB migration 0067: motd table + RLS (anon-read active window only) + post_motd helper. Applied and verified (anon sees only active in-window rows; embargoed/expired hidden; anon cannot insert or call post_motd; no new security advisor lints).
2. [DONE] Read path: handled by the RLS policy in 0067 (anon SELECT over PostgREST, server-side window gate). No edge function / storage / CDN needed at current scale.
3. [DONE] Plugin: CommunityClient.FetchMotdAsync (anon-key bearer) + MotdCache in TrueforceSettings with a ~6h TTL (MotdCacheTtl), offline-first (failed fetch keeps cache; empty success replaces it), date-stable daily pool pick + scheduled rotation in MotdStrip.
4. [DONE] Plugin UI: MotdStrip.xaml/.cs hosted above the tabs (SettingsControl 3-row grid, MotdStripHost). 8s rotation, pause-on-expand, per-message dismiss, empty-collapse.
5. [DONE] Setting: All / Important / None radio with the None warning (Network expander), classified in BackupProjection (MotdLevel Portable; MotdCache / MotdDismissedIds / MotdPoolDismissedOn Excluded).
6. [DONE] Security: https-only enforced both at the DB (check constraint) and the client (fetch drops non-https links); links open in the external browser; plain text, no HTML rendering.

Status: v1 built and validated in-plugin. Recurrence (yearly) + richer categories added (migrations 0068/0069), clean Release build, DLL deployed. Pending: validate recurrence in-plugin, then seed the generic pool.
