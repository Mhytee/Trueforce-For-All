-- 0143: a counted form's placeholders are a subset of the plural English's
-- (owner, 2026-11-03).
--
-- 0136's rule is that a translation carries exactly the placeholders its English
-- carries. That is right for an ordinary string and wrong for a counted one, because
-- English writes a literal 1 in its singular. "+1 more issue" has no placeholder at
-- all, so a language whose singular form also covers 21 and 31 had no way to write
-- its own singular: Russian needs the {0} that English's singular does not use, and
-- the exact rule refused it. Twelve of the twenty-two counted strings were affected
-- in Russian, Translate_PublishRefused among them, which is the message the server
-- sends back when it refuses a translation.
--
-- The same correction went into tools/loc/validate.ps1 when Russian first exposed it;
-- this is the server holding the same rule, so what the validator accepts in a shipped
-- file is what the service accepts from a contributor.
--
-- The 'other' form keeps the exact rule. It translates the plural English directly,
-- and a subset there would let a count quietly go missing from a sentence.
set lock_timeout = '3s';
set statement_timeout = '60s';

CREATE OR REPLACE FUNCTION public._submit_translation_row(p_culture text, p_uid uuid, p_anon text, p_source text, p_plugin_version text, p_ip_hash text, p_key text, p_text text, p_hash text, p_note text)
 RETURNS text
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
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
    -- Placeholders. Exactly English's, except on a counted form other than 'other',
    -- where they are checked as a subset of the plural English's instead.
    --
    -- English writes two forms and puts a literal 1 in the singular: "+1 more issue"
    -- beside "+{0} more issues". A language whose singular form also covers 21 and 31
    -- cannot write a literal, so Russian's one-form needs the {0} that English's
    -- one-form has no use for, and demanding an exact match refuses the only correct
    -- translation of it. A subset of the plural form's placeholders is the rule that
    -- admits that without admitting an invented placeholder. Spanish keeps its literal
    -- "1 piloto" under the same rule, because a subset may be empty.
    --
    -- The 'other' form itself stays exact: it translates the plural English directly,
    -- and a subset there would let the count quietly go missing.
    select array_agg(m[1] order by m[1]) into v_ph_tr
      from regexp_matches(v_text, '(\{\d+(?::[^{}]*)?\})', 'g') m;
    if p_key ~ '\.(zero|one|two|few|many)$' then
        select array_agg(m[1] order by m[1]) into v_ph_en
          from regexp_matches(
                 coalesce((select e.text from public.english_strings e
                            where e.key = regexp_replace(p_key,
                                            '\.(zero|one|two|few|many)$', '.other')
                              and e.retired_at is null),
                          v_english),
                 '(\{\d+(?::[^{}]*)?\})', 'g') m;
        if exists (select 1 from unnest(coalesce(v_ph_tr, '{}'::text[])) t
                    where t <> all (coalesce(v_ph_en, '{}'::text[]))) then
            raise exception 'The placeholders can only be the ones the English uses: %.',
                  coalesce(array_to_string(v_ph_en, ', '), 'none') using errcode = 'P0001';
        end if;
    else
        select array_agg(m[1] order by m[1]) into v_ph_en
          from regexp_matches(v_english, '(\{\d+(?::[^{}]*)?\})', 'g') m;
        if coalesce(v_ph_en, '{}') <> coalesce(v_ph_tr, '{}') then
            raise exception 'The placeholders must match the English exactly: %.',
                  coalesce(array_to_string(v_ph_en, ', '), 'none') using errcode = 'P0001';
        end if;
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
end $function$;

revoke all on function public._submit_translation_row(text, uuid, text, text, text, text, text, text, text, text)
       from public, anon, authenticated;
