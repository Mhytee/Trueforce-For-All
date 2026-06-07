-- Edit rate-limit predicate fix.
--
-- The original update_preset / update_game_preset / update_custom_engine /
-- update_pack RPCs counted recent rows by source_ip_hash + created_at,
-- which incorrectly throttled fresh uploads against the per-hour edit
-- budget (a user who just uploaded 5 presets would be locked out of the
-- next 25 edits for an hour, even if they never actually edited
-- anything).
--
-- The fix counts actual edits per signed-in user:
--   owner_user_id = auth.uid()
--   AND updated_at > now() - interval '1 hour'
--   AND updated_at <> created_at
--
-- updated_at <> created_at filters out the row inserted by the upload
-- path (where the insert default leaves them equal until the first
-- update bumps updated_at). Switching from IP-hash to owner_user_id
-- also stops innocent IP-sharing (households, NAT) from cross-counting
-- between users.
--
-- All four RPCs already require auth.uid() upstream of this check, so
-- the new predicate has no NULL ambiguity.
--
-- Re-runnable via CREATE OR REPLACE.

-- ---- presets ------------------------------------------------------

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

    -- Count actual edits in the last hour for this user. Excludes
    -- fresh uploads where updated_at = created_at.
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
    p_body_version   int default null
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

    -- Count actual edits across this user's car + game preset rows.
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
    p_body_version     int default null
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

-- ---- packs --------------------------------------------------------

create or replace function public.update_pack(
    p_pack_id        uuid,
    p_name           text default null,
    p_description    text default null,
    p_author_version text default null,
    p_body           jsonb default null,
    p_entry_count    int default null,
    p_body_version   int default null
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
      from public.packs where id = p_pack_id and not is_suppressed;
    if not found then raise exception 'pack not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your pack';
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
         + (select count(*) from public.packs
              where owner_user_id = v_uid
                and updated_at > now() - interval '1 hour'
                and updated_at <> created_at)
      into v_recent;
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.packs set name = btrim(p_name) where id = p_pack_id;
    end if;
    if p_description is not null then
        if length(btrim(p_description)) > 1024 then raise exception 'description too long'; end if;
        update public.packs set description = nullif(btrim(p_description), '') where id = p_pack_id;
    end if;
    if p_author_version is not null then
        if length(btrim(p_author_version)) > 32 then raise exception 'author_version too long'; end if;
        update public.packs set author_version = nullif(btrim(p_author_version), '') where id = p_pack_id;
    end if;
    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.packs set body = p_body where id = p_pack_id;
    end if;
    if p_entry_count is not null then
        if p_entry_count < 0 or p_entry_count > 10000 then raise exception 'entry_count out of range'; end if;
        update public.packs set entry_count = p_entry_count where id = p_pack_id;
    end if;
    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
        update public.packs set body_version = p_body_version where id = p_pack_id;
    end if;

    update public.packs
        set content_version = content_version + 1,
            updated_at = now()
        where id = p_pack_id
        returning content_version into v_new_version;

    return jsonb_build_object(
        'id', p_pack_id,
        'content_version', v_new_version);
end;
$$;

-- ---- supporting indexes -------------------------------------------
--
-- The new rate-limit predicates filter by (owner_user_id, updated_at).
-- Existing indexes on these four tables are keyed on (owner_user_id,
-- created_at desc) (or similar), so a count(*) over the new predicate
-- would degrade to an index-scan-with-filter or a seq scan as user
-- volume grows. Add covering indexes; CREATE INDEX IF NOT EXISTS keeps
-- this migration re-runnable.

create index if not exists presets_owner_updated_idx
    on public.presets (owner_user_id, updated_at desc);

create index if not exists game_presets_owner_updated_idx
    on public.game_presets (owner_user_id, updated_at desc);

create index if not exists custom_engines_owner_updated_idx
    on public.custom_engines (owner_user_id, updated_at desc);

create index if not exists packs_owner_updated_idx
    on public.packs (owner_user_id, updated_at desc);
