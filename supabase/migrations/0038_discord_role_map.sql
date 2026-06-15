-- 0038: Discord contribution-role map (role_key -> the server's Discord role id), read
-- by the `discord-role-sync` Edge Function (service role) to reconcile roles. The owner
-- fills in the real Discord role ids after creating the roles in their server (a row
-- with a null discord_role_id is skipped by the sync). Internal table: RLS on, no client
-- access (service role bypasses).
create table if not exists public.discord_role_map (
  role_key        text primary key,
  discord_role_id text,
  label           text,
  updated_at      timestamptz not null default now()
);
alter table public.discord_role_map enable row level security;
revoke all on public.discord_role_map from anon, authenticated;

insert into public.discord_role_map (role_key, label) values
  ('creator',         'Creator (shared at least 1 community item)'),
  ('popular_creator', 'Popular Creator (>= 25 total downloads)')
on conflict (role_key) do nothing;
