-- 0026: revoke anon + PUBLIC EXECUTE on the auth-gated RPCs.
-- These functions all reject anon already (raise 'sign-in required' /
-- 'Login required'): uploads, edits, deletes, votes, reports, downloads,
-- car-fact submit/vote, and the account RPCs (get_my_presets, set_username,
-- export_my_data, delete_my_account). The plugin only ever calls them with a
-- Bearer token, so removing anon/PUBLIC execute is a no-op for real users
-- while letting anon be rejected at the API permission layer instead of
-- inside the function body. `authenticated` keeps EXECUTE, so signed-in
-- owners can still edit and delete their own presets. The public-read RPCs
-- (get_*_by_ids, get_my_* vote lookups, get_account_stats,
-- check_username_available) intentionally keep anon and are left untouched.
do $$
declare r record;
begin
  for r in
    select p.oid::regprocedure as sig
    from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and p.prosecdef
      and (pg_get_functiondef(p.oid) ilike '%sign-in required%'
           or pg_get_functiondef(p.oid) ilike '%Login required%')
  loop
    execute format('revoke execute on function %s from anon, public', r.sig);
  end loop;
end $$;
