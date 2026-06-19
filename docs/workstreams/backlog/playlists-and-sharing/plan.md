# Workstream: Playlists And Sharing

## Goal

Implement server-side playlist metadata, playlist entries, block-aware operations, collaborator invitations, followers, cloning, share-token creation/revocation, mobile share-token exchange, access checks, and hydrated/non-hydrated playlist reads under `/api/v3/library/playlists`.

## Why This Workstream Exists

Playlists are the core user-visible feature. The server must preserve live-music blocks during shuffle, allow duplicate source tracks through distinct playlist entry UUIDs, and support sharing without treating URL tokens as durable bearer credentials.

## Mutable Surface

Allowed files and directories:

- playlist domain models and DTOs under `RelistenUserApi/Models/`
- playlist operation and access services under `RelistenUserApi/Services/`
- playlist controllers under `RelistenUserApi/Controllers/`
- migrations for playlists, entries, collaborators, followers, share tokens, mobile access grants, and edit log
- catalog hydration queries through existing catalog services or small bounded query helpers
- tests under `RelistenApiTests/` whose names contain `UserLibraryPlaylist`, `PlaylistOperation`, or `ShareToken`

Out of scope:

- realtime WebSocket collaboration
- public playlist discovery/search
- mobile Queue V2 implementation

## Main Validator

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibraryPlaylist|FullyQualifiedName~PlaylistOperation|FullyQualifiedName~ShareToken"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

Run operation-engine tests before endpoint tests:

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~PlaylistOperation"

## Dependencies or Blockers

Depends on foundation and basic authenticated user identity. Hydrated mobile playback depends on returning or resolving enough catalog identity for the mobile app to fetch full `ShowWithSources` data, but the server implementation can expose the needed UUIDs first.

## Current Hypothesis

A small playlist aggregate service can own all operation application inside a database transaction. The service should serialize writes per playlist, compute canonical `position` and integer `block_position`, enforce contiguity, and return deterministic per-operation results without adding an event-sourcing framework.

## Next Scoped Step

After auth identities exist, implement playlist create/read plus `add_track` and `add_source_range_as_block` operations with tests. Add sharing after the operation engine proves basic invariants.

