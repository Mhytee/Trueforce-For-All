-- Hardening for 0127: cheaper size guard + revoke default table grants.
create or replace function public.telemetry_ping(
  p_anon_id        text,
  p_plugin_version text default null,
  p_wheel          text default null,
  p_game           text default null,
  p_settings       jsonb default null
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
end;
$$;

revoke all on function public.telemetry_ping(text, text, text, text, jsonb) from public;
grant execute on function public.telemetry_ping(text, text, text, text, jsonb) to anon, authenticated;

revoke all on table public.telemetry from anon, authenticated;