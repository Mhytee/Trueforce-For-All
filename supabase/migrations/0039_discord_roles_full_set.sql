-- 0039: extend compute_discord_roles to the full role set.
-- KEY FACT (verified): car_fact_submissions.submitter_id IS auth.uid()::text (set by
-- submit_car_fact since the auth-gate; the IP lives in source_ip_hash), so car-fact
-- contributions are ALREADY user-attributed -- no schema change needed. The vote tables
-- likewise key voter_id to the user. Every metric joins discord_links, so only LINKED
-- users are computed.
--
-- Roles (thresholds are tunable right here):
--   creator              : shared >= 1 community item (preset / game preset / custom engine / pack)
--   popular_creator      : >= 25 total downloads across their shared items
--   car_fact_contributor : currently BACKS >= 3 car-fact CONSENSUS values (a submission of
--                          theirs equals the live, non-suppressed consensus payload).
--                          DYNAMIC -- recomputed each sync, so it is lost if their value
--                          loses consensus, exactly as requested.
--   curator              : cast >= 10 votes on community content (achievement badge)

drop function if exists public.compute_discord_roles();
create or replace function public.compute_discord_roles()
returns table(user_id uuid, discord_id text, discord_username text,
              upload_count bigint, total_downloads bigint, best_wilson numeric,
              consensus_facts bigint, votes_cast bigint, roles text[])
language sql stable security definer set search_path = public, pg_temp as $$
  with linked as (select user_id, discord_id, discord_username from public.discord_links),
  uploads as (
    select u.uid, count(*) as cnt,
           coalesce(sum(u.downloads),0) as dl, coalesce(max(u.wilson_score),0) as wil
    from (
      select owner_user_id as uid, downloads, wilson_score from public.presets        where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.game_presets   where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.custom_engines where owner_user_id is not null and not is_suppressed
      union all select owner_user_id, downloads, wilson_score from public.packs          where owner_user_id is not null and not is_suppressed
    ) u
    join linked l on l.user_id = u.uid
    group by u.uid
  ),
  facts as (
    select s.submitter_id as uid_text,
           count(distinct (s.game, s.car_id, s.fact_type, s.variant_signature)) as cf
    from public.car_fact_submissions s
    join public.car_fact_consensus c
      on c.game = s.game and c.car_id = s.car_id and c.fact_type = s.fact_type
     and c.variant_signature = s.variant_signature and not c.is_suppressed and c.payload = s.payload
    join linked l on l.user_id::text = s.submitter_id
    group by s.submitter_id
  ),
  votes as (
    select v.uid_text, count(*) as vc
    from (
      select voter_id as uid_text from public.preset_votes      where value <> 0
      union all select voter_id from public.game_preset_votes   where value <> 0
      union all select voter_id from public.custom_engine_votes where value <> 0
      union all select voter_id from public.pack_votes          where value <> 0
    ) v
    join linked l on l.user_id::text = v.uid_text
    group by v.uid_text
  )
  select l.user_id, l.discord_id, l.discord_username,
         coalesce(up.cnt,0)::bigint, coalesce(up.dl,0)::bigint, coalesce(up.wil,0)::numeric,
         coalesce(f.cf,0)::bigint, coalesce(vt.vc,0)::bigint,
         array_remove(array[
           case when coalesce(up.cnt,0) >= 1  then 'creator'              end,
           case when coalesce(up.dl,0)  >= 25 then 'popular_creator'      end,
           case when coalesce(f.cf,0)   >= 3  then 'car_fact_contributor' end,
           case when coalesce(vt.vc,0)  >= 10 then 'curator'              end
         ], null) as roles
  from linked l
  left join uploads up on up.uid = l.user_id
  left join facts   f  on f.uid_text = l.user_id::text
  left join votes   vt on vt.uid_text = l.user_id::text;
$$;
revoke all on function public.compute_discord_roles() from public, anon, authenticated;
grant execute on function public.compute_discord_roles() to service_role;

insert into public.discord_role_map (role_key, label) values
  ('car_fact_contributor', 'Car Fact Contributor (backs >= 3 consensus facts)'),
  ('curator',              'Curator (rated >= 10 community items)')
on conflict (role_key) do nothing;
