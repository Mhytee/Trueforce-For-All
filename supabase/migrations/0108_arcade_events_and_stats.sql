-- 0108: the reads and the event log the arcade leaderboards need to become social.
--
-- Three things, all resting on 0106's arcade_lap_times:
--
--   1. The two read gaps left open in 0106: "which cars have times on this course" and
--      "where do I rank", both of which a UI needs and neither of which was expressible.
--   2. An EVENT LOG so a Discord bot can announce a new time or a stolen crown.
--   3. Player stats and an overall ranking, so somebody's standing across every board is a
--      thing that can be asked for rather than assembled client-side.
--
-- Why an event TABLE rather than realtime: a bot that is restarting, rate limited, or simply
-- offline must not lose announcements. Rows persist until the bot marks them consumed, so it can
-- catch up, replay, or be rewritten without the history being gone. Realtime is a delivery
-- optimisation that can be layered on top of this; it is not a substitute for it.
--
-- A NOTE ON WHAT COUNTS AS A BOARD. The game has two shapes, and both matter here:
--   * a COURSE board, keyed (game, course, direction), showing the best per player whatever
--     they drove. 32 of them. This is the prestigious one.
--   * a CAR board, keyed (game, course, direction, car). Up to 1600. Broader, easier to top.
-- Stats report the two separately rather than blending them, because "I hold Akina downhill" and
-- "I hold Akina downhill in an AE86" are different claims and a single number hides which.

-- ---- event log ----------------------------------------------------------

create table if not exists public.arcade_events (
    id            bigint generated always as identity primary key,

    -- 'time_set'    someone stored a new personal best on a board
    -- 'crown_taken' someone took first place, displacing a DIFFERENT player
    kind          text        not null check (kind in ('time_set', 'crown_taken')),

    -- 'course' for the any-car board, 'car' for a single car's board. A crown_taken on a course
    -- board is the interesting one; car crowns are frequent and a bot will usually filter them out.
    scope         text        not null check (scope in ('course', 'car')),

    game          text        not null,
    course_id     smallint    not null,
    direction     smallint    not null,
    car_id        integer,

    actor_user_id uuid        references auth.users(id) on delete set null,
    actor_name    text        not null,

    -- Only set on crown_taken, and only when the previous holder was somebody else. Beating your
    -- own record is not an event anybody wants announced.
    victim_user_id uuid       references auth.users(id) on delete set null,
    victim_name    text,

    goal_ms        integer    not null,
    previous_ms    integer,

    created_at     timestamptz not null default now(),

    -- The bot stamps this when it has announced the row. Null means outstanding.
    consumed_at    timestamptz
);

-- The bot's only query: outstanding rows, oldest first.
create index if not exists arcade_events_outstanding
    on public.arcade_events (created_at) where consumed_at is null;

create index if not exists arcade_events_actor
    on public.arcade_events (actor_user_id, created_at desc);

comment on table public.arcade_events is
    'Append-only feed of arcade leaderboard activity for the Discord bot. Rows persist until consumed so an offline bot can catch up.';

alter table public.arcade_events enable row level security;

-- Written only by the security-definer submit RPC; consumed only by the bot on the service role.
-- Signed-in users may read it so a UI can show a recent-activity feed.
revoke all on public.arcade_events from anon, authenticated;
grant select on public.arcade_events to authenticated;

drop policy if exists arcade_events_select_auth on public.arcade_events;
create policy arcade_events_select_auth on public.arcade_events
    for select to authenticated using (true);

-- ---- submit, now emitting events ----------------------------------------
--
-- Replaces 0106's version. Same signature and same validation; the addition is that after a row
-- is genuinely stored it works out whether a crown changed hands and logs it.

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
    -- Crown holders BEFORE this submission, so a change can be detected after it.
    v_cc_uid    uuid;   v_cc_name text; v_cc_ms integer;   -- course board
    v_car_uid   uuid;   v_car_name text; v_car_ms integer; -- car board
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

    -- Who held each crown before this. Captured first because the upsert may take it.
    select t.user_id, t.author, t.goal_ms into v_cc_uid, v_cc_name, v_cc_ms
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
    order by t.goal_ms asc, t.updated_at asc
    limit 1;

    select t.user_id, t.author, t.goal_ms into v_car_uid, v_car_name, v_car_ms
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and t.car_id = p_car_id
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

    -- ROW_COUNT is 0 when the ON CONFLICT ... WHERE guard rejected the update, which is the
    -- "somebody else's faster row is already there, or this is not an improvement" case.
    get diagnostics v_rows = row_count;
    if v_rows = 0 then
        return jsonb_build_object('ok', true, 'stored', false, 'reason', 'not a personal best');
    end if;

    insert into public.arcade_events
        (kind, scope, game, course_id, direction, car_id, actor_user_id, actor_name, goal_ms)
    values
        ('time_set', 'car', p_game, p_course_id, p_direction, p_car_id, v_uid, v_author, p_goal_ms);

    -- Course crown. Only an event when it actually changed hands to a different person: taking
    -- your own record back off yourself is not news.
    select t.user_id into v_new_uid
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
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

    -- Car crown, same rule.
    select t.user_id into v_new_uid
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
      and t.car_id = p_car_id
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

-- ---- gap 1: which cars have times on this course -------------------------

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
    select t.car_id,
           count(distinct t.user_id)::integer,
           min(t.goal_ms)::integer,
           (select t2.author
            from public.arcade_lap_times t2
            where t2.game = p_game and t2.course_id = p_course_id
              and t2.direction = p_direction and t2.car_id = t.car_id
            order by t2.goal_ms asc, t2.updated_at asc
            limit 1)
    from public.arcade_lap_times t
    where t.game = p_game and t.course_id = p_course_id and t.direction = p_direction
    group by t.car_id
    order by min(t.goal_ms) asc;
$function$;

revoke all on function public.get_arcade_course_cars(text, smallint, smallint) from anon, public;
grant execute on function public.get_arcade_course_cars(text, smallint, smallint) to authenticated;

-- ---- gap 2: where do I rank ---------------------------------------------
--
-- p_car_id null asks about the course board, a car id about that car's board. Returns one row
-- always, so a caller does not have to distinguish "no answer" from "not on this board".

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

-- ---- player stats --------------------------------------------------------
--
-- Components are reported separately and a points total is derived from them. The weights below
-- are a starting point, not a law: they are here so a bot has one number to sort by, and every
-- input is also returned so a UI can present its own view without another query.
--
--   course crown  25   there are only 32, so holding one is a real claim
--   car crown      5   up to 1600 of them, so worth much less each
--   course top ten 11 - rank   second place scores 9, tenth scores 1

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
    course_best as (
        select distinct on (t.course_id, t.direction, t.user_id)
               t.course_id, t.direction, t.user_id, t.author, t.goal_ms, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
        order by t.course_id, t.direction, t.user_id, t.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (
                   partition by cb.course_id, cb.direction
                   order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    car_ranked as (
        select t.course_id, t.direction, t.car_id, t.user_id,
               (row_number() over (
                   partition by t.course_id, t.direction, t.car_id
                   order by t.goal_ms asc, t.updated_at asc))::integer as r
        from public.arcade_lap_times t
        where t.game = p_game
    ),
    fav as (
        select t.car_id, count(*)::integer as runs
        from public.arcade_lap_times t, who
        where t.game = p_game and t.user_id = who.uid
        group by t.car_id
        order by count(*) desc, min(t.goal_ms) asc
        limit 1
    )
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           (select count(*)::integer from course_ranked c where c.user_id = who.uid and c.r = 1),
           (select count(*)::integer from car_ranked  c where c.user_id = who.uid and c.r = 1),
           (select count(*)::integer from course_ranked c where c.user_id = who.uid and c.r <= 10),
           (select count(distinct (t.course_id, t.direction))::integer
              from public.arcade_lap_times t where t.game = p_game and t.user_id = who.uid),
           (select count(distinct t.car_id)::integer
              from public.arcade_lap_times t where t.game = p_game and t.user_id = who.uid),
           (select car_id from fav),
           (select runs from fav),
           coalesce((select min(c.r) from course_ranked c where c.user_id = who.uid), 0),
           coalesce((select count(*) from course_ranked c where c.user_id = who.uid and c.r = 1), 0)::integer * 25
         + coalesce((select count(*) from car_ranked  c where c.user_id = who.uid and c.r = 1), 0)::integer * 5
         + coalesce((select sum(11 - c.r) from course_ranked c where c.user_id = who.uid and c.r <= 10), 0)::integer
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;

-- ---- overall ranking -----------------------------------------------------
--
-- Ordered by the same points, with the components exposed so a tie can be explained rather than
-- broken arbitrarily.

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
    with course_best as (
        select distinct on (t.course_id, t.direction, t.user_id)
               t.course_id, t.direction, t.user_id, t.author, t.goal_ms, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
        order by t.course_id, t.direction, t.user_id, t.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (
                   partition by cb.course_id, cb.direction
                   order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    car_ranked as (
        select t.user_id,
               (row_number() over (
                   partition by t.course_id, t.direction, t.car_id
                   order by t.goal_ms asc, t.updated_at asc))::integer as r
        from public.arcade_lap_times t
        where t.game = p_game
    ),
    agg as (
        select u.user_id,
               (select p.username from public.profiles p where p.id = u.user_id) as author,
               coalesce((select count(*) from course_ranked c where c.user_id = u.user_id and c.r = 1), 0)::integer as cc,
               coalesce((select count(*) from car_ranked   c where c.user_id = u.user_id and c.r = 1), 0)::integer as carc,
               coalesce((select count(*) from course_ranked c where c.user_id = u.user_id and c.r <= 10), 0)::integer as t10,
               coalesce((select count(distinct (t.course_id, t.direction))
                           from public.arcade_lap_times t
                          where t.game = p_game and t.user_id = u.user_id), 0)::integer as be,
               coalesce((select sum(11 - c.r) from course_ranked c where c.user_id = u.user_id and c.r <= 10), 0)::integer as tp
        from (select distinct t.user_id
                from public.arcade_lap_times t
               where t.game = p_game
                 and not exists (select 1 from public.submitter_blocked b
                                  where b.submitter_id = t.user_id::text
                                    and (b.banned_until is null or b.banned_until > now()))) u
    )
    select (row_number() over (order by (a.cc * 25 + a.carc * 5 + a.tp) desc, a.cc desc, a.be desc))::integer,
           a.user_id, a.author, a.cc, a.carc, a.t10, a.be,
           (a.cc * 25 + a.carc * 5 + a.tp)::integer
    from agg a
    order by (a.cc * 25 + a.carc * 5 + a.tp) desc, a.cc desc, a.be desc
    limit greatest(1, least(coalesce(p_limit, 25), 200));
$function$;

revoke all on function public.get_arcade_overall_ranking(text, integer) from anon, public;
grant execute on function public.get_arcade_overall_ranking(text, integer) to authenticated;
