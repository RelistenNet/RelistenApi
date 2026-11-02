# Mobile handoff: load v4 through the existing browsing journey

Use the smaller v4 endpoints for artist → years → year → show → source → playable
track navigation. The combined `/artists/{id}/catalog` endpoint has been removed.
The earlier handoff directed the pilot to fetch all artist collections; that
instruction is superseded. Do not replace it with an eager fan-out to every list.
The local C3 playback proof and first C5 venue/song/favorite journeys are complete.
Alec and Switz can review them on the retained iPhone 17. Account/auth deployment
and release activation stay separate; catalog reads need no login.

## Current integration

The mobile pilot is in the sibling `relisten-mobile` checkout on
`codex/v4-mobile-pilot`, with Realm schema 20 storage. Recheck its current
branch and working tree before editing. Its public entrypoints are
`relisten/api/client.ts`, `relisten/api/v4/catalog_client.ts` and the existing
Realm repositories. Keep one repository set and the existing player.

For simulator review, set
`EXPO_PUBLIC_RELISTEN_CATALOG_ORIGIN=http://127.0.0.1:43823` (without `/api`) and
opt into `EXPO_PUBLIC_RELISTEN_CATALOG_TRANSPORT=v4`. V3 remains the pre-cutover default; adopting v4 is one-way.
Start the API with `./local-dev/v4-review/run.sh` and inspect
[Swagger](http://127.0.0.1:43823/api-docs-v4/index.html). Physical devices need a
reachable HTTPS origin.

## Fetch what the screen needs

| Use | Endpoint | Local replacement |
| --- | --- | --- |
| Artist metadata | `GET /artists` or `/artists/{artistId}` | Upsert returned artist fields. |
| Artist's years | `GET /artists/{artistId}/years` | Replace only that artist's year membership. |
| Selected year | `GET /artists/{artistId}/years/{yearId}` or `/years/by-number/{year}` | Upsert year metadata; replace only that year's show membership. |
| Show / source | `GET /shows/{showId}` or `/sources/{sourceId}` | Replace children only for the fully returned owner. |
| Track | `GET /source-tracks/{trackId}` | Upsert track fields and supplied shallow parents. |
| Venue / song / tour / era browsing | Respective artist list and resource-detail endpoints | Load on demand; replace only the requested list/detail membership. |

All paths above are under `/api/v4`. A complete empty list clears its own cached
membership. Omission never globally deletes a shared entity or another query's
membership. Search and filtered results own only their exact query results;
rankings stay transient online views. Resolver responses are shallow entity
patches. Read [endpoints.json](../endpoints.json) and
[contracts](../contracts.md#load-collections-as-needed) for the full map.

Keep response apply and its validator in one Realm transaction. Use ordinary
ETags/304s, the existing refresh interval, manual refresh and reconnect retry.
Fetch/apply failure must leave prior local data and validator intact. Do not add
an artist-wide validator or invalidate every query after a write. Smaller
responses can still overlap shared entities; removing the full download alone
is not proof that every overlapping-response race is resolved.

## Preserve the existing player and imported display

Decode UUID-backed catalog IDs at the adapter. Keep existing Realm UUID keys,
download filenames, queues and history. Short `venue_`, `song_` and
`artist_venue_` IDs remain opaque strings. Keep typed account targets unchanged.

Display imported venue labels and track titles. Store optional canonical links
and track `normalization` internally. A medley stays one playable file, with no
invented per-part seek offsets. Compact parent projections update supplied fields
without clearing richer metadata, aliases, counts, reviews or children. Retain
records needed by downloads, queues, history and favorites.

## Review against real data

1. Open [artists](http://127.0.0.1:43823/api/v4/artists), then
   [Grateful Dead's years](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/years)
   and [1985's shows](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/years/by-number/1985).
   Confirm mobile requests these endpoints rather than all artist collections.
2. Open a returned show, select a source and play a track. Exercise source
   switching and a medley/partial file while retaining imported display text.
   The [matched show](http://127.0.0.1:43823/api/v4/shows/show_2jn80dqf5ntfxmmkwx11j27gdk)
   and [partial file](http://127.0.0.1:43823/api/v4/source-tracks/track_7b6v6pgvzqfvhd0s7avr18bdw5)
   remain useful direct cases.
3. Repeat with [Spafford's years](http://127.0.0.1:43823/api/v4/artists/artist_0h4h1nj2n61q70bb880qksd6yy/years)
   and an [imported-only show](http://127.0.0.1:43823/api/v4/shows/show_3pdmj761gph53bb7ad3db8v4x4).
   Missing normalization must preserve browsing and playback.
4. With disposable data, change normalization without advancing imported
   timestamps, refresh its owning endpoint, and verify the correction. Exercise
   failed apply followed by retry and a downloaded track after an offline cold
   start. Preserve upgrade coverage for imported identities and files.

Keep tests at these public behavior boundaries. Remove full-snapshot-only wire
models, apply code, query state and tests after replacing their callers. Remove
the extra UTF-8 diagnostic copy used only to count response bytes. Keep useful
shared mapping code; add no generic sync engine.

The [origin audit](api-qualification.md) retains historical measurements; its
full-artist snapshot results describe the removed endpoint. The large Drums file
list remains separate and uses gzip as agreed. Do not eagerly download song file
matches. Cloudflare owns downstream caching. Production capacity, deployment and
full venue/song UI adoption remain later checkpoints.

## November 1 local checkpoint

The mobile implementation now uses mixed canonical/imported venue and song
lists, artist-scoped and global venue detail, canonical song shows and lazy file
matches, and the existing favorite repository. My Library has a separate Venues
destination. Canonical venue favorites span artists and never add implicit show
favorites; canonical song favorites contribute their owning artist only.

The retained iPhone 17 proved artist/year/show/source browsing, source switching,
selected song-file playback and the original downloaded file after a cold launch.
The offline test used Always Offline Mode plus an unreachable catalog origin;
it did not disable the simulator radio. Cached content rendered immediately,
playback restored stopped at 16.352 seconds, and explicit Play advanced it using
the existing UUID-named MP3.

Red Rocks returned 20 Grateful Dead shows in artist context and 176 across
12 artists globally in this local snapshot. Removing its venue favorite left the
existing two show favorites intact. China Cat Sunflower's 4,459 file matches
retained imported titles and opened the exact selected recording for playback.

Independent checks passed all 335 mobile tests, TypeScript and lint, plus four
real-API/production-client/Realm integration cases. Those cases include failure
recovery, overlapping year/show and song-track repairs, and a real merged venue
ID returning its survivor. Shared metadata invalidation retains cached detail
membership, only changed fields make related details stale, and an ordinary
second song-recordings open makes no new requests. Empty cached lists remain
usable after offline refresh failure. UUID-backed routes retain exact ID checks;
canonical routes accept the server's merge survivor while checking ownership.

Implementation commits, screenshots and the detailed native record belong in
the sibling mobile plan, `docs/plans/active/2026-10-31-v4-catalog-mobile-pilot.md`.
V3 remains the pre-cutover default; the v4 build is an explicit development opt-in.
Alec clarified that the cutover is one-way: a v4-populated install does not switch
back to v3. No fallback-only routing or downgrade verification is required.
Imported fallback inside v4 and legacy server support for older releases remain.
No production migration, release build, OTA or deployment occurred in this checkpoint.
