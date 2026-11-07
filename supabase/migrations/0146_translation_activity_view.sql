-- 0146: who has been submitting, and whether they are filling or replacing
-- (owner, 2026-11-04).
--
-- A burst alert was the obvious thing to build here and it was the wrong thing.
-- Translating a language IS a burst: three thousand strings against a three hundred an
-- hour cap is a ten hour run, so any volume threshold below the cap fires on exactly
-- the contributor we most want to keep. By rate, a dedicated translator and a vandal
-- are the same event, and an alert that cries wolf on good work gets ignored, which
-- leaves us worse off than with no alert at all.
--
-- What separates them is not speed but what they land on. Someone translating a
-- language mostly fills keys nobody had done. Someone defacing one replaces text that
-- already had a translation, usually other people's. Same volume, opposite shape.
--
-- So this reports rather than warns, and it refuses nobody. Run it when a report comes
-- in, or out of curiosity. onto_existing counts keys that already had a row before
-- theirs; onto_others narrows that to keys where the earlier row was somebody else's,
-- which is the number worth reading first. A column of zeroes beside a large 'sent' is
-- a translator working through an empty language. A large onto_others is the shape to
-- look at, whatever the rate.
--
-- 'who' comes back in the form revert_translations takes, and the addresses with it,
-- so aiming a revert is a copy rather than a second query. That matters because the
-- window is usually what you know least: several ids behind one address is the case
-- p_since exists for, and it is visible here.
--
-- "Earlier" means a lower submitted_at, which is sound on the live path because every
-- submission is its own transaction and gets its own timestamp. It is NOT sound inside
-- one transaction: now() is frozen for its duration, so rows written by a rehearsal in
-- a single block all share a timestamp and none of them looks earlier than any other,
-- and both columns come back zero. A rehearsal has to backdate the first pass to get
-- the ordering the live path would have produced. This cost an hour to notice once.
--
-- Owner only through the service_role key, the MOTD posture (0067). Read only: it holds
-- no locks and writes nothing, so it is safe to run against production mid-incident.
set lock_timeout = '3s';
set statement_timeout = '60s';

create or replace function public.translation_activity(
    p_culture text        default null,
    p_since   timestamptz default null,
    p_hours   int         default 24)
returns jsonb language sql stable security definer
set search_path = public, extensions, pg_temp as $fn$
  with win as (
      select coalesce(p_since,
                      now() - make_interval(hours => greatest(1, coalesce(p_hours, 24))))
             as since
  ), mine as (
      select t.id, t.culture, t.key, t.submitted_at, t.status, t.source_ip_hash,
             coalesce(t.submitter::text, t.anon_id, 'unknown') as who
        from public.translations t, win
       where t.submitted_at >= win.since
         and (p_culture is null or t.culture = p_culture)
  ), marked as (
      select m.*,
             exists (select 1 from public.translations p
                      where p.culture = m.culture and p.key = m.key
                        and p.submitted_at < m.submitted_at) as onto_existing,
             exists (select 1 from public.translations p
                      where p.culture = m.culture and p.key = m.key
                        and p.submitted_at < m.submitted_at
                        and coalesce(p.submitter::text, p.anon_id, 'unknown') <> m.who)
             as onto_others
        from mine m
  ), grouped as (
      select culture, who,
             count(*)::int                                            as sent,
             count(distinct key)::int                                 as keys,
             count(distinct key) filter (where onto_existing)::int    as onto_existing,
             count(distinct key) filter (where onto_others)::int      as onto_others,
             count(*) filter (where status = 'approved')::int          as serving,
             min(submitted_at)                                        as first_at,
             max(submitted_at)                                        as last_at,
             coalesce((select jsonb_agg(distinct x.source_ip_hash)
                         from marked x
                        where x.culture = marked.culture and x.who = marked.who
                          and x.source_ip_hash is not null
                          and x.source_ip_hash <> 'redacted'), '[]'::jsonb)
                                                                      as addresses
        from marked
       group by culture, who
  )
  select coalesce(jsonb_agg(jsonb_build_object(
             'culture', culture, 'who', who,
             'sent', sent, 'keys', keys,
             'onto_existing', onto_existing, 'onto_others', onto_others,
             'serving', serving,
             'first_at', first_at, 'last_at', last_at,
             'addresses', addresses)
             order by onto_others desc, sent desc), '[]'::jsonb)
    from grouped;
$fn$;
revoke all on function public.translation_activity(text, timestamptz, int)
       from public, anon, authenticated;
grant execute on function public.translation_activity(text, timestamptz, int)
      to service_role;
