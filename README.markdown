# Local Development

Local development uses Docker Compose for PostgreSQL/TimescaleDB, PgBouncer,
Redis, and Adminer.

## Prerequisites

1. Docker with Compose v2
2. Visual Studio Code (with C# extension)
3. .NET 10 SDK

## Database Setup

Start the local services:

```sh
./start-local-databases.sh
```

Startup is non-destructive. It preserves `relisten_db` and ensures that the
`relisten_db`, `temporal`, and `temporal_visibility` databases exist under the
local `relisten` login. Restoring the catalog snapshot is a separate, explicit
command because it replaces `relisten_db`.

See [local-dev/README.md](local-dev/README.md) for connection details, the
guarded S3 restore procedure, User Service startup, and reset warnings.

Adminer is available at [http://localhost:18080](http://localhost:18080).

Stop the containers without deleting their data:

```sh
./stop-local-databases.sh
```

## Code Setup

1. Open this repo folder in Visual Studio Code
2. If prompted, restore the packages for the project
3. Debug > Start Debugging (F5) or Debug > Start without Debugging (shift+F5)

Open the API Server at: [http://localhost:3823/api-docs](http://localhost:3823/api-docs)
