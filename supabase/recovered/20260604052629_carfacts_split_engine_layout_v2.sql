-- Clean legacy engine_layout test rows first so the new check constraint can land.
delete from car_fact_consensus_votes where consensus_fact_type = 'engine_layout';
delete from car_fact_consensus where fact_type = 'engine_layout';
delete from car_fact_submissions where fact_type = 'engine_layout';

alter table car_fact_submissions
    drop constraint if exists car_fact_submissions_fact_type_check;
alter table car_fact_submissions
    add constraint car_fact_submissions_fact_type_check
    check (fact_type in ('engine_cylinders', 'engine_config', 'car_name', 'redline'));

create or replace function normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
returns jsonb language plpgsql immutable
set search_path = '' as $$
declare v_int int; v_text text;
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_cylinders' then
        if pg_catalog.jsonb_typeof(p_payload->'cyl') <> 'number' then return null; end if;
        v_int := (p_payload->>'cyl')::int;
        if v_int < 1 or v_int > 16 then return null; end if;
        return pg_catalog.jsonb_build_object('cyl', v_int);

    elsif p_fact_type = 'engine_config' then
        if pg_catalog.jsonb_typeof(p_payload->'config') <> 'string' then return null; end if;
        v_text := pg_catalog.upper(pg_catalog.btrim(p_payload->>'config'));
        if v_text not in ('SINGLE','INLINE','BOXER','V60','V90EVEN',
                          'V8CROSSPLANE','V8FLATPLANE','V6ODDFIRE',
                          'VTWIN90','VTWIN45','ROTARY') then
            return null;
        end if;
        return pg_catalog.jsonb_build_object('config', v_text);

    elsif p_fact_type = 'car_name' then
        if pg_catalog.jsonb_typeof(p_payload->'name') <> 'string' then return null; end if;
        v_text := pg_catalog.btrim(p_payload->>'name');
        if pg_catalog.length(v_text) < 2 or pg_catalog.length(v_text) > 96 then return null; end if;
        return pg_catalog.jsonb_build_object('name', v_text);

    elsif p_fact_type = 'redline' then
        if pg_catalog.jsonb_typeof(p_payload->'rpm') <> 'number' then return null; end if;
        v_int := (p_payload->>'rpm')::int;
        if v_int < 500 or v_int > 25000 then return null; end if;
        return pg_catalog.jsonb_build_object('rpm', v_int);
    end if;
    return null;
end;
$$;