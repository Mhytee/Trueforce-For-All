-- 0070: self-incrementing anniversary count for yearly MOTD messages.
--
-- An optional anchor_year plus a {years} token in the body. The client
-- substitutes (occurrence year - anchor_year), clamped at 0, so a message like
-- "Trueforce For All turns {years} today" ages itself every year with no edit.
-- Generic to any anniversary, not just the plugin birthday. {years} is the only
-- supported template token (kept deliberately minimal).
--
-- Idempotent (re-runnable).

alter table motd add column if not exists anchor_year int;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_anchor_year_range') then
        alter table motd add constraint motd_anchor_year_range
            check (anchor_year is null or anchor_year between 1900 and 2200);
    end if;
end $$;

-- Recreate post_motd_annual with the optional anchor year. Drop the old 8-arg
-- signature first so there's no confusing overload.
drop function if exists post_motd_annual(text,int,int,text,text,text,int,text);

create or replace function post_motd_annual(
    p_body        text,
    p_month       int,
    p_day         int,
    p_link_url    text default null,
    p_importance  text default 'normal',
    p_category    text default 'onthisday',
    p_window_days int default 1,
    p_link_label  text default null,
    p_anchor_year int default null
) returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    insert into motd (kind, importance, category, body, link_url, link_label,
                      recurrence, recur_month, recur_day, recur_window_days, anchor_year)
    values ('scheduled', p_importance, p_category, p_body, p_link_url, p_link_label,
            'yearly', p_month, p_day, greatest(1, coalesce(p_window_days, 1)), p_anchor_year)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_motd_annual(text,int,int,text,text,text,int,text,int)
    from anon, authenticated, public;
