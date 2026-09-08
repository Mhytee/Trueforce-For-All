-- 0121: score what you BEAT, weighted by how many there were to beat.
--
-- The old scoring was backwards on the thing that matters most. A course crown was a flat 25 and a
-- car crown a flat 5, so WINNING A TWO-DRIVER BOARD SCORED 35 while being tenth of a thousand on
-- the AE86, the most contested car in the game, scored 1, and two hundredth scored nothing at all.
-- Beating an empty board outranked a real result against the field.
--
--     board score = round(100 * (players - rank) / (players - 1) * log2(players)), 1.5x for a win
--
-- Read plainly: the share of the field you beat, weighted by how big that field was. log2 of a
-- one-driver board is 0, so beating nobody is worth nothing however obscure the board, which is the
-- whole correction.
--
-- 1.5x for a win, not 2x. At 2x, winning a 50-driver board scored 1128 against 988 for tenth of a
-- thousand, which put a small-field win back above a strong finish in a large one, reintroducing
-- exactly what this migration removes. The applied ledger therefore holds two entries for this
-- change, 0121 and 0122; this file is the settled form and replaying it alone converges.
--
--     win the AE86 board (1000)  1495      win a 50-driver board   847
--     10th of 1000                988      200th of 1000           798
--     win a 5-driver board        348      alone on a board          0
--
-- Car boards are capped at your best ten. Without a cap, driving more cars would outscore driving
-- better, which is the same farming the old flat car-crown cap guarded against.
--
-- One point per board entered, so a community too young to have a second driver still has a
-- ranking. Everything else measures who you beat, and beating nobody is worth nothing, which would
-- otherwise leave every early player on zero and ordered by tiebreak alone.

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
