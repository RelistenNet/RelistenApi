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
