-- 0085: a ban raised from a report must keep the REPORTED item removed even
-- after a temp ban expires. The reported item becomes a removal (is_suppressed
-- true, suppressed_by_ban FALSE -> never auto-restored, appealable on its own);
-- the rest of the account's content is the temporary suspension
-- (suppressed_by_ban TRUE -> restored by the reaper when the ban lifts).

drop function if exists public.mod_ban_user(uuid, timestamptz, text, text);
create or replace function public.mod_ban_user(
    p_user_id uuid, p_banned_until timestamptz default null, p_reason text default null,
    p_actor text default null, p_offending_type text default null, p_offending_id uuid default null)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_uid_text text := p_user_id::text; v_perm boolean := p_banned_until is null;
    v_dev int := 0; v_alts int := 0; v_cf int := 0; v_votes int := 0;
    v_p int := 0; v_g int := 0; v_e int := 0; v_k int := 0; v_tmp int := 0; v_msg text; r record;
    v_offtable text; v_offname text; v_removed boolean := false;
begin
    if p_user_id is null then return jsonb_build_object('error', 'no account on this item'); end if;

    insert into public.submitter_blocked (submitter_id, reason, banned_until)
        values (v_uid_text, coalesce(p_reason, 'mod ban'), p_banned_until)
    on conflict (submitter_id) do update set reason = excluded.reason,
        banned_until = case when public.submitter_blocked.banned_until is null then null
                            when excluded.banned_until is null then null
                            else greatest(public.submitter_blocked.banned_until, excluded.banned_until) end;

    for r in select distinct device_fp from public.session_metadata where user_id = p_user_id and device_fp is not null loop
        insert into public.banned_devices (device_fp, banned_until, reason, banned_by)
            values (r.device_fp, p_banned_until, coalesce(p_reason, 'mod ban'), p_actor)
        on conflict (device_fp) do update set banned_until = case
                when public.banned_devices.banned_until is null then null
                when excluded.banned_until is null then null
                else greatest(public.banned_devices.banned_until, excluded.banned_until) end,
            reason = excluded.reason, banned_by = excluded.banned_by;
        v_dev := v_dev + 1;
        insert into public.submitter_blocked (submitter_id, reason, banned_until)
            select distinct sm.user_id::text, 'device ban (alt)', p_banned_until
              from public.session_metadata sm where sm.device_fp = r.device_fp and sm.user_id <> p_user_id
        on conflict (submitter_id) do nothing;
        get diagnostics v_tmp = row_count; v_alts := v_alts + v_tmp;
    end loop;

    -- (1) reported item -> a removal that survives the ban (suppressed_by_ban false).
    v_offtable := case p_offending_type
        when 'preset' then 'presets' when 'game_preset' then 'game_presets'
        when 'custom_engine' then 'custom_engines' when 'pack' then 'packs' end;
    if v_offtable is not null and p_offending_id is not null then
        execute format('update public.%I set is_suppressed = true, suppressed_by_ban = false where id = $1', v_offtable) using p_offending_id;
        execute format('select name from public.%I where id = $1', v_offtable) into v_offname using p_offending_id;
        insert into public.moderation_notices (user_id, target_type, target_id, kind, message, created_by)
          values (p_user_id, p_offending_type, p_offending_id, 'removal',
                  'Your upload "' || coalesce(v_offname, '') || '" was removed after a report. You can request a review if you believe this was a mistake.', p_actor);
        v_removed := true;
    end if;

    -- (2) suspend the REST (auto-restored on temp-ban expiry). The reported item
    -- is already suppressed above, so "not is_suppressed" skips it.
    update public.presets       set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_p = row_count;
    update public.game_presets   set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_g = row_count;
    update public.custom_engines set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_e = row_count;
    update public.packs          set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_k = row_count;

    if v_perm then
        create temp table _affected (game text, car_id text, fact_type text, variant_signature text) on commit drop;
        insert into _affected select distinct game, car_id, fact_type, variant_signature from public.car_fact_submissions where submitter_id = v_uid_text;
        insert into _affected select distinct consensus_game, consensus_car_id, consensus_fact_type, consensus_variant_signature from public.car_fact_consensus_votes where voter_id = v_uid_text;
        insert into public.car_fact_submissions_archive
            (id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature, archived_user)
            select id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature, p_user_id
              from public.car_fact_submissions where submitter_id = v_uid_text;
        delete from public.car_fact_submissions where submitter_id = v_uid_text; get diagnostics v_cf = row_count;
        insert into public.car_fact_consensus_votes_archive
            (consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature, archived_user)
            select consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature, p_user_id
              from public.car_fact_consensus_votes where voter_id = v_uid_text;
        delete from public.car_fact_consensus_votes where voter_id = v_uid_text; get diagnostics v_votes = row_count;
        for r in select distinct game, car_id, fact_type, variant_signature from _affected loop
            perform public._recompute_car_fact_consensus(r.game, r.car_id, r.fact_type, r.variant_signature);
        end loop;
    end if;

    v_msg := case when v_perm
        then 'Your account was permanently banned from community uploads and your content was removed. You can request a review if you believe this was a mistake.'
        else 'Your account is suspended from community uploads. Your other uploads are hidden until the suspension ends; the reported item was removed separately. You can request a review if you believe this was a mistake.' end;
    insert into public.moderation_notices (user_id, kind, message, created_by) values (p_user_id, 'ban', v_msg, p_actor);

    return jsonb_build_object('banned_user', v_uid_text, 'permanent', v_perm, 'banned_until', p_banned_until,
        'reported_item_removed', v_removed, 'devices_banned', v_dev, 'alts_blocked', v_alts,
        'presets', v_p, 'game_presets', v_g, 'custom_engines', v_e, 'packs', v_k,
        'car_facts_archived', v_cf, 'votes_archived', v_votes);
end;
$$;
revoke all on function public.mod_ban_user(uuid, timestamptz, text, text, text, uuid) from public, anon, authenticated;
grant  execute on function public.mod_ban_user(uuid, timestamptz, text, text, text, uuid) to service_role;
