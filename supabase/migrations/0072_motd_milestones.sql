-- 0072: auto-activating user-count milestones.
--
-- A milestone is a motd row (category='milestone') that carries a
-- milestone_count threshold and ships INACTIVE. A daily cron counts registered
-- accounts and activates the highest just-crossed milestone for ~a week, shown
-- once. Lower milestones jumped over in a single step are marked processed so
-- they don't backlog. The body carries the number verbatim (no client token).
--
-- Idempotent (re-runnable).

alter table motd add column if not exists milestone_count int;

-- Seed/update one milestone row (idempotent by milestone_count). Inactive until
-- the cron activates it. Owner-only (service_role).
create or replace function post_milestone(p_count int, p_body text)
returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    select id into v_id from motd
        where category = 'milestone' and milestone_count = p_count
        limit 1;
    if v_id is not null then
        update motd set body = p_body where id = v_id;
        return v_id;
    end if;
    insert into motd (kind, importance, category, body, active, milestone_count)
    values ('scheduled', 'normal', 'milestone', p_body, false, p_count)
    returning id into v_id;
    return v_id;
end;
$$;

revoke execute on function post_milestone(int, text) from anon, authenticated, public;

-- Daily activation: show the highest never-shown milestone at/below the live
-- registered-account count for ~7 days; skip lower ones jumped over so they
-- don't backlog. SECURITY DEFINER so the cron can read auth.users + write motd.
create or replace function activate_due_milestones()
returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_count  bigint;
    v_target uuid;
begin
    select count(*) into v_count from auth.users;   -- registered accounts

    select id into v_target
        from motd
        where category = 'milestone' and milestone_count is not null
          and milestone_count <= v_count and ends_at is null
        order by milestone_count desc
        limit 1;
    if v_target is null then return; end if;

    -- Mark lower, never-shown, already-crossed milestones as processed.
    update motd set ends_at = now()
        where category = 'milestone' and ends_at is null
          and milestone_count <= v_count and id <> v_target;

    -- Only the newest milestone is active at a time.
    update motd set active = false
        where category = 'milestone' and active = true and id <> v_target;

    -- Activate the target for ~a week.
    update motd set active = true, starts_at = now(),
                    ends_at = now() + interval '7 days', updated_at = now()
        where id = v_target;
end;
$$;

revoke execute on function activate_due_milestones() from anon, authenticated, public;

-- Daily cron (noon UTC). pg_cron runs the SECURITY DEFINER function directly; no
-- edge function needed. cron.schedule upserts by job name.
select cron.schedule('tf4all-motd-milestones', '0 12 * * *',
    $job$ select public.activate_due_milestones(); $job$);
