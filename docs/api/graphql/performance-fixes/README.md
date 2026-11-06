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

## Remaining work

Scope membership before catalog joins, omit unselected timing statistics, then
rerun the full corpus and affected REST routes. No migration, persistent index,
new caching layer, deployment or search-specific fix has been made.
