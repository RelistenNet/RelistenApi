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
- `popularity_refreshes`: `kind`, `scope_uuid`, and `generated_at`, keyed by the
  first two columns. Zero UUID identifies a global population; another UUID
  identifies an artist population. This is a refresh key, not a catalog ID.

Each metrics table has plays, hours, and hot score for 48 hours, seven days, and
30 days, plus trend ratio and raw momentum. Only show rows have nullable organic
momentum. Six-hour and 90-day play counts are calculation inputs, not read-model
fields. Global ranking indexes contain only global rows. Catalog foreign keys
cascade deletion; there are no per-row timestamps or revision counters.

Global and artist-local percentiles have different comparison populations and
can refresh independently. Storing separate rows preserves those results without
coordinating their calculations. Window values and public metric-map scores
retain warm Redis precision; artist-local show raw/organic scores and trend
ratios retain their cache DTO precision.

Freshness belongs to the complete population. A successful empty calculation
still needs a timestamp, otherwise every request would treat it as missing.
The refresh row also orders competing publications. A `generated_at` column on
metric rows alone would duplicate timestamps and lose both guarantees on empty
results.

## Read and refresh ownership

`PopularityRefreshRequest` is scoped to one HTTP request. Readers register their
required populations while assembling the response. After catalog snapshots
are disposed, the controller checks all required timestamps in one SQL query
and queues at most one `PopularityJobs.RefreshPopulations` job. The same one-hour
staleness threshold is used by legacy maps. Unknown artists are excluded from
refresh requests; successful empty populations are fresh normally.

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
Previously stored artist populations are included in a full repair so that rows
whose plays have aged out can be removed. Normal reads request only populations
they actually use; unused artists require no automatic work.

## Publication and failure

`PopularityService` owns calculations. Metric-map calculations publish their full
population through `PopularityStore` before the caller writes Redis. This covers
explicit map refreshes and map cache misses. Legacy artist lists retain their
separate 30-day-active comparison population; their metric map spans 90 days.

Publication uses one short transaction: update the population's refresh row,
upsert calculated rows, remove departed rows, and commit. An older calculation
that finishes late is ignored. The refresh row serializes publication only for
that population. Other artists use different rows; readers use MVCC. The writer
takes no explicit catalog/table locks. It joins current catalog owners to omit
unknown or foreign targets before publishing.

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
   queue failures and `popularity_refreshes.generated_at` when diagnosing age.

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
