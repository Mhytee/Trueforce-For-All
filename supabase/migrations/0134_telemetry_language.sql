-- 0134: telemetry language. Two nullable columns on public.telemetry, two
-- defaulted parameters on telemetry_ping, one aggregate-only reading view.
--
-- WHAT. ui_lang is the language Windows is displayed in on the install's PC.
-- fmt_lang is the language subtag of the Windows regional-format locale, the
-- country picked at Windows setup that decides date and number formats. Both
-- arrive as lowercase two-letter ISO 639-1 codes ("de", "pt") or null, with
-- no region, and they answer exactly one question: which languages would a
-- translated plugin reach. ui_lang misses anyone who runs Windows in English
-- and thinks in something else; fmt_lang catches that person but also counts
-- an English speaker living abroad. The truth sits between the two columns,
-- which is why both exist. Read docs/telemetry-runbook.md 2.6 before quoting
-- either number.
--
-- WHY COLUMNS AND NOT THE SETTINGS BLOB. The blob has an 8192-byte cap that,
-- when crossed, nulls the WHOLE blob while the ping still answers ok (see
-- 0131 and docs/telemetry-runbook.md 2.5). It was measured about 60 percent
-- full on 2026-09-24 and it grows every time a setting is classified Portable
-- for backup, for reasons that have nothing to do with telemetry. It is also
-- built by reflection over BackupProjection.Portable, so an OS-derived value
-- that is not a setting cannot ride it without inventing a fake setting. Two
-- plain columns cost nothing and cannot take the blob down with them.
--
-- WHY NO RECEIPT KEY. The value is stateless: nothing is persisted on the
-- client, it is recomputed at the single SendPing call site on every ping and
-- sent again the next day. A filtered or dropped value is back tomorrow, so
-- there is nothing for the client to commit and nothing for the receipt to
-- confirm. The receipt shape (ok, settings, games, presets) is unchanged.
--
-- WHY NO telemetry_devices COLUMN AND NO telemetry_rollup CHANGE. Raw rows
-- prune at 90 days, and an install silent for that long should not keep
-- voting on which language to translate into. The reading view below works
-- from the raw table over a 30-day window and needs no durable copy.
-- Re-emitting telemetry_rollup, which has already needed a hardening pass
-- (0129), is the riskiest edit available in this schema, and this feature
-- does not need it.
--
-- TRANSACTION. "supabase db query --linked -f" runs the whole file through
-- the Management API, which wraps it in ONE implicit transaction. There is no
-- begin/commit here on purpose. lock_timeout is the first statement so that
-- the ALTER TABLE below, which takes an ACCESS EXCLUSIVE lock on
-- public.telemetry, gives up after 3 seconds instead of queueing behind a
-- long reader and then blocking every ping behind itself.
--
-- CALLERS. Exactly ONE released build, v0.4.0, has ever called this RPC. It
-- always sends all seven keys, with explicit JSON nulls for the ones it has
-- nothing for (TelemetryClient.cs builds a JObject and does not strip nulls).
-- The next build sends nine keys. Both bodies must resolve to the one
-- function below: the seven-key body through the two defaults, the nine-key
-- body exactly.
--
-- DEPLOY ORDER. Apply and verify this migration BEFORE the plugin build that
-- sends p_ui_lang and p_fmt_lang ships. Against the seven-argument function a
-- nine-key body matches nothing, PostgREST answers 404 with code PGRST202,
-- and the WHOLE ping fails: base row, settings snapshot and game-days
-- included. It self-heals on the client's 6-hour retry once the migration
-- lands, but every install that ticks in between gets a failed ping and
-- waits up to six hours for the next try. The reverse order is safe: the old
-- build's seven-key body keeps working against the nine-argument function.
--
-- WHEN. Apply outside 04:20-04:40 UTC. telemetry_rollup runs at 04:25 UTC
-- (0128) and reads from and deletes from public.telemetry; the ALTER TABLE
-- would either wait on it and hit lock_timeout, or make it wait.
--
-- Idempotent: add column if not exists, drop function if exists, drop view
-- if exists. Safe to re-run.

set lock_timeout = '3s';
set statement_timeout = '60s';

-- ---- 1) columns ----

alter table public.telemetry add column if not exists ui_lang  text;
alter table public.telemetry add column if not exists fmt_lang text;

comment on column public.telemetry.ui_lang is
  'Language Windows is displayed in on the install PC. Two-letter ISO 639-1 code, lowercase, no region, or null. Server-gated by telemetry_ping (^[a-z]{2}$); anything else is stored as null. Stateless: resent on every ping, never persisted client-side.';

comment on column public.telemetry.fmt_lang is
  'Language subtag of the Windows regional-format locale on the install PC (the country chosen at setup that decides date and number formats). Two-letter ISO 639-1 code, lowercase, no region, or null. Server-gated by telemetry_ping (^[a-z]{2}$); anything else is stored as null. A ceiling, not a stated preference.';

-- ---- 2) telemetry_ping: drop every narrower form ----
--
-- Leaving any narrower form beside the wider defaulted one is a real Postgres
-- overload. PostgREST resolves an RPC call by the SET OF JSON KEYS in the
-- body and prefers an exact key-set match, so a leftover seven-argument form
-- would keep taking every v0.4.0 ping in silence, and any body missing one of
-- the new keys would land on it and store no language at all. One function,
-- one target. The five-argument form is dropped again here because the
-- recovered ledger shows it WAS applied to production
-- (recovered/20260921065749_telemetry_usage_stats_hardening.sql) and there is
-- no ledger proof that 0128's drop of it ever ran. Both drops are no-ops when
-- the form is already gone.

drop function if exists public.telemetry_ping(text, text, text, text, jsonb);
drop function if exists public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb);

-- The nine-type form this file creates is dropped too, so a re-run replaces
-- it instead of failing with duplicate_function. 0131 set the pattern: drop
-- exactly the signature you are about to create. The grants are re-issued
-- below, after the create, so nothing is lost by the drop.
drop function if exists public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text);

-- ---- 3) telemetry_ping: the 0131 body plus the two language parameters ----
--
-- Body is 0131's verbatim except for: two declared locals that gate the new
-- parameters through the regex, two extra insert columns, and two extra
-- coalesce lines in the on-conflict update so a later ping on the same day
-- that carries null (or junk) does not erase a value already stored.

create function public.telemetry_ping(
  p_anon_id        text,
  p_plugin_version text  default null,
  p_wheel          text  default null,
  p_game           text  default null,
  p_settings       jsonb default null,
  p_games          jsonb default null,
  p_game_presets   jsonb default null,
  p_ui_lang        text  default null,
  p_fmt_lang       text  default null
) returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
  v_id       text    := left(coalesce(nullif(trim(p_anon_id), ''), ''), 64);
  v_settings boolean := false;
  v_games    jsonb   := '[]'::jsonb;
  v_presets  jsonb   := '[]'::jsonb;
  -- Language codes: exactly two lowercase ASCII letters or nothing. The
  -- client already lowercases and strips the region; this is the server's
  -- own gate against anything else, and it stores null rather than raising.
  v_ui       text    := case when p_ui_lang  ~ '^[a-z]{2}$' then p_ui_lang  end;
  v_fmt      text    := case when p_fmt_lang ~ '^[a-z]{2}$' then p_fmt_lang end;
begin
  if v_id = '' then
    return jsonb_build_object('ok', false, 'settings', false,
                              'games', v_games, 'presets', v_presets);
  end if;

  if p_settings is not null and pg_column_size(p_settings) > 8192 then
    p_settings := null;          -- too large to store; the receipt says so
  end if;
  v_settings := p_settings is not null;

  insert into public.telemetry as t
      (anon_id, day, plugin_version, wheel, game, settings, ui_lang, fmt_lang)
  values (v_id, current_date,
          left(p_plugin_version, 32), left(p_wheel, 60), left(p_game, 60), p_settings,
          v_ui, v_fmt)
  on conflict (anon_id, day) do update
    set plugin_version = excluded.plugin_version,
        wheel          = excluded.wheel,
        game           = coalesce(excluded.game, t.game),
        settings       = coalesce(excluded.settings, t.settings),
        ui_lang        = coalesce(excluded.ui_lang, t.ui_lang),
        fmt_lang       = coalesce(excluded.fmt_lang, t.fmt_lang),
        updated_at     = now();

  -- {g: <string>, d: yyyy-MM-dd}. ISO-only, parsed through the non-throwing
  -- helper so an impossible-but-ISO date cannot abort the ping. Valid rows are
  -- selected BEFORE the limit, so junk cannot crowd them out. `k` carries the
  -- caller's untruncated key so the receipt matches what the client queued.
  if p_games is not null and jsonb_typeof(p_games) = 'array'
     and jsonb_array_length(p_games) <= 400 then
    with accepted as (
      select g, d, k from (
        select left(trim(e->>'g'), 60) as g,
               public._telemetry_safe_date(e->>'d') as d,
               (e->>'g') || '|' || (e->>'d') as k
        from jsonb_array_elements(p_games) e
        where jsonb_typeof(e->'g') = 'string'
          and length(trim(e->>'g')) > 0
          and e->>'d' ~ '^\d{4}-\d{2}-\d{2}$'
      ) parsed
      where parsed.d is not null
        and parsed.d between current_date - 60 and current_date
      limit 200
    ), ins as (
      insert into public.telemetry_game_days (anon_id, game, day)
      select v_id, g, d from accepted
      on conflict (anon_id, game, day) do nothing
    )
    select coalesce(jsonb_agg(distinct k), '[]'::jsonb) into v_games from accepted;
  end if;

  -- {g: <string>, p: <object>}. De-duplicated first: two entries for one game
  -- would make ON CONFLICT DO UPDATE touch the same row twice and abort the
  -- whole ping. The cap admits only NEW games; a game already stored can always
  -- be refreshed, so an install past the cap does not go permanently stale.
  if p_game_presets is not null and jsonb_typeof(p_game_presets) = 'array'
     and jsonb_array_length(p_game_presets) <= 20 then
    with parsed as (
      select left(trim(e->>'g'), 60) as g, e->'p' as p, e->>'g' as k
      from jsonb_array_elements(p_game_presets) e
      where jsonb_typeof(e->'g') = 'string'
        and length(trim(e->>'g')) > 0
        and jsonb_typeof(e->'p') = 'object'
        and pg_column_size(e->'p') <= 8192
    ), capped as (
      select g, p, k from (
        select distinct on (g) g, p, k from parsed order by g
      ) deduped
      limit 10
    ), accepted as (
      select c.g, c.p, c.k from capped c
      where exists (select 1 from public.telemetry_game_settings gs
                     where gs.anon_id = v_id and gs.game = c.g)
         or (select count(*) from public.telemetry_game_settings gs
              where gs.anon_id = v_id) < 50
    ), ins as (
      insert into public.telemetry_game_settings as s (anon_id, game, preset, updated_at)
      select v_id, g, p, now() from accepted
      on conflict (anon_id, game) do update
        set preset = excluded.preset, updated_at = now()
    )
    select coalesce(jsonb_agg(k), '[]'::jsonb) into v_presets from accepted;
  end if;

  return jsonb_build_object('ok', true, 'settings', v_settings,
                            'games', v_games, 'presets', v_presets);
end;
$$;

-- ---- 4) grants, on the nine-type signature ----
--
-- Same trap as 0129: Supabase default privileges hand EXECUTE to anon and
-- authenticated by name at creation time, so the revoke from public alone
-- would not be enough to remove them. Here both roles are meant to call it,
-- so the explicit grant states the decision.

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text) to anon, authenticated;

-- ---- 5) reading view: aggregate only, never per-install ----
--
-- One row per (signal, lang), where signal is 'ui' or 'fmt'. Built as:
--
--   window_installs : distinct anon_id in public.telemetry with day >= today - 30.
--   latest          : per signal, NULLS FILTERED FIRST, then distinct on
--                     (anon_id) the latest row ordered by day desc, updated_at
--                     desc. Filtering before distinct-on matters: an install
--                     whose newest row carries null (a bad day, a re-ping that
--                     failed the gate) still counts under its last good value
--                     rather than dropping out of coverage.
--   buckets         : any (signal, lang) bucket with fewer than 5 installs is
--                     rolled into lang = 'other'. Minimum cohort: a single
--                     Korean install beside wheel model and games played is a
--                     nameable person. 'other' itself may hold fewer than 5;
--                     that is fine, because it names no language.
--   unknown         : one row per signal for the installs in the window that
--                     never reported that signal (installs_seen - covered).
--                     This is the update-adoption curve.
--
-- Columns:
--   installs        installs whose latest non-null value is this lang
--   installs_7d     of those, how many have their latest day >= today - 7
--   covered         installs with a non-null value for this signal
--   installs_seen   the whole fleet in the 30-day window
--   pct_of_covered  100 * installs / covered (null on the unknown row)
--   coverage_pct    100 * covered / installs_seen
--   ci_lo, ci_hi    95 percent Wilson interval on installs / covered, as
--                   percentages rounded to 1 (null on the unknown row)
--   rankable        installs >= 30 and covered >= 150 (false on unknown)
--
-- Wilson (z = 1.96): center = (p + z^2/(2n)) / (1 + z^2/n),
--                    half   = z * sqrt(p(1-p)/n + z^2/(4n^2)) / (1 + z^2/n).
--
-- SECURITY. Same reasoning as 0132, not repeated in full: public.telemetry is
-- deny-all RLS, a plain view runs as its owner (postgres, BYPASSRLS), so
-- security_invoker = true makes it run as the caller, and the explicit revoke
-- below removes the select that Supabase default privileges grant to anon and
-- authenticated at creation time. Owner and service_role only.

drop view if exists public.v_install_language;

create view public.v_install_language
with (security_invoker = true)
as
with window_rows as (
  select anon_id, day, updated_at, ui_lang, fmt_lang
    from public.telemetry
   where day >= current_date - 30
),
per_install as (
  select anon_id, max(day) as last_day
    from window_rows
   group by anon_id
),
fleet as (
  select count(*) as installs_seen from per_install
),
signals as (
  select signal from (values ('ui'), ('fmt')) as s(signal)
),
latest as (
  select 'ui'::text as signal, u.anon_id, u.lang, u.day
    from (
      select distinct on (anon_id) anon_id, ui_lang as lang, day
        from window_rows
       where ui_lang is not null
       order by anon_id, day desc, updated_at desc
    ) u
  union all
  select 'fmt'::text as signal, f.anon_id, f.lang, f.day
    from (
      select distinct on (anon_id) anon_id, fmt_lang as lang, day
        from window_rows
       where fmt_lang is not null
       order by anon_id, day desc, updated_at desc
    ) f
),
covered as (
  select s.signal, count(l.anon_id) as covered
    from signals s
    left join latest l on l.signal = s.signal
   group by s.signal
),
bucketed as (
  select signal,
         case when count(*) over (partition by signal, lang) < 5
              then 'other' else lang end as lang,
         anon_id,
         day
    from latest
),
buckets as (
  select signal, lang,
         count(*)                                          as installs,
         count(*) filter (where day >= current_date - 7)   as installs_7d
    from bucketed
   group by signal, lang
),
scored as (
  select b.signal, b.lang, b.installs, b.installs_7d, c.covered, f.installs_seen,
         round(100.0 * b.installs / c.covered, 1)                  as pct_of_covered,
         round(100.0 * c.covered / nullif(f.installs_seen, 0), 1)  as coverage_pct,
         w.ci_lo,
         w.ci_hi,
         (b.installs >= 30 and c.covered >= 150)                    as rankable
    from buckets b
    join covered c on c.signal = b.signal
    cross join fleet f
    cross join lateral (
      select round((100 * (center - half))::numeric, 1) as ci_lo,
             round((100 * (center + half))::numeric, 1) as ci_hi
        from (
          select (p + z * z / (2 * n)) / (1 + z * z / n)                          as center,
                 z * sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n) as half
            from (
              select b.installs::double precision / c.covered::double precision as p,
                     c.covered::double precision                                 as n,
                     1.96::double precision                                      as z
            ) v
        ) w0
    ) w
),
unknown as (
  select s.signal,
         'unknown'::text                                              as lang,
         f.installs_seen - c.covered                                  as installs,
         count(p.anon_id) filter (where l.anon_id is null
                                    and p.last_day >= current_date - 7) as installs_7d,
         c.covered,
         f.installs_seen,
         null::numeric                                                as pct_of_covered,
         round(100.0 * c.covered / nullif(f.installs_seen, 0), 1)     as coverage_pct,
         null::numeric                                                as ci_lo,
         null::numeric                                                as ci_hi,
         false                                                        as rankable
    from signals s
    join covered c on c.signal = s.signal
    cross join fleet f
    left join per_install p on true
    left join latest l on l.signal = s.signal and l.anon_id = p.anon_id
   group by s.signal, c.covered, f.installs_seen
)
select signal, lang, installs, installs_7d, covered, installs_seen,
       pct_of_covered, coverage_pct, ci_lo, ci_hi, rankable
  from scored
union all
select signal, lang, installs, installs_7d, covered, installs_seen,
       pct_of_covered, coverage_pct, ci_lo, ci_hi, rankable
  from unknown
order by signal, installs desc;

comment on view public.v_install_language is
  'Owner and service_role only. Per (signal, lang) where signal is ui (Windows display language) or fmt (Windows regional-format language), over installs seen in the last 30 days: installs, installs_7d, covered, installs_seen, pct_of_covered, coverage_pct, a 95 percent Wilson interval (ci_lo, ci_hi) and rankable = installs >= 30 and covered >= 150. Buckets under 5 installs are rolled into other. Read coverage_pct before anything else. Do not compare two non-rankable rows. The unknown row is the update-adoption curve and should fall week over week. Everyone counted already cleared an English installer, an English GitHub page and 28 English guides, so every non-English share is a floor and the floors are not equally deep. Never join this back to per-install rows. security_invoker = true; never grant to anon or authenticated.';

-- Load-bearing, per 0132: Supabase default privileges grant new relations to
-- anon and authenticated at creation time. Without this line the view ships
-- readable through the anon key that lives in the plugin binary.
revoke all on table public.v_install_language from public, anon, authenticated;

-- service_role already holds this via those same defaults; stated so the
-- intended reader survives any future tightening.
grant select on table public.v_install_language to service_role;

-- ---- Verify after applying ----
--
-- (a) The one-row check. Expect exactly ONE row, nine arguments with their
--     defaults, ending in "p_ui_lang text DEFAULT NULL::text, p_fmt_lang text
--     DEFAULT NULL::text", result "jsonb", and a proacl carrying anon=X and
--     authenticated=X. pg_get_function_arguments keeps the defaults; the
--     _identity_ variant strips them and would never show the expected text.
--
--     select pg_get_function_arguments(p.oid),
--            pg_get_function_result(p.oid),
--            p.proacl
--       from pg_proc p
--       join pg_namespace n on n.oid = p.pronamespace
--      where n.nspname = 'public' and p.proname = 'telemetry_ping';
--
-- (b) Run that same query BEFORE applying and record what is live. If it
--     returns two rows (a five-argument and a seven-argument form) the drop
--     in 0128 never ran and this file's first drop is doing real work; if it
--     returns one seven-argument row, 0131 is the live state. Either way the
--     expected result after applying is the same one row.
--
--     Also confirm the columns and the view exist. The first query returns
--     two rows; the second returns only the two 'unknown' rows until the
--     new build ships:
--
--     select column_name from information_schema.columns
--      where table_schema = 'public' and table_name = 'telemetry'
--        and column_name in ('ui_lang', 'fmt_lang');
--     select * from public.v_install_language;
--
-- (c) Two smoke tests, run BEFORE the plugin build that sends the new keys
--     ships. The pg_proc query proves the function exists; only a real RPC
--     call proves PostgREST's schema cache picked it up, and PGRST202 on a
--     nine-key body is exactly the failure this migration exists to prevent.
--     <url> is the project URL and <anon> the anon key. Each command is one
--     line; on Windows PowerShell call curl.exe, not the curl alias. Both
--     must return {"ok":true,...}.
--
--     The exact v0.4.0 seven-key body (the six optional keys as JSON null):
--
--     curl.exe -s -X POST "<url>/rest/v1/rpc/telemetry_ping" -H "apikey: <anon>" -H "Authorization: Bearer <anon>" -H "Content-Type: application/json" -d "{\"p_anon_id\":\"smoke-test-7\",\"p_plugin_version\":null,\"p_wheel\":null,\"p_game\":null,\"p_settings\":null,\"p_games\":null,\"p_game_presets\":null}"
--
--     The nine-key body the next build sends:
--
--     curl.exe -s -X POST "<url>/rest/v1/rpc/telemetry_ping" -H "apikey: <anon>" -H "Authorization: Bearer <anon>" -H "Content-Type: application/json" -d "{\"p_anon_id\":\"smoke-test-9\",\"p_plugin_version\":null,\"p_wheel\":null,\"p_game\":null,\"p_settings\":null,\"p_games\":null,\"p_game_presets\":null,\"p_ui_lang\":\"de\",\"p_fmt_lang\":\"de\"}"
--
--     Then check the second one landed and remove both:
--
--     select anon_id, ui_lang, fmt_lang from public.telemetry where anon_id like 'smoke-test-%';
--     delete from public.telemetry         where anon_id like 'smoke-test-%';
--     delete from public.telemetry_devices where anon_id like 'smoke-test-%';  -- in case the 04:25 rollup ran in between
--
--     Only after both smoke tests pass does the plugin build ship.
