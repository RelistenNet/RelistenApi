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
staleness threshold is used by legacy maps. Unknown artists and scopes without
catalog targets are excluded from refresh requests. Successfully calculated
inactive rows are fresh normally.

The response keeps its stored scores. A missing show metric is null. A missing
ranking population is empty while its first calculation is queued. SQL or queue
submission failures use v4's existing 503/no-store boundary. A later request can
retry. Enqueueing finishes before publication of the response or its ETag.

Concurrent stale requests may enqueue duplicate batches, as in the existing
legacy path. There is no distributed lease or pending-refresh state. Each job
uses the existing scoped calculations. Retries recalculate even if Postgres has
become fresh: a previous attempt may have committed there and then failed to
publish Redis. This keeps partial-failure recovery straightforward.

The nightly `PopularityJobs.RefreshReadModel` job starts at 05:00 UTC,
before full catalog ingestion at 07:00 UTC and the search rebuild at 11:00
UTC. The admin refresh-all action can run the same job on demand. A full
refresh visits every catalog artist and clears scores for plays that aged out.
Existing read-triggered refreshes still cover the artist, show, and year
populations a request uses.

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
The track rollup stores month totals per track. The source rollup groups by
source and UTC day, so two tracks from one source played on the same day
contribute one source-day.

Two Timescale jobs maintain the rollups once enabled. `Refresh monthly track
plays` refreshes the three latest UTC months that have been closed for at least
one day. `Refresh daily source plays` refreshes the last seven UTC days that
have been closed for at least one day. Each job refreshes the daily track
buckets for its range first, then the upper rollup for the same range, because
an upper refresh reads only what the daily aggregate has materialized. A failed
attempt starts over with daily on retry.

Migration 21 registers both jobs unscheduled. Both rollups are real-time
aggregates: until a bucket is materialized, reads compute it from the daily
aggregate, so yearly totals are correct but slower before the backfill. A
scheduled run before the backfill would materialize only recent buckets and
move the watermark past the unmaterialized history, and older months would
then read as empty. Enable the jobs only after the backfill.

The yearly window covers 365 UTC dates through the calculation date. Complete
months come from the monthly rollup. The partial month at each edge comes
from the daily rollup. At month turnover, the real-time monthly view reads the
daily aggregate for buckets it has yet to materialize. The daily aggregate's
own freshness still bounds what these reads can see. Song and performance
pages read the published performance and source rows.

Popularity jobs run in Hangfire on API pods started with
`ENABLE_HANGFIRE_SERVER`. Each refresh calculates the yearly window, with JIT
disabled for that read, before the existing atomic upsert. If the yearly scan
fails, the job fails for retry and the previous publication stays in place.

Run these steps in order. Run SQL on the primary unless a step says otherwise.

1. Deploy the compatibility Catalog build before migrating. Run the `api`
   workflow with `deploy=true` on `codex/popularity-explicit-projections`
   at `75b3bf436`. Wait for both `relistenapi` and `relistenapi-worker` to
   finish rolling. This build selects explicit columns, so prepared queries
   keep the same result shape when migration 21 adds the yearly columns.

2. Check ownership. Timescale runs each job as the role that registers it and
   requires that role to own the aggregates it refreshes. As the role the
   migration connects with, this query must return `true`:

   ```sql
   SELECT pg_has_role(current_user, view_owner, 'USAGE')
   FROM timescaledb_information.continuous_aggregates
   WHERE view_name = 'source_track_plays_daily';
   ```

3. Migrate. [Verify the prebuilt review-search index](../plans/active/2026-11-18-relisten-mcp.md#1-review-search)
   and publish the `api` and `ingestion-worker` images from the same merged
   release revision with `deploy=false`. Record both digests. Run the published
   ingestion worker image once with `--migrate`.
   Give the one-shot process a `DATABASE_URL` that points to the primary and
   leave `PGBOUNCER_WRITE_HOST` and `PGBOUNCER_READ_ONLY_HOST` unset. Migration
   21 sets `lock_timeout = '5s'` before adding the popularity columns. A lock
   timeout rolls back migration 21. Run `--migrate` again if that happens.
   Confirm version `22` on the primary and standby with the query below.

   ```sql
   SELECT max(version) FROM versioninfo;
   ```

   Running compatibility pods can serve during the backfill. After migration
   21, that image fails schema validation on startup. If a pod exits, roll the
   affected Deployment to the recorded final `api` digest and confirm it is
   ready before resuming. If that Deployment serves HTTP, check a normal v4
   read. Until the backfill finishes, yearly scans on the final image are
   correct but slow, and a timeout keeps the previous publication. Keep both
   refresh jobs disabled until verification passes.

4. Backfill. Use `psql -v ON_ERROR_STOP=1` so a failed refresh stops the batch.
   `\gexec` runs each generated `CALL` as its own statement because the
   refresh procedure requires it. Production data takes about 10–25 minutes
   in total. Completed calls stay committed if a later call fails. Fix the
   failure and rerun the same commands. `force => true` makes each upper
   rollup refresh safe to repeat.

   First bring the daily track aggregate up to date, because each upper refresh
   reads only what daily has materialized. Without `force`, this processes only
   pending invalidations:

   ```sql
   CALL refresh_continuous_aggregate(
       'source_track_plays_daily', NULL,
       date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
   );
   ```

   Then backfill the monthly track rollup, one month per statement, through
   the last complete UTC month. Daily now covers that month, including on the
   first day of a month.

   ```sql
   WITH months AS (
       SELECT generate_series(
           date_trunc('month', min(play_day) AT TIME ZONE 'UTC'),
           date_trunc('month', now() AT TIME ZONE 'UTC') - INTERVAL '1 month',
           INTERVAL '1 month'
       ) AS month
       FROM source_track_plays_daily
   )
   SELECT format(
       'CALL refresh_continuous_aggregate(%L, %L::timestamptz, %L::timestamptz, force => true)',
       'source_track_plays_monthly', month AT TIME ZONE 'UTC',
       (month + INTERVAL '1 month') AT TIME ZONE 'UTC'
   )
   FROM months ORDER BY month
   \gexec
   ```

   Then backfill the source-day rollup through the daily aggregate's
   materialization watermark. The last range is cut at the watermark; when the
   watermark is the first of the month, the current month has no range.

   ```sql
   WITH bounds AS (
       SELECT
           date_trunc('month', (SELECT min(play_day) FROM source_track_plays_daily)
               AT TIME ZONE 'UTC') AS first_month,
           _timescaledb_functions.to_timestamp(
               _timescaledb_functions.cagg_watermark(mat_hypertable_id)
           ) AT TIME ZONE 'UTC' AS watermark
       FROM _timescaledb_catalog.continuous_agg
       WHERE user_view_name = 'source_track_plays_daily'
   ), months AS (
       SELECT month, least(month + INTERVAL '1 month', watermark) AS month_end
       FROM bounds, generate_series(first_month, watermark, INTERVAL '1 month') AS month
   )
   SELECT format(
       'CALL refresh_continuous_aggregate(%L, %L::timestamptz, %L::timestamptz, force => true)',
       'source_plays_daily', month AT TIME ZONE 'UTC', month_end AT TIME ZONE 'UTC'
   )
   FROM months WHERE month < month_end ORDER BY month
   \gexec
   ```

5. Verify the backfill on the primary before enabling the jobs. This query must
   report `true` for both rollups. Each watermark must pass the last day with
   eligible input. Comparing against a day with plays lets the check pass when
   the latest completed month or day was empty. Checking the watermarks catches
   a backfill that left real-time reads to compute recent buckets.

   ```sql
   WITH watermarks AS (
       SELECT user_view_name AS name, _timescaledb_functions.to_timestamp(
           _timescaledb_functions.cagg_watermark(mat_hypertable_id)
       ) AS through
       FROM _timescaledb_catalog.continuous_agg
       WHERE user_view_schema = 'public'
   ), expected(name, last_input_day) AS (
       SELECT 'source_track_plays_monthly', max(play_day)
       FROM source_track_plays_daily
       WHERE play_day < date_trunc('month', now(), 'UTC')
       UNION ALL
       SELECT 'source_plays_daily', max(play_day)
       FROM source_track_plays_daily
       WHERE play_day < (SELECT through FROM watermarks WHERE name = 'source_track_plays_daily')
   )
   SELECT expected.name, w.through AS materialized_through, expected.last_input_day,
          w.name IS NOT NULL
              AND (expected.last_input_day IS NULL OR coalesce(w.through > expected.last_input_day, false)) AS complete
   FROM expected LEFT JOIN watermarks w USING (name);
   ```

   Check play counts against the daily track input. Both queries must return no
   rows. The full joins flag missing buckets in either direction as well as
   buckets with different totals.

   ```sql
   WITH daily AS (
       SELECT time_bucket(INTERVAL '1 month', play_day) AS play_month,
              sum(plays) AS plays
       FROM source_track_plays_daily
       WHERE play_day < date_trunc('month', now(), 'UTC')
       GROUP BY 1
   ), monthly AS (
       SELECT play_month, sum(plays) AS plays
       FROM source_track_plays_monthly
       WHERE play_month < date_trunc('month', now(), 'UTC')
       GROUP BY 1
   )
   SELECT play_month, daily.plays AS daily_plays, monthly.plays AS monthly_plays
   FROM daily FULL JOIN monthly USING (play_month)
   WHERE daily.plays IS DISTINCT FROM monthly.plays;
   ```

   ```sql
   WITH bound AS (
       SELECT _timescaledb_functions.to_timestamp(
           _timescaledb_functions.cagg_watermark(mat_hypertable_id)
       ) AS through
       FROM _timescaledb_catalog.continuous_agg
       WHERE user_view_schema = 'public' AND user_view_name = 'source_track_plays_daily'
   ), tracks AS (
       SELECT source_uuid, play_day, sum(plays) AS plays
       FROM source_track_plays_daily
       WHERE play_day < (SELECT through FROM bound)
       GROUP BY source_uuid, play_day
   ), sources AS (
       SELECT source_uuid, play_day, plays
       FROM source_plays_daily
       WHERE play_day < (SELECT through FROM bound)
   )
   SELECT source_uuid, play_day, tracks.plays AS track_plays, sources.plays AS source_plays
   FROM tracks FULL JOIN sources USING (source_uuid, play_day)
   WHERE tracks.plays IS DISTINCT FROM sources.plays;
   ```

6. Enable both jobs after both rollups pass verification. If a check fails,
   rerun the affected backfill and repeat its checks first.

   ```sql
   SELECT alter_job(job_id, scheduled => true, next_start => now())
   FROM timescaledb_information.jobs
   WHERE proc_schema = 'public'
     AND proc_name IN ('refresh_source_track_plays_monthly', 'refresh_source_plays_daily');
   ```

7. Run the `api` release workflow from the same release revision with
   `deploy=true`. It rolls `relistenapi` and `relistenapi-worker`, including
   the Hangfire server. Verify publication after the rollout.
   - Both jobs report `scheduled = true`, and after their first run
     `timescaledb_information.job_stats.last_run_status` is `Success`.
   - Run `PopularityJobs.RefreshReadModel`. Ordinary v4 reads expose
     `windows.1y`. A legacy read on a cold Redis cache completes; cache misses
     use the same writer and wait for the yearly scan.
   - Yearly-only rows stay out of rankings: rankings require a stored momentum
     score, and rows without 30-day plays store none. Each query returns `0`:

     ```sql
     SELECT count(*) FROM show_popularity
     WHERE plays_1y > 0 AND plays_30d = 0 AND momentum_score IS NOT NULL;
     SELECT count(*) FROM year_popularity
     WHERE plays_1y > 0 AND plays_30d = 0 AND momentum_score IS NOT NULL;
     SELECT count(*) FROM artist_popularity
     WHERE plays_1y > 0 AND plays_30d = 0 AND momentum_score IS NOT NULL;
     ```

To repair a historical month with changed raw plays, refresh its daily track
buckets first, then the monthly track and daily source buckets for that month.


## Song performances and recordings

Migration 26 adds `song_performance_popularity`, `recording_popularity`, and
`song_popularity`. The nightly job reads rollups for one artist at a time and
publishes that artist's three populations in one transaction. An artist
publication timestamp rejects late calculations even when the newer run
skipped rewriting unchanged metric rows. A failed artist leaves its prior
publication readable. The job visits the remaining artists and reports the
failures at the end.

Song performance momentum compares versions of the same song. Recording
momentum compares sources of the same show. Song momentum compares songs by
the same artist. Song totals add up the published performances and include
soundchecks. A file that contains two songs credits one start to each song
for every playback. Listening time is split evenly among the songs because
the catalog has no per-song timing for that file. The recording keeps the
complete file's starts and hours.

The nightly job stores only rows with activity. V4 returns null until a new
item's popularity is published and for items without recorded activity.
Search copies the published windows during indexing. Its song ranking uses
the same stored yearly starts as the displayed song popularity. Exact-file
starts still help choose previews. The Redis maps and request-triggered
refreshes for artists, shows, and years use their existing cadence.
