-- 0102: expose a small tier ORDINAL so the client can color Patreon badges by tier
-- standing (gold / silver / bronze) without amounts ever reaching the client.
-- tier_level = 1 for the highest-pledge tier currently represented among active
-- patrons, 2 for the next, and so on (dense_rank over the tiers' pledge amounts,
-- computed server-side). Manual one-time donors get null. Self-maintaining: when a
-- patron of a higher tier joins, existing tiers shift down automatically.

-- Return type changes (adds `tier_level`), so the old function must be dropped first.
drop function if exists public.list_supporters();
create function public.list_supporters()
returns table(display_name text, tier text, source text, tier_level integer)
language sql stable security definer set search_path = public, pg_temp as $$
  with tiers as (
    select s.tier, dense_rank() over (order by max(s.tier_rank) desc)::integer as lvl
    from public.supporters s
    where s.source = 'patreon' and s.is_active and not s.hidden and s.tier is not null
    group by s.tier
  )
  select s.display_name, s.tier, s.source,
         case when s.source = 'patreon' then t.lvl end as tier_level
  from public.supporters s
  left join tiers t on s.source = 'patreon' and t.tier = s.tier
  where s.is_active and not s.hidden
  order by s.lifetime_cents desc, s.first_seen asc, s.display_name asc;
$$;
revoke all on function public.list_supporters() from public;
grant execute on function public.list_supporters() to anon, authenticated;
