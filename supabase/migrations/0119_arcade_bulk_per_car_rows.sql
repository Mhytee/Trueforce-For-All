-- 0119: the bulk board RPC stops collapsing a person to a single time.
--
-- It returned one row per user per board, their fastest, which is the right shape for a ten-place
-- board once a server has ten drivers on it and the wrong shape for the server that has one. A
-- player who has driven six cars on a course has set six times worth reading, and returning only
-- their best left nine of the ten in-game ranks showing TF4ALL filler: a board that reads as broken
-- rather than as new.
--
-- The board is now what a time-attack board says it is: the ten fastest TIMES. Not the ten fastest
-- people. Ranking people and backfilling the spare places would put somebody above a time that
-- actually beat them, which states something untrue on screen, so the plugin simply sorts what it
-- is given. This migration stops the collapse happening here, where the plugin cannot undo it.
--
-- One row per user per CAR, not every row: a second, slower run in the same car is still not worth
-- a rank. That matches the table's own unique key, (game, course_id, direction, car_id, user_id).
--
-- The cap moves with it. p_limit was ten because ten was all a board could show; the plugin now
-- wants candidates rather than a finished board, so the ceiling goes to 200. The whole payload is
-- still small: 32 boards is a few hundred rows.

drop function if exists public.get_arcade_leaderboards_all(text, integer);

create or replace function public.get_arcade_leaderboards_all(
    p_game  text,
    p_limit integer default 10)
returns table (
    course_id   smallint,
    direction   smallint,
    rank        integer,
    author      text,
    goal_ms     integer,
    section1_ms integer,
    section2_ms integer,
    section3_ms integer,
    car_id      integer,
    set_at      timestamptz)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with best as (
        select distinct on (t.course_id, t.direction, t.user_id, t.car_id)
               t.course_id, t.direction, t.user_id, t.author, t.goal_ms,
               t.section1_ms, t.section2_ms, t.section3_ms, t.car_id, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (
              select 1 from public.submitter_blocked b
              where b.submitter_id = t.user_id::text
                and (b.banned_until is null or b.banned_until > now()))
        order by t.course_id, t.direction, t.user_id, t.car_id, t.goal_ms asc
    ), ranked as (
        select b.*,
               (row_number() over (partition by b.course_id, b.direction
                                   order by b.goal_ms asc, b.updated_at asc))::integer as r
        from best b
    )
    select r.course_id, r.direction, r.r, r.author, r.goal_ms,
           r.section1_ms, r.section2_ms, r.section3_ms, r.car_id, r.updated_at
    from ranked r
    where r.r <= greatest(1, least(coalesce(p_limit, 10), 200))
    order by r.course_id, r.direction, r.r;
$function$;

revoke all on function public.get_arcade_leaderboards_all(text, integer) from anon, public;
grant execute on function public.get_arcade_leaderboards_all(text, integer) to authenticated;
