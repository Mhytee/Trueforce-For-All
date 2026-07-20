-- 0103: tier badges rank tiers by their FLOOR, not their ceiling. tier_rank
-- per supporter row is what that member actually pays each month, and
-- Patreon allows paying above a tier's price, so ranking tiers by
-- max(member payment) (0102) let one generous over-pledger in a low tier
-- steal gold from the real top tier. The smallest payment in a tier can
-- never be below the tier's price, so min() tracks the sticker price and
-- survives renames, added tiers, and order-preserving price changes
-- (grandfathered members keep the old order until they converge) with
-- zero maintenance. Return shape unchanged from 0102.
create or replace function public.list_supporters()
returns table(display_name text, tier text, source text, tier_level integer)
language sql stable security definer set search_path = public, pg_temp as $$
  with tiers as (
    select s.tier, dense_rank() over (order by min(s.tier_rank) desc)::integer as lvl
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
