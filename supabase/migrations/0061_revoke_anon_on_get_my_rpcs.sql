-- 0061: revoke anon EXECUTE on the per-user get_my_* RPCs.
--
-- These are SECURITY DEFINER and filter by auth.uid(), so an anon caller (auth.uid()
-- null) already gets nothing back, but the anon grant was unintended and is flagged by
-- the Supabase security advisor (anon_security_definer_function_executable). The other
-- get_my_* functions (profile-less account RPCs: get_my_achievements, get_my_discord,
-- get_my_entitlement, get_my_patreon, get_my_presets, get_my_sessions) were already
-- anon=false; this brings the remaining five in line. Re-grant to the intended roles so
-- the change is explicit and idempotent.

revoke execute on function public.get_my_profile()                       from anon, public;
revoke execute on function public.get_my_votes(uuid[])                   from anon, public;
revoke execute on function public.get_my_game_votes(uuid[])             from anon, public;
revoke execute on function public.get_my_pack_votes(uuid[])             from anon, public;
revoke execute on function public.get_my_custom_engine_votes(uuid[])    from anon, public;

grant execute on function public.get_my_profile()                        to authenticated, service_role;
grant execute on function public.get_my_votes(uuid[])                    to authenticated, service_role;
grant execute on function public.get_my_game_votes(uuid[])              to authenticated, service_role;
grant execute on function public.get_my_pack_votes(uuid[])              to authenticated, service_role;
grant execute on function public.get_my_custom_engine_votes(uuid[])     to authenticated, service_role;
