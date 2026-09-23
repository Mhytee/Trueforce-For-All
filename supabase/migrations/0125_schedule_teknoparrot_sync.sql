-- Once a day, at 04:10 UTC.
--
-- Daily on purpose. The plugin already caches their page for 24 hours on every machine that runs
-- it, so one server request a day does not increase what we ask of teknoparrot.com; it just puts
-- the copy somewhere the ranking can reach. 04:10 sits away from the 05:30 role sync and the
-- Sunday 18:00 digest so the three do not stack.
--
-- The function refuses to write when the page parses to nothing, so a login wall, an error page or
-- a redesign leaves yesterday's copy in place rather than emptying the table.
select cron.unschedule('tf4all-teknoparrot-sync')
where exists (select 1 from cron.job where jobname = 'tf4all-teknoparrot-sync');

select cron.schedule('tf4all-teknoparrot-sync', '10 4 * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/arcade-teknoparrot-sync?op=sync',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb,
    timeout_milliseconds := 120000);
$job$);
