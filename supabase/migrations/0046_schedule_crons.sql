-- 0046: schedule the maintenance crons (pg_cron + pg_net). Each calls an Edge Function with
-- the service_role key read from Vault (secret name 'service_role_key', stored out-of-band so
-- the key never lives in this migration / repo). Idempotent: cron.schedule upserts by job name.
--
--   tf4all-role-sync        every 15 min  -> discord-role-sync op=sync (all-members backstop:
--                                            percentile recompute + stale-role cleanup; the
--                                            plugin already does instant per-user syncs)
--   tf4all-entitlement-sync every 30 min  -> discord-role-sync op=entitlements (supporter
--                                            Discord role -> is_supporter via set_supporter)
--   tf4all-backup-gc        nightly 03:30 -> backup-retention-gc (delete expired backups)

select cron.schedule('tf4all-role-sync', '*/15 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/discord-role-sync?op=sync',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb);
$job$);

select cron.schedule('tf4all-entitlement-sync', '*/30 * * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/discord-role-sync?op=entitlements',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb);
$job$);

select cron.schedule('tf4all-backup-gc', '30 3 * * *', $job$
  select net.http_post(
    url := 'https://dvttzzjbktelcikvyzmt.supabase.co/functions/v1/backup-retention-gc',
    headers := jsonb_build_object(
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'service_role_key'),
      'Content-Type', 'application/json'),
    body := '{}'::jsonb);
$job$);
