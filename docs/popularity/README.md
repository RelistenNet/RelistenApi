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

This replaces the separate freshness table and its scope key. Per-row timestamp
comparisons reject old publications; inactive rows replace the former deletion
step. There is no placeholder catalog ID or special global UUID.

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

Publication uses one sorted bulk upsert in a transaction. It joins calculated
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

## Rollout and recovery

1. Apply migration 17 and deploy this foundation. Legacy calculations can begin
   dual publication while existing clients continue reading Redis.
2. Run `PopularityJobs.RefreshReadModel` through the normal job runner before
   deploying v4, so its first requests have stored scores. Measure a full repair
   on real telemetry; it invokes existing scoped calculations sequentially.
3. Deploy v4. Stale or missing required populations refresh on demand. Monitor
   queue failures and the affected rows' `generated_at` when diagnosing age.

An application rollback keeps the additive tables and Redis data. Repair by
correcting and rerunning a calculation, without truncating last successful data.
The schema is still unshipped in this PR; local preview schema changes are applied
explicitly. No production schema or schedules changed during development.

For local review, publish the existing real Redis snapshot through the writer,
retaining its timestamps. Local workers stay disabled, so queued refreshes cannot
replace that snapshot with results calculated from the empty local play history.

## Verification

`./test-local.sh --filter FullyQualifiedName~Popularity` exercises real publication,
scoped replacement, late-calculation rejection, empty-population freshness, one
batch for stale/missing scopes, and dual-publication failure/retry in a disposable
catalog. V4 HTTP tests cover ordering, enrichment, ETags, and queue-failure recovery
without registering Redis map readers or calculation services.

A temporary-table comparison using the real catalog measured global show layout
at 215,939 rows versus 38,628 scored rows. Storage was 51 MB versus 14.6 MB;
stamping all rows took 1.35 seconds versus 0.38 seconds. Indexed top-five reads
remained 0.12 ms. These are local layout measurements without foreign-key or
calculation cost, not a production budget. The additional background writes
are accepted to keep publication and freshness in the three resource tables.
