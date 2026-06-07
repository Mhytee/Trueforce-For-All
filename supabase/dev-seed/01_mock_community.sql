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
--   - owner_user_id is NULL on all mock rows (no auth.users entries
--     seeded). Mock items appear as "anonymous community uploads" with
--     a denormalized author name in the UI.
--   - submitter_id = 'mock-seed', source_ip_hash = 'mock-seed-ip' so
--     teardown is trivial and mock data won't be confused with real
--     uploads.
--   - Wilson scores are hand-assigned to produce four legible tiers
--     (viral 0.92+, popular 0.72-0.82, average 0.50-0.65, quiet 0.05-
--     0.20) so the Wilson / downloads / newest sort columns each show
--     a distinct ordering.
--   - Bodies are minimal valid JSON shapes so the download path can
--     deserialize them. A handful per kind get richer non-null sections
--     so the Preview Window has interesting content to render.

begin;

-- Idempotent: wipe any prior mock content first.
delete from public.packs          where submitter_id = 'mock-seed';
delete from public.custom_engines where submitter_id = 'mock-seed';
delete from public.game_presets   where submitter_id = 'mock-seed';
delete from public.presets        where submitter_id = 'mock-seed';

-- ===========================================================
-- CAR PRESETS
-- ===========================================================
-- Spread across Forza Horizon 6 (~12) and Assetto Corsa (~8).
-- Mix of cars per game so the active-card top-community dropdown
-- has data for several distinct (game, car) keys.

insert into public.presets
  (name, author, description, game, car_id, body, effect_tags,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version)
values
  -- Forza Horizon 6 / Ferrari 812 Superfast -- VIRAL
  ('GT Endurance',
   'ApexPredator',
   'Smooth all-around setup with subtle road feel and detailed engine pulse. Holds up over long stints.',
   'Forza Horizon 6', 'ferrari_812sf',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.08,"Freq":85.0},"RoadBumps":{"Enabled":true,"Gain":0.55,"Freq":60.0},"RevLimiter":{"Enabled":true,"Gain":0.6}}}',
   '{engine,roadbumps,revlimiter}', 312, 8, 0.94, 1850, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- Forza Horizon 6 / Ferrari 812 Superfast -- popular alt
  ('Track Day Aggressive',
   'DriftKing',
   'Punchier engine pulse and stronger road bumps for short, intense sessions.',
   'Forza Horizon 6', 'ferrari_812sf',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.10}}}',
   '{engine}', 84, 6, 0.79, 412, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- Forza Horizon 6 / Ferrari 812 Superfast -- average
  ('Cruise Mode',
   'NightShift',
   'Dialed-back forces for relaxed cruising.',
   'Forza Horizon 6', 'ferrari_812sf',
   '{"override":{}}',
   '{}', 22, 3, 0.62, 76, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- Forza Horizon 6 / McLaren P1 -- popular
  ('Hybrid Punch',
   'RaceCraft42',
   'Captures the hybrid boost feel with a snappy engine pulse and pronounced gear shift.',
   'Forza Horizon 6', 'mclaren_p1',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.085},"GearShift":{"Enabled":true,"Gain":0.7}}}',
   '{engine,gearshift}', 68, 4, 0.78, 285, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- Forza Horizon 6 / McLaren P1 -- average
  ('Quiet Predator',
   'GearGremlin',
   'Subdued forces, focus on traction loss feedback.',
   'Forza Horizon 6', 'mclaren_p1',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.55}}}',
   '{tractionloss}', 18, 2, 0.58, 51, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- Forza Horizon 6 / Lamborghini Huracan -- popular
  ('V10 Symphony',
   'WheelmanRX',
   'Sharpened engine pulse tuned to the V10 firing order. Includes airborne ducking.',
   'Forza Horizon 6', 'lamborghini_huracan',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.09,"Freq":92.0},"Airborne":{"Enabled":true}}}',
   '{engine,airborne}', 92, 7, 0.81, 367, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- Forza Horizon 6 / Porsche 911 GT3 RS -- viral
  ('GT3 Apex Hunter',
   'ApexPredator',
   'Carefully balanced for GT3 cup-car feel. Road bumps tuned to the stiff suspension; engine pulse matches the flat-six.',
   'Forza Horizon 6', 'porsche_911_gt3rs',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.075,"Freq":78.0},"RoadBumps":{"Enabled":true,"Gain":0.65,"Freq":70.0},"GearShift":{"Enabled":true,"Gain":0.6},"AbsClick":{"Enabled":true,"Gain":0.5}}}',
   '{engine,roadbumps,gearshift,abs}', 258, 11, 0.92, 1240, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- Forza Horizon 6 / Ford Mustang GT500 -- average
  ('American Thunder',
   'GearGremlin',
   'Low-frequency engine rumble emphasis with a softer road feel.',
   'Forza Horizon 6', 'ford_mustang_gt500',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.082,"Freq":60.0}}}',
   '{engine}', 26, 4, 0.58, 89, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- Forza Horizon 6 / Ford Mustang GT500 -- quiet
  ('Drag Special',
   'DriftKing',
   'Stripped-back tune for drag passes.',
   'Forza Horizon 6', 'ford_mustang_gt500',
   '{"override":{}}',
   '{}', 3, 1, 0.15, 11, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- Forza Horizon 6 / Chevrolet Corvette C8 -- popular
  ('Mid-Engine Refined',
   'RaceCraft42',
   'Captures the mid-engine balance shift with paired collision + traction loss feedback.',
   'Forza Horizon 6', 'chevrolet_corvette_c8',
   '{"override":{"Collision":{"Enabled":true,"Gain":0.7},"TractionLoss":{"Enabled":true,"Gain":0.6}}}',
   '{collision,tractionloss}', 57, 5, 0.75, 198, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- Forza Horizon 6 / Chevrolet Corvette C8 -- quiet
  ('Stock Plus',
   'NightShift',
   'Just a touch firmer than stock. Good for newcomers.',
   'Forza Horizon 6', 'chevrolet_corvette_c8',
   '{"override":{}}',
   '{}', 4, 0, 0.18, 19, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- Assetto Corsa / Ferrari 488 GT3 -- viral
  ('Endurance Spec',
   'NightShift',
   'Built around long-distance endurance racing. Detailed road texture without fatigue.',
   'Assetto Corsa', 'ks_ferrari_488_gt3',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.072,"Freq":82.0},"RoadBumps":{"Enabled":true,"Gain":0.50,"Freq":65.0},"AbsClick":{"Enabled":true,"Gain":0.45},"PitLimiter":{"Enabled":true}}}',
   '{engine,roadbumps,abs,pitlimiter}', 287, 9, 0.93, 1620, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- AC / Ferrari 488 GT3 -- popular alt
  ('Sprint Specialist',
   'ApexPredator',
   'Stronger forces tuned for sprint races. Pronounced gear shift + traction loss.',
   'Assetto Corsa', 'ks_ferrari_488_gt3',
   '{"override":{"GearShift":{"Enabled":true,"Gain":0.78},"TractionLoss":{"Enabled":true,"Gain":0.7}}}',
   '{gearshift,tractionloss}', 72, 5, 0.79, 248, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- AC / Ferrari 488 GT3 -- average
  ('Calibration Baseline',
   'GearGremlin',
   'A neutral starting point you can build off of.',
   'Assetto Corsa', 'ks_ferrari_488_gt3',
   '{"override":{}}',
   '{}', 14, 2, 0.55, 42, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- AC / Porsche 911 GT3 R -- popular
  ('Flat-Six Detailed',
   'WheelmanRX',
   'Engine pulse matched to the flat-six firing order; emphasises the high-rev character.',
   'Assetto Corsa', 'ks_porsche_911_gt3_r',
   '{"override":{"EnginePulse":{"Enabled":true,"Gain":0.078,"Freq":86.0}}}',
   '{engine}', 61, 4, 0.77, 215, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  -- AC / Porsche 911 GT3 R -- average
  ('No-Frills Race',
   'DriftKing',
   'Minimal effects, just the essentials.',
   'Assetto Corsa', 'ks_porsche_911_gt3_r',
   '{"override":{}}',
   '{}', 12, 3, 0.51, 38, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),

  -- AC / BMW M3 E92 -- average
  ('Drift School',
   'DriftKing',
   'Tuned for drift practice; emphasises traction loss and abs feedback.',
   'Assetto Corsa', 'bmw_m3_e92',
   '{"override":{"TractionLoss":{"Enabled":true,"Gain":0.72},"AbsClick":{"Enabled":true,"Gain":0.5}}}',
   '{tractionloss,abs}', 28, 4, 0.61, 95, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),

  -- AC / Lotus Exos 125 -- quiet
  ('Open Wheel Light',
   'RaceCraft42',
   'Light forces; the chassis already provides plenty of feedback.',
   'Assetto Corsa', 'lotus_exos_125',
   '{"override":{}}',
   '{}', 2, 0, 0.12, 8, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1);

-- ===========================================================
-- GAME PRESETS
-- ===========================================================
-- 3 Universal (target_games = '{}')
-- 4 single-game
-- 3 multi-game
-- Spread across viral/popular/average/quiet tiers.

insert into public.game_presets
  (name, author, description, game, body, effect_tags,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version, target_games)
values
  -- Universal -- viral
  -- Schema note: game_presets.game has a NOT-NULL + length(1..64)
  -- check, so Universal rows can't use empty string. We park the
  -- sentinel 'universal' there; target_games = '{}' is what the
  -- client reads to render the "Universal" badge. Multi-game rows
  -- use their first target as the legacy game column value.
  ('Cinematic Heavy',
   'ApexPredator',
   'Strong forces, detailed road texture, full effect stack on. Best with a high-torque wheel.',
   'universal',
   '{"snapshot":{"MasterGain":1.1,"FfbScale":1.0,"EnginePulse":{"Enabled":true,"Gain":0.09},"RoadBumps":{"Enabled":true,"Gain":0.65}}}',
   '{engine,roadbumps,collision}', 245, 8, 0.93, 1410, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{}'),
  -- Universal -- popular
  ('Endurance Light',
   'NightShift',
   'Reduced fatigue tune. Lighter forces, longer sessions.',
   'universal',
   '{"snapshot":{"MasterGain":0.9,"FfbScale":0.85}}',
   '{}', 76, 4, 0.80, 312, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{}'),
  -- Universal -- average
  ('Beginner-Friendly',
   'WheelmanRX',
   'Safe defaults. Good for someone new to Trueforce who wants to sanity-check their wheel.',
   'universal',
   '{"snapshot":{"MasterGain":1.0,"FfbScale":1.0}}',
   '{}', 19, 2, 0.59, 64, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{}'),

  -- Single-game: Forza Horizon 6 -- popular
  ('Forza Arcade Plus',
   'DriftKing',
   'Tuned for Forza''s arcade physics. Stronger collision + airborne ducking.',
   'Forza Horizon 6',
   '{"snapshot":{"Collision":{"Enabled":true,"Gain":0.75},"Airborne":{"Enabled":true}}}',
   '{collision,airborne}', 88, 6, 0.79, 365, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"Forza Horizon 6"}'),
  -- Single-game: Forza Horizon 6 -- average
  ('FH6 Quick Start',
   'GearGremlin',
   'Ready-made tune to drop in. Edit from here.',
   'Forza Horizon 6',
   '{"snapshot":{}}',
   '{}', 21, 3, 0.59, 71, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"Forza Horizon 6"}'),

  -- Single-game: Assetto Corsa -- popular
  ('AC Sim Purist',
   'NightShift',
   'Restrained synthetics on top of AC''s native FFB. Lets the game''s own forces lead.',
   'Assetto Corsa',
   '{"snapshot":{"FfbScale":1.05,"EnginePulse":{"Enabled":true,"Gain":0.06}}}',
   '{engine}', 65, 5, 0.76, 234, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"Assetto Corsa"}'),
  -- Single-game: Assetto Corsa -- quiet
  ('AC Beginner Tune',
   'RaceCraft42',
   'Conservative defaults for new AC drivers.',
   'Assetto Corsa',
   '{"snapshot":{}}',
   '{}', 5, 1, 0.21, 24, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"Assetto Corsa"}'),

  -- Multi-game: Forza series -- viral
  ('Forza Combo',
   'ApexPredator',
   'One tune for both Forza Horizon games. Bumps + collision balanced for arcade feel.',
   'Forza Horizon 6',
   '{"snapshot":{"RoadBumps":{"Enabled":true,"Gain":0.7},"Collision":{"Enabled":true,"Gain":0.75}}}',
   '{roadbumps,collision}', 268, 7, 0.93, 1320, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"Forza Horizon 6","Forza Horizon 5"}'),
  -- Multi-game: Kunos AC + ACC -- popular
  ('Kunos Pack',
   'WheelmanRX',
   'Works equally well in Assetto Corsa and Competizione. Sim-friendly defaults.',
   'Assetto Corsa',
   '{"snapshot":{"FfbScale":1.0,"EnginePulse":{"Enabled":true,"Gain":0.07}}}',
   '{engine}', 71, 5, 0.78, 268, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1, '{"Assetto Corsa","Assetto Corsa Competizione"}'),
  -- Multi-game: cross-style FH6 + AC -- average
  ('Versatile Tune',
   'GearGremlin',
   'Compromise tune for switching between arcade and sim. Doesn''t excel at either but works in both.',
   'Forza Horizon 6',
   '{"snapshot":{}}',
   '{}', 24, 4, 0.58, 81, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1, '{"Forza Horizon 6","Assetto Corsa"}');

-- ===========================================================
-- CUSTOM ENGINES
-- ===========================================================
-- Bodies follow CustomEngineDef shape: name + a firing pattern stub.
-- Game-agnostic, so the dropdown surfaces them across any game.

insert into public.custom_engines
  (name, author, description, body,
   upvotes, downvotes, wilson_score, downloads,
   submitter_id, source_ip_hash, allow_in_packs, plugin_version, content_version)
values
  ('V8 American Muscle',
   'GearGremlin',
   'Cross-plane V8 firing order tuned for muscle car character.',
   '{"Name":"V8 American Muscle","CustomFiringPattern":"0:1.0, 0.125:0.8, 0.25:0.95, 0.375:0.85, 0.5:1.0, 0.625:0.8, 0.75:0.95, 0.875:0.85"}',
   158, 6, 0.86, 720, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('V12 Italian',
   'ApexPredator',
   'V12 firing pattern with a glassy high-rev character. Use with a high-RPM redline.',
   '{"Name":"V12 Italian","CustomFiringPattern":"0:1.0, 0.083:0.9, 0.167:1.0, 0.25:0.9, 0.333:1.0, 0.417:0.9, 0.5:1.0, 0.583:0.9, 0.667:1.0, 0.75:0.9, 0.833:1.0, 0.917:0.9"}',
   202, 9, 0.89, 920, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('Boxer-6 Flat',
   'WheelmanRX',
   'Porsche-style flat-six firing characteristic. Punchy mids.',
   '{"Name":"Boxer-6 Flat","CustomFiringPattern":"0:1.0, 0.167:0.9, 0.333:1.0, 0.5:0.9, 0.667:1.0, 0.833:0.9"}',
   83, 5, 0.80, 290, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('I4 Hot Hatch',
   'DriftKing',
   'Inline-4 hot-hatch character. Light, raspy.',
   '{"Name":"I4 Hot Hatch","CustomFiringPattern":"0:1.0, 0.25:0.9, 0.5:1.0, 0.75:0.9"}',
   24, 3, 0.59, 88, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1),
  ('Rotary Twin-Turbo',
   'NightShift',
   'Two-rotor wankel character. Smooth, high-frequency.',
   '{"Name":"Rotary Twin-Turbo","CustomFiringPattern":"0:0.9, 0.5:0.9"}',
   45, 4, 0.71, 165, 'mock-seed', 'mock-seed-ip', true, '0.1.24', 1),
  ('V10 Audi Snarl',
   'RaceCraft42',
   'V10 with a hard mid-range bite. Pairs well with the Lamborghini Huracan car preset.',
   '{"Name":"V10 Audi Snarl","CustomFiringPattern":"0:1.0, 0.1:0.9, 0.2:1.0, 0.3:0.9, 0.4:1.0, 0.5:0.9, 0.6:1.0, 0.7:0.9, 0.8:1.0, 0.9:0.9"}',
   3, 0, 0.17, 14, 'mock-seed', 'mock-seed-ip', false, '0.1.24', 1);

-- ===========================================================
-- PACKS
-- ===========================================================
-- A pack body bundles entries by kind. We use a CTE so the entry id
-- references reach into the live preset/engine ids we just inserted.

with
  -- IDs we'll need to template into the pack bodies.
  pkg_fh6 as (
    select id, name, game, car_id from public.presets
     where submitter_id = 'mock-seed' and game = 'Forza Horizon 6'
     order by wilson_score desc limit 4
  ),
  pkg_ac as (
    select id, name, game, car_id from public.presets
     where submitter_id = 'mock-seed' and game = 'Assetto Corsa'
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
  'Forza Horizon Starter Pack',
  'ApexPredator',
  'A curated set of the best community car presets for Forza Horizon 6, plus a universal game preset and a custom engine to tie them together.',
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
  'Community-tested car presets for the most popular AC GT3 cars. Includes the Endurance Spec preset tuned for long stints.',
  jsonb_build_object(
    'presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name, 'game', game, 'car_id', car_id)), '[]'::jsonb) from pkg_ac)
  ),
  (select count(*) from pkg_ac),
  74, 4, 0.79, 285, 'mock-seed', 'mock-seed-ip', '0.1.24', 1, '1.0.0'
union all
select
  'Multi-Game Universal Starter',
  'WheelmanRX',
  'A starter kit that works in any game. Universal game presets plus a couple of custom engines.',
  jsonb_build_object(
    'game_presets', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_universal_gp),
    'custom_engines', (select coalesce(jsonb_agg(jsonb_build_object('id', id, 'name', name)), '[]'::jsonb) from pkg_engines)
  ),
  (select count(*) from pkg_universal_gp) + (select count(*) from pkg_engines),
  31, 5, 0.61, 102, 'mock-seed', 'mock-seed-ip', '0.1.24', 1, '1.0.0';

commit;
