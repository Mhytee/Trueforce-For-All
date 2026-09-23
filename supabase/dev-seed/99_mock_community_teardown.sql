-- supabase/dev-seed/99_mock_community_teardown.sql
-- Remove every row inserted by 01_mock_community.sql. Safe to run any
-- time; mock content is identified by submitter_id = 'mock-seed'. Will
-- not touch any real user uploads.

begin;

delete from public.packs          where submitter_id = 'mock-seed';
delete from public.custom_engines where submitter_id = 'mock-seed';
delete from public.game_presets   where submitter_id = 'mock-seed';
delete from public.presets        where submitter_id = 'mock-seed';

commit;
