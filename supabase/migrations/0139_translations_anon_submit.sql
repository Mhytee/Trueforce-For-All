-- 0139: translating needs no account (owner, 2026-09-28).
--
-- The design required a signed-in submitter. It should not: the person who notices a
-- bad Spanish label is not the person who wanted an account, and an account was never
-- the thing protecting this anyway. Email OTP with create_user = true makes one cost a
-- disposable address, which is why section 6's caps already key three of themselves on
-- something other than the account.
--
-- This follows submit_car_fact (0100) rather than inventing a second scheme: the
-- account wins when a request carries a token, else a client-minted id arrives as
-- p_anon_id and is stored as 'anon:<id>', and the hashed address is the backstop
-- because a self-chosen id costs nothing. submitter_blocked already matches all three
-- forms. The plugin sends the same CarFactsAnonId it already mints and already carries
-- in a backup, so one person stays one contributor across their machines.
--
-- What an account still buys: a name in the credits. An anonymous row collapses into
-- (anonymous) in translation_contributors, which is where it belongs.
set lock_timeout = '3s';
set statement_timeout = '60s';

-- The anonymous identity, beside the account one. Not in submitter, which is a uuid
-- with a foreign key: an anonymous submitter has no auth.users row to point at.
alter table public.translations
    add column if not exists anon_id text
    check (anon_id is null or anon_id ~ '^anon:[A-Za-z0-9-]{8,64}$');

-- The per-person hourly cap has to find both kinds of person.
create index if not exists translations_anon_idx
    on public.translations (anon_id, submitted_at desc) where anon_id is not null;

-- ---------------------------------------------------------------------------
-- The row writer gains the anonymous identity. Everything else about it is 0137's:
-- the English must exist, the hash must name an English this key has had, the text is
-- normalized, checked and measured, and the row supersedes the one it replaces.
create or replace function public._submit_translation_row(
    p_culture text, p_uid uuid, p_anon text, p_source text, p_plugin_version text,
    p_ip_hash text, p_key text, p_text text, p_hash text, p_note text)
returns text language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_english      text;
    v_english_hash text;
    v_text         text;
    v_max          int;
    v_approved     text;
    v_ph_en        text[];
    v_ph_tr        text[];
begin
    select e.text, e.sha256 into v_english, v_english_hash
      from public.english_strings e
     where e.key = p_key and e.retired_at is null;
    if v_english is null then
        raise exception 'This key is not in the English the server holds. Reload and try again.'
              using errcode = 'P0001';
    end if;
    if p_hash <> v_english_hash
       and not exists (select 1 from public.english_strings_history h
                        where h.key = p_key and h.sha256 = p_hash) then
        raise exception 'The English for this key moved. Reload the page and translate the current text.'
              using errcode = 'P0001';
    end if;
    v_text := replace(replace(p_text, chr(13) || chr(10), chr(10)), chr(13), chr(10));
    if length(btrim(v_text)) = 0 then
        raise exception 'A translation cannot be empty. Clear the row instead, so the key falls back to English.'
              using errcode = 'P0001';
    end if;
    if length(v_text) > 2000 then
        raise exception 'A translation must be 2000 characters or fewer.' using errcode = 'P0001';
    end if;
    v_max := greatest(64, 4 * length(v_english));
    if length(v_text) > v_max then
        raise exception 'That is more than four times the length of the English. Shorten it.'
              using errcode = 'P0001';
    end if;
    if position(chr(10) in v_text) > 0 and position(chr(10) in v_english) = 0 then
        raise exception 'The English here has no line break, so the translation cannot have one.'
              using errcode = 'P0001';
    end if;
    if v_text <> translate(v_text, public._loc_banned_chars(), '') then
        raise exception 'That text carries an invisible or control character that is not allowed.'
              using errcode = 'P0001';
    end if;
    if position(chr(8212) in v_text) > 0 then
        raise exception 'An em dash is not allowed here. Use a comma or a colon.'
              using errcode = 'P0001';
    end if;
    if position('--' in v_text) > 0 then
        raise exception 'Two hyphens in a row are not allowed here.' using errcode = 'P0001';
    end if;
    select array_agg(m[1] order by m[1]) into v_ph_en
      from regexp_matches(v_english, '(\{\d+(?::[^{}]*)?\})', 'g') m;
    select array_agg(m[1] order by m[1]) into v_ph_tr
      from regexp_matches(v_text, '(\{\d+(?::[^{}]*)?\})', 'g') m;
    if coalesce(v_ph_en, '{}') <> coalesce(v_ph_tr, '{}') then
        raise exception 'The placeholders must match the English exactly: %.',
              coalesce(array_to_string(v_ph_en, ', '), 'none') using errcode = 'P0001';
    end if;
    if public._has_blocked_term(v_text)
       or (p_note is not null and public._has_blocked_term(p_note)) then
        raise exception 'That note contains a blocked word.' using errcode = 'P0001';
    end if;

    select t.text into v_approved
      from public.translations t
     where t.culture = p_culture and t.key = p_key
       and t.english_sha256 = p_hash and t.status = 'approved';
    if v_approved is not null and v_approved = v_text then
        return 'already_approved';
    end if;

    update public.translations
       set status = 'superseded', updated_at = now()
     where culture = p_culture and key = p_key
       and english_sha256 = p_hash and status = 'approved';
    -- The caller's own pending row for this key, whichever way they are identified.
    update public.translations
       set status = 'superseded', updated_at = now()
     where culture = p_culture and key = p_key and status = 'pending'
       and ((p_uid is not null and submitter = p_uid)
            or (p_uid is null and p_anon is not null and anon_id = p_anon));

    insert into public.translations
        (culture, key, text, english_sha256, submitter, anon_id, source, plugin_version,
         note, source_ip_hash, status, submitted_at)
    values (p_culture, p_key, v_text, p_hash, p_uid, p_anon, p_source, p_plugin_version,
            p_note, p_ip_hash, 'approved', now());
    return 'approved';
end $$;
revoke all on function public._submit_translation_row(text, uuid, text, text, text, text, text, text, text, text)
       from public, anon, authenticated;
-- 0137's nine-argument shape has no caller left; dropping it keeps one writer rather
-- than two that could drift.
drop function if exists public._submit_translation_row(text, uuid, text, text, text, text, text, text, text);

-- ---------------------------------------------------------------------------
-- The write path, now callable signed out.
create or replace function public.submit_translations(
    p_culture text, p_rows jsonb, p_source text default 'plugin',
    p_plugin_version text default null, p_anon_id text default null)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_uid      uuid := auth.uid();
    v_anon     text;
    v_who      text;          -- what the per-person caps count against
    v_ip_hash  text;
    v_accepting boolean;
    v_count    int;
    v_row      jsonb;
    v_status   text;
    v_accepted jsonb := '[]'::jsonb;
    v_rejected jsonb := '[]'::jsonb;
begin
    -- An account when there is one, so a contribution can carry a name; else the
    -- client's own id, checked for shape so junk never reaches the column.
    if v_uid is not null then
        if coalesce((auth.jwt() ->> 'is_anonymous')::boolean, false) then
            raise exception 'Sign in with an email address to send translations.' using errcode = 'P0001';
        end if;
        v_who := v_uid::text;
    elsif p_anon_id is not null and length(btrim(p_anon_id)) > 0 then
        if btrim(p_anon_id) !~ '^[A-Za-z0-9-]{8,64}$' then
            raise exception 'That client id is not a valid one.' using errcode = 'P0001';
        end if;
        v_anon := 'anon:' || btrim(p_anon_id);
        v_who := v_anon;
    else
        -- Reachable only from a client that sends neither, which is a client too old
        -- to know about this or one that has not minted its id yet.
        raise exception 'This copy of the plugin cannot send translations. Update it, or sign in.'
              using errcode = 'P0001';
    end if;

    v_ip_hash := encode(digest(public._client_ip(), 'sha256'), 'hex');
    -- All three forms: the account, the self-chosen id, and the address. The last is
    -- the backstop, because the middle one costs nothing to change.
    if exists (select 1 from public.submitter_blocked b
                where b.submitter_id in (v_who, 'ip:' || v_ip_hash)
                  and (b.banned_until is null or b.banned_until > now())) then
        raise exception 'This account cannot send translations.' using errcode = 'P0001';
    end if;
    if p_source is null or p_source not in ('web', 'plugin') then
        raise exception 'Unknown source.' using errcode = 'P0001';
    end if;

    select c.accepting into v_accepting
      from public.translation_cultures c where c.tag = p_culture;
    if v_accepting is null then
        raise exception 'That language is not open for translation yet. Ask on Discord to open it.'
              using errcode = 'P0001';
    end if;
    if not v_accepting then
        raise exception 'That language is closed for edits right now.' using errcode = 'P0001';
    end if;

    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'Nothing to send.' using errcode = 'P0001';
    end if;
    v_count := jsonb_array_length(p_rows);
    if v_count = 0 then
        return jsonb_build_object('ok', true, 'accepted', '[]'::jsonb, 'rejected', '[]'::jsonb);
    end if;
    if v_count > 50 then
        raise exception 'Fifty rows per send. Split the rest into another send.' using errcode = 'P0001';
    end if;

    -- The new-account wait is an account rule and has nothing to say about someone who
    -- has no account. The address cap below is what bounds a fresh anonymous client.
    if v_uid is not null and exists (select 1 from auth.users u
                where u.id = v_uid and u.created_at > now() - interval '10 minutes') then
        raise exception 'A new account can send translations ten minutes after it is created.'
              using errcode = 'P0001';
    end if;
    -- Per person, counted against whichever identity this is.
    if (select count(*) from public.translations t
         where t.submitted_at > now() - interval '1 hour'
           and ((v_uid is not null and t.submitter = v_uid)
                or (v_uid is null and t.anon_id = v_anon)))
       + v_count > 300 then
        raise exception 'That is 300 translations in an hour. The rest can go in an hour.'
              using errcode = 'P0001';
    end if;
    if (select count(*) from public.translations t
         where t.source_ip_hash = v_ip_hash and t.submitted_at > now() - interval '1 hour')
       + v_count > 600 then
        raise exception 'This connection has sent 600 translations in an hour. Try again later.'
              using errcode = 'P0001';
    end if;
    if (select count(*) from public.translations t
         where t.culture = p_culture and t.status = 'pending'
           and ((v_uid is not null and t.submitter = v_uid)
                or (v_uid is null and t.anon_id = v_anon)))
       + v_count > 3000 then
        raise exception 'You have 3,000 translations waiting for review in this language. Wait for a review before sending more.'
              using errcode = 'P0001';
    end if;
    if (select count(*) from public.translations t
         where t.culture = p_culture and t.status = 'pending') + v_count > 20000 then
        raise exception 'This language has 20,000 translations waiting for review. Try again once the queue moves.'
              using errcode = 'P0001';
    end if;

    for v_row in select * from jsonb_array_elements(p_rows) loop
        begin
            v_status := public._submit_translation_row(
                p_culture, v_uid, v_anon, p_source, p_plugin_version, v_ip_hash,
                v_row ->> 'k', v_row ->> 't', v_row ->> 'h', v_row ->> 'n');
            v_accepted := v_accepted || jsonb_build_object('k', v_row ->> 'k', 'status', v_status);
        exception when others then
            v_rejected := v_rejected || jsonb_build_object('k', v_row ->> 'k', 'error', sqlerrm);
        end;
    end loop;

    return jsonb_build_object('ok', true, 'accepted', v_accepted, 'rejected', v_rejected);
end $$;
-- 0137's four-argument shape goes: a client calling it would be silently signed out
-- again, which is the bug this migration exists to fix.
drop function if exists public.submit_translations(text, jsonb, text, text);
revoke all on function public.submit_translations(text, jsonb, text, text, text) from public;
grant execute on function public.submit_translations(text, jsonb, text, text, text)
      to anon, authenticated;

-- ---------------------------------------------------------------------------
-- Withdrawing, for whichever identity sent the row.
create or replace function public.withdraw_translations(p_ids uuid[], p_anon_id text default null)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_uid  uuid := auth.uid();
    v_anon text;
    v_n    int;
begin
    if p_ids is null or array_length(p_ids, 1) is null then
        return jsonb_build_object('ok', true, 'withdrawn', 0);
    end if;
    if v_uid is null then
        if p_anon_id is null or btrim(p_anon_id) !~ '^[A-Za-z0-9-]{8,64}$' then
            raise exception 'Sign in to withdraw translations.' using errcode = 'P0001';
        end if;
        v_anon := 'anon:' || btrim(p_anon_id);
    end if;
    update public.translations
       set status = 'withdrawn'
     where id = any(p_ids) and status = 'pending'
       and ((v_uid is not null and submitter = v_uid)
            or (v_uid is null and anon_id = v_anon));
    get diagnostics v_n = row_count;
    return jsonb_build_object('ok', true, 'withdrawn', v_n);
end $$;
drop function if exists public.withdraw_translations(uuid[]);
revoke all on function public.withdraw_translations(uuid[], text) from public;
grant execute on function public.withdraw_translations(uuid[], text) to anon, authenticated;
