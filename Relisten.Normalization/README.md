# Normalization vocabulary

`CatalogueVocabulary.cs` owns song kinds, venue kinds, venue-name kinds,
coordinate precision, and track qualifiers. Normalization models and API
resources use these types directly. API-specific availability and response
states belong to the API.

`EnumMember` values are the persisted text contract. Use `NormalizationEnums`
to format SQL parameters, parse text arrays, and supply structured model-output
schemas. Do not use `Enum.ToString()` for stored values: `WithGuest` must remain
`with_guest`. The existing simple-name scalar tokens can be read by Dapper;
qualifier arrays require explicit text conversion.

System.Text.Json converters preserve historical activity payloads, including
nullable values. The API's Newtonsoft adapter uses the same `EnumMember`
values without changing unrelated legacy numeric enums. This change needs no
database migration or workflow drain.

Unknown model-proposed song kinds are rejected as invalid answers before any
write. Previously they reached the database's CHECK constraint. Missing or
null song kinds remain valid. Venue proposal fallback rules are unchanged.

Run `Relisten.NormalizationTests` for parser, matcher, model-answer, and payload
contracts. `RelistenApiTests/Normalization` exercises SQL writes and reads on
disposable local databases; see `../local-dev/README.md` for setup.

## Stored artist venue counts

Migration 15 adds `artist_venues`: stable `artist_venue_…` identities and stored
show counts for artist/place pairs. Counts follow accepted show-level assignments
resolved to their canonical venue. They do not depend on artist rollout flags or
the number of recordings. Zero-count rows keep their IDs; merged pairs redirect
within the same artist. Creation and update timestamps describe the derived row.

Source transactions enqueue affected artists through database triggers. The
`ArtistVenueCounts` service refreshes them in separate short transactions, so
counts can briefly lag imports or admin edits. Queue acknowledgment and refreshed
counts commit together; failures remain retryable. The minute refresh schedule
drains pending work, and daily reconciliation requests all known owners.

These derived rows have no human-edit or run provenance fields. Normalization
decisions and their audit history remain authoritative. The foundation changes no
public API response; v4 can consume the stored relationships in a later change.
See the [implementation plan](../docs/plans/active/2026-10-30-artist-venues/README.md)
for ownership, validation, and rollout details.
