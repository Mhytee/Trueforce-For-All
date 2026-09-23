-- 0029: a vote requires having downloaded the item first, so votes reflect
-- people who actually ran the preset (better recommendation signal) and
-- drive-by rating is impossible. Enforced server-side in all four content
-- vote RPCs by checking the per-user *_user_downloads table (PK (item,user),
-- so it's a fast index lookup). Retraction (value 0) stays allowed so a user
-- can always remove a vote. Owners auto-excluded: their self-downloads are
-- never recorded, so they have no download row to vote against.

create or replace function public.vote_preset(p_preset_id uuid, p_value smallint)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.presets where id = p_preset_id and not is_suppressed) then
        raise exception 'preset not found';
    end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;
    if p_value <> 0 and not exists (
        select 1 from public.preset_user_downloads
         where preset_id = p_preset_id and user_id = v_uid) then
        raise exception 'download before voting';
    end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent from public.preset_votes
        where voter_id = v_voter_id and updated_at > now() - interval '1 hour';
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;
    if p_value = 0 then
        delete from public.preset_votes where preset_id = p_preset_id and voter_id = v_voter_id;
    else
        insert into public.preset_votes (preset_id, voter_id, source_ip_hash, value)
        values (p_preset_id, v_voter_id, v_ip_hash, p_value)
        on conflict (preset_id, voter_id) do update
            set value = excluded.value, source_ip_hash = excluded.source_ip_hash, updated_at = now();
    end if;
    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes from public.preset_votes where preset_id = p_preset_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.presets set upvotes = coalesce(v_upvotes, 0), downvotes = coalesce(v_downvotes, 0),
        wilson_score = v_wilson where id = p_preset_id;
    return jsonb_build_object('upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id, 'value', p_value);
end;
$$;

create or replace function public.vote_game_preset(p_game_preset_id uuid, p_value smallint)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.game_presets where id = p_game_preset_id and not is_suppressed) then
        raise exception 'game preset not found';
    end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;
    if p_value <> 0 and not exists (
        select 1 from public.game_preset_user_downloads
         where game_preset_id = p_game_preset_id and user_id = v_uid) then
        raise exception 'download before voting';
    end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;
    if p_value = 0 then
        delete from public.game_preset_votes where game_preset_id = p_game_preset_id and voter_id = v_voter_id;
    else
        insert into public.game_preset_votes (game_preset_id, voter_id, source_ip_hash, value)
        values (p_game_preset_id, v_voter_id, v_ip_hash, p_value)
        on conflict (game_preset_id, voter_id) do update
            set value = excluded.value, source_ip_hash = excluded.source_ip_hash, updated_at = now();
    end if;
    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes from public.game_preset_votes where game_preset_id = p_game_preset_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.game_presets set upvotes = coalesce(v_upvotes, 0), downvotes = coalesce(v_downvotes, 0),
        wilson_score = v_wilson where id = p_game_preset_id;
    return jsonb_build_object('upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id, 'value', p_value);
end;
$$;

create or replace function public.vote_custom_engine(p_custom_engine_id uuid, p_value smallint)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.custom_engines where id = p_custom_engine_id and not is_suppressed) then
        raise exception 'custom engine not found';
    end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;
    if p_value <> 0 and not exists (
        select 1 from public.custom_engine_user_downloads
         where custom_engine_id = p_custom_engine_id and user_id = v_uid) then
        raise exception 'download before voting';
    end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engine_votes
              where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;
    if p_value = 0 then
        delete from public.custom_engine_votes where custom_engine_id = p_custom_engine_id and voter_id = v_voter_id;
    else
        insert into public.custom_engine_votes (custom_engine_id, voter_id, source_ip_hash, value)
        values (p_custom_engine_id, v_voter_id, v_ip_hash, p_value)
        on conflict (custom_engine_id, voter_id) do update
            set value = excluded.value, source_ip_hash = excluded.source_ip_hash, updated_at = now();
    end if;
    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes from public.custom_engine_votes where custom_engine_id = p_custom_engine_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.custom_engines set upvotes = coalesce(v_upvotes, 0), downvotes = coalesce(v_downvotes, 0),
        wilson_score = v_wilson where id = p_custom_engine_id;
    return jsonb_build_object('upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id, 'value', p_value);
end;
$$;

create or replace function public.vote_pack(p_pack_id uuid, p_value smallint)
returns jsonb language plpgsql security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text; v_ip_hash text; v_voter_id text; v_recent int;
    v_upvotes int; v_downvotes int; v_wilson numeric; v_uid uuid;
begin
    v_uid := auth.uid();
    if v_uid is null then raise exception 'sign-in required'; end if;
    if p_value not in (-1, 0, 1) then raise exception 'value must be -1, 0, or 1'; end if;
    if not exists (select 1 from public.packs where id = p_pack_id and not is_suppressed) then
        raise exception 'pack not found';
    end if;
    v_voter_id := v_uid::text;
    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;
    if p_value <> 0 and not exists (
        select 1 from public.pack_user_downloads
         where pack_id = p_pack_id and user_id = v_uid) then
        raise exception 'download before voting';
    end if;
    v_ip := _client_ip();
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select (select count(*) from public.preset_votes        where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.game_preset_votes   where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.custom_engine_votes where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
         + (select count(*) from public.pack_votes          where voter_id = v_voter_id and updated_at > now() - interval '1 hour')
      into v_recent;
    if v_recent >= 60 then raise exception 'vote rate limit exceeded'; end if;
    if p_value = 0 then
        delete from public.pack_votes where pack_id = p_pack_id and voter_id = v_voter_id;
    else
        insert into public.pack_votes (pack_id, voter_id, source_ip_hash, value)
        values (p_pack_id, v_voter_id, v_ip_hash, p_value)
        on conflict (pack_id, voter_id) do update
            set value = excluded.value, source_ip_hash = excluded.source_ip_hash, updated_at = now();
    end if;
    select count(*) filter (where value = 1), count(*) filter (where value = -1)
      into v_upvotes, v_downvotes from public.pack_votes where pack_id = p_pack_id;
    v_wilson := public.wilson_lower_bound(coalesce(v_upvotes, 0), coalesce(v_downvotes, 0));
    update public.packs set upvotes = coalesce(v_upvotes, 0), downvotes = coalesce(v_downvotes, 0),
        wilson_score = v_wilson where id = p_pack_id;
    return jsonb_build_object('upvotes', v_upvotes, 'downvotes', v_downvotes,
        'wilson_score', v_wilson, 'voter_id', v_voter_id, 'value', p_value);
end;
$$;
