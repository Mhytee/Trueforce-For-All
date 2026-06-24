-- Per-gear redline consensus, approach A: each forward gear is its OWN fact.
--
-- Background: the redline fact used to carry the whole per-gear profile in one
-- payload ({ "rpm": N, "gears": [...] }, migration 0062). The consensus engine
-- clusters by the WHOLE canonical payload, so a per-gear submission lands in a
-- different bucket from a plain { "rpm": N } and fragments the overall
-- agreement; partial per-gear data is thrown away unless an entire identical
-- profile is the plurality.
--
-- Fix: gear 0 (the overall redline) stays the existing 'redline' fact with a
-- bare { "rpm": N } payload. Each forward gear 1..16 becomes its own fact
-- 'redline_g1'..'redline_g16' with a single { "rpm": N } payload, so the
-- generic consensus / support-floor / voting / GC machinery computes each
-- gear's agreement INDEPENDENTLY. Accurate gears are usable even when other
-- gears never reach consensus.
--
-- The consensus recompute, the superseded-submission GC, the weekly GC cron,
-- vote_car_fact, the indexes, and the RLS model are all already fully generic
-- over fact_type, so they need NO change. The only two gates that reject the
-- new fact types today are widened here, additively:
--
--   1. car_fact_submissions.fact_type CHECK (hardcoded to 3 values).
--   2. normalize_car_fact_payload (if/elsif chain that returns null, and thus
--      makes submit_car_fact raise 'invalid payload', for anything else).
--
-- Additive + reversible. Existing 'redline'/'car_name'/'engine_layout'/
-- 'engine_engagement_percent' submissions are untouched. (engine_engagement_percent
-- was normalizer-accepted since 0031 but never added to the CHECK; folded in here
-- to close that latent gap.)
--
-- DOWN (manual): delete any redline_g% submissions first, then restore the
-- 3-value CHECK and CREATE OR REPLACE normalize_car_fact_payload back to the
-- 0062 body. A narrowed CHECK is validated against existing rows and fails if
-- redline_g% rows exist.

-- 1) Widen the submissions fact_type CHECK. Bounded pattern: g1..g16 only.
alter table public.car_fact_submissions
    drop constraint car_fact_submissions_fact_type_check;
alter table public.car_fact_submissions
    add constraint car_fact_submissions_fact_type_check
    check (
        fact_type = any (array['engine_layout','car_name','redline','engine_engagement_percent'])
        or fact_type ~ '^redline_g(1[0-6]|[1-9])$'
    );

-- 2) Accept redline_gN payloads in the normalizer. Body is the current LIVE
-- function (== migration 0062) with one new branch added before the trailing
-- return null. Each per-gear fact is a single { "rpm": 500..25000 }, same range
-- as the overall redline, with no gears array of its own.
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
        -- Retained for back-compat (older clients embedded gears here); new
        -- clients submit bare { "rpm" } for gear 0 and separate redline_gN facts
        -- so the overall consensus does not fragment.
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

    elsif p_fact_type ~ '^redline_g(1[0-6]|[1-9])$' then
        -- Per-gear redline as its own fact (approach A): a single forward
        -- gear's redline, validated like the overall value. No gears array.
        if pg_catalog.jsonb_typeof(p_payload->'rpm') <> 'number' then return null; end if;
        v_int := (p_payload->>'rpm')::int;
        if v_int < 500 or v_int > 25000 then return null; end if;
        return pg_catalog.jsonb_build_object('rpm', v_int);
    end if;
    return null;
end;
$function$;
