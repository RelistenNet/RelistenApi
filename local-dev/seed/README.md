# Small development catalog

Normal local startup loads this sample when the database is empty. No AWS account,
backup download, or production credentials are needed. Existing databases are kept.

The checked-in sample contains five artists, eight shows per artist, at most two
recordings per show, and their tracks, sets, setlists, songs, venues, and tours.
It covers all seven Temporal providers:

| Artist | Providers |
| --- | --- |
| Phish | Phish.in and Phish.net |
| Grateful Dead | Archive and Jerry Garcia |
| Lotus | Archive and Phantasy Tour |
| Guerilla Toss | Archive and Setlist.fm |
| Trey Anastasio | Local |

`data.sql.gz` is about 175 KiB. It contains public catalog records only. User
accounts, authentication, favorites, listening history, and production job state
are excluded. Upstream reviews are public catalog content and are limited to ten
per recording. `manifest.json` records the export date, row counts, and checksum.
IDs and UUIDs are preserved so existing catalog links work.

This is a browsing and development sample. A full artist refresh can still import
that artist's entire upstream catalog; the small seed does not limit import work.
Use the local import controls described in [local development](../README.md).
Integration tests create their own data using the same `schema.sql`.

## Files and loading

- `schema.sql` is the generated, reviewable PostgreSQL catalog schema. It also
  creates empty Timescale playback tables and hourly/daily aggregates required by
  the API. No production playback data or Timescale job state is copied.
- `data.sql.gz` contains the selected records and SQL to rebuild derived counts
  and reset sequences. Year/show/source counts describe this sample.
- `analytics.sql` defines the empty local playback structures appended to the
  generated schema. Its two refresh policies belong to the local database user.
- `finalize.sql` supplies the aggregate and sequence SQL included in the data file.

For a new, empty database in the local Compose container:

```sh
./local-dev/seed/load-seed.sh CONTAINER_NAME
```

The optional second and third arguments select the database and user; defaults
are `relisten_db` and `relisten`. The loader refuses existing application tables.
`--if-empty` lets normal startup leave an existing database alone. Schema and data
load in one transaction, so an error does not leave a partial catalog.

Use [the full restore command](../../restore-local-database.sh) when you need a
larger production-shaped dataset. Loading this sample never replaces one.

## Reset the development catalog

Stop the local API, User Service, and ingestion worker first. These commands
**delete `relisten_db`** and replace it with the sample. Other databases remain
untouched. Ordinary startup never performs this reset.

```sh
container="$(docker compose -f local-dev/docker-compose.yml ps -q relisten-db)"
docker exec "$container" dropdb -U relisten --force relisten_db
docker exec "$container" createdb -U relisten --owner relisten --template template0 relisten_db
./local-dev/seed/load-seed.sh "$container"
```

## Refresh manually

Refresh only when the sample or schema needs an update. There is no scheduled
export or S3 dependency. The script requires Python 3 and PostgreSQL client tools
at least as new as the source server. It uses ordinary libpq connection variables
(`PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, and `PGSSLMODE`). Supply
credentials through your environment; do not put them in files or Git.

With those variables set for a read-only source:

```sh
python3 local-dev/seed/export-seed.py
```

The script selects the latest eight shows for each named artist, caps recordings
and reviews, and follows catalog relationships. The SQL table allowlist excludes
private schemas. It forces read-only sessions and limits each query to 30 seconds.
No full database backup is downloaded. Review the changed schema and manifest,
then load the result into a new scratch database and run API smoke checks before
committing. Exports larger than 5 MB are rejected.

`schema.sql` is regenerated from the source plus `analytics.sql`; edit the latter
for changes to empty local playback setup. `finalize.sql` changes take effect on
the next manual export. Gzip timestamps are fixed so identical exported data
produces identical compressed bytes.
