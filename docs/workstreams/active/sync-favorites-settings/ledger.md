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

## Outcomes

### SYNC-001: Outcome

- Timestamp: 2026-06-20T04:06:46Z
- Result: Implemented the favorites/settings sync base under `/api/v3/library/favorites`, `/api/v3/library/settings`, and `/api/v3/library/sync`.
- Artifact locations: `RelistenUserApi/Controllers/FavoritesController.cs`, `RelistenUserApi/Controllers/SettingsController.cs`, `RelistenUserApi/Controllers/SyncController.cs`, `RelistenUserApi/Services/UserLibrarySyncService.cs`, `RelistenUserApi/Models/SyncDtos.cs`, `RelistenUserApi/Migrations/006_CreateFavoritesSettingsTables.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, and `RelistenUserApiTests/UserLibrarySyncTests.cs`.
- Evidence: focused `UserLibrarySyncTests` passed 4 tests; full `RelistenUserApiTests` passed 61 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; local Postgres schema smoke found migration marker 6 plus `sync_version` columns and `user_data.user_sync_version_seq`.
- Review: Reviewer findings on timestamp cursor skips and noisy idempotent retries were accepted and fixed by switching to a sequence-backed cursor, per-user advisory transaction locks, and no-op retry preservation. A final corrected reviewer attempt hit the subagent usage limit, so the root pass reviewed the corrected diff and fixed change ordering to use `sync_version` instead of timestamps.
- Conclusion: SYNC-001 is complete for source/tour/song-inclusive favorites, settings JSON, tombstones, idempotent retries, and an opaque monotonic sync cursor. The workstream should continue with playlist, invitation, and revocation sync aggregation before playback-history promotion.
- next_action: continue
