-- nullif is SQL conditional-expression syntax, not a pg_catalog function;
-- qualifying it with pg_catalog.nullif causes function-lookup failure
-- under empty search_path because the literal '' resolves to type unknown
-- and there's no pg_catalog.nullif(text, unknown). Bare nullif works
-- because it's parsed as syntax, not a function call.

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
    v_ip := nullif(v_hdrs ->> 'cf-connecting-ip', '');
    if v_ip is null then return 'unknown'; end if;
    v_ip := pg_catalog.btrim(v_ip);
    if v_ip = '' then return 'unknown'; end if;
    return v_ip;
end;
$$;