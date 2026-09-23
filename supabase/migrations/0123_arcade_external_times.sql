-- A cached copy of somebody else's leaderboard, so a standing can be worked out against it.
--
-- The plugin has fetched and parsed TeknoParrot's board for a while, but only into memory on one
-- machine, so the server has never been able to answer "where do I stand against the real
-- cabinets". This is that page, kept server side.
--
-- SHAPED LIKE arcade_lap_times ON PURPOSE, one row per player per car per board, so the two can be
-- ranked as one field without a translation step in the middle. car_text stays FREE TEXT rather
-- than a car id: mapping their names onto CarIDs takes a 154-line matcher that already exists in
-- the plugin, and porting it would be a second copy of exactly the logic most likely to drift. A
-- course standing does not need it. If per-car boards ever want it, the raw text is still here.
--
-- NOT A CLAIM ABOUT ANYONE. These are scraped from a public page and nobody in them signed up to
-- be ranked by us. They are stored to answer a question for our own users, never to be presented
-- as our records, and the source column says whose they are.
create table if not exists public.arcade_external_times (
    id           bigint generated always as identity primary key,
    source       text        not null,
    game         text        not null,
    course_id    smallint    not null,
    direction    smallint    not null,
    player_name  text        not null,
    car_text     text        not null default '',
    goal_ms      integer     not null,
    set_at       timestamptz,
    external_id  text,
    fetched_at   timestamptz not null default now(),
    constraint arcade_external_course  check (course_id >= 0 and course_id <= 15),
    constraint arcade_external_dir     check (direction >= 0 and direction <= 1),
    constraint arcade_external_time    check (goal_ms > 0 and goal_ms < 360000)
);

create unique index if not exists arcade_external_unique
    on public.arcade_external_times (source, game, course_id, direction, car_text, player_name);

create index if not exists arcade_external_board
    on public.arcade_external_times (game, course_id, direction, goal_ms);

-- Locked down completely. Every read goes through a security-definer function, so no client ever
-- selects from this directly and a scrape of ours cannot become a scrape of theirs.
alter table public.arcade_external_times enable row level security;
revoke all on public.arcade_external_times from anon, authenticated;
