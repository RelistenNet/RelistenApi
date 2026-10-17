# Provider evidence required by the full normalization layer

The live database provides all currently stored catalog information. It cannot recover relationships that an importer discarded before persistence. The full normalization layer therefore includes durable input capture and metadata refresh, alongside its resolvers, taper/transferrer credits and shared composition identities.

The findings below come from the current checkout on October 17, 2026. They describe code and persisted fields, not a fresh claim about every field currently offered by each provider. Preserve structured fields before conversion, with provider resource keys, content revisions and supporting locators. Refresh only the affected metadata through ordinary bounded provider adapters, with explicit progress and failure outcomes.

## Archive.org, including GD and mixed collections

The [recording writer](../../../RelistenApi/Services/Importers/ArchiveOrgImporter.Recording.cs) retains descriptions, notes, source/equipment text, taper, transferrer, lineage and selected file URLs and provider-reported MP3/FLAC MD5 values. That retains wording for extracting taper and transferrer credits.

The [intake observation projector](../../../RelistenApi/Services/Ingestion/Providers/TapersSection/ArchiveObservationProjector.cs) also preserves scalar/array distinctions, file inventory, `original`, file `source`, SHA1, CRC32, size and other selected metadata. These observations can support file derivation and richer extraction for retained items.

The [native item import](../../../RelistenApi/Services/Importers/ArchiveOrgImporter.Item.cs) instead converts metadata through the narrower [Archive DTO](../../../RelistenApi/Vendor/ArchiveOrg.cs); its tolerant scalar converter can select the first array value. The new [shared capture adapter](../../../RelistenApi/Services/Ingestion/Providers/Archive/ArchiveNormalizationInput.cs) can retain the complete response before conversion when capture is enabled; existing catalog rows remain lossy. The composition/work and writer information is not modeled as structured fields in the current DTO or observation allowlist. New captures preserve available fields independently of that DTO, while older records still need a bounded refresh. Free-text assertions already retained remain usable evidence.

Collection membership establishes where an item was selected, not its performer, taper or lineage. Aadam, Jerry and Taper's Section must use the same underlying recording identities and normalization contracts as other Archive material.

## Phish.in and Phish.net

The [Phish.in DTO](../../../RelistenApi/Vendor/Phishin.cs) contains song IDs, `alias_for`, ordered track `song_ids`, sets, positions, notes and recording flags. The [importer](../../../RelistenApi/Services/Importers/PhishinImporter.cs) skips alias entries and reduces song relationships to unique show/song membership. Preserve those ordered identifiers and relationships explicitly. The current DTO models no composer, original-artist or external-work identifier; its notes can still support attributed extraction.

The [Phish.net client](../../../RelistenApi/Services/Importers/PhishNet/PhishNetApiClient.cs) and [ingestion adapter](../../../RelistenApi/Services/Importers/PhishNetImporter.Ingestion.cs) fetch shows, reviews and ratings, retaining provider show references and notes. They acquire no structured song catalog, ordered song setlist or work credits. Recovering those domains requires acquisition work, not merely adding database columns.

Phish-specific identifiers can strengthen matching and evaluation. The generic resolver must also work for artists with only recording metadata.

## Setlist.fm, JerryGarcia.com and PhantasyTour

The [Setlist.fm DTO](../../../RelistenApi/Vendor/SetlistFm.cs) models artist MusicBrainz IDs, which identify artists rather than musical works. Song `cover`, `with` and `info` fields are commented out. The [importer](../../../RelistenApi/Services/Importers/SetlistFmImporter.cs) also flattens sets and repeated song entries by slug. Refresh must preserve the available ordered entries and credits. A cover attribution does not independently establish a composer or work identity.

The [JerryGarcia.com parser](../../../RelistenApi/Services/Importers/JerryGarciaComImporter.cs) removes trailing segue/annotation markers and discards song links during `ProcessPage`. Repeated titles can receive separate numbered slugs, but explicit set and occurrence relationships are lost. The [PhantasyTour DTO](../../../RelistenApi/Vendor/PhantasyTour.cs) contains identifiers, segue flags, footnotes and show credits that the [importer](../../../RelistenApi/Services/Importers/PhantasyTourImporter.cs) reduces to unique name/slug membership. Capture adapters preserve these responses independently of legacy conversion. Typed decoders now retain ordered account entries and exact support; current catalog binding and evaluated occurrence correspondence remain work.

## Local recordings

The [local importer](../../../RelistenApi/Services/Importers/LocalImporter.cs) retains title tags, playback order, duration and combined notes. It can combine several directories for one day into a single source/set, losing directory-specific credit boundaries. Its current persisted model supplies neither composition relationships nor content checksums. Preserve directory and file evidence before combining it; apply the same resolution and uncertainty contracts.

Provider capture is implemented with reads and capture disabled by default. The [capture contract](../../plans/active/2026-10-17-full-normalization/provider-capture.md) records exact representations, resource identities and unresolved backfill work. Existing production data has not been backfilled by this code change.

The [decoding and catalog-seed contract](../../plans/active/2026-10-17-full-normalization/provider-decoding.md) now covers all configured providers and stored recording snapshots. Real Phish.net HTML checks preserve 30 and 15 passages, including repeats and footnotes. A real Kool Keith recording from the Aadam Jacobs collection supplies selected metadata examples; the fixture is a projection, not an original HTTP response. Jerry and Local HTTP tests remain synthetic. These are extraction and provenance checks, not identity-matching accuracy.

## Distinguish what each evidence type establishes

- A legacy track UUID hashes artist and URL. It is a playback reference, not an audio fingerprint.
- An observation content hash identifies metadata content, not the audio described by it.
- A provider file's `original` relationship concerns file derivation. It does not alone prove a complete capture or transfer chain.
- Provider-reported file checksums are assertions about bytes. Verified matching bytes and measured audio similarity are different evidence.
- Similar audio can support a shared performance without proving the same microphones, capture or transfer ancestry.
- Disjoint or unmatched audio clusters do not prove different shows: partial coverage and failed comparisons can also explain them.

Full ancestry and audio-family analysis are outside the current release, which requires taper and transferrer identities. For any later acoustic conclusions, retain exact asset/version anchors, algorithm versions, parameters and measurements. Evaluate false and missed matches on the actual material before adopting acceptance rules. Universal correlation, fingerprint-distance or overlap thresholds from a proposal are not established facts about this catalog.

Persist relevant inputs in PostgreSQL, independently of execution payloads. Temporal's payload transport can expire, and a successful workflow is not durable evidence retention. Metadata extraction and review produce attributable proposals; accepted normalized relationships and their corrections remain durable across re-import and rebuild.
