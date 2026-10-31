# Review and roll out the first legacy recording adapter

Operator artist writes and import commands remain at their existing v2 paths.
The endpoint manifest lists eight retained operations separately from the 51 v4
catalog/playback operations. The separate relisten-admin app uses direct Postgres
actions; this change does not migrate either admin UI.

## What changes

The first adapter covers legacy full-show responses and their nested sources,
sets and tracks. Legacy has no standalone source/track detail routes. Show
metadata, artist feature gating, v2/v3 serialization, date aliases and cache
headers stay in their existing transport/service path. Generic show-list reads
remain unchanged until that family is migrated.

Native show/source reads and the enabled adapter share `CatalogRecordingReader`.
The first command selects sources; the second batches sets, playable tracks and
links using those source IDs. Each source description is materialized once.
Native reads keep their primary
snapshot and normalization overlay; the adapter uses the existing replica
connection and imported fields. Native reads retain empty sets; the legacy
projection emits playable sets only.

One empty-source edge case improves: a source with no playable tracks now returns
`sets: []` in the enabled adapter. The old loader throws while looking up that
source's missing set group. This exception behavior is deliberately not copied.

With the adapter enabled, full-show popularity reads only the selected show's
artist-local Postgres score, using the same lookup as v4. It serves stale scores
and queues a refresh through the existing population job after reads finish.
Legacy score/freshness reads prefer replicas. No score calculation happens in
the request. Other legacy families keep their current popularity paths.

## Default-off rollout

`Catalog:LegacyRecordingAdaptersEnabled` defaults to false. Its environment name
is `Catalog__LegacyRecordingAdaptersEnabled`. Changing it takes an API restart.
It switches the recording loader
and full-show popularity together. Leave it false during the popularity backfill.

1. Deploy the existing dual-write popularity calculation and read-model schema.
   Keep the adapter flag off; old clients continue using the original loader and
   Redis scores. Migration and production deployment remain separate actions.
2. Run the existing Hangfire job `PopularityJobs.RefreshReadModel` once. It
   publishes global and artist-local populations to both Postgres and Redis,
   including rows for inactive targets. For a targeted retry, the existing
   built-in admin POST `/relisten-admin/popularity/refresh-shows-map?artistUuid=…`
   queues one artist's show map under current operator authorization. A queued
   handle is not proof that publication completed; check job completion/errors.
3. Run [the read-only coverage query](../../../../../local-dev/v4-review/legacy-popularity-readiness.sql).
   Require `missing_shows = 0`, review publication age and job failures, and compare
   representative scores with the existing Redis results. A NULL momentum score
   in an existing row means unscored activity; an absent row means missing data.
   Do not fill absent populations with fabricated zeroes. New catalog imports can
   require a targeted refresh before the final check.
4. Enable the flag in a local/staging API and restart it. Compare the routes below
   and their complete JSON/headers. Repeat production readiness checks against
   the production database before enabling the production flag and rolling API
   instances. Do not infer production readiness from the local snapshot.
5. If responses, latency or jobs regress, set the flag false and roll/restart the
   API. This restores the old recording/Redis path without deleting Postgres
   populations or rolling back schema. Remove the old loader and switch only
   after the production rollout is qualified.

The local review host can be started with:

```sh
Catalog__LegacyRecordingAdaptersEnabled=true ./local-dev/v4-review/run.sh
```

The restored local popularity snapshot has real values but old timestamps. It is
used for response comparison, not proof of fresh production metrics. No local
backfill or worker is run: local telemetry is incomplete and would replace the
saved real aggregate data.

## Response and performance checks

Compare the raw, partial and large real catalog captures under the ignored
`local-dev/v4-review/.output/legacy-recordings/` directory. The before/after JSON
files and timing summaries retain exact paths and response sizes. Compare full
objects, including array order, UUID/numeric identities, URLs, imported text,
timestamps, links and popularity—not selected fields alone. Keep headers too.

Representative routes:

- [Large v2 show: 38 sources](http://127.0.0.1:43823/api/v2/artists/grateful-dead/shows/1983-10-15)
- [The same v3 show](http://127.0.0.1:43823/api/v3/shows/bc2acb62-6b75-7a1e-7b41-842e0aaf4140)
- [Imported-only show](http://127.0.0.1:43823/api/v2/artists/spafford/shows/2026-09-15)
- [Partial interpretation in native v4](http://127.0.0.1:43823/api/v4/shows/show_67xxr3g58g2y59tctv16a0s8kw)

For a controlled comparison, run the original build on port 43824 and the adapter
build on 43823 against the same local database/Redis, with workers disabled. Then:

```sh
python3 local-dev/v4-review/compare-legacy-recordings.py \
  http://127.0.0.1:43824 http://127.0.0.1:43823
```

The script uses only GETs, warms each route four times, alternates builds for
15 samples, compares complete JSON and cache/ETag headers, and saves captures and
timings in the ignored output directory. It exits nonzero on response differences;
latency changes require judgment. The original build for this slice is commit
`92b4ff5e5`. Use a disposable checkout for that build; do not reset the work branch.

Warm each route before sampling. Measure with no imports/workers or concurrent
test builds running. Count Npgsql command completions after log flushing; a batch
is one round trip containing three SELECT statements. Local warm timings are
comparative samples, not production latency guarantees. Verification results and
material differences belong in [the verification log](../verification.md).
