-- 0047: progressive backup-deletion warning emails. A lapsed supporter's cloud backup is kept
-- for 2 years (backup_retain_until); before that window closes we email them at 6mo / 3mo / 1mo
-- / 1wk / 1day so they can resubscribe (restart the timer) or export a local backup. Track the
-- most-urgent stage already sent so each fires once, and reset it on (re)subscribe.

alter table public.entitlements add column if not exists backup_warn_stage int not null default 0;

-- set_supporter, updated to also reset the warn cycle on (re)subscribe (mirrors the retain_until
-- reset). Otherwise unchanged from 0034: lapse -> now()+2y; resubscribe -> null; duplicate lapse
-- keeps the existing window. Service-role only.
create or replace function public.set_supporter(
  p_user_id uuid, p_on boolean, p_tier text default null, p_patreon_member_id text default null)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
begin
  insert into public.entitlements as e
        (user_id, is_supporter, tier, patreon_member_id, supporter_since, last_active_at, backup_retain_until, backup_warn_stage, updated_at)
  values (p_user_id, p_on, p_tier, p_patreon_member_id,
          case when p_on then now() else null end,
          now(),
          case when p_on then null else now() + interval '2 years' end,
          0,
          now())
  on conflict (user_id) do update set
    is_supporter        = p_on,
    tier                = coalesce(p_tier, e.tier),
    patreon_member_id   = coalesce(p_patreon_member_id, e.patreon_member_id),
    supporter_since     = case when p_on and e.supporter_since is null then now() else e.supporter_since end,
    last_active_at      = now(),
    backup_retain_until = case when p_on then null
                               when e.is_supporter then now() + interval '2 years'
                               else e.backup_retain_until end,
    backup_warn_stage   = case when p_on then 0 else e.backup_warn_stage end,
    updated_at          = now();
end;
$$;
revoke all on function public.set_supporter(uuid, boolean, text, text) from public, anon, authenticated;
grant execute on function public.set_supporter(uuid, boolean, text, text) to service_role;

-- Candidates for the warning worker: lapsed users with a still-future deletion date + their
-- email (from auth.users). Service-role only; the email never reaches a client.
create or replace function public.list_backup_warn_candidates()
returns table(user_id uuid, email text, backup_retain_until timestamptz, backup_warn_stage int)
language sql stable security definer set search_path = public, pg_temp as $$
  select e.user_id, u.email::text, e.backup_retain_until, e.backup_warn_stage
  from public.entitlements e
  join auth.users u on u.id = e.user_id
  where e.is_supporter = false
    and e.backup_retain_until is not null
    and e.backup_retain_until > now();
$$;
revoke all on function public.list_backup_warn_candidates() from public, anon, authenticated;
grant execute on function public.list_backup_warn_candidates() to service_role;

-- Record the most-urgent warning stage sent (service-role; called by the worker after a send).
create or replace function public.set_backup_warn_stage(p_user_id uuid, p_stage int)
returns void language sql security definer set search_path = public, pg_temp as $$
  update public.entitlements set backup_warn_stage = p_stage, updated_at = now()
   where user_id = p_user_id;
$$;
revoke all on function public.set_backup_warn_stage(uuid, int) from public, anon, authenticated;
grant execute on function public.set_backup_warn_stage(uuid, int) to service_role;
