-- 0107: close the default-privilege surface 0106 left open.
--
-- Same trap as 0034, 0091 and the 2026-06 audit: Supabase ships
--   alter default privileges in schema public
--     grant execute on functions to anon, authenticated;
--   alter default privileges in schema public
--     grant all on tables to anon, authenticated;
-- so a newly created function or table arrives with EXPLICIT role grants
-- already attached. `revoke ... from public` does not touch those, because
-- PUBLIC and a named role are different grantees. 0106 revoked only PUBLIC,
-- so both of its RPCs kept anon EXECUTE and the table kept authenticated
-- INSERT/UPDATE/DELETE.
--
-- (1) get_arcade_leaderboard is SECURITY DEFINER with no auth.uid() check of
--     its own: it was reachable at /rest/v1/rpc/get_arcade_leaderboard by any
--     anon caller, returning every board row including usernames. That is the
--     real hole here, and it directly contradicts 0106's stated intent that
--     the leaderboard is signed-in only.
--
-- (2) submit_arcade_lap_time was not exploitable (it returns 'sign-in
--     required' when auth.uid() is null) but it has no business being on the
--     anon REST surface at all.
revoke execute on function public.get_arcade_leaderboard(
    text, smallint, smallint, integer, integer) from anon, public;

revoke execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text) from anon, public;

-- (3) The table is RLS-enabled with a SELECT-only policy, so the stray write
--     privileges were already denied at the row level. Removing them anyway
--     keeps the grant table honest about the design: every write goes through
--     submit_arcade_lap_time, and nothing else may write.
revoke insert, update, delete, truncate, references
    on public.arcade_lap_times from authenticated;

-- anon holds no table grants (verified live before writing this), but state it
-- so a replay onto a differently-configured project cannot inherit any.
revoke all on public.arcade_lap_times from anon;
