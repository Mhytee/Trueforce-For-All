-- Custom engines as a third community-shareable kind. Game-agnostic
-- (a firing pattern is the same in any game), so no game/car_id
-- scoping like presets/game_presets. Same voting / ownership /
-- content-version / update-detection infra. Body is a serialized
-- CustomEngineDef ({id, name, pattern, isElectric, electricMode}).

create table if not exists public.custom_engines (
    id uuid primary key default gen_random_uuid(),
    name text not null
        check (length(btrim(name)) between 2 and 96),
    author text
        check (author is null or length(author) <= 64),
    description text
        check (description is null or length(description) <= 1024),
    body_version int not null default 1,
    body jsonb not null,
    upvotes int not null default 0,
    downvotes int not null default 0,
    wilson_score numeric not null default 0,
    downloads int not null default 0,
    submitter_id text not null,
    source_ip_hash text not null,
    plugin_version text,
    owner_user_id uuid references auth.users(id) on delete set null,
    content_version int not null default 1,
    reported boolean not null default false,
    is_suppressed boolean not null default false,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists custom_engines_wilson_idx
    on public.custom_engines (wilson_score desc);
create index if not exists custom_engines_recent_idx
    on public.custom_engines (created_at desc);
create index if not exists custom_engines_downloads_idx
    on public.custom_engines (downloads desc);
create index if not exists custom_engines_owner_idx
    on public.custom_engines (owner_user_id, created_at desc);

create table if not exists public.custom_engine_votes (
    custom_engine_id uuid not null references public.custom_engines(id) on delete cascade,
    voter_id text not null,
    source_ip_hash text not null,
    value smallint not null check (value in (-1, 1)),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (custom_engine_id, voter_id)
);

create index if not exists custom_engine_votes_voter_idx
    on public.custom_engine_votes (voter_id, updated_at desc);

alter table public.custom_engines       enable row level security;
alter table public.custom_engine_votes  enable row level security;

drop policy if exists custom_engines_select_public on public.custom_engines;
create policy custom_engines_select_public
    on public.custom_engines for select to anon
    using (not is_suppressed);

drop policy if exists custom_engine_votes_select_public on public.custom_engine_votes;
create policy custom_engine_votes_select_public
    on public.custom_engine_votes for select to anon
    using (true);

revoke insert, update, delete on public.custom_engines       from anon;
revoke insert, update, delete on public.custom_engine_votes  from anon;

-- ---- Upload / update / vote / download / report / fetch RPCs --------

create or replace function public.upload_custom_engine(
    p_name           text,
    p_body           jsonb,
    p_description    text default null,
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
    v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;

    if p_name is null then raise exception 'name required'; end if;
    v_name := btrim(p_name);
    if length(v_name) < 2 or length(v_name) > 96 then
        raise exception 'name must be 2..96 chars';
    end if;

    v_desc := nullif(btrim(coalesce(p_description, '')), '');
    if v_desc is not null and length(v_desc) > 1024 then
        raise exception 'description too long';
    end if;

    if p_body is null or pg_catalog.jsonb_typeof(p_body) <> 'object' then
        raise exception 'body must be a JSON object';
    end if;
    if p_body_version is null or p_body_version < 1 or p_body_version > 100 then
        raise exception 'body_version invalid';
    end if;

    v_ip := _client_ip();
    v_submitter_id := v_uid::text;
    select username into v_author from public.profiles where id = v_uid;
    if v_author is null then raise exception 'set a username first'; end if;
    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    -- Shared upload rate limit across presets + game_presets + custom_engines.
    select (select count(*) from public.presets
              where source_ip_hash = v_ip_hash
                and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets
              where source_ip_hash = v_ip_hash
                and created_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engines
              where source_ip_hash = v_ip_hash
                and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 10 then raise exception 'upload rate limit exceeded'; end if;

    insert into public.custom_engines
        (name, author, description,
         body_version, body,
         submitter_id, source_ip_hash, plugin_version, owner_user_id)
    values (v_name, v_author, v_desc,
            p_body_version, p_body,
            v_submitter_id, v_ip_hash, p_plugin_version, v_uid)
    returning id into v_id;

    return jsonb_build_object(
        'id', v_id, 'submitter_id', v_submitter_id,
        'owner_user_id', v_uid, 'author', v_author);
end;
$$;

create or replace function public.update_custom_engine(
    p_custom_engine_id uuid,
    p_name             text default null,
    p_description      text default null,
    p_body             jsonb default null,
    p_body_version     int default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid; v_owner uuid;
    v_ip text; v_ip_hash text; v_recent int;
    v_new_version int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select owner_user_id into v_owner
      from public.custom_engines where id = p_custom_engine_id and not is_suppressed;
    if not found then raise exception 'custom engine not found'; end if;
    if v_owner is null or v_owner <> v_uid then
        raise exception 'not your custom engine';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.presets        where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour')
         + (select count(*) from public.game_presets   where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engines where source_ip_hash = v_ip_hash and created_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 30 then raise exception 'edit rate limit exceeded'; end if;

    if p_name is not null then
        if length(btrim(p_name)) < 2 or length(btrim(p_name)) > 96 then
            raise exception 'name must be 2..96 chars';
        end if;
        update public.custom_engines set name = btrim(p_name) where id = p_custom_engine_id;
    end if;
    if p_description is not null then
        if length(btrim(p_description)) > 1024 then raise exception 'description too long'; end if;
        update public.custom_engines set description = nullif(btrim(p_description), '') where id = p_custom_engine_id;
    end if;
    if p_body is not null then
        if pg_catalog.jsonb_typeof(p_body) <> 'object' then
            raise exception 'body must be a JSON object';
        end if;
        update public.custom_engines set body = p_body where id = p_custom_engine_id;
    end if;
    if p_body_version is not null then
        if p_body_version < 1 or p_body_version > 100 then raise exception 'body_version invalid'; end if;
        update public.custom_engines set body_version = p_body_version where id = p_custom_engine_id;
    end if;

    update public.custom_engines
        set content_version = content_version + 1,
            updated_at = now()
        where id = p_custom_engine_id
        returning content_version into v_new_version;

    return jsonb_build_object(
        'id', p_custom_engine_id,
        'content_version', v_new_version);
end;
$$;

create or replace function public.vote_custom_engine(
    p_custom_engine_id uuid,
    p_value            smallint
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric;
    v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then
        raise exception 'value must be -1, 0, or 1';
    end if;
    if not exists (select 1 from public.custom_engines where id = p_custom_engine_id and not is_suppressed) then
        raise exception 'custom engine not found';
    end if;

    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes
              where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes
              where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engine_votes
              where source_ip_hash = v_ip_hash and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;

    if p_value = 0 then
        delete from public.custom_engine_votes
         where custom_engine_id = p_custom_engine_id and voter_id = v_voter_id;
    else
        insert into public.custom_engine_votes (custom_engine_id, voter_id, source_ip_hash, value)
        values (p_custom_engine_id, v_voter_id, v_ip_hash, p_value)
        on conflict (custom_engine_id, voter_id) do update
            set value = excluded.value,
                source_ip_hash = excluded.source_ip_hash,
                updated_at = now();
    end if;

    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes
      from public.custom_engine_votes where custom_engine_id = p_custom_engine_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.custom_engines
        set upvotes = coalesce(v_upvotes, 0),
            downvotes = coalesce(v_downvotes, 0),
            wilson_score = v_wilson
        where id = p_custom_engine_id;

    return jsonb_build_object(
        'upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id,
        'value', p_value);
end;
$$;

create or replace function public.record_custom_engine_download(
    p_custom_engine_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    if not exists (select 1 from public.custom_engines where id = p_custom_engine_id and not is_suppressed) then
        return;
    end if;
    update public.custom_engines
        set downloads = downloads + 1
        where id = p_custom_engine_id;
end;
$$;

create or replace function public.report_custom_engine(
    p_custom_engine_id uuid
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
begin
    update public.custom_engines
        set reported = true
        where id = p_custom_engine_id;
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
            'is_suppressed', is_suppressed)
          from public.custom_engines
         where id = any(p_ids);
end;
$$;

create or replace function public.get_my_custom_engine_votes(p_custom_engine_ids uuid[])
returns setof jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then return; end if;
    if p_custom_engine_ids is null or array_length(p_custom_engine_ids, 1) is null then return; end if;
    return query
        select jsonb_build_object('custom_engine_id', custom_engine_id, 'value', value)
          from public.custom_engine_votes
         where voter_id = v_uid::text
           and custom_engine_id = any(p_custom_engine_ids);
end;
$$;

-- get_my_presets gains a third UNION arm for custom_engines so the
-- "My uploads" view shows all three kinds in one list.
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
               ''::text as game, ''::text as car_id,
               '{}'::text[] as effect_tags,
               upvotes, downvotes, wilson_score, downloads,
               created_at, is_suppressed
          from public.custom_engines where owner_user_id = v_uid
    ),
    combined as (
        select * from car_rows
        union all select * from game_rows
        union all select * from engine_rows
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
        created_at desc
     limit v_lim;
end;
$$;

-- get_account_stats counts custom_engines too.
create or replace function public.get_account_stats()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_uid uuid;
    v_email text; v_created_at timestamptz; v_username text;
    v_upload_count int; v_downloads bigint; v_upvotes bigint; v_downvotes bigint;
begin
    v_uid := auth.uid();
    if v_uid is null then return jsonb_build_object('signed_in', false); end if;
    select email, created_at into v_email, v_created_at from auth.users where id = v_uid;
    select username into v_username from public.profiles where id = v_uid;

    select
        (select count(*) from public.presets        where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.game_presets   where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.custom_engines where owner_user_id = v_uid and not is_suppressed),
        coalesce((select sum(downloads) from public.presets        where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(downloads) from public.game_presets   where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(downloads) from public.custom_engines where owner_user_id = v_uid and not is_suppressed), 0),
        coalesce((select sum(upvotes)   from public.presets        where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(upvotes)   from public.game_presets   where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(upvotes)   from public.custom_engines where owner_user_id = v_uid and not is_suppressed), 0),
        coalesce((select sum(downvotes) from public.presets        where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(downvotes) from public.game_presets   where owner_user_id = v_uid and not is_suppressed), 0)
      + coalesce((select sum(downvotes) from public.custom_engines where owner_user_id = v_uid and not is_suppressed), 0)
      into v_upload_count, v_downloads, v_upvotes, v_downvotes;

    return jsonb_build_object(
        'signed_in', true, 'user_id', v_uid, 'email', v_email,
        'username', v_username, 'created_at', v_created_at,
        'upload_count', v_upload_count,
        'downloads_received', v_downloads,
        'upvotes_received', v_upvotes,
        'downvotes_received', v_downvotes);
end;
$$;

-- export_my_data includes custom engines + their votes.
create or replace function public.export_my_data()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_result jsonb;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    v_result := jsonb_build_object(
        'exported_at', now(),
        'user_id', v_uid,
        'profile', (select to_jsonb(p) from public.profiles p where p.id = v_uid),
        'presets', coalesce(
            (select jsonb_agg(to_jsonb(pr)) from public.presets pr where pr.owner_user_id = v_uid),
            '[]'::jsonb),
        'game_presets', coalesce(
            (select jsonb_agg(to_jsonb(gp)) from public.game_presets gp where gp.owner_user_id = v_uid),
            '[]'::jsonb),
        'custom_engines', coalesce(
            (select jsonb_agg(to_jsonb(ce)) from public.custom_engines ce where ce.owner_user_id = v_uid),
            '[]'::jsonb),
        'votes_cast_on_presets', coalesce(
            (select jsonb_agg(to_jsonb(pv)) from public.preset_votes pv where pv.voter_id = v_uid::text),
            '[]'::jsonb),
        'votes_cast_on_game_presets', coalesce(
            (select jsonb_agg(to_jsonb(gv)) from public.game_preset_votes gv where gv.voter_id = v_uid::text),
            '[]'::jsonb),
        'votes_cast_on_custom_engines', coalesce(
            (select jsonb_agg(to_jsonb(cv)) from public.custom_engine_votes cv where cv.voter_id = v_uid::text),
            '[]'::jsonb),
        'car_fact_submissions', coalesce(
            (select jsonb_agg(to_jsonb(s)) from car_fact_submissions s where s.submitter_id = v_uid::text),
            '[]'::jsonb),
        'car_fact_votes', coalesce(
            (select jsonb_agg(to_jsonb(v)) from car_fact_consensus_votes v where v.voter_id = v_uid::text),
            '[]'::jsonb)
    );
    return v_result;
end;
$$;

-- delete_my_account anonymizes custom_engine_votes too.
create or replace function public.delete_my_account()
returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare v_uid uuid; v_username text; v_upload_count int;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    select username into v_username from public.profiles where id = v_uid;
    select
        (select count(*) from public.presets        where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.game_presets   where owner_user_id = v_uid and not is_suppressed)
      + (select count(*) from public.custom_engines where owner_user_id = v_uid and not is_suppressed)
      into v_upload_count;

    update public.preset_votes
        set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
            source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.game_preset_votes
        set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
            source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.custom_engine_votes
        set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
            source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.car_fact_consensus_votes
        set voter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
            source_ip_hash = 'redacted'
        where voter_id = v_uid::text;
    update public.car_fact_submissions
        set submitter_id = 'deleted:' || encode(gen_random_bytes(16), 'hex'),
            source_ip_hash = 'redacted'
        where submitter_id = v_uid::text;

    delete from auth.users where id = v_uid;
    return jsonb_build_object(
        'deleted', true,
        'username_was', v_username,
        'uploads_orphaned', v_upload_count);
end;
$$;
