-- 0043: make set_discord_link's discord-id conflict deterministic + race-safe.
--
-- 0037's version did a check-then-write: SELECT the discord_id owner, raise if it's a
-- different user, then upsert ON CONFLICT (user_id). Under two users linking the SAME
-- discord_id concurrently, both pre-checks can miss (TOCTOU) and the second write trips the
-- discord_id unique index (discord_links_discord_id_key) with a raw "duplicate key" error
-- whose text does NOT contain "already linked to another account" -- so the discord-link
-- Edge Function classifies it as a generic 500 instead of the friendly 409. Wrapping the
-- upsert in an exception handler normalizes EVERY conflict path (explicit pre-check OR the
-- race that slips past it) to the one canonical message. Behaviour is otherwise identical.
create or replace function public.set_discord_link(
  p_user_id uuid, p_discord_id text, p_username text, p_source text)
returns void language plpgsql security definer set search_path = public, pg_temp as $$
declare v_existing_source text; v_owner uuid;
begin
  if p_source not in ('discord_oauth','patreon') then
    raise exception 'invalid discord link source: %', p_source;
  end if;
  select user_id into v_owner from public.discord_links where discord_id = p_discord_id;
  if v_owner is not null and v_owner <> p_user_id then
    raise exception 'discord id already linked to another account';
  end if;

  select source into v_existing_source from public.discord_links where user_id = p_user_id;
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
    -- the only unique constraint reachable here is discord_links_discord_id_key
    raise exception 'discord id already linked to another account';
  end;
end;
$$;
revoke all on function public.set_discord_link(uuid, text, text, text) from public, anon, authenticated;
grant execute on function public.set_discord_link(uuid, text, text, text) to service_role;
