-- 0027: require a signed-in account to READ community data.
--
-- Community sharing is unreleased (not on main, not in any tag), so there is
-- no installed base reading with the anon key. We flip every community-read
-- SELECT policy from anon/public to `authenticated`, drop the anon SELECT
-- grants, and make the get_*_by_ids read RPCs authenticated-only. The plugin
-- now sends the user's bearer on every read (browse already requires sign-in
-- in-app). Writes were already auth-gated. This closes anonymous scraping and
-- stops a third-party client from tapping the data with the public anon key.

-- ---- content tables: public-read flips anon -> authenticated ----
drop policy if exists presets_select_public on public.presets;
create policy presets_select_authenticated on public.presets
    for select to authenticated using (not is_suppressed);

drop policy if exists game_presets_select_public on public.game_presets;
create policy game_presets_select_authenticated on public.game_presets
    for select to authenticated using (not is_suppressed);

drop policy if exists custom_engines_select_public on public.custom_engines;
create policy custom_engines_select_authenticated on public.custom_engines
    for select to authenticated using (not is_suppressed);

drop policy if exists packs_select_public on public.packs;
create policy packs_select_authenticated on public.packs
    for select to authenticated using (not is_suppressed);

-- ---- vote tallies ----
drop policy if exists preset_votes_select_public on public.preset_votes;
create policy preset_votes_select_authenticated on public.preset_votes
    for select to authenticated using (true);

drop policy if exists game_preset_votes_select_public on public.game_preset_votes;
create policy game_preset_votes_select_authenticated on public.game_preset_votes
    for select to authenticated using (true);

drop policy if exists custom_engine_votes_select_public on public.custom_engine_votes;
create policy custom_engine_votes_select_authenticated on public.custom_engine_votes
    for select to authenticated using (true);

drop policy if exists pack_votes_select_public on public.pack_votes;
create policy pack_votes_select_authenticated on public.pack_votes
    for select to authenticated using (true);

-- ---- car-fact consensus ----
drop policy if exists "Anyone can read car_fact_consensus" on public.car_fact_consensus;
create policy car_fact_consensus_select_authenticated on public.car_fact_consensus
    for select to authenticated using (true);

-- ---- table grants: drop anon SELECT, keep authenticated (belt + braces) ----
grant select on
    public.presets, public.game_presets, public.custom_engines, public.packs,
    public.preset_votes, public.game_preset_votes, public.custom_engine_votes,
    public.pack_votes, public.car_fact_consensus
  to authenticated;

revoke select on
    public.presets, public.game_presets, public.custom_engines, public.packs,
    public.preset_votes, public.game_preset_votes, public.custom_engine_votes,
    public.pack_votes, public.car_fact_consensus
  from anon;

-- ---- read RPCs: authenticated only ----
revoke execute on function public.get_presets_by_ids(uuid[])        from anon, public;
revoke execute on function public.get_game_presets_by_ids(uuid[])   from anon, public;
revoke execute on function public.get_custom_engines_by_ids(uuid[]) from anon, public;
revoke execute on function public.get_packs_by_ids(uuid[])          from anon, public;
