-- 0089: report/appeal audit fixes.
--   (1) request_review validates the proposed name/description and no longer
--       accepts a raw proposed_body (was an unvalidated write to a distributed
--       preset; the plugin never sent it). mod_reinstate stops applying body.
--   (2) per-user hourly cap on report submissions (anti-flood; dedup-per-target
--       alone let one account fire unlimited distinct-target reports).
--   (4) get_my_uploads returns the notice's `appealable` so the plugin can hide
--       the Fix/appeal affordance on final (no-appeal) removals.
--   (6) mod_hide resolves EVERY open report for the target, not just one row, so
--       acting on a multi-reporter item closes them all.
--   (5) a sweep cron re-fires report/appeal Discord cards that never posted
--       (silent pg_net failure left them invisible to mods).
-- (8 actor-id is handled in report-action by storing "DisplayName (discord_id)".)

-- ---- (1) request_review: validated proposal, no body ------------------------
drop function if exists public.request_review(uuid, text, text, text, jsonb);
create or replace function public.request_review(
    p_id uuid, p_note text default null,
    p_proposed_name text default null, p_proposed_description text default null)
returns boolean language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid(); v_name text; v_desc text;
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_name := nullif(btrim(coalesce(p_proposed_name, '')), '');
    if v_name is not null and (char_length(v_name) < 2 or char_length(v_name) > 96) then
        raise exception 'Proposed name must be 2 to 96 characters' using errcode = 'P0001';
    end if;
    v_desc := nullif(left(btrim(coalesce(p_proposed_description, '')), 1024), '');
    update public.moderation_notices
       set appeal_state = 'pending',
           appeal_note = nullif(left(trim(coalesce(p_note, '')), 1000), ''),
           proposed_name = v_name,
           proposed_description = v_desc,
           proposed_body = null,   -- body replacement not accepted yet (would bypass upload validation)
           appeal_at = now()
     where id = p_id and user_id = v_uid and appealable and appeal_state in ('none', 'denied');
    return found;
end;
$$;
revoke all on function public.request_review(uuid, text, text, text) from public, anon;
grant  execute on function public.request_review(uuid, text, text, text) to authenticated;

-- ---- (1) mod_reinstate: stop applying proposed_body -------------------------
create or replace function public.mod_reinstate(p_notice_id uuid, p_actor text default null)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
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
            if v_n.proposed_name is not null then
                execute format('update public.%I set name = $1 where id = $2', v_table) using v_n.proposed_name, v_n.target_id;
            end if;
            if v_n.proposed_description is not null then
                execute format('update public.%I set description = $1 where id = $2', v_table) using v_n.proposed_description, v_n.target_id;
            end if;
            execute format('update public.%I set is_suppressed = false, suppressed_by_ban = false where id = $1', v_table) using v_n.target_id;
        end if;
    end if;
    update public.moderation_notices set appeal_state = 'approved', resolved_by = p_actor, resolved_at = now() where id = p_notice_id;
    return jsonb_build_object('reinstated', true, 'kind', v_n.kind,
        'applied_fix', (v_n.proposed_name is not null or v_n.proposed_description is not null), 'detail', v_res);
end;
$$;
revoke all on function public.mod_reinstate(uuid, text) from public, anon, authenticated;
grant  execute on function public.mod_reinstate(uuid, text) to service_role;

-- ---- (2) per-user hourly report cap -----------------------------------------
-- shared guard; raises when the caller filed >= 20 reports in the last hour.
create or replace function public._report_rate_ok() returns boolean
language sql security definer set search_path = public, extensions, pg_temp as $$
    select (select count(*) from public.report_flags
             where reporter_id = auth.uid() and created_at > now() - interval '1 hour') < 20;
$$;
revoke all on function public._report_rate_ok() from public, anon, authenticated;

create or replace function public.report_preset(
    p_preset_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.presets where id = p_preset_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if not public._report_rate_ok() then
        raise exception 'Report rate limit reached, please try again later' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_preset_id
                  and target_type = 'preset' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('preset', p_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.presets set reported = true where id = p_preset_id;
end;
$$;

create or replace function public.report_game_preset(
    p_game_preset_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.game_presets where id = p_game_preset_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if not public._report_rate_ok() then
        raise exception 'Report rate limit reached, please try again later' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_game_preset_id
                  and target_type = 'game_preset' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('game_preset', p_game_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.game_presets set reported = true where id = p_game_preset_id;
end;
$$;

create or replace function public.report_custom_engine(
    p_custom_engine_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.custom_engines where id = p_custom_engine_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if not public._report_rate_ok() then
        raise exception 'Report rate limit reached, please try again later' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_custom_engine_id
                  and target_type = 'custom_engine' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('custom_engine', p_custom_engine_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.custom_engines set reported = true where id = p_custom_engine_id;
end;
$$;

create or replace function public.report_pack(
    p_pack_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.packs where id = p_pack_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if not public._report_rate_ok() then
        raise exception 'Report rate limit reached, please try again later' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_pack_id
                  and target_type = 'pack' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('pack', p_pack_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.packs set reported = true where id = p_pack_id;
end;
$$;

-- ---- (6) mod_hide resolves ALL open reports for the target ------------------
create or replace function public.mod_hide(
    p_report_id uuid, p_kind text, p_actor text default null,
    p_message text default null, p_appealable boolean default true)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_rep record; v_table text; v_owner uuid; v_name text; v_msg text; v_label text;
    v_custom text := nullif(btrim(coalesce(p_message, '')), ''); v_base text; v_default text;
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
    v_default := case
        when not p_appealable then 'This removal is final.'
        when p_kind = 'warn' then 'Please fix it (for example, rename it) and request a review to restore it.'
        else 'You can request a review if you believe this was a mistake.' end;
    v_msg := v_base || ' ' || coalesce(v_custom, v_default);
    if v_owner is not null then
        insert into public.moderation_notices (user_id, target_type, target_id, kind, reason_category, message, created_by, appealable)
        values (v_owner, v_rep.target_type, v_rep.target_id, p_kind, v_rep.category, v_msg, p_actor, p_appealable);
    end if;
    -- resolve EVERY open report for this content, not just the card-holder.
    update public.report_flags
       set status = case p_kind when 'warn' then 'warned' else 'removed' end, resolved_by = p_actor, resolved_at = now()
     where target_type = v_rep.target_type and target_id = v_rep.target_id and status = 'open';
    return jsonb_build_object('ok', true, 'kind', p_kind, 'owner', v_owner, 'name', v_name,
        'notified', v_owner is not null, 'appealable', p_appealable);
end;
$$;
revoke all on function public.mod_hide(uuid, text, text, text, boolean) from public, anon, authenticated;
grant  execute on function public.mod_hide(uuid, text, text, text, boolean) to service_role;

-- ---- (4) get_my_uploads returns appealable ----------------------------------
create or replace function public.get_my_uploads()
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid(); v jsonb;
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    select coalesce(jsonb_agg(to_jsonb(x) order by x.created_at desc), '[]'::jsonb) into v
      from (
        select kind, id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at,
               (select mn.id from public.moderation_notices mn
                 where mn.target_type = u.kind and mn.target_id = u.id and mn.user_id = v_uid
                 order by mn.created_at desc limit 1) as notice_id,
               (select mn.appeal_state from public.moderation_notices mn
                 where mn.target_type = u.kind and mn.target_id = u.id and mn.user_id = v_uid
                 order by mn.created_at desc limit 1) as appeal_state,
               coalesce((select mn.appealable from public.moderation_notices mn
                 where mn.target_type = u.kind and mn.target_id = u.id and mn.user_id = v_uid
                 order by mn.created_at desc limit 1), true) as appealable,
               case
                 when not is_suppressed then 'live'
                 when suppressed_by_ban then 'suspended'
                 when exists (select 1 from public.moderation_notices mn
                               where mn.target_type = u.kind and mn.target_id = u.id
                                 and mn.user_id = v_uid and mn.appeal_state = 'pending') then 'under_review'
                 else 'removed'
               end as state
          from (
            select 'preset'        as kind, id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at from public.presets        where owner_user_id = v_uid
            union all select 'game_preset',   id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at from public.game_presets   where owner_user_id = v_uid
            union all select 'custom_engine', id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at from public.custom_engines where owner_user_id = v_uid
            union all select 'pack',          id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at from public.packs          where owner_user_id = v_uid
          ) u
      ) x;
    return v;
end;
$$;
revoke all on function public.get_my_uploads() from public, anon;
grant  execute on function public.get_my_uploads() to authenticated;

-- ---- (5) sweep: re-fire Discord cards that never posted ---------------------
select cron.schedule('tf4all-report-card-sweep', '*/10 * * * *', $job$
  do $sweep$
  declare r record; v_key text;
  begin
    v_key := (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key');
    -- reports: a target with open reports but NO posted card, aged > 5 min.
    for r in
      select distinct on (rf.target_type, rf.target_id) rf.id
        from public.report_flags rf
       where rf.status = 'open' and rf.created_at < now() - interval '5 minutes'
         and not exists (select 1 from public.report_flags rf2
                          where rf2.target_type = rf.target_type and rf2.target_id = rf.target_id
                            and rf2.discord_message_id is not null)
       order by rf.target_type, rf.target_id, rf.created_at asc
    loop
      perform net.http_post(
        url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/report-notify',
        headers := jsonb_build_object('Authorization', 'Bearer ' || v_key, 'Content-Type', 'application/json'),
        body := jsonb_build_object('report_id', r.id));
    end loop;
    -- appeals: pending notice with no posted appeal card, aged > 5 min.
    for r in
      select id from public.moderation_notices
       where appeal_state = 'pending' and discord_message_id is null
         and appeal_at < now() - interval '5 minutes'
    loop
      perform net.http_post(
        url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/appeal-notify',
        headers := jsonb_build_object('Authorization', 'Bearer ' || v_key, 'Content-Type', 'application/json'),
        body := jsonb_build_object('notice_id', r.id));
    end loop;
  end
  $sweep$;
$job$);
