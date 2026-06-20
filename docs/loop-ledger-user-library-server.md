# Root Loop Ledger: User Library Server Components

This ledger records root-level coordination for `docs/autoplan-user-library-server.md`. It is not a diary of every code experiment; detailed work belongs in each workstream ledger.

## Iterations

### Iteration 1

- Timestamp: 2026-06-19T20:35:14Z
- Hypothesis: A root AutoPlan plus workstream package will keep the server implementation scoped, testable, and maintainable across the independent auth, playlist, sync, and history branches.
- Action: Created the AutoPlan package, chose `docs/autoplan-user-library-server.md` and `docs/loop-ledger-user-library-server.md` as the root pair, and created workstream directories under `docs/workstreams/`.
- Evidence: Root AutoPlan names the design source, acceptance evidence, mutable surface, milestones, and a workstream board. Each workstream has a plan and ledger file.
- Verdict: pass
- Next Action: continue
- Next move: Start `user-library-foundation` workstream experiment `FND-001` before implementation, using its ledger as the write-ahead log.

### Iteration 2

- Timestamp: 2026-06-19T20:47:31Z
- Hypothesis: The implementation plan should treat the user-library API as a separately deployable server project, not a controller set inside the existing catalog API.
- Action: Updated the design and AutoPlan package to target a new `RelistenUserApi` ASP.NET Core web project in `RelistenApi.sln`, with independent startup/configuration/health/deployment artifacts and optional small shared class libraries only when they remove real duplication.
- Evidence: Root AutoPlan mutable surface names `RelistenUserApi/RelistenUserApi.csproj` as the primary server write surface. The foundation workstream validator includes `dotnet sln RelistenApi.sln list`, targeted tests, and `dotnet build RelistenApi.sln`. The design deployment model names a separate .NET process, Kubernetes deployment, and Docker image.
- Verdict: pass
- Next Action: continue
- Next move: Start `user-library-foundation` workstream experiment `FND-001` by creating the separate project and proving independent build/run shape before adding larger auth or playlist behavior.

## Root Coordination Notes

- The active set currently has one implementation workstream: `user-library-foundation`. Other workstreams are backlog because they depend on the foundation's schema, serializer, and test seams.
- Before using Git worktrees, create or switch to a `codex/...` branch for the active workstream and record it in both the root board and the workstream ledger.
- After each milestone, launch a single-shot reviewer against the concrete diff and targeted test evidence before promoting dependent workstreams.

### 2026-06-19T20:54:30Z Coordination Update

- Active workstream: `user-library-foundation`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-foundation`.
- Action: Claimed preregistered experiment `FND-001` before implementation edits.
- Next Action: continue.

### 2026-06-19T21:02:30Z Coordination Update

- Active workstream: `user-library-foundation`.
- Result: completed experiment `FND-001`.
- Code commit: `b7cab47`.
- Evidence: new user API tests passed (7 tests), existing API tests passed (47 tests), solution build passed, solution list includes the new API/test projects, runtime smoke proved `/health` 200 `OK` and unauthenticated `/api/v3/library/users/me` 401 with `Cache-Control: no-store`.
- Next Action: done for `user-library-foundation`; promote `auth-and-sessions` when continuing the broader server AutoPlan.

### 2026-06-19T23:24:01Z Coordination Update

- Active workstream: `auth-and-sessions`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-auth`.
- Action: Promoted auth-and-sessions from backlog to active and claimed preregistered experiment `AUTH-001` before implementation edits.
- Next Action: continue.

### 2026-06-19T23:53:31Z Coordination Update

- Active workstream: `auth-and-sessions`.
- Result: completed experiment `AUTH-001`.
- Branch: `codex/user-library-auth`.
- Evidence: `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 17 tests, `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and local Postgres contained the expected `user_data` auth/session tables and migration markers.
- Review: First reviewer findings were accepted and fixed; user feedback removed low-signal migration/route reflection tests; second reviewer findings were accepted and fixed; final reviewer reported no findings.
- Steering incorporated: Mobile local development will use separate catalog/user-library base URLs, so the auth slice added a Development/Test-only token endpoint for the iOS Simulator at `/api/v3/library/auth/development/session`.
- Next Action: done for `auth-and-sessions`; promote `playlists-and-sharing` next.

### 2026-06-19T23:55:35Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-playlists`.
- Action: Promoted playlists-and-sharing from backlog to active and claimed preregistered experiment `PL-001` before implementation edits.
- Start commit: `709bb81`.
- Next Action: continue.

### 2026-06-20T00:27:02Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Result: completed experiment `PL-001`.
- Branch: `codex/user-library-playlists`.
- Evidence: focused playlist tests passed 14 tests, `RelistenUserApiTests` passed 31 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and local Postgres contained the expected `user_data` playlist/auth tables with migration markers 1, 2, and 3.
- Review: Reviewer findings were accepted and fixed, including idempotency replay/conflict handling, cross-playlist UUID conflicts, block UUID identity, invalid mixed operation fields, missing `idempotency_key`, and list endpoint entry loading. Final reviewer reported no findings.
- Next Action: continue for `playlists-and-sharing`; implement share-token creation/exchange, mobile access grants, collaborator/follower access checks, and tokenless reopened-link resolution next.

### 2026-06-20T00:35:57Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-share-tokens`.
- Action: Claimed preregistered experiment `PL-002` before implementation edits.
- Start commit: `23346bf`.
- Next Action: continue.

### 2026-06-20T00:59:06Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Result: completed experiment `PL-002`.
- Branch: `codex/user-library-share-tokens`.
- Evidence: focused playlist/share-token tests passed 20 tests, `RelistenUserApiTests` passed 37 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and local Postgres contained expected `user_data` share/access tables with migration markers 1, 2, 3, and 4.
- Review: Explorer findings shaped the access resolver and tests. First reviewer finding on mobile grants outliving source token expiry was fixed. Second reviewer finding on exchange/revoke races was fixed with share-token row locks and a race test. Final reviewer reported no findings.
- Next Action: continue for `playlists-and-sharing`; implement source-range-as-block and reorder operations next.

### 2026-06-20T01:01:54Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-playlist-reorder`.
- Action: Claimed preregistered experiment `PL-003` before implementation edits.
- Start commit: `8265b66`.
- Next Action: continue.

### 2026-06-20T01:21:03Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Result: completed experiment `PL-003`.
- Branch: `codex/user-library-playlist-reorder`.
- Evidence: focused playlist/share-token tests passed 29 tests, `RelistenUserApiTests` passed 46 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and local Postgres contained migration markers 1 through 5 plus `playlist_entries_block_uuid_fkey`.
- Review: Explorer findings shaped the catalog resolver and reorder update strategy. First reviewer findings on standalone moves, self-referential anchors, and orphaned block rows were fixed. Second reviewer reported no findings.
- Next Action: continue for `playlists-and-sharing`; implement clone and collaborator invitation acceptance next.

### 2026-06-20T01:22:15Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-collaboration`.
- Action: Claimed preregistered experiment `PL-004` before implementation edits.
- Start commit: `a14ab3a`.
- Next Action: continue.

### 2026-06-20T01:45:02Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Result: completed experiment `PL-004`.
- Branch: `codex/user-library-collaboration`.
- Evidence: focused share/collaboration tests passed 10 tests, `RelistenUserApiTests` passed 50 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, and `git diff --check` passed.
- Review: Explorer findings shaped clone and invitation boundaries. Reviewer findings on clone revision/logging, snapshot consistency, short-id collision retry, idempotency lock ordering, replay snapshots, description idempotency semantics, retry-safe revoke, and the documented invitation accept route were accepted and fixed with behavior tests.
- Next Action: continue for `playlists-and-sharing`; implement public playlist/cache behavior and the remaining playlist read contract next.

### 2026-06-20T01:49:48Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-playlist-cache`.
- Action: Claimed preregistered experiment `PL-005` before implementation edits.
- Start commit: `6deea35`.
- Next Action: continue.

### 2026-06-20T02:01:13Z Coordination Update

- Active workstream: `playlists-and-sharing`.
- Result: completed experiment `PL-005`.
- Branch: `codex/user-library-playlist-cache`.
- Evidence: focused playlist/share-token tests passed 40 tests, `RelistenUserApiTests` passed 57 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, and `git diff --check` passed.
- Review: Explorer findings shaped the cache/read scope. Reviewer findings on missing `Vary` and partial mobile-grant headers becoming cacheable were accepted and fixed with behavior tests. Final reviewer reported no actionable findings.
- Next Action: continue; promote `sync-favorites-settings` next for favorites/settings schema, tombstones, and sync cursors.

### 2026-06-20T02:02:51Z Coordination Update

- Active workstream: `sync-favorites-settings`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-sync`.
- Action: Promoted sync-favorites-settings from backlog to active, moved its workstream files under `docs/workstreams/active/`, and claimed preregistered experiment `SYNC-001` before implementation edits.
- Start commit: `019b599`.
- Next Action: continue.

### 2026-06-20T04:06:46Z Coordination Update

- Active workstream: `sync-favorites-settings`.
- Result: completed experiment `SYNC-001`.
- Branch: `codex/user-library-sync`.
- Evidence: focused `UserLibrarySyncTests` passed 4 tests, `RelistenUserApiTests` passed 61 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and local Postgres contained migration marker 6 plus `sync_version` support for favorites/settings.
- Review: Reviewer findings on timestamp cursor skips and idempotent retry churn were fixed with sequence-backed cursors, per-user advisory transaction locks, and no-op retry preservation. The root review also fixed timestamp-based response ordering after the corrected reviewer attempt hit the subagent usage limit.
- Next Action: continue for `sync-favorites-settings`; implement `SYNC-002` for playlist, invitation, and revocation sync aggregation before playback-history promotion.

### 2026-06-20T04:14:40Z Coordination Update

- Active workstream: `sync-favorites-settings`.
- Result: completed experiment `SYNC-002`.
- Branch: `codex/user-library-sync-feed`.
- Evidence: focused `UserLibrarySyncTests` passed 6 tests, `RelistenUserApiTests` passed 63 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, secret-path scan found no local OAuth secrets or paths, and local Postgres contained migration markers 6 and 7 plus playlist/collaborator/follower `sync_version` columns.
- Review: Root adversarial pass checked cursor advancement, access scoping, idempotent/no-op churn, and noisy tombstones; it fixed direct editor share-token exchange producing irrelevant invitation tombstones.
- Next Action: done for `sync-favorites-settings`; promote `playback-history` next.

### 2026-06-20T04:20:20Z Coordination Update

- Active workstream: `playback-history`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-playback-history`.
- Action: Promoted playback-history from backlog to active, moved its workstream files under `docs/workstreams/active/`, and claimed preregistered experiment `HIST-001` before implementation edits.
- Start commit: `2cc2a51`.
- Next Action: continue.

### 2026-06-20T04:23:26Z Coordination Update

- Active workstream: `playback-history`.
- Result: completed experiment `HIST-001`.
- Branch: `codex/user-library-playback-history`.
- Evidence: focused `UserLibraryHistoryTests` passed 2 tests, `RelistenUserApiTests` passed 65 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, secret-path scan found no local OAuth secrets or paths, and local Postgres contained migration marker 8 plus `playback_history`, `playback_history_ingest_keys`, and expected playback-history indexes.
- Review: Root adversarial pass fixed ingest-key `device_id` normalization, added block-attribution validation, confirmed no direct user API writes to catalog `source_track_plays`, and deferred Timescale hypertable conversion after full-suite validation exposed non-idempotent local DDL when startup converted the table implicitly.
- Next Action: continue for `playback-history`; implement a narrow catalog aggregate sink or recent-history query endpoint next.

### 2026-06-20T04:27:04Z Coordination Update

- Active workstream: `playback-history`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-history-read`.
- Action: Claimed preregistered experiment `HIST-002` for a capped authenticated recent-history endpoint before implementation completion.
- Start commit: `315ce33`.
- Next Action: continue.

### 2026-06-20T04:29:14Z Coordination Update

- Active workstream: `playback-history`.
- Result: completed experiment `HIST-002`.
- Branch: `codex/user-library-history-read`.
- Evidence: focused `UserLibraryHistoryTests` passed 6 tests, `RelistenUserApiTests` passed 69 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and secret-path scan found no local OAuth secrets or paths.
- Review: Root adversarial pass tightened the recent-history response by omitting ingest-only `device_id`, `platform`, and `app_version`. Reviewer found a low raw response-shape test gap; the test now asserts snake-case attribution fields and rejects ingest-only metadata. Focused tests caught the intermediate DTO mistake before full validation.
- Next Action: continue for `playback-history`; implement the catalog-owned aggregate sink next.

### 2026-06-20T04:37:47Z Coordination Update

- Active workstream: `playback-history`.
- Result: completed experiment `HIST-003`.
- Branch: `codex/user-library-history-aggregate`.
- Evidence: focused `UserLibraryHistoryTests` passed 6 tests, `RelistenUserApiTests` passed 69 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, and local Postgres schema smoke found migration marker 9 plus `playback_history_catalog_play_queue` and its unprocessed index.
- Review: Root review kept the aggregate boundary as an anonymous `user_data` outbox. The user API does not write directly to `public.source_track_plays`, does not enqueue duplicate retry or history-disabled uploads, and does not put user/device/client-event/playlist/app-version fields into the aggregate queue.
- Next Action: done for `playback-history`; promote `server-contract-tests` next.

### 2026-06-20T04:43:12Z Coordination Update

- Active workstream: `server-contract-tests`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-contract-hardening`.
- Action: Promoted server-contract-tests from backlog to active, moved its workstream files under `docs/workstreams/active/`, and claimed experiment `CT-001`.
- Evidence: focused `UserLibraryContractTests` passed 8 tests, `RelistenUserApiTests` passed 77 tests, `RelistenApiTests` passed 47 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, `git diff --check` passed, and secret-path scan found no local OAuth secrets or paths.
- Review: Reviewer found no code/test correctness issues and flagged stale docs; the evidence and board text now match the completed checks.
- Next Action: done for CT-001; commit this slice, then continue deployment/runtime release gates.

### 2026-06-20T04:49:12Z Coordination Update

- Active workstream: `server-contract-tests`.
- Responsible agent: root Codex agent.
- Branch: `codex/user-library-deployment-hardening`.
- Action: Started CT-002 for separate user API deployment artifacts.
- Evidence: release publish produced `RelistenUserApi.dll`, focused `UserLibraryContractTests` passed 8 tests, `dotnet build RelistenApi.sln` passed with 0 warnings/errors, Docker image build for `Dockerfile.userapi` passed, container `/health` smoke returned `OK` with schema initialization disabled, workflow YAML parse passed, `git diff --check` passed, and secret-path scan found no local OAuth secrets or paths.
- Review: Reviewer findings were fixed with deploy concurrency, pinned action refs for secret-sensitive setup, base64-decoded kubeconfig setup preserving the existing secret contract, direct kubectl rollout commands, and rollout status waiting. Final reviewer verification found no deployment workflow blockers.
- Next Action: done for CT-002; commit this slice, then implement account deletion/export server gates.
