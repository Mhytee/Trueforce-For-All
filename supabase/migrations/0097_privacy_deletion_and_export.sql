-- 0097: privacy pass ahead of the privacy policy. Three fixes so the policy
-- can describe deletion and export truthfully:
--
-- 1. delete_my_account left three residues behind:
--    a. The user's cloud backup object (backups/<uid>/setup.json) was never
--       removed. The entitlements row cascades away with auth.users, so the
--       lapsed-supporter retention GC (which keys on entitlements) could
--       never see the user again: the backup was orphaned indefinitely.
--       Direct SQL deletion is blocked by Supabase's storage.protect_delete
--       trigger (see 0035), so deletion must go through the Storage REST
--       API. Fix: a backup_delete_queue table the RPC enqueues into and the
--       backup-retention-gc Edge Function drains on its schedule. The client
--       also best-effort deletes its own object at account-deletion time
--       (backups_delete_own RLS policy from 0033 permits it), so the queue
--       is the guaranteed backstop, not the only path.
--    b. source_ip_hash on the user's UPLOAD rows survived un-redacted (votes
--       and car-fact rows were redacted; uploads were merely orphaned).
--    c. A banned user's archived car-fact rows (0082 archive tables) kept
--       their uuid + hashed IP forever. With the account gone an unban
--       restore is impossible anyway, so the rows serve no purpose: delete.
--    d. The denormalized author column (the username snapshot stamped on
--       every upload at share time, 0008) survived deletion, so "your name
--       comes off them" was false: every read path kept serving the old
--       username. Null it; clients already render "(anonymous)" for null
--       author (0030).
--
-- 2. export_my_data returned far less than the UI's "everything we have on
--    your account": it omitted the account email, sign-in sessions, Discord
--    and Patreon links, entitlement/supporter status, download history,
--    reports filed, and moderation notices. Extended to cover all of them.
--
-- Grants: CREATE OR REPLACE preserves existing function ACLs (0026 anon
-- revoke stays). New queue RPCs are service-role only.

-- ---- 1. backup deletion queue -----------------------------------------------

create table if not exists public.backup_delete_queue (
    user_id   uuid primary key,          -- no FK: the auth.users row is gone
    queued_at timestamptz not null default now()
);
alter table public.backup_delete_queue enable row level security;
revoke all on public.backup_delete_queue from anon, authenticated, public;

create or replace function public.list_queued_backup_deletions()
returns table(user_id uuid)
language sql stable security definer set search_path = public, pg_temp as $$
  select q.user_id from public.backup_delete_queue q;
$$;
revoke all on function public.list_queued_backup_deletions() from public, anon, authenticated;
grant execute on function public.list_queued_backup_deletions() to service_role;

-- Clears queue rows the GC has confirmed deleted (or 404). Returns the count
-- removed. Per-id clearing keeps a failed Storage DELETE retryable next run.
create or replace function public.clear_backup_delete_queue(p_user_ids uuid[])
returns int
language sql security definer set search_path = public, pg_temp as $$
  with d as (
    delete from public.backup_delete_queue
     where user_id = any(p_user_ids)
     returning 1)
  select count(*)::int from d;
$$;
revoke all on function public.clear_backup_delete_queue(uuid[]) from public, anon, authenticated;
grant execute on function public.clear_backup_delete_queue(uuid[]) to service_role;

-- ---- 2. delete_my_account: queue backup + redact uploads + purge archives ---

create or replace function public.delete_my_account()
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_username text; v_upload_count int; v_anon text;
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

    delete from auth.users where id = v_uid;
    return jsonb_build_object(
        'deleted', true,
        'username_was', v_username,
        'uploads_orphaned', v_upload_count,
        'backup_queued', v_backup_queued);
end;
$$;

-- ---- 3. export_my_data: cover everything the account actually holds ---------

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
               from public.moderation_notices n where n.user_id = v_uid), '[]'::jsonb)
    );
    return v_result;
end;
$$;
