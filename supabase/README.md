# Trueforce For All community backend

Hosts the CarFacts community data (and, eventually, preset sharing) on a
single Supabase project. The schema treats the anon API key as **public**
and gates every write through `SECURITY DEFINER` RPC functions that
validate input, derive identity server-side, and rate-limit by IP. Direct
table writes from the anon key are revoked.

## What's in here

- `migrations/0001_carfacts_init.sql` — CarFacts schema: private
  submissions / votes / app_config / submitter_blocked tables, public
  `car_fact_consensus` table, two callable RPCs (`submit_car_fact`,
  `vote_car_fact`), payload normalization per fact_type, IP-based rate
  limiting, salt-rotating submitter_id derivation, suppression flag for
  moderation.

Future migrations land alongside `0001_*`; Supabase applies them in
filename order. Always make new migrations idempotent (`if not exists`,
`create or replace`, `drop ... if exists`) so re-runs are safe.

## Security model

**Threat assumption**: the anon API key ships in the plugin DLL. Anyone
with the plugin has it. Treat it as a public token.

**What the anon key can do**:

1. `select` from `car_fact_consensus` (pull trusted facts).
2. `execute` `submit_car_fact(...)`.
3. `execute` `vote_car_fact(...)`.

**What the anon key can NOT do**:

- Read raw submissions, votes, the salt config, or the block list.
- Insert / update / delete anything directly.
- Call the internal helpers (`_recompute_car_fact_consensus`,
  `_derive_submitter_id`, `_client_ip`, `normalize_car_fact_payload`).

**Identity**: `submitter_id` and `voter_id` are derived inside the RPCs as
`sha256(client_ip + salt)`. The salt is held in `app_config` and a
moderator can rotate it to invalidate accumulated attacker reputation.
Legitimate users get re-seeded on their next submission, attackers lose
their accumulated standing. False-positive bans wear off when the salt
rotates — that's a deliberate trade-off against permanent collateral
damage.

**Sybil resistance**:
- Consensus uses `count(distinct submitter_id)` per payload, so a single
  IP submitting the same value 100 times contributes 1, not 100.
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

**Rate limiting**: per IP, 30 submissions and 30 votes per rolling hour.
Not bypassable by waiting for salt rotation (the rate limit keys on raw IP
hash, which is salt-independent).

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
   - For release builds: hardcode the production values into
     `CommunityClient` constants and have `Settings.CommunityEnabled`
     gate the actual network calls (default off until we're confident).

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

### Ban a submitter (use a hash you've identified offline)

```sql
insert into submitter_blocked (submitter_id, reason)
  values ('<the-hash>', 'sybil flood 2026-06-04');
```

### Rotate the salt (invalidates all current submitter_ids)

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

- Hosted Postgres + PostgREST means we get a HTTP API for free; no Edge
  Functions or custom server to deploy.
- The PostgREST RPC pattern is the right fit for "client can't be
  trusted with table access" — declarative functions enforce all the
  identity / validation / rate-limit logic in one place.
- Cloudflare-fronted edge gives us a `cf-connecting-ip` header that the
  IP-derived identity model needs.
- Row-Level Security is enabled belt-and-braces even though the revokes
  do the real gating; if a grant ever gets clobbered we fail closed.

## Cost notes

The CarFacts read path is plugin-startup pull only, not per-frame. Even
with thousands of users the read load is bounded by the number of unique
plugin-launch events per day. Writes are limited by the per-IP rate
limit. Supabase free tier comfortably covers any reasonable usage; first
paid tier ($25/mo) kicks in if/when concurrent connections or row counts
grow.
