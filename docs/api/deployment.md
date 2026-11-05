# Deploy the catalog and account changes

Merge the API stack after review, then deploy catalog and accounts in coordinated
stages. Merging does not deploy an image. Catalog schema/backfill comes first;
all writers must maintain derived counts before edits resume. Accounts have a
separate breaking alpha cutover requiring tester re-sign-in.

Use fix-forward recovery. Preserve installed schemas, stable identities and
accepted writes; pause affected writes if correctness is at risk. Backups protect
against data loss. No separate recovery image or return to a pre-stack binary is
required. A version-14 API cannot restart on catalog version 18.

## Pre-release observations — November 5, 2026

Read-only checks on November 5 found catalog version 14 and account migrations
through `AddDurableBrowserSessions`. New count/popularity tables are absent.
Both Timescale aggregates exist; TimescaleDB is 2.28.2. No artist has
`features.norm_catalogue` enabled. API has three replicas, its Hangfire worker one,
and ingestion runs separately. Accounts has one replica; confirm its no-surge
strategy before the breaking migration. The image workflow is manually dispatched,
with `deploy` defaulting to **true**. Merging alone does not trigger deployment.

## Merge and rollout

This is native GitHub stack **#119**, in order
**#109 → #108 → #111 → #112 → #113 → #114 → #115 → #117**. All eight have the
correct parent branches and share current master ancestry. Use the stack merge
box on top PR #117 to merge all eight after reviews/checks pass. GitHub supports
merge, squash or rebase for the whole stack and preserves the per-PR ordering.
Alternatively merge a bottom-up subset; GitHub automatically rebases/retargets
what remains. Do not manually edit the base branches: GitHub owns those links
while the PRs belong to a native stack. #116 is obsolete and remains outside it.
See [GitHub's stack merge instructions](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/merging-stacked-pull-requests).

The complete stack can merge while its components deploy separately:

1. Publish images with `deploy=false`; retain immutable digests for provenance.
   Fix forward after deployment; no separate recovery image is required. Today's
   version-14 binary cannot restart on version 18, so a correction must retain
   the installed schema and current contracts. Do not use the root `deploy`
   shortcut for a build-only operation.
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

Fix forward for catalog and account issues. Preserve the additive schema,
stable IDs/counts, Redis data and accepted writes. Pause affected writes when
correctness is at risk, then deploy the correction against the installed schema.
Do not prepare a recovery image, revert to pre-migration binaries, or downcast
account IDs. Backups remain protection against data loss, not the routine response
to an application bug. Normalization read flags can be disabled independently
while imported playback remains available within v4.

These observations record the pre-release review, not live deployment status.
Recheck deployed versions and flags before acting. Release-signed device and
production load checks remain deployment work.
