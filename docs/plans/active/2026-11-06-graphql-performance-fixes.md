# Make catalog pages and song-history playback fast

Status: implementation in progress on `codex/v4-catalog-graphql-plan`. The review is
complete. This ExecPlan is a living document.
Keep Progress, Discoveries, Decision Log and Outcomes current.

## Purpose

Make the existing web queries practical against the full catalog while preserving
the shared REST readers, projections, identities and interpretation rules. Start
with song-history playback and scoped pages. Search is a separate, low-priority
follow-up because Alec expects to replace it soon.

The measured starting point is Scarlet playback at 3.0 seconds for ten shows and
6.4 seconds for 25, and Drums playback at 12 seconds for 25. Those responses each
contain one selected file per show. Hydrating 250 known files takes 36 ms, so the
first work is selecting files efficiently, rather than replacing hydration.

## Progress

- [x] (2026-11-06) Reproduce latency on the full local catalog and independently
      validate the SQL causes. Evidence is under `docs/api/graphql/review/`.
- [x] (2026-11-06) Apply Alec's priority: playback and pagination first, search last.
- [x] (2026-11-06) Batch song matching and restrict shows before source ranking.
  Six real-catalog comparison cases retain identical JSON; Scarlet25 falls from
  6.1 seconds to 637 ms and Drums25 from 13 seconds to 1.05 seconds. See
  `docs/api/graphql/performance-fixes/` for fresh before and stage measurements.
- [ ] Replace broad owner selection with explicit scoped SQL.
- [ ] Omit timing statistics when the operation does not select them.
- [ ] Rerun representative full-catalog workloads and affected REST reads; commit
      the new evidence and remaining limitations.

## Discoveries

- Ten nested track requests execute `norm.song_recording_matches` ten times and
  rank all matching sources ten times inside one command. That command takes
  2.6 seconds and 3.03m buffer hits. Batching commands alone did not batch the work.
- Artist/song pages visit approximately 216k shows across 4,387 artists. An
  explicit artist-scoped membership SELECT takes 1.75 ms using an existing index,
  versus 678 ms for the generic selector. The experiment excludes hydration.
- `SongShows` currently controls both wrapper construction and statistics loading.
  Consequently a dates/venues-only page still calculates timing statistics.
- The matching function deliberately validates all resolved sibling parts using
  the complete artist merge map. A performance fix must retain that validation.

## Decision Log

- Alec, 2026-11-06: search is probably the least important endpoint and will be
  replaced soon. It must not lead the implementation sequence.
- Implementation recommendation: first remove repeated work using the existing
  matching function and indexes. Measure before changing the function or schema.
- Retain Hot Chocolate loaders, shared catalog readers and `V4CatalogProjection`.
  Preserve short read-only repeatable-read units and ordinary failure propagation.
  Retain the existing generous cost limits and 20-second cancellation deadline.
- This plan fixes existing operations. Song-index filters/count ordering, global
  duration ordering, population summaries and analytics remain a later capability
  change; they are not prerequisites for fixing current playback latency.

## Context and interfaces

`RelistenApi/Services/Catalog/CatalogCollectionReader.cs` selects collection members
and cursor keys before hydration. REST search/discovery/browse and GraphQL call it.
Its JSON input carries request ordinals, owner, filters, sort and page boundaries.
Each request must retain its independent limit and output position.

`CatalogSongMatchReader.cs` owns shared matching/statistics/source-selection reads.
REST's `ReadShowTracks` already matches once and ranks sources by show. Reuse that
SQL structure and `CatalogSourceRanking.OrderBy`; do not copy its complete output
into memory and paginate afterward. The common membership definition lives in
`Relisten.Normalization/Sql/016_song_counts.sql` as `norm.song_recording_matches`.

`RelistenApi/GraphQL/CatalogGraphOperation.cs` owns read units and immediate
hydration/projection. `CatalogPageLoad` is its loader key. `CatalogGraphFields.cs`
builds that key from field arguments and selected fields. `SongShow` wraps a show
with song context and optional duration samples. It must exist even without a
statistics query, so that subsequent track traversal keeps its song/show scope.

## Plan of work

### 1. Batch song-track selection in the shared reader

Change the song-backed track selection in `CatalogCollectionReader`, extracting a
cohesive song-page method into `CatalogSongMatchReader` if that makes ownership
clearer. Gather distinct artist/song populations across the compatible batch.
Materialize matches once per population in an outer SQL CTE, outside the
per-request lateral page query. Keep rows in SQL rather than transferring the
complete match population into C#.

Join those matches to the requested shows before ranking sources. Rank distinct
matching sources per show with `CatalogSourceRanking.OrderBy`, then apply TOP/ALL
selection and the existing per-parent cursor/limit. Quality filters continue to
affect eligible sources before ranking. Preserve intersections when both owner
song and filter song are supplied; sharing a match computation must not union
away their constraints. Keep request ordinals when assembling results.

The source-matching definition, merged aliases, orphan exclusion, invalid sibling
handling, file order and complete recording hydration remain shared with REST.
Each loader fetch owns its own read unit: computing matches once per population
in that command does not promise one match computation across an entire HTTP
operation. Do not introduce cross-request caches or a transaction-wide framework.

Proof: repeat the ten-parent plan. It must no longer execute whole-song matching
and whole-history source ranking ten times. Compare returned IDs/order for TOP,
ALL, multiple matching files, merged songs and quality-filtered selections.

### 2. Scope membership before joining the catalog

In `CatalogCollectionReader`, choose explicit server-owned SQL for the relevant
owner: artist, year, tour, song, canonical venue/artist-venue and imported owners.
Resolve UUID owners to internal IDs or normalized membership once, then join only
their candidate shows. For song pages, start with matching show IDs; for venue
pages, start with approved venue membership. Keep normalization feature gates,
merge rules and imported fallback behavior.

Group requests only as needed to select compatible SQL. Keep this as named domain
queries, not a new SQL AST or general query-planning framework. Values remain
parameters. Preserve the existing complete ordering tuple and cursor encoding;
use native date/UUID predicates and compatible casts where that helps the plan.
Every parent still gets its own limit before hydration. Counts run only when
requested, over the same scoped population and snapshot as the page.

Start with the measured artist/song/venue cases. Exercise other owner kinds before
replacing their paths. Global browse legitimately considers a global population;
the invariant is that an owner-scoped page does not traverse unrelated artists.

Proof: first/next pages visit the owner's population, not all 216k shows. Verify
unchanged membership/order, equal-key boundaries, filter intersections and counts.
Recheck REST discovery and browse using the same reader.

### 3. Separate song wrappers from requested timing data

Add an explicit statistics-selection flag to `CatalogPageLoad`, distinct from
`SongShows`, and include it in compatible loader grouping. Detect `timings` under
both `nodes` and `edges.node` using the existing Hot Chocolate selection helpers.
Honor fragments, aliases and conditional inclusion.

`ReadPageGroup` calls `ReadShowStatisticsBatch` only when statistics are selected.
`ProjectPageNode` constructs `CatalogGraphSongShow` based on `SongShows`, regardless
of whether statistics were loaded. Selected timing fields retain their existing
per-source maximum and single/multi-song sample definitions. REST timing reads
remain unchanged.

Proof: a rows-only operation returns proper song/show wrappers and traversable
tracks with no statistics command. A timing-selected variant returns the existing
values. Mixed branches in one request must not share an incompatible loader result.

### 4. Measure remaining work, then consider narrower matching or indexes

Rerun the same 20 GraphQL cases and affected REST cases. Initial engineering
targets on this same warm local catalog are under 500 ms for a 25-row history page
and under one second for 25 shows with TOP playback, including Drums. These are
targets to test, not measured promises or production SLOs. Use multiple serial
samples and report ranges alongside medians. Keep the baseline evidence intact.

If matching remains dominant after it runs once per population, the known next
step is to restrict candidate files to selected shows inside the shared matching
function, before grouping and sibling validation. Add an explicit scoped overload
through a new normalization migration; keep the existing unscoped signature and
results available to REST/recounts. Share one implementation, preserve the complete
artist map, and compare scoped results with filtering the original function's
output. Do not edit an already-applied migration. Inspect the migration runner and
current schema before implementing this step.

Add an index only when the resulting plan shows a remaining access problem.
Existing artist/date, source/show, track/source and song-match indexes already
support the first fixes. No search-specific indexes, trigram rollout, cache
invalidation system or materialized aggregate design belongs in this pass.

Search remains recorded as an open low-priority regression. Measure any incidental
improvement from shared changes, but handle its remaining correlated-count fix
separately from completing the playback/pagination work.

## Concrete steps and verification

Work from the repository root. Start local databases using the documented helper;
use the existing catalog without imports or replacement data. Keep the review's
`results.json` immutable. Rebuild/restart the development API when measuring changed
code and record the actual running source commit.

Run focused checks after each change:

```sh
./test-local.sh --filter 'FullyQualifiedName~RelistenApiTests.V4'
dotnet build RelistenApi.sln
dotnet csharpier check .
```

Keep lasting tests for selection/order, sibling validation, independent parent
limits, omitted-versus-selected statistics, cursor ties and snapshot consistency.
Use actual database execution for reader behavior; do not test SQL source strings
or prescribe private call order. Add query instrumentation only where needed to
prove unselected statistics do no work. Reuse existing parity fixtures where they
already assert the contract.

For actual-catalog measurements, use the review's documented baseline setup and:

```sh
python3 docs/api/graphql/review/benchmark.py \
  --current-commit ACTUAL_RUNNING_COMMIT \
  --output /tmp/relisten-graphql-after.json
```

Capture new EXPLAIN ANALYZE/BUFFERS plans for scoped first/next/count pages and
one/ten/25-parent track pages. Compare executed loops, visited rows and buffer
work, not just the number of SQL commands. Keep returned payloads or stable hashes
for parity comparison. Check REST song detail/top tracks, basic artist shows,
discovery and artist/venue browse. Record search separately with its lower priority.

Before completion, run the full `./test-local.sh`, inspect the formatted code for
readability, trace selected/omitted-statistics and failed-read paths, and publish
before/after evidence. Commit related changes as each milestone is verified;
run `dotnet csharpier check .` and `git diff --check` before each commit.

## Idempotence and recovery

The initial changes are reader-only and need no catalog writes, index creation,
backfill or production deployment. Repeating tests and read-only benchmarks is
safe. A failed read fails its existing unit; later requests open fresh sessions.
If a scoped function becomes necessary, its additive migration must retain the
old entrypoint so reverting application code remains possible. Deployments and
production schema changes are outside this local implementation plan.

## Outcomes

Planning complete; implementation and before/after qualification remain open.
Completion means the demonstrated redundant work is removed, useful playback
queries meet the measured targets, and the affected non-search REST behavior and
performance remain intact. A missed target keeps performance qualification open;
record the remaining cause and next concrete change. Additional UI
capabilities and the search replacement remain separate work.
