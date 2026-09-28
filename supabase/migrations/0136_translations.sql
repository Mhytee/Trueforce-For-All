-- 0136: community translation service (docs/localization-translation-service.md).
--
-- Numbered 0136, not the 0135 the design doc names: 0135 went to
-- telemetry_first_version while this was still on paper. The privacy hooks the doc
-- calls 0136 are 0137.
--
-- Six objects: five tables and one column on profiles. Nothing here alters an
-- existing object, so applying it changes no behaviour until the RPCs land.
-- Idempotent like every file in this folder: safe to apply twice.
set lock_timeout = '3s';
set statement_timeout = '60s';
create extension if not exists "pgcrypto";   -- gen_random_uuid, digest

-- ---------------------------------------------------------------------------
-- The banned-character helper, created first: the translations text check calls
-- it, so it has to exist before the table, and pg_dump restores it in that order
-- for the same reason.
--
-- The characters no language value may carry: C0 except TAB and LF, DEL, C1, and
-- the invisible and bidi controls that let one string render as another. Spelled
-- through chr() rather than as a bracket-expression escape, which silently
-- matches nothing. chr(0) is absent because a Postgres text value cannot hold
-- NUL at all.
create or replace function public._loc_banned_chars()
returns text language sql immutable as $$
  select string_agg(chr(c), '' order by c) from (
    select generate_series(1, 8) as c                 -- C0 below TAB
    union all select 11 union all select 12           -- VT, FF
    union all select generate_series(14, 31)          -- C0 above CR
    union all select 127                              -- DEL
    union all select generate_series(128, 159)        -- C1
    union all select generate_series(8203, 8207)      -- ZWSP, ZWNJ, ZWJ, LRM, RLM
    union all select 8232 union all select 8233       -- LS, PS
    union all select generate_series(8234, 8238)      -- LRE, RLE, PDF, LRO, RLO
    union all select generate_series(8294, 8297)      -- LRI, RLI, FSI, PDI
    union all select 65279                            -- ZWNBSP, a stray BOM
  ) t;
$$;
-- Revoking EXECUTE cannot break an insert: every write goes through a security
-- definer function owned by the role that owns this helper, so the privilege
-- check inside the constraint runs as that owner.
revoke all on function public._loc_banned_chars() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Which languages are open.
create table if not exists public.translation_cultures (
    tag           text primary key
                  check (tag ~ '^[a-z]{2,3}(-[A-Z][a-z]{3})?(-[A-Z]{2})?$' and tag <> 'en'),
    name          text not null check (length(btrim(name)) between 1 and 60),
    -- false pauses new submissions only. It never affects serving: approved rows
    -- keep reaching every install while edits are shut.
    accepting     boolean not null default true,
    -- 'open' ships (owner, 2026-09-27): a submitted row serves, and a bad one is
    -- repaired rather than prevented. The other three stay in the check so
    -- turning a gate on later is an update, not a migration.
    review_policy text not null default 'open'
                  check (review_policy in ('open','reviewer','trusted','agreement')),
    revision      bigint not null default 0,       -- the plugin's ETag
    updated_at    timestamptz not null default now()
);
-- 'Espanol' with n-tilde, written through chr() so this file stays ASCII like
-- every other one in this folder. Seeded closed: a language opens when its
-- machine pass ships as Languages\<tag>.json, so nobody is handed 2000 blank
-- rows. Spanish ships complete, so the runbook opens it after the English push.
insert into public.translation_cultures (tag, name, accepting)
     values ('es', 'Espa' || chr(241) || 'ol', false)
     on conflict (tag) do nothing;

-- ---------------------------------------------------------------------------
-- The server's copy of Languages\en.json. Without it a signed-in caller could
-- invent keys, languages and the English a reviewer is shown, and the server
-- could not check placeholder parity or compute progress.
create table if not exists public.english_strings (
    key           text primary key
                  check (key ~ '^[A-Za-z][A-Za-z0-9_.]{0,127}$'),
    text          text not null check (length(text) between 1 and 4000),
    -- extensions.digest: where this project's pgcrypto lives, probed before
    -- writing this file. A generated column resolves the function at DDL time,
    -- so a wrong qualification fails this create table outright.
    sha256        text generated always as
                  (encode(extensions.digest(text, 'sha256'), 'hex')) stored,
    context       jsonb,          -- area, siblingNames, helpText, ownToolTip
    first_version text not null,  -- the push that introduced the key
    version       text not null,  -- the last push that carried it
    retired_at    timestamptz,    -- set when a push stops carrying it
    updated_at    timestamptz not null default now()
);
create index if not exists english_strings_live_idx
    on public.english_strings (key) where retired_at is null;

-- Every English a key has ever had, so a reviewer can be shown the text a
-- submission actually translated. Written by upsert_english_strings. Keys are
-- never deleted: a push that stops carrying one stamps retired_at, which keeps
-- an older build's translation of that key valid.
create table if not exists public.english_strings_history (
    key         text not null,
    sha256      text not null check (sha256 ~ '^[0-9a-f]{64}$'),
    text        text not null,
    version     text not null,
    replaced_at timestamptz not null default now(),
    primary key (key, sha256)
);

-- ---------------------------------------------------------------------------
-- The translations themselves.
create table if not exists public.translations (
    id              uuid primary key default gen_random_uuid(),
    culture         text not null references public.translation_cultures(tag)
                    on update cascade,
    key             text not null check (key ~ '^[A-Za-z][A-Za-z0-9_.]{0,127}$'),
    text            text not null
                    check (length(text) between 1 and 2000
                           and text = translate(text, public._loc_banned_chars(), '')
                           and position(chr(8212) in text) = 0   -- U+2014 em dash
                           and position('--' in text) = 0),
    english_sha256  text not null check (english_sha256 ~ '^[0-9a-f]{64}$'),
    -- on delete set null is what lets approved text survive an account deletion
    -- without a name, the rule presets already follow. Without it the delete
    -- inside delete_my_account fails for anyone who ever had a translation
    -- approved, which is exactly the contributor this service recruits.
    submitter       uuid references auth.users(id) on delete set null,
    reviewer        uuid references auth.users(id) on delete set null,
    -- The row a reviewer cloned when accepting a stale row or approving with an
    -- edit, so the queue can show provenance and credit stays traceable.
    cloned_from     uuid references public.translations(id) on delete set null,
    source          text not null check (source in ('web','plugin')),
    plugin_version  text check (plugin_version is null or length(plugin_version) <= 32),
    note            text check (note is null or length(note) <= 500),
    review_note     text check (review_note is null or length(review_note) <= 500),
    -- Admits the literal 'redacted' beside a digest because that is what 0137
    -- writes on account deletion. A check that admitted only the digest would
    -- make delete_my_account raise for anyone who ever submitted.
    source_ip_hash  text not null
                    check (source_ip_hash = 'redacted'
                           or source_ip_hash ~ '^[0-9a-f]{64}$'),
    -- The default stays 'pending' even though review_policy is 'open': nothing
    -- but the RPC should be able to put text in front of users, so an insert
    -- that forgets to say 'approved' goes nowhere rather than going live.
    status          text not null default 'pending'
                    check (status in ('pending','approved','superseded','rejected','withdrawn')),
    shipped_version text check (shipped_version is null or length(shipped_version) <= 32),
    created_at      timestamptz not null default now(),
    submitted_at    timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    reviewed_at     timestamptz
);

-- One approved row per (culture, key, English). Not per key: dev, beta and
-- stable display different English at once.
create unique index if not exists translations_one_approved_per_english
    on public.translations (culture, key, english_sha256) where status = 'approved';
-- One open proposal per translator per key; submit upserts into it.
create unique index if not exists translations_one_pending_per_submitter
    on public.translations (culture, key, submitter) where status = 'pending';
-- The three query indexes: the reviewer queue, "my rows", and per-key reads.
create index if not exists translations_queue_idx
    on public.translations (culture, status, created_at);
create index if not exists translations_submitter_idx
    on public.translations (submitter, updated_at desc);
create index if not exists translations_key_idx
    on public.translations (culture, key, status);

-- Three timestamps, three jobs. created_at is the first insert. submitted_at is
-- written only by the submit path, and the hourly cap counts on it, so a
-- reviewer approving 300 rows cannot spend a translator's hourly budget.
-- updated_at is maintained for every write by this trigger.
create or replace function public._translations_touch()
returns trigger language plpgsql as $$
begin new.updated_at := now(); return new; end $$;
revoke all on function public._translations_touch() from public, anon, authenticated;
drop trigger if exists translations_touch on public.translations;
create trigger translations_touch before update on public.translations
    for each row execute function public._translations_touch();

-- ---------------------------------------------------------------------------
-- Reviewers. Rows are written only with the service key, in Studio, never in a
-- migration file: this repository is public and the owner's uid does not belong
-- in git. Appointing a reviewer widens "owner-approved" to "approved by a
-- reviewer the owner appointed for that language", which is the point: the owner
-- cannot review a language he does not read.
create table if not exists public.translation_reviewers (
    user_id  uuid primary key references auth.users(id) on delete cascade,
    cultures text[],                 -- null = every language
    added_at timestamptz not null default now(),
    added_by text                    -- 'studio' or a reviewer uuid
);

-- ---------------------------------------------------------------------------
-- The sixth object. Its ACL is the table's, and that means the flag is readable:
-- 0023 revoked the anon profiles read and granted select to authenticated, so
-- any signed-in caller can read this column for any account. Adding a column
-- does not re-run Supabase's default privileges, so no grant or revoke is needed
-- and none should be added. The write side is closed: no client role holds
-- update on profiles, so honouring an opt-out is one service-key update.
alter table public.profiles
    add column if not exists hide_from_translation_credits boolean not null default false;

-- ---------------------------------------------------------------------------
-- Row level security and grants.
alter table public.translation_cultures    enable row level security;
alter table public.english_strings         enable row level security;
alter table public.english_strings_history enable row level security;
alter table public.translations            enable row level security;
alter table public.translation_reviewers   enable row level security;

-- Supabase default privileges grant every new relation and function to anon and
-- authenticated at creation (0107, 0129, 0132). "from public" alone does not
-- touch a named role. Revoke by name.
revoke all on table public.translation_cultures, public.english_strings,
                    public.english_strings_history, public.translations,
                    public.translation_reviewers
       from public, anon, authenticated;

-- service_role holds this through the same defaults, and the runbook's
-- moderation updates need more than select. Stated so the intended reader
-- survives a future tightening (the 0132 habit).
grant all on table public.translation_cultures, public.english_strings,
                   public.english_strings_history, public.translations,
                   public.translation_reviewers
      to service_role;

-- The reviewer test. The grant to authenticated exists because the policy below
-- evaluates it as the caller.
create or replace function public._is_translation_reviewer(p_culture text default null)
returns boolean language sql stable security definer
set search_path = public, pg_temp as $$
  select exists (
    select 1 from public.translation_reviewers r
     where r.user_id = auth.uid()
       and (r.cultures is null or p_culture is null or p_culture = any(r.cultures)));
$$;
revoke all on function public._is_translation_reviewer(text) from public, anon;
grant execute on function public._is_translation_reviewer(text) to authenticated;

-- Second lock behind the revoke: no grant makes this policy reachable today, and
-- a restored grant still exposes only your own rows or your languages. There is
-- no anon policy on any of the six objects.
drop policy if exists translations_select_own_or_review on public.translations;
create policy translations_select_own_or_review
    on public.translations for select to authenticated
    using (submitter = auth.uid() or public._is_translation_reviewer(culture));

-- ---------------------------------------------------------------------------
-- The revision counter: the plugin's revalidation token, because PostgREST
-- computes no ETag on an RPC response. Fires whenever old.status or new.status
-- is 'approved', which covers a clone insert, an approval, a revoke and a
-- superseded row stepping down. The culture is read through a tg_op case rather
-- than coalesce(new.culture, old.culture), because NEW is unassigned in a DELETE
-- branch and reading it there raises. That branch exists only for a hand-run
-- service-key delete: no RPC deletes an approved row.
create or replace function public._translations_bump_revision()
returns trigger language plpgsql security definer set search_path = public, pg_temp as $$
begin
    if (tg_op = 'INSERT' and new.status = 'approved')
       or (tg_op = 'UPDATE' and ((old.status = 'approved') <> (new.status = 'approved')
                                 or (new.status = 'approved' and old.text <> new.text)))
       or (tg_op = 'DELETE' and old.status = 'approved') then
        update public.translation_cultures
           set revision = revision + 1, updated_at = now()
         where tag = case tg_op when 'DELETE' then old.culture
                                else new.culture end;
    end if;
    return null;
end $$;
revoke all on function public._translations_bump_revision() from public, anon, authenticated;
drop trigger if exists translations_bump_revision on public.translations;
create trigger translations_bump_revision
    after insert or update or delete on public.translations
    for each row execute function public._translations_bump_revision();
