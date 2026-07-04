-- get_my_uploads omitted pack entry_count, so a user's moderated/removed own
-- packs rendered "Items: 0" in My uploads. Add entry_count to the union (null
-- for non-pack kinds, real value for packs) and surface it in the result.
-- Applied to prod via MCP 2026-06-28; this file keeps the repo in sync.
create or replace function public.get_my_uploads()
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid := auth.uid(); v jsonb;
begin
    if v_uid is null then raise exception 'sign-in required'; end if;
    select coalesce(jsonb_agg(to_jsonb(x) order by x.created_at desc), '[]'::jsonb) into v
      from (
        select kind, id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at, entry_count,
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
            select 'preset'        as kind, id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at, null::int as entry_count from public.presets        where owner_user_id = v_uid
            union all select 'game_preset',   id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at, null::int from public.game_presets   where owner_user_id = v_uid
            union all select 'custom_engine', id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at, null::int from public.custom_engines where owner_user_id = v_uid
            union all select 'pack',          id, name, description, is_suppressed, suppressed_by_ban, downloads, created_at, entry_count from public.packs          where owner_user_id = v_uid
          ) u
         where not u.is_suppressed
            or u.suppressed_by_ban
            or exists (select 1 from public.moderation_notices mn
                        where mn.target_type = u.kind and mn.target_id = u.id and mn.user_id = v_uid)
      ) x;
    return v;
end;
$$;
revoke all on function public.get_my_uploads() from public, anon;
grant  execute on function public.get_my_uploads() to authenticated;
