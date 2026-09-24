# Recovered migrations

These 28 migrations are applied to production and existed in no branch as
files. The SQL was recovered verbatim from
`supabase_migrations.schema_migrations` on 2026-09-24 and every file was
md5-verified against the server before being committed.

Filenames keep the applied form, `<remote version>_<applied name>.sql`, so a
row in the ledger can be found here by either field.

## These are a record, not a replay set

Do not run these files. Several are `pg_get_functiondef` source patches: they
read the live function body, splice text into it, and write it back, so their
result depended on what the function looked like at the moment they ran.

- `0129`, `0134` and `0135` raise "anchor not found" against any other
  database, because the text they search for is gone once they have run.
- `0133` is worse. Its replacement re-contains its own anchor, so a second run
  matches again and inserts a duplicate `world_records` key rather than
  failing. It would not error, it would quietly produce a wrong function.

`0136_public_assets_bucket` is the exception and is genuinely idempotent.

## Nine of these exist nowhere else

`0128`, `0129`, `0130`, `0131`, `0132`, `0133`, `0134`, `0135` (all arcade) and
`0136_public_assets_bucket` have no equivalent in `../migrations/`. Before this
directory existed, the only copy of their SQL was a single row in a production
table. The rest were folded into settled-form files in `../migrations/`, which
is why their names do not match: a file there is the replayable end state,
while a row in the ledger is one step that got there.

`embed_seed_pack_bodies` is a one-shot DATA migration rather than schema. It
rewrote the three reference-only seed packs and left the backup table
`packs_body_backup_seed_embed` behind. Do not drop that table.

## Why the local and remote lists will never match

Local migrations are numbered `NNNN_name.sql` and applied in filename order.
The remote ledger is keyed by timestamp, and 28 of its 157 entries have no
local file. `supabase migration list` therefore shows every local file as
missing remotely and every remote row as missing locally. That is expected and
is not drift.

The arcade work also grew a second numbering series that collides with the
main one, so `0132` means `telemetry_tuning_views` locally and
`arcade_unclaimed_boards` in the ledger.

## Never run migration repair here

`supabase migration repair --status reverted` is the command the CLI offers,
pre-filled with every version, whenever it notices the mismatch above. Its
implementation is:

```sql
DELETE FROM supabase_migrations.schema_migrations WHERE version = ANY($1)
```

The `rollback` column is null on all 157 rows, so there is no undo. Running it
would delete the ledger, and before this directory existed that would have
destroyed the only copy of nine migrations. A full archive also lives outside
the repo at `supabase-history-2026-09-24`.

`supabase db push` and `supabase db pull` are also offered in that error text.
Push refuses to start here, which is why nothing has gone wrong so far, and
pull would generate a large diff migration and deepen the two series.

To apply a schema change, commit the numbered file in `../migrations/` and run:

```
supabase db query --linked -f supabase/migrations/NNNN_name.sql
```
