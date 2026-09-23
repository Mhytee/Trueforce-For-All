-- 0111: close the scoring exploit, make bans actually work, and add a way to remove one bad time.
--
-- Everything here came out of an adversarial review of the arcade leaderboards, and none of it is
-- the integrity feature that review was called to design. Two of the four measures proposed (a
-- car-data file hash with a consensus rule, and a per-course floor seeded from TeknoParrot's
-- published times) were dropped as worthless: the hash is defeated by copying the files before
-- modding and is a permanent free bypass for any client that omits it, and the floor is publicly
-- computable from a cache already sitting on the player's own disk while only ever firing on
-- genuinely fast honest players. What the review found instead was that the exploit was already
-- shipped, in the scoring, and that the ban lever it would have fed did not work.
--
-- SQL only. submit_arcade_lap_time keeps its exact ten-parameter signature so create-or-replace
-- preserves the ACL, PostgREST still resolves the deployed client's body against a single
-- candidate, and no plugin release is needed.

-- ---- 1. the scoring exploit -------------------------------------------------
--
-- get_arcade_overall_ranking scored a car crown at 5 points with no cap and no minimum board
-- population. The game has 50 cars and 32 course/direction boards, so 1600 car boards exist and
-- essentially all are empty, and ANY time however slow is rank 1 on an empty one. At the existing
-- rate limit of 60 stored improvements an hour, a scripted account owns every car crown in about
-- a day for ~8000 points, against roughly 1120 for somebody who genuinely holds all 32 course
-- crowns and every top ten. Every forged row is a real row from a real account: nothing about the
-- times themselves is detectable, which is why no amount of per-submission validation would have
-- helped.
--
-- Two changes fix it. A crown on a board only you have ever driven is not a win, so car crowns
-- count only where the board has at least two distinct players. And the contribution is capped, so
-- breadth across cars can never dominate a course crown, which is the scarce thing worth holding.

-- ---- 2. bans that actually remove somebody ----------------------------------
--
-- get_arcade_leaderboard, _all, _car_bests and _overall_ranking filtered submitter_blocked.
-- get_arcade_course_cars, get_my_arcade_rank and get_arcade_player_stats did not, and neither did
-- the four crown-detection selects inside submit_arcade_lap_time. So a banned account still showed
-- as a board's best_author, still set everyone else's gap_ms, still counted as a course crown in
-- its own stats, and, worst of the four, permanently suppressed crown events on any board it led:
-- every honest player who beat it afterwards was compared against a row nobody could see, found
-- not to be first, and announced nothing. That reads as the feature being broken.
--
-- Always the full predicate. banned_until null means permanent and a timestamp means temporary
-- (0081), so testing only for the row's existence would make every temporary ban permanent.

-- ---- 3. a proportionate response --------------------------------------------
--
-- There was no way to delete one bad time. The only lever was submitter_blocked, which is global:
-- it also removes that person's presets, packs, custom engines and car facts. Detection with no
-- proportionate response produces a queue nobody acts on, so the removal lever ships BEFORE any
-- detection does.

begin;

-- ---- get_arcade_overall_ranking ---------------------------------------------

create or replace function public.get_arcade_overall_ranking(
    p_game  text,
    p_limit integer default 25)
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
    with live as (
        select t.*
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    course_best as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.author, l.goal_ms, l.updated_at
        from live l
        order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (partition by cb.course_id, cb.direction
                                         order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    -- arcade_lap_times_unique_user guarantees one row per user per car board, so count(*) IS the
    -- number of distinct players on that board. A separate group-by rather than a windowed
    -- count(distinct ...), which Postgres does not implement.
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
        -- Capped: breadth across cars must never outweigh a course crown, of which there are only
        -- 32 and which is the scarce thing actually worth holding.
        select a.*, (a.cc * 25 + least(a.carc, 10) * 5 + a.tp)::integer as pts from agg a
    )
    select (row_number() over (order by s.pts desc, s.cc desc, s.be desc))::integer,
           s.user_id, s.author, s.cc, s.carc, s.t10, s.be, s.pts
    from scored s
    order by s.pts desc, s.cc desc, s.be desc
    limit greatest(1, least(coalesce(p_limit, 25), 200));
$function$;

revoke all on function public.get_arcade_overall_ranking(text, integer) from anon, public;
grant execute on function public.get_arcade_overall_ranking(text, integer) to authenticated;

-- ---- get_arcade_player_stats ------------------------------------------------

create or replace function public.get_arcade_player_stats(
    p_game    text,
    p_user_id uuid default null)
returns table (
    user_id            uuid,
    author             text,
    course_crowns      integer,
    car_crowns         integer,
    course_top_ten     integer,
    boards_entered     integer,
    cars_driven        integer,
    favourite_car_id   integer,
    favourite_car_runs integer,
    best_rank          integer,
    points             integer)
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
    car_crowns as (
        select c.user_id, count(*)::integer as n
        from car_ranked c
        join car_boards b on b.course_id = c.course_id
                         and b.direction = c.direction
                         and b.car_id   = c.car_id
        where c.r = 1 and b.players >= 2
        group by c.user_id
    ),
    fav as (
        select l.car_id, count(*)::integer as runs
        from live l, who
        where l.user_id = who.uid
        group by l.car_id
        order by count(*) desc, min(l.goal_ms) asc
        limit 1
    )
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           (select count(*)::integer from course_ranked c where c.user_id = who.uid and c.r = 1),
           coalesce((select n from car_crowns c where c.user_id = who.uid), 0)::integer,
           (select count(*)::integer from course_ranked c where c.user_id = who.uid and c.r <= 10),
           (select count(distinct (l.course_id, l.direction))::integer from live l where l.user_id = who.uid),
           (select count(distinct l.car_id)::integer from live l where l.user_id = who.uid),
           (select car_id from fav),
           (select runs from fav),
           coalesce((select min(c.r) from course_ranked c where c.user_id = who.uid), 0),
           (coalesce((select count(*) from course_ranked c where c.user_id = who.uid and c.r = 1), 0)::integer * 25
          + least(coalesce((select n from car_crowns c where c.user_id = who.uid), 0), 10)::integer * 5
          + coalesce((select sum(11 - c.r) from course_ranked c where c.user_id = who.uid and c.r <= 10), 0)::integer)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;

-- ---- get_arcade_course_cars: add the ban filter -----------------------------

create or replace function public.get_arcade_course_cars(
    p_game      text,
    p_course_id smallint,
    p_direction smallint)
returns table (
    car_id       integer,
    entries      integer,
    best_ms      integer,
    best_author  text)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with live as (
        select t.*
        from public.arcade_lap_times t
        where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    )
    select l.car_id,
           count(distinct l.user_id)::integer,
           min(l.goal_ms)::integer,
           (select l2.author from live l2 where l2.car_id = l.car_id
             order by l2.goal_ms asc, l2.updated_at asc limit 1)
    from live l
    group by l.car_id
    order by min(l.goal_ms) asc;
$function$;

revoke all on function public.get_arcade_course_cars(text, smallint, smallint) from anon, public;
grant execute on function public.get_arcade_course_cars(text, smallint, smallint) to authenticated;

-- ---- get_my_arcade_rank: add the ban filter ---------------------------------
--
-- Without it a banned leader still sets the caller's gap_ms and pushes their rank down by one, so
-- a player would be told they are second to somebody who is not on the board.

create or replace function public.get_my_arcade_rank(
    p_game      text,
    p_course_id smallint,
    p_direction smallint,
    p_car_id    integer default null)
returns table (
    rank     integer,
    total    integer,
    goal_ms  integer,
    gap_ms   integer)
language sql
stable
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with best as (
        select distinct on (t.user_id) t.user_id, t.goal_ms
        from public.arcade_lap_times t
        where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
          and (p_car_id is null or t.car_id = p_car_id)
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
        order by t.user_id, t.goal_ms asc
    ), ranked as (
        select user_id, goal_ms,
               (row_number() over (order by goal_ms asc))::integer as r,
               (count(*) over ())::integer as n,
               (min(goal_ms) over ())::integer as leader
        from best
    )
    select coalesce(r.r, 0), coalesce((select n from ranked limit 1), 0),
           coalesce(r.goal_ms, 0), coalesce(r.goal_ms - r.leader, 0)
    from (select 1) one
    left join ranked r on r.user_id = auth.uid();
$function$;

revoke all on function public.get_my_arcade_rank(text, smallint, smallint, integer) from anon, public;
grant execute on function public.get_my_arcade_rank(text, smallint, smallint, integer) to authenticated;

-- ---- submit_arcade_lap_time: ban filter on all four crown selects -----------
--
-- Identical ten-parameter signature to 0108. Only the four crown-detection selects change: the two
-- that capture the previous holder and the two that read the new one. Anything else here is 0108
-- verbatim.

create or replace function public.submit_arcade_lap_time(
    p_game           text,
    p_course_id      smallint,
    p_direction      smallint,
    p_car_id         integer,
    p_goal_ms        integer,
    p_section1_ms    integer default null,
    p_section2_ms    integer default null,
    p_section3_ms    integer default null,
    p_tuning_level   smallint default null,
    p_plugin_version text default null)
returns jsonb
language plpgsql
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
declare
    v_uid       uuid := auth.uid();
    v_author    text;
    v_recent    integer;
    v_existing  integer;
    v_rows      integer := 0;
    v_cc_uid    uuid;   v_cc_name text; v_cc_ms integer;
    v_car_uid   uuid;   v_car_name text; v_car_ms integer;
    v_new_uid   uuid;
begin
    if v_uid is null then
        return jsonb_build_object('ok', false, 'error', 'sign-in required');
    end if;

    select p.username into v_author from public.profiles p where p.id = v_uid;
    if v_author is null then
        return jsonb_build_object('ok', false, 'error', 'set a username first');
    end if;

    if p_game is null or char_length(btrim(p_game)) = 0 then
        return jsonb_build_object('ok', false, 'error', 'game required');
    end if;
    if p_goal_ms is null or p_goal_ms <= 0 or p_goal_ms >= 360000 then
        return jsonb_build_object('ok', false, 'error', 'implausible time');
    end if;
    if p_course_id is null or p_course_id < 0 or p_course_id > 15
       or p_direction is null or p_direction < 0 or p_direction > 1 then
        return jsonb_build_object('ok', false, 'error', 'bad board key');
    end if;
    if p_car_id is null then
        return jsonb_build_object('ok', false, 'error', 'car required');
    end if;

    if exists (
        select 1 from public.submitter_blocked b
        where b.submitter_id = v_uid::text
          and (b.banned_until is null or b.banned_until > now())) then
        return jsonb_build_object('ok', true, 'stored', false);
    end if;

    select count(*) into v_recent
    from public.arcade_lap_times t
    where t.user_id = v_uid and t.updated_at > now() - interval '1 hour';

    if v_recent >= 60 then
        return jsonb_build_object('ok', false, 'error', 'rate limited');
    end if;

    select goal_ms into v_existing
    from public.arcade_lap_times
    where game = p_game and course_id = p_course_id and direction = p_direction
      and car_id = p_car_id and user_id = v_uid;

    if v_existing is not null and v_existing <= p_goal_ms then
        return jsonb_build_object('ok', true, 'stored', false, 'reason', 'not a personal best');
    end if;

    -- Previous course crown. A banned holder must be invisible here or nobody who beats them is
    -- ever seen to take the crown, and the board announces nothing again for good.
    select t.user_id, t.author, t.goal_ms into v_cc_uid, v_cc_name, v_cc_ms
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and not exists (select 1 from public.submitter_blocked b
                       where b.submitter_id = t.user_id::text
                         and (b.banned_until is null or b.banned_until > now()))
    order by t.goal_ms asc, t.updated_at asc
    limit 1;

    select t.user_id, t.author, t.goal_ms into v_car_uid, v_car_name, v_car_ms
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and t.car_id = p_car_id
      and not exists (select 1 from public.submitter_blocked b
                       where b.submitter_id = t.user_id::text
                         and (b.banned_until is null or b.banned_until > now()))
    order by t.goal_ms asc, t.updated_at asc
    limit 1;

    insert into public.arcade_lap_times as t
        (game, course_id, direction, car_id, tuning_level, goal_ms,
         section1_ms, section2_ms, section3_ms, user_id, author, plugin_version)
    values
        (p_game, p_course_id, p_direction, p_car_id, p_tuning_level, p_goal_ms,
         p_section1_ms, p_section2_ms, p_section3_ms, v_uid, v_author, p_plugin_version)
    on conflict (game, course_id, direction, car_id, user_id) do update
        set goal_ms        = excluded.goal_ms,
            section1_ms    = excluded.section1_ms,
            section2_ms    = excluded.section2_ms,
            section3_ms    = excluded.section3_ms,
            tuning_level   = excluded.tuning_level,
            author         = excluded.author,
            plugin_version = excluded.plugin_version,
            updated_at     = now()
        where excluded.goal_ms < t.goal_ms;

    get diagnostics v_rows = row_count;
    if v_rows = 0 then
        return jsonb_build_object('ok', true, 'stored', false, 'reason', 'not a personal best');
    end if;

    insert into public.arcade_events
        (kind, scope, game, course_id, direction, car_id, actor_user_id, actor_name, goal_ms)
    values
        ('time_set', 'car', p_game, p_course_id, p_direction, p_car_id, v_uid, v_author, p_goal_ms);

    select t.user_id into v_new_uid
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and not exists (select 1 from public.submitter_blocked b
                       where b.submitter_id = t.user_id::text
                         and (b.banned_until is null or b.banned_until > now()))
    order by t.goal_ms asc, t.updated_at asc
    limit 1;

    if v_new_uid = v_uid and (v_cc_uid is null or v_cc_uid <> v_uid) then
        insert into public.arcade_events
            (kind, scope, game, course_id, direction, car_id,
             actor_user_id, actor_name, victim_user_id, victim_name, goal_ms, previous_ms)
        values
            ('crown_taken', 'course', p_game, p_course_id, p_direction, null,
             v_uid, v_author, v_cc_uid, v_cc_name, p_goal_ms, v_cc_ms);
    end if;

    select t.user_id into v_new_uid
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and t.car_id = p_car_id
      and not exists (select 1 from public.submitter_blocked b
                       where b.submitter_id = t.user_id::text
                         and (b.banned_until is null or b.banned_until > now()))
    order by t.goal_ms asc, t.updated_at asc
    limit 1;

    if v_new_uid = v_uid and (v_car_uid is null or v_car_uid <> v_uid) then
        insert into public.arcade_events
            (kind, scope, game, course_id, direction, car_id,
             actor_user_id, actor_name, victim_user_id, victim_name, goal_ms, previous_ms)
        values
            ('crown_taken', 'car', p_game, p_course_id, p_direction, p_car_id,
             v_uid, v_author, v_car_uid, v_car_name, p_goal_ms, v_car_ms);
    end if;

    return jsonb_build_object('ok', true, 'stored', true, 'author', v_author);
end;
$function$;

revoke all on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text) from anon, public;
grant execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text) to authenticated;

-- ---- the removal lever ------------------------------------------------------
--
-- service_role only, so it is reachable from an edge function or the dashboard and from nowhere a
-- user can get to. Deliberately deletes rather than hides: a hidden row still occupies the unique
-- index on (game, course, direction, car, user) and would silently block that player from ever
-- resubmitting a clean time on that board.

create or replace function public.admin_delete_arcade_lap_time(p_id uuid)
returns jsonb
language plpgsql
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
declare v_row public.arcade_lap_times%rowtype;
begin
    select * into v_row from public.arcade_lap_times where id = p_id;
    if not found then
        return jsonb_build_object('ok', false, 'error', 'not found');
    end if;

    delete from public.arcade_lap_times where id = p_id;

    -- An unannounced event celebrating a time that no longer exists is no longer true. Only
    -- unconsumed ones: anything already posted is history and rewriting it would be a lie of a
    -- different kind.
    delete from public.arcade_events e
     where e.consumed_at is null
       and e.actor_user_id = v_row.user_id
       and e.game = v_row.game
       and e.course_id = v_row.course_id
       and e.direction = v_row.direction
       and e.goal_ms = v_row.goal_ms;

    return jsonb_build_object('ok', true,
                              'user_id', v_row.user_id, 'author', v_row.author,
                              'game', v_row.game, 'course_id', v_row.course_id,
                              'direction', v_row.direction, 'car_id', v_row.car_id,
                              'goal_ms', v_row.goal_ms);
end;
$function$;

revoke all on function public.admin_delete_arcade_lap_time(uuid) from public, anon, authenticated;
grant execute on function public.admin_delete_arcade_lap_time(uuid) to service_role;

commit;
