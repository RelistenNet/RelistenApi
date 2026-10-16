# Provider behavior and logging

Provider workflows expose the sequence of import operations. Their activities call the existing importer parsers and catalog methods through `ProviderActivityRunner`. Each provider workflow admits up to ten item activities concurrently. Preparation finishes first; finalization waits for all admitted items. Providers for the same artist still run in catalog order. Only the artist coordinator rebuilds derived shows, years, and the cache after provider work completes.

The provider directories contain contracts, workflows, and activities together. Each activity delegates to the matching `*Importer.Ingestion.cs` domain method; parsing and catalog writes shared with Hangfire remain ordinary importer methods.

| Provider workflow | Visible activity sequence | Domain implementation and catalog result |
|---|---|---|
| [ArchiveWorkflow](../../RelistenApi/Services/Ingestion/Providers/Archive/ArchiveWorkflows.cs) | `DiscoverRecordings` → `ImportItem` for each selected recording → `VerifyDeletionCandidates` → `ApplyVerifiedDeletions` | [Archive importer](../../RelistenApi/Services/Importers/ArchiveOrgImporter.Ingestion.cs) shares `ImportReason`, `FetchItemMetadata`, `ImportSingleIdentifier`, and deletion verification. Each successful item returns its source ID. |
| [PhishInWorkflow](../../RelistenApi/Services/Ingestion/Providers/PhishIn/PhishInWorkflows.cs) | `PrepareCatalogs` → `ReadShowPage` → `ImportShow` | [Phish.in importer](../../RelistenApi/Services/Importers/PhishinImporter.Ingestion.cs) shares era, tour, song, venue, and show processing. Prepared mappings are separate typed inputs. Each show returns source and setlist IDs. |
| [PhishNetWorkflow](../../RelistenApi/Services/Ingestion/Providers/PhishNet/PhishNetWorkflows.cs) | `SelectSources` → `EnrichSource` | [Phish.net importer](../../RelistenApi/Services/Importers/PhishNetImporter.Ingestion.cs) fetches ratings and required reviews, locks and reloads only the current source row, and enriches that source. |
| [SetlistFmWorkflow](../../RelistenApi/Services/Ingestion/Providers/SetlistFm/SetlistFmWorkflows.cs) | `ReadSetlistPage` → `ImportSetlist` → `RefreshTourDates` | [Setlist.fm importer](../../RelistenApi/Services/Importers/SetlistFmImporter.Ingestion.cs) shares `ProcessSetlist`, excludes setlists with no sets, and atomically expands tour bounds during item writes. After complete pagination, it recomputes bounds from committed setlists. Each item returns a setlist ID. |
| [PhantasyTourWorkflow](../../RelistenApi/Services/Ingestion/Providers/PhantasyTour/PhantasyTourWorkflows.cs) | `ReadShowPage` → `ImportShow` for missing shows → `RepairListedDates` | [Phantasy Tour importer](../../RelistenApi/Services/Importers/PhantasyTourImporter.Ingestion.cs) shares `ApplyShowBody`. Existing shows need no detail GET. Complete listing evidence drives date and tour-bound repair. Each imported show returns a setlist ID. |
| [JerryWorkflow and JerryVenueRepairProviderWorkflow](../../RelistenApi/Services/Ingestion/Providers/Jerry/JerryWorkflows.cs) | Refresh: `SelectMissingPages` → `ImportPage`. Repair: `SelectExistingPages` → `RepairVenue` → `FinishVenueRepair` | [Jerry importer](../../RelistenApi/Services/Importers/JerryGarciaComImporter.Ingestion.cs) shares `ProcessPage` and venue backfill behavior. Each item returns a setlist ID. |
| [LocalWorkflow](../../RelistenApi/Services/Ingestion/Providers/Local/LocalWorkflows.cs) | `FetchRootAndPrepareVenues` → `SelectChangedDates` → `ImportDate` | [Local importer](../../RelistenApi/Services/Importers/LocalImporter.Ingestion.cs) retains the fetched root for selection, fetches each selected directory's metadata, and merges all directories for one date into one source. |

[The contract reference](provider-workflow-contracts.md#3-concrete-provider-sequences-and-ownership) defines the typed inputs, cursors, checkpoint evidence, and result fields. Upstream JSON/HTML parsing retains its existing behavior; Temporal serialization uses System.Text.Json and does not inherit Newtonsoft attributes.

## Archive selection and deletion

Hangfire's `ProcessDocument` and Temporal's item activity share selection reasons, metadata validation, date repair, and `ImportSingleIdentifier`. [ArchiveOrgImporter.Item.cs](../../RelistenApi/Services/Importers/ArchiveOrgImporter.Item.cs) contains those shared decisions and diagnostics. Archive skip results distinguish invalid or missing metadata, a dark item, an unrepaired date, and no VBR MP3.

A full refresh records the complete listing's observed IDs. Verification loads current catalog candidates and checks missing-listing items directly. Only upstream errcode 104 proves deletion. A 404, empty response, or dark flag alone does not. No-VBR-MP3 evidence comes from successful metadata validation; an exhausted item failure cannot manufacture that evidence.

The existing five-candidate guard remains independent of workflow success. The
provider keeps at most six IDs of stored recordings that lost their MP3s because
six already trips that guard. New unplayable upstream items are skipped without
consuming this evidence or counting as deletions.

Verification returns an overall allowed, guarded, or not-applicable result.
Transactional deletion applies only the fixed verified decisions; it does not
enumerate new candidates on retry. Guard notifications are best effort and may
repeat after an activity retry.

Targeted Archive imports call the same `ImportItem` activity with a requested identifier. They do not enumerate or reconcile unrelated sources. An item outside the artist's Archive collection fails explicitly. Exact-year Archive imports also omit reconciliation. Other providers cannot accept targeted item commands.

## Provider-specific constraints

- Phish.in runs before Phish.net. A Phish.in year is a lower bound; Phish.net and Archive years are exact filters. A filtered artist command preserves the other providers' unfiltered refreshes.
- Phish.net locks its source row before replacing reviews; different sources remain concurrent. It distinguishes reviews not fetched from a fetched empty list. If current catalog state requires reviews absent from the fetched inputs, `EnrichmentInputsChanged` rolls back and retries the activity with fresh HTTP inputs. It does not erase existing reviews based on an omitted fetch.
- Shared venue and tour creation reuses the existing row on a unique conflict. Setlist.fm and Jerry expand tour dates with atomic minimum/maximum bounds, so concurrent items cannot shrink another item's range.
- Setlist.fm recomputes nonempty tour ranges after all listing pages and item activities settle; corrected show dates can move a boundary inward. Item failures still permit this refresh from committed rows. Listing failure, cancellation, or expiry suppresses it. Tours without setlists retain their metadata.
- Phantasy Tour retains local calendar dates for every listed past show; a later observation of the same ID replaces its earlier date. Date/tour repair runs only after complete enumeration, including when a missing-show detail import failed. A listing failure suppresses repair.
- Jerry venue repair preserves successful item changes if another item fails. It refreshes tour bounds, but removes orphan venues only when all repair items succeeded. If the provider finalizer fails, the Jerry repair coordinator still rebuilds committed venue changes into listener shows and invalidates the cache before reporting failure. Cancellation and expiry stop without rebuilding. The existing no-Last-Modified fallback remains; its timestamp is not an exactly-once identity.
- Local imports preserve the upstream date key and catalog UUID formulas. All directories for the date produce one source, its set, and its tracks. The activity fetches directory metadata before its write transaction.

## Results and diagnostics

`Applied` and `Unchanged` include the current catalog identity. Supported `Skipped` results include a provider-specific reason. Artist summaries expose completed, skipped, and failed counts; completed includes applied and unchanged. Attempt-local created/updated/removed diagnostics are not exact per-execution totals because an acknowledged retry can observe an earlier committed attempt.

Shared importer messages use `ILogger` and also the Hangfire console when a `PerformContext` exists. Passing a null Hangfire context does not suppress Archive validation, date-repair, or guard diagnostics. Provider activity scopes identify execution, artist, provider, workflow, activity, and attempt. Typed item inputs/results and bounded failure samples remain in workflow history.

HTTP logs omit query parameters because some providers put credentials there. The standalone worker does not initialize a separate Sentry integration; existing parser calls and API/Hangfire configuration remain unchanged. Temporal UI supplies retries, cancellation state, and workflow failures. Large offloaded inputs appear as Redis references unless inspected with the configured converter.

## Shared behavior and verification

Hangfire and Temporal share catalog identity and parser rules. Temporal adds bounded activity retries, explicit partial results, atomic catalog transactions and PostgreSQL conflict retries, typed workflow state, and a finite execution deadline. Fetched item metadata, HTML, ratings, and redirects stay in activity memory and may be fetched again on retry. Stable selections and prerequisite catalogs remain recorded Temporal inputs, with oversized bytes held by the codec.

Use the verification record in [the implementation plan](provider-workflow-plan.md) for current test results. Provider comparisons must check catalog content and stable identities through data-service interfaces, together with cancellation, retry, deletion evidence, and incomplete-listing behavior. Test notifications use a fake sink; tests must not send real operator alerts.
