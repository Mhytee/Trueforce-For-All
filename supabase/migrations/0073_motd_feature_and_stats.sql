-- 0073: a `feature` category (plugin-capability / hype messages) and live-stat
-- MOTD messages whose number refreshes daily from the database.
--
-- A stat message stores a template (stat_template) with a {n} token and a
-- stat_key naming the count. A daily cron renders body = template with the
-- current count, so "the community has shared {n} presets" stays accurate with
-- no manual edits. Idempotent.

-- 1. feature category
alter table motd drop constraint if exists motd_category_valid;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday','tip','joke',
                'reminder','vibes','milestone','feature','sponsored'));
    end if;
end $$;

-- 2. live-stat columns
alter table motd add column if not exists stat_key      text;
alter table motd add column if not exists stat_template text;

-- 3. render every stat message's body from its template + the live count.
create or replace function refresh_stat_motd()
returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    r       record;
    v_count bigint;
begin
    for r in select id, stat_key, stat_template from motd where stat_key is not null loop
        v_count := case r.stat_key
            when 'presets'  then (select count(*) from presets)
            when 'carfacts' then (select count(*) from car_fact_submissions)
            else null
        end;
        if v_count is not null and r.stat_template is not null then
            update motd
               set body = replace(r.stat_template, '{n}', to_char(v_count, 'FM999,999,999')),
                   updated_at = now()
             where id = r.id;
        end if;
    end loop;
end;
$$;

revoke execute on function refresh_stat_motd() from anon, authenticated, public;

-- 4. seed/update a stat message (idempotent by stat_key). Owner-only.
create or replace function post_stat_motd(
    p_stat_key  text,
    p_template  text,
    p_kind      text default 'pool',
    p_category  text default 'feature')
returns uuid
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_id uuid;
begin
    select id into v_id from motd where stat_key = p_stat_key limit 1;
    if v_id is not null then
        update motd set stat_template = p_template, kind = p_kind, category = p_category
            where id = v_id;
    else
        insert into motd (kind, importance, category, body, active, stat_key, stat_template)
        values (p_kind, 'normal', p_category, p_template, true, p_stat_key, p_template)
        returning id into v_id;
    end if;
    return v_id;
end;
$$;

revoke execute on function post_stat_motd(text, text, text, text) from anon, authenticated, public;

-- 5. daily refresh cron (noon UTC).
select cron.schedule('tf4all-motd-stats', '0 12 * * *',
    $job$ select public.refresh_stat_motd(); $job$);
