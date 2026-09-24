-- Where everybody was, so the digest can say where they have got to.
--
-- "Mhytee moved from 71st to 65th worldwide" is the line a weekly summary is for, and it cannot be
-- computed from the present. Nothing has ever recorded a past standing, so this starts collecting
-- one. It is worth creating early precisely because it is worthless until it has run twice.
--
-- Only Trueforce For All users are recorded. The merged standing includes 203 scraped names, and
-- keeping a daily history of other people's positions is a lot of somebody else's data to hold for
-- a line we would never print about them.
create table if not exists public.arcade_standing_history (
    game        text     not null,
    user_id     uuid     not null references auth.users(id) on delete cascade,
    taken_on    date     not null default (now() at time zone 'utc')::date,
    merged_rank integer,
    merged_of   integer,
    tf_rank     integer,
    tf_of       integer,
    points      integer,
    primary key (game, user_id, taken_on)
);

alter table public.arcade_standing_history enable row level security;
revoke all on public.arcade_standing_history from anon, authenticated;

create index if not exists arcade_standing_history_when
    on public.arcade_standing_history (game, taken_on desc);

-- Idempotent per day: running twice in one day overwrites rather than fails, so a manual run or a
-- retried cron cannot break the schedule.
create or replace function public.snapshot_arcade_standing(p_game text)
returns integer
language sql
security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with merged as (select * from public.get_arcade_scores_merged(p_game)),
    tf as (select * from public.get_arcade_scores(p_game)),
    rows as (
        select p_game as game,
               t.user_id,
               (now() at time zone 'utc')::date as taken_on,
               (select m.rank from merged m where m.who = 'u:' || t.user_id::text) as merged_rank,
               (select count(*)::integer from merged) as merged_of,
               t.rank as tf_rank,
               (select count(*)::integer from tf) as tf_of,
               t.points
        from tf t
    ),
    ins as (
        insert into public.arcade_standing_history
            (game, user_id, taken_on, merged_rank, merged_of, tf_rank, tf_of, points)
        select game, user_id, taken_on, merged_rank, merged_of, tf_rank, tf_of, points from rows
        on conflict (game, user_id, taken_on) do update
            set merged_rank = excluded.merged_rank, merged_of = excluded.merged_of,
                tf_rank = excluded.tf_rank, tf_of = excluded.tf_of, points = excluded.points
        returning 1
    )
    select count(*)::integer from ins;
$function$;

revoke all on function public.snapshot_arcade_standing(text) from anon, authenticated, public;

-- Daily, ten minutes after the TeknoParrot sync, so the snapshot reflects the board it just read.
select cron.unschedule('tf4all-arcade-standing-snapshot')
where exists (select 1 from cron.job where jobname = 'tf4all-arcade-standing-snapshot');

select cron.schedule('tf4all-arcade-standing-snapshot', '20 4 * * *',
                     $job$ select public.snapshot_arcade_standing('ID8'); $job$);

-- A baseline today, so next week's digest has something to compare against.
select public.snapshot_arcade_standing('ID8');