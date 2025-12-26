# Popularity & Trending Design

## Data Sources (Current Schema)
- Materialized views: `source_track_plays_by_day_6mo`, `source_track_plays_by_hour_48h`, `show_source_information`, `source_review_counts`, `venue_show_counts`.
- Fact table: `source_track_plays` (anonymous playback events).
- Dimension tables: `artists`, `shows`, `years`, `sources`.

## Dataset Observations (Local Backup)
- Total play events: `227,518,814` (range `2018-10-19` to `2025-12-20` in the backup).
- Plays in the last 30 days: `2,606,343`.
- Artists with plays in 30d: `211`.
- 30d artist play distribution: `p50=1,808`, `p90=10,657`, `p95≈21,872`, `p99≈130,230`.
- Top 4 artists (30d) total: `70.24%` of all plays.
- 48h artist play distribution: `p50=72`, `p70≈130`, `p80≈180`, `p90≈418`.
- 48h show play distribution: `p50=2`, `p70=5`, `p80=9`, `p90=19`.
- 48h year play distribution: `p50=5`, `p70=12`, `p80=20`, `p90=42`.
- Global 48h/30d ratio (all plays): `0.5953` (used as a prior for smoothing).

## Core Metrics
- **Hot**: raw popularity over a window (e.g., last 30 days) with optional compression to reduce dominance.
- **Trending**: short-term velocity vs baseline (e.g., last 48h vs prior 30d average), with smoothing to avoid tiny-volume spikes.

Suggested score formulas (per entity):
- `hot_score = log10(plays_30d + 1)`
- `trend_score = (plays_48h / 2) / NULLIF(plays_30d / 30, 0)` with a minimum-play floor (e.g., `plays_48h >= 10`).

## Query Shapes
**Artists (global hot):**
```sql
select
  p.artist_uuid,
  a.name,
  sum(p.plays) as plays_30d,
  log(10, sum(p.plays) + 1) as hot_score
from source_track_plays_by_day_6mo p
join artists a on a.uuid = p.artist_uuid
where p.play_day >= now() - interval '30 days'
group by 1, 2
order by hot_score desc, plays_30d desc;
```

**Artists (global trending):**
```sql
with plays_48h as (
  select artist_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  group by 1
),
plays_30d as (
  select artist_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where play_day >= now() - interval '30 days'
  group by 1
)
select
  a.uuid as artist_uuid,
  a.name,
  p48.plays_48h,
  p30.plays_30d,
  (p48.plays_48h / 2.0) / nullif(p30.plays_30d / 30.0, 0) as trend_ratio,
  log(10, p30.plays_30d + 1) as hot_score
from plays_48h p48
join plays_30d p30 on p30.artist_uuid = p48.artist_uuid
join artists a on a.uuid = p48.artist_uuid
where p48.plays_48h >= 10
order by trend_ratio desc;
```

**Artists (global trending, blended score):**
```sql
with plays_48h as (
  select artist_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  group by 1
),
plays_30d as (
  select artist_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where play_day >= now() - interval '30 days'
  group by 1
),
base as (
  select
    p48.artist_uuid,
    p48.plays_48h,
    p30.plays_30d,
    (p48.plays_48h / 2.0) / nullif(p30.plays_30d / 30.0, 0) as trend_ratio,
    sqrt(p30.plays_30d) as hot_score
  from plays_48h p48
  join plays_30d p30 on p30.artist_uuid = p48.artist_uuid
  where p48.plays_48h >= 130 and p30.plays_30d >= 1808
),
scored as (
  select
    *,
    (trend_ratio - min(trend_ratio) over ()) / nullif(max(trend_ratio) over () - min(trend_ratio) over (), 0) as trend_norm,
    (hot_score - min(hot_score) over ()) / nullif(max(hot_score) over () - min(hot_score) over (), 0) as hot_norm
  from base
)
select
  a.uuid as artist_uuid,
  a.name,
  plays_48h,
  plays_30d,
  round(trend_ratio::numeric, 2) as trend_ratio,
  round(hot_score::numeric, 2) as hot_score,
  round((0.7 * trend_norm + 0.3 * hot_norm)::numeric, 4) as blended_score
from scored
join artists a on a.uuid = scored.artist_uuid
order by blended_score desc;
```

**Shows within an artist (hot/trending):**
```sql
select
  s.uuid as show_uuid,
  s.display_date,
  sum(p.plays) as plays_30d,
  log(10, sum(p.plays) + 1) as hot_score
from source_track_plays_by_day_6mo p
join shows s on s.uuid = p.show_uuid
where p.artist_uuid = :artist_uuid
  and p.play_day >= now() - interval '30 days'
group by 1, 2
order by hot_score desc, plays_30d desc;
```

**Years within an artist (hot/trending):**
```sql
select
  y.uuid as year_uuid,
  y.year,
  sum(p.plays) as plays_30d,
  log(10, sum(p.plays) + 1) as hot_score
from source_track_plays_by_day_6mo p
join shows s on s.uuid = p.show_uuid
join years y on y.id = s.year_id
where p.artist_uuid = :artist_uuid
  and p.play_day >= now() - interval '30 days'
group by 1, 2
order by hot_score desc, plays_30d desc;
```

## Balancing Discovery vs Popularity
Top 4 artists are ~70% of plays, so global lists should be mixed:
- **Balanced feed**: 50% hot, 30% trending, 20% long-tail (artists with `plays_30d` below a threshold).
- **Artist caps**: limit any artist to N% of a feed page (e.g., max 2 entries per 20 items).
- **Compression**: use `log` or `sqrt` of plays to reduce dominance without hiding popularity.

## Preferred Scoring (Based on Current Data)
Compression impact on top 4 artists (30d):
- Raw plays: top 4 = `70.24%` of total plays.
- `sqrt(plays_30d)`: top 4 = `19.20%` of total score.
- `log10(plays_30d + 1)`: top 4 = `3.28%` of total score.

Recommended default:
- **Hot lists**: use `sqrt` compression (keeps popular artists visible while not drowning the list).
- **Discovery/long-tail lists**: optionally use `log10` to strongly flatten the distribution.
- **Trending lists**: use ratio + minimum-play floor; consider blend with `sqrt` hot_score to avoid pure spikes.

Suggested minimum-play floors (from percentiles):
- **Trending artists (global)**: `plays_48h >= 130` (p70), `plays_30d >= 1,808` (p50).
- **Trending shows (global)**: `plays_48h >= 9` (p80), `plays_30d >= 7` (p50).
- **Trending years (per artist)**: `plays_48h >= 12` (p70), `plays_30d >= 67` (p50).
- **Trending shows (per artist)**: `plays_48h >= 5` (p70), `plays_30d >= 7` (p50).

Queries to derive floors:
```sql
-- 48h artists
with plays_48h as (
  select artist_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_48h) as p50,
  percentile_cont(0.7) within group (order by plays_48h) as p70,
  percentile_cont(0.8) within group (order by plays_48h) as p80,
  percentile_cont(0.9) within group (order by plays_48h) as p90
from plays_48h;
```

```sql
-- 48h shows
with plays_48h as (
  select show_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_48h) as p50,
  percentile_cont(0.7) within group (order by plays_48h) as p70,
  percentile_cont(0.8) within group (order by plays_48h) as p80,
  percentile_cont(0.9) within group (order by plays_48h) as p90
from plays_48h;
```

```sql
-- 48h years (global)
with plays_48h as (
  select s.year_id, sum(p.plays) as plays_48h
  from source_track_plays_by_hour_48h p
  join shows s on s.uuid = p.show_uuid
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_48h) as p50,
  percentile_cont(0.7) within group (order by plays_48h) as p70,
  percentile_cont(0.8) within group (order by plays_48h) as p80,
  percentile_cont(0.9) within group (order by plays_48h) as p90
from plays_48h;
```

```sql
-- 30d artists
with plays_30d as (
  select artist_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where play_day >= now() - interval '30 days'
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_30d) as p50,
  percentile_cont(0.7) within group (order by plays_30d) as p70,
  percentile_cont(0.8) within group (order by plays_30d) as p80,
  percentile_cont(0.9) within group (order by plays_30d) as p90
from plays_30d;
```

```sql
-- 30d shows
with plays_30d as (
  select show_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where play_day >= now() - interval '30 days'
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_30d) as p50,
  percentile_cont(0.7) within group (order by plays_30d) as p70,
  percentile_cont(0.8) within group (order by plays_30d) as p80,
  percentile_cont(0.9) within group (order by plays_30d) as p90
from plays_30d;
```

```sql
-- 30d years (global)
with plays_30d as (
  select s.year_id, sum(p.plays) as plays_30d
  from source_track_plays_by_day_6mo p
  join shows s on s.uuid = p.show_uuid
  where p.play_day >= now() - interval '30 days'
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_30d) as p50,
  percentile_cont(0.7) within group (order by plays_30d) as p70,
  percentile_cont(0.8) within group (order by plays_30d) as p80,
  percentile_cont(0.9) within group (order by plays_30d) as p90
from plays_30d;
```

Per-artist floors (examples):
- **Grateful Dead**: shows 48h `p50=11`, `p70=21`, `p80=30`, `p90≈47`; shows 30d `p50=281`, `p70≈486`, `p80=674`, `p90≈1232`. Years 48h `p50=1004`, `p70=1508`, `p80=1740`, `p90=2368`; years 30d `p50=28848`, `p70=57908`, `p80=71486`, `p90=78637`.
- **Phish**: shows 48h `p50=5`, `p70=12`, `p80=18`, `p90=28`; shows 30d `p50=66`, `p70=117`, `p80≈169`, `p90≈352`. Years 48h `p50=138`, `p70≈349`, `p80≈515`, `p90≈867`; years 30d `p50≈2756`, `p70≈6926`, `p80≈10033`, `p90≈23519`.

Queries to derive per-artist floors (replace `:artist_uuid`):
```sql
-- per-artist 48h shows
with plays_48h as (
  select show_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  where artist_uuid = :artist_uuid
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_48h) as p50,
  percentile_cont(0.7) within group (order by plays_48h) as p70,
  percentile_cont(0.8) within group (order by plays_48h) as p80,
  percentile_cont(0.9) within group (order by plays_48h) as p90
from plays_48h;
```

```sql
-- per-artist 30d shows
with plays_30d as (
  select show_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where artist_uuid = :artist_uuid
    and play_day >= now() - interval '30 days'
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_30d) as p50,
  percentile_cont(0.7) within group (order by plays_30d) as p70,
  percentile_cont(0.8) within group (order by plays_30d) as p80,
  percentile_cont(0.9) within group (order by plays_30d) as p90
from plays_30d;
```

```sql
-- per-artist 48h years
with plays_48h as (
  select s.year_id, sum(p.plays) as plays_48h
  from source_track_plays_by_hour_48h p
  join shows s on s.uuid = p.show_uuid
  where p.artist_uuid = :artist_uuid
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_48h) as p50,
  percentile_cont(0.7) within group (order by plays_48h) as p70,
  percentile_cont(0.8) within group (order by plays_48h) as p80,
  percentile_cont(0.9) within group (order by plays_48h) as p90
from plays_48h;
```

```sql
-- per-artist 30d years
with plays_30d as (
  select s.year_id, sum(p.plays) as plays_30d
  from source_track_plays_by_day_6mo p
  join shows s on s.uuid = p.show_uuid
  where p.artist_uuid = :artist_uuid
    and p.play_day >= now() - interval '30 days'
  group by 1
)
select
  percentile_cont(0.5) within group (order by plays_30d) as p50,
  percentile_cont(0.7) within group (order by plays_30d) as p70,
  percentile_cont(0.8) within group (order by plays_30d) as p80,
  percentile_cont(0.9) within group (order by plays_30d) as p90
from plays_30d;
```

## Proposed API Routes (v3 UUID-first)
Default to exposing popularity metrics on existing v3 routes (artists, shows, years). Only add new routes for global lists that have no home yet:
- `GET /api/v3/popular/artists?window=30d&limit=50&mode=balanced` (global list)
- `GET /api/v3/trending/artists?window=48h&baseline=30d&limit=50` (global list)
- `GET /api/v3/popular/shows?window=30d&limit=50` (global list)
- `GET /api/v3/trending/shows?window=48h&baseline=30d&limit=50` (global list)
- `GET /api/v3/popular/years?window=30d&limit=50` (global list, optional)
- `GET /api/v3/trending/years?window=48h&baseline=30d&limit=50` (global list, optional)

Embed scores on existing endpoints:
- `GET /api/v3/artists/{artistUuid}` includes a `popularity` object.
- `GET /api/v3/artists/{artistUuid}/shows` and `/years` include the same fields per item.

## Example App Experience
- **Home**: “Hot Right Now” (global hot, compressed), “Trending Up” (global trending), “Hidden Gems” (long-tail + trending).
- **Artist page**: “Hot Shows (30d)”, “Trending Shows (48h)”, “Hot Years”.

Example list payloads (shape, not exact schema):
```json
{
  "hotArtists": [
    {"rank": 1, "artist_uuid": "77a58ff9-2e01-c59c-b8eb-cff106049b72", "name": "Grateful Dead", "plays_30d": 1286398, "popularity": {"hot_score": 6.11, "momentum_score": 0.72}},
    {"rank": 2, "artist_uuid": "ca53d281-0041-ae33-a050-c87702d93b0c", "name": "Phish", "plays_30d": 300693, "popularity": {"hot_score": 5.48, "momentum_score": 0.61}},
    {"rank": 3, "artist_uuid": "ce1dd669-3f3b-0186-ab59-e8300e92db7f", "name": "Widespread Panic", "plays_30d": 132330, "popularity": {"hot_score": 5.12, "momentum_score": 0.54}}
  ],
  "trendingArtists": [
    {"rank": 1, "artist_uuid": "6086be97-5b76-888a-1ad8-2f705148fb8e", "name": "John Mayer", "plays_48h": 337, "plays_30d": 4050, "trend_ratio": 1.25, "popularity": {"hot_score": 3.61, "momentum_score": 0.88}},
    {"rank": 2, "artist_uuid": "93d0c6ac-8ca6-d38b-35db-ca20de369301", "name": "Tea Leaf Green", "plays_48h": 256, "plays_30d": 3950, "trend_ratio": 0.97, "popularity": {"hot_score": 3.60, "momentum_score": 0.71}}
  ]
}
```

```json
{
  "artist_uuid": "77a58ff9-2e01-c59c-b8eb-cff106049b72",
  "hotShows": [
    {"rank": 1, "show_uuid": "b2b19aa3-f1f1-b1bd-27e1-018be854ca87", "display_date": "1973-12-18", "plays_30d": 18708, "popularity": {"hot_score": 4.27, "momentum_score": 0.66}},
    {"rank": 2, "show_uuid": "f9656fee-2a3b-face-c298-5cd718dca084", "display_date": "1973-12-08", "plays_30d": 14542, "popularity": {"hot_score": 4.16, "momentum_score": 0.61}}
  ],
  "trendingShows": [
    {"rank": 1, "show_uuid": "0d1f0250-a7d0-4ad7-3bc7-b822c613d0ef", "display_date": "1978-12-19", "plays_48h": 3239, "plays_30d": 3549, "trend_ratio": 13.69, "popularity": {"hot_score": 3.55, "momentum_score": 0.93}},
    {"rank": 2, "show_uuid": "0e1047e5-2491-8897-9267-49873292314b", "display_date": "1969-12-19", "plays_48h": 1476, "plays_30d": 1683, "trend_ratio": 13.16, "popularity": {"hot_score": 3.23, "momentum_score": 0.90}}
  ],
  "hotYears": [
    {"rank": 1, "year_uuid": "eba36ce9-05d5-8084-391a-8ee355464575", "year": "1973", "plays_30d": 142666, "popularity": {"hot_score": 5.15, "momentum_score": 0.68}},
    {"rank": 2, "year_uuid": "4e1274be-a348-0477-9502-4454853ac162", "year": "1979", "plays_30d": 94830, "popularity": {"hot_score": 4.98, "momentum_score": 0.62}}
  ]
}
```

## Alternatives to Consider
- **Z-score or z-normalized trend**: `(recent - baseline_mean) / baseline_stddev` if we build a per-entity history.
- **Bayesian smoothing**: blend an entity’s ratio with a global prior to avoid small-volume spikes.
- **EWMA**: exponentially weighted moving average for “hotness” to favor fresh plays.
- **Hybrid exploration**: epsilon-greedy or multi-armed bandit to rotate long-tail content.
- **Source-weighted plays**: optionally dampen repeated plays from the same source/show to reduce dominance.

What we can evaluate now:
- Z-score and EWMA need per-entity history (daily/weekly aggregates for 1-2 years) to be meaningful.
- Bayesian smoothing can be approximated today using the global ratio as a prior.

Example smoothed trend ratio using a global prior:
```sql
with plays_48h as (
  select artist_uuid, sum(plays) as plays_48h
  from source_track_plays_by_hour_48h
  group by 1
),
plays_30d as (
  select artist_uuid, sum(plays) as plays_30d
  from source_track_plays_by_day_6mo
  where play_day >= now() - interval '30 days'
  group by 1
),
global as (
  select
    sum(plays) filter (where p48 = true) as plays_48h,
    sum(plays) filter (where p48 = false) as plays_30d
  from (
    select plays, true as p48 from source_track_plays_by_hour_48h
    union all
    select plays, false as p48 from source_track_plays_by_day_6mo where play_day >= now() - interval '30 days'
  ) x
),
base as (
  select
    p48.artist_uuid,
    p48.plays_48h,
    p30.plays_30d,
    (p48.plays_48h / 2.0) / nullif(p30.plays_30d / 30.0, 0) as trend_ratio,
    (global.plays_48h / 2.0) / nullif(global.plays_30d / 30.0, 0) as global_ratio
  from plays_48h p48
  join plays_30d p30 on p30.artist_uuid = p48.artist_uuid
  cross join global
)
select
  *,
  -- k controls the strength of the prior; start with 200 (near artist p70 of 48h plays)
  ((plays_48h + 200 * global_ratio) / 2.0) / nullif((plays_30d + 200) / 30.0, 0) as trend_ratio_smoothed
from base;
```

These definitions are common in media apps, but we can tune windows, floors, and compression once we observe real traffic patterns.

## Open Questions / Missing Data
- Should we exclude repeat plays from the same IP/session to reduce bot bias? (no user accounts.)
- Minimum-play thresholds per entity to prevent noise spikes.
- Do we need separate scoring for sources vs shows vs tracks?
- Should we refresh materialized views on a fixed cadence (e.g., hourly for 48h, daily for 6mo)?
- Any need to backfill older playback data into the 6-month view?
- Consider new materialized views for long-range baselines (e.g., `source_track_plays_by_day_2y` or weekly rollups) and pre-aggregated `artist_plays_by_day_6mo`, `show_plays_by_day_6mo`, `year_plays_by_day_6mo` to speed up API queries.

## Materialized View Refresh Schedule
- `source_track_plays_by_hour_48h`: hourly at `:15` UTC (keeps trending data responsive).
- `source_track_plays_by_day_6mo`: daily at `05:10` UTC (stable daily aggregates).

Refresh uses `CONCURRENTLY` so reads continue during updates. After refresh, enqueue popularity cache refreshes so Redis serves fresh scores.

## Naming & Buckets
Use consistent field names across artists/shows/years:
- `hot_score`: compressed popularity (sqrt or log10).
- `momentum_score`: blended score combining trend_ratio and hot_score (0-1 normalized).

Bucket suggestion (based on `momentum_score`):
- `1`: 0.00-0.25
- `2`: 0.25-0.50
- `3`: 0.50-0.75
- `4`: 0.75-1.00

## Implementation Strategy
### New/Updated Models
- Add `PopularityMetrics` DTO with `hot_score`, `momentum_score`, `trend_ratio`, `plays_30d`, `plays_48h`.
- Embed as `popularity` on artist/show/year API models (v3 response models only).

### V3 Response Contracts (Populated Fields)
```json
// Artist detail
{
  "artist_uuid": "uuid",
  "name": "string",
  "slug": "string",
  "popularity": {
    "hot_score": 5.12,
    "hot_score_7d": 4.01,
    "momentum_score": 0.61,
    "trend_ratio": 0.88,
    "hours_30d": 5200.4,
    "hours_7d": 840.2,
    "hours_48h": 112.6,
    "plays_30d": 132330,
    "plays_7d": 21345,
    "plays_48h": 1441
  }
}
```

```json
// Show list item
{
  "show_uuid": "uuid",
  "display_date": "YYYY-MM-DD",
  "venue_name": "string",
  "avg_rating": 4.3,
  "popularity": {
    "hot_score": 3.98,
    "hot_score_7d": 2.31,
    "momentum_score": 0.66,
    "trend_ratio": 1.12,
    "hours_30d": 310.5,
    "hours_7d": 44.9,
    "hours_48h": 18.2,
    "plays_30d": 9820,
    "plays_7d": 1440,
    "plays_48h": 610
  }
}
```

```json
// Year list item
{
  "year_uuid": "uuid",
  "year": "YYYY",
  "show_count": 128,
  "source_count": 302,
  "popularity": {
    "hot_score": 4.98,
    "hot_score_7d": 3.22,
    "momentum_score": 0.62,
    "trend_ratio": 1.08,
    "hours_30d": 1420.1,
    "hours_7d": 210.3,
    "hours_48h": 98.6,
    "plays_30d": 94830,
    "plays_7d": 11785,
    "plays_48h": 5209
  }
}
```

### New Services
- `PopularityService` (query/compute): runs the SQL to compute hot, trend, blended scores for artists/shows/years.
- `PopularityCache` (Redis): read-through stale-while-revalidate cache using `RedisService`, storing `generated_at` and payload.
- `PopularityBackgroundRefresher` (Hangfire job): refreshes cached lists on a schedule and when stale reads occur.

### Controllers / Routes to Edit
- `ArtistsController` (v3): include `popularity` in `GET /api/v3/artists/{artistUuid}` response.
- `ShowsController` (v3): include `popularity` on show objects in artist show listings.
- `YearsController` (v3): include `popularity` on year objects in artist year listings.

### New Controllers / Routes
- `PopularityController` (v3): global lists for artists/shows/years (hot, trending, blended). Reuse shared query paths in `PopularityService`.

### Redis Caching Behavior
- Cache keys: `popularity:artists:hot:30d`, `popularity:artists:trending:48h`, `popularity:shows:hot:30d`, etc.
- Payload includes `generated_at` and query parameters.
- When stale: return cached payload and enqueue a refresh job (no hard TTL).

## Caching Strategy (Redis, stale-while-revalidate)
Use Redis as a soft cache with background refresh:
- Store payload + `generated_at` timestamp and a `stale_after` threshold.
- If cache is fresh: return immediately.
- If cache is stale: return cached payload and enqueue a refresh job.
- If cache missing: compute synchronously, store, return.

This avoids hard TTL expirations and keeps responses fast even during recompute.
