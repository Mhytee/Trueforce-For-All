-- 0048: Patreon supporters wall (recognition, Phase 2). PUBLIC read surface for the
-- in-plugin "Supporters" wall, populated DIRECTLY from Patreon (not via Discord roles),
-- so a patron who never links Discord still gets recognition.
--
-- Why Patreon-direct: the Discord-role path (0034/0037/0039) only ever sees a user who
-- made a Trueforce account AND linked Discord AND holds the role. For a recognition wall
-- that under-counts exactly the people we most want to thank. The `patreon-supporters-sync`
-- Edge Function pulls the campaign's active patrons on a cron and calls sync_supporters().
--
-- Privacy: the wall shows a first name + last initial only (e.g. "Caleb P."), computed in
-- the Edge Function so a legal surname never reaches this table's readable column. The
-- per-row `hidden` flag lets the owner exclude anyone on request. `full_name` is kept for
-- de-dup/debug but is NEVER exposed by the read RPC.
--
-- Security posture matches the rest of the schema: RLS on, the data tables carry no client
-- grants (service_role bypasses RLS for the sync), and the ONE client-facing function is a
-- SECURITY DEFINER reader that returns only the display string + tier. Idempotent.

-- 1. Patreon OAuth token store (private) ---------------------------------------
-- Single row (id = 1). The Creator's Access Token from the developer portal is seeded
-- into the Edge Function's secrets; once the function refreshes it (Patreon tokens expire
-- and rotate), the CURRENT pair lives here so the rotated refresh token isn't lost between
-- invocations. No client ever reads this: service_role only.
create table if not exists public.patreon_oauth (
  id            smallint primary key default 1 check (id = 1),
  access_token  text,
  refresh_token text,
  expires_at    timestamptz,
  updated_at    timestamptz not null default now()
);
alter table public.patreon_oauth enable row level security;
revoke all on public.patreon_oauth from anon, authenticated;

-- 2. supporters (recognition wall source) -------------------------------------
create table if not exists public.supporters (
  patreon_member_id text primary key,
  full_name         text,                      -- private: never exposed by list_supporters()
  display_name      text not null,             -- "First L." computed in the Edge Function
  tier              text,                       -- the patron's Patreon tier title (display label)
  tier_rank         integer not null default 0, -- pledge amount in cents; orders the wall
  amount_cents      integer not null default 0,
  is_active         boolean not null default true,
  hidden            boolean not null default false, -- owner exclude list (honored on request)
  first_seen        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);
alter table public.supporters enable row level security;
revoke all on public.supporters from anon, authenticated;

-- 3. PUBLIC read: the wall ----------------------------------------------------
-- Intentionally public recognition: returns ONLY the display string + tier, for active,
-- non-hidden supporters, best-pledge first. Granted to anon AND authenticated so the wall
-- renders even for a signed-out user (this is a thank-you, and the row carries nothing
-- sensitive: a first name + initial that the patron's own Patreon page already shows).
create or replace function public.list_supporters()
returns table(display_name text, tier text, tier_rank integer)
language sql stable security definer set search_path = public, pg_temp as $$
  select s.display_name, s.tier, s.tier_rank
  from public.supporters s
  where s.is_active and not s.hidden
  order by s.tier_rank desc, s.first_seen asc, s.display_name asc;
$$;
revoke all on function public.list_supporters() from public;
grant execute on function public.list_supporters() to anon, authenticated;

-- 4. Sync (service-role only) -------------------------------------------------
-- One atomic call replaces the active roster: upsert every incoming patron as active, then
-- deactivate anyone previously active who is no longer in the set (a lapse). The Edge
-- Function ONLY calls this after a SUCCESSFUL members pull, so a transient Patreon API
-- failure can never blank the wall (an empty argument is treated as "the campaign genuinely
-- has zero active patrons", which only the function decides). `hidden` is never reset: the
-- insert doesn't set it, so on-conflict updates leave the owner's exclude intact.
create or replace function public.sync_supporters(p_patrons jsonb)
returns table(active integer, deactivated integer)
language plpgsql security definer set search_path = public, pg_temp as $$
declare
  v_ids text[];
begin
  select coalesce(array_agg(x->>'member_id'), '{}')
    into v_ids
  from jsonb_array_elements(coalesce(p_patrons, '[]'::jsonb)) x
  where coalesce(x->>'member_id', '') <> '';

  insert into public.supporters as s
        (patreon_member_id, full_name, display_name, tier, tier_rank, amount_cents, is_active, updated_at)
  select x->>'member_id',
         nullif(x->>'full_name', ''),
         coalesce(nullif(x->>'display_name', ''), 'A supporter'),
         nullif(x->>'tier', ''),
         coalesce((x->>'tier_rank')::integer, 0),
         coalesce((x->>'amount_cents')::integer, 0),
         true, now()
  from jsonb_array_elements(coalesce(p_patrons, '[]'::jsonb)) x
  where coalesce(x->>'member_id', '') <> ''
  on conflict (patreon_member_id) do update set
    full_name    = excluded.full_name,
    display_name = excluded.display_name,
    tier         = excluded.tier,
    tier_rank    = excluded.tier_rank,
    amount_cents = excluded.amount_cents,
    is_active    = true,
    updated_at   = now();

  update public.supporters
     set is_active = false, updated_at = now()
   where is_active = true
     and not (patreon_member_id = any (v_ids));

  return query select
    (select count(*)::integer from public.supporters where is_active),
    (select count(*)::integer from public.supporters where not is_active);
end;
$$;
revoke all on function public.sync_supporters(jsonb) from public, anon, authenticated;
grant execute on function public.sync_supporters(jsonb) to service_role;

-- 5. Cron: pull the roster hourly ---------------------------------------------
-- Mirrors 0046's pg_cron + pg_net pattern (service key out of Vault). The function no-ops
-- (dry run) until the Patreon secrets are set, exactly like discord-role-sync, so scheduling
-- it now is harmless. Hourly is plenty for a recognition wall; a webhook can make it instant
-- later without touching this.
select cron.schedule('tf4all-patreon-supporters', '0 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/patreon-supporters-sync?op=sync',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb);
$job$);
