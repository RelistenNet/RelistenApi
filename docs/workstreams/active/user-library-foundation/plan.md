# Workstream: User Library Server Foundation

## Goal

Create the smallest useful separately deployable server foundation for `/api/v3/library` work: a new `RelistenUserApi` ASP.NET Core web project in `RelistenApi.sln`, `user_data` schema bootstrap, migration safety checks, snake-case DTO serialization tests, a testable auth seam, and one protected profile endpoint. This workstream should prove that the user API can run and scale separately while sharing only intentional code with the existing catalog API.

## Why This Workstream Exists

Every later workstream depends on the same basics: the new web project must be buildable and runnable on its own, tables must be created in `user_data`, v3 JSON must be snake_case, protected endpoints need a consistent authenticated user context, and tests need a fast way to exercise endpoints without real Apple or Google OAuth. Building this as a narrow slice prevents the larger auth, playlist, sync, and history work from inventing their own foundations.

## Mutable Surface

Allowed files and directories:

- New `RelistenUserApi/RelistenUserApi.csproj` web project and its `Program.cs`, appsettings, controllers, services, models, and migrations.
- `RelistenApi.sln` to include the new project and any intentional shared/test projects.
- Optional small shared class-library project if needed for common database/serializer/catalog code. Do not reference the existing `RelistenApi/RelistenApi.csproj` web project from `RelistenUserApi`.
- Existing `RelistenApi/` files only as read-only pattern references or for narrow shared-code extraction when direct duplication would be worse.
- `RelistenUserApiTests/` or `RelistenApiTests/` for serializer, migration, and endpoint tests.
- `docs/autoplan-user-library-server.md`, `docs/loop-ledger-user-library-server.md`, and this workstream ledger

Out of scope:

- Apple/Google OAuth implementation
- refresh-token rotation
- playlist operation engine
- playback-history batch upload
- mobile client changes

## Main Validator

Run from `/Users/alecgorge/code/relisten/RelistenApi`:

    dotnet sln RelistenApi.sln list
    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibrary"
    dotnet build RelistenApi.sln

If a separate `RelistenUserApiTests` project is created, run that test project directly as the primary targeted validator. If the targeted filter has no tests yet at the start, the first iteration should add tests with names containing `UserLibrary` and then run the command again.

## Fastest Useful Current Check

For day-to-day edits, run the narrowest new fixture, for example:

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibrarySerialization"

Then run `dotnet build RelistenApi.sln` before claiming a foundation slice is ready.

## Dependencies or Blockers

No code dependency blocks this workstream. Local database-backed checks depend on local Postgres only when migration integration tests are added. Pure serializer and controller contract tests should be possible without the database.

## Current Hypothesis

The foundation slice is complete in commit `b7cab47`. The small web project plus minimal intentional seams worked: `RelistenUserApi` builds independently, has its own route prefix, authenticated principal abstraction, snake-case DTO serializer settings, and schema-qualified bootstrap SQL. No shared class-library extraction was needed.

## Next Scoped Step

No further foundation step is pending. Promote the `auth-and-sessions` workstream when continuing the broader server AutoPlan.

## Code Quality Rules

Use straightforward code. Prefer named service classes with one reason to change. Keep SQL close to the service that owns it unless repeated query fragments create real duplication. Do not introduce a generic repository, mediator framework, or broad architectural layer. Add abstractions only when they reduce immediate duplication or make the next workstream simpler.
