-- 0144: reverting a run of translations, and blocking whoever sent it
-- (owner, 2026-11-04).
--
-- Nothing could undo a submission. The forward path never overwrites: it marks the
-- approved row superseded and inserts a new one, so every version of every string is
-- still here with its submitter, its time and its hashed address. The history was
-- complete and there was no way to walk back into it, which meant a defaced language
-- could only be repaired by hand, one key at a time, under exactly the time pressure
-- that makes hand repair a bad idea.
--
-- Scope is deliberately plural. An account is not the only thing to aim at, because
-- translating needs no account (0139), so a run can be named by its account, by its
-- anon id, by the hashed address it came from, or by a timestamp. The last one is what
-- you reach for mid-incident, when one person has used several ids and all you really
-- know is "everything in Russian since nine this morning".
--
-- A reverted row rests at 'withdrawn' rather than 'rejected'. Both exist, and
-- 'rejected' reads like a reviewer read it and judged it; withdrawing says only that
-- it is no longer serving, which is the true statement.
--
-- What goes back is the newest row for that key that nobody in scope wrote. Two cases
-- need saying out loud. If the same person hit a key more than once, their earlier
-- attempt is also theirs, so everything in scope is skipped rather than just the row
-- on top. And if they were the first person to translate a key, there is nothing to
-- put back: the key ends with no approved row and falls back to whatever ships in the
-- language file, or to English. In a language opened by one person that is the normal
-- case, not the exception, and the receipt counts it as orphaned so it is never
-- mistaken for a restore.
--
-- Everything in scope rests at withdrawn, not only what was serving. A row left at
-- superseded is still a candidate for promotion, and it would not be in scope the
-- next time somebody else is reverted on the same key, so one left behind is how a
-- second revert quietly promotes the first vandal's text back onto the wheel.
--
-- Blocking stays a separate decision, because a wrong translation is usually just a
-- wrong translation. p_block makes it one call when it is not, writing the forms that
-- were named into submitter_blocked, which already matches all three. The addresses
-- seen in the reverted rows come back in the receipt rather than being blocked
-- outright: an address can be a household or a whole campus, so widening a ban that
-- far is a second, deliberate call.
--
-- Owner only, the MOTD posture (0067): no anon or authenticated grant exists, so this
-- is reachable through the service_role key alone.
--
-- Dry run by default. It reports what it would do and writes nothing.
set lock_timeout = '3s';
set statement_timeout = '60s';

create or replace function public.revert_translations(
    p_culture      text,
    p_submitter    uuid        default null,
    p_anon_id      text        default null,
    p_ip_hash      text        default null,
    p_since        timestamptz default null,
    p_dry_run      boolean     default true,
    p_block        boolean     default false,
    p_banned_until timestamptz default null,
    p_reason       text        default null)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_anon     text;
    v_scope    uuid[];
    v_doomed   uuid[];
    v_restore  uuid[];
    v_reverted int;
    v_restored int;
    v_retired  int;
    v_blocked  int := 0;
    v_keys     jsonb;
    v_ips      jsonb;
begin
    if p_culture is null or btrim(p_culture) = '' then
        raise exception 'A culture is required.' using errcode = 'P0001';
    end if;
    if p_submitter is null and p_anon_id is null
       and p_ip_hash is null and p_since is null then
        raise exception 'Name a submitter, an anon id, an address hash or a since time. Reverting a whole language on no grounds at all is not something this offers.'
              using errcode = 'P0001';
    end if;
    -- The id is taken with or without the prefix the column stores it under, because
    -- the receipt that named it prints the stored form.
    if p_anon_id is not null and btrim(p_anon_id) <> '' then
        v_anon := case when btrim(p_anon_id) like 'anon:%' then btrim(p_anon_id)
                       else 'anon:' || btrim(p_anon_id) end;
    end if;

    with scope as (
        -- Everything in scope, whatever its status. This is what a replacement may
        -- not be, so an earlier attempt by the same hand is not mistaken for the
        -- good version.
        select t.id
          from public.translations t
         where t.culture = p_culture
           and (p_since is null or t.submitted_at >= p_since)
           and ((p_submitter is null and v_anon is null and p_ip_hash is null)
                or (p_submitter is not null and t.submitter = p_submitter)
                or (v_anon is not null and t.anon_id = v_anon)
                or (p_ip_hash is not null and t.source_ip_hash = p_ip_hash))
    ), doomed as (
        -- The rows actually serving right now.
        select t.id, t.key, t.english_sha256
          from public.translations t
         where t.id in (select id from scope)
           and t.status = 'approved'
    ), restore as (
        -- Per key and per English version, the newest superseded row from outside
        -- the scope. One row only: the unique index allows one approved row per
        -- (culture, key, english_sha256), which is the same grouping the forward
        -- path supersedes within.
        select distinct on (d.key, d.english_sha256) t.id
          from doomed d
          join public.translations t
            on t.culture = p_culture
           and t.key = d.key
           and t.english_sha256 = d.english_sha256
           and t.status = 'superseded'
           and t.id not in (select id from scope)
         order by d.key, d.english_sha256, t.updated_at desc, t.submitted_at desc
    )
    select coalesce((select array_agg(id) from scope), '{}'::uuid[]),
           coalesce((select array_agg(id) from doomed), '{}'::uuid[]),
           coalesce((select array_agg(id) from restore), '{}'::uuid[])
      into v_scope, v_doomed, v_restore;

    v_reverted := coalesce(array_length(v_doomed, 1), 0);
    v_restored := coalesce(array_length(v_restore, 1), 0);
    v_retired  := coalesce(array_length(v_scope, 1), 0);

    select coalesce(jsonb_agg(s.k order by s.k), '[]'::jsonb) into v_keys
      from (select distinct t.key as k from public.translations t
             where t.id = any(v_doomed) order by 1 limit 25) s;
    select coalesce(jsonb_agg(distinct t.source_ip_hash), '[]'::jsonb) into v_ips
      from public.translations t
     where t.id = any(v_doomed) and t.source_ip_hash is not null
       and t.source_ip_hash <> 'redacted';

    if not p_dry_run then
        -- Every row in scope goes to withdrawn, not only the ones that were serving.
        -- A row left at superseded is still a candidate for promotion, and it would
        -- not be in scope the next time someone else is reverted on the same key, so
        -- leaving one behind is how a second revert quietly promotes the first
        -- vandal's text. withdrawn is never promoted, so the pool stays clean.
        --
        -- Demoted before promoted: one approved row per key per English version is a
        -- unique index, so the other order collides with itself.
        update public.translations
           set status = 'withdrawn',
               review_note = left(coalesce(p_reason, 'reverted'), 500)
         where id = any(v_scope) and status <> 'withdrawn';
        update public.translations
           set status = 'approved'
         where id = any(v_restore);

        if p_block then
            insert into public.submitter_blocked (submitter_id, reason, banned_until)
            select s.who, left(coalesce(p_reason, 'translation vandalism'), 500),
                   p_banned_until
              from (select p_submitter::text as who where p_submitter is not null
                    union all select v_anon where v_anon is not null
                    union all select 'ip:' || p_ip_hash where p_ip_hash is not null) s
                on conflict (submitter_id) do update
                   set reason = excluded.reason,
                       banned_until = excluded.banned_until;
            get diagnostics v_blocked = row_count;
        end if;
    end if;

    return jsonb_build_object(
        'ok', true,
        'dry_run', p_dry_run,
        'culture', p_culture,
        -- Rows that were serving and are not any more.
        'reverted', v_reverted,
        -- Every row in scope, serving or not, now resting at withdrawn.
        'retired', v_retired,
        'restored', v_restored,
        -- Keys with nothing to put back. They fall back to the shipped file or English.
        'orphaned', v_reverted - v_restored,
        'blocked', v_blocked,
        'keys', v_keys,
        'address_hashes', v_ips);
end $$;
revoke all on function public.revert_translations(
    text, uuid, text, text, timestamptz, boolean, boolean, timestamptz, text)
    from public, anon, authenticated;
grant execute on function public.revert_translations(
    text, uuid, text, text, timestamptz, boolean, boolean, timestamptz, text)
    to service_role;
