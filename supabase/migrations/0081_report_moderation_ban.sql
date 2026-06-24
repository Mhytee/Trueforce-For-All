-- 0081: report moderation - account/device ban (temp + permanent) + scoped purge.
--
-- Builds on 0080's report card. Adds three moderator powers, all driven from the
-- Discord buttons via report-action:
--   * Temp or permanent BAN of the uploader.
--   * Optional content PURGE (just the reported item, or everything they posted
--     across presets/game presets/engines/packs AND their car facts + votes).
--   * Device-level evasion blocking keyed by the plugin's hardware fingerprint.
--
-- Design choice that avoids touching the security-audited write RPCs: every
-- write path (upload_*, submit_car_fact, vote_*) already gates on
-- `exists(select 1 from submitter_blocked where submitter_id = auth.uid()::text)`.
-- So banning an account = inserting that row; temp-ban expiry = a reaper cron
-- that deletes expired rows (the unchanged exists-checks then pass); and device
-- banning = the session heartbeat pulling any account that signs in on a banned
-- machine into submitter_blocked. No per-RPC edits, nothing re-audited.

-- ---- 1. Temp-ban expiry on the existing account-ban lever -------------------
alter table public.submitter_blocked
    add column if not exists banned_until timestamptz;   -- null = permanent

-- ---- 2. Device ban list -----------------------------------------------------
-- device_fp = sha256(Windows MachineGuid | lowercased username), computed by the
-- plugin (DeviceFingerprint.Compute) and sent on the session heartbeat. Private:
-- no client role can read or write it.
create table if not exists public.banned_devices (
    device_fp    text primary key,
    banned_until timestamptz,        -- null = permanent
    reason       text,
    banned_by    text,
    created_at   timestamptz not null default now()
);
alter table public.banned_devices enable row level security;
revoke all on public.banned_devices from anon, authenticated, public;

-- ---- 3. Persist the device fingerprint per session (server-only) ------------
-- Never surfaced by get_my_sessions; used only for ban matching.
alter table public.session_metadata
    add column if not exists device_fp text;

-- ---- 4. session_heartbeat: carry device_fp + propagate device bans ----------
-- Old 3-arg overload dropped so the new arg set is unambiguous; old clients send
-- their 3 named args and bind to this version with device_fp null (fail-open).
drop function if exists public.session_heartbeat(text, text, text);
create or replace function public.session_heartbeat(
    p_device_name    text,
    p_wheel          text,
    p_plugin_version text,
    p_device_fp      text default null)
returns boolean
language plpgsql security definer set search_path = public, pg_temp as $$
declare
    sid     uuid;
    is_live boolean;
    v_fp    text := left(nullif(p_device_fp, ''), 64);
    v_until timestamptz;
    v_found boolean;
begin
    sid := nullif(current_setting('request.jwt.claims', true)::json ->> 'session_id', '')::uuid;
    if sid is null then
        return true;   -- fail OPEN; no session key to write
    end if;

    is_live := exists(
        select 1 from auth.sessions s
        where s.id = sid and s.user_id = auth.uid());
    if not is_live then
        return false;  -- revoked: signal sign-out, write nothing
    end if;

    insert into public.session_metadata
        (session_id, user_id, device_name, wheel, plugin_version, device_fp, last_seen)
    values
        (sid, auth.uid(),
         left(nullif(p_device_name,    ''), 80),
         left(nullif(p_wheel,          ''), 60),
         left(nullif(p_plugin_version, ''), 32),
         v_fp, now())
    on conflict (session_id) do update
        set device_name    = excluded.device_name,
            wheel          = excluded.wheel,
            plugin_version = excluded.plugin_version,
            -- keep a known fp if a later heartbeat omits it
            device_fp      = coalesce(excluded.device_fp, public.session_metadata.device_fp),
            last_seen      = excluded.last_seen;

    -- Device-ban propagation: if this machine is banned, pull the signed-in
    -- account (including fresh sock-puppets) into submitter_blocked so the
    -- existing write-RPC checks block it. Honors the device ban's own expiry.
    if v_fp is not null then
        select banned_until into v_until
          from public.banned_devices
         where device_fp = v_fp and (banned_until is null or banned_until > now());
        get diagnostics v_found = row_count;
        if v_found then
            insert into public.submitter_blocked (submitter_id, reason, banned_until)
                values (auth.uid()::text, 'device ban propagation', v_until)
            on conflict (submitter_id) do update
                set banned_until = case
                        when public.submitter_blocked.banned_until is null then null  -- already perm
                        when excluded.banned_until is null then null                  -- escalate to perm
                        else greatest(public.submitter_blocked.banned_until, excluded.banned_until)
                    end;
        end if;
    end if;
    return true;
end;
$$;
revoke all on function public.session_heartbeat(text, text, text, text) from public, anon;
grant  execute on function public.session_heartbeat(text, text, text, text) to authenticated;

-- ---- 5. Reaper: expire temp bans (so unchanged exists-checks honor expiry) ---
select cron.schedule('tf4all-ban-reaper', '7 * * * *', $job$
  do $reap$
  begin
    delete from public.submitter_blocked where banned_until is not null and banned_until <= now();
    delete from public.banned_devices  where banned_until is not null and banned_until <= now();
  end
  $reap$;
$job$);

-- ---- 6. mod_ban_user: the one action report-action calls (service role) ------
-- Bans the account, bans every device it has used (and any alts already seen on
-- those devices), and optionally purges content. Idempotent / re-runnable.
create or replace function public.mod_ban_user(
    p_user_id      uuid,
    p_scope        text        default 'item',  -- 'item' = caller suppresses the item; 'all' = purge everything
    p_banned_until timestamptz default null,     -- null = permanent
    p_reason       text        default null,
    p_actor        text        default null)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_uid_text text := p_user_id::text;
    v_dev int := 0; v_alts int := 0; v_cf int := 0; v_votes int := 0;
    v_p int := 0; v_g int := 0; v_e int := 0; v_k int := 0; v_tmp int := 0;
    r record;
begin
    if p_user_id is null then
        return jsonb_build_object('error', 'no account on this item');
    end if;

    -- (a) account ban -- the lever every write RPC already checks
    insert into public.submitter_blocked (submitter_id, reason, banned_until)
        values (v_uid_text, coalesce(p_reason, 'mod ban'), p_banned_until)
    on conflict (submitter_id) do update
        set reason = excluded.reason,
            banned_until = case
                when public.submitter_blocked.banned_until is null then null
                when excluded.banned_until is null then null
                else greatest(public.submitter_blocked.banned_until, excluded.banned_until)
            end;

    -- (b) device ban -- every fingerprint this user has signed in with, plus
    -- block any alt accounts already seen on those machines.
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
                    else greatest(public.banned_devices.banned_until, excluded.banned_until)
                end,
                reason = excluded.reason, banned_by = excluded.banned_by;
        v_dev := v_dev + 1;

        insert into public.submitter_blocked (submitter_id, reason, banned_until)
            select distinct sm.user_id::text, 'device ban (alt)', p_banned_until
              from public.session_metadata sm
             where sm.device_fp = r.device_fp and sm.user_id <> p_user_id
        on conflict (submitter_id) do nothing;
        get diagnostics v_tmp = row_count;
        v_alts := v_alts + v_tmp;
    end loop;

    -- (c) optional purge
    if p_scope = 'all' then
        update public.presets       set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed;
        get diagnostics v_p = row_count;
        update public.game_presets   set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed;
        get diagnostics v_g = row_count;
        update public.custom_engines set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed;
        get diagnostics v_e = row_count;
        update public.packs          set is_suppressed = true where owner_user_id = p_user_id and not is_suppressed;
        get diagnostics v_k = row_count;

        -- Car facts are attributed by submitter_id / voter_id = uid::text.
        -- Capture affected consensus keys BEFORE deleting, then recompute each.
        create temp table _affected (game text, car_id text, fact_type text, variant_signature text)
            on commit drop;
        insert into _affected
            select distinct game, car_id, fact_type, variant_signature
              from public.car_fact_submissions where submitter_id = v_uid_text;
        insert into _affected
            select distinct consensus_game, consensus_car_id, consensus_fact_type, consensus_variant_signature
              from public.car_fact_consensus_votes where voter_id = v_uid_text;

        delete from public.car_fact_submissions where submitter_id = v_uid_text;
        get diagnostics v_cf = row_count;
        delete from public.car_fact_consensus_votes where voter_id = v_uid_text;
        get diagnostics v_votes = row_count;

        for r in select distinct game, car_id, fact_type, variant_signature from _affected loop
            perform public._recompute_car_fact_consensus(r.game, r.car_id, r.fact_type, r.variant_signature);
        end loop;
    end if;

    return jsonb_build_object(
        'banned_user', v_uid_text, 'scope', p_scope, 'banned_until', p_banned_until,
        'devices_banned', v_dev, 'alts_blocked', v_alts,
        'presets', v_p, 'game_presets', v_g, 'custom_engines', v_e, 'packs', v_k,
        'car_facts_deleted', v_cf, 'votes_deleted', v_votes);
end;
$$;
revoke all on function public.mod_ban_user(uuid, text, timestamptz, text, text) from public, anon, authenticated;
grant  execute on function public.mod_ban_user(uuid, text, timestamptz, text, text) to service_role;

-- ---- 7. mod_user_footprint: everything an account has posted (service role) --
-- Powers the report card's rap-sheet summary and the "View footprint" button.
create or replace function public.mod_user_footprint(p_user_id uuid)
returns jsonb
language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v jsonb; v_uid_text text := p_user_id::text;
begin
    if p_user_id is null then return jsonb_build_object('error', 'no account'); end if;
    select jsonb_build_object(
        'user_id',          v_uid_text,
        'username',         (select username   from public.profiles where id = p_user_id),
        'account_created',  (select created_at from auth.users    where id = p_user_id),
        'presets_total',        (select count(*) from public.presets        where owner_user_id = p_user_id),
        'presets_suppressed',   (select count(*) from public.presets        where owner_user_id = p_user_id and is_suppressed),
        'game_presets_total',   (select count(*) from public.game_presets   where owner_user_id = p_user_id),
        'custom_engines_total', (select count(*) from public.custom_engines where owner_user_id = p_user_id),
        'packs_total',          (select count(*) from public.packs          where owner_user_id = p_user_id),
        'car_facts_total',      (select count(*) from public.car_fact_submissions where submitter_id = v_uid_text),
        'times_reported',       (select count(*) from public.report_flags   where target_id in (
                                    select id from public.presets        where owner_user_id = p_user_id
                                    union all select id from public.game_presets   where owner_user_id = p_user_id
                                    union all select id from public.custom_engines where owner_user_id = p_user_id
                                    union all select id from public.packs          where owner_user_id = p_user_id)),
        'items',            (
            select coalesce(jsonb_agg(to_jsonb(x) order by x.created_at desc), '[]'::jsonb) from (
                select 'preset'        as kind, name, is_suppressed, downloads, created_at from public.presets        where owner_user_id = p_user_id
                union all select 'game_preset',   name, is_suppressed, downloads, created_at from public.game_presets   where owner_user_id = p_user_id
                union all select 'custom_engine', name, is_suppressed, downloads, created_at from public.custom_engines where owner_user_id = p_user_id
                union all select 'pack',          name, is_suppressed, downloads, created_at from public.packs          where owner_user_id = p_user_id
                limit 40
            ) x)
    ) into v;
    return v;
end;
$$;
revoke all on function public.mod_user_footprint(uuid) from public, anon, authenticated;
grant  execute on function public.mod_user_footprint(uuid) to service_role;
