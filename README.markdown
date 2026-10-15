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
