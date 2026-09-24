-- "best finish 17th" on its own is a trivia number. Where, in what, and how fast is the useful
-- version, and it is the one a driver can act on: it names the board they are closest to breaking
-- into and the car they were in when they got there.
drop function if exists public.get_arcade_player_stats(text, uuid);

create or replace function public.get_arcade_player_stats(p_game text, p_user_id uuid default null)
returns table (user_id uuid, author text, course_crowns integer, course_crowns_contested integer,
               car_crowns integer, course_top_ten integer, boards_entered integer, cars_driven integer,
               favourite_car_id integer, favourite_car_runs integer, best_course_finish integer,
               overall_rank integer, players_ranked integer,
               merged_rank integer, merged_players integer,
               merged_crowns integer, merged_top_ten integer, merged_best_finish integer,
               merged_best_course smallint, merged_best_direction smallint,
               merged_best_ms integer, merged_best_players integer, merged_best_car_id integer,
               points integer)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with who as (select coalesce(p_user_id, auth.uid()) as uid),
    live as (
        select t.* from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    course_best as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.goal_ms, l.updated_at
        from live l order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    course_field as (
        select course_id, direction, count(*)::integer as players
        from course_best group by 1, 2
    ),
    course_ranked as (
        select cb.*, f.players,
               (row_number() over (partition by cb.course_id, cb.direction
                                   order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
        join course_field f on f.course_id = cb.course_id and f.direction = cb.direction
    ),
    fav as (
        select l.car_id, count(*)::integer as runs
        from live l, who where l.user_id = who.uid
        group by l.car_id order by count(*) desc, min(l.goal_ms) asc limit 1
    ),
    scores as (select * from public.get_arcade_scores(p_game)),
    merged as (select * from public.get_arcade_scores_merged(p_game)),
    mine_merged as (
        select b.* from public.get_arcade_board_ranks_merged(p_game) b, who
        where b.who = 'u:' || who.uid::text
    ),
    -- The single board they placed highest on. Ties break on the faster time, so the one named is
    -- the better drive rather than whichever course happened to sort first.
    best_board as (
        select * from mine_merged order by rank asc, goal_ms asc limit 1
    )
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           coalesce((select s.course_crowns  from scores s where s.user_id = who.uid), 0),
           (select count(*)::integer from course_ranked c
             where c.user_id = who.uid and c.r = 1 and c.players >= 2),
           coalesce((select s.car_crowns     from scores s where s.user_id = who.uid), 0),
           coalesce((select s.course_top_ten from scores s where s.user_id = who.uid), 0),
           coalesce((select s.boards_entered from scores s where s.user_id = who.uid), 0),
           (select count(distinct l.car_id)::integer from live l where l.user_id = who.uid),
           (select car_id from fav),
           (select runs from fav),
           coalesce((select min(c.r) from course_ranked c where c.user_id = who.uid), 0),
           (select s.rank from scores s where s.user_id = who.uid),
           (select count(*)::integer from scores),
           (select m.rank from merged m where m.who = 'u:' || who.uid::text),
           (select count(*)::integer from merged),
           (select count(*)::integer from mine_merged where rank = 1),
           (select count(*)::integer from mine_merged where rank <= 10),
           coalesce((select rank from best_board), 0),
           (select course_id from best_board),
           (select direction from best_board),
           (select goal_ms from best_board),
           (select players from best_board),
           -- Which car that time was actually set in. The merged boards are course boards and carry
           -- no car, so it comes from our own row for the same board and time.
           (select l.car_id from live l, who, best_board bb
             where l.user_id = who.uid and l.course_id = bb.course_id
               and l.direction = bb.direction and l.goal_ms = bb.goal_ms
             limit 1),
           coalesce((select s.points from scores s where s.user_id = who.uid), 0)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;