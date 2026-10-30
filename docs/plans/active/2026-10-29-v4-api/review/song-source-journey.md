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
   cross-artist composition catalog.
3. Open [matched files](http://127.0.0.1:43823/api/v4/songs/song_62mhvjyd/tracks).
   Each item contains a complete `track` and `matching_part_ordinals`. A medley
   can contain the song more than once, but its file appears once. Count files,
   not matching parts. The track retains its imported title and all normalized
   parts, including unresolved parts of a partial interpretation.
4. Follow an item's `track.source_id` to `/api/v4/sources/{sourceId}` and
   `track.id` to `/api/v4/source-tracks/{trackId}`. The returned file, exact
   show/source/set parents, playback URLs, and normalization should match.
5. Open [matched shows](http://127.0.0.1:43823/api/v4/songs/song_62mhvjyd/shows).
   A show appears once even if several recordings/files match. Follow its ID to
   `/api/v4/shows/{showId}` and compare the playable hierarchy.
6. Open [the imported song](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/imported-songs/imported_song_3km7ryr3hc5dm877wmc69zpdb5).
   `setlist_show_count` matches this resource's complete `shows` list. It describes
   imported setlist evidence and may differ from the canonical recording count.
   A setlist association alone never invents a source or playable file.

The current snapshot returns 3,355 Scarlet Begonias files across 332 shows;
the imported setlist row returns 315 catalog shows. Saved examples include the
[canonical resource](responses/song.json), a [complete file match](responses/song-track-match.json),
and a [zero-match song](responses/zero-match-song.json). The
[browse entry excerpt](responses/artist-song-entries-excerpt.json) selects two
entries from the full artist list. The [imported metadata excerpt](responses/imported-song-metadata-excerpt.json)
omits the 315-show array; use the detail URL for the complete response.

## Check incomplete and absent normalization

- [Unmapped imported Jam](http://127.0.0.1:43823/api/v4/artists/artist_3qmp7zjbg1rpebhtyfy43096vj/imported-songs/imported_song_3f21yqt0vvbt6ygnh6ca1rqkhf)
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

The artist song list replaces only that artist's browse membership. Raw inventory
owns its separate imported membership. A song's file/show lists replace only that
query's results; imported detail replaces only its imported show association.
Apply resources, associations, and the ETag in one Realm transaction. A 304 keeps
applied data; fetch or apply failure keeps the last complete local state. No
song browse response implicitly changes favorites or downloaded files.


## Local performance limit

The captured full Grateful Dead song browse took 5.704 seconds. It calculates
all canonical recording counts and imported setlist counts in the request's
snapshot. Scarlet detail took 47 ms, its complete file list 200 ms, and its
show list 77 ms. These are single local observations, not production targets.
Optimize the artist-wide count reads and measure primary capacity before rollout.
This checkpoint exposes their cost; it does not add a count cache or new writer
maintenance protocol.
