-- Stop the per-gear fan-out from eating the submit rate-limit budget.
--
-- The per-user cap (30 submissions/hour) counted RAW rows. Before per-gear a
-- car was ~3 facts (name, layout, redline); now a fully-geared car fans out to
-- the overall 'redline' plus up to 16 'redline_gN' facts, so the same helpful
-- act costs far more of the budget and genuinely-helpful users documenting a
-- few multi-gear cars in a row get gated. The 2-driver consensus floor (not
-- this cap) is the anti-manipulation guard; this cap is only a volume guard.
--
-- Fix: count distinct car-fact EVENTS in the window instead of raw rows, where
-- a car's overall 'redline' and ALL its 'redline_gN' facts collapse to one
-- 'redline' event per (game, car_id, variant_signature). Also exclude the event
-- the current submission belongs to, so adding another gear to a car already
-- submitted this hour is never gated (it is the same act, not new volume).
--
-- The abuse ceiling is UNCHANGED at ~30 distinct car-fact events/hour, which is
-- what it effectively was before per-gear existed (a documented car was a few
-- events). Per-gear depth is now free. Within one event the row fan-out is
-- naturally bounded (overall + g1..g16, GC'd to latest per submitter).
--
-- Body is the current LIVE submit_car_fact with only the rate-limit count
-- changed. Additive / reversible (restore the count(*) form).

CREATE OR REPLACE FUNCTION public.submit_car_fact(p_game text, p_car_id text, p_fact_type text, p_payload jsonb, p_plugin_version text DEFAULT NULL::text, p_variant_signature text DEFAULT ''::text)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
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
    -- Distinct car-fact EVENTS in the last hour (per-gear collapsed to one
    -- 'redline' event per car/variant), excluding the event this submission
    -- belongs to so adding a gear to an already-submitted car is never gated.
    select count(distinct (game, car_id, variant_signature,
             case when fact_type ~ '^redline_g' then 'redline' else fact_type end))
      into v_recent
      from car_fact_submissions
     where submitter_id = v_submitter_id
       and created_at > now() - interval '1 hour'
       and not (game = trim(p_game)
                and car_id = trim(p_car_id)
                and variant_signature = v_variant
                and (case when fact_type   ~ '^redline_g' then 'redline' else fact_type   end)
                  = (case when p_fact_type ~ '^redline_g' then 'redline' else p_fact_type end));
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
$function$;
