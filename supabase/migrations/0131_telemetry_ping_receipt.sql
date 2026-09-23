-- Round-3 hardening: telemetry_ping returns a RECEIPT instead of void.
--
-- Why. The ping is fire-and-forget and the client commits its local "already
-- sent" state (day stamp, settings hash, per-game preset hashes, game-day
-- queue drain) on a 2xx. Every filter in this function, though, drops its input
-- SILENTLY and still returns 2xx: an oversized settings snapshot becomes NULL,
-- an oversized preset body is skipped, an out-of-window date is skipped, a new
-- game past the 50-game cap is skipped. The client therefore recorded discarded
-- payloads as delivered, and because the "has it changed" test is a hash
-- compare, it then never resent them: the loss was permanent for that state,
-- not just for that ping. Measured headroom at the time of writing was 4912 of
-- 8192 bytes for a real settings snapshot, on the fastest-growing surface in
-- the product, so the cliff was reachable and would have been invisible from
-- both sides.
--
-- The fix is to say what was actually stored. The receipt echoes the client's
-- OWN keys verbatim ("<game>|<yyyy-MM-dd>" for game-days, the raw game name for
-- preset bodies) so the client can commit exactly those and leave the rest
-- queued for the next ping:
--   {"ok":true,"settings":true,"games":["iRacing|2026-09-21"],"presets":["iRacing"]}
-- A client that ignores the body keeps working; it just keeps the old
-- commit-everything-on-2xx behaviour.
--
-- Also fixed here: a duplicate game in p_game_presets raised SQLSTATE 21000
-- ("ON CONFLICT DO UPDATE command cannot affect row a second time") and, since
-- the whole function is one transaction, took the base row, the settings
-- snapshot and every game-day row down with it. De-duplicated before the
-- insert, which is the last whole-ping abort path.
--
-- Return type changes, so this drops and recreates rather than replaces.

drop function if exists public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb);

create function public.telemetry_ping(
  p_anon_id        text,
  p_plugin_version text  default null,
  p_wheel          text  default null,
  p_game           text  default null,
  p_settings       jsonb default null,
  p_games          jsonb default null,
  p_game_presets   jsonb default null
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
begin
  if v_id = '' then
    return jsonb_build_object('ok', false, 'settings', false,
                              'games', v_games, 'presets', v_presets);
  end if;

  if p_settings is not null and pg_column_size(p_settings) > 8192 then
    p_settings := null;          -- too large to store; the receipt says so
  end if;
  v_settings := p_settings is not null;

  insert into public.telemetry as t (anon_id, day, plugin_version, wheel, game, settings)
  values (v_id, current_date,
          left(p_plugin_version, 32), left(p_wheel, 60), left(p_game, 60), p_settings)
  on conflict (anon_id, day) do update
    set plugin_version = excluded.plugin_version,
        wheel          = excluded.wheel,
        game           = coalesce(excluded.game, t.game),
        settings       = coalesce(excluded.settings, t.settings),
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

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) to anon, authenticated;
