# Review the venue-to-playback journey

The next C1 review connects artist browsing to a physical venue, its shows, and a
playable recording. The local API now exposes 16 GET routes. Start with the
[request collection](requests.http) or [Swagger](http://127.0.0.1:43823/api-docs-v4).
The capture script uses real restored records; regression tests use a disposable
catalog. Neither review path runs ingestion or normalization jobs.

Start with these live responses from the restored catalog:

- [Grateful Dead venue groups](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues):
  canonical artist–venue resources and imported fallback entries share one list.
- [Oakland artist venue](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_wwrsykhn)
  and its [shows](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_wwrsykhn/shows).
- [Red Rocks across artists](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f/shows):
  inspect the shared venue's count and show assignments across approved artists.
- [One Oakland recording](http://127.0.0.1:43823/api/v4/sources/source_0ey8bt709t1kgkmtpmgvq85art)
  and [one of its tracks](http://127.0.0.1:43823/api/v4/source-tracks/track_11be6feqht0xd6q911jmx31nqs).

Saved full examples: [artist venue entry](responses/artist-venue.json),
[direct source](responses/venue-journey-source.json), and
[unnormalized artist's imported venue detail](responses/disabled-imported-venue.json).
The request collection also includes unmatched shows within an approved artist.
Large collections, response headers, and request timings remain in the ignored
`local-dev/v4-review/.output/` directory.

## Follow one normalized venue

1. Read `/api/v4/artists/{artistId}/venues`. Each entry has `kind`,
   `artist_venue`, and `imported_venue`, with exactly one target populated. A
   canonical entry has `kind: "artist_venue"`; its child owns the stored ID,
   `artist_id`, `artist_show_count`, timestamps, and nested shared venue.
   Imported children include their full `show_count`, matching imported detail.
   The server handles approval and partial coverage; clients use this list for
   every artist and follow the returned `kind`.
2. For a canonical child, read `/api/v4/artists/{artistId}/venues/{venueId}`.
   The response is the direct relationship resource, equal to the list's child.
   Read `/api/v4/venues/{venueId}` for the shared venue alone: global count,
   identity, locality, parent venue, and distinct `{ name, kind }` aliases.
   Merged IDs resolve to the survivor; no name periods are exposed.
3. Read `/api/v4/artists/{artistId}/venues/{venueId}/shows`. Every summary's
   `artist_venue` must match the relationship detail, including its artist-wide
   count. A year-filtered response retains that same full artist/place count.
4. Read `/api/v4/venues/{venueId}/shows` for approved assignments across artists.
   Add `?artist_id={artistId}` to narrow show membership. Nested and direct
   `global_show_count` stays global under that filter. After a successful count
   refresh, the full qualifying show lists should agree with their scoped counts.
5. Open a returned `/api/v4/shows/{showId}`. Display its imported venue label and
   locality, linking through `artist_venue.venue.id` when present. Canonical name
   is fallback only when imported data is absent. Missing materialized
   relationships preserve imported fallback and never trigger a GET-time refresh.
6. Open `/api/v4/sources/{sourceId}` from that show's `sources`. The resource must
   equal its nested show counterpart, including its own imported venue, links,
   sets, and tracks. Only this source's active files are included. Its source
   venue can differ from the show's imported venue without changing show grouping.
7. Open `/api/v4/source-tracks/{sourceTrackId}` from the source. The playable title,
   URLs, parent IDs, and `normalization` must agree with the nested track. Partial,
   raw, and disabled normalization remain usable.

## Follow the imported fallback

For an `imported_venue` browse entry, open
`/api/v4/artists/{artistId}/imported-venues/{importedVenueId}`. The response contains
the imported resource fields plus `show_count` and `shows`. It uses the show's
artist and imported venue assignment, even when one of those shows has a
`artist_venue` link. A recording's different venue observation does not add a show
to this collection. The count matches the imported child in the browse list.
That child appears when at least one show needs imported fallback, but opens the
full imported collection. Its shows can therefore overlap canonical entries
during partial normalization; do not sum mixed browse counts as an artist total.

The separate `/api/v4/artists/{artistId}/imported-venues` list returns all raw
venue rows for that artist, including unused rows with `show_count: 0`. V4 derives
these counts from its actual show collection. The legacy cached count can use
source-level observations and remains unchanged.

This path must work for artists without normalization and for unmatched shows
within an approved artist. No canonical venue is fabricated from a similar name.

## Check errors and refresh

- A malformed or wrong-kind ID returns 422; an unknown parent or foreign imported
  venue returns 404. A canonical artist detail with no approved stored relationship
  also returns 404. A known artist and venue with no qualifying shows returns `[]` from
  the show collection, including when normalization is disabled.
- Stored counts can lag show membership after a failed write hook or out-of-band
  repair. GET requests neither recompute counts nor write relationships.
- Responses are direct resources or lists with weak ETags. A matching validator
  returns 304. Alias name/kind or show-assignment changes refresh affected JSON;
  date-only alias edits do not, because dates are not represented.
- Database failures return no-store 503 problems. Retrying after recovery returns
  a complete snapshot rather than a partial hierarchy.

These eight new routes are part of the existing full-route plan. Song discovery,
search/recent lists, artist bootstrap, legacy adapters, accounts, and mobile remain
separate work. Review this journey before expanding the next catalog family.
