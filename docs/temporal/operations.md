# Operate the ingestion worker

`RelistenIngestionWorker` is a separate .NET executable. It runs workflows and provider activities, using the API project's catalog services and parsers. It starts no API server or Hangfire server. [Local setup](../../local-dev/temporal/README.md) runs the same worker registrations against isolated PostgreSQL, Redis and Temporal services.

## Configuration and entry points

| Setting | Use |
|---|---|
| `DATABASE_URL` | Application PostgreSQL URL; includes the catalog and migration 13's `ingestion_snapshots` and `ingestion_receipts` tables |
| `REDIS_URL` | Existing catalog cache Redis |
| `PGBOUNCER_WRITE_HOST`, `PGBOUNCER_READ_ONLY_HOST` | Optional existing database routing settings |
| `Temporal__Address` | Temporal gRPC endpoint; default `localhost:7233` |
| `Temporal__Namespace` | Namespace; default `default` |
| `Ingestion__Engine=Temporal` | API admission and Hangfire handoff switch; unset/`Hangfire` keeps legacy admission |
| `Ingestion__Concurrency__ingest-archive=10` | Example per-provider worker concurrency override; every provider defaults to 10 |
| `Ingestion__Rate__ingest-archive` | Optional queue activities/second limit; no default rate cap |
| `Ingestion__UpstreamBaseUrl` | Local fixture override; leave unset in ordinary operation |

The worker does not read `Ingestion__Engine` as a pause control. Stop its process or pause schedules/cancel workflows in Temporal. All pollers on a provider queue must use the same optional rate setting. An item activity may issue several sequential GETs. The optional rate is activities per second, not HTTP requests per second. There is no internal request fanout or custom limiter.

Publish with `dotnet publish RelistenIngestionWorker -c Release`, or build the worker image with `docker build -f RelistenIngestionWorker/Dockerfile .`. Run `dotnet RelistenIngestionWorker.dll` from the publish directory with the environment above. The API remains a separate process.

The following commands use `dotnet run --project RelistenIngestionWorker --` locally. A deployed executable accepts the same arguments.

```sh
# Resolve an artist by UUID, numeric ID or slug, then start a full import.
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead

# Archive-only exact item, or a legacy thin/year filter.
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead --provider=archive.org --item=UPSTREAM_IDENTIFIER
dotnet run --project RelistenIngestionWorker -- --import=grateful-dead --year=2026

# Catalog-wide entry points: full, thin, rebuild, discover.
dotnet run --project RelistenIngestionWorker -- --batch=full

# Cancel a workflow. Start a fresh artist/item import when ready to retry.
dotnet run --project RelistenIngestionWorker -- --cancel=ingest/artist/ARTIST_UUID
```

Temporal Archive item imports suppress artist-wide absence reconciliation; run a full import for verified cleanup of other items. The six non-Archive targeted importer methods retain their existing no-op behavior. `--year` also retains provider-specific legacy behavior; it is not a newly uniform year backfill API. Jerry venue backfill and Phish.in-only refresh remain available through the existing admin/import endpoints.

Full, targeted and rebuild artist workflows share `ingest/artist/{uuid}`. An overlapping start returns that running workflow. Read the actual `RequestId` from its Temporal input; a rejected overlapping start does not replace that request. Request IDs identify discovery/catalog snapshots and commit receipts across Temporal runs and Continue-As-New.

Workers automatically continue open workflows after a restart. Cancellation waits for the active activity to stop; heartbeats deliver cancellation during I/O, and item/catalog-preparation transactions check it before committing. Rebuild SQL owns its transactions and may finish before observing cancellation. Cancellation does not undo committed items. Start a fresh artist/item import after cancellation or a completed partial result. The next artist import rebuilds aggregates even if it finds no new changes.

Temporal reset can retry earlier work within the original seven-day deadline. Use it only before a newer import for that artist; resetting an old selection after newer work could apply stale inputs. A fresh import is the normal recovery path. There is no application-side resume command or progress journal.

## Switch from Hangfire

1. Disable/delete the three ingestion recurring jobs in the Hangfire dashboard: Archive artist discovery, full artist refresh and thin refresh. Stop manual admission during the handoff. Let current imports finish, or stop the old ingestion process.
2. Apply migration 13 through normal API startup or the worker's explicit `--migrate` command. Existing catalog installations use their recorded migration version; do not set the local baseline flag.
3. Set `Ingestion__Engine=Temporal` on the API and restart it. This stops registration of ingestion recurring jobs and the `artist_import` Hangfire server. Existing queued ingestion methods also return without importing when called in this mode. The default Hangfire server and popularity/cache jobs remain.
4. Start one ingestion worker. Run a manual import if useful, then run `--register-schedules`. It creates Archive discovery at 06:30 UTC, full refresh at 07:00 UTC and the existing nine daily thin slots. Existing schedules, including paused schedules, are left intact.

Stopping registration does not delete recurring jobs already stored by Hangfire; step 1 prevents them from accumulating unconsumed work. No per-artist rollout machinery or distributed ownership service is required. Run only one ingestion engine against the catalog at a time.

Rollback: pause Temporal schedules and manual admission, cancel/drain running workflows, and stop the worker before selecting Hangfire on the API. Verify the old `artist_import` queue before restarting it; discard obsolete queued imports if appropriate. Restarting the API in Hangfire mode restores its recurring registrations. Keep migration 13 and committed catalog changes. Temporal histories and unexpired receipts remain available for inspection. Do not reset old workflows while Hangfire owns ingestion.

Synchronous debug and `deleteOldContent=true` routes return HTTP 409 while Temporal is selected. Use the same stop/switch procedure for these exceptional legacy operations. They retain their old behavior in Hangfire mode and are never automatically retried by Temporal. Normal authenticated import routes retain HTTP 201 and their existing response shape, with a Temporal workflow ID in place of a Hangfire ID. Playback/catalog API models are unchanged.

## Inspect and recover

Temporal UI shows each artist, current activity, retry, cancellation, and batch result. `FailedItems > 0` means a partial result even when a workflow completed. Item identifiers appear in activity inputs and exhausted errors are logged. Discovery and prerequisite failures stop the workflow. Batch results count failed or overlapping artists.

Archive reconciliation returns its Present/Deleted/Unknown or no-MP3 eligibility decisions in the activity result and transactional receipt. Only Archive errcode 104 establishes upstream deletion; 404, an empty response, or a dark flag alone does not. Completeness checks and the existing five-candidate guard are separate from Temporal execution status.

Use one worker host, ten activities per provider, and ten artist children per batch. Watch queue wait, activity failures, upstream 429/5xx responses, application/Temporal database sizes, and cleanup errors. Catalog size alone does not establish hardware capacity. HTTP requests time out after 60 seconds and have a 64 MiB response-buffer bound. An activity attempt has a five-minute execution timeout, five attempts, a ten-second heartbeat timeout, and six hours including queue/retry delays. Adjust timeouts based on observed operation duration.

## Storage, backups and upgrades

Migration 13 creates two application tables:

- `ingestion_snapshots`: stable discovery selections and reused prerequisite catalogs.
- `ingestion_receipts`: catalog operation results committed atomically with their writes.

Fetched item metadata, HTML, and redirect hops are not persisted. There are no application request/failure journals or finalization markers. See [the architecture](architecture.md) for each provider's retained data.

A run expires after seven days; this deadline survives history rollover. Activities reject expired work and require a fresh import. Snapshots and receipts expire one day later. The worker removes expired rows at startup and hourly, logging cleanup failures for the next attempt. Temporal namespace history retention is separate. Do not manually remove unexpired receipts or inputs from a run that can still execute.

This branch has never been deployed. Migration 13 was replaced directly; there is no upgrade path from its earlier draft table. To reset a disposable local stack from that draft, use the reset instructions in [local setup](../../local-dev/temporal/README.md).

The local auto-setup image creates separate `temporal` and `temporal_visibility` databases. A hosted installation should use explicit schema setup/update jobs and separate database credentials. Protect the Temporal frontend/UI with the deployment's normal network/auth controls. Back up Temporal persistence and visibility plus application snapshots, receipts, and catalog data; an application-only restore cannot recover workflow histories. A restore must start with ingestion stopped, restore the matched data set, then verify namespace/workflow access and an open workflow before starting pollers.

Pin server and SDK versions. Follow Temporal's supported server upgrade order and run its PostgreSQL persistence/visibility schema updates for each upgrade. Drain open workflows before deploying incompatible workflow code, or use Temporal's workflow versioning APIs. Keep replay tests for histories that will remain open. This work supplies a worker image and local stack; it does not create production infrastructure, configure production backups or deploy anything.
