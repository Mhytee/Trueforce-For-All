-- 0030: get_my_presets dropped the author field from its CTE selects and final
-- jsonb_build_object, so every row in the "My Uploads" view came back with no
-- author and the client fell through to its "(anonymous)" placeholder. Same
-- RPC also omitted owner_user_id, content_version, updated_at, allow_in_packs,
-- entry_count / author_version (packs), target_games (game presets) - all of
-- which the browse UI shows or uses for ownership / chooser gating. This
-- rewrite adds them so the My Uploads view reads like the kind-specific list
-- RPCs without changing any RLS or auth gating.

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
          from public.presets where owner_user_id = v_uid
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
          from public.game_presets where owner_user_id = v_uid
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
          from public.custom_engines where owner_user_id = v_uid
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
          from public.packs where owner_user_id = v_uid
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
