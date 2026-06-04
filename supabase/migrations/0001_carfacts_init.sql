-- Trueforce For All community backend: CarFacts schema v2 (hardened)
--
-- Threat model: the anon API key ships in the plugin DLL. Anyone with the
-- plugin has it. Treat the anon key as a PUBLIC token; never grant it any
-- permission that should require trust.
--
-- Posture:
--   * Tables car_fact_submissions, car_fact_consensus_votes, app_config,
--     submitter_blocked are PRIVATE: anon has no SELECT / INSERT / UPDATE /
--     DELETE on them. Only car_fact_consensus is readable (so the plugin
--     can pull trusted facts on startup).
--   * Writes only go through two SECURITY DEFINER RPC functions:
--       submit_car_fact(...) - validates fact_type, normalizes payload,
--           derives submitter_id from a server-side hash of (client IP +
--           daily-rotating salt), enforces per-IP rate limit, checks block
--           list, inserts, recomputes consensus.
--       vote_car_fact(...)  - same identity derivation + rate limit, upserts
--           a vote, recomputes consensus.
--   * Internal helpers (_recompute_car_fact_consensus, _derive_submitter_id,
--     normalize_car_fact_payload, wilson_lower_bound) have EXECUTE revoked
--     from anon so they cannot be called directly via PostgREST RPC.
--   * Consensus has an is_suppressed flag the recompute respects, so a
--     moderator can kill a bad consensus row WITHOUT having it silently
--     reinstated by the next submission. Moderation uses the service_role
--     key (NOT shipped in the plugin) via Supabase Studio or psql.
--
-- This replaces the original v1 design where tables were directly client-
-- writable. v1 was found to be exploitable: anyone with the anon key could
-- (a) overwrite anyone else's vote, (b) spoof a victim's submitter_id to
-- frame them, (c) sybil-flood with fake UUIDs to inflate Wilson scores, (d)
-- submit non-conforming jsonb that broke downstream consumers, (e) directly
-- call recompute via PostgREST RPC. See the adversarial review at
-- wf_dc9e63e0-1ab for the full list.

create extension if not exists "pgcrypto";   -- gen_random_uuid + digest

-- ====================================================================
-- app_config: rotating salt for submitter_id derivation
-- ====================================================================
-- One row per knob. The salt makes submitter_id rotate periodically (a
-- moderator triggers rotation by overwriting the value): after rotation,
-- yesterday's submitter_id stops matching today's submissions, so an
-- accumulated identity an attacker built up under a single IP loses its
-- prior reputation. Rotation also limits how long a moderator's ban-by-id
-- lasts; that's a deliberate trade-off (false-positive bans wear off).
create table if not exists app_config (
    key         text primary key,
    value       text not null,
    updated_at  timestamptz not null default now()
);
insert into app_config (key, value)
    values ('submitter_id_salt', encode(gen_random_bytes(16), 'hex'))
    on conflict (key) do nothing;

-- ====================================================================
-- submitter_blocked: ban list consulted by both RPCs
-- ====================================================================
-- Populated only by moderators via the service_role key. The RPCs reject
-- submissions / votes from submitter_ids present here. A row stays
-- effective until either the salt rotates (the submitter_id changes) or
-- the moderator removes it.
create table if not exists submitter_blocked (
    submitter_id text primary key,
    reason       text,
    blocked_at   timestamptz not null default now()
);

-- ====================================================================
-- Submissions: append-only, server-controlled writes
-- ====================================================================
-- submitter_id is server-derived inside submit_car_fact, NOT a client field.
-- source_ip_hash is the raw IP hash (no salt) used solely for rate-limiting
-- queries; it lasts forever (no salt rotation) so the rate limit can't be
-- bypassed by waiting a day.
create table if not exists car_fact_submissions (
    id              uuid primary key default gen_random_uuid(),
    game            text not null,
    car_id          text not null,
    -- engine_layout stores one EngineLayout name per fact (V8CROSSPLANE,
    -- INLINE6, ROTARY2, etc.). Each EngineLayout uniquely encodes both
    -- cylinder count and firing pattern variant, so splitting them would
    -- give voting/Wilson surfaces for axes users can't independently
    -- assert from the EngineLayoutCombo.
    fact_type       text not null check (fact_type in ('engine_layout','car_name','redline')),
    payload         jsonb not null,
    submitter_id    text not null,
    source_ip_hash  text not null,
    plugin_version  text,
    created_at      timestamptz not null default now()
);
create index if not exists car_fact_submissions_lookup_idx
    on car_fact_submissions (game, car_id, fact_type);
create index if not exists car_fact_submissions_submitter_idx
    on car_fact_submissions (submitter_id);
create index if not exists car_fact_submissions_iphash_recent_idx
    on car_fact_submissions (source_ip_hash, created_at);

-- ====================================================================
-- Consensus: anon-readable, server-write-only
-- ====================================================================
create table if not exists car_fact_consensus (
    game                    text not null,
    car_id                  text not null,
    fact_type               text not null,
    payload                 jsonb not null,
    confirmations           int not null default 0,
    refutations             int not null default 0,
    wilson_score            numeric(10,6) not null default 0,
    supporting_submissions  int not null default 1,
    is_suppressed           boolean not null default false,
    last_recomputed         timestamptz not null default now(),
    primary key (game, car_id, fact_type)
);
create index if not exists car_fact_consensus_wilson_idx
    on car_fact_consensus (game, wilson_score desc) where not is_suppressed;

-- ====================================================================
-- Votes: server-write-only, one row per (entry, derived voter_id)
-- ====================================================================
-- payload_hash binds a vote to the candidate payload it was cast on. Without
-- it, a sybil burst could flip the consensus's candidate via distinct-count
-- and silently inherit the honest Wilson score from votes that endorsed the
-- previous payload. payload_hash is part of the primary key so a single
-- voter can legitimately endorse multiple candidates (e.g. Forza engine-
-- swap variants where both layouts are valid for the same carId).
create table if not exists car_fact_consensus_votes (
    consensus_game        text not null,
    consensus_car_id      text not null,
    consensus_fact_type   text not null,
    voter_id              text not null,        -- server-derived (salt-rotating)
    payload_hash          text not null,        -- sha256(canonical payload at vote time)
    source_ip_hash        text not null,        -- raw-IP hash for rate-limiting (salt-independent)
    direction             int not null check (direction in (-1, 1)),
    created_at            timestamptz not null default now(),
    updated_at            timestamptz not null default now(),
    primary key (consensus_game, consensus_car_id, consensus_fact_type, voter_id, payload_hash),
    foreign key (consensus_game, consensus_car_id, consensus_fact_type)
        references car_fact_consensus (game, car_id, fact_type)
        on delete cascade
);
create index if not exists car_fact_consensus_votes_voter_idx
    on car_fact_consensus_votes (voter_id, updated_at);
create index if not exists car_fact_consensus_votes_iphash_recent_idx
    on car_fact_consensus_votes (source_ip_hash, updated_at);
create index if not exists car_fact_consensus_votes_payload_idx
    on car_fact_consensus_votes (consensus_game, consensus_car_id, consensus_fact_type, payload_hash);

-- ====================================================================
-- Wilson lower bound (z=1.96, ~95% confidence)
-- ====================================================================
create or replace function wilson_lower_bound(pos int, neg int)
returns numeric
language plpgsql
immutable
as $$
declare
    n    numeric;
    phat numeric;
    z    constant numeric := 1.959964;
    z2   constant numeric := 3.841459;
begin
    n := pos + neg;
    if n <= 0 then return 0; end if;
    phat := pos::numeric / n::numeric;
    return ( phat + z2 / (2*n) - z * sqrt( (phat*(1-phat) + z2/(4*n)) / n ) ) / (1 + z2/n);
end;
$$;

-- ====================================================================
-- Payload normalization (per fact_type)
-- ====================================================================
-- Returns the canonical jsonb to store, or NULL if the payload is malformed.
-- Normalization prevents the split-vote attack where structurally-distinct-
-- but-semantically-equal payloads (different key order, casing, whitespace)
-- would be counted as different values by the most-submitted-wins query.
create or replace function normalize_car_fact_payload(p_fact_type text, p_payload jsonb)
returns jsonb
language plpgsql
immutable
as $$
declare
    v_int  int;
    v_text text;
begin
    if p_payload is null then return null; end if;

    if p_fact_type = 'engine_layout' then
        if jsonb_typeof(p_payload->'layout') <> 'string' then return null; end if;
        v_text := upper(btrim(p_payload->>'layout'));
        -- Whitelist matches the EngineLayout enum (FiringPatterns.cs)
        -- excluding Auto / Electric / Custom: Auto is "I don't know",
        -- Electric is a different feature (no firing pattern), Custom is
        -- authored via the Custom Engine library, separate pipeline.
        if v_text not in (
            'SINGLE','TWIN','INLINE3','INLINE4','INLINE5','INLINE6',
            'BOXER4','BOXER6',
            'V6_60EVEN','V6_ODDFIRE','V8CROSSPLANE','V8FLATPLANE',
            'V10_72','V12_60','W12_W16',
            'VTWIN90','VTWIN45',
            'ROTARY1','ROTARY2','ROTARY3','ROTARY4'
        ) then
            return null;
        end if;
        return jsonb_build_object('layout', v_text);

    elsif p_fact_type = 'car_name' then
        if jsonb_typeof(p_payload->'name') <> 'string' then return null; end if;
        v_text := btrim(p_payload->>'name');
        if length(v_text) < 2 or length(v_text) > 96 then return null; end if;
        return jsonb_build_object('name', v_text);

    elsif p_fact_type = 'redline' then
        if jsonb_typeof(p_payload->'rpm') <> 'number' then return null; end if;
        v_int := (p_payload->>'rpm')::int;
        if v_int < 500 or v_int > 25000 then return null; end if;
        return jsonb_build_object('rpm', v_int);
    end if;
    return null;
end;
$$;

-- ====================================================================
-- Submitter / voter id derivation
-- ====================================================================
-- Hash(IP + current salt). Stable until salt rotates; deterministic per
-- session so resubmitting from the same IP yields the same id (the
-- DISTINCT-submitter query in recompute then collapses repeats).
create or replace function _derive_submitter_id(p_ip text)
returns text
language plpgsql
stable
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_salt text;
begin
    select value into v_salt from app_config where key = 'submitter_id_salt';
    if v_salt is null then
        v_salt := encode(gen_random_bytes(16), 'hex');
        insert into app_config (key, value)
            values ('submitter_id_salt', v_salt)
            on conflict (key) do nothing;
    end if;
    return encode(digest(coalesce(p_ip, 'unknown') || '|' || v_salt, 'sha256'), 'hex');
end;
$$;

-- Extract the trusted client IP from Cloudflare. Supabase fronts everything
-- with Cloudflare, which overwrites any client-supplied cf-connecting-ip at
-- the edge - that's the only header value we can trust.
--
-- Crucial: we DO NOT fall back to x-forwarded-for. XFF's first hop is
-- client-controllable in any topology where Cloudflare is bypassed (self-
-- hosted dev/staging, direct PostgREST connection inside Supabase infra,
-- future Supabase config changes), and an attacker could put a victim's
-- real IP there to mint the victim's submitter_id (frame-job) or voter_id
-- (vote-as-victim). When cf-connecting-ip is missing we collapse to a
-- single 'unknown' bucket: loud and self-limiting via the rate limit
-- rather than silently spoofable.
create or replace function _client_ip()
returns text
language plpgsql
stable
as $$
declare
    v_hdrs jsonb;
    v_ip   text;
begin
    -- request.headers is set by PostgREST per-call. May fail in non-HTTP
    -- contexts (psql, direct SQL); guard with current_setting(..., true).
    begin
        v_hdrs := current_setting('request.headers', true)::jsonb;
    exception when others then v_hdrs := null;
    end;
    if v_hdrs is null then return 'unknown'; end if;
    -- nullif is SQL conditional-expression syntax (must stay unqualified);
    -- btrim is a real pg_catalog function and the schema-pinned helpers
    -- below qualify it explicitly.
    v_ip := nullif(v_hdrs ->> 'cf-connecting-ip', '');
    if v_ip is null then return 'unknown'; end if;
    v_ip := btrim(v_ip);
    if v_ip = '' then return 'unknown'; end if;
    return v_ip;
end;
$$;

-- ====================================================================
-- Consensus recompute (internal; only RPCs call this)
-- ====================================================================
-- Uses count(distinct submitter_id) so a single IP submitting 100 times
-- contributes 1, not 100. Respects is_suppressed: a moderator-frozen row
-- is left alone.
--
-- Wilson is driven SOLELY by votes, not by submission count. v1 of this
-- file seeded `pos_votes := pos_votes + top_distinct`, which let an
-- attacker with a residential proxy network (100 IPs costs single-digit
-- dollars) capture consensus for any car the real community hadn't
-- engaged with: 100 distinct submitter_ids → Wilson ≈ 0.96 → looks
-- trusted, no real votes needed. Now Wilson stays at 0 until users
-- actively confirm/refute via vote_car_fact, and the attacker's only
-- ranking signal is supporting_submissions, which the plugin's pull
-- query can threshold separately (trusted: high wilson; pending: high
-- supporting_submissions but no wilson — surfaces for vote, not for
-- automatic use).
create or replace function _recompute_car_fact_consensus(
    p_game text, p_car_id text, p_fact_type text
) returns void
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    top_payload      jsonb;
    top_distinct     int;
    total_distinct   int;
    pos_votes        int;
    neg_votes        int;
    v_suppressed     boolean;
    v_current_payload  jsonb;
    v_current_distinct int;
    v_current_wilson   numeric;
    v_payload_hash     text;
    sticky_margin      int;
begin
    -- A suppressed consensus row is moderator-frozen. Skip.
    -- v_current_distinct is filled later from the latest-per-submitter
    -- model below; the stored supporting_submissions is rebuilt each call
    -- and isn't the source of truth at recompute time.
    select is_suppressed, payload, wilson_score
        into v_suppressed, v_current_payload, v_current_wilson
      from car_fact_consensus
     where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
    if coalesce(v_suppressed, false) then return; end if;

    -- Most-distinct-submitter payload is the *candidate*. A submitter's
    -- LATEST submission per (game, car_id, fact_type) counts as their
    -- current endorsement; earlier submissions get superseded so a user
    -- who changes their mind doesn't double-count. Tiebreak picks the
    -- most-recently-active payload (latest_activity desc) rather than the
    -- earliest, so a community gradually migrating to a new value doesn't
    -- get stuck on the original. Wilson never reads this; it's the
    -- proposition votes attach to.
    with latest_per_submitter as (
        select distinct on (submitter_id) submitter_id, payload, created_at
          from car_fact_submissions
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type
         order by submitter_id, created_at desc
    ),
    payload_supports as (
        select payload,
               count(*)::int   as distinct_supporters,
               max(created_at) as latest_activity
          from latest_per_submitter
         group by payload
    )
    select payload, distinct_supporters
      into top_payload, top_distinct
      from payload_supports
     order by distinct_supporters desc, latest_activity desc
     limit 1;

    if top_payload is null then
        delete from car_fact_consensus
         where game = p_game and car_id = p_car_id and fact_type = p_fact_type;
        return;
    end if;

    -- Incumbent's CURRENT endorser count (not the stored value), computed
    -- under the same latest-per-submitter model. Drops to zero when every
    -- submitter who originally endorsed the incumbent has since moved on,
    -- letting the stickiness check correctly allow the migration.
    if v_current_payload is not null then
        with latest_per_submitter as (
            select distinct on (submitter_id) submitter_id, payload
              from car_fact_submissions
             where game = p_game and car_id = p_car_id and fact_type = p_fact_type
             order by submitter_id, created_at desc
        )
        select count(*)::int into v_current_distinct
          from latest_per_submitter
         where payload = v_current_payload;
    else
        v_current_distinct := 0;
    end if;

    -- Stickiness: scaled by incumbent support and Wilson score. Low-support
    -- or unconfirmed incumbents are easy to displace (they're effectively
    -- bootstrap candidates that haven't been community-vetted yet). High-
    -- support, Wilson-confirmed incumbents become progressively stickier
    -- so a 100-IP attack can't cheaply displace a well-established
    -- consensus. The +1 floor keeps bootstrap displacement realistic;
    -- a constant +2 was below the residential-proxy cost floor cited
    -- above for low-incumbent cases.
    if v_current_payload is not null and v_current_payload <> top_payload then
        if coalesce(v_current_wilson, 0) >= 0.5 then
            -- Vote-confirmed incumbent: hard to displace.
            sticky_margin := greatest(
                3,
                ceil(coalesce(v_current_distinct, 0) * 0.5)::int);
        elsif coalesce(v_current_distinct, 0) <= 2 then
            -- Effectively bootstrap; only +1 to displace.
            sticky_margin := 1;
        elsif coalesce(v_current_distinct, 0) <= 9 then
            sticky_margin := 2;
        else
            sticky_margin := ceil(coalesce(v_current_distinct, 0) * 0.3)::int;
        end if;
        if top_distinct < coalesce(v_current_distinct, 0) + sticky_margin then
            top_payload := v_current_payload;
            top_distinct := coalesce(v_current_distinct, 0);
        end if;
    end if;

    -- supporting_submissions reflects distinct count of the WINNING payload
    -- (after stickiness applied), not the global total. Drives the plugin's
    -- future "pending review" tier separately from Wilson.
    total_distinct := top_distinct;

    -- Vote tally is restricted to votes cast on THIS payload (payload_hash
    -- match). Votes for prior candidates become inert when the candidate
    -- flips - they neither help nor hurt the new payload's Wilson score.
    v_payload_hash := encode(digest(top_payload::text, 'sha256'), 'hex');
    select count(*) filter (where direction =  1),
           count(*) filter (where direction = -1)
      into pos_votes, neg_votes
      from car_fact_consensus_votes
     where consensus_game     = p_game
       and consensus_car_id   = p_car_id
       and consensus_fact_type = p_fact_type
       and payload_hash       = v_payload_hash;

    insert into car_fact_consensus
        (game, car_id, fact_type, payload, confirmations, refutations,
         wilson_score, supporting_submissions, is_suppressed, last_recomputed)
    values
        (p_game, p_car_id, p_fact_type, top_payload,
         coalesce(pos_votes, 0), coalesce(neg_votes, 0),
         wilson_lower_bound(coalesce(pos_votes, 0), coalesce(neg_votes, 0)),
         coalesce(total_distinct, 0), false, now())
    on conflict (game, car_id, fact_type) do update
        set payload                = excluded.payload,
            confirmations          = excluded.confirmations,
            refutations            = excluded.refutations,
            wilson_score           = excluded.wilson_score,
            supporting_submissions = excluded.supporting_submissions,
            last_recomputed        = excluded.last_recomputed
        where car_fact_consensus.is_suppressed = false;
end;
$$;

-- ====================================================================
-- Public RPC: submit_car_fact
-- ====================================================================
-- Returns jsonb {id, submitter_id} on success. Raises on validation
-- failure, block-list hit, rate-limit hit. The client doesn't need the
-- submitter_id (it's not persisted client-side) but it's returned so logs
-- correlate.
create or replace function submit_car_fact(
    p_game text,
    p_car_id text,
    p_fact_type text,
    p_payload jsonb,
    p_plugin_version text default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_canonical jsonb;
    v_ip text;
    v_ip_hash text;
    v_submitter_id text;
    v_recent int;
    v_id uuid;
begin
    -- Input sanity. PostgREST rejects empty strings via length checks so
    -- the RPC stays defensive even if a caller bypasses the client.
    if p_game is null or length(trim(p_game)) = 0 then
        raise exception 'game required';
    end if;
    if p_car_id is null or length(trim(p_car_id)) = 0 then
        raise exception 'car_id required';
    end if;
    if length(p_game) > 64 then raise exception 'game too long'; end if;
    if length(p_car_id) > 128 then raise exception 'car_id too long'; end if;

    v_canonical := normalize_car_fact_payload(p_fact_type, p_payload);
    if v_canonical is null then
        raise exception 'invalid payload for fact_type %', p_fact_type;
    end if;

    v_ip := _client_ip();
    v_submitter_id := _derive_submitter_id(v_ip);

    if exists (select 1 from submitter_blocked where submitter_id = v_submitter_id) then
        raise exception 'submitter blocked';
    end if;

    -- Rate limit by IP (not by salt-rotating submitter_id) so the salt
    -- rotation doesn't reset attackers.
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from car_fact_submissions
     where source_ip_hash = v_ip_hash
       and created_at > now() - interval '1 hour';
    if v_recent >= 30 then
        raise exception 'rate limit exceeded';
    end if;

    insert into car_fact_submissions
        (game, car_id, fact_type, payload, submitter_id, source_ip_hash, plugin_version)
    values
        (trim(p_game), trim(p_car_id), p_fact_type, v_canonical,
         v_submitter_id, v_ip_hash, p_plugin_version)
    returning id into v_id;

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type);
    return jsonb_build_object('id', v_id, 'submitter_id', v_submitter_id);
end;
$$;

-- ====================================================================
-- Public RPC: vote_car_fact
-- ====================================================================
create or replace function vote_car_fact(
    p_game text,
    p_car_id text,
    p_fact_type text,
    p_direction int,
    p_expected_payload_hash text default null
) returns jsonb
language plpgsql
security definer
set search_path = public, extensions, pg_temp
as $$
declare
    v_ip text;
    v_ip_hash text;
    v_voter_id text;
    v_recent int;
    v_consensus_payload jsonb;
    v_payload_hash text;
begin
    if p_direction not in (-1, 1) then
        raise exception 'direction must be -1 or 1';
    end if;

    -- The vote attaches to the CURRENT consensus payload. Read it now and
    -- hash it for payload_hash binding so subsequent candidate flips can't
    -- silently transfer this vote's signal onto an attacker's payload.
    select payload into v_consensus_payload
      from car_fact_consensus
     where game = trim(p_game) and car_id = trim(p_car_id) and fact_type = p_fact_type;
    if v_consensus_payload is null then
        raise exception 'no consensus to vote on';
    end if;
    v_payload_hash := encode(digest(v_consensus_payload::text, 'sha256'), 'hex');

    -- Optimistic-concurrency check: caller may pass the payload_hash they
    -- saw when rendering the Confirm UI. If the candidate has flipped
    -- between UI render and RPC receive, the caller's intent was for a
    -- DIFFERENT payload than the one we'd attach to; raise and force the
    -- caller to refetch. Without this guard, a user clicking Confirm on
    -- a stale cache silently endorses the just-flipped attacker payload.
    -- Nullable for backward compatibility while the vote UI is being
    -- built; a future release should make it required and reject null.
    if p_expected_payload_hash is not null
       and p_expected_payload_hash <> v_payload_hash then
        raise exception 'consensus changed, refetch';
    end if;

    v_ip := _client_ip();
    v_voter_id := _derive_submitter_id(v_ip);

    if exists (select 1 from submitter_blocked where submitter_id = v_voter_id) then
        raise exception 'voter blocked';
    end if;

    -- Rate limit votes against raw-IP hash (not the salt-rotating
    -- voter_id), matching submit_car_fact. Salt rotation must not reset
    -- attacker rate-limit budgets at the exact moment the rotation is
    -- supposed to be hardening defenses.
    v_ip_hash := encode(digest(v_ip, 'sha256'), 'hex');
    select count(*) into v_recent
      from car_fact_consensus_votes
     where source_ip_hash = v_ip_hash
       and updated_at > now() - interval '1 hour';
    if v_recent >= 30 then
        raise exception 'rate limit exceeded';
    end if;

    -- Per (game, car_id, fact_type, voter_id, payload_hash). A voter can
    -- legitimately vote on multiple candidate payloads (e.g. Forza engine-
    -- swap variants where both layouts are valid for the same carId).
    insert into car_fact_consensus_votes
        (consensus_game, consensus_car_id, consensus_fact_type, voter_id,
         payload_hash, source_ip_hash, direction)
    values (trim(p_game), trim(p_car_id), p_fact_type, v_voter_id,
            v_payload_hash, v_ip_hash, p_direction)
    on conflict (consensus_game, consensus_car_id, consensus_fact_type,
                 voter_id, payload_hash)
        do update set direction      = excluded.direction,
                      source_ip_hash = excluded.source_ip_hash,
                      updated_at     = now();

    perform _recompute_car_fact_consensus(trim(p_game), trim(p_car_id), p_fact_type);
    return jsonb_build_object('voter_id', v_voter_id, 'payload_hash', v_payload_hash);
end;
$$;

-- ====================================================================
-- Permissions
-- ====================================================================
-- Anon can ONLY:
--   1. SELECT from car_fact_consensus (pull trusted facts)
--   2. EXECUTE submit_car_fact and vote_car_fact
-- Everything else is locked.

revoke all on car_fact_submissions     from anon, authenticated, public;
revoke all on car_fact_consensus_votes from anon, authenticated, public;
revoke all on app_config               from anon, authenticated, public;
revoke all on submitter_blocked        from anon, authenticated, public;

grant select on car_fact_consensus to anon, authenticated;

-- Internal helpers must NOT be callable via PostgREST RPC. SECURITY DEFINER
-- makes them powerful; un-grant EXECUTE so anon can't invoke them.
revoke execute on function _recompute_car_fact_consensus(text,text,text) from anon, authenticated, public;
revoke execute on function _derive_submitter_id(text)            from anon, authenticated, public;
revoke execute on function _client_ip()                          from anon, authenticated, public;
revoke execute on function normalize_car_fact_payload(text,jsonb) from anon, authenticated, public;
revoke execute on function wilson_lower_bound(int,int)           from anon, authenticated, public;

grant execute on function submit_car_fact(text,text,text,jsonb,text)  to anon, authenticated;
grant execute on function vote_car_fact(text,text,text,int,text)      to anon, authenticated;

-- ====================================================================
-- RLS (belt-and-braces; the revokes above are the real gate)
-- ====================================================================
alter table car_fact_submissions       enable row level security;
alter table car_fact_consensus         enable row level security;
alter table car_fact_consensus_votes   enable row level security;
alter table app_config                 enable row level security;
alter table submitter_blocked          enable row level security;

drop policy if exists "Anyone can read car_fact_consensus" on car_fact_consensus;
create policy "Anyone can read car_fact_consensus"
    on car_fact_consensus for select using (true);

-- No other policies on the other tables → RLS denies all anon/auth access.

-- ====================================================================
-- Moderation operations (run via service_role, NOT shipped in plugin)
-- ====================================================================
-- Suppress a bad consensus entry:
--   update car_fact_consensus set is_suppressed = true
--    where game = 'FH6' and car_id = 'Car_X' and fact_type = 'engine_layout';
--
-- Ban a submitter (use a hash you've identified offline as abusive):
--   insert into submitter_blocked (submitter_id, reason)
--     values ('<the-hash>', 'sybil flood 2026-06-04');
--
-- Rotate the salt (invalidates all current submitter_ids; legitimate users
-- just get re-seeded on next submission, attackers lose accumulated state):
--   update app_config
--      set value = encode(gen_random_bytes(16), 'hex'),
--          updated_at = now()
--    where key = 'submitter_id_salt';
