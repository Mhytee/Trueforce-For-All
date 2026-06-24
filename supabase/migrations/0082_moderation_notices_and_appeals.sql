-- 0082: moderation notices, appeals, and fully-reversible enforcement.
--
-- Converged model (warn/remove/ban all hide immediately; nothing is permanent
-- without a mod re-approving):
--   * Warn / Remove -> suppress the item now + leave the uploader a notice. The
--     user can fix it and Request review; a mod must Reinstate to restore it.
--   * Ban           -> account/device block + (scope 'all') suppress everything
--     and ARCHIVE their car facts instead of deleting them, so a ban is fully
--     reversible. The live consensus-recompute code is untouched (it still only
--     reads the live table); archived rows just stop counting until restored.
--   * Reinstate / Deny are mod actions on the appeal card (handled by report-action).
--
-- All moderation tables are private (RPC-only). User RPCs derive identity from
-- auth.uid(); the mod_* RPCs are service-role only (report-action calls them).

-- ---- report status vocabulary: add warned/banned ---------------------------
alter table public.report_flags drop constraint if exists report_flags_status_check;
alter table public.report_flags
    add constraint report_flags_status_check
    check (status in ('open', 'dismissed', 'warned', 'removed', 'banned'));

-- ---- 1. moderation_notices --------------------------------------------------
create table if not exists public.moderation_notices (
    id              uuid primary key default gen_random_uuid(),
    user_id         uuid not null references auth.users(id) on delete cascade,
    target_type     text,          -- null for account-wide (ban)
    target_id       uuid,
    kind            text not null check (kind in ('warn', 'removal', 'ban')),
    reason_category text,
    message         text,
    created_at      timestamptz not null default now(),
    created_by      text,
    acknowledged_at timestamptz,
    appeal_state    text not null default 'none'
                      check (appeal_state in ('none', 'pending', 'approved', 'denied')),
    appeal_note     text,
    appeal_at       timestamptz,
    resolved_by     text,
    resolved_at     timestamptz,
    discord_message_id text,
    discord_channel_id text
);
create index if not exists moderation_notices_user_idx   on public.moderation_notices (user_id, created_at desc);
create index if not exists moderation_notices_appeal_idx on public.moderation_notices (appeal_state) where appeal_state = 'pending';
alter table public.moderation_notices enable row level security;
revoke all on public.moderation_notices from anon, authenticated, public;

-- ---- 2. car-fact archive (reversible purge) ---------------------------------
create table if not exists public.car_fact_submissions_archive (
    id uuid, game text, car_id text, fact_type text, payload jsonb,
    submitter_id text, source_ip_hash text, plugin_version text,
    created_at timestamptz, variant_signature text,
    archived_user uuid, archived_at timestamptz not null default now()
);
create index if not exists cf_sub_archive_user_idx on public.car_fact_submissions_archive (archived_user);
create table if not exists public.car_fact_consensus_votes_archive (
    consensus_game text, consensus_car_id text, consensus_fact_type text,
    voter_id text, payload_hash text, source_ip_hash text, direction int,
    created_at timestamptz, updated_at timestamptz, consensus_variant_signature text,
    archived_user uuid, archived_at timestamptz not null default now()
);
create index if not exists cf_vote_archive_user_idx on public.car_fact_consensus_votes_archive (archived_user);
alter table public.car_fact_submissions_archive       enable row level security;
alter table public.car_fact_consensus_votes_archive   enable row level security;
revoke all on public.car_fact_submissions_archive     from anon, authenticated, public;
revoke all on public.car_fact_consensus_votes_archive from anon, authenticated, public;

-- ---- 3. mod_hide: warn / removal (suppress now + leave a notice) ------------
create or replace function public.mod_hide(
    p_report_id uuid,
    p_kind      text,                 -- 'warn' | 'removal'
    p_actor     text default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_rep record; v_table text; v_owner uuid; v_name text; v_msg text; v_label text;
begin
    if p_kind not in ('warn', 'removal') then
        raise exception 'invalid kind %', p_kind;
    end if;
    select target_type, target_id, category into v_rep
      from public.report_flags where id = p_report_id;
    if not found then return jsonb_build_object('error', 'report not found'); end if;

    v_table := case v_rep.target_type
        when 'preset' then 'presets' when 'game_preset' then 'game_presets'
        when 'custom_engine' then 'custom_engines' when 'pack' then 'packs' end;
    if v_table is null then return jsonb_build_object('error', 'bad target_type'); end if;

    execute format('update public.%I set is_suppressed = true where id = $1', v_table) using v_rep.target_id;
    execute format('select owner_user_id, name from public.%I where id = $1', v_table)
        into v_owner, v_name using v_rep.target_id;

    v_label := case v_rep.target_type
        when 'preset' then 'car preset' when 'game_preset' then 'game preset'
        when 'custom_engine' then 'custom engine' else 'pack' end;
    v_msg := case p_kind
        when 'warn' then
            'Your ' || v_label || ' "' || coalesce(v_name, '') || '" was hidden after a report ('
            || coalesce(v_rep.category, 'other') || '). Please fix it (for example, rename it) and request a review to restore it.'
        else
            'Your ' || v_label || ' "' || coalesce(v_name, '') || '" was removed after a report ('
            || coalesce(v_rep.category, 'other') || '). You can request a review if you believe this was a mistake.'
    end;

    if v_owner is not null then
        insert into public.moderation_notices
            (user_id, target_type, target_id, kind, reason_category, message, created_by)
        values
            (v_owner, v_rep.target_type, v_rep.target_id, p_kind, v_rep.category, v_msg, p_actor);
    end if;

    update public.report_flags
       set status = case p_kind when 'warn' then 'warned' else 'removed' end,
           resolved_by = p_actor, resolved_at = now()
     where id = p_report_id;

    return jsonb_build_object('ok', true, 'kind', p_kind, 'owner', v_owner, 'name', v_name, 'notified', v_owner is not null);
end;
$$;
revoke all on function public.mod_hide(uuid, text, text) from public, anon, authenticated;
grant  execute on function public.mod_hide(uuid, text, text) to service_role;

-- ---- 4. mod_ban_user: archive car facts (reversible) + ban notice -----------
create or replace function public.mod_ban_user(
    p_user_id      uuid,
    p_scope        text        default 'item',
    p_banned_until timestamptz default null,
    p_reason       text        default null,
    p_actor        text        default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_uid_text text := p_user_id::text;
    v_dev int := 0; v_alts int := 0; v_cf int := 0; v_votes int := 0;
    v_p int := 0; v_g int := 0; v_e int := 0; v_k int := 0; v_tmp int := 0;
    v_msg text; r record;
begin
    if p_user_id is null then return jsonb_build_object('error', 'no account on this item'); end if;

    insert into public.submitter_blocked (submitter_id, reason, banned_until)
        values (v_uid_text, coalesce(p_reason, 'mod ban'), p_banned_until)
    on conflict (submitter_id) do update
        set reason = excluded.reason,
            banned_until = case
                when public.submitter_blocked.banned_until is null then null
                when excluded.banned_until is null then null
                else greatest(public.submitter_blocked.banned_until, excluded.banned_until) end;

    for r in
        select distinct device_fp from public.session_metadata
         where user_id = p_user_id and device_fp is not null
    loop
        insert into public.banned_devices (device_fp, banned_until, reason, banned_by)
            values (r.device_fp, p_banned_until, coalesce(p_reason, 'mod ban'), p_actor)
        on conflict (device_fp) do update
            set banned_until = case
                    when public.banned_devices.banned_until is null then null
                    when excluded.banned_until is null then null
                    else greatest(public.banned_devices.banned_until, excluded.banned_until) end,
                reason = excluded.reason, banned_by = excluded.banned_by;
        v_dev := v_dev + 1;
        insert into public.submitter_blocked (submitter_id, reason, banned_until)
            select distinct sm.user_id::text, 'device ban (alt)', p_banned_until
              from public.session_metadata sm
             where sm.device_fp = r.device_fp and sm.user_id <> p_user_id
        on conflict (submitter_id) do nothing;
        get diagnostics v_tmp = row_count; v_alts := v_alts + v_tmp;
    end loop;

    if p_scope = 'all' then
        update public.presets       set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_p = row_count;
        update public.game_presets   set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_g = row_count;
        update public.custom_engines set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_e = row_count;
        update public.packs          set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed; get diagnostics v_k = row_count;

        create temp table _affected (game text, car_id text, fact_type text, variant_signature text) on commit drop;
        insert into _affected select distinct game, car_id, fact_type, variant_signature
            from public.car_fact_submissions where submitter_id = v_uid_text;
        insert into _affected select distinct consensus_game, consensus_car_id, consensus_fact_type, consensus_variant_signature
            from public.car_fact_consensus_votes where voter_id = v_uid_text;

        -- ARCHIVE (not delete) so the ban is reversible.
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

    -- ban notice (account-wide) so the user is told + can appeal
    v_msg := 'Your account was '
        || case when p_banned_until is null then 'permanently banned' else 'banned (temporary)' end
        || ' from community uploads after a report. You can request a review if you believe this was a mistake.';
    insert into public.moderation_notices (user_id, kind, message, created_by)
        values (p_user_id, 'ban', v_msg, p_actor);

    return jsonb_build_object(
        'banned_user', v_uid_text, 'scope', p_scope, 'banned_until', p_banned_until,
        'devices_banned', v_dev, 'alts_blocked', v_alts,
        'presets', v_p, 'game_presets', v_g, 'custom_engines', v_e, 'packs', v_k,
        'car_facts_archived', v_cf, 'votes_archived', v_votes);
end;
$$;
revoke all on function public.mod_ban_user(uuid, text, timestamptz, text, text) from public, anon, authenticated;
grant  execute on function public.mod_ban_user(uuid, text, timestamptz, text, text) to service_role;

-- ---- 5. mod_unban_user: reverse a ban (restore archived car facts) ----------
create or replace function public.mod_unban_user(p_user_id uuid)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid_text text := p_user_id::text; r record; v_cf int := 0; v_votes int := 0;
begin
    delete from public.submitter_blocked where submitter_id = v_uid_text;
    delete from public.banned_devices
     where device_fp in (select device_fp from public.session_metadata where user_id = p_user_id and device_fp is not null);

    update public.presets       set is_suppressed = false where owner_user_id = p_user_id and is_suppressed;
    update public.game_presets   set is_suppressed = false where owner_user_id = p_user_id and is_suppressed;
    update public.custom_engines set is_suppressed = false where owner_user_id = p_user_id and is_suppressed;
    update public.packs          set is_suppressed = false where owner_user_id = p_user_id and is_suppressed;

    create temp table _aff (game text, car_id text, fact_type text, variant_signature text) on commit drop;
    insert into _aff select distinct game, car_id, fact_type, variant_signature
        from public.car_fact_submissions_archive where archived_user = p_user_id;
    insert into _aff select distinct consensus_game, consensus_car_id, consensus_fact_type, consensus_variant_signature
        from public.car_fact_consensus_votes_archive where archived_user = p_user_id;

    insert into public.car_fact_submissions
        (id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature)
        select id, game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version, created_at, variant_signature
          from public.car_fact_submissions_archive where archived_user = p_user_id
        on conflict (id) do nothing;
    delete from public.car_fact_submissions_archive where archived_user = p_user_id; get diagnostics v_cf = row_count;

    insert into public.car_fact_consensus_votes
        (consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature)
        select consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash, source_ip_hash, direction, created_at, updated_at, consensus_variant_signature
          from public.car_fact_consensus_votes_archive where archived_user = p_user_id
        on conflict do nothing;
    delete from public.car_fact_consensus_votes_archive where archived_user = p_user_id; get diagnostics v_votes = row_count;

    for r in select distinct game, car_id, fact_type, variant_signature from _aff loop
        perform public._recompute_car_fact_consensus(r.game, r.car_id, r.fact_type, r.variant_signature);
    end loop;

    return jsonb_build_object('unbanned', v_uid_text, 'car_facts_restored', v_cf, 'votes_restored', v_votes);
end;
$$;
revoke all on function public.mod_unban_user(uuid) from public, anon, authenticated;
grant  execute on function public.mod_unban_user(uuid) to service_role;

-- ---- 6. mod_reinstate / mod_deny_appeal (resolve an appeal) -----------------
create or replace function public.mod_reinstate(p_notice_id uuid, p_actor text default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_n record; v_table text; v_res jsonb := '{}'::jsonb;
begin
    select * into v_n from public.moderation_notices where id = p_notice_id;
    if not found then return jsonb_build_object('error', 'notice not found'); end if;

    if v_n.kind = 'ban' then
        v_res := public.mod_unban_user(v_n.user_id);
    elsif v_n.target_id is not null then
        v_table := case v_n.target_type
            when 'preset' then 'presets' when 'game_preset' then 'game_presets'
            when 'custom_engine' then 'custom_engines' when 'pack' then 'packs' end;
        if v_table is not null then
            execute format('update public.%I set is_suppressed = false where id = $1', v_table) using v_n.target_id;
        end if;
    end if;

    update public.moderation_notices
       set appeal_state = 'approved', resolved_by = p_actor, resolved_at = now()
     where id = p_notice_id;
    return jsonb_build_object('reinstated', true, 'kind', v_n.kind, 'detail', v_res);
end;
$$;
revoke all on function public.mod_reinstate(uuid, text) from public, anon, authenticated;
grant  execute on function public.mod_reinstate(uuid, text) to service_role;

create or replace function public.mod_deny_appeal(p_notice_id uuid, p_actor text default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
begin
    update public.moderation_notices
       set appeal_state = 'denied', resolved_by = p_actor, resolved_at = now()
     where id = p_notice_id;
    if not found then return jsonb_build_object('error', 'notice not found'); end if;
    return jsonb_build_object('denied', true);
end;
$$;
revoke all on function public.mod_deny_appeal(uuid, text) from public, anon, authenticated;
grant  execute on function public.mod_deny_appeal(uuid, text) to service_role;

-- ---- 7. user-facing RPCs (auth.uid scoped) ----------------------------------
create or replace function public.get_my_moderation_notices()
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid(); v jsonb;
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    select coalesce(jsonb_agg(to_jsonb(n) order by n.created_at desc), '[]'::jsonb) into v
      from (
        select mn.id, mn.target_type, mn.target_id, mn.kind, mn.reason_category, mn.message,
               mn.created_at, mn.acknowledged_at, mn.appeal_state,
               coalesce(p.name, g.name, ce.name, pk.name) as item_name
          from public.moderation_notices mn
          left join public.presets        p  on mn.target_type = 'preset'        and p.id  = mn.target_id
          left join public.game_presets   g  on mn.target_type = 'game_preset'   and g.id  = mn.target_id
          left join public.custom_engines ce on mn.target_type = 'custom_engine' and ce.id = mn.target_id
          left join public.packs          pk on mn.target_type = 'pack'          and pk.id = mn.target_id
         where mn.user_id = v_uid
      ) n;
    return v;
end;
$$;
revoke all on function public.get_my_moderation_notices() from public, anon;
grant  execute on function public.get_my_moderation_notices() to authenticated;

create or replace function public.acknowledge_moderation_notice(p_id uuid)
returns boolean
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid();
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    update public.moderation_notices set acknowledged_at = now()
     where id = p_id and user_id = v_uid and acknowledged_at is null;
    return found;
end;
$$;
revoke all on function public.acknowledge_moderation_notice(uuid) from public, anon;
grant  execute on function public.acknowledge_moderation_notice(uuid) to authenticated;

create or replace function public.request_review(p_id uuid, p_note text default null)
returns boolean
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid();
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    update public.moderation_notices
       set appeal_state = 'pending', appeal_note = nullif(left(trim(coalesce(p_note, '')), 1000), ''), appeal_at = now()
     where id = p_id and user_id = v_uid and appeal_state in ('none', 'denied');
    return found;
end;
$$;
revoke all on function public.request_review(uuid, text) from public, anon;
grant  execute on function public.request_review(uuid, text) to authenticated;

-- ---- 8. appeal -> Discord notify trigger ------------------------------------
create or replace function public.notify_appeal()
returns trigger language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
begin
    if NEW.appeal_state = 'pending' and OLD.appeal_state is distinct from 'pending' then
        perform net.http_post(
            url     := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/appeal-notify',
            headers := jsonb_build_object(
                'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
                'Content-Type',  'application/json'),
            body    := jsonb_build_object('notice_id', NEW.id));
    end if;
    return NEW;
end;
$$;
drop trigger if exists moderation_notices_appeal on public.moderation_notices;
create trigger moderation_notices_appeal
    after update on public.moderation_notices
    for each row execute function public.notify_appeal();
