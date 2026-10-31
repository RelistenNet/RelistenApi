# Stored popularity for catalog reads

Popularity calculations publish the same results to Postgres and Redis. Legacy
APIs keep their cache keys and response contracts. The v4 branch reads stored
Postgres rows, selecting ranked IDs in SQL and enriching only returned shows.

## Ownership and publication

`PopularityService` owns the existing calculations. Each calculation publishes
its full population through `PopularityStore` before the caller writes its
Redis cache. This covers explicit refreshes and legacy cache misses.

- `artist_popularity`: global artist metrics.
- `show_popularity`: separate global and artist-local rows for each show.
- `year_popularity`: separate global and artist-local rows for each year.
- `popularity_refreshes`: last accepted calculation start time per population.

The public counters, hours, hot scores, trend ratio, and raw momentum are stored
as scalar columns. Artist-local show rows also store the organic momentum used
by momentum discovery. Global and artist-local percentile scores have different
comparison populations; neither can replace the other. Window values and public metric-map scores retain the four-decimal precision
of the existing warm Redis maps. Artist-local raw/organic momentum and trend
ratios retain their cache DTO precision. This preserves ranking tie behavior;
public JSON still applies its normal rounding.

Each publication uses one short transaction: claim/update its refresh row,
upsert the calculated rows, delete rows absent from that population, and commit.
The refresh row prevents an older calculation that finishes late from replacing
a newer publication. It also serializes simultaneous publication of the same
population. Different artists use different rows; readers use MVCC. There are
no triggers, advisory locks, table locks, or per-record round trips. Catalog
foreign keys cascade deletion. Unknown or foreign artist targets are omitted.

Postgres and Redis are not one transaction. If Postgres publication fails, the
calculation fails and existing data in both stores remains available. If Redis
fails afterward, Postgres retains its new population and the job fails for
retry. Brief differences between stores are acceptable; no distributed commit
protocol is introduced. A legacy warm-cache hit does not backfill Postgres.

## Refresh and operations

`PopularityJobs.RefreshReadModel(CancellationToken)` refreshes all global maps,
then show/year maps for artists with activity or previously stored local rows.
The latter ensures scores disappear when old plays leave the rolling windows.
Calculations and publications use the existing methods; each population commits
independently. Cancellation is checked between artists. A retry recomputes
populations; it does not clear successfully published data first.

A single hourly Hangfire recurring job owns time-based refresh, independently
of the ingestion engine. Registration requires the existing production
`ENABLE_HANGFIRE_SERVER` setting; development starts no job. Rolling windows
change as time passes, so refresh cannot depend only on new plays or legacy
requests. The admin “refresh all” action also queues this job, alongside the
legacy response-cache refreshes. Read `popularity_refreshes.generated_at` to
check job progress and retained freshness. An empty successful population still
updates its refresh record.

## Rollout and recovery

1. Apply migration 17 and deploy this foundation. Existing legacy traffic can
   continue using Redis while calculations begin dual publication.
2. Run `PopularityJobs.RefreshReadModel` through the normal job runner. Confirm
   global and active artist populations exist before deploying the v4 reader.
   This is derived data; the migration does not recalculate play history.
3. Enable the normal hourly job and monitor its duration and failures. The first
   full refresh has more work than a cache hit: it invokes the existing scoped
   calculations for each active artist sequentially, without concurrent scans.
4. Deploy v4 after backfill. Missing show metrics are `null`; an unpopulated
   ranking is empty. GETs do not calculate or repair derived state.

An application rollback keeps the additive tables and Redis data. Reverting the
v4 reader restores its Redis path. Pause the recurring job if investigating a
bad calculation, then correct and rerun it. Do not truncate the stored tables
before a repair: readers can continue using the last successful populations.

For local review, publish the existing real Redis aggregate snapshot through
`PopularityStore`. Preserve its generation timestamps and compare both resource
JSON and ranking order. This avoids replacing real review metrics with the
empty local play history. Never run a full recalculation against that empty
history as a substitute for a real backfill.

## Verification

`./test-local.sh --filter FullyQualifiedName~Popularity` checks publication,
scoped replacement, late-calculation rejection, raw/organic separation, and the
real calculation-to-Postgres-and-Redis boundary in a disposable catalog. The
v4 branch adds HTTP tests for ranking order, ordinary enrichment, and ETags
without registering Redis/popularity calculation services in its test host.
