-- Was anybody else there?
--
-- A course record on a board only you have driven is not a win, it is a first time. The scoring
-- already says so, because beating nobody is worth nothing, but the readouts did not: /id8 me
-- reported "18 course records" directly above "18 pts" and left a reader to reconcile a bragging
-- line with a score that prices those records at zero.
--
-- So the count comes back split. Contested crowns are wins over somebody. The rest are invitations,
-- and can be read as such: "first time on this board, nobody has challenged it yet" invites the
-- next driver, where "record" just sounds hollow.
--
-- Contested means at least two TRUEFORCE FOR ALL drivers on that board, matching the scope of the
-- number it sits beside. The merged standing answers the other question separately.
drop function if exists public.get_arcade_player_stats(text, uuid);

create or replace function public.get_arcade_player_stats(p_game text, p_user_id uuid default null)
returns table (user_id uuid, author text, course_crowns integer, course_crowns_contested integer,
               car_crowns integer, course_top_ten integer, boards_entered integer, cars_driven integer,
               favourite_car_id integer, favourite_car_runs integer, best_course_finish integer,
               overall_rank integer, players_ranked integer,
               merged_rank integer, merged_players integer, points integer)
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
    merged as (select * from public.get_arcade_scores_merged(p_game))
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
           coalesce((select s.points from scores s where s.user_id = who.uid), 0)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;