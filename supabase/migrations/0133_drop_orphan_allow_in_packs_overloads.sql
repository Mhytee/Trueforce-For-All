-- 0133: drop the three orphaned pre-allow_in_packs RPC overloads.
--
-- Same mechanism as 0095. 0015_allow_in_packs added p_allow_in_packs to
-- upload_preset, upload_custom_engine and update_custom_engine using
-- CREATE OR REPLACE. In Postgres a changed argument list creates a NEW
-- overload rather than replacing the old one, so each pre-change function
-- was left orphaned beside its replacement.
--
-- UNLIKE 0095, THIS IS HYGIENE AND NOT A FIX. Nothing is broken today and
-- no row was ever written wrong. Recording why, so nobody re-audits this
-- from scratch:
--
--   * The orphans were kept current by hand. 0024_rate_limits_per_user
--     re-created both upload arities on purpose, and 0094 carries explicit
--     "upload_preset (9-arg)" and "upload_preset (10-arg)" sections. So the
--     drift that made 0095 an actual incident never happened here: both
--     upload_preset bodies carry the same 14-tag whitelist, including
--     axleslip, kerbthump and lockupjudder.
--   * Diffing pg_get_functiondef across each pair, the ONLY semantic
--     difference is the allow_in_packs write itself. Every auth gate,
--     ownership check, suppression filter, rate-limit CTE and validation
--     bound is identical, as is SECURITY DEFINER and search_path. The
--     column already defaults to false, which is exactly what the newer
--     bodies coalesce a null parameter to, so a stale-bound call would
--     have produced an identical row.
--   * They were never reachable anyway. The plugin serializes RPC bodies
--     with NullValueHandling.Ignore (PresetSharingClient.cs:185), which
--     drops only nulls, and DefaultValueHandling is not set, so a literal
--     false is still sent. UpdateCustomEngineWithVersionAsync does declare
--     bool? allowInPacks = null, but its only caller passes a plain bool:
--     PresetShareWindow.cs:622 computes
--         bool allowPacks = allowInPacksCheck?.IsChecked == true;
--     where "== true" collapses the nullable, so null is not representable.
--     p_allow_in_packs is therefore always present and PostgREST always
--     exact-matches the newer overload. Checked across every release tag:
--     PresetSharingClient.cs does not exist before v0.2.0, and from v0.2.0
--     through v0.4.0 that line is unchanged, so no shipped build can send
--     a stale-shaped body.
--
-- THE REASON TO DROP THEM is that every future edit to these RPCs has to be
-- authored twice, and that discipline has already slipped once: 0094 patched
-- only update_preset's 7-arg form, leaving the 6-arg one to silently strip
-- three effect tags until 0095 removed it. Removing the duplicates removes
-- the chance of repeating that.
--
-- Safety, to the standard 0095 set: nothing outside PostgREST calls these.
-- Verified before dropping, each check returning nothing: Edge Functions
-- under supabase/functions, other database functions via pg_proc.prosrc,
-- the 14 cron.job commands, non-auto pg_depend entries on the three oids,
-- and a repo-wide search that finds the RPC paths only at
-- PresetSharingClient.cs lines 139, 160 and 161.
--
-- A drop cannot be undone from the catalog once applied, so the three bodies
-- were captured first and live outside the repo at
-- supabase-history-2026-09-24/dropped-functions-0133/.
--
-- Idempotent: DROP ... IF EXISTS against each exact stale signature. The
-- surviving overloads differ in arity (they take the trailing boolean) and
-- are untouched.

drop function if exists public.upload_preset(
    text, text, text, jsonb, text, text, text[], integer, text);

drop function if exists public.upload_custom_engine(
    text, jsonb, text, integer, text);

drop function if exists public.update_custom_engine(
    uuid, text, text, jsonb, integer);
