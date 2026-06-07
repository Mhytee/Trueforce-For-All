-- B9: Server-side hardening. Lock down report abuse channels.
-- Adds auth.uid() requirement to all report_* functions.
-- Implements per-user per-target rate limiting via a 24h time-window check
-- BEFORE the insert, so a reporter cannot re-flag the same target within
-- 24 hours. The unique (target_type, target_id, reporter_id) constraint
-- keeps long-term history deduplicated as a defense-in-depth fallback.

create table public.report_flags (
    id uuid primary key default gen_random_uuid(),
    target_type text not null check (target_type in ('preset', 'game_preset', 'custom_engine', 'pack')),
    target_id uuid not null,
    reporter_id uuid not null references auth.users(id) on delete cascade,
    created_at timestamptz not null default now(),
    unique (target_type, target_id, reporter_id)
);

create index report_flags_reporter_created_idx
    on public.report_flags (reporter_id, created_at desc);

alter table public.report_flags enable row level security;
revoke insert, update, delete on public.report_flags from anon;
revoke select on public.report_flags from anon;

create or replace function public.report_preset(p_preset_id uuid)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if exists (
        select 1 from public.report_flags
        where reporter_id = auth.uid()
          and target_id   = p_preset_id
          and target_type = 'preset'
          and created_at  > now() - interval '24 hours'
    ) then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id)
        values ('preset', p_preset_id, auth.uid())
        on conflict (target_type, target_id, reporter_id) do nothing;
    update public.presets set reported = true where id = p_preset_id;
end;
$$;

create or replace function public.report_game_preset(p_game_preset_id uuid)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if exists (
        select 1 from public.report_flags
        where reporter_id = auth.uid()
          and target_id   = p_game_preset_id
          and target_type = 'game_preset'
          and created_at  > now() - interval '24 hours'
    ) then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id)
        values ('game_preset', p_game_preset_id, auth.uid())
        on conflict (target_type, target_id, reporter_id) do nothing;
    update public.game_presets set reported = true where id = p_game_preset_id;
end;
$$;

create or replace function public.report_custom_engine(p_custom_engine_id uuid)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if exists (
        select 1 from public.report_flags
        where reporter_id = auth.uid()
          and target_id   = p_custom_engine_id
          and target_type = 'custom_engine'
          and created_at  > now() - interval '24 hours'
    ) then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id)
        values ('custom_engine', p_custom_engine_id, auth.uid())
        on conflict (target_type, target_id, reporter_id) do nothing;
    update public.custom_engines set reported = true where id = p_custom_engine_id;
end;
$$;

create or replace function public.report_pack(p_pack_id uuid)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if exists (
        select 1 from public.report_flags
        where reporter_id = auth.uid()
          and target_id   = p_pack_id
          and target_type = 'pack'
          and created_at  > now() - interval '24 hours'
    ) then
        raise exception 'You have already reported this in the last 24 hours' using errcode = 'P0001';
    end if;
    insert into public.report_flags (target_type, target_id, reporter_id)
        values ('pack', p_pack_id, auth.uid())
        on conflict (target_type, target_id, reporter_id) do nothing;
    update public.packs set reported = true where id = p_pack_id;
end;
$$;
