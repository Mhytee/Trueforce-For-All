-- 0025: cap uploaded body JSON size to stop storage/bandwidth abuse.
-- The upload/update RPCs validate that body is a JSON object but never bound
-- its size, so a single user could push multi-MB rows that cost storage on
-- write and bandwidth on every download. A CHECK on length(body::text) (an
-- immutable expression) enforces a ceiling on every write path at once -
-- uploads, edits, and any future path - without touching the RPCs. Caps are
-- deliberately generous; oversized content should be split into a separate
-- pack rather than bloating one row.

alter table public.presets        drop constraint if exists presets_body_size_chk;
alter table public.presets        add  constraint presets_body_size_chk
    check (length(body::text) <= 131072);   -- 128 KB

alter table public.custom_engines  drop constraint if exists custom_engines_body_size_chk;
alter table public.custom_engines  add  constraint custom_engines_body_size_chk
    check (length(body::text) <= 131072);   -- 128 KB

alter table public.game_presets    drop constraint if exists game_presets_body_size_chk;
alter table public.game_presets    add  constraint game_presets_body_size_chk
    check (length(body::text) <= 262144);   -- 256 KB

alter table public.packs           drop constraint if exists packs_body_size_chk;
alter table public.packs           add  constraint packs_body_size_chk
    check (length(body::text) <= 2097152);  -- 2 MB
