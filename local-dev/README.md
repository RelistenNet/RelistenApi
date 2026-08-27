# Local services

Local development uses one TimescaleDB/PostgreSQL container and one login role,
`relisten`. The container owns three logical databases:

| Database | Purpose |
| --- | --- |
| `relisten_db` | Catalog, identity, and user data |
| `temporal` | Reserved for Temporal persistence |
| `temporal_visibility` | Reserved for Temporal visibility |

Temporal itself is not part of the normal Compose stack. Authentication and
favorites do not need it.

## Start and stop

Start PostgreSQL, PgBouncer, Redis, and Adminer:

```sh
./start-local-databases.sh
```

This command is non-destructive. It never downloads a backup or replaces
`relisten_db`. It creates a missing logical database only after PostgreSQL is
healthy and verifies that `relisten` is the only non-system login role.

Stop the local containers without deleting their data:

```sh
./stop-local-databases.sh
```

Connection details:

| Service | Address | Credentials |
| --- | --- | --- |
| PostgreSQL | `127.0.0.1:15432` | `relisten` / `local_dev_password` |
| PgBouncer | `127.0.0.1:16432` | `relisten` / `local_dev_password` |
| Redis | `127.0.0.1:16379` | none |
| Adminer | <http://localhost:18080> | PostgreSQL credentials above |

## Restore the catalog snapshot

Restoring is deliberately separate from startup because it force-drops
`relisten_db` and terminates connections to that database. It preserves the two
Temporal databases. Stop locally running catalog and User Service processes
before restoring so they cannot reconnect while the database is being replaced.

Download the current slim custom-format archive to an ignored directory. Use a
partial filename so an interrupted download cannot be mistaken for a valid
archive:

```sh
mkdir -p local-dev/backups
aws s3 cp \
  s3://relistenapi-db/relisten-db-slim-backup/app_2026-07-18T08:00:00Z.dump \
  local-dev/backups/app_2026-07-18T08:00:00Z.dump.partial
mv \
  local-dev/backups/app_2026-07-18T08:00:00Z.dump.partial \
  local-dev/backups/app_2026-07-18T08:00:00Z.dump
```

Inspect the plan, then restore:

```sh
./restore-local-database.sh --dry-run \
  local-dev/backups/app_2026-07-18T08:00:00Z.dump
./restore-local-database.sh \
  local-dev/backups/app_2026-07-18T08:00:00Z.dump
```

The script checks the archive before dropping anything, requires the pinned
PostgreSQL 17/TimescaleDB 2.28.2 image, enforces a conservative disk-space
margin, runs TimescaleDB's pre/post-restore hooks, restores without production
owners or grants, and analyzes the result.

TimescaleDB stores the owners of its scheduled policies inside extension data.
The production archive names `app`, even when `pg_restore --no-owner` is used.
The restore creates that role temporarily as `NOLOGIN`, verifies the three known
policy definitions, recreates them through TimescaleDB's public APIs under
`relisten`, and removes the temporary role. Application code always connects as
`relisten`; the production role graph is not reproduced locally.

If the existing PostgreSQL cluster cannot start with the pinned image, the
restore stops without deleting it. `--reset-local-cluster` is the explicit
escape hatch: it deletes the entire local PostgreSQL data directory, including
both Temporal databases, regardless of the underlying startup failure. Inspect
the container logs first and do not use it when local workflow state matters.

The snapshot contains dormant Hasura metadata and event rows. Local Compose does
not run Hasura. Do not point a Hasura instance at the restored database without
first reviewing pending events, or old work could be replayed.

After a successful restore, delete the downloaded archive when disk space is
tight; it is about 6.26 GiB.

## Run the .NET services

Run the anonymous catalog API in one terminal:

```sh
dotnet run --project RelistenApi/RelistenApi.csproj
```

Run the User Service in another terminal:

```sh
dotnet run --project RelistenUserService/RelistenUserService.csproj
```

The catalog API listens on `http://localhost:3823`. The development User Service
listens on `https://127.0.0.1:5443` and serves the exact
`auth.relisten.localhost:5443` and `accounts.relisten.localhost:5443` hosts. Run
`pnpm setup:browser-session` once from the sibling `relisten-web` checkout before
starting the User Service. That command creates and trusts the local certificate
outside Git and writes its paths through .NET Secret Manager.

Development startup applies the checked-in identity and user-data migrations,
seeds the local clients, and offers fixed Apple and Google personas. The
personas exercise the real authorization-code, PKCE, token, session, account,
and favorites paths without contributor-owned provider secrets. Development
signing and encryption keys persist under the ignored
`RelistenUserService/.local-openiddict` directory, so ordinary issuer restarts
do not invalidate local credentials. Development personas and runtime
migrations remain disabled outside `Development`.

With the User Service running, check its account/session security invariants:

```sh
RelistenUserService/SmokeTests/refresh-token-replay.sh
```
