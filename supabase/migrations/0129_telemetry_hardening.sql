-- Telemetry hardening. Fixes found by the adversarial review of 0128.
--
-- SECURITY (critical): telemetry_rollup was EXECUTE-able by anon/authenticated.
-- Supabase's default ACL on schema public grants EXECUTE to anon+authenticated
-- at creation time, so "revoke ... from public" (0128) did NOT remove it, and
-- the anon key ships inside the plugin. Any user could POST
-- /rest/v1/rpc/telemetry_rollup with p_retain_days = -1 and delete every raw
-- telemetry row (including today's, which the aggregates had not captured).
-- Every other maintenance function in this schema revokes from public, anon,
-- authenticated; telemetry_rollup was the sole outlier.
--
-- CORRECTNESS: the aggregate fills froze a day's count on first sight
-- (on conflict do nothing), but a game played on UTC day D is only delivered by
-- the client on D+1's ping, which for most timezones lands AFTER the 04:25 UTC
-- rollup. Per-game DAU was therefore permanently undercounted and skewed by
-- region. The fills now RECOMPUTE, but only for days whose raw rows still exist
-- (>= current_date - p_retain_days); older days keep their last computed value
-- because they are no longer in the select. Late arrivals raise the count, and
-- deleting junk rows lowers it again, which do-nothing made impossible.
--
-- ROBUSTNESS: an impossible-but-regex-valid date (2026-02-30) raised SQLSTATE
-- 22008 out of the ::date cast and rolled back the WHOLE ping, losing the base
-- row and the settings snapshot. Dates now go through a non-throwing helper.
-- Game names are type-checked (a jsonb object rendered via ->> stored game
-- names like '{"x": 1}'), and the per-call preset budget drops from 50 x 16 KB
-- (782 KB permanent per unauthenticated call) to 10 x 4 KB, with a per-install
-- cap on distinct games and a stale-install prune.

-- ---- 1) CRITICAL: close the destructive grant ----
revoke all on function public.telemetry_rollup(int) from public, anon, authenticated;

-- ---- 2) non-throwing date parse ----
create or replace function public._telemetry_safe_date(p text)
returns date
language plpgsql
immutable
as $$
begin
  return p::date;
exception when others then
  return null;
end;
$$;
revoke all on function public._telemetry_safe_date(text) from public, anon, authenticated;

-- ---- 3) telemetry_ping: safe dates, typed game names, smaller budgets ----
create or replace function public.telemetry_ping(
  p_anon_id        text,
  p_plugin_version text  default null,
  p_wheel          text  default null,
  p_game           text  default null,
  p_settings       jsonb default null,
  p_games          jsonb default null,
  p_game_presets   jsonb default null
) returns void
language plpgsql
security definer
set search_path = public
as $$
declare
  v_id text := left(coalesce(nullif(trim(p_anon_id), ''), ''), 64);
begin
  if v_id = '' then return; end if;
  if p_settings is not null and pg_column_size(p_settings) > 8192 then
    p_settings := null;
  end if;

  insert into public.telemetry as t (anon_id, day, plugin_version, wheel, game, settings)
  values (v_id, current_date,
          left(p_plugin_version, 32), left(p_wheel, 60), left(p_game, 60), p_settings)
  on conflict (anon_id, day) do update
    set plugin_version = excluded.plugin_version,
        wheel          = excluded.wheel,
        game           = coalesce(excluded.game, t.game),
        settings       = coalesce(excluded.settings, t.settings),
        updated_at     = now();

  -- Per-game daily activity. Entries are {g: <string game>, d: yyyy-MM-dd}.
  -- The date goes through _telemetry_safe_date so an impossible date yields
  -- null and is filtered instead of aborting the whole ping. An oversized array
  -- costs its tail (limit), never the whole batch.
  if p_games is not null and jsonb_typeof(p_games) = 'array' then
    insert into public.telemetry_game_days (anon_id, game, day)
    select v_id, g, d
    from (
      select left(trim(e->>'g'), 60) as g, public._telemetry_safe_date(e->>'d') as d
      from jsonb_array_elements(p_games) e
      where jsonb_typeof(e->'g') = 'string'
        and length(trim(e->>'g')) > 0
      limit 200
    ) v
    where v.d is not null
      and v.d between current_date - 60 and current_date
    on conflict (anon_id, game, day) do nothing;
  end if;

  -- Per-game default-preset bodies (sent only when changed client-side, identity
  -- stripped client-side). Budget: 10 entries x 4 KB per call, and at most 50
  -- distinct games retained per install, so an unauthenticated caller cannot use
  -- this as unbounded permanent storage.
  if p_game_presets is not null and jsonb_typeof(p_game_presets) = 'array' then
    insert into public.telemetry_game_settings as s (anon_id, game, preset, updated_at)
    select v_id, g, p, now()
    from (
      select left(trim(e->>'g'), 60) as g, e->'p' as p
      from jsonb_array_elements(p_game_presets) e
      where jsonb_typeof(e->'g') = 'string'
        and length(trim(e->>'g')) > 0
        and jsonb_typeof(e->'p') = 'object'
        and pg_column_size(e->'p') <= 4096
      limit 10
    ) v
    where (select count(*) from public.telemetry_game_settings gs where gs.anon_id = v_id) < 50
    on conflict (anon_id, game) do update
      set preset = excluded.preset, updated_at = now();
  end if;
end;
$$;

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) to anon, authenticated;

-- ---- 4) rollup: clamped window, recompute-in-window, prune the latest-preset store ----
create or replace function public.telemetry_rollup(p_retain_days int default 90)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
  -- Clamp: no caller-supplied value can shorten the window below a floor, so a
  -- negative argument can never delete rows the aggregates have not captured.
  p_retain_days := greatest(coalesce(p_retain_days, 90), 30);

  -- 1) Device registry: first / last seen + latest version & wheel.
  insert into public.telemetry_devices as d
      (anon_id, first_seen, last_seen, last_version, last_wheel, updated_at)
  select t.anon_id, min(t.day), max(t.day),
         (array_agg(t.plugin_version order by t.day desc, t.updated_at desc))[1],
         (array_agg(t.wheel          order by t.day desc, t.updated_at desc))[1],
         now()
  from public.telemetry t
  group by t.anon_id
  on conflict (anon_id) do update
    set first_seen   = least(d.first_seen, excluded.first_seen),
        last_seen    = greatest(d.last_seen, excluded.last_seen),
        last_version = excluded.last_version,
        last_wheel   = excluded.last_wheel,
        updated_at   = now();

  -- 2) Latest NON-NULL settings snapshot per device (advance only).
  update public.telemetry_devices d
     set last_settings = s.settings, last_settings_day = s.day
  from (
    select distinct on (anon_id) anon_id, settings, day
    from public.telemetry
    where settings is not null
    order by anon_id, day desc, updated_at desc
  ) s
  where s.anon_id = d.anon_id
    and (d.last_settings_day is null or s.day >= d.last_settings_day);

  -- 3) DAU for complete days, RECOMPUTED while the raw rows still exist. Days
  --    older than the window are not selected, so their finalized value stands.
  insert into public.telemetry_daily (day, active)
  select day, count(distinct anon_id)
  from public.telemetry
  where day < current_date
    and day >= current_date - p_retain_days
  group by day
  on conflict (day) do update set active = excluded.active;

  -- 4) Per-game DAU, same discipline. This is the fix for the undercount: a
  --    day's game rows arrive on the NEXT day's ping, after the first rollup.
  insert into public.telemetry_game_daily (game, day, active)
  select game, day, count(distinct anon_id)
  from public.telemetry_game_days
  where day < current_date
    and day >= current_date - p_retain_days
  group by game, day
  on conflict (game, day) do update set active = excluded.active;

  -- 5) Prune raw detail past the window (already rolled up above).
  delete from public.telemetry           where day < current_date - p_retain_days;
  delete from public.telemetry_game_days where day < current_date - p_retain_days;

  -- 6) telemetry_game_settings is a "latest" store, not a daily one, so it needs
  --    its own bound: drop presets for installs not seen in a year.
  delete from public.telemetry_game_settings s
  where not exists (
    select 1 from public.telemetry_devices d
    where d.anon_id = s.anon_id and d.last_seen > current_date - 365);
end;
$$;

revoke all on function public.telemetry_rollup(int) from public, anon, authenticated;
