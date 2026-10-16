# Local services

Run the API, User Service, and ingestion tests with the same PostgreSQL/TimescaleDB
and Redis containers. The checked-in [development seed](seed/README.md) supplies a
small catalog with representative artists, recordings, tracks, and setlists.
No AWS account or database download is required.

## Start and stop

From the repository root:

```sh
./start-local-databases.sh
```

The command starts PostgreSQL, PgBouncer, Redis, and Adminer. It loads the seed
only when `relisten_db` is empty. Existing catalog data and any other logical
databases are preserved. It does not start an importer or register schedules.
Running containers are reused across worktrees. To apply an intentional Compose
image or configuration change, run `docker compose -f local-dev/docker-compose.yml up -d`
from the checkout that owns your local data directory.

Stop the containers without deleting their data:

```sh
./stop-local-databases.sh
```

| Service | Address | Credentials |
| --- | --- | --- |
| PostgreSQL | `127.0.0.1:15432` | `relisten` / `local_dev_password` |
| PgBouncer | `127.0.0.1:16432` | `relisten` / `local_dev_password` |
| Redis | `127.0.0.1:16379` | none |
| Adminer | <http://localhost:18080> | PostgreSQL credentials above |

The application database is `relisten_db`. Tests create a separate database on
this PostgreSQL server and remove it when the test process finishes.

## Run tests

```sh
./test-local.sh
```

This starts or reuses PostgreSQL and Redis, builds the test project, and includes
the opt-in integration tests. It preserves running containers even when launched
from another worktree. Each test process gets a unique empty database, initialized
from the same schema as the development seed. Tests supply their own synthetic
catalog and upstream responses; they never import music-provider data.

Temporal tests start temporary local servers through the .NET SDK. The first
run may download the server binary. The tests stop those servers automatically;
no separately managed Temporal server or persistence databases are needed.

Pass ordinary `dotnet test` options through the script, for example:

```sh
./test-local.sh --filter FullyQualifiedName~TestIngestionPersistence
```

A plain `dotnet test RelistenApiTests` skips PostgreSQL/Redis integration tests
unless `RELISTEN_LOCAL_INGESTION_TESTS=1` is set. Tests for the separate tour
fixture require `RELISTEN_TOUR_TEST_DATABASE_URL` pointing to a disposable local
database. See [the tour fixture](../RelistenApiTests/Controllers/TestToursController.cs)
for its setup and cleanup behavior.

## Replace the local catalog explicitly

Startup never replaces an existing catalog. To reset it to the checked-in seed,
use the explicit reset command in [the seed guide](seed/README.md).

For investigations that need a larger snapshot, the existing restore command
accepts a PostgreSQL custom-format backup downloaded separately:

```sh
./restore-local-database.sh --dry-run /path/to/app.dump
./restore-local-database.sh /path/to/app.dump
```

Restoring force-drops `relisten_db` and terminates its connections. Stop local API,
User Service, and ingestion worker processes first. Other databases are preserved.
The restore uses the pinned PostgreSQL 17/TimescaleDB 2.28.2 image, restores without
production owners or grants, normalizes Timescale policy ownership, and refreshes
planner statistics. See `./restore-local-database.sh --help` for reset options.

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

## Run an ingestion worker manually

The development API does not start Hangfire workers. Ordinary worker startup
creates no schedules, and the seed contains no queued imports. Importing a whole
artist can fetch far more shows than the seed contains; keep live imports explicit.

Install the [Temporal CLI](https://docs.temporal.io/cli/setup-cli), then start a
local server in its own terminal:

```sh
temporal server start-dev
```

The CLI starts an in-memory server at `127.0.0.1:7233` and its UI at
<http://localhost:8233>. Stopping it discards its workflow history.

In another terminal, point the worker at the normal development catalog:

```sh
export DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db
export REDIS_URL=redis://127.0.0.1:16379
export Temporal__Address=127.0.0.1:7233
export Temporal__Namespace=default
dotnet run --project RelistenIngestionWorker
```

Use the same variables for operator commands in another terminal. For a bounded
live import, use an Archive item identifier:

```sh
dotnet run --project RelistenIngestionWorker -- \
  --import ARTIST_SLUG --provider archive.org --item ARCHIVE_IDENTIFIER
```

Only Archive supports a single-item command. Archive and Phish.net accept an
exact year; Phish.in interprets the year as a lower bound. The other providers
refresh the whole artist. Always name the provider when using a year filter.
The synthetic tests cover all seven providers without these live fetches.

Do not register recurring schedules as part of local setup. The explicit
`--register-schedules` command creates active full-catalog and discovery schedules.
A worker also resumes any previously started workflows on its namespace.
See [ingestion operations](../docs/temporal/operations.md) for operator commands.

Redis stores oversized Temporal payloads as well as API caches. Keep Redis while
manual workflow histories matter; clearing its data can make those histories
unreadable. The temporary test servers have no history to retain after teardown.
