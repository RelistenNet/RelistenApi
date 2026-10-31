# Mobile handoff: prove one v4 catalog journey

The server is ready for the local C3 mobile pilot. The [contract audit and origin
measurements](api-qualification.md) cover all 51 operations, real response/schema
agreement, conditional reads, and representative concurrent requests. Production
capacity and mobile memory/apply time still need release qualification.

Alec and Switz should now prove one artist sync → show → source → playable track
journey, with an offline restart. Keep one Realm/repository set and the existing
player. Account/auth adoption remains separate; this pilot needs no login.

## Start from the current mobile code

The sibling checkout inspected on October 31 was `codex/c2-account-client` at
`0e033d5`, with Realm schema 17. It already has typed account targets and a UUID
codec in `relisten/util/typed_uuid_id.ts`; catalog reads still use v3.
Recheck the branch before implementation. Earlier mobile investigation notes
describe an older account baseline and say v4 live routes are missing; those
status claims are superseded by this handoff.

The main integration points are `relisten/api/client.ts`,
`relisten/api/catalog_config.ts`, `relisten/realm/schema.ts`, the existing
repositories, and the account target adapter. Leave the v3 resolver DTOs intact.
Add v4 wire types and a small transport/apply adapter; do not change every URL
version in the existing client.

For local iOS Simulator review, set
`EXPO_PUBLIC_RELISTEN_CATALOG_ORIGIN=http://127.0.0.1:43823` before starting Metro.
The value is an origin, without `/api`. Start the API with
`./local-dev/v4-review/run.sh`; [Swagger](http://127.0.0.1:43823/api-docs-v4/index.html)
documents the current schemas, ETags and 304 responses. Loopback URLs target the
Mac/simulator; a physical device needs a reachable HTTPS origin.

## Implement only the pilot

1. **Transport and identity.** Decode UUID-backed catalog IDs at the adapter and
   retain existing Realm UUIDs, downloaded filenames, queue and history keys.
   `track_` names the existing playable file. Short `venue_`, `song_` and
   `artist_venue_` IDs remain strings in additive normalized models. Feature code
   should not decode IDs. Keep typed account IDs/targets unchanged in either
   catalog transport.
2. **Authoritative apply.** Apply the response, its owned memberships and its ETag
   in one Realm transaction. Reuse coherent mapping/write primitives, but bypass
   v3's timestamp-derived hash and request cooldown. Use the agreed 15-minute
   refresh default with manual refresh and reconnect retry. Discard superseded
   same-query responses and results from an old transport/account generation.
3. **Playback and recovery.** Load the artist catalog, then load show/source/track
   detail on demand. Preserve imported venue labels and track titles. Store the
   server's optional canonical links and ordered track interpretation. Verify
   source switching, playback, offline restart, correction refresh and retry
   before adding all venue/song screens.

## Apply ownership, not absence everywhere

- `/artists/{id}/catalog` replaces its artist-owned collection memberships,
  including empty lists. Its `artist_venues`, `venues` and `songs` arrays patch
  entities. They never delete globally shared records or recording detail.
- Full show detail replaces that show's active sources and each fully returned
  source/set's child membership. Full source detail owns only its sets/tracks.
  Retain entities required by downloads, queues, history and favorites.
- Compact parents and track-part songs update supplied fields only. They cannot
  clear richer metadata, counts, aliases, reviews or child memberships.
- Venue/song/year/tour detail owns its complete `shows` membership. Song `/tracks`
  owns only its file matches. Artist mixed venue/song entries use their returned
  `kind`; mobile does not choose a route using the normalization feature flag.
- Rankings remain transient online views. Search/filtered lists replace only
  their exact query results. Resolver arrays are shallow patches.
- Save a validator only after successful apply. A 304 is usable only with the
  matching applied local response; otherwise refetch without a validator.
  Fetch/apply failure keeps the last complete state and validator.

These are endpoint contracts, not instructions to build a generic sync engine.
Use [endpoints.json](../endpoints.json) for the full replacement map and
[contracts](../contracts.md#mobile-persistence-transport-and-ui) for exact rules.

## Concrete review cases

- [Artist bootstrap](http://127.0.0.1:43823/api/v4/artists), then
  [Grateful Dead's catalog](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/catalog).
  This catalog has 2,081 show summaries; recording hierarchies stay lazy.
- [Matched show](http://127.0.0.1:43823/api/v4/shows/show_2jn80dqf5ntfxmmkwx11j27gdk),
  [matched medley file](http://127.0.0.1:43823/api/v4/source-tracks/track_3s1y1rg4yaxa7nqw31wk617b0d),
  and [partial file](http://127.0.0.1:43823/api/v4/source-tracks/track_7b6v6pgvzqfvhd0s7avr18bdw5).
  Follow the returned `source_id`; the direct and nested track must agree.
  A medley remains one playable file, with no per-part seek offsets.
- [Imported-only show](http://127.0.0.1:43823/api/v4/shows/show_3pdmj761gph53bb7ad3db8v4x4)
  and [Spafford's raw catalog](http://127.0.0.1:43823/api/v4/artists/artist_0h4h1nj2n61q70bb880qksd6yy/catalog).
  Missing normalization must preserve browsing and playback.
- [Red Rocks relationship](http://127.0.0.1:43823/api/v4/artist-venues/artist_venue_344wrm28)
  and [global venue](http://127.0.0.1:43823/api/v4/venues/venue_fp4h900f).
  Keep `artist_show_count`, `venue.global_show_count` and imported `show_count`
  in their distinct scopes. Do not sum overlapping mixed browse entries.

For the correction test, use a disposable fixture: change normalization without
advancing imported timestamps. Revalidate the owning show/track, verify the
overlay changes, and keep the imported display text. A track-only correction
need not change the artist-catalog ETag when no represented catalog field changes.

The pilot passes when an upgrade from the current schema-17 account build
preserves imported keys/files/queue/history, failed apply commits no validator,
retry succeeds, and an offline cold start plays the retained download. Retain
direct older-schema upgrade coverage for release. Alec reviews these observable
journeys before broader UI adoption. V3 catalog fallback must preserve typed
accounts and additive normalized rows; no legacy server adapter is needed.

## Keep the remaining work small

The large Drums file-match response is about 15.5 MB uncompressed. Do not eagerly
sync every song's `/tracks` while applying an artist catalog. Measure actual
mobile parse/apply memory during the pilot. Cloudflare owns downstream response
caching; the API needs no additional response-cache framework or zero-allocation
requirement. Production capacity/latency budgets, deployment and full C5 browsing
remain separate decisions, not missing product rules for this pilot.
