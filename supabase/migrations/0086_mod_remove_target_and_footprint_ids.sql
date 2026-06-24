-- 0086: act on an uploader's other content during review.
--   * mod_remove_target: remove any one upload directly (no report, no ban).
--     Sets suppressed_by_ban = false so it stays removed even if the user is
--     under a temp ban whose suspension later lifts. Leaves an appealable notice.
--   * mod_user_footprint: items now carry id + suppressed_by_ban so the Discord
--     footprint can offer a Remove picker over exactly the removable ones.

create or replace function public.mod_remove_target(
    p_target_type text, p_target_id uuid, p_actor text default null, p_message text default null)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_table text; v_owner uuid; v_name text; v_label text; v_msg text;
    v_custom text := nullif(btrim(coalesce(p_message, '')), '');
begin
    v_table := case p_target_type
        when 'preset' then 'presets' when 'game_preset' then 'game_presets'
        when 'custom_engine' then 'custom_engines' when 'pack' then 'packs' end;
    if v_table is null then return jsonb_build_object('error', 'bad target_type'); end if;
    execute format('update public.%I set is_suppressed = true, suppressed_by_ban = false where id = $1', v_table) using p_target_id;
    execute format('select owner_user_id, name from public.%I where id = $1', v_table) into v_owner, v_name using p_target_id;
    if not found or v_owner is null then
        return jsonb_build_object('ok', true, 'name', v_name, 'owner', null);
    end if;
    v_label := case p_target_type
        when 'preset' then 'car preset' when 'game_preset' then 'game preset'
        when 'custom_engine' then 'custom engine' else 'pack' end;
    v_msg := 'Your ' || v_label || ' "' || coalesce(v_name, '') || '" was removed after moderator review. '
          || coalesce(v_custom, 'You can request a review if you believe this was a mistake.');
    insert into public.moderation_notices (user_id, target_type, target_id, kind, message, created_by)
        values (v_owner, p_target_type, p_target_id, 'removal', v_msg, p_actor);
    return jsonb_build_object('ok', true, 'name', v_name, 'owner', v_owner);
end;
$$;
revoke all on function public.mod_remove_target(text, uuid, text, text) from public, anon, authenticated;
grant  execute on function public.mod_remove_target(text, uuid, text, text) to service_role;

create or replace function public.mod_user_footprint(p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
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
                select 'preset'        as kind, id, name, is_suppressed, suppressed_by_ban, downloads, created_at from public.presets        where owner_user_id = p_user_id
                union all select 'game_preset',   id, name, is_suppressed, suppressed_by_ban, downloads, created_at from public.game_presets   where owner_user_id = p_user_id
                union all select 'custom_engine', id, name, is_suppressed, suppressed_by_ban, downloads, created_at from public.custom_engines where owner_user_id = p_user_id
                union all select 'pack',          id, name, is_suppressed, suppressed_by_ban, downloads, created_at from public.packs          where owner_user_id = p_user_id
                limit 60
            ) x)
    ) into v;
    return v;
end;
$$;
revoke all on function public.mod_user_footprint(uuid) from public, anon, authenticated;
grant  execute on function public.mod_user_footprint(uuid) to service_role;
