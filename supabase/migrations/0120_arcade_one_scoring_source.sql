-- 0120: one place computes a player's score, and "rank" means one thing.
--
-- get_arcade_player_stats reported best_rank, which is min(r) across the course boards, i.e. the
-- best finish on any single course. /id8 me rendered it as "ranked N overall". So somebody who won
-- one course was told they were ranked 1 overall. The number was real and the label was wrong.
--
-- It drifted because the points formula was written out twice, once in get_arcade_overall_ranking
-- and once here, and nothing made the two agree. get_arcade_scores is now the only place that
-- decides what a player is worth, and both callers read it.
--
-- EVERYONE IS RANKED. row_number() runs over every player with a lap time, not just the ones who
-- score, so a player outside every board's top ten still has a position and can be told it. That
-- is the point: a standing you can only have by being good is a trophy, not a standing. The
-- hundredth best player wants to know they are hundredth.
--
-- players_ranked comes back with it so the answer can be "42 of 118" rather than a bare number
-- whose meaning depends on how many people are playing.
--
-- The scoring itself is UNCHANGED here on purpose. It still awards nothing below a board's top
-- ten, so a player who has driven every course and sits eleventh on each scores zero and is ranked
-- only by the tiebreaks. That is a real gap and worth closing, but it is a decision about what the
-- community is measured on rather than a bug, so it is left for the owner rather than smuggled in
-- alongside a correctness fix.

create or replace function public.get_arcade_scores(p_game text)
returns table (
    user_id        uuid,
    author         text,
    course_crowns  integer,
    car_crowns     integer,
    course_top_ten integer,
    boards_entered integer,
    points         integer,
    rank           integer)
language sql
stable
security definer
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
    -- One row per player per course, their best. A player holding several places on a board with
    -- different cars is still one competitor on that course, so multiple cars cannot farm points.
    course_best as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.goal_ms, l.updated_at
        from live l
        order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (partition by cb.course_id, cb.direction
                                         order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    car_boards as (
        select l.course_id, l.direction, l.car_id, count(*)::integer as players
        from live l group by 1, 2, 3
    ),
    car_ranked as (
        select l.course_id, l.direction, l.car_id, l.user_id,
               (row_number() over (partition by l.course_id, l.direction, l.car_id
                                   order by l.goal_ms asc, l.updated_at asc))::integer as r
        from live l
    ),
    -- players >= 2: being the only person to drive a car is not a record over anybody.
    car_crowns as (
        select c.user_id, count(*)::integer as n
        from car_ranked c
        join car_boards b on b.course_id = c.course_id
                         and b.direction = c.direction
                         and b.car_id   = c.car_id
        where c.r = 1 and b.players >= 2
        group by c.user_id
    ),
    agg as (
        select u.user_id,
               (select p.username from public.profiles p where p.id = u.user_id) as author,
               coalesce((select count(*) from course_ranked c where c.user_id = u.user_id and c.r = 1), 0)::integer as cc,
               coalesce((select n from car_crowns c where c.user_id = u.user_id), 0)::integer as carc,
               coalesce((select count(*) from course_ranked c where c.user_id = u.user_id and c.r <= 10), 0)::integer as t10,
               coalesce((select count(distinct (l.course_id, l.direction)) from live l where l.user_id = u.user_id), 0)::integer as be,
               coalesce((select sum(11 - c.r) from course_ranked c where c.user_id = u.user_id and c.r <= 10), 0)::integer as tp
        from (select distinct l.user_id from live l) u
    ),
    scored as (
        select a.*, (a.cc * 25 + least(a.carc, 10) * 5 + a.tp)::integer as pts from agg a
    )
    select s.user_id, s.author, s.cc, s.carc, s.t10, s.be, s.pts,
           (row_number() over (order by s.pts desc, s.cc desc, s.be desc))::integer
    from scored s;
$function$;

revoke all on function public.get_arcade_scores(text) from anon, public;
grant execute on function public.get_arcade_scores(text) to authenticated;

-- Both callers now read the one source.

drop function if exists public.get_arcade_overall_ranking(text, integer);

create or replace function public.get_arcade_overall_ranking(p_game text, p_limit integer default 25)
returns table (
    rank           integer,
    user_id        uuid,
    author         text,
    course_crowns  integer,
    car_crowns     integer,
    course_top_ten integer,
    boards_entered integer,
    points         integer)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    select s.rank, s.user_id, s.author, s.course_crowns, s.car_crowns,
           s.course_top_ten, s.boards_entered, s.points
    from public.get_arcade_scores(p_game) s
    order by s.rank
    limit greatest(1, least(coalesce(p_limit, 25), 200));
$function$;

revoke all on function public.get_arcade_overall_ranking(text, integer) from anon, public;
grant execute on function public.get_arcade_overall_ranking(text, integer) to authenticated;

drop function if exists public.get_arcade_player_stats(text, uuid);

create or replace function public.get_arcade_player_stats(p_game text, p_user_id uuid default null)
returns table (
    user_id           uuid,
    author            text,
    course_crowns     integer,
    car_crowns        integer,
    course_top_ten    integer,
    boards_entered    integer,
    cars_driven       integer,
    favourite_car_id  integer,
    favourite_car_runs integer,
    best_course_finish integer,
    overall_rank      integer,
    players_ranked    integer,
    points            integer)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with who as (select coalesce(p_user_id, auth.uid()) as uid),
    live as (
        select t.*
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    course_best as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.goal_ms, l.updated_at
        from live l
        order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (partition by cb.course_id, cb.direction
                                         order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    fav as (
        select l.car_id, count(*)::integer as runs
        from live l, who
        where l.user_id = who.uid
        group by l.car_id
        order by count(*) desc, min(l.goal_ms) asc
        limit 1
    ),
    scores as (select * from public.get_arcade_scores(p_game))
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           coalesce((select s.course_crowns  from scores s where s.user_id = who.uid), 0),
           coalesce((select s.car_crowns     from scores s where s.user_id = who.uid), 0),
           coalesce((select s.course_top_ten from scores s where s.user_id = who.uid), 0),
           coalesce((select s.boards_entered from scores s where s.user_id = who.uid), 0),
           (select count(distinct l.car_id)::integer from live l where l.user_id = who.uid),
           (select car_id from fav),
           (select runs from fav),
           -- Their best finish on any ONE course. Kept because it is worth knowing, renamed
           -- because calling it a rank is what made /id8 me report a course win as an overall
           -- standing.
           coalesce((select min(c.r) from course_ranked c where c.user_id = who.uid), 0),
           (select s.rank from scores s where s.user_id = who.uid),
           (select count(*)::integer from scores),
           coalesce((select s.points from scores s where s.user_id = who.uid), 0)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;
