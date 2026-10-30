# Proposed v4 interfaces and journeys

These are contract sketches for choosing the approach, not implemented DTOs.
The [implementation contracts](../../plans/active/2026-10-29-v4-api/contracts.md)
supersede exploratory shapes here, including canonical account/auth ID storage
and the in-place account API cutover. The 2026-10-30 venue decision also
supersedes the historical-display sketches below: show UI uses `imported_venue`,
links through optional `venue`, and receives `{ name, kind }` aliases without dates.
Names and field casing can be settled in planning. Angle-bracket IDs below are
schematic placeholders, not parser test vectors.

## Show and venue ownership

```json
{
  "id": "show_<lossless-128-bit-suffix>",
  "artist_id": "artist_<lossless-128-bit-suffix>",
  "imported_venue": {
    "id": "imported_venue_<lossless-128-bit-suffix>",
    "name": "MSG",
    "location": "New York, NY"
  },
  "normalized_venue": {
    "id": "venue_k3m9x2qa",
    "current_name": "Madison Square Garden"
  },
  "venue_display": {
    "name": "Madison Square Garden",
    "location": "New York, NY"
  },
  "revision": "<opaque-read-revision>"
}
```

`imported_venue` preserves the existing artist-specific resource. Its prefixed
identifier is reversible to the stored UUID. `normalized_venue` is a separate
resource and may be null. The API resolves it through the show-specific
normalized link, not solely through the imported venue's evidence mapping.

`venue_display` is a show-specific projection. The recommendation is to use the
normalized name for the show date, then fall back to the imported label. Shared
venue current-name changes need not rename every historical show. This naming
policy remains distinct from the confirmed decision to prefer normalized data.

A source may have its own imported venue observation. Preserve that observation
with the source rather than rewriting it to claim agreement with the show's
canonical venue. The show-specific normalized link controls show grouping.

Proposed C# types distinguish imported and normalized identity:

```csharp
// In a dedicated v4 contract namespace; sketches, not declarations to copy verbatim.
public sealed record ShowDto(
    ShowId Id,
    ArtistId ArtistId,
    ImportedVenueDto? ImportedVenue,
    VenueDto? NormalizedVenue,
    ShowVenueDisplayDto VenueDisplay,
    ReadRevision Revision);

public sealed record ArtistVenueBrowseEntry(
    VenueBrowseReference Reference,
    int ShowCount,
    VenueDisplayDto Display);
```

`ArtistId`, `ShowId`, `SourceId`, `SourceSetId`, `SourceTrackId`, and
`ImportedVenueId` wrap existing 128-bit catalog values. `VenueId` and `SongId`
retain the normalization library's short string IDs. Reuse encoding helpers,
but do not apply `INormId`'s eight-character validator to legacy values.
There is already an ingestion `ArtistId` type; public DTO placement must avoid
coupling ingestion contracts to the HTTP contract merely to reuse that name.

All wire IDs serialize as JSON strings. Route parsing checks the expected
entity prefix; queries bind decoded UUIDs or normalized string values as
appropriate. A venue ID cannot be supplied as a track ID. Slugs are display
and navigation aids, not identity. OpenAPI must describe strings rather than
UUID-only fields or record-struct objects.

## Grouped venue browsing

```text
GET /api/v4/artists/{artistId}/venues
GET /api/v4/artist-venues/{artistVenueId}
GET /api/v4/artists/{artistId}/venues/{venueId}
GET /api/v4/venues/{venueId}
GET /api/v4/imported-venues/{importedVenueId}
```

The first endpoint returns one entry per normalized place for that artist,
plus separate imported-venue entries where normalization is missing or disabled.
The reference discriminates canonical versus imported identity. Imported fallback
detail needs an explicit imported-venue route or typed-reference lookup; it must
not be interpreted as a canonical `VenueId`.

The [implementation contract](../../plans/active/2026-10-29-v4-api/contracts.md)
supersedes this exploration’s response sketches. Details return metadata,
complete `shows` with one payload ETag. Artists are cached separately, referenced
by `artist_id`. Shared venue
counts are global across approved artists; relationship counts belong to the
artist and canonical venue. Artist-venue ID and pair lookups return the same
membership owner. Imported detail accepts its globally unique imported ID alone.
Compact browse resources have no detail arrays, and no detail URL is returned.

Mobile keeps `Venue`, adds `NormalizedVenue`, and gives `Show` both links.
It caches grouped browse entries with artist, reference kind, reference ID,
count, and collection revision. A local composite key identifies the cached
entry; it does not become a public venue ID. Old venue rows and favorites need
not be discarded merely because a normalized group replaces them in browsing.

## Track normalization and song discovery

All track normalization lives under one `normalization` key. Its `state`
describes the track interpretation, and its ordered `parts` carry the results.
There are no top-level `normalization_state` or `contents` fields. This is
required on every returned v4 track object, including nested source tracks,
search/discovery results, and catalog-resolution results. This information
does not require a separate song-detail request. A response containing only
track references is distinct from one returning track objects; ordinary track
objects must not silently omit normalization.

```json
{
  "id": "track_<lossless-128-bit-suffix>",
  "title": "Scarlet Begonias > Fire on the Mountain",
  "normalization": {
    "state": "partial",
    "parts": [
      {
        "ordinal": 1,
        "text": "Scarlet Begonias",
        "song": {
          "id": "song_7hd2pw4e",
          "name": "Scarlet Begonias"
        },
        "state": "matched",
        "qualifiers": [],
        "notes": [],
        "segue_after": true
      },
      {
        "ordinal": 2,
        "text": "Fire on the Mountain",
        "song": null,
        "state": "unresolved",
        "qualifiers": [],
        "notes": [],
        "segue_after": false
      }
    ]
  }
}
```

The track remains one file and one queue/download identity. Its imported title
remains the playback title. `normalization.parts` exposes normalization for mobile display,
search, and details without replacing the imported title. Matched parts carry
the current canonical song ID and name, plus available kind/artist information
where useful. Read this compact song data with the track's interpretation in
the same snapshot; avoid per-track or per-part follow-up queries. Part
ordinals are not durable occurrence IDs. No seek position is implied by an
ordinal or segue flag.

An uninterpreted track has a distinct state from a track whose parts are all
resolved. A resolved null song link can mean non-song content, while an
unresolved null link means the matcher has not placed it. Nullable song IDs
alone cannot encode those states. Preserve qualifiers and available part text.
When no interpretation is available, return an explicit state such as
`normalization: { "state": "unavailable", "parts": [] }`. Do not claim that
matching has never run merely because no rows exist. The final state vocabulary
belongs in planning and must account for disabled rollout and absent results.
Track refresh revisions cover normalization changes even when the imported
title and file remain unchanged. Future domains add named optional relationships
without changing playable identity or reinterpreting these song parts as timed
occurrences.

```text
GET /api/v4/songs/{songId}/tracks
    -> playable source-track references + show/source parents + matching parts
```

A song may occur in multiple parts of one track. Return the playable track once
with the matching part ordinals; do not count each part as a separate file.
Do not label recording-track counts as unique performance counts. A future
timed occurrence resource requires its own identity and evidence.

## Favorites and legacy reference resolution

Confirmed requirement: global normalized venue favorites ship in the first
release. A canonical venue target and an imported venue target are different
reference kinds. Existing account `catalog_type = venue` currently means the
imported UUID resource; a new contract must distinguish that legacy meaning.

The account service needs typed catalog targets while keeping favorite IDs,
mutation IDs, and authentication identity separate. The global target covers
all artists at the physical venue, even when created from an artist's list.
The artist is navigation context, not part of global favorite identity.

Proposed account payload:

```json
{
  "target": {
    "kind": "normalized_venue",
    "id": "venue_k3m9x2qa"
  },
  "desired_state": "favorite",
  "mutation_uuid": "<independent-UUIDv7>",
  "favorite_uuid": "<independent-UUIDv7>"
}
```

Use one favorite store and change stream with a validated `{kind, id}` target.
Migrate the target in favorites, mutation receipts, and change records, plus
membership uniqueness and allowed-kind constraints. Imported targets can keep
canonical UUID text internally while the public v4 adapter encodes/decodes
prefixed IDs. This prevents two encodings creating duplicate membership.
Normalized targets store the existing short string. Avoid a UUID-surrogate
registry solely to fit an old Guid property.

The account service owns membership, replay/idempotency, quotas, and library
revisions. The catalog API owns entity availability and merge resolution.
Catalog read failures do not erase membership or need to block writes. An
account library revision tracks membership changes, independently of the
normalization epoch that tracks catalog interpretation changes.

Alec confirms that favorites have only shipped in private developer alpha,
so a breaking favorite contract is acceptable. Use a new contract version and
coordinated server/mobile migration rather than requiring an old-client read
projection or dual outbox transports. Existing imported venue favorites may keep
their scope, but perfect migration of internal test favorites is unnecessary.
Planning must choose a clean alpha bootstrap/reset or a simple retained-data
conversion; do not implicitly replay rewritten old mutations under old IDs.
No reset has been performed or authorized for unrelated account/catalog data.

New-version mutation hashes must include target kind and canonical identity.
Keep new retries idempotent and retain the rule that replay returns the original
result instead of reapplying an obsolete desired state. Do not reinterpret old
receipts as if they had been generated under the new hash format.

The catalog resolver accepts an explicit reference scheme, resource kind,
and ID. It echoes the request and reports availability separately from
normalization mapping:

```text
legacy imported venue UUID -> imported venue resource, if available
                           -> optional normalized candidate(s)
v4 encoded track ID        -> same legacy track, using decoded bits
merged normalized venue ID -> canonical survivor plus redirect information
```

An imported venue may remain available even when no canonical match exists.
If show-specific rulings divide its shows among places, return contextual
mapping or candidates; do not invent a one-to-one rewrite. Favorite membership
survives temporary catalog unavailability. The resolver supplies evidence for
migration, not authority to broaden an existing favorite's scope.

## Read consistency and recovery

Each response carries an opaque read revision covering raw and normalized
state. Read revision and data share one database snapshot. Opaque tokens
support equality only. Planning must define an ordered generation or request
generation plus revalidation before claiming older responses cannot overwrite
newer state. Mobile does not parse an ID or timestamp to infer freshness.

If a complete artist venue list loses a canonical group after a correction,
mobile replaces that collection's associations at the declared revision. It
keeps imported venue rows, favorite membership, and offline playback records.
An individual page, failed fetch, or stale response cannot establish absence.
Pagination must retain one collection revision or restart before replacement.

If normalization is unavailable, the API returns the imported show/track data.
If a canonical venue merges, canonical lookup follows the survivor and retains
the old public reference. Retirement needs a durable unavailable/redirect
record before externally referenced IDs can be physically removed.
