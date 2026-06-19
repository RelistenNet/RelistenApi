# Ledger: Sync, Favorites, And Settings

## Preregistered Experiments

### SYNC-001: Favorites And Settings Sync Base

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: Favorites and settings can establish the sync row/tombstone pattern before playlist sync is complete.
- Responsible agent: unassigned
- Start commit: `70747d7`
- Worktree or branch: none yet; recommended branch `codex/user-library-sync`
- Mutable surface: favorites/settings/sync models, services, controllers, migrations, and tests.
- Validator: targeted favorites/settings/sync tests plus `dotnet build RelistenApi.sln`.
- Expected deliverable: Favorite add/remove/list for six entity types, settings get/put, tombstone behavior, and initial sync response shape.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: none yet.

## Outcomes

No outcome yet. This workstream is backlog until foundation and auth are available.

