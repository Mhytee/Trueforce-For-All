-- 0049: supporters wall v2. Group by tier + order within a tier by LIFETIME spend, and
-- support manually-entered one-time donors (Ko-fi / PayPal) that the Patreon sync must never
-- touch. Builds on 0048. Idempotent.

-- lifetime_cents = Patreon's campaign_lifetime_support_cents (total ever paid to the campaign),
-- used to rank supporters within a tier (top spender first). source distinguishes auto Patreon
-- rows from manual one-time entries so the sync can leave the latter alone.
alter table public.supporters add column if not exists lifetime_cents integer not null default 0;
alter table public.supporters add column if not exists source        text    not null default 'patreon';
do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'supporters_source_chk') then
    alter table public.supporters add constraint supporters_source_chk check (source in ('patreon','manual'));
  end if;
end $$;

-- list_supporters: now also returns lifetime_cents. Order = highest tier first, then biggest
-- lifetime spend within the tier. The client groups the ordered rows into tier sections.
-- DROP first: adding an OUT column changes the return type, which CREATE OR REPLACE can't do.
drop function if exists public.list_supporters();
create or replace function public.list_supporters()
returns table(display_name text, tier text, tier_rank integer, lifetime_cents integer)
language sql stable security definer set search_path = public, pg_temp as $$
  select s.display_name, s.tier, s.tier_rank, s.lifetime_cents
  from public.supporters s
  where s.is_active and not s.hidden
  order by s.tier_rank desc, s.lifetime_cents desc, s.display_name asc;
$$;
revoke all on function public.list_supporters() from public;
grant execute on function public.list_supporters() to anon, authenticated;

-- sync_supporters: carries lifetime_cents, stamps source='patreon', and ONLY deactivates
-- patreon rows that fell out of the pull. Manual one-time entries (source='manual') are never
-- touched by the sync, so a clean Patreon pull can't wipe the pre-seeded one-time wall.
create or replace function public.sync_supporters(p_patrons jsonb)
returns table(active integer, deactivated integer)
language plpgsql security definer set search_path = public, pg_temp as $$
declare v_ids text[];
begin
  select coalesce(array_agg(x->>'member_id'), '{}') into v_ids
  from jsonb_array_elements(coalesce(p_patrons, '[]'::jsonb)) x
  where coalesce(x->>'member_id','') <> '';

  insert into public.supporters as s
    (patreon_member_id, full_name, display_name, tier, tier_rank, amount_cents, lifetime_cents, source, is_active, updated_at)
  select x->>'member_id', nullif(x->>'full_name',''),
         coalesce(nullif(x->>'display_name',''),'A supporter'),
         nullif(x->>'tier',''), coalesce((x->>'tier_rank')::integer,0),
         coalesce((x->>'amount_cents')::integer,0),
         coalesce((x->>'lifetime_cents')::integer,0),
         'patreon', true, now()
  from jsonb_array_elements(coalesce(p_patrons,'[]'::jsonb)) x
  where coalesce(x->>'member_id','') <> ''
  on conflict (patreon_member_id) do update set
    full_name=excluded.full_name, display_name=excluded.display_name,
    tier=excluded.tier, tier_rank=excluded.tier_rank, amount_cents=excluded.amount_cents,
    lifetime_cents=excluded.lifetime_cents, source='patreon', is_active=true, updated_at=now();

  update public.supporters set is_active=false, updated_at=now()
   where is_active=true and source='patreon' and not (patreon_member_id = any(v_ids));

  return query select
    (select count(*)::integer from public.supporters where is_active),
    (select count(*)::integer from public.supporters where not is_active);
end;
$$;
revoke all on function public.sync_supporters(jsonb) from public, anon, authenticated;
grant execute on function public.sync_supporters(jsonb) to service_role;

-- Manual one-time supporter (Ko-fi / PayPal pre-seed). Owner-only (service_role). Keyed by a
-- deterministic synthetic id from a slug (or the name) so re-adding updates in place. tier
-- defaults to 'One-time supporters' at rank 0 so it sits below the paid recurring tiers; pass
-- p_tier_rank to move it. p_lifetime_cents lets one-time donors sort by total given too.
create or replace function public.add_manual_supporter(
  p_display_name text, p_lifetime_cents integer default 0,
  p_tier text default 'One-time supporters', p_tier_rank integer default 0, p_key text default null)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_key text;
begin
  v_key := 'manual:' || coalesce(nullif(p_key,''), md5(lower(coalesce(p_display_name,''))));
  insert into public.supporters as s
    (patreon_member_id, full_name, display_name, tier, tier_rank, amount_cents, lifetime_cents, source, is_active, updated_at)
  values (v_key, null, coalesce(nullif(p_display_name,''),'A supporter'),
          nullif(p_tier,''), coalesce(p_tier_rank,0), 0, coalesce(p_lifetime_cents,0), 'manual', true, now())
  on conflict (patreon_member_id) do update set
    display_name=excluded.display_name, tier=excluded.tier, tier_rank=excluded.tier_rank,
    lifetime_cents=excluded.lifetime_cents, source='manual', is_active=true, updated_at=now();
end;
$$;
revoke all on function public.add_manual_supporter(text,integer,text,integer,text) from public, anon, authenticated;
grant execute on function public.add_manual_supporter(text,integer,text,integer,text) to service_role;
