-- pgcrypto lives in the `extensions` schema on Supabase; our SECURITY DEFINER
-- functions need it on the search_path so digest() resolves. Replacing the
-- four affected functions with the corrected search_path; bodies are
-- unchanged byte-for-byte from 0001.

create or replace function _derive_submitter_id(p_ip text)
returns text language plpgsql stable security definer
set search_path = public, extensions, pg_temp as $$
declare v_salt text;
begin
    select value into v_salt from app_config where key = 'submitter_id_salt';
    if v_salt is null then
        v_salt := encode(gen_random_bytes(16), 'hex');
        insert into app_config (key, value)
            values ('submitter_id_salt', v_salt)
            on conflict (key) do nothing;
    end if;
    return encode(digest(coalesce(p_ip, 'unknown') || '|' || v_salt, 'sha256'), 'hex');
end;
$$;

create or replace function _recompute_car_fact_consensus(
    p_game text, p_car_id text, p_fact_type text
) returns void language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    top_payload jsonb; top_distinct int; total_distinct int;
    pos_votes int; neg_votes int; v_suppressed boolean;
    v_current_payload jsonb; v_current_distinct int; v_current_wilson numeric;
    v_payload_hash text; sticky_margin int;
begin
    select is_suppressed, payload, supporting_submissions, wilson_score
        into v_suppressed, v_current_payload, v_current_distinct, v_current_wilson
      from car_fact_consensus
     where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
    if coalesce(v_suppressed, false) then return; end if;

    select payload, count(distinct submitter_id)
      into top_payload, top_distinct
      from car_fact_submissions
     where game = p_game and car_id = p_car_id and fact_type = p_fact_type
     group by payload
     order by count(distinct submitter_id) desc, min(created_at) asc
     limit 1;

    if top_payload is null then
        delete from car_fact_consensus
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
        return;
    end if;

    if v_current_payload is not null and v_current_payload <> top_payload then
        if coalesce(v_current_wilson, 0) >= 0.5 then
            sticky_margin := greatest(3, ceil(coalesce(v_current_distinct, 0) * 0.5)::int);
        elsif coalesce(v_current_distinct, 0) <= 2 then
            sticky_margin := 1;
        elsif coalesce(v_current_distinct, 0) <= 9 then
            sticky_margin := 2;
        else
            sticky_margin := ceil(coalesce(v_current_distinct, 0) * 0.3)::int;
        end if;
        if top_distinct < coalesce(v_current_distinct, 0) + sticky_margin then
            top_payload := v_current_payload;
            top_distinct := coalesce(v_current_distinct, 0);
        end if;
    end if;

    total_distinct := top_distinct;

    v_payload_hash := encode(digest(top_payload::text, 'sha256'), 'hex');
    select count(*) filter (where direction =  1),
           count(*) filter (where direction = -1)
      into pos_votes, neg_votes
      from car_fact_consensus_votes
     where consensus_game = p_game and consensus_car_id = p_car_id
       and consensus_fact_type = p_fact_type and payload_hash = v_payload_hash;

    insert into car_fact_consensus
        (game, car_id, fact_type, payload, confirmations, refutations,
         wilson_score, supporting_submissions, is_suppressed, last_recomputed)
    values
        (p_game, p_car_id, p_fact_type, top_payload,
         coalesce(pos_votes, 0), coalesce(neg_votes, 0),
         wilson_lower_bound(coalesce(pos_votes, 0), coalesce(neg_votes, 0)),
         coalesce(total_distinct, 0), false, now())
    on conflict (game, car_id, fact_type) do update
        set payload                = excluded.payload,
            confirmations          = excluded.confirmations,
            refutations            = excluded.refutations,
            wilson_score           = excluded.wilson_score,
            supporting_submissions = excluded.supporting_submissions,
            last_recomputed        = excluded.last_recomputed
        where car_fact_consensus.is_suppressed = false;
end;
$$;

create or replace function submit_car_fact(
    p_game text, p_car_id text, p_fact_type text, p_payload jsonb,
    p_plugin_version text default null
) returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_canonical jsonb; v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
begin
    if p_game is null or length(trim(p_game)) = 0 then raise exception 'game required'; end if;
    if p_car_id is null or length(trim(p_car_id)) = 0 then raise exception 'car_id required'; end if;
    if length(p_game) > 64 then raise exception 'game too long'; end if;
    if length(p_car_id) > 128 then raise exception 'car_id too long'; end if;

    v_canonical := normalize_car_fact_payload(p_fact_type, p_payload);
    if v_canonical is null then
        raise exception 'invalid payload for fact_type %', p_fact_type;
    end if;

    v_ip := _client_ip();
    v_submitter_id := _derive_submitter_id(v_ip);

    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from car_fact_submissions
     where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour';
    if v_recent >= 30 then raise exception 'rate limit exceeded'; end if;

    insert into car_fact_submissions
        (game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_canonical,
            v_submitter_id, v_ip_hash, p_plugin_version)
    returning id into v_id;

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type);
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id);
end;
$$;

create or replace function vote_car_fact(
    p_game text, p_car_id text, p_fact_type text, p_direction int,
    p_expected_payload_hash text default null
) returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_consensus_payload jsonb; v_payload_hash text;
begin
    if p_direction not in (-1, 1) then raise exception 'direction must be -1 or 1'; end if;

    select payload into v_consensus_payload from car_fact_consensus
     where game = trim(p_game) and car_id = trim(p_car_id) and fact_type = p_fact_type;
    if v_consensus_payload is null then raise exception 'no consensus to vote on'; end if;
    v_payload_hash := encode(digest(v_consensus_payload::text, 'sha256'), 'hex');

    if p_expected_payload_hash is not null and p_expected_payload_hash <> v_payload_hash then
        raise exception 'consensus changed, refetch';
    end if;

    v_ip := _client_ip();
    v_voter_id := _derive_submitter_id(v_ip);

    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from car_fact_consensus_votes
     where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour';
    if v_recent >= 30 then raise exception 'rate limit exceeded'; end if;

    insert into car_fact_consensus_votes
        (consensus_game, consensus_car_id, consensus_fact_type, voter_id,
         payload_hash, source_ip_hash, direction)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_voter_id,
            v_payload_hash, v_ip_hash, p_direction)
    on conflict (consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash)
        do update set direction = excluded.direction,
                      source_ip_hash = excluded.source_ip_hash,
                      updated_at = now();

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type);
    return jsonb_build_object('voter_id', v_voter_id, 'payload_hash', v_payload_hash);
end;
$$;
