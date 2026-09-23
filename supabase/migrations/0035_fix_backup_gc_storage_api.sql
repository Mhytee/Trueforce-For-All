-- 0035: the 0034 retention GC used a direct DELETE from storage.objects, which
-- Supabase BLOCKS (the storage.protect_delete trigger: "Direct deletion from storage
-- tables is not allowed. Use the Storage API instead."). Confirmed live on apply.
--
-- Storage object deletion must go through the Storage API with the service role. That
-- belongs in the M4 retention worker (a Supabase Edge Function, which gets the service
-- role key from its runtime env, lists expired users via list_expired_backup_users(),
-- and DELETEs each object via the Storage REST API). Nothing is deletable for ~2 years
-- anyway (the first lapse + 2-year window), so deferring the deletion mechanism is safe.
--
-- This migration: drop the broken direct-delete GC + unschedule its nightly job, and
-- expose list_expired_backup_users() for the M4 worker. The entitlements table, the
-- supporter upload gate, set_supporter, is_active_supporter, and get_my_entitlement
-- from 0034 are unchanged and working.

do $$
begin
  if exists (select 1 from cron.job where jobname = 'gc-expired-backups') then
    perform cron.unschedule('gc-expired-backups');
  end if;
end $$;

drop function if exists public.gc_expired_backups();

-- Identifies users whose backups are due for deletion: lapsed AND past the 2-year
-- window. The M4 retention Edge Function calls this, then deletes each user's
-- '<user_id>/setup.json' via the Storage API. Service-role only.
create or replace function public.list_expired_backup_users()
returns table(user_id uuid)
language sql stable security definer set search_path = public, pg_temp as $$
  select e.user_id from public.entitlements e
   where e.is_supporter = false
     and e.backup_retain_until is not null
     and e.backup_retain_until < now();
$$;
revoke all on function public.list_expired_backup_users() from public, anon, authenticated;
grant execute on function public.list_expired_backup_users() to service_role;
