# Discover bounded song histories from ordered shows

This living plan tracks show-first pagination on `codex/v4-catalog-graphql-plan`.

## Purpose

Return song-history pages without validating every recording of a frequently
performed song first. Keep REST and GraphQL readers, matching rules, projections,
cursor ordering and read snapshots shared. Search-specific work stays deferred.

## Progress

- [x] Prototype ordered candidate shows against the existing real local catalog.
- [x] Capture fresh dashboard, sparse-history and non-search REST baselines.
- [ ] Implement bounded candidate scans and scoped matching in shared readers.
- [ ] Verify continuation, lookahead, filters, counts, snapshots and sparse fallback.
- [ ] Benchmark the same requests, simplify, independently review, run checks and commit.

## Discoveries

The current `CatalogCollectionReader` materializes all song matches before it can
apply a show-page limit. The existing three-argument `norm.song_recording_matches`
accepts selected show UUIDs and retains full artist merge-map and sibling validity.
A read-only prototype found the original first 11 Drums shows among 32 candidates
in 14 ms warm, and Scarlet's first 11 among 128 candidates in 34 ms. These are SQL
observations, not HTTP results. Sparse and old-only histories can make a show scan
more expensive than the original song-first query; measure and bound that work.

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
Outcome pending implementation and qualification.
