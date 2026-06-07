-- Per-(asset, user) download dedup. 0016's per-IP cap stopped the
-- bare-anon flood but a signed-in user reinstalling the plugin and
-- re-downloading the same preset would still tick the counter every
-- time. Real fix: one row per (asset_id, user_id) tracked in a side
-- table; the counter on the preset/game_preset/custom_engine/pack
-- row only bumps when the insert actually inserts a new row.
--
-- Side effect: enables a future "you downloaded N community assets"
-- account stat via SELECT-own RLS.

create table if not exists public.preset_user_downloads (
    preset_id uuid not null references public.presets(id) on delete cascade,
    user_id   uuid not null references auth.users(id)     on delete cascade,
    downloaded_at timestamptz not null default now(),
    primary key (preset_id, user_id)
);

create table if not exists public.game_preset_user_downloads (
    game_preset_id uuid not null references public.game_presets(id) on delete cascade,
    user_id        uuid not null references auth.users(id)          on delete cascade,
    downloaded_at  timestamptz not null default now(),
    primary key (game_preset_id, user_id)
);

create table if not exists public.custom_engine_user_downloads (
    custom_engine_id uuid not null references public.custom_engines(id) on delete cascade,
    user_id          uuid not null references auth.users(id)            on delete cascade,
    downloaded_at    timestamptz not null default now(),
    primary key (custom_engine_id, user_id)
);

create table if not exists public.pack_user_downloads (
    pack_id uuid not null references public.packs(id) on delete cascade,
    user_id uuid not null references auth.users(id)   on delete cascade,
    downloaded_at timestamptz not null default now(),
    primary key (pack_id, user_id)
);

create index if not exists preset_user_downloads_user_idx
    on public.preset_user_downloads (user_id, downloaded_at desc);
create index if not exists game_preset_user_downloads_user_idx
    on public.game_preset_user_downloads (user_id, downloaded_at desc);
create index if not exists custom_engine_user_downloads_user_idx
    on public.custom_engine_user_downloads (user_id, downloaded_at desc);
create index if not exists pack_user_downloads_user_idx
    on public.pack_user_downloads (user_id, downloaded_at desc);

alter table public.preset_user_downloads        enable row level security;
alter table public.game_preset_user_downloads   enable row level security;
alter table public.custom_engine_user_downloads enable row level security;
alter table public.pack_user_downloads          enable row level security;

-- Owners can read their own download history (account-stats hook).
-- Mutations go exclusively through the SECURITY DEFINER RPCs below.
drop policy if exists preset_user_downloads_select_own on public.preset_user_downloads;
create policy preset_user_downloads_select_own
    on public.preset_user_downloads for select
    using (auth.uid() = user_id);

drop policy if exists game_preset_user_downloads_select_own on public.game_preset_user_downloads;
create policy game_preset_user_downloads_select_own
    on public.game_preset_user_downloads for select
    using (auth.uid() = user_id);

drop policy if exists custom_engine_user_downloads_select_own on public.custom_engine_user_downloads;
create policy custom_engine_user_downloads_select_own
    on public.custom_engine_user_downloads for select
    using (auth.uid() = user_id);

drop policy if exists pack_user_downloads_select_own on public.pack_user_downloads;
create policy pack_user_downloads_select_own
    on public.pack_user_downloads for select
    using (auth.uid() = user_id);

-- ---- RPCs: insert-once-per-user, counter follows ----------------

create or replace function public.record_preset_download(p_preset_id uuid)
returns void
language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.presets where id = p_preset_id and not is_suppressed;
    if not found then return; end if;
    -- Don't let authors tick their own counter.
    if v_owner is not null and v_owner = v_uid then return; end if;

    insert into public.preset_user_downloads (preset_id, user_id)
    values (p_preset_id, v_uid)
    on conflict (preset_id, user_id) do nothing;

    -- FOUND is true only when the INSERT actually inserted; ON
    -- CONFLICT DO NOTHING leaves FOUND false on the second+ call.
    if FOUND then
        update public.presets
            set downloads = downloads + 1
            where id = p_preset_id;
    end if;
end;
$$;

create or replace function public.record_game_preset_download(p_game_preset_id uuid)
returns void
language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.game_presets where id = p_game_preset_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    insert into public.game_preset_user_downloads (game_preset_id, user_id)
    values (p_game_preset_id, v_uid)
    on conflict (game_preset_id, user_id) do nothing;
    if FOUND then
        update public.game_presets
            set downloads = downloads + 1
            where id = p_game_preset_id;
    end if;
end;
$$;

create or replace function public.record_custom_engine_download(p_custom_engine_id uuid)
returns void
language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.custom_engines where id = p_custom_engine_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    insert into public.custom_engine_user_downloads (custom_engine_id, user_id)
    values (p_custom_engine_id, v_uid)
    on conflict (custom_engine_id, user_id) do nothing;
    if FOUND then
        update public.custom_engines
            set downloads = downloads + 1
            where id = p_custom_engine_id;
    end if;
end;
$$;

create or replace function public.record_pack_download(p_pack_id uuid)
returns void
language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.packs where id = p_pack_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    insert into public.pack_user_downloads (pack_id, user_id)
    values (p_pack_id, v_uid)
    on conflict (pack_id, user_id) do nothing;
    if FOUND then
        update public.packs
            set downloads = downloads + 1
            where id = p_pack_id;
    end if;
end;
$$;

-- delete_my_account: ON DELETE CASCADE on auth.users handles all four
-- new tables automatically, so no update needed there.
