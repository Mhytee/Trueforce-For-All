-- 0088: you can't report your own upload. The report_* RPCs already require
-- login + dedup per 24h, but nothing stopped an owner flagging their own item
-- (pointless, and a way to spam the mod queue). Add an owner guard to each.
-- Built-ins aren't reachable here at all (they're local-only, never community
-- rows), so there's nothing to gate for them. create-or-replace keeps the
-- existing 3-arg signatures + grants.

create or replace function public.report_preset(
    p_preset_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.presets where id = p_preset_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_preset_id
                  and target_type = 'preset' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('preset', p_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.presets set reported = true where id = p_preset_id;
end;
$$;

create or replace function public.report_game_preset(
    p_game_preset_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.game_presets where id = p_game_preset_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_game_preset_id
                  and target_type = 'game_preset' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('game_preset', p_game_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.game_presets set reported = true where id = p_game_preset_id;
end;
$$;

create or replace function public.report_custom_engine(
    p_custom_engine_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.custom_engines where id = p_custom_engine_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_custom_engine_id
                  and target_type = 'custom_engine' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('custom_engine', p_custom_engine_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.custom_engines set reported = true where id = p_custom_engine_id;
end;
$$;

create or replace function public.report_pack(
    p_pack_id uuid, p_category text default 'other', p_note text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then raise exception 'Login required' using errcode = 'P0001'; end if;
    if exists (select 1 from public.packs where id = p_pack_id and owner_user_id = auth.uid()) then
        raise exception 'You cannot report your own upload' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
    end if;
    if exists (select 1 from public.report_flags
                where reporter_id = auth.uid() and target_id = p_pack_id
                  and target_type = 'pack' and created_at > now() - interval '24 hours') then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('pack', p_pack_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.packs set reported = true where id = p_pack_id;
end;
$$;