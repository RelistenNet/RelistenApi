# Native v4 contract and origin performance review

> November 1 update: the combined artist-catalog endpoint is removed. Earlier
> full-artist snapshot sizes, timings and test counts below are historical
> evidence, not the current route inventory. Use [the mobile handoff](mobile-handoff.md)
> and [endpoint manifest](../endpoints.json) for current behavior.

October 31, 2026. Native v4 is ready for the local [mobile pilot](mobile-handoff.md).
All 51 manifest operations are present in runtime OpenAPI. All 49 GET samples
returned 200; 47 cacheable GETs returned bodyless 304 on revalidation. Random show
and public history intentionally use no-store. POST behavior remains covered by
the focused disposable-database HTTP tests. A real resolver POST also preserved
six input references in order, returning five available targets and one unavailable
target with no-store and no ETag. No review telemetry was inserted.

This review does not activate v4 or change production. Existing v2/v3 services,
Redis popularity and all eight operator paths remain intact. Cloudflare owns
downstream response caching. The results below measure work reaching the origin.

## Contract evidence and fixes

The audit compared the running manifest and OpenAPI operations, then checked real
GET payloads recursively against their response schemas: property names, types,
ID patterns, enum values and nullability. The initial mismatches were popularity
and grouped-ranking schemas using camelCase while the wire uses snake_case.
Their DTO declarations now match the unchanged responses.

Swagger now documents `If-None-Match`, ETag response headers and bodyless 304s for
ordinary GETs. Errors declare `application/problem+json`. Random, live history and
both POSTs are excluded from cache validation. Normalization remains under one
track key; resources/lists stay direct, without generic metadata or detail URLs.
Typed identity, approval gating, partial/raw fallback and parent references retain
their existing contracts.

After the fixes, all 49 real GET samples had zero schema mismatches. All 48
deterministic payloads and ETags were byte-identical to the pre-audit host; the
random response differed as expected. The audit covered normalized and imported
detail, artist navigation/snapshots, discovery, search, rankings and reviews.
The [manifest](../endpoints.json) and [sync contracts](../contracts.md) remain the
endpoint ownership reference. No blanket absence/deletion rule was added.

The existing ranking test now compares its actual nested response properties to
their schemas. One new HTTP/schema test checks actual conditional status, ETag
headers and error media type, including no-store exceptions. Both regressions
failed without the fixes. The final focused catalog/resolver/popularity suite
passed **202 checks**, up from 201; no additional fixture or test host was added.
Existing tests exercise normalization-only corrections, empty/missing/retired
resources, SQL and queue failure/recovery, snapshots, resolver validation and
telemetry writes against disposable data.

## Allocation and query work

`CatalogJsonResult` now serializes directly into a UTF-8 memory buffer, hashes that
buffer and sends the same bytes. It avoids the intermediate full UTF-16 JSON
string, retains no new cache, and releases the buffer after body completion or a
304. The existing correction/compression test also round-trips accented text and
a supplementary Unicode character. Matched before/prototype samples reduced
allocation by 28.6% for Grateful Dead's catalog, 27.9% for Widespread Panic's catalog
and 30.4% for Drums' file list. These are allocation deltas, not live-heap limits.

Single-artist reads now reuse the resolver's selected-artist count SQL. The bulk
artist list still aggregates once across the catalog. For Grateful Dead, one
local `EXPLAIN (ANALYZE, BUFFERS)` measured 13.444 → 1.834 ms and 50,308 → 8,972
shared buffer hits. This changes no stored counts, schema, locking or responses.

The simplicity pass removed the duplicated source SELECT and placed its shared
definition in `CatalogRecordingReader`, used by recording, search and resolver
reads. It added no query builder, policy mode, pooling layer or runtime switch.
No source-string tests or query-count assertions were added. Both independent
code reviews found no actionable correctness or readability issue in the fixes.

## Final workload

Measured implementation: `3ed4713c2`; the later source-SELECT deduplication preserves
the same query semantics. Real restored local `relisten_db`, existing real score
snapshot, .NET 10.0.0 runtime / SDK 10.0.100, Release ARM64, loopback Kestrel.
The measurement host uses production v4 controllers/readers/serialization and
compression, with a no-op job dispatcher. Freshness SQL still runs; no worker,
import, normalization, count refresh or popularity recalculation runs.

Shared paths and each endpoint were warmed before seven complete GETs, seven
conditional GETs, one gzip GET and three waves of four simultaneous complete
GETs: **162 measured requests, zero failures**. SQL completion logging was enabled.
Process allocation deltas include the in-process streaming HTTP client and
diagnostic overhead. They are not isolated server allocations or peak resident
memory. Report medians and observed ranges, not production percentiles.

| Response | GET median (range), ms | 304 median, ms | Commands | Allocated MiB / GET | Body / gzip, decimal MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Grateful Dead catalog, 2,081 shows | 123.5 (110.3–157.4) | 110.2 | 47 | 42.2 | 6.98 / 1.12 |
| Widespread Panic catalog, 2,562 shows | 134.1 (124.1–137.0) | 124.7 | 44 | 47.1 | 7.50 / 1.22 |
| Large full show | 20.8 (19.8–23.1) | 19.7 | 18 | 5.3 | 0.834 / 0.152 |
| Scarlet Begonias detail with shows | 52.4 (51.0–57.0) | 50.7 | 20 | 5.6 | 0.815 / 0.138 |
| Drums file matches | 372.2 (357.8–422.6) | 360.2 | 10 | 96.8 | 15.49 / 3.85 |
| Red Rocks global detail | 18.7 (17.0–20.5) | 17.2 | 17 | 2.5 | 0.423 / 0.056 |

Commands count logged completions, including read-only SET and any freshness
lookup, excluding BEGIN/ROLLBACK protocol messages. A recording child batch has
three SELECTs in one command completion. Counts stay fixed across these warm
samples and four-way concurrency; they do not grow per returned show or track.

The slowest complete four-request waves were 348 ms for Grateful Dead's catalog,
236 ms for Widespread Panic's catalog, 47 ms for the full show, 76 ms for Scarlet,
623 ms for Drums and 34 ms for Red Rocks. These are wave completion times, not
per-request p95 values. The Drums waves allocated roughly 389 MiB in total per
four requests; this does not establish their peak live heap.

## Limits that matter to mobile and release

A 304 saves response bytes but still runs read/materialize/serialize work.
Use years and selected-year details for navigation; the full-artist endpoint is
removed. Keep recording detail and unrelated collections lazy. Cloudflare handles
downstream caching; no application response cache was added.

The largest song-track response completed under the local workload. This does not
establish production capacity, ingress behavior or phone parse/apply memory.
Measure those before broad activation and agree capacity/latency budgets with
Alec. If mobile measurements require pagination, define that endpoint's cursor
and replacement behavior explicitly; do not silently truncate collections.

Raw samples, full SQL/plans, the measurement program and environment record are
in ignored `local-dev/v4-review/.output/final-audit/`. `performance/final.json`
contains the measured samples and `performance/summary.json` their aggregates.
`contracts-before.json` and `contracts.json` record payload hashes, ETags and
schema results. The benchmark is an investigation artifact, not another shipping
test framework. Run `./local-dev/v4-review/run.sh` for the standard review host;
use the concrete URLs in the mobile handoff for normal API inspection.
