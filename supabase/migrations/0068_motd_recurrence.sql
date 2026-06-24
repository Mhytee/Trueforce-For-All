-- 0068: recurring (yearly) MOTD messages.
--
-- A recurring message is a single scheduled row that repeats on a calendar
-- date every year (birthdays, RIP notes, "on this day"). Editing the body of
-- one row changes what shows next occurrence without touching the schedule.
--
-- Recurrence is evaluated CLIENT-SIDE in the user's local time, NOT in this
-- RLS policy, for two reasons: (1) "on this day" events should light up on the
-- user's local date, not UTC; (2) the client holds the rule and re-checks it
-- live, so a message activates exactly at local midnight regardless of the ~6h
-- fetch cache. starts_at / ends_at therefore stay the OVERALL validity window
-- (when the rule is in effect at all, usually null = always); the per-year
-- date is recur_month / recur_day, shown for recur_window_days. The existing
-- read policy needs no change: a recurring row with an open overall window is
-- returned year-round and the client decides each day.
--
-- Dismiss behavior also branches on recurrence (client-side): a recurring
-- message dismisses for the current occurrence only and returns next year,
-- while a one-off scheduled message still dismisses forever.
--
-- Idempotent per repo convention (re-runnable).

alter table motd add column if not exists recurrence        text not null default 'none';
alter table motd add column if not exists recur_month       int;
alter table motd add column if not exists recur_day         int;
alter table motd add column if not exists recur_window_days int not null default 1;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_recurrence_valid') then
        alter table motd add constraint motd_recurrence_valid
            check (recurrence in ('none','yearly'));
    end if;
    if not exists (select 1 from pg_constraint where conname = 'motd_recur_month_range') then
        alter table motd add constraint motd_recur_month_range
            check (recur_month is null or recur_month between 1 and 12);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'motd_recur_day_range') then
        alter table motd add constraint motd_recur_day_range
            check (recur_day is null or recur_day between 1 and 31);
    end if;
    if not exists (select 1 from pg_constraint where conname = 'motd_recur_window_range') then
        alter table motd add constraint motd_recur_window_range
            check (recur_window_days between 1 and 60);
    end if;
    -- A yearly message must carry its month + day.
    if not exists (select 1 from pg_constraint where conname = 'motd_recur_fields_present') then
        alter table motd add constraint motd_recur_fields_present
            check (recurrence <> 'yearly' or (recur_month is not null and recur_day is not null));
    end if;
end $$;

-- ====================================================================
-- post_motd_annual: owner helper for a yearly message (service_role only)
-- ====================================================================
-- e.g.:
--   select post_motd_annual('Happy birthday, Ayrton Senna', 3, 21);
--   select post_motd_annual('In memory of <driver>', 5, 1, null, 'normal', 'community', 3);
-- Defaults: normal / community / 1-day window. Always-valid overall window
-- (no starts_at / ends_at); pass those via a direct insert if you need to
-- start or retire the recurrence. SECURITY DEFINER + revoked from public roles.
create or replace function post_motd_annual(
    p_body        text,
    p_month       int,
    p_day         int,
    p_link_url    text default null,
    p_importance  text default 'normal',
    p_category    text default 'community',
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
