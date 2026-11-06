# V4 GraphQL capability inventory

The graph is an alternative web read API. Existing REST payloads, complete
collections, ETags, and mobile sync behavior remain available. The
`GET /api/v4/graphql/schema.graphql` provides the current field/argument reference;
[operations](operations/) are client examples. The separate, initially empty
[persisted-operation registry](persisted-operations.md) holds deployed client queries.

## Reader ownership and public mappings

Both transports use `Services/Catalog` for membership, metadata, approval and
identity resolution, and `Models/Api/V4` for public field mappings. GraphQL owns
traversal, execution limits, cursor encoding, and loader scheduling. It contains
no normalization matching SQL or alternate public field calculations.

`CatalogEntityReader` supplies shallow entity and complete source batches to
GraphQL and REST reference hydration. `CatalogCollectionReader` selects pages
before hydration, including a separate lateral limit per parent. REST search,
canonical/imported song and venue browsing, and show discovery compose this same
selector with complete or existing bounded collection policies. Ordinary REST
show/source reads retain `CatalogRecordingReader`; its complete recording batches
are also used by the graph. Existing metadata readers supply UI counts,
approval-gated overlays, imported facts, and ordinary popularity in both paths.

`CatalogRankingReader`, `CatalogReferenceLookup`, `CatalogSongMatchReader`,
`CatalogLiveReader`, and `CatalogLiveSummaryReader` are shared directly. The graph
uses `V4CatalogProjection`, `CatalogBrowseProjection`, and `V4SongProjection`
for public representations; it does not call controllers or issue loopback HTTP.

For a concrete call path, REST `CatalogSearchReader` asks `CatalogCollectionReader`
for 20 members, then `CatalogEntityReader` hydrates them. GraphQL
`CatalogGraphOperation.ReadPageGroup` calls those same two readers with the
requested cursor and per-parent limit. Both then use the public projections.
The collection reader's keyset and per-parent lateral SQL is new: the old readers
returned complete lists or fixed top-N results. Some older REST list selection
queries remain separate, while sharing metadata hydration and mappings.

## Read capability mapping

- **CatalogBrowseController:** `artists`, `artist`, `artistBySlug`; artist
  `years`, `shows`, and `eras`; `year` and `yearByNumber`. Growing lists are
  connections. Ordinary scalar counts/features/popularity use the REST mappings.
- **CatalogReadController:** `show`, `source`, `sourceTrack`, and source `reviews`;
  direct `sourceSet` and `sourceReview` supplement traversal. Source sets/tracks
  remain complete ordered recording arrays. Artist/show/source/set parents are
  navigable without embedded REST detail hierarchies.
- **CatalogSongsController:** artist `songs` chooses canonical entries when
  normalization is approved and imported entries otherwise; `songs` and
  `importedSongs` expose their separate inventories. `song`,
  `songBySlug`, `importedSong`, and `importedSongBySlug` support direct lookup.
  Canonical `shows` carries shared single/multi-song timing statistics and paged
  playable `tracks`, with `TOP`/`ALL` source selection. Canonical song `tracks`
  reaches every matching file. Imported song `shows` retains setlist membership;
  `normalizedSong` follows the existing nullable approved mapping.
- **CatalogVenuesController:** `venue`, `venueBySlug`, `venues`; `artistVenue`,
  `artistVenueFor`, `artistVenueBySlug`, and `artistVenues`; `importedVenue`,
  `importedVenueBySlug`, and `importedVenues`. Artist `venues` chooses canonical
  entries when normalization is approved and imported entries otherwise. Global
  `venues` includes zero-show canonical venues and shares the REST list's membership
  and counts. Each collection has its own show membership;
  canonical merges follow survivors. Global `parent` is a canonical venue edge.
- **CatalogToursController:** `tour`, `tourBySlug`, `tours`, artist `tours`, and
  tour `shows`. Year/show/setlist metadata links retain their original identities.
- **CatalogShowDiscoveryController:** `showsToday`, `showsOnDate`,
  `recentlyPerformedShows`, `recentlyUpdatedShows`, `recentlyAddedShows`,
  `topShows`, `showByDate`, and `randomShow`. Artist scope is a typed filter or
  required lookup argument. Connections page beyond REST's bounded discovery view.
- **CatalogSearchController:** `search` has independently selected artist, show,
  song, source, tour, and venue connections. Literal substring matching,
  canonical/imported fallback, and result tie-breaks share REST's selector.
- **CatalogRankingController:** `popularArtists`, `trendingArtists`, `popularYears`,
  `trendingYears`, `popularShows`, `trendingShows`, `artistRankings`, and
  `momentumShows`. These are bounded top-N views. Rank `metrics` retains contextual
  scores; the ordinary `node` retains its original comparison population.
- **CatalogResolveController:** ordered `resolve(references:)` availability,
  reasons, redirects, retirement, and typed targets. `sourceTracksByIds` supplies
  input-ordered playlist hydration with duplicates/missing files preserved.
- **CatalogLiveController:** `liveHistory` and `liveSummary` expose existing public
  activity. Event writes stay on REST. Event selection/catalog hydration remains
  best effort. This endpoint does not expose User Service account data or writes.

## Consistency, counts, and errors

Each entity batch, page plus immediate hydration/statistics/count, ranking, and
reference lookup owns a primary read-only repeatable-read unit. A selected source
bundle includes its complete recording interpretation. Further relationships may
observe newer units; there is no recursive descendant query planner.

A part's `TrackPartSong` consumes materialized identity/name/kind. Its `catalog`
edge starts independent full-song traversal. Other already-materialized overlays
remain attached to their node. Stored UI counts retain domain meaning and can lag
membership; selected `totalCount` describes the page's filter snapshot. Imported
and canonical groups can overlap and their counts cannot safely be added.

A failed SQL/normalization/child read fails the unit. Library error propagation
handles independent fields. Known invalid/retired/unavailable states use existing
domain rules. There is no savepoint or partial-recording salvage policy.

## Verification

`TestV4GraphQLHttp` exercises these connection families through HTTP and real SQL,
compares REST IDs/membership/selected fields, executes documented operations, and
checks filters, owner intersections, mixed fallback, fragments/directives, budgets,
reference validation, and transport limits. Shared REST tests cover merges,
retirement, approval, canonical counts, and ETags. The cyclic-merge fixture now
checks GraphQL field errors while an independent artist read succeeds.

`TestV4SearchRankingHttp` checks ordinary/global/organic score contexts and
selected ranking series. `TestV4GraphQLReadBoundaries` checks bounded batching,
complete recordings, search continuation, and request-wide refresh collection.
`TestCatalogSongShowPaging` checks membership and entity hydration against a
concurrent writer in a caller-owned snapshot. `TestCatalogReadSession` checks
read-only enforcement and cancellation/disposal with a one-connection pool.
See [performance checks](performance.md) for real-catalog measurements and
remaining production capacity qualification.
