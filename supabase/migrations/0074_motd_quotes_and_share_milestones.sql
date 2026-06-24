-- 0074: a `quote` category, and generalize milestones to fire off other count
-- sources (community sharing milestones), not just user accounts.
--
-- A milestone now carries a milestone_source ('accounts' | 'presets' |
-- 'carfacts'). The daily cron activates the highest just-crossed milestone PER
-- source, so account milestones and preset/correction milestones can each fire
-- on their own counter. Idempotent.

-- 1. quote category
alter table motd drop constraint if exists motd_category_valid;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday','tip','joke',
                'reminder','vibes','milestone','feature','quote','sponsored'));
    end if;
end $$;

-- 2. milestone source (existing milestones default to 'accounts').
alter table motd add column if not exists milestone_source text not null default 'accounts';

-- 3. generalized activation: per source, activate the highest just-crossed
--    never-shown milestone for ~7 days, skip lower ones jumped over.
create or replace function activate_due_milestones()
returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_sources text[] := array['accounts','presets','carfacts'];
    s        text;
    v_count  bigint;
    v_target uuid;
begin
    foreach s in array v_sources loop
        v_count := case s
            when 'accounts' then (select count(*) from auth.users)
            when 'presets'  then (select count(*) from presets)
            when 'carfacts' then (select count(*) from car_fact_submissions)
            else 0
        end;

        select id into v_target
            from motd
            where category = 'milestone' and milestone_source = s
              and milestone_count is not null and milestone_count <= v_count
              and ends_at is null
            order by milestone_count desc
            limit 1;
        if v_target is null then continue; end if;

        update motd set ends_at = now()
            where category = 'milestone' and milestone_source = s and ends_at is null
              and milestone_count <= v_count and id <> v_target;

        update motd set active = false
            where category = 'milestone' and milestone_source = s and active = true
              and id <> v_target;

        update motd set active = true, starts_at = now(),
                        ends_at = now() + interval '7 days', updated_at = now()
            where id = v_target;
    end loop;
end;
$$;

revoke execute on function activate_due_milestones() from anon, authenticated, public;

-- 4. post_milestone with a source (idempotent by source + count).
drop function if exists post_milestone(int, text);
create or replace function post_milestone(p_count int, p_body text, p_source text default 'accounts')
returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    select id into v_id from motd
        where category = 'milestone' and milestone_source = p_source and milestone_count = p_count
        limit 1;
    if v_id is not null then
        update motd set body = p_body where id = v_id;
        return v_id;
    end if;
    insert into motd (kind, importance, category, body, active, milestone_count, milestone_source)
    values ('scheduled', 'normal', 'milestone', p_body, false, p_count, p_source)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_milestone(int, text, text) from anon, authenticated, public;
