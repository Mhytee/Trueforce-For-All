-- Re-schedules the weekly digest with ?op=post. The function now defaults to preview, so a call
-- with a missing or mistyped parameter renders the message instead of publishing it to a public
-- channel. The cron is the one caller that means to post, so the cron is the one caller that says
-- so. cron.schedule upserts by job name, so this replaces the job created moments ago.

select cron.schedule('tf4all-arcade-digest', '0 18 * * 0', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/arcade-digest?op=post',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 30000);
$job$);