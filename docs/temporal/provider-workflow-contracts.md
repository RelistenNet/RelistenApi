# Provider workflow contracts

This reference defines the implemented contracts described by [the implementation plan](provider-workflow-plan.md), October 12, 2026. Read [the provider guide](provider-behavior.md) for links to concrete workflow, activity, and importer entrypoints. Private helper names can vary; the responsibilities and observable behavior below remain the contract.

## 1. Requests, admission, and handles

An artist command uses the catalog artist UUID. API/CLI adapters resolve a numeric artist ID or slug through `ArtistService` before constructing it. Internal catalog data services can continue to use numeric IDs. Callers do not supply an execution ID, deadline, checkpoint, or storage reference.

```csharp
public readonly record struct ArtistId(Guid Value);
public readonly record struct ArchiveItemId(string Value);
public readonly record struct PhishInShowId(int Value);
public readonly record struct SetlistFmId(string Value);
public readonly record struct PhantasyTourShowId(int Value);
public readonly record struct JerryShowId(string Value);
public readonly record struct LocalShowDate(string Value); // Existing date key, not a timestamp.
public readonly record struct CatalogSourceId(long Value);
public readonly record struct CatalogSetlistId(long Value);

public sealed record RefreshArtist(ArtistId Artist);
public sealed record RefreshArchiveArtist(ArtistId Artist, int? Year = null);
public sealed record RefreshPhishInArtist(ArtistId Artist, int? FromYear = null);
public sealed record RefreshPhishNetArtist(ArtistId Artist, int? Year = null);
public sealed record RefreshSetlistFmArtist(ArtistId Artist);
public sealed record RefreshPhantasyTourArtist(ArtistId Artist);
public sealed record RefreshJerryArtist(ArtistId Artist);
public sealed record RefreshLocalArtist(ArtistId Artist);
public sealed record ImportArchiveItem(ArtistId Artist, ArchiveItemId Item);
public sealed record RepairJerryVenues(ArtistId Artist);
public sealed record RebuildArtistCatalog(ArtistId Artist);
public sealed record RefreshCatalog();
public sealed record RefreshRecentlyActiveArtists();
public sealed record RebuildCatalog();
public sealed record DiscoverArchiveArtists();
```

`TemporalIngestionClient.RefreshAsync` accepts any named `ArtistRefreshCommand`, including `RefreshArtist` and provider-specific refreshes. Maintenance and catalog operations use `ImportArchiveItemAsync`, `RepairJerryVenuesAsync`, `RebuildArtistCatalogAsync`, `RefreshCatalogAsync`, `RefreshRecentlyActiveArtistsAsync`, `RebuildCatalogAsync`, and `DiscoverArchiveArtistsAsync`. Each takes its named request above. Artist starts return `Task<ImportHandle<ArtistImportResult>>`; catalog batches return `Task<ImportHandle<ImportBatchResult>>`; collection discovery returns `Task<ImportHandle<int>>` with its processed count. The client owns admission directly; there are no forwarding provider clients or plugin registry.

Provider-specific refreshes enter the same artist ownership/admission rule and rebuild once after that provider completes. `RefreshArtist` obtains the artist's enabled mappings and runs their concrete workflows in the existing order; preserve Phish.in before Phish.net. The dispatch point may use one explicit switch. It must not send `object` or JSON strings to provider activities.

### Admission rules

- Every artist operation uses workflow ID `ingest/artist/{lowercase-canonical-uuid}`. Active conflict policy is Fail; completed workflow IDs can be reused for a new intentional operation. Do not use `UseExisting` or terminate an active execution. Temporal start is the authoritative race check.
- Resolve artist existence, supported operation, and required mapping before admission where possible. Activities re-resolve current catalog state and fail explicitly if the artist/mapping disappeared. Unknown historical provider strings remain readable in catalog APIs but cannot select ingestion work. PanicStream is unsupported.
- On active conflict, throw `ImportAlreadyRunning` containing the workflow ID. Do not return an `ImportHandle` for the requested operation. Batch callers count a conflict separately and continue; they do not attach to or cancel somebody else's workflow.
- Invalid key/year/provider syntax produces `InvalidImportRequest`. Missing artist produces `ArtistNotFound`; missing requested provider mapping or a non-Archive targeted request produces `UnsupportedImportOperation`. Validate years as integers 1–9999; `LocalShowDate` preserves the actual upstream key without timezone conversion. Never interpret a missing item as a full refresh.
- An ambiguous start transport error is not success. Propagate the admission error; a retry can discover an existing active workflow as a conflict. This scope does not add caller-supplied idempotency tokens.
- Catalog-wide manual requests start separate batch IDs, as they do now. Their artist children still obey artist exclusivity. Schedules use their fixed IDs and `Skip` overlap policy.

`ImportHandle<TResult>` retains the SDK handle returned by start, including `WorkflowId`, `ResultRunId`, and `FirstExecutionRunId`. `GetResultAsync` starts at `ResultRunId` and follows continuation runs. `CancelAsync` supplies `FirstExecutionRunId`, which prevents canceling a later independently started operation with the same workflow ID. Those fields have different SDK meanings; `FirstExecutionRunId` does not restrict Describe. `DescribeAsync` starts with the explicit admitted run and follows only recorded ContinueAsNew completion links to describe the chain's current run. It never treats the latest execution for the workflow ID as proof of chain membership. A missing/expired history returns an explicit not-found result. A completed result distinguishes full success from partial item failures. No execution table or new public HTTP status endpoint is required. See the [SDK handle contract](https://dotnet.temporal.io/api/Temporalio.Client.WorkflowHandle.html).

### Existing HTTP and CLI adapters

Preserve route names, authorization, missing-artist responses, and successful body shapes in `Controllers/Import/ImportController.cs` and `Controllers/Admin/AdminController.cs`. Import routes that currently return `201` with `Queued as job {id}!` retain it; Phish.in/rebuild routes retain their existing ID body, and admin routes retain their JSON message. The ID is the admitted Temporal workflow ID.

For Temporal admission conflicts return HTTP `409` with `{ "code": "import_already_running", "workflowId": "ingest/artist/...", "message": "An import or rebuild is already running for this artist." }`. Invalid requests return `400` with code `invalid_import_request`. Unsupported targeted/provider operations return `400` with code `unsupported_import_operation`. Both error objects also contain a plain-language `message`; they do not contain a job ID. Preserve existing 409 responses for synchronous debug and destructive `deleteOldContent=true` routes when Temporal is enabled. Leave Hangfire-mode behavior unchanged.

`POST {artist}/{showIdentifier}` maps only to Archive's typed item request and requires an Archive mapping. It cannot silently invoke six no-op importers. A targeted import never enumerates or reconciles unrelated recordings. A requested item missing from the specified Archive collection is a nonretryable item failure; admission only validates the local artist/mapping.

Keep the worker CLI's existing `--import`, `--provider`, `--item`, `--year`, `--batch`, `--register-schedules`, and `--cancel` spellings as adapters. Print the workflow ID on admitted work. Report conflicts and validation errors on stderr with a nonzero exit code. `--item` implies Archive unless `--provider=archive.org` is explicit; reject other providers and reject `--item` with `--year`. `--year` alone constructs the legacy filtered artist plan: Archive exact year, Phish.in lower bound, Phish.net exact year, and the other providers' normal unfiltered refresh. With an explicit provider, accept year only for Archive/Phish.in/Phish.net; reject meaningless filters. Add `--rebuild-artist=<id>` and `--repair-jerry-venues=<id>` for the explicit recovery/maintenance commands. Mutually exclusive operation flags are invalid. The existing workflow-ID-only cancel command intentionally targets the current active execution and prints that fact; the programmatic handle is chain-bound.

## 2. Wire representation and result meaning

Use the installed Temporal default System.Text.Json payload converter. Keep current `UpstreamProvider` wire values exactly (`archive.org`, `phish.in`, `phish.net`, `setlist.fm`, `phantasytour.com`, `jerrygarcia.com`, `local`, `panicstream.com`). Do not serialize enum ordinals. Provider catalog API `name` fields remain unchanged.

Provider HTTP parsers still use their existing Newtonsoft.Json rules. Do not assume those attributes control Temporal serialization. In particular, Archive `SearchDoc` has computed date getters that can throw for incomplete metadata. Add explicit System.Text.Json ignore annotations for computed/derived members that should not travel, preserving the existing Newtonsoft behavior; carry the underlying raw fields needed to reconstruct the provider input. If a vendor type contains arbitrary `object` members or cannot round-trip without executing parsing logic, map its consumed fields into a plain provider input record instead. Do not introduce a general serialization framework. Test null/missing optional values and partial-date strings as well as normal fixture values through the actual converter before adopting each type.

New key wrappers serialize as `{ "Value": ... }`; GUID values use canonical strings, numeric upstream IDs remain numbers, and Local's date key remains a string. New records retain their C# property names on the Temporal wire. Use explicit string enum conversion for new disposition/stage enums. Existing HTTP bodies above are independent of this internal representation.

Where a workflow receives multiple request variants, declare a closed System.Text.Json union using a `kind` discriminator and explicit registered concrete records. The artist refresh input supports `artist`, `archive`, `phish-in`, `phish-net`, `setlist-fm`, `phantasy-tour`, `jerry`, and `local`; the filtered CLI/batch artist plan is an internal `filtered-artist` variant carrying its integer year. Targeted Archive, rebuild, and Jerry repair have their own coordinator inputs, rather than flags on that union. Archive item input variants are `discovered` (typed selected document/reason) and `requested` (typed identifier). Unknown `kind` values fail deserialization. Never enable arbitrary CLR type-name deserialization. Declare the concrete discriminators and test them before migrating activities.

Use small shared data types:

```csharp
public sealed record DiscoveredItem<TKey, TInput>(TKey Id, TInput Input);
public sealed record Page<TItem, TCursor>(TItem[] Items, TCursor? Next)
    where TCursor : class;
public sealed record PhishInPage(int Number);
public sealed record SetlistFmPage(int Number);
public sealed record PhantasyTourPage(uint Number);
public sealed record ArchiveCollectionCursor(string Token);
```

`Next == null` means enumeration is finished. An empty page with a next cursor is valid. Shared iteration never interprets a cursor's contents. Archive recording discovery has no upstream cursor: `rows=*` produces one complete selection. A local index into that selection is a workflow checkpoint, not an Archive API cursor.

Successful provider item methods return a typed key and the catalog identity stated in section 3. They distinguish `Applied` (successfully processed, with no claim about number of SQL changes), `Unchanged` (an existing identity satisfied the provider's current timestamp/content rule), and `Skipped` where supported. Use provider result records with explicit `Disposition`, identity fields, and a provider-specific nullable skip enum when needed. Applied/Unchanged require their catalog identity; Skipped requires its reason and may carry an existing identity. Do not add fake skip variants to providers that have none. Archive's skip enum must distinguish missing/invalid metadata, dark item, unrepaired date, and no VBR MP3; preserve the detailed diagnostic messages and any finer existing reason from its shared validation method. Upstream transport errors or malformed required listing pages are failures, not skipped or unchanged items.

Results do not contain a derived show ID before the rebuild. Do not accumulate every successful item identity in the artist/batch result: each item result exists in its provider workflow history and is available immediately to future composed processing steps.

`ProviderSummary` and final `ArtistImportResult` contain `CompletedItems`, `SkippedItems`, and `FailedItems`; completed includes Applied and Unchanged and excludes skips. Final status is `Completed` or `Partial` based on failed items. A zero-selection import completes and still rebuilds. Attempt-local `ImportStats` can remain in diagnostic logs but is not promised as exact per-execution Created/Updated/Removed accounting. A retry result is counted once, after its activity completes in workflow history. Cancellation/expiration/preparation failure is not a successful partial result.

Keep at most 20 structured failed-item details in a provider summary (`typed key`, stable error category, short message) plus the complete failure count; Temporal history and logs retain the rest. A batch reports `CompletedArtists`, `PartialArtists`, `FailedArtists`, and `ConflictingArtists`, counting each selected artist once. A failed child is not described as an overlap unless Temporal returned the conflict category.

## 3. Concrete provider sequences and ownership

Every provider child inherits its artist's logical deadline and returns a bounded summary. Only the artist coordinator rebuilds derived data after provider completion. Each provider workflow admits up to ten item activities concurrently. This is separate from the batch limit of ten active artists and the worker default of ten concurrent activities per provider queue. Worker limits apply across all workflows on that worker, including a single large artist.

| Provider and workflow file | Sequence, typed data, and catalog effect |
|---|---|
| `Archive/ArchiveWorkflows.cs` | `DiscoverRecordings → ImportSelectedRecordings → VerifyDeletionCandidates → ApplyVerifiedDeletions`. Discovery returns typed Archive documents with selection reasons and all observed identifiers. Item key `ArchiveItemId`; result source ID. Share current `FetchItemMetadata`/`ImportSingleIdentifier` parsing and date repair. Targeted coordinator uses the same item operation with `requested` input and omits reconciliation. `Year` is exact and also disables reconciliation. |
| `PhishIn/PhishInWorkflows.cs` | `PrepareCatalogs → ImportShowPages`. Preparation fetches eras, tours, songs, venues, commits their catalog rows, and returns typed era/year and song mappings needed by items. `Page<DiscoveredItem<PhishInShowId, PhishinShow>, PhishInPage>` uses existing 20-item pages. Full refresh moves forward; `FromYear` retains the current first-page probe, jump to last page, backward iteration and lower-date-bound filtering. One show returns source ID plus setlist ID. Reload current catalog rows inside the item transaction and restore the typed prepared mappings. |
| `PhishNet/PhishNetWorkflows.cs` | `SelectSourcesAndMatchingShows → EnrichSelectedSources`. Run after Phish.in. Selection contains `CatalogSourceId` and optional typed Phish.net show metadata; exact `Year` filter matches current behavior. An item fetches ratings and conditionally reviews, then reloads the current source inside its transaction and applies enrichment without overwriting other fields from a stale object. Result is the same source ID. Related redirects/GETs share the activity. |
| `SetlistFm/SetlistFmWorkflows.cs` | `ReadSetlistPages → ImportSetlists`. Key `SetlistFmId`; input parsed `Setlist`; cursor `SetlistFmPage`. Preserve the current exclusion of setlists with no sets and tolerant parser behavior. Upsert the setlist and performances; retain prior tour bounds when extending dates. Result setlist ID. No provider-wide reconciliation or year option. |
| `PhantasyTour/PhantasyTourWorkflows.cs` | `ReadShowPages → ImportMissingShowDetails → RepairListedDatesAndTours`. Key `PhantasyTourShowId`; input listing; cursor `PhantasyTourPage`. Existing shows require no detail GET. Retain typed local calendar dates for all listed past shows across pages; use the latest observed date for one ID. Repair dates/tour bounds only after complete enumeration, even if an individual missing-show detail import failed, matching current behavior. Discovery failure suppresses repair. Result setlist ID for imported items. |
| `Jerry/JerryWorkflows.cs` | Ordinary refresh: `SelectMissingPages → ImportPages`; key `JerryShowId`, typed file metadata, result setlist ID. Separate repair: `SelectExistingPages → RepairVenues → RefreshTourBoundsAndRemoveOrphans`. Preserve successful item's venue/tour updates even if another item fails, but remove orphan venues only if all repair items succeeded. Neither operation has a year option. Keep no-Last-Modified fallback consistent with current parser; timestamps can advance on refetch and are not an exactly-once identity. |
| `Local/LocalWorkflows.cs` | `FetchRootAndPrepareVenues → SelectChangedDates → ImportDates`. Keep the typed root result for selection instead of fetching it again. Key `LocalShowDate`; input all `LocalShow` directory records for that date. Fetch each directory's metadata before the write transaction. Merge the date's directories into **one source**, its set and tracks. Return one source ID. Preserve the current change-selection rule and UUIDs; no year option. |

The initial provider guide must list these entrypoints and link each visible workflow step to its activity/domain method. Shared activities such as `RebuildShows`, `RebuildYears`, and `InvalidateArtistCache` call data services. Shared workflow helpers schedule those activities; ordinary parsing/mapping functions remain ordinary code.

### Archive deletion is a separate domain policy

The earlier incident showed that a self-consistent partial upstream listing can still be incomplete. Preserve the existing completeness checks and direct metadata evidence independently of Temporal's execution status. Absence alone never permits deletion.

`VerifyDeletionCandidates` takes the fixed discovery’s observed IDs and at most
six `ArchiveItemId` values for stored recordings proved to have no VBR MP3. For
these skips, the item result includes the existing `CatalogSourceId` when present.
An upstream item with no stored recording cannot become deletion evidence.

Verification loads the current catalog candidates and applies the five-candidate
guard. Six existing recordings are enough to trip it. Allowed candidates missing
from the listing receive direct upstream verification. The typed result contains
an overall `Allowed`, `Guarded`, or `NotApplicable` disposition and at most five
item decisions when allowed. A guard is an overall result, never a fake `"*"` item.

Each decision names `ArchiveItemId`, the evidence (`Present`, `Deleted`, `Unknown`, or `NoVbrMp3`), action (`Keep` or `Delete`), and reason. Only the existing upstream errcode 104 establishes Deleted. A 404, empty response, or dark flag alone cannot. No-VBR-MP3 evidence comes from the item metadata decision, as today. Item failures do not imply deletion and do not manufacture no-MP3 evidence; the current independent verification policy remains in force.

`ApplyVerifiedDeletions` accepts only that fixed allowed list and deletes its approved identifiers transactionally. It never enumerates another candidate list on retry. Return the fixed verified decisions and identifiers now absent, rather than an exact first-attempt delete count. Guard notifications remain best-effort diagnostic side effects and can repeat on an activity retry; no exactly-once notification store is introduced. Normal logging must preserve the guard even if the external notification fails.

Compatibility limitation: the existing candidate selection considers all artist sources, and source identity is not provider-qualified. Preserve it here rather than silently changing catalog ownership. Existing removal behavior for sets/tracks/links/reviews stays inside `SourceService` and existing constraints; verify the resulting public catalog response.

## 4. Transactions and idempotency

`CatalogImportTransaction.RunAsync<T>(DateTimeOffset deadline, CancellationToken token, Func<Task<T>> apply)` owns one compound write. Use an ambient `TransactionScope` with async flow enabled, ReadCommitted isolation, and a five-minute timeout. There is no artist-wide advisory lock. Fetch HTTP inputs before entering the transaction and reload mutable catalog rows inside it.

Each callback makes sequential data-service calls. Npgsql reuses the enlisted physical connection when wrappers are opened and closed sequentially; do not open concurrent SQL connections or use `Task.WhenAll` inside one transaction. Different items use separate transactions and can run concurrently. `DbService` routes ambient reads to the primary. Errors leave the scope uncompleted and roll back. Existing Required scopes join the same ReadCommitted transaction. [Npgsql documents sequential ambient-transaction connection reuse](https://www.npgsql.org/doc/basic-usage.html#systemtransactions-and-distributed-transactions).

Check cancellation and the inherited deadline before applying writes and immediately before `Complete`. Transaction-local statement timeouts are bounded by the remaining deadline and the five-minute limit. Existing Dapper calls do not all accept cancellation tokens, so an in-flight statement can take time to finish.

Existing unique constraints and conflict handling preserve catalog identities. PostgreSQL deadlocks (`40P01`) and serialization failures (`40001`) propagate to Temporal's existing bounded activity retries. Each retry creates a fresh scoped importer and transaction, including fresh HTTP inputs and mutable lookups; do not retry only the SQL callback with stale dictionaries. Phish.in prepares eras before its item activities, so era creation does not require an artist-wide item lock. Artist workflow admission prevents overlapping artist operations; it does not coordinate a simultaneous Hangfire engine. Drain the old engine during handoff.

The data services keep ownership of UUID formulas, conflict targets, inserts/updates, dependent rows, and deletions. In particular:

- Source UUID: existing artist plus upstream identifier. Track UUID: existing artist plus MP3 URL. Source-set UUID: source UUID plus existing V1/V2 namespace and index. Do not regenerate numeric IDs or adopt a new UUID scheme.
- Setlist insert must resolve an existing `(artist_id, upstream_identifier)` conflict and return its ID before applying performances. Do not rely on an old preloaded dictionary to prove the row does not exist.
- Venue and tour creation uses `INSERT ... ON CONFLICT DO NOTHING`, then reads the existing row in a second statement if insertion lost the race. The tables have both UUID and natural-key uniqueness; targeting only one constraint can still fail on the other. The second ReadCommitted statement sees the winner's committed row. Existing metadata remains unchanged. Setlist.fm and Jerry extend tour bounds atomically with `LEAST`/`GREATEST` so an item cannot overwrite another item's wider range.
- Phish.net locks and reloads the source row with `FOR UPDATE` before applying fetched enrichment or replacing reviews. Distinguish “reviews not fetched” from a fetched empty list. If the current source/ratings comparison now requires reviews that were not fetched, throw retryable `EnrichmentInputsChanged` before any writes; the transaction rolls back and the next activity attempt refetches outside the lock. Do not erase reviews with an empty result that meant “not fetched.”
- Retry-safe item results look up the current catalog identity even when the provider timestamp check produces no writes. The original acknowledgement is not recoverable without a receipt; do not pretend otherwise.
- Artist collection indexing also needs receipt-free idempotency. Split fetch/validate of a typed collection page from transactional application so application retries use the recorded page and next cursor. Preserve the current page-wide ambient transaction, with one separate advisory lock for Archive collection indexing acquired before loading artists/mappings. Artist creation can precede knowledge of its numeric ID, and slug allocation examines other artists, so an artist-specific lock cannot protect this operation. The indexer must create an artist and its upstream mapping in the same transaction; a repeat then finds that mapping and skips creation. Return processed count and the recorded cursor, not exact first-attempt creation counts. Test overlapping applications of the same collection and distinct collections with colliding titles/slugs. Share the ordinary transaction mechanics with the item runner; only collection-page application acquires this advisory lock.

Rebuild operations keep their own SQL transaction ownership and are idempotent activities. `CatalogSteps.RebuildArtist` schedules shows, then years, then cache invalidation on `ingest-db`. Extract cache invalidation from `RebuildYears`; update both Temporal and Hangfire call sites. The existing rebuild SQL has some incremental aggregate work after COMMIT, so failure can leave derived data partially refreshed. Repeating the activity must converge; do not claim all three activities form one transaction.

## 5. Execution state, failure behavior, and schedules

Assign a logical execution ID once using workflow-safe GUID generation and an absolute deadline of seven days after server admission (`Workflow.Info.WorkflowStartTime`), including queue delay before the first task. Carry both through ContinueAsNew and into child/activity inputs. A batch's children inherit its deadline, and provider children inherit the artist's. Set remaining execution time when starting children; never grant each continuation/child a fresh seven days. The execution ID supports correlation only; it is not a catalog key or a required caller parameter.

Checkpoint only work that must cross a run boundary. Fixed-selection providers retain the selection, next item, summary, and any cleanup state. Paged providers (Phish.in, Setlist.fm, Phantasy Tour) finish one page before ContinueAsNew and retain only the next cursor, summary, prepared catalogs, and required accumulated evidence. They do not checkpoint a current page, item index, or stage. The page result remains in the current run's Temporal history, so replay after a worker restart still uses that result. A null next cursor ends enumeration; an empty page with a next cursor does not.

Carry a batch's original UUID selection and next index across rollover; do not select again from a changing catalog. Set the thin batch's filter year once at selection time in UTC, and preserve it across New Year/rollover. Phantasy Tour retains its date map for final repair; a later observation of the same show replaces its earlier date. Upstream pages can change during an import, and a later sync can repair missed or stale metadata. Archive retains its original observed IDs and up to six no-MP3 candidates for independent deletion checks. Do not keep a full list of successful item outcomes.

Keep a retained immutable selection or prerequisite catalog in its own payload argument, separate from changing progress. For example, Archive continuation takes `(request, selection, progress)`; batch continuation takes `(request, selectedArtists, progress)`. The selection serializes to the same bytes each time, so the codec reuses its content key. An item needing prepared catalogs takes `(context, preparedCatalogs, selectedItem)` rather than embedding those catalogs in every changing item envelope. Do not combine a multi-megabyte selection with a changing index/counter and then expect content hashing to deduplicate it. Growing provider evidence such as the Phantasy Tour date map can still produce new bytes; measure that separately in rollover tests.

Consider ContinueAsNew after 100 completed activity calls or when the SDK recommends it. Fixed-selection providers process segments of at most 100 items with up to ten activities in flight. Drain the complete segment before advancing the saved position or continuing. Paged providers continue between pages, so one run can exceed the threshold by one upstream page. Phish.in requests 20 shows per page; Phantasy Tour uses 100; Setlist.fm supplies its own page size. If a provider starts returning pages large enough to threaten Temporal history limits, revisit that provider's page size based on measurements. For batch work retain the existing 250-artist segment and drain all active children before continuation. Provider parents wait for their child through its continuation chain. No live child/activity is abandoned by rollover. Use RequestCancel parent-close policy and wait-for-cancellation-completion behavior where supported; cancellation stops new item scheduling and propagates instead of becoming a partial result.

Keep current activity limits unless a focused test demonstrates that an operation needs a different bound: five-minute attempt, six hours including queue/retry delays, five attempts, ten-second heartbeat, and wait for cancellation completion. HTTP has a 60-second request timeout and 64 MiB response buffer. Respect positive Retry-After for 429/5xx. Activity heartbeat lifetime includes HTTP and database waits. Expired execution is a named nonretryable failure; cancellation checks happen before commit. Ordinary exhausted item failures are counted and the provider continues. Discovery/preparation failures stop the provider before its reconciliation or cleanup. After an ordinary provider failure, the artist coordinator rebuilds committed catalog data, then propagates the provider failure. Failed artist preparation, cancellation, and expiration do not start rebuilding. A failed rebuild fails the artist operation, even if item imports committed.

Final rebuild happens once after provider children settle, including partial results and ordinary provider failure. This restores show/year associations for recordings committed before a later provider outage; the failed import remains failed. Rebuilding does not run in a canceled workflow's finally block. A standalone rebuild or fresh artist import recovers derived data left by cancellation, expiration, or a failed rebuild. No receipts, database progress journal, or custom resume command exists.

Use existing queue names: `ingest-workflows`, `ingest-db`, `ingest-archive`, `ingest-phishin`, `ingest-phishnet`, `ingest-setlistfm`, `ingest-phantasytour`, `ingest-jerry`, and `ingest-local`. Register each provider's activities only on its queue; pure catalog shared activities use `ingest-db`. Keep default ten concurrent activities per worker queue, optional native activities/second limit, and disabled eager activity execution. Multiple worker hosts multiply per-worker concurrency; one host remains the operating assumption.

Schedules remain UTC: Archive collection discovery 06:30; full catalog 07:00; thin/recently-active at 00:00, 02:00, 04:00, 12:00, 14:00, 16:00, 18:00, 20:00, and 22:00. Preserve current `ArtistService.UuidsForImport` membership criteria and provider mapping order. Existing schedules, including paused ones, remain unchanged when registration is repeated. Schedule arguments contain only the small named command; selections and logical deadlines are created when execution starts.

Log execution ID, artist, provider's existing string value, typed item key rendered canonically, workflow/activity IDs, attempt, disposition/skip reason, and exhausted failure category. Keep the shared importer event diagnostics available without Hangfire's context. Do not add PII-enabled Sentry configuration or secret-bearing HTTP query logs.

## 6. Redis payload codec

Implement `Temporalio.Converters.IPayloadCodec` with StackExchange.Redis. The optional `Temporal__PayloadRedisUrl` setting selects a separate payload connection, owned and disposed by the client registration. Without it, registration borrows the existing catalog-cache connection and does not dispose that connection. API clients and workers must use the same payload URL and namespace. One encoder result corresponds to one original Temporal `Payload`; preserve order and count and do not mutate caller-owned payload objects. The codec sees protobuf payloads, after JSON serialization. It stores both `Metadata` and `Data`, not just JSON data bytes.

Fixed initial policy:

| Property | Contract |
|---|---|
| Inline threshold | Full protobuf `Payload` serialized size at most 32 KiB remains unchanged. Larger values are offloaded. This is a local choice, not a Temporal limit. |
| Maximum original size | 64 MiB per serialized payload; reject a larger value with `PayloadTooLarge` before Redis write or unbounded allocation. Do not truncate or silently chunk it. |
| Redis key | `relisten:temporal:<namespace>:payload:v1:<sha256>`, with an unambiguous escaped namespace component; SHA-256 covers the complete stored protobuf bytes. |
| Reference payload | `Metadata["encoding"] = "binary/relisten-redis-v1"`; UTF-8 JSON data with exact fields `version` (1), `key`, `length`, and `sha256`. Original metadata is in the stored bytes. |
| Expiry | 15 days from every successful encode/reuse: seven-day maximum execution plus at most seven-day closed-history retention plus one day margin. No deletion on decode or completion. |
| Redis failure | Inline values need no Redis operation. Offload must finish storing/refreshing before returning a reference. Failed writes throw; never return a dangling reference deliberately. |

Store the complete bytes and expiry with one unconditional Redis SET. Identical bytes have the same content hash, so repeated encodes refresh the same key and repair a damaged cached value. Accept the negligible SHA-256 collision risk rather than implementing a separate compare-and-refresh protocol; test concurrent reuse and repair. Equal bytes can share a key across executions. The codec must not change output JSON ordering to create more cache hits; identical serialized bytes are the deduplication unit. A failed activity acknowledgement can leave unused payload keys; TTL handles them.

On decode, pass unrelated encodings through. For this encoding, validate version, namespace/prefix, bounded length and digest syntax before Redis lookup. Check actual length and SHA-256, then reconstruct the original protobuf Payload. Reject nested Redis-reference payloads. Decode must not access arbitrary Redis keys supplied by a forged reference. Missing/corrupt/unsupported references throw named errors with key/hash context but never bytes or secrets. Transient Redis errors remain errors for Temporal to retry where it can; the codec never refetches an upstream response.

Install the same DataConverter/codec in API admission clients, worker/CLI clients, workflow and activity workers, integration tests, and `WorkflowReplayerOptions.DataConverter`. Use at most three typed argument payloads for these contracts. This permits stable selections/catalogs to remain separate from changing request/progress data while bounding aggregate inline argument size. Test the full command/result sizes through the real Temporal service; offloading does not bound total history event count. Verify repeated continuations reuse a retained selection key and refresh its TTL rather than storing another full selection for every counter update.

The codec requires retained bytes; it cannot make Redis durable. The production manifests provide a dedicated PVC, AOF every second, and no eviction. A crash may lose the last second of writes, and permanent storage loss may make history unreadable. Recovery starts fresh imports after old activities have stopped. Local tests continue to use disposable Redis. Before rollout verify namespace retention is at most seven days; if longer retention is needed, increase payload TTL by the same amount before using that policy. Do not reset/retry old executions beyond their inherited seven-day deadline. Saved schedules must not contain references to expiring bytes. Longer-lived replay fixtures need their referenced bytes retained alongside the fixture in isolated test storage or must generate their histories afresh.

The Temporal UI will display the reference for offloaded values unless a codec endpoint is supplied. A web codec endpoint is outside this scope. Small command/result summaries remain directly readable; operator recovery uses workflow identity/status and structured logs. Decode failure may block workflow activation rather than producing an ordinary failed-workflow result. Follow the stop/drain/terminate/fresh-import procedure in the plan when bytes are irrecoverable.

## 7. Responsibility map for code review

| Component | Owns | Does not own |
|---|---|---|
| API/CLI adapters | Existing authentication/route shapes, text-ID resolution, mapping commands and admission errors | Provider orchestration or SQL |
| Temporal client | Start options, conflict classification, chain-bound handles, schedules | Import execution or catalog progress |
| Artist/batch coordinators | Artist exclusivity, provider order, bounded children, final rebuild, summaries | Provider pagination semantics or deletion policy |
| Provider workflow | Readable provider sequence and its typed checkpoint/evidence | I/O, custom storage keys, or generic optional capabilities |
| Activities/importer domain methods | I/O retries, cancellation, parsing/selection, typed item output, shared diagnostics | Temporal progress stored in SQL or exact-once external notifications |
| Catalog data services/transaction runner | Catalog identities, current-state reads, atomic writes, shared-row conflict resolution | HTTP calls or workflow history |
| Catalog rebuild service | Derived show/year/count data and explicit cache invalidation | Item receipt transactions |
| Redis codec | Exact temporary payload bytes and reference validation/expiry | Catalog ownership, discovery policy, or substituting fresh inputs |

At review, trace an Archive full refresh and targeted import, a Phish.in→Phish.net refresh, a Local date import, and a canceled import followed by rebuild. Each trace should identify the admitted operation, typed input, catalog owner, retry boundary, returned identity, and public result without reconstructing a string-key protocol.
