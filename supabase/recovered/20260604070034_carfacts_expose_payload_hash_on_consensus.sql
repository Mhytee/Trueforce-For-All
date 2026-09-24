-- Expose payload_hash on car_fact_consensus so vote callers don't have to
-- recompute it client-side (Postgres's canonical jsonb::text whitespace is
-- subtle to match from C#). Recompute populates it whenever consensus is
-- updated; the client echoes it back via vote_car_fact's
-- p_expected_payload_hash CAS guard.

alter table car_fact_consensus
    add column if not exists payload_hash text;

-- Backfill the existing rows (none in production at the moment but good
-- hygiene for re-runs).
update car_fact_consensus
   set payload_hash = encode(digest(payload::text, 'sha256'), 'hex')
 where payload_hash is null;

-- Rewrite recompute to populate payload_hash alongside the other fields.
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
    select is_suppressed, payload, wilson_score
        into v_suppressed, v_current_payload, v_current_wilson
      from car_fact_consensus
     where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
    if coalesce(v_suppressed, false) then return; end if;

    with latest_per_submitter as (
        select distinct on (submitter_id) submitter_id, payload, created_at
          from car_fact_submissions
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type
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
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
        return;
    end if;

    if v_current_payload is not null then
        with latest_per_submitter as (
            select distinct on (submitter_id) submitter_id, payload
              from car_fact_submissions
             where game = p_game and car_id = p_car_id and fact_type = p_fact_type
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
       and consensus_fact_type = p_fact_type and payload_hash = v_payload_hash;

    insert into car_fact_consensus
        (game, car_id, fact_type, payload, payload_hash, confirmations, refutations,
         wilson_score, supporting_submissions, is_suppressed, last_recomputed)
    values
        (p_game, p_car_id, p_fact_type, top_payload, v_payload_hash,
         coalesce(pos_votes, 0), coalesce(neg_votes, 0),
         wilson_lower_bound(coalesce(pos_votes, 0), coalesce(neg_votes, 0)),
         coalesce(total_distinct, 0), false, now())
    on conflict (game, car_id, fact_type) do update
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