# Web workload corpus

These documents exercise the current schema through HTTP, using the web screens
and playlist/comparison discussion linked in the [review](README.md). They cover
the catalog's navigation, matching and playback primitives. They also expose
where a screen needs server capabilities beyond those inherited from REST.

The measured variables are in [results.json](results.json), grouped by case name.
Paste a document and that case's `variables` object into `/relisten-admin/graphql/`.
Extra supplied variables are ignored by GraphQL. IDs refer to the existing local
catalog, rather than the small synthetic test fixture.

## Supported documents

- [artist-navigation](queries/artist-navigation.graphql): artist header/features,
  years, 25 mixed canonical/imported songs, and 25 canonical/imported venue entries.
  Verifies the browse overlay remains available while the web client requests
  selected fields. This list uses today's name ordering, not the screenshot's
  show-count ordering.
- [artist-shows](queries/artist-shows.graphql): 25 date-descending shows, date and
  venue details. Set `count: true` to measure the exact filtered count separately.
- [song-metadata](queries/song-metadata.graphql): header, aliases, stored matched
  show/file counts and owner artist, without song-history traversal.
- [song-history-rows](queries/song-history-rows.graphql): 25 shows with date,
  normalized/imported venue, tapes, rating, SBD availability and 30-day reported
  plays. Resume with `after`; `count` selects the exact count. The benchmark runs
  Scarlet first/next pages, count on/off, and high-frequency Drums.
- [song-history-timings](queries/song-history-timings.graphql): per-show single-song
  and multi-song duration summaries. These describe selected history rows, not a
  whole-song typical/longest summary.
- [song-history-playback](queries/song-history-playback.graphql): a playable history
  page, with TOP source selection, URLs, source quality facts and normalization
  parts. The same document runs at 1, 10 and 25 Scarlet shows, plus 25 Drums and
  Tweezer Reprise shows. It requests up to 20 matching files per show and returns
  each nested `pageInfo`; it does not assume a performance is always one file.
- [song-source-comparison](queries/song-source-comparison.graphql): one date's song
  history with up to 50 files from ALL matching sources. A source is a recording,
  and one recording can have multiple matching files; file count is not tape count.
- [venue-history](queries/venue-history.graphql): artist/venue relationship,
  canonical venue coordinates, count optional, and 25 date-descending shows.
- [venue-history-filtered](queries/venue-history-filtered.graphql): the same
  relationship's 1994–95 SBD-available table and exact filtered count. Filtering
  shows by SBD does not prune their independently selected recording lists.
- [recording-playback](queries/recording-playback.graphql): one complete source,
  all sets/tracks, URLs, interpretation and parent show/artist context. Complete
  recording arrays have no pagination or silent truncation.
- [playlist-hydration](queries/playlist-hydration.graphql): 250 known files with
  playback URLs, source/show/artist context and normalization. This models restoring
  or preparing a large queue; discovery of those IDs is a separate workload.
- [catalog-search](queries/catalog-search.graphql): artist-scoped song, venue and
  show categories, 20 per category. This exercises alias/fallback membership with
  category selection rather than paying for every REST category.

`benchmark.py` executes 20 GraphQL cases from these 12 documents. It also compares
12 REST cases: basic artist shows, performed/updated discovery, top shows,
anniversary, artist songs/venues, venue detail, song detail, song detail with top
tracks, tour-only search, and song/venue search. Both versions use the same local
DB. The script stores HTTP status/errors, bytes, payload hashes, costs and samples.

## Additional queries needed by the demonstrated UI

No runnable query is supplied for unsupported fields. These are proposed
capabilities to specify:

1. **Songs index:** a server-side recording-backed filter and explicit show-count
   ordering, with stable name/identity ties. Decide how the existing imported
   fallback entries participate before defining their count comparator.
2. **Song detail:** a filtered population summary with distinct show count,
   first/last dates, annual show histogram and clearly defined duration measures.
   Add duration ordering over that same population before pagination. Decide
   whether the UI's “times played” means setlist occurrences or another fact.
3. **Venue detail:** filtered first/last and annual show histogram from the same
   membership definition as the table. A global venue can also span several artists;
   the artist/venue view must retain its owner scope.
4. **Exploration and queue building:** compose venue/year/song intersections with
   comparison and TOP/ALL file selection. Sort and select an entire population
   correctly before bulk hydration. The existing graph supplies some primitives;
   its original supported filter set does not yet express every screenshot control.
5. **Listening trends:** track/song/day heatmaps and artist/year/venue rollups over
   received play reports. Specify time zone, published watermark, current versus
   historical normalization attribution, and how multi-song files count. Label
   file starts and duration sums honestly; they are not measured engagement.

These additions should reuse transport-independent membership and aggregate
readers. REST can adopt those capabilities when useful without having its current
fields silently acquire new definitions.
