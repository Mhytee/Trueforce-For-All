-- 0057: per-user rate limit for the WARNEMAIL preview self-test (backup-retention-warn op=preview).
-- The email always goes to the caller's own verified address (verify_jwt=true), so the only abuse is
-- self-spam / Resend quota burn. Cap it at 5 per rolling hour AND 15 per day per user. Mirrors the
-- project's per-user-count throttles (0024), keyed on the verified auth user id passed by the fn.

create table if not exists public.warn_preview_log (
  user_id uuid not null references auth.users(id) on delete cascade,
  sent_at timestamptz not null default now()
);
create index if not exists warn_preview_log_user_time on public.warn_preview_log(user_id, sent_at);
alter table public.warn_preview_log enable row level security;
revoke all on public.warn_preview_log from anon, authenticated;

-- Atomic claim: returns true + records a send if the user is under the limit, false otherwise.
-- Service-role only; the Edge Function calls it with the JWT's verified subject.
create or replace function public.claim_warn_preview(p_user_id uuid)
returns boolean language plpgsql security definer set search_path = public, pg_temp as $$
begin
  -- Keep the table tiny; the daily count below relies on this prune.
  delete from public.warn_preview_log where sent_at < now() - interval '1 day';
  if (select count(*) from public.warn_preview_log
        where user_id = p_user_id and sent_at > now() - interval '1 hour') >= 5
     or (select count(*) from public.warn_preview_log where user_id = p_user_id) >= 15 then
    return false;
  end if;
  insert into public.warn_preview_log(user_id) values (p_user_id);
  return true;
end;
$$;
revoke all on function public.claim_warn_preview(uuid) from public, anon, authenticated;
grant execute on function public.claim_warn_preview(uuid) to service_role;
