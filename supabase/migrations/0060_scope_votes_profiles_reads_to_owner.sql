-- 0060: scope community vote-row and profile reads to the caller's own rows.
--
-- Before this, the SELECT policies on the four *_votes tables and on profiles were
-- USING (true) for the authenticated role, letting any signed-in user enumerate every
-- account UUID, username, signup date, and per-voter vote (the UUID->vote->author
-- linkage that the 2026-06-14 audit flagged as AP-3). The client never needs other
-- users' raw rows:
--   * vote COUNTS come from the denormalized upvotes / downvotes / wilson_score columns
--     on the parent tables (presets, game_presets, packs, custom_engines),
--   * "did I vote" comes from the get_my_*_votes SECURITY DEFINER RPCs,
--   * author display uses the denormalized `author` column on each preset row.
-- Writes stay on the SECURITY DEFINER vote RPCs (vote_preset etc.), which bypass RLS,
-- so tightening SELECT does not affect voting. Only SELECT policies exist on these
-- tables today, so this migration changes nothing else.

alter policy "preset_votes_select_authenticated"        on public.preset_votes
  using (voter_id = auth.uid()::text);
alter policy "game_preset_votes_select_authenticated"   on public.game_preset_votes
  using (voter_id = auth.uid()::text);
alter policy "pack_votes_select_authenticated"          on public.pack_votes
  using (voter_id = auth.uid()::text);
alter policy "custom_engine_votes_select_authenticated" on public.custom_engine_votes
  using (voter_id = auth.uid()::text);

-- profiles.id is the account uuid; the client reads only its own row (get_my_profile is
-- SECURITY DEFINER and unaffected), and author names are denormalized on presets, so
-- own-row visibility is sufficient and removes the userbase-enumeration surface.
alter policy "profiles_select_authenticated" on public.profiles
  using (id = auth.uid());
