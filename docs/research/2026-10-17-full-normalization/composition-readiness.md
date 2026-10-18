# Composition resolver evidence readiness

The inspected development material can supply real writer-extraction examples, but it does not yet supply verified global composition matches. No recognized work MBID or ISWC was found in the development subset. Recording prose contains explicit writer statements, including named people and groups. The catalog dictionaries and current decoders do not turn those statements into resolved author sets or evidenced repertoire-to-work links.

The next acquisition increment needs retained, provider-qualified work records and evidence connecting a repertoire claim to the work. Equal titles, cover performers and complete-looking credit rows cannot supply that connection. This audit does not activate matching policy or establish an accuracy estimate.

## Inputs and scope

The audit ran on October 17, 2026 against source at commit `8e52eb7169e3d63a7714300d6cd8a8d86f6ba783`. Detailed catalog inspection was restricted in Python **before output** to annotation evidence with both `family_partition == "development"` and `artist_partition == "development"`. This intersection is narrower than either development suite alone.

The filter retained 6,678 records: 2,185 recording sources, 1,625 tracks, 567 repertoire rows, 141 setlists and 2,160 venues. The authorship scan used the 4,518 non-venue records, spanning 2,227 artist UUIDs. The 567 repertoire rows span 29 artists. These are selected development records, not population coverage or independently adjudicated labels. The annotation pack contains 74 proposed composition pairs; only two have both inputs inside this strict development intersection, and neither has an answer. No resolved composition-pair label is established here.

The [machine-readable audit receipt](composition-readiness-receipt.json) records the exact selection, input hashes, scan patterns, computed counts and holdout exclusion. It records audit provenance; word matches are not labels.

Primary retained inputs and SHA-256 values:

| Input | SHA-256 |
| --- | --- |
| [Annotation evidence](../../../local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/evidence.jsonl), 18,628 total records | `968505f1b24b67e710aeb3b0aefcbbdaca9c59b2eb9106567f0c04c9717ec5c0` |
| [Annotation tasks](../../../local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/tasks.jsonl) | `d1cedcdc10b8efe923aee37cada9bd18345246379d068c2d8d3d915b2a9c9426` |
| [Annotation manifest](../../../local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/manifest.json) | `925db125599f4c28d8cc4c7c7cb6b48727619c8336afc915d3beadf4e7821a93` |
| [Underlying catalog snapshot](../../../local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl) | `1bcfa40bfc666c1222b3ee876250b61a76cd9b9adbbb978b62db1d16952166a4` |
| [Development writer excerpts](evaluation/references/evidence.jsonl) | `8d1f85c2edc9738c36e727c7bdc1a6ff2ab7194fcb85ff75bc8c4d5afa1aee73` |
| [Seed case partition file](evaluation/cases.jsonl) | `9f05f4ddcb8a85bb8578a203be56f2ee10d6f07aa19605e8bcf2b172d25f1040` |

The ignored `local-dev` files retain the input bytes; the Markdown report is not their backup. The [sampling guide](scale/README.md) and [annotation contract](scale/annotation/README.md) explain selection and partitioning. No production query was needed. A read-only check of the default local `relisten_db` found no `normalization.input_revisions` or `normalization.input_resources` table; that database supplied no additional retained HTTP corpus. This does not inventory other disposable development databases.

## What the development records contain

A recursive string scan found 37 fields in 36 records containing authorship-like words. Twenty-four fields contained `written by`, `composed by`, `words by`, `lyrics by` or `music by`. These counts include false leads and duplicated recording context; they are not certified work credits. The scan found no `composed by`, `lyrics by` or `words by` phrase. A generic `composer` mention in band prose is not a per-work composer assignment.

No structured writer/composer/lyricist fields were present in this catalog projection. Recognized work-ID searches covered `musicbrainz.org/.../work/`, `ISWC` and `musicbrainz_workid`; there were zero matches. This establishes absence in these retained fields, not in every original provider response. The captured dictionary rows retain only artist ID, legacy row ID, stored song UUID, name, slug, upstream identifier and timestamps. All 567 upstream identifiers are JSON strings; one is an all-digit string. The numeric example is Guster's song named `1999`, with upstream identifier `1999` (`song_sample:bf985e83-c12b-2463-db4d-d91317f39c2e`, line 1176). Numeric syntax is not a work identifier.

The following examples all passed both development filters. Line numbers refer to the annotation evidence file; field paths are within `raw_record`.

- **Explicit song-to-writer wording:** Parker Smith and the Bandwith, `source_sample:adf01ad5-aba1-6d39-1d68-9606e055b9f9`, line 6409, `/row/description`. The source lists its program and names Mark James for *Suspicious Minds*, Lowell George for *Willin’* and Bruce Springsteen for *Atlantic City*, with a general Parker Smith credit for the other songs. Its catalog upstream identifier is `Psbw2012-03-09.eddiesAttic`. These are generic writer assertions, without work IDs, resolved person UUIDs or a music/lyrics distinction.
- **Scope must remain local to the named song:** Mulpus Brook, `track_sample:bfb16693-1f72-5381-dd17-b4855184b934`, line 10813, `/source_context/taper_notes`, states that *Mystic Ride* was written by Craig Pingsterhaus. The sampled file title is *Last Time*. The source-level statement cannot be assigned to that file merely because it is embedded in its recording context. The recording UUID is `fb49f18e-0a2e-93d8-21e8-bcd8d0a57ad9`.
- **List-shaped prose is not certified completeness:** Hollowgate, `source_sample:ad3ebf33-9a11-43fc-c064-37c633f59c76`, line 3788, `/row/description`, credits Gary Weaver and Thad Parker for original songs. Chris Anderson, `source_sample:98e0b9e4-8598-481f-bafd-5db26c5d2a20`, line 4896, `/row/description`, has an album-wide writer statement and publisher wording. Preserve those statements and their scopes. This audit has not independently determined which individual works they exhaustively describe.
- **A credited group is not a catalog UUID substitution:** Flowstone, `source_sample:bf9e388f-9729-b5df-23bd-be87fafc72aa`, line 5428, `/row/description`, credits Flowstone with writing the songs. Mood Doculators, `source_sample:e25b5702-407c-552d-41ee-ca9b032bf019`, line 3089, `/row/taper_notes`, attributes *A Forest* to The Cure. Preserve the asserted group and role; neither the recording artist UUID nor a guessed individual member becomes the canonical author identity. Canonical people can represent groups, but require their own supported identity resolution.

Two existing development references add narrower official writer evidence: `reference:bobdylan:not-fade-away` names Charles Hardin/Buddy Holly and Norman Petty; `reference:bobdylan:johnny-b-goode` names Chuck Berry. Their retained URLs and brief excerpts are in [reference evidence](evaluation/references/evidence.jsonl). They supply generic writer wording and one explicit alias, not a global work ID or an automatic link to every same-titled repertoire row. No new song-page fetch was made in this audit.

There is no structured complete-author-list flag in the catalog capture. Completeness of individual prose statements remains an explicit review question. A successfully parsed list, the phrase “all songs,” or an external API returning its currently known relationships must not silently set `CompleteAuthorship=true`.

## Current decoder coverage

The observation models already have `WorkIdentities`, `WorkCredits` and `CompleteAuthorship`, but none of the current provider decoder branches populates them. Their presence is a storage contract, not evidence that acquisition is complete.

- [Imported repertoire decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.ImportedCatalog.cs) retains the stored song UUID as `legacy_catalog/repertoire_song/<UUID>`. Slug and upstream identifier remain catalog text. It does not infer provider ownership or work identity.
- [Archive decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.Archive.cs), `ArchiveUnclassifiedWorkFields`, retains `writer`, `writers`, `composer`, `composers`, `songwriter` and `lyricist` strings as `provider_field:<field>` text and emits `unsupported_work_credit_field`. Item and file scopes remain separate. It does not assume that an uploader's field defines a universal authorship schema.
- [Legacy recording decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.Legacy.cs) retains descriptions and taper notes, so the writer prose above remains available for a separate evidenced extractor. The recording extractor currently produces credit/title proposals, not complete composition author assertions.
- [Phish.in decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.Phish.cs) retains scoped song IDs and `alias_for`; those identify provider repertoire, not global works.
- [Setlist decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.Enrichment.cs) retains artist MusicBrainz IDs and separates `cover_artist`/`guest_artist` annotations. Those are performer assertions. PT song IDs and Phish.net/Jerry song links likewise remain scoped repertoire identities.
- [Local decoder](../../../RelistenApi/Services/Normalization/NormalizationProviderDecoder.Local.cs) reads title and track-number tags and retains notes. It does not project composer tags, work MBIDs or ISWCs from otherwise retained tag JSON. Availability of those tags in real Local directory responses remains unknown in this audited corpus; legacy catalog rows cannot recover discarded tags.

The real development fixtures `setlist-1992-11-21.html` and `setlist-1997-11-22.html` retain `/song/the-landlady` and `/song/mikes-song` among their song links. These are genuine Phish.net song paths, not work IDs. Their SHA-256 values are respectively `b1d44f552e71e2c87aafd315bb70e04ab88ce021554d593420c3ad289bd19544` and `a94f79ea995c37c17701e5645835bae9ae89c1abbbd2e939422eaf9e184d8cce`; neither contains a recognized work-ID pattern. They are parsing regressions, not resolved composition pair labels.

## Smallest supported acquisition and resolver increment

Add one bounded work-evidence adapter and decoder before activating composition acceptance. MusicBrainz is a concrete candidate: its [work entity](https://musicbrainz.org/doc/Work) has a work MBID and optional ISWCs, separate from recordings and artists. Its [artist-work relationships](https://musicbrainz.org/relationships/artist-work) distinguish writer, composer, lyricist and arranger. Its [API](https://musicbrainz.org/doc/MusicBrainz_API) supports work lookup with artist relationships. These official documents were checked for this audit; no work records were fetched or labeled.

1. Retain exact work lookup responses, request/resource identity, retrieval time and raw citations. A resource such as `musicbrainz.org/work/<work MBID>` must remain distinct from an artist or recording MBID. Keep aliases, disambiguation, ISWCs, relationship types and author artist identities. Fetch author entity type where needed to distinguish a person from a group; resolve each into the normalized people domain separately.
2. Retain the **bridge** from each repertoire claim to its work candidate: a supplied work identifier, an explicit recording-to-work relationship, or independently supported song/work evidence. Search by title can retrieve several candidates; title and search rank cannot accept one. A catalog song slug or a cover-artist annotation is not this bridge.
3. Extract the development writer statements with exact song/account/file scope. Resolve generic writer names independently, preserve group and unresolved targets, and keep composer/lyricist roles unknown unless stated. Store partial assertions without inventing completeness. Use an exhaustive author assertion only when its actual source semantics support that claim; a list of known external relationships alone supplies no completeness guarantee.
4. Exercise both existing accepted paths: `qualified_work_identity` with the genuine qualified work identity, and `writer_evidence` with independently established complete resolved author sets. The current [link writer](../../../RelistenApi/Services/Data/NormalizedMusicService.Compositions.cs) rejects title-only links; the [composition matcher](../../../RelistenApi/Services/Normalization/IdentityCandidateMatcher.cs) requires complete author sets for its title-and-authorship rule. Preserve those guards, candidate universe/owner observations and original evidence clocks.
5. Build real development pairs and independently adjudicated evaluation families: same work across artists with an explicit bridge; equal titles/different works; person/group namesakes; partial, conflicting and unresolved author lists; cover/arranger-only claims; source changes and negative searches. Report candidate recall and accepted-match accuracy separately. Existing writer-extraction references do not count as resolved composition-pair labels.

This is acquisition work required for the global composition resolver, not a replacement for it. Unsupported works must retain an explicit unresolved outcome while evidence is acquired. No paid model/API requests, runtime code changes, catalog mutations, provider backfill or policy activation occurred in this audit.

## Holdout exposure record

An initial broad text search of the mixed seed case file accidentally displayed the complete JSON records for two reserved cases:

- `compositions-claudia-title`, evidence `track:33689437`, input field `title`.
- `compositions-yes-filename`, evidence `track:7018353`, input field `title`.

The displayed fields were `id`, `domain`, `task`, `group`, `split`, `annotation_status`, `inputs`, `basis` (including the title value), `expected` (status/reason) and `rationale`. No further reserved-case inspection was performed. Neither family was used for the findings, supported examples, new labels or matching policy recommendations above.

Exclude these two exposed seed cases from claims of an untouched scored holdout. Before a future scored evaluation, replace them or move their complete related families into development and retain that partition change in the evaluation receipt. This audit records the exclusion requirement; it does not silently edit the frozen version-1 case file or relabel those examples.
