-- Pin search_path on the three non-SECURITY-DEFINER helpers so they
-- can't be tricked into resolving symbols from an attacker-controlled
-- schema. wilson_lower_bound + normalize_car_fact_payload use only
-- pg_catalog builtins; _client_ip uses current_setting + nullif which
-- are also pg_catalog. Empty search_path is the strictest setting and
-- works for all three.

create or replace function wilson_lower_bound(pos int, neg int)
returns numeric language plpgsql immutable
set search_path = '' as $$
declare
    n numeric; phat numeric;
    z constant numeric := 1.959964; z2 constant numeric := 3.841459;
begin
    n := pos + neg;
    if n <= 0 then return 0; end if;
    phat := pos::numeric / n::numeric;
    return ( phat + z2 / (2*n) - z * sqrt( (phat*(1-phat) + z2/(4*n)) / n ) ) / (1 + z2/n);
end;
$$;

create or replace function normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
returns jsonb language plpgsql immutable
set search_path = '' as $$
declare v_int int; v_text text;
begin
    if p_payload is null then return null; end if;
    if p_fact_type = 'engine_layout' then
        if pg_catalog.jsonb_typeof(p_payload->'cyl') <> 'number'
           or pg_catalog.jsonb_typeof(p_payload->'config') <> 'string'
        then return null; end if;
        v_int  := (p_payload->>'cyl')::int;
        v_text := pg_catalog.upper(pg_catalog.trim(p_payload->>'config'));
        if v_int < 1 or v_int > 16 then return null; end if;
        if v_text not in ('AUTO','SINGLE','INLINE','BOXER','V60','V90EVEN',
                          'V8CROSSPLANE','V8FLATPLANE','V6ODDFIRE',
                          'VTWIN90','VTWIN45','ROTARY') then
            return null;
        end if;
        return pg_catalog.jsonb_build_object('cyl', v_int, 'config', v_text);
    elsif p_fact_type = 'car_name' then
        if pg_catalog.jsonb_typeof(p_payload->'name') <> 'string' then return null; end if;
        v_text := pg_catalog.trim(p_payload->>'name');
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

create or replace function _client_ip()
returns text language plpgsql stable
set search_path = '' as $$
declare v_hdrs jsonb; v_ip text;
begin
    begin
        v_hdrs := pg_catalog.current_setting('request.headers', true)::jsonb;
    exception when others then v_hdrs := null;
    end;
    if v_hdrs is null then return 'unknown'; end if;
    v_ip := pg_catalog.nullif(v_hdrs ->> 'cf-connecting-ip', '');
    if v_ip is null then return 'unknown'; end if;
    v_ip := pg_catalog.trim(v_ip);
    if v_ip = '' then return 'unknown'; end if;
    return v_ip;
end;
$$;