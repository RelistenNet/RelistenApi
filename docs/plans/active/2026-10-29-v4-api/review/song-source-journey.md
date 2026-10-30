# Song to playable recording

This review is for Alec and Switz: inspect artist song browsing, follow exact
recording matches to playback, and compare the separate imported setlist view.
Start the [local API](../../../../../local-dev/v4-review/README.md) and open
[Swagger](http://127.0.0.1:43823/api-docs-v4/index.html). Test results and confirmed
capture counts are recorded in [verification](../verification.md), not inferred
from this walkthrough.

## Start with Scarlet Begonias

1. Open [Grateful Dead's songs](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/songs).
   Find `song_62mhvjyd`. Its entry has `kind: "song"` and a `song` child.
   Imported fallbacks have `kind: "imported_song"` and an `imported_song` child.
   Mobile uses this route for every artist; it follows `kind` without branching
   on normalization feature flags.
2. Open [canonical song detail](http://127.0.0.1:43823/api/v4/songs/song_62mhvjyd).
   Its metadata, aliases, `matched_track_count`, and `matched_show_count` must
   agree with the browse child. The song belongs to Grateful Dead, not a global
   cross-artist composition catalog. Detail also returns complete `shows`;
   the browse child stays compact. Resolve `artist_id` through the separately
   loaded artist catalog.
3. Open [matched files](http://127.0.0.1:43823/api/v4/songs/song_62mhvjyd/tracks).
   This response contains song metadata and `tracks`.
   Each `tracks` item contains a complete `track` and `matching_part_ordinals`. A medley
   can contain the song more than once, but its file appears once. Count files,
   not matching parts. The track retains its imported title and all normalized
   parts, including unresolved parts of a partial interpretation.
4. Follow an item's `track.source_id` to `/api/v4/sources/{sourceId}` and
   `track.id` to `/api/v4/source-tracks/{trackId}`. The returned file, exact
   show/source/set parents, playback URLs, and normalization should match.
5. Inspect canonical detail’s `shows`. A show appears once even if several
   recordings/files match. There is no separate `/songs/{id}/shows` route. Follow its ID to
   `/api/v4/shows/{showId}` and compare the playable hierarchy.
6. Open [the imported song](http://127.0.0.1:43823/api/v4/imported-songs/imported_song_3km7ryr3hc5dm877wmc69zpdb5).
   After a completed refresh, `setlist_show_count` matches this resource's complete
   `shows` list. It describes imported setlist evidence and may differ from the
   canonical recording count.
   A setlist association alone never invents a source or playable file.

The current snapshot returns 3,355 Scarlet Begonias files across 332 shows;
the imported setlist row returns 315 catalog shows. Saved examples include the
[canonical resource](responses/song-detail-excerpt.json), a [complete file match](responses/song-track-match.json),
and a [zero-match song](responses/zero-match-song.json). The
[browse entry excerpt](responses/artist-song-entries-excerpt.json) selects two
entries from the full artist list. The [imported metadata excerpt](responses/imported-song-metadata-excerpt.json)
omits the 315-show array; use the detail URL for the complete response.

## Check incomplete and absent normalization

- [Unmapped imported Jam](http://127.0.0.1:43823/api/v4/imported-songs/imported_song_3f21yqt0vvbt6ygnh6ca1rqkhf)
  remains reachable from imported browse fallback. Similar text does not establish
  a canonical mapping; `norm.song_evidence` does.
- [Grateful Dead's full imported inventory](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/imported-songs)
  preserves mapped, unmapped, and unused rows. Canonical browse replacement does
  not delete those imported entities or their setlist collections.
- [Born to Be Alive](http://127.0.0.1:43823/api/v4/songs/song_aaatqpek) is a real
  canonical song to inspect for zero recording coverage. A zero count is valid;
  it does not imply that imported setlists have no appearances.
- [Ryan Adams' song browse](http://127.0.0.1:43823/api/v4/artists/artist_1e72h78badf1zvzkvkzcp7kk2a/songs)
  exercises an artist without approved normalization in the restored catalog.
  It uses the same route and returns imported entries. Existing playback remains
  available without canonical song discovery.

Only resolved parts in active files with valid canonical and recording parents
establish matches. Orphaned files, unresolved links, and wholly inconsistent
interpretations are excluded. Direct canonical reads are approval-gated. Invalid
IDs return 422, unknown or unapproved songs return 404, and broken known canonical
chains return a no-store 503. Regression fixtures test corrections and recovery;
manual review does not edit the restored source data.

## What mobile stores

Canonical detail and browse children upsert a song's discovery metadata and
counts. Compact song references inside track parts update only their four
supplied identity/name/kind fields. They do not reset absent counts or aliases.
Each file still has its existing UUID-backed track identity. Matching ordinals
are query metadata, not new playable identities.

The artist song list replaces only that artist’s browse membership. Raw inventory
owns its separate imported membership. Canonical and imported detail each return
flat resource metadata and complete `shows` in one read
snapshot with one ETag. Mobile upserts these resources and replaces only the
owner’s show membership, including an empty list, in one Realm transaction. Save
the ETag in that transaction and bypass legacy parent-timestamp guards. A change
to show membership must apply even if the parent timestamp did not advance.

File-match detail has no `shows` field and replaces only song-track matches. It
cannot clear song-show membership. Compact browse children contain no `shows` and cannot clear detail membership. A 304 keeps applied
data; fetch or apply failure keeps the last complete local state. Retain shared
show entities, favorites, downloads, and unrelated query memberships. Direct
`song_` IDs use `/songs/{id}`; `imported_song_` IDs use `/imported-songs/{id}`.
Neither requires an additional artist ID or a returned detail URL.


## Stored counts and performance review

The earlier capture took 5.704 seconds for the full Grateful Dead song browse
because it calculated all canonical and imported counts in the request. That
implementation is superseded by the [stored-count foundation](../../2026-10-30-song-counts/README.md).
Current timings and local backfill verification belong in the
[verification log](../verification.md); do not use the earlier measurement as
the stored-read result.

Canonical and imported counters are saved separately from normalization decisions.
GETs read those rows and never recount. File/show discovery still reads current
membership through the shared SQL function. Counts may briefly lag after an
import commits or remain stale after a failed refresh; an explicit repair brings
them back into agreement. Admin song edits refresh atomically. Ordinary repairs
commit one artist at a time under the existing artist lock, and readers take no
count lock. No background scan or new global lock is required.

A count-only correction must invalidate song detail and browse ETags without
changing imported timestamps. Refreshing unchanged source data must preserve
count timestamps and validators. Mobile stores the returned count; its cached
file/show list is not a replacement aggregate.


After migration 16 and the stored-read change, five warm full-body requests gave
medians of 18.751 ms for artist songs, 6.252 ms for imported inventory, and 6.612 ms
for the earlier metadata-only Scarlet detail. These measurements predate the
resource-with-shows contract; full-detail latency must be measured again. The
artist-list response bodies and ETag stayed the same during the stored-count change.
See the [current timing evidence](../verification.md#stored-song-counts-and-latency-2026-10-30)
for ranges and cold-start limits. The [foundation PR](https://github.com/RelistenNet/RelistenApi/pull/111)
and [admin PR](https://github.com/RelistenNet/relisten-admin/pull/3) own maintenance.
