# Operate the ingestion worker

`RelistenIngestionWorker` is a separate .NET executable. It runs workflows and provider activities, using the API project's catalog services and parsers. It starts no API server or Hangfire server. [Local setup](../../local-dev/temporal/README.md) runs the same worker registrations against isolated PostgreSQL, Redis and Temporal services.

## Configuration and entry points

| Setting | Use |
|---|---|
| `DATABASE_URL` | Application PostgreSQL URL; includes the catalog and migration 13's `ingestion_records` table |
| `REDIS_URL` | Existing catalog cache Redis |
| `PGBOUNCER_WRITE_HOST`, `PGBOUNCER_READ_ONLY_HOST` | Optional existing database routing settings |
| `Temporal__Address` | Temporal gRPC endpoint; default `localhost:7233` |
| `Temporal__Namespace` | Namespace; default `default` |
| `Ingestion__Engine=Temporal` | API admission and Hangfire handoff switch; unset/`Hangfire` keeps legacy admission |
| `Ingestion__Concurrency__ingest-archive=10` | Example per-provider worker concurrency override; every provider defaults to 10 |
| `Ingestion__Rate__ingest-archive` | Optional queue activities/second limit; no default rate cap |
| `Ingestion__UpstreamBaseUrl` | Local fixture override; leave unset in ordinary operation |

The worker does not read `Ingestion__Engine` as a pause control. Stop its process or pause schedules/cancel workflows in Temporal. All pollers on a provider queue must use the same optional rate setting. One activity attempt performs at most one upstream request; redirects and nested HTTP retries cannot bypass the queue.

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

# Cancel a workflow, then resume an unfinished artist request from staged inputs.
dotnet run --project RelistenIngestionWorker -- --cancel=ingest/artist/ARTIST_UUID
dotnet run --project RelistenIngestionWorker -- --resume=REQUEST_ID
```

Temporal Archive item imports suppress artist-wide absence reconciliation; run a full import for verified cleanup of other items. The six non-Archive targeted importer methods retain their existing no-op behavior. `--year` also retains provider-specific legacy behavior; it is not a newly uniform year backfill API. Jerry venue backfill and Phish.in-only refresh remain available through the existing admin/import endpoints.

Full, targeted and rebuild artist workflows share `ingest/artist/{uuid}`. An overlapping start returns that running workflow. Read the actual `RequestId` from its Temporal input; a rejected overlapping start does not replace that request. Request IDs identify staged input and commit receipts across Temporal runs and Continue-As-New.

Cancellation waits for the current activity to finish or acknowledge cancellation. SQL work can finish before cancellation is observed. Canceling never undoes committed items. `--resume` restarts an unfinished artist request and skips its committed writes through receipts. Use it before a newer import for that artist: resuming an old staged snapshot after newer work can reapply stale unfinished items. Pause admission while making that recovery choice. A completed partial result needs a fresh artist/item import, not `--resume`. For a canceled batch, start another batch; committed catalog data remains.

## Switch from Hangfire

1. Disable/delete the three ingestion recurring jobs in the Hangfire dashboard: Archive artist discovery, full artist refresh and thin refresh. Stop manual admission during the handoff. Let current imports finish, or stop the old ingestion process.
2. Apply migration 13 through normal API startup or the worker's explicit `--migrate` command. Existing catalog installations use their recorded migration version; do not set the local baseline flag.
3. Set `Ingestion__Engine=Temporal` on the API and restart it. This stops registration of ingestion recurring jobs and the `artist_import` Hangfire server. Existing queued ingestion methods also return without importing when called in this mode. The default Hangfire server and popularity/cache jobs remain.
4. Start one ingestion worker. Run a manual import if useful, then run `--register-schedules`. It creates Archive discovery at 06:30 UTC, full refresh at 07:00 UTC and the existing nine daily thin slots. Existing schedules, including paused schedules, are left intact.

Stopping registration does not delete recurring jobs already stored by Hangfire; step 1 prevents them from accumulating unconsumed work. No per-artist rollout machinery or distributed ownership service is required. Run only one ingestion engine against the catalog at a time.

Rollback: pause Temporal schedules and manual admission, cancel/drain running workflows, and stop the worker before selecting Hangfire on the API. Verify the old `artist_import` queue before restarting it; discard obsolete queued imports if appropriate. Restarting the API in Hangfire mode restores its recurring registrations. Keep migration 13 and committed catalog changes. Unfinished Temporal records remain available for inspection, but do not resume them while Hangfire owns ingestion.

Synchronous debug and `deleteOldContent=true` routes return HTTP 409 while Temporal is selected. Use the same stop/switch procedure for these exceptional legacy operations. They retain their old behavior in Hangfire mode and are never automatically retried by Temporal. Normal authenticated import routes retain HTTP 201 and their existing response shape, with a Temporal workflow ID in place of a Hangfire ID. Playback/catalog API models are unchanged.

## Inspect and recover

Temporal UI shows each artist, current activity, retry, cancellation and batch result. `FailedItems > 0` is a partial result even when the workflow completed. Batch results count failed or overlapping artists. Exhausted item errors also appear in `ingestion_records`; discovery and prerequisite failures stop the workflow. Archive deletion evidence records Present/Deleted/Unknown or confirmed no-MP3 eligibility separately from Temporal status. Only Archive errcode 104 establishes deletion; 404, an empty body or a dark flag alone does not.

`ingestion_records` stores request inputs, staged pages/items/fetches, commit receipts, errors and final results. It lives in the application database. Bodies do not enter Temporal history. Item writes and their receipts commit in the same transaction. A `needs-finalize` record without a `finished` record means committed changes still need an artist resume/rebuild. This query finds those requests:

```sql
SELECT r.request_id, r.updated_at
FROM ingestion_records r
WHERE r.record_key = 'needs-finalize'
  AND NOT EXISTS (
    SELECT 1 FROM ingestion_records f
    WHERE f.request_id = r.request_id AND f.record_key = 'finished'
  );
```

Start with one worker host, ten activities per provider and ten artist children per batch. Watch activity failures, queue wait, upstream 429/5xx responses, application/Temporal PostgreSQL sizes, and unfinished requests. There is no claim that catalog size alone establishes server capacity. Fetches have a 60-second HTTP timeout and a 64 MiB response buffer bound. Activity attempts have a five-minute execution timeout, five attempts, and a six-hour total queue/retry allowance; rebuilds that exceed five minutes need an explicit timeout adjustment based on their observed duration.

## Storage, backups and upgrades

Staged bodies initially use application PostgreSQL to avoid another service. No automatic artifact deletion is enabled. Inspect growth and choose retention with the recovery window in mind. After a request is finished and no longer needed for audit, all its rows can be removed together. Never prune unfinished requests or individual receipts while they can resume. Batch artist-selection and discovery records have no artist `finished` receipt; remove those only after confirming the corresponding Temporal workflow has closed. Temporal namespace history retention does not clean application artifacts.

The local auto-setup image creates separate `temporal` and `temporal_visibility` databases. A hosted installation should use explicit schema setup/update jobs and separate database credentials. Protect the Temporal frontend/UI with the deployment's normal network/auth controls. Back up Temporal persistence and visibility plus application ingestion records and catalog data; an application-only restore cannot recover workflow histories. A restore must start with ingestion stopped, restore the matched data set, then verify namespace/workflow access and a resumable request before starting pollers.

Pin server and SDK versions. Follow Temporal's supported server upgrade order and run its PostgreSQL persistence/visibility schema updates for each upgrade. Drain open workflows before deploying incompatible workflow code, or use Temporal's workflow versioning APIs. Keep replay tests for histories that will remain open. This work supplies a worker image and local stack; it does not create production infrastructure, configure production backups or deploy anything.
