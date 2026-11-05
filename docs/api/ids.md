# Public identities and saved references

Treat public IDs as opaque strings. [Relisten.Identifiers](../../Relisten.Identifiers/)
owns the catalog prefix registry, UUID codec and UUID-backed catalog types.
[Account identities](../../Relisten.Accounts.Contracts/Identifiers/AccountIds.cs)
use that codec and own their canonical string types/prefixes.
[Normalization IDs](../../Relisten.Normalization/NormIds.cs) retain their short
native format. Catalog hosts own JSON/binding rules; accounts do not depend on
the normalization worker.

## Catalog prefixes

These eleven prefixes encode the exact existing UUID as 26 lowercase Crockford
base32 characters: `artist_`, `show_`, `source_`, `source_set_`, `track_`, `year_`,
`tour_`, `imported_venue_`, `imported_song_`, `setlist_show_`, `source_review_`.
Existing UUID/numeric catalog join keys stay in storage. Never encode a numeric
foreign key as if it were the parent's UUID.

`venue_`, `song_`, `artist_venue_` use eight-character suffixes stored natively in
the normalization schema. Venue is a shared physical place; song is artist-owned;
artist-venue is a stable artist/place pair. Imported venue/song IDs identify
separate observations, not canonical entities with another prefix.

The codec preserves all 128 bits, including old UUID version/variant bits.
Prefixes end at the last underscore. Validate full kind, length, lowercase
alphabet and overflow; reject wrong kinds and ambiguous characters. The playable
file prefix is `track_` even on `/source-tracks/{sourceTrackId}`. `year_` identifies
an artist's year row; its year label remains a separate domain value.

Eras are embedded ordered metadata with no public identity. Provider item IDs,
slugs, ordinals, calendar dates, telemetry client UUIDs and protocol tokens keep
their own meanings. No speculative prefixes are reserved for future resources.

## Account prefixes and storage

Application-owned account IDs are stored as complete canonical prefixed text:
`user_`, `session_`, `favorite_`, `mutation_`, `library_change_`, `command_`.
Each uses a 26-character suffix. New IDs encode UUIDv7; migration encodes original
UUID bits without reminting logical identities. Account references, issued
`sub`/`sid`, and catalog favorite targets store the same canonical strings.

Framework-owned OpenIddict keys, private surrogates and external provider
subjects remain private values in their existing formats. Revisions remain
numeric counters. A favorite's membership ID, operation ID and catalog target
ID identify different things. See [accounts](accounts.md) for boundaries and cutover.

## Saved-reference resolver

`POST /api/v4/catalog/resolve` accepts 1–500 references in at most 128 KiB:

```json
{
  "references": [
    { "kind": "track", "scheme": "v4", "id": "track_<26 characters>" }
  ]
}
```

An item has `kind`, `scheme`, `id`, optional typed `artist_id`. Kinds correspond
to the 14 catalog prefixes. Supported schemes:

- `v4`: every catalog kind.
- `legacy_uuid`: every kind except `artist_venue`; canonical song/venue lookup uses its retained former UUID on an existing row.
- `legacy_integer`: UUID-backed catalog kinds only. For years this is the row key, not the calendar year.
- `legacy_slug`: artist, track, tour, imported venue/song, canonical venue/song.

Artist-owned integer/slug lookup requires artist context. Artist and venue
lookup are global. Optional context restricts other owned lookups too.
[CatalogReferenceLookup](../../RelistenApi/Services/Catalog/CatalogReferenceLookup.cs)
is the implemented lookup map.

The result has `references` in input order (including duplicates) and deduplicated
`entities` grouped by kind. Each reference echoes its input and reports
`availability` (`available`, `redirected`, `unavailable`, `retired`), nullable
`target: { kind, id }` and diagnostic `reason`. Returned parents are shallow:
shows omit sources, sources omit sets/links, sets omit tracks. Tracks retain
complete normalization. Artists appear only when explicitly requested.

All entity groups are patches. They never replace unreturned children or remove
favorites. Imported saved targets remain imported even when normalization exists.
The resolver is read-only/no-store with no ETag. Semantic errors return 422,
malformed JSON 400, oversized bodies 413, SQL failure 503 with no partial batch.

## Merges and retirement

Follow canonical merge chains to a survivor with bounded cycle detection; song
chains must stay within their artist. Direct lookup returns the survivor resource
and ID; resolver marks the redirect. Broken chains preserve embedded imported
fallback or report normalization unavailable. An artist-venue pair's identity is
retained at zero after its shows move to a merge survivor.

Migration 18 installs [canonical ID reservations](../../Relisten.Normalization/Sql/017_public_ids.sql)
in `norm.public_ids`. Song/venue insert/delete triggers reserve or retire IDs in
the source transaction. Ordinary minting cannot reuse a deleted ID. Explicit
validated human restore may reactivate one. Keep the registry and triggers when
fixing application code; they protect saved references.

Known retired direct IDs return 410; the resolver reports `retired` without a
target. Unlogged/pruned deletions are unknown, not provably retired. Former UUID
lookup can resolve a current row, but cannot recover a deleted row's tombstone
from that UUID alone. Catalog retirement never removes personal membership.
