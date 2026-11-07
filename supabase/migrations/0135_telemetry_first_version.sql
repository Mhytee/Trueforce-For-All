-- The version each INSTALL started on, so "how many of these are new users" is
-- a measurement instead of an inference.
--
-- Why. AnalyticsAnonId is minted per install and only exists from 0.4.0, so
-- every 0.4.0 install looked brand new to the backend whether it was a first
-- install or an upgrade from 0.3.0. On 2026-09-26, with 110 active installs
-- against 116 downloads, the best available answer was a proxy: 47 of them
-- arrived with a ships-false setting already true on their very first ping,
-- which bounds returning users at 43% and no tighter. An earlier proxy (a
-- non-default MasterGain on the first ping) had to be thrown away entirely
-- once it turned out 43 of its 54 hits were long-decimal values written by
-- auto-calibration rather than by a person.
--
-- The client stamps Settings.FirstInstalledVersion exactly once, at Init,
-- BEFORE the badge-seed block overwrites LastSeenVersion, and never rewrites
-- it. Three shapes, each carrying its own meaning:
--   '0.4.1'     a genuinely fresh install of that build
--   '<=0.4.0'   already present, last ran 0.4.0, so it began at or before that
--   'pre-0.4.1' already present but never stamped a LastSeenVersion either
--
-- Adding a parameter changes the signature, so this replaces rather than
-- alters. The column is filled once and never overwritten: a later ping that
-- omits it cannot erase what is already known.

alter table public.telemetry add column if not exists first_version text;

comment on column public.telemetry.first_version is
  'Version this install started on: "0.4.1" fresh, "<=0.4.0" upgraded from at or before that, "pre-X" origin unknown. Written once, never overwritten.';

drop function if exists public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text);

CREATE OR REPLACE FUNCTION public.telemetry_ping(p_anon_id text, p_plugin_version text DEFAULT NULL::text, p_wheel text DEFAULT NULL::text, p_game text DEFAULT NULL::text, p_settings jsonb DEFAULT NULL::jsonb, p_games jsonb DEFAULT NULL::jsonb, p_game_presets jsonb DEFAULT NULL::jsonb, p_ui_lang text DEFAULT NULL::text, p_fmt_lang text DEFAULT NULL::text, p_first_version text DEFAULT NULL::text)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
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
  -- The install's first-seen version. Three client shapes, all short and all
  -- version-ish: "0.4.1", "<=0.4.0", "pre-0.4.1". Anything else is stored as
  -- null rather than raising, and it is length-clamped like every other text
  -- parameter here.
  v_first    text    := case when p_first_version ~ '^(<=|pre-)?[0-9]{1,3}(\.[0-9]{1,4}){0,3}$'
                             then left(p_first_version, 24) end;
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
      (anon_id, day, plugin_version, wheel, game, settings, ui_lang, fmt_lang, first_version)
  values (v_id, current_date,
          left(p_plugin_version, 32), left(p_wheel, 60), left(p_game, 60), p_settings,
          v_ui, v_fmt, v_first)
  on conflict (anon_id, day) do update
    set plugin_version = excluded.plugin_version,
        wheel          = excluded.wheel,
        game           = coalesce(excluded.game, t.game),
        settings       = coalesce(excluded.settings, t.settings),
        ui_lang        = coalesce(excluded.ui_lang, t.ui_lang),
        fmt_lang       = coalesce(excluded.fmt_lang, t.fmt_lang),
        -- Never overwritten once known: it describes the install's origin, so a
        -- later ping that somehow omits it must not erase it.
        first_version  = coalesce(t.first_version, excluded.first_version),
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
$function$;

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text, text) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb, text, text, text) to anon, authenticated;
