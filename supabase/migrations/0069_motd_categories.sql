-- 0069: richer MOTD category vocabulary.
--
-- Splits `holiday` and `onthisday` out of the catch-all `community`, which is
-- now specifically plugin-community (Discord events, contests, meetups):
--   community  - plugin community happenings
--   onthisday  - history / "on this day" (incl. driver birthdays, RIP notes)
--   holiday    - holiday recognition (Christmas, New Year, ...)
-- Adding category VALUES is vocabulary, not per-message complexity: each
-- message still sets exactly one category (or a helper defaults it).
--
-- post_motd_annual now defaults to `onthisday` (most annual messages are
-- history / commemoration); pass `holiday` for the festive ones.
--
-- Idempotent (re-runnable): swaps the inline category check from 0067 for a
-- named one, and CREATE OR REPLACE on the helper.

alter table motd drop constraint if exists motd_category_check;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in ('announcement','community','onthisday','holiday','tip','joke','reminder','sponsored'));
    end if;
end $$;

create or replace function post_motd_annual(
    p_body        text,
    p_month       int,
    p_day         int,
    p_link_url    text default null,
    p_importance  text default 'normal',
    p_category    text default 'onthisday',
    p_window_days int default 1,
    p_link_label  text default null
) returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    insert into motd (kind, importance, category, body, link_url, link_label,
                      recurrence, recur_month, recur_day, recur_window_days)
    values ('scheduled', p_importance, p_category, p_body, p_link_url, p_link_label,
            'yearly', p_month, p_day, greatest(1, coalesce(p_window_days, 1)))
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_motd_annual(text,int,int,text,text,text,int,text)
    from anon, authenticated, public;
