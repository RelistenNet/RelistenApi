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

## Common application interfaces and legacy projections

The maintained application code lives in `RelistenApi/Services/Catalog/`, with
core result models that do not inherit `RelistenApi.Models` legacy DTOs. Readers
accept one `CatalogReadSession`; commands accept typed domain inputs. The session
owns one connection/transaction and is disposed before serialization. Shape
conversion stays in `Models/Api/V4` and `Services/Catalog/Compatibility` adapters.
Reuse the same resource/track projection across endpoints. Add query-specific
result models only where ownership or represented data differs; avoid a chain
of one-for-one model copies or a separate reader stack per route.

Imported result identities include both the stored numeric key and typed UUID
identity where legacy output needs them. Those internal keys are never serialized
through v4 DTOs. Canonical entities use their short IDs. V2 adapters emit the
existing numeric ID fields; v3 emits existing UUID fields. Do not recover numeric
IDs through reverse HTTP lookups or prefix stripping.

Two internal read views are sufficient: `Imported` and `ApprovedNormalization`.
The first skips normalization joins and exposes imported facts; the second
applies existing artist approval and raw fallback. Read target is separately
`Primary` or `PreferReplica`, selected by the adapter/session owner. V4 uses
Primary plus the appropriate view; v2/v3 use Imported plus PreferReplica and
keep their existing response cache policy. Perform the legacy full-artist cache
lookup after minimal identity resolution and before expensive core materialization;
a Redis cache hit must not trigger a new full catalog read. These are not public
query flags.

New v4 code must not wrap legacy services/JSON. Move each migrated read algorithm
into the common reader and remove duplicate old read SQL after auditing callers.
Legacy controller actions translate old routes/aliases into common typed queries
and project old DTOs. Keep ambiguous numeric/slug compatibility parsing in those
adapters or the explicit resolver, not in core identity types. Preserve full
numeric-leading tour slugs and the current source/set/track hierarchy.

Operator commands likewise have one handler over existing ingestion services.
V4 PUT selects the target from its typed route and rejects conflicting body ID;
the legacy PUT adapter preserves its existing body-target interpretation before
calling that same typed update handler. Error/status adaptation remains at the
wire boundary. This preserves old behavior without making core code inspect an
API version.

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
result groups. Full artist bootstrap returns a direct catalog object with
`artist`, `years`, `shows`, `tours`, `eras`, `imported_venues`, `imported_songs`,
`venue_entries`, `song_entries`, `artist_venues`, `venues` and `songs`.
These are domain collections, not a generic response wrapper.

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
- Search, popular/recent/top, anniversary and other filtered/limited endpoints
  replace only their exact cached query result. An absent result never implies
  that the resource is gone from another list or the catalog.
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
No v4 Redis body cache or immutable max-age policy in this release. Ranking
maps retain their existing asynchronous Redis ownership; load once per request.

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
adding replicas or shared response caching requires an explicit consistency
design and measurements, not a future switch of connection strings alone.

Use ProblemDetails with a stable `code` for failures. Preserve 400 for malformed
JSON, 422 for semantically invalid request/prefix/kind/filter, 404 for a valid
unknown resource, 410 for a known retired canonical ID, 413 for body bounds,
and 503 for disabled service/read timeout/dependency failure. Authorization
keeps its current 401/403 semantics. Resolver item unavailability is data in a
200 batch response. No partial successful collection is returned after SQL failure.

## Core browsing before account work

Alec expanded C1 to review navigation before auth or mobile work. These reads
are now part of the local checkpoint:

- `GET /api/v4/artists` and `/artists/{artistId}` return artist identity,
  counts, capabilities and provider assignments. `capabilities.normalization`
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
routes remain unchanged. Redis popularity augmentation and the remaining route
families follow the [catalog review sequence](review-guide.md); these local
preview routes do not claim complete legacy capability coverage yet.

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

`GET /songs/{songId}/tracks` returns song metadata and `tracks`.
It has no `shows` property. Each `tracks` item is shaped as:

```json
{
  "track": { "id": "track_<UUID-suffix>", "normalization": {} },
  "matching_part_ordinals": [1, 3]
}
```

The example abbreviates `track`: the actual child is the complete existing
`TrackResource`, identical to its direct track read. It preserves the imported
playback title, file URLs, exact artist/show/source/set parents, and full ordered
normalization. Return each playable file once even when the requested song
occurs in several parts. `matching_part_ordinals` lists those matched parts in
ascending order; it does not establish new file identities or audio offsets.
This separate file-match detail avoids loading thousands of tracks when a client
only needs song metadata and shows.

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

## Canonical redirects and retirement

Follow `merged_into` to the survivor with bounded cycle detection. Direct lookup
returns the survivor resource directly, whose ID identifies the result. The
caller already knows its requested ID; batch resolution explicitly echoes both
requested reference and canonical target when that mapping is needed. Artist-
scoped songs cannot cross artist ownership through a merge. Corrupt merge chains
are logged and cannot hang reads: show/track projections preserve imported
fallback; direct canonical lookups return dependency-unavailable rather than
invent a target.

Add a small `norm.public_ids` reservation/tombstone table limited to canonical
`song`/`venue` IDs: `id text PRIMARY KEY`, `kind text`, `last_name text`, and
nullable `retired_at timestamptz`. Backfill existing canonical rows as active.
A BEFORE INSERT trigger atomically reserves every newly minted ID by inserting
its row into this table. The row is never removed on retirement. Its primary key
refuses any later remint even while deletion is concurrent; a SELECT-only guard
against a separate tombstone table is insufficient. Current mint/retry behavior
handles the resulting unique violation.

A deletion trigger marks the existing reservation retired and captures the last
name in the same transaction. The existing human restore/revert context may
reactivate the exact reservation and restore that row; ordinary minting cannot.
Validate restore authority and resource kind. A rolled-back deletion does not
retire the reservation. Retaining one row per canonical ID avoids global/artist
writer locks and closes both sequential and concurrent reuse.

This preserves current matcher cleanup and historical row-image comparisons:
do not add canonical-row columns or rewrite old change-log JSON for retention.
Direct lookup of a retired reservation is 410; resolver returns `retired`;
membership is unaffected. Active merges already retain canonical rows. This is
one fixed two-kind identity-safety table, not a general provenance/claims system
or new public editing API.

## Resolver and operator inputs

`POST /api/v4/catalog/resolve` accepts at most 500 references and 128 KiB.
Each reference supplies `kind`, `scheme`, `id`, and optional `artist_id` context.
Schemes are `v4`, `legacy_uuid`, `legacy_integer`, `legacy_slug`. Integer/slug
lookups for artist-owned resources require artist context; an artist lookup is
global. The numeric calendar year uses its explicit by-number route, not a
legacy ID parser. The complete resolver must support all 14 catalog kinds where
their backing identity exists, including nested set/review/setlist-show records.
The new `artist_venue` kind has a native short v4 identity and no legacy UUID,
integer, or slug identity. Add its `artist_venues` entity group when implementing that resolver; this C1
update still navigates relationships through artist/venue lookup. This catalog
resource does not add a favorite target kind; global venue favorites remain
`venue` targets.

Per-item results echo the request and give `available`, `unavailable`, `redirected`,
or `retired`, with canonical target and hydrated entities when available.
Imported-to-normalized mapping is a separate optional result: `matched`, `unmapped`,
or `ambiguous`, with show context/candidates. Available imported data stays
available when not normalized. Never silently choose a canonical venue for a
conflated imported row. Parent data is enough for existing mobile hydration;
missing catalog data does not erase account state.

V4 query names use snake_case: `artist_ids`, `artist_id`, `previous_days`,
`sort_by`, `delete_old_content`, with other current scalar filters/limits
preserved. `shows/on-date` means month/day anniversary; `shows/by-date` means
exact display date. Default anniversary values use the database session date, preserving current
NOW()/CURRENT_DATE behavior; record its configured timezone at rollout rather
than silently switching to a client-local or new hardcoded clock. Callers can
continue sending explicit month/day. Recent-update sorting remains imported
recording changes.

`POST /api/v4/live/play` uses JSON `{ track_id, app_type, client_uuid? }`.
`client_uuid` retains optional caller-supplied telemetry identity and is not
authenticated account identity. History uses `after_sequence` and current limit
bounds; event sequence is not a prefixed catalog ID. Ranking/recent bounds and
supported sort/window choices are listed in the coverage tables; invalid enum
values are 422 rather than silently selecting a different sort.

Artist create/update accepts v4 artist fields, embedded capabilities and provider-
qualified upstream assignments. Numeric database IDs are not writable identities.
PUT route identity is authoritative; reject a conflicting body ID. Save the
artist/features/upstream assignments in one `CatalogImportTransaction`. Keep
existing API operator-cookie authorization on these writes and import commands;
do not accept ordinary account-service login as operator admission. Preserve
the provider API-key endpoint, Temporal/Hangfire engine checks, delete restrictions,
debug restrictions, and workflow/job handle response fields. Operations and random
reads are no-store. No endpoint in this plan starts an import as a test.

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
