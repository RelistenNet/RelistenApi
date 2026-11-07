# GraphQL performance checks

GraphQL uses the catalog readers and V4 projections shared with REST. Selection
adds bounded membership queries and batching; it does not introduce another
normalization pipeline or a graph-specific catalog cache. See the
[capability inventory](capabilities.md) for the reader behind each field family.

## Read strategy and costs

- Collection SQL applies filters, stable keysets, and limits before entity
  hydration. Each parent has its own page; later pages do not use offsets.
- Shared catalog read transactions disable PostgreSQL JIT locally. The batched
  pagination query's estimates can trigger expensive compilation even for small
  pages. Other database transactions retain their configured JIT behavior.
- Large song histories with small pages scan candidate shows first and match
  recordings within those candidates to fill the page. Small populations use the
  complete matching path; sparse scans fall back to it in the same snapshot.
  Large pages and selected `totalCount` also use the complete matching path.
- Scoped recording matching uses the same normalization rules and whole-artist
  merge maps as REST. Changing a canonical mapping can invalidate matching without
  changing the underlying source file. This work cannot be replaced with a plain
  indexed foreign-key lookup against the imported catalog.
- Complete recordings include all sets, tracks, parts, and interpretation links.
  Header-only source fields skip that work, including `resolve` targets. Deep
  playback requests still pay for the selected recording's full interpretation.
- A page computes its cursor scope once. Edge cursors are encoded when selected;
  nodes-only pages encode the final cursor for continuation.
- Grouped rankings load only selected series. REST requests both by default.
  Header-only graph selections load existing owner identities without show scores.

Counts, song timings, and selected metadata can dominate otherwise small pages.
Request only the fields the screen needs. Domain matching and complete recording
interpretation have costs that pagination alone cannot remove. Global search
continues to use existing substring semantics; search-specific optimization is
deferred. Existing catalog indexes support the measured artist/song/venue paths;
this does not establish capacity for arbitrary global filters or fan-outs.

Every page and its immediate hydration/count/statistics share a short primary
read-only repeatable-read session. Independent relationships may observe newer
sessions. See [read consistency and execution limits](README.md) for the public
contract, cancellation, budgets, and error behavior.

## Reproduce against a real catalog

Start the API against an existing representative local catalog, then run:

```sh
python3 docs/api/graphql/benchmark.py --runs 7 --output /tmp/graphql-benchmark.json
```

The standard-library-only command exercises the
[web dashboard](operations/dashboard.graphql), source headers, a 500-show page,
ranking headers, and REST controls. It is read-only and fails on HTTP or GraphQL
errors. Defaults use real Grateful Dead catalog IDs; `--variables path.json`
overrides dashboard variables and the corresponding controls. `--url` selects
another host. Small synthetic fixtures cannot qualify catalog performance.

The first response is separate from subsequent warm samples. These serial local
measurements are diagnostic, not throughput or production percentile claims.
Run before and after against the same catalog, with other builds/tests stopped.
Alternate the hosts when comparing changes; compare payload hashes as well as
latencies. Ranking/activity changes can legitimately alter hashes over time.
Keep raw output outside the repository.

For allocation work, attach `dotnet-counters` to the API process and collect
`System.Runtime` while repeatedly issuing one request shape. Compare allocation
rate divided by completed requests per second. This includes host/runtime activity
and is an estimate; keep an unchanged request as a control. ASP.NET/Npgsql
telemetry and the `Relisten.GraphQL` / `Relisten.Catalog` instruments identify
HTTP, SQL, batching, read duration, and row-budget work.

## Local catalog observations

On 2026-11-06, .NET 10/Kestrel on macOS ARM64 and Docker PostgreSQL were checked
against the existing real local catalog. Compared with the pre-review branch:

- Source-header resolution fell from 4.7 to 2.3 ms warm median; ranking headers
  fell from 21.8 to 1.3 ms. These requests now avoid unselected recording/ranking
  hydration. Both selected ranking series still use the shared REST reader.
- Runtime-counter estimates fell from about 247 KB to 106 KB allocated per
  resolved source header, and 6.15 MB to 3.55 MB per 500-show nodes-only page.
  These are allocation-rate/request-rate estimates, including host activity.
  The unchanged direct-source control varied from 126 KB to 142 KB per request.
- Alternating before/after hosts measured the dashboard at 201/187 ms and the
  500-show page at 40/39 ms. Treat these as comparable local latency; the measured
  cursor benefit is allocation reduction. Response hashes, including cursors,
  matched for every compared request.

Repeated interleaved REST controls also preserved payloads. Artist-song and
song-history warm medians were within 3%; source and grouped-ranking controls
were faster on the updated host. Absolute latency varied substantially between
workload mixes, so these observations do not establish small speedups or capacity.

Production verification on 2026-11-06 exposed an environment difference: the local
ARM64 database had JIT disabled/unavailable, while production had LLVM JIT enabled.
A read-only replica comparison of the same 500-show membership query returned 501
rows in 3,257 ms with JIT (3,220 ms compiling), 23 ms without, and 2,943 ms with JIT
again. The public dashboard's warm median before the transaction-local fix was
16.5 seconds. This is why JIT is disabled inside shared catalog read sessions,
rather than relying on local latency or changing database-wide settings.

## Contract checks

```sh
./test-local.sh --filter 'FullyQualifiedName~RelistenApiTests.V4'
dotnet build RelistenApi.sln
curl --fail http://127.0.0.1:3823/api/v4/graphql/schema.graphql > /tmp/graphql-schema.graphql
```

The HTTP suite protects REST projection parity, cursor continuation and ties,
owner/filter intersections, selected-field budgets, full recordings, ordered
bulk results with missing slots, and transport limits. Focused shared-reader tests
cover concurrent-writer snapshots, sparse histories, and normalization semantics.
The read-boundary fixture checks batching growth, a 210-track recording,
continuation beyond REST's search window, and one request-wide refresh handoff.
Timing loops, runtime counter collection, and benchmark report generation belong
in manual qualification, outside the test suite.

The route requires no catalog backfill. The scoped-matching SQL function is
installed by the normal migration path and used by shared readers. REST remains
available for rollout/rollback. Production concurrency, cold process behavior,
and broad global workloads need qualification against their actual traffic.
