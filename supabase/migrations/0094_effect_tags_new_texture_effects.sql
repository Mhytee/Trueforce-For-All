-- 0094: allow the three new texture effect tags on community uploads.
--
-- The plugin now shares AxleSlip, KerbThump and LockupJudder sections on
-- car and game presets, tagged 'axleslip', 'kerbthump' and 'lockupjudder'.
-- The effect_tags whitelist is an inline v_allowed_tags array re-declared
-- inside every upload/update RPC body (no shared helper exists), so this
-- migration re-creates all five tag-filtering functions verbatim from
-- their latest definitions with only the v_allowed_tags array extended by
-- the three new tags:
--   upload_preset (9-arg)  from 0024_rate_limits_per_user.sql
--   upload_preset (10-arg) from 0024_rate_limits_per_user.sql
--   upload_game_preset     from 0024_rate_limits_per_user.sql
--   update_preset          from 0019_restore_update_allow_in_packs.sql
--   update_game_preset     from 0022_game_presets_target_games.sql
-- No signature, security or search_path changes.
--
-- Idempotent: CREATE OR REPLACE throughout, safe to re-run.

-- ---- upload_preset (9-arg) ------------------------------------------

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
        'abs','pitlimiter','drs','collision','audio','airborne',
        'axleslip','kerbthump','lockupjudder'
    ];
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then
        raise exception 'name must be 2..96 chars';
    end if;
    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then
        raise exception 'description too long';
    end if;
    if p_game is null or length(btrim(p_game)) = 0 then raise exception 'game required'; end if;
    v_game := btrim(p_game);
    if length(v_game) > 64 then raise exception 'game too long'; end if;
    if p_car_id is null or length(btrim(p_car_id)) = 0 then raise exception 'car_id required'; end if;
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
            if v_tag is not null and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then v_clean_tags := v_clean_tags || v_tag; end if;
        end loop;
    end if;

    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.presets
        where owner_user_id = v_uid and created_at > now() - interval '1 hour';
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;

    insert into public.presets
        (name, author, description, game, car_id,
         body_type, body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values (v_name, v_author, v_desc, v_game, v_car_id,
            'trueforce-car-preset', p_body_version, p_body, v_clean_tags,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id, 'submitter_id', v_submitter_id,
        'owner_user_id', v_uid, 'author', v_author);
end;
$$;

-- ---- upload_preset (10-arg, + p_allow_in_packs) ---------------------

create or replace function public.upload_preset(
    p_name           text,
    p_game           text,
    p_car_id         text,
    p_body           jsonb,
    p_author         text default null,
    p_description    text default null,
    p_effect_tags    text[] default '{}'::text[],
    p_body_version   int default 1,
    p_plugin_version text default null,
    p_allow_in_packs boolean default false
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_name text; v_author text; v_desc text; v_game text; v_car_id text;
    v_tag text; v_clean_tags text[] := '{}'; v_uid uuid;
    v_allowed_tags text[] := array['engine','revlimiter','roadbumps','tractionloss','gearshift','abs','pitlimiter','drs','collision','audio','airborne','axleslip','kerbthump','lockupjudder'];
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then raise exception 'name must be 2..96 chars'; end if;
    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then raise exception 'description too long'; end if;
    if p_game is null or length(btrim(p_game)) = 0 then raise exception 'game required'; end if;
    v_game := btrim(p_game);
    if length(v_game) > 64 then raise exception 'game too long'; end if;
    if p_car_id is null or length(btrim(p_car_id)) = 0 then raise exception 'car_id required'; end if;
    v_car_id := btrim(p_car_id);
    if length(v_car_id) > 128 then raise exception 'car_id too long'; end if;
    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then raise exception 'body must be a JSON object'; end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
    if p_effect_tags is not null then
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null and v_tag = any(v_allowed_tags) and not (v_tag = any(v_clean_tags)) then v_clean_tags := v_clean_tags || v_tag; end if;
        end loop;
    end if;
    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then raise exception 'submitter blocked'; end if;
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.presets where owner_user_id = v_uid and created_at > now() - interval '1 hour';
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;
    insert into public.presets
        (name, author, description, game, car_id, body_type, body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version, owner_user_id, allow_in_packs)
    values (v_name, v_author, v_desc, v_game, v_car_id, 'trueforce-car-preset', p_body_version, p_body, v_clean_tags,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid, coalesce(p_allow_in_packs, false))
    returning id into v_id;
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id, 'owner_user_id', v_uid, 'author', v_author);
end;
$$;

-- ---- upload_game_preset ---------------------------------------------

create or replace function public.upload_game_preset(
    p_name           text,
    p_game           text,
    p_body           jsonb,
    p_description    text default null,
    p_effect_tags    text[] default '{}'::text[],
    p_body_version   int default 1,
    p_plugin_version text default null,
    p_allow_in_packs boolean default false,
    p_target_games   text[] default '{}'::text[]
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_name text; v_author text; v_desc text; v_game text;
    v_tag text; v_clean_tags text[] := '{}';
    v_game_name text; v_clean_games text[] := '{}';
    v_uid uuid;
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne',
        'axleslip','kerbthump','lockupjudder'
    ];
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then
        raise exception 'name must be 2..96 chars';
    end if;
    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then
        raise exception 'description too long';
    end if;
    if p_game is null or length(btrim(p_game)) = 0 then raise exception 'game required'; end if;
    v_game := btrim(p_game);
    if length(v_game) > 64 then raise exception 'game too long'; end if;
    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then
        raise exception 'body must be a JSON object';
    end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then
        raise exception 'body_version invalid';
    end if;
    if p_effect_tags is not null then
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then v_clean_tags := v_clean_tags || v_tag; end if;
        end loop;
    end if;

    if p_target_games is not null then
        foreach v_game_name in array p_target_games loop
            exit when coalesce(array_length(v_clean_games, 1), 0) >= 64;
            v_game_name := btrim(coalesce(v_game_name, ''));
            if length(v_game_name) between 1 and 64
                and not (v_game_name = any(v_clean_games))
            then v_clean_games := v_clean_games || v_game_name; end if;
        end loop;
    end if;

    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.presets
              where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets
              where owner_user_id = v_uid and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;

    insert into public.game_presets
        (name, author, description, game,
         body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version, owner_user_id,
         allow_in_packs, target_games)
    values (v_name, v_author, v_desc, v_game,
            p_body_version, p_body, v_clean_tags,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid,
            coalesce(p_allow_in_packs, false), v_clean_games)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id, 'submitter_id', v_submitter_id,
        'owner_user_id', v_uid, 'author', v_author);
end;
$$;

-- ---- update_preset --------------------------------------------------

create or replace function public.update_preset(
    p_preset_id     uuid,
    p_name          text default null,
    p_description   text default null,
    p_body          jsonb default null,
    p_effect_tags   text[] default null,
    p_body_version  int default null,
    p_allow_in_packs boolean default null
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
        'abs','pitlimiter','drs','collision','audio','airborne',
        'axleslip','kerbthump','lockupjudder'
    ];
    v_recent int;
    v_new_version int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.presets where id = p_preset_id and not is_suppressed;
    if not found then raise exception 'preset not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your preset';
    end if;

    select count(*) into v_recent from public.presets
        where owner_user_id = v_uid
          and updated_at > now() - interval '1 hour'
          and updated_at <> created_at;
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.presets set name = btrim(p_name) where id = p_preset_id;
    end if;
    if p_description is not null then
        if length(btrim(p_description)) > 1024 then raise exception 'description too long'; end if;
        update public.presets set description = nullif(btrim(p_description), '') where id = p_preset_id;
    end if;
    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.presets set body = p_body where id = p_preset_id;
    end if;
    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
        update public.presets set body_version = p_body_version where id = p_preset_id;
    end if;
    if p_effect_tags is not null then
        v_clean_tags := '{}';
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then v_clean_tags := v_clean_tags || v_tag; end if;
        end loop;
        update public.presets set effect_tags = v_clean_tags where id = p_preset_id;
    end if;
    if p_allow_in_packs is not null then
        update public.presets set allow_in_packs = p_allow_in_packs where id = p_preset_id;
    end if;

    update public.presets
        set content_version = content_version + 1,
            updated_at = now()
        where id = p_preset_id
        returning content_version into v_new_version;

    return jsonb_build_object(
        'id', p_preset_id,
        'content_version', v_new_version);
end;
$$;

-- ---- update_game_preset ---------------------------------------------

create or replace function public.update_game_preset(
    p_game_preset_id uuid,
    p_name           text default null,
    p_description    text default null,
    p_body           jsonb default null,
    p_effect_tags    text[] default null,
    p_body_version   int default null,
    p_allow_in_packs boolean default null,
    p_target_games   text[] default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_clean_tags text[]; v_tag text;
    v_game_name text; v_clean_games text[];
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne',
        'axleslip','kerbthump','lockupjudder'
    ];
    v_recent int;
    v_new_version int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.game_presets where id = p_game_preset_id and not is_suppressed;
    if not found then raise exception 'game preset not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your preset';
    end if;

    select (select count(*) from public.presets
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
         + (select count(*) from public.game_presets
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
      into v_recent;
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.game_presets set name = btrim(p_name) where id = p_game_preset_id;
    end if;
    if p_description is not null then
        if length(btrim(p_description)) > 1024 then raise exception 'description too long'; end if;
        update public.game_presets set description = nullif(btrim(p_description), '') where id = p_game_preset_id;
    end if;
    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.game_presets set body = p_body where id = p_game_preset_id;
    end if;
    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
        update public.game_presets set body_version = p_body_version where id = p_game_preset_id;
    end if;
    if p_effect_tags is not null then
        v_clean_tags := '{}';
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then v_clean_tags := v_clean_tags || v_tag; end if;
        end loop;
        update public.game_presets set effect_tags = v_clean_tags where id = p_game_preset_id;
    end if;
    if p_allow_in_packs is not null then
        update public.game_presets set allow_in_packs = p_allow_in_packs where id = p_game_preset_id;
    end if;
    -- p_target_games null = no change. A non-null value (including the
    -- empty array, which resets the preset back to universal) replaces
    -- the stored set after the same light hygiene as upload.
    if p_target_games is not null then
        v_clean_games := '{}';
        foreach v_game_name in array p_target_games loop
            exit when coalesce(array_length(v_clean_games, 1), 0) >= 64;
            v_game_name := btrim(coalesce(v_game_name, ''));
            if length(v_game_name) between 1 and 64
                and not (v_game_name = any(v_clean_games))
            then v_clean_games := v_clean_games || v_game_name; end if;
        end loop;
        update public.game_presets set target_games = v_clean_games where id = p_game_preset_id;
    end if;

    update public.game_presets
        set content_version = content_version + 1,
            updated_at = now()
        where id = p_game_preset_id
        returning content_version into v_new_version;

    return jsonb_build_object(
        'id', p_game_preset_id,
        'content_version', v_new_version);
end;
$$;
