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
- tests under `RelistenUserApiTests/` whose names contain `UserLibraryPlaylist`, `PlaylistOperation`, or `ShareToken`

Out of scope:

- realtime WebSocket collaboration
- public playlist discovery/search
- mobile Queue V2 implementation

## Main Validator

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryPlaylist|FullyQualifiedName~PlaylistOperation|FullyQualifiedName~ShareToken"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

Run operation-engine tests before endpoint tests:

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~PlaylistOperation"

## Dependencies or Blockers

Depends on foundation and basic authenticated user identity. Auth/session landed in commit `709bb81`, so playlist writes can use the protected user context and Postgres-backed `user_data` schema helpers. Hydrated mobile playback depends on returning or resolving enough catalog identity for the mobile app to fetch full `ShowWithSources` data, but the first server implementation can expose raw UUIDs first.

## Current Hypothesis

A small playlist aggregate service can own operation application inside a database transaction. Source-range blocks, reorder operations, clone, and collaborator invitations are now in place without adding event sourcing. The next playlist/sharing slice should close the remaining read-contract hardening before sync work: public playlist cache behavior, private/authenticated no-store behavior, and catalog hydration boundaries.

## Next Scoped Step

Implement PL-005: public playlist/cache behavior and the remaining playlist read contract. Tests should prove public reads are revision-cacheable, private/authenticated reads stay `no-store`, share-token and mobile-grant paths do not leak tokens, and playlist responses expose a stable catalog hydration boundary for mobile without pulling catalog API concerns into the user-library service.
