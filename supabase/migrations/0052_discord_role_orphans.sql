-- 0052: role hygiene -- when a user switches their linked Discord (or unlinks), the OLD Discord
-- account keeps the roles the bot gave it. Roles must apply to ONE Discord account per user, so
-- record the orphaned id and let discord-role-sync strip our managed roles from it.
--
-- Only OUR managed (achievement / contribution) roles are stripped. The supporter tier roles are
-- granted by Patreon's own Discord integration, so Patreon handles those on its side.

create table if not exists public.discord_role_orphans (
  discord_id text primary key,
  created_at timestamptz not null default now()
);
alter table public.discord_role_orphans enable row level security;
revoke all on public.discord_role_orphans from anon, authenticated;

-- set_discord_link: same behaviour as 0043, plus -- when the user's linked discord_id actually
-- CHANGES -- queue the previous id for role cleanup. (The early-return path keeps an authoritative
-- oauth link unchanged, so no orphan there.)
create or replace function public.set_discord_link(
  p_user_id uuid, p_discord_id text, p_username text, p_source text)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_existing_source text; v_owner uuid; v_old_discord text;
begin
  if p_source not in ('discord_oauth','patreon') then
    raise exception 'invalid discord link source: %', p_source;
  end if;
  select user_id into v_owner from public.discord_links where discord_id = p_discord_id;
  if v_owner is not null and v_owner <> p_user_id then
    raise exception 'discord id already linked to another account';
  end if;

  select source, discord_id into v_existing_source, v_old_discord
    from public.discord_links where user_id = p_user_id;
  -- Keep an authoritative standalone link rather than downgrading to a patreon-reported id.
  if v_existing_source = 'discord_oauth' and p_source <> 'discord_oauth' then
    return;
  end if;

  begin
    insert into public.discord_links as d (user_id, discord_id, discord_username, source, linked_at, updated_at)
    values (p_user_id, p_discord_id, p_username, p_source, now(), now())
    on conflict (user_id) do update set
      discord_id       = excluded.discord_id,
      discord_username = coalesce(excluded.discord_username, d.discord_username),
      source           = excluded.source,
      updated_at       = now();
  exception when unique_violation then
    raise exception 'discord id already linked to another account';
  end;

  -- The linked account changed: the old one still holds our managed roles -> queue cleanup.
  if v_old_discord is not null and v_old_discord <> p_discord_id then
    insert into public.discord_role_orphans(discord_id) values (v_old_discord) on conflict do nothing;
  end if;
end;
$$;
revoke all on function public.set_discord_link(uuid, text, text, text) from public, anon, authenticated;
grant execute on function public.set_discord_link(uuid, text, text, text) to service_role;

-- unlink_my_discord: drop the link AND queue the removed id for role cleanup.
create or replace function public.unlink_my_discord()
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_old text;
begin
  select discord_id into v_old from public.discord_links where user_id = auth.uid();
  delete from public.discord_links where user_id = auth.uid();
  if v_old is not null then
    insert into public.discord_role_orphans(discord_id) values (v_old) on conflict do nothing;
  end if;
end;
$$;
revoke all on function public.unlink_my_discord() from public, anon;
grant execute on function public.unlink_my_discord() to authenticated;
