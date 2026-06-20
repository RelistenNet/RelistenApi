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

### PL-002: Share Tokens And Mobile Access Grants

- Timestamp: 2026-06-20T00:35:57Z
- Intention / hypothesis: Share-token access can be implemented as a narrow, testable layer over the existing playlist aggregate: owner-created role-scoped URL tokens, signed-out mobile viewer exchange into a short-lived device grant, signed-in editor exchange into durable collaborator access, and tokenless reads through owner/collaborator/follower/mobile-grant state.
- Responsible agent: root Codex agent.
- Start commit: `23346bf`
- Worktree or branch: branch `codex/user-library-share-tokens` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: playlist/share DTOs, playlist sharing/access services, playlist controller routes, `user_data` migrations/bootstrap SQL, and `RelistenUserApiTests` fixtures whose names contain `ShareToken` or `UserLibraryPlaylist`.
- Validator: targeted `RelistenUserApiTests` playlist/share-token tests plus `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj`, `dotnet test RelistenApiTests/RelistenApiTests.csproj`, and `dotnet build RelistenApi.sln`.
- Expected deliverable: Share-token schema/bootstrap, owner-only token create/revoke endpoints, exchange endpoint for mobile signed-out viewer grants and signed-in editor collaborator conversion, follower/collaborator/mobile-grant access checks for tokenless playlist reads, and tests proving tokenless reopened-link behavior.
- Expected artifacts: Code diff, targeted test output, local Postgres schema smoke, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: none. Create one only if the sharing/access slice grows beyond a narrow server increment.

### PL-003: Source Range Blocks And Reorder Operations

- Timestamp: 2026-06-20T01:01:54Z
- Intention / hypothesis: Source-range blocks and reorder operations can extend the existing transactional playlist operation service without adding event sourcing: source ranges should write contiguous entries in one block, while reorder should update canonical playlist positions and preserve integer block positions and block contiguity.
- Responsible agent: root Codex agent.
- Start commit: `8265b66`
- Worktree or branch: branch `codex/user-library-playlist-reorder` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: playlist operation DTOs and entities, `PlaylistService`, playlist controller operation routes only if the existing operation endpoint needs new request shapes, and `RelistenUserApiTests` fixtures whose names contain `UserLibraryPlaylist` or `PlaylistOperation`.
- Validator: targeted `RelistenUserApiTests` playlist operation tests plus `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj`, `dotnet test RelistenApiTests/RelistenApiTests.csproj`, and `dotnet build RelistenApi.sln`.
- Expected deliverable: source-range-as-block operation semantics, entry/block reorder semantics, deterministic validation for broken block contiguity, duplicate-track-safe ordering behavior, and tests proving canonical positions and integer block positions survive reorder.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: none. Create one only if reorder semantics require a broader rewrite than the current playlist operation service can cleanly support.

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

### PL-002 Outcome

- Timestamp: 2026-06-20T00:59:06Z
- End commit: committed immediately after this ledger update on branch `codex/user-library-share-tokens`.
- Artifact location: `RelistenUserApi/Controllers/PlaylistsController.cs`, `RelistenUserApi/Services/PlaylistSharingService.cs`, `RelistenUserApi/Services/OpaqueTokenService.cs`, `RelistenUserApi/Services/PlaylistService.cs`, `RelistenUserApi/Migrations/004_CreatePlaylistSharingTables.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, playlist share/access DTOs and records, and `RelistenUserApiTests/UserLibraryShareTokenTests.cs`.
- Evidence summary:
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryPlaylist|FullyQualifiedName~PlaylistOperation|FullyQualifiedName~ShareToken"` passed 20 tests.
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 37 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests.
  - `dotnet build RelistenApi.sln` passed with 0 warnings and 0 errors.
  - `git diff --check` passed.
  - Local Postgres smoke showed `user_data` tables: `playlist_blocks`, `playlist_collaborators`, `playlist_edit_log`, `playlist_entries`, `playlist_followers`, `playlist_mobile_access_grants`, `playlist_share_tokens`, `playlists`, `refresh_tokens`, `user_auth_methods`, `user_service_migrations`, `user_sessions`, `users`; migration marker rows 1, 2, 3, and 4.
- Review summary:
  - Explorer called out owner-only current reads/writes, missing share/access tables, need for central access resolution, mobile grant device binding, short-id routing, owner-only share-token management, and tokenless reopened-link tests.
  - First reviewer found mobile grants could outlive an expiring source share token. The implementation now caps mobile grant expiry to the earlier of 24 hours or source token expiry and tests the near-expiry case.
  - Second reviewer found a high-severity exchange/revoke race. Exchange and revoke now lock the share-token row with `FOR UPDATE`, and a race test proves an exchange waiting behind a revoke fails with `invalid_share_token`.
  - Final reviewer reported no findings. Residual risks: durable follower/collaborator access survives share-token revocation by design, public playlist cache/ETag behavior remains deferred, and mobile grant plus device-id exfiltration is bearer-equivalent until expiry.
- Conclusion: PL-002 is complete. The slice adds owner-only share-token creation/revocation, hashed URL share tokens, short-lived selector/secret mobile grants, anonymous token exchange for signed-out viewer access, signed-in editor exchange into durable collaborator write access, follower-backed tokenless reopened links, owner/collaborator/follower/mobile-grant access resolution for `GET /api/v3/library/playlists/{playlistUuidOrShortId}`, and tests for owner boundaries, wrong-device grants, viewer write denial, revocation, expiry capping, and exchange/revoke serialization.
- next_action: continue
- Next move: Start PL-003 for source-range-as-block and reorder operations, including canonical playlist positions, integer block positions, and tests that cover duplicate tracks and block contiguity after reorder.

### PL-003 Outcome

- Timestamp: 2026-06-20T01:21:03Z
- End commit: committed immediately after this ledger update on branch `codex/user-library-playlist-reorder`.
- Artifact location: `RelistenUserApi/Services/PlaylistService.cs`, `RelistenUserApi/Services/CatalogSourceRangeService.cs`, `RelistenUserApi/Models/PlaylistDtos.cs`, `RelistenUserApi/Migrations/005_AddPlaylistBlockForeignKey.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, `RelistenUserApi/Services/UserDataSchemaInitializer.cs`, and behavior tests in `RelistenUserApiTests/UserLibraryPlaylistTests.cs`.
- Evidence summary:
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryPlaylist|FullyQualifiedName~PlaylistOperation|FullyQualifiedName~ShareToken"` passed 29 tests.
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 46 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests.
  - `dotnet build RelistenApi.sln` passed with 0 warnings and 0 errors.
  - `git diff --check` passed.
  - Local Postgres smoke showed `user_data.user_service_migrations` marker rows 1, 2, 3, 4, and 5, plus `playlist_entries_block_uuid_fkey` referencing `user_data.playlist_blocks`.
- Review summary:
  - Explorer identified the existing append-only operation shape, missing source-range fields, placement fields, catalog resolver need, reorder unique-index risk, and the full-playlist reorder rejection risk.
  - First reviewer found three real issues: moving a block entry to standalone did not clear block fields, self-referential move anchors silently appended, and emptied block rows could be orphaned. The implementation now clears block membership, rejects anchors inside the moving set, deletes empty blocks after applied operations, and adds a database foreign key from entries to block rows.
  - Second reviewer reported no findings. Residual risks: placement behavior for missing or contradictory anchors remains intentionally permissive server-owned intent handling, source-range hydration is raw UUID-only, and clone/direct invite/public cache behavior remains deferred.
- Conclusion: PL-003 is complete. The slice adds `add_source_range_as_block`, placement-aware adds, `move_entry`, `move_block`, canonical playlist-position rewrites, integer block-position renumbering, non-applied deterministic replay statuses, source-range catalog resolution through a narrow service, and block identity enforcement with cleanup and a foreign key.
- next_action: continue
- Next move: Start PL-004 for clone and collaborator invitation acceptance, including owner/invitee authorization, tokenless clone/follow interactions, and tests for collaborator write access through accepted invites.
