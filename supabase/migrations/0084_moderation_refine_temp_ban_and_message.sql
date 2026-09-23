-- 0084: refine moderation enforcement.
--   * Temp ban (banned_until set) = full suspension: hide ALL the user's uploads
--     for the window, then auto-restore them when the ban expires. Car facts are
--     left in place (they're community consensus, not on-display content).
--   * Permanent ban (banned_until null) = purge: hide all uploads AND archive
--     car facts; restored only by a moderator reinstate.
--   * Content hidden by a ban is tagged suppressed_by_ban so the expiry reaper
--     restores exactly that, and never un-hides something removed individually.
--   * mod_hide gains an optional moderator message shown to the user.

-- ---- 1. tag: hidden-by-ban (vs removed individually) ------------------------
alter table public.presets        add column if not exists suppressed_by_ban boolean not null default false;
alter table public.game_presets   add column if not exists suppressed_by_ban boolean not null default false;
alter table public.custom_engines add column if not exists suppressed_by_ban boolean not null default false;
alter table public.packs          add column if not exists suppressed_by_ban boolean not null default false;

-- ---- 2. mod_hide: optional moderator message -------------------------------
drop function if exists public.mod_hide(uuid, text, text);
create or replace function public.mod_hide(
    p_report_id uuid,
    p_kind      text,
    p_actor     text default null,
    p_message   text default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_rep record; v_table text; v_owner uuid; v_name text; v_msg text; v_label text;
    v_custom text := nullif(btrim(coalesce(p_message, '')), '');
    v_base text; v_default text;
begin
    if p_kind not in ('warn', 'removal') then raise exception 'invalid kind %', p_kind; end if;
    select target_type, target_id, category into v_rep from public.report_flags where id = p_report_id;
    if not found then return jsonb_build_object('error', 'report not found'); end if;
    v_table := case v_rep.target_type
        when 'preset' then 'presets' when 'game_preset' then 'game_presets'
        when 'custom_engine' then 'custom_engines' when 'pack' then 'packs' end;
    if v_table is null then return jsonb_build_object('error', 'bad target_type'); end if;
    execute format('update public.%I set is_suppressed = true where id = $1', v_table) using v_rep.target_id;
    execute format('select owner_user_id, name from public.%I where id = $1', v_table) into v_owner, v_name using v_rep.target_id;
    v_label := case v_rep.target_type
        when 'preset' then 'car preset' when 'game_preset' then 'game preset'
        when 'custom_engine' then 'custom engine' else 'pack' end;
    v_base := 'Your ' || v_label || ' "' || coalesce(v_name, '') || '" was '
           || (case p_kind when 'warn' then 'hidden' else 'removed' end) || ' after a report.';
    v_default := case p_kind
        when 'warn' then 'Please fix it (for example, rename it) and request a review to restore it.'
        else 'You can request a review if you believe this was a mistake.' end;
    v_msg := v_base || ' ' || coalesce(v_custom, v_default);

    if v_owner is not null then
        insert into public.moderation_notices (user_id, target_type, target_id, kind, reason_category, message, created_by)
        values (v_owner, v_rep.target_type, v_rep.target_id, p_kind, v_rep.category, v_msg, p_actor);
    end if;
    update public.report_flags
       set status = case p_kind when 'warn' then 'warned' else 'removed' end, resolved_by = p_actor, resolved_at = now()
     where id = p_report_id;
    return jsonb_build_object('ok', true, 'kind', p_kind, 'owner', v_owner, 'name', v_name, 'notified', v_owner is not null);
end;
$$;
revoke all on function public.mod_hide(uuid, text, text, text) from public, anon, authenticated;
grant  execute on function public.mod_hide(uuid, text, text, text) to service_role;

-- ---- 3. mod_ban_user: temp = suspend all, perm = purge ----------------------
-- Scope is now derived from duration (temp vs permanent), so the old p_scope arg
-- is gone. Drop the 5-arg version and define the 4-arg one.
drop function if exists public.mod_ban_user(uuid, text, timestamptz, text, text);
create or replace function public.mod_ban_user(
    p_user_id      uuid,
    p_banned_until timestamptz default null,   -- null = permanent
    p_reason       text        default null,
    p_actor        text        default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_uid_text text := p_user_id::text;
    v_perm boolean := p_banned_until is null;
    v_dev int := 0; v_alts int := 0; v_cf int := 0; v_votes int := 0;
    v_p int := 0; v_g int := 0; v_e int := 0; v_k int := 0; v_tmp int := 0;
    v_msg text; r record;
begin
    if p_user_id is null then return jsonb_build_object('error', 'no account on this item'); end if;

    -- account block (the lever every write RPC already checks)
    insert into public.submitter_blocked (submitter_id, reason, banned_until)
        values (v_uid_text, coalesce(p_reason, 'mod ban'), p_banned_until)
    on conflict (submitter_id) do update set reason = excluded.reason,
        banned_until = case when public.submitter_blocked.banned_until is null then null
                            when excluded.banned_until is null then null
                            else greatest(public.submitter_blocked.banned_until, excluded.banned_until) end;

    -- device block + pull existing alts on those machines
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

    -- Hide ALL their uploads (temp + perm both). Tag them so the reaper restores
    -- exactly these on temp-ban expiry; only touch currently-visible items.
    update public.presets       set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_p = row_count;
    update public.game_presets   set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_g = row_count;
    update public.custom_engines set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_e = row_count;
    update public.packs          set is_suppressed = true, suppressed_by_ban = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_k = row_count;

    -- Permanent only: also purge car facts (archived, so a reinstate can restore).
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
        else 'Your account is suspended from community uploads and your uploads are hidden until the suspension ends. You can request a review if you believe this was a mistake.' end;
    insert into public.moderation_notices (user_id, kind, message, created_by) values (p_user_id, 'ban', v_msg, p_actor);

    return jsonb_build_object('banned_user', v_uid_text, 'permanent', v_perm, 'banned_until', p_banned_until,
        'devices_banned', v_dev, 'alts_blocked', v_alts, 'presets', v_p, 'game_presets', v_g,
        'custom_engines', v_e, 'packs', v_k, 'car_facts_archived', v_cf, 'votes_archived', v_votes);
end;
$$;
revoke all on function public.mod_ban_user(uuid, timestamptz, text, text) from public, anon, authenticated;
grant  execute on function public.mod_ban_user(uuid, timestamptz, text, text) to service_role;

-- ---- 4. mod_unban_user: also clear the by-ban tag ---------------------------
create or replace function public.mod_unban_user(p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid_text text := p_user_id::text; r record; v_cf int := 0; v_votes int := 0;
begin
    delete from public.submitter_blocked where submitter_id = v_uid_text;
    delete from public.banned_devices where device_fp in (select device_fp from public.session_metadata where user_id = p_user_id and device_fp is not null);
    update public.presets       set is_suppressed = false, suppressed_by_ban = false where owner_user_id = p_user_id and suppressed_by_ban;
    update public.game_presets   set is_suppressed = false, suppressed_by_ban = false where owner_user_id = p_user_id and suppressed_by_ban;
    update public.custom_engines set is_suppressed = false, suppressed_by_ban = false where owner_user_id = p_user_id and suppressed_by_ban;
    update public.packs          set is_suppressed = false, suppressed_by_ban = false where owner_user_id = p_user_id and suppressed_by_ban;
    create temp table _aff (game text, car_id text, fact_type text, variant_signature text) on commit drop;
    insert into _aff select distinct game, car_id, fact_type, variant_signature from public.car_fact_submissions_archive where archived_user = p_user_id;
    insert into _aff select distinct consensus_game, consensus_car_id, consensus_fact_type, consensus_variant_signature from public.car_fact_consensus_votes_archive where archived_user = p_user_id;
    insert into public.car_fact_submissions
        (id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature)
        select id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature
          from public.car_fact_submissions_archive where archived_user = p_user_id on conflict (id) do nothing;
    delete from public.car_fact_submissions_archive where archived_user = p_user_id; get diagnostics v_cf = row_count;
    insert into public.car_fact_consensus_votes
        (consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature)
        select consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature
          from public.car_fact_consensus_votes_archive where archived_user = p_user_id on conflict do nothing;
    delete from public.car_fact_consensus_votes_archive where archived_user = p_user_id; get diagnostics v_votes = row_count;
    for r in select distinct game, car_id, fact_type, variant_signature from _aff loop
        perform public._recompute_car_fact_consensus(r.game, r.car_id, r.fact_type, r.variant_signature);
    end loop;
    return jsonb_build_object('unbanned', v_uid_text, 'car_facts_restored', v_cf, 'votes_restored', v_votes);
end;
$$;
revoke all on function public.mod_unban_user(uuid) from public, anon, authenticated;
grant  execute on function public.mod_unban_user(uuid) to service_role;

-- ---- 5. reaper: restore temp-ban-hidden content on expiry -------------------
select cron.schedule('tf4all-ban-reaper', '7 * * * *', $job$
  do $reap$
  declare exp text[];
  begin
    select array_agg(submitter_id) into exp
      from public.submitter_blocked where banned_until is not null and banned_until <= now();
    if exp is not null then
      update public.presets        set is_suppressed = false, suppressed_by_ban = false where suppressed_by_ban and owner_user_id::text = any(exp);
      update public.game_presets   set is_suppressed = false, suppressed_by_ban = false where suppressed_by_ban and owner_user_id::text = any(exp);
      update public.custom_engines set is_suppressed = false, suppressed_by_ban = false where suppressed_by_ban and owner_user_id::text = any(exp);
      update public.packs          set is_suppressed = false, suppressed_by_ban = false where suppressed_by_ban and owner_user_id::text = any(exp);
    end if;
    delete from public.submitter_blocked where banned_until is not null and banned_until <= now();
    delete from public.banned_devices  where banned_until is not null and banned_until <= now();
  end
  $reap$;
$job$);
