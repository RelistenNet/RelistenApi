# Repository Guidelines

## Project Structure & Module Organization
- `RelistenApi/` contains the ASP.NET Core API (controllers, services, models, importers, views, and `wwwroot/` assets).
- `RelistenApiTests/` holds NUnit tests and fixtures (HTML snapshots in `RelistenApiTests/Fixtures/`).
- `RelistenApi/Migrations/` includes database schema and data migrations.
- `local-dev/` contains Docker Compose for Postgres/Redis; helper scripts live at the repo root (`start-local-databases.sh`, `stop-local-databases.sh`).
- Root files like `RelistenApi.sln`, `Dockerfile`, and `Jenkinsfile` describe solution/build entry points.

## Build, Test, and Development Commands
- `./start-local-databases.sh` starts Postgres/Redis and restores a dev backup; use `./stop-local-databases.sh` to shut them down.
- `docker-compose -f local-dev/docker-compose.yml up -d` starts databases without refreshing the backup.
- `dotnet build RelistenApi.sln` builds the solution.
- `dotnet run --project RelistenApi/RelistenApi.csproj` runs the API locally (see `README.markdown` for URLs).
- `dotnet test RelistenApiTests/RelistenApiTests.csproj` runs the test suite.

## Coding Style & Naming Conventions
- C# uses standard conventions: PascalCase for types/methods, camelCase for locals/parameters, and private fields commonly prefixed with `_`.
- Follow the existing formatting in files (4-space indents, aligned SQL in verbatim strings); there is no enforced formatter in the repo.
- Keep namespaces consistent with the folder structure (e.g., `RelistenApi/Services/Data` -> `Relisten.Data`).

## Testing Guidelines
- Tests use NUnit with FluentAssertions; new tests should live in `RelistenApiTests/` alongside related fixtures.
- Name test classes `Test*` and keep `[TestFixture]`/`[Test]` attributes aligned with existing patterns.
- There are no explicit coverage gates; run targeted tests when changing importer logic or data services.

## Commit & Pull Request Guidelines
- Commit messages are short, imperative, and scoped (e.g., “Fix 3 phish.in import issues”).
- PRs should describe the change, list key files touched, and note tests run; link issues when applicable.
- Include screenshots only when UI in `RelistenApi/Views` or `wwwroot/` assets change.

## Configuration & Data Notes
- Local configuration lives in `RelistenApi/appsettings.json`; use environment variables for overrides (e.g., `ASPNETCORE_ENVIRONMENT=Development`).
- Database access is expected via the local Docker containers (see `README.markdown` for ports/credentials).

## Database & Postgres Tips
- Local Postgres runs on `127.0.0.1:15432` with database `relisten_db`, user `relisten`, password `local_dev_password`.
- Quick connect: `PGPASSWORD=local_dev_password psql -h 127.0.0.1 -p 15432 -U relisten -d relisten_db`.
- Helpful tables: `artists`, `features`, `artists_upstream_sources`, `upstream_sources` (archive.org is `upstream_source_id = 1`).
- Example query to inspect archive.org artists:\n  `select a.id, a.name, a.slug, a.featured, aus.upstream_identifier from artists a join artists_upstream_sources aus on aus.artist_id=a.id where aus.upstream_source_id=1;`
- It is MUCH better to inspect the schema using psql than to rely on the migration files to learn about the schema.  
