-- 0071: two more MOTD categories.
--   vibes     - good-vibes / encouragement (distinct from joke = humor and
--               community = calls to action).
--   milestone - user-count celebrations (count-triggered, not date-based).
-- Vocabulary only; each message still sets exactly one category. Idempotent.

alter table motd drop constraint if exists motd_category_valid;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'motd_category_valid') then
        alter table motd add constraint motd_category_valid
            check (category in (
                'announcement','community','onthisday','holiday',
                'tip','joke','reminder','vibes','milestone','sponsored'));
    end if;
end $$;
