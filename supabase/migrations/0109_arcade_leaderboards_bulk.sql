-- 0109: every board for a game in one call.
--
-- The plugin fills the game's in-game leaderboards, and the game has 32 of them (16 courses x 2
-- directions). Reading them one at a time meant a course was only filled once the player drove it,
-- so browsing the leaderboard menu showed empty boards for everywhere they had not been that
-- session. Filling all 32 up front is the fix, and doing that as 32 round trips would be silly:
-- the whole payload is at most 320 rows.
--
-- Same dedupe rule as get_arcade_leaderboard: one row per user per board, at their best.

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
    car_id      integer)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with best as (
        select distinct on (t.course_id, t.direction, t.user_id)
               t.course_id, t.direction, t.user_id, t.author, t.goal_ms,
               t.section1_ms, t.section2_ms, t.section3_ms, t.car_id, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (
              select 1 from public.submitter_blocked b
              where b.submitter_id = t.user_id::text
                and (b.banned_until is null or b.banned_until > now()))
        order by t.course_id, t.direction, t.user_id, t.goal_ms asc
    ), ranked as (
        select b.*,
               (row_number() over (partition by b.course_id, b.direction
                                   order by b.goal_ms asc, b.updated_at asc))::integer as r
        from best b
    )
    select r.course_id, r.direction, r.r, r.author, r.goal_ms,
           r.section1_ms, r.section2_ms, r.section3_ms, r.car_id
    from ranked r
    where r.r <= greatest(1, least(coalesce(p_limit, 10), 50))
    order by r.course_id, r.direction, r.r;
$function$;

revoke all on function public.get_arcade_leaderboards_all(text, integer) from anon, public;
grant execute on function public.get_arcade_leaderboards_all(text, integer) to authenticated;
