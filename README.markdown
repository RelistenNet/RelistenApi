# Local Development

Local development uses Docker Compose for PostgreSQL/TimescaleDB, PgBouncer,
Redis, and Adminer.

## Prerequisites

1. Docker with Compose v2
2. .NET 10 SDK

## Database Setup

Start the local services:

```sh
./start-local-databases.sh
```

Startup loads the checked-in development seed only when `relisten_db` is empty.
It preserves existing data and starts no imports. No AWS access or database
download is required.

See [local-dev/README.md](local-dev/README.md) for connection details, the
seed and explicit restore procedures, User Service startup, and manual ingestion.
Run `./test-local.sh` for tests against disposable databases on the same local
PostgreSQL and Redis services. Temporal test servers start automatically.

Adminer is available at [http://localhost:18080](http://localhost:18080).

Stop the containers without deleting their data:

```sh
./stop-local-databases.sh
```

## Code Setup

Run the API from the repository root:

```sh
dotnet run --project RelistenApi/RelistenApi.csproj
```

You can also open the repository in your C# editor and use its debugger.

Open the API Server at: [http://localhost:3823/api-docs](http://localhost:3823/api-docs)

## Ingestion and operations

`RelistenIngestionWorker` runs Temporal ingestion separately from the API.
Temporal records execution progress so imports can resume after worker restarts.
Hangfire still runs cache and popularity jobs in production.

Start with the [ingestion overview](docs/temporal/README.md) for architecture,
provider behavior, and production validation. Use the
[operations guide](docs/temporal/operations.md) for imports, schedules, and recovery,
or [local setup](local-dev/README.md#run-an-ingestion-worker-manually) for development commands.
Importing a seed artist can fetch its full upstream catalog; the seed does not
limit import work.

The API and standalone ingestion worker initialize Sentry independently when
`SENTRY_DSN` is configured. Scheduled-run monitoring is not enabled.
See [diagnostics and monitoring](docs/temporal/operations.md#diagnostics-and-monitoring)
for coverage and the remaining work.

## Formatting

Restore the repository-local CSharpier tool, then format the codebase:

```sh
dotnet tool restore
dotnet csharpier format .
```

Before every commit and push, check formatting without changing files:

```sh
dotnet csharpier check .
```

If the check fails, run `dotnet csharpier format .`, review the diff, and rerun
the check before committing or pushing.
