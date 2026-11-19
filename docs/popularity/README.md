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

Each metrics table has plays, hours, and hot score for 48 hours, seven days,
30 days, and 365 days, plus trend ratio and raw momentum. Show rows also store
organic momentum. Each row has `generated_at`. Yearly counters are null until
they have been calculated once.

Ordinary v4 metadata includes `popularity`
when any window has plays. Rows without 30-day activity have null stored
momentum and return numeric zero for momentum and trend ratio. Rankings require
a stored momentum score. The v4 population compares 30-day-active rows only,
which can change percentiles for scopes that previously included older
activity. Legacy Redis maps use their existing populations and fields.
Six-hour and 90-day play counts remain scoring inputs. Catalog foreign keys
cascade deletion.

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

The response keeps its stored scores. A missing show metric is null. A missing ranking population is empty while its
first calculation is queued. SQL or queue
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

## Yearly window rollout

Migration 21 creates `source_track_plays_monthly` and `source_plays_daily`
without an initial backfill. Both derive from `source_track_plays_daily`.
The track rollup stores month totals and the count of daily rows per track.
The source rollup groups by source and UTC day, so two tracks from one source
played on the same day contribute one source-day.

The Timescale job `Refresh monthly track plays` runs daily and refreshes the
three latest UTC months that have been closed for at least one day. It refreshes
daily track buckets for that range first. After that call succeeds, it refreshes
the monthly track buckets for the same range. A failed attempt starts over with
daily on retry. The regular daily track and source-day policies still handle
recent activity.

Before applying migration 21, verify that its database role owns the existing
`source_track_plays_daily` aggregate or inherits its owner role. Timescale runs
the job as the role that registers it and requires ownership to refresh the
aggregate.

The yearly window covers 365 UTC dates through the calculation date. Complete
months come from the monthly rollup. The partial month at each edge comes
from the daily rollup. At month turnover, the real-time monthly view reads the
daily aggregate for buckets it has yet to materialize. The daily aggregate's
own freshness still bounds what these reads can see.

1. Deploy the explicit popularity read projections and let older instances
   drain. Build the API, popularity job runners, and ingestion worker from the
   commit that adds migration 21 and the rollups, before the yearly reader and
   writer changes. Run that ingestion worker image with `--migrate`. Roll out
   the matching API and job runners before starting the backfill. The previous
   binaries can keep running across the schema change, but only binaries that
   include migration 21 can restart against it. Backfill both new rollups from the
   earliest available daily bucket, moving forward in bounded UTC month ranges. Use the start of the current UTC month as the
   exclusive end of the monthly backfill. Force each range so it fills any gaps
   left by an earlier refresh. Run each call as its own statement on the
   primary. These calls backfill October 2018.

   ```sql
   CALL refresh_continuous_aggregate(
       'source_track_plays_monthly', TIMESTAMPTZ '2018-10-01 00:00Z',
       TIMESTAMPTZ '2018-11-01 00:00Z', force => true
   );
   CALL refresh_continuous_aggregate(
       'source_plays_daily', TIMESTAMPTZ '2018-10-01 00:00Z',
       TIMESTAMPTZ '2018-11-01 00:00Z', force => true
   );
   ```

   For the source-day rollup, also refresh the current month through the
   daily aggregate's last completed day. This fills any earlier days in the
   current month before the real-time tail takes over. To repair a historical
   month with changed raw plays, refresh its daily track buckets first, then
   refresh the monthly track and daily source buckets for that month.

2. Compare yearly totals with a daily-only calculation across both edge
   months. Measure the yearly scan and complete popularity refresh on
   production data, including global shows and the largest artist. Check
   legacy reads on a cold Redis cache too. Their cache-miss calculations use
   the same writer and wait for the yearly query.
3. Verify the migration has reached the standby, then deploy the catalog API
   and popularity job runners that calculate the yearly window. Wait for all
   popularity writers to roll before running `PopularityJobs.RefreshReadModel`.
   Confirm ordinary reads expose `windows.1y` and yearly-only rows stay out of
   rankings.

Each refresh calculates the yearly window before the existing atomic upsert.
The yearly scan uses the same database read path as the recent windows. If the
scan fails, the job keeps the previous publication for retry. After migration 21,
recovery images must recognize that schema version.
