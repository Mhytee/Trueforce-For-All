-- Every improvement carries the next rung.
--
-- "Mhytee took 4.232s off Lake Akina downhill, now 2:15.700" is a result. Adding "next up: 2:11.053
-- by in0602" turns it into a ladder, which is the whole point of the mode this digest is reporting
-- on. It also answers the passing question without listing anybody: a run that overtakes six people
-- is still one line, and the only other name in it is the one still ahead.
--
-- The next target is the closest time FASTER than theirs on that board, across the merged field, so
-- it is a real target rather than the nearest tf4all user who may be minutes away.
do $$
declare d text; n int;
begin
    select pg_get_functiondef(p.oid) into d
    from pg_proc p join pg_namespace n2 on n2.oid = p.pronamespace
    where n2.nspname = 'public' and p.proname = 'get_arcade_week';
    if d is null then raise exception 'get_arcade_week not found'; end if;

    n := (length(d) - length(replace(d, '''gain_ms'', g.gain) order by g.gain desc)', '')))
         / length('''gain_ms'', g.gain) order by g.gain desc)');
    if n <> 1 then raise exception 'expected one gains aggregate, found %', n; end if;

    d := replace(d, '''gain_ms'', g.gain) order by g.gain desc)',
        '''gain_ms'', g.gain,' || chr(10) ||
        '                ''next_ms'', (select w.goal_ms from public.get_arcade_board_ranks_merged(p_game) w' || chr(10) ||
        '                              where w.course_id = g.course_id and w.direction = g.direction' || chr(10) ||
        '                                and w.goal_ms < g.goal_ms order by w.goal_ms desc limit 1),' || chr(10) ||
        '                ''next_author'', (select w.author from public.get_arcade_board_ranks_merged(p_game) w' || chr(10) ||
        '                              where w.course_id = g.course_id and w.direction = g.direction' || chr(10) ||
        '                                and w.goal_ms < g.goal_ms order by w.goal_ms desc limit 1))' || chr(10) ||
        '            order by g.gain desc)');
    execute d;
end $$;