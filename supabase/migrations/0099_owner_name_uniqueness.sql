-- 0099: per-owner community name uniqueness (owner decision 2026-07-13).
-- A user must not hold two live uploads of the same kind with the same name:
-- name collisions ACROSS users stay allowed by design (author + votes
-- disambiguate), but within one account a duplicate name is always either an
-- accident (re-sharing an unchanged preset created a second identical row) or
-- a mess (renaming one upload onto another). Two layers:
--
--   1. A shared BEFORE trigger on all four content tables raising a clean
--      'duplicate name' message (the client maps that substring to a friendly
--      error). Covers every path: both upload_preset overloads, the four
--      upload_* RPCs, and every update_* rename branch, with no need to
--      re-create those function bodies.
--   2. Partial unique indexes as the race backstop (two concurrent inserts
--      can both pass the trigger's lookup; the index cannot be raced).
--
-- Suppressed rows don't hold names (a user who soft-deletes an upload can
-- reuse its name), and orphaned rows (owner_user_id null after account
-- deletion) are exempt. Un-suppressing via a moderation appeal re-checks, so
-- a restore that would collide with a newer live upload fails loudly and the
-- moderator resolves by rename. All four tables are currently small (the
-- pre-launch wipe), so the indexes build instantly.

create or replace function public._enforce_owner_name_unique() returns trigger
language plpgsql
set search_path = public, pg_temp
as $$
declare v_hit int;
begin
    if new.owner_user_id is null then return new; end if;
    if new.is_suppressed then return new; end if;
    if tg_op = 'UPDATE'
       and new.name = old.name
       and new.owner_user_id is not distinct from old.owner_user_id
       and new.is_suppressed = old.is_suppressed then
        return new;   -- nothing name-relevant changed
    end if;
    execute format(
        'select 1 from public.%I where owner_user_id = $1 and lower(name) = lower($2) '
        || 'and not is_suppressed and id <> $3 limit 1', tg_table_name)
      into v_hit
      using new.owner_user_id, new.name, new.id;
    if v_hit is not null then
        raise exception 'duplicate name: you already shared an item named "%". Update it instead, or pick a different name.', new.name;
    end if;
    return new;
end;
$$;

drop trigger if exists presets_owner_name_unique        on public.presets;
drop trigger if exists game_presets_owner_name_unique   on public.game_presets;
drop trigger if exists custom_engines_owner_name_unique on public.custom_engines;
drop trigger if exists packs_owner_name_unique          on public.packs;

create trigger presets_owner_name_unique
    before insert or update of name, owner_user_id, is_suppressed on public.presets
    for each row execute function public._enforce_owner_name_unique();
create trigger game_presets_owner_name_unique
    before insert or update of name, owner_user_id, is_suppressed on public.game_presets
    for each row execute function public._enforce_owner_name_unique();
create trigger custom_engines_owner_name_unique
    before insert or update of name, owner_user_id, is_suppressed on public.custom_engines
    for each row execute function public._enforce_owner_name_unique();
create trigger packs_owner_name_unique
    before insert or update of name, owner_user_id, is_suppressed on public.packs
    for each row execute function public._enforce_owner_name_unique();

-- Race backstop: uniqueness the trigger's read-then-insert cannot guarantee.
create unique index if not exists presets_owner_name_live_key
    on public.presets (owner_user_id, lower(name))
    where not is_suppressed and owner_user_id is not null;
create unique index if not exists game_presets_owner_name_live_key
    on public.game_presets (owner_user_id, lower(name))
    where not is_suppressed and owner_user_id is not null;
create unique index if not exists custom_engines_owner_name_live_key
    on public.custom_engines (owner_user_id, lower(name))
    where not is_suppressed and owner_user_id is not null;
create unique index if not exists packs_owner_name_live_key
    on public.packs (owner_user_id, lower(name))
    where not is_suppressed and owner_user_id is not null;
