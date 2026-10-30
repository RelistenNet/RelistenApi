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

Ranking entries are the normal artist/year/show resource plus one-based `rank`
and `popularity`. The latter contains `momentum_score`, `trend_ratio`, and the
`48h`/`7d`/`30d` windows of plays, hours and hot score. No duplicate top-level
window counts are needed. Artist metadata retains `features`.

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
no synthetic play events were added. The score maps were captured at
2026-10-30 23:43 UTC and marked fresh locally for 24 hours. Catalog resources still come
from the restored local PostgreSQL database, so missing references are omitted.

Ignored `local-dev/v4-review/.output/ranking-snapshot-provenance.json` records the
source, capture time and map sizes; `ranking-cache-before-snapshot.json` preserves
previous local values. The five raw aggregate JSON files and temporary scoring
runner are local review artifacts. Production uses its normal map miss/stale
refresh behavior, not this review snapshot. The correct context and read-only
connection command are now recorded in [AGENTS.md](../../../../../AGENTS.md).

Mobile replaces only the exact cached query result and upserts supplied resource
fields. A ranking never deletes artist inventory or detail memberships. A
normalization correction can update returned venues and ETags while listening
scores remain the same. Redis/SQL failures return 503/no-store, not successful
empty lists. Grouped and momentum requests with no `artist_ids` intentionally
return empty arrays.
