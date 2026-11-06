# Exact web dashboard profile

The exact [dashboard query](query.graphql) improves from **799 to 345 ms warm
median**, about 57%, on the existing local catalog. The shared statistics and
track readers now match only the selected shows instead of matching the full
song histories again. REST and GraphQL still share readers and projections.

The largest remaining cost is selecting the initial song-history pages. This
change adds no indexes or cache. Search-specific work remains deferred.

## Measurements

The query combines artist navigation, Scarlet and Drums history,
venue history and a full recording. Against application `a956703b9`, five serial
requests take 1,159 / 758 / 718 / 840 / 840 ms. The four warm samples have a median
of 799 ms. [before.json](before.json) records payload hashes, sizes and completed
SQL command timings from the local development API log.

With application `0cea17090` and migration 20, the repeat takes
680 / 452 / 324 / 356 / 335 ms: warm median 345 ms, range 324–452 ms.
[after.json](after.json) also retains the first pass after restarting the process
and applying the local migration: 2,434 / 570 / 443 / 350 / 327 ms, warm median
396 ms. Restart warmup remains visible; these are local observations, not
production percentiles or a concurrency test. Measurements ran serially without
concurrent test or SQL profiling work.

All fifteen dashboard responses have the same canonical JSON hash, including
array order and cursors. Each settled request still completes 73 SQL commands,
including session setup and teardown. The improvement comes from doing less work
inside the shared reads, rather than changing their count or response shape.

Warm SQL observations before versus the repeat:

- Timing statistics: **159–180 → 10–12 ms**.
- Nested track membership: **247–388 → 20–26 ms**.
- Initial song-history membership: **179–266 → 187–311 ms**.
- Artist song browsing: **51–59 → 52–55 ms**.

These commands can overlap; their durations must not be summed as HTTP wall time.

Show membership, timing statistics and nested track membership repeatedly match
the songs' full recording histories in the original profile. Those dependent
reads account for roughly 600–700 ms of the warm requests. The timing/track
readers already know the selected shows but originally restricted them after the
SQL function had done full-history work.

## Approach and proof

- The show-scoped overload keeps the existing matcher's complete artist merge map,
  sibling validation and relational-integrity joins. There is one active
  implementation; existing one/two-argument calls forward with NULL show scope.
- Two explicit candidate access paths preserve performance. The scoped path starts from
  show UUIDs, then existing source/show and track/source indexes. NULL retains all
  matches; an empty array means no selected shows.
- Shared statistics and collection readers pass known show scopes. Root song
  history still requires the unscoped population. Projections, ranking, limits
  and snapshot boundaries remain unchanged.

`CatalogSongMatchReader` passes the requested shows to the function. REST's
unscoped statistics call passes NULL through the same method.
`CatalogCollectionReader` unions known show scopes per requested song. If any
request needs an unscoped population, the union stays unscoped. Each parent's
existing membership predicates still enforce its own song/show intersection,
TOP/ALL ranking and cursor. Projections and read-session ownership are unchanged.

Two temporary-function experiments returned exactly the original filtered rows.
A nullable filter inside the original join was much slower. Explicit candidate
paths took 49/33 ms in the first scoped Drums/Scarlet observations. Warm unscoped
old/new observations were 137/129 ms for Drums and 29/28 ms for Scarlet, with exact
row parity. These single SQL observations establish a direction, not HTTP targets.

## What remains

The [nested matcher plan](remaining-history-plan.json) explains the initial
history cost. Drums produces 16,198 matching files from 16,213 matching parts.
The function uses indexes to visit their tracks, sibling parts, sources, sets
and shows. It records 326,652 buffer hits, zero physical reads and zero temporary
I/O in this observation. Parent buffer counts include their children.

This is repeated validation and grouping across the full recording population,
rather than a table scan waiting for a missing index. The current function
materializes that population before selecting ten shows. The complete artist map
itself has only 402 rows. Artist song browsing separately builds and sorts the
mixed canonical/imported population before returning eight songs.

Further reductions are possible, but would require changing how the initial
history page is discovered or retaining derived membership. Neither is included
here: the measured simple fix removes the repeated full-history work from
descendant reads while retaining one matching implementation and current
snapshot/validity rules.

## REST and correctness

[rest.json](rest.json) records four requests each for ten non-search REST paths
before and after the restart/migration. Every response matches exactly. Scarlet
song detail has warm medians of 96/99 ms; its TOP variant has 152/149 ms.
Artist songs has 73/67 ms. Unchanged short reads vary: anniversary history has
12/16 ms. These samples show similar matching-path timings. The sequential process
measurements do not establish a speedup for unmodified paths or qualify concurrent
production load.

The focused run passes 209 tests. Migration tests compare actual pre-upgrade rows
with legacy/default calls, NULL/empty/selected show scopes and whole-file sibling
validity. A deliberate mutation that narrowed the artist map to the requested song
made the sibling test fail; restoring the full map passed. Independent review and
validation found no actionable correctness, regression, readability or complexity
issues. The simplicity pass retained the legacy signature because REST, recounts
and administrative callers use it.

The full `./test-local.sh` run passes **636 API tests and 366 normalization tests**,
with 37 existing API skips. The solution build passes.

All 20 queries from the existing [catalog corpus](corpus.json) retain their response
and operation-cost hashes. Drums playback with 25 shows has a 305 ms warm median,
versus 548 ms in the earlier qualification. Scarlet playback has 126 versus 156 ms.
The first corpus has slower Scarlet row/count samples at 89/140 ms. Seven-request
control runs immediately afterward, without code/config/data changes, settle at
52/54 ms warm medians, ranges 48–54/48–58 ms. The artifact retains both results;
these observations do not establish the cause of variation on the shared machine.

## Operation and reproduction

The change needs one additive function migration; it changes no catalog rows,
tables or indexes. Existing callers keep their signature for application rollback.
Production deployment is outside this local task. Search-specific work stays deferred.

To try the exact request on the running development API, repeat this command:

```sh
jq -Rs '{query: .}' docs/api/graphql/performance-fixes/dashboard/query.graphql |
  curl -sS http://127.0.0.1:3823/api/v4/graphql \
    -H 'Content-Type: application/json' --data-binary @- \
    -o /tmp/relisten-dashboard-response.json -w '%{time_total}\n'
```

Inspect the response for GraphQL errors as well as elapsed time. Development
Npgsql logs supply the completed-command timings used above. The query's default
history size remains ten, and no response or operation cache is introduced.
