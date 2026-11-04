-- 0145: a translation carries only the web addresses its English carries
-- (owner, 2026-11-04).
--
-- The blocked-terms list cannot see a link. It strips everything but letters after
-- normalizing leetspeak, so evil.com becomes evilcom and matches only if that exact
-- term is listed. Meanwhile review_policy is 'open', so a submitted string serves to
-- every install on that language within the day, and the panel renders whatever text
-- it is handed. A plausible address in a settings label is the cheapest phishing a
-- stranger could post here.
--
-- Two shipped strings do carry an address, the Patreon and PayPal tips, and nothing
-- else in the 18,328 strings across the six language files does. So the rule is a
-- subset, not a ban: every address in a translation must be one its English already
-- uses. An unconditional ban would have broken those two keys in all five languages,
-- and a plain "does the English have one" test would have made them the single place
-- where an address could be swapped for another, which is worse than either.
--
-- Detection and extraction deliberately disagree about case. _loc_url_shape decides
-- whether to check at all and is strict: schemes and 'www.' match either case, because
-- nobody writes those by accident, but a bare domain must be LOWERCASE. That is what
-- separates patreon.com from a missing space after a period, since a sentence break
-- leaves the next word capitalized, as in "sitio.Online ahora". It is why the suffix
-- list can include ordinary words like 'online' and 'live' at all, and it was verified
-- against every shipped string: exactly the two keys above match, in all six files.
-- _loc_url_tokens then extracts hosts case-insensitively, and catches a scheme's host
-- whatever its suffix, so https://evil.quest cannot walk through a suffix list.
--
-- What this does not do: stop someone determined. Spaces, brackets, or a Cyrillic
-- homoglyph all walk past a pattern. That is what revert_translations (0144) and
-- submitter_blocked are for. This stops the pasted link, which is the common case.
set lock_timeout = '3s';
set statement_timeout = '60s';

-- Whether this text contains something shaped like an address. Strict, see above.
create or replace function public._loc_url_shape(p_text text)
returns boolean language sql immutable
set search_path = pg_temp as $fn$
  select coalesce(p_text, '') ~* '(https?://)|(\ywww\.)'
      or coalesce(p_text, '') ~ '[a-z0-9][a-z0-9-]*\.(com|net|org|io|ru|cn|xyz|biz|cc|su|ir|tk|ml|ga|cf|ly|gg|tv|info|top|me|co|app|dev|link|click|online|site|shop|live|pro|work|store|fun|to)(?![a-z])';
$fn$;
revoke all on function public._loc_url_shape(text) from public, anon, authenticated;

-- The hosts in this text, lowercased, with any leading www. dropped so that
-- www.patreon.com and patreon.com compare as the same place. Permissive on case and
-- on suffix: this decides what is allowed, so missing one would be the hole.
create or replace function public._loc_url_tokens(p_text text)
returns text[] language sql immutable
set search_path = pg_temp as $fn$
  select coalesce(array_agg(distinct s.h), '{}'::text[]) from (
    select regexp_replace(lower(m[1]), '^www\.', '') as h
      from regexp_matches(coalesce(p_text, ''),
                          'https?://([^[:space:]/]+)', 'gi') m
    union
    select regexp_replace(lower(m[1]), '^www\.', '')
      from regexp_matches(coalesce(p_text, ''),
                          '((?:www\.)?[a-z0-9][a-z0-9-]*\.(?:com|net|org|io|ru|cn|xyz|biz|cc|su|ir|tk|ml|ga|cf|ly|gg|tv|info|top|me|co|app|dev|link|click|online|site|shop|live|pro|work|store|fun|to))(?![a-z])', 'gi') m
  ) s;
$fn$;
revoke all on function public._loc_url_tokens(text) from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- The row writer as deployed, with the check added beside the line-break one.

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
    v_url_en       text[];
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
    -- A translation may carry only the web addresses its English carries. Two shipped
    -- strings have one, the Patreon and PayPal tips, and their translations keep it;
    -- everywhere else the English has none, so none is allowed. A subset rather than a
    -- yes or no, because "the English has a link" must not become "any link goes here":
    -- that would make those two keys the one place an address could be swapped.
    if public._loc_url_shape(v_text) then
        v_url_en := public._loc_url_tokens(v_english);
        if exists (select 1 from unnest(public._loc_url_tokens(v_text)) u
                    where u <> all (v_url_en)) then
            if coalesce(array_length(v_url_en, 1), 0) = 0 then
                raise exception 'A web address cannot be added where the English has none.'
                      using errcode = 'P0001';
            else
                raise exception 'The only web address allowed here is the one the English uses: %.',
                      array_to_string(v_url_en, ', ') using errcode = 'P0001';
            end if;
        end if;
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
