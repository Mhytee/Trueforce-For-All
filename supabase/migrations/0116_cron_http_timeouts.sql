-- 0116: give the tf4all-* cron jobs a real net.http_post timeout, so the health signal stops
-- lying. This is the backfill 0114 deferred to whoever investigated docs/discord-role-sync-slow.md.
--
-- pg_net defaults to 5000 ms. None of the jobs scheduled in 0046, 0047 or 0048 override it, so
-- every one of their responses is recorded in net._http_response as "Timeout of 5000 ms reached"
-- no matter what the function actually did, and the body is thrown away with it. That is why
-- cron.job_run_details could report 8358 clean runs of tf4all-role-sync while every single sweep
-- was taking about 142 seconds and silently abandoning roughly 8 of its 143 Discord writes on a
-- 429. cron.job_run_details only ever measured that net.http_post queued the request.
--
-- 30000 matches 0114. It is deliberately well under the edge function wall-clock ceiling of about
-- 150 s and is NOT meant to be raised to reach it: the pg_net worker holds a Postgres transaction
-- open until the slowest request in its batch resolves, so a 150 s timeout would pin the worker
-- four times an hour and delay everything queued behind it, including the moderation cards that
-- notify_report_flag and notify_appeal fire. A job that cannot answer in 30 s wants fixing, not a
-- longer rope.
--
-- Idempotent: cron.schedule upserts on (jobname, username), same as 0046. Apply as postgres,
-- which owns all eleven existing jobs. Applying as another role would not match those rows and
-- would create a SECOND job of the same name.
--
-- ORDER: deploy the new discord-role-sync BEFORE applying this. Against the old function a sweep
-- still takes about 142 s, so raising its timeout from 5 s to 30 s only makes the pg_net worker
-- hold each request six times longer, four times an hour, for no signal in return.
--
-- Deliberately NOT touched here:
--   tf4all-arcade-digest      already sets timeout_milliseconds := 30000 (0114).
--   tf4all-report-card-sweep  its command is a nested dollar-quoted do block wrapping two
--                             http_post calls and a Vault read. Retyping 33 lines to change one
--                             argument is the worst transcription risk on offer, and a slip
--                             breaks the moderation backstop quietly. It also has nothing
--                             measured to protect: net.http_post only queues, so the job's own
--                             duration says nothing about the functions it calls, and its loops
--                             fire only for a report or appeal whose card FAILED to post, of
--                             which there are none, so it has produced no net._http_response
--                             rows at all. When it does fire it inherits the same 5 s default.
--                             If it is ever worth doing, lift the body into a SECURITY DEFINER
--                             function first and schedule that.
--   tf4all-ban-reaper, tf4all-motd-milestones, tf4all-motd-stats,
--   gc_superseded_car_fact_submissions
--                             plain SQL, no net.http_post, nothing to time out.
--
-- The commands below are otherwise byte-identical to the ones in 0046, 0047 and 0048.

select cron.schedule('tf4all-role-sync', '*/15 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/discord-role-sync?op=sync',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);

select cron.schedule('tf4all-entitlement-sync', '*/30 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/discord-role-sync?op=entitlements',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);

select cron.schedule('tf4all-backup-gc', '30 3 * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/backup-retention-gc',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);

select cron.schedule('tf4all-backup-warn', '0 9 * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/backup-retention-warn',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);

select cron.schedule('tf4all-patreon-supporters', '0 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/patreon-supporters-sync?op=sync',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);
