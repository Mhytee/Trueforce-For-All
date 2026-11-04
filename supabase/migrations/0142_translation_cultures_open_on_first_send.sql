-- 0142: every language is open, and one opens itself on the first send
-- (owner, 2026-11-03).
--
-- 0136 seeded a language closed and opened it when its machine pass shipped, so that
-- nobody picking an untranslated language was handed three thousand blank rows. The
-- shipped copy makes the opposite promise, that a language nobody has started can be
-- started, and the picker lets a person type any tag at all, so no list held on this
-- side could cover what they are able to ask for. Four of the five languages that
-- ship translated were refused outright because only Spanish was ever opened.
--
-- So opening moves to the first row sent. What is new is a ceiling, because opening
-- is now something a stranger can do: four hundred languages in all, five a day per
-- submitter. The existing per-hour caps bound the rows, not the languages, and a
-- user-visible list is worth keeping clean.
--
-- opened_by records the account or the 'anon:' id that opened a language, for the
-- same reason submitter is recorded on a row: a language opened in bad faith has to
-- be traceable to something submitter_blocked can match. The table is readable only
-- through security definer functions (0136 revokes it from anon and authenticated),
-- and none of them return this column.
set lock_timeout = '3s';
set statement_timeout = '60s';

alter table public.translation_cultures
      add column if not exists opened_by text,
      add column if not exists opened_at timestamptz;

-- ---------------------------------------------------------------------------
-- The four languages that ship translated alongside Spanish. Opened here rather
-- than left to someone's first correction, because we started them, not a stranger.
-- Written through chr() so this file stays ASCII like every other one in this folder.
insert into public.translation_cultures (tag, name, accepting) values
    ('de', 'Deutsch', true),
    ('pt', 'Portugu' || chr(234) || 's', true),
    ('zh', chr(31616) || chr(20307) || chr(20013) || chr(25991), true),
    ('ru', chr(1056) || chr(1091) || chr(1089) || chr(1089) || chr(1082)
           || chr(1080) || chr(1081), true)
     on conflict (tag) do update set accepting = true;

-- ---------------------------------------------------------------------------
-- The row writer is untouched. This is 0139's function with 0141's English lookup,
-- as deployed, with the culture gate replaced.

CREATE OR REPLACE FUNCTION public.submit_translations(p_culture text, p_rows jsonb, p_source text DEFAULT 'plugin'::text, p_plugin_version text DEFAULT NULL::text, p_anon_id text DEFAULT NULL::text)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
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

    -- A language opens on the first row sent to it. The tag needs no new check: the
    -- table's own check demands a lowercase language with an optional titlecase script and
    -- uppercase region, so 'pt-BR' and 'zh-Hans' open and a typo opens nothing.
    select c.accepting into v_accepting
      from public.translation_cultures c where c.tag = p_culture;
    if v_accepting is null then
        if (select count(*) from public.translation_cultures) >= 400 then
            raise exception 'No more languages can be opened right now. Ask on Discord.'
                  using errcode = 'P0001';
        end if;
        if (select count(*) from public.translation_cultures c
             where c.opened_by = v_who
               and c.opened_at > now() - interval '1 day') >= 5 then
            raise exception 'That is five new languages in a day. Ask on Discord to open another.'
                  using errcode = 'P0001';
        end if;
        begin
            insert into public.translation_cultures (tag, name, accepting, opened_by, opened_at)
                 values (p_culture, p_culture, true, v_who, now());
        exception
            -- The tag failed the table's shape check, which is the only thing that
            -- stands between a language code and a typo.
            when check_violation then
                raise exception 'That is not a language code this can open. Use a code like pt or pt-BR.'
                      using errcode = 'P0001';
            -- Two first sends raced. The other one opened it, so carry on.
            when unique_violation then null;
        end;
        select c.accepting into v_accepting
          from public.translation_cultures c where c.tag = p_culture;
        if v_accepting is null then
            raise exception 'That language could not be opened. Ask on Discord.'
                  using errcode = 'P0001';
        end if;
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
end $function$;

revoke all on function public.submit_translations(text, jsonb, text, text, text) from public;
grant execute on function public.submit_translations(text, jsonb, text, text, text)
      to anon, authenticated;
