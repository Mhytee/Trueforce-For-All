-- 0110: the per-car boards.
--
-- ID8 has a second pair of leaderboards keyed (course, direction, car), and unlike the top-ten
-- boards they hold exactly ONE row per car: that car's best on that course, with the driver's name.
-- So this is a different shape from get_arcade_leaderboards_all, which returns ten ranks per board.
--
-- One call for the lot, same reasoning as 0109: there are up to 32 x 50 of these and doing them
-- one at a time would be absurd. In practice it is far fewer, because most cars have no times.

drop function if exists public.get_arcade_car_bests(text);

create or replace function public.get_arcade_car_bests(p_game text)
returns table (
    course_id   smallint,
    direction   smallint,
    car_id      integer,
    author      text,
    goal_ms     integer,
    section1_ms integer,
    section2_ms integer,
    section3_ms integer,
    set_at      timestamptz)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    select distinct on (t.course_id, t.direction, t.car_id)
           t.course_id, t.direction, t.car_id, t.author, t.goal_ms,
           t.section1_ms, t.section2_ms, t.section3_ms, t.updated_at
    from public.arcade_lap_times t
    where t.game = p_game
      and not exists (
          select 1 from public.submitter_blocked b
          where b.submitter_id = t.user_id::text
            and (b.banned_until is null or b.banned_until > now()))
    order by t.course_id, t.direction, t.car_id, t.goal_ms asc, t.updated_at asc;
$function$;

revoke all on function public.get_arcade_car_bests(text) from anon, public;
grant execute on function public.get_arcade_car_bests(text) to authenticated;
