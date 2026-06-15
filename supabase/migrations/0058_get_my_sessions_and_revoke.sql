-- 0058: let a signed-in user see and revoke their own auth sessions (multi-device).
--
-- The Supabase REST/GoTrue API doesn't expose a "list my sessions" endpoint to the
-- anon client, so we add two SECURITY DEFINER helpers that read/mutate auth.sessions
-- strictly scoped to auth.uid(). Both are revoked from anon and granted only to the
-- authenticated role (same lockdown pattern as get_my_achievements / the entitlement
-- helpers).
--
-- Revoke note: deleting an auth.sessions row cascades to auth.refresh_tokens
-- (refresh_tokens_session_id_fkey ON DELETE CASCADE), so the target device can no
-- longer refresh. Its current access token (JWT) stays valid until it expires (<= 1h);
-- that's identical to GoTrue's own logout?scope=others behaviour. The UI says so.

-- ---- list my sessions ------------------------------------------------------
drop function if exists public.get_my_sessions();
create or replace function public.get_my_sessions()
returns table(
  id           uuid,
  created_at   timestamptz,
  last_active  timestamptz,
  not_after    timestamptz,
  user_agent   text,
  ip           text,
  is_current   boolean)
language sql stable security definer set search_path = public, pg_temp as $$
  select
    s.id,
    s.created_at,
    -- refreshed_at is a naive UTC timestamp; normalise to timestamptz. Fall
    -- back to updated_at (already tz-aware) when a session has never refreshed.
    coalesce((s.refreshed_at at time zone 'utc'), s.updated_at) as last_active,
    s.not_after,
    s.user_agent,
    host(s.ip) as ip,
    -- mark the row matching the caller's own JWT session_id claim so the UI can
    -- label "This device" and hide its revoke button.
    (s.id = nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '')::uuid) as is_current
  from auth.sessions s
  where s.user_id = auth.uid()
  order by coalesce((s.refreshed_at at time zone 'utc'), s.updated_at) desc nulls last;
$$;

revoke all on function public.get_my_sessions() from public, anon;
grant execute on function public.get_my_sessions() to authenticated;

-- ---- revoke one of my sessions ---------------------------------------------
drop function if exists public.revoke_my_session(uuid);
create or replace function public.revoke_my_session(p_session_id uuid)
returns boolean
language plpgsql security definer set search_path = public, pg_temp as $$
declare
  deleted int;
  current_sid uuid;
begin
  -- Never let this path nuke the session the caller is currently using; that's
  -- what Sign Out is for, and doing it here would leave the local client in a
  -- half-signed-in state. Compare against the JWT's own session_id claim, and
  -- fail CLOSED: if we can't read the current session id (claim missing), refuse
  -- rather than risk deleting the session in use.
  current_sid := nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '')::uuid;
  if current_sid is null or p_session_id = current_sid then
    return false;
  end if;

  delete from auth.sessions
   where id = p_session_id
     and user_id = auth.uid();
  get diagnostics deleted = row_count;
  return deleted > 0;
end;
$$;

revoke all on function public.revoke_my_session(uuid) from public, anon;
grant execute on function public.revoke_my_session(uuid) to authenticated;

-- ---- is my session still active? (revoke heartbeat) ------------------------
-- The plugin polls this on a timer so a device whose session was revoked from
-- another device signs ITSELF out within minutes, instead of coasting on its
-- cached access token until exp (~1h). Returns true while the caller's JWT
-- session_id still maps to a live auth.sessions row.
--
-- Fail-direction matters and is deliberate: this FAILS OPEN. If the token has
-- no session_id claim, it returns true so a legacy/odd token never self-signs-
-- out. A present-but-revoked session id returns false (the device signs out).
-- A non-uuid claim RAISES, which the client receives as an HTTP error and must
-- treat as "unknown" (leave the session alone) - never as revoked. So the only
-- way a client signs out is an explicit HTTP 200 + boolean false.
drop function if exists public.is_my_session_active();
create or replace function public.is_my_session_active()
returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select case
    when nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '') is null
      then true   -- no session_id claim: fail OPEN
    else exists(
      select 1 from auth.sessions s
      where s.id = (current_setting('request.jwt.claims', true)::json ->> 'session_id')::uuid
        and s.user_id = auth.uid())
  end;
$$;

revoke all on function public.is_my_session_active() from public, anon;
grant execute on function public.is_my_session_active() to authenticated;
