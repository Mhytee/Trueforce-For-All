-- Replace the consensus candidate selector so a submitter's *latest* submission
-- counts as their current endorsement, and ties between equally-supported
-- payloads break on the most recently active payload rather than the earliest.
--
-- Old model: count distinct submitter_id per payload across ALL submissions
-- (so a user who submitted {cyl:8} then {cyl:2} would count as +1 to BOTH);
-- tied payloads break on min(created_at) asc (earliest wins).
--
-- New model: per submitter, only the row with max(created_at) for this
-- (game, car_id, fact_type) is treated as their endorsement; tied payloads
-- break on max(latest_endorsement) desc (most-recent activity wins).
-- v_current_distinct is recomputed from the same latest-per-submitter view
-- so the stickiness check uses the LIVE endorser count, not the stale
-- supporting_submissions stored on the consensus row.
--
-- Effects:
--   * Self-correction works: a single user changing their mind moves the
--     candidate, and the now-empty-supporter incumbent loses stickiness
--     so the new payload becomes consensus.
--   * Community migration works: as real submitters move to a new value,
--     the incumbent's live supporter count drops while the challenger
--     grows.
--   * Sybil flips still blocked by the existing stickiness margin (scales
--     with incumbent support + Wilson score).

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

    -- Latest endorsement per submitter. distinct on (submitter_id) keeps the
    -- row with the highest created_at when sorted by submitter_id, created_at desc.
    with latest_per_submitter as (
        select distinct on (submitter_id) submitter_id, payload, created_at
          from car_fact_submissions
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type
         order by submitter_id, created_at desc
    ),
    payload_supports as (
        select payload,
               count(*)::int       as distinct_supporters,
               max(created_at)     as latest_activity
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

    -- The incumbent's CURRENT endorser count (not the stored value),
    -- computed under the same latest-per-submitter model so stickiness
    -- reflects who's actually still endorsing it. Zero when the incumbent
    -- has no current supporters (everyone moved away).
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

-- Force a recompute of every existing consensus row under the new model
-- so the test row from earlier reflects "latest wins" immediately.
do $$
declare r record;
begin
    for r in
        select distinct game, car_id, fact_type
          from car_fact_submissions
    loop
        perform _recompute_car_fact_consensus(r.game, r.car_id, r.fact_type);
    end loop;
end $$;