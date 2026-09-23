-- 0090: lightweight profanity gate on public text (usernames, preset/pack/engine
-- names + descriptions). Server-side + trigger-based so it covers every write
-- path (upload, edit, appeal-reinstate rename) and can't be bypassed by a patched
-- client. Deliberately tight: a small curated list of the worst slurs + hardest
-- profanity, matched after leetspeak normalization, with separators stripped so
-- "f.u.c.k" / "f u c k" / "f4ck" are caught. Terms are chosen to NOT be substrings
-- of common racing/English words (no "ass", "cock", "spec", "tranny", etc.) to
-- avoid the Scunthorpe false-positive problem. Owner tunes the list with one
-- INSERT/DELETE on blocked_terms, no redeploy.

create table if not exists public.blocked_terms (term text primary key);
alter table public.blocked_terms enable row level security;
revoke all on public.blocked_terms from anon, authenticated, public;

insert into public.blocked_terms (term) values
  ('fuck'), ('motherfucker'), ('fucker'), ('shit'), ('bullshit'),
  ('cunt'), ('nigger'), ('nigga'), ('faggot'), ('kike'), ('wetback'),
  ('whore'), ('slut'), ('bitch'), ('twat'), ('wanker'), ('jizz'),
  ('cocksucker'), ('dickhead')
on conflict (term) do nothing;

-- Normalize then substring-match: lowercase, map common leet, drop everything
-- non-alpha (collapses separators/spacing evasion), then look for any term.
create or replace function public._has_blocked_term(p_text text) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  with norm as (
    select regexp_replace(
             translate(lower(coalesce(p_text, '')), '4310$@!5', 'aeiosais'),
             '[^a-z]', '', 'g') as s
  )
  select exists (select 1 from public.blocked_terms b, norm where norm.s like '%' || b.term || '%');
$$;
revoke all on function public._has_blocked_term(text) from anon, authenticated, public;

-- Trigger fns. UPDATE OF name,description means an is_suppressed-only update
-- (mod actions) does NOT re-trigger; only content writes are checked.
create or replace function public._reject_blocked_text() returns trigger
language plpgsql security definer set search_path = public, pg_temp as $$
begin
    if public._has_blocked_term(NEW.name) then
        raise exception 'That name contains a blocked word' using errcode = 'P0001';
    end if;
    if NEW.description is not null and public._has_blocked_term(NEW.description) then
        raise exception 'That description contains a blocked word' using errcode = 'P0001';
    end if;
    return NEW;
end;
$$;
create or replace function public._reject_blocked_username() returns trigger
language plpgsql security definer set search_path = public, pg_temp as $$
begin
    if public._has_blocked_term(NEW.username) then
        raise exception 'That username contains a blocked word' using errcode = 'P0001';
    end if;
    return NEW;
end;
$$;

drop trigger if exists presets_blocked_text on public.presets;
create trigger presets_blocked_text before insert or update of name, description
    on public.presets for each row execute function public._reject_blocked_text();
drop trigger if exists game_presets_blocked_text on public.game_presets;
create trigger game_presets_blocked_text before insert or update of name, description
    on public.game_presets for each row execute function public._reject_blocked_text();
drop trigger if exists custom_engines_blocked_text on public.custom_engines;
create trigger custom_engines_blocked_text before insert or update of name, description
    on public.custom_engines for each row execute function public._reject_blocked_text();
drop trigger if exists packs_blocked_text on public.packs;
create trigger packs_blocked_text before insert or update of name, description
    on public.packs for each row execute function public._reject_blocked_text();
drop trigger if exists profiles_blocked_username on public.profiles;
create trigger profiles_blocked_username before insert or update of username
    on public.profiles for each row execute function public._reject_blocked_username();

-- Username availability gains a 'profanity' reason so the pick-username modal
-- gives instant feedback (it already calls this live as you type).
create or replace function public.check_username_available(p_username text)
returns jsonb language plpgsql security definer set search_path = public, extensions, pg_temp
as $$
declare
    v_lower text;
    v_uid uuid;
    v_existing_owner uuid;
    v_reserved text[] := array[
        'admin','administrator','root','system','support','help',
        'official','trueforce','community','anonymous','moderator',
        'mod','staff','team','owner','dev','test'
    ];
begin
    if p_username is null then
        return jsonb_build_object('available', false, 'reason', 'required');
    end if;
    if not (p_username ~ '^[a-zA-Z0-9_]{3,32}$') then
        return jsonb_build_object('available', false, 'reason', 'format');
    end if;
    v_lower := lower(p_username);
    if v_lower = any(v_reserved) then
        return jsonb_build_object('available', false, 'reason', 'reserved');
    end if;
    if public._has_blocked_term(p_username) then
        return jsonb_build_object('available', false, 'reason', 'profanity');
    end if;
    v_uid := auth.uid();
    select id into v_existing_owner from public.profiles where username_lower = v_lower;
    if v_existing_owner is null then
        return jsonb_build_object('available', true);
    end if;
    if v_uid is not null and v_existing_owner = v_uid then
        return jsonb_build_object('available', true, 'reason', 'self');
    end if;
    return jsonb_build_object('available', false, 'reason', 'taken');
end;
$$;
