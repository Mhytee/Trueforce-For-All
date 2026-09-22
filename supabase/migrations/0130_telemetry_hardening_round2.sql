-- Round-2 hardening: fixes found reviewing the 0129 fixes themselves.
--
-- 1) _telemetry_safe_date was labelled IMMUTABLE but text->date depends on the
--    DateStyle GUC ('03-04-2026' parses differently under DMY vs MDY), and an
--    IMMUTABLE label lets Postgres constant-fold it or accept it in an index.
--    It is STABLE. 0129 also dropped 0128's ISO regex, which had made the
--    DateStyle dependence unreachable; restored below in the ping filter.
-- 2) The 50-games-per-install cap was a whole-statement WHERE, so it also
--    blocked ON CONFLICT DO UPDATE: an install past 50 games could never
--    refresh ANY preset body again, while the client (which commits hashes on
--    a 2xx) recorded them as delivered. Now per-row: updates to a game already
--    stored always pass; only NEW games face the cap.
-- 3) In p_games the LIMIT ran BEFORE the date filter, so 200 junk-dated entries
--    could crowd out valid ones. Filter first, then limit.
-- 4) 0129 replaced 0128's whole-array length guards with inner LIMITs and so
--    lost any O(1) bound on the parameter itself. Cheap outer caps restored,
--    generous enough to keep the graceful-truncation behaviour.

create or replace function public._telemetry_safe_date(p text)
returns date
language plpgsql
stable
as $$
begin
  return p::date;
exception when others then
  return null;
end;
$$;
revoke all on function public._telemetry_safe_date(text) from public, anon, authenticated;

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

  -- {g: <string>, d: yyyy-MM-dd}. ISO-only (restores 0128's input contract, and
  -- keeps the DateStyle dependence of the cast unreachable), parsed through the
  -- non-throwing helper so an impossible-but-ISO date cannot abort the ping.
  -- Valid rows are selected BEFORE the limit, so junk cannot crowd them out.
  if p_games is not null and jsonb_typeof(p_games) = 'array'
     and jsonb_array_length(p_games) <= 400 then
    insert into public.telemetry_game_days (anon_id, game, day)
    select v_id, g, d from (
      select g, d from (
        select left(trim(e->>'g'), 60) as g,
               public._telemetry_safe_date(e->>'d') as d
        from jsonb_array_elements(p_games) e
        where jsonb_typeof(e->'g') = 'string'
          and length(trim(e->>'g')) > 0
          and e->>'d' ~ '^\d{4}-\d{2}-\d{2}$'
      ) parsed
      where parsed.d is not null
        and parsed.d between current_date - 60 and current_date
      limit 200
    ) v
    on conflict (anon_id, game, day) do nothing;
  end if;

  -- {g: <string>, p: <object>}. The cap admits only NEW games; a game already
  -- stored can always be refreshed, so an install past the cap does not go
  -- permanently stale.
  if p_game_presets is not null and jsonb_typeof(p_game_presets) = 'array'
     and jsonb_array_length(p_game_presets) <= 20 then
    insert into public.telemetry_game_settings as s (anon_id, game, preset, updated_at)
    select v_id, g, p, now()
    from (
      select left(trim(e->>'g'), 60) as g, e->'p' as p
      from jsonb_array_elements(p_game_presets) e
      where jsonb_typeof(e->'g') = 'string'
        and length(trim(e->>'g')) > 0
        and jsonb_typeof(e->'p') = 'object'
        and pg_column_size(e->'p') <= 8192
      limit 10
    ) v
    where exists (select 1 from public.telemetry_game_settings gs
                   where gs.anon_id = v_id and gs.game = v.g)
       or (select count(*) from public.telemetry_game_settings gs
            where gs.anon_id = v_id) < 50
    on conflict (anon_id, game) do update
      set preset = excluded.preset, updated_at = now();
  end if;
end;
$$;

revoke all on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb, jsonb, jsonb) to anon, authenticated;
