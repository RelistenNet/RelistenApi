# Decode provider assertions before matching

Provider capture retains the input. Decoding identifies what that input says and
where it says it. Matching, catalog binding and acceptance follow separately.
This separation lets the same retained input support corrected extraction without
changing a recording's UUID or asking the provider for the same response again.

Read the [capture contract](provider-capture.md) for resource keys and formats,
and the [domain contracts](../../../design/2026-10-17-normalization-contracts.md)
for accepted identities, corrections and public reads. This document describes
the decoding and legacy-seed contracts used by the implementation. The
[catalog-binding guide](catalog-binding.md) specifies current associations,
catalog invalidation and complete contributor-owned input sets.

## Typed assertions and exact evidence

`NormalizationProviderDecoder.Decode(input, cancellationToken)` is a pure
operation. Its dispatch key is the provider, resource type and capture version.
An unsupported version fails explicitly. The result records its decoder version
and input revision, then supplies recording, venue, repertoire and setlist
observations, plus cited issues. It performs no network calls, database writes,
identity allocation or automatic acceptance.

Malformed fields can produce cited issues while valid neighbors remain available.
Invalid envelopes or resource identities either fail decoding or return an issue
without the affected account. An empty result is not a withdrawal instruction.
The caller must preserve accepted data when an account is incomplete, unavailable
or unsupported. A supported negative observation, such as a dark Archive item,
also does not by itself withdraw playable catalog data.
Route an issue to the affected assertion. Unrelated supported fields can still
proceed through their matching policy; an unresolved credit or setlist annotation
must not withhold the recording.

Each observed value carries a `NormalizedEvidenceReference` into the retained
input. JSON values use JSON Pointers. HTML values use spans in the original
decoded document. Display names may decode HTML entities; prose passed to a
credit extractor retains the original tags and entities. A property name used
as data, such as a Local date-map key, cites its containing object because JSON
Pointer cannot address a property name as a scalar value.

Origins distinguish an individual retained resource, a catalog member, a page
member and a legacy snapshot. A page member establishes that a response contained
an assertion. It does not establish the latest revision of an item that can move
between pages. Nested files retain their recording-resource origin and their
own qualified file identities separately. An explicit file recording owner also
remains a cited assertion. If it names another recording, catalog binding rejects
that association even when the media URL matches.

Setlist passages preserve provider order and repeated songs. An observed ordinal
is a coordinate in that account, not a stable occurrence UUID. Set descriptors
retain boundaries, including unnamed or empty sets. Footnotes, explicit transition
markers, cover performers and guests remain distinct assertions. A cover credit
does not become a composer credit. Several song IDs attached to one file supply
membership without establishing passage order or audio boundaries.

Venue observations retain names and individually cited locality fields. City
coordinates remain city coordinates. Historical-name prose remains prose until
an extractor can support individual names. Archive fields without an established
work-credit convention remain cited text with an unresolved issue; the decoder
does not turn arbitrary `writer` or `composer` fields into accepted authorship.

`RetainedRecordingCreditExtractor` runs the local credit grammar on relevant
recording prose. It maps extracted spans back to the original input and validates
every quote. Array entries and directory accounts stay separate. Unknown or
unparsed credits remain unresolved. Extraction supplies names and roles, not
global person identities. Paid extraction and identity matching use their own
evaluated policies and caches.

## Seed evidence from the existing catalog

`NormalizationLegacyInputService.SeedRecordingAsync(sourceUuid, cancellationToken)`
captures stored data without provider requests. Its resource is
`legacy_catalog / recording / <source UUID>` and its capture version is
`legacy-recording-snapshot-v1`. The result is `Seeded`, `Existing` or `Missing`.

One primary-database, read-only repeatable-read transaction selects the source,
artist identity, show, source and show venues, configured provider identifiers,
sets and related tracks. It retains actual IDs and UUIDs. Missing, orphaned and
inconsistent track relationships remain evidence; their presence does not claim
playability. Artist settings and credentials are excluded. The payload explicitly
records `representation: legacy_catalog` and the snapshot time. It cannot recover
arrays, order or provider fields discarded by older imports.

The snapshot contains at most 10,000 tracks, 1,000 sets and 16 MiB. Exceeding a
bound fails before capture; no truncated snapshot becomes current. Track selection
uses indexed source and set relationships, including inconsistent cross-links.
After the catalog transaction closes, capture uses expected generation zero.
An existing input wins, including a concurrent seed. Repeating the command does
not refresh or replace it. Missing sources create no resource. The service rejects
ambient catalog transactions and observes cancellation.

These services are registered for the API and standalone worker. Registration does
not start a seed, decoder, resolver or backfill job. Production writes remain a
separate rollout step.

## Bind observations before matching

[`NormalizationCatalogBindingService`](../../../../RelistenApi/Services/Data/NormalizationCatalogBindingService.cs)
now binds legacy, Archive and Phish.in recording/file observations to current
catalog UUIDs. It reports missing, ambiguous and conflicting associations without
changing playback. Pass its artist and global source-identifier revisions into
work admission and publication. Migration 24 makes relevant catalog mutations
invalidate registered work even when provider bytes have not changed.

The [binding contract](catalog-binding.md) defines exact provider/file mappings,
lock order, retry behavior and atomic contributor-owned input unions. Each
recording belongs to one performance; files can span songs or sets within it.
Unclear early/late assignments remain on the default/current show.

Local still needs its retained root map and directory account bound together.
Jerry, enrichment setlists and imported repertoire/venue associations also need
provider-specific ownership checks. Legacy song upserts can replace an upstream
identifier when titles share a slug while keeping the winning UUID. Neither a
computed UUID nor a title fallback repairs that ambiguity.

Binding refreshes associations, not original wording. The legacy seed is still
seed-only. A refresh producer must retain and decode changed catalog fields before
publishing fresh credit/title interpretations. Negative binding outcomes also need
durable retry registration. These producers, remaining bindings and the evaluated
resolver remain required before automatic normalization.

## Verification

From the repository root, run the affected normalization and playback contracts:

```sh
./test-local.sh --filter 'FullyQualifiedName~Normaliz|FullyQualifiedName~Collection|FullyQualifiedName~Performance|FullyQualifiedName~TestPhishInPagination|FullyQualifiedName~TestPhishNetApiClient|FullyQualifiedName~TestPhishNetRatings|FullyQualifiedName~TestPhantasyTourDates'
```

This increment passes all 611 checks without skips. After the final fixture
cleanup, the following 26 seed, Local-decoder and artist-registry checks also pass:

```sh
./test-local.sh --filter 'FullyQualifiedName~TestNormalizationLegacyInput|FullyQualifiedName~TestNormalizationProviderDecoderLocal|FullyQualifiedName~TestTapersSectionArtistRegistry'
```

`dotnet build RelistenApi.sln --no-restore` succeeds with the existing 24 package
advisory warnings and no compiler errors. `dotnet csharpier check .` passes.
Fresh correctness and readability reviews cover each provider decoder, exact
citation handling, legacy seeding and recovery. These checks verify extraction
and service contracts; they do not establish identity-matching accuracy.
