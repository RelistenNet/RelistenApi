# Process recording evidence with durable recovery

`NormalizationRecordingProcessor.ProcessAsync(attemptUuid, sourceUuid,
cancellationToken)` prepares one recording's complete retained evidence set and
saves local credit and title proposals. It starts from stored fields for any
artist and includes supported provider inputs already owned by that recording.
It requires no repertoire dictionary. It does not accept person, song or composition matches,
change playback, call Luna or enable public normalization reads.

The shared processor also consumes [retained Archive, Phish and joint Local accounts](provider-processing.md)
through lookup handoffs. A stored legacy snapshot cannot recover upstream
information that an older importer discarded. Automatic resolution remains
required by the [main plan](README.md).

The [lookup coordinator and dispatcher](lookup-enrollment.md) provide a separate
entry point for durable positive and negative catalog association. A bound legacy
handoff freezes its original evidence before this processor prepares the complete
owner set, so dispatch does not refresh that initiating input. Lookup publication and extraction
publication have independent owners and watermarks.

## Ownership and retained results

Migration 26 adds `normalization.recording_processing_attempts` and the work-owner
domain `extractions`. One source uses owner `(extractions, recording, sourceUuid)`.
That owner is separate from accepted recording credits and song contents. Saving
an extraction must not mark those domain owners successfully normalized.

Each attempt retains its source UUID, processing version, optional initiating
handoff, original preparation and terminal result. Preparation names each input's
exact revision, checked binding and admission revisions. Legacy inputs also retain
actual fetch receipts and original producer catalog revisions. Published batches add
the complete work ticket, separate per-input credit mentions, unresolved credit text,
bound-file title interpretations and decoder issues. Unresolved file associations
remain in the binding. Candidate search is explicitly `not_run`.

`NormalizationRecordingProposalService.GetAsync(sourceUuid)` returns the last
successfully published extraction batch. Its owner can later become pending;
this read does not establish current identity matches.
`GetAttemptBatchAsync(attemptUuid)` also retrieves a completed extraction retained
before publication or after stale publication. Proposal UUIDs identify
versioned evidence mentions, rather than canonical people or songs. Repeated
mentions remain distinct. Title-segment offsets refer to the retained title,
without inferred audio times or performance positions.

Attempts retain history after catalog deletion and have no cascading source FK.
Legacy preparation references an immutable fetch receipt; raw provider input needs
no invented legacy receipt. Headers, saved preparations
and terminal outcomes cannot be replaced. The processing version includes the
catalog representation, decoder and extractor versions. Reusing an attempt UUID
for another source raises `NormalizationConflictException`. The proposal store
also rejects a changed processing version; processor compatibility behavior is
specified below.

The processor preparation contract is version 4, including joint Local inputs. It carries complete per-input
preparations and original contributor membership alongside producer, catalog,
identifier, UUID and Local mapping-registry read sets. The processor
returns a saved same-source terminal result before applying the current version.
An unfinished older-version attempt ends as `Unresolved` with
`processing_version_changed`; use a new attempt UUID to prepare current evidence.
It cannot publish or replace the previous extraction. Drain older workers before
changing binaries so two processing versions cannot compete over one attempt.
Existing pages encode the processor version; start a new page after this change.

## Processing and recovery

1. Create or replay the attempt. A terminal replay returns its original outcome
   without fetching evidence or changing work configuration.
2. Read complete contributor ownership. Prepare the initiating legacy snapshot
   through the [v2 catalog path](catalog-refresh.md), or preserve the exact handoff
   evidence. Prepare supported peers, then save the complete immutable preparation.
3. Admit or replay the prepared contributions atomically, with their original
   membership, catalog dependencies and input revisions. The stable
   `relisten / recording / sourceUuid` contributor keeps legacy input ownership;
   provider accounts keep their own resource-based contributor keys.
4. Recover every saved input, check original legacy producer revisions, and
   reproduce each complete binding. Extract recording/file credits and titles of
   positively bound files into separate account groups. Missing show,
   venue or sibling-file context does not withhold usable source credits.
5. Save the completed extraction as an immutable checkpoint. Then mark the batch
   current and save its terminal published result in the work publication
   transaction. Changed inputs or dependencies cannot replace the prior current
   batch; completed stale extraction remains available for audit.

Before preparation commits, a retry can repeat the entire refresh. An orphan
receipt from an interrupted preparation is never paired with fresh clocks.
After preparation commits, recovery must use that saved receipt and read set.
Concurrent callers resume the winning preparation. A losing refresh cannot
terminalize a different preparation saved by another caller.

Admission replay precedes revalidation, so a crash after admission can reuse the
original contributor revision. Stale publication never calls the proposal writer.
The processor records its terminal stale outcome separately and repairs a crash
between those commits on retry. If another caller already published the exact
attempt, its committed result wins even when the catalog changes before the
slower caller revalidates.

Cancellation and unexpected failures leave unfinished attempts resumable.
Transactions release attempt locks before entering the work service. Publication
acquires catalog, resource and work locks first, then writes the attempt through
the supplied transaction. The extraction checkpoint does not claim freshness or
acquire work locks. Neither operation makes catalog writes or external calls.

## Outcomes and limits

- `Published`: the extraction batch became current under a fresh work ticket.
  This does not mean identity resolution succeeded.
- `Missing`: no catalog source existed during preparation.
- `Unresolved`: recording ownership or the complete owner input set could not be
  processed under this version.
- `Stale`: saved facts or admission/publication dependencies changed.

A terminal missing, unresolved or stale attempt needs a new UUID to try changed
facts. None of these outcomes withdraws accepted identities, recording credits,
song contents or playable files. Empty extraction also leaves accepted data alone.

The processor and proposal-saving service enforce the same complete read-set
contract. Extra resources, artist or identifier clocks, source UUIDs, causal
owners, imported venues or candidate facts cannot be acknowledged by a batch
that did not consume them.

The processor consumes legacy, Archive, Phish and joint Local recording inputs. Unsupported
contributors, unmanaged baseline bindings or an unexpected union return an
explicit attention outcome and leave the owner unpublished and pending for
reconciliation. They cannot consume those publication watermarks. See the
[complete-input contract](provider-processing.md) for membership races,
file-credit scope, per-account conflicts and handoff recovery.

## Continue a committed handoff through normalization

`NormalizationRecordingPipeline.ProcessAsync(request, cancellationToken)` delivers
one committed lookup handoff through the existing guarded domain services. The
`NormalizationRecordingPipelineRequest` contains `AttemptUuid` and `HandoffUuid`.
It carries no proposed winners, accepted facts or policy overrides. A retained
Archive or Phish handoff keeps its exact provider revision. A Local handoff keeps
the directory and its actual supporting root together.

The pipeline first enrolls owner `(extractions, recording_pipeline, handoffUuid)`
with work UUID `AttemptUuid`. Enrollment happens before the existing dispatcher
acknowledges extraction delivery. A crash after acknowledgement therefore leaves
the downstream pipeline discoverable. This owner tracks delivery; the recording
extraction owner and accepted people, alias and venue owners retain their own
publication receipts and authority.

After extraction publishes, the pipeline performs these phases:

1. Resolve complete people candidates and apply eligible recording-credit
   decisions through `NormalizationPeopleResolutionProcessor` and
   `NormalizationPeopleResolutionApplier`.
2. Resolve role-free source assertions and apply eligible aliases through
   `SourceActorResolutionService` and `SourceActorAliasApplicationService`.
   An alias assertion does not create a recording role or identify a performer.
3. Resolve every exact recording-account venue claim through
   `NormalizationVenueResolutionProcessor`. Local venue evidence retains its
   root revision and account pointer; the authenticated directory binding supplies
   the catalog association. Adjacent root accounts cannot supply that evidence.
4. Publish an empty pipeline completion only when the domain stages report
   applied or complete results. Domain writers perform the accepted writes;
   pipeline completion changes no identity or playback data.

`NormalizationRecordingPipelineResult` includes the request, source UUID,
extraction result, typed people and alias resolution/application receipts, venue
receipts and optional completion. Its disposition distinguishes `Completed`,
`AwaitingPolicy`, `Review`, `Stale` and `Unresolved`. The last four leave the
pipeline owner pending. Inactive policies still permit proposal resolution and
durable attention receipts; they do not authorize accepted facts. Existing
accepted credits, aliases and venue assignments remain governed by the domain
writers' preservation and replacement rules.

Reuse both request UUIDs after cancellation or a transient exception. Child IDs
derive from that immutable request and each phase's original source coordinate,
so replay resumes the existing child checkpoints. Admission uses the same request
shape on every replay; it does not substitute a later owner generation. A parked
policy, review or unresolved result needs the stated external change before a
new attempt can help. Do not busy-loop it. A terminal stale attempt needs a new
`AttemptUuid`; changed provider evidence first needs a new lookup and handoff.

`NormalizationWorkService.GetPendingRunsAsync("extractions", "recording_pipeline",
afterUuid, limit, cancellationToken)` returns the pending owner, latest work ticket
and run state. Recover the request from `Ticket.WorkUuid` and
`Subject.Subject.Uuid`. `Running` includes both interrupted work and parked domain
outcomes; it is discovery state, not retry advice. Replay the original request once
to recover its typed outcome from the durable child checkpoints, then park it if
the outcome requires an external change. A published or stale run needs a new
attempt after inspecting the changed facts. Pagination
follows the shared pending-owner UUID scan, so an empty filtered page with a
non-null `Next` must continue. Ticket pages exceeding 32 MiB are refused; request
a smaller page. Discovery does not itself establish freshness or authorize writes.

Migration 59 adds `recording_pipeline` to the allowed extraction-owner kinds. It
adds no pipeline table and does not convert extraction proposals into accepted
facts. Apply it after the existing normalization migrations before using this
service. Rollback stops pipeline delivery and retains the schema, original
evidence, domain attempts and unfinished owners. Its down migration refuses to
delete recovery state or restore a constraint that excludes those owners.

Automatic importer enrollment, pending-work draining and Temporal registration
are **not wired yet**. The existing `normalize-recordings` page command still
publishes extraction proposals only. No schedule or production acceptance policy
is activated by this service. An automatic drain must retain or derive parked
outcomes and react to policy, review and evidence changes; repeatedly selecting
every `Running` owner would not provide that behavior. Listener outcomes use the
existing additive v3 people, credit, venue and show routes after domain acceptance and explicit read
enablement; they do not read the shadow JSON output.

## Run a bounded recording page

`NormalizationRecordingPageService.ProcessPageAsync(pageUuid, artistUuid?,
afterSourceUuid?, limit, cancellationToken)` saves its selection before processing
recordings sequentially. Migration 27 retains the page, ordered source UUIDs,
stable per-item attempt UUIDs and separate failure receipts. Selection defaults
to 25 recordings and permits 1–100. A new request rejects an unknown artist;
a known artist with no recordings produces a valid empty page.

Reuse the page UUID and all selectors to resume after cancellation or a retryable
failure. Exact replay processes the saved items; it never selects replacement
recordings. A changed selector or processor version conflicts. Terminal attempts
return their saved outcome without fetching evidence again. Completed items
survive a failure in a later item, and processing holds no page lock while it
refreshes or extracts recording evidence.

After correcting a terminal `Missing`, `Unresolved` or `Stale` outcome, create a
new page UUID covering that recording's range, or invoke the single-recording
processor with a new attempt UUID. Replaying the old page does not refresh it.

The worker command requires the catalog's `DATABASE_URL` and migrations through
35. Lookup registration requires migrations through 34. Build the
worker first; keep build output separate from command receipts:

```sh
dotnet build RelistenIngestionWorker/RelistenIngestionWorker.csproj
```

Set `normalization_artist_uuid` to the desired catalog artist UUID, then run the
built executable:

```sh
normalization_page_uuid=$(uuidgen)
dotnet RelistenIngestionWorker/bin/Debug/net10.0/RelistenIngestionWorker.dll \
  --normalize-recordings "$normalization_page_uuid" \
  --artist "$normalization_artist_uuid" --limit 25
```

Omit `--artist` for a global selection. Save the page UUID and request arguments
before starting. After a settled receipt supplies `continuation_cursor`, use
that UUID as `--after` on a **new** page UUID with the same artist and limit.
A null continuation means this selection saw no further recordings, or the
current page is unfinished; inspect `settled` to distinguish them.

On exits 0 and 2, the command writes one JSON receipt to stdout and diagnostics
to stderr. Exits 1 and 130 can have only stderr output. Its
receipt contains selection/attempt UUIDs, terminal outcomes and safe failure
codes; it excludes provider prose and exception details. Exit codes are:

- `0`: every selected attempt settled. Inspect `published`, `missing`,
  `unresolved` and `stale`; settled does not mean every recording was normalized.
- `2`: retryable unfinished items remain. Retry the same request. No continuation
  is supplied, even when other items published.
- `1`: invalid or conflicting request, or an unexpected failure. Inspect retained
  page, attempt and failure records before retrying.
- `130`: operator cancellation. Retry the same request to resume.

`NormalizationRecordingPageStore.GetAsync` reads the immutable selection;
`GetFailuresAsync(pageUuid, afterSequence?, limit)` reads append-only classified
failures. Codes are `preparation_conflict`, `database_failed`,
`processing_timeout` and `processing_failed` in command JSON. Transient failures
continue with later items. An unexpected item error records a nonretryable
failure and stops the invocation. If page creation or receipt persistence itself
fails, no failure receipt is guaranteed. Previously committed progress remains
resumable. Failure receipts do not terminalize attempts;
after correcting the cause, an operator can explicitly retry the page.

Artist filtering means membership when the page was selected. A later source
move is processed from current checked facts. Global UUID paging uses an existing
index; migration 27 adds `(artist_id, uuid)` for artist filtering. A keyset page
is not a catalog snapshot: late arrivals and moves can land behind a cursor.
Full backfill therefore also needs enrollment or reconciliation.

The dedicated operator adapter runs before resolving Temporal-dependent
`IngestionOperatorCommands`. It uses the worker's catalog services without starting
hosted workers or schedules. It needs no Redis or Temporal connection and makes
no provider or Luna calls. Normalized reads and automatic acceptance remain
disabled. The separate evaluation tool keeps its own database restrictions.

## Verification and rollout

Run `dotnet build RelistenApi.sln`, then:

```sh
./test-local.sh --no-build --filter 'FullyQualifiedName~TestNormalizationRecording'
```

Verify exact replay, preparation and admission recovery, concurrent publication,
changed inputs, partial playback context, preserved contributor unions, callback
rollback and unchanged accepted data. Page tests also exercise selection replay,
partial failure, cancellation, moved/deleted catalog rows and the actual worker
executable under Test and Development environments. Pure extraction cases include retained
wording from several artists; they verify reproducibility and evidence handling,
not identity-matching accuracy. The [main plan](README.md#progress) records results.

The current processor and page command require migrations through 35, including
the original processing/page storage from migrations 26–27 and the later lookup
clock and complete-input contracts. Migration 27 builds a regular `(artist_id, uuid)` index
on `sources`; measure the build on a representative database and allow for blocked
source writes during production migration. Registration in the shared
catalog container starts no work. On rollback, stop processors and retain attempts,
proposals, evidence and work history. Pending-owner reconciliation,
evaluated matching, full-catalog backfill and production rollout remain separate
implementation requirements.
