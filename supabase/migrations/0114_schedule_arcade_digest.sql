-- 0114: schedule the weekly arcade digest.
--
-- Sunday 18:00 UTC, once a week, matching what the message is: a week in review rather than a feed.
-- cron.schedule upserts by job name, so this is idempotent like 0046.
--
-- timeout_milliseconds := 30000, and this one is not cosmetic. pg_net defaults to 5000 ms, which
-- 0046 never overrides, so every job scheduled there records a timeout in net._http_response no
-- matter what the function did. The result is that cron.job_run_details reports 8358 successes for
-- role-sync while roughly one run in six is actually killed at the edge ceiling, and nobody could
-- tell the difference (see docs/discord-role-sync-slow.md, which is somebody else's ticket). The
-- digest is not going to inherit that blindness on the day it is created.
--
-- Only the new job is fixed here. Backfilling the other nine tf4all-* jobs belongs with the
-- investigation that found the problem, not smuggled into a leaderboard change.
--
-- The function itself is safe to schedule before the Discord secrets exist: with
-- DISCORD_ARCADE_CHANNEL_ID unset it logs a DRY-RUN and posts nothing, so this can run for weeks
-- without a single message reaching a channel.

select cron.schedule('tf4all-arcade-digest', '0 18 * * 0', $job$
  select net.http_post(
    -- ?op=post is REQUIRED, not decoration: the function defaults to preview so that a call with a
    -- missing or mistyped parameter renders the message instead of publishing it. The cron is the
    -- one caller that means to post, so the cron is the one caller that says so.
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/arcade-digest?op=post',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);
