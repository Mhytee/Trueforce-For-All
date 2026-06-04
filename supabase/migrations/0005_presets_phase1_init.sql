-- Community preset sharing Phase 1. Per docs/preset-sharing-design.md:
-- presets table stores whole rated tuning bundles (Tier 3 taste fields);
-- voting drives Wilson ranking; anonymous submitter_id reuses the CarFacts
-- IP-derived identity. Phase 1 ships single car presets; packs and game
-- presets are deferred. Mirrors carfacts_preset_phase1_init applied live.

create table public.presets (
    id uuid primary key default gen_random_uuid(),
    name text not null
        check (length(btrim(name)) between 2 and 96),
    -- Optional curator credit. Sourced from Settings.SharingAuthor on the
    -- client. Free text but length-capped + sanitized.
    author text
        check (author is null or length(author) <= 64),
    description text
        check (description is null or length(description) <= 1024),
    game text not null
        check (length(game) between 1 and 64),
    car_id text not null
        check (length(car_id) between 1 and 128),
    -- Body shape token. Phase 1 ships car presets only; game presets defer.
    body_type text not null default 'trueforce-car-preset'
        check (body_type = 'trueforce-car-preset'),
    -- CarPresetFile schema version. Forward-compatible parsing on the
    -- client side: ignore unknown fields, never crash.
    body_version int not null default 1,
    -- The CarOverride or full CarPresetFile JSON payload. Tiny (typically
    -- 0.3-2.5 KB) so inline jsonb beats a Storage bucket.
    body jsonb not null,
    -- Which effect sections the body actually contains. Powers
    -- "presets with a RevLimiter section" filters and the Auto-Sync
    -- per-section toggles. Client computes from non-null CarOverride
    -- sections; server validates against a whitelist.
    effect_tags text[] not null default '{}'::text[],
    upvotes int not null default 0,
    downvotes int not null default 0,
    wilson_score numeric not null default 0,
    -- Server-incremented via record_preset_download; powers trending sort.
    downloads int not null default 0,
    -- Anonymous identity (same shape as car_fact_submissions:
    -- sha256(client_ip + rotating salt), server-derived).
    submitter_id text not null,
    source_ip_hash text not null,
    plugin_version text,
    reported boolean not null default false,
    -- Moderator nuke without destroying the row.
    is_suppressed boolean not null default false,
    created_at timestamptz not null default now()
);

create index presets_game_car_wilson_idx
    on public.presets (game, car_id, wilson_score desc);
create index presets_game_car_recent_idx
    on public.presets (game, car_id, created_at desc);
create index presets_game_car_downloads_idx
    on public.presets (game, car_id, downloads desc);
create index presets_submitter_idx
    on public.presets (submitter_id, created_at desc);

create table public.preset_votes (
    preset_id uuid not null references public.presets(id) on delete cascade,
    voter_id text not null,
    source_ip_hash text not null,
    value smallint not null check (value in (-1, 1)),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (preset_id, voter_id)
);

create index preset_votes_voter_idx
    on public.preset_votes (voter_id, updated_at desc);

alter table public.presets       enable row level security;
alter table public.preset_votes  enable row level security;

create policy presets_select_public
    on public.presets for select to anon
    using (not is_suppressed);

create policy preset_votes_select_public
    on public.preset_votes for select to anon
    using (true);

revoke insert, update, delete on public.presets       from anon;
revoke insert, update, delete on public.preset_votes  from anon;

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

    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    -- 10 uploads/hour/IP - presets are slower + bigger than fact submissions.
    select count(*) into v_recent from public.presets
        where source_ip_hash = v_ip_hash
              and created_at > now() - interval '1 hour';
    if v_recent >= 10 then raise exception 'preset upload rate limit exceeded'; end if;

    insert into public.presets
        (name, author, description, game, car_id,
         body_type, body_version, body, effect_tags,
         submitter_id, source_ip_hash, plugin_version)
    values
        (v_name, v_author, v_desc, v_game, v_car_id,
         'trueforce-car-preset', p_body_version, p_body, v_clean_tags,
         v_submitter_id, v_ip_hash, p_plugin_version)
    returning id into v_id;

    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id);
end;
$$;

create or replace function public.vote_preset(
    p_preset_id uuid,
    p_value     smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric;
begin
    if p_value not in (-1, 1) then
        raise exception 'value must be +1 or -1';
    end if;
    if not exists (select 1 from public.presets where id = p_preset_id and not is_suppressed) then
        raise exception 'preset not found';
    end if;

    v_ip := _client_ip();
    v_voter_id := _derive_submitter_id(v_ip);

    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.preset_votes
        where source_ip_hash = v_ip_hash
              and updated_at > now() - interval '1 hour';
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;

    insert into public.preset_votes
        (preset_id, voter_id, source_ip_hash, value)
    values (p_preset_id, v_voter_id, v_ip_hash, p_value)
    on conflict (preset_id, voter_id) do update
        set value          = excluded.value,
            source_ip_hash = excluded.source_ip_hash,
            updated_at     = now();

    -- Recompute counters + Wilson from the votes table so flipping a vote
    -- doesn't double-count. Cheap (indexed lookup).
    select count(*) filter (where value = 1),
           count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.preset_votes
      where preset_id = p_preset_id;

    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));

    update public.presets
        set upvotes      = coalesce(v_upvotes, 0),
            downvotes    = coalesce(v_downvotes, 0),
            wilson_score = v_wilson
        where id = p_preset_id;

    return jsonb_build_object(
        'upvotes',      v_upvotes,
        'downvotes',    v_downvotes,
        'wilson_score', v_wilson,
        'voter_id',     v_voter_id);
end;
$$;

create or replace function public.record_preset_download(
    p_preset_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if not exists (select 1 from public.presets where id = p_preset_id and not is_suppressed) then
        return;
    end if;
    update public.presets
        set downloads = downloads + 1
        where id = p_preset_id;
end;
$$;

create or replace function public.report_preset(
    p_preset_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    update public.presets
        set reported = true
        where id = p_preset_id;
end;
$$;
