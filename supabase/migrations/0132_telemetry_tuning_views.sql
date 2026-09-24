-- Telemetry tuning views. Added 2026-09-24.
--
-- These answer one question that until now cost a hand-written two-level jsonb
-- flatten every single time it was asked: "how does each game actually get
-- tuned, and where do installs disagree with each other?"
--
--   select * from public.v_game_disagreement where game = 'FH6';
--
-- telemetry_game_settings stores one preset body per (anon_id, game) as jsonb.
-- The body is one level of effect objects (GearShift, RevLimiter, AudioCapture)
-- sitting over scalars, mixed in with top-level scalars. Nothing useful comes
-- out of it with -> chains, because the interesting part is which KEYS differ,
-- not which values a key you already guessed happens to hold. So v_game_tuning
-- unpivots the whole body into one row per leaf, and v_game_disagreement groups
-- those leaves and keeps only the ones where installs do not agree.
--
-- DEPTH. Two levels is deliberate, not an approximation, and it was measured
-- against live data on 2026-09-24 before this view was written: at depth 2 the
-- bodies hold 770 numbers and 287 booleans and zero objects or arrays, and at
-- depth 1 they hold objects, numbers, nulls and booleans with no arrays. A leaf
-- is therefore always reached in at most two hops today. If an effect ever
-- gains a nested sub-object, its children lose their own rows and the
-- sub-object instead appears as one opaque blob leaf, compared whole. An array
-- at either level does the same, because branch 1 admits anything that is not
-- an object. Neither is silent corruption, but neither is a usable leaf: an
-- array would flag a disagreement on element reordering alone. The regression
-- check is that depth 2 type census: if it ever reports 'object' or 'array',
-- this view needs a third branch.
--
-- GOTCHA (the one that bites): every aggregate over the value goes through
-- val::text. There is no max(jsonb) aggregate in Postgres and string_agg has no
-- jsonb overload, so the raw column cannot be aggregated at all. Casting first
-- also keeps distinct_vals honest against values_seen: jsonb compares 1.0 and
-- 1.00 as equal numerics while their text renderings differ, so counting one
-- way and rendering the other would print a values_seen holding more entries
-- than distinct_vals claims. The cost of casting is the mirror case: two
-- installs holding the same number at different scale (1 and 1.0) will read as
-- a disagreement, because text does not normalize what numeric would. The live
-- bodies already carry variable-precision doubles (567.0934 beside
-- 572.07560640569375), so a serializer change on either side is enough to
-- manufacture one.
--
-- WHAT A DISAGREEMENT IS NOT. Only keys that two installs both SENT can
-- disagree. A key one install omits entirely does not appear as a difference,
-- it simply contributes no row. That is the right behavior for reading tuning
-- intent (a key absent because the client's schema predates it is not a user
-- decision), but it does mean these views under-report churn across a release
-- that adds settings.
--
-- Nor is a row necessarily two users disagreeing. A wide oldest-to-newest
-- spread means the rows may straddle a defaults migration, so check that before
-- reading a row as user intent: ModeBDefaultsGeneration moves fields on installs
-- that never touched the bench, and for a while afterwards this table holds
-- pre-migration rows beside post-migration ones. Two installs differing only by
-- which build wrote the row render here exactly like a tuning disagreement,
-- which is why the disagreement view carries oldest and newest.
--
-- READING IT AT SCALE. values_seen has no cap, so once a path has been seen by
-- dozens of installs it stops being readable (one of these paths already holds
-- a 17-significant-digit double). Select distinct_vals and installs and leave
-- values_seen out of the list at that point.

-- ---- SECURITY ----
--
-- This is the part worth reading twice, because the naive version of this file
-- would have published the entire raw telemetry table to the internet.
--
-- public.telemetry_game_settings has RLS enabled AND forced, and carries ZERO
-- policies: it is a deny-all table, reachable only by a role holding BYPASSRLS.
-- Checked against the live database 2026-09-24: postgres and service_role have
-- rolbypassrls, anon and authenticated do not.
--
-- A plain Postgres view executes as its OWNER, not as its caller. These views
-- are created by the migration runner, so they are owned by postgres, which
-- holds BYPASSRLS. A plain view over this table would therefore hand back every
-- row of raw per-install telemetry to anyone who can select from the view, with
-- the table's own deny-all RLS silently stepped around. The plugin ships a
-- Supabase anon key inside its binary, so anon is not a theoretical caller
-- here, it is a published one. security_invoker = true (Postgres 15 and up; the
-- server reports 17.6) makes the view run as the CALLER instead, so anon meets
-- the same deny-all RLS it would meet on the table and reads nothing.
--
-- Defense in depth, because one flag should not be the only thing standing
-- between a deny-all table and a shipped API key:
--
--   1. security_invoker = true, so RLS is evaluated against the caller.
--   2. An explicit revoke below. This is NOT redundant. Supabase ships default
--      privileges on schema public granting arwdDxtm to anon and authenticated
--      for every new relation (defaclobjtype 'r', which covers views), so a
--      freshly created view arrives already granted to both. This is the same
--      trap 0129 found on functions, where "revoke from public" did not remove
--      a grant that default privileges had handed out by name.
--   3. anon and authenticated already had their table-level select revoked by
--      0128 (the do-block at its line 67 that enables, forces and revokes
--      across the five telemetry tables; the table itself is created 8 lines
--      above it), so a security_invoker view has no privilege to inherit.
--
-- These are owner and service_role only analysis views. Nothing in the plugin
-- reads them and nothing ever should; they exist for a human holding the
-- service key. If a future feature needs to show users aggregate tuning data,
-- it belongs behind a security definer function with its own aggregation and
-- its own minimum cohort size, not behind a select grant on these.

-- Idempotent, in the house style: every migration here is re-applied. Plain
-- "create or replace view" is not enough on its own, because it refuses to
-- change a view's column names, types or order, which would make any future
-- edit to these column lists fail on a database that already holds the old
-- shape. Drop and recreate instead, dependent view first.
drop view if exists public.v_game_disagreement;
drop view if exists public.v_game_tuning;

-- One row per settings leaf. Dotted path, so a top-level scalar reads
-- "FfbSmoothTimeConstantMs" and a nested one reads "GearShift.Waveform".
create view public.v_game_tuning
with (security_invoker = true)
as
  -- Branch 1: the top-level scalars. <> 'object' keeps json nulls, which
  -- matters: a key explicitly sent as null is a real observation (the install
  -- has the setting and left it unset) and is not the same as the key being
  -- absent.
  select t.anon_id,
         t.game,
         t.updated_at,
         e.k as path,
         e.v as val
  from public.telemetry_game_settings t,
       lateral jsonb_each(t.preset) e(k, v)
  where jsonb_typeof(e.v) <> 'object'
  union all
  -- Branch 2: one level down, inside each effect object.
  select t.anon_id,
         t.game,
         t.updated_at,
         e.k || '.' || n.k,
         n.v
  from public.telemetry_game_settings t,
       lateral jsonb_each(t.preset) e(k, v),
       lateral jsonb_each(e.v) n(k, v)
  where jsonb_typeof(e.v) = 'object';

comment on view public.v_game_tuning is
  'Owner and service_role only. Flattens telemetry_game_settings.preset into one row per settings leaf (dotted path, two levels deep). security_invoker = true so the caller RLS applies; the underlying table is deny-all RLS and this view must never be granted to anon or authenticated.';

-- The payoff: per game, which settings do installs not agree on. installs is
-- how many distinct installs contributed a value for that path at all, so a
-- distinct_vals of 2 across 40 installs reads very differently from 2 across 2.
-- oldest and newest bound when the contributing rows were written: a wide
-- spread is the tell that the disagreement may be a defaults migration rather
-- than two users, per the header. Today every row sits inside a 24 hour window,
-- so it reads as intent; after the next release that stops being true.
create view public.v_game_disagreement
with (security_invoker = true)
as
  select game,
         path,
         count(distinct val::text) as distinct_vals,
         count(distinct anon_id)   as installs,
         min(updated_at)           as oldest,
         max(updated_at)           as newest,
         string_agg(distinct val::text, ' | ') as values_seen
  from public.v_game_tuning
  group by game, path
  having count(distinct val::text) > 1;

comment on view public.v_game_disagreement is
  'Owner and service_role only. Per (game, settings path), the paths where installs disagree, with how many installs contributed, when the contributing rows were written (oldest, newest) and the distinct values seen. A wide oldest-to-newest spread may mean the rows straddle a defaults migration rather than two users disagreeing. Aggregates go through val::text because there is no max(jsonb) and string_agg has no jsonb overload. security_invoker = true; never grant to anon or authenticated.';

-- See the SECURITY note above: this revoke is load-bearing, not decoration.
-- Supabase default privileges grant new relations to anon and authenticated at
-- creation time, so without these two lines the views ship readable.
revoke all on table public.v_game_tuning       from public, anon, authenticated;
revoke all on table public.v_game_disagreement from public, anon, authenticated;

-- service_role already holds this via those same default privileges. Stated
-- explicitly so the intended reader survives any future tightening of the
-- defaults, and so the grant list reads as a decision rather than an accident.
grant select on table public.v_game_tuning       to service_role;
grant select on table public.v_game_disagreement to service_role;
