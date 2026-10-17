# Process recording evidence with durable recovery

`NormalizationRecordingProcessor.ProcessAsync(attemptUuid, sourceUuid,
cancellationToken)` refreshes one catalog recording and saves local credit and
title proposals. It works from stored fields for any artist, without requiring
a repertoire dictionary. It does not accept person, song or composition matches,
change playback, call Luna or enable public normalization reads.

This is the catalog-recording producer. Direct processing of richer retained
provider accounts and automatic resolution remain required by
the [main plan](README.md). A stored snapshot cannot recover upstream information
that an older importer discarded.

The [lookup coordinator and dispatcher](lookup-enrollment.md) provide a separate
entry point for durable positive and negative catalog association. A bound legacy
handoff installs its original preparation before calling this processor, so
dispatch does not refresh the recording again. Lookup publication and extraction
publication have independent owners and watermarks.

## Ownership and retained results

Migration 26 adds `normalization.recording_processing_attempts` and the work-owner
domain `extractions`. One source uses owner `(extractions, recording, sourceUuid)`.
That owner is separate from accepted recording credits and song contents. Saving
an extraction must not mark those domain owners successfully normalized.

Each attempt retains its source UUID, processing version, original preparation
and terminal result. Preparation names the exact fetch receipt, original producer
catalog revisions, checked binding and admission revisions. Published batches add
the complete work ticket, evidenced credit mentions, unresolved credit text,
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
The preparation references an immutable fetch receipt. Headers, saved preparations
and terminal outcomes cannot be replaced. The processing version includes the
catalog representation, decoder and extractor versions. Reusing an attempt UUID
for another source raises `NormalizationConflictException`. The proposal store
also rejects a changed processing version; processor compatibility behavior is
specified below.

The processor preparation contract is version 2. It carries source UUID lookup
clocks alongside the original producer and binding read set. The processor
returns a saved same-source terminal result before applying the current version.
An unfinished older-version attempt ends as `Unresolved` with
`processing_version_changed`; use a new attempt UUID to prepare current evidence.
It cannot publish or replace the previous extraction. Drain older workers before
changing binaries so two processing versions cannot compete over one attempt.
Existing pages encode the processor version; start a new page after this change.

## Processing and recovery

1. Create or replay the attempt. A terminal replay returns its original outcome
   without fetching evidence or changing work configuration.
2. Refresh the recording through the [v2 catalog path](catalog-refresh.md), decode
   its exact input and check its binding. Save the preparation before admission.
3. Admit or replay contributed work using that saved request. The stable
   `relisten / recording / sourceUuid` contributor owns its complete input set.
   Supply every catalog dependency and exact input-revision expectation.
4. Recover the saved fetch, check its original producer revisions, and reproduce
   and compare the complete saved binding. Extract source credits and titles of
   positively bound files. Missing show,
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

The current producer consumes one legacy recording input. Contributed admission
preserves other contributors' inputs, but this producer must not publish a subset
of their union. An unexpected union returns `unsupported_owner_inputs`, retains
the full bindings and leaves its work unpublished and pending for reconciliation.
It does not consume those publication watermarks. A later provider coordinator
must process the complete input set and check each producer's original read set;
capturing newer union clocks cannot make older catalog facts current.

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
33. Lookup enrollment and dispatch additionally require migration 34. Build the
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

The current processor and page command require migrations through 33, including
the original processing/page storage from migrations 26–27 and the later lookup
clock contracts. Migration 27 builds a regular `(artist_id, uuid)` index
on `sources`; measure the build on a representative database and allow for blocked
source writes during production migration. Registration in the shared
catalog container starts no work. On rollback, stop processors and retain attempts,
proposals, evidence and work history. Pending-owner reconciliation,
evaluated matching, full-catalog backfill and production rollout remain separate
implementation requirements.
