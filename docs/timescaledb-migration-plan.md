# TimescaleDB Migration Plan

## Goals
- Replace heavy materialized views with incremental, continuous aggregates.
- Speed up popularity/trending queries without sacrificing accuracy.
- Keep operational overhead low and production safe.

## Current Pain Points
- `source_track_plays` (~230M rows) makes time-sliced analytics slow.
- Materialized views require full refresh and expensive group/sort work.

## Proposed Architecture
- Convert `source_track_plays` to a hypertable on `created_at`.
- Use continuous aggregates for hourly and daily rollups.
- Query popularity/trending metrics from continuous aggregates.
- Add policies for automated refresh, retention, and (optional) compression.

## Step 1: Prerequisites
- TimescaleDB 2.19.3 installed and enabled.
- Confirm extension:
  ```sql
  CREATE EXTENSION IF NOT EXISTS timescaledb;
  SELECT * FROM timescaledb_information.license;
  ```

## Step 2: Convert to Hypertable
Recommended chunk size: **1 day**.
- 1 hour chunks add catalog/index overhead and do not improve time_bucket queries.
- 1 day keeps chunk count manageable and still enables fast time-sliced scans.

```sql
SELECT create_hypertable('source_track_plays', 'created_at', chunk_time_interval => interval '1 day');
```

Indexes:
```sql
CREATE INDEX IF NOT EXISTS idx_stp_created_at ON source_track_plays (created_at);
CREATE INDEX IF NOT EXISTS idx_stp_source_track_uuid ON source_track_plays (source_track_uuid);
```

## Step 3: Continuous Aggregates
### Hourly Rollup (48h trending)
```sql
CREATE MATERIALIZED VIEW source_track_plays_hourly
WITH (timescaledb.continuous) AS
SELECT
  time_bucket('1 hour', p.created_at) AS play_hour,
  p.source_track_uuid,
  t.artist_id,
  s.uuid AS source_uuid,
  sh.uuid AS show_uuid,
  count(*) AS plays,
  sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON p.source_track_uuid = t.uuid
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1,2,3,4,5;
```

Indexes:
```sql
CREATE INDEX ON source_track_plays_hourly (play_hour DESC, artist_id);
CREATE INDEX ON source_track_plays_hourly (play_hour DESC, show_uuid);
```

### Daily Rollup (30d hot and 6mo baselines)
```sql
CREATE MATERIALIZED VIEW source_track_plays_daily
WITH (timescaledb.continuous) AS
SELECT
  time_bucket('1 day', p.created_at) AS play_day,
  p.source_track_uuid,
  t.artist_id,
  s.uuid AS source_uuid,
  sh.uuid AS show_uuid,
  count(*) AS plays,
  sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON p.source_track_uuid = t.uuid
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1,2,3,4,5;
```

Indexes:
```sql
CREATE INDEX ON source_track_plays_daily (play_day DESC, artist_id);
CREATE INDEX ON source_track_plays_daily (play_day DESC, show_uuid);
```

## Step 4: Refresh Policies
```sql
SELECT add_continuous_aggregate_policy('source_track_plays_hourly',
  start_offset => interval '6 hours',
  end_offset => interval '1 hour',
  schedule_interval => interval '1 hour');

SELECT add_continuous_aggregate_policy('source_track_plays_daily',
  start_offset => interval '2 days',
  end_offset => interval '1 day',
  schedule_interval => interval '1 day');
```

Why these windows:
- Hourly covers late arrivals (typically <2 hours) with margin.
- Daily refreshes the last two days, covering day rollovers and late arrivals.

## Step 5: Update Popularity/Trending Queries
### Trending artists (48h)
```sql
WITH plays_48h AS (
  SELECT artist_id, sum(plays) AS plays_48h
  FROM source_track_plays_hourly
  WHERE play_hour >= now() - interval '48 hours'
  GROUP BY 1
),
plays_30d AS (
  SELECT artist_id, sum(plays) AS plays_30d
  FROM source_track_plays_daily
  WHERE play_day >= now() - interval '30 days'
  GROUP BY 1
)
SELECT ...
```

### Hot artists (30d)
```sql
SELECT artist_id, sum(plays) AS plays_30d
FROM source_track_plays_daily
WHERE play_day >= now() - interval '30 days'
GROUP BY 1;
```

Shows/years use `show_uuid` plus joins to `shows` and `years` as before, but against the rollups.
Use `total_track_seconds` for coarse “time played” totals without re-joining `source_tracks`.

## Step 5A: Improvements Now Possible
Because continuous aggregates are incremental and cheap to query, you can add:
- **Multi-window trend**: include 6h vs 7d vs 30d in one response.
- **Smoother trends**: compute `trend_ratio` using average plays per hour/day rather than raw sums.
- **Per-entity baselines**: 90-day baselines for each artist/show/year without raw table scans.
- **Weighted trend**: blend `trend_ratio` with `sqrt(plays_30d)` using percentile ranks.

Example blended score using caggs:
```
momentum = 0.7 * percentile_rank(trend_ratio)
         + 0.3 * percentile_rank(sqrt(plays_30d))
```

Example 7-day measures (daily cagg):
```sql
SELECT artist_id, sum(plays) AS plays_7d
FROM source_track_plays_daily
WHERE play_day >= now() - interval '7 days'
GROUP BY 1;
```

## Step 6: Drop Old Materialized Views
Once the new rollups are verified:
```sql
DROP MATERIALIZED VIEW IF EXISTS source_track_plays_by_day_6mo;
DROP MATERIALIZED VIEW IF EXISTS source_track_plays_by_hour_48h;
```

Remove the scheduled refresh jobs for those views in app code.

## Step 7: Optional Improvements
### Compression
If you keep raw plays for years, compress older chunks:
```sql
ALTER TABLE source_track_plays SET (timescaledb.compress);
SELECT add_compression_policy('source_track_plays', interval '90 days');
```

### Retention
If you can drop old raw events:
```sql
SELECT add_retention_policy('source_track_plays', interval '2 years');
```

### Additional Rollups
For more analytics (less runtime cost):
- `source_track_plays_weekly` for longer trends.
- `artist_plays_daily` to avoid grouping on `source_track_uuid` for artist-only queries.

## Production Checklist
- Run on staging with a snapshot of production data.
- Validate query plans and timings against old materialized views.
- Verify continuous aggregate policies run as expected.
- Confirm popularity/trending results match current outputs within tolerance.
- Monitor chunk count, disk usage, and background jobs.

## Migration Sequence
1. Create a new hypertable (`source_track_plays_new`) and dual-write trigger.
2. Backfill old data in chunks until counts match.
3. Swap tables in a short maintenance window.
4. Fix sequences and rebuild indexes if needed.
5. Create continuous aggregates and policies.
6. Backfill aggregates with `CALL refresh_continuous_aggregate`.
7. Deploy app changes to use new rollups.
8. Verify outputs and performance.
9. Drop old materialized views and refresh jobs.

## Staged Migration (No Data Loss)
### 1) Create the new hypertable
```sql
CREATE TABLE source_track_plays_new (LIKE source_track_plays INCLUDING ALL);

SELECT create_hypertable(
  'source_track_plays_new',
  'created_at',
  chunk_time_interval => interval '1 day'
);

ALTER TABLE source_track_plays_new
ADD CONSTRAINT source_track_plays_new_pkey PRIMARY KEY (id, created_at);
```

### 2) Dual-write trigger
```sql
CREATE OR REPLACE FUNCTION source_track_plays_mirror()
RETURNS trigger AS $$
BEGIN
  INSERT INTO source_track_plays_new
  VALUES (NEW.*)
  ON CONFLICT (id, created_at) DO NOTHING;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_source_track_plays_mirror
AFTER INSERT ON source_track_plays
FOR EACH ROW EXECUTE FUNCTION source_track_plays_mirror();
```

### 3) Backfill in 1M chunks
```sql
INSERT INTO source_track_plays_new
SELECT *
FROM source_track_plays
WHERE id BETWEEN :start_id AND :end_id
ON CONFLICT (id, created_at) DO NOTHING;
```

The admin job `/relisten-admin/timescaledb` runs this in 1M increments.

### 4) Swap tables (short maintenance)
```sql
BEGIN;
DROP TRIGGER trg_source_track_plays_mirror ON source_track_plays;
DROP FUNCTION source_track_plays_mirror();

ALTER TABLE source_track_plays RENAME TO source_track_plays_old;
ALTER TABLE source_track_plays_new RENAME TO source_track_plays;
COMMIT;
```

### 5) Reset the sequence
```sql
SELECT setval('source_track_plays_id_seq', (SELECT max(id) FROM source_track_plays));
```

## Backfill Example
```sql
CALL refresh_continuous_aggregate('source_track_plays_hourly', now() - interval '90 days', now());
CALL refresh_continuous_aggregate('source_track_plays_daily', now() - interval '7 months', now());
```

## Operational Notes
- Continuous aggregates read from raw data only for new time buckets.
- Chunk size changes later require `set_chunk_time_interval` or migration.
- Consider `timescaledb_toolkit` if you want advanced analytics.
