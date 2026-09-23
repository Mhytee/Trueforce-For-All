-- 0087: uploader fix-and-appeal proposals, final (no-appeal) removals, and owner
-- visibility into their own suppressed uploads.
--   * Notices carry an `appealable` flag (final removals hide Request review) and
--     the user's proposed fix (name/description/body) submitted WITH the appeal.
--   * request_review takes the proposal; mod_reinstate applies it before un-hiding,
--     so the reported row stays frozen until a mod approves the exact fix.
--   * get_my_uploads lets the owner see their own content INCLUDING suppressed
--     ones (RLS hides those from normal reads), each tagged with a moderation state,
--     so the plugin can show greyed rows that open the fix+appeal form.

alter table public.moderation_notices
    add column if not exists appealable           boolean not null default true,
    add column if not exists proposed_name        text,
    add column if not exists proposed_description text,
    add column if not exists proposed_body        jsonb;

-- ---- mod_hide: + p_appealable (final removal => user can't request review) ----
drop function if exists public.mod_hide(uuid, text, text, text);
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
    update public.report_flags
       set status = case p_kind when 'warn' then 'warned' else 'removed' end, resolved_by = p_actor, resolved_at = now()
     where id = p_report_id;
    return jsonb_build_object('ok', true, 'kind', p_kind, 'owner', v_owner, 'name', v_name,
        'notified', v_owner is not null, 'appealable', p_appealable);
end;
$$;
revoke all on function public.mod_hide(uuid, text, text, text, boolean) from public, anon, authenticated;
grant  execute on function public.mod_hide(uuid, text, text, text, boolean) to service_role;

-- ---- request_review: carry the proposed fix; refuse non-appealable notices ----
drop function if exists public.request_review(uuid, text);
create or replace function public.request_review(
    p_id uuid, p_note text default null,
    p_proposed_name text default null, p_proposed_description text default null, p_proposed_body jsonb default null)
returns boolean language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid();
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    update public.moderation_notices
       set appeal_state = 'pending',
           appeal_note = nullif(left(trim(coalesce(p_note, '')), 1000), ''),
           proposed_name = nullif(btrim(coalesce(p_proposed_name, '')), ''),
           proposed_description = nullif(btrim(coalesce(p_proposed_description, '')), ''),
           proposed_body = p_proposed_body,
           appeal_at = now()
     where id = p_id and user_id = v_uid and appealable and appeal_state in ('none', 'denied');
    return found;
end;
$$;
revoke all on function public.request_review(uuid, text, text, text, jsonb) from public, anon;
grant  execute on function public.request_review(uuid, text, text, text, jsonb) to authenticated;

-- ---- get_my_moderation_notices: + current name/description + appealable ----
create or replace function public.get_my_moderation_notices()
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid(); v jsonb;
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    select coalesce(jsonb_agg(to_jsonb(n) order by n.created_at desc), '[]'::jsonb) into v
      from (
        select mn.id, mn.target_type, mn.target_id, mn.kind, mn.reason_category, mn.message,
               mn.created_at, mn.acknowledged_at, mn.appeal_state, mn.appealable,
               coalesce(p.name, g.name, ce.name, pk.name) as item_name,
               coalesce(p.description, g.description, ce.description, pk.description) as item_description
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

-- ---- mod_reinstate: apply the proposed fix before un-hiding ----
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
            -- apply any proposed fix, then un-hide
            if v_n.proposed_name is not null then
                execute format('update public.%I set name = $1 where id = $2', v_table) using v_n.proposed_name, v_n.target_id;
            end if;
            if v_n.proposed_description is not null then
                execute format('update public.%I set description = $1 where id = $2', v_table) using v_n.proposed_description, v_n.target_id;
            end if;
            if v_n.proposed_body is not null then
                execute format('update public.%I set body = $1 where id = $2', v_table) using v_n.proposed_body, v_n.target_id;
            end if;
            execute format('update public.%I set is_suppressed = false, suppressed_by_ban = false where id = $1', v_table) using v_n.target_id;
        end if;
    end if;
    update public.moderation_notices set appeal_state = 'approved', resolved_by = p_actor, resolved_at = now() where id = p_notice_id;
    return jsonb_build_object('reinstated', true, 'kind', v_n.kind,
        'applied_fix', (v_n.proposed_name is not null or v_n.proposed_description is not null or v_n.proposed_body is not null),
        'detail', v_res);
end;
$$;
revoke all on function public.mod_reinstate(uuid, text) from public, anon, authenticated;
grant  execute on function public.mod_reinstate(uuid, text) to service_role;

-- ---- get_my_uploads: owner sees own content incl suppressed, with state ----
-- State: live | suspended (hidden by an active ban) | under_review (pending
-- appeal) | removed (hidden, not by ban). notice_id = the latest notice for the
-- item so the plugin can open the fix+appeal form straight from a greyed row.
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