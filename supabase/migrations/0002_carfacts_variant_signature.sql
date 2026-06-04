-- Add variant_signature so per-variant consensus works for games that
-- reuse the same car_id across in-game engine swaps (Forza). Empty
-- string '' is the sentinel for "no variant discriminator known" and
-- matches the pre-variant single-row behavior on non-Forza games. This
-- migration mirrors what was applied live to the remote project
-- (carfacts_variant_signature) so a fresh `supabase db reset` reproduces
-- the same schema.

alter table car_fact_submissions
  add column if not exists variant_signature text not null default '';

alter table car_fact_consensus
  add column if not exists variant_signature text not null default '';

alter table car_fact_consensus_votes
  add column if not exists consensus_variant_signature text not null default '';

-- 64-char cap handles "cyl=16;maxrpm=25000;redline=25000" plus future
-- additions.
alter table car_fact_submissions
  add constraint car_fact_submissions_variant_len_chk
  check (length(variant_signature) <= 64);
alter table car_fact_consensus
  add constraint car_fact_consensus_variant_len_chk
  check (length(variant_signature) <= 64);

-- Rebuild PKs and the votes FK so variant_signature is part of the
-- consensus identity. Order matters: drop the FK first, then the PKs,
-- then add new PKs, then re-add the FK.
alter table car_fact_consensus_votes
  drop constraint car_fact_consensus_votes_consensus_game_consensus_car_id_c_fkey;

alter table car_fact_consensus_votes
  drop constraint car_fact_consensus_votes_pkey;

alter table car_fact_consensus
  drop constraint car_fact_consensus_pkey;

alter table car_fact_consensus
  add constraint car_fact_consensus_pkey
  primary key (game, car_id, fact_type, variant_signature);

alter table car_fact_consensus_votes
  add constraint car_fact_consensus_votes_pkey
  primary key (consensus_game, consensus_car_id, consensus_fact_type,
               consensus_variant_signature, voter_id, payload_hash);

alter table car_fact_consensus_votes
  add constraint car_fact_consensus_votes_consensus_fkey
  foreign key (consensus_game, consensus_car_id, consensus_fact_type,
               consensus_variant_signature)
  references car_fact_consensus (game, car_id, fact_type, variant_signature)
  on delete cascade;

-- Index for variant-aware submissions lookups (consensus recompute
-- filters by variant). The implicit (game, car_id, fact_type) index is
-- no longer sufficient on its own.
create index if not exists car_fact_submissions_variant_idx
  on car_fact_submissions (game, car_id, fact_type, variant_signature);

-- Replace functions to be variant-aware. The new optional parameter
-- defaults to '' so clients calling the old signature still work during
-- rollout.

drop function if exists public.submit_car_fact(text, text, text, jsonb, text);

create function public.submit_car_fact(
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
    v_variant text;
begin
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
        (game, car_id, fact_type, payload, submitter_id,
         source_ip_hash, plugin_version, variant_signature)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_canonical,
            v_submitter_id, v_ip_hash, p_plugin_version, v_variant)
    returning id into v_id;

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type, v_variant);
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id);
end;
$$;

drop function if exists public.vote_car_fact(text, text, text, integer, text);

create function public.vote_car_fact(
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
    v_variant text;
begin
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

drop function if exists public._recompute_car_fact_consensus(text, text, text);

create function public._recompute_car_fact_consensus(
    p_game text, p_car_id text, p_fact_type text,
    p_variant_signature text default ''
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    top_payload jsonb; top_distinct int; total_distinct int;
    pos_votes int; neg_votes int; v_suppressed boolean;
    v_current_payload jsonb; v_current_distinct int; v_current_wilson numeric;
    v_payload_hash text; sticky_margin int;
    v_variant text;
begin
    v_variant := coalesce(p_variant_signature, '');
    select is_suppressed, payload, wilson_score
        into v_suppressed, v_current_payload, v_current_wilson
      from car_fact_consensus
     where game = p_game and car_id = p_car_id and fact_type = p_fact_type
       and variant_signature = v_variant;
    if coalesce(v_suppressed, false) then return; end if;

    with latest_per_submitter as (
        select distinct on (submitter_id) submitter_id, payload, created_at
          from car_fact_submissions
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type
           and variant_signature = v_variant
         order by submitter_id, created_at desc
    ),
    payload_supports as (
        select payload,
               count(*)::int   as distinct_supporters,
               max(created_at) as latest_activity
          from latest_per_submitter
         group by payload
    )
    select payload, distinct_supporters
      into top_payload, top_distinct
      from payload_supports
     order by distinct_supporters desc, latest_activity desc
     limit 1;

    if top_payload is null then
        delete from car_fact_consensus
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type
           and variant_signature = v_variant;
        return;
    end if;

    if v_current_payload is not null then
        with latest_per_submitter as (
            select distinct on (submitter_id) submitter_id, payload
              from car_fact_submissions
             where game = p_game and car_id = p_car_id and fact_type = p_fact_type
               and variant_signature = v_variant
             order by submitter_id, created_at desc
        )
        select count(*)::int into v_current_distinct
          from latest_per_submitter
         where payload = v_current_payload;
    else
        v_current_distinct := 0;
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
       and consensus_fact_type = p_fact_type
       and consensus_variant_signature = v_variant
       and payload_hash = v_payload_hash;

    insert into car_fact_consensus
        (game, car_id, fact_type, variant_signature,
         payload, payload_hash, confirmations, refutations,
         wilson_score, supporting_submissions, is_suppressed, last_recomputed)
    values
        (p_game, p_car_id, p_fact_type, v_variant,
         top_payload, v_payload_hash,
         coalesce(pos_votes, 0), coalesce(neg_votes, 0),
         wilson_lower_bound(coalesce(pos_votes, 0), coalesce(neg_votes, 0)),
         coalesce(total_distinct, 0), false, now())
    on conflict (game, car_id, fact_type, variant_signature) do update
        set payload                = excluded.payload,
            payload_hash           = excluded.payload_hash,
            confirmations          = excluded.confirmations,
            refutations            = excluded.refutations,
            wilson_score           = excluded.wilson_score,
            supporting_submissions = excluded.supporting_submissions,
            last_recomputed        = excluded.last_recomputed
        where car_fact_consensus.is_suppressed = false;
end;
$$;
