-- Telemetry retention + rollup, plus per-game daily activity.
--
-- Durable aggregates (all survive raw-row pruning):
--   telemetry_devices     : one row per unique install. All-time unique count,
--                           current version / wheel, and the latest settings
--                           snapshot for the settings-distribution view.
--   telemetry_daily       : one row per day, the distinct-install (DAU) count.
--   telemetry_game_daily  : one row per (game, day), distinct installs that
--                           played that game (per-game DAU over time).
--
-- Raw detail tables (pruned past the retention window by telemetry_rollup):
--   telemetry             : one row per (install, day) with the settings snapshot.
--   telemetry_game_days   : one row per (install, game, day) it was played.
--
-- Per-game activity is accumulated client-side (games played since the last
-- ping, each stamped with the day it happened) and drained into the once-a-day
-- ping via telemetry_ping's p_games, so we learn per-game daily activity a day
-- late without extra pings. The client's LastTelemetrySettingsHash likewise
-- stops re-sending an unchanged settings snapshot, so most raw telemetry rows
-- are tiny.

-- ---- durable + raw tables ----

create table if not exists public.telemetry_devices (
  anon_id           text primary key,
  first_seen        date not null,
  last_seen         date not null,
  last_version      text,
  last_wheel        text,
  last_settings     jsonb,
  last_settings_day date,
  updated_at        timestamptz not null default now()
);

create table if not exists public.telemetry_daily (
  day    date primary key,
  active integer not null
);

create table if not exists public.telemetry_game_days (
  anon_id    text not null,
  game       text not null,
  day        date not null,
  first_seen timestamptz not null default now(),
  primary key (anon_id, game, day)
);
create index if not exists telemetry_game_days_day_idx on public.telemetry_game_days (day);

create table if not exists public.telemetry_game_daily (
  game   text not null,
  day    date not null,
  active integer not null,
  primary key (game, day)
);

-- Latest default-preset body per (install, game). Sent only when it changed
-- (client-side hash), identity fields stripped client-side. Bounded by
-- installs x games; never pruned (it is a "latest" table, not raw daily).
create table if not exists public.telemetry_game_settings (
  anon_id    text not null,
  game       text not null,
  preset     jsonb,
  updated_at timestamptz not null default now(),
  primary key (anon_id, game)
);

do $$
declare r text;
begin
  foreach r in array array['telemetry_devices','telemetry_daily','telemetry_game_days','telemetry_game_daily','telemetry_game_settings']
  loop
    execute format('alter table public.%I enable row level security', r);
    execute format('alter table public.%I force row level security', r);
    execute format('revoke all on table public.%I from anon, authenticated', r);
  end loop;
end $$;

-- ---- telemetry_ping: add p_games (per-game-day activity) and p_game_presets ----
-- Drop the 5-arg form (0127) and create the 7-arg form with the two new params
-- defaulted, so an older client that sends only the first five keys still
-- routes here (p_games and p_game_presets default to null).

drop function if exists public.telemetry_ping(text, text, text, text, jsonb);

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

  -- Per-game daily activity. Bounded (<=200 entries) and validated: each entry
  -- is {g: game, d: yyyy-MM-dd}, the day within a sane 60-day-past .. today
  -- window (rejects garbage and stale/abuse dates before the ::date cast).
  if p_games is not null and jsonb_typeof(p_games) = 'array'
     and jsonb_array_length(p_games) between 1 and 200 then
    insert into public.telemetry_game_days (anon_id, game, day)
    select v_id, left(e->>'g', 60), (e->>'d')::date
    from jsonb_array_elements(p_games) e
    where e->>'g' is not null
      and e->>'d' ~ '^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$'
      and (e->>'d')::date between current_date - 60 and current_date
    on conflict (anon_id, game, day) do nothing;
  end if;

  -- Per-game default-preset bodies, sent only when changed client-side, stored
  -- as the latest per (install, game). Bounded: <= 50 entries, each an object of
  -- <= 16 KB. Identity fields are stripped client-side before sending.
  if p_game_presets is not null and jsonb_typeof(p_game_presets) = 'array'
     and jsonb_array_length(p_game_presets) between 1 and 50 then
    insert into public.telemetry_game_settings as s (anon_id, game, preset, updated_at)
    select v_id, left(e->>'g', 60), e->'p', now()
    from jsonb_array_elements(p_game_presets) e
    where e->>'g' is not null
      and jsonb_typeof(e->'p') = 'object'
      and pg_column_size(e->'p') <= 16384
    on conflict (anon_id, game) do update
      set preset = excluded.preset, updated_at = now();
  end if;
end;
$$;

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) to anon, authenticated;

-- ---- rollup + retention ----

create or replace function public.telemetry_rollup(p_retain_days int default 90)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
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

  -- 2) Latest NON-NULL settings snapshot per device (only advance, never
  --    overwrite a captured snapshot with an older one).
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

  -- 3) Finalize DAU for COMPLETE days; do-nothing on conflict keeps the count
  --    captured while the raw rows still existed.
  insert into public.telemetry_daily (day, active)
  select day, count(distinct anon_id)
  from public.telemetry
  where day < current_date
  group by day
  on conflict (day) do nothing;

  -- 4) Finalize per-game DAU for COMPLETE days, same discipline.
  insert into public.telemetry_game_daily (game, day, active)
  select game, day, count(distinct anon_id)
  from public.telemetry_game_days
  where day < current_date
  group by game, day
  on conflict (game, day) do nothing;

  -- 5) Prune raw detail past the retention window (already rolled up above,
  --    same transaction).
  delete from public.telemetry          where day < current_date - p_retain_days;
  delete from public.telemetry_game_days where day < current_date - p_retain_days;
end;
$$;

revoke all on function public.telemetry_rollup(int) from public;

-- Daily at 04:25 UTC, clear of the 03:30 backup GC, 04:10 teknoparrot sync and
-- 05:30 role sync. Idempotent: cron.schedule upserts by job name.
select cron.unschedule('tf4all-telemetry-rollup')
where exists (select 1 from cron.job where jobname = 'tf4all-telemetry-rollup');
select cron.schedule('tf4all-telemetry-rollup', '25 4 * * *',
  $job$ select public.telemetry_rollup(); $job$);
