# People and recording credits in normalization

The [domain contracts](2026-10-17-normalization-contracts.md) now specify evidence, decisions, person merges/splits, recording roles and public reads for implementation. This brief explains the product purpose. Use the contracts if their details differ. The [real-data corpus](../research/2026-10-17-full-normalization/evaluation/README.md) includes structured credits and description-only Jerry/Aadam examples.

Normalized people must support finding recordings by taper or transferrer across artists. A person has one stable identity independent of the bands they recorded. Their role belongs to a credit on a particular recording. The same person can tape one recording, transfer another and build a matrix from several recordings.

The [full normalization plan](../plans/active/2026-10-17-full-normalization/README.md) includes people, recording credits and cross-artist compositions as required capabilities for all current and future artists. The [completed performance work](../plans/active/2026-10-17-show-performances/README.md) preserves the source identities these relationships will reference. The earlier [schema reference](2026-10-16-catalog-normalization-schema.md) deferred normalized taper identities; its scope exclusions are superseded by the full plan. These relationships are not implemented yet.

## Source design and terminology

The colleague's [catalogue design, at its October 16 revision](https://github.com/RelistenNet/string-theory/blob/2a5f4ac33fe92373f5810308efb21d4e877321d7/docs/catalogue.md), describes normalized people, aliases and external identities, plus captures and transfers. Its [editing design](https://github.com/RelistenNet/string-theory/blob/2a5f4ac33fe92373f5810308efb21d4e877321d7/docs/edits.md) separates proposed and reviewed interpretations from imported evidence. These are useful inputs to Relisten's normalization design.

Here, **catalog** means the imported recording and playback data. **Normalization** means the identities and supported relationships derived from that evidence. The colleague uses “catalogue” for the latter. Keep this distinction explicit when comparing the designs.

The performance terminology also differs. Relisten now uses `Show` for one artist's performance engagement and groups shows by date for browsing. A default show can also retain recordings with unresolved or combined same-day coverage; sharing that show does not prove that their audio covers one performance. The colleague's `kb.events` describes an engagement; `reality.performances` describes stretches of music, which our broader design calls occurrences or passages. Do not restore the old date-only show model to adopt the people design.

## What exists today

The [source model](../../RelistenApi/Models/Source.cs) retains `taper`, `transferrer`, `lineage` and `taper_notes` as strings. The [Archive writer](../../RelistenApi/Services/Importers/ArchiveOrgImporter.Recording.cs) copies the provider's `taper`, `transferer` and `lineage` fields. Preserve those original values and the existing public spelling `transferrer`.

The [retained observation projector](../../RelistenApi/Services/Ingestion/Providers/TapersSection/ArchiveObservationProjector.cs) keeps original metadata, including scalar and array distinctions. Extraction should use that evidence where available instead of treating a converted display string as the complete upstream record.

[Existing source search](../../RelistenApi/Services/Data/SearchService.cs) already searches taper, transferrer and lineage text across artists when `artist_uuid` is omitted. It returns at most 20 source matches and also searches other descriptive fields. It cannot establish a person's identity, resolve their aliases, distinguish their credited roles or list all their recordings. Normalization adds those capabilities while preserving text search.

## Identities, credits and evidence

Use three domain concepts:

- **Person:** a stable UUID and display name, independent of any artist. Names are not globally unique. A credited handle or team name that has not been resolved to a person can remain unresolved credit text.
- **Names and external identities:** supported aliases and provider-qualified identifiers, with their evidence and review history. Equal names, initials or usernames on different providers do not prove that two credits concern the same person.
- **Recording credit:** a canonical source UUID, a role, original credit wording, supporting evidence and an optional resolved person UUID. Several people can hold the same role on one recording, and one person can have several roles. Initial roles include taper, transferrer and matrix builder; musician credits remain distinct from these recording-production roles.

A credit reports what the source evidence supports. A collection owner, uploader or collector is not automatically the taper. In particular, Aadam Jacobs collection membership does not by itself establish a taping credit.

Extraction can propose names and roles from structured fields, descriptions and lineage text. Retain the input observation or text hash, field and supporting span. Keep extraction separate from accepting a person assignment. Ambiguous names remain searchable as text without silently joining several people's recordings. Reviewed assignments survive re-import; changed supporting evidence can require review without removing playback.

Correcting a person link must not rewrite imported credit text. A person merge must preserve old references through redirects or equivalent durable identity history. Source, set, track and show UUIDs and media URLs remain unchanged. Moving a source during a show split changes its show association, not its person credits.

## Search outcome and lineage boundary

A listener must be able to find a person by a supported name or alias, then browse a paginated list of their playable recordings across artists. Role filters distinguish recordings they taped from recordings they transferred. Return each source once even when several matching credits apply. Counts must distinguish published recordings from unresolved credit mentions. Existing source text and listener routes remain compatible; exact additive API contracts belong in the implementation plan.

The release requires automatic person matching and cross-artist taper/transferrer search. Retain original lineage/source prose to extract explicit credits. Full capture/transfer ancestry, matrix inputs and audio-family reconstruction are outside this release. A shared person does not prove a shared capture, and an equipment chain does not identify a person.

Implement the person merge/correction, role-filtered read API, trusted operator authority and incremental refresh contracts in the linked domain specification. Verification must cover one person across artists, namesakes, aliases, several credited people, different roles, unresolved credits, repeated imports and source moves. Evaluate extraction, candidate recall and accepted-match accuracy separately on a substantially expanded real-data sample. The initial 102 challenge cases do not authorize automatic matching. Composer, lyricist, writer, arranger, cover artist, taper and transferrer remain distinct relationships even when one person has several roles.
