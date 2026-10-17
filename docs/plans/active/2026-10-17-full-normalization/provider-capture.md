# Retain provider evidence before conversion

Provider capture is the next implementation step. Current domain writers and
readers can preserve evidence and corrections, but legacy import DTOs discard
information that normalization needs. Capture completed provider responses before
those conversions. Run extraction and matching separately from catalog import
transactions.

This note records the current code audit and constraints. The adapters below are
not implemented yet. Read it with the [domain contracts](../../../design/2026-10-17-normalization-contracts.md)
and [ingestion guide](../../../ingestion/README.md).

## Capture points

- **Archive.org recordings, including GD, Taper's Section and Aadam:** retain item
  metadata at [`ArchiveOrgImporter.FetchItemMetadata`](../../../../RelistenApi/Services/Importers/ArchiveOrgImporter.Item.cs),
  [`TapersSectionActivities.FetchEvidence`](../../../../RelistenApi/Services/Ingestion/Providers/TapersSection/TapersSectionActivities.cs) and
  [`ArchiveCollectionDiscoveryActivities.ObserveItem`](../../../../RelistenApi/Services/Ingestion/Providers/ArchiveCollections/ArchiveCollectionDiscoveryActivities.cs), before deserialization or
  selected-field projection. Use one resource key
  `archive.org / item_metadata / <identifier>` across collections and artists.
  Keep metadata scalar/array forms, creator and venue fields, credit and lineage
  prose, structured work information and file inventory. Do not fetch once per
  track; the item response already contains its files.
- **Phish.in:** capture in [`PhishinImporter.PhishinApiRequest`](../../../../RelistenApi/Services/Importers/PhishinImporter.cs), before converting
  catalog pages or selecting show fields. Keep song aliases, venue objects,
  ordered tracks, song IDs, sets, positions and notes. Current conversion drops
  aliases and reduces tracks to unique show/song membership.
- **GD/Jerry setlists:** retain the mirror response in
  [`ApplySelectedPageAsync`](../../../../RelistenApi/Services/Importers/JerryGarciaComImporter.Ingestion.cs), before
  `ProcessPage` converts song nodes to legacy repertoire entries, discards song
  links and strips trailing annotation/segue markers. Repeated titles can become
  separate numbered slugs; explicit set and occurrence relationships are lost.
  Use the provider-qualified show identifier. A fallback current timestamp is
  not an upstream revision.
- **Local recordings:** capture each configured root and directory before
  [`LocalImporter`](../../../../RelistenApi/Services/Importers/LocalImporter.cs) combines notes and file tags. Key the root resource by its configured metadata
  root. Key each directory by that root plus its directory path, so credits
  retain their source context.
- **Other enrichment:** Phish.net currently supplies notes, ratings and reviews,
  not an ordered repertoire or writer catalog. Setlist.fm response fields such
  as `cover`, `with` and `info` can support specific assertions when retained;
  a cover credit alone does not establish authorship.

Archive's tolerant conversion currently takes the first array value and omits
object contents. The intake projection preserves selected JSON types but has an
allowlist that omits structured writer information. Prefer complete Archive
metadata/file objects for new capture. Existing selected observations can seed
retention under their own representation version; never label canonical JSONB
projections as exact HTTP responses.

## Fetch and retention contract

Use [`NormalizationEvidenceService`](../../../../RelistenApi/Services/Data/NormalizationEvidenceService.cs) for resource generations, immutable revisions
and fetch receipts. Read the expected generation **before** starting the HTTP
request. The provider activity owns HTTP, cancellation, response identity
validation and retry delays. Retention owns the completed bytes, capture version
and receipt. Neither component decides identities.

Capture outside the catalog mutation transaction. Once capture is enabled for an
adapter, persist its evidence before acknowledging the input checkpoint. A later
catalog transaction failure leaves the captured revision reusable. Capture or
normalization failure must preserve previously published playback. Reuse a fetch
UUID only when retrying retention of identical completed input. A new HTTP
request gets a new UUID and a newly read generation. Stale captures remain
historical evidence and cannot drive current publication.

Paged Phish.in responses need special care: their show IDs are discovered after
the request. Reading each show's generation after receiving that page does not
fence stale responses. The first safe implementation can retain each response
under a stable request resource with item pointers. Overlapping or shifting page
observations need an explicit reconciliation rule before publishing a current
show interpretation. Per-show resources without
extra HTTP need a defined pre-fetch batch of known generations and zero-only
admission for newly discovered keys. Choose and test this before wiring capture;
do not quietly weaken the generation contract.

## Backfill and incremental work

1. Seed available retained observations and bounded groups of catalog sources
   and their files. Label catalog snapshots `legacy_catalog`; they cannot recover
   discarded provider fields. Seed an observation only when the provider resource
   has no current input, using expected generation zero. Retain further historical
   observations without promoting them over current input; revision strings alone
   do not establish chronology.
2. Refresh missing upstream evidence per recording or provider page. Bound
   request size, concurrency and retries; checkpoint successful retention.
3. Capture subsequent responses already fetched by ingestion. Add durable dirty
   markers for catalog edits outside those imports. Use content/version caches
   and work-subject watermarks to avoid repeating extraction unnecessarily.
4. Reconcile upstream metadata periodically. Provider timestamps and existing
   Archive import-selection rules do not guarantee detection of every correction.

Global identities publish before recording credits, venue assignments, editions
and file contents. A recording owner consumes identity-owner dependencies; it
cannot edit person or venue metadata. Define an atomic way to retain the complete
input binding set for artist repertoire before concurrent title-derived additions
are enabled. Replacing that set with one recording's inputs would discard other
songs' invalidation dependencies.

Verify fetch-generation races, exact retries, selected-observation seeding,
partial pages, cancellation, capture success followed by catalog rollback, and
two concurrent additions to one artist's repertoire. Use retained real provider
examples as fixtures. These are capture and ownership checks; independent
matching evaluation remains a later phase of the main plan.
