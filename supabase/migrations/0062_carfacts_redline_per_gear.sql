-- Per-gear redline support in the community car-fact payload.
--
-- The 'redline' fact previously canonicalized to exactly { "rpm": N }. This
-- adds an optional per-gear override array so a variant can carry gear-specific
-- redlines (e.g. a lower 1st-gear limit):
--
--   { "rpm": 7600, "gears": [ { "g": 1, "rpm": 7200 }, { "g": 2, "rpm": 7400 } ] }
--
-- Gear 0 (the overall / default redline) stays in "rpm" for full back-compat:
-- a redline submission with no per-gear data canonicalizes to { "rpm": N }
-- exactly as before, so existing consensus rows and older clients are
-- unaffected and no consensus fragmentation occurs. Forward gears 1..16 ride
-- the optional "gears" array, canonicalized (validated, deduped by gear keeping
-- the lowest rpm, sorted ascending) so identical per-gear profiles hash
-- identically and cluster in consensus.
--
-- NOTE: this CREATE OR REPLACE was first applied to the live DB directly via
-- the Supabase MCP (migration name redline_payload_optional_per_gear) and is
-- committed here so the function is reproducible from migrations. The body is
-- the full current function (all fact types) with only the 'redline' branch
-- extended and a v_gears local added.

CREATE OR REPLACE FUNCTION public.normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'extensions', 'pg_temp'
AS $function$
declare
    v_int int; v_text text;
    v_custom jsonb;
    v_name text; v_pattern text;
    v_electric boolean; v_mode text;
    v_pct numeric;
    v_gears jsonb;
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_layout' then
        if pg_catalog.jsonb_typeof(p_payload->'layout') <> 'string' then return null; end if;
        v_text := pg_catalog.upper(pg_catalog.btrim(p_payload->>'layout'));

        if v_text = 'CUSTOM' then
            v_custom := p_payload->'custom';
            if pg_catalog.jsonb_typeof(v_custom) <> 'object' then return null; end if;

            if pg_catalog.jsonb_typeof(v_custom->'name') <> 'string' then return null; end if;
            v_name := pg_catalog.btrim(v_custom->>'name');
            if pg_catalog.length(v_name) < 2 or pg_catalog.length(v_name) > 96 then return null; end if;

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
        -- Optional per-gear overrides: gears = [{ "g": 1..16, "rpm": 500..25000 }].
        -- Gear 0 (overall/default) is represented by 'rpm' for back-compat, so
        -- only forward gears 1..16 live in the array. Canonical form: validated,
        -- deduped by gear (lowest rpm wins), sorted ascending, so identical
        -- per-gear profiles hash identically and cluster in consensus.
        v_gears := null;
        if p_payload ? 'gears'
           and pg_catalog.jsonb_typeof(p_payload->'gears') = 'array' then
            select pg_catalog.jsonb_agg(
                       pg_catalog.jsonb_build_object('g', g, 'rpm', rpm) order by g)
              into v_gears
              from (
                  select distinct on (g) g, rpm
                    from (
                        select (e->>'g')::int as g, (e->>'rpm')::int as rpm
                          from pg_catalog.jsonb_array_elements(p_payload->'gears') e
                         where pg_catalog.jsonb_typeof(e->'g') = 'number'
                           and pg_catalog.jsonb_typeof(e->'rpm') = 'number'
                    ) raw
                   where g between 1 and 16 and rpm between 500 and 25000
                   order by g, rpm
              ) s;
        end if;
        if v_gears is null or pg_catalog.jsonb_array_length(v_gears) = 0 then
            return pg_catalog.jsonb_build_object('rpm', v_int);
        end if;
        return pg_catalog.jsonb_build_object('rpm', v_int, 'gears', v_gears);

    elsif p_fact_type = 'engine_engagement_percent' then
        if pg_catalog.jsonb_typeof(p_payload->'percent') <> 'number' then return null; end if;
        v_pct := (p_payload->>'percent')::numeric;
        if v_pct < 0.50 or v_pct > 1.00 then return null; end if;
        v_pct := round(v_pct, 2);
        return pg_catalog.jsonb_build_object('percent', v_pct);
    end if;
    return null;
end;
$function$;
