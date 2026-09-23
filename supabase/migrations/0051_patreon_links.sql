-- 0051: Patreon account link (Phase 2). Lets a user prove supporter status in-plugin via
-- Patreon OAuth, unlocking the badge + cloud backup WITHOUT Discord, and opportunistically
-- fills their Discord link from Patreon's social_connections. Mirrors discord_links (0037):
-- RLS on, clients read only their own link, all writes go through SECURITY DEFINER functions
-- revoked from anon/authenticated. Idempotent.

create table if not exists public.patreon_links (
  user_id           uuid primary key references auth.users(id) on delete cascade,
  patreon_user_id   text not null,
  patreon_full_name text,
  linked_at         timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);
-- One Patreon account per Trueforce user (and vice versa).
create unique index if not exists patreon_links_patreon_user_id_key on public.patreon_links(patreon_user_id);
alter table public.patreon_links enable row level security;
drop policy if exists "patreon_links_read_own" on public.patreon_links;
create policy "patreon_links_read_own" on public.patreon_links
  for select to authenticated using (user_id = auth.uid());
revoke insert, update, delete on public.patreon_links from anon, authenticated;
revoke select on public.patreon_links from anon;

-- Client: read / unlink own Patreon link.
create or replace function public.get_my_patreon()
returns table(patreon_user_id text, patreon_full_name text, linked_at timestamptz)
language sql stable security definer set search_path = public, pg_temp as $$
  select p.patreon_user_id, p.patreon_full_name, p.linked_at
  from public.patreon_links p where p.user_id = auth.uid();
$$;
revoke all on function public.get_my_patreon() from public, anon;
grant execute on function public.get_my_patreon() to authenticated;

create or replace function public.unlink_my_patreon()
returns void language sql security definer set search_path = public, pg_temp as $$
  delete from public.patreon_links where user_id = auth.uid();
$$;
revoke all on function public.unlink_my_patreon() from public, anon;
grant execute on function public.unlink_my_patreon() to authenticated;

-- Service-role: set / transition a link. Rejects a Patreon id already held by another user
-- (mirrors set_discord_link's conflict guard). The linked user is always the JWT subject the
-- patreon-link Edge Function passes; never a client-supplied id.
create or replace function public.set_patreon_link(
  p_user_id uuid, p_patreon_user_id text, p_full_name text)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_owner uuid;
begin
  select user_id into v_owner from public.patreon_links where patreon_user_id = p_patreon_user_id;
  if v_owner is not null and v_owner <> p_user_id then
    raise exception 'patreon account already linked to another account';
  end if;
  insert into public.patreon_links as p (user_id, patreon_user_id, patreon_full_name, linked_at, updated_at)
  values (p_user_id, p_patreon_user_id, p_full_name, now(), now())
  on conflict (user_id) do update set
    patreon_user_id   = excluded.patreon_user_id,
    patreon_full_name = coalesce(excluded.patreon_full_name, p.patreon_full_name),
    updated_at        = now();
end;
$$;
revoke all on function public.set_patreon_link(uuid, text, text) from public, anon, authenticated;
grant execute on function public.set_patreon_link(uuid, text, text) to service_role;
