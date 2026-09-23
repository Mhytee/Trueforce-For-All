-- Anonymous usage statistics.
--
-- One row per (anon_id, day). The anon_id is a random GUID minted by the
-- plugin (NOT derived from hardware or identity, mirroring CarFactsAnonId),
-- so rows count unique installs / DAU / MAU and hold a settings snapshot
-- without any account link. No IP is stored on these rows. Writes go only
-- through the SECURITY DEFINER telemetry_ping RPC; the table is not readable
-- through PostgREST (RLS forced, no policies). The (anon_id, day) primary
-- key caps one row per id per day; the RPC bounds the payload size.

create table if not exists public.telemetry (
  anon_id        text        not null,
  day            date        not null,
  plugin_version text,
  wheel          text,
  game           text,
  settings       jsonb,
  first_seen     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  primary key (anon_id, day)
);

create index if not exists telemetry_day_idx on public.telemetry (day);

alter table public.telemetry enable row level security;
alter table public.telemetry force row level security;
-- No policies: only the definer RPC below writes; nothing reads via PostgREST.

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
  if v_id = '' then return; end if;                       -- no id, no row
  -- Bound the settings payload defensively (abuse / bloat guard). pg_column_size
  -- is a cheap size proxy, so an oversized payload is not materialized to text
  -- just to measure it.
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

-- Belt-and-suspenders: the SECURITY DEFINER RPC (owned by postgres, bypasses
-- table grants) is the only intended read/write path. Revoke the default table
-- privileges so a future stray policy or a force-RLS change can never hand
-- anon / authenticated direct read or write to the raw rows.
revoke all on table public.telemetry from anon, authenticated;

-- Rate-limiting note: anon_id is caller-supplied, so (anon_id, day) does not cap
-- rows per caller and the unique / DAU / MAU counts are forgeable. A per-IP daily
-- cap (a transient hashed IP kept OFF these rows) was DELIBERATELY DEFERRED for
-- v1 to keep telemetry fully IP-free; edge limits + the Spend Cap bound bloat.
-- Revisit only if the counts look inflated or the table grows oddly.
