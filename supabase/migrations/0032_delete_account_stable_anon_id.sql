-- 0032: stable de-identified id when a user deletes their account.
--
-- delete_my_account preserves a deleting user's votes/submissions (so the
-- consensus they helped build survives) and swaps their attribution to an
-- opaque 'deleted:<random>' placeholder. The placeholder was built inline in
-- each UPDATE's SET clause with gen_random_bytes(), which is VOLATILE and so
-- gets evaluated once PER ROW. A user with more than one not-yet-GC'd row for
-- the same (game, car_id, fact_type, variant_signature) therefore had each
-- row anonymized to a DIFFERENT id, splitting one person's single endorsement
-- into several distinct supporters. Because the ids now differ,
-- _gc_superseded_car_fact_submissions (which keys on submitter_id) no longer
-- recognizes them as the same submitter's duplicates and never collapses
-- them, so the inflated distinct-supporter count becomes permanent. The same
-- split applies to the per-vote tables' distinct-voter tallies.
--
-- Fix: generate ONE token per call into a variable and reuse it across every
-- table, so a former user stays a single identity everywhere. The recompute's
-- count(distinct submitter_id) and the vote tallies then stay accurate, and
-- the GC can still collapse that identity's duplicates to its latest row.
--
-- Forward-only: deletions that already split cannot be merged back (the
-- account link is gone). Expected to be negligible: the weekly GC normally
-- collapses a user's duplicates to their latest before a deletion can split
-- them. Only the function body changes; signature and grants are unchanged,
-- so the existing anon/PUBLIC revoke from 0026 is preserved by CREATE OR
-- REPLACE.

create or replace function public.delete_my_account()
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_username text; v_upload_count int; v_anon text;
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

    delete from auth.users where id = v_uid;
    return jsonb_build_object(
        'deleted', true,
        'username_was', v_username,
        'uploads_orphaned', v_upload_count);
end;
$$;
