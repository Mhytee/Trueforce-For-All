-- 0115: put arcade lap times in the data export.
--
-- PRIVACY.md promises the export contains "uploads, votes, submissions". Arcade times are all
-- three: submitted by the account, stored against it, and published under its username. They shipped
-- in 0106 without being added here, so the export has been quietly incomplete since.
--
-- Deletion was already correct (arcade_lap_times.user_id is ON DELETE CASCADE, verified against a
-- real row), which is why this went unnoticed: the loud half of the privacy contract worked.
--
-- The whole function is restated because that is what create-or-replace requires. Everything from
-- 0097 is byte-identical, generated from that file rather than retyped, and the only change is the
-- two keys at the end.
--
-- ON EVENT IDENTITY: actor_name and victim_name are KEPT, unlike the moderator fields stripped from
-- reports_filed above them. A moderator's identity is private to the moderation system, while a
-- leaderboard name is published to every player's in-game board and to the weekly digest, so it is
-- already known to the exporting user. The raw uuids are stripped: another person's internal
-- identifier is not this user's data, and 0113 nulls their name when their account goes.

create or replace function public.export_my_data()
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_result jsonb;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_result := jsonb_build_object(
        'exported_at', now(),
        'user_id', v_uid,
        'email', (select u.email from auth.users u where u.id = v_uid),
        'account_created_at', (select u.created_at from auth.users u where u.id = v_uid),
        'profile', (select to_jsonb(p) from public.profiles p where p.id = v_uid),
        'entitlement', (select to_jsonb(e) from public.entitlements e where e.user_id = v_uid),
        'discord_link', (select to_jsonb(d) from public.discord_links d where d.user_id = v_uid),
        'patreon_link', (select to_jsonb(pl) from public.patreon_links pl where pl.user_id = v_uid),
        'sessions', coalesce(
            (select jsonb_agg(to_jsonb(s)) from public.get_my_sessions() s), '[]'::jsonb),
        -- The anti-abuse device code, per session. Deliberately not part of
        -- get_my_sessions (0081 keeps it out of the Account-tab list), but an
        -- export claiming completeness must include it. Zero anti-abuse cost:
        -- the plugin is open source, so a user can already derive their own
        -- code from DeviceFingerprint.Compute.
        'session_device_codes', coalesce(
            (select jsonb_agg(jsonb_build_object('session_id', m.session_id, 'device_fp', m.device_fp))
               from public.session_metadata m
              where m.user_id = v_uid and m.device_fp is not null), '[]'::jsonb),
        'presets', coalesce(
            (select jsonb_agg(to_jsonb(pr)) from public.presets pr where pr.owner_user_id = v_uid), '[]'::jsonb),
        'game_presets', coalesce(
            (select jsonb_agg(to_jsonb(gp)) from public.game_presets gp where gp.owner_user_id = v_uid), '[]'::jsonb),
        'custom_engines', coalesce(
            (select jsonb_agg(to_jsonb(ce)) from public.custom_engines ce where ce.owner_user_id = v_uid), '[]'::jsonb),
        'packs', coalesce(
            (select jsonb_agg(to_jsonb(pk)) from public.packs pk where pk.owner_user_id = v_uid), '[]'::jsonb),
        'votes_cast_on_presets', coalesce(
            (select jsonb_agg(to_jsonb(pv)) from public.preset_votes pv where pv.voter_id = v_uid::text), '[]'::jsonb),
        'votes_cast_on_game_presets', coalesce(
            (select jsonb_agg(to_jsonb(gv)) from public.game_preset_votes gv where gv.voter_id = v_uid::text), '[]'::jsonb),
        'votes_cast_on_custom_engines', coalesce(
            (select jsonb_agg(to_jsonb(cv)) from public.custom_engine_votes cv where cv.voter_id = v_uid::text), '[]'::jsonb),
        'votes_cast_on_packs', coalesce(
            (select jsonb_agg(to_jsonb(pv)) from public.pack_votes pv where pv.voter_id = v_uid::text), '[]'::jsonb),
        'car_fact_submissions', coalesce(
            (select jsonb_agg(to_jsonb(s)) from car_fact_submissions s where s.submitter_id = v_uid::text), '[]'::jsonb),
        'car_fact_votes', coalesce(
            (select jsonb_agg(to_jsonb(v)) from car_fact_consensus_votes v where v.voter_id = v_uid::text), '[]'::jsonb),
        'downloads_of_presets', coalesce(
            (select jsonb_agg(to_jsonb(d)) from public.preset_user_downloads d where d.user_id = v_uid), '[]'::jsonb),
        'downloads_of_game_presets', coalesce(
            (select jsonb_agg(to_jsonb(d)) from public.game_preset_user_downloads d where d.user_id = v_uid), '[]'::jsonb),
        'downloads_of_custom_engines', coalesce(
            (select jsonb_agg(to_jsonb(d)) from public.custom_engine_user_downloads d where d.user_id = v_uid), '[]'::jsonb),
        'downloads_of_packs', coalesce(
            (select jsonb_agg(to_jsonb(d)) from public.pack_user_downloads d where d.user_id = v_uid), '[]'::jsonb),
        -- Moderator identity (resolved_by / created_by carry the actioning
        -- mod's Discord display name + id) and internal Discord routing ids
        -- are NOT the exporting user's data; strip them.
        'reports_filed', coalesce(
            (select jsonb_agg(to_jsonb(r) - 'resolved_by' - 'discord_message_id' - 'discord_channel_id')
               from public.report_flags r where r.reporter_id = v_uid), '[]'::jsonb),
        'moderation_notices', coalesce(
            (select jsonb_agg(to_jsonb(n) - 'created_by' - 'resolved_by' - 'discord_message_id' - 'discord_channel_id')
               from public.moderation_notices n where n.user_id = v_uid), '[]'::jsonb),
        'arcade_lap_times', coalesce(
            (select jsonb_agg(to_jsonb(t)) from public.arcade_lap_times t where t.user_id = v_uid), '[]'::jsonb),
        'arcade_events', coalesce(
            (select jsonb_agg(to_jsonb(e) - 'actor_user_id' - 'victim_user_id')
               from public.arcade_events e
              where e.actor_user_id = v_uid or e.victim_user_id = v_uid), '[]'::jsonb)
    );
    return v_result;
end;
$$;

-- create-or-replace keeps the existing ACL, so these are belt and braces rather than required. They
-- cost nothing and mean the file states the intended grant instead of relying on 0097 still being
-- read alongside it.
revoke all on function public.export_my_data() from anon, public;
grant execute on function public.export_my_data() to authenticated;
