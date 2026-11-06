# Integrating the catalog API

Read this when adding a catalog screen or changing how catalog data reaches a
client. [Swagger](http://localhost:3823/api-docs/index.html) defines the routes,
parameters and response fields. This guide explains how the music entities relate
and which data a client may treat as authoritative.

## Artist capabilities and providers

V4 artist resources expose `features` as a decimal string encoding an unsigned
64-bit mask, for example `"8388608"` (normalization only). OpenAPI declares it as
a `string`. A set bit means true, an unset bit means false; `"0"` means no capabilities. Bit positions never change or get reused. Clients must
ignore unknown bits. The separate `featured` field keeps its existing meaning.

- Bit 0 (`1`): `descriptions`.
- Bit 1 (`2`): `eras`.
- Bit 2 (`4`): `multiple_sources`.
- Bit 3 (`8`): `reviews`.
- Bit 4 (`16`): `ratings`.
- Bit 5 (`32`): `tours`.
- Bit 6 (`64`): `taper_notes`.
- Bit 7 (`128`): `source_information`.
- Bit 8 (`256`): `sets`.
- Bit 9 (`512`): `per_show_venues`.
- Bit 10 (`1024`): `per_source_venues`.
- Bit 11 (`2048`): `venue_coords`.
- Bit 12 (`4096`): `songs`.
- Bit 13 (`8192`): `years`.
- Bit 14 (`16384`): `track_md5s`.
- Bit 15 (`32768`): `review_titles`.
- Bit 16 (`65536`): `jam_charts`.
- Bit 17 (`131072`): `setlist_data_incomplete`.
- Bit 18 (`262144`): `track_names`.
- Bit 19 (`524288`): `venue_past_names`.
- Bit 20 (`1048576`): `reviews_have_ratings`.
- Bit 21 (`2097152`): `track_durations`.
- Bit 22 (`4194304`): `can_have_flac`.
- Bit 23 (`8388608`): `normalization`.

JavaScript numbers represent integers exactly only through `2^53 - 1`, and
number bitwise operators coerce to 32 bits. The decimal string preserves the full
unsigned 64-bit range (`0` through `18446744073709551615`). Decode and test flags
with the generated TypeScript helper; keep the string when saving JSON:

```ts
import { ArtistFeature, hasArtistFeature } from "./artist-features";

if (hasArtistFeature(artist, ArtistFeature.Normalization)) {
  // Normalized metadata is enabled for this artist.
}
```

Copy or import [the generated module](../../clients/typescript/artist-features.ts)
into a TypeScript client. It exports named decimal-string constants and the
`hasArtistFeature` helper, so callers never copy bit positions or expand the mask.
The helper uses `BigInt` internally and requires runtime support for it. It uses
no bigint literals, so it can compile with an ES2017 target and ES2020 (or newer)
library definitions.

OpenAPI publishes all flag names and decimal-string values under
`components.schemas.ArtistResource.properties.features.x-feature-flags`, and on
`RankedArtistResource` as well. An excerpt:

```json
{
  "x-feature-flags": {
    "Descriptions": "1",
    "Normalization": "8388608"
  }
}
```

The map and human-readable description both come from `ArtistFeatures.cs`. The
TypeScript generation tool builds the real V4 Swagger document and reads that
map; it starts no API server and connects to no database. From the repository root:

```sh
dotnet run --project tools/generate-artist-features
dotnet run --project tools/generate-artist-features -- --check
node --test clients/typescript/artist-features.test.mjs
```

Regenerate the module when adding a feature. `--check` fails if the checked-in
module differs from the current schema; an optional output path after `--` lets
clients generate the same module directly into their own source tree. The Node
tests require Node 22.18 or newer for TypeScript loading. Other OpenAPI generators
must explicitly consume `x-feature-flags` to produce equivalent named constants.

`upstream_sources` contains only `{ provider, upstream_identifier }` assignments.
`provider` uses the shared `UpstreamProvider` string enum (`archive.org`, `phish.in`,
`phish.net`, `setlist.fm`, `phantasytour.com`, `jerrygarcia.com`, `panicstream.com`,
`local`). An unrecognized stored provider is returned as `null` without failing
artist reads; its upstream identifier is retained. Multiple assignments remain
separate. Provider URLs, descriptions and credit lines are no longer repeated
on each artist. List, detail, search, ranking and resolver artist resources share
this representation.

This is a breaking V4 response change. Clients must update feature decoding and
provider handling together, then refresh persisted artist and song/venue browse
responses. Existing V2/V3 resources and database records are unchanged; no schema
migration or backfill is required.

## Shows group recordings; tracks identify playable files

One show can have several recordings, each represented by a source. A source
contains sets and playable tracks. A track identifies a file, so a medley can
contain several songs without becoming several playable tracks. Normalized parts
provide song matches, qualifiers and transitions; they do not provide seek offsets.
Normalization adds metadata to a file while preserving its imported title,
playback URL and ordering.

Start with artist → years → selected year → show. Fetch sources, song file matches
and other collections when the listener opens them. Full show detail includes the
recording hierarchy; a show summary does not. Retain that distinction when applying
responses to a local database: a summary's omitted children say nothing about the
children already stored. See [local persistence](mobile-sync.md).

## Imported observations and canonical entities serve different purposes

An imported venue or song belongs to an artist's upstream catalog. A canonical
venue identifies a shared physical place; a canonical song is still artist-owned.
Changing an imported ID's prefix cannot convert one into the other. Their link
requires accepted normalization evidence. A show's venue assignment can differ
from its source's imported venue observation.

Normalization is optional and can be incomplete. Artist venue/song browse lists
return only canonical entries when normalization is enabled, even if coverage is
incomplete or the canonical list is empty. Otherwise they return imported entries.
Follow each entry's kind; a client does not need a feature-flag decision to select
a browsing route. The normalization bit permits normalized reads but does not
promise coverage.
The imported inventory and detail routes, and imported references in the resolver,
remain available for either kind of artist. Embedded imported venues and track titles
continue to describe the original observations.

Display imported show venue labels and track titles. Link a venue label to its
canonical venue when one exists; use canonical display metadata only when the
imported observation is absent. Canonical aliases aid discovery. They are not a
history from which a client should infer the venue's name on the concert date.

`GET /api/v4/venues` lists canonical venues across all artists as a `VenueResource[]`,
ordered by name (ordinal) and then ID. Each surviving venue appears once, including
venues with no shows or no artists approved for normalization. Merged IDs are
excluded; their aliases remain on the surviving venue. The list uses the same
metadata and approved-artist `global_show_count` as global venue detail, without
embedding `shows` or artist-specific wrappers. It is a complete, unpaginated
canonical inventory; imported venues remain in artist-scoped inventories.

A show's `artist_venue` represents the artist/place pair, with that artist's show
count. Its nested venue's global count spans approved artists. An imported venue's
count covers its entire imported group. During partial normalization, an imported
group can overlap canonical groups, so combining imported inventory counts with
canonical group counts can double-count shows. Canonical song counts/shows require exact recording matches; imported song
counts/shows use setlist evidence and cannot identify a playable file. See
[count maintenance](../normalization-counts.md) before changing these definitions.

Imported song resources always include `normalized_song_id`: the surviving `song_…`
ID from approved mapping evidence, or `null` if no usable mapping is available.
The link follows song merges. It does not replace the imported ID, metadata, or
setlist-backed show collection. Inventory, detail and resolver responses expose
the same link. Approved browse lists contain only canonical songs; search selects
canonical targets where mapped and otherwise retains imported results.

Canonical song detail's `shows` entries contain `{ show, timings }`.
Each timing bucket takes the longest positive, known matching file per source,
then reports `source_count`, `min`, `max`, `avg` and `median` across those maxima.
A short reprise therefore does not dilute a recording's timing sample. Durations
are whole-file seconds; they do not measure a song's segment within a medley.
`single_song` requires fully resolved parts with only that canonical song; resolved
non-song parts are ignored. `multi_song` requires at least two known canonical songs.
One-song files with unresolved parts contribute to neither timing bucket.

Average and median values are rounded to two decimals. An empty timing bucket is
null; missing durations or incomplete interpretations never remove a matched show.

Add `?include_tracks=top` or `?include_tracks=all` to include a `tracks` array in
each song-show entry. `top` lists the matching files of one source per show: the first source
containing the song in the clients' default order (soundboard, then Charlie Miller
or Peter Costello transfers, then the higher etree ID, then the higher weighted
rating). See [source ranking](../../RelistenApi/Services/Catalog/CatalogSourceRanking.cs).
`all` lists every matching file, with each show's sources in that order. Each file
appears once with only `id`, `source_id`, `show_id`, `title`, `slug`, `duration`
and `mp3_url`, including files without usable timing samples. The key is omitted
by default and with `include_tracks=none`; omission does not clear any files already stored
by a client. Timings always summarize every source. Use the song's `/tracks` route
for full track resources, including FLAC URLs, checksums and normalization.

Imported song details still return plain show summaries because setlists cannot
identify the file to time.

## Identity and saved references

UUID-backed public IDs losslessly encode the existing catalog UUID. Canonical
venue/song and artist-venue IDs are native short strings. Clients generally keep
all IDs opaque; mobile decodes UUID-backed catalog IDs at its storage adapter to
preserve existing download filenames and playback references.

Canonical merges return a survivor ID. Deleted canonical IDs are reserved against
reuse so a saved reference cannot silently point to a different place or song.
Saved references may therefore resolve as redirected, retired or unavailable.
Unavailable metadata does not imply that a listener removed a favorite. The
resolver returns entity patches, without ownership of unreturned child collections.

`artists/{artistId}/songs/by-slug/{slug}` returns the same song detail as
`songs/{songId}`, including `include_tracks`, for that artist's canonical song slug. A merged
song's slug returns the survivor, so a client redirects when the returned `slug`
differs from its URL. Imported song slugs are not accepted here; resolve them with
the reference resolver.

Canonical venue slugs support readable URLs through the explicit global and
artist-scoped `venues/by-slug/{slug}` routes. They return the same resource as an
ID lookup, including the survivor's current ID and slug after a merge. Imported
artist-specific slugs can identify different show collections; resolve saved
imported URLs with the reference resolver's `legacy_slug` scheme and imported kind.

The [catalog types and codec](../../Relisten.Identifiers/),
[normalization IDs](../../Relisten.Normalization/NormIds.cs) and
[reference lookup](../../RelistenApi/Services/Catalog/CatalogReferenceLookup.cs)
define supported identities and lookup schemes. Swagger publishes their wire forms.

## Ranking scores have a comparison population

Ordinary show/year resources carry artist-local popularity. Artist resources and
global popular/trending views use global comparisons; momentum is a separate
discovery score. The same show can legitimately have different scores in different
views. Treat ranking views as online results rather than writing their contextual
scores into the ordinary offline catalog. Opening a ranked item loads its ordinary
detail before catalog persistence. [Stored popularity](../popularity/README.md)
explains refresh ownership and failure recovery.

## Where changes belong

Ordinary [catalog reads](../../RelistenApi/Services/Catalog/) select data in one primary,
read-only repeatable-read session. [Resource projections](../../RelistenApi/Models/Api/V4/)
produce the public representation. Normalization owns canonical links and vocabulary;
imports own original recording metadata. Keep batch hydration shared across routes.
Existing v2/v3 readers remain independent for older clients.

A client transport returns API responses; its repositories own local mapping and
collection membership. The [User Service](accounts.md) owns account membership
independently of catalog availability. Public play activity represents anonymous
track starts, not an authenticated listener's history or measured listening time.
Live history selects events separately from catalog hydration and is a best-effort
activity view.
