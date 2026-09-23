-- 0042: finalize the achievement names (owner-chosen, 2026-06-11).
--
-- The person-word moves from "Creator" to "Tuner" across the sharing + popularity ladders
-- (a shared item is an FFB tune; "creator" overstated it). Curation adopts the motorsport
-- scrutineering terms; the car-fact prestige drops "Authority" for "Expert". Internal keys
-- are renamed to match labels (snake_case); safe because the role-sync Edge Function reads
-- achievements dynamically and drives Discord off discord_role_id (all still null), never
-- off the key string. No metric/threshold/kind changes -- names only.
--
--   sharing    : creator              -> tuner              'Tuner'
--                prolific_creator     -> tireless_tuner     'Tireless Tuner'   (quantity, not quality)
--   popularity : popular_creator      -> popular_tuner      'Popular Tuner'
--                top_creator          -> top_tuner          'Top Tuner'
--   quality    : highly_rated / top_rated  (labels already correct from 0041)
--   car facts  : car_fact_contributor (unchanged)           'Car Fact Contributor'
--                car_fact_authority   -> car_fact_expert    'Car Fact Expert'
--   curation   : curator              -> scrutineer         'Scrutineer'
--                tastemaker           -> chief_scrutineer   'Chief Scrutineer'

update public.achievements set key='tuner',          label='Tuner',          updated_at=now() where key='creator';
update public.achievements set key='tireless_tuner', label='Tireless Tuner', updated_at=now() where key='prolific_creator';
update public.achievements set key='popular_tuner',  label='Popular Tuner',  updated_at=now() where key='popular_creator';
update public.achievements set key='top_tuner',      label='Top Tuner',      description='Top 10% of tuners by total downloads', updated_at=now() where key='top_creator';
update public.achievements set                         description='Top 10% of tuners by best rating',     updated_at=now() where key='top_rated';
update public.achievements set key='car_fact_expert', label='Car Fact Expert', updated_at=now() where key='car_fact_authority';
update public.achievements set key='scrutineer',      label='Scrutineer',      updated_at=now() where key='curator';
update public.achievements set key='chief_scrutineer',label='Chief Scrutineer',updated_at=now() where key='tastemaker';
