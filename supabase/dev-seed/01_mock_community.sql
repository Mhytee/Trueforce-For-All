-- supabase/dev-seed/01_mock_community.sql
-- Mock community content for testing list views, sorting (Wilson +
-- downloads), the active-card top-community dropdown, the download +
-- pack-install + delete flow, and target-games tiering on game presets.
--
-- Apply via:
--   psql ... -f supabase/dev-seed/01_mock_community.sql
-- or via the Supabase MCP execute_sql tool.
--
-- Teardown is 99_mock_community_teardown.sql -- every mock row carries
-- submitter_id = 'mock-seed' so removal is a one-liner per table.
--
-- Conventions:
--   - Car IDs + game name strings match the maintainer's local library
--     (FH6 numeric car ids, FH5, AssettoCorsa slugs, Wreckfest2). The
--     plugin's active-card top-community dropdown keys on
--     (game, car_id) literal-equality, so mock rows that don't match
--     real installed cars wouldn't surface in the picker.
--   - owner_user_id is NULL on all mock rows (no auth.users seeded).
--     Mock items appear as "anonymous community uploads" with the
--     denormalised author name in the UI.
--   - submitter_id = 'mock-seed', source_ip_hash = 'mock-seed-ip' so
--     teardown is trivial.
--   - Wilson scores are hand-assigned to four legible tiers (viral
--     0.92+, popular 0.72-0.82, average 0.50-0.65, quiet 0.05-0.20)
--     so Wilson / downloads / newest sorts each show distinct order.
--   - Bodies are minimal valid JSON shapes so the download path can
--     deserialize them. A handful per kind get richer non-null
--     sections so the Preview Window has interesting content.
--   - Schema note: game_presets.game has a NOT-NULL + length(1..64)
--     check, so Universal target_games rows park 'universal' as a
--     sentinel and multi-game rows use their first target as the
--     legacy game column value. target_games is the array the client
--     reads to render the Scope badge.

begin;

-- Idempotent: wipe any prior mock content first.
delete from public.packs          where submitter_id = 'mock-seed';
delete from public.custom_engines where submitter_id = 'mock-seed';
delete from public.game_presets   where submitter_id = 'mock-seed';
delete from public.presets        where submitter_id = 'mock-seed';

-- ===========================================================
-- CAR PRESETS (18 total)
-- ===========================================================
-- FH6 (10), FH5 (3), AssettoCorsa (5). Cars chosen from the
-- maintainer's installed library so the top-community dropdown
-- actually has data when you drive these cars in-game.

insert into public.presets
  (name, author, description, game, car_id, body, effect_tags,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version)
values
  -- FH6 / Car_2267 -- viral + popular + average (3 presets, exercises
  -- the top-community dropdown's tier ordering)
  ('Endurance Spec', 'ApexPredator', 'Smooth all-around tune for long stints. Subtle road texture, detailed engine pulse.',
   'FH6', 'Car_2267',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.08,"Freq":85.0},"RoadBumps":{"Enabled":true,"Gain":0.55,"Freq":60.0},"RevLimiter":{"Enabled":true,"Gain":0.6}}}',
   '{engine,roadbumps,revlimiter}', 312, 8, 0.94, 1850, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Track Day Punchy', 'DriftKing', 'Stronger gear shift + traction loss for short sessions.',
   'FH6', 'Car_2267',
   '{"override":{"GearShift":{"Enabled":true,"Gain":0.78},"TractionLoss":{"Enabled":true,"Gain":0.7}}}',
   '{gearshift,tractionloss}', 84, 6, 0.79, 412, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Cruise Mode', 'NightShift', 'Dialed-back forces for relaxed driving.',
   'FH6', 'Car_2267',
   '{"override":{}}',
   '{}', 22, 3, 0.62, 76, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- FH6 / Car_378 -- viral + average
  ('Apex Hunter', 'ApexPredator', 'Carefully balanced tune. Detailed engine pulse, firm road bumps, gear shift + abs feel.',
   'FH6', 'Car_378',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.075,"Freq":78.0},"RoadBumps":{"Enabled":true,"Gain":0.65,"Freq":70.0},"GearShift":{"Enabled":true,"Gain":0.6},"AbsClick":{"Enabled":true,"Gain":0.5}}}',
   '{engine,roadbumps,gearshift,abs}', 258, 11, 0.92, 1240, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Drift Bias', 'DriftKing', 'Heavier traction loss feedback, lighter engine pulse.',
   'FH6', 'Car_378',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.72}}}',
   '{tractionloss}', 26, 4, 0.58, 89, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- FH6 / Car_4222 -- popular + average
  ('Hybrid Punch', 'RaceCraft42', 'Snappy engine pulse + pronounced gear shift; captures hybrid boost transitions.',
   'FH6', 'Car_4222',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.085},"GearShift":{"Enabled":true,"Gain":0.7}}}',
   '{engine,gearshift}', 68, 4, 0.78, 285, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Quiet Predator', 'GearGremlin', 'Subdued forces, traction loss focus.',
   'FH6', 'Car_4222',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.55}}}',
   '{tractionloss}', 18, 2, 0.58, 51, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- FH6 / Car_422 -- popular + quiet
  ('V12 Symphony', 'WheelmanRX', 'Engine pulse matched to V12 firing. Pairs with the V12 Italian custom engine.',
   'FH6', 'Car_422',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.09,"Freq":92.0},"Airborne":{"Enabled":true}}}',
   '{engine,airborne}', 92, 7, 0.81, 367, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Stock Plus', 'NightShift', 'Just a touch firmer than stock.',
   'FH6', 'Car_422',
   '{"override":{}}',
   '{}', 4, 0, 0.18, 19, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- FH6 / Car_455 -- popular
  ('Mid-Engine Refined', 'RaceCraft42', 'Mid-engine balance shift captured via collision + traction loss.',
   'FH6', 'Car_455',
   '{"override":{"Collision":{"Enabled":true,"Gain":0.7},"TractionLoss":{"Enabled":true,"Gain":0.6}}}',
   '{collision,tractionloss}', 57, 5, 0.75, 198, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- FH5 / Car_424 (1997 Mazda RX-7) -- viral + average
  ('Rotary Bliss', 'NightShift', 'Tuned around the rotary engine character. Smooth high-frequency engine pulse.',
   'FH5', 'Car_424',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.072,"Freq":82.0},"RoadBumps":{"Enabled":true,"Gain":0.50}}}',
   '{engine,roadbumps}', 287, 9, 0.93, 1620, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Casual Cruise', 'GearGremlin', 'Easy starting point, lighter forces.',
   'FH5', 'Car_424',
   '{"override":{}}',
   '{}', 14, 2, 0.55, 42, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- FH5 / Car_4260 -- popular
  ('Modern Hot Hatch', 'WheelmanRX', 'I4 character with subtle road bumps.',
   'FH5', 'Car_4260',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.078,"Freq":86.0}}}',
   '{engine}', 61, 4, 0.77, 215, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- AssettoCorsa / ferrari_f40 -- viral + average
  ('Twin-Turbo V8 Detailed', 'ApexPredator', 'Captures the F40 twin-turbo V8 firing order. Detailed engine pulse with abs click.',
   'AssettoCorsa', 'ferrari_f40',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.078,"Freq":80.0},"AbsClick":{"Enabled":true,"Gain":0.45}}}',
   '{engine,abs}', 245, 7, 0.92, 1180, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Calibration Baseline', 'GearGremlin', 'Neutral starting point.',
   'AssettoCorsa', 'ferrari_f40',
   '{"override":{}}',
   '{}', 12, 3, 0.51, 38, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- AssettoCorsa / ks_mazda_mx5_cup -- popular
  ('Cup Car Crisp', 'WheelmanRX', 'Tight, crisp tune for the MX-5 cup spec. Light forces, sharp pulse.',
   'AssettoCorsa', 'ks_mazda_mx5_cup',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.07,"Freq":88.0}}}',
   '{engine}', 72, 5, 0.79, 248, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- AssettoCorsa / ks_nissan_skyline_r34 -- average
  ('Drift School', 'DriftKing', 'Tuned for drift practice. Traction loss + abs emphasis.',
   'AssettoCorsa', 'ks_nissan_skyline_r34',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.72},"AbsClick":{"Enabled":true,"Gain":0.5}}}',
   '{tractionloss,abs}', 28, 4, 0.61, 95, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- AssettoCorsa / prvvy_mustang_2024_tuned -- quiet
  ('Restrained', 'RaceCraft42', 'Light forces; chassis does the talking.',
   'AssettoCorsa', 'prvvy_mustang_2024_tuned',
   '{"override":{}}',
   '{}', 2, 0, 0.12, 8, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- AssettoCorsa / gravygarage_street_e36_touring -- viral + popular + average
  ('Touring Sweet Spot', 'NightShift', 'Balanced setup tuned for the E36 touring chassis. Smooth road feel, detailed engine pulse, abs click on the threshold.',
   'AssettoCorsa', 'gravygarage_street_e36_touring',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.076,"Freq":80.0},"RoadBumps":{"Enabled":true,"Gain":0.55,"Freq":62.0},"AbsClick":{"Enabled":true,"Gain":0.48}}}',
   '{engine,roadbumps,abs}', 264, 9, 0.92, 1340, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Aggressive Street', 'DriftKing', 'Punchier engine pulse + stronger gear shift for street-style aggression.',
   'AssettoCorsa', 'gravygarage_street_e36_touring',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.088},"GearShift":{"Enabled":true,"Gain":0.72},"TractionLoss":{"Enabled":true,"Gain":0.6}}}',
   '{engine,gearshift,tractionloss}', 78, 5, 0.80, 312, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Calibrated Daily', 'GearGremlin', 'Moderate forces suitable for daily driving. Easy on the wrists over long sessions.',
   'AssettoCorsa', 'gravygarage_street_e36_touring',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.07}}}',
   '{engine}', 24, 3, 0.62, 84, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- AssettoCorsa / bdc_streetspec_e36_v4 -- viral + popular + average
  ('Drift Setup Pro', 'DriftKing', 'Aggressive traction loss + gear shift tuned for BDC drift sessions. Strong wheel kickback on grip break.',
   'AssettoCorsa', 'bdc_streetspec_e36_v4',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.78},"GearShift":{"Enabled":true,"Gain":0.7},"EnginePulse":{"Enabled":true,"Gain":0.082}}}',
   '{tractionloss,gearshift,engine}', 251, 8, 0.92, 1290, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Street Spec Calibrated', 'RaceCraft42', 'Balanced street-spec tune. Detailed engine pulse with road bump texture.',
   'AssettoCorsa', 'bdc_streetspec_e36_v4',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.075,"Freq":78.0},"RoadBumps":{"Enabled":true,"Gain":0.55}}}',
   '{engine,roadbumps}', 67, 4, 0.78, 244, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Cone Killer', 'GearGremlin', 'Autocross-oriented tune. Crisp abs click + collision feedback for tight courses.',
   'AssettoCorsa', 'bdc_streetspec_e36_v4',
   '{"override":{"AbsClick":{"Enabled":true,"Gain":0.55},"Collision":{"Enabled":true,"Gain":0.6}}}',
   '{abs,collision}', 26, 4, 0.58, 92, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1);

-- ===========================================================
-- GAME PRESETS (10 total)
-- ===========================================================
-- target_games variety: 3 Universal ('{}'), 5 single-game, 2 multi-game.
-- Game column uses the SHORT-CODE the plugin emits as ActiveGame:
-- FH6, FH5, AssettoCorsa, Wreckfest2.

insert into public.game_presets
  (name, author, description, game, body, effect_tags,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version, target_games)
values
  ('Cinematic Heavy', 'ApexPredator', 'Strong forces, detailed road texture. Best with a high-torque wheel.',
   'universal',
   '{"snapshot":{"MasterGain":1.1,"FfbScale":1.0,"EnginePulse":{"Enabled":true,"Gain":0.09},"RoadBumps":{"Enabled":true,"Gain":0.65}}}',
   '{engine,roadbumps,collision}', 245, 8, 0.93, 1410, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{}'),
  ('Endurance Light', 'NightShift', 'Reduced fatigue tune. Lighter forces, longer sessions.',
   'universal',
   '{"snapshot":{"MasterGain":0.9,"FfbScale":0.85}}',
   '{}', 76, 4, 0.80, 312, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{}'),
  ('Beginner-Friendly', 'WheelmanRX', 'Safe defaults for new Trueforce users.',
   'universal',
   '{"snapshot":{"MasterGain":1.0,"FfbScale":1.0}}',
   '{}', 19, 2, 0.59, 64, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{}'),

  ('Forza Arcade Plus', 'DriftKing', 'Tuned for Forza''s arcade physics. Stronger collision + airborne ducking.',
   'FH6',
   '{"snapshot":{"Collision":{"Enabled":true,"Gain":0.75},"Airborne":{"Enabled":true}}}',
   '{collision,airborne}', 88, 6, 0.79, 365, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"FH6"}'),
  ('FH6 Quick Start', 'GearGremlin', 'Ready-made tune to drop in. Edit from here.',
   'FH6',
   '{"snapshot":{}}',
   '{}', 21, 3, 0.59, 71, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"FH6"}'),
  ('AC Sim Purist', 'NightShift', 'Restrained synthetics on top of AC''s native FFB.',
   'AssettoCorsa',
   '{"snapshot":{"FfbScale":1.05,"EnginePulse":{"Enabled":true,"Gain":0.06}}}',
   '{engine}', 65, 5, 0.76, 234, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"AssettoCorsa"}'),
  ('AC Beginner Tune', 'RaceCraft42', 'Conservative defaults for new AC drivers.',
   'AssettoCorsa',
   '{"snapshot":{}}',
   '{}', 5, 1, 0.21, 24, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"AssettoCorsa"}'),
  ('Wreckfest Smash', 'WheelmanRX', 'Heavy collision feedback tuned for Wreckfest carnage.',
   'Wreckfest2',
   '{"snapshot":{"Collision":{"Enabled":true,"Gain":0.85}}}',
   '{collision}', 41, 4, 0.69, 152, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"Wreckfest2"}'),

  -- Multi-game: Forza series (FH5 + FH6) -- viral
  ('Forza Combo', 'ApexPredator', 'One tune for both Forza Horizon games. Bumps + collision tuned for arcade feel.',
   'FH6',
   '{"snapshot":{"RoadBumps":{"Enabled":true,"Gain":0.7},"Collision":{"Enabled":true,"Gain":0.75}}}',
   '{roadbumps,collision}', 268, 7, 0.93, 1320, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"FH6","FH5"}'),
  -- Multi-game: cross-style FH6 + AC -- average
  ('Versatile Tune', 'GearGremlin', 'Compromise tune for switching between arcade and sim. Doesn''t excel at either but works in both.',
   'FH6',
   '{"snapshot":{}}',
   '{}', 24, 4, 0.58, 81, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"FH6","AssettoCorsa"}');

-- ===========================================================
-- CUSTOM ENGINES (6 total)
-- ===========================================================
-- Bodies follow CustomEngineDef shape: name + a firing pattern.
-- Game-agnostic, so the dropdown surfaces them across any game.

insert into public.custom_engines
  (name, author, description, body,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version)
values
  ('V8 American Muscle', 'GearGremlin', 'Cross-plane V8 firing order tuned for muscle car character.',
   '{"Name":"V8 American Muscle","CustomFiringPattern":"0:1.0, 0.125:0.8, 0.25:0.95, 0.375:0.85, 0.5:1.0, 0.625:0.8, 0.75:0.95, 0.875:0.85"}',
   158, 6, 0.86, 720, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('V12 Italian', 'ApexPredator', 'V12 firing pattern with a glassy high-rev character.',
   '{"Name":"V12 Italian","CustomFiringPattern":"0:1.0, 0.083:0.9, 0.167:1.0, 0.25:0.9, 0.333:1.0, 0.417:0.9, 0.5:1.0, 0.583:0.9, 0.667:1.0, 0.75:0.9, 0.833:1.0, 0.917:0.9"}',
   202, 9, 0.89, 920, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Boxer-6 Flat', 'WheelmanRX', 'Porsche-style flat-six character. Punchy mids.',
   '{"Name":"Boxer-6 Flat","CustomFiringPattern":"0:1.0, 0.167:0.9, 0.333:1.0, 0.5:0.9, 0.667:1.0, 0.833:0.9"}',
   83, 5, 0.80, 290, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('I4 Hot Hatch', 'DriftKing', 'Inline-4 hot-hatch character. Light, raspy.',
   '{"Name":"I4 Hot Hatch","CustomFiringPattern":"0:1.0, 0.25:0.9, 0.5:1.0, 0.75:0.9"}',
   24, 3, 0.59, 88, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),
  ('Rotary Twin-Turbo', 'NightShift', 'Two-rotor wankel character. Smooth, high-frequency.',
   '{"Name":"Rotary Twin-Turbo","CustomFiringPattern":"0:0.9, 0.5:0.9"}',
   45, 4, 0.71, 165, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('V10 Audi Snarl', 'RaceCraft42', 'V10 with a hard mid-range bite.',
   '{"Name":"V10 Audi Snarl","CustomFiringPattern":"0:1.0, 0.1:0.9, 0.2:1.0, 0.3:0.9, 0.4:1.0, 0.5:0.9, 0.6:1.0, 0.7:0.9, 0.8:1.0, 0.9:0.9"}',
   3, 0, 0.17, 14, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1);

-- ===========================================================
-- PACKS (3 total)
-- ===========================================================
-- A pack body bundles entries by kind. We use CTEs so the entry ids
-- reach into the live preset / engine ids we just inserted.

with
  pkg_fh6 as (
    select id, name, game, car_id from public.presets
     where submitter_id = 'mock-seed' and game = 'FH6'
     order by wilson_score desc limit 4
  ),
  pkg_ac as (
    select id, name, game, car_id from public.presets
     where submitter_id = 'mock-seed' and game = 'AssettoCorsa'
     order by wilson_score desc limit 4
  ),
  pkg_universal_gp as (
    select id, name, game from public.game_presets
     where submitter_id = 'mock-seed' and target_games = '{}'
     order by wilson_score desc limit 2
  ),
  pkg_engines as (
    select id, name from public.custom_engines
     where submitter_id = 'mock-seed'
     order by wilson_score desc limit 2
  )
insert into public.packs
  (name, author, description, body, entry_count,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, plugin_version, content_version, author_version)
select
  'Forza Horizon 6 Starter',
  'ApexPredator',
  'A curated set of community car presets for FH6, plus a universal game preset and a couple of custom engines.',
  jsonb_build_object(
    'presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name, 'game', game, 'car_id', car_id)), '[]'::jsonb) from pkg_fh6),
    'game_presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_universal_gp),
    'custom_engines', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_engines)
  ),
  (select count(*) from pkg_fh6) + (select count(*) from pkg_universal_gp) + (select count(*) from pkg_engines),
  198, 7, 0.90, 980, 'mock-seed', 'mock-seed-ip', '0.1.24', 1, '1.0.0'
union all
select
  'Assetto Corsa Essentials',
  'NightShift',
  'Community-tested car presets for the most popular AC cars.',
  jsonb_build_object(
    'presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name, 'game', game, 'car_id', car_id)), '[]'::jsonb) from pkg_ac)
  ),
  (select count(*) from pkg_ac),
  74, 4, 0.79, 285, 'mock-seed', 'mock-seed-ip', '0.1.24', 1, '1.0.0'
union all
select
  'Multi-Game Universal Starter',
  'WheelmanRX',
  'Universal game presets plus a couple of custom engines.',
  jsonb_build_object(
    'game_presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_universal_gp),
    'custom_engines', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_engines)
  ),
  (select count(*) from pkg_universal_gp) + (select count(*) from pkg_engines),
  31, 5, 0.61, 102, 'mock-seed', 'mock-seed-ip', '0.1.24', 1, '1.0.0';

commit;
