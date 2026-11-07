-- 0137: the translation RPC contract (docs/localization-translation-service.md
-- section 3). The design doc carries these in the same migration as the schema;
-- they are their own file here because the schema went in as 0136 and a reader
-- following the doc will look for the contract under its own number.
--
-- All security definer, search_path pinned, identity from auth.uid() and never
-- from the body. Every refusal is errcode P0001 with a sentence both clients show
-- verbatim. Idempotent: create or replace throughout, ACLs set by name after.
set lock_timeout = '3s';
set statement_timeout = '60s';

-- ---------------------------------------------------------------------------
-- One row of a submission. Revoked from every role and called inside a per-row
-- exception block by submit_translations, so one bad row does not abort a batch.
-- Returns the row's status word, or raises with the sentence the caller shows.
create or replace function public._submit_translation_row(
    p_culture text, p_uid uuid, p_source text, p_plugin_version text,
    p_ip_hash text, p_key text, p_text text, p_hash text, p_note text)
returns text language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_english      text;
    v_english_hash text;
    v_text         text;
    v_max           int;
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
    -- The current English, or any English this key has ever had: a mixed-version
    -- fleet means an install may be translating last release's wording.
    if p_hash <> v_english_hash
       and not exists (select 1 from public.english_strings_history h
                        where h.key = p_key and h.sha256 = p_hash) then
        raise exception 'The English for this key moved. Reload the page and translate the current text.'
              using errcode = 'P0001';
    end if;
    -- CRLF and a lone CR become LF. Nothing else is normalized: not trimming, not
    -- Unicode, because the hash convention does not either.
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
    -- Placeholder sets as multisets, sorted, so {0} twice in the English needs
    -- {0} twice in the translation.
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

    -- Already live and identical: say so rather than writing a row that would
    -- supersede text with the same text.
    select t.text into v_approved
      from public.translations t
     where t.culture = p_culture and t.key = p_key
       and t.english_sha256 = p_hash and t.status = 'approved';
    if v_approved is not null and v_approved = v_text then
        return 'already_approved';
    end if;

    -- review_policy 'open' (owner, 2026-09-27): the submit writes the live row,
    -- so it has to supersede the one it replaces in the same statement, which is
    -- what the reviewer's accept used to do and what keeps
    -- translations_one_approved_per_english satisfied.
    update public.translations
       set status = 'superseded', updated_at = now()
     where culture = p_culture and key = p_key
       and english_sha256 = p_hash and status = 'approved';
    -- The caller's own pending row for this key, if any, steps aside too: their
    -- new text is the one that serves.
    update public.translations
       set status = 'superseded', updated_at = now()
     where culture = p_culture and key = p_key
       and submitter = p_uid and status = 'pending';

    insert into public.translations
        (culture, key, text, english_sha256, submitter, source, plugin_version,
         note, source_ip_hash, status, submitted_at)
    values (p_culture, p_key, v_text, p_hash, p_uid, p_source, p_plugin_version,
            p_note, p_ip_hash, 'approved', now());
    return 'approved';
end $$;
revoke all on function public._submit_translation_row(text, uuid, text, text, text, text, text, text, text)
       from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- The write path. {k, t, h, n} rows, at most 50, all caps counted before the
-- loop so a batch cannot straddle one.
create or replace function public.submit_translations(
    p_culture text, p_rows jsonb, p_source text default 'plugin',
    p_plugin_version text default null)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_uid      uuid := auth.uid();
    v_ip_hash  text;
    v_accepting boolean;
    v_policy   text;
    v_count    int;
    v_row      jsonb;
    v_status   text;
    v_accepted jsonb := '[]'::jsonb;
    v_rejected jsonb := '[]'::jsonb;
begin
    if v_uid is null then
        raise exception 'Sign in to send translations.' using errcode = 'P0001';
    end if;
    if coalesce((auth.jwt() ->> 'is_anonymous')::boolean, false) then
        raise exception 'Sign in with an email address to send translations.' using errcode = 'P0001';
    end if;
    v_ip_hash := encode(digest(public._client_ip(), 'sha256'), 'hex');
    -- Both forms of the block: the account uuid, and 'ip:' plus the digest,
    -- because a browser session carries no device code and that is the only
    -- handle a web-only abuser leaves.
    if exists (select 1 from public.submitter_blocked b
                where b.submitter_id in (v_uid::text, 'ip:' || v_ip_hash)
                  and (b.banned_until is null or b.banned_until > now())) then
        raise exception 'This account cannot send translations.' using errcode = 'P0001';
    end if;
    if p_source is null or p_source not in ('web', 'plugin') then
        raise exception 'Unknown source.' using errcode = 'P0001';
    end if;

    select c.accepting, c.review_policy into v_accepting, v_policy
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

    if exists (select 1 from auth.users u
                where u.id = v_uid and u.created_at > now() - interval '10 minutes') then
        raise exception 'A new account can send translations ten minutes after it is created.'
              using errcode = 'P0001';
    end if;
    -- One count against rows already inside the window, so the call that would
    -- cross the cap is refused whole and no row is ever refused as "the 301st".
    if (select count(*) from public.translations t
         where t.submitter = v_uid and t.submitted_at > now() - interval '1 hour')
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
         where t.culture = p_culture and t.submitter = v_uid and t.status = 'pending')
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
                p_culture, v_uid, p_source, p_plugin_version, v_ip_hash,
                v_row ->> 'k', v_row ->> 't', v_row ->> 'h', v_row ->> 'n');
            v_accepted := v_accepted || jsonb_build_object('k', v_row ->> 'k', 'status', v_status);
        exception when others then
            v_rejected := v_rejected || jsonb_build_object('k', v_row ->> 'k', 'error', sqlerrm);
        end;
    end loop;

    return jsonb_build_object('ok', true, 'accepted', v_accepted, 'rejected', v_rejected);
end $$;
revoke all on function public.submit_translations(text, jsonb, text, text) from public, anon;
grant execute on function public.submit_translations(text, jsonb, text, text) to authenticated;

-- ---------------------------------------------------------------------------
-- Own pending rows only.
create or replace function public.withdraw_translations(p_ids uuid[])
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_uid uuid := auth.uid();
    v_n   int;
begin
    if v_uid is null then
        raise exception 'Sign in to withdraw translations.' using errcode = 'P0001';
    end if;
    if p_ids is null or array_length(p_ids, 1) is null then
        return jsonb_build_object('ok', true, 'withdrawn', 0);
    end if;
    update public.translations
       set status = 'withdrawn'
     where id = any(p_ids) and submitter = v_uid and status = 'pending';
    get diagnostics v_n = row_count;
    return jsonb_build_object('ok', true, 'withdrawn', v_n);
end $$;
revoke all on function public.withdraw_translations(uuid[]) from public, anon;
grant execute on function public.withdraw_translations(uuid[]) to authenticated;

-- ---------------------------------------------------------------------------
-- The fetch. Anon, stable, and the only way approved text leaves the server.
create or replace function public.list_approved_translations(
    p_culture text, p_known_revision bigint default null)
returns jsonb language plpgsql stable security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_revision bigint;
    v_count    int;
    v_rows     jsonb;
begin
    select c.revision into v_revision
      from public.translation_cultures c where c.tag = p_culture;
    -- An unknown tag is an empty answer, not an error: the plugin asks for every
    -- member of a chain such as zh-Hans-CN, zh-Hans, zh, and two of those three
    -- will never have a row. A tag that has a row is served whatever accepting
    -- says, because accepting gates submission alone.
    if v_revision is null then
        return jsonb_build_object('ok', true, 'culture', p_culture, 'revision', 0,
                                  'rows', '[]'::jsonb);
    end if;
    if p_known_revision is not null and p_known_revision = v_revision then
        return jsonb_build_object('ok', true, 'culture', p_culture,
                                  'revision', v_revision, 'unchanged', true);
    end if;
    select count(*) into v_count from public.translations t
     where t.culture = p_culture and t.status = 'approved';
    -- No rows at all above the cap. A partial set would be half-applied by a
    -- client whose file is a wholesale replace, so the client keeps what it has.
    if v_count > 10000 then
        return jsonb_build_object('ok', true, 'culture', p_culture,
                                  'revision', v_revision, 'truncated', true);
    end if;
    select coalesce(jsonb_agg(jsonb_build_object('k', t.key, 't', t.text,
                                                 'h', t.english_sha256)
                              order by t.key), '[]'::jsonb)
      into v_rows
      from public.translations t
     where t.culture = p_culture and t.status = 'approved';
    return jsonb_build_object('ok', true, 'culture', p_culture,
                              'revision', v_revision, 'rows', v_rows);
end $$;
revoke all on function public.list_approved_translations(text, bigint) from public;
grant execute on function public.list_approved_translations(text, bigint) to anon, authenticated;

-- ---------------------------------------------------------------------------
-- Per language: what exists, never how good it is. percent counts only rows whose
-- hash equals the live English; approved_other is the re-approval queue.
create or replace function public.translation_progress()
returns jsonb language sql stable security definer
set search_path = public, extensions, pg_temp as $$
  with live as (
      select count(*)::int as total from public.english_strings where retired_at is null
  ), per as (
      select c.tag, c.name, c.accepting, c.revision,
             (select count(*) from public.translations t
               join public.english_strings e on e.key = t.key and e.retired_at is null
              where t.culture = c.tag and t.status = 'approved'
                and t.english_sha256 = e.sha256)::int as approved_current,
             (select count(*) from public.translations t
               left join public.english_strings e on e.key = t.key and e.retired_at is null
              where t.culture = c.tag and t.status = 'approved'
                and (e.sha256 is null or t.english_sha256 <> e.sha256))::int as approved_other,
             (select count(*) from public.translations t
              where t.culture = c.tag and t.status = 'pending')::int as pending
        from public.translation_cultures c
  )
  select coalesce(jsonb_agg(jsonb_build_object(
             'tag', per.tag, 'name', per.name, 'accepting', per.accepting,
             'revision', per.revision,
             'english_ref', (select value from public.app_config where key = 'english_catalog_ref'),
             'english_version', (select value from public.app_config where key = 'english_catalog_version'),
             'total', live.total,
             'approved_current', per.approved_current,
             'approved_other', per.approved_other,
             'pending', per.pending,
             'percent', case when live.total = 0 then 0
                             else round(100.0 * per.approved_current / live.total, 1) end)
             order by per.tag), '[]'::jsonb)
    from per cross join live;
$$;
revoke all on function public.translation_progress() from public;
grant execute on function public.translation_progress() to anon, authenticated;

-- ---------------------------------------------------------------------------
-- Per key, how many rows are pending. Never text, never a username.
create or replace function public.translation_pending_counts(p_culture text)
returns jsonb language sql stable security definer
set search_path = public, extensions, pg_temp as $$
  select coalesce(jsonb_object_agg(t.key, t.n), '{}'::jsonb) from (
      select key, count(*)::int as n from public.translations
       where culture = p_culture and status = 'pending'
       group by key) t;
$$;
revoke all on function public.translation_pending_counts(text) from public;
grant execute on function public.translation_pending_counts(text) to anon, authenticated;

-- ---------------------------------------------------------------------------
-- Credits. Accounts with no username, a deleted account, or the opt-out flag
-- collapse into one (anonymous) row.
create or replace function public.translation_contributors(p_culture text default null)
returns jsonb language sql stable security definer
set search_path = public, extensions, pg_temp as $$
  with named as (
      select case when p.username is null
                    or coalesce(p.hide_from_translation_credits, false)
                  then '(anonymous)' else p.username end as name,
             count(*)::int as n
        from public.translations t
        left join public.profiles p on p.id = t.submitter
       where t.status in ('approved', 'superseded')
         and (p_culture is null or t.culture = p_culture)
       group by 1)
  -- Ordered by the count as a number. Ordering by the built object's text would
  -- have put 9 above 10.
  select coalesce(jsonb_agg(jsonb_build_object('name', name, 'rows', n)
                            order by n desc, name), '[]'::jsonb)
    from named;
$$;
revoke all on function public.translation_contributors(text) from public;
grant execute on function public.translation_contributors(text) to anon, authenticated;

-- ---------------------------------------------------------------------------
-- Service role only: the English push, and the shipped stamp.
create or replace function public.upsert_english_strings(
    p_version text, p_ref text, p_rows jsonb)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_added   int := 0;
    v_changed int := 0;
    v_retired int := 0;
    v_live    int := 0;
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'p_rows must be a JSON array.' using errcode = 'P0001';
    end if;
    create temporary table _push (key text primary key, text text, context jsonb)
        on commit drop;
    insert into _push (key, text, context)
    select r ->> 'k', r ->> 't',
           case when r ? 'c' then r -> 'c' else null end
      from jsonb_array_elements(p_rows) r
     where r ->> 'k' is not null and r ->> 't' is not null
        on conflict (key) do nothing;

    -- The text a key is leaving behind, so a reviewer can be shown what a
    -- submission actually translated.
    insert into public.english_strings_history (key, sha256, text, version)
    select e.key, e.sha256, e.text, e.version
      from public.english_strings e join _push p on p.key = e.key
     where e.text <> p.text
        on conflict (key, sha256) do nothing;

    with up as (
        insert into public.english_strings (key, text, context, first_version, version, retired_at, updated_at)
        select p.key, p.text, p.context, p_version, p_version, null, now() from _push p
            on conflict (key) do update
               set text = excluded.text,
                   context = coalesce(excluded.context, public.english_strings.context),
                   version = excluded.version,
                   retired_at = null,
                   updated_at = now()
             where public.english_strings.text <> excluded.text
                or public.english_strings.retired_at is not null
                or public.english_strings.version <> excluded.version
                or coalesce(public.english_strings.context, '{}'::jsonb)
                   <> coalesce(excluded.context, public.english_strings.context, '{}'::jsonb)
        returning (xmax = 0) as inserted)
    select count(*) filter (where inserted), count(*) filter (where not inserted)
      into v_added, v_changed from up;

    update public.english_strings e
       set retired_at = now(), updated_at = now()
     where e.retired_at is null and not exists (select 1 from _push p where p.key = e.key);
    get diagnostics v_retired = row_count;

    select count(*) into v_live from public.english_strings where retired_at is null;

    insert into public.app_config (key, value) values ('english_catalog_version', p_version)
        on conflict (key) do update set value = excluded.value;
    insert into public.app_config (key, value) values ('english_catalog_ref', p_ref)
        on conflict (key) do update set value = excluded.value;

    return jsonb_build_object('ok', true, 'added', v_added, 'changed', v_changed,
                              'retired', v_retired, 'live', v_live);
end $$;
revoke all on function public.upsert_english_strings(text, text, jsonb)
       from public, anon, authenticated;

-- Informational: stamps the release an approved row went out in.
create or replace function public.mark_translations_shipped(p_culture text, p_version text)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare v_n int;
begin
    update public.translations t
       set shipped_version = p_version
      from public.english_strings e
     where e.key = t.key and e.retired_at is null
       and t.culture = p_culture and t.status = 'approved'
       and t.english_sha256 = e.sha256
       and coalesce(t.shipped_version, '') <> p_version;
    get diagnostics v_n = row_count;
    return jsonb_build_object('ok', true, 'stamped', v_n);
end $$;
revoke all on function public.mark_translations_shipped(text, text)
       from public, anon, authenticated;
