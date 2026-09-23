-- 0118: the bulk board RPC returns the date it already sorts by.
--
-- The 48-byte game record carries a date field, and the plugin writes it from the entry's
-- UnixTime. get_arcade_leaderboards_all never returned one, so every community row written to the
-- two top-ten boards carried epoch 0. The single-board get_arcade_leaderboard has always returned
-- set_at; only the bulk call, which is the one actually used, did not.
--
-- Nothing new is computed. 0109 already selects updated_at into its `best` CTE and orders on it to
-- break ties; this only carries that value out to the caller.
--
-- Adding a column to the return table is safe for a plugin that has not been updated: the client
-- parses by name and ignores what it does not know. A plugin that DOES read it treats a missing
-- set_at as 0, which is exactly the behaviour it had before, so the two versions cross in either
-- direction without a coordinated release.

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
           r.section1_ms, r.section2_ms, r.section3_ms, r.car_id, r.updated_at
    from ranked r
    where r.r <= greatest(1, least(coalesce(p_limit, 10), 50))
    order by r.course_id, r.direction, r.r;
$function$;

revoke all on function public.get_arcade_leaderboards_all(text, integer) from anon, public;
grant execute on function public.get_arcade_leaderboards_all(text, integer) to authenticated;
