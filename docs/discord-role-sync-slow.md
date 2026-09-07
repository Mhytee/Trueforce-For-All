# discord-role-sync takes 96 seconds per run, and nothing can tell you

Handoff for a separate session. Found 2026-09-07 while working on the arcade leaderboards; it is
unrelated to that work and is a live bug in something already shipped.

Nothing here has been fixed. No code has been changed for this issue.

## Symptom

`discord-role-sync` runs every 15 minutes and takes **96 seconds on average**, for **11 linked
Discord users**. Roughly one run in six reaches ~150 seconds, which is Supabase's edge function
wall-clock ceiling, and is therefore killed partway through its work.

Measured, consistent across 12 hours, not a spike:

```
hour    invocations   avg_ms    runs over 145s
09:00        6         96,222         0
08:00        6         95,365         1
07:00        6         97,230         1
06:00        6         96,024         1
05:00        6         96,392         0
```

Note **6 invocations an hour**, not the 4 a `*/15` schedule implies. Something else triggers it too.
The plugin can: `AchievementClient` POSTs `/functions/v1/discord-role-sync?op=sync` from the Account
tab, and that path is user-initiated.

## Why nobody noticed

Three separate things make a healthy run and a killed run look identical:

1. **The cron job reports success.** `cron.job_run_details` shows `tf4all-role-sync` with 0 failures
   in 8358 runs. All it measures is that `net.http_post` queued the request.
2. **pg_net gives up after 5 seconds.** `0046_schedule_crons.sql` never passes
   `timeout_milliseconds`, so it uses the 5000 ms default. Every response is recorded as a timeout
   regardless of what the function did.
3. **The function keeps running** after the caller disconnects, so most runs probably do finish.

So the only health signal anyone has is guaranteed to say "timeout" whether things are fine or not.

## Reproduce

Function id `1b06d50d-b367-40be-a927-7520fe0aadfb` is `discord-role-sync`.

```sql
-- Execution time, via the MCP query_logs tool (ClickHouse, not Postgres):
select toStartOfHour(timestamp) as hour, count(*) as invocations,
       round(avg(toFloat64OrNull(log_attributes['execution_time_ms'])), 0) as avg_ms,
       countIf(toFloat64OrNull(log_attributes['execution_time_ms']) > 145000) as at_the_ceiling
from logs
where source = 'function_edge_logs'
  and log_attributes['function_id'] = '1b06d50d-b367-40be-a927-7520fe0aadfb'
group by hour order by hour desc
```

```sql
-- The timeouts, in Postgres. Minutes 15 and 45 are role-sync ALONE: 12 of 12 timed out.
select extract(minute from created)::int as minute, count(*) as n,
       count(*) filter (where status_code is null) as timed_out,
       count(*) filter (where status_code between 200 and 299) as ok
from net._http_response group by 1 order by 1;
```

`net.http_request_queue` rows are reaped quickly, so joining a response back to its URL usually
fails. Correlate by cron minute instead.

## Two separate problems

### 1. The 5 second timeout is cosmetic, and cheap to fix

`net.http_post` accepts `timeout_milliseconds` (pg_net 0.20.3 confirmed on this project) and
`0046_schedule_crons.sql` omits it. Passing something like 30000 restores a real health signal.

This does not make anything faster. It only stops the monitoring lying.

Worth applying to every `tf4all-*` job, since they all inherit the same 5 s default: role-sync,
entitlement-sync, backup-gc, backup-warn, patreon-supporters, motd-milestones, motd-stats,
ban-reaper, report-card-sweep.

### 2. The 96 seconds is the real bug, and is not diagnosed

`supabase/functions/discord-role-sync/index.ts`, 253 lines. Eleven users should take a second or
two. Candidate causes, none confirmed:

- **Line 145, the supporter loop.** One `discordGet(/guilds/{id}/members/{discord_id})` per linked
  user, plus an `rpc/set_supporter` per user, all sequential and awaited.
- **Lines 220-227, the role application loop.** Nested `for` over plan entries and then over each
  role to add and remove, one awaited Discord PUT or DELETE per role. Sequential.
- **Line 38-40, the retry helper.** On HTTP 429 it sleeps `retry-after` up to 5 seconds and retries
  once. If Discord is rate limiting the sequential calls above, each one can add up to 5 seconds,
  and the arithmetic gets to 96 seconds quickly.

The rate-limit sleep interacting with sequential per-user, per-role calls is the most likely story,
but it has NOT been verified. Check the function's own console output for 429s before assuming.

`compute_member_metrics` (line 178) is also worth timing directly; it is a single RPC but its cost
is unknown from here.

## Useful context

- 132 rows in `auth.users`, 107 in `profiles`, **11 in `discord_links`**, 13 achievements.
- The function is `verify_jwt = true` and gates in-function on `service_role` or `authenticated`;
  an authenticated caller can only sync themselves (`claims.sub`).
- It runs DRY-RUN and returns a plan when `DISCORD_BOT_TOKEN` or `DISCORD_GUILD_ID` are unset, so it
  can be exercised safely without touching the guild.
- `discord-role-sync` is at version 22 and is actively maintained; check whether a recent version
  introduced the slowdown before rewriting anything.

## Suggested order

1. Add `timeout_milliseconds` so the health signal is real. Small, safe, independent.
2. Read the function's own logs for 429s to confirm or kill the rate-limit theory.
3. Only then change the loops. Batching or parallelising Discord calls has its own rate-limit
   consequences, so measure first.

## What NOT to conclude

"Role sync is broken" is too strong. Most runs finish inside the ceiling and probably apply roles
correctly. What is certain is that roughly one run in six is cut off, and that nobody could tell the
difference either way. Fix the visibility before judging the severity.
