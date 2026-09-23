-- Car facts go account-free (owner decision 2026-07-16).
--
-- Adoption reality: community car facts sat behind two walls. Reads were
-- locked to authenticated (0027) and submit_car_fact required auth.uid()
-- (0009), so on default settings the whole fact pipeline was dark, and in
-- practice the project owner was the only lifetime submitter. Car facts
-- never show a username to other users, so an account should not be
-- required to contribute:
--
--   1. car_fact_consensus becomes anon-READABLE again (a partial revert of
--      0027, for this one table only). The client still gates fetches
--      behind the CommunityEnabled toggle; presets and votes stay
--      authenticated.
--   2. submit_car_fact accepts an anonymous submitter as a FALLBACK: a
--      client-minted random id (p_anon_id, new trailing arg), stored as
--      'anon:<id>' in submitter_id. auth.uid() still WINS when the request
--      carries a user token, so signed-in submissions stay keyed to the
--      account (car-fact achievements via compute_member_metrics, the
--      owner's moderation view, export/delete_my_account coverage). Old
--      clients keep working unchanged; old signed-out clients keep
--      getting 'sign-in required'.
--   3. Anti-abuse for the anon path: the existing 30 distinct car-fact
--      events/hour cap now applies per anon id AND per source ip hash
--      (anon ids are free to mint, so the ip cap is the backstop), and
--      submitter_blocked also matches an 'ip:<sha256>' row so moderation
--      can ban by ip hash, not just by the self-chosen submitter id.
--
-- Consensus logic (_recompute_car_fact_consensus, the 2-driver confirm
-- floor, per-gear handling) is deliberately untouched.
-- compute_member_metrics needs no change: its uuid-shape regex on
-- submitter_id already excludes 'anon:<id>' rows safely.

-- ---- 1. anon reads on consensus ----------------------------------------

grant select on public.car_fact_consensus to anon;

drop policy if exists car_fact_consensus_select_anon on public.car_fact_consensus;
create policy car_fact_consensus_select_anon on public.car_fact_consensus
    for select to anon using (true);

-- ---- 2 + 3. anon-capable submit ----------------------------------------

-- The signature gains a trailing defaulted arg. CREATE OR REPLACE would
-- leave the old 6-arg overload alongside a new 7-arg one and make every
-- PostgREST call ambiguous, so drop + recreate.
drop function if exists public.submit_car_fact(text, text, text, jsonb, text, text);

create function public.submit_car_fact(
    p_game text,
    p_car_id text,
    p_fact_type text,
    p_payload jsonb,
    p_plugin_version text default null::text,
    p_variant_signature text default ''::text,
    p_anon_id text default null::text)
 returns jsonb
 language plpgsql
 security definer
 set search_path to 'public', 'extensions', 'pg_temp'
as $function$
declare
    v_canonical jsonb; v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_variant text; v_uid uuid; v_anon boolean := false;
begin
    -- Identity: the signed-in account when the request carries a user token
    -- (keeps achievements, moderation, and export/delete coverage working),
    -- else the client-minted anonymous id, else reject. Either way other
    -- users never see who submitted; only consensus counts are exposed.
    v_uid := auth.uid();
    if v_uid is not null then
        v_submitter_id := v_uid::text;
    elsif p_anon_id is not null and length(trim(p_anon_id)) > 0 then
        -- Strict shape check so junk never reaches submitter_id. The plugin
        -- sends a 32-char hex GUID; allow simple dashed forms too.
        if trim(p_anon_id) !~ '^[A-Za-z0-9-]{8,64}$' then
            raise exception 'invalid anon id';
        end if;
        v_submitter_id := 'anon:' || trim(p_anon_id);
        v_anon := true;
    else
        raise exception 'sign-in required';
    end if;

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
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');

    -- Moderation ban: by submitter id (works for 'anon:<id>' rows too) or
    -- by ip hash via an 'ip:<sha256>' row, since an anon id costs nothing
    -- to rotate.
    if exists (select 1 from submitter_blocked
               where submitter_id in (v_submitter_id, 'ip:' || v_ip_hash)) then
        raise exception 'submitter blocked';
    end if;

    -- Volume cap: distinct car-fact EVENTS in the last hour (per-gear rows
    -- collapse to one 'redline' event per car/variant), excluding the event
    -- this submission belongs to. See 0066 for the collapse rationale.
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

    -- Anon backstop: the same event cap keyed on the source ip hash across
    -- ALL submitter ids, so rotating anon ids buys no extra budget. Not
    -- applied to the signed-in path (accounts already cost an email, and a
    -- shared IP must not let one anon flooder starve signed-in users).
    if v_anon then
        select count(distinct (game, car_id, variant_signature,
                 case when fact_type ~ '^redline_g' then 'redline' else fact_type end))
          into v_recent
          from car_fact_submissions
         where source_ip_hash = v_ip_hash
           and created_at > now() - interval '1 hour'
           and not (game = trim(p_game)
                    and car_id = trim(p_car_id)
                    and variant_signature = v_variant
                    and (case when fact_type   ~ '^redline_g' then 'redline' else fact_type   end)
                      = (case when p_fact_type ~ '^redline_g' then 'redline' else p_fact_type end));
        if v_recent >= 30 then raise exception 'rate limit exceeded'; end if;
    end if;

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

-- Recreate the explicit grant set the old function had (0026-style lockdown),
-- now including anon.
revoke all on function public.submit_car_fact(text, text, text, jsonb, text, text, text) from public;
grant execute on function public.submit_car_fact(text, text, text, jsonb, text, text, text)
    to anon, authenticated, service_role;
