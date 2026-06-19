# Workstream: Playback History

## Goal

Implement authenticated playback history batch upload, race-safe idempotency, playlist attribution, history-disabled behavior, and narrow catalog popularity integration under `/api/v3/library/history`.

## Why This Workstream Exists

Listening history is high-volume and cost-sensitive. The server needs append-only personal history without duplicating retry uploads, while still feeding anonymous aggregate popularity data through a narrow path that does not leak per-user data into the catalog schema.

## Mutable Surface

Allowed files and directories:

- playback history models and DTOs under `RelistenUserApi/Models/`
- history ingest services under `RelistenUserApi/Services/`
- history controllers under `RelistenUserApi/Controllers/`
- migrations for `playback_history`, `playback_history_ingest_keys`, Timescale setup where applicable
- narrow integration with existing `SourceTrackPlaysService` or a catalog-owned wrapper
- tests under `RelistenApiTests/` whose names contain `UserLibraryHistory` or `PlaybackHistory`

Out of scope:

- mobile local journal implementation
- year-in-review/wrapped features
- broad catalog schema write access

## Main Validator

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibraryHistory|FullyQualifiedName~PlaybackHistory"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~PlaybackHistoryIngest"

## Dependencies or Blockers

Depends on authenticated users, user settings for history-disabled behavior, and a decision about whether local development should require TimescaleDB or run a reduced Postgres-compatible test path.

## Current Hypothesis

Use a regular `playback_history_ingest_keys` table with a unique primary key for race-safe idempotency, then insert accepted rows into the history hypertable. Keep catalog aggregate insertion fire-and-forget and narrow, preferably through an existing service.

## Next Scoped Step

After user settings exist, implement the batch request DTO and ingest-key service with unit/integration tests before adding Timescale-specific retention/continuous aggregate work.

