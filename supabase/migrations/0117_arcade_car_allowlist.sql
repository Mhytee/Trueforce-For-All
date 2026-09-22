-- 0117: bound car_id to cars that actually exist.
--
-- submit_arcade_lap_time checked that p_car_id was not null and nothing else, so any signed-in
-- client could put a record on car 999999. Such a row is invisible in game, since there is no
-- per-car board for a car that does not exist, unbeatable by anyone driving a real car, and it
-- still counts toward the overall ranking. It was the last unbounded field in the payload.
--
-- A TABLE rather than a constant inside the function. The Discord commands need a car list for
-- autocomplete, and a second hardcoded copy of 50 cars in a second edge function is exactly how
-- two lists drift apart. Generated from Id8CarTable.cs and romanised the same way the digest
-- romanises it, so a car reads identically wherever it appears.
--
-- VALIDATION LIVES IN THE FUNCTION, not in a foreign key, for the reason 0112 records: a
-- constraint violation is not the jsonb shape the client parses, so a rejected lap disappears with
-- an empty reason in the log. Here it comes back as {ok:false, error:'unknown car'}.

create table if not exists public.arcade_cars (
    game    text    not null,
    car_id  integer not null,
    code    text    not null,
    name    text    not null,
    primary key (game, car_id)
);

comment on table public.arcade_cars is
    'Cars that exist, per game. Generated from Id8CarTable.cs. Bounds submit_arcade_lap_time and feeds the Discord command autocomplete.';

alter table public.arcade_cars enable row level security;

drop policy if exists arcade_cars_readable on public.arcade_cars;
create policy arcade_cars_readable on public.arcade_cars for select using (true);

revoke all on public.arcade_cars from anon, authenticated;
grant select on public.arcade_cars to anon, authenticated;

insert into public.arcade_cars (game, car_id, code, name)
select 'ID8', v.car_id, v.code, v.name
from (values
    (0, 'AE86T', 'TRUENO GT-APEX (AE86)'),
    (1, 'AE86L', 'LEVIN GT-APEX (AE86)'),
    (2, 'AE85L', 'LEVIN SR (AE85)'),
    (3, 'SW20', 'MR2 G-Limited (SW20)'),
    (4, 'SXE10', 'ALTEZZA RS200 (SXE10)'),
    (5, 'ZZW30', 'MR-S (ZZW30)'),
    (6, 'JZA80', 'SUPRA RZ (JZA80)'),
    (7, 'ZN6', '86 GT (ZN6)'),
    (8, 'ZVW30', 'PRIUS (ZVW30)'),
    (9, 'AE86T2', 'TRUENO 2door GT-APEX (AE86)'),
    (10, 'ST205', 'CELICA GT-FOUR (ST205)'),
    (256, 'BNR32', 'SKYLINE GT-R (BNR32)'),
    (257, 'BNR34', 'SKYLINE GT-R (BNR34)'),
    (258, 'S13K', 'SILVIA K''s (S13)'),
    (259, 'S14Q', 'Silvia Q''s (S14)'),
    (260, 'S15', 'Silvia spec-R (S15)'),
    (261, 'RPS13', '180SX TYPE II (RPS13)'),
    (262, 'Z33', 'FAIRLADY Z (Z33)'),
    (263, 'R35', 'GT-R NISMO (R35)'),
    (264, 'ER34', 'SKYLINE 25GT TURBO (ER34)'),
    (512, 'EG6', 'Civic SiR·II (EG6)'),
    (513, 'EK9', 'CIVIC TYPE R (EK9)'),
    (514, 'DC2', 'INTEGRA TYPE R (DC2)'),
    (515, 'AP1', 'S2000 (AP1)'),
    (516, 'NA1', 'NSX (NA1)'),
    (768, 'FC3S', 'RX-7 Infini III (FC3S)'),
    (769, 'FD3S', 'RX-7 Type R (FD3S)'),
    (770, 'SE3P', 'RX-8 Type S (SE3P)'),
    (771, 'NA6CE', 'ROADSTER (NA6CE)'),
    (772, 'NB8C', 'ROADSTER RS (NB8C)'),
    (773, 'FD3S6', 'RX-7 Type RS (FD3S)'),
    (1024, 'GC8S5', 'IMPREZA STi Ver.V (GC8)'),
    (1025, 'GDBF', 'IMPREZA STI (GDBF)'),
    (1026, 'GDBA', 'IMPREZA STi (GDBA)'),
    (1027, 'ZC6', 'BRZ S (ZC6)'),
    (1280, 'CE9A', 'LANCER Evolution III (CE9A)'),
    (1281, 'CN9A', 'LANCER EVOLUTION IV (CN9A)'),
    (1282, 'CT9A9', 'LANCER Evolution IX (CT9A)'),
    (1283, 'CT9A7', 'LANCER EVOLUTION VII (CT9A)'),
    (1284, 'CZ4A', 'LANCER EVOLUTION X (CZ4A)'),
    (1285, 'CP9A5', 'LANCER EVOLUTION V (CP9A)'),
    (1286, 'CP9A6T', 'LANCER EVOLUTION VI (CP9A)'),
    (1536, 'EA11R', 'Cappuccino (EA11R)'),
    (1792, 'RPS13K', 'SILEIGHTY'),
    (2048, 'FD3SC', 'GENKI-7 (FD3S)'),
    (2049, 'EK9C', 'MONSTER CIVIC R (EK9)'),
    (2050, 'AP1C', 'S2000 GT1 (AP1)'),
    (2051, 'JZA80C', 'G-FORCE SUPRA (JZA80 Kai)'),
    (2052, 'NA8CC', 'ROADSTER C-SPEC (NA8C Kai)'),
    (2053, 'NA2C', 'NSX-R GT (NA2)')
) as v(car_id, code, name)
on conflict (game, car_id) do update
    set code = excluded.code, name = excluded.name;

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

    -- Bounded by arcade_cars. A game with NO rows there is accepted on purpose: when a second
    -- cabinet is added, its runs must not start failing because nobody has seeded its cars yet.
    if exists (select 1 from public.arcade_cars c where c.game = p_game)
       and not exists (select 1 from public.arcade_cars c
                        where c.game = p_game and c.car_id = p_car_id) then
        return jsonb_build_object('ok', false, 'error', 'unknown car', 'car_id', p_car_id);
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

-- create-or-replace keeps the existing ACL, unlike the drop in 0112, so these restate the intended
-- grant rather than restoring it. Supabase's default privileges hand EXECUTE to anon on newly
-- created functions, and this file would create one on a fresh database.
revoke all on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) from anon, public;
grant execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) to authenticated;
