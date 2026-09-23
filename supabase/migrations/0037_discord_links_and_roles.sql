-- 0037: Discord account linking + contribution-role computation (Phase 2, M5 part 1).
--
-- Two link sources feed ONE discord_id per user (see docs/preset-sharing-design.md):
--   * 'discord_oauth'  : the standalone Discord OAuth link (authoritative).
--   * 'patreon'        : opportunistically read from Patreon social_connections (M4);
--                        only fills an empty slot, never overwrites a standalone link.
-- The actual OAuth callback (an Edge Function) and the Patreon webhook call
-- set_discord_link via the service role. The role bot calls compute_discord_roles.
--
-- Reuses the vetted M3/M4 conventions: RLS on, clients read only their own link, all
-- writes go through SECURITY DEFINER functions revoked from anon/authenticated.
-- Idempotent.

-- 1. discord_links -------------------------------------------------------------
create table if not exists public.discord_links (
  user_id          uuid primary key references auth.users(id) on delete cascade,
  discord_id       text not null,
  discord_username text,
  source           text not null check (source in ('discord_oauth','patreon')),
  linked_at        timestamptz not null default now(),
  updated_at       timestamptz not null default now()
);
-- One Discord account per Trueforce user (prevents two accounts claiming the same id).
create unique index if not exists discord_links_discord_id_key on public.discord_links(discord_id);

alter table public.discord_links enable row level security;
drop policy if exists "discord_links_read_own" on public.discord_links;
create policy "discord_links_read_own" on public.discord_links
  for select to authenticated using (user_id = auth.uid());
revoke insert, update, delete on public.discord_links from anon, authenticated;
revoke select on public.discord_links from anon;

-- 2. Client: read / unlink own Discord ----------------------------------------
create or replace function public.get_my_discord()
returns table(discord_id text, discord_username text, source text, linked_at timestamptz)
language sql stable security definer set search_path = public, pg_temp as $$
  select d.discord_id, d.discord_username, d.source, d.linked_at
  from public.discord_links d where d.user_id = auth.uid();
$$;
revoke all on function public.get_my_discord() from public, anon;
grant execute on function public.get_my_discord() to authenticated;

create or replace function public.unlink_my_discord()
returns void language sql security definer set search_path = public, pg_temp as $$
  delete from public.discord_links where user_id = auth.uid();
$$;
revoke all on function public.unlink_my_discord() from public, anon;
grant execute on function public.unlink_my_discord() to authenticated;

-- 3. Set / transition a link (service-role only) ------------------------------
-- Precedence: a standalone OAuth link is authoritative; a 'patreon'-reported link
-- only fills an empty slot or refreshes a prior patreon link, and NEVER overwrites a
-- standalone link. Raises if the discord_id already belongs to a DIFFERENT user.
create or replace function public.set_discord_link(
  p_user_id uuid, p_discord_id text, p_username text, p_source text)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_existing_source text; v_owner uuid;
begin
  if p_source not in ('discord_oauth','patreon') then
    raise exception 'invalid discord link source: %', p_source;
  end if;
  -- Reject linking a Discord id that another user already holds.
  select user_id into v_owner from public.discord_links where discord_id = p_discord_id;
  if v_owner is not null and v_owner <> p_user_id then
    raise exception 'discord id already linked to another account';
  end if;

  select source into v_existing_source from public.discord_links where user_id = p_user_id;
  -- Keep an authoritative standalone link rather than downgrading to a patreon-reported id.
  if v_existing_source = 'discord_oauth' and p_source <> 'discord_oauth' then
    return;
  end if;

  insert into public.discord_links as d (user_id, discord_id, discord_username, source, linked_at, updated_at)
  values (p_user_id, p_discord_id, p_username, p_source, now(), now())
  on conflict (user_id) do update set
    discord_id       = excluded.discord_id,
    discord_username = coalesce(excluded.discord_username, d.discord_username),
    source           = excluded.source,
    updated_at       = now();
end;
$$;
revoke all on function public.set_discord_link(uuid, text, text, text) from public, anon, authenticated;
grant execute on function public.set_discord_link(uuid, text, text, text) to service_role;

-- 4. Contribution roles for the bot (service-role only) -----------------------
-- For each linked user, aggregate their (non-suppressed) community uploads and emit
-- the role keys they qualify for. The bot reconciles these against the server's Discord
-- role ids. Thresholds are simple + tunable here.
-- NOTE: 'car_fact_contributor' is NOT included yet: car_fact_submissions is keyed by an
-- IP-derived submitter_id with NO owner_user_id, so car-fact contributions can't be
-- attributed to a user. Adding that role needs owner_user_id on car_fact_submissions
-- (a future migration + a submit_car_fact tweak).
create or replace function public.compute_discord_roles()
returns table(user_id uuid, discord_id text, discord_username text,
              total_downloads bigint, best_wilson numeric, upload_count bigint, roles text[])
language sql stable security definer set search_path = public, pg_temp as $$
  with contrib as (
    select owner_user_id as uid, downloads, wilson_score from public.presets        where owner_user_id is not null and not is_suppressed
    union all
    select owner_user_id, downloads, wilson_score        from public.game_presets   where owner_user_id is not null and not is_suppressed
    union all
    select owner_user_id, downloads, wilson_score        from public.custom_engines where owner_user_id is not null and not is_suppressed
    union all
    select owner_user_id, downloads, wilson_score        from public.packs          where owner_user_id is not null and not is_suppressed
  ),
  agg as (
    select uid,
           coalesce(sum(downloads), 0)::bigint     as total_downloads,
           coalesce(max(wilson_score), 0)::numeric as best_wilson,
           count(*)::bigint                        as upload_count
    from contrib group by uid
  )
  select dl.user_id, dl.discord_id, dl.discord_username,
         coalesce(a.total_downloads, 0), coalesce(a.best_wilson, 0), coalesce(a.upload_count, 0),
         array_remove(array[
           case when coalesce(a.upload_count, 0)     >= 1  then 'creator'         end,
           case when coalesce(a.total_downloads, 0)  >= 25 then 'popular_creator' end
         ], null) as roles
  from public.discord_links dl
  left join agg a on a.uid = dl.user_id;
$$;
revoke all on function public.compute_discord_roles() from public, anon, authenticated;
grant execute on function public.compute_discord_roles() to service_role;
