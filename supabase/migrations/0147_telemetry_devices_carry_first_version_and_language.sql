-- 0147: first_version and language survive the 90 day prune.
--
-- Migration 0135 added telemetry.first_version and the language pair landed with
-- the localization work, but all three live ONLY on the raw table, and the rollup
-- deletes raw rows past p_retain_days (90). telemetry_devices is the durable
-- per-install record and had no columns for them, so the moment a row ages out its
-- install's first version and language are gone for good.
--
-- That matters more than it sounds. first_version is the only thing separating a
-- new user from an upgrader, and it is per install and never resent for an install
-- that stops pinging. A cohort question asked in February ("what did the installs
-- we picked up in September start on") would have had no answer at all, and no way
-- to recover one, because the evidence had been deleted on schedule rather than
-- lost. The columns cost three text fields per install and close that off.
--
-- Fill rules differ per column, on purpose:
--   first_version  EARLIEST non-null wins and is never overwritten, matching the
--                  never-overwrite contract telemetry.first_version already has.
--                  An install's first version cannot change; a later value that
--                  disagrees is wrong, so the older one is kept.
--   ui_lang        LATEST non-null wins. Language is a live preference and a user
--   fmt_lang       who switches should read as switched.
--
-- Every fill is coalesced so a null NEVER overwrites a known value. Without that,
-- one ping from a pre-0.5.0 client (which sends no language at all) would wipe the
-- language of an install that had already reported one, and the next prune would
-- make the wipe permanent.

alter table public.telemetry_devices
  add column if not exists first_version text,
  add column if not exists ui_lang       text,
  add column if not exists fmt_lang      text;

create or replace function public.telemetry_rollup(p_retain_days integer default 90)
returns void
language plpgsql
security definer
set search_path to 'public'
as $function$
begin
  p_retain_days := greatest(coalesce(p_retain_days, 90), 30);

  insert into public.telemetry_devices as d
      (anon_id, first_seen, last_seen, last_version, last_wheel,
       first_version, ui_lang, fmt_lang, updated_at)
  select t.anon_id, min(t.day), max(t.day),
         (array_agg(t.plugin_version order by t.day desc, t.updated_at desc))[1],
         (array_agg(t.wheel          order by t.day desc, t.updated_at desc))[1],
         -- earliest non-null: an install's first version never changes
         (array_agg(t.first_version order by t.day asc, t.updated_at asc)
            filter (where t.first_version is not null))[1],
         -- latest non-null: language is a live preference
         (array_agg(t.ui_lang  order by t.day desc, t.updated_at desc)
            filter (where t.ui_lang is not null))[1],
         (array_agg(t.fmt_lang order by t.day desc, t.updated_at desc)
            filter (where t.fmt_lang is not null))[1],
         now()
  from public.telemetry t
  group by t.anon_id
  on conflict (anon_id) do update
    set first_seen    = least(d.first_seen, excluded.first_seen),
        last_seen     = greatest(d.last_seen, excluded.last_seen),
        last_version  = excluded.last_version,
        last_wheel    = excluded.last_wheel,
        -- never overwrite a first version we already hold
        first_version = coalesce(d.first_version, excluded.first_version),
        -- a client that sends no language must not erase one we have
        ui_lang       = coalesce(excluded.ui_lang,  d.ui_lang),
        fmt_lang      = coalesce(excluded.fmt_lang, d.fmt_lang),
        updated_at    = now();

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

  insert into public.telemetry_daily (day, active)
  select day, count(distinct anon_id)
  from public.telemetry
  where day < current_date
    and day >= current_date - p_retain_days
  group by day
  on conflict (day) do update set active = excluded.active;

  insert into public.telemetry_game_daily (game, day, active)
  select game, day, count(distinct anon_id)
  from public.telemetry_game_days
  where day < current_date
    and day >= current_date - p_retain_days
  group by game, day
  on conflict (game, day) do update set active = excluded.active;

  delete from public.telemetry           where day < current_date - p_retain_days;
  delete from public.telemetry_game_days where day < current_date - p_retain_days;

  delete from public.telemetry_game_settings s
  where not exists (
    select 1 from public.telemetry_devices d
    where d.anon_id = s.anon_id and d.last_seen > current_date - 365);
end;
$function$;

-- Supabase's default ACL grants EXECUTE to anon/authenticated at CREATE time, and
-- "revoke from public" does not remove it. telemetry_rollup both prunes and deletes,
-- so it must never be anon-callable (it briefly was, which is why this is explicit).
revoke all on function public.telemetry_rollup(integer) from public, anon, authenticated;

-- Backfill from the raw rows still inside the window, using the same rules as above.
update public.telemetry_devices d
   set first_version = coalesce(d.first_version, s.first_version),
       ui_lang       = coalesce(s.ui_lang,  d.ui_lang),
       fmt_lang      = coalesce(s.fmt_lang, d.fmt_lang),
       updated_at    = now()
from (
  select t.anon_id,
    (array_agg(t.first_version order by t.day asc, t.updated_at asc)
       filter (where t.first_version is not null))[1] as first_version,
    (array_agg(t.ui_lang  order by t.day desc, t.updated_at desc)
       filter (where t.ui_lang is not null))[1]       as ui_lang,
    (array_agg(t.fmt_lang order by t.day desc, t.updated_at desc)
       filter (where t.fmt_lang is not null))[1]      as fmt_lang
  from public.telemetry t
  group by t.anon_id
) s
where s.anon_id = d.anon_id
  and (s.first_version is not null or s.ui_lang is not null or s.fmt_lang is not null);
