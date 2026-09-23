-- 0076: a `share` category and an `action` field so a message's button can
-- trigger an in-plugin action (the Spread-the-word share modal) instead of
-- opening a URL. action is null for normal messages, or 'share'. Idempotent.

-- 1. share category
alter table motd drop constraint if exists motd_category_valid;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday','tip','joke',
                'reminder','vibes','milestone','feature','quote','support','share','sponsored'));
    end if;
end $$;

-- 2. action column (in-plugin button action)
alter table motd add column if not exists action text;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_action_valid') then
        alter table motd add constraint motd_action_valid
            check (action is null or action in ('share'));
    end if;
end $$;

-- 3. post_motd gains an optional action arg (replaces the 8-arg version).
drop function if exists post_motd(text,text,text,text,text,text,timestamptz,timestamptz);
create or replace function post_motd(
    p_body       text,
    p_link_url   text default null,
    p_importance text default 'normal',
    p_kind       text default 'scheduled',
    p_category   text default 'announcement',
    p_link_label text default null,
    p_starts_at  timestamptz default null,
    p_ends_at    timestamptz default null,
    p_action     text default null
) returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    insert into motd (kind, importance, category, body, link_url, link_label, starts_at, ends_at, action)
    values (p_kind, p_importance, p_category, p_body, p_link_url, p_link_label, p_starts_at, p_ends_at, p_action)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_motd(text,text,text,text,text,text,timestamptz,timestamptz,text)
    from anon, authenticated, public;
