-- Weekly GC of car_fact_submissions rows that no longer contribute to
-- consensus. _recompute_car_fact_consensus uses distinct on (submitter_id)
-- ... order by created_at desc, so anything except the latest from a
-- submitter for the same (game, car_id, fact_type, variant_signature)
-- is structurally non-contributing audit trail. Safe to delete and keeps
-- table size bounded as submission traffic grows.
--
-- Frequency intentionally weekly. The non-latest rows are inert until
-- garbage-collected so daily runs would just burn quota. Sunday 03:30
-- UTC lands in a low-traffic window for most sim-racing communities.
-- Mirrors what was applied live (carfacts_gc_superseded_submissions).

create extension if not exists pg_cron with schema pg_catalog;

create or replace function public._gc_superseded_car_fact_submissions()
returns int
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_deleted int;
begin
    with ranked as (
        select id,
               row_number() over (
                   partition by submitter_id, game, car_id, fact_type, variant_signature
                   order by created_at desc, id desc
               ) as rn
          from car_fact_submissions
    ),
    deleted as (
        delete from car_fact_submissions
         where id in (select id from ranked where rn > 1)
         returning 1
    )
    select count(*)::int into v_deleted from deleted;
    return coalesce(v_deleted, 0);
end;
$$;

do $$
begin
    perform cron.unschedule('gc_superseded_car_fact_submissions');
exception
    when others then
        null;
end $$;

select cron.schedule(
    'gc_superseded_car_fact_submissions',
    '30 3 * * 0',  -- weekly Sunday 03:30 UTC
    $cmd$ select public._gc_superseded_car_fact_submissions(); $cmd$
);
