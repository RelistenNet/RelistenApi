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
- [Grateful Dead shows](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows).

Rankings select ordered, limited score rows before catalog hydration. Artist
ranking groups apply limits independently per artist. Ordinary show lists load
only their requested artist-local score rows in one SQL query across artists.
Both scores and catalog metadata use the same primary repeatable-read snapshot.
There are no Redis map reads or play aggregations in v4 GETs. After catalog reads
finish, one freshness query checks the required populations and queues at most
one batch for stale or missing data. There is no automatic refresh schedule.

Global rankings retain global momentum. Ordinary shows and artist groups retain
artist-local raw momentum; momentum discovery publishes organic/fallback
momentum. Missing ordinary metrics remain null. An unpopulated ranking is empty.
A failed background refresh leaves its last successful rows available. SQL or
queue-submission failures use the existing 503/no-store response.

## Real-data comparison

The local backfill publishes the existing real aggregate snapshot through
`PopularityStore`, preserving its generation times. It does not calculate from
empty local telemetry or change Redis. The final schema covers every current
catalog target with zero/null rows when inactive: 4,387 artist rows, 431,878 show
rows across both contexts, and 33,231 year rows across the contexts present in
the snapshot. There are 101,270 scored show rows, unchanged from the sparse
model. Repopulating the final schema took 20.5 seconds; that is publication of
existing scores, not a complete calculation job.

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
| Complete artist catalog (historical; endpoint removed) | 208 | 152 | 64.4 | 60.3 | 1 → 0 |

Four simultaneous popular-five requests allocated 1.19 MiB total, compared with
330.7 MiB before. Ordinary show lists add one batched SQL query in place of
per-artist Redis reads: July 7 uses 12 data SQL commands; five global popular
shows use 12. The top-five score selection itself used
`show_popularity_hot_30d_idx` and measured 0.234 ms in a local EXPLAIN.
The existing metadata hydration accounts for the remaining queries.

## Demand refresh and schema review

The final schema is [migration SQL](../../../../../RelistenApi/Migrations/Sql/017_popularity.sql):
three resource tables, each with `generated_at`. Counters are zero and momentum
is null on inactive rows. Rankings and ordinary score hydration exclude those
rows, preserving the public null/empty behavior. The separate freshness table,
sentinel scope key, scope publication gate, and departure deletion are removed.
One sorted UPSERT stamps the whole population and uses per-row timestamps to
reject older results. It needs no explicit multi-statement transaction.

The same one-hour age policy is checked once per request, after catalog snapshots
close. Required scopes are deduplicated and queued as one batch. Unknown artists
and populations without catalog targets do not queue work. A fresh inactive
population does not repeatedly enqueue. Job retries always calculate again so
Postgres-success/Redis-failure can recover; no pending-job lease is introduced.

A temporary-table comparison using real catalog membership measured the global
show layout at 215,939 rows versus 38,628 scored rows. Storage was 51 MB versus
14.6 MB. Updating all timestamps took 1.35 seconds versus 0.38 seconds, while
indexed active top-five reads remained 0.12 ms. These local measurements omit
foreign-key and calculation cost. The extra background writes buy simpler row
semantics and publication.

## Verification and remaining work

The focused suite passes 183 v4, popularity, and saved-reference checks. It
covers one batch from a composed snapshot, fresh inactive populations, zero UUIDs,
queue failure and retry, raw/organic ordering, ETags, and dual-publication repair.
The v4 host registers queue submission only, not Redis maps or calculators.
The normal Development API was restarted against the real snapshot for the
live response/ETag comparison. No workers or production writes were started.

The schema foundation remains migration 17. V4's unshipped public-ID migration
is 18. Local preview changes were applied directly; new deployments apply the
final 17 then 18 normally. A production backfill is still needed before v4
rollout. Full refresh is an explicit repair action; unused artists have no
scheduled work.

JSON copies, repeated snapshot metadata queries, and source substring search
remain separate work from this popularity change.

Ignored evidence is in `local-dev/v4-review/.output/performance-review/`, including
`zero-row-layout-experiment.txt`, `swr-live-response-parity.json`, prior baseline
and SQL-read timings, and preserved input provenance. Earlier timing tables
above measured SQL retrieval before demand-refresh wiring. Final local Release
samples measured five popular shows at 13 ms / 0.25 MiB allocated with 13 data
SQL commands, including freshness. The 424-artist anniversary freshness lookup
took 9 ms in the traced sample. Queue submission was stubbed in this allocation
harness; the real Development API also passed all eight response/ETag checks.
The final harness registered the production date handler; elapsed times are
illustrative local samples rather than a production latency estimate.
