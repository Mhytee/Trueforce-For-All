# Trueforce For All community backend

Hosts the whole community backend (CarFacts, preset sharing, moderation,
auth, cloud backup) on a single Supabase project. The schema treats the
anon API key as **public** and gates every write through `SECURITY
DEFINER` RPC functions that validate input, derive identity server-side,
and rate-limit. Direct table writes from the anon key are revoked.

## What's in here

`migrations/` (135 files, applied in filename order) grouped by subsystem:

- **CarFacts** (`0001`-`0004` + later refinements): community car data;
  submissions/votes/consensus with Wilson scoring, payload normalization,
  account or anonymous submitter identity, rate limiting, suppression.
- **Preset sharing** (`0005`+): presets, game presets, custom engines,
  packs; owner auth, votes, downloads, edit rate limits, target games.
- **Accounts / auth** (`0007`, `0009`, `0023`+): profiles + usernames,
  auth-gated RPCs, per-user rate limits, locked internal helpers.
- **Moderation / reports** (`0021`, `0080`-`0090`): report flags, Discord
  notify pipeline, action/appeal RPCs.
- **Backup / sync + entitlements** (interleaved): cloud settings backup,
  retention, Patreon/Discord linking, supporters, achievements.

`functions/`: Edge Functions (report/appeal notify and moderation actions,
Discord + Patreon linking and role sync, supporters sync, backup retention
warn/GC, the weekly arcade digest and the daily TeknoParrot board sync).
`dev-seed/`: mock community data for local testing + teardown.

Run `ls migrations/` for the current set; migration filenames are
descriptive. The numbering is not contiguous: `0047` and `0090` are each used
by two files and `0122` is unused, so the highest prefix is not the file
count. Always make new migrations idempotent (`if not exists`,
`create or replace`, `drop ... if exists`) so re-runs are safe.

`recovered/`: 28 migrations that are applied to production but existed in no
branch as files, recovered verbatim from the ledger on 2026-09-24. They are a
record of what ran, not a replay set. See its README before touching them.

## Applying a change

Commit the numbered file first, then apply it directly:

```
supabase db query --linked -f supabase/migrations/NNNN_name.sql
```

Then verify the objects it claimed to create really exist, by querying
`pg_class` or `pg_proc` for them and checking their ACLs. A migration that
silently did nothing looks exactly like one that worked.

**Never run `supabase migration repair`, `db push`, `db pull` or `db reset`
against this project.** Local files are numbered and the remote ledger is
keyed by timestamp, so the two lists share nothing and the CLI reports all 135
local files as pending and all 157 remote rows as missing. That is expected,
not drift.

`migration repair` is the dangerous one. The CLI offers it pre-filled with
every version whenever it notices the mismatch, and it runs
`DELETE FROM supabase_migrations.schema_migrations WHERE version = ANY($1)`.
The `rollback` column is null on all 157 rows, so there is no undo, and that
ledger was until recently the only copy of nine migrations' SQL. A full
archive now lives outside the repo at `supabase-history-2026-09-24`.

## Security model

**Threat assumption**: the anon API key ships in the plugin DLL. Anyone
with the plugin has it. Treat it as a public token.

**What the anon key can do**:

1. `select` from the three tables still open to it: `car_fact_consensus`
   (trusted facts, granted in `0100`), `motd` (granted in `0067`, and its
   policy shows only active rows) and `arcade_cars` (granted in `0117`).
   `0023` revoked the anon `profiles` read and `0027` the preset and vote
   reads, and nothing since has restored them.
2. `execute` `submit_car_fact(...)`.
3. `execute` `list_supporters()` and `telemetry_ping(...)`.

`vote_car_fact` is not on the anon surface at all. `0002` dropped the signature
`0001` had granted, and `0026` revoked anon and PUBLIC EXECUTE from every
SECURITY DEFINER function whose body raises `sign-in required`, which this one
does. Voting needs a signed-in user, and anon is refused at the permission layer
before the function body runs.

**What the anon key can NOT do**:

- Read raw car-fact submissions or votes, the salt config, or the block list.
- Write to any table directly. The only anon-reachable write paths are the
  validated `submit_car_fact` and `telemetry_ping` RPCs.
- Call the internal helpers (`_recompute_car_fact_consensus`,
  `_derive_submitter_id`, `_client_ip`, `normalize_car_fact_payload`).

**Identity**: a signed-in caller's id is derived inside the RPC from their
token (`auth.uid()`), never from the request body. An anonymous car-fact
submitter supplies its own: the plugin mints a random GUID, sends it as
`p_anon_id`, and the RPC shape-checks it before storing it as `anon:<id>`.
A client can therefore rotate its anon id at will, which is exactly why the
IP backstop below exists. `vote_car_fact` has no anonymous path at all. The client IP survives only as an unsalted
`source_ip_hash`, which backs the per-IP volume cap on anonymous submissions
and lets a ban row key on `ip:<sha256>` when an anon id gets rotated. The
`submitter_id_salt` row is still in `app_config`, but no current RPC reads
it: `_derive_submitter_id` is the only function that ever did, and every RPC
that called it has since been redefined without it.

**Sybil resistance**:
- Consensus counts one endorsement per submitter, their most recent
  submission only, so a single submitter sending the same value 100 times
  contributes 1, not 100, and one who changes their mind moves their support
  instead of backing both payloads.
- Wilson score is driven ONLY by `vote_car_fact` votes, not by submission
  count. 100 distinct submitter_ids alone do NOT produce a high Wilson:
  the v1 design that seeded Wilson from submission counts let cheap
  residential-proxy networks capture consensus on low-engagement cars.
- Votes are bound to the `payload_hash` they were cast on. When the
  candidate payload changes (e.g., an attacker pushes a new payload past
  the incumbent), votes cast on the prior payload become inert: they
  neither help nor hurt the new payload's Wilson score. Without this an
  attacker could silently inherit honest Wilson signals by flipping the
  candidate.
- A new candidate payload must beat the incumbent by a `sticky_margin`
  scaled to incumbent support + Wilson confidence:
  - Wilson-confirmed (`wilson_score >= 0.5`): margin = `max(3, ceil(0.5
    x supporters))` — well-established consensus is genuinely hard to
    displace.
  - Unconfirmed incumbent with 1-2 supporters: margin = 1 — effectively
    bootstrap, easy to displace by a single legit submitter with the
    correct payload.
  - Unconfirmed with 3-9 supporters: margin = 2.
  - Unconfirmed with 10+: margin = `ceil(0.3 x supporters)`.
  Slows drive-by candidate swaps without making honest community shifts
  impossible.
- `vote_car_fact` accepts an optional `p_expected_payload_hash` parameter.
  The plugin's future vote UI will pass the hash of the payload it
  rendered for the user; the RPC raises `consensus changed, refetch` if
  the consensus payload has flipped between UI render and RPC receive,
  so a stale-cache Confirm click can't silently endorse the attacker's
  freshly-flipped candidate.
- Submission count is exposed separately as `supporting_submissions` so
  the plugin's future pull query can surface high-support entries as
  "pending review" without trusting them automatically.

**Validation**: `normalize_car_fact_payload(fact_type, payload)` enforces
shape per fact_type and canonicalizes (upper-cases config strings, trims
names) to prevent split-vote via structurally-different-but-semantically-
equal payloads. Out-of-spec payloads are rejected outright.

**Rate limiting**: 30 distinct car-fact events and 30 votes per rolling hour,
keyed on the caller's `submitter_id` / `voter_id`. Anonymous submissions carry
a second cap on the same event count keyed on `source_ip_hash`, so minting
fresh anon ids buys no extra budget. Signed-in callers are exempt from that
backstop, so one anon flooder on a shared address cannot starve them.

**Moderation**: suppress a bad consensus row by setting `is_suppressed =
true`; the recompute respects the flag and won't unsuppress it on the next
submission. The block list is consulted by both RPCs. All moderation
operations require the service_role key (not shipped in the plugin) via
Supabase Studio or psql.

## First-time setup

1. **Create the Supabase project.**
   - sign up at supabase.com, click New Project, pick a region near most
     users, set a strong database password.
   - In Project Settings → API, copy:
     - The Project URL (e.g. `https://abcd1234.supabase.co`)
     - The `anon` (public) API key

2. **Apply the migration.**
   - In the Supabase dashboard → SQL Editor, paste the contents of
     `migrations/0001_carfacts_init.sql` and Run.
   - Verify the tables exist: `car_fact_submissions` (private),
     `car_fact_consensus` (readable), `car_fact_consensus_votes`
     (private), `app_config`, `submitter_blocked`.
   - Verify the two RPCs exist under Database → Functions:
     `submit_car_fact`, `vote_car_fact`.

3. **Plug the URL + anon key into the plugin.**
   - For development: set `Settings.CommunityBackendUrl` and
     `Settings.CommunityBackendAnonKey` directly in
     `TrueforcePlugin.GeneralSettings.json`. Toggle
     `Settings.CommunityEnabled = true`.
   - For release builds: the production URL and anon key are constants in
     `CommunityBackend.cs`, written into `Settings.CommunityBackendUrl` and
     `Settings.CommunityBackendAnonKey` on every launch, so a key rotation
     ships with the next release. `Settings.CommunityEnabled` defaults to
     true and is the user's off switch; the welcome screen discloses it.

4. **Smoke-test from the plugin.**
   - Enable community, save a CarFact correction via the Effects tab,
     watch the Supabase logs (Database → Logs) for the RPC call.
   - Run `select * from car_fact_consensus` in the SQL Editor to verify
     the recompute updated it.
   - Try posting an invalid payload via the SQL Editor:
     `select submit_car_fact('FH6', 'X', 'engine_layout',
        '{"cyl":"banana","config":99}'::jsonb);`
     — expect a `raise exception 'invalid payload for fact_type
     engine_layout'`.

## Moderation operations

These all use the service_role key (Project Settings → API), never the
anon key.

### Suppress a bad consensus entry (so the next submission can't unsuppress it)

```sql
update car_fact_consensus
   set is_suppressed = true
 where game = 'FH6' and car_id = 'Car_X' and fact_type = 'engine_layout';
```

### Ban a submitter (an account uuid, an `anon:<id>`, or `ip:<sha256>`)

```sql
insert into submitter_blocked (submitter_id, reason)
  values ('<the-id>', 'sybil flood 2026-06-04');
```

### Rotate the salt (legacy; no longer invalidates anything)

The salt was read by `_derive_submitter_id`, which no current RPC calls.
Identities are account uuids or `anon:<id>` and bans key on those or on
`ip:<sha256>`, so rotating the salt resets neither reputation nor bans. The
row is kept only so the old migrations stay replayable.

```sql
update app_config
   set value = encode(gen_random_bytes(16), 'hex'),
       updated_at = now()
 where key = 'submitter_id_salt';
```

### Inspect what's been submitted for a car

```sql
select created_at, fact_type, payload, submitter_id
  from car_fact_submissions
 where game = 'FH6' and car_id = 'Car_2267'
 order by created_at desc
 limit 50;
```

### Review community reports

Reports normally come to you in the private #preset-reports Discord channel
(an after-insert trigger on `report_flags` posts each one via the
`report-notify` Edge Function), and you action them with the Remove / Dismiss
buttons there. This query is the pull-based backstop / audit view.

```sql
select rf.created_at, rf.target_type, rf.category, rf.note, rf.status,
       coalesce(p.name, g.name, ce.name, pk.name) as target_name
  from report_flags rf
  left join presets        p  on rf.target_type = 'preset'        and p.id  = rf.target_id
  left join game_presets   g  on rf.target_type = 'game_preset'   and g.id  = rf.target_id
  left join custom_engines ce on rf.target_type = 'custom_engine' and ce.id = rf.target_id
  left join packs          pk on rf.target_type = 'pack'          and pk.id = rf.target_id
 where rf.status = 'open'
 order by rf.created_at desc;
```

To act by hand (the buttons do exactly this): suppress the target and close
the report.

```sql
update presets set is_suppressed = true where id = '<target-id>';   -- the right table per target_type
update report_flags set status = 'removed', resolved_by = 'manual', resolved_at = now()
 where id = '<report-id>';
-- or to dismiss without touching the content:
update report_flags set status = 'dismissed', resolved_by = 'manual', resolved_at = now()
 where id = '<report-id>';
```

## Why Supabase

- Hosted Postgres + PostgREST means we get a HTTP API for free: the core
  read/write path is RPC only, with no custom server fronting the data API.
  The Edge Functions we do deploy (see `functions/`) handle outbound
  integrations, not the data path.
- The PostgREST RPC pattern is the right fit for "client can't be
  trusted with table access" — declarative functions enforce all the
  identity / validation / rate-limit logic in one place.
- Cloudflare-fronted edge gives us a `cf-connecting-ip` header that the
  per-IP volume cap and the `ip:<sha256>` ban rows need.
- Row-Level Security is enabled belt-and-braces even though the revokes
  do the real gating; if a grant ever gets clobbered we fail closed.

## Cost notes

The CarFacts read path is plugin-startup pull only, not per-frame. Even
with thousands of users the read load is bounded by the number of unique
plugin-launch events per day. Writes are limited by the per-caller rate
limit. Supabase free tier comfortably covers any reasonable usage; first
paid tier ($25/mo) kicks in if/when concurrent connections or row counts
grow.
