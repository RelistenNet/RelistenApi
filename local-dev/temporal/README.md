# Run ingestion locally

This stack owns its own Docker project, volume and ports. It does not use the existing development database. PostgreSQL contains an empty application catalog plus separate Temporal persistence and visibility databases. Tests serve synthetic upstream responses on loopback; they do not contact music providers.

Requirements: Docker and .NET 10. Run commands from the repository root.

```sh
docker compose -f local-dev/temporal/docker-compose.yml up -d
export DATABASE_URL=postgresql://relisten:local_ingestion@127.0.0.1:15433/relisten_ingestion
export REDIS_URL=redis://127.0.0.1:16380
export Temporal__Address=127.0.0.1:17233
export Temporal__Namespace=default
Ingestion__LocalSchemaBaseline=12 dotnet run --project RelistenIngestionWorker -- --migrate
RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test RelistenApiTests
```

The explicit baseline applies only to the supplied empty schema fixture. Historical migrations include markers for manual schema work and cannot recreate the current catalog from scratch. `init/01-catalog.sql` is a schema-only export from the read-only replica; it contains no production rows or credentials. Existing installations must retain their migration history. The latest catalog migration is 12. The undeployed ingestion migration 13 and its snapshot/receipt tables have been removed. Reset this disposable stack before setup if it contains an earlier ingestion draft or has migration 13 recorded; do not apply that reset to an existing catalog installation.

[Temporal UI](http://127.0.0.1:18233). Application PostgreSQL: port 15433. Redis: 16380. Temporal gRPC: 17233. The local stack uses server/auto-setup 1.29.1, UI 2.34.0 and PostgreSQL 17.10. These are tested local pins, not a recommendation to copy auto-setup or local credentials into production.

Redis holds catalog cache entries and oversized Temporal payload bytes. The codec retains those bytes for 15 days after each encode or reuse. Keep the test namespace's closed-history retention at most seven days. Resetting Redis independently can make existing workflow histories unreadable; reset the disposable stack together when a clean start is needed.

Tests create fixture artists in this dedicated database and use a fresh Temporal namespace for each integration suite, so old local schedules cannot poll the fixture workers. Workflow unit tests start an additional ephemeral Temporal development server through the .NET SDK. The first run may download that binary. Without `RELISTEN_LOCAL_INGESTION_TESTS=1`, the PostgreSQL/Compose integration fixture is skipped.

For manual imports from real upstreams, start the host:

```sh
dotnet run --project RelistenIngestionWorker
```

Tests register their own HTTP transport to route requests to a fixture server inside the test process. The standalone worker contacts real upstreams; it has no fixture-routing setting.

Schedule creation is explicit and should follow the [handoff procedure](../../docs/temporal/operations.md):

```sh
dotnet run --project RelistenIngestionWorker -- --register-schedules
```

Stop this stack without deleting its fixture data:

```sh
docker compose -f local-dev/temporal/docker-compose.yml down
```

For a fresh disposable catalog, add `--volumes` to that command and repeat setup. It deletes this project's local data, including Temporal histories.
