# Review stored popularity

V4 reads popularity from Postgres. Existing calculations publish to both
Postgres and Redis, so legacy APIs retain their cache contracts. The
[foundation](../../../../popularity/README.md) owns the schema, publication,
refresh jobs, and rollout. This checkpoint changes no public resource shape or
scoring meaning.

## Try the same navigation

- [Five popular shows](http://127.0.0.1:43823/api/v4/popular/shows?limit=5).
- [July 7 across artists](http://127.0.0.1:43823/api/v4/shows/on-date?month=7&day=7).
- [Red Rocks with shows](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f).
- [Grateful Dead popular/trending](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/popular-trending?limit=5).
- [Grateful Dead momentum](http://127.0.0.1:43823/api/v4/shows/momentum?artist_ids=artist_3qmp7zjbg1rpebhtyfy43096vj&limit=5).
- [Grateful Dead catalog](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/catalog).

Rankings select ordered, limited score rows before catalog hydration. Artist
ranking groups apply limits independently per artist. Ordinary show lists load
only their requested artist-local score rows in one SQL query across artists.
Both scores and catalog metadata use the same primary repeatable-read snapshot.
There are no Redis reads, refresh enqueues, or play aggregations in v4 GETs.

Global rankings retain global momentum. Ordinary shows and artist groups retain
artist-local raw momentum; momentum discovery publishes organic/fallback
momentum. Missing ordinary metrics remain null. An unpopulated ranking is empty.
A failed background refresh leaves its last successful population available;
SQL read failures use the existing 503/no-store response.

## Real-data comparison

The local backfill published the existing real aggregate snapshot through
`PopularityStore`, preserving its generation timestamps. It did not recalculate
from empty local telemetry or change Redis. Postgres contains 2,817 artist rows,
38,628 global show rows, 62,642 current artist-local show rows, and 9,341 global
year rows. The artist-local input contained 62,650 entries; publication joins to
the current restored catalog and accepts only current owned targets. Empty
cached populations are also recorded. Backfill took 18.3 seconds; that measures
publication of existing scores, not a full calculation job.

Eight complete response/ETag comparisons matched the prior Redis-backed API:
popular shows, seven-day trending shows, popular artists, trending years,
global July 7 shows, artist catalog, artist rankings, and momentum. The scratch
parity host registered the production `DateTimeHandler`; without it, dates had
an unrelated local-time offset. Scores and ordering already matched.

The same isolated Release .NET 10 ARM64 HTTP harness measured warm reads before
and after. Values below are medians of three samples. Allocation is managed
bytes allocated per request, not retained memory. SQL excludes transaction
setup. The harness includes SQL/Redis profiling and a small in-process client;
these are local comparisons, not production latency or capacity estimates.

| Request | Before ms | After ms | Before MiB | After MiB | Redis before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Five popular shows | 123 | 4.8 | 82.4 | 0.23 | 1 → 0 |
| July 7 across artists | 266 | 28 | 122.6 | 7.72 | 424 → 0 |
| Red Rocks detail | 96 | 17 | 36.6 | 3.50 | 12 → 0 |
| Full 1978-07-07 show | 50 | 12 | 8.2 | 1.87 | 1 → 0 |
| Artist momentum, five shows | 15 | 6.7 | 6.6 | 0.25 | 1 → 0 |
| Complete artist catalog | 208 | 152 | 64.4 | 60.3 | 1 → 0 |

Four simultaneous popular-five requests allocated 1.19 MiB total, compared with
330.7 MiB before. Ordinary show lists add one batched SQL query in place of
per-artist Redis reads: July 7 uses 12 data SQL commands; five global popular
shows use 12. The top-five score selection itself used
`show_popularity_hot_30d_idx` and measured 0.234 ms in a local EXPLAIN.
The existing metadata hydration accounts for the remaining queries.

## Verification and remaining work

The focused suite passes 180 v4, popularity, and saved-reference checks. The v4
HTTP host registers no Redis or calculation services. It exercises global/local/organic ordering,
window thresholds, empty groups, current ownership, cross-route enrichment,
score-sensitive ETags, and near-equal trending ratios after cache precision is
applied. Foundation tests exercise scoped replacement, older-publication
rejection, real dual publication, Redis failure after a changed play input, and
successful retry. Two independent source reviews found no runtime defect.

The schema foundation is migration 17. V4's unshipped public-ID migration moves
to 18; its normalization SQL resource remains named `017_public_ids.sql`. The
private preview already had that public-ID migration applied, so its version
ledger was adjusted while applying the new popularity schema. Its records are
ordered 17 then 18: SimpleMigrations reads the last inserted record, rather than
the maximum version. All eight live response/ETag comparisons passed after the
normal Development API restarted. New deployments apply 17 then 18 normally.

Before production v4 rollout, backfill with the real calculation job and measure
its complete duration. The hourly job runs scoped calculations sequentially;
read-only production samples took 885 ms for Grateful Dead's show calculation
and 1.3 ms for an artist with one active show. These samples do not establish
the full hourly budget. No production schema, metrics, or schedules changed in
this checkpoint.

The earlier performance review also identified JSON UTF-16 copies, repeated
snapshot metadata queries, and expensive source substring search. Those remain
separate work; this change addresses popularity storage and retrieval.

Ignored evidence is in `local-dev/v4-review/.output/performance-review/`:
`postgres-measurements.json`, `postgres-response-parity.json`,
`postgres-backfill.json`, `postgres-popular-plan.json`, and the two read-only
production refresh query plans. Earlier baseline and component measurements
remain alongside them.
