-- 0105: reject placeholder car ids at the database boundary.
--
-- SimHub fabricates a per-session placeholder car id ("DC__" + its own
-- startup timestamp) for games that report no car identity, Farming
-- Simulator before the TF4ALL Enhanced Telemetry mod's stable ids. Facts
-- keyed to those ids are unreachable by construction (the id never recurs),
-- and clients older than the 0.2.6 identity fix keep submitting them. The
-- 2026-08-09 cleanup archived and purged the existing rows; this trigger
-- keeps the tables clean regardless of client version. Client-side, the
-- fire-and-forget submit path logs and drops the rejection, so old clients
-- are unaffected beyond their garbage being declined.
--
-- Idempotent: create-or-replace + drop-if-exists throughout.

create or replace function public.reject_placeholder_car_ids()
returns trigger
language plpgsql
as $$
begin
  if new.car_id ~ '^DC__\d+$' then
    raise exception 'placeholder car_id rejected: % is a session-local SimHub id, not a real vehicle', new.car_id
      using errcode = '23514';
  end if;
  return new;
end
$$;

drop trigger if exists trg_reject_placeholder_car_ids on public.car_fact_submissions;
create trigger trg_reject_placeholder_car_ids
  before insert or update on public.car_fact_submissions
  for each row execute function public.reject_placeholder_car_ids();

drop trigger if exists trg_reject_placeholder_car_ids_consensus on public.car_fact_consensus;
create trigger trg_reject_placeholder_car_ids_consensus
  before insert or update on public.car_fact_consensus
  for each row execute function public.reject_placeholder_car_ids();
