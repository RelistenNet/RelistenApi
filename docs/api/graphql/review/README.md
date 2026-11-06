# GraphQL adversarial performance review

The graph shares REST readers and projections, but its new shared pagination SQL
does too much work. The reported three-second song-history request is reproduced.
The branch also regresses existing REST search. Fix those readers before exposing
the endpoint to normal web traffic. The present database can support substantially
better queries; these measurements do not establish a fundamental PostgreSQL limit.

This review compares `41813085b` with pre-GraphQL commit `5087f5a39`. It adds
queries, measurements and recommendations only. No application code, index,
migration, ingestion or deployment changed. The investigation used a first
reviewer followed by an independent validator; measured evidence resolved the
initially provisional tour-search concern.

## Measured behavior

Serial local HTTP against the existing catalog, approximately 216k shows and
5.1m source tracks, with both API versions using the same PostgreSQL database.
Each case has one first sample and three warm samples. These are warm medians,
not production percentiles. All GraphQL cases completed without errors.
Full samples, costs, variables and response sizes are in [results.json](results.json).

- Scarlet metadata: **9 ms**.
- Scarlet history, 25 rows: **575 ms**; with exact count: **1,059 ms**.
- Scarlet next 25 rows: **563 ms**. A cursor avoids OFFSET but does not avoid
  the current broad candidate scan.
- Scarlet playback: **817 ms / 2,999 ms / 6,405 ms** for 1 / 10 / 25 shows.
  The 25-show result contains only 25 playable files.
- Drums history, 25 rows: **773 ms**; playback: **11,950 ms**, also 25 files.
- Tweezer Reprise playback, 25 shows: **5,158 ms**.
- Oakland venue history, 25 rows: **951 ms**; date/SBD-filtered case: **156 ms**.
- One complete recording: **18 ms**. Hydrating 250 known tracks: **36 ms**.
- Artist navigation: **172 ms**. Artist shows, 25 rows: **616 ms**.

The Scarlet and Drums playback documents have identical field/type costs,
8,582 / 5,830, far below the configured 200k / 500k limits. Cost estimates bound
requested graph expansion; they do not measure candidate scans or song frequency.
Keep the agreed generous limits while fixing the readers. Lower limits would
reject useful operations without fixing this work.

REST comparisons alternate baseline/current request order. Tour-only search for
`summer` rises from **12.8 to 756 ms**, roughly **59×**, with identical JSON.
Artist-scoped song/venue search for `Oakland` rises from **27.2 to 282 ms**,
roughly **10×**. Its payload has one additional fallback venue, explained below.
Recently updated shows rise from **23.9 to 46.8 ms**; artist venues from
**14.8 to 27.8 ms**. Those smaller differences need further isolation before
attributing every millisecond to a particular query.

Other sampled REST routes retain identical payloads without a comparable large
slowdown: basic artist shows, recently performed, top shows, anniversary, artist
songs, venue detail, song detail, and song detail with top tracks. Basic artist
shows still use their original scoped selector. These samples are not a blanket
claim about REST throughput or every artist.

## Confirmed findings

### High: REST tour search repeatedly scans all shows

Location: [CatalogCollectionReader.cs:314](../../../../RelistenApi/Services/Catalog/CatalogCollectionReader.cs#L314).

The shared selector replaced the previous grouped join with a correlated count
for every matching tour. The local schema has no `shows(tour_id)` index.
The isolated query scans all 215,939 shows **153 times**: 744 ms and 516,613
buffer hits. The previous grouped form scans shows once: **12.6 ms**, 3,424 hits.
The HTTP comparison establishes the corresponding existing-client regression.

Restore a set-based count over the matching tours in the shared reader. An index
on `shows(tour_id)` is a concrete candidate for tour membership/count queries,
but the grouped experiment already fixes this case using existing indexes.
Do not make adding an index a prerequisite for removing the repeated scans.

### High: nested track pages repeat whole-song matching and ranking

Location: [CatalogCollectionReader.cs:258](../../../../RelistenApi/Services/Catalog/CatalogCollectionReader.cs#L258)
and [016_song_counts.sql:20](../../../../Relisten.Normalization/Sql/016_song_counts.sql#L20).

Each parent request's lateral query materializes song matches and ranks matching
sources across the song's history before applying `show_uuid`. One Scarlet parent
takes 456 ms and 303k buffer hits. Ten parents take **2,627 ms / 3.03m hits**.
The ten-parent plan executes `song_recording_matches` **ten times**, each returning
3,355 rows, and ranks 3,278 sources ten times. This is repeated work inside one
batched command; command-count assertions cannot detect it.

Batch matching once per distinct song population in the read unit. Restrict
selected shows before source ranking. Where practical, let the shared matching
reader/function accept a selected-show scope so it can reduce candidate files
before joins and grouping. Preserve the complete artist merge map and validation
of other resolved parts: an invalid sibling part invalidates a file's interpretation.
Narrowing that map to only the requested song would change correctness.

Keep membership, ranking, immediate hydration and interpretation in the same
existing short snapshot. No cross-request cache or global transaction is needed
to remove these repeated executions.

### High: ordinary pages scan unrelated artists and shows

Location: [CatalogCollectionReader.cs:43](../../../../RelistenApi/Services/Catalog/CatalogCollectionReader.cs#L43)
and [CatalogCollectionReader.cs:351](../../../../RelistenApi/Services/Catalog/CatalogCollectionReader.cs#L351).

JSON request columns drive a large set of OR/CASE branches for owner, filters and
sort. In the measured artist/song show pages, PostgreSQL does not simplify those
branches into a cheap owner restriction. The plans visit **4,387 artists** and
approximately **216k shows** to return ten rows. Artist membership takes 678 ms
and 2.35m buffer hits; song first/next pages take 566 / 527 ms and about 2.41m hits.
The buffers are already resident. Warming the database cannot remove that work.

An owner-scoped SELECT with the same date/identity ordering takes **1.75 ms** and
1,403 hits using existing indexes, considering only that artist's 2,081 shows.
This is a membership-query experiment, not an equal-payload endpoint comparison.

Start with explicit owner-specific SQL branches in the shared collection reader.
Keep values parameterized and preserve independent per-parent limits. Apply the
owner and selected population before metadata joins. Then use native ordered
columns and cursor predicates where plans can use them. A generic query-planning
framework and indexes for every filter combination would add unnecessary scope.

### Medium: omitting timing fields does not omit their SQL

Location: [CatalogGraphOperation.cs:209](../../../../RelistenApi/GraphQL/CatalogGraphOperation.cs#L209)
and [CatalogGraphFields.cs:175](../../../../RelistenApi/GraphQL/CatalogGraphFields.cs#L175).

Every `SongShows` page calls `ReadShowStatisticsBatch`, including a table that
selects only dates and venue names. Wrapper construction and timing materialization
currently share one flag. Request timing statistics only when selected, while
still constructing song/show wrappers. A selection flag suffices; this does not
need a general-purpose planner. Similar HTTP times with/without timing selection
are consistent with this source behavior, not evidence that timings cost nothing.

### Medium: existing qualification misses these regressions

Location: [TestV4GraphQLWorkloads.cs:173](../../../../RelistenApiTests/V4/TestV4GraphQLWorkloads.cs#L173)
and [TestV4GraphQLWorkloads.cs:221](../../../../RelistenApiTests/V4/TestV4GraphQLWorkloads.cs#L221).

The 101-show fixture verifies batching, complete recordings, limits and correctness.
Its command ceiling cannot detect hundreds of thousands of rows scanned within
one command. REST containment checks omit several selectors rewritten by this
branch. Keep those useful tests; add actual-catalog qualification for first/next
pages, counts, high-frequency songs and all changed REST search/discovery/browse
readers. Use plans and work amplification alongside HTTP latency. Do not turn
these three-sample timings into brittle unit-test deadlines.

## Product capabilities exposed by the screenshots

These are additional server capabilities, separate from defects in the supported
pagination contract. The earlier decision deferred extra filters; this review
identifies what the demonstrated web UI would need before implementing them.
Runnable documents for the currently supported operations are in [queries/](queries/).

- [Songs index](https://discord.com/channels/395033814008594436/466023446590259220/1568325021204746452):
  a paged list sorted by show count, search, and “With recordings.” Ordinary song
  browsing currently sorts by name; show-count ranking happens implicitly only
  during search. There is no recording-backed-only filter. Filtering/sorting an
  already fetched page cannot implement either control correctly.
- [Drums history](https://discord.com/channels/395033814008594436/466023446590259220/1568325235843801192):
  global length-descending order, first/last, typical/longest duration, annual
  histogram, date/venue search, year/SBD controls and playable rows. Current graph
  supports date ordering and per-show timing statistics, but no duration ordering
  or whole-population timing/year summary. Search is currently a literal field
  search, not a combined date/venue/city interpretation.
- [Venue history](https://discord.com/channels/395033814008594436/466023446590259220/1568325353892614257):
  first/last and a year histogram alongside paged shows. Venue details and filtered
  shows are available; aggregate summaries need a reader/API contract.
- [Dense playlists](https://discord.com/channels/395033814008594436/466023446590259220/1568308477166747740)
  and [back-to-back song comparisons](https://discord.com/channels/395033814008594436/466023446590259220/1568316982015823923):
  fetch a useful table, compare sources, multi-select recordings and batch hydrate
  a queue. Current operations demonstrate those primitives. Fetching every playback
  bundle before first paint is optional, but the nested reader must still perform
  well when the user requests a page of playable selections.
- [Popularity over time](https://discord.com/channels/395033814008594436/466023446590259220/1568325463971991697)
  and [listening heatmaps](https://discord.com/channels/395033814008594436/466023446590259220/1568325682369667263):
  current rolling show scores are only one view of existing play reports.

For summary/length ordering, first define the population: distinct shows, files,
or performances; all files or selected source; single-song durations versus
multi-song files; median across files versus median across per-show estimates.
Apply the same filters to summary and membership. Compute statistics for ordering
before LIMIT, with stable ties. A table's summaries and rows should use one read
unit or the same explicitly published aggregate generation. Keep REST's existing
timing meaning unless a separate contract change is agreed.

## Database limits versus query and index work

**Existing data can support better browsing and analytics.** Owner-scoped pages,
set-based matching, bounded source ranking, first/last and annual show histograms
are query/API work. Common per-song/show summaries can be precomputed if live
calculation remains expensive. Existing song counts already allow refresh lag;
popularity is published asynchronously. A new summary needs an explicit population,
mapping generation and freshness contract. Per-show timings currently calculate
inside the live read snapshot. Arbitrary exact filtered medians still require
work over their chosen population unless an appropriate summary exists.

**Indexes are secondary to fixing the demonstrated scans.** The local schema
already has artist/date and artist/year show indexes, source/show covering indexes,
source-track/source indexes, normalized song/artist indexes and track-song/song
indexes. The direct and grouped experiments use those existing structures.
`shows(tour_id)` is missing and worth testing for tour-scoped reads. Matching a
new composite index to the final native cursor order may help large scoped pages.
Substring search may justify targeted trigram indexes after identifying the costly
terms/columns; no `pg_trgm` extension or such indexes are present locally. None of
those indexes has been added or benchmarked here. Avoid broad index migrations
before fixing structural predicates, particularly given ingestion write costs.

**Historical popularity is largely available data with missing readers.** The active
table is singular `source_track_plays`. It records a received track play report,
server timestamp, app type and optional anonymous client UUID. Existing hourly/day
Timescale aggregates retain track/source/show/artist dimensions, play counts and
summed whole-file duration. Production read-only metadata inspection found daily
raw chunks reaching back to 2018 and no configured play retention policy. Chunk
bounds do not prove complete historical coverage. Historical track/song heatmaps
therefore need rollups, indexes and API work rather than new play telemetry.

Both continuous aggregates are materialized-only and deliberately lag recent
activity. A timely view needs a published watermark or a bounded raw-data tail.
Rolling artist/show/year popularity tables overwrite their current windows; they
do not store historical score snapshots. Reconstructing an old score may require
its historical formula and inputs. Joining today's song mapping attributes old
reports according to today's interpretation, not a recorded song-at-playtime.

**Some missing information cannot be supplied by an index.**

- A multi-song file has whole-file duration and ordered song parts, without
  segment time offsets. We cannot measure one composition's length or seek to its
  exact start from those facts alone.
- Matching files, distinct shows, imported setlist occurrences and actual performance
  occurrences differ. There is no authoritative normalized performance identity
  joining all tapes and repeated occurrences. Imported setlists can supply their
  own occurrence view where mapped and covered, with that qualification.
- Canonical songs are artist-owned. Cross-artist composition analytics need explicit
  shared composition relationships; equal names alone do not establish identity.
- Reported file starts and sums of file durations do not establish listened seconds,
  completion, seeking or segment engagement. Associating one multi-song file report
  with every matched song does not show which song was heard. Anonymous UUIDs can
  approximate clients; aggregate tables do not retain them for distinct-user counts.

## Shared REST behavior and review corrections

Entity hydration, normalization, source ranking and `V4CatalogProjection` are shared.
The new custom SQL is primarily collection membership; sharing it spread the
inefficient selector to some REST calls. Fix it at the domain reader rather than
forking GraphQL-only SQL or introducing a separate graph cache.

The Oakland search response difference is **page filling**, not projection drift:
the first 19 venue objects and their order are identical. Current search appends
`imported_venue_332vv5h01zav5yff45xvvjs3qe`, a zero-show fallback venue. Baseline
limits imported candidates before suppressing normalized replacements; current
membership filters before the final limit. Preserve the more complete page unless
the old early truncation was deliberately promised. Cover that policy explicitly
when tightening regression tests.

**Rejected finding:** exact count does not execute once per returned row. The active
count `InitPlan` executes once. It evaluates the candidate population again,
which is expensive here, but the plan does not support an N+1-count diagnosis.
There are no remaining partially validated findings presented as confirmed bugs.

## Recommended implementation order

Priority update, 2026-11-06: Alec expects to replace search and considers it the
least important endpoint. The [implementation plan](../../../plans/active/2026-11-06-graphql-performance-fixes.md)
therefore prioritizes existing playback and pagination. Search findings remain
valid, but do not lead or block that work.

1. Batch song matching and restrict shows before source ranking in the shared reader.
2. Fix owner scoping and make timings conditional on selection. Retain existing
   read-unit snapshots, full interpretation validation, cancellation and failure
   behavior. Preserve REST fallback policy, ordering and complete relationships.
3. Re-run this actual-catalog corpus and inspect plans. Verify ten parent requests
   no longer compute the same whole-song population ten times. Add only indexes
   justified by the remaining plans. Recheck ingestion implications of any index.
4. Agree the song-index and duration/summary semantics, then implement shared
   aggregate readers and explicit ordering/filter inputs. These extend the earlier
   scope; they should not be smuggled into the performance fixes.
5. Qualify production-sized plans and concurrent pool/CPU behavior before rollout.
   The existing small-pool correctness test does not establish production capacity.
6. Handle any remaining search regression separately, without search-specific
   indexes or a redesign in the playback/pagination implementation.

## Reproduce

Keep the current development API on loopback 3823. In a separate checkout of
`5087f5a39`, start the baseline on 3824 using the same local database/Redis and
Development configuration, with no workers/import schedules. Then run:

```sh
git worktree add --detach /tmp/relisten-graphql-rest-baseline 5087f5a39
DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db \
REDIS_URL=redis://127.0.0.1:16379 \
ASPNETCORE_URLS=http://127.0.0.1:3824 \
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project /tmp/relisten-graphql-rest-baseline/RelistenApi/RelistenApi.csproj
```

From another terminal:

```sh
python3 docs/api/graphql/review/benchmark.py --current-commit 41813085b
```

The script performs GETs and GraphQL POST queries only. It requires existing catalog
IDs listed in its variables; another seed/catalog needs replacement IDs. It writes
`results.json`; use `--output /tmp/relisten-graphql-review.json` to preserve this
record. The documents can also be pasted into the admin explorer with the variables
from the corresponding result entry. See [query coverage](workloads.md).
`--current-commit` records the operator-supplied running binary's source; use its
actual commit when rerunning. `checkoutCommit` is separately observed from Git.
The saved run's `currentCommit` was verified when the API was started; the script
does not discover binary provenance from HTTP. Failed cases retain their samples
without successful warm medians or a payload-equality claim.

[SQL experiments](sql/) are read-only transactions with a 20-second statement
timeout. Run an individual file through the local `psql` connection described in
the repository guide. [sql-results.json](sql-results.json) records timings, buffer
counts and active plan nodes; unexecuted branches are omitted. Parent buffer counts
include children and must not be summed. [local-indexes.json](local-indexes.json)
captures the inspected index definitions. SQL plans are single observations,
separate from repeated HTTP samples. No cold-disk or production-load claim follows.
