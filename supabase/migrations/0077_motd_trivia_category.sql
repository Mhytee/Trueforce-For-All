-- 0077: a `trivia` category for non-dated motoring / sim / engineering
-- "did you know" facts that aren't tied to a calendar day (onthisday), aren't
-- quotes (quote), and aren't about the plugin (feature). Vocabulary only; each
-- message still sets exactly one category. Idempotent.
alter table motd drop constraint if exists motd_category_valid;
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday','tip','joke',
                'reminder','vibes','milestone','feature','quote','support','share',
                'trivia','sponsored'));
    end if;
end $$;
