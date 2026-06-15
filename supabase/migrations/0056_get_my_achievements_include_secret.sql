-- 0056: optional include_secret arg on get_my_achievements, for a dev/test toggle that reveals
-- the hidden achievements (Founder/OG, Founding Supporter) even when unearned. Default false keeps
-- the normal behaviour for everyone else. Same body as 0054, only the final WHERE changes.
drop function if exists public.get_my_achievements();
create or replace function public.get_my_achievements(p_include_secret boolean default false)
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
      (select case when exists(select 1 from auth.users u where u.id = (select uid from me) and u.created_at < timestamptz '2026-08-01') then 1 else 0 end)::numeric as founder,
      coalesce((select case when e.is_supporter and e.supporter_since is not null
                       then (extract(year from age(now(), e.supporter_since)) * 12 + extract(month from age(now(), e.supporter_since)))
                       else 0 end
                  from public.entitlements e where e.user_id = (select uid from me)), 0)::numeric as supporter_months,
      (select case when
                 exists(select 1 from public.founding_supporters fo where fo.user_id = (select uid from me))
                 or exists(select 1 from public.entitlements e where e.user_id = (select uid from me) and e.supporter_since is not null
                           and (select count(*) from public.entitlements e2 where e2.supporter_since is not null and e2.supporter_since <= e.supporter_since) <= 25)
               then 1 else 0 end)::numeric as founding_supporter
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
  where (p_include_secret or not (q.secret and not coalesce(q.earned, false)))
  order by q.sort;
$$;
revoke all on function public.get_my_achievements(boolean) from public, anon;
grant execute on function public.get_my_achievements(boolean) to authenticated;
