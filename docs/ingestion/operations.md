# Operate the ingestion worker

`RelistenIngestionWorker` runs workflows and activities using the API project's catalog
services and parsers. It starts no API or Hangfire server. [Local
setup](../../local-dev/README.md) uses the normal development PostgreSQL and Redis
services. Tests create disposable databases and temporary Temporal servers. Use
[deployment](#deploy-or-update) for release checks. Temporal is the sole ingestion engine; Hangfire continues to
serve cache and popularity jobs.

## Configuration

| Setting | Use |
|---|---|
| `DATABASE_URL` | Application catalog PostgreSQL URL |
| `SENTRY_DSN` | Optional Sentry error reporting; omitted for local development |
| `PHISHIN_KEY` | Phish.in API bearer token |
| `PHISHNET_API_KEY` | Phish.net API v5 key, sent in the `X-API-KEY` header. Phish.net imports fail without it. Production reads it from `relistenapi-secrets`. |
| `REDIS_URL` | Catalog cache and local default for oversized Temporal payload bytes |
| `Temporal__PayloadRedisUrl` | Optional separate Redis for payload bytes. Production uses the persistent payload store; configure the same URL on API clients and workers. |
| `PGBOUNCER_WRITE_HOST`, `PGBOUNCER_READ_ONLY_HOST` | Optional existing database routing |
| `Temporal__Address` | Temporal gRPC endpoint; default `localhost:7233` |
| `Temporal__Namespace` | Namespace; default `default` |
| `Temporal__WebUrl` | Browser UI base URL for admin execution links; default `http://localhost:8233`, with the private Tailscale UI configured in production |
| `ACCOUNTS_DATABASE_URL` | Accounts PostgreSQL URL. Enables the `user-data` queue (account deletion) and the `purge-account` command. Without it the worker logs a warning and does not poll `user-data`. |
| `Ingestion__Concurrency__ingest-archive=10` | Example provider activity concurrency override; each provider defaults to ten |
| `Ingestion__Rate__ingest-archive` | Optional queue activities-per-second limit |

Ingestion HTTP routes always admit Temporal work. There is no engine-selection
flag. To stop ingestion, stop admission and pause Temporal schedules; stopping a
poller alone does not close running workflows.

### Concurrency and schedules

A batch admits ten artists. Each provider workflow admits up to ten item
activities, and each worker's provider queue executes ten activities concurrently
by default across all artists. One large artist can therefore use the worker.
These are separate bounds; another host multiplies worker concurrency.

Provider queues are `ingest-archive`, `ingest-phishin`, `ingest-phishnet`,
`ingest-setlistfm`, `ingest-phantasytour`, `ingest-jerry`, and `ingest-local`.
Shared catalog activities use `ingest-db`; workflows use `ingest-workflows`.
Account deletion uses `user-data`, which runs only with `ACCOUNTS_DATABASE_URL`.
PanicStream is unsupported. Eager activity execution is disabled so item work
passes through the configured queues.

Queue rate limits count activities, not HTTP requests. One item activity can
fetch several related resources. All pollers on a queue must agree on its rate
setting. Positive `Retry-After` values influence activity retry delays. Reduce
concurrency or the queue rate when sustained throttling or database load requires
it; use the existing settings before adding another limiter.

Schedules use UTC and skip overlapping runs:

| Batch | Time |
| --- | --- |
| Archive artist discovery | 06:30 daily |
| Full catalog refresh | 07:00 daily |
| Recently active artists, or thin refresh | 00:00, 02:00, 04:00, 12:00, 14:00, 16:00, 18:00, 20:00, 22:00 |
| Catalog search full build | 11:00 daily |

Thin refresh selection uses `ArtistService.UuidsForImport` and provider-specific
year rules. A small local seed does not limit a full artist import.

`--register-schedules` creates `relisten-search-build` paused. Enable it after
the production index and endpoint have been verified. Manual and scheduled
builds use the same workflow ID, so they can't run at the same time.

## Run the worker

Publish with `dotnet publish RelistenIngestionWorker -c Release`, or build with `docker
build -f RelistenIngestionWorker/Dockerfile .`. Run `dotnet RelistenIngestionWorker.dll`
from its publish directory. The API runs separately.

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

# Build or extend an artist's norm song catalogue with the LLM, then match its tracks
# (workflow norm/songs/ARTIST_UUID). Needs OpenAI:Model and OpenAI:ApiKey on the worker.
# Only titles nothing resolves yet are sent; existing rows are never changed. --max-chunks
# caps the model calls of one run (about 50 title groups each).
dotnet run --project RelistenIngestionWorker -- --norm-songs=phish --max-chunks=5

# Ask again about titles whose answers look wrong (an expert's list): their machine
# decisions are forgotten first (people's edits stay), and only they are asked.
dotnet run --project RelistenIngestionWorker -- --norm-songs=phish --reask="Let Me Lie|Mellow Mood"
dotnet run --project RelistenIngestionWorker -- --norm-songs=phish --reask-file=titles.txt

# Venues (switched-on artists only; --norm-venues=all is every one of them). Place again
# every row an older matcher placed, after a matcher fix: rows placed right land on the
# venue they had, and venues left with nothing are retired. People's placements stay.
dotnet run --project RelistenIngestionWorker -- --norm-venues=phish --rematch

# Forget the machine's placement of specific rows and leave them to the model this run:
# "Name@Location" as the row writes its town, or a bare name for any town.
dotnet run --project RelistenIngestionWorker -- --norm-venues=phish --reask="The Greek Theatre@Redlands, CA"

# Batch values: full, thin, rebuild, discover.
dotnet run --project RelistenIngestionWorker -- --batch=full

# Create missing schedules; preserve existing schedules, including paused ones.
dotnet run --project RelistenIngestionWorker -- --register-schedules

# Intentionally cancel the current execution using this artist workflow ID.
dotnet run --project RelistenIngestionWorker -- --cancel=ingest/artist/ARTIST_UUID

# After a backup restore, purge an account deleted after the backup was taken.
# Runs the account-deletion SQL by user ID without a job row. Needs ACCOUNTS_DATABASE_URL.
dotnet run --project RelistenIngestionWorker -- purge-account --user user_XXXXXXXX

# Build playlist search from the accounts and catalog databases, then publish its alias.
dotnet run --project RelistenIngestionWorker -- --playlist-search-build=true
```

Playlist search writes use the `user-data` worker and both database connections.
The User Service signals the playlist's workflow after saving changes. That
workflow waits one minute after the playlist's latest signal and caps the wait
at five minutes after the first signal. Reindexing reads the current playlist,
updates its document or deletes it if gone, and exits once it has handled its
pending edits.
If sync exhausts its activity retries, the workflow waits two minutes and carries
its pending edits into a new run under the same workflow ID.
The nightly playlist build repairs missed signals and updates follow counts
and catalog names. Playlist syncs record requests in the catalog database
before resolving the playlist alias. After publication, the build replays the
requests made since it started and drops older requests. Catalog migration 23
creates the request table. Schedule registration creates
`relisten-playlist-search-build` paused.

Account-deletion activities retry Typesense cleanup after the database commit.
The restore replay command can run with the accounts database alone. Set
`Search__Typesense__Url` and the writer key when the command should also remove
documents from a live playlist index.

`--item` implies Archive. Combining it with another provider or with `--year` is
invalid. With an explicit provider, year applies only to Archive, Phish.in, or
Phish.net. Archive and Phish.net use an exact year; Phish.in uses a lower bound. Without
an explicit provider, `--year` preserves those filters and runs other enabled providers
normally. Targeted and year-filtered Archive imports do not reconcile unrelated
recordings.

Every artist operation shares `ingest/artist/{uuid}`. An overlapping start reports a
conflict containing that ID and exits unsuccessfully; it does not return somebody else's
operation as success. Invalid or unsupported commands also exit unsuccessfully. Admitted
commands print their workflow ID. The programmatic `ImportHandle` is tied to its
admitted execution chain; the CLI's ID-only cancel command intentionally targets the
current execution.

### HTTP admission

The [import](../../RelistenApi/Controllers/Import/ImportController.cs) and
[admin](../../RelistenApi/Controllers/Admin/AdminController.cs) routes retain
authorization and successful response shapes. Routes returning `201` with
`Queued as job {id}!` keep that body; the ID is the admitted workflow ID. Other
routes return their existing ID or JSON bodies. Listener catalog and playback
responses are unchanged.

Artist admission conflicts return `409` with `code: import_already_running`,
`workflowId`, and a message. Invalid requests return `400` with
`code: invalid_import_request`; unsupported operations return `400` with
`code: unsupported_import_operation`. Both include a plain-language message.
A missing artist retains the route's existing not-found response.

Synchronous debug and destructive `deleteOldContent=true` routes return `409`.
They cannot bypass Temporal admission and write alongside active workflows.

## Inspect and recover

Workers resume open executions after a restart. A completed artist can have status
`Partial`; inspect its failed-item count and provider failure details. Batch results
distinguish completed, partial, failed, and conflicting artists. Prerequisite,
discovery, expiration, cancellation, and rebuild failures are not successful partial
results.

Each operation has a seven-day deadline shared across children and continuation runs. Do
not reset or retry an old execution beyond that deadline. A fresh import is the normal
recovery after item failures, cancellation, or expiry. A standalone artist rebuild
repairs derived data without requiring upstream availability. Every completed artist
import rebuilds, including when it selects no items.

An ordinary provider failure also triggers an artist rebuild before the workflow
reports failure, because earlier item activities may have committed catalog
changes. Cancellation and expiry skip that recovery rebuild. If recovery fails,
inspect the workflow history and run a standalone rebuild after resolving the
cause.

Cancellation reaches activities through heartbeats. HTTP accepts the cancellation token.
Catalog transactions check it before applying writes and before commit. Different shows
for one artist write concurrently; constraints and row locks coordinate shared records.
Existing Dapper calls have bounded SQL timeouts; cancellation does not immediately
interrupt every query. Rebuild SQL owns its transactions and may complete some work
before observing cancellation. Wait for the worker's activities to stop before handing
ingestion to another owner. Committed catalog changes remain committed.

Archive verification results include fixed deletion evidence in Temporal history. Only
errcode 104 proves upstream deletion. Boolean `is_dark: true` establishes public
unavailability and permits catalog cleanup without deleting track rows or playback
events. A successful metadata parse showing no VBR MP3s provides separate removal
evidence. The admin's **Archive deletion candidate limit**
applies only to the requested full refresh. Leaving it blank uses five; zero blocks
cleanup when any candidate exists. If the candidate count exceeds the limit, none
are deleted. Candidates within the allowance still require verification. Single-item
and year-filtered Archive imports do not reconcile deletions.

The authenticated full-artist endpoint accepts, for example,
`POST /api/v2/import/max-creek?archiveDeletionLimit=10`. It preserves the existing
201 text response and includes a `Location` header pointing to the admitted Temporal
run, including namespace, workflow ID, and run ID. The admin displays that link only
after a successful response. Access to the production Temporal UI requires Tailscale.

The legacy admin is private at
[relisten-ingestion-admin.tail09dbf.ts.net/relisten-admin/](https://relisten-ingestion-admin.tail09dbf.ts.net/relisten-admin/).
Use this host for authenticated artist edits and imports. The public API redirects
those privileged routes to the private host; `POST /api/v2/import/phishin` remains
public with its existing API-key check. Public catalog reads and listener writes
such as play reporting remain available. The private endpoint retains the existing
admin login. Infrastructure and the Switz cross-tailnet grant are documented in
`relisten-flux/clusters/relisten3-k3s/tailscale/README.md`.

This deletion-limit change supports deployment while imports are running. Existing
requests omit `ArchiveDeletionLimit`, retain the five-candidate policy, and continue
to schedule the original three-argument `Archive.VerifyDeletionCandidates` activity.
An explicit override selects `Archive.VerifyDeletionCandidatesWithLimit`. Both
handlers are registered in new workers. Saved verification plans remain fixed.
Dark-item cleanup changes activity verification, not workflow commands. Recorded
plans keep their original decisions; newly executed verification can return `Dark`
evidence. Complete the worker rollout before admitting cleanup requests.

The original registered activity signature also allows queued old verification
calls to execute. Missing override fields retain the original continuation and
checkpoint behavior.

Roll out ingestion workers first, wait for all old pollers to be replaced, then
roll out the API that exposes the override. Scheduled imports can continue during
this sequence. Worker-first ordering ensures new override requests cannot land on
code that does not understand the option. Rolling back after admitting overrides
requires workers that understand the new activity contract, or draining those
specific executions before restoring older workers.

Watch queue wait, activity failures, upstream 429/5xx responses, Temporal history
growth, and Redis eviction/persistence. HTTP requests have a 60-second timeout and 64
MiB response limit. Activity attempts have a five-minute timeout, five attempts, a
ten-second heartbeat timeout, and six hours including queue/retry delays. Positive
`Retry-After` values influence retry timing. Tune concurrency from queue delay, upstream
responses, and listener database load; an activity can issue several HTTP requests.

## Diagnostics and monitoring

HTTP diagnostics omit query parameters because some upstream URLs contain
credentials. Preserve that redaction when adding logging or Sentry context.

The worker writes JSON console logs with Temporal and importer scopes. Use
`WorkflowId`, `ActivityId`, `Artist`, `Provider`, and `Attempt` to connect an
upstream response or item result to its workflow. Item results distinguish applied,
unchanged, and skipped inputs; they are not counts of newly created catalog rows.

The API and standalone worker initialize Sentry independently when `SENTRY_DSN`
is configured. The worker reports error-level logs and existing importer
`SentrySdk.CaptureException` calls. Information and warning logs become
breadcrumbs attached to subsequent errors, rather than separate error events.
Logging scopes include artist, provider, workflow, activity, and attempt where
those fields are available. The worker uses its host environment name and keeps
Sentry's default personal-data collection disabled.

The worker owns the SDK lifetime explicitly and disposes it after the host,
which flushes pending events on normal shutdown and after operator commands.
No DSN is required for local development. The production worker manifest reads
`SENTRY_DSN` from the existing `relistenapi-secrets` secret. Deploy the worker
image and its settings together.

Sentry error capture does not turn every partial or failed workflow result into
an event. Temporal history remains the source for typed import outcomes and
retries. Scheduled-run and batch-result monitoring are not enabled.

## Deploy or update

Use matching API and ingestion-worker images from one reviewed commit. The
worker and API admission clients must agree on workflow contracts, namespace,
and payload Redis. Infrastructure manifests and their runbook live in the
[infrastructure repository](https://github.com/RelistenNet/relisten-flux), under
`clusters/relisten3-k3s/temporal/`.

1. Record the current API and worker image digests for rollback. For a compatible
   change verified against old histories, keep schedules and existing imports running.
   Deploy new workers before enabling new request options in API clients.
2. For incompatible changes without versioning, pause schedules, stop manual
   admission, and drain running imports before deploying. When
   work cannot finish, cancel it and wait for activities to stop writing before
   terminating remaining executions. Stopping a poller alone does not erase open
   histories. To retain incompatible histories, use Temporal workflow versioning.
3. Publish API and worker images from the reviewed revision. The image workflow
   defaults to deploying immediately. Use `deploy=false` to publish without a
   rollout, then record the resulting digests:

   ```sh
   gh workflow run build_and_push_image.yml --repo RelistenNet/RelistenApi \
     --ref REVIEWED_REVISION -f component=api -f deploy=false
   gh workflow run build_and_push_image.yml --repo RelistenNet/RelistenApi \
     --ref REVIEWED_REVISION -f component=ingestion-worker -f deploy=false
   ```

4. Apply matching images and settings to `relistenapi`, `relistenapi-worker`,
   and `relistenapi-ingestion-worker`. Keep the existing Hangfire worker serving
   its `default` cache queue. Follow the infrastructure runbook for rollout and
   schema checks; the Temporal development test server is not a production service.
   Account deletion also needs `ACCOUNTS_DATABASE_URL` on the ingestion worker and
   `Temporal__Address` and `Temporal__Namespace` on `relisten-user-service`, which
   refuses to start in Production without them. Roll out the worker first so a
   deletion request is never accepted with no `user-data` poller.
5. Verify API readiness and a targeted Archive import; resume schedules if paused.
   Confirm the worker polls `user-data` in the Temporal UI.
   Compare source/track UUIDs, set order, playable URLs, and show/year counts
   through listener APIs. Admission only starts work; wait for its typed result.
   Repeat the import to check stable identities, then run a standalone rebuild.
6. For shared importer changes, test an appropriate artist for each affected
   provider. Observe queue delay, upstream failures, database load, and listener
   errors. Resume any paused schedules after these checks and inspect the next thin result.

### Catalog search startup

Apply catalog migration 23 before rolling out the worker that refreshes search.
Bring up Typesense and publish the first catalog aliases before rolling out the
search API. With the API image that serves `/health/search-startup`, configure
the `relistenapi` container's startup probe in
`clusters/relisten3-k3s/apps/relistenapi.yaml` in the infrastructure repository.

```yaml
startupProbe:
  httpGet:
    path: /health/search-startup
    port: 3823
  periodSeconds: 5
  failureThreshold: 24
```

This gives the container about two minutes to start and pass the check.
The API returns 503 while loading its first search evidence snapshot, and
200 once it publishes the snapshot or 60 seconds after warmup starts.
Kubernetes starts its usual readiness checks once the startup probe passes.
Leave `/health/ready` on the database checks so browse can serve while search
continues warming after the grace period.
[Kubernetes startup probe documentation](https://kubernetes.io/docs/concepts/workloads/pods/probes/#startup-probe)

After an artist import, including a thin refresh, the worker matches tracks to existing songs for
artists with `norm_catalogue` enabled. A match failure is logged and the import
still finishes. New titles still need an explicit `--norm-songs` run. Artist imports
start a background reindex after matching, and song normalization reindexes its
artist. Venue normalization is picked up by the daily build. Import and song
refresh requests record their start before resolving the
aliases, using `search_artist_reindexes` from migration 23. Once the full build
publishes new aliases and starts the retirement timer, it reindexes artists whose
requests started since its database snapshot began. The next successful daily
build repairs failed or missed refreshes. Catalog API pods refresh query
evidence after an alias swap and about once an hour, with jitter, to pick up
in-place index changes. They read catalog metadata from the replica. A failed
reload keeps the previous snapshot and waits five minutes before trying again.

Retirement drops the old collections. Typesense reclaims their disk space on
its compaction interval (`--db-compaction-interval=86400`). Raft snapshots
bound the import log on a separate interval (`--snapshot-interval-seconds=300`).
Allow disk headroom for the retiring generation until compaction and a subsequent
snapshot complete. Run any manual full compaction during low traffic because it
can slow live searches.

### Recovery

Replacing a worker with the same image should resume unfinished work. A restart
may finish an in-flight activity gracefully; inspect history before claiming that
it exercised an activity retry. Verify that a failed or canceled operation can
be followed by a fresh import with stable identities and usable listener responses.

For a bad release, stop admission and drain work before restoring the recorded
images and settings. Old code must not resume histories written by incompatible
new code. Keep committed catalog changes; use a fresh import or standalone
rebuild after correcting the failure. Stop ingestion when an error can remove
unrelated data or overload the listener database.

## Retired Hangfire ingestion

The API no longer registers ingestion schedules or an `artist_import` Hangfire
server. The admin and import HTTP routes use Temporal unconditionally. Hangfire's
`default` queue still serves cache and popularity jobs; retain
`ENABLE_HANGFIRE_SERVER=true` on that worker.

`ScheduledService` retains no-op method signatures solely for previously saved
Hangfire jobs. Retrying one cannot import or delete catalog data. Remove stale
saved ingestion schedules operationally if any remain; they are not recreated by
startup. The obsolete `Ingestion__Engine` setting has no effect.

## Stored artist venue count refresh

Migration 15 creates stable artist/place identities and stored show counts.
Foreign keys cascade relationship deletion when an artist or venue is removed.
It seeds only the derived table; imported rows, accepted normalization decisions,
and their audit history remain unchanged. `norm.venues` remains the authority for
venue merges; relationship rows contain canonical counts and no copied redirects.

Apply migration 15 before deploying the companion admin change. Ensure the
existing `relisten_admin` role has `EXECUTE` on
`norm.refresh_artist_venues(uuid[])`; the migration and admin role-provisioning
script provide that narrow grant. The function uses `SECURITY DEFINER` with a
safe fixed search path. The admin role needs no direct derived-table write grant.

Admin venue edits call the full refresh in the same `withNormRun` transaction,
before finishing the run and committing. Any refresh failure rolls back the edit,
run record, and counts. Resolve the failure before retrying the original admin
operation. Import and normalization activities keep their best-effort post-commit
refreshes; a database failure is logged and does not undo completed source work.

There is no recurring count job, dedicated Temporal workflow, or pending queue.
After a failed post-commit hook or an out-of-band SQL repair, an operator with
execution permission can repair counts explicitly:

```sql
SELECT * FROM norm.refresh_artist_venues();
```

An explicit UUID array limits the refresh and its exclusive artist locks to those
artists. Scoped recounts share the `norm-venues` gate, allowing disjoint artist
scopes to run concurrently. Overlapping artist scopes serialize. Null or omitted
input refreshes all current owners under the exclusive venue gate; venue edits
also hold that gate exclusively. Ordinary reads use snapshots and do not acquire
these advisory locks. The repair performs database work only, with no provider or
model requests.

Locks last until transaction end. Use one complete refresh scope per transaction.
If a transaction will need a full recount, acquire the venue gate exclusively
before editing source rows, as the admin does; do not upgrade a scoped shared
gate to exclusive while other scopes run.

Fix forward while keeping the schema, grants and stable relationship IDs.
Older admin builds/workers can leave counts stale; deploy matching maintenance
before resuming edits and explicitly repair affected counts. Recounts do not
introduce a sync revision protocol.
See the [count reference](../normalization-counts.md) for
tests and the detailed storage contract.

## Storage, backups, and recovery

Temporal stores workflow history in its own PostgreSQL persistence and visibility
databases. Dedicated Redis stores oversized inputs and results referenced by that
history. Each successful encode or reuse refreshes a 15-day expiry; decoding does not
extend retention or delete bytes. See [payload
contracts](README.md#durable-progress-and-payloads) before changing the codec or
retention settings.

Fifteen days covers the seven-day execution limit, at most seven days of closed-history
retention, and one day of margin. If closed-history retention must exceed seven days,
increase payload retention by the same amount before adopting that policy. Saved
schedules contain small commands rather than expiring payload references. Replay
fixtures that outlive the TTL need their referenced bytes retained in isolated test
storage or regenerated histories.

Production uses the separate `relisten-temporal-payloads` Redis with a persistent
volume, AOF writes every second, and no eviction. API clients and workers must use its
URL before creating production workflows. The default shared-cache setting remains
suitable for disposable local tests. A crash can still lose the last second of AOF
writes.

Redis loss or eviction can make referenced history unreadable. The codec reports
missing, corrupt, oversized, or unsupported payloads; it never refetches upstream inputs
to conceal the loss. There is no web codec endpoint, so Temporal UI shows references for
offloaded values. Keep small command/result summaries and structured logs available for
diagnosis.

If required bytes cannot be recovered, stop new admission and pause affected schedules.
Attempt cancellation and drain running activities. If decoding prevents cancellation
from being processed, stop the worker and verify that activity writes have ended before
terminating the unreadable execution. Start fresh only after the old execution and its
activities can no longer write. A fresh artist import or standalone rebuild repairs
derived data. Do not delete arbitrary Redis keys as a recovery step.

Production logical backups dump only the `app` database. Temporal uses separate
`temporal` and `temporal_visibility` databases in the same PostgreSQL cluster, so these
dumps exclude workflow state without schema filters. The deployment adds no backups or
archival for Temporal or its payload Redis. Persistent storage supports pod restarts; it
is not a backup guarantee.

After catalog restoration or loss of Temporal/payload storage, keep ingestion stopped.
Discard unmatched workflow state, recreate the namespace and schedules, and start fresh
imports from the restored catalog. Do not resume old histories against a catalog or
payload store restored to a different point. This recovery policy trades saved progress
for simpler backups of rebuildable imports. A future physical PostgreSQL cluster backup
would include all databases; it cannot exclude Temporal with `pg_dump` flags.

The [infrastructure repository](https://github.com/RelistenNet/relisten-flux)
owns `clusters/relisten3-k3s/temporal/README.md` and its deployment manifests.
Follow that runbook for database schema jobs, namespace creation, private UI
access, and persistent payload Redis. The cluster uses manual manifest
application. Its root kustomization does not include the Temporal bundle;
provision it before applying an API manifest that selects Temporal. Applying
infrastructure alone does not switch ingestion ownership or register schedules.

Run migration 26 and publish the nightly popularity read model before
building the first catalog search generation with typed resources. The search
build reads the published windows alongside its existing exact-file ranking
evidence.
