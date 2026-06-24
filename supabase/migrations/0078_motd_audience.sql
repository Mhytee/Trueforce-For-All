-- 0078: per-user audience targeting for MOTD. A nullable `audience` tag the
-- client evaluates against LOCAL signals (supporter status, Discord link,
-- contribution recency). The server stays global/anon-read; targeting is purely
-- a client-side filter, so it also keeps working off the stale cache.
--   null              -> everyone
--   supporter         -> only confirmed supporters (thank-you messages)
--   non_supporter     -> only non-supporters (Patreon nudges)
--   non_discord       -> only users who have NOT linked Discord
--   non_sharer        -> only users who have NOT shared a preset recently
--   non_voter         -> only users who have NOT voted recently
--   non_factsubmitter -> only users who have NOT submitted a car fact recently
-- Idempotent.
alter table motd add column if not exists audience text;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_audience_valid') then
        alter table motd add constraint motd_audience_valid
            check (audience is null or audience in (
                'supporter','non_supporter','non_discord',
                'non_sharer','non_voter','non_factsubmitter'));
    end if;
end $$;

-- post_motd gains an optional p_audience (replaces the 9-arg version from 0076).
drop function if exists post_motd(text,text,text,text,text,text,timestamptz,timestamptz,text);
create or replace function post_motd(
    p_body       text,
    p_link_url   text default null,
    p_importance text default 'normal',
    p_kind       text default 'scheduled',
    p_category   text default 'announcement',
    p_link_label text default null,
    p_starts_at  timestamptz default null,
    p_ends_at    timestamptz default null,
    p_action     text default null,
    p_audience   text default null
) returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    insert into motd (kind, importance, category, body, link_url, link_label, starts_at, ends_at, action, audience)
    values (p_kind, p_importance, p_category, p_body, p_link_url, p_link_label, p_starts_at, p_ends_at, p_action, p_audience)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_motd(text,text,text,text,text,text,timestamptz,timestamptz,text,text)
    from anon, authenticated, public;
