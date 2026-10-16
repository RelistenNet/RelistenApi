# Operate the ingestion worker

`RelistenIngestionWorker` runs workflows and activities using the API project's catalog services and parsers. It starts no API or Hangfire server. [Local setup](../../local-dev/README.md) uses the normal development PostgreSQL and Redis services. Tests create disposable databases and temporary Temporal servers. Use [the production cutover guide](production-cutover.md) for the staged handoff and live checks.

## Configuration

| Setting | Use |
|---|---|
| `DATABASE_URL` | Application catalog PostgreSQL URL |
| `REDIS_URL` | Catalog cache and local default for oversized Temporal payload bytes |
| `Temporal__PayloadRedisUrl` | Optional separate Redis for payload bytes. Production uses the persistent payload store; configure the same URL on API clients and workers. |
| `PGBOUNCER_WRITE_HOST`, `PGBOUNCER_READ_ONLY_HOST` | Optional existing database routing |
| `Temporal__Address` | Temporal gRPC endpoint; default `localhost:7233` |
| `Temporal__Namespace` | Namespace; default `default` |
| `Ingestion__Engine=Temporal` | API admission and Hangfire handoff switch; unset or `Hangfire` retains legacy admission |
| `Ingestion__Concurrency__ingest-archive=10` | Example provider activity concurrency override; each provider defaults to ten |
| `Ingestion__Rate__ingest-archive` | Optional queue activities-per-second limit |

`Ingestion__Engine` does not pause an already running worker. Pause schedules and stop admission in addition to canceling or draining work. Use one worker host initially. Each provider workflow admits at most ten item activities. The worker's provider concurrency setting limits how many activities execute across all artists on that worker; another host multiplies worker capacity. A batch separately allows ten active artist workflows. Queue rate settings must agree across pollers. They count activities, not individual HTTP requests.

Publish with `dotnet publish RelistenIngestionWorker -c Release`, or build with `docker build -f RelistenIngestionWorker/Dockerfile .`. Run `dotnet RelistenIngestionWorker.dll` from its publish directory. The API runs separately.

## Operator commands

Commands below run from the repository root. The published executable accepts the same flags.

```sh
# Resolve an artist UUID, numeric ID, or slug and refresh all enabled providers.
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead

# Refresh only Archive or import one of its recordings.
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead --provider=archive.org
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead --item=UPSTREAM_IDENTIFIER

# Preserve the legacy provider-specific year filters.
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead --year=2026

# Recover derived catalog data or run the explicit Jerry maintenance operation.
dotnet run --project RelistenIngestionWorker -- --rebuild-artist=grateful-dead
dotnet run --project RelistenIngestionWorker -- --repair-jerry-venues=grateful-dead

# Batch values: full, thin, rebuild, discover.
dotnet run --project RelistenIngestionWorker -- --batch=full

# Create missing schedules; preserve existing schedules, including paused ones.
dotnet run --project RelistenIngestionWorker -- --register-schedules

# Intentionally cancel the current execution using this artist workflow ID.
dotnet run --project RelistenIngestionWorker -- --cancel=ingest/artist/ARTIST_UUID
```

`--item` implies Archive. Combining it with another provider or with `--year` is invalid. With an explicit provider, year applies only to Archive, Phish.in, or Phish.net. Archive and Phish.net use an exact year; Phish.in uses a lower bound. Without an explicit provider, `--year` preserves those filters and runs other enabled providers normally. Targeted and year-filtered Archive imports do not reconcile unrelated recordings.

Every artist operation shares `ingest/artist/{uuid}`. An overlapping start reports a conflict containing that ID and exits unsuccessfully; it does not return somebody else's operation as success. Invalid or unsupported commands also exit unsuccessfully. Admitted commands print their workflow ID. The programmatic `ImportHandle` is tied to its admitted execution chain; the CLI's ID-only cancel command intentionally targets the current execution.

HTTP routes retain authorization and successful response shapes. Artist admission conflicts return `409` with code `import_already_running`, `workflowId`, and a message. Invalid requests and unsupported operations return `400` with their error code and message. Synchronous debug and destructive `deleteOldContent=true` routes retain their `409` protection when Temporal is selected. Listener catalog and playback responses remain unchanged.

## Inspect and recover

Workers resume open executions after a restart. A completed artist can have status `Partial`; inspect its failed-item count and provider failure details. Batch results distinguish completed, partial, failed, and conflicting artists. Prerequisite, discovery, expiration, cancellation, and rebuild failures are not successful partial results.

Each operation has a seven-day deadline shared across children and continuation runs. Do not reset or retry an old execution beyond that deadline. A fresh import is the normal recovery after item failures, cancellation, or expiry. A standalone artist rebuild repairs derived data without requiring upstream availability. Every completed artist import rebuilds, including when it selects no items.

An ordinary provider failure also triggers an artist rebuild before the workflow
reports failure, because earlier item activities may have committed catalog
changes. Cancellation and expiry skip that recovery rebuild. If recovery fails,
inspect the workflow history and run a standalone rebuild after resolving the
cause.

Cancellation reaches activities through heartbeats. HTTP accepts the cancellation token. Catalog transactions check it before applying writes and before commit. Different shows for one artist write concurrently; constraints and row locks coordinate shared records. Existing Dapper calls have bounded SQL timeouts; cancellation does not immediately interrupt every query. Rebuild SQL owns its transactions and may complete some work before observing cancellation. Wait for the worker's activities to stop before handing ingestion to another owner. Committed catalog changes remain committed.

Archive verification results include fixed deletion evidence in Temporal history. Only errcode 104 proves upstream deletion. The completeness checks and five-candidate guard apply independently of workflow status. A guarded result must remain visible in logs even if an external alert fails.

Watch queue wait, activity failures, upstream 429/5xx responses, Temporal history growth, and Redis eviction/persistence. HTTP requests have a 60-second timeout and 64 MiB response limit. Activity attempts have a five-minute timeout, five attempts, a ten-second heartbeat timeout, and six hours including queue/retry delays. Positive `Retry-After` values influence retry timing. These bounds do not establish production capacity; use the [queue guide](upstream-throttling.md) for tuning.

## Diagnostics and monitoring

The worker writes JSON console logs with Temporal and importer scopes. Use
`WorkflowId`, `ActivityId`, `Artist`, `Provider`, and `Attempt` to connect an
upstream response or item result to its workflow. Item results distinguish applied,
unchanged, and skipped inputs; they are not counts of newly created catalog rows.

The API initializes Sentry through `UseSentry` when `SENTRY_DSN` is configured.
Production supplies that setting to the API. The standalone ingestion worker
does not run the API's host setup: it currently neither initializes Sentry nor
receives `SENTRY_DSN` in its deployment. Existing importer calls to
`SentrySdk.CaptureException` therefore do not establish Sentry coverage there.
Temporal history and worker logs are the current sources for ingestion failures.

Scheduled-run monitoring remains unimplemented. Sentry's
[.NET Cron Monitoring](https://docs.sentry.io/platforms/dotnet/crons/)
supports check-ins for missed starts, excessive runtime, and reported failures.
The installed Sentry 6.0 SDK already exposes manual check-ins. A worker
integration must send external reports from activities, preserve the check-in
identity across retries and workflow continuations, and evaluate typed results:
a completed batch can contain partial or failed artist imports. Expected skips
and attempts that later succeed should not generate separate failure alerts.
Sentry initialization and deployment configuration are both required before
this coverage can be claimed.

## Switch from Hangfire

1. Stop manual ingestion admission. Disable or remove the three Hangfire ingestion recurring jobs: Archive discovery, full refresh, and thin refresh. Let existing work finish, or stop its process and verify that writes have ended.
2. Verify catalog migration history, Temporal namespace retention of at most seven days, and Redis persistence/eviction settings. This refactor adds no application ingestion tables. Existing catalog installations retain their normal migration history; do not use the disposable local baseline flag.
3. Set `Ingestion__Engine=Temporal` on both the `relistenapi` and `relistenapi-worker` deployments in the same rollout as the reviewed API image. Set the same Temporal address, namespace, and payload Redis URL on those deployments and the new ingestion worker. This stops ingestion recurring registration and the `artist_import` Hangfire server. Already queued ingestion methods return without importing in this mode. Popularity/cache jobs and the default Hangfire server remain.
4. Start one ingestion worker. Verify a manual import, then register schedules explicitly. Archive discovery runs at 06:30 UTC; full refresh at 07:00 UTC; thin refresh at 00:00, 02:00, 04:00, 12:00, 14:00, 16:00, 18:00, 20:00, and 22:00 UTC. Overlap policy is Skip. Repeated registration preserves existing schedules, including paused ones.

Stopping recurring registration does not remove jobs already stored in Hangfire. Drain both scheduled and manually admitted ingestion before switching. Artist workflow admission applies only to Temporal. It does not coordinate admission or finalization with Hangfire; drain the old engine before switching.

Rollback: pause Temporal schedules and manual admission, cancel/drain work, and stop the worker. Verify no activity is still writing before selecting Hangfire on the API. Review the old `artist_import` queue and discard obsolete queued work where appropriate. Restarting the API in Hangfire mode restores its recurring registrations. Keep committed catalog changes. Do not reset Temporal workflows while Hangfire owns ingestion.

Current tests start with fresh databases and Temporal servers; see [local setup](../../local-dev/README.md). In production, drain incompatible open workflow histories or adopt Temporal workflow versioning before changing workflow behavior.

## Storage, backups, and recovery

There are no application ingestion snapshot or receipt tables. Temporal stores workflow history in its own PostgreSQL persistence and visibility databases. The Redis codec stores complete serialized payloads larger than 32 KiB and no larger than 64 MiB. Content keys include the Temporal namespace and SHA-256 digest. Each successful encode or reuse refreshes a 15-day expiry. Decoding does not extend retention or delete bytes.

Fifteen days covers the seven-day execution limit, at most seven days of closed-history retention, and one day of margin. If closed-history retention must exceed seven days, increase payload retention by the same amount before adopting that policy. Saved schedules contain small commands rather than expiring payload references. Replay fixtures that outlive the TTL need their referenced bytes retained in isolated test storage or regenerated histories.

Production uses the separate `relisten-temporal-payloads` Redis with a persistent volume, AOF writes every second, and no eviction. API clients and workers must use its URL before creating production workflows. The default shared-cache setting remains suitable for disposable local tests. A crash can still lose the last second of AOF writes.

Redis loss or eviction can make referenced history unreadable. The codec reports missing, corrupt, oversized, or unsupported payloads; it never refetches upstream inputs to conceal the loss. There is no web codec endpoint, so Temporal UI shows references for offloaded values. Keep small command/result summaries and structured logs available for diagnosis.

If required bytes cannot be recovered, stop new admission and pause affected schedules. Attempt cancellation and drain running activities. If decoding prevents cancellation from being processed, stop the worker and verify that activity writes have ended before terminating the unreadable execution. Start fresh only after the old execution and its activities can no longer write. A fresh artist import or standalone rebuild repairs derived data. Do not delete arbitrary Redis keys as a recovery step.

Production logical backups dump only the `app` database. Temporal uses separate `temporal` and `temporal_visibility` databases in the same PostgreSQL cluster, so these dumps exclude workflow state without schema filters. The deployment adds no backups or archival for Temporal or its payload Redis. Persistent storage supports pod restarts; it is not a backup guarantee.

After catalog restoration or loss of Temporal/payload storage, keep ingestion stopped. Discard unmatched workflow state, recreate the namespace and schedules, and start fresh imports from the restored catalog. Do not resume old histories against a catalog or payload store restored to a different point. This recovery policy trades saved progress for simpler backups of rebuildable imports. A future physical PostgreSQL cluster backup would include all databases; it cannot exclude Temporal with `pg_dump` flags.

The manifests live in `clusters/relisten3-k3s/temporal/` in the infrastructure
repository. Until [infrastructure PR #22](https://github.com/RelistenNet/relisten-flux/pull/22)
merges, use its `codex/temporal-production` branch; a checkout of `main` may not
contain the bundle. Explicit versioned Jobs initialize the two SQL schemas and
the seven-day namespace. The server uses its own database role; frontend and UI
Services remain private. The cluster uses manual manifest application. Follow
that bundle's README and the [production cutover guide](production-cutover.md);
applying the bundle alone does not switch API admission or enable ingestion
schedules.
