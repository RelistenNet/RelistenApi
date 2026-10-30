# C1: inspect the real responses

Start the API with `./local-dev/v4-review/run.sh`, then open
[Swagger](http://127.0.0.1:43823/api-docs-v4) or run [requests.http](requests.http).
The [local setup](../../../../../local-dev/v4-review/README.md) explains startup,
captures, the materialized counts, and restoring local approval flags.

The current review adds stored artist–venue identities and two explicit count
scopes. Shows now expose `artist_venue`, whose `artist_show_count` belongs to that
artist and canonical place. Its nested `venue.global_show_count` belongs to the
shared venue across approved-read artists. The old flat `show.venue` is removed.
The same relationship resource appears in show summaries, full shows, canonical
artist-list children, and direct artist venue detail.

The local host is running this graph. The 46-request real-data capture completed,
and all count, identity, and fallback checks passed.
The [verification log](../verification.md#stored-artistvenue-response-review-2026-10-30)
records the acceptance checks; earlier captures describe their recorded revision.

Start with Red Rocks and Grateful Dead: the shared venue has 176 shows across
approved artists; Grateful Dead's relationship has 20 across all its years.

- [Global venue](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f)
- [Grateful Dead relationship](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/venues/venue_fp4h900f)
- [Show on 1978-07-07](http://127.0.0.1:43823/api/v4/shows/show_0gmtpk4g0pf5yfayb76m0j0k7g)

The saved [relationship response](responses/shared-artist-venue.json),
[global venue response](responses/shared-venue.json), and
[show venue excerpt](responses/shared-show-venue-excerpt.json) preserve the same
IDs and counts. The excerpt includes selected venue-related fields from the full
show response; use its URL for the complete recording hierarchy.

## Follow the counts through the catalog

Use the [venue-to-playback walkthrough](venue-source-journey.md): artist venue
list → relationship detail → venue shows → show → source → track. The existing
16 GET routes retain their paths. This update changes their shared resource graph.

1. Open an artist's venue list. A canonical entry has `kind: "artist_venue"` and
   an `artist_venue` child. Its imported alternative has `kind: "imported_venue"`
   and a base imported child. Its `fallback_show_count` counts that entry's
   unmatched subset; canonical entries set it to null. It must not overwrite the
   imported venue's full `show_count` from imported-venue routes.
2. Open `/artists/{artistId}/venues/{venueId}`. It returns the direct relationship
   resource, identical to the canonical child in the list. Its native short ID
   maps to `norm.artist_venues`; the artist and venue retain their own IDs.
3. Open one of that venue's shows and its year. `artist_venue.id` and
   `artist_show_count` must agree. The relationship count covers all the artist's
   shows at the venue, even in a single-year response.
4. Open `/venues/{venueId}` and its cross-artist show list. Compare
   `global_show_count` with the same venue nested under any artist. Filtering by
   `artist_id` narrows show membership, never the global venue count.
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

## Review the remaining response contracts

- [Resolved medley](responses/matched-track.json): one playable file and its
  imported title, with ordered normalized song parts.
- [Partial track](responses/partial-track.json) and [longer JRAD example](responses/partial-medley-track.json):
  unresolved text remains visible and playable.
- [Raw](responses/raw-track.json) and [disabled](responses/disabled-track.json)
  states: imported playback remains usable and parent IDs are typed strings.
- [Artist venue](responses/artist-venue.json), [direct source](responses/venue-journey-source.json),
  and [imported fallback](responses/disabled-imported-venue.json): compare direct
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

The branch stack is normalization enums → artist-venue foundation → v4 API.
Production was not changed. This checkpoint qualifies local response review;
remaining API routes, account migration, mobile rollout, and production capacity
remain separate work.
