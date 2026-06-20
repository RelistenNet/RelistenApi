# Ledger: Playback History

## Preregistered Experiments

### HIST-001: Batch Ingest Idempotency

- Timestamp: 2026-06-20T04:20:20Z
- Intention / hypothesis: A separate ingest-key table can provide race-safe dedupe for history batch uploads without relying on unsupported hypertable unique indexes.
- Responsible agent: root Codex agent.
- Start commit: `2cc2a51`
- Worktree or branch: branch `codex/user-library-playback-history` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: history models, services, controllers, migrations, and history tests.
- Validator: targeted `UserLibraryHistoryTests`, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, schema smoke, `git diff --check`, and secret-path scan.
- Expected deliverable: Batch upload endpoint, ingest-key idempotency, playlist attribution persistence, and history-disabled behavior tests.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, review notes, and this ledger outcome.
- Linked ExecPlan: likely needed before Timescale retention, continuous aggregate work, or catalog aggregate sink implementation.

### HIST-002: Recent History Read Contract

- Timestamp: 2026-06-20T04:27:04Z
- Intention / hypothesis: A capped authenticated recent-history endpoint can expose persisted personal history to mobile without adding cursor complexity or catalog aggregate writes in this slice.
- Responsible agent: root Codex agent.
- Start commit: `315ce33`
- Worktree or branch: branch `codex/user-library-history-read` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: history DTOs, controller, service, and history tests.
- Validator: targeted `UserLibraryHistoryTests`, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, `git diff --check`, and secret-path scan.
- Expected deliverable: `GET /api/v3/library/history/recent` with current-user scoping, descending played-at order, capped limit validation, and endpoint coverage.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, review notes, and this ledger outcome.
- Linked ExecPlan: not needed unless cursor pagination or retention policy is added.

## Outcomes

### HIST-001: Outcome

- Timestamp: 2026-06-20T04:20:20Z
- Result: Implemented authenticated `POST /api/v3/library/history/batch` with a Postgres-compatible playback history table, race-safe ingest-key dedupe, playlist attribution fields, and history-disabled no-op behavior.
- Artifact locations: `RelistenUserApi/Controllers/HistoryController.cs`, `RelistenUserApi/Models/PlaybackHistoryDtos.cs`, `RelistenUserApi/Services/PlaybackHistoryService.cs`, `RelistenUserApi/Migrations/008_CreatePlaybackHistoryTables.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, `RelistenUserApi/Services/UserDataSchemaInitializer.cs`, `RelistenUserApi/UserApiApplication.cs`, and `RelistenUserApiTests/UserLibraryHistoryTests.cs`.
- Evidence: focused `UserLibraryHistoryTests` passed 2 tests; full `RelistenUserApiTests` passed 65 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths; local Postgres schema smoke found migration marker 8, `playback_history`, `playback_history_ingest_keys`, and expected playback-history indexes.
- Review: Root adversarial pass fixed ingest-key normalization so `device_id` dedupe uses the same trimmed value persisted to history rows, added block-attribution validation, confirmed the user API does not write directly to catalog `source_track_plays`, and deferred Timescale hypertable conversion after full-suite validation exposed non-idempotent local DDL from implicit startup conversion.
- Conclusion: HIST-001 is complete for authenticated batch upload, retry dedupe, playlist attribution, and history-disabled no-op semantics. A later slice should add a narrow catalog aggregate sink or recent-history query endpoint.
- next_action: continue

### HIST-002: Outcome

- Timestamp: 2026-06-20T04:29:14Z
- Result: Implemented authenticated `GET /api/v3/library/history/recent` with a capped `limit`, current-user scoping, newest-first ordering, and a minimal response DTO that omits ingest-only device/app metadata.
- Artifact locations: `RelistenUserApi/Controllers/HistoryController.cs`, `RelistenUserApi/Models/PlaybackHistoryDtos.cs`, `RelistenUserApi/Services/PlaybackHistoryService.cs`, and `RelistenUserApiTests/UserLibraryHistoryTests.cs`.
- Evidence: focused `UserLibraryHistoryTests` passed 6 tests; full `RelistenUserApiTests` passed 69 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths.
- Review: Root adversarial pass tightened the read response so it returns playback identity and attribution fields without echoing `device_id`, `platform`, or `app_version`. Reviewer found a low contract-test gap for that omission; the history test now asserts raw snake-case attribution fields and rejects ingest-only metadata in the response. The focused tests also caught an intermediate DTO edit error before broad validation.
- Conclusion: HIST-002 is complete for a first mobile-readable personal history contract. The remaining playback-history gap is the catalog-owned aggregate sink for accepted plays.
- next_action: continue
