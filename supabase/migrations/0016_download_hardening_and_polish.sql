-- Hardens the four record_*_download RPCs (anyone with the anon key
-- could previously inflate the downloads counter in a loop, which
-- weaponizes the "Most downloaded" sort), patches the three
-- get_*_by_ids RPCs that forgot allow_in_packs (latent schema drift
-- spotted before any consumer reads it), and adds an unconditional
-- popularity tiebreak to get_my_presets ordering.
--
-- Hardening shape:
--   * require auth.uid() (closes the bare-anon attack vector)
--   * reject self-downloads (owner_user_id = caller; counters should
--     measure other people grabbing your work, not you re-fetching it)
--   * per-IP window (50 downloads/hour) like the vote/upload RPCs
--   * downloads on a deleted (suppressed) or missing row stay a no-op

create or replace function public.record_preset_download(
    p_preset_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_ip text; v_ip_hash text; v_recent int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.presets where id = p_preset_id and not is_suppressed;
    if not found then return; end if;
    -- Don't let authors inflate their own counter.
    if v_owner is not null and v_owner = v_uid then return; end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from public.presets
     where source_ip_hash = v_ip_hash
       and downloads > 0
       and updated_at > now() - interval '1 hour';
    -- Fast path: cap at 50/hour total downloads triggered from one IP.
    if v_recent >= 50 then return; end if;

    update public.presets
        set downloads = downloads + 1
        where id = p_preset_id;
end;
$$;

create or replace function public.record_game_preset_download(
    p_game_preset_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_ip text; v_ip_hash text; v_recent int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.game_presets where id = p_game_preset_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from public.game_presets
     where source_ip_hash = v_ip_hash
       and downloads > 0
       and updated_at > now() - interval '1 hour';
    if v_recent >= 50 then return; end if;

    update public.game_presets
        set downloads = downloads + 1
        where id = p_game_preset_id;
end;
$$;

create or replace function public.record_custom_engine_download(
    p_custom_engine_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_ip text; v_ip_hash text; v_recent int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.custom_engines where id = p_custom_engine_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from public.custom_engines
     where source_ip_hash = v_ip_hash
       and downloads > 0
       and updated_at > now() - interval '1 hour';
    if v_recent >= 50 then return; end if;

    update public.custom_engines
        set downloads = downloads + 1
        where id = p_custom_engine_id;
end;
$$;

create or replace function public.record_pack_download(
    p_pack_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_ip text; v_ip_hash text; v_recent int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.packs where id = p_pack_id and not is_suppressed;
    if not found then return; end if;
    if v_owner is not null and v_owner = v_uid then return; end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from public.packs
     where source_ip_hash = v_ip_hash
       and downloads > 0
       and updated_at > now() - interval '1 hour';
    if v_recent >= 50 then return; end if;

    update public.packs
        set downloads = downloads + 1
        where id = p_pack_id;
end;
$$;

-- ---- Patch get_*_by_ids to include allow_in_packs ------------------
-- Latent schema drift: 0010/0012/0013 wrote these projections before
-- 0015 added the flag; nobody reads it via these RPCs today but the
-- next consumer would silently see false. Closing the gap pre-emptively.

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
            'allow_in_packs', allow_in_packs,
            'is_suppressed', is_suppressed)
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
            'allow_in_packs', allow_in_packs,
            'is_suppressed', is_suppressed)
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
            'allow_in_packs', allow_in_packs,
            'is_suppressed', is_suppressed)
          from public.custom_engines
         where id = any(p_ids);
end;
$$;

-- ---- get_my_presets popularity tiebreak ----------------------------
-- Today: "top" sorts by wilson_score only, "downloads" by downloads
-- only; ties fall through to created_at. With low traffic many rows
-- tie at 0/0, surfacing newest-only and burying actually-popular work.

create or replace function public.get_my_presets(
    p_sort text default 'newest',
    p_limit int default 100
) returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_lim int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_lim := greatest(1, least(coalesce(p_limit, 100), 500));

    return query
    with car_rows as (
        select id, 'car'::text as kind, name, description, game, car_id,
               effect_tags, upvotes, downvotes, wilson_score, downloads,
               created_at, is_suppressed
          from public.presets where owner_user_id = v_uid
    ),
    game_rows as (
        select id, 'game'::text as kind, name, description, game,
               '*'::text as car_id, effect_tags, upvotes, downvotes,
               wilson_score, downloads, created_at, is_suppressed
          from public.game_presets where owner_user_id = v_uid
    ),
    engine_rows as (
        select id, 'engine'::text as kind, name, description,
               ''::text as game, ''::text as car_id, '{}'::text[] as effect_tags,
               upvotes, downvotes, wilson_score, downloads,
               created_at, is_suppressed
          from public.custom_engines where owner_user_id = v_uid
    ),
    pack_rows as (
        select id, 'pack'::text as kind, name, description,
               ''::text as game, ''::text as car_id, '{}'::text[] as effect_tags,
               upvotes, downvotes, wilson_score, downloads,
               created_at, is_suppressed
          from public.packs where owner_user_id = v_uid
    ),
    combined as (
        select * from car_rows
        union all select * from game_rows
        union all select * from engine_rows
        union all select * from pack_rows
    )
    select jsonb_build_object(
        'id', id, 'kind', kind, 'name', name, 'description', description,
        'game', game, 'car_id', car_id, 'effect_tags', effect_tags,
        'upvotes', upvotes, 'downvotes', downvotes,
        'wilson_score', wilson_score, 'downloads', downloads,
        'created_at', created_at, 'is_suppressed', is_suppressed)
      from combined
     order by
        case when p_sort = 'top'       then wilson_score end desc nulls last,
        case when p_sort = 'downloads' then downloads    end desc nulls last,
        -- Unconditional popularity tiebreak so wilson/downloads ties
        -- don't fall through to pure recency.
        downloads     desc,
        wilson_score  desc,
        created_at    desc
     limit v_lim;
end;
$$;
