-- 0113: make arcade names disappear with the account, and give the weekly digest its data.
--
-- Both ship together on purpose. The digest is the moment arcade usernames stop being an internal
-- detail and start being posted to a public Discord channel, so the deletion path has to be right
-- BEFORE the first post, not after somebody asks for their name back.

-- ---- 1. names go with the account -------------------------------------------
--
-- arcade_lap_times.user_id is ON DELETE CASCADE, so a deleted account's times vanish with it.
-- arcade_events is ON DELETE SET NULL, so the row survives (correctly: "Alice took Akina from Bob"
-- is still Alice's achievement) but actor_name and victim_name were plain text snapshots that
-- outlived the account. That is exactly the defect 0097 was written to close for presets, packs and
-- custom engines, and the arcade tables shipped after it without inheriting the rule.
--
-- A TRIGGER rather than more lines in delete_my_account, for two reasons. It covers EVERY deletion
-- path, including an admin removing a user from the dashboard and the cascade from any future
-- tooling, where delete_my_account only covers the self-serve one. And it does not require
-- rewriting a 76-line function that already handles eight tables correctly, where the risk of a
-- transcription slip outweighs the benefit of keeping it all in one place.
--
-- Same convention as 0097: null the name, let clients render "(anonymous)".

alter table public.arcade_events alter column actor_name drop not null;

comment on column public.arcade_events.actor_name is
    'Username snapshot. Nulled when the account is deleted; render null as "(anonymous)".';

create or replace function public.scrub_arcade_identity()
returns trigger
language plpgsql
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
begin
    update public.arcade_events set actor_name  = null where actor_user_id  = old.id;
    update public.arcade_events set victim_name = null where victim_user_id = old.id;
    return old;
end;
$function$;

revoke all on function public.scrub_arcade_identity() from public, anon, authenticated;

drop trigger if exists scrub_arcade_identity_on_delete on auth.users;
create trigger scrub_arcade_identity_on_delete
    before delete on auth.users
    for each row execute function public.scrub_arcade_identity();

-- Retrospective, for anybody already deleted before this shipped. Costs nothing today (there are no
-- orphans yet) and means the rule is true of the whole table rather than only of future deletions.
update public.arcade_events set actor_name  = null where actor_user_id  is null and actor_name  is not null;
update public.arcade_events set victim_name = null where victim_user_id is null and victim_name is not null;

-- ---- 2. the weekly digest's data --------------------------------------------
--
-- WEEKLY, and not per-event. Per-event announcing needs an events claim, a posted-message id, a
-- leader-change state machine, a per-victim cooldown and a five-minute cron, all of which exist to
-- solve problems (double posting, flapping, ping spam, three events per single run) that a once-a-
-- week message does not have. And it would post nothing at all today: of the six events on the
-- table, zero have a victim, because a board with one player has nobody to take a crown from.
--
-- Returns the week rather than rendering it, so the copy lives in the edge function where it can be
-- previewed and changed without a migration.
--
-- Deliberately NOT returning discord_id. The digest names people in plain text with mentions
-- disabled: a mention would turn "you lost your record" into a notification, and joining a TF4ALL
-- username to a Discord account in a public channel is an identity disclosure nobody opted into.

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
    -- Crowns that changed hands between two different people. BOTH scopes: an outright course
    -- record and a per-car record are different prizes, and collapsing them would tell somebody
    -- their AE86 time was beaten in the same words used for losing the course outright. The
    -- digest names the car, so the scope and car_id both come back and course-scope sorts first,
    -- since the outright record is the bigger story when the list has to be capped.
    --
    -- A first time on an empty board is not a story, hence the two-different-people requirement,
    -- and the actor being null means the account is gone.
    --
    -- consumed_at is null is what makes the stamp in section 3 mean anything. Without it the
    -- column would be written and never read, and a second invocation inside the same week (a
    -- manual run, or a retry after the caller gave up on a post that had in fact succeeded) would
    -- tell every steal a second time. The window is still applied on top, so an event that somehow
    -- went untold for a fortnight stays untold: a story that old is not news.
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
        'top', coalesce((select jsonb_agg(jsonb_build_object(
                'rank', r.rank, 'author', r.author, 'points', r.points,
                'course_crowns', r.course_crowns) order by r.rank)
            from public.get_arcade_overall_ranking(p_game, 5) r), '[]'::jsonb)
    );
$function$;

revoke all on function public.get_arcade_week(text, timestamptz) from public, anon, authenticated;
grant execute on function public.get_arcade_week(text, timestamptz) to service_role;

-- ---- 3. marking a week as told ----------------------------------------------
--
-- The digest stamps consumed_at so a crashed or re-run post cannot tell the same story twice.
-- Separate from the read so the edge function only marks AFTER Discord has accepted the message:
-- doing both in one call would lose a week's events to a failed post.

create or replace function public.mark_arcade_events_consumed(
    p_game  text,
    p_until timestamptz)
returns integer
language sql
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with done as (
        update public.arcade_events
           set consumed_at = now()
         where game = p_game and consumed_at is null and created_at <= p_until
        returning 1)
    select count(*)::integer from done;
$function$;

revoke all on function public.mark_arcade_events_consumed(text, timestamptz) from public, anon, authenticated;
grant execute on function public.mark_arcade_events_consumed(text, timestamptz) to service_role;
