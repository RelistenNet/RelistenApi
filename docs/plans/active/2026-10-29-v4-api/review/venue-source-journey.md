# Review the venue-to-playback journey

The next C1 review connects artist browsing to a physical venue, its shows, and a
playable recording. The local API now exposes 16 GET routes. Start with the
[request collection](requests.http) or [Swagger](http://127.0.0.1:43823/api-docs-v4).
The capture script uses real restored records; regression tests use a disposable
catalog. Neither review path runs ingestion or normalization jobs.

Start with these live responses from the restored catalog:

- [Grateful Dead venue groups](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues):
  479 normalized groups and 65 imported fallback groups cover 2,081 shows.
- [Oakland artist venue](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_wwrsykhn)
  and its [34 shows](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_wwrsykhn/shows).
- [Red Rocks across artists](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f/shows):
  176 shows across 12 approved artists in this snapshot.
- [One Oakland recording](http://127.0.0.1:43823/api/v4/sources/source_0ey8bt709t1kgkmtpmgvq85art)
  and [one of its tracks](http://127.0.0.1:43823/api/v4/source-tracks/track_11be6feqht0xd6q911jmx31nqs).

Saved full examples: [artist venue entry](responses/artist-venue.json),
[direct source](responses/venue-journey-source.json), and
[unnormalized artist's imported venue detail](responses/disabled-imported-venue.json).
The request collection also includes unmatched shows within an approved artist.
Large collections, response headers, and request timings remain in the ignored
`local-dev/v4-review/.output/` directory.

## Follow one normalized venue

1. Read `/api/v4/artists/{artistId}/venues`. Each entry has `kind`, `show_count`,
   `venue`, and `imported_venue`. Exactly one target is populated. Approved shows
   group by the surviving normalized venue; unmatched or unapproved shows group
   by their imported venue. One imported row can therefore contribute shows to
   different entries.
2. For a `venue` entry, read `/api/v4/artists/{artistId}/venues/{venueId}`. It
   returns the same artist-scoped entry and count as the list. Read
   `/api/v4/venues/{venueId}` for the canonical resource alone: identity, locality,
   parent venue, and distinct `{ name, kind }` aliases. Merged IDs resolve to the
   surviving resource, including retained aliases; no name periods are exposed.
3. Read `/api/v4/artists/{artistId}/venues/{venueId}/shows`. Compare the list length
   with the entry's `show_count`. These are distinct server catalog shows, not
   summed imported counters or the mobile cache.
4. Read `/api/v4/venues/{venueId}/shows` to see approved assignments across artists.
   Add `?artist_id={artistId}` to recover the same artist-filtered collection.
   Global venue metadata remains readable when an artist's normalization flag is
   off; that artist's shows do not appear in the canonical collection.
5. Open a returned `/api/v4/shows/{showId}`. Display its imported venue label and
   locality, with `venue.id` as the normalized navigation target when present.
   The canonical name is fallback only when the imported venue is absent.
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
normalized link. A recording's different venue observation does not add a show
to this collection.

The separate `/api/v4/artists/{artistId}/imported-venues` list returns all raw
venue rows for that artist, including unused rows with `show_count: 0`. V4 derives
these counts from its actual show collection. The legacy cached count can use
source-level observations and remains unchanged.

This path must work for artists without normalization and for unmatched shows
within an approved artist. No canonical venue is fabricated from a similar name.

## Check errors and refresh

- A malformed or wrong-kind ID returns 422; an unknown parent or foreign imported
  venue returns 404. A canonical artist detail with no approved membership also
  returns 404. A known artist and venue with no qualifying shows returns `[]` from
  the show collection, including when normalization is disabled.
- Responses are direct resources or lists with weak ETags. A matching validator
  returns 304. Alias name/kind or show-assignment changes refresh affected JSON;
  date-only alias edits do not, because dates are not represented.
- Database failures return no-store 503 problems. Retrying after recovery returns
  a complete snapshot rather than a partial hierarchy.

These eight new routes are part of the existing full-route plan. Song discovery,
search/recent lists, artist bootstrap, legacy adapters, accounts, and mobile remain
separate work. Review this journey before expanding the next catalog family.
