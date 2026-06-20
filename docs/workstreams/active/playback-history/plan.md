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
- tests under `RelistenUserApiTests/` whose names contain `UserLibraryHistory` or `PlaybackHistory`

Out of scope:

- mobile local journal implementation
- year-in-review/wrapped features
- broad catalog schema write access

## Main Validator

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryHistory|FullyQualifiedName~PlaybackHistory"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryHistory"

## Dependencies or Blockers

Depends on authenticated users and user settings for history-disabled behavior. HIST-001 uses a regular Postgres-compatible table; Timescale hypertable conversion should be an explicit later migration.

## Current Hypothesis

Use a regular `playback_history_ingest_keys` table with a unique primary key for race-safe idempotency, then insert accepted rows into `user_data.playback_history`. Expose recent personal history through a capped authenticated read endpoint. Do not write directly to catalog `source_track_plays` from the user API; add a narrow catalog-owned aggregate sink later.

## Next Scoped Step

HIST-001 and HIST-002 are implemented. The next step is to add a narrow catalog-owned aggregate sink for accepted plays.
