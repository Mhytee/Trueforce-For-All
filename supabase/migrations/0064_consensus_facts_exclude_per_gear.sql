-- Keep per-gear redline facts (redline_g1..g16, added in 0063) OUT of the
-- 'consensus_facts' achievement metric.
--
-- compute_member_metrics counts distinct (game, car_id, fact_type,
-- variant_signature) consensus tuples a user backs, feeding the
-- 'car_fact_contributor' (3+) and 'car_fact_authority' (25+) achievements.
-- With per-gear facts a single car could mint up to 17 tuples, trivializing
-- those thresholds. Exclude redline_g% so per-gear contributions don't inflate
-- the count (the overall 'redline' fact still counts, once, as before).
--
-- Body is the current LIVE compute_member_metrics (which has drifted ahead of
-- repo 0040: it carries the founder / supporter_months / founding_supporter
-- columns added by later achievement migrations) with ONE added filter in the
-- facts CTE. Committing it here also resyncs the repo to live for this function.

CREATE OR REPLACE FUNCTION public.compute_member_metrics()
 RETURNS TABLE(user_id uuid, discord_id text, discord_username text, upload_count bigint, total_downloads bigint, best_wilson numeric, consensus_facts bigint, votes_cast bigint, founder numeric, supporter_months numeric, founding_supporter numeric)
 LANGUAGE sql
 STABLE SECURITY DEFINER
 SET search_path TO 'public', 'pg_temp'
AS $function$
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
      and s.fact_type !~ '^redline_g'
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
    union select user_id from public.founding_supporters
  )
  select i.uid as user_id, dl.discord_id, dl.discord_username,
         coalesce(up.cnt,0)::bigint, coalesce(up.dl,0)::bigint, coalesce(up.wil,0)::numeric,
         coalesce(f.cf,0)::bigint, coalesce(vt.vc,0)::bigint,
         (case when au.created_at < timestamptz '2026-08-01' then 1 else 0 end)::numeric as founder,
         (case when e.is_supporter and e.supporter_since is not null
               then (extract(year from age(now(), e.supporter_since)) * 12 + extract(month from age(now(), e.supporter_since)))
               else 0 end)::numeric as supporter_months,
         (case when fs.uid is not null or fov.user_id is not null then 1 else 0 end)::numeric as founding_supporter
  from ids i
  left join uploads up on up.uid = i.uid
  left join facts   f  on f.uid_text = i.uid::text
  left join votes   vt on vt.uid_text = i.uid::text
  left join public.discord_links dl on dl.user_id = i.uid
  left join public.entitlements e  on e.user_id  = i.uid
  left join founding_sup fs on fs.uid = i.uid
  left join public.founding_supporters fov on fov.user_id = i.uid
  left join auth.users au on au.id = i.uid;
$function$;
