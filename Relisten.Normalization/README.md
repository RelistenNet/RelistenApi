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

## Derived counts

Migration 15 stores stable artist/place relationships and their show counts.
Migration 16 stores canonical song recording counts and imported setlist-show
counts. Public GETs read those rows without recounting. Imports/normalization
refresh after committing source work; admin edits refresh atomically. Failures
in post-commit maintenance require explicit repair, without repeating successful
imports or model work.

See the [count reference](../docs/normalization-counts.md) for definitions, identity
lifecycle, scoped concurrency, timestamps and repair SQL. The
[catalog contract](../docs/api/README.md) describes the public resources and
approval/fallback rules.
