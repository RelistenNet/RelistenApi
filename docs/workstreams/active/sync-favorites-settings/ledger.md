# Ledger: Sync, Favorites, And Settings

## Preregistered Experiments

### SYNC-001: Favorites And Settings Sync Base

- Timestamp: 2026-06-20T02:02:51Z
- Intention / hypothesis: Favorites and settings can establish the sync row/tombstone pattern before playlist sync is complete.
- Responsible agent: root Codex agent.
- Start commit: `019b599`
- Worktree or branch: branch `codex/user-library-sync` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: favorites/settings/sync models, services, controllers, migrations, and tests.
- Validator: targeted `RelistenUserApiTests` favorites/settings/sync tests plus `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj`, `dotnet test RelistenApiTests/RelistenApiTests.csproj`, and `dotnet build RelistenApi.sln`.
- Expected deliverable: Favorite add/remove/list for six entity types, settings get/put, tombstone behavior, and initial sync response shape.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: none yet.

### SYNC-002: Playlist, Invitation, And Revocation Sync

- Timestamp: 2026-06-20T04:14:40Z
- Intention / hypothesis: The existing playlist/follower/collaborator rows can join the same sequence-backed sync feed without adding a separate generic sync table.
- Responsible agent: root Codex agent.
- Start commit: `765e810`
- Worktree or branch: branch `codex/user-library-sync-feed` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: playlist/access sync metadata migration, playlist/sharing mutation SQL, sync DTOs/service queries, and endpoint tests.
- Validator: focused `UserLibrarySyncTests` plus full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, schema smoke, `git diff --check`, and secret-path scan.
- Expected deliverable: Sync feed entries for playlist snapshots, followed playlist updates, collaborator invitations, accepted collaborator access, and collaborator revocation tombstones.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, review notes, and this ledger outcome.
- Linked ExecPlan: none yet.

## Outcomes

### SYNC-001: Outcome

- Timestamp: 2026-06-20T04:06:46Z
- Result: Implemented the favorites/settings sync base under `/api/v3/library/favorites`, `/api/v3/library/settings`, and `/api/v3/library/sync`.
- Artifact locations: `RelistenUserApi/Controllers/FavoritesController.cs`, `RelistenUserApi/Controllers/SettingsController.cs`, `RelistenUserApi/Controllers/SyncController.cs`, `RelistenUserApi/Services/UserLibrarySyncService.cs`, `RelistenUserApi/Models/SyncDtos.cs`, `RelistenUserApi/Migrations/006_CreateFavoritesSettingsTables.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, and `RelistenUserApiTests/UserLibrarySyncTests.cs`.
- Evidence: focused `UserLibrarySyncTests` passed 4 tests; full `RelistenUserApiTests` passed 61 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; local Postgres schema smoke found migration marker 6 plus `sync_version` columns and `user_data.user_sync_version_seq`.
- Review: Reviewer findings on timestamp cursor skips and noisy idempotent retries were accepted and fixed by switching to a sequence-backed cursor, per-user advisory transaction locks, and no-op retry preservation. A final corrected reviewer attempt hit the subagent usage limit, so the root pass reviewed the corrected diff and fixed change ordering to use `sync_version` instead of timestamps.
- Conclusion: SYNC-001 is complete for source/tour/song-inclusive favorites, settings JSON, tombstones, idempotent retries, and an opaque monotonic sync cursor. The workstream should continue with playlist, invitation, and revocation sync aggregation before playback-history promotion.
- next_action: continue

### SYNC-002: Outcome

- Timestamp: 2026-06-20T04:14:40Z
- Result: Extended `/api/v3/library/sync` to include playlist snapshots, viewer state, pending collaborator invitations, owner collaborator changes, and playlist/collaborator access tombstones.
- Artifact locations: `RelistenUserApi/Migrations/007_AddPlaylistSyncMetadata.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, `RelistenUserApi/Services/PlaylistService.cs`, `RelistenUserApi/Services/PlaylistSharingService.cs`, `RelistenUserApi/Services/UserLibrarySyncService.cs`, `RelistenUserApi/Models/SyncDtos.cs`, and `RelistenUserApiTests/UserLibrarySyncTests.cs`.
- Evidence: focused `UserLibrarySyncTests` passed 6 tests; full `RelistenUserApiTests` passed 63 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths; local Postgres schema smoke found migration markers 6 and 7 plus playlist, collaborator, and follower `sync_version` columns.
- Review: Root adversarial pass checked cursor advancement, inaccessible playlist leakage, no-op retry churn, and noisy tombstones. It fixed direct editor share-token exchanges emitting irrelevant invitation tombstones by limiting invitation tombstones to invitation-backed collaborator rows.
- Conclusion: The sync/favorites/settings workstream now covers favorites, settings, playlists, follower-visible playlist edits, collaborator invitations, accepted collaborator access, and revocations. Playback-history can be promoted next.
- next_action: done
