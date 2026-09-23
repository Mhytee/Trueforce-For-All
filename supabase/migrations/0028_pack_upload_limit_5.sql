-- 0028: tighter, pack-specific upload throttle. Packs are the heaviest
-- single upload (body capped at 2 MB vs 128 KB for presets, and each bundles
-- many entries), so cap pack creation at 5/hour/user - half the general
-- 10/hour pooled ceiling, which still applies on top. Keyed on the verified
-- owner_user_id (auth.uid), covered by packs_owner_idx.
create or replace function public.upload_pack(
    p_name text,
    p_body jsonb,
    p_description text default null,
    p_author_version text default null,
    p_entry_count integer default 0,
    p_body_version integer default 1,
    p_plugin_version text default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_pack_recent int; v_id uuid;
    v_name text; v_author text; v_desc text; v_ver text; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then raise exception 'name must be 2..96 chars'; end if;
    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then raise exception 'description too long'; end if;
    v_ver := nullif(btrim(coalesce(p_author_version, '')), '');
    if v_ver is not null and length(v_ver) > 32 then raise exception 'author_version too long'; end if;
    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then raise exception 'body must be a JSON object'; end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
    if p_entry_count < 0 or p_entry_count > 10000 then raise exception 'entry_count out of range'; end if;
    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then raise exception 'submitter blocked'; end if;
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');

    -- pack-specific cap: 5/hour (tighter than the general pool below)
    select count(*) into v_pack_recent from public.packs
        where owner_user_id = v_uid and created_at > now() - interval '1 hour';
    if v_pack_recent >= 5 then raise exception 'pack upload rate limit exceeded'; end if;

    -- general pooled cap: 10/hour across all upload types
    select (select count(*) from public.presets        where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets   where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engines where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.packs          where owner_user_id = v_uid and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 10 then raise exception 'upload rate limit exceeded'; end if;

    insert into public.packs
        (name, author, description, author_version, body_version, body, entry_count,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values (v_name, v_author, v_desc, v_ver, p_body_version, p_body, p_entry_count,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id, 'owner_user_id', v_uid, 'author', v_author);
end;
$$;
