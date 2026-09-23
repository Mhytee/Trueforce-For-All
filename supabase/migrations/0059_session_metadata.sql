-- 0059: per-session device metadata for the Account "Active sessions" list
-- (friendly device name, last-used wheel, plugin version, last-seen) instead of
-- just an IP. auth.sessions.user_agent is immutable per session and can't carry
-- the (changing) wheel, so we keep mutable metadata in our own table keyed by the
-- same session id, upserted by the existing ~3-min plugin heartbeat.
--
-- Same lockdown discipline as 0058: no table grants, RLS forced + deny-all, all
-- access via SECURITY DEFINER RPCs that derive identity from auth.uid() + the JWT
-- session_id claim, never from client args.
--
-- FK session_id -> auth.sessions(id) ON DELETE CASCADE is verified safe on this
-- project: postgres holds REFERENCES on auth.sessions; refresh_tokens and
-- mfa_amr_claims already cascade off it; and an RI cascade DELETE bypasses our
-- forced RLS, so GoTrue's session delete (run by the non-superuser
-- supabase_auth_admin) auto-removes our row and is never blocked by our lockdown.
-- Net effect: revoke / logout / token-expiry GC all auto-clean the metadata row.

create table if not exists public.session_metadata (
  session_id     uuid primary key
                   references auth.sessions(id) on delete cascade,
  -- Denormalised owner (also FK-cascaded) so RLS/queries scope by auth.uid()
  -- without joining back to auth.sessions. Always server-derived, never a client arg.
  user_id        uuid not null references auth.users(id) on delete cascade,
  device_name    text,
  wheel          text,
  plugin_version text,
  -- Server clock, refreshed every heartbeat upsert. Independent of
  -- auth.sessions.refreshed_at (which only moves on token refresh, ~hourly), so a
  -- truer "device is alive now".
  last_seen      timestamptz not null default now(),
  created_at     timestamptz not null default now(),
  constraint session_metadata_device_name_len check (device_name    is null or length(device_name)    <= 80),
  constraint session_metadata_wheel_len        check (wheel          is null or length(wheel)          <= 60),
  constraint session_metadata_plugin_ver_len   check (plugin_version is null or length(plugin_version) <= 32)
);

create index if not exists session_metadata_user_id_idx
  on public.session_metadata(user_id);

-- Deny-all defence-in-depth: RLS on + zero policies blocks every client role even
-- if a grant ever leaked. The RI cascade DELETE still fires past this (verified).
alter table public.session_metadata enable row level security;
alter table public.session_metadata force  row level security;
revoke all on table public.session_metadata from public, anon, authenticated;

-- ---- heartbeat + metadata upsert (NEW 3-arg fn; 0-arg is_my_session_active stays) ----
-- Folds the metadata upsert into the liveness heartbeat: one round-trip that both
-- answers "is my session still alive?" and records this device's name/wheel/version.
-- IDENTICAL fail directions to is_my_session_active() so the verified revoke
-- behaviour is preserved:
--   * no session_id claim   -> return true  (fail OPEN), write nothing
--   * non-uuid claim        -> ::uuid RAISES -> client sees HTTP error -> Unknown
--   * revoked (not live)    -> return false, write NOTHING (never reference a dead session)
--   * live                  -> upsert metadata, return true
-- The 0-arg is_my_session_active() from 0058 is left intact for old clients
-- (PostgREST routes by arg set, so the two coexist as distinct endpoints).
drop function if exists public.session_heartbeat(text, text, text);
create or replace function public.session_heartbeat(
  p_device_name    text,
  p_wheel          text,
  p_plugin_version text)
returns boolean
language plpgsql security definer set search_path = public, pg_temp as $$
declare
  sid     uuid;
  is_live boolean;
begin
  sid := nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '')::uuid;
  if sid is null then
    return true;   -- fail OPEN, exactly like is_my_session_active(); no key to write
  end if;

  is_live := exists(
    select 1 from auth.sessions s
    where s.id = sid and s.user_id = auth.uid());

  if not is_live then
    return false;  -- revoked: signal sign-out, write nothing
  end if;

  -- Live: record metadata. session_id from the claim, user_id from auth.uid();
  -- both server-derived, never client args. left() truncation backstops the CHECKs.
  insert into public.session_metadata
    (session_id, user_id, device_name, wheel, plugin_version, last_seen)
  values
    (sid, auth.uid(),
     left(nullif(p_device_name,    ''), 80),
     left(nullif(p_wheel,          ''), 60),
     left(nullif(p_plugin_version, ''), 32),
     now())
  on conflict (session_id) do update
    set device_name    = excluded.device_name,
        wheel          = excluded.wheel,
        plugin_version = excluded.plugin_version,
        last_seen      = excluded.last_seen;
  return true;
end;
$$;

revoke all on function public.session_heartbeat(text, text, text) from public, anon;
grant  execute on function public.session_heartbeat(text, text, text) to authenticated;

-- ---- get_my_sessions: LEFT JOIN metadata + greatest() last_active ----
-- Recreated to surface the device columns. LEFT JOIN so sessions that never sent a
-- heartbeat (brand-new sign-in, or a pre-metadata old client) still appear with the
-- new columns null. last_active = the freshest of heartbeat last_seen / token
-- refresh / updated, so a still-refreshing-but-not-heartbeating session isn't
-- shown as staler than it is. DROP drops the grants, so re-issue them below.
drop function if exists public.get_my_sessions();
create or replace function public.get_my_sessions()
returns table(
  id             uuid,
  created_at     timestamptz,
  last_active    timestamptz,
  not_after      timestamptz,
  user_agent     text,
  ip             text,
  is_current     boolean,
  device_name    text,
  wheel          text,
  plugin_version text,
  last_seen      timestamptz)
language sql stable security definer set search_path = public, pg_temp as $$
  select
    s.id,
    s.created_at,
    greatest(
      coalesce(m.last_seen,                        'epoch'::timestamptz),
      coalesce((s.refreshed_at at time zone 'utc'),'epoch'::timestamptz),
      coalesce(s.updated_at,                       'epoch'::timestamptz)
    ) as last_active,
    s.not_after,
    s.user_agent,
    host(s.ip) as ip,
    (s.id = nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '')::uuid) as is_current,
    m.device_name,
    m.wheel,
    m.plugin_version,
    m.last_seen
  from auth.sessions s
  left join public.session_metadata m on m.session_id = s.id
  where s.user_id = auth.uid()
  order by greatest(
      coalesce(m.last_seen,                        'epoch'::timestamptz),
      coalesce((s.refreshed_at at time zone 'utc'),'epoch'::timestamptz),
      coalesce(s.updated_at,                       'epoch'::timestamptz)
    ) desc nulls last;
$$;

revoke all on function public.get_my_sessions() from public, anon;
grant  execute on function public.get_my_sessions() to authenticated;
