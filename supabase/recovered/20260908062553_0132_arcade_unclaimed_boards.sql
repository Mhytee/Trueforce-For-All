-- Which boards are open, and how open they really are.
--
-- The digest's invitation section needs three things it could not get: who holds a board where
-- nobody else HERE has driven it, how that time looks against the world, and how many boards this
-- server has not touched at all.
--
-- The worldwide rank is what stops the section reading as a victory lap. "2:19.932, unchallenged"
-- sounds untouchable; "2:19.932, which is 17th of 61 worldwide" reads as a target.
create or replace function public.get_arcade_unclaimed(p_game text)
returns jsonb
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with live as (
        select t.*
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    -- One row per board per driver, their best, so a driver with several cars is one competitor.
    per_driver as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.author, l.goal_ms
        from live l order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    boards as (
        select course_id, direction, count(*)::integer as here, min(goal_ms) as best_ms
        from per_driver group by 1, 2
    ),
    -- Held by exactly one person on this server: nobody else HERE has taken it on.
    solo as (
        select b.course_id, b.direction, b.best_ms,
               (select p.author from per_driver p
                 where p.course_id = b.course_id and p.direction = b.direction limit 1) as author
        from boards b where b.here = 1
    ),
    world as (select * from public.get_arcade_board_ranks_merged(p_game))
    select jsonb_build_object(
        'held', coalesce((select jsonb_agg(jsonb_build_object(
                    'author', s.author, 'course_id', s.course_id, 'direction', s.direction,
                    'goal_ms', s.best_ms,
                    'world_rank', (select w.rank from world w
                                    where w.course_id = s.course_id and w.direction = s.direction
                                      and w.goal_ms = s.best_ms limit 1),
                    'world_of', (select w.players from world w
                                  where w.course_id = s.course_id and w.direction = s.direction
                                  limit 1))
                order by s.best_ms)
            from solo s), '[]'::jsonb),
        'undriven', 32 - (select count(*)::integer from boards)
    );
$function$;

revoke all on function public.get_arcade_unclaimed(text) from anon, public;
grant execute on function public.get_arcade_unclaimed(text) to authenticated;

-- Fold it into the weekly payload without retyping the whole function.
do $$
declare d text; n int;
begin
    select pg_get_functiondef(p.oid) into d
    from pg_proc p join pg_namespace n2 on n2.oid = p.pronamespace
    where n2.nspname = 'public' and p.proname = 'get_arcade_week';
    if d is null then raise exception 'get_arcade_week not found'; end if;

    n := (length(d) - length(replace(d, '''top'', coalesce(', ''))) / length('''top'', coalesce(');
    if n <> 1 then raise exception 'expected one top key, found %', n; end if;

    d := replace(d, '''top'', coalesce(',
                    '''unclaimed'', public.get_arcade_unclaimed(p_game),' || chr(10) ||
                    '        ''top'', coalesce(');
    execute d;
end $$;