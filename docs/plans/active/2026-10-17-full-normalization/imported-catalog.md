# Retain imported venues and artist dictionaries

Use these services to bring existing `venues` and `setlist_songs` rows into
normalization without treating their names as accepted identities. A stored
venue may have no artist. A song dictionary belongs to one artist; equal titles
under different artists remain separate evidence.

The [recording refresh](catalog-refresh.md) and [setlist-account binder](setlist-binding.md)
cover recordings and provider accounts. This guide covers imported venue and
dictionary facts, their existing row associations and publication dependencies.
It does not select physical venues, compositions or canonical repertoire entries.

## Capture complete facts

`NormalizationImportedCatalogService.RefreshImportedVenueAsync(venueUuid, ct)`
returns `NormalizationImportedVenueSnapshot`, or null if the stored UUID is
missing. Its resource is `legacy_catalog/imported_venue/<venue UUID>` and its
capture version is `legacy-imported-venue-v1`.

`RefreshArtistRepertoireAsync(artistUuid, ct)` returns
`NormalizationArtistRepertoireSnapshot`, or null for a missing artist. An
existing artist with no songs produces a valid empty dictionary. Its resource is
`legacy_catalog/artist_repertoire/<artist UUID>` and its capture version is
`legacy-artist-repertoire-v1`.

Each operation reads the retained resource generation, then reads catalog facts
and producer revisions in one primary, read-only repeatable-read transaction.
Capture runs after that transaction closes. Both the immutable payload and the
returned snapshot contain the original producer revisions. A competing capture
can win the resource compare-and-swap; the loser retains its receipt and throws
`NormalizationStaleInputException`. Retry the whole refresh rather than combining
another capture with old facts.

Payloads preserve stored UUIDs, null and empty wording, upstream identifiers and
slugs. They exclude internal numeric IDs and operational timestamps. Songs sort
by stored UUID. Unchanged facts and producer revisions reuse the same immutable
input; every admitted fetch still advances the resource generation. A broader
artist revision change can produce a new payload even when these particular
names did not change.

Both representations have a 16 MiB UTF-8 limit. A dictionary has a 50,000-song
limit; the producer checks a bounded lookahead and rejects oversized dictionaries
without capturing a partial list. No truncation implies deletion. Venue
coordinates must be finite when supplied. Missing producer clocks raise a
conflict that requires catalog repair. Ambient transactions are rejected, and
cancellation does not undo evidence already retained.

## Decode wording without inventing facts

`NormalizationProviderDecoder` emits exact JSON evidence references. Imported
venues use the existing `legacy_catalog/venue/<UUID>` observation identity;
the containing input remains `imported_venue`. Their coordinates have unknown
precision because legacy data can contain city coordinates. Location and past
names remain original prose.

Dictionary members use `legacy_catalog/repertoire_song/<song UUID>` observation
identities. A missing title stays missing; a slug is not a substitute title.
Catalog upstream identifiers and slugs remain cited text because the legacy row
does not identify their provider. No alias, composition, author, setlist order
or repeated occurrence is inferred from these fields.

## Bind the exact retained facts

`NormalizationCatalogBindingService.BindRefreshedImportedVenueAsync(snapshot, ct)`
returns the existing venue UUID, its optional artist and the original input,
artist and imported-venue dependencies. `BindRefreshedRepertoireAsync(snapshot,
ct)` returns the existing artist and each stored song UUID with its reproduced
observation. Neither method needs caller-supplied decoded observations.

Before any database await, binding freezes caller-owned bytes and revision
arrays and decodes the frozen representation. In one primary repeatable-read
transaction it verifies the actual retained capture receipt, then compares the
complete current projection with the retained payload and wrapper revisions.
The capture and binding paths share each projection query. A fabricated receipt,
changed ownership, deleted row, stale dictionary membership or changed producer
revision raises `NormalizationConflictException`. Refresh the whole resource.
Attaching fresh wrapper revisions to old wording cannot make that wording current,
even if a later correction restores the original name.

Pass every returned `Inputs` entry as `expectedInputRevisions` and every
`CatalogInputs` entry to work admission. Venue consumers must also pass
`ImportedVenues` as `importedVenues`. The shared work service includes these
venue dependencies in configuration, contributor ownership, pending work and
publication checks. Omitted venue dependencies retain prior ticket and request
hash compatibility. The current single-recording proposal processor rejects
additional imported-venue dependencies it cannot consume.

## Migration and operations

Migration 29 seeds independent venue revisions and retains them after row
deletion or UUID replacement. Artist ownership alone cannot fence unused,
artistless venues. Venue changes advance the old and new UUID revisions;
timestamp-only updates do not. Imported songs use their artist revisions,
including both owners when a row moves between artists.

The lock order is artist revisions, source identifiers, imported venues, mapping
registry, retained input resources, then work owners. Artist deletion locks its
owned venue revisions before foreign-key cascades can take the mapping registry.
Bulk writes across owners must prelock affected clocks in this order or retry
the whole transaction after a deadlock. `TRUNCATE` is offline maintenance: pause
writers and publication first. Its callbacks advance retained venue revisions.

Deploy schema before code that consumes these dependencies. Before rolling code
back, stop normalization coordinators and publication: older binaries do not
check imported-venue dependencies. Retain migration 29, its clocks, evidence and
contributor ownership. The down migration deliberately refuses destructive rollback.
Existing playback identifiers and public API responses do not change. No
automatic matching policy or normalized read flag is enabled by this increment.

Verification uses the public refresh, decode, bind, admission and publication
contracts against disposable PostgreSQL. It covers empty dictionaries, shared
titles across artists, ownership changes, deleted/replaced venue UUIDs,
concurrent capture, exact replay, stale publication, rollback and cancellation.
These tests establish evidence and freshness behavior; they do not measure
identity-matching accuracy.
