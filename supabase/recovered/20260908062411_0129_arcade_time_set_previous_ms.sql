-- Record what the time BEAT, not just what it was.
--
-- submit_arcade_lap_time already has the old time in scope as v_existing, compares against it, and
-- then throws it away. Without it the weekly digest can only report records changing hands, which
-- on a small server is most weeks silent, while the thing people actually do, taking a few seconds
-- off their own time, produces nothing to say.
--
-- Patched in place rather than retyped. The function is 150 lines of working validation and
-- transcribing it to change two would be a good way to introduce a bug in the part that matters.
-- Both replacement targets are unique to the time_set insert: the crown inserts end
-- "victim_name, goal_ms, previous_ms)" and "p_goal_ms, v_cc_ms)" respectively.
do $$
declare d text; n int;
begin
    select pg_get_functiondef(p.oid) into d
    from pg_proc p join pg_namespace n2 on n2.oid = p.pronamespace
    where n2.nspname = 'public' and p.proname = 'submit_arcade_lap_time';
    if d is null then raise exception 'submit_arcade_lap_time not found'; end if;

    -- Refuse rather than half-apply if the shape is not what we expect.
    n := (length(d) - length(replace(d, 'actor_name, goal_ms)', ''))) / length('actor_name, goal_ms)');
    if n <> 1 then raise exception 'expected exactly one time_set column list, found %', n; end if;
    n := (length(d) - length(replace(d, 'v_author, p_goal_ms);', ''))) / length('v_author, p_goal_ms);');
    if n <> 1 then raise exception 'expected exactly one time_set values list, found %', n; end if;

    d := replace(d, 'actor_name, goal_ms)', 'actor_name, goal_ms, previous_ms)');
    d := replace(d, 'v_author, p_goal_ms);', 'v_author, p_goal_ms, v_existing);');
    execute d;
end $$;