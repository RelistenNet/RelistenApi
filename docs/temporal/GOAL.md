# Goal: run Relisten ingestion in a separate Temporal worker

Implement the agreed Temporal ingestion design in a standalone `RelistenIngestionWorker` .NET entry point. Share existing importer services and catalog code with the API. Support the seven active providers, recurring artist discovery/full/thin imports, manual artist/item work, rebuilds and existing maintenance operations. Preserve catalog identities, API contracts and Archive deletion verification. Exclude PanicStream.

Use self-hosted Temporal with PostgreSQL, native provider task queues, ten-concurrent provider workers and ten active artists per batch. Keep rate caps configurable without arbitrary low defaults. Compose shared activities through artist and manual-item workflows. Keep histories bounded and retries safe. Retain Hangfire for unrelated jobs and a simple operator-controlled ingestion handoff/rollback. Do not add distributed rate limiters, write-ownership infrastructure, HA, AI/search work or a staged deployment program.

Implementation is authorized. Production deployment and production mutations are not part of this work. Work in the main RelistenApi checkout on `codex/temporal-ingestion-plan`, based on master; preserve unrelated work and running release/reindex jobs.

## Working standards

- Use the plan as guidance and adapt when code or verification reveals a better approach. Record consequential departures and their tradeoffs here.
- Finish correctness and maintainability issues exposed by the implementation when they affect the intended outcome.
- Commit logical chunks with a title and body explaining the change and its reason for readers without this conversation.
- Prefer direct, readable code. Apply single responsibility and DRY where they clarify real responsibilities; avoid frameworks and abstractions without a concrete consumer.
- Document public interfaces. Add file-level context and intent/constraint/tradeoff comments where code alone is insufficient.
- Test observable behavior through public interfaces and contract boundaries. Do not use string/configuration assertions or tests that copy implementation logic as substitutes for behavior tests.
- Write documentation, comments, commits and PR descriptions for their readers. Use plain language and ASD-STE100 where useful.
- Apply the local `deslop` skill to substantive writing and `code-simplifier` after focused validation of nontrivial code changes. Rerun affected checks after cleanup.

## Completion criteria

The separate worker builds and starts independently of the API/Hangfire host. Workflows and provider activities run against an isolated local Temporal/PostgreSQL environment. API/manual and recurring admission can select Temporal while legacy ingestion remains available for handoff. Tests verify provider behavior, duplicate/retry handling, interruption/resume, cancellation, Archive deletion policy and history rollover. Local setup and operator handoff/rollback instructions are reproducible. All implementation work is committed, with remaining external/environment limitations reported explicitly.

## Progress

- Standalone worker, shared provider adapters, API handoff, schedules and maintenance entry points are implemented.
- All seven active providers have local legacy/Temporal catalog comparisons. Other checks cover receipts, rollback, overlapping attempts, cancellation, worker replacement, history replay/rollover, Archive evidence, API admission and maintenance.
- The worker publishes and starts independently against local Temporal, then stops cleanly on SIGINT. Local schedule registration and migration 13 were also exercised.
- Implementation and verification are complete and committed in logical chunks. No production deployment or production data changes occurred.

## Decisions and implementation departures

- Use application PostgreSQL for raw inputs and receipts initially. This keeps Temporal histories small without adding object storage. Retention is explicit operator work; [operations.md](operations.md) explains what can be removed and what must remain for recovery.
- The historical migrations contain manual-operation markers and cannot rebuild the current catalog. The isolated local stack therefore includes a schema-only fixture from the read-only replica and an explicit local baseline of 12 before migration 13. It contains no production rows or credentials.
- Fetch and apply are separate activities for Archive as well as the other providers. Native queues control upstream requests; catalog writes and receipts share a transaction. Bulk page staging avoids one database round-trip per discovered item.
- Reuse existing parsers with a null Hangfire progress context. Temporal activities and persisted results provide progress/error visibility; a new progress-adapter abstraction was unnecessary.
- Artist, item and rebuild starts share an artist UUID workflow ID. Cancellation leaves a durable `needs-finalize` marker when item changes committed. Resume is an operator action before newer work for that artist, not an additional ownership/fencing service.
- Temporal exact-item imports suppress unrelated absence reconciliation. The legacy Archive targeted path could run artist-wide cleanup. Full imports retain verified reconciliation; a narrow reimport now changes only its selected item. The compatibility tradeoff is documented in the operator instructions.
- Debug and destructive reimports remain on the legacy path. While Temporal is selected they return 409; stop ingestion and select Hangfire to use them. Normal import responses retain their status and shape and return a Temporal workflow ID.
- Fixes exposed by verification: Phish.net discarded normal review envelopes; Archive nullable list fields did not round-trip through staging; source updates assumed every artist used per-source venues; discovery held overlapping database connections and promoted its transaction unnecessarily. These fixes preserve catalog intent and have contract coverage.
- API-only popularity dependencies are lazy so the worker can use catalog writes without constructing a Hangfire client. Hangfire remains responsible for unrelated popularity/cache jobs.
- A public HTTP status check confirmed that Phish.net's date-based ratings URL redirects. Redirects are now staged and followed in separate fetch activities, preserving both the old URL behavior and one-request queue accounting.
- `deslop` was applied to the goal/runbook and commit messages. `code-simplifier` removed redundant legacy guards, shared fixture setup, cleaned extraction residue and kept provider parsing in existing methods.

## Verification evidence

- Baseline: 93 passed, 24 pre-existing database-dependent skips.
- Complete suite: 126 passed, 24 pre-existing skips. After the final rolling-batch change, all eight workflow tests passed again, including batch history replay. After the Phish.net redirect fix, all seven provider comparisons and five HTTP tests passed again.
- Provider/discovery/maintenance regression set: 11 passed after the connection-lifetime fix.
- Synthetic 18,386-item discovery: 0.32 seconds to stage on this local machine, 4,456,759 stored payload bytes, 56-character page reference returned. This uses tiny synthetic documents and excludes indexes/WAL; it is not a production capacity estimate.
- Worker Release publish, independent startup/shutdown, local schema migration and explicit schedule creation succeeded.
- Existing `OpenTelemetry.Resources.Host` 1.15.1-beta.1 reports NU1903. It predates this work. No live-provider load test, production rollout, backup setup or infrastructure creation was performed.

## Commits

- `7b5ad9c`: goal and planning documents.
- `0f6fb1a`: independent host and shared catalog registration.
- `5637d06`: durable artist/item workflow core, staging and receipts.
- `db6dc86`: active provider extraction and catalog fixes.
- `0af1d55`: API handoff, worker commands, scheduling and recovery.
- `d94294c`: isolated stack, contract tests and rolling ten-artist batches.
- `71d7b36`: Phish.net redirects as individually queued fetches.

[Operator instructions](operations.md) · [Local setup](../../local-dev/temporal/README.md)
