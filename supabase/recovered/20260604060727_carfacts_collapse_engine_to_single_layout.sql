-- Collapse engine_cylinders + engine_config back into a single
-- engine_layout fact whose value is the full EngineLayout name (e.g.
-- "V8CROSSPLANE", "INLINE6") rather than the (cyl, config) pair. Every
-- EngineLayout uniquely encodes both axes; the split added a vote/Wilson
-- surface for axes users couldn't independently assert from the UI.

-- Clean test rows for the now-retired fact_types.
delete from car_fact_consensus_votes where consensus_fact_type in ('engine_cylinders','engine_config');
delete from car_fact_consensus where fact_type in ('engine_cylinders','engine_config');
delete from car_fact_submissions where fact_type in ('engine_cylinders','engine_config');

alter table car_fact_submissions
    drop constraint if exists car_fact_submissions_fact_type_check;
alter table car_fact_submissions
    add constraint car_fact_submissions_fact_type_check
    check (fact_type in ('engine_layout','car_name','redline'));

create or replace function normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
returns jsonb language plpgsql immutable
set search_path = '' as $$
declare v_int int; v_text text;
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_layout' then
        if pg_catalog.jsonb_typeof(p_payload->'layout') <> 'string' then return null; end if;
        v_text := pg_catalog.upper(pg_catalog.btrim(p_payload->>'layout'));
        -- Whitelist matches the EngineLayout enum (FiringPatterns.cs)
        -- excluding Auto / Electric / Custom: Auto means "I don't know",
        -- Electric is a different feature (no firing pattern), Custom is
        -- authored via the Custom Engine library (separate community
        -- pipeline if ever needed).
        if v_text not in (
            'SINGLE','TWIN','INLINE3','INLINE4','INLINE5','INLINE6',
            'BOXER4','BOXER6',
            'V6_60EVEN','V6_ODDFIRE','V8CROSSPLANE','V8FLATPLANE',
            'V10_72','V12_60','W12_W16',
            'VTWIN90','VTWIN45',
            'ROTARY1','ROTARY2','ROTARY3','ROTARY4'
        ) then
            return null;
        end if;
        return pg_catalog.jsonb_build_object('layout', v_text);

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