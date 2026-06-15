-- 0034: supporter entitlements + 2-year retention + gate backup UPLOADS on supporter
-- status (Phase 2, milestone M3).
--
-- Model (see docs/preset-sharing-design.md, Phase 2):
--   * UPLOAD (insert/update to the `backups` bucket) requires is_supporter = true.
--   * RESTORE (select/download) stays owner-only with NO supporter requirement, so a
--     lapsed supporter can still recover their files during the 2-year window.
--   * On lapse, backup_retain_until = now() + 2 years. On (re)subscribe it is cleared
--     (so a later lapse restarts a fresh 2 years). A nightly pg_cron GC deletes the
--     backup objects of users who lapsed and whose window has passed.
--
-- Security posture matches the rest of the schema: RLS on, clients can only READ their
-- own entitlement; all writes + the GC go through SECURITY DEFINER functions that are
-- revoked from anon/authenticated (service_role / Patreon webhook / dev-test only).
-- Idempotent (re-runnable).

-- 1. entitlements --------------------------------------------------------------
create table if not exists public.entitlements (
  user_id             uuid primary key references auth.users(id) on delete cascade,
  is_supporter        boolean     not null default false,
  tier                text,
  patreon_member_id   text,
  supporter_since     timestamptz,
  last_active_at      timestamptz,
  backup_retain_until timestamptz,            -- null while supporter; now()+2y on lapse
  updated_at          timestamptz not null default now()
);
alter table public.entitlements enable row level security;

-- Owner can read their own entitlement (to show status in the UI). No direct writes.
drop policy if exists "entitlements_read_own" on public.entitlements;
create policy "entitlements_read_own" on public.entitlements
  for select to authenticated using (user_id = auth.uid());
revoke insert, update, delete on public.entitlements from anon, authenticated;
-- Belt-and-braces (matches 0023/0027): no table-level read for anon either.
revoke select on public.entitlements from anon;

-- 2. is-active-supporter check (used by the storage upload policy) -------------
-- No parameter: uses auth.uid() internally so it can't be used to probe another
-- user's supporter status. SECURITY DEFINER to read entitlements past RLS.
create or replace function public.is_active_supporter()
returns boolean
language sql
stable
security definer
set search_path = public, pg_temp
as $$
  select coalesce((select is_supporter from public.entitlements where user_id = auth.uid()), false);
$$;
revoke all on function public.is_active_supporter() from public;
grant execute on function public.is_active_supporter() to authenticated;

-- 3. Gate backup UPLOADS on supporter status ----------------------------------
-- Replaces the 0033 insert/update policies, adding the supporter requirement. INSERT
-- (first backup) AND UPDATE (overwrite via x-upsert) are both backup WRITES, so both
-- require an active supporter -- otherwise a lapsed user could keep refreshing their
-- backup for free, defeating the gate. SELECT (restore) + DELETE (manage own data)
-- from 0033 stay owner-only with NO supporter check, so a lapsed supporter can still
-- recover and remove files during the 2-year retention window.
drop policy if exists "backups_insert_own" on storage.objects;
create policy "backups_insert_own" on storage.objects
  for insert to authenticated
  with check (bucket_id = 'backups'
              and (storage.foldername(name))[1] = auth.uid()::text
              and public.is_active_supporter());

drop policy if exists "backups_update_own" on storage.objects;
create policy "backups_update_own" on storage.objects
  for update to authenticated
  using      (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text)
  with check (bucket_id = 'backups'
              and (storage.foldername(name))[1] = auth.uid()::text
              and public.is_active_supporter());

-- 4. Client read of own entitlement (for the Settings UI) ---------------------
create or replace function public.get_my_entitlement()
returns table(is_supporter boolean, tier text, backup_retain_until timestamptz)
language sql
stable
security definer
set search_path = public, pg_temp
as $$
  select coalesce(e.is_supporter, false), e.tier, e.backup_retain_until
  from (select auth.uid() as uid) c
  left join public.entitlements e on e.user_id = c.uid;
$$;
revoke all on function public.get_my_entitlement() from public;
grant execute on function public.get_my_entitlement() to authenticated;

-- 5. Set / transition entitlement (service-role only) -------------------------
-- The Patreon webhook (M4) and the dev/test toggle call this. Encapsulates the
-- retention-timer rule: supporter (or resubscribe) -> retain_until null; lapse ->
-- now()+2y. NOT callable by clients (revoked from anon/authenticated).
create or replace function public.set_supporter(
  p_user_id uuid, p_on boolean, p_tier text default null, p_patreon_member_id text default null)
returns void
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
  insert into public.entitlements as e
        (user_id, is_supporter, tier, patreon_member_id, supporter_since, last_active_at, backup_retain_until, updated_at)
  values (p_user_id, p_on, p_tier, p_patreon_member_id,
          case when p_on then now() else null end,
          now(),
          case when p_on then null else now() + interval '2 years' end,
          now())
  on conflict (user_id) do update set
    is_supporter        = p_on,
    tier                = coalesce(p_tier, e.tier),
    patreon_member_id   = coalesce(p_patreon_member_id, e.patreon_member_id),
    supporter_since     = case when p_on and e.supporter_since is null then now() else e.supporter_since end,
    last_active_at      = now(),
    -- (re)subscribe -> clear the timer; FIRST lapse (was a supporter) -> fresh 2y;
    -- duplicate/retry lapse (already lapsed) -> keep the existing window so a retried
    -- webhook can't keep pushing the expiry out.
    backup_retain_until = case when p_on then null
                               when e.is_supporter then now() + interval '2 years'
                               else e.backup_retain_until end,
    updated_at          = now();
end;
$$;
revoke all on function public.set_supporter(uuid, boolean, text, text) from public, anon, authenticated;
grant execute on function public.set_supporter(uuid, boolean, text, text) to service_role;

-- 6. 2-year retention GC (nightly pg_cron) ------------------------------------
-- Deletes backup objects for users who lapsed AND whose retention window has passed.
-- Deleting the storage.objects rows makes them inaccessible immediately; physical S3
-- cleanup is handled by Supabase's orphan sweep. Returns the count deleted.
create or replace function public.gc_expired_backups()
returns integer
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  v_deleted integer := 0;
begin
  with expired as (
    select user_id::text as uid
    from public.entitlements
    where is_supporter = false
      and backup_retain_until is not null
      and backup_retain_until < now()
  )
  delete from storage.objects o
  using expired x
  where o.bucket_id = 'backups'
    and (storage.foldername(o.name))[1] = x.uid;
  get diagnostics v_deleted = row_count;
  return v_deleted;
end;
$$;
revoke all on function public.gc_expired_backups() from public, anon, authenticated;
-- pg_cron runs the job as the scheduling role; grant so execution never depends on
-- which internal role pg_cron uses (mirrors set_supporter's service_role grant).
grant execute on function public.gc_expired_backups() to service_role;

-- Schedule nightly at 03:30 UTC (idempotent: unschedule any prior job first).
do $$
begin
  if exists (select 1 from cron.job where jobname = 'gc-expired-backups') then
    perform cron.unschedule('gc-expired-backups');
  end if;
end $$;
select cron.schedule('gc-expired-backups', '30 3 * * *', $job$ select public.gc_expired_backups(); $job$);
