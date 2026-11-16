-- 0150: widget_stats gains weeks[] and months[], for week-over-week and
-- month-over-month on the TV widget. Additive only: every existing field, including
-- history[], is unchanged, as is null-on-bad-token and 401-without-apikey.
--
-- WHY DISCRETE BUCKETS RATHER THAN A LONGER history[]. "Week over week" normally means
-- two non-overlapping weeks, not two rolling 7 day windows that share six days. The
-- same goes for months. Extending history to 35 days so the widget could do the
-- arithmetic itself was measured at 123 ms per call against roughly 30 ms today,
-- because each extra day is another windowed distinct-count; these two are one grouped
-- scan each and cost less than history already does.
--
-- THE complete FLAG IS LOAD-BEARING. A bucket is complete only when the whole period
-- lies inside the data we hold AND the period has ended:
--
--     complete = period_start >= min(telemetry.day) AND period_end < current_date
--
-- That single test catches both ways a bucket lies to you. The in-progress week or
-- month is short simply because it has not finished, and would otherwise read as a
-- collapse every Monday and every 1st, the same trap dau vs today_active already
-- avoids. The first bucket is short because the data starts mid-period: telemetry
-- begins 2026-09-21, so September reads 181 against October's 546 and a naive
-- month-over-month would show a 200% jump that is pure measurement artifact.
--
-- The same test keeps working once the 90 day prune starts biting. Pruning raises
-- min(telemetry.day), so a month that has been partly deleted stops satisfying the
-- first clause and flags itself incomplete rather than quietly reading low. months is
-- capped at 4, which is about 120 days, so the oldest entry WILL go incomplete and then
-- disappear as the window moves. That is correct behaviour, not a bug to fix.
--
-- A widget must not plot or compare a bucket whose complete is false. Showing it greyed
-- or labelled "in progress" is fine.
--
-- Today is excluded from both, matching dau and history: buckets are built from
-- complete days only. So on a Monday the current week has no rows yet and simply does
-- not appear, which is the honest answer rather than a bucket reading zero.

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
  v_weeks     jsonb;
  v_months    jsonb;
  v_floor     date;
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

  select min(day) into v_floor from public.telemetry;

  select greatest(
           (select count(*) from public.telemetry_devices),
           (select count(distinct anon_id) from public.telemetry)
         ) into v_installs;

  select s.downloads_total into v_downloads
  from public.download_snapshots s order by s.day desc limit 1;

  -- Eight complete UTC days ending on dau_day (yesterday), oldest first.
  -- wau/mau here use windows that END ON THAT DAY, unlike the top-level wau/mau which
  -- include today so far, so consecutive entries are comparable with each other.
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
    where d.d >= v_floor
  ) x;

  -- Last 8 discrete ISO weeks (Monday start), oldest first.
  select coalesce(jsonb_agg(x.e order by x.p), '[]'::jsonb) into v_weeks
  from (
    select w.p,
           jsonb_build_object(
             'period',   to_char(w.p, 'YYYY-MM-DD'),
             'active',   w.active,
             'complete', (w.p >= v_floor and w.p + 6 < current_date)
           ) as e
    from (
      select date_trunc('week', t.day)::date as p,
             count(distinct t.anon_id)       as active
      from public.telemetry t
      where t.day < current_date
      group by 1
      order by 1 desc
      limit 8
    ) w
  ) x;

  -- Last 4 calendar months, oldest first.
  select coalesce(jsonb_agg(x.e order by x.p), '[]'::jsonb) into v_months
  from (
    select m.p,
           jsonb_build_object(
             'period',   to_char(m.p, 'YYYY-MM'),
             'active',   m.active,
             'complete', (m.p >= v_floor
                          and (m.p + interval '1 month')::date <= current_date)
           ) as e
    from (
      select date_trunc('month', t.day)::date as p,
             count(distinct t.anon_id)        as active
      from public.telemetry t
      where t.day < current_date
      group by 1
      order by 1 desc
      limit 4
    ) m
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
    'history',         v_history,
    'weeks',           v_weeks,
    'months',          v_months
  );
end;
$function$;

revoke all on function public.widget_stats(text) from public;
grant execute on function public.widget_stats(text) to anon, authenticated;
