-- 0075: a `support` category for gentle Patreon / donation nudges, kept
-- separate from `community` so they can be toggled as their own group later.
-- Idempotent.

alter table motd drop constraint if exists motd_category_valid;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday','tip','joke',
                'reminder','vibes','milestone','feature','quote','support','sponsored'));
    end if;
end $$;
