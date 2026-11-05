# Stored popularity for catalog reads

Popularity metric-map calculations publish to Postgres and Redis. Legacy APIs
keep their cache keys and contracts. V4 reads stored SQL rows, selecting limited
ranking results and enriching only returned shows. Requests serve stored values
and queue one batch for any required stale or missing populations.

## Schema

- `artist_popularity`: global artist metrics, keyed by `uuid`.
- `show_popularity`: global and artist-local metrics, keyed by `(uuid, is_global)`;
  `artist_uuid` supports artist-scoped selection and replacement.
- `year_popularity`: global and artist-local year metrics, with the same keys.

Each metrics table has plays, hours, and hot score for 48 hours, seven days, and
30 days, plus trend ratio and raw momentum. Only show rows have nullable organic
momentum. Each row also has `generated_at`. Counters are zero and momentum is
null for targets without scored activity. Public `popularity` remains null for
those rows; rankings exclude them. Six-hour and 90-day play counts remain only
calculation inputs. Ranking indexes contain only scored rows, and global
show/year indexes contain only global scored rows. Catalog foreign keys cascade
deletion.

Global and artist-local percentiles have different comparison populations and
can refresh independently. Storing separate rows preserves those results without
coordinating their calculations. Window values and public metric-map scores
retain warm Redis precision; artist-local show raw/organic scores and trend
ratios retain their cache DTO precision.

A publication covers every current catalog target in its scope, including
inactive entities. The same `generated_at` is written to those rows in one
transaction, so a representative row supplies population age. A population with
no catalog targets has nothing to score and needs no refresh. A missing row for
an entirely uninitialized population triggers the first background calculation.
New catalog targets join the next population calculation, as with the legacy
whole-map cache.

Per-row timestamp comparisons reject old publications. Inactive rows preserve
population freshness without a separate freshness table or placeholder ID.

## Read and refresh ownership

`PopularityRefreshRequest` is scoped to one HTTP request. Readers register their
required populations while assembling the response. After catalog snapshots
are disposed, the controller checks all required timestamps in one SQL query
and queues at most one `PopularityJobs.RefreshPopulations` job. The same one-hour
staleness threshold is used by legacy maps. Unknown artists and scopes without catalog targets are excluded from refresh
requests. Successfully calculated inactive rows are fresh normally.

The response keeps its stored scores. A missing show metric is null; a missing
ranking population is empty while its first calculation is queued. SQL or queue
submission failures use v4's existing 503/no-store boundary. A later request can
retry. Enqueueing finishes before publication of the response or its ETag.

Concurrent stale requests may enqueue duplicate batches, as in the existing
legacy path. There is no distributed lease or pending-refresh state. Each job
uses the existing scoped calculations. Retries recalculate even if Postgres has
become fresh: a previous attempt may have committed there and then failed to
publish Redis. This keeps partial-failure recovery straightforward.

`PopularityJobs.RefreshReadModel` remains an explicit full backfill/repair action,
also available through admin refresh-all. There is no automatic schedule.
A full repair visits every catalog artist. Results whose plays aged out become
zero/null rows. Normal reads request only populations they use; unused artists
require no automatic work.

## Publication and failure

`PopularityService` owns calculations. Metric-map calculations publish their full
population through `PopularityStore` before the caller writes Redis. This covers
explicit map refreshes and map cache misses. Legacy artist lists retain their
separate 30-day-active comparison population; their metric map spans 90 days.

Publication uses one atomic sorted bulk upsert. It joins calculated
results onto all current catalog targets, filling missing calculations with
zero counters and null momentum. A row updates only when the incoming
`generated_at` is at least its stored timestamp. Older calculations finishing
late cannot overwrite newer rows. Sorted row order keeps overlapping writes in
the same lock order; no explicit catalog/table lock is taken. Catalog ownership
comes from the joined target, not the calculation payload.

Postgres and Redis are not one transaction. A Postgres publication failure leaves
both previous populations available. If Redis fails afterward, Postgres retains
its new population and the job fails for retry. A warm legacy cache hit does not
backfill Postgres. No v4 body cache or invalidation system is involved.

## Repair and diagnosis

Check the affected rows' `generated_at` and the Hangfire job result when scores
are stale. Use `PopularityJobs.RefreshReadModel` through the normal job runner
for an explicit full repair; it calculates scopes sequentially and can take much
longer than refreshing one artist. Correct and rerun a failed calculation while
retaining the last successful population.

An initial PostgreSQL population needs calculation/publication; a warm Redis
cache hit alone cannot seed it. Empty local play telemetry cannot reproduce
production scores. Keep workers stopped when using a captured review population
that must remain intact.

## Calculation sources

`source_track_plays` stores anonymous track-start events. Popularity calculations
use the hourly/daily Timescale aggregates installed by the
[Timescale migration SQL](../../RelistenApi/Migrations/timescaledb_migration.sql).
[PopularityService](../../RelistenApi/Services/Popularity/PopularityService.cs)
owns the scoring rules. Change the calculation there rather than maintaining
a second formula specification in documentation.
