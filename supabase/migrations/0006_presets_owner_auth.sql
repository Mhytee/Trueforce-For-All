-- Pull Supabase Auth forward so signed-in users can edit/delete their
-- own preset uploads. Anonymous uploads stay allowed (owner_user_id
-- null) but are unmanageable - same trade as before. Existing rows
-- pre-auth keep their NULL ownership and become read-only forever.
-- Mirrors carfacts_presets_owner_auth applied live.

alter table public.presets
    add column if not exists owner_user_id uuid references auth.users(id) on delete set null;

create index if not exists presets_owner_idx
    on public.presets (owner_user_id);

-- Replace upload_preset so authenticated callers get owner_user_id
-- stamped automatically from auth.uid(). PostgREST exposes auth.uid()
-- inside SECURITY DEFINER functions when the JWT is valid.

create or replace function public.upload_preset(
    p_name           text,
    p_game           text,
    p_car_id         text,
    p_body           jsonb,
    p_author         text default null,
    p_description    text default null,
    p_effect_tags    text[] default '{}'::text[],
    p_body_version   int default 1,
    p_plugin_version text default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_name text; v_author text; v_desc text;
    v_game text; v_car_id text;
    v_tag text; v_clean_tags text[] := '{}';
    v_uid uuid;
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne'
    ];
begin
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then
        raise exception 'name must be 2..96 chars';
    end if;

    v_author := nullif(btrim(coalesce(p_author, '')), '');
    if v_author is not null and length(v_author) > 64 then
        raise exception 'author too long';
    end if;

    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then
        raise exception 'description too long';
    end if;

    if p_game is null or length(btrim(p_game)) = 0 then
        raise exception 'game required';
    end if;
    v_game := btrim(p_game);
    if length(v_game) > 64 then raise exception 'game too long'; end if;

    if p_car_id is null or length(btrim(p_car_id)) = 0 then
        raise exception 'car_id required';
    end if;
    v_car_id := btrim(p_car_id);
    if length(v_car_id) > 128 then raise exception 'car_id too long'; end if;

    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then
        raise exception 'body must be a JSON object';
    end if;

    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then
        raise exception 'body_version invalid';
    end if;

    if p_effect_tags is not null then
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null
                and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then
                v_clean_tags := v_clean_tags || v_tag;
            end if;
        end loop;
    end if;

    v_ip := _client_ip();
    v_submitter_id := _derive_submitter_id(v_ip);
    v_uid := auth.uid();

    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.presets
        where source_ip_hash = v_ip_hash
              and created_at > now() - interval '1 hour';
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;

    insert into public.presets
        (name, author, description, game, car_id,
         body_type, body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values
        (v_name, v_author, v_desc, v_game, v_car_id,
         'trueforce-car-preset', p_body_version, p_body, v_clean_tags,
         v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id,
        'submitter_id', v_submitter_id,
        'owner_user_id', v_uid);
end;
$$;

-- Update an existing preset's metadata + body. The owner gate is
-- enforced server-side: auth.uid() must match owner_user_id, else the
-- update is rejected. Rate-limited per IP.
create or replace function public.update_preset(
    p_preset_id     uuid,
    p_name          text default null,
    p_description   text default null,
    p_body          jsonb default null,
    p_effect_tags   text[] default null,
    p_body_version  int default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_owner uuid;
    v_clean_tags text[];
    v_tag text;
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne'
    ];
    v_ip text; v_ip_hash text; v_recent int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    select owner_user_id into v_owner
      from public.presets
     where id = p_preset_id and not is_suppressed;
    if not found then raise exception 'preset not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your preset';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.presets
        where source_ip_hash = v_ip_hash
              and created_at > now() - interval '1 hour';
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.presets set name = btrim(p_name) where id = p_preset_id;
    end if;

    if p_description is not null then
        if length(btrim(p_description)) > 1024 then
            raise exception 'description too long';
        end if;
        update public.presets set description = nullif(btrim(p_description), '') where id = p_preset_id;
    end if;

    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.presets set body = p_body where id = p_preset_id;
    end if;

    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then
            raise exception 'body_version invalid';
        end if;
        update public.presets set body_version = p_body_version where id = p_preset_id;
    end if;

    if p_effect_tags is not null then
        v_clean_tags := '{}';
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null
                and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then
                v_clean_tags := v_clean_tags || v_tag;
            end if;
        end loop;
        update public.presets set effect_tags = v_clean_tags where id = p_preset_id;
    end if;

    return jsonb_build_object('id', p_preset_id);
end;
$$;

create or replace function public.delete_preset(
    p_preset_id uuid
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.presets
     where id = p_preset_id;
    if not found then raise exception 'preset not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your preset';
    end if;
    update public.presets set is_suppressed = true where id = p_preset_id;
    return jsonb_build_object('id', p_preset_id, 'deleted', true);
end;
$$;
