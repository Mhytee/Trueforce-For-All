-- Harden normalize_car_fact_payload and finish the per-gear-fact migration.
--
-- Two fixes, both additive CREATE OR REPLACE of the one validator:
--
-- 1) Missing-key junk. Every branch guards a field with
--    `jsonb_typeof(p_payload->'x') <> 't'`. When the KEY IS ABSENT,
--    p_payload->'x' is SQL NULL, jsonb_typeof(NULL) is NULL, and NULL <> 't'
--    is NULL (not true), so the guard does not fire. Execution falls through
--    and the function returns junk like {"rpm": null} / {"percent": null} /
--    {"name": null} / {"layout": null}, which submit_car_fact accepts (it only
--    rejects a NULL canonical) and which then clusters in consensus. Not
--    reachable from the shipping client (it always serializes the field), but
--    reachable by any authenticated user calling the RPC directly, and the
--    per-gear branch multiplied the attackable surface by 16. Fix: add an
--    explicit key-presence check before each type guard.
--
-- 2) One representation for per-gear. Now that each gear is its own
--    'redline_gN' fact (0063), drop the legacy embedded-`gears` handling from
--    the overall 'redline' branch so it always canonicalizes to bare {rpm}.
--    The new client never embeds gears; this makes the write side match the
--    read side (FetchRedlineConsensus reads only payload.rpm off 'redline').
--    Verified no live 'redline' row carries a gears array, so nothing is lost.
--
-- Reversible: restore the 0063 body (CREATE OR REPLACE). No data migration.

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
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_layout' then
        if not (p_payload ? 'layout')
           or pg_catalog.jsonb_typeof(p_payload->'layout') <> 'string' then return null; end if;
        v_text := pg_catalog.upper(pg_catalog.btrim(p_payload->>'layout'));

        if v_text = 'CUSTOM' then
            v_custom := p_payload->'custom';
            if not (p_payload ? 'custom')
               or pg_catalog.jsonb_typeof(v_custom) <> 'object' then return null; end if;

            if not (v_custom ? 'name')
               or pg_catalog.jsonb_typeof(v_custom->'name') <> 'string' then return null; end if;
            v_name := pg_catalog.btrim(v_custom->>'name');
            if pg_catalog.length(v_name) < 2 or pg_catalog.length(v_name) > 96 then return null; end if;

            if not (v_custom ? 'pattern')
               or pg_catalog.jsonb_typeof(v_custom->'pattern') <> 'string' then return null; end if;
            v_pattern := v_custom->>'pattern';
            if pg_catalog.length(v_pattern) > 512 then return null; end if;

            if not (v_custom ? 'electric')
               or pg_catalog.jsonb_typeof(v_custom->'electric') <> 'boolean' then return null; end if;
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
        if not (p_payload ? 'name')
           or pg_catalog.jsonb_typeof(p_payload->'name') <> 'string' then return null; end if;
        v_text := pg_catalog.btrim(p_payload->>'name');
        if pg_catalog.length(v_text) < 2 or pg_catalog.length(v_text) > 96 then return null; end if;
        return pg_catalog.jsonb_build_object('name', v_text);

    elsif p_fact_type = 'redline' then
        -- Gear 0 / overall only. Per-gear data now lives in separate redline_gN
        -- facts (0063), so this canonicalizes to a bare { rpm } - no gears array.
        if not (p_payload ? 'rpm')
           or pg_catalog.jsonb_typeof(p_payload->'rpm') <> 'number' then return null; end if;
        v_int := (p_payload->>'rpm')::int;
        if v_int < 500 or v_int > 25000 then return null; end if;
        return pg_catalog.jsonb_build_object('rpm', v_int);

    elsif p_fact_type = 'engine_engagement_percent' then
        if not (p_payload ? 'percent')
           or pg_catalog.jsonb_typeof(p_payload->'percent') <> 'number' then return null; end if;
        v_pct := (p_payload->>'percent')::numeric;
        if v_pct < 0.50 or v_pct > 1.00 then return null; end if;
        v_pct := round(v_pct, 2);
        return pg_catalog.jsonb_build_object('percent', v_pct);

    elsif p_fact_type ~ '^redline_g(1[0-6]|[1-9])$' then
        -- Per-gear redline as its own fact: a single forward gear's redline.
        if not (p_payload ? 'rpm')
           or pg_catalog.jsonb_typeof(p_payload->'rpm') <> 'number' then return null; end if;
        v_int := (p_payload->>'rpm')::int;
        if v_int < 500 or v_int > 25000 then return null; end if;
        return pg_catalog.jsonb_build_object('rpm', v_int);
    end if;
    return null;
end;
$function$;
