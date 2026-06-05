-- Per-user profiles with unique case-insensitive username. Mirrors
-- profiles_usernames applied live. Username is the public identity
-- attached to anything a signed-in user contributes (preset author
-- denormalized into presets.author from profiles.username; future:
-- vote attribution, comments, etc.).
--
-- Plugin treats username and Settings.SharingAuthor as the same thing:
-- signed-in users see + edit profile.username via Settings.SharingAuthor;
-- signed-out users have a freeform local SharingAuthor with no
-- uniqueness check.

create table public.profiles (
    id           uuid primary key references auth.users(id) on delete cascade,
    username     text not null
        check (username ~ '^[a-zA-Z0-9_]{3,32}$'),
    -- Case-folded copy enforces "Mhytee" / "mhytee" / "MHYTEE" all
    -- collide. Real-case username stays for display.
    username_lower text generated always as (lower(username)) stored,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);
create unique index profiles_username_lower_unique
    on public.profiles (username_lower);

alter table public.profiles enable row level security;
create policy profiles_select_public
    on public.profiles for select to anon using (true);
revoke insert, update, delete on public.profiles from anon;

-- ---- check_username_available --------------------------------------
-- Returns { available: bool, reason: text|null }. Reasons:
--   format    - failed the regex (3-32 chars, [a-zA-Z0-9_])
--   reserved  - matches a hardcoded reserved name
--   taken     - matches an existing profile (case-insensitive)
--   self      - matches the caller's own current username (treat as
--               available so the modal can no-op cleanly)
create or replace function public.check_username_available(p_username text)
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_lower text;
    v_uid uuid;
    v_existing_owner uuid;
    v_reserved text[] := array[
        'admin','administrator','root','system','support','help',
        'official','trueforce','community','anonymous','moderator',
        'mod','staff','team','owner','dev','test'
    ];
begin
    if p_username is null then
        return jsonb_build_object('available', false, 'reason', 'required');
    end if;
    if not (p_username ~ '^[a-zA-Z0-9_]{3,32}$') then
        return jsonb_build_object('available', false, 'reason', 'format');
    end if;
    v_lower := lower(p_username);
    if v_lower = any(v_reserved) then
        return jsonb_build_object('available', false, 'reason', 'reserved');
    end if;
    v_uid := auth.uid();
    select id into v_existing_owner
        from public.profiles where username_lower = v_lower;
    if v_existing_owner is null then
        return jsonb_build_object('available', true);
    end if;
    if v_uid is not null and v_existing_owner = v_uid then
        return jsonb_build_object('available', true, 'reason', 'self');
    end if;
    return jsonb_build_object('available', false, 'reason', 'taken');
end;
$$;

-- ---- set_username ---------------------------------------------------
-- Auth-gated. Upserts profile.username for the caller. Re-validates
-- via check_username_available so a race between two clients claiming
-- the same name still resolves cleanly via the unique index on the
-- loser.
create or replace function public.set_username(p_username text)
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_avail jsonb;
    v_reason text;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_avail := public.check_username_available(p_username);
    if not (v_avail->>'available')::boolean then
        v_reason := v_avail->>'reason';
        raise exception 'username % unavailable: %', p_username, v_reason
            using errcode = '23505';
    end if;
    insert into public.profiles (id, username)
        values (v_uid, p_username)
        on conflict (id) do update
            set username   = excluded.username,
                updated_at = now();
    return jsonb_build_object('username', p_username, 'user_id', v_uid);
end;
$$;

-- ---- get_my_profile -------------------------------------------------
-- Auth-gated. Used by the client right after verify_otp to decide
-- whether to open the PickUsernameWindow. Returns
-- { signed_in: bool, user_id: uuid, username: text|null }.
create or replace function public.get_my_profile()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_username text;
begin
    v_uid := auth.uid();
    if v_uid is null then return jsonb_build_object('signed_in', false); end if;
    select username into v_username
        from public.profiles where id = v_uid;
    return jsonb_build_object(
        'signed_in', true,
        'user_id', v_uid,
        'username', v_username);
end;
$$;

-- ---- upload_preset: stamp author from profile when signed in --------
-- When v_uid is set, override the client-supplied p_author with the
-- authoritative profiles.username. Anonymous uploads keep using the
-- freeform p_author. presets.author is denormalized for display, so
-- it reflects the user's CURRENT username at upload time (rotates if
-- they rename later).

create or replace function public.upload_preset(
    p_name           text,
    p_game           text,
    p_car_id         text,
    p_body           jsonb,
    p_author         text default null,
    p_description    text default null,
    p_effect_tags    text[] default '{}'::text[],
    p_body_version   int default 1,
    p_plugin_version text default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text;
    v_submitter_id text; v_recent int; v_id uuid;
    v_name text; v_author text; v_desc text;
    v_game text; v_car_id text;
    v_tag text; v_clean_tags text[] := '{}';
    v_uid uuid; v_username text;
    v_allowed_tags text[] := array[
        'engine','revlimiter','roadbumps','tractionloss','gearshift',
        'abs','pitlimiter','drs','collision','audio','airborne'
    ];
begin
    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then
        raise exception 'name must be 2..96 chars';
    end if;

    v_author := nullif(btrim(coalesce(p_author, '')), '');
    if v_author is not null and length(v_author) > 64 then
        raise exception 'author too long';
    end if;

    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then
        raise exception 'description too long';
    end if;

    if p_game is null or length(btrim(p_game)) = 0 then
        raise exception 'game required';
    end if;
    v_game := btrim(p_game);
    if length(v_game) > 64 then raise exception 'game too long'; end if;

    if p_car_id is null or length(btrim(p_car_id)) = 0 then
        raise exception 'car_id required';
    end if;
    v_car_id := btrim(p_car_id);
    if length(v_car_id) > 128 then raise exception 'car_id too long'; end if;

    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then
        raise exception 'body must be a JSON object';
    end if;

    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then
        raise exception 'body_version invalid';
    end if;

    if p_effect_tags is not null then
        foreach v_tag in array p_effect_tags loop
            if v_tag is not null
                and v_tag = any(v_allowed_tags)
                and not (v_tag = any(v_clean_tags))
            then
                v_clean_tags := v_clean_tags || v_tag;
            end if;
        end loop;
    end if;

    v_ip := _client_ip();
    v_submitter_id := _derive_submitter_id(v_ip);
    v_uid := auth.uid();
    if v_uid is not null then
        select username into v_username
            from public.profiles where id = v_uid;
        if v_username is not null then
            v_author := v_username;
        end if;
    end if;

    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.presets
        where source_ip_hash = v_ip_hash
              and created_at > now() - interval '1 hour';
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;

    insert into public.presets
        (name, author, description, game, car_id,
         body_type, body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values
        (v_name, v_author, v_desc, v_game, v_car_id,
         'trueforce-car-preset', p_body_version, p_body, v_clean_tags,
         v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id,
        'submitter_id', v_submitter_id,
        'owner_user_id', v_uid,
        'author', v_author);
end;
$$;
