-- Rebuild the reference-only seed packs (FH6 Starter, AC Essentials,
-- Multi-Game Universal Starter) into the embedded body shape the plugin
-- expects (snapshot/override/engine-def per entry). The seed packs stored
-- only {id,name} pointers, so preview drill-in and download/import both
-- failed. References are resolved from game_presets/presets/custom_engines.
-- Guarded to only touch reference-only packs (top-level 'presets' key, or
-- entries carrying a raw 'id'); embedded packs use 'car_presets' + 'source_id'
-- and are left untouched. Backup: table packs_body_backup_seed_embed.
update packs p
set
  body        = coalesce(sub.new_body, p.body),
  entry_count = sub.new_count
from (
  select pk.id,
    (select jsonb_object_agg(k, v)
       from jsonb_each(jsonb_build_object(
         'game_presets',   gp.arr,
         'car_presets',    cp.arr,
         'custom_engines', ce.arr
       )) j(k, v)
       where jsonb_typeof(v) <> 'null') as new_body,
    coalesce(jsonb_array_length(gp.arr), 0)
      + coalesce(jsonb_array_length(cp.arr), 0)
      + coalesce(jsonb_array_length(ce.arr), 0) as new_count
  from packs pk
  left join lateral (
    select jsonb_agg(jsonb_build_object(
      'name',           el->>'name',
      'snapshot',       g.body->'snapshot',
      'source_id',      el->>'id',
      'source_author',  g.author,
      'allow_in_packs', coalesce(g.allow_in_packs, true)
    )) as arr
    from jsonb_array_elements(coalesce(pk.body->'game_presets', '[]'::jsonb)) el
    join game_presets g on g.id = (el->>'id')::uuid
  ) gp on true
  left join lateral (
    select jsonb_agg(jsonb_build_object(
      'preset_name',    el->>'name',
      'car_id',         el->>'car_id',
      'game_name',      el->>'game',
      'override',       c.body->'override',
      'source_id',      el->>'id',
      'source_author',  c.author,
      'allow_in_packs', coalesce(c.allow_in_packs, true)
    )) as arr
    from jsonb_array_elements(coalesce(pk.body->'presets', '[]'::jsonb)) el
    join presets c on c.id = (el->>'id')::uuid
  ) cp on true
  left join lateral (
    select jsonb_agg(
      ce0.body || jsonb_build_object('source_id', el->>'id', 'allow_in_packs', coalesce(ce0.allow_in_packs, true))
    ) as arr
    from jsonb_array_elements(coalesce(pk.body->'custom_engines', '[]'::jsonb)) el
    join custom_engines ce0 on ce0.id = (el->>'id')::uuid
  ) ce on true
  where (pk.body ? 'presets')
     or (pk.body->'game_presets'->0 ? 'id')
     or (pk.body->'custom_engines'->0 ? 'id')
) sub
where p.id = sub.id
  and sub.new_body is not null;