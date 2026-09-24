-- A win is worth 1.5x, not 2x.
--
-- At 2x, winning a 50-driver board scored 1128 against 988 for tenth of a thousand, which put a
-- small-field win above a strong finish in a large one. That is the exact inversion this scoring
-- was changed to remove. At 1.5x the same win is 846 and tenth of a thousand keeps its 988, while
-- a win is still plainly the best thing you can do on any given board.
create or replace function public.get_arcade_scores(p_game text)
returns table (user_id uuid, author text, course_crowns integer, car_crowns integer,
               course_top_ten integer, boards_entered integer, points integer, rank integer)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with live as (
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
    course_scored as (
        select c.user_id, c.r, c.players,
               round(100.0 * (c.players - c.r) / greatest(c.players - 1, 1)
                     * log(2.0, greatest(c.players, 1)::numeric)
                     * case when c.r = 1 then 1.5 else 1 end)::integer as pts
        from course_ranked c
    ),
    car_field as (
        select course_id, direction, car_id, count(*)::integer as players
        from live group by 1, 2, 3
    ),
    car_ranked as (
        select l.course_id, l.direction, l.car_id, l.user_id, f.players,
               (row_number() over (partition by l.course_id, l.direction, l.car_id
                                   order by l.goal_ms asc, l.updated_at asc))::integer as r
        from live l
        join car_field f on f.course_id = l.course_id and f.direction = l.direction
                        and f.car_id = l.car_id
    ),
    car_scored as (
        select c.user_id, c.r, c.players,
               round(100.0 * (c.players - c.r) / greatest(c.players - 1, 1)
                     * log(2.0, greatest(c.players, 1)::numeric)
                     * case when c.r = 1 then 1.5 else 1 end)::integer as pts
        from car_ranked c
    ),
    car_top as (
        select user_id, pts from (
            select cs.user_id, cs.pts,
                   row_number() over (partition by cs.user_id order by cs.pts desc) as n
            from car_scored cs
        ) x where x.n <= 10
    ),
    agg as (
        select u.user_id,
               (select p.username from public.profiles p where p.id = u.user_id) as author,
               coalesce((select count(*) from course_scored c where c.user_id = u.user_id and c.r = 1), 0)::integer as cc,
               coalesce((select count(*) from car_scored c where c.user_id = u.user_id and c.r = 1 and c.players >= 2), 0)::integer as carc,
               coalesce((select count(*) from course_scored c where c.user_id = u.user_id and c.r <= 10), 0)::integer as t10,
               coalesce((select count(distinct (l.course_id, l.direction)) from live l where l.user_id = u.user_id), 0)::integer as be,
               coalesce((select sum(c.pts) from course_scored c where c.user_id = u.user_id), 0)::integer as cpts,
               coalesce((select sum(t.pts) from car_top t where t.user_id = u.user_id), 0)::integer as rpts
        from (select distinct l.user_id from live l) u
    ),
    scored as (select a.*, (a.cpts + a.rpts + a.be)::integer as pts from agg a)
    select s.user_id, s.author, s.cc, s.carc, s.t10, s.be, s.pts,
           (row_number() over (order by s.pts desc, s.cc desc, s.be desc))::integer
    from scored s;
$function$;

revoke all on function public.get_arcade_scores(text) from anon, public;
grant execute on function public.get_arcade_scores(text) to authenticated;