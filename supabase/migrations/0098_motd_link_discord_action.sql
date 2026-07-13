-- 0098: allow the 'link_discord' MOTD action (client support added on beta,
-- commit 0971137). The Discord nudge messages' button used to open a bare
-- invite URL, which put people in the server with no account link (so no
-- roles and no identity). With the action set, updated clients run the
-- in-app Discord link flow instead, which verifies the account, auto-joins
-- the server via the bot, and unlocks roles; rows keep link_url so the
-- already-shipped 0.2.0 client (which predates the action and falls through
-- to the URL) still gets the plain invite as a fallback.

alter table public.motd drop constraint if exists motd_action_valid;
alter table public.motd add constraint motd_action_valid
    check (action is null or action in ('share', 'link_discord'));

-- Flip the four non_discord community nudges to the new action.
update public.motd
   set action = 'link_discord'
 where audience = 'non_discord'
   and link_url like '%discord.gg%';
