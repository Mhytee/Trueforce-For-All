-- 0023: shrink the abuse surface.
--
-- (1) Two internal car-fact helpers silently regained PUBLIC execute when
--     their signatures changed: the revoke in 0001 targeted the old 3-arg
--     _recompute_car_fact_consensus, but 0002 redefined it with a 4th
--     p_variant_signature arg (a new function that defaults to PUBLIC
--     EXECUTE), and _gc_superseded_car_fact_submissions (added in 0004)
--     was never revoked at all. Both run full-table scans, so leaving them
--     anon-callable is a free, unauthenticated compute/DoS lever. Re-revoke.
--     submit_car_fact / vote_car_fact still invoke them internally as
--     SECURITY DEFINER, so legitimate callers are unaffected.
revoke execute on function public._recompute_car_fact_consensus(text, text, text, text)
    from anon, authenticated, public;
revoke execute on function public._gc_superseded_car_fact_submissions()
    from anon, authenticated, public;

-- (3) profiles enumeration: the public-select policy was open to the anon
--     (logged-out, whole-internet) role, exposing every
--     username -> auth user UUID -> created_at mapping. The plugin never
--     reads profiles over REST (author is denormalised onto each content
--     row; username operations go through SECURITY DEFINER RPCs such as
--     check_username_available / get_my_profile), so require a signed-in
--     session to browse the table directly. Those RPCs keep working because
--     a SECURITY DEFINER function bypasses RLS.
drop policy if exists profiles_select_public on public.profiles;
create policy profiles_select_authenticated
    on public.profiles for select to authenticated using (true);
grant select on public.profiles to authenticated;
revoke select on public.profiles from anon;
