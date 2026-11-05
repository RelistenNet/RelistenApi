# Master integration review — November 5, 2026

The eight active API PRs now build on master `2255fb070` (PR #118). Master added
one public capability since their previous base: `/api/v3/live/summary`. V4 now
supplies `/api/v4/live/summary`; legacy implementations remain intact. The open
Phish.net header PR #120 is not on master and adds no public route.

## Live summary

Use `/api/v4/live/summary?minutes=15&sort=surprise`. Query names, defaults,
clamping and scoring follow the shared live-summary domain rules: `minutes`,
`shows_limit`, `artists_limit`, `month_day`, `sort`, `max_per_artist`, `fresh_days`.
Swagger publishes `surprise|plays`; malformed queries return 422 ProblemDetails.

The direct resource includes the completed `[window_start,window_end)` span,
totals, app counts, zero-filled minute timeline, artists with their top show,
headline shows, deep cuts, recent concerts and on-this-day results. Plays are
track starts. `listened_seconds` sums started track durations, substituting 420
seconds when unknown; it is an estimate, not observed playback time.

Each show entry contains ordinary `show` and `source` summaries and
`latest_play: { track, played_at }`. The latest track may come from a different
recording than the most-played source. Artist entries use `artist_id` without
repeated artist metadata; on-this-day references use typed `show_ids`. `fresh`
means recent concert dates, not newly imported tapes, and uses the same entry shape.

All four current-window aggregates exclude unsupported app types, orphaned tracks
and missing/inconsistent source sets before counting. Raw artists still take part;
track normalization follows the ordinary approval/fallback policy. Historical
baselines retain the existing hourly/daily aggregate population, which does not
retain app type. V3 eligibility and serialization are unchanged.

Aggregation and batched catalog hydration share one primary read-only repeatable-read
snapshot. V4 reuses domain scoring and low-level SQL, without calling the legacy
summary service, DTOs or JSON cache. SQL failure returns 503/no-store; the next
request can recover. The payload has an ETag and can be shared until the next minute
boundary, then served stale for one further minute while revalidating. There is no
new origin JSON cache. Request-time `generated_at` is omitted so unchanged data can
validate. This online summary does not replace catalog or account membership.

Both service Dockerfiles now copy `Relisten.Identifiers` for restore and publish;
the Docker ignore file excludes its host build artifacts. Previously, solution
builds could pass while production image publication failed. Linux/amd64 API and
account image builds passed locally. Focused HTTP tests cover typed raw/normalized
resources, eligibility, timeline totals, caching/schema, bad queries and dependency
failure/recovery. Disabling eligibility made the regression fail. Existing upstream
tests retain the detailed scoring cases; no second test harness was added.

## Production observations

Read-only checks on November 5 found catalog version 14 and account migrations
through `AddDurableBrowserSessions`. New count/popularity tables are absent.
Both Timescale aggregates exist; TimescaleDB is 2.28.2. No artist has
`features.norm_catalogue` enabled. API has three replicas, its Hangfire worker one,
and ingestion runs separately. Accounts has one replica; confirm its no-surge
strategy before the breaking migration. The image workflow is manually dispatched,
with `deploy` defaulting to **true**. Merging alone does not trigger deployment.

## Merge and rollout

Merge **#109 → #108 → #111 → #112 → #113 → #114 → #115 → #117**. Prefer merge
commits while consuming the stack. After each merge, retarget the next PR to master
before merging it or deleting its parent branch. Squashing/rebasing parents requires
restacking descendants. #116 is obsolete and remains closed.

The complete stack can merge while its components deploy separately:

1. Publish images with `deploy=false`; retain immutable digests. Prepare a recovery
   image that recognizes migrations 15-18 and keeps count-maintaining writers.
   Today's version-14 binary cannot restart on version 18: Production startup
   rejects migration definitions it does not know. Keep the current digest for
   provenance, but do not promise that `rollout undo` alone can recover after DDL.
   Do not use the root `deploy` shortcut for a build-only operation.
2. Verify backup/recovery. Pause imports, normalization writes and admin edits
   during catalog migration. Run the new ingestion-worker image once with
   `--migrate`, using its production database configuration, before rolling API
   replicas. Normal API startup also migrates; do not race the one-shot run.
3. Confirm version 18. Migrations 15 and 16 create **and backfill** the venue/song
   counters; they are not empty tables waiting for later refresh. Migration 18
   reserves canonical IDs and locks song/venue writes during installation. SELECTs
   remain available, but DDL/FK acquisition can wait for or block writers. The
   first migration is not a lock-free rolling operation.
4. Deploy API/Hangfire and ingestion workers, then matching
   [admin PR #3](https://github.com/RelistenNet/relisten-admin/pull/3). Resume writes
   once every writer maintains counts. Failed post-import refreshes require an
   explicit retry; no periodic repair schedule exists.
5. Seed popularity after deploying the new **`relistenapi-worker`**, which executes
   Hangfire jobs. The separate ingestion worker does not. Make one origin request
   each to `/api/v4/popular/artists?limit=1`, `/api/v4/popular/shows?limit=1` and
   `/api/v4/popular/years?limit=1`; missing populations enqueue their calculations.
   For each initial artist, also request `/api/v4/artists/{artist_id}/shows/popular-trending?limit=1`
   and `/api/v4/artists/{artist_id}/years`. Use the new API origin rather than a CDN
   hit. Wait for successful jobs in authenticated `/relisten-admin/hangfire` and
   check stored `generated_at` before re-reading; a first 200/empty list does not
   establish a completed backfill. Avoid repeated polling that queues duplicate work.
   For all artists, the existing **Refresh All Global Popularity Caches** button
   on `/relisten-admin/popularity` also queues `RefreshReadModel`, which visits
   every artist sequentially. This optional full job can take much longer than
   seeding the first cohort. Warm Redis hits do not populate Postgres. Missing
   scores otherwise serve null/empty and request background refresh.

6. Analyze the six new tables below. Compare stored counts for representative
   raw/matched artists and examine actual browse/ranking query plans before changing
   server tuning. Migration 17 includes ranking indexes; no additional index,
   connection-pool expansion or PostgreSQL setting is established as necessary.
   The prior 20.5-second local result measured publication of an existing score
   snapshot, **not** a full production calculation. Backfill storage/WAL and elapsed
   time still need observation during deployment.
7. Smoke-test legacy and v4 routes, gzip/ETags, raw/normalized fallback and summary.
   Check CDN sharing and primary query latency for summary before broad traffic;
   small local telemetry does not establish its production load. Enable normalized
   reads for a reviewed artist cohort only after validating data/counts. Deploying
   API leaves today's all-off approval flags unchanged. No full-catalog warm-up or
   combined artist endpoint is needed.
8. Cut over **accounts separately**, once compatible alpha mobile/web clients are
   ready. Stop old account writers, take a whole-account backup and use one no-surge
   replica so EF finishes before serving. Typed conversion preserves users/provider
   links/favorites but revokes sessions and requires re-sign-in. The release iOS
   registration changes to its HTTPS callback. Finish release-signed physical-device
   associated-link/provider checks before broad mobile release; simulator sign-in
   does not establish that behavior.

After backfill, run as the database owner:

```sql
ANALYZE norm.artist_venues;
ANALYZE norm.song_counts;
ANALYZE public.setlist_song_counts;
ANALYZE public.artist_popularity;
ANALYZE public.show_popularity;
ANALYZE public.year_popularity;
```

Catalog rollback keeps the additive schema, stable IDs/counts and Redis. Use a
prepared schema-18-aware recovery image or a forward fix; today's pre-stack API
cannot restart on the new schema. Once mobile uses v4, its fallback must also
serve v4. Do not drop derived tables. Account rollback after conversion needs a forward
fix or coordinated restore; the old binary alone cannot read typed storage, and
restoring an old backup can discard accepted writes. Normalization read flags can
be disabled independently while imported playback remains available.

No PR was merged, image published or production schema, data, flag, registration or
deployment changed by this review. Verification results belong to the refreshed PRs;
release-signed device and production load checks remain deployment work.
