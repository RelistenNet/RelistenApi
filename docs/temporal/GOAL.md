# Goal: typed provider workflows with reliable catalog retries

## Current implementation objective

Implement the [provider workflow plan](provider-workflow-plan.md) and [contract reference](provider-workflow-contracts.md). Make each provider's sequence understandable in its own workflow. Use named commands, typed provider keys and cursors, shared item activity composition, and reusable catalog rebuild activities. Preserve the seven active providers, catalog identities, listener API contracts, and independent Archive deletion verification. Keep PanicStream disabled.

The catalog is a rebuildable cache of upstream data. Accept occasional missed or stale metadata that a later sync can repair. Favor frequent refreshes over protocols for rare races. Preserve stable identities, atomic compound writes, and conservative deletion checks. Readability and the cost of adding a provider matter more than perfect per-sync completeness. The [architecture guide](architecture.md#cache-consistency-and-extension-choices) records this policy and its limits.

All artist operations must share one admission rule. An overlapping manual request reports a conflict and the active workflow ID. Batches retain one artist selection and UTC filter year, replace completed children up to ten active artists, and distinguish partial, failed, and conflicting results. Each provider workflow admits up to ten item activities concurrently. Fixed selections drain each segment of at most 100 items before checkpointing; paged providers drain the page before advancing. Providers retain their own pagination and finalization rules. Only an artist coordinator schedules its final shows → years → cache rebuild.

Use the existing Redis connection for oversized Temporal payloads. Keep complete payloads up to 256 KiB inline, reject those above 64 MiB, and retain offloaded bytes for 15 days from each successful encode or reuse. Preserve immutable selections separately from changing progress. Every execution and its children share a seven-day deadline. Redis loss can require a fresh import.

Remove the earlier application snapshots, commit receipts, and resume mechanism. Ordinary ReadCommitted catalog transactions reload current data and preserve existing identities through constraints and conflict handling. Temporal retries failed transactions with fresh importer state. Use row locks only for the record whose dependent rows are being replaced; retain the separate collection-index lock for artist creation. Retries must converge after a lost acknowledgement without claiming exact mutation counts. Related GETs remain together when they serve one item operation. Rebuild SQL retains its own transaction ownership.

This branch has never been deployed. Replace its draft workflow protocols and migration directly; no compatibility layer is required for them. Do not deploy, mutate production, add AI processing, or roll out search indexing.

## Readers and working standards

Write for Alec, Switz, and engineers who know Relisten as an app but may not know its whole technical stack. Explain responsibility, ownership, failure behavior, and the reasons behind decisions. Keep [the overview](README.md), [code map](architecture.md), [provider guide](provider-behavior.md), and [operator instructions](operations.md) usable without this conversation.

Follow repository `AGENTS.md`, `.editorconfig`, and pinned CSharpier. Check formatting before every commit and push. Prefer straightforward code, concrete interfaces, and direct data flow. Apply single responsibility and DRY where they clarify the code. Document public interfaces and explain constraints or trade-offs that code alone cannot express.

Use the plan as guidance. Resolve correctness and maintainability issues revealed by implementation and verification. Record consequential departures and their trade-offs. Use `deslop` for writing and `code-simplifier` after focused validation. Apply ASD-STE100 selectively where its direct wording helps.

Source readability is an acceptance criterion separate from tests and formatting. Workflow entrypoints must show named domain phases; helpers own iteration, durable state transitions, and finalization. Use braced control flow, conceptual blank lines, and comments explaining non-obvious policy. A fresh source reader must be able to trace success, partial failure, cancellation, and continuation without reconstructing a long mixed-responsibility function. The [readability follow-up](readability-follow-up.md) records the review and corrections.

Commit logical chunks. Every commit needs a title and body explaining what changed and why. Verify observable behavior through stable interfaces and contract boundaries. Tests must protect behavior rather than repeat configuration, search for strings, or mirror implementation details. Finish with an independent review for correctness, regressions, and missing tests.

## Current progress and verification

The typed commands, provider workflows, shared catalog steps, receipt-free transaction runner, payload codec, and updated API/CLI adapters are present on the branch. The guides describe these interfaces and operating constraints. The implementation plan owns the detailed progress and verification record.

**October 15 concurrency update: verified locally.** The implementation now allows bounded parallel item activities and uses ordinary catalog transactions. Read-only local and production schema inspection confirmed the required identity constraints; no new migration is needed. The full local-stack suite passed 261 tests with zero failures and 24 unrelated opt-in tour skips. Build, formatting, whitespace, and documentation links passed. A fresh review checked correctness and readability; its test-fixture findings are fixed. See [the concurrency update](provider-workflow-plan.md#concurrent-show-imports-october-15) for scope and verification.

The earlier cache-simplification pass passed 256 local-stack tests with zero failures and 24 unrelated opt-in tour skips. Solution build, formatting, whitespace, and documentation links passed. A fresh correctness and source-reading review found no actionable issue. See [the October 12 plan update](provider-workflow-plan.md#cache-oriented-simplification-october-12) for changes, trade-offs, and checks. The earlier [readability follow-up](readability-follow-up.md) and [245-test verification record](evidence/provider-workflow-verification.md) preserve baseline evidence, including Release publishes and CLI rejection checks. Those earlier checks did not include production verification or deployment.

## Historical work on this undeployed branch

Earlier revisions used a generic importer protocol, PostgreSQL discovery snapshots, and transactional result receipts. A readability pass replaced numbered fetch parts and the application resume journal with domain activities, then added typed discovery helpers and shared importer diagnostics. Those designs were useful intermediate steps; their storage and interface rules have been superseded by the current plan.

Recorded checks for those earlier revisions included an isolated suite with 135 passing tests, a provider-identifier follow-up with 149 passing tests, and a typed importer/behavior follow-up with 157 passing tests. Each full run recorded 24 unrelated opt-in skips. Earlier Release worker publishes and CSharpier checks also passed. These are historical results, not verification of the current implementation.

The retained decisions from that work are stable catalog identities, unchanged provider string values, shared Hangfire/Temporal domain behavior, independent Archive evidence checks, and importer SQL owned by data services. Test HTTP routing remains in the test project. The worker keeps standard diagnostics without requiring a Hangfire context or adding PII-enabled Sentry configuration.

The earlier design proposal remained under review until the provider workflow plan resolved the main choices: concrete provider workflows, shared activity composition for routine items, explicit maintenance commands, explicit admission conflicts, Redis payload offloading, and receipt-free retries. Planning is complete; this goal now covers implementation and verification.
