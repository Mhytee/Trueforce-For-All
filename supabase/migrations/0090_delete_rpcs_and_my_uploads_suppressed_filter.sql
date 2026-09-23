-- 0090: owner-delete for game presets / packs / custom engines, and make the
-- "My uploads" listing honour suppression.
--
-- Before this, the only owner-facing delete RPC was delete_preset (car presets
-- only). The plugin's Delete button on a game-preset / pack / custom-engine
-- upload called delete_preset with that row's id, which raised "preset not
-- found" (the id lives in a different table), so the delete silently failed and
-- the row stayed. This adds the three missing soft-delete RPCs, mirroring
-- delete_preset exactly (owner check, then is_suppressed = true so the row drops
-- out of public browse via RLS while its vote history stays for moderation).
--
-- It also fixes two latent issues that affected car deletes too:
--   * get_my_presets returned suppressed rows (it never filtered is_suppressed),
--     and the client renders them as normal "live" rows. So a deleted upload
--     reappeared on the next refresh, and moderator-removed rows showed live
--     instead of greyed. Add `and not is_suppressed` to every CTE.
--   * get_my_uploads surfaces suppressed rows as greyed state-tagged rows so the
--     owner can fix / appeal a MODERATOR action. An owner SELF-delete is also
--     suppressed, so it came back as a greyed "Removed" row with an appeal
--     button. Distinguish the two: a moderator action always leaves a
--     moderation_notice (and a ban sets suppressed_by_ban); a self-delete leaves
--     neither. Drop suppressed rows that have neither, so a self-delete truly
--     disappears from the owner's view while moderator removals stay appealable.

-- ---- delete_game_preset --------------------------------------------------
create or replace function public.delete_game_preset(
    p_game_preset_id uuid
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.game_presets
     where id = p_game_preset_id;
    if not found then raise exception 'game preset not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your game preset';
    end if;
    update public.game_presets set is_suppressed = true where id = p_game_preset_id;
    return jsonb_build_object('id', p_game_preset_id, 'deleted', true);
end;
$$;

-- ---- delete_pack ---------------------------------------------------------
create or replace function public.delete_pack(
    p_pack_id uuid
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.packs
     where id = p_pack_id;
    if not found then raise exception 'pack not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your pack';
    end if;
    update public.packs set is_suppressed = true where id = p_pack_id;
    return jsonb_build_object('id', p_pack_id, 'deleted', true);
end;
$$;

-- ---- delete_custom_engine ------------------------------------------------
create or replace function public.delete_custom_engine(
    p_custom_engine_id uuid
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.custom_engines
     where id = p_custom_engine_id;
    if not found then raise exception 'custom engine not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your custom engine';
    end if;
    update public.custom_engines set is_suppressed = true where id = p_custom_engine_id;
    return jsonb_build_object('id', p_custom_engine_id, 'deleted', true);
end;
$$;

-- Tighten execute to signed-in callers (the auth.uid() check is the real guard;
-- this matches the newer convention used by get_my_uploads).
revoke all on function public.delete_game_preset(uuid)   from public, anon;
revoke all on function public.delete_pack(uuid)           from public, anon;
revoke all on function public.delete_custom_engine(uuid)  from public, anon;
grant execute on function public.delete_game_preset(uuid)  to authenticated;
grant execute on function public.delete_pack(uuid)         to authenticated;
grant execute on function public.delete_custom_engine(uuid) to authenticated;

-- ---- get_my_presets: hide suppressed rows from the live listing ----------
-- The greyed moderation rows + appeal flow come from get_my_uploads; the normal
-- listing must NOT surface suppressed rows or they render as live and a delete
-- looks like it didn't stick.
create or replace function public.get_my_presets(p_sort text default 'newest', p_limit int default 100)
returns setof jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_lim int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_lim := greatest(1, least(coalesce(p_limit, 100), 500));
    return query
    with car_rows as (
        select id, 'car'::text as kind, name, author, description, game, car_id,
               effect_tags, upvotes, downvotes, wilson_score, downloads,
               created_at, updated_at, owner_user_id, content_version,
               allow_in_packs,
               null::int       as entry_count,
               null::text      as author_version,
               '{}'::text[]    as target_games,
               is_suppressed
          from public.presets where owner_user_id = v_uid and not is_suppressed
    ),
    game_rows as (
        select id, 'game'::text as kind, name, author, description, game,
               '*'::text as car_id, effect_tags, upvotes, downvotes,
               wilson_score, downloads, created_at, updated_at, owner_user_id,
               content_version, allow_in_packs,
               null::int       as entry_count,
               null::text      as author_version,
               target_games,
               is_suppressed
          from public.game_presets where owner_user_id = v_uid and not is_suppressed
    ),
    engine_rows as (
        select id, 'engine'::text as kind, name, author, description,
               ''::text as game, ''::text as car_id, '{}'::text[] as effect_tags,
               upvotes, downvotes, wilson_score, downloads,
               created_at, updated_at, owner_user_id, content_version,
               allow_in_packs,
               null::int       as entry_count,
               null::text      as author_version,
               '{}'::text[]    as target_games,
               is_suppressed
          from public.custom_engines where owner_user_id = v_uid and not is_suppressed
    ),
    pack_rows as (
        select id, 'pack'::text as kind, name, author, description,
               ''::text as game, ''::text as car_id, '{}'::text[] as effect_tags,
               upvotes, downvotes, wilson_score, downloads,
               created_at, updated_at, owner_user_id, content_version,
               null::boolean   as allow_in_packs,
               entry_count,
               author_version,
               '{}'::text[]    as target_games,
               is_suppressed
          from public.packs where owner_user_id = v_uid and not is_suppressed
    ),
    combined as (
        select * from car_rows
        union all select * from game_rows
        union all select * from engine_rows
        union all select * from pack_rows
    )
    select jsonb_build_object(
        'id', id, 'kind', kind, 'name', name, 'author', author,
        'description', description,
        'game', game, 'car_id', car_id, 'effect_tags', effect_tags,
        'upvotes', upvotes, 'downvotes', downvotes,
        'wilson_score', wilson_score, 'downloads', downloads,
        'created_at', created_at, 'updated_at', updated_at,
        'owner_user_id', owner_user_id, 'content_version', content_version,
        'allow_in_packs', allow_in_packs,
        'entry_count', entry_count, 'author_version', author_version,
        'target_games', target_games,
        'is_suppressed', is_suppressed)
      from combined
     order by
        case when p_sort = 'top'       then wilson_score end desc nulls last,
        case when p_sort = 'downloads' then downloads    end desc nulls last,
        downloads     desc,
        wilson_score  desc,
        created_at    desc
     limit v_lim;
end;
$$;

-- ---- get_my_uploads: don't resurface owner self-deletes ------------------
-- Keep: live rows, ban-suspended rows, and any row carrying a moderation notice
-- (removed / under_review, so the owner can fix + appeal). Drop: suppressed rows
-- with no ban and no notice = an owner self-delete.
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
