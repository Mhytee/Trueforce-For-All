-- Adds the unknown-car guard to submit_arcade_lap_time. The 0117 file carries the whole function;
-- here it is spliced into the live source at an anchor so the 170 lines that are NOT changing stay
-- byte-identical rather than being retyped. Refuses to run twice, and refuses to run at all if the
-- anchor is missing, so a silent no-op is impossible.
do $do$
declare
    v_src    text;
    v_anchor text;
    v_add    text;
    v_new    text;
begin
    select p.prosrc into v_src
      from pg_proc p join pg_namespace n on n.oid = p.pronamespace
     where n.nspname = 'public' and p.proname = 'submit_arcade_lap_time';

    if v_src is null then
        raise exception 'submit_arcade_lap_time not found';
    end if;
    if position('unknown car' in v_src) > 0 then
        raise exception 'guard already present; refusing to splice twice';
    end if;

    v_anchor := '        return jsonb_build_object(''ok'', false, ''error'', ''car required'');
    end if;
';
    if position(v_anchor in v_src) = 0 then
        raise exception 'anchor not found; the function body is not what 0112 left';
    end if;

    v_add := v_anchor || '
    -- Bounded by arcade_cars. A game with NO rows there is accepted on purpose: when a second
    -- cabinet is added, its runs must not start failing because nobody has seeded its cars yet.
    if exists (select 1 from public.arcade_cars c where c.game = p_game)
       and not exists (select 1 from public.arcade_cars c
                        where c.game = p_game and c.car_id = p_car_id) then
        return jsonb_build_object(''ok'', false, ''error'', ''unknown car'', ''car_id'', p_car_id);
    end if;
';

    v_new := replace(v_src, v_anchor, v_add);

    execute 'create or replace function public.submit_arcade_lap_time(
    p_game           text,
    p_course_id      smallint,
    p_direction      smallint,
    p_car_id         integer,
    p_goal_ms        integer,
    p_section1_ms    integer default null,
    p_section2_ms    integer default null,
    p_section3_ms    integer default null,
    p_tuning_level   smallint default null,
    p_plugin_version text default null,
    p_sections_ms    integer[] default null)
returns jsonb
language plpgsql
security definer
set search_path to ''public'', ''extensions'', ''pg_temp''
as $f$' || v_new || '$f$';
end
$do$;

revoke all on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) from anon, public;
grant execute on function public.submit_arcade_lap_time(
    text, smallint, smallint, integer, integer, integer, integer, integer,
    smallint, text, integer[]) to authenticated;