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

## Progress

- Formatting rebase is complete. The first implementation is the starting point, not the accepted end state.
- Readability redesign is in progress. Current verification and remaining issues will be recorded as each chunk completes.
