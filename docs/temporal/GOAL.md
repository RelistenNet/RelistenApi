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

- Planning and evidence complete; implementation started.
- Main checkout is on the intended branch with only this task's planning artifacts present.
- Read repository instructions and the requested deslop/code-simplifier skills.
- Next: inspect shared importer/DI boundaries, establish a baseline build/test, and implement the worker plus first end-to-end import.

## Decisions and verification

Record implementation departures, completed commits and meaningful test results here as work proceeds.

- Foundation: added the standalone .NET host, pinned Temporal SDK 1.20.0 and shared catalog registrations without API/Hangfire startup. Removed PanicStream registration and ignore unsupported provider mappings.
- Baseline and foundation checks: worker build passed; existing API suite passed (93 passed, 24 existing database-dependent tests skipped). Existing OpenTelemetry.Resources.Host dependency reports NU1903; not introduced by Temporal.
