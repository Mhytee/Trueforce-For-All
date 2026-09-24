-- The weekly summary gets what a weekly summary is actually for: what changed.
--
-- Until now it could only report records changing hands, which on a small server is most weeks
-- silent, and a standing, which barely moves. Meanwhile the thing people genuinely do every week,
-- taking seconds off their own time and climbing a few places, produced nothing to say.
--
-- Two sources, both new this session. previous_ms on a time_set event gives the seconds gained.
-- arcade_standing_history gives the places moved. Neither can be computed from the present, which
-- is why both had to start being recorded before the digest could use them.
create or replace function public.get_arcade_week(p_game text, p_since timestamptz default null)
returns jsonb
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with win as (select coalesce(p_since, now() - interval '7 days') as since),
    live as (
        select t.*
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    merged as (select * from public.get_arcade_scores_merged(p_game)),
    steals as (
        select e.scope, e.car_id, e.course_id, e.direction, e.actor_name, e.victim_name,
               e.goal_ms, e.previous_ms, e.created_at
        from public.arcade_events e, win
        where e.game = p_game
          and e.kind = 'crown_taken' and e.scope in ('course', 'car')
          and e.victim_user_id is not null
          and e.actor_user_id is not null
          and e.consumed_at is null
          and e.created_at >= win.since
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = e.actor_user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
        order by e.created_at
    ),
    -- Seconds taken off a time somebody already held. Only genuine improvements: a first time on a
    -- board has no previous_ms and is a different story, told by the unclaimed section.
    gains as (
        select e.actor_name, e.course_id, e.direction, e.car_id, e.goal_ms, e.previous_ms,
               (e.previous_ms - e.goal_ms) as gain
        from public.arcade_events e, win
        where e.game = p_game and e.kind = 'time_set'
          and e.previous_ms is not null and e.previous_ms > e.goal_ms
          and e.created_at >= win.since
        order by (e.previous_ms - e.goal_ms) desc
        limit 5
    ),
    -- Places moved in the merged field, comparing the earliest snapshot in the window with now.
    -- Null when there is no snapshot old enough, which is every week until this has run twice.
    moves as (
        select pr.username as author,
               (select m.rank from merged m where m.who = 'u:' || h.user_id::text) as rank_now,
               h.merged_rank as rank_then,
               (select count(*)::integer from merged) as of_now
        from (
            select distinct on (user_id) user_id, merged_rank
            from public.arcade_standing_history hh, win
            where hh.game = p_game and hh.taken_on >= (win.since)::date
            order by user_id, taken_on asc
        ) h
        join public.profiles pr on pr.id = h.user_id
    ),
    fresh as (
        select count(*)::integer as n, count(distinct t.user_id)::integer as drivers
        from live t, win where t.updated_at >= win.since
    )
    select jsonb_build_object(
        'game', p_game,
        'since', (select since from win),
        'times_set', (select n from fresh),
        'drivers', (select drivers from fresh),
        'boards_with_times', (select count(distinct (course_id, direction))::integer from live),
        'steals', coalesce((select jsonb_agg(jsonb_build_object(
                'scope', s.scope, 'car_id', s.car_id,
                'course_id', s.course_id, 'direction', s.direction,
                'actor', s.actor_name, 'victim', s.victim_name,
                'goal_ms', s.goal_ms, 'previous_ms', s.previous_ms)
            order by (s.scope = 'course') desc, s.created_at)
            from steals s), '[]'::jsonb),
        'gains', coalesce((select jsonb_agg(jsonb_build_object(
                'actor', g.actor_name, 'course_id', g.course_id, 'direction', g.direction,
                'car_id', g.car_id, 'goal_ms', g.goal_ms, 'previous_ms', g.previous_ms,
                'gain_ms', g.gain) order by g.gain desc)
            from gains g), '[]'::jsonb),
        'moves', coalesce((select jsonb_agg(jsonb_build_object(
                'author', mv.author, 'rank_now', mv.rank_now,
                'rank_then', mv.rank_then, 'of', mv.of_now)
            order by (mv.rank_then - mv.rank_now) desc nulls last)
            from moves mv where mv.rank_now is not null and mv.rank_then is not null
                            and mv.rank_then <> mv.rank_now), '[]'::jsonb),
        'top', coalesce((select jsonb_agg(jsonb_build_object(
                'rank', r.rank, 'author', r.author, 'points', r.points,
                'course_crowns', r.course_crowns,
                'merged_rank', (select m.rank from merged m where m.who = 'u:' || r.user_id::text),
                'merged_of', (select count(*)::integer from merged))
                order by r.rank)
            from public.get_arcade_overall_ranking(p_game, 5) r), '[]'::jsonb)
    );
$function$;

revoke all on function public.get_arcade_week(text, timestamptz) from anon, public;
grant execute on function public.get_arcade_week(text, timestamptz) to authenticated;