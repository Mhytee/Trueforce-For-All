-- Lock down upload_preset: drop the client-supplied p_author entirely.
-- Author is always either profiles.username (signed in) or null
-- (anonymous). Clients can keep sending p_author for backward compat
-- with already-deployed plugin builds, but the server ignores it.
-- Mirrors upload_preset_drop_freeform_author applied live.

create or replace function public.upload_preset(
    p_name           text,
    p_game           text,
    p_car_id         text,
    p_body           jsonb,
    p_author         text default null,   -- IGNORED
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

    -- Author is server-authoritative now. Client p_author is ignored.
    -- Signed-in users get their profile.username; anonymous get null.
    v_author := null;
    if v_uid is not null then
        select username into v_author from public.profiles where id = v_uid;
    end if;

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
        'owner_user_id', v_uid,
        'author', v_author);
end;
$$;
