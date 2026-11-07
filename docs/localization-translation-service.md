# Community translation service

How to build and operate the service behind Phase 3b of
`docs/localization-plan.md`: a translator fixes a string, a reviewer the
owner appointed for that language approves it, and every install of that
language picks it up within a day with no release. The plan owns the
decisions. This file owns the build: the schema, the RPC contract, the
web page, the plugin client, the scripts, the apply checklist and the
operations runbook.

Everything below was checked against `dev` on 2026-09-25, while the tab
conversions were landing one commit at a time. Any count of keys or of
commits here is stale by construction: `en.json` passed 790 keys with the
Effects tab and reaches about 2,000 when the conversion finishes, so
measure the tree rather than trusting a number in this file. Source line
numbers are hints; grep for the named symbol.

Migrations 0136 and 0137 carry this, APPLIED 2026-09-27: 0135 went to
telemetry_first_version while this was on paper, so the schema landed as
`0136_translations.sql` and the RPC contract as `0137_translation_rpcs.sql`,
which is one file more than this doc's plan and the same objects. The privacy
hooks this doc calls 0136 are therefore 0138, and are NOT built yet. No new Edge Function: the Discord nudge reuses
`report-notify` with a new payload kind and its own channel secret.

---

## 1. What the service is, and the trust model

**Decision, 2026-09-28: translating needs no account (migration 0139).** The
person who notices a bad Spanish label is not the person who wanted an
account, and an account was never what protected this: email OTP with
`create_user = true` makes one cost a disposable address, which is why three
of section 6's caps already key on something else. A submission now follows
`submit_car_fact`: the account wins when the request carries a token, else a
client-minted id arrives as `p_anon_id` and is stored in a new `anon_id`
column as `'anon:<id>'`, and the hashed address is the backstop because a
self-chosen id costs nothing. `submitter_blocked` matches all three forms. The
plugin sends the `CarFactsAnonId` it already mints and already carries in a
backup, so one person stays one contributor across their machines. What an
account still buys is a name in the credits; an anonymous row collapses into
`(anonymous)`.

Two consequences to hold onto. The anon key ships inside the plugin, so the
barrier to writing live text is now reading a DLL rather than registering an
address, and with no review gate the defence is entirely the caps, the blocked
table, the English hash and the length and character checks. And
`translations_one_pending_per_submitter` does not cover `anon_id`, which is
moot while `review_policy` is `'open'` and nothing is ever pending, but would
need an index before a gate is ever switched on.

**Decision, 2026-09-28: one surface, not two. The web page is dropped.**
Everyone who would translate this plugin runs it, and the in-plugin window
is the better tool anyway: it shows each string in the real control while
the translator types, which a page cannot do. What the page was carrying
and now is not: a contributor without SimHub, a public progress and credits
display, and the reviewer mode. Repair is therefore service-key SQL rather
than a screen, which the owner already does for every schema change. Section
7 below is kept as a record of what was designed, not as work to do.

The in-plugin Translate window is the only surface that writes. It sends
`{k, t, h}` rows to `submit_translations`, which is the contract the page
would also have used, so nothing in the schema or the RPCs assumes one
client.

**Decision, 2026-09-27: there is no review gate.** A submitted row is what
installs receive. Everything below that reads "a reviewer approves" is the
repair path now, not a precondition: the owner uses it to put a bad row
back, and `status = 'approved'` keeps its meaning of "this is what installs
receive" rather than "a person blessed this". What that decision leans on,
and what therefore has to be built rather than deferred: the English hash
on every row, the history table, the submitter on every row, the rate
limits in section 6, the banned-character helper in section 2.1 and the
length caps. Those are the whole of the defence now. The one property the
gate was carrying that nothing else does is that a language at 100 percent
is no longer evidence that anyone read it, so the plugin's own progress
figure says how much exists, never how good it is.

Four properties hold the design together.

**The server holds the English.** `english_strings` is the server's copy
of `Languages\en.json`, pushed with the service key. Without it a
signed-in caller could invent keys, languages and the English a reviewer
is shown, and the server could not check placeholder parity or compute
progress.

**Every row names the English it translated,** by the SHA-256 of that
exact value (section 4). That is what makes a mixed-version fleet safe:
an install applies a row only when the row's hash equals the hash of the
English that install actually displays.

**Approval is the only gate to the fleet.** `translations` is deny-all to
both client roles. Anon reaches approved rows through one read function
and nothing else. A reviewer is a row in `translation_reviewers`, scoped
to the languages the owner appointed them for.

**Each side treats the other's text as hostile.** The reviewer's browser
builds every network-supplied string with `textContent` under a
`default-src 'none'` policy. The plugin re-checks every fetched row
against its own English before it reaches a label, and a localized value
never becomes a URL, a path or a process argument (section 8.5).

What the plugin sends when it reads: the language tag, a revision number
and the public anon key. Never the user's token, so a translation fetch
is not joinable to an account.

---

## 2. Schema (migration 0136, applied)

Six objects: five new tables and one column on `profiles`. Header in the
house style of 0134, no `begin`/`commit`:

```sql
-- 0135: community translation service (docs/localization-translation-service.md).
set lock_timeout = '3s';
set statement_timeout = '60s';
create extension if not exists "pgcrypto";   -- gen_random_uuid, digest
```

### 2.1 The banned-character helper, created first

The `translations` text check calls this, so it has to exist before the
table, and `pg_dump` restores it in that order for the same reason.

```sql
-- The characters no language value may carry: C0 except TAB and LF, DEL,
-- C1, and the invisible and bidi controls that let one string render as
-- another. Spelled through chr() rather than as a bracket-expression
-- escape, which silently matches nothing. chr(0) is absent because a
-- Postgres text value cannot hold NUL at all.
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
revoke all on function public._loc_banned_chars() from public, anon, authenticated;
```

Revoking EXECUTE cannot break an insert: every write goes through a
`security definer` function owned by the role that owns this helper, so
the privilege check inside the constraint runs as that owner.

Changing the list later does not revalidate stored rows. A change is
therefore a new constraint plus a validation pass, not an edit to this
body, and the same list is pre-checked in the RPC so the readable message
and the constraint cannot drift.

### 2.2 translation_cultures: which languages are open

```sql
create table if not exists public.translation_cultures (
    tag           text primary key
                  check (tag ~ '^[a-z]{2,3}(-[A-Z][a-z]{3})?(-[A-Z]{2})?$' and tag <> 'en'),
    name          text not null check (length(btrim(name)) between 1 and 60),
    -- false pauses new submissions only. It never affects serving:
    -- approved rows keep reaching every install while edits are shut.
    accepting     boolean not null default true,
    -- 'open' ships (owner, 2026-09-27): a submitted row serves, and a bad
    -- one is repaired rather than prevented. The other three stay in the
    -- check so turning a gate on later is an update, not a migration.
    review_policy text not null default 'open'
                  check (review_policy in ('open','reviewer','trusted','agreement')),
    revision      bigint not null default 0,       -- the plugin's ETag
    updated_at    timestamptz not null default now()
);
-- 'Espanol' with n-tilde, written through chr() so the migration file
-- stays ASCII like every other one in this folder. Seeded closed: a
-- language opens when its machine pass ships as Languages\<tag>.json, so
-- nobody is handed 2000 blank rows.
insert into public.translation_cultures (tag, name, accepting)
     values ('es', 'Espa' || chr(241) || 'ol', false)
     on conflict (tag) do nothing;
```

The regex admits every tag the plan names: the six SimHub ships
(`de-DE`, `fr-FR`, `it`, `ko-KR`, `ru-RU`, `zh-Hans-CN`), plus `es`,
which SimHub ships no translation for and which is exactly why Spanish is
the launch language. It also admits regional children such as `es-MX`,
and refuses `en`, `ES`, `es_MX`, `qps-ploc` and anything that could reach
a path. `review_policy` ships `'open'`, so `submit_translations` writes the
row at `'approved'` directly and every read path, index and revision bump
below works unchanged; switching a single language to `'reviewer'` later is
an update rather than a migration.

Two mechanics follow from `'open'` and have to be built that way.
**`submit_translations` supersedes as it writes.** The unique index admits one
approved row per `(culture, key, english_sha256)`, so a second person
translating a key that already has live text means the RPC sets the existing
row to `'superseded'` in the same statement as the insert, which is what
leaves the previous text recoverable and the index satisfied. Under a review
gate the reviewer's accept did this; now the submit does.
**The column default stays `'pending'`.** Nothing but the RPC should be able
to put text in front of users, so an insert that forgets to say `'approved'`
goes nowhere rather than going live.

### 2.3 english_strings and its history

```sql
create table if not exists public.english_strings (
    key           text primary key
                  check (key ~ '^[A-Za-z][A-Za-z0-9_.]{0,127}$'),
    text          text not null check (length(text) between 1 and 4000),
    -- <pgcrypto> is the schema apply-checklist step 1 probes. Write
    -- what that probe returns: a generated column resolves the function
    -- at DDL time, so a wrong qualification fails this create table.
    sha256        text generated always as
                  (encode(<pgcrypto>.digest(text, 'sha256'), 'hex')) stored,
    context       jsonb,          -- area, siblingNames, helpText, ownToolTip
    first_version text not null,  -- the push that introduced the key
    version       text not null,  -- the last push that carried it
    retired_at    timestamptz,    -- set when a push stops carrying it
    updated_at    timestamptz not null default now()
);
create index if not exists english_strings_live_idx
    on public.english_strings (key) where retired_at is null;

-- Every English a key has ever had, so a reviewer can be shown the text a
-- submission actually translated. Written by upsert_english_strings.
create table if not exists public.english_strings_history (
    key         text not null,
    sha256      text not null check (sha256 ~ '^[0-9a-f]{64}$'),
    text        text not null,
    version     text not null,
    replaced_at timestamptz not null default now(),
    primary key (key, sha256)
);
```

`<pgcrypto>.digest` is a placeholder for the schema step 1 of the apply
checklist proves. No migration in this repo qualifies `digest` at all,
`0001` creates pgcrypto with no `with schema`, and a generated column
resolves the function at DDL time, so a wrong qualification fails the
`create table` outright. The hash vectors catch a wrong encoding, not a
wrong schema, which is why the probe comes first.

Keys are never deleted. A push that no longer carries a key stamps
`retired_at`, which keeps an older build's translation of that key valid.

The key regex is `LocStore.IsValidKey` (letters, digits, underscore, dot,
starting with a letter) with a 128-character cap, because the runtime
allows the dot for the coming `.one` and `.other` plurals. The server and
the page use that form. `tools/loc/README.md` documents the narrower
`^[A-Za-z][A-Za-z0-9_]*$`, which stays the writer-side rule for new keys
and is what `validate.ps1` enforces alongside the plural-suffix form; the
README gains one sentence naming the runtime form and the cap rather than
widening its own rule, and `LocKeyRuleFormsAgree` in Core.Tests asserts
the three agree.

### 2.4 translations, in full

```sql
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
    submitter       uuid references auth.users(id) on delete set null,
    reviewer        uuid references auth.users(id) on delete set null,
    cloned_from     uuid references public.translations(id) on delete set null,
    source          text not null check (source in ('web','plugin')),
    plugin_version  text check (plugin_version is null or length(plugin_version) <= 32),
    note            text check (note is null or length(note) <= 500),
    review_note     text check (review_note is null or length(review_note) <= 500),
    source_ip_hash  text not null
                    check (source_ip_hash = 'redacted'
                           or source_ip_hash ~ '^[0-9a-f]{64}$'),
    status          text not null default 'pending'
                    check (status in ('pending','approved','superseded','rejected','withdrawn')),
    shipped_version text check (shipped_version is null or length(shipped_version) <= 32),
    created_at      timestamptz not null default now(),
    submitted_at    timestamptz not null default now(),
    updated_at      timestamptz not null default now(),
    reviewed_at     timestamptz
);

-- One approved row per (culture, key, English). Not per key: dev, beta and
-- stable display different English at once (section 5).
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
```

`submitter on delete set null` is what lets approved text survive an
account deletion without a name, the rule presets already follow. Without
it `delete from auth.users` inside `delete_my_account` fails for anyone
who ever had a translation approved, which is exactly the contributor
this service recruits.

`source_ip_hash` admits the literal `'redacted'` beside a 64-hex digest
because that is what 0136 writes on account deletion (section 10.6). A
check that admitted only the digest pattern would make
`delete_my_account` raise for anyone who ever submitted, which is the
same account-deletion break `submitter on delete set null` fixes one
paragraph up. Every other new column was checked for the same shape of
conflict: 0136 writes no other column, and `tf4all-translation-gc` writes
only `status`, whose vocabulary already holds `superseded`, and a
`review_note` well inside its 500-character cap.

`cloned_from` holds the id of the row a reviewer cloned when accepting a
stale row or approving with an edit, so the queue can show provenance and
credit stays traceable.

**Status vocabulary.** `pending` waits for review. `approved` is what the
fleet gets. `superseded` was approved once and stepped down, or was
pending when a reviewer approved its text, edited or not, as a new row,
and earns credit either
way. `rejected` carries a note the submitter reads. `withdrawn` was
pulled by the submitter. Nothing is deleted except a deleted account's
non-approved rows.

**Three timestamps, three jobs.** `created_at` is the first insert.
`submitted_at` is written only by the submit path, and the hourly cap
counts on it, so a reviewer approving 300 rows cannot spend a
translator's hourly budget. `updated_at` is maintained for every write by
one trigger:

```sql
create or replace function public._translations_touch()
returns trigger language plpgsql as $$
begin new.updated_at := now(); return new; end $$;
revoke all on function public._translations_touch() from public, anon, authenticated;
drop trigger if exists translations_touch on public.translations;
create trigger translations_touch before update on public.translations
    for each row execute function public._translations_touch();
```

### 2.5 translation_reviewers

```sql
create table if not exists public.translation_reviewers (
    user_id  uuid primary key references auth.users(id) on delete cascade,
    cultures text[],                 -- null = every language
    added_at timestamptz not null default now(),
    added_by text                    -- 'studio' or a reviewer uuid
);
```

Rows are written only with the service key, in Studio, never in a
migration file: this repository is public and the owner's uid does not
belong in git. The owner is the first row. Every other owner-only path in
this schema is service_role, which cannot serve a web page where the
owner signs in with the same OTP flow as everyone else, and the page must
never carry the service key.

Appointing a reviewer widens "owner-approved" to "approved by a reviewer
the owner appointed for that language". That is the point: the owner
cannot review a language he does not read.

### 2.6 The sixth object: profiles.hide_from_translation_credits

```sql
alter table public.profiles
    add column if not exists hide_from_translation_credits boolean not null default false;
```

Its ACL is the table's, and that means the flag is readable. `0023`
revoked the anon `profiles` read and granted `select` to `authenticated`
under a `using (true)` policy, so any signed-in caller can read this
column for any account. Adding a column does not re-run Supabase's
default privileges, which apply to new relations, so no grant or revoke
is needed and none should be added. Only the write side is closed: no
client role holds `update` on `profiles` and `set_username` writes only
`username`, so honoring an opt-out is one service-key update in Studio
(section 10.5). What the flag protects is the credits list,
which `translation_contributors` builds as a definer and which skips
those accounts. It is not a secret, and no RPC returns it.

### 2.7 Row level security and grants

```sql
alter table public.translation_cultures    enable row level security;
alter table public.english_strings         enable row level security;
alter table public.english_strings_history enable row level security;
alter table public.translations            enable row level security;
alter table public.translation_reviewers   enable row level security;

-- Supabase default privileges grant every new relation and function to anon
-- and authenticated at creation (0107, 0129, 0132). "from public" alone does
-- not touch a named role. Revoke by name.
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

-- Second lock behind the revoke: no grant makes this policy reachable today,
-- and a restored grant still exposes only your own rows or your languages.
drop policy if exists translations_select_own_or_review on public.translations;
create policy translations_select_own_or_review
    on public.translations for select to authenticated
    using (submitter = auth.uid() or public._is_translation_reviewer(culture));
```

There is no anon policy on any of the six objects, and anonymous sign-ins
stay disabled: every write and reviewer RPC raises on an `is_anonymous`
claim. A `security_invoker` view of approved rows was considered and
rejected: it would need a grant to anon plus an anon policy, and it could
not do the revision check the read function does.

### 2.8 The reviewer test

```sql
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
```

The grant to `authenticated` exists because the policy above evaluates it
as the caller.

### 2.9 The revision counter

One row trigger, and the only trigger that posts nothing:

```sql
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
```

It fires on insert, update and delete whenever `old.status` or
`new.status` is `'approved'`, which covers a clone insert, an approval, a
revoke and a superseded row stepping down. The culture is read through a
`tg_op` case rather than `coalesce(new.culture, old.culture)`, because
NEW is unassigned in a DELETE branch and reading it there raises. That
branch exists only for a hand-run service-key delete: no RPC deletes an
approved row. PostgREST computes no ETag on an RPC response, so this
counter is the revalidation token the plugin sends back.

---

## 3. The RPC contract (migration 0137, applied)

All `security definer`, `set search_path = public, extensions, pg_temp`,
identity from `auth.uid()` and never from the body. Each is created with
`create or replace` and then has its ACL set by name. The four
anon-callable reads are `stable`.

**Wire shape, once for both clients.** A submission row is
`{k, t, h, n}`: key, text, the English hash, an optional note. At most 50
rows per call. No English text in a submission: the server holds every
English a key has had, so `h` alone says which one was translated.

Every refusal is `raise exception ... using errcode = 'P0001'` with a
sentence both clients show verbatim. The RPC pre-checks length, the
character list, the em dash and the hyphen pair, so a translator who
types an em dash reads a sentence rather than
`violates check constraint`; the table check stays as the backstop.

### 3.1 Writes (authenticated)

`submit_translations(p_culture text, p_rows jsonb, p_source text default
'plugin', p_plugin_version text default null) returns jsonb`

Returns `{ok, accepted:[{k, status}], rejected:[{k, error}]}`, where
`status` is `pending` or `already_approved`, in the spirit of
0131's `telemetry_ping`, which returns `{ok, settings, games, presets}`
rather than an accepted-and-rejected pair. Its single-row body
`_submit_translation_row(...)` is revoked from every role and called
inside a per-row `begin ... exception when others` block, so one bad row
does not abort the batch.

Checks before the row loop, so a batch cannot straddle a cap:

```sql
if v_uid is null then
    -- 0139: gone. A caller with no token and no client id gets
    -- 'This copy of the plugin cannot send translations. Update it, or sign in.',
    -- which is reachable only from a client too old to send either.
    raise exception 'Sign in to send translations.' using errcode = 'P0001';
end if;
if coalesce((auth.jwt() ->> 'is_anonymous')::boolean, false) then
    raise exception 'Sign in with an email address to send translations.' using errcode = 'P0001';
end if;
v_ip_hash := encode(digest(_client_ip(), 'sha256'), 'hex');
if exists (select 1 from public.submitter_blocked b
            where b.submitter_id in (v_uid::text, 'ip:' || v_ip_hash)
              and (b.banned_until is null or b.banned_until > now())) then
    raise exception 'This account cannot send translations.' using errcode = 'P0001';
end if;
```

Both forms of the block are checked, the account uuid and
`'ip:' || <sha256>`, because a browser session carries no device code and
that is the only handle a web-only abuser leaves. `submitter_blocked` is
the same table `submit_car_fact` consults, and `_client_ip()` reads
`cf-connecting-ip` from PostgREST's request headers and collapses to
`'unknown'` when it is absent, so a browser call reaches the per-address
cap exactly as the plugin does.

Then, still before the loop: the culture must exist and be `accepting`;
`p_rows` must be a JSON array of at most 50 objects; the caller's
`auth.users.created_at` must be at least 10 minutes old; the hourly and
pending caps of section 6 are counted once against rows already in the
window.

Per row: the key must exist in `english_strings` and not be retired;
`h` must equal the current `sha256` or a row in `english_strings_history`
for that key; CRLF and lone CR become LF and nothing else is normalized;
the text must be non-empty after `btrim`, within 2,000 characters and
within `greatest(64, 4 * length(english))`; it may carry a line break
only if its English does; `{n}` placeholder sets must be equal as
multisets; `_has_blocked_term` must pass on the text and on the row's
`n`. A
row whose text already equals the approved row returns
`status: 'already_approved'` rather than a rejection. Otherwise the row
upserts into the caller's one pending row for that key, writing
`submitted_at = now()`.

Refusal sentences, verbatim:

- `That language is not open for translation yet. Ask on Discord to open it.`
- `That language is closed for edits right now.`
- `Fifty rows per send. Split the rest into another send.`
- `A new account can send translations ten minutes after it is created.`
- `That is 300 translations in an hour. The rest can go in an hour.`
- `You have 3,000 translations waiting for review in this language. Wait
  for a review before sending more.`
- `This connection has sent 600 translations in an hour. Try again later.`
- `This language has 20,000 translations waiting for review. Try again
  once the queue moves.`
- `This key is not in the English the server holds. Reload and try again.`
- `The English for this key moved. Reload the page and translate
  the current text.`
- `A translation cannot be empty. Clear the row instead, so the key
  falls back to English.`
- `A translation must be 2000 characters or fewer.`
- `That is more than four times the length of the English. Shorten it.`
- `The English here has no line break, so the translation cannot have one.`
- `The placeholders must match the English exactly: {0}, {1}.`
- `An em dash is not allowed here. Use a comma or a colon.`
- `Two hyphens in a row are not allowed here.`
- `That text carries an invisible or control character that is not allowed.`
- `That note contains a blocked word.`

`withdraw_translations(p_ids uuid[]) returns jsonb`. Own pending rows
only, set to `withdrawn`. Same receipt shape.

### 3.2 Review, which is now the repair path (authenticated, gated on the language)

With `review_policy = 'open'` nothing waits here. This section is what the
owner reaches for when a row needs putting back: `reject` the bad row, then
`approve` the superseded one that held the previous text for that key and
English hash. It is described as a gate below because it was designed as
one, and the operations are the same either way.

`review_translations(p_ids uuid[], p_action text, p_text text default
null, p_note text default null) returns jsonb`

Returns `{ok, acted:[ids], needs_confirm:[ids], rejected:[{id, error}]}`,
so a mixed batch is defined and the Discord line reports `acted` length.
Four actions, of which three approve, and each of those three leaves the
source row in a stated status. Below, `H` is the hash the source row
names and `C` the current English hash for that key.

- `approve`: `H` must equal `C`. Any row already `approved` at
  `(culture, key, C)` steps down to `superseded`, the source row becomes
  `approved`, and identical pending rows from other translators become
  `superseded` with credit. A row whose `H` is an older English is not
  approved and its id comes back under `needs_confirm`, with
  `english_then` and `english_now` for the confirm dialog.
- `approve_with_edit` (one id, `p_text` required): inserts the reviewer's
  text as a new `approved` row at `C`, keeping the original `submitter`
  for credit, with `reviewer` the caller, `cloned_from` set and the
  pedigree columns 5.2 names. The
  source row becomes `superseded`, because its own hash is `C` and two
  `approved` rows cannot coexist there. This action requires `H` equal to
  `C`: a stale row is approved through `approve_stale` first, and asking
  for an edit on one is refused beside "Approving with an edit takes one
  row at a time.".
- `approve_stale`: for a row whose `H` is an older English. It supersedes
  any row already `approved` at `(culture, key, C)`, then inserts a clone
  carrying the current `e.sha256` from `english_strings`, the original
  `submitter` and `cloned_from`. The source row stays `approved` at its
  own older `H` when nothing is `approved` at that hash already, which is
  what keeps older builds served, and becomes `superseded` when something
  is. A repeat call whose clone would duplicate the row already
  `approved` at `C` is a no-op returning that row's id, not a unique
  violation.
- `reject`: accepts a `pending` row and an already-approved one. On a
  live row it is what the page labels **Revoke**: status becomes
  `rejected` with the reviewer's note required, the row's
  `english_sha256` is left alone, and the revision trigger fires on the
  approved-to-rejected transition so the fleet drops it on the next
  fetch. The key then falls back to the shipped file or to English, never
  to a superseded row, because only `approved` rows are served. There is
  no separate revoke verb.

`_has_blocked_term` runs on `p_note` here too: a review note is free text
shown to another person.

Refusals: `Only a reviewer for this language can do that.`,
`That row cannot be approved while it is <status>.`,
`This key is retired and cannot be approved.`,
`A note is required to take a live translation down.`,
`Approving with an edit takes one row at a time.`

`am_i_translation_reviewer() returns jsonb`:
`{reviewer, cultures}`. What reveals the page's reviewer toggle. The
server re-checks on every reviewer call, so the flag is cosmetic.

`list_my_translations(p_culture text default null)`: the caller's rows
with `status`, `english_state` (`current`, `previous`, `retired`), the
note and the `review_note`. No IP hash, no reviewer uuid. This is what
the window's status glyph reads.

`list_translation_queue(p_culture text, p_status text default 'pending',
p_limit int default 200, p_offset int default 0)`: puts
`public._is_translation_reviewer(p_culture)` inside the `where`, so a
non-reviewer gets zero rows rather than an error. Carries
`submitter_name` from `profiles`, the note, `english_now`,
`english_then`, `english_state`, `live_text`, the server's `context`, and
`support`, which counts distinct accounts that proposed exactly this text
and only those already holding an approved row in that language.

### 3.3 Reads (anon and authenticated)

`list_approved_translations(p_culture text, p_known_revision bigint
default null) returns jsonb`

- a tag `translation_cultures` does not hold:
  `{ok:true, culture, revision:0, rows:[]}`. The plugin calls this for
  every member of a chain such as `zh-Hans-CN`, `zh-Hans`, `zh`, and two
  of those three will never have a row, so an unknown tag is an empty
  answer rather than an error. A tag that has a row is served whatever
  `accepting` says: `accepting` gates submission alone, and refusing an
  unknown or closed language is `submit_translations`' job. Were serving
  gated on it too, closing a language for edits would blank the
  translations on every install of it overnight.
- revision matches: `{ok:true, culture, revision, unchanged:true}`, about
  80 bytes.
- otherwise `{ok:true, culture, revision, rows:[{k, t, h}]}`, the whole
  approved set ordered by key. No submitter, no ids, no timestamps.
- above 10,000 rows: `{ok:true, culture, revision, truncated:true}` and
  **no rows at all**. A partial set would be half-applied by a client
  whose file is a wholesale replace, so the cap returns nothing and the
  client keeps what it has (section 5 has the release valve).

`translation_pending_counts(p_culture text)`: per key, how many rows are
pending. Never text, never a username. It is anon-readable as a
consequence of "signed out, everything but sending works": per-key
pending counts are public, the text and the names are not.

`translation_progress()`: per language, `name`, `accepting`, `revision`,
`english_ref`, `english_version`, `total`, `approved_current`,
`approved_other`, `pending`, `percent`. `percent` comes from
`approved_current` only, meaning rows whose hash equals the live English;
`approved_other` is the re-approval queue and the number MAIRA cannot
show. `english_ref` and `english_version` come from the `app_config` rows
`upsert_english_strings` writes, and the page shows them beside the ref
it loaded.

`translation_contributors(p_culture text default null)`: by username,
counting `approved` and `superseded` rows, skipping accounts whose
`profiles.hide_from_translation_credits` is true. Accounts with no
username or a deleted account collapse into one `(anonymous)` row.

Those four are the entire anon surface. `list_english_strings` is
deliberately absent: the page reads English from the repository
(section 7.4) and the plugin holds its own, so the function would have
had no caller.

### 3.4 Service role only

`upsert_english_strings(p_version text, p_ref text, p_rows jsonb)`:
inserts the replaced text into `english_strings_history`, upserts each
`{k, t, c}`, stamps `retired_at` on keys the push no longer carries, and
writes `english_catalog_ref` and `english_catalog_version` into
`app_config`. Returns a receipt with `added`, `changed`, `retired` and
`live`.

`mark_translations_shipped(p_culture text, p_version text)`: stamps
`shipped_version` on approved rows whose hash matches the current
English, so the queue can show "in v0.5.0". Informational only.

### 3.5 The Discord nudge, without a new Edge Function

`report-notify` gains a branch, reached before its `report_id required`
check, keyed on a new payload kind:

```ts
if (body?.kind === "translation" || body?.kind === "translation_daily") {
  return await translationNotify(body);   // DISCORD_TRANSLATE_CHANNEL_ID
}
```

It keeps the function's existing posture: `verify_jwt` on, the
service_role claim checked first, and a DRY-RUN log when the bot token or
the channel id is unset, which is how `arcade-digest` shipped weeks
before its channel existed. Splitting it into its own function later is a
one-line change: move the branch to a new file and repoint the URL. The
apply checklist therefore adds one secret and one redeploy of an existing
function, not a new deployable with its own JWT posture and README entry.

Three lines, and no others:

- one per approval call, naming the language, the reviewer and
  `acted.length`, which is how a hijacked reviewer session gets noticed;
- one on an account's first-ever submission;
- one a day per language while anything is pending.

The first two are posted by the RPCs themselves through `net.http_post`,
not by triggers: a statement trigger cannot see a call, and an unfiltered
after-insert trigger would post on every submit. Both use
`timeout_milliseconds := 30000`, because pg_net defaults to 5000 ms,
which 0046 never overrides, so every job it scheduled records a timeout
whatever the function did (0114); 30000 matches 0114 and 0116. No
submission text and no IP ever reaches Discord.

Two cron jobs in the house naming:

```sql
select cron.schedule('tf4all-translation-nudge', '0 17 * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/report-notify',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets
                                      where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{"kind":"translation_daily"}'::jsonb,
    timeout_milliseconds := 30000);
$job$);
```

and `tf4all-translation-gc` at 03:10 UTC, which does the two deletions of
section 5.4 in plain SQL with no HTTP call.

---

## 4. The hash convention

`english_sha256` is SHA-256 over the UTF-8 bytes of the decoded JSON
string value, lowercase hex, 64 characters. No trimming, no newline
normalization, no Unicode normalization, no BOM, and the key is not part
of the input.

The value as Newtonsoft holds it, as `JSON.parse` holds it and as
`ConvertFrom-Json` holds it are the same UTF-16 code points, and
`Encoding.UTF8.GetBytes`, `new TextEncoder().encode` and
`[Text.Encoding]::UTF8.GetBytes` produce identical bytes for any
well-formed string. Postgres computes the same value from the stored
text.

Three vectors, recomputed at HEAD 8556e27 on 2026-09-25 from real
`en.json` values that are all still live at that commit
(`Account_AccountAuth`, `Account_AccountChangeEmail` and
`Account_AccountStatusSignedIn_Fmt`):

```
Sign in
  bfd402b2f6f3812529b55596136d3a11c51616317e3b1cd999928e2d4eae7d3f
Change plus U+2026
  a6b4705c688314f0a6e0de5886c082aab286c8fc763490d724218e7f5f53c54e
Signed in as {0}.
  57d8ac41163a34ec577c56848983580e14ffacf51935889a9591bc9537bd698a
```

All three go into four places: the Core.Tests case
`LocEnglishHashVectors`, `push-english.ps1`'s preflight, the page's
self-check on load, and the apply checklist. A wrong second value means
something on that path is encoding text as Windows-1252.

The plugin computes it in one named pure member,
`LocFile.EnglishSha256(string value)`, over
`Encoding.UTF8.GetBytes(value)` to lowercase hex, beside
`LocStore.EnglishText(key)`, which returns the English this build
displays for a key. Both are pure, so both compile into Core.Tests on
net8. The fetch filter (section 8.3) is the only production caller.

---

## 5. Staleness and cardinality

### 5.1 Why approval is per English, not per key

Three channels display different English at once. If approval were
unique per `(culture, key)`, approving the beta's new Spanish would pull
the stable build's correct Spanish for that key. So the unique index is
on `(culture, key, english_sha256) where status = 'approved'`, and a key
carries one approved row per English it has had.

### 5.2 The clone rule, and where the source row ends up

`approve` supersedes only same-hash rows, and the source row itself
becomes the live one at the current hash. `approve_stale` and
`approve_with_edit` both:

1. supersede any approved row for `(culture, key, current hash)`, exactly
   as `approve` does;
2. insert a clone selecting `e.sha256` from `english_strings`, keeping
   `submitter` for credit and setting `cloned_from`. The clone also copies
   `source`, `source_ip_hash` and `submitted_at` from the source row. All
   three are `not null`, and copying rather than restamping them keeps the
   row's pedigree with its translator and moves neither the hourly count
   nor the per-IP count, both of which read `updated_at`.

They part company on the source row, and the unique index is why. Under
`approve_with_edit` the source row's own hash is the current hash, so it
cannot stay approved beside the clone: it becomes `superseded`. Under
`approve_stale` the source row's hash is an older one, so it stays
`approved` at that hash, which is what keeps older builds served, unless
a row is already approved there, in which case it becomes `superseded`
too. Section 3.2 states the same three outcomes verb by verb, and the two
sections have to be read as one rule.

Step 1 is what keeps the insert from raising 23505 when a second
translator's row was approved first, when the reviewer runs
`approve_stale` on two pending rows for one key, or when the machine pass
already covered that key. A repeat call whose clone would duplicate the
row already approved at the current hash is a no-op returning that row's
id.

### 5.3 What each side filters

**At submit**, the hash must name an English the key has actually had.
Anything else is garbage or tampering.

**At review**, the queue carries `english_state` and, when `previous`,
the exact text that was translated beside the current one. A `retired`
key cannot be approved.

**At fetch**, the server serves every approved row with its hash and
filters nothing, because the client knows the one thing the server does
not: which English this build displays. The plugin keeps a row only when
`LocFile.EnglishSha256(LocStore.EnglishText(k)) == h`. Consequences, all
intended: a row approved for the beta's English never shows on a stable
build; a row that goes stale after a release stops applying on new builds
until someone re-approves it; between two builds with the same English
the filter is a no-op.

### 5.4 Bounding the set

Growth is one approved row per key per distinct English. At about 2,000
keys and one English change a year for a small fraction of them, a
language sits near 2,000 to 2,500 rows, roughly 250 KB. The 10,000-row
cap is the ceiling, and `tf4all-translation-gc` is the release valve:

```sql
-- Rows whose English is more than a year out of date can match no build
-- anyone still runs, so they stop being served.
update public.translations t set status = 'superseded',
       review_note = coalesce(t.review_note, 'the English it translated is over a year old')
 where t.status = 'approved'
   and not exists (select 1 from public.english_strings e
                    where e.key = t.key and e.sha256 = t.english_sha256)
   and not exists (select 1 from public.english_strings_history h
                    where h.key = t.key and h.sha256 = t.english_sha256
                      and h.replaced_at > now() - interval '365 days');

-- Abandoned proposals from accounts that never had one approved.
delete from public.translations t
 where t.status = 'pending' and t.created_at < now() - interval '180 days'
   and not exists (select 1 from public.translations a
                    where a.submitter = t.submitter and a.status in ('approved','superseded'));
```

The supersede pass bumps the revision through the row trigger, so the
fleet converges on the next fetch. The delete removes rows the fleet never
saw, so it moves no revision.

---

## 6. Rate limits and the account-blind backstops

Email OTP with `create_user = true` makes an account cost one disposable
address, so three of these key on something other than the account. One
set for both surfaces.

| Cap | Value | Keyed on | Checked |
|---|---|---|---|
| Rows per hour | 300 | account or client id, on `submitted_at` | pre-loop |
| Rows per call | 50 | the call | pre-loop |
| Pending rows | 3,000 | account and language | pre-loop |
| Text length | 2,000 characters | row | per row, and the check |
| Length vs English | `greatest(64, 4 * len(english))` | row | per row |
| Rows per hour | 600 | `source_ip_hash` | pre-loop |
| Pending rows | 20,000 | language, globally | pre-loop |
| New account wait | 10 minutes | `auth.users.created_at`, signed in only | pre-loop |
| Reads | unlimited | | |

"Pre-loop" means before the row loop, so a batch cannot straddle a cap.

The hourly cap is one pre-loop count against rows already inside the
window, so the call that would cross 300 is refused whole and no row is
ever refused as "the 301st". A call that passes may take the hour's total
to 349, which is the intended looseness: the cap exists to stop a flood,
not to meter.

The 4x rule with a 64-character floor was re-checked against the longest
English in the tree, 429 characters, whose ceiling is 1,716 and therefore
under the 2,000 table cap.

---

## 7. The web page (DROPPED 2026-09-28, kept as a record)

### 7.1 Files

- `guides/translate.html`, front matter `layout: app`, about 150 lines of
  markup.
- `guides/_layouts/app.html`, the site header and nav without the guides
  sidebar, search box or footer.
- `guides/assets/js/translate.js`, plain ES2017, no build step, no
  dependency, about 1,300 lines.
- `guides/assets/css/translate.css`, reusing `style.css`'s tokens.
- one nav line in `guides/_layouts/default.html`.

Its own layout, because `default.html` carries an inline script that uses
`innerHTML` (lines 121 to 132) and the page's policy forbids both.

`scripts/gen_public_guides.py` removes only stale `*.md` from `guides/`,
so these four files survive a regeneration. `pages.yml` deploys
`guides/**` on a push to `main`, so they reach users by cherry-pick, once
at launch and then only when the page changes.

### 7.2 The policy, verbatim

GitHub Pages cannot set headers, so it goes in a meta tag:

```html
<meta http-equiv="Content-Security-Policy" content="
  default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:;
  connect-src https://dvttzzjbktelcikvyzmt.supabase.co https://raw.githubusercontent.com;
  base-uri 'none'; form-action 'none'; frame-ancestors 'none'">
```

`frame-ancestors` does nothing in a meta policy and is kept because it
states the intent. No `font-src`: the page uses the system stack
`style.css` already uses.
`style-src 'self'` also blocks `style` attributes, so the progress bar
sets width through `el.style.setProperty`, and it rules out a CDN
`supabase-js`. The client is hand-rolled against the four endpoints
`CommunityAuth.cs` already proves: `POST /auth/v1/otp` with
`create_user: true`, `POST /auth/v1/verify`,
`POST /auth/v1/token?grant_type=refresh_token`, and
`POST /rest/v1/rpc/<fn>` with `apikey` plus a bearer. The `apikey` is the
public anon key, inlined in `translate.js`: the same key the plugin ships,
not a secret, and the page has no build step that could hide one. Only the
service key stays out of the repository.

### 7.3 Text-only DOM, and how it is gated

Every network-supplied string is built with `document.createElement` plus
`textContent`. No `innerHTML`, `outerHTML`, `insertAdjacentHTML`,
`document.write`, `eval`, `new Function` or `javascript:` URL, and no
linkifying of URLs found inside translation text. The word diff in
reviewer mode returns DOM nodes.

The gate is a step in `.github/workflows/pages.yml`, placed before
`actions/jekyll-build-pages`, because that workflow is the only thing
that publishes the page and no workflow in this repository runs
`dotnet test` (Core.Tests is deliberately outside the `.sln`). A grep in
a test nobody runs is not a gate:

```yaml
      - name: The translation page builds its DOM from text only
        run: |
          if grep -nE 'innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(|new Function|javascript:' \
                  guides/translate.html guides/_layouts/app.html guides/assets/js/translate.js; then
            echo "Build every network-supplied string with textContent."
            exit 1
          fi
```

Three files, named. It does not cover `default.html`, whose inline search
script legitimately uses `innerHTML` on strings from its own generated
`search.json`, and that script is the reason `translate.html` has its own
layout. `docs/RELEASING.md`'s guides step names this check, so a failed
deploy reads as expected rather than mysterious, and the same grep runs
locally in one line before the cherry-pick.

### 7.4 English, context, and the ref

English and context load from the repository:

```
https://raw.githubusercontent.com/Mhytee/Trueforce-For-All/<ref>/src/TrueforceForAll.Plugin/Languages/en.json
https://raw.githubusercontent.com/Mhytee/Trueforce-For-All/<ref>/tools/loc/context/en.context.json
```

`<ref>` is whatever `translation_progress` reports as `english_ref`, so
the text the page renders and the text the server validates against agree
by construction; the constant `dev` is the fallback when that call fails,
and it flips to `beta` in the release that first ships
`Languages/en.json`. `?ref=` overrides it for the owner, validated
against `^(dev|beta|main|v\d+\.\d+\.\d+)$` before it reaches a URL, or
`ref=../../Attacker/Repo/main` points the English column at another
repository. The header always shows the ref and version loaded beside
`english_ref` and `english_version`, and a 404 names the ref tried.

One ref at a time, deliberately. The page does not fetch beta and main
together to label a row "current in stable"; the staleness a translator
sees is relative to the loaded ref, and that is cheaper and enough.

When a key's English has moved on the loaded ref since the last push, the
submission is refused with
`The English for this key moved. Reload the page and translate the
current text.` The standing mitigation is a release-independent step:
push dev English after each converted slice lands (section 9.1).

`tools/loc/context.ps1` writes `tools/loc/context/en.context.json` by
converting the `siblingNames`, `helpText` and `ownToolTip` columns
`keys.ps1` already emits into `proposed-keys-*.csv`, plus the area each
slice was harvested from. It writes no new XAML walker. A key with no
context gets an empty entry, so the parity check stays exact once
Phase 2's C# keys arrive, none of which has a sibling or a tooltip.

The file must not sit under `Languages\`: the csproj embeds
`Languages\*.json` by glob with
`LogicalName TrueforceForAll.Plugin.Languages.%(Filename).json`, which
would embed it as the tag `en.context`, and `LocStore.NormalizeTag`
rejects a dot. Two Core.Tests cases freeze that:
`LocEmbeddedLanguageNamesAreTags` asserts every embedded `Languages`
resource name is a valid tag, and `LocContextFileMatchesEnglish` fails on
a missing or extra key.

Context has one precedence rule: the reviewer queue shows the server's
`context`, because it is pinned to the English the row was approved
against; the table's Context column is the raw file, labeled with the ref
it came from.

### 7.5 Tokens and parameters

Every project page under `mhytee.github.io` shares one origin, so the
access token lives in a closure and the refresh token in
`sessionStorage`, never in `localStorage`, where unsent drafts may sit.
Drafts are keyed by `(ref, culture)` and survive a reload; signing out
clears the session and keeps them. `lang` is checked against the tags
`translation_progress` returned and `key` against the server's key regex
before either reaches a URL.

### 7.6 The table

Columns: Location, English, Translation, Notes, grouped by area in
document order with a sticky group header, one status badge per row.
Keys sit behind a persisted "Show keys" toggle on both surfaces, which
supersedes Phase 2's "keys never shown". Signed out, everything but
sending works: browse, search, the area and Untranslated, Stale, Pending,
Mine filters, the per-key pending counts, the contributors list, the
progress bar, drafts, and the `es.json` download.

Validation mirrors the server exactly (section 3.1) and adds client-only
warnings: identical to English, trailing-punctuation parity, a length
gauge against the English, and a never-translate glossary term present in
English but absent from the translation. The glossary list starts as a
constant in `translate.js` and moves to a generated list when
`docs/localization/glossary.md` lands in Phase 3.

The shared-English hint flags "same English as N other keys" with a "use
their translation" action. Fourteen English values are shared at HEAD and
thirty in the working tree, and the number grows with every slice, so the
hint is generated rather than listed.

Send chunks at 50, passes `p_source = 'web'`, shows each `P0001` sentence
verbatim on the row, and repeats `Translate_SendConfirmBody`'s sentence
before the first send of a session. The download writes a valid
`es.json` for
`<SimHub>\PluginsData\Common\TrueforceForAll-Languages\`, which
`LocWatcher` picks up within a second.

### 7.7 Reviewer mode

The same table behind one toggle revealed by
`am_i_translation_reviewer`. Verbs, each mapping to one
`review_translations` action: Approve, Approve against the older English
(`approve_stale`, behind the `needs_confirm` dialog), Edit and approve,
Reject with a note, Revoke on a live row (`reject`), Skip, and "Approve
all 37 visible from this submitter", which is one call and therefore one
Discord line. `translate.html?lang=es&key=Account_AccountAuth` is what
the window's Pick mode hands over.

---

## 8. The plugin

`LocStore.cs` compiles into Core.Tests and runs there on net8, so what is
added to it stays file and dictionary code. HTTP lives in
`LocCommunity.cs`, WPF in `LocLocator.cs` and `TranslateWindow.cs`.

### 8.1 LocStore: what changes, not only what is added

Layer order per chain member (`es-MX`, then `es`), highest first: the
window's in-memory preview overlay, `<root>\<tag>.json`,
`<root>\community\<tag>.json` (new), `<root>\shipped\<tag>.json`, the
embedded resource, then English, then `[key]` with one Warn. The user's
own file beats community by construction.

Three existing members change:

- `OverlayTagLayers` gains one `ReadDiskLayer(CommunityPath(tag))` line
  between shipped and root, skipped when `UseCommunityLayer` is false.
- `HasAnyLayer` gains the community path. This is load-bearing: it
  decides which chain member becomes active, and without it a fetched
  `community\es-MX.json` is written and never read, because `Load`
  overlays chain members only from the active index outward. With it, a
  fetched file can activate its own tag, and the loop still overlays the
  parent beneath it.
- `Describe` adds `community` to the `Layers` string
  (`root+community+shipped+embedded`) and a `CommunityCount`.
  `RootOverrides` and `RootUnknown` keep their meaning, which is the
  user's own file, and that is what the divergence marks read.

Added, all pure: `CommunityFolder`, `UseCommunityLayer` (skips the layer
and deletes nothing), `SetPreview` and `ClearPreview` raising the
existing `Item[]` change so every `{loc:T}` binding re-renders,
`ExplainSources(tag)` for per-key provenance,
`ResolveBelowCommunity(tag, key)`, and `EnglishText(key)`.

Two new files join `LocStore.cs` in the Core.Tests csproj:

- `LocFile.cs`: `Serialize` in exactly `Write-LocJson`'s shape (`_meta`
  first, ordinal keys, 2-space indent, LF, no BOM), `WriteAtomic` copied
  from `CommunityBrowseCacheStore`'s temp-then-`File.Replace` path, and
  `EnglishSha256`.
- `LocPlaceholderRules.cs`: `Signature` identical to
  `Get-LocPlaceholders`, `Check(english, translation)`, and
  `HasUnsafeChars`, which carries the same character list as
  `_loc_banned_chars()`.

`LocSeed.ReadmeText` gains a paragraph: `community\<language>.json` is
the plugin's own cache of approved fixes, rewritten by the plugin, and
your file in the folder root wins over it.

### 8.2 The cache file

`<SimHub>\PluginsData\Common\TrueforceForAll-Languages\community\<tag>.json`,
a flat language file so `LocStore` needs no new parser:

```json
{
  "_meta": {
    "culture": "es", "name": "Espanol", "source": "community",
    "revision": 412, "fetched_at": "2026-09-24T18:00:00Z",
    "served": 380, "stale": ["Support_ReportIssue_Tip"],
    "dropped_stale": 2, "dropped_unsafe": 0, "dropped_redundant": 30
  },
  "Account_AccountAuth": "Iniciar sesion"
}
```

`culture` and `name` are mandatory and `name` is taken from the shipped
or embedded file rather than from the response. Without them
`ParseLanguageJson` warns, and for a disk layer that callback is an
unconditional `Log` on every read, so every `Reload` would write
`"_meta" should carry "culture" and "name" strings` into SimHub.txt. A
chain member with no shipped and no embedded file has no name of its own
to take, so it takes the one from the nearest parent that has a file, and
the tag itself when no parent does. The sample above is spelled ASCII for
this document alone: `name` is copied byte for byte, so `Espanol` carries
its tilde on disk.

`ParseLanguageJson` returns only `metaName`, so `LocCommunity` reads
`revision`, `fetched_at`, `stale[]` and the three drop counters with its
own `JObject` parse of the same file. `LOCSTATUS` prints the counters and
the fetch age; nothing else parses them.

### 8.3 The fetch

Gated on
`CommunityEnabled && UseCommunityTranslations && backend configured
&& ActiveTag != "en"`, single-flight, at most three chain members, one
call each and one file per member that has rows, always with the anon key
as bearer so the read is never joinable to an account. A dedicated
`HttpClient` sets `MaxResponseContentBufferSize = 2 MB` and the URL passes
`ChannelValidation.IsTrustedSupabaseUrl`.

Caps derive from the server, not from the English key count. The whole
fetch is abandoned with the previous file intact when the response
exceeds 2 MB, when `truncated` is true (one Warn naming the language, and
a retry on the next tick), or when the payload is not a flat array of
string fields. After the hash filter the kept set cannot exceed
`store.EnglishKeyCount` by construction, since the server allows one
approved row per `(culture, key, English)` and a build displays one
English per key; a duplicate key after filtering means the unique index
was violated, so the client takes the last and logs one Warn.

Per row, in order, counting each drop:

1. the key must match the key rule and exist in English, or it is dropped
   as unknown;
2. `h` must equal `LocFile.EnglishSha256(store.EnglishText(k))`, or the
   key is recorded under `_meta.stale`, counted in `dropped_stale` and
   not served, so it falls through to the shipped translation or to
   English rather than showing text written for different English;
3. `t` must be non-empty, pass `HasUnsafeChars`, add no newline its
   English lacks, stay within `Math.Max(64, 4 * english.Length)` and
   match the placeholder signature, or it is counted in
   `dropped_unsafe` with one Info line naming the key;
4. `t` equal to `ResolveBelowCommunity(tag, key)` is counted in
   `dropped_redundant` and dropped, which is what makes the layer hold
   only deltas after a release merge.

The response is the whole approved set for that language and the file is
replaced wholesale by `LocFile.WriteAtomic`, never merged. That is what
makes a rejected or superseded row disappear with no removal list. A
member whose response holds no rows writes no file at all and deletes a
stale one if it finds it, so the two empty members of a `zh-Hans-CN`
chain cost one call each and no file. `LocFile.WriteAtomic` creates the
`community` folder on first use, and `LocSeed` prunes nothing under the
root, so the folder survives every start. Then a
dispatcher-marshaled `Reload()`, because `LocWatcher` watches root
`*.json` with `IncludeSubdirectories = false` and never sees the
subfolder. Any failure keeps the file that is there, which was already
applied at `Load` before any network call, so the layer is offline-first.

It runs on its own `System.Threading.Timer`, 20 seconds after
`Loc.Initialize` and then on a 6-hour period, self-gated by
`_meta.fetched_at` with a 24-hour TTL. Not on `_usagePingTimer`
(grep `_usagePingTimer` in TrueforcePlugin.cs): that timer's callback is
`MaybeSendUsagePing`, self-gated on `ShareUsageStats`, so hanging the
fetch off it would stop translations reaching anyone who turned usage
statistics off and would couple a community read to the one timer
PRIVACY.md describes as tied to nothing. It also runs on
`LanguageChanged` and on the `LOCFETCH` access code, and the window's
"Refresh community" button calls the same forced path. The 24-hour stamp
lives in the file, never in a settings key.

### 8.4 The window: what 3b adds

Phase 2 builds the window: the non-modal shell, the two-column grid,
search, filters, Save, and the language picker row with `UiLanguage`. The
filters are gone since; see the bullets below. Phase 3b adds to that
window:

- **Status glyphs**, provenance from `ExplainSources` and outcome from
  `list_my_translations`: pending, approved, and declined with the
  reviewer's note on hover. A volunteer who cannot tell whether a fix
  went live stops sending fixes.
- **Pick mode**: Ctrl+click an element, read its binding path through
  `BindingOperations`, select that row. Its forward twin "Where?" uses
  `LocLocator.Find`, which reuses `LocDiagnostics`' visual plus logical
  walker, selects the containing `sh:SHTabItem` and flashes a gold
  adorner for 1.5 seconds. Best effort, never throws; a key only C#
  assigns reports "Shown in a dialog, not on the settings panel".
- **Divergence marks** on root rows that differ from the approved or
  shipped text, offering to adopt the community text, to drop overrides
  that now match, and after an accepted Send to move those keys out of
  the root file.
- **The shared-English hint**, the same rule the page uses.
- **A persisted "Show keys" toggle**, the same one the page carries,
  which supersedes Phase 2's "keys never shown". Off by default: a
  translator typing does not need the key, and a translator reporting a
  bad string cannot do without it.
- **Publishing, with no Send button** (owner, 2026-09-29: "a typo is
  likely better than no translation at all"). A row goes to the service a
  few seconds after it is typed, chunked at 50, `p_source = 'plugin'` with
  the build version, each `P0001` sentence shown verbatim. A refusal is
  remembered per key so a row the server will not take is not offered again
  every few seconds, and the retry waits rather than repeating against a
  per-hour cap. No sign-in (owner, 2026-09-30: "users should not need to be
  signed in to translate"): an account is used when there is one, else the
  client-minted `anon:<id>`, which is `submit_car_fact`'s pattern and
  migration 0139's.
- **One row per plural form THIS language has**, not per form English has:
  four for Russian, one for Japanese, six for Arabic, built from
  `LocPlurals.FormsFor(tag)` rather than from the English key list. The
  forms of one counted string stay together and in the language's own order,
  singular first, and each carries the counts it covers ("used for 2, 3, 4,
  22") rather than the category name, because "few" says nothing to someone
  who is not a translator by trade. See section 10.7 for the server half.
- **No filters** (owner, 2026-09-29: "why 3 states, everything, still to
  do, needs a fix?"). Untranslated rows sort first inside each area
  heading, which is the same answer without a control, and the problem
  count is clickable when there is one. "Copy the English" stayed, "open
  the folder" went: the folder is not where the work happens.
- **One setting**: `UseCommunityTranslations`, default true (owner, 2026-09-25),
  gated by `CommunityEnabled`, its row hidden when the active tag is `en`,
  with one `BackupProjection.Portable` line. Default-on is an exception to
  the default-off baseline, granted on one condition: an English install
  must not notice. It does not, and the gate is what guarantees it rather
  than the default's wording. With `en` active the fetch makes no request,
  writes no file and adds no layer, so on an English install the setting is
  inert and invisible. `UiLanguage` and its own Portable
  line are Phase 2's.

Phase 2 is a hard predecessor for this half, and it ships the `TRANSLATE`
access code with the shell it opens. That code
decides how the window opens, not whether one exists, so every exit
criterion below that names the window is 3b's exit given Phase 2 in the
build, and the order list's window item waits on it. The backend, the
page and the fetch client depend on nothing in Phase 2 and can land
before it.

### 8.5 Language resolution

There is no `ResolveRequestedTag()` in the tree. The resolution is inline
in the language-init block in `TrueforcePlugin.cs`, around line 4269, and
already reads `ReadSimHubCultureSetting()` when that is non-empty and not
`"?"`, else `UsageLanguage.UiLang() ?? "en"`. The two-letter defect is
live code today, not a risk in a design.

3b replaces that `UsageLanguage.UiLang()` call with a new internal member
that returns `FirstPreferredUiLanguage()` as Windows gives it (`de-DE`),
sharing the existing `GetUserPreferredUILanguages` P/Invoke, and extracts
the block as `ResolveRequestedTag()`. Phase 2 adds the `UiLanguage`
branch to the same method, ahead of SimHub's `Culture`, which leaves the
order `UiLanguage`, SimHub's `Culture`, the full Windows tag, `en`.

`UsageLanguage.UiLang()` stays exactly two lowercase letters, on purpose,
for the usage ping's privacy promise, and it is not a resolution source.
This matters because `LocStore`'s chain walks child to parent only. The
moment a `de-DE.json` ships, embedded or planted in `shipped\`, a
`de-DE` Windows resolves to `de`, `HasAnyLayer("de")` is false, the chain
for `de` never reaches that file, and the install falls back to English
with the translation sitting on disk unread. `Languages\` holds only
`en.json` today, so nothing is broken yet; it breaks on the first shipped
tag that carries a region, which is five of the plan's seven. Spanish
works either way only because `es` happens to be the neutral tag.

### 8.6 A localized value never becomes a URL, a path or a process argument

Once the daily fetch lands, every localized string is remotely controlled
after one approval. Today no `Loc.T/F/N` result reaches `Process.Start`,
`new Uri`, `NavigateUri` or `Path.Combine`, so this is cheap to freeze
now and expensive after a Phase 2 conversion routes a link label through
`Loc`. The rule: URLs, file names, culture tags and identifiers are never
localized, which is why
`tools/loc/xaml-keep-literal.txt` already keeps the repository address
literal.

One Core.Tests case beside `LocKeysResolve`,
`LocValuesNeverBecomeUrlsOrPaths`, fails when a `Loc.T/F/N(` call or a
`{loc:T ...}` binding shares a statement or an attribute with
`Process.Start`, `new Uri`, `NavigateUri`, `Path.Combine`, `File.` or
`Directory.`. It is order item 1 of section 12, because its target
already exists, and it is in Exit.

`LocalizationTests.cs` holds nine cases today. Phase 3b adds eight,
counted by name:

1. `LocCommunityLayerPrecedence`: the community layer sits between
   shipped and root, and `UseCommunityLayer = false` skips it.
2. `LocPreviewOverlayWins`: the window's preview beats every file layer,
   and `ClearPreview` puts the file back.
3. `LocEnglishHashVectors`: the three vectors of section 4.
4. `LocFileSerializeMatchesWriteLocJson`: `LocFile.Serialize` is
   byte-equal to a committed `Write-LocJson` sample.
5. `LocEmbeddedLanguageNamesAreTags`: every embedded `Languages`
   resource name is a valid tag (section 7.4).
6. `LocContextFileMatchesEnglish`: no missing and no extra context key.
7. `LocKeyRuleFormsAgree`: `LocStore.IsValidKey`, the server regex and
   `tools/loc/README.md`'s writer-side form agree (section 2.3).
8. `LocValuesNeverBecomeUrlsOrPaths`: the rule above.

---

## 9. Scripts and RELEASING.md

### 9.1 push-english.ps1

```
tools\loc\push-english.ps1 -EnJson src\TrueforceForAll.Plugin\Languages\en.json
    -Version X.Y.Z -Ref dev|beta|main
    [-Context tools\loc\context\en.context.json]
```

Takes the service key from the environment, never from a file in the
repo. Asserts all three hash vectors locally, runs `validate.ps1`, calls
`upsert_english_strings` and prints the receipt. Keys absent from a push
are stamped `retired_at` rather than deleted.

Two standing lines in `docs/RELEASING.md`:

- **After each converted slice lands on `dev`**, and on demand, run
  `push-english.ps1 -Ref dev`. The page renders English from `dev` while
  the server validates against the last push, so a slice that moves
  English without a push turns every submission on those keys into a
  refusal.
- **In the release list**, in this order: merge approved rows on `dev`,
  bump the version, push English for that version on the release branch,
  build, draft, publish, then `mark_translations_shipped`. The push
  precedes the build because `submit_translations` rejects a key
  `english_strings` does not hold. Rows are matched by English hash, not
  by version, so promoting a beta to stable needs no re-push.

The `LegalRevision` sentence at the top of RELEASING.md gains PRIVACY.md
beside `EULA.txt` and `LICENSE`. The guides step gains the name of the
text-only check in `pages.yml`.

### 9.2 merge-community.ps1

```
tools\loc\merge-community.ps1 -Culture es [-Name <native>] [-DryRun]
```

Reads the release branch's `en.json`, asserts the vectors, calls
`list_approved_translations` with the anon key, and keeps rows whose hash
matches the local English; the rest print as the re-approval queue.
Re-checks placeholder parity locally, writes `Languages\es.json` through
`Write-LocJson` in the canonical shape, and refuses to run when
`git status` shows that file modified. Appends the merged keys to
`tools\loc\human-keys.es.txt` and one audit line per row to
`tools\loc\merged-es.csv`, quoting any cell that starts with `=`, `+`, a
hyphen, `@`, tab or CR.

One human-keys sidecar, not two: `merge-community.ps1` folds approved
community rows and Phase 4's `merge-user-fix.ps1` folds a user's root
file, both appending to the one file `retranslate.ps1` reads.

### 9.3 validate.ps1

It already checks duplicate keys, key validity including the plural
suffixes, placeholder parity per translated key, and leading and trailing
whitespace parity per translated key, the last of which
`LocEdgeWhitespaceIsDeliberateAndPreserved` also covers. The
plain-versus-`_Fmt` rule is not among them: `LocPairedCaptionsAgree`
holds that one, in Core.Tests alone, and nothing in `tools\loc` reads it.
`validate.ps1` gains two things: the em-dash and double-hyphen rule the
plan states but no tool enforces, and the `_loc_banned_chars()` character
list, so the page, the plugin, the server and the scripts all refuse the
same bytes.

A plural form English does not have is compared against the English it was
written against, which is the `.other` row of the same base (section 10.7),
so a Russian `X.few` is a key and not a typo. Which forms a language needs
is not checked here: the count of forms English does not have is printed
per language, and a form the language never uses is dead weight in the file
rather than a failure. A counted string counts as reached once the language
has any form of it, so a Japanese file is not reported as missing the
singular it will never write.

---

## 10. Privacy and operations

### 10.1 PRIVACY.md, eight anchored edits

They ship in the release that first carries the fetch, because the
Changes clause re-shows the notice pages on update. Final copy:

1. **The short version**, first sentence. Add translations to the list of
   what runs without an account: "the plugin fetches crowd-sourced car
   data (redlines, engine types, car names) and community-reviewed
   translations of its own text, and shares back the values you tune,
   without an account and without your name."
2. **What runs by default**, a new bullet after the Lovely Sim Racing
   one: "fetches community-reviewed translation fixes for the language
   the plugin displays, when that is not English (the request carries the
   language code and nothing about the account, and the answer is cached
   on this PC for about a day);"
3. **A new subsection** after "Community car data (car facts)", so the
   two account-free default fetches sit together:

   > ### Translating the plugin
   >
   > Reading translations is account-free. When the plugin displays a
   > language other than English, it fetches the translations a reviewer
   > has approved for that language and keeps a copy on this PC,
   > refreshed about once a day. The request carries the language code
   > and nothing about the account. It stops with the "Enable community
   > features (online)" switch, and it has its own switch, "Use
   > community translation fixes".
   >
   > Sending a translation needs you signed in. Each one is stored with
   > the language, the text you wrote, a reference to the English it
   > translates, an optional note to the reviewer, whether it came from
   > the web page or the plugin, the plugin version, your account id and
   > a hashed form of your IP address. A reviewer appointed for that
   > language approves it before anyone else sees it, and what other
   > users receive is the text alone. Translations still waiting for
   > review, and rejected ones, are visible only to you and to the
   > reviewers. The translation page lists contributors by username per
   > language; email to be left off that list. Your translations are
   > included in your data export. Deleting your account removes the ones
   > waiting for review and the rejected ones; approved text stays
   > published without your name.
4. **The retention table**, one row. The installer flattens each row to
   "Name: description", so the right cell reads as a sentence on its own:
   "| Translations you sent | Pending and rejected ones are deleted with
   the account; approved text stays published but loses your name, and
   the hashed IP is redacted |"
5. **The export list**, in "Your controls": add "translations you sent"
   to the Export my data bullet.
6. **The delete paragraph**, in the same section: "Translations you sent
   that were approved stay published without your name; the rest are
   deleted."
7. **The GitHub processor row**: "Serves update checks, downloads, this
   repository, and the guides and translation pages."
8. **The effective date** at the top, to the release date.

`installer/TrueforceForAll.iss` line 37 moves
`#define LegalRevision` from `"6"` to `"7"` in that release. The cost is
deliberate and worth naming in the release notes: every updating user
re-accepts the GPL, EULA and privacy pages once, which is exactly what
`ShouldSkipPage`'s revision match is for.

### 10.2 Plugin copy

A `WelcomeWindow` bullet, since the fetch runs by default: "Community
features also fetch reviewed translations of the plugin's own text when
you run it in another language." The Community master-toggle tooltip
names the fetch, because it is the only consent surface that enumerates
what goes online. Four keys:
`Settings_LanguageCommunityCheck` ("Use community translation fixes for
the plugin's text (online)"), `Settings_LanguageCommunityCheck_Tip`,
`Settings_LanguageHelp`, and `Translate_SendConfirmBody`, whose sentence
the web page repeats before its own first send.

### 10.3 supabase/README.md

The file count moves from 135 to 137 in **both** places, line 11 and the
line inside the do-not-run-`migration repair` warning, which is the
paragraph most likely to be read under pressure. It gains a Translations
line in the subsystem list, the four anon-executable reads by name, the
five unreadable tables plus the `profiles` column as a sixth object, the
note that anonymous sign-ins stay disabled, the rate limits of section 6,
and the moderation entries below.

### 10.4 Appointing and removing a reviewer

Appoint, in Studio with the service key:

```sql
insert into public.translation_reviewers (user_id, cultures, added_by)
     values ('<uid from auth.users>', '{es}', 'studio');
```

Remove one language, or the whole row with a delete. Past approvals
stand either way.

```sql
update public.translation_reviewers
   set cultures = array_remove(cultures, 'es')
 where user_id = '<uid>';
```

Opening a language is a reviewer row plus `accepting = true` plus a
machine pass shipped as its `Languages\<tag>.json`, so a volunteer
corrects rows instead of filling blanks. The other six SimHub tags read
"ask on Discord to open this language" until then.

### 10.5 Bans, credit opt-outs and bad rows

- **Ban a translator** by account uuid, or by `ip:<sha256>` for a
  web-only abuser who leaves no device code. `submit_translations` checks
  both forms.

  ```sql
  insert into public.submitter_blocked (submitter_id, reason, banned_until)
       values ('ip:<hash>', 'translation flood', null);
  ```

- **Honor a credit opt-out** with one update. `translation_contributors`
  skips that account from the next call.

  ```sql
  update public.profiles set hide_from_translation_credits = true
   where id = '<uid>';
  ```

- **A bad live row** is gone from every install on the next fetch, or at
  once with `LOCFETCH`, after a reviewer revokes it. Once it has been
  merged into a shipped file, only a fixed shipped file removes it.
- **Roll back a hijacked reviewer session.** The row trigger bumps the
  revision, so the fleet drops those rows on the next fetch, and the
  Discord line per approval call is what makes the timestamp findable.

  ```sql
  update public.translations
     set status = 'rejected', review_note = 'session rollback'
   where reviewer = '<uid>' and reviewed_at > '<timestamp>';
  ```

### 10.6 Migration 0138: the account RPCs (APPLIED 2026-09-28)

**Owner, 2026-09-28: a deletion takes the name off the work, and removes
nothing.** The delete of non-approved rows this section used to specify is
gone: a row that helped is a contribution whether or not it is the one
serving today, and `translation_contributors` already collapses a nameless
submitter into `(anonymous)`. What shipped, before the `delete from
auth.users`:

```sql
select count(*) into v_translations from public.translations where submitter = v_uid;
update public.translations set source_ip_hash = 'redacted'
 where submitter = v_uid;
```

The submitter itself needs no statement: the column nulls through the
foreign key's `on delete set null`, which is what leaves the text with no
author. The count goes into the receipt as `translations_kept`, and the
plugin's deletion dialog now says presets *and translations* stay.

The literal `'redacted'` is why `source_ip_hash`'s check admits it beside
a 64-hex digest (section 2.4); with a digest-only check this update makes
`delete_my_account` raise for every contributor.

`export_my_data` gains a `translations` key with culture, key, text,
status, source, note, timestamps and `review_note`.

Both bodies must be captured from `pg_get_functiondef` of the live
objects on the apply date, never from a file. `0097` last replaced
`delete_my_account`, and `0113` deliberately added its arcade deletion as
a trigger beside it rather than as more lines inside it; `0115` last
replaced `export_my_data`. The migration header records which live
definition each one started from and the date.

### 10.7 Migration 0141: the plural forms English does not have (APPLIED 2026-09-30)

A counted string ships as two English rows, `X.one` and `X.other`, because
English has two forms. Most languages do not. Russian needs four, Arabic
six, Japanese one. Those forms are real keys with real translations and no
English row of their own: they are written against the English plural.

Three things followed from that, and none of them worked:

- `_submit_translation_row` looked `X.few` up in `english_strings`, did not
  find it, and told the translator the key was not one the server holds;
- `translation_progress` joined `translations` to `english_strings` on the
  key, so a form English lacks could never be current and every one of them
  landed in `approved_other`, which is the re-approval queue;
- the same query counted every English row towards the total, so a Japanese
  translator, who will never write `X.one`, could not reach 100 percent.

The migration adds two functions and changes the three call sites that need
them.

```sql
public._loc_english_key(p_key text)      -- the English row that governs a key
public._loc_progress_unit(p_key text)    -- one counted string is one unit
```

`_loc_english_key` returns the key itself when English holds it, which
covers every ordinary key and both of the forms English ships; otherwise the
`.other` row of the same base; otherwise null, so a stray key is still
refused. `_loc_progress_unit` collapses a plural form onto its base with a
`.#` suffix, which matters because `Header_MoreIssues` exists as a plain key
beside `Header_MoreIssues.one` and `.other`, and a bare collapse would count
the two as one.

Progress is now counted in strings rather than in rows: a counted string is
one unit however many forms the language writes for it. Counting forms would
put Japanese permanently short of the total and Arabic permanently over it.
A language reaches the unit with any one form of it, which is generous to a
half-finished set and never wrong in the direction that matters.

**Which forms a language uses stays out of the database.** That rule lives
in `LocPlurals.cs`, where it is asserted count by count in
`LocPluralTests`, and a second copy in SQL would drift from it. The server
checks that a form was written against English it holds, which is the part
it can know. `validate.ps1` takes the same position.

The apply was verified four ways: `_loc_english_key('Header_MoreIssues.few')`
returns `Header_MoreIssues.other` and `_loc_english_key('Nothing_Here.few')`
returns null; `_loc_progress_unit` separates the collapsed base from the
plain key; `translation_progress` reports a total of 2,932 units against
2,975 live English keys; and `_submit_translation_row` returns `approved`
for `Achievements_JoinDiscordRole.many`, inside a transaction that was rolled
back, leaving no row behind.

---

## 11. Apply checklist

Per `supabase/README.md`: commit the file on `dev` first, apply with
`supabase db query --linked -f <file>`, then verify the objects exist.
Never `migration repair`, `db push`, `db pull` or `db reset`.

1. **Probe pgcrypto's schema, before writing the DDL.**

   ```sql
   select extnamespace::regnamespace from pg_extension where extname = 'pgcrypto';
   ```

   Write the `sha256` generated column with whatever this returns. A
   generated column resolves the function at DDL time and never consults
   `search_path` afterwards, so a wrong qualification fails the
   `create table` outright, and the hash vectors cannot catch it because
   they never get to run.
2. Apply `0136_translations.sql`, then `0137_translation_rpcs.sql`, then
   `0138_translations_privacy_hooks.sql` when it exists.
3. Existence. Five rows, all `t`, for the tables; one row for the
   column; both triggers; both partial uniques and the three query
   indexes.

   ```sql
   select relname, relrowsecurity from pg_class
    where relname in ('translation_cultures','english_strings',
                      'english_strings_history','translations',
                      'translation_reviewers');
   select column_name from information_schema.columns
    where table_name = 'profiles'
      and column_name = 'hide_from_translation_credits';
   select tgname from pg_trigger
    where tgrelid = 'public.translations'::regclass;
   select indexname from pg_indexes where tablename = 'translations';
   ```
4. ACLs. `has_function_privilege('anon', '<sig>', 'execute')` is true for
   exactly `list_approved_translations`, `translation_pending_counts`,
   `translation_progress` and `translation_contributors`, and false for
   every write, reviewer and service function;
   `has_table_privilege` for `select` is false for both `anon` and
   `authenticated` on all five new tables, and false for `anon` on
   `profiles`. A true here is the 0129 trap and a missed revoke.
5. All three hash vectors:
   `select encode(<pgcrypto>.digest('Sign in','sha256'),'hex')` with the
   schema step 1 returned, the U+2026 one and
   the `{0}` one, against section 4. A wrong second value means the
   server encoding is not UTF-8 and the whole contract is off.
6. The constraints bite. Separate inserts carrying `chr(7)`,
   `chr(8206)`, `chr(8212)`, a hyphen pair and 2,001 characters must
   each fail, and so must a key `9bad`.
7. Seed the reviewer row in Studio, not in a file (section 10.4).
8. Deploy `report-notify` with the translation branch, and set
   `DISCORD_TRANSLATE_CHANNEL_ID`. With it unset the branch logs a
   DRY-RUN and posts nothing, so steps 9 and 10 can run first.
9. `push-english.ps1 -EnJson ... -Version <dev version> -Ref dev`. The
   receipt's `live` equals the pushed file's own key count (598 at
   8556e27, 794 in the working tree that day, about 2,000 once the
   conversion finishes; re-measure), and
   `select sha256 from english_strings where key = 'Account_AccountAuth'`
   equals the first vector.
10. Smoke test with a throwaway account, never a real person's record.
    Set `accepting = true` on `es` first, and back to false afterwards
    unless the Spanish pass has already shipped. Submit one row, see it in
    `list_my_translations` and in `list_translation_queue`, approve it,
    read it back through `list_approved_translations('es')` with the anon
    key at `revision = 1`, call again with `p_known_revision = 1` for
    `unchanged`, revoke it and see `revision = 2` with an empty set. Then
    submit the row again, approve the new row, and delete the account: its pending, rejected and
    withdrawn rows must be gone, and its approved row must survive with a
    null `submitter` and `source_ip_hash = 'redacted'`. A check that
    admits only a 64-hex digest makes `delete_my_account` raise right
    here, which is what this step exists to catch.
11. Schedule `tf4all-translation-nudge` and `tf4all-translation-gc`, then
    check `cron.job` for exactly one row each.
12. Update `supabase/README.md` (section 10.3).

---

## 12. Effort and order

About 21 and a half working days, and Phase 2's window shell is not in
that number: 4 backend (2.5 of it migration 0135), 3 tooling, 7.5 page,
6 plugin, 1 documentation.


Order:

1. The store layer, `LocFile`, `LocPlaceholderRules` and the Core.Tests
   check `LocValuesNeverBecomeUrlsOrPaths`, whose targets all exist
   today. No server dependency.
2. Both migrations, applied and verified with `push-english.ps1` in hand
   and the reviewer and `es` rows written by hand.
3. `context.ps1` and the dev English push, so the page has English and
   context to render. From here on, every converted slice that lands on
   `dev` means another `push-english.ps1 -Ref dev` (section 9.1).
4. The page, which is the volume path and can start against a mock,
   together with `pages.yml`'s text-only step, whose three files this
   item creates.
5. The fetch client, in the release that carries the PRIVACY.md edits and
   `LegalRevision 7`.
6. Send, the status glyphs, Pick mode and the divergence marks, on top of
   Phase 2's window shell.
7. `merge-community.ps1`, at the first release with approved rows.

Spanish opens once Phase 3's first Spanish pass ships as
`Languages\es.json`: the page and the whole review loop can be built
before that, but there is nothing for a volunteer to correct until it
exists. A second language additionally needs Phase 4's `retranslate.ps1`
for its machine pass. Nothing else here waits on Phase 4.

---

## 13. Exit criteria

- Both migrations applied; the six objects exist; `relrowsecurity` true
  on all five new tables; the apply checklist's pgcrypto probe recorded
  in the 0135 header.
- `has_function_privilege('anon', ...)` true for exactly the four reads
  and false for every write and reviewer function;
  `has_table_privilege` false for both client roles on all five new
  tables and false for `anon` on `profiles`.
- All three hash vectors match in Postgres, in `LocEnglishHashVectors`
  and in the page's self-check.
- Inserts carrying `chr(7)`, `chr(8206)`, `chr(8212)`, a hyphen pair and
  2,001 characters each fail; a language with `accepting = false` refuses
  a submit with its `P0001` sentence.
- `pages.yml` fails on a planted `innerHTML` in `translate.js`;
  Core.Tests fails on a planted `Languages/en.context.json` and on a
  planted `Loc.T` result inside a `Process.Start` argument.
- A throwaway account sends 50 rows in one call; a 51-row call is refused
  whole; the first call made when the hour already holds 300 rows is
  refused whole with the server's sentence verbatim.
- With Phase 2's window shell in the build, the window opened from
  `TRANSLATE` shows pending, approved and declined glyphs. Every window
  criterion here is 3b's exit given Phase 2; the backend, page and fetch
  criteria stand on their own.
- Deleting the throwaway account leaves its approved row serving, with a
  null `submitter` and `source_ip_hash = 'redacted'`.
- Setting `accepting = false` on `es` refuses a submit and changes
  nothing about what `list_approved_translations('es')` serves.
- A second signed-in account sees "2 proposals pending" on that key and
  neither the text nor a username.
- `approve_with_edit` still credits the original submitter and leaves its
  source row `superseded`; a row approved against an earlier English keeps
  serving the build that displays that English while a new approved row
  serves the current one; a repeat `approve_stale` returns the existing id
  instead of raising.
- Revoke on a live row removes it from a rig install within one
  `LOCFETCH`, and that key falls back to the shipped file or to English,
  never to a superseded row.
- Planted rows holding U+202E, a newline their English lacks, or five
  times an English of 64 characters or more are dropped and counted in
  `LOCSTATUS`; a 3 MB response abandons the fetch with the previous file
  intact; a `truncated` response keeps the file and logs one Warn.
- One Discord line per approval call in the private channel; one daily
  line for a language with pending rows; `tf4all-translation-gc` removes
  a planted 200-day-old pending row from an account with no approval.
- On a `de-AT` Windows with SimHub's Culture unset and a planted shipped
  `de.json`, the plugin loads `de` rather than falling back to English.
  That is the common path: a `de-DE` Windows with a planted `de-DE.json`
  beside it covers the exact-match case if a region file ever ships.
- PRIVACY.md, the welcome and Settings copy, and `LegalRevision 7` ship
  in the release that first carries the fetch.
- `BackupSelfTest` passes with `UseCommunityTranslations` classified.
