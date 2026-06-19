# Workstream: Sync, Favorites, And Settings

## Goal

Implement incremental sync, tombstones, favorites, and settings under `/api/v3/library/sync`, `/api/v3/library/favorites`, and `/api/v3/library/settings`.

## Why This Workstream Exists

Mobile needs scoped Realm rows and deterministic server sync. Favorites must include current mobile categories including source, tour, and song. Deletions must sync across devices through tombstones rather than disappearing silently.

## Mutable Surface

Allowed files and directories:

- favorites/settings/sync models and DTOs under `RelistenUserApi/Models/`
- sync cursor, tombstone, favorite, and settings services under `RelistenUserApi/Services/`
- sync/favorite/settings controllers under `RelistenUserApi/Controllers/`
- migrations for `user_favorites`, `user_settings`, sync cursor/tombstone support if separate rows are needed
- tests under `RelistenApiTests/` whose names contain `UserLibrarySync`, `UserLibraryFavorites`, or `UserLibrarySettings`

Out of scope:

- mobile Realm implementation
- changing catalog `isFavorite` flags
- social/activity feed

## Main Validator

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibrarySync|FullyQualifiedName~UserLibraryFavorites|FullyQualifiedName~UserLibrarySettings"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibraryFavorites"

## Dependencies or Blockers

Depends on authenticated users and enough playlist tables to include playlist changes in sync. Can implement favorites/settings before full playlist operations if user identity and schema helpers exist.

## Current Hypothesis

Sync should be simple and cursor-based: each user-owned row that needs cross-device deletion semantics has `updated_at` and `deleted_at`, and the sync endpoint returns changed rows plus tombstones since the cursor. Avoid a complex CRDT or bidirectional merge system for M1.

## Next Scoped Step

Implement favorites with `artist`, `show`, `source`, `track`, `tour`, and `song`, then add settings. Add sync cursor aggregation after at least two user-data row families exist.

