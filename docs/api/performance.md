# Origin performance reference

These are October 31, 2026 local measurements, not production or phone budgets.
They used a restored catalog, captured real score maps, .NET 10 Release ARM64 and
loopback Kestrel. The host used production readers/serialization/compression and
a no-op job dispatcher. Freshness SQL still ran; no recalculation ran.
The measurement program and raw artifacts remain local, ignored under
`local-dev/v4-review/.output/final-audit/`; they are not a second shipping harness.

## Work that affects serving cost

UTF-8 serialization hashes and sends the same buffer, avoiding a full UTF-16 JSON
copy. The prototype reduced allocation by 30.4% for the Drums file list.
Single-artist count SQL was restricted to its selected artist; one Grateful Dead
plan changed from 13.444 to 1.834 ms and 50,308 to 8,972 shared buffer hits.
Stored song counts replaced a 5.7-second list calculation; five later warm list
requests had a 19 ms median with the same payload/ETag.

There is no origin response cache. A 304 still reads/materializes/serializes but
saves transfer and mobile application. Child hydration is batched rather than
queried per returned file/show. Stored rankings select limited results in SQL.
Cloudflare owns downstream caching. Do not fetch unrelated artist collections
or song file lists eagerly.

## Representative local results

After warming each endpoint, seven complete and seven conditional GETs, one gzip
GET and three waves of four simultaneous GETs were measured per workload.
The retained results describe routes that still exist:

| Response | GET median, ms | 304 median, ms | Logged commands | Allocated MiB / GET | JSON / gzip, decimal MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Large full show | 20.8 | 19.7 | 18 | 5.3 | 0.834 / 0.152 |
| Scarlet Begonias with shows | 52.4 | 50.7 | 20 | 5.6 | 0.815 / 0.138 |
| Drums file matches | 372.2 | 360.2 | 10 | 96.8 | 15.49 / 3.85 |
| Red Rocks global detail | 18.7 | 17.2 | 17 | 2.5 | 0.423 / 0.056 |

Command counts include read-only SET and freshness lookup, exclude transaction
protocol messages, and count a three-SELECT recording batch as one completion.
They did not grow per returned file/show. Allocation deltas include the in-process
HTTP client and diagnostics; they are not isolated server allocations or peak
resident memory. Timings are medians from this workload, not production percentiles.
The slowest four-request Drums wave completed in 623 ms and allocated about
389 MiB total; that does not establish peak live heap or phone parse/apply memory.

The former full-artist catalog benchmark is omitted because that endpoint was
removed. Reproduce current behavior through selected-year/recording requests.
The large Drums response uses gzip initially; measure actual phone memory and
production latency before considering an endpoint-specific pagination contract.
Do not add silent truncation or generic sync metadata.

Before broad traffic, observe migrations/backfill WAL/storage, actual query plans,
primary read latency, summary load and release-device parse/apply/playback.
Migration 17 already adds ranking indexes; these local results do not establish
any need for extra indexes, larger pools or server tuning. See [deployment](deployment.md).
