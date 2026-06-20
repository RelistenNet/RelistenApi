# Ledger: Playlists And Sharing

## Preregistered Experiments

### PL-001: Minimal Playlist Aggregate And Operations

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: The playlist operation model can be implemented as a small transactional service with deterministic result statuses and no framework-heavy event sourcing.
- Responsible agent: root Codex agent.
- Start commit: `709bb81`
- Worktree or branch: branch `codex/user-library-playlists` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: playlist models, migrations, operation services, playlist controllers, and playlist tests.
- Validator: targeted `RelistenUserApiTests` playlist operation/store tests plus `dotnet build RelistenApi.sln`.
- Expected deliverable: Playlist schema/bootstrap, create/read endpoints, add-track operation, add-tracks-as-block operation, integer block-position checks, duplicate-track tests, and idempotent operation replay.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: likely needed if the operation service grows beyond a narrow first slice.

## Outcomes

### PL-001 Outcome

- End commit: committed immediately after this ledger update on branch `codex/user-library-playlists`.
- Artifact location: `RelistenUserApi/Controllers/PlaylistsController.cs`, `RelistenUserApi/Models/PlaylistDtos.cs`, `RelistenUserApi/Models/PlaylistEntities.cs`, `RelistenUserApi/Services/PlaylistService.cs`, `RelistenUserApi/Migrations/003_CreatePlaylistTables.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, and behavior tests in `RelistenUserApiTests/UserLibraryPlaylistTests.cs`.
- Evidence summary:
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryPlaylist|FullyQualifiedName~PlaylistOperation|FullyQualifiedName~ShareToken"` passed 14 tests.
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 31 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests.
  - `dotnet build RelistenApi.sln` passed with 0 warnings and 0 errors.
  - `git diff --check` passed.
  - Local Postgres smoke showed `user_data` tables: `playlist_blocks`, `playlist_edit_log`, `playlist_entries`, `playlists`, `refresh_tokens`, `user_auth_methods`, `user_service_migrations`, `user_sessions`, `users`; migration marker rows 1, 2, and 3.
- Review summary:
  - Early review found real idempotency and deterministic-error gaps around concurrent replay, duplicate entry UUIDs, duplicate block UUIDs, reused client playlist UUIDs, cross-playlist UUID reuse, different payloads under the same idempotency key, and `add_track` accepting block fields. The implementation now serializes operations per idempotency key, compares persisted JSON operations, adds a `playlist_blocks` identity table, and returns deterministic 400 errors instead of leaking database uniqueness failures.
  - Main-thread review found that `GET /api/v3/library/playlists` returned empty `entries` for persisted playlists. The list endpoint now loads entries for all returned playlists in one query and has a behavior test.
  - Final reviewer found that omitted `idempotency_key` deserialized to `Guid.Empty` and was accepted. The operation service now rejects empty idempotency keys, with raw JSON coverage for omitted keys and DTO coverage for all-zero GUIDs.
  - Final reviewer rerun reported no findings. Residual risk: collaboration/following, share-token reads, hydration, batch operations, reorder/delete, and public cache behavior are intentionally deferred.
- Conclusion: PL-001 is complete. The first playlist aggregate slice supports authenticated create/list/get under `/api/v3/library/playlists`, append-only `add_track`, append-only `add_tracks_as_block`, duplicate source tracks through distinct playlist-entry UUIDs, integer `block_position`, playlist-level sortable position strings, idempotent replay, owner-scoped reads, snake-case JSON, no-store authenticated responses, and schema-qualified `user_data` playlist tables.
- next_action: continue
- Next move: Start a new playlist/sharing slice for share-token creation/exchange, mobile token exchange/access grants, collaborator/follower access checks, and the first tokenless reopened-link behavior before catalog hydration or reorder/delete.
