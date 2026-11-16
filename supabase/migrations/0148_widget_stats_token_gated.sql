-- 0148: a narrow, token-gated read of the headline usage numbers, for the
-- owner's own screensaver widget (Android TV launcher, separate project).
--
-- Why an RPC and not a direct query. telemetry, telemetry_devices and the daily
-- rollups are RLS-forced with zero policies, so only the service key can read
-- them, and a service key must never travel in an APK: it would hand anyone who
-- unzips the app full read AND write over every table in the project. This
-- returns five integers and a timestamp, so the widget needs no table access at
-- all and the blast radius of a leaked token is "someone learns our DAU".
--
-- Why a token and not plain anon. The anon key is already extractable from the
-- shipped plugin, so an ungated RPC would make the usage numbers effectively
-- public. They are not secret in a security sense, but publishing them is a
-- business decision rather than a side effect of building a widget, so the gate
-- keeps that decision open (owner, 2026-11-16).
--
-- The token is stored as a SHA-256 hash, never in the clear: a dump, a backup or
-- a support query then cannot leak a working credential. sha256() is built in on
-- PG 11+, so this needs no extension. Rotating is one insert plus one delete and
-- is documented at the bottom of this file.
--
-- No per-install data leaves here. Every value is a count over the whole base,
-- so the result cannot be narrowed to any anon_id, wheel, game or setting.

create table if not exists public.widget_tokens (
  name         text primary key,
  token_sha256 text not null,
  created_at   timestamptz not null default now(),
  last_used_at timestamptz
);

alter table public.widget_tokens enable row level security;
alter table public.widget_tokens force row level security;
-- Deliberately zero policies: only SECURITY DEFINER functions may read this.
revoke all on table public.widget_tokens from public, anon, authenticated;

create or replace function public.widget_stats(p_token text)
returns jsonb
language plpgsql
security definer
set search_path to 'public'
as $function$
declare
  v_name text;
  v_installs int;
begin
  if p_token is null or length(p_token) < 16 then
    return null;
  end if;

  select t.name into v_name
  from public.widget_tokens t
  where t.token_sha256 = encode(sha256(convert_to(p_token, 'UTF8')), 'hex');

  -- Unknown token: null, not an error. An error message is itself a signal, and
  -- the caller has no legitimate need to tell "wrong token" from "no data".
  if v_name is null then
    return null;
  end if;

  update public.widget_tokens set last_used_at = now() where name = v_name;

  -- Total installs: telemetry_devices is the DURABLE record but is only filled
  -- by the 04:25 UTC rollup, so it lags up to a day and once undercounted 36
  -- live installs as 18. The raw table is current but is pruned past 90 days.
  -- greatest() of the two is correct in both directions, now and later.
  select greatest(
           (select count(*) from public.telemetry_devices),
           (select count(distinct anon_id) from public.telemetry)
         ) into v_installs;

  return jsonb_build_object(
    'generated_at',   to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'installs_total', v_installs,
    -- Latest COMPLETE day. Today is excluded on purpose: a partial day always
    -- reads low and would show the widget a false decline every morning.
    'dau',            (select count(distinct anon_id) from public.telemetry
                        where day = current_date - 1),
    'dau_day',        to_char(current_date - 1, 'YYYY-MM-DD'),
    -- Today so far, labelled as partial so the widget can style it differently.
    'today_active',   (select count(distinct anon_id) from public.telemetry
                        where day = current_date),
    'wau',            (select count(distinct anon_id) from public.telemetry
                        where day > current_date - 7  and day <= current_date),
    'mau',            (select count(distinct anon_id) from public.telemetry
                        where day > current_date - 30 and day <= current_date),
    'new_installs_7d',(select count(*) from (
                        select anon_id, min(day) as f from public.telemetry group by anon_id
                      ) z where z.f > current_date - 7)
  );
end;
$function$;

-- The widget calls this with the project's anon key plus the token.
revoke all on function public.widget_stats(text) from public;
grant execute on function public.widget_stats(text) to anon, authenticated;

-- Rotating the token (run as service role, replace the literal):
--   delete from public.widget_tokens where name = 'tv-widget';
--   insert into public.widget_tokens (name, token_sha256)
--   values ('tv-widget', encode(sha256(convert_to('<new token>','UTF8')),'hex'));
