# Review C1 against the restored catalog

Run from the repository root:

```sh
./local-dev/v4-review/run.sh
```

Open <http://127.0.0.1:43823/api-docs-v4>. This is the normal Development API
with the real controller, reader, serializer and middleware registrations. It
binds to loopback and uses the restored `relisten_db`. Development starts no
import workers or schedules. Startup applies the checkout's catalog migrations.
The foundation's migration 15 creates and seeds derived relationship rows from
the real show assignments; it has already been applied to this local database.

In another terminal:

```sh
./local-dev/v4-review/capture.sh
```

This selects real records from the 2026-10-29 catalog snapshot, captures full show
and track responses with headers in `.output/`, and refreshes the checked-in
[request collection](../../docs/plans/active/2026-10-29-v4-api/review/requests.http).
It changes no catalog data. The representative UUIDs come from that snapshot;
a different snapshot may require choosing replacement records.

The request collection works in Rider or a compatible HTTP client. Swagger
provides Try it out too. Start with the matched and partial tracks, compare
their full shows, then inspect the venue alias example. Playback URLs and
titles are imported values; normalized parts describe the file without replacing it.

The [date and recent browsing review](../../docs/plans/active/2026-10-29-v4-api/review/discovery-browsing.md)
provides anniversary, exact-date, recent, top-rated, and random-show links.

The expanded C1 browse checkpoint exposes 49 routes (48 GETs and one POST). Follow the
[venue-to-playback walkthrough](../../docs/plans/active/2026-10-29-v4-api/review/venue-source-journey.md)
and [song-to-playback walkthrough](../../docs/plans/active/2026-10-29-v4-api/review/song-source-journey.md)
to inspect the resource links. Implemented families are:

- `GET /api/v4/artists` and `/api/v4/artists/{artistId}`: artist list/detail,
  counts, features and provider metadata.
- `GET /api/v4/artists/{artistId}/catalog`: complete artist-owned collections and
  shared venue/song entity patches, with one snapshot ETag. Recording hierarchies
  remain lazy. The capture compares approved/partial and raw artist inventories.
- `POST /api/v4/catalog/resolve`: bounded saved-reference lookup across typed and
  explicit legacy schemes; per-input results and shallow entity patches. No-store,
  with no ETag or collection replacement. The generated request includes its JSON.
- `GET /api/v4/artists/{artistId}/years`: year list; append `/{yearId}` or
  `/by-number/{year}` for year detail with show summaries.
- `GET /api/v4/artists/{artistId}/shows`: the complete artist show-summary list.
- `GET /api/v4/artists/{artistId}/venues`: compact canonical relationship and
  imported fallback entries. Canonical children have stable `artist_venue` IDs
  and `artist_show_count`; imported children have `show_count`.
- `GET /api/v4/artist-venues/{artistVenueId}`: relationship metadata with complete
  `shows`. `/artists/{artistId}/venues/{venueId}` is
  the equivalent pair lookup.
- `GET /api/v4/venues/{venueId}`: shared metadata, aliases,
  `global_show_count`, complete cross-artist `shows`.
- `GET /api/v4/artists/{artistId}/imported-venues`: compact full imported inventory,
  including unused rows. `/imported-venues/{importedVenueId}` returns metadata,
  count, complete `shows` directly by ID.
- `GET /api/v4/artists/{artistId}/songs`: compact canonical and imported fallback
  entries, selected by the server from approved evidence.
- `GET /api/v4/songs/{songId}`: canonical metadata, exact recording counts,
  complete matched `shows`. Its `/tracks` response
  has song metadata and complete file matches in `tracks`; no `shows`.
- `GET /api/v4/artists/{artistId}/imported-songs`: compact full imported inventory.
  `/imported-songs/{importedSongId}` returns that resource with complete
  setlist-supported `shows` directly by ID.
- `GET /api/v4/artists/{artistId}/tours`: compact tour inventory with actual
  catalog `show_count`; `/tours/{tourId}` returns metadata plus complete shows.
- `GET /api/v4/artists/{artistId}/eras`: ordered `{ order, name }` metadata with
  no era identity. Source sets also stay embedded; there is no direct set lookup.
- `GET /api/v4/sources/{sourceId}/reviews`: complete imported review resources,
  newest update first, with typed review/source IDs and original text/ratings.
- `GET /api/v4/shows/{showId}`: full show with sources, sets, tracks, imported
  venue observations, stored `artist_venue` links, aliases and approved normalization.
- `GET /api/v4/sources/{sourceId}`: that same complete source resource directly,
  including its own imported venue and active playable files.
- `GET /api/v4/source-tracks/{sourceTrackId}`: that same track resource directly,
  with exact typed artist/show/source/set parents.

Venue/song details use one snapshot and ETag for metadata and complete membership.
Artist references resolve through the separately loaded artist catalog. Bare
venue/song `/shows` routes are removed. Browse and
embedded resources stay compact. No `detail_url` is returned: use the ID kind to
select the fixed direct route. See the [contracts](../../docs/plans/active/2026-10-29-v4-api/contracts.md)
for membership replacement and count ownership.

Resources return directly. Every track has `normalization` with `state`,
`reason` and ordered `parts`. Raw, disabled and partial records stay playable.
Ordinary responses carry a weak HTTP ETag and require cache revalidation.
Send the returned ETag in `If-None-Match` to get 304 when unchanged. Errors use
ProblemDetails with `code`: 422 `invalid_id`, 404 `not_found`, or 503
`catalog_unavailable`; errors are no-store. The complete v4 manifest remains
future work after this review.

## Local approval flags and source provenance

The faithful snapshot had zero approved-read artists. Alec approved enabling
`norm_catalogue` locally for **all 12 artists with normalized data**. The target
set includes artists owning canonical songs, normalized show venues, or track
parts. Existing metadata, titles and interpretations were not edited.

Original feature rows are saved in `.output/normalization-flags-before.json`.
To restore the previous flags on this local snapshot:

```sh
PGPASSWORD=local_dev_password psql -X -v ON_ERROR_STOP=1 \
  -h 127.0.0.1 -p 15432 -U relisten -d relisten_db \
  -f local-dev/v4-review/.output/restore-flags.sql
```

These local files and large captures are ignored. Backup provenance and restore
verification are in `local-dev/backups/v4-c1-20261029/README.md` (local, ignored).
The archive has all catalog/normalization rows but omits playback analytics;
C1 does not depend on those event histories. Production flags were not changed.

The local setup accepts the inert `app` NOLOGIN role retained by restored
Timescale policies; it still rejects extra login roles or elevated `app` roles.
No provider imports or normalization jobs are needed for this review.

Synthetic regression data is confined to disposable test databases under
`RelistenApiTests/Fixtures/v4/`. Manual review uses the restored records.

## Artist and global venue counts

Show resources expose nullable `artist_venue` alongside `imported_venue`; the old
flat `show.venue` is removed. The relationship contains its native short ID,
`artist_id`, stored `artist_show_count`, timestamps, and nested `venue`. That
shared venue includes `global_show_count`, summing stored counts only for artists
with approved normalized reads. Its value stays the same across artist/year
filters. The artist count likewise covers all the artist's shows at that place.

Artist venue lists discriminate `artist_venue` and `imported_venue` targets.
Canonical children match show relationships and direct artist venue detail.
Imported targets include their full `show_count`, matching imported detail.
Embedded imported venues in show/source responses include the same full count;
a source-only venue observation can have a count of zero.
Missing, disabled, unmatched, or unmaterialized relationships keep imported
entries visible. During partial normalization, an imported entry's full show
collection can overlap canonical entries. Clients use `/artists/{id}/venues`
for normal browsing and follow each entry's `kind`; `/imported-venues` is the
separate full imported inventory.

The local full refresh was independently verified against real assignments:
5,397 pairs across 12 artists and 13,418 show memberships, with zero mismatches.
A second recount changed no IDs, counts, or timestamps. Neither API reads nor
captures run recounts. Stored counts can lag current show membership after a
failed write hook or out-of-band repair; use the explicit repair described in the
[foundation plan](../../docs/plans/active/2026-10-30-artist-venues/README.md).

Venue resources retain distinct `{ name, kind }` aliases, including merged names.
Kinds are `official`, `short`, and `spelling`; date ranges are absent. Show UI uses
the imported venue label and locality and links through `artist_venue.venue.id`.
Canonical metadata is fallback only when imported data is absent. The request
collection includes the Oakland/Henry J. Kaiser example.


## Song counts and playback

Canonical `matched_track_count` counts distinct active playable files containing
a resolved part for that song; `matched_show_count` counts their distinct shows.
`/songs/{id}/tracks` returns each file once as a complete track resource. Match
its normalization parts against the returned root song ID. The complete track is
shared with direct track and recording reads, preserving imported titles,
playback URLs, exact parents, and partial normalization.

Imported `setlist_show_count` counts distinct catalog shows supported by imported
setlist evidence matched by artist/date. After refresh, its detail's show list
has that count.
Setlist-only dates without catalog shows are outside this collection. This count
can differ from recording-derived discovery and never establishes playable
matches. Raw inventory remains available even when mapped rows leave the mixed
browse list. Zero-match canonical songs remain visible.

The server chooses canonical or imported browse entries; mobile follows `kind`.
Embedded track parts carry compact song references and do not reset discovery
counts or aliases. All six song reads use direct responses and the same ETag
and endpoint-specific replacement rules as the other catalog reads. Known songs
with corrupt canonical chains return no-store 503 responses; imported fallback
remains available where evidence cannot resolve.


Song counters now come from `norm.song_counts` and `public.setlist_song_counts`,
seeded by migration 16. Apply that migration through the normal worker migration
command before starting this v4 build. The [song-count foundation](../../docs/plans/active/2026-10-30-song-counts/README.md)
describes local backfill, permissions, and repair. GETs never recount. File/show
membership is live, so counts may lag after imports or failed refreshes. Admin
song edits refresh atomically; ordinary full repairs commit each artist
separately using the existing artist lock, with no reader lock or new global gate.

For one explicit repair, use the underlying artist UUID (not the public typed ID):

```sql
SELECT * FROM norm.refresh_song_counts('77a58ff9-2e01-c59c-b8eb-cff106049b72'::uuid);
```

Use `SongCounts.RefreshAllAsync` for all artists without retaining artist locks
across the whole repair. Revalidate an endpoint after repair; a changed represented
count changes its ETag even when imported timestamps stay the same.

## Search and listening-ranking review

The [search/ranking walkthrough](../../docs/plans/active/2026-10-29-v4-api/review/search-rankings.md)
provides all ten new route examples, response conventions, and score provenance.
The real catalog backup has no telemetry rows. Its ranking preview uses separately
approved real aggregate maps from the production read-only replica, marked fresh for
24 hours in local Redis DB 0. The capture time, source and sizes are in ignored
`.output/ranking-snapshot-provenance.json`; original local cache values are in
`.output/ranking-cache-before-snapshot.json`. No production or catalog data changed.

Tests use Redis DB 15 and restore every fixed key they write; they do not alter
the review snapshot in DB 0. Production keeps the existing asynchronous score
refresh behavior. If the local snapshot becomes stale, refresh it from real aggregates
before comparing scores; the local empty telemetry cannot reconstruct them.

Every show resource includes nullable `popularity` from its endpoint's context:
artist-local on ordinary catalog reads and artist groups, global on global
popular/trending, organic/fallback on momentum. Shared enrichment accepts the
ranking's loaded map, avoiding extra artist reads. Ranking views are transient on
mobile; they never overwrite offline show data. Their ETags use their own inputs.

The expanded real artist-local snapshot contains 62,650 show metrics across 2,814
artists, with fresh empty maps for 1,552 other local artists that had no matching
activity in the export. Its separate capture time and sizes are recorded under
`artistLocalShows` in the ignored provenance file. Global score maps retain their
earlier capture time. No production writes or synthetic play events were used.
