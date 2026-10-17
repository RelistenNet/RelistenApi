# Process recording evidence with durable recovery

`NormalizationRecordingProcessor.ProcessAsync(attemptUuid, sourceUuid,
cancellationToken)` refreshes one catalog recording and saves local credit and
title proposals. It works from stored fields for any artist, without requiring
a repertoire dictionary. It does not accept person, song or composition matches,
change playback, call Luna or enable public normalization reads.

This is the catalog-recording producer. Direct processing of richer retained
provider accounts, bounded page jobs and automatic resolution remain required by
the [main plan](README.md). A stored snapshot cannot recover upstream information
that an older importer discarded.

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
for another source or version raises `NormalizationConflictException`.

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

The current producer consumes one legacy recording input. Contributed admission
preserves other contributors' inputs, but this producer must not publish a subset
of their union. An unexpected union returns `unsupported_owner_inputs`, retains
the full bindings and leaves its work unpublished and pending for reconciliation.
It does not consume those publication watermarks. A later provider coordinator
must process the complete input set and check each producer's original read set;
capturing newer union clocks cannot make older catalog facts current.

## Next: bounded operator jobs

Add a page runner that saves its selected source UUIDs before processing starts.
Its request must fix a page UUID, optional artist UUID and source cursor, item
limit and processor version. Use a default of 25 items and allow 1–100. Reject
an unknown artist explicitly. Replay uses that selection and the same per-item
attempt UUIDs. Keep item outcomes in the attempt store; retain retryable failure
receipts separately. Cancellation must preserve the selection and completed items.
Return a continuation cursor only after every selected attempt has settled, while
reporting missing, unresolved and stale items as attention outcomes.

Artist filtering means membership when the page was selected. A later source
move is processed from current checked facts. Global UUID paging has an existing
index; artist-filtered paging needs an `(artist_id, uuid)` index. A keyset page
is not a catalog snapshot: late arrivals and moves can land behind a cursor.
Full backfill therefore also needs enrollment or reconciliation.

Expose the runner through a dedicated operator adapter in
`RelistenIngestionWorker`, before resolving the existing Temporal-dependent
`IngestionOperatorCommands`. Reuse the worker's database configuration and catalog
services. Do not start Temporal workers, schedules or a matching policy for a
local page command. Keep the separate evaluation tool's database restrictions.

## Verification and rollout

Run `dotnet build RelistenApi.sln`, then:

```sh
./test-local.sh --no-build --filter 'FullyQualifiedName~TestNormalizationRecording'
```

Verify exact replay, preparation and admission recovery, concurrent publication,
changed inputs, partial playback context, preserved contributor unions, callback
rollback and unchanged accepted data. Pure extraction cases include retained
wording from several artists; they verify reproducibility and evidence handling,
not identity-matching accuracy. The [main plan](README.md#progress) records results.

Install migration 26 before invoking the processor. Registration in the shared
catalog container starts no work. On rollback, stop processors and retain attempts,
proposals, evidence and work history. Bounded jobs, pending-owner reconciliation,
evaluated matching, full-catalog backfill and production rollout remain separate
implementation requirements.
