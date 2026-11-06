# Discover bounded song histories from ordered shows

This living plan tracks show-first pagination on `codex/v4-catalog-graphql-plan`.

## Purpose

Return song-history pages without validating every recording of a frequently
performed song first. Keep REST and GraphQL readers, matching rules, projections,
cursor ordering and read snapshots shared. Search-specific work stays deferred.

## Progress

- [x] Prototype ordered candidate shows against the existing real local catalog.
- [x] Capture fresh dashboard, sparse-history and non-search REST baselines.
- [x] Implement bounded candidate scans and scoped matching in shared readers.
- [x] Verify continuation, lookahead, filters, counts, snapshots and sparse fallback.
- [x] Benchmark the same requests, simplify, independently review, run checks and commit.

## Discoveries

The current `CatalogCollectionReader` materializes all song matches before it can
apply a show-page limit. The existing three-argument `norm.song_recording_matches`
accepts selected show UUIDs and retains full artist merge-map and sibling validity.
A read-only prototype found the original first 11 Drums shows among 32 candidates
in 14 ms warm, and Scarlet's first 11 among 128 candidates in 34 ms. These are SQL
observations, not HTTP results. Sparse and old-only histories can make a show scan
more expensive than the original song-first query; measure and bound that work.

The first implementation used a 1,024-file threshold. An alternating old/new
comparison found Drums pages at 158/34 ms but Scarlet at 52/93 ms. Raising the
threshold to 4,096 retains the cheaper song-first path for Scarlet and sparse
histories. The threshold only selects a strategy; it is not a membership limit.
Candidate scans use batches of 32 and fall back after 128 shows or a batch without
matches. Complete reads restart from the original public cursor in the same snapshot.
All 203 V4 tests pass, including six new cases. A premature-completion mutation
fails all four pagination/order cases; restoring continuation passes.

## Decisions and interfaces

`RelistenApi/Services/Catalog/CatalogCollectionReader.cs` owns candidate paging,
filters and complete cursor tuples. Add bounded song-show discovery there, not
inside GraphQL resolvers. Reuse its existing show ordering and metadata filters.
`CatalogSongMatchReader.cs` owns recording membership; batch scoped membership
through the existing SQL function. Keep both inside the caller's existing
`CatalogReadSession` repeatable-read transaction, including continuation batches.

Collect one extra matching show for `hasNextPage`. The public cursor describes
the last returned matching show; the scan cursor is private to this read. Exact
counts and complete REST reads retain the existing complete-membership path.
Any scan fallback must preserve the original cursor and return the same page.
Stored counts can choose a cheaper strategy but cannot establish membership or
terminate a scan: counts may be stale. No new cache, index, table or public option.

## Work and validation

First capture evidence under `docs/api/graphql/performance-fixes/show-first/`
against running application `0cea17090`, schema 20. Preserve the existing local
catalog. Include the exact dashboard query, common pages, rare one/two-show songs,
old-only histories, next/exhausted pages and non-search REST paths. Record first
and warm timings and canonical JSON hashes; benchmark serially without tests or
other intentional database work running concurrently.

Implement and run focused integration tests using `./test-local.sh --filter
FullyQualifiedName~RelistenApiTests.V4`. Tests should compare pages against the
existing complete membership, exercise skipped candidates and lookahead across
batch boundaries, show that stale counts cannot change results, and retain
owner/filter intersections and snapshot consistency. Use actual reader entrypoints
and disposable synthetic data. Run the full `./test-local.sh` and
`dotnet build RelistenApi.sln`, then `dotnet csharpier check .` and
`git diff --check` before every commit. Use the code-simplifier and sequential
review/validation passes; reject speculative defenses or a new planning framework.

Rerun identical benchmark inputs. Require identical payload/cost hashes and
explain any sparse-history or REST timing regression before accepting the change.
Document residual costs, observed variation and the actual strategy chosen.

## Recovery and outcome

There is no schema or data migration. Application rollback restores the original
page strategy; existing complete reads and the scoped matcher remain compatible.
Do not deploy or write production data. Leave the updated local API available.
Implemented in `77d0f376f` and locally qualified. The alternating old/new benchmark
reduces the exact dashboard from 324 to 230 ms warm median and Drums pages from
186 to 49 ms. Every one of 14 GraphQL and 16 REST cases retains its payload and
operation-cost hash. Sparse cases stay on the original membership path, with one
strategy-lookup command and small overhead in some samples. Both REST comparisons
retain all payloads; noisy short-read differences change direction across runs.
Four command-parity checks confirm unchanged SQL texts and command counts for
recent histories and venue reads. A separate 20-query corpus retains all earlier
payload/cost hashes; Drums25 playback is 93 ms warm.

All 1,008 tests pass (642 API, 366 normalization), with 37 existing skips. The
solution builds, focused mutation/restore passes, and two independent reviews find
no actionable correctness or readability issues. The simplicity pass keeps scan
state together and adds no framework. Evidence and reproduction scripts are under
`docs/api/graphql/performance-fixes/show-first/`. No production deployment occurred.
