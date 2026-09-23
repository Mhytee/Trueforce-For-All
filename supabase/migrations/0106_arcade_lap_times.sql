-- Arcade lap times, so the community can fill Initial D 8's dead leaderboards.
--
-- Why this exists: ID8's in-game boards were served by SEGA's ALL.Net and a
-- per-shop server tower, both long dead. The boards themselves are still live
-- arrays in the game's memory, and every row a player sees today is SEGA's
-- shipped filler (name SEGA, time six minutes flat). The plugin can write those
-- arrays, so the only missing piece is somewhere to keep real times.
--
-- SIGNED IN ONLY, deliberately, and unlike car facts (0100). A leaderboard is
-- an identity feature: the entire point is a name next to a time, and a name
-- only means anything if it is tied to an account. So:
--   * user_id references auth.users directly rather than the text
--     'anon:<id>' submitter_id car facts use. Deletion coverage (0097) then
--     comes from the cascade for free.
--   * the display name is read SERVER-SIDE from profiles.username. A client
--     cannot claim to be somebody else, and the profanity filter (0090) and
--     uniqueness (0007) that govern usernames govern board names too.
--   * author is a snapshot, matching the denormalized author column pattern
--     0097 established, so a rename does not rewrite history.
--
-- One row per (game, course, direction, car, user), holding that user's BEST.
-- A leaderboard showing one person ten times is not a leaderboard, and
-- upserting the best makes that impossible by construction rather than by
-- query discipline.

-- ---- table --------------------------------------------------------------

create table if not exists public.arcade_lap_times (
    id              uuid primary key default gen_random_uuid(),

    -- Game identity as the plugin reports it, e.g. 'ID8'. Text rather than an
    -- enum so a second cabinet needs no migration.
    game            text        not null,

    -- The game's own board key. ID8 indexes every board by
    -- (direction + 2 * course), 16 courses by 2 directions.
    course_id       smallint    not null,
    direction       smallint    not null,

    -- ID8 CarID. Kept per-car even for the any-car boards, because the any-car
    -- board is derivable from per-car rows but not the other way round.
    car_id          integer     not null,

    -- Recorded but not yet used to segregate boards. The game has no per-tune
    -- ranking, so a fully tuned car will out-run a stock one on the same board.
    -- Storing it now costs nothing and is unrecoverable later.
    tuning_level    smallint,

    goal_ms         integer     not null,
    section1_ms     integer,
    section2_ms     integer,
    section3_ms     integer,

    user_id         uuid        not null references auth.users(id) on delete cascade,

    -- Snapshot of profiles.username at submission time. Written server-side.
    author          text        not null,

    plugin_version  text,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),

    constraint arcade_lap_times_course_range    check (course_id between 0 and 15),
    constraint arcade_lap_times_direction_range check (direction between 0 and 1),
    -- Upper bound is SEGA's filler time. Anything at or beyond six minutes is
    -- either a filler row that leaked in or a run that timed out, and neither
    -- belongs on a board.
    constraint arcade_lap_times_goal_sane       check (goal_ms > 0 and goal_ms < 360000)
);

-- One best per person per board.
create unique index if not exists arcade_lap_times_unique_user
    on public.arcade_lap_times (game, course_id, direction, car_id, user_id);

-- The leaderboard read: filter to a board, order by time.
create index if not exists arcade_lap_times_board
    on public.arcade_lap_times (game, course_id, direction, goal_ms);

-- Rate limiting and "my times" both look up by user over a time window.
create index if not exists arcade_lap_times_user_recent
    on public.arcade_lap_times (user_id, updated_at desc);

comment on table public.arcade_lap_times is
    'Community lap times used to fill the in-game arcade leaderboards. Signed-in only. One best row per user per board.';

-- ---- RLS ----------------------------------------------------------------
--
-- Readable by any signed-in user, matching the community auth gate in 0027.
-- Writes go exclusively through the security-definer RPC below, so no insert,
-- update or delete policy is granted to anyone.

alter table public.arcade_lap_times enable row level security;

revoke all on public.arcade_lap_times from anon;
grant select on public.arcade_lap_times to authenticated;

drop policy if exists arcade_lap_times_select_all on public.arcade_lap_times;
drop policy if exists arcade_lap_times_select_auth on public.arcade_lap_times;
create policy arcade_lap_times_select_auth on public.arcade_lap_times
    for select to authenticated using (true);

-- Drop the anon-capable signature from any earlier draft of this migration so
-- PostgREST cannot resolve a call to it.
drop function if exists public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, text, text);

-- ---- submit -------------------------------------------------------------

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
    v_uid      uuid := auth.uid();
    v_author   text;
    v_recent   integer;
    v_existing integer;
begin
    if v_uid is null then
        return jsonb_build_object('ok', false, 'error', 'sign-in required');
    end if;

    -- The name on the board is the account's, never the client's claim.
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

    -- banned_until is null for a permanent ban and a timestamp for a temporary one
    -- (0081). Checking only for the row's existence would make every temporary ban
    -- permanent, so match the predicate 0081 itself uses.
    if exists (
        select 1 from public.submitter_blocked b
        where b.submitter_id = v_uid::text
          and (b.banned_until is null or b.banned_until > now())) then
        -- Deliberately reports success. Telling a blocked submitter they are
        -- blocked only tells them to try something else.
        return jsonb_build_object('ok', true, 'stored', false);
    end if;

    -- 60 writes an hour is far above honest play (a run takes minutes) and far
    -- below what makes flooding worthwhile.
    select count(*) into v_recent
    from public.arcade_lap_times t
    where t.user_id = v_uid
      and t.updated_at > now() - interval '1 hour';

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
            -- Refresh the snapshot on a genuine improvement, so a renamed user
            -- appears under their current name once they beat their own time.
            author         = excluded.author,
            plugin_version = excluded.plugin_version,
            updated_at     = now()
        where excluded.goal_ms < t.goal_ms;

    return jsonb_build_object('ok', true, 'stored', true, 'author', v_author);
end;
$function$;

revoke all on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text) from public;

grant execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text) to authenticated;

-- ---- read ---------------------------------------------------------------
--
-- p_car_id null gives the any-car board, which is each user's best on that
-- course whatever they drove. A car id gives that car's board. Both dedupe by
-- user, so nobody occupies two rows.

drop function if exists public.get_arcade_leaderboard(text, smallint, smallint, integer, integer);

create or replace function public.get_arcade_leaderboard(
    p_game      text,
    p_course_id smallint,
    p_direction smallint,
    p_car_id    integer default null,
    p_limit     integer default 10)
returns table (
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
        select distinct on (t.user_id)
               t.user_id, t.author, t.goal_ms,
               t.section1_ms, t.section2_ms, t.section3_ms, t.car_id, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
          and t.course_id = p_course_id
          and t.direction = p_direction
          and (p_car_id is null or t.car_id = p_car_id)
          and not exists (
              select 1 from public.submitter_blocked b
              where b.submitter_id = t.user_id::text
                and (b.banned_until is null or b.banned_until > now()))
        order by t.user_id, t.goal_ms asc
    )
    select (row_number() over (order by b.goal_ms asc, b.updated_at asc))::integer,
           b.author, b.goal_ms, b.section1_ms, b.section2_ms, b.section3_ms,
           b.car_id, b.updated_at
    from best b
    order by b.goal_ms asc, b.updated_at asc
    limit greatest(1, least(coalesce(p_limit, 10), 100));
$function$;

revoke all on function public.get_arcade_leaderboard(text, smallint, smallint, integer, integer) from public;
grant execute on function public.get_arcade_leaderboard(text, smallint, smallint, integer, integer) to authenticated;
