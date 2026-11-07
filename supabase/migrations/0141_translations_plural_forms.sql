-- 0141: the plural forms English does not have.
--
-- A counted string ships as two English rows, X.one and X.other, because English has two
-- forms. Most languages do not. Russian needs four, Arabic six, Japanese one. Those forms
-- are real keys with real translations and no English row of their own: they are written
-- against the English plural.
--
-- Three things followed from that and none of them worked:
--
--   _submit_translation_row looked X.few up in english_strings, did not find it, and told
--   the translator the key was not one the server holds;
--
--   translation_progress joined translations to english_strings on the key, so a form
--   English lacks could never be current and landed in approved_other, which is the
--   re-approval queue;
--
--   and the same query counted every English row towards the total, so a Japanese
--   translator, who will never write X.one, could not reach 100 percent.
--
-- The fix is two small functions and the three call sites that need them. Which forms a
-- language uses stays out of the database on purpose: that rule lives in LocPlurals.cs,
-- it is asserted there count by count, and a second copy here would drift. The server
-- checks that a form was written against English it holds, which is the part it can know.
--
-- Idempotent: functions only, all create or replace.

-- ---------------------------------------------------------------------------
-- The English key that governs a translation key. Itself when English has it, which
-- covers every ordinary key and both of the forms English ships; otherwise the plural,
-- which is what a form English does not have translates. Null when there is no such
-- English, so a stray key is still refused.
create or replace function public._loc_english_key(p_key text)
returns text language sql stable
set search_path = public, pg_temp as $$
  select case
      when exists (select 1 from public.english_strings e
                    where e.key = p_key and e.retired_at is null) then p_key
      when p_key ~ '\.(zero|one|two|few|many|other)$'
           and exists (select 1 from public.english_strings e
                        where e.key = regexp_replace(p_key, '\.(zero|one|two|few|many|other)$', '.other')
                          and e.retired_at is null)
      then regexp_replace(p_key, '\.(zero|one|two|few|many|other)$', '.other')
      else null
  end;
$$;
revoke all on function public._loc_english_key(text) from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- What one unit of progress is. A counted string is one, however many forms the language
-- writes for it: counting forms would put Japanese permanently short of the total and
-- Arabic permanently over it. The '.#' suffix keeps a collapsed base from colliding with
-- an ordinary key that happens to be spelled like one.
create or replace function public._loc_progress_unit(p_key text)
returns text language sql immutable
set search_path = pg_temp as $$
  select case when p_key ~ '\.(zero|one|two|few|many|other)$'
              then regexp_replace(p_key, '\.(zero|one|two|few|many|other)$', '') || '.#'
              else p_key end;
$$;
revoke all on function public._loc_progress_unit(text) from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- The row writer, 0140's with the English looked up through _loc_english_key. Everything
-- else in it is unchanged, including the two rules 0140 moved into the constraint.
create or replace function public._submit_translation_row(
    p_culture text, p_uid uuid, p_anon text, p_source text, p_plugin_version text,
    p_ip_hash text, p_key text, p_text text, p_hash text, p_note text)
returns text language plpgsql security definer
set search_path = public, extensions, pg_temp as $$
declare
    v_english_key  text;
    v_english      text;
    v_english_hash text;
    v_text         text;
    v_max          int;
    v_approved     text;
    v_ph_en        text[];
    v_ph_tr        text[];
begin
    v_english_key := public._loc_english_key(p_key);
    if v_english_key is null then
        raise exception 'This key is not in the English the server holds. Reload and try again.'
              using errcode = 'P0001';
    end if;
    select e.text, e.sha256 into v_english, v_english_hash
      from public.english_strings e
     where e.key = v_english_key and e.retired_at is null;
    if v_english is null then
        raise exception 'This key is not in the English the server holds. Reload and try again.'
              using errcode = 'P0001';
    end if;
    if p_hash <> v_english_hash
       and not exists (select 1 from public.english_strings_history h
                        where h.key = v_english_key and h.sha256 = p_hash) then
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
    -- Four times the English, with a floor. Counted in characters, which is generous to a
    -- language that says the same thing in fewer of them: Japanese and Chinese routinely
    -- come in at a third of the English and can never reach this.
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
        raise exception 'That text carries a control character that could make it render as something else.'
              using errcode = 'P0001';
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

-- ---------------------------------------------------------------------------
-- Progress counted in strings rather than in rows, and joined through the governing
-- English key so a form English does not have can be current.
create or replace function public.translation_progress()
returns jsonb language sql stable security definer
set search_path = public, extensions, pg_temp as $$
  with live as (
      select count(distinct public._loc_progress_unit(key))::int as total
        from public.english_strings where retired_at is null
  ), per as (
      select c.tag, c.name, c.accepting, c.revision,
             (select count(distinct public._loc_progress_unit(t.key))
               from public.translations t
               join public.english_strings e
                 on e.key = public._loc_english_key(t.key) and e.retired_at is null
              where t.culture = c.tag and t.status = 'approved'
                and t.english_sha256 = e.sha256)::int as approved_current,
             (select count(*) from public.translations t
               left join public.english_strings e
                 on e.key = public._loc_english_key(t.key) and e.retired_at is null
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
