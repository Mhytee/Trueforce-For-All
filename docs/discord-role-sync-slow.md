# discord-role-sync took 142 seconds because it never asked what roles a member already had

Found 2026-09-07 while working on the arcade leaderboards. Diagnosed and fixed the same day.

Status: FIXED. `supabase/functions/discord-role-sync/index.ts` plus
`supabase/migrations/0116_cron_http_timeouts.sql`.

## What it actually was

`op=sync` built, for every linked member, an `add` list and a `remove` list that between them
covered every managed role. `remove` was `managed.filter(id => !desired.has(id))`, so
`add.length + remove.length` was exactly `managed.length` for every member no matter what they had
earned. It then awaited one Discord PUT or DELETE per entry, having never asked Discord what roles
the member currently held, so it could not tell a no-op from a real change.

11 linked members times 13 managed roles is 143 sequential writes, every 15 minutes, forever.

Measured cost of a Discord role write from this function is about 0.9 seconds gross of retry
sleeps, in both sizes of run we have:

| run | members | writes | wall clock | per write |
|---|---|---|---|---|
| `?op=sync&user=<uid>` | 1 | 13 | 11,723 ms | 902 ms |
| `?op=sync` (full sweep) | 11 | 143 | 140,097 ms | 980 ms |

143 times 0.9 s is the whole of the runtime. The fix is one read per member and writes only for
real differences, which is 11 reads and normally zero writes.

## What the first writeup got wrong

Recorded because the wrong numbers are what produced the wrong hypothesis.

1. **"96 seconds on average" blended two different jobs.** Two cron jobs hit the same function id.
   Split by query string over 24 hours: `?op=sync` averaged 142,537 ms across 95 runs, and
   `?op=entitlements` averaged 5,418 ms across 48. There was never a 96 second run.
2. **"6 invocations an hour, something else triggers it too" was not the plugin.** It is 4
   role-sync on `*/15` plus 2 entitlement-sync on `*/30`, both pointed at `discord-role-sync`.
3. **"Roughly one run in six is killed at the ceiling" was two errors canceling out.** Every
   `op=sync` run was slow, not one in six: 84 of 95 exceeded 140 s and the fastest ever seen was
   131.7 s. But they were not being killed. 94 of 95 returned 200 with a complete body; exactly one
   returned 504. The sweep was finishing with about 8 seconds to spare.
4. **`compute_member_metrics` was never a suspect worth keeping.** `explain analyze` puts it at
   56.7 ms for 40 rows.
5. **The retry sleep is a contributor, not the mechanism.** Both measured runs contain 429s, so
   neither number is clean of retry sleeps, and the 902 vs 980 ms/write gap between them is n=1
   against n=1. What rules the sleep out as the main cause is the scale: at ~8 abandoned writes
   per sweep the retries can account for tens of seconds, not 142. The bulk is 143 plain
   sequential round trips. A saturated rate-limit bucket would also predict a 13 write burst
   costing far less per write than a 143 write one, and it does not.

## What was really invisible

At least 72 of the 95 sweeps silently abandoned about 8 of their 143 writes.

`discord()` retries a 429 once, then returns status 429, which becomes a string in `errors`. Nobody
ever saw that array: pg_net hung up after its default 5000 ms and discarded the body, and the
function wrote nothing to its own console. The count is recoverable from the response sizes in
`function_edge_logs`: 72 of 95 runs returned an identical 517 byte body against a 95 byte body for
a clean run, and each error string is about 54 bytes. Size cannot say which status, though. Every
non-404 failure lands in `errors` as a three digit code, so 403 and 500 look exactly like 429 from
here, and 72 byte-identical bodies fit a standing permission failure on the same roles just as well
as they fit rate limiting. The only error string actually read was a 429, from the single-user run.
The new `console.log` settles it on the first deployed sweep.

So the honest severity was never "role sync is broken" and never "it is fine". It was: uniformly 30
times slower than it needed to be, sitting 8 seconds from the ceiling, dropping a handful of role
changes every run, and structurally unable to tell anyone.

## The fix

**Function.** Read each planned member once with `GET /guilds/{id}/members/{discord_id}`, then guard
the two existing write loops: skip the PUT when the member already holds the role, skip the DELETE
when they do not.

The guards are applied to `p.add` and `p.remove` exactly as they were already built. That is
load-bearing. The write set can then only shrink relative to the old behavior, which is what makes
the change safe to ship without a dry run. Rebuilding the diff from the member's actual role array
instead would sweep in the three Patreon tier roles, `op=entitlements` would read no tier role, call
`set_supporter(p_on => false)`, and start the two year backup retention timer on paying supporters.
For the same reason a failed member read skips the member rather than treating them as holding
nothing.

Also in the function: a 10 s deadline and one `retry-after` aware retry on the member read now that
it is the hot path, a `console.log` of the run summary so the next person has a signal that pg_net
cannot throw away, one line per run recording the member read's actual rate limit headers, and a
bound of 5 rows per run on the orphan sweep, which is still 13 blind writes per queued row.

`applied: 0` with everything in `unchanged` is the healthy steady state now. It is not a regression.

**Migration 0116.** `timeout_milliseconds := 30000` on the five `tf4all-*` jobs that omitted it, so
`net._http_response` stops recording a timeout for every run regardless of outcome.
`tf4all-arcade-digest` already had it. `tf4all-report-card-sweep` was left alone on purpose: its
command is a nested dollar quoted `do` block and retyping it to change one argument is a worse risk
than the blindness it buys. Three of the nine the original writeup listed (`tf4all-ban-reaper`,
`tf4all-motd-milestones`, `tf4all-motd-stats`) are plain SQL with no `net.http_post`, so that list
was too long.

30000 is deliberately not raised toward the 150 s edge ceiling. The pg_net worker holds a Postgres
transaction open until the slowest request in its batch resolves.

## Result, measured

Function deployed as version 23, then 0116 applied, in that order. A full sweep immediately after:

```
141,114 ms  ->  4,862 ms
{"dryRun":false,"scope":"all","members":40,"linked":11,"applied":0,"removed":0,
 "unchanged":117,"notInGuild":2,"skipped":0,"orphansCleared":0,"orphansMore":false,"errors":[]}
```

117 is 9 members times 13 roles, so every role on every reachable member was already correct and
nothing needed writing. `errors` is empty, so the roughly 8 abandoned writes a run are gone with the
writes that produced them.

The run also surfaced something the old code could not report: **`notInGuild: 2`**. Two of the 11
linked accounts have left the Discord server, and each was absorbing 13 pointless writes a sweep.
They are not a fault, and nothing needs doing about them, but they were invisible before.

The rate limit line settles the question the old code could only guess at:

```
[role-sync] member-read limits bucket=e06f83c33559dfd4dc34f5666fdfa1d3 limit=5 remaining=4 reset-after=1.000
```

The member read route is 5 requests a second, so 11 reads has a floor of about 2.2 s. The measured
4.9 s is that floor plus `compute_member_metrics`, the achievements query and boot. Room to grow a
long way before this needs thinking about again.

## Still open, deliberately not in this change

`managed` is built from enabled achievements only, so disabling an achievement, or clearing its
`discord_role_id`, strands that role on every member holding it. The role leaves the removal set at
the same moment it stops being granted, so nothing ever takes it back off. Latent today because all
13 achievements are enabled and all carry a role id. Fixing it widens the removal set, which is the
one direction this change must not move in, so it wants its own commit and its own dry run.
