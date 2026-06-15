-- 0050: keep monetary amounts off the public wall. list_supporters must NOT expose pledge or
-- lifetime cents to anon callers (the previous version returned them for client-side sorting,
-- which leaked each supporter's spend). It now returns only the display name + tier; the
-- spend-based ordering is done entirely server-side, so amounts never leave the database.
--
-- Ordering lets the client group by tier in plain encounter order: tier sections by the tier's
-- top pledge (window max keeps a tier's rows contiguous even if one member pledged extra),
-- then biggest LIFETIME spend first within a tier. tier_rank / lifetime_cents stay in the
-- table (private) purely to drive this ORDER BY.
drop function if exists public.list_supporters();
create or replace function public.list_supporters()
returns table(display_name text, tier text)
language sql stable security definer set search_path = public, pg_temp as $$
  select s.display_name, s.tier
  from public.supporters s
  where s.is_active and not s.hidden
  order by max(s.tier_rank) over (partition by s.tier) desc nulls last,
           s.tier asc nulls last,
           s.lifetime_cents desc,
           s.display_name asc;
$$;
revoke all on function public.list_supporters() from public;
grant execute on function public.list_supporters() to anon, authenticated;
