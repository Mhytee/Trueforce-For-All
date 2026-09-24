-- Amends get_arcade_week from 0113 (the file carries this change; applied separately because the
-- rest of 0113 was already live). Adds `e.consumed_at is null` to the steals CTE: without it the
-- stamp written by mark_arcade_events_consumed was never read, so a second run inside the same week
-- would have told every steal a second time.

create or replace function public.get_arcade_week(
    p_game  text,
    p_since timestamptz default null)
returns jsonb
language sql
stable
security definer
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
    steals as (
        select e.course_id, e.direction, e.actor_name, e.victim_name, e.goal_ms, e.previous_ms, e.created_at
        from public.arcade_events e, win
        where e.game = p_game
          and e.kind = 'crown_taken' and e.scope = 'course'
          and e.victim_user_id is not null
          and e.actor_user_id is not null
          and e.consumed_at is null
          and e.created_at >= win.since
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = e.actor_user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
        order by e.created_at
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
                'course_id', s.course_id, 'direction', s.direction,
                'actor', s.actor_name, 'victim', s.victim_name,
                'goal_ms', s.goal_ms, 'previous_ms', s.previous_ms) order by s.created_at)
            from steals s), '[]'::jsonb),
        'top', coalesce((select jsonb_agg(jsonb_build_object(
                'rank', r.rank, 'author', r.author, 'points', r.points,
                'course_crowns', r.course_crowns) order by r.rank)
            from public.get_arcade_overall_ranking(p_game, 5) r), '[]'::jsonb)
    );
$function$;

revoke all on function public.get_arcade_week(text, timestamptz) from public, anon, authenticated;
grant execute on function public.get_arcade_week(text, timestamptz) to service_role;