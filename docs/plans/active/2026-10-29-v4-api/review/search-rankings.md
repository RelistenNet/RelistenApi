# Review search and listening rankings

All ten remaining mobile-facing catalog reads are available. The local API now
has 49 operations: 48 GETs and resolver POST. Only the ten operator/import and
live-activity operations remain outside this catalog checkpoint; auth is still
separate.

## Search into the existing navigation

- [Grateful Dead “Scarlet” search](http://127.0.0.1:43823/api/v4/search?q=Scarlet&artist_id=artist_3qmp7zjbg1rpebhtyfy43096vj)
  returns six categories. Song and venue hits use the same typed entries as
  artist browsing. Follow their IDs to existing resource-with-shows details.
  Source hits are summaries; follow the source ID to load sets and tracks.
- [Inherited venue alias](http://127.0.0.1:43823/api/v4/search?q=The%20Crystal%20Ballrom&artist_id=artist_3qmp7zjbg1rpebhtyfy43096vj)
  finds McMenamins Crystal Ballroom through an alias on a merged predecessor.
  The result links to the current artist/place relationship and shared venue.
- [Global venue search](http://127.0.0.1:43823/api/v4/search?q=Center&artists=false&shows=false&songs=false&sources=false&tours=false)
  ranks matching resources across artists. It does not choose the first artists
  by UUID or load each artist's full inventory.

Use the six category booleans to narrow work. Artist scope is optional;
empty/short queries return all six empty categories. Search keeps imported
fallback for raw/disabled artists and unmatched records. Valid approved evidence
can map a matching imported label to its canonical target. Names and inherited
aliases remain searchable after merges. The [search excerpt](responses/search-excerpt.json)
selects one hit per category; full captured bodies are in the ignored output folder.

## Compare the ranking routes

- [Popular artists](http://127.0.0.1:43823/api/v4/popular/artists?limit=5)
  and [trending artists](http://127.0.0.1:43823/api/v4/trending/artists?limit=5).
- [Popular years](http://127.0.0.1:43823/api/v4/popular/years?limit=5)
  and [trending years](http://127.0.0.1:43823/api/v4/trending/years?limit=5).
- [Popular shows](http://127.0.0.1:43823/api/v4/popular/shows?limit=5)
  and [48-hour trending shows](http://127.0.0.1:43823/api/v4/trending/shows?limit=5&window=48h).
- [One artist's popular and trending lists](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/shows/popular-trending?limit=5).
- [Grouped artist selection](http://127.0.0.1:43823/api/v4/shows/popular-trending?artist_ids=artist_3qmp7zjbg1rpebhtyfy43096vj&artist_ids=artist_6aaf982021nrst0m68ew1djerc&limit=5).
- [Momentum across those artists](http://127.0.0.1:43823/api/v4/shows/momentum?artist_ids=artist_3qmp7zjbg1rpebhtyfy43096vj&artist_ids=artist_6aaf982021nrst0m68ew1djerc&limit=5).

Every show response includes `popularity`; ranked shows add `rank`. Ordinary show
reads and artist ranking groups use raw artist-local metrics. Global popular and
trending lists use global metrics, and momentum publishes its organic/fallback
score. All use the same projection of `momentum_score`, `trend_ratio`, and the
`48h`/`7d`/`30d` windows of plays, hours and hot score. Missing entries are `null`.
Artist metadata retains `features`.

Compare a global ranking with direct show detail: different momentum scores are
expected because they describe different comparison contexts. Compare ordinary
artist, song and venue copies with direct detail: those use the same artist-local
scores. The ranking reader selects stored SQL rows before loading catalog
resources, and supplies those metrics to shared enrichment. Ordinary lists read
only the required score rows in one SQL batch. V4 uses no Redis score reads or
request-scoped score cache. The [stored-popularity checkpoint](stored-popularity.md)
records the real-data parity and performance comparison.

The show `window` query offers those three values in Swagger. The existing hot
score, play floors, and organic momentum rules are preserved. Artist/year
trending ranks by trend ratio; show trending keeps its existing selected-window
hot-score order. `/shows/top` remains the separate rating-based list.
The [contracts](../contracts.md#search-and-listening-rankings) specify limits,
empty selections, unknown references, and ordering.

## Real-data provenance and mobile apply

The local PostgreSQL backup contains no play telemetry. For this review, Alec
approved a separate aggregate snapshot from the production read-only replica.
Production was not changed. The existing query/scoring code produced 2,817 artist,
38,628 show and 9,341 year metrics, plus the Grateful Dead and Phish per-artist
maps used for momentum. Only aggregate metrics were loaded into local Redis DB 0;
no synthetic play events were added. The global maps were captured at
2026-10-30 23:43 UTC. Shared show enrichment now uses a larger artist-local
snapshot captured at 2026-10-31 00:02 UTC (October 30, 5:02 p.m. Pacific): 62,650
show entries across 2,814 artists, plus valid empty maps for 1,552 other local
catalog artists. Each artist's scores were calculated separately with the existing
score builder. These inputs are marked fresh locally for 24 hours. Catalog resources still come
from the restored local PostgreSQL database, so missing references are omitted.

Ignored `local-dev/v4-review/.output/ranking-snapshot-provenance.json` records the
source, capture time and map sizes; `ranking-cache-before-snapshot.json` preserves
previous local values. The raw aggregate JSON files, including the all-artist show export, and temporary
scoring runner are local review artifacts. The same real maps have now been
published into local Postgres through the production population writer. V4 reads
those stored rows. Production refresh jobs publish to both Postgres and Redis;
v4 requests read stored scores and queue one batch for stale or missing required
populations; they do not calculate scores. There is no automatic schedule. The correct context and read-only
connection command are now recorded in [AGENTS.md](../../../../../AGENTS.md).

Search and ordinary catalog queries can replace their exact cached query results
and upsert supplied fields under the endpoint contract. Popular/trending/momentum
results are transient online views: keep them out of the persisted show catalog
and offline membership. Open a selected resource's ordinary detail route to apply
catalog data. ETags still support online revalidation of rankings.

A normalization correction can change ranking metadata and ETags without changing
scores. SQL read failures return 503/no-store. Background refresh failures retain the
last successful stored values; an unpopulated ranking is empty. Grouped and momentum requests with no `artist_ids` intentionally return
empty arrays.
