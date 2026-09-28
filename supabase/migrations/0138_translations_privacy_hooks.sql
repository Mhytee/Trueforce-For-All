-- 0138: what an account deletion does to translations, and what an export carries
-- (docs/localization-translation-service.md section 10.6). The design doc calls this
-- 0136; it is 0138 because the schema and the RPC contract took 0136 and 0137.
--
-- Owner, 2026-09-28: deleting an account must NOT remove the person's translation
-- contributions. It takes the name off them and nothing else. That is the rule the
-- presets already follow, and it is now the rule here, which also means this file no
-- longer deletes a deleted account's non-approved rows the way the design planned:
-- a row that helped is a contribution whether or not it is the one serving today.
--
-- Both functions are replaced whole, from their live definitions rather than from the
-- migrations that wrote them, so nothing changed in between is undone. The only
-- differences from live are the blocks this file's comments name.
set lock_timeout = '3s';
set statement_timeout = '60s';

CREATE OR REPLACE FUNCTION public.delete_my_account()
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
declare v_uid uuid; v_username text; v_upload_count int; v_anon text;
        v_translations int := 0;
        v_backup_queued boolean := false;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    -- One stable token for this former user, reused across every table below
    -- so a single person never splits into multiple distinct submitter/voter
    -- ids (which would inflate consensus distinct-counts and defeat the GC).
    v_anon := 'deleted:' || encode(gen_random_bytes(16), 'hex');

    select username into v_username from public.profiles where id = v_uid;
    select
        (select count(*) from public.presets        where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.game_presets   where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.custom_engines where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.packs          where owner_user_id = v_uid and not is_suppressed)
      into v_upload_count;

    update public.preset_votes
        set voter_id = v_anon, source_ip_hash = 'redacted' where voter_id = v_uid::text;
    update public.game_preset_votes
        set voter_id = v_anon, source_ip_hash = 'redacted' where voter_id = v_uid::text;
    update public.custom_engine_votes
        set voter_id = v_anon, source_ip_hash = 'redacted' where voter_id = v_uid::text;
    update public.pack_votes
        set voter_id = v_anon, source_ip_hash = 'redacted' where voter_id = v_uid::text;
    update public.car_fact_consensus_votes
        set voter_id = v_anon, source_ip_hash = 'redacted' where voter_id = v_uid::text;
    update public.car_fact_submissions
        set submitter_id = v_anon, source_ip_hash = 'redacted' where submitter_id = v_uid::text;

    -- Uploads stay published (orphaned via the FK's SET NULL), but the
    -- author snapshot and hashed submission IPs go with the account.
    -- Must run BEFORE the auth.users delete nulls owner_user_id. The
    -- profanity triggers on these tables are column-scoped to
    -- name/description, so these updates don't fire them; clients render
    -- "(anonymous)" for a null author (0030).
    update public.presets        set source_ip_hash = 'redacted', author = null where owner_user_id = v_uid;
    update public.game_presets   set source_ip_hash = 'redacted', author = null where owner_user_id = v_uid;
    update public.custom_engines set source_ip_hash = 'redacted', author = null where owner_user_id = v_uid;
    update public.packs          set source_ip_hash = 'redacted', author = null where owner_user_id = v_uid;

    -- Ban-archive rows exist only so an unban can restore them; with the
    -- account gone a restore is impossible, so the copies (uuid + hashed IP)
    -- have no remaining purpose.
    delete from public.car_fact_submissions_archive     where archived_user = v_uid;
    delete from public.car_fact_consensus_votes_archive where archived_user = v_uid;

    -- Queue the cloud backup object for deletion by the retention GC. The
    -- storage.protect_delete trigger blocks SQL deletes, and the entitlements
    -- row (which the retention GC keys on) cascades away with auth.users, so
    -- without this the object would be orphaned forever. Unconditional: a
    -- backup upload in flight can commit its storage.objects row after any
    -- existence check here (READ COMMITTED gives each statement its own
    -- snapshot), and once auth.users is gone no GC path can ever see the
    -- user again. A queue row with no object self-cleans: the GC counts 404
    -- as done and clears the row on its next run. The EXISTS feeds only the
    -- informational return value.
    insert into public.backup_delete_queue(user_id) values (v_uid)
        on conflict (user_id) do nothing;
    v_backup_queued := exists (select 1 from storage.objects
                where bucket_id = 'backups' and name = v_uid::text || '/setup.json');

    -- Translations stay, and only the name comes off them. The submitter column
    -- nulls itself when auth.users goes (the FK is on delete set null), which is
    -- what leaves the text in place with no author; this clears the other handle
    -- on the person, the hashed address, and counts what stayed so the dialog can
    -- say so. Nothing is deleted, not even a pending or rejected row: a row that
    -- helped is a contribution whether or not it is the one serving today, and
    -- translation_contributors already collapses a nameless submitter into
    -- (anonymous).
    select count(*) into v_translations from public.translations where submitter = v_uid;
    update public.translations set source_ip_hash = 'redacted' where submitter = v_uid;

    delete from auth.users where id = v_uid;
    return jsonb_build_object(
        'deleted', true,
        'username_was', v_username,
        'uploads_orphaned', v_upload_count,
        'backup_queued', v_backup_queued,
        'translations_kept', v_translations);
end;
$function$;

CREATE OR REPLACE FUNCTION public.export_my_data()
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
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
        -- Their translations, because these outlive the account: text, the
        -- English it was written for, where it was sent from and what became
        -- of it. No other submitter's rows, and no addresses.
        'translations', coalesce((select jsonb_agg(jsonb_build_object(
                'culture', t.culture, 'key', t.key, 'text', t.text,
                'english_sha256', t.english_sha256, 'status', t.status,
                'source', t.source, 'submitted_at', t.submitted_at,
                'shipped_version', t.shipped_version)
                order by t.culture, t.key)
            from public.translations t where t.submitter = v_uid), '[]'::jsonb),
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
$function$;
