# Goal: make Temporal ingestion understandable and maintainable

Implement the agreed readability redesign on `codex/temporal-ingestion-plan`. This branch has never been deployed. Replace its internal interfaces, workflows, migrations, and recovery commands freely; do not retain compatibility layers for the first implementation.

The separate worker must support the seven active providers, scheduled and manual artist imports, targeted Archive reimports, rebuilds, and existing maintenance operations. Exclude PanicStream. Preserve catalog identities, public API contracts, upstream behavior, and independent Archive completeness/deletion verification. Use native provider queues with ten concurrent activities and ten active artist children per batch. Do not deploy or mutate production.

## Intended end state

- Workflow entrypoints show prepare, provider imports, and final rebuild. Named methods own pagination, item failures, and history rollover.
- One item activity normally fetches related GET inputs, parses them, and commits catalog changes. Split an operation only when its cost or recovery needs justify it. Remove numbered fetch parts and the one-HTTP-request rule.
- Prerequisite catalogs and reconciliation are explicit provider operations, not fake items or hidden string-key protocols.
- Keep fetched item responses in memory. Retain stable discovery/catalog snapshots only where repeated enumeration is expensive or the payload is too large. Retain compact transactional receipts for atomic retry results. Give retained data automatic cleanup and a bounded lifetime enforced by execution, so expired data cannot silently restart an old operation.
- Temporal owns execution progress and recovery. Remove the application request/progress/failure journal and the custom resume-from-PostgreSQL mechanism. Retry interrupted work through Temporal or start a fresh import.
- Carry the rebased Phantasy Tour existing-date repair into provider finalization after complete enumeration.

## Working standards

Follow repository `AGENTS.md`, `.editorconfig`, and pinned CSharpier. Run its formatting check before every commit and push. Add braces, conceptual whitespace, and clear names where a formatter cannot identify intent.

Favor straightforward, readable code. Apply single responsibility and DRY where they clarify responsibilities. Avoid speculative frameworks. Document public interfaces and explain local constraints and trade-offs when the code cannot. Write documentation, comments, and commit messages for readers without this conversation; use plain language and ASD-STE100 where useful.

Use the plan as guidance and adapt when implementation or verification reveals a better approach. Finish issues that affect correctness or maintainability and record consequential departures. Apply the local `deslop` and `code-simplifier` skills throughout.

Commit logical chunks, each with a title and body explaining what changed and why. Verify observable behavior through public interfaces and contract boundaries. Cover retries, cancellation, workflow rollover/replay, provider output, Archive deletion evidence, and Phantasy Tour date repair. Do not substitute string/configuration checks or tests that mirror the implementation.

## Completion and verification

The readability redesign is implemented on October 12, 2026. The branch is rebased on `codex/csharpier-format`; CSharpier and scoped explicit-brace rules cover the ingestion code. The separate worker, seven provider adapters, artist/item entrypoints, bounded batches, and cleanup service use the revised contracts. PanicStream remains excluded.

The local `deslop` and `code-simplifier` passes removed the generic fetch protocol, fake catalog items, duplicated item-failure handling, and application resume journal. The [execution graph and code map](architecture.md) describe the final design. [Operations](operations.md) describes commands, recovery, handoff, and storage retention.

Verification against the isolated local Temporal/PostgreSQL/Redis stack:

- `RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test RelistenApiTests/RelistenApiTests.csproj --nologo`: **135 passed, 24 skipped, 0 failed**. The skipped tests belong to the separate opt-in tour lookup fixture.
- All seven provider comparisons start from a cleared catalog while preserving the artist identity. They compare legacy and Temporal catalog output, including source, track, and show UUIDs.
- Tests cover receipts after lost acknowledgement, rollback and overlap, cancellation before commit, retry refetches, expiry/cleanup, 18,386-item bounded discovery, targeted imports, Archive deletion verification, workflow rollover/replay, worker replacement, and ten-child batch concurrency.
- Phantasy Tour tests verify existing local-date and tour-bound repairs without fetching known-show details, and suppress repair after incomplete discovery.
- `dotnet publish RelistenIngestionWorker/RelistenIngestionWorker.csproj -c Release` passed. `dotnet csharpier check .` checked 317 files; `git diff --check` passed.

The build still reports the existing NU1903 advisory for `OpenTelemetry.Resources.Host` 1.15.1-beta.1. Dependency remediation is outside this ingestion redesign.

## Decisions made during implementation

- A run has a seven-day deadline that survives ContinueAsNew. Discovery/catalog snapshots and receipts expire one day later; cleanup runs at worker startup and hourly. Expired work requires a fresh import. This bounds storage without adding a configurable retention/recovery framework.
- Stable listing selections remain outside Temporal history. Related item metadata, HTML, review responses, and redirect hops stay in memory and may be fetched again on retry.
- The existing aggregate rebuild SQL owns its transactions. Rebuild therefore runs as an idempotent activity without an outer receipt transaction. It always runs at the end of an artist import, including when no item changed, to repair aggregates after an interrupted import.
- The undeployed migration was replaced directly. There is no compatibility path for the previous draft schema, workflow protocol, or resume command. Public catalog/API behavior remains unchanged.

No production infrastructure, deployment, production mutation, AI normalization, embeddings, or search indexing is part of this implementation. Future work should preserve the standards above and update this goal when the intended outcome changes.

## Provider identifier follow-up

Use one `UpstreamProvider` string-serialized enum throughout workflow contracts, activities, importer dispatch, and source lookups. Preserve existing JSON strings, database names, queue names, and snapshot/receipt keys. The upstream-source database/API `name` field remains the wire representation; its `Provider` property supplies the typed value for application code. Preserve unknown source names in catalog responses while excluding them from importer dispatch. PanicStream remains a known historical source but has no active ingestion registration.

Verified this follow-up with 149 passing tests and 24 unrelated opt-in skips, plus a Release worker publish and CSharpier check. Provider tests round-trip the unchanged strings through Newtonsoft API JSON, Temporal payload conversion, and Dapper source mapping.

## Typed importer flow and behavior parity follow-up

Carry concrete types through discovery, immutable snapshots, item loading, and provider import methods. Use generic importer bases for shared Temporal mechanics, with provider-specific discovery types when reconciliation needs extra evidence. Keep JSON encoding in the persistence boundary; HTTP parsers still decode upstream responses. Manual import is an explicit entrypoint rather than a missing/empty metadata string.

Share Archive's selection, metadata validation, and deletion verification between engines. Preserve importer diagnostics through standard worker logging even without a Hangfire context, and attach execution context to Temporal activity logs. Explain intentional execution differences and remaining sink differences in [the provider behavior guide](provider-behavior.md).

Keep importer SQL in injected data services: source lookups, tour bounds, orphan venues, batch membership, and aggregate rebuilds. Preserve existing transaction ownership and public catalog behavior. The SQL extraction passed 25 persistence tests before its separate commit.

The completed follow-up passed the full isolated suite: **157 passed, 24 unrelated opt-in skips, 0 failed**. New paired Hangfire/Temporal tests cover Archive metadata skips, date repair diagnostics, selection reasons, and guarded deletions with a fake notification sink. Release worker publish passed. Standard worker logging includes execution scopes; external Sentry initialization remains unchanged.

## Test transport isolation follow-up

Keep fixture routing and other test setup in the test project. Production HTTP code uses standard dependency injection for its transport and contains no test-only endpoint settings or request rewriting. Preserve activity cancellation, timeouts, upstream retry delays, redirects, and importer behavior.

Verified with six HTTP tests and 33 local ingestion integration tests, including all seven provider comparisons. The HTTP tests cover client cancellation/disposal, redirects, retry delays, and provider response parsing. CSharpier checked 326 files.
