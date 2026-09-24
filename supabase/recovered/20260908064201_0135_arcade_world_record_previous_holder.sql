-- Who held the world record before it fell.
--
-- "X set a new world record" is a fact; "X took the world record from Y, 1.250s faster" is the
-- same sentence the crown lines use, and the owner is right that the "beat" form belongs on a
-- record changing hands rather than on ordinary overtaking.
--
-- Derived rather than stored. The previous holder is whoever is second on that board now, but ONLY
-- if their time predates the new one: if the second-place time is newer, they never held it and
-- naming them would invent a defeat that did not happen. A null set_at on the runner-up is treated
-- as "cannot show it predates", so they are named only when the evidence is there.
do $$
declare d text; n int;
begin
    select pg_get_functiondef(p.oid) into d
    from pg_proc p join pg_namespace n2 on n2.oid = p.pronamespace
    where n2.nspname = 'public' and p.proname = 'get_arcade_week';
    if d is null then raise exception 'get_arcade_week not found'; end if;

    n := (length(d) - length(replace(d, '''is_tf4all'', wr.is_tf4all) order by wr.set_at)', '')))
         / length('''is_tf4all'', wr.is_tf4all) order by wr.set_at)');
    if n <> 1 then raise exception 'expected one world_records aggregate, found %', n; end if;

    d := replace(d, '''is_tf4all'', wr.is_tf4all) order by wr.set_at)',
        '''is_tf4all'', wr.is_tf4all,' || chr(10) ||
        '                ''prev_author'', (select w2.author from public.get_arcade_board_ranks_merged(p_game) w2' || chr(10) ||
        '                                   where w2.course_id = wr.course_id and w2.direction = wr.direction' || chr(10) ||
        '                                     and w2.rank = 2 and w2.set_at is not null' || chr(10) ||
        '                                     and w2.set_at < wr.set_at limit 1),' || chr(10) ||
        '                ''prev_ms'', (select w2.goal_ms from public.get_arcade_board_ranks_merged(p_game) w2' || chr(10) ||
        '                               where w2.course_id = wr.course_id and w2.direction = wr.direction' || chr(10) ||
        '                                 and w2.rank = 2 and w2.set_at is not null' || chr(10) ||
        '                                 and w2.set_at < wr.set_at limit 1))' || chr(10) ||
        '            order by wr.set_at)');
    execute d;
end $$;