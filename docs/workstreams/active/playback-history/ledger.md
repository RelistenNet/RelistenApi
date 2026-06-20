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

### HIST-003: Catalog Aggregate Queue

- Timestamp: 2026-06-20T04:37:47Z
- Intention / hypothesis: The user API can feed catalog popularity without writing directly to `public.source_track_plays` by enqueueing anonymous accepted-play events in `user_data` for a catalog-owned worker to drain later.
- Responsible agent: root Codex agent.
- Start commit: `a9dead4`
- Worktree or branch: branch `codex/user-library-history-aggregate` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: playback-history schema, initializer, aggregate sink service, ingest service wiring, DI registration, and history tests.
- Validator: targeted `UserLibraryHistoryTests`, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, schema smoke, `git diff --check`, and secret-path scan.
- Expected deliverable: accepted history rows enqueue one anonymous aggregate event, duplicate retries do not enqueue another event, and history-disabled uploads enqueue nothing.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, review notes, and this ledger outcome.
- Linked ExecPlan: the catalog worker/drainer can be a later workstream if real-time popularity updates need deployment automation.

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

### HIST-003: Outcome

- Timestamp: 2026-06-20T04:37:47Z
- Result: Implemented `user_data.playback_history_catalog_play_queue` and a small `PlaybackHistoryCatalogAggregateSink` that enqueues anonymous aggregate events only after accepted history inserts.
- Artifact locations: `RelistenUserApi/Migrations/009_CreatePlaybackHistoryCatalogPlayQueue.cs`, `RelistenUserApi/Migrations/UserDataSchemaSql.cs`, `RelistenUserApi/Services/UserDataSchemaInitializer.cs`, `RelistenUserApi/Services/PlaybackHistoryCatalogAggregateSink.cs`, `RelistenUserApi/Services/PlaybackHistoryService.cs`, `RelistenUserApi/UserApiApplication.cs`, and `RelistenUserApiTests/UserLibraryHistoryTests.cs`.
- Evidence: focused `UserLibraryHistoryTests` passed 6 tests; full `RelistenUserApiTests` passed 69 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; local Postgres schema smoke found migration marker 9, `playback_history_catalog_play_queue`, and its unprocessed index.
- Review: Root review kept the aggregate boundary outbox-based: the user API does not write to `public.source_track_plays`, does not enqueue on duplicate retry or history-disabled uploads, and does not put `user_id`, `device_id`, `client_event_uuid`, playlist attribution, or app version into the aggregate queue. Reviewer reported no findings; residual queue drain/retry semantics and backfill for already-accepted historical rows are explicitly out of scope for this slice.
- Conclusion: HIST-003 completes the M1 server-side playback-history surface. A later catalog-owned worker can drain the queue into catalog play aggregates.
- next_action: done
