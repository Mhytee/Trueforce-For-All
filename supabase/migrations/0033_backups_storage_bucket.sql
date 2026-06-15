-- 0033: private per-user backup storage for the paid backup/sync feature
-- (Phase 2, milestone M2). Creates the `backups` bucket and owner-scoped RLS on
-- storage.objects so each authenticated user can read/write ONLY objects under
-- their own '<user_id>/' prefix (object key = '<auth.uid()>/setup.json').
--
-- Ungated at this stage: any signed-in user can back up, so the end-to-end flow
-- can be proven before entitlements exist. The is_supporter gate on UPLOADS (and
-- the 2-year retention GC) lands in the entitlements migration (M3); RESTORE stays
-- allowed during the retention window even when a subscription has lapsed.
--
-- Idempotent per repo convention (re-runnable).

-- 1. Bucket: private, size-capped, JSON/binary payloads only.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values (
  'backups', 'backups', false, 52428800,  -- 50 MB ceiling (library guard caps ~25 MB)
  array['application/json', 'application/octet-stream', 'application/gzip']
)
on conflict (id) do update
  set public             = excluded.public,
      file_size_limit    = excluded.file_size_limit,
      allowed_mime_types = excluded.allowed_mime_types;

-- 2. Owner-scoped RLS on storage.objects. No policy is granted to `anon`, so the
--    public anon key (shipped in the plugin) can touch nothing here; only a JWT
--    whose uid matches the object's first path segment passes.
drop policy if exists "backups_read_own"   on storage.objects;
drop policy if exists "backups_insert_own" on storage.objects;
drop policy if exists "backups_update_own" on storage.objects;
drop policy if exists "backups_delete_own" on storage.objects;

create policy "backups_read_own" on storage.objects
  for select to authenticated
  using (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text);

create policy "backups_insert_own" on storage.objects
  for insert to authenticated
  with check (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text);

create policy "backups_update_own" on storage.objects
  for update to authenticated
  using      (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text)
  with check (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text);

create policy "backups_delete_own" on storage.objects
  for delete to authenticated
  using (bucket_id = 'backups' and (storage.foldername(name))[1] = auth.uid()::text);
