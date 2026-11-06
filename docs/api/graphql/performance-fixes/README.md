# Catalog performance fixes

The [implementation plan](../../../plans/active/2026-11-06-graphql-performance-fixes.md)
prioritizes playback and scoped pagination. Search-specific work remains deferred.
Measurements use the existing local catalog, approximately 216k shows and 5.1m
tracks, with serial HTTP requests and one first plus three warm samples per case.
These are local medians/ranges, not production percentiles.

## Verified result

Application commit `a6c302e12` meets the plan's local targets using the existing
indexes and matching function. Warm HTTP medians from the fresh before run and
the repeated final corpus are:

- Scarlet playback, 25 shows: **6,066 → 156 ms**, final warm range 144–170 ms.
- Drums playback, 25 shows: **13,021 → 548 ms**, range 538–559 ms.
- Scarlet history, 25 rows: **502 → 51 ms**; exact-count variant **1,033 → 61 ms**.
- Drums history, 25 rows: **771 → 196 ms**, range 169–246 ms.
- Venue history, 25 rows: **940 → 28 ms**, range 27–39 ms.
- Artist shows, 25 rows: **515 → 29 ms**; artist navigation **181 → 87 ms**.
- Source comparison: **579 → 93 ms**. Hydrating 250 known tracks remains fast at 33 ms.

All four samples in each of the 20 final GraphQL cases and 12 current REST cases
have matching JSON payload hashes against this branch's before run. Arrays retain
their order; object keys are canonicalized for hashing. Requested operation costs
remain unchanged, including the configured generous execution limits.

REST artist songs improve from 144 to 79 ms. Other non-search REST samples remain
near the branch's before measurements; the raw results retain ranges. The older
pre-GraphQL baseline still has faster artist-venue browsing: about 15 ms versus
25 ms here, compared with 30 ms on this branch before these fixes. That existing
overhead remains visible in the evidence. Search-specific regressions remain
deferred as requested.

The full `./test-local.sh` run passes **634 API tests and 366 normalization tests**,
with 37 existing API skips. The solution build passes. Both independent review
passes found no actionable correctness or readability issues. A real-catalog
recording fetched through a source connection also matches the direct source's
complete selected sets/tracks.

## Final evidence and scope

[after.json](after.json) contains the repeated final corpus; [after-initial.json](after-initial.json)
retains the first final run after restarting the API. Both use the same committed
application and preserve the before payloads. The initial run has more variation
on short operations, including song metadata at 63 ms versus 9 ms in the repeat,
although its implementation is unchanged. A subsequent control check observed an
Xcode build on the shared machine and metadata settling near 10 ms. This was not
an isolated load test; the evidence does not attribute all variation to one cause.
Both final passes meet the row/playback targets. The headline numbers use the
repeat after a complete corpus warmup. Variables are unchanged and referenced
from `before.json` to avoid duplicating the 250-ID playlist in every result entry.

[sql-results.json](sql-results.json) and [executable read-only SQL](sql/) record
nine final EXPLAIN observations, separately from the HTTP samples:

- Artist membership visits its 2,081 shows, rather than all 4,387 artists and
  approximately 216k shows. The 25-row query takes 25.7 ms and 23,701 buffer hits.
  The review's earlier ten-row generic query took 678 ms and 2.35m hits.
- Song membership visits its 332 matching shows. First/next/count queries take
  79/48/51 ms in these single observations; matching executes once in each command.
- Venue membership visits 71 shows: 2.0 ms and 1,222 buffer hits.
- One/ten/25-parent track commands take 45/55/66 ms. Each computes the 3,355 song
  matches once. The ten-parent command ranks an average of seven sources per show
  and uses 70,311 buffer hits, versus the original 2,627 ms and 3.03m hits.

The artifact omits unexecuted plan branches. Parent buffer counts include children
and must not be added. Show pages use first 25; nested track pages use first 20 per
parent. Existing read-only snapshots, matching validity and per-parent limits are
preserved. The optional scoped matching-function migration was unnecessary.

This qualifies the demonstrated owned/song/venue web corpus, complete recording
reads and queue hydration locally. Arbitrary global track browsing, every possible
fan-out and sustained production concurrency remain unqualified. Additional UI
filters, aggregate summaries and duration ordering are separate capability work.

To rerun, start the current API on 3823 and the pre-GraphQL baseline on 3824 as in
the [review setup](../review/README.md#reproduce), then use:

```sh
python3 docs/api/graphql/review/benchmark.py \
  --current-commit ACTUAL_RUNNING_COMMIT \
  --output /tmp/relisten-graphql-performance.json
```

## Before

[before.json](before.json) is a fresh run of the review benchmark against the
unchanged GraphQL implementation, whose running application source was `41813085b`.
The later commits before this run only changed documentation. Its REST comparison
uses pre-GraphQL `5087f5a39` on port 3824 against the same database.

Scarlet playback takes 2,867 ms for ten shows and 6,066 ms for 25. Drums playback
takes 13,021 ms for 25. A Scarlet page with exact count takes 1,033 ms. The original
review evidence remains unchanged under `docs/api/graphql/review/`.

## Stage 1: shared matching and bounded source ranking

The collection reader materializes distinct requested song populations once,
outside each parent's page query. Song-backed tracks start from matching file IDs.
Source ranking considers the selected shows before calculating ranks. Matching
still uses the original normalization function and complete artist merge map;
ranking uses `CatalogSourceRanking`. Counts reuse the same matched population.

[stage1.json](stage1.json) records six selected before-corpus cases against the
working source committed with this report. All six completed successfully with
identical JSON payload hashes to the fresh before run:

- Scarlet playback 1 / 10 / 25 shows: 591 / 618 / 637 ms.
- Drums playback 25 shows: 1,054 ms.
- Source comparison: 181 ms.
- Scarlet page with exact count: 1,026 ms; its owner scan remains for stage 2.

The [ten-parent SQL observation](stage1-sql.json) takes 64 ms and 70,655 buffer
hits. Matching executes once, returning 3,355 rows. Source ranking executes per
parent over an average of seven matching sources, rather than each parent's
whole song history. The review's earlier ten-parent plan took 2,627 ms and 3.03m
hits. These are single EXPLAIN observations, separate from HTTP sampling.

The focused V4 suite passes 193 tests. Its new HTTP case exercises independent
per-show limits, TOP/ALL source selection and two simultaneously requested songs
whose file populations overlap. Existing intersection and REST parity tests pass.

## Stage 2: owner-scoped membership

The shared collection reader now chooses explicit owner/filter predicates and
starts show pages from the owner's membership. Venue EXISTS filters and paged
membership share the same merge-aware row definition. Song/venue browsing also
uses explicit artist scope, including the imported fallback branches.

[stage2.json](stage2.json) records the full corpus, with variables unchanged from
`before.json`. All 20 GraphQL payloads and all 12 REST payloads match the same
branch's before run. The known pre-GraphQL search payload difference is unchanged.
Warm medians include:

- Scarlet history rows / rows with count: 95 / 88 ms; playback 25: 144 ms.
- Drums rows / playback 25: 299 / 533 ms.
- Artist shows / artist navigation: 34 / 95 ms.
- Venue history / filtered history: 24 / 19 ms.
- REST artist songs: 73 ms, down from the before run's 144 ms.

The V4 suite passes 194 tests. The new owner test compares artist, year, tour,
canonical/imported venue, imported song and artist-venue collections against REST,
including a conflicting song/artist filter and a zero-count page. Search-specific
selection/ranking remains unchanged.

## Stage 3: selected timing and recording details

`SongShows` now controls wrapper construction independently of the statistics flag.
Only a selected timing field requests live statistics; the flag participates in
loader identity and grouping. Source connections likewise load complete recordings
only when sets or links are selected. Both nodes and edges use the same selection
helper, honoring aliases, fragments and execution conditions.

The old `IsSelected("nodes", "sets")` usage checked alternative siblings, so every
nodes selection loaded complete recordings. Use explicit `Select` traversal and
the single-field `IsSelected` overload. In the pinned 16.7.3 implementation, the
multi-field selection-collection overload also fails to unwrap list types. The
single-field overload handles them. See the upstream
[resolver extension](https://github.com/ChilliCream/graphql-platform/blob/16.7.3/src/HotChocolate/Core/src/Types/Extensions/ResolverContextExtensions.cs)
and [selection collection](https://github.com/ChilliCream/graphql-platform/blob/16.7.3/src/HotChocolate/Core/src/Types/Execution/Processing/SelectionCollection.cs).

[statistics-selection.json](statistics-selection.json) records sequential HTTP
requests and completed statistics SQL observed in the development API log:
omitted/selected timings change from 1/1 commands to **0/1**, with 25 shows in both
responses. The full V4 fixture passes 197 tests. New cases cover selected/omitted
timings through aliases, fragments and directives, and ensure a header-only source
page fits a row budget that rejects its complete recording. Restoring the old
sibling check makes that regression test fail; restoring the fix makes it pass.

## Operational outcome

The fixes require no migration, persistent index, backfill or new caching layer.
No deployment occurred; search-specific changes remain deferred. The development API is left running with the
updated application; the temporary pre-GraphQL comparison server is stopped after
qualification. Production rollout remains separate.
