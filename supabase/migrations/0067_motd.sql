-- 0067: Message of the Day (MOTD) feed.
--
-- A small, owner-authored strip shown at the top of the plugin UI. Reaches
-- users who are not in Discord, adds personality, and is a future home for
-- time-limited sponsored messages. Design doc: docs/motd-design.md.
--
-- Posture (mirrors the car_fact_consensus model in 0001):
--   * The motd table is PUBLIC-READ, but the RLS policy only ever returns rows
--     that are active AND inside their schedule window. The anon key ships in
--     the plugin DLL, so the read policy must not expose embargoed / future
--     rows: a sponsor message scheduled for next week stays invisible in the
--     API until its starts_at passes. now() in the policy USING clause is the
--     gate, and it also expires ended messages automatically (no cron needed).
--   * NO anon / authenticated write path. Authoring is owner-only via the
--     service_role key (MCP / Studio / psql), through post_motd() or a direct
--     insert. No in-plugin admin surface ships in the binary.
--   * Serving: clients read active rows via PostgREST with the anon key (the
--     same mechanism that already serves car_fact_consensus to every plugin on
--     startup) and cache the result ~6h. If that load ever matters, a
--     pre-rendered static feed file behind the CDN is a drop-in upgrade and the
--     client just swaps the URL.
--
-- Idempotent per repo convention (re-runnable).

create extension if not exists "pgcrypto";  -- gen_random_uuid

-- ====================================================================
-- motd: owner-authored, public-read (active window only)
-- ====================================================================
-- kind:
--   'scheduled' - a specific message / announcement. Shown whenever active;
--                 dismissed permanently per-message on the client (seen-id).
--   'pool'      - evergreen filler (tips, jokes, reminders). The client picks
--                 one per day by a date seed and shows it only when no
--                 scheduled message is active; dismissed for-the-day only.
-- importance drives the client's All / Important / None tier filter.
-- category is descriptive; drives future granular toggles and the eventual
--   Patreon-gated "hide sponsored".
create table if not exists motd (
    id          uuid primary key default gen_random_uuid(),
    kind        text not null default 'scheduled'
                  check (kind in ('scheduled','pool')),
    importance  text not null default 'normal'
                  check (importance in ('normal','important')),
    category    text not null default 'announcement'
                  check (category in ('announcement','community','tip','joke','reminder','sponsored')),
    body        text not null check (length(btrim(body)) between 1 and 500),
    -- https-only, enforced at the DB so a bad row can never ship a non-https
    -- link to the strip's link handler.
    link_url    text check (link_url is null or link_url ~ '^https://'),
    link_label  text check (link_label is null or length(btrim(link_label)) between 1 and 40),
    starts_at   timestamptz,            -- null = active immediately
    ends_at     timestamptz,            -- null = never expires
    active      boolean not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now()
);

-- Feed lookup helper (active rows; now() comparison happens at query time).
create index if not exists motd_active_window_idx
    on motd (starts_at, ends_at) where active;

-- ====================================================================
-- Permissions + RLS
-- ====================================================================
-- Anon / authenticated may SELECT, but the policy only returns active,
-- in-window rows. No write grants, so service_role (owner) is the only writer.
revoke all on motd from anon, authenticated, public;
grant select on motd to anon, authenticated;

alter table motd enable row level security;

drop policy if exists motd_public_read_active on motd;
create policy motd_public_read_active
    on motd for select
    using (
        active
        and (starts_at is null or starts_at <= now())
        and (ends_at   is null or ends_at   >  now())
    );

-- ====================================================================
-- post_motd: owner convenience helper (service_role only)
-- ====================================================================
-- One-liner authoring, e.g.:
--   select post_motd('We just shipped v0.1.25!', 'https://tf4all.mhytee.com', 'important');
--   select post_motd('Drive safe out there', null, 'normal', 'pool', 'joke');
--   -- a one-week sponsor run:
--   select post_motd('Thanks to <sponsor>', 'https://...', 'normal',
--                    'scheduled', 'sponsored', null, now(), now() + interval '7 days');
-- Defaults: scheduled / announcement / active now / never expires. SECURITY
-- DEFINER + revoked from every public role, so only the service_role key (never
-- the shipped anon key) can call it.
create or replace function post_motd(
    p_body       text,
    p_link_url   text default null,
    p_importance text default 'normal',
    p_kind       text default 'scheduled',
    p_category   text default 'announcement',
    p_link_label text default null,
    p_starts_at  timestamptz default null,
    p_ends_at    timestamptz default null
) returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    insert into motd (kind, importance, category, body, link_url, link_label, starts_at, ends_at)
    values (p_kind, p_importance, p_category, p_body, p_link_url, p_link_label, p_starts_at, p_ends_at)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_motd(text,text,text,text,text,text,timestamptz,timestamptz)
    from anon, authenticated, public;

-- ====================================================================
-- Owner operations (run via service_role, NOT shipped in plugin)
-- ====================================================================
--   Post:    select post_motd('text', 'https://...', 'important');
--   Expire:  update motd set ends_at = now() where id = '<id>';
--   Pull:    update motd set active = false where id = '<id>';
--   Edit:    update motd set body = '...', updated_at = now() where id = '<id>';
--   List:    select id, kind, importance, body, starts_at, ends_at from motd order by created_at desc;
