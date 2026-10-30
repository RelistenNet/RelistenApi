# C1: inspect the real responses

Start the API with `./local-dev/v4-review/run.sh`, then open
[Swagger](http://127.0.0.1:43823/api-docs-v4) or run [requests.http](requests.http).
The [local setup](../../../../../local-dev/v4-review/README.md) explains startup,
captures, the materialized counts, and restoring local approval flags.

The [search and ranking walkthrough](search-rankings.md) covers the final ten
mobile-facing catalog reads and the real aggregate snapshot used locally.

The [date and recent browsing walkthrough](discovery-browsing.md) connects the
new discovery lists to full playback, with exact query and mobile apply rules.

The current review includes a [song-to-playback journey](song-source-journey.md)
as well as stored artist–venue identities and two explicit venue count scopes.
Song discovery keeps exact recording matches separate from imported setlist
appearances. Its test and live-capture results belong in the
[verification log](../verification.md).

Shows now expose `artist_venue`, whose `artist_show_count` belongs to that
artist and canonical place. Its nested `venue.global_show_count` belongs to the
shared venue across approved-read artists. The old flat `show.venue` is removed.
The same relationship metadata appears in show summaries, full shows, canonical
artist-list children, and direct artist venue detail. Only detail adds complete `shows`.

The local host now serves 49 review routes (48 GETs and one POST). Venue and song detail destinations
return metadata and complete `shows` together. Direct
lookups use the resource's ID kind; there is no returned detail URL. Compact
browse children stay compact. The [contracts](../contracts.md) define replacement
ownership. Mobile holds the artist catalog separately and resolves `artist_id`
references from it; detail responses contain no artist arrays. Mobile v4 apply
implementation remains a later checkpoint.

Earlier captures and timings describe their recorded revision. The
[verification log](../verification.md) distinguishes the stored-count evidence
from the newer resource-with-shows contract.

Start with Red Rocks and Grateful Dead: the shared venue has 176 shows across
approved artists; Grateful Dead's relationship has 20 across all its years.

- [Global venue](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f)
- [Grateful Dead relationship](http://127.0.0.1:43823/api/v4/artist-venues/artist_venue_344wrm28)
- [Show on 1978-07-07](http://127.0.0.1:43823/api/v4/shows/show_0gmtpk4g0pf5yfayb76m0j0k7g)

The saved [relationship excerpt](responses/shared-artist-venue-detail-excerpt.json),
[global venue excerpt](responses/shared-venue-detail-excerpt.json), and
[show venue excerpt](responses/shared-show-venue-excerpt.json) preserve the same
IDs and counts. Detail excerpts select the first show; use the live URLs or
ignored full captures for complete memberships.
The show excerpt selects venue-related fields from the recording hierarchy.

## Follow the counts through the catalog

Use the [venue-to-playback walkthrough](venue-source-journey.md): artist venue
list → relationship with shows → show → source → track. Shared venue detail
provides the cross-artist view in the same resource-with-shows format.

1. Open an artist's venue list. A canonical entry has `kind: "artist_venue"` and
   an `artist_venue` child. Its imported alternative has `kind: "imported_venue"`
   and an imported child with its full `show_count`, matching imported detail.
   Imported entries keep unmatched shows reachable, but their full collections
   can overlap canonical entries during partial normalization. Use this list for
   every artist; follow each entry's `kind` instead of inspecting feature flags.
   Embedded imported venues in show/source responses use that same full count.
2. Open `/artist-venues/{artistVenueId}`. Its metadata matches the canonical
   browse child; it adds complete `shows`. The
   `/artists/{artistId}/venues/{venueId}` pair lookup returns the same detail.
   Its native short ID maps to `norm.artist_venues`.
3. Open one of that venue's shows and its year. `artist_venue.id` and
   `artist_show_count` must agree. The relationship count covers all the artist's
   shows at the venue, even in a single-year response.
4. Open `/venues/{venueId}`. Its `shows` spans approved artists, referenced by
   `artist_id` into mobile’s cached artist catalog. Compare `global_show_count` with
   the same venue nested under any artist. Artist-venue detail provides the
   artist-scoped membership; it never narrows the nested global count.
5. Keep playing through the show's source and direct track. Imported titles,
   URLs, exact parents, and track `normalization` remain unchanged.

Counts come from stored relationships and can lag current show membership after
an interrupted write hook or out-of-band SQL repair. GET requests never repair
or mint relationships. With an up-to-date full refresh, the counts should agree
with their complete qualifying show collections. Disabled artists do not
contribute to the public global count; global venue metadata remains readable
with a count of zero when no approved artist contributes shows.

For display, preserve the imported venue name and locality. The canonical label
is fallback only when imported data is absent:

```js
const venue = show.artist_venue?.venue;
const label = show.imported_venue?.name ?? venue?.name;
const venueId = venue?.id;
```

Missing, disabled, unmatched, or unmaterialized relationships return
`artist_venue: null`, keeping imported fallback usable. A source retains its own
imported venue observation, which may differ from the show's venue.

## Follow a song to its playable files

Use the [song-to-playback walkthrough](song-source-journey.md) to compare the
artist browse list, canonical song detail, exact matched files and shows, and
imported setlist detail. Start with
[Scarlet Begonias](http://127.0.0.1:43823/api/v4/songs/song_62mhvjyd).
Compare `matched_track_count` and `matched_show_count` with the complete recording
lists. Imported `setlist_show_count` describes a separate collection; it can
legitimately differ. Full live captures and test results are recorded separately
from this guide.

## Review offline catalog sync

Open [Grateful Dead’s complete catalog](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/catalog)
and [Ryan Adams’ raw catalog](http://127.0.0.1:43823/api/v4/artists/artist_1e72h78badf1zvzkvkzcp7kk2a/catalog).
Both use the same 12 top-level fields. Compare their artist, show, imported venue,
imported song, and browse collections with the individual list routes. The
approved catalog retains imported fallback alongside canonical browse; the raw
catalog keeps imported browsing usable without invented normalized entities.

A complete snapshot replaces only the artist-owned memberships. Its
`artist_venues` refreshes available approved relationships, including retained
zero counts; `venues` and `songs` are shared entity patches. It contains no
recording hierarchies. Inspect tracks through their own detail routes, whose
validators own track-normalization changes outside the snapshot fields. A matching
artist snapshot ETag returns 304. Empty collections still establish complete
membership; failed fetch/apply preserves the last complete state.

Then run the mixed `POST /catalog/resolve` request in [requests.http](requests.http).
It includes typed and explicit legacy references, repeated requests, missing IDs,
and a merged canonical venue from the restored data. Retirement behavior uses
regression fixtures: the restored catalog currently has no retired reservations.
Results keep input order and duplicate references; entities deduplicate and include shallow
parents. Imported targets keep imported identity. Resolve-only patches never clear
unreturned children or personal membership. The POST is no-store with no ETag.

The [contracts](../contracts.md#complete-artist-snapshots) define ownership and
validation; the [verification log](../verification.md) records completed checks.
The saved [approved catalog excerpt](responses/artist-catalog-excerpt.json),
[raw catalog excerpt](responses/raw-artist-catalog-excerpt.json), and
[resolver excerpt](responses/resolver-excerpt.json) select one entity per domain
group. Full arrays live in the real responses and ignored captures. Artist metadata
is cached separately; the resolver returns artists only when explicitly requested.
Mobile apply remains future work, and no account/auth changes occur in this review.

## Tours, eras, and source reviews

The [request collection](requests.http) adds these live responses:

- [Phish tours](http://127.0.0.1:43823/api/v4/artists/artist_6aaf982021nrst0m68ew1djerc/tours):
  130 compact tours with catalog show counts.
- [Fall Tour 1997](http://127.0.0.1:43823/api/v4/tours/tour_4emejbsvpbph76zy5kh1n954vt):
  direct tour metadata with 21 complete show summaries.
- [Phish eras](http://127.0.0.1:43823/api/v4/artists/artist_6aaf982021nrst0m68ew1djerc/eras):
  four ordered metadata entries.
- [Recording reviews](http://127.0.0.1:43823/api/v4/sources/source_2cr3tmxwvyxp5g9jxzk1jf944x/reviews):
  361 original reviews, including typed identities and source references.

Saved samples include a [tour detail excerpt](responses/phish-tour-detail-excerpt.json),
[compact tour inventory excerpt](responses/phish-tours-excerpt.json),
[era metadata](responses/phish-eras.json), and [source review excerpt](responses/source-reviews-excerpt.json).
Tour `show_count` has the same catalog-show meaning
in lists, detail, and embedded show tours. Source sets remain embedded in a
source’s recording hierarchy; there is no direct source-set endpoint.

Check that a tour needs only its `tour_` ID to open, eras have `order` and `name`
without IDs, and each review has `source_review_` identity and its `source_`
parent. The [contracts](../contracts.md#tours-eras-and-source-reviews) define ordering,
empty results, and exactly which membership mobile will replace. Reviews preserve
upstream text and ratings. No auth or mobile runtime behavior changes here.

## Review the remaining response contracts

- [Resolved medley](responses/matched-track.json): one playable file and its
  imported title, with ordered normalized song parts.
- [Partial track](responses/partial-track.json) and [longer JRAD example](responses/partial-medley-track.json):
  unresolved text remains visible and playable.
- [Raw](responses/raw-track.json) and [disabled](responses/disabled-track.json)
  states: imported playback remains usable and parent IDs are typed strings.
- [Artist venue](responses/shared-artist-venue-detail-excerpt.json), [direct source](responses/venue-journey-source.json),
  and [imported fallback](responses/disabled-imported-venue-detail-excerpt.json): compare direct
  and nested resource identity without confusing count scopes.
- [Henry J. Kaiser venue](responses/kaiser-venue.json): deduplicated `{ name, kind }`
  aliases, including retained merged names. Kinds are `official`, `short`, and
  `spelling`; no date ranges or `venue_display` are exposed.

OpenAPI must describe the actual graph, nullable relationships, string IDs,
counts, and closed string enums. Responses remain direct resources/lists with
weak payload ETags. Count corrections and approval changes can invalidate an ETag
without changing imported timestamps. No-op recounts preserve it. Database
failure returns a no-store 503 problem; recovery returns a complete snapshot.

## Local evidence and scope

Alec enabled normalized reads locally for all 12 artists with normalized data.
Migration 15 and a full refresh were then applied to local `relisten_db`.
Independent per-show verification matched 5,397 relationships and 13,418 show
memberships, with no mismatches. A repeated recount preserved IDs, counts, and
timestamps. The [foundation plan](../../2026-10-30-artist-venues/README.md) owns
count maintenance and records concurrency, permissions, and repair behavior.

The branch stack is normalization enums → artist-venue foundation → song-count
foundation → v4 API.
Production was not changed. This checkpoint qualifies local response review;
remaining API routes, account migration, mobile rollout, and production capacity
remain separate work.
