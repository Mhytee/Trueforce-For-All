drop function if exists public.get_arcade_board_ranks_merged(text);

create or replace function public.get_arcade_board_ranks_merged(p_game text)
returns table (who text, author text, is_tf4all boolean,
               course_id smallint, direction smallint,
               goal_ms integer, players integer, rank integer, set_at timestamptz)
language sql stable security definer
set search_path to 'public', 'extensions', 'pg_temp'
as $function$
    with ours as (
        select 'u:' || t.user_id::text as who, t.author, true as is_tf4all,
               t.course_id, t.direction, t.goal_ms, t.updated_at
        from public.arcade_lap_times t
        where t.game = p_game
          and not exists (select 1 from public.submitter_blocked b
                           where b.submitter_id = t.user_id::text
                             and (b.banned_until is null or b.banned_until > now()))
    ),
    theirs as (
        select 'x:' || e.player_name as who, e.player_name as author, false as is_tf4all,
               e.course_id, e.direction, e.goal_ms, e.set_at as updated_at
        from public.arcade_external_times e
        where e.game = p_game
    ),
    live as (select * from ours union all select * from theirs),
    course_best as (
        select distinct on (l.course_id, l.direction, l.who)
               l.course_id, l.direction, l.who, l.author, l.is_tf4all, l.goal_ms, l.updated_at
        from live l
        order by l.course_id, l.direction, l.who, l.goal_ms asc, l.updated_at asc nulls last
    ),
    field as (
        select course_id, direction, count(*)::integer as players
        from course_best group by 1, 2
    )
    select cb.who, cb.author, cb.is_tf4all, cb.course_id, cb.direction, cb.goal_ms, f.players,
           (row_number() over (partition by cb.course_id, cb.direction
                               order by cb.goal_ms asc, cb.updated_at asc nulls last))::integer,
           cb.updated_at
    from course_best cb
    join field f on f.course_id = cb.course_id and f.direction = cb.direction;
$function$;

revoke all on function public.get_arcade_board_ranks_merged(text) from anon, public;
grant execute on function public.get_arcade_board_ranks_merged(text) to authenticated;

do $$
declare d text; n int;
begin
    select pg_get_functiondef(p.oid) into d
    from pg_proc p join pg_namespace n2 on n2.oid = p.pronamespace
    where n2.nspname = 'public' and p.proname = 'get_arcade_week';
    if d is null then raise exception 'get_arcade_week not found'; end if;

    n := (length(d) - length(replace(d, '''unclaimed'', public.get_arcade_unclaimed(p_game),', '')))
         / length('''unclaimed'', public.get_arcade_unclaimed(p_game),');
    if n <> 1 then raise exception 'expected one unclaimed key, found %', n; end if;

    d := replace(d, '''unclaimed'', public.get_arcade_unclaimed(p_game),',
        '''world_records'', coalesce((select jsonb_agg(jsonb_build_object(' || chr(10) ||
        '                ''author'', wr.author, ''course_id'', wr.course_id,' || chr(10) ||
        '                ''direction'', wr.direction, ''goal_ms'', wr.goal_ms,' || chr(10) ||
        '                ''is_tf4all'', wr.is_tf4all) order by wr.set_at)' || chr(10) ||
        '            from public.get_arcade_board_ranks_merged(p_game) wr, win' || chr(10) ||
        '            where wr.rank = 1 and wr.set_at is not null and wr.set_at >= win.since),' || chr(10) ||
        '            ''[]''::jsonb),' || chr(10) ||
        '        ''unclaimed'', public.get_arcade_unclaimed(p_game),');
    execute d;
end $$;