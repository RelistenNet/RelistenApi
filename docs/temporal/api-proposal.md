# Proposal: typed provider workflows and explicit import operations

Status: historical design proposal, superseded on October 12, 2026 by the [implementation plan](provider-workflow-plan.md) and [contract reference](provider-workflow-contracts.md). No implementation of this new refactor has started. [Current implementation](architecture.md) · [Current provider behavior](provider-behavior.md)

The confirmed choices are separate provider workflows, shared activity composition for routine items, and explicit conflicts for overlapping artist requests. Large typed Temporal payloads use a Redis codec through StackExchange.Redis; idempotent catalog transactions replace snapshots and receipts. The Local importer returns one source per date group, correcting the multiple-source example below. Retain this proposal for its rationale and alternatives; follow the linked plan for implementation contracts.

Readers: Alec, Switz, and engineers implementing or reviewing Relisten ingestion. This proposal uses the [Explore, Planning, and Implementation templates](https://chatgpt.com/space/page_c2d80e22b8288191b4e8dbbe10ba26af). It is a design proposal with an implementation outline, not a finished execution plan.

The API should answer three questions without reading a base class: what can this provider import, what happens to one item, and what happens after all items finish?

Use concrete provider workflows to state the sequence. Share small generic data types and execution helpers. Remove the universal importer interface that requires every provider to prepare, discover, import, and reconcile in the same way.

## What is wrong with the current contracts

| Current member | What it does today | Proposed replacement |
|---|---|---|
| `BackfillJerryVenues` | Selects already imported Jerry mirror pages and repairs venue information instead of importing only missing setlists. | A named Jerry venue-repair operation. Other providers never receive it. |
| `RebuildOnly` | Skips provider imports. Recomputes derived shows, source durations/ratings, year summaries and related counts, then invalidates the artist cache. It does not fetch upstream data. | `RebuildArtistCatalog(ArtistId)`, implemented through the catalog data service. |
| `RequestId` | Supplies the namespace for saved inputs and commit receipts, and a correlation value for logs/results. It is not a source ID or Temporal's Run ID. | An internal `ImportExecutionId`, assigned once for a logical import. Callers receive a handle for status and cancellation. |
| `DiscoveryCursor` | Combines an upstream string cursor with an offset into a saved JSON array. | A provider's cursor type for upstream paging; a separate internal position for reading stored items. |
| `DiscoveredItem.Identifier` | Converts Archive identifiers, Phish.in integers and local source IDs into one string field. | `DiscoveredItem<TKey, TInput>` or a named provider record with a typed key. |
| `DeletionDecision` | Carries Archive-specific verification and deletion results through a shared result type. | Typed decisions, returned only by workflows that reconcile deletions. Archive owns its evidence and policy. |
| Default `ReimportItemAsync` | Returns zero counts without importing anything for six providers. | No default implementation. Expose a targeted operation only when it can perform that operation. Reject unsupported requests explicitly. |
| `ImportCheckpoint` | Makes the generic artist coordinator track provider preparation, paging, item outcomes and Archive's six-item deletion guard. | Small checkpoints owned by each concrete workflow. Archive's guard belongs to Archive. |

The existing `IngestionImporter<TItem, TDiscovery>` makes listing data typed, but the surrounding workflow still discards provider types. Adding more type parameters to that base class would preserve the wrong dependency: every provider would still have to fit its lifecycle.

## The caller requests one operation

Illustrative client calls:

```csharp
await imports.RefreshArtistAsync(new RefreshArtist(artistId));
await imports.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artistId));

await imports.Archive.RefreshAsync(new RefreshArchiveArtist(artistId, Year: 1977));
await imports.Archive.ImportItemAsync(new ImportArchiveItem(artistId, archiveItemId));

await imports.PhishIn.RefreshAsync(new RefreshPhishInArtist(artistId));
await imports.Jerry.RepairVenuesAsync(new RepairJerryVenues(artistId));
```

Each client method starts work and returns an `ImportHandle`. Awaiting the client method means admission completed, not that the import finished. The handle identifies the workflow and provides status/cancellation operations.

`RefreshArtist` runs the enabled provider imports in their existing order, then rebuilds the artist's derived catalog data. It has no provider-specific flags. The active-artist schedule chooses artists and constructs the appropriate provider requests; it does not assume every provider supports a year filter. Replace batch mode strings with named commands such as `RefreshCatalog`, `RefreshRecentlyActiveArtists`, `RebuildCatalog`, and `DiscoverArchiveArtists`.

The artist coordinator dispatches concrete provider requests through one explicit switch. If the plan needs heterogeneous requests, use a closed, explicitly serialized union at that dispatch point. Do not pass arbitrary JSON or `object` through provider activities. Provider-specific refresh client methods build a one-provider plan so they still get the final artist rebuild.

Provider options live on their provider request. For example, an Archive year filter restricts discovery and disables whole-artist absence reconciliation. Phish.in's current year argument is a lower year bound, not an exact-year filter; name it `FromYear` if exposed. Do not rename it `Year` and silently change that behavior. Phantasy Tour has no year-filter option until one is implemented.

`RepairJerryVenues` reads existing Jerry setlists, applies venue corrections, updates tour bounds, and removes unused venues only after the repair completes without item failures. It is a maintenance operation with its own inputs and result.

Keep existing HTTP route names, authentication, and catalog responses. Their adapters call the new client methods. Preserve the existing Archive targeted import. A new targeted operation for another provider requires an actual item loader and behavior tests; a successful no-op is not support.

## Use generics for data that actually varies

```csharp
public readonly record struct ArtistId(Guid Value);
public readonly record struct ArchiveItemId(string Value);
public readonly record struct PhishInShowId(int Value);
public readonly record struct CatalogSourceId(long Value);

public sealed record DiscoveredItem<TKey, TInput>(TKey Id, TInput Input);

public sealed record Page<TItem, TCursor>(
    IReadOnlyList<TItem> Items,
    TCursor? Next
) where TCursor : class;

public sealed record PhishInPage(int Number);
public sealed record SetlistFmPage(int Number);
public sealed record ArchiveCollectionCursor(string Token);
```

`Next == null` means discovery finished. An empty page can still have a next cursor. A cursor may contain a page number, opaque token, timestamp plus tie-breaker, or another provider-defined position. The shared helper passes it back without interpreting it. Reference-type cursor records make the nullable contract unambiguous in C#.

Examples of concrete return types:

```csharp
Page<DiscoveredItem<PhishInShowId, PhishinShow>, PhishInPage>
Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>
```

Archive recording discovery uses `rows=*` and returns a complete selection. It does not need an upstream page cursor. Large stored selections can be delivered to the workflow in bounded batches using an internal offset. Archive collection discovery is a different operation and has an upstream token cursor. Keep those two concepts separate.

A `SnapshotRef<T>` identifies a stored value of type `T`. A `StoredItemRef<TKey, TInput>` identifies one typed item inside a stored selection. They belong to activity/workflow transport, not to public import requests. Provider item methods receive the loaded `TInput`; they do not deserialize JSON or find array elements themselves.

Key values remain typed through requests, discovery, checkpoints, item outcomes, and reconciliation. Convert them to canonical text only for Temporal workflow IDs, receipt keys, logs, or existing database columns. One formatter per key type owns that conversion. Include artist and provider in the identity where the item ID is only locally unique. Keep the current provider enum and its exact string values.

## Read one provider's workflow to understand its execution

Recommended execution tree:

```text
RefreshArtistWorkflow
  ArchiveArtistWorkflow
    discover selected recordings and retain observed identifiers
    ArchiveItemWorkflow for each selected recording
      fetch related inputs, validate, and commit the recording
      additional item processing can follow here
    verify deletion candidates and apply permitted deletions
  JerryArtistWorkflow, if enabled for this artist
    import missing setlists
  rebuild derived artist catalog data
```

A Phish artist instead runs Phish.in and then Phish.net. Keep provider ordering: Phish.net enriches sources created by Phish.in. Provider workflows return counts and a partial/failure summary; item outputs do not accumulate unboundedly in the artist coordinator.

The top of `ArchiveArtistWorkflow` should read approximately as follows. These are workflow orchestration helpers, not direct HTTP/database calls:

```csharp
var discovery = await DiscoverRecordings(request);
var imports = await ImportSelectedRecordings(discovery);
var reconciliation = await VerifyAndReconcileRecordings(request, discovery, imports);
return Summarize(imports, reconciliation);
```

`ImportSelectedRecordings` owns bounded item iteration and history rollover. `VerifyAndReconcileRecordings` owns Archive's year scope, direct metadata verification, eligibility rules, and five-deletion limit. The reader should not need to find these rules in a generic artist workflow.

The equivalent Phish.in sequence is:

```csharp
var catalogs = await PrepareCatalogs(request.Artist);
return await ImportShowPages(request, catalogs);
```

Catalog preparation fetches and applies eras, tours, songs, and venues. The page loop uses `PhishInPage`. Each item imports one show, its recording and tracks using those catalogs. There is no invented reconciliation method that returns zero counts.

The item workflow contains the processing steps. Initially Archive needs one import activity:

```text
ArchiveItemWorkflow
  ImportArchiveRecording activity
    load saved discovery input, or resolve the explicitly requested identifier
    GET metadata and any related inputs
    validate the response
    commit catalog changes and their retry receipt together
  return the item's catalog references and outcome
```

The internal item-workflow input distinguishes `FromDiscovery` from `RequestedById`. The first carries a typed saved reference; the second carries the typed upstream ID. Do not use a null item or empty metadata string to select a different operation. If represented as a serialized union, declare its variants explicitly for the Temporal JSON converter; do not enable arbitrary CLR type deserialization.

Both entry paths run the same item workflow. A targeted-import coordinator resolves prerequisites, runs that workflow, and rebuilds the artist's derived data. The artist refresh performs the final rebuild once after all provider work. The item workflow does not choose whether to rebuild based on how it was invoked.

## Shared operations are composable

Separate provider workflows share catalog operations and ordinary C# helpers. They do not copy rebuild SQL or reimplement retry rules. Use these three levels:

| Level | Responsibility | Examples |
|---|---|---|
| Data service or ordinary function | Perform a catalog operation or transform data. No Temporal dependency. | `CatalogRebuildService.RebuildShows`, date parsing, source upserts |
| Shared activity | Run an operation with Temporal retries, cancellation and execution logging. | `CatalogActivities.RebuildShows`, `RebuildYears`, `InvalidateArtistCache` |
| Workflow helper | Compose activity calls in a named sequence. No direct database or HTTP access. | `CatalogSteps.RebuildArtist` |

The proposed shared activity methods take a typed `ArtistId`. Each resolves the artist and invokes the appropriate data-service operation. Their names describe effects; they do not accept a provider enum or an import-mode flag.

A workflow can use individual operations when their prerequisites are met. The normal finalization helper composes them in this order:

```csharp
internal static async Task RebuildArtist(ArtistId artist)
{
    await RebuildShows(artist);
    await RebuildYears(artist);
    await InvalidateArtistCache(artist);
}
```

This is a workflow-helper sketch. Each awaited method schedules the corresponding `CatalogActivities` method on the database task queue. It does not directly call a data service.

`RebuildShows` refreshes source durations/ratings, creates or updates derived shows, and associates sources with shows. `RebuildYears` then uses those show associations to rebuild years and related aggregates. The cache is invalidated after both operations succeed. Reordering these steps can leave derived data inconsistent.

The full artist refresh, targeted-item coordinator, and standalone `RebuildArtistCatalogWorkflow` use this same helper. Normal artist refreshes call it after all providers, not once per item. A new maintenance workflow can reuse the appropriate individual activity without implementing a provider importer.

The current [catalog rebuild service](../../RelistenApi/Services/Data/CatalogRebuildService.cs) already contains the SQL. Its `RebuildYears` method also invalidates the artist cache. The proposed change makes cache invalidation a separate service operation and activity. Keep the SQL's existing transaction ownership: rebuild operations must not be wrapped in an item receipt transaction.

If rebuilding years fails, Temporal retries that activity. The completed show rebuild remains completed in workflow history. If the process loses a database-commit acknowledgement, the rebuild may run again and must converge to the same catalog state. A standalone rebuild remains available after an import is canceled. Cache invalidation can retry independently.

Not every helper needs an activity or child workflow. Parsing, selecting items, converting typed IDs and calculating values remain ordinary functions. Named shared workflow helpers schedule durable operations; provider workflows state when those operations are required. Avoid a configurable step registry or another inheritance hierarchy for this composition.

## Each provider keeps its own item definition

| Provider | Item key | Input and operation | Work after enumeration |
|---|---|---|---|
| Archive | `ArchiveItemId` wrapping its string identifier | Search document and selection reason; fetch metadata and import one recording, sets, tracks, reviews and links | Verify absence/eligibility candidates before deletion |
| Phish.in | `PhishInShowId` wrapping its integer ID | Listing show with tracks plus prepared catalogs; import its recording and setlist | No separate provider cleanup |
| Phish.net | `CatalogSourceId` | Existing Relisten source plus matching API show; fetch ratings and required reviews, then enrich that source | No separate provider cleanup |
| Setlist.fm | `SetlistFmId` wrapping its string ID | Parsed setlist, including songs and venue; import setlist and preserve tour bounds | No separate provider cleanup |
| Phantasy Tour | `PhantasyTourShowId` wrapping its integer ID | Import details for missing shows; retain listed dates for existing shows | Repair dates and tour bounds after complete enumeration |
| Jerry mirror | `JerryShowId` wrapping its current identifier | Missing setlist page during ordinary import; existing page during the separate venue repair | Venue repair may remove unused venues after success |
| Local | `LocalShowDate` wrapping its current date key | All directories grouped under that date; fetch directory metadata and import the group | No separate provider cleanup |

One upstream item does not always equal one Relisten source. Local can import several directories in one date group. Setlist providers can produce setlists without an audio source. Results must retain these distinctions.

## Typed outcomes and deletion evidence

A successful item returns the catalog identities that later processing needs, not just `Created`, `Updated`, and `Removed` counters. For example, Archive returns its recording source ID and affected track IDs when needed; Setlist.fm returns its setlist ID; Local returns its group of recording source IDs. Include only identities consumed by the next stage. A derived show row may not exist until the artist rebuild, so do not promise a show ID before that operation.

Use explicit imported/unchanged/skipped outcomes with provider-specific skip reasons. Failed activities remain failures after retry exhaustion; a missing result is not an unchanged item. Aggregate counters are derived from these outcomes. Archive's no-MP3 outcome remains an Archive concern rather than a boolean on every provider result.

A shared decision type can be small:

```csharp
public enum DeletionAction { Keep, Delete, Review }

public sealed record DeletionDecision<TKey>(
    TKey Item,
    DeletionAction Action,
    string Reason
);
```

The provider evaluates evidence before constructing the decision. Archive uses its Present/Deleted/Unknown evidence and completeness checks. A future provider can use numeric or composite keys and its own evidence type. Do not use the same enum for an upstream observation, an operator-review requirement, and an executed deletion. The reconciliation result reports which authorized deletions actually committed. Logs and receipts retain the decision reasons.

A guarded whole-provider operation is a separate reconciliation outcome. Do not encode it as `Item = "*"` or another fake item key. No other provider acquires an absence-deletion rule by implementing a generic interface.

## Extend item processing without rewriting discovery

An item workflow gives a clear place for another durable step. For example, a future Archive item workflow could call:

```text
ImportArchiveRecording -> NormalizeSource -> BuildSourceEmbedding -> IndexSource
```

These are proposed future activities, not features in this migration. Simple deterministic mapping can remain inside the import activity. Separate steps when they need independent retries, expensive work, or different worker resources. Related GETs may remain together.

The import result supplies a source ID. Before adding normalization, define the exact source fields it consumes and a revision/hash for those fields. A future processing request identifies that input revision and a named processing version. Persist model/prompt/schema versions with generated results. An unchanged input with the same processing version reuses its result; a new processing version permits reprocessing without downloading the upstream listing again.

A standalone source-processing workflow calls the same processing sequence. Thus reimporting upstream data and reprocessing existing data are distinct operations. Do not add `RunEmbeddings`, `NormalizeWithAi`, or a `List<string> Steps` to every import request.

Reject or retry a result whose input revision changed before publication. Keep generated suggestions separate from canonical identity fields unless a reviewed policy permits applying them. Embedding model and index schema versions belong to the processing/index operation. Model choice, budget, approval behavior and index rollout remain future decisions.

If normalization fails after import commits, retry normalization independently. The recording remains imported. Report import and processing status separately so an AI failure cannot hide successful ingestion or authorize source deletion.

## Temporal mechanics stay small and explicit

Keep named concrete workflow entrypoints and named concrete activity methods with closed argument/result types. Generic records and ordinary C# helpers can supply shared behavior. The .NET SDK requires an explicit `[WorkflowRun]` method on each registered workflow; do not depend on discovering open generic workflows or reflection-based provider execution. See the [SDK workflow definition guidance](https://github.com/temporalio/sdk-dotnet#workflow-definition).

An internal `ImportExecution` contains an `ImportExecutionId` and deadline. Generate it once for the logical artist operation, or once for a standalone item operation. Carry it through its provider/item children and ContinueAsNew checkpoints. Receipt keys additionally include artist, provider, typed item key and operation. A new intentional import gets a new execution ID; retrying the same operation retains it. Temporal's per-run ID must not replace this stable identity across ContinueAsNew.

Workflow identity and write receipts answer different questions. The workflow ID controls overlap; the receipt prevents a repeated activity from applying a committed operation again. Keep the existing per-artist admission policy for refreshes and targeted import coordinators. Do not silently return a full-refresh handle as if a different requested item operation was accepted. Report that conflict explicitly. Supporting concurrent manual-item and artist-refresh work is a separate admission change, not required by typed APIs.

Keep ten active artist children per batch and ten concurrent activities per provider. Start with the current sequential item processing within each artist. Provider/item children do not need another custom concurrency controller. All upstream activities still use their provider's task queue.

Each provider owns its checkpoint: stage, typed cursor or saved-selection position, completed item count, and bounded summary. ContinueAsNew only after active item children finish. Parent cancellation requests child cancellation and waits for in-flight work as appropriate. A retry uses the same receipt identity. A finished item failure can produce a partial provider result; cancellation and expired work must propagate. Keep structured provider/item/execution logs and explicit skip reasons. The operator must be able to distinguish an upstream import failure from a later processing failure. See [.NET child workflows](https://docs.temporal.io/develop/dotnet/workflows/child-workflows) and [ContinueAsNew](https://docs.temporal.io/develop/dotnet/workflows/continue-as-new).

Snapshots and receipts remain implementation details. Keep full item HTTP responses in activity memory. Type safety does not require a new JSON row per item or a permanent artifact store. The separate proposal to shorten successful-run retention remains unimplemented.

## Code and documentation an implementer can follow

Group each provider's contracts, workflows and activities under its provider directory. Start reading `ArchiveWorkflows.cs` or `PhishInWorkflows.cs`. Put shared retry policy, typed snapshot loading, receipt transactions and bounded iteration helpers together in the ingestion infrastructure directory. Catalog SQL remains in data services.

Use a short provider guide with: entrypoints, execution sequence, item identity, selection rules, database effects, skip/failure behavior, and a concrete reimport example. Link each step to its method. Do not describe an empty method as a supported operation or make readers infer behavior from “legacy.”

For example, replace the current no-op method documentation with a capability statement in the guide: “Archive supports importing one recording by identifier. Other providers currently support artist refresh only. The client rejects an unsupported targeted-import request.” No method needs to exist merely to carry that comment.

A useful import-method comment states the contract: “Imports one Archive recording and returns its source ID. Reuses the committed result when the activity retries. Throws when the response is invalid. Returns a skipped outcome when the recording is dark or its date cannot be repaired.” Explain required prerequisites and side effects in the same method's documentation. Use short sentences, stable terms, and concrete subjects; do not claim full ASD-STE100 compliance without checking its vocabulary.

## Recommendation and implementation sequence

Two designs are credible. A universal `Importer<TKey, TCursor, TInput, TState, TResult>` would provide one loop, but would keep optional capabilities and provider-specific checkpoints hidden in generic machinery. Concrete provider workflows duplicate a few visible calls while allowing each provider to state its real operations. Recommend the latter, with shared generic helpers only for repeated mechanics.

Recommend an item child workflow now because single-item reimport and future independent processing are stated goals. Initially it can contain one import activity. The cost is more workflow histories and registrations. An alternative is to keep item activity composition until a second durable step exists; the typed provider contracts work with either choice. This choice remains part of the proposal, not an accepted change.

If accepted:

1. Introduce typed commands, keys, outcomes and internal execution identity. Remove mixed-purpose flags and silent default item imports. Preserve existing external catalog/HTTP contracts; document explicit admission errors.
2. Convert Archive first. Make discovery, item import and deletion verification visible. Verify typed payload serialization and checkpoints, direct reimport, overlap reporting, skip reasons, item failures, cancellation, receipts and deletion evidence.
3. Convert Phish.in and Phish.net together. Verify catalog prerequisites, page-number cursors, source identity, enrichment ordering and targeted capability validation. Then convert the remaining four providers using their current domain behavior.
4. Replace global provider/item checkpoint plumbing with bounded typed helpers. Test continuation after partial pages, empty pages with continuation, opaque cursors, and cancellation while children are running. Verify that final rebuild runs after successful/partial imports as intended and that it is independently recoverable after cancellation.
5. Rewrite provider guides against the resulting code. Run seven-provider behavior comparisons, receipt/cancellation tests, workflow replay/rollover checks, and formatting. Remove the superseded universal base and contracts.

This branch has never deployed, so the internal workflow/storage contracts need no compatibility layer. Keep logical commits and existing behavior tests while replacing them. No deployment, production schema change, AI feature, or indexing rollout is included in this proposal.

## Evidence and remaining planning work

Observed behavior comes from [current contracts](../../RelistenApi/Services/Ingestion/IngestionContracts.cs), the [generic importer](../../RelistenApi/Services/Ingestion/IngestionImporter.cs), the [artist workflow](../../RelistenApi/Services/Ingestion/Workflows/ArtistRefreshWorkflow.cs), and the [catalog rebuild service](../../RelistenApi/Services/Data/CatalogRebuildService.cs). The provider behavior guide links the current domain methods. Proposed types and execution trees above describe the replacement; they are not observations about deployed code.

Fresh review against the prompt templates:

- Accepted: separate provider workflows and reusable operations. These fit the existing data services and the separate worker. Keep SQL in data services and fixture transport in tests.
- Still proposed: item child workflows now versus activity composition until another durable step exists. Extra histories are the principal cost. Activity composition would become preferable if independent item orchestration no longer provides value.
- Planning must specify concrete serialized request/result variants, provider checkpoints, admission/conflict behavior and the initial targeted-import capability set. The implementer should not infer these from examples. Changing completed-run retention remains a separate proposal.
- Add shared-operation verification: rebuild after partial import, correct show/year associations and counts, retry after a lost commit acknowledgement, year-rebuild failure followed by retry, and eventual cache invalidation. Verify through resulting catalog data and workflow behavior, not assertions about helper names or configuration.
- Before implementation, map these contracts to coherent commits and confirm dependencies. The branch is undeployed, so there is no running-workflow compatibility migration. Preserve external catalog/API contracts and the existing [Hangfire handoff and rollback procedure](operations.md). A local refactor can be rolled back by reverting its commits; deploying it remains outside this task.
- The final plan needs one more review after the remaining decisions are resolved. Check that each provider's sequence is readable, shared utilities remove actual duplication, and every observable behavior change has a contract test.
