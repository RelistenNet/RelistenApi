# Intake contracts

These contracts implement the [release plan](README.md). The names below refer to the implemented additions or identify existing contracts. Preserve the behavior and ownership described here; private helper names and class layout can follow the surrounding code.

## State and ownership

Add three relations in a `normalization` schema in the application PostgreSQL database. Include them in the same backup/restore boundary as the catalog. Use typed columns for selection and publication state; use versioned JSON only for retained provider fields and interpretation detail. No general rules engine, decision ledger, feedback service or dependency graph is required.

### `archive_artist_candidates`

One row groups a proposed performer for review and becomes its durable intake membership after materialization.

- `candidate_id uuid` primary key, allocated once.
- `creator_key text` unique grouping key; `proposed_name`, `proposed_slug`; `reviewed_creator_keys text[]` initially containing that key.
- `approved boolean NOT NULL DEFAULT false` is the operator-owned publication permission.
- `identity_status`: `pending`, `existing_match`, `needs_review` or `cohort`; `matched_artist_uuids uuid[]`; representative observation IDs for review.
- `artist_uuid uuid NULL UNIQUE` is the mapped catalog identity. Only the materialization service writes it, once. Retain the creation slug and creation numeric artist ID alongside it for identity checks.
- Creation/update timestamps and a bounded current error/reason. Do not store credentials or exception dumps.

Normalize lookup keys with Unicode normalization, invariant case folding and collapsed whitespace. Do not remove words, split ampersands, translate names or apply fuzzy acceptance. The grouping key groups observations; it does not prove that everyone using the name is the same performer. Conflicting identity evidence makes the candidate unresolved. Reviewed extra labels are a finite intake vocabulary, not a new global alias registry. If a label matches multiple candidates, abstain.

The discovery worker may append representative evidence and refresh match diagnostics. It must not rewrite proposed identity or reviewed labels underneath an existing approval. Operators may edit those fields before approval. After materialization, keep the name, slug and mapped identity fixed; adding a reviewed label is a deliberate database maintenance action performed with the candidate paused and its row locked. Removing a label stops new matching; it does not erase published assignments.

Clearing `approved` pauses subsequent catalog writes and automatic visibility promotion. It neither removes membership nor retracts existing recordings. The publication transaction locks this row before its final check, so a concurrent approval update takes effect at a transaction boundary. It cannot undo a committed import.

### `archive_item_observations`

Retain immutable relevant item metadata before the existing tolerant DTO loses information.

- `observation_id uuid` primary key; `item_identifier text`; `projection_version`; `content_hash`; `observed_at`; provider URL.
- `payload jsonb`: original JSON values for creator, title, date/year, collection, description, notes, venue/coverage, source, lineage, taper, transferer and subject; selected file metadata and original/derivative relationships; dark/restricted/error indicators used by the importer.
- File entries retain name, format, source, original filename, supplied checksums, size, length, track number and title where present. Preserve scalar/array distinctions and absent versus empty fields. Do not fetch audio to construct an observation.
- Unique `(item_identifier, projection_version, content_hash)`. Compute the hash from canonical serialization of this selected projection; exclude fetch time and diagnostic fields. Reobserving identical content reuses the row and advances the item’s last-check time.

Use the existing 64 MiB HTTP response limit. Reject a response that exceeds the limit or cannot be parsed. Do not silently truncate descriptions or file lists and then claim the interpretation covers the item. Escape retained HTML when displaying evidence. Capture the fields needed for the decision; omit unrelated account metadata and reviews. Existing recording review import can continue independently through the shared writer.

These observations survive catalog deletion. Store catalog UUID references as durable values, without cascading foreign keys into imported tables. A provider error response is availability evidence; it does not replace the last valid content observation.

### `archive_item_intake`

One row per exact Archive `item_identifier`, independent of its current collection membership.

- Latest observation ID, last observation time, originating collection and discovery times. An ID-only discovery row can initially have no observation.
- Candidate ID, `rules_version`, and a typed interpretation containing matched creator keys, selected display date, field evidence, item-kind checks and blocking reason codes. Optional `DateDetails` records date precision, selected evidence and nonblocking notes. Legacy interpretations without that field remain readable.
- `status`: `pending_observation`, `awaiting_artist_approval`, `deferred_existing_artist`, `needs_review`, `eligible`, `published`, `paused` or `unavailable`. Keep `last_error`, attempt time and bounded retry information separate; a transport failure is not an identity classification.
- Last published observation ID, rules version, artist UUID, numeric artist ID, display date, source UUID and published time. These remain populated when a later observation is held.
- `catalog_revision bigint` and `rebuilt_revision bigint`, initially zero. Increment `catalog_revision` with a committed source change; mark the corresponding revision rebuilt only after shows, years and cache invalidation succeed. `catalog_revision > rebuilt_revision` means recovery work remains.
- Next observation/retry time. This row supplies pending work; no fourth work queue table is needed.

Foreign keys between these three intake relations may restrict deletion. Catalog deletion must not delete intake records. A missing mapped artist or an unexpected source identity stops publication with `needs_review`; it does not allocate a replacement artist automatically. Retained item identity and published references enforce continued ownership.

## Automatic publication policy

Evaluate each current observation independently. Approval of a candidate enables the following rule; it does not bypass it.

1. There is exactly one nonempty structured creator value after deduplicating equal lookup keys. It matches exactly one approved candidate’s reviewed keys. Missing creators and unreviewed spelling variants remain pending. No automatic artist inference from a free-form description in this release.
2. The candidate either has no catalog match and can be materialized safely, or already belongs to this intake cohort. A match to a pre-existing artist is deferred. Check current names, slugs, Archive collection labels and retained intake labels. A shared name or conflicting candidate is a review case.
3. The item is an audio recording for one performer and a supported full or partial performance date. Hold explicit compilation/remaster-only, tribute/memorial, multi-act billing and ambiguous early/late evidence. Use named, tested parsing rules with evidence spans. Do not equate several named guests with several headline performers without supporting context. Unresolved performer or item-kind uncertainty yields `needs_review`, never a guessed single performer.
4. Accept known-year full or partial performance dates under the [date selection contract](#date-selection-and-nonblocking-notes). Current metadata takes precedence over the immutable identifier. Preserve unknown components as `XX`; do not turn a year-only recording into a precise January 1 performance. An identifier disagreement or supported partial date creates a nonblocking note, not a publication hold. Actual conflicting current metadata, an unusable date, an unknown year and ambiguous early/late attribution still require review.
5. The item passes current Archive metadata and access checks and has playable VBR MP3 files under the existing parser. Keep its existing ordering, format-pairing and duration behavior. FLAC metadata is retained, but this release does not broaden codec eligibility. Lack of normalized songs or a physical venue ID does not block import.
6. If already published, the candidate’s mapped artist equals the stored published artist. A different artist holds the update. A supported current metadata date correction may move the recording to another show or year during rebuild while preserving source, set and track identities. Before writing, validate that the existing catalog source still matches its last committed ownership record; an out-of-band catalog edit remains a repair case. Track identity continues to follow the existing MP3 URL formula; do not transfer annotations or decisions by title or position.

“Clear” means these checks passed. It is a deterministic eligibility label, not a calibrated confidence score. No finite text parser proves that an unknown description contains no contradiction. Test the known attribution failures and review the pilot’s actual output before widening approval. When a later rule finds a blocking conflict, retain the last published assignment and hold the new write.

Use stable reason codes, including `missing_creator`, `ambiguous_creator`, `unapproved_artist`, `existing_artist`, `multiple_performers`, `compilation`, `tribute_context`, `missing_date`, `conflicting_date`, `ambiguous_date`, `no_vbr_mp3`, `upstream_unavailable`, `published_assignment_conflict` and `catalog_identity_missing`. Distinguish external transport failures from these supported outcomes.

## Date selection and nonblocking notes

The existing listener contract remains `display_date` plus the derived show sorting `date`. Full dates use `YYYY-MM-DD`; partial dates use `XX` for unknown components. The sorting surrogate does not assert a precise performance date. No new listener API fields are added.

Use current structured metadata when it supplies a supported date. Compatible current title metadata may add known components to a partial date. A supported performer/date title can supply the date when structured metadata is absent. If a title explicitly supplies a compatible partial date and structured metadata merely fills the unknown components with first-day placeholders, retain the title’s partial precision. For example, title `Pavement: 1992-00-00 ...` with structured `1992-01-01` selects `1992-XX-XX`. A confirmed January 1 date without that uncertainty stays exact.

An identifier is durable provenance, not current date authority. Parse and retain its date evidence, but a different identifier date does not block current metadata or downgrade its precision. For `pavement1994-06-06.mtx.flac16`, current title/date metadata selects March 6. An identifier alone cannot supply a performance date when current metadata has no supported date. Conflicting current mutable metadata remains a review case unless the values are explained by the partial-date placeholder rule. Upload, transfer and remaster dates do not supply performance dates.

`TapersSectionInterpretation.DateDetails` is optional for compatibility with retained JSON. It contains:

- `Precision`: `day`, `month`, `year` or `partial` for a noncontiguous known-component date.
- `SelectedEvidence`: the field locator, original value and named selection rule.
- `Notes`: typed codes and their supporting evidence. Codes include `partial_date`, `identifier_date_disagreement` and `metadata_date_disagreement`.

Notes describe uncertainty or selection without changing eligibility. Only `Reasons` block publication. A published row can therefore have `reason=NULL` and nonempty date notes. Keep original observations immutable; interpretation changes do not fabricate new upstream evidence.

The operator status command includes `selected_display_date` and structured `date_details` beside the existing publication state and published date. Legacy rows return null details until reevaluated. This is operator output, not a new listener response. Rule-version changes cause retained observations to be reevaluated through the existing selection/recovery paths; no schema migration or metadata backfill is needed.

## Transactions and publication

### Materialize an approved candidate

`ArchiveIntakeService.MaterializeArtistAsync(candidateId)` owns this operation. Acquire the existing artist-registry advisory transaction lock, then lock the candidate row. Recheck approval and the current artist registry. Use sequential data-service calls within `CatalogImportTransaction`; perform HTTP before the transaction.

If the row already maps to a live cohort artist with the recorded identity, reuse it. If an unlinked candidate now matches an established cohort member, attach its item observations to that member’s candidate only when the reviewed key is already present; otherwise require reviewed label maintenance. A new non-cohort match becomes `existing_match`, even if the candidate’s approval flag is true. A slug collision or ambiguous match remains unresolved; do not create a suffix to avoid an identity question.

Otherwise create the artist through `ArtistService.Save`, create the normal Archive feature defaults, and store its UUID/numeric ID/creation slug in the candidate in the same transaction. Set `AutoCreated` initially. Do not insert an `artists_upstream_sources` collection mapping. A replay after a lost acknowledgement finds the mapped row.

Expose the existing collection advisory lock as a named artist-registry transaction operation. Collection discovery and authenticated artist create/update must use it around artist/features/mapping writes. Current collection discovery must also consult cohort reviewed keys before creating an apparent duplicate: a recognized cohort match is left for explicit collection attachment, rather than creating a suffixed artist. Keep unrelated collection discovery behavior unchanged. Authenticated creation must recheck cohort names/reviewed keys after taking that lock and reject a collision. An update to another artist must also reject a newly introduced cohort collision. Without that recheck, taking the lock alone would merely serialize duplicate creation. This is a cooperating-writer guarantee; direct administrative identity edits must follow the same transaction contract.

### Apply a recording

`ArchiveIntakeService.ApplyObservationAsync(input, projection, metadataJson)` owns the database commit. The activity fetches current raw metadata and projects its decision fields. The service saves or reuses that observation, then evaluates the current candidate and published assignment under row locks. The input identifies the item and expected candidate. The service takes rules, selected date and current assignment from retained evidence and locked database state, then parses the legacy recording DTO only for an eligible write.

Lock the candidate row, then the item row, in that order. Reread approval, reviewed keys, mapped catalog identity, latest observation and published assignment. If any required basis changed, return a stale/paused outcome and leave the item pending for reevaluation. Do not publish a result computed from a superseded observation. Check the item’s source identity against the current catalog before writing.

Extract a shared Archive recording writer that takes the resolved artist, Archive provider attribution, validated metadata and explicit display date. It must not discover collection membership or decide performer identity. Ordinary Archive imports still use their current collection selection and membership checks. Inside the write transaction, they check whether the item/source belongs to intake. If it does, return the explicit supported skip `owned_by_taperssection`; the dedicated cohort refresh owns its updates. Apply this check to full, filtered and targeted Archive imports before calling the writer. Do not delegate by starting another workflow inside that activity. A cohort import calls it only through the intake transaction. Preserve source, set and track UUID formulas.

Commit source/sets/tracks and the item’s published references plus incremented `catalog_revision` atomically. Refresh the writer even when the decision projection is unchanged so excluded review metadata can still update. Return `unchanged` for reused assignment evidence; advance the revision for every committed writer invocation. Evidence capture is required: if saving the needed observation fails, do not write the recording. A later catalog failure may leave a retained observation; that is safe retry input.

### Rebuild and visibility

The per-artist workflow settles started item activities, captures each dirty item’s catalog revision, and runs existing `RebuildShows → RebuildYears → InvalidateArtistCache`. Record rebuild completion for only those captured revisions. A later revision remains dirty. A fresh cohort refresh or standalone recovery can repeat the rebuild safely.

After a successful rebuild with at least one published playable recording, clear `AutoCreated` for the cohort artist, while retaining other feature bits. Lock the candidate and recheck approval before changing the flag. Invalidate the artist-list and artist-detail caches affected by visibility. Do not claim source insertion alone made the release browseable. A crash between source commit and rebuild leaves dirty rows for repair. A crash during visibility promotion is repaired by the next cohort refresh.

## Discovery and refresh protocol

Add a Taper’s Section search client using `/services/search/v1/scrape` with fixed query `collection:taperssection AND mediatype:audio`. Request `identifier,collection,mediatype`. Reject the whole page if any row lacks the `taperssection` collection or the `audio` media type. Discard those validation fields after checking them; persist identifiers as before. Search hints never substitute for the metadata observation used at publication.

The [live local smoke](live-smoke.md) found that Archive returned an unrelated collection for identifier-only field selection. The membership field selection returned matching recordings on both the first page and a continuation. The internal upstream cause is unknown. Keep membership validation even if identifier-only requests later recover.

The bounded October 16 check found that including `count` with a continuation cursor repeats the first page. Omitting `count` advanced the cursor and returned 5,000 items. The returned total decreased from 30,180 to 30,080 after the first 100 items; it was not a frozen traversal total.

Use `count` only for the first page; omit it on cursor requests. Support continuation pages up to 5,000 items under the existing HTTP byte limit. Persist every received item identifier, in bounded database batches, before advancing the cursor. Reject repeated cursors and nonempty pages with no new identifiers. Deduplicate item IDs. An empty page with a non-null advancing cursor is not completion. Finish when the endpoint supplies no continuation cursor; fail if its terminal response claims unreturned items. Keep totals diagnostic rather than comparing them to cumulative processed counts.

Only one advancing continuation was verified. Terminal response handling and cursor expiry are explicit implementation proof tasks. Do not add an untested alternate pagination algorithm as a fallback. On cursor failure, report discovery failure; retained IDs remain usable, and a later traversal can restart safely from the beginning. Periodic traversal is eventually consistent and cannot prove deletion by absence.

Discovery first persists IDs. Metadata activities then process new or previously failed IDs with the existing Archive concurrency limit. Repeated discovery skips unchanged completed discovery work; approved-item refresh fetches its current metadata separately. A bounded operator discovery may stop after its requested number of IDs, returning `limited` and its count. It must not claim a complete scan or advance past unpersisted page members. Daily discovery traverses all pages with workflow continuations to bound history.

Cohort refresh selects approved candidates for item processing and also selects every artist with dirty item revisions, regardless of approval. For a paused artist, rebuild already committed catalog state without importing items or revealing a new artist. This repairs a commit followed by pause/cancellation. It handles initial artist materialization and starts per-artist work for the current set of items. Reobserve published items regardless of performance date or current collection. If an item leaves Taper’s Section but remains available by identifier, keep its identity and refresh it normally. Artist approval changes are discovered by querying current rows; a failed notification cannot lose work.

Intake refresh does not run artist-wide missing-item deletion. On error 104, dark/restricted content or a parsed item with no usable MP3, retain evidence and references and mark it unavailable for further intake writes. Keep existing catalog rows under the release’s retain-last-state policy; direct upstream access may fail. Do not claim an unavailable upstream URL is playable. HTTP 404, timeout or an empty response is an observation failure, not deletion proof. Recovery rechecks the same item and identities. A later source-removal policy can be designed separately.

Ordinary Archive selection may observe intake items, but its write-time ownership check skips them. Also exclude source UUIDs recorded as intake-owned from ordinary Archive collection deletion candidate selection. This also protects them if a real artist collection is attached later. Keep the existing five-candidate guard and error-104 verification for other sources. The exclusion must survive paused approval and upstream collection moves.

## New interfaces and command behavior

Add typed records in `Services/Ingestion/Providers/TapersSection/`. Use `ArchiveItemId` and `ArtistId` wrappers already defined by ingestion. Public command and result structure (the source uses typed enums for outcome and reason):

```csharp
record DiscoverTapersSection(int? MaxItems = null);
record RefreshTapersSection;
record RefreshTapersSectionArtist(ArtistId Artist);
record TapersSectionItemInput(ArchiveItemId Item, Guid CandidateId);
record TapersSectionItemResult(
    ArchiveItemId Item, TapersSectionItemOutcome Outcome,
    Guid? SourceUuid = null, TapersSectionReason? Reason = null, bool Observed = true);
record TapersSectionSummary(
    int Discovered = 0, int Observed = 0, int Published = 0, int Unchanged = 0,
    int Deferred = 0, int NeedsReview = 0, int Paused = 0, int Unavailable = 0,
    int Failed = 0, int Conflicts = 0, bool Limited = false, ItemFailure[]? Failures = null);
record TapersSectionArtistResult(
    ArtistId Artist, TapersSectionSummary Summary, bool RebuildCompleted);

```

Use closed enums with stable string wire values for outcomes/reasons in the implemented contract; the records above show the response fields. The artist result must also identify the artist and whether rebuild completed. Define item outcomes as `published`, `unchanged`, `deferred`, `needs_review`, `paused`, `unavailable` or `failed`; deferred means an existing non-cohort artist; unavailable has its own summary count. Observed counts successful activity results with retained metadata observations, including reused hashes. Failed activities can also retain observations; those remain visible through database status and are not included in Observed. This outcome counter does not attempt exact per-attempt accounting across overlapping discovery and refresh operations. Conflicts counts rejected artist workflow admissions, not metadata conflicts. Results count processing outcomes, not exact insertion counts after retries. Retain at most 20 failure details, alongside the total failure count, following current ingestion practice. Item failures use Archive item keys; a failed artist operation uses an artist key and keeps its catalog revisions pending for recovery.

Add named methods to `TemporalIngestionClient`: discovery, cohort refresh and single-cohort-artist refresh. Admit the artist operation on existing `ingest/artist/{uuid}` with the current execution-bound handle and `ImportAlreadyRunning` conflict. Validate durable cohort membership instead of an artist collection mapping. Use distinct global IDs `ingest/taperssection/discovery` and `ingest/taperssection/refresh` for the two coordinators. An overlap returns a conflict, not another execution as a successful new start.

Add three worker CLI starts:

- `--taperssection-discover` with optional `--max-items=N` for a bounded pilot;
- `--taperssection-refresh` for all approved candidates and dirty rebuilds;
- `--taperssection-artist=UUID` for a mapped cohort artist.

Add `--taperssection-status` with optional `--item=IDENTIFIER`, returning bounded JSON from PostgreSQL: candidate/approval, input and published versions, blocking reason/error, selected date, structured date details and rebuild state. Validate incompatible options explicitly. Starts return an execution-bound workflow identifier and do not claim completion. Existing cancellation and Temporal result inspection remain available. Add `--register-taperssection-schedules`; create only missing schedules and create them paused. Preserve existing paused/running state on repetition.

No new listener HTTP API or payload field is required. Existing import HTTP routes and collection membership checks retain their semantics. Existing authenticated artist writes gain a single transaction and registry serialization; their successful response shapes stay unchanged. Cohort identity edits that would violate the pinned name/slug/UUID, cohort collisions, and an attempt to install the shared `taperssection` collection mapping return HTTP 409 with `{ "code": "intake_identity_conflict", "artist_uuid": "<affected UUID>", "message": "<specific conflict>" }`. If no catalog identity exists yet, `artist_uuid` is null. A real dedicated collection may be attached explicitly; intake ownership checks still apply to its overlapping items. All other successful response shapes remain unchanged.

## Existing contracts and change locations

Paths here are relative to the repository root.

- `RelistenApi/Services/Ingestion/Providers/TapersSection/ArchiveObservationProjector.cs`, `TapersSectionSearchClient.cs`, `TapersSectionEligibilityEvaluator.cs`: retain and interpret provider evidence and page item identifiers. Keep the existing tolerant converter’s legacy behavior.
- `RelistenApi/Services/Data/ArchiveIntakeService.cs`: owner of the three intake tables, approval/materialization, item state and atomic publication checks. SQL stays in data services.
- `RelistenApi/Services/Data/CatalogImportTransaction.cs`: expose registry locking; retain sequential connection use, deadlines and cancellation checks.
- `RelistenApi/Services/Indexing/ArchiveOrgArtistIndexer.cs`, `Controllers/ArtistsController.cs`: cooperate in registry locking and cohort collision/identity protection. `ArtistService.Save`, `SourceService.Save` and `SourceTrackService.InsertAll` keep their identity formulas.
- `RelistenApi/Services/Importers/ArchiveOrgImporter.cs`, `.Item.cs`, `.Ingestion.cs`, `.Recording.cs`: separate selection/validation from the shared resolved recording write; enforce intake ownership on ordinary writes and deletion accounting.
- `RelistenApi/Services/Ingestion/Providers/TapersSection/`: contracts, activities and workflows. Activity scopes resolve artist and Archive provider explicitly from intake ownership. Reuse existing HTTP handling, heartbeat lifetime and diagnostics without routing through a fabricated mapping.
- `TemporalIngestionClient.cs`, `TemporalIngestionRegistration.cs`, `IngestionServiceRegistration.cs`: admission, workflow/activity registration and dependency injection. The existing `ArtistRefreshWorkflow` remains the ordinary provider coordinator.
- `RelistenIngestionWorker/Program.cs`, `IngestionOperatorCommands.cs`: command parsing and named starts/status. The worker has its own host configuration and telemetry; API registration alone is insufficient.
- `RelistenApi/Migrations/`: next available SimpleMigrations version for the schema and indexes. Update the disposable test schema setup in `RelistenApiTests/LocalTestDatabase.cs` as required.
- `RelistenApiTests/TapersSection/`, `RelistenApiTests/Services/TestTapersSectionArtistRegistry.cs`, `RelistenApiTests/Ingestion/`, `RelistenApiTests/ArchiveOrg/`, `RelistenApiTests/Importers/ArchiveOrg/`: behavior tests using real disposable PostgreSQL, fixture HTTP and SDK-managed Temporal.

Existing contracts required by this plan are described in [ingestion architecture](../../../ingestion/README.md) and [Archive provider behavior](../../../ingestion/providers.md). Preserve five attempts/timeouts and deadlines unless measured workload requires a documented change. Reuse the `ingest-archive` activity queue and its limit of ten, `ingest-db` for catalog work, and `ingest-workflows` for workflows. HTTP and SQL stay in activities. Do not add another queue or independent retry scheduler for the same item.
