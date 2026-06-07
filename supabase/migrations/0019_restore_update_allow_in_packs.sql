-- Restore p_allow_in_packs parameter on update_preset / update_game_preset /
-- update_custom_engine.
--
-- Migration 0015 added allow_in_packs columns to all three asset tables AND
-- extended the three update_* RPCs to accept p_allow_in_packs (default null =
-- no change). Migration 0018 rewrote those three functions to fix the
-- edit-rate-limit predicate but inadvertently dropped the p_allow_in_packs
-- parameter, so the client (PresetSharingClient.UpdatePresetAsync line 344,
-- UpdateGamePresetAsync line 1198, UpdateCustomEngineAsync line 1571) now
-- hits a PostgREST function-signature-mismatch on every edit that tries to
-- flip the flag.
--
-- This migration recreates the three functions with both fixes in place:
--   - 0018's per-user predicate (owner_user_id = auth.uid()
--     AND updated_at > now() - interval '1 hour' AND updated_at <> created_at)
--   - 0015's p_allow_in_packs parameter and the conditional update statement
-- update_pack is unaffected (allow_in_packs is not a pack-level field).
--
-- Re-runnable via CREATE OR REPLACE.

-- ---- presets ------------------------------------------------------

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
        'abs','pitlimiter','drs','collision','audio','airborne'
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

-- ---- game_presets -------------------------------------------------

create or replace function public.update_game_preset(
    p_game_preset_id uuid,
    p_name           text default null,
    p_description    text default null,
    p_body           jsonb default null,
    p_effect_tags    text[] default null,
    p_body_version   int default null,
    p_allow_in_packs boolean default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_clean_tags text[]; v_tag text;
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne'
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

-- ---- custom_engines -----------------------------------------------

create or replace function public.update_custom_engine(
    p_custom_engine_id uuid,
    p_name             text default null,
    p_description      text default null,
    p_body             jsonb default null,
    p_body_version     int default null,
    p_allow_in_packs   boolean default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_recent int;
    v_new_version int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.custom_engines where id = p_custom_engine_id and not is_suppressed;
    if not found then raise exception 'custom engine not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your custom engine';
    end if;

    select (select count(*) from public.presets
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
         + (select count(*) from public.game_presets
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
         + (select count(*) from public.custom_engines
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
      into v_recent;
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.custom_engines set name = btrim(p_name) where id = p_custom_engine_id;
    end if;
    if p_description is not null then
        if length(btrim(p_description)) > 1024 then raise exception 'description too long'; end if;
        update public.custom_engines set description = nullif(btrim(p_description), '') where id = p_custom_engine_id;
    end if;
    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.custom_engines set body = p_body where id = p_custom_engine_id;
    end if;
    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
        update public.custom_engines set body_version = p_body_version where id = p_custom_engine_id;
    end if;
    if p_allow_in_packs is not null then
        update public.custom_engines set allow_in_packs = p_allow_in_packs where id = p_custom_engine_id;
    end if;

    update public.custom_engines
        set content_version = content_version + 1,
            updated_at = now()
        where id = p_custom_engine_id
        returning content_version into v_new_version;

    return jsonb_build_object(
        'id', p_custom_engine_id,
        'content_version', v_new_version);
end;
$$;
