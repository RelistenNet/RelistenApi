# Repository Guidelines

## Project Structure & Module Organization
- `RelistenApi/` contains the ASP.NET Core API (controllers, services, models, importers, views, and `wwwroot/` assets).
- `RelistenApiTests/` holds NUnit tests and fixtures (HTML snapshots in `RelistenApiTests/Fixtures/`).
- `RelistenApi/Migrations/` includes database schema and data migrations.
- `RelistenIngestionWorker/` hosts Temporal ingestion and operator commands without starting the API or Hangfire. Workflows, activities, and contracts live in `RelistenApi/Services/Ingestion/`.
- `docs/ingestion/README.md` explains ingestion architecture and shared contracts, with links to provider rules and operations. Read the relevant contracts before changing workflow behavior.
- `local-dev/` contains Docker Compose for Postgres/Redis; helper scripts live at the repo root (`start-local-databases.sh`, `stop-local-databases.sh`).
- Root files like `RelistenApi.sln`, `Dockerfile`, and `Jenkinsfile` describe solution/build entry points.

## Build, Test, and Development Commands
- `./start-local-databases.sh` starts the local services and loads the checked-in seed only into an empty catalog. It preserves existing data and starts no imports or schedules. Use `./stop-local-databases.sh` to stop services without deleting data.
- `local-dev/README.md` is the canonical setup guide. No AWS access, backup download, or separate Temporal database container is needed for tests.
- `dotnet build RelistenApi.sln` builds the solution.
- `dotnet run --project RelistenApi/RelistenApi.csproj` runs the API locally (see `README.markdown` for URLs).
- `./test-local.sh` runs tests, including PostgreSQL/Redis integration tests, against disposable databases on the shared local services. Pass filters and other `dotnet test` options through this script.
- Plain `dotnet test RelistenApiTests/RelistenApiTests.csproj` skips those integration tests unless `RELISTEN_LOCAL_INGESTION_TESTS=1` is set.

## Coding Style & Naming Conventions
- C# uses standard conventions: PascalCase for types/methods, camelCase for locals/parameters, and private fields commonly prefixed with `_`.
- Use CSharpier's formatting defaults and the repository's `.editorconfig` settings.
- Keep namespaces consistent with the folder structure (e.g., `RelistenApi/Services/Data` -> `Relisten.Data`).
- Use the repository-local CSharpier tool for formatting: run `dotnet tool restore`, then `dotnet csharpier format .` from the repo root.
- Before every commit and push, run `dotnet csharpier check .`. If it fails, run `dotnet csharpier format .`, review the resulting diff, and rerun the check before proceeding.

## Testing Guidelines
- Tests use NUnit with FluentAssertions; new tests should live in `RelistenApiTests/` alongside related fixtures.
- Name test classes `Test*` and keep `[TestFixture]`/`[Test]` attributes aligned with existing patterns.
- There are no explicit coverage gates; run targeted tests when changing importer logic or data services.
- Temporal tests use SDK-managed temporary servers and synthetic upstream responses. Do not start production schedules or import real provider catalogs to verify local behavior.
- Verify success, partial failure, and recovery through workflow results and catalog service contracts. A completed Temporal workflow can still report partial or failed imports.

## Commit & Pull Request Guidelines
- Commit messages are short, imperative, and scoped (e.g., “Fix 3 phish.in import issues”).
- PRs should describe the change, list key files touched, and note tests run; link issues when applicable.
- Include screenshots only when UI in `RelistenApi/Views` or `wwwroot/` assets change.

## Configuration & Data Notes
- Local configuration lives in `RelistenApi/appsettings.json`; use environment variables for overrides (e.g., `ASPNETCORE_ENVIRONMENT=Development`).
- Database access is expected via the local Docker containers (see `README.markdown` for ports/credentials).
- The standalone worker uses its own host configuration; it does not execute API `Program` or `Startup` setup. Dependencies and telemetry configured there do not automatically apply to the worker. See `docs/ingestion/operations.md` for worker settings and current Sentry coverage.
- Keep workflow code deterministic. Network calls, database writes, and external reporting belong in activities. Drain incompatible open histories before deploying changed workflow behavior, or use Temporal versioning.

## Database & Postgres Tips
- Local Postgres runs on `127.0.0.1:15432` with database `relisten_db`, user `relisten`, password `local_dev_password`.
- Quick connect: `PGPASSWORD=local_dev_password psql -h 127.0.0.1 -p 15432 -U relisten -d relisten_db`.
- Production read-only Postgres runs on the relisten3 replica. Use the direct endpoint `relisten-db-ro.tail09dbf.ts.net:5432` with database `app` and user `app` for schema inspection and query-performance checks such as `EXPLAIN (ANALYZE, BUFFERS)`.
- Use `relisten-db-pgbouncer-ro.tail09dbf.ts.net:5432` when a check needs to follow the application's PgBouncer path. The matching Postico favorites are `relisten-prod direct RO app` and `relisten-prod PgBouncer RO app`.
- Read the current password from the `relisten-db-app` secret in the `relisten3` context instead of relying on a cached Postico password. Do not print or persist it; the quick-connect command below passes it directly to `psql`.
- Quick connect to the direct production replica: `PGPASSWORD="$(kubectl --context relisten3 -n default get secret relisten-db-app -o jsonpath='{.data.password}' | base64 --decode)" PGSSLMODE=require PGOPTIONS='-c default_transaction_read_only=on' psql -X -v ON_ERROR_STOP=1 -h relisten-db-ro.tail09dbf.ts.net -p 5432 -U app -d app`.
- Helpful tables: `artists`, `features`, `artists_upstream_sources`, `upstream_sources` (archive.org is `upstream_source_id = 1`).
- Example query to inspect archive.org artists:\n  `select a.id, a.name, a.slug, a.featured, aus.upstream_identifier from artists a join artists_upstream_sources aus on aus.artist_id=a.id where aus.upstream_source_id=1;`
- It is MUCH better to inspect the schema using psql than to rely on the migration files to learn about the schema.  

## Readability acceptance

- Treat source readability as a deliverable for new or substantially changed code. A formatter pass and passing tests do not establish it.
- Keep orchestration entrypoints at one level of detail. Name the domain phases in execution order; put pagination, item processing, checkpoint advancement, and finalization in methods with clear responsibilities. A single-caller helper is useful when it exposes a coherent phase or invariant. Do not create forwarding layers just to shorten a method.
- Use braces for control-flow bodies. Separate setup, guards, domain operations, state transitions, and returns with blank lines where they form distinct thoughts. Inspect the formatted source; formatting tools do not choose these groups.
- Explain non-obvious ownership, durable state, retry/cancellation behavior, and domain policy near the code that implements them. Comments should supply context a maintainer needs, not narrate assignments.
- Preserve this standard in subagent tasks and reviews. Before completion, use a fresh reader to trace a representative success, partial failure, and recovery path from the entrypoints. Fix places that require mentally executing a long method or reconstructing an undocumented protocol. Review readability separately from correctness.
- Prefer clear names, cohesive methods, and direct data flow over minimum line count. Do not replace dense methods with arbitrary wrappers, excessive parameters, a generic framework, or comments that disguise mixed responsibilities.
