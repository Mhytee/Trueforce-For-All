-- 0079: a minimum-count gate for live-stat MOTD messages, so a running counter
-- never shows an awkwardly low (or zero) number. A stat row gets a stat_min;
-- the daily refresh sets active only when the live count is at/above it. null
-- stat_min = ungated (always active once it has data). Idempotent.

alter table motd add column if not exists stat_min int;

-- Render every stat body from its template + live count, AND gate visibility on
-- the threshold (active = count >= stat_min, or always when stat_min is null).
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
    for r in select id, stat_key, stat_template, stat_min from motd where stat_key is not null loop
        v_count := case r.stat_key
            when 'presets'  then (select count(*) from presets)
            when 'carfacts' then (select count(*) from car_fact_submissions)
            else null
        end;
        if v_count is not null and r.stat_template is not null then
            update motd
               set body = replace(r.stat_template, '{n}', to_char(v_count, 'FM999,999,999')),
                   active = (r.stat_min is null or v_count >= r.stat_min),
                   updated_at = now()
             where id = r.id;
        end if;
    end loop;
end;
$$;

revoke execute on function refresh_stat_motd() from anon, authenticated, public;

-- post_stat_motd gains an optional p_stat_min (replaces the 4-arg version). New
-- stats insert INACTIVE so the {n} template never flashes before the first
-- refresh renders + gates it.
drop function if exists post_stat_motd(text, text, text, text);
create or replace function post_stat_motd(
    p_stat_key  text,
    p_template  text,
    p_kind      text default 'pool',
    p_category  text default 'feature',
    p_stat_min  int  default null)
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
        update motd set stat_template = p_template, kind = p_kind,
                        category = p_category, stat_min = p_stat_min
            where id = v_id;
    else
        insert into motd (kind, importance, category, body, active, stat_key, stat_template, stat_min)
        values (p_kind, 'normal', p_category, p_template, false, p_stat_key, p_template, p_stat_min)
        returning id into v_id;
    end if;
    return v_id;
end;
$$;

revoke execute on function post_stat_motd(text, text, text, text, int) from anon, authenticated, public;
