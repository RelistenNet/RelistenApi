# Build Relisten User Library Server Components

This AutoPlan is a living document. The sections `Progress`, `Workstream Board`, `Current Hypothesis`, `Next Iteration`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept current as work proceeds.

The repository does not currently contain `PLANS.md` or `AUTOPLANS.md`. This document follows the fallback AutoPlan rules from `/Users/alecgorge/.codex/skills/autoplan/references/AUTOPLANS.md` and the fallback ExecPlan quality bar from `/Users/alecgorge/.codex/skills/autoplan/references/PLANS.md`.

## Purpose / Big Picture

Relisten needs a separately deployable user-library API server for user accounts, playlists, favorites, settings, and playback history so users can sign in with Apple or Google, keep a personal library across devices, share playlists, collaborate, and upload listening history. The successful server build should be boring to operate for a solo developer: a new ASP.NET Core web project in this solution, small domain services, explicit SQL, focused DTOs, deterministic tests, and no speculative framework or database split beyond the `user_data` schema boundary already chosen in the design.

Success is visible when the new user API project can be built, migrated, tested, run separately from the existing catalog API, and exercised through `/api/v3/library/...` endpoints against the local Postgres database, with snake-case JSON contracts, scoped user data, playlist operations, share-token access, favorites/settings sync, and authenticated history batch upload all covered by tests.

## Goal

Implement and test the server-side components described by `docs/design/2026-04-11-relisten-playlists-user-accounts-design.md` as a new separately deployable API server project in `RelistenApi.sln`, while keeping the implementation clean, minimal, straightforward, and maintainable for a solo developer. The server work must preserve existing catalog API behavior and defer mobile/client implementation to separate work.

## Evaluation Mode

Deterministic. The primary evidence is a passing server build and test suite plus targeted integration tests that exercise the new API contracts against a local database. Rubric judgment is allowed only for code-organization review, and the rubric is: small classes with one responsibility, no generic repository abstraction unless it removes real duplication, explicit SQL with named DTOs, no hidden cross-schema coupling, and no broad rewrites outside the user-library surface.

## Acceptance Evidence

The full server-side goal is accepted only when all of the following are true:

- `dotnet build RelistenApi.sln` succeeds.
- `dotnet test RelistenApiTests/RelistenApiTests.csproj` succeeds, plus any new user-API test project if created.
- `dotnet sln RelistenApi.sln list` includes the separate user API project and any intentionally created shared/test projects.
- The existing catalog API and the new user API can run as separate processes with separate configuration, ports, health checks, and deployment artifacts.
- Local database migration creates `user_data` objects without placing user-owned tables in `public`.
- API v3 user-library DTO serialization tests prove snake-case JSON while C# model properties stay idiomatic where the serializer supports it.
- Auth/session tests prove provider-subject account creation, refresh-token rotation, session revoke, and protected endpoint authorization behavior without sending email.
- Playlist operation tests cover create playlist, add track, add source range as block, reorder entry/block, block contiguity validation, duplicate source tracks, share-token exchange, follow, clone, and collaborator invitation acceptance.
- Sync tests cover incremental changes and tombstones for playlists, favorites, settings, invitations, and revocations.
- Playback history tests cover batch upload idempotency through `playback_history_ingest_keys`, history-disabled rejection/no-op behavior, playlist attribution fields, and anonymous aggregate write isolation.
- Public/cache tests prove public playlist reads are cacheable by revision, private/authenticated endpoints are `no-store`, and share-token endpoints scrub/avoid token leakage.

Fast day-to-day checks are narrower: run the targeted NUnit fixture for the workstream under development, then `dotnet build RelistenApi.sln`. The full suite must run at each milestone boundary.

## Local Testing and External Credentials

Implementation should not block on Apple, Google, or production credentials for the foundation and most server behavior. Use deterministic test seams:

- Serializer and controller tests run in-process against `RelistenUserApi`.
- Auth/session tests use fake provider verifiers that return provider, subject, and allowlisted claims; they do not call Apple or Google.
- Migration tests use local Postgres from `./start-local-databases.sh` when database coverage is needed.
- Protected endpoint tests use a test authentication handler or locally signed test JWTs, not real provider tokens.

Useful local smoke checks once Milestone 1 exists:

```bash
dotnet sln RelistenApi.sln list
dotnet build RelistenApi.sln
dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj
dotnet run --project RelistenUserApi/RelistenUserApi.csproj --urls http://localhost:5119
curl -i http://localhost:5119/health
curl -i http://localhost:5119/api/v3/library/users/me
```

The unauthenticated `/api/v3/library/users/me` smoke check should return `401` with `Cache-Control: no-store`. Contract tests should cover successful authenticated requests through the test auth seam.

Live Apple/Google credentials are only needed when implementing provider verification against real identity providers. At that point, expect to collect Google OAuth client IDs for supported platforms and Apple Sign In identifiers/key material for web/mobile callbacks. Keep those out of the repo and inject them through local secrets or environment variables. No email provider, SMTP, or SES credentials should be required for M1.

## Mutable Surface

In bounds:

- A new separately deployable ASP.NET Core project, expected name `RelistenUserApi/RelistenUserApi.csproj`, added to `RelistenApi.sln`; this is the primary server write surface for user-library controllers, services, models, auth, and migrations.
- Small shared class-library projects only when they remove real duplication between `RelistenApi` and `RelistenUserApi`; expected candidates are shared database connection helpers, serializer settings, and catalog DTO/query helpers. Do not make the new user API project reference the existing catalog web project just to reuse code.
- A new user API test project, expected name `RelistenUserApiTests/RelistenUserApiTests.csproj`, or focused additions to `RelistenApiTests/` if a separate test project would add needless overhead.
- Existing `RelistenApi/` code only when extracting a genuinely shared helper, proving catalog behavior stayed unchanged, or adding a small integration seam that cannot reasonably live in the new user API project.
- A new user-data migration area owned by `RelistenUserApi`, or a clearly separated migration location if the repo's SimpleMigrations conventions require it.
- `RelistenApiTests/`, `RelistenUserApiTests/`, or shared test fixtures.
- `docs/design/2026-04-11-relisten-playlists-user-accounts-design.md` only for clarifying decisions discovered during implementation.
- This AutoPlan package under `docs/autoplan-user-library-server.md`, `docs/loop-ledger-user-library-server.md`, and `docs/workstreams/...`.

Out of bounds unless this AutoPlan is explicitly updated:

- Mobile implementation in `/Users/alecgorge/code/relisten/relisten-mobile`.
- A physical second Postgres database. M1 uses the same database with a separate `user_data` schema.
- Email login, passkeys, ATProto login implementation, push notifications, WebSocket realtime collaboration, and playlist discovery/search.
- Replacing Realm, implementing Queue V2 client code, or changing CarPlay/Cast client behavior.
- Broad catalog importer rewrites unrelated to user-library hydration.

## Iteration Unit

One iteration is a scoped server increment with a named mutable surface, a validator, and an observable result. Examples: "add user_data schema bootstrap migration and tests", "implement refresh-token rotation service and tests", or "implement playlist add-track operation and tests." An iteration is not complete until the workstream ledger records the outcome, evidence, conclusion, and exactly one `next_action`.

## Loop Budget

Default root budget is eight root coordination iterations before reassessing scope and active workstream count. A workstream may run multiple internal coding cycles, but each externally accepted result must have focused validation evidence. If two consecutive iterations in one workstream do not produce passing targeted tests or a precise blocker, pivot by shrinking the workstream or adding an ExecPlan.

## Dispositions

Allowed `next_action` values are `continue`, `retry`, `pivot`, `undo`, `ask_user`, and `done`.

## Pivot Rules

Pivot when:

- user-library code starts requiring broad changes to existing catalog controllers or importers;
- schema migration work cannot be safely isolated under `user_data`;
- auth requires email delivery or provider behavior outside Apple/Google OAuth;
- DTO naming cannot satisfy snake-case wire JSON with maintainable C# model names;
- a workstream needs more than two failed attempts to make a targeted test pass;
- local database setup is too slow or fragile to support repeated integration tests.

The preferred pivot is to narrow the slice and make the fastest validator better before adding abstractions.

## Stop Conditions

Stop as `done` when the acceptance evidence is complete and all workstream ledgers show `next_action: done`.

Stop as `ask_user` when implementation requires a product decision not resolved by the design document, such as changing source/tour/song favorite behavior, changing share-token visibility semantics, retaining personal history longer than specified, or enabling a non-M1 auth provider.

Stop as `undo` only when a change is unsafe, leaks secrets, corrupts schema placement, breaks existing public API behavior, or creates irreversible data loss risk.

## Milestones

Milestone 1 establishes the separate server foundation. It adds `RelistenUserApi` to the solution as its own ASP.NET Core web project, gives it independent local configuration and health/API-docs startup, wires only the minimal shared code needed, adds `user_data` schema bootstrap, migration safety checks, snake-case DTO serialization tests, and a minimal protected `/api/v3/library/users/me` path backed by a test-auth seam. At the end of this milestone, the solution builds and targeted tests prove the new user API can run separately while existing catalog routes remain unchanged.

Milestone 2 implements public account and session infrastructure. It supports Apple/Google provider subject linking, refresh-token rotation, session listing/revocation, recent reauthentication markers for sensitive actions, and no email/password flow. At the end, auth tests prove tokens rotate and revoked/reused refresh tokens cannot be used.

Milestone 3 implements playlists, operations, visibility, and sharing. It creates playlist tables and services, applies deterministic operations, enforces block contiguity, resolves public/private/unlisted access, exchanges share tokens, and supports follow/clone/invite flows. At the end, endpoint tests cover the core M1 playlist lifecycle.

Milestone 4 implements sync, favorites, settings, and tombstones. It supports incremental pull cursors, scoped favorite entity types including source/tour/song, settings upsert, and tombstone propagation. At the end, tests prove multi-device sync semantics without relying on catalog booleans.

Milestone 5 implements playback history batch upload and catalog popularity integration. It creates the Timescale-compatible history table and regular ingest-key table, proves race-safe idempotency, records playlist attribution, honors history-disabled behavior, and emits anonymous aggregate writes through a narrow integration path. At the end, tests prove duplicate upload attempts do not create duplicate personal history rows.

Milestone 6 hardens deployment, observability, and release gates. It verifies cache headers, no-store auth responses, token/log scrubbing, migration placement, backup/deletion hooks, and documentation of local-dev commands. At the end, the full build and test suite pass and the server can be exercised locally through the documented happy path.

## Progress

- [x] 2026-06-19T20:35:14Z Created this root AutoPlan package and initial workstream structure.
- [x] 2026-06-19T20:54:30Z Started Milestone 1 on branch `codex/user-library-foundation`; root Codex agent claimed workstream experiment `FND-001`.
- [x] 2026-06-19T21:02:30Z Completed foundation workstream first iteration in commit `b7cab47` with separate `RelistenUserApi` and `RelistenUserApiTests` projects, validation evidence, and runtime smoke check.
- [x] 2026-06-19T23:24:01Z Promoted auth/session workstream to active on branch `codex/user-library-auth`.
- [x] 2026-06-19T23:53:31Z Completed auth/session first iteration with provider-subject signup, Postgres-backed refresh rotation, session listing/revocation, Development/Test local token endpoint, behavior tests, Postgres store tests, and reviewer passes.
- [x] 2026-06-19T23:55:35Z Promoted playlist/sharing workstream to active on branch `codex/user-library-playlists`.
- [x] 2026-06-20T00:27:02Z Completed playlist/sharing first iteration with schema/bootstrap, create/read endpoints, add-track/add-block operations, high-signal tests, reviewer pass, validation evidence, and commit.
- [x] 2026-06-20T00:35:57Z Started playlist/sharing second iteration on branch `codex/user-library-share-tokens`.
- [x] 2026-06-20T00:59:06Z Completed playlist/sharing second iteration with share-token creation/exchange, mobile grants, follower/collaborator access checks, race-safe revocation, high-signal tests, reviewer pass, validation evidence, and commit.
- [x] 2026-06-20T01:01:54Z Started playlist/sharing third iteration on branch `codex/user-library-playlist-reorder`.
- [x] 2026-06-20T01:21:03Z Completed playlist/sharing third iteration with source-range-as-block, placement-aware adds, entry/block reorder, block-contiguity validation, block foreign key, high-signal tests, reviewer pass, validation evidence, and commit.
- [x] 2026-06-20T01:22:15Z Started playlist/sharing fourth iteration on branch `codex/user-library-collaboration`.
- [x] 2026-06-20T01:45:02Z Completed playlist/sharing fourth iteration with playlist clone, collaborator invitations, invite acceptance/revoke, retry-safe clone/revoke semantics, high-signal tests, reviewer findings fixed, validation evidence, and commit.
- [x] 2026-06-20T01:49:48Z Started playlist/sharing fifth iteration on branch `codex/user-library-playlist-cache`.
- [x] 2026-06-20T02:01:13Z Completed playlist/sharing fifth iteration with public playlist ETags, no-store cache boundaries, owner visibility updates, explicit hydration boundary, high-signal tests, reviewer findings fixed, validation evidence, and commit.
- [ ] Promote sync/favorites/settings and playback-history workstreams after the core user and playlist tables exist.
- [ ] Run a reviewer pass after each milestone before promoting dependent backlog workstreams.

## Workstream Board

| Workstream | Status | Responsible agent | Blocker | Plan | Ledger | Worktree | Next step | Latest next_action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| user-library-foundation | completed | root Codex agent | none | `docs/workstreams/active/user-library-foundation/plan.md` | `docs/workstreams/active/user-library-foundation/ledger.md` | branch `codex/user-library-foundation`; code commit `b7cab47` | Foundation slice complete: separate project, independent run/build shape, schema bootstrap, serializer contract tests, and protected profile endpoint. | `done` |
| auth-and-sessions | completed | root Codex agent | none | `docs/workstreams/active/auth-and-sessions/plan.md` | `docs/workstreams/active/auth-and-sessions/ledger.md` | branch `codex/user-library-auth` | Auth/session slice complete: provider-subject signup seam, Postgres-backed sessions and refresh rotation, Development/Test local mobile token endpoint, behavior tests, Postgres store tests, and reviewer pass. | `done` |
| playlists-and-sharing | active | root Codex agent | none | `docs/workstreams/active/playlists-and-sharing/plan.md` | `docs/workstreams/active/playlists-and-sharing/ledger.md` | branch `codex/user-library-playlist-cache` | Core playlist/sharing M1 surface complete enough to promote sync/favorites/settings; bounded catalog hydration remains deferred. | `continue` |
| sync-favorites-settings | backlog | unassigned | depends on users, playlists, favorites/settings schema | `docs/workstreams/backlog/sync-favorites-settings/plan.md` | `docs/workstreams/backlog/sync-favorites-settings/ledger.md` | none | Promote next: implement initial favorites/settings schema, tombstones, and incremental sync cursors. | `continue` |
| playback-history | backlog | unassigned | depends on users, auth, history schema, catalog aggregate integration choice | `docs/workstreams/backlog/playback-history/plan.md` | `docs/workstreams/backlog/playback-history/ledger.md` | none | Implement authenticated history batch upload with ingest keys and playlist attribution. | `continue` |
| server-contract-tests | backlog | unassigned | depends on endpoints existing | `docs/workstreams/backlog/server-contract-tests/plan.md` | `docs/workstreams/backlog/server-contract-tests/ledger.md` | none | Add broad endpoint contract, cache/header, migration placement, and no-regression tests. | `continue` |

## Current Hypothesis

The foundation, auth/session, playlist aggregate, share-token/mobile-access, source-range, reorder, clone, collaborator-invitation, and public cache/read-contract slices are complete. The next move is to promote `sync-favorites-settings` and start schema/API work for source/tour/song favorites, settings, tombstones, and sync cursors. Keep local mobile development pointed at separate base URLs: catalog API at `http://localhost:3823/api` and user-library API at `http://localhost:5119`. Provider credential files are local/dev inputs only and must stay out of repo source.

## Next Iteration

Promote `sync-favorites-settings` from backlog and preregister its first implementation slice. The first slice should create schema/API support for source/tour/song favorites and settings with tombstone-friendly writes, then prove authenticated sync behavior with high-signal endpoint tests.

## Workstream Notes

New steering requests must be classified before changing this board. Ready workstreams should not be left without a responsible agent unless this file and `docs/loop-ledger-user-library-server.md` record why.

When using subagents, prefer one forked worker per active workstream once write surfaces are disjoint. Use a reviewer after each worker produces a concrete diff and targeted test result. Do not spawn short-lived workers for questions the root can answer with a fast `rg` or file read.

## Surprises & Discoveries

- Observation: The existing v3 formatting path uses `ApiV3ContractResolver`, but the design requires snake-case JSON while C# models may use C# conventions only if the user-service serializer guarantees it.
  Evidence: `RelistenApi/Startup.cs` configures `ApiV3ContractResolver`; the design now requires DTO serialization tests.
- Observation: The current live play endpoint is `/api/v2/live/play` and writes anonymous aggregate plays directly to `source_track_plays`.
  Evidence: `RelistenApi/Controllers/LiveController.cs` and `RelistenApi/Services/Data/SourceTrackPlayService.cs`.
- Observation: Migration string/reflection tests are low signal for this server work. Behavior tests and real Postgres store tests provide better evidence.
  Evidence: Removed `UserLibraryMigrationTests` and `UserLibraryRouteContractTests`; added HTTP auth/session tests and `PostgresUserAuthStoreTests`.

## Decision Log

- Decision: Build user-library functionality as a separate ASP.NET Core API project in `RelistenApi.sln`, not as controllers inside the existing catalog API project.
  Rationale: The user wants separate deployment and scaling. Keeping the project in the same solution preserves local developer ergonomics and allows selective shared code without coupling the new service to the existing web application.
  Date/Author: 2026-06-19 / Codex.
- Decision: Use root pair plus workstream directories for this AutoPlan.
  Rationale: The job has multiple dependent server branches, and the workstream board prevents auth, playlists, sync, and history from collapsing into a single oversized iteration.
  Date/Author: 2026-06-19 / Codex.
- Decision: Leave repo `AGENTS.md` unchanged for now.
  Rationale: The user asked for an AutoPlan artifact, not a permanent repository operating-model update. Future adoption can add an AGENTS note after the AutoPlan is used for implementation.
  Date/Author: 2026-06-19 / Codex.
- Decision: Add a Development/Test-only local auth endpoint for mobile simulator work.
  Rationale: Mobile will use separate catalog and user-library base URLs. The iOS Simulator needs real user-library access/refresh tokens against `http://localhost:5119` without Apple/Google credentials. The endpoint is under `/api/v3/library/auth/development/session` and returns 404 outside Development/Test.
  Date/Author: 2026-06-19 / Codex.

## Outcomes & Retrospective

2026-06-19: Milestone 1 foundation slice landed in commit `b7cab47`. It added the separate `RelistenUserApi` project, test project, `/health`, protected `/api/v3/library/users/me`, snake-case serialization, no-store user-library headers, disabled-by-default auth, test-only auth override, and schema-qualified `user_data` bootstrap SQL. Remaining Milestone 1 work, if desired before auth promotion, is deployment packaging beyond local run/build shape.

2026-06-19: Milestone 2 auth/session first slice completed on branch `codex/user-library-auth`. It adds Postgres-backed `user_data` users/auth methods/sessions/refresh tokens, configured HMAC access tokens, opaque refresh-token rotation and reuse handling, Apple/Google-only provider callback seam, session list/revoke, and a Development/Test-only local token endpoint for mobile simulator development. Validation passed for `RelistenUserApiTests` 17 tests, existing `RelistenApiTests` 47 tests, full solution build, local Postgres schema smoke, `git diff --check`, and final reviewer pass with no findings.

2026-06-20: Milestone 3 playlist aggregate first slice completed on branch `codex/user-library-playlists`. It adds schema-qualified `user_data` playlist tables, authenticated `/api/v3/library/playlists` create/list/get endpoints, append-only `add_track`, append-only `add_tracks_as_block`, duplicate source-track support through distinct playlist-entry UUIDs, integer `block_position`, idempotent operation replay, deterministic conflict errors, owner-scoped reads, and high-signal HTTP/Postgres tests. Validation passed for focused playlist tests 14/14, `RelistenUserApiTests` 31 tests, existing `RelistenApiTests` 47 tests, full solution build, local Postgres schema smoke, `git diff --check`, and final reviewer pass with no findings.

2026-06-20: Milestone 3 share-token/mobile-access slice completed on branch `codex/user-library-share-tokens`. It adds schema-qualified share/access tables, owner-only share-token create/revoke, hashed URL tokens, signed-out mobile viewer exchange into device-bound short-lived grants, signed-in editor exchange into durable collaborator write access, follower-backed tokenless reopened links, owner/collaborator/follower/mobile-grant access resolution, and race-safe exchange/revoke locking. Validation passed for focused playlist/share-token tests 20/20, `RelistenUserApiTests` 37 tests, existing `RelistenApiTests` 47 tests, full solution build, local Postgres schema smoke, `git diff --check`, and final reviewer pass with no findings.

2026-06-20: Milestone 3 source-range/reorder slice completed on branch `codex/user-library-playlist-reorder`. It adds source-range-as-block through a narrow catalog resolver, placement-aware adds, `move_entry`, `move_block`, canonical playlist-position rewrites, integer block-position renumbering, non-applied deterministic replay statuses, empty-block cleanup, and a `playlist_entries` to `playlist_blocks` foreign key. Validation passed for focused playlist/share-token tests 29/29, `RelistenUserApiTests` 46 tests, existing `RelistenApiTests` 47 tests, full solution build with 0 warnings, local Postgres schema smoke, `git diff --check`, and final reviewer pass with no findings.

2026-06-20: Milestone 3 clone/collaboration slice completed on branch `codex/user-library-collaboration`. It adds playlist clone with new entry/block UUIDs, clone idempotency replay, direct collaborator invitation, invitee-only acceptance, collaborator write access, owner revocation, retry-safe revoke, and the documented invitation inbox accept route. Validation passed for focused share/collaboration tests 10/10, `RelistenUserApiTests` 50 tests, existing `RelistenApiTests` 47 tests, full solution build with 0 warnings, `git diff --check`, and reviewer findings were fixed with behavior coverage.

2026-06-20: Milestone 3 public cache/read-contract slice completed on branch `codex/user-library-playlist-cache`. It adds owner-only visibility updates, anonymous tokenless public playlist ETags and `304 Not Modified`, conservative `Vary` headers, no-store behavior for authenticated/private/unlisted/mobile-grant paths, explicit unsupported hydration behavior, and transition coverage for public-to-private follower access. Validation passed for focused playlist/share tests 40/40, `RelistenUserApiTests` 57 tests, existing `RelistenApiTests` 47 tests, full solution build with 0 warnings, `git diff --check`, and reviewer findings were fixed with behavior coverage.
