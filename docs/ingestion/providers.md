# Provider behavior

Relisten combines recordings and setlists from seven providers. A catalog
**source** is a recording with its tracks and reviews. A **setlist** describes
the songs performed, venue, and tour. The artist rebuild joins that information
into the shows, years, and cached responses used by the app.

Use this guide when changing selection, parsing, or catalog writes. The
[overview](README.md) explains the worker and workflow structure;
[operations](operations.md) covers commands, diagnostics, and recovery.

The overview defines [result meanings](README.md#requests-admission-and-results),
[write and retry rules](README.md#writes-retries-and-failure-recovery), and
[durable progress](README.md#durable-progress-and-payloads). The sections below
explain where each provider needs different selection or cleanup behavior.

## Provider map

Each provider directory contains its contracts, activities, and workflow. The
linked importer adapter handles I/O and calls parser and catalog methods shared
with the legacy Hangfire importer.

| Provider | Item selection and key | Successful result | Entry points |
| --- | --- | --- | --- |
| Archive | Selected recordings, keyed by Archive identifier; also accepts one requested identifier | Source ID | [Workflow](../../RelistenApi/Services/Ingestion/Providers/Archive/ArchiveWorkflows.cs), [importer](../../RelistenApi/Services/Importers/ArchiveOrgImporter.Ingestion.cs) |
| Phish.in | Show pages, keyed by upstream numeric show ID | Source and setlist IDs | [Workflow](../../RelistenApi/Services/Ingestion/Providers/PhishIn/PhishInWorkflows.cs), [importer](../../RelistenApi/Services/Importers/PhishinImporter.Ingestion.cs) |
| Phish.net | Existing catalog recordings matched to show metadata, keyed by catalog source ID | The same source ID | [Workflow](../../RelistenApi/Services/Ingestion/Providers/PhishNet/PhishNetWorkflows.cs), [importer](../../RelistenApi/Services/Importers/PhishNetImporter.Ingestion.cs) |
| Setlist.fm | Paged setlists containing sets, keyed by upstream setlist ID | Setlist ID | [Workflow](../../RelistenApi/Services/Ingestion/Providers/SetlistFm/SetlistFmWorkflows.cs), [importer](../../RelistenApi/Services/Importers/SetlistFmImporter.Ingestion.cs) |
| Phantasy Tour | Missing past shows from listing pages, keyed by upstream numeric show ID | Setlist ID | [Workflow](../../RelistenApi/Services/Ingestion/Providers/PhantasyTour/PhantasyTourWorkflows.cs), [importer](../../RelistenApi/Services/Importers/PhantasyTourImporter.Ingestion.cs) |
| Jerry Garcia | Missing mirror pages for refresh; existing pages for venue repair; keyed by mirror show identifier | Setlist ID | [Workflows](../../RelistenApi/Services/Ingestion/Providers/Jerry/JerryWorkflows.cs), [importer](../../RelistenApi/Services/Importers/JerryGarciaComImporter.Ingestion.cs) |
| Local | Changed date entries from the metadata root, keyed by the upstream date string | One source ID per date | [Workflow](../../RelistenApi/Services/Ingestion/Providers/Local/LocalWorkflows.cs), [importer](../../RelistenApi/Services/Importers/LocalImporter.Ingestion.cs) |

Archive and Phish.net year filters select an exact year. Phish.in treats the year
as a lower bound. The other providers have no year option. An artist-wide
year-filtered command still runs those other providers without filtering.
Only Archive accepts a single-item command. PanicStream is not enabled for
Temporal ingestion.

## Archive: selection and independent deletion checks

Discovery records selected documents, their selection reasons, and all observed
identifiers. Item activities share metadata validation and date repair with
Hangfire through [ArchiveOrgImporter.Item.cs](../../RelistenApi/Services/Importers/ArchiveOrgImporter.Item.cs).
Skip reasons distinguish missing or invalid metadata, a dark item, an unrepaired
date, and no VBR MP3 files. Dark items are upstream recordings unavailable to
listeners; that status alone does not establish deletion.

A full refresh verifies deletion candidates after item processing. An apparently
complete listing can still omit recordings, so absence alone never authorizes
deletion. Preserve all of these checks:

- Load current catalog candidates and apply the five-candidate guard. More than
  five candidates prevents deletion.
- For recordings absent from the listing, check upstream metadata directly.
  Only Archive error code 104 establishes deletion. HTTP 404, an empty response,
  or a dark flag leaves deletion unproved.
- A successfully parsed item with no VBR MP3 can provide separate removal
  evidence, but only if that recording already exists in the catalog. New
  unplayable items do not consume the guard allowance. The workflow retains at
  most six such identifiers because six already triggers the guard.
- Exhausted item failures cannot supply missing-MP3 or deletion evidence.

Verification returns an allowed, guarded, or not-applicable plan. The deletion
activity applies only the plan's fixed decisions in a transaction. A retry must
not discover additional candidates. Its result describes the decisions and
identifiers now absent, rather than claiming an exact first-attempt delete count.
Guard alerts are best effort and may repeat after retries; logs must retain the
guard result even if the alert fails.

Targeted imports use the same item activity, verify membership in the artist's
Archive collection, and never reconcile unrelated recordings. An item outside
that collection fails explicitly. Exact-year imports also omit reconciliation.

**Shared ownership caveat:** Archive's candidate query currently considers all
sources for the artist, and source identity does not include a provider. Do not
assume its deletion candidates are isolated to Archive-owned rows. Changing this
requires an explicit catalog ownership change, not just a workflow filter.

Archive collection discovery creates artists and their upstream mappings in one
transaction per fetched page. Unlike recording writes, page application takes a
global lock before loading existing artists because slug allocation can affect
other artists. A retry uses the recorded page and finds an existing mapping
instead of creating another artist.

## Phish.in and Phish.net: creation followed by enrichment

Phish.in runs before Phish.net, which enriches its recordings. Phish.in fetches
and commits eras, tours, songs, and venues before importing shows.
It records the era and song mappings needed by item activities. Each item reloads
mutable catalog rows inside its transaction and restores those prepared mappings.
Keep these prerequisites ahead of concurrent shows; era creation is not an item
operation.

Full Phish.in imports read forward through 20-show pages. A lower-year-bound
import probes the first page, then walks backward from the last page until it
passes that bound. Each imported show provides both recording and setlist
identities keyed by its numeric upstream show ID. An unambiguous legacy date-keyed
setlist is upgraded in place. Source and setlist assignments retain the canonical
performance UUID; ordinary concert sets remain subdivisions of one performance.

Phish.net selects existing recordings and matching show metadata, then fetches
ratings and reviews when required. HTTP finishes before the write transaction.
The activity acquires the artist performance lock, then locks and reloads the
source before replacing its reviews or metadata. Different sources can fetch
concurrently; their writes are serialized for that artist. The legacy importer
uses the same guarded fetch/apply code. A missing, ambiguous, stale or unsupported
performance match returns a skipped outcome without overwriting accepted data.
Supported selections retain provider show ID and official permalink. Drain old
Phish.net histories before rollout because their selections lack this evidence.

Reviews not fetched and a fetched empty list are different inputs. If the source
date changed, or current ratings now require reviews that were not fetched,
`EnrichmentInputsChanged` rolls back the attempt. The next activity attempt
fetches fresh inputs outside the lock. An omitted review fetch must not erase
existing reviews.

## Setlist.fm: corrected dates and tour bounds

The page reader preserves the tolerant upstream parser and excludes setlists
with no sets before creating item activities. An item upserts its setlist and
song performances; unchanged items still return the existing setlist ID.

Items extend shared tour bounds with atomic minimum/maximum updates. Once all
pages and their items finish, `RefreshTourDates` recomputes each nonempty tour's
range from committed setlists. This second step matters when an upstream date
correction moves a tour boundary inward: extending bounds alone cannot fix it.

Individual item failures still allow that refresh, using the rows that committed.
A listing failure, cancellation, or expiry prevents finalization. Tours without
setlists retain their existing metadata.

The finalizer calls
[TourService.RefreshDatesFromSetlists](../../RelistenApi/Services/Data/TourService.cs),
which derives bounds from **all committed setlists for the artist**, grouped by
tour. It does not filter by provider. This treats those setlists as authoritative
for nonempty tour ranges, including tours another provider created. A provider
that supplies authoritative tour dates must resolve this shared ownership before
its mappings are combined with Setlist.fm; the schema does not enforce separate
provider ownership.

## Phantasy Tour: complete listings supply date repairs

The importer fetches detail pages only for missing past shows. It also records
the local calendar date of every listed past show, including existing shows.
Do not convert that date to UTC. When an ID appears again, its later observed
date replaces the earlier observation.

After complete enumeration, the finalizer repairs dates and tour bounds from
that recorded map. Individual detail-import failures do not prevent repair of
other committed rows. A listing failure prevents repair because the map is
incomplete. Existing shows need no detail request, so a date repair can change
the catalog while the imported-item count remains zero.

## Jerry Garcia: refresh and venue repair are separate operations

Normal refresh freezes the mirror index and selects pages missing from the
catalog. A retry that finds its setlist already inserted returns `Unchanged`.
Explicit venue repair selects existing pages and updates their venues without
replacing songs or setlist identities.

Both operations report a page without show details as `MissingShowDetails`.
Repair also reports `MissingCatalogSetlist` when a selected setlist has since
disappeared. These are skips, not successful imports with a missing identity.
Pages without a Last-Modified header retain the parser's current-time fallback;
that timestamp is not an identity or proof that an operation happened once.

Repair finalization refreshes tour bounds after all items finish. It deletes
orphan venues only when no item failed or was skipped: an unprocessed setlist may
still depend on an old venue. Successful repairs remain committed when another
item fails.

The [repair coordinator](../../RelistenApi/Services/Ingestion/Workflows/JerryVenueRepairWorkflow.cs)
rebuilds derived shows and years and invalidates cache after the provider,
including partial or empty repairs. If provider finalization fails, the
coordinator attempts that rebuild before reporting failure so committed venue
changes can reach listeners.
Cancellation and expiry stop without the recovery rebuild. A later rebuild or
import can repair derived data left unfinished.

## Local: one date can contain several directories

Local fetches the metadata root once, creates venue prerequisites, then selects
dates from the recorded root. A nonempty date is selected when its source is
missing or the first directory entry's timestamp is newer than the source.
Preserve that existing selection rule and the exact upstream date key.

The item activity fetches metadata for all selected directories before opening
its write transaction. It merges the date's directories into one source, its
set, and tracks. Splitting directories into separate recording identities would
change the catalog contract.

## Taper’s Section item intake

`TapersSectionDiscoveryWorkflow` records each search page before observing item
metadata. It continues after settled batches and retains the cursor and pending page.
Discovery requests identifier, collection and media type, and rejects a page containing
a row outside Taper’s Section audio. Archive has returned unrelated results for
identifier-only field selection; the extra fields make that failure detectable.
Search continuation requests omit `count`; repeated cursors and pages containing no
new identifiers fail discovery. Empty advancing pages continue the traversal.

`TapersSectionRefreshWorkflow` materializes approved new artists and selects both
approved artists and artists with catalog revisions that still need rebuilding.
`TapersSectionArtistWorkflow` owns item writes under `ingest/artist/{uuid}`, settles
its item activities, captures dirty revisions, rebuilds shows and years, invalidates
cache, then records completed revisions and promotes approved artists to visibility.
A paused artist can finish a required rebuild without publishing new items.

Unavailable items, artist conflicts and unresolved current-metadata date conflicts retain
their last catalog assignment. Supported partial dates publish with `XX`; current metadata
can correct an identifier date or move an existing recording to another show/year.
Precision and date disagreements are retained as nonblocking intake notes and exposed
through operator status only. Listener API response fields remain unchanged.
Intake does not infer deletion from collection absence. Ordinary Archive imports
skip intake-owned items and exclude their sources from collection deletion checks.

The [collection contract](../plans/active/2026-10-17-archive-collections/contracts.md)
adds configured Archive collection discovery, both membership rules, exact native-source
reuse and read-only v3 routes. Membership does not identify a performer or transfer
source ownership. The [performer contract](../plans/active/2026-10-17-archive-collections/performer-contract.md)
adds explicit per-item project reviews through the existing artist workflow. Ordinary
intake still matches reviewed structured creator keys; description evidence requires
an explicit item review. The [existing-artist contract](../plans/active/2026-10-17-archive-collections/existing-artist-publication.md)
adds reviewed bindings and missing-recording publication without taking native
source ownership. The [show contract](../plans/active/2026-10-17-show-performances/contracts.md)
defines distinct performances, source moves and date browsing. The [current roadmap](../plans/active/2026-10-17-archive-collections/README.md)
links local verification, including the [bounded Jerry proof](../plans/active/2026-10-17-archive-collections/jerry-projects-smoke.md),
and remaining release checks.

## Provider verification

Verify behavior through workflow results and catalog services: selection,
stable identities after retries, partial item failures, cancellation, and the
effect of incomplete listings on cleanup. The
[ingestion persistence tests](../../RelistenApiTests/Ingestion/TestIngestionPersistence.cs)
exercise synthetic upstream responses against PostgreSQL and temporary Temporal
servers. Use fake notification sinks; tests must not send operator alerts.
