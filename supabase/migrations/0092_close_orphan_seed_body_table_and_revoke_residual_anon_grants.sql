-- Security fix from the 2026-06-28 anon-access audit.
-- Applied to prod via MCP the same day; this file keeps the repo in sync (the
-- root cause here was a table created by an out-of-band migration that never
-- landed in this folder). Browse stays account-required per owner; no loosening.

-- 1) The real hole: public.packs_body_backup_seed_embed was created out-of-band
--    (migration 20260627030436_embed_seed_pack_bodies, not in this folder), with
--    RLS DISABLED and full anon DML, holding seed-pack body content. Anyone with
--    the shipped anon key could read AND tamper/delete the rows. Lock it down
--    without dropping (data preserved; service_role keeps access). RLS-on with no
--    permissive policy is default-deny; revoking the grants is belt-and-suspenders.
alter table if exists public.packs_body_backup_seed_embed enable row level security;
revoke all on public.packs_body_backup_seed_embed from anon, authenticated;

-- 2) Defense-in-depth: residual anon table-level grants that are inert today
--    (no permissive anon RLS policy) but a footgun if a permissive policy is ever
--    added. Strip the anon grants; authenticated/RLS behaviour is unchanged.
revoke all on public.car_fact_consensus from anon;
revoke all on public.preset_user_downloads from anon;
revoke all on public.pack_user_downloads from anon;
revoke all on public.game_preset_user_downloads from anon;
revoke all on public.custom_engine_user_downloads from anon;
