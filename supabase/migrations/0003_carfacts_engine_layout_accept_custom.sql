-- Extend the engine_layout payload schema to accept Custom engines.
-- Payload shape becomes:
--   { "layout": "CUSTOM", "custom": { "name": "...", "pattern": "...",
--                                      "electric": bool,
--                                      "electric_mode": "MUTEDHUM"|"SILENT" } }
-- For non-custom layouts the payload stays { "layout": "INLINE6" } etc.
-- This keeps engine information in one fact_type rather than two,
-- so consensus / variants / dedup all use the same plumbing.
-- Mirrors what was applied live (carfacts_engine_layout_accept_custom).

create or replace function public.normalize_car_fact_payload(
    p_fact_type text, p_payload jsonb
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_int int; v_text text;
    v_custom jsonb;
    v_name text; v_pattern text;
    v_electric boolean; v_mode text;
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_layout' then
        if pg_catalog.jsonb_typeof(p_payload->'layout') <> 'string' then return null; end if;
        v_text := pg_catalog.upper(pg_catalog.btrim(p_payload->>'layout'));

        if v_text = 'CUSTOM' then
            -- Custom engines carry the full def in a nested 'custom'
            -- object. Validates: name (string 2..96), pattern (string,
            -- <= 512), electric (bool), electric_mode (optional enum).
            v_custom := p_payload->'custom';
            if pg_catalog.jsonb_typeof(v_custom) <> 'object' then return null; end if;

            if pg_catalog.jsonb_typeof(v_custom->'name') <> 'string' then return null; end if;
            v_name := pg_catalog.btrim(v_custom->>'name');
            if pg_catalog.length(v_name) < 2 or pg_catalog.length(v_name) > 96 then return null; end if;

            -- pattern can be empty (silence) but must be a string
            if pg_catalog.jsonb_typeof(v_custom->'pattern') <> 'string' then return null; end if;
            v_pattern := v_custom->>'pattern';
            if pg_catalog.length(v_pattern) > 512 then return null; end if;

            if pg_catalog.jsonb_typeof(v_custom->'electric') <> 'boolean' then return null; end if;
            v_electric := (v_custom->>'electric')::boolean;

            v_mode := pg_catalog.upper(pg_catalog.btrim(coalesce(v_custom->>'electric_mode', 'MUTEDHUM')));
            if v_mode not in ('MUTEDHUM','SILENT') then return null; end if;

            return pg_catalog.jsonb_build_object(
                'layout', 'CUSTOM',
                'custom', pg_catalog.jsonb_build_object(
                    'name',          v_name,
                    'pattern',       v_pattern,
                    'electric',      v_electric,
                    'electric_mode', v_mode));
        end if;

        -- Built-in EngineLayout whitelist (unchanged).
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
