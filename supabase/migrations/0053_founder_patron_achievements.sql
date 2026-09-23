-- 0053: three new achievements -- Founder (early-access account), Loyal Patron (active supporter
-- tenure), Founding Supporter (first 25 supporters). Adds their metrics to compute_member_metrics
-- + get_my_achievements, and a `secret` flag so achievements that are impossible to newly earn
-- (Founder after the cutoff, Founding Supporter after #25) are HIDDEN from anyone who hasn't earned
-- them -- they only show for the people who hold them, so they never clutter the tab as locked items.
--
-- Founder = account created before the cutoff below (server-set created_at, so it can't be gamed by
-- reinstalling an old plugin version). Founding Supporter = first 25 by supporter_since, permanent
-- even if they lapse. Loyal Patron = currently active supporter for 3+ months (lost on lapse).

-- 1. schema: allow the new metrics + a secret flag.
alter table public.achievements drop constraint if exists achievements_metric_check;
alter table public.achievements add constraint achievements_metric_check
  check (metric in ('upload_count','total_downloads','best_wilson','consensus_facts','votes_cast',
                    'founder','supporter_months','founding_supporter'));
alter table public.achievements add column if not exists secret boolean not null default false;

-- 2. compute_member_metrics: add the new metrics, and broaden the population to every discord-linked
-- or entitled user (not just contributors) so non-contributing founders/supporters get evaluated.
-- Zero-metric users don't affect the percentile cutoffs.
drop function if exists public.compute_member_metrics();
create or replace function public.compute_member_metrics()
returns table(user_id uuid, discord_id text, discord_username text,
              upload_count bigint, total_downloads bigint, best_wilson numeric,
              consensus_facts bigint, votes_cast bigint,
              founder numeric, supporter_months numeric, founding_supporter numeric)
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
  founding_sup as (
    select uid from (
      select user_id as uid, row_number() over (order by supporter_since asc) as rn
      from public.entitlements where supporter_since is not null
    ) r where rn <= 25
  ),
  ids as (
    select uid from uploads
    union select uid_text::uuid from facts
    union select uid_text::uuid from votes
    union select user_id from public.discord_links
    union select user_id from public.entitlements
  )
  select i.uid as user_id, dl.discord_id, dl.discord_username,
         coalesce(up.cnt,0)::bigint, coalesce(up.dl,0)::bigint, coalesce(up.wil,0)::numeric,
         coalesce(f.cf,0)::bigint, coalesce(vt.vc,0)::bigint,
         (case when au.created_at < timestamptz '2026-07-01' then 1 else 0 end)::numeric as founder,
         (case when e.is_supporter and e.supporter_since is not null
               then (extract(year from age(now(), e.supporter_since)) * 12 + extract(month from age(now(), e.supporter_since)))
               else 0 end)::numeric as supporter_months,
         (case when fs.uid is not null then 1 else 0 end)::numeric as founding_supporter
  from ids i
  left join uploads up on up.uid = i.uid
  left join facts   f  on f.uid_text = i.uid::text
  left join votes   vt on vt.uid_text = i.uid::text
  left join public.discord_links dl on dl.user_id = i.uid
  left join public.entitlements e  on e.user_id  = i.uid
  left join founding_sup fs on fs.uid = i.uid
  left join auth.users au on au.id = i.uid;
$$;
revoke all on function public.compute_member_metrics() from public, anon, authenticated;
grant execute on function public.compute_member_metrics() to service_role;

-- 3. get_my_achievements: expose the new metrics for the caller + drop secret achievements the
-- caller hasn't earned (so Founder / Founding Supporter never show as un-earnable clutter).
create or replace function public.get_my_achievements()
returns table(
  key text, label text, description text, tier int, sort int,
  metric text, kind text, threshold numeric, current_value numeric, earned boolean)
language sql stable security definer set search_path = public, pg_temp as $$
  with me as (select auth.uid() as uid),
  uploads as (
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
  ),
  allm as (
    select i.uid,
      coalesce(up.cnt,0)::numeric as upload_count,
      coalesce(up.dl,0)::numeric  as total_downloads,
      coalesce(up.wil,0)::numeric as best_wilson,
      coalesce(f.cf,0)::numeric   as consensus_facts,
      coalesce(vt.vc,0)::numeric  as votes_cast
    from ids i
    left join uploads up on up.uid = i.uid
    left join facts   f  on f.uid_text = i.uid::text
    left join votes   vt on vt.uid_text = i.uid::text
  ),
  mine as (
    select
      coalesce((select upload_count    from allm where uid = (select uid from me)),0) as upload_count,
      coalesce((select total_downloads from allm where uid = (select uid from me)),0) as total_downloads,
      coalesce((select best_wilson     from allm where uid = (select uid from me)),0) as best_wilson,
      coalesce((select consensus_facts from allm where uid = (select uid from me)),0) as consensus_facts,
      coalesce((select votes_cast      from allm where uid = (select uid from me)),0) as votes_cast,
      (select case when exists(select 1 from auth.users u where u.id = (select uid from me) and u.created_at < timestamptz '2026-07-01') then 1 else 0 end)::numeric as founder,
      coalesce((select case when e.is_supporter and e.supporter_since is not null
                       then (extract(year from age(now(), e.supporter_since)) * 12 + extract(month from age(now(), e.supporter_since)))
                       else 0 end
                  from public.entitlements e where e.user_id = (select uid from me)), 0)::numeric as supporter_months,
      coalesce((select case when e.supporter_since is not null and
                       (select count(*) from public.entitlements e2 where e2.supporter_since is not null and e2.supporter_since <= e.supporter_since) <= 25
                       then 1 else 0 end
                  from public.entitlements e where e.user_id = (select uid from me)), 0)::numeric as founding_supporter
  )
  select q.key, q.label, q.description, q.tier, q.sort, q.metric, q.kind, q.threshold, q.current_value, q.earned
  from (
    select a.key, a.label, a.description, a.tier, a.sort, a.metric, a.kind, a.threshold,
      cur.v as current_value,
      case when a.kind = 'threshold' then cur.v >= a.threshold
           else (pop.n >= a.min_population and cur.v >= a.min_floor and cur.v >= coalesce(cut.cutoff, 1e18))
      end as earned,
      a.secret as secret
    from public.achievements a
    cross join lateral (
      select case a.metric
        when 'upload_count'       then (select upload_count       from mine)
        when 'total_downloads'    then (select total_downloads    from mine)
        when 'best_wilson'        then (select best_wilson        from mine)
        when 'consensus_facts'    then (select consensus_facts    from mine)
        when 'votes_cast'         then (select votes_cast         from mine)
        when 'founder'            then (select founder            from mine)
        when 'supporter_months'   then (select supporter_months   from mine)
        when 'founding_supporter' then (select founding_supporter from mine)
      end as v
    ) cur
    cross join lateral (
      select count(*) as n from allm where (case a.metric
        when 'upload_count'    then upload_count    when 'total_downloads' then total_downloads
        when 'best_wilson'     then best_wilson     when 'consensus_facts' then consensus_facts
        when 'votes_cast'      then votes_cast end) > 0
    ) pop
    left join lateral (
      select v2 as cutoff from (
        select (case a.metric
          when 'upload_count'    then upload_count    when 'total_downloads' then total_downloads
          when 'best_wilson'     then best_wilson     when 'consensus_facts' then consensus_facts
          when 'votes_cast'      then votes_cast end) as v2
        from allm
      ) x where x.v2 > 0 order by x.v2 desc
      offset greatest(0, ceil(a.threshold/100.0 * pop.n)::int - 1) limit 1
    ) cut on a.kind = 'percentile'
    where a.enabled
  ) q
  where not (q.secret and not coalesce(q.earned, false))
  order by q.sort;
$$;
revoke all on function public.get_my_achievements() from public, anon;
grant execute on function public.get_my_achievements() to authenticated;

-- 4. the three new achievement rows.
insert into public.achievements (key, label, description, metric, kind, threshold, secret, tier, sort) values
  ('founder',            'Founder',            'Had a Trueforce account during early access',  'founder',            'threshold', 1, true,  1,  5),
  ('loyal_patron',       'Loyal Patron',       'An active supporter for 3+ months',            'supporter_months',   'threshold', 3, false, 2, 70),
  ('founding_supporter', 'Founding Supporter', 'One of the first 25 supporters',               'founding_supporter', 'threshold', 1, true,  2, 71)
on conflict (key) do nothing;
