-- A public bucket for the handful of images the project needs a URL for.
--
-- Discord embeds take a URL, not an upload, so a thumbnail has to be hosted somewhere. The
-- alternative was committing the image to the public GitHub repo and pointing at raw.github,
-- which works but puts a binary in a source tree and needs a push to change.
--
-- Public read, writes by service_role only. Nothing user-supplied ever lands here: it holds assets
-- the project ships, so there is no upload path for anybody else to reach.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('assets', 'assets', true, 2097152,
        array['image/png', 'image/jpeg', 'image/webp', 'image/gif'])
on conflict (id) do update
    set public = true,
        file_size_limit = excluded.file_size_limit,
        allowed_mime_types = excluded.allowed_mime_types;

-- Read for everyone, because that is the entire point of the bucket.
drop policy if exists "assets are publicly readable" on storage.objects;
create policy "assets are publicly readable"
    on storage.objects for select
    using (bucket_id = 'assets');

-- No insert, update or delete policy at all, so only service_role can write. A bucket that anybody
-- could upload to would be a free file host attached to the project's domain.