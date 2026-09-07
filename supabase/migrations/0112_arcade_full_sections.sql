-- 0112: store every section time, not the first three.
--
-- The live Momiji row records goal_ms 208021 against sections 48503 + 51513 + 59202 = 159218. A
-- 48803 ms fourth split was thrown away, because the client copied three sections while the time
-- summed all of them. Courses have different section counts (the two rows in the table are a
-- four-section course and a two-section one), so three fixed columns cannot hold them.
--
-- WHY DROP AND RECREATE rather than create-or-replace: adding a parameter makes a NEW signature, so
-- create-or-replace would leave TWO candidates and PostgREST would answer a ten-key body with
-- PGRST203 "could not choose the best candidate function". Dropping first leaves exactly one, and
-- the deployed 0.3.1 client's ten-key body still resolves against it because the new parameter has
-- a default. The grants are re-applied below because a drop takes the ACL with it.
--
-- VALIDATION DISCIPLINE, learned from the review of 0111:
--   * never a CHECK constraint a client payload can trip. The client cannot parse a constraint
--     violation (it is not the jsonb shape it expects) and logs an empty reason, so a rejected lap
--     vanishes with nothing to debug. Validate in the function and return {ok:false,error}.
--   * an element bound of `< p_goal_ms` would reject a ONE-section course 100% of the time, since
--     that single element necessarily EQUALS the goal. It is `<=`.
--   * a null array is accepted, because every deployed client sends one. This must never become a
--     reason to reject an honest run from an older build.

alter table public.arcade_lap_times
    add column if not exists sections_ms integer[];

comment on column public.arcade_lap_times.sections_ms is
    'Every section time in order. section1_ms..section3_ms are the first three, kept for readers that predate this column.';

drop function if exists public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer, smallint, text);

create function public.submit_arcade_lap_time(
    p_game           text,
    p_course_id      smallint,
    p_direction      smallint,
    p_car_id         integer,
    p_goal_ms        integer,
    p_section1_ms    integer default null,
    p_section2_ms    integer default null,
    p_section3_ms    integer default null,
    p_tuning_level   smallint default null,
    p_plugin_version text default null,
    p_sections_ms    integer[] default null)
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
    v_sum       integer;
    v_n         integer;
    v_s         integer[];
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

    -- Sections, when supplied. A client that computes its time as the sum of these passes by
    -- construction, so this is a client-correctness assertion rather than an integrity measure: a
    -- faker authors both sides of it. It earns its place by catching the truncation bug that put a
    -- short, self-consistent, unbeatable time on a board.
    v_s := p_sections_ms;
    if v_s is not null then
        v_n := coalesce(array_length(v_s, 1), 0);
        if v_n < 1 or v_n > 16 then
            return jsonb_build_object('ok', false, 'error', 'bad section count');
        end if;
        if exists (select 1 from unnest(v_s) x where x is null or x <= 0 or x > p_goal_ms) then
            -- `> p_goal_ms`, not `>=`: on a one-section course the only element IS the goal.
            return jsonb_build_object('ok', false, 'error', 'bad section time');
        end if;
        select sum(x) into v_sum from unnest(v_s) x;
        if v_sum <> p_goal_ms then
            return jsonb_build_object('ok', false, 'error', 'sections do not sum to the time',
                                      'sections_sum', v_sum, 'goal_ms', p_goal_ms);
        end if;
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

    -- A banned holder must be invisible here, or nobody who beats them is ever seen to take the
    -- crown and the board announces nothing again for good.
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
         section1_ms, section2_ms, section3_ms, sections_ms, user_id, author, plugin_version)
    values
        (p_game, p_course_id, p_direction, p_car_id, p_tuning_level, p_goal_ms,
         coalesce(p_section1_ms, v_s[1]), coalesce(p_section2_ms, v_s[2]),
         coalesce(p_section3_ms, v_s[3]), v_s, v_uid, v_author, p_plugin_version)
    on conflict (game, course_id, direction, car_id, user_id) do update
        set goal_ms        = excluded.goal_ms,
            section1_ms    = excluded.section1_ms,
            section2_ms    = excluded.section2_ms,
            section3_ms    = excluded.section3_ms,
            sections_ms    = excluded.sections_ms,
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

-- The drop took the ACL with it, so both lines are required, not belt and braces. Supabase's
-- default privileges hand EXECUTE to anon on any newly created function, and a revoke from PUBLIC
-- does not remove a named-role grant.
revoke all on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) from anon, public;
grant execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) to authenticated;
