-- 0149: widget_stats gains downloads_total and an 8 day history, for the TV
-- screensaver widget. Additive only: the eight existing fields, the null-on-bad-token
-- behaviour and the 401-without-apikey behaviour are all unchanged.
--
-- TWO THINGS THE REQUEST ASSUMED THAT ARE NOT TRUE, and what this does instead.
--
-- 1. "from the download counts we already store in the database". We do not store any.
--    The only download counters in this database are COMMUNITY CONTENT: game_presets
--    .downloads (72 at the time of writing), packs.downloads, custom_engines.downloads
--    and presets.downloads. Returning any of those as "TF4ALL downloads" would report
--    72 where the real figure is 5267, so none of them is "the one that best means
--    downloads"; the right answer is that the figure lives only on GitHub.
--
-- 2. Per-day downloads cannot be read from GitHub at all. Its API exposes one
--    cumulative counter per release asset and no time series, so a daily figure is only
--    obtainable by snapshotting the counter and differencing consecutive snapshots.
--
-- So this migration adds the snapshot that both fields need. pg_net and pg_cron are
-- already in use here (see tf4all-telemetry-rollup and tf4all-arcade-standing-snapshot),
-- so the fetch runs in the database on a schedule rather than adding a service.
--
-- WHY THE FETCH IS IN TWO PHASES. pg_net cannot issue and collect a request inside one
-- transaction. net.http_get queues a row that its background worker picks up, and the
-- worker cannot see a row your own uncommitted transaction just wrote, so calling
-- net.http_collect_response(..., async := false) in the same function body blocks until
-- the client gives up. That was tried first and hung for two minutes before failing, so
-- it is written down here rather than rediscovered. The request and the collect are
-- therefore separate functions on separate cron jobs five minutes apart, which run as
-- separate transactions.
--
-- THE DAY BOUNDARY, because the off-by-one here is the easy mistake. The collect runs at
-- 00:10 UTC and stores under the day it ran, so a row for day D means "cumulative
-- downloads as of the start of day D". Downloads DURING day D are therefore
-- snapshot(D+1) - snapshot(D), which is what history[].downloads computes. Both rows
-- must exist or the value is null, as the spec allows. That means per-day downloads
-- begin appearing the day after this migration lands, and are null for every day before
-- the first snapshot. There is no way to backfill them: GitHub never had the history.
--
-- UNIQUE OR REPEATS: repeats. GitHub's download_count is a raw hit counter. It counts
-- the same person downloading twice, every bot, scanner and mirror, and re-downloads
-- after a reinstall. There is no unique figure available from GitHub and no endpoint
-- that de-duplicates. installs_total, already in this payload, IS unique (one random
-- GUID per install), so the widget should label the two differently and never imply
-- downloads are people.

-- ---------------------------------------------------------------------------
-- Stores
-- ---------------------------------------------------------------------------

create table if not exists public.download_snapshots (
  day             date primary key,       -- cumulative total as of the START of this day
  downloads_total integer not null,
  release_count   integer,                -- so pagination drift is detectable, see below
  fetched_at      timestamptz not null default now()
);

alter table public.download_snapshots enable row level security;
alter table public.download_snapshots force row level security;
revoke all on table public.download_snapshots from public, anon, authenticated;

-- One row, holding the id of the request awaiting collection.
create table if not exists public.download_fetch_state (
  id           smallint primary key default 1 check (id = 1),
  request_id   bigint,
  requested_at timestamptz
);

alter table public.download_fetch_state enable row level security;
alter table public.download_fetch_state force row level security;
revoke all on table public.download_fetch_state from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Phase 1: issue the request
-- ---------------------------------------------------------------------------

create or replace function public.snapshot_download_request()
returns bigint
language plpgsql
security definer
set search_path to 'public'
as $function$
declare
  v_req bigint;
begin
  -- per_page=100 covers the 39 releases that exist today. release_count is stored by
  -- phase 2 so that crossing 100 shows up as a stalled count rather than a silent
  -- undercount; at that point this needs to follow the Link header.
  select net.http_get(
    url     := 'https://api.github.com/repos/Mhytee/Trueforce-For-All/releases?per_page=100',
    headers := jsonb_build_object(
                 'User-Agent', 'tf4all-stats-snapshot',
                 'Accept',     'application/vnd.github+json')
  ) into v_req;

  insert into public.download_fetch_state (id, request_id, requested_at)
  values (1, v_req, now())
  on conflict (id) do update
    set request_id = excluded.request_id, requested_at = excluded.requested_at;

  return v_req;
end;
$function$;

revoke all on function public.snapshot_download_request() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Phase 2: collect it and store the total
-- ---------------------------------------------------------------------------

create or replace function public.snapshot_download_total()
returns integer
language plpgsql
security definer
set search_path to 'public'
as $function$
declare
  v_req      bigint;
  v_status   integer;
  v_body     text;
  v_json     jsonb;
  v_total    integer;
  v_releases integer;
begin
  select s.request_id into v_req from public.download_fetch_state s where s.id = 1;
  if v_req is null then
    raise warning '[tf4all] download snapshot: no pending request to collect';
    return null;
  end if;

  select r.status_code, r.content into v_status, v_body
  from net._http_response r where r.id = v_req;

  -- Not ready, or pg_net already garbage-collected it. Either way leave the previous
  -- snapshot alone and let tomorrow's run try again.
  if v_status is null then
    raise warning '[tf4all] download snapshot: response % not available', v_req;
    return null;
  end if;

  if v_status <> 200 then
    raise warning '[tf4all] download snapshot: http %', v_status;
    return null;
  end if;

  v_json     := v_body::jsonb;
  v_releases := jsonb_array_length(v_json);

  select coalesce(sum((a->>'download_count')::integer), 0) into v_total
  from jsonb_array_elements(v_json) r,
       jsonb_array_elements(r->'assets') a;

  -- A zero total from a 200 means the response shape changed, not that downloads
  -- vanished. Never write a wrong number: the series is differenced, so one bad row
  -- corrupts two days.
  if v_total = 0 and v_releases > 0 then
    raise warning '[tf4all] download snapshot read 0 across % releases, not storing', v_releases;
    return null;
  end if;

  insert into public.download_snapshots (day, downloads_total, release_count, fetched_at)
  values (current_date, v_total, v_releases, now())
  on conflict (day) do update
    set downloads_total = excluded.downloads_total,
        release_count   = excluded.release_count,
        fetched_at      = excluded.fetched_at;

  update public.download_fetch_state set request_id = null where id = 1;

  return v_total;
end;
$function$;

-- Both make outbound requests or write: never anon-callable. Supabase's default ACL
-- grants EXECUTE to anon at creation and "revoke from public" does not remove it.
revoke all on function public.snapshot_download_total() from public, anon, authenticated;

-- Two jobs, five minutes apart, because of the transaction boundary explained above.
select cron.schedule('tf4all-download-request',  '5 0 * * *',
                     $cron$select public.snapshot_download_request();$cron$);
select cron.schedule('tf4all-download-snapshot', '10 0 * * *',
                     $cron$select public.snapshot_download_total();$cron$);

-- ---------------------------------------------------------------------------
-- widget_stats: the eight existing fields verbatim, plus two
-- ---------------------------------------------------------------------------

create or replace function public.widget_stats(p_token text)
returns jsonb
language plpgsql
security definer
set search_path to 'public'
as $function$
declare
  v_name      text;
  v_installs  int;
  v_downloads int;
  v_history   jsonb;
begin
  if p_token is null or length(p_token) < 16 then
    return null;
  end if;

  select t.name into v_name
  from public.widget_tokens t
  where t.token_sha256 = encode(sha256(convert_to(p_token, 'UTF8')), 'hex');

  if v_name is null then
    return null;
  end if;

  update public.widget_tokens set last_used_at = now() where name = v_name;

  select greatest(
           (select count(*) from public.telemetry_devices),
           (select count(distinct anon_id) from public.telemetry)
         ) into v_installs;

  -- Most recent snapshot, i.e. cumulative as of the start of today.
  select s.downloads_total into v_downloads
  from public.download_snapshots s order by s.day desc limit 1;

  -- Eight complete UTC days ending on dau_day (yesterday), oldest first.
  -- wau/mau here use windows that END ON THAT DAY, unlike the top-level wau/mau which
  -- include today so far, so consecutive entries are comparable with each other.
  -- Days before any telemetry exists are omitted rather than returned as zeros.
  select coalesce(jsonb_agg(x.e order by x.d), '[]'::jsonb) into v_history
  from (
    select d.d,
           jsonb_build_object(
             'day',       to_char(d.d, 'YYYY-MM-DD'),
             'dau',       coalesce(a.dau, 0),
             'wau',       coalesce(a.wau, 0),
             'mau',       coalesce(a.mau, 0),
             'downloads', case
                            when s_next.downloads_total is not null
                             and s_this.downloads_total is not null
                            then s_next.downloads_total - s_this.downloads_total
                          end
           ) as e
    from (select (current_date - 1 - g)::date as d
          from generate_series(0, 7) as g) d
    left join lateral (
      select count(distinct t.anon_id) filter (where t.day = d.d)     as dau,
             count(distinct t.anon_id) filter (where t.day > d.d - 7) as wau,
             count(distinct t.anon_id)                                as mau
      from public.telemetry t
      where t.day > d.d - 30 and t.day <= d.d
    ) a on true
    left join public.download_snapshots s_this on s_this.day = d.d
    left join public.download_snapshots s_next on s_next.day = d.d + 1
    where d.d >= (select min(day) from public.telemetry)
  ) x;

  return jsonb_build_object(
    'generated_at',    to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'installs_total',  v_installs,
    'dau',             (select count(distinct anon_id) from public.telemetry
                         where day = current_date - 1),
    'dau_day',         to_char(current_date - 1, 'YYYY-MM-DD'),
    'today_active',    (select count(distinct anon_id) from public.telemetry
                         where day = current_date),
    'wau',             (select count(distinct anon_id) from public.telemetry
                         where day > current_date - 7  and day <= current_date),
    'mau',             (select count(distinct anon_id) from public.telemetry
                         where day > current_date - 30 and day <= current_date),
    'new_installs_7d', (select count(*) from (
                         select anon_id, min(day) as f from public.telemetry group by anon_id
                       ) z where z.f > current_date - 7),
    'downloads_total', v_downloads,
    'history',         v_history
  );
end;
$function$;

revoke all on function public.widget_stats(text) from public;
grant execute on function public.widget_stats(text) to anon, authenticated;
