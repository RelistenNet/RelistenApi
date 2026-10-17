# Retain provider evidence before conversion

Capture adapters now retain provider inputs before legacy import DTOs discard
information needed by normalization. The adapters cover all seven providers in
`ImporterService`, plus Taper’s Section and Archive collection discovery. Capture
is disabled by default. Extraction, matching and accepted domain publication remain
separate work; retaining a response does not accept its claims.

Read the [domain contracts](../../../design/2026-10-17-normalization-contracts.md)
for publication fences and [ingestion operations](../../../ingestion/operations.md#normalization-input-capture)
for enablement and rollback. This document defines the retained representations,
provider ownership and remaining backfill work.

## Shared fetch contract

[`NormalizationProviderInputService`](../../../../RelistenApi/Services/Data/NormalizationProviderInputService.cs)
runs one caller-owned fetch. When enabled, it reads the resource generation before
HTTP, receives a validated value and the original response bytes, then commits
retention through `NormalizationEvidenceService` before returning the value.
The callback owns HTTP, envelope validation, cancellation and retry policy.
`NormalizationProviderResponse<T>` carries the parsed value, exact bytes, media
type including supplied charset, and optional real provider revision.

An enabled fetch rejects an ambient catalog transaction before HTTP. Captured
inputs use an independent transaction and survive a later catalog rollback.
Retention failure prevents the caller from acknowledging its checkpoint. A stale
capture remains historical evidence and throws `NormalizationStaleInputException`;
the caller must retry the whole fetch with a new generation and fetch UUID.
The adapter throws; the ingestion engine owns retry scheduling. Native Archive
can log an item failure and leave it for a later import. Internal retention
retries reuse one completed byte snapshot, observation time and UUID.

Acceptance fences the capture commit. It does not freeze the resource while a
later catalog transaction runs. Existing artist admission and catalog locks still
own catalog concurrency. Normalization publication must independently compare
its consumed input and dependency revisions through `NormalizationWorkService`.

Disabled capture performs the existing fetch without accessing normalization
storage. Legacy malformed-response paths in Jerry, Setlist.fm and Phantasy Tour
now use the same HTTP/envelope validation as their Temporal paths, so error
pages fail instead of becoming catalog data.

## Retained representations and resource keys

### Archive.org

Native imports, Taper’s Section and collection discovery share
`archive.org / item_metadata / <identifier>`, version
`archive-item-response-v1`. [`ArchiveNormalizationInput`](../../../../RelistenApi/Services/Ingestion/Providers/Archive/ArchiveNormalizationInput.cs)
retains the complete response bytes before tolerant deserialization or intake
projection. Metadata arrays/objects, creator and credit fields, writer information
and the file inventory remain available. One recording request covers its files;
no request is added per track.

Enabled capture rejects errors, mismatched item IDs and malformed envelopes before
changing current input. An identityless `is_dark:true` response is a supported
negative observation tied to the requested metadata URL; a supplied identifier
must still match. These rules do not retract existing accepted playback.

### Phish.in

Fixed catalogs use `phish.in / catalog / <route>`, version `raw-http:1`, retaining
the complete response before alias and venue conversion.

[`NormalizationPhishInInputService`](../../../../RelistenApi/Services/Data/NormalizationPhishInInputService.cs)
retains shows as `phish.in / show / <numeric id>`, version
`phishin-show-object-v1`. These are exact selected JSON objects from the response,
decoded with the response charset/BOM and encoded as UTF-8, rather than complete
HTTP response bodies. Unknown fields, embedded venue objects, track order,
song IDs, sets and tag notes survive.

Before each existing show-page request, read one generation-only snapshot of the
retained show namespace. Newly discovered IDs use expected generation zero;
never read their generations after receiving the page. Validate the success/data
envelope and unique positive show IDs before any capture. The importer checks
pagination metadata separately. The namespace snapshot fails before HTTP above
100,000 resources rather than silently truncating. A show keeps the same resource
when it moves between pages.

Each object commits separately. Failure or cancellation after a retained prefix
prevents page return and checkpoint advancement; retry the whole page. Successful
prefix captures remain usable evidence. No extra HTTP request is introduced.

### GD/Jerry setlists

The native and Temporal mirror paths use
`jerrygarcia.com / mirror_show_page / <mirror-prefix>/<show-id>`, version
`jerry-mirror-html:1`. Retain exact HTML bytes and charset before `ProcessPage`
strips song links and annotation/segue markers or converts repeated titles into
numbered legacy slugs. The prefix separates configured catalogs; the show ID
remains stable when a file moves.

Placeholders without show details skip without replacing current input. Required
malformed details and failed HTTP responses fail before capture. ETag and actual
Last-Modified can supply provider revision; the catalog’s fallback current time
cannot. Capture tests include synthetic mirror HTML and Latin1 decoding. A real
retained Jerry HTML cohort remains part of backfill verification.

### Local recordings

Local roots use `local / metadata_root / <configured root>`. Directory inputs use
`local / directory_metadata / ["<configured root>","<directory>"]`, with a JSON
array preserving the boundary between key parts. Both use `local-http-json-v1`.
Keys contain provider paths, not authentication headers.

Retain exact root and directory responses before merging notes into one date’s
recording. Both import paths stage changed-directory fetches before entering the
catalog transaction. Unchanged native dates still skip those fetches. Missing
root configuration fails before HTTP. Capture tests use synthetic Local documents;
recorded Local responses remain a cohort-verification gap.

### Enrichment providers

- Setlist.fm: `setlist.fm / setlist-page / <configured MBID>:page=<N>`, version
  `setlist-fm-http-v1`. Complete responses retain sets, repeats and available
  `cover`, `with` and `info` fields.
- Phantasy Tour: `phantasytour.com / show-list / <band>:page=<N>:page_size=100`
  and `phantasytour.com / show / <id>`, version `phantasy-tour-http-v1`. Retain
  original listing/detail JSON, footnotes, segue flags and credits.
- Phish.net: `phish.net / shows / v5`, `reviews / <date>` and `ratings / <date>`,
  version `phish-net-http-v1`. Retain bytes before string repair, filtering and
  HTML extraction. A successful unmatched ratings page retains its HTML and
  preserves existing published ratings/reviews. Resource keys exclude API keys.

A current page resource establishes request evidence, not the latest revision of
every item appearing on shifting or overlapping pages. Item-level normalization
needs an explicit reconciliation or targeted-refresh rule before treating such
observations as current entity facts. A cover credit alone does not establish a
composer or global work identity.

## Backfill and domain processing still required

1. Seed existing selected observations and bounded catalog snapshots, labeled by
   representation. JSONB projections are not original HTTP bytes. Seed a provider
   resource only with expected generation zero; retain additional historical
   observations without promoting them over a current input.
2. Refresh missing provider evidence per recording, show or page, with bounded
   concurrency, retries and checkpoints. Capture enablement does not backfill
   previously imported records or completed workflow selections.
3. Decode retained JSON/HTML into typed proposals with exact support. The shared
   `NormalizationRetainedDocument` already reads supported representations and
   validates exact locators and quotes; provider-specific observation decoders
   and catalog binding remain work. Add durable dirty markers for catalog edits
   outside imports and use content/version caches and work watermarks for
   incremental processing.
4. Reconcile upstream periodically. Timestamps and existing Archive selection rules
   do not guarantee discovery of every metadata correction.

Global identities publish before recording credits, venue assignments, editions
and file contents. A recording owner consumes identity dependencies; it cannot
edit person or venue metadata. Before concurrent title-derived additions are
enabled, implement atomic retention of the artist repertoire’s complete input
binding set. Replacing that set with one recording’s inputs would lose other
songs’ invalidation dependencies.
