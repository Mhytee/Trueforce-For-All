-- 0080: report reasons + Discord moderation workflow.
--
-- Two gaps this closes:
--   1. report_flags carried no reason. A report was a bare (target, reporter)
--      row, so a moderator had no idea WHY something was flagged. We add a
--      fixed-vocabulary category + an optional free-text note.
--   2. Reports landed in a table nobody watched. We add an after-insert
--      trigger that posts the report to the private #preset-reports Discord
--      channel (via the report-notify Edge Function) with two buttons:
--        - "Remove content" -> sets is_suppressed = true on the target
--          (instant hide, reversible; same lever the car-fact runbook uses)
--        - "Dismiss"        -> leaves the target live, marks the report closed
--      Button clicks come back to the report-action Edge Function, which
--      verifies the Discord request signature and the clicker's mod role.
--
-- The notify path mirrors the 0046/0048 pg_net pattern: the trigger sends the
-- service_role JWT (read from Vault) so report-notify stays verify_jwt = true.

-- ---- 1. Reason + resolution columns ----------------------------------------
-- Adding NOT NULL columns with defaults backfills existing rows ('other'/'open').
alter table public.report_flags
    add column if not exists category    text not null default 'other'
        check (category in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other')),
    add column if not exists note               text,
    add column if not exists status      text not null default 'open'
        check (status in ('open', 'removed', 'dismissed')),
    add column if not exists resolved_by         text,        -- Discord username of the actioning mod
    add column if not exists resolved_at         timestamptz,
    add column if not exists discord_message_id  text,        -- stamped by report-notify after posting
    add column if not exists discord_channel_id  text;

-- Index the moderation queue read path (open reports, newest first).
create index if not exists report_flags_status_created_idx
    on public.report_flags (status, created_at desc);

-- ---- 2. RPCs gain p_category + p_note --------------------------------------
-- Signature change means a new overload, so drop the 1-arg originals first
-- (otherwise both versions coexist and PostgREST picks ambiguously).
-- On re-report by the same user after the 24h window, we refresh the reason
-- but deliberately leave status/resolution untouched: a re-flag should not
-- silently re-open something a mod already actioned, and a different reporter
-- always creates its own row + its own Discord message regardless.

drop function if exists public.report_preset(uuid);
create function public.report_preset(
    p_preset_id uuid,
    p_category  text default 'other',
    p_note      text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
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
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('preset', p_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.presets set reported = true where id = p_preset_id;
end;
$$;

drop function if exists public.report_game_preset(uuid);
create function public.report_game_preset(
    p_game_preset_id uuid,
    p_category       text default 'other',
    p_note           text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
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
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('game_preset', p_game_preset_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.game_presets set reported = true where id = p_game_preset_id;
end;
$$;

drop function if exists public.report_custom_engine(uuid);
create function public.report_custom_engine(
    p_custom_engine_id uuid,
    p_category         text default 'other',
    p_note             text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
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
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('custom_engine', p_custom_engine_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.custom_engines set reported = true where id = p_custom_engine_id;
end;
$$;

drop function if exists public.report_pack(uuid);
create function public.report_pack(
    p_pack_id  uuid,
    p_category text default 'other',
    p_note     text default null)
returns void language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_cat  text := lower(coalesce(nullif(trim(p_category), ''), 'other'));
    v_note text := nullif(left(trim(coalesce(p_note, '')), 1000), '');
begin
    if auth.uid() is null then
        raise exception 'Login required' using errcode = 'P0001';
    end if;
    if v_cat not in ('broken', 'inappropriate', 'spam', 'wrong_data', 'stolen', 'other') then
        raise exception 'Invalid report category' using errcode = 'P0001';
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
    insert into public.report_flags (target_type, target_id, reporter_id, category, note)
        values ('pack', p_pack_id, auth.uid(), v_cat, v_note)
        on conflict (target_type, target_id, reporter_id)
        do update set category = excluded.category, note = excluded.note, created_at = now();
    update public.packs set reported = true where id = p_pack_id;
end;
$$;

-- ---- 3. Notify trigger -----------------------------------------------------
-- Fires once per fresh report (INSERT only; the on-conflict refresh path above
-- is an UPDATE and intentionally does not re-notify). net.http_post is async
-- (queues into pg_net), so it never blocks or fails the reporting transaction:
-- a missing Vault secret or a down function just means no Discord message, the
-- report row is still recorded for the SQL runbook.
create or replace function public.notify_report_flag()
returns trigger language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    perform net.http_post(
        url     := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/report-notify',
        headers := jsonb_build_object(
            'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
            'Content-Type',  'application/json'),
        body    := jsonb_build_object('report_id', NEW.id));
    return NEW;
end;
$$;

drop trigger if exists report_flags_notify on public.report_flags;
create trigger report_flags_notify
    after insert on public.report_flags
    for each row execute function public.notify_report_flag();
