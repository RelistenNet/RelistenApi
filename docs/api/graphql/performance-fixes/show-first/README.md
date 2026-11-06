# Show-first song-history pages

The exact dashboard improves from **324 to 230 ms warm median**, about 29%, in an
alternating old/new comparison on the existing local catalog. Drums' ten-show page
improves from **186 to 49 ms**. All 14 GraphQL cases and 16 non-search REST paths
retain their response hashes, including array order, cursors and reported costs.

Application `77d0f376f` adds bounded discovery to the shared collection reader.
The comparison application is `3735df99a`, whose application code is the same as
`0cea17090`; intervening changes only recorded documentation and baselines. Both
processes use schema 20 and the same approximately 216k-show/5.1m-track local
catalog. This change adds no schema, index, cache, public option or projection.

## Shared reader behavior

`CatalogCollectionReader.ReadPages` selects ordered candidate shows using its
existing metadata filters and full cursor tuple. For large recording populations,
it sends batches of 32 show UUIDs to `CatalogSongMatchReader.ReadShowMembershipBatch`.
That method uses the existing scoped matcher, with one union of show scopes per
song across compatible parents. The complete artist merge map, sibling validity
and relational-integrity rules stay in the function. Each parent retains its own
membership intersection.

The reader continues until it finds the requested matching shows plus one for
`hasNextPage`, or exhausts the candidates. Its private cursor advances over
unmatched shows. The public cursor still describes the last returned matching show;
lookahead and unused candidates remain available on the next request. All scan
commands use the caller's existing repeatable-read session and cancellation token.
Errors fail the read normally; no partial page or retry is introduced.

Complete REST reads, exact counts and pages requesting at least 128 rows retain the
original complete-membership query. For smaller pages, stored counts choose that
query when a requested song has at most 4,096 matching files or lacks a count.
Counts never establish membership or truncate results. A show scan falls back
after 128 candidates or a batch without matches, restarting from the original
public cursor in the same snapshot and discarding partial scan results.

## Why the strategy has a cutoff

Forcing show-first discovery on every song is slower. The initial 1,024-file cutoff
made Scarlet's first page take 93 ms versus 52 ms for the original reader; its next
page took 77 versus 50 ms. Its roughly 3,400-file population is cheaper to match
directly than to discover through several candidate batches. Drums' roughly
16,000-file population benefits from checking recent shows first.

[strategy-comparison.json](strategy-comparison.json) retains that alternating
comparison. The final 4,096-file cutoff avoids Scarlet's scan while preserving
Drums' improvement. It selects an access path, not a new query limit. Stale counts
can choose a slower path but cannot change the page.

## Measurements

[before.json](before.json) is a fresh five-request baseline before implementation.
[after.json](after.json) alternates five baseline/current requests per case and
retains first samples separately from the four warm samples. Requests run serially,
without concurrent tests or intentional SQL profiling. These local observations
do not establish production percentiles or sustained concurrency performance.

Selected warm medians from the alternating comparison, baseline/current:

- Exact dashboard: **324/230 ms**, warm ranges 309–408/206–241 ms.
- Drums first/next page: **186/49 ms** and **167/43 ms**.
- Scarlet first/next page: **47/53 ms** and **46/53 ms**.
- Darling Corey, one matching show: **13/18 ms**; exhausted continuation **7/7 ms**.
- Golden Road, two matching shows: **14/14 ms**.
- Phish's Big Pimpin', one matching show: **13/13 ms**.
- Viola Lee Blues, 36 matching shows: **15/16 ms**.
- Older Cryptical Envelopment first/next page: **22/23 ms** and **21/21 ms**.
- Older Keep Your Day Job: **28/26 ms**. Scarlet with exact count: **49/48 ms**.

The strategy lookup adds one command to bounded song-show groups, including those
that choose complete matching. The Scarlet and Darling Corey samples show a small
overhead remains; the implementation does not claim every page gets faster.

The sixteen REST comparisons include complete artist browse/discovery and complete
song histories. Scarlet detail has warm medians **82/79 ms**, its TOP variant
**134/131 ms**, and sparse song details stay near their baselines. Unchanged short
reads also vary: recently performed and updated histories are each about
**19–20/24 ms**, while artist shows are **108/115 ms**. Complete REST paths execute
neither the strategy lookup nor candidate scanning. Retain the raw samples when
assessing these differences; this is not an isolated throughput test.

The seven-request [REST control](rest-control.json) retains all payloads and shows
the earlier recent-history differences do not persist: performed history has warm
medians 28/28 ms, updated history 26/23 ms. Other short paths vary in that run,
including venue detail at 28/39 ms versus 15/14 ms in the first comparison.
[Command parity checks](rest-sql-parity.json) confirm identical completed SQL texts
and counts for performed/updated histories, artist venues and venue detail. These
checks show unchanged database work; they do not turn noisy host timings into a
throughput guarantee. Sampled complete song reads remain near their baselines.

[profile.json](profile.json) records a separate exact-dashboard trace without the
cost-report header: 309/209/219/197/212 ms, warm median 210 ms. Its response hash
matches the original exact dashboard. Requests complete 76 SQL commands versus 73
before, including session commands. Drums discovery uses one 32-show scope. Its
scoped membership command takes 14–19 ms warm; Scarlet still uses the complete path
at 34–38 ms. Artist song browsing remains roughly 52–58 ms. Commands can overlap;
their durations must not be summed as HTTP wall time.

The captured Drums scope yields 241 valid matching files, versus 16,198 across the
complete history. The profile artifact retains its 32 show UUIDs and count method.
The [20-query catalog corpus](catalog-corpus.json) also retains its earlier response
and cost hashes. Drums rows/playback for 25 shows measure 44/93 ms warm; Scarlet
playback measures 104 ms. These are separate current-version observations, not an
alternating comparison for all twenty cases.

## Verification and operation

The focused V4 run passes 203 tests, including six new cases covering ordering,
nonmatching batches, lookahead, continuation, exhausted pages, per-parent filters,
exact counts, scan-budget fallback, deliberately stale counters and snapshot
consistency after a concurrent membership change. Prematurely completing a full
candidate batch made all four pagination/order cases fail; restoring continuation
passed. Independent review and validation found no actionable correctness,
readability or complexity issues. The simplicity pass kept candidate cursor and
accumulated-match ownership together in the scan helper.

The full `./test-local.sh` passes **642 API and 366 normalization tests**, with 37
existing API skips. The solution build and repository formatting checks pass.
The [execution plan](../../../../plans/active/2026-11-06-show-first-song-pagination.md)
records the decisions and completed qualification.
There is no production deployment or data mutation. Application rollback restores
the prior strategy; schema 20 stays compatible.

To reproduce, run the original application at `3735df99a` on port 3824 and the
current application on port 3823 against the same existing local catalog. Use the
[local setup](../../../../../local-dev/README.md), setting
`ASPNETCORE_URLS=http://127.0.0.1:3824` for the baseline, then run:

```sh
python3 docs/api/graphql/performance-fixes/show-first/paired.py \
  --baseline-commit 3735df99a --current-commit ACTUAL_RUNNING_COMMIT \
  --output /tmp/relisten-show-first-paired.json
```

The script uses frozen queries, variables and cursors from `before.json`. The
standalone `benchmark.py` records a fresh corpus with `--application-commit`,
`--output` and optionally `--compare` pointing at an earlier artifact.
