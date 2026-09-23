-- 0104: expose entitlements.supporter_since through get_my_entitlement, so the
-- plugin can tell "supports now" apart from "has ever supported".
--
-- supporter_since is stamped once on first support and never cleared (0034:
-- `case when p_on and e.supporter_since is null then now() else ... end`), so it
-- survives any number of lapses. backup_retain_until does NOT work for this: it
-- is a 2-year backup-retention clock that is cleared on resubscribe.
--
-- Used by the support prompt, which stays gone for anyone who has ever backed
-- the project rather than only current supporters.
--
-- Return-type change needs drop + create; create-or-replace cannot widen it.
-- Older plugin builds read fields by name and ignore the extra column, so this
-- is backwards compatible. Grants below match what the dropped function had
-- (authenticated + service_role; anon never had execute). Idempotent.

drop function if exists public.get_my_entitlement();

create function public.get_my_entitlement()
returns table (
    is_supporter        boolean,
    tier                text,
    backup_retain_until timestamptz,
    supporter_since     timestamptz)
language sql
stable
security definer
set search_path = public, pg_temp
as $$
  select coalesce(e.is_supporter, false), e.tier, e.backup_retain_until, e.supporter_since
  from (select auth.uid() as uid) c
  left join public.entitlements e on e.user_id = c.uid;
$$;

revoke all on function public.get_my_entitlement() from public, anon;
grant execute on function public.get_my_entitlement() to authenticated, service_role;
