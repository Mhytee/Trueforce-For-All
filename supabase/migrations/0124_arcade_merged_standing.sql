-- 0124: where you stand in the whole field, TeknoParrot included.
--
-- COURSE BOARDS ONLY, deliberately. Their car names are free text and ours are CarIDs, and the
-- matcher that bridges them lives in the plugin; scoring car boards across the two sources would
-- need that ported, which is the one piece most likely to drift. A standing against the real
-- cabinets is a question about course times, so this answers exactly that and no more.
--
-- Identity is a text key because the two sides have different ones: 'u:<uuid>' for a tf4all
-- account, 'x:<name>' for a scraped entry. Nobody on the external side has an account and this
-- never pretends they do.
--
-- get_arcade_player_stats gains merged_rank and merged_players alongside the tf4all pair, so
-- /id8 me can say "65 of 204 overall, 1 of 1 among Trueforce For All". On a young server the
-- second number says nothing and the first is the one worth chasing.


create or replace function public.get_arcade_scores_merged(p_game text)
returns table (who text, author text, is_tf4all boolean, points integer, rank integer)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with ours as (
        select 'u:' || t.user_id::text as who, t.author, true as is_tf4all,
               t.course_id, t.direction, t.goal_ms, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    theirs as (
        select 'x:' || e.player_name as who, e.player_name as author, false as is_tf4all,
               e.course_id, e.direction, e.goal_ms, coalesce(e.set_at, e.fetched_at) as updated_at
        from public.arcade_external_times e
        where e.game = p_game
    ),
    live as (select * from ours union all select * from theirs),
    course_best as (
        select distinct on (l.course_id, l.direction, l.who)
               l.course_id, l.direction, l.who, l.author, l.is_tf4all, l.goal_ms, l.updated_at
        from live l order by l.course_id, l.direction, l.who, l.goal_ms asc
    ),
    field as (
        select course_id, direction, count(*)::integer as players
        from course_best group by 1, 2
    ),
    ranked as (
        select cb.*, f.players,
               (row_number() over (partition by cb.course_id, cb.direction
                                   order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
        join field f on f.course_id = cb.course_id and f.direction = cb.direction
    ),
    -- The same shape the tf4all standing uses: the share of the field you beat, weighted by how
    -- big it was, and 1.5x for a win.
    scored_rows as (
        select w.who, w.author, w.is_tf4all,
               round(100.0 * (w.players - w.r) / greatest(w.players - 1, 1)
                     * log(2.0, greatest(w.players, 1)::numeric)
                     * case when w.r = 1 then 1.5 else 1 end)::integer as pts
        from ranked w
    ),
    agg as (
        select s.who, min(s.author) as author, bool_or(s.is_tf4all) as is_tf4all,
               (sum(s.pts) + count(*))::integer as pts     -- one point per board entered
        from scored_rows s group by s.who
    )
    select a.who, a.author, a.is_tf4all, a.pts,
           (row_number() over (order by a.pts desc, a.author))::integer
    from agg a;
$function$;

revoke all on function public.get_arcade_scores_merged(text) from anon, public;
grant execute on function public.get_arcade_scores_merged(text) to authenticated;

drop function if exists public.get_arcade_player_stats(text, uuid);

create or replace function public.get_arcade_player_stats(p_game text, p_user_id uuid default null)
returns table (user_id uuid, author text, course_crowns integer, car_crowns integer,
               course_top_ten integer, boards_entered integer, cars_driven integer,
               favourite_car_id integer, favourite_car_runs integer, best_course_finish integer,
               overall_rank integer, players_ranked integer,
               merged_rank integer, merged_players integer, points integer)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with who as (select coalesce(p_user_id, auth.uid()) as uid),
    live as (
        select t.* from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    course_best as (
        select distinct on (l.course_id, l.direction, l.user_id)
               l.course_id, l.direction, l.user_id, l.goal_ms, l.updated_at
        from live l order by l.course_id, l.direction, l.user_id, l.goal_ms asc
    ),
    course_ranked as (
        select cb.*, (row_number() over (partition by cb.course_id, cb.direction
                                         order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
    ),
    fav as (
        select l.car_id, count(*)::integer as runs
        from live l, who where l.user_id = who.uid
        group by l.car_id order by count(*) desc, min(l.goal_ms) asc limit 1
    ),
    scores as (select * from public.get_arcade_scores(p_game)),
    merged as (select * from public.get_arcade_scores_merged(p_game))
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           coalesce((select s.course_crowns  from scores s where s.user_id = who.uid), 0),
           coalesce((select s.car_crowns     from scores s where s.user_id = who.uid), 0),
           coalesce((select s.course_top_ten from scores s where s.user_id = who.uid), 0),
           coalesce((select s.boards_entered from scores s where s.user_id = who.uid), 0),
           (select count(distinct l.car_id)::integer from live l where l.user_id = who.uid),
           (select car_id from fav),
           (select runs from fav),
           coalesce((select min(c.r) from course_ranked c where c.user_id = who.uid), 0),
           (select s.rank from scores s where s.user_id = who.uid),
           (select count(*)::integer from scores),
           (select m.rank from merged m where m.who = 'u:' || who.uid::text),
           (select count(*)::integer from merged),
           coalesce((select s.points from scores s where s.user_id = who.uid), 0)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;
