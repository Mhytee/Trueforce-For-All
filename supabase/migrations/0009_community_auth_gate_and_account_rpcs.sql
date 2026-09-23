-- Wave 1: lock all submissions/votes/uploads behind auth.uid(); add the
-- new RPCs Wave 2's UI needs (my_presets, account_stats, export,
-- delete account, vote retraction).
-- Mirrors community_auth_gate_and_account_rpcs applied live.

-- ---- 1. Auth-gate submit_car_fact ----------------------------------
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
        where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour';
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

-- ---- 2. Auth-gate vote_car_fact ------------------------------------
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
     where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour';
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

-- ---- 3. Auth-gate upload_preset ------------------------------------
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
        where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour';
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

-- ---- 4. vote_preset: auth.uid() voter_id + value=0 retraction -------
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
        where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour';
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

-- ---- 5. get_my_presets ---------------------------------------------
create or replace function public.get_my_presets(
    p_sort text default 'newest',
    p_limit int default 100
) returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    if p_sort = 'top' then
        return query
            select jsonb_build_object(
                'id', id, 'name', name, 'description', description,
                'game', game, 'car_id', car_id, 'effect_tags', effect_tags,
                'upvotes', upvotes, 'downvotes', downvotes,
                'wilson_score', wilson_score, 'downloads', downloads,
                'created_at', created_at, 'is_suppressed', is_suppressed)
              from public.presets
             where owner_user_id = v_uid
             order by wilson_score desc, created_at desc
             limit greatest(1, least(p_limit, 500));
    elsif p_sort = 'downloads' then
        return query
            select jsonb_build_object(
                'id', id, 'name', name, 'description', description,
                'game', game, 'car_id', car_id, 'effect_tags', effect_tags,
                'upvotes', upvotes, 'downvotes', downvotes,
                'wilson_score', wilson_score, 'downloads', downloads,
                'created_at', created_at, 'is_suppressed', is_suppressed)
              from public.presets
             where owner_user_id = v_uid
             order by downloads desc, created_at desc
             limit greatest(1, least(p_limit, 500));
    else
        return query
            select jsonb_build_object(
                'id', id, 'name', name, 'description', description,
                'game', game, 'car_id', car_id, 'effect_tags', effect_tags,
                'upvotes', upvotes, 'downvotes', downvotes,
                'wilson_score', wilson_score, 'downloads', downloads,
                'created_at', created_at, 'is_suppressed', is_suppressed)
              from public.presets
             where owner_user_id = v_uid
             order by created_at desc
             limit greatest(1, least(p_limit, 500));
    end if;
end;
$$;

-- ---- 6. get_my_votes -----------------------------------------------
create or replace function public.get_my_votes(p_preset_ids uuid[])
returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then return; end if;
    if p_preset_ids is null or array_length(p_preset_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object('preset_id', preset_id, 'value', value)
          from public.preset_votes
         where voter_id = v_uid::text
           and preset_id = any(p_preset_ids);
end;
$$;

-- ---- 7. get_account_stats ------------------------------------------
create or replace function public.get_account_stats()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_email text; v_created_at timestamptz; v_username text;
    v_upload_count int; v_downloads bigint; v_upvotes bigint; v_downvotes bigint;
begin
    v_uid := auth.uid();
    if v_uid is null then return jsonb_build_object('signed_in', false); end if;
    select email, created_at into v_email, v_created_at from auth.users where id = v_uid;
    select username into v_username from public.profiles where id = v_uid;
    select count(*), coalesce(sum(downloads), 0),
           coalesce(sum(upvotes), 0), coalesce(sum(downvotes), 0)
      into v_upload_count, v_downloads, v_upvotes, v_downvotes
      from public.presets where owner_user_id = v_uid and not is_suppressed;
    return jsonb_build_object(
        'signed_in', true, 'user_id', v_uid, 'email', v_email,
        'username', v_username, 'created_at', v_created_at,
        'upload_count', v_upload_count,
        'downloads_received', v_downloads,
        'upvotes_received', v_upvotes,
        'downvotes_received', v_downvotes);
end;
$$;

-- ---- 8. export_my_data ---------------------------------------------
create or replace function public.export_my_data()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_result jsonb;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_result := jsonb_build_object(
        'exported_at', now(),
        'user_id', v_uid,
        'profile', (select to_jsonb(p) from public.profiles p where p.id = v_uid),
        'presets', coalesce(
            (select jsonb_agg(to_jsonb(pr)) from public.presets pr where pr.owner_user_id = v_uid),
            '[]'::jsonb),
        'votes_cast_on_presets', coalesce(
            (select jsonb_agg(to_jsonb(pv)) from public.preset_votes pv where pv.voter_id = v_uid::text),
            '[]'::jsonb),
        'car_fact_submissions', coalesce(
            (select jsonb_agg(to_jsonb(s)) from car_fact_submissions s where s.submitter_id = v_uid::text),
            '[]'::jsonb),
        'car_fact_votes', coalesce(
            (select jsonb_agg(to_jsonb(v)) from car_fact_consensus_votes v where v.voter_id = v_uid::text),
            '[]'::jsonb)
    );
    return v_result;
end;
$$;

-- ---- 9. delete_my_account ------------------------------------------
create or replace function public.delete_my_account()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_username text; v_upload_count int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select username into v_username from public.profiles where id = v_uid;
    select count(*) into v_upload_count from public.presets
        where owner_user_id = v_uid and not is_suppressed;

    -- Anonymize anything that referenced the user via plain text ids;
    -- the FK on presets.owner_user_id is ON DELETE SET NULL so the
    -- preset rows themselves stay (already-downloaded users keep them).
    update public.preset_votes set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
                                   source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.car_fact_consensus_votes set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
                                                source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.car_fact_submissions set submitter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
                                            source_ip_hash = 'redacted'
        where submitter_id = v_uid::text;

    delete from auth.users where id = v_uid;
    return jsonb_build_object(
        'deleted', true,
        'username_was', v_username,
        'uploads_orphaned', v_upload_count);
end;
$$;
