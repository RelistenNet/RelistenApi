# Task queues and concurrency

Use Temporal's native controls. **Start with ten concurrent activities per provider worker**, with ten artists in the normal batch, matching the existing Hangfire artist-job concurrency baseline. One worker host can register all the queues. No custom limiter, distributed permits or provider ownership service.

| Task queue | Work | Starting concurrency |
|---|---|---|
| `ingest-archive` | Collection discovery, artist listing, metadata import and deletion verification | 10 |
| `ingest-phishin` | Prerequisite API requests and show pages | 10 |
| `ingest-phishnet` | API shows, ratings HTML and reviews | 10 |
| `ingest-setlistfm` | Artist setlist pages | 10 |
| `ingest-phantasytour` | Band pages and show blobs | 10 |
| `ingest-jerry` | Mirror index and HTML pages | 10 |
| `ingest-local` | Root and directory metadata | 10 |
| `ingest-db` | Artist preparation, batch selection and rebuilds | 10 |
| `ingest-workflows` | Orchestration | 10 active artist children per normal batch |

**PanicStream is disabled and excluded.** Do not register its activities, create a queue or migrate/test its importer.

Ten concurrent jobs can issue more than ten requests per second. Leave `MaxTaskQueueActivitiesPerSecond` unset initially unless an existing provider-specific limit is configured. Keep it available as a simple setting to tune from normal operation. `MaxConcurrentActivities=10` is per worker registration, not a global ten across all queues. Ten artist children doing one item at a time bound the normal batch; manual/discovery work can add some concurrency, as separate Hangfire jobs do today. No exact global cap is required here.

The queue's optional rate setting is server-controlled; pollers on the same queue must use the same value. Concurrency is worker-local. Use one host and operator-controlled stop/start deployments instead of designing around autoscaling or overlapping replicas. See [.NET worker options](https://dotnet.temporal.io/api/Temporalio.Worker.TemporalWorkerOptions.html).

## Activity boundaries

Activities follow domain operations: prepare provider catalogs, discover items, import an item, and reconcile. An item activity can issue several sequential GETs and follow redirects before its catalog transaction. Phish.net ratings/reviews and Local directory groups use this composition. There is no one-request assertion or numbered fetch-part protocol.

The optional queue rate limits activities per second. It does not count HTTP requests. Native concurrency remains useful because item activities do not fan requests out internally. Disable eager activity execution so provider work uses the configured queue. Temporal retries the item operation; its safe GETs may repeat.

Keep ordinary bounded retries and HTTP timeouts. Honor `Retry-After` through the activity retry delay; reduce the queue rate if a provider returns sustained 429s. No shared cooldown service or fairness scheduler. Backfills use the same queues; Alec can pause them when needed.

## Hangfire handoff

Keep Hangfire installed while building and testing Temporal. For rollout, disable Hangfire ingestion schedules/manual admission, let running ingestion finish or stop its worker, then enable Temporal ingestion. Switch all migrated providers together; a per-artist canary progression is not required. Popularity/cache jobs remain on Hangfire.

Rollback is the reverse: stop Temporal ingestion, then re-enable Hangfire. Use the same procedure for an exceptional legacy debug import. Alec controls these rollouts, so no cross-scheduler write-ownership protocol is needed. Do not run both ingestion systems against the same catalog at once.

Locally verify the configured concurrency, activity retries and optional queue rate against the fake upstream. A second worker is useful to check rate behavior if that deployment becomes relevant; it is not a migration gate. Keep recovery/idempotency tests focused on interrupted activities and committed writes.
