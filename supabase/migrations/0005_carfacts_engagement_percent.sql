-- 0005 carfacts: accept engine_engagement_percent as a fact_type.
--
-- Motivation: Forza-family titles report MaxRpm but not RedlineRpm. The
-- plugin's rev limiter falls back to MaxRpm * EngagementPercent on those
-- games; that engagement percent IS the de-facto redline for the car
-- and is community-shareable per (game, carId, variant_signature). The
-- existing 'redline' fact_type stores absolute RPM, which doesn't fit
-- the Forza model (would require every consumer to know MaxRpm to
-- convert back). Engagement-percent CarFacts skip that translation:
-- store the percent, multiply by live MaxRpm at apply time, done.
--
-- Schema-wise this is purely a normalize_car_fact_payload extension.
-- car_fact_consensus + car_fact_submissions already store payload as
-- jsonb so no column changes. Consensus recompute is type-agnostic.
--
-- Payload shape: {"percent": 0.85}
-- Range: 0.50 <= percent <= 1.00 (mirrors the RevLimiter slider clamp).

create or replace function public.normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_int int; v_text text;
    v_custom jsonb;
    v_name text; v_pattern text;
    v_electric boolean; v_mode text;
    v_pct numeric;
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
        return pg_catalog.jsonb_build_object('rpm', v_int);

    elsif p_fact_type = 'engine_engagement_percent' then
        -- Forza-family rev-limiter engagement: percent of MaxRpm at
        -- which the limiter haptic fires. The plugin clamps the
        -- slider to 0.50..1.00; mirror those bounds so a malformed
        -- submission never bakes into consensus. Bucket to 2 decimals
        -- so 0.8500001 and 0.8499 land in the same consensus bin.
        if pg_catalog.jsonb_typeof(p_payload->'percent') <> 'number' then return null; end if;
        v_pct := (p_payload->>'percent')::numeric;
        if v_pct < 0.50 or v_pct > 1.00 then return null; end if;
        v_pct := round(v_pct, 2);
        return pg_catalog.jsonb_build_object('percent', v_pct);
    end if;
    return null;
end;
$$;
