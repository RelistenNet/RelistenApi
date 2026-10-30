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
