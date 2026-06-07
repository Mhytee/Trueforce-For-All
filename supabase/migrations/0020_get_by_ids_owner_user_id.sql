-- Add owner_user_id to the four get_*_by_ids RPC responses.
--
-- These RPCs back the per-session "check for updates" sweep: the client
-- passes the list of UUIDs the user has previously downloaded and gets
-- back current server metadata to compare against
-- Settings.DownloadedCommunityPresets[id].SeenContentVersion.
--
-- The client's PresetSummary class includes OwnerUserId, which drives
-- Edit / Delete button visibility on community rows (only the owner
-- sees those affordances). Every browse RPC (FetchCommunityPresetsForCar,
-- FetchGamePresetsForGame, FetchCommunityCustomEngines, FetchCommunityPacks)
-- already returns owner_user_id, but these four by-ids variants were
-- never updated to include it. As a result PresetSummary.OwnerUserId is
-- null on rows surfaced via the update-tracker flow, and the Edit /
-- Delete affordance is suppressed for the row's actual owner.
--
-- Additive change: the JSON output gains one new field. Existing callers
-- that ignore unknown fields keep working unchanged. Re-runnable via
-- CREATE OR REPLACE.

create or replace function public.get_presets_by_ids(p_ids uuid[])
returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if p_ids is null or array_length(p_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object(
            'id', id, 'name', name, 'author', author,
            'description', description, 'game', game, 'car_id', car_id,
            'content_version', content_version, 'updated_at', updated_at,
            'is_suppressed', is_suppressed,
            'owner_user_id', owner_user_id)
          from public.presets
         where id = any(p_ids);
end;
$$;

create or replace function public.get_game_presets_by_ids(p_ids uuid[])
returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if p_ids is null or array_length(p_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object(
            'id', id, 'name', name, 'author', author,
            'description', description, 'game', game,
            'content_version', content_version, 'updated_at', updated_at,
            'is_suppressed', is_suppressed,
            'owner_user_id', owner_user_id)
          from public.game_presets
         where id = any(p_ids);
end;
$$;

create or replace function public.get_custom_engines_by_ids(p_ids uuid[])
returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if p_ids is null or array_length(p_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object(
            'id', id, 'name', name, 'author', author,
            'description', description,
            'content_version', content_version, 'updated_at', updated_at,
            'is_suppressed', is_suppressed,
            'owner_user_id', owner_user_id)
          from public.custom_engines
         where id = any(p_ids);
end;
$$;

create or replace function public.get_packs_by_ids(p_ids uuid[])
returns setof jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if p_ids is null or array_length(p_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object(
            'id', id, 'name', name, 'author', author,
            'description', description, 'author_version', author_version,
            'entry_count', entry_count,
            'content_version', content_version, 'updated_at', updated_at,
            'is_suppressed', is_suppressed,
            'owner_user_id', owner_user_id)
          from public.packs where id = any(p_ids);
end;
$$;
