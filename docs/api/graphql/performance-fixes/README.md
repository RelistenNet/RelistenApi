# Catalog performance fixes

The [implementation plan](../../../plans/active/2026-11-06-graphql-performance-fixes.md)
prioritizes playback and scoped pagination. Search-specific work remains deferred.
Measurements use the existing local catalog, approximately 216k shows and 5.1m
tracks, with serial HTTP requests and one first plus three warm samples per case.
These are local medians/ranges, not production percentiles.

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

## Remaining work

Finish the full suite, independent validation and final corpus benchmark. No migration, persistent index,
new caching layer, deployment or search-specific fix has been made.
