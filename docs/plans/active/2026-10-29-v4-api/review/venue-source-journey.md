# Venue to playable recording

For Alec and Switz: inspect one artist's venue groups, follow their shows to
playback, and compare a shared venue across artists. Start the
[local API](../../../../../local-dev/v4-review/README.md), then open
[Swagger](http://127.0.0.1:43823/api-docs-v4). This checkpoint has 20 GET routes.
Manual review uses restored records; tests use a disposable catalog. Neither
review path runs ingestion or normalization jobs.

## Follow a shared venue

1. Open [Grateful Dead's venues](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues).
   Each entry has `kind` and exactly one populated target: `artist_venue` or
   `imported_venue`. Clients use this list for every artist; the server handles
   approval and partial normalization. These targets contain compact metadata,
   without detail `shows` arrays.
2. Open [Grateful Dead at Red Rocks](http://127.0.0.1:43823/api/v4/artist-venues/artist_venue_344wrm28).
   It returns relationship metadata, including `artist_show_count`, plus complete `shows`. The
   [artist-plus-venue lookup](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_fp4h900f)
   returns the same response. The relationship ID is the membership owner for
   either lookup.
3. Open [Red Rocks across artists](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f).
   This has the shared venue metadata, aliases, `global_show_count`, complete
   `shows`. Use each show’s `artist_id` to find its artist in the separately loaded
   artist catalog. In the refreshed snapshot, the venue has 176 shows and Grateful
   Dead's relationship has 20. Its nested and direct global count must agree.
4. Open [one show](http://127.0.0.1:43823/api/v4/shows/show_0gmtpk4g0pf5yfayb76m0j0k7g).
   `show.artist_venue` retains the same relationship ID and artist-wide count,
   even in a year response. Display `show.imported_venue.name` and locality,
   linking through `artist_venue.venue.id` when available. Canonical metadata is
   fallback when imported data is absent. Venue aliases expose `{ name, kind }`,
   with no name periods or server-selected display label.
5. Open the show's `/api/v4/sources/{sourceId}`, then one of its
   `/api/v4/source-tracks/{trackId}`. The direct resource must match its nested
   counterpart: imported title, URLs, exact parents, and track `normalization`.
   A source's venue observation can differ from the show's venue without changing
   show grouping. Partial, raw, and disabled normalization remain playable.

## Follow the imported fallback

Open [the imported Ace's Studio venue](http://127.0.0.1:43823/api/v4/imported-venues/imported_venue_78331pxdqjdg434rrgm1d2pnrj).
Its globally unique imported ID needs no artist path parameter. The response has
imported metadata, `show_count`, and complete `shows`.
`/venues/{id}` accepts only `venue_` IDs; imported IDs use `/imported-venues/{id}`.
There is no `detail_url` field.

Imported detail includes every show-level assignment to that row, including
shows with canonical links. Source-only observations add no shows. An imported
browse entry appears when at least one show needs fallback, but opens the full
imported collection. Its shows can overlap canonical entries; do not sum mixed
browse counts as an artist total.

`/api/v4/artists/{artistId}/imported-venues` remains the compact full imported
inventory, including mapped and unused rows. An unused imported detail returns
`shows: []` and retains its `artist_id`. Imported browsing and detail
work without normalization; similar names never fabricate a canonical venue.

## Check refresh and failure behavior

Each detail is one repeatable-read snapshot of its metadata and membership,
validated by one payload ETag. A matching `If-None-Match` returns 304.
Changing shows can invalidate the ETag without changing imported parent timestamps.
Bare venue `/shows` routes and artist-scoped imported detail routes are removed.

Mobile will upsert show summaries, replace only the detail
owner's show membership, and save the ETag in one Realm transaction. An empty
list clears that membership. Shared shows, downloads, favorites, and other query
memberships remain. A failed fetch/apply preserves the last complete state.
Compact browse responses never clear detail membership.

Counts are stored separately and can lag live membership after a failed write
hook or out-of-band edit. Never replace a stored count with a local array length.
GET requests neither refresh counts nor create relationships.

Malformed or wrong-kind IDs return 422; missing resources and unavailable
artist–venue relationships return 404. A retained empty relationship is a valid
detail. Database failures return no-store 503 problems; recovery returns a complete
snapshot. Global venue metadata remains available with an empty show list when no
approved artist contributes shows.

Saved examples and full ignored captures are linked from the
[review overview](README.md). Review this journey and the
[song journey](song-source-journey.md) before account/auth work.
