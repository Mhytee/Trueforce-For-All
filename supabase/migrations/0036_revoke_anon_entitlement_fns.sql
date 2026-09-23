-- 0036: belt-and-braces. Revoke anon EXECUTE on the entitlement read helpers. They
-- already return nothing for anon (auth.uid() is null), but Supabase's default
-- privileges grant EXECUTE to anon on new public functions, so `revoke from public`
-- in 0034 didn't remove the explicit anon grant. Revoking matches the project's
-- auth-gated convention (cf. 0026) and clears the security-advisor warning.
revoke all on function public.is_active_supporter() from anon;
revoke all on function public.get_my_entitlement() from anon;
