-- 0126: was anybody else there, and how do you look against the world?
--
-- Two readout problems, both of which made /id8 me say something it did not mean.
--
-- A course record on a board only you have driven is not a win, it is a first time. The scoring
-- already said so, because beating nobody is worth nothing, but the readout did not: it reported
-- "18 course records" directly above "18 pts" and left a reader to reconcile a boast with a score
-- that priced those records at zero. The count now comes back split, so an unchallenged one can be
-- read as an invitation rather than a hollow trophy.
--
-- And the stats were tf4all-only with no counterpart, so "18 first times" had nothing to be
-- measured against. On a one-driver server those numbers flatter in a way the worldwide ones do
-- not: eighteen unchallenged firsts here, best finish seventeenth out there.
--
-- get_arcade_board_ranks_merged exists so that per-board merged ranking lives in ONE place.
-- get_arcade_scores_merged already computed it and threw it away, keeping only the total, and the
-- player readout needs the same numbers. A second copy of a ranking is exactly the drift 0120 was
-- written to remove.
--
-- The applied ledger holds two entries for this change, 0126 and 0127, because the worldwide half
-- followed the contested half after the owner asked what the tf4all numbers were being compared
-- with. This file is the settled form and replaying it alone converges.

create or replace function public.get_arcade_board_ranks_merged(p_game text)
returns table (who text, author text, is_tf4all boolean,
               course_id smallint, direction smallint,
               goal_ms integer, players integer, rank integer)
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
    )
    select cb.who, cb.author, cb.is_tf4all, cb.course_id, cb.direction, cb.goal_ms, f.players,
           (row_number() over (partition by cb.course_id, cb.direction
                               order by cb.goal_ms asc, cb.updated_at asc))::integer
    from course_best cb
    join field f on f.course_id = cb.course_id and f.direction = cb.direction;
$function$;

revoke all on function public.get_arcade_board_ranks_merged(text) from anon, public;
grant execute on function public.get_arcade_board_ranks_merged(text) to authenticated;

create or replace function public.get_arcade_scores_merged(p_game text)
returns table (who text, author text, is_tf4all boolean, points integer, rank integer)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with r as (select * from public.get_arcade_board_ranks_merged(p_game)),
    scored_rows as (
        select w.who, w.author, w.is_tf4all,
               round(100.0 * (w.players - w.rank) / greatest(w.players - 1, 1)
                     * log(2.0, greatest(w.players, 1)::numeric)
                     * case when w.rank = 1 then 1.5 else 1 end)::integer as pts
        from r w
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
returns table (user_id uuid, author text, course_crowns integer, course_crowns_contested integer,
               car_crowns integer, course_top_ten integer, boards_entered integer, cars_driven integer,
               favourite_car_id integer, favourite_car_runs integer, best_course_finish integer,
               overall_rank integer, players_ranked integer,
               merged_rank integer, merged_players integer,
               merged_crowns integer, merged_top_ten integer, merged_best_finish integer,
               points integer)
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
    course_field as (
        select course_id, direction, count(*)::integer as players
        from course_best group by 1, 2
    ),
    course_ranked as (
        select cb.*, f.players,
               (row_number() over (partition by cb.course_id, cb.direction
                                   order by cb.goal_ms asc, cb.updated_at asc))::integer as r
        from course_best cb
        join course_field f on f.course_id = cb.course_id and f.direction = cb.direction
    ),
    fav as (
        select l.car_id, count(*)::integer as runs
        from live l, who where l.user_id = who.uid
        group by l.car_id order by count(*) desc, min(l.goal_ms) asc limit 1
    ),
    scores as (select * from public.get_arcade_scores(p_game)),
    merged as (select * from public.get_arcade_scores_merged(p_game)),
    mine_merged as (
        select b.* from public.get_arcade_board_ranks_merged(p_game) b, who
        where b.who = 'u:' || who.uid::text
    )
    select who.uid,
           (select p.username from public.profiles p where p.id = who.uid),
           coalesce((select s.course_crowns  from scores s where s.user_id = who.uid), 0),
           (select count(*)::integer from course_ranked c
             where c.user_id = who.uid and c.r = 1 and c.players >= 2),
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
           (select count(*)::integer from mine_merged where rank = 1),
           (select count(*)::integer from mine_merged where rank <= 10),
           coalesce((select min(rank) from mine_merged), 0),
           coalesce((select s.points from scores s where s.user_id = who.uid), 0)
    from who;
$function$;

revoke all on function public.get_arcade_player_stats(text, uuid) from anon, public;
grant execute on function public.get_arcade_player_stats(text, uuid) to authenticated;
