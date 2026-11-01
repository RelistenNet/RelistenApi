# Contracts the implementer must preserve

This document resolves the design's open contract choices. Paths are relative
to the catalog host unless explicitly labeled account service. DTO/code names
are proposed target names; public behavior below is required.

## Public IDs and C# boundaries

Use the [approved catalog/account prefix table](../../../design/2026-10-29-v4-api/prefixes.md).
UUID-backed catalog types hold the original Guid, never a new or truncated
hash. Account identity types instead hold canonical strings, as specified below.
The codec encodes canonical UUID byte order; it accepts legacy 128-bit values
without changing UUID version/variant bits. Validate the whole expected prefix,
suffix alphabet/length, and 128-bit overflow. Split prefix/suffix at the last
underscore. Reject uppercase, ambiguous characters and wrong resource kinds.

`Relisten.Identifiers` owns the fixed catalog/account prefix registries, codec,
UUID-backed catalog types and string-backed account types. It has no HTTP,
database, Temporal or AI dependencies. Hosts own JSON
converters, route/request validation and SQL binding. Existing normalized
`VenueId`/`SongId`/`ArtistVenueId` and their Temporal strings stay in
`Relisten.Normalization`;
reuse shared short-format validation rather than copying a second definition.
Accounts reference this small identifier library, not the normalization worker.

Use Newtonsoft string converters for catalog v4 and System.Text.Json converters
for accounts. OpenAPI describes string values with prefix-specific patterns,
not `format: uuid` or record-struct objects. Public catalog references such as
`artist_id`, `show_id`, `source_id` and `track_id` contain typed strings. Provider
names/IDs, eras, positions and protocol tokens retain their documented formats.
Account application identities use the six approved account prefixes and native
string storage described below. A review's parent source ID is obtained
by joining to the source UUID, not by encoding its numeric foreign key.

## Native application interfaces and legacy isolation

The maintained application code lives in `RelistenApi/Services/Catalog/`, with
core result models that do not inherit `RelistenApi.Models` legacy DTOs. Readers
accept one `CatalogReadSession`; commands accept typed domain inputs. The session
owns one connection/transaction and is disposed before serialization. Shape
conversion stays in `Models/Api/V4`; existing legacy services retain their own serializers.
Reuse the same resource/track projection across endpoints. Add query-specific
result models only where ownership or represented data differs; avoid a chain
of one-for-one model copies or a separate reader stack per route.

Native result models retain numeric keys only where database joins need them.
Public v4 DTOs expose typed identities. Existing v2/v3 services keep their own
numeric/UUID models and serializers; their routes are not migrated to the v4 core.

V4 readers use primary read-only snapshots and apply approved normalization with
raw fallback where required. Legacy services retain their existing prefer-replica
reads, Redis popularity and response caches. A legacy cache hit must not trigger
native v4 catalog materialization. There are no compatibility modes, legacy
ordering switches or legacy rollout flags in the v4 reader.

V4 does not wrap legacy services or JSON, and old routes do not impose behavior
on new readers. Share storage helpers only when their semantics are naturally
identical and sharing simplifies maintenance. Independent query implementations
are acceptable. Database/schema changes must remain compatible with supported
legacy readers until those clients are retired. Saved-reference resolution and
the mobile ID adapter remain required; they are separate from server API shims.

Operator artist writes and import commands retain their existing v2 routes and
authorization. They are listed separately in the endpoint manifest; no v4
copies or new command framework are required. The separate relisten-admin app
uses direct Postgres actions, while the older built-in admin uses these v2 APIs.
Their request targeting, job handles, and ingestion engine rules remain unchanged.

## Response, validation and read freshness

Ordinary reads return the resource object or list directly. There is no generic
`data`/`included`/`meta` envelope and no body `contract_version`, `scope`,
`revision`, or `complete` fields. `/api/v4` identifies the catalog contract;
HTTP ETag is the cache validator. For example:

```json
{
  "id": "artist_<lossless-UUID-suffix>",
  "name": "Artist name"
}
```

A list endpoint returns an array of those resources or its documented browse
entries. Search and grouped ranking endpoints retain their meaningful named
result groups. Browse artist years, year shows and recording details through
their own endpoints; there is no full artist bootstrap payload.

Sync replacement is a property of each endpoint, not a server-supplied boolean.
Every endpoint in [endpoints.json](endpoints.json) declares its response shape
and `sync_policy`; the manifest also defines each policy. Mobile dispatches by
that route contract and the request it made. It computes its local request key
from the endpoint and normalized filters. No returned scope identifier is needed.

- Full artist collection routes replace only that artist/query's association
  list. Bootstrap replaces only its named artist-owned collections. Its
  `artist_venues`, `venues`, and `songs` arrays upsert entities; `venue_entries`
  owns the visible artist-venue query membership. An absent entity patch does
  not delete a record referenced by another cached query or offline playback.
- Full show/source/set detail replaces active source/set/track associations only
  for the fully returned owners. Keep entity records needed for offline playback,
  saved references and other query results. Missing shallow parent fields do not
  clear previously stored detail.
- Search, recent/top, anniversary and other filtered/limited catalog endpoints
  replace only their exact cached query result. An absent result never implies
  that the resource is gone from another list or the catalog.
- Popular/trending/momentum responses are transient online ranking views. Do not
  apply their context-dependent scores or membership to the persisted catalog.
  Open ordinary resource details when catalog apply is needed.
- Ordinary singleton reads upsert the returned resource/fields. History applies
  its documented event-sequence window/append behavior. Commands change only what
  their explicit result says changed.
- The specialized resolver returns `references` with per-request availability/
  redirects and `entities` grouped by type. Those entity arrays are patches; the
  resolver never establishes collection replacement or removes favorite membership.
- Account library snapshots/deltas keep their own membership revision/cursor
  protocol; removing catalog `meta` does not remove those domain guarantees.

First release: artist collections/bootstrap are unpaginated. Do not silently
truncate them. If measurements require pagination, design an endpoint-specific
cursor and replacement contract before release rather than reintroducing generic
`complete` metadata. Failed/partial fetches never establish absence.

Use one primary READ ONLY REPEATABLE READ transaction per v4 read request. V4 SQL
receives the same open connection/transaction explicitly. Deterministically
order arrays with ID tie-breakers; materialize, dispose the transaction, then
serialize the final resource/list bytes and hash them for a weak HTTP ETag
(`W/"<hash>"`). There is one validator in the response header, with no duplicate hash in the body and
no volatile response-time field. ETag is an equality token, not an ordered
catalog generation.

Ordinary public GET: `Cache-Control: public, no-cache` and weak ETag; revalidate
before reuse. A matching `If-None-Match` yields 304 after reading current data.
Random, errors, commands, resolver POST and account responses use no-store.
Cloudflare owns downstream response caching. There is no v4 application response
cache or immutable max-age policy in this release. Scores are read from Postgres
in the catalog snapshot; calculations still publish to both Postgres and Redis
for the existing legacy readers.

Hash the uncompressed deterministic JSON; a weak validator avoids tying this
mechanism to host/ingress compression. Conditional GET uses weak comparison,
as specified by [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.2).
Retain `Vary: Accept-Encoding` where responses are compressed. The v4 public CORS
policy permits `If-None-Match` and exposes `ETag` to browser readers on 200/304.
Do not use these validators for write concurrency or range requests.

This guarantees one database snapshot and detects changed represented data.
It does not establish a total order across independent queries or make a whole
normalization workflow atomic. Mobile ignores superseded requests for the same
scope and revalidates stale views. Initial primary routing for v4 avoids replica lag;
adding replicas or an origin response cache requires an explicit consistency
design and measurements, not a future switch of connection strings alone.

Use ProblemDetails with a stable `code` for failures. Resolver POST uses 400 for
malformed JSON; play reporting uses 422 for invalid JSON or fields, as specified
below. Other semantic request/prefix/kind/filter errors use 422; use 404 for a valid
unknown resource, 410 for a known retired canonical ID, 413 for body bounds,
and 503 for disabled service/read timeout/dependency failure. Authorization
keeps its current 401/403 semantics. Resolver item unavailability is data in a
200 batch response. No partial successful collection is returned after SQL failure.

## Core browsing before account work

Alec expanded C1 to review navigation before auth or mobile work. These reads
are now part of the local checkpoint:

- `GET /api/v4/artists` and `/artists/{artistId}` return artist identity,
  counts, features and provider assignments. `features.normalization`
  means approved normalized reads; it does not claim complete coverage. Provider
  names stay open strings, including unknown imported providers. Numeric feature
  and provider-row IDs are not public identities. `featured` retains the imported
  bitmask (1 = featured, 2 = autocreated; bits can combine).
- Artist listing keeps featured/name order with ID tie-breakers. The default
  `include_autocreated=false` excludes autocreated artists. `true` includes
  artists with shows or sources, matching the existing filter semantics.
- `GET /artists/{artistId}/years` returns chronological year resources. A year
  has a typed `year_` ID; its existing `year` label remains a string.
- `GET /artists/{artistId}/years/{yearId}` and `/years/by-number/{year}` return
  the same flat year fields plus `shows`. Calendar lookup accepts integers from
  1 through 9999. Both lookups enforce artist ownership.
- `GET /artists/{artistId}/shows` and a year's `shows` return chronological
  `ShowSummaryResource` rows. They contain typed parents and the same venue
  metadata/display policy as full show detail, with no `sources` field. Follow
  the show ID to retrieve the complete recording hierarchy. Lists never claim
  to have fetched an empty source collection.

Unknown artists/years return 404. Existing artists with no years/shows return
`[]`. Counts come from the server catalog; full collections are unpaginated.
All reads use the same snapshot and ETag rules as other v4 reads. Metadata for
show lists is batched, including normalized venue links and aliases. Legacy
routes retain their response contracts. Search and listening rankings now use
these v4 resources as described below. Operator/import and live-activity routes
remain outside this local catalog checkpoint.

## Artist approval, show venues and raw fallback

For artist-specific reads, check `features.norm_catalogue` in the same snapshot.
False/missing means imported venue/song browsing and explicit disabled track
normalization. `norm_venues` is not a read switch. With approval but incomplete
results, use available accepted links and preserve raw fallback for the rest.

Show DTOs preserve imported scalar metadata and parent links. They expose
`imported_venue` and nullable `artist_venue`; the old flat `show.venue` field is
removed. There is no separate display projection. `ArtistVenueResource` contains:

- `id`: the stored `artist_venue_` identity with an eight-character suffix;
- `artist_id`: the UUID-backed public artist ID;
- `artist_show_count`: the stored count for this artist and canonical venue;
- `created_at` and `updated_at`: the relationship row's timestamps;
- `venue`: the shared `VenueResource`, including `global_show_count`.

`VenueResource` and `SongResource` name canonical public resources. Imported
resources retain the explicit `ImportedVenueResource`/`ImportedSongResource`
names. Internal normalization storage remains in the `norm` schema.

A venue includes `names`: `{ name, kind }` entries. Kind is `official`, `short`,
or `spelling`. These are aliases for discovery and details, not a timeline.
Deduplicate exact `(name, kind)` pairs across stored periods and retained names
from merged venues. Return them in deterministic kind/name order. V4 does not
publish venue-name date ranges or require clients to select a name for a show date.

Show UI renders the imported venue's name and locality. If the imported venue is
absent, it may fall back to `artist_venue.venue` metadata. Link that label through
`artist_venue.venue.id` for canonical details and global favorites. A normalized
correction must not replace the imported display label. One shared venue retains
one identity, alias collection, and global count across artist and year queries.

Resolve the show assignment through `norm.show_venues`, following venue merge
chains, then look up the stored artist/canonical-venue pair. Do not assume a shared
imported row maps every show to the same place. If the assignment or stored pair
is missing, return `artist_venue: null` and retain imported fallback. Never invent
a relationship ID or refresh counts during a read. Keep a source's own imported
venue observation even when its show's assignment differs.

### Count ownership and freshness

Every `ImportedVenueResource` includes `show_count`, including those embedded in
shows, sources, and browse entries. It counts all show-level assignments to that
imported venue for its artist. A source's own venue observation does not add a
show to the count, so a source-only imported venue can have `show_count: 0`.
Store this full count on the imported entity; it has the same meaning wherever
that entity appears.

`artist_show_count` comes from `norm.artist_venues`. It counts the artist's shows
at the canonical venue across all years, not just the current response's shows.
`global_show_count` sums those stored counts across artists whose
`features.norm_catalogue` read flag is enabled. It is the same value wherever the
shared venue appears, including an artist-filtered or year-filtered response.
Disabled artists do not contribute to that public count or the global show list.
Global venue metadata remains resolvable with a zero global count when no approved
artist contributes shows.

The [artist-venue foundation](../2026-10-30-artist-venues/README.md) owns count
maintenance. Admin edits refresh atomically; imports use explicit post-write
hooks. Stored counts can lag current show membership after an interrupted hook or
out-of-band repair. A read uses the stored values and performs no repair. Count
corrections, approval changes that alter a global count, and represented alias
changes alter payload ETags even when imported timestamps are unchanged. An
unchanged refresh does not change the resource; date-only alias edits remain
outside the represented contract.

Canonical artist lists use positive stored relationships. Zero-count rows retain
their IDs in storage while their parents exist and can be returned by artist detail.
Imported browse entries keep shows without an available approved relationship
reachable. Each imported entry represents its full imported show collection;
with partial normalization, it can overlap canonical entries.
Never derive these counts from mobile's partial show cache or sum imported venue
counters. One imported row can contribute shows to several canonical places.

### Implemented venue collections and source detail

`GET /artists/{artistId}/venues` returns `ArtistVenueEntryResource[]`. Each entry
has `kind` (`artist_venue` or `imported_venue`), nullable `artist_venue`, nullable
`imported_venue`; exactly one target is populated. A canonical target is an
`ArtistVenueResource`. An imported target includes `show_count`, counting all
shows with that imported venue assignment for the artist. Include an imported
entry if at least one of its shows lacks an available approved relationship.
Opening that entry returns its complete imported collection, including any shows
also represented by canonical entries. Its count matches imported list and
detail routes. The wrapper owns no venue identity or count. Sort by target name,
then ID.

Clients use this list for every artist; the server applies approval and partial
coverage. Follow `artist_venue.id` to `/artist-venues/{artistVenueId}` and
`imported_venue.id` to `/imported-venues/{importedVenueId}`. A shared `venue_` ID
opens `/venues/{venueId}`. Artist feature flags do not select the client route.
IDs are sufficient to select these fixed routes; responses add no `detail_url`.

All three venue detail resources return their metadata directly, plus complete
`shows` arrays:

- `GET /artist-venues/{artistVenueId}` returns `ArtistVenueWithShowsResource`.
  Its metadata matches the compact `artist_venue` browse child. Its `shows`
  contains that artist's approved assignments to the canonical place.
- `GET /artists/{artistId}/venues/{venueId}` is a pair lookup returning the same
  full relationship detail. It resolves merged venue IDs before looking up the
  pair. Both lookups return 404 for missing or disabled relationships. A retained
  zero-count relationship returns 200 with `shows: []`.
- `GET /venues/{venueId}` returns `VenueWithShowsResource`, resolving a merged
  ID to its survivor. Its `shows` spans approved assignments across artists.
  The resource remains readable with `shows: []` when no approved artist
  contributes shows. Use artist-venue detail for artist-scoped membership.
- `GET /imported-venues/{importedVenueId}` returns the imported fields,
  `show_count`, and complete `shows`. Membership uses `shows.artist_id` and
  `shows.venue_id`; a source's different venue does not add a show. It includes
  shows with canonical links too. No artist parameter is needed to retrieve a
  globally unique imported ID. Imported detail remains available without
  normalization and returns `shows: []` for an unused row.

`GET /artists/{artistId}/imported-venues` remains the compact full inventory,
including unused rows and rows fully covered by normalization. Browse, embedded,
and inventory resources do not contain detail `shows`.

Venue and song details reference artists by `artist_id`; they do not repeat artist
resources. Mobile holds the artist catalog separately in memory and persistence.
Load `/artists?include_autocreated=true` during artist bootstrap: unlike the
default list, it includes autocreated artists with shows or sources, covering
artists referenced by returned show collections. Artist metadata has its own
resource/list ETag and refresh lifecycle. The detail ETag covers its represented
metadata and membership, not changes to separately cached artist metadata.

Read each detail’s metadata and membership in one repeatable-read session.
Separate bare venue `/shows` routes are removed. Wrong-kind or malformed IDs
return 422; valid but unknown IDs return 404.

`GET /sources/{sourceId}` returns the same `SourceResource` as full show detail.
Load only its sets, active non-orphaned tracks, links, and own imported venue in
the request snapshot. Preserve exact show/artist/set parents and reuse the common
track normalization projection. Sibling sources are outside this resource's
association replacement scope. Unknown source IDs return 404; malformed or
wrong-kind IDs return 422. All these reads use the existing payload ETag and
dependency-failure contract.

## Track and normalized song contracts

Every returned track object, including nested full source/show, resolver, history
and discovery objects, includes:

```json
{
  "id": "track_<lossless-UUID-suffix>",
  "source_id": "source_<lossless-UUID-suffix>",
  "show_id": "show_<lossless-UUID-suffix>",
  "artist_id": "artist_<lossless-UUID-suffix>",
  "title": "Original imported title",
  "normalization": {
    "state": "partial",
    "reason": null,
    "parts": [
      {
        "ordinal": 1,
        "text": "Original part text",
        "state": "matched",
        "song": {
          "id": "song_7hd2pw4e",
          "artist_id": "artist_<suffix>",
          "name": "Canonical song",
          "kind": "song"
        },
        "qualifiers": [],
        "notes": [],
        "segue_after": false
      },
      {
        "ordinal": 2,
        "text": "Unresolved part text",
        "state": "unresolved",
        "song": null,
        "qualifiers": [],
        "notes": [],
        "segue_after": false
      }
    ]
  }
}
```

The example omits existing playback fields for space. Retain duration, file URLs,
checksums, slug, set/track order and other non-ID playback metadata. Never change
the imported `title` to a normalized composition. `normalization.state` is
`unavailable`, `partial`, or `interpreted`: unavailable publishes an empty
parts array; partial has at least one unresolved part; interpreted means every
part is resolved, not that the inference is infallible. Unavailable reason is
`disabled`, `not_interpreted`, or `inconsistent` for a corrupt canonical link;
absence of rows does not prove a job never ran.

V4 uses typed string enums for the closed state/reason, part-state, qualifier,
song/venue-kind, coordinate-precision and FLAC vocabularies. OpenAPI lists their
allowed values from the same types used to serialize responses. Nullable values
remain null; notes and provider labels remain open text. Normalization owns
the shared [domain enums](../../../../Relisten.Normalization/CatalogueVocabulary.cs)
and their [SQL/JSON token contract](../../../../Relisten.Normalization/README.md).
V4 consumes those types directly. API response states and recording FLAC values
remain in [CatalogEnums.cs](../../../../RelistenApi/Models/Api/V4/CatalogEnums.cs).

Part state is `matched` for a resolved canonical song link, `non_song` for
resolved content with no song, or `unresolved` when `resolved=false`. Unresolved
links do not establish playable song matches. Preserve part text, qualifiers,
notes and boolean segue; no audio offset or stable occurrence ID is implied.
Read compact canonical song data in the same batch/snapshot, avoiding an N+1
HTTP/SQL query per part. Preserve the whole ordered interpretation on mobile
as one replacement list; do not turn ordinals into persistent file identities.

### Song browsing and recording discovery

`GET /artists/{artistId}/songs` is the browse list for every artist. Each
`ArtistSongEntryResource` has `kind` (`song` or `imported_song`), nullable `song`,
and nullable `imported_song`; exactly one target is populated. The server applies
`features.norm_catalogue` and partial coverage. Clients follow the returned kind
without selecting a list endpoint from feature flags.

For approved artists, include canonical root songs and imported entries without
a valid `norm.song_evidence` mapping to that artist's surviving song. An imported
title alone never establishes a mapping. Invalid merge chains retain imported
fallback entries. Include canonical songs with zero recording matches. For
unapproved artists, return imported entries only. The full
`GET /artists/{artistId}/imported-songs` inventory preserves mapped and unmapped
rows, including unused songs, even when canonical entries replace them in the
browse list.

`GET /songs/{songId}` returns `SongWithShowsResource` directly:

- `id`, `artist_id`, `name`, and nullable `kind` identify the surviving song;
- `slug` and nullable `original_artist` retain canonical metadata;
- `aliases` contains distinct alias strings and merged predecessors' names in
  stable order. Song aliases have no alias-kind field in the normalization schema;
- `matched_track_count` and `matched_show_count` are the stored recording counts;
- `shows` is the complete, deduplicated recording-show membership.

The `song` child in a canonical browse entry contains the same metadata and
counts, without detail collections. Track parts retain the compact `SongResource`
containing only `id`, `artist_id`, `name`, and `kind`. A compact reference does not
clear previously stored song metadata or set absent counts to zero.

`GET /songs/{songId}/tracks` returns song metadata and `tracks: TrackResource[]`.
It has no `shows` property. Each item is the complete existing `TrackResource`,
identical to its direct track read. It preserves the imported playback title,
file URLs, exact artist/show/source/set parents, and full ordered normalization.
Return each playable file once even when the song occurs in several parts.

To highlight the song’s matching parts, select `track.normalization.parts` where
`state == "matched"` and `song.id` equals the returned root song `id`. Use that
canonical returned ID because a requested merged song ID can resolve to its
survivor. The existing parts already supply their ordinals, text, and song
references; no per-track match wrapper or duplicated ordinal list is needed.
Parts do not establish new file identities or audio offsets. This separate
detail avoids loading thousands of tracks when a client only needs song metadata
and shows.

Discovery membership requires a resolved song part, a valid artist-owned
canonical chain, an active non-orphaned track, and consistent source/set/show/
artist parents. Unresolved and non-song parts cannot establish a match. A partial
track can match through its resolved parts; a track whose complete interpretation
is inconsistent is excluded. Counts use this same recording membership at refresh
time; discovery reads current membership from the shared
`norm.song_recording_matches` function.
The canonical detail’s `shows` contains distinct summaries from those recording
matches, ordered by show date and identity. It excludes shows known only through
imported setlists. There is no separate canonical song `/shows` route.

Both canonical song routes resolve a merged ID to its survivor. Unknown IDs
or a song owned by an artist without approved reads return 404. Malformed or
wrong-kind IDs return 422. A known approved song with an invalid canonical chain
returns a no-store 503 problem; correcting that chain restores normal reads.

### Imported songs and setlist membership

An `ImportedSongResource` contains `id`, `artist_id`, `name`, `slug`,
`upstream_identifier`, `created_at`, `updated_at`, and `setlist_show_count`.
`GET /imported-songs/{importedSongId}` returns those fields plus `shows`, the
complete setlist-supported catalog show-summary collection. Imported IDs need no
artist scope for lookup. Unknown IDs return 404; known songs with no catalog show
matches return `shows: []`. Stored counts can lag membership until refresh.

`setlist_show_count` counts distinct **catalog show identities** supported by
`setlist_songs_plays → setlist_shows`, matched by artist and date. Multiple
imported setlists on one date do not duplicate a catalog show. Distinct catalog
show IDs on that date remain distinct. A setlist-only date with no catalog show
is outside this collection and count. After a successful refresh, the stored
count matches the detail's show list. The count has the same meaning in raw
inventory, imported detail, and imported browse entries.

Imported evidence may map song identity, but must not fabricate source/track
matches or join the canonical recording-show count. Imported detail retains its
setlist-derived show list even if normalized recording matches differ.

### Stored song counts and refresh ownership

Song reads do not aggregate recording or imported setlist membership to build
counts. Canonical `matched_track_count` and `matched_show_count` come from
`norm.song_counts`; imported `setlist_show_count` comes from
`public.setlist_song_counts`. Each count row belongs to its song through a
cascading foreign key. It has no separate public identity. A missing row reads as
zero until its first refresh. Counts stay separate from the audited decisions in
`norm.songs`; recounting does not create a normalization edit or change imported
song timestamps.

The [song-count foundation](../2026-10-30-song-counts/README.md) owns maintenance.
`norm.refresh_song_counts(artist_uuid)` recomputes one artist's canonical and
imported counts atomically. It uses the same recording-membership function as
discovery and updates timestamps only when counts change, including transitions
to zero. It reuses the existing artist-scoped `norm-catalogue` lock. Readers take
no count lock, and there is no global gate, schedule, queue, or per-row source
trigger. `SongCounts.RefreshAllAsync` commits each artist separately rather than
holding locks across a full repair.

Admin song edits refresh in the source transaction, so refresh failure rolls
back the edit. Imports/rebuilds and normalization refresh after their source
work commits. During that gap, or after a failed hook or out-of-band SQL edit,
stored counts can lag the live track/show lists. A GET never repairs them.
Explicit refresh restores agreement. A count-only correction changes payload
ETags even when imported timestamps and track interpretations are unchanged.
Mobile accepts the returned count and does not replace it with a partial local
list length. For compact browse responses, a 304 validates the represented stored count
and metadata; it does not establish fresh recording membership. Detail ETags
also validate the live membership returned in that response. Its stored counts
can independently lag that membership.

These five song routes use direct responses and the existing failure contracts.
Each detail reads its metadata and collection in one
repeatable-read session, covered by one payload ETag. Artist lists replace only
their own browse/inventory membership. Canonical and imported detail replace only
their respective song-show associations, including an empty list. File-match
detail replaces only that song’s track-match membership and never clears shows.
Upsert shows and tracks and save the ETag in the same mobile
transaction. Retain favorites, offline files, shared entities, and other queries.
A 304 keeps the previously applied snapshot; failed fetch/apply keeps the last
complete state. Bypass legacy parent-timestamp guards: changed membership can
change the ETag while the parent’s imported timestamp remains unchanged.

The implementation separates
[canonical resolution](../../../../RelistenApi/Services/Catalog/CatalogSongMetadataReader.cs),
[recording membership and counts](../../../../RelistenApi/Services/Catalog/CatalogSongMatchReader.cs),
[shared track interpretation](../../../../RelistenApi/Services/Catalog/CatalogTrackNormalizationReader.cs),
and [song navigation](../../../../RelistenApi/Services/Catalog/CatalogSongReader.cs).

## Tours, eras, and source reviews

`GET /artists/{artistId}/tours` returns the artist’s complete compact
`TourResource[]`, ordered by `start_date` ascending with nulls last, then tour ID.
Each tour has `id`, `artist_id`, nullable `start_date` and `end_date`, `name`,
`slug`, `upstream_identifier`, `created_at`, `updated_at`, and `show_count`.
`GET /tours/{tourId}` accepts its globally unique typed ID directly and returns
that metadata plus complete `shows`, ordered by imported display date and show ID.
It needs no artist path parameter. Artist and show references use the separately
cached artist catalog. Compact list and show-embedded tours omit `shows`.

Tour `show_count` counts catalog shows assigned to both that tour and its owning
artist. It has the same meaning in list, embedded, and detail responses; the full
detail’s show membership uses those same assignments. It does not count imported
setlist appearances. The legacy API’s `shows_on_tour` retains its old meaning.
No new stored counter or refresh process is needed for this count.

`GET /artists/{artistId}/eras` returns complete ordered `EraResource[]` metadata.
An era has only `order` and `name`, matching the era embedded in show responses.
It has no public ID, prefix, or independent detail route. Order is configured era
order, then name and internal identity to break ties. Mobile replaces the artist’s
ordered era metadata as a whole; it does not create shared era entities.

`GET /sources/{sourceId}/reviews` returns the complete `SourceReviewResource[]`
for one source. Each review has `id` (`source_review_`), `source_id` (`source_`),
nullable `rating`, `title`, and `author`, review text in `review`, and `created_at`
and `updated_at`. Order is `updated_at` descending, then review ID. Imported review
text and rating retain their upstream meaning; they are not normalization evidence.

Each request reads in one repeatable-read session. Payload ETags cover the whole
returned resource or list, including membership and tour counts. A known artist
or source with no children returns 200 with `[]`; a known empty tour detail returns
`shows: []`. Missing parents return 404, malformed or wrong-kind IDs return 422,
and dependency failures return no-store 503. A matching ETag returns 304.

Tour inventory replaces only that artist’s tour membership. Tour detail replaces
only that tour’s show membership. Reviews replace only that source’s review
membership. Each complete empty list clears its owned membership. Apply resources,
owned membership, and the ETag atomically; preserve shared shows, offline files,
favorites, and other owners’ memberships. Mobile runtime changes remain pending.

Source sets remain embedded in full source/show responses with their typed IDs
and tracks. There is deliberately no direct `/source-sets/{sourceSetId}` endpoint;
clients fetch the owning source to inspect a complete set.

## Load collections as needed

There is no combined artist-catalog endpoint. Fetch `/artists` for cached artist
metadata, `/artists/{artistId}/years` for year navigation, and
`/artists/{artistId}/years/{yearId}` (or `/years/by-number/{year}`) for the selected
year and its complete `shows` list. Fetch show/source/track detail on demand.
Venue, song, tour, era and imported inventories have separate list routes; load
them when the relevant screen or explicit sync operation needs them. Do not
recreate an eager artist-wide download by calling every list during navigation.

Each complete list replaces only its endpoint's membership, including an empty
list. Year detail replaces only that year's show membership. Upsert returned
entities and supplied related fields, retaining unrelated memberships, richer
recording detail, downloads and personal data. Persist each endpoint's ETag with
its successful apply; a failed fetch/apply retains that endpoint's previous
state. This requires no global artist revision or combined-response validator.

## Canonical redirects and retirement

Follow `merged_into` to the survivor with bounded cycle detection. Direct lookup
returns the survivor resource and ID. The resolver explicitly echoes the requested
reference and target. Song merges cannot cross artist ownership. Broken chains
never hang reads: embedded projections preserve imported fallback; the resolver
reports `unavailable` with `normalization_unavailable` for that reference.

Migration 18, [017_public_ids.sql](../../../../Relisten.Normalization/Sql/017_public_ids.sql),
creates `norm.public_ids` for canonical `song` and `venue` IDs: `id` is the primary
key, with `kind`, `last_name`, and nullable `retired_at`. It backfills current rows
and known historical deletions retained in `norm.changes`. The migration locks
canonical writes while installing the backfill and triggers; ordinary reads remain
available. Production deployment and capacity are separate rollout checks.

Two `AFTER INSERT OR DELETE` triggers reserve or retire the ID in the same source
transaction. Using `AFTER INSERT` preserves ordinary `INSERT ... ON CONFLICT`
updates: only an actual insertion attempts a new reservation. A collision still
rolls back the complete statement before commit. Both insertion and deletion
acquire the canonical row before its reservation, keeping the same lock order.
Ordinary random minting cannot reuse an active or retired public ID.

A deletion records the last name and retirement timestamp without removing the
reservation. An explicit human restore/revert may reactivate the same ID under
the existing validated restore context; ordinary minting cannot. Rollback leaves
both canonical row and reservation unchanged. There are no new canonical-row
columns or rewritten historical row images, and no global writer lock in normal
operation. Keep reservations and triggers when rolling back application code;
dropping them would reopen saved-ID reuse.

Direct canonical lookup of a known retired ID returns 410. Resolver returns
`retired` with no target and does not change personal membership. A missing ID
without retirement evidence is `unavailable`, not assumed retired. Historical
unlogged or pruned deletions cannot be recovered from this migration. A normalized
row’s `retired_uuid` is its former UUID identity, not a retirement indicator;
`legacy_uuid` still looks up an existing row. After that row is deleted, a UUID
alone cannot identify its public-ID tombstone and resolves as unavailable.

## Resolver and operator inputs

`POST /api/v4/catalog/resolve` accepts JSON
`{ "references": [{ "kind": "track", "scheme": "v4", "id": "track_...", "artist_id": "artist_..." }] }`.
The batch must contain 1–500 references and fit within 128 KiB. Each item supplies
`kind`, `scheme`, `id`, and optional artist context. This POST is a read-only
lookup with `Cache-Control: no-store`, no ETag, and no 304 behavior. All lookups
and entity hydration share one read session. A database failure returns a no-store
503 for the request, with no partial batch response.

The 14 kinds are `artist`, `show`, `source`, `source_set`, `track`, `year`, `tour`,
`imported_venue`, `imported_song`, `setlist_show`, `source_review`, `venue`, `song`,
and `artist_venue`. Supported schemes follow the fixed
[lookup map](../../../../RelistenApi/Services/Catalog/CatalogReferenceLookup.cs):

- `v4`: every kind, using its typed public ID.
- `legacy_uuid`: every kind except `artist_venue`. Canonical songs and venues use
  their retained former UUID on an existing row.
- `legacy_integer`: UUID-backed kinds only; no song, venue, or artist-venue short
  identity accepts this scheme. For years, this means the internal legacy row ID,
  not the calendar year number.
- `legacy_slug`: artist, track, tour, imported venue, imported song, venue, and song.

Artist-owned integer/slug lookup requires typed `artist_id` context. Artist and
venue lookups are global. Optional artist context also restricts available owned
lookups by other schemes. A global song/venue public-ID tombstone can still be
classified retired without owner context. The explicit by-number year route
remains the calendar-year lookup.

OpenAPI publishes closed enums for kind, scheme, and availability. Request tokens
remain strings until semantic validation so unknown enum values, unsupported
scheme/kind pairs, invalid IDs/context, and invalid batch size return 422.
Malformed JSON returns 400 and oversized bodies return 413. Validation runs before
catalog queries; invalid requests do not return partial per-reference results.

Each response has `references` and `entities`. Each reference result repeats the
flat `kind`, `scheme`, `id`, and nullable `artist_id`, followed by `availability`
(`available`, `redirected`, `unavailable`, or `retired`), nullable
`target: { kind, id }`, and nullable diagnostic `reason`. Input order and duplicates
are preserved. Available/redirected targets use current typed IDs; entity arrays
deduplicate by identity in stable order. Unavailable diagnostics include
`not_found`, `ambiguous`, `disabled`, and `normalization_unavailable`.

`entities` has 14 groups: `artists`, `shows`, `sources`, `source_sets`, `tracks`,
`years`, `tours`, `imported_venues`, `imported_songs`, `venues`, `songs`,
`artist_venues`, `source_reviews`, and `setlist_shows`. Required parents are shallow:
show summaries omit sources; source summaries omit sets and links; set summaries
omit tracks. Returned tracks retain complete normalization. Artists are returned
only for explicit artist references because clients cache artists separately.
Source-set IDs can resolve here despite having no direct GET route.

These are upsert-only entity patches. Never replace unreturned child collections,
delete catalog rows, or remove library membership from a resolver result.
Imported identities remain imported, even when normalization exists. There is no
additional imported-to-canonical mapping result: the show and browse
resources already expose approved normalization without assuming a raw venue has
one canonical target. Resolving a saved imported target preserves that exact kind.

V4 query names use snake_case: `artist_ids`, `artist_id`, `previous_days`,
`sort_by`, `delete_old_content`, with other current scalar filters/limits
preserved. `shows/on-date` means month/day anniversary; `shows/by-date` means
exact display date. Default anniversary values use the database session date, preserving current
NOW()/CURRENT_DATE behavior; record its configured timezone at rollout rather
than silently switching to a client-local or new hardcoded clock. Callers can
continue sending explicit month/day. Recent-update sorting remains imported
recording changes.

### Popularity on every show resource

Every `ShowSummaryResource` and full `ShowResource` includes nullable `popularity`.
Ordinary artist/year, date/recent, song/venue/tour, search, full-show, snapshot and
resolver reads use raw artist-local metrics. Artist-specific popular/trending
groups also use artist-local metrics. Global popular/trending show lists use
their global input map; momentum publishes its organic score, falling back to
raw momentum for older inputs. These are the legacy scoring meanings.

`CatalogShowMetadataReader` enriches shows in batches. A ranking supplies its
already-selected metric map; otherwise the reader fetches stored artist-local
rows for all requested show IDs in one SQL batch, checking current artist
ownership. An explicitly supplied empty map never triggers fallback reads. The base show projection publishes the field once, including on ranked
shows. There is no request-scoped score cache or ranking-specific projection copy.
A missing entry stays `null`, independently of normalization approval.

Ranking results are transient online views on mobile. They do not update persisted
show popularity or offline catalog membership. Opening a ranked result loads its
ordinary detail route for catalog apply. The same show may therefore display a
global, artist-local or organic momentum score in different views; the endpoint
defines that context. All nine ranking operations use `transient_ranking` in
[endpoints.json](endpoints.json). Search and other catalog query policies remain
unchanged.

Popularity participates in the represented JSON and ETag. An artist-score refresh
changes ordinary show/list/detail/snapshot validators without requiring imported
`updated_at` changes. Global-ranking validators depend on their global scores,
not unrelated artist-score refreshes. Both also include returned catalog metadata.

Refresh jobs calculate scores and publish to Postgres and legacy Redis. V4 reads
scores with catalog metadata in the same primary repeatable-read snapshot. After
readers dispose that snapshot, one request-scoped collector checks the required
populations' ages in one SQL query and queues at most one refresh batch. Reads
serve stored scores; they never calculate them. Missing metrics remain null and
missing rankings remain empty while the first calculation is queued.

Every current catalog target receives a stored row. Inactive targets have zero
counters, null momentum, and a `generated_at` timestamp. They remain null in
public show resources and are excluded from ranking selection. A population with
no catalog targets does not need a refresh. There is no separate freshness table
or scheduled refresh. The same one-hour age policy is used by legacy maps.

A failed refresh leaves prior values readable. SQL or queue-submission failure
returns 503/no-store without a partial body or ETag; a later request retries.
Concurrent stale requests can enqueue duplicate batches, matching the existing
legacy behavior. Batch retries recalculate even after Postgres became fresh,
because an earlier attempt may have failed during Redis publication. See the
[popularity foundation](../../../popularity/README.md) for ownership and repair.

### Search and listening rankings

These ten operations are implemented. Search keeps six named categories; rankings
return direct resource arrays or the documented artist groups. They have ETags
and `public, no-cache`. Search uses query-result replacement; rankings are
transient online views with no offline catalog apply.

`GET /search` accepts `q`, optional typed `artist_id`, and boolean `artists`,
`shows`, `songs`, `sources`, `tours`, `venues` (all default true). Trim `q`;
fewer than three characters or all categories disabled returns six empty arrays.
Unknown well-formed artist filters return empty results; malformed IDs or query
values return 422. Match literal case-insensitive substrings, including percent
and underscore characters. Each category returns at most 20 hits:

- `artists`: ordinary artist resources, including features, ordered by catalog
  show count descending, then name and ID.
- `shows`: ordinary summaries, ordered by source count descending, then display
  date descending and ID. No nested artist or recording hierarchy.
- `sources`: shallow source summaries, matched on upstream identifier,
  description, taper notes, source, taper, transferrer, or lineage. Order by
  weighted rating, duration, display date descending, then ID; null ratings and
  durations last. Source detail remains lazy.
- `tours`: ordinary compact tour resources, ordered by show count descending,
  start date descending, name and ID.
- `songs`: the existing `kind`/`song`/`imported_song` entries. Approved canonical
  matches include names and inherited aliases. A matched imported label maps to
  its canonical song only with valid same-artist evidence; otherwise retain the
  imported hit. Resolve and deduplicate canonical IDs. Order by each hit's stored
  show count descending, then name and ID.
- `venues`: the existing `kind`/`artist_venue`/`imported_venue` entries. Match
  names, inherited aliases and locality, including imported labels. Prefer valid
  approved artist/place relationships; retain the full imported collection when
  any show needs fallback. Empty imported venues remain searchable. Order by
  artist show count descending, then name and ID. A global search can return
  separate artist relationships at the same shared venue.

Canonical and imported song/venue candidate queries are each bounded to 20 before
resolution and combined ranking. This bounds hydration without loading full artist
inventories; it does not promise exhaustive fuzzy-search relevance. Imported label
mapping may consolidate several candidates into one canonical hit, so fewer than
20 results is valid. Feature approval and merge resolution use the same request
snapshot as the returned catalog metadata. Counts follow the existing resource
readers; materialized song and artist/place counts are never refreshed by GETs.

`GET /popular/artists` and `/trending/artists` return artist resources plus `rank`
and `popularity`. `/popular/years` and `/trending/years` do the same for year
resources. `limit=50` is positive, with no additional v4 cap. Artist/year trending
uses the existing 48-hour/30-day play floors and trend-ratio order; popular uses
30-day hot score then plays. IDs break ties. All artists resolve from the normal
catalog; no separate embedded artist lookup arrays are returned.

`GET /popular/shows` and `/trending/shows` return ordinary show summaries,
with global `popularity` from stored ranking rows, plus `rank`. They accept `limit=25` (nonpositive resets to 25; larger clamps to 25)
and `window=30d`, with exactly `48h`, `7d`, `30d` supported. Invalid windows return
422 rather than silently changing the sort. Hot score and plays in the selected
window determine order; trending also applies existing play floors and trend-ratio
ties. These are listening statistics, separate from the rating-based `/shows/top`.

`GET /artists/{artistId}/shows/popular-trending` returns
`{ artist_id, popular_shows, trending_shows }`, with each list independently limited
as above. A known artist with no activity has two empty lists; unknown paths are
404. `GET /shows/popular-trending` returns a direct array of those groups for
repeated `artist_ids`, sorted by artist ID with duplicates removed. Its per-group
`limit=10` resets/clamps to 10. Empty input stays empty; unknown filter artists
are omitted. This array replaces the legacy outer `artists` wrapper.

`GET /shows/momentum` accepts repeated `artist_ids` and the show limit/reset rules.
Empty input stays empty. Order by organic momentum, raw momentum, 30-day hot
score, 30-day plays, then show ID. Retain the existing raw-score fallback for
selection from older maps. Publish that selected organic/fallback score as
`popularity.momentum_score`, matching the momentum ordering semantics.

`popularity` contains `momentum_score`, `trend_ratio`, and `windows`, whose keys
are `48h`, `7d`, `30d`, each with `plays`, `hours`, `hot_score`. Scores retain
four-decimal rounding. Do not duplicate window counts as top-level fields.
`rank` is one-based result order. Postgres stores derived counters and scores,
not v4 catalog bodies. SQL applies ranking order, thresholds, and limits before
hydration. Artist groups apply their limits independently per artist. All score
and metadata reads share one primary catalog snapshot. Pass selected global or
organic metrics to shared enrichment; ordinary reads use one artist-local SQL
batch. Catalog foreign keys remove deleted ranking targets. Owner-mismatched
hydrated records are omitted. SQL failure returns 503/no-store, with no partial
successful result or validator. Normalization corrections can change metadata
and ETags without a new score calculation.

### Date and recent show browsing

These 13 operations are implemented. All lists contain `ShowSummaryResource`,
with the same imported venue, optional `artist_venue`, and parent typed IDs as
artist/year browsing. Artists remain in the separately cached artist list.

- `/shows/today` and `/artists/{artistId}/shows/today` accept optional `month`
  (1–12) and `day` (1–31). Missing components use `CURRENT_DATE` in the read
  session. `/shows/on-date` and the artist-scoped equivalent require both.
  Match the stored show date across all years; order by `display_date` ascending,
  then show UUID. Explicit invalid calendar pairs return 422; February 29 is valid.
- `/artists/{artistId}/shows/by-date/{showDate}` returns the full show hierarchy
  for that exact imported `display_date`, including partial labels such as
  `1995-12-xx`. It is artist-scoped and has the same response as `/shows/{showId}`.
- `/shows/recently-performed` and `/shows/recently-updated`, and their
  artist-scoped equivalents, accept positive `shows` and `days`. With neither,
  return 25. Without `days`, cap `shows` at 250. With `days`, ignore `shows`, cap
  the window at 90 days, and return all matches strictly after database midnight
  minus that window. Performed uses `shows.date` and descending `display_date`;
  updated uses imported `shows.updated_at` for both filtering and sorting.
  Global routes accept repeated typed `artist_ids` query parameters; omitted or
  empty means all artists. An unknown, well-formed filter ID contributes no matches.
- `/shows/recently-added` and its artist-scoped equivalent accept `limit=20`
  (positive, capped at 200), nonnegative optional `previous_days`, and `sort_by`
  (`created_at` or `updated_at`, default `created_at`). Read stored
  `show_source_information.max_created_at` or `.max_updated_at`, respectively,
  in descending order. With `previous_days`, include timestamps at or after
  database midnight minus that window; the limit still applies. No recording
  aggregate means no match. Windows extending before year 1 saturate at that
  earliest representable catalog timestamp. This differs from imported-show
  update order above.
- `/artists/{artistId}/shows/top?limit=25` returns weighted-source rating order,
  highest first, with null ratings last. `limit` must be positive. All descending
  lists break ties by show UUID ascending for stable ETags.
- `/artists/{artistId}/shows/random` returns one full show hierarchy. Unknown or
  empty artists return 404. The result is `no-store`, has no ETag, and ignores
  conditional cache validators. Ordinary lists and exact-date details use ETags.

Known empty artist lists return `[]`; unknown artist paths return 404. Malformed
IDs and invalid query values return 422 ProblemDetails with `no-store`.
Normalization-only corrections can change returned metadata and ETags, but do
not change imported date/update/rating rank. GETs never refresh stored aggregates.

Mobile replaces only the cached membership of each exact discovery URL/query,
including empty lists, and upserts returned show summaries. A limited or filtered
list never removes shows from other collection/detail memberships or the personal
library. Full exact-date and random responses apply the existing `full_show`
replacement policy to the selected show; random carries no saved validator.

### Anonymous play reporting and public activity

`POST /api/v4/live/play` accepts JSON
`{ "track_id": "track_…", "app_type": "ios" }` with an optional `client_uuid` UUID.
`app_type` is the string enum `ios|web|sonos|android`; numeric enum values are
rejected. Omit `client_uuid` when unavailable. It is a caller-supplied telemetry
UUID, unrelated to authenticated `user_` identities. It remains in telemetry
storage and is never returned by v4. No account login is required.

A successful report returns 200 with
`{ sequence, played_at, track_id, app_type }` after insertion into the existing
`source_track_plays` table. The integer sequence and database timestamp identify
the event; the sequence has no catalog prefix. Invalid JSON, missing/invalid
fields, and wrong ID kinds return 422 ProblemDetails. Unknown, orphaned, or
missing-parent tracks return 404 and write nothing. Database failures return
503. Each accepted POST records a play: retry after a lost response can duplicate
telemetry. There is no new idempotency store or account-history write. Popularity
continues to refresh on ranking/catalog demand; reporting does not enqueue work.

`GET /api/v4/live/history?after_sequence=123&limit=20` returns a direct array of
`{ sequence, played_at, app_type, track, show }`. `track` is the ordinary v4
`TrackResource`, including one `normalization` object. `show` is the ordinary
`ShowSummaryResource`, including imported venue, artist–venue, and artist-local
popularity. Track parent IDs provide exact source/set/show/artist references;
artist metadata comes from the separately held artist catalog. No full source
hierarchy or artist array is repeated. Missing/orphaned recordings and unsupported
legacy app types are omitted.

The window covers events created in the last hour. Without `after_sequence`, take
the latest `limit` events; with it, take the next events whose sequence is greater
than the cursor. Both return sequence ascending, independently of tied or
non-monotonic event timestamps. Default limit is 20, positive limits cap at 2000,
and `after_sequence` must be a nonnegative 32-bit integer. Invalid query values
return 422. Empty results are `[]`. Both endpoints use `Cache-Control: no-store`
and emit no ETag.

`sequence` is the existing `source_track_plays.id`, identifying one play report.
The same track reported twice has two sequences. Poll with the last returned
sequence in `after_sequence`. Gaps are normal. This public global counter can
reveal approximate cumulative insert volume; it is not an opaque cursor or an
exact count of retained plays. The legacy history route exposes the same IDs.

History accepts repeated `artist_ids` query keys with canonical `artist_` IDs,
for example `?artist_ids=artist_…&artist_ids=artist_…&limit=20`. Omit the filter
for all artists. When present, return events belonging to any selected artist;
duplicate IDs do not duplicate events. Empty values and malformed/wrong-kind IDs
return 422. A valid unknown artist or an artist with no recent plays contributes
no events, so a selection with no matches returns `[]` without a separate artist
existence lookup. Apply this scope in the event SQL before LIMIT, for both initial
windows and `after_sequence` polling. Normalization approval does not restrict
artist selection: imported-only artists remain usable.

This preserves best-effort public activity, not a durable change feed: replica
delay, overlapping insert commits, the one-hour window, and removed recordings can
cause omissions. Clients append/deduplicate by sequence when polling; periodically
reload the recent window without a cursor. A full reload replaces only that
window for its selected artists. Changing the artist selection starts a new
window without reusing the previous selection's cursor. It never deletes catalog
records or personal listening history. Account history remains separate.
V2 routes retain their request/response formats and
share telemetry storage; their recent-event selection now also follows sequence
order so cursor paging does not skip a lower sequence with a later timestamp.

Ranking/recent bounds and supported sort/window choices are listed in the
coverage tables; invalid enum values are 422 rather than silently selecting a
different sort.

Artist create/update and import operations remain on their current v2 routes.
Preserve existing operator authorization and the provider API-key endpoint;
ordinary account-service login does not grant operator access. Preserve current
request bodies, targeting, Temporal/Hangfire engine checks, delete/debug
restrictions, and workflow/job handle responses. No provider import is started
to verify this work. Random catalog reads remain no-store.

## Canonical account/auth IDs and the in-place library

Alec approved canonical stored typed strings for first-party account identities,
including auth, and a coordinated tester re-sign-in. Keep `/v1/library`, `/v1/me`
and current auth paths; no `/v2/library` route family or old-client projection.
The breaking private-alpha payload changes in place. Retain `contract_version: 1`
where the current account protocol has it; internal hash, cookie and cursor format
markers change independently. Only the revised payload is accepted after cutover.

### Canonical identity and storage

Account types hold the complete canonical string, with strict constructors and
EF text conversion. Do not hide Guid-backed persistence behind those types.
Use U26 UUIDv7 suffixes for new IDs, not the normalization catalog's S8 suffixes.
Migrate old IDs by encoding their exact UUID bits; do not remint users or memberships.

| Owned identity | Canonical prefix | Storage/reference conversion |
|---|---|---|
| Account user | `user_` | `User.Id` and every application `UserId` FK |
| First-party session | `session_` | Native/identity session PKs and application session references, including `AuthSsoSessionId` |
| Favorite membership | `favorite_` | Favorite PK, receipt submitted/canonical favorite references, change favorite references |
| Favorite operation | `mutation_` | Mutation receipt PK and application operation references |
| Library event | `library_change_` | Library change PK and client change identities |
| Client command | `command_` | Username command receipt PK and client command identities |

Indexes/FKs store these complete values. Preserve every FK/unique constraint,
including the identity-session composite self-FK `(AuthSsoSessionId, UserId)`.
Replace UUID generators/SQL checks only for converted owned IDs, retaining their
existing UUIDv7 requirements inside the typed format. Keep numeric library and
username revisions as counters. Internal surrogate row IDs without public
identity (ExternalIdentity, UsernameHold, LibraryState) may remain UUIDs; their
user/session references still become typed IDs.

Keep OpenIddict's framework application/authorization/scope/token PKs as opaque
Guid keys and its existing `UseOpenIddict<Guid>` configuration. For example,
`NativeSession.AuthorizationId` remains the framework Guid FK. Its authorization
Subject is already a string and carries the canonical `user_` reference. This
boundary avoids renaming framework-owned IDs without a consumer requirement.
Legacy API operator credentials are separate and do not migrate with account users.

Catalog favorite targets also store their canonical typed strings, including
`track_` and the existing `venue_`/`song_` forms. There is no alternate raw UUID
storage representation for imported targets in the revised library. Convert
existing target UUIDs with the prefix selected by their old kind. Map old kinds
`source_track→track`, `song→imported_song`, `venue→imported_venue`. Retain target
uniqueness per user/kind/ID and the current 10,000-active-target quota.

### Public fields

Use typed `user_id` and optional `native_session_id` in `/v1/me`. PATCH uses
`command_id` instead of `client_command_uuid`. Library fields become `mutation_id`,
`favorite_id`, `submitted_favorite_id`, `canonical_favorite_id`, and `change_id`;
all carry their expected prefix. Target fields are `target: { kind, id }`.
Conflict arrays become `conflicting_mutation_ids` / `conflicting_favorite_ids`;
application errors use ID terminology such as `invalid_command_id` and
`favorite_id_conflict`. Do not leak raw owned UUIDs through errors/current-profile
payloads or aliases. Unknown/wrong-prefix IDs are rejected at the boundary.

```json
{
  "contract_version": 1,
  "mutations": [
    {
      "mutation_id": "mutation_<U26>",
      "favorite_id": "favorite_<U26>",
      "target": { "kind": "venue", "id": "venue_k3m9x2qa" },
      "desired_state": "favorite"
    }
  ]
}
```

Allowed favorite kinds are `artist`, `show`, `source`, `track`, `tour`,
`imported_song`, `imported_venue`, `song`, `venue`. Venue membership has no artist
scope. Favorite/mutation/change IDs are distinct from the target's identity.
Accounts validate syntax/kind and exact membership without querying the catalog
during writes. Metadata unavailability never erases membership. Preserve current
batch/page limits and 256-KiB mutation body bound. Snapshots/deltas retain their
membership revision/cursor behavior. Exact-target duplicate/conflict and receipt
replay semantics remain; original receipt hashes are audit data, not recomputed
under the new typed hash domain. Typed operations use fresh mutation IDs.

### Auth and credential boundaries

First-party issued `sub`/`sid` claims carry canonical `user_`/`session_` values.
Update principal factories, authorization/token endpoints, native authorization,
web callbacks and application-session lookups to strict typed-ID parsing. Do not
blindly replace Guid parsing for OpenIddict-owned authorization keys. External
Google/Apple issuer/subject values, registered client IDs, signed token strings,
nonces and PKCE retain their protocol-defined formats and security behavior.

Mobile keeps canonical user/session IDs in credentials, profile snapshots and
account scope keys. Token `sub`/`sid`, `/me`, and the stored credential envelope
must compare equal as typed strings; do not decode back to UUID internally.
Username commands, favorite rows/operations and changes likewise use native
typed IDs in mobile account storage. Imported catalog Realm UUID keys and file
paths are a separate compatibility choice and stay unchanged.

The old session cookie codec embeds UUID text and the old library cursor embeds
16-byte user IDs. Replace their internal format/protection purposes so old
credentials/cursors are not accepted as new ones. Cursor payload binds the full
canonical user ID and revision; invalid cursors recover with 410 and
`/v1/library/snapshot`. Route version does not need to change for a new protected
format. Preserve `/v1/library` browser boundary/CSRF/origin/no-store protections
and test segment safety; there is no new route family to admit.

### Coordinated alpha migration and re-sign-in

Put first-party account/auth admission into maintenance for one coordinated
migration. Preserve users, usernames, external provider links, exact memberships,
revision counters, timestamps and receipt audit history. Build deterministic
old-UUID→typed-ID maps, convert every owned PK/FK and typed catalog target, then
restore validated constraints/indexes. Use a transactionally bounded migration
with writes stopped; do not let old and new binaries mutate mixed schemas.
A migration-local encoder is acceptable only if it matches shared known vectors;
otherwise stage the map with the shared codec before applying the schema changes.

Migrate application-owned IDs in explicit receipt/bootstrap JSON where present;
never regex-rewrite opaque signed/encrypted payloads. Revoke all pre-cutover
native/web/SSO sessions and their linked first-party authorizations/token families.
Update framework Subject references that represent migrated application users;
framework PKs and external provider identities stay unchanged. Bump cookie/cursor
formats and require fresh tester login. Do not implement dual raw-UUID/typed
claim acceptance or try to preserve old refresh credentials.

On mobile, use one durable typed-identity upgrade marker before activating auth.
Clear old auth credential envelopes, cached account profiles, account scopes,
account-local membership caches, cursors/outboxes and pending username/favorite
operations. Re-sign-in refills account membership from the server. Do not clear
anonymous favorites, imported catalog, offline files, queue or playback history.

Recreate anonymous favorite rows under `favorite_` primary keys by encoding their
existing UUID bits and migrate raw target kinds/IDs into canonical catalog IDs. Do not mutate Realm primary keys
in place. For pre-favorites schemas run legacy-flag conversion first, then this
mapping. Preserve anonymous membership; clear/reset affected unfinished anonymous
import receipts to Deferred so discarded operations cannot strand Importing.
Completed imports are not automatically replayed. Any explicit retry after login
uses new typed mutation IDs and the new canonical account scope.

The identity upgrade is restartable across SecureStore and Realm; do not claim
a cross-store atomic transaction. Gate auth while a durable upgrade marker is
pending, clear old credential envelopes idempotently, and transactionally clear
account-local caches/recreate anonymous rows. Mark completion only after both
stores are ready, then permit new login. Failure keeps the gate closed and
retries safely; no new credentials may be cleared by a delayed upgrade retry. Old account-local caches are disposable, server memberships
are not. Never submit an old mutation under a rewritten payload/ID. Historical
receipts keep their original hash bytes; their IDs/FKs convert deterministically.

Favorites remain exact targets after merges. A saved alias may redirect and
coexist with a survivor favorite; each removes its exact stored target. New
favorites use the currently displayed canonical target. Re-favoriting is an
accepted edge case; do not add cross-service alias reconciliation.

## Mobile persistence, transport and UI

Keep one set of Realm models, query APIs and repositories. Transport selection
belongs in the API adapter. Legacy transport retains its cooldown/pseudo-ETag
and timestamp-guarded imported updates. V4 has one authoritative apply coordinator
that bypasses those timestamp guards and commits its own applied ETag. Share
mapping/write primitives where coherent; do not force v4 through legacy DTOs
or introduce parallel v4 repositories. Use explicit resource, owned-relationship
and query-result writes for the reads mobile uses. The route manifest is a
server coverage/test baseline, not a universal mobile sync engine.

Keep imported UUID Realm keys; convert IDs only in the v4 request/response adapter.
Add short-keyed `NormalizedVenue`, `ArtistVenue`, and `NormalizedSong` rows,
embedded track normalization, and cached artist browse entries. `ArtistVenue`
uses the server's `artist_venue_` key, links the artist to one shared venue, and
stores `artist_show_count`. `NormalizedVenue` stores `global_show_count`; never
overwrite it with an artist count, a filtered collection length, or a partial
mobile cache total. A show links to its `ArtistVenue` when supplied. Query entries
discriminate the relationship from imported fallback. Store the imported target's
full `show_count` on its imported venue row; browse and detail use the same count.
Do not sum mixed browse-entry counts to infer an artist total: partially
normalized imported collections can overlap canonical ones. Keep one canonical
venue row shared across artists and preserve the imported venue rows for display.

Add an applied query state scoped by endpoint/filters and account where relevant.
Each request captures its scope generation and account/session fence. Apply
only the current request for that scope, current account and selected transport
generation. Discard in-flight results after a transport switch. HTTP ETag
equality supports 304; it is not lexically ordered. Do not use the legacy synthetic
ETag, 24-hour equality suppression, one-hour request cooldown, or timestamp-only
Realm guards for v4 data. Use a 15-minute default v4 query refresh interval;
manual refresh bypasses it, reconnect retries pending work, and offline views
use the last successfully applied local state.

Apply returned resources, supplied related entities, owned relationships and
the endpoint-defined query replacement in one Realm transaction. Write the
response ETag to applied-query state in that same transaction; it becomes an
applied validator only when the transaction commits. A 304 preserves rows and updates
successful check time only when a matching applied ETag exists; otherwise refetch
without the validator. Failed apply retries; failed/mixed-snapshot fetch never
tombstones associations. Independent query scopes may converge asynchronously;
do not claim a globally ordered client catalog snapshot.

Favorite kind selection follows model identity, not the catalog transport flag.
Update `FavoriteIconButton`'s model mapping and artist song/venue favorite filters:
imported `Song→imported_song`, `Venue→imported_venue`, `SourceTrack→track`.
The schema-aware build uses these mappings with either v4 or v2/v3 catalog reads.
Its revised account library stays enabled during catalog fallback. Never send a
raw UUID as canonical `song`/`venue` because a legacy screen retained an old kind
constant. Normalized models map to canonical kinds and short IDs. Convert an
imported catalog row's UUID into its canonical target ID before invoking library
repositories/heart lookups. Account membership rows and sync carry that same
typed target unchanged; only the catalog projection adapter converts back to
legacy Realm catalog keys when linking imported entities.

For the isolated account/auth checkpoint, keep catalog reads on v3. The catalog
adapter decodes only imported target UUIDs and maps `track→source_track`,
`imported_song→song`, `imported_venue→venue` for `/api/v3/catalog/resolve`.
Map echoed references back to canonical typed targets before membership checks.
Freeze that legacy resolver's DTO vocabulary separately from expanded account
target kinds; its server contract does not change. Account-owned IDs stay typed.
Short canonical `song_`/`venue_` memberships remain durable with metadata unavailable
until v4 hydration is present. This does not defer global venue favorites from
the completed v4 release; it keeps the account migration independently testable.

Normalized `song` favorites contribute only their owning artist to existing
My Library artist grouping, as imported song favorites do today. Hydrate the
`NormalizedSong` and owning artist; do not implicitly favorite or add every matched
show. This is independent from song discovery's exact recording matches.

The My Library Venues section lists exact normalized venue favorite targets,
opening a global venue/show destination. It does not add implicit artist/year/show
membership. Keep existing artist detail navigation and imported fallback screens.
Use existing My Library typography, spacing, row, error and empty-state tokens.
The UI thesis is the smallest new destination that gives global favorites a
home; this work does not introduce new brand rules or a component system.
