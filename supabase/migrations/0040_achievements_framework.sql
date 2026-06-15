-- 0040: data-driven, tiered ACHIEVEMENT framework for Discord roles.
--
-- Each positive community/DB action gets a base + prestige achievement, defined as a ROW
-- in `achievements` (adding/tuning one = a row change, no code). Two rule kinds:
--   * threshold  : metric >= threshold (e.g. >= 25 downloads)
--   * percentile : metric in the top (threshold)% across members with metric>0, AND
--                  metric >= min_floor, AND at least min_population members have metric>0
--                  (so "top 10%" can't be won trivially in a tiny population).
-- Metrics per member come from compute_member_metrics(); the discord-role-sync Edge
-- Function evaluates the rules (percentiles are easy in TS) and assigns roles.
--
-- Replaces discord_role_map (0038) + the rule-baked compute_discord_roles (0039).

drop function if exists public.compute_discord_roles();
drop table if exists public.discord_role_map;

create table public.achievements (
  key             text primary key,
  label           text not null,
  description     text,
  metric          text not null check (metric in ('upload_count','total_downloads','best_wilson','consensus_facts','votes_cast')),
  kind            text not null default 'threshold' check (kind in ('threshold','percentile')),
  threshold       numeric not null,
  min_floor       numeric not null default 0,
  min_population  int     not null default 5,
  tier            int     not null default 1,
  enabled         boolean not null default true,
  discord_role_id text,
  sort            int     not null default 0,
  updated_at      timestamptz not null default now()
);
alter table public.achievements enable row level security;
revoke all on public.achievements from anon, authenticated;

insert into public.achievements (key, label, description, metric, kind, threshold, min_floor, min_population, tier, sort) values
  ('creator',              'Creator',              'Shared at least 1 community item',           'upload_count',    'threshold',   1,   0,   5, 1, 10),
  ('prolific_creator',     'Prolific Creator',     'Shared 10+ community items',                 'upload_count',    'threshold',  10,   0,   5, 2, 11),
  ('popular_creator',      'Popular Creator',      '25+ total downloads across your uploads',    'total_downloads', 'threshold',  25,   0,   5, 1, 20),
  ('top_creator',          'Top Creator',          'Top 10% of creators by total downloads',     'total_downloads', 'percentile', 10,  50,   5, 2, 21),
  ('acclaimed',            'Acclaimed',            'Has a highly-rated upload (Wilson >= 0.70)', 'best_wilson',     'threshold', 0.70, 0,   5, 1, 30),
  ('top_rated',            'Top Rated',            'Top 10% of creators by best rating',         'best_wilson',     'percentile', 10, 0.60,  5, 2, 31),
  ('car_fact_contributor', 'Car Fact Contributor', 'Backs 3+ car-fact consensus values',         'consensus_facts', 'threshold',   3,   0,   5, 1, 40),
  ('car_fact_authority',   'Car Fact Authority',   'Backs 25+ car-fact consensus values',        'consensus_facts', 'threshold',  25,   0,   5, 2, 41),
  ('curator',              'Curator',              'Rated 10+ community items',                  'votes_cast',      'threshold',  10,   0,   5, 1, 50),
  ('tastemaker',           'Tastemaker',           'Rated 50+ community items',                  'votes_cast',      'threshold',  50,   0,   5, 2, 51)
on conflict (key) do nothing;

-- Per-member metrics for ALL contributors (linked or not, so the function can compute
-- percentiles across the whole population). submitter_id / voter_id are auth.uid()::text
-- since the auth-gate; the uuid regex filters out any legacy IP/device-keyed rows.
create or replace function public.compute_member_metrics()
returns table(user_id uuid, discord_id text, discord_username text,
              upload_count bigint, total_downloads bigint, best_wilson numeric,
              consensus_facts bigint, votes_cast bigint)
language sql stable security definer set search_path = public, pg_temp as $$
  with uploads as (
    select u.uid, count(*) as cnt, coalesce(sum(u.downloads),0) as dl, coalesce(max(u.wilson_score),0) as wil
    from (
      select owner_user_id as uid, downloads, wilson_score from public.presets        where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.game_presets   where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.custom_engines where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.packs          where owner_user_id is not null and not is_suppressed
    ) u group by u.uid
  ),
  facts as (
    select s.submitter_id as uid_text, count(distinct (s.game, s.car_id, s.fact_type, s.variant_signature)) as cf
    from public.car_fact_submissions s
    join public.car_fact_consensus c
      on c.game=s.game and c.car_id=s.car_id and c.fact_type=s.fact_type
     and c.variant_signature=s.variant_signature and not c.is_suppressed and c.payload=s.payload
    where s.submitter_id ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    group by s.submitter_id
  ),
  votes as (
    select v.uid_text, count(*) as vc from (
      select voter_id as uid_text from public.preset_votes      where value <> 0
      union all select voter_id from public.game_preset_votes   where value <> 0
      union all select voter_id from public.custom_engine_votes where value <> 0
      union all select voter_id from public.pack_votes          where value <> 0
    ) v
    where v.uid_text ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    group by v.uid_text
  ),
  ids as (
    select uid from uploads
    union select uid_text::uuid from facts
    union select uid_text::uuid from votes
  )
  select i.uid as user_id, dl.discord_id, dl.discord_username,
         coalesce(up.cnt,0)::bigint, coalesce(up.dl,0)::bigint, coalesce(up.wil,0)::numeric,
         coalesce(f.cf,0)::bigint, coalesce(vt.vc,0)::bigint
  from ids i
  left join uploads up on up.uid = i.uid
  left join facts   f  on f.uid_text = i.uid::text
  left join votes   vt on vt.uid_text = i.uid::text
  left join public.discord_links dl on dl.user_id = i.uid;
$$;
revoke all on function public.compute_member_metrics() from public, anon, authenticated;
grant execute on function public.compute_member_metrics() to service_role;
