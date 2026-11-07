-- 0140: stop refusing text that other writing systems need (owner question, 2026-09-29:
-- "can all languages actually be translated?").
--
-- The chooser offers every language the framework knows. Three of the rules in 0136 were
-- written for English and quietly said no to some of them. Tested against real samples
-- before this file was written:
--
--   Japanese, Chinese, Korean and Thai plain text  passed everything already.
--   Chinese with the standard dash, which is two   refused. chr(8212) twice IS the
--     U+2014 characters                            Chinese dash, not a typographic
--                                                  flourish, and the ban on it is this
--                                                  project's English house style. A house
--                                                  style is not a translator's rule.
--   Arabic carrying a right-to-left mark, Hebrew    refused, because the banned list held
--     using the directional isolates                every bidi control.
--   Arabic carrying a right-to-left OVERRIDE        refused, and that one is correct: an
--                                                  override can make a string render as
--                                                  something else entirely.
--
-- So: the em dash and the hyphen pair stop being table rules, and the banned list keeps
-- the controls that can disguise text while releasing the ones a script needs to be
-- written at all. Persian is the clearest case: without the zero-width non-joiner it
-- cannot be spelled correctly.
set lock_timeout = '3s';
set statement_timeout = '60s';

-- The narrowed list. Same shape as 0136, eight code points lighter, and the comment on
-- each line says why it is still here.
create or replace function public._loc_banned_chars()
returns text language sql immutable as $$
  select string_agg(chr(c), '' order by c) from (
    select generate_series(1, 8) as c                 -- C0 below TAB
    union all select 11 union all select 12           -- VT, FF
    union all select generate_series(14, 31)          -- C0 above CR
    union all select 127                              -- DEL
    union all select generate_series(128, 159)        -- C1
    union all select 8203                             -- ZWSP: padding and invisible text
    union all select 8232 union all select 8233       -- LS, PS: break a single-line label
    union all select generate_series(8234, 8238)      -- LRE, RLE, PDF, LRO, RLO: these
                                                      -- can make one string render as
                                                      -- another, which is the whole
                                                      -- reason this list exists
    union all select 65279                            -- ZWNBSP, a stray BOM
  ) t;
$$;
revoke all on function public._loc_banned_chars() from public, anon, authenticated;

-- Released, and why each one had to be:
--   8204 ZWNJ   Persian and Urdu spelling, Indic non-conjuncts
--   8205 ZWJ    Indic conjuncts, and emoji sequences
--   8206 LRM    an English name inside an Arabic sentence
--   8207 RLM    punctuation placement in mixed text
--   8294..8297  LRI, RLI, FSI, PDI: the modern, recommended way to isolate a run of the
--               other direction, and the pairing is why they arrive together

-- Changing the function body does not revalidate stored rows, so the constraint is
-- replaced rather than edited. Dropping first is safe: nothing can be written between the
-- two statements in one transaction, and every write goes through the RPC in any case.
alter table public.translations drop constraint if exists translations_text_check;
alter table public.translations add constraint translations_text_check
    check (length(text) between 1 and 2000
           and text = translate(text, public._loc_banned_chars(), ''));

-- ---------------------------------------------------------------------------
-- The row writer loses the same two rules, so the readable message and the constraint
-- cannot disagree. Everything else is 0139's.
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
