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

The expanded C1 browse checkpoint exposes 16 GET routes. Follow the
[venue-to-playback walkthrough](../../docs/plans/active/2026-10-29-v4-api/review/venue-source-journey.md)
to inspect the resource links. Implemented families are:

- `GET /api/v4/artists` and `/api/v4/artists/{artistId}`: artist list/detail,
  counts, capabilities and provider metadata.
- `GET /api/v4/artists/{artistId}/years`: year list; append `/{yearId}` or
  `/by-number/{year}` for year detail with show summaries.
- `GET /api/v4/artists/{artistId}/shows`: the complete artist show-summary list.
- `GET /api/v4/artists/{artistId}/venues`: canonical show groups with imported
  fallback entries. Canonical children are stored `artist_venue` resources with
  `artist_show_count`. Append `/{venueId}` for that direct relationship resource
  or `/{venueId}/shows` for its artist-filtered shows.
- `GET /api/v4/venues/{venueId}`: canonical metadata, aliases, and
  `global_show_count`; append `/shows`
  for approved assignments across artists, optionally filtered by `artist_id`.
- `GET /api/v4/artists/{artistId}/imported-venues`: all imported rows and show
  counts, including unused rows. Append `/{importedVenueId}` for that row with its
  complete show collection.
- `GET /api/v4/shows/{showId}`: full show with sources, sets, tracks, imported
  venue observations, stored `artist_venue` links, aliases and approved normalization.
- `GET /api/v4/sources/{sourceId}`: that same complete source resource directly,
  including its own imported venue and active playable files.
- `GET /api/v4/source-tracks/{sourceTrackId}`: that same track resource directly,
  with exact typed artist/show/source/set parents.

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
