# Provider workflow verification — October 12, 2026

This record covers the undeployed provider-workflow refactor. It uses synthetic upstream responses and the dedicated `local-dev/temporal` stack. No production catalog, provider account, or deployment was changed.

## Environment and reproducible checks

.NET SDK 10.0.100; Temporal .NET SDK 1.20.0; Compose Temporal server 1.29.1; PostgreSQL 17.10; Redis 7.4. Workflow-only tests also start the SDK's ephemeral local Temporal server. The SDK manages that binary; it is not a production server pin.

Run the setup in [the local guide](../../../local-dev/temporal/README.md), then:

```sh
dotnet build RelistenApi.sln --nologo
RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test RelistenApiTests/RelistenApiTests.csproj --no-build --nologo
dotnet publish RelistenApi/RelistenApi.csproj -c Release --nologo
dotnet publish RelistenIngestionWorker/RelistenIngestionWorker.csproj -c Release --nologo
dotnet csharpier check .
git diff --check
```

The solution build and both Release publishes passed. CSharpier checked 362 files. The existing `OpenTelemetry.Resources.Host` 1.15.1-beta.1 dependency reports NU1903; API publish also reports the existing unset runtime-identifier warning. No package versions were changed.

Final clean-stack test result: **245 passed, 0 failed, 24 skipped; 269 total**, in 2 minutes 5 seconds. The 24 skips belong to the unrelated tour fixture, which requires `RELISTEN_TOUR_TEST_DATABASE_URL`; every enabled ingestion case ran. The first integrated run had 243 passes and two test-only exception-type assertion failures. After correcting those assertions, the complete suite passed on the reset stack.

## Behavior covered

| Boundary | Evidence |
|---|---|
| HTTP admission | 17 tests use a real HTTP server, MVC routes, authorization, catalog, and Temporal admission. They preserve success bodies, reject unsupported operations, and expose the active workflow ID on conflicts. Authentication uses a test handler; credential issuance is outside these tests. |
| Commands and handles | Actual Temporal serialization covers typed commands, keys, provider values, dates, and closed unions. Handle tests follow continuation and prove that an old handle cannot act on a later execution reusing the artist workflow ID. |
| Provider catalog writes | All seven providers have paired Hangfire/Temporal fixture comparisons. Repeated selected-item activities preserve returned and stored identities. Archive additionally refetches changed metadata after a lost acknowledgement. Phish.net tests change source date and review prerequisites during HTTP work, reject stale writes, then retry while preserving an unrelated recording edit. |
| Transaction ownership | PostgreSQL tests exercise rollback, cancellation/expiration, primary reads, transaction lock lifetime after connection-wrapper disposal, overlapping era preparation, collection serialization, and stable setlist/performance identity. |
| Archive policy | Tests cover missing/dark metadata diagnostics, date repair, guard notifications, direct Present/Deleted/Unknown evidence, filtered/targeted scope, and repeated application of a fixed deletion list. |
| Durable progress | A 180-item provider selection crosses continuation without rediscovery. A 122-item page crosses continuation with its remaining entries intact. Empty pages with a next cursor advance. A 251-artist batch selects once, drains before continuation, and stays within ten active children. Histories are replayed. |
| Failure and cancellation | Tests bound failed-item samples without losing totals, stop after prerequisite failures, keep partial results, and wait for both fast and slow child cancellation. Delayed first polling consumes the original seven-day budget. |
| Derived catalog | Provider comparisons inspect source/show/year output. Workflow tests retry years and cache independently without repeating earlier completed steps; an empty import still rebuilds. |
| Redis transport | 20 codec tests cover threshold, reference validation, missing/corrupt values, write failure, atomic reuse, and expiry. A real Temporal test passes a 3 MiB result into another activity after worker restart, continues through three runs with one retained key, refreshes its TTL, and replays every run with the codec. |

The tests generate fresh histories and synthetic payload bytes on each run; they do not depend on a retained history dump or expiring external fixture. See `TestRedisPayloadWorkflowTransport`, `TestIngestionWorkflows`, `TestPagedProviderWorkflow`, and `TestBatchContinuation` in `RelistenApiTests/Ingestion/` to reproduce restart and replay.

## Local migration and recovery checks

The original disposable database still contained the earlier ingestion tables. After the first integrated run, the dedicated Compose project was stopped with `down --volumes` and recreated from its schema fixture. The documented worker `--migrate` command with local baseline 12 succeeded. `versioninfo` recorded version 12, and PostgreSQL reported no `ingestion_snapshots` or `ingestion_receipts` tables. The final suite passed against that clean setup. Services were then stopped without deleting the verified local database volume.

The published CLI rejects mutually exclusive operations and provider/year/item modifiers without `--import`; both smoke commands returned exit code 1 and an explanatory stderr message before admission.

Synthetic failure/recovery sequence: the codec tests delete or corrupt stored bytes and require explicit decode failure, with no substituted input. The transport test stops a worker after the large result and next command reach history, restarts it, then consumes the same bytes and replays the chain. Operational recovery from irrecoverable bytes remains stop/drain/terminate/fresh import as documented in [operations](../operations.md); no production termination was exercised.

## Review and limits

A fresh reviewer found two lifecycle defects: batch cancellation returned before all children settled, and deadline assignment omitted initial queue delay. Both were fixed and covered by regression tests. The follow-up found no further production defect. It also caught a test expecting an internal exception rather than the activity's retryable Temporal failure; the assertion now checks the public activity boundary.

Provider implementation and test owners applied simplification passes. The final root pass removed duplicate CLI validation and reconciled the guides with the actual generic handle and bounded SQL cancellation behavior.

This verification does not measure full-catalog throughput or an 18,386-item production selection. Namespace retention, target schema, infrastructure capacity, live upstream behavior, schedule handoff, and production smoke imports remain rollout checks. Redis availability loss is an accepted limit, not durable artifact storage. The dedicated test suite does not establish production readiness by itself.
