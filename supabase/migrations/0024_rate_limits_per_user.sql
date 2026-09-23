-- 0024: make write rate limits spoof-proof.
--
-- The throttles on uploads, votes and car-fact submit/vote counted rows by
-- source_ip_hash, where the IP comes from _client_ip() reading the
-- cf-connecting-ip request header. The PostgREST endpoint is reachable
-- without going through Cloudflare, so a caller can set that header to a
-- fresh value per request and rotate past every per-IP cap. Since all of
-- these paths already require auth.uid() and stamp owner_user_id /
-- submitter_id / voter_id from it, switch the COUNT predicates to that
-- verified identity. auth.uid() comes from the signed JWT and cannot be
-- forged from a header.
--
-- The update_* (edit) functions were already converted to per-owner counts
-- in 0018, so they are untouched here. source_ip_hash is still stored on
-- every row for abuse forensics and submitter_blocked; only the rate-limit
-- predicate changes. Every new predicate is covered by an existing index
-- (presets_owner_idx, *_owner_idx, *_votes_voter_idx,
-- car_fact_submissions_submitter_idx, car_fact_consensus_votes_voter_idx).

-- ===================== uploads (shared budget, 10/hr) =====================

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
    v_allowed_tags text[] := array['engine','revlimiter','roadbumps','tractionloss','gearshift','abs','pitlimiter','drs','collision','audio','airborne'];
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
        'abs','pitlimiter','drs','collision','audio','airborne'
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

create or replace function public.upload_custom_engine(
    p_name text,
    p_body jsonb,
    p_description text default null,
    p_body_version integer default 1,
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
    v_uid uuid;
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
    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then
        raise exception 'body must be a JSON object';
    end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then
        raise exception 'body_version invalid';
    end if;

    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.presets        where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets   where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engines where owner_user_id = v_uid and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 10 then raise exception 'upload rate limit exceeded'; end if;

    insert into public.custom_engines
        (name, author, description, body_version, body,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values (v_name, v_author, v_desc, p_body_version, p_body,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id, 'submitter_id', v_submitter_id,
        'owner_user_id', v_uid, 'author', v_author);
end;
$$;

create or replace function public.upload_custom_engine(
    p_name text,
    p_body jsonb,
    p_description text default null,
    p_body_version integer default 1,
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
    v_name text; v_author text; v_desc text; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then raise exception 'name must be 2..96 chars'; end if;
    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then raise exception 'description too long'; end if;
    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then raise exception 'body must be a JSON object'; end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then raise exception 'submitter blocked'; end if;
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.presets        where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets   where owner_user_id = v_uid and created_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engines where owner_user_id = v_uid and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 10 then raise exception 'upload rate limit exceeded'; end if;
    insert into public.custom_engines
        (name, author, description, body_version, body,
         submitter_id, source_ip_hash, plugin_version, owner_user_id, allow_in_packs)
    values (v_name, v_author, v_desc, p_body_version, p_body,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid, coalesce(p_allow_in_packs, false))
    returning id into v_id;
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id, 'owner_user_id', v_uid, 'author', v_author);
end;
$$;

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
    v_submitter_id text; v_recent int; v_id uuid;
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

-- ===================== votes (shared budget, 60/hr) =====================

create or replace function public.vote_preset(
    p_preset_id uuid,
    p_value     smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric;
    v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then
        raise exception 'value must be -1, 0, or 1';
    end if;
    if not exists (select 1 from public.presets where id = p_preset_id and not is_suppressed) then
        raise exception 'preset not found';
    end if;

    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.preset_votes
        where voter_id = v_voter_id and updated_at > now() - interval '1 hour';
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;

    if p_value = 0 then
        delete from public.preset_votes
         where preset_id = p_preset_id and voter_id = v_voter_id;
    else
        insert into public.preset_votes (preset_id, voter_id, source_ip_hash, value)
        values (p_preset_id, v_voter_id, v_ip_hash, p_value)
        on conflict (preset_id, voter_id) do update
            set value = excluded.value,
                source_ip_hash = excluded.source_ip_hash,
                updated_at = now();
    end if;

    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.preset_votes where preset_id = p_preset_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.presets
        set upvotes = coalesce(v_upvotes, 0),
            downvotes = coalesce(v_downvotes, 0),
            wilson_score = v_wilson
        where id = p_preset_id;

    return jsonb_build_object(
        'upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id,
        'value', p_value);
end;
$$;

create or replace function public.vote_game_preset(
    p_game_preset_id uuid,
    p_value          smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric;
    v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then
        raise exception 'value must be -1, 0, or 1';
    end if;
    if not exists (select 1 from public.game_presets where id = p_game_preset_id and not is_suppressed) then
        raise exception 'game preset not found';
    end if;

    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;

    if p_value = 0 then
        delete from public.game_preset_votes
         where game_preset_id = p_game_preset_id and voter_id = v_voter_id;
    else
        insert into public.game_preset_votes (game_preset_id, voter_id, source_ip_hash, value)
        values (p_game_preset_id, v_voter_id, v_ip_hash, p_value)
        on conflict (game_preset_id, voter_id) do update
            set value = excluded.value,
                source_ip_hash = excluded.source_ip_hash,
                updated_at = now();
    end if;

    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.game_preset_votes where game_preset_id = p_game_preset_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.game_presets
        set upvotes = coalesce(v_upvotes, 0),
            downvotes = coalesce(v_downvotes, 0),
            wilson_score = v_wilson
        where id = p_game_preset_id;

    return jsonb_build_object(
        'upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id,
        'value', p_value);
end;
$$;

create or replace function public.vote_custom_engine(
    p_custom_engine_id uuid,
    p_value            smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric;
    v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.custom_engines where id = p_custom_engine_id and not is_suppressed) then
        raise exception 'custom engine not found';
    end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engine_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;

    if p_value = 0 then
        delete from public.custom_engine_votes
         where custom_engine_id = p_custom_engine_id and voter_id = v_voter_id;
    else
        insert into public.custom_engine_votes (custom_engine_id, voter_id, source_ip_hash, value)
        values (p_custom_engine_id, v_voter_id, v_ip_hash, p_value)
        on conflict (custom_engine_id, voter_id) do update
            set value = excluded.value,
                source_ip_hash = excluded.source_ip_hash,
                updated_at = now();
    end if;

    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.custom_engine_votes where custom_engine_id = p_custom_engine_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.custom_engines
        set upvotes = coalesce(v_upvotes, 0),
            downvotes = coalesce(v_downvotes, 0),
            wilson_score = v_wilson
        where id = p_custom_engine_id;

    return jsonb_build_object(
        'upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id,
        'value', p_value);
end;
$$;

create or replace function public.vote_pack(
    p_pack_id uuid,
    p_value   smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.packs where id = p_pack_id and not is_suppressed) then raise exception 'pack not found'; end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then raise exception 'voter blocked'; end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes        where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes   where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engine_votes where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.pack_votes          where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;
    if p_value = 0 then
        delete from public.pack_votes where pack_id = p_pack_id and voter_id = v_voter_id;
    else
        insert into public.pack_votes (pack_id, voter_id, source_ip_hash, value)
        values (p_pack_id, v_voter_id, v_ip_hash, p_value)
        on conflict (pack_id, voter_id) do update
            set value = excluded.value, source_ip_hash = excluded.source_ip_hash, updated_at = now();
    end if;
    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.pack_votes where pack_id = p_pack_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.packs
        set upvotes = coalesce(v_upvotes, 0), downvotes = coalesce(v_downvotes, 0), wilson_score = v_wilson
        where id = p_pack_id;
    return jsonb_build_object('upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id, 'value', p_value);
end;
$$;

-- ===================== car facts (30/hr) =====================

create or replace function public.submit_car_fact(
    p_game              text,
    p_car_id            text,
    p_fact_type         text,
    p_payload           jsonb,
    p_plugin_version    text  default null,
    p_variant_signature text  default ''
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_canonical jsonb; v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_variant text; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    if p_game is null or length(trim(p_game)) = 0 then raise exception 'game required'; end if;
    if p_car_id is null or length(trim(p_car_id)) = 0 then raise exception 'car_id required'; end if;
    if length(p_game) > 64 then raise exception 'game too long'; end if;
    if length(p_car_id) > 128 then raise exception 'car_id too long'; end if;
    v_variant := coalesce(p_variant_signature, '');
    if length(v_variant) > 64 then raise exception 'variant_signature too long'; end if;

    v_canonical := normalize_car_fact_payload(p_fact_type, p_payload);
    if v_canonical is null then
        raise exception 'invalid payload for fact_type %', p_fact_type;
    end if;

    v_submitter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from car_fact_submissions
        where submitter_id = v_submitter_id and created_at > now() - interval '1 hour';
    if v_recent >= 30 then raise exception 'rate limit exceeded'; end if;

    insert into car_fact_submissions
        (game, car_id, fact_type, payload, submitter_id,
         source_ip_hash, plugin_version, variant_signature)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_canonical,
            v_submitter_id, v_ip_hash, p_plugin_version, v_variant)
    returning id into v_id;

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type, v_variant);
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id);
end;
$$;

create or replace function public.vote_car_fact(
    p_game                  text,
    p_car_id                text,
    p_fact_type             text,
    p_direction             integer,
    p_expected_payload_hash text  default null,
    p_variant_signature     text  default ''
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_consensus_payload jsonb; v_payload_hash text;
    v_variant text; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_direction not in (-1, 1) then raise exception 'direction must be -1 or 1'; end if;
    v_variant := coalesce(p_variant_signature, '');

    select payload into v_consensus_payload from car_fact_consensus
     where game = trim(p_game) and car_id = trim(p_car_id)
       and fact_type = p_fact_type
       and variant_signature = v_variant;
    if v_consensus_payload is null then raise exception 'no consensus to vote on'; end if;
    v_payload_hash := encode(digest(v_consensus_payload::text, 'sha256'), 'hex');

    if p_expected_payload_hash is not null and p_expected_payload_hash <> v_payload_hash then
        raise exception 'consensus changed, refetch';
    end if;

    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from car_fact_consensus_votes
     where voter_id = v_voter_id and updated_at > now() - interval '1 hour';
    if v_recent >= 30 then raise exception 'rate limit exceeded'; end if;

    insert into car_fact_consensus_votes
        (consensus_game, consensus_car_id, consensus_fact_type,
         consensus_variant_signature, voter_id, payload_hash,
         source_ip_hash, direction)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_variant,
            v_voter_id, v_payload_hash, v_ip_hash, p_direction)
    on conflict (consensus_game, consensus_car_id, consensus_fact_type,
                 consensus_variant_signature, voter_id, payload_hash)
        do update set direction = excluded.direction,
                      source_ip_hash = excluded.source_ip_hash,
                      updated_at = now();

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type, v_variant);
    return jsonb_build_object('voter_id', v_voter_id, 'payload_hash', v_payload_hash);
end;
$$;
