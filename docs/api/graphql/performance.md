# GraphQL local qualification

The measurements below retain the initial small-fixture correctness/execution
evidence. The subsequent [actual-catalog review](review/README.md) found performance
problems that the fixture missed. The [fixes and real-catalog qualification](performance-fixes/README.md)
record their resolution for the measured web corpus; search-specific work remains
deferred. Use that report for current performance and test results.

The defaults allow the representative web queries with headroom. The largest
measured field cost is 18,010 against a 200,000 limit (11.1×); the largest type
cost is 20,053 against 500,000 (24.9×). An excessive 500 × 500 × 500 traversal
is rejected before catalog SQL. A grouped ranking that requests more than the
100,000-row materialization budget fails before entering its reader.

## Measurement setup

Measured on 2026-11-06 on the local macOS ARM64 development machine, .NET 10,
Hot Chocolate 16.7.3, real Kestrel HTTP, and the repository's Docker PostgreSQL.
The fixture creates 101 shows across two artists, 104 sources/sets, 1,240 tracks,
and 1,240 normalization parts. The first artist owns 100 shows; one show has five
sources, and one source has a complete 210-track recording.

Each GraphQL workload runs once, then five more times serially against an already
running host. `FirstMs` is its first sample, not cold startup or a cold database.
Warm median/max describe five samples, not production latency percentiles. JSON
bytes are uncompressed and include the requested cost extension. Commands count
calls through `CatalogReadSession.Command`; they exclude transaction setup and
popularity freshness checks. Hangfire is a recording test double, so queue/Redis
network costs are absent. No real catalogs, imports, or production data are used.

Exact documents, fixture construction, and assertions live in
[TestV4GraphQLWorkloads.cs](../../../RelistenApiTests/V4/TestV4GraphQLWorkloads.cs).
The raw [measurement artifact](performance-results.json) includes all samples'
summary values and REST containment baselines.

## Observed workloads

- **artist-100**: 17 domain SQL commands maximum, 18,920 bytes; warm median 21.9 ms, max 35.6 ms. Field/type cost 1,022/204.
- **song-history-50**: 30 domain SQL commands maximum, 114,304 bytes; warm median 546.4 ms, max 583.1 ms. Field/type cost 10,721/2,253.
- **playlist-250**: 20 domain SQL commands maximum, 119,133 bytes; warm median 18.9 ms, max 20.4 ms. Field/type cost 18,010/6,501.
- **show-five-recordings**: 14 domain SQL commands maximum, 59,457 bytes; warm median 15.2 ms, max 34.1 ms. Field/type cost 12,521/20,053.
- **long-recording**: 7 domain SQL commands maximum, 52,577 bytes; warm median 7.3 ms, max 7.7 ms. Field/type cost 1,260/2,006.
- **recording**: 7 domain SQL commands maximum, 2,595 bytes; warm median 4.1 ms, max 4.3 ms. Field/type cost 1,260/2,006.

All six requests also completed together with a four-connection PostgreSQL pool
in 602.5 ms. This verifies progress under local pool contention;
it does not establish sustainable production throughput. The command-bound
assertion is fewer than 100 per request, which catches a per-item regression for
the 250-track and 50-parent cases. Loader dispatch can change the exact command
count between runs without changing the contract.

The long-recording assertion verifies all 210 tracks arrive. Static estimates
(4 sets, 50 tracks per set, 4 parts) affect cost estimation only. Runtime row
accounting protects the full materialization without slicing any recording.

## REST containment baselines

REST responses contain the selected GraphQL information plus their normal extra
fields and complete relationships. These are one warmed measurement each,
using sequential HTTP requests; they are not equal-payload performance comparisons.

- **artist-100**: 2 request(s), 11 domain SQL commands, 54,515 bytes, 103.9 ms.
- **show-five-recordings**: 1 request(s), 10 domain SQL commands, 207,343 bytes, 23.8 ms.
- **long-recording**: 1 request(s), 5 domain SQL commands, 170,794 bytes, 10.5 ms.
- **recording**: 1 request(s), 5 domain SQL commands, 8,910 bytes, 8.7 ms.

GraphQL reduces the selected payload and lets the caller traverse relationships
in one operation. REST can do less query/execution work when its existing complete
resource already fits the page. These measurements do not establish a GraphQL
latency advantage. Song history and mixed track hydration are acceptance workloads;
this artifact does not benchmark equivalent REST request sequences for those two.

## Correctness and execution evidence

- The full `./test-local.sh` run passed 627 API-project tests and 366 normalization
  tests; 37 API cases were skipped by their existing fixtures. The final focused
  V4 implementation run passed 191 tests. The subsequent admin-editor addition
  passed all 192 V4 tests, including cookie login and embedded-asset checks.
- `dotnet build RelistenApi.sln` passed. Existing package-advisory and unrelated
  nullable warnings remain; the new GraphQL code produces no nullable warnings.
- HTTP fixtures compare shared projections, identity/order, partial filters,
  owner intersections, canonical/imported fallback, source completion, reference
  validation, ordinary/global/organic score context, and discovery membership.
- A tie-heavy source search traverses all 100 matches in 17-item keyset pages;
  nullable ratings and equal durations do not lose boundary rows. REST's top 20
  retain the same descending date order.
- A writer commits between membership selection and hydration inside the graph's
  read-unit wrapper. Hydration sees the original snapshot; a later HTTP request
  sees the update. Existing one-connection session tests prove read-only
  enforcement, canceled-query disposal, and fresh-read recovery.
- Parallel branches share the real HTTP-owned refresh collector and enqueue one
  union. Known cyclic normalization fails its field while an independent artist
  field completes. There are no table-renaming fault tests or savepoint recovery.
- All nine client documents execute against the fixture through HTTP and report
  operation costs. The exported SDL matches that executable schema.
  Static excessive-cost rejection performs zero catalog SQL. Page limits,
  cross-scope cursors, combined ranking size, and URL/body limits are exercised.

Reproduce from the repository root:

```sh
./test-local.sh --filter 'FullyQualifiedName~RelistenApiTests.V4'
dotnet build RelistenApi.sln
```

The workload test writes `/tmp/relisten-graphql-performance.json`; the schema test
writes `/tmp/relisten-graphql-schema.graphql`. Copy these into this directory only
when intentionally refreshing the published evidence.

## Operational limits of this qualification

No index migration was needed for these local fixtures. They do not reproduce
production artist sizes, song-match query plans, concurrent traffic, network
latency, or a cold process. Production query-plan and load observations remain
rollout work. This branch does not deploy the route or migrate clients. REST is
available throughout; GraphQL requires no schema/data migration or backfill.
