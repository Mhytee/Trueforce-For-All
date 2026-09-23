-- 0101: supporters wall v3: ONE flat ranked list instead of tier sections. Rows are
-- ordered by lifetime support (Patreon campaign_lifetime_support_cents for patrons,
-- donation total for manual one-time donors), so one-time donors sort proportionally
-- to what they gave and long-running patrons rise as their lifetime support grows.
-- `source` is exposed so the client can tell a Patreon patron (tier badge) from a
-- manual donor (name only). Amounts still never reach the client (0050 decision):
-- ordering happens server-side and no cents column is returned.

-- Return type changes (adds `source`), so the old function must be dropped first.
drop function if exists public.list_supporters();
create function public.list_supporters()
returns table(display_name text, tier text, source text)
language sql stable security definer set search_path = public, pg_temp as $$
  select s.display_name, s.tier, s.source
  from public.supporters s
  where s.is_active and not s.hidden
  order by s.lifetime_cents desc, s.first_seen asc, s.display_name asc;
$$;
revoke all on function public.list_supporters() from public;
grant execute on function public.list_supporters() to anon, authenticated;
