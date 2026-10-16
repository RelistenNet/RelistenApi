# Production Temporal cutover — October 16, 2026

The API and ingestion worker were deployed directly from the reviewed branches,
without merging either pull request. Temporal owns ingestion. Hangfire retains
its `default` queue for cache jobs.

The rollout uses one private Temporal 1.32.1 server, seven-day closed-history
retention, two databases on the existing PostgreSQL cluster, and a separate
persistent Redis for workflow payloads. The existing full and slim backups select
only `app`, so they exclude both Temporal databases. See the
[infrastructure PR](https://github.com/RelistenNet/relisten-flux/pull/22) and
[cutover procedure](../production-cutover.md) for setup and recovery.

## Tested release

Both images contain application commit `bd33ae2454d05df4e1ca42509625ed3226d14e50`:

- API and legacy cache worker: `ghcr.io/relistennet/relistenapi@sha256:b73e759377b90aa692f9562baaee03cec0db2c0c6a42297d0ddb4b59742b092f`.
- Ingestion worker: `ghcr.io/relistennet/relistenapi-ingestion-worker@sha256:f5d306d3afc955913a7b50360e2cea1b1c1cb6557a9f95d4f87a20bbbc6c9a68`.

The [API build](https://github.com/RelistenNet/RelistenApi/actions/runs/38944529410)
and [worker build](https://github.com/RelistenNet/RelistenApi/actions/runs/38944533077)
published commit tags without changing `latest`. Later CI deployments set the
built digest explicitly so they can advance these pinned deployments.

## Provider checks

- **Archive:** `guerilla-toss`, item `GT2026-06-01.vwmule`. The import and repeat
  each completed one item with no failures. A separate artist rebuild completed.
  The recording has eight tracks; source and track identities, order, playable
  URLs, venue, and artist show count remained stable.
- **Phish.in:** `phish`, lower year bound 2026. After the sorting fix below, all
  42 selected recordings completed with no failures.
- **Phish.net:** `phish`, exact year 1983. After the timestamp fix below, one
  recording completed with no failures. The source retained its UUID and two
  track UUIDs. Seven reviews were stored and returned by a fresh public API read.
- **Setlist.fm:** `guerilla-toss`. All 431 selected setlists completed with no
  failures. The run exercised continuation as well as item activities.
- **Phantasy Tour:** `lotus`. Discovery and date finalization completed with no
  missing setlists selected.
- **Jerry:** `grateful-dead`. Mirror discovery completed with no missing pages
  selected.
- **Local:** `trey`. Root metadata and venue preparation completed with no changed
  dates selected.

The last three checks exercised live discovery, prerequisites, finalization, and
artist rebuilds. They did not exercise new item writes because the catalog was
current. Fixture-backed integration tests cover those writes without removing
production data to manufacture missing items.

The Setlist.fm recovery run started on pod
`relistenapi-ingestion-worker-59c4bc45c5-jn9x2` and resumed on replacement pod
`relistenapi-ingestion-worker-5bb989c79f-wldvq`. History records workflow tasks from
both identities in the same artist execution, from 17:55:04 through 17:55:16 UTC.
All 431 items completed. This verified a graceful worker handoff; it did not force
an activity timeout or database crash.

## Issues found and fixed

Phish.net returns review timestamps such as `2010-09-27 17:30:43-04`. The existing
exact date format rejected the offset. The first run returned a partial result;
the failed transaction left the source and reviews unchanged. The client now
accepts offsets using invariant culture and normalizes explicit offsets to UTC.
A public-client fixture test covers hour-only, full, and absent offsets under a
non-English culture.

Phish.in requires both `sort_attr` and `sort_dir` to apply sorting. Supplying only
`sort_attr=date` returned unordered pages, so the year-filtered importer stopped
early and incorrectly selected zero shows. The shared request builder now asks
for ascending order. Public pagination tests cover complete full and filtered
selections. Live first and last pages confirmed chronological dates with both
parameters. The [upstream sort implementation](https://github.com/jcraigk/phishin/blob/main/app/controllers/api/v1/api_controller.rb)
defines this contract.

Schedule registration initially failed before creating any schedules because the
SDK rejects an explicit workflow ID conflict policy on scheduled actions. The
shared options now leave that policy at the SDK default, which still rejects
overlapping direct starts. A real-server test registers schedules, preserves an
operator pause on repeated registration, and triggers a thin-refresh workflow.
Existing admission conflict tests also pass.

The replacement worker also encountered the node's limit of 128 inotify
instances while constructing .NET configuration file watchers. Its manifest sets
`DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false`. Configuration changes arrive
through pod rollouts; the worker does not need file-change notifications.

## Verification and limits

The full local suite passed **267 tests**, with zero failures and 24 existing tour
fixture skips. CSharpier checked 370 files. Workflow lint and deployment-script
checks passed, including the exact container names for all three CI components.

Production checks use public artist/show responses, source and track identities,
workflow results and history, read-only SQL, readiness, and resource metrics.
Ordinary public requests can return an earlier Cloudflare response for 30 minutes.
Use a unique query parameter and confirm a cache miss when checking newly imported
metadata. The corrected Phish.net response returned seven reviews through this
fresh-read check; an older cache hit still returned zero.

The manual thin batch `ingest/thin/8646f3f4-1317-42dd-8ec4-65db725f547e`
completed **46 artists**, with zero partial results, failures, or admission
conflicts. It ran with schedules absent and exercised the normal ten-artist
concurrency limit. Selection used the ordinary recently active artist rule;
Archive and Phish.net used exact-year 2026 filters, and Phish.in used 2026 as its
lower bound.

Fresh public comparisons preserved all source, set, and track UUIDs, track order,
playback URLs, and artist counts in eleven representative responses. Expected
changes included seven reviews on Phish's 1983 show, eleven on its October 12,
2026 show, a derived year-rating update, and Lotus's tour start date corrected by
Phantasy Tour finalization. Live popularity values also changed. The Archive
canary, Dead show, and Trey show responses were fully unchanged.

## Operating state

All three Temporal schedules are active with overlap policy `Skip`:

- `relisten-ingest-thin`: next at October 16, 20:00 UTC (13:00 PDT).
- `relisten-ingest-discover`: next at October 17, 06:30 UTC (October 16, 23:30 PDT).
- `relisten-ingest-full`: next at October 17, 07:00 UTC (00:00 PDT).

After registration, the thin schedule was explicitly triggered. Its execution
`ingest/scheduled/thin-2026-10-16T18:07:17Z` completed **46 artists**, with zero
partial results, failures, or conflicts. This tested the actual scheduled action
and batch workflow; it did not wait for the next cron boundary. The overnight
full catalog and artist-discovery runs have not yet executed under Temporal.

Both API deployments select Temporal admission and the dedicated payload Redis.
Hangfire has one live server polling only `default`; its three saved ingestion
recurring definitions are removed. One ingestion worker remains running, and the
tracked manifest retains that replica and the tested image digests.

The independent final check found listener readiness passing, healthy PostgreSQL
replication, and stable fresh public API identities and playback URLs. During the
scheduled thin run, the worker used about 0.83 CPU cores and 245 MiB; its node used
45% CPU and 51% memory. Replica replay lag was 0.037 seconds. These are observed
canary loads, not capacity guarantees for the overnight full import. Payload Redis
reported healthy AOF writes and no evictions.

Both pull requests remain unmerged. Use the cutover procedure to return ownership
to Hangfire if needed. Preserve committed catalog changes and stop Temporal work
before enabling legacy ingestion.
